using PromptFlowStudio.Services.Camera;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 무선 카메라 화면 VM — 연결 토글 · 프레임 캡처 · I2V 첫 프레임 핸드오프.
///   ▸ 텔레메트리는 <see cref="RefreshTelemetry"/> 호출 시점 스냅숏(페이지 타이머가 주기 호출).
/// </summary>
public sealed class WirelessCameraViewModel
{
    private readonly WirelessCameraService _camera;

    public WirelessCameraViewModel(WirelessCameraService camera)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
    }

    public event Action? Changed;

    public string StreamEndpointInput { get; set; } = "rtsp://192.168.0.10:554/live";
    public CameraConnectionState ConnectionState => _camera.State;
    public double CurrentFps { get; private set; }
    public int LatencyMs { get; private set; }
    public int BitrateKbps { get; private set; }
    public string ResolutionDisplay { get; private set; } = "—";
    public CapturedFrameMeta? LastCapturedFrame => _camera.LastFrame;
    public bool IsStreaming => _camera.State == CameraConnectionState.Streaming;
    public bool IsBusy => _camera.State == CameraConnectionState.Connecting;
    public bool I2vQueued => _camera.HasPendingI2vReference;
    public string? StatusMessage { get; private set; }

    /// <summary>Streaming/Connecting → 해제, 그 외 → 입력 주소로 연결.</summary>
    public async Task ToggleConnection()
    {
        if (_camera.State is CameraConnectionState.Streaming or CameraConnectionState.Connecting)
        {
            _camera.DisconnectStream();
            StatusMessage = "연결 해제됨";
        }
        else
        {
            StatusMessage = "연결 중…";
            Raise();
            var ok = await _camera.ConnectStreamAsync(StreamEndpointInput).ConfigureAwait(false);
            StatusMessage = !ok ? _camera.LastError ?? "연결 취소됨"
                : _camera.IsFallbackMode ? "폴백 프레임 수신 중(" + _camera.FallbackReason + ") · " + _camera.Endpoint
                : "스트리밍 중 · " + _camera.Endpoint;
        }
        RefreshTelemetry();
    }

    public async Task CaptureCurrentFrame()
    {
        var frame = await _camera.CaptureFrameAsync().ConfigureAwait(false);
        StatusMessage = frame is not null ? "프레임 캡처 완료 · " + frame.Resolution
            : _camera.State == CameraConnectionState.Streaming ? _camera.LastError ?? "프레임 수신 실패"
            : "스트리밍 중일 때만 캡처할 수 있습니다.";
        Raise();
    }

    /// <summary>마지막 캡처를 프롬프트 입력(워크스페이스)의 I2V 첫 프레임 버퍼로 전달.</summary>
    public bool SendToI2vReference()
    {
        var ok = _camera.QueueI2vReference();
        StatusMessage = ok ? "I2V 첫 프레임으로 전달됨 — 프롬프트 입력에서 적용됩니다." : "먼저 프레임을 캡처하세요.";
        Raise();
        return ok;
    }

    public void RefreshTelemetry()
    {
        var t = _camera.GetTelemetry();
        CurrentFps = t.Fps;
        LatencyMs = t.LatencyMs;
        BitrateKbps = t.BitrateKbps;
        ResolutionDisplay = string.IsNullOrEmpty(t.Resolution) ? "—" : t.Resolution;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
