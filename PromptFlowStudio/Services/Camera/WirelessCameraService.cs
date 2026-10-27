using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Camera;

public enum CameraConnectionState { Disconnected, Connecting, Streaming, Error }

/// <summary>스트림 실시간 지표 — 해제 상태는 <see cref="Empty"/>(전부 0).</summary>
public sealed record StreamTelemetry(double Fps, string Resolution, int LatencyMs, int BitrateKbps)
{
    public static readonly StreamTelemetry Empty = new(0, "", 0, 0);
}

/// <summary>캡처 1장 메타 — <see cref="FrameDataUrl"/> 은 썸네일·I2V 핸드오프에 그대로 쓰는 BMP data URL.</summary>
public sealed record CapturedFrameMeta(Guid FrameId, DateTime Timestamp, string Resolution, string FrameDataUrl);

/// <summary>
/// [PromptFlowStudio] 무선 카메라(RTSP/SRT/WebRTC/HTTP) 스트림 연결 시뮬레이터 + 단일 프레임 캡처.
///   ▸ 캡처 버퍼는 Pure BCL 24bpp BMP(외부 디코더 0) — <see cref="QueueI2vReference"/> 로 프롬프트 입력의 I2V 첫 프레임에 1회 전달.
///   ▸ 연결 중 재호출/해제는 CTS 로 취소, 해제 시 텔레메트리·캡처 버퍼 초기화.
/// </summary>
public sealed class WirelessCameraService : IDisposable
{
    public const string DefaultResolution = "1920x1080";
    public const double NominalFps = 30.0;
    public const int DefaultBitrateKbps = 8000;
    /// <summary>캡처 버퍼 크기(16:9 축소 프록시) — 원본 해상도는 메타에만 기록.</summary>
    public const int BufferWidth = 192, BufferHeight = 108;

    private static readonly string[] Schemes = { "rtsp", "rtsps", "srt", "rtmp", "webrtc", "http", "https" };

    private readonly object _gate = new();
    private readonly TimeSpan _handshake;
    private CancellationTokenSource? _connectCts;
    private StreamTelemetry _telemetry = StreamTelemetry.Empty;
    private byte[]? _lastFrameBytes;
    private byte[]? _pendingI2v;
    private long _frameCounter;

    public WirelessCameraService() : this(TimeSpan.FromMilliseconds(350)) { }

    /// <summary>테스트용 — 핸드셰이크 지연 주입.</summary>
    public WirelessCameraService(TimeSpan handshake) => _handshake = handshake < TimeSpan.Zero ? TimeSpan.Zero : handshake;

    public event Action? StateChanged;

    public CameraConnectionState State { get; private set; } = CameraConnectionState.Disconnected;
    public string? Endpoint { get; private set; }
    public string? LastError { get; private set; }
    public CapturedFrameMeta? LastFrame { get; private set; }
    public bool HasPendingI2vReference => Volatile.Read(ref _pendingI2v) is not null;

