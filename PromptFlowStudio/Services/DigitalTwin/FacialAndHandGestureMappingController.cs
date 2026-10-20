using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.DigitalTwin;

[JsonConverter(typeof(JsonStringEnumConverter<HandGesture>))]
public enum HandGesture { None, OpenPalm, Fist, Point, Peace, ThumbsUp, Ok, Pinch }

/// <summary>리타겟 리그 21 관절(부모 인덱스 &lt; 자식 · 위상 정렬).</summary>
public enum RigJoint
{
    Hips, Spine, Chest, Neck, Head, LeftShoulder, LeftElbow, LeftWrist, LeftHandTip, RightShoulder, RightElbow, RightWrist, RightHandTip,
    LeftHip, LeftKnee, LeftAnkle, LeftToe, RightHip, RightKnee, RightAnkle, RightToe,
}

/// <summary>리그 위상 · T-포즈 방향(Y-up · +X = 피사체 왼쪽 · +Z = 카메라 쪽).</summary>
public static class Rig
{
    public const int Joints = 21;
    public static readonly int[] Parent = { -1, 0, 1, 2, 3, 2, 5, 6, 7, 2, 9, 10, 11, 0, 13, 14, 15, 0, 17, 18, 19 };
    /// <summary>주 자식(회전 구동 본) — 리프 = −1.</summary>
    public static readonly int[] Primary = { 1, 2, 3, 4, -1, 6, 7, 8, -1, 10, 11, 12, -1, 14, 15, 16, -1, 18, 19, 20, -1 };
    public static readonly Vector3[] RestDir =
    {
        Vector3.Zero, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY, Vector3.UnitY,
        Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, Vector3.UnitX, -Vector3.UnitX, -Vector3.UnitX, -Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ,
    };
    public static readonly string[] Names =
    {
        "Hips", "Spine", "Chest", "Neck", "Head", "LeftShoulder", "LeftElbow", "LeftWrist", "LeftHandTip", "RightShoulder", "RightElbow", "RightWrist", "RightHandTip",
        "LeftHip", "LeftKnee", "LeftAnkle", "LeftToe", "RightHip", "RightKnee", "RightAnkle", "RightToe",
    };
    /// <summary>리그 → 와이어프레임 본(부모, 자식) 쌍.</summary>
    public static readonly int[] Bones = BuildBones();

    private static int[] BuildBones()
    {
        var b = new int[(Joints - 1) * 2];
        for (var j = 1; j < Joints; j++) { b[(j - 1) * 2] = Parent[j]; b[(j - 1) * 2 + 1] = j; }
        return b;
    }
}

/// <summary>타깃 캐릭터 본 길이(헤드 단위 · 관절별 부모→관절).</summary>
public sealed class RigProportions
{
    public string Label { get; init; } = "성인";
    public float[] Length { get; init; } = new float[Rig.Joints];

    private static RigProportions Make(string label, float spine, float chest, float neck, float head, float clav, float upper, float lower, float hand, float hip, float thigh, float shin, float toe)
    {
        var l = new float[Rig.Joints];
        l[1] = spine; l[2] = chest; l[3] = neck; l[4] = head;
        l[5] = l[9] = clav; l[6] = l[10] = upper; l[7] = l[11] = lower; l[8] = l[12] = hand;
        l[13] = l[17] = hip; l[14] = l[18] = thigh; l[15] = l[19] = shin; l[16] = l[20] = toe;
        return new RigProportions { Label = label, Length = l };
    }

    public static readonly RigProportions Adult = Make("성인(8두신)", 1.0f, 1.1f, 0.35f, 0.9f, 0.75f, 1.35f, 1.2f, 0.7f, 0.5f, 1.9f, 1.85f, 0.6f);
    public static readonly RigProportions Baby = Make("아기(4두신)", 0.55f, 0.6f, 0.15f, 1.0f, 0.5f, 0.6f, 0.55f, 0.35f, 0.35f, 0.7f, 0.65f, 0.3f);

    /// <summary>트윈 두신/어깨비 → 성인↔아기 보간 · 쇄골 폭 보정.</summary>
    public static RigProportions FromTwin(DigitalTwinAsset t)
    {
        var k = (float)Math.Clamp((t.HeadsTall - 4) / 4, 0, 1);
        var l = new float[Rig.Joints];
        for (var i = 0; i < l.Length; i++) l[i] = Baby.Length[i] + (Adult.Length[i] - Baby.Length[i]) * k;
        if (t.ShoulderRatio > 0) { var s = (float)Math.Clamp(t.ShoulderRatio / 2.6, 0.7, 1.4); l[5] *= s; l[9] *= s; }
        return new RigProportions { Label = "트윈 · " + t.Name, Length = l };
    }

    /// <summary>v16.22.0 캐릭터 프로필 → 아기/성인 비율.</summary>
    public static RigProportions FromProfile(CharacterProfile p) =>
        FacialEmotionPuppeteerEngine.IsBabySubject(p.Name + " " + p.StyleTokens) || p.Name.Contains("아기", StringComparison.Ordinal) ? Baby : Adult;
}

/// <summary>EWMA 지터 억제 필터 — 데드밴드(미세 떨림 고정) · 속도 적응 α(빠른 동작 지연 감소) · 스파이크 보류(연속 3프레임 미만 이상치 무시).</summary>
public sealed class EwmaJitterFilter
{
    public const float MaxJump = 0.2f;
    private readonly float[] _s, _raw;
    private readonly byte[] _outlier;
    private bool _primed;
    private float _sens = 0.5f;

    public EwmaJitterFilter(int channels)
    {
        _s = new float[channels];
        _raw = new float[channels];
        _outlier = new byte[channels];
        Sensitivity = 0.5f;
    }

