using System.Numerics;

namespace PromptFlowStudio.Services.Mocap;

public enum JointLimitKind : byte { Free = 0, Ball, Hinge }

/// <summary>관절 가동 범위(로컬 · 레스트 대비). Ball = 스윙 원뿔각 + 비틀림 ± · Hinge = 축 회전 [Min, Max]°.</summary>
public readonly record struct JointLimit(JointLimitKind Kind, Vector3 Axis, float MinDeg, float MaxDeg, float SwingDeg, float TwistDeg);

/// <summary>
/// 핀홀 카메라(OpenCV 규약: 카메라 공간 x 오른쪽 · y 아래 · z 전방). 월드 → 픽셀 투영 · 픽셀 → 월드 광선 · DLT 행 생성.
/// </summary>
public struct PinholeCamera
{
    public Vector3 Position, Right, Down, Forward;
    public float Fx, Fy, Cx, Cy;
    public int Width, Height;

    public static PinholeCamera LookAt(Vector3 position, Vector3 target, float fovYDeg, int width, int height)
    {
        var f = Vector3.Normalize(target - position);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        var d = Vector3.Cross(f, r);
        var fy = height * 0.5f / MathF.Tan(fovYDeg * MathF.PI / 360f);
        return new PinholeCamera { Position = position, Right = r, Down = d, Forward = f, Fx = fy, Fy = fy, Cx = width * 0.5f, Cy = height * 0.5f, Width = width, Height = height };
    }

    public readonly bool Project(Vector3 p, out Vector2 uv)
    {
        var rel = p - Position;
        var z = Vector3.Dot(rel, Forward);
        uv = default;
        if (z <= 1e-4f) return false;
        uv = new Vector2(Fx * Vector3.Dot(rel, Right) / z + Cx, Fy * Vector3.Dot(rel, Down) / z + Cy);
        return true;
    }

    public readonly Vector3 Ray(Vector2 uv) => Vector3.Normalize(Forward + Right * ((uv.X - Cx) / Fx) + Down * ((uv.Y - Cy) / Fy));

    public readonly bool InFrame(Vector2 uv) => uv.X >= 0 && uv.Y >= 0 && uv.X < Width && uv.Y < Height;
}

/// <summary>
/// [PromptFlow Studio v16.75.0] 마커리스 모션 캡처 공용 토폴로지 — 전신 24관절(SMPL 순서) + 손 21×2(MediaPipe 순서) + 표정 10 = 76 키포인트.
/// 기준 레스트 포즈 = 1.70 m T-포즈(Y-up · +Z 정면 · +X = 캐릭터 왼쪽) · 관절 한계 · FK.
/// </summary>
public static class MocapSkeleton
{
    public const int Body = 24, HandPoints = 21, Hands = HandPoints * 2, Face = 10, Keypoints = Body + Hands + Face;
    public const int LeftHandBase = Body, RightHandBase = Body + HandPoints, FaceBase = Body + Hands;
    public const float ReferenceHeight = 1.70f;

    public const int Pelvis = 0, LHip = 1, RHip = 2, Spine1 = 3, LKnee = 4, RKnee = 5, Spine2 = 6, LAnkle = 7, RAnkle = 8, Spine3 = 9, LFoot = 10, RFoot = 11,
        Neck = 12, LCollar = 13, RCollar = 14, Head = 15, LShoulder = 16, RShoulder = 17, LElbow = 18, RElbow = 19, LWrist = 20, RWrist = 21, LHand = 22, RHand = 23;

