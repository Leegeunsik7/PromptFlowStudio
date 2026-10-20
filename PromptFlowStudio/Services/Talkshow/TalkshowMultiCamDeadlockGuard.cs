using PromptFlowStudio.Services.CinematicDirector;

namespace PromptFlowStudio.Services.Talkshow;

public enum TalkShot : byte { Wide, TwoShot, SpeakerBust, OverShoulder, Reaction }

/// <summary>
/// [PromptFlow Studio v17.65.0] 토크쇼 자동 멀티캠 스위처 &amp; 동시 발화 데드락 세이프티 가드(무할당 핫패스).
///   ▸ 발언권 중재: 점수 = 역할(호스트 1.0 · 게스트 0.6) + 토픽 오너 0.8 + 대기 보너스(≤ 0.3) − 발언 점유 초과 페널티(0.6 × 공정 몫 초과분) · 동점 = 낮은 인덱스(호스트).
///     바지인은 도전자 점수 &gt; 화자 점수 + 0.15(화자 현직 가산 0.2 · 최소 발언 800 ms 보호) 일 때만 인터럽트, 아니면 도전자 경청 강제.
///   ▸ 데드락: 침묵 <see cref="DeadlockMs"/> 초과 → 토픽 오너(지정 시) 또는 호스트(직전 화자가 호스트면 첫 게스트)가 강제 발언권.
///   ▸ 멀티캠: v17.39 <see cref="RealtimeCinematicShotFramingEngine"/>(마스터 투샷 · OTS A/B · 액션 바스트/리액션) 4 캠 — 좌석 순(좌 → 우)으로 쌍을 고정해 180° 축 유지.
///     컷 규칙: 인계 → 화자 OTS/바스트 교대 · 오버랩/데드락 → 투샷/와이드 · 독백 5 s → 투샷 · 청자 놀람 → 리액션 CU(≤ 1.5 s) · 모든 컷 최소 유지 <see cref="MinHoldS"/>(대기 컷은 유지 충족 즉시 실행).
/// </summary>
public sealed class TalkshowMultiCamDeadlockGuard
{
    public const int MaxAgents = RealtimeTurnTakingFsmEngine.MaxAgents;
    public const float MinHoldS = 0.35f, MonologueS = 5f, ReactionMaxS = 1.5f, BargeMargin = 0.15f, Incumbent = 0.2f, TopicBonus = 0.8f, MinTurnMs = 800f, CutDelayS = 0.12f;

    private readonly TalkRole[] _role = { TalkRole.Host, TalkRole.Guest, TalkRole.Guest };
    private readonly int[] _rank = { 1, 0, 2 };
    private readonly DirectorActor[] _actors = new DirectorActor[MaxAgents];
    private int _active = 3;
    // 라이브/대기 컷
    private TalkShot _shot = TalkShot.Wide, _pendShot;
    private DirectorCam _cam = DirectorCam.Master;
    private int _pairA = 1, _pairB = 2, _subject = -1, _pendSubject, _pendOther, _speaker = -1, _addressee = -1, _bustToggle;
    private double _liveSince = double.NaN, _pendAt, _pendReady;
    private bool _pending, _deferred;

    public TalkshowMultiCamDeadlockGuard() { }

    public RealtimeCinematicShotFramingEngine Framing { get; } = new() { LineSide = 1f };
    public float DeadlockMs { get; set; } = 1200f;
    public int TopicOwner { get; set; } = -1;
    public long OverlapsResolved { get; private set; }
    public long BargeIns { get; private set; }
    public long BargeRejected { get; private set; }
    public long Deadlocks { get; private set; }
    public long Cuts { get; private set; }
    public long DeferredCuts { get; private set; }
    public float MinObservedHoldS { get; private set; } = float.MaxValue;
    public float LastCutLatencyMs { get; private set; }
    public TalkShot Shot => _shot;
    public DirectorCam LiveCam => _cam;
    public int PairA => _pairA;
    public int PairB => _pairB;
    public int Subject => _subject;
    public bool HasPending => _pending;
    public TalkShot PendingShot => _pendShot;
    public ref readonly CameraRig Program => ref Framing.Rig((int)_cam);
    public ref readonly DirectorActor Actor(int a) => ref _actors[a];
    public TalkRole Role(int a) => _role[a];
    public int Active { get => _active; set => _active = Math.Clamp(value, 2, MaxAgents); }
    public double ShotAgeS(double nowMs) => double.IsNaN(_liveSince) ? 0 : (nowMs - _liveSince) / 1000.0;

