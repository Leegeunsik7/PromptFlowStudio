using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace PromptFlowStudio.Services.StrandHair;

public enum HairSimdPath : byte { Scalar = 0, V128, V256 }

/// <summary>SIMD 레인 추상(정적 추상 멤버 · 구조체 특수화 → 무할당 인라인). 모든 연산은 IEEE 단일 정밀 → 경로 간 비트 동일.</summary>
public interface IHairLane<TV> where TV : struct
{
    static abstract int Width { get; }
    static abstract TV Load(float[] a, int i);
    static abstract void Store(float[] a, int i, TV v);
    static abstract TV S(float f);
    static abstract TV Add(TV a, TV b);
    static abstract TV Sub(TV a, TV b);
    static abstract TV Mul(TV a, TV b);
    static abstract TV Div(TV a, TV b);
    static abstract TV Sqrt(TV a);
    static abstract TV Max(TV a, TV b);
    static abstract TV Min(TV a, TV b);
    /// <summary>a &gt; b ? t : f.</summary>
    static abstract TV Gt(TV a, TV b, TV t, TV f);
}

public readonly struct ScalarLane : IHairLane<float>
{
    public static int Width => 1;
    public static float Load(float[] a, int i) => a[i];
    public static void Store(float[] a, int i, float v) => a[i] = v;
    public static float S(float f) => f;
    public static float Add(float a, float b) => a + b;
    public static float Sub(float a, float b) => a - b;
    public static float Mul(float a, float b) => a * b;
    public static float Div(float a, float b) => a / b;
    public static float Sqrt(float a) => MathF.Sqrt(a);
    public static float Max(float a, float b) => MathF.Max(a, b);
    public static float Min(float a, float b) => MathF.Min(a, b);
    public static float Gt(float a, float b, float t, float f) => a > b ? t : f;
}

public readonly struct V128Lane : IHairLane<Vector128<float>>
{
    public static int Width => 4;
    public static Vector128<float> Load(float[] a, int i) => Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
    public static void Store(float[] a, int i, Vector128<float> v) => v.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
    public static Vector128<float> S(float f) => Vector128.Create(f);
    public static Vector128<float> Add(Vector128<float> a, Vector128<float> b) => a + b;
    public static Vector128<float> Sub(Vector128<float> a, Vector128<float> b) => a - b;
    public static Vector128<float> Mul(Vector128<float> a, Vector128<float> b) => a * b;
    public static Vector128<float> Div(Vector128<float> a, Vector128<float> b) => a / b;
    public static Vector128<float> Sqrt(Vector128<float> a) => Vector128.Sqrt(a);
    public static Vector128<float> Max(Vector128<float> a, Vector128<float> b) => Vector128.Max(a, b);
    public static Vector128<float> Min(Vector128<float> a, Vector128<float> b) => Vector128.Min(a, b);
    public static Vector128<float> Gt(Vector128<float> a, Vector128<float> b, Vector128<float> t, Vector128<float> f) => Vector128.ConditionalSelect(Vector128.GreaterThan(a, b), t, f);
}

public readonly struct V256Lane : IHairLane<Vector256<float>>
{
    public static int Width => 8;
    public static Vector256<float> Load(float[] a, int i) => Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
    public static void Store(float[] a, int i, Vector256<float> v) => v.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
    public static Vector256<float> S(float f) => Vector256.Create(f);
    public static Vector256<float> Add(Vector256<float> a, Vector256<float> b) => a + b;
    public static Vector256<float> Sub(Vector256<float> a, Vector256<float> b) => a - b;
    public static Vector256<float> Mul(Vector256<float> a, Vector256<float> b) => a * b;
    public static Vector256<float> Div(Vector256<float> a, Vector256<float> b) => a / b;
    public static Vector256<float> Sqrt(Vector256<float> a) => Vector256.Sqrt(a);
    public static Vector256<float> Max(Vector256<float> a, Vector256<float> b) => Vector256.Max(a, b);
    public static Vector256<float> Min(Vector256<float> a, Vector256<float> b) => Vector256.Min(a, b);
    public static Vector256<float> Gt(Vector256<float> a, Vector256<float> b, Vector256<float> t, Vector256<float> f) => Vector256.ConditionalSelect(Vector256.GreaterThan(a, b), t, f);
}

/// <summary>스트랜드 충돌/앵커 결선점(가드 구현 · 인터페이스 호출 무할당).</summary>
public interface IStrandCollider
{
    /// <summary>서브스텝 alpha(0..1) 두피 자세로 모근 2점 운동학 고정.</summary>
    void Anchor(StrandHairPbdDynamicsEngine e, float alpha);
    void Collide(StrandHairPbdDynamicsEngine e, int iteration, int substep);
}

