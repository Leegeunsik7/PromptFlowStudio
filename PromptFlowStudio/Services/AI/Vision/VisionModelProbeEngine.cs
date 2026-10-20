using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Vision;

public enum VisionProbeStatus : byte { NotProbed = 0, ServerOffline = 1, InvalidResponse = 2, NoVisionModel = 3, Ready = 4 }

/// <summary>설치 모델 1개의 판정 결과.</summary>
public sealed record OllamaModelInfo(string Name, bool IsVision, string Evidence);

/// <summary>프로빙 결과(스냅샷).</summary>
public sealed record VisionProbeResult(VisionProbeStatus Status, string? SelectedModel, IReadOnlyList<OllamaModelInfo> Models, string Message, DateTimeOffset At);

/// <summary>합성 프레임 벤치마크 결과. Fps = 성공 응답 수 / 전체 경과(순차 1건씩 - 실효 처리율).</summary>
public sealed record VisionBenchmarkResult(string Model, int Frames, int Succeeded, int Failed, double P50Ms, double P95Ms, double AvgMs, double Fps, string Message);

/// <summary>
/// [PromptFlow Studio v13.2.0] Ollama 비전 모델 자율 프로빙 + 합성 프레임 벤치마크(순수 BCL).
///   ▸ 탐지: <c>GET /api/tags</c> 로 설치 모델 목록 → 모델마다 <c>POST /api/show</c> 의 <c>capabilities</c> 에 "vision" 이 있으면 확정(가장 정확),
///     capabilities 를 주지 않는 구버전 Ollama 면 계열 이름 휴리스틱(qwen2.5vl·llava·bakllava·moondream·minicpm-v·llama3.2-vision·granite-vision…)으로 폴백.
///   ▸ 선택: 선호 순위(<see cref="PreferredFamilies"/>)로 1개 선택 → 연결된 <see cref="LocalVlmHttpClient"/> 가 설정 기본값(llava)을
///     쓰는데 실제로는 설치돼 있지 않은 흔한 상황을 자동 교정(단, PFS_VLM_MODEL 로 명시했으면 존중).
///   ▸ 가이드: 비전 모델이 없으면 <see cref="NoVisionModelGuide"/>, 서버가 없으면 <see cref="ServerOfflineGuide"/> 를 HUD 에 노출.
///   ▸ 헌장 7: 요청별 타임아웃(CTS) · 응답 스키마 즉시 검증(불일치=InvalidResponse) · 비루프백 엔드포인트는 PFS_VLM_ALLOW_REMOTE 없이는 호출 안 함.
/// </summary>
public sealed class VisionModelProbeEngine : IDisposable
{
    public const string NoVisionModelGuide = "⚠️ No Vision Model Pulled (ollama pull qwen2.5vl:3b)";
    public const string ServerOfflineGuide = "🔴 Ollama 서버 미가동 (ollama serve)";
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>선호 순위(앞일수록 우선) - 경량·성능 균형 순.</summary>
    internal static readonly string[] PreferredFamilies =
        { "qwen2.5vl", "qwen2.5-vl", "qwen2-vl", "llama3.2-vision", "minicpm-v", "gemma3", "llava-phi3", "llava-llama3", "llava", "bakllava", "moondream", "granite3.2-vision", "llama4", "mistral-small3.1" };

