using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace PromptFlowStudio.Services.Media;

/// <summary>[v9.3.1] 글래스 재질 파라미터(굴절률·프로스트·틴트 · 불변).</summary>
public sealed record GlassMaterial(double RefractiveIndex, double Frost, double TintAlpha, double Saturation)
{
    /// <summary>기본 크라운 글라스(IOR 1.52).</summary>
    public static GlassMaterial Crown { get; } = new(1.52d, 0.55d, 0.14d, 1.6d);

    /// <summary>홀로그램 아크릴(IOR 1.49 · 저프로스트).</summary>
    public static GlassMaterial Holo { get; } = new(1.49d, 0.30d, 0.08d, 1.9d);
}

/// <summary>
/// [v9.3.1] 공간 깊이 레이어 1장의 합성 결과.
/// CSS 문자열(<see cref="Transform"/>/<see cref="BackdropFilter"/>)은 전량 <see cref="CultureInfo.InvariantCulture"/> 로 직렬화된다(제4헌장).
/// </summary>
public sealed record GlassDepthLayer(
    int Index,
    double Depth,
    double ParallaxX,
    double ParallaxY,
    double BlurPx,
    double Opacity,
    double Fresnel,
    double Specular,
    double RefractionShiftPx,
    string Transform,
    string BackdropFilter);

/// <summary>[v9.3.1] 1회 렌더 패스 결과(불변).</summary>
public sealed record GlassRenderResult(
    long Pass,
    double YawDeg,
    double PitchDeg,
    IReadOnlyList<GlassDepthLayer> Layers,
    double FresnelAvg,
    double SpecularPeak,
    double DepthSpread,
    double ElapsedMs);

/// <summary>
/// [PromptFlow Studio v9.3.1] 공간 글래스모피즘 &amp; 3D 뎁스 렌더러(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 시점(Yaw/Pitch) → 원근 4×4 행렬(<c>perspective · rotateX · rotateY · translateZ</c>)을 <c>stackalloc</c> 스팬에서 합성해 CSS <c>matrix3d</c> 로 출력한다.
///   ▸ 레이어마다 Schlick 근사 Fresnel 반사율, Snell 굴절 오프셋, Blinn-Phong 스펙큘러, 깊이 비례 프로스트 블러를 계산한다.
///   ▸ <see cref="QuantizeDepthLayers"/>: 휘도 버퍼를 깊이 레이어로 양자화(실 뎁스넷 대체 BCL 휴리스틱 · <see cref="Spatial3DVideoEngine"/> 와 동일 결선점 계약).
///   ▸ 제5헌장: NaN/Infinity·빈 입력은 예외 없이 기본 시점/균등 분포로 강등한다.
/// </summary>
public sealed class SpatialGlassmorphismRenderer
{
    private const int MaxLayers = 12;
    private const double PerspectivePx = 900d;
    private const double LayerSpacingPx = 42d;

    private readonly object _gate = new();
    private long _passes;
    private double _yaw;
    private double _pitch;

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>현재 재질.</summary>
    public GlassMaterial Material { get; private set; } = GlassMaterial.Crown;

    /// <summary>현재 레이어 수.</summary>
    public int LayerCount { get; private set; } = 6;

    /// <summary>현재 Yaw(도).</summary>
    public double YawDeg => _yaw;

    /// <summary>현재 Pitch(도).</summary>
    public double PitchDeg => _pitch;

    /// <summary>누적 렌더 패스.</summary>
    public long PassCount => Interlocked.Read(ref _passes);

    /// <summary>마지막 렌더 결과(없으면 null).</summary>
    public GlassRenderResult? LastResult { get; private set; }

    /// <summary>마지막 동작 요약(HUD 1줄).</summary>
    public string? LastAction { get; private set; }

    /// <summary>재질을 교체하고 즉시 재렌더한다.</summary>
    public GlassRenderResult SetMaterial(GlassMaterial material)
    {
        Material = material ?? GlassMaterial.Crown;
        return Render(_yaw, _pitch, LayerCount);
    }

