using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

public class BilingualBridgeBgmLauncherTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public int Calls;
        public string? LastBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return f(request);
        }
    }

    private static HttpResponseMessage Text(string t) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":" + System.Text.Json.JsonSerializer.Serialize(t) + "}]}}]}", Encoding.UTF8, "application/json"),
    };

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs_bbl_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task GeminiPromptService_TranslatesKoreanPromptToCinematicEnglish()
    {
        Assert.True(GeminiPromptService.ContainsHangul("노을 해변"));
        Assert.True(GeminiPromptService.ContainsHangul("ㅋㅋ"));
        Assert.False(GeminiPromptService.ContainsHangul("sunset beach, 8k"));
        Assert.False(GeminiPromptService.ContainsHangul(null));

        // API 경로: 번역 모드 지침 주입 + 영문 치환 + 원문 보존
        const string en = "A golden retriever running along a sunset beach, warm rim lighting, 35mm anamorphic lens, shallow depth of field, 8k";
        var h = new StubHandler(_ => Text(en));
        var svc = new GeminiPromptService(new HttpClient(h), () => "k");
        var r = await svc.EnhancePromptAsync("노을 지는 해변을 달리는 골든리트리버", VideoDurationMode.Sec5_Single);
        Assert.False(r.UsedFallback);
        Assert.True(r.Translated);
        Assert.Equal(en, r.Prompt);
        Assert.Equal("노을 지는 해변을 달리는 골든리트리버", r.SourceKorean);
        Assert.Contains("Professional Cinematic Video Prompt Translation", h.LastBody);

        // 영문 입력은 번역 모드 미적용
        var h2 = new StubHandler(_ => Text("a cat on a sofa, soft light"));
        var r2 = await new GeminiPromptService(new HttpClient(h2), () => "k").EnhancePromptAsync("a cat", VideoDurationMode.Sec5_Single);
        Assert.False(r2.Translated);
        Assert.DoesNotContain("Prompt Translation", h2.LastBody);

        // 모델이 한글을 남기면 로컬 용어집 폴백(원문 보존 · 예외 0)
        var h3 = new StubHandler(_ => Text("노을 해변의 강아지"));
        var r3 = await new GeminiPromptService(new HttpClient(h3), () => "k").EnhancePromptAsync("노을 해변의 강아지", VideoDurationMode.Sec5_Single);
        Assert.True(r3.UsedFallback);
        Assert.Equal(GeminiPromptService.TranslateFallbackNotice, r3.Message);
        Assert.StartsWith("sunset beach puppy, ", r3.Prompt);
        Assert.Contains("8K photorealistic", r3.Prompt);
        Assert.Equal("노을 해변의 강아지", r3.SourceKorean);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task AudioMuxService_BuildsCorrectFfmpegMuxingArguments()
    {
        var args = AudioMuxService.BuildMuxArgs("in.mp4", "bgm.mp3", 10, "out_sns.mp4");
        var s = string.Join(' ', args);
        Assert.Contains("-i in.mp4 -stream_loop -1 -i bgm.mp3", s);
        Assert.Contains("-map 0:v:0 -map 1:a:0", s);
        Assert.Contains("-c:v copy -c:a aac -b:a 192k -shortest", s);
        Assert.Contains("-af afade=t=out:st=8.5:d=1.5", s);
        Assert.Contains("-t 10", s);
        Assert.Equal("out_sns.mp4", args[^1]);

        Assert.Contains("afade=t=out:st=3.5:d=1.5", string.Join(' ', AudioMuxService.BuildMuxArgs("a", "b", 5, "c")));
        Assert.Contains("afade=t=out:st=13.5:d=1.5", string.Join(' ', AudioMuxService.BuildMuxArgs("a", "b", 15, "c")));
        var unknown = string.Join(' ', AudioMuxService.BuildMuxArgs("a", "b", 0, "c"));
        Assert.DoesNotContain("afade", unknown);
        Assert.Contains("-shortest", unknown);
        Assert.Equal(0, AudioMuxService.FadeStart(1));
        Assert.EndsWith("clip_sns.mp4", AudioMuxService.BuildOutputPath(Path.Combine(Path.GetTempPath(), "clip.mp4")));
        Assert.Equal(SnsBgmPreset.LofiBeat, AudioMuxService.ParsePreset("lofi"));
        Assert.Equal(SnsBgmPreset.None, AudioMuxService.ParsePreset("??"));

        // 실행 경로: 사용자 MP3 → 러너에 동일 인자 전달, 출력 생성 시 Ok
        var dir = TempDir();
        try
        {
            var video = Path.Combine(dir, "clip.mp4");
            var bgm = Path.Combine(dir, "song.mp3");
            File.WriteAllBytes(video, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(bgm, new byte[] { 4, 5, 6 });
            IReadOnlyList<string>? seen = null;
            FfmpegRunner run = (exe, a, ct) => { seen = a; File.WriteAllBytes(a[^1], new byte[] { 9 }); return Task.FromResult(true); };
            var mux = new AudioMuxService(() => "ffmpeg.exe", run, dir);
            var ok = await mux.MuxAsync(video, SnsBgmPreset.CustomFile, bgm, 10, CancellationToken.None);
            Assert.True(ok.Ok);
            Assert.Equal(AudioMuxService.BuildOutputPath(video), ok.OutputPath);
            Assert.NotNull(seen);
            Assert.Contains("afade=t=out:st=8.5:d=1.5", seen!);

            // ffmpeg 부재 → 원본 유지(예외 0)
            var none = await new AudioMuxService(() => null, run, dir).MuxAsync(video, SnsBgmPreset.CustomFile, bgm, 10);
            Assert.False(none.Ok);
            Assert.Equal(video, none.OutputPath);

            // 무음 → 합성 생략
            Assert.False((await mux.MuxAsync(video, SnsBgmPreset.None, null, 10)).Ok);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void StockSuiteExporter_ValidatesExecutablePathSuccessfully()
    {
        var dir = TempDir();
        try
        {
            var exe = Path.Combine(dir, "StockSuite.exe");
            var txt = Path.Combine(dir, "readme.txt");
            File.WriteAllBytes(exe, new byte[] { 0x4D, 0x5A });
            File.WriteAllText(txt, "x");

            Assert.Equal(StockSuiteExeStatus.NotConfigured, StockSuiteExporter.ValidateExecutablePath("  "));
            Assert.Equal(StockSuiteExeStatus.NotFound, StockSuiteExporter.ValidateExecutablePath(Path.Combine(dir, "Missing.exe")));
            Assert.Equal(StockSuiteExeStatus.NotExecutable, StockSuiteExporter.ValidateExecutablePath(txt));
            Assert.Equal(StockSuiteExeStatus.Ok, StockSuiteExporter.ValidateExecutablePath("\"" + exe + "\""));

            // 미설정 → 실행 불가
            Assert.False(new StockSuiteExporter(() => dir).CanLaunch);
            Assert.False(new StockSuiteExporter(() => dir).Launch().Ok);

            var running = 0;
            ProcessStartInfo? started = null;
            string? terminated = null;
            var ss = new StockSuiteExporter(() => dir, executableProvider: () => exe)
            {
                RunningProbe = _ => running,
                Starter = psi => { started = psi; return true; },
                Terminator = n => { terminated = n; running = 0; return true; },
            };
            Assert.True(ss.CanLaunch);

            // 최초 실행
            var r1 = ss.Launch();
            Assert.True(r1.Ok);
            Assert.Equal(Path.GetFullPath(exe), started!.FileName);
            Assert.Equal(dir.TrimEnd(Path.DirectorySeparatorChar), started.WorkingDirectory.TrimEnd(Path.DirectorySeparatorChar));

            // 이미 실행 중 → 경고(재시작 대기) · 시작 안 함
            running = 1; started = null;
            var r2 = ss.Launch();
            Assert.False(r2.Ok);
            Assert.True(r2.AlreadyRunning);
            Assert.Null(started);

            // 재실행 → 종료 후 시작
            var r3 = ss.Launch(restartIfRunning: true);
            Assert.True(r3.Ok);
            Assert.Equal("StockSuite", terminated);
            Assert.NotNull(started);

            // 시작기 예외도 격리
            ss.Starter = _ => throw new System.ComponentModel.Win32Exception("denied");
            Assert.False(ss.Launch().Ok);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
