using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using PromptFlowStudio.Services.DigitalHuman;

namespace PromptFlowStudio.Services.FacialPerformance;

public enum FaceSimdPath : byte { Scalar = 0, V128, V256 }

public enum LandmarkGroup : byte { Grid, Oval, Brow, EyeUpper, EyeLower, EyeCorner, Iris, Nose, LipOuterUpper, LipOuterLower, LipInnerUpper, LipInnerLower, LipCorner }

/// <summary>
/// [PromptFlow Studio v17.12.0] 🗣️ 실시간 468 랜드마크 → 52 ARKit 블렌드셰이프 역산 엔진(<see cref="Solve"/> 무할당).
///   ▸ 랜드마크 모델(절차 · 468점): 얼굴 격자 18×18 + 윤곽 30 + 눈썹 10×2 + 눈꺼풀 16×2 + 홍채 5×2 + 코 12 + 입술 외곽/내곽 20/20. 미터 단위 · +X = 피사체 왼쪽 · +Z 정면.
///     선형 셰이프 모델 L(w) = L₀ + Σₖ wₖ·Dₖ (ARKit 52 · 해부 영역 로브 · 강체 앵커 = 이마/윤곽 상단/콧등 변위 0 · 홍채 = 시선 셰이프 전용).
///   ▸ ① 헤드 포즈: 강체 앵커 Horn 사원수 절대 정렬(스케일 포함 · 4×4 Jacobi 고유분해) → 관측을 정규 좌표로 역변환.
///   ▸ ② 투영 b = Dᵀ(L̂ − L₀) [SIMD · 셰이프 = 레인 · V256 → V128 → 스칼라 비트 동일] · 그램 G = DᵀD(+ Tikhonov λ) 사전 계산.
///   ▸ ③ 상자 제약 [0,1] 최소제곱: 직전 프레임 웜스타트 → 투영 좌표 하강 4회 → 활성 집합 투영 뉴턴(콜레스키 · ≤ 8회 · 각 1 하강) 수렴 1e-6.
///   ▸ ④ 재구성 L(w) [SIMD · 좌표 = 레인 · 비트 동일] → 잔차 RMS(mm).
/// 학습된 신경망 대신 해석적 역산(외부 모델 결선점: <see cref="SetObservation"/> 에 검출기 출력을 주입).
/// </summary>
public sealed class NeuralFacialLandmarkBlendshapeEngine
{
    public const int Landmarks = 468, K = NeuralFacialMuscleSimulator.ShapeCount, Kp = 56, C = Landmarks * 3, Cp = 1408;
    public const int GridN = 18, OvalBase = GridN * GridN, BrowBase = OvalBase + 30, EyeBase = BrowBase + 20, IrisBase = EyeBase + 32, NoseBase = IrisBase + 10, LipOuterBase = NoseBase + 12, LipInnerBase = LipOuterBase + 20;
    public const float LipCy = -0.045f, EyeCy = 0.026f, EyeCx = 0.032f, IrisShift = 0.005f, MaxGazeDeg = 25f;
    /// <summary>홍채 이동 0.005 m = 25° 회전이 되는 안구 반경.</summary>
    public static readonly float EyeballRadius = IrisShift / MathF.Sin(MaxGazeDeg * MathF.PI / 180f);

    private static readonly Vector3[] NeutralPts = BuildNeutral(out GroupOf, out SideOf, out RigidOf);
    public static readonly LandmarkGroup[] GroupOf;
    public static readonly sbyte[] SideOf;
    public static readonly bool[] RigidOf;
    public static readonly int[] Edges = BuildEdges();

    private readonly float[] _l0 = new float[Cp];
    private readonly float[] _dT = new float[C * Kp];     // [coord][shape] — 투영(셰이프 레인)
    private readonly float[] _dK = new float[K * Cp];     // [shape][coord] — 재구성(좌표 레인)
    private readonly float[] _g = new float[K * K], _diag = new float[K];
    private readonly float[] _obs = new float[C], _can = new float[C], _r = new float[C], _rec = new float[Cp];
    private readonly float[] _b = new float[Kp], _w = new float[K], _wExt = new float[Kp];
    private readonly int[] _anchors;
    private Quaternion _q = Quaternion.Identity;
    private Vector3 _t;
    private float _s = 1f;

    public NeuralFacialLandmarkBlendshapeEngine()
    {
        for (var i = 0; i < Landmarks; i++) { _l0[i * 3] = NeutralPts[i].X; _l0[i * 3 + 1] = NeutralPts[i].Y; _l0[i * 3 + 2] = NeutralPts[i].Z; }
        for (var k = 0; k < K; k++)
            for (var i = 0; i < Landmarks; i++)
            {
                var d = ShapeDisplacement(k, i);
                for (var a = 0; a < 3; a++)
                {
                    var v = a == 0 ? d.X : a == 1 ? d.Y : d.Z;
                    _dT[(i * 3 + a) * Kp + k] = v;
                    _dK[k * Cp + i * 3 + a] = v;
                }
            }
        double tr = 0;
        for (var a = 0; a < K; a++)
            for (var b = a; b < K; b++)
            {
                double s = 0;
                for (var c = 0; c < C; c++) s += (double)_dK[a * Cp + c] * _dK[b * Cp + c];
                _g[a * K + b] = _g[b * K + a] = (float)s;
                if (a == b) tr += s;
            }
        Lambda = (float)(1e-5 * tr / K);
        for (var k = 0; k < K; k++) _diag[k] = _g[k * K + k] + Lambda;
        var n = 0;
        for (var i = 0; i < Landmarks; i++) if (RigidOf[i]) n++;
        _anchors = new int[n];
        n = 0;
        for (var i = 0; i < Landmarks; i++) if (RigidOf[i]) _anchors[n++] = i;
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? FaceSimdPath.V256 : Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian ? FaceSimdPath.V128 : FaceSimdPath.Scalar;
        for (var i = 0; i < Landmarks; i++) SetObservation(i, NeutralPts[i]);
    }

