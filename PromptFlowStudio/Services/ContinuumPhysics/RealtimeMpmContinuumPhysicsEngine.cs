using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.ContinuumPhysics;

/// <summary>연속체 재질 프리셋.</summary>
public enum ContinuumMaterial : byte { Jelly = 0, Glass = 1, Clay = 2, Sand = 3, Metal = 4 }

/// <summary>재질 파라미터(무차원 ρ = 1 · 영역 1 m).</summary>
public static class ContinuumMaterials
{
    public const int Count = 5;
    public static readonly string[] Names = { "탄성 젤리", "파쇄 유리", "점토", "모래", "금속" };
    private static readonly float[] Es = { 400f, 1500f, 1000f, 600f, 1500f };
    private static readonly float[] Nus = { 0.3f, 0.2f, 0.3f, 0.3f, 0.3f };
    private static readonly float[] Yields = { 0f, 0f, 12f, 0f, 30f };

    public static float YoungModulus(ContinuumMaterial m) => Es[(int)m];
    public static float Poisson(ContinuumMaterial m) => Nus[(int)m];
    /// <summary>폰 미제스 항복 응력(Kirchhoff) — 점토/금속.</summary>
    public static float Yield(ContinuumMaterial m) => Yields[(int)m];
    public static float Hardening(ContinuumMaterial m) => m == ContinuumMaterial.Metal ? 8f : 0f;
    public static float FrictionAngleDeg => 30f;
    public static bool IsNeoHookean(ContinuumMaterial m) => m is ContinuumMaterial.Jelly or ContinuumMaterial.Glass;
    public static bool IsBrittle(ContinuumMaterial m) => m == ContinuumMaterial.Glass;
    public static void Lame(ContinuumMaterial m, out float mu, out float la)
    {
        float e = Es[(int)m], nu = Nus[(int)m];
        mu = e / (2f * (1f + nu));
        la = e * nu / ((1f + nu) * (1f - 2f * nu));
    }
    /// <summary>P파 속도 √((λ+2μ)/ρ) — CFL 기준.</summary>
    public static float WaveSpeed(ContinuumMaterial m) { Lame(m, out var mu, out var la); return MathF.Sqrt(la + 2f * mu); }
}

/// <summary>MPM SIMD 커널 — 모든 경로 public · 스칼라 쌍둥이와 비트 동일(채널 우선 SoA · stride = 용량).</summary>
public static class MpmKernels
{
    /// <summary>J = det F.</summary>
    public static void DeterminantScalar(ReadOnlySpan<float> f, int stride, int n, Span<float> j)
    {
        for (var i = 0; i < n; i++)
        {
            float a = f[i], b = f[stride + i], c = f[2 * stride + i], d = f[3 * stride + i], e = f[4 * stride + i], g = f[5 * stride + i], h = f[6 * stride + i], k = f[7 * stride + i], l = f[8 * stride + i];
            j[i] = a * (e * l - g * k) - b * (d * l - g * h) + c * (d * k - e * h);
        }
    }

