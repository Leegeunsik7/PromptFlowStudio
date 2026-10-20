using System.Numerics;
using PromptFlowStudio.Services.Atmosphere;

namespace PromptFlowStudio.Services.StrandHair;

/// <summary>
/// [PromptFlow Studio v17.15.0] ✨ 마슈너(Marschner) 미세 산란 쉐이딩 &amp; 기류 클럼핑 합성기(<see cref="Apply"/> · <see cref="Evaluate"/> 무할당).
///   ▸ 쉐이딩 S(θi, θr, φ) = Σₚ Mₚ(θh) · Nₚ(φ, θd) / cos²θd · p ∈ {R, TT, TRT}.
///     종방향 Mₚ = 정규 가우스(θh − αₚ; βₚ) · 큐티클 기울기 α: α_R = −2α · α_TT = α · α_TRT = 3α · 폭 β_TT = β/2 · β_TRT = 2β.
///     방위 N_R = ¼|cos ½φ|·F(η, cosθd·cos ½φ) · N_TT = (1 − F)²·T·g(φ − π; γ_TT) · N_TRT = (1 − F)²·F·T²·¼|cos ½φ| (+ 글린트 ≈ ±(π − 0.6) 협대역).
///     흡수 T = exp(−σa·2/cosθt) (Snell sinθt = sinθd/η) · σa = 유멜라닌·(0.419, 0.697, 1.37) + 페오멜라닌·(0.187, 0.4, 1.05)(Chiang 2016).
///   ▸ 기류: v16.84 <see cref="DynamicMicroWeatherSimulatorEngine"/> 바람장(머리 높이) → 64 프레임 링버퍼 → 가이드별 풍하 거리 지연(진행 돌풍) →
///     항력 a = k·(w − v)⊥ · 노출 √t(접선 성분 제거).
///   ▸ 정전기 반발: 모근 최근접 6 이웃 가이드 쌍 · 3 cm 이내 선형 반발 · 습도 ↑ → 뭉침 강도 ↑ · 정전기 ↑ → 뭉침 ↓ · 잔머리 확산 ↑.
/// </summary>
public sealed class MarschnerHairScatteringAerodynamicsSynthesizer
{
    public const int Ring = 64, Neighbors = 6;
    public const float Deg = MathF.PI / 180f, RepelRadius = 0.03f;
    private static readonly Vector3 EuSigma = new(0.419f, 0.697f, 1.37f), PheoSigma = new(0.187f, 0.4f, 1.05f);

    private readonly DynamicMicroWeatherSimulatorEngine _weather = new(0x1715_A1E);
    private readonly Vector3[] _ring = new Vector3[Ring];
    private int[] _nbr = Array.Empty<int>();
    private float[] _delay = Array.Empty<float>();
    private int _ringHead, _ringCount;

    public MarschnerHairScatteringAerodynamicsSynthesizer()
    {
        var s = WeatherScenario.Clear;
        s.WindSpeed = 3f; s.WindYawDeg = 90f; s.Humidity = 0.45f;
        _weather.SetScenario(s);
    }

    // ── 쉐이딩 파라미터 ──
    public float CuticleTiltDeg { get; set; } = 3f;
    public float RoughnessDeg { get; set; } = 7f;
    public float Eumelanin { get; set; } = 1.3f;
    public float Pheomelanin { get; set; } = 0.2f;
    public float Eta { get; set; } = 1.55f;
    public float SpecularGain { get; set; } = 1f;
    public float GlintGain { get; set; } = 0.4f;
    // ── 기류 · 그루밍 파라미터 ──
    public float WindResponse { get; set; } = 1f;
    public float DragK { get; set; } = 2.5f;
    public float Static { get; set; } = 0.2f;
    public float ClumpBase { get; set; } = 0.5f;
    public float FrizzBase { get; set; } = 0.002f;
    public bool WindEnabled { get; set; } = true;

