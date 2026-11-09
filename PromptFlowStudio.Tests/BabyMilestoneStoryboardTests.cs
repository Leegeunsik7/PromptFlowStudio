using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Tests;

/// <summary>🍼 아기 성장 다이어리 4컷 마일스톤 프리셋 · 📱 15초 9:16 쇼츠 합성 옵션 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class BabyMilestoneStoryboardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_babyshorts_" + Guid.NewGuid().ToString("N"));

    public BabyMilestoneStoryboardTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void StoryboardDirector_GeneratesBabyMilestoneCuts_WithConsistency()
    {
        Assert.Equal(3, StoryboardDirectorService.Presets.Length);
        const string features = "chubby cheeks, curly brown hair";
        foreach (var p in StoryboardDirectorService.Presets)
        {
            Assert.Equal(StoryboardDirectorService.CutCount, p.Cuts.Length);
            var cuts = StoryboardDirectorService.BuildCuts(p.Kind, BabyGrowthStage.FirstBirthday, features);
            Assert.Equal(4, cuts.Length);
            var tag = StoryboardDirectorService.BuildCharacterTag(BabyGrowthStage.FirstBirthday, features);
            for (var i = 0; i < cuts.Length; i++)
            {
                Assert.StartsWith(tag, cuts[i], StringComparison.Ordinal);
                Assert.Contains(StoryboardDirectorService.ConsistencyTag, cuts[i], StringComparison.Ordinal);
                Assert.Contains(GeminiPromptService.GrowthStageOf(BabyGrowthStage.FirstBirthday).Tokens, cuts[i], StringComparison.Ordinal);
                Assert.Contains(features, cuts[i], StringComparison.Ordinal);
                Assert.Contains(p.Cuts[i], cuts[i], StringComparison.Ordinal);
                Assert.Equal(cuts[i], StoryboardDirectorService.BuildCut(p.Kind, i, BabyGrowthStage.FirstBirthday, features));
            }
            Assert.Equal(4, cuts.Distinct().Count());
        }

        Assert.Contains("tummy time", StoryboardDirectorService.PresetOf(BabyMilestonePresetKind.HundredDayMiracle).Cuts[0], StringComparison.Ordinal);
        Assert.Contains("doljabi", StoryboardDirectorService.PresetOf(BabyMilestonePresetKind.FirstBirthday).Cuts[2], StringComparison.Ordinal);
        Assert.Contains("rubber duck", StoryboardDirectorService.PresetOf(BabyMilestonePresetKind.DayInLife).Cuts[2], StringComparison.Ordinal);

        var proj = new StoryboardProject();
        Assert.True(StoryboardDirectorService.ApplyToProject(proj, BabyMilestonePresetKind.FirstBirthday));
        Assert.Equal(4, proj.Scenes.Count);
        Assert.Equal("1080x1920", proj.ExportResolution);
        Assert.True(proj.IsBabyPreset);
        var defTag = StoryboardDirectorService.BuildCharacterTag(BabyGrowthStage.FirstBirthday, null);
        Assert.All(proj.Scenes, s => Assert.StartsWith(defTag, s.Prompt, StringComparison.Ordinal));
    }

    [Fact]
    public void StoryboardStitch_ConfiguresVerticalShortsOptions_Correctly()
    {
        var o = StoryboardStitchService.BuildBabyShortsOptions();
        Assert.Equal(1080, o.Width);
        Assert.Equal(1920, o.Height);
        Assert.Equal("9:16", o.AspectRatio);
        Assert.Equal(3.75, o.CutSec, 3);
        Assert.Equal(15d, o.TotalSec);
        Assert.Equal(BabyBgmKind.Lullaby, o.Bgm);
        Assert.True(o.MuxBgm);
        Assert.InRange(o.BgmVolume, 0.05, 0.5);
        Assert.Equal(0.5, StoryboardStitchService.BuildBabyShortsOptions(bgmVolume: 2.0).BgmVolume);

        // 합성 BGM(파일 없음) — lavfi 입력 + 볼륨 믹스 + 9:16 센터 크롭 + 15초
        var args = StoryboardStitchService.BuildVerticalShortsArgs("list.txt", "out.mp4", o);
        var joined = string.Join(" ", args);
        Assert.Contains("lavfi", args);
        Assert.Contains("aevalsrc=", joined, StringComparison.Ordinal);
        Assert.Contains("scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920", joined, StringComparison.Ordinal);
        Assert.Contains("volume=0.25", joined, StringComparison.Ordinal);
        Assert.Contains("[a]", args);
        Assert.Equal("15", args[Array.IndexOf(args, "-t") + 1]);
        Assert.Equal("out.mp4", args[^1]);

        // 사용자 BGM 파일 — 루프 입력
        var withFile = StoryboardStitchService.BuildVerticalShortsArgs("l.txt", "o.mp4", o with { BgmPath = "bgm.mp3" });
        Assert.Equal("-stream_loop", withFile[Array.IndexOf(withFile, "bgm.mp3") - 3]);

        // 무음 폴백
        var silent = StoryboardStitchService.BuildVerticalShortsArgs("l.txt", "o.mp4", o with { MuxBgm = false });
        Assert.Contains("-an", silent);
        Assert.DoesNotContain("lavfi", silent);

        // 컷별 3.75초 트림 concat 목록
        var cuts = new StoryboardStitchCut[4];
        for (var i = 0; i < 4; i++) cuts[i] = new StoryboardStitchCut(Path.Combine(_dir, "c" + i + ".mp4"), null, 5);
        var list = StoryboardStitchService.BuildTrimmedConcatList(cuts, o.CutSec);
        Assert.Equal(4, list.Split("outpoint 3.75\n").Length - 1);

        foreach (var k in new[] { BabyBgmKind.Lullaby, BabyBgmKind.MusicBox, BabyBgmKind.Acoustic })
            Assert.EndsWith(":d=15", StoryboardStitchService.BuildSynthBgmSource(k, 15), StringComparison.Ordinal);
        Assert.EndsWith(StoryboardStitchService.ShortsSuffix, StoryboardStitchService.BuildShortsOutputPath(_dir, new DateTime(2026, 11, 9)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StoryboardStitch_BabyShorts_FallsBackToSilent_WhenBgmMixFails()
    {
        var cuts = new StoryboardStitchCut[4];
        for (var i = 0; i < 4; i++)
        {
            var p = Path.Combine(_dir, "cut" + i + ".mp4");
            File.WriteAllBytes(p, new byte[] { 1, 2, 3, (byte)i });
            cuts[i] = new StoryboardStitchCut(p, "p" + i, 5);
        }
        var calls = new List<IReadOnlyList<string>>();
        var svc = new StoryboardStitchService(() => "ffmpeg", (exe, a, ct) =>
        {
            calls.Add(a);
            if (!a.Contains("-an")) return Task.FromResult(false);   // BGM 믹스 실패
            File.WriteAllBytes(a[a.Count - 1], new byte[] { 9, 9 });
            return Task.FromResult(true);
        });
        var outPath = Path.Combine(_dir, "shorts.mp4");
        var r = await svc.StitchBabyShortsAsync(cuts, outPath, StoryboardStitchService.BuildBabyShortsOptions());
        Assert.True(r.Ok, r.Message);
        Assert.Equal(2, calls.Count);
        Assert.True(File.Exists(outPath));
        Assert.NotNull(r.MetaPath);
        Assert.False(File.Exists(outPath + ".concat.txt"));
    }
}
