namespace PromptFlowStudio.Services.ProductionCrew;

public enum CrewRole : byte { Director, Cinematographer, Gaffer, SoundDesigner, VfxSupervisor, Editor, ScriptSupervisor }
public enum CrewTopic : byte { KeyIntensity, ColorTemp, FocalLength, CameraSpeed, Dutch, CutLength, MusicTempo, MusicIntensity, VfxDensity, Haze }
public enum CrewMsgKind : byte { Intent, Proposal, Decision, TaskAssign, TaskDone, Report, Override, Throttle }
public enum CrewTaskKind : byte { Blocking, LightingSetup, CameraRehearsal, ContinuityPrep, TakeCapture, SoundCue, VfxPass, EditAssembly, ContinuitySignoff }
public enum CrewTaskState : byte { Waiting, Ready, Running, Done }

/// <summary>버스 메시지(값 형식 · 32 B 내외): 순번 · 틱 · 발신 · 수신(255 = 전체) · 종류 · 주제/작업 · 값 · 신뢰도.</summary>
public readonly record struct CrewMessage(long Seq, long Tick, CrewRole From, byte To, CrewMsgKind Kind, byte Topic, float Value, float Confidence);

/// <summary>장면 감정 목표: 정서가 −1..1 · 각성 0..1 · 긴장 0..1.</summary>
public readonly record struct EmotionTarget(float Valence, float Arousal, float Tension);

/// <summary>작업(값 형식 · 선할당 배열): 종류 · 담당 역할 · 선행 비트마스크 · 작업량 · 진척 · 상태 · 임계 경로 우선순위.</summary>
public struct CrewTask
{
    public CrewTaskKind Kind;
    public CrewRole Owner;
    public uint Deps;
    public float Work, Done, Priority;
    public CrewTaskState State;
    public long StartTick, EndTick;
}

/// <summary>
/// 무할당 메시지 버스: 고정 링(1024) · 순번 단조 · 에이전트별 커서 · 수신 필터(대상 = 본인 ∨ 전체) · 커서가 링을 추월당하면 손실 집계.
/// </summary>
public sealed class CrewMessageBus
{
    public const int Capacity = 1024, Broadcast = 255;
    private readonly CrewMessage[] _ring = new CrewMessage[Capacity];
    private readonly long[] _cursor = new long[MultiAgentVirtualProductionCrewOrchestratorEngine.Agents];
    private long _seq;

    public long Published => _seq;
    public long Dropped { get; private set; }
    public long Delivered { get; private set; }
    public CrewMessage Recent(int back) => _ring[(int)((_seq - 1 - back) % Capacity + Capacity) % Capacity];

    public void Reset() { _seq = 0; Array.Clear(_cursor); Dropped = 0; Delivered = 0; }

    public long Publish(long tick, CrewRole from, byte to, CrewMsgKind kind, byte topic, float value, float conf)
    {
        var s = _seq++;
        _ring[(int)(s % Capacity)] = new CrewMessage(s, tick, from, to, kind, topic, value, conf);
        return s;
    }

    /// <summary>수신(무할당): 커서 이후 메시지 중 본인/전체 대상 → dst · 반환 = 개수.</summary>
    public int Poll(CrewRole agent, Span<CrewMessage> dst)
    {
        var a = (int)agent; var c = _cursor[a];
        if (_seq - c > Capacity) { Dropped += _seq - c - Capacity; c = _seq - Capacity; }
        var n = 0;
        for (; c < _seq && n < dst.Length; c++)
        {
            ref readonly var m = ref _ring[(int)(c % Capacity)];
            if (m.To != Broadcast && m.To != a) continue;
            if (m.From == agent && m.Kind == CrewMsgKind.Proposal) continue;
            dst[n++] = m;
        }
        _cursor[a] = c;
        Delivered += n;
        return n;
    }
}

