using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Security;   // SecureSecretVault(v10.5.0-F1)
using PromptFlowStudio.Services.Wan;        // WanPipelineHardening.IsTransientHttp/IsTransientException(정적 재사용)

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>
/// v10.5.0-F2 ComfyUI 원격 백엔드 커넥터 — REST(<c>POST /prompt</c> 워크플로 그래프 제출) + WebSocket
/// (<c>ws://{host}/ws?clientId=</c>, <c>progress</c>/<c>executing</c>/<c>executed</c> 메시지)로 타일/노드 단위
/// 진행률을 프로그레시브 스트리밍한다. CLAUDE.md Rule 7 준수: <see cref="HttpClient.Timeout"/>+<see cref="CancellationTokenSource"/>
/// 이중 타임아웃, 지수 백오프(전송계열 오류만 재시도 — <see cref="WanPipelineHardening.IsTransientHttp"/>/
/// <see cref="WanPipelineHardening.IsTransientException"/> 정적 분류기 재사용), 원격 API 키는
/// <see cref="SecureSecretVault.TryGetExternalCredential"/>(환경변수 전용·자동생성 없음)로 조회 후 없으면 무인증 모드.
///   ⚠ 워크플로 그래프(<see cref="BuildWorkflowGraph"/>)는 표준 ComfyUI 노드(CheckpointLoaderSimple 등)로 구성한
///   <b>결선점 플레이스홀더</b> — 실제 설치된 Wan 2.1 커스텀 노드 이름/입력은 운영 환경마다 달라 그대로 교체 필요
///   (교체 시 이 클래스의 나머지 제출/스트리밍/취소 로직은 무변경). 순수 BCL(HttpClient/ClientWebSocket/System.Text.Json)
///   · 네트워킹뿐이라 하드웨어 비의존 · 데스크톱/모바일 공용.
/// </summary>
public sealed class ComfyUiWanConnector : IWanBackendConnector, IDisposable
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private sealed class JobState
    {
        public string? PromptId;
        public readonly Channel<WanBackendProgress> Channel = System.Threading.Channels.Channel.CreateUnbounded<WanBackendProgress>();
    }

    private readonly HttpClient _http;
    private readonly SecureSecretVault _vault;
    private readonly string _baseUrl;
    private readonly string _clientId = Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _promptIdToJobId = new(StringComparer.Ordinal);
    private readonly object _wsGate = new();
    private ClientWebSocket? _ws;
    private Task? _wsReaderTask;
    private readonly Random _jitter = new();

    public WanBackendKind Kind => WanBackendKind.ComfyUi;
    public string? LastAction { get; private set; }

    public ComfyUiWanConnector(SecureSecretVault vault, string baseUrl = "http://127.0.0.1:8188")
    {
        _vault = vault;
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient { Timeout = RequestTimeout };
        var apiKey = _vault.TryGetExternalCredential("comfyui-api-key");   // 없으면 무인증(로컬 self-host 기본값)
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <summary>짧은 타임아웃 헬스체크(<c>GET /system_stats</c>) — 실패는 전부 "미가용"으로 수렴(Graceful).</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HealthCheckTimeout);
        try
        {
            using var res = await _http.GetAsync(_baseUrl + "/system_stats", cts.Token).ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    public async Task<WanBackendSubmitResult> SubmitJobAsync(WanBackendJobRequest request, CancellationToken ct = default)
    {
        var state = _jobs.GetOrAdd(request.JobId, _ => new JobState());
        var graph = BuildWorkflowGraph(request);
        var body = new JsonObject { ["prompt"] = graph, ["client_id"] = _clientId };

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            try
            {
                using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var res = await _http.PostAsync(_baseUrl + "/prompt", content, cts.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    var json = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var promptId = doc.RootElement.TryGetProperty("prompt_id", out var pidEl) ? pidEl.GetString() : null;
                    if (string.IsNullOrEmpty(promptId))
                    {
                        LastAction = $"작업 {request.JobId} 제출 응답에 prompt_id 없음 — 거부";
                        return new WanBackendSubmitResult(false, request.JobId, Kind, "ComfyUI 응답에 prompt_id 누락");
                    }
                    state.PromptId = promptId;
                    _promptIdToJobId[promptId] = request.JobId;
                    await EnsureWebSocketAsync(ct).ConfigureAwait(false);
                    LastAction = $"작업 {request.JobId} 제출 완료 · prompt_id={promptId}";
                    return new WanBackendSubmitResult(true, request.JobId, Kind, "제출 완료");
                }
                if (!IsRetryableStatus(res.StatusCode) || attempt == MaxAttempts)
                {
                    LastAction = $"작업 {request.JobId} 제출 실패 · HTTP {(int)res.StatusCode}";
                    return new WanBackendSubmitResult(false, request.JobId, Kind, $"HTTP {(int)res.StatusCode}");
                }
            }
            catch (Exception ex) when (IsRetryableException(ex))
            {
                if (attempt == MaxAttempts)
                {
                    LastAction = $"작업 {request.JobId} 제출 실패(재시도 소진) · {ex.Message}";
                    return new WanBackendSubmitResult(false, request.JobId, Kind, ex.Message);
                }
            }
            await Task.Delay(BackoffFor(attempt), ct).ConfigureAwait(false);
        }
        return new WanBackendSubmitResult(false, request.JobId, Kind, "재시도 소진");
    }

    public async IAsyncEnumerable<WanBackendProgress> StreamProgressAsync(string jobId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) yield break;   // 제출된 적 없는 작업 — Graceful 빈 스트림
        await foreach (var progress in state.Channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return progress;
            if (progress.Stage is WanBackendStage.Completed or WanBackendStage.Failed or WanBackendStage.Cancelled) yield break;
        }
    }

    public async Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var state) || state.PromptId is not { } promptId) return;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            // 큐 대기 중이면 삭제, 실행 중이면 인터럽트(ComfyUI 실제 API — prompt_id 특정 인터럽트는 미지원이라 광의 처리).
            var delBody = new JsonObject { ["delete"] = new JsonArray(promptId) };
            using var delContent = new StringContent(delBody.ToJsonString(), Encoding.UTF8, "application/json");
            using var _ = await _http.PostAsync(_baseUrl + "/queue", delContent, cts.Token).ConfigureAwait(false);
            using var __ = await _http.PostAsync(_baseUrl + "/interrupt", new StringContent(""), cts.Token).ConfigureAwait(false);
            LastAction = $"작업 {jobId} 취소 요청 전송(큐삭제+인터럽트)";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            LastAction = $"작업 {jobId} 취소 요청 실패(네트워크) — Graceful 무시";
        }
        finally
        {
            state.Channel.Writer.TryComplete();
        }
    }

    // ── WebSocket 진행률 수신(지연 연결 · 1개 커넥션을 전 작업이 공유 · prompt_id 로 작업 매핑) ──
    private async Task EnsureWebSocketAsync(CancellationToken ct)
    {
        lock (_wsGate) { if (_ws is { State: WebSocketState.Open }) return; }
        var ws = new ClientWebSocket();
        var wsUri = new Uri(_baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws?clientId=" + _clientId);
        try { await ws.ConnectAsync(wsUri, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            LastAction = "WebSocket 연결 실패 — 진행률 스트리밍 불가(제출 자체는 유지)";
            ws.Dispose();
            return;
        }
        lock (_wsGate) { _ws = ws; _wsReaderTask = Task.Run(() => ReadWebSocketLoopAsync(ws)); }
    }

    private async Task ReadWebSocketLoopAsync(ClientWebSocket ws)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                using var ms = new System.IO.MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) continue;   // 미리보기 프레임 바이너리 프레임은 v1 스킵(결선점)
                DispatchMessage(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            LastAction = "WebSocket 수신 루프 종료(연결 단절) — Graceful";
        }
    }

    // ComfyUI 표준 메시지: {"type":"progress","data":{"value":N,"max":M,"prompt_id":".."}} /
    // {"type":"executing","data":{"node":null,"prompt_id":".."}}(node=null 은 실행 완료 신호) / {"type":"execution_error",...}
    private void DispatchMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("prompt_id", out var pidEl)) return;
            var promptId = pidEl.GetString();
            if (promptId is null || !_promptIdToJobId.TryGetValue(promptId, out var jobId) || !_jobs.TryGetValue(jobId, out var state)) return;

            switch (type)
            {
                case "progress" when data.TryGetProperty("value", out var v) && data.TryGetProperty("max", out var m):
                    var pct = m.GetInt32() > 0 ? (int)(v.GetInt32() * 100.0 / m.GetInt32()) : 0;
                    state.Channel.Writer.TryWrite(new WanBackendProgress(jobId, WanBackendStage.Sampling, pct, $"샘플링 {v.GetInt32()}/{m.GetInt32()}"));
                    break;
                case "executing" when data.TryGetProperty("node", out var nodeEl):
                    if (nodeEl.ValueKind == JsonValueKind.Null)
                    {
                        state.Channel.Writer.TryWrite(new WanBackendProgress(jobId, WanBackendStage.Completed, 100, "실행 완료"));
                        state.Channel.Writer.TryComplete();
                    }
                    else
                    {
                        state.Channel.Writer.TryWrite(new WanBackendProgress(jobId, WanBackendStage.Loading, 0, $"노드 실행 중: {nodeEl.GetString()}"));
                    }
                    break;
                case "execution_error":
                    state.Channel.Writer.TryWrite(new WanBackendProgress(jobId, WanBackendStage.Failed, 0, "ComfyUI 실행 오류"));
                    state.Channel.Writer.TryComplete();
                    break;
            }
        }
        catch (JsonException) { /* 형식 불일치 메시지 — 무시(Graceful) */ }
    }

    // ⚠ 결선점: 표준 ComfyUI 노드로 구성한 플레이스홀더 그래프. 실 Wan 2.1 커스텀 노드(체크포인트/샘플러 이름)로 교체 필요.
    private static JsonObject BuildWorkflowGraph(WanBackendJobRequest req)
    {
        var spec = req.Spec;
        return new JsonObject
        {
            ["3"] = new JsonObject   // KSampler
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new JsonObject
                {
                    ["seed"] = Environment.TickCount64,
                    ["steps"] = 20,
                    ["cfg"] = 7.0,
                    ["model"] = new JsonArray(4, 0),
                    ["positive"] = new JsonArray(6, 0),
                    ["negative"] = new JsonArray(7, 0),
                    ["latent_image"] = new JsonArray(5, 0),
                },
            },
            ["4"] = new JsonObject { ["class_type"] = "CheckpointLoaderSimple", ["inputs"] = new JsonObject { ["ckpt_name"] = spec.ModelTierId } },
            ["5"] = new JsonObject { ["class_type"] = "EmptyLatentImage", ["inputs"] = new JsonObject { ["width"] = spec.Width, ["height"] = spec.Height, ["batch_size"] = spec.Frames } },
            ["6"] = new JsonObject { ["class_type"] = "CLIPTextEncode", ["inputs"] = new JsonObject { ["text"] = req.Prompt, ["clip"] = new JsonArray(4, 1) } },
            ["7"] = new JsonObject { ["class_type"] = "CLIPTextEncode", ["inputs"] = new JsonObject { ["text"] = "", ["clip"] = new JsonArray(4, 1) } },
            ["8"] = new JsonObject { ["class_type"] = "VAEDecode", ["inputs"] = new JsonObject { ["samples"] = new JsonArray(3, 0), ["vae"] = new JsonArray(4, 2) } },
            ["9"] = new JsonObject { ["class_type"] = "SaveImage", ["inputs"] = new JsonObject { ["images"] = new JsonArray(8, 0), ["filename_prefix"] = "promptflow_wan_" + req.JobId } },
        };
    }

    private static bool IsRetryableStatus(HttpStatusCode status) => WanPipelineHardening.IsTransientHttp(status);
    private static bool IsRetryableException(Exception ex) => WanPipelineHardening.IsTransientException(ex);

    private TimeSpan BackoffFor(int attempt)
    {
        var baseMs = Math.Min(BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt - 1), MaxBackoff.TotalMilliseconds);
        var jitterMs = baseMs * 0.2 * (_jitter.NextDouble() * 2 - 1);
        return TimeSpan.FromMilliseconds(Math.Max(50, baseMs + jitterMs));
    }

    public void Dispose()
    {
        lock (_wsGate) { _ws?.Dispose(); _ws = null; }
        _http.Dispose();
        foreach (var kv in _jobs) kv.Value.Channel.Writer.TryComplete();
    }
}
