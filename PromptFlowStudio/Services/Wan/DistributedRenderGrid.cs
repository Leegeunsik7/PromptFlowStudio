using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>분산 그리드가 처리하는 세그먼트 1건의 생명주기.</summary>
public enum SegmentState
{
    /// <summary>대기(미배정) — 유휴 노드가 생기면 배정된다.</summary>
    Queued,
    /// <summary>노드에 배정되어 렌더 진행 중.</summary>
    Dispatched,
    /// <summary>완료(VideoUrl 확보).</summary>
    Completed,
    /// <summary>실패(페일오버 소진 또는 요청 거부).</summary>
    Failed,
}

/// <summary>
/// [PromptFlow Studio v6.0.1] 분산 렌더 큐의 세그먼트 1건 — 원 요청 + 런타임 배정/재배정 상태(살아있는 엔티티).
///   ▸ <see cref="TriedNodeIds"/> 로 이미 실패한 노드로의 재배정을 차단(페일오버 시 서로 다른 노드로만 이동).
/// </summary>
public sealed class RenderSegment
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>원래 큐 순서(0-based) — 병합(concat) 시 정렬 키.</summary>
    public int Index { get; init; }
    /// <summary>이 세그먼트의 Wan 렌더 요청 페이로드.</summary>
    public required WanRenderRequest Request { get; init; }

    // ── 런타임 상태 ──
    public SegmentState State { get; set; } = SegmentState.Queued;
    public string? AssignedNodeId { get; set; }
    public string? AssignedNodeName { get; set; }
    /// <summary>총 배정 시도 횟수(페일오버 상한 판정).</summary>
    public int Attempts { get; set; }
    public string? VideoUrl { get; set; }
    public string? Error { get; set; }
    /// <summary>이미 배정을 시도한 노드 Id 집합(같은 노드 재배정 금지).</summary>
    public HashSet<string> TriedNodeIds { get; } = new();

    public bool IsTerminal => State is SegmentState.Completed or SegmentState.Failed;
}

/// <summary>분산 진행 스냅샷(HUD/진행바용) — 스케줄러가 <see cref="IProgress{T}"/> 로 보고.</summary>
public sealed record GridProgress(int Total, int Completed, int Failed, int Dispatched, int Queued, int Percent);

/// <summary>
/// 분산 렌더 실행 결과. <see cref="OrderedVideoUrls"/> 는 완료 세그먼트를 원래 순서로 정렬한 URL 목록(그대로 <see cref="WanRenderClient.ConcatAsync"/> 에 넘길 수 있다).
/// </summary>
public sealed record GridRunResult(
    bool Ok, int Total, int Completed, int Failed, IReadOnlyList<string> OrderedVideoUrls, string Message);

/// <summary>
/// [PromptFlow Studio v6.0.1] 원격 GPU 워커 분산 렌더링 노드 그리드(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 대량 양산·세그먼트 생성 큐를 등록된 N개 워커 노드로 <b>최소 부하 우선</b> 분산 배정하고, 각 노드의 여유 슬롯이 빌 때마다 다음 세그먼트를 흘려보낸다(워크-풀).
///   ▸ <b>페일오버</b>: 노드 예외/단선(<see cref="SegmentRenderResult.NodeFault"/>) 시 해당 노드를 Degraded/Offline 로 격리하고
///     세그먼트를 <b>다른</b> 정상 노드로 즉시 재배정한다(<see cref="RenderSegment.TriedNodeIds"/> 로 동일 노드 회피 · 상한 <see cref="MaxSegmentAttempts"/>).
///   ▸ <b>하트비트</b>: <see cref="System.Threading.Timer"/> 로 주기 프로브 → Offline 노드가 복구되면 재승격(자가 치유). Timer/CTS 는 <see cref="Dispose"/> 에서 해제.
///   ▸ Graceful: 노드가 하나도 없거나 모두 죽으면 남은 세그먼트를 Failed 로 마감하되 이미 완료한 세그먼트는 보존해 부분 결과를 반환한다.
///   ⚠ <see cref="OnChange"/> 는 백그라운드(스케줄러/하트비트) 스레드에서 올 수 있다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링할 것.
/// </summary>
public sealed class DistributedRenderGrid : IDisposable
{
    /// <summary>세그먼트당 최대 배정 시도(원 배정 + 페일오버 재배정 합계).</summary>
    public const int MaxSegmentAttempts = 4;
    /// <summary>이 횟수만큼 연속 실패하면 노드를 Offline 격리(하트비트 복구 전까지 배정 제외).</summary>
    private const int OfflineFailureThreshold = 2;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly NodeWorkerService _worker;
    private readonly object _gate = new();
    private readonly List<RenderWorkerNode> _nodes = new();
    private readonly System.Threading.Timer _heartbeat;
    private int _heartbeatBusy;         // 하트비트 재진입 방지(0/1 · Interlocked)
    private CancellationTokenSource? _runCts;

