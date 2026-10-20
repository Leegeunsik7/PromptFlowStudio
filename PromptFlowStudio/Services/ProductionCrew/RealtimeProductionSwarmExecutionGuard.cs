namespace PromptFlowStudio.Services.ProductionCrew;

public enum ModuleCategory : byte { Render, Simulation, Audio, Generative, Pipeline }
public enum GuardAction : byte { Started, Denied, Preempted, Quarantined, Released, Throttled, Recovered }

/// <summary>모듈 프로브(제어 평면 등록): 이름 · 버전 · 분류 · 선언 비용(ms/프레임 · MB) · 우선순위(높을수록 보존) · 상태/시작/정지 델리게이트.</summary>
public sealed class ModuleProbe
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public ModuleCategory Category { get; init; }
    public float CostMs { get; set; }
    public float MemoryMb { get; set; }
    public int Priority { get; set; }
    public bool Pinned { get; set; }
    public required Func<bool> IsRunning { get; init; }
    public required Action Start { get; init; }
    public required Action Stop { get; init; }
    // 상태(가드 소유)
    public bool WasRunning;
    public int Transitions;
    public long WindowStartTick, QuarantinedUntil;
    public long Preemptions;
}

/// <summary>가드 이벤트(값 형식).</summary>
public readonly record struct GuardEvent(long Tick, int Module, GuardAction Action, float CostMs, float MemoryMb);

/// <summary>
/// [PromptFlow Studio v16.91.0] 🚦 실시간 풀 파이프라인 프로덕션 스웜 실행 가드(무할당 스캔 · 규칙 기반).
///   ▸ v16.71~v16.90 스튜디오 러너 20종 프로브(IsRunning/Start/Stop 델리게이트 · 선언 비용 ms/MB · 우선순위 · 고정).
///   ▸ 예산: 프레임 = 16.6 ms × 병렬 레인 · 메모리 = MB 상한 · 관리 힙 실측(GC.GetTotalMemory(false)) 반영.
///   ▸ 승인 제어(스태킹 예방): RequestStart → 예산 초과 시 낮은 우선순위(비고정) 선점 정지 → 그래도 초과면 거부.
///   ▸ 감시 스캔: 외부에서 시작된 모듈 포함 실행 집합 추적 → 초과 시 최저 우선순위부터 선점 · 진동(창 내 전이 ≥ 6) → 격리(일정 틱 시작 차단) · 이벤트 링.
///   ▸ VFX 스로틀(0..1) = 예산 압력 × 요청 밀도 → 오케스트레이터 VFX 용량/헤드룸 피드백.
/// </summary>
public sealed class RealtimeProductionSwarmExecutionGuard
{
    public const int MaxModules = 32, LogSize = 64, FlapWindow = 600, FlapLimit = 6, QuarantineTicks = 1800;
    private readonly object _gate = new();
    private readonly ModuleProbe[] _m = new ModuleProbe[MaxModules];
    private readonly GuardEvent[] _log = new GuardEvent[LogSize];
    private int _n, _logHead, _logCount;
    private long _tick;

    public float FrameBudgetMs { get; set; } = 16.6f;
    public int Lanes { get; set; } = 2;
    public float MemoryBudgetMb { get; set; } = 1024f;
    public bool AutoPreempt { get; set; } = true;
    public float VfxCostPerDensityMs { get; set; } = 6f;

    public int Count => _n;
    public ModuleProbe Module(int i) => _m[i];
    public float LoadMs { get; private set; }
    public float LoadMb { get; private set; }
    public float ManagedHeapMb { get; private set; }
    public int RunningCount { get; private set; }
    public float Pressure { get; private set; }
    public float Headroom { get; private set; } = 1f;
    public float VfxThrottle { get; private set; }
    public long Preemptions { get; private set; }
    public long Denials { get; private set; }
    public long Quarantines { get; private set; }
    public long Scans { get; private set; }
    public double LastScanMs { get; private set; }
    public float CapacityMs => FrameBudgetMs * Lanes;
    public int LogCount => _logCount;
    public GuardEvent Log(int back) => _log[(_logHead - 1 - back + 2 * LogSize) % LogSize];

    public static string ActionName(GuardAction a) => a switch
    {
        GuardAction.Started => "시작 승인", GuardAction.Denied => "시작 거부", GuardAction.Preempted => "선점 정지", GuardAction.Quarantined => "진동 격리",
        GuardAction.Released => "격리 해제", GuardAction.Throttled => "VFX 스로틀", _ => "정상화",
    };

    public int Register(ModuleProbe p)
    {
        lock (_gate)
        {
            if (_n >= MaxModules) return -1;
            _m[_n] = p; p.WasRunning = p.IsRunning();
            return _n++;
        }
    }

