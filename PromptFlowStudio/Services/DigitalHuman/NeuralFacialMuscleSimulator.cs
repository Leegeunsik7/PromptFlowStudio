using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.DigitalHuman;

/// <summary>ARKit 표준 52 블렌드셰이프(순서 = Apple ARFaceAnchor.BlendShapeLocation 알파벳순 · Left = 피사체 기준 왼쪽 = +X).</summary>
public enum ArkitBlendshape : byte
{
    BrowDownLeft, BrowDownRight, BrowInnerUp, BrowOuterUpLeft, BrowOuterUpRight,
    CheekPuff, CheekSquintLeft, CheekSquintRight,
    EyeBlinkLeft, EyeBlinkRight, EyeLookDownLeft, EyeLookDownRight, EyeLookInLeft, EyeLookInRight, EyeLookOutLeft, EyeLookOutRight,
    EyeLookUpLeft, EyeLookUpRight, EyeSquintLeft, EyeSquintRight, EyeWideLeft, EyeWideRight,
    JawForward, JawLeft, JawOpen, JawRight,
    MouthClose, MouthDimpleLeft, MouthDimpleRight, MouthFrownLeft, MouthFrownRight, MouthFunnel, MouthLeft,
    MouthLowerDownLeft, MouthLowerDownRight, MouthPressLeft, MouthPressRight, MouthPucker, MouthRight,
    MouthRollLower, MouthRollUpper, MouthShrugLower, MouthShrugUpper, MouthSmileLeft, MouthSmileRight,
    MouthStretchLeft, MouthStretchRight, MouthUpperUpLeft, MouthUpperUpRight,
    NoseSneerLeft, NoseSneerRight, TongueOut,
}

public enum FacePreset : byte { Neutral, Joy, Sadness, Anger, Surprise, Fear, Disgust }

public enum Viseme : byte { Rest, A, E, I, O, U, M, F }

/// <summary>표정 목표 레이어(합산 후 0..1 클램프 → 근육 활성 입력).</summary>
public enum FaceLayer : byte { Expression, Speech, Gaze, Manual }

/// <summary>
/// [PromptFlow Studio v16.82.0] 👤 실시간 표정 근육 시뮬레이터 + FLAME 스타일 파이프라인(<see cref="Step"/> 무할당).
/// ① 목표 = 4 레이어(표정 · 발화 비짐 · 시선 · 수동) 합산 → ② 길항근 억제(턱 열림 ↔ 입 다묾 · 미소 ↔ 찡그림 · 눈 크게 ↔ 깜빡임) →
/// ③ Hill형 1차 활성 동역학(수축 τ 35 ms / 이완 τ 90 ms 비대칭) → ④ 연조직 2차 스프링-댐퍼(6 Hz · ζ 0.75 · 미세 오버슈트) →
/// ⑤ 기하 합성 [SIMD]: 중립 + 정체성 β 8 + ARKit 52 델타 + FACS 조합 교정 셰이프 3(곱 항) → ⑥ 턱 관절 선형 블렌드 스키닝(FLAME 턱 회전/측방/전방) → ⑦ 격자 법선.
/// 메시 = 절차 머리(위경도 격자 48×40 · 코/입술/눈두덩/광대 특징) · 셰이프 델타 = 해부 영역 타원 감쇠 로브(외부 스캔/학습 모델 아님).
/// </summary>
public sealed class NeuralFacialMuscleSimulator
{
    public const int ShapeCount = 52, IdentityCount = 8, CorrectiveCount = 3, LayerCount = 4;
    public const int GridW = 48, GridH = 40, VertexCount = GridW * GridH;
    public const float HeadA = 0.078f, HeadB = 0.112f, HeadC = 0.095f, ThetaMax = 1.7f, PhiMin = -1.15f, PhiMax = 1.25f;
    public const float TauActivate = 0.035f, TauRelease = 0.09f, TissueHz = 6f, TissueDamping = 0.75f, Inhibition = 0.6f;
    public const float JawMaxRad = 0.32f;
    public static readonly Vector3 JawPivot = new(0f, -0.012f, -0.035f);

    private static readonly string[] Names =
    {
        "browDownLeft", "browDownRight", "browInnerUp", "browOuterUpLeft", "browOuterUpRight",
        "cheekPuff", "cheekSquintLeft", "cheekSquintRight",
        "eyeBlinkLeft", "eyeBlinkRight", "eyeLookDownLeft", "eyeLookDownRight", "eyeLookInLeft", "eyeLookInRight", "eyeLookOutLeft", "eyeLookOutRight",
        "eyeLookUpLeft", "eyeLookUpRight", "eyeSquintLeft", "eyeSquintRight", "eyeWideLeft", "eyeWideRight",
        "jawForward", "jawLeft", "jawOpen", "jawRight",
        "mouthClose", "mouthDimpleLeft", "mouthDimpleRight", "mouthFrownLeft", "mouthFrownRight", "mouthFunnel", "mouthLeft",
        "mouthLowerDownLeft", "mouthLowerDownRight", "mouthPressLeft", "mouthPressRight", "mouthPucker", "mouthRight",
        "mouthRollLower", "mouthRollUpper", "mouthShrugLower", "mouthShrugUpper", "mouthSmileLeft", "mouthSmileRight",
        "mouthStretchLeft", "mouthStretchRight", "mouthUpperUpLeft", "mouthUpperUpRight",
        "noseSneerLeft", "noseSneerRight", "tongueOut",
    };

