using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Services.Pyrotechnics;

public enum PyroPreset : byte { Campfire, MassiveExplosion, SciFiPlasma, SparkFountain }

/// <summary>파이로 프리셋 — 연소 유체 · 스파크 방출 매개변수(정적 1회 할당).</summary>
public sealed class PyroPresetSpec
{
    public required string Name { get; init; }
    public float Buoyancy { get; init; }        // α (m/s² per K)
    public float SmokeWeight { get; init; }     // β (m/s² per 밀도)
    public float Ignition { get; init; }        // 착화 온도 초과분 K
    public float BurnRate { get; init; }        // 1/s
    public float HeatRelease { get; init; }     // K per 연료 단위
    public float Soot { get; init; }            // 연료 단위당 매연
    public float Expansion { get; init; }       // 연소 팽창(발산 소스)
    public float CoolRate { get; init; }        // 1/s
    public float SmokeDecay { get; init; }      // 1/s
    public float Vorticity { get; init; }       // ε
    public float FuelRate { get; init; }        // 연속 연료 주입/s(0 = 점화 트리거 전용)
    public float EmitRadius { get; init; }
    public float EmitTemp { get; init; }
    public float BurstFuel { get; init; }
    public float BurstTemp { get; init; }
    public float BurstSpeed { get; init; }      // 방사 충격 속도 m/s
    public float BurstRadius { get; init; }
    public int BurstSparks { get; init; }
    public float SparkRate { get; init; }       // 연속 스파크/s
    public float SparkSpeedMin { get; init; }
    public float SparkSpeedMax { get; init; }
    public float SparkLifeMin { get; init; }
    public float SparkLifeMax { get; init; }
    public float SparkTempK { get; init; }
    public float SparkCone { get; init; }       // 0 = 구형 · >0 = 상향 원뿔 cos 하한
    public bool Plasma { get; init; }
}

/// <summary>
/// [PromptFlow Studio v17.42.0] 🔥 실시간 신경망 연소 유체 &amp; 연기·화염 시뮬레이션 엔진(<see cref="Step"/> 무할당).
///   ▸ 32³ 셀-중심 복셀(Δx 0.25 m · 8 m 무대 볼륨) — 속도(u,v,w) · 온도 초과분 θ · 연료 Y · 매연 밀도 ρ, 2-슬롯 링버퍼(핑퐁) 스왑.
///   ▸ 프레임: 부력 v += Δt(αθ − βρ)(Vector&lt;T&gt;) → 보텍스 컨파인먼트 f = εΔx(N × ω) → 연소(착화 θ &gt; θᵢ · 연소율 · 발열 · 매연 · 팽창, Vector&lt;T&gt; ConditionalSelect)
///     → 압력 투영(발산 − 팽창 소스 · 웜스타트 Jacobi 12회 Vector&lt;T&gt; · 노이만 경계) → 반-라그랑주 이류(역추적 1회 · 6장 공유 삼선형) → 냉각/소산(SIMD) · 상단 유출.
///   ▸ 스칼라 쌍둥이(<see cref="UseSimd"/> = false)와 비트 동일 — 동일 연산 순서 · FMA 미사용.
///   ▸ 화염 방출 중심/광도/색온도 집계 → v17.42 스파크 합성기의 포인트 라이트 0번(화염광)으로 주입.
/// </summary>
public sealed class RealtimeNeuralCombustionFluidEngine
{
    public const int N = 32, NN = N * N, Cells = NN * N, JacobiIters = 12;
    public const float Dx = 0.25f, Extent = N * Dx, Half = 0.5f * Extent, Ambient = 293f, EmitThreshold = 450f;

