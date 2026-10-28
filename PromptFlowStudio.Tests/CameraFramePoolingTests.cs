using PromptFlowStudio.Services.Camera;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>카메라 프레임 풀링 — ArrayPool 수신 버퍼 확장 정확성 · 상한 차단 · 롤 원본 바이트 보관/온디맨드 data URL 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class CameraFramePoolingTests
{
    private static byte[] Payload(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++) b[i] = (byte)(i * 31 + 7);
        return b;
    }

    /// <summary>요청 크기와 무관하게 소량씩 돌려주는 스트림(네트워크 분할 수신 모사).</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
    }

    [Fact]
    public async Task ReadAll_SpanningMultiplePoolGrowths_ReturnsExactBytes()
    {
        var src = Payload(NetworkCameraStreamReceiver.PoolChunk * 5 + 123);
        var got = await NetworkCameraStreamReceiver.ReadAllCappedAsync(new TrickleStream(src, 7000), CancellationToken.None);
        Assert.Equal(src, got);
    }

    [Fact]
    public async Task ReadAll_Empty_ReturnsNull()
        => Assert.Null(await NetworkCameraStreamReceiver.ReadAllCappedAsync(new MemoryStream(), CancellationToken.None));

    [Fact]
    public async Task ReadFirstJpeg_AcrossGrowth_ExtractsSoiToEoi()
    {
        var jpeg = Payload(NetworkCameraStreamReceiver.PoolChunk * 2);
        jpeg[0] = 0xFF; jpeg[1] = 0xD8;
        for (var i = 2; i < jpeg.Length - 2; i++) if (jpeg[i] == 0xFF) jpeg[i] = 0x00;   // 중간 마커 제거
        jpeg[^2] = 0xFF; jpeg[^1] = 0xD9;
        var head = "--frame\r\nContent-Type: image/jpeg\r\n\r\n"u8.ToArray();
        var stream = new byte[head.Length + jpeg.Length + 16];
        head.CopyTo(stream, 0);
        jpeg.CopyTo(stream, head.Length);

        var got = await NetworkCameraStreamReceiver.ReadFirstJpegAsync(new TrickleStream(stream, 5000), CancellationToken.None);
        Assert.Equal(jpeg, got);
    }

    [Fact]
    public void FrameMeta_DataUrl_IsOnDemand_AndReusedWhileReferenced()
    {
        var bytes = Payload(1024);
        var meta = new CapturedFrameMeta(Guid.NewGuid(), DateTime.UtcNow, "32x32", "image/jpeg", bytes);
        var a = meta.FrameDataUrl;
        var b = meta.FrameDataUrl;
        Assert.Same(a, b);
        Assert.StartsWith("data:image/jpeg;base64,", a);
        Assert.Equal(bytes, Convert.FromBase64String(a["data:image/jpeg;base64,".Length..]));
        GC.KeepAlive(a);
    }

    [Fact]
    public async Task CameraVm_TelemetryTick_DoesNotBumpStateVersion_ButCaptureDoes()
    {
        using var svc = new WirelessCameraService(TimeSpan.Zero);
        var vm = new WirelessCameraViewModel(svc);
        await vm.ToggleConnection();
        var v = vm.StateVersion;

        for (var i = 0; i < 10; i++) vm.RefreshTelemetry();
        Assert.Equal(v, vm.StateVersion);

        await vm.CaptureCurrentFrame();
        Assert.NotEqual(v, vm.StateVersion);
        Assert.Equal(vm.SelectedFrame!.Data, svc.TakeI2vReference() ?? (vm.SendToI2vReference() ? svc.TakeI2vReference() : null));
    }

    [Fact]
    public void FrameRoll_RetainedBytes_AtLeast60PercentSmallerThanBase64String()
    {
        const int frame = 200 * 1024;
        var bytes = Payload(frame);
        long before = 0, after = 0;
        for (var i = 0; i < 5; i++)
        {
            before += ("data:image/jpeg;base64," + Convert.ToBase64String(bytes)).Length * 2L + frame;   // 구: 문자열(UTF-16) + 최신 원본
            after += frame;                                                                                // 신: 원본 바이트만
        }
        Assert.True(1.0 - after / (double)before >= 0.6, $"saved={1.0 - after / (double)before:P1}");
    }
}
