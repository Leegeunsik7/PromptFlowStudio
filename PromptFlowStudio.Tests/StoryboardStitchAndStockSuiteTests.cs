using System.Text.Json;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>스토리보드 4컷 자동 합성(concat copy → 1280x720 정규화 폴백 · 실물 검증 · 메타 사이드카) · StockSuite 원자적 내보내기 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class StoryboardStitchAndStockSuiteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_sbstitch_" + Guid.NewGuid().ToString("N"));

    public StoryboardStitchAndStockSuiteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private StoryboardStitchCut[] MakeCuts()
    {
        var cuts = new StoryboardStitchCut[4];
        for (var i = 0; i < 4; i++)
        {
            var p = Path.Combine(_dir, "cut" + (i + 1) + ".mp4");
            File.WriteAllBytes(p, new byte[] { 0, 0, 0, 0x18, (byte)i });
            cuts[i] = new StoryboardStitchCut(p, "prompt " + (i + 1), 5);
        }
        return cuts;
    }

    [Fact]
    public async Task StoryboardStitchService_GeneratesValidConcatListAndExecutesFfmpeg()
    {
        var cuts = MakeCuts();
        Assert.True(StoryboardStitchService.ShouldAutoStitch(cuts));
        Assert.False(StoryboardStitchService.ShouldAutoStitch(cuts[..3]));

        var output = StoryboardStitchService.BuildOutputPath(_dir, new DateTime(2026, 11, 4, 21, 5, 9));
        Assert.Equal("storyboard_20261104_210509_full20s.mp4", Path.GetFileName(output));

        string? listText = null;
        var calls = new List<string[]>();
        var svc = new StoryboardStitchService(() => "ffmpeg", (exe, args, ct) =>
        {
            var a = args.ToArray();
            calls.Add(a);
            var list = a[Array.IndexOf(a, "-i") + 1];
            listText ??= File.ReadAllText(list);
            // 1차(스트림 복사) 실패 → 2차(정규화)만 산출물 기록
            if (Array.IndexOf(a, "copy") >= 0) return Task.FromResult(false);
            File.WriteAllBytes(a[^1], new byte[] { 1, 2, 3, 4 });
            return Task.FromResult(true);
        });

        var r = await svc.StitchAsync(cuts, output);

        Assert.True(r.Ok, r.Message);
        Assert.True(r.Normalized);
        Assert.Equal(Path.GetFullPath(output), r.OutputPath);
        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".part.mp4"));
        Assert.False(File.Exists(output + ".concat.txt"));

        // concat 목록: 4컷 순서 · file '…' 형식
        Assert.NotNull(listText);
        var lines = listText!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, lines.Length);
        for (var i = 0; i < 4; i++)
        {
            Assert.StartsWith("file '", lines[i].Trim());
            Assert.Contains("cut" + (i + 1) + ".mp4", lines[i]);
        }

        // ffmpeg 인자: concat demuxer · copy 우선 → scale=1280:720 정규화
        Assert.Equal(2, calls.Count);
        Assert.Contains("concat", calls[0]);
        Assert.Contains("-safe", calls[0]);
        Assert.Equal("copy", calls[0][Array.IndexOf(calls[0], "-c:v") + 1]);
        Assert.Contains(calls[1], a => a.StartsWith("scale=1280:720", StringComparison.Ordinal));

        // 메타 사이드카: 컷별 프롬프트 · 누적 타임스탬프
        Assert.NotNull(r.MetaPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(r.MetaPath!));
        var mc = doc.RootElement.GetProperty("Cuts");
        Assert.Equal(4, mc.GetArrayLength());
        Assert.Equal("prompt 4", mc[3].GetProperty("Prompt").GetString());
        Assert.Equal(15d, mc[3].GetProperty("StartSec").GetDouble());
        Assert.Equal(20d, doc.RootElement.GetProperty("TotalSec").GetDouble());

        // ffmpeg 부재 → 실패 결과(파일 미생성)
        var none = new StoryboardStitchService(() => null, (_, _, _) => Task.FromResult(true));
        var miss = await none.StitchAsync(cuts, Path.Combine(_dir, "none.mp4"));
        Assert.False(miss.Ok);
        Assert.False(File.Exists(Path.Combine(_dir, "none.mp4")));
    }

    [Fact]
    public async Task StockSuiteExporter_CopiesFileToTargetDirectorySuccessfully()
    {
        var src = Path.Combine(_dir, "master.mp4");
        File.WriteAllBytes(src, new byte[] { 9, 8, 7, 6, 5 });
        var target = Path.Combine(_dir, "StockSuite", "Assets", "Video");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);   // 상위 존재 → 대상 폴더 자동 생성

        var exp = new StockSuiteExporter(() => target);
        Assert.True(exp.IsConfigured);

        var r = await exp.ExportAsync(src);
        Assert.True(r.Ok, r.Message);
        var dest = Path.Combine(target, StockSuiteExporter.DefaultAssetName);
        Assert.Equal(dest, r.TargetPath);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dest));
        Assert.Empty(Directory.GetFiles(target, "*.~pfs"));

        // 덮어쓰기
        File.WriteAllBytes(src, new byte[] { 1, 1, 1 });
        var r2 = await exp.ExportAsync(src);
        Assert.True(r2.Ok, r2.Message);
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(dest));

        // 미설정 → 경로 설정 안내
        var unset = await new StockSuiteExporter(() => "  ").ExportAsync(src);
        Assert.False(unset.Ok);
        Assert.True(unset.NeedsPathSetup);

        // 상위 폴더도 없는 경로 → 재설정 안내
        var bad = await new StockSuiteExporter(() => Path.Combine(_dir, "nope", "deeper", "Video")).ExportAsync(src);
        Assert.False(bad.Ok);
        Assert.True(bad.NeedsPathSetup);
    }
}
