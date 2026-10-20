using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Atmosphere;
using PromptFlowStudio.Services.Cinematography;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.FilmScore;

namespace PromptFlowStudio.Services.HoloTelepresence;

/// <summary>분기 사유: 시작 · 비트 완료 · 관객 고조 · 몰입 하락 · 정서 반전 · 수동.</summary>
public enum DirectorReason : byte { Start, BeatComplete, AudienceSurge, EngagementDrop, SentimentFlip, Manual }

/// <summary>디렉터 오케스트레이션 큐(값 형식 · 무할당): 카메라(v16.85) · 조명(v16.84) · 음악(v16.86) · 표정(v16.82).</summary>
public readonly record struct DirectorCue(
    ShotScale Scale, CameraMove Move, float FocalMm, float DutchDeg, float FStop, float OrbitRateDeg,
    float KeyLux, float FillRatio, float RimLux, float ColorTempK, float FogDensity, float SunElevationDeg,
    float Valence, float Arousal, float Tension, float Bpm, FacePreset Face);

/// <summary>분기 결정 기록(값 형식).</summary>
public readonly record struct BranchDecision(long Tick, int From, int To, DirectorReason Reason, float Score);

/// <summary>
/// [PromptFlow Studio v17.00.0] 🎬 범용 AGI 자율 시네마틱 디렉터 &amp; 실시간 동적 서사 분기 합성기(무할당 · 결정적).
///   ▸ 서사 그래프: 비트(최대 32 · 목표 정서가/각성/긴장 · 지속) + 분기 간선(비트당 ≤ 3) — 기본 = 홀로그래픽 라이브 텔레프레전스 쇼 8비트.
///   ▸ 관객 감응: 몰입/정서/환호 EWMA → 템포 배율(몰입 낮음 = 전개 가속) · 감정 목표 보정 · 조기 분기(몰입 하락 3 s · 환호 급등 · 정서 반전).
///   ▸ 분기 선택: 관객 희망 정서(정서가 = 관객 정서 · 각성 = 환호 + 재후킹)와 자식 비트 목표의 정합 − 최근 방문 반복 감점 → argmax.
///   ▸ 무단절 전개: 감정 상태 = 목표로의 지수 접근(τ 1.2 s) — 분기 시 계단 없음(틱당 변화 상한 계측) · 1.5 s 크로스페이드 · 음악 스팅어 컷.
///   ▸ 오케스트레이션: 강도(각성·긴장) → 샷 스케일/초점거리/조리개 · 긴장 → 더치/핸드헬드 · 각성 → 오빗 속도 · 정서가 → 색온도/태양 고도 ·
///     긴장 → 키:필 대비/안개 · v16.85 카메라 리그 · v16.84 대기 조명(4 Hz) · v16.86 BGM 큐/컷 · v16.82 표정 감정 혼합 결선(선택).
/// </summary>
public sealed class UniversalAgiCinematicDirectorSynthesizer
{
    public const int MaxBeats = 32, MaxChildren = 3, LogSize = 32, PathSize = 16;
    public const float EmotionTau = 1.2f, CrossfadeS = 1.5f, DropThreshold = 0.3f, DropHoldS = 3f, SurgeThreshold = 0.85f;
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly string[] _name = new string[MaxBeats];
    private readonly float[] _bv = new float[MaxBeats], _ba = new float[MaxBeats], _bt = new float[MaxBeats], _bd = new float[MaxBeats];
    private readonly float[] _lx = new float[MaxBeats], _ly = new float[MaxBeats];
    private readonly int[] _child = new int[MaxBeats * MaxChildren], _childN = new int[MaxBeats], _visits = new int[MaxBeats];
    private readonly BranchDecision[] _log = new BranchDecision[LogSize];
    private readonly int[] _path = new int[PathSize];
    private int _beats, _cur, _logHead, _logCount, _pathHead, _pathCount, _applyCounter;
    private long _tick;
    private float _elapsed, _dropTime, _fade, _prevSentiment;
    private float _v, _a, _t, _faceV = float.NaN, _faceA, _faceF;
    private NeuralCameraTrajectoryEngine? _cam;
    private AtmosphericScatteringLightingEngine? _atmo;
    private NeuralCinematographicFilmScoreGenerator? _score;
    private NeuralFacialMuscleSimulator? _face;

