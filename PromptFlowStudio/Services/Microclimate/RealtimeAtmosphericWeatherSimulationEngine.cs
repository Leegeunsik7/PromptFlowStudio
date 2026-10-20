using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Microclimate;

/// <summary>기상 프리셋.</summary>
public enum WeatherPreset : byte { Clear = 0, Typhoon = 1, Blizzard = 2, DenseFog = 3 }

/// <summary>프리셋 파라미터(지상 기온 · 상대습도 · 지균풍 · 난류 · 소용돌이).</summary>
public readonly record struct WeatherPresetSpec(string Name, float SurfaceC, float Humidity, float WindMs, float WindDirDeg, float Turbulence, float Vortex, float SurfaceHeatC, float CloudRh, int CloudBase, int CloudTop);

/// <summary>대기 격자 SIMD 커널 — 평탄 인덱스 i = (z·NY + y)·NX + x · 범위 [NXY, n − NXY) · 스칼라 쌍둥이와 비트 동일.</summary>
public static class WeatherKernels
{
    public static void DivergenceScalar(ReadOnlySpan<float> u, ReadOnlySpan<float> v, ReadOnlySpan<float> w, Span<float> div, int nx, int nxy, float inv2h)
    {
        for (var i = nxy; i < div.Length - nxy; i++) div[i] = ((u[i + 1] - u[i - 1]) + (v[i + nx] - v[i - nx]) + (w[i + nxy] - w[i - nxy])) * inv2h;
    }

