using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🎥 Pluto Deep-Motion Ken Burns 컴파일러 — 컴파일된 FFmpeg 수식 문자열을 직접 평가해 경계 · 연속성 검증.</summary>
public sealed class PlutoMotionCompilerTests : IDisposable
{
    private const int W = 1080, H = 1920, Fps = 60, Ss = PlutoMotionCompiler.DefaultSupersample;
    private const double Iw = W * Ss, Ih = H * Ss;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_motion_" + Guid.NewGuid().ToString("N"));

    public PlutoMotionCompilerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── FFmpeg 수식 부분집합 평가기(if · lte · clip · max · min · pow · sin · cos · 사칙 · 변수) ──
    private sealed class Expr
    {
        private readonly string _s;
        private readonly Dictionary<string, double> _vars;
        private int _i;

        private Expr(string s, Dictionary<string, double> vars) { _s = s; _vars = vars; }

        public static double Eval(string s, Dictionary<string, double> vars)
        {
            var p = new Expr(s, vars);
            var v = p.Sum();
            Assert.Equal(s.Length, p._i);   // 전체 소비(괄호 균형)
            return v;
        }

        private double Sum()
        {
            var v = Product();
            while (_i < _s.Length && _s[_i] is '+' or '-')
            {
                var op = _s[_i++];
                var r = Product();
                v = op == '+' ? v + r : v - r;
            }
            return v;
        }

        private double Product()
        {
            var v = Unary();
            while (_i < _s.Length && _s[_i] is '*' or '/')
            {
                var op = _s[_i++];
                var r = Unary();
                v = op == '*' ? v * r : v / r;
            }
            return v;
        }