    /// <summary>capabilities 가 없을 때의 이름 휴리스틱(텍스트 전용 계열은 절대 포함하지 않음 - 예: qwen2.5, gemma2).</summary>
    internal static bool LooksLikeVisionName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("vl") && (n.Contains("qwen") || n.Contains("internvl") || n.Contains("minicpm"))
            || n.Contains("llava") || n.Contains("moondream") || n.Contains("minicpm-v") || n.Contains("vision")
            || n.StartsWith("gemma3", StringComparison.Ordinal) && !n.StartsWith("gemma3:1b", StringComparison.Ordinal)   // gemma3 1b 는 텍스트 전용
            || n.StartsWith("llama4", StringComparison.Ordinal) || n.StartsWith("mistral-small3.1", StringComparison.Ordinal);
    }

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _endpoint;
    private readonly bool _allowed;
    private VisionProbeResult _last = new(VisionProbeStatus.NotProbed, null, Array.Empty<OllamaModelInfo>(), "아직 프로빙하지 않음", DateTimeOffset.MinValue);
    private VisionBenchmarkResult? _lastBenchmark;
    private int _busy;

    public event Action? OnChange;

    public VisionModelProbeEngine() : this(VlmClientOptions.FromEnvironment(), null) { }

    internal VisionModelProbeEngine(VlmClientOptions options, HttpClient? http)
    {
        _endpoint = options.Endpoint;
        _allowed = options.Endpoint.IsLoopback || options.AllowRemote;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Uri Endpoint => _endpoint;
    public VisionProbeResult Last => Volatile.Read(ref _last);
    public VisionBenchmarkResult? LastBenchmark => Volatile.Read(ref _lastBenchmark);
    public bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>HUD 배지 문구.</summary>
    public string Badge => Last.Status switch
    {
        VisionProbeStatus.Ready => "🟢 비전 모델: " + Last.SelectedModel,
        VisionProbeStatus.NoVisionModel => NoVisionModelGuide,
        VisionProbeStatus.ServerOffline => ServerOfflineGuide,
        VisionProbeStatus.InvalidResponse => "🟠 Ollama 응답 형식 불일치",
        _ => "⚪ 비전 모델 미확인",
    };

    /// <summary>설치 모델을 조회해 비전 모델을 판정·선택. 실패는 예외 없이 상태로 반환.</summary>
    public async Task<VisionProbeResult> ProbeAsync(CancellationToken ct = default)
    {
        VisionProbeResult result;
        if (!_allowed)
            result = new(VisionProbeStatus.ServerOffline, null, Array.Empty<OllamaModelInfo>(), "비루프백 엔드포인트 - PFS_VLM_ALLOW_REMOTE=1 없이는 프로빙하지 않음", DateTimeOffset.UtcNow);
        else
        {
            try { result = await ProbeCoreAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.IO.IOException)
            {
                result = new(VisionProbeStatus.ServerOffline, null, Array.Empty<OllamaModelInfo>(), ServerOfflineGuide + " - " + ex.GetType().Name, DateTimeOffset.UtcNow);
            }
        }
        Volatile.Write(ref _last, result);
        OnChange?.Invoke();
        return result;
    }

    private async Task<VisionProbeResult> ProbeCoreAsync(CancellationToken ct)
    {
        string tagsJson;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(ProbeTimeout);
            using var resp = await _http.GetAsync(new Uri(_endpoint, "/api/tags"), cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new(VisionProbeStatus.InvalidResponse, null, Array.Empty<OllamaModelInfo>(), $"/api/tags HTTP {(int)resp.StatusCode}", DateTimeOffset.UtcNow);
            tagsJson = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }

        var names = ParseTagNames(tagsJson);
        if (names is null) return new(VisionProbeStatus.InvalidResponse, null, Array.Empty<OllamaModelInfo>(), "/api/tags 스키마 불일치(models[].name 없음)", DateTimeOffset.UtcNow);

        var models = new List<OllamaModelInfo>(names.Count);
        foreach (var name in names)
        {
            var caps = await TryGetCapabilitiesAsync(name, ct).ConfigureAwait(false);
            models.Add(caps is not null
                ? new OllamaModelInfo(name, caps.Contains("vision"), "capabilities=[" + string.Join(",", caps) + "]")
                : new OllamaModelInfo(name, LooksLikeVisionName(name), "이름 휴리스틱(capabilities 미제공)"));
        }

        var selected = SelectPreferred(models);
        return selected is null
            ? new(VisionProbeStatus.NoVisionModel, null, models, NoVisionModelGuide + $" - 설치 {models.Count}개 전부 텍스트 전용", DateTimeOffset.UtcNow)
            : new(VisionProbeStatus.Ready, selected, models, "비전 모델 탐지: " + selected, DateTimeOffset.UtcNow);
    }

    /// <summary>/api/tags 응답에서 모델명 목록. 스키마 불일치면 null.</summary>
    internal static List<string>? ParseTagNames(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var m in arr.EnumerateArray())
            {
                var n = m.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString()
                      : m.TryGetProperty("model", out var md) && md.ValueKind == JsonValueKind.String ? md.GetString() : null;
                if (!string.IsNullOrWhiteSpace(n)) list.Add(n!);
            }
            return list;
        }
        catch (JsonException) { return null; }
    }

    // /api/show 의 capabilities(없거나 실패면 null → 이름 휴리스틱으로 폴백).
    private async Task<HashSet<string>?> TryGetCapabilitiesAsync(string model, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            var body = "{\"model\":\"" + JsonEncodedText.Encode(model).ToString() + "\"}";
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "/api/show")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Array) return null;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in caps.EnumerateArray()) if (c.ValueKind == JsonValueKind.String) set.Add(c.GetString()!);
            return set;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    internal static string? SelectPreferred(IReadOnlyList<OllamaModelInfo> models)
    {
        string? best = null; var bestRank = int.MaxValue;
        foreach (var m in models)
        {
            if (!m.IsVision) continue;
            var lower = m.Name.ToLowerInvariant();
            var rank = PreferredFamilies.Length;
            for (var i = 0; i < PreferredFamilies.Length; i++) if (lower.StartsWith(PreferredFamilies[i], StringComparison.Ordinal)) { rank = i; break; }
            if (rank < bestRank) { best = m.Name; bestRank = rank; }
        }
        return best;
    }

    /// <summary>프로빙 결과가 Ready 이고 클라이언트 모델이 명시 설정이 아니면 탐지 모델로 교체. 교체했으면 true.</summary>
    public bool ApplyTo(LocalVlmHttpClient client)
    {
        var last = Last;
        if (last.Status != VisionProbeStatus.Ready || last.SelectedModel is null || client.ModelExplicitlyConfigured) return false;
        if (string.Equals(client.Model, last.SelectedModel, StringComparison.Ordinal)) return false;
        client.UseModel(last.SelectedModel);
        OnChange?.Invoke();
        return true;
    }

    /// <summary>합성 RGB 프레임(그라디언트+주황 섬광 패턴) N장을 순차 추론해 지연 분포·실효 FPS 측정. 동시 실행 1건(중복 호출은 즉시 null).</summary>
    public async Task<VisionBenchmarkResult?> RunSyntheticBenchmarkAsync(IVisionLanguageClient client, int frames = 5, int width = 224, int height = 126, CancellationToken ct = default)
    {
        if (frames < 1) throw new ArgumentOutOfRangeException(nameof(frames));
        if (Interlocked.Exchange(ref _busy, 1) != 0) return null;
        OnChange?.Invoke();
        try
        {
            var rgb = new byte[width * height * 3];
            var lat = new List<double>(frames);
            var ok = 0; var fail = 0;
            var total = Stopwatch.StartNew();
            for (var f = 0; f < frames; f++)
            {
                FillSynthetic(rgb, width, height, f);
                var t0 = Stopwatch.GetTimestamp();
                VisionVlmReply? reply = null;
                try { reply = await client.DescribeAsync(rgb, width, height, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { /* 실패로 집계 */ }
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                if (reply is not null) { ok++; lat.Add(ms); } else fail++;
            }
            total.Stop();
            lat.Sort();
            double Pct(double q) => lat.Count == 0 ? 0 : lat[Math.Min(lat.Count - 1, (int)Math.Ceiling(q * lat.Count) - 1)];
            var avg = 0.0; foreach (var v in lat) avg += v; avg = lat.Count == 0 ? 0 : avg / lat.Count;
            var fps = total.Elapsed.TotalSeconds > 0 ? ok / total.Elapsed.TotalSeconds : 0;
            var model = client is LocalVlmHttpClient h ? h.Model : client.GetType().Name;
            var result = new VisionBenchmarkResult(model, frames, ok, fail, Pct(0.50), Pct(0.95), avg, fps,
                ok == 0 ? "성공 응답 0건 - 모델/서버 확인 필요" : $"{ok}/{frames} 성공");
            Volatile.Write(ref _lastBenchmark, result);
            return result;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            OnChange?.Invoke();
        }
    }

    // 프레임마다 달라지는 결정적 패턴(캐시 적중 방지): 대각 그라디언트 + 중앙 주황 섬광 원.
    internal static void FillSynthetic(Span<byte> rgb, int w, int h, int frame)
    {
        int cx = w / 2, cy = h / 2, r2 = (Math.Min(w, h) / 4) * (Math.Min(w, h) / 4);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var o = (y * w + x) * 3;
                var dx = x - cx; var dy = y - cy;
                if (dx * dx + dy * dy < r2 && (frame & 1) == 0) { rgb[o] = 255; rgb[o + 1] = 150; rgb[o + 2] = 30; }
                else { rgb[o] = (byte)((x * 255 / Math.Max(1, w - 1) + frame * 17) & 0xFF); rgb[o + 1] = (byte)(y * 255 / Math.Max(1, h - 1)); rgb[o + 2] = (byte)(96 + frame * 8); }
            }
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
