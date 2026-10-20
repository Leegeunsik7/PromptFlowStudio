using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Vision;

/// <summary>방송 화면 상황 분류(리액션 매핑 단위).</summary>
public enum VisionScene : byte { Unknown = 0, Calm = 1, DarkScene = 2, ExplosionFlash = 3, HighAction = 4 }

/// <summary>VLM 응답 1건(스키마 검증 통과분만 생성).</summary>
public readonly record struct VisionVlmReply(VisionScene Scene, double Confidence, string? Summary);

/// <summary>로컬 비전 모델 결선점 - HTTP(Ollama/LM Studio) 구현이 기본이며, 향후 ONNX 런타임 백엔드는 이 계약만 구현하면 상위 무변경.</summary>
public interface IVisionLanguageClient
{
    bool Enabled { get; }
    /// <summary>RGB24(행 우선, 패딩 없음) 축소 프레임 분석. 실패·스키마 불일치·비활성은 null(호출측이 BCL 휴리스틱으로 폴백).</summary>
    Task<VisionVlmReply?> DescribeAsync(ReadOnlyMemory<byte> rgb, int width, int height, CancellationToken ct);
}

/// <summary>v13.1.0: 선택적 VLM 건강 프로브 - 구현체만 제공(기존 <see cref="IVisionLanguageClient"/> 구현은 무변경 호환).
///   분석기가 서킷 개방 중 호출을 생략하고 HUD 가 "VLM Offline" 을 표시하는 근거.</summary>
public interface IVlmHealthProbe
{
    /// <summary>연속 실패로 서킷 개방 중(호출해도 네트워크를 타지 않음) - 분석기는 이 동안 요청을 생략한다.</summary>
    bool CircuitOpen { get; }
    /// <summary>마지막 호출이 연결 실패·5xx·타임아웃으로 끝났는지(다음 성공 시 해제).</summary>
    bool LastCallFailed { get; }
}

public enum VlmApiFlavor : byte { Ollama = 0, OpenAiCompatible = 1 }

/// <summary>VLM 접속 설정 - 시크릿/엔드포인트는 전부 환경변수(하드코딩 금지). 기본은 루프백 Ollama.</summary>
public sealed record VlmClientOptions(
    Uri Endpoint,
    string Model,
    VlmApiFlavor Flavor,
    string? ApiKey,
    TimeSpan RequestTimeout,
    int MaxAttempts,
    bool AllowRemote)
{
    public const string DefaultEndpoint = "http://127.0.0.1:11434";
    public const string DefaultModel = "llava";

    /// <summary>PFS_VLM_ENDPOINT / PFS_VLM_MODEL / PFS_VLM_API(ollama|openai) / PFS_VLM_API_KEY / PFS_VLM_TIMEOUT_MS / PFS_VLM_ALLOW_REMOTE(=1).</summary>
    public static VlmClientOptions FromEnvironment()
    {
        var ep = Environment.GetEnvironmentVariable("PFS_VLM_ENDPOINT");
        if (!Uri.TryCreate(string.IsNullOrWhiteSpace(ep) ? DefaultEndpoint : ep, UriKind.Absolute, out var uri)) uri = new Uri(DefaultEndpoint);
        var model = Environment.GetEnvironmentVariable("PFS_VLM_MODEL");
        var flavor = string.Equals(Environment.GetEnvironmentVariable("PFS_VLM_API"), "openai", StringComparison.OrdinalIgnoreCase)
            ? VlmApiFlavor.OpenAiCompatible : VlmApiFlavor.Ollama;
        var key = Environment.GetEnvironmentVariable("PFS_VLM_API_KEY");
        var timeoutMs = int.TryParse(Environment.GetEnvironmentVariable("PFS_VLM_TIMEOUT_MS"), out var t) && t > 0 ? t : 8000;
        var allowRemote = Environment.GetEnvironmentVariable("PFS_VLM_ALLOW_REMOTE") == "1";
        return new VlmClientOptions(uri, string.IsNullOrWhiteSpace(model) ? DefaultModel : model!, flavor,
            string.IsNullOrEmpty(key) ? null : key, TimeSpan.FromMilliseconds(timeoutMs), 2, allowRemote);
    }
}