    private static readonly string[] IdentityNames = { "얼굴 폭", "얼굴 길이", "코 크기", "턱 폭", "볼 볼륨", "눈썹뼈", "입술 두께", "턱끝 길이" };

    /// <summary>길항근 쌍(서로의 활성이 상대 목표를 억제).</summary>
    private static readonly (ArkitBlendshape A, ArkitBlendshape B)[] Antagonists =
    {
        (ArkitBlendshape.JawOpen, ArkitBlendshape.MouthClose),
        (ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthFrownLeft), (ArkitBlendshape.MouthSmileRight, ArkitBlendshape.MouthFrownRight),
        (ArkitBlendshape.BrowDownLeft, ArkitBlendshape.BrowOuterUpLeft), (ArkitBlendshape.BrowDownRight, ArkitBlendshape.BrowOuterUpRight),
        (ArkitBlendshape.EyeWideLeft, ArkitBlendshape.EyeBlinkLeft), (ArkitBlendshape.EyeWideRight, ArkitBlendshape.EyeBlinkRight),
        (ArkitBlendshape.MouthPucker, ArkitBlendshape.MouthStretchLeft), (ArkitBlendshape.MouthPucker, ArkitBlendshape.MouthStretchRight),
    };

    /// <summary>중립 / 현재 정점(SoA 블록 [X | Y | Z] · 길이 3V).</summary>
    public readonly float[] Neutral = new float[VertexCount * 3], Pos = new float[VertexCount * 3];
    public readonly float[] NX = new float[VertexCount], NY = new float[VertexCount], NZ = new float[VertexCount];
    /// <summary>턱 관절 스키닝 가중치(0..1).</summary>
    public readonly float[] JawWeight = new float[VertexCount];
    private readonly float[][] _delta = new float[ShapeCount][], _identity = new float[IdentityCount][], _corrective = new float[CorrectiveCount][];
    private readonly bool[] _empty = new bool[ShapeCount];
    private readonly float[] _layers = new float[LayerCount * ShapeCount], _target = new float[ShapeCount], _act = new float[ShapeCount];
    private readonly float[] _x = new float[ShapeCount], _v = new float[ShapeCount], _w = new float[ShapeCount], _beta = new float[IdentityCount];
    private readonly float[] _corrW = new float[CorrectiveCount];
    private long _identityVersion;

    public NeuralFacialMuscleSimulator()
    {
        BuildMesh();
        BuildShapes();
        BuildIdentity();
        Synthesize();
    }

    public static string Name(ArkitBlendshape k) => Names[(int)k];
    public static string Name(int k) => Names[k];
    public static string IdentityName(int k) => IdentityNames[k];

    /// <summary>현재 출력 웨이트(0..1 · ARKit 순서).</summary>
    public ReadOnlySpan<float> Weights => _w;
    public float Weight(ArkitBlendshape k) => _w[(int)k];
    public float Target(ArkitBlendshape k) => _target[(int)k];
    public float Activation(ArkitBlendshape k) => _act[(int)k];
    public float Identity(int k) => _beta[k];
    public float CorrectiveWeight(int k) => _corrW[k];
    public bool IsJointDriven(ArkitBlendshape k) => k is ArkitBlendshape.JawOpen or ArkitBlendshape.JawLeft or ArkitBlendshape.JawRight or ArkitBlendshape.JawForward;

    public int ActiveShapes { get; private set; }
    public double LastStepMs { get; private set; }
    public double MaxStepMs { get; private set; }
    public long Frames { get; private set; }
    public float MaxDisplacement { get; private set; }
    public float JawAngle { get; private set; }
    public FacePreset Preset { get; private set; }
    public Viseme CurrentViseme { get; private set; }
    public long Version { get; private set; }

    public Vector3 Position(int v) => new(Pos[v], Pos[VertexCount + v], Pos[2 * VertexCount + v]);
    public Vector3 NeutralPosition(int v) => new(Neutral[v], Neutral[VertexCount + v], Neutral[2 * VertexCount + v]);
    public Vector3 Normal(int v) => new(NX[v], NY[v], NZ[v]);
    public static int Index(int ix, int iy) => iy * GridW + ix;
    public static int Mirror(int v) => v / GridW * GridW + (GridW - 1 - v % GridW);

    /// <summary>안구 중심(side +1 = 왼눈 · −1 = 오른눈) — 중립 메시 기준 눈두덩 안쪽.</summary>
    public static Vector3 EyeCenter(int side) => new(side * 0.032f, 0.026f, 0.072f);
    public const float EyeRadius = 0.0118f;

    // ───────────────────────── 메시 ─────────────────────────

    private static float Gauss(float dx, float dy, float sx, float sy) => MathF.Exp(-(dx * dx / (sx * sx) + dy * dy / (sy * sy)));

