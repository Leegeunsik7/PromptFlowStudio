using PromptFlowStudio.Services.ProductionCrew;

namespace PromptFlowStudio.Services.CloudVerification;

public enum VerificationPhase : byte { Idle, SourceScan, DynamicProbe, UiDriving, Manifest, Done, Failed }

/// <summary>
/// [PromptFlow Studio v16.92.0] 🔍 AI 클라우드 소스 검증 &amp; UI 인터랙티브 검사 스튜디오 러너.
/// 1-Click 전수 검사 = 소스 2패스 스캔(+ Razor 인벤토리) → 동적 할당 프로브(격리 인스턴스 핫패스) → UI 가상 순회(가드 래핑 · 스텝 간 지연) → 진단 매니페스트.
/// 대상 등록: v16.71~v16.90 스튜디오 20종(실행 가드 프로브 Start/Stop) · 프로덕션 크루(가동 · 결선/자동 진행/신뢰 학습 스위치 · 가드 예산 슬라이더) · 자체 명령.
/// </summary>
public sealed class CloudVerificationStudioRunner : IDisposable
{
    public static readonly string[] PhaseNames = { "대기", "소스 스캔", "동적 프로브", "UI 순회", "매니페스트", "완료", "실패" };

    private readonly CloudSourceCodeIntegrityVerifierEngine _source;
    private readonly FullUiInteractiveMenuAutomatedDriver _driver;
    private readonly RealtimeUiExceptionSelfHealingGuard _guard;
    private CancellationTokenSource? _cts;

    public CloudVerificationStudioRunner(IServiceProvider sp)
        : this((CloudSourceCodeIntegrityVerifierEngine?)sp.GetService(typeof(CloudSourceCodeIntegrityVerifierEngine)) ?? new CloudSourceCodeIntegrityVerifierEngine(),
               (FullUiInteractiveMenuAutomatedDriver?)sp.GetService(typeof(FullUiInteractiveMenuAutomatedDriver)) ?? new FullUiInteractiveMenuAutomatedDriver(new RealtimeUiExceptionSelfHealingGuard()))
    {
        RegisterTargets(sp);
    }

    public CloudVerificationStudioRunner(CloudSourceCodeIntegrityVerifierEngine source, FullUiInteractiveMenuAutomatedDriver driver)
    {
        _source = source; _driver = driver; _guard = driver.Guard;
        _guard.OnIncident += Raise;
        _driver.AddCommand("검증 스튜디오", "진단 매니페스트 생성", () => _guard.BuildManifest(_source, _driver), () => _guard.LastDigest is { Length: 64 });
    }

    public event Action? OnChange;

    public CloudSourceCodeIntegrityVerifierEngine Source => _source;
    public FullUiInteractiveMenuAutomatedDriver Driver => _driver;
    public RealtimeUiExceptionSelfHealingGuard Guard => _guard;
    public bool IsRunning => _cts is not null;
    public VerificationPhase Phase { get; private set; }
    public string? LastAction { get; private set; }
    public string? SourceRoot { get; set; } = CloudSourceCodeIntegrityVerifierEngine.FindSourceRoot();
    public int StepDelayMs { get; set; } = 60;
    public bool IncludeStudios { get; set; } = true;
    public double LastRunMs { get; private set; }
    public long Runs { get; private set; }

    /// <summary>앱 결선 대상 등록(미등록 서비스 생략 · 중복 차단).</summary>
    public int RegisterTargets(IServiceProvider sp)
    {
        var before = _driver.Count;
        var swarm = sp.GetService(typeof(RealtimeProductionSwarmExecutionGuard)) as RealtimeProductionSwarmExecutionGuard;
        if (swarm is not null)
        {
            if (swarm.Count == 0) swarm.RegisterStudios(sp);
            for (var i = 0; i < swarm.Count; i++)
            {
                var m = swarm.Module(i);
                _driver.AddStudio("헤더 스튜디오", "v" + m.Version + " " + m.Name, m.IsRunning, m.Start, m.Stop);
            }
            _driver.AddToggle("실행 가드", "자동 선점", () => swarm.AutoPreempt, v => swarm.AutoPreempt = v, UiControlKind.Switch);
            _driver.AddSlider("실행 가드", "프레임 예산(ms)", 8d, 33.3d, () => swarm.FrameBudgetMs, v => swarm.FrameBudgetMs = (float)v);
            _driver.AddSlider("실행 가드", "병렬 레인", 1d, 8d, () => swarm.Lanes, v => swarm.Lanes = (int)Math.Round(v), 1d);
            _driver.AddSlider("실행 가드", "메모리 예산(MB)", 256d, 4096d, () => swarm.MemoryBudgetMb, v => swarm.MemoryBudgetMb = (float)v);
        }
        if (sp.GetService(typeof(MultiAgentCrewStudioRunner)) is MultiAgentCrewStudioRunner crew)
        {
            _driver.AddStudio("헤더 스튜디오", "v16.91 프로덕션 크루", () => crew.IsRunning, crew.Start, crew.Stop);
            _driver.AddToggle("크루 모달", "모듈 결선", () => crew.ApplyToModules, v => crew.ApplyToModules = v, UiControlKind.Switch);
            _driver.AddToggle("크루 모달", "샷 자동 진행", () => crew.AutoAdvance, v => crew.AutoAdvance = v);
            _driver.AddToggle("크루 모달", "신뢰 학습", () => crew.Consensus.AdaptWeights, v => crew.Consensus.AdaptWeights = v);
        }
        _driver.AddSlider("검증 스튜디오", "스텝 지연(ms)", 0d, 500d, () => StepDelayMs, v => StepDelayMs = (int)Math.Round(v), 1d);
        _driver.AddToggle("검증 스튜디오", "스튜디오 포함", () => IncludeStudios, v => IncludeStudios = v, UiControlKind.Switch);
        return _driver.Count - before;
    }