/// <summary>
/// [PromptFlow Studio v13.0.0] 로컬 VLM HTTP 클라이언트(순수 BCL) — Ollama <c>/api/chat</c>(images=base64) 또는 OpenAI 호환
///   <c>/v1/chat/completions</c>(LM Studio 등, image_url=data URL)로 축소 프레임을 PNG 로 보내 JSON 분류를 받는다.
///   ▸ 헌장 7: 요청별 타임아웃(CTS) · 일시 오류(연결 실패/5xx/타임아웃) 지수 백오프 재시도 · 응답 스키마 도착 즉시 검증(불일치=null)
///     · 시크릿은 환경변수에서만.
///   ▸ 프라이버시 게이트: 엔드포인트가 루프백이 아니면 <see cref="VlmClientOptions.AllowRemote"/>(PFS_VLM_ALLOW_REMOTE=1) 없이는
///     비활성 - 방송 화면이 명시적 동의 없이 외부로 나가지 않게.
///   ▸ 연속 실패 시 서킷 개방(<see cref="CircuitOpenFor"/>) - 모델 미기동 상태에서 1초마다 연결 시도하며 로그를 채우지 않게.
///   ⚠ 이 경로(HTTP·JSON·base64)는 1FPS 이하 저빈도라 문자열/버퍼 할당이 발생한다 - Zero-Alloc 은 프레임 샘플·축소·휴리스틱 핫패스 범위.
/// </summary>
public sealed class LocalVlmHttpClient : IVisionLanguageClient, IVlmHealthProbe, IDisposable
{
    internal const string Instruction =
        "You are a live-stream scene classifier. Look at the frame and answer ONLY with compact JSON: " +
        "{\"scene\":\"explosion|dark|action|calm|other\",\"confidence\":0.0-1.0,\"summary\":\"<=12 words\"}. " +
        "explosion = explosion, fire, bright flash. dark = dark room or night. action = fast motion or fighting. calm = static or talking.";

    public static readonly TimeSpan CircuitOpenFor = TimeSpan.FromSeconds(30);
    private const int FailuresToOpen = 3;

    private readonly VlmClientOptions _opt;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Func<long> _clock;
    private int _consecutiveFailures;
    private long _circuitUntilTicks;
    private long _requests, _failures, _schemaRejects;
    private int _lastCallFailed;

    public LocalVlmHttpClient() : this(VlmClientOptions.FromEnvironment())
    {
        ModelExplicitlyConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PFS_VLM_MODEL"));
    }

    public LocalVlmHttpClient(VlmClientOptions options, HttpClient? http = null, Func<long>? clock = null)
    {
        _opt = options;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // 요청별 CTS 로 타임아웃 통제
        _clock = clock ?? System.Diagnostics.Stopwatch.GetTimestamp;
        IsLoopback = options.Endpoint.IsLoopback;
        Enabled = IsLoopback || options.AllowRemote;
    }

    public bool Enabled { get; }
    public bool IsLoopback { get; }
    public Uri Endpoint => _opt.Endpoint;
    private string? _modelOverride;
    /// <summary>실제 요청에 쓰는 모델명(<see cref="UseModel"/> 로 교체 가능 - 없으면 설정값).</summary>
    public string Model => Volatile.Read(ref _modelOverride) ?? _opt.Model;
    /// <summary>설정 모델이 환경변수(PFS_VLM_MODEL)로 명시된 경우 true - 자동 탐지가 덮어쓰지 않는다.</summary>
    public bool ModelExplicitlyConfigured { get; init; }

