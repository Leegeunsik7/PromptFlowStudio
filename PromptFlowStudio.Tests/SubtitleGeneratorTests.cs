using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>컷 타임코드 → 표준 SRT 정합성 · 합본 옆 <c>{project}_full.srt</c> 자동 기록 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class SubtitleGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_srt_" + Guid.NewGuid().ToString("N"));

    public SubtitleGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SubtitleGenerator_ProducesValidSrtFormat()
    {
        var srt = SubtitleGeneratorService.BuildSrt(new[]
        {
            new SubtitleCut(5.0, "첫 번째 컷"),
            new SubtitleCut(2.5, null),                       // 대본 없음 — 시간만 소비
            new SubtitleCut(3.25, "둘째 줄\r\n\r\n  이어지는 줄  "),
            new SubtitleCut(3661.5, "긴 컷"),
        });

        const string expected =
            "1\r\n00:00:00,000 --> 00:00:05,000\r\n첫 번째 컷\r\n\r\n" +
            "2\r\n00:00:07,500 --> 00:00:10,750\r\n둘째 줄\r\n이어지는 줄\r\n\r\n" +
            "3\r\n00:00:10,750 --> 01:01:12,250\r\n긴 컷\r\n\r\n";
        Assert.Equal(expected, srt);

        // 블록 구조: 번호 · 타임코드(HH:mm:ss,fff --> HH:mm:ss,fff) · 텍스트 · 빈 줄
        var blocks = srt.Split("\r\n\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, blocks.Length);
        var rx = new System.Text.RegularExpressions.Regex(@"^\d{2}:\d{2}:\d{2},\d{3} --> \d{2}:\d{2}:\d{2},\d{3}$");
        for (var i = 0; i < blocks.Length; i++)
        {
            var lines = blocks[i].Split("\r\n");
            Assert.Equal((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), lines[0]);
            Assert.Matches(rx, lines[1]);
            Assert.True(lines.Length >= 3);
        }

        Assert.Equal("00:00:00,000", SubtitleGeneratorService.FormatTimestamp(-1));
        Assert.Equal("00:00:01,001", SubtitleGeneratorService.FormatTimestamp(1.0006));
        Assert.Equal("", SubtitleGeneratorService.BuildSrt(new[] { new SubtitleCut(3, " "), new SubtitleCut(0, "zero") }));
    }

    [Fact]
    public async Task StitchCuts_WritesFullSrt_BesideVideo_EvenWhenVirtual()
    {
        var a = Path.Combine(_dir, "a.mp4"); File.WriteAllText(a, "A");
        var b = Path.Combine(_dir, "b.mp4"); File.WriteAllText(b, "B");
        var svc = new FFmpegVideoStitchService(() => null, null);   // ffmpeg 부재 → 가상 병합
        var output = FFmpegVideoStitchService.BuildFullOutputPath(_dir, "My Project");

        var r = await svc.StitchCutsAsync(new[] { new StitchCut(a, 3, "하나"), new StitchCut(b, 4, "둘") }, output, null, new SubtitleGeneratorService());

        Assert.True(r.Ok);
        Assert.True(r.IsVirtual);
        Assert.False(r.AudioMuxed);
        var srtPath = Path.Combine(_dir, "My Project_full.srt");
        Assert.Equal(srtPath, r.SubtitlePath);
        Assert.Equal("1\r\n00:00:00,000 --> 00:00:03,000\r\n하나\r\n\r\n2\r\n00:00:03,000 --> 00:00:07,000\r\n둘\r\n\r\n", File.ReadAllText(srtPath));
        Assert.False(File.Exists(srtPath + ".tmp"));
        Assert.Contains("자막", r.Message);

        // 대본이 하나도 없으면 SRT 생략
        var r2 = await svc.StitchCutsAsync(new[] { new StitchCut(a, 3, null) }, Path.Combine(_dir, "x_full.mp4"), null, new SubtitleGeneratorService());
        Assert.Null(r2.SubtitlePath);
        Assert.False(File.Exists(Path.Combine(_dir, "x_full.srt")));
    }
}