    private void BuildMesh()
    {
        for (var iy = 0; iy < GridH; iy++)
        {
            var phi = PhiMin + (PhiMax - PhiMin) * iy / (GridH - 1);
            for (var ix = 0; ix < GridW; ix++)
            {
                var th = -ThetaMax + 2f * ThetaMax * ix / (GridW - 1);
                var x = HeadA * MathF.Cos(phi) * MathF.Sin(th);
                var y = HeadB * MathF.Sin(phi);
                var z = HeadC * MathF.Cos(phi) * MathF.Cos(th);
                if (y < 0f) x *= 1f - 0.28f * (y / HeadB) * (y / HeadB); // 턱선 좁힘
                if (z > 0f)
                {
                    var ax = MathF.Abs(x);
                    var f = 0.022f * Gauss(x, y + 0.004f, 0.009f, 0.011f)                       // 코끝
                          + 0.012f * Gauss(x, y - 0.018f, 0.006f, 0.02f)                        // 콧대
                          - 0.010f * Gauss(ax - 0.032f, y - 0.024f, 0.014f, 0.009f)              // 눈두덩
                          + 0.004f * Gauss(ax - 0.032f, y - 0.043f, 0.018f, 0.005f)              // 눈썹뼈
                          + 0.006f * Gauss(x, y + 0.036f, 0.02f, 0.005f)                        // 윗입술
                          + 0.006f * Gauss(x, y + 0.051f, 0.019f, 0.006f)                       // 아랫입술
                          + 0.006f * Gauss(x, y + 0.086f, 0.016f, 0.01f)                        // 턱끝
                          + 0.004f * Gauss(ax - 0.048f, y, 0.014f, 0.012f);                     // 광대
                    z += f * MathF.Min(1f, z / 0.05f);
                }
                var v = Index(ix, iy);
                Neutral[v] = x; Neutral[VertexCount + v] = y; Neutral[2 * VertexCount + v] = z;
                // 턱 관절 영역: 입선(−0.044) 아래 · 전면부(뒤통수 제외)
                var jw = Math.Clamp((-0.041f - y) / 0.008f, 0f, 1f) * Math.Clamp((z + 0.02f) / 0.04f, 0f, 1f);
                JawWeight[v] = jw * jw * (3f - 2f * jw);
            }
        }
    }

    private float[] NewShape() => new float[VertexCount * 3];

    /// <summary>타원 감쇠 로브(얼굴 좌표 중심 · 반경 · 변위 m) 누적 — 전면(z &gt; 0.02)만.</summary>
    private void Lobe(float[] d, float cx, float cy, float rx, float ry, float dx, float dy, float dz)
    {
        for (var v = 0; v < VertexCount; v++)
        {
            var z = Neutral[2 * VertexCount + v];
            if (z < 0.02f) continue;
            var ex = (Neutral[v] - cx) / rx; var ey = (Neutral[VertexCount + v] - cy) / ry;
            var e = ex * ex + ey * ey;
            if (e >= 1f) continue;
            var w = (1f - e) * (1f - e);
            d[v] += w * dx; d[VertexCount + v] += w * dy; d[2 * VertexCount + v] += w * dz;
        }
    }

    private void Def(ArkitBlendshape k, float cx, float cy, float rx, float ry, float dx, float dy, float dz)
    {
        var d = _delta[(int)k] ??= NewShape();
        Lobe(d, cx, cy, rx, ry, dx, dy, dz);
    }

    /// <summary>좌우 쌍(Left = +X) 정의 — dx 는 바깥쪽 양(+)으로 지정.</summary>
    private void Pair(ArkitBlendshape left, ArkitBlendshape right, float cx, float cy, float rx, float ry, float dxOut, float dy, float dz)
    {
        Def(left, cx, cy, rx, ry, dxOut, dy, dz);
        Def(right, -cx, cy, rx, ry, -dxOut, dy, dz);
    }

