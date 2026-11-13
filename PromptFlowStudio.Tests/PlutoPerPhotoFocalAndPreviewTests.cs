using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using PromptFlowStudio.Blazor.Components;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🎯 사진별 초점 검출 · 🎞 멀티컷 몽타주 프리뷰 계약 · 프리뷰어 컴포넌트 렌더.</summary>
public sealed class PlutoPerPhotoFocalAndPreviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs-focal-" + Guid.NewGuid().ToString("N"));

    public PlutoPerPhotoFocalAndPreviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>어두운 균일 배경 + (cx, cy) 중심 웜 스킨톤 원형 피사체 PNG.</summary>
    private string MakeSubjectBitmap(string name, double cx, double cy, int w = 320, int h = 480)
    {
        var path = Path.Combine(_dir, name);
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.FromArgb(18, 18, 24));
            using var skin = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(224, 172, 140));
            var r = (int)(Math.Min(w, h) * 0.12);
            g.FillEllipse(skin, (int)(cx * w) - r, (int)(cy * h) - r, 2 * r, 2 * r);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        return path;
    }

    [Fact]
    public void FocalDetector_TracksOffCenterSubject_InSyntheticBitmap()
    {
        var path = MakeSubjectBitmap("subject.png", 0.75, 0.30);
        var (x, y) = PlutoImageFocalDetector.Shared.DetectLocalFocalPoint(path);
        Assert.True(x > 0.60, "X=" + x.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(y < 0.45, "Y=" + y.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(PlutoImageFocalDetector.Fallback, PlutoImageFocalDetector.Shared.DetectLocalFocalPoint(Path.Combine(_dir, "missing.png")));
    }

    [Fact]
    public async Task FocalDetector_BatchExtraction_ReturnsOnePointPerPhoto_WithSafeClamping()
    {
        var paths = new[]
        {
            MakeSubjectBitmap("a.png", 0.05, 0.05),   // 극단 좌상단 → 클램프
            MakeSubjectBitmap("b.png", 0.95, 0.95),   // 극단 우하단 → 클램프
            MakeSubjectBitmap("c.png", 0.50, 0.40),
            Path.Combine(_dir, "unreadable.png"),     // 없음 → 폴백
        };
        var pts = await PlutoImageFocalDetector.Shared.ExtractBatchFocalPointsAsync(paths);
        Assert.Equal(4, pts.Count);
        foreach (var (x, y) in pts)
        {
            Assert.InRange(x, PlutoImageFocalDetector.MinCoord, PlutoImageFocalDetector.MaxCoord);
            Assert.InRange(y, PlutoImageFocalDetector.MinCoord, PlutoImageFocalDetector.MaxCoord);
        }
        Assert.True(pts[0].X < pts[1].X && pts[0].Y < pts[1].Y);
        Assert.Equal(PlutoImageFocalDetector.Fallback, pts[3]);
    }

    private static PlutoMontagePreviewPlan BuildPreview(int clips, out PlutoMontagePlan plan)
    {
        var paths = new string[clips];
        var urls = new string[clips];
        var focals = new (double X, double Y)[clips];
        for (var i = 0; i < clips; i++)
        {
            paths[i] = "photo" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg";
            urls[i] = "https://appassets/photo" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jpg";
            focals[i] = (0.25 + 0.12 * i, 0.70 - 0.10 * i);
        }
        var profiles = new PlutoColorGradeProfile[clips];
        for (var i = 0; i < clips; i++) profiles[i] = new PlutoColorGradeProfile("Test", 0.05, 1.1, 1.2, 0.05, 0, false);
        plan = PlutoMontageCompiler.Shared.CompileMontagePlan(paths, focals, new[] { 3.0, 6.0, 9.0, 12.0 }, profiles);
        return PlutoMontageCompiler.Shared.BuildMontagePreviewPlan(plan, urls, new[] { 3.0, 6.0, 9.0, 12.0 });
    }

    [Fact]
    public void MontageCompiler_BuildsMultiCutPreviewPlan_WithDistinctPerClipTrajectories()
    {
        var preview = BuildPreview(4, out var plan);
        Assert.Equal(4, preview.Clips.Count);
        Assert.Equal(15.0, preview.TotalDurationSec, 6);
        Assert.Equal(0.0, preview.Clips[0].StartSec, 6);
        Assert.Equal(15.0, preview.Clips[^1].EndSec, 6);
        Assert.Equal(4, preview.BeatTimestampsSec.Count);

        var focalX = new HashSet<double>();
        for (var i = 0; i < preview.Clips.Count; i++)
        {
            var c = preview.Clips[i];
            Assert.True(c.EndSec > c.StartSec);
            if (i > 0)
            {
                Assert.True(c.StartSec > preview.Clips[i - 1].StartSec);
                Assert.True(c.EndSec > preview.Clips[i - 1].EndSec);
                Assert.Equal(c.StartSec + preview.Clips[i - 1].TransitionOutSec, preview.Clips[i - 1].EndSec, 6);   // xfade 창 = 겹침
            }
            Assert.Equal(i == preview.Clips.Count - 1 ? 0.0 : plan.TransitionSec, c.TransitionOutSec, 9);
            Assert.NotEmpty(c.Trajectory.Segments);
            Assert.Equal(c.EndSec - c.StartSec, c.Trajectory.DurationSec, 2);
            var mid = PlutoMotionPreviewModel.Evaluate(c.Trajectory, (c.EndSec - c.StartSec) / 2);
            focalX.Add(Math.Round(mid.FocalX, 3));
            Assert.Equal(1.05, c.ColorGrade.Brightness, 6);
            Assert.Equal(1.1, c.ColorGrade.Contrast, 6);
            Assert.Equal(1.2, c.ColorGrade.Saturation, 6);
            Assert.Equal(0.04, c.ColorGrade.WarmTintAlpha, 6);
            Assert.StartsWith("https://appassets/photo", c.ImageUrl, StringComparison.Ordinal);
        }
        Assert.Equal(4, focalX.Count);   // 클립별 초점 → 서로 다른 궤적

        using var doc = JsonDocument.Parse(preview.ToJson());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal(4, root.GetProperty("clips").GetArrayLength());
        var c0 = root.GetProperty("clips")[0];
        Assert.Equal(1, c0.GetProperty("trajectory").GetProperty("version").GetInt32());
        Assert.True(c0.GetProperty("colorGrade").GetProperty("brightness").GetDouble() > 1.0);
    }

    [Fact]
    public async Task PlutoMotionPreviewerComponent_RendersMultiCutMontagePlan_WithoutErrors()
    {
        var preview = BuildPreview(3, out _);
        using var sp = new HeadlessUiRendererCheck.AutoWireProvider();
        var html = await HeadlessUiRendererCheck.RenderAsync(typeof(PlutoMotionPreviewer), sp,
            new Dictionary<string, object?> { [nameof(PlutoMotionPreviewer.MontagePlan)] = preview });

        Assert.Contains("data-pmp-empty=\"0\"", html, StringComparison.Ordinal);
        Assert.Contains("data-pmp-montage=\"3\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-pmp-empty-msg=\"1\"", html, StringComparison.Ordinal);
        Assert.Contains("<canvas class=\"pmp-canvas\" width=\"1080\" height=\"1920\"", html, StringComparison.Ordinal);
    }
}
