using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
    /// <summary>🌊 스트리밍 1회 전체 상한(청크 간 유휴 타임아웃과 별개).</summary>
    public static readonly TimeSpan StreamTotalBudget = TimeSpan.FromMilliseconds(PlutoLocalGatewayOptions.MaxTimeoutMs);

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
    public async Task<bool> PingAsync(CancellationToken ct = default) => (await PingDetailedAsync(ct).ConfigureAwait(false)).IsOnline;

    /// <summary>📡 진단 Ping — 온라인 여부 · 감지 프로토콜(OpenAI/Ollama) · 왕복 지연 · 사용자용 오류 팁. 예외 없음(호출자 취소 제외).</summary>
    public async Task<PlutoGatewayPingResult> PingDetailedAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return PlutoGatewayPingResult.Offline("게이트웨이가 종료되었어요");
        if (!Options.TryResolveBase(out var baseUri)) return PlutoGatewayPingResult.Offline(PlutoGatewayPingResult.InvalidUrlTip);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Options.EffectiveTimeout);
        var t0 = Environment.TickCount64;
        try
        {
            var openAi = await GetStatusAsync(new Uri(baseUri, OpenAiModelsPath), cts.Token).ConfigureAwait(false);
            if (openAi is >= 200 and < 300) { OnSuccess(); return PlutoGatewayPingResult.Online(PlutoGatewayPingResult.OpenAiProtocol, Environment.TickCount64 - t0); }
            var ollama = await GetStatusAsync(new Uri(baseUri, OllamaTagsPath), cts.Token).ConfigureAwait(false);
            if (ollama is >= 200 and < 300)
            {
                Volatile.Write(ref _preferOllama, 1);
                OnSuccess();
                return PlutoGatewayPingResult.Online(PlutoGatewayPingResult.OllamaProtocol, Environment.TickCount64 - t0);
            }
            if (openAi < 0 && ollama < 0) return PlutoGatewayPingResult.Offline(PlutoGatewayPingResult.RefusedTip);
            if (openAi is 401 or 403 || ollama is 401 or 403) return PlutoGatewayPingResult.Offline(PlutoGatewayPingResult.AuthTip);
            return PlutoGatewayPingResult.Offline(PlutoGatewayPingResult.NoEndpointTip);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return PlutoGatewayPingResult.Offline(PlutoGatewayPingResult.TimeoutTip); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException) { Log("ping", ex); return PlutoGatewayPingResult.Offline(PlutoGatewayPingResult.RefusedTip); }
    }

    /// <summary>🔄 설정 변경 직후 — 서킷·실패 카운트·프로토콜 선호 초기화(새 엔드포인트를 즉시 재시도).</summary>
    public void ResetState()
    {
        OnSuccess();
        Volatile.Write(ref _preferOllama, 0);
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

    /// <summary>
    /// 🌊 스트리밍 채팅(<c>stream:true</c> · OpenAI SSE / Ollama NDJSON) → 응답 배열 요소가 닫히는 즉시 방출(TTFT 단축 · 본문 전체 버퍼링 없음).
    ///   ▸ 헤더 도착 · 청크 간 유휴는 <see cref="PlutoLocalGatewayOptions.EffectiveTimeout"/>, 전체는 <see cref="StreamTotalBudget"/> 상한.
    ///   ▸ 절단(컨텍스트 한도 · 소켓 종료 · 타임아웃) 시 진행 중 요소 봉합 후 정상 종료. 요소 0건이면 서킷 실패 집계. 호출자 취소만 전파(구제분 방출 후).
    /// </summary>
    public async IAsyncEnumerable<T> StreamArrayAsync<T>(string system, string user, double temperature, PlutoJsonElementParser<T> parser,
        PlutoStreamReport? report = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(parser);
        report ??= new PlutoStreamReport();
        if (Volatile.Read(ref _disposed) != 0 || !Options.IsEnabled || !Options.TryResolveBase(out var baseUri) || IsCircuitOpen) yield break;
        var model = string.IsNullOrWhiteSpace(Options.ModelName) ? PlutoLocalGatewayOptions.DefaultModelName : Options.ModelName.Trim();
        var idle = Options.EffectiveTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(idle);
        var resp = await OpenChatStreamAsync(baseUri, model, Options.ApiKey, system, user, temperature, cts.Token, ct).ConfigureAwait(false);
        if (resp is null) { OnFailure(); yield break; }
        try
        {
            var body = await OpenBodyAsync(resp, cts.Token, ct).ConfigureAwait(false);
            if (body is not null)
            {
                await foreach (var item in PlutoStreamingPipeConsumer.StreamArrayAsync(body, parser, report, cts, idle, StreamTotalBudget, cts.Token).ConfigureAwait(false))
                    yield return item;
            }
        }
        finally { resp.Dispose(); }
        if (ct.IsCancellationRequested) { report.End = PlutoStreamEnd.Canceled; ct.ThrowIfCancellationRequested(); }   // 사용자 취소는 서킷 미집계
        if (report.Elements > 0) OnSuccess(); else OnFailure();
    }

    /// <summary>스트리밍 응답 헤더까지 수신(성공 2xx만 반환 · 프로토콜 대체 포함). 실패 → null · 호출자 취소만 전파.</summary>
    private async Task<HttpResponseMessage?> OpenChatStreamAsync(Uri baseUri, string model, string? key, string system, string user, double temperature, CancellationToken linked, CancellationToken callerCt)
    {
        try
        {
            var ollamaFirst = PrefersOllama;
            var (resp, tryAlternate) = await SendChatStreamAsync(baseUri, ollamaFirst, model, key, system, user, temperature, linked).ConfigureAwait(false);
            if (resp is null && tryAlternate)
            {
                (resp, _) = await SendChatStreamAsync(baseUri, !ollamaFirst, model, key, system, user, temperature, linked).ConfigureAwait(false);
                if (resp is not null) Volatile.Write(ref _preferOllama, ollamaFirst ? 0 : 1);
            }
            return resp;
        }
        catch (OperationCanceledException) when (callerCt.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log("stream", ex);
            return null;
        }
    }

    private async Task<(HttpResponseMessage? Response, bool TryAlternate)> SendChatStreamAsync(Uri baseUri, bool ollama, string model, string? key, string system, string user, double temperature, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, ollama ? OllamaChatPath : OpenAiChatPath))
        {
            Content = new StringContent(BuildPayload(model, system, user, temperature, ollama, stream: true), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(key)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { Log(ollama ? "ollama" : "openai", ex); return (null, true); }   // 🔁 연결 실패 → 대체 프로토콜
        if (resp.IsSuccessStatusCode) return (resp, false);
        var alternate = resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;
        resp.Dispose();
        return (null, alternate);
    }

    private static async Task<Stream?> OpenBodyAsync(HttpResponseMessage resp, CancellationToken linked, CancellationToken callerCt)
    {
        try { return await resp.Content.ReadAsStreamAsync(linked).ConfigureAwait(false); }
        catch (OperationCanceledException) when (callerCt.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log("stream-body", ex);
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

    /// <returns>HTTP 상태 코드 · 연결 실패 시 -1.</returns>
    private async Task<int> GetStatusAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrEmpty(Options.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Options.ApiKey);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)resp.StatusCode;
        }
        catch (HttpRequestException) { return -1; }
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

    /// <summary>스트리밍 요청 본문 — OpenAI <c>"stream":true</c> · Ollama <c>"stream":true</c>(NDJSON).</summary>
    internal static string BuildStreamPayload(string model, string system, string user, double temperature, bool ollama)
        => BuildPayload(model, system, user, temperature, ollama, stream: true);

    private static string BuildPayload(string model, string system, string user, double temperature, bool ollama, bool stream = false)
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
                w.WriteBoolean("stream", stream);
                w.WriteStartObject("options"); w.WriteNumber("temperature", t); w.WriteEndObject();
            }
            else
            {
                w.WriteNumber("temperature", t);
                if (stream) w.WriteBoolean("stream", true);
            }
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
        var healed = PlutoJsonSanitizer.Sanitize(text);   // 🩹 산문 속 JSON 추출 — 잘린 출력도 마지막 '}' 오절단 없이 봉합
        return healed.Length > 0 ? healed : text;
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