    public static void DivergenceSimd(ReadOnlySpan<float> u, ReadOnlySpan<float> v, ReadOnlySpan<float> w, Span<float> div, int nx, int nxy, float inv2h)
    {
        var i = nxy;
        var end = div.Length - nxy;
        var vw = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            ref var ru = ref MemoryMarshal.GetReference(u); ref var rv = ref MemoryMarshal.GetReference(v); ref var rw = ref MemoryMarshal.GetReference(w); ref var rd = ref MemoryMarshal.GetReference(div);
            var k = new Vector<float>(inv2h);
            for (; i <= end - vw; i += vw)
                S(ref rd, i, ((L(ref ru, i + 1) - L(ref ru, i - 1)) + (L(ref rv, i + nx) - L(ref rv, i - nx)) + (L(ref rw, i + nxy) - L(ref rw, i - nxy))) * k);
        }
        for (; i < end; i++) div[i] = ((u[i + 1] - u[i - 1]) + (v[i + nx] - v[i - nx]) + (w[i + nxy] - w[i - nxy])) * inv2h;
    }

    /// <summary>색 SOR 반 스윕(제자리 · 이웃 오프셋 ox/oy/oz): p += mask·ω·((Σ 6 이웃 − k·div)/6 − p) — 같은 색 셀은 서로 이웃이 아니므로 벡터 제자리 갱신이 순차와 동일.
    /// 범위 [oz, n − oz).</summary>
    public static void SorColorScalar(Span<float> p, ReadOnlySpan<float> div, ReadOnlySpan<float> mask, int ox, int oy, int oz, float k, float omega)
    {
        const float sixth = 1f / 6f;
        for (var i = oz; i < p.Length - oz; i++)
        {
            var j = (p[i - ox] + p[i + ox] + p[i - oy] + p[i + oy] + p[i - oz] + p[i + oz] - k * div[i]) * sixth;
            p[i] = p[i] + mask[i] * omega * (j - p[i]);
        }
    }

    public static void SorColorSimd(Span<float> p, ReadOnlySpan<float> div, ReadOnlySpan<float> mask, int ox, int oy, int oz, float k, float omega)
    {
        const float sixth = 1f / 6f;
        var i = oz;
        var end = p.Length - oz;
        var vw = Vector<float>.Count;
        // 같은 벡터 안의 셀끼리 x 이웃(±ox) 이면 제자리 갱신이 순차와 달라짐 → 색 마스크가 이를 배제(같은 색은 이웃 아님)
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            ref var rp = ref MemoryMarshal.GetReference(p); ref var rd = ref MemoryMarshal.GetReference(div); ref var rm = ref MemoryMarshal.GetReference(mask);
            var vk = new Vector<float>(k);
            var vs = new Vector<float>(sixth);
            var vo = new Vector<float>(omega);
            for (; i <= end - vw; i += vw)
            {
                var j = (L(ref rp, i - ox) + L(ref rp, i + ox) + L(ref rp, i - oy) + L(ref rp, i + oy) + L(ref rp, i - oz) + L(ref rp, i + oz) - vk * L(ref rd, i)) * vs;
                var c = L(ref rp, i);
                S(ref rp, i, c + L(ref rm, i) * vo * (j - c));
            }
        }
        for (; i < end; i++)
        {
            var j = (p[i - ox] + p[i + ox] + p[i - oy] + p[i + oy] + p[i - oz] + p[i + oz] - k * div[i]) * sixth;
            p[i] = p[i] + mask[i] * omega * (j - p[i]);
        }
    }

    /// <summary>u −= ∂p/∂x · v −= ∂p/∂y · w −= ∂p/∂z (중심 차분).</summary>
    public static void SubtractGradientScalar(Span<float> u, Span<float> v, Span<float> w, ReadOnlySpan<float> p, int nx, int nxy, float inv2h)
    {
        for (var i = nxy; i < p.Length - nxy; i++)
        {
            u[i] -= (p[i + 1] - p[i - 1]) * inv2h;
            v[i] -= (p[i + nx] - p[i - nx]) * inv2h;
            w[i] -= (p[i + nxy] - p[i - nxy]) * inv2h;
        }
    }

    public static void SubtractGradientSimd(Span<float> u, Span<float> v, Span<float> w, ReadOnlySpan<float> p, int nx, int nxy, float inv2h)
    {
        var i = nxy;
        var end = p.Length - nxy;
        var vw = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian)
        {
            ref var ru = ref MemoryMarshal.GetReference(u); ref var rv = ref MemoryMarshal.GetReference(v); ref var rw = ref MemoryMarshal.GetReference(w); ref var rp = ref MemoryMarshal.GetReference(p);
            var k = new Vector<float>(inv2h);
            for (; i <= end - vw; i += vw)
            {
                S(ref ru, i, L(ref ru, i) - (L(ref rp, i + 1) - L(ref rp, i - 1)) * k);
                S(ref rv, i, L(ref rv, i) - (L(ref rp, i + nx) - L(ref rp, i - nx)) * k);
                S(ref rw, i, L(ref rw, i) - (L(ref rp, i + nxy) - L(ref rp, i - nxy)) * k);
            }
        }
        for (; i < end; i++)
        {
            u[i] -= (p[i + 1] - p[i - 1]) * inv2h;
            v[i] -= (p[i + nx] - p[i - nx]) * inv2h;
            w[i] -= (p[i + nxy] - p[i - nxy]) * inv2h;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> L(ref float r, int i) => Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref r, i)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void S(ref float r, int i, Vector<float> v) => Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref r, i)), v);
}

/// <summary>
/// [PromptFlow Studio v17.28.0] ⛈ 실시간 3D 대기 기상 &amp; 풍장 유체 이류 엔진(<see cref="Step"/> 무할당).
///   ▸ 32×16×32 복셀(50 m · 1.6 × 0.8 × 1.6 km) — v17.27 생성형 월드 지형(해시 그리드 높이 × 25 축척)을 고체 기둥으로 사용.
///   ▸ 비압축 나비에-스톡스: 외력(부력 · 기압 경도력/지면 항력 · 태풍 소용돌이 · 컬-노이즈 난류) → 반라그랑주 삼선형 이류(속도 · 기온 · 수증기 · 운수량, 2-슬롯 핑퐁 링버퍼) → 가드 투영.
///   ▸ 열역학: 환경 기온 감률 6.5 °C/km · 테텐스 포화 혼합비 · 응결/증발 잠열(2.5 K per g/kg) · 자동 변환 → 기둥 강수율(mm/h) · 미해상 종관 상승은 운층 RH 완화로 근사.
///   ▸ 운량: 기둥 적분 운수량 &gt; 0.05 kg/m² 비율 · 안개: 지면층 운수량 → 코쉬미더 가시거리.
/// </summary>
public sealed class RealtimeAtmosphericWeatherSimulationEngine
{
    public const int NX = 32, NY = 16, NZ = 32, NXY = NX * NY, N = NXY * NZ, Columns = NX * NZ;
    public const float H = 50f, Gravity = 9.81f, LapseRate = 6.5f, Latent = 2.5f, AirDensity = 1.2f, AutoConvert = 0.15f, Drag = 0.02f, TerrainScale = 25f;

