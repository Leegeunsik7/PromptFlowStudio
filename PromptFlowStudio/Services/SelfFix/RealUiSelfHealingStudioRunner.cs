using PromptFlowStudio.Services.CloudVerification;
using PromptFlowStudio.Services.ProductionCrew;

namespace PromptFlowStudio.Services.SelfFix;

public enum SelfHealPhase : byte { Idle, DomDriving, Planning, Repairing, Done, RolledBack, Failed }

/// <summary>
/// [PromptFlow Studio v16.93.0] 🛠 AI 실시간 DOM UI 구동 &amp; 소스 자동 수정 스튜디오 러너.
/// 1-Click = (선택) *StudioModal 전수 RenderTree 구동 → 소스 결함 패치 계획(Diff) → (패치 스위치 ON 시) 폐루프 적용/빌드/테스트/롤백.
/// 구동은 실 DI 싱글턴을 사용 → 컴포넌트마다 v16.92 바인딩 대상 값 복원 + 구동 전 정지 상태였던 스튜디오 정지.
/// </summary>
public sealed class RealUiSelfHealingStudioRunner : IDisposable
{
    public static readonly string[] PhaseNames = { "대기", "DOM 구동", "패치 계획", "폐루프 수리", "완료", "롤백", "실패" };
    private static readonly string[] GuardPhaseNames = { "대기", "스냅샷", "적용", "빌드", "테스트", "복원", "커밋", "롤백", "실패" };
    public static string GuardPhaseName(RepairPhase p) => GuardPhaseNames[(int)p];

    private readonly IServiceProvider? _sp;
    private readonly List<Type> _modals;
    private CancellationTokenSource? _cts;
    private bool[] _wasRunning = Array.Empty<bool>();
    private double[] _bindings = Array.Empty<double>();

    public RealUiSelfHealingStudioRunner(IServiceProvider sp)
        : this(new RealDomBlazorRenderTreeDriver(sp), new AutonomousCodeDefectSelfFixEngine(), new ClosedLoopRegressionSelfRepairGuard(), sp) { }

    public RealUiSelfHealingStudioRunner(RealDomBlazorRenderTreeDriver driver, AutonomousCodeDefectSelfFixEngine engine, ClosedLoopRegressionSelfRepairGuard guard, IServiceProvider? sp = null)
    {
        Driver = driver; Engine = engine; Guard = guard; _sp = sp;
        _modals = RealDomBlazorRenderTreeDriver.DiscoverStudioModals();
        _modals.RemoveAll(static t => t.Name == "RealUiSelfHealingStudioModal"); // 자기 자신(패치 스위치) 구동 제외
        Driver.AfterComponent = RestoreState;
        Driver.OnProgress += Raise;
        Guard.OnChange += Raise;
    }

    public event Action? OnChange;

    public RealDomBlazorRenderTreeDriver Driver { get; }
    public AutonomousCodeDefectSelfFixEngine Engine { get; }
    public ClosedLoopRegressionSelfRepairGuard Guard { get; }
    public int ModalCount => _modals.Count;
    public string ModalName(int i) => _modals[i].Name;
    public bool IsRunning => _cts is not null;
    public SelfHealPhase Phase { get; private set; }
    public string? LastAction { get; private set; }
    public bool DriveDom { get; set; } = true;
    /// <summary>패치 스위치: OFF = 계획/Diff만(드라이런) · ON = 실제 파일 수정 + 폐루프 검증.</summary>
    public bool ApplyPatches { get; set; }
    public bool RunTests { get => Guard.RunTests; set => Guard.RunTests = value; }
    public string? SourceRoot { get; set; } = CloudSourceCodeIntegrityVerifierEngine.FindSourceRoot();
    public string? RepoRoot => SourceRoot is null ? null : Directory.GetParent(SourceRoot)?.FullName;
    public long Runs { get; private set; }

