using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Mocap;
using PromptFlowStudio.Services.Neuro;
using PromptFlowStudio.Services.SpatialAudio;

namespace PromptFlowStudio.Services.Holo;

public enum AvatarBlendShape : byte { Smile = 0, BrowRaise, BrowFurrow, EyeWide, BlinkL, BlinkR, JawOpen, LipPress }

/// <summary>
/// [PromptFlow Studio v16.77.0] 🧑‍🚀 AI 텔레프레젠스 아바타 합성 &amp; 시선/표정 동기화기.
/// 수신 관절(볼류메트릭) → 머리 전방/좌표계 추정 → Eye-Contact 시선 교정(원격 카메라 응시 → 로컬 시청자 눈, 머리 요 ±25° · 피치 ±15° 한도 + 안구 잔여 ±30°, 지수 평활) ·
/// BCI 감정(Valence · Arousal · Fear · Engagement) + 음성 엔벨로프 → 미세 표정 블렌드셰이프(어택/릴리스 · 절차적 깜빡임) ·
/// 음성(외부 또는 성문 펄스 + 포먼트 합성) → 영상 지연 정렬 지연선(립싱크) → HRTF 바이노럴(머리 관절 위치 기준). 블록 처리 무할당.
/// </summary>
public sealed class AiTelepresenceAvatarSynthesizer
{
    public const int BlendShapes = 8, MaxBlock = 4096, DelayLen = 48000 / 5; // 200 ms
    public const float MaxHeadYaw = 25f, MaxHeadPitch = 15f, MaxEyeDeg = 30f;
    public static readonly string[] BlendNames = { "미소", "눈썹 올림", "미간 찌푸림", "눈 크게", "왼눈 깜빡", "오른눈 깜빡", "턱 벌림", "입술 다묾" };

    private readonly object _gate = new();
    private readonly int _fs;
    private readonly NeuralBinauralHrtfSpatializer _hrtf;
    private readonly float[] _w = new float[BlendShapes], _target = new float[BlendShapes];
    private readonly float[] _voice = new float[MaxBlock], _delayed = new float[MaxBlock], _line = new float[DelayLen];
    private int _linePos;
    private double _phase, _syll, _t;
    private float _f1z1, _f1z2, _f2z1, _f2z2, _env, _blinkTimer = 1.2f, _blinkPhase = -1f;
    private ulong _rng = 0x1677_C0FFEEUL;
    private Quaternion _headCorr = Quaternion.Identity;

    public AiTelepresenceAvatarSynthesizer(NeuralBinauralHrtfSpatializer? hrtf = null, int sampleRate = 48000)
    {
        _fs = sampleRate;
        _hrtf = hrtf ?? new NeuralBinauralHrtfSpatializer(sampleRate);
    }

    public NeuralBinauralHrtfSpatializer Hrtf => _hrtf;
    public bool EyeContact { get; set; } = true;
    /// <summary>시선 교정 강도(0..1).</summary>
    public float GazeStrength { get; set; } = 1f;
    public float GazeErrorBeforeDeg { get; private set; }
    public float GazeErrorAfterDeg { get; private set; }
    public float HeadYawCorrDeg { get; private set; }
    public float HeadPitchCorrDeg { get; private set; }
    public float EyeYawDeg { get; private set; }
    public float EyePitchDeg { get; private set; }
    public Vector3 HeadPosition { get; private set; }
    public Vector3 GazeDirection { get; private set; } = -Vector3.UnitZ;
    public Quaternion HeadCorrection => _headCorr;
    public float VoiceEnvelope => _env;
    public float AvSyncDelayMs { get; private set; }
    public EmotionState Emotion { get; private set; } = EmotionState.Neutral;
    public long Blinks { get; private set; }
    public long Blocks { get; private set; }
    public float Weight(AvatarBlendShape b) => _w[(int)b];
    public ReadOnlySpan<float> Weights => _w;
    public ReadOnlySpan<float> Voice(int n) => _delayed.AsSpan(0, Math.Min(n, MaxBlock));

    // ══════════════════════ 시선 ══════════════════════

