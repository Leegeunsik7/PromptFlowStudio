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

    /// <summary>일부 바이트를 내보낸 뒤 실패(IOException)·조기 종료 또는 외부 CTS 취소를 일으키는 스트림 — 중간 실패/취소 재현.</summary>
    private sealed class FaultStream(int okBytes, Action? onFault, bool throwIo) : Stream
    {
        private int _sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _sent; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(ReadCore(buffer.Span, cancellationToken));
        private int ReadCore(Span<byte> dst, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_sent >= okBytes)
            {
                onFault?.Invoke();
                ct.ThrowIfCancellationRequested();
                if (throwIo) throw new IOException("연결 재설정(테스트)");
                return 0;
            }
            var n = Math.Min(dst.Length, okBytes - _sent);
            dst[..n].Fill(0x5A);
            _sent += n;
            return n;
        }
    }

    private static HttpResponseMessage Faulty(int okBytes, long declared, Action? onFault, bool throwIo)
    {
        var c = new StreamContent(new FaultStream(okBytes, onFault, throwIo));
        c.Headers.ContentLength = declared;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = c };
    }

    private FfmpegAutoDownloaderService FaultySvc(string tag, Func<HttpResponseMessage> route, out string work, out string baseDir)
    {
        work = Directory.CreateDirectory(Path.Combine(_dir, "w_" + tag)).FullName;
        baseDir = Path.Combine(_dir, "b_" + tag);
        var h = new MapHandler();
        h.Routes["https://m.test/ff.zip"] = route;
        return new FfmpegAutoDownloaderService(new HttpClient(h), new[] { new FfmpegMirror("m", "https://m.test/ff.zip", null) }, baseDir, static _ => TimeSpan.Zero) { WorkRoot = work };
    }

    private static void AssertNoLeftovers(string work, string baseDir)
    {
        Assert.Empty(Directory.GetFileSystemEntries(work, "*", SearchOption.AllDirectories));   // .tmp · 불완전 zip · 작업 폴더 전부 롤백
        Assert.False(File.Exists(Path.Combine(baseDir, "tools", "ffmpeg", "bin", "ffmpeg.exe")));
    }

    [Fact]
    public async Task FfmpegDownloader_CleansUpTempFiles_OnFailureOrCancellation()
    {
        // 1) 중간 실패(연결 끊김) — 진행률 보고 후 IOException → 결과 실패 · 임시 파일 0
        var reports = new List<float>();
        var s1 = FaultySvc("io", () => Faulty(300_000, 1_000_000, null, throwIo: true), out var w1, out var b1);
        var r1 = await s1.DownloadAndExtractAsync(new SyncProgress(reports), CancellationToken.None, requireSupportedPlatform: false);
        Assert.False(r1.Ok);
        Assert.Contains("연결 재설정", r1.Message);
        Assert.Contains(reports, v => v > 0.2f && v < 0.9f);   // 수신/전체 바이트 실시간 백분율(0.3 × 0.9 ≈ 0.27)
        AssertNoLeftovers(w1, b1);

        // 2) 선언 길이보다 짧게 끝난 응답(불완전 압축) — 실패 · 롤백
        var s2 = FaultySvc("short", () => Faulty(200_000, 900_000, null, throwIo: false), out var w2, out var b2);
        var r2 = await s2.DownloadAndExtractAsync(null, CancellationToken.None, requireSupportedPlatform: false);
        Assert.False(r2.Ok);
        Assert.Contains("중간에 끊겼", r2.Message);
        AssertNoLeftovers(w2, b2);

        // 3) 사용자 취소 — 다운로드 도중 CTS 취소 → OperationCanceledException 전파 · 롤백
        using var cts = new CancellationTokenSource();
        var s3 = FaultySvc("cancel", () => Faulty(250_000, 1_000_000, cts.Cancel, throwIo: false), out var w3, out var b3);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s3.DownloadAndExtractAsync(null, cts.Token, requireSupportedPlatform: false));
        AssertNoLeftovers(w3, b3);

        // 4) 친절한 실패 문구
        Assert.Equal("⚠ 다운로드 실패: 연결 재설정. 네트워크 상태를 확인하세요.", PromptFlowStudio.ViewModels.SettingsViewModel.FormatFfmpegFailure("연결 재설정."));
        Assert.Equal("⚠ 다운로드 실패: 알 수 없는 오류. 네트워크 상태를 확인하세요.", PromptFlowStudio.ViewModels.SettingsViewModel.FormatFfmpegFailure(null));
    }

    [Fact]
    public void RenderQueue_CancelJobGroup_CancelsAllCuts_AndMiniQueueShowsCancelled()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var jobs = q.EnqueueJob(new BatchRenderJob { Prompt = "p", DurationMode = VideoDurationMode.Sec15_Triple, OutputPath = Path.Combine(_dir, "proj.mp4") }, null, "proj");
        Assert.Equal(3, jobs.Count);
        var ids = new List<Guid>();
        foreach (var j in jobs) ids.Add(j.JobId);
        var group = jobs[0].StitchGroupId!;
        var other = q.EnqueueJob(new BatchRenderJob { Prompt = "x", DurationMode = VideoDurationMode.Sec10_Dual, OutputPath = Path.Combine(_dir, "other.mp4") }, null, "other");
        Assert.Equal(2, other.Count);

        var p = MiniQueueProgress.Compute(q.Snapshot(), ids);
        Assert.Equal(MiniQueuePhase.Rendering, p.Phase);
        Assert.True(p.IsActive);

        Assert.Equal(3, q.CancelJobGroup(group));
        Assert.Equal(0, q.CancelJobGroup(group));   // 중복 취소 차단
        Assert.Equal(0, q.CancelJobGroup(""));
        foreach (var j in q.Snapshot())
            Assert.Equal(string.Equals(j.StitchGroupId, group, StringComparison.Ordinal) ? BatchJobStatus.Cancelled : BatchJobStatus.Queued, j.Status);

        p = MiniQueueProgress.Compute(q.Snapshot(), ids);
        Assert.Equal(MiniQueuePhase.Cancelled, p.Phase);
        Assert.False(p.IsActive);
        Assert.StartsWith("⏹ 취소됨 (Cancelled)", p.Label);
        Assert.Equal(MiniQueuePhase.Cancelled, new MiniQueueProgress(MiniQueuePhase.Rendering, 3, 1, 40, null).AsCancelled().Phase);
    }
}
