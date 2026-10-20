using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;   // DistributedRenderGrid · WanRenderClient · WanPromptBuild · WanRenderRequest

namespace PromptFlowStudio.Services.Inference;

/// <summary>세그먼트 배정 티어 — 로컬 경량 추론 / 원격 대형 렌더.</summary>
public enum RenderTier { LocalLight, RemoteHeavy }

/// <summary>하이브리드 배정 입력 — 프롬프트 + 대형(고해상) 여부.</summary>
public sealed record HybridSegment(string Prompt, bool HighRes = false);

/// <summary>하이브리드 스케줄 결과 — 로컬/원격 분배 수 + 로컬 즉시 프레임 URL.</summary>
public sealed record HybridPlanResult(
    bool Ok, int Total, int LocalCount, int RemoteCount, IReadOnlyList<string> LocalFrameUrls, string Message);

/// <summary>
/// [PromptFlow Studio v7.0.0] 로컬-클라우드 하이브리드 메시 오케스트레이션 스케줄러(관찰 가능 싱글턴 · 순수 BCL).
///   ▸ 경량 세그먼트(저해상·프리뷰)는 <see cref="WebGpuInferenceEngine"/> 로컬 추론으로 <b>0.5초</b> 즉시 생성,
///     고해상 Wan 3.0 대형 컷은 v6.0.1 <see cref="DistributedRenderGrid"/> 원격 워커로 분산 배정(페일오버 포함).
///   ▸ 원격 워커가 없으면 대형 컷도 로컬로 폴백(Graceful) → 어떤 환경에서도 결과를 낸다.
///   ▸ 로컬/원격 배정 카운터를 노출해 <see cref="TelemetryAggregator"/>·글로벌 관제 HUD 가 합산 집계한다.
///   ⚠ <see cref="OnChange"/> 는 임의 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class HybridNodeMeshScheduler
{
    private readonly WebGpuInferenceEngine _local;
    private readonly DistributedRenderGrid _grid;
    private long _localDispatched, _remoteDispatched, _runs;

    public HybridNodeMeshScheduler(WebGpuInferenceEngine local, DistributedRenderGrid grid)
    {
        _local = local;
        _grid = grid;
    }

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    public long LocalDispatched => Interlocked.Read(ref _localDispatched);
    public long RemoteDispatched => Interlocked.Read(ref _remoteDispatched);
    public long Runs => Interlocked.Read(ref _runs);
    public string LocalBackendLabel => _local.BackendLabel;
    public int RemoteUsableNodes => _grid.UsableNodeCount;

    /// <summary>
    /// 세그먼트 목록을 로컬/원격으로 스마트 배정한다. 고해상+가용노드=원격, 그 외=로컬. 원격 노드 없으면 전부 로컬 폴백.
    /// </summary>
    public async Task<HybridPlanResult> ScheduleAsync(IReadOnlyList<HybridSegment> segments, CancellationToken ct = default)
    {
        var segs = (segments ?? Array.Empty<HybridSegment>()).Where(s => s is not null).ToList();
        if (segs.Count == 0) return new(false, 0, 0, 0, Array.Empty<string>(), "배정할 세그먼트가 없습니다.");
        Interlocked.Increment(ref _runs);

        var remoteReqs = new List<WanRenderRequest>();
        var localFrames = new List<string>();
        var localN = 0; var remoteN = 0;
        var canRemote = _grid.UsableNodeCount > 0;

        foreach (var s in segs)
        {
            if (ct.IsCancellationRequested) break;
            var tier = Classify(s, canRemote);
            if (tier == RenderTier.RemoteHeavy)
            {
                remoteReqs.Add(WanRenderClient.BuildRequest(new WanPromptBuild(s.Prompt, 30, Array.Empty<string>(), null), null));
                remoteN++;
            }
            else
            {
                var r = await _local.GenerateStillAsync(s.Prompt, ct: ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(r.FrameUrl)) localFrames.Add(r.FrameUrl!);
                localN++;
            }
        }

        Interlocked.Add(ref _localDispatched, localN);
        Interlocked.Add(ref _remoteDispatched, remoteN);
        Notify();

        // 원격 배치는 그리드로 이관(수락 후 비동기 · 그리드가 페일오버·자산화 담당).
        if (remoteReqs.Count > 0)
            _ = _grid.DispatchAsync(remoteReqs, null, ct);

        var msg = $"하이브리드 배정 — 로컬 {localN} · 원격 {remoteN}" + (canRemote ? "" : "(원격 노드 없음 → 전부 로컬 폴백)");
        return new(true, segs.Count, localN, remoteN, localFrames, msg);
    }

    /// <summary>배정 판정 — 고해상 요청 + 가용 원격 노드가 있으면 원격, 아니면 로컬.</summary>
    public static RenderTier Classify(HybridSegment seg, bool canRemote)
        => seg.HighRes && canRemote ? RenderTier.RemoteHeavy : RenderTier.LocalLight;
}