/// <summary>
/// [PromptFlow Studio v17.15.0] 💇 실시간 스트랜드 PBD 헤어 동역학 &amp; 가이드 커브 엔진(<see cref="Step"/> 무할당).
///   ▸ 구조: 가이드 커브 G(기본 256) × 16 입자(15 세그먼트) + 가이드당 밀집 자식 C(기본 400) → 102,400 자식 + 256 가이드 스트랜드 · 1.64 M 자식 정점/프레임.
///   ▸ PBD(서브스텝 2 × 반복 4 · Verlet 감쇠 적분 · 모근 2점 역질량 0): 신장 (i, i+1) · 굽힘 (i, i+2) · 비틀림 (i, i+3) 교차 대각 거리(나선/웨이브 키랄성 유지) ·
///     스타일 형상 정합(두피 좌표 레스트 · 모근→모끝 감쇠) · LRA 장거리 부착(과신장 차단) · 반복 끝 DFTL 정확 길이 투영(보정 × 0.9 직전 위치 반영 = 속도 주입 억제) → 충돌.
///     가우스-자이델은 모근→모끝 순 · 가이드 = SIMD 레인(SoA [입자][가이드]).
///   ▸ 자식 생성: 가이드 평행 이송 프레임(t · n · b) 기준 두피 원판 오프셋 + 뭉침(클럼프 중심 보간 · 모끝 쪽 강도 tᵏ) + 잔머리 확산 + 컬(cos(a+b) 전개 · 레인 내 삼각 함수 0) · 자식 = SIMD 레인.
///   ▸ SIMD: <see cref="IHairLane{TV}"/> 정적 추상 커널 1벌을 V256 · V128 · 스칼라로 특수화 → 비트 동일.
/// </summary>
public sealed class StrandHairPbdDynamicsEngine
{
    public const int N = 16, Seg = N - 1;
    public static readonly Vector3 HeadCenterLocal = new(0f, 0.04f, 0.01f);
    public const float HeadRadius = 0.098f, RootRadius = 0.1f;

    public readonly int G, C;
    public readonly float[] X, Y, Z, PX, PY, PZ, FX, FY, FZ;
    public readonly float[] RLx, RLy, RLz;
    public readonly float[] Nx, Ny, Nz, Bx, By, Bz;
    public readonly float[] CX, CY, CZ;
    public readonly float[] ChildU, ChildV, ChildCU, ChildCV, ChildFr, ChildCa, ChildSa;
    private readonly float[] _len, _bend, _tors, _lra;
    private readonly float[] _w = new float[N], _shapeK = new float[N];
    private readonly float[] _clumpS = new float[N], _frizzI = new float[N], _curlA = new float[N], _curlC = new float[N], _curlS = new float[N];
    private readonly Vector3[] _rootLocal, _rootNormal, _rootSide;
    private readonly float[] _guideLen;

    public StrandHairPbdDynamicsEngine(int guides = 256, int childrenPerGuide = 400)
    {
        if (guides % 8 != 0 || childrenPerGuide % 8 != 0) throw new ArgumentException("가이드/자식 수는 8 의 배수");
        G = guides; C = childrenPerGuide;
        X = new float[N * G]; Y = new float[N * G]; Z = new float[N * G]; PX = new float[N * G]; PY = new float[N * G]; PZ = new float[N * G];
        FX = new float[N * G]; FY = new float[N * G]; FZ = new float[N * G];
        RLx = new float[N * G]; RLy = new float[N * G]; RLz = new float[N * G];
        Nx = new float[N * G]; Ny = new float[N * G]; Nz = new float[N * G]; Bx = new float[N * G]; By = new float[N * G]; Bz = new float[N * G];
        CX = new float[G * N * C]; CY = new float[G * N * C]; CZ = new float[G * N * C];
        ChildU = new float[G * C]; ChildV = new float[G * C]; ChildCU = new float[G * C]; ChildCV = new float[G * C]; ChildFr = new float[G * C]; ChildCa = new float[G * C]; ChildSa = new float[G * C];
        _len = new float[Seg * G]; _bend = new float[(N - 2) * G]; _tors = new float[(N - 3) * G]; _lra = new float[N * G];
        _rootLocal = new Vector3[G]; _rootNormal = new Vector3[G]; _rootSide = new Vector3[G]; _guideLen = new float[G];
        for (var i = 0; i < N; i++) _w[i] = i < 2 ? 0f : 1f;
        Path = Vector256.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HairSimdPath.V256 : Vector128.IsHardwareAccelerated && BitConverter.IsLittleEndian ? HairSimdPath.V128 : HairSimdPath.Scalar;
        HeadRot = PrevHeadRot = SubRot = Quaternion.Identity;
        Groom();
    }