    public UniversalAgiCinematicDirectorSynthesizer() => BuildDefaultStory();

    // ── 관객(EWMA) ──
    public float Engagement { get; private set; } = 0.6f;
    public float Sentiment { get; private set; } = 0.2f;
    public float Excitement { get; private set; } = 0.4f;
    // ── 상태 ──
    public int BeatCount => _beats;
    public int CurrentBeat => _cur;
    public float BeatElapsed => _elapsed;
    public float BeatProgress => _beats == 0 ? 0f : Math.Clamp(_elapsed / _bd[_cur], 0f, 1f);
    public float Tempo { get; private set; } = 1f;
    public float Crossfade => _fade;
    public float Valence => _v;
    public float Arousal => _a;
    public float Tension => _t;
    public DirectorCue Cue { get; private set; }
    public long Ticks => _tick;
    public long Branches { get; private set; }
    public long EarlyBranches { get; private set; }
    public float MaxEmotionStep { get; private set; }
    public double LastDirectUs { get; private set; }
    public double MaxDirectUs { get; private set; }
    public bool Autonomous { get; set; } = true;
    public bool CameraBound => _cam is not null;
    public bool LightingBound => _atmo is not null;
    public bool ScoreBound => _score is not null;
    public bool FaceBound => _face is not null;
    // ── 그래프 조회 ──
    public string BeatName(int b) => _name[b];
    public float BeatValence(int b) => _bv[b];
    public float BeatArousal(int b) => _ba[b];
    public float BeatTension(int b) => _bt[b];
    public float BeatDuration(int b) => _bd[b];
    public float LayoutX(int b) => _lx[b];
    public float LayoutY(int b) => _ly[b];
    public int ChildCount(int b) => _childN[b];
    public int Child(int b, int k) => _child[b * MaxChildren + k];
    public int Visits(int b) => _visits[b];
    public int LogCount => _logCount;
    public BranchDecision LogEntry(int back) => _log[(_logHead - 1 - back + 2 * LogSize) % LogSize];
    public int PathCount => _pathCount;
    public int PathBeat(int back) => _path[(_pathHead - 1 - back + 2 * PathSize) % PathSize];

    public static string ReasonName(DirectorReason r) => r switch
    {
        DirectorReason.Start => "시작", DirectorReason.BeatComplete => "비트 완료", DirectorReason.AudienceSurge => "관객 고조",
        DirectorReason.EngagementDrop => "몰입 하락", DirectorReason.SentimentFlip => "정서 반전", _ => "수동",
    };

    public static string ScaleName(ShotScale s) => s switch
    {
        ShotScale.Wide => "와이드", ShotScale.Full => "풀", ShotScale.Medium => "미디엄", ShotScale.MediumCloseUp => "미디엄 클로즈업", _ => "클로즈업",
    };

    public static string MoveName(CameraMove m) => m switch
    {
        CameraMove.Follow => "팔로우", CameraMove.Orbit => "오빗", CameraMove.Dolly => "돌리", CameraMove.Crane => "크레인", _ => "핸드헬드",
    };

    /// <summary>v16.85 카메라 · v16.84 조명 · v16.86 음악 · v16.82 표정 엔진 결선(null = 미결선 · 큐만 산출).</summary>
    public void Bind(NeuralCameraTrajectoryEngine? cam, AtmosphericScatteringLightingEngine? atmo, NeuralCinematographicFilmScoreGenerator? score, NeuralFacialMuscleSimulator? face)
    {
        lock (_gate) { _cam = cam; _atmo = atmo; _score = score; _face = face; }
    }

    public void ClearStory()
    {
        lock (_gate) { _beats = 0; Array.Clear(_childN); Array.Clear(_visits); _logCount = 0; _pathCount = 0; }
    }

