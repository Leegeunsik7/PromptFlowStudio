using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.ViewModels;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🛡️ 자가 진단 감사 회귀 — drawtext 3단계 이스케이프 왕복(FFmpeg 파서 포팅 시뮬레이터) · Pluto 저장소 동시 기록/잠금/손상 내성 · 트렌드 보상 결선.
/// </summary>
public class PlutoHardeningAuditTests
{
    // ══════════════════════ FFmpeg 파서 포팅(libavutil av_get_token · libavfilter 옵션 · drawtext 확장) ══════════════════════

    private const string Ws = " \n\t\r";

    /// <summary>libavutil/avstring.c <c>av_get_token</c> 1:1 포팅 — 따옴표 내부 리터럴 · 백슬래시 1단계 소거 · 비보호 후행 공백 절단.</summary>
    private static string AvGetToken(string buf, ref int p, string term)
    {
        var o = new StringBuilder();
        var end = 0;
        while (p < buf.Length && Ws.IndexOf(buf[p]) >= 0) p++;
        while (p < buf.Length && term.IndexOf(buf[p]) < 0)
        {
            var c = buf[p++];
            if (c == '\\' && p < buf.Length) { o.Append(buf[p++]); end = o.Length; }
            else if (c == '\'')
            {
                while (p < buf.Length && buf[p] != '\'') o.Append(buf[p++]);
                if (p < buf.Length) { p++; end = o.Length; }
            }
            else o.Append(c);
        }
        var len = o.Length;
        while (len > end && Ws.IndexOf(o[len - 1]) >= 0) len--;
        return o.ToString(0, len);
    }

    /// <summary>필터 체인(<c>name=opts,name=opts</c>) → (이름, 옵션 원문) 목록 — 그래프 수준 토큰화.</summary>
    private static List<(string Name, string Opts)> ParseChain(string chain)
    {
        var list = new List<(string, string)>();
        var p = 0;
        while (p < chain.Length)
        {
            var name = AvGetToken(chain, ref p, "=,;[");
            var opts = "";
            if (p < chain.Length && chain[p] == '=') { p++; opts = AvGetToken(chain, ref p, "[],;"); }
            list.Add((name, opts));
            if (p < chain.Length)
            {
                Assert.Equal(',', chain[p]);   // 체인 구분자 외 문자 → 그래프 파싱 오류(따옴표 탈출)
                p++;
            }
        }
        return list;
    }