    public HairSimdPath Path { get; set; }
    public Vector3 HeadPos { get; private set; }
    public Quaternion HeadRot { get; private set; }
    public Vector3 PrevHeadPos { get; private set; }
    public Quaternion PrevHeadRot { get; private set; }
    public Vector3 SubPos { get; private set; }
    public Quaternion SubRot { get; private set; }
    public Vector3 Gravity { get; set; } = new(0f, -9.81f, 0f);
    public float Damping { get; set; } = 0.03f;
    public float StretchStiffness { get; set; } = 1f;
    public float BendStiffness { get; set; } = 0.3f;
    public float TorsionStiffness { get; set; } = 0.12f;
    public float ShapeStiffness { get; set; } = 0.05f;
    public int Iterations { get; set; } = 4;
    public int Substeps { get; set; } = 2;
    public bool ChildrenEnabled { get; set; } = true;
    /// <summary>반복 끝 FTL 길이 투영(신장 0 보장 · 충돌 직전).</summary>
    public bool FollowTheLeader { get; set; } = true;
    /// <summary>DFTL 속도 보정 계수 s(보정 변위 × s 를 직전 위치에 반영).</summary>
    public float FtlDamping { get; set; } = 0.9f;
    // 그루밍
    public float Length { get; set; } = 0.3f;
    public float Comb { get; set; } = 0.6f;
    public float PartX { get; set; } = 0.02f;
    public float Spread { get; set; } = 0.012f;
    public int ClumpCount { get; private set; } = 6;
    public float ClumpStrength { get; set; } = 0.5f;
    public float ClumpShape { get; set; } = 1.5f;
    public float Frizz { get; set; } = 0.002f;
    public float CurlAmp { get; set; }
    public float CurlFreq { get; set; } = 1.3f;
    // 상태
    public float LastSubstepDt { get; private set; } = 1f / 120f;
    public double LastSolveUs { get; private set; }
    public double LastChildUs { get; private set; }
    public float MaxStretchError { get; private set; }
    public float MeanStretchError { get; private set; }
    public long Steps { get; private set; }
    public int StrandCount => G + G * C;
    public long ChildVertexCount => (long)G * C * N;
    public float MaxChildOffset => Spread + Frizz + CurlAmp + 0.002f;
    public Vector3 RootLocal(int g) => _rootLocal[g];
    public float GuideLength(int g) => _guideLen[g];
    public float RestLength(int seg, int g) => _len[seg * G + g];
    public Vector3 P(int i, int g) => new(X[i * G + g], Y[i * G + g], Z[i * G + g]);
    public Vector3 Child(int g, int c, int i) { var k = (g * N + i) * C + c; return new Vector3(CX[k], CY[k], CZ[k]); }

    private static float Hash(int a, int b)
    {
        var h = (uint)(a * 73856093) ^ (uint)(b * 19349663) ^ 0x9E3779B9u;
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        return (h & 0xFFFFFF) / 16777216f;
    }

    // ══════════════════════ 그루밍(제어 평면) ══════════════════════

    /// <summary>두피 마스크: 위/뒤/옆머리(얼굴 · 귀 제외).</summary>
    public static bool ScalpMask(Vector3 d) => d.Y > -0.3f && !(d.Z > 0.3f && d.Y < 0.55f) && !(MathF.Abs(d.X) > 0.8f && d.Y < 0.15f && d.Z > -0.4f);

    /// <summary>모근 배치(황금각 나선 · 마스크 · 균등 추출) → 빗질 폴리라인(두피 밀어내기) → 레스트 · 자식 초기화.</summary>
    public void Groom()
    {
        const int M = 6000;
        var accepted = 0;
        for (var k = 0; k < M; k++) if (ScalpMask(Spiral(k, M))) accepted++;
        var pick = 0; var idx = 0;
        for (var k = 0; k < M && pick < G; k++)
        {
            var d = Spiral(k, M);
            if (!ScalpMask(d)) continue;
            if (idx++ != (int)((long)pick * accepted / G)) continue;
            _rootNormal[pick] = d;
            _rootLocal[pick] = HeadCenterLocal + d * RootRadius;
            var side = Vector3.Cross(d, Vector3.UnitY);
            _rootSide[pick] = side.LengthSquared() < 1e-6f ? Vector3.UnitX : Vector3.Normalize(side);
            pick++;
        }
        for (var g = 0; g < G; g++)
        {
            var len = Length * (0.9f + 0.2f * Hash(g, 7)) * (1f - 0.35f * MathF.Max(0f, _rootNormal[g].Z));   // 앞머리 짧게
            _guideLen[g] = len;
            var seg = len / Seg;
            var p = _rootLocal[g];
            var dir = _rootNormal[g];
            var sideSign = MathF.Sign(p.X - PartX);
            for (var i = 0; i < N; i++)
            {
                if (i > 0)
                {
                    var comb = new Vector3(0.15f * sideSign, -0.45f, -0.2f) * Comb;
                    var desired = Vector3.Normalize(dir + comb * (i == 1 ? 0.3f : 1f));
                    var np = p + desired * seg;
                    var q = np - HeadCenterLocal;
                    var ql = q.Length();
                    if (ql < RootRadius + 0.004f) np = HeadCenterLocal + q / ql * (RootRadius + 0.004f);
                    dir = Vector3.Normalize(np - p);
                    p = np;
                }
                var k = i * G + g;
                RLx[k] = p.X; RLy[k] = p.Y; RLz[k] = p.Z;
            }
        }
        PlaceFromRest(HeadPos, HeadRot);
        CaptureRest(false);
        InitChildren();
        UpdateChildProfiles();
    }

    private static Vector3 Spiral(int k, int m)
    {
        var y = 1f - 2f * (k + 0.5f) / m;
        var r = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
        var th = k * 2.39996323f;
        return new Vector3(r * MathF.Cos(th), y, r * MathF.Sin(th));
    }

