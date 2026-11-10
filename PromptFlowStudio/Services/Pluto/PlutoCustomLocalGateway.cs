using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🔌 Pluto 커스텀 로컬 AI 게이트웨이 — 외부 독립 로컬 AI 마이크로서비스용 초경량 REST 클라이언트(네이티브 런타임 비탑재 · 순수 BCL).
///   ▸ 이중 프로토콜: OpenAI 호환 <c>POST /v1/chat/completions</c> 우선 → 404/405/501·연결 실패 시 Ollama 호환 <c>POST /api/chat</c>(stream=false).
///     성공한 프로토콜을 기억해 다음 호출부터 우선 사용.
///   ▸ 요청별 타임아웃(<see cref="PlutoLocalGatewayOptions.TimeoutMs"/> 기본 3.5초) · 실패/타임아웃/스키마 불일치 → null(호출자 취소만 전파).
///   ▸ 서킷: 연속 실패 시 지수 백오프 쿨다운(1→2→4…→30초) 동안 즉시 null — 서버 미기동 시 매 호출 3.5초 지연 방지. 성공/Ping 성공 시 해제.
///   ▸ 응답 상한 1MiB · <c>&lt;think&gt;</c> 블록 제거 · 산문에 둘러싸인 JSON 객체 추출.
/// </summary>
public sealed class PlutoCustomLocalGateway : IPlutoLlmClient, IDisposable
{
    public const string OpenAiChatPath = "v1/chat/completions";
    public const string OllamaChatPath = "api/chat";
    public const string OpenAiModelsPath = "v1/models";
    public const string OllamaTagsPath = "api/tags";
    public const double DefaultTemperature = 0.7;
    public const int MaxResponseBytes = 1 << 20;
    private const int MaxCooldownMs = 30_000;

    private readonly HttpClient _http;
    private int _preferOllama;          // 0 = OpenAI 우선 · 1 = Ollama 우선
    private int _failures;
    private long _circuitOpenUntil;     // Environment.TickCount64 기준
    private int _disposed;

    public PlutoLocalGatewayOptions Options { get; }

    public PlutoCustomLocalGateway() : this(null, null) { }

    /// <param name="handler">테스트/프록시용 핸들러(소유권은 호출자 — Dispose 하지 않음).</param>
    public PlutoCustomLocalGateway(PlutoLocalGatewayOptions? options, HttpMessageHandler? handler = null)
    {
        Options = options ?? PlutoLocalGatewayOptions.FromEnvironment();
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 요청별 CTS로 제어
    }

    /// <summary>활성 · 유효 엔드포인트 · 서킷 닫힘일 때만 true(false면 브리지가 즉시 템플릿 폴백).</summary>
    public bool IsAvailable => Volatile.Read(ref _disposed) == 0 && Options.IsEnabled && Options.TryResolveBase(out _) && !IsCircuitOpen;

    public bool IsCircuitOpen => Environment.TickCount64 < Interlocked.Read(ref _circuitOpenUntil);

    /// <summary>마지막 성공 프로토콜이 Ollama인지(진단 표시용).</summary>
    public bool PrefersOllama => Volatile.Read(ref _preferOllama) == 1;

