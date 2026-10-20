using System.Numerics;
using PromptFlowStudio.Services.AvatarActing;
using PromptFlowStudio.Services.DigitalHuman;

namespace PromptFlowStudio.Services.Talkshow;

/// <summary>v17.65.0 아바타 1 기 시선 · 리액션 출력(몸통 기준 라디안 · 0..1 가중치).</summary>
public struct GazePose
{
    public float HeadYaw, HeadPitch, HeadRoll, EyeYaw, EyePitch, Blink;
    public float Nod, Smile, Surprise, HandWeight;
    public GestureClass Hand;
    public Vector3 Target, GazeDir;
    /// <summary>주 대상(화자/청자 얼굴) 대비 실제 시선 오차(°) · 시선 분산 중 여부.</summary>
    public float ErrorDeg;
    public bool Averted;
    public int LookAt;
}

/// <summary>
/// [PromptFlow Studio v17.65.0] 상호 시선 추적 &amp; 경청 리액션 합성기(60 fps · 무할당).
///   ▸ 대상: 청자 → 화자 얼굴 · 화자 → 지목 청자(3 인 시 3~5 s 마다 다른 청자 0.8~1.4 s 순회) · 침묵 → 직전 화자/호스트.
///   ▸ 노려봄 방지: 시선 회피(청자 2.5~5 s 마다 0.3~0.6 s · 8° · 화자 1.8~4 s · 12°) · 미세 단속(0.4~1.2 s · ±1.2°) · 깜빡임(2.2~5.5 s · 150 ms · 큰 시선 이동 동반).
///   ▸ 운동학: 목표 방향 → 몸통 기준 yaw/pitch → 머리 70 %(반감기 180 ms · ±70°/±35°) + 안구 잔여(±30°/±20° · 30 ms 단속).
///   ▸ 경청 리액션(화자 감정 동조): 비트/정서가 → 끄덕임(감쇠 2.2 Hz · 쿨다운 0.9 s) · 정서가 → 미소 · 각성 급등(+0.22) → 놀람(눈썹/눈 크게/턱 · 머리 뒤로) ·
///     v17.60 수동작(고각성 긍정 = 포용 · 저각성 = 생각) → 52 블렌드셰이프 가중치(<see cref="WriteFace"/>).
/// </summary>
public sealed class MutualGazeListeningReactionSynthesizer
{
    public const int MaxAgents = RealtimeTurnTakingFsmEngine.MaxAgents;
    public const float HeadShare = 0.7f, HeadHalfLife = 0.18f, EyeHalfLife = 0.03f, MaxHeadYaw = 1.22f, MaxHeadPitch = 0.61f, MaxEyeYaw = 0.52f, MaxEyePitch = 0.35f;
    public const float SurpriseJump = 0.22f, NodCooldown = 0.9f, MutualDeg = 8f;
    private const float Deg = MathF.PI / 180f;

    private readonly GazePose[] _p = new GazePose[MaxAgents];
    private readonly Vector3[] _head = new Vector3[MaxAgents];
    private readonly float[] _bodyYaw = new float[MaxAgents];
    private readonly float[] _avertT = new float[MaxAgents], _avertLeft = new float[MaxAgents], _sacT = new float[MaxAgents], _blinkT = new float[MaxAgents], _blinkPhase = new float[MaxAgents];
    private readonly float[] _sacYaw = new float[MaxAgents], _sacPitch = new float[MaxAgents], _avYaw = new float[MaxAgents], _avPitch = new float[MaxAgents];
    private readonly float[] _nodPhase = new float[MaxAgents], _nodAmp = new float[MaxAgents], _nodCool = new float[MaxAgents], _surprise = new float[MaxAgents];
    private readonly float[] _lock = new float[MaxAgents], _glanceT = new float[MaxAgents], _glanceLeft = new float[MaxAgents];
    private readonly float[] _lastYaw = new float[MaxAgents], _blinkCool = new float[MaxAgents];
    private readonly int[] _glance = { -1, -1, -1 };
    private readonly uint[] _rng = { 0x9E3779B9u, 0x85EBCA6Bu, 0xC2B2AE35u };
    private float _arousalSlow = 0.4f;
    private int _surpriseCue = -1;