    /// <summary>레스트(두피 좌표) → 월드 배치(속도 0).</summary>
    public void PlaceFromRest(Vector3 pos, Quaternion rot)
    {
        HeadPos = PrevHeadPos = SubPos = pos; HeadRot = PrevHeadRot = SubRot = rot;
        for (var k = 0; k < N * G; k++)
        {
            var w = pos + Vector3.Transform(new Vector3(RLx[k], RLy[k], RLz[k]), rot);
            X[k] = PX[k] = w.X; Y[k] = PY[k] = w.Y; Z[k] = PZ[k] = w.Z;
        }
        Array.Clear(FX); Array.Clear(FY); Array.Clear(FZ);
    }

    /// <summary>현재 월드 형상 → 레스트(두피 좌표 · 굽힘/비틀림/LRA/세그먼트 길이). 안착(settle) 후 호출하면 평형 = 그루밍 스타일.</summary>
    public void CaptureRest(bool fromWorld = true)
    {
        var inv = Quaternion.Conjugate(HeadRot);
        for (var k = 0; fromWorld && k < N * G; k++)
        {
            var l = Vector3.Transform(new Vector3(X[k], Y[k], Z[k]) - HeadPos, inv);
            RLx[k] = l.X; RLy[k] = l.Y; RLz[k] = l.Z;
        }
        for (var g = 0; g < G; g++)
        {
            for (var i = 0; i < N; i++)
            {
                var p = RestL(i, g);
                if (i < Seg) _len[i * G + g] = Vector3.Distance(p, RestL(i + 1, g));
                if (i < N - 2) _bend[i * G + g] = Vector3.Distance(p, RestL(i + 2, g));
                if (i < N - 3) _tors[i * G + g] = Vector3.Distance(p, RestL(i + 3, g));
                _lra[i * G + g] = Vector3.Distance(RestL(0, g), p) * 1.02f;
            }
        }
        for (var i = 0; i < N; i++) { var t = i / (N - 1f); _shapeK[i] = i < 2 ? 0f : (1f - t) * (1f - t); }
    }

    private Vector3 RestL(int i, int g) => new(RLx[i * G + g], RLy[i * G + g], RLz[i * G + g]);

    private uint _rng = 0x1715u;

    private float U()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    private void InitChildren()
    {
        _rng = 0x1715u;
        for (var g = 0; g < G; g++)
            for (var c = 0; c < C; c++)
            {
                var k = g * C + c;
                var r = Spread * MathF.Sqrt(U()); var a = MathF.Tau * U();
                ChildU[k] = r * MathF.Cos(a); ChildV[k] = r * MathF.Sin(a);
                ChildFr[k] = 2f * U() - 1f;
                var ph = MathF.Tau * U();
                ChildCa[k] = MathF.Cos(ph); ChildSa[k] = MathF.Sin(ph);
            }
        AssignClumps();
    }

    /// <summary>클럼프 수 변경(가이드별 결정적 중심 · 자식 = 최근접 중심).</summary>
    public void SetClumpCount(int k)
    {
        ClumpCount = Math.Clamp(k, 1, 32);
        AssignClumps();
    }

    private void AssignClumps()
    {
        Span<float> cu = stackalloc float[32];
        Span<float> cv = stackalloc float[32];
        for (var g = 0; g < G; g++)
        {
            for (var j = 0; j < ClumpCount; j++)
            {
                var r = Spread * 0.75f * MathF.Sqrt(Hash(g * 31 + j, 3)); var a = MathF.Tau * Hash(g * 31 + j, 5);
                cu[j] = ClumpCount == 1 ? 0f : r * MathF.Cos(a); cv[j] = ClumpCount == 1 ? 0f : r * MathF.Sin(a);
            }
            for (var c = 0; c < C; c++)
            {
                var k = g * C + c;
                var best = 0; var bd = float.MaxValue;
                for (var j = 0; j < ClumpCount; j++)
                {
                    float du = ChildU[k] - cu[j], dv = ChildV[k] - cv[j];
                    var d = du * du + dv * dv;
                    if (d < bd) { bd = d; best = j; }
                }
                ChildCU[k] = cu[best]; ChildCV[k] = cv[best];
            }
        }
    }

    /// <summary>입자별 자식 프로파일(뭉침 tᵏ · 확산 t² · 컬 진폭/위상) 갱신(무할당).</summary>
    public void UpdateChildProfiles()
    {
        for (var i = 0; i < N; i++)
        {
            var t = i / (N - 1f);
            _clumpS[i] = Math.Clamp(ClumpStrength, 0f, 1f) * MathF.Pow(t, ClumpShape);
            _frizzI[i] = Frizz * t * t;
            var ramp = Math.Clamp((t - 0.1f) / 0.3f, 0f, 1f);
            _curlA[i] = CurlAmp * ramp;
            _curlC[i] = MathF.Cos(i * CurlFreq); _curlS[i] = MathF.Sin(i * CurlFreq);
        }
    }

    // ══════════════════════ 머리 자세 · 앵커 ══════════════════════

    /// <summary>프레임 머리 자세(두피 강체 변환) 갱신 — 직전 자세는 서브스텝 보간 시작점.</summary>
    public void SetHeadPose(Vector3 pos, Quaternion rot)
    {
        PrevHeadPos = HeadPos; PrevHeadRot = HeadRot;
        HeadPos = pos; HeadRot = Quaternion.Normalize(rot);
    }

