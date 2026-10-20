using System.Numerics;
namespace PromptFlowStudio.Services.SplatEnvironment;

/// <summary>스플랫 카메라(오른손 · 전방 −Z 규약 무관 — 기저 벡터로 정의).</summary>
public struct SplatCamera
{
    public Vector3 Position, Forward, Right, Up;
    public float VFovDeg, Aspect, Near, Far;

    public readonly float TanHalfV => MathF.Tan(VFovDeg * (MathF.PI / 360f));
    public readonly float TanHalfH => TanHalfV * Aspect;

    public static SplatCamera LookAt(Vector3 eye, Vector3 target, float vfovDeg, float aspect, float near = 0.1f, float far = 200f)
    {
        var f = Vector3.Normalize(target - eye);
        var r = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitY));
        var u = Vector3.Cross(r, f);
        return new SplatCamera { Position = eye, Forward = f, Right = r, Up = u, VFovDeg = vfovDeg, Aspect = aspect, Near = near, Far = far };
    }
}

/// <summary>
/// [PromptFlow Studio v17.55.0] 🌌 3D 가우시안 볼류메트릭 세트 장면(SoA · 사전 할당 · 수백만 스플랫).
/// 스플랫 = 위치 · 3D 공분산 6 성분(R·S²·Rᵀ) · 불투명도 · SH DC(float RGB) + 고차 15 계수 × 3(int8 · 스플랫별 스케일).
/// GPU 상주 추정은 표준 3DGS 레이아웃(위치 3 · 스케일 3 · 회전 4 · 불투명도 1 · SH 48 = 59 float = 236 B/스플랫)으로 계산한다.
/// <see cref="Generate"/>: 지면 45 % · 하늘 돔 10 % · 기둥 링 25 % · 광택 구체 8 %(정반사 로브를 차수 3 SH 로 투영) · 저불투명 부유 입자 12 %(≤ 0.02).
/// </summary>
public sealed class GaussianSplatScene
{
    public const int ShRest = 45, GpuBytesPerSplat = 59 * 4;
    public static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(-0.4f, 0.8f, 0.45f));
    public static readonly Vector3 SphereCenter = new(0f, 1.1f, 0f);
    public const float SphereRadius = 0.9f;

    public readonly int Capacity;
    public readonly float[] X, Y, Z, C00, C01, C02, C11, C12, C22, Opacity, Dr, Dg, Db, ShScale, MaxSigma;
    public readonly sbyte[] Sh;
    public readonly byte[] Kind;
    public int Count { get; private set; }
    public int Glossy { get; private set; }
    public int Floaters { get; private set; }

    public GaussianSplatScene(int capacity)
    {
        Capacity = Math.Max(capacity, 1);
        X = new float[Capacity]; Y = new float[Capacity]; Z = new float[Capacity];
        C00 = new float[Capacity]; C01 = new float[Capacity]; C02 = new float[Capacity]; C11 = new float[Capacity]; C12 = new float[Capacity]; C22 = new float[Capacity];
        Opacity = new float[Capacity]; Dr = new float[Capacity]; Dg = new float[Capacity]; Db = new float[Capacity]; ShScale = new float[Capacity]; MaxSigma = new float[Capacity];
        Sh = new sbyte[(long)Capacity * ShRest];
        Kind = new byte[Capacity];
    }

    /// <summary>호스트 버퍼 바이트(SoA 15 float + SH 45 B + 종류 1 B).</summary>
    public long HostBytes => (long)Capacity * (15 * 4 + ShRest + 1);

    public void Clear() => Count = 0;

    /// <summary>스플랫 추가(공분산 = R diag(s²) Rᵀ · R = 열 벡터 a/b/c) — 색은 선형 RGB(0..1), SH 고차 계수 없음.</summary>
    public int Add(Vector3 p, Vector3 a, Vector3 b, Vector3 c, Vector3 s, float opacity, Vector3 rgb, byte kind = 0)
    {
        if (Count >= Capacity) return -1;
        var i = Count++;
        Write(i, p, a, b, c, s, opacity, rgb, kind);
        return i;
    }

    /// <summary>v17.56 — 기존 슬롯 i 덮어쓰기(동적 스플랫 · 무할당 · SH 고차 계수 0).</summary>
    public void Write(int i, Vector3 p, Vector3 a, Vector3 b, Vector3 c, Vector3 s, float opacity, Vector3 rgb, byte kind)
    {
        X[i] = p.X; Y[i] = p.Y; Z[i] = p.Z;
        float s0 = s.X * s.X, s1 = s.Y * s.Y, s2 = s.Z * s.Z;
        C00[i] = a.X * a.X * s0 + b.X * b.X * s1 + c.X * c.X * s2;
        C01[i] = a.X * a.Y * s0 + b.X * b.Y * s1 + c.X * c.Y * s2;
        C02[i] = a.X * a.Z * s0 + b.X * b.Z * s1 + c.X * c.Z * s2;
        C11[i] = a.Y * a.Y * s0 + b.Y * b.Y * s1 + c.Y * c.Y * s2;
        C12[i] = a.Y * a.Z * s0 + b.Y * b.Z * s1 + c.Y * c.Z * s2;
        C22[i] = a.Z * a.Z * s0 + b.Z * b.Z * s1 + c.Z * c.Z * s2;
        MaxSigma[i] = MathF.Max(s.X, MathF.Max(s.Y, s.Z));
        Opacity[i] = opacity;
        Dr[i] = (rgb.X - 0.5f) / SphericalHarmonicsLightingSynthesizer.C0;
        Dg[i] = (rgb.Y - 0.5f) / SphericalHarmonicsLightingSynthesizer.C0;
        Db[i] = (rgb.Z - 0.5f) / SphericalHarmonicsLightingSynthesizer.C0;
        ShScale[i] = 0f;
        Sh.AsSpan(i * ShRest, ShRest).Clear();
        Kind[i] = kind;
    }

    /// <summary>v17.56 — 동적 슬롯 블록 예약(투명 · 불투명도 0 → 조기 컬링) · 시작 인덱스 반환(용량 부족 시 −1).</summary>
    public int Reserve(int k)
    {
        if (Count + k > Capacity) return -1;
        var start = Count;
        for (var j = 0; j < k; j++) Add(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(0.01f), 0f, Vector3.Zero, 6);
        return start;
    }

    /// <summary>고차 SH 계수(k = 1..15, 채널별 동일 스칼라 × 틴트) 양자화 기록.</summary>
    public void SetShRest(int i, ReadOnlySpan<float> coeff16, Vector3 tint)
    {
        var m = 0f;
        for (var k = 1; k < 16; k++) m = MathF.Max(m, MathF.Abs(coeff16[k]) * MathF.Max(tint.X, MathF.Max(tint.Y, tint.Z)));
        ShScale[i] = m;
        if (m <= 0f) return;
        var o = i * ShRest;
        for (var k = 1; k < 16; k++)
        {
            Sh[o + (k - 1) * 3] = (sbyte)Math.Clamp(MathF.Round(coeff16[k] * tint.X / m * 127f), -127, 127);
            Sh[o + (k - 1) * 3 + 1] = (sbyte)Math.Clamp(MathF.Round(coeff16[k] * tint.Y / m * 127f), -127, 127);
            Sh[o + (k - 1) * 3 + 2] = (sbyte)Math.Clamp(MathF.Round(coeff16[k] * tint.Z / m * 127f), -127, 127);
        }
    }

    private static uint Next(ref uint s) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s; }
    private static float U(ref uint s) => (Next(ref s) >> 8) * (1f / 16777216f);

    private static void Frame(Vector3 n, out Vector3 t, out Vector3 b)
    {
        t = Vector3.Normalize(Vector3.Cross(MathF.Abs(n.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX, n));
        b = Vector3.Cross(n, t);
    }

    /// <summary>절차적 볼류메트릭 세트 생성(비핫패스).</summary>
    /// <param name="centerpiece">false 면 중앙 광택 구체 대신 그만큼 지면을 늘린다(v17.56 배우 무대).</param>
    public void Generate(int n, uint seed = 0x9E3779B9u, bool centerpiece = true)
    {
        Clear(); Glossy = Floaters = 0;
        n = Math.Min(n, Capacity);
        var rng = seed == 0 ? 1u : seed;
        int nGround = n * 45 / 100, nSky = n * 10 / 100, nPillar = n * 25 / 100, nGloss = centerpiece ? n * 8 / 100 : 0;
        if (!centerpiece) nGround += n * 8 / 100;
        var nFloat = n - nGround - nSky - nPillar - nGloss;
        Span<float> coef = stackalloc float[16];
        for (var k = 0; k < nGround; k++)
        {
            var r = 30f * MathF.Sqrt(U(ref rng)); var a = MathF.Tau * U(ref rng);
            var p = new Vector3(r * MathF.Cos(a), 0f, r * MathF.Sin(a));
            var cell = ((int)MathF.Floor(p.X) + (int)MathF.Floor(p.Z)) & 1;
            var g = 0.85f + 0.3f * U(ref rng);
            var col = cell == 0 ? new Vector3(0.32f, 0.42f, 0.22f) * g : new Vector3(0.45f, 0.38f, 0.28f) * g;
            var sz = 0.03f + 0.004f * r;
            Add(p, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(sz, 0.006f, sz), 0.95f, col, 1);
        }
        for (var k = 0; k < nSky; k++)
        {
            var a = MathF.Tau * U(ref rng); var e = MathF.Asin(0.05f + 0.95f * U(ref rng));
            var d = new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Sin(e), MathF.Cos(e) * MathF.Sin(a));
            var col = Vector3.Lerp(new Vector3(0.75f, 0.82f, 0.9f), new Vector3(0.25f, 0.45f, 0.85f), MathF.Sqrt(MathF.Max(d.Y, 0f)));
            Add(d * 60f, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(1.6f), 0.95f, col, 2);
        }
        for (var k = 0; k < nPillar; k++)
        {
            var pi = (int)(Next(ref rng) % 12u);
            var a = pi * MathF.Tau / 12f;
            var c = new Vector3(10f * MathF.Cos(a), 0f, 10f * MathF.Sin(a));
            var ang = MathF.Tau * U(ref rng); var h = 6f * U(ref rng);
            var p = c + new Vector3(0.5f * MathF.Cos(ang), h, 0.5f * MathF.Sin(ang));
            var g = 0.55f + 0.15f * U(ref rng);
            Add(p, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(0.09f), 0.9f, new Vector3(g, g * 0.97f, g * 0.92f), 3);
        }
        for (var k = 0; k < nGloss; k++)
        {
            var z = 2f * U(ref rng) - 1f; var a = MathF.Tau * U(ref rng); var rr = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
            var nrm = new Vector3(rr * MathF.Cos(a), z, rr * MathF.Sin(a));
            Frame(nrm, out var t, out var b);
            var i = Add(SphereCenter + nrm * SphereRadius, t, b, nrm, new Vector3(0.05f, 0.05f, 0.006f), 0.95f, new Vector3(0.55f, 0.08f, 0.06f), 4);
            if (i < 0) break;
            // 정반사 로브: 광원 반사 방향 R 에서 보는 시점(dir = 카메라 → 스플랫 ≈ −R)일 때 하이라이트
            var refl = 2f * Vector3.Dot(nrm, LightDir) * nrm - LightDir;
            SphericalHarmonicsLightingSynthesizer.ProjectLobe(-refl, 8f, Vector3.Dot(nrm, LightDir) > 0f ? 1.4f : 0f, coef);
            SetShRest(i, coef, new Vector3(1f, 0.95f, 0.85f));
            Glossy++;
        }
        for (var k = 0; k < nFloat; k++)
        {
            var p = new Vector3(50f * U(ref rng) - 25f, 8f * U(ref rng), 50f * U(ref rng) - 25f);
            if (Add(p, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(0.05f), 0.002f + 0.018f * U(ref rng), new Vector3(0.9f), 5) >= 0) Floaters++;
        }
    }
}