    public static void DeterminantSimd(ReadOnlySpan<float> f, int stride, int n, Span<float> j)
    {
        var i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && n >= w)
        {
            ref var rf = ref MemoryMarshal.GetReference(f);
            ref var rj = ref MemoryMarshal.GetReference(j);
            for (; i <= n - w; i += w)
            {
                Vector<float> a = L(ref rf, i), b = L(ref rf, stride + i), c = L(ref rf, 2 * stride + i), d = L(ref rf, 3 * stride + i), e = L(ref rf, 4 * stride + i);
                Vector<float> g = L(ref rf, 5 * stride + i), h = L(ref rf, 6 * stride + i), k = L(ref rf, 7 * stride + i), l = L(ref rf, 8 * stride + i);
                S(ref rj, i, a * (e * l - g * k) - b * (d * l - g * h) + c * (d * k - e * h));
            }
        }
        DeterminantScalar(f.Slice(i), stride, n - i, j.Slice(i));
    }

    /// <summary>신-후크 Kirchhoff 응력 τ = μ(FFᵀ − I) + λ lnJ I (6 채널) · 변형 에너지 밀도 ψ = μ/2(tr b − 3) − μ lnJ + λ/2 lnJ².</summary>
    public static void NeoHookeanScalar(ReadOnlySpan<float> f, int stride, ReadOnlySpan<float> mu, ReadOnlySpan<float> la, ReadOnlySpan<float> lnJ, int n, Span<float> tau, Span<float> psi)
    {
        for (var i = 0; i < n; i++)
        {
            float f0 = f[i], f1 = f[stride + i], f2 = f[2 * stride + i], f3 = f[3 * stride + i], f4 = f[4 * stride + i], f5 = f[5 * stride + i], f6 = f[6 * stride + i], f7 = f[7 * stride + i], f8 = f[8 * stride + i];
            float m = mu[i], l = la[i], lj = lnJ[i];
            var b00 = f0 * f0 + f1 * f1 + f2 * f2;
            var b11 = f3 * f3 + f4 * f4 + f5 * f5;
            var b22 = f6 * f6 + f7 * f7 + f8 * f8;
            var p = l * lj;
            tau[i] = m * (b00 - 1f) + p;
            tau[stride + i] = m * (f0 * f3 + f1 * f4 + f2 * f5);
            tau[2 * stride + i] = m * (f0 * f6 + f1 * f7 + f2 * f8);
            tau[3 * stride + i] = m * (b11 - 1f) + p;
            tau[4 * stride + i] = m * (f3 * f6 + f4 * f7 + f5 * f8);
            tau[5 * stride + i] = m * (b22 - 1f) + p;
            psi[i] = 0.5f * m * (b00 + b11 + b22 - 3f) - m * lj + 0.5f * l * lj * lj;
        }
    }

    public static void NeoHookeanSimd(ReadOnlySpan<float> f, int stride, ReadOnlySpan<float> mu, ReadOnlySpan<float> la, ReadOnlySpan<float> lnJ, int n, Span<float> tau, Span<float> psi)
    {
        var i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && n >= w)
        {
            ref var rf = ref MemoryMarshal.GetReference(f);
            ref var rm = ref MemoryMarshal.GetReference(mu);
            ref var rl = ref MemoryMarshal.GetReference(la);
            ref var rj = ref MemoryMarshal.GetReference(lnJ);
            ref var rt = ref MemoryMarshal.GetReference(tau);
            ref var rp = ref MemoryMarshal.GetReference(psi);
            var one = Vector<float>.One;
            var three = new Vector<float>(3f);
            var half = new Vector<float>(0.5f);
            for (; i <= n - w; i += w)
            {
                Vector<float> f0 = L(ref rf, i), f1 = L(ref rf, stride + i), f2 = L(ref rf, 2 * stride + i), f3 = L(ref rf, 3 * stride + i), f4 = L(ref rf, 4 * stride + i);
                Vector<float> f5 = L(ref rf, 5 * stride + i), f6 = L(ref rf, 6 * stride + i), f7 = L(ref rf, 7 * stride + i), f8 = L(ref rf, 8 * stride + i);
                Vector<float> m = L(ref rm, i), l = L(ref rl, i), lj = L(ref rj, i);
                var b00 = f0 * f0 + f1 * f1 + f2 * f2;
                var b11 = f3 * f3 + f4 * f4 + f5 * f5;
                var b22 = f6 * f6 + f7 * f7 + f8 * f8;
                var p = l * lj;
                S(ref rt, i, m * (b00 - one) + p);
                S(ref rt, stride + i, m * (f0 * f3 + f1 * f4 + f2 * f5));
                S(ref rt, 2 * stride + i, m * (f0 * f6 + f1 * f7 + f2 * f8));
                S(ref rt, 3 * stride + i, m * (b11 - one) + p);
                S(ref rt, 4 * stride + i, m * (f3 * f6 + f4 * f7 + f5 * f8));
                S(ref rt, 5 * stride + i, m * (b22 - one) + p);
                S(ref rp, i, half * m * (b00 + b11 + b22 - three) - m * lj + half * l * lj * lj);
            }
        }
        NeoHookeanScalar(f.Slice(i), stride, mu.Slice(i), la.Slice(i), lnJ.Slice(i), n - i, tau.Slice(i), psi.Slice(i));
    }

    /// <summary>격자 정규화: m &gt; 0 이면 v = p/m + g·Δt(y) · 아니면 0.</summary>
    public static void GridNormalizeScalar(ReadOnlySpan<float> m, Span<float> x, Span<float> y, Span<float> z, float gdt)
    {
        for (var i = 0; i < m.Length; i++)
        {
            if (m[i] > 0f) { var inv = 1f / m[i]; x[i] = x[i] * inv; y[i] = y[i] * inv + gdt; z[i] = z[i] * inv; }
            else { x[i] = 0f; y[i] = 0f; z[i] = 0f; }
        }
    }

    public static void GridNormalizeSimd(ReadOnlySpan<float> m, Span<float> x, Span<float> y, Span<float> z, float gdt)
    {
        var i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && m.Length >= w)
        {
            ref var rm = ref MemoryMarshal.GetReference(m);
            ref var rx = ref MemoryMarshal.GetReference(x);
            ref var ry = ref MemoryMarshal.GetReference(y);
            ref var rz = ref MemoryMarshal.GetReference(z);
            var g = new Vector<float>(gdt);
            var one = Vector<float>.One;
            for (; i <= m.Length - w; i += w)
            {
                var mm = L(ref rm, i);
                var mask = Vector.GreaterThan(mm, Vector<float>.Zero);
                var inv = one / Vector.ConditionalSelect(mask, mm, one);
                S(ref rx, i, Vector.ConditionalSelect(mask, L(ref rx, i) * inv, Vector<float>.Zero));
                S(ref ry, i, Vector.ConditionalSelect(mask, L(ref ry, i) * inv + g, Vector<float>.Zero));
                S(ref rz, i, Vector.ConditionalSelect(mask, L(ref rz, i) * inv, Vector<float>.Zero));
            }
        }
        GridNormalizeScalar(m.Slice(i), x.Slice(i), y.Slice(i), z.Slice(i), gdt);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> L(ref float r, int i) => Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref r, i)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void S(ref float r, int i, Vector<float> v) => Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref r, i)), v);
}