    public MutualGazeListeningReactionSynthesizer()
    {
        for (var a = 0; a < MaxAgents; a++) { _avertT[a] = 2f + a; _sacT[a] = 0.5f + 0.2f * a; _blinkT[a] = 2.5f + 0.7f * a; _glanceT[a] = 3.5f; }
    }

    public int Active { get; set; } = 3;
    public long Blinks { get; private set; }
    public long Aversions { get; private set; }
    public long Saccades { get; private set; }
    public long Nods { get; private set; }
    public long Surprises { get; private set; }
    /// <summary>얼굴 고정 응시 최장 연속(s) — 노려봄 지표.</summary>
    public float MaxLockS { get; private set; }
    public double LastStepUs { get; private set; }
    public ref readonly GazePose Pose(int a) => ref _p[a];
    public Vector3 Head(int a) => _head[a];
    public float BodyYaw(int a) => _bodyYaw[a];
    public bool Mutual(int a, int b) => _p[a].LookAt == b && _p[b].LookAt == a && !_p[a].Averted && !_p[b].Averted && _p[a].ErrorDeg < MutualDeg && _p[b].ErrorDeg < MutualDeg;

    public void SetSeat(int a, Vector3 head, float bodyYaw) { _head[a] = head; _bodyYaw[a] = bodyYaw; }

    public void ResetMetrics() { Blinks = Aversions = Saccades = Nods = Surprises = 0; MaxLockS = 0; }

    /// <summary>청자 놀람 큐 1 회 소비(멀티캠 리액션 컷).</summary>
    public bool TakeSurpriseCue(out int listener) { listener = _surpriseCue; _surpriseCue = -1; return listener >= 0; }

    private float Rand(int a) { var x = _rng[a]; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng[a] = x; return (x & 0xFFFFFF) / 16777215f; }
    private float Range(int a, float lo, float hi) => lo + (hi - lo) * Rand(a);