    /// <summary>노이즈 억제 감도(0 = 원본 추종 ~ 1 = 최대 억제).</summary>
    public float Sensitivity
    {
        get => _sens;
        set
        {
            _sens = Math.Clamp(value, 0, 1);
            Alpha = 0.85f + (0.12f - 0.85f) * _sens;
            Deadband = 0.0005f + (0.006f - 0.0005f) * _sens;
            VelocityGain = 20f + (4f - 20f) * _sens;
        }
    }

    public float Alpha { get; private set; }
    public float Deadband { get; private set; }
    public float VelocityGain { get; private set; }
    public double RawJitter { get; private set; }
    public double FilteredJitter { get; private set; }
    public long SpikesRejected { get; private set; }
    public double Reduction => RawJitter <= 1e-9 ? 0 : Math.Clamp(1 - FilteredJitter / RawJitter, 0, 1);

    public void Reset() { _primed = false; RawJitter = FilteredJitter = 0; Array.Clear(_outlier); }

    /// <summary>제자리 필터(무할당).</summary>
    public void Process(Span<float> x)
    {
        var n = Math.Min(x.Length, _s.Length);
        if (!_primed)
        {
            x[..n].CopyTo(_s); x[..n].CopyTo(_raw);
            _primed = true;
            return;
        }
        double rawE = 0, outE = 0;
        for (var i = 0; i < n; i++)
        {
            var v = x[i];
            rawE += Math.Abs(v - _raw[i]);
            _raw[i] = v;
            var prev = _s[i];
            var d = v - prev;
            var ad = Math.Abs(d);
            if (ad <= Deadband) { x[i] = prev; _outlier[i] = 0; continue; }
            if (ad > MaxJump && _outlier[i] < 2) { _outlier[i]++; SpikesRejected++; x[i] = prev; continue; }
            _outlier[i] = 0;
            var a = Alpha + (1 - Alpha) * Math.Min(1f, ad * VelocityGain);
            var o = prev + d * a;
            outE += Math.Abs(o - prev);
            _s[i] = o;
            x[i] = o;
        }
        RawJitter = RawJitter * 0.9 + rawE / Math.Max(1, n) * 0.1;
        FilteredJitter = FilteredJitter * 0.9 + outE / Math.Max(1, n) * 0.1;
    }
}

/// <summary>매핑 결과(사전 할당 · UI 복사용).</summary>
public sealed class MappedPose
{
    public readonly float[] Joints = new float[Rig.Joints * 3];        // 리타겟 위치(Y-up · 헤드 단위 × 0.1)
    public readonly float[] Source = new float[Rig.Joints * 3];        // 소스(필터) 위치
    public readonly float[] Curls = new float[10];                     // 왼손 5 · 오른손 5
    public LandmarkGuide Guide;
    public FacialEmotion Emotion;
    public double Intensity;
    public HandGesture Left, Right;
    public float LeftPinch, RightPinch;
    public double JitterReduction;
    public long Seq;

    public void CopyTo(MappedPose d)
    {
        Joints.AsSpan().CopyTo(d.Joints); Source.AsSpan().CopyTo(d.Source); Curls.AsSpan().CopyTo(d.Curls);
        d.Guide = Guide; d.Emotion = Emotion; d.Intensity = Intensity; d.Left = Left; d.Right = Right;
        d.LeftPinch = LeftPinch; d.RightPinch = RightPinch; d.JitterReduction = JitterReduction; d.Seq = Seq;
    }
}

/// <summary>녹화 클립 — 프레임별 [리그 63 · 가이드 6 · 감정 · 강도 · 왼손 · 오른손 · 시각(ms)].</summary>
public sealed class MocapClip
{
    public const int Stride = Rig.Joints * 3 + 6 + 5;
    public const int GuideAt = Rig.Joints * 3, EmotionAt = GuideAt + 6, IntensityAt = EmotionAt + 1, LeftAt = EmotionAt + 2, RightAt = EmotionAt + 3, TimeAt = EmotionAt + 4;
    public int Frames { get; init; }
    public double Fps { get; init; }
    public double DurationSec { get; init; }
    public float[] Data { get; init; } = Array.Empty<float>();
    public FacialEmotion DominantEmotion { get; init; }
    public double MeanIntensity { get; init; }
    public HandGesture DominantLeft { get; init; }
    public HandGesture DominantRight { get; init; }
    public double MotionEnergy { get; init; }
    public string Action { get; init; } = "";
    public string ActionLabel { get; init; } = "";
    public RigProportions Target { get; init; } = RigProportions.Adult;
}

/// <summary>
/// [PromptFlow Studio v16.36.0] 🦴 안면/손가락 모션 매핑 &amp; 포즈 리타겟팅 컨트롤러.
///   ▸ 추적기 프레임 싱크 → 포즈/안면/손 채널별 BCL EWMA 지터 억제(데드밴드 · 속도 적응 α · 스파이크 보류) · 감도 슬라이더.
///   ▸ 33 관절 → 21 관절 리그: 방향 보존 + 타깃 본 길이(성인/아기/트윈 · v16.22.0 프로필) 1:1 리타겟.
///   ▸ 468 랜드마크 → v16.20.0 LandmarkGuide 6축 → 프리셋 투영 잔차 최소 감정 · 강도.
///   ▸ 21 손 관절 → 손가락 굽힘 5 · 핀치 → 마이크로 제스처(주먹/펼침/포인트/V/엄지/OK/핀치).
///   ▸ 녹화(사전 할당 링 · 무할당 기록) → BVH(ZXY 오일러 · 로컬 회전) · 동작 분석 · 1-Click 스토리보드 인젝션(감정 + 제스처 + 트윈).
/// </summary>
public sealed class FacialAndHandGestureMappingController : IDisposable
{
    public const int MaxRecordFrames = RealtimeWebcamMotionCaptureTracker.TargetFps * 120;
    public const float Unit = 0.1f;   // 헤드 1 = 0.1