    public static readonly PyroPresetSpec[] Presets =
    {
        new() { Name = "캠프파이어", Buoyancy = 0.012f, SmokeWeight = 0.4f, Ignition = 250f, BurnRate = 2.5f, HeatRelease = 2600f, Soot = 0.35f, Expansion = 1.2f, CoolRate = 1.6f, SmokeDecay = 0.18f, Vorticity = 0.35f,
                FuelRate = 3.2f, EmitRadius = 0.45f, EmitTemp = 900f, BurstFuel = 1.5f, BurstTemp = 900f, BurstSpeed = 3f, BurstRadius = 0.6f, BurstSparks = 900,
                SparkRate = 120f, SparkSpeedMin = 1.5f, SparkSpeedMax = 4.5f, SparkLifeMin = 0.8f, SparkLifeMax = 2.2f, SparkTempK = 1500f, SparkCone = 0.8f },
        new() { Name = "대폭발", Buoyancy = 0.016f, SmokeWeight = 0.9f, Ignition = 200f, BurnRate = 9f, HeatRelease = 3000f, Soot = 1.4f, Expansion = 6f, CoolRate = 0.9f, SmokeDecay = 0.08f, Vorticity = 0.9f,
                FuelRate = 0f, EmitRadius = 0.6f, EmitTemp = 0f, BurstFuel = 4f, BurstTemp = 2400f, BurstSpeed = 22f, BurstRadius = 1.6f, BurstSparks = 40000,
                SparkRate = 0f, SparkSpeedMin = 6f, SparkSpeedMax = 26f, SparkLifeMin = 0.9f, SparkLifeMax = 2.6f, SparkTempK = 2400f, SparkCone = 0f },
        new() { Name = "SF 플라즈마", Buoyancy = 0.004f, SmokeWeight = 0.05f, Ignition = 150f, BurnRate = 6f, HeatRelease = 5200f, Soot = 0.03f, Expansion = 2.5f, CoolRate = 2.6f, SmokeDecay = 1.2f, Vorticity = 1.6f,
                FuelRate = 2.4f, EmitRadius = 0.5f, EmitTemp = 2600f, BurstFuel = 3f, BurstTemp = 5000f, BurstSpeed = 14f, BurstRadius = 1.1f, BurstSparks = 12000,
                SparkRate = 400f, SparkSpeedMin = 3f, SparkSpeedMax = 14f, SparkLifeMin = 0.4f, SparkLifeMax = 1.2f, SparkTempK = 6500f, SparkCone = 0f, Plasma = true },
        new() { Name = "스파크 분수", Buoyancy = 0.01f, SmokeWeight = 0.3f, Ignition = 200f, BurnRate = 3f, HeatRelease = 1800f, Soot = 0.2f, Expansion = 0.8f, CoolRate = 2.2f, SmokeDecay = 0.4f, Vorticity = 0.25f,
                FuelRate = 1.2f, EmitRadius = 0.35f, EmitTemp = 1600f, BurstFuel = 1f, BurstTemp = 1200f, BurstSpeed = 4f, BurstRadius = 0.5f, BurstSparks = 6000,
                SparkRate = 3500f, SparkSpeedMin = 7f, SparkSpeedMax = 12f, SparkLifeMin = 1.2f, SparkLifeMax = 2.4f, SparkTempK = 2100f, SparkCone = 0.93f },
    };

    private static readonly double TickUs = 1_000_000.0 / Stopwatch.Frequency;
    private float[] _u = new float[Cells], _v = new float[Cells], _w = new float[Cells], _t = new float[Cells], _f = new float[Cells], _d = new float[Cells];
    private float[] _u1 = new float[Cells], _v1 = new float[Cells], _w1 = new float[Cells], _t1 = new float[Cells], _f1 = new float[Cells], _d1 = new float[Cells];
    private float[] _p = new float[Cells], _p1 = new float[Cells];
    private readonly float[] _div = new float[Cells], _src = new float[Cells], _wx = new float[Cells], _wy = new float[Cells], _wz = new float[Cells], _wm = new float[Cells];
    private readonly int[] _aj = new int[Cells];
    private readonly float[] _afx = new float[Cells], _afy = new float[Cells], _afz = new float[Cells];
    private float _injAcc;

