using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.DigitalTwin;

/// <summary>MediaPipe BlazePose 33 관절 순서.</summary>
public enum PoseJoint
{
    Nose, LeftEyeInner, LeftEye, LeftEyeOuter, RightEyeInner, RightEye, RightEyeOuter, LeftEar, RightEar, MouthLeft, MouthRight,
    LeftShoulder, RightShoulder, LeftElbow, RightElbow, LeftWrist, RightWrist, LeftPinky, RightPinky, LeftIndex, RightIndex, LeftThumb, RightThumb,
    LeftHip, RightHip, LeftKnee, RightKnee, LeftAnkle, RightAnkle, LeftHeel, RightHeel, LeftFootIndex, RightFootIndex,
}

/// <summary>모캡 프레임(사전 할당 · 재사용) — 포즈 33×(x,y,z,vis) · 안면 468×xyz · 손 2×21×xyz(정규화 영상 좌표 0~1 · z 상대 깊이).</summary>
public sealed class MocapFrame
{
    public const int PoseJoints = 33, PoseStride = 4, FaceLandmarks = 468, HandLandmarks = 21, Hands = 2;
    public const int PoseLength = PoseJoints * PoseStride, FaceLength = FaceLandmarks * 3, HandLength = HandLandmarks * 3, HandsLength = Hands * HandLength;

    public readonly float[] Pose = new float[PoseLength];
    public readonly float[] Face = new float[FaceLength];
    public readonly float[] HandsXyz = new float[HandsLength];
    public bool FaceValid, LeftValid, RightValid;
    public float Confidence;
    public double TimestampMs;
    public long Seq;

    public Span<float> Hand(int side) => HandsXyz.AsSpan(side * HandLength, HandLength);

    public void CopyTo(MocapFrame d)
    {
        Pose.AsSpan().CopyTo(d.Pose);
        Face.AsSpan().CopyTo(d.Face);
        HandsXyz.AsSpan().CopyTo(d.HandsXyz);
        d.FaceValid = FaceValid; d.LeftValid = LeftValid; d.RightValid = RightValid;
        d.Confidence = Confidence; d.TimestampMs = TimestampMs; d.Seq = Seq;
    }
}

/// <summary>프레임 소스 — 최신 프레임이 있으면 dst 에 기록 후 true(핫패스 · 무할당).</summary>
public interface IMotionFrameSource
{
    string Name { get; }
    bool TryRead(MocapFrame dst, double nowMs);
}

/// <summary>
/// 안면 템플릿 — 468 점 파라메트릭(타원 링) + MediaPipe 핵심 인덱스 의미 배치(입술 13/14 · 입꼬리 61/291 · 눈 159/145/386/374 · 눈썹 105/334 · 이마 10 · 턱 152 · 볼 234/454 · 코 1).
/// 중립 상수는 매핑 컨트롤러와 공유(중립 입력 → 가이드 0).
/// </summary>
public static class FaceTemplate
{
    public const int NoseTip = 1, UpperLip = 13, LowerLip = 14, MouthLeft = 61, MouthRight = 291, Forehead = 10, Chin = 152, CheekLeft = 234, CheekRight = 454;
    public const int LeftEyeTop = 159, LeftEyeBottom = 145, RightEyeTop = 386, RightEyeBottom = 374, LeftBrow = 105, RightBrow = 334;
    /// <summary>얼굴 높이(이마~턱) 대비 중립 비율.</summary>
    public const float NeutralEyeGap = 0.045f, NeutralBrowGap = 0.07f, NeutralMouthWidth = 0.42f, MouthCornerScale = 0.04f, MouthOpenScale = 0.12f;

