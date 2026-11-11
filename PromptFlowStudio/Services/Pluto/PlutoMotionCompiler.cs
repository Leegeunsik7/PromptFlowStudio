using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎥 세그먼트 이징 — 선형 저크 제거용 S-커브.</summary>
public enum PlutoEasingFunction : byte
{
    /// <summary>Cubic Ease-In-Out = Hermite smoothstep <c>3t² − 2t³</c>(양 끝 1차 도함수 0).</summary>
    CubicEaseInOut,
    /// <summary>5차 smootherstep <c>6t⁵ − 15t⁴ + 10t³</c>(양 끝 1·2차 도함수 0).</summary>
    HermiteSmootherstep,
    /// <summary>선형(디버그/비교용).</summary>
    Linear,
}

/// <summary>
/// 🎥 Ken Burns 모션 1구간 — <see cref="StartTimeSec"/>~<see cref="EndTimeSec"/> 동안 줌 <see cref="StartZoom"/>→<see cref="EndZoom"/>,
/// 초점은 직전 구간 초점(첫 구간 = <see cref="PlutoMotionTrajectory.OriginFocal"/>)에서 <see cref="FocalCenter"/>로 이징 이동.
/// </summary>
public sealed record PlutoMotionSegment
{
    public double StartTimeSec { get; init; }
    public double EndTimeSec { get; init; }
    public double StartZoom { get; init; } = 1.0;
    public double EndZoom { get; init; } = 1.0;
    /// <summary>구간 종료 시점 초점(정규화 0~1 · 원점 좌상단).</summary>
    public (double X, double Y) FocalCenter { get; init; } = (0.5, 0.5);
    public PlutoEasingFunction EasingFunction { get; init; } = PlutoEasingFunction.CubicEaseInOut;
    /// <summary>마이크로 오빗 반경(프레임 폭 대비 정규화 · 0 = 없음) — 구간 양 끝에서 0으로 수렴(sin 엔벨로프)해 연속성 유지.</summary>
    public double OrbitRadius { get; init; }
    /// <summary>구간 동안 오빗 회전 수.</summary>
    public double OrbitTurns { get; init; } = 1.0;
}

/// <summary>🎥 다구간 카메라 궤적(시간순 세그먼트).</summary>
public sealed record PlutoMotionTrajectory
{
    public static readonly PlutoMotionTrajectory Empty = new();

    public IReadOnlyList<PlutoMotionSegment> Segments { get; init; } = Array.Empty<PlutoMotionSegment>();
    /// <summary>첫 구간 시작 초점(기본 화면 중앙).</summary>
    public (double X, double Y) OriginFocal { get; init; } = (0.5, 0.5);
    /// <summary>총 길이(초) — 마지막 세그먼트 종료 시각.</summary>
    public double DurationSec
    {
        get
        {
            var d = 0.0;
            for (var i = 0; i < Segments.Count; i++)
                if (Segments[i] is { } s && double.IsFinite(s.EndTimeSec) && s.EndTimeSec > d) d = s.EndTimeSec;
            return d;
        }
    }
}

/// <summary>🎥 프레임 1장의 카메라 상태 — <see cref="X"/>/<see cref="Y"/>/<see cref="ViewWidth"/>/<see cref="ViewHeight"/>는 슈퍼샘플 캔버스 픽셀.</summary>
public readonly record struct PlutoMotionFrame(double Zoom, double FocalX, double FocalY, double X, double Y, double ViewWidth, double ViewHeight);