    private void BuildShapes()
    {
        Pair(ArkitBlendshape.BrowDownLeft, ArkitBlendshape.BrowDownRight, 0.03f, 0.045f, 0.022f, 0.012f, 0f, -0.006f, -0.001f);
        Pair(ArkitBlendshape.BrowDownLeft, ArkitBlendshape.BrowDownRight, 0.012f, 0.04f, 0.01f, 0.01f, -0.003f, -0.003f, 0f);
        Def(ArkitBlendshape.BrowInnerUp, 0.013f, 0.045f, 0.012f, 0.012f, 0f, 0.007f, 0f);
        Def(ArkitBlendshape.BrowInnerUp, -0.013f, 0.045f, 0.012f, 0.012f, 0f, 0.007f, 0f);
        Pair(ArkitBlendshape.BrowOuterUpLeft, ArkitBlendshape.BrowOuterUpRight, 0.04f, 0.045f, 0.015f, 0.012f, 0f, 0.007f, 0f);
        Def(ArkitBlendshape.CheekPuff, 0.045f, -0.03f, 0.025f, 0.025f, 0.006f, 0f, 0.008f);
        Def(ArkitBlendshape.CheekPuff, -0.045f, -0.03f, 0.025f, 0.025f, -0.006f, 0f, 0.008f);
        Pair(ArkitBlendshape.CheekSquintLeft, ArkitBlendshape.CheekSquintRight, 0.042f, -0.005f, 0.02f, 0.015f, 0f, 0.005f, 0.002f);
        Pair(ArkitBlendshape.EyeBlinkLeft, ArkitBlendshape.EyeBlinkRight, 0.032f, 0.028f, 0.016f, 0.008f, 0f, -0.009f, 0.001f);
        Pair(ArkitBlendshape.EyeLookDownLeft, ArkitBlendshape.EyeLookDownRight, 0.032f, 0.027f, 0.014f, 0.007f, 0f, -0.003f, 0f);
        Pair(ArkitBlendshape.EyeLookUpLeft, ArkitBlendshape.EyeLookUpRight, 0.032f, 0.028f, 0.014f, 0.007f, 0f, 0.0025f, 0f);
        Pair(ArkitBlendshape.EyeLookInLeft, ArkitBlendshape.EyeLookInRight, 0.032f, 0.026f, 0.014f, 0.01f, -0.0015f, 0f, 0f);
        Pair(ArkitBlendshape.EyeLookOutLeft, ArkitBlendshape.EyeLookOutRight, 0.032f, 0.026f, 0.014f, 0.01f, 0.0015f, 0f, 0f);
        Pair(ArkitBlendshape.EyeSquintLeft, ArkitBlendshape.EyeSquintRight, 0.032f, 0.012f, 0.015f, 0.007f, 0f, 0.004f, 0.001f);
        Pair(ArkitBlendshape.EyeWideLeft, ArkitBlendshape.EyeWideRight, 0.032f, 0.028f, 0.016f, 0.008f, 0f, 0.004f, 0f);
        Def(ArkitBlendshape.JawOpen, 0f, -0.05f, 0.02f, 0.008f, 0f, -0.002f, 0f); // 아랫입술 이완(회전은 관절)
        Def(ArkitBlendshape.MouthClose, 0f, -0.047f, 0.022f, 0.01f, 0f, 0.004f, 0f);
        Pair(ArkitBlendshape.MouthDimpleLeft, ArkitBlendshape.MouthDimpleRight, 0.03f, -0.045f, 0.008f, 0.008f, 0.002f, 0f, -0.003f);
        Pair(ArkitBlendshape.MouthFrownLeft, ArkitBlendshape.MouthFrownRight, 0.026f, -0.046f, 0.012f, 0.012f, 0f, -0.006f, 0f);
        Def(ArkitBlendshape.MouthFunnel, 0f, -0.043f, 0.022f, 0.014f, 0f, 0f, 0.006f);
        Def(ArkitBlendshape.MouthFunnel, 0.025f, -0.043f, 0.01f, 0.01f, -0.006f, 0f, 0f);
        Def(ArkitBlendshape.MouthFunnel, -0.025f, -0.043f, 0.01f, 0.01f, 0.006f, 0f, 0f);
        Def(ArkitBlendshape.MouthLeft, 0f, -0.044f, 0.03f, 0.015f, 0.007f, 0f, 0f);
        Def(ArkitBlendshape.MouthRight, 0f, -0.044f, 0.03f, 0.015f, -0.007f, 0f, 0f);
        Pair(ArkitBlendshape.MouthLowerDownLeft, ArkitBlendshape.MouthLowerDownRight, 0.012f, -0.051f, 0.014f, 0.007f, 0f, -0.005f, 0f);
        Pair(ArkitBlendshape.MouthPressLeft, ArkitBlendshape.MouthPressRight, 0.012f, -0.043f, 0.016f, 0.008f, 0f, 0f, -0.002f);
        Def(ArkitBlendshape.MouthPucker, 0f, -0.043f, 0.026f, 0.016f, 0f, 0f, 0.008f);
        Def(ArkitBlendshape.MouthPucker, 0.025f, -0.043f, 0.01f, 0.01f, -0.008f, 0f, 0.002f);
        Def(ArkitBlendshape.MouthPucker, -0.025f, -0.043f, 0.01f, 0.01f, 0.008f, 0f, 0.002f);
        Def(ArkitBlendshape.MouthRollLower, 0f, -0.05f, 0.02f, 0.006f, 0f, 0.002f, -0.004f);
        Def(ArkitBlendshape.MouthRollUpper, 0f, -0.036f, 0.02f, 0.006f, 0f, -0.002f, -0.004f);
        Def(ArkitBlendshape.MouthShrugLower, 0f, -0.058f, 0.02f, 0.012f, 0f, 0.004f, 0.002f);
        Def(ArkitBlendshape.MouthShrugUpper, 0f, -0.034f, 0.02f, 0.008f, 0f, 0.003f, 0.001f);
        Pair(ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight, 0.026f, -0.044f, 0.014f, 0.012f, 0.006f, 0.006f, -0.002f);
        Pair(ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight, 0.026f, -0.045f, 0.014f, 0.012f, 0.007f, -0.002f, -0.001f);
        Pair(ArkitBlendshape.MouthUpperUpLeft, ArkitBlendshape.MouthUpperUpRight, 0.012f, -0.035f, 0.012f, 0.008f, 0f, 0.004f, 0f);
        Pair(ArkitBlendshape.NoseSneerLeft, ArkitBlendshape.NoseSneerRight, 0.012f, 0.002f, 0.01f, 0.012f, 0f, 0.004f, 0.001f);
        Def(ArkitBlendshape.TongueOut, 0f, -0.048f, 0.012f, 0.006f, 0f, -0.002f, 0.006f);
        for (var k = 0; k < ShapeCount; k++)
        {
            _empty[k] = _delta[k] is null;
            _delta[k] ??= NewShape();
        }
        // FACS 조합 교정(곱 항): 0 = 턱 열림×미소(입꼬리 들림 보정) · 1 = 깜빡임×찡그림(눈꺼풀 주름) · 2 = 안쪽 눈썹 올림×내림(미간 주름)
        _corrective[0] = NewShape();
        Lobe(_corrective[0], 0.026f, -0.046f, 0.014f, 0.012f, 0.002f, 0.004f, -0.002f);
        Lobe(_corrective[0], -0.026f, -0.046f, 0.014f, 0.012f, -0.002f, 0.004f, -0.002f);
        _corrective[1] = NewShape();
        Lobe(_corrective[1], 0.032f, 0.02f, 0.016f, 0.008f, 0f, -0.002f, -0.001f);
        Lobe(_corrective[1], -0.032f, 0.02f, 0.016f, 0.008f, 0f, -0.002f, -0.001f);
        _corrective[2] = NewShape();
        Lobe(_corrective[2], 0.008f, 0.042f, 0.008f, 0.01f, -0.002f, 0f, -0.001f);
        Lobe(_corrective[2], -0.008f, 0.042f, 0.008f, 0.01f, 0.002f, 0f, -0.001f);
    }

