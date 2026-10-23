using System.Globalization;
using PromptFlowStudio.Services.CopyrightCompliance;
using PromptFlowStudio.Services.LiveArchive;
using PromptFlowStudio.Services.LiveClipper;
using PromptFlowStudio.Services.MatrixSyndication;
using PromptFlowStudio.Services.MultilingualDubbing;
using PromptFlowStudio.Services.ViralExport;
using PromptFlowStudio.Services.VodRepurposing;

namespace PromptFlowStudio.Services.MasterPipeline;

/// <summary>
/// [PromptFlow Studio v17.92.0] ⚡ 마스터 실러너 E2E 샌드박스 러너(HUD <c>MrrRun</c>).
///   ▸ DI 싱글톤 7 러너(v17.84 · v17.85 · v17.91 · v17.82 · v17.81 · v17.88 · v17.87)를 그대로 주입받아 <see cref="RealRunnerE2EChainExecutionEngine"/> 에 결속 —
///     DI 에 없으면 로컬 인스턴스로 폴백(소유 · Dispose 대상)하고 <see cref="IsInjected"/> = false 로 표시.
///   ▸ 수동 스텝(<see cref="StepAsync"/>) · E2E 원클릭 배치(<see cref="EnqueueBatch"/> → <see cref="HeadlessBatchRenderDriver"/> 전용 스레드).
///   ▸ 스테이지 텔레메트리 → 최근 로그 링(32) · OnChange 250 ms 스로틀(완료/실패/패키지 즉시).
/// </summary>
public sealed class MasterPipelineRealRunnerStudioRunner : IDisposable
{
    public const int LogSize = 32, StageCount = RealRunnerE2EChainExecutionEngine.StageCount;
    private const long NotifyIntervalMs = 250;

    private readonly bool[] _injected = new bool[StageCount];
    private readonly IDisposable?[] _owned = new IDisposable?[StageCount];
    private readonly string[] _log = new string[LogSize];
    private readonly CancellationTokenSource _life = new();
    private int _logHead, _logCount, _stepBusy;
    private long _lastNotify;

    public MasterPipelineRealRunnerStudioRunner(IServiceProvider sp)
    {
        var clipper = Resolve<LiveToShortsAutoClipperStudioRunner>(sp, RunnerStage.Clip);
        var predictor = Resolve<ViralPredictorShortsExportStudioRunner>(sp, RunnerStage.Score);
        if (predictor is not null && (clipper is null || !ReferenceEquals(predictor.Clipper, clipper))) { clipper = predictor.Clipper; _injected[0] = true; }
        clipper ??= Own(new LiveToShortsAutoClipperStudioRunner(), RunnerStage.Clip);
        predictor ??= Own(new ViralPredictorShortsExportStudioRunner(clipper), RunnerStage.Score);
        Engine = new RealRunnerE2EChainExecutionEngine(clipper, predictor,
            Resolve<CopyrightComplianceStudioRunner>(sp, RunnerStage.Compliance) ?? Own(new CopyrightComplianceStudioRunner(), RunnerStage.Compliance),
            Resolve<MultilingualDubbingStudioRunner>(sp, RunnerStage.Dubbing) ?? Own(new MultilingualDubbingStudioRunner(), RunnerStage.Dubbing),
            Resolve<MatrixSyndicationStudioRunner>(sp, RunnerStage.Variant) ?? Own(new MatrixSyndicationStudioRunner(), RunnerStage.Variant),
            Resolve<VodRepurposingStudioRunner>(sp, RunnerStage.Article) ?? Own(new VodRepurposingStudioRunner(), RunnerStage.Article),
            Resolve<LiveArchiveCloudVaultStudioRunner>(sp, RunnerStage.Vault) ?? Own(new LiveArchiveCloudVaultStudioRunner(), RunnerStage.Vault))
        {
            Observer = OnStage,
        };
        Batch = new HeadlessBatchRenderDriver(Engine);
        Batch.OnPackage += OnPackage;
    }