    private static readonly FacialEmotion[] Emotions = { FacialEmotion.HappySmile, FacialEmotion.Giggle, FacialEmotion.Surprised, FacialEmotion.Curious, FacialEmotion.Pout };
    private static readonly LandmarkGuide[] EmotionGuides = BuildGuides();

    private readonly RealtimeWebcamMotionCaptureTracker? _tracker;
    private readonly Action<MocapFrame> _sink;
    private readonly object _gate = new();
    private readonly EwmaJitterFilter _poseFilter = new(MocapFrame.PoseJoints * 3);
    private readonly EwmaJitterFilter _faceFilter = new(MocapFrame.FaceLength);
    private readonly EwmaJitterFilter _handFilter = new(MocapFrame.HandsLength);
    private readonly float[] _pose = new float[MocapFrame.PoseJoints * 3];
    private readonly float[] _face = new float[MocapFrame.FaceLength];
    private readonly float[] _hands = new float[MocapFrame.HandsLength];
    private readonly Vector3[] _src = new Vector3[Rig.Joints];
    private readonly Vector3[] _dst = new Vector3[Rig.Joints];
    private readonly MappedPose _work = new(), _published = new();
    private float[]? _rec;
    private int _recFrames;
    private double _recStartMs, _lastRaiseMs;
    private bool _recording;
    private RigProportions _target = RigProportions.Adult;

    public FacialAndHandGestureMappingController() : this((RealtimeWebcamMotionCaptureTracker?)null) { }

    public FacialAndHandGestureMappingController(IServiceProvider sp) : this(sp.GetService(typeof(RealtimeWebcamMotionCaptureTracker)) as RealtimeWebcamMotionCaptureTracker) { }

    public FacialAndHandGestureMappingController(RealtimeWebcamMotionCaptureTracker? tracker)
    {
        _sink = OnFrame;
        _tracker = tracker;
        tracker?.AddSink(_sink);
    }

    public event Action? OnChange;

    public float Sensitivity
    {
        get => _poseFilter.Sensitivity;
        set { lock (_gate) { _poseFilter.Sensitivity = value; _faceFilter.Sensitivity = value; _handFilter.Sensitivity = value; } }
    }

    public RigProportions Target { get => _target; set => _target = value ?? RigProportions.Adult; }
    public EwmaJitterFilter PoseFilter => _poseFilter;
    public bool IsRecording => _recording;
    public int RecordedFrames => Volatile.Read(ref _recFrames);
    public MocapClip? LastClip { get; private set; }
    public string? LastAction { get; private set; }
    public long Mapped { get; private set; }

    private static LandmarkGuide[] BuildGuides()
    {
        var g = new LandmarkGuide[Emotions.Length];
        for (var i = 0; i < g.Length; i++) g[i] = FacialEmotionPuppeteerEngine.PresetOf(Emotions[i]).Guide;
        return g;
    }

    // ── 순수 매핑 ──────────────────────────────────────────────────────────────