    // ══════════════════════ 프레임(무할당) ══════════════════════
    /// <param name="speaker">현재 화자(−1 = 침묵) · <paramref name="addressee"/> 지목 청자 · <paramref name="frame"/> 화자 v17.60 제스처 프레임(정서가 · 각성 · 비트).</param>
    public void Step(float dt, int speaker, int addressee, int fallback, in GestureFrame frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = Math.Clamp(Active, 2, MaxAgents);
        var valence = speaker >= 0 ? Math.Clamp(frame.Valence, -1f, 1f) : 0f;
        var arousal = speaker >= 0 ? Math.Clamp(frame.Arousal, 0f, 1f) : 0.2f;
        var jump = arousal - _arousalSlow;
        _arousalSlow += (arousal - _arousalSlow) * (1f - MathF.Exp(-dt / 1.2f));
        float kHead = 1f - MathF.Exp(-dt * 0.693f / HeadHalfLife), kEye = 1f - MathF.Exp(-dt * 0.693f / EyeHalfLife), kFace = 1f - MathF.Exp(-dt / 0.25f);
        for (var a = 0; a < n; a++)
        {
            ref var p = ref _p[a];
            var isSpeaker = a == speaker;
            // ① 주 대상
            int look;
            if (isSpeaker)
            {
                look = (uint)addressee < (uint)n && addressee != a ? addressee : (a == 0 ? 1 : 0);
                if (n > 2)
                {
                    if (_glance[a] >= 0) { _glanceLeft[a] -= dt; if (_glanceLeft[a] <= 0) _glance[a] = -1; else look = _glance[a]; }
                    else if ((_glanceT[a] -= dt) <= 0)
                    {
                        _glanceT[a] = Range(a, 3f, 5f); _glanceLeft[a] = Range(a, 0.8f, 1.4f);
                        for (var o = 0; o < n; o++) if (o != a && o != look) { _glance[a] = o; break; }
                    }
                }
            }
            else if (speaker >= 0) look = speaker;
            else look = fallback >= 0 && fallback != a ? fallback : (a == 0 ? 1 : 0);
            p.LookAt = look;
            var face = _head[look];
            // ② 시선 회피 · 미세 단속
            if (_avertLeft[a] > 0) { _avertLeft[a] -= dt; if (_avertLeft[a] <= 0) _avertT[a] = isSpeaker ? Range(a, 1.8f, 4f) : Range(a, 2.5f, 5f); }
            else if ((_avertT[a] -= dt) <= 0)
            {
                _avertLeft[a] = isSpeaker ? Range(a, 0.4f, 0.9f) : Range(a, 0.3f, 0.6f);
                var mag = (isSpeaker ? 12f : 8f) * Deg;
                _avYaw[a] = (Rand(a) < 0.5f ? -1f : 1f) * mag * 0.8f; _avPitch[a] = -mag * 0.6f;
                Aversions++;
            }
            if ((_sacT[a] -= dt) <= 0) { _sacT[a] = Range(a, 0.4f, 1.2f); _sacYaw[a] = Range(a, -1.2f, 1.2f) * Deg; _sacPitch[a] = Range(a, -0.8f, 0.8f) * Deg; Saccades++; }
            p.Averted = _avertLeft[a] > 0;
            // ③ 목표 각(몸통 기준)
            var d = face - _head[a];
            var tYaw = Wrap(MathF.Atan2(d.X, d.Z) - _bodyYaw[a]);
            var tPitch = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z));
            var gYaw = tYaw + _sacYaw[a] + (p.Averted ? _avYaw[a] : 0f);
            var gPitch = tPitch + _sacPitch[a] + (p.Averted ? _avPitch[a] : 0f);
            // ④ 경청 리액션(청자 · 화자 감정 동조)
            if (!isSpeaker && speaker >= 0)
            {
                _nodCool[a] -= dt;
                if (frame.Beat && valence > -0.4f && _nodCool[a] <= 0 && Rand(a) < 0.75f) { _nodPhase[a] = 0f; _nodAmp[a] = (5f + 4f * arousal) * Deg; _nodCool[a] = NodCooldown; Nods++; }
                if (jump > SurpriseJump && _surprise[a] < 0.2f) { _surprise[a] = 1f; Surprises++; if (_surpriseCue < 0) _surpriseCue = a; }
            }
            _surprise[a] = MathF.Max(0f, _surprise[a] - dt / 0.8f);
            float nod = 0f;
            if (_nodAmp[a] > 1e-4f) { _nodPhase[a] += dt; nod = -_nodAmp[a] * MathF.Sin(_nodPhase[a] * 2.2f * 2f * MathF.PI) * MathF.Exp(-_nodPhase[a] / 0.35f); if (_nodPhase[a] > 1.2f) _nodAmp[a] = 0f; }
            var smileT = isSpeaker ? MathF.Max(0f, valence) * 0.45f : 0.08f + MathF.Max(0f, valence) * 0.7f;
            p.Smile += (smileT - p.Smile) * kFace;
            p.Nod = nod; p.Surprise = _surprise[a];
            // ⑤ 머리(70 %) · 안구(잔여) 운동학
            var hYaw = Math.Clamp(gYaw * HeadShare, -MaxHeadYaw, MaxHeadYaw);
            var hPitch = Math.Clamp(gPitch * HeadShare, -MaxHeadPitch, MaxHeadPitch);
            p.HeadYaw += (hYaw - p.HeadYaw) * kHead;
            p.HeadPitch += (hPitch - p.HeadPitch) * kHead;
            p.HeadRoll += ((isSpeaker ? 0f : -0.05f * MathF.Sign(tYaw)) - p.HeadRoll) * kHead;
            var eYaw = Math.Clamp(gYaw - p.HeadYaw, -MaxEyeYaw, MaxEyeYaw);
            var ePitch = Math.Clamp(gPitch - (p.HeadPitch + nod - 3f * Deg * p.Surprise), -MaxEyePitch, MaxEyePitch);
            p.EyeYaw += (eYaw - p.EyeYaw) * kEye;
            p.EyePitch += (ePitch - p.EyePitch) * kEye;
            // ⑥ 깜빡임(주기 · 큰 시선 이동 동반)
            _blinkCool[a] -= dt;
            if (MathF.Abs(tYaw - _lastYaw[a]) > 20f * Deg && _blinkCool[a] <= 0 && _blinkPhase[a] <= 0) { _blinkPhase[a] = 0.15f; _blinkCool[a] = 0.6f; Blinks++; }
            _lastYaw[a] = tYaw;
            if ((_blinkT[a] -= dt) <= 0) { _blinkT[a] = Range(a, 2.2f, 5.5f); if (_blinkPhase[a] <= 0) { _blinkPhase[a] = 0.15f; Blinks++; } }
            if (_blinkPhase[a] > 0) { _blinkPhase[a] -= dt; var u = 1f - MathF.Max(0f, _blinkPhase[a]) / 0.15f; p.Blink = u < 0.4f ? u / 0.4f : MathF.Max(0f, 1f - (u - 0.4f) / 0.6f); }
            else p.Blink = 0f;
            // ⑦ v17.60 수동작
            GestureClass hand; float hw;
            if (isSpeaker) { hand = GestureClass.Rest; hw = 0f; }
            else if (valence > 0.3f && arousal > 0.6f) { hand = GestureClass.Inclusive; hw = 0.6f; }
            else if (arousal < 0.3f && speaker >= 0) { hand = GestureClass.Think; hw = 0.45f; }
            else { hand = GestureClass.Rest; hw = 0f; }
            if (hand != p.Hand && p.HandWeight > 0.05f) p.HandWeight += (0f - p.HandWeight) * kFace;
            else { p.Hand = hand; p.HandWeight += (hw - p.HandWeight) * kFace; }
            // ⑧ 실제 시선 방향 · 정렬 오차 · 고정 응시
            float wy = _bodyYaw[a] + p.HeadYaw + p.EyeYaw, wp = p.HeadPitch + nod + p.EyePitch;
            p.GazeDir = new Vector3(MathF.Sin(wy) * MathF.Cos(wp), MathF.Sin(wp), MathF.Cos(wy) * MathF.Cos(wp));
            p.Target = face;
            var len = d.Length();
            p.ErrorDeg = len > 1e-4f ? MathF.Acos(Math.Clamp(Vector3.Dot(p.GazeDir, d / len), -1f, 1f)) / Deg : 0f;
            if (!p.Averted && p.Blink < 0.5f && p.ErrorDeg < 5f) { _lock[a] += dt; if (_lock[a] > MaxLockS) MaxLockS = _lock[a]; } else _lock[a] = 0f;
        }
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static float Wrap(float a) { while (a > MathF.PI) a -= 2f * MathF.PI; while (a < -MathF.PI) a += 2f * MathF.PI; return a; }

    /// <summary>시선 · 리액션 → v17.58 ARKit 52 가중치(가산 · 입 영역은 호출자 담당).</summary>
    public void WriteFace(int a, Span<float> w52)
    {
        ref readonly var p = ref _p[a];
        Add(w52, ArkitBlendshape.EyeBlinkLeft, p.Blink); Add(w52, ArkitBlendshape.EyeBlinkRight, p.Blink);
        var yl = p.EyeYaw / MaxEyeYaw; var pt = p.EyePitch / MaxEyePitch;
        Set(w52, ArkitBlendshape.EyeLookOutLeft, yl); Set(w52, ArkitBlendshape.EyeLookInRight, yl);
        Set(w52, ArkitBlendshape.EyeLookInLeft, -yl); Set(w52, ArkitBlendshape.EyeLookOutRight, -yl);
        Set(w52, ArkitBlendshape.EyeLookUpLeft, pt); Set(w52, ArkitBlendshape.EyeLookUpRight, pt);
        Set(w52, ArkitBlendshape.EyeLookDownLeft, -pt); Set(w52, ArkitBlendshape.EyeLookDownRight, -pt);
        Add(w52, ArkitBlendshape.MouthSmileLeft, p.Smile); Add(w52, ArkitBlendshape.MouthSmileRight, p.Smile);
        Add(w52, ArkitBlendshape.CheekSquintLeft, 0.5f * p.Smile); Add(w52, ArkitBlendshape.CheekSquintRight, 0.5f * p.Smile);
        Add(w52, ArkitBlendshape.BrowInnerUp, 0.8f * p.Surprise);
        Add(w52, ArkitBlendshape.BrowOuterUpLeft, 0.7f * p.Surprise); Add(w52, ArkitBlendshape.BrowOuterUpRight, 0.7f * p.Surprise);
        Add(w52, ArkitBlendshape.EyeWideLeft, 0.7f * p.Surprise); Add(w52, ArkitBlendshape.EyeWideRight, 0.7f * p.Surprise);
        Add(w52, ArkitBlendshape.JawOpen, 0.22f * p.Surprise);
    }

    private static void Set(Span<float> w, ArkitBlendshape k, float v) => w[(int)k] = Math.Clamp(v, 0f, 1f);
    private static void Add(Span<float> w, ArkitBlendshape k, float v) => w[(int)k] = Math.Clamp(w[(int)k] + v, 0f, 1f);
}