/// <summary>
/// [PromptFlow Studio v17.26.0] ⚛ 실시간 물질점(MPM) 연속체 물리 &amp; 점탄소성 엔진(<see cref="Step"/> 무할당).
///   ▸ MLS-MPM/APIC: 2차 B-스플라인 27 노드 P2G(질량 · 운동량 + 아핀 C · 응력 −Δt·V·4/Δx²·τ) → 격자 정규화/중력(SIMD) → 경계 · SDF 충돌(가드) → G2P(v · C · F ← (I + Δt·C)F · 이류).
///   ▸ 탄성: 신-후크(젤리 · 유리) τ = μ(FFᵀ − I) + λ lnJ I — det/τ/ψ 를 Vector&lt;float&gt; 일괄(lnJ 만 스칼라).
///   ▸ 소성: b = FFᵀ 야코비 고유분해 → 헨키 변형 ε = ½ ln λ → 드루커-프라거(모래 · 30°) / 폰 미제스(점토 · 금속 경화) 반환 사상 → F ← Q diag(e^ε′/e^ε) Qᵀ F.
///   ▸ 24³ 격자 · 입자 간격 Δx/2 · 하위 단계마다 가드가 콜라이더 자세 · CFL Δt · 입자 비침투 투영을 결정.
/// </summary>
public sealed class RealtimeMpmContinuumPhysicsEngine
{
    public const int N = 24, Nodes = N * N * N, Bound = 3, Capacity = 4096, MaxTracers = 1024;
    public const float Dx = 1f / N, InvDx = N, PVol = Dx * 0.5f * Dx * 0.5f * Dx * 0.5f, PMass = PVol, Gravity = -9.8f, Floor = Bound * Dx, FloorFriction = 0.5f;
    public static readonly Vector3 BlockMin = new(0.38f, Floor, 0.38f), BlockMax = new(0.62f, Floor + 0.16f, 0.62f);

    private readonly float[] _x = new float[Capacity], _y = new float[Capacity], _z = new float[Capacity];
    private readonly float[] _vx = new float[Capacity], _vy = new float[Capacity], _vz = new float[Capacity];
    private readonly float[] _c = new float[9 * Capacity], _f = new float[9 * Capacity], _tau = new float[6 * Capacity];
    private readonly float[] _mu = new float[Capacity], _la = new float[Capacity], _j = new float[Capacity], _lnJ = new float[Capacity], _psi = new float[Capacity];
    private readonly float[] _plastic = new float[Capacity], _damage = new float[Capacity];
    private readonly float[] _gm = new float[Nodes], _gx = new float[Nodes], _gy = new float[Nodes], _gz = new float[Nodes];
    private float[]? _tx, _ty, _tz;
    private int _tn;
    private int _n;
    private float _mu0, _la0;

    public RealtimeMpmContinuumPhysicsEngine(NonPenetrationEnergyConservationPhysicsGuard guard)
    {
        Guard = guard;
        Reset(ContinuumMaterial.Jelly);
    }

    public NonPenetrationEnergyConservationPhysicsGuard Guard { get; }
    public ContinuumMaterial Material { get; private set; }
    public int Count => _n;
    public bool UseSimd { get; set; } = true;
    public double SimTime { get; private set; }
    public long Substeps { get; private set; }
    public int LastSubsteps { get; private set; }
    public float LastDt { get; private set; }
    public double LastStepMs { get; private set; }
    public float MaxSpeed { get; private set; }
    public float KineticEnergy { get; private set; }
    public float PotentialEnergy { get; private set; }
    public float ElasticEnergy { get; private set; }
    public float TotalEnergy => KineticEnergy + PotentialEnergy + ElasticEnergy;
    public Vector3 Position(int i) => new(_x[i], _y[i], _z[i]);
    public Vector3 Velocity(int i) => new(_vx[i], _vy[i], _vz[i]);
    public float Determinant(int i) => _j[i];
    public float StrainEnergy(int i) => _psi[i];
    public float PlasticStrain(int i) => _plastic[i];
    public float Damage(int i) => _damage[i];
    public float Tau(int i, int comp) => _tau[comp * Capacity + i];
    public float DeformationGradient(int i, int comp) => _f[comp * Capacity + i];
    /// <summary>폰 미제스 등가 응력 √(3/2 ‖dev τ‖²).</summary>
    public float VonMises(int i)
    {
        float t00 = _tau[i], t01 = _tau[Capacity + i], t02 = _tau[2 * Capacity + i], t11 = _tau[3 * Capacity + i], t12 = _tau[4 * Capacity + i], t22 = _tau[5 * Capacity + i];
        var p = (t00 + t11 + t22) / 3f;
        float d0 = t00 - p, d1 = t11 - p, d2 = t22 - p;
        return MathF.Sqrt(1.5f * (d0 * d0 + d1 * d1 + d2 * d2 + 2f * (t01 * t01 + t02 * t02 + t12 * t12)));
    }
    public float GridMass(int i, int j, int k) => _gm[(i * N + j) * N + k];
    public float MeanPlasticStrain { get; private set; }
    /// <summary>비유한 값으로 발산해 복구된 입자 수(누적).</summary>
    public long Diverged { get; private set; }

    /// <summary>표면 메쉬 등 추적점(격자 속도로 이류) 등록 — 합성기 소유 배열.</summary>
    public void RegisterTracers(float[] x, float[] y, float[] z, int count) { _tx = x; _ty = y; _tz = z; _tn = Math.Min(count, MaxTracers); }

