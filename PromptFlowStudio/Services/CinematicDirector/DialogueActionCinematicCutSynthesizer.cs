namespace PromptFlowStudio.Services.CinematicDirector;

public enum CutPacing : byte { Calm, Standard, Fast }
public enum CutReason : byte { Start, SpeakerChange, Reaction, ImpactInsert, Reestablish, Silence, Fallback, MusicBeat }

/// <summary>컷 기록(링버퍼).</summary>
public readonly record struct CutEvent(double TimeS, byte From, byte To, CutReason Reason, CutVerdict FirstVerdict, float AngleDeg, float LatencyMs);

/// <summary>
/// [PromptFlow Studio v17.39.0] ✂ 대화·액션 반응형 시네마틱 컷 자동 전환 합성기(<see cref="Step"/> 무할당 · 60 fps).
///   ▸ 발화 감지: 배우별 프레임 음량(v17.32 레인 RMS · dBFS) 슬라이딩 창 15 프레임(250 ms) 평균 · 히스테리시스(−38 / −46 dBFS) → 발화 시작 시각 기록 ·
///     화자 = 활성 중 창 평균 최대(동시 발화 시 직전 화자 유지 · 끼어들기는 6 dB 우세 필요).
///   ▸ 규칙(페이싱 Calm/Standard/Fast — 최소 샷 3.0/1.8/0.9 s · 최대 9/6/3.5 s · 리액션 1.6/1.2/0.8 s):
///     화자 전환 → 새 화자 OTS(배우 0 → Cam-A · 배우 1 → Cam-B) · 긴 독백(최대 샷 초과) → 청자 리액션(역 OTS) 후 복귀 ·
///     충격(v17.37 임펄스 에너지 ≥ 20 J) → 즉시 액션 인서트(CU · ≥ 150 J = ECU · 최소 샷 무시 · 0.25 s 가드 간격) 1.2 s 후 마스터 재확립 · 침묵 ≥ 2.5 s → 마스터.
///   ▸ 가드 심사: 거부 사유별 대안 — 점프컷 → 액션 샷 한 단계 조임/다른 카메라 · 측 위반 → 마스터 · 간격/상한 → 보류(다음 프레임 재시도 · 최대 1 s).
///   ▸ 스무스 컷: 충격 인서트 = 하드 컷 · 일반 = 하드 컷 + 다음 카메라 사전 정착(Snap) · 마스터 재확립 = 6 프레임(100 ms) 크로스 디졸브 가중.
///   ▸ 지연: 발화 시작 → 컷 확정(ms) 기록 · 링 64.
/// </summary>
public sealed class DialogueActionCinematicCutSynthesizer
{
    public const int Window = 15, Ring = 64, Actors = 2;
    public const float OnDb = -38f, OffDb = -46f, ImpactJ = 20f, BigImpactJ = 150f, DissolveS = 0.1f, SilenceS = 2.5f, InsertS = 1.2f;

    private readonly float[] _win = new float[Actors * Window], _mean = new float[Actors];
    private readonly bool[] _active = new bool[Actors];
    private readonly double[] _onset = new double[Actors];
    private readonly CutEvent[] _log = new CutEvent[Ring];
    private int _wHead, _logHead, _logN, _speaker = -1, _pending = -1, _returnTo = -1;
    private double _now, _shotStart, _lastSpeech, _pendingSince, _pendingOnset, _impactAt = double.NegativeInfinity;
    private CutReason _pendingReason;
    private bool _pendingImpact, _reaction;
    private CutVerdict _firstVerdict;
    private float _impactJ;

    public CutPacing Pacing { get; set; } = CutPacing.Standard;
    /// <summary>v17.53 비트 그리드 — 설정 시 충격/시작 외 컷을 비트 ±40 ms 로 양자화(최대 0.6 s 대기 후 강행).</summary>
    public PromptFlowStudio.Services.BeatSync.IBeatClock? BeatClock { get; set; }
    public long BeatAlignedCuts { get; private set; }
    public int Program { get; private set; }
    public int Previous { get; private set; }
    /// <summary>크로스 디졸브 진행(1 = 프로그램 완전).</summary>
    public float Blend { get; private set; } = 1f;
    public int Speaker => _speaker;
    public float LevelDb(int a) => _mean[a];
    public bool Speaking(int a) => _active[a];
    public double ShotSeconds => _now - _shotStart;
    public long Cuts { get; private set; }
    public long SpeakerCuts { get; private set; }
    public long ReactionCuts { get; private set; }
    public long ImpactInserts { get; private set; }
    public long Reestablish { get; private set; }
    public long Deferred { get; private set; }
    public long Alternates { get; private set; }
    /// <summary>v17.54 MV 리듬 시퀀서가 요청해 확정된 컷.</summary>
    public long MusicCuts { get; private set; }
    public float LastLatencyMs { get; private set; }
    public float LastImpactLatencyMs { get; private set; }
    public double TotalShotSeconds { get; private set; }
    public float MeanShotSeconds => Cuts > 0 ? (float)(TotalShotSeconds / Cuts) : 0f;
    public int LogCount => _logN;
    public CutEvent Log(int back) => _log[(_logHead - 1 - back + Ring * 2) % Ring];