    /// <summary>FLAME 정체성 기저 β(8 · ±1σ 당 변위 m).</summary>
    private void BuildIdentity()
    {
        for (var k = 0; k < IdentityCount; k++) _identity[k] = NewShape();
        for (var v = 0; v < VertexCount; v++)
        {
            float x = Neutral[v], y = Neutral[VertexCount + v], z = Neutral[2 * VertexCount + v];
            var front = Math.Clamp(z / 0.05f, 0f, 1f); var ax = MathF.Abs(x); var sx = MathF.Sign(x);
            _identity[0][v] = 0.06f * x;                                                                    // 얼굴 폭
            _identity[1][VertexCount + v] = 0.06f * y;                                                      // 얼굴 길이
            _identity[2][2 * VertexCount + v] = 0.006f * Gauss(x, y + 0.004f, 0.011f, 0.016f) * front;      // 코 크기
            _identity[3][v] = 0.008f * sx * Math.Clamp(-y / HeadB, 0f, 1f);                                  // 턱 폭
            var ck = Gauss(ax - 0.045f, y + 0.02f, 0.02f, 0.02f) * front;                                    // 볼 볼륨
            _identity[4][v] = 0.003f * sx * ck; _identity[4][2 * VertexCount + v] = 0.003f * ck;
            _identity[5][2 * VertexCount + v] = 0.003f * Gauss(ax - 0.032f, y - 0.043f, 0.02f, 0.006f) * front; // 눈썹뼈
            var lip = Gauss(x, y + 0.036f, 0.02f, 0.005f) + Gauss(x, y + 0.051f, 0.019f, 0.006f);             // 입술 두께
            _identity[6][2 * VertexCount + v] = 0.003f * lip * front;
            _identity[7][VertexCount + v] = -0.006f * Math.Clamp((-0.06f - y) / 0.04f, 0f, 1f) * front;       // 턱끝 길이
        }
    }

    // ───────────────────────── 제어 ─────────────────────────

    public void SetLayer(FaceLayer layer, ArkitBlendshape k, float w) => _layers[(int)layer * ShapeCount + (int)k] = Math.Clamp(w, 0f, 1f);
    public float Layer(FaceLayer layer, ArkitBlendshape k) => _layers[(int)layer * ShapeCount + (int)k];
    public void ClearLayer(FaceLayer layer) => _layers.AsSpan((int)layer * ShapeCount, ShapeCount).Clear();

    public void SetIdentity(int k, float beta)
    {
        if ((uint)k >= IdentityCount) return;
        _beta[k] = Math.Clamp(beta, -2.5f, 2.5f);
        _identityVersion++;
    }

    public void ResetIdentity() { Array.Clear(_beta); _identityVersion++; }

    /// <summary>즉시 리셋(모든 레이어 · 근육 상태 0).</summary>
    public void Reset()
    {
        Array.Clear(_layers); Array.Clear(_target); Array.Clear(_act); Array.Clear(_x); Array.Clear(_v); Array.Clear(_w); Array.Clear(_corrW);
        Preset = FacePreset.Neutral; CurrentViseme = Viseme.Rest;
        Synthesize();
    }

    /// <summary>FACS 기반 감정 프리셋 → 표정 레이어(강도 0..1).</summary>
    public void SetExpression(FacePreset p, float intensity = 1f)
    {
        ClearLayer(FaceLayer.Expression);
        AddExpression(p, intensity);
        Preset = p;
    }

    private void Add(ArkitBlendshape k, float w)
    {
        var i = (int)FaceLayer.Expression * ShapeCount + (int)k;
        _layers[i] = Math.Clamp(_layers[i] + w, 0f, 1f);
    }

    private void Both(ArkitBlendshape l, ArkitBlendshape r, float w) { Add(l, w); Add(r, w); }