    /// <summary>템플릿 기록(face 길이 ≥ 1404). 표정 파라미터: 입 벌림(0~1) · 미소(−1~1) · 눈썹(−1~1) · 눈 크기(−1~1) · 오므림(0~1) · 요(yaw −1~1).</summary>
    public static void Write(Span<float> face, float cx, float cy, float fw, float fh, float mouthOpen = 0, float smile = 0, float brow = 0, float eye = 0, float pucker = 0, float yaw = 0)
    {
        // 1) 바탕: 12 링 × 39 점 타원(468)
        const int Rings = 12, PerRing = 39;
        for (var r = 0; r < Rings; r++)
        {
            var rr = (r + 1) / (float)Rings;
            for (var k = 0; k < PerRing; k++)
            {
                var a = k * (MathF.PI * 2 / PerRing);
                var i = (r * PerRing + k) * 3;
                face[i] = cx + MathF.Cos(a) * rr * fw * 0.5f + yaw * fw * 0.12f * (1 - rr);
                face[i + 1] = cy + MathF.Sin(a) * rr * fh * 0.5f;
                face[i + 2] = -(1 - rr * rr) * fw * 0.3f;
            }
        }
        // 2) 의미 인덱스(y 아래로 증가 · 얼굴 높이 기준)
        Set(face, Forehead, cx, cy - fh * 0.5f, 0);
        Set(face, Chin, cx, cy + fh * 0.5f, 0);
        Set(face, CheekLeft, cx - fw * 0.5f, cy, 0.1f * fw);
        Set(face, CheekRight, cx + fw * 0.5f, cy, 0.1f * fw);
        Set(face, NoseTip, cx + yaw * fw * 0.15f, cy + fh * 0.05f, -0.25f * fw);
        var lipY = cy + fh * 0.25f;
        var open = Math.Clamp(mouthOpen, 0, 1) * MouthOpenScale * fh;
        Set(face, UpperLip, cx, lipY - open * 0.5f - fh * 0.005f, -0.15f * fw);
        Set(face, LowerLip, cx, lipY + open * 0.5f + fh * 0.005f, -0.15f * fw);
        var halfMouth = NeutralMouthWidth * fw * 0.5f * (1 - 0.3f * Math.Clamp(pucker, 0, 1));
        var cornerY = lipY - Math.Clamp(smile, -1, 1) * MouthCornerScale * fh;
        Set(face, MouthLeft, cx - halfMouth, cornerY, -0.1f * fw);
        Set(face, MouthRight, cx + halfMouth, cornerY, -0.1f * fw);
        var eyeY = cy - fh * 0.08f;
        var gap = NeutralEyeGap * fh * (1 + Math.Clamp(eye, -1, 1));
        var browGap = (NeutralBrowGap + Math.Clamp(brow, -1, 1) * 0.04f) * fh;
        Span<float> ex = stackalloc float[] { cx - fw * 0.2f, cx + fw * 0.2f };
        Set(face, LeftEyeTop, ex[0], eyeY - gap * 0.5f, -0.05f * fw);
        Set(face, LeftEyeBottom, ex[0], eyeY + gap * 0.5f, -0.05f * fw);
        Set(face, RightEyeTop, ex[1], eyeY - gap * 0.5f, -0.05f * fw);
        Set(face, RightEyeBottom, ex[1], eyeY + gap * 0.5f, -0.05f * fw);
        Set(face, LeftBrow, ex[0], eyeY - gap * 0.5f - browGap, -0.06f * fw);
        Set(face, RightBrow, ex[1], eyeY - gap * 0.5f - browGap, -0.06f * fw);
    }

    private static void Set(Span<float> f, int i, float x, float y, float z) { f[i * 3] = x; f[i * 3 + 1] = y; f[i * 3 + 2] = z; }
}

/// <summary>손 템플릿 — 21 점(0 손목 · 엄지 1~4 · 검지 5~8 · 중지 9~12 · 약지 13~16 · 소지 17~20) · 손가락별 굽힘(0 펴짐 ~ 1 주먹).</summary>
public static class HandTemplate
{
    private static readonly float[] BaseAngle = { -1.15f, -0.32f, -0.05f, 0.2f, 0.45f };   // 엄지→소지(라디안, 손 위쪽 기준)
    private static readonly float[] SegLen = { 0.32f, 0.3f, 0.32f, 0.3f, 0.25f };

    /// <summary>손 기록(hand 길이 ≥ 63). curls 길이 5. mirror = 왼손(x 반전).</summary>
    public static void Write(Span<float> hand, float wx, float wy, float scale, float angle, ReadOnlySpan<float> curls, bool mirror)
    {
        var sx = mirror ? -1f : 1f;
        hand[0] = wx; hand[1] = wy; hand[2] = 0;
        for (var f = 0; f < 5; f++)
        {
            var c = Math.Clamp(curls[f], 0, 1);
            // MCP 위치(손바닥 부채꼴)
            var a = angle + BaseAngle[f];
            var palm = f == 0 ? 0.35f : 0.9f;
            float px = wx + sx * MathF.Sin(a) * palm * scale, py = wy - MathF.Cos(a) * palm * scale, pz = 0;
            var dir = a;
            var o = (1 + f * 4) * 3;
            hand[o] = px; hand[o + 1] = py; hand[o + 2] = pz;
            for (var s = 1; s < 4; s++)
            {
                // 굽힘: 관절마다 손바닥 쪽(엄지는 검지 쪽)으로 회전 + 깊이 감소
                dir += (f == 0 ? 0.55f : 1.2f) * c;
                var len = SegLen[f] * scale;
                px += sx * MathF.Sin(dir) * len;
                py -= MathF.Cos(dir) * len;
                pz -= c * len * 0.4f;
                hand[o + s * 3] = px; hand[o + s * 3 + 1] = py; hand[o + s * 3 + 2] = pz;
            }
        }
    }
}