    /// <summary>재질 블록(0.24 × 0.16 × 0.24) 재생성 · 입자 간격 Δx/2 · 결정적 지터.</summary>
    public void Reset(ContinuumMaterial m)
    {
        Material = m;
        ContinuumMaterials.Lame(m, out _mu0, out _la0);
        var sp = Dx * 0.5f;
        var rng = 0x6A09E667u;
        _n = 0;
        for (var x = BlockMin.X + sp * 0.5f; x < BlockMax.X && _n < Capacity; x += sp)
            for (var y = BlockMin.Y + sp * 0.5f; y < BlockMax.Y && _n < Capacity; y += sp)
                for (var z = BlockMin.Z + sp * 0.5f; z < BlockMax.Z && _n < Capacity; z += sp)
                {
                    _x[_n] = x + Jit(ref rng) * sp; _y[_n] = y + Jit(ref rng) * sp; _z[_n] = z + Jit(ref rng) * sp;
                    _n++;
                }
        Array.Clear(_vx); Array.Clear(_vy); Array.Clear(_vz); Array.Clear(_c); Array.Clear(_f); Array.Clear(_tau);
        Array.Clear(_psi); Array.Clear(_plastic); Array.Clear(_damage);
        for (var i = 0; i < _n; i++)
        {
            _f[i] = 1f; _f[4 * Capacity + i] = 1f; _f[8 * Capacity + i] = 1f;
            _mu[i] = _mu0; _la[i] = _la0; _j[i] = 1f; _lnJ[i] = 0f;
        }
        SimTime = 0; Substeps = 0; MaxSpeed = 0f; MeanPlasticStrain = 0f; Diverged = 0;
        Guard.OnReset(this);
        MeasureEnergy();
    }

    private static float Jit(ref uint r)
    {
        r ^= r << 13; r ^= r >> 17; r ^= r << 5;
        return ((r >> 8) * (1f / 16777216f) - 0.5f) * 0.3f;
    }

    /// <summary>손상 d 반영(유리 균열면 → 응력 0).</summary>
    public void SetDamage(int i, float d)
    {
        d = Math.Clamp(d, 0f, 1f);
        _damage[i] = d;
        _mu[i] = _mu0 * (1f - d);
        _la[i] = _la0 * (1f - d);
    }

    /// <summary>1 프레임: 가드가 정한 하위 단계 수 × (콜라이더 갱신 → 응력 → P2G → 격자 → G2P).</summary>
    public void Step(float frameSimSeconds)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var steps = Guard.PlanSubsteps(this, frameSimSeconds, out var dt);
        for (var s = 0; s < steps; s++)
        {
            SimTime += dt;
            Guard.UpdateColliders(SimTime, dt);
            ComputeStress();
            P2G(dt);
            UpdateGrid(dt);
            G2P(dt);
            Substeps++;
        }
        LastSubsteps = steps;
        LastDt = dt;
        MeasureEnergy();
        LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void ComputeStress()
    {
        var n = _n;
        if (UseSimd) MpmKernels.DeterminantSimd(_f, Capacity, n, _j);
        else MpmKernels.DeterminantScalar(_f, Capacity, n, _j);
        for (var i = 0; i < n; i++) _lnJ[i] = MathF.Log(MathF.Max(_j[i], 0.05f));
        if (ContinuumMaterials.IsNeoHookean(Material))
        {
            if (UseSimd) MpmKernels.NeoHookeanSimd(_f, Capacity, _mu, _la, _lnJ, n, _tau, _psi);
            else MpmKernels.NeoHookeanScalar(_f, Capacity, _mu, _la, _lnJ, n, _tau, _psi);
            return;
        }
        var sand = Material == ContinuumMaterial.Sand;
        var yield = ContinuumMaterials.Yield(Material);
        var hard = ContinuumMaterials.Hardening(Material);
        var sinPhi = MathF.Sin(ContinuumMaterials.FrictionAngleDeg * (MathF.PI / 180f));
        var alpha = MathF.Sqrt(2f / 3f) * 2f * sinPhi / (3f - sinPhi);
        double pl = 0;
        for (var i = 0; i < n; i++)
        {
            HenckyReturnMap(i, sand, yield * (1f + hard * _plastic[i]), alpha);
            pl += _plastic[i];
        }
        MeanPlasticStrain = n > 0 ? (float)(pl / n) : 0f;
    }

