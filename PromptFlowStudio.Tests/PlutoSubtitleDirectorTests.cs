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
        var ok = new FakeLlm { Response = "{\"act1\":\"햇살 같은 너의 첫 미소 🌞\",\"act2\":\"세상을 향해 내딛는 작은 발걸음\",\"act3\":\"하린아, 언제나 사랑해\"}" };
        var s = await new PlutoSubtitleDirector(ok).GenerateLetterScriptAsync("baby first step in the park", "하린");
        Assert.True(s.UsedLlm);
        Assert.Equal("하린", s.SubjectName);
        Assert.Equal("햇살 같은 너의 첫 미소", s.Act1Text);
        Assert.Equal("하린아, 언제나 사랑해", s.Act3Text);
        Assert.Contains("하린", ok.LastUser, StringComparison.Ordinal);
        Assert.Equal(new[] { (1, 4), (5, 10), (11, 14) }, PlutoSubtitleScript.ActWindows);
        var cues = s.ToCues();
        Assert.Equal(3, cues.Length);
        Assert.Equal((1, 4), (cues[0].StartSec, cues[0].EndSec));
        Assert.Equal((5, 10), (cues[1].StartSec, cues[1].EndSec));
        Assert.Equal((11, 14), (cues[2].StartSec, cues[2].EndSec));

        // [부분 구제] act2 누락 → 로컬 템플릿 보강
        var partial = await new PlutoSubtitleDirector(new FakeLlm { Response = "{\"act1\":\"첫 줄\",\"act3\":\"마지막 줄\"}" }).GenerateLetterScriptAsync("baby first step", "하린");
        Assert.True(partial.UsedLlm);
        Assert.Equal("첫 줄", partial.Act1Text);
        Assert.False(string.IsNullOrWhiteSpace(partial.Act2Text));

        // 💥 예외 · 빈 응답 · 깨진 JSON · 미설정 → 결정론 로컬 폴백(동일 입력 = 동일 결과)
        var expected = PlutoSubtitleDirector.GenerateLocal("baby first step in the park", "하린");
        foreach (var llm in new IPlutoLlmClient?[] { null, new FakeLlm { Throw = new InvalidOperationException("boom") }, new FakeLlm { Response = "" }, new FakeLlm { Response = "not json" }, new FakeLlm { IsAvailable = false } })
        {
            var f = await new PlutoSubtitleDirector(llm).GenerateLetterScriptAsync("baby first step in the park", "하린");
            Assert.False(f.UsedLlm);
            Assert.Equal(expected.Act1Text, f.Act1Text);
            Assert.Equal(expected.Act2Text, f.Act2Text);
            Assert.Equal(expected.Act3Text, f.Act3Text);
        }
        Assert.Equal("walk", PlutoSubtitleDirector.ThemeOf("baby first step in the park"));
        Assert.StartsWith("하린아,", expected.Act3Text, StringComparison.Ordinal);

        // ⏱ 예산 초과(토큰 무시 클라이언트) → 로컬 폴백 — 기본 예산 3.5초 · 부하 무관 결정론 위해 짧은 예산 주입
        Assert.Equal(TimeSpan.FromMilliseconds(3500), new PlutoSubtitleDirector().LlmBudget);
        var slow = new FakeLlm { Response = "{\"act1\":\"a\",\"act2\":\"b\",\"act3\":\"c\"}", Delay = TimeSpan.FromSeconds(30) };
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
        Assert.Equal("1\n00:00:01,000 --> 00:00:04,000\n" + s.Act1Text, blocks[0].TrimEnd('\n'));
        Assert.Equal("2\n00:00:05,000 --> 00:00:10,000\n" + s.Act2Text, blocks[1].TrimEnd('\n'));
        Assert.Equal("3\n00:00:11,000 --> 00:00:14,000\n" + s.Act3Text, blocks[2].TrimEnd('\n'));
        Assert.Matches(@"^\d+\n\d{2}:\d{2}:\d{2},\d{3} --> \d{2}:\d{2}:\d{2},\d{3}\n.+$", blocks[0].TrimEnd('\n'));
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
        Assert.Contains("fontsize=24:fontcolor=white:box=1:boxcolor=black@0.4", vf, StringComparison.Ordinal);
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
        Assert.Contains("y=h-240", k4, StringComparison.Ordinal);
        Assert.Null(StockSuiteExporter.BuildTimedSubtitleFilter(Array.Empty<SubtitleCue>(), null));
        Assert.Null(StockSuiteExporter.BuildTimedSubtitleFilter(new[] { new SubtitleCue(1, 4, "  ") }, null));
    }
}