    public FaceSimdPath Path { get; set; }
    public float Lambda { get; }
    public int MaxNewton { get; set; } = 8;
    public int AnchorCount => _anchors.Length;
    public ReadOnlySpan<float> Weights => _w;
    public float Weight(int k) => _w[k];
    public float Weight(ArkitBlendshape k) => _w[(int)k];
    public Quaternion HeadRotation => _q;
    public Vector3 HeadTranslation => _t;
    public float HeadScale => _s;
    public float HeadYawDeg { get; private set; }
    public float HeadPitchDeg { get; private set; }
    public float HeadRollDeg { get; private set; }
    public float ResidualMm { get; private set; }
    public int LastSweeps { get; private set; }
    public int LastNewton { get; private set; }
    private float LastMaxDelta { get; set; }
    public double LastSolveUs { get; private set; }
    public double LastPoseUs { get; private set; }
    public double LastProjectUs { get; private set; }
    public long Solves { get; private set; }
    public static Vector3 Neutral(int i) => NeutralPts[i];
    public Vector3 Canonical(int i) => new(_can[i * 3], _can[i * 3 + 1], _can[i * 3 + 2]);
    public Vector3 Observed(int i) => new(_obs[i * 3], _obs[i * 3 + 1], _obs[i * 3 + 2]);
    public Vector3 Reconstructed(int i) => new(_rec[i * 3], _rec[i * 3 + 1], _rec[i * 3 + 2]);
    public float Gram(int a, int b) => _g[a * K + b];

    public void SetObservation(int i, Vector3 p) { _obs[i * 3] = p.X; _obs[i * 3 + 1] = p.Y; _obs[i * 3 + 2] = p.Z; }

    // ══════════════════════ 랜드마크 모델(정적) ══════════════════════

    private static float Surface(float x, float y) => 0.095f * MathF.Sqrt(MathF.Max(0.02f, 1f - (x / 0.085f) * (x / 0.085f) - (y / 0.13f) * (y / 0.13f)));

    private static Vector3[] BuildNeutral(out LandmarkGroup[] group, out sbyte[] side, out bool[] rigid)
    {
        var p = new Vector3[Landmarks];
        group = new LandmarkGroup[Landmarks]; side = new sbyte[Landmarks]; rigid = new bool[Landmarks];
        for (var r = 0; r < GridN; r++)
            for (var c = 0; c < GridN; c++)
            {
                float x = -0.075f + 0.15f * c / (GridN - 1), y = -0.1f + 0.2f * r / (GridN - 1);
                p[r * GridN + c] = new Vector3(x, y, Surface(x, y));
            }
        for (var i = 0; i < 30; i++)
        {
            var th = MathF.Tau * i / 30;
            p[OvalBase + i] = new Vector3(0.08f * MathF.Cos(th), 0.115f * MathF.Sin(th) - 0.005f, 0.025f);
            group[OvalBase + i] = LandmarkGroup.Oval;
        }
        for (var s = 0; s < 2; s++)
        {
            var sg = s == 0 ? 1f : -1f;
            for (var i = 0; i < 10; i++)
            {
                float x = sg * (0.012f + 0.045f * i / 9f), y = 0.048f + 0.008f * MathF.Sin(MathF.PI * i / 9f);
                p[BrowBase + s * 10 + i] = new Vector3(x, y, Surface(x, y) + 0.004f);
                group[BrowBase + s * 10 + i] = LandmarkGroup.Brow;
            }
            for (var i = 0; i < 16; i++)
            {
                var th = MathF.Tau * i / 16;
                float x = sg * EyeCx + 0.016f * MathF.Cos(th), y = EyeCy + 0.0065f * MathF.Sin(th);
                p[EyeBase + s * 16 + i] = new Vector3(x, y, Surface(x, y) + 0.002f);
                var sn = MathF.Sin(th);
                group[EyeBase + s * 16 + i] = sn > 0.01f ? LandmarkGroup.EyeUpper : sn < -0.01f ? LandmarkGroup.EyeLower : LandmarkGroup.EyeCorner;
            }
            var ic = new Vector3(sg * EyeCx, EyeCy, Surface(sg * EyeCx, EyeCy) + 0.006f);
            p[IrisBase + s * 5] = ic;
            for (var i = 0; i < 4; i++)
            {
                var th = MathF.Tau * i / 4;
                p[IrisBase + s * 5 + 1 + i] = ic + new Vector3(0.0055f * MathF.Cos(th), 0.0055f * MathF.Sin(th), -0.0005f);
            }
            for (var i = 0; i < 5; i++) group[IrisBase + s * 5 + i] = LandmarkGroup.Iris;
        }
        ReadOnlySpan<float> nose = stackalloc float[]
        {
            0, 0.035f, 0.003f, 0, 0.02f, 0.006f, 0, 0.005f, 0.009f, 0, -0.012f, 0.02f, 0, -0.02f, 0.012f,
            0.012f, -0.015f, 0.008f, -0.012f, -0.015f, 0.008f, 0.017f, -0.012f, 0.004f, -0.017f, -0.012f, 0.004f,
            0.006f, -0.02f, 0.01f, -0.006f, -0.02f, 0.01f, 0, -0.003f, 0.012f,
        };
        for (var i = 0; i < 12; i++)
        {
            float x = nose[i * 3], y = nose[i * 3 + 1];
            p[NoseBase + i] = new Vector3(x, y, Surface(x, y) + nose[i * 3 + 2]);
            group[NoseBase + i] = LandmarkGroup.Nose;
        }
        for (var l = 0; l < 2; l++)
            for (var i = 0; i < 20; i++)
            {
                var th = MathF.Tau * i / 20;
                float a = l == 0 ? 0.026f : 0.02f, b = l == 0 ? 0.012f : 0.004f;
                float x = a * MathF.Cos(th), y = LipCy + b * MathF.Sin(th);
                var idx = (l == 0 ? LipOuterBase : LipInnerBase) + i;
                p[idx] = new Vector3(x, y, Surface(x, y) + (l == 0 ? 0.006f : 0.004f));
                var sn = MathF.Sin(th);
                group[idx] = sn > 0.01f ? (l == 0 ? LandmarkGroup.LipOuterUpper : LandmarkGroup.LipInnerUpper)
                           : sn < -0.01f ? (l == 0 ? LandmarkGroup.LipOuterLower : LandmarkGroup.LipInnerLower) : LandmarkGroup.LipCorner;
            }
        for (var i = 0; i < Landmarks; i++)
        {
            side[i] = (sbyte)(p[i].X > 0.003f ? 1 : p[i].X < -0.003f ? -1 : 0);
            rigid[i] = (group[i] == LandmarkGroup.Grid && p[i].Y > 0.072f) || (group[i] == LandmarkGroup.Oval && p[i].Y > 0.05f) || (i >= NoseBase && i < NoseBase + 2);
        }
        return p;
    }

