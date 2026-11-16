using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🧠📋📱 Pluto 4.6 — 두뇌 리포트 집계 · 쇼츠/릴스 정규화 복사 포매터 · 9:16 자막 세이프 존 · 심리스 루프 페이드.</summary>
public sealed class Pluto46BrainSummaryAndSnsFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_pluto46_" + Guid.NewGuid().ToString("N"));

    public Pluto46BrainSummaryAndSnsFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static PlutoVariantABPlan Plan(string id)
    {
        var cues = new[] { SubtitleCue.Precise(0.2, 4.95, "a") };
        var a = new PlutoExperimentVariant(PlutoVariantABGenerator.ChampionLabel, "이 순간을 기다렸어요", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", "", cues);
        var b = new PlutoExperimentVariant(PlutoVariantABGenerator.ChallengerLabel, "3초만 보면 웃음 터져요", PlutoColorGradingCompiler.GoldenHourWarmName, "Lullaby", "", cues);
        return new PlutoVariantABPlan(id, "돌잔치", a, b, "h");
    }

    private static PlutoSnsPublishingKit Kit(string title, params string[] tags)
        => new(new[] { title, "두 번째 제목", "세 번째 제목" }, "우리 아기 첫 걸음마 💕", "Baby's very first steps", tags, "20:30–22:30 KST");

    [Fact]
    public async Task BrainSummaryService_AggregatesEvaluatedExperimentsWinRateAndMistakeCount()
    {
        using var notebook = new PlutoMistakeNotebookService(Path.Combine(_dir, "nb.json"));
        using var ledger = new PlutoExperimentLedgerService(null);
        var ranker = new PlutoAdaptivePatternRanker(null, notebook);

        await ledger.EvaluateExperimentAsync(Plan("e1"), new PlutoExperimentMetricsInput(50, 40, 3), new PlutoExperimentMetricsInput(70, 55, 6));   // 챌린저
        await ledger.EvaluateExperimentAsync(Plan("e2"), new PlutoExperimentMetricsInput(40, 30, 2), new PlutoExperimentMetricsInput(80, 70, 7));   // 챌린저
        await ledger.EvaluateExperimentAsync(Plan("e3"), new PlutoExperimentMetricsInput(80, 70, 7), new PlutoExperimentMetricsInput(30, 20, 1));   // 챔피언
        ledger.RegisterExperiment(Plan("e4-pending"));

        await notebook.RecordNegativeFeedbackAsync(new[] { "sad-face", "dark-film" }, 1.0);
        await notebook.RecordNegativeFeedbackAsync(new[] { "sad-face", "dark-film" }, 1.0);   // 2.0 ≥ 1.8 → 차단

        var svc = new PlutoAutonomousBrainSummaryService(ledger, ranker, notebook);
        var s = await svc.GetSummaryAsync();

        Assert.Equal(3, s.TotalEvaluatedExperiments);
        Assert.Equal(1, s.PendingExperimentsCount);
        Assert.Equal(66.7, s.ChallengerWinRatePct, 1);
        Assert.Equal(2, s.ActiveMistakeGuardCount);
        Assert.False(string.IsNullOrWhiteSpace(s.TopHookLabel));
        Assert.False(string.IsNullOrWhiteSpace(s.TopLookLabel));
        Assert.False(string.IsNullOrWhiteSpace(s.TopBgmLabel));
        Assert.Contains("챌린저 우세", s.NextHypothesisBanner, StringComparison.Ordinal);
        Assert.Contains("대기 1건", s.NextHypothesisBanner, StringComparison.Ordinal);
        Assert.Contains("오답 가드 2개", s.NextHypothesisBanner, StringComparison.Ordinal);

        // 소스 전무 → 0 · 기본 라벨 · 첫 실험 유도 배너([부분 구제])
        var empty = await new PlutoAutonomousBrainSummaryService(null, null, null).GetSummaryAsync();
        Assert.Equal(0, empty.TotalEvaluatedExperiments);
        Assert.Equal(0.0, empty.ChallengerWinRatePct);
        Assert.Equal(PlutoAutonomousBrainSummaryService.NoneLabel, empty.TopHookLabel);
        Assert.Contains("첫 A/B 실험", empty.NextHypothesisBanner, StringComparison.Ordinal);
    }

    [Fact]
    public void SnsPublishingKit_FormatForYouTubeShorts_ClampsTitleUnder95CharsAndNormalizesTags()
    {
        var longTitle = string.Join(' ', new string('가', 10), new string('나', 10), new string('다', 10), new string('라', 10), new string('마', 10),
            new string('바', 10), new string('사', 10), new string('아', 10), new string('자', 10), new string('차', 10));   // 109자
        var kit = Kit(longTitle, "#baby", "Baby", "# 육아 일기", "first steps!", "#BABY", "돌잔치", "vlog", "kids");
        var text = PlutoMultiFormatPackager.FormatForYouTubeShorts(kit);
        var lines = text.Split('\n');

        Assert.True(lines[0].Length <= PlutoMultiFormatPackager.ShortsTitleMaxChars, lines[0]);
        Assert.EndsWith("…", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^[가-힣 ]+…$", lines[0]);
        Assert.EndsWith(new string('아', 10) + "…", lines[0], StringComparison.Ordinal);   // 단어 경계 절단(중간 절단 없음)
        Assert.Contains("우리 아기 첫 걸음마", text, StringComparison.Ordinal);
        Assert.Contains("Baby's very first steps", text, StringComparison.Ordinal);
        Assert.EndsWith("#baby #육아일기 #firststeps #돌잔치 #vlog", text, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(text, @"(?<!\S)#\S+").Count);

        // 서로게이트 비분할 — 공백 없는 이모지 연속 제목
        var emoji = PlutoMultiFormatPackager.ClampTitle(new string('a', 93) + "😀😀😀");
        Assert.True(emoji.Length <= 95);
        Assert.False(char.IsHighSurrogate(emoji[^2]));
        Assert.Equal("짧은 제목", PlutoMultiFormatPackager.ClampTitle("  짧은 제목 "));
    }

    [Fact]
    public void SnsPublishingKit_FormatForInstagramReels_DeduplicatesHashtagsAndIncludesCaption()
    {
        var tags = new[] { "#아기", "아기", "#ARMY", "#army", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l" };
        var text = PlutoMultiFormatPackager.FormatForInstagramReels(Kit("제목", tags));

        Assert.StartsWith("우리 아기 첫 걸음마 💕\n\nBaby's very first steps", text, StringComparison.Ordinal);
        Assert.DoesNotContain("제목", text, StringComparison.Ordinal);
        var tagLine = Array.Find(text.Split('\n'), l => l.StartsWith('#'))!;
        var parsed = tagLine.Split(' ');
        Assert.Equal(PlutoMultiFormatPackager.ReelsMaxHashtags, parsed.Length);
        Assert.Equal(parsed.Length, new System.Collections.Generic.HashSet<string>(parsed, StringComparer.OrdinalIgnoreCase).Count);
        Assert.Equal("#아기", parsed[0]);
        Assert.Equal("#ARMY", parsed[1]);
        Assert.All(parsed, t => Assert.Matches(@"^#[^\s#]+$", t));
        Assert.EndsWith("🕗 추천 게시 시간: 20:30–22:30 KST", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Vertical9x16SubtitleSafeZone_UsesElevatedBottomMargin_AboveMobileUiOverlay()
    {
        var mono = new[] { SubtitleCue.Precise(0.5, 4.5, "첫 걸음") };
        var bilingual = new[] { SubtitleCue.Precise(0.5, 4.5, "첫 걸음", "First steps") };

        // 16:9/기본 — 기존 오프셋 유지(회귀 차단)
        Assert.Contains("y=h-120", StockSuiteExporter.BuildTimedSubtitleFilter(mono, null)!, StringComparison.Ordinal);

        // 9:16 — 최하단 박스 하단 ≥ 높이 24%(1920 → ≥ 440 · 461)
        var margin = StockSuiteExporter.ComputeSubtitleBottomMarginPx(true);
        Assert.True(margin >= 440, margin.ToString());
        Assert.True(margin >= 1920 * StockSuiteExporter.VerticalSubtitleSafeZoneFraction);
        Assert.True(StockSuiteExporter.ComputeSubtitleBottomMarginPx(true, StockSuiteResolution.Uhd4K) >= 3840 * StockSuiteExporter.VerticalSubtitleSafeZoneFraction);
        Assert.True(StockSuiteExporter.ComputeSubtitleBottomMarginPx(false) < 440);

        var v = StockSuiteExporter.BuildTimedSubtitleFilter(mono, null, StockSuiteResolution.Fhd1080, vertical9x16: true)!;
        Assert.DoesNotContain("y=h-120", v, StringComparison.Ordinal);
        var offsets = Regex.Matches(v, @"y=h-(\d+)");
        Assert.NotEmpty(offsets);
        foreach (Match m in offsets) Assert.True(int.Parse(m.Groups[1].Value) - StockSuiteExporter.SubtitlePrimaryFontSize - 10 >= 440, m.Value);

        var vb = StockSuiteExporter.BuildTimedSubtitleFilter(bilingual, null, StockSuiteResolution.Fhd1080, vertical9x16: true)!;
        foreach (Match m in Regex.Matches(vb, @"y=h-(\d+)")) Assert.True(int.Parse(m.Groups[1].Value) - StockSuiteExporter.SubtitlePrimaryFontSize - 10 >= 440, m.Value);

        // 몽타주 그래프 말미 번인도 9:16 플래그 전달
        var graph = StockSuiteExporter.AppendMontageSubtitles("[0:v]null[vout]", "vout", mono, null, StockSuiteResolution.Fhd1080, vertical9x16: true);
        Assert.Contains("y=h-" + (120 + StockSuiteExporter.VerticalSubtitleSafeLift).ToString(), graph, StringComparison.Ordinal);

        // 🔁 15초 심리스 루프 — 0.3s in · 0.6s out 경계 페이드(기본 1.5s 는 유지)
        Assert.Equal("afade=t=in:st=0:d=0.3,afade=t=out:st=14.40:d=0.6", StockSuiteExporter.BuildBabyBgmAudioFilter(15, mastered: true, seamlessLoop: true));
        Assert.Equal("afade=t=in:st=0:d=1.5,afade=t=out:st=13.50:d=1.5", StockSuiteExporter.BuildBabyBgmAudioFilter(15, mastered: true));
        var args = StockSuiteExporter.BuildBabyBgmMuxArgs("in.mp4", "m.wav", 15, "out.mp4", mastered: true, seamlessLoop: true);
        Assert.Equal("afade=t=in:st=0:d=0.3,afade=t=out:st=14.40:d=0.6", args[Array.IndexOf(args, "-af") + 1]);
    }
}