    public float MinShot => Pacing switch { CutPacing.Calm => 3f, CutPacing.Fast => 0.9f, _ => 1.8f };
    public float MaxShot => Pacing switch { CutPacing.Calm => 9f, CutPacing.Fast => 3.5f, _ => 6f };
    public float ReactionS => Pacing switch { CutPacing.Calm => 1.6f, CutPacing.Fast => 0.8f, _ => 1.2f };
    public static string PacingName(CutPacing p) => p switch { CutPacing.Calm => "Calm", CutPacing.Fast => "Fast", _ => "Standard" };
    public static string ReasonName(CutReason r) => r switch
    {
        CutReason.Start => "시작", CutReason.SpeakerChange => "화자 전환", CutReason.Reaction => "리액션", CutReason.ImpactInsert => "충격 인서트",
        CutReason.Reestablish => "마스터 재확립", CutReason.Silence => "침묵 → 마스터", CutReason.MusicBeat => "음악 비트", _ => "대안",
    };

    public void ResetMetrics() { Cuts = SpeakerCuts = ReactionCuts = ImpactInserts = Reestablish = Deferred = Alternates = MusicCuts = 0; TotalShotSeconds = 0; _logN = 0; }

    /// <summary>충격 보고(v17.37 임펄스 에너지 J · 피격 배우).</summary>
    public void ReportImpact(float energyJ, byte actor, RealtimeCinematicShotFramingEngine eng)
    {
        if (!(energyJ >= ImpactJ)) return;
        _impactAt = _now; _impactJ = energyJ;
        eng.ActionSubject = actor;
        eng.ActionSize = energyJ >= BigImpactJ ? ShotSize.ExtremeCloseUp : ShotSize.CloseUp;
        Request((int)DirectorCam.Action, CutReason.ImpactInsert, true, _now);
    }

    /// <summary>v17.54 — 외부 음악 비트 컷 요청(최소 샷 · 비트 양자화 · 180° 가드 심사는 그대로 적용).</summary>
    public void RequestMusicCut(int cam) { if ((uint)cam < RealtimeCinematicShotFramingEngine.Cams) Request(cam, CutReason.MusicBeat, false, _now); }

    private void Request(int cam, CutReason reason, bool impact, double onset)
    {
        if (cam == Program && _pending < 0) return;
        if (_pending >= 0 && _pendingImpact && !impact) return;   // 충격 우선
        _pending = cam; _pendingReason = reason; _pendingImpact = impact; _pendingSince = _now; _pendingOnset = onset; _firstVerdict = CutVerdict.Approved;
    }

    // ══════════════════════ 프레임(무할당) ══════════════════════

