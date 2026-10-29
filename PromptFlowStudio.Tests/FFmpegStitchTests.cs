using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Commercial;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>멀티 컷 최종 합본(concat · 가상 병합) · 커머셜 로고/디스클레이머 번인 후처리 회귀 잠금 — ffmpeg 는 실행기 주입으로 결정론 구동.</summary>
[Trait("Category", "FastUnit")]
public class FFmpegStitchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_stitch_" + Guid.NewGuid().ToString("N"));

    public FFmpegStitchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Part(string name, string body)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, body);
        return p;
    }

    private static BatchRenderJob Done(string group, int cut, string? output) => new()
    {
        Prompt = "cut" + cut,
        CutIndex = cut,
        OutputPath = output,
        Status = BatchJobStatus.Completed,
        Metadata = FFmpegVideoStitchService.WithGroup(null, group),
    };

    [Fact]
    public async Task FFmpegStitch_ConcatenatesMultipleCuts_IntoSingleOutput()
    {
        const string group = "g1";
        var jobs = new List<BatchRenderJob>
        {
            Done(group, 3, Part("c3.mp4", "C")),
            Done(group, 1, Part("c1.mp4", "A")),
            Done(group, 2, Part("c2 it's.mp4", "B")),
            Done("other", 1, Part("x.mp4", "X")),   // 다른 그룹은 제외
        };

        Assert.True(FFmpegVideoStitchService.TryCollectCompletedGroup(jobs, group, out var parts));
        Assert.Equal(3, parts.Length);
        Assert.EndsWith("c1.mp4", parts[0]);
        Assert.EndsWith("c2 it's.mp4", parts[1]);
        Assert.EndsWith("c3.mp4", parts[2]);

        string? listSeen = null;
        IReadOnlyList<string>? argsSeen = null;
        var svc = new FFmpegVideoStitchService(() => "ffmpeg.exe", (exe, args, ct) =>
        {
            argsSeen = args;
            var list = args[Array.IndexOf(args.ToArray(), "-i") + 1];
            listSeen = File.ReadAllText(list);
            var sb = new System.Text.StringBuilder();
            foreach (var line in listSeen.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                sb.Append(File.ReadAllText(line[6..^1].Replace("'\\''", "'")));
            File.WriteAllText(args[^1], sb.ToString());
            return Task.FromResult(true);
        });

        var output = FFmpegVideoStitchService.BuildFullOutputPath(_dir, "My:Project");
        Assert.EndsWith("My_Project_full.mp4", output);
        var r = await svc.StitchAsync(parts, output);

        Assert.True(r.Ok, r.Message);
        Assert.False(r.IsVirtual);
        Assert.Equal(3, r.PartCount);
        Assert.Equal(Path.GetFullPath(output), r.OutputPath);
        Assert.Equal("ABC", File.ReadAllText(output));   // 컷 순서대로 단일 출력
        Assert.NotNull(argsSeen);
        Assert.Contains("concat", argsSeen!);
        Assert.Equal("copy", argsSeen![argsSeen.ToList().IndexOf("-c") + 1]);   // 무손실 스트림 복사
        Assert.Contains("'\\''", listSeen);                                     // 작은따옴표 이스케이프
        Assert.False(File.Exists(output + ".concat.txt"));                      // 임시 목록 정리
    }

    [Fact]
    public async Task FFmpegStitch_FallsBackToVirtualManifest_WhenFfmpegMissing()
    {
        var parts = new[] { Part("a.mp4", "A"), Part("b.mp4", "B") };
        var called = false;
        var svc = new FFmpegVideoStitchService(() => null, (_, _, _) => { called = true; return Task.FromResult(true); });

        var r = await svc.StitchAsync(parts, Path.Combine(_dir, "p_full.mp4"));

        Assert.True(r.Ok, r.Message);
        Assert.True(r.IsVirtual);
        Assert.False(called);
        Assert.EndsWith(FFmpegVideoStitchService.VirtualManifestExt, r.OutputPath);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(r.OutputPath!));
        var listed = doc.RootElement.GetProperty("parts");
        Assert.Equal(2, listed.GetArrayLength());
        Assert.Equal(parts[0], listed[0].GetString());
    }

    [Fact]
    public void FFmpegStitch_GroupNotReady_WhenAnyCutIncompleteOrMissing()
    {
        const string group = "g2";
        var ok = Done(group, 1, Part("a.mp4", "A"));
        var running = Done(group, 2, Part("b.mp4", "B"));
        running.Status = BatchJobStatus.Processing;
        Assert.False(FFmpegVideoStitchService.TryCollectCompletedGroup(new[] { ok, running }, group, out _));
        Assert.Equal((2, 1), FFmpegVideoStitchService.GroupProgress(new[] { ok, running }, group));

        var missing = Done(group, 2, Path.Combine(_dir, "nope.mp4"));
        Assert.False(FFmpegVideoStitchService.TryCollectCompletedGroup(new[] { ok, missing }, group, out _));
        Assert.False(FFmpegVideoStitchService.TryCollectCompletedGroup(new[] { ok }, null, out _));
    }

    [Fact]
    public async Task Commercial_AppliesLogoOverlayPostProcessing_WhenMetadataPresent()
    {
        var raw = Part("gen.mp4", "RAW");
        var logo = Part("logo.png", "PNG");
        IReadOnlyList<string>? argsSeen = null;
        var post = new FFmpegVideoStitchService(() => "ffmpeg.exe", (exe, args, ct) =>
        {
            argsSeen = args;
            File.WriteAllText(args[^1], "BRANDED");
            return Task.FromResult(true);
        });
        var engine = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(new FakeVideoEngine(raw)), post, (_, _) => Task.CompletedTask);

        var pkg = new CommercialBrandService().SynthesizeCommercialPrompt("무선 이어폰",
            new BrandKitProfile("Acme", logo, WatermarkPlacement.BottomRight, "#112233", "광고 포함"),
            CommercialBrandService.Preset(CommercialAdFormat.Bumper6s));
        var r = await engine.ExecuteAsync(pkg.Job, new Progress<int>(), CancellationToken.None);

        Assert.True(r.Ok, r.Message);
        Assert.Equal(raw, r.OutputPath);                       // 합성본이 최종 경로로 확정
        Assert.Equal("BRANDED", File.ReadAllText(raw));
        Assert.Equal("burned", pkg.Job.Metadata![CommercialBrandService.MetaOverlayApplied]);
        Assert.NotNull(argsSeen);
        Assert.Contains(logo, argsSeen!);
        var filter = argsSeen![argsSeen.ToList().IndexOf("-filter_complex") + 1];
        Assert.Contains("overlay=W-w-10:H-h-10", filter);
        Assert.Contains("drawtext=textfile=", filter);
    }

    [Fact]
    public async Task Commercial_SkipsOverlay_WhenMetadataAbsent_OrFfmpegMissing()
    {
        var raw = Part("plain.mp4", "RAW");
        var called = false;
        var post = new FFmpegVideoStitchService(() => "ffmpeg.exe", (_, _, _) => { called = true; return Task.FromResult(true); });
        var engine = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(new FakeVideoEngine(raw)), post, (_, _) => Task.CompletedTask);

        var plain = new BatchRenderJob { Prompt = "p" };
        var r = await engine.ExecuteAsync(plain, new Progress<int>(), CancellationToken.None);
        Assert.True(r.Ok);
        Assert.False(called);
        Assert.Null(plain.Metadata);

        var offline = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(new FakeVideoEngine(raw)), new FFmpegVideoStitchService(() => null, null), (_, _) => Task.CompletedTask);
        var job = new BatchRenderJob
        {
            Prompt = "p",
            Metadata = new Dictionary<string, string> { [CommercialBrandService.MetaKind] = "commercial", [CommercialBrandService.MetaDisclaimerAlias] = "고지" },
        };
        var r2 = await offline.ExecuteAsync(job, new Progress<int>(), CancellationToken.None);
        Assert.True(r2.Ok);
        Assert.Equal("RAW", File.ReadAllText(raw));            // 원본 유지
        Assert.Equal("virtual", job.Metadata![CommercialBrandService.MetaOverlayApplied]);
    }

    [Fact]
    public async Task FFmpegStitch_WithMixedResolutions_GeneratesValidTranscodeFilterCommand()
    {
        var parts = new[] { Part("hd.mp4", "A"), Part("oom720.mp4", "B") };
        var calls = new List<string[]>();
        var probes = new Dictionary<string, StitchProbe>(StringComparer.OrdinalIgnoreCase)
        {
            [parts[0]] = new(1920, 1080, HasAudio: false),
            [parts[1]] = new(1280, 720, HasAudio: false),   // OOM 하향 컷
        };
        var svc = new FFmpegVideoStitchService(() => "ffmpeg.exe", (exe, args, ct) =>
        {
            calls.Add(args.ToArray());
            File.WriteAllText(args[^1], "T");
            return Task.FromResult(true);
        }, (exe, path, ct) => Task.FromResult<StitchProbe?>(probes[path]));

        var output = Path.Combine(_dir, "mixed_full.mp4");
        var r = await svc.StitchAsync(parts, output);

        Assert.True(r.Ok, r.Message);
        Assert.Contains("1920x1080", r.Message);
        var args = Assert.Single(calls);   // 해상도 불일치 사전 감지 → -c copy 생략
        Assert.DoesNotContain("copy", args);
        var filter = args[Array.IndexOf(args, "-filter_complex") + 1];
        Assert.Contains("[0:v]scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2", filter);
        Assert.Contains("[1:v]scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2", filter);
        Assert.EndsWith("[v0][v1]concat=n=2:v=1:a=0[vout]", filter);
        Assert.Equal("libx264", args[Array.IndexOf(args, "-c:v") + 1]);
        Assert.Equal("aac", args[Array.IndexOf(args, "-c:a") + 1]);
        Assert.Contains("anullsrc=r=44100:cl=stereo", args);   // 무음 컷 → aac 무음 트랙
        Assert.Equal("2:a", args[Array.LastIndexOf(args, "-map") + 1]);

        // 오디오 보유 컷 → [a] 체인 concat · 프로브 불명 → copy 실패 시 1080p 폴백
        var withAudio = FFmpegVideoStitchService.BuildTranscodeConcatArgs(parts, 1281, 721, includeAudio: true, output);
        var f2 = withAudio[Array.IndexOf(withAudio, "-filter_complex") + 1];
        Assert.Contains("scale=1280:720:", f2);   // 짝수 정렬
        Assert.EndsWith("[v0][a0][v1][a1]concat=n=2:v=1:a=1[vout][aout]", f2);
        Assert.DoesNotContain("anullsrc=r=44100:cl=stereo", withAudio);

        var seq = new List<bool>();
        var fallback = new FFmpegVideoStitchService(() => "ffmpeg.exe", (exe, a, ct) =>
        {
            var copy = a.Contains("copy");
            seq.Add(copy);
            if (copy) return Task.FromResult(false);   // 스트림 복사 실패
            File.WriteAllText(a[^1], "T");
            return Task.FromResult(true);
        });
        var r2 = await fallback.StitchAsync(parts, Path.Combine(_dir, "fb_full.mp4"));
        Assert.True(r2.Ok, r2.Message);
        Assert.Equal(new[] { true, false }, seq);
        Assert.Contains("1920x1080", r2.Message);

        Assert.Equal(new StitchProbe(1280, 720, true), FFmpegVideoStitchService.ParseProbeOutput("video,1280,720\r\naudio,,\r\n"));
        Assert.Null(FFmpegVideoStitchService.ParseProbeOutput("audio\n"));
    }
}

/// <summary>테스트용 비디오 엔진 — 고정 산출물 반환 또는 취소까지 무한 대기(취소 관측 기록).</summary>
internal sealed class FakeVideoEngine(string? output, bool hang = false) : IVideoGenerationEngine
{
    public string Key => "Fake";
    public string DisplayName => "Fake";
    public bool Enabled => true;
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ObservedCancellation { get; private set; }

    public static VideoEngineSelector Selector(IVideoGenerationEngine engine)
        => new(new[] { engine }, new EngineConfigService(new ConfigurationBuilder().Build()));

    public async Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
    {
        Started.TrySetResult();
        if (hang)
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { ObservedCancellation = true; throw; }
        }
        return new WanGenerationResult(true, WanTaskStatus.Succeeded, output, "ok", null, TimeSpan.Zero);
    }

    public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
        => Task.FromResult(new WanUploadResult(false, null, "n/a"));
}