    public DistributedRenderGrid(NodeWorkerService worker)
    {
        _worker = worker;
        // 하트비트 타이머 — 등록된 노드를 주기 프로브해 건강 상태를 갱신(자가 치유). 노드가 없으면 즉시 반환.
        _heartbeat = new System.Threading.Timer(_ => _ = HeartbeatTickAsync(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;

    // ── 상태(HUD) ──
    public bool IsRunning { get; private set; }
    public string? StatusMessage { get; private set; }
    /// <summary>최근 분산 진행 스냅샷(HUD 프로그레스 바용) — 실행 중 <see cref="ReportProgress"/> 가 갱신. 미실행/미시작 시 null.</summary>
    public GridProgress? LastProgress { get; private set; }

    /// <summary>등록된 노드 스냅샷(렌더용 · 스레드 안전 복사본).</summary>
    public IReadOnlyList<RenderWorkerNode> Nodes { get { lock (_gate) return _nodes.ToList(); } }
    /// <summary>지금 배정 가능한(Online/Degraded) 노드 수.</summary>
    public int UsableNodeCount { get { lock (_gate) return _nodes.Count(n => n.IsUsable); } }
    /// <summary>v8.1.1: 전 노드 InFlight 합(핫패스 무할당 — Nodes ToList/LINQ 클로저 회피).</summary>
    public int TotalInFlight { get { lock (_gate) { var s = 0; foreach (var n in _nodes) s += n.InFlight; return s; } } }

    private void Notify() => OnChange?.Invoke();

    // ── 노드 레지스트리 ───────────────────────────────────────────────────

    /// <summary>워커 노드 등록(중복 BaseUrl 은 무시). 등록 즉시 다음 하트비트에서 탐색된다.</summary>
    public RenderWorkerNode RegisterNode(string name, string baseUrl, int maxConcurrency = 1)
    {
        var node = new RenderWorkerNode
        {
            Name = string.IsNullOrWhiteSpace(name) ? baseUrl : name.Trim(),
            BaseUrl = (baseUrl ?? "").Trim(),
            MaxConcurrency = Math.Max(1, maxConcurrency),
        };
        lock (_gate)
        {
            if (_nodes.Any(n => string.Equals(n.BaseUrl, node.BaseUrl, StringComparison.OrdinalIgnoreCase)))
                return _nodes.First(n => string.Equals(n.BaseUrl, node.BaseUrl, StringComparison.OrdinalIgnoreCase));
            _nodes.Add(node);
        }
        Notify();
        return node;
    }

    /// <summary>노드 제거(진행 중이 아닐 때 권장). 진행 중 배정은 다음 완료 시 자연 반영된다.</summary>
    public void RemoveNode(string id)
    {
        lock (_gate) _nodes.RemoveAll(n => n.Id == id);
        Notify();
    }

    /// <summary>노드 격리 해제 — 연속 실패 카운터를 리셋하고 Unknown 으로 되돌려 다음 하트비트에서 재탐색시킨다(수동 복구).</summary>
    public void ResetNode(string id)
    {
        lock (_gate)
        {
            var n = _nodes.FirstOrDefault(x => x.Id == id);
            if (n is null) return;
            n.ConsecutiveFailures = 0; n.LastError = null; n.Health = WorkerNodeHealth.Unknown;
        }
        Notify();
    }

    /// <summary>단일 노드를 즉시 프로브해 건강 상태를 갱신한다(UI 등록 직후·수동 헬스체크). 반환=도달 여부.</summary>
    public async Task<bool> ProbeNodeAsync(string id, CancellationToken ct = default)
    {
        RenderWorkerNode? node;
        lock (_gate) node = _nodes.FirstOrDefault(n => n.Id == id);
        if (node is null) return false;
        var (reachable, is5xx) = await _worker.ProbeAsync(node, ct).ConfigureAwait(false);
        lock (_gate) ApplyProbe(node, reachable, is5xx);
        Notify();
        return reachable;
    }

    // ── 분산 실행 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 세그먼트 요청 목록을 등록된 노드들로 분산 렌더한다(워크-풀 + 페일오버). 완료 URL 을 원래 순서로 정렬해 반환한다.
    ///   이미 실행 중이거나 요청이 비면 무시. 노드가 없으면 전량 실패로 마감하되 예외는 던지지 않는다(Graceful).
    /// </summary>
    public async Task<GridRunResult> DispatchAsync(
        IReadOnlyList<WanRenderRequest> requests, IProgress<GridProgress>? progress = null, CancellationToken ct = default)
    {
        if (IsRunning) return new(false, 0, 0, 0, Array.Empty<string>(), "이미 분산 렌더가 진행 중입니다.");
        var reqs = (requests ?? Array.Empty<WanRenderRequest>()).Where(r => r is not null).ToList();
        if (reqs.Count == 0) return new(false, 0, 0, 0, Array.Empty<string>(), "분산할 세그먼트가 없습니다.");
        if (UsableNodeCount == 0 && Nodes.Count == 0)
            return new(false, reqs.Count, 0, reqs.Count, Array.Empty<string>(), "등록된 워커 노드가 없습니다 — 먼저 GPU 워커를 등록하세요.");

        var segments = reqs.Select((r, i) => new RenderSegment { Index = i, Request = r }).ToList();

        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _runCts.Token;
        IsRunning = true;
        StatusMessage = $"분산 렌더 시작 — 세그먼트 {segments.Count}개 · 노드 {UsableNodeCount}대";
        Notify();

        // 시작 전 1회 프로브 — Unknown 노드의 건강을 확정해 첫 배정 품질을 높인다.
        await ProbeAllAsync(token).ConfigureAwait(false);
        ReportProgress(segments, progress);

        // 진행 중 배정: (세그먼트, 노드, 태스크). Task.WhenAny 로 하나 끝날 때마다 슬롯을 회수하고 재충전.
        var running = new List<(RenderSegment Seg, RenderWorkerNode Node, Task<SegmentRenderResult> Task)>();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();

                // ① 여유 슬롯을 가진 노드에 대기 세그먼트를 최대한 채운다.
                while (TryPickAssignment(segments, out var seg, out var node))
                {
                    seg!.State = SegmentState.Dispatched;
                    seg.Attempts++;
                    seg.AssignedNodeId = node!.Id;
                    seg.AssignedNodeName = node.Name;
                    seg.TriedNodeIds.Add(node.Id);
                    lock (_gate) node.InFlight++;
                    running.Add((seg, node, _worker.RenderSegmentAsync(node, seg.Request, token)));
                }
                ReportProgress(segments, progress);
                Notify();

                if (running.Count == 0)
                {
                    // 대기 세그먼트가 남았는데 아무것도 배정/진행 중이 아니다 = 가용 노드 소진 → 남은 세그먼트 실패 마감.
                    if (segments.Any(s => s.State == SegmentState.Queued))
                        foreach (var s in segments.Where(s => s.State == SegmentState.Queued))
                        {
                            s.State = SegmentState.Failed;
                            s.Error = "가용 워커 노드 없음(전 노드 장애/소진).";
                        }
                    break;   // 모든 세그먼트 종료
                }

                // ② 하나가 끝나길 대기 → 슬롯 회수.
                var done = await Task.WhenAny(running.Select(r => r.Task)).ConfigureAwait(false);
                var entry = running.First(r => r.Task == done);
                running.Remove(entry);
                var (finSeg, finNode, _) = entry;
                lock (_gate) { if (finNode.InFlight > 0) finNode.InFlight--; }

                var result = await done.ConfigureAwait(false);   // RenderSegmentAsync 는 예외를 삼키고 결과로 환원
                ApplyResult(finSeg, finNode, result, segments);
                ReportProgress(segments, progress);
                Notify();
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "분산 렌더 취소됨 — 완료 세그먼트는 보존.";
            // 진행 중이던 배정은 노드 취소 결과로 흘러들어오나, 취소 경로에서는 대기하지 않고 부분 결과로 마감.
        }
        catch (Exception ex)
        {
            Program.Log("DistributedRenderGrid.Dispatch", ex);
            StatusMessage = "분산 렌더 오류: " + ex.Message;
        }
        finally
        {
            IsRunning = false;
            Notify();
        }

        var completed = segments.Where(s => s.State == SegmentState.Completed).OrderBy(s => s.Index).ToList();
        var failed = segments.Count(s => s.State == SegmentState.Failed);
        var urls = completed.Select(s => s.VideoUrl!).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        var ok = completed.Count > 0 && failed == 0;
        StatusMessage = $"분산 렌더 종료 — 성공 {completed.Count} · 실패 {failed} / 총 {segments.Count}";
        Notify();
        return new(ok, segments.Count, completed.Count, failed, urls, StatusMessage);
    }

    /// <summary>진행 중 분산 렌더 취소(폴링·재배정 중단). 완료 세그먼트는 결과에 남는다.</summary>
    public void Cancel() => _runCts?.Cancel();

    // ── 스케줄링 핵심 ─────────────────────────────────────────────────────

    /// <summary>여유 슬롯 노드 ↔ 그 노드를 아직 시도하지 않은 대기 세그먼트를 1쌍 고른다. 최소 부하(그다음 최소 지연) 우선.</summary>
    private bool TryPickAssignment(List<RenderSegment> segments, out RenderSegment? seg, out RenderWorkerNode? node)
    {
        seg = null; node = null;
        lock (_gate)
        {
            var freeNodes = _nodes.Where(n => n.HasFreeSlot)
                                  .OrderBy(n => n.InFlight)
                                  .ThenBy(n => n.AvgLatencyMs <= 0 ? double.MaxValue : n.AvgLatencyMs)
                                  .ToList();
            foreach (var n in freeNodes)
            {
                var candidate = segments.FirstOrDefault(s => s.State == SegmentState.Queued && !s.TriedNodeIds.Contains(n.Id));
                if (candidate is not null) { seg = candidate; node = n; return true; }
            }
        }
        return false;
    }

    /// <summary>노드 렌더 결과를 세그먼트/노드에 반영하고, 노드 장애면 페일오버(재배정 or 실패 마감)한다.</summary>
    private void ApplyResult(RenderSegment seg, RenderWorkerNode node, SegmentRenderResult result, List<RenderSegment> segments)
    {
        if (result.Ok && !string.IsNullOrWhiteSpace(result.VideoUrl))
        {
            seg.State = SegmentState.Completed;
            seg.VideoUrl = result.VideoUrl;
            seg.Error = null;
            lock (_gate)
            {
                node.CompletedCount++;
                node.ConsecutiveFailures = 0;
                node.Health = WorkerNodeHealth.Online;
                node.LastError = null;
                NodeWorkerService.UpdateLatency(node, result.LatencyMs);
            }
            return;
        }

        if (!result.NodeFault)
        {
            // 요청 자체 거부(4xx/취소) — 다른 노드로 옮겨도 실패 → 재배정하지 않고 마감.
            seg.State = SegmentState.Failed;
            seg.Error = result.Message;
            return;
        }

        // ── 노드 장애 → 격리 + 페일오버 ──
        lock (_gate)
        {
            node.FailedCount++;
            node.ConsecutiveFailures++;
            node.LastError = result.Message;
            node.Health = node.ConsecutiveFailures >= OfflineFailureThreshold
                ? WorkerNodeHealth.Offline    // 반복 장애 → 배정 제외(하트비트가 복구 시 재승격)
                : WorkerNodeHealth.Degraded;  // 1회 장애 → 후순위로 강등하되 재시도 여지 유지
        }

        // 아직 시도하지 않은 '사용 가능' 노드가 있고 시도 상한 미만이면 재배정, 아니면 실패 마감.
        var canFailover = seg.Attempts < MaxSegmentAttempts && HasUntriedUsableNode(seg);
        if (canFailover)
        {
            seg.State = SegmentState.Queued;   // 재대기 → 다음 루프에서 다른 노드로 배정
            seg.AssignedNodeId = null;
            seg.AssignedNodeName = null;
            seg.Error = $"[페일오버] {node.Name} 장애 → 재배정({seg.Attempts}/{MaxSegmentAttempts})";
        }
        else
        {
            seg.State = SegmentState.Failed;
            seg.Error = seg.Attempts >= MaxSegmentAttempts
                ? $"페일오버 상한 소진({MaxSegmentAttempts}회) — 마지막 오류: {result.Message}"
                : $"재배정할 정상 노드 없음 — 마지막 오류: {result.Message}";
        }
    }

    /// <summary>이 세그먼트가 아직 시도하지 않은 '사용 가능' 노드가 존재하는가(페일오버 가능 판정).</summary>
    private bool HasUntriedUsableNode(RenderSegment seg)
    {
        lock (_gate) return _nodes.Any(n => n.IsUsable && !seg.TriedNodeIds.Contains(n.Id));
    }

    // ── 하트비트 / 프로브 ─────────────────────────────────────────────────

    private async Task HeartbeatTickAsync()
    {
        if (Interlocked.Exchange(ref _heartbeatBusy, 1) == 1) return;   // 이전 틱이 아직 도는 중이면 스킵
        try { await ProbeAllAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("DistributedRenderGrid.Heartbeat", ex); }
        finally { Interlocked.Exchange(ref _heartbeatBusy, 0); }
    }

    /// <summary>등록된 전 노드를 병렬 프로브하고 건강 상태를 갱신한다(변화 시 방송).</summary>
    private async Task ProbeAllAsync(CancellationToken ct)
    {
        List<RenderWorkerNode> snapshot;
        lock (_gate) snapshot = _nodes.ToList();
        if (snapshot.Count == 0) return;

        var probes = snapshot.Select(async n =>
        {
            var (reachable, is5xx) = await _worker.ProbeAsync(n, ct).ConfigureAwait(false);
            lock (_gate) ApplyProbe(n, reachable, is5xx);
        });
        await Task.WhenAll(probes).ConfigureAwait(false);
        Notify();
    }

    /// <summary>프로브 결과를 노드 건강에 반영(락 아래 호출).
    ///   미도달=Offline. 도달 시 5xx 또는 실패 이력이 있으면 Degraded(후순위·재시도 여지), 무결하면 Online 으로 승격(자가 치유).</summary>
    private static void ApplyProbe(RenderWorkerNode node, bool reachable, bool is5xx)
    {
        node.LastHeartbeatAt = DateTimeOffset.Now;
        if (!reachable) { node.Health = WorkerNodeHealth.Offline; return; }
        node.Health = (is5xx || node.ConsecutiveFailures > 0) ? WorkerNodeHealth.Degraded : WorkerNodeHealth.Online;
    }

    private void ReportProgress(List<RenderSegment> segments, IProgress<GridProgress>? progress)
    {
        var total = segments.Count;
        var completed = segments.Count(s => s.State == SegmentState.Completed);
        var failed = segments.Count(s => s.State == SegmentState.Failed);
        var dispatched = segments.Count(s => s.State == SegmentState.Dispatched);
        var queued = segments.Count(s => s.State == SegmentState.Queued);
        var percent = total == 0 ? 0 : (int)Math.Round((completed + failed) * 100.0 / total);
        var snap = new GridProgress(total, completed, failed, dispatched, queued, percent);
        LastProgress = snap;      // HUD 프로그레스 바(OnChange 로 실시간 반영)
        progress?.Report(snap);
    }

    public void Dispose()
    {
        try { _heartbeat.Dispose(); } catch { /* 무시 */ }
        try { _runCts?.Cancel(); _runCts?.Dispose(); } catch { /* 무시 */ }
    }
}
