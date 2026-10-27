using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Camera;

/// <summary>
/// [PromptFlowStudio] 오프라인/CI 수신기 — 공칭 1920x1080·30fps 텔레메트리 + 192x108 24bpp BMP 프록시 프레임(외부 장비 0).
///   ▸ 단독 사용(테스트) 및 <see cref="NetworkCameraStreamReceiver"/> 폴백 경로 공용.
/// </summary>
public sealed class MockStreamReceiver : ICameraStreamReceiver
{
    private StreamTelemetry _telemetry = StreamTelemetry.Empty;
    private long _frameCounter;
    private volatile bool _streaming;

    public bool IsStreaming => _streaming;
    public bool IsFallback => true;
    public string? FallbackReason => "시뮬레이션 수신기";

    public Task<bool> ConnectAsync(string endpoint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!WirelessCameraService.TryValidateEndpoint(endpoint, out _)) return Task.FromResult(false);
        _telemetry = new StreamTelemetry(WirelessCameraService.NominalFps, WirelessCameraService.DefaultResolution,
            WirelessCameraService.LatencyFor(endpoint.Trim()), WirelessCameraService.DefaultBitrateKbps);
        Interlocked.Exchange(ref _frameCounter, 0);
        _streaming = true;
        return Task.FromResult(true);
    }

    public void Disconnect()
    {
        _streaming = false;
        _telemetry = StreamTelemetry.Empty;
        Interlocked.Exchange(ref _frameCounter, 0);
    }

    public Task<byte[]?> CaptureFrameBytesAsync(CancellationToken ct)
    {
        if (!_streaming) return Task.FromResult<byte[]?>(null);
        return Task.FromResult<byte[]?>(RenderProxyBmp(Interlocked.Increment(ref _frameCounter)));
    }

    public StreamTelemetry GetCurrentTelemetry() => _streaming ? _telemetry : StreamTelemetry.Empty;

    /// <summary>24bpp bottom-up BMP 프록시 프레임 — 프레임 번호로 색조가 이동하는 그라디언트(캡처 시점 구분용).</summary>
    internal static byte[] RenderProxyBmp(long frameNo)
    {
        const int w = WirelessCameraService.BufferWidth, h = WirelessCameraService.BufferHeight;
        const int stride = (w * 3 + 3) & ~3;
        const int pixelBytes = stride * h;
        const int headerBytes = 54;
        var buf = new byte[headerBytes + pixelBytes];
        buf[0] = (byte)'B'; buf[1] = (byte)'M';
        WriteLe(buf, 2, buf.Length);
        WriteLe(buf, 10, headerBytes);
        WriteLe(buf, 14, 40);
        WriteLe(buf, 18, w);
        WriteLe(buf, 22, h);
        buf[26] = 1; buf[28] = 24;
        WriteLe(buf, 34, pixelBytes);
        var shift = (int)(frameNo * 37 % 256);
        for (var y = 0; y < h; y++)
        {
            var row = headerBytes + y * stride;
            for (var x = 0; x < w; x++)
            {
                var o = row + x * 3;
                buf[o] = (byte)(96 + y * 120 / h);                 // B
                buf[o + 1] = (byte)((x * 255 / w + shift) & 0xFF);  // G
                buf[o + 2] = (byte)(40 + (shift >> 1));            // R
            }
        }
        return buf;
    }

    private static void WriteLe(byte[] b, int o, int v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }
}