    /// <summary>모근 2점을 두피 자세로 고정(운동학 락 · 역질량 0).</summary>
    public void AnchorTo(Vector3 pos, Quaternion rot)
    {
        SubPos = pos; SubRot = rot;
        for (var i = 0; i < 2; i++)
            for (var g = 0; g < G; g++)
            {
                var k = i * G + g;
                var w = pos + Vector3.Transform(new Vector3(RLx[k], RLy[k], RLz[k]), rot);
                X[k] = PX[k] = w.X; Y[k] = PY[k] = w.Y; Z[k] = PZ[k] = w.Z;
            }
    }

    // ══════════════════════ SIMD 커널 ══════════════════════

    private static void IntegrateK<TV, L>(float[] x, float[] p, float[] f, int from, int to, float keep, float g, float h2) where TV : struct where L : struct, IHairLane<TV>
    {
        TV k = L.S(keep), gg = L.S(g), hh = L.S(h2);
        for (var i = from; i < to; i += L.Width)
        {
            var xi = L.Load(x, i);
            var v = L.Mul(L.Sub(xi, L.Load(p, i)), k);
            L.Store(p, i, xi);
            L.Store(x, i, L.Add(L.Add(xi, v), L.Mul(L.Add(gg, L.Load(f, i)), hh)));
        }
    }

    private static void DistK<TV, L>(float[] X, float[] Y, float[] Z, int ia, int ib, float[] rest, int ir, float wa, float wb, float k, int n) where TV : struct where L : struct, IHairLane<TV>
    {
        TV wsum = L.S(wa + wb), kk = L.S(k), eps = L.S(1e-9f), va = L.S(wa), vb = L.S(wb);
        for (var g = 0; g < n; g += L.Width)
        {
            TV ax = L.Load(X, ia + g), ay = L.Load(Y, ia + g), az = L.Load(Z, ia + g);
            TV bx = L.Load(X, ib + g), by = L.Load(Y, ib + g), bz = L.Load(Z, ib + g);
            TV dx = L.Sub(bx, ax), dy = L.Sub(by, ay), dz = L.Sub(bz, az);
            var d = L.Sqrt(L.Add(L.Add(L.Mul(dx, dx), L.Mul(dy, dy)), L.Mul(dz, dz)));
            var s = L.Mul(L.Div(L.Sub(d, L.Load(rest, ir + g)), L.Mul(L.Max(d, eps), wsum)), kk);
            TV sx = L.Mul(dx, s), sy = L.Mul(dy, s), sz = L.Mul(dz, s);
            if (wa > 0f) { L.Store(X, ia + g, L.Add(ax, L.Mul(sx, va))); L.Store(Y, ia + g, L.Add(ay, L.Mul(sy, va))); L.Store(Z, ia + g, L.Add(az, L.Mul(sz, va))); }
            if (wb > 0f) { L.Store(X, ib + g, L.Sub(bx, L.Mul(sx, vb))); L.Store(Y, ib + g, L.Sub(by, L.Mul(sy, vb))); L.Store(Z, ib + g, L.Sub(bz, L.Mul(sz, vb))); }
        }
    }

    private static void ShapeK<TV, L>(float[] X, float[] Y, float[] Z, float[] rx, float[] ry, float[] rz, int at, int n, float k, in Matrix4x4 m, Vector3 p) where TV : struct where L : struct, IHairLane<TV>
    {
        TV kk = L.S(k), m11 = L.S(m.M11), m12 = L.S(m.M12), m13 = L.S(m.M13), m21 = L.S(m.M21), m22 = L.S(m.M22), m23 = L.S(m.M23), m31 = L.S(m.M31), m32 = L.S(m.M32), m33 = L.S(m.M33);
        TV px = L.S(p.X), py = L.S(p.Y), pz = L.S(p.Z);
        for (var g = 0; g < n; g += L.Width)
        {
            var i = at + g;
            TV a = L.Load(rx, i), b = L.Load(ry, i), c = L.Load(rz, i);
            var tx = L.Add(px, L.Add(L.Add(L.Mul(a, m11), L.Mul(b, m21)), L.Mul(c, m31)));
            var ty = L.Add(py, L.Add(L.Add(L.Mul(a, m12), L.Mul(b, m22)), L.Mul(c, m32)));
            var tz = L.Add(pz, L.Add(L.Add(L.Mul(a, m13), L.Mul(b, m23)), L.Mul(c, m33)));
            TV x = L.Load(X, i), y = L.Load(Y, i), z = L.Load(Z, i);
            L.Store(X, i, L.Add(x, L.Mul(L.Sub(tx, x), kk))); L.Store(Y, i, L.Add(y, L.Mul(L.Sub(ty, y), kk))); L.Store(Z, i, L.Add(z, L.Mul(L.Sub(tz, z), kk)));
        }
    }