    public static string ShotName(TalkShot s) => s switch { TalkShot.Wide => "와이드(3인)", TalkShot.TwoShot => "투샷", TalkShot.SpeakerBust => "화자 바스트", TalkShot.OverShoulder => "OTS", _ => "리액션 CU" };

    /// <summary>좌석 순위(좌 → 우 = 0, 1, 2) — 180° 축 고정용.</summary>
    public void SetSeatRank(int a, int rank) => _rank[a] = rank;
    public void SetActor(int a, in DirectorActor actor) => _actors[a] = actor;

    public void ResetMetrics() { OverlapsResolved = BargeIns = BargeRejected = Deadlocks = Cuts = DeferredCuts = 0; MinObservedHoldS = float.MaxValue; LastCutLatencyMs = 0; }

    // ══════════════════════ 발언권 중재(무할당) ══════════════════════
    public float Score(int a, ReadOnlySpan<double> talkMs, int agents, double waitMs)
    {
        double total = 0; for (var i = 0; i < agents; i++) total += talkMs[i];
        var share = total > 1 ? (float)(talkMs[a] / total) : 1f / agents;
        var s = (_role[a] == TalkRole.Host ? 1f : 0.6f) + (a == TopicOwner ? TopicBonus : 0f) + 0.3f * Math.Clamp((float)(waitMs / 2000.0), 0f, 1f);
        return s - 0.6f * MathF.Max(0f, share - 1f / agents);
    }

    /// <summary>동시 요청 중 1 명 선택(<paramref name="contenders"/> = 요청 수 · 2 이상이면 나머지 경청 강제).</summary>
    public int Resolve(ReadOnlySpan<bool> req, ReadOnlySpan<double> reqAt, ReadOnlySpan<double> talkMs, int agents, double nowMs, int exclude, out int contenders, out float best)
    {
        var w = -1; best = float.MinValue; contenders = 0;
        for (var a = 0; a < agents; a++)
        {
            if (!req[a] || a == exclude) continue;
            contenders++;
            var s = Score(a, talkMs, agents, nowMs - reqAt[a]);
            if (s > best + 1e-5f) { best = s; w = a; }
        }
        if (contenders > 1) OverlapsResolved++;
        if (w < 0) best = 0f;
        return w;
    }

    /// <summary>화자 ↔ 끼어든 도전자 즉시 판정 → 승자.</summary>
    public int ResolvePair(int holder, int challenger, ReadOnlySpan<double> talkMs, int agents, double turnAgeMs, double waitMs, out float challengerScore)
    {
        challengerScore = Score(challenger, talkMs, agents, waitMs);
        var hs = Score(holder, talkMs, agents, 0) + Incumbent + (turnAgeMs < MinTurnMs ? 10f : 0f);
        OverlapsResolved++;
        if (challengerScore > hs + BargeMargin) { BargeIns++; return challenger; }
        BargeRejected++;
        return holder;
    }

    public int BreakDeadlock(int agents, int lastSpeaker)
    {
        Deadlocks++;
        if ((uint)TopicOwner < (uint)agents && TopicOwner != lastSpeaker) return TopicOwner;
        if (lastSpeaker != 0) return 0;
        return agents > 1 ? 1 : 0;
    }

    // ══════════════════════ 멀티캠 컷(무할당) ══════════════════════
    /// <summary>턴 이벤트 → 컷 요청(FSM OnTurn 결선).</summary>
    public void OnTurn(in TurnEvent e)
    {
        if (e.Kind == TurnEventKind.OverlapResolved || e.Kind == TurnEventKind.DeadlockBreak)
        {
            RequestShot(_active > 2 && e.Kind == TurnEventKind.DeadlockBreak ? TalkShot.Wide : TalkShot.TwoShot, e.Agent, e.From >= 0 ? e.From : Other(e.Agent), e.AtMs, 0);
            _bustToggle = 1;
            return;
        }
        if (e.Kind != TurnEventKind.Granted) return;
        _speaker = e.Agent;
        if (_pending && (_pendShot == TalkShot.TwoShot || _pendShot == TalkShot.Wide) && e.AtMs - _pendAt < 1) return;
        var other = e.From >= 0 && e.From != e.Agent ? e.From : Other(e.Agent);
        RequestShot((_bustToggle++ & 1) == 0 ? TalkShot.OverShoulder : TalkShot.SpeakerBust, e.Agent, other, e.AtMs, CutDelayS);
    }