    /// <summary>📡 서버 가용성 — <c>GET /v1/models</c> → 실패 시 <c>GET /api/tags</c>. 2xx면 true · 서킷 해제. 예외 없음(호출자 취소 제외).</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || !Options.TryResolveBase(out var baseUri)) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Options.EffectiveTimeout);
        try
        {
            if (await GetOkAsync(new Uri(baseUri, OpenAiModelsPath), cts.Token).ConfigureAwait(false)
                || await GetOkAsync(new Uri(baseUri, OllamaTagsPath), cts.Token).ConfigureAwait(false))
            {
                OnSuccess();
                return true;
            }
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException) { Log("ping", ex); return false; }
    }

    /// <summary>시스템/사용자 지시 → 응답 본문(temperature 0.7). 실패/타임아웃 → null.</summary>
    public Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        => CompleteJsonAsync(systemPrompt, userPrompt, DefaultTemperature, ct);

    /// <inheritdoc/>
    public async Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
    {
        if (Volatile.Read(ref _disposed) != 0 || !Options.IsEnabled || !Options.TryResolveBase(out var baseUri) || IsCircuitOpen) return null;
        var model = string.IsNullOrWhiteSpace(Options.ModelName) ? PlutoLocalGatewayOptions.DefaultModelName : Options.ModelName.Trim();
        var key = Options.ApiKey;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Options.EffectiveTimeout);
        try
        {
            var ollamaFirst = PrefersOllama;
            var (text, tryAlternate) = await SendChatAsync(baseUri, ollamaFirst, model, key, system, user, temperature, cts.Token).ConfigureAwait(false);
            if (text is null && tryAlternate)
            {
                (text, _) = await SendChatAsync(baseUri, !ollamaFirst, model, key, system, user, temperature, cts.Token).ConfigureAwait(false);
                if (text is not null) Volatile.Write(ref _preferOllama, ollamaFirst ? 0 : 1);
            }
            if (text is null) { OnFailure(); return null; }
            OnSuccess();
            return text;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or JsonException or ObjectDisposedException or InvalidOperationException)
        {
            Log("chat", ex);
            OnFailure();
            return null;
        }
    }

    /// <returns>(본문 또는 null, 대체 프로토콜 시도 여부)</returns>
    private async Task<(string? Text, bool TryAlternate)> SendChatAsync(Uri baseUri, bool ollama, string model, string? key, string system, string user, double temperature, CancellationToken ct)
    {
        var payload = ollama ? BuildOllamaPayload(model, system, user, temperature) : BuildOpenAiPayload(model, system, user, temperature);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, ollama ? OllamaChatPath : OpenAiChatPath))
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { Log(ollama ? "ollama" : "openai", ex); return (null, true); }   // 🔁 연결 실패 → 대체 프로토콜
        using (resp)
        {
            if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented) return (null, true);
            if (!resp.IsSuccessStatusCode) return (null, false);
            var body = await ReadBoundedAsync(resp.Content, ct).ConfigureAwait(false);
            return (body is null ? null : ExtractContent(body), false);
        }
    }

    private async Task<bool> GetOkAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(Options.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxResponseBytes) return null;
        await using var s = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await s.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxResponseBytes) return null;
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    // ── 페이로드/응답(테스트 검증용 internal) ──

    /// <summary>OpenAI 호환: <c>{"model","messages":[system,user],"temperature"}</c>.</summary>
    internal static string BuildOpenAiPayload(string model, string system, string user, double temperature)
        => BuildPayload(model, system, user, temperature, ollama: false);

    /// <summary>Ollama 호환: <c>{"model","messages":[system,user],"stream":false,"options":{"temperature"}}</c>.</summary>
    internal static string BuildOllamaPayload(string model, string system, string user, double temperature)
        => BuildPayload(model, system, user, temperature, ollama: true);

    private static string BuildPayload(string model, string system, string user, double temperature, bool ollama)
    {
        var t = double.IsFinite(temperature) ? Math.Clamp(temperature, 0.0, 2.0) : DefaultTemperature;
        using var ms = new MemoryStream(256 + (system?.Length ?? 0) + (user?.Length ?? 0));
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteStartArray("messages");
            w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", system ?? ""); w.WriteEndObject();
            w.WriteStartObject(); w.WriteString("role", "user"); w.WriteString("content", user ?? ""); w.WriteEndObject();
            w.WriteEndArray();
            if (ollama)
            {
                w.WriteBoolean("stream", false);
                w.WriteStartObject("options"); w.WriteNumber("temperature", t); w.WriteEndObject();
            }
            else w.WriteNumber("temperature", t);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    /// <summary>응답 스키마 즉시 검증 — OpenAI <c>choices[0].message.content</c> · Ollama <c>message.content</c>. 불일치/빈 본문 → null.</summary>
    internal static string? ExtractContent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            JsonElement msg = default;
            var found = false;
            if (r.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0
                && ch[0].ValueKind == JsonValueKind.Object && ch[0].TryGetProperty("message", out msg)) found = true;
            else if (r.TryGetProperty("message", out msg)) found = true;
            if (!found || msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("content", out var c) || c.ValueKind != JsonValueKind.String) return null;
            return NormalizeContent(c.GetString());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>추론형 소형 모델 출력 정리 — <c>&lt;think&gt;…&lt;/think&gt;</c> 제거 · 코드펜스는 보존 · 산문 속 JSON 객체 추출.</summary>
    internal static string? NormalizeContent(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var text = s.Trim();
        var end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (text.StartsWith("<think>", StringComparison.OrdinalIgnoreCase) && end >= 0) text = text[(end + 8)..].Trim();
        if (text.Length == 0) return null;
        if (text[0] is '{' or '[' || text.StartsWith("```", StringComparison.Ordinal)) return text;
        var a = text.IndexOf('{');
        var b = text.LastIndexOf('}');
        return a >= 0 && b > a ? text[a..(b + 1)] : text;
    }

    private void OnSuccess()
    {
        Interlocked.Exchange(ref _failures, 0);
        Interlocked.Exchange(ref _circuitOpenUntil, 0);
    }

    private void OnFailure()
    {
        var n = Interlocked.Increment(ref _failures);
        var cooldown = Math.Min(MaxCooldownMs, 1000 << Math.Min(n - 1, 5));
        Interlocked.Exchange(ref _circuitOpenUntil, Environment.TickCount64 + cooldown);
    }

    private static void Log(string stage, Exception ex) => System.Diagnostics.Debug.WriteLine("[Pluto.Gateway] " + stage + " fallback: " + ex.Message);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _http.Dispose();
    }
}