/// <summary>
/// [PromptFlow Studio v16.91.0] 🎬 멀티에이전트 가상 프로덕션 크루 오케스트레이션 엔진(60 Hz · 무할당 틱 · 규칙 기반 에이전트 · 학습 모델/LLM 미사용).
///   ▸ 크루 7(연출 · 촬영 · 조명 · 음향 · VFX · 편집 · 스크립트 수퍼바이저) · 무할당 메시지 버스(의도 → 제안 → 결정 브로드캐스트 · 작업 할당/완료 보고 · 스로틀).
///   ▸ 제안 정책: 역할별 도메인 목적(예: 조명 = 긴장 ↑ → 로우키 · 음향 = 각성 → 템포 · 편집 = 음악 박자 맞춤 컷 길이 · 스크립트 = 이전 샷 일치/축 유지 · VFX = 가드 헤드룸 상한) × 감정 목표.
///   ▸ 작업 분배: 샷 DAG 9작업(블로킹 → 조명/리허설/연속성 준비 → 테이크 → 음향/VFX → 편집 → 연속성 승인) · 임계 경로 우선순위 · 역할 용량(틱당) · 가드 스로틀 시 VFX 용량 감소 ·
///     메이크스팬/유휴율/계획 대비 지연.
///   ▸ 합의(결정)는 <see cref="AutonomousDirectorialConsensusEvaluator"/> · 실행 예산은 <see cref="RealtimeProductionSwarmExecutionGuard"/> 결선.
/// </summary>
public sealed class MultiAgentVirtualProductionCrewOrchestratorEngine
{
    public const int Agents = 7, Topics = 10, Tasks = 9;
    public static readonly string[] RoleNames = { "연출", "촬영", "조명", "음향", "VFX", "편집", "스크립트" };
    public static readonly string[] RoleIcons = { "🎬", "🎥", "💡", "🎧", "💥", "✂", "📋" };
    public static readonly string[] TopicNames = { "키 라이트", "색온도", "초점거리", "카메라 속도", "더치", "컷 길이", "음악 템포", "음악 강도", "VFX 밀도", "헤이즈" };
    public static readonly string[] TopicUnits = { "", "K", "mm", "m/s", "°", "s", "BPM", "", "", "" };
    public static readonly float[] TopicMin = { 0.1f, 2700f, 18f, 0f, 0f, 0.8f, 50f, 0f, 0f, 0f };
    public static readonly float[] TopicMax = { 1f, 7500f, 135f, 3f, 25f, 8f, 180f, 1f, 1f, 1f };
    public static readonly string[] TaskNames = { "블로킹", "조명 셋업", "카메라 리허설", "연속성 준비", "테이크 촬영", "음향 큐", "VFX 패스", "편집 어셈블리", "연속성 승인" };
    private static readonly CrewRole[] TaskOwner = { CrewRole.Director, CrewRole.Gaffer, CrewRole.Cinematographer, CrewRole.ScriptSupervisor, CrewRole.Cinematographer, CrewRole.SoundDesigner, CrewRole.VfxSupervisor, CrewRole.Editor, CrewRole.ScriptSupervisor };
    private static readonly uint[] TaskDeps = { 0, 1u << 0, 1u << 0, 1u << 0, (1u << 1) | (1u << 2) | (1u << 3), 1u << 0, 1u << 4, (1u << 4) | (1u << 5) | (1u << 6), 1u << 7 };
    private static readonly float[] TaskWork = { 30f, 45f, 40f, 20f, 60f, 35f, 55f, 50f, 15f };
    /// <summary>역할 × 주제 관여(제안 여부).</summary>
    private static readonly bool[,] Involved =
    {
        //            Key   CT    Foc   Spd   Dut   Cut   Tem   MInt  Vfx   Haze
        /* 연출 */  { true, true, true, false, true, true, true, true, true, false },
        /* 촬영 */  { true, false, true, true, true, false, false, false, false, false },
        /* 조명 */  { true, true, false, false, false, false, false, false, false, true },
        /* 음향 */  { false, false, false, false, false, true, true, true, false, false },
        /* VFX */   { false, false, false, true, false, false, false, false, true, true },
        /* 편집 */  { false, true, false, false, false, true, true, true, false, false },
        /* 스크립트 */ { false, false, true, true, true, false, false, false, false, false },
    };