    public static readonly int[] Parent = { -1, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 9, 12, 13, 14, 16, 17, 18, 19, 20, 21 };
    /// <summary>관절 회전이 정렬하는 주 자식(−1 = 말단).</summary>
    public static readonly int[] PrimaryChild = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, -1, -1, 15, 16, 17, -1, 18, 19, 20, 21, 22, 23, -1, -1 };

    public static readonly string[] Names =
    {
        "골반", "왼엉덩이", "오른엉덩이", "척추1", "왼무릎", "오른무릎", "척추2", "왼발목", "오른발목", "가슴", "왼발끝", "오른발끝",
        "목", "왼쇄골", "오른쇄골", "머리", "왼어깨", "오른어깨", "왼팔꿈치", "오른팔꿈치", "왼손목", "오른손목", "왼손", "오른손",
    };

    public static readonly string[] FaceNames = { "왼눈썹", "오른눈썹", "왼눈꺼풀", "오른눈꺼풀", "코", "왼입꼬리", "오른입꼬리", "윗입술", "아랫입술", "턱" };

    /// <summary>부모 기준 오프셋(루트 = 절대 위치).</summary>
    public static readonly Vector3[] RestOffset =
    {
        new(0, 0.95f, 0), new(0.09f, -0.06f, 0), new(-0.09f, -0.06f, 0), new(0, 0.10f, 0),
        new(0, -0.42f, 0), new(0, -0.42f, 0), new(0, 0.13f, 0),
        new(0, -0.41f, 0), new(0, -0.41f, 0), new(0, 0.13f, 0),
        new(0, -0.06f, 0.13f), new(0, -0.06f, 0.13f), new(0, 0.15f, 0),
        new(0.07f, 0.11f, 0), new(-0.07f, 0.11f, 0), new(0, 0.12f, 0),
        new(0.11f, 0.02f, 0), new(-0.11f, 0.02f, 0), new(0.27f, 0, 0), new(-0.27f, 0, 0),
        new(0.25f, 0, 0), new(-0.25f, 0, 0), new(0.08f, 0, 0), new(-0.08f, 0, 0),
    };

    /// <summary>왼손 21점 레스트(손목 기준 · 손 방향 +X · 손바닥 −Y · 엄지 +Z). 오른손은 X 반전.</summary>
    public static readonly Vector3[] HandRest = BuildHand();

    /// <summary>표정 10점 레스트(머리 관절 기준).</summary>
    public static readonly Vector3[] FaceRest =
    {
        new(0.035f, 0.055f, 0.085f), new(-0.035f, 0.055f, 0.085f), new(0.033f, 0.035f, 0.09f), new(-0.033f, 0.035f, 0.09f), new(0, 0.0f, 0.11f),
        new(0.025f, -0.045f, 0.09f), new(-0.025f, -0.045f, 0.09f), new(0, -0.035f, 0.1f), new(0, -0.055f, 0.098f), new(0, -0.09f, 0.08f),
    };

    public static readonly JointLimit[] Limits = BuildLimits();

    private static Vector3[] BuildHand()
    {
        var h = new Vector3[HandPoints];
        // 엄지(1~4) · 검지(5~8) · 중지(9~12) · 약지(13~16) · 소지(17~20)
        ReadOnlySpan<float> spread = stackalloc float[] { 0.035f, 0.022f, 0.006f, -0.01f, -0.025f };
        ReadOnlySpan<float> baseX = stackalloc float[] { 0.02f, 0.085f, 0.09f, 0.085f, 0.075f };
        ReadOnlySpan<float> seg = stackalloc float[] { 0.032f, 0.035f, 0.038f, 0.035f, 0.028f };
        for (var f = 0; f < 5; f++)
            for (var k = 0; k < 4; k++)
                h[1 + f * 4 + k] = new Vector3(baseX[f] + seg[f] * k * (f == 0 ? 0.85f : 1f), 0, spread[f] + (f == 0 ? 0.012f * k : 0));
        return h;
    }

    private static JointLimit[] BuildLimits()
    {
        var l = new JointLimit[Body];
        JointLimit Ball(float swing, float twist) => new(JointLimitKind.Ball, default, 0, 0, swing, twist);
        l[Pelvis] = new JointLimit(JointLimitKind.Free, default, 0, 0, 180, 180);
        l[LHip] = l[RHip] = Ball(120, 45);
        l[Spine1] = l[Spine2] = l[Spine3] = Ball(35, 30);
        l[LKnee] = l[RKnee] = new JointLimit(JointLimitKind.Hinge, Vector3.UnitX, -3, 150, 0, 0);
        l[LAnkle] = l[RAnkle] = Ball(45, 25);
        l[LFoot] = l[RFoot] = Ball(30, 10);
        l[Neck] = Ball(50, 60);
        l[Head] = Ball(45, 50);
        l[LCollar] = l[RCollar] = Ball(25, 15);
        l[LShoulder] = l[RShoulder] = Ball(165, 90);
        l[LElbow] = new JointLimit(JointLimitKind.Hinge, -Vector3.UnitY, -3, 150, 0, 0);
        l[RElbow] = new JointLimit(JointLimitKind.Hinge, Vector3.UnitY, -3, 150, 0, 0);
        l[LWrist] = l[RWrist] = Ball(80, 80);
        l[LHand] = l[RHand] = Ball(30, 20);
        return l;
    }

    /// <summary>레스트 전역 위치(스케일 배율).</summary>
    public static void RestPositions(ReadOnlySpan<Vector3> offsets, Span<Vector3> pos)
    {
        for (var j = 0; j < Body; j++) pos[j] = Parent[j] < 0 ? offsets[j] : pos[Parent[j]] + offsets[j];
    }

    /// <summary>
    /// FK: 관절 j 의 전역 회전 G_j = G_parent · L_j, 자식 위치 = 부모 위치 + G_parent · offset_child(관절 회전은 그 관절의 자식 오프셋을 회전). 무할당.
    /// </summary>
    public static void ForwardKinematics(ReadOnlySpan<Vector3> offsets, ReadOnlySpan<Quaternion> local, Vector3 root, Span<Vector3> pos, Span<Quaternion> global)
    {
        for (var j = 0; j < Body; j++)
        {
            var p = Parent[j];
            if (p < 0) { global[j] = Quaternion.Normalize(local[j]); pos[j] = root; continue; }
            global[j] = Quaternion.Normalize(global[p] * local[j]);
            pos[j] = pos[p] + Vector3.Transform(offsets[j], global[p]);
        }
    }

    /// <summary>손 21점 월드 위치(손목 전역 회전 · 손가락 굽힘 0..1 × 5).</summary>
    public static void HandPositions(bool left, Vector3 wrist, Quaternion wristRot, ReadOnlySpan<float> curl, float scale, Span<Vector3> dst)
    {
        var mirror = left ? 1f : -1f;
        dst[0] = wrist;
        for (var f = 0; f < 5; f++)
        {
            var c = Math.Clamp(curl[f], 0f, 1f);
            var p = HandRest[1 + f * 4];
            var seg = Vector3.Distance(HandRest[2 + f * 4], p);
            var step = c * (f == 0 ? 0.6f : 1.35f) / 3f;   // 관절당 굽힘(rad) — 손바닥(−Y) 쪽
            var angle = 0f;
            for (var k = 0; k < 4; k++)
            {
                if (k > 0)
                {
                    angle += step;
                    p += new Vector3(MathF.Cos(angle), -MathF.Sin(angle), 0) * seg;
                }
                dst[1 + f * 4 + k] = wrist + Vector3.Transform(new Vector3(p.X * mirror, p.Y, p.Z) * scale, wristRot);
            }
        }
    }
}
