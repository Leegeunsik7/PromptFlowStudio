using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>FFmpeg 원클릭 자동 설치 — ZIP 추출 경로 · Zip-Slip/헤더 검증 · 체크섬 · 미러 페일오버 + 작업실 미니 큐 진행률 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class FfmpegDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ffdl_t_" + Guid.NewGuid().ToString("N"));

    public FfmpegDownloaderTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static byte[] Pe(string tag) => System.Text.Encoding.ASCII.GetBytes("MZ" + tag);

    private string MakeZip(string name, params (string Entry, byte[] Data)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in files)
        {
            using var s = zip.CreateEntry(entry).Open();
            s.Write(data);
        }
        return path;
    }

    [Fact]
    public void FfmpegDownloader_ExtractsBinaries_ToToolsDirectory()
    {
        var zip = MakeZip("ff.zip",
            ("ffmpeg-7.1-essentials_build/doc/ffmpeg.exe", Pe("doc")),            // /bin/ 외 동명 — 우선순위 낮음
            ("ffmpeg-7.1-essentials_build/bin/ffmpeg.exe", Pe("bin-ffmpeg")),
            ("ffmpeg-7.1-essentials_build/bin/ffprobe.exe", Pe("bin-ffprobe")),
            ("ffmpeg-7.1-essentials_build/README.txt", "readme"u8.ToArray()));
        var baseDir = Path.Combine(_dir, "app");

        var exe = FfmpegAutoDownloaderService.ExtractBinaries(zip, baseDir);

        var bin = Path.Combine(baseDir, "tools", "ffmpeg", "bin");
        Assert.Equal(Path.GetFullPath(Path.Combine(bin, "ffmpeg.exe")), exe);
        Assert.Equal("MZbin-ffmpeg", File.ReadAllText(exe));
        Assert.Equal("MZbin-ffprobe", File.ReadAllText(Path.Combine(bin, "ffprobe.exe")));
        Assert.False(File.Exists(Path.Combine(bin, "README.txt")));
        Assert.Empty(Directory.GetFiles(bin, "*.part"));
        if (OperatingSystem.IsWindows())
        {
            var loc = FfmpegLocator.Locate(baseDir, () => null);
            Assert.True(loc.Found);
            Assert.Equal(exe, loc.Path);
        }
    }

    [Fact]
    public void FfmpegDownloader_ZipSlipEntry_ExtractedByNameOnly_AndBadHeaderRejected()
    {
        var baseDir = Path.Combine(_dir, "app2");
        var zip = MakeZip("slip.zip", ("../../evil/bin/ffmpeg.exe", Pe("x")));
        var exe = FfmpegAutoDownloaderService.ExtractBinaries(zip, baseDir);
        Assert.StartsWith(Path.GetFullPath(FfmpegAutoDownloaderService.TargetBinOf(baseDir)), exe);
        Assert.False(Directory.Exists(Path.Combine(_dir, "evil")));

        var bad = MakeZip("bad.zip", ("bin/ffmpeg.exe", "not-pe"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => FfmpegAutoDownloaderService.ExtractBinaries(bad, Path.Combine(_dir, "app3")));
        var none = MakeZip("none.zip", ("bin/readme.txt", "x"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => FfmpegAutoDownloaderService.ExtractBinaries(none, Path.Combine(_dir, "app4")));
    }

    [Fact]
    public void FfmpegDownloader_ParsesChecksumFormats()
    {
        var h1 = new string('a', 64);
        var h2 = new string('B', 64);
        Assert.Equal(h1, FfmpegAutoDownloaderService.ParseSha256(h1 + "\n", "ffmpeg-release-essentials.zip"));
        Assert.Equal(h2.ToLowerInvariant(), FfmpegAutoDownloaderService.ParseSha256(
            h1 + "  ffmpeg-master-latest-linux64-gpl.tar.xz\n" + h2 + "  ffmpeg-master-latest-win64-gpl.zip\n", "ffmpeg-master-latest-win64-gpl.zip"));
        Assert.Null(FfmpegAutoDownloaderService.ParseSha256(h1 + "  other.zip\n", "ffmpeg-master-latest-win64-gpl.zip"));
        Assert.Null(FfmpegAutoDownloaderService.ParseSha256("garbage", "x.zip"));
    }

    private sealed class MapHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new(StringComparer.Ordinal);
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var key = request.RequestUri!.ToString();
            return Task.FromResult(Routes.TryGetValue(key, out var f) ? f() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task FfmpegDownloader_InstallAsync_FailsOverMirror_VerifiesChecksum_ReportsProgress()
    {
        var zipBytes = File.ReadAllBytes(MakeZip("ok.zip", ("x/bin/ffmpeg.exe", Pe("ok")), ("x/bin/ffprobe.exe", Pe("probe"))));
        var sha = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
        var h = new MapHandler();
        h.Routes["https://m2.test/ff.zip"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
        h.Routes["https://m2.test/ff.zip.sha256"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha) };
        var baseDir = Path.Combine(_dir, "inst");
        var svc = new FfmpegAutoDownloaderService(new HttpClient(h),
            new[] { new FfmpegMirror("dead", "https://m1.test/ff.zip", null), new FfmpegMirror("ok", "https://m2.test/ff.zip", "https://m2.test/ff.zip.sha256") },
            baseDir, static _ => TimeSpan.Zero);
        var reports = new List<float>();
        var progress = new SyncProgress(reports);

        var r = await svc.InstallAsync(progress, CancellationToken.None, requireSupportedPlatform: false);

        Assert.True(r.Ok, r.Message);
        Assert.True(r.ChecksumVerified);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "tools", "ffmpeg", "bin", "ffmpeg.exe")), r.FfmpegPath);
        Assert.True(File.Exists(Path.Combine(baseDir, "tools", "ffmpeg", "bin", "ffprobe.exe")));
        Assert.Equal(1f, reports[^1]);
        Assert.Contains(0.9f, reports);

        // 체크섬 불일치 → 실패 · 미설치
        var h2 = new MapHandler();
        h2.Routes["https://m2.test/ff.zip"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) };
        h2.Routes["https://m2.test/ff.zip.sha256"] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('0', 64)) };
        var bad = await new FfmpegAutoDownloaderService(new HttpClient(h2), new[] { new FfmpegMirror("ok", "https://m2.test/ff.zip", "https://m2.test/ff.zip.sha256") },
            Path.Combine(_dir, "inst2"), static _ => TimeSpan.Zero).InstallAsync(null, CancellationToken.None, requireSupportedPlatform: false);
        Assert.False(bad.Ok);
        Assert.Contains("체크섬", bad.Message);
        Assert.False(File.Exists(Path.Combine(_dir, "inst2", "tools", "ffmpeg", "bin", "ffmpeg.exe")));
    }

    private sealed class SyncProgress(List<float> sink) : IProgress<float>
    {
        public void Report(float value) { lock (sink) sink.Add(value); }
    }

    [Fact]
    public void MiniQueueProgress_TracksCuts_StitchAndFinalPath()
    {
        var a = new BatchRenderJob { Status = BatchJobStatus.Completed };
        var b = new BatchRenderJob { Status = BatchJobStatus.Processing, ProgressPercent = 50 };
        var c = new BatchRenderJob { Status = BatchJobStatus.Queued };
        var ids = new[] { a.JobId, b.JobId, c.JobId };
        var snap = new[] { a, b, c };

        var p = MiniQueueProgress.Compute(snap, ids);
        Assert.Equal(MiniQueuePhase.Rendering, p.Phase);
        Assert.Equal(1, p.CompletedCuts);
        Assert.Equal(50, p.Percent);

        b.Status = BatchJobStatus.Completed;
        p = MiniQueueProgress.Compute(snap, ids);
        Assert.Equal("🎬 멀티 컷 렌더링 중... 2/3 컷 생성 완료 (66%)", p.Label);

        c.Status = BatchJobStatus.Completed;
        p = MiniQueueProgress.Compute(snap, ids);
        Assert.Equal(MiniQueuePhase.Stitching, p.Phase);
        Assert.Equal("🎞️ 최종 비디오 합본 중...", p.Label);

        c.Metadata = MultiDurationPlanner.WithMeta(null, MultiDurationPlanner.MetaStitched, @"C:\out\ws_15s.mp4");
        p = MiniQueueProgress.Compute(snap, ids);
        Assert.Equal(MiniQueuePhase.Completed, p.Phase);
        Assert.Equal(@"C:\out\ws_15s.mp4", p.FinalPath);

        c.Status = BatchJobStatus.Failed;
        Assert.Equal(MiniQueuePhase.Failed, MiniQueueProgress.Compute(snap, ids).Phase);
        Assert.Equal(MiniQueuePhase.Idle, MiniQueueProgress.Compute(Array.Empty<BatchRenderJob>(), ids).Phase);

        // 단일(앰비언트) — ResultPath 후처리 산출물 우선
        var amb = Path.Combine(_dir, "bg_ambient15s.mp4");
        File.WriteAllText(amb, "v");
        var single = new BatchRenderJob { Status = BatchJobStatus.Completed, ResultPath = amb };
        var sp = MiniQueueProgress.Compute(new[] { single }, new[] { single.JobId });
        Assert.Equal(MiniQueuePhase.Completed, sp.Phase);
        Assert.Equal(Path.GetFullPath(amb), sp.FinalPath);
    }
}