    /// <summary>1-Click 전수 검사(백그라운드).</summary>
    public void StartFull() => Launch(source: true, ui: true);
    public void StartSourceOnly() => Launch(source: true, ui: false);
    public void StartUiOnly() => Launch(source: false, ui: true);

    private void Launch(bool source, bool ui)
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(async () =>
        {
            try { await RunAsync(source, ui, cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { LastAction = "⏹ 검사 중단"; Phase = VerificationPhase.Idle; }
            catch (Exception ex) { Phase = VerificationPhase.Failed; LastAction = "⚠ 검사 실패: " + ex.Message; PromptFlowStudio.Program.Log("CloudVerify.Run", ex); }
            finally
            {
                if (Interlocked.CompareExchange(ref _cts, null, cts) == cts) cts.Dispose();
                Raise();
            }
        });
        Raise();
    }

    /// <summary>검사 파이프라인(테스트에서 직접 await 가능).</summary>
    public async Task RunAsync(bool source, bool ui, CancellationToken ct)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Runs++;
        _guard.Arm();
        try
        {
            if (source)
            {
                Phase = VerificationPhase.SourceScan; LastAction = "🔍 소스 2패스 스캔"; Raise();
                _driver.ResetInventory();
                if (SourceRoot is { } root && Directory.Exists(root))
                {
                    var n = await _source.ScanDirectoryAsync(root, ct, _driver.InventoryMarkup).ConfigureAwait(false);
                    LastAction = "🔍 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 파일 스캔";
                }
                else LastAction = "⚠ 소스 루트 미발견(PROMPTFLOW_SOURCE_ROOT) — 동적 검사만 수행";
                Raise();
                Phase = VerificationPhase.DynamicProbe; Raise();
                ProbeHotPaths();
            }
            if (ui)
            {
                Phase = VerificationPhase.UiDriving; LastAction = "🖱 UI 가상 순회"; Raise();
                _driver.Rewind();
                var done = _driver.Count == 0;
                while (!done)
                {
                    ct.ThrowIfCancellationRequested();
                    var t = _driver.Target(_driver.Cursor);
                    if (!IncludeStudios && t.Kind == UiControlKind.StudioButton) { t.Outcome = UiStepOutcome.Pending; done = SkipOne(); continue; }
                    done = _driver.Step();
                    Raise();
                    if (StepDelayMs > 0) await Task.Delay(StepDelayMs, ct).ConfigureAwait(false);
                }
            }
            Phase = VerificationPhase.Manifest; Raise();
            _guard.BuildManifest(_source, _driver);
            Phase = VerificationPhase.Done;
            LastAction = "✅ 검사 완료 — 정합성 " + _source.IntegrityScore.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                + " · UI 실패 " + _driver.CycleFailures.ToString(System.Globalization.CultureInfo.InvariantCulture) + " · 사고 " + _guard.Incidents.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            _guard.Disarm();
            LastRunMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            Raise();
        }
    }

    private bool SkipOne()
    {
        // 스튜디오 제외 시 커서만 전진(Step 우회)
        var last = _driver.Cursor + 1 >= _driver.Count;
        _driver.SkipCursor();
        return last;
    }

    /// <summary>격리 인스턴스 핫패스 동적 할당 실측(앱 상태 비간섭).</summary>
    public void ProbeHotPaths()
    {
        var crew = new MultiAgentCrewStudioRunner(new MultiAgentVirtualProductionCrewOrchestratorEngine(new AutonomousDirectorialConsensusEvaluator(), new RealtimeProductionSwarmExecutionGuard()));
        _source.ProbeAllocations("v16.91 Crew.TickOnce", crew.TickOnce, 120);
        var g = new RealtimeProductionSwarmExecutionGuard();
        long tick = 0;
        _source.ProbeAllocations("v16.91 SwarmGuard.Scan", () => g.Scan(++tick, 0.5f), 120);
        var probeGuard = new RealtimeUiExceptionSelfHealingGuard();
        var d = new FullUiInteractiveMenuAutomatedDriver(probeGuard);
        var flag = false;
        d.AddToggle("probe", "toggle", () => flag, v => flag = v);
        _source.ProbeAllocations("v16.92 UiDriver.Step", () => d.Step(), 120);
        crew.Dispose();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("CloudVerify.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        _guard.OnIncident -= Raise;
        _guard.Dispose();
    }
}