    /// <summary>엔드포인트 검증 → Connecting → 핸드셰이크 → Streaming. 형식 오류는 Error(false), 취소/해제는 Disconnected(false).</summary>
    public async Task<bool> ConnectStreamAsync(string streamEndpoint, CancellationToken ct = default)
    {
        if (!TryValidateEndpoint(streamEndpoint, out var error))
        {
            LastError = error;
            SetState(CameraConnectionState.Error);
            return false;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        CancellationTokenSource? old;
        lock (_gate) { old = _connectCts; _connectCts = cts; }
        CancelAndDispose(old);

        var token = cts.Token;   // 해제 경로가 cts 를 Dispose 해도 토큰 판독은 유효
        var endpoint = streamEndpoint.Trim();
        Endpoint = endpoint;
        LastError = null;
        SetState(CameraConnectionState.Connecting);
        try
        {
            if (_handshake > TimeSpan.Zero) await Task.Delay(_handshake, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _telemetry = new StreamTelemetry(NominalFps, DefaultResolution, LatencyFor(endpoint), DefaultBitrateKbps);
            SetState(CameraConnectionState.Streaming);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>연결 해제 — 진행 중 핸드셰이크 취소, 텔레메트리/캡처 버퍼 초기화. I2V 대기 버퍼는 이미 전송된 것이므로 보존.</summary>
    public void DisconnectStream()
    {
        CancellationTokenSource? old;
        lock (_gate) { old = _connectCts; _connectCts = null; }
        CancelAndDispose(old);

        _telemetry = StreamTelemetry.Empty;
        _lastFrameBytes = null;
        LastFrame = null;
        Endpoint = null;
        LastError = null;
        Interlocked.Exchange(ref _frameCounter, 0);
        SetState(CameraConnectionState.Disconnected);
    }

    /// <summary>현재 스트림에서 단일 프레임 캡처. Streaming 이 아니면 null.</summary>
    public CapturedFrameMeta? CaptureFrame()
    {
        if (State != CameraConnectionState.Streaming) return null;
        var n = Interlocked.Increment(ref _frameCounter);
        var bytes = RenderProxyBmp(n);
        var meta = new CapturedFrameMeta(Guid.NewGuid(), DateTime.UtcNow, _telemetry.Resolution,
            "data:image/bmp;base64," + Convert.ToBase64String(bytes));
        _lastFrameBytes = bytes;
        LastFrame = meta;
        Raise();
        return meta;
    }

    /// <summary>실시간 지표 — Streaming 중엔 프레임 카운터 기반 ±0.03fps 지터, 그 외 <see cref="StreamTelemetry.Empty"/>.</summary>
    public StreamTelemetry GetTelemetry()
    {
        var t = _telemetry;
        if (State != CameraConnectionState.Streaming) return StreamTelemetry.Empty;
        var jitter = (Environment.TickCount64 / 250 % 7 - 3) * 0.01;
        return t with { Fps = t.Fps + jitter };
    }

    /// <summary>마지막 캡처를 I2V 첫 프레임 대기 버퍼에 적재(캡처 없으면 false).</summary>
    public bool QueueI2vReference()
    {
        var bytes = _lastFrameBytes;
        if (bytes is null) return false;
        Volatile.Write(ref _pendingI2v, bytes);
        return true;
    }

    /// <summary>대기 중인 I2V 참조 프레임을 1회 소비(없으면 null).</summary>
    public byte[]? TakeI2vReference() => Interlocked.Exchange(ref _pendingI2v, null);

    public static bool TryValidateEndpoint(string? endpoint, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(endpoint)) { error = "스트림 주소를 입력하세요."; return false; }
        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        { error = "올바른 스트림 URL 이 아닙니다(예: rtsp://192.168.0.10:554/live)."; return false; }
        if (Array.IndexOf(Schemes, uri.Scheme.ToLowerInvariant()) < 0)
        { error = "지원 프로토콜: RTSP · SRT · RTMP · WebRTC · HTTP(S)."; return false; }
        return true;
    }

    /// <summary>프로토콜별 대표 지연(ms) — WebRTC/SRT 저지연, HTTP(MJPEG/HLS) 고지연.</summary>
    public static int LatencyFor(string endpoint)
    {
        var scheme = Uri.TryCreate(endpoint, UriKind.Absolute, out var u) ? u.Scheme.ToLowerInvariant() : "";
        return scheme switch
        {
            "webrtc" => 80,
            "srt" => 120,
            "rtsp" or "rtsps" => 180,
            "rtmp" => 450,
            _ => 900,
        };
    }

    /// <summary>24bpp bottom-up BMP 프록시 프레임 — 프레임 번호로 색조가 이동하는 그라디언트(캡처 시점 구분용).</summary>
    internal static byte[] RenderProxyBmp(long frameNo)
    {
        const int w = BufferWidth, h = BufferHeight;
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

    public void Dispose()
    {
        CancellationTokenSource? old;
        lock (_gate) { old = _connectCts; _connectCts = null; }
        CancelAndDispose(old);
        StateChanged = null;
    }

    private static void WriteLe(byte[] b, int o, int v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
    }

    private static void CancelAndDispose(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        cts.Dispose();
    }

    private void SetState(CameraConnectionState s)
    {
        State = s;
        Raise();
    }

    private void Raise()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { Program.Log("WirelessCamera.StateChanged", ex); }
    }
}