    private void AddExpression(FacePreset p, float s)
    {
        if (s <= 0f) return;
        switch (p)
        {
            case FacePreset.Joy: // AU6 + AU12 (+AU25)
                Both(ArkitBlendshape.CheekSquintLeft, ArkitBlendshape.CheekSquintRight, 0.6f * s);
                Both(ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight, 0.85f * s);
                Both(ArkitBlendshape.EyeSquintLeft, ArkitBlendshape.EyeSquintRight, 0.35f * s);
                Both(ArkitBlendshape.MouthDimpleLeft, ArkitBlendshape.MouthDimpleRight, 0.2f * s);
                Add(ArkitBlendshape.JawOpen, 0.1f * s);
                break;
            case FacePreset.Sadness: // AU1 + AU4 + AU15
                Add(ArkitBlendshape.BrowInnerUp, 0.8f * s);
                Both(ArkitBlendshape.BrowDownLeft, ArkitBlendshape.BrowDownRight, 0.2f * s);
                Both(ArkitBlendshape.MouthFrownLeft, ArkitBlendshape.MouthFrownRight, 0.7f * s);
                Add(ArkitBlendshape.MouthShrugLower, 0.3f * s);
                Both(ArkitBlendshape.EyeLookDownLeft, ArkitBlendshape.EyeLookDownRight, 0.15f * s);
                break;
            case FacePreset.Anger: // AU4 + AU5 + AU7 + AU23
                Both(ArkitBlendshape.BrowDownLeft, ArkitBlendshape.BrowDownRight, 0.9f * s);
                Both(ArkitBlendshape.EyeSquintLeft, ArkitBlendshape.EyeSquintRight, 0.4f * s);
                Both(ArkitBlendshape.MouthPressLeft, ArkitBlendshape.MouthPressRight, 0.5f * s);
                Both(ArkitBlendshape.NoseSneerLeft, ArkitBlendshape.NoseSneerRight, 0.4f * s);
                Add(ArkitBlendshape.JawForward, 0.1f * s);
                break;
            case FacePreset.Surprise: // AU1 + AU2 + AU5 + AU26
                Add(ArkitBlendshape.BrowInnerUp, 0.9f * s);
                Both(ArkitBlendshape.BrowOuterUpLeft, ArkitBlendshape.BrowOuterUpRight, 0.85f * s);
                Both(ArkitBlendshape.EyeWideLeft, ArkitBlendshape.EyeWideRight, 0.8f * s);
                Add(ArkitBlendshape.JawOpen, 0.55f * s);
                break;
            case FacePreset.Fear: // AU1 + AU2 + AU4 + AU5 + AU20
                Add(ArkitBlendshape.BrowInnerUp, 0.8f * s);
                Both(ArkitBlendshape.BrowOuterUpLeft, ArkitBlendshape.BrowOuterUpRight, 0.4f * s);
                Both(ArkitBlendshape.EyeWideLeft, ArkitBlendshape.EyeWideRight, 0.9f * s);
                Both(ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight, 0.6f * s);
                Add(ArkitBlendshape.JawOpen, 0.2f * s);
                break;
            case FacePreset.Disgust: // AU9 + AU10
                Both(ArkitBlendshape.NoseSneerLeft, ArkitBlendshape.NoseSneerRight, 0.8f * s);
                Both(ArkitBlendshape.MouthUpperUpLeft, ArkitBlendshape.MouthUpperUpRight, 0.6f * s);
                Both(ArkitBlendshape.BrowDownLeft, ArkitBlendshape.BrowDownRight, 0.4f * s);
                Both(ArkitBlendshape.CheekSquintLeft, ArkitBlendshape.CheekSquintRight, 0.3f * s);
                break;
        }
    }

    /// <summary>연속 감정(정서가 · 각성 · 공포 0..1) → 프리셋 혼합(대사 감정선 · BCI 연동).</summary>
    public void BlendEmotion(float valence, float arousal, float fear)
    {
        ClearLayer(FaceLayer.Expression);
        var pos = Math.Clamp((valence - 0.5f) * 2f, 0f, 1f); var neg = Math.Clamp((0.5f - valence) * 2f, 0f, 1f);
        var ar = Math.Clamp(arousal, 0f, 1f); var fe = Math.Clamp(fear, 0f, 1f);
        AddExpression(FacePreset.Joy, pos * (0.6f + 0.4f * ar));
        AddExpression(FacePreset.Sadness, neg * (1f - ar) * (1f - fe));
        AddExpression(FacePreset.Anger, neg * ar * (1f - fe));
        AddExpression(FacePreset.Fear, fe);
        AddExpression(FacePreset.Surprise, Math.Clamp(ar - 0.75f, 0f, 1f) * 2f * (1f - neg));
        Preset = fe > 0.5f ? FacePreset.Fear : pos > 0.3f ? FacePreset.Joy : neg > 0.3f ? (ar > 0.55f ? FacePreset.Anger : FacePreset.Sadness) : FacePreset.Neutral;
    }

    /// <summary>비짐 → 발화 레이어(진폭 0..1 · 동시조음은 근육 동역학이 평활).</summary>
    public void SetSpeech(Viseme v, float amp = 1f)
    {
        ClearLayer(FaceLayer.Speech);
        CurrentViseme = v;
        var o = (int)FaceLayer.Speech * ShapeCount;
        void S(ArkitBlendshape k, float w) => _layers[o + (int)k] = Math.Clamp(w * amp, 0f, 1f);
        switch (v)
        {
            case Viseme.A: S(ArkitBlendshape.JawOpen, 0.55f); S(ArkitBlendshape.MouthLowerDownLeft, 0.3f); S(ArkitBlendshape.MouthLowerDownRight, 0.3f); S(ArkitBlendshape.MouthStretchLeft, 0.1f); S(ArkitBlendshape.MouthStretchRight, 0.1f); break;
            case Viseme.E: S(ArkitBlendshape.JawOpen, 0.3f); S(ArkitBlendshape.MouthStretchLeft, 0.45f); S(ArkitBlendshape.MouthStretchRight, 0.45f); S(ArkitBlendshape.MouthSmileLeft, 0.15f); S(ArkitBlendshape.MouthSmileRight, 0.15f); break;
            case Viseme.I: S(ArkitBlendshape.JawOpen, 0.15f); S(ArkitBlendshape.MouthStretchLeft, 0.55f); S(ArkitBlendshape.MouthStretchRight, 0.55f); S(ArkitBlendshape.MouthSmileLeft, 0.25f); S(ArkitBlendshape.MouthSmileRight, 0.25f); break;
            case Viseme.O: S(ArkitBlendshape.JawOpen, 0.4f); S(ArkitBlendshape.MouthFunnel, 0.65f); S(ArkitBlendshape.MouthPucker, 0.25f); break;
            case Viseme.U: S(ArkitBlendshape.JawOpen, 0.12f); S(ArkitBlendshape.MouthPucker, 0.8f); S(ArkitBlendshape.MouthFunnel, 0.35f); break;
            case Viseme.M: S(ArkitBlendshape.MouthClose, 0.7f); S(ArkitBlendshape.MouthPressLeft, 0.45f); S(ArkitBlendshape.MouthPressRight, 0.45f); S(ArkitBlendshape.MouthRollLower, 0.2f); break;
            case Viseme.F: S(ArkitBlendshape.MouthRollLower, 0.55f); S(ArkitBlendshape.MouthUpperUpLeft, 0.2f); S(ArkitBlendshape.MouthUpperUpRight, 0.2f); S(ArkitBlendshape.JawOpen, 0.08f); break;
        }
    }