    private static float Dist(ReadOnlySpan<float> f, int a, int b)
    {
        float dx = f[a * 3] - f[b * 3], dy = f[a * 3 + 1] - f[b * 3 + 1];
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static float Dist3(ReadOnlySpan<float> f, int a, int b)
    {
        float dx = f[a * 3] - f[b * 3], dy = f[a * 3 + 1] - f[b * 3 + 1], dz = f[a * 3 + 2] - f[b * 3 + 2];
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>468 랜드마크 → v16.20.0 6축 가이드(−1~1 · 중립 0).</summary>
    public static LandmarkGuide GuideFromFace(ReadOnlySpan<float> f)
    {
        if (f.Length < MocapFrame.FaceLength) return default;
        var fh = Dist(f, FaceTemplate.Forehead, FaceTemplate.Chin);
        var fw = Dist(f, FaceTemplate.CheekLeft, FaceTemplate.CheekRight);
        if (fh < 1e-5f || fw < 1e-5f) return default;
        var open = Math.Clamp((Dist(f, FaceTemplate.UpperLip, FaceTemplate.LowerLip) / fh - 0.01f) / FaceTemplate.MouthOpenScale, 0, 1);
        var lipY = (f[FaceTemplate.UpperLip * 3 + 1] + f[FaceTemplate.LowerLip * 3 + 1]) / 2;
        var cornerY = (f[FaceTemplate.MouthLeft * 3 + 1] + f[FaceTemplate.MouthRight * 3 + 1]) / 2;
        var lift = Math.Clamp((lipY - cornerY) / fh / FaceTemplate.MouthCornerScale, -1, 1);
        var gap = (Dist(f, FaceTemplate.LeftEyeTop, FaceTemplate.LeftEyeBottom) + Dist(f, FaceTemplate.RightEyeTop, FaceTemplate.RightEyeBottom)) / 2 / fh;
        var eye = Math.Clamp(gap / FaceTemplate.NeutralEyeGap - 1, -1, 1);
        var browGap = ((f[FaceTemplate.LeftEyeTop * 3 + 1] - f[FaceTemplate.LeftBrow * 3 + 1]) + (f[FaceTemplate.RightEyeTop * 3 + 1] - f[FaceTemplate.RightBrow * 3 + 1])) / 2 / fh;
        var brow = Math.Clamp((browGap - FaceTemplate.NeutralBrowGap) / 0.04f, -1, 1);
        var pucker = Math.Clamp((1 - Dist(f, FaceTemplate.MouthLeft, FaceTemplate.MouthRight) / fw / FaceTemplate.NeutralMouthWidth) / 0.3f, 0, 1);
        return new LandmarkGuide(R(lift), R(open), R(brow), R(eye), R(Math.Clamp(lift * 0.85f, -1, 1)), R(pucker));
    }

    private static double R(float v) => Math.Round(v, 4);

    /// <summary>가이드 → 프리셋 투영(k = g·p/p·p) 잔차 최소 감정 · 강도(최대 변위 &lt; 0.15 = 중립).</summary>
    public static (FacialEmotion Emotion, double Intensity) Classify(in LandmarkGuide g)
    {
        if (g.MaxMagnitude < 0.15) return (FacialEmotion.Neutral, 0);
        var best = FacialEmotion.Neutral; var bestErr = double.MaxValue; double bestK = 0;
        for (var i = 0; i < Emotions.Length; i++)
        {
            var p = EmotionGuides[i];
            var pp = Dot(p, p);
            if (pp < 1e-9) continue;
            var k = Math.Clamp(Dot(g, p) / pp, 0, 1);
            double e0 = g.MouthCornerLift - k * p.MouthCornerLift, e1 = g.MouthOpen - k * p.MouthOpen, e2 = g.BrowRaise - k * p.BrowRaise,
                   e3 = g.EyeOpen - k * p.EyeOpen, e4 = g.CheekRaise - k * p.CheekRaise, e5 = g.LipPucker - k * p.LipPucker;
            var err = e0 * e0 + e1 * e1 + e2 * e2 + e3 * e3 + e4 * e4 + e5 * e5;
            if (k > 0.2 && err < bestErr) { bestErr = err; best = Emotions[i]; bestK = k; }
        }
        return (best, Math.Round(bestK, 3));
    }

    private static double Dot(in LandmarkGuide a, in LandmarkGuide b) =>
        a.MouthCornerLift * b.MouthCornerLift + a.MouthOpen * b.MouthOpen + a.BrowRaise * b.BrowRaise + a.EyeOpen * b.EyeOpen + a.CheekRaise * b.CheekRaise + a.LipPucker * b.LipPucker;

    /// <summary>21 손 관절 → 손가락 굽힘(curls 5) · 핀치 거리(손 크기 비) · 제스처.</summary>
    public static HandGesture ClassifyHand(ReadOnlySpan<float> h, Span<float> curls, out float pinch)
    {
        pinch = 1;
        if (h.Length < MocapFrame.HandLength) return HandGesture.None;
        var size = Dist3(h, 0, 9);
        if (size < 1e-5f) return HandGesture.None;
        var thumb = Dist3(h, 4, 5) / size;
        curls[0] = Math.Clamp((0.6f - thumb) / 0.4f, 0, 1);
        for (var f = 1; f < 5; f++)
        {
            int m = 1 + f * 4;
            var path = Dist3(h, 0, m) + Dist3(h, m, m + 1) + Dist3(h, m + 1, m + 2) + Dist3(h, m + 2, m + 3);
            var ext = path < 1e-6f ? 0 : Dist3(h, 0, m + 3) / path;
            curls[f] = Math.Clamp((0.95f - ext) / 0.45f, 0, 1);
        }
        pinch = Dist3(h, 4, 8) / size;
        bool thumbExt = thumb > 0.6f, idx = curls[1] < 0.5f, mid = curls[2] < 0.5f, ring = curls[3] < 0.5f, pky = curls[4] < 0.5f;
        if (!idx && !mid && !ring && !pky) return thumbExt ? HandGesture.ThumbsUp : HandGesture.Fist;
        if (idx && !mid && !ring && !pky) return HandGesture.Point;
        if (idx && mid && !ring && !pky) return HandGesture.Peace;
        if (!idx && mid && ring && pky && pinch < 0.35f) return HandGesture.Ok;
        if (idx && mid && ring && pky) return HandGesture.OpenPalm;
        return pinch < 0.2f ? HandGesture.Pinch : HandGesture.None;
    }

    public static string GestureLabel(HandGesture g) => g switch
    {
        HandGesture.OpenPalm => "✋ 펼침", HandGesture.Fist => "✊ 주먹", HandGesture.Point => "☝ 포인트", HandGesture.Peace => "✌ V",
        HandGesture.ThumbsUp => "👍 엄지", HandGesture.Ok => "👌 OK", HandGesture.Pinch => "🤏 핀치", _ => "—",
    };

    private static string GestureEnglish(HandGesture g) => g switch
    {
        HandGesture.OpenPalm => "open palm gesture", HandGesture.Fist => "clenched fist", HandGesture.Point => "pointing with the index finger",
        HandGesture.Peace => "making a peace sign", HandGesture.ThumbsUp => "giving a thumbs up", HandGesture.Ok => "making an OK hand sign",
        HandGesture.Pinch => "delicate pinch gesture", _ => "",
    };

    /// <summary>MediaPipe 포즈(x,y,z 33×3) → 리그 소스 관절(Y-up 월드).</summary>
    public static void SourceRig(ReadOnlySpan<float> p, Span<Vector3> s)
    {
        static Vector3 P(ReadOnlySpan<float> q, PoseJoint j) { var i = (int)j * 3; return new Vector3(q[i] - 0.5f, 0.5f - q[i + 1], -q[i + 2]); }
        var hips = (P(p, PoseJoint.LeftHip) + P(p, PoseJoint.RightHip)) * 0.5f;
        var chest = (P(p, PoseJoint.LeftShoulder) + P(p, PoseJoint.RightShoulder)) * 0.5f;
        var nose = P(p, PoseJoint.Nose);
        var neck = Vector3.Lerp(chest, nose, 0.4f);
        s[(int)RigJoint.Hips] = hips;
        s[(int)RigJoint.Spine] = Vector3.Lerp(hips, chest, 0.5f);
        s[(int)RigJoint.Chest] = chest;
        s[(int)RigJoint.Neck] = neck;
        s[(int)RigJoint.Head] = nose + (nose - neck) * 0.6f;
        s[(int)RigJoint.LeftShoulder] = P(p, PoseJoint.LeftShoulder);
        s[(int)RigJoint.LeftElbow] = P(p, PoseJoint.LeftElbow);
        s[(int)RigJoint.LeftWrist] = P(p, PoseJoint.LeftWrist);
        s[(int)RigJoint.LeftHandTip] = P(p, PoseJoint.LeftIndex);
        s[(int)RigJoint.RightShoulder] = P(p, PoseJoint.RightShoulder);
        s[(int)RigJoint.RightElbow] = P(p, PoseJoint.RightElbow);
        s[(int)RigJoint.RightWrist] = P(p, PoseJoint.RightWrist);
        s[(int)RigJoint.RightHandTip] = P(p, PoseJoint.RightIndex);
        s[(int)RigJoint.LeftHip] = P(p, PoseJoint.LeftHip);
        s[(int)RigJoint.LeftKnee] = P(p, PoseJoint.LeftKnee);
        s[(int)RigJoint.LeftAnkle] = P(p, PoseJoint.LeftAnkle);
        s[(int)RigJoint.LeftToe] = P(p, PoseJoint.LeftFootIndex);
        s[(int)RigJoint.RightHip] = P(p, PoseJoint.RightHip);
        s[(int)RigJoint.RightKnee] = P(p, PoseJoint.RightKnee);
        s[(int)RigJoint.RightAnkle] = P(p, PoseJoint.RightAnkle);
        s[(int)RigJoint.RightToe] = P(p, PoseJoint.RightFootIndex);
    }

    /// <summary>방향 보존 리타겟 — 루트 = 소스 엉덩이 · 자식 = 부모 + 단위 방향 × 타깃 길이 × Unit.</summary>
    public static void Retarget(ReadOnlySpan<Vector3> src, RigProportions target, Span<Vector3> dst)
    {
        dst[0] = src[0];
        for (var j = 1; j < Rig.Joints; j++)
        {
            var p = Rig.Parent[j];
            var d = src[j] - src[p];
            var len = d.Length();
            var dir = len > 1e-6f ? d / len : Rig.RestDir[j];
            dst[j] = dst[p] + dir * (target.Length[j] * Unit);
        }
    }

    // ── 프레임 싱크(핫패스 · 무할당) ──────────────────────────────────────────

    private void OnFrame(MocapFrame f)
    {
        lock (_gate)
        {
            for (var j = 0; j < MocapFrame.PoseJoints; j++)
            {
                _pose[j * 3] = f.Pose[j * MocapFrame.PoseStride];
                _pose[j * 3 + 1] = f.Pose[j * MocapFrame.PoseStride + 1];
                _pose[j * 3 + 2] = f.Pose[j * MocapFrame.PoseStride + 2];
            }
            _poseFilter.Process(_pose);
            if (f.FaceValid) { f.Face.AsSpan().CopyTo(_face); _faceFilter.Process(_face); }
            f.HandsXyz.AsSpan().CopyTo(_hands);
            _handFilter.Process(_hands);

            SourceRig(_pose, _src);
            Retarget(_src, _target, _dst);
            for (var j = 0; j < Rig.Joints; j++)
            {
                _work.Joints[j * 3] = _dst[j].X; _work.Joints[j * 3 + 1] = _dst[j].Y; _work.Joints[j * 3 + 2] = _dst[j].Z;
                _work.Source[j * 3] = _src[j].X; _work.Source[j * 3 + 1] = _src[j].Y; _work.Source[j * 3 + 2] = _src[j].Z;
            }
            if (f.FaceValid)
            {
                _work.Guide = GuideFromFace(_face);
                (_work.Emotion, _work.Intensity) = Classify(_work.Guide);
            }
            var hs = _hands.AsSpan();
            _work.Left = f.LeftValid ? ClassifyHand(hs[..MocapFrame.HandLength], _work.Curls.AsSpan(0, 5), out _work.LeftPinch) : HandGesture.None;
            _work.Right = f.RightValid ? ClassifyHand(hs.Slice(MocapFrame.HandLength, MocapFrame.HandLength), _work.Curls.AsSpan(5, 5), out _work.RightPinch) : HandGesture.None;
            _work.JitterReduction = _poseFilter.Reduction;
            _work.Seq = f.Seq;
            _work.CopyTo(_published);
            Mapped++;
            if (_recording && _rec is not null && _recFrames < MaxRecordFrames)
            {
                if (_recFrames == 0) _recStartMs = f.TimestampMs;
                var o = _recFrames * MocapClip.Stride;
                _work.Joints.AsSpan().CopyTo(_rec.AsSpan(o));
                var g = _work.Guide;
                _rec[o + MocapClip.GuideAt] = (float)g.MouthCornerLift; _rec[o + MocapClip.GuideAt + 1] = (float)g.MouthOpen; _rec[o + MocapClip.GuideAt + 2] = (float)g.BrowRaise;
                _rec[o + MocapClip.GuideAt + 3] = (float)g.EyeOpen; _rec[o + MocapClip.GuideAt + 4] = (float)g.CheekRaise; _rec[o + MocapClip.GuideAt + 5] = (float)g.LipPucker;
                _rec[o + MocapClip.EmotionAt] = (float)_work.Emotion;
                _rec[o + MocapClip.IntensityAt] = (float)_work.Intensity;
                _rec[o + MocapClip.LeftAt] = (float)_work.Left;
                _rec[o + MocapClip.RightAt] = (float)_work.Right;
                _rec[o + MocapClip.TimeAt] = (float)(f.TimestampMs - _recStartMs);
                Volatile.Write(ref _recFrames, _recFrames + 1);
            }
        }
        if (f.TimestampMs - _lastRaiseMs >= 250) { _lastRaiseMs = f.TimestampMs; Raise(); }
    }

    /// <summary>테스트/외부 소스용 직접 투입(싱크와 동일 경로).</summary>
    public void Process(MocapFrame f) => _sink(f);

    public void CopyMapped(MappedPose dst)
    {
        lock (_gate) _published.CopyTo(dst);
    }

    public void ResetFilters()
    {
        lock (_gate) { _poseFilter.Reset(); _faceFilter.Reset(); _handFilter.Reset(); }
    }

    // ── 녹화 ─────────────────────────────────────────────────────────────────

    public void BeginRecording()
    {
        lock (_gate)
        {
            _rec ??= new float[MaxRecordFrames * MocapClip.Stride];
            _recFrames = 0;
            _recording = true;
        }
        LastAction = "⏺ 녹화 시작 · " + _target.Label;
        Raise();
    }

    /// <summary>녹화 종료 → 클립(동작 분석 · 지배 감정/제스처). 프레임 0 → null.</summary>
    public MocapClip? EndRecording()
    {
        float[] data; int n; RigProportions target;
        lock (_gate)
        {
            _recording = false;
            n = _recFrames;
            if (_rec is null || n == 0) { LastAction = "⚠ 녹화 프레임 없음"; Raise(); return null; }
            data = _rec.AsSpan(0, n * MocapClip.Stride).ToArray();
            target = _target;
        }
        var clip = BuildClip(data, n, target);
        LastClip = clip;
        LastAction = "⏹ 녹화 " + n.ToString(CultureInfo.InvariantCulture) + "프레임 · " + clip.DurationSec.ToString("0.0", CultureInfo.InvariantCulture) + "초 · " + clip.ActionLabel;
        Raise();
        return clip;
    }

    public static MocapClip BuildClip(float[] data, int n, RigProportions target)
    {
        Span<int> emo = stackalloc int[6];
        Span<int> lg = stackalloc int[8];
        Span<int> rg = stackalloc int[8];
        double inten = 0; var inN = 0;
        for (var i = 0; i < n; i++)
        {
            var o = i * MocapClip.Stride;
            var e = (int)data[o + MocapClip.EmotionAt];
            if ((uint)e < 6) emo[e]++;
            if (e != 0) { inten += data[o + MocapClip.IntensityAt]; inN++; }
            var l = (int)data[o + MocapClip.LeftAt]; if ((uint)l < 8) lg[l]++;
            var r = (int)data[o + MocapClip.RightAt]; if ((uint)r < 8) rg[r]++;
        }
        var dur = n > 1 ? data[(n - 1) * MocapClip.Stride + MocapClip.TimeAt] / 1000.0 : 0;
        var (action, label, energy) = AnalyzeMotion(data, 0, n);
        return new MocapClip
        {
            Frames = n, DurationSec = Math.Round(dur, 3), Fps = dur > 0 ? Math.Round((n - 1) / dur, 2) : RealtimeWebcamMotionCaptureTracker.TargetFps, Data = data,
            DominantEmotion = (FacialEmotion)ArgMax(emo, true), MeanIntensity = inN == 0 ? 0 : Math.Round(inten / inN, 3),
            DominantLeft = (HandGesture)ArgMax(lg, true), DominantRight = (HandGesture)ArgMax(rg, true),
            MotionEnergy = energy, Action = action, ActionLabel = label, Target = target,
        };
    }

    /// <summary>히스토그램 최빈값(skipZero: 0 이외 값이 존재하면 우선).</summary>
    private static int ArgMax(ReadOnlySpan<int> h, bool skipZero)
    {
        var best = 0;
        for (var i = 1; i < h.Length; i++)
            if (h[i] > 0 && ((skipZero && best == 0) || h[i] > h[best])) best = i;
        return best;
    }

    private static Vector3 J(float[] d, int frame, RigJoint j) { var o = frame * MocapClip.Stride + (int)j * 3; return new Vector3(d[o], d[o + 1], d[o + 2]); }

    /// <summary>동작 분석 — 손 머리 위 비율 · 손목 좌우 왕복(흔들기) · 엉덩이 상하(바운스) · 이동 · 평균 관절 속도.</summary>
    public static (string English, string Korean, double Energy) AnalyzeMotion(float[] d, int start, int end)
    {
        var n = end - start;
        if (n < 2) return ("standing still with subtle idle motion", "정지/미세 동작", 0);
        int above = 0, flips = 0, prevSign = 0;
        double hipMin = double.MaxValue, hipMax = double.MinValue, energy = 0;
        for (var i = start; i < end; i++)
        {
            var head = J(d, i, RigJoint.Head).Y;
            if (J(d, i, RigJoint.LeftWrist).Y > head || J(d, i, RigJoint.RightWrist).Y > head) above++;
            var hy = J(d, i, RigJoint.Hips).Y;
            hipMin = Math.Min(hipMin, hy); hipMax = Math.Max(hipMax, hy);
            if (i == start) continue;
            var o = i * MocapClip.Stride; var p = (i - 1) * MocapClip.Stride;
            for (var k = 0; k < Rig.Joints * 3; k++) energy += Math.Abs(d[o + k] - d[p + k]);
            var vx = d[o + (int)RigJoint.LeftWrist * 3] - d[p + (int)RigJoint.LeftWrist * 3] + d[o + (int)RigJoint.RightWrist * 3] - d[p + (int)RigJoint.RightWrist * 3];
            var sign = vx > 0.002f ? 1 : vx < -0.002f ? -1 : 0;
            if (sign != 0 && prevSign != 0 && sign != prevSign) flips++;
            if (sign != 0) prevSign = sign;
        }
        energy /= (n - 1) * Rig.Joints;
        var travel = Math.Abs(J(d, end - 1, RigJoint.Hips).X - J(d, start, RigJoint.Hips).X);
        var parts = new List<string>(4); var ko = new List<string>(4);
        if (flips >= 3 && flips * 60.0 / n >= 1.0) { parts.Add("waving hand energetically"); ko.Add("손 흔들기"); }
        if (above > n / 3) { parts.Add("raising arms above the head"); ko.Add("팔 들기"); }
        if (hipMax - hipMin > Unit * 0.25) { parts.Add("bouncing rhythmically"); ko.Add("바운스"); }
        if (travel > Unit * 1.5) { parts.Add("walking across the frame"); ko.Add("이동"); }
        if (parts.Count == 0) { parts.Add(energy > 0.002 ? "expressive upper-body movement" : "standing still with subtle idle motion"); ko.Add(energy > 0.002 ? "상체 표현" : "정지/미세 동작"); }
        return (string.Join(", ", parts), string.Join(" · ", ko), Math.Round(energy, 5));
    }

    // ── BVH ──────────────────────────────────────────────────────────────────

    /// <summary>최단 호 회전(a→b 단위 벡터).</summary>
    public static Quaternion Arc(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d > 0.99999f) return Quaternion.Identity;
        if (d < -0.99999f)
        {
            var axis = Vector3.Cross(Vector3.UnitX, a);
            if (axis.LengthSquared() < 1e-6f) axis = Vector3.Cross(Vector3.UnitY, a);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1 + d));
    }