/// <summary>스켈레톤 연결(와이어프레임 렌더용).</summary>
public static class MocapTopology
{
    public static readonly int[] PoseBones =
    {
        11, 12, 11, 13, 13, 15, 15, 19, 15, 21, 12, 14, 14, 16, 16, 20, 16, 22, 11, 23, 12, 24, 23, 24,
        23, 25, 25, 27, 27, 29, 29, 31, 24, 26, 26, 28, 28, 30, 30, 32, 0, 2, 0, 5, 2, 7, 5, 8, 9, 10,
    };

    public static readonly int[] HandBones =
    {
        0, 1, 1, 2, 2, 3, 3, 4, 0, 5, 5, 6, 6, 7, 7, 8, 5, 9, 9, 10, 10, 11, 11, 12, 9, 13, 13, 14, 14, 15, 15, 16, 13, 17, 0, 17, 17, 18, 18, 19, 19, 20,
    };
}

/// <summary>
/// 표준 체형 포즈 빌더 — 머리/어깨/엉덩이 기준 33 관절 배치(팔 끝 = 손목 목표 · 다리 = 스탠스). 합성 퍼포머 · 웹캠 추정 공용.
/// </summary>
public static class PoseBuilder
{
    public static void Write(Span<float> pose, float headX, float headY, float headH, float lwx, float lwy, float rwx, float rwy, float bounce, float vis)
    {
        var sh = headY + headH * 1.1f;               // 어깨선
        var sw = headH * 1.3f;                       // 어깨 반폭
        var hip = sh + headH * 2.6f + bounce;
        var hw = headH * 0.75f;
        J(pose, PoseJoint.Nose, headX, headY + headH * 0.1f, -0.1f, vis);
        J(pose, PoseJoint.LeftEyeInner, headX + headH * 0.08f, headY - headH * 0.05f, -0.08f, vis);
        J(pose, PoseJoint.LeftEye, headX + headH * 0.15f, headY - headH * 0.05f, -0.08f, vis);
        J(pose, PoseJoint.LeftEyeOuter, headX + headH * 0.22f, headY - headH * 0.05f, -0.07f, vis);
        J(pose, PoseJoint.RightEyeInner, headX - headH * 0.08f, headY - headH * 0.05f, -0.08f, vis);
        J(pose, PoseJoint.RightEye, headX - headH * 0.15f, headY - headH * 0.05f, -0.08f, vis);
        J(pose, PoseJoint.RightEyeOuter, headX - headH * 0.22f, headY - headH * 0.05f, -0.07f, vis);
        J(pose, PoseJoint.LeftEar, headX + headH * 0.42f, headY, 0, vis);
        J(pose, PoseJoint.RightEar, headX - headH * 0.42f, headY, 0, vis);
        J(pose, PoseJoint.MouthLeft, headX + headH * 0.12f, headY + headH * 0.3f, -0.07f, vis);
        J(pose, PoseJoint.MouthRight, headX - headH * 0.12f, headY + headH * 0.3f, -0.07f, vis);
        float lsx = headX + sw, rsx = headX - sw;
        J(pose, PoseJoint.LeftShoulder, lsx, sh, 0, vis);
        J(pose, PoseJoint.RightShoulder, rsx, sh, 0, vis);
        Arm(pose, PoseJoint.LeftShoulder, PoseJoint.LeftElbow, PoseJoint.LeftWrist, PoseJoint.LeftPinky, PoseJoint.LeftIndex, PoseJoint.LeftThumb, lsx, sh, lwx, lwy, headH, 1, vis);
        Arm(pose, PoseJoint.RightShoulder, PoseJoint.RightElbow, PoseJoint.RightWrist, PoseJoint.RightPinky, PoseJoint.RightIndex, PoseJoint.RightThumb, rsx, sh, rwx, rwy, headH, -1, vis);
        float lhx = headX + hw, rhx = headX - hw;
        J(pose, PoseJoint.LeftHip, lhx, hip, 0, vis);
        J(pose, PoseJoint.RightHip, rhx, hip, 0, vis);
        var leg = headH * 1.9f;
        var lv = vis * 0.8f;
        J(pose, PoseJoint.LeftKnee, lhx + headH * 0.05f, hip + leg - bounce * 0.5f, -0.05f, lv);
        J(pose, PoseJoint.RightKnee, rhx - headH * 0.05f, hip + leg - bounce * 0.5f, -0.05f, lv);
        J(pose, PoseJoint.LeftAnkle, lhx + headH * 0.1f, hip + leg * 2 - bounce, 0, lv);
        J(pose, PoseJoint.RightAnkle, rhx - headH * 0.1f, hip + leg * 2 - bounce, 0, lv);
        J(pose, PoseJoint.LeftHeel, lhx + headH * 0.1f, hip + leg * 2 - bounce + headH * 0.12f, 0.05f, lv);
        J(pose, PoseJoint.RightHeel, rhx - headH * 0.1f, hip + leg * 2 - bounce + headH * 0.12f, 0.05f, lv);
        J(pose, PoseJoint.LeftFootIndex, lhx + headH * 0.15f, hip + leg * 2 - bounce + headH * 0.18f, -0.25f, lv);
        J(pose, PoseJoint.RightFootIndex, rhx - headH * 0.15f, hip + leg * 2 - bounce + headH * 0.18f, -0.25f, lv);
    }

