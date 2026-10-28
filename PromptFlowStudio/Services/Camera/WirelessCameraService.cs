using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Camera;

public enum CameraConnectionState { Disconnected, Connecting, Streaming, Error }

/// <summary>스트림 실시간 지표 — 해제 상태는 <see cref="Empty"/>(전부 0).</summary>
public sealed record StreamTelemetry(double Fps, string Resolution, int LatencyMs, int BitrateKbps)
{
    public static readonly StreamTelemetry Empty = new(0, "", 0, 0);
}

/// <summary>
/// 캡처 1장 메타 — <see cref="Data"/> 는 이미지(JPEG/PNG/BMP) 원본 바이트(I2V 핸드오프에 그대로 사용).
///   ▸ 롤 보관은 원본 바이트만 — <see cref="FrameDataUrl"/> 은 렌더 시 온디맨드 인코딩 후 약참조 캐시(화면에서 내려가면 GC 회수).
/// </summary>
public sealed record CapturedFrameMeta(Guid FrameId, DateTime Timestamp, string Resolution, string Mime, byte[] Data)
{
    private WeakReference<string>? _dataUrl;

    /// <summary>썸네일·뷰파인더용 data URL — 표시 중엔 동일 문자열 재사용, 비표시 시 회수되면 재인코딩.</summary>
    public string FrameDataUrl
    {
        get
        {
            if (_dataUrl is not null && _dataUrl.TryGetTarget(out var cached)) return cached;
            var url = "data:" + Mime + ";base64," + Convert.ToBase64String(Data);
            _dataUrl = new WeakReference<string>(url);
            return url;
        }
    }
}

