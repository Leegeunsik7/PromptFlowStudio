using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.Atmosphere;

/// <summary>환경 공용 SIMD 커널(Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치).</summary>
public static class EnvSimd
{
    private static int Body(int n) => Vector.IsHardwareAccelerated && n >= Vector<float>.Count ? n - n % Vector<float>.Count : 0;

    /// <summary>dst = ax·sx + ay·sy + az·sz.</summary>
    public static void Dot3(ReadOnlySpan<float> ax, ReadOnlySpan<float> ay, ReadOnlySpan<float> az, float sx, float sy, float sz, Span<float> dst)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var x = MemoryMarshal.Cast<float, Vector<float>>(ax[..m]); var y = MemoryMarshal.Cast<float, Vector<float>>(ay[..m]); var z = MemoryMarshal.Cast<float, Vector<float>>(az[..m]);
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]);
            Vector<float> vx = new(sx), vy = new(sy), vz = new(sz);
            for (var k = 0; k < d.Length; k++) d[k] = x[k] * vx + y[k] * vy + z[k] * vz;
        }
        for (var i = m; i < n; i++) dst[i] = ax[i] * sx + ay[i] * sy + az[i] * sz;
    }

    public static void Dot3Scalar(ReadOnlySpan<float> ax, ReadOnlySpan<float> ay, ReadOnlySpan<float> az, float sx, float sy, float sz, Span<float> dst)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = ax[i] * sx + ay[i] * sy + az[i] * sz;
    }

    /// <summary>acc += a·b.</summary>
    public static void MulAcc(Span<float> acc, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = acc.Length; var m = Body(n);
        if (m > 0)
        {
            var c = MemoryMarshal.Cast<float, Vector<float>>(acc[..m]); var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]);
            for (var k = 0; k < c.Length; k++) c[k] = c[k] + va[k] * vb[k];
        }
        for (var i = m; i < n; i++) acc[i] = acc[i] + a[i] * b[i];
    }

    public static void MulAccScalar(Span<float> acc, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        for (var i = 0; i < acc.Length; i++) acc[i] = acc[i] + a[i] * b[i];
    }

    /// <summary>공중 원근(안개 합성): c = fog + (c − fog)·t.</summary>
    public static void Composite(Span<float> c, ReadOnlySpan<float> t, float fog)
    {
        var n = c.Length; var m = Body(n);
        if (m > 0)
        {
            var vc = MemoryMarshal.Cast<float, Vector<float>>(c[..m]); var vt = MemoryMarshal.Cast<float, Vector<float>>(t[..m]);
            var f = new Vector<float>(fog);
            for (var k = 0; k < vc.Length; k++) vc[k] = f + (vc[k] - f) * vt[k];
        }
        for (var i = m; i < n; i++) c[i] = fog + (c[i] - fog) * t[i];
    }

    public static void CompositeScalar(Span<float> c, ReadOnlySpan<float> t, float fog)
    {
        for (var i = 0; i < c.Length; i++) c[i] = fog + (c[i] - fog) * t[i];
    }

    /// <summary>dst = a + (b − a)·t (재질 오버레이).</summary>
    public static void Lerp(Span<float> dst, ReadOnlySpan<float> a, float b, ReadOnlySpan<float> t)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vt = MemoryMarshal.Cast<float, Vector<float>>(t[..m]);
            var vb = new Vector<float>(b);
            for (var k = 0; k < d.Length; k++) d[k] = va[k] + (vb - va[k]) * vt[k];
        }
        for (var i = m; i < n; i++) dst[i] = a[i] + (b - a[i]) * t[i];
    }

    public static void LerpScalar(Span<float> dst, ReadOnlySpan<float> a, float b, ReadOnlySpan<float> t)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = a[i] + (b - a[i]) * t[i];
    }

    /// <summary>수막 상한: over = max(x − cap, 0) · x = min(x, cap).</summary>
    public static void ClampOverflow(Span<float> x, ReadOnlySpan<float> cap, Span<float> over)
    {
        var n = x.Length; var m = Body(n);
        if (m > 0)
        {
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x[..m]); var vc = MemoryMarshal.Cast<float, Vector<float>>(cap[..m]); var vo = MemoryMarshal.Cast<float, Vector<float>>(over[..m]);
            for (var k = 0; k < vx.Length; k++) { vo[k] = Vector.Max(vx[k] - vc[k], Vector<float>.Zero); vx[k] = Vector.Min(vx[k], vc[k]); }
        }
        for (var i = m; i < n; i++) { over[i] = MathF.Max(x[i] - cap[i], 0f); x[i] = MathF.Min(x[i], cap[i]); }
    }

    public static void ClampOverflowScalar(Span<float> x, ReadOnlySpan<float> cap, Span<float> over)
    {
        for (var i = 0; i < x.Length; i++) { over[i] = MathF.Max(x[i] - cap[i], 0f); x[i] = MathF.Min(x[i], cap[i]); }
    }
}