    public int AddBeat(string name, float valence, float arousal, float tension, float durationS, float layoutX, float layoutY)
    {
        lock (_gate)
        {
            if (_beats >= MaxBeats) return -1;
            var b = _beats++;
            _name[b] = name; _bv[b] = Math.Clamp(valence, -1f, 1f); _ba[b] = Math.Clamp(arousal, 0f, 1f); _bt[b] = Math.Clamp(tension, 0f, 1f);
            _bd[b] = MathF.Max(1f, durationS); _lx[b] = layoutX; _ly[b] = layoutY; _childN[b] = 0;
            return b;
        }
    }

    public bool Link(int from, int to)
    {
        lock (_gate)
        {
            if ((uint)from >= (uint)_beats || (uint)to >= (uint)_beats || _childN[from] >= MaxChildren) return false;
            _child[from * MaxChildren + _childN[from]++] = to;
            return true;
        }
    }

    private void BuildDefaultStory()
    {
        ClearStory();
        var b0 = AddBeat("오프닝 등장", 0.3f, 0.4f, 0.2f, 8f, 0.08f, 0.5f);
        var b1 = AddBeat("친밀한 고백", 0.4f, 0.2f, 0.1f, 10f, 0.3f, 0.2f);
        var b2 = AddBeat("긴장 고조", -0.3f, 0.7f, 0.7f, 8f, 0.3f, 0.8f);
        var b3 = AddBeat("회상", -0.2f, 0.25f, 0.3f, 10f, 0.52f, 0.12f);
        var b4 = AddBeat("폭발적 클라이맥스", 0.8f, 1f, 0.6f, 8f, 0.52f, 0.5f);
        var b5 = AddBeat("반전 위기", -0.7f, 0.85f, 0.95f, 8f, 0.52f, 0.88f);
        var b6 = AddBeat("화해", 0.6f, 0.35f, 0.15f, 10f, 0.74f, 0.3f);
        var b7 = AddBeat("피날레", 0.9f, 0.9f, 0.3f, 10f, 0.93f, 0.5f);
        Link(b0, b1); Link(b0, b2);
        Link(b1, b3); Link(b1, b4);
        Link(b2, b4); Link(b2, b5);
        Link(b3, b6);
        Link(b4, b6); Link(b4, b7);
        Link(b5, b4); Link(b5, b6);
        Link(b6, b7);
        Link(b7, b0);
        Restart();
    }

    public void ResetStory() => BuildDefaultStory();

    public void Restart()
    {
        lock (_gate)
        {
            _cur = 0; _elapsed = 0f; _fade = 0f; _dropTime = 0f;
            if (_beats == 0) return;
            _v = _bv[0]; _a = _ba[0]; _t = _bt[0];
            _visits[0]++; PushPath(0);
            Record(-1, 0, DirectorReason.Start, 1f);
            Cue = Derive();
        }
    }

    /// <summary>관객 피드백(몰입 0..1 · 정서 −1..1 · 환호 0..1) — EWMA α 0.1.</summary>
    public void SubmitAudience(float engagement, float sentiment, float excitement)
    {
        lock (_gate)
        {
            Engagement += (Math.Clamp(engagement, 0f, 1f) - Engagement) * 0.1f;
            Sentiment += (Math.Clamp(sentiment, -1f, 1f) - Sentiment) * 0.1f;
            Excitement += (Math.Clamp(excitement, 0f, 1f) - Excitement) * 0.1f;
        }
    }

    /// <summary>자식 비트 점수: 관객 희망 정서 정합 − 최근 방문 반복 감점.</summary>
    public float ScoreChild(int child)
    {
        var wantA = Math.Clamp(0.25f + 0.75f * Excitement + (Engagement < 0.45f ? 0.35f : 0f), 0f, 1f);
        var wantV = Sentiment;
        var s = 1f - 0.45f * MathF.Abs(_bv[child] - wantV) - 0.8f * MathF.Abs(_ba[child] - wantA);
        for (var k = 0; k < _pathCount && k < 4; k++) if (PathBeat(k) == child) { s -= 0.3f; break; }
        return s;
    }