    private static int[] BuildEdges()
    {
        var e = new List<int>(2048);
        for (var r = 0; r < GridN; r++)
            for (var c = 0; c < GridN; c++)
            {
                var i = r * GridN + c;
                if (c + 1 < GridN) { e.Add(i); e.Add(i + 1); }
                if (r + 1 < GridN) { e.Add(i); e.Add(i + GridN); }
            }
        void Ring(int b, int n) { for (var i = 0; i < n; i++) { e.Add(b + i); e.Add(b + (i + 1) % n); } }
        void Chain(int b, int n) { for (var i = 0; i + 1 < n; i++) { e.Add(b + i); e.Add(b + i + 1); } }
        Ring(OvalBase, 30); Chain(BrowBase, 10); Chain(BrowBase + 10, 10); Ring(EyeBase, 16); Ring(EyeBase + 16, 16);
        Ring(IrisBase + 1, 4); Ring(IrisBase + 6, 4); Chain(NoseBase, 4); Ring(LipOuterBase, 20); Ring(LipInnerBase, 20);
        return e.ToArray();
    }

    /// <summary>강조 표시용 특징 간선 시작 인덱스(<see cref="Edges"/> 쌍 단위).</summary>
    public static int FeatureEdgeStart => 2 * GridN * (GridN - 1);

    private static float Lobe(Vector3 p, float cx, float cy, float sigma)
    {
        float dx = p.X - cx, dy = p.Y - cy;
        return MathF.Exp(-(dx * dx + dy * dy) / (2f * sigma * sigma));
    }

