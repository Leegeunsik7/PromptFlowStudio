using System.Numerics;

namespace PromptFlowStudio.Services.SkinOptics;

/// <summary>
/// [PromptFlow Studio v17.17.0] 👁️ 각막 이중 굴절 &amp; 눈물막 광학 습윤성 가드(무할당).
///   ▸ 안구 광학(굴스트랜드 근사 · mm · 각막 정점 원점 · +Z = 관찰자): 전면 각막 구 R 7.8 · 두께 0.55 · 후면 구 R 6.5 · 각막 n 1.376 · 방수 n 1.336 ·
///     홍채 평면 z = −3.6 (반경 6) · 동공 반경 1.5–4 mm(조도 · 각성) · 공막 구 R 12(윤부 반경 5.8 에서 각막과 접합).
///   ▸ 광선 추적: 공기 → 전면 각막(Snell · 프레넬 반사) → 후면 각막 → 방수 → 홍채 평면. 굴절 없는 직진 대비 홍채/동공 겉보기 확대(≈ 1.13) · 시선 경사 시 홍채 시차 이동.
///     전반사(1.376 → 1.336 경계 · 림부 근처 그레이징) = 반사 처리 · 계수 · NaN 차단.
///   ▸ 눈물막: 깜빡임 직후 두께 7 µm → 증발 0.35 µm/s 박화 · 파열(&lt; 3 µm) 시 거칠기 증가 → 가드: 2 µm 미만이면 깜빡임 요청 · 하한 클램프 · 건조 이벤트 계수.
///     메니스커스: 위/아래 눈꺼풀 경계 원통형 수막(반경 0.3 mm · n 1.336) 스펙큘러 = 프레넬 × GGX(거칠기 = 눈물막 상태).
/// </summary>
public sealed class CornealRefractionTearFilmOpticsGuard
{
    public const float NCornea = 1.376f, NAqueous = 1.336f, RAnt = 7.8f, RPost = 6.5f, CorneaThick = 0.55f, IrisZ = -3.6f, IrisR = 6f, LimbusR = 5.8f, ScleraR = 12f;
    public const int Img = 96;
    public const float ViewMm = 26f, CamDist = 300f, MeniscusR = 0.3f;
    public static readonly float ScleraCz = -2.585f - MathF.Sqrt(ScleraR * ScleraR - LimbusR * LimbusR);
    public static readonly float LimbusZ = -RAnt + MathF.Sqrt(RAnt * RAnt - LimbusR * LimbusR);

    public readonly float[] ImgR = new float[Img * Img], ImgG = new float[Img * Img], ImgB = new float[Img * Img];
    public readonly byte[] Overlay = new byte[Img * Img];   // 1 = 굴절 홍채 윤곽 · 2 = 직진 홍채 윤곽 · 3 = 메니스커스

    public bool RefractionEnabled { get; set; } = true;
    public bool TearFilmGuard { get; set; } = true;
    public float PupilRadius { get; private set; } = 2.5f;
    public float TearFilmUm { get; private set; } = 7f;
    public float TearRoughness { get; private set; } = 0.05f;
    public float SinceBlinkS { get; private set; }
    public bool BlinkRequested { get; private set; }
    public long DryEvents { get; private set; }
    public long BlinksSeen { get; private set; }
    public float Magnification { get; private set; } = 1f;
    public float ParallaxMm { get; private set; }
    public int TirCount { get; private set; }
    public int NanCount { get; private set; }
    public float MeniscusPeak { get; private set; }
    public float EvaporationUmS { get; set; } = 0.35f;
    /// <summary>메니스커스 하이라이트 이득(곡면 수막 · 넓은 GGX 로브 = 0.15 + 눈물막 거칠기).</summary>
    public float MeniscusGain { get; set; } = 4f;
    public double LastRenderUs { get; private set; }
    public double LastMeasureUs { get; private set; }
    public Vector3 LightDir { get; set; } = Vector3.Normalize(new Vector3(0.4f, 0.45f, 0.8f));
    private bool _blinkHigh;

    // ══════════════════════ 광학 기본 ══════════════════════