    private void Branch(DirectorReason reason, int forced)
    {
        if (_childN[_cur] == 0) { _elapsed = 0f; return; }
        int best = _child[_cur * MaxChildren]; var bs = float.NegativeInfinity;
        if (forced >= 0) { best = forced; bs = ScoreChild(forced); }
        else
            for (var k = 0; k < _childN[_cur]; k++)
            {
                var c = _child[_cur * MaxChildren + k];
                var s = ScoreChild(c);
                if (s > bs) { bs = s; best = c; }
            }
        Record(_cur, best, reason, bs);
        _cur = best; _elapsed = 0f; _fade = 1f; _dropTime = 0f;
        _visits[best]++; PushPath(best);
        Branches++;
        if (reason is not DirectorReason.BeatComplete) EarlyBranches++;
        _score?.Cut(reason is not DirectorReason.BeatComplete);
    }

    /// <summary>수동 분기(자식 인덱스 k).</summary>
    public bool ForceBranch(int k)
    {
        lock (_gate)
        {
            if ((uint)k >= (uint)_childN[_cur]) return false;
            Branch(DirectorReason.Manual, _child[_cur * MaxChildren + k]);
            return true;
        }
    }

    private void Record(int from, int to, DirectorReason r, float score)
    {
        _log[_logHead] = new BranchDecision(_tick, from, to, r, score);
        _logHead = (_logHead + 1) % LogSize;
        if (_logCount < LogSize) _logCount++;
    }

    private void PushPath(int b)
    {
        _path[_pathHead] = b;
        _pathHead = (_pathHead + 1) % PathSize;
        if (_pathCount < PathSize) _pathCount++;
    }

    /// <summary>1틱(무할당): 템포 · 조기/정규 분기 · 감정 연속 접근 · 큐 산출 · 엔진 결선 적용.</summary>
    public void Tick(float dt)
    {
        lock (_gate)
        {
            if (_beats == 0) return;
            var t0 = Stopwatch.GetTimestamp();
            _tick++;
            Tempo = Math.Clamp(1f + (0.55f - Engagement) * 1.2f, 0.6f, 1.6f);
            _elapsed += dt * Tempo;
            _fade = MathF.Max(0f, _fade - dt / CrossfadeS);
            _dropTime = Engagement < DropThreshold ? _dropTime + dt : 0f;
            if (Autonomous)
            {
                var progress = _elapsed / _bd[_cur];
                if (progress >= 1f) Branch(DirectorReason.BeatComplete, -1);
                else if (progress > 0.4f && _dropTime >= DropHoldS) Branch(DirectorReason.EngagementDrop, -1);
                else if (progress > 0.25f && Excitement > SurgeThreshold && _ba[_cur] < 0.5f) Branch(DirectorReason.AudienceSurge, -1);
                else if (progress > 0.25f && MathF.Abs(Sentiment - _prevSentiment) > 0.9f && MathF.Sign(Sentiment) != MathF.Sign(_prevSentiment)) Branch(DirectorReason.SentimentFlip, -1);
            }
            if (MathF.Abs(Sentiment - _prevSentiment) > 0.9f || _tick % 60 == 0) _prevSentiment = Sentiment;
            // 감정 목표(비트 + 관객 보정) → 지수 접근(연속)
            var tv = Math.Clamp(_bv[_cur] + 0.2f * Sentiment, -1f, 1f);
            var ta = Math.Clamp(_ba[_cur] + 0.25f * (Excitement - 0.5f), 0f, 1f);
            var tt = Math.Clamp(_bt[_cur] + 0.15f * (0.5f - Engagement), 0f, 1f);
            var k = 1f - MathF.Exp(-dt / EmotionTau);
            float dv = (tv - _v) * k, da = (ta - _a) * k, dtt = (tt - _t) * k;
            _v += dv; _a += da; _t += dtt;
            var step = MathF.Max(MathF.Abs(dv), MathF.Max(MathF.Abs(da), MathF.Abs(dtt)));
            if (step > MaxEmotionStep) MaxEmotionStep = step;
            Cue = Derive();
            Apply(dt);
            LastDirectUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
            if (LastDirectUs > MaxDirectUs) MaxDirectUs = LastDirectUs;
        }
    }

