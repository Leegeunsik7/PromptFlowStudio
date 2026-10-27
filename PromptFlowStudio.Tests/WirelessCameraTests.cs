using PromptFlowStudio.Services.Camera;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>무선 카메라 — 연결 상태 전이 · 캡처 메타/BMP 버퍼 · 해제 초기화 · I2V 핸드오프 회귀 잠금.</summary>
public class WirelessCameraTests
{
    [Fact]
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
    public async Task DisconnectStream_ResetsTelemetryAndState()
    {
        using var svc = new WirelessCameraService(TimeSpan.Zero);
        var vm = new WirelessCameraViewModel(svc) { StreamEndpointInput = "srt://10.0.0.5:9000" };

        await vm.ToggleConnection();
        Assert.True(vm.IsStreaming);
        Assert.Equal(120, vm.LatencyMs);
        vm.CaptureCurrentFrame();
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
}