    private void Event(int module, GuardAction a)
    {
        var p = module >= 0 ? _m[module] : null;
        _log[_logHead] = new GuardEvent(_tick, module, a, p?.CostMs ?? 0f, p?.MemoryMb ?? 0f);
        _logHead = (_logHead + 1) % LogSize; if (_logCount < LogSize) _logCount++;
    }

    private void Totals(out float ms, out float mb, out int running)
    {
        ms = 0f; mb = 0f; running = 0;
        for (var i = 0; i < _n; i++) { if (!_m[i].IsRunning()) continue; ms += _m[i].CostMs; mb += _m[i].MemoryMb; running++; }
    }

    /// <summary>최저 우선순위 비고정 실행 모듈(except 제외) — 없으면 −1.</summary>
    private int Victim(int except, int belowPriority)
    {
        var best = -1;
        for (var i = 0; i < _n; i++)
        {
            var p = _m[i];
            if (i == except || p.Pinned || !p.IsRunning() || p.Priority >= belowPriority) continue;
            if (best < 0 || p.Priority < _m[best].Priority || (p.Priority == _m[best].Priority && p.CostMs > _m[best].CostMs)) best = i;
        }
        return best;
    }

    private void Preempt(int i)
    {
        var p = _m[i];
        try { p.Stop(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { PromptFlowStudio.Program.Log("CrewGuard.Stop", ex); }
        p.Preemptions++; Preemptions++;
        p.WasRunning = false;
        Event(i, GuardAction.Preempted);
    }

    /// <summary>승인 제어: 예산 확보(선점) 후 시작 · 불가 시 거부. 반환 = 시작 여부.</summary>
    public bool RequestStart(int i)
    {
        lock (_gate)
        {
            var p = _m[i];
            if (p.IsRunning()) return true;
            if (_tick < p.QuarantinedUntil) { Denials++; Event(i, GuardAction.Denied); return false; }
            Totals(out var ms, out var mb, out _);
            while (ms + p.CostMs > CapacityMs || mb + p.MemoryMb > MemoryBudgetMb)
            {
                var v = AutoPreempt ? Victim(i, p.Priority) : -1;
                if (v < 0) { Denials++; Event(i, GuardAction.Denied); return false; }
                ms -= _m[v].CostMs; mb -= _m[v].MemoryMb;
                Preempt(v);
            }
            try { p.Start(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { PromptFlowStudio.Program.Log("CrewGuard.Start", ex); return false; }
            Track(i, true);
            Event(i, GuardAction.Started);
            return true;
        }
    }

    private void Track(int i, bool running)
    {
        var p = _m[i];
        if (p.WasRunning == running) return;
        if (_tick - p.WindowStartTick > FlapWindow) { p.WindowStartTick = _tick; p.Transitions = 0; }
        p.Transitions++; p.WasRunning = running;
        if (p.Transitions >= FlapLimit && _tick >= p.QuarantinedUntil)
        {
            p.QuarantinedUntil = _tick + QuarantineTicks; Quarantines++;
            if (p.IsRunning() && !p.Pinned) { try { p.Stop(); } catch (InvalidOperationException) { } p.WasRunning = false; }
            Event(i, GuardAction.Quarantined);
        }
    }

    /// <summary>감시 스캔(무할당): 실행 집합 · 진동 감지 · 예산 초과 선점 · 압력/헤드룸 · VFX 스로틀(요청 밀도 0..1).</summary>
    public void Scan(long tick, float requestedVfxDensity)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _tick = tick;
            for (var i = 0; i < _n; i++)
            {
                var run = _m[i].IsRunning();
                if (run && _tick < _m[i].QuarantinedUntil && !_m[i].Pinned) { try { _m[i].Stop(); } catch (InvalidOperationException) { } Event(i, GuardAction.Denied); run = false; }
                Track(i, run);
            }
            Totals(out var ms, out var mb, out var running);
            ManagedHeapMb = GC.GetTotalMemory(false) / (1024f * 1024f);
            while (AutoPreempt && (ms > CapacityMs || mb > MemoryBudgetMb))
            {
                var v = Victim(-1, int.MaxValue);
                if (v < 0) break;
                ms -= _m[v].CostMs; mb -= _m[v].MemoryMb; running--;
                Preempt(v);
            }
            LoadMs = ms; LoadMb = mb; RunningCount = running;
            var vfxMs = requestedVfxDensity * VfxCostPerDensityMs;
            Pressure = MathF.Max((ms + vfxMs) / CapacityMs, mb / MemoryBudgetMb);
            Headroom = Math.Clamp((CapacityMs - ms) / MathF.Max(VfxCostPerDensityMs, 1e-3f), 0f, 1f);
            var prev = VfxThrottle;
            VfxThrottle = Math.Clamp((Pressure - 0.85f) / 0.3f, 0f, 1f) * Math.Clamp(requestedVfxDensity * 1.5f, 0f, 1f);
            if (VfxThrottle > 0.05f && prev <= 0.05f) Event(-1, GuardAction.Throttled);
            else if (VfxThrottle <= 0.05f && prev > 0.05f) Event(-1, GuardAction.Recovered);
            Scans++;
            LastScanMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    /// <summary>v16.71~v16.90 스튜디오 러너 20종 등록(DI 해석 실패 모듈은 생략 · 비용 = 각 STATUS 실측/추정).</summary>
    public int RegisterStudios(IServiceProvider sp)
    {
        var n = 0;
        void R<T>(string name, string ver, ModuleCategory cat, float ms, float mb, int prio, Func<T, bool> run, Action<T> start, Action<T> stop) where T : class
        {
            if (sp.GetService(typeof(T)) is not T s) return;
            if (Register(new ModuleProbe { Name = name, Version = ver, Category = cat, CostMs = ms, MemoryMb = mb, Priority = prio, IsRunning = () => run(s), Start = () => start(s), Stop = () => stop(s) }) >= 0) n++;
        }
        R<LiveStream.LiveBroadcastStudioRunner>("라이브 방송", "16.71", ModuleCategory.Pipeline, 4f, 60f, 9, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<OpenUsd.OpenUsdSpatialStudioRunner>("OpenUSD 공간", "16.72", ModuleCategory.Simulation, 3f, 80f, 7, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Twin.DigitalTwinStudioRunner>("디지털 트윈", "16.73", ModuleCategory.Simulation, 4f, 70f, 4, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<PathTracing.PathTracingStudioRunner>("경로 추적", "16.74", ModuleCategory.Render, 14f, 120f, 5, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Mocap.MotionCaptureStudioRunner>("모션 캡처", "16.75", ModuleCategory.Simulation, 3f, 40f, 6, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<SpatialAudio.SpatialAudioStudioRunner>("공간 오디오", "16.76", ModuleCategory.Audio, 2f, 30f, 8, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Holo.HolographicTeleportationStudioRunner>("홀로그래픽", "16.77", ModuleCategory.Render, 8f, 90f, 3, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Cinema.InteractiveCinemaStudioRunner>("인터랙티브 시네마", "16.78", ModuleCategory.Pipeline, 3f, 50f, 4, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Editorial.AutonomousEditorialStudioRunner>("자율 편집", "16.79", ModuleCategory.Pipeline, 3f, 50f, 6, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<CinematicMastering.VirtualProductionMasteringStudioRunner>("가상 프로덕션 마스터링", "16.80", ModuleCategory.Render, 6f, 80f, 5, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<GenerativeVfx.GenerativeVfxStudioRunner>("VFX 파티클", "16.81", ModuleCategory.Simulation, 9f, 110f, 4, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<DigitalHuman.DigitalHumanPerformanceStudioRunner>("디지털 휴먼", "16.82", ModuleCategory.Simulation, 4f, 40f, 6, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<HairCloth.NeuralHairClothStudioRunner>("헤어·의상", "16.83", ModuleCategory.Simulation, 12.5f, 60f, 4, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Atmosphere.GenerativeEnvironmentStudioRunner>("환경·기상", "16.84", ModuleCategory.Simulation, 1f, 20f, 5, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Cinematography.NeuralCinematographyStudioRunner>("시네마토그래피", "16.85", ModuleCategory.Render, 3.9f, 30f, 7, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<FilmScore.FilmScoreStudioRunner>("영화 음악", "16.86", ModuleCategory.Audio, 1.6f, 25f, 8, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<VideoDiffusion.LatentVideoDiffusionStudioRunner>("비디오 디퓨전", "16.87", ModuleCategory.Generative, 5.8f, 40f, 5, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Continuity.ScriptContinuityStudioRunner>("스크립트 연속성", "16.88", ModuleCategory.Pipeline, 0.1f, 5f, 9, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<Localization.Dubbing.NeuralLipSyncDubbingStudioRunner>("립싱크 더빙", "16.89", ModuleCategory.Audio, 0.5f, 20f, 6, x => x.IsRunning, x => x.Start(), x => x.Stop());
        R<ContentSecurity.ContentSecurityStudioRunner>("콘텐츠 보안", "16.90", ModuleCategory.Pipeline, 2.3f, 30f, 7, x => x.IsRunning, x => x.Start(), x => x.Stop());
        return n;
    }
}
