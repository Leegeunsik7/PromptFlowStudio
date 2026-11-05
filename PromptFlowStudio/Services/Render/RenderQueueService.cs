using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Telemetry;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// [PromptFlowStudio] 렌더 큐 + 렌더 파이프라인 워커.
///   ▸ <see cref="IRenderExecutionEngine"/> 가 가용하면 실물 추론(ComfyUI/클라우드) 호출, 미설정·오프라인이면 내장 시뮬레이션 폴백.
///   ▸ Enqueue/Retry/Resume 시 워커가 없으면 1개만 기동(<c>_workerRunning</c> 잠금 판정 — 소진 직전 적재분 유실 방지).
///   ▸ 프레임 틱마다 진행률 +<see cref="ProgressStep"/>% — 완료 시 Completed + CompletedAt.
///   ▸ 처리 중 작업 취소는 작업별 링크 CTS 로 즉시 중단 → Cancelled. 서비스 Dispose 시 처리 중 작업은 Queued 로 복귀.
///   ▸ VRAM 가드레일: 디스패치 직전 사용률 ≥ <see cref="VramPauseThreshold"/> 이면 새 작업을 꺼내지 않고 자동 일시정지(<see cref="IsVramPaused"/>) — 재개는 수동.
///   ▸ 디스크 가드레일: 디스패치 직전 출력 드라이브 잔여 &lt; <see cref="DiskPauseThresholdBytes"/> 면 자동 일시정지(<see cref="IsDiskPaused"/>) — 재개는 수동.
///   ▸ OnChange 구독자 예외는 격리.
/// </summary>
public sealed class RenderQueueService : IRenderQueueService
{
    public const int ProgressStep = 5;
    /// <summary>VRAM 사용률 자동 일시정지 임계(0~1).</summary>
    public const double VramPauseThreshold = 0.95;
    /// <summary>출력 드라이브 잔여 용량 자동 일시정지 임계(5GB).</summary>
    public const long DiskPauseThresholdBytes = 5L * 1024 * 1024 * 1024;
    private static readonly TimeSpan TelemetryInterval = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly TimeSpan _frameDelay;
    private readonly bool _autoStart;
    private readonly IRenderExecutionEngine? _engine;
    private readonly Func<double?>? _vramProbe;
    private readonly Func<string?, long?>? _diskProbe;
    private readonly SystemHardwareTelemetryMonitor? _telemetry;
    private readonly CancellationTokenSource _shutdown = new();
    private TaskCompletionSource _resume = NewResumeSignal();
    private CancellationTokenSource? _activeCts;
    private bool _paused;
    private bool _vramPaused;
    private bool _diskPaused;
    private bool _workerRunning;
    private bool _disposed;
    private FFmpegVideoStitchService? _stitcher;
    private readonly HashSet<string> _stitchedGroups = new(StringComparer.Ordinal);

    public RenderQueueService() : this(TimeSpan.FromMilliseconds(120), autoStart: true) { }

    public RenderQueueService(IRenderExecutionEngine engine) : this(TimeSpan.FromMilliseconds(120), autoStart: true, engine) { }

    /// <summary>DI 기본 — 워커 가동 중 하드웨어 텔레메트리 폴링(참조 카운트) · 최신 샘플 VRAM 사용률로 가드.</summary>
    public RenderQueueService(IRenderExecutionEngine engine, SystemHardwareTelemetryMonitor telemetry)
        : this(TimeSpan.FromMilliseconds(120), autoStart: true, engine, telemetry is null ? null : () => telemetry.Latest?.VramUsedRatio, ProbeFreeBytes)
    {
        _telemetry = telemetry;
        _stitcher = new FFmpegVideoStitchService();
    }

    /// <summary>멀티 샷 후처리기(마지막 프레임 I2V 앵커 추출 · 그룹 합본) — null 이면 체이닝 후처리 생략(DI 기본 ctor 는 ffmpeg 실물).</summary>
    public FFmpegVideoStitchService? Stitcher { get => _stitcher; init => _stitcher = value; }