    private void HenckyReturnMap(int i, bool sand, float yield, float alpha)
    {
        const int S = Capacity;
        float f0 = _f[i], f1 = _f[S + i], f2 = _f[2 * S + i], f3 = _f[3 * S + i], f4 = _f[4 * S + i], f5 = _f[5 * S + i], f6 = _f[6 * S + i], f7 = _f[7 * S + i], f8 = _f[8 * S + i];
        var b = new Sym3(f0 * f0 + f1 * f1 + f2 * f2, f0 * f3 + f1 * f4 + f2 * f5, f0 * f6 + f1 * f7 + f2 * f8, f3 * f3 + f4 * f4 + f5 * f5, f3 * f6 + f4 * f7 + f5 * f8, f6 * f6 + f7 * f7 + f8 * f8);
        Sym3.Eigen(b, out var l0, out var l1, out var l2, out var q);
        float e0 = 0.5f * MathF.Log(MathF.Max(l0, 1e-8f)), e1 = 0.5f * MathF.Log(MathF.Max(l1, 1e-8f)), e2 = 0.5f * MathF.Log(MathF.Max(l2, 1e-8f));
        float n0 = e0, n1 = e1, n2 = e2;
        float mu = _mu[i], la = _la[i];
        var tr = e0 + e1 + e2;
        float d0 = e0 - tr / 3f, d1 = e1 - tr / 3f, d2 = e2 - tr / 3f;
        var dn = MathF.Sqrt(d0 * d0 + d1 * d1 + d2 * d2);
        if (sand)
        {
            if (tr >= 0f) { _plastic[i] += MathF.Sqrt(e0 * e0 + e1 * e1 + e2 * e2); n0 = n1 = n2 = 0f; }
            else if (dn > 1e-10f)
            {
                var dg = dn + (3f * la + 2f * mu) / (2f * mu) * tr * alpha;
                if (dg > 0f) { var k = dg / dn; n0 -= k * d0; n1 -= k * d1; n2 -= k * d2; _plastic[i] += dg; }
            }
        }
        else
        {
            var r = yield / (2f * mu);
            if (dn > r && dn > 1e-10f)
            {
                var k = r / dn;
                n0 = tr / 3f + d0 * k; n1 = tr / 3f + d1 * k; n2 = tr / 3f + d2 * k;
                _plastic[i] += dn - r;
            }
        }
        if (n0 != e0 || n1 != e1 || n2 != e2)
        {
            // F ← Q diag(e^(ε′−ε)) Qᵀ F
            var m = new Sym3(q, MathF.Exp(n0 - e0), MathF.Exp(n1 - e1), MathF.Exp(n2 - e2));
            _f[i] = m.A00 * f0 + m.A01 * f3 + m.A02 * f6; _f[S + i] = m.A00 * f1 + m.A01 * f4 + m.A02 * f7; _f[2 * S + i] = m.A00 * f2 + m.A01 * f5 + m.A02 * f8;
            _f[3 * S + i] = m.A01 * f0 + m.A11 * f3 + m.A12 * f6; _f[4 * S + i] = m.A01 * f1 + m.A11 * f4 + m.A12 * f7; _f[5 * S + i] = m.A01 * f2 + m.A11 * f5 + m.A12 * f8;
            _f[6 * S + i] = m.A02 * f0 + m.A12 * f3 + m.A22 * f6; _f[7 * S + i] = m.A02 * f1 + m.A12 * f4 + m.A22 * f7; _f[8 * S + i] = m.A02 * f2 + m.A12 * f5 + m.A22 * f8;
        }
        var trn = n0 + n1 + n2;
        var t = new Sym3(q, 2f * mu * n0 + la * trn, 2f * mu * n1 + la * trn, 2f * mu * n2 + la * trn);
        _tau[i] = t.A00; _tau[S + i] = t.A01; _tau[2 * S + i] = t.A02; _tau[3 * S + i] = t.A11; _tau[4 * S + i] = t.A12; _tau[5 * S + i] = t.A22;
        _psi[i] = mu * (n0 * n0 + n1 * n1 + n2 * n2) + 0.5f * la * trn * trn;
        _j[i] = MathF.Exp(trn);
    }

    private void P2G(float dt)
    {
        const int S = Capacity;
        Array.Clear(_gm); Array.Clear(_gx); Array.Clear(_gy); Array.Clear(_gz);
        var k0 = -dt * PVol * 4f * InvDx * InvDx;
        Span<float> wx = stackalloc float[3];
        Span<float> wy = stackalloc float[3];
        Span<float> wz = stackalloc float[3];
        ref var rm = ref MemoryMarshal.GetArrayDataReference(_gm);
        ref var rx = ref MemoryMarshal.GetArrayDataReference(_gx);
        ref var ry = ref MemoryMarshal.GetArrayDataReference(_gy);
        ref var rz = ref MemoryMarshal.GetArrayDataReference(_gz);
        for (var p = 0; p < _n; p++)
        {
            Weights(_x[p], wx, out var bx, out var fx);
            Weights(_y[p], wy, out var by, out var fy);
            Weights(_z[p], wz, out var bz, out var fz);
            float t00 = _tau[p], t01 = _tau[S + p], t02 = _tau[2 * S + p], t11 = _tau[3 * S + p], t12 = _tau[4 * S + p], t22 = _tau[5 * S + p];
            var a00 = k0 * t00 + PMass * _c[p]; var a01 = k0 * t01 + PMass * _c[S + p]; var a02 = k0 * t02 + PMass * _c[2 * S + p];
            var a10 = k0 * t01 + PMass * _c[3 * S + p]; var a11 = k0 * t11 + PMass * _c[4 * S + p]; var a12 = k0 * t12 + PMass * _c[5 * S + p];
            var a20 = k0 * t02 + PMass * _c[6 * S + p]; var a21 = k0 * t12 + PMass * _c[7 * S + p]; var a22 = k0 * t22 + PMass * _c[8 * S + p];
            float mvx = PMass * _vx[p], mvy = PMass * _vy[p], mvz = PMass * _vz[p];
            for (var i = 0; i < 3; i++)
            {
                var dpx = (i - fx) * Dx;
                for (var j = 0; j < 3; j++)
                {
                    var dpy = (j - fy) * Dx;
                    var wij = wx[i] * wy[j];
                    var row = ((bx + i) * N + by + j) * N + bz;
                    for (var k = 0; k < 3; k++)
                    {
                        var dpz = (k - fz) * Dx;
                        var w = wij * wz[k];
                        var g = row + k;   // Weights 가 b ∈ [0, N−3] 보장 → 범위 검사 생략
                        Unsafe.Add(ref rm, g) += w * PMass;
                        Unsafe.Add(ref rx, g) += w * (mvx + a00 * dpx + a01 * dpy + a02 * dpz);
                        Unsafe.Add(ref ry, g) += w * (mvy + a10 * dpx + a11 * dpy + a12 * dpz);
                        Unsafe.Add(ref rz, g) += w * (mvz + a20 * dpx + a21 * dpy + a22 * dpz);
                    }
                }
            }
        }
    }