    public static readonly WeatherPresetSpec[] Presets =
    {
        new("쾌청", 22f, 0.45f, 4f, 240f, 0.4f, 0f, 2.5f, 0f, 0, 0),
        new("태풍", 26f, 0.97f, 28f, 120f, 2.5f, 1f, 0f, 1.15f, 3, 13),
        new("폭설", -8f, 0.92f, 12f, 315f, 1.2f, 0f, -1f, 1.12f, 3, 10),
        new("짙은 안개", 9f, 1f, 0.8f, 200f, 0.15f, 0f, -3f, 0f, 0, 0),
    };

    // 2-슬롯 링버퍼(현재 · 다음) — 이류 후 교환
    private float[] _u = new float[N], _v = new float[N], _w = new float[N], _t = new float[N], _q = new float[N], _qc = new float[N];
    private float[] _u2 = new float[N], _v2 = new float[N], _w2 = new float[N], _t2 = new float[N], _q2 = new float[N], _qc2 = new float[N];
    private readonly byte[] _solid = new byte[N];
    private readonly int[] _ground = new int[Columns];
    private readonly float[] _terrain = new float[Columns];
    private readonly float[] _precip = new float[Columns], _cloudCol = new float[Columns];
    private readonly float[] _tenv = new float[NY], _penv = new float[NY];
    private float _time;

    public RealtimeAtmosphericWeatherSimulationEngine(ThermodynamicEnergyDivergenceWeatherGuard guard) : this(guard, null) { }

    public RealtimeAtmosphericWeatherSimulationEngine(ThermodynamicEnergyDivergenceWeatherGuard guard, WorldExpansion.GenerativeNeuralWorldExpansionEngine? world)
    {
        Guard = guard;
        // 지형(v17.27): 열 중심 높이 × 축척 → 고체 셀
        for (var z = 0; z < NZ; z++)
            for (var x = 0; x < NX; x++)
            {
                var c = z * NX + x;
                var h = world is null ? 0f : MathF.Max(0f, world.Height((x - NX / 2) * 2f, (z - NZ / 2) * 2f) * TerrainScale);
                _terrain[c] = h;
                _ground[c] = Math.Clamp((int)(h / H), 0, NY - 4);
                for (var y = 0; y < _ground[c]; y++) _solid[Idx(x, y, z)] = 1;
            }
        SetPreset(WeatherPreset.Clear);
    }

    public ThermodynamicEnergyDivergenceWeatherGuard Guard { get; }
    public WeatherPreset Preset { get; private set; }
    public WeatherPresetSpec Spec { get; private set; }
    public float SurfaceC { get; set; }
    public float Humidity { get; set; }
    public float WindMs { get; set; }
    public float WindDirDeg { get; set; }
    public float Turbulence { get; set; }
    public float Vortex { get; set; }
    /// <summary>기압 경도력 배율(1 = 프리셋 · 스트레스 시험용 ≤ 8).</summary>
    public float PressureStress { get; set; } = 1f;
    public float SurfaceHeatC { get; set; }
    /// <summary>종관 규모 상승(전선 · 태풍 나선대) 근사: 운층(CloudBase..CloudTop) 수증기를 RH 목표로 완화(0 = 없음).</summary>
    public float CloudRh { get; set; }
    public bool UseSimd { get; set; } = true;
    public float SimTime => _time;
    public long Steps { get; private set; }
    public float MaxSpeed { get; private set; }
    public float MeanWind { get; private set; }
    public float CloudCover { get; private set; }
    public float FogVisibilityM { get; private set; }
    public float MeanPrecipMmH { get; private set; }
    public float MaxPrecipMmH { get; private set; }
    public float KineticEnergy { get; private set; }
    public float ForcingWork { get; private set; }
    public float PressureGradientHpaKm => AirDensity * Drag * WindMs * PressureStress * 10f;   // ρ·a 를 hPa/km 로
    public double LastStepMs { get; private set; }
    public int Nan { get; private set; }

