using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Enterprise;

public enum CloudProvider : byte { Aws, Gcp, Azure, Custom }

public enum RenderNodeState : byte { Unknown, Online, Busy, Degraded, Offline }

/// <summary>v16.27.0 렌더 노드 — 원격 GPU 워커(HTTP 렌더 에이전트) 1대.</summary>
public sealed class RenderNode
{
    public string Id { get; init; } = "node_" + Guid.NewGuid().ToString("N")[..6];
    public CloudProvider Provider { get; init; }
    public string Endpoint { get; init; } = "";
    public string Region { get; init; } = "";
    public string Gpu { get; set; } = "?";
    public int Slots { get; set; } = 1;
    public RenderNodeState State { get; set; }
    public double LatencyMs { get; set; }
    public double Utilization { get; set; }
    public int Inflight;
    public int Failures;
    public long Completed;
    public long OfflineUntil;
    public string? LastError { get; set; }
}

/// <summary>v16.27.0 분할 단위 — 씬(컷) 1개 또는 고부하 씬의 시간 구간.</summary>
public sealed record RenderChunk(int Index, string SceneId, string Source, double Start, double Duration, bool Heavy);

/// <summary>v16.27.0 씬 메타(프로브 결과).</summary>
public sealed record FarmSceneInput(string SceneId, string Path, double Duration, int Width, int Height, double Fps);

/// <summary>v16.27.0 통일 인코딩 프리셋 — 모든 청크를 동일 파라미터로 재인코딩해야 concat 무재인코딩 결합 가능.</summary>
public sealed record FarmPreset(int Width = 1920, int Height = 1080, int Fps = 30, int Crf = 20)
{
    public static FarmPreset Uhd60 => new(3840, 2160, 60, 20);
}

/// <summary>노드 선택 입력(무할당 값형).</summary>
public readonly record struct NodeLoad(bool Available, int Inflight, int Slots, double LatencyMs);

public sealed record FarmJobResult(bool Ok, string? OutputPath, int Chunks, int Remote, int Local, int Failovers, string Message);

/// <summary>
/// [PromptFlowStudio] v16.27.0 ☁️ 클라우드 분산 렌더 팜 오케스트레이터 — 컷 단위 Split-Merge 청킹 · 원격 GPU 노드 병렬 송출 · 장애 시 로컬 GPU/CPU 폴백.
///   ▸ 노드: 환경변수 <c>PFS_RENDER_FARM_NODES</c>(<c>aws|https://host|region;gcp|...</c>) 또는 UI 등록 · 토큰 <c>PFS_RENDER_FARM_TOKEN</c>(하드코딩 0)
///     · URL 은 https(또는 loopback http)만. 헬스 프로브 <c>GET /v1/health</c>(3초 · 스키마 즉시 검증) · 연속 3회 실패 시 60초 서킷 오픈.
///   ▸ 청킹: 4K/고프레임(≥50fps) 씬은 <see cref="MaxChunkSeconds"/> 구간 분할, 일반 씬은 컷 1청크(<see cref="PlanChunks"/>).
///   ▸ 스케줄: 가용 노드 중 (in-flight+1)/슬롯 × 지연 가중 최소(<see cref="PickNode"/> · 무할당) · 원격 실패는 다른 노드로 지수 백오프 재시도(최대 2회) → 로컬 폴백(NVENC→x264).
///   ▸ 전송: <c>POST /v1/chunks</c>(Bearer · 구간/프리셋 헤더 · 원본 스트림) → 응답 mp4 를 스트리밍 저장하며 SHA-256 증분 해시 → <c>X-Pfs-Sha256</c> 와 FixedTimeEquals 검증.
///   ▸ 오토스케일: 대기 청크 기반 희망 노드 수(<see cref="DesiredNodes"/>)를 <c>PFS_RENDER_FARM_SCALE_URL</c> 웹훅(AWS/GCP/Azure 오토스케일러 측)에 통지(미설정 시 권고값만 표시).
///   ▸ 결합: ffconcat + <c>-c copy</c>. 백그라운드 잡 1개(<see cref="StartJob"/>) · CTS 취소 · 진행 카운터 Interlocked.
/// </summary>
public sealed class CloudRenderFarmDispatcherService : IDisposable
{
    public const int MaxNodes = 32;
    public const int MaxChunks = 2000;
    public const double MaxChunkSeconds = 10;
    public const int CircuitFailures = 3;
    public const int MaxRemoteAttempts = 2;
    public static readonly TimeSpan CircuitCooldown = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(3);

    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private readonly Func<string?> _ffmpeg;
    private readonly Func<string?> _token;
    private readonly Func<string?> _scaleUrl;
    private readonly object _gate = new();
    private readonly List<RenderNode> _nodes = new();
    private CancellationTokenSource? _cts;
    private int _total, _done, _remote, _local, _failovers, _active, _nvencBroken;
    private bool _disposed;