    /// <summary>시점을 상대 회전(궤도)하고 재렌더한다. Pitch 는 ±60° 로 클램프.</summary>
    public GlassRenderResult Orbit(double deltaYawDeg, double deltaPitchDeg)
        => Render(Wrap(_yaw + Safe(deltaYawDeg)), _pitch + Safe(deltaPitchDeg), LayerCount);

    /// <summary>시점/레이어를 초기화한다.</summary>
    public GlassRenderResult ResetView() => Render(0d, 0d, 6);

    /// <summary>지정 시점에서 깊이 레이어 스택을 합성한다.</summary>
    public GlassRenderResult Render(double yawDeg, double pitchDeg, int layerCount)
    {
        var sw = Stopwatch.StartNew();
        var yaw = Wrap(Safe(yawDeg));
        var pitch = Math.Clamp(Safe(pitchDeg), -60d, 60d);
        var count = Math.Clamp(layerCount, 1, MaxLayers);
        var mat = Material;

        // 시선 벡터(카메라→원점) — 회전된 판 법선과의 코사인이 Fresnel/굴절 입사각이 된다.
        var yr = yaw * Math.PI / 180d;
        var pr = pitch * Math.PI / 180d;
        var cosTheta = Math.Clamp(Math.Cos(yr) * Math.Cos(pr), 0.02d, 1d);
        var r0 = Math.Pow((mat.RefractiveIndex - 1d) / (mat.RefractiveIndex + 1d), 2d);

        // 고정 광원(좌상단 전방) · 하프 벡터 Blinn-Phong.
        const double lx = -0.42d, ly = -0.58d, lz = 0.70d;
        var nx = Math.Sin(yr) * Math.Cos(pr);
        var ny = -Math.Sin(pr);
        var nz = Math.Cos(yr) * Math.Cos(pr);
        var hx = lx; var hy = ly; var hz = lz + 1d;
        var hl = Math.Sqrt(hx * hx + hy * hy + hz * hz);
        var nDotH = Math.Max(0d, (nx * hx + ny * hy + nz * hz) / hl);

        Span<double> m = stackalloc double[16];
        var layers = new GlassDepthLayer[count];
        double fresnelSum = 0d, specPeak = 0d;

        for (var i = 0; i < count; i++)
        {
            var depth = count == 1 ? 0d : i / (double)(count - 1);            // 0=전경 · 1=원경
            var z = -depth * LayerSpacingPx * count;
            var parallax = 1d - depth * 0.85d;                                  // 원경일수록 시차 축소
            var px = Math.Sin(yr) * 36d * parallax;
            var py = -Math.Sin(pr) * 36d * parallax;

            // Schlick: F = R0 + (1-R0)(1-cosθ)^5 — 원경은 누적 굴절로 입사각을 약간 키운다.
            var cosLayer = Math.Clamp(cosTheta - depth * 0.08d, 0.02d, 1d);
            var fresnel = r0 + (1d - r0) * Math.Pow(1d - cosLayer, 5d);

            // Snell: sinθt = sinθi / n → 두께(LayerSpacing) 투과 시 측면 변위.
            var sinI = Math.Sqrt(Math.Max(0d, 1d - cosLayer * cosLayer));
            var sinT = sinI / mat.RefractiveIndex;
            var cosT = Math.Sqrt(Math.Max(0d, 1d - sinT * sinT));
            var shift = cosT <= 1e-6 ? 0d : LayerSpacingPx * (sinI / Math.Max(cosLayer, 1e-3) - sinT / cosT) * 0.25d;

            var spec = Math.Pow(nDotH, 48d) * (1d - depth * 0.6d);
            var blur = 2d + mat.Frost * (6d + depth * 18d);
            var opacity = Math.Clamp(1d - depth * 0.62d + fresnel * 0.4d, 0.12d, 1d);

            ComposeMatrix(m, yr, pr, px, py, z);

            fresnelSum += fresnel;
            if (spec > specPeak) specPeak = spec;

            layers[i] = new GlassDepthLayer(
                i, depth, px, py, blur, opacity, fresnel, spec, shift,
                ToMatrix3d(m),
                string.Create(CultureInfo.InvariantCulture,
                    $"blur({blur:0.#}px) saturate({mat.Saturation * 100d:0}%) brightness({1d + spec * 0.35d:0.###})"));
        }

        sw.Stop();
        var pass = Interlocked.Increment(ref _passes);
        var result = new GlassRenderResult(pass, yaw, pitch, layers, fresnelSum / count, specPeak,
            LayerSpacingPx * (count - 1), sw.Elapsed.TotalMilliseconds);

        lock (_gate)
        {
            _yaw = yaw;
            _pitch = pitch;
            LayerCount = count;
            LastResult = result;
            LastAction = string.Create(CultureInfo.InvariantCulture,
                $"패스 #{pass} · {count}레이어 · yaw {yaw:0}° pitch {pitch:0}° · {result.ElapsedMs:0.###}ms");
        }

        OnChange?.Invoke();
        return result;
    }