    /// <summary>문자 → 비짐(한글 음절 중성/종성 분해 · 라틴 모음/순음).</summary>
    public static Viseme VisemeOf(char c)
    {
        if (c >= '가' && c <= '힣')
        {
            var s = c - 0xAC00; var jung = s / 28 % 21; var jong = s % 28;
            if (jong is 16 or 17 or 26) return Viseme.M; // ㅁ ㅂ ㅍ 받침 = 양순 폐쇄
            return jung switch
            {
                0 or 2 or 9 => Viseme.A,
                1 or 3 or 4 or 5 or 6 or 7 or 10 or 11 or 15 => Viseme.E,
                8 or 12 => Viseme.O,
                13 or 14 or 16 or 17 => Viseme.U,
                _ => Viseme.I,
            };
        }
        return char.ToLowerInvariant(c) switch
        {
            'a' => Viseme.A,
            'e' => Viseme.E,
            'i' or 'y' => Viseme.I,
            'o' => Viseme.O,
            'u' or 'w' => Viseme.U,
            'm' or 'b' or 'p' => Viseme.M,
            'f' or 'v' => Viseme.F,
            _ when char.IsLetter(c) => Viseme.E,
            _ => Viseme.Rest,
        };
    }

    // ───────────────────────── 시뮬레이션 ─────────────────────────

    /// <summary>1프레임(무할당): 근육 동역학 → 기하 합성.</summary>
    public void Step(float dt)
    {
        var t0 = Stopwatch.GetTimestamp();
        if (dt > 0f) StepMuscles(dt);
        Synthesize();
        var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        LastStepMs = ms; if (ms > MaxStepMs) MaxStepMs = ms;
        Frames++;
    }

    private void StepMuscles(float dt)
    {
        // ① 레이어 합산
        for (var k = 0; k < ShapeCount; k++)
        {
            var s = 0f;
            for (var l = 0; l < LayerCount; l++) s += _layers[l * ShapeCount + k];
            _target[k] = Math.Clamp(s, 0f, 1f);
        }
        // ② 길항근 억제(현재 활성 기준 · 대칭)
        foreach (var (a, b) in Antagonists)
        {
            int ia = (int)a, ib = (int)b;
            var ta = _target[ia] * (1f - Inhibition * _act[ib]); var tb = _target[ib] * (1f - Inhibition * _act[ia]);
            _target[ia] = ta; _target[ib] = tb;
        }
        // ③ 활성(비대칭 1차 · 정확 이산화) ④ 연조직 2차(반암시 오일러 · 2 서브스텝)
        float ka = 1f - MathF.Exp(-dt / TauActivate), kr = 1f - MathF.Exp(-dt / TauRelease);
        var om = 2f * MathF.PI * TissueHz; var om2 = om * om; var c = 2f * TissueDamping * om; var h = dt * 0.5f;
        var active = 0;
        for (var k = 0; k < ShapeCount; k++)
        {
            var u = _target[k]; var a = _act[k];
            a += (u - a) * (u > a ? ka : kr);
            _act[k] = a;
            float x = _x[k], v = _v[k];
            for (var s = 0; s < 2; s++) { v += (om2 * (a - x) - c * v) * h; x += v * h; }
            _x[k] = x; _v[k] = v;
            var w = Math.Clamp(x, 0f, 1f);
            _w[k] = w;
            if (w > 1e-3f) active++;
        }
        ActiveShapes = active;
        float smile = 0.5f * (_w[(int)ArkitBlendshape.MouthSmileLeft] + _w[(int)ArkitBlendshape.MouthSmileRight]);
        _corrW[0] = _w[(int)ArkitBlendshape.JawOpen] * smile;
        _corrW[1] = 0.5f * (_w[(int)ArkitBlendshape.EyeBlinkLeft] * _w[(int)ArkitBlendshape.EyeSquintLeft] + _w[(int)ArkitBlendshape.EyeBlinkRight] * _w[(int)ArkitBlendshape.EyeSquintRight]);
        _corrW[2] = _w[(int)ArkitBlendshape.BrowInnerUp] * 0.5f * (_w[(int)ArkitBlendshape.BrowDownLeft] + _w[(int)ArkitBlendshape.BrowDownRight]);
    }