    private static void LraK<TV, L>(float[] X, float[] Y, float[] Z, float[] lra, int at, int n) where TV : struct where L : struct, IHairLane<TV>
    {
        TV eps = L.S(1e-9f), one = L.S(1f);
        for (var g = 0; g < n; g += L.Width)
        {
            var i = at + g;
            TV rx = L.Load(X, g), ry = L.Load(Y, g), rz = L.Load(Z, g);
            TV dx = L.Sub(L.Load(X, i), rx), dy = L.Sub(L.Load(Y, i), ry), dz = L.Sub(L.Load(Z, i), rz);
            var d = L.Sqrt(L.Add(L.Add(L.Mul(dx, dx), L.Mul(dy, dy)), L.Mul(dz, dz)));
            var D = L.Load(lra, i);
            var f = L.Gt(d, D, L.Div(D, L.Max(d, eps)), one);
            L.Store(X, i, L.Add(rx, L.Mul(dx, f))); L.Store(Y, i, L.Add(ry, L.Mul(dy, f))); L.Store(Z, i, L.Add(rz, L.Mul(dz, f)));
        }
    }

    /// <summary>FTL(Follow-The-Leader) 길이 투영: x_b = x_a + (x_b − x_a)·L/|x_b − x_a| (모근 → 모끝 순 · 정확 길이).</summary>
    private static void FtlK<TV, L>(StrandHairPbdDynamicsEngine e, int ia, int ib, int ir, float velKeep) where TV : struct where L : struct, IHairLane<TV>
    {
        float[] X = e.X, Y = e.Y, Z = e.Z, PX = e.PX, PY = e.PY, PZ = e.PZ, rest = e._len;
        TV eps = L.S(1e-9f), sk = L.S(velKeep);
        for (var g = 0; g < e.G; g += L.Width)
        {
            TV ax = L.Load(X, ia + g), ay = L.Load(Y, ia + g), az = L.Load(Z, ia + g);
            TV bx = L.Load(X, ib + g), by = L.Load(Y, ib + g), bz = L.Load(Z, ib + g);
            TV dx = L.Sub(bx, ax), dy = L.Sub(by, ay), dz = L.Sub(bz, az);
            var d = L.Sqrt(L.Add(L.Add(L.Mul(dx, dx), L.Mul(dy, dy)), L.Mul(dz, dz)));
            var f = L.Div(L.Load(rest, ir + g), L.Max(d, eps));
            TV nx = L.Add(ax, L.Mul(dx, f)), ny = L.Add(ay, L.Mul(dy, f)), nz = L.Add(az, L.Mul(dz, f));
            L.Store(X, ib + g, nx); L.Store(Y, ib + g, ny); L.Store(Z, ib + g, nz);
            // DFTL: 보정 변위를 직전 위치에도 반영(속도 주입 억제)
            L.Store(PX, ib + g, L.Add(L.Load(PX, ib + g), L.Mul(L.Sub(nx, bx), sk)));
            L.Store(PY, ib + g, L.Add(L.Load(PY, ib + g), L.Mul(L.Sub(ny, by), sk)));
            L.Store(PZ, ib + g, L.Add(L.Load(PZ, ib + g), L.Mul(L.Sub(nz, bz), sk)));
        }
    }

    private static void ChildK<TV, L>(StrandHairPbdDynamicsEngine e, int g, int i, int c0, int c1) where TV : struct where L : struct, IHairLane<TV>
    {
        int G = e.G, C = e.C, k = i * G + g;
        TV gx = L.S(e.X[k]), gy = L.S(e.Y[k]), gz = L.S(e.Z[k]), nx = L.S(e.Nx[k]), ny = L.S(e.Ny[k]), nz = L.S(e.Nz[k]), bx = L.S(e.Bx[k]), by = L.S(e.By[k]), bz = L.S(e.Bz[k]);
        TV s = L.S(e._clumpS[i]), fz = L.S(e._frizzI[i]), A = L.S(e._curlA[i]), cb = L.S(e._curlC[i]), sb = L.S(e._curlS[i]);
        var outBase = (g * N + i) * C;
        for (var c = c0; c < c1; c += L.Width)
        {
            var ci = g * C + c;
            TV u = L.Load(e.ChildU, ci), v = L.Load(e.ChildV, ci), ca = L.Load(e.ChildCa, ci), sa = L.Load(e.ChildSa, ci), fr = L.Load(e.ChildFr, ci);
            var ue = L.Add(L.Add(L.Add(u, L.Mul(L.Sub(L.Load(e.ChildCU, ci), u), s)), L.Mul(fr, fz)), L.Mul(A, L.Sub(L.Mul(ca, cb), L.Mul(sa, sb))));
            var ve = L.Add(L.Add(L.Add(v, L.Mul(L.Sub(L.Load(e.ChildCV, ci), v), s)), L.Mul(fr, fz)), L.Mul(A, L.Add(L.Mul(sa, cb), L.Mul(ca, sb))));
            var o = outBase + c;
            L.Store(e.CX, o, L.Add(gx, L.Add(L.Mul(nx, ue), L.Mul(bx, ve))));
            L.Store(e.CY, o, L.Add(gy, L.Add(L.Mul(ny, ue), L.Mul(by, ve))));
            L.Store(e.CZ, o, L.Add(gz, L.Add(L.Mul(nz, ue), L.Mul(bz, ve))));
        }
    }

    // ── 경로 디스패치 ───────────────────────────────────────

    public HairSimdPath EffectivePath => Eff;

