using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using PromptFlowStudio.Blazor.Components;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🎞 Pluto 실시간 모션 프리뷰어 — 프리뷰 계약 JSON · C# 컴파일러(FFmpeg zoompan 미러)와의 좌표 동치 · 빈 상태 렌더.
/// (<see cref="PlutoMotionCompiler.EvaluateFrame"/> ↔ FFmpeg 수식 문자열 동치는 <see cref="PlutoMotionCompilerTests"/> 가 보증.)
/// </summary>
public sealed class PlutoMotionPreviewerTests
{
    private const int W = PlutoMotionCompiler.DefaultWidth, H = PlutoMotionCompiler.DefaultHeight, Fps = PlutoMotionCompiler.DefaultFps;
    private const int Ss = PlutoMotionCompiler.DefaultSupersample;

    private static readonly PlutoMotionTrajectory Trajectory = PlutoMotionCompiler.SynthesizeFourAct((0.42, 0.33));

    [Fact]
    public void MotionCompiler_ExportsValidPreviewContractJson()
    {
        var json = PlutoMotionCompiler.ExportPreviewContractJson(Trajectory);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal(PlutoMotionPreviewContract.CurrentVersion, root.GetProperty("version").GetInt32());
        Assert.Equal(W, root.GetProperty("width").GetInt32());
        Assert.Equal(H, root.GetProperty("height").GetInt32());
        Assert.Equal(Fps, root.GetProperty("fps").GetInt32());
        Assert.Equal(PlutoMotionCompiler.FourActDurationSec, root.GetProperty("durationSec").GetDouble(), 9);
        // 클램프 한계 = 컴파일러 zoompan 허용 범위
        Assert.Equal(PlutoMotionCompiler.MinZoom, root.GetProperty("minZoom").GetDouble());
        Assert.Equal(PlutoMotionCompiler.MaxZoom, root.GetProperty("maxZoom").GetDouble());

        var segs = root.GetProperty("segments");
        Assert.Equal(4, segs.GetArrayLength());
        double[] starts = { 0, 4, 8, 12 }, ends = { 4, 8, 12, 15 }, z0 = { 1.00, 1.10, 1.25, 1.35 }, z1 = { 1.10, 1.25, 1.35, 1.15 };
        var cubic = PlutoMotionCompiler.EaseCoefficients(PlutoEasingFunction.CubicEaseInOut);
        Assert.Equal(new[] { 0.0, 0.0, 3.0, -2.0, 0.0, 0.0 }, cubic);   // S(τ) = 3τ² − 2τ³
        for (var i = 0; i < 4; i++)
        {
            var s = segs[i];
            Assert.Equal(starts[i], s.GetProperty("startSec").GetDouble(), 9);
            Assert.Equal(ends[i], s.GetProperty("endSec").GetDouble(), 9);
            Assert.Equal((int)(starts[i] * Fps), s.GetProperty("startFrame").GetInt32());
            Assert.Equal((int)(ends[i] * Fps), s.GetProperty("endFrame").GetInt32());
            Assert.Equal(z0[i], s.GetProperty("z0").GetDouble(), 6);
            Assert.Equal(z1[i], s.GetProperty("z1").GetDouble(), 6);
            Assert.Equal(nameof(PlutoEasingFunction.CubicEaseInOut), s.GetProperty("easing").GetString());
            var ease = s.GetProperty("ease");
            Assert.Equal(cubic.Length, ease.GetArrayLength());
            for (var k = 0; k < cubic.Length; k++) Assert.Equal(cubic[k], ease[k].GetDouble());
            foreach (var name in new[] { "x0", "y0", "x1", "y1" })
                Assert.InRange(s.GetProperty(name).GetDouble(), 0.0, 1.0);
        }
        // 초점 체인: 원점(0.5,0.5) → 초점 → 초점 → 초점 → 풀백 재중심
        Assert.Equal(0.5, segs[0].GetProperty("x0").GetDouble(), 6);
        Assert.Equal(0.42, segs[0].GetProperty("x1").GetDouble(), 6);
        Assert.Equal(0.42, segs[1].GetProperty("x0").GetDouble(), 6);
        Assert.Equal(0.42 + (0.5 - 0.42) * PlutoMotionCompiler.PullBackRecenter, segs[3].GetProperty("x1").GetDouble(), 6);
        // Act 3 마이크로 오빗만 반경 보유
        Assert.Equal(0.0, segs[0].GetProperty("orbitX").GetDouble());
        Assert.Equal(PlutoMotionCompiler.MicroOrbitRadius, segs[2].GetProperty("orbitX").GetDouble(), 6);
        Assert.Equal(Math.Round(2 * Math.PI, 6), segs[2].GetProperty("orbitOmega").GetDouble(), 9);   // 컴파일러 6자리 양자화

        // 이징 계수 다항식 == 컴파일러 Ease(전 이징 종류)
        foreach (var easing in Enum.GetValues<PlutoEasingFunction>())
            for (var u = 0.0; u <= 1.0; u += 0.05)
                Assert.Equal(PlutoMotionCompiler.Ease(easing, u), PlutoMotionPreviewModel.Horner(PlutoMotionCompiler.EaseCoefficients(easing), u), 12);

        // 라운드트립 + 빈 궤적
        var back = PlutoMotionPreviewModel.FromJson(json);
        Assert.Equal(json, PlutoMotionPreviewModel.ToJson(back));
        var empty = PlutoMotionPreviewModel.FromJson(PlutoMotionCompiler.ExportPreviewContractJson(PlutoMotionTrajectory.Empty));
        Assert.Empty(empty.Segments);
        Assert.Equal(1.0, PlutoMotionPreviewModel.Evaluate(empty, 7.5).Zoom);
        Assert.Equal(0.0, PlutoMotionPreviewModel.Evaluate(empty, 7.5).X);
    }