    private readonly object _gate = new();
    private readonly CrewMessageBus _bus = new();
    private readonly CrewTask[] _tasks = new CrewTask[Tasks];
    private readonly float[] _proposal = new float[Agents * Topics], _confidence = new float[Agents * Topics];
    private readonly bool[] _has = new bool[Agents * Topics];
    private readonly float[] _decided = new float[Topics], _prevShotDecided = new float[Topics];
    private readonly float[] _capacity = new float[Agents], _busy = new float[Agents], _baseCapacity = { 2.4f, 1.6f, 1.6f, 1.4f, 1.6f, 1.6f, 1.2f };
    private readonly CrewMessage[] _inbox = new CrewMessage[64];
    private readonly AutonomousDirectorialConsensusEvaluator _consensus;
    private readonly RealtimeProductionSwarmExecutionGuard _guard;
    private long _tick, _shotStartTick, _idleTicks, _agentTicks;
    private int _shot;
    private EmotionTarget _target;

    public MultiAgentVirtualProductionCrewOrchestratorEngine(AutonomousDirectorialConsensusEvaluator consensus, RealtimeProductionSwarmExecutionGuard guard)
    {
        _consensus = consensus; _guard = guard;
        for (var k = 0; k < Topics; k++) _decided[k] = _prevShotDecided[k] = 0.5f;
        BeginShot(0, new EmotionTarget(0.3f, 0.3f, 0.2f));
    }

    public CrewMessageBus Bus => _bus;
    public AutonomousDirectorialConsensusEvaluator Consensus => _consensus;
    public RealtimeProductionSwarmExecutionGuard Guard => _guard;
    public long Tick => _tick;
    public int Shot => _shot;
    public EmotionTarget Target => _target;
    public int ConsensusInterval { get; set; } = 6;
    public bool ShotComplete { get; private set; }
    public long ShotsCompleted { get; private set; }
    public float LastMakespanSec { get; private set; }
    public float PlannedMakespanSec { get; private set; }
    public float UtilizationPct => _agentTicks > 0 ? 100f * (1f - _idleTicks / (float)_agentTicks) : 0f;
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public ref readonly CrewTask Task(int i) => ref _tasks[i];
    /// <summary>결정값(정규화 0..1).</summary>
    public float Decided(CrewTopic t) => _decided[(int)t];
    public float Proposal(CrewRole r, CrewTopic t) => _proposal[(int)r * Topics + (int)t];
    public bool HasProposal(CrewRole r, CrewTopic t) => _has[(int)r * Topics + (int)t];
    public float Busy(CrewRole r) => _busy[(int)r];
    public float Capacity(CrewRole r) => _capacity[(int)r];
    public static bool IsInvolved(CrewRole r, CrewTopic t) => Involved[(int)r, (int)t];
    public static float Normalize(int topic, float v) => (v - TopicMin[topic]) / (TopicMax[topic] - TopicMin[topic]);
    public static float Denormalize(int topic, float u) => TopicMin[topic] + Math.Clamp(u, 0f, 1f) * (TopicMax[topic] - TopicMin[topic]);
    public static float DenormalizeSpan(CrewTopic t, ReadOnlySpan<float> x) => Denormalize((int)t, x[(int)t]);