    private void UpdateGrid(float dt)
    {
        if (UseSimd) MpmKernels.GridNormalizeSimd(_gm, _gx, _gy, _gz, Gravity * dt);
        else MpmKernels.GridNormalizeScalar(_gm, _gx, _gy, _gz, Gravity * dt);
        // 영역 경계: 바닥(마찰) · 벽(법선 성분 차단)
        for (var i = 0; i < N; i++)
            for (var j = 0; j < N; j++)
                for (var k = 0; k < N; k++)
                {
                    var bi = i < Bound || i >= N - Bound;
                    var bj = j < Bound || j >= N - Bound;
                    var bk = k < Bound || k >= N - Bound;
                    if (!bi && !bj && !bk) { k = N - Bound - 1; continue; }
                    var g = (i * N + j) * N + k;
                    if (_gm[g] <= 0f) continue;
                    if (i < Bound && _gx[g] < 0f) _gx[g] = 0f;
                    if (i >= N - Bound && _gx[g] > 0f) _gx[g] = 0f;
                    if (k < Bound && _gz[g] < 0f) _gz[g] = 0f;
                    if (k >= N - Bound && _gz[g] > 0f) _gz[g] = 0f;
                    if (j >= N - Bound && _gy[g] > 0f) _gy[g] = 0f;
                    if (j < Bound && _gy[g] < 0f)
                    {
                        var vn = -_gy[g];
                        _gy[g] = 0f;
                        var vt = MathF.Sqrt(_gx[g] * _gx[g] + _gz[g] * _gz[g]);
                        var s = vt > FloorFriction * vn ? 1f - FloorFriction * vn / vt : 0f;
                        _gx[g] *= s; _gz[g] *= s;
                    }
                }
        Guard.ApplyGridColliders(_gm, _gx, _gy, _gz, dt);
    }