    [Fact]
    public void PlutoMotionPreviewer_CalculatesIdenticalCoordinates_ToFfmpegZoompan()
    {
        // JS 가 실제로 소비하는 JSON 을 거친 계약으로 평가(직렬화 손실까지 포함해 검증).
        var contract = PlutoMotionPreviewModel.FromJson(PlutoMotionCompiler.ExportPreviewContractJson(Trajectory));
        double iw = (double)W * Ss, ih = (double)H * Ss;
        double[] samples = { 0.0, 1.5, 4.0, 6.2, 8.0, 10.5, 12.0, 13.5, 14.8, 15.0 };
        const double eps = 0.001;
        foreach (var t in samples)
        {
            var frame = (int)Math.Round(t * Fps, MidpointRounding.AwayFromZero);
            var reference = PlutoMotionCompiler.EvaluateFrame(Trajectory, frame, W, H, Fps, Ss);   // FFmpeg zoompan 수식의 C# 미러
            var preview = PlutoMotionPreviewModel.Evaluate(contract, t);

            Assert.True(Math.Abs(reference.Zoom - preview.Zoom) < eps, "z @" + t);
            Assert.True(Math.Abs(reference.X / iw - preview.X) < eps, "x(norm) @" + t);
            Assert.True(Math.Abs(reference.Y / ih - preview.Y) < eps, "y(norm) @" + t);
            // 출력 1080×1920 픽셀 기준으로도 ε 미만(캔버스 setTransform 의 translate = −x·W·z)
            Assert.True(Math.Abs(reference.X / Ss - preview.X * W) < eps, "x(px) @" + t);
            Assert.True(Math.Abs(reference.Y / Ss - preview.Y * H) < eps, "y(px) @" + t);
            // 블랙 보더 금지: 뷰포트가 프레임 안
            Assert.InRange(preview.X, 0.0, 1.0 - 1.0 / preview.Zoom + 1e-12);
            Assert.InRange(preview.Y, 0.0, 1.0 - 1.0 / preview.Zoom + 1e-12);
        }

        // 극단 초점(모서리)에서도 클램프 동치
        var corner = PlutoMotionCompiler.SynthesizeFourAct((0.0, 1.0));
        var cornerContract = PlutoMotionCompiler.ExportPreviewContract(corner);
        foreach (var t in samples)
        {
            var reference = PlutoMotionCompiler.EvaluateFrame(corner, (int)Math.Round(t * Fps, MidpointRounding.AwayFromZero), W, H, Fps, Ss);
            var preview = PlutoMotionPreviewModel.Evaluate(cornerContract, t);
            Assert.True(Math.Abs(reference.X / iw - preview.X) < eps && Math.Abs(reference.Y / ih - preview.Y) < eps && Math.Abs(reference.Zoom - preview.Zoom) < eps, "corner @" + t);
        }
    }