    /// <summary>
    /// 관절(MocapSkeleton 24) → 머리 전방 = 어깨축 × 상향(목→머리) · 원격 카메라 방향 기준 원 시선. EyeContact 시 시청자 눈 방향으로 머리/안구 분배 교정.
    /// </summary>
    public void UpdateGaze(ReadOnlySpan<Vector3> joints, Vector3 remoteCameraWorld, Vector3 viewerEye, float dt)
    {
        lock (_gate)
        {
            if (joints.Length < MocapSkeleton.Body) return;
            var head = joints[MocapSkeleton.Head];
            var up = SafeNorm(head - joints[MocapSkeleton.Neck], Vector3.UnitY);
            var right = SafeNorm(joints[MocapSkeleton.RShoulder] - joints[MocapSkeleton.LShoulder], Vector3.UnitX);
            var fwd = SafeNorm(Vector3.Cross(up, right), Vector3.UnitZ); // 골격 전방 = +Z(왼쪽 = +X)
            // 원격 인물은 자기 카메라를 응시(눈 = 머리 전방을 카메라 쪽으로 최대 30° 회전)
            var toCam = SafeNorm(remoteCameraWorld - head, fwd);
            var raw = ClampCone(fwd, toCam, MaxEyeDeg);
            var toViewer = SafeNorm(viewerEye - head, fwd);
            HeadPosition = head;
            GazeErrorBeforeDeg = AngleDeg(raw, toViewer);

            // 머리 좌표계 내 요/피치 오차
            var lx = Vector3.Dot(toViewer, right); var ly = Vector3.Dot(toViewer, up); var lz = Vector3.Dot(toViewer, fwd);
            var yaw = MathF.Atan2(lx, lz) * (180f / MathF.PI);
            var pitch = MathF.Atan2(ly, MathF.Sqrt(lx * lx + lz * lz)) * (180f / MathF.PI);
            var k = EyeContact ? Math.Clamp(GazeStrength, 0f, 1f) : 0f;
            var hy = Math.Clamp(yaw * 0.6f, -MaxHeadYaw, MaxHeadYaw) * k;
            var hp = Math.Clamp(pitch * 0.6f, -MaxHeadPitch, MaxHeadPitch) * k;
            var a = 1f - MathF.Exp(-dt / 0.12f); // 120 ms 평활
            HeadYawCorrDeg += (hy - HeadYawCorrDeg) * a;
            HeadPitchCorrDeg += (hp - HeadPitchCorrDeg) * a;
            _headCorr = Quaternion.CreateFromAxisAngle(up, -HeadYawCorrDeg * (MathF.PI / 180f)) * Quaternion.CreateFromAxisAngle(right, HeadPitchCorrDeg * (MathF.PI / 180f));
            var newFwd = Vector3.Transform(fwd, _headCorr);
            if (k > 0)
            {
                // 안구 = 교정된 머리 전방 → 시청자(잔여 ±30°)
                var eye = ClampCone(newFwd, toViewer, MaxEyeDeg);
                GazeDirection = Vector3.Normalize(Vector3.Lerp(raw, eye, k));
            }
            else GazeDirection = raw;
            EyeYawDeg = MathF.Atan2(Vector3.Dot(GazeDirection, right), Vector3.Dot(GazeDirection, newFwd)) * (180f / MathF.PI);
            EyePitchDeg = MathF.Asin(Math.Clamp(Vector3.Dot(GazeDirection, up), -1f, 1f)) * (180f / MathF.PI);
            GazeErrorAfterDeg = AngleDeg(GazeDirection, toViewer);
        }
    }

    // ══════════════════════ 표정 + 오디오 ══════════════════════

    /// <summary>
    /// 1블록: 음성(mic 가 비면 합성) → 엔벨로프 → BCI 감정 결합 블렌드셰이프 → 지연선(videoLatencyMs − audioLatencyMs 정렬) → HRTF 바이노럴 누적(outL/outR).
    /// </summary>
    public void ProcessBlock(EmotionState emotion, ReadOnlySpan<float> mic, int n, Vector3 listener, float videoLatencyMs, float audioLatencyMs, Span<float> outL, Span<float> outR)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Holo.Avatar");
            n = Math.Min(n, MaxBlock);
            Emotion = emotion;
            var dt = (float)n / _fs;
            var v = _voice.AsSpan(0, n);
            if (mic.Length >= n) mic[..n].CopyTo(v);
            else SynthesizeVoice(v, emotion);

            // 엔벨로프(어택 5 ms · 릴리스 60 ms)
            float ea = 1f - MathF.Exp(-1f / (0.005f * _fs)), er = 1f - MathF.Exp(-1f / (0.06f * _fs));
            for (var i = 0; i < n; i++)
            {
                var x = MathF.Abs(v[i]);
                _env += (x - _env) * (x > _env ? ea : er);
            }

            // 지연 정렬: 영상 파이프라인이 더 느리면 음성을 지연(립싱크 ±0 목표)
            var dMs = Math.Clamp(videoLatencyMs - audioLatencyMs, 0f, 199f);
            AvSyncDelayMs = dMs;
            var d = (int)(dMs * 0.001f * _fs);
            for (var i = 0; i < n; i++)
            {
                _line[_linePos] = v[i];
                var r = _linePos - d; if (r < 0) r += DelayLen;
                _delayed[i] = _line[r];
                if (++_linePos == DelayLen) _linePos = 0;
            }