    /// <summary>새 샷(무할당): 감정 목표 · DAG 초기화 · 임계 경로 우선순위 · 계획 메이크스팬(임계 경로 ÷ 용량).</summary>
    public void BeginShot(int shot, EmotionTarget target)
    {
        lock (_gate)
        {
            _shot = shot; _target = target; _shotStartTick = _tick; ShotComplete = false;
            _decided.AsSpan().CopyTo(_prevShotDecided);
            for (var i = 0; i < Tasks; i++)
                _tasks[i] = new CrewTask { Kind = (CrewTaskKind)i, Owner = TaskOwner[i], Deps = TaskDeps[i], Work = TaskWork[i], State = TaskDeps[i] == 0 ? CrewTaskState.Ready : CrewTaskState.Waiting, StartTick = -1, EndTick = -1 };
            // 임계 경로(역위상 · 남은 최장 경로 = 우선순위) — DAG 인덱스 위상 정렬 가정
            for (var i = Tasks - 1; i >= 0; i--)
            {
                var longest = 0f;
                for (var j = i + 1; j < Tasks; j++) if ((_tasks[j].Deps & (1u << i)) != 0) longest = MathF.Max(longest, _tasks[j].Priority);
                _tasks[i].Priority = _tasks[i].Work / _baseCapacity[(int)_tasks[i].Owner] + longest;
            }
            PlannedMakespanSec = _tasks[0].Priority / 60f;
            _bus.Publish(_tick, CrewRole.Director, CrewMessageBus.Broadcast, CrewMsgKind.Intent, 0, target.Arousal, target.Tension);
        }
    }