    private static void Arm(Span<float> p, PoseJoint s, PoseJoint e, PoseJoint w, PoseJoint pinky, PoseJoint index, PoseJoint thumb, float sx, float sy, float wx, float wy, float headH, float side, float vis)
    {
        // 팔 길이 제한(상완+전완 ≈ 2.9 헤드) → 손목 목표 투영 · 팔꿈치 = 중점 + 바깥쪽 굽힘
        var maxReach = headH * 2.9f;
        float dx = wx - sx, dy = wy - sy;
        var d = MathF.Sqrt(dx * dx + dy * dy);
        if (d > maxReach) { wx = sx + dx / d * maxReach; wy = sy + dy / d * maxReach; d = maxReach; }
        var bend = MathF.Sqrt(Math.Max(0, maxReach * maxReach * 0.25f - d * d * 0.25f));
        float nx = d > 1e-5f ? -dy / d : 0, ny = d > 1e-5f ? dx / d : 0;
        if (nx * side < 0) { nx = -nx; ny = -ny; }
        J(p, e, (sx + wx) / 2 + nx * bend, (sy + wy) / 2 + ny * bend, -0.05f, vis);
        J(p, w, wx, wy, -0.1f, vis);
        J(p, pinky, wx + side * headH * 0.08f, wy + headH * 0.18f, -0.1f, vis);
        J(p, index, wx, wy + headH * 0.22f, -0.12f, vis);
        J(p, thumb, wx - side * headH * 0.08f, wy + headH * 0.12f, -0.12f, vis);
    }

    private static void J(Span<float> p, PoseJoint j, float x, float y, float z, float v)
    {
        var i = (int)j * MocapFrame.PoseStride;
        p[i] = x; p[i + 1] = y; p[i + 2] = z; p[i + 3] = v;
    }
}

/// <summary>합성 퍼포머(카메라 없는 프리뷰/테스트) — 손 흔들기 · 고개 · 미소/놀람 · 손가락 세기 주기 동작(결정적).</summary>
public sealed class SyntheticPerformerSource : IMotionFrameSource
{
    private readonly float[] _curls = new float[5];
    public string Name => "합성 퍼포머";