    public DynamicMicroWeatherSimulatorEngine Weather => _weather;
    public Vector3 SigmaA => Eumelanin * EuSigma + Pheomelanin * PheoSigma;
    public float Humidity => _weather.State.Humidity;
    public Vector3 WindAtHead { get; private set; }
    public float GustRms { get; private set; }
    public float MaxDragAccel { get; private set; }
    public int RepelPairs { get; private set; }
    public float MeanTipSpreadMm { get; private set; }
    public double LastAeroUs { get; private set; }
    public Vector3 RingSample(int back) => _ring[(_ringHead - 1 - back + 2 * Ring) % Ring];
    public int RingCount => _ringCount;

    public void SetWeather(float speed, float yawDeg, float humidity)
    {
        var s = _weather.Target;
        s.WindSpeed = MathF.Max(0f, speed); s.WindYawDeg = yawDeg; s.Humidity = Math.Clamp(humidity, 0f, 1f);
        _weather.SetScenario(s);
    }

    // ══════════════════════ Marschner ══════════════════════

    public static float Gauss(float x, float sigma) => MathF.Exp(-x * x / (2f * sigma * sigma)) / (2.5066283f * sigma);

    /// <summary>유전체 프레넬(비편광).</summary>
    public static float Fresnel(float eta, float cosI)
    {
        cosI = Math.Clamp(MathF.Abs(cosI), 0f, 1f);
        var sinT2 = (1f - cosI * cosI) / (eta * eta);
        if (sinT2 >= 1f) return 1f;
        var cosT = MathF.Sqrt(1f - sinT2);
        var rs = (cosI - eta * cosT) / (cosI + eta * cosT);
        var rp = (eta * cosI - cosT) / (eta * cosI + cosT);
        return 0.5f * (rs * rs + rp * rp);
    }

    public float ShiftR => -2f * CuticleTiltDeg * Deg;
    public float ShiftTT => CuticleTiltDeg * Deg;
    public float ShiftTRT => 3f * CuticleTiltDeg * Deg;
    public float WidthR => MathF.Max(0.5f, RoughnessDeg) * Deg;

    public float LongitudinalR(float thetaH) => Gauss(thetaH - ShiftR, WidthR);
    public float LongitudinalTT(float thetaH) => Gauss(thetaH - ShiftTT, WidthR * 0.5f);
    public float LongitudinalTRT(float thetaH) => Gauss(thetaH - ShiftTRT, WidthR * 2f);

    private Vector3 Transmit(float thetaD)
    {
        var sinT = MathF.Sin(thetaD) / Eta;
        var cosT = MathF.Sqrt(MathF.Max(1e-4f, 1f - sinT * sinT));
        var s = SigmaA * (2f / cosT);
        return new Vector3(MathF.Exp(-s.X), MathF.Exp(-s.Y), MathF.Exp(-s.Z));
    }

    public float AzimuthalR(float phi, float thetaD)
    {
        var c = MathF.Abs(MathF.Cos(0.5f * phi));
        return 0.25f * c * Fresnel(Eta, MathF.Cos(thetaD) * c);
    }

    public Vector3 AzimuthalTT(float phi, float thetaD)
    {
        var f = Fresnel(Eta, MathF.Cos(thetaD));
        var dphi = MathF.IEEERemainder(phi - MathF.PI, MathF.Tau);
        return (1f - f) * (1f - f) * Gauss(dphi, 0.35f) * Transmit(thetaD);
    }

    public Vector3 AzimuthalTRT(float phi, float thetaD)
    {
        var f = Fresnel(Eta, MathF.Cos(thetaD));
        var t = Transmit(thetaD);
        var c = MathF.Abs(MathF.Cos(0.5f * phi));
        var a = MathF.Abs(MathF.IEEERemainder(phi, MathF.Tau));
        var glint = GlintGain * Gauss(a - (MathF.PI - 0.6f), 0.12f);
        return (1f - f) * (1f - f) * f * (0.25f * c + glint) * (t * t);
    }