    private void G2P(float dt)
    {
        const int S = Capacity;
        Span<float> wx = stackalloc float[3];
        Span<float> wy = stackalloc float[3];
        Span<float> wz = stackalloc float[3];
        var lo = Dx * 1.01f;
        var hi = 1f - Dx * 1.01f;
        var c4 = 4f * InvDx * InvDx;
        var vmax = 0f;
        ref var rx = ref MemoryMarshal.GetArrayDataReference(_gx);
        ref var ry = ref MemoryMarshal.GetArrayDataReference(_gy);
        ref var rz = ref MemoryMarshal.GetArrayDataReference(_gz);
        for (var p = 0; p < _n; p++)
        {
            Weights(_x[p], wx, out var bx, out var fx);
            Weights(_y[p], wy, out var by, out var fy);
            Weights(_z[p], wz, out var bz, out var fz);
            float vx = 0, vy = 0, vz = 0, c00 = 0, c01 = 0, c02 = 0, c10 = 0, c11 = 0, c12 = 0, c20 = 0, c21 = 0, c22 = 0;
            for (var i = 0; i < 3; i++)
            {
                var dpx = (i - fx) * Dx;
                for (var j = 0; j < 3; j++)
                {
                    var dpy = (j - fy) * Dx;
                    var wij = wx[i] * wy[j];
                    var row = ((bx + i) * N + by + j) * N + bz;
                    for (var k = 0; k < 3; k++)
                    {
                        var dpz = (k - fz) * Dx;
                        var w = wij * wz[k];
                        var g = row + k;
                        float gx = Unsafe.Add(ref rx, g) * w, gy = Unsafe.Add(ref ry, g) * w, gz = Unsafe.Add(ref rz, g) * w;
                        vx += gx; vy += gy; vz += gz;
                        c00 += gx * dpx; c01 += gx * dpy; c02 += gx * dpz;
                        c10 += gy * dpx; c11 += gy * dpy; c12 += gy * dpz;
                        c20 += gz * dpx; c21 += gz * dpy; c22 += gz * dpz;
                    }
                }
            }
            Guard.LimitVelocity(ref vx, ref vy, ref vz, dt);
            _vx[p] = vx; _vy[p] = vy; _vz[p] = vz;
            c00 *= c4; c01 *= c4; c02 *= c4; c10 *= c4; c11 *= c4; c12 *= c4; c20 *= c4; c21 *= c4; c22 *= c4;
            _c[p] = c00; _c[S + p] = c01; _c[2 * S + p] = c02; _c[3 * S + p] = c10; _c[4 * S + p] = c11; _c[5 * S + p] = c12; _c[6 * S + p] = c20; _c[7 * S + p] = c21; _c[8 * S + p] = c22;
            // F ← (I + Δt·C) F
            float f0 = _f[p], f1 = _f[S + p], f2 = _f[2 * S + p], f3 = _f[3 * S + p], f4 = _f[4 * S + p], f5 = _f[5 * S + p], f6 = _f[6 * S + p], f7 = _f[7 * S + p], f8 = _f[8 * S + p];
            float m00 = 1f + dt * c00, m01 = dt * c01, m02 = dt * c02, m10 = dt * c10, m11 = 1f + dt * c11, m12 = dt * c12, m20 = dt * c20, m21 = dt * c21, m22 = 1f + dt * c22;
            _f[p] = m00 * f0 + m01 * f3 + m02 * f6; _f[S + p] = m00 * f1 + m01 * f4 + m02 * f7; _f[2 * S + p] = m00 * f2 + m01 * f5 + m02 * f8;
            _f[3 * S + p] = m10 * f0 + m11 * f3 + m12 * f6; _f[4 * S + p] = m10 * f1 + m11 * f4 + m12 * f7; _f[5 * S + p] = m10 * f2 + m11 * f5 + m12 * f8;
            _f[6 * S + p] = m20 * f0 + m21 * f3 + m22 * f6; _f[7 * S + p] = m20 * f1 + m21 * f4 + m22 * f7; _f[8 * S + p] = m20 * f2 + m21 * f5 + m22 * f8;
            float x = _x[p] + dt * vx, y = _y[p] + dt * vy, z = _z[p] + dt * vz;
            Guard.ProjectParticle(ref x, ref y, ref z, ref _vx[p], ref _vy[p], ref _vz[p]);
            // 영역 안전(NaN 포함) — 인덱스 범위 보장
            _x[p] = x >= lo ? (x <= hi ? x : hi) : lo;
            _y[p] = y >= lo ? (y <= hi ? y : hi) : lo;
            _z[p] = z >= lo ? (z <= hi ? z : hi) : lo;
            if (!float.IsFinite(_vx[p] + _vy[p] + _vz[p] + c00 + c11 + c22 + _f[p] + _f[4 * S + p] + _f[8 * S + p]))
            {
                // 수치 발산 입자 복구(가드 OFF 의 명시적 적분 폭주 · 인덱스/NaN 전파 차단)
                _vx[p] = _vy[p] = _vz[p] = 0f;
                for (var q = 0; q < 9; q++) { _c[q * S + p] = 0f; _f[q * S + p] = q % 4 == 0 ? 1f : 0f; }
                Diverged++;
            }
            var sp = _vx[p] * _vx[p] + _vy[p] * _vy[p] + _vz[p] * _vz[p];
            if (sp > vmax) vmax = sp;
        }
        MaxSpeed = MathF.Sqrt(vmax);
        // 추적점(표면 메쉬) 이류
        if (_tx is null || _ty is null || _tz is null) return;
        for (var t = 0; t < _tn; t++)
        {
            var v = SampleVelocity(_tx[t], _ty[t], _tz[t], wx, wy, wz);
            _tx[t] = Math.Clamp(_tx[t] + dt * v.X, lo, hi);
            _ty[t] = Math.Clamp(_ty[t] + dt * v.Y, lo, hi);
            _tz[t] = Math.Clamp(_tz[t] + dt * v.Z, lo, hi);
        }
    }