    public bool UseSimd { get; set; } = true;
    public long Frames { get; private set; }
    public double LastStepUs { get; private set; }
    public double MaxStepUs { get; private set; }

    // 가드/관측 접근(같은 스레드 · 러너 게이트 하)
    public Span<float> U => _u;
    public Span<float> V => _v;
    public Span<float> W => _w;
    public Span<float> Temperature => _t;
    public Span<float> Fuel => _f;
    public Span<float> Density => _d;
    public Span<float> Pressure => _p;
    // 가드 일괄 처리(배열 직접 · 링 스왑 후 현재 슬롯)
    internal float[] UArray => _u;
    internal float[] VArray => _v;
    internal float[] WArray => _w;
    internal float[] TArray => _t;
    internal float[] FArray => _f;
    internal float[] DArray => _d;
    internal float[] PArray => _p;

    /// <summary>직전 프레임 열 입력(주입 + 연소 발열, θ·셀 단위) — 가드의 에너지 보존 상한.</summary>
    public float FrameHeatInput { get; private set; }
    public float ReleasedHeat { get; private set; }
    public float ThermalEnergy { get; private set; }
    public float KineticEnergy { get; private set; }
    public float MaxTempK { get; private set; } = Ambient;
    public float MaxSpeed { get; private set; }
    public float SmokeMass { get; private set; }
    public float FuelMass { get; private set; }
    public float BurnPerSec { get; private set; }
    public int BurningCells { get; private set; }
    public int SmokeCells { get; private set; }
    public float MaxDivergence { get; private set; }
    public float MeanVorticity { get; private set; }
    /// <summary>화염 방출(Σ(θ/1000)⁴ · 셀) · 방출 가중 중심 · 방출 가중 색온도.</summary>
    public float FlameEmission { get; private set; }
    public Vector3 FlameCentroid { get; private set; } = new(0f, 0.5f, 0f);
    public float FlameTempK { get; private set; } = Ambient;

    public static int Index(int x, int y, int z) => x + N * (y + N * z);
    public static Vector3 CellCenter(int x, int y, int z) => new((x + 0.5f) * Dx - Half, (y + 0.5f) * Dx, (z + 0.5f) * Dx - Half);

    public void Reset()
    {
        Array.Clear(_u); Array.Clear(_v); Array.Clear(_w); Array.Clear(_t); Array.Clear(_f); Array.Clear(_d);
        Array.Clear(_p); Array.Clear(_p1); Array.Clear(_src); Array.Clear(_wm);
        _injAcc = 0f; FrameHeatInput = ReleasedHeat = 0f; Frames = 0; MaxStepUs = 0;
        Metrics();
    }

    // ══════════════════════ 소스 주입(무할당) ══════════════════════