        private double Unary()
        {
            if (_s[_i] == '-') { _i++; return -Unary(); }
            if (_s[_i] == '(') { _i++; var v = Sum(); Assert.Equal(')', _s[_i++]); return v; }
            var start = _i;
            if (char.IsDigit(_s[_i]) || _s[_i] == '.')
            {
                while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
                return double.Parse(_s.AsSpan(start, _i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
            var name = _s[start.._i];
            Assert.NotEqual("", name);
            if (_i >= _s.Length || _s[_i] != '(') return name == "PI" ? Math.PI : _vars[name];
            _i++;
            var a = new List<double>(3);
            while (true)
            {
                a.Add(Sum());
                if (_s[_i] == ',') { _i++; continue; }
                Assert.Equal(')', _s[_i++]);
                break;
            }
            return name switch
            {
                "if" => a[0] != 0 ? a[1] : a[2],
                "lte" => a[0] <= a[1] ? 1 : 0,
                "clip" => Math.Min(Math.Max(a[0], a[1]), a[2]),
                "max" => Math.Max(a[0], a[1]),
                "min" => Math.Min(a[0], a[1]),
                "pow" => Math.Pow(a[0], a[1]),
                "sin" => Math.Sin(a[0]),
                "cos" => Math.Cos(a[0]),
                _ => throw new InvalidOperationException("미지원 함수: " + name),
            };
        }
    }

    private static string Option(string filter, string key)
    {
        var at = filter.IndexOf(key + "='", StringComparison.Ordinal);
        Assert.True(at >= 0, key);
        at += key.Length + 2;
        return filter[at..filter.IndexOf('\'', at)];
    }

    private static void Near(double expected, double actual, double tol)
        => Assert.True(Math.Abs(expected - actual) <= tol, $"expected {expected:R} · actual {actual:R}");

    /// <summary>zoompan 과 같은 순서(z → x,y)로 컴파일 수식 평가.</summary>
    private static (double Z, double X, double Y) Run(string filter, int on)
    {
        var vars = new Dictionary<string, double> { ["on"] = on, ["iw"] = Iw, ["ih"] = Ih, ["zoom"] = 1 };
        var z = Expr.Eval(Option(filter, "zoompan=z"), vars);
        vars["zoom"] = z;
        return (z, Expr.Eval(Option(filter, ":x"), vars), Expr.Eval(Option(filter, ":y"), vars));
    }

    [Fact]
    public void PlutoMotionCompiler_GeneratesClampedCoordinates_WithoutOutOfBounds()
    {
        var focals = new (double X, double Y)[]
        {
            PlutoMotionCompiler.BabyFocal, PlutoMotionCompiler.PetFocal,
            (0, 0), (1, 1), (0, 1), (1, 0), (0.02, 0.97), (0.93, 0.04),
            (-3, 7), (double.NaN, double.PositiveInfinity),   // 불량 입력 → 정규화
        };
        foreach (var focal in focals)
        {
            var traj = PlutoMotionCompiler.SynthesizeFourAct(focal);
            var filter = PlutoMotionCompiler.CompileFfmpegZoomPanFilter(traj);
            for (var on = 0; on <= 901; on++)   // on 0/1-기반 양쪽 + 종료 후 1프레임
            {
                var (z, x, y) = Run(filter, on);
                Assert.InRange(z, PlutoMotionCompiler.MinZoom, PlutoMotionCompiler.MaxZoom);
                Assert.True(x >= 0 && y >= 0, $"음수 오프셋 on={on} x={x} y={y}");
                Assert.True(x + Iw / z <= Iw + 1e-6, $"가로 스필 on={on}");
                Assert.True(y + Ih / z <= Ih + 1e-6, $"세로 스필 on={on}");

                var m = PlutoMotionCompiler.EvaluateFrame(traj, on);   // C# 미러 = 컴파일 수식
                Near(z, m.Zoom, 1e-9);
                Near(x, m.X, 1e-5);
                Near(y, m.Y, 1e-5);
            }
        }

        // 임의 궤적: 극단 줌 · 겹침/역순 · 무효 세그먼트 · 큰 오빗 · 이징 3종 → 여전히 캔버스 내부.
        var wild = new PlutoMotionTrajectory
        {
            OriginFocal = (9, -9),
            Segments = new[]
            {
                new PlutoMotionSegment { StartTimeSec = 5, EndTimeSec = 10, StartZoom = 40, EndZoom = 0.2, FocalCenter = (1.4, -0.3), OrbitRadius = 5, OrbitTurns = 3, EasingFunction = PlutoEasingFunction.HermiteSmootherstep },
                new PlutoMotionSegment { StartTimeSec = 0, EndTimeSec = 6, StartZoom = double.NaN, EndZoom = 3, FocalCenter = (0, 1), EasingFunction = PlutoEasingFunction.Linear },
                new PlutoMotionSegment { StartTimeSec = 3, EndTimeSec = 2 },
                new PlutoMotionSegment { StartTimeSec = 10, EndTimeSec = 15, StartZoom = 2, EndZoom = 1, FocalCenter = (1, 0), OrbitRadius = 0.2 },
            },
        };
        var wf = PlutoMotionCompiler.CompileFfmpegZoomPanFilter(wild);
        Assert.Contains(":d=900:", wf, StringComparison.Ordinal);
        for (var on = 0; on <= 901; on++)
        {
            var (z, x, y) = Run(wf, on);
            Assert.InRange(z, 1.0, 10.0);
            Assert.InRange(x, 0, Iw - Iw / z + 1e-6);
            Assert.InRange(y, 0, Ih - Ih / z + 1e-6);
            var m = PlutoMotionCompiler.EvaluateFrame(wild, on);
            Near(z, m.Zoom, 1e-9);
            Near(x, m.X, 1e-5);
            Near(y, m.Y, 1e-5);
        }

        // 빈 궤적 → 정지 프레임(줌 1 · 오프셋 0), 문화권 무관 소수점.
        var empty = PlutoMotionCompiler.CompileFfmpegZoomPanFilter(PlutoMotionTrajectory.Empty);
        Assert.Contains("zoompan=z='1':x='0':y='0':d=60:s=1080x1920:fps=60", empty, StringComparison.Ordinal);
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(PlutoMotionCompiler.CompileFfmpegZoomPanFilter(PlutoMotionCompiler.SynthesizeFourAct((0.52, 0.38))),
                InvariantCompile());
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    private static string InvariantCompile()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            return PlutoMotionCompiler.CompileFfmpegZoomPanFilter(PlutoMotionCompiler.SynthesizeFourAct((0.52, 0.38)));
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    [Fact]
    public void PlutoMotionCompiler_CompilesContinuousPiecewiseCurve()
    {
        var traj = PlutoMotionCompiler.SynthesizeFourAct((0.52, 0.38));
        Assert.Equal(900, PlutoMotionCompiler.TotalFrames(traj));
        Assert.Equal(15.0, traj.DurationSec);
        var filter = PlutoMotionCompiler.CompileFfmpegZoomPanFilter(traj);

        // 필터 체인 구조: 슈퍼샘플 커버 스케일 → 크롭 → zoompan(9:16 · 900프레임 · 60fps).
        Assert.StartsWith("scale=4320:7680:force_original_aspect_ratio=increase:flags=lanczos,crop=4320:7680,setsar=1,zoompan=z='", filter, StringComparison.Ordinal);
        Assert.EndsWith(":d=900:s=1080x1920:fps=60", filter, StringComparison.Ordinal);
        var zexpr = Option(filter, "zoompan=z");
        Assert.Equal(PlutoMotionCompiler.BuildZoomExpression(traj), zexpr);
        Assert.Equal(PlutoMotionCompiler.BuildPanXExpression(traj), Option(filter, ":x"));
        Assert.Equal(PlutoMotionCompiler.BuildPanYExpression(traj), Option(filter, ":y"));

        // 구간 경계 = 60fps 프레임 수(4s·8s·12s → 240·480·720), 마지막 구간은 else 분기.
        foreach (var n in new[] { 240, 480, 720 })
            Assert.Contains("if(lte(on," + n.ToString(CultureInfo.InvariantCulture) + "),", zexpr, StringComparison.Ordinal);
        Assert.DoesNotContain("lte(on,900)", zexpr, StringComparison.Ordinal);
        Assert.Equal(3, zexpr.Split("if(lte(on,").Length - 1);
        Assert.Contains("clip((on-720)/180,0,1)", zexpr, StringComparison.Ordinal);
        Assert.Contains("3*pow(clip(on/240,0,1),2)-2*pow(clip(on/240,0,1),3)", zexpr, StringComparison.Ordinal);   // 3t²−2t³

        // 키프레임 줌 값(Act 경계) + smoothstep 중점.
        Near(1.00, Run(filter, 0).Z, 1e-9);
        Near(1.05, Run(filter, 120).Z, 1e-9);
        Near(1.10, Run(filter, 240).Z, 1e-9);
        Near(1.25, Run(filter, 480).Z, 1e-9);
        Near(1.35, Run(filter, 720).Z, 1e-9);
        Near(1.15, Run(filter, 900).Z, 1e-9);

        // 0~900 프레임 연속성 — 프레임 간 줌 · 팬 점프 없음(선형 최대 기울기의 1.5배 = smoothstep 피크 이내).
        var prev = Run(filter, 0);
        double maxDz = 0, maxDx = 0, maxDy = 0;
        for (var on = 1; on <= 900; on++)
        {
            var cur = Run(filter, on);
            maxDz = Math.Max(maxDz, Math.Abs(cur.Z - prev.Z));
            maxDx = Math.Max(maxDx, Math.Abs(cur.X - prev.X));
            maxDy = Math.Max(maxDy, Math.Abs(cur.Y - prev.Y));
            prev = cur;
        }
        Assert.InRange(maxDz, 1e-6, 1.5 * 0.20 / 180 + 1e-9);   // 최급 구간 = Act 4 풀백
        Assert.True(maxDx < 8 && maxDy < 12, $"팬 점프 dx={maxDx} dy={maxDy}");   // 슈퍼샘플 캔버스 px(출력 기준 ≤ 3px/프레임)

        // 구간 경계 양쪽 1프레임 속도 ≈ 0(ease-in-out) — 선형 저크 제거.
        foreach (var n in new[] { 240, 480, 720 })
        {
            Assert.True(Math.Abs(Run(filter, n).Z - Run(filter, n - 1).Z) < 1e-4);
            Assert.True(Math.Abs(Run(filter, n + 1).Z - Run(filter, n).Z) < 1e-4);
        }

        // Act 3 마이크로 오빗: 구간 중앙에서 초점 이탈, 경계에서 0 수렴.
        var mid = PlutoMotionCompiler.EvaluateFrame(traj, 600);
        Assert.True(Math.Abs(mid.FocalX - 0.52) > 0.005);
        Assert.Equal(0.52, PlutoMotionCompiler.EvaluateFrame(traj, 480).FocalX, 9);
        Assert.Equal(0.52, PlutoMotionCompiler.EvaluateFrame(traj, 720).FocalX, 9);

        // fps 변경 → 프레임 격자 재계산.
        var f30 = PlutoMotionCompiler.CompileFfmpegZoomPanFilter(traj, 1080, 1920, 30, 2);
        Assert.Contains("lte(on,120)", f30, StringComparison.Ordinal);
        Assert.EndsWith(":d=450:s=1080x1920:fps=30", f30, StringComparison.Ordinal);
        Assert.StartsWith("scale=2160:3840:", f30, StringComparison.Ordinal);

        // 이징 3종 끝점 고정.
        foreach (var e in Enum.GetValues<PlutoEasingFunction>())
        {
            Assert.Equal(0.0, PlutoMotionCompiler.Ease(e, 0), 12);
            Assert.Equal(0.5, PlutoMotionCompiler.Ease(e, 0.5), 12);
            Assert.Equal(1.0, PlutoMotionCompiler.Ease(e, 1), 12);
        }
    }

    private string WritePhoto(string name)
    {
        var path = Path.Combine(_dir, name);
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x40 };
        bytes.AddRange(Encoding.ASCII.GetBytes("Exif\0\0MM2026:05:05 10:30:00\0"));
        bytes.AddRange(new byte[64]);
        bytes.Add(0xFF); bytes.Add(0xD9);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private sealed class FakeVision : IPlutoVisionClient
    {
        public bool IsVisionAvailable => true;
        public string? Response { get; init; }
        public string LastSystem = "";

        public Task<string?> DescribeImageJsonAsync(string system, string user, byte[] image, string mimeType, double temperature, CancellationToken ct)
        {
            LastSystem = system;
            return Task.FromResult(Response);
        }
    }

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool IsAvailable => true;
        public string? Response { get; init; }
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct) => Task.FromResult(Response);
    }

    private static void AssertFourAct(PlutoMotionTrajectory path, (double X, double Y) focal)
    {
        Assert.Equal(4, path.Segments.Count);
        var times = new[] { 0.0, 4, 8, 12, 15 };
        var zooms = new[] { 1.00, 1.10, 1.25, 1.35, 1.15 };
        for (var i = 0; i < 4; i++)
        {
            var s = path.Segments[i];
            Assert.Equal(times[i], s.StartTimeSec);
            Assert.Equal(times[i + 1], s.EndTimeSec);
            Assert.Equal(zooms[i], s.StartZoom, 9);
            Assert.Equal(zooms[i + 1], s.EndZoom, 9);
            Assert.Equal(PlutoEasingFunction.CubicEaseInOut, s.EasingFunction);
            if (i < 3) Assert.Equal(focal, s.FocalCenter);
        }
        Assert.Equal(0, path.Segments[1].OrbitRadius);
        Assert.True(path.Segments[2].OrbitRadius > 0);   // Act 3 마이크로 오빗
        // Act 4 풀백 → 초점이 중앙 쪽으로 살짝 복귀.
        Assert.InRange(Math.Abs(path.Segments[3].FocalCenter.Y - 0.5), 0, Math.Abs(focal.Y - 0.5));
    }

    [Fact]
    public async Task PlutoVisionStorySynthesizer_ConstructsCoordinatedMotionPlan()
    {
        // ① 규칙 분석 폴백 — 아기 삼분할(0.50, 0.38) · 반려(0.50, 0.55).
        var baby = await new PlutoVisionStorySynthesizer().SynthesizeFromImageAsync(WritePhoto("smile_baby.jpg"));
        Assert.Equal(PlutoVisionSource.Heuristic, baby.Source);
        Assert.Equal((0.50, 0.38), baby.DetectedFocalPoint);
        AssertFourAct(baby.MotionPath, (0.50, 0.38));

        var pet = await new PlutoVisionStorySynthesizer().SynthesizeFromImageAsync(WritePhoto("puppy_play.jpg"));
        Assert.True(pet.Attributes.IsPet);
        Assert.Equal((0.50, 0.55), pet.DetectedFocalPoint);
        AssertFourAct(pet.MotionPath, (0.50, 0.55));

        // ② Gemini Vision — 지시문이 초점 좌표를 요구하고, 응답 좌표가 궤적에 반영.
        var vision = new FakeVision { Response = "{\"kind\":\"smile\",\"mood\":\"해맑은 웃음\",\"focal_x\":0.61,\"focal_y\":0.33}" };
        var seen = await new PlutoVisionStorySynthesizer(vision, null, null, new PlutoDirectorRankService(() => 60)).SynthesizeFromImageAsync(WritePhoto("a.jpg"));
        Assert.Contains("\"focal_x\":0.52,\"focal_y\":0.38", vision.LastSystem, StringComparison.Ordinal);
        Assert.Equal(PlutoVisionSource.Vision, seen.Source);
        Assert.Equal((0.61, 0.33), seen.DetectedFocalPoint);
        AssertFourAct(seen.MotionPath, (0.61, 0.33));   // 광학(디렉터 티어) 적용 후에도 궤적 보존
        Assert.Equal("해맑은 웃음", seen.DetectedMood);

        // 좌표만 유효한 응답도 채택(문자열 수치 허용).
        var only = await new PlutoVisionStorySynthesizer(new FakeVision { Response = "{\"focal_x\":\"0.4\",\"focal_y\":0.7}" }).SynthesizeFromImageAsync(WritePhoto("b.jpg"));
        Assert.Equal(PlutoVisionSource.Vision, only.Source);
        Assert.Equal((0.4, 0.7), only.DetectedFocalPoint);

        // ③ 스키마 위반(범위 밖 · 한쪽 누락 · 비수치) → 삼분할 폴백 유지.
        foreach (var bad in new[] { "\"focal_x\":1.7,\"focal_y\":0.3", "\"focal_x\":0.5", "\"focal_x\":\"left\",\"focal_y\":null", "\"focal_x\":-0.1,\"focal_y\":0.5" })
        {
            var p = await new PlutoVisionStorySynthesizer(new FakeVision { Response = "{\"mood\":\"포근\"," + bad + "}" }).SynthesizeFromImageAsync(WritePhoto("c.jpg"));
            Assert.Equal(PlutoVisionSource.Vision, p.Source);
            Assert.Equal((0.50, 0.38), p.DetectedFocalPoint);
            AssertFourAct(p.MotionPath, (0.50, 0.38));
        }

        // ④ 게이트웨이(픽셀 미열람)의 좌표는 불신 → 폴백.
        var gw = await new PlutoVisionStorySynthesizer(null, new FakeLlm { Response = "{\"mood\":\"포근\",\"focal_x\":0.9,\"focal_y\":0.9}" }).SynthesizeFromImageAsync(WritePhoto("d.jpg"));
        Assert.Equal(PlutoVisionSource.Gateway, gw.Source);
        Assert.Equal((0.50, 0.38), gw.DetectedFocalPoint);

        // ⑤ 플랜 → 컴파일: Act 2 종료(줌 1.25 → 뷰포트 0.8) 시 뷰포트 중심이 판독 초점에 최대한 근접(레터박스 방지 클램프 = 중심 [0.4, 0.6]).
        var filter = PlutoMotionCompiler.CompileFfmpegZoomPanFilter(seen.MotionPath);
        Assert.Contains("zoompan=z='max(1,min(10,if(lte(on,240),", filter, StringComparison.Ordinal);
        var (z, x, y) = Run(filter, 480);
        Near(0.60, (x + Iw / z / 2) / Iw, 1e-6);
        Near(0.40, (y + Ih / z / 2) / Ih, 1e-6);
        Assert.InRange(0.61 * Iw, x, x + Iw / z);   // 초점은 뷰포트 내부
        Assert.InRange(0.33 * Ih, y, y + Ih / z);
        // 중앙 근처 초점은 클램프 없이 정확히 추적.
        var centered = Run(PlutoMotionCompiler.CompileFfmpegZoomPanFilter(PlutoMotionCompiler.SynthesizeFourAct((0.52, 0.45))), 480);
        Near(0.52, (centered.X + Iw / centered.Z / 2) / Iw, 1e-6);
        Near(0.45, (centered.Y + Ih / centered.Z / 2) / Ih, 1e-6);
    }

    [Fact]
    public async Task ExportMotionVideoFromPhotoAsync_RendersZoomPan_ThenMuxesBgmAndSfx()
    {
        var photo = WritePhoto("first_smile.jpg");
        var bgm = Path.Combine(_dir, "lullaby.wav");
        File.WriteAllBytes(bgm, new byte[64]);
        var outDir = Path.Combine(_dir, "out");
        var calls = new List<string[]>();
        var failSubtitles = false;
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            if (failSubtitles && arr.Any(a => a.Contains("drawtext", StringComparison.Ordinal))) return Task.FromResult(false);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => null, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = (_, _) => bgm,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            SfxTempDir = Path.Combine(_dir, "sfx"),
        };
        var traj = PlutoMotionCompiler.SynthesizeFourAct((0.52, 0.38));
        var cues = new[] { new SubtitleCue(1, 4, "안녕, 우리 아기", "Hello, my little one") };

        var r = await exp.ExportMotionVideoFromPhotoAsync(photo, traj, new ExportOptions { OutputDirectory = outDir }, cues);

        Assert.True(r.Ok, r.Message);
        Assert.True(r.HasAudio);
        Assert.Equal(Path.Combine(outDir, "first_smile_kenburns.mp4"), r.TargetPath);
        Assert.True(File.Exists(r.TargetPath));
        Assert.True(File.Exists(Path.ChangeExtension(r.TargetPath!, ".srt")));
        Assert.Contains(StockSuiteExporter.MotionTag + "900프레임 @60fps", r.Message, StringComparison.Ordinal);
        Assert.Contains(StockSuiteExporter.SubtitleTag, r.Message, StringComparison.Ordinal);
        Assert.Contains(StockSuiteExporter.SfxTag + "3큐", r.Message, StringComparison.Ordinal);

        Assert.Equal(2, calls.Count);   // ① 모션 렌더 ② BGM+SFX 믹스
        var render = calls[0];
        Assert.Equal(Path.GetFullPath(photo), render[Array.IndexOf(render, "-i") + 1]);
        var vf = render[Array.IndexOf(render, "-vf") + 1];
        Assert.StartsWith(PlutoMotionCompiler.CompileFfmpegZoomPanFilter(traj) + ",drawtext=", vf, StringComparison.Ordinal);
        Assert.Equal("900", render[Array.IndexOf(render, "-frames:v") + 1]);
        Assert.Equal("60", render[Array.IndexOf(render, "-r") + 1]);
        Assert.Equal("15", render[Array.IndexOf(render, "-t") + 1]);
        Assert.Contains("-an", render);
        var mix = calls[1];
        Assert.Contains("loudnorm=I=-14", mix[Array.IndexOf(mix, "-filter_complex") + 1], StringComparison.Ordinal);
        Assert.Contains("amix=inputs=4", mix[Array.IndexOf(mix, "-filter_complex") + 1], StringComparison.Ordinal);
        Assert.Null(exp.BabyBgm);   // 인스턴스 상태 복원
        Assert.False(exp.IncludeSfx);
        Assert.Null(exp.SubtitleCues);
        Assert.Empty(Directory.GetFiles(outDir, "*.~*"));   // 임시 파일 잔여 없음

        // [부분 구제] 자막 번인 실패 → 자막 없이 재시도 · 빈 궤적 → 기본 4막.
        calls.Clear();
        failSubtitles = true;
        var rescued = await exp.ExportMotionVideoFromPhotoAsync(photo, PlutoMotionTrajectory.Empty, new ExportOptions { OutputDirectory = outDir }, cues, includeSfx: false);
        Assert.True(rescued.Ok, rescued.Message);
        Assert.Contains("⚠ 자막 생략", rescued.Message, StringComparison.Ordinal);
        Assert.Equal(3, calls.Count);
        Assert.DoesNotContain("drawtext", calls[1][Array.IndexOf(calls[1], "-vf") + 1], StringComparison.Ordinal);
        Assert.Contains("lte(on,720)", calls[1][Array.IndexOf(calls[1], "-vf") + 1], StringComparison.Ordinal);
        Assert.Contains("-af", calls[2]);   // SFX OFF → BGM 단독 먹싱

        // 입력 검증.
        Assert.False((await exp.ExportMotionVideoFromPhotoAsync(Path.Combine(_dir, "missing.jpg"), traj, new ExportOptions())).Ok);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exp.ExportMotionVideoFromPhotoAsync(photo, traj, new ExportOptions(), ct: new CancellationToken(true)));
    }

