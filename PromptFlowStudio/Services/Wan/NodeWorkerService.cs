using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Mobile;   // RemoteRenderClient(IsValidWorkerUrl · BuildEndpoint)

namespace PromptFlowStudio.Services.Wan;

/// <summary>원격 GPU 렌더 워커 노드의 건강 상태 — 스케줄러가 배정 우선순위·페일오버 판정에 사용한다.</summary>
public enum WorkerNodeHealth
{
    /// <summary>미탐색(등록 직후 · 아직 하트비트 없음).</summary>
    Unknown = 0,
    /// <summary>정상 — 최우선 배정 대상.</summary>
    Online,
    /// <summary>불안정(연속 실패 이력/5xx 응답) — 도달은 되나 후순위 배정.</summary>
    Degraded,
    /// <summary>단선/타임아웃 — 배정 제외(하트비트 복구 시 재승격).</summary>
    Offline,
}

/// <summary>
/// [PromptFlow Studio v6.0.1] 분산 렌더 그리드의 <b>워커 노드 1대</b>(원격 GPU) — 주소·용량과 런타임 건강 상태를 담는 살아있는 엔티티.
///   ▸ <see cref="MaxConcurrency"/> = 이 노드의 GPU 동시 처리 슬롯 수. <see cref="InFlight"/> 가 그 미만이면 새 세그먼트를 받을 수 있다.
///   ▸ 런타임 필드(건강/인플라이트/실패수/지연)는 <see cref="DistributedRenderGrid"/> 스케줄러와 하트비트가 락 아래에서 갱신한다.
///   ⚠ <see cref="BatchItem"/> 와 동일하게 상태가 제자리 갱신되는 mutable class(그래서 record 가 아니다).
/// </summary>
public sealed class RenderWorkerNode
{
    /// <summary>노드 식별자(세그먼트 재배정 시 '이미 시도한 노드' 추적 키).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>표시용 노드 이름(예: "GPU-01 · RTX4090").</summary>
    public required string Name { get; init; }

    /// <summary>노드 백엔드 기본 주소(http/https/ws/wss). <c>api/wan/render</c> 등이 이 위에 결합된다.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>동시 처리 슬롯 수(GPU 병렬도). 최소 1로 클램프된다.</summary>
    public int MaxConcurrency { get; init; } = 1;

    // ── 런타임 상태(스케줄러/하트비트가 갱신 · BatchItem 규약과 동일하게 public set) ──
    public WorkerNodeHealth Health { get; set; } = WorkerNodeHealth.Unknown;
    /// <summary>현재 처리 중 세그먼트 수.</summary>
    public int InFlight { get; set; }
    /// <summary>연속 실패 횟수 — 임계 초과 시 Offline 격리. 렌더 성공 시 0으로 리셋.</summary>
    public int ConsecutiveFailures { get; set; }
    public long CompletedCount { get; set; }
    public long FailedCount { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    /// <summary>성공 렌더 왕복 지연 EWMA(ms) — 동률 노드 간 배정 우선순위 보정.</summary>
    public double AvgLatencyMs { get; set; }
    public string? LastError { get; set; }

    /// <summary>배정 가능한(도달 가능한) 노드인지 — Online/Degraded.</summary>
    public bool IsUsable => Health is WorkerNodeHealth.Online or WorkerNodeHealth.Degraded;

    /// <summary>지금 새 세그먼트를 받을 수 있는지(도달 가능 + 여유 슬롯).</summary>
    public bool HasFreeSlot => IsUsable && InFlight < Math.Max(1, MaxConcurrency);
}

/// <summary>
/// 세그먼트 1건의 노드 렌더 결과.
///   ▸ <see cref="NodeFault"/>=true → <b>노드 장애</b>(단선/타임아웃/5xx) 이므로 스케줄러가 다른 노드로 <b>페일오버</b>해야 한다.
///   ▸ <see cref="NodeFault"/>=false 이면서 Ok=false → 요청 자체 거부(4xx/잘못된 페이로드)이므로 다른 노드로 옮겨도 실패한다(재배정 금지).
/// </summary>
public sealed record SegmentRenderResult(
    bool Ok, bool NodeFault, string? VideoUrl, string Status, string Message, double LatencyMs);

/// <summary>
/// [PromptFlow Studio v6.0.1] 단일 워커 노드 전송 계층(순수 BCL · System.Text.Json · 외부 SDK 0) — <see cref="DistributedRenderGrid"/> 가 노드별로 사용한다.
///   ▸ <see cref="ProbeAsync"/>: 경량 하트비트. HTTP 응답이 오면(2xx~5xx 무관) 도달로 보고 5xx 만 Degraded, 예외/타임아웃은 Offline.
///   ▸ <see cref="RenderSegmentAsync"/>: <c>api/wan/render</c> POST → 즉시 URL 이면 완료, 비동기 잡이면 <c>api/wan/status/{id}</c> 폴링(노드 스코프).
///     단선/타임아웃/5xx 는 <see cref="SegmentRenderResult.NodeFault"/>=true 로 승격해 상위 스케줄러가 재배정하도록 신호한다.
///   ▸ Graceful: 어떤 경로에서도 예외를 밖으로 던지지 않고 결과 레코드로 환원한다(스케줄러 루프 무중단).
/// </summary>
public sealed class NodeWorkerService
{
    private const double LatencyAlpha = 0.3;                        // EWMA 평활 계수
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SegmentDeadline = TimeSpan.FromMinutes(5);   // 비동기 폴링 상한