    private Vector3 SampleVelocity(float x, float y, float z, Span<float> wx, Span<float> wy, Span<float> wz)
    {
        Weights(x, wx, out var bx, out _);
        Weights(y, wy, out var by, out _);
        Weights(z, wz, out var bz, out _);
        float vx = 0, vy = 0, vz = 0;
        for (var i = 0; i < 3; i++)
            for (var j = 0; j < 3; j++)
            {
                var row = ((bx + i) * N + by + j) * N + bz;
                for (var k = 0; k < 3; k++)
                {
                    var w = wx[i] * wy[j] * wz[k];
                    vx += w * _gx[row + k]; vy += w * _gy[row + k]; vz += w * _gz[row + k];
                }
            }
        return new Vector3(vx, vy, vz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Weights(float x, Span<float> w, out int b, out float f)
    {
        var xs = x * InvDx;
        b = Math.Clamp((int)(xs - 0.5f), 0, N - 3);
        f = xs - b;
        var a = 1.5f - f; var c = f - 1f; var d = f - 0.5f;
        w[0] = 0.5f * a * a; w[1] = 0.75f - c * c; w[2] = 0.5f * d * d;
    }

    /// <summary>총 에너지: 운동 ½mv² · 중력 위치 m·g·(y − 바닥) · 탄성 V·ψ.</summary>
    public void MeasureEnergy()
    {
        double ke = 0, pe = 0, ee = 0;
        for (var i = 0; i < _n; i++)
        {
            ke += 0.5 * PMass * (_vx[i] * _vx[i] + _vy[i] * _vy[i] + _vz[i] * _vz[i]);
            pe += PMass * -Gravity * (_y[i] - Floor);
            ee += PVol * _psi[i];
        }
        KineticEnergy = (float)ke; PotentialEnergy = (float)pe; ElasticEnergy = (float)ee;
    }

    /// <summary>해밀토니안 클램프: 입자 속도 · 아핀 C 를 s 배.</summary>
    public void ScaleVelocities(float s)
    {
        for (var i = 0; i < _n; i++) { _vx[i] *= s; _vy[i] *= s; _vz[i] *= s; }
        for (var c = 0; c < 9; c++)
        {
            var span = _c.AsSpan(c * Capacity, _n);
            for (var i = 0; i < span.Length; i++) span[i] *= s;
        }
        KineticEnergy *= s * s;
    }
}

/// <summary>대칭 3×3(무할당 값 형식) · 야코비 고유분해.</summary>
public readonly struct Sym3
{
    public readonly float A00, A01, A02, A11, A12, A22;

    public Sym3(float a00, float a01, float a02, float a11, float a12, float a22) { A00 = a00; A01 = a01; A02 = a02; A11 = a11; A12 = a12; A22 = a22; }

    /// <summary>Q diag(d) Qᵀ (Q 열 = 고유벡터).</summary>
    public Sym3(in Basis3 q, float d0, float d1, float d2)
    {
        A00 = q.Q00 * q.Q00 * d0 + q.Q01 * q.Q01 * d1 + q.Q02 * q.Q02 * d2;
        A01 = q.Q00 * q.Q10 * d0 + q.Q01 * q.Q11 * d1 + q.Q02 * q.Q12 * d2;
        A02 = q.Q00 * q.Q20 * d0 + q.Q01 * q.Q21 * d1 + q.Q02 * q.Q22 * d2;
        A11 = q.Q10 * q.Q10 * d0 + q.Q11 * q.Q11 * d1 + q.Q12 * q.Q12 * d2;
        A12 = q.Q10 * q.Q20 * d0 + q.Q11 * q.Q21 * d1 + q.Q12 * q.Q22 * d2;
        A22 = q.Q20 * q.Q20 * d0 + q.Q21 * q.Q21 * d1 + q.Q22 * q.Q22 * d2;
    }

    /// <summary>순환 야코비(최대 8 스윕 · double) — 고유값 l0..l2 · 고유벡터 열 q.</summary>
    public static void Eigen(in Sym3 m, out float l0, out float l1, out float l2, out Basis3 q)
    {
        double a00 = m.A00, a01 = m.A01, a02 = m.A02, a11 = m.A11, a12 = m.A12, a22 = m.A22;
        double v00 = 1, v01 = 0, v02 = 0, v10 = 0, v11 = 1, v12 = 0, v20 = 0, v21 = 0, v22 = 1;
        for (var sweep = 0; sweep < 8; sweep++)
        {
            var off = a01 * a01 + a02 * a02 + a12 * a12;
            if (off < 1e-14 * (a00 * a00 + a11 * a11 + a22 * a22) + 1e-30) break;   // float 정밀도면 충분
            Rot(ref a00, ref a11, ref a22, ref a01, ref a02, ref a12, ref v00, ref v01, ref v02, ref v10, ref v11, ref v12, ref v20, ref v21, ref v22);
            Rot(ref a00, ref a22, ref a11, ref a02, ref a01, ref a12, ref v00, ref v02, ref v01, ref v10, ref v12, ref v11, ref v20, ref v22, ref v21);
            Rot(ref a11, ref a22, ref a00, ref a12, ref a01, ref a02, ref v01, ref v02, ref v00, ref v11, ref v12, ref v10, ref v21, ref v22, ref v20);
        }
        l0 = (float)a00; l1 = (float)a11; l2 = (float)a22;
        q = new Basis3((float)v00, (float)v01, (float)v02, (float)v10, (float)v11, (float)v12, (float)v20, (float)v21, (float)v22);
    }

    // (p,q) 평면 회전으로 apq 소거 — r 은 나머지 축, apr/aqr 은 r 과의 결합 항, v 열 p/q/r
    private static void Rot(ref double app, ref double aqq, ref double arr, ref double apq, ref double apr, ref double aqr,
        ref double v0p, ref double v0q, ref double v0r, ref double v1p, ref double v1q, ref double v1r, ref double v2p, ref double v2q, ref double v2r)
    {
        _ = arr; _ = v0r; _ = v1r; _ = v2r;
        if (Math.Abs(apq) < 1e-300) return;
        var theta = (aqq - app) / (2 * apq);
        var t = (theta >= 0 ? 1.0 : -1.0) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
        var c = 1 / Math.Sqrt(t * t + 1);
        var s = t * c;
        app -= t * apq;
        aqq += t * apq;
        apq = 0;
        var pr = apr; var qr = aqr;
        apr = c * pr - s * qr;
        aqr = s * pr + c * qr;
        var a = v0p; var b = v0q; v0p = c * a - s * b; v0q = s * a + c * b;
        a = v1p; b = v1q; v1p = c * a - s * b; v1q = s * a + c * b;
        a = v2p; b = v2q; v2p = c * a - s * b; v2q = s * a + c * b;
    }
}

/// <summary>3×3 고유벡터 기저(열 = 고유벡터).</summary>
public readonly struct Basis3
{
    public readonly float Q00, Q01, Q02, Q10, Q11, Q12, Q20, Q21, Q22;
    public Basis3(float q00, float q01, float q02, float q10, float q11, float q12, float q20, float q21, float q22)
    { Q00 = q00; Q01 = q01; Q02 = q02; Q10 = q10; Q11 = q11; Q12 = q12; Q20 = q20; Q21 = q21; Q22 = q22; }
}