    public bool TryRead(MocapFrame d, double nowMs)
    {
        var t = (float)(nowMs / 1000.0);
        var wave = MathF.Sin(t * 5f);
        float hx = 0.5f + 0.02f * MathF.Sin(t * 0.9f), hy = 0.16f, hh = 0.1f;
        PoseBuilder.Write(d.Pose, hx, hy, hh, 0.72f + 0.06f * wave, 0.12f + 0.03f * MathF.Cos(t * 5f), 0.36f, 0.55f, 0.01f * MathF.Sin(t * 3f), 0.95f);
        var phase = (int)(t / 2) % 4;
        FaceTemplate.Write(d.Face, hx, hy, hh * 1.4f, hh * 1.8f,
            mouthOpen: phase == 2 ? 0.8f : 0.05f, smile: phase == 1 ? 0.8f : 0, brow: phase == 2 ? 0.9f : 0, eye: phase == 2 ? 0.7f : 0, yaw: 0.2f * MathF.Sin(t * 0.9f));
        d.FaceValid = true;
        var count = (int)(t % 5) + 1;   // 손가락 1~5 세기(오른손)
        for (var f = 0; f < 5; f++) _curls[f] = f < count ? 0 : 1;
        HandTemplate.Write(d.Hand(1), d.Pose[(int)PoseJoint.RightWrist * 4], d.Pose[(int)PoseJoint.RightWrist * 4 + 1] - 0.02f, 0.06f, 0, _curls, false);
        for (var f = 0; f < 5; f++) _curls[f] = 0;
        HandTemplate.Write(d.Hand(0), d.Pose[(int)PoseJoint.LeftWrist * 4], d.Pose[(int)PoseJoint.LeftWrist * 4 + 1] - 0.02f, 0.06f, wave * 0.3f, _curls, true);
        d.LeftValid = d.RightValid = true;
        d.Confidence = 0.99f;
        d.TimestampMs = nowMs;
        return true;
    }
}

/// <summary>외부 랜드마크 피드(MediaPipe JS/사이드카) — 최신 1프레임 스테이징(락 · 복사).</summary>
public sealed class ExternalLandmarkSource : IMotionFrameSource
{
    private readonly object _gate = new();
    private readonly MocapFrame _staged = new();
    private bool _fresh;
    public string Name => "외부 MediaPipe";

    public void Push(ReadOnlySpan<float> pose, ReadOnlySpan<float> face, ReadOnlySpan<float> left, ReadOnlySpan<float> right, float confidence, double nowMs)
    {
        lock (_gate)
        {
            pose[..Math.Min(pose.Length, MocapFrame.PoseLength)].CopyTo(_staged.Pose);
            _staged.FaceValid = face.Length >= MocapFrame.FaceLength;
            if (_staged.FaceValid) face[..MocapFrame.FaceLength].CopyTo(_staged.Face);
            _staged.LeftValid = left.Length >= MocapFrame.HandLength;
            if (_staged.LeftValid) left[..MocapFrame.HandLength].CopyTo(_staged.Hand(0));
            _staged.RightValid = right.Length >= MocapFrame.HandLength;
            if (_staged.RightValid) right[..MocapFrame.HandLength].CopyTo(_staged.Hand(1));
            _staged.Confidence = confidence;
            _staged.TimestampMs = nowMs;
            _fresh = true;
        }
    }

    public bool TryRead(MocapFrame dst, double nowMs)
    {
        lock (_gate)
        {
            if (!_fresh) return false;
            _staged.CopyTo(dst);
            _fresh = false;
            return true;
        }
    }
}

/// <summary>
/// 웹캠 BCL 추정기 — RGBA 프레임(≤320×240 샘플링) → 16×12 셀 피부 밀도 · 얼굴(상단 최대 3×3 합) · 손(얼굴 밖 상위 2 셀) ·
/// 입/눈 어두운 비율 → 표정 · 손 블롭 밀도 → 굽힘 → 템플릿 포즈/안면/손 기록(사전 할당 · 무할당).
/// </summary>
public sealed class WebcamHeuristicSource : IMotionFrameSource
{
    public const int GridW = 16, GridH = 12;
    private readonly object _gate = new();
    private readonly MocapFrame _staged = new();
    private readonly int[] _cell = new int[GridW * GridH];
    private readonly int[] _cellN = new int[GridW * GridH];
    private readonly float[] _curls = new float[5];
    private bool _fresh;
    public string Name => "웹캠(BCL 추정)";
    public long Frames { get; private set; }

    /// <summary>RGBA32 프레임 투입(UI 인터롭 스레드).</summary>
    public void PushRgba(ReadOnlySpan<byte> rgba, int w, int h, double nowMs)
    {
        if (w < 16 || h < 12 || rgba.Length < w * h * 4) return;
        lock (_gate)
        {
            Estimate(rgba, w, h, _staged);
            _staged.TimestampMs = nowMs;
            _fresh = true;
            Frames++;
        }
    }