    [Fact]
    public void PreviewSubtitles_CrossfadeBetweenActs_SumsToOne()
    {
        var script = PlutoSubtitleDirector.GenerateLocal("아기가 처음 웃는 순간", "하준");
        var acts = PlutoMotionPreviewModel.BuildSubtitles(script);
        Assert.Equal(3, acts.Length);
        Assert.Equal((0.0, 4.0), (acts[0].StartSec, acts[0].EndSec));
        Assert.Equal((4.0, 12.0), (acts[1].StartSec, acts[1].EndSec));
        Assert.Equal((12.0, 15.0), (acts[2].StartSec, acts[2].EndSec));
        Assert.Equal(script.Act2Text, acts[1].Korean);
        Assert.Equal(script.Act2En, acts[1].English);

        Assert.Equal(1.0, PlutoMotionPreviewModel.SubtitleAlpha(acts, 0, 0.0));
        Assert.Equal(1.0, PlutoMotionPreviewModel.SubtitleAlpha(acts, 2, 15.0));
        Assert.Equal(0.5, PlutoMotionPreviewModel.SubtitleAlpha(acts, 0, 4.0), 9);          // 경계 중앙 = 50/50
        Assert.Equal(0.5, PlutoMotionPreviewModel.SubtitleAlpha(acts, 1, 4.0), 9);
        Assert.Equal(1.0, PlutoMotionPreviewModel.SubtitleAlpha(acts, 0, 4.0 - 0.075), 9);  // 150ms 창 시작
        Assert.Equal(0.0, PlutoMotionPreviewModel.SubtitleAlpha(acts, 0, 4.0 + 0.075), 9);  // 150ms 창 끝
        for (var t = 3.9; t <= 4.1; t += 0.01)
            Assert.Equal(1.0, PlutoMotionPreviewModel.SubtitleAlpha(acts, 0, t) + PlutoMotionPreviewModel.SubtitleAlpha(acts, 1, t), 9);
        for (var t = 11.9; t <= 12.1; t += 0.01)
            Assert.Equal(1.0, PlutoMotionPreviewModel.SubtitleAlpha(acts, 1, t) + PlutoMotionPreviewModel.SubtitleAlpha(acts, 2, t), 9);

        using var doc = JsonDocument.Parse(PlutoMotionPreviewModel.SubtitlesJson(script));
        Assert.Equal(PlutoMotionPreviewModel.CrossfadeSec, doc.RootElement.GetProperty("crossfadeSec").GetDouble());
        Assert.Equal(3, doc.RootElement.GetProperty("acts").GetArrayLength());
        Assert.Equal(script.Act1Text, doc.RootElement.GetProperty("acts")[0].GetProperty("ko").GetString());
        using var none = JsonDocument.Parse(PlutoMotionPreviewModel.SubtitlesJson(null));
        Assert.Equal(0, none.RootElement.GetProperty("acts").GetArrayLength());
    }

    [Fact]
    public async Task PlutoMotionPreviewerComponent_RendersWithoutErrors_WhenMediaEmpty()
    {
        using var sp = new HeadlessUiRendererCheck.AutoWireProvider();
        var html = await HeadlessUiRendererCheck.RenderAsync(typeof(PlutoMotionPreviewer), sp);

        Assert.Contains("data-pmp-empty=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-pmp-empty-msg=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("<canvas class=\"pmp-canvas\" width=\"1080\" height=\"1920\"", html, StringComparison.Ordinal);
        Assert.Contains("data-pmp-play=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("data-pmp-scrub=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("max=\"15\"", html, StringComparison.Ordinal);
        Assert.Contains("0.5×", html, StringComparison.Ordinal);
        Assert.Contains("2.0×", html, StringComparison.Ordinal);
        Assert.Contains("disabled", html, StringComparison.Ordinal);   // 미디어 없음 → 컨트롤 비활성
    }

    [Fact]
    public void Previewer_Js_And_Workspace_AreWired()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "wwwroot"))) d = d.Parent;
        Assert.NotNull(d);
        var app = Path.Combine(d!.FullName, "PromptFlowStudio");
        var js = File.ReadAllText(Path.Combine(app, "wwwroot", "js", "pluto-motion-previewer.js"));
        Assert.Contains("window.plutoMotionPreviewer", js, StringComparison.Ordinal);
        Assert.Contains("ctx.setTransform(z, 0, 0, z, -s.x * W * z, -s.y * H * z)", js, StringComparison.Ordinal);
        Assert.Contains("requestAnimationFrame", js, StringComparison.Ordinal);
        Assert.Contains("ac.currentTime", js, StringComparison.Ordinal);
        Assert.Contains("{ kind: 0, at: 0.8,", js, StringComparison.Ordinal);
        Assert.Contains("{ kind: 1, at: 5.5,", js, StringComparison.Ordinal);
        Assert.Contains("{ kind: 2, at: 13.2,", js, StringComparison.Ordinal);
        // JS 큐 오프셋 == C# SFX 디렉터 큐 시트
        var cues = PlutoSfxDirector.Shared.BuildDefault15sCueSheet();
        Assert.Equal(new[] { 800, 5500, 13200 }, new[] { cues[0].OffsetMs, cues[1].OffsetMs, cues[2].OffsetMs });

        Assert.Contains("js/pluto-motion-previewer.js", File.ReadAllText(Path.Combine(app, "wwwroot", "index.html")), StringComparison.Ordinal);
        var ws = File.ReadAllText(Path.Combine(app, "Blazor", "Pages", "Workspace.razor"));
        Assert.Contains("<PlutoMotionPreviewer Js=\"JS\" Trajectory=\"_motionPath\" Subtitles=\"_motionSubs\"", ws, StringComparison.Ordinal);
        Assert.Contains("PlutoDirector.Changed += OnDirectorRankChanged;", ws, StringComparison.Ordinal);
        Assert.Contains("PlutoDirector.Changed -= OnDirectorRankChanged;", ws, StringComparison.Ordinal);
    }
}