    /// <summary>Snell 굴절(d · n 단위 · n 은 입사측 반대 · eta = n1/n2). 전반사면 false.</summary>
    public static bool Refract(Vector3 d, Vector3 n, float eta, out Vector3 t)
    {
        var cosi = -Vector3.Dot(n, d);
        if (cosi < 0f) { n = -n; cosi = -cosi; }
        var k = 1f - eta * eta * (1f - cosi * cosi);
        if (k < 0f) { t = d - 2f * Vector3.Dot(d, n) * n; return false; }
        t = Vector3.Normalize(eta * d + (eta * cosi - MathF.Sqrt(k)) * n);
        return true;
    }

    public static float Fresnel(float n1, float n2, float cosi)
    {
        cosi = Math.Clamp(MathF.Abs(cosi), 0f, 1f);
        var eta = n1 / n2;
        var s2 = eta * eta * (1f - cosi * cosi);
        if (s2 >= 1f) return 1f;
        var cost = MathF.Sqrt(1f - s2);
        float rs = (n1 * cosi - n2 * cost) / (n1 * cosi + n2 * cost), rp = (n2 * cosi - n1 * cost) / (n2 * cosi + n1 * cost);
        return 0.5f * (rs * rs + rp * rp);
    }

    /// <summary>광선–구 교차(가까운 양의 t · 없으면 false). far = 먼 교점.</summary>
    public static bool Sphere(Vector3 o, Vector3 d, Vector3 c, float r, bool far, out float t)
    {
        var oc = o - c;
        var b = Vector3.Dot(oc, d);
        var q = Vector3.Dot(oc, oc) - r * r;
        var disc = b * b - q;
        t = 0f;
        if (disc < 0f) return false;
        var s = MathF.Sqrt(disc);
        float t0 = -b - s, t1 = -b + s;
        t = far ? t1 : t0 > 1e-5f ? t0 : t1;
        return t > 1e-5f;
    }

    /// <summary>각막 → 방수 → 홍채 평면 추적. 반환: 홍채 평면 교점(굴절 없으면 직진) · 각막 진입 여부 · 전면 프레넬 · 전반사 여부.</summary>
    public bool TraceToIris(Vector3 o, Vector3 d, bool refract, out Vector3 hit, out float fresnel, out bool tir)
    {
        hit = default; fresnel = 0f; tir = false;
        var ca = new Vector3(0f, 0f, -RAnt);
        if (!Sphere(o, d, ca, RAnt, false, out var t)) return false;
        var p = o + d * t;
        if (p.Z < LimbusZ) return false;
        var n = Vector3.Normalize(p - ca);
        fresnel = Fresnel(1f, NCornea, Vector3.Dot(n, d));
        if (refract)
        {
            Refract(d, n, 1f / NCornea, out d);
            var cp = new Vector3(0f, 0f, -CorneaThick - RPost);
            if (Sphere(p, d, cp, RPost, false, out var t2))
            {
                var p2 = p + d * t2;
                var n2 = Vector3.Normalize(p2 - cp);
                if (!Refract(d, n2, NCornea / NAqueous, out var d2)) tir = true;
                d = d2; p = p2;
            }
        }
        if (d.Z >= -1e-6f) return false;
        var s = (IrisZ - p.Z) / d.Z;
        hit = p + d * s;
        return true;
    }

    // ══════════════════════ 동공 · 눈물막 ══════════════════════

    /// <summary>동공(조도 lux · 각성 0..1): 밝을수록 축소 · 각성(교감) 확대.</summary>
    public void SetPupil(float lux, float arousal)
    {
        var r = 4f - 2.2f * Math.Clamp(MathF.Log10(MathF.Max(1f, lux)) / 4f, 0f, 1f) + 0.8f * Math.Clamp(arousal, 0f, 1f);
        PupilRadius = Math.Clamp(r, 1.5f, 4f);
    }