/// <summary>
/// [PromptFlow Studio v16.84.0] 🌅 실시간 대기 산란 &amp; 볼류메트릭 하늘 광원 엔진(Hillaire 2020 계열 · 단일 산란 · 무할당).
///   ▸ 대기: 행성 반경 6360 km · 상한 6460 km · Rayleigh(β = 5.8/13.6/33.1 ×10⁻⁶ m⁻¹ · H = 8 km) · Mie(Cornette-Shanks g = 0.8 · H = 1.2 km · 연무 배율) · 오존 텐트 흡수(25 km ± 15 km).
///   ▸ 투과율 LUT(고도 32 × 천정 코사인 128 · 행성 그림자 = 0) — 연무 변경 시에만 재구축.
///   ▸ 하늘 그리드(방위 32 × 고도 16 · 지평선 밀집) × 시선 16 스텝: 정적 가중(시선 투과 × 밀도 × ds) 선계산 → 프레임당 태양/달 방향 내적 · LUT 조회 · 누적만 SIMD.
///   ▸ 태양(NOAA 근사 적위/균시차) · 달(위상 → 시간각 지연 근사) · 구름(2-스트림 확산 투과) · 높이 안개(Koschmieder 가시거리) · 자동 노출 · 야간 퍼킨제 색조(지각).
/// </summary>
public sealed class AtmosphericScatteringLightingEngine
{
    public const int NAz = 32, NEl = 16, Directions = NAz * NEl, Steps = 16, LutH = 32, LutMu = 128, LutSamples = 40;
    public const float GroundRadius = 6_360_000f, TopRadius = 6_460_000f, RayleighH = 8000f, MieH = 1200f, MieG = 0.8f, CameraAltitude = 200f;
    public const float MieScattering = 3.996e-6f, MieAbsorption = 4.40e-6f, MoonIrradiance = 2.5e-6f, SunLux = 120_000f;
    public static readonly Vector3 RayleighScattering = new(5.802e-6f, 13.558e-6f, 33.1e-6f);
    public static readonly Vector3 OzoneAbsorption = new(0.650e-6f, 1.881e-6f, 0.085e-6f);
    private static readonly Vector3 NightGlow = new(1.5e-8f, 2.0e-8f, 3.5e-8f);
    private static readonly Vector3 OvercastTint = new(0.92f, 0.94f, 0.97f);
    private static readonly Vector3 ScotopicTint = new(0.55f, 0.68f, 1.0f);

    private readonly object _gate = new();
    private readonly float[] _lutR = new float[LutH * LutMu], _lutG = new float[LutH * LutMu], _lutB = new float[LutH * LutMu];
    private readonly float[] _ux = new float[Steps * Directions], _uy = new float[Steps * Directions], _uz = new float[Steps * Directions], _uh = new float[Steps * Directions];
    private readonly float[] _arR = new float[Steps * Directions], _arG = new float[Steps * Directions], _arB = new float[Steps * Directions];
    private readonly float[] _amR = new float[Steps * Directions], _amG = new float[Steps * Directions], _amB = new float[Steps * Directions];
    private readonly float[] _mu = new float[Directions], _tR = new float[Directions], _tG = new float[Directions], _tB = new float[Directions];
    private readonly float[] _aRr = new float[Directions], _aRg = new float[Directions], _aRb = new float[Directions];
    private readonly float[] _aMr = new float[Directions], _aMg = new float[Directions], _aMb = new float[Directions];
    private readonly float[] _dx = new float[Directions], _dy = new float[Directions], _dz = new float[Directions], _cosW = new float[Directions];
    private float _builtHaze = -1f, _logExposure = 8f;

    public readonly float[] SkyR = new float[Directions], SkyG = new float[Directions], SkyB = new float[Directions];

