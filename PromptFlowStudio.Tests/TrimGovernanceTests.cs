using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Video;
using PromptFlowStudio.Services.Vfx;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>트리머 파일 락 해제 핫스왑 · 트림 메타데이터 동기화 · Vfx/Media/Comfy IProcessRunner 수렴 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class TrimGovernanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_trimgov_" + Guid.NewGuid().ToString("N"));

    public TrimGovernanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>호출 순서 기록 + 마지막 인자(출력 경로)에 더미 산출물 기록 · 지정 stderr 반환.</summary>
    private sealed class OrderedRunner(List<string> log, string stdErr = "") : IProcessRunner
    {
        public readonly List<(string Exe, string[] Args)> Calls = new();

        public Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string? workingDir = null,
            Action<bool, string>? onLine = null, CancellationToken ct = default)
        {
            var a = new List<string>(args).ToArray();
            lock (Calls) { Calls.Add((exe, a)); log.Add("run"); }
            if (a.Length > 0 && Path.IsPathRooted(a[^1])) File.WriteAllBytes(a[^1], new byte[4096]);
            if (onLine is not null && stdErr.Length > 0) onLine(true, stdErr);
            return Task.FromResult(new ProcessResult(true, 0, "", stdErr));
        }
    }

    private string Src(string name, string? metaJson = null)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[2048]);
        if (metaJson is not null) File.WriteAllText(GalleryMediaService.MetaPathFor(p), metaJson);
        return p;
    }

    private static JsonObject Meta(string mediaPath) => (JsonObject)JsonNode.Parse(File.ReadAllText(GalleryMediaService.MetaPathFor(mediaPath)))!;

    [Fact]
    public async Task VideoTrimService_UpdatesMetadataDuration_UponSuccessfulTrim()
    {
        var src = Src("clip.mp4", """{ "title": "원본", "prompt": "p", "durationSeconds": 15, "isFavorite": true }""");
        var svc = new VideoTrimService(() => "ffmpeg", new OrderedRunner(new List<string>()));

        // ① 덮어쓰기 — 재생시간 14.0 · 이력 1건 · 기존 필드 보존
        var r = await svc.TrimInPlaceAsync(src, 0.5, 14.5);
        Assert.True(r.Ok);
        Assert.Equal(14.0, r.DurationSeconds);
        var m = Meta(src);
        Assert.Equal(14.0, m["durationSeconds"]!.GetValue<double>());
        Assert.Equal("원본", m["title"]!.GetValue<string>());
        Assert.True(m["isFavorite"]!.GetValue<bool>());
        var hist = (JsonArray)m["trimHistory"]!;
        Assert.Single(hist);
        Assert.Equal(0.5, hist[0]!["start"]!.GetValue<double>());
        Assert.Equal(14.5, hist[0]!["end"]!.GetValue<double>());
        Assert.True(DateTime.TryParse(hist[0]!["trimmedAt"]!.GetValue<string>(), out _));
        Assert.Empty(Directory.GetFiles(_dir, "*.~tmp"));

        // ② 새 클립 — 원본 길이(14)로 end 클램프 → 13.0 · 이력 계승 2건 · 즐겨찾기 미계승
        var clip = Path.Combine(_dir, "clip_cut.mp4");
        var c = await svc.TrimAsync(src, clip, 1, 30);
        Assert.True(c.Ok);
        Assert.Equal(13.0, c.DurationSeconds);
        var cm = Meta(clip);
        Assert.Equal(13.0, cm["durationSeconds"]!.GetValue<double>());
        Assert.Equal(2, ((JsonArray)cm["trimHistory"]!).Count);
        Assert.Null(cm["isFavorite"]);
        Assert.Equal("clip_cut", cm["title"]!.GetValue<string>());

        // ③ 갤러리 — 스캔 시 durationSeconds 반영 · 통지 시 즉시 갱신 + 이벤트
        var gallery = new GalleryMediaService(() => null);
        gallery.ScanGalleryItems(_dir);
        Assert.Equal(14.0, gallery.FindByPath(src)!.DurationSeconds);
        string? notified = null;
        gallery.MediaMetadataChanged += p => notified = p;
        gallery.MediaMetadataChanged += _ => throw new InvalidOperationException("격리 확인");
        Assert.True(gallery.NotifyMediaMetadataChanged(src, 9.5));
        Assert.Equal(9.5, gallery.FindByPath(src)!.DurationSeconds);
        Assert.Equal(src, notified);
        Assert.False(gallery.NotifyMediaMetadataChanged(Path.Combine(_dir, "none.mp4"), 1));

        Assert.Equal("9.5s", GalleryMediaService.FormatDurationBadge(9.5));
        Assert.Equal("1:05.3", GalleryMediaService.FormatDurationBadge(65.3));
        Assert.Null(GalleryMediaService.FormatDurationBadge(null));
        Assert.Equal(13.0, VideoTrimService.ComputeTrimmedDuration(1, 30, 14));
        Assert.Equal(29.0, VideoTrimService.ComputeTrimmedDuration(1, 30, null));
    }

    [Fact]
    public async Task VideoTrimService_OverwritesPlayingFile_WithLockReleasePattern()
    {
        var log = new List<string>();
        var svc = new VideoTrimService(() => "ffmpeg", new OrderedRunner(log));

        // ① 재생 중(공유 읽기 핸들 보유) → Release 훅이 핸들 해제 → 교체 성공 → Reload(true)
        var src = Src("playing.mp4");
        var player = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hooks = new VideoHandleHooks(
            _ => { log.Add("release"); player.Dispose(); return Task.CompletedTask; },
            (ok, _) => { log.Add("reload:" + ok); return Task.CompletedTask; });
        var r = await svc.TrimInPlaceAsync(src, 0.5, 4.5, hooks);
        Assert.True(r.Ok, r.Message);
        Assert.Equal(new[] { "run", "release", "reload:True" }, log);   // FFmpeg 컷은 재생 유지 중 · 교체 직전 해제
        Assert.Equal(4096, new FileInfo(src).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.trim-*"));

        // ② 핸들 미해제 → 재시도 후 실패 · 원본 무손상 · 임시 정리 · Reload(false) 로 원본 재마운트
        log.Clear();
        var locked = Src("locked.mp4");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var stuck = new VideoHandleHooks(_ => { log.Add("release"); return Task.CompletedTask; }, (ok, _) => { log.Add("reload:" + ok); return Task.CompletedTask; });
            var f = await svc.TrimInPlaceAsync(locked, 0.5, 4.5, stuck);
            Assert.False(f.Ok);
            Assert.Equal(new[] { "run", "release", "reload:False" }, log);
        }
        Assert.Equal(2048, new FileInfo(locked).Length);
        Assert.Empty(Directory.GetFiles(_dir, "*.trim-*"));
        Assert.Null(VideoTrimService.ReadMetaDuration(locked));   // 실패 시 메타 미기록

        // ③ 훅 예외 격리 — Release 가 던져도 교체 진행
        log.Clear();
        var boom = new VideoHandleHooks(_ => throw new InvalidOperationException("JS 부재"), (_, _) => throw new InvalidOperationException("JS 부재"));
        Assert.True((await svc.TrimInPlaceAsync(Src("boom.mp4"), 0.5, 4.5, boom)).Ok);
    }

    [Fact]
    public async Task AllRemainingServices_UtilizeIProcessRunner_WithoutDirectProcessInstantiation()
    {
        var log = new List<string>();
        const string banner = "  Stream #0:0: Video: h264, yuv420p, 1280x720, 24 fps, 24 tbr\n  Stream #0:1: Audio: aac";
        var runner = new OrderedRunner(log, banner);
        var src = Src("v.mp4");

        // Vfx — fps 프로브 · 일반 CLI 실행이 주입 runner 경유
        Assert.Equal(24, await AiNeuralVfxRelightingEngine.ProbeFpsAsync(runner, "ffmpeg", src, CancellationToken.None));
        Assert.Equal(0, await VfxProcess.RunAsync(runner, "rife", new List<string> { "-i", src }, CancellationToken.None));
        Assert.Equal("rife", runner.Calls[^1].Exe);

        // Media — QA 인스펙터 스캔이 runner 경유(stderr 라인 → 파서)
        var before = runner.Calls.Count;
        var qa = await new AutomatedVideoQaInspector(() => "ffmpeg", runner).InspectAsync(src);
        Assert.NotNull(qa);
        Assert.Equal(before + 1, runner.Calls.Count);
        Assert.Contains(src, runner.Calls[^1].Args);

        // 소스 가드 — 대상 계열(Vfx · Media · Video · Comfy)에 직접 Process 생성 0
        var root = RepoRoot();
        var targets = new List<string>();
        foreach (var sub in new[] { "Vfx", "Media", "Video" })
            targets.AddRange(Directory.GetFiles(Path.Combine(root, "PromptFlowStudio", "Services", sub), "*.cs", SearchOption.AllDirectories));
        foreach (var f in Directory.GetFiles(Path.Combine(root, "PromptFlowStudio", "Services"), "*Comfy*.cs", SearchOption.AllDirectories)) targets.Add(f);
        Assert.True(targets.Count >= 10);
        var direct = new System.Text.RegularExpressions.Regex(@"new\s+(System\.Diagnostics\.)?Process\s*[({]|\bProcess\.Start\s*\(");
        foreach (var f in targets)
            Assert.False(direct.IsMatch(File.ReadAllText(f)), "직접 Process 생성: " + f);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build_titan_release.ps1"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("저장소 루트를 찾지 못함");
    }
}