    public bool TryRead(MocapFrame dst, double nowMs)
    {
        lock (_gate)
        {
            if (!_fresh) return false;
            _staged.CopyTo(dst);
            _fresh = false;
            return true;
        }
    }

    private static bool Skin(ReadOnlySpan<byte> p, int i) => AiDigitalTwinGeneratorEngine.IsSkin(p[i], p[i + 1], p[i + 2]);

    private void Estimate(ReadOnlySpan<byte> rgba, int w, int h, MocapFrame d)
    {
        Array.Clear(_cell);
        Array.Clear(_cellN);
        var step = Math.Max(1, Math.Max(w / 320, h / 240));
        for (var y = 0; y < h; y += step)
        {
            var cy = y * GridH / h;
            for (var x = 0; x < w; x += step)
            {
                var c = cy * GridW + x * GridW / w;
                _cellN[c]++;
                if (Skin(rgba, (y * w + x) * 4)) _cell[c]++;
            }
        }
        // 얼굴: 상단 60% 3×3 밀도 합 최대
        int best = -1; float bestScore = 0;
        for (var gy = 0; gy < GridH * 6 / 10; gy++)
            for (var gx = 0; gx < GridW; gx++)
            {
                var s = Density3(gx, gy);
                if (s > bestScore) { bestScore = s; best = gy * GridW + gx; }
            }
        d.FaceValid = best >= 0 && bestScore > 0.9f;
        float fx = 0.5f, fy = 0.2f;
        if (d.FaceValid) { fx = (best % GridW + 0.5f) / GridW; fy = (best / GridW + 0.5f) / GridH; }
        var fw = 2.2f / GridW; var fh = 2.6f / GridH;
        // 손: 얼굴 3×3 밖 밀도 > 0.45 상위 2 셀(좌/우 화면 구분)
        int lc = -1, rc = -1; float ld = 0.45f, rd = 0.45f;
        var fgx = best % GridW; var fgy = best / GridW;
        for (var c = 0; c < _cell.Length; c++)
        {
            int gx = c % GridW, gy = c / GridW;
            if (best >= 0 && Math.Abs(gx - fgx) <= 1 && Math.Abs(gy - fgy) <= 2) continue;
            var dn = _cellN[c] == 0 ? 0 : _cell[c] / (float)_cellN[c];
            if (gx * 2 >= GridW) { if (dn > ld) { ld = dn; lc = c; } }   // 화면 오른쪽 = 피사체 왼손
            else if (dn > rd) { rd = dn; rc = c; }
        }
        var headH = fh * 0.55f;
        float lwx = fx + headH * 1.3f, lwy = fy + headH * 4f, rwx = fx - headH * 1.3f, rwy = lwy;
        d.LeftValid = lc >= 0; d.RightValid = rc >= 0;
        if (d.LeftValid) { lwx = (lc % GridW + 0.5f) / GridW; lwy = (lc / GridW + 0.5f) / GridH; }
        if (d.RightValid) { rwx = (rc % GridW + 0.5f) / GridW; rwy = (rc / GridW + 0.5f) / GridH; }
        PoseBuilder.Write(d.Pose, fx, fy, headH, lwx, lwy, rwx, rwy, 0, d.FaceValid ? 0.7f : 0.2f);
        // 표정: 입(얼굴 하단 1/3 중앙) · 눈 띠 어두운 비율
        float mouthDark = DarkRatio(rgba, w, h, fx - fw * 0.15f, fy + fh * 0.18f, fw * 0.3f, fh * 0.15f);
        float eyeDark = DarkRatio(rgba, w, h, fx - fw * 0.3f, fy - fh * 0.12f, fw * 0.6f, fh * 0.1f);
        FaceTemplate.Write(d.Face, fx, fy, fw * 0.8f, fh * 0.75f, mouthOpen: Math.Clamp((mouthDark - 0.1f) * 2.5f, 0, 1), eye: Math.Clamp((eyeDark - 0.25f) * 2f, -1, 1));
        // 손 굽힘: 블롭 밀도 높음(주먹) → 굽힘 · 낮음(펼침) → 폄
        WriteHand(d.Hand(0), lwx, lwy, ld, true);
        WriteHand(d.Hand(1), rwx, rwy, rd, false);
        d.Confidence = Math.Clamp(bestScore / 6f, 0, 1);
    }

    private void WriteHand(Span<float> hand, float x, float y, float density, bool mirror)
    {
        var curl = Math.Clamp((density - 0.55f) * 2.5f, 0, 1);
        for (var f = 0; f < 5; f++) _curls[f] = curl;
        HandTemplate.Write(hand, x, y, 0.05f, 0, _curls, mirror);
    }