    /// <summary>BSDF 값(θ = 법선 평면 대비 각 · φ = 방위 차 · 라디안). 로브별 출력 포함.</summary>
    public Vector3 Evaluate(float thetaI, float thetaR, float phi, out Vector3 r, out Vector3 tt, out Vector3 trt)
    {
        float th = 0.5f * (thetaI + thetaR), td = 0.5f * (thetaR - thetaI);
        var inv = 1f / MathF.Max(0.05f, MathF.Cos(td) * MathF.Cos(td));
        r = new Vector3(SpecularGain * LongitudinalR(th) * AzimuthalR(phi, td) * inv);
        tt = LongitudinalTT(th) * AzimuthalTT(phi, td) * inv;
        trt = SpecularGain * LongitudinalTRT(th) * AzimuthalTRT(phi, td) * inv;
        return r + tt + trt;
    }

    /// <summary>스트랜드 접선 T · 광원 L · 시선 V(단위) → 산란 라디언스(광원 색 · 코사인 포함).</summary>
    public Vector3 Shade(Vector3 t, Vector3 l, Vector3 v, Vector3 lightColor)
    {
        float si = Math.Clamp(Vector3.Dot(l, t), -1f, 1f), sr = Math.Clamp(Vector3.Dot(v, t), -1f, 1f);
        float ti = MathF.Asin(si), tr = MathF.Asin(sr);
        var lp = l - t * si; var vp = v - t * sr;
        float ll = lp.Length(), vl = vp.Length();
        var phi = ll > 1e-5f && vl > 1e-5f ? MathF.Acos(Math.Clamp(Vector3.Dot(lp, vp) / (ll * vl), -1f, 1f)) : 0f;
        var s = Evaluate(ti, tr, phi, out _, out _, out _);
        return s * MathF.Cos(ti) * lightColor;
    }

    /// <summary>다중 산란 근사 기저색(흡수 → 반사 알베도).</summary>
    public Vector3 BaseColor
    {
        get
        {
            var s = SigmaA;
            return new Vector3(MathF.Exp(-s.X * 0.9f), MathF.Exp(-s.Y * 0.9f), MathF.Exp(-s.Z * 0.9f));
        }
    }

    // ══════════════════════ 기류 · 정전기 · 뭉침 ══════════════════════

    /// <summary>가이드 모근 최근접 이웃 · 풍하 지연 테이블(제어 평면).</summary>
    public void Bind(StrandHairPbdDynamicsEngine e)
    {
        _nbr = new int[e.G * Neighbors];
        _delay = new float[e.G];
        Span<float> bd = stackalloc float[Neighbors];
        for (var g = 0; g < e.G; g++)
        {
            bd.Fill(float.MaxValue);
            for (var j = 0; j < Neighbors; j++) _nbr[g * Neighbors + j] = g;
            for (var o = 0; o < e.G; o++)
            {
                if (o == g) continue;
                var d = Vector3.DistanceSquared(e.RootLocal(g), e.RootLocal(o));
                for (var j = 0; j < Neighbors; j++)
                {
                    if (d >= bd[j]) continue;
                    for (var m = Neighbors - 1; m > j; m--) { bd[m] = bd[m - 1]; _nbr[g * Neighbors + m] = _nbr[g * Neighbors + m - 1]; }
                    bd[j] = d; _nbr[g * Neighbors + j] = o;
                    break;
                }
            }
        }
    }