    /// <summary>가우스 구 소스 — 연료 · 온도 초과분 · 매연 · 방사/병진 속도.</summary>
    public void Inject(Vector3 world, float radius, float fuel, float temp, float smoke, float radialSpeed, Vector3 vel)
    {
        radius = MathF.Max(radius, 0.5f * Dx);
        var inv = 1f / (radius * radius);
        int x0 = Cell(world.X + Half - radius), x1 = Cell(world.X + Half + radius);
        int y0 = Cell(world.Y - radius), y1 = Cell(world.Y + radius);
        int z0 = Cell(world.Z + Half - radius), z1 = Cell(world.Z + Half + radius);
        for (var z = z0; z <= z1; z++)
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var r = CellCenter(x, y, z) - world;
            var q = r.LengthSquared() * inv;
            if (q > 1f) continue;
            var k = MathF.Exp(-3f * q);
            var i = Index(x, y, z);
            _f[i] += fuel * k;
            _t[i] += temp * k;
            _d[i] += smoke * k;
            _injAcc += temp * k;
            var dir = q > 1e-6f ? r / MathF.Sqrt(r.LengthSquared()) : Vector3.UnitY;
            _u[i] += k * (radialSpeed * dir.X + vel.X);
            _v[i] += k * (radialSpeed * dir.Y + vel.Y);
            _w[i] += k * (radialSpeed * dir.Z + vel.Z);
        }
    }

    private static int Cell(float m) => Math.Clamp((int)(m / Dx), 0, N - 1);

    // ══════════════════════ 프레임(무할당) ══════════════════════

    public void Step(float dt, PyroPresetSpec p)
    {
        var t0 = Stopwatch.GetTimestamp();
        Frames++;
        Buoyancy(dt * p.Buoyancy, dt * p.SmokeWeight);
        if (p.Vorticity > 0f) Confine(dt * p.Vorticity * Dx);
        var released = Combust(dt, p);
        ReleasedHeat = released;
        BurnPerSec = released / MathF.Max(p.HeatRelease, 1f) / dt;
        Project();
        Advect(dt);
        Dissipate(MathF.Exp(-p.CoolRate * dt), MathF.Exp(-p.SmokeDecay * dt));
        FrameHeatInput = _injAcc + released;
        _injAcc = 0f;
        Metrics();
        var us = (Stopwatch.GetTimestamp() - t0) * TickUs;
        LastStepUs = us;
        if (us > MaxStepUs && Frames > 10) MaxStepUs = us;
    }

    private void Buoyancy(float a, float b)
    {
        int i = 0, n = Cells;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var va = new Vector<float>(a); var vb = new Vector<float>(b);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var v = new Vector<float>(_v, i) + (va * new Vector<float>(_t, i) - vb * new Vector<float>(_d, i));
                v.CopyTo(_v, i);
            }
        }
        for (; i < n; i++) _v[i] = _v[i] + (a * _t[i] - b * _d[i]);
    }

    private void Confine(float eps)
    {
        const float h = 0.5f / Dx;
        double sum = 0;
        float[] uA = _u, vA = _v, wA = _w, wxA = _wx, wyA = _wy, wzA = _wz, wmA = _wm;
        for (var z = 1; z < N - 1; z++)
        for (var y = 1; y < N - 1; y++)
        {
            var i = Index(1, y, z);
            for (var x = 1; x < N - 1; x++, i++)
            {
                var ox = h * ((wA[i + N] - wA[i - N]) - (vA[i + NN] - vA[i - NN]));
                var oy = h * ((uA[i + NN] - uA[i - NN]) - (wA[i + 1] - wA[i - 1]));
                var oz = h * ((vA[i + 1] - vA[i - 1]) - (uA[i + N] - uA[i - N]));
                wxA[i] = ox; wyA[i] = oy; wzA[i] = oz;
                var m = MathF.Sqrt(ox * ox + oy * oy + oz * oz);
                wmA[i] = m; sum += m;
            }
        }
        MeanVorticity = (float)(sum / ((N - 2) * (N - 2) * (N - 2)));
        for (var z = 1; z < N - 1; z++)
        for (var y = 1; y < N - 1; y++)
        {
            var i = Index(1, y, z);
            for (var x = 1; x < N - 1; x++, i++)
            {
                var gx = wmA[i + 1] - wmA[i - 1];
                var gy = wmA[i + N] - wmA[i - N];
                var gz = wmA[i + NN] - wmA[i - NN];
                var inv = 1f / (MathF.Sqrt(gx * gx + gy * gy + gz * gz) + 1e-5f);
                gx *= inv; gy *= inv; gz *= inv;
                uA[i] += eps * (gy * wzA[i] - gz * wyA[i]);
                vA[i] += eps * (gz * wxA[i] - gx * wzA[i]);
                wA[i] += eps * (gx * wyA[i] - gy * wxA[i]);
            }
        }
    }

    /// <summary>연소: θ &gt; θᵢ ∧ Y &gt; 0 → 연소량 b = min(Y, k·Δt) · Y −= b · θ += H·b · ρ += s·b · 팽창 소스 = e·b/Δt. 반환 = 총 발열.</summary>
    private float Combust(float dt, PyroPresetSpec p)
    {
        var rate = p.BurnRate * dt; var heat = p.HeatRelease; var soot = p.Soot; var exp = p.Expansion / dt; var ign = p.Ignition;
        int i = 0, n = Cells;
        var burnSum = 0f;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var vr = new Vector<float>(rate); var vh = new Vector<float>(heat); var vs = new Vector<float>(soot); var ve = new Vector<float>(exp); var vi = new Vector<float>(ign);
            var acc = Vector<float>.Zero;
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var f = new Vector<float>(_f, i); var t = new Vector<float>(_t, i);
                var b = Vector.ConditionalSelect(Vector.GreaterThan(t, vi) & Vector.GreaterThan(f, Vector<float>.Zero), Vector.Min(f, vr), Vector<float>.Zero);
                (f - b).CopyTo(_f, i);
                (t + vh * b).CopyTo(_t, i);
                (new Vector<float>(_d, i) + vs * b).CopyTo(_d, i);
                (ve * b).CopyTo(_src, i);
                acc += b;
            }
            for (var k = 0; k < Vector<float>.Count; k++) burnSum += acc[k];
        }
        for (; i < n; i++)
        {
            var f = _f[i]; var t = _t[i];
            var b = t > ign && f > 0f ? MathF.Min(f, rate) : 0f;
            _f[i] = f - b; _t[i] = t + heat * b; _d[i] = _d[i] + soot * b; _src[i] = exp * b;
            burnSum += b;
        }
        return burnSum * heat;
    }

    private void Project()
    {
        const float h = 0.5f / Dx, h2 = Dx * Dx, sixth = 1f / 6f;
        var maxDiv = 0f;
        Array.Clear(_div);
        float[] u = _u, v = _v, w = _w, dv = _div, sr = _src;
        for (var z = 1; z < N - 1; z++)
        for (var y = 1; y < N - 1; y++)
        {
            var i = Index(1, y, z);
            for (var x = 1; x < N - 1; x++, i++)
            {
                var d = h * ((u[i + 1] - u[i - 1]) + (v[i + N] - v[i - N]) + (w[i + NN] - w[i - NN]));
                maxDiv = MathF.Max(maxDiv, MathF.Abs(d));
                dv[i] = d - sr[i];
            }
        }
        MaxDivergence = maxDiv;
        for (var it = 0; it < JacobiIters; it++)
        {
            Jacobi(_p, _p1, _div, h2, sixth, UseSimd);
            Neumann(_p1);
            (_p, _p1) = (_p1, _p);
        }
        var p = _p;
        for (var z = 1; z < N - 1; z++)
        for (var y = 1; y < N - 1; y++)
        {
            var i = Index(1, y, z);
            for (var x = 1; x < N - 1; x++, i++)
            {
                u[i] -= h * (p[i + 1] - p[i - 1]);
                v[i] -= h * (p[i + N] - p[i - N]);
                w[i] -= h * (p[i + NN] - p[i - NN]);
            }
        }
        // 벽면 무관통(바닥 · 측벽) · 상단 개방
        for (var a = 0; a < N; a++)
        for (var b = 0; b < N; b++)
        {
            var xr = N * (a + N * b);        // (0, a, b)
            u[xr] = 0f; u[xr + N - 1] = 0f;
            var yr = a + NN * b;             // (a, 0, b)
            if (v[yr] < 0f) v[yr] = 0f;
            var zr = a + N * b;              // (a, b, 0)
            w[zr] = 0f; w[zr + NN * (N - 1)] = 0f;
        }
    }

    /// <summary>Jacobi 1회(내부 z 슬랩 전 구간 · x 경계 랩 셀은 <see cref="Neumann"/>이 덮어씀) — SIMD/스칼라 비트 동일.</summary>
    public static void Jacobi(float[] p, float[] o, float[] div, float h2, float sixth, bool simd)
    {
        int i = NN, n = Cells - NN;
        if (simd && Vector.IsHardwareAccelerated)
        {
            var vh = new Vector<float>(h2); var vs = new Vector<float>(sixth);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                var s = new Vector<float>(p, i - 1) + new Vector<float>(p, i + 1) + new Vector<float>(p, i - N) + new Vector<float>(p, i + N) + new Vector<float>(p, i - NN) + new Vector<float>(p, i + NN);
                ((s - vh * new Vector<float>(div, i)) * vs).CopyTo(o, i);
            }
        }
        for (; i < n; i++)
            o[i] = (p[i - 1] + p[i + 1] + p[i - N] + p[i + N] + p[i - NN] + p[i + NN] - h2 * div[i]) * sixth;
    }

    private static void Neumann(float[] p)
    {
        const int top = N * (N - 1), back = NN * (N - 1);
        for (var a = 0; a < N; a++)
        for (var b = 0; b < N; b++)
        {
            var xr = N * (a + N * b);        // (0, a, b)
            p[xr] = p[xr + 1]; p[xr + N - 1] = p[xr + N - 2];
            var yr = a + NN * b;             // (a, 0, b)
            p[yr] = p[yr + N]; p[yr + top] = 0f; // 상단 개방(디리클레)
            var zr = a + N * b;              // (a, b, 0)
            p[zr] = p[zr + NN]; p[zr + back] = p[zr + back - NN];
        }
    }

    /// <summary>반-라그랑주: 셀당 역추적 1회(기저 인덱스 · 분수부 캐시) → 6장(u,v,w,θ,Y,ρ) 장별 연속 삼선형 리샘플 → 링 슬롯 스왑.</summary>
    private void Advect(float dt)
    {
        var s = dt / Dx;
        const float lim = N - 1.001f;
        float[] u = _u, v = _v, w = _w, ax = _afx, ay = _afy, az = _afz;
        var aj = _aj;
        var i = 0;
        for (var z = 0; z < N; z++)
        for (var y = 0; y < N; y++)
        for (var x = 0; x < N; x++, i++)
        {
            var bx = x - s * u[i]; var by = y - s * v[i]; var bz = z - s * w[i];
            if (!(bx >= 0f)) bx = bx < 0f ? 0f : x; else if (bx > lim) bx = lim; // NaN 역추적 → 제자리(가드 미가동 시에도 인덱스 안전)
            if (!(by >= 0f)) by = by < 0f ? 0f : y; else if (by > lim) by = lim;
            if (!(bz >= 0f)) bz = bz < 0f ? 0f : z; else if (bz > lim) bz = lim;
            int ix = (int)bx, iy = (int)by, iz = (int)bz;
            aj[i] = ix + N * (iy + N * iz);
            ax[i] = bx - ix; ay[i] = by - iy; az[i] = bz - iz;
        }
        Resample(_u, _u1); Resample(_v, _v1); Resample(_w, _w1);
        Resample(_t, _t1); Resample(_f, _f1); Resample(_d, _d1);
        (_u, _u1) = (_u1, _u); (_v, _v1) = (_v1, _v); (_w, _w1) = (_w1, _w);
        (_t, _t1) = (_t1, _t); (_f, _f1) = (_f1, _f); (_d, _d1) = (_d1, _d);
    }

    private void Resample(float[] a, float[] o)
    {
        float[] ax = _afx, ay = _afy, az = _afz;
        var aj = _aj;
        for (var i = 0; i < Cells; i++)
        {
            var j = aj[i]; float fx = ax[i], fy = ay[i], fz = az[i];
            var a0 = a[j] + (a[j + 1] - a[j]) * fx;
            var a1 = a[j + N] + (a[j + N + 1] - a[j + N]) * fx;
            var a2 = a[j + NN] + (a[j + NN + 1] - a[j + NN]) * fx;
            var a3 = a[j + NN + N] + (a[j + NN + N + 1] - a[j + NN + N]) * fx;
            var b0 = a0 + (a1 - a0) * fy;
            var b1 = a2 + (a3 - a2) * fy;
            o[i] = b0 + (b1 - b0) * fz;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Tri(float[] a, int j, float w000, float w100, float w010, float w110, float w001, float w101, float w011, float w111)
        => w000 * a[j] + w100 * a[j + 1] + w010 * a[j + N] + w110 * a[j + N + 1]
         + w001 * a[j + NN] + w101 * a[j + NN + 1] + w011 * a[j + NN + N] + w111 * a[j + NN + N + 1];

    private void Dissipate(float cool, float smoke)
    {
        int i = 0, n = Cells;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var vc = new Vector<float>(cool); var vs = new Vector<float>(smoke);
            for (; i <= n - Vector<float>.Count; i += Vector<float>.Count)
            {
                (new Vector<float>(_t, i) * vc).CopyTo(_t, i);
                (new Vector<float>(_d, i) * vs).CopyTo(_d, i);
            }
        }
        for (; i < n; i++) { _t[i] = _t[i] * cool; _d[i] = _d[i] * smoke; }
        // 상단 유출층
        for (var z = 0; z < N; z++)
        for (var x = 0; x < N; x++) { var j = Index(x, N - 1, z); _t[j] = 0f; _d[j] = 0f; _f[j] = 0f; }
    }

    private void Metrics()
    {
        double th = 0, ke = 0, sm = 0, fu = 0, em = 0, ex = 0, ey = 0, ez = 0, et = 0;
        float maxT = 0f, maxV2 = 0f;
        int burning = 0, smokeCells = 0;
        float[] uA = _u, vA = _v, wA = _w, tA = _t, dA = _d, fA = _f;
        for (var z = 0; z < N; z++)
        for (var y = 0; y < N; y++)
        {
            var i = Index(0, y, z);
            for (var x = 0; x < N; x++, i++)
            {
                var t = tA[i];
                var v2 = uA[i] * uA[i] + vA[i] * vA[i] + wA[i] * wA[i];
                th += t; ke += 0.5 * v2; sm += dA[i]; fu += fA[i];
                if (t > maxT) maxT = t;
                if (v2 > maxV2) maxV2 = v2;
                if (dA[i] > 0.02f) smokeCells++;
                if (t > EmitThreshold)
                {
                    burning++;
                    var q = t * 1e-3f; q *= q; q *= q;
                    em += q;
                    ex += q * ((x + 0.5f) * Dx - Half); ey += q * ((y + 0.5f) * Dx); ez += q * ((z + 0.5f) * Dx - Half);
                    et += q * t;
                }
            }
        }
        ThermalEnergy = (float)th; KineticEnergy = (float)ke; SmokeMass = (float)sm; FuelMass = (float)fu;
        MaxTempK = Ambient + maxT; MaxSpeed = MathF.Sqrt(maxV2);
        BurningCells = burning; SmokeCells = smokeCells;
        FlameEmission = (float)em;
        if (em > 1e-6)
        {
            FlameCentroid = new Vector3((float)(ex / em), (float)(ey / em), (float)(ez / em));
            FlameTempK = Ambient + (float)(et / em);
        }
        else FlameTempK = Ambient;
    }

    // ══════════════════════ 색 · 볼류메트릭 렌더(뷰어) ══════════════════════

    /// <summary>흑체 근사 RGB(1000~12000 K · 최대 채널 1) — 플라즈마 팔레트는 청록 → 자홍 이온화 색.</summary>
    public static Vector3 FlameColor(float k, bool plasma)
    {
        if (plasma)
        {
            var s = Math.Clamp((k - 1500f) / 5000f, 0f, 1f);
            return Vector3.Lerp(new Vector3(0.55f, 0.15f, 1f), new Vector3(0.35f, 0.95f, 1f), s);
        }
        var t = Math.Clamp(k, 1000f, 12000f) / 100f;
        float r, g, b;
        if (t <= 66f) { r = 1f; g = Math.Clamp((99.4708f * MathF.Log(t) - 161.1196f) / 255f, 0f, 1f); }
        else { r = Math.Clamp(329.6987f * MathF.Pow(t - 60f, -0.1332f) / 255f, 0f, 1f); g = Math.Clamp(288.1222f * MathF.Pow(t - 60f, -0.0755f) / 255f, 0f, 1f); }
        b = t >= 66f ? 1f : t <= 19f ? 0f : Math.Clamp((138.5177f * MathF.Log(t - 10f) - 305.0448f) / 255f, 0f, 1f);
        return new Vector3(r, g, b);
    }

    /// <summary>궤도(yaw) 직교 레이마치 res×res → rgb(선형 0..1 · 톤맵) — 앞→뒤 흡수 · 흑체 방출 · 화염 조명 매연 산란(무할당).</summary>
    public void RenderView(float yaw, bool plasma, Span<float> rgb, int res)
    {
        const int steps = 48;
        var dir = new Vector3(MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
        var right = new Vector3(MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        var ds = Extent * 1.42f / steps;
        var fire = FlameEmission;
        var glow = FlameColor(FlameTempK, plasma) * MathF.Min(1f, fire * 0.01f);
        for (var py = 0; py < res; py++)
        for (var px = 0; px < res; px++)
        {
            var u = ((px + 0.5f) / res - 0.5f) * Extent;
            var h = (1f - (py + 0.5f) / res) * Extent;
            var o = new Vector3(0f, h, 0f) + right * u - dir * (Extent * 0.71f);
            var trans = 1f; var c = Vector3.Zero;
            for (var k = 0; k < steps && trans > 0.01f; k++)
            {
                var q = o + dir * ((k + 0.5f) * ds);
                var gx = (q.X + Half) / Dx - 0.5f; var gy = q.Y / Dx - 0.5f; var gz = (q.Z + Half) / Dx - 0.5f;
                if (gx < 0f || gy < 0f || gz < 0f || gx >= N - 1 || gy >= N - 1 || gz >= N - 1) continue;
                int ix = (int)gx, iy = (int)gy, iz = (int)gz;
                float fx = gx - ix, fy = gy - iy, fz = gz - iz;
                float w000 = (1 - fx) * (1 - fy) * (1 - fz), w100 = fx * (1 - fy) * (1 - fz), w010 = (1 - fx) * fy * (1 - fz), w110 = fx * fy * (1 - fz);
                float w001 = (1 - fx) * (1 - fy) * fz, w101 = fx * (1 - fy) * fz, w011 = (1 - fx) * fy * fz, w111 = fx * fy * fz;
                var j = Index(ix, iy, iz);
                var d = Tri(_d, j, w000, w100, w010, w110, w001, w101, w011, w111);
                var t = Tri(_t, j, w000, w100, w010, w110, w001, w101, w011, w111);
                if (!float.IsFinite(d) || !float.IsFinite(t)) continue;
                var a = 1f - MathF.Exp(-2.2f * MathF.Max(d, 0f) * ds);
                if (t > 350f)
                {
                    var e = (t - 350f) * 1e-3f; e *= e;
                    c += trans * FlameColor(Ambient + t, plasma) * (e * ds * 2.5f);
                }
                c += trans * a * (new Vector3(0.11f, 0.105f, 0.1f) + glow * 0.6f);
                trans *= 1f - a;
            }
            var b = (py * res + px) * 3;
            rgb[b] = 1f - MathF.Exp(-c.X); rgb[b + 1] = 1f - MathF.Exp(-c.Y); rgb[b + 2] = 1f - MathF.Exp(-c.Z);
        }
    }
}
