using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>
/// v10.5.0-F2 DirectML/ONNX 인프로세스 폴백 커넥터 — ComfyUI 원격 서버가 미가동일 때 <see cref="WanBackendFactory"/>가
/// 자동 전환하는 로컬 실행 경로. <see cref="AdaptiveHardwareProfiler"/>(v10.1.2)로 사전 다운스케일 계획을 확정하고,
/// <see cref="LivePipelineAutoTuner"/>(v10.2.2)로 타일마다 실측 소요시간을 통보해 <b>같은 작업 실행 도중에도</b>
/// 양자화를 실시간 보정한다 — <see cref="WanPipelineOrchestrator"/>의 검증된 타일 루프와 동일한 계획/보정 primitive
/// 를 재사용해(신규 병행 구현 금지) <see cref="IWanBackendConnector"/> 계약으로 노출한다.
///   ⚠ 실 ONNX Runtime/DirectML 텐서 커널은 결선점(스텁 · <see cref="WebGpuInferenceEngine.RunNativeTensorInferenceAsync"/>
///   와 동일 계열) — 여기서는 타일 단위 계획/타이밍 시뮬레이션만 담당. <see cref="AdaptiveHardwareProfiler"/> 의존
///   (Process 기반 VRAM 탐지) 때문에 데스크톱 전용 — Mobile DI 미등록.
/// </summary>
public sealed class DirectMlWanConnector : IWanBackendConnector
{
    private sealed class JobState
    {
        public required WanBackendJobRequest Request;
        public readonly CancellationTokenSource Cts = new();
    }

    private readonly AlibabaWanLowSpecEngine _engine;
    private readonly AdaptiveHardwareProfiler _profiler;
    private readonly LivePipelineAutoTuner _tuner;
    private readonly ConcurrentDictionary<string, JobState> _jobs = new(StringComparer.Ordinal);

    public WanBackendKind Kind => WanBackendKind.DirectMlInProcess;
    public string? LastAction { get; private set; }

    public DirectMlWanConnector(AlibabaWanLowSpecEngine engine, AdaptiveHardwareProfiler profiler, LivePipelineAutoTuner tuner)
    {
        _engine = engine;
        _profiler = profiler;
        _tuner = tuner;
    }

    /// <summary>인프로세스 경로는 로컬 CPU/GPU 만 있으면 항상 가용 — 팩토리의 최종 무조건 폴백.</summary>
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<WanBackendSubmitResult> SubmitJobAsync(WanBackendJobRequest request, CancellationToken ct = default)
    {
        _jobs[request.JobId] = new JobState { Request = request };
        LastAction = $"작업 {request.JobId} 인프로세스 접수(스트리밍 시작 시 실행)";
        return Task.FromResult(new WanBackendSubmitResult(true, request.JobId, Kind, "인프로세스 접수 완료"));
    }

    public async IAsyncEnumerable<WanBackendProgress> StreamProgressAsync(string jobId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) yield break;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, job.Cts.Token);
        var lct = linked.Token;

        yield return new WanBackendProgress(jobId, WanBackendStage.Loading, 2, "하드웨어 프로파일링 중…(AdaptiveHardwareProfiler)");

        WanDownscaleResult? down = null;
        string? profileError = null;
        try { down = await _profiler.AutoDownscaleAsync(job.Request.Spec, lct).ConfigureAwait(false); }
        catch (OperationCanceledException) { profileError = "취소됨"; }

        if (profileError is not null) { yield return new WanBackendProgress(jobId, WanBackendStage.Cancelled, 0, profileError); yield break; }
        if (down is null || !down.Feasible)
        {
            yield return new WanBackendProgress(jobId, WanBackendStage.Failed, 0, $"다운스케일 바닥에도 예산 초과 — {down?.Plan.Note}");
            yield break;
        }

        yield return new WanBackendProgress(jobId, WanBackendStage.Sampling, 15, $"계획 확정({down.StepsApplied}단계 다운스케일) · {down.Plan.Quant}");

        var tiling = down.Plan.Tiling;
        var liveQuant = down.Plan.Quant;   // 사전 계획 양자화로 시작 — 이후 타일마다 LivePipelineAutoTuner 가 실시간 보정
        for (var i = 0; i < tiling.TileCount; i++)
        {
            if (lct.IsCancellationRequested) { yield return new WanBackendProgress(jobId, WanBackendStage.Cancelled, 0, "취소됨"); yield break; }

            var tileMs = Math.Max(5, 80 * AlibabaWanLowSpecEngine.QuantFactor(liveQuant));   // WanPipelineOrchestrator 와 동일 상대 모델
            var sw = Stopwatch.StartNew();
            string? delayError = null;
            try { await Task.Delay((int)tileMs, lct).ConfigureAwait(false); }
            catch (OperationCanceledException) { delayError = "취소됨"; }
            sw.Stop();
            if (delayError is not null) { yield return new WanBackendProgress(jobId, WanBackendStage.Cancelled, 0, delayError); yield break; }

            liveQuant = _tuner.ObserveFrame(sw.Elapsed.TotalMilliseconds);   // 실측 통보 → 다음 타일 양자화 즉시 반영
            var pct = 15 + (int)((i + 1) / (double)tiling.TileCount * 80);
            yield return new WanBackendProgress(jobId, WanBackendStage.Decoding, pct, $"타일 디코드 {i + 1}/{tiling.TileCount} · {liveQuant}");
        }

        yield return new WanBackendProgress(jobId, WanBackendStage.Completed, 100,
            $"완료 · {down.FinalSpec.Width}x{down.FinalSpec.Height}@{down.FinalSpec.Frames}f 계획={down.Plan.Quant}→라이브={liveQuant}");
        _jobs.TryRemove(jobId, out _);
        LastAction = $"작업 {jobId} 인프로세스 완료";
    }

    public Task CancelJobAsync(string jobId, CancellationToken ct = default)
    {
        if (_jobs.TryGetValue(jobId, out var job)) { job.Cts.Cancel(); LastAction = $"작업 {jobId} 취소 요청"; }
        return Task.CompletedTask;
    }
}