    public event Action? OnChange;
    public RealRunnerE2EChainExecutionEngine Engine { get; }
    public HeadlessBatchRenderDriver Batch { get; }
    public RealRunnerChainContext Context => Engine.Context;
    public PipelineInterlockStagingRollbackGuard Guard => Engine.Guard;
    public bool Busy => Engine.Busy || Batch.Processing || Batch.Pending > 0 || Volatile.Read(ref _stepBusy) != 0;
    public bool SimulateContentIdBreach => Engine.InjectFaultStage == (int)RunnerStage.Compliance;
    public int SegmentSeconds { get; private set; } = RealRunnerE2EChainExecutionEngine.DefaultSegmentSeconds;
    public long Steps { get; private set; }
    public double LastBatchMs { get; private set; }
    public string? LastAction { get; private set; }
    public BatchMediaPackage? LastPackage => Batch.Package(0);
    public int LogCount => _logCount;
    public bool IsInjected(int stage) => _injected[stage];
    public int InjectedCount { get { var n = 0; foreach (var i in _injected) if (i) n++; return n; } }

    /// <summary>최근 로그(0 = 최신).</summary>
    public string Log(int i) => _log[(_logHead - 1 - i + LogSize * 2) % LogSize];

    private T? Resolve<T>(IServiceProvider sp, RunnerStage s) where T : class
    {
        var r = sp.GetService(typeof(T)) as T;
        _injected[(int)s] = r is not null;
        return r;
    }

    private T Own<T>(T runner, RunnerStage s) where T : IDisposable
    {
        _owned[(int)s] = runner;
        return runner;
    }

    // ══════════════════════ 조작(비핫패스) ══════════════════════

    public void SetSegmentSeconds(int seconds)
    {
        SegmentSeconds = Math.Clamp(seconds, RealRunnerE2EChainExecutionEngine.MinSegmentSeconds, RealRunnerE2EChainExecutionEngine.MaxSegmentSeconds);
        LastAction = "⏱ 세그먼트 " + SegmentSeconds.ToString(CultureInfo.InvariantCulture) + " s";
        Raise(true);
    }

    public void ToggleContentIdBreach()
    {
        Engine.InjectFaultStage = SimulateContentIdBreach ? -1 : (int)RunnerStage.Compliance;
        LastAction = SimulateContentIdBreach ? "🧪 Content ID 위반 주입 ON — ③ 검역 직후 인터락 · 상류 버퍼 롤백" : "🧪 Content ID 위반 주입 OFF";
        Raise(true);
    }

    /// <summary>단일 단계 수동 스텝(백그라운드 실행 · 배치 진행 중이면 거절).</summary>
    public async Task<bool> StepAsync()
    {
        if (Batch.Processing || Batch.Pending > 0 || Interlocked.Exchange(ref _stepBusy, 1) != 0) { LastAction = "⏳ 실행 중 — 스텝 대기"; Raise(true); return false; }
        try
        {
            var ok = await Task.Run(() => Engine.StepAsync(SegmentSeconds, _life.Token), _life.Token).ConfigureAwait(false);
            Steps++;
            LastAction = ok
                ? "⏭ 스텝 완료 — " + DoneLabel()
                : "⛔ 인터락 — " + Engine.LastError + " · 회수 " + Kb(Engine.LastReclaimedBytes);
            return ok;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            LastAction = "⚠️ 스텝 실패 — " + ex.Message;
            PromptFlowStudio.Program.Log("MasterPipeline.Step", ex);
            return false;
        }
        finally { Volatile.Write(ref _stepBusy, 0); Raise(true); }
    }

    /// <summary>E2E 전단계 원클릭 배치 — 헤드리스 드라이버 큐에 투입(UI 비차단).</summary>
    public long EnqueueBatch()
    {
        if (Volatile.Read(ref _stepBusy) != 0) { LastAction = "⏳ 스텝 실행 중 — 배치 대기"; Raise(true); return -1; }
        var id = Batch.Enqueue(SegmentSeconds);
        LastAction = id > 0
            ? "⚡ 배치 #" + id.ToString(CultureInfo.InvariantCulture) + " 투입 — 대기 " + Batch.Pending.ToString(CultureInfo.InvariantCulture) + " · 헤드리스 스레드"
            : "⚠️ 배치 큐 포화(" + HeadlessBatchRenderDriver.Capacity.ToString(CultureInfo.InvariantCulture) + ") — 거절";
        Raise(true);
        return id;
    }

    public void Reset()
    {
        if (Busy) { LastAction = "⏳ 실행 중 — 초기화 불가"; Raise(true); return; }
        Engine.Reset();
        Array.Clear(_log);
        _logHead = _logCount = 0;
        LastAction = "♻ 초기화 — 스테이징 회수 · 인터락 해제";
        Raise(true);
    }