    private float Density3(int gx, int gy)
    {
        float s = 0;
        for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int x = gx + dx, y = gy + dy;
                if ((uint)x >= GridW || (uint)y >= GridH) continue;
                var c = y * GridW + x;
                if (_cellN[c] > 0) s += _cell[c] / (float)_cellN[c];
            }
        return s;
    }

    private static float DarkRatio(ReadOnlySpan<byte> rgba, int w, int h, float x, float y, float rw, float rh)
    {
        int x0 = Math.Clamp((int)(x * w), 0, w - 1), y0 = Math.Clamp((int)(y * h), 0, h - 1);
        int x1 = Math.Clamp((int)((x + rw) * w), x0 + 1, w), y1 = Math.Clamp((int)((y + rh) * h), y0 + 1, h);
        int dark = 0, n = 0;
        for (var yy = y0; yy < y1; yy += 2)
            for (var xx = x0; xx < x1; xx += 2)
            {
                var i = (yy * w + xx) * 4;
                if (rgba[i] + rgba[i + 1] + rgba[i + 2] < 180) dark++;
                n++;
            }
        return n == 0 ? 0 : dark / (float)n;
    }
}

/// <summary>
/// [PromptFlow Studio v16.36.0] 🎥 단일 카메라 실시간 AI 모션 캡처 추적기.
///   ▸ 33 관절 3D 골격 · 468 안면 랜드마크 · 21×2 손가락 관절 — 소스 교체형(웹캠 BCL 추정 · 외부 MediaPipe 피드 · 합성 퍼포머).
///   ▸ 60FPS 전용 스레드 루프(Sleep + 스핀 마감 · Stopwatch 데드라인) · 3중 버퍼(prev/front/back) 스왑 · 무할당 틱.
///   ▸ 렌더 스무딩: prev→front 보간 샘플(Vector&lt;float&gt; SIMD 가속 · 스칼라 꼬리).
///   ▸ 프레임 싱크(매핑 컨트롤러): 캐시 배열 스냅샷 순회 · 예외 격리 · HUD OnChange 250ms 스로틀.
/// </summary>
public sealed class RealtimeWebcamMotionCaptureTracker : IDisposable
{
    public const int TargetFps = 60;
    private const double FrameMs = 1000.0 / TargetFps;

    private readonly object _gate = new();
    private MocapFrame _prev = new(), _front = new(), _back = new();
    private Action<MocapFrame>[] _sinks = Array.Empty<Action<MocapFrame>>();
    private IMotionFrameSource? _source;
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private long _frames, _ticks, _sinkErrors, _seq;
    private double _fps, _lastFrameMs = -1, _lastRaiseMs, _tickCostMs;
    private bool _disposed;

    public event Action? OnChange;

    public WebcamHeuristicSource Webcam { get; } = new();
    public ExternalLandmarkSource External { get; } = new();
    public SyntheticPerformerSource Synthetic { get; } = new();

    public bool IsRunning => _thread is not null;
    public string SourceName => _source?.Name ?? "없음";
    public long Frames => Interlocked.Read(ref _frames);
    public long Ticks => Interlocked.Read(ref _ticks);
    public long SinkErrors => Interlocked.Read(ref _sinkErrors);
    public double Fps => Volatile.Read(ref _fps);
    public double TickCostMs => Volatile.Read(ref _tickCostMs);
    public bool SimdAccelerated => Vector.IsHardwareAccelerated;
    public float Confidence { get { lock (_gate) return _front.Confidence; } }
    public string? LastAction { get; private set; }

    public void AddSink(Action<MocapFrame> sink)
    {
        lock (_gate) { var a = new Action<MocapFrame>[_sinks.Length + 1]; _sinks.CopyTo(a, 0); a[^1] = sink; _sinks = a; }
    }

    public void RemoveSink(Action<MocapFrame> sink)
    {
        lock (_gate)
        {
            var i = Array.IndexOf(_sinks, sink);
            if (i < 0) return;
            var a = new Action<MocapFrame>[_sinks.Length - 1];
            for (int k = 0, j = 0; k < _sinks.Length; k++) if (k != i) a[j++] = _sinks[k];
            _sinks = a;
        }
    }

