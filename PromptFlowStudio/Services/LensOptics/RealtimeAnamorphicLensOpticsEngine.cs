using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Services.LensOptics;

public enum LensPreset : byte { CookeAnamorphic, KowaProminar, ZeissMasterPrime, VintageLomo }

/// <summary>렌즈 프리셋 — 왜곡 · 색수차 · 브리딩 · 플레어/보케 매개변수(정적 1회 할당).</summary>
public sealed class LensPresetSpec
{
    public required string Name { get; init; }
    public float Squeeze { get; init; }        // 1.0 구면 · 2.0 아나모픽
    public float K1 { get; init; }             // 역매핑 배럴 계수(+ = 배럴)
    public float K2 { get; init; }
    public float Ca { get; init; }             // 방사상 횡색수차(r² 당 R + / B −)
    public float Breathing { get; init; }      // 근접 초점 시 화각 축소율
    public float MinFocusM { get; init; }
    public float Vignette { get; init; }
    public float Streak { get; init; }         // 수평 스트릭 강도
    public Vector3 StreakTint { get; init; }
    public float Halation { get; init; }
    public int Blades { get; init; }
    public float BokehRim { get; init; }       // 비누방울 테두리(0 = 균일)
    public float Ghosts { get; init; }
}

/// <summary>렌즈 카메라 상태(v17.39 리그 또는 v17.41 무대 카메라 → 초점 거리 · T-스톱 · 초점).</summary>
public struct LensCamera
{
    public Vector3 Position, Forward, Right, Up;
    public float FocalMm, TStop, FocusM;
}

/// <summary>
/// [PromptFlow Studio v17.43.0] 🎥 실시간 아나모픽 렌즈 광학 수차 &amp; 왜곡 엔진(<see cref="Update"/>/<see cref="Apply"/> 무할당 · 60 fps).
///   ▸ 출력 240×100(2.4:1 시네마스코프) · 센서 Super35 4-perf 아나모픽 게이트 21.95×18.6 mm — 수직 FoV = 2·atan(18.6/2f) · 수평 = 스퀴즈 × 구면.
///   ▸ 역매핑 LUT(Vector&lt;T&gt; · 스칼라 쌍둥이 비트 동일): 타원 반경 r² = (u·e)² + v²(e = 1/(1 + 0.35(S − 1))) · 배럴 1 + k₁r² + k₂r⁴ ·
///     수평 가장자리 정규화 · 렌즈 브리딩 · 채널별 방사 색수차 1 ± ca·r² · cos⁴ 비네팅.
///   ▸ 렌즈 브리딩: 초점 근접도 min(1, MinFocus/초점)에 비례해 화각 축소 — 임계 감쇠 1차 평활(τ 0.15 s)로 포커스 풀 중 팝 방지.
///   ▸ <see cref="Apply"/>: 평면(R/G/B) HDR → 채널별 LUT 쌍선형 샘플(가장자리 클램프) × 비네팅.
/// </summary>
public sealed class RealtimeAnamorphicLensOpticsEngine
{
    public const int W = 240, H = 100, Pixels = W * H;
    public const float Aspect = (float)W / H, SensorH = 18.6f, SensorW = 21.95f, BreathTau = 0.15f;

