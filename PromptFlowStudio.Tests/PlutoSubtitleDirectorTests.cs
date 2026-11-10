using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

public sealed class PlutoSubtitleDirectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_letter_" + Guid.NewGuid().ToString("N"));

    public PlutoSubtitleDirectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool IsAvailable { get; init; } = true;
        public string? Response { get; init; }
        public Exception? Throw { get; init; }
        public TimeSpan Delay { get; init; }
        public int Calls;
        public string LastUser = "";

        public async Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastUser = user;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, CancellationToken.None).ConfigureAwait(false);   // 토큰 무시 클라이언트 모사
            if (Throw is not null) throw Throw;
            return Response;
        }
    }

    [Fact]
    public async Task PlutoSubtitleDirector_GeneratesSynchronizedThreeActScript_WithFallback()
    {
        // 🧠 LLM 성공 → Gemini 편지 채택(이름 전달 · 이모지 제거 · 40자 상한)
        var ok = new FakeLlm { Response = "[{\"act\":1,\"startSec\":1,\"endSec\":4,\"ko\":\"햇살 같은 너의 첫 미소 🌞\",\"en\":\"Your first smile, like sunshine\"},{\"act\":2,\"startSec\":5,\"endSec\":10,\"ko\":\"세상을 향해 내딛는 작은 발걸음\",\"en\":\"Tiny steps toward the world\"},{\"act\":3,\"startSec\":11,\"endSec\":14,\"ko\":\"하린아, 언제나 사랑해\",\"en\":\"Harin, I'll always love you\"}]" };
        var s = await new PlutoSubtitleDirector(ok).GenerateLetterScriptAsync("baby first step in the park", "하린");
        Assert.True(s.UsedLlm);
        Assert.Equal("하린", s.SubjectName);
        Assert.Equal("햇살 같은 너의 첫 미소", s.Act1Text);
        Assert.Equal("하린아, 언제나 사랑해", s.Act3Text);
        Assert.Equal("Your first smile, like sunshine", s.Act1En);
        Assert.Equal("Harin, I'll always love you", s.Act3En);
        Assert.Contains("하린", ok.LastUser, StringComparison.Ordinal);
        Assert.Equal(new[] { (1, 4), (5, 10), (11, 14) }, PlutoSubtitleScript.ActWindows);
        var cues = s.ToCues();
        Assert.Equal(3, cues.Length);
        Assert.Equal((1, 4), (cues[0].StartSec, cues[0].EndSec));
        Assert.Equal((5, 10), (cues[1].StartSec, cues[1].EndSec));
        Assert.Equal((11, 14), (cues[2].StartSec, cues[2].EndSec));

        // [부분 구제] act2 누락 → 로컬 템플릿 보강
        var partial = await new PlutoSubtitleDirector(new FakeLlm { Response = "{\"acts\":[{\"act\":1,\"ko\":\"첫 줄\",\"en\":\"First line\"},{\"act\":3,\"ko\":\"마지막 줄\",\"en\":\"Last line\"}]}" }).GenerateLetterScriptAsync("baby first step", "하린");
        Assert.True(partial.UsedLlm);
        Assert.Equal("첫 줄", partial.Act1Text);
        Assert.Equal("First line", partial.Act1En);
        Assert.False(string.IsNullOrWhiteSpace(partial.Act2Text));
        Assert.False(string.IsNullOrWhiteSpace(partial.Act2En));

        // 💥 예외 · 빈 응답 · 깨진 JSON · 미설정 → 결정론 로컬 폴백(동일 입력 = 동일 결과)
        var expected = PlutoSubtitleDirector.GenerateLocal("baby first step in the park", "하린");
        foreach (var llm in new IPlutoLlmClient?[] { null, new FakeLlm { Throw = new InvalidOperationException("boom") }, new FakeLlm { Response = "" }, new FakeLlm { Response = "not json" }, new FakeLlm { IsAvailable = false },
                                                     new FakeLlm { Response = "{\"act1\":\"구 스키마\"}" }, new FakeLlm { Response = "[{\"act\":1,\"ko\":\"영어 누락\",\"en\":\"\"},{\"act\":2,\"ko\":\"한글만\",\"en\":\"한글 번역\"}]" } })
        {
            var f = await new PlutoSubtitleDirector(llm).GenerateLetterScriptAsync("baby first step in the park", "하린");
            Assert.False(f.UsedLlm);
            Assert.Equal(expected.Act1Text, f.Act1Text);
            Assert.Equal(expected.Act2Text, f.Act2Text);
            Assert.Equal(expected.Act3Text, f.Act3Text);
            Assert.Equal(expected.Act3En, f.Act3En);
        }
        Assert.Equal("walk", PlutoSubtitleDirector.ThemeOf("baby first step in the park"));
        Assert.StartsWith("하린아,", expected.Act3Text, StringComparison.Ordinal);

        // ⏱ 예산 초과(토큰 무시 클라이언트) → 로컬 폴백 — 기본 예산 3.5초 · 부하 무관 결정론 위해 짧은 예산 주입
        Assert.Equal(TimeSpan.FromMilliseconds(3500), new PlutoSubtitleDirector().LlmBudget);
        var slow = new FakeLlm { Response = "[{\"act\":1,\"ko\":\"a\",\"en\":\"a\"}]", Delay = TimeSpan.FromSeconds(30) };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var timed = await new PlutoSubtitleDirector(slow) { LlmBudget = TimeSpan.FromMilliseconds(150) }.GenerateLetterScriptAsync("baby nap", "도윤");
        sw.Stop();
        Assert.False(timed.UsedLlm);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(25), sw.Elapsed.ToString());
        Assert.Equal("sleep", PlutoSubtitleDirector.ThemeOf("baby nap"));

        // 🍼 아이덴티티 볼트 활성 프로필 이름 사용 · 호격 받침 규칙
        using var vault = new PlutoIdentityVault(Path.Combine(_dir, "vault.json"));
        await vault.SaveProfileAsync(new PlutoIdentityProfile { Id = "p1", SubjectName = "서아", IsActive = true });
        var v = await new PlutoSubtitleDirector(null, vault).GenerateLetterScriptAsync("first birthday cake");
        Assert.Equal("서아", v.SubjectName);
        Assert.StartsWith("서아야,", v.Act3Text, StringComparison.Ordinal);
        Assert.Equal("우리 아기", PlutoSubtitleDirector.GenerateLocal("", null).SubjectName);
        Assert.Equal("Mia,", PlutoSubtitleDirector.Vocative("Mia"));
    }

    [Fact]
    public void PlutoSubtitleDirector_FormatsValidSrtAndLetterText()
    {
        var s = PlutoSubtitleDirector.GenerateLocal("햇살 가득한 아침 산책", "하린");
        var srt = s.ToSrtFormat();
        var blocks = srt.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, blocks.Length);
        Assert.Equal("1\n00:00:01,000 --> 00:00:04,000\n" + s.Act1Text + "\n" + s.Act1En, blocks[0].TrimEnd('\n'));
        Assert.Equal("2\n00:00:05,000 --> 00:00:10,000\n" + s.Act2Text + "\n" + s.Act2En, blocks[1].TrimEnd('\n'));
        Assert.Equal("3\n00:00:11,000 --> 00:00:14,000\n" + s.Act3Text + "\n" + s.Act3En, blocks[2].TrimEnd('\n'));
        Assert.Matches(@"^\d+\n\d{2}:\d{2}:\d{2},\d{3} --> \d{2}:\d{2}:\d{2},\d{3}\n.+\n.+$", blocks[0].TrimEnd('\n'));
        Assert.Equal("", StockSuiteExporter.BuildSrt(null));

        var letter = s.ToFullLetterText();
        var lines = letter.Split('\n');
        Assert.Equal("💌 To. 하린", lines[0]);
        Assert.Equal("", lines[1]);
        Assert.Equal(s.Act1Text, lines[2]);
        Assert.Equal(s.Act2Text, lines[3]);
        Assert.Equal(s.Act3Text, lines[4]);
        Assert.StartsWith("From.", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain('\r', letter);
        Assert.DoesNotContain("{v}", letter, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StockSuiteExporter_AppliesTimedSubtitleFilters_WhenBurnSubtitlesEnabled()
    {
        var src = Path.Combine(_dir, "render.mp4");
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", runner);
        var script = PlutoSubtitleDirector.GenerateLocal("baby first step", "하린");

        // 🔕 OFF → drawtext enable 체인 · .srt 없음
        exp.SubtitleCues = script.ToCues();
        exp.BurnSubtitles = false;
        var off = await exp.ExportAsync(src, sharpen: true);
        Assert.True(off.Ok, off.Message);
        Assert.DoesNotContain(calls.SelectMany(c => c), a => a.Contains("between(t,", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.ChangeExtension(off.TargetPath!, ".srt")));

        // 💌 ON → 1-4 · 5-10 · 11-14 시간 게이트 drawtext + .srt 동반
        calls.Clear();
        exp.BurnSubtitles = true;
        var r = await exp.ExportAsync(src, sharpen: true);
        Assert.True(r.Ok, r.Message);
        var vf = calls[0][Array.IndexOf(calls[0], "-vf") + 1];
        Assert.Contains("enable='between(t,1,4)'", vf, StringComparison.Ordinal);
        Assert.Contains("enable='between(t,5,10)'", vf, StringComparison.Ordinal);
        Assert.Contains("enable='between(t,11,14)'", vf, StringComparison.Ordinal);
        Assert.Contains("fontsize=24:fontcolor=0xFFF1D6:box=1:boxcolor=black@0.45", vf, StringComparison.Ordinal);
        Assert.Contains("x=(w-text_w)/2:y=h-164", vf, StringComparison.Ordinal);
        Assert.Contains("fontsize=16:fontcolor=white@0.85", vf, StringComparison.Ordinal);
        Assert.Contains("x=(w-text_w)/2:y=h-120", vf, StringComparison.Ordinal);
        Assert.Contains(script.Act2Text, vf, StringComparison.Ordinal);
        Assert.True(vf.IndexOf("between(t,1,4)", StringComparison.Ordinal) < vf.IndexOf("between(t,11,14)", StringComparison.Ordinal));
        Assert.Contains(StockSuiteExporter.SubtitleTag, r.Message, StringComparison.Ordinal);
        var srtPath = Path.ChangeExtension(r.TargetPath!, ".srt");
        Assert.True(File.Exists(srtPath));
        Assert.Equal(script.ToSrtFormat(), File.ReadAllText(srtPath));

        // 정적 빌더 — 4K 2배 스케일 · 빈 큐 → null
        var k4 = StockSuiteExporter.BuildTimedSubtitleFilter(script.ToCues(), null, StockSuiteResolution.Uhd4K)!;
        Assert.Contains("fontsize=48", k4, StringComparison.Ordinal);
        Assert.Contains("fontsize=32", k4, StringComparison.Ordinal);
        Assert.Contains("y=h-328", k4, StringComparison.Ordinal);
        Assert.Contains("y=h-240", k4, StringComparison.Ordinal);
        Assert.Null(StockSuiteExporter.BuildTimedSubtitleFilter(Array.Empty<SubtitleCue>(), null));
        Assert.Null(StockSuiteExporter.BuildTimedSubtitleFilter(new[] { new SubtitleCue(1, 4, "  ") }, null));
    }

    [Fact]
    public async Task Workspace_ExportWithSubtitles_AppliesTimedBurnInAndCreatesSrt()
    {
        var src = Path.Combine(_dir, "ws_render.mp4");
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        var target = Path.Combine(_dir, "WsStockSuite");
        Directory.CreateDirectory(target);
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", runner);
        var script = await new PlutoSubtitleDirector().GenerateLetterScriptAsync("baby first step in the park", "하린");

        // 💌 토글 ON → 단일 렌더 경로에 1-4 · 5-10 · 11-14 시간 게이트 drawtext + {출력명}.srt
        var r = await exp.ExportWithSubtitlesAsync(src, script.ToCues());
        Assert.True(r.Ok, r.Message);
        var vf = calls[0][Array.IndexOf(calls[0], "-vf") + 1];
        Assert.Contains("enable='between(t,1,4)'", vf, StringComparison.Ordinal);
        Assert.Contains("enable='between(t,5,10)'", vf, StringComparison.Ordinal);
        Assert.Contains("enable='between(t,11,14)'", vf, StringComparison.Ordinal);
        Assert.Contains(script.Act1Text, vf, StringComparison.Ordinal);
        Assert.Contains(StockSuiteExporter.SubtitleTag, r.Message, StringComparison.Ordinal);
        var srtPath = Path.ChangeExtension(r.TargetPath!, ".srt");
        Assert.Equal(Path.GetFileNameWithoutExtension(r.TargetPath!) + ".srt", Path.GetFileName(srtPath));
        Assert.Equal(script.ToSrtFormat(), File.ReadAllText(srtPath));

        // 공유 싱글톤 누수 차단 — 호출 후 스위치/큐 원복
        Assert.False(exp.BurnSubtitles);
        Assert.Null(exp.SubtitleCues);

        // 🔕 토글 OFF(null 큐) → drawtext 시간 게이트 없음 · .srt 미생성
        File.Delete(srtPath);
        calls.Clear();
        var off = await exp.ExportWithSubtitlesAsync(src, null);
        Assert.True(off.Ok, off.Message);
        Assert.DoesNotContain(calls.SelectMany(c => c), a => a.Contains("between(t,", StringComparison.Ordinal));
        Assert.DoesNotContain(StockSuiteExporter.SubtitleTag, off.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.ChangeExtension(off.TargetPath!, ".srt")));
    }

    [Fact]
    public async Task SubtitleDirector_GeneratesBilingualActs_FromTemplatesAndLlm()
    {
        // 🍼 8 테마 × 다양 시드 — 모든 막 KO/EN 쌍 채움 · 플레이스홀더 잔존 없음 · EN 길이 상한
        var prompts = new[] { "first birthday cake", "100일 기념", "baby first step", "baby nap", "bubble bath", "puppy playing", "snow day", "평범한 하루" };
        var themes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in prompts)
        {
            themes.Add(PlutoSubtitleDirector.ThemeOf(p));
            foreach (var name in new[] { "하린", "Mia", "도윤", null })
            {
                var s = PlutoSubtitleDirector.GenerateLocal(p, name);
                foreach (var act in s.Acts)
                {
                    Assert.False(string.IsNullOrWhiteSpace(act.KoreanText));
                    Assert.False(string.IsNullOrWhiteSpace(act.EnglishText));
                    Assert.Equal(act.KoreanText, act.Text);
                    Assert.DoesNotContain("{", act.KoreanText + act.EnglishText, StringComparison.Ordinal);
                    Assert.True(act.EnglishText.Length <= PlutoSubtitleDirector.MaxEnglishLength, act.EnglishText);
                    Assert.True(act.EnglishText.Any(char.IsAsciiLetter), act.EnglishText);
                }
                Assert.Equal(new[] { (1, 4), (5, 10), (11, 14) }, s.Acts.Select(a => (a.StartSec, a.EndSec)).ToArray());
                Assert.StartsWith(name is null ? "My little one," : name + ",", s.Act3En, StringComparison.Ordinal);
            }
        }
        Assert.Equal(8, themes.Count);

        // 🧠 LLM 이중 언어 — act 번호 순서 무관 · 이모지 제거 · KO/EN 상한
        var longEn = new string('a', 200);
        var llm = new FakeLlm { Response = "```json\n[{\"act\":3,\"ko\":\"사랑해 💕\",\"en\":\"I love you 💕\"},{\"act\":2,\"ko\":\"오늘\",\"en\":\"" + longEn + "\"},{\"act\":1,\"ko\":\"아침\",\"en\":\"Morning\"},]\n```" };
        var g = await new PlutoSubtitleDirector(llm).GenerateLetterScriptAsync("baby nap", "도윤");
        Assert.True(g.UsedLlm);
        Assert.Equal(("아침", "Morning"), (g.Act1Text, g.Act1En));
        Assert.Equal(("사랑해", "I love you"), (g.Act3Text, g.Act3En));
        Assert.Equal(PlutoSubtitleDirector.MaxEnglishLength, g.Act2En.Length);
        Assert.Equal("My little one", PlutoSubtitleDirector.EnglishName("우리 아기"));

        // 📋 이중 언어 복사 본문
        var clip = g.ToBilingualClipboardText();
        var lines = clip.Split('\n');
        Assert.Equal("💌 To. 도윤", lines[0]);
        Assert.Equal(new[] { "", "아침", "Morning", "", "오늘", g.Act2En, "", "사랑해", "I love you", "", PlutoSubtitleScript.LetterSignatureKo, PlutoSubtitleScript.LetterSignatureEn }, lines[1..]);
        Assert.DoesNotContain('\r', clip);
    }

    [Fact]
    public void SubtitleDirector_BuildsBilingualSrt_WithBothLanguages()
    {
        var s = PlutoSubtitleDirector.GenerateLocal("first birthday cake", "서아");
        var blocks = s.ToSrtFormat().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, blocks.Length);
        var acts = s.Acts;
        for (var i = 0; i < 3; i++)
        {
            var lines = blocks[i].TrimEnd('\n').Split('\n');
            Assert.Equal(4, lines.Length);
            Assert.Equal((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), lines[0]);
            Assert.Equal(acts[i].KoreanText, lines[2]);
            Assert.Equal(acts[i].EnglishText, lines[3]);
        }
        Assert.Equal("00:00:11,000 --> 00:00:14,000", blocks[2].Split('\n')[1]);

        // 보조 자막 없음/공백 → 기존 단일 줄 SRT 호환 · 개행 평탄화
        var mono = StockSuiteExporter.BuildSrt(new[] { new SubtitleCue(1, 4, "한 줄"), new SubtitleCue(5, 10, "둘", "  "), new SubtitleCue(11, 14, "셋", "line\r\nthree") });
        Assert.Equal("1\n00:00:01,000 --> 00:00:04,000\n한 줄\n\n2\n00:00:05,000 --> 00:00:10,000\n둘\n\n3\n00:00:11,000 --> 00:00:14,000\n셋\nline  three\n", mono);
    }

    [Fact]
    public void StockSuiteExporter_AppliesBilingualDrawTextFilters()
    {
        var cues = new[]
        {
            new SubtitleCue(1, 4, "100% 행복: 첫 날", "100% joy: day one"),
            new SubtitleCue(5, 10, "엄마's 노래", "Let's sing, it's you\\me"),
            new SubtitleCue(11, 14, "한국어만", null),
        };
        var vf = StockSuiteExporter.BuildTimedSubtitleFilter(cues, "C:\\Fonts\\malgun.ttf")!;
        var parts = vf.Split(",drawtext=");
        Assert.Equal(5, parts.Length);   // KO+EN · KO+EN · KO 단독

        // 🔤 계층: KO 24 웜 크림 h-164 → EN 16 반투명 h-120 · 단일 언어 → 기존 24 흰색 h-120
        Assert.Contains("fontsize=24:fontcolor=0xFFF1D6", parts[0], StringComparison.Ordinal);
        Assert.EndsWith("y=h-164", parts[0], StringComparison.Ordinal);
        Assert.Contains("fontsize=16:fontcolor=white@0.85:box=1:boxcolor=black@0.3", parts[1], StringComparison.Ordinal);
        Assert.EndsWith("y=h-120", parts[1], StringComparison.Ordinal);
        Assert.Contains("fontsize=24:fontcolor=white:box=1:boxcolor=black@0.4", parts[4], StringComparison.Ordinal);
        Assert.EndsWith("y=h-120", parts[4], StringComparison.Ordinal);
        Assert.Contains("enable='between(t,1,4)'", parts[1], StringComparison.Ordinal);
        Assert.Contains("enable='between(t,5,10)'", parts[3], StringComparison.Ordinal);
        Assert.All(parts, x => Assert.Contains("fontfile='C\\:/Fonts/malgun.ttf':", x, StringComparison.Ordinal));

        // 🛡 EscapeDrawText 경유(% : ' \)
        Assert.Contains("text='" + StockSuiteExporter.EscapeDrawText("100% joy: day one") + "'", parts[1], StringComparison.Ordinal);
        Assert.Contains("100\\\\% joy\\: day one", parts[1], StringComparison.Ordinal);
        Assert.Contains("Let'\\\\\\''s sing", parts[3], StringComparison.Ordinal);
        Assert.Contains("you\\\\\\\\me", parts[3], StringComparison.Ordinal);
        Assert.Contains("엄마'\\\\\\''s", parts[2], StringComparison.Ordinal);

        // 📐 4K 2배 스케일
        var k4 = StockSuiteExporter.BuildTimedSubtitleFilter(cues, null, StockSuiteResolution.Uhd4K)!;
        Assert.Contains("fontsize=48:fontcolor=0xFFF1D6", k4, StringComparison.Ordinal);
        Assert.Contains("fontsize=32:fontcolor=white@0.85", k4, StringComparison.Ordinal);
        Assert.Contains("boxborderw=16:x=(w-text_w)/2:y=h-240", k4, StringComparison.Ordinal);
        Assert.Contains("y=h-328", k4, StringComparison.Ordinal);

        // 🔕 EN 이모지 전용 → 단일 언어 폴백
        var emojiOnly = StockSuiteExporter.BuildTimedSubtitleFilter(new[] { new SubtitleCue(1, 4, "한 줄", "💕") }, null)!;
        Assert.DoesNotContain(",drawtext=", emojiOnly, StringComparison.Ordinal);
        Assert.EndsWith("y=h-120", emojiOnly, StringComparison.Ordinal);
    }
}
