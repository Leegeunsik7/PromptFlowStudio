using System;
using System.Collections.Generic;
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

/// <summary>🛡️ Pluto 3.0 안정화 감사 — 임시 파일 정리 · 원본 무잠금 · 진행률 포화 · 장문 자막 클리핑 가드.</summary>
public sealed class PlutoStabilityAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_stab_" + Guid.NewGuid().ToString("N"));

    public PlutoStabilityAuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── 1. 임시 파일 정리 ──

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task BabyAudioMux_NonIoFfmpegFault_DeletesTempMuxAndSfxWavs()
    {
        var src = Path.Combine(_dir, "render.mp4");
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        var bgm = Path.Combine(_dir, "lullaby.wav");
        File.WriteAllBytes(bgm, new byte[64]);
        var sfxDir = Path.Combine(_dir, "sfx");
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);   // 부분 산출물 기록 후
            throw new InvalidOperationException("ffmpeg process crashed");                          // 비IO 예외(필터 밖)
        };
        var exp = new StockSuiteExporter(() => _dir, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = (_, _) => bgm,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            SfxTempDir = sfxDir,
            BabyBgm = BabyBgmTrack.Lullaby,
            IncludeSfx = true,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => exp.MuxBabyAudioIntoPrimaryAsync(src, CancellationToken.None));

        Assert.False(File.Exists(src + StockSuiteExporter.BabyBgmMuxTempSuffix));
        Assert.Empty(Directory.GetFiles(sfxDir));
        Assert.True(File.Exists(src));   // 원본 보존
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void SubtitleSidecar_Success_LeavesNoTempFile()
    {
        var target = Path.Combine(_dir, "letter.mp4");
        var exp = new StockSuiteExporter(() => _dir)
        {
            SubtitleCues = new[] { new SubtitleCue(1, 4, "사랑해", "I love you") },
        };
        var srt = exp.TryWriteSubtitleSidecar(target);
        Assert.NotNull(srt);
        Assert.True(File.Exists(srt));
        Assert.Equal(new[] { srt }, Directory.GetFiles(_dir));   // .srt.~pfs 잔여 없음
    }

    // ── 1b. 원본 이미지 무잠금 ──

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task VisionStory_ReadsImage_WhileUserAppHoldsWriteHandle()
    {
        var photo = Path.Combine(_dir, "IMG_0505.jpg");
        var body = new byte[4096];
        var exif = Encoding.ASCII.GetBytes("2024:05:05 10:30:00");
        Buffer.BlockCopy(exif, 0, body, 512, exif.Length);
        File.WriteAllBytes(photo, body);

        // 편집기·동기화 클라이언트가 쓰기 핸들 점유 중(FileShare.ReadWrite) — File.ReadAllBytes(FileShare.Read)는 공유 위반.
        using (new FileStream(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var plan = await new PlutoVisionStorySynthesizer().SynthesizeFromImageAsync(photo);
            Assert.Equal(new DateTime(2024, 5, 5, 10, 30, 0), plan.Attributes.CapturedAt);
        }

        // 읽기 종료 후 원본 독점 쓰기 가능(핸들 누수 없음).
        using (new FileStream(photo, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    // ── 2. 진행률 0 나눗셈 · 오버플로 가드 ──

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData(int.MaxValue, 2, 100)]
    [InlineData(int.MaxValue, 3, 100)]
    [InlineData(100, 3, 0)]
    [InlineData(200, 3, 100)]
    [InlineData(int.MinValue, 1, 0)]
    [InlineData(int.MinValue, 0, 0)]
    public void ProgressPercent_ExtremeXp_SaturatesWithoutOverflow(int xp, int tierIndex, int expected)
        => Assert.Equal(expected, PlutoDirectorRankService.ProgressPercent(xp, PlutoDirectorRankService.Tiers[tierIndex]));

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData(100, 100)]
    [InlineData(200, 100)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void ProgressPercent_DegenerateTierSpan_Returns100(int required, int next)
    {
        var tier = new PlutoDirectorTier(9, "t", "t", required, next, Array.Empty<string>(), "");
        Assert.Equal(100, PlutoDirectorRankService.ProgressPercent(150, tier));
        Assert.Equal(100, PlutoDirectorRankService.ProgressPercent(int.MaxValue, tier));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void DirectorRank_MaxTierHugeXp_ProgressCapped()
    {
        using var svc = new PlutoDirectorRankService(() => int.MaxValue);
        var t = svc.GetCurrentTier();
        Assert.Equal(PlutoDirectorRankService.MaxLevel, t.Level);
        Assert.Equal(100, PlutoDirectorRankService.ProgressPercent(svc.CurrentXp, t));
    }

    // ── 3. 장문 자막 클리핑 가드 ──

    [Fact]
    [Trait("Category", "FastUnit")]
    public void SubtitleFont_WithinLimits_KeepsBaseSizes()
    {
        Assert.Equal(24, StockSuiteExporter.FitSubtitleFontSize(new string('가', 30), 24));
        Assert.Equal(16, StockSuiteExporter.FitSubtitleFontSize(new string('a', 65), 16));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void SubtitleFont_OverLimits_ScalesProportionally()
    {
        Assert.Equal(18, StockSuiteExporter.FitSubtitleFontSize(new string('가', 40), 24));   // 24·30/40
        Assert.Equal(14, StockSuiteExporter.FitSubtitleFontSize(new string('a', 72), 16));   // 16·65/72
        Assert.Equal(StockSuiteExporter.SubtitleMinFontSize, StockSuiteExporter.FitSubtitleFontSize(new string('가', 500), 24));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void SubtitleText_BeyondMinFontCapacity_Ellipsized()
    {
        var huge = string.Join(' ', Enumerable.Repeat("forever", 60));
        var fit = StockSuiteExporter.FitSubtitleText(huge, 16);
        Assert.EndsWith("...", fit, StringComparison.Ordinal);
        Assert.True(fit.Length <= StockSuiteExporter.SubtitleEnFitChars * 16 / StockSuiteExporter.SubtitleMinFontSize);
        Assert.Equal("짧은 편지", StockSuiteExporter.FitSubtitleText("짧은 편지", 24));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void TimedSubtitleFilter_LongBilingualCue_EmitsScaledFontSizes()
    {
        var ko = new string('사', 40);
        var en = new string('l', 72);
        var cues = new[] { new SubtitleCue(1, 4, ko, en), new SubtitleCue(5, 8, "짧은 줄", "short") };
        var vf = StockSuiteExporter.BuildTimedSubtitleFilter(cues, null)!;
        Assert.Contains("fontsize=18:fontcolor=0xFFF1D6", vf, StringComparison.Ordinal);
        Assert.Contains("fontsize=14:fontcolor=white@0.85", vf, StringComparison.Ordinal);
        Assert.Contains("fontsize=24:fontcolor=0xFFF1D6", vf, StringComparison.Ordinal);   // 짧은 큐 기본 유지
        Assert.Contains("fontsize=16:fontcolor=white@0.85", vf, StringComparison.Ordinal);

        var k4 = StockSuiteExporter.BuildTimedSubtitleFilter(cues, null, StockSuiteResolution.Uhd4K)!;
        Assert.Contains("fontsize=36:fontcolor=0xFFF1D6", k4, StringComparison.Ordinal);
        Assert.Contains("fontsize=28:fontcolor=white@0.85", k4, StringComparison.Ordinal);
    }
}