/// <summary>
/// 🎥 Pluto Deep-Motion Ken Burns 컴파일러 — 정지 사진 1장 + 초점 → 4막 15초 카메라 궤적 → FFmpeg <c>zoompan</c> 구간별(piecewise) 수식.
///   ▸ 줌: <c>z='max(1,min(10,if(lte(on,N1),seg1,if(lte(on,N2),seg2,…))))'</c> — 구간 내 이징 <c>3u²−2u³</c>, <c>u=clip((on−F)/N,0,1)</c>.
///   ▸ 팬: <c>x='max(0,min(iw-iw/zoom,fx·iw−iw/zoom/2))'</c>(y 동일) — 뷰포트를 캔버스 안으로 클램프해 블랙 레터박스 원천 차단.
///   ▸ 서브픽셀: zoompan 은 x/y 를 정수 픽셀로 내리므로 입력을 <see cref="DefaultSupersample"/>배 Lanczos 업스케일(커버 크롭)해 출력 기준 1/4px 정밀도 확보.
/// <see cref="EvaluateFrame"/> 는 같은 수식의 C# 미러(검증 · 미리보기용 · 무할당).
/// </summary>
public static partial class PlutoMotionCompiler
{
    public const int DefaultWidth = 1080, DefaultHeight = 1920, DefaultFps = 60;
    public const int DefaultSupersample = 4, MaxSupersample = 8;
    /// <summary>FFmpeg zoompan 허용 줌 범위.</summary>
    public const double MinZoom = 1.0, MaxZoom = 10.0;
    public const double FourActDurationSec = 15.0;
    /// <summary>Act 3 마이크로 오빗 반경(폭 대비 0.8%).</summary>
    public const double MicroOrbitRadius = 0.008;
    /// <summary>Act 4 풀백 시 초점이 화면 중앙 쪽으로 되돌아가는 비율(숨 쉴 여백).</summary>
    public const double PullBackRecenter = 0.25;

    /// <summary>삼분할 폴백 초점 — 아기/인물 눈높이.</summary>
    public static readonly (double X, double Y) BabyFocal = (0.50, 0.38);
    /// <summary>삼분할 폴백 초점 — 반려동물.</summary>
    public static readonly (double X, double Y) PetFocal = (0.50, 0.55);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>폴백 초점(반려 여부).</summary>
    public static (double X, double Y) FallbackFocal(bool isPet) => isPet ? PetFocal : BabyFocal;

    /// <summary>초점 정규화 — 비유한 값 → 폴백, 범위 밖 → [0,1] 클램프.</summary>
    public static (double X, double Y) NormalizeFocal((double X, double Y) focal, (double X, double Y) fallback)
        => (double.IsFinite(focal.X) ? Math.Clamp(focal.X, 0.0, 1.0) : fallback.X,
            double.IsFinite(focal.Y) ? Math.Clamp(focal.Y, 0.0, 1.0) : fallback.Y);

    /// <summary>
    /// 🎬 4막 15초 궤적 — Act 1(0-4s) 눈 쪽 틸트 1.00→1.10 · Act 2(4-8s) 표정 푸시인 1.10→1.25 ·
    /// Act 3(8-12s) 매크로 클로즈업 1.25→1.35 + 마이크로 오빗 · Act 4(12-15s) 풀백 1.35→1.15.
    /// </summary>
    public static PlutoMotionTrajectory SynthesizeFourAct((double X, double Y) focal)
    {
        var f = NormalizeFocal(focal, BabyFocal);
        var rest = (f.X + (0.5 - f.X) * PullBackRecenter, f.Y + (0.5 - f.Y) * PullBackRecenter);
        return new PlutoMotionTrajectory
        {
            Segments = new[]
            {
                new PlutoMotionSegment { StartTimeSec = 0, EndTimeSec = 4, StartZoom = 1.00, EndZoom = 1.10, FocalCenter = f },
                new PlutoMotionSegment { StartTimeSec = 4, EndTimeSec = 8, StartZoom = 1.10, EndZoom = 1.25, FocalCenter = f },
                new PlutoMotionSegment { StartTimeSec = 8, EndTimeSec = 12, StartZoom = 1.25, EndZoom = 1.35, FocalCenter = f, OrbitRadius = MicroOrbitRadius },
                new PlutoMotionSegment { StartTimeSec = 12, EndTimeSec = FourActDurationSec, StartZoom = 1.35, EndZoom = 1.15, FocalCenter = rest },
            },
        };
    }

    /// <summary>재배치 후 세그먼트 최소 길이(초) — 비트 스냅이 막을 붕괴시키지 않도록.</summary>
    public const double MinRetimedSegmentSec = 0.5;