    /// <summary>프레임 — 배우 음량(dBFS) 입력 → 컷 결정 → 가드 심사 → 확정.</summary>
    public void Step(float dt, float level0Db, float level1Db, RealtimeCinematicShotFramingEngine eng, CinematicAxisJumpCutSafetyGuard guard)
    {
        _now += dt;
        if (Cuts == 0 && _logN == 0) { Commit(eng, guard, (int)DirectorCam.Master, CutReason.Start, false, _now, CutVerdict.Approved); }
        // 슬라이딩 창
        _win[_wHead] = level0Db; _win[Window + _wHead] = level1Db;
        _wHead = (_wHead + 1) % Window;
        for (var a = 0; a < Actors; a++)
        {
            var s = 0f;
            for (var i = 0; i < Window; i++) s += _win[a * Window + i];
            _mean[a] = s / Window;
            var lv = a == 0 ? level0Db : level1Db;
            if (!_active[a] && _mean[a] > OnDb) { _active[a] = true; _onset[a] = _now - Window * dt * 0.5; }
            else if (_active[a] && _mean[a] < OffDb) _active[a] = false;
            if (lv > OffDb) _lastSpeech = _now;
        }
        // 화자 결정
        var cand = _speaker;
        if (_active[0] && _active[1]) { if (_speaker < 0) cand = _mean[0] >= _mean[1] ? 0 : 1; else if (_mean[1 - _speaker] > _mean[_speaker] + 6f) cand = 1 - _speaker; }
        else if (_active[0]) cand = 0;
        else if (_active[1]) cand = 1;
        var shot = _now - _shotStart;
        var insert = Program == (int)DirectorCam.Action && _now - _impactAt < InsertS + 0.5;
        if (cand >= 0 && cand != _speaker)
        {
            _speaker = cand; _reaction = false;
            if (!insert) Request(cand == 0 ? (int)DirectorCam.CamA : (int)DirectorCam.CamB, CutReason.SpeakerChange, false, _onset[cand]);
        }
        else if (_pending < 0 && !insert)
        {
            if (Program == (int)DirectorCam.Action && _now - _impactAt >= InsertS) Request((int)DirectorCam.Master, CutReason.Reestablish, false, _now);
            else if (_now - _lastSpeech > SilenceS && Program != (int)DirectorCam.Master && shot > MinShot) Request((int)DirectorCam.Master, CutReason.Silence, false, _now);
            else if (_speaker >= 0 && _active[_speaker] && shot > MaxShot && !_reaction)
            {
                _returnTo = Program; _reaction = true;
                Request(_speaker == 0 ? (int)DirectorCam.CamB : (int)DirectorCam.CamA, CutReason.Reaction, false, _now);
            }
            else if (_reaction && shot > ReactionS && _returnTo >= 0)
            {
                _reaction = false;
                var back = _speaker >= 0 && _active[_speaker] ? (_speaker == 0 ? (int)DirectorCam.CamA : (int)DirectorCam.CamB) : (int)DirectorCam.Master;
                Request(back, CutReason.SpeakerChange, false, _now);
                _returnTo = -1;
            }
        }
        // 보류 요청 처리(최소 샷 · 가드)
        if (_pending >= 0)
        {
            var ready = _pendingImpact || shot >= MinShot || _pendingReason == CutReason.Start;
            if (ready && !_pendingImpact && _pendingReason != CutReason.Start && BeatClock is { Locked: true } bc)
            {
                if (bc.NearBeat(0.04, false)) BeatAlignedCuts++;
                else if (_now - _pendingSince < 0.6) ready = false;
            }
            if (ready)
            {
                eng.Snap(_pending);
                var v = guard.Review(eng, Program, _pending, _now, _pendingImpact);
                if (_firstVerdict == CutVerdict.Approved && v != CutVerdict.Approved) _firstVerdict = v;
                if (v == CutVerdict.Approved) Commit(eng, guard, _pending, _pendingReason, _pendingImpact, _pendingOnset, _firstVerdict);
                else if (v == CutVerdict.JumpCut)
                {
                    // 대안: 액션이면 한 단계 조임 · 그 외 마스터(마스터가 프로그램이면 액션)
                    Alternates++;
                    if (_pending == (int)DirectorCam.Action && eng.ActionTighten < 1.5f) eng.ActionTighten = 2f;
                    else _pending = Program == (int)DirectorCam.Master ? (int)DirectorCam.Action : (int)DirectorCam.Master;
                }
                else if (v == CutVerdict.AxisViolation) { Alternates++; _pending = (int)DirectorCam.Master; }
                else
                {
                    Deferred++;
                    if (_now - _pendingSince > 1.0) _pending = -1;
                }
            }
        }
        // 디졸브
        Blend = Program == (int)DirectorCam.Master && Previous != Program && _log[(_logHead - 1 + Ring) % Ring].Reason == CutReason.Reestablish
            ? MathF.Min(1f, (float)((_now - _shotStart) / DissolveS)) : 1f;
        if (Program != (int)DirectorCam.Action && eng.ActionTighten > 1f && _now - _impactAt > InsertS) eng.ActionTighten = 1f;
        guard.Observe(eng, Program);
    }

    private void Commit(RealtimeCinematicShotFramingEngine eng, CinematicAxisJumpCutSafetyGuard guard, int to, CutReason reason, bool impact, double onset, CutVerdict first)
    {
        eng.Snap(to);
        CinematicAxisJumpCutSafetyGuard.Metrics(eng, Program, to, out var ang, out _);
        if (reason != CutReason.Start) guard.Commit(eng, Program, to, _now, impact);
        var lat = (float)((_now - onset) * 1000.0);
        if (reason == CutReason.SpeakerChange) { SpeakerCuts++; LastLatencyMs = lat; }
        else if (reason == CutReason.Reaction) ReactionCuts++;
        else if (reason == CutReason.ImpactInsert) { ImpactInserts++; LastImpactLatencyMs = (float)((_now - _impactAt) * 1000.0); lat = LastImpactLatencyMs; }
        else if (reason is CutReason.Reestablish or CutReason.Silence) Reestablish++;
        else if (reason == CutReason.MusicBeat) MusicCuts++;
        if (reason != CutReason.Start) { Cuts++; TotalShotSeconds += _now - _shotStart; }
        _log[_logHead] = new CutEvent(_now, (byte)Program, (byte)to, reason, first, ang, lat);
        _logHead = (_logHead + 1) % Ring; if (_logN < Ring) _logN++;
        Previous = Program; Program = to; _shotStart = _now; _pending = -1; _pendingImpact = false;
    }

}