    // ══════════════════════ 텔레메트리 ══════════════════════

    private void OnStage(RunnerStageTelemetry e)
    {
        var s = (int)e.Stage;
        string line = e.State switch
        {
            RunnerStageState.Running => "▶ " + RealRunnerE2EChainExecutionEngine.StageIcons[s] + " " + RealRunnerE2EChainExecutionEngine.StageNames[s] + " → " + RealRunnerE2EChainExecutionEngine.RunnerNames[s],
            RunnerStageState.Done => "✅ " + RealRunnerE2EChainExecutionEngine.StageNames[s] + " · " + Us(e.ElapsedUs) + " · " + Kb(e.Bytes),
            _ => "⛔ " + RealRunnerE2EChainExecutionEngine.StageNames[s] + " 인터락 · " + Us(e.ElapsedUs) + " · 롤백 회수 " + Kb(e.Bytes) + " · " + Engine.LastError,
        };
        lock (_log)
        {
            _log[_logHead] = "#" + e.Seq.ToString(CultureInfo.InvariantCulture) + " " + line;
            _logHead = (_logHead + 1) % LogSize;
            if (_logCount < LogSize) _logCount++;
        }
        Raise(e.State != RunnerStageState.Running);
    }

    private void OnPackage(BatchMediaPackage p)
    {
        LastBatchMs = p.ElapsedMs;
        LastAction = p.Ok
            ? "📦 배치 #" + p.JobId.ToString(CultureInfo.InvariantCulture) + " 패키지 — 쇼츠 " + Kb(p.ShortsMp4.Length) + " · 카드 " + p.Cards.Length.ToString(CultureInfo.InvariantCulture) + "장 · 아티클 " +
              p.Markdown.Length.ToString(CultureInfo.InvariantCulture) + "자 · 볼트 " + p.VaultObjects.ToString(CultureInfo.InvariantCulture) + "개 · " + p.ElapsedMs.ToString("0", CultureInfo.InvariantCulture) + " ms"
            : "⛔ 배치 #" + p.JobId.ToString(CultureInfo.InvariantCulture) + " 중단 — " + p.Error + " · 회수 " + Kb(p.ReclaimedBytes);
        Raise(true);
    }

    private string DoneLabel() =>
        Context.DoneCount.ToString(CultureInfo.InvariantCulture) + "/" + StageCount.ToString(CultureInfo.InvariantCulture) + " · " + Us(Context.TotalUs);

    public static string Us(long us) => us >= 1_000_000 ? (us / 1e6).ToString("0.00", CultureInfo.InvariantCulture) + " s" : us >= 1000 ? (us / 1e3).ToString("0.0", CultureInfo.InvariantCulture) + " ms" : us.ToString(CultureInfo.InvariantCulture) + " µs";
    public static string Kb(long bytes) => bytes >= 1_048_576 ? (bytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";

    public string Summary() =>
        "실행 " + Engine.Runs.ToString(CultureInfo.InvariantCulture) + " · 완료 " + Engine.Completions.ToString(CultureInfo.InvariantCulture) + " · 인터락 " + Engine.Aborts.ToString(CultureInfo.InvariantCulture) +
        " · 롤백 회수 " + Kb(Guard.ReclaimedBytes) + " · 스테이징 " + Guard.OutstandingLeases.ToString(CultureInfo.InvariantCulture) + "건 " + Kb(Guard.OutstandingBytes) +
        " · 배치 " + Batch.Completed.ToString(CultureInfo.InvariantCulture) + "/" + Batch.Enqueued.ToString(CultureInfo.InvariantCulture);

    private void Raise(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now - Interlocked.Read(ref _lastNotify) < NotifyIntervalMs) return;
        Interlocked.Exchange(ref _lastNotify, now);
        try { OnChange?.Invoke(); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("MasterPipeline.OnChange", ex); }
    }

    public void Dispose()
    {
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        Batch.OnPackage -= OnPackage;
        Batch.Dispose();
        Engine.Observer = null;
        Engine.Dispose();
        for (var i = 0; i < _owned.Length; i++) { _owned[i]?.Dispose(); _owned[i] = null; }
        _life.Dispose();
        OnChange = null;
    }
}