    /// <summary>기하 합성(SIMD 누적 · 턱 LBS · 법선).</summary>
    public void Synthesize()
    {
        var pos = Pos.AsSpan();
        Neutral.AsSpan().CopyTo(pos);
        for (var k = 0; k < IdentityCount; k++) if (_beta[k] != 0f) AddScaled(pos, _identity[k], _beta[k]);
        for (var k = 0; k < ShapeCount; k++) if (!_empty[k] && _w[k] > 1e-4f) AddScaled(pos, _delta[k], _w[k]);
        for (var k = 0; k < CorrectiveCount; k++) if (_corrW[k] > 1e-4f) AddScaled(pos, _corrective[k], _corrW[k]);
        SkinJaw();
        ComputeNormals();
        var md = 0f;
        for (var v = 0; v < VertexCount; v++)
        {
            float dx = Pos[v] - Neutral[v], dy = Pos[VertexCount + v] - Neutral[VertexCount + v], dz = Pos[2 * VertexCount + v] - Neutral[2 * VertexCount + v];
            var d = dx * dx + dy * dy + dz * dz;
            if (d > md) md = d;
        }
        MaxDisplacement = MathF.Sqrt(md);
        Version++;
    }

    /// <summary>FLAME 턱 관절: 피치 = jawOpen · 요 = jawLeft − jawRight · 전방 이동 = jawForward → LBS 블렌드.</summary>
    private void SkinJaw()
    {
        var pitch = JawMaxRad * _w[(int)ArkitBlendshape.JawOpen];
        var yaw = 0.09f * (_w[(int)ArkitBlendshape.JawLeft] - _w[(int)ArkitBlendshape.JawRight]);
        var fwd = 0.008f * _w[(int)ArkitBlendshape.JawForward];
        JawAngle = pitch;
        if (pitch == 0f && yaw == 0f && fwd == 0f) return;
        var m = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch);
        for (var v = 0; v < VertexCount; v++)
        {
            var w = JawWeight[v];
            if (w <= 0f) continue;
            var p = Position(v);
            var q = Vector3.Transform(p - JawPivot, m) + JawPivot + new Vector3(0f, 0f, fwd);
            var r = p + (q - p) * w;
            Pos[v] = r.X; Pos[VertexCount + v] = r.Y; Pos[2 * VertexCount + v] = r.Z;
        }
    }

    private void ComputeNormals()
    {
        for (var iy = 0; iy < GridH; iy++)
        {
            int y0 = Math.Max(iy - 1, 0), y1 = Math.Min(iy + 1, GridH - 1);
            for (var ix = 0; ix < GridW; ix++)
            {
                int x0 = Math.Max(ix - 1, 0), x1 = Math.Min(ix + 1, GridW - 1);
                var du = Position(Index(x1, iy)) - Position(Index(x0, iy));
                var dv = Position(Index(ix, y1)) - Position(Index(ix, y0));
                var n = Vector3.Cross(du, dv);
                var l = n.Length();
                n = l > 1e-12f ? n / l : Vector3.UnitZ;
                var v = Index(ix, iy);
                NX[v] = n.X; NY[v] = n.Y; NZ[v] = n.Z;
            }
        }
    }

    /// <summary>복원력 상태 즉시 반영(테스트/프리뷰): 목표를 출력 웨이트에 직접 설정.</summary>
    public void SetWeightsImmediate(ReadOnlySpan<float> w)
    {
        var n = Math.Min(w.Length, ShapeCount);
        for (var k = 0; k < n; k++) { var c = Math.Clamp(w[k], 0f, 1f); _w[k] = c; _x[k] = c; _act[k] = c; _v[k] = 0f; }
        Synthesize();
    }

    // ───────────────────────── SIMD 커널 ─────────────────────────

    /// <summary>dst += src·s (Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치).</summary>
    public static void AddScaled(Span<float> dst, ReadOnlySpan<float> src, float s)
    {
        var n = Math.Min(dst.Length, src.Length); var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var vs = new Vector<float>(s);
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..n]); var r = MemoryMarshal.Cast<float, Vector<float>>(src[..n]);
            for (var k = 0; k < d.Length; k++) d[k] += r[k] * vs;
            i = d.Length * Vector<float>.Count;
        }
        for (; i < n; i++) dst[i] += src[i] * s;
    }

    public static void AddScaledScalar(Span<float> dst, ReadOnlySpan<float> src, float s)
    {
        var n = Math.Min(dst.Length, src.Length);
        for (var i = 0; i < n; i++) dst[i] += src[i] * s;
    }

    /// <summary>셰이프 델타 크기(최대 변위 m · 진단).</summary>
    public float ShapeMagnitude(ArkitBlendshape k)
    {
        var d = _delta[(int)k]; var m = 0f;
        for (var v = 0; v < VertexCount; v++)
        {
            var l = d[v] * d[v] + d[VertexCount + v] * d[VertexCount + v] + d[2 * VertexCount + v] * d[2 * VertexCount + v];
            if (l > m) m = l;
        }
        return MathF.Sqrt(m);
    }

    public Vector3 ShapeDelta(ArkitBlendshape k, int v)
    {
        var d = _delta[(int)k];
        return new Vector3(d[v], d[VertexCount + v], d[2 * VertexCount + v]);
    }
}