    public void StartFull()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(async () =>
        {
            try { await RunAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Phase = SelfHealPhase.Idle; LastAction = "⏹ 중단(적용 파일 복원됨)"; }
            catch (Exception ex) { Phase = SelfHealPhase.Failed; LastAction = "⚠ 실패: " + ex.Message; PromptFlowStudio.Program.Log("SelfFix.Run", ex); }
            finally
            {
                if (Interlocked.CompareExchange(ref _cts, null, cts) == cts) cts.Dispose();
                Raise();
            }
        });
        Raise();
    }

    /// <summary>파이프라인(테스트 직접 await 가능).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Runs++;
        if (DriveDom)
        {
            Phase = SelfHealPhase.DomDriving; LastAction = "🖱 RenderTree 구동 — 모달 " + _modals.Count; Raise();
            SnapshotState();
            await Driver.DriveAllAsync(_modals, ct).ConfigureAwait(false);
        }
        if (SourceRoot is not { } root || !Directory.Exists(root)) { Phase = SelfHealPhase.Done; LastAction = "⚠ 소스 루트 미발견(PROMPTFLOW_SOURCE_ROOT) — DOM 구동만 수행"; return; }

        Phase = SelfHealPhase.Planning; LastAction = "🧩 패치 계획"; Raise();
        var n = await Engine.PlanAsync(root, ct).ConfigureAwait(false);
        if (!ApplyPatches || n == 0)
        {
            Phase = SelfHealPhase.Done;
            LastAction = "✅ 드라이런 — 패치 " + n + " · 권고 " + Engine.AdvisoryCount + " · DOM 예외 " + Driver.Exceptions;
            return;
        }
        Phase = SelfHealPhase.Repairing; LastAction = "🔁 폐루프 수리 — 패치 " + n; Raise();
        var rep = await Guard.RepairAsync(RepoRoot!, root, Engine.GroupByFile(), ct).ConfigureAwait(false);
        Phase = rep.Committed ? SelfHealPhase.Done : SelfHealPhase.RolledBack;
        LastAction = rep.Committed
            ? "✅ 커밋 — 패치 " + rep.PatchesCommitted + "/" + rep.PatchesPlanned + " · 파일 " + rep.FilesCommitted + " · 시도 " + rep.Attempts + (rep.TestsPassed >= 0 ? " · 테스트 " + rep.TestsPassed + " 통과" : "")
            : "↺ 롤백 — 원본 복원 " + rep.RestoredFiles + " · 시도 " + rep.Attempts;
    }

    private void SnapshotState()
    {
        if (_sp?.GetService(typeof(RealtimeProductionSwarmExecutionGuard)) is RealtimeProductionSwarmExecutionGuard g)
        {
            _wasRunning = new bool[g.Count];
            for (var i = 0; i < g.Count; i++) { try { _wasRunning[i] = g.Module(i).IsRunning(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Snapshot", ex); } }
        }
        if (_sp?.GetService(typeof(FullUiInteractiveMenuAutomatedDriver)) is FullUiInteractiveMenuAutomatedDriver d)
        {
            _bindings = new double[d.Count];
            for (var i = 0; i < d.Count; i++) { try { _bindings[i] = d.Target(i).Read(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Snapshot", ex); } }
        }
    }

    /// <summary>컴포넌트 구동 후 복원: 바인딩 값 · 구동 전 정지였던 스튜디오 정지.</summary>
    private void RestoreState(Type _)
    {
        if (_sp?.GetService(typeof(FullUiInteractiveMenuAutomatedDriver)) is FullUiInteractiveMenuAutomatedDriver d)
            for (var i = 0; i < d.Count && i < _bindings.Length; i++)
            {
                var t = d.Target(i);
                if (t.Kind is UiControlKind.Command or UiControlKind.StudioButton) continue;
                try { if (Math.Abs(t.Read() - _bindings[i]) > 1e-9) t.Write(_bindings[i]); } catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Restore.Binding", ex); }
            }
        if (_sp?.GetService(typeof(RealtimeProductionSwarmExecutionGuard)) is RealtimeProductionSwarmExecutionGuard g)
            for (var i = 0; i < g.Count && i < _wasRunning.Length; i++)
            {
                try { var m = g.Module(i); if (!_wasRunning[i] && m.IsRunning()) m.Stop(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Restore.Studio", ex); }
            }
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
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Driver.OnProgress -= Raise;
        Guard.OnChange -= Raise;
    }
}