    private DirectorCue Derive()
    {
        float v = _v, a = _a, t = _t;
        var intensity = 0.6f * a + 0.4f * t;
        var scale = intensity < 0.25f ? ShotScale.Wide : intensity < 0.45f ? ShotScale.Full : intensity < 0.6f ? ShotScale.Medium : intensity < 0.78f ? ShotScale.MediumCloseUp : ShotScale.CloseUp;
        var move = _fade > 0.6f ? CameraMove.Crane : t > 0.75f ? CameraMove.Handheld : a > 0.75f ? CameraMove.Orbit : v > 0.4f && a < 0.4f ? CameraMove.Dolly : CameraMove.Follow;
        var face = t > 0.6f && v < 0f ? FacePreset.Fear : v > 0.3f ? FacePreset.Joy : v < -0.3f ? (a > 0.55f ? FacePreset.Anger : FacePreset.Sadness) : a > 0.85f ? FacePreset.Surprise : FacePreset.Neutral;
        return new DirectorCue(
            scale, move,
            FocalMm: 24f + 76f * intensity,
            DutchDeg: t > 0.6f ? (t - 0.6f) * 30f : 0f,
            FStop: 5.6f - 4.2f * intensity,
            OrbitRateDeg: 6f + 30f * a,
            KeyLux: 400f + 800f * a,
            FillRatio: 1.5f + 6f * t,
            RimLux: 150f + 450f * a,
            ColorTempK: 5000f - 1800f * v,
            FogDensity: 0.002f + 0.02f * t * (v < 0f ? 1f : 0.4f),
            SunElevationDeg: 4f + 40f * (v + 1f) * 0.5f * (1f - 0.7f * t),
            Valence: v, Arousal: a, Tension: t,
            Bpm: 70f + 80f * a,
            Face: face);
    }

    private void Apply(float dt)
    {
        var c = Cue;
        if (_cam is { } cam)
        {
            cam.Move = c.Move; cam.Scale = c.Scale; cam.DutchDeg = c.DutchDeg; cam.FStop = c.FStop; cam.OrbitRateDeg = c.OrbitRateDeg; cam.AutoZoom = true;
        }
        _score?.SetCue(c.Valence, c.Arousal, c.Tension, c.Bpm);
        if (_face is { } face)
        {
            float fv = (c.Valence + 1f) * 0.5f, fa = c.Arousal, ff = c.Tension > 0.5f && c.Valence < 0f ? (c.Tension - 0.5f) * 2f : 0f;
            if (float.IsNaN(_faceV) || MathF.Abs(fv - _faceV) + MathF.Abs(fa - _faceA) + MathF.Abs(ff - _faceF) > 0.02f)
            {
                face.BlendEmotion(fv, fa, ff);
                _faceV = fv; _faceA = fa; _faceF = ff;
            }
        }
        if (_atmo is { } atmo && ++_applyCounter >= 15)
        {
            _applyCounter = 0;
            atmo.Update(AtmosphericScatteringLightingEngine.FromAngles(c.SunElevationDeg, 200f), AtmosphericScatteringLightingEngine.FromAngles(-30f, 20f), 0.5f,
                        c.Tension * 0.7f, 2f * c.Tension, c.FogDensity, 1f + 0.5f * MathF.Round(c.Tension * 4f), dt * 15f); // 연무 4단 양자화 = LUT 재구축 최소화
        }
    }

    /// <summary>강도(0..1) — HUD/UI 공용.</summary>
    public float Intensity => 0.6f * _a + 0.4f * _t;
    public Vector3 EmotionVector => new(_v, _a, _t);
}