    /// <summary>🛡️ 장중 트레이딩 세이프가드 — 설정 시 정규장 동안 렌더 실행이 인코딩 슬롯(1개)을 점유. null = 제한 없음.</summary>
    public PromptFlowStudio.Services.ProcessBridge.TradingSafetyGuardService? TradingGuard { get; set; }

    /// <summary>🛡️ 장중 부하 제한 활성 여부(인디케이터용).</summary>
    public bool IsTradingThrottled => TradingGuard?.IsThrottling == true;

    /// <summary>멀티 샷 그룹 합본 완료 알림(그룹 키 · 결과) — 워커 스레드에서 발생, 구독자 예외 격리.</summary>
    public event Action<string, VideoStitchResult>? OnDurationStitched;

    /// <param name="vramProbe">VRAM 사용률(0~1) 조회 — null 반환(GPU 미탐지)이면 가드 생략.</param>
    /// <param name="diskProbe">출력 경로(null = 임시 폴더) 드라이브 잔여 바이트 조회 — null 반환(판정 불가)이면 가드 생략.</param>
    public RenderQueueService(TimeSpan frameDelay, bool autoStart, IRenderExecutionEngine? engine = null, Func<double?>? vramProbe = null, Func<string?, long?>? diskProbe = null)
    {
        _frameDelay = frameDelay < TimeSpan.Zero ? TimeSpan.Zero : frameDelay;
        _autoStart = autoStart;
        _engine = engine;
        _vramProbe = vramProbe;
        _diskProbe = diskProbe;
        _resume.TrySetResult();
    }

    public ObservableCollection<BatchRenderJob> Queue { get; } = new();
    public event Action? OnChange;

    public bool IsPaused { get { lock (_gate) return _paused; } }
    public bool IsVramPaused { get { lock (_gate) return _vramPaused; } }
    public bool IsDiskPaused { get { lock (_gate) return _diskPaused; } }

    private BatchRenderJob? _activeJob;
    public BatchRenderJob? ActiveJob { get { lock (_gate) return _activeJob; } }

    public int Enqueue(IEnumerable<BatchRenderJob> jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        var added = 0;
        lock (_gate)
        {
            foreach (var job in jobs)
            {
                if (job is null || IndexOf(job.JobId) >= 0) continue;   // alreadyRegistered
                job.Status = BatchJobStatus.Queued;
                job.ProgressPercent = 0;
                job.CompletedAt = null;
                ResetRunInfo(job);
                Queue.Add(job);
                added++;
            }
        }
        if (added > 0) { Raise(); EnsureWorker(); }
        return added;
    }

    public IReadOnlyList<BatchRenderJob> EnqueueJob(BatchRenderJob job, IReadOnlyList<string?>? subPrompts = null, string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobs = MultiDurationPlanner.Expand(job, subPrompts, projectName);
        return Enqueue(jobs) == jobs.Length ? jobs : Array.Empty<BatchRenderJob>();
    }