    /// <summary>
    /// 🥁 비트 양자화 재배치 — 시간순 세그먼트의 내부 경계(i번 종료 = i+1번 시작)를 <paramref name="interiorBoundaries"/>로 교체.
    /// 첫 시작 · 마지막 종료(총 길이)는 불변 · 줌/초점/오빗 값 체인 유지 → 경계에서 위치 연속(C⁰).
    /// Cubic/Smootherstep 이징은 구간 양 끝 1차 도함수 0 이라 재배치 후에도 경계 속도 0 → C¹ 연속 보존.
    /// 경계 개수 불일치 · 비유한 · 비단조 · 최소 길이(<see cref="MinRetimedSegmentSec"/>) 위반 시 원본 반환.
    /// </summary>
    public static PlutoMotionTrajectory RetimeBoundaries(PlutoMotionTrajectory trajectory, ReadOnlySpan<double> interiorBoundaries)
    {
        if (trajectory?.Segments is not { Count: > 1 } src || interiorBoundaries.Length != src.Count - 1) return trajectory ?? PlutoMotionTrajectory.Empty;
        for (var i = 0; i < src.Count; i++)
            if (src[i] is null || (i > 0 && src[i].StartTimeSec < src[i - 1].StartTimeSec)) return trajectory;   // 시간순 전제
        var prev = src[0].StartTimeSec;
        for (var i = 0; i < interiorBoundaries.Length; i++)
        {
            var b = interiorBoundaries[i];
            if (!double.IsFinite(b) || b - prev < MinRetimedSegmentSec - 1e-9) return trajectory;
            prev = b;
        }
        if (src[^1].EndTimeSec - prev < MinRetimedSegmentSec - 1e-9) return trajectory;

        var segs = new PlutoMotionSegment[src.Count];
        for (var i = 0; i < segs.Length; i++)
        {
            var start = i == 0 ? src[0].StartTimeSec : interiorBoundaries[i - 1];
            var end = i == segs.Length - 1 ? src[^1].EndTimeSec : interiorBoundaries[i];
            segs[i] = src[i] with { StartTimeSec = start, EndTimeSec = end };
        }
        return trajectory with { Segments = segs };
    }

    /// <summary>총 출력 프레임 수(세그먼트 없음 → 0).</summary>
    public static int TotalFrames(PlutoMotionTrajectory? trajectory, int fps = DefaultFps)
    {
        var segs = Resolve(trajectory, DefaultWidth, DefaultHeight, ClampFps(fps));
        return segs.Length == 0 ? 0 : segs[^1].EndFrame;
    }

    /// <summary>
    /// 🎥 궤적 → FFmpeg 필터 체인: <c>scale(커버·Lanczos)</c> → <c>crop</c> → <c>setsar=1</c> → <c>zoompan=z:x:y:d:s:fps</c>.
    /// 입력은 정지 이미지 1프레임(<c>d</c> = 총 프레임). 세그먼트가 없으면 줌 1.0 정지 프레임(1초).
    /// </summary>
    public static string CompileFfmpegZoomPanFilter(PlutoMotionTrajectory trajectory, int width = DefaultWidth, int height = DefaultHeight, int fps = DefaultFps, int supersample = DefaultSupersample)
    {
        width = ClampDim(width);
        height = ClampDim(height);
        fps = ClampFps(fps);
        supersample = Math.Clamp(supersample, 1, MaxSupersample);
        var segs = Resolve(trajectory, width, height, fps);
        var frames = segs.Length == 0 ? fps : segs[^1].EndFrame;
        var cw = (width * supersample).ToString(Inv);
        var ch = (height * supersample).ToString(Inv);

        var sb = new StringBuilder(2048);
        sb.Append("scale=").Append(cw).Append(':').Append(ch).Append(":force_original_aspect_ratio=increase:flags=lanczos,crop=").Append(cw).Append(':').Append(ch)
          .Append(",setsar=1,zoompan=z='");
        AppendZoomExpression(sb, segs);
        sb.Append("':x='");
        AppendPanExpression(sb, segs, 0);
        sb.Append("':y='");
        AppendPanExpression(sb, segs, 1);
        sb.Append("':d=").Append(frames.ToString(Inv))
          .Append(":s=").Append(width.ToString(Inv)).Append('x').Append(height.ToString(Inv))
          .Append(":fps=").Append(fps.ToString(Inv));
        return sb.ToString();
    }