    public CloudRenderFarmDispatcherService(IVideoMergeService merge, SecureSecretVault vault)
        : this(() => merge.ResolvedFfmpegPath, () => vault.TryGetExternalCredential("RENDER_FARM_TOKEN") ?? Environment.GetEnvironmentVariable("PFS_RENDER_FARM_TOKEN"),
               () => Environment.GetEnvironmentVariable("PFS_RENDER_FARM_SCALE_URL"))
    {
        foreach (var n in ParseNodeList(Environment.GetEnvironmentVariable("PFS_RENDER_FARM_NODES"))) AddNode(n);
    }

    public CloudRenderFarmDispatcherService(Func<string?> ffmpeg, Func<string?> token, Func<string?> scaleUrl)
    {
        _ffmpeg = ffmpeg;
        _token = token;
        _scaleUrl = scaleUrl;
    }

    public event Action? OnChange;

    public bool IsRunning => Volatile.Read(ref _cts) is not null;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public bool TokenConfigured => !string.IsNullOrWhiteSpace(_token());
    public bool ScalerConfigured => !string.IsNullOrWhiteSpace(_scaleUrl());
    public int TotalChunks => Volatile.Read(ref _total);
    public int DoneChunks => Volatile.Read(ref _done);
    public int RemoteChunks => Volatile.Read(ref _remote);
    public int LocalChunks => Volatile.Read(ref _local);
    public int Failovers => Volatile.Read(ref _failovers);
    public int ActiveChunks => Volatile.Read(ref _active);
    public int LastDesiredNodes { get; private set; }
    public double Progress => TotalChunks == 0 ? 0 : (double)DoneChunks / TotalChunks;
    public string? LastAction { get; private set; }
    public FarmJobResult? LastResult { get; private set; }
    public IReadOnlyList<RenderNode> Nodes { get { lock (_gate) return _nodes.ToArray(); } }

    public int OnlineNodes
    {
        get
        {
            var n = 0;
            lock (_gate) foreach (var x in _nodes) if (x.State is RenderNodeState.Online or RenderNodeState.Busy) n++;
            return n;
        }
    }

    // ══════════════════════ 노드 관리 ══════════════════════

    public bool AddNode(RenderNode node)
    {
        if (node is null || !IsValidNodeUrl(node.Endpoint)) return false;
        lock (_gate)
        {
            if (_nodes.Count >= MaxNodes) return false;
            foreach (var n in _nodes) if (string.Equals(n.Endpoint, node.Endpoint, StringComparison.OrdinalIgnoreCase)) return false;
            _nodes.Add(node);
        }
        LastAction = "➕ 노드 등록 · " + ProviderLabel(node.Provider) + " " + node.Region;
        Raise();
        return true;
    }

    public void RemoveNode(string id)
    {
        lock (_gate) _nodes.RemoveAll(n => n.Id == id);
        Raise();
    }

    // ══════════════════════ 순수 헬퍼(테스트 대상) ══════════════════════

    public static string ProviderLabel(CloudProvider p) => p switch { CloudProvider.Aws => "AWS", CloudProvider.Gcp => "GCP", CloudProvider.Azure => "Azure", _ => "Custom" };