    /// <summary>기상 스텝 → 링버퍼 → 가이드 외력(항력 + 정전기 반발) · 뭉침/확산 프로파일(무할당).</summary>
    public void Apply(StrandHairPbdDynamicsEngine e, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _weather.Step(dt);
        var w = WindEnabled ? _weather.WindAt(0f, 1.6f, 0f) * WindResponse : Vector3.Zero;
        WindAtHead = w;
        _ring[_ringHead] = w;
        _ringHead = (_ringHead + 1) % Ring;
        if (_ringCount < Ring) _ringCount++;
        double gs = 0; var mean = Vector3.Zero;
        for (var k = 0; k < _ringCount; k++) mean += RingSample(k);
        mean /= Math.Max(1, _ringCount);
        for (var k = 0; k < _ringCount; k++) gs += Vector3.DistanceSquared(RingSample(k), mean);
        GustRms = (float)Math.Sqrt(gs / Math.Max(1, _ringCount));

        int G = e.G, N = StrandHairPbdDynamicsEngine.N;
        var wl = w.Length();
        var wd = wl > 1e-4f ? w / wl : Vector3.UnitX;
        var invRot = Quaternion.Conjugate(e.HeadRot);
        var wdl = Vector3.Transform(wd, invRot);
        for (var g = 0; g < G && _delay.Length == G; g++)
        {
            var along = Vector3.Dot(e.RootLocal(g), wdl) + 0.15f;
            _delay[g] = wl > 0.2f ? Math.Clamp(along / MathF.Max(0.5f, wl) * 60f, 0f, Ring - 2) : 0f;
        }
        var h = MathF.Max(1e-4f, e.LastSubstepDt);
        var maxA = 0f;
        for (var i = 0; i < N; i++)
        {
            var expo = i < 2 ? 0f : MathF.Sqrt(i / (N - 1f)) * DragK;
            for (var g = 0; g < G; g++)
            {
                var k = i * G + g;
                if (expo <= 0f || !WindEnabled) { e.FX[k] = 0f; e.FY[k] = 0f; e.FZ[k] = 0f; continue; }
                var d = _delay.Length == G ? _delay[g] : 0f;
                var di = (int)d; var df = d - di;
                var wg = Vector3.Lerp(RingSample(Math.Min(di, _ringCount - 1)), RingSample(Math.Min(di + 1, _ringCount - 1)), df);
                var v = new Vector3(e.X[k] - e.PX[k], e.Y[k] - e.PY[k], e.Z[k] - e.PZ[k]) / h;
                int a = (i < StrandHairPbdDynamicsEngine.Seg ? i : i - 1) * G + g, b = a + G;
                var t = new Vector3(e.X[b] - e.X[a], e.Y[b] - e.Y[a], e.Z[b] - e.Z[a]);
                var tl = t.Length();
                t = tl > 1e-9f ? t / tl : Vector3.UnitY;
                var rel = wg - v;
                var acc = (rel - t * Vector3.Dot(rel, t)) * expo;
                var al = acc.Length();
                if (al > 60f) acc *= 60f / al;
                if (al > maxA) maxA = al;
                e.FX[k] = acc.X; e.FY[k] = acc.Y; e.FZ[k] = acc.Z;
            }
        }
        MaxDragAccel = maxA;

        // 정전기 반발(모끝 쪽 절반 입자 · 이웃 가이드)
        var pairs = 0;
        double spread = 0;
        if (_nbr.Length == G * Neighbors)
        {
            var ks = Static * 40f;
            for (var g = 0; g < G; g++)
            {
                for (var j = 0; j < Neighbors; j++)
                {
                    var o = _nbr[g * Neighbors + j];
                    spread += Vector3.Distance(e.P(N - 1, g), e.P(N - 1, o));
                    if (ks <= 0f) continue;
                    for (var i = N / 2; i < N; i++)
                    {
                        int ka = i * G + g, kb = i * G + o;
                        var dv = new Vector3(e.X[ka] - e.X[kb], e.Y[ka] - e.Y[kb], e.Z[ka] - e.Z[kb]);
                        var dl = dv.Length();
                        if (dl >= RepelRadius || dl < 1e-6f) continue;
                        var f = dv / dl * (ks * (1f - dl / RepelRadius));
                        e.FX[ka] += f.X; e.FY[ka] += f.Y; e.FZ[ka] += f.Z;
                        pairs++;
                    }
                }
            }
            MeanTipSpreadMm = (float)(spread / (G * Neighbors)) * 1000f;
        }
        RepelPairs = pairs;

        // 습도 · 정전기 → 뭉침/확산
        var hum = Humidity;
        e.ClumpStrength = Math.Clamp(ClumpBase * (0.4f + 1.2f * hum) * (1f - 0.6f * Static), 0f, 1f);
        e.Frizz = FrizzBase + 0.008f * Static * (1f - hum);
        e.UpdateChildProfiles();
        LastAeroUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
