using System.Net;
using System.Net.Sockets;
using System.Text;
using PromptFlowStudio.Services.Camera;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>무선 카메라 — 연결 상태 전이 · 캡처 메타/BMP 버퍼 · 해제 초기화 · I2V 핸드오프 · 실물 수신기(루프백 HTTP)/폴백 회귀 잠금.</summary>
public class WirelessCameraTests
{
    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task ConnectStream_TransitionsToStreamingState()
    {
        using var svc = new WirelessCameraService(TimeSpan.Zero);
        var seen = new List<CameraConnectionState>();
        svc.StateChanged += () => seen.Add(svc.State);

        Assert.True(await svc.ConnectStreamAsync("rtsp://192.168.0.10:554/live"));

        Assert.Equal(CameraConnectionState.Streaming, svc.State);
        Assert.Equal(new[] { CameraConnectionState.Connecting, CameraConnectionState.Streaming }, seen);
        var t = svc.GetTelemetry();
        Assert.InRange(t.Fps, 29.9, 30.1);
        Assert.Equal("1920x1080", t.Resolution);
        Assert.Equal(180, t.LatencyMs);
        Assert.True(t.BitrateKbps > 0);

        // 형식 오류/미지원 프로토콜 → Error
        using var bad = new WirelessCameraService(TimeSpan.Zero);
        Assert.False(await bad.ConnectStreamAsync("ftp://cam.local/x"));
        Assert.Equal(CameraConnectionState.Error, bad.State);
        Assert.False(string.IsNullOrEmpty(bad.LastError));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task CaptureFrame_ProducesValidMetadataAndTimestamp()
    {
        using var svc = new WirelessCameraService(TimeSpan.Zero);
        Assert.Null(svc.CaptureFrame());   // 미연결 시 캡처 불가

        await svc.ConnectStreamAsync("webrtc://cam.local/stream");
        var before = DateTime.UtcNow;
        var a = svc.CaptureFrame();
        var b = svc.CaptureFrame();

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotEqual(Guid.Empty, a!.FrameId);
        Assert.NotEqual(a.FrameId, b!.FrameId);
        Assert.Equal(DateTimeKind.Utc, a.Timestamp.Kind);
        Assert.InRange(a.Timestamp, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
        Assert.Equal("1920x1080", a.Resolution);
        Assert.StartsWith("data:image/bmp;base64,", a.FrameDataUrl);
        Assert.Same(b, svc.LastFrame);

        var bytes = Convert.FromBase64String(a.FrameDataUrl["data:image/bmp;base64,".Length..]);
        Assert.True(PromptInputViewModel.TryReadImageSize(bytes, out var w, out var h));
        Assert.Equal(WirelessCameraService.BufferWidth, w);
        Assert.Equal(WirelessCameraService.BufferHeight, h);

        // I2V 핸드오프 — VM 전송 → 1회 소비
        var vm = new WirelessCameraViewModel(svc);
        Assert.True(vm.SendToI2vReference());
        Assert.True(vm.I2vQueued);
        Assert.NotNull(svc.TakeI2vReference());
        Assert.Null(svc.TakeI2vReference());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DisconnectStream_ResetsTelemetryAndState()
    {
        using var svc = new WirelessCameraService(TimeSpan.Zero);
        var vm = new WirelessCameraViewModel(svc) { StreamEndpointInput = "srt://10.0.0.5:9000" };

        await vm.ToggleConnection();
        Assert.True(vm.IsStreaming);
        Assert.Equal(120, vm.LatencyMs);
        await vm.CaptureCurrentFrame();
        Assert.NotNull(vm.LastCapturedFrame);

        await vm.ToggleConnection();   // 해제

        Assert.Equal(CameraConnectionState.Disconnected, svc.State);
        Assert.Equal(StreamTelemetry.Empty, svc.GetTelemetry());
        Assert.Null(svc.LastFrame);
        Assert.Null(svc.Endpoint);
        Assert.Equal(0, vm.CurrentFps);
        Assert.Equal(0, vm.LatencyMs);
        Assert.Equal("—", vm.ResolutionDisplay);
        Assert.Null(svc.CaptureFrame());

        // 핸드셰이크 중 해제 → 취소되어 Streaming 으로 넘어가지 않음
        using var slow = new WirelessCameraService(TimeSpan.FromSeconds(5));
        var pending = slow.ConnectStreamAsync("rtsp://cam.local/live");
        Assert.Equal(CameraConnectionState.Connecting, slow.State);
        slow.DisconnectStream();
        Assert.False(await pending);
        Assert.Equal(CameraConnectionState.Disconnected, slow.State);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NetworkReceiver_FallsBackToMock_WhenEndpointUnreachable()
    {
        // 바인딩 후 즉시 해제한 루프백 포트 — 연결 거부(외부 네트워크 불필요)
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var receiver = new NetworkCameraStreamReceiver();
        using var svc = new WirelessCameraService(receiver);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(await svc.ConnectStreamAsync("http://127.0.0.1:" + port + "/shot.jpg"));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(6), "폴백 지연: " + sw.Elapsed);
        Assert.Equal(CameraConnectionState.Streaming, svc.State);
        Assert.True(svc.IsFallbackMode);
        Assert.False(string.IsNullOrEmpty(svc.FallbackReason));
        Assert.Equal(WirelessCameraService.DefaultResolution, svc.GetTelemetry().Resolution);

        var frame = await svc.CaptureFrameAsync();
        Assert.NotNull(frame);
        Assert.StartsWith("data:image/bmp;base64,", frame!.FrameDataUrl);
        Assert.Equal(WirelessCameraService.DefaultResolution, frame.Resolution);

        // WebRTC(시그널링 미지원) 도 크래시 없이 폴백
        Assert.True(await receiver.ConnectAsync("webrtc://cam.local/stream", CancellationToken.None));
        Assert.True(receiver.IsFallback);
        Assert.NotNull(await receiver.CaptureFrameBytesAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CaptureFrame_ReturnsValidImageBytes_WhenConnected()
    {
        // 루프백 HTTP 스냅샷 서버 — 매 요청마다 64x36 BMP 응답
        var served = BuildBmp(64, 36);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var server = ServeSnapshotsAsync(listener, served, stop.Token);
        try
        {
            using var receiver = new NetworkCameraStreamReceiver();
            using var svc = new WirelessCameraService(receiver);

            Assert.True(await svc.ConnectStreamAsync("http://127.0.0.1:" + port + "/shot.jpg"));
            Assert.Equal(CameraConnectionState.Streaming, svc.State);
            Assert.False(svc.IsFallbackMode, svc.FallbackReason);
            Assert.Equal("64x36", svc.GetTelemetry().Resolution);

            var bytes = await receiver.CaptureFrameBytesAsync(CancellationToken.None);
            Assert.Equal(served, bytes);
            Assert.True(CameraFrameCodec.TryReadSize(bytes!, out var w, out var h));
            Assert.Equal((64, 36), (w, h));

            var meta = await svc.CaptureFrameAsync();
            Assert.NotNull(meta);
            Assert.Equal("64x36", meta!.Resolution);
            Assert.StartsWith("data:image/bmp;base64,", meta.FrameDataUrl);
            Assert.Equal(served, Convert.FromBase64String(meta.FrameDataUrl["data:image/bmp;base64,".Length..]));
            Assert.True(svc.QueueI2vReference());

            svc.DisconnectStream();
            Assert.False(receiver.IsStreaming);
            Assert.Null(await receiver.CaptureFrameBytesAsync(CancellationToken.None));
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            try { await server; } catch (Exception) { }
        }
    }

    private static async Task ServeSnapshotsAsync(TcpListener listener, byte[] body, CancellationToken ct)
    {
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/bmp\r\nContent-Length: "
            + body.Length + "\r\nConnection: close\r\n\r\n");
        while (!ct.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var buf = new byte[4096];
            var req = new StringBuilder();
            while (!req.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                var n = await stream.ReadAsync(buf, ct);
                if (n == 0) break;
                req.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
        }
    }

    private static byte[] BuildBmp(int w, int h)
    {
        var stride = (w * 3 + 3) & ~3;
        var b = new byte[54 + stride * h];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(b.Length).CopyTo(b, 2);
        BitConverter.GetBytes(54).CopyTo(b, 10);
        BitConverter.GetBytes(40).CopyTo(b, 14);
        BitConverter.GetBytes(w).CopyTo(b, 18);
        BitConverter.GetBytes(h).CopyTo(b, 22);
        b[26] = 1; b[28] = 24;
        for (var i = 54; i < b.Length; i++) b[i] = (byte)(i * 7);
        return b;
    }
}