    public static readonly LensPresetSpec[] Presets =
    {
        new() { Name = "Cooke 아나모픽/i", Squeeze = 2f, K1 = 0.05f, K2 = 0.004f, Ca = 0.004f, Breathing = 0.035f, MinFocusM = 0.9f, Vignette = 0.06f,
                Streak = 0.8f, StreakTint = new Vector3(0.35f, 0.55f, 1f), Halation = 0.35f, Blades = 11, BokehRim = 0.15f, Ghosts = 0.25f },
        new() { Name = "Kowa Prominar", Squeeze = 2f, K1 = 0.08f, K2 = 0.008f, Ca = 0.007f, Breathing = 0.05f, MinFocusM = 1.0f, Vignette = 0.1f,
                Streak = 1.1f, StreakTint = new Vector3(0.25f, 0.45f, 1f), Halation = 0.45f, Blades = 8, BokehRim = 0.3f, Ghosts = 0.45f },
        new() { Name = "Zeiss Master Prime", Squeeze = 1f, K1 = 0.01f, K2 = 0f, Ca = 0.0015f, Breathing = 0.006f, MinFocusM = 0.45f, Vignette = 0.03f,
                Streak = 0f, StreakTint = new Vector3(0.9f, 0.9f, 1f), Halation = 0.15f, Blades = 9, BokehRim = 0f, Ghosts = 0.08f },
        new() { Name = "Vintage Lomo 라운드프론트", Squeeze = 2f, K1 = 0.12f, K2 = 0.015f, Ca = 0.012f, Breathing = 0.08f, MinFocusM = 1.2f, Vignette = 0.18f,
                Streak = 1.4f, StreakTint = new Vector3(0.3f, 0.6f, 1f), Halation = 0.7f, Blades = 6, BokehRim = 0.55f, Ghosts = 0.7f },
    };

    private static readonly double TickUs = 1_000_000.0 / Stopwatch.Frequency;
    private readonly float[] _bu = new float[Pixels], _bv = new float[Pixels];
    private readonly float[] _xr = new float[Pixels], _yr = new float[Pixels], _xg = new float[Pixels], _yg = new float[Pixels], _xb = new float[Pixels], _yb = new float[Pixels], _vig = new float[Pixels];

