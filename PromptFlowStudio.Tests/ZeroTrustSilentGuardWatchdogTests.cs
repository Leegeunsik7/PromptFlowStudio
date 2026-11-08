using Microsoft.JSInterop;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Platform;
using PromptFlowStudio.Services.ProcessBridge;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>🛡️ Zero-Trust 무음 가드 · 🩺 핫리로드 워치독 자동 롤백 · 🎞️ 라이트박스 JS 시각 · 🎮 GPU 배지 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class ZeroTrustSilentGuardWatchdogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ztw_" + Guid.NewGuid().ToString("N"));

    public ZeroTrustSilentGuardWatchdogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>ftyp 헤더 + <c>hdlr vide</c> 만 가진 최소 MP4(무음 확인 가능).</summary>
    internal static byte[] SilentMp4()
    {
        var b = new byte[4096];
        byte[] ftyp = [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];
        ftyp.CopyTo(b, 0);
        byte[] hdlr = [0, 0, 0, 0x21, (byte)'h', (byte)'d', (byte)'l', (byte)'r', 0, 0, 0, 0, 0, 0, 0, 0, (byte)'v', (byte)'i', (byte)'d', (byte)'e'];
        hdlr.CopyTo(b, 1000);
        return b;
    }

    private static byte[] Mkv(string codecId)
    {
        var b = new byte[2048];
        b[0] = 0x1A; b[1] = 0x45; b[2] = 0xDF; b[3] = 0xA3;
        b[600] = 0x86; b[601] = (byte)(0x80 | codecId.Length);
        System.Text.Encoding.ASCII.GetBytes(codecId).CopyTo(b, 602);
        return b;
    }

    [Fact]
    public async Task StockSuiteExporter_ZeroTrustSilentGuard_RejectsUnrecognizedContainers_WhenFfmpegUnavailable()
    {
        var dest = Path.Combine(_dir, "stock");
        Directory.CreateDirectory(dest);
        var ex = new StockSuiteExporter(() => dest, null, () => null);

        // 판별 불가(매직 부재) MP4 · 미지원 확장자(AVI) · 비디오 트랙 미확인 → 거부
        var blank = Path.Combine(_dir, "blank.mp4");
        File.WriteAllBytes(blank, new byte[4096]);
        Assert.False(StockSuiteExporter.ContainsAudioTrack(blank));   // 기존 스캐너는 "오디오 없음" 이지만
        Assert.Equal(SilentVerdict.Unverified, StockSuiteExporter.VerifySilentContainer(blank));
        var r = await ex.ExportAsync(blank, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false);
        Assert.False(r.Ok);
        Assert.Equal(StockSuiteExporter.ZeroTrustRejectedMessage, r.Message);

        var avi = Path.Combine(_dir, "clip.avi");
        File.WriteAllBytes(avi, SilentMp4());
        Assert.Equal(SilentVerdict.Unverified, StockSuiteExporter.VerifySilentContainer(avi));
        Assert.Equal(StockSuiteExporter.ZeroTrustRejectedMessage, (await ex.ExportAsync(avi, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false)).Message);

        var noTrack = Path.Combine(_dir, "notrack.mp4");
        var nt = new byte[4096];
        SilentMp4().AsSpan(0, 12).CopyTo(nt);
        File.WriteAllBytes(noTrack, nt);
        Assert.Equal(SilentVerdict.Unverified, StockSuiteExporter.VerifySilentContainer(noTrack));

        // WebM 경로(ffmpeg 실패 → MP4 폴백)도 동일 거부
        FfmpegRunner fail = (_, _, _) => Task.FromResult(false);
        var failing = new StockSuiteExporter(() => dest, null, () => "ff", fail);
        var rw = await failing.ExportAsync(blank, format: StockSuiteExportFormat.WebMVp9, sharpen: false, smooth60: false);
        Assert.False(rw.Ok);
        Assert.Equal(StockSuiteExporter.ZeroTrustRejectedMessage, rw.Message);
        Assert.Empty(Directory.GetFiles(dest, "*.mp4"));

        // MKV/WebM: V_ 만 = Silent · A_ = HasAudio
        var mkvV = Path.Combine(_dir, "v.mkv");
        File.WriteAllBytes(mkvV, Mkv("V_VP9"));
        Assert.Equal(SilentVerdict.Silent, StockSuiteExporter.VerifySilentContainer(mkvV));
        var webmA = Path.Combine(_dir, "a.webm");
        var wa = Mkv("V_VP9");
        wa[900] = 0x86; wa[901] = 0x88;
        "A_OPUS"u8.CopyTo(wa.AsSpan(902));
        File.WriteAllBytes(webmA, wa);
        Assert.Equal(SilentVerdict.HasAudio, StockSuiteExporter.VerifySilentContainer(webmA));

        // 확인된 무음 MP4 만 원본 복사 허용
        var ok = Path.Combine(_dir, "ok.mp4");
        File.WriteAllBytes(ok, SilentMp4());
        Assert.Equal(SilentVerdict.Silent, StockSuiteExporter.VerifySilentContainer(ok));
        var good = await ex.ExportAsync(ok, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false);
        Assert.True(good.Ok, good.Message);
        Assert.Null(good.HotReloadWatch);   // 워치독 미결선 = 감시 없음

        // 주입 검증기 Unverified → 거부
        ex.SilentContainerVerifier = _ => SilentVerdict.Unverified;
        Assert.Equal(StockSuiteExporter.ZeroTrustRejectedMessage, (await ex.ExportAsync(ok, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false)).Message);
    }

    [Fact]
    public async Task StockSuiteHotReloadWatchdog_TriggersAutoRollback_OnAcknowledgementTimeout()
    {
        var dest = Path.Combine(_dir, "stock");
        Directory.CreateDirectory(dest);
        var ex = new StockSuiteExporter(() => dest, null, () => null);
        var wd = new StockSuiteHotReloadWatchdog(ex.RollbackPreviousAssetAsync) { Timeout = TimeSpan.FromSeconds(30), PollInterval = TimeSpan.FromMilliseconds(20) };
        using var _ = wd;
        HotReloadWatchResult? notified = null;
        wd.OnCompleted += r => notified = r;

        var v1 = Path.Combine(_dir, "v1.mp4");
        var b1 = SilentMp4(); b1[2000] = 1;
        File.WriteAllBytes(v1, b1);
        var v2 = Path.Combine(_dir, "v2.mp4");
        var b2 = SilentMp4(); b2[2000] = 2;
        File.WriteAllBytes(v2, b2);
        Assert.True((await ex.ExportAsync(v1, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false)).Ok);

        ex.Watchdog = wd;
        // 스테일 ack 는 무장 시 제거되어야 함
        File.WriteAllText(StockSuiteHotReloadWatchdog.AckPathFor(dest), "");
        var r2 = await ex.ExportAsync(v2, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false);
        Assert.True(r2.Ok, r2.Message);
        Assert.True(File.Exists(Path.Combine(dest, StockSuiteExporter.HotReloadTriggerName)));
        var active = Path.Combine(dest, StockSuiteExporter.DefaultAssetName);
        Assert.Equal(2, File.ReadAllBytes(active)[2000]);
        Assert.NotNull(r2.HotReloadWatch);
        wd.Timeout = TimeSpan.FromMilliseconds(250);   // 교체 확인 후 타임아웃 단축(대기 루프가 매 폴링 재평가) — 부하 시 경합 제거

        var w = await r2.HotReloadWatch!;
        Assert.Equal(HotReloadOutcome.RolledBack, w.Outcome);
        Assert.Equal(StockSuiteHotReloadWatchdog.RollbackWarning, w.Message);
        Assert.Same(w, notified);
        Assert.Equal(1, File.ReadAllBytes(active)[2000]);   // bg_ambient.backup.mp4 → 복원
        Assert.False(wd.IsWatching);

        // IPC ack → 롤백 없음
        wd.Timeout = TimeSpan.FromSeconds(30);   // ack 경로는 즉시 해제 — 부하 시 오탐 방지
        var r3 = await ex.ExportAsync(v2, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false);
        var ipc = new StockSuiteIpcBridge(null, "pfs_test_" + Guid.NewGuid().ToString("N"));
        ipc.OnHotReloadAck += wd.Acknowledge;
        Assert.True(ipc.DispatchLine("{\"type\":\"HotReloadAck\"}"));
        Assert.Equal(HotReloadOutcome.Acknowledged, (await r3.HotReloadWatch!).Outcome);
        Assert.Equal(2, File.ReadAllBytes(active)[2000]);

        // 파일 ack 오류 보고 → 롤백
        var r4 = await ex.ExportAsync(v1, format: StockSuiteExportFormat.Mp4, sharpen: false, smooth60: false);
        File.WriteAllText(StockSuiteHotReloadWatchdog.AckPathFor(dest), "{\"ok\":false,\"error\":\"decode failed\"}");
        var w4 = await r4.HotReloadWatch!;
        Assert.Equal(HotReloadOutcome.RolledBack, w4.Outcome);
        Assert.Equal("decode failed", w4.Error);

        // IPC 파싱 · StockSuite 미가동 → 감시 생략
        var perr = StockSuiteIpcBridge.ParseHotReloadAck("{\"type\":\"HotReloadError\",\"error\":\"x\"}");
        Assert.NotNull(perr);
        Assert.False(perr.Value.Ok);
        Assert.Equal("x", perr.Value.Error);
        Assert.Null(StockSuiteIpcBridge.ParseHotReloadAck("{\"type\":\"Nope\"}"));
        using var idle = new StockSuiteHotReloadWatchdog(ex.RollbackPreviousAssetAsync, () => false);
        Assert.Null(idle.Arm(active));
        ipc.Dispose();
    }

    private sealed class FakeJs(Func<string, object?[]?, object?> impl) : IJSRuntime
    {
        public string? LastIdentifier;
        public object?[]? LastArgs;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            LastIdentifier = identifier; LastArgs = args;
            return new ValueTask<TValue>((TValue)impl(identifier, args)!);
        }
    }

    private sealed class NullYt : IYouTubeShortsPublisher
    {
        public bool Enabled => false;
        public Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ShortsPublishResult(false, false, null, null, false, "off"));
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NullIg : IInstagramReelsPublisher
    {
        public bool Enabled => false;
        public Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ReelsPublishOutcome(false, false, ReelsRoute.Mock, "x", "off"));
        public Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default) => Task.FromResult(false);
    }

    [Fact]
    public async Task GalleryLightbox_ExtractsExactTimestamp_ViaJsInterop()
    {
        var src = Path.Combine(_dir, "lb.mp4");
        File.WriteAllBytes(src, SilentMp4());
        var gvm = new GalleryViewModel(new GalleryMediaService());
        var js = new FakeJs((_, _) => 12.375);
        var at = await gvm.ResolveLightboxTimestampAsync(js, src);
        Assert.Equal(12.375, at);
        Assert.Equal("promptflow.getVideoCurrentTime", js.LastIdentifier);
        Assert.Equal(GalleryViewModel.LightboxVideoId, Assert.Single(js.LastArgs!));

        using var vm = new PublishViewModel(new NullYt(), new NullIg());
        vm.Initialize(src, at, "p");
        Assert.Equal(12.375, vm.PreviewTimestampSec);
        Assert.Equal(12.375, vm.CapturePreviewOptions().AtSeconds);

        // 요소 부재(-1) · JS 예외 · JS 미가용 → 폴백(갤러리 미등록 = 1초)
        Assert.Equal(1.0, await gvm.ResolveLightboxTimestampAsync(new FakeJs((_, _) => -1.0), src));
        Assert.Equal(1.0, await gvm.ResolveLightboxTimestampAsync(new FakeJs((_, _) => throw new JSException("gone")), src));
        Assert.Equal(1.0, await gvm.ResolveLightboxTimestampAsync(null, src));

        // promptflow.js 결선 확인
        var js2 = File.ReadAllText(FindRepoFile("PromptFlowStudio", "wwwroot", "promptflow.js"));
        Assert.Contains("pf.getVideoCurrentTime = function (elementId)", js2);
    }

    [Fact]
    public void StatusFooter_GpuBadge_FormatsAndColorCodes()
    {
        static ResourceSample S(double util, double used, double total) => new(8, 0, 0, 8192, util, used, total, "RTX", true);
        Assert.Equal("🎮 GPU: 42% | VRAM: 2048/8192MB", ResourceMonitorService.GpuBadgeText(S(42.4, 2048, 8192)));
        Assert.Equal("ok", ResourceMonitorService.GpuBadgeLevel(S(42, 2048, 8192)));
        Assert.Equal("warn", ResourceMonitorService.GpuBadgeLevel(S(75, 2048, 8192)));
        Assert.Equal("warn", ResourceMonitorService.GpuBadgeLevel(S(10, 6000, 8192)));   // VRAM 73%
        Assert.Equal("crit", ResourceMonitorService.GpuBadgeLevel(S(95, 2048, 8192)));
        Assert.Null(ResourceMonitorService.GpuBadgeText(new ResourceSample(8, 0, 0, 8192, null, null, null, null, false)));
    }

    private static string FindRepoFile(params string[] parts)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var p = Path.Combine(new[] { d.FullName }.Concat(parts).ToArray());
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