    private HairSimdPath Eff => Path switch
    {
        HairSimdPath.V256 when Vector256.IsHardwareAccelerated => HairSimdPath.V256,
        HairSimdPath.V256 or HairSimdPath.V128 when Vector128.IsHardwareAccelerated => HairSimdPath.V128,
        _ => HairSimdPath.Scalar,
    };

    private void Integrate(float h)
    {
        int from = 2 * G, to = N * G;
        var keep = 1f - Damping;
        var h2 = h * h;
        switch (Eff)
        {
            case HairSimdPath.V256:
                IntegrateK<Vector256<float>, V256Lane>(X, PX, FX, from, to, keep, Gravity.X, h2); IntegrateK<Vector256<float>, V256Lane>(Y, PY, FY, from, to, keep, Gravity.Y, h2); IntegrateK<Vector256<float>, V256Lane>(Z, PZ, FZ, from, to, keep, Gravity.Z, h2); break;
            case HairSimdPath.V128:
                IntegrateK<Vector128<float>, V128Lane>(X, PX, FX, from, to, keep, Gravity.X, h2); IntegrateK<Vector128<float>, V128Lane>(Y, PY, FY, from, to, keep, Gravity.Y, h2); IntegrateK<Vector128<float>, V128Lane>(Z, PZ, FZ, from, to, keep, Gravity.Z, h2); break;
            default:
                IntegrateK<float, ScalarLane>(X, PX, FX, from, to, keep, Gravity.X, h2); IntegrateK<float, ScalarLane>(Y, PY, FY, from, to, keep, Gravity.Y, h2); IntegrateK<float, ScalarLane>(Z, PZ, FZ, from, to, keep, Gravity.Z, h2); break;
        }
    }

    private void Dist(int ia, int ib, float[] rest, int ir, float k)
    {
        float wa = _w[ia], wb = _w[ib];
        if (wa + wb <= 0f || k <= 0f) return;
        switch (Eff)
        {
            case HairSimdPath.V256: DistK<Vector256<float>, V256Lane>(X, Y, Z, ia * G, ib * G, rest, ir * G, wa, wb, k, G); break;
            case HairSimdPath.V128: DistK<Vector128<float>, V128Lane>(X, Y, Z, ia * G, ib * G, rest, ir * G, wa, wb, k, G); break;
            default: DistK<float, ScalarLane>(X, Y, Z, ia * G, ib * G, rest, ir * G, wa, wb, k, G); break;
        }
    }

    private void Constraints(bool shape)
    {
        for (var i = 0; i < Seg; i++) Dist(i, i + 1, _len, i, StretchStiffness);
        for (var i = 0; i < N - 2; i++) Dist(i, i + 2, _bend, i, BendStiffness);
        for (var i = 0; i < N - 3; i++) Dist(i, i + 3, _tors, i, TorsionStiffness);
        if (shape && ShapeStiffness > 0f)
        {
            var m = Matrix4x4.CreateFromQuaternion(SubRot);
            for (var i = 2; i < N; i++)
            {
                var k = ShapeStiffness * _shapeK[i];
                switch (Eff)
                {
                    case HairSimdPath.V256: ShapeK<Vector256<float>, V256Lane>(X, Y, Z, RLx, RLy, RLz, i * G, G, k, m, SubPos); break;
                    case HairSimdPath.V128: ShapeK<Vector128<float>, V128Lane>(X, Y, Z, RLx, RLy, RLz, i * G, G, k, m, SubPos); break;
                    default: ShapeK<float, ScalarLane>(X, Y, Z, RLx, RLy, RLz, i * G, G, k, m, SubPos); break;
                }
            }
        }
        for (var i = 2; i < N; i++)
            switch (Eff)
            {
                case HairSimdPath.V256: LraK<Vector256<float>, V256Lane>(X, Y, Z, _lra, i * G, G); break;
                case HairSimdPath.V128: LraK<Vector128<float>, V128Lane>(X, Y, Z, _lra, i * G, G); break;
                default: LraK<float, ScalarLane>(X, Y, Z, _lra, i * G, G); break;
            }
    }

    private void Ftl()
    {
        if (!FollowTheLeader) return;
        for (var i = 1; i < Seg; i++)
            switch (Eff)
            {
                case HairSimdPath.V256: FtlK<Vector256<float>, V256Lane>(this, i * G, (i + 1) * G, i * G, FtlDamping); break;
                case HairSimdPath.V128: FtlK<Vector128<float>, V128Lane>(this, i * G, (i + 1) * G, i * G, FtlDamping); break;
                default: FtlK<float, ScalarLane>(this, i * G, (i + 1) * G, i * G, FtlDamping); break;
            }
    }

    /// <summary>충돌 없는 1 서브스텝(앵커 = 현재 자세 · 적분 + 제약) — 경로 비교 테스트용.</summary>
    public void SolveOnce(float h)
    {
        AnchorTo(HeadPos, HeadRot);
        Integrate(h);
        for (var it = 0; it < Iterations; it++) { Constraints(it == 0); Ftl(); }
    }

    // ══════════════════════ 프레임 스텝 ══════════════════════