    /// <summary>줌 수식(<c>z=</c> 값 · 따옴표 제외).</summary>
    public static string BuildZoomExpression(PlutoMotionTrajectory trajectory, int fps = DefaultFps)
    {
        var sb = new StringBuilder(512);
        AppendZoomExpression(sb, Resolve(trajectory, DefaultWidth, DefaultHeight, ClampFps(fps)));
        return sb.ToString();
    }

    /// <summary>팬 X 수식(<c>x=</c> 값 · 따옴표 제외).</summary>
    public static string BuildPanXExpression(PlutoMotionTrajectory trajectory, int width = DefaultWidth, int height = DefaultHeight, int fps = DefaultFps)
    {
        var sb = new StringBuilder(768);
        AppendPanExpression(sb, Resolve(trajectory, ClampDim(width), ClampDim(height), ClampFps(fps)), 0);
        return sb.ToString();
    }

    /// <summary>팬 Y 수식(<c>y=</c> 값 · 따옴표 제외).</summary>
    public static string BuildPanYExpression(PlutoMotionTrajectory trajectory, int width = DefaultWidth, int height = DefaultHeight, int fps = DefaultFps)
    {
        var sb = new StringBuilder(768);
        AppendPanExpression(sb, Resolve(trajectory, ClampDim(width), ClampDim(height), ClampFps(fps)), 1);
        return sb.ToString();
    }

    /// <summary>🧮 컴파일 수식의 C# 미러 — 출력 프레임 <paramref name="frame"/>(zoompan <c>on</c>)의 줌 · 초점 · 클램프된 뷰포트.</summary>
    public static PlutoMotionFrame EvaluateFrame(PlutoMotionTrajectory trajectory, int frame, int width = DefaultWidth, int height = DefaultHeight, int fps = DefaultFps, int supersample = DefaultSupersample)
    {
        width = ClampDim(width);
        height = ClampDim(height);
        supersample = Math.Clamp(supersample, 1, MaxSupersample);
        var segs = Resolve(trajectory, width, height, ClampFps(fps));
        double iw = (double)width * supersample, ih = (double)height * supersample;
        double zoom = MinZoom, fx = 0.5, fy = 0.5;
        if (segs.Length > 0)
        {
            var i = 0;
            while (i < segs.Length - 1 && frame > segs[i].EndFrame) i++;   // if(lte(on,End_i), seg_i, …) 와 동일한 선택
            ref readonly var s = ref segs[i];
            var u = Math.Clamp((frame - s.StartFrame) / (double)s.Frames, 0.0, 1.0);
            var e = Ease(s.Easing, u);
            zoom = s.Z0 + (s.Z1 - s.Z0) * e;
            fx = s.X0 + (s.X1 - s.X0) * e;
            fy = s.Y0 + (s.Y1 - s.Y0) * e;
            if (s.OrbitX > 0)
            {
                var env = Math.Sin(Math.PI * u);
                var ang = s.OrbitOmega * u;
                fx += s.OrbitX * env * Math.Cos(ang);
                fy += s.OrbitY * env * Math.Sin(ang);
            }
        }
        zoom = Math.Max(MinZoom, Math.Min(MaxZoom, zoom));
        double vw = iw / zoom, vh = ih / zoom;
        var x = Math.Max(0.0, Math.Min(iw - vw, fx * iw - vw / 2));
        var y = Math.Max(0.0, Math.Min(ih - vh, fy * ih - vh / 2));
        return new PlutoMotionFrame(zoom, fx, fy, x, y, vw, vh);
    }

