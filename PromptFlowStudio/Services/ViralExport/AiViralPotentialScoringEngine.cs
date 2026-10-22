using PromptFlowStudio.Services.LiveClipper;

namespace PromptFlowStudio.Services.ViralExport;

public enum ViralGrade : byte { C, B, A, S }

/// <summary>v17.85.0 60 fps 바이럴 신호 프레임(값 형식).</summary>
public struct ViralFrame
{
    public long TUs;
    public float PitchHz, Rms, Motion, Luma;
    public AvatarEmotionTag Emotion;
    public bool ZoomPunch, BRollCut, Sticker;
}

/// <summary>v17.85.0 4 축 바이럴 포텐셜 판정(각 0..100).</summary>
public struct ViralScore
{
    public float Hook, Emotion, Visual, Loop, Total, PitchSemis, RmsRangeDb, LoopSimilarity;
    public int Punches, Inflections, VisualEvents, InBand, Alternations, Frames;
    public ViralGrade Grade;
    public bool Priority;
}

/// <summary>
/// [PromptFlow Studio v17.85.0] 📊 AI 바이럴 포텐셜 예측 스코어러 — <see cref="Push"/> 60 fps 무할당(고정 배열 · 분기 몇 개).
///   ① 훅 리텐션(35 %): 도입 3 s 음성 피치 변조 폭(반음 · 7 st 만점) + 키네틱 줌 펀치 빈도(2 회 만점).
///   ② 감정 롤러코스터(25 %): 200 ms RMS 엔벨로프 p10–p90 다이내믹 레인지(18 dB 만점) + 감정/에너지 변곡점(10 s 당 3 회 만점).
///   ③ 시각적 환기(20 %): B-Roll · 벡터 스티커 이벤트 간격 1.5–2.5 s 적중률 + 교차율.
///   ④ 종결 루프성(20 %): 마지막 0.5 s ↔ 첫 0.5 s 음량 · 피치 · 모션 · 밝기 · 감정 유사도(Seamless Loop).
///   ▸ 등급 S ≥ 90 · A ≥ 80 · B ≥ 70 · C &lt; 70 — S/A = 우선 배포 플래그.
/// </summary>
public sealed class AiViralPotentialScoringEngine
{
    public const long HookUs = 3_000_000, LoopUs = 500_000, RmsTauUs = 200_000;
    public const int Bins = 64, TailCap = 64, Fps = 60;
    public const float MinDb = -60f, HookSemis = 7f, HookPunches = 2f, RangeDbFull = 18f, InflectPer10s = 3f, SwingDb = 6f;
    public const float WHook = 0.35f, WEmotion = 0.25f, WVisual = 0.20f, WLoop = 0.20f;

    private readonly int[] _hist = new int[Bins];
    private readonly ViralFrame[] _tail = new ViralFrame[TailCap];
    private ViralFrame _head;
    private int _headN, _tailHead, _tailN, _frames, _punches, _inflect, _events, _inBand, _alt;
    private long _t0 = -1, _lastT, _lastEventUs = -1;
    private float _pMin = float.MaxValue, _pMax, _env = MinDb, _swingRef = MinDb, _credit;
    private sbyte _swingDir;
    private bool _lastWasBRoll;
    private AvatarEmotionTag _emo;

    public int Frames => _frames;
    public ViralScore Last { get; private set; }
    public long Scored { get; private set; }

    public void Reset()
    {
        Array.Clear(_hist);
        _head = default; _headN = _tailHead = _tailN = _frames = _punches = _inflect = _events = _inBand = _alt = 0;
        _t0 = -1; _lastT = 0; _lastEventUs = -1;
        _pMin = float.MaxValue; _pMax = 0; _env = _swingRef = MinDb; _credit = 0; _swingDir = 0; _lastWasBRoll = false;
        _emo = AvatarEmotionTag.Neutral;
    }

    /// <summary>프레임 1 개 누적(무할당).</summary>
    public void Push(in ViralFrame f)
    {
        if (_t0 < 0) { _t0 = f.TUs; _lastT = f.TUs; _emo = f.Emotion; }
        var t = f.TUs - _t0;
        var dt = Math.Max(1, f.TUs - _lastT); _lastT = f.TUs;
        _frames++;

        // ① 훅 — 도입 3 s
        if (t < HookUs)
        {
            if (f.PitchHz > 50f) { if (f.PitchHz < _pMin) _pMin = f.PitchHz; if (f.PitchHz > _pMax) _pMax = f.PitchHz; }
            if (f.ZoomPunch) _punches++;
        }

        // ② 감정 — RMS 엔벨로프(EMA τ 200 ms) dB 히스토그램 + 스윙 변곡(6 dB 히스테리시스) + 감정 전이
        var db = f.Rms > 1e-6f ? 20f * MathF.Log10(f.Rms) : MinDb;
        if (db < MinDb) db = MinDb;
        var a = 1f - MathF.Exp(-dt / (float)RmsTauUs);
        _env += (db - _env) * a;
        _hist[Math.Clamp((int)((_env - MinDb) / -MinDb * Bins), 0, Bins - 1)]++;
        if (_swingDir >= 0 && _env > _swingRef) _swingRef = _env;
        else if (_swingDir <= 0 && _env < _swingRef) _swingRef = _env;
        if (_swingDir >= 0 && _env < _swingRef - SwingDb) { if (_swingDir > 0) _inflect++; _swingDir = -1; _swingRef = _env; }
        else if (_swingDir <= 0 && _env > _swingRef + SwingDb) { if (_swingDir < 0) _inflect++; _swingDir = 1; _swingRef = _env; }
        if (f.Emotion != _emo) { _inflect++; _emo = f.Emotion; }

        // ③ 시각적 환기 — 이벤트 간격 밴드 적중 · B-Roll ↔ 스티커 교차
        if (f.BRollCut || f.Sticker)
        {
            if (_lastEventUs >= 0)
            {
                var gap = (t - _lastEventUs) / 1e6f;
                if (gap is >= 1.5f and <= 2.5f) { _inBand++; _credit += 1f; }
                else if (gap is >= 1.0f and <= 3.5f) _credit += 0.5f;
                if (f.BRollCut != _lastWasBRoll) _alt++;
            }
            _events++; _lastEventUs = t; _lastWasBRoll = f.BRollCut;
        }

        // ④ 루프 — 머리 0.5 s 평균 · 꼬리 고정 링
        if (t < LoopUs)
        {
            _headN++;
            var k = 1f / _headN;
            _head.Rms += (f.Rms - _head.Rms) * k; _head.PitchHz += (f.PitchHz - _head.PitchHz) * k;
            _head.Motion += (f.Motion - _head.Motion) * k; _head.Luma += (f.Luma - _head.Luma) * k;
            if (_headN == 1) _head.Emotion = f.Emotion;
        }
        _tail[_tailHead] = f; _tailHead = (_tailHead + 1) % TailCap; if (_tailN < TailCap) _tailN++;
    }