    /// <summary>역할별 제안 정책(정규화 0..1 · 신뢰도) — 감정 목표 + 이전 샷 + 현재 결정 + 가드 헤드룸.</summary>
    private void Propose()
    {
        float v = _target.Valence, a = _target.Arousal, t = _target.Tension;
        var head = _guard.Headroom;
        Array.Clear(_has);
        void P(CrewRole r, CrewTopic k, float u, float c) { var i = (int)r * Topics + (int)k; _proposal[i] = Math.Clamp(u, 0f, 1f); _confidence[i] = c; _has[i] = true; }
        float N(CrewTopic k, float val) => Normalize((int)k, val);
        var tempoNow = DecidedValue(CrewTopic.MusicTempo); var cutNow = DecidedValue(CrewTopic.CutLength);
        // 연출: 감정 목표 직역(전 주제 균형)
        P(CrewRole.Director, CrewTopic.KeyIntensity, 0.75f - 0.45f * t + 0.15f * v, 0.8f);
        P(CrewRole.Director, CrewTopic.ColorTemp, N(CrewTopic.ColorTemp, 5400f - 1400f * v + 500f * t), 0.7f);
        P(CrewRole.Director, CrewTopic.FocalLength, N(CrewTopic.FocalLength, 40f + 55f * t), 0.6f);
        P(CrewRole.Director, CrewTopic.Dutch, N(CrewTopic.Dutch, 14f * t * t), 0.6f);
        P(CrewRole.Director, CrewTopic.CutLength, N(CrewTopic.CutLength, 5.5f - 3.8f * a), 0.7f);
        P(CrewRole.Director, CrewTopic.MusicTempo, N(CrewTopic.MusicTempo, 70f + 90f * a), 0.6f);
        P(CrewRole.Director, CrewTopic.MusicIntensity, 0.25f + 0.45f * t + 0.25f * a, 0.7f);
        P(CrewRole.Director, CrewTopic.VfxDensity, 0.15f + 0.55f * a, 0.5f);
        // 촬영: 각성 → 카메라 속도 · 긴장 → 망원/더치 · 노출 확보(밝은 쪽)
        P(CrewRole.Cinematographer, CrewTopic.KeyIntensity, 0.7f - 0.25f * t, 0.6f);
        P(CrewRole.Cinematographer, CrewTopic.FocalLength, N(CrewTopic.FocalLength, 32f + 70f * t + 20f * (1f - a)), 0.9f);
        P(CrewRole.Cinematographer, CrewTopic.CameraSpeed, N(CrewTopic.CameraSpeed, 0.3f + 2.4f * a), 0.9f);
        P(CrewRole.Cinematographer, CrewTopic.Dutch, N(CrewTopic.Dutch, 22f * MathF.Max(0f, t - 0.4f)), 0.8f);
        // 조명: 긴장 → 로우키 · 정서가 → 웜 · 볼류메트릭 헤이즈
        P(CrewRole.Gaffer, CrewTopic.KeyIntensity, 0.9f - 0.7f * t + 0.1f * v, 1f);
        P(CrewRole.Gaffer, CrewTopic.ColorTemp, N(CrewTopic.ColorTemp, 5200f - 1800f * v + 900f * t), 1f);
        P(CrewRole.Gaffer, CrewTopic.Haze, 0.1f + 0.6f * t, 0.9f);
        // 음향: 각성 → 템포 · 긴장/각성 → 강도 · 컷 = 8박 정렬 희망
        P(CrewRole.SoundDesigner, CrewTopic.MusicTempo, N(CrewTopic.MusicTempo, 60f + 105f * a + 15f * t), 1f);
        P(CrewRole.SoundDesigner, CrewTopic.MusicIntensity, 0.15f + 0.55f * a + 0.3f * t, 1f);
        P(CrewRole.SoundDesigner, CrewTopic.CutLength, N(CrewTopic.CutLength, 8f * 60f / MathF.Max(tempoNow, 50f)), 0.6f);
        // VFX: 각성 → 밀도(가드 헤드룸 상한) · 카메라 속도 ↓(모션 블러 비용) · 헤이즈 연동
        P(CrewRole.VfxSupervisor, CrewTopic.VfxDensity, MathF.Min(0.25f + 0.7f * a, head), 0.9f);
        P(CrewRole.VfxSupervisor, CrewTopic.CameraSpeed, N(CrewTopic.CameraSpeed, 1.2f), 0.4f);
        P(CrewRole.VfxSupervisor, CrewTopic.Haze, 0.15f + 0.5f * t, 0.6f);
        // 편집: 각성 → 짧은 컷 · 템포 = 컷당 4박 · 대사 명료도 → 음악 강도 ↓ · 색 일관(중립 5600 K)
        P(CrewRole.Editor, CrewTopic.CutLength, N(CrewTopic.CutLength, 6.5f - 5f * a), 1f);
        P(CrewRole.Editor, CrewTopic.MusicTempo, N(CrewTopic.MusicTempo, 240f / MathF.Max(cutNow, 0.8f)), 0.6f);
        P(CrewRole.Editor, CrewTopic.MusicIntensity, 0.35f + 0.3f * a, 0.5f);
        P(CrewRole.Editor, CrewTopic.ColorTemp, N(CrewTopic.ColorTemp, 5600f), 0.4f);
        // 스크립트: 이전 샷 일치(초점거리) · 저속 카메라 · 더치 0(축/시선 연속성)
        P(CrewRole.ScriptSupervisor, CrewTopic.FocalLength, _prevShotDecided[(int)CrewTopic.FocalLength], 0.7f);
        P(CrewRole.ScriptSupervisor, CrewTopic.CameraSpeed, N(CrewTopic.CameraSpeed, 0.4f), 0.8f);
        P(CrewRole.ScriptSupervisor, CrewTopic.Dutch, 0f, 0.8f);
        for (var r = 0; r < Agents; r++)
            for (var k = 0; k < Topics; k++)
                if (_has[r * Topics + k]) _bus.Publish(_tick, (CrewRole)r, CrewMessageBus.Broadcast, CrewMsgKind.Proposal, (byte)k, _proposal[r * Topics + k], _confidence[r * Topics + k]);
    }