    private static float Smoothstep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>셰이프 k 의 랜드마크 i 변위(m · 해부 영역 로브).</summary>
    public static Vector3 ShapeDisplacement(int k, int i)
    {
        var g = GroupOf[i];
        if (RigidOf[i]) return Vector3.Zero;
        var p = NeutralPts[i];
        var bs = (ArkitBlendshape)k;
        var s = bs.ToString().EndsWith("Left", StringComparison.Ordinal) ? 1f : -1f;
        var sideMatch = SideOf[i] == (sbyte)s;
        if (g == LandmarkGroup.Iris)
        {
            if (!sideMatch) return Vector3.Zero;
            return bs switch
            {
                ArkitBlendshape.EyeLookDownLeft or ArkitBlendshape.EyeLookDownRight => new Vector3(0, -IrisShift, 0),
                ArkitBlendshape.EyeLookUpLeft or ArkitBlendshape.EyeLookUpRight => new Vector3(0, IrisShift, 0),
                ArkitBlendshape.EyeLookInLeft or ArkitBlendshape.EyeLookInRight => new Vector3(-s * IrisShift, 0, 0),
                ArkitBlendshape.EyeLookOutLeft or ArkitBlendshape.EyeLookOutRight => new Vector3(s * IrisShift, 0, 0),
                _ => Vector3.Zero,
            };
        }
        bool lip = g is LandmarkGroup.LipOuterUpper or LandmarkGroup.LipOuterLower or LandmarkGroup.LipInnerUpper or LandmarkGroup.LipInnerLower or LandmarkGroup.LipCorner;
        bool lower = g is LandmarkGroup.LipOuterLower or LandmarkGroup.LipInnerLower, upper = g is LandmarkGroup.LipOuterUpper or LandmarkGroup.LipInnerUpper;
        var sw = 0.5f + 0.5f * MathF.Tanh(s * p.X / 0.01f);
        var lowerFace = lower ? 1f : upper || g is LandmarkGroup.Nose or LandmarkGroup.Brow || g is LandmarkGroup.EyeUpper or LandmarkGroup.EyeLower or LandmarkGroup.EyeCorner ? 0f
            : g == LandmarkGroup.LipCorner ? 0.5f : Smoothstep(-0.04f, -0.065f, p.Y);
        var lidSin = (p.Y - EyeCy) / 0.0065f;
        switch (bs)
        {
            case ArkitBlendshape.BrowDownLeft: case ArkitBlendshape.BrowDownRight: return new Vector3(-s * 0.2f, -1f, 0) * (0.008f * Lobe(p, s * 0.035f, 0.05f, 0.016f));
            case ArkitBlendshape.BrowInnerUp: return new Vector3(0, 0.009f * (Lobe(p, 0.015f, 0.048f, 0.011f) + Lobe(p, -0.015f, 0.048f, 0.011f)), 0);
            case ArkitBlendshape.BrowOuterUpLeft: case ArkitBlendshape.BrowOuterUpRight: return new Vector3(0, 0.008f * Lobe(p, s * 0.05f, 0.05f, 0.011f), 0);
            case ArkitBlendshape.CheekPuff: return new Vector3(0.5f, 0, 1f) * (0.006f * Lobe(p, 0.05f, -0.03f, 0.018f)) + new Vector3(-0.5f, 0, 1f) * (0.006f * Lobe(p, -0.05f, -0.03f, 0.018f));
            case ArkitBlendshape.CheekSquintLeft: case ArkitBlendshape.CheekSquintRight: return new Vector3(0, 1f, 0.3f) * (0.006f * Lobe(p, s * 0.045f, -0.005f, 0.013f));
            case ArkitBlendshape.EyeBlinkLeft: case ArkitBlendshape.EyeBlinkRight: return g == LandmarkGroup.EyeUpper && sideMatch ? new Vector3(0, -1.9f * (p.Y - EyeCy), 0) : Vector3.Zero;
            case ArkitBlendshape.EyeWideLeft: case ArkitBlendshape.EyeWideRight: return g == LandmarkGroup.EyeUpper && sideMatch ? new Vector3(0, 0.004f * lidSin, 0) : Vector3.Zero;
            case ArkitBlendshape.EyeSquintLeft: case ArkitBlendshape.EyeSquintRight:
                if (g == LandmarkGroup.EyeLower && sideMatch) return new Vector3(0, -0.0035f * lidSin, 0);
                return g == LandmarkGroup.Grid ? new Vector3(0, 0.002f * Lobe(p, s * EyeCx, 0.01f, 0.01f), 0) : Vector3.Zero;
            case ArkitBlendshape.JawForward: return new Vector3(0, 0, 0.008f * lowerFace);
            case ArkitBlendshape.JawLeft: case ArkitBlendshape.JawRight: return new Vector3(s * 0.008f * lowerFace, 0, 0);
            case ArkitBlendshape.JawOpen: return new Vector3(0, -0.02f, -0.004f) * lowerFace;
            case ArkitBlendshape.MouthClose: return lower ? new Vector3(0, 0.006f, 0) : upper ? new Vector3(0, -0.002f, 0) : Vector3.Zero;
            case ArkitBlendshape.MouthDimpleLeft: case ArkitBlendshape.MouthDimpleRight: return new Vector3(s * 0.3f, 0, -1f) * (0.004f * Lobe(p, s * 0.03f, LipCy, 0.008f));
            case ArkitBlendshape.MouthFrownLeft: case ArkitBlendshape.MouthFrownRight: return new Vector3(0, -0.006f * Lobe(p, s * 0.027f, LipCy - 0.002f, 0.01f), 0);
            case ArkitBlendshape.MouthFunnel: return lip ? new Vector3(-p.X * 0.15f, MathF.Sign(p.Y - LipCy) * 0.004f, 0.006f) : Vector3.Zero;
            case ArkitBlendshape.MouthLeft: case ArkitBlendshape.MouthRight: return new Vector3(s * 0.007f * (lip ? 1f : g == LandmarkGroup.Grid ? Lobe(p, 0, LipCy, 0.02f) : 0f), 0, 0);
            case ArkitBlendshape.MouthLowerDownLeft: case ArkitBlendshape.MouthLowerDownRight: return lower ? new Vector3(0, -0.006f * sw, 0) : Vector3.Zero;
            case ArkitBlendshape.MouthPressLeft: case ArkitBlendshape.MouthPressRight: return lower ? new Vector3(0, 0.001f, -0.003f) * sw : upper ? new Vector3(0, -0.001f, -0.003f) * sw : Vector3.Zero;
            case ArkitBlendshape.MouthPucker: return lip ? new Vector3(-p.X * 0.35f, 0, 0.007f) : Vector3.Zero;
            case ArkitBlendshape.MouthRollLower: return lower ? new Vector3(0, 0.0015f, -0.004f) : Vector3.Zero;
            case ArkitBlendshape.MouthRollUpper: return upper ? new Vector3(0, -0.0015f, -0.004f) : Vector3.Zero;
            case ArkitBlendshape.MouthShrugLower: return new Vector3(0, 0.004f, 0.0012f) * (lower ? 1f : g == LandmarkGroup.Grid || g == LandmarkGroup.Oval ? Lobe(p, 0, -0.07f, 0.014f) : 0f);
            case ArkitBlendshape.MouthShrugUpper: return upper ? new Vector3(0, 0.003f, 0.0006f) : Vector3.Zero;
            case ArkitBlendshape.MouthSmileLeft: case ArkitBlendshape.MouthSmileRight: return new Vector3(s * 0.6f, 0.8f, -0.2f) * (0.009f * Lobe(p, s * 0.027f, LipCy, 0.012f));
            case ArkitBlendshape.MouthStretchLeft: case ArkitBlendshape.MouthStretchRight: return new Vector3(s, -0.2f, 0) * (0.008f * Lobe(p, s * 0.027f, LipCy, 0.012f));
            case ArkitBlendshape.MouthUpperUpLeft: case ArkitBlendshape.MouthUpperUpRight: return upper ? new Vector3(0, 0.005f * sw, 0) : Vector3.Zero;
            case ArkitBlendshape.NoseSneerLeft: case ArkitBlendshape.NoseSneerRight: return new Vector3(0, 1f, 0.2f) * (0.004f * Lobe(p, s * 0.012f, -0.008f, 0.009f));
            case ArkitBlendshape.TongueOut: return g == LandmarkGroup.LipInnerLower ? new Vector3(0, -0.003f, 0.01f) : Vector3.Zero;
            default: return Vector3.Zero;
        }
    }