    /// <summary>소스만 지정(루프 미기동 · 외부 Step 구동/테스트).</summary>
    public void Attach(IMotionFrameSource source) => Volatile.Write(ref _source, source);

    /// <summary>소스 지정 후 60FPS 루프 시작(중복 시작 = 소스 교체).</summary>
    public void Start(IMotionFrameSource source)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Volatile.Write(ref _source, source);
        if (_thread is not null) { LastAction = "🔁 소스 교체 · " + source.Name; Raise(); return; }
        var cts = new CancellationTokenSource();
        _cts = cts;
        _lastFrameMs = -1;
        var t = new Thread(() => Loop(cts.Token)) { IsBackground = true, Name = "PFS-Mocap60", Priority = ThreadPriority.AboveNormal };
        _thread = t;
        t.Start();
        LastAction = "▶ 추적 시작 · " + source.Name;
        Raise();
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        var t = Interlocked.Exchange(ref _thread, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        if (t is not null && t != Thread.CurrentThread) t.Join(500);
        cts.Dispose();
        Volatile.Write(ref _fps, 0);
        LastAction = "■ 추적 정지";
        Raise();
    }

    private void Loop(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var next = 0.0;
        while (!ct.IsCancellationRequested)
        {
            try { Step(sw.Elapsed.TotalMilliseconds); }
            catch (Exception ex) { Program.Log("Mocap.Tick", ex); }
            next += FrameMs;
            var now = sw.Elapsed.TotalMilliseconds;
            if (next < now - FrameMs * 4) next = now;   // 지연 누적 방지
            var remain = next - now;
            if (remain > 2) Thread.Sleep((int)(remain - 1.5));
            while (sw.Elapsed.TotalMilliseconds < next && !ct.IsCancellationRequested) Thread.SpinWait(64);
        }
    }

    /// <summary>1틱(테스트/루프 공용 · 무할당) — 새 프레임이면 버퍼 회전 · 싱크 통지. 반환: 새 프레임 여부.</summary>
    public bool Step(double nowMs)
    {
        var t0 = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _ticks);
        var src = Volatile.Read(ref _source);
        if (src is null || !src.TryRead(_back, nowMs)) return false;
        MocapFrame cur;
        Action<MocapFrame>[] sinks;
        lock (_gate)
        {
            _back.Seq = ++_seq;
            (_prev, _front, _back) = (_front, _back, _prev);
            cur = _front;
            sinks = _sinks;
        }
        Interlocked.Increment(ref _frames);
        if (_lastFrameMs >= 0)
        {
            var dt = Math.Max(0.1, nowMs - _lastFrameMs);
            var inst = 1000.0 / dt;
            Volatile.Write(ref _fps, _fps <= 0 ? inst : _fps * 0.9 + inst * 0.1);
        }
        _lastFrameMs = nowMs;
        for (var i = 0; i < sinks.Length; i++)
        {
            try { sinks[i](cur); }
            catch (Exception ex) { Interlocked.Increment(ref _sinkErrors); Program.Log("Mocap.Sink", ex); }
        }
        Volatile.Write(ref _tickCostMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        if (nowMs - _lastRaiseMs >= 250) { _lastRaiseMs = nowMs; Raise(); }
        return true;
    }

    /// <summary>최신 프레임 복사(UI · 락).</summary>
    public void CopyLatest(MocapFrame dst)
    {
        lock (_gate) _front.CopyTo(dst);
    }

    /// <summary>렌더 스무딩 — prev→front 포즈 보간(alpha 0~1 · dst 길이 ≥ 132 · SIMD).</summary>
    public void SampleForRender(float alpha, Span<float> dst)
    {
        lock (_gate) Lerp(_prev.Pose, _front.Pose, Math.Clamp(alpha, 0, 1), dst);
    }

    /// <summary>a + (b − a)·t (Vector&lt;float&gt; 가속 · 스칼라 꼬리).</summary>
    public static void Lerp(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float t, Span<float> dst)
    {
        var n = Math.Min(Math.Min(a.Length, b.Length), dst.Length);
        var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vt = new Vector<float>(t);
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..n]);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..n]);
            var vd = MemoryMarshal.Cast<float, Vector<float>>(dst[..n]);
            for (var k = 0; k < va.Length; k++) vd[k] = va[k] + (vb[k] - va[k]) * vt;
            i = va.Length * Vector<float>.Count;
        }
        for (; i < n; i++) dst[i] = a[i] + (b[i] - a[i]) * t;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("Mocap.OnChange", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }
}