    /// <summary>작업 스케줄(무할당): 준비 작업 우선순위 순 → 역할 용량 분배 → 진척 · 완료 시 후속 준비 · 버스 보고.</summary>
    private void Schedule()
    {
        var throttle = _guard.VfxThrottle;
        for (var r = 0; r < Agents; r++) { _capacity[r] = _baseCapacity[r] * (r == (int)CrewRole.VfxSupervisor ? 1f - 0.7f * throttle : 1f); _busy[r] = 0f; }
        for (var i = 0; i < Tasks; i++)
        {
            if (_tasks[i].State != CrewTaskState.Waiting) continue;
            var ok = true; for (var j = 0; j < Tasks; j++) if ((_tasks[i].Deps & (1u << j)) != 0 && _tasks[j].State != CrewTaskState.Done) { ok = false; break; }
            if (ok) _tasks[i].State = CrewTaskState.Ready;
        }
        // 역할별 우선순위 최상위부터 용량 소진(한 역할 여러 작업 가능)
        for (var pass = 0; pass < Tasks; pass++)
        {
            var best = -1; var bp = -1f;
            for (var i = 0; i < Tasks; i++)
            {
                if (_tasks[i].State is not (CrewTaskState.Ready or CrewTaskState.Running)) continue;
                var o = (int)_tasks[i].Owner;
                if (_busy[o] >= _capacity[o] - 1e-4f) continue;
                if (_tasks[i].Priority > bp && !Served(i)) { bp = _tasks[i].Priority; best = i; }
            }
            if (best < 0) break;
            ref var tk = ref _tasks[best];
            var own = (int)tk.Owner;
            var give = MathF.Min(_capacity[own] - _busy[own], tk.Work - tk.Done);
            if (tk.State == CrewTaskState.Ready) { tk.State = CrewTaskState.Running; tk.StartTick = _tick; _bus.Publish(_tick, CrewRole.Director, (byte)own, CrewMsgKind.TaskAssign, (byte)best, tk.Work, tk.Priority); }
            tk.Done += give; _busy[own] += give;
            MarkServed(best);
            if (tk.Done >= tk.Work - 1e-4f) { tk.State = CrewTaskState.Done; tk.EndTick = _tick; _bus.Publish(_tick, tk.Owner, CrewMessageBus.Broadcast, CrewMsgKind.TaskDone, (byte)best, 1f, 1f); }
        }
        ClearServed();
        for (var r = 0; r < Agents; r++) { _agentTicks++; if (_busy[r] < 1e-4f) _idleTicks++; }
        var all = true; for (var i = 0; i < Tasks; i++) if (_tasks[i].State != CrewTaskState.Done) { all = false; break; }
        if (all && !ShotComplete)
        {
            ShotComplete = true; ShotsCompleted++;
            LastMakespanSec = (_tick - _shotStartTick + 1) / 60f;
            _bus.Publish(_tick, CrewRole.ScriptSupervisor, CrewMessageBus.Broadcast, CrewMsgKind.Report, (byte)_shot, LastMakespanSec, 1f);
        }
    }

    private uint _served;
    private bool Served(int i) => (_served & (1u << i)) != 0;
    private void MarkServed(int i) => _served |= 1u << i;
    private void ClearServed() => _served = 0;

    /// <summary>1틱(무할당): 에이전트 수신 → (주기) 제안 → 합의 → 결정 브로드캐스트 → 작업 스케줄 → 가드 스캔 → 스로틀 통지.</summary>
    public void Step()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var r = 0; r < Agents; r++) _bus.Poll((CrewRole)r, _inbox);
            if (_tick % ConsensusInterval == 0)
            {
                Propose();
                _consensus.Resolve(_target, _proposal, _confidence, _has, _decided, _tick, _shot);
                for (var k = 0; k < Topics; k++) _bus.Publish(_tick, CrewRole.Director, CrewMessageBus.Broadcast, CrewMsgKind.Decision, (byte)k, _decided[k], 1f);
            }
            Schedule();
            var prevThrottle = _guard.VfxThrottle;
            _guard.Scan(_tick, _decided[(int)CrewTopic.VfxDensity]);
            if (_guard.VfxThrottle > prevThrottle + 0.05f) _bus.Publish(_tick, CrewRole.VfxSupervisor, CrewMessageBus.Broadcast, CrewMsgKind.Throttle, (byte)CrewTopic.VfxDensity, _guard.VfxThrottle, 1f);
            _tick++;
            var ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            LastTickMs = ms; if (ms > MaxTickMs) MaxTickMs = ms;
        }
    }

    /// <summary>결정값(실단위).</summary>
    public float DecidedValue(CrewTopic t) => Denormalize((int)t, _decided[(int)t]);
}