    /// <summary>누적 신호 → 4 축 점수 · 등급 · 우선 배포 플래그(무할당).</summary>
    public ViralScore Evaluate()
    {
        var r = new ViralScore { Frames = _frames, Punches = _punches, Inflections = _inflect, VisualEvents = _events, InBand = _inBand, Alternations = _alt };
        if (_frames < 2) { Last = r; return r; }
        var durS = Math.Max(0.5f, (_lastT - _t0) / 1e6f);

        var semis = _pMax > _pMin && _pMin < float.MaxValue ? 12f * MathF.Log2(_pMax / _pMin) : 0f;
        r.PitchSemis = semis;
        r.Hook = 100f * (0.55f * Sat(semis / HookSemis) + 0.45f * Sat(_punches / HookPunches));

        r.RmsRangeDb = Percentile(0.9f) - Percentile(0.1f);
        r.Emotion = 100f * (0.5f * Sat(r.RmsRangeDb / RangeDbFull) + 0.5f * Sat(_inflect / (InflectPer10s * durS / 10f)));

        var expected = Math.Max(1f, durS / 2f - 1f);
        var cadence = Sat(_credit / expected);
        var alternation = _events > 1 ? Sat(_alt / (float)(_events - 1) / 0.5f) : 0f;
        r.Visual = 100f * (0.75f * cadence + 0.25f * alternation);

        // 꼬리 0.5 s 평균
        var tailFrames = 0; ViralFrame tail = default; var tailEmo = AvatarEmotionTag.Neutral;
        for (var i = 0; i < _tailN; i++)
        {
            ref readonly var f = ref _tail[(_tailHead - 1 - i + TailCap) % TailCap];
            if (_lastT - f.TUs > LoopUs) break;
            tailFrames++;
            tail.Rms += f.Rms; tail.PitchHz += f.PitchHz; tail.Motion += f.Motion; tail.Luma += f.Luma;
            if (i == 0) tailEmo = f.Emotion;
        }
        if (tailFrames > 0 && _headN > 0)
        {
            var inv = 1f / tailFrames;
            var dRms = Rel(tail.Rms * inv, _head.Rms); var dPitch = Rel(tail.PitchHz * inv, _head.PitchHz);
            var dMot = MathF.Abs(tail.Motion * inv - _head.Motion); var dLuma = MathF.Abs(tail.Luma * inv - _head.Luma);
            var sim = 1f - (0.3f * dRms + 0.2f * dPitch + 0.2f * Sat(dMot) + 0.2f * Sat(dLuma) + 0.1f * (tailEmo == _head.Emotion ? 0f : 1f));
            r.LoopSimilarity = Sat(sim);
            r.Loop = 100f * r.LoopSimilarity;
        }

        r.Total = Math.Clamp(WHook * r.Hook + WEmotion * r.Emotion + WVisual * r.Visual + WLoop * r.Loop, 0f, 100f);
        r.Grade = GradeOf(r.Total);
        r.Priority = r.Grade >= ViralGrade.A;
        Last = r; Scored++;
        return r;
    }

    public static ViralGrade GradeOf(float total) => total >= 90f ? ViralGrade.S : total >= 80f ? ViralGrade.A : total >= 70f ? ViralGrade.B : ViralGrade.C;

    public static string GradeName(ViralGrade g) => g switch { ViralGrade.S => "S", ViralGrade.A => "A", ViralGrade.B => "B", _ => "C" };

    private float Percentile(float q)
    {
        var target = q * _frames; var acc = 0;
        for (var i = 0; i < Bins; i++)
        {
            acc += _hist[i];
            if (acc >= target) return MinDb + (i + 0.5f) * (-MinDb / Bins);
        }
        return 0f;
    }

    private static float Sat(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    private static float Rel(float a, float b) { var m = MathF.Max(MathF.Abs(a), MathF.Abs(b)); return m < 1e-6f ? 0f : Sat(MathF.Abs(a - b) / m); }
}