/// <summary>
/// [PromptFlowStudio] 무선 카메라(RTSP/SRT/WebRTC/HTTP) 스트림 연결 + 단일 프레임 캡처 — 수신은 <see cref="ICameraStreamReceiver"/> 위임.
///   ▸ 실물 프레임(JPEG/PNG/BMP) 또는 폴백 프록시 BMP — <see cref="QueueI2vReference"/> 로 프롬프트 입력의 I2V 첫 프레임에 1회 전달.
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
    private readonly ICameraStreamReceiver _receiver;
    private CancellationTokenSource? _connectCts;
    private byte[]? _lastFrameBytes;
    private byte[]? _pendingI2v;

    /// <summary>DI — 실물 수신기(<see cref="NetworkCameraStreamReceiver"/>) 주입.</summary>
    public WirelessCameraService(ICameraStreamReceiver receiver) : this(receiver, TimeSpan.Zero) { }

    /// <summary>테스트/오프라인용 — <see cref="MockStreamReceiver"/> + 핸드셰이크 지연 주입.</summary>
    public WirelessCameraService(TimeSpan handshake) : this(new MockStreamReceiver(), handshake) { }

    public WirelessCameraService(ICameraStreamReceiver receiver, TimeSpan handshake)
    {
        _receiver = receiver ?? throw new ArgumentNullException(nameof(receiver));
        _handshake = handshake < TimeSpan.Zero ? TimeSpan.Zero : handshake;
    }

    public event Action? StateChanged;

    public CameraConnectionState State { get; private set; } = CameraConnectionState.Disconnected;
    public string? Endpoint { get; private set; }
    public string? LastError { get; private set; }
    public CapturedFrameMeta? LastFrame { get; private set; }
    public bool HasPendingI2vReference => Volatile.Read(ref _pendingI2v) is not null;
    /// <summary>실물 수신 실패로 프록시 프레임 송출 중인가(사유 <see cref="FallbackReason"/>).</summary>
    public bool IsFallbackMode => _receiver.IsFallback;
    public string? FallbackReason => _receiver.FallbackReason;

    /// <summary>엔드포인트 검증 → Connecting → 핸드셰이크 → 수신기 연결(실패 시 폴백) → Streaming. 형식 오류는 Error(false), 취소/해제는 Disconnected(false).</summary>
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
            if (!await _receiver.ConnectAsync(endpoint, token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                LastError = "스트림 수신기 연결 실패";
                SetState(CameraConnectionState.Error);
                return false;
            }
            token.ThrowIfCancellationRequested();
            SetState(CameraConnectionState.Streaming);
            return true;
        }
        catch (OperationCanceledException)
        {
            bool current;
            lock (_gate) current = ReferenceEquals(_connectCts, cts);
            if (current) _receiver.Disconnect();   // 외부 토큰 취소 — 수신기만 정리(해제 경로는 DisconnectStream 이 처리)
            return false;
        }
        catch (Exception ex)
        {
            Program.Log("WirelessCamera.Connect", ex);
            LastError = "스트림 연결 오류: " + ex.GetType().Name;
            SetState(CameraConnectionState.Error);
            return false;
        }
    }

    /// <summary>연결 해제 — 진행 중 핸드셰이크 취소, 텔레메트리/캡처 버퍼 초기화. I2V 대기 버퍼는 이미 전송된 것이므로 보존.</summary>
    public void DisconnectStream()
    {
        CancellationTokenSource? old;
        lock (_gate) { old = _connectCts; _connectCts = null; }
        CancelAndDispose(old);
        _receiver.Disconnect();

        _lastFrameBytes = null;
        LastFrame = null;
        Endpoint = null;
        LastError = null;
        SetState(CameraConnectionState.Disconnected);
    }

    /// <summary>동기 캡처(폴백/Mock 은 즉시 완료) — UI 는 <see cref="CaptureFrameAsync"/> 사용.</summary>
    public CapturedFrameMeta? CaptureFrame() => CaptureFrameAsync().GetAwaiter().GetResult();

    /// <summary>
    /// 현재 스트림에서 단일 프레임 캡처. Streaming 이 아니거나 수신 실패면 null(LastError 기록).
    ///   해상도: 실물 프레임은 헤더 실측, 폴백 프록시는 공칭 해상도(텔레메트리).
    /// </summary>
    public async Task<CapturedFrameMeta?> CaptureFrameAsync(CancellationToken ct = default)
    {
        if (State != CameraConnectionState.Streaming) return null;
        byte[]? bytes;
        try { bytes = await _receiver.CaptureFrameBytesAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            Program.Log("WirelessCamera.Capture", ex);
            bytes = null;
        }
        if (State != CameraConnectionState.Streaming) return null;   // 수신 중 해제
        var mime = bytes is null ? null : CameraFrameCodec.MimeOf(bytes);
        if (bytes is null || mime is null)
        {
            LastError = "프레임 수신 실패 — 카메라 응답을 확인하세요.";
            Raise();
            return null;
        }

        var resolution = !_receiver.IsFallback && CameraFrameCodec.TryReadSize(bytes, out var w, out var h)
            ? w.ToString(CultureInfo.InvariantCulture) + "x" + h.ToString(CultureInfo.InvariantCulture)
            : _receiver.GetCurrentTelemetry().Resolution;
        var meta = new CapturedFrameMeta(Guid.NewGuid(), DateTime.UtcNow, resolution, mime, bytes);
        _lastFrameBytes = bytes;
        LastFrame = meta;
        LastError = null;
        Raise();
        return meta;
    }

    /// <summary>실시간 지표 — Streaming 중엔 수신기 지표 + ±0.03fps 지터, 그 외 <see cref="StreamTelemetry.Empty"/>.</summary>
    public StreamTelemetry GetTelemetry()
    {
        if (State != CameraConnectionState.Streaming) return StreamTelemetry.Empty;
        var t = _receiver.GetCurrentTelemetry();
        if (t.Fps <= 0) return t;
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

    /// <summary>캡처 롤의 특정 프레임을 I2V 대기 버퍼에 적재 — 롤이 보관한 원본 바이트 그대로(빈 프레임이면 false).</summary>
    public bool QueueI2vReference(CapturedFrameMeta frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = frame.Data;
        if (bytes is not { Length: > 0 }) return false;
        Volatile.Write(ref _pendingI2v, bytes);
        return true;
    }

    /// <summary>대기 중인 I2V 참조 프레임을 1회 소비(없으면 null).</summary>
    public byte[]? TakeI2vReference() => Interlocked.Exchange(ref _pendingI2v, null);

    private CameraWorkspaceTransfer? _pendingTransfer;
    public bool HasPendingWorkspaceTransfer => Volatile.Read(ref _pendingTransfer) is not null;

    /// <summary>작업실 전송 페이로드(모션·강도·속도) 적재 — 작업실 진입 시 1회 소비되어 프롬프트 병합 + I2V 자동 활성화.</summary>
    public void QueueWorkspaceTransfer(CameraWorkspaceTransfer transfer)
        => Volatile.Write(ref _pendingTransfer, transfer ?? throw new ArgumentNullException(nameof(transfer)));

    public CameraWorkspaceTransfer? TakeWorkspaceTransfer() => Interlocked.Exchange(ref _pendingTransfer, null);

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

    public void Dispose()
    {
        CancellationTokenSource? old;
        lock (_gate) { old = _connectCts; _connectCts = null; }
        CancelAndDispose(old);
        _receiver.Disconnect();   // 수신기 자원(HttpClient)은 DI 컨테이너 소유
        StateChanged = null;
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
