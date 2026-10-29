using PromptFlowStudio.Services.Audio;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>대본 TTS(로컬 엔진 → Mock 무음 WAV) · 합본 AAC 오디오 믹싱(<c>-c:v copy -c:a aac -shortest</c>) 회귀 잠금 — SAPI 생략 · ffmpeg 실행기 주입.</summary>
[Trait("Category", "FastUnit")]
public class AudioTtsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ttstest_" + Guid.NewGuid().ToString("N"));

    public AudioTtsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void AssertRiffWav(string path)
    {
        Assert.True(File.Exists(path));
        var b = File.ReadAllBytes(path);
        Assert.True(b.Length > 44);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(b, 8, 4));
        Assert.Equal(b.Length - 8, BitConverter.ToInt32(b, 4));
    }

    [Fact]
    public async Task TtsService_GeneratesAudioFile_FromScriptText()
    {
        var tts = new LocalTtsSynthesisService(null, useSapi: false);
        var path = Path.Combine(_dir, "sub", "cut1.wav");

        Assert.True(await tts.SynthesizeToFileAsync("안녕하세요, 오늘의 이야기를 시작합니다.", path, CancellationToken.None));
        AssertRiffWav(path);
        Assert.Equal(LocalTtsSynthesisService.BackendMock, tts.LastBackend);
        Assert.False(File.Exists(path + ".tmp"));

        // 길이 = 대본 추정 시간(44.1kHz · 16bit 모노)
        var dataBytes = new FileInfo(path).Length - 44;
        var seconds = dataBytes / 2.0 / 44100;
        Assert.InRange(seconds, LocalTtsSynthesisService.EstimateSeconds("안녕하세요, 오늘의 이야기를 시작합니다.") - 0.01, 90);

        // 빈 대본 · 빈 경로 · 취소 → false(예외 0)
        Assert.False(await tts.SynthesizeToFileAsync("   ", Path.Combine(_dir, "e.wav"), CancellationToken.None));
        Assert.False(await tts.SynthesizeToFileAsync("text", "", CancellationToken.None));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(await tts.SynthesizeToFileAsync("text", Path.Combine(_dir, "c.wav"), cts.Token));
    }

    [Fact]
    public async Task TtsService_PrefersLocalEngine_WhenAvailable()
    {
        var local = new FakeLocalTts(_dir);
        var tts = new LocalTtsSynthesisService(local, useSapi: false);
        var path = Path.Combine(_dir, "local.wav");

        Assert.True(await tts.SynthesizeToFileAsync("로컬 엔진", path, CancellationToken.None));
        AssertRiffWav(path);
        Assert.Equal("Fake-TTS", tts.LastBackend);
        Assert.Equal(1, local.Calls);

        // 로컬 엔진 실패 → Mock 폴백
        local.Fail = true;
        Assert.True(await tts.SynthesizeToFileAsync("폴백", Path.Combine(_dir, "fb.wav"), CancellationToken.None));
        Assert.Equal(LocalTtsSynthesisService.BackendMock, tts.LastBackend);
    }

    [Fact]
    public async Task StitchCuts_MuxesNarrationTrack_WithAacCopyShortest()
    {
        var cuts = new[]
        {
            new StitchCut(Part("a.mp4"), 3.0, "첫 번째 컷 대본"),
            new StitchCut(Part("b.mp4"), 4.5, null),
        };
        var calls = new List<string[]>();
        var svc = new FFmpegVideoStitchService(() => "ffmpeg.exe", (exe, args, ct) =>
        {
            calls.Add(args.ToArray());
            File.WriteAllText(args[^1], "VIDEO+" + calls.Count);   // 마지막 인자 = 출력
            return Task.FromResult(true);
        });
        var output = FFmpegVideoStitchService.BuildFullOutputPath(_dir, "proj");

        var r = await svc.StitchCutsAsync(cuts, output, new LocalTtsSynthesisService(null, useSapi: false), new SubtitleGeneratorService());

        Assert.True(r.Ok);
        Assert.False(r.IsVirtual);
        Assert.True(r.AudioMuxed);
        Assert.Equal(2, calls.Count);                       // concat → audio mux
        var mux = calls[1];
        Assert.Contains("-filter_complex", mux);
        var filter = mux[Array.IndexOf(mux, "-filter_complex") + 1];
        Assert.Contains("atrim=0:3,", filter);
        Assert.Contains("atrim=0:4.5,", filter);
        Assert.Contains("concat=n=2:v=0:a=1[aout]", filter);
        var joined = string.Join(' ', mux);
        Assert.Contains("-map 0:v:0 -map [aout] -c:v copy -c:a aac", joined);
        Assert.Contains("-shortest", mux);
        Assert.Equal("VIDEO+2", File.ReadAllText(output));  // 믹싱본이 합본 자리 교체
        Assert.False(File.Exists(output + ".mux.mp4"));
        Assert.NotNull(r.SubtitlePath);
        Assert.True(File.Exists(Path.ChangeExtension(output, ".srt")));
    }

    [Fact]
    public async Task StitchCuts_KeepsSilentVideo_WhenMuxFailsOrNoScript()
    {
        var cuts = new[] { new StitchCut(Part("a.mp4"), 3.0, "대본"), new StitchCut(Part("b.mp4"), 3.0, "대본2") };
        var n = 0;
        var svc = new FFmpegVideoStitchService(() => "ffmpeg.exe", (exe, args, ct) =>
        {
            n++;
            if (args.Contains("-filter_complex")) return Task.FromResult(false);   // 믹싱 실패
            File.WriteAllText(args[^1], "CONCAT");
            return Task.FromResult(true);
        });
        var output = Path.Combine(_dir, "silent_full.mp4");
        var r = await svc.StitchCutsAsync(cuts, output, new LocalTtsSynthesisService(null, useSapi: false), null);
        Assert.True(r.Ok);
        Assert.False(r.AudioMuxed);
        Assert.Equal("CONCAT", File.ReadAllText(output));
        Assert.Equal(2, n);

        // 대본 전무 → 믹싱 시도 0
        n = 0;
        var noScript = new[] { new StitchCut(Part("c.mp4"), 3.0, null) };
        var r2 = await svc.StitchCutsAsync(noScript, Path.Combine(_dir, "n_full.mp4"), new LocalTtsSynthesisService(null, useSapi: false), null);
        Assert.True(r2.Ok);
        Assert.False(r2.AudioMuxed);
        Assert.Equal(1, n);
    }

    [Fact]
    public void TryCollectCompletedCuts_CarriesScriptAndDuration()
    {
        var job = new BatchRenderJob
        {
            CutIndex = 1, DurationSec = 4f, OutputPath = Part("x.mp4"), Status = BatchJobStatus.Completed,
            Metadata = FFmpegVideoStitchService.WithGroup(null, "g", "  대본 텍스트 "),
        };
        Assert.True(FFmpegVideoStitchService.TryCollectCompletedCuts(new[] { job }, "g", out var cuts));
        Assert.Single(cuts);
        Assert.Equal("대본 텍스트", cuts[0].Script);
        Assert.Equal(4.0, cuts[0].DurationSec);
        Assert.False(FFmpegVideoStitchService.WithGroup(null, "g", " ").ContainsKey(FFmpegVideoStitchService.MetaScript));
    }

    private string Part(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, name);
        return p;
    }

    private sealed class FakeLocalTts(string dir) : ILocalTtsService
    {
        public bool Fail { get; set; }
        public int Calls { get; private set; }
        public bool IsAvailable => true;
        public string EngineName => "Fake-TTS";

        public Task<string?> SynthesizeAsync(string text, string? outPath = null, CancellationToken ct = default)
        {
            Calls++;
            if (Fail) return Task.FromResult<string?>(null);
            var p = Path.Combine(dir, "fake_src_" + Calls + ".wav");
            File.WriteAllBytes(p, AudioWaveSynthesizer.BuildRiffWav(new short[4410], 44100));
            return Task.FromResult<string?>(p);
        }
    }
}