    /// <summary>이징 값(u ∈ [0,1]).</summary>
    public static double Ease(PlutoEasingFunction easing, double u) => easing switch
    {
        PlutoEasingFunction.Linear => u,
        PlutoEasingFunction.HermiteSmootherstep => u * u * u * (u * (6 * u - 15) + 10),
        _ => 3 * u * u - 2 * u * u * u,
    };

    // ══════════════════════ 🔧 내부 ══════════════════════

    /// <summary>정규화 세그먼트 — 프레임 격자 스냅 · 줌/초점 클램프 · 직전 초점 체인.</summary>
    private readonly struct Seg
    {
        public readonly int StartFrame, EndFrame;
        public readonly double Z0, Z1, X0, Y0, X1, Y1, OrbitX, OrbitY, OrbitOmega;
        public readonly PlutoEasingFunction Easing;
        public int Frames => EndFrame - StartFrame;

        public Seg(int start, int end, double z0, double z1, double x0, double y0, double x1, double y1, double orbitX, double orbitY, double omega, PlutoEasingFunction easing)
        {
            StartFrame = start; EndFrame = end;
            Z0 = z0; Z1 = z1; X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
            OrbitX = orbitX; OrbitY = orbitY; OrbitOmega = omega;
            Easing = easing;
        }
    }

    private static int ClampDim(int v) => Math.Clamp(v, 16, 8192) & ~1;   // 짝수(yuv420p)
    private static int ClampFps(int fps) => Math.Clamp(fps, 1, 240);

    /// <summary>수식 리터럴과 C# 미러가 같은 값을 쓰도록 소수 6자리 양자화.</summary>
    private static double Q(double v) => Math.Round(v, 6, MidpointRounding.AwayFromZero);