    /// <summary>쿼터니언 → BVH ZXY 오일러(도 · R = Rz·Rx·Ry).</summary>
    public static Vector3 EulerZxy(Quaternion q)
    {
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        float r01 = 2 * (x * y - z * w), r11 = 1 - 2 * (x * x + z * z);
        float r20 = 2 * (x * z - y * w), r21 = 2 * (y * z + x * w), r22 = 1 - 2 * (x * x + y * y);
        const float Deg = 180f / MathF.PI;
        var ax = MathF.Asin(Math.Clamp(r21, -1, 1));
        return new Vector3(ax * Deg, MathF.Atan2(-r20, r22) * Deg, MathF.Atan2(-r01, r11) * Deg);   // (X, Y, Z)
    }

    public static Quaternion FromEulerZxy(Vector3 deg)
    {
        const float Rad = MathF.PI / 180f;
        return Quaternion.CreateFromAxisAngle(Vector3.UnitZ, deg.Z * Rad) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, deg.X * Rad) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, deg.Y * Rad);
    }

    /// <summary>클립 → BVH 텍스트(cm · 헤드 = 22cm · 비리프 관절 ZXY 회전 · 루트 위치).</summary>
    public static string ToBvh(MocapClip clip)
    {
        const float HeadCm = 22f;
        var len = clip.Target.Length;
        var order = new List<int>(Rig.Joints);
        var sb = new StringBuilder(4096 + clip.Frames * 200);
        sb.Append("HIERARCHY\n");
        WriteJoint(sb, 0, 0, len, HeadCm, order);
        sb.Append("MOTION\nFrames: ").Append(clip.Frames.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Frame Time: ").Append((1.0 / Math.Max(1, clip.Fps)).ToString("0.000000", CultureInfo.InvariantCulture)).Append('\n');
        Span<Quaternion> world = stackalloc Quaternion[Rig.Joints];
        var cmPerUnit = HeadCm / Unit;
        for (var f = 0; f < clip.Frames; f++)
        {
            for (var j = 0; j < Rig.Joints; j++)
            {
                var c = Rig.Primary[j];
                if (c < 0) { world[j] = j > 0 ? world[Rig.Parent[j]] : Quaternion.Identity; continue; }
                var dir = J(clip.Data, f, (RigJoint)c) - J(clip.Data, f, (RigJoint)j);
                world[j] = dir.LengthSquared() < 1e-12f ? Quaternion.Identity : Arc(Rig.RestDir[c], Vector3.Normalize(dir));
            }
            var root = J(clip.Data, f, RigJoint.Hips) * cmPerUnit;
            sb.Append(F(root.X)).Append(' ').Append(F(root.Y)).Append(' ').Append(F(root.Z));
            foreach (var j in order)
            {
                var local = j == 0 ? world[0] : Quaternion.Inverse(world[Rig.Parent[j]]) * world[j];
                var e = EulerZxy(local);
                sb.Append(' ').Append(F(e.Z)).Append(' ').Append(F(e.X)).Append(' ').Append(F(e.Y));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    private static void WriteJoint(StringBuilder sb, int j, int depth, float[] len, float headCm, List<int> order)
    {
        var pad = new string(' ', depth * 2);
        var off = j == 0 ? Vector3.Zero : Rig.RestDir[j] * len[j] * headCm;
        sb.Append(pad).Append(j == 0 ? "ROOT " : "JOINT ").Append(Rig.Names[j]).Append('\n').Append(pad).Append("{\n");
        sb.Append(pad).Append("  OFFSET ").Append(F(off.X)).Append(' ').Append(F(off.Y)).Append(' ').Append(F(off.Z)).Append('\n');
        sb.Append(pad).Append(j == 0 ? "  CHANNELS 6 Xposition Yposition Zposition Zrotation Xrotation Yrotation\n" : "  CHANNELS 3 Zrotation Xrotation Yrotation\n");
        order.Add(j);
        for (var c = 1; c < Rig.Joints; c++)
        {
            if (Rig.Parent[c] != j) continue;
            if (Rig.Primary[c] < 0)
            {
                var e = Rig.RestDir[c] * len[c] * headCm;
                sb.Append(pad).Append("  End Site\n").Append(pad).Append("  {\n").Append(pad).Append("    OFFSET ").Append(F(e.X)).Append(' ').Append(F(e.Y)).Append(' ').Append(F(e.Z)).Append('\n').Append(pad).Append("  }\n");
            }
            else WriteJoint(sb, c, depth + 1, len, headCm, order);
        }
        sb.Append(pad).Append("}\n");
    }

    public string ExportBvh(MocapClip clip, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToBvh(clip));
        LastAction = "💾 BVH " + Path.GetFileName(path);
        Raise();
        return path;
    }

    // ── 스토리보드 인젝션 ──────────────────────────────────────────────────────

    /// <summary>클립 → 감정/오른손 제스처 구간 컷(1.5초 창 · 동일 키 병합 · 최대 maxCuts · 마지막 구간 = 잔여 전체).</summary>
    public static List<StoryboardScene> BuildScenes(MocapClip clip, DigitalTwinAsset? twin, string? basePrompt, int maxCuts = 6)
    {
        var scenes = new List<StoryboardScene>();
        if (clip.Frames == 0) return scenes;
        var win = Math.Max(1, (int)(clip.Fps * 1.5));
        var n = clip.Frames;
        var segStart = 0;
        var (curE, curG) = WindowKey(clip.Data, 0, Math.Min(n, win));
        for (var s = win; s < n; s += win)
        {
            var (ke, kg) = WindowKey(clip.Data, s, Math.Min(n, s + win));
            if (ke == curE && kg == curG) continue;
            if (scenes.Count >= maxCuts - 1) break;
            scenes.Add(MakeScene(clip, segStart, s, (FacialEmotion)curE, (HandGesture)curG, twin, basePrompt));
            segStart = s; curE = ke; curG = kg;
        }
        scenes.Add(MakeScene(clip, segStart, n, (FacialEmotion)curE, (HandGesture)curG, twin, basePrompt));
        return scenes;
    }

    private static (int Emotion, int Gesture) WindowKey(float[] d, int s, int e)
    {
        Span<int> emo = stackalloc int[6];
        Span<int> ges = stackalloc int[8];
        for (var i = s; i < e; i++)
        {
            var o = i * MocapClip.Stride;
            var em = (int)d[o + MocapClip.EmotionAt]; if ((uint)em < 6) emo[em]++;
            var g = (int)d[o + MocapClip.RightAt]; if ((uint)g < 8) ges[g]++;
        }
        return (ArgMax(emo, false), ArgMax(ges, true));   // 감정 = 다수결 · 제스처 = 오른손 검출 우선
    }

    private static StoryboardScene MakeScene(MocapClip clip, int s, int e, FacialEmotion emotion, HandGesture gesture, DigitalTwinAsset? twin, string? basePrompt)
    {
        var (action, _, _) = AnalyzeMotion(clip.Data, s, e);
        double inten = 0; var n = 0;
        for (var i = s; i < e; i++) { var v = clip.Data[i * MocapClip.Stride + MocapClip.IntensityAt]; if (v > 0) { inten += v; n++; } }
        var sb = new StringBuilder(256);
        sb.Append(string.IsNullOrWhiteSpace(basePrompt) ? (clip.Target == RigProportions.Baby ? "a baby" : "a person") + " performing in front of the camera" : basePrompt.Trim().TrimEnd(','));
        sb.Append(", ").Append(action);
        var g = GestureEnglish(gesture);
        if (g.Length > 0) sb.Append(", ").Append(g);
        sb.Append(", motion-captured performance, natural body mechanics, consistent limb proportions");
        var inj = FacialEmotionPuppeteerEngine.Apply(sb.ToString(), "jittery motion, broken limbs, extra fingers", emotion, n == 0 ? FacialEmotionPuppeteerEngine.DefaultIntensity : inten / n);
        var scene = new StoryboardScene { Prompt = inj.Prompt, NegativePrompt = inj.Negative, Emotion = emotion };
        if (emotion != FacialEmotion.Neutral) scene.EmotionIntensity = inj.Intensity;
        if (twin is not null) AiDigitalTwinGeneratorEngine.ApplyToScene(scene, twin);
        return scene;
    }

    /// <summary>1-Click 인젝션 — 추가(append) 또는 교체. 반환: 추가된 컷 수.</summary>
    public int InjectToProject(StoryboardProject project, MocapClip clip, DigitalTwinAsset? twin, bool append = true, int maxCuts = 6, string? basePrompt = null)
    {
        var scenes = BuildScenes(clip, twin, basePrompt, Math.Clamp(maxCuts, 1, 20));
        if (scenes.Count == 0) return 0;
        if (!append) project.Scenes.Clear();
        project.Scenes.AddRange(scenes);
        LastAction = "🎬 인젝션 " + scenes.Count.ToString(CultureInfo.InvariantCulture) + "컷 · " + clip.ActionLabel + (twin is null ? "" : " · 트윈 " + twin.Name);
        Raise();
        return scenes.Count;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("MocapMapping.OnChange", ex); }
    }

    public void Dispose() => _tracker?.RemoveSink(_sink);
}