    /// <summary>눈물막 증발/깜빡임 갱신(blink = 0..1 눈꺼풀 닫힘) → 거칠기 · 가드.</summary>
    public void UpdateTearFilm(float blink, float dt)
    {
        var closed = blink > 0.6f;
        if (closed && !_blinkHigh) { BlinksSeen++; TearFilmUm = 7f; SinceBlinkS = 0f; BlinkRequested = false; }
        _blinkHigh = closed;
        if (!closed)
        {
            SinceBlinkS += dt;
            TearFilmUm -= EvaporationUmS * dt;
            if (TearFilmGuard && TearFilmUm < 2f)
            {
                if (!BlinkRequested) DryEvents++;
                BlinkRequested = true;
                TearFilmUm = 2f;
            }
            if (TearFilmUm < 0.5f) TearFilmUm = 0.5f;
        }
        var breakup = Math.Clamp((3f - TearFilmUm) / 2.5f, 0f, 1f);
        TearRoughness = 0.04f + 0.3f * breakup + 0.02f * (7f - MathF.Min(7f, TearFilmUm)) / 7f;
    }

    private static float Ggx(float nh, float a)
    {
        var a2 = a * a;
        var d = nh * nh * (a2 - 1f) + 1f;
        return a2 / (MathF.PI * d * d);
    }

    // ══════════════════════ 계측 · 렌더 ══════════════════════

    /// <summary>축상 겉보기 동공 확대(정점 평면 입사 높이 / 실제 반경 · 이분 탐색) · 30° 경사 홍채 중심 시차(mm).</summary>
    public void Measure()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        float lo = 0.01f, hi = LimbusR - 0.05f;
        var d = -Vector3.UnitZ;
        for (var it = 0; it < 40; it++)
        {
            var mid = 0.5f * (lo + hi);
            if (!TraceToIris(new Vector3(mid, 0f, 20f), d, true, out var h, out _, out _)) { hi = mid; continue; }
            if (h.X < PupilRadius) lo = mid; else hi = mid;
        }
        Magnification = 0.5f * (lo + hi) / PupilRadius;
        // 30° 경사: 홍채 중심을 지나는 직진 광선 대비 굴절 광선의 홍채 교점 차
        var a = 30f * MathF.PI / 180f;
        var dir = Vector3.Normalize(new Vector3(-MathF.Sin(a), 0f, -MathF.Cos(a)));
        var o = new Vector3(0f, 0f, IrisZ) - dir * (20f - IrisZ);   // 직진 시 홍채 중심 통과
        ParallaxMm = TraceToIris(o, dir, true, out var hr, out _, out _) ? hr.X : 0f;
        LastMeasureUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static Vector3 IrisColor(float r, float th, Vector3 tint)
    {
        var fib = 0.55f + 0.45f * MathF.Abs(MathF.Sin(th * 37f + 3f * MathF.Sin(r * 2.1f)));
        var ring = r < 2.6f ? 0.7f : 1f;
        var limb = r > IrisR - 0.6f ? 0.45f : 1f;
        return tint * (fib * ring * limb);
    }

