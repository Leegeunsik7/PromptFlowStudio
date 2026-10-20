using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>Edge 서버 상태 — 유휴/수신중/실패.</summary>
public enum EdgeServerState { Idle, Listening, Failed }

/// <summary>외부 렌더 배치 요청 본문(POST /api/v1/render/batch).</summary>
public sealed record RenderBatchRequest(string? Prompt, int? Segments, string? TenantId);

/// <summary>Edge 헬스체크 응답(GET /).</summary>
internal sealed record EdgeHealthPayload(int Status, string Service, bool Listening);

/// <summary>Edge 단순 상태 응답(404/500 등).</summary>
internal sealed record EdgeSimpleStatusPayload(int Status, string Message);

/// <summary>API 게이트웨이 판정 응답(배치 렌더 제출 결과 — 성공/거부/한도초과).</summary>
internal sealed record ApiErrorResponsePayload(int Status, string Message, string? Tenant, int? RetryAfterSec);

/// <summary>
/// [PromptFlow Studio v6.3.0] 경량 REST API Edge 서버(관찰 가능 싱글턴 · 순수 BCL <see cref="HttpListener"/> · 외부 웹 프레임워크 0 · IDisposable).
///   ▸ 무거운 ASP.NET Core 호스트 없이 백그라운드 HTTP 수신기를 띄워 외부 B2B 소켓 트래픽을 수용한다.
///   ▸ 수신 루프는 완전 비동기(<see cref="HttpListener.GetContextAsync"/>) — UI/렌더 스레드 무병목. 요청 처리도 per-request fire-and-forget.
///   ▸ <c>POST /api/v1/render/batch</c>: <b>X-API-Key</b> 헤더를 추출해 <see cref="ApiGatewayEngine.SubmitRenderAsync"/>로 위임
///     → HMAC 인증·Token Bucket·GPU 가용성 판정 결과를 401/429/503/200 상태 코드로 그대로 응답.
///   ▸ Graceful: 포트 점유·권한(URL ACL) 실패는 예외를 삼키고 <see cref="State"/>=Failed 로 보고(앱 무중단). 기본은 localhost 바인딩(관리자 권한 불요).
///   ⚠ <see cref="OnChange"/> 는 수신/처리(백그라운드) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class EmbeddedHttpServer : IDisposable
{
    private readonly ApiGatewayEngine _gateway;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    private long _requests;

    public EmbeddedHttpServer(ApiGatewayEngine gateway) { _gateway = gateway; }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 상태(HUD) ──
    public EdgeServerState State { get; private set; } = EdgeServerState.Idle;
    public string? Prefix { get; private set; }
    public string? LastError { get; private set; }
    public long RequestCount => Interlocked.Read(ref _requests);
    public bool IsListening => State == EdgeServerState.Listening;

    /// <summary>
    /// 지정 포트(기본 8787)에서 수신 시작(localhost 바인딩). 이미 수신 중이면 무시. 실패는 Graceful(State=Failed).
    /// </summary>
    public bool Start(int port = 8787)
    {
        if (IsListening) return true;
        var prefix = $"http://localhost:{Math.Clamp(port, 1, 65535)}/";
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();

            lock (_gate)
            {
                _listener = listener;
                _cts = new CancellationTokenSource();
                Prefix = prefix; LastError = null; State = EdgeServerState.Listening;
                _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            }
            Notify();
            return true;
        }
        catch (Exception ex)
        {
            Program.Log("EdgeServer.Start", ex);
            State = EdgeServerState.Failed;
            LastError = Short(ex);
            Notify();
            return false;
        }
    }

    /// <summary>수신 중지(리스너 종료 → 수신 루프가 자연 탈출). 멱등.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch { /* 무시 */ }
            try { _listener?.Stop(); _listener?.Close(); } catch { /* 무시 */ }   // GetContextAsync 를 깨워 루프 종료
            _listener = null;
            State = EdgeServerState.Idle; Prefix = null;
        }
        Notify();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }   // Stop() 으로 리스너 종료됨
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Program.Log("EdgeServer.Accept", ex); break; }

            // 요청 처리는 per-request 로 분리 → 수신 루프는 즉시 다음 연결 수용(무병목).
            _ = HandleAsync(ctx, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            Interlocked.Increment(ref _requests);
            var req = ctx.Request;
            var path = (req.Url?.AbsolutePath ?? "").TrimEnd('/');

            // 헬스 체크(인증 불요).
            if (req.HttpMethod == "GET" && (path == "/health" || path == ""))
            {
                await WriteJsonAsync(ctx, 200, new EdgeHealthPayload(200, "PromptFlow Edge", true), PromptFlowWireJsonContext.Default.EdgeHealthPayload).ConfigureAwait(false);
                Notify();
                return;
            }

            if (req.HttpMethod == "POST" && path == "/api/v1/render/batch")
            {
                await HandleRenderBatchAsync(ctx, ct).ConfigureAwait(false);
                Notify();
                return;
            }

            await WriteJsonAsync(ctx, 404, new EdgeSimpleStatusPayload(404, "Not Found"), PromptFlowWireJsonContext.Default.EdgeSimpleStatusPayload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Program.Log("EdgeServer.Handle", ex);
            try { await WriteJsonAsync(ctx, 500, new EdgeSimpleStatusPayload(500, "Internal error"), PromptFlowWireJsonContext.Default.EdgeSimpleStatusPayload).ConfigureAwait(false); } catch { /* 무시 */ }
        }
    }

    private async Task HandleRenderBatchAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        // X-API-Key 추출 → 게이트웨이 위임(HMAC 인증 + Token Bucket + GPU 가용성).
        var apiKey = ctx.Request.Headers["X-API-Key"];

        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8))
            body = await reader.ReadToEndAsync().ConfigureAwait(false);

        var parsed = TryParse(body);
        var prompt = parsed?.Prompt ?? "";
        var segments = parsed?.Segments ?? 1;

        var result = await _gateway.SubmitRenderAsync(apiKey, prompt, segments, ct).ConfigureAwait(false);

        // 401/429/503 은 게이트웨이 판정을 그대로 상태 코드로 전달.
        if (result.RetryAfterSec is int retry && (result.Code == 429 || result.Code == 503))
            ctx.Response.Headers["Retry-After"] = retry.ToString();

        await WriteJsonAsync(ctx, result.Code,
            new ApiErrorResponsePayload(result.Code, result.Message, result.TenantId, result.RetryAfterSec),
            PromptFlowWireJsonContext.Default.ApiErrorResponsePayload).ConfigureAwait(false);
    }

    private static RenderBatchRequest? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize(body!, PromptFlowWireJsonContext.Default.RenderBatchRequest); }
        catch { return null; }
    }

    private static async Task WriteJsonAsync<T>(HttpListenerContext ctx, int statusCode, T payload, JsonTypeInfo<T> typeInfo)
    {
        var json = JsonSerializer.Serialize(payload, typeInfo);
        var bytes = Encoding.UTF8.GetBytes(json);
        var resp = ctx.Response;
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = bytes.Length;
        try
        {
            await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        finally { resp.OutputStream.Close(); }
    }

    private static string Short(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Length <= 140 ? m : m[..140] + "…";
    }

    public void Dispose() => Stop();
}