    /// <summary>상호 배타 셰이프 쌍(동일 축 반대 방향 · 상자 제약이 구분).</summary>
    public static readonly (ArkitBlendshape A, ArkitBlendshape B)[] ExclusivePairs =
    {
        (ArkitBlendshape.EyeBlinkLeft, ArkitBlendshape.EyeWideLeft), (ArkitBlendshape.EyeBlinkRight, ArkitBlendshape.EyeWideRight),
        (ArkitBlendshape.EyeLookInLeft, ArkitBlendshape.EyeLookOutLeft), (ArkitBlendshape.EyeLookInRight, ArkitBlendshape.EyeLookOutRight),
        (ArkitBlendshape.EyeLookUpLeft, ArkitBlendshape.EyeLookDownLeft), (ArkitBlendshape.EyeLookUpRight, ArkitBlendshape.EyeLookDownRight),
        (ArkitBlendshape.JawLeft, ArkitBlendshape.JawRight), (ArkitBlendshape.MouthLeft, ArkitBlendshape.MouthRight),
    };

    // ══════════════════════ 헤드 포즈(Horn) ══════════════════════

    /// <summary>강체 앵커 Horn 정렬(정규 → 관측) · 관측 정규화. 무할당.</summary>
    private void SolvePose()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = _anchors.Length;
        double ax = 0, ay = 0, az = 0, bx = 0, by = 0, bz = 0;
        for (var a = 0; a < n; a++)
        {
            var i = _anchors[a] * 3;
            ax += _l0[i]; ay += _l0[i + 1]; az += _l0[i + 2];
            bx += _obs[i]; by += _obs[i + 1]; bz += _obs[i + 2];
        }
        ax /= n; ay /= n; az /= n; bx /= n; by /= n; bz /= n;
        Span<double> S = stackalloc double[9];
        S.Clear();
        double na = 0, nb = 0;
        for (var a = 0; a < n; a++)
        {
            var i = _anchors[a] * 3;
            double px = _l0[i] - ax, py = _l0[i + 1] - ay, pz = _l0[i + 2] - az;
            double qx = _obs[i] - bx, qy = _obs[i + 1] - by, qz = _obs[i + 2] - bz;
            S[0] += px * qx; S[1] += px * qy; S[2] += px * qz;
            S[3] += py * qx; S[4] += py * qy; S[5] += py * qz;
            S[6] += pz * qx; S[7] += pz * qy; S[8] += pz * qz;
            na += px * px + py * py + pz * pz; nb += qx * qx + qy * qy + qz * qz;
        }
        double sxx = S[0], sxy = S[1], sxz = S[2], syx = S[3], syy = S[4], syz = S[5], szx = S[6], szy = S[7], szz = S[8];
        Span<double> N = stackalloc double[16];
        N[0] = sxx + syy + szz; N[1] = syz - szy; N[2] = szx - sxz; N[3] = sxy - syx;
        N[5] = sxx - syy - szz; N[6] = sxy + syx; N[7] = szx + sxz;
        N[10] = -sxx + syy - szz; N[11] = syz + szy;
        N[15] = -sxx - syy + szz;
        N[4] = N[1]; N[8] = N[2]; N[12] = N[3]; N[9] = N[6]; N[13] = N[7]; N[14] = N[11];
        Span<double> V = stackalloc double[16];
        Jacobi4(N, V);
        var best = 0;
        for (var c = 1; c < 4; c++) if (N[c * 5] > N[best * 5]) best = c;
        var q = Quaternion.Normalize(new Quaternion((float)V[4 + best], (float)V[8 + best], (float)V[12 + best], (float)V[best]));
        if (Quaternion.Dot(q, _q) < 0) q = -q;
        _q = q;
        _s = (float)Math.Sqrt(nb / Math.Max(1e-18, na));
        _t = new Vector3((float)bx, (float)by, (float)bz) - _s * Vector3.Transform(new Vector3((float)ax, (float)ay, (float)az), q);
        var inv = Quaternion.Conjugate(q);
        var invS = 1f / _s;
        for (var i = 0; i < Landmarks; i++)
        {
            var c = Vector3.Transform(new Vector3(_obs[i * 3], _obs[i * 3 + 1], _obs[i * 3 + 2]) - _t, inv) * invS;
            _can[i * 3] = c.X; _can[i * 3 + 1] = c.Y; _can[i * 3 + 2] = c.Z;
            _r[i * 3] = c.X - _l0[i * 3]; _r[i * 3 + 1] = c.Y - _l0[i * 3 + 1]; _r[i * 3 + 2] = c.Z - _l0[i * 3 + 2];
        }
        // 요(Y) · 피치(X) · 롤(Z) — 표시용
        var fwd = Vector3.Transform(Vector3.UnitZ, q); var up = Vector3.Transform(Vector3.UnitY, q);
        HeadYawDeg = MathF.Atan2(fwd.X, fwd.Z) * 180f / MathF.PI;
        HeadPitchDeg = MathF.Asin(Math.Clamp(-fwd.Y, -1f, 1f)) * 180f / MathF.PI;
        HeadRollDeg = MathF.Atan2(-up.X, up.Y) * 180f / MathF.PI;
        LastPoseUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>대칭 4×4 순환 Jacobi(a 대각 = 고유값 · v 열 = 고유벡터). 무할당.</summary>
    private static void Jacobi4(Span<double> a, Span<double> v)
    {
        v.Clear();
        for (var i = 0; i < 4; i++) v[i * 5] = 1;
        for (var sweep = 0; sweep < 12; sweep++)
        {
            double off = 0;
            for (var p = 0; p < 4; p++) for (var q = p + 1; q < 4; q++) off += a[p * 4 + q] * a[p * 4 + q];
            if (off < 1e-30) break;
            for (var p = 0; p < 4; p++)
                for (var q = p + 1; q < 4; q++)
                {
                    var apq = a[p * 4 + q];
                    if (Math.Abs(apq) < 1e-300) continue;
                    var th = (a[q * 4 + q] - a[p * 4 + p]) / (2 * apq);
                    var t = Math.Sign(th) / (Math.Abs(th) + Math.Sqrt(th * th + 1));
                    if (th == 0) t = 1;
                    var c = 1 / Math.Sqrt(t * t + 1); var s = t * c;
                    for (var k = 0; k < 4; k++)
                    {
                        double akp = a[k * 4 + p], akq = a[k * 4 + q];
                        a[k * 4 + p] = c * akp - s * akq; a[k * 4 + q] = s * akp + c * akq;
                    }
                    for (var k = 0; k < 4; k++)
                    {
                        double apk = a[p * 4 + k], aqk = a[q * 4 + k];
                        a[p * 4 + k] = c * apk - s * aqk; a[q * 4 + k] = s * apk + c * aqk;
                    }
                    for (var k = 0; k < 4; k++)
                    {
                        double vkp = v[k * 4 + p], vkq = v[k * 4 + q];
                        v[k * 4 + p] = c * vkp - s * vkq; v[k * 4 + q] = s * vkp + c * vkq;
                    }
                }
        }
    }

