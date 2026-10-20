using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.ProductionCrew;

/// <summary>샷 결과(값 형식).</summary>
public readonly record struct CrewShotRecord(int Shot, EmotionTarget Target, EmotionTarget Achieved, float Loss, float MakespanSec, float PlannedSec, int Conflicts, float BeatsPerCut);

/// <summary>
/// [PromptFlow Studio v16.91.0] 🎬 AI 멀티에이전트 프로덕션 크루 스튜디오 러너(60 Hz · <see cref="TickOnce"/> 무할당).
/// 스크립트 감정 아크 8샷(평온 → 의심 → 추격 → 절정 → 여운 …) → 크루 제안 → 합의 → 작업 DAG 진행 → 샷 완료 기록 → 다음 샷.
/// 선택: 결정 → 실제 모듈 결선(v16.86 영화 음악 큐 · v16.85 더치 · v16.84 안개 — 샷 전환 시 1회).
/// </summary>
public sealed class MultiAgentCrewStudioRunner : IDisposable
{
    public const int Hz = 60, MaxRecords = 32;
    public static readonly EmotionTarget[] Script =
    {
        new(0.5f, 0.2f, 0.1f), new(0.0f, 0.35f, 0.5f), new(-0.4f, 0.9f, 0.7f), new(-0.6f, 0.95f, 0.95f),
        new(0.2f, 0.3f, 0.3f), new(0.7f, 0.5f, 0.1f), new(-0.3f, 0.6f, 0.8f), new(0.8f, 0.4f, 0.05f),
    };
    public static readonly string[] ShotNames = { "새벽 골목(평온)", "그림자의 기척(의심)", "옥상 추격(추격)", "충돌(절정)", "빗속의 숨(여운)", "재회(희망)", "배신(긴장)", "엔딩(해소)" };

    private readonly object _gate = new();
    private readonly MultiAgentVirtualProductionCrewOrchestratorEngine _crew;
    private readonly IServiceProvider? _sp;
    private readonly CrewShotRecord[] _records = new CrewShotRecord[MaxRecords];
    private int _recHead, _recCount, _shotIndex, _conflictsInShot;
    private CancellationTokenSource? _cts;
    private long _frame;

    public MultiAgentCrewStudioRunner(IServiceProvider sp)
        : this((MultiAgentVirtualProductionCrewOrchestratorEngine?)sp.GetService(typeof(MultiAgentVirtualProductionCrewOrchestratorEngine)) ?? Build(sp), sp) { }

    public MultiAgentCrewStudioRunner(MultiAgentVirtualProductionCrewOrchestratorEngine crew, IServiceProvider? sp = null)
    {
        _crew = crew; _sp = sp;
        NewProduction();
    }

    private static MultiAgentVirtualProductionCrewOrchestratorEngine Build(IServiceProvider sp)
    {
        var g = (RealtimeProductionSwarmExecutionGuard?)sp.GetService(typeof(RealtimeProductionSwarmExecutionGuard)) ?? new RealtimeProductionSwarmExecutionGuard();
        if (g.Count == 0) g.RegisterStudios(sp);
        return new MultiAgentVirtualProductionCrewOrchestratorEngine((AutonomousDirectorialConsensusEvaluator?)sp.GetService(typeof(AutonomousDirectorialConsensusEvaluator)) ?? new AutonomousDirectorialConsensusEvaluator(), g);
    }

    public event Action? OnChange;

    public MultiAgentVirtualProductionCrewOrchestratorEngine Crew => _crew;
    public AutonomousDirectorialConsensusEvaluator Consensus => _crew.Consensus;
    public RealtimeProductionSwarmExecutionGuard Guard => _crew.Guard;
    public bool IsRunning => _cts is not null;
    public long Frame { get { lock (_gate) return _frame; } }
    public int ShotIndex => _shotIndex;
    public bool ApplyToModules { get; set; }
    public bool AutoAdvance { get; set; } = true;
    public int RecordCount => _recCount;
    public CrewShotRecord Record(int back) => _records[(_recHead - 1 - back + 2 * MaxRecords) % MaxRecords];
    public double LastTickMs { get; private set; }
    public long ModuleApplies { get; private set; }
    public string? LastAction { get; private set; }

    public void NewProduction()
    {
        lock (_gate)
        {
            _shotIndex = 0; _recHead = _recCount = 0; _frame = 0; _conflictsInShot = 0;
            _crew.BeginShot(0, Script[0]);
        }
        LastAction = "🎬 새 프로덕션 — " + ShotNames[0];
        Raise();
    }

    /// <summary>수동 샷 점프(제어 평면).</summary>
    public void JumpToShot(int s)
    {
        lock (_gate) { _shotIndex = ((s % Script.Length) + Script.Length) % Script.Length; _conflictsInShot = 0; _crew.BeginShot(_shotIndex, Script[_shotIndex]); }
        LastAction = "⏭ 샷 " + (_shotIndex + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + ShotNames[_shotIndex];
        Raise();
    }

    /// <summary>1-Click 오버라이드(실단위 · null = 해제).</summary>
    public void Override(CrewTopic t, float? value)
    {
        _crew.Consensus.Override(t, value);
        LastAction = value is { } v ? "✋ 오버라이드 " + MultiAgentVirtualProductionCrewOrchestratorEngine.TopicNames[(int)t] + " = " + v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "↺ 오버라이드 해제 " + MultiAgentVirtualProductionCrewOrchestratorEngine.TopicNames[(int)t];
        Raise();
    }

    /// <summary>1틱(무할당): 크루 스텝 → 샷 완료 시 기록 · (선택) 모듈 결선 · 다음 샷.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Crew.Tick");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _crew.Step();
            _conflictsInShot += _crew.Tick % _crew.ConsensusInterval == 1 ? _crew.Consensus.Conflicts : 0;
            if (_crew.ShotComplete && AutoAdvance)
            {
                var c = _crew.Consensus;
                _records[_recHead] = new CrewShotRecord(_shotIndex, _crew.Target, c.Predicted, c.LastLossAfter, _crew.LastMakespanSec, _crew.PlannedMakespanSec, _conflictsInShot, c.BeatsPerCut);
                _recHead = (_recHead + 1) % MaxRecords; if (_recCount < MaxRecords) _recCount++;
                if (ApplyToModules) Apply();
                _shotIndex = (_shotIndex + 1) % Script.Length; _conflictsInShot = 0;
                _crew.BeginShot(_shotIndex, Script[_shotIndex]);
            }
            _frame++;
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    /// <summary>결정 → 실제 모듈(샷 전환 시 1회 · 미등록 모듈 생략).</summary>
    private void Apply()
    {
        if (_sp is null) return;
        var e = _crew.Target;
        if (_sp.GetService(typeof(FilmScore.FilmScoreStudioRunner)) is FilmScore.FilmScoreStudioRunner fs) { fs.SetCue(e.Valence, e.Arousal, e.Tension); ModuleApplies++; }
        if (_sp.GetService(typeof(Cinematography.NeuralCinematographyStudioRunner)) is Cinematography.NeuralCinematographyStudioRunner cn) { cn.SetDutch(_crew.DecidedValue(CrewTopic.Dutch)); ModuleApplies++; }
        if (_sp.GetService(typeof(Atmosphere.GenerativeEnvironmentStudioRunner)) is Atmosphere.GenerativeEnvironmentStudioRunner env) { env.SetFog(_crew.DecidedValue(CrewTopic.Haze)); ModuleApplies++; }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 크루 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Crew.Tick", ex); }
                if ((Frame & 7) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Crew.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