    public bool CancelJob(Guid jobId)
    {
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            var i = IndexOf(jobId);
            if (i < 0) return false;
            var job = Queue[i];
            if (job.Status == BatchJobStatus.Processing && ReferenceEquals(job, _activeJob))
            {
                cts = _activeCts;
                if (cts is null || cts.IsCancellationRequested) return false;   // 이미 취소 진행 중 — 중복 통지 차단
                job.UserCancelRequested = true;   // 사용자 의도 취소 → 저널 UserCanceled(좀비 회수 차단)
            }
            else if (job.Status is BatchJobStatus.Queued or BatchJobStatus.Pending) job.Status = BatchJobStatus.Cancelled;
            else return false;
        }
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        Raise();
        return true;
    }

    /// <inheritdoc/>
    public int CancelJobGroup(string stitchGroupId)
    {
        if (string.IsNullOrEmpty(stitchGroupId)) return 0;
        CancellationTokenSource? cts = null;
        var n = 0;
        lock (_gate)
        {
            // 단일 잠금 하 — 대기 컷을 먼저 Cancelled 로 확정해 워커가 다음 컷을 집어가지 못하게 한 뒤 진행 컷 취소
            for (var i = 0; i < Queue.Count; i++)
            {
                var job = Queue[i];
                if (!string.Equals(job.StitchGroupId, stitchGroupId, StringComparison.Ordinal)) continue;
                if (job.Status is BatchJobStatus.Queued or BatchJobStatus.Pending) { job.Status = BatchJobStatus.Cancelled; n++; }
                else if (job.Status == BatchJobStatus.Processing && ReferenceEquals(job, _activeJob) && _activeCts is { IsCancellationRequested: false } a) { job.UserCancelRequested = true; cts = a; n++; }
            }
        }
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }   // 엔진 프로세스 킬 + ComfyUI /interrupt 전파
        if (n > 0) Raise();
        return n;
    }

    public bool RetryJob(Guid jobId)
    {
        lock (_gate)
        {
            var i = IndexOf(jobId);
            if (i < 0) return false;
            var job = Queue[i];
            if (job.Status is not (BatchJobStatus.Failed or BatchJobStatus.Cancelled)) return false;
            job.Status = BatchJobStatus.Queued;
            job.ProgressPercent = 0;
            job.CompletedAt = null;
            ResetRunInfo(job);
        }
        Raise();
        EnsureWorker();
        return true;
    }

    public int ClearCompleted()
    {
        var removed = 0;
        lock (_gate)
        {
            for (var i = Queue.Count - 1; i >= 0; i--)
            {
                if (Queue[i].Status != BatchJobStatus.Completed) continue;
                Queue.RemoveAt(i);
                removed++;
            }
        }
        if (removed > 0) Raise();
        return removed;
    }

    public void PauseQueue()
    {
        lock (_gate)
        {
            if (_paused) return;
            _paused = true;
            _resume = NewResumeSignal();
        }
        Raise();
    }

    public void ResumeQueue()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _paused = false;
            _vramPaused = false;
            _diskPaused = false;
            _resume.TrySetResult();
        }
        Raise();
        EnsureWorker();
    }

    public BatchRenderJob[] Snapshot()
    {
        lock (_gate)
        {
            var arr = new BatchRenderJob[Queue.Count];
            Queue.CopyTo(arr, 0);
            return arr;
        }
    }

    public async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await WaitIfPausedAsync(ct).ConfigureAwait(false);
            if (TryVramAutoPause()) continue;   // 위험 수준 — 다음 루프에서 재개 대기
            if (TryDiskAutoPause()) continue;   // 디스크 부족 — 동일

            BatchRenderJob? job = null;
            CancellationTokenSource jobCts;
            lock (_gate)
            {
                for (var i = 0; i < Queue.Count; i++)
                {
                    if (Queue[i].Status == BatchJobStatus.Queued) { job = Queue[i]; break; }
                }
                if (job is null) { _workerRunning = false; return; }
                job.Status = BatchJobStatus.Processing;
                job.ProgressPercent = 0;
                ResetRunInfo(job);
                jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _activeJob = job;
                _activeCts = jobCts;
            }
            Raise();

            var startedAt = DateTimeOffset.Now;
            try
            {
                var engine = _engine;
                var guard = TradingGuard;
                if (guard is not null && engine is { IsAvailable: true })   // 🛡️ 장중: 인코딩 슬롯 1개 공유(내보내기 ffmpeg 와 직렬화)
                    await guard.RunGatedAsync(async c => { await ExecuteOnEngineAsync(engine, job, c).ConfigureAwait(false); return true; }, jobCts.Token).ConfigureAwait(false);
                else if (engine is { IsAvailable: true }) await ExecuteOnEngineAsync(engine, job, jobCts.Token).ConfigureAwait(false);
                else await SimulateAsync(job, jobCts.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    job.ProgressPercent = 100;
                    job.Status = BatchJobStatus.Completed;
                    job.CompletedAt = DateTimeOffset.Now;
                }
                WriteRenderMeta(job, startedAt);
                await ChainDurationCutAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                lock (_gate) { job.Status = BatchJobStatus.Queued; job.ProgressPercent = 0; }   // 종료 — 재기동 시 재처리
            }
            catch (OperationCanceledException)
            {
                lock (_gate) job.Status = BatchJobStatus.Cancelled;
            }
            catch (Exception ex)
            {
                if (TryOomDownscaleRetry(job, ex.Message))
                    Program.Log("RenderQueue.OomRetry", new InvalidOperationException(OomRetryNotice + " → " + job.Resolution, ex));
                else
                {
                    // 🧯 결함 격리: 이 작업만 Failed+Quarantined 로 격리하고 루프는 다음 대기 작업으로 계속(전체 큐 중단 없음).
                    var reason = ClassifyQuarantineReason(ex);
                    lock (_gate) { job.Status = BatchJobStatus.Failed; job.ErrorMessage = ex.Message; job.IsQuarantined = true; job.QuarantineReason = reason; }
                    Program.Log("RenderQueue.Quarantine", new InvalidOperationException(reason, ex));
                }
            }
            finally
            {
                lock (_gate) { _activeJob = null; _activeCts = null; }
                jobCts.Dispose();
                Raise();
            }
        }
    }

    /// <summary>
    /// 멀티 샷 체이닝 후처리 — 다음 컷이 Queued 면 이 컷의 마지막 프레임을 추출해 다음 컷 I2V 앵커(<see cref="BatchRenderJob.ReferenceImagePath"/>)로 주입,
    /// 그룹 전 컷 Completed·로컬 산출물 실존 시 <c>{project}_10s.mp4</c>/<c>_15s.mp4</c> 합본(그룹당 1회). 실패는 격리(T2V 폴백 · 컷 상태 유지).
    /// </summary>
    internal async Task ChainDurationCutAsync(BatchRenderJob job, CancellationToken ct)
    {
        var stitcher = _stitcher;
        if (stitcher is null || !MultiDurationPlanner.IsMultiShot(job.DurationMode) || job.StitchGroupId is not { Length: > 0 } group) return;
        try
        {
            var snapshot = Snapshot();
            var output = FFmpegVideoStitchService.LocalOutputOf(job);
            var next = MultiDurationPlanner.FindNextCut(snapshot, job);
            if (output is not null && next is not null && next.Status == BatchJobStatus.Queued)
            {
                var png = MultiDurationPlanner.LastFramePathFor(output);
                if (await stitcher.ExtractLastFrameAsync(output, png, ct).ConfigureAwait(false))
                {
                    lock (_gate)
                    {
                        if (next.Status == BatchJobStatus.Queued)
                        {
                            next.ReferenceImagePath = png;
                            next.Metadata = MultiDurationPlanner.WithMeta(next.Metadata, MultiDurationPlanner.MetaAnchorFrame, png);
                        }
                    }
                    Raise();
                }
            }

            if (!FFmpegVideoStitchService.TryCollectCompletedCuts(snapshot, group, out var cuts)
                || cuts.Length < MultiDurationPlanner.ShotCount(job.DurationMode)) return;
            lock (_gate) { if (!_stitchedGroups.Add(group)) return; }
            var dir = Path.GetDirectoryName(cuts[0].VideoPath) ?? Path.GetTempPath();
            var target = MultiDurationPlanner.BuildOutputPath(dir, MultiDurationPlanner.MetaOf(job, MultiDurationPlanner.MetaProject), job.DurationMode);
            var r = await stitcher.StitchCutsAsync(cuts, target, null, null, ct).ConfigureAwait(false);
            if (r.Ok && r.OutputPath is not null)
                lock (_gate) job.Metadata = MultiDurationPlanner.WithMeta(job.Metadata, r.IsVirtual ? MultiDurationPlanner.MetaStitchedVirtual : MultiDurationPlanner.MetaStitched, r.OutputPath);
            else
            {
                lock (_gate) _stitchedGroups.Remove(group);   // 재시도 허용
                Program.Log("RenderQueue.DurationStitch", new InvalidOperationException(r.Message));
            }
            try { OnDurationStitched?.Invoke(group, r); } catch (Exception ex) { Program.Log("RenderQueue.DurationStitch", ex); }
            Raise();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Program.Log("RenderQueue.DurationChain", ex); }
    }

    /// <summary>디스패치 직전 VRAM 점검 — 대기 작업이 있고 사용률 ≥ 임계면 큐 일시정지(Paused · VRAM Warning). 프로브 예외는 격리(통과).</summary>
    private bool TryVramAutoPause()
    {
        var probe = _vramProbe;
        if (probe is null) return false;
        double? usage;
        try { usage = probe(); }
        catch (Exception ex) { Program.Log("RenderQueue.VramProbe", ex); return false; }
        if (usage is not { } u || u < VramPauseThreshold) return false;
        lock (_gate)
        {
            if (_paused) return true;
            var hasQueued = false;
            for (var i = 0; i < Queue.Count; i++)
                if (Queue[i].Status == BatchJobStatus.Queued) { hasQueued = true; break; }
            if (!hasQueued) return false;
            _paused = true;
            _vramPaused = true;
            _resume = NewResumeSignal();
        }
        Raise();
        return true;
    }

    /// <summary>디스패치 직전 디스크 점검 — 다음 Queued 작업 출력 드라이브 잔여 &lt; 5GB 면 큐 일시정지(Paused · Disk Full Warning). 프로브 예외는 격리(통과).</summary>
    private bool TryDiskAutoPause()
    {
        var probe = _diskProbe;
        if (probe is null) return false;
        string? target = null;
        var hasQueued = false;
        lock (_gate)
        {
            if (_paused) return true;
            for (var i = 0; i < Queue.Count; i++)
                if (Queue[i].Status == BatchJobStatus.Queued) { hasQueued = true; target = Queue[i].OutputPath; break; }
        }
        if (!hasQueued) return false;
        long? free;
        try { free = probe(target); }
        catch (Exception ex) { Program.Log("RenderQueue.DiskProbe", ex); return false; }
        if (free is not { } f || f >= DiskPauseThresholdBytes) return false;
        lock (_gate)
        {
            if (_paused) return true;
            _paused = true;
            _diskPaused = true;
            _resume = NewResumeSignal();
        }
        Raise();
        return true;
    }

    /// <summary>경로(null/빈 값 = 임시 폴더) 드라이브의 사용 가능 잔여 바이트 — 판정 불가(UNC·권한·미준비)는 null.</summary>
    public static long? ProbeFreeBytes(string? path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? Path.GetTempPath() : path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// 임시 렌더 캐시 비우기 — 완료된 중간 조각만 삭제(완성 영상·갤러리 원본 보존):
    /// <paramref name="mediaRoot"/> 하위 <c>*.concat.txt · *.mux.mp4 · *.tmp</c>, %TEMP% 의 <c>pfs_tts_*</c> 폴더 · <c>pfs_disclaimer_*.txt</c>.
    /// 사용 중(잠김) 파일은 건너뜀. (삭제 파일 수, 확보 바이트) 반환.
    /// </summary>
    public static (int Files, long Bytes) CleanRenderCache(string? mediaRoot, string? tempRoot = null)
    {
        int files = 0; long bytes = 0;
        void Del(string f)
        {
            try { var len = new FileInfo(f).Length; File.Delete(f); files++; bytes += len; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        void Sweep(string dir, string pattern, SearchOption opt)
        {
            try { foreach (var f in Directory.EnumerateFiles(dir, pattern, opt)) Del(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("RenderQueue.CacheSweep", ex); }
        }
        if (!string.IsNullOrWhiteSpace(mediaRoot) && Directory.Exists(mediaRoot))
        {
            Sweep(mediaRoot, "*.concat.txt", SearchOption.AllDirectories);
            Sweep(mediaRoot, "*.mux.mp4", SearchOption.AllDirectories);
            Sweep(mediaRoot, "*.tmp", SearchOption.AllDirectories);
        }
        var temp = string.IsNullOrWhiteSpace(tempRoot) ? Path.GetTempPath() : tempRoot;
        if (Directory.Exists(temp))
        {
            Sweep(temp, "pfs_disclaimer_*.txt", SearchOption.TopDirectoryOnly);
            try
            {
                foreach (var d in Directory.EnumerateDirectories(temp, FFmpegVideoStitchService.TtsTempPrefix + "*", SearchOption.TopDirectoryOnly))
                {
                    Sweep(d, "*", SearchOption.AllDirectories);
                    try { Directory.Delete(d, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("RenderQueue.CacheSweep", ex); }
        }
        return (files, bytes);
    }

    /// <summary>내장 시뮬레이션 — 프레임 틱마다 +<see cref="ProgressStep"/>%.</summary>
    private async Task SimulateAsync(BatchRenderJob job, CancellationToken ct)
    {
        lock (_gate) job.EngineKind = RenderEngineKind.Mock;
        for (var p = ProgressStep; p <= 100; p += ProgressStep)
        {
            await WaitIfPausedAsync(ct).ConfigureAwait(false);
            await Task.Delay(_frameDelay, ct).ConfigureAwait(false);
            lock (_gate) job.ProgressPercent = p;
            Raise();
        }
        // Mock 실물 보장 — 어댑터가 샘플 생성기를 제공하면 testsrc 5초 MP4(+앰비언트 후처리)를 디스크에 생성. 실패는 기존 빈 완료 유지.
        if (_engine is IMockRenderOutputProvider mock)
        {
            string? path = null;
            try { path = await mock.RenderMockAsync(job, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("RenderQueue.MockOutput", ex); }
            ct.ThrowIfCancellationRequested();
            if (path is not null && HasLocalOutput(path)) lock (_gate) job.ResultPath = path;
        }
    }

    /// <summary>실물 엔진 실행 — 실패 결과·로컬 산출물 부재는 예외로 승격(Failed 격리).</summary>
    private async Task ExecuteOnEngineAsync(IRenderExecutionEngine engine, BatchRenderJob job, CancellationToken ct)
    {
        await WaitIfPausedAsync(ct).ConfigureAwait(false);
        lock (_gate) job.EngineKind = engine.EngineKind;
        Raise();

        var result = await engine.ExecuteAsync(job, new JobProgress(this, job), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (result is null || !result.Ok)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result?.Message) ? "렌더 엔진 실행 실패" : result.Message);

        // 산출물 실존 가드 — 엔진이 경로를 비워 보내면 작업 OutputPath 로 재확인. 로컬 경로인데 부재/0바이트면 '유령 완료' 대신 Failed.
        var output = string.IsNullOrWhiteSpace(result.OutputPath) ? job.OutputPath : result.OutputPath;
        if (IsLocalPath(output) && !HasLocalOutput(output!))
            throw new FileNotFoundException("렌더 산출 파일 없음(디스크 미존재 또는 0바이트): " + output, output);
        lock (_gate) { job.EngineKind = result.EngineKind; job.ResultPath = output; }
    }

    /// <summary>로컬 산출물 실존 + 비어있지 않음.</summary>
    internal static bool HasLocalOutput(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// CUDA OOM 가드 — 첫 실패(<see cref="BatchRenderJob.RetryCount"/>==0)이고 하향 해상도가 있으면 해상도 하향 · 재시도 1 증가 ·
    /// 메타 <see cref="MetaOomAutoDownscaled"/>="true" 기록 후 Queued 복귀(워커 루프가 즉시 재실행). 아니면 false(Failed 처리).
    /// </summary>
    private bool TryOomDownscaleRetry(BatchRenderJob job, string? message)
    {
        if (!VideoEngineRenderExecutionEngine.IsCudaOom(message)) return false;
        lock (_gate)
        {
            if (job.RetryCount != 0) return false;
            var lower = VideoEngineRenderExecutionEngine.DownscaleResolution(job.Resolution);
            if (lower is null) return false;
            var meta = new Dictionary<string, string>(StringComparer.Ordinal);
            if (job.Metadata is not null) foreach (var kv in job.Metadata) meta[kv.Key] = kv.Value;
            meta[MetaOomAutoDownscaled] = "true";
            job.Metadata = meta;
            job.Resolution = lower;
            job.RetryCount++;
            job.Status = BatchJobStatus.Queued;
            job.ProgressPercent = 0;
            job.ErrorMessage = OomRetryNotice;
        }
        return true;
    }

    /// <summary>OOM 자동 다운스케일 재시도 메타 키.</summary>
    public const string MetaOomAutoDownscaled = "OomAutoDownscaled";
    /// <summary>OOM 자동 재시도 안내 문구.</summary>
    public const string OomRetryNotice = "GPU VRAM 부족으로 해상도를 자동 조정하여 재시도합니다";

    private static bool IsLocalPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && !path.Contains("://", StringComparison.Ordinal) && Path.IsPathRooted(path);

    private static void ResetRunInfo(BatchRenderJob job)
    {
        job.EngineKind = null;
        job.ResultPath = null;
        job.ErrorMessage = null;
        job.UserCancelRequested = false;
        job.IsQuarantined = false;
        job.QuarantineReason = null;
    }

    /// <summary>🧯 예외 → 격리 사유 분류(대소문자 무시 부분 일치 · 할당 최소화).</summary>
    internal static string ClassifyQuarantineReason(Exception ex)
    {
        var m = ex.Message ?? "";
        if (ex is System.Net.Http.HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests }
            || m.Contains("429", StringComparison.Ordinal) || m.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            return "API Rate Limit 429";
        if (ex is ArgumentException or FormatException || m.Contains("invalid", StringComparison.OrdinalIgnoreCase))
            return "Invalid Input";
        if (ex is TimeoutException || m.Contains("timeout", StringComparison.OrdinalIgnoreCase) || m.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return "Timeout";
        if (ex is System.Net.Http.HttpRequestException) return "Network Error";
        if (ex is IOException or UnauthorizedAccessException) return "I/O Error";
        return "Engine Error";
    }

    /// <summary>대기열 이동 대상(아직 디스패치 전) 판정.</summary>
    private static bool IsReorderable(BatchJobStatus s) => s is BatchJobStatus.Queued or BatchJobStatus.Pending;

    /// <inheritdoc/>
    public bool MoveJobUp(Guid jobId)
    {
        lock (_gate)
        {
            var i = IndexOf(jobId);
            if (i < 0 || !IsReorderable(Queue[i].Status)) return false;
            var j = i - 1;
            while (j >= 0 && !IsReorderable(Queue[j].Status)) j--;
            if (j < 0) return false;
            Queue.Move(i, j);
        }
        Raise();
        return true;
    }

    /// <inheritdoc/>
    public bool MoveJobDown(Guid jobId)
    {
        lock (_gate)
        {
            var i = IndexOf(jobId);
            if (i < 0 || !IsReorderable(Queue[i].Status)) return false;
            var j = i + 1;
            while (j < Queue.Count && !IsReorderable(Queue[j].Status)) j++;
            if (j >= Queue.Count) return false;
            Queue.Move(i, j);
        }
        Raise();
        return true;
    }

    /// <inheritdoc/>
    public bool MoveJobToTop(Guid jobId)
    {
        lock (_gate)
        {
            var i = IndexOf(jobId);
            if (i < 0 || !IsReorderable(Queue[i].Status)) return false;
            var first = 0;
            while (first < i && !IsReorderable(Queue[first].Status)) first++;
            if (first == i) return false;   // 이미 1순위
            Queue.Move(i, first);
        }
        Raise();
        return true;
    }

    /// <summary>엔진 진행률 → 작업 진행률(동기 반영 · 0~99 클램프, 100 은 완료 전이에서만).</summary>
    private sealed class JobProgress(RenderQueueService owner, BatchRenderJob job) : IProgress<int>
    {
        public void Report(int value)
        {
            var p = value < 0 ? 0 : value > 99 ? 99 : value;
            lock (owner._gate)
            {
                if (job.Status != BatchJobStatus.Processing || p <= job.ProgressPercent) return;
                job.ProgressPercent = p;
            }
            owner.Raise();
        }
    }

    /// <summary>
    /// 완료 작업의 출력 미디어 옆에 <c>{이름}.meta.json</c> 기록 — <see cref="GalleryMediaService"/> 스키마(camelCase).
    /// 기록 실패는 격리(작업 상태는 Completed 유지).
    /// </summary>
    internal static void WriteRenderMeta(BatchRenderJob job, DateTimeOffset startedAt)
    {
        var mediaPath = !string.IsNullOrWhiteSpace(job.OutputPath) ? job.OutputPath
                      : IsLocalPath(job.ResultPath) ? job.ResultPath : null;
        if (mediaPath is null) return;
        try
        {
            var created = job.CompletedAt ?? DateTimeOffset.Now;
            var json = JsonSerializer.Serialize(new
            {
                title = Path.GetFileNameWithoutExtension(mediaPath),
                prompt = job.Prompt,
                seed = job.Seed,
                resolution = job.Resolution,
                createdAt = created,
                durationSec = job.DurationSec,
                renderDurationSec = (float)Math.Max(0, (created - startedAt).TotalSeconds),
                cutIndex = job.CutIndex,
                bgmBpm = job.BgmBpm,
                transitionBeatIndex = job.TransitionBeatIndex,
                transitionFx = job.TransitionFx,
                subjectAnchor = job.SubjectAnchor,
                referenceImagePath = job.ReferenceImagePath,
                metadata = job.Metadata,
                engine = job.EngineKind,
                isMock = string.Equals(job.EngineKind, RenderEngineKind.Mock, StringComparison.Ordinal),
            });
            File.WriteAllText(GalleryMediaService.MetaPathFor(mediaPath), json);
            // 최종 산출물(앰비언트 15s 등)이 별도 파일이면 동일 메타 복제 — 갤러리 Mock 태깅·복원 일관성
            if (IsLocalPath(job.ResultPath) && !string.Equals(Path.GetFullPath(job.ResultPath!), Path.GetFullPath(mediaPath), StringComparison.OrdinalIgnoreCase)
                && File.Exists(job.ResultPath))
                File.WriteAllText(GalleryMediaService.MetaPathFor(job.ResultPath!), json);
        }
        catch (Exception ex) { Program.Log("RenderQueue.Meta", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        _shutdown.Dispose();
        OnChange = null;
        OnDurationStitched = null;
    }

    private void EnsureWorker()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (!_autoStart || _paused || _disposed || _workerRunning) return;
            _workerRunning = true;
            token = _shutdown.Token;
        }
        var telemetry = _telemetry;
        _ = Task.Run(async () =>
        {
            try { telemetry?.Start(TelemetryInterval); } catch (Exception ex) { Program.Log("RenderQueue.Telemetry", ex); }
            try { await ProcessQueueAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                lock (_gate) _workerRunning = false;
                try { telemetry?.Stop(); } catch (Exception ex) { Program.Log("RenderQueue.Telemetry", ex); }
            }
        });
    }

    private Task WaitIfPausedAsync(CancellationToken ct)
    {
        Task wait;
        lock (_gate)
        {
            if (!_paused) return Task.CompletedTask;
            wait = _resume.Task;
        }
        return wait.WaitAsync(ct);
    }

    private int IndexOf(Guid jobId)
    {
        for (var i = 0; i < Queue.Count; i++)
            if (Queue[i].JobId == jobId) return i;
        return -1;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception) { }
    }

    private static TaskCompletionSource NewResumeSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