    // ══════════════════════ SIMD 투영 / 재구성 ══════════════════════

    /// <summary>b = Dᵀ r(셰이프 레인 · 좌표 오름차순 누적 → 경로 간 비트 동일).</summary>
    public void Project(ReadOnlySpan<float> r, Span<float> b)
    {
        switch (Path)
        {
            case FaceSimdPath.V256 when Vector256.IsHardwareAccelerated: ProjectV256(r, b); break;
            case FaceSimdPath.V128 when Vector128.IsHardwareAccelerated: ProjectV128(r, b); break;
            default: ProjectScalar(r, b); break;
        }
    }

    public void ProjectScalar(ReadOnlySpan<float> r, Span<float> b)
    {
        for (var k = 0; k < Kp; k++)
        {
            var acc = 0f;
            for (var c = 0; c < C; c++) acc += _dT[c * Kp + k] * r[c];
            b[k] = acc;
        }
    }

    public void ProjectV128(ReadOnlySpan<float> r, Span<float> b)
    {
        ref var d = ref MemoryMarshal.GetArrayDataReference(_dT);
        for (var k = 0; k < Kp; k += 4)
        {
            var acc = Vector128<float>.Zero;
            for (var c = 0; c < C; c++) acc += Vector128.LoadUnsafe(ref d, (nuint)(c * Kp + k)) * Vector128.Create(r[c]);
            acc.StoreUnsafe(ref MemoryMarshal.GetReference(b), (nuint)k);
        }
    }