            UpdateExpressions(emotion, dt);
            var dir = HeadPosition - listener;
            if (dir.LengthSquared() < 1e-6f) dir = -Vector3.UnitZ;
            var dist = MathF.Max(0.5f, dir.Length());
            _hrtf.ProcessVoice(0, _delayed.AsSpan(0, n), dir, 1f / dist, outL, outR);
            Blocks++;
        }
    }

    private void UpdateExpressions(EmotionState e, float dt)
    {
        var jaw = Math.Clamp(_env * 6f, 0f, 1f);
        _target[(int)AvatarBlendShape.Smile] = Math.Clamp((e.Valence - 0.5f) * 2f, 0f, 1f) * (0.6f + 0.4f * e.Engagement);
        _target[(int)AvatarBlendShape.BrowRaise] = Math.Clamp((e.Arousal - 0.5f) * 1.6f + e.Fear * 0.5f, 0f, 1f);
        _target[(int)AvatarBlendShape.BrowFurrow] = Math.Clamp((0.5f - e.Valence) * 2f, 0f, 1f) * (1f - e.Fear * 0.5f);
        _target[(int)AvatarBlendShape.EyeWide] = Math.Clamp((e.Fear - 0.4f) * 2f + (e.Arousal - 0.6f), 0f, 1f);
        _target[(int)AvatarBlendShape.JawOpen] = jaw;
        _target[(int)AvatarBlendShape.LipPress] = Math.Clamp((0.45f - e.Valence) * 1.5f, 0f, 1f) * (1f - jaw);

        // 절차적 깜빡임: 2~6 s 간격(각성↑ → 빈도↑) · 150 ms 닫힘/열림
        _blinkTimer -= dt;
        if (_blinkPhase < 0 && _blinkTimer <= 0)
        {
            _blinkPhase = 0; Blinks++;
            _blinkTimer = (2f + 4f * NextFloat()) * (1.3f - 0.6f * e.Arousal);
        }
        var blink = 0f;
        if (_blinkPhase >= 0)
        {
            _blinkPhase += dt / 0.15f;
            blink = _blinkPhase < 0.5f ? _blinkPhase * 2f : MathF.Max(0f, 2f - _blinkPhase * 2f);
            if (_blinkPhase >= 1f) _blinkPhase = -1f;
        }
        _target[(int)AvatarBlendShape.BlinkL] = blink;
        _target[(int)AvatarBlendShape.BlinkR] = blink;

        for (var b = 0; b < BlendShapes; b++)
        {
            var isFast = b is (int)AvatarBlendShape.BlinkL or (int)AvatarBlendShape.BlinkR or (int)AvatarBlendShape.JawOpen;
            var tau = isFast ? 0.02f : _target[b] > _w[b] ? 0.08f : 0.25f;
            _w[b] += (_target[b] - _w[b]) * (1f - MathF.Exp(-dt / tau));
        }
    }

    /// <summary>성문 펄스(110~150 Hz · 감정 각성 따라 피치↑) + 2 포먼트 공진 · 4 Hz 음절 변조.</summary>
    private void SynthesizeVoice(Span<float> dst, EmotionState e)
    {
        var f0 = 110f + 40f * e.Arousal;
        for (var i = 0; i < dst.Length; i++)
        {
            _t += 1.0 / _fs;
            _phase += f0 / _fs; if (_phase >= 1) _phase -= 1;
            _syll += 4.2 / _fs; if (_syll >= 1) _syll -= 1;
            var glottal = _phase < 0.4 ? (float)Math.Sin(Math.PI * _phase / 0.4) : 0f;
            var am = (float)Math.Max(0, Math.Sin(2 * Math.PI * _syll));
            // 포먼트 F1 ≈ 600~800 Hz · F2 ≈ 1200~1800 Hz(음절 위상 따라 이동)
            var f1 = 600f + 200f * am; var f2 = 1200f + 600f * (float)Math.Sin(2 * Math.PI * _syll * 0.5);
            var y1 = Resonate(glottal, f1, 80f, ref _f1z1, ref _f1z2);
            var y2 = Resonate(glottal, f2, 120f, ref _f2z1, ref _f2z2);
            dst[i] = 0.08f * am * (y1 + 0.6f * y2);
        }
    }

    private float Resonate(float x, float f, float bw, ref float z1, ref float z2)
    {
        var r = MathF.Exp(-MathF.PI * bw / _fs);
        var c = 2f * r * MathF.Cos(2f * MathF.PI * f / _fs);
        var y = (1f - r) * x + c * z1 - r * r * z2;
        z2 = z1; z1 = y;
        return y;
    }

    private float NextFloat()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / (float)(1 << 24);
    }

    private static Vector3 SafeNorm(Vector3 v, Vector3 fallback) => v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : fallback;

    public static float AngleDeg(Vector3 a, Vector3 b) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(a), Vector3.Normalize(b)), -1f, 1f)) * (180f / MathF.PI);

    /// <summary>from → to 방향을 from 기준 최대 maxDeg 원뿔로 제한(구면 선형 보간).</summary>
    private static Vector3 ClampCone(Vector3 from, Vector3 to, float maxDeg)
    {
        var ang = AngleDeg(from, to);
        if (ang <= maxDeg || ang < 1e-3f) return to;
        var t = maxDeg / ang;
        var q = Quaternion.Slerp(Quaternion.Identity, FromTo(from, to), t);
        return Vector3.Normalize(Vector3.Transform(from, q));
    }

    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        a = Vector3.Normalize(a); b = Vector3.Normalize(b);
        var d = Vector3.Dot(a, b);
        if (d < -0.9999f)
        {
            var ax = Vector3.Cross(Vector3.UnitX, a);
            if (ax.LengthSquared() < 1e-6f) ax = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(ax), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c, 1f + d));
    }
}