    /// <summary>v13.2.0: 프로빙으로 찾은 설치 모델로 교체(스레드 안전 · 다음 요청부터 적용).</summary>
    public void UseModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("모델명 필요", nameof(model));
        Volatile.Write(ref _modelOverride, model.Trim());
    }
    public long Requests => Interlocked.Read(ref _requests);
    public long Failures => Interlocked.Read(ref _failures);
    public long SchemaRejects => Interlocked.Read(ref _schemaRejects);
    public bool CircuitOpen => _clock() < Interlocked.Read(ref _circuitUntilTicks);
    public bool LastCallFailed => Volatile.Read(ref _lastCallFailed) != 0;
    public string? LastError { get; private set; }

    public async Task<VisionVlmReply?> DescribeAsync(ReadOnlyMemory<byte> rgb, int width, int height, CancellationToken ct)
    {
        if (!Enabled || CircuitOpen || width <= 0 || height <= 0 || rgb.Length < width * height * 3) return null;
        var png = PngRgbEncoder.Encode(rgb.Span, width, height);
        var body = BuildRequestJson(png);

        for (var attempt = 1; attempt <= Math.Max(1, _opt.MaxAttempts); attempt++)
        {
            Interlocked.Increment(ref _requests);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_opt.RequestTimeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, RequestUri()) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                if (_opt.ApiKey is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opt.ApiKey);
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode >= 500) throw new HttpRequestException($"VLM {(int)resp.StatusCode}", null, resp.StatusCode);
                if (!resp.IsSuccessStatusCode) { Fail($"VLM {(int)resp.StatusCode}"); return null;   /* 4xx = 설정 오류, 재시도 무의미 */ }
                var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var reply = ParseEnvelope(text, _opt.Flavor);
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                Volatile.Write(ref _lastCallFailed, 0);   // 서버 응답 도착 = 온라인(스키마 불일치여도 연결은 정상)
                if (reply is null) { Interlocked.Increment(ref _schemaRejects); LastError = "응답 스키마 불일치"; }
                return reply;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
            {
                if (attempt >= _opt.MaxAttempts) { Fail(ex is OperationCanceledException ? "VLM 타임아웃" : ex.Message); return null; }
                await Task.Delay(200 * (1 << (attempt - 1)), ct).ConfigureAwait(false);   // 지수 백오프 200→400ms…
            }
        }
        return null;
    }

    private void Fail(string reason)
    {
        Interlocked.Increment(ref _failures);
        Volatile.Write(ref _lastCallFailed, 1);
        LastError = reason;
        if (Interlocked.Increment(ref _consecutiveFailures) >= FailuresToOpen)
        {
            Interlocked.Exchange(ref _circuitUntilTicks, _clock() + (long)(CircuitOpenFor.TotalSeconds * System.Diagnostics.Stopwatch.Frequency));
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
    }

    private Uri RequestUri() => new(_opt.Endpoint, _opt.Flavor == VlmApiFlavor.Ollama ? "/api/chat" : "/v1/chat/completions");

    private string BuildRequestJson(byte[] png)
    {
        var b64 = Convert.ToBase64String(png);
        using var ms = new MemoryStream(b64.Length + 1024);
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", Model);
            w.WriteBoolean("stream", false);
            if (_opt.Flavor == VlmApiFlavor.Ollama) w.WriteString("format", "json");
            else w.WriteNumber("temperature", 0);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "user");
            if (_opt.Flavor == VlmApiFlavor.Ollama)
            {
                w.WriteString("content", Instruction);
                w.WriteStartArray("images"); w.WriteStringValue(b64); w.WriteEndArray();
            }
            else
            {
                w.WriteStartArray("content");
                w.WriteStartObject(); w.WriteString("type", "text"); w.WriteString("text", Instruction); w.WriteEndObject();
                w.WriteStartObject(); w.WriteString("type", "image_url");
                w.WriteStartObject("image_url"); w.WriteString("url", "data:image/png;base64," + b64); w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>API 봉투(Ollama message.content / OpenAI choices[0].message.content) → 모델 텍스트 → 분류 JSON 검증. 불일치 null.</summary>
    internal static VisionVlmReply? ParseEnvelope(string json, VlmApiFlavor flavor)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? content = null;
            if (flavor == VlmApiFlavor.Ollama)
            {
                if (root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) content = c.GetString();
            }
            else if (root.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0
                     && ch[0].TryGetProperty("message", out var m2) && m2.TryGetProperty("content", out var c2) && c2.ValueKind == JsonValueKind.String)
                content = c2.GetString();
            return content is null ? null : ParseClassification(content);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>모델 텍스트에서 첫 JSON 객체를 찾아 scene/confidence 검증(코드펜스·앞뒤 잡음 허용). scene 필수, confidence 0..1 로 클램프(없으면 0.5).</summary>
    internal static VisionVlmReply? ParseClassification(string content)
    {
        var start = content.IndexOf('{');
        var end = content.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            using var doc = JsonDocument.Parse(content.AsMemory(start, end - start + 1));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("scene", out var s) || s.ValueKind != JsonValueKind.String) return null;
            var scene = MapScene(s.GetString());
            var conf = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var d) ? Math.Clamp(d, 0, 1) : 0.5;
            string? summary = root.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String ? sm.GetString() : null;
            if (summary is { Length: > 120 }) summary = summary[..120];
            return new VisionVlmReply(scene, conf, summary);
        }
        catch (JsonException) { return null; }
    }

    internal static VisionScene MapScene(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return VisionScene.Unknown;
        var l = label.Trim().ToLowerInvariant();
        if (l.Contains("explo") || l.Contains("fire") || l.Contains("flash") || l.Contains("폭발")) return VisionScene.ExplosionFlash;
        if (l.Contains("dark") || l.Contains("night") || l.Contains("어두")) return VisionScene.DarkScene;
        if (l.Contains("action") || l.Contains("fight") || l.Contains("fast") || l.Contains("액션")) return VisionScene.HighAction;
        if (l.Contains("calm") || l.Contains("static") || l.Contains("talk") || l.Contains("정적")) return VisionScene.Calm;
        return VisionScene.Unknown;
    }

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}

/// <summary>최소 PNG 인코더(RGB24, 8bit, 필터 0, zlib) - 순수 BCL(ZLibStream + 자체 CRC32). VLM 전송 전용.</summary>
public static class PngRgbEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(ReadOnlySpan<byte> rgb, int width, int height)
    {
        using var ms = new MemoryStream(width * height * 3 / 2 + 128);
        ms.Write(stackalloc byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr[0..4], width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..8], height);
        ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;   // 8bit · RGB · deflate · filter0 · no-interlace
        WriteChunk(ms, "IHDR"u8, ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var stride = width * 3;
            for (var y = 0; y < height; y++)
            {
                z.WriteByte(0);   // 스캔라인 필터 None
                z.Write(rgb.Slice(y * stride, stride));
            }
        }
        WriteChunk(ms, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        s.Write(type);
        s.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFFu, type), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    /// <summary>PNG/zlib 표준 CRC-32(다항식 0xEDB88320). 초기값·최종 XOR 은 호출측 책임(연쇄 계산용).</summary>
    public static uint Crc32(uint crc, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++) crc = CrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