    public float[] U => _u;
    public float[] V => _v;
    public float[] W => _w;
    public float[] Pressure { get; } = new float[N];
    public float[] Divergence { get; } = new float[N];
    public float[] Scratch { get; } = new float[N];
    public bool Solid(int i) => _solid[i] != 0;
    public static int Idx(int x, int y, int z) => (z * NY + y) * NX + x;
    public float Temperature(int i) => _t[i];
    public float Vapor(int i) => _q[i];
    public float CloudWater(int i) => _qc[i];
    public float ColumnCloud(int c) => _cloudCol[c];
    public float ColumnPrecip(int c) => _precip[c];
    public int GroundLevel(int c) => _ground[c];
    public float TerrainHeight(int c) => _terrain[c];
    public float EnvTemperature(int y) => _tenv[y];

    public void SetPreset(WeatherPreset p)
    {
        Preset = p;
        var s = Presets[(int)p];
        Spec = s;
        SurfaceC = s.SurfaceC; Humidity = s.Humidity; WindMs = s.WindMs; WindDirDeg = s.WindDirDeg; Turbulence = s.Turbulence; Vortex = s.Vortex; SurfaceHeatC = s.SurfaceHeatC; CloudRh = s.CloudRh;
        Reset();
    }

    /// <summary>환경 연직 분포로 초기화(정지 공기 + 지균풍 로그 프로파일).</summary>
    public void Reset()
    {
        UpdateEnvironment();
        for (var z = 0; z < NZ; z++)
            for (var y = 0; y < NY; y++)
                for (var x = 0; x < NX; x++)
                {
                    var i = Idx(x, y, z);
                    Background(y, out var bu, out var bw);
                    _u[i] = _solid[i] != 0 ? 0f : bu; _v[i] = 0f; _w[i] = _solid[i] != 0 ? 0f : bw;
                    _t[i] = _tenv[y];
                    _q[i] = Humidity * 0.9f * Qs(_tenv[y], _penv[y]);
                    _qc[i] = 0f;
                }
        Array.Clear(Pressure); Array.Clear(Divergence); Array.Clear(_precip); Array.Clear(_cloudCol);
        _time = 0f; Steps = 0; Nan = 0;
        Guard.OnReset();
    }

    private void UpdateEnvironment()
    {
        for (var y = 0; y < NY; y++)
        {
            var alt = (y + 0.5f) * H;
            _tenv[y] = SurfaceC - LapseRate * alt / 1000f;
            _penv[y] = 1013.25f * MathF.Exp(-alt / 8400f);
        }
    }

    /// <summary>테텐스 포화 혼합비(g/kg).</summary>
    public static float Qs(float tc, float hpa) => 622f * 6.112f * MathF.Exp(17.67f * tc / (tc + 243.5f)) / hpa;

    private void Background(int y, out float bu, out float bw)
    {
        var prof = MathF.Log(((y + 0.5f) * H + 0.5f) / 0.5f) / MathF.Log((NY * H + 0.5f) / 0.5f);   // 로그 풍속 분포(z0 0.5 m)
        var a = (WindDirDeg + 180f) * (MathF.PI / 180f);   // 풍향(불어오는 방향) → 흐름 방향
        bu = WindMs * prof * MathF.Sin(a);
        bw = WindMs * prof * MathF.Cos(a);
    }