    /// <summary>안구 뷰(96² · 시선 요/피치 · 눈꺼풀 깜빡임) 렌더 + 오버레이(무할당).</summary>
    public void Render(float yawDeg, float pitchDeg, float blink, Vector3 irisTint, float scleraRedness)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var rot = Quaternion.CreateFromYawPitchRoll(-yawDeg * MathF.PI / 180f, pitchDeg * MathF.PI / 180f, 0f);
        var inv = Quaternion.Conjugate(rot);
        var camPos = Vector3.Transform(new Vector3(0f, 0f, CamDist), inv);
        var light = Vector3.Transform(Vector3.Normalize(LightDir), inv);
        int tir = 0, nan = 0;
        var menPeak = 0f;
        var open = 1f - Math.Clamp(blink, 0f, 1f);
        for (var py = 0; py < Img; py++)
            for (var px = 0; px < Img; px++)
            {
                var i = py * Img + px;
                float sx = ((px + 0.5f) / Img - 0.5f) * ViewMm, sy = (0.5f - (py + 0.5f) / Img) * ViewMm;
                Overlay[i] = 0;
                // 눈꺼풀(화면 공간 · 아몬드형) — 위 눈꺼풀이 닫힘에 따라 내려옴
                var lidHalf = 10.5f * MathF.Sqrt(MathF.Max(0f, 1f - (sx / 14f) * (sx / 14f)));
                float upper = -lidHalf * 0.05f + lidHalf * (0.95f * open), lower = -lidHalf * 0.75f;
                if (sy > upper || sy < lower)
                {
                    ImgR[i] = 0.78f; ImgG[i] = 0.55f; ImgB[i] = 0.47f;
                    continue;
                }
                var target = Vector3.Transform(new Vector3(sx, sy, 0f), inv);
                var o = camPos; var d = Vector3.Normalize(target - camPos);
                Vector3 col;
                if (TraceToIris(o, d, RefractionEnabled, out var hit, out var fr, out var isTir))
                {
                    if (isTir) tir++;
                    var r = MathF.Sqrt(hit.X * hit.X + hit.Y * hit.Y);
                    col = r < PupilRadius ? new Vector3(0.02f) : r < IrisR ? IrisColor(r, MathF.Atan2(hit.Y, hit.X), irisTint) : new Vector3(0.85f, 0.82f, 0.8f);
                    if (MathF.Abs(r - IrisR) < 0.18f || MathF.Abs(r - PupilRadius) < 0.12f) Overlay[i] = 1;
                    // 직진(무굴절) 윤곽
                    if (TraceToIris(o, d, false, out var h0, out _, out _))
                    {
                        var r0 = MathF.Sqrt(h0.X * h0.X + h0.Y * h0.Y);
                        if (MathF.Abs(r0 - IrisR) < 0.15f || MathF.Abs(r0 - PupilRadius) < 0.1f) Overlay[i] = 2;
                    }
                    // 각막 반사(눈물막 거칠기 GGX)
                    Sphere(o, d, new Vector3(0f, 0f, -RAnt), RAnt, false, out var tc);
                    var n = Vector3.Normalize(o + d * tc - new Vector3(0f, 0f, -RAnt));
                    var hv = Vector3.Normalize(light - d);
                    var spec = fr * Ggx(MathF.Max(0f, Vector3.Dot(n, hv)), TearRoughness) * 0.25f;
                    col = col * (1f - fr) + new Vector3(spec);
                }
                else
                {
                    // 공막(혈관 홍조 반영)
                    var sc = new Vector3(0f, 0f, ScleraCz);
                    if (Sphere(o, d, sc, ScleraR, false, out var ts))
                    {
                        var p = o + d * ts;
                        var n = Vector3.Normalize(p - sc);
                        var diff = 0.35f + 0.65f * MathF.Max(0f, Vector3.Dot(n, light));
                        var vein = MathF.Max(0f, MathF.Sin(MathF.Atan2(p.Y, p.X) * 9f + p.Z)) * 0.15f * (0.3f + scleraRedness);
                        col = new Vector3(0.92f, 0.88f - vein, 0.86f - vein) * diff;
                    }
                    else col = new Vector3(0.1f);
                }
                // 메니스커스(눈꺼풀 경계 원통 수막)
                var du = MathF.Min(MathF.Abs(sy - upper), MathF.Abs(sy - lower));
                if (du < MeniscusR * 2.2f)
                {
                    var side = MathF.Abs(sy - upper) < MathF.Abs(sy - lower) ? -1f : 1f;
                    var u = Math.Clamp(du / (MeniscusR * 2.2f), 0f, 1f);
                    var nm = Vector3.Normalize(new Vector3(0f, side * MathF.Cos(u * MathF.PI * 0.5f), MathF.Sin(u * MathF.PI * 0.5f) + 0.2f));
                    var hv = Vector3.Normalize(Vector3.Normalize(LightDir) + Vector3.UnitZ);
                    var m = MeniscusGain * Fresnel(1f, NAqueous, Vector3.Dot(nm, Vector3.UnitZ)) * Ggx(MathF.Max(0f, Vector3.Dot(nm, hv)), 0.15f + TearRoughness) * (TearFilmUm / 7f);
                    if (m > menPeak) menPeak = m;
                    col += new Vector3(m);
                    if (m > 0.08f) Overlay[i] = 3;
                }
                if (!float.IsFinite(col.X) || !float.IsFinite(col.Y) || !float.IsFinite(col.Z)) { nan++; col = new Vector3(0.1f); }
                ImgR[i] = col.X; ImgG[i] = col.Y; ImgB[i] = col.Z;
            }
        TirCount = tir; NanCount = nan; MeniscusPeak = menPeak;
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