    /// <summary>노드 URL — https 절대 URL(또는 loopback http) · 자격증명/쿼리/프래그먼트 금지.</summary>
    public static bool IsValidNodeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 512) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (!string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)) return false;
        return u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback);
    }

    /// <summary><c>provider|url|region;...</c> 파싱(불량 항목 건너뜀).</summary>
    public static List<RenderNode> ParseNodeList(string? spec)
    {
        var list = new List<RenderNode>();
        if (string.IsNullOrWhiteSpace(spec)) return list;
        foreach (var item in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = item.Split('|', StringSplitOptions.TrimEntries);
            if (p.Length < 2 || !IsValidNodeUrl(p[1])) continue;
            var prov = p[0].ToLowerInvariant() switch { "aws" => CloudProvider.Aws, "gcp" => CloudProvider.Gcp, "azure" => CloudProvider.Azure, _ => CloudProvider.Custom };
            list.Add(new RenderNode { Provider = prov, Endpoint = p[1].TrimEnd('/'), Region = p.Length > 2 ? p[2] : "" });
            if (list.Count >= MaxNodes) break;
        }
        return list;
    }

    /// <summary>
    /// Split-Merge 청킹 — 4K(픽셀 ≥ 3840×2160×0.9) 또는 ≥50fps 씬은 <paramref name="maxChunkSeconds"/> 구간으로 균등 분할, 나머지는 씬 1청크.
    /// </summary>
    public static List<RenderChunk> PlanChunks(IReadOnlyList<FarmSceneInput> scenes, double maxChunkSeconds = MaxChunkSeconds)
    {
        var res = new List<RenderChunk>(scenes.Count);
        maxChunkSeconds = Math.Max(1, maxChunkSeconds);
        foreach (var s in scenes)
        {
            if (s.Duration <= 0) continue;
            var heavy = (long)s.Width * s.Height >= (long)(3840 * 2160 * 0.9) || s.Fps >= 50;
            var parts = heavy ? Math.Max(1, (int)Math.Ceiling(s.Duration / maxChunkSeconds)) : 1;
            var len = s.Duration / parts;
            for (var i = 0; i < parts && res.Count < MaxChunks; i++)
                res.Add(new RenderChunk(res.Count, s.SceneId, s.Path, i * len, i == parts - 1 ? s.Duration - i * len : len, heavy));
        }
        return res;
    }

    /// <summary>노드 선택(무할당) — 가용 + 슬롯 여유 중 (in-flight+1)/slots × (1+지연/1초) 최소. 없으면 -1.</summary>
    public static int PickNode(ReadOnlySpan<NodeLoad> loads)
    {
        var best = -1; var bestScore = double.MaxValue;
        for (var i = 0; i < loads.Length; i++)
        {
            var l = loads[i];
            if (!l.Available || l.Slots <= 0 || l.Inflight >= l.Slots) continue;
            var score = (l.Inflight + 1.0) / l.Slots * (1 + Math.Max(0, l.LatencyMs) / 1000.0);
            if (score < bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    /// <summary>오토스케일 희망 노드 수 = ceil(대기/노드당 슬롯) · [min,max] 클램프.</summary>
    public static int DesiredNodes(int pendingChunks, int slotsPerNode, int min, int max)
    {
        if (max < min) max = min;
        if (pendingChunks <= 0) return min;
        var need = (pendingChunks + Math.Max(1, slotsPerNode) - 1) / Math.Max(1, slotsPerNode);
        return Math.Clamp(need, min, max);
    }

    /// <summary>재시도 백오프(1→2→4…최대 8초).</summary>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(8, 1 << Math.Clamp(attempt, 0, 3)));

    /// <summary>헬스 응답 스키마 검증 — <c>{"gpu":"A10G","slots":4,"busy":1,"util":0.4}</c> (slots 1~64 · util 0~1).</summary>
    public static bool TryParseHealth(string? json, out string gpu, out int slots, out double util)
    {
        gpu = ""; slots = 0; util = 0;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 8192) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object
                || !r.TryGetProperty("gpu", out var g) || g.ValueKind != JsonValueKind.String
                || !r.TryGetProperty("slots", out var s) || !s.TryGetInt32(out slots) || slots is < 1 or > 64) return false;
            gpu = (g.GetString() ?? "?").Trim();
            if (gpu.Length > 48) gpu = gpu[..48];
            if (r.TryGetProperty("util", out var u) && u.ValueKind == JsonValueKind.Number && u.TryGetDouble(out var ud) && double.IsFinite(ud)) util = Math.Clamp(ud, 0, 1);
            return true;
        }
        catch (JsonException) { slots = 0; return false; }
    }

    /// <summary>청크 인코딩 인자(로컬/원격 에이전트 공통 규약) — 정확 구간 · 통일 해상도/fps/GOP · AAC 48k 스테레오(무음 보강).</summary>
    public static List<string> BuildChunkArgs(RenderChunk c, FarmPreset p, string output, bool nvenc)
    {
        var inv = CultureInfo.InvariantCulture;
        var w = p.Width.ToString(inv); var h = p.Height.ToString(inv); var fps = p.Fps.ToString(inv);
        var a = new List<string>
        {
            "-hide_banner", "-v", "error", "-y", "-ss", c.Start.ToString("0.###", inv), "-t", c.Duration.ToString("0.###", inv), "-i", c.Source,
            "-f", "lavfi", "-t", c.Duration.ToString("0.###", inv), "-i", "anullsrc=r=48000:cl=stereo",
            "-filter_complex", "[0:v]scale=" + w + ":" + h + ":force_original_aspect_ratio=decrease,pad=" + w + ":" + h + ":(ow-iw)/2:(oh-ih)/2,setsar=1,fps=" + fps + ",format=yuv420p[v];"
                + "[0:a][1:a]amix=inputs=2:duration=longest:dropout_transition=0[a]",
            "-map", "[v]", "-map", "[a]",
        };
        if (nvenc) a.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", p.Crf.ToString(inv), "-b:v", "0" });
        else a.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-crf", p.Crf.ToString(inv) });
        a.AddRange(new[] { "-g", (p.Fps * 2).ToString(inv), "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-ar", "48000", "-ac", "2", "-movflags", "+faststart", output });
        return a;
    }

    /// <summary>원본 오디오가 없을 때 — 무음 트랙만 매핑.</summary>
    public static List<string> BuildSilentChunkArgs(RenderChunk c, FarmPreset p, string output, bool nvenc)
    {
        var a = BuildChunkArgs(c, p, output, nvenc);
        var i = a.IndexOf("-filter_complex");
        a[i + 1] = a[i + 1][..(a[i + 1].IndexOf(';') + 1)] + "[1:a]anull[a]";
        return a;
    }

    /// <summary>ffmpeg stderr 의 "xx fps" → fps(실패 0).</summary>
    public static double ParseFps(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return 0;
        var i = stderr.IndexOf(" fps", StringComparison.Ordinal);
        if (i <= 0) return 0;
        var s = i - 1;
        while (s >= 0 && (char.IsDigit(stderr[s]) || stderr[s] == '.')) s--;
        return double.TryParse(stderr.AsSpan(s + 1, i - s - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
    }

    // ══════════════════════ 헬스 프로브 ══════════════════════

    /// <summary>전체 노드 헬스 프로브(병렬 · 3초 데드라인) — 서킷 오픈 노드는 쿨다운 경과 후에만 재시도.</summary>
    public async Task ProbeAllAsync(CancellationToken ct = default)
    {
        RenderNode[] nodes;
        lock (_gate) nodes = _nodes.ToArray();
        var tasks = new Task[nodes.Length];
        for (var i = 0; i < nodes.Length; i++) tasks[i] = ProbeAsync(nodes[i], ct);
        await Task.WhenAll(tasks).ConfigureAwait(false);
        LastAction = "🩺 헬스 체크 · 온라인 " + OnlineNodes.ToString(CultureInfo.InvariantCulture) + "/" + nodes.Length.ToString(CultureInfo.InvariantCulture);
        Raise();
    }

    private async Task ProbeAsync(RenderNode n, CancellationToken ct)
    {
        if (n.State == RenderNodeState.Offline && Environment.TickCount64 < Interlocked.Read(ref n.OfflineUntil)) return;
        var sw = Stopwatch.StartNew();
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(HealthTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, n.Endpoint + "/v1/health");
            AddAuth(req);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, d.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(d.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode || !TryParseHealth(body, out var gpu, out var slots, out var util))
            {
                MarkFailure(n, resp.IsSuccessStatusCode ? "헬스 스키마 불일치" : "HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture));
                return;
            }
            lock (_gate)
            {
                n.Gpu = gpu; n.Slots = slots; n.Utilization = util;
                n.LatencyMs = n.LatencyMs <= 0 ? sw.Elapsed.TotalMilliseconds : n.LatencyMs * 0.7 + sw.Elapsed.TotalMilliseconds * 0.3;
                n.State = util >= 0.95 ? RenderNodeState.Busy : RenderNodeState.Online;
                n.Failures = 0; n.LastError = null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { MarkFailure(n, ex is OperationCanceledException ? "헬스 타임아웃" : ex.Message); }
    }

    private void MarkFailure(RenderNode n, string why)
    {
        lock (_gate)
        {
            n.Failures++;
            n.LastError = why;
            if (n.Failures >= CircuitFailures)
            {
                n.State = RenderNodeState.Offline;
                Interlocked.Exchange(ref n.OfflineUntil, Environment.TickCount64 + (long)CircuitCooldown.TotalMilliseconds);
            }
            else n.State = RenderNodeState.Degraded;
        }
    }

    private void AddAuth(HttpRequestMessage req)
    {
        var t = _token();
        if (!string.IsNullOrWhiteSpace(t)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + t.Trim());
    }

    // ══════════════════════ 잡 실행 ══════════════════════

    /// <summary>스토리보드 완성 컷 → 렌더 팜 백그라운드 잡 시작(실행 중이면 false). 결과는 <see cref="LastResult"/>/<see cref="OnChange"/>.</summary>
    public bool StartJob(StoryboardProject project, string outputPath, FarmPreset preset)
    {
        var paths = new List<(string, string)>();
        foreach (var s in project.Scenes)
            if (!string.IsNullOrWhiteSpace(s.LocalPath) && File.Exists(s.LocalPath)) paths.Add((s.Id, s.LocalPath));
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed || _cts is not null) { cts.Dispose(); return false; }
            _cts = cts;
        }
        _ = RunJobGuardedAsync(paths, outputPath, preset, cts);
        return true;
    }

    public void Cancel()
    {
        try { Volatile.Read(ref _cts)?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task RunJobGuardedAsync(List<(string Id, string Path)> paths, string output, FarmPreset preset, CancellationTokenSource cts)
    {
        try { LastResult = await RunAsync(paths, output, preset, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { LastResult = new(false, null, TotalChunks, RemoteChunks, LocalChunks, Failovers, "취소됨"); }
        catch (Exception ex) { Program.Log("RenderFarm.Job", ex); LastResult = new(false, null, TotalChunks, RemoteChunks, LocalChunks, Failovers, "잡 실패: " + ex.Message); }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_cts, cts)) _cts = null; }
            cts.Dispose();
            LastAction = (LastResult?.Ok == true ? "✅ " : "⚠ ") + LastResult?.Message;
            Raise();
        }
    }

    /// <summary>
    /// 분산 렌더 본체 — 프로브 → 청킹 → 오토스케일 통지 → 워커(가용 슬롯 합 + 로컬 1) 병렬 처리 → concat 결합.
    /// 원격 노드가 없거나 전부 실패해도 로컬 폴백으로 완주(ffmpeg 부재 시 실패 반환 · 예외 0).
    /// </summary>
    public async Task<FarmJobResult> RunAsync(IReadOnlyList<(string Id, string Path)> scenes, string outputPath, FarmPreset preset, CancellationToken ct)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) return new(false, null, 0, 0, 0, 0, "ffmpeg 실행 파일을 찾을 수 없습니다.");
        if (scenes.Count == 0) return new(false, null, 0, 0, 0, 0, "렌더할 완성 컷이 없습니다.");
        Interlocked.Exchange(ref _total, 0); Interlocked.Exchange(ref _done, 0); Interlocked.Exchange(ref _remote, 0);
        Interlocked.Exchange(ref _local, 0); Interlocked.Exchange(ref _failovers, 0); Interlocked.Exchange(ref _active, 0);
        LastAction = "🔎 씬 분석 · 노드 프로브";
        Raise();

        var inputs = new List<FarmSceneInput>(scenes.Count);
        foreach (var (id, path) in scenes)
        {
            var pr = await ProbeVideoAsync(ffmpeg, path, ct).ConfigureAwait(false);
            if (pr is { } p && p.Duration > 0) inputs.Add(new FarmSceneInput(id, path, p.Duration, p.W, p.H, p.Fps));
        }
        await ProbeAllAsync(ct).ConfigureAwait(false);
        var chunks = PlanChunks(inputs);
        if (chunks.Count == 0) return new(false, null, 0, 0, 0, 0, "길이를 읽을 수 있는 컷이 없습니다.");
        Interlocked.Exchange(ref _total, chunks.Count);

        var slotSum = 0; var maxSlots = 1;
        lock (_gate) foreach (var n in _nodes) if (n.State is RenderNodeState.Online or RenderNodeState.Busy) { slotSum += n.Slots; maxSlots = Math.Max(maxSlots, n.Slots); }
        LastDesiredNodes = DesiredNodes(chunks.Count, maxSlots, 0, MaxNodes);
        await NotifyScalerAsync(LastDesiredNodes, chunks.Count, ct).ConfigureAwait(false);

        var tmp = Path.Combine(Path.GetTempPath(), "pfs_farm_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            var outs = new string[chunks.Count];
            var ch = Channel.CreateBounded<RenderChunk>(new BoundedChannelOptions(chunks.Count) { SingleWriter = true });
            foreach (var c in chunks) ch.Writer.TryWrite(c);
            ch.Writer.Complete();
            var workers = Math.Clamp(slotSum + 1, 1, 64);
            var failed = 0;
            var tasks = new Task[workers];
            for (var w = 0; w < workers; w++)
            {
                tasks[w] = Task.Run(async () =>
                {
                    await foreach (var c in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref _active);
                        try
                        {
                            var dst = Path.Combine(tmp, "c" + c.Index.ToString("0000", CultureInfo.InvariantCulture) + ".mp4");
                            if (await ProcessChunkAsync(ffmpeg, c, preset, dst, ct).ConfigureAwait(false)) outs[c.Index] = dst;
                            else Interlocked.Increment(ref failed);
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _active);
                            Interlocked.Increment(ref _done);
                            Raise();
                        }
                    }
                }, ct);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            if (failed > 0) return new(false, null, chunks.Count, RemoteChunks, LocalChunks, Failovers, "청크 " + failed.ToString(CultureInfo.InvariantCulture) + "개 렌더 실패 — 결합 중단");

            LastAction = "🧩 청크 결합";
            Raise();
            var list = new StringBuilder("ffconcat version 1.0\n", 64 + chunks.Count * 24);
            foreach (var o in outs) list.Append("file '").Append(Path.GetFileName(o)).Append("'\n");
            var listPath = Path.Combine(tmp, "list.ffconcat");
            await File.WriteAllTextAsync(listPath, list.ToString(), ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            var ok = await RunFfmpegAsync(ffmpeg, new List<string> { "-hide_banner", "-v", "error", "-y", "-f", "concat", "-safe", "0", "-i", listPath, "-c", "copy", "-movflags", "+faststart", outputPath },
                TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);
            await NotifyScalerAsync(0, 0, ct).ConfigureAwait(false);
            return ok
                ? new(true, outputPath, chunks.Count, RemoteChunks, LocalChunks, Failovers, "렌더 완료 · 청크 " + chunks.Count.ToString(CultureInfo.InvariantCulture) + " (원격 " + RemoteChunks.ToString(CultureInfo.InvariantCulture) + " · 로컬 " + LocalChunks.ToString(CultureInfo.InvariantCulture) + ")")
                : new(false, null, chunks.Count, RemoteChunks, LocalChunks, Failovers, "청크 결합(concat) 실패");
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch (Exception ex) { Program.Log("RenderFarm.Cleanup", ex); }
        }
    }

    /// <summary>청크 1개 — 원격(최대 2회 · 노드 교체 · 백오프) → 로컬 폴백.</summary>
    private async Task<bool> ProcessChunkAsync(string ffmpeg, RenderChunk c, FarmPreset preset, string dst, CancellationToken ct)
    {
        for (var attempt = 0; attempt < MaxRemoteAttempts; attempt++)
        {
            var node = Acquire();
            if (node is null) break;
            try
            {
                if (await SendRemoteAsync(node, c, preset, dst, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _remote);
                    Interlocked.Increment(ref node.Completed);
                    return true;
                }
            }
            finally { Interlocked.Decrement(ref node.Inflight); }
            Interlocked.Increment(ref _failovers);
            await Task.Delay(Backoff(attempt), ct).ConfigureAwait(false);
        }
        if (await RenderLocalAsync(ffmpeg, c, preset, dst, ct).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _local);
            return true;
        }
        return false;
    }

    private RenderNode? Acquire()
    {
        lock (_gate)
        {
            var n = _nodes.Count;
            if (n == 0) return null;
            Span<NodeLoad> loads = n <= 32 ? stackalloc NodeLoad[n] : new NodeLoad[n];
            for (var i = 0; i < n; i++)
            {
                var x = _nodes[i];
                loads[i] = new NodeLoad(x.State is RenderNodeState.Online or RenderNodeState.Busy, x.Inflight, x.Slots, x.LatencyMs);
            }
            var k = PickNode(loads);
            if (k < 0) return null;
            Interlocked.Increment(ref _nodes[k].Inflight);
            return _nodes[k];
        }
    }

    internal async Task<bool> SendRemoteAsync(RenderNode node, RenderChunk c, FarmPreset p, string dst, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, c.Duration * 20)));
            await using var src = new FileStream(c.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var req = new HttpRequestMessage(HttpMethod.Post, node.Endpoint + "/v1/chunks") { Content = new StreamContent(src, 81920) };
            AddAuth(req);
            req.Headers.TryAddWithoutValidation("X-Pfs-Chunk", c.Index.ToString(inv));
            req.Headers.TryAddWithoutValidation("X-Pfs-Start", c.Start.ToString("0.###", inv));
            req.Headers.TryAddWithoutValidation("X-Pfs-Duration", c.Duration.ToString("0.###", inv));
            req.Headers.TryAddWithoutValidation("X-Pfs-Preset", p.Width.ToString(inv) + "x" + p.Height.ToString(inv) + "@" + p.Fps.ToString(inv) + ":crf" + p.Crf.ToString(inv));
            req.Content.Headers.TryAddWithoutValidation("Content-Type", "application/octet-stream");
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, d.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) { MarkFailure(node, "HTTP " + ((int)resp.StatusCode).ToString(inv)); return false; }
            if (!resp.Headers.TryGetValues("X-Pfs-Sha256", out var hv)) { MarkFailure(node, "무결성 헤더 누락"); return false; }
            string? expected = null;
            foreach (var v in hv) { expected = v; break; }
            if (expected is null || expected.Length != 64) { MarkFailure(node, "무결성 헤더 형식 오류"); return false; }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var body = await resp.Content.ReadAsStreamAsync(d.Token).ConfigureAwait(false))
            await using (var fs = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
                try
                {
                    int r;
                    while ((r = await body.ReadAsync(buf.AsMemory(0, 81920), d.Token).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buf, 0, r);
                        await fs.WriteAsync(buf.AsMemory(0, r), d.Token).ConfigureAwait(false);
                    }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buf); }
            }
            if (!HashMatches(hash.GetHashAndReset(), expected))
            {
                MarkFailure(node, "SHA-256 불일치");
                TryDelete(dst);
                return false;
            }
            lock (_gate) { node.Failures = 0; if (node.State == RenderNodeState.Degraded) node.State = RenderNodeState.Online; }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            MarkFailure(node, ex is OperationCanceledException ? "청크 타임아웃" : ex.Message);
            TryDelete(dst);
            return false;
        }
    }

    /// <summary>16진 해시 문자열 상수 시간 비교.</summary>
    public static bool HashMatches(ReadOnlySpan<byte> actual, string? expectedHex)
    {
        if (expectedHex is null || expectedHex.Length != actual.Length * 2) return false;
        if (actual.Length > 64) return false;
        Span<byte> exp = stackalloc byte[64];
        for (var i = 0; i < actual.Length; i++)
        {
            int hi = Nib(expectedHex[i * 2]), lo = Nib(expectedHex[i * 2 + 1]);
            if ((hi | lo) < 0) return false;
            exp[i] = (byte)((hi << 4) | lo);
        }
        return CryptographicOperations.FixedTimeEquals(actual, exp[..actual.Length]);
    }

    private static int Nib(char c) => c is >= '0' and <= '9' ? c - '0' : c is >= 'a' and <= 'f' ? c - 'a' + 10 : c is >= 'A' and <= 'F' ? c - 'A' + 10 : -1;

    private async Task<bool> RenderLocalAsync(string ffmpeg, RenderChunk c, FarmPreset p, string dst, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(120, c.Duration * 30));
        if (Volatile.Read(ref _nvencBroken) == 0)
        {
            if (await RunFfmpegAsync(ffmpeg, BuildChunkArgs(c, p, dst, true), timeout, ct).ConfigureAwait(false)
                || await RunFfmpegAsync(ffmpeg, BuildSilentChunkArgs(c, p, dst, true), timeout, ct).ConfigureAwait(false)) return true;
            Volatile.Write(ref _nvencBroken, 1);   // NVENC 미지원/실패 → 이후 x264 고정
        }
        return await RunFfmpegAsync(ffmpeg, BuildChunkArgs(c, p, dst, false), timeout, ct).ConfigureAwait(false)
            || await RunFfmpegAsync(ffmpeg, BuildSilentChunkArgs(c, p, dst, false), timeout, ct).ConfigureAwait(false);
    }

    private async Task NotifyScalerAsync(int desired, int pending, CancellationToken ct)
    {
        var url = _scaleUrl();
        if (string.IsNullOrWhiteSpace(url) || !IsValidNodeUrl(url)) return;
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(5));
            var body = "{\"desired\":" + desired.ToString(CultureInfo.InvariantCulture) + ",\"pending\":" + pending.ToString(CultureInfo.InvariantCulture) + "}";
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            AddAuth(req);
            using var resp = await Http.SendAsync(req, d.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) Program.Log("RenderFarm.Scale", new HttpRequestException("HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture)));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("RenderFarm.Scale", ex); }
    }

    private static async Task<(int W, int H, double Duration, double Fps)?> ProbeVideoAsync(string ffmpeg, string path, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-hide_banner"); psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
            using var p = new Process { StartInfo = psi };
            if (!p.Start()) return null;
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(TimeSpan.FromSeconds(15));
            var err = await p.StandardError.ReadToEndAsync(d.Token).ConfigureAwait(false);
            try { await p.WaitForExitAsync(d.Token).ConfigureAwait(false); } catch (OperationCanceledException) { try { p.Kill(true); } catch { } }
            if (!CommercialProductSegmenterService.TryParseVideoSize(err, out var w, out var h)) return null;
            return (w, h, InteractiveChoiceBranchingService.ParseDuration(err), ParseFps(err));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Program.Log("RenderFarm.Probe", ex); return null; }
    }

    internal static async Task<bool> RunFfmpegAsync(string ffmpeg, List<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        try { if (!p.Start()) return false; }
        catch (Exception ex) { Program.Log("RenderFarm.Ffmpeg", ex); return false; }
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(timeout);
        try
        {
            var err = p.StandardError.ReadToEndAsync(d.Token);
            _ = p.StandardOutput.ReadToEndAsync(d.Token);
            await p.WaitForExitAsync(d.Token).ConfigureAwait(false);
            if (p.ExitCode != 0) Program.Log("RenderFarm.Ffmpeg", new InvalidOperationException((await err.ConfigureAwait(false)).Trim()));
            return p.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Program.Log("RenderFarm.Delete", ex); }
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("RenderFarm.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Cancel();
        OnChange = null;
    }
}