    /// <summary>옵션 수준 <c>key=value:key=value</c> 분해(<c>av_opt_get_key_value</c> · 값은 <c>av_get_token(":")</c>).</summary>
    private static Dictionary<string, string> ParseOptions(string opts)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var p = 0;
        while (p < opts.Length)
        {
            var k = p;
            while (p < opts.Length && opts[p] != '=') p++;
            Assert.True(p < opts.Length, "키에 '=' 없음(옵션 구분자 누수): " + opts[k..]);
            var key = opts[k..p];
            p++;
            d[key] = AvGetToken(opts, ref p, ":");
            if (p < opts.Length && opts[p] == ':') p++;
        }
        return d;
    }

    /// <summary>drawtext <c>expansion=normal</c> — <c>\x</c> → x · <c>%{…}</c> 외 <c>%</c> 는 FFmpeg "Stray %" 오류.</summary>
    private static string ExpandDrawText(string t)
    {
        var sb = new StringBuilder(t.Length);
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '\\' && i + 1 < t.Length) { sb.Append(t[++i]); continue; }
            if (t[i] == '%') throw new InvalidOperationException("Stray % near '" + t[i..] + "'");
            sb.Append(t[i]);
        }
        return sb.ToString();
    }

    private static string RenderedText(string filterChain, int index)
    {
        var chain = ParseChain(filterChain);
        Assert.Equal("drawtext", chain[index].Name);
        return ExpandDrawText(ParseOptions(chain[index].Opts)["text"]);
    }

    [Theory]
    [Trait("Category", "FastUnit")]
    [InlineData("엄마가 '사랑해' 라고 말했어")]
    [InlineData("It's 하윤's first step: 100% 행복")]
    [InlineData("C:\\Users\\mom\\letter — \"곁에\" 있을게, 언제나; [약속]")]
    [InlineData("%{pts} 는 확장되면 안 돼 \\% 도 그대로")]
    [InlineData("끝이 백슬래시 \\")]
    [InlineData("'''")]
    public void EscapeDrawText_RoundTripsThroughFfmpegParsers_ForParentLetterPunctuation(string letter)
    {
        var cues = new[] { new SubtitleCue(1, 4, letter), new SubtitleCue(5, 10, "두 번째, 막: '확인'"), new SubtitleCue(11, 14, "세 번째 %") };
        var filter = StockSuiteExporter.BuildTimedSubtitleFilter(cues, "C:\\Windows\\Fonts\\malgun.ttf");
        Assert.NotNull(filter);
        var chain = ParseChain(filter!);
        Assert.Equal(3, chain.Count);   // 텍스트 내 쉼표/따옴표가 체인을 쪼개지 않음

        Assert.Equal(letter.Trim(), RenderedText(filter!, 0));
        Assert.Equal("두 번째, 막: '확인'", RenderedText(filter!, 1));
        Assert.Equal("세 번째 %", RenderedText(filter!, 2));

        var o = ParseOptions(chain[0].Opts);
        Assert.Equal("between(t,1,4)", o["enable"]);
        Assert.Equal("C:/Windows/Fonts/malgun.ttf", o["fontfile"]);
        Assert.Equal("(w-text_w)/2", o["x"]);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void EscapeDrawText_NeutralizesControlCharacters_AndBadgeSurvivesQuotes()
    {
        Assert.Equal("a b c d", StockSuiteExporter.EscapeDrawText("a\tb\u0000c\u001Fd"));
        Assert.Equal("", StockSuiteExporter.EscapeDrawText(null));

        var badge = StockSuiteExporter.BuildGrowthBadgeFilter("🍼 O'Neil · D+100%\n첫 걸음", null);
        Assert.NotNull(badge);
        Assert.Single(ParseChain(badge!));
        Assert.Equal("O'Neil · D+100% 첫 걸음", RenderedText(badge!, 0));

        var ticker = StockSuiteExporter.BuildTickerBadgeFilter(new TickerBadgeData("aapl", "Apple's Inc.", 185.5m, 2.4), null);
        Assert.NotNull(ticker);
        var tc = ParseChain(ticker!);
        Assert.Equal("drawbox", tc[0].Name);
        Assert.Equal("AAPL · Apple's Inc.", ExpandDrawText(ParseOptions(tc[1].Opts)["text"]));
        Assert.Equal("$185.50 ▲ +2.4%", ExpandDrawText(ParseOptions(tc[2].Opts)["text"]));
    }

    // ══════════════════════ 🗄️ Pluto 저장소 동시성 · 잠금 · 손상 ══════════════════════

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoStores_ConcurrentWrites_PersistLatestState_WithoutTempLeftovers()
    {
        var dir = TempDir();
        try
        {
            var learnPath = Path.Combine(dir, PlutoLearningService.FileName);
            var dnaPath = Path.Combine(dir, PlutoPreferenceLexicon.FileName);
            var vaultPath = Path.Combine(dir, PlutoIdentityVault.FileName);
            using (var learn = new PlutoLearningService(learnPath, new Random(1)))
            using (var dna = new PlutoPreferenceLexicon(dnaPath))
            using (var vault = new PlutoIdentityVault(vaultPath))
            {
                // 👍 연타 + 내보내기 완료 + 레이더 보너스 + DNA 강화 + 프로필 저장 동시 발생
                var tasks = new List<Task>();
                for (var i = 0; i < 24; i++)
                {
                    var n = i;
                    tasks.Add(Task.Run(() => learn.RecordInteractionAsync("trend-" + (n % 3), n % 2 == 0 ? InteractionType.ThumbsUp : InteractionType.ExportCompleted)));
                    tasks.Add(Task.Run(() => learn.AddBonusXpAsync("social:x" + (n % 2), 2)));
                    tasks.Add(Task.Run(() => dna.ReinforceAsync("golden hour, slow push-in, warm tones " + n)));
                    tasks.Add(Task.Run(() => vault.SaveProfileAsync(new PlutoIdentityProfile { Id = "p" + n, SubjectName = "아기" + n, IsActive = false })));
                }
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
                Assert.Equal(48, learn.TotalInteractions);
            }

            // 재적재 = 마지막 상태(오래된 스냅샷이 나중에 기록되는 Lost-Update 없음)
            using (var learn2 = new PlutoLearningService(learnPath, new Random(1)))
            using (var dna2 = new PlutoPreferenceLexicon(dnaPath))
            using (var vault2 = new PlutoIdentityVault(vaultPath))
            {
                await learn2.EnsureLoadedAsync();
                await dna2.EnsureLoadedAsync();
                await vault2.EnsureLoadedAsync();
                Assert.Equal(48, learn2.TotalInteractions);
                Assert.Equal(PlutoLearningService.ComputeXp(0, 12, 12) + 24 * 2, learn2.CurrentXP);
                Assert.Equal(24, dna2.GetFrequency(DnaBucket.Lighting, "golden hour"));
                Assert.Equal(24, vault2.Profiles.Count);
            }
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoStores_LockedFile_NeverOverwritesExistingData_AndReportsPersistFailure()
    {
        var dir = TempDir();
        try
        {
            // ① 적재 시 잠김(공유 위반) → 빈 메모리로 동작하되 기존 누적 XP 파일을 덮어쓰지 않음
            var learnPath = Path.Combine(dir, PlutoLearningService.FileName);
            await File.WriteAllTextAsync(learnPath, "{\"keep\":{\"Weight\":1.5,\"Interactions\":9,\"Exports\":3}}");
            var original = await File.ReadAllBytesAsync(learnPath);
            using (var hold = new FileStream(learnPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            using (var learn = new PlutoLearningService(learnPath, new Random(1)))
            {
                await learn.RecordInteractionAsync("new-trend", InteractionType.ThumbsUp);   // 메모리 반영 · 디스크 기록 금지
                Assert.Equal(1, learn.TotalInteractions);
            }
            Assert.Equal(original, await File.ReadAllBytesAsync(learnPath));

            // ② 적재 정상 → 기록 시 대상 잠김 → 재시도 소진 false · 임시 파일 정리 → 해제 후 재저장 true
            var vaultPath = Path.Combine(dir, PlutoIdentityVault.FileName);
            using var vault = new PlutoIdentityVault(vaultPath);
            Assert.True(await vault.SaveProfileAsync(new PlutoIdentityProfile { SubjectName = "하윤이" }));
            using (new FileStream(vaultPath, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.False(await vault.SaveProfileAsync(new PlutoIdentityProfile { SubjectName = "콩이", Category = PlutoIdentityProfile.PetCategory }));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
            Assert.Equal("콩이", vault.ActiveProfile?.SubjectName);   // [부분 구제] 메모리 유지
            Assert.True(await vault.SaveProfileAsync(vault.ActiveProfile!));
            using var reload = new PlutoIdentityVault(vaultPath);
            Assert.Equal("콩이", (await reload.GetActiveProfileAsync())?.SubjectName);
            Assert.True(await vault.DeleteProfileAsync("no-such-id"));   // 대상 없음 = 디스크 일치
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoStores_CorruptJson_IsQuarantined_AndRadarNullFieldsAreRepaired()
    {
        var dir = TempDir();
        try
        {
            var dnaPath = Path.Combine(dir, PlutoPreferenceLexicon.FileName);
            await File.WriteAllTextAsync(dnaPath, "[{\"Bucket\":0,\"Phrase\":\"golden hour\"");   // 잘린 JSON
            using (var dna = new PlutoPreferenceLexicon(dnaPath))
            {
                await dna.EnsureLoadedAsync();
                Assert.Equal(0, dna.Count);
                Assert.True(File.Exists(dnaPath + ".corrupt"));   // 원본 격리 사본(수동 복구용)
                await dna.ReinforceAsync("soft window light");
            }
            using (var dna2 = new PlutoPreferenceLexicon(dnaPath))
            {
                await dna2.EnsureLoadedAsync();
                Assert.Equal(1, dna2.GetFrequency(DnaBucket.Lighting, "soft window light"));
            }

            // 수동 편집/구버전 null 필드 → Find/TopLearned NRE 없이 생략
            var radarPath = Path.Combine(dir, "pluto_social_radar.json");
            await File.WriteAllTextAsync(radarPath, "{\"pluto-social-llm-x\":{\"Weight\":3,\"Learned\":1,\"Title\":null,\"ViralMechanism\":null,\"PacingGuideline\":null,\"Camera\":null,\"Hashtags\":null,\"Category\":99}}");
            using var radar = new PlutoSocialRadarEngine(radarPath);
            await radar.EnsureLoadedAsync();
            Assert.Null(radar.Find("pluto-social-llm-x"));
            Assert.Empty(radar.TopLearned(5));
            Assert.Equal(3, radar.GetWeight("pluto-social-llm-x"));
        }
        finally { Cleanup(dir); }
    }

    // ══════════════════════ 🪐 트렌드 보상 결선 ══════════════════════

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PublishViewModel_BindPlutoTrend_ClearsStaleTrend_WhenEnteringWithoutTrend()
    {
        var dir = TempDir();
        try
        {
            using var svc = new PlutoLearningService(Path.Combine(dir, PlutoLearningService.FileName), new Random(3));
            using var vm = new PublishViewModel(new NullYt(), new NullIg(), learning: svc);
            Assert.Equal("baby-first-steps", vm.BindPlutoTrend(" baby-first-steps "));   // 트렌드 랩 → 작업실
            Assert.False(vm.IsPlutoRewardExportBusy);
            Assert.Null(vm.BindPlutoTrend(null));                                       // 트렌드 없이 재진입/프롬프트 교체 → 잔존 소거
            Assert.False(await vm.RecordPlutoExportAsync());                            // 무관 내보내기 → 오보상 없음
            Assert.Equal(0, svc.CurrentXP);
        }
        finally { Cleanup(dir); }
    }

    private sealed class NullYt : IYouTubeShortsPublisher
    {
        public bool Enabled => false;
        public Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null, IProgress<double>? progress = null, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new ShortsPublishResult(false, false, null, null, false, "off"));
        public Task<bool> TestConnectionAsync(System.Threading.CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NullIg : IInstagramReelsPublisher
    {
        public bool Enabled => false;
        public Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent, IProgress<double>? progress = null, System.Threading.CancellationToken ct = default)
            => Task.FromResult(new ReelsPublishOutcome(false, false, ReelsRoute.Mock, null, "off"));
        public Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, System.Threading.CancellationToken ct = default) => Task.FromResult(false);
    }
}