    public RealtimeAnamorphicLensOpticsEngine()
    {
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = y * W + x;
            _bu[i] = ((x + 0.5f) / W * 2f - 1f) * Aspect;
            _bv[i] = 1f - (y + 0.5f) / H * 2f;
        }
        Configure(Presets[0], Presets[0].Squeeze);
        BreathScale = 1f;
    }

    public bool UseSimd { get; set; } = true;
    public LensPresetSpec Spec { get; private set; } = Presets[0];
    public float Squeeze { get; private set; } = 2f;
    public float BreathScale { get; private set; } = 1f;
    public float BreathTarget { get; private set; } = 1f;
    public float EdgeFit { get; private set; } = 1f;
    public float VFovDeg { get; private set; }
    public float HFovDeg { get; private set; }
    public float EffectiveVFovDeg => VFovDeg * BreathScale;
    public float TanHalfV { get; private set; }
    public float FocalMm { get; private set; } = 40f;
    public float TStop { get; private set; } = 2.8f;
    public float FocusM { get; private set; } = 4f;
    public double LastLutUs { get; private set; }
    public double LastApplyUs { get; private set; }

    public ReadOnlySpan<float> LutXr => _xr;
    public ReadOnlySpan<float> LutYr => _yr;
    public ReadOnlySpan<float> LutXg => _xg;
    public ReadOnlySpan<float> LutYg => _yg;
    public ReadOnlySpan<float> LutXb => _xb;
    public ReadOnlySpan<float> LutYb => _yb;
    public ReadOnlySpan<float> VignetteMap => _vig;

    public void Configure(LensPresetSpec spec, float squeeze)
    {
        Spec = spec;
        Squeeze = Math.Clamp(squeeze, 1f, 2f);
    }

    public static float Ellipse(float squeeze) => 1f / (1f + 0.35f * (squeeze - 1f));

    /// <summary>카메라(초점 거리 · T-스톱 · 초점) → FoV · 브리딩 평활 · LUT 재구성.</summary>
    public void Update(in LensCamera cam, float dt)
    {
        FocalMm = Math.Clamp(cam.FocalMm, 12f, 300f);
        TStop = Math.Clamp(cam.TStop, 1.3f, 22f);
        FocusM = Math.Clamp(cam.FocusM, 0.3f, 1000f);
        VFovDeg = 2f * MathF.Atan(SensorH / (2f * FocalMm)) * (180f / MathF.PI);
        HFovDeg = 2f * MathF.Atan(Squeeze * SensorW / (2f * FocalMm)) * (180f / MathF.PI);
        BreathTarget = 1f - Spec.Breathing * MathF.Min(1f, Spec.MinFocusM / FocusM);
        BreathScale += (BreathTarget - BreathScale) * (1f - MathF.Exp(-dt / BreathTau));
        TanHalfV = MathF.Tan(0.5f * EffectiveVFovDeg * MathF.PI / 180f);
        BuildLut();
    }

    /// <summary>역매핑 LUT — SIMD/스칼라 동일 연산 순서(FMA 미사용).</summary>
    public void BuildLut()
    {
        var t0 = Stopwatch.GetTimestamp();
        var e = Ellipse(Squeeze);
        var k1 = Spec.K1; var k2 = Spec.K2; var ca = Spec.Ca; var vg = Spec.Vignette;
        var re = Aspect * e; re *= re;
        EdgeFit = 1f / (1f + k1 * re + k2 * re * re);
        var m0 = EdgeFit * BreathScale;
        const float sx = 0.5f * W / Aspect, ox = 0.5f * W - 0.5f, sy = -0.5f * H, oy = 0.5f * H - 0.5f;
        int i = 0;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var ve = new Vector<float>(e); var vk1 = new Vector<float>(k1); var vk2 = new Vector<float>(k2); var vca = new Vector<float>(ca); var vvg = new Vector<float>(vg);
            var vm0 = new Vector<float>(m0); var one = Vector<float>.One;
            var vsx = new Vector<float>(sx); var vox = new Vector<float>(ox); var vsy = new Vector<float>(sy); var voy = new Vector<float>(oy);
            for (; i <= Pixels - Vector<float>.Count; i += Vector<float>.Count)
            {
                var u = new Vector<float>(_bu, i); var v = new Vector<float>(_bv, i);
                var ue = u * ve;
                var r2 = ue * ue + v * v;
                var m = (one + vk1 * r2 + vk2 * (r2 * r2)) * vm0;
                var ug = u * m; var vgc = v * m;
                var cr = one + vca * r2; var cb = one - vca * r2;
                (ug * cr * vsx + vox).CopyTo(_xr, i); (vgc * cr * vsy + voy).CopyTo(_yr, i);
                (ug * vsx + vox).CopyTo(_xg, i); (vgc * vsy + voy).CopyTo(_yg, i);
                (ug * cb * vsx + vox).CopyTo(_xb, i); (vgc * cb * vsy + voy).CopyTo(_yb, i);
                var q = one + vvg * r2;
                (one / (q * q)).CopyTo(_vig, i);
            }
        }
        for (; i < Pixels; i++)
        {
            var u = _bu[i]; var v = _bv[i];
            var ue = u * e;
            var r2 = ue * ue + v * v;
            var m = (1f + k1 * r2 + k2 * (r2 * r2)) * m0;
            var ug = u * m; var vgc = v * m;
            var cr = 1f + ca * r2; var cb = 1f - ca * r2;
            _xr[i] = ug * cr * sx + ox; _yr[i] = vgc * cr * sy + oy;
            _xg[i] = ug * sx + ox; _yg[i] = vgc * sy + oy;
            _xb[i] = ug * cb * sx + ox; _yb[i] = vgc * cb * sy + oy;
            var q = 1f + vg * r2;
            _vig[i] = 1f / (q * q);
        }
        LastLutUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    /// <summary>평면 HDR(R/G/B · W×H) → 왜곡 · 색수차 · 비네팅 적용 출력.</summary>
    public void Apply(float[] sr, float[] sg, float[] sb, float[] dr, float[] dg, float[] db)
    {
        var t0 = Stopwatch.GetTimestamp();
        Remap(sr, _xr, _yr, _vig, dr);
        Remap(sg, _xg, _yg, _vig, dg);
        Remap(sb, _xb, _yb, _vig, db);
        LastApplyUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    /// <summary>채널 1장 LUT 쌍선형 리맵(가장자리 클램프 · 비유한 좌표 → 0) — 호출 없는 인라인 루프.</summary>
    private static void Remap(float[] p, float[] lx, float[] ly, float[] vig, float[] d)
    {
        const float mx = W - 1f, my = H - 1f;
        for (var i = 0; i < Pixels; i++)
        {
            var x = lx[i]; var y = ly[i];
            if (!(x >= 0f)) x = 0f; else if (x > mx) x = mx;   // NaN → 0
            if (!(y >= 0f)) y = 0f; else if (y > my) y = my;
            var ix = (int)x; var iy = (int)y;
            if (ix > W - 2) ix = W - 2;
            if (iy > H - 2) iy = H - 2;
            var fx = x - ix; var fy = y - iy;
            var j = iy * W + ix;
            var a = p[j] + (p[j + 1] - p[j]) * fx;
            var b = p[j + W] + (p[j + W + 1] - p[j + W]) * fx;
            d[i] = (a + (b - a) * fy) * vig[i];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Sample(ReadOnlySpan<float> p, float x, float y)
    {
        x = Math.Clamp(x, 0f, W - 1f); y = Math.Clamp(y, 0f, H - 1f);
        if (!float.IsFinite(x)) x = 0f;
        if (!float.IsFinite(y)) y = 0f;
        int ix = Math.Min((int)x, W - 2), iy = Math.Min((int)y, H - 2);
        float fx = x - ix, fy = y - iy;
        var j = iy * W + ix;
        var a = p[j] + (p[j + 1] - p[j]) * fx;
        var b = p[j + W] + (p[j + W + 1] - p[j + W]) * fx;
        return a + (b - a) * fy;
    }

    /// <summary>순방향 근사(뷰어 왜곡 격자) — 화면 좌표 → 출력 좌표(고정점 4회 · G 채널).</summary>
    public Vector2 Distort(Vector2 px)
    {
        var e = Ellipse(Squeeze); var m0 = EdgeFit * BreathScale;
        var tu = ((px.X + 0.5f) / W * 2f - 1f) * Aspect; var tv = 1f - (px.Y + 0.5f) / H * 2f;
        float u = tu, v = tv;
        for (var k = 0; k < 4; k++)
        {
            var ue = u * e; var r2 = ue * ue + v * v;
            var m = (1f + Spec.K1 * r2 + Spec.K2 * r2 * r2) * m0;
            u = tu / m; v = tv / m;
        }
        return new Vector2((u / Aspect + 1f) * 0.5f * W - 0.5f, (1f - v) * 0.5f * H - 0.5f);
    }

    /// <summary>월드 → 출력 픽셀(아나모픽 2.4:1 · 브리딩 반영 FoV) · depth = 시선축 거리.</summary>
    public Vector2 Project(in LensCamera c, Vector3 p, out float depth)
    {
        var d = p - c.Position;
        depth = Vector3.Dot(d, c.Forward);
        var z = MathF.Max(depth, 1e-3f);
        var nx = Vector3.Dot(d, c.Right) / (z * TanHalfV * Aspect);
        var ny = Vector3.Dot(d, c.Up) / (z * TanHalfV);
        return new Vector2((nx + 1f) * 0.5f * W - 0.5f, (1f - ny) * 0.5f * H - 0.5f);
    }

    /// <summary>얇은 렌즈 착란원(센서 mm) → 출력 픽셀 반경(세로).</summary>
    public float CocPx(float depthM)
    {
        var f = FocalMm; var s = FocusM * 1000f; var d = MathF.Max(depthM, 0.05f) * 1000f;
        var c = f * f / (TStop * MathF.Max(s - f, 1f)) * MathF.Abs(d - s) / d;
        return 0.5f * c / SensorH * H;
    }
}