    /// <summary>
    /// 휘도(0~255) 버퍼를 깊이 레이어 점유율로 양자화한다(밝을수록 전경 가정 · 합=1).
    /// 빈 버퍼는 균등 분포로 강등한다. ⚠ 실 뎁스넷 교체 시 이 시그니처만 유지하면 상위 무변경.
    /// </summary>
    public static double[] QuantizeDepthLayers(ReadOnlySpan<byte> luma, int layers)
    {
        var n = Math.Clamp(layers, 1, MaxLayers);
        var result = new double[n];
        if (luma.IsEmpty)
        {
            Array.Fill(result, 1d / n);
            return result;
        }

        Span<int> bins = stackalloc int[MaxLayers];
        bins.Clear();
        foreach (var v in luma)
        {
            var layer = (255 - v) * n / 256;   // 밝음=전경(0)
            bins[layer]++;
        }

        for (var i = 0; i < n; i++) result[i] = bins[i] / (double)luma.Length;
        return result;
    }

    // perspective(P) · translate(px,py,z) · rotateX(pitch) · rotateY(yaw) — CSS column-major 순서로 m 에 기록.
    private static void ComposeMatrix(Span<double> m, double yaw, double pitch, double tx, double ty, double tz)
    {
        double cy = Math.Cos(yaw), sy = Math.Sin(yaw);
        double cp = Math.Cos(pitch), sp = Math.Sin(pitch);

        // R = Rx(pitch) · Ry(yaw) (row-major r[row,col])
        double r00 = cy, r01 = 0d, r02 = sy;
        double r10 = sp * sy, r11 = cp, r12 = -sp * cy;
        double r20 = -cp * sy, r21 = sp, r22 = cp * cy;

        // 원근: w' = 1 - z/P → 4행 = (0,0,-1/P,1)
        var ip = -1d / PerspectivePx;

        // column-major: m[col*4 + row]
        m[0] = r00; m[1] = r10; m[2] = r20; m[3] = r20 * ip;
        m[4] = r01; m[5] = r11; m[6] = r21; m[7] = r21 * ip;
        m[8] = r02; m[9] = r12; m[10] = r22; m[11] = r22 * ip;
        m[12] = tx; m[13] = ty; m[14] = tz; m[15] = 1d + tz * ip;
    }

    private static string ToMatrix3d(ReadOnlySpan<double> m)
    {
        var sb = new System.Text.StringBuilder("matrix3d(", 200);
        for (var i = 0; i < 16; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(m[i].ToString("0.#####", CultureInfo.InvariantCulture));
        }
        return sb.Append(')').ToString();
    }

    private static double Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0d : v;

    private static double Wrap(double deg)
    {
        var d = deg % 360d;
        if (d > 180d) d -= 360d;
        else if (d < -180d) d += 360d;
        return d;
    }
}