    /// <summary>서브스텝(앵커 보간 → 적분 → 제약 + 충돌) → 평행 이송 프레임 → 자식 생성(무할당).</summary>
    public void Step(float dt, IStrandCollider? collider)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var ns = Math.Max(1, Substeps);
        var h = dt / ns;
        LastSubstepDt = h;
        for (var s = 0; s < ns; s++)
        {
            var a = (s + 1f) / ns;
            if (collider is not null) collider.Anchor(this, a);
            else AnchorTo(Vector3.Lerp(PrevHeadPos, HeadPos, a), Quaternion.Slerp(PrevHeadRot, HeadRot, a));
            Integrate(h);
            for (var it = 0; it < Iterations; it++)
            {
                Constraints(it == 0);
                Ftl();
                collider?.Collide(this, it, s);
            }
            if (collider is not null && s == ns - 1) { Ftl(); collider.Collide(this, Iterations, s); }   // 최종 길이 ↔ 충돌 재정합
        }
        Frames();
        var err = 0f; double sum = 0;
        for (var i = 0; i < Seg; i++)
            for (var g = 0; g < G; g++)
            {
                int a = i * G + g, b = a + G;
                float dx = X[b] - X[a], dy = Y[b] - Y[a], dz = Z[b] - Z[a];
                var r = MathF.Abs(MathF.Sqrt(dx * dx + dy * dy + dz * dz) / _len[a] - 1f);
                if (r > err) err = r;
                sum += r;
            }
        MaxStretchError = err;
        MeanStretchError = (float)(sum / (Seg * G));
        LastSolveUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        if (ChildrenEnabled) GenerateChildren();
        Steps++;
    }

    /// <summary>가이드 평행 이송 프레임(t · n · b) · 모근 기준 = 두피 측방 벡터의 머리 회전.</summary>
    public void Frames()
    {
        for (var g = 0; g < G; g++)
        {
            var reference = Vector3.Transform(_rootSide[g], SubRot);
            var n = reference;
            for (var i = 0; i < N; i++)
            {
                int k = i * G + g, a = (i < Seg ? i : Seg - 1) * G + g, b = a + G;
                var t = new Vector3(X[b] - X[a], Y[b] - Y[a], Z[b] - Z[a]);
                var tl = t.Length();
                t = tl > 1e-9f ? t / tl : Vector3.UnitY;
                n -= t * Vector3.Dot(t, n);
                var nl = n.Length();
                n = nl > 1e-6f ? n / nl : Vector3.Normalize(Vector3.Cross(t, MathF.Abs(t.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ));
                var bb = Vector3.Cross(t, n);
                Nx[k] = n.X; Ny[k] = n.Y; Nz[k] = n.Z; Bx[k] = bb.X; By[k] = bb.Y; Bz[k] = bb.Z;
            }
        }
    }

    /// <summary>자식 스트랜드 생성(가이드 × 입자 × 자식 레인 · 무할당).</summary>
    public void GenerateChildren()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var path = Eff;
        for (var g = 0; g < G; g++)
            for (var i = 0; i < N; i++)
                switch (path)
                {
                    case HairSimdPath.V256: ChildK<Vector256<float>, V256Lane>(this, g, i, 0, C); break;
                    case HairSimdPath.V128: ChildK<Vector128<float>, V128Lane>(this, g, i, 0, C); break;
                    default: ChildK<float, ScalarLane>(this, g, i, 0, C); break;
                }
        LastChildUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>상태 스냅샷/복원(경로 비교 테스트 · 9 × N·G).</summary>
    public void Snapshot(float[] dst)
    {
        var n = N * G;
        X.CopyTo(dst, 0); Y.CopyTo(dst, n); Z.CopyTo(dst, 2 * n); PX.CopyTo(dst, 3 * n); PY.CopyTo(dst, 4 * n); PZ.CopyTo(dst, 5 * n);
    }

    public void Restore(float[] src)
    {
        var n = N * G;
        Array.Copy(src, 0, X, 0, n); Array.Copy(src, n, Y, 0, n); Array.Copy(src, 2 * n, Z, 0, n);
        Array.Copy(src, 3 * n, PX, 0, n); Array.Copy(src, 4 * n, PY, 0, n); Array.Copy(src, 5 * n, PZ, 0, n);
    }

    /// <summary>모끝 클럼프 응집도(표본 자식 모끝 ↔ 클럼프 중심 평균 거리 · mm).</summary>
    public float ClumpTightnessMm()
    {
        double s = 0; var n = 0;
        var i = N - 1;
        for (var g = 0; g < G; g += 4)
        {
            var k = i * G + g;
            var gp = new Vector3(X[k], Y[k], Z[k]); var nn = new Vector3(Nx[k], Ny[k], Nz[k]); var bb = new Vector3(Bx[k], By[k], Bz[k]);
            for (var c = 0; c < C; c += 8)
            {
                var ci = g * C + c;
                var center = gp + nn * ChildCU[ci] + bb * ChildCV[ci];
                s += Vector3.Distance(Child(g, c, i), center); n++;
            }
        }
        return n > 0 ? (float)(s / n) * 1000f : 0f;
    }

    /// <summary>가이드 모끝 평균 위치(월드).</summary>
    public Vector3 MeanTip()
    {
        var s = Vector3.Zero;
        for (var g = 0; g < G; g++) s += P(N - 1, g);
        return s / G;
    }
}