    public AtmosphericScatteringLightingEngine()
    {
        for (var j = 0; j < NEl; j++)
        {
            float v0 = j / (float)NEl, v1 = (j + 1) / (float)NEl, vm = (j + 0.5f) / NEl;
            var el = vm * vm * MathF.PI * 0.5f;
            var dEl = (v1 * v1 - v0 * v0) * MathF.PI * 0.5f;
            for (var i = 0; i < NAz; i++)
            {
                var az = (i + 0.5f) / NAz * MathF.Tau;
                var d = j * NAz + i;
                _dx[d] = MathF.Sin(az) * MathF.Cos(el); _dy[d] = MathF.Sin(el); _dz[d] = MathF.Cos(az) * MathF.Cos(el);
                _cosW[d] = MathF.Sin(el) * MathF.Cos(el) * dEl * (MathF.Tau / NAz);
            }
        }
        SunDirection = Vector3.UnitY;
        Update(Vector3.Normalize(new Vector3(0.3f, 0.8f, 0.5f)), -Vector3.UnitY, 0.5f, 0f, 0f, 0f, 1f, 1f);
    }

    // ── 입력/출력 상태 ──
    public float Haze { get; private set; } = 1f;
    public float CloudCoverage { get; private set; }
    public float CloudOpticalDepth { get; private set; }
    public float FogDensity { get; private set; }
    public Vector3 SunDirection { get; private set; }
    public Vector3 MoonDirection { get; private set; }
    public float MoonPhase { get; private set; }
    public Vector3 SunIrradiance { get; private set; }
    public Vector3 MoonLight { get; private set; }
    public Vector3 AmbientIrradiance { get; private set; }
    public Vector3 HorizonColor { get; private set; }
    public Vector3 FogColor { get; private set; }
    public Vector3 ZenithColor { get; private set; }
    public float Exposure { get; private set; } = 1f;
    public float NightFactor { get; private set; }
    public float GodRayStrength { get; private set; }
    public float Visibility { get; private set; }
    public float GroundLux { get; private set; }
    public int LutBuilds { get; private set; }
    public double LastUpdateMs { get; private set; }
    public float SunElevationDeg => MathF.Asin(Math.Clamp(SunDirection.Y, -1f, 1f)) * 180f / MathF.PI;