    private static Seg[] Resolve(PlutoMotionTrajectory? trajectory, int width, int height, int fps)
    {
        var src = trajectory?.Segments;
        if (src is null || src.Count == 0) return Array.Empty<Seg>();
        var ordered = new List<PlutoMotionSegment>(src.Count);
        for (var i = 0; i < src.Count; i++)
        {
            var s = src[i];
            if (s is null || !double.IsFinite(s.StartTimeSec) || !double.IsFinite(s.EndTimeSec) || s.EndTimeSec <= s.StartTimeSec || s.EndTimeSec <= 0) continue;
            ordered.Add(s);
        }
        ordered.Sort(static (a, b) => a.StartTimeSec.CompareTo(b.StartTimeSec));

        var origin = NormalizeFocal(trajectory!.OriginFocal, (0.5, 0.5));
        double px = Q(origin.X), py = Q(origin.Y);
        var list = new List<Seg>(ordered.Count);
        var cursor = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var start = Math.Max(cursor, (int)Math.Round(Math.Max(0, s.StartTimeSec) * fps, MidpointRounding.AwayFromZero));   // 겹침 → 직전 종료로 밀기
            var end = (int)Math.Round(Math.Min(s.EndTimeSec, 3600) * fps, MidpointRounding.AwayFromZero);
            if (end <= start) continue;
            var f = NormalizeFocal(s.FocalCenter, (px, py));
            double x1 = Q(f.X), y1 = Q(f.Y);
            var r = double.IsFinite(s.OrbitRadius) ? Math.Clamp(s.OrbitRadius, 0.0, 0.25) : 0.0;
            var turns = double.IsFinite(s.OrbitTurns) ? Math.Clamp(s.OrbitTurns, -16.0, 16.0) : 1.0;
            list.Add(new Seg(start, end, ClampZoom(s.StartZoom), ClampZoom(s.EndZoom), px, py, x1, y1,
                Q(r), Q(r * width / height), Q(2 * Math.PI * turns), s.EasingFunction));
            px = x1; py = y1;
            cursor = end;
        }
        return list.ToArray();
    }

    private static double ClampZoom(double z) => double.IsFinite(z) ? Q(Math.Clamp(z, MinZoom, MaxZoom)) : MinZoom;

    private static void AppendZoomExpression(StringBuilder sb, Seg[] segs)
    {
        if (segs.Length == 0) { sb.Append('1'); return; }
        sb.Append("max(1,min(10,");
        AppendPiecewise(sb, segs, 2);
        sb.Append("))");
    }

    /// <param name="axis">0 = x(iw) · 1 = y(ih).</param>
    private static void AppendPanExpression(StringBuilder sb, Seg[] segs, int axis)
    {
        var dim = axis == 0 ? "iw" : "ih";
        if (segs.Length == 0) { sb.Append('0'); return; }
        // 뷰포트 = dim/zoom → 좌상단 = 초점·dim − 뷰포트/2, [0, dim − 뷰포트] 클램프.
        sb.Append("max(0,min(").Append(dim).Append('-').Append(dim).Append("/zoom,(");
        AppendPiecewise(sb, segs, axis);
        sb.Append(")*").Append(dim).Append('-').Append(dim).Append("/zoom/2))");
    }

    /// <param name="channel">0 = 초점 X · 1 = 초점 Y · 2 = 줌.</param>
    private static void AppendPiecewise(StringBuilder sb, Seg[] segs, int channel)
    {
        for (var i = 0; i < segs.Length; i++)
        {
            if (i < segs.Length - 1) sb.Append("if(lte(on,").Append(segs[i].EndFrame.ToString(Inv)).Append("),");
            AppendSegment(sb, in segs[i], channel);
            if (i < segs.Length - 1) sb.Append(',');
        }
        sb.Append(')', segs.Length - 1);
    }

    private static void AppendSegment(StringBuilder sb, in Seg s, int channel)
    {
        double a, b;
        switch (channel)
        {
            case 0: a = s.X0; b = s.X1; break;
            case 1: a = s.Y0; b = s.Y1; break;
            default: a = s.Z0; b = s.Z1; break;
        }
        var orbit = channel != 2 && s.OrbitX > 0;
        var delta = b - a;
        if (delta == 0 && !orbit) { sb.Append(Num(a)); return; }

        sb.Append('(').Append(Num(a));
        if (delta != 0)
        {
            sb.Append(delta < 0 ? '-' : '+').Append(Num(Math.Abs(delta))).Append('*');
            AppendEase(sb, in s);
        }
        if (orbit)
        {
            // r·sin(πu)·cos|sin(ωu) — 엔벨로프가 구간 양 끝에서 0 → 경계 연속.
            sb.Append('+').Append(Num(channel == 0 ? s.OrbitX : s.OrbitY)).Append("*sin(PI*");
            AppendProgress(sb, in s);
            sb.Append(")*").Append(channel == 0 ? "cos(" : "sin(").Append(Num(s.OrbitOmega)).Append('*');
            AppendProgress(sb, in s);
            sb.Append(')');
        }
        sb.Append(')');
    }

    private static void AppendEase(StringBuilder sb, in Seg s)
    {
        switch (s.Easing)
        {
            case PlutoEasingFunction.Linear:
                AppendProgress(sb, in s);
                break;
            case PlutoEasingFunction.HermiteSmootherstep:   // u³·(u·(6u−15)+10)
                sb.Append("(pow("); AppendProgress(sb, in s); sb.Append(",3)*("); AppendProgress(sb, in s);
                sb.Append("*(6*"); AppendProgress(sb, in s); sb.Append("-15)+10))");
                break;
            default:   // 3u² − 2u³
                sb.Append("(3*pow("); AppendProgress(sb, in s); sb.Append(",2)-2*pow("); AppendProgress(sb, in s); sb.Append(",3))");
                break;
        }
    }

    /// <summary>구간 진행도 <c>u = clip((on−F)/N,0,1)</c>.</summary>
    private static void AppendProgress(StringBuilder sb, in Seg s)
    {
        sb.Append("clip(");
        if (s.StartFrame == 0) sb.Append("on");
        else sb.Append("(on-").Append(s.StartFrame.ToString(Inv)).Append(')');
        sb.Append('/').Append(s.Frames.ToString(Inv)).Append(",0,1)");
    }

    private static string Num(double v) => v.ToString("0.######", Inv);
}