    public void ProjectV256(ReadOnlySpan<float> r, Span<float> b)
    {
        ref var d = ref MemoryMarshal.GetArrayDataReference(_dT);
        for (var k = 0; k < Kp; k += 8)
        {
            var acc = Vector256<float>.Zero;
            for (var c = 0; c < C; c++) acc += Vector256.LoadUnsafe(ref d, (nuint)(c * Kp + k)) * Vector256.Create(r[c]);
            acc.StoreUnsafe(ref MemoryMarshal.GetReference(b), (nuint)k);
        }
    }

    /// <summary>L(w) = L₀ + Σ wₖ Dₖ(좌표 레인 · 셰이프 오름차순 누적 → 비트 동일). dst 길이 ≥ Cp.</summary>
    public void Reconstruct(ReadOnlySpan<float> w, Span<float> dst)
    {
        switch (Path)
        {
            case FaceSimdPath.V256 when Vector256.IsHardwareAccelerated: ReconstructV256(w, dst); break;
            case FaceSimdPath.V128 when Vector128.IsHardwareAccelerated: ReconstructV128(w, dst); break;
            default: ReconstructScalar(w, dst); break;
        }
    }

    public void ReconstructScalar(ReadOnlySpan<float> w, Span<float> dst)
    {
        for (var c = 0; c < Cp; c++)
        {
            var acc = _l0[c];
            for (var k = 0; k < K; k++) acc += w[k] * _dK[k * Cp + c];
            dst[c] = acc;
        }
    }