    public Vector3 Direction(int d) => new(_dx[d], _dy[d], _dz[d]);
    public Vector3 Sky(int d) => new(SkyR[d], SkyG[d], SkyB[d]);
    public static float Luminance(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;

    /// <summary>방위/고도(°) → 월드 방향(+Y 위 · +Z 북 · +X 동).</summary>
    public static Vector3 FromAngles(float elevDeg, float azDeg)
    {
        float e = elevDeg * MathF.PI / 180f, a = azDeg * MathF.PI / 180f;
        return new Vector3(MathF.Sin(a) * MathF.Cos(e), MathF.Sin(e), MathF.Cos(a) * MathF.Cos(e));
    }

    /// <summary>NOAA 근사 태양 위치(지방 표준 자오선 가정): 연중 일 · 시(0..24) · 위도 → (고도°, 방위° 북 기준 시계).</summary>
    public static (float Elevation, float Azimuth) SolarPosition(int dayOfYear, float hour, float latitudeDeg)
    {
        var g = MathF.Tau / 365f * (dayOfYear - 1 + (hour - 12f) / 24f);
        var decl = 0.006918f - 0.399912f * MathF.Cos(g) + 0.070257f * MathF.Sin(g) - 0.006758f * MathF.Cos(2 * g) + 0.000907f * MathF.Sin(2 * g) - 0.002697f * MathF.Cos(3 * g) + 0.00148f * MathF.Sin(3 * g);
        var eqt = 229.18f * (0.000075f + 0.001868f * MathF.Cos(g) - 0.032077f * MathF.Sin(g) - 0.014615f * MathF.Cos(2 * g) - 0.040849f * MathF.Sin(2 * g));
        return FromHourAngle(((hour * 60f + eqt) / 4f - 180f) * MathF.PI / 180f, decl, latitudeDeg * MathF.PI / 180f);
    }

    /// <summary>달 근사: 위상(0 = 삭 · 0.5 = 망)만큼 시간각 지연 · 적위는 망일 때 태양 반대.</summary>
    public static (float Elevation, float Azimuth) LunarPosition(int dayOfYear, float hour, float latitudeDeg, float phase)
    {
        var g = MathF.Tau / 365f * (dayOfYear - 1 + (hour - 12f) / 24f);
        var decl = (0.006918f - 0.399912f * MathF.Cos(g) + 0.070257f * MathF.Sin(g)) * MathF.Cos(MathF.Tau * phase);
        var ha = (hour * 15f - 180f - phase * 360f) * MathF.PI / 180f;
        return FromHourAngle(ha, decl, latitudeDeg * MathF.PI / 180f);
    }

    private static (float, float) FromHourAngle(float ha, float decl, float lat)
    {
        var cz = Math.Clamp(MathF.Sin(lat) * MathF.Sin(decl) + MathF.Cos(lat) * MathF.Cos(decl) * MathF.Cos(ha), -1f, 1f);
        var el = 90f - MathF.Acos(cz) * 180f / MathF.PI;
        var az = MathF.Atan2(MathF.Sin(ha), MathF.Cos(ha) * MathF.Sin(lat) - MathF.Tan(decl) * MathF.Cos(lat)) * 180f / MathF.PI + 180f;
        return (el, az % 360f);
    }

    public static float RayleighPhase(float mu) => 3f / (16f * MathF.PI) * (1f + mu * mu);

    public static float MiePhase(float mu, float g)
    {
        var g2 = g * g; var den = 1f + g2 - 2f * g * mu;
        return 3f / (8f * MathF.PI) * (1f - g2) * (1f + mu * mu) / ((2f + g2) * den * MathF.Sqrt(den));
    }

    private static double OzoneDensity(double h) => Math.Max(0.0, 1.0 - Math.Abs(h - 25000.0) / 15000.0);

    /// <summary>LUT 고도 좌표(연속 · 0..LutH−1).</summary>
    private static float HeightCoord(float h) => MathF.Sqrt(Math.Clamp(h / (TopRadius - GroundRadius), 0f, 1f)) * (LutH - 1);

    /// <summary>투과율 LUT 재구축(연무 배율 haze → Mie) + 하늘 정적 가중. 제어/드문 경로(배열 재사용 · 무할당).</summary>
    private void BuildTables(float haze)
    {
        double bRr = RayleighScattering.X, bRg = RayleighScattering.Y, bRb = RayleighScattering.Z;
        double bM = (MieScattering + MieAbsorption) * (double)haze;
        double oR = OzoneAbsorption.X, oG = OzoneAbsorption.Y, oB = OzoneAbsorption.Z;
        const double Rg = GroundRadius, Rt = TopRadius;
        for (var i = 0; i < LutH; i++)
        {
            var u = i / (double)(LutH - 1);
            var r = Rg + u * u * (Rt - Rg);
            for (var j = 0; j < LutMu; j++)
            {
                var mu = -1.0 + 2.0 * j / (LutMu - 1);
                var idx = i * LutMu + j;
                var disc = r * r * (mu * mu - 1.0) + Rg * Rg;
                if (mu < 0 && disc >= 0) { _lutR[idx] = 0; _lutG[idx] = 0; _lutB[idx] = 0; continue; }
                var len = -r * mu + Math.Sqrt(Math.Max(0.0, r * r * (mu * mu - 1.0) + Rt * Rt));
                var ds = len / LutSamples;
                double tr = 0, tg = 0, tb = 0;
                for (var s = 0; s < LutSamples; s++)
                {
                    var t = (s + 0.5) * ds;
                    var h = Math.Sqrt(r * r + t * t + 2.0 * r * mu * t) - Rg;
                    double dR = Math.Exp(-h / RayleighH), dM = Math.Exp(-h / MieH), dO = OzoneDensity(h);
                    var m = bM * dM;
                    tr += (bRr * dR + m + oR * dO) * ds; tg += (bRg * dR + m + oG * dO) * ds; tb += (bRb * dR + m + oB * dO) * ds;
                }
                _lutR[idx] = (float)Math.Exp(-tr); _lutG[idx] = (float)Math.Exp(-tg); _lutB[idx] = (float)Math.Exp(-tb);
            }
        }
        const double r0 = Rg + CameraAltitude;
        for (var d = 0; d < Directions; d++)
        {
            double dx = _dx[d], dy = _dy[d], dz = _dz[d];
            var b = r0 * dy;
            var tmax = -b + Math.Sqrt(b * b - (r0 * r0 - Rt * Rt));
            double tr = 0, tg = 0, tb = 0;
            for (var k = 0; k < Steps; k++)
            {
                double f0 = k / (double)Steps, f1 = (k + 1) / (double)Steps;
                double t0 = tmax * f0 * f0, t1 = tmax * f1 * f1, ds = t1 - t0, tm = 0.5 * (t0 + t1);
                double px = tm * dx, py = r0 + tm * dy, pz = tm * dz;
                var r = Math.Sqrt(px * px + py * py + pz * pz);
                var h = r - Rg;
                double dR = Math.Exp(-h / RayleighH), dM = Math.Exp(-h / MieH), dO = OzoneDensity(h);
                var m = bM * dM;
                double sr = bRr * dR + m + oR * dO, sg = bRg * dR + m + oG * dO, sb = bRb * dR + m + oB * dO;
                double vr = Math.Exp(-(tr + 0.5 * sr * ds)), vg = Math.Exp(-(tg + 0.5 * sg * ds)), vb = Math.Exp(-(tb + 0.5 * sb * ds));
                tr += sr * ds; tg += sg * ds; tb += sb * ds;
                var idx = k * Directions + d;
                _ux[idx] = (float)(px / r); _uy[idx] = (float)(py / r); _uz[idx] = (float)(pz / r); _uh[idx] = HeightCoord((float)h);
                _arR[idx] = (float)(vr * dR * ds); _arG[idx] = (float)(vg * dR * ds); _arB[idx] = (float)(vb * dR * ds);
                _amR[idx] = (float)(vr * dM * ds); _amG[idx] = (float)(vg * dM * ds); _amB[idx] = (float)(vb * dM * ds);
            }
        }
        _builtHaze = haze;
        LutBuilds++;
    }

    /// <summary>LUT 쌍선형 조회(무할당).</summary>
    private Vector3 Lookup(float hc, float mu)
    {
        var mc = (Math.Clamp(mu, -1f, 1f) + 1f) * 0.5f * (LutMu - 1);
        int i0 = Math.Min((int)hc, LutH - 2), j0 = Math.Min((int)mc, LutMu - 2);
        float fi = hc - i0, fj = mc - j0;
        int a = i0 * LutMu + j0, b = a + LutMu;
        float w00 = (1 - fi) * (1 - fj), w01 = (1 - fi) * fj, w10 = fi * (1 - fj), w11 = fi * fj;
        return new Vector3(
            _lutR[a] * w00 + _lutR[a + 1] * w01 + _lutR[b] * w10 + _lutR[b + 1] * w11,
            _lutG[a] * w00 + _lutG[a + 1] * w01 + _lutG[b] * w10 + _lutG[b + 1] * w11,
            _lutB[a] * w00 + _lutB[a + 1] * w01 + _lutB[b] * w10 + _lutB[b + 1] * w11);
    }

    /// <summary>고도 h(m) · 천정 코사인 mu → 대기 투과율(행성 그림자 0).</summary>
    public Vector3 Transmittance(float h, float mu) { lock (_gate) return Lookup(HeightCoord(h), mu); }

    /// <summary>광원 1개 단일 산란을 하늘 그리드에 누적(무할당 · SIMD 내적/누적).</summary>
    private void Scatter(Vector3 l, Vector3 e)
    {
        _aRr.AsSpan().Clear(); _aRg.AsSpan().Clear(); _aRb.AsSpan().Clear(); _aMr.AsSpan().Clear(); _aMg.AsSpan().Clear(); _aMb.AsSpan().Clear();
        for (var k = 0; k < Steps; k++)
        {
            var o = k * Directions;
            EnvSimd.Dot3(_ux.AsSpan(o, Directions), _uy.AsSpan(o, Directions), _uz.AsSpan(o, Directions), l.X, l.Y, l.Z, _mu);
            for (var d = 0; d < Directions; d++)
            {
                var t = Lookup(_uh[o + d], _mu[d]);
                _tR[d] = t.X; _tG[d] = t.Y; _tB[d] = t.Z;
            }
            EnvSimd.MulAcc(_aRr, _arR.AsSpan(o, Directions), _tR); EnvSimd.MulAcc(_aRg, _arG.AsSpan(o, Directions), _tG); EnvSimd.MulAcc(_aRb, _arB.AsSpan(o, Directions), _tB);
            EnvSimd.MulAcc(_aMr, _amR.AsSpan(o, Directions), _tR); EnvSimd.MulAcc(_aMg, _amG.AsSpan(o, Directions), _tG); EnvSimd.MulAcc(_aMb, _amB.AsSpan(o, Directions), _tB);
        }
        var bm = MieScattering * Haze;
        for (var d = 0; d < Directions; d++)
        {
            var c = _dx[d] * l.X + _dy[d] * l.Y + _dz[d] * l.Z;
            float pr = RayleighPhase(c), pm = MiePhase(c, MieG) * bm;
            SkyR[d] += e.X * (RayleighScattering.X * pr * _aRr[d] + pm * _aMr[d]);
            SkyG[d] += e.Y * (RayleighScattering.Y * pr * _aRg[d] + pm * _aMg[d]);
            SkyB[d] += e.Z * (RayleighScattering.Z * pr * _aRb[d] + pm * _aMb[d]);
        }
    }

    /// <summary>
    /// 1프레임 갱신(무할당 · 연무 변경 0.05 초과 시에만 LUT 재구축): 태양/달 → 하늘 산란 → 구름 블렌드 → 직사/환경 조도 · 지평선/안개 색 · 가시거리 · 자동 노출.
    /// </summary>
    public void Update(Vector3 sun, Vector3 moon, float moonPhase, float cloudCoverage, float cloudTau, float fogDensity, float haze, float dt)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Haze = Math.Clamp(haze, 0.2f, 12f); CloudCoverage = Math.Clamp(cloudCoverage, 0f, 1f); CloudOpticalDepth = Math.Max(0f, cloudTau);
            FogDensity = Math.Clamp(fogDensity, 0f, 0.2f); MoonPhase = moonPhase - MathF.Floor(moonPhase);
            SunDirection = Vector3.Normalize(sun); MoonDirection = Vector3.Normalize(moon);
            if (MathF.Abs(Haze - _builtHaze) > 0.05f) BuildTables(Haze);

            SkyR.AsSpan().Clear(); SkyG.AsSpan().Clear(); SkyB.AsSpan().Clear();
            var moonFrac = 0.5f * (1f - MathF.Cos(MathF.Tau * MoonPhase));
            var moonE = new Vector3(MoonIrradiance * moonFrac);
            if (SunDirection.Y > -0.3f) Scatter(SunDirection, Vector3.One);
            if (MoonDirection.Y > -0.05f && moonFrac > 0.01f) Scatter(MoonDirection, moonE);

            var hc0 = HeightCoord(CameraAltitude);
            var cloudDirect = (1f - CloudCoverage) + CloudCoverage * MathF.Exp(-CloudOpticalDepth);
            var sunT = Lookup(hc0, SunDirection.Y); var moonT = Lookup(hc0, MoonDirection.Y);
            SunIrradiance = sunT * cloudDirect;
            MoonLight = moonT * moonE * cloudDirect;
            // 2-스트림 확산 투과(g = 0.85) → 흐린 하늘 = 구름 위 수평 조도 · T / π (회색 람베르트)
            var tDiff = 1f / (1f + 0.75f * CloudOpticalDepth * (1f - 0.85f));
            var above = Luminance(sunT) * MathF.Max(SunDirection.Y, 0f) + Luminance(moonT) * moonE.X * MathF.Max(MoonDirection.Y, 0f);
            var overcast = OvercastTint * (above * tDiff / MathF.PI);
            var cov = CloudCoverage * CloudCoverage * (3f - 2f * CloudCoverage);
            Vector3 amb = Vector3.Zero, hor = Vector3.Zero;
            for (var d = 0; d < Directions; d++)
            {
                var s = new Vector3(SkyR[d], SkyG[d], SkyB[d]) + NightGlow;
                s += (overcast - s) * cov;
                SkyR[d] = s.X; SkyG[d] = s.Y; SkyB[d] = s.Z;
                amb += s * _cosW[d];
                if (d < NAz) hor += s;
            }
            AmbientIrradiance = amb;
            HorizonColor = hor / NAz;
            var zen = Vector3.Zero;
            for (var d = (NEl - 1) * NAz; d < Directions; d++) zen += new Vector3(SkyR[d], SkyG[d], SkyB[d]);
            ZenithColor = zen / NAz;

            // 높이 안개: 지상 소광(Rayleigh + Mie·연무 + 안개 입자) → Koschmieder 가시거리 3.912/σ
            var sigmaClear = Luminance(RayleighScattering) + (MieScattering + MieAbsorption) * Haze;
            Visibility = 3.912f / (sigmaClear + FogDensity);
            var fogMix = FogDensity / (FogDensity + sigmaClear * 50f);
            var lit = AmbientIrradiance / MathF.PI + (SunIrradiance * MathF.Max(SunDirection.Y, 0f) + MoonLight * MathF.Max(MoonDirection.Y, 0f)) * (0.25f / MathF.PI);
            FogColor = HorizonColor + (lit - HorizonColor) * fogMix;

            // 볼류메트릭 광선(갓 레이): 부분 구름(경계) × 연무 × 태양 가시성
            GodRayStrength = Math.Clamp(4f * CloudCoverage * (1f - CloudCoverage) * MathF.Min(1f, 0.25f + 0.25f * Haze + FogDensity * 40f) * MathF.Min(1f, MathF.Max(SunDirection.Y, 0f) * 6f) * Luminance(sunT), 0f, 1f);

            var horiz = Luminance(AmbientIrradiance) + Luminance(SunIrradiance) * MathF.Max(SunDirection.Y, 0f) + Luminance(MoonLight) * MathF.Max(MoonDirection.Y, 0f);
            GroundLux = horiz * SunLux;
            var target = MathF.Log(0.18f * MathF.PI / MathF.Max(horiz, 2e-6f));
            _logExposure += (target - _logExposure) * MathF.Min(1f, dt * 2f);
            Exposure = MathF.Exp(_logExposure);
            NightFactor = Math.Clamp((-SunDirection.Y - 0.02f) / 0.13f, 0f, 1f);
            LastUpdateMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    /// <summary>방향 → 하늘 복사휘도(그리드 쌍선형 · 지평선 아래 = 지평선 행).</summary>
    public Vector3 SampleSky(Vector3 dir)
    {
        var el = MathF.Asin(Math.Clamp(dir.Y, 0f, 1f));
        var v = MathF.Sqrt(el / (MathF.PI * 0.5f)) * NEl - 0.5f;
        var az = MathF.Atan2(dir.X, dir.Z); if (az < 0) az += MathF.Tau;
        var u = az / MathF.Tau * NAz - 0.5f;
        var j0 = (int)MathF.Floor(v); var fj = v - j0; var i0 = (int)MathF.Floor(u); var fi = u - i0;
        int ja = Math.Clamp(j0, 0, NEl - 1), jb = Math.Clamp(j0 + 1, 0, NEl - 1);
        int ia = ((i0 % NAz) + NAz) % NAz, ib = (ia + 1) % NAz;
        return Vector3.Lerp(Vector3.Lerp(Sky(ja * NAz + ia), Sky(ja * NAz + ib), fi), Vector3.Lerp(Sky(jb * NAz + ia), Sky(jb * NAz + ib), fi), Math.Clamp(fj, 0f, 1f));
    }

    /// <summary>공중 원근: 거리별 투과 T = exp(−σd)를 채널 배열에 SIMD 합성(t 스크래치 = 호출자 버퍼).</summary>
    public void ApplyAerial(Span<float> r, Span<float> g, Span<float> b, ReadOnlySpan<float> distance, Span<float> t)
    {
        var sigma = Luminance(RayleighScattering) + (MieScattering + MieAbsorption) * Haze + FogDensity;
        for (var i = 0; i < t.Length; i++) t[i] = MathF.Exp(-sigma * distance[i]);
        var f = FogColor;
        EnvSimd.Composite(r, t, f.X); EnvSimd.Composite(g, t, f.Y); EnvSimd.Composite(b, t, f.Z);
    }

    /// <summary>복사휘도 → 표시 sRGB(노출 · 야간 퍼킨제 색조 · Reinhard · γ 2.2).</summary>
    public Vector3 ToDisplay(Vector3 radiance)
    {
        var x = radiance * Exposure;
        if (NightFactor > 0f) x += (ScotopicTint * Luminance(x) - x) * (0.7f * NightFactor);
        x = Vector3.Max(x, Vector3.Zero);
        x /= Vector3.One + x;
        return new Vector3(MathF.Pow(x.X, 1f / 2.2f), MathF.Pow(x.Y, 1f / 2.2f), MathF.Pow(x.Z, 1f / 2.2f));
    }
}