    public void SetAddressee(int speaker, int addressee) { _speaker = speaker; _addressee = addressee; }

    /// <summary>청자 리액션(놀람) 큐 → 리액션 CU.</summary>
    public void ReactionCue(int listener, double nowMs)
    {
        if (_speaker < 0 || listener == _speaker || _shot == TalkShot.Reaction || ShotAgeS(nowMs) < 1.0) return;
        RequestShot(TalkShot.Reaction, listener, _speaker, nowMs, 0);
    }

    public void RequestShot(TalkShot shot, int subject, int other, double nowMs, double delayS)
    {
        if ((uint)subject >= (uint)_active) return;
        if ((uint)other >= (uint)_active || other == subject) other = Other(subject);
        _pendShot = shot; _pendSubject = subject; _pendOther = other; _pendAt = nowMs; _pendReady = nowMs + delayS * 1000.0;
        _pending = true; _deferred = false;
    }

    private int Other(int a) { if (_addressee >= 0 && _addressee != a && _addressee < _active) return _addressee; for (var i = 0; i < _active; i++) if (i != a) return i; return 0; }

    /// <summary>프레임 — 정책(독백 · 리액션 복귀) · 대기 컷 실행(최소 유지) · v17.39 프레이밍 갱신.</summary>
    public void StepCamera(double nowMs, float dt)
    {
        if (double.IsNaN(_liveSince)) { Execute(TalkShot.Wide, 0, 0, nowMs); }
        var age = ShotAgeS(nowMs);
        if (!_pending && _speaker >= 0)
        {
            if (_shot == TalkShot.Reaction && age >= ReactionMaxS) RequestShot((_bustToggle & 1) == 0 ? TalkShot.SpeakerBust : TalkShot.OverShoulder, _speaker, Other(_speaker), nowMs, 0);
            else if ((_shot == TalkShot.SpeakerBust || _shot == TalkShot.OverShoulder) && age >= MonologueS) RequestShot(_active > 2 ? TalkShot.Wide : TalkShot.TwoShot, _speaker, Other(_speaker), nowMs, 0);
            else if ((_shot == TalkShot.TwoShot || _shot == TalkShot.Wide) && age >= 4.0) RequestShot((_bustToggle++ & 1) == 0 ? TalkShot.OverShoulder : TalkShot.SpeakerBust, _speaker, Other(_speaker), nowMs, 0);
        }
        if (_pending && nowMs >= _pendReady)
        {
            if (age >= MinHoldS) { _pending = false; Execute(_pendShot, _pendSubject, _pendOther, nowMs); }
            else if (!_deferred) { _deferred = true; DeferredCuts++; }
        }
        Framing.A = _actors[_pairA];
        Framing.B = _actors[_pairB];
        Framing.Update(dt);
    }

    private void Execute(TalkShot shot, int subject, int other, double nowMs)
    {
        int a, b;
        if (shot == TalkShot.Wide)
        {
            a = -1; b = -1;
            for (var i = 0; i < _active; i++) { if (a < 0 || _rank[i] < _rank[a]) a = i; if (b < 0 || _rank[i] > _rank[b]) b = i; }
        }
        else if (_rank[subject] <= _rank[other]) { a = subject; b = other; }
        else { a = other; b = subject; }
        var subjIsA = subject == a;
        var cam = shot switch
        {
            TalkShot.OverShoulder => subjIsA ? DirectorCam.CamA : DirectorCam.CamB,
            TalkShot.SpeakerBust or TalkShot.Reaction => DirectorCam.Action,
            _ => DirectorCam.Master,
        };
        Framing.ActionSubject = (byte)(subjIsA ? 0 : 1);
        Framing.ActionSize = shot == TalkShot.Reaction ? ShotSize.CloseUp : ShotSize.MediumClose;
        if (!double.IsNaN(_liveSince))
        {
            var hold = (float)((nowMs - _liveSince) / 1000.0);
            if (hold < MinObservedHoldS) MinObservedHoldS = hold;
            Cuts++;
            LastCutLatencyMs = (float)(nowMs - _pendAt);
        }
        _shot = shot; _cam = cam; _pairA = a; _pairB = b; _subject = subject; _liveSince = nowMs;
        Framing.Snap((int)cam);
    }
}