    public void ReconstructV128(ReadOnlySpan<float> w, Span<float> dst)
    {
        ref var d = ref MemoryMarshal.GetArrayDataReference(_dK);
        ref var l = ref MemoryMarshal.GetArrayDataReference(_l0);
        for (var c = 0; c < Cp; c += 4)
        {
            var acc = Vector128.LoadUnsafe(ref l, (nuint)c);
            for (var k = 0; k < K; k++) acc += Vector128.Create(w[k]) * Vector128.LoadUnsafe(ref d, (nuint)(k * Cp + c));
            acc.StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)c);
        }
    }

    public void ReconstructV256(ReadOnlySpan<float> w, Span<float> dst)
    {
        ref var d = ref MemoryMarshal.GetArrayDataReference(_dK);
        ref var l = ref MemoryMarshal.GetArrayDataReference(_l0);
        for (var c = 0; c < Cp; c += 8)
        {
            var acc = Vector256.LoadUnsafe(ref l, (nuint)c);
            for (var k = 0; k < K; k++) acc += Vector256.Create(w[k]) * Vector256.LoadUnsafe(ref d, (nuint)(k * Cp + c));
            acc.StoreUnsafe(ref MemoryMarshal.GetReference(dst), (nuint)c);
        }
    }

    /// <summary>가중치 → 468 랜드마크(정규 좌표 · 미리보기/합성 · 무할당).</summary>
    public void Forward(ReadOnlySpan<float> w, Span<Vector3> dst)
    {
        Reconstruct(w, _rec);
        for (var i = 0; i < Landmarks; i++) dst[i] = new Vector3(_rec[i * 3], _rec[i * 3 + 1], _rec[i * 3 + 2]);
    }

    // ══════════════════════ 역산 ══════════════════════

    /// <summary>포즈 → 투영 → 상자 제약 좌표 하강 → 잔차. 무할당.</summary>
    public void Solve()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        SolvePose();
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Project(_r, _b);
        LastProjectUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t1) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        var sweeps = Sweeps(4, 0f);
        Span<float> chol = stackalloc float[K * K];
        Span<float> rhs = stackalloc float[K];
        Span<int> free = stackalloc int[K];
        var newton = 0;
        for (; newton < MaxNewton; newton++)
        {
            if (!NewtonStep(chol, rhs, free)) break;
            var d = Sweeps(1, 0f);
            sweeps += d;
            if (LastMaxDelta < 1e-6f) break;
        }
        LastNewton = newton;
        LastSweeps = sweeps;
        _w.AsSpan().CopyTo(_wExt);
        Reconstruct(_wExt, _rec);
        double e = 0;
        for (var c = 0; c < C; c++) { var d = _rec[c] - _can[c]; e += d * d; }
        ResidualMm = (float)Math.Sqrt(e / Landmarks) * 1000f;
        Solves++;
        LastSolveUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>투영 좌표 하강 n 회(반환 = 수행 횟수 · 최대 변화 기록).</summary>
    private int Sweeps(int n, float tol)
    {
        var done = 0;
        for (; done < n; done++)
        {
            var maxD = 0f;
            for (var k = 0; k < K; k++)
            {
                var row = k * K;
                var acc = _b[k];
                for (var j = 0; j < K; j++) acc -= _g[row + j] * _w[j];
                var nw = Math.Clamp(_w[k] + (acc - Lambda * _w[k]) / _diag[k], 0f, 1f);
                maxD = MathF.Max(maxD, MathF.Abs(nw - _w[k]));
                _w[k] = nw;
            }
            LastMaxDelta = maxD;
            if (maxD < tol) { done++; break; }
        }
        return done;
    }

    /// <summary>
    /// 활성 집합 투영 뉴턴: KKT 위반 없는 경계 변수 고정 → 자유 변수 (G_FF + λI) x = b_F − G_F,B w_B 콜레스키 해 → [0,1] 투영. 반환 = 진행 여부.
    /// </summary>
    private bool NewtonStep(Span<float> chol, Span<float> rhs, Span<int> free)
    {
        var nf = 0;
        for (var k = 0; k < K; k++)
        {
            var g = Lambda * _w[k] - _b[k];
            for (var j = 0; j < K; j++) g += _g[k * K + j] * _w[j];
            var atLo = _w[k] <= 0f && g >= 0f; var atHi = _w[k] >= 1f && g <= 0f;
            if (!atLo && !atHi) free[nf++] = k;
        }
        if (nf == 0) return false;
        for (var a = 0; a < nf; a++)
        {
            var ka = free[a];
            var r = _b[ka];
            for (var j = 0; j < K; j++) r -= _g[ka * K + j] * _w[j];
            for (var c = 0; c < nf; c++) r += _g[ka * K + free[c]] * _w[free[c]];
            rhs[a] = r;
            for (var c = 0; c <= a; c++) chol[a * K + c] = _g[ka * K + free[c]] + (a == c ? Lambda : 0f);
        }
        // 콜레스키(하삼각 · 제자리)
        for (var a = 0; a < nf; a++)
        {
            for (var c = 0; c <= a; c++)
            {
                var sum = chol[a * K + c];
                for (var m = 0; m < c; m++) sum -= chol[a * K + m] * chol[c * K + m];
                if (a == c) { if (sum <= 1e-20f) return false; chol[a * K + a] = MathF.Sqrt(sum); }
                else chol[a * K + c] = sum / chol[c * K + c];
            }
        }
        for (var a = 0; a < nf; a++)
        {
            var sum = rhs[a];
            for (var m = 0; m < a; m++) sum -= chol[a * K + m] * rhs[m];
            rhs[a] = sum / chol[a * K + a];
        }
        for (var a = nf - 1; a >= 0; a--)
        {
            var sum = rhs[a];
            for (var m = a + 1; m < nf; m++) sum -= chol[m * K + a] * rhs[m];
            rhs[a] = sum / chol[a * K + a];
        }
        for (var a = 0; a < nf; a++) _w[free[a]] = Math.Clamp(rhs[a], 0f, 1f);
        return true;
    }

    public void ResetWeights() => Array.Clear(_w);

    /// <summary>정규 좌표 홍채 5점 평균 변위 → 시선(요 + = 피사체 왼쪽 · 피치 + = 위 · 도).</summary>
    public void Gaze(int side, out float yawDeg, out float pitchDeg)
    {
        var b = IrisBase + (side > 0 ? 0 : 5);
        float dx = 0, dy = 0;
        for (var i = b; i < b + 5; i++) { dx += _can[i * 3] - NeutralPts[i].X; dy += _can[i * 3 + 1] - NeutralPts[i].Y; }
        dx *= 0.2f; dy *= 0.2f;
        yawDeg = MathF.Asin(Math.Clamp(dx / EyeballRadius, -1f, 1f)) * 180f / MathF.PI;
        pitchDeg = MathF.Asin(Math.Clamp(dy / EyeballRadius, -1f, 1f)) * 180f / MathF.PI;
    }

    /// <summary>시선 각 → eyeLook 4 가중치(side +1 = 왼눈).</summary>
    public static void GazeToWeights(int side, float yawDeg, float pitchDeg, Span<float> w52)
    {
        var l = side > 0;
        float y = yawDeg / MaxGazeDeg, p = pitchDeg / MaxGazeDeg;
        // 왼눈: +요 = 바깥(Out) · 오른눈: +요 = 안쪽(In)
        var outW = Math.Clamp(l ? y : -y, 0f, 1f); var inW = Math.Clamp(l ? -y : y, 0f, 1f);
        w52[(int)(l ? ArkitBlendshape.EyeLookOutLeft : ArkitBlendshape.EyeLookOutRight)] = MathF.Sin(outW * MaxGazeDeg * MathF.PI / 180f) / MathF.Sin(MaxGazeDeg * MathF.PI / 180f);
        w52[(int)(l ? ArkitBlendshape.EyeLookInLeft : ArkitBlendshape.EyeLookInRight)] = MathF.Sin(inW * MaxGazeDeg * MathF.PI / 180f) / MathF.Sin(MaxGazeDeg * MathF.PI / 180f);
        w52[(int)(l ? ArkitBlendshape.EyeLookUpLeft : ArkitBlendshape.EyeLookUpRight)] = MathF.Sin(Math.Clamp(p, 0f, 1f) * MaxGazeDeg * MathF.PI / 180f) / MathF.Sin(MaxGazeDeg * MathF.PI / 180f);
        w52[(int)(l ? ArkitBlendshape.EyeLookDownLeft : ArkitBlendshape.EyeLookDownRight)] = MathF.Sin(Math.Clamp(-p, 0f, 1f) * MaxGazeDeg * MathF.PI / 180f) / MathF.Sin(MaxGazeDeg * MathF.PI / 180f);
    }
}