    /// <summary>🎬 작업실 1-Click 모션 렌더 결선 — 버튼 · 진행 스피너 · 위치 열기 링크 · 자막/BGM/SFX 인자 · 궤적 전달.</summary>
    [Fact]
    public void Workspace_Wires_OneClick_MotionVideo_Render()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) d = d.Parent;
        Assert.NotNull(d);
        var ws = File.ReadAllText(Path.Combine(d!.FullName, "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));

        Assert.Contains("data-motion-render=\"1\" @onclick=\"RenderMotionVideoAsync\"", ws, StringComparison.Ordinal);
        Assert.Contains("🎬 15초 모션 비디오 즉시 렌더", ws, StringComparison.Ordinal);
        Assert.Contains("data-motion-busy=\"1\"", ws, StringComparison.Ordinal);
        Assert.Contains("ws-motion-spin", ws, StringComparison.Ordinal);
        Assert.Contains("@@keyframes ws-motion-spin", ws, StringComparison.Ordinal);
        Assert.Contains("data-motion-open=\"1\" @onclick=\"() => RevealInExplorer(mvo)\"", ws, StringComparison.Ordinal);
        Assert.Contains("_motionPath = plan.MotionPath;", ws, StringComparison.Ordinal);
        Assert.Contains("StockSuite.ExportMotionVideoFromPhotoAsync(image, _motionPath", ws, StringComparison.Ordinal);
        Assert.Contains("(await GenerateWorkspaceLetterAsync(_mediaCts.Token)).ToCues()", ws, StringComparison.Ordinal);
        Assert.Contains("includeSfx: true, ct: _mediaCts.Token", ws, StringComparison.Ordinal);
        Assert.Contains("finally { _motionBusy = false; _stockBusy = false; }", ws, StringComparison.Ordinal);
    }
}
