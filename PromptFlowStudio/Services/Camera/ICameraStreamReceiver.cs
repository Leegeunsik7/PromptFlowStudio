using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Camera;

/// <summary>
/// [PromptFlowStudio] 카메라 스트림 수신기 계약 — 연결/해제/단일 프레임 바이트 캡처/텔레메트리.
///   ▸ 구현: <see cref="NetworkCameraStreamReceiver"/>(HTTP 스냅샷·MJPEG · FFmpeg RTSP) · <see cref="MockStreamReceiver"/>(오프라인/CI 프록시 프레임).
///   ▸ <see cref="IsFallback"/> = 실물 수신 실패로 프록시 프레임을 내보내는 중(사유는 <see cref="FallbackReason"/>).
/// </summary>
public interface ICameraStreamReceiver
{
    bool IsStreaming { get; }
    bool IsFallback { get; }
    string? FallbackReason { get; }

    /// <summary>형식 오류는 false. 네트워크 실패는 폴백 전환 후 true. 취소는 <see cref="System.OperationCanceledException"/>.</summary>
    Task<bool> ConnectAsync(string endpoint, CancellationToken ct);

    void Disconnect();

    /// <summary>인코딩된 프레임(JPEG/PNG/BMP) 1장 — 미연결이면 null.</summary>
    Task<byte[]?> CaptureFrameBytesAsync(CancellationToken ct);

    /// <summary>미연결 시 <see cref="StreamTelemetry.Empty"/>.</summary>
    StreamTelemetry GetCurrentTelemetry();
}

/// <summary>인코딩 프레임 판별 — 매직 바이트로 MIME, 헤더로 픽셀 크기(디코딩 없음).</summary>
public static class CameraFrameCodec
{
    public static string? MimeOf(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G') return "image/png";
        if (b.Length >= 26 && b[0] == (byte)'B' && b[1] == (byte)'M') return "image/bmp";
        return null;
    }

    public static bool TryReadSize(byte[] b, out int width, out int height)
    {
        width = height = 0;
        switch (MimeOf(b))
        {
            case "image/bmp":
                width = ReadLe(b, 18);
                height = System.Math.Abs(ReadLe(b, 22));
                break;
            case "image/png":
                if (b.Length < 24) return false;
                width = ReadBe(b, 16);
                height = ReadBe(b, 20);
                break;
            case "image/jpeg":
                // SOFn(C0~CF, DHT C4·JPG C8·DAC CC 제외) 세그먼트의 높이/너비
                var i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    var m = b[i + 1];
                    if (m == 0xFF) { i++; continue; }
                    if (m == 0xD8 || m == 0x01 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                    if (m >= 0xC0 && m <= 0xCF && m != 0xC4 && m != 0xC8 && m != 0xCC)
                    {
                        height = (b[i + 5] << 8) | b[i + 6];
                        width = (b[i + 7] << 8) | b[i + 8];
                        break;
                    }
                    i += 2 + ((b[i + 2] << 8) | b[i + 3]);
                }
                break;
            default:
                return false;
        }
        return width > 0 && height > 0;
    }

    private static int ReadLe(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
    private static int ReadBe(byte[] b, int o) => (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];
}