    /// <summary>1 프레임(sim dt): 가드가 정한 CFL 하위 단계 × (외력 → 이류 → 미세물리 → 경계 → 투영).</summary>
    public void Step(float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        UpdateEnvironment();
        var n = Guard.PlanSubsteps(this, dt, out var h);
        Array.Clear(_precip);
        var work = 0f;
        for (var s = 0; s < n; s++)
        {
            _time += h;
            work += Forces(h);
            Advect(h);
            Microphysics(h);
            Boundaries();
            Guard.Project(this, h);
            Steps++;
        }
        ForcingWork = work;
        for (var c = 0; c < Columns; c++) _precip[c] /= dt;   // g/kg/s(프레임 평균) — mm/h 환산은 Measure
        Measure();
        Guard.EndFrame(this);
        LastStepMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private float Forces(float dt)
    {
        var work = 0.0;
        var cx = NX * 0.5f; var cz = NZ * 0.5f;
        var stress = PressureStress;
        var kTurb = Turbulence;
        var t = _time;
        for (var z = 0; z < NZ; z++)
            for (var y = 0; y < NY; y++)
            {
                Background(y, out var bu, out var bw);
                var te = _tenv[y];
                var drag = Drag * (y < 3 ? 3f - y : 1f);   // 지면 마찰층
                for (var x = 0; x < NX; x++)
                {
                    var i = Idx(x, y, z);
                    if (_solid[i] != 0) continue;
                    // 부력(가온 공기 상승 · 운수량 하중)
                    var ay = Gravity * (_t[i] - te) / (te + 273.15f) - Gravity * _qc[i] * 1e-3f;
                    // 기압 경도력(지균풍 × 항력 균형) + 마찰
                    var ax = Drag * bu * stress - drag * _u[i];
                    var az = Drag * bw * stress - drag * _w[i];
                    // 태풍: 접선 소용돌이 + 하층 수렴 유입
                    if (Vortex > 0f)
                    {
                        float dx = x + 0.5f - cx, dz = z + 0.5f - cz;
                        var r = MathF.Sqrt(dx * dx + dz * dz) + 0.5f;
                        var prof = r / 4f * MathF.Exp(1f - r / 4f) * (1f - (float)y / NY);
                        var a = 0.8f * Vortex * stress * prof;
                        ax += a * (-dz / r) - 0.25f * a * dx / r * (y < 4 ? 1f : -0.5f);
                        az += a * (dx / r) - 0.25f * a * dz / r * (y < 4 ? 1f : -0.5f);
                    }
                    // 컬-노이즈 난류(해석적 회전장 · 결정적)
                    if (kTurb > 0f)
                    {
                        float px = x * 0.45f, py = y * 0.6f, pz = z * 0.45f;
                        ax += kTurb * 0.3f * (MathF.Cos(py + 0.7f * t) - MathF.Sin(pz * 1.3f - 0.5f * t));
                        ay += kTurb * 0.15f * (MathF.Cos(pz + 0.4f * t) - MathF.Sin(px * 1.1f + 0.6f * t));
                        az += kTurb * 0.3f * (MathF.Cos(px + 0.9f * t) - MathF.Sin(py * 1.2f - 0.3f * t));
                    }
                    _u[i] += ax * dt; _v[i] += ay * dt; _w[i] += az * dt;
                    work += (ax * _u[i] + ay * _v[i] + az * _w[i]) * dt;
                    // 지면 열/수증기 플럭스
                    if (y == _ground[z * NX + x])
                    {
                        var ts = SurfaceC + SurfaceHeatC;
                        _t[i] += (ts - _t[i]) * MathF.Min(1f, 0.02f * dt);
                        var qs = Qs(ts, _penv[y]) * Humidity;
                        if (_q[i] < qs) _q[i] += (qs - _q[i]) * MathF.Min(1f, 0.02f * dt);
                    }
                }
            }
        return (float)work * 0.5f * AirDensity * H * H * H;
    }

    private void Advect(float dt)
    {
        var k = dt / H;
        for (var z = 0; z < NZ; z++)
            for (var y = 0; y < NY; y++)
                for (var x = 0; x < NX; x++)
                {
                    var i = Idx(x, y, z);
                    if (_solid[i] != 0) { _u2[i] = 0f; _v2[i] = 0f; _w2[i] = 0f; _t2[i] = _t[i]; _q2[i] = _q[i]; _qc2[i] = 0f; continue; }
                    var bx = x - _u[i] * k; var by = y - _v[i] * k; var bz = z - _w[i] * k;
                    Sample(bx, by, bz, out var i000, out var fx, out var fy, out var fz);
                    _u2[i] = Tri(_u, i000, fx, fy, fz); _v2[i] = Tri(_v, i000, fx, fy, fz); _w2[i] = Tri(_w, i000, fx, fy, fz);
                    _t2[i] = Tri(_t, i000, fx, fy, fz); _q2[i] = Tri(_q, i000, fx, fy, fz); _qc2[i] = Tri(_qc, i000, fx, fy, fz);
                }
        (_u, _u2) = (_u2, _u); (_v, _v2) = (_v2, _v); (_w, _w2) = (_w2, _w);
        (_t, _t2) = (_t2, _t); (_q, _q2) = (_q2, _q); (_qc, _qc2) = (_qc2, _qc);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sample(float x, float y, float z, out int i000, out float fx, out float fy, out float fz)
    {
        // NaN 은 비교가 거짓 → 0 으로 고정(인덱스 안전)
        x = x >= 0f ? (x <= NX - 1.001f ? x : NX - 1.001f) : 0f;
        y = y >= 0f ? (y <= NY - 1.001f ? y : NY - 1.001f) : 0f;
        z = z >= 0f ? (z <= NZ - 1.001f ? z : NZ - 1.001f) : 0f;
        int ix = (int)x, iy = (int)y, iz = (int)z;
        fx = x - ix; fy = y - iy; fz = z - iz;
        i000 = Idx(ix, iy, iz);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Tri(float[] f, int i, float fx, float fy, float fz)
    {
        float c00 = f[i] + (f[i + 1] - f[i]) * fx, c10 = f[i + NX] + (f[i + NX + 1] - f[i + NX]) * fx;
        float c01 = f[i + NXY] + (f[i + NXY + 1] - f[i + NXY]) * fx, c11 = f[i + NXY + NX] + (f[i + NXY + NX + 1] - f[i + NXY + NX]) * fx;
        var c0 = c00 + (c10 - c00) * fy;
        var c1 = c01 + (c11 - c01) * fy;
        return c0 + (c1 - c0) * fz;
    }

    /// <summary>월드 좌표(m · 영역 중심 원점 · y = 해발) 풍속 삼선형 표본.</summary>
    public Vector3 WindAt(Vector3 p)
    {
        Sample(p.X / H + NX * 0.5f - 0.5f, p.Y / H - 0.5f, p.Z / H + NZ * 0.5f - 0.5f, out var i, out var fx, out var fy, out var fz);
        return new Vector3(Tri(_u, i, fx, fy, fz), Tri(_v, i, fx, fy, fz), Tri(_w, i, fx, fy, fz));
    }

    public float TemperatureAt(Vector3 p)
    {
        Sample(p.X / H + NX * 0.5f - 0.5f, p.Y / H - 0.5f, p.Z / H + NZ * 0.5f - 0.5f, out var i, out var fx, out var fy, out var fz);
        return Tri(_t, i, fx, fy, fz);
    }

    private void Microphysics(float dt)
    {
        var rate = MathF.Min(1f, 0.5f * dt);
        for (var z = 0; z < NZ; z++)
            for (var y = 0; y < NY; y++)
            {
                var pe = _penv[y];
                for (var x = 0; x < NX; x++)
                {
                    var i = Idx(x, y, z);
                    if (_solid[i] != 0) continue;
                    var qs = Qs(_t[i], pe);
                    if (CloudRh > 0f && y >= Spec.CloudBase && y <= Spec.CloudTop) { var tq = CloudRh * qs; if (_q[i] < tq) _q[i] += (tq - _q[i]) * MathF.Min(1f, 0.03f * dt); }
                    if (_q[i] > qs)
                    {
                        var d = (_q[i] - qs) * rate;
                        _q[i] -= d; _qc[i] += d; _t[i] += Latent * d;
                    }
                    else if (_qc[i] > 0f)
                    {
                        var d = MathF.Min(_qc[i], (qs - _q[i]) * rate);
                        _q[i] += d; _qc[i] -= d; _t[i] -= Latent * d;
                    }
                    if (_qc[i] > AutoConvert)
                    {
                        var d = (_qc[i] - AutoConvert) * MathF.Min(1f, 0.2f * dt);
                        _qc[i] -= d;
                        _precip[z * NX + x] += d;   // g/kg (셀) — Step 에서 시간 정규화
                    }
                }
            }
    }

    private void Boundaries()
    {
        for (var z = 0; z < NZ; z++)
            for (var y = 0; y < NY; y++)
            {
                Background(y, out var bu, out var bw);
                for (var x = 0; x < NX; x++)
                {
                    var edge = x == 0 || x == NX - 1 || z == 0 || z == NZ - 1;
                    var i = Idx(x, y, z);
                    if (_solid[i] != 0) { _u[i] = _v[i] = _w[i] = 0f; continue; }
                    if (y == NY - 1) { _v[i] = MathF.Max(0f, _v[i]); }
                    if (y == _ground[z * NX + x] && _v[i] < 0f) _v[i] = 0f;
                    if (!edge) continue;
                    // 유입면: 배경 흐름 · 환경 열역학(유출면은 영 기울기)
                    var inflow = (x == 0 && bu > 0f) || (x == NX - 1 && bu < 0f) || (z == 0 && bw > 0f) || (z == NZ - 1 && bw < 0f);
                    if (inflow && Vortex <= 0f)
                    {
                        _u[i] = bu; _v[i] = 0f; _w[i] = bw;
                        _t[i] = _tenv[y]; _q[i] = Humidity * 0.9f * Qs(_tenv[y], _penv[y]); _qc[i] = 0f;
                    }
                }
            }
    }

    private void Measure()
    {
        double ke = 0, sum = 0;
        var vmax = 0f;
        var nan = 0;
        var cells = 0;
        for (var i = 0; i < N; i++)
        {
            if (_solid[i] != 0) continue;
            var s2 = _u[i] * _u[i] + _v[i] * _v[i] + _w[i] * _w[i];
            if (!float.IsFinite(s2)) { nan++; continue; }
            ke += s2;
            sum += MathF.Sqrt(s2);
            if (s2 > vmax) vmax = s2;
            cells++;
        }
        Nan = nan;
        MaxSpeed = MathF.Sqrt(vmax);
        MeanWind = cells > 0 ? (float)(sum / cells) : 0f;
        KineticEnergy = (float)(0.5 * AirDensity * H * H * H * ke);
        var cloudy = 0;
        double pr = 0;
        var pmax = 0f;
        var fog = 0f;
        for (var c = 0; c < Columns; c++)
        {
            int x = c % NX, z = c / NX;
            var col = 0f;
            for (var y = _ground[c]; y < NY; y++) col += _qc[Idx(x, y, z)] * 1e-3f * AirDensity * H;   // kg/m²
            _cloudCol[c] = col;
            if (col > 0.05f) cloudy++;
            fog += _qc[Idx(x, _ground[c], z)];
            // 강수율: g/kg/s × ρ × H(m) → g/m²/s = mm/s × 1e-3 → mm/h
            var mmh = _precip[c] * 1e-3f * AirDensity * H * 3600f;
            _precip[c] = mmh;
            pr += mmh;
            if (mmh > pmax) pmax = mmh;
        }
        CloudCover = cloudy / (float)Columns;
        MeanPrecipMmH = (float)(pr / Columns);
        MaxPrecipMmH = pmax;
        // 코쉬미더: 소광 β ≈ 0.15·LWC(g/m³)^0.8 /m → 가시거리 3.912/β
        var lwc = fog / Columns * AirDensity;
        FogVisibilityM = lwc > 1e-4f ? MathF.Min(20000f, 3.912f / (0.15f * MathF.Pow(lwc, 0.8f))) : 20000f;
    }

    /// <summary>수치 사고 주입(가드 시험): 셀 i 속도를 NaN/∞ 로.</summary>
    public void InjectFault(int i, float value) { _u[i] = value; _v[i] = value; _w[i] = value; }

    /// <summary>전 격자 속도 배율(에너지 클램프).</summary>
    public void ScaleVelocities(float s)
    {
        for (var i = 0; i < N; i++) { _u[i] *= s; _v[i] *= s; _w[i] *= s; }
        KineticEnergy *= s * s;
    }
}