    // 노드 간 커넥션 풀 공유(싱글턴 · WanRenderClient 와 동일 패턴). 개별 요청 상한은 링크드 CTS 로 건다.
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// [하트비트] 노드 도달성만 가볍게 확인한다(렌더/과금 없음). <c>api/wan/health</c> GET.
    ///   응답이 오면 도달(5xx=Degraded, 그 외=Online 성향) · 예외/타임아웃이면 Offline. 상태는 반환만 하고 노드 필드는 상위(그리드)가 갱신한다.
    /// </summary>
    /// <returns>(도달 여부, 5xx 여부).</returns>
    public async Task<(bool Reachable, bool Is5xx)> ProbeAsync(RenderWorkerNode node, CancellationToken ct = default)
    {
        if (node is null || !RemoteRenderClient.IsValidWorkerUrl(node.BaseUrl)) return (false, false);
        try
        {
            var url = RemoteRenderClient.BuildEndpoint(node.BaseUrl, "api/wan/health");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(ProbeTimeout);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            return (true, (int)resp.StatusCode >= 500);   // 어떤 HTTP 응답이든 = 도달. health 라우트 미구현(404)도 '살아있음'으로 취급.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // 상위 취소는 전파
        catch { return (false, false); }                                                 // 단선/타임아웃 = 미도달
    }

    /// <summary>
    /// [세그먼트 렌더] 한 세그먼트를 이 노드로 보내 완료까지 진행한다. 노드 장애는 <see cref="SegmentRenderResult.NodeFault"/>=true 로 환원(페일오버 신호).
    /// </summary>
    public async Task<SegmentRenderResult> RenderSegmentAsync(RenderWorkerNode node, WanRenderRequest request, CancellationToken ct = default)
    {
        if (node is null || !RemoteRenderClient.IsValidWorkerUrl(node.BaseUrl))
            return new(false, true, null, "offline", "노드 주소가 유효하지 않습니다.", 0);   // 배정 불가 = 노드 장애로 승격

        var sw = Stopwatch.StartNew();
        try
        {
            var json = JsonSerializer.Serialize(request, PromptFlowWireJsonContext.Default.WanRenderRequest);
            var url = RemoteRenderClient.BuildEndpoint(node.BaseUrl, "api/wan/render");
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(SubmitTimeout);
            using var resp = await _http.PostAsync(url, content, linked.Token).ConfigureAwait(false);

            var code = (int)resp.StatusCode;
            if (code is >= 400 and < 500)   // 요청 거부 → 다른 노드로 옮겨도 동일 실패 → 재배정 금지(NodeFault=false)
                return new(false, false, null, "rejected", $"요청 거부({code}) — 페이로드/파라미터 문제일 수 있습니다.", sw.Elapsed.TotalMilliseconds);
            if (code >= 500)                // 서버 오류 → 노드 장애 → 페일오버
                return new(false, true, null, "error", $"워커 5xx({code})", sw.Elapsed.TotalMilliseconds);

            var body = await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);
            var parsed = TryParse(body);
            var jobId = string.IsNullOrWhiteSpace(parsed?.JobId) ? request.JobId : parsed!.JobId!;

            // 동기 서버는 videoUrl 즉시 반환. 비동기 서버는 jobId/status 만 → 폴링.
            if (!string.IsNullOrWhiteSpace(parsed?.VideoUrl))
                return new(true, false, parsed!.VideoUrl, "completed", "완료", sw.Elapsed.TotalMilliseconds);

            var poll = await PollUntilDoneAsync(node, jobId, ct).ConfigureAwait(false);
            sw.Stop();
            return poll with { LatencyMs = sw.Elapsed.TotalMilliseconds };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(false, false, null, "canceled", "취소됨", sw.Elapsed.TotalMilliseconds);   // 상위 취소 = 노드 장애 아님
        }
        catch (OperationCanceledException)
        {
            return new(false, true, null, "timeout", "요청 타임아웃", sw.Elapsed.TotalMilliseconds);   // 무응답 = 노드 장애
        }
        catch (Exception ex)
        {
            Program.Log("NodeWorker.Render", ex);
            return new(false, true, null, "error", "단선/전송 오류: " + Short(ex), sw.Elapsed.TotalMilliseconds);   // 단선 = 노드 장애
        }
    }

    /// <summary>비동기 잡을 이 노드의 <c>api/wan/status/{id}</c> 로 완료(또는 실패/취소)까지 폴링. 단선/타임아웃은 NodeFault=true.</summary>
    private async Task<SegmentRenderResult> PollUntilDoneAsync(RenderWorkerNode node, string jobId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.Add(SegmentDeadline);
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return new(false, false, null, "canceled", "취소됨", 0);
            try { await Task.Delay(2500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new(false, false, null, "canceled", "취소됨", 0); }

            try
            {
                var url = RemoteRenderClient.BuildEndpoint(node.BaseUrl, "api/wan/status/" + Uri.EscapeDataString(jobId));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(ProbeTimeout);
                using var resp = await _http.GetAsync(url, linked.Token).ConfigureAwait(false);
                if ((int)resp.StatusCode >= 500) continue;   // 일시 5xx → 계속 폴링(마감 시 타임아웃 처리)

                var p = TryParse(await resp.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false));
                var status = (p?.Status ?? "").Trim().ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(p?.VideoUrl))
                    return new(true, false, p!.VideoUrl, "completed", "완료", 0);
                if (status is "failed" or "error")
                    return new(false, true, null, status, "워커 렌더 실패", 0);   // 노드측 실패 → 다른 노드 재시도 가치 있음
                // running/queued → 계속
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(false, false, null, "canceled", "취소됨", 0); }
            catch (Exception ex) { Program.Log("NodeWorker.Poll", ex); return new(false, true, null, "error", "상태 폴링 단선", 0); }
        }
        return new(false, true, null, "timeout", "세그먼트 완료 대기 시간 초과", 0);
    }

    /// <summary>성공 왕복 지연으로 노드의 EWMA 를 갱신한다(스케줄러가 락 아래에서 호출).</summary>
    public static void UpdateLatency(RenderWorkerNode node, double sampleMs)
    {
        if (node is null || sampleMs <= 0) return;
        node.AvgLatencyMs = node.AvgLatencyMs <= 0 ? sampleMs : (LatencyAlpha * sampleMs + (1 - LatencyAlpha) * node.AvgLatencyMs);
    }

    private static WorkerRenderResponse? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonSerializer.Deserialize(body!, PromptFlowWireJsonContext.Default.WorkerRenderResponse); }
        catch { return null; }
    }

    /// <summary>워커 응답 본문(WanRenderClient 규약과 동일 필드).</summary>
    internal sealed record WorkerRenderResponse(string? JobId, string? Status, string? VideoUrl, string? LastFrameUrl);

    private static string Short(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Length <= 120 ? m : m[..120] + "…";
    }
}
