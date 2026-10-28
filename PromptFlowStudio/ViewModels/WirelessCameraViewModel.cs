using System.Collections.ObjectModel;
using PromptFlowStudio.Services.Camera;

namespace PromptFlowStudio.ViewModels;

/// <summary>
/// [PromptFlowStudio] 무선 카메라 화면 VM — 연결 토글 · 프레임 캡처(캡처 롤 최근 5장) · 종횡비 가이드 · 모션 강도/속도 · 작업실 원클릭 전송.
///   ▸ 텔레메트리는 <see cref="RefreshTelemetry"/> 호출 시점 스냅숏(페이지 타이머가 주기 호출).
///   ▸ 캡처 롤은 연결 해제 후에도 유지 → 베스트 컷을 고른 뒤 전송 가능.
///   ▸ <see cref="StateVersion"/> 은 텔레메트리 외 상태 변경 시에만 증가 — 텔레메트리 미표시 화면은 주기 틱 렌더를 건너뛴다.
/// </summary>
public sealed class WirelessCameraViewModel
{
    public const int MaxRecentCaptures = 5;
    public static readonly string[] AspectGuides = { "16:9", "9:16", "1:1" };

    private readonly WirelessCameraService _camera;

    public WirelessCameraViewModel(WirelessCameraService camera)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
    }

    public event Action? Changed;

    private long _stateVersion;
    /// <summary>연결·캡처 롤·활성 프레임·가이드·상태 메시지 변경 버전(텔레메트리 틱은 미증가).</summary>
    public long StateVersion => Interlocked.Read(ref _stateVersion);

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

    /// <summary>캡처 롤(Filmstrip) — 최신이 0번, 최대 <see cref="MaxRecentCaptures"/>장(초과분은 가장 오래된 것부터 롤오버).</summary>
    public ObservableCollection<CapturedFrameMeta> RecentCaptures { get; } = new();

    /// <summary>현재 활성 프레임(작업실 I2V 첫 프레임 후보) — 새 캡처 시 자동 선택, 롤에서 클릭으로 변경.</summary>
    public CapturedFrameMeta? SelectedFrame { get; private set; }

    /// <summary>뷰파인더 세이프존 가이드("16:9" · "9:16" · "1:1").</summary>
    public string AspectGuide { get; private set; } = "16:9";

    public MotionIntensity Intensity { get; set; } = MotionIntensity.Normal;
    public double Speed { get; set; } = 1.0;

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
        Interlocked.Increment(ref _stateVersion);
        RefreshTelemetry();
    }

    public async Task CaptureCurrentFrame()
    {
        var frame = await _camera.CaptureFrameAsync().ConfigureAwait(false);
        if (frame is not null) AddToRoll(frame);
        StatusMessage = frame is not null ? "프레임 캡처 완료 · " + frame.Resolution + " · 롤 " + RecentCaptures.Count + "/" + MaxRecentCaptures
            : _camera.State == CameraConnectionState.Streaming ? _camera.LastError ?? "프레임 수신 실패"
            : "스트리밍 중일 때만 캡처할 수 있습니다.";
        Raise();
    }

    /// <summary>캡처 롤 최신순 누적 + 활성 프레임 갱신. 상한 초과 시 가장 오래된 컷 제거(활성 컷이면 최신으로 이동).</summary>
    public void AddToRoll(CapturedFrameMeta frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        RecentCaptures.Insert(0, frame);
        while (RecentCaptures.Count > MaxRecentCaptures) RecentCaptures.RemoveAt(RecentCaptures.Count - 1);
        SelectedFrame = frame;
        Interlocked.Increment(ref _stateVersion);
    }

    /// <summary>롤에서 활성 프레임 변경(롤에 없는 ID 면 false).</summary>
    public bool SelectFrame(Guid frameId)
    {
        foreach (var f in RecentCaptures)
        {
            if (f.FrameId != frameId) continue;
            SelectedFrame = f;
            Raise();
            return true;
        }
        return false;
    }

    public void SetAspectGuide(string guide)
    {
        if (Array.IndexOf(AspectGuides, guide) < 0) return;
        AspectGuide = guide;
        Raise();
    }

    /// <summary>활성 프레임을 프롬프트 입력(워크스페이스)의 I2V 첫 프레임 버퍼로 전달.</summary>
    public bool SendToI2vReference()
    {
        var f = SelectedFrame;
        var ok = f is not null ? _camera.QueueI2vReference(f) : _camera.QueueI2vReference();
        StatusMessage = ok ? "I2V 첫 프레임으로 전달됨 — 프롬프트 입력에서 적용됩니다." : "먼저 프레임을 캡처하세요.";
        Raise();
        return ok;
    }

    /// <summary>작업실 미리보기용 병합 결과(작업실은 자체 기존 프롬프트에 동일 규칙으로 병합).</summary>
    public string PreviewMerge(string? existingPrompt, string? cameraMotion)
        => SmartCameraMerge.MergeCameraPrompt(existingPrompt, cameraMotion, Intensity, Speed);

    /// <summary>✨ 작업실로 전송 — 활성 프레임 → I2V 참조 버퍼, 모션·강도·속도 → 작업실 1회 페이로드. 프레임·모션 둘 다 없으면 false.</summary>
    public bool TransferToWorkspace(string? cameraMotion)
    {
        var motion = cameraMotion?.Trim() ?? "";
        var framed = SelectedFrame is { } f && _camera.QueueI2vReference(f);
        if (!framed && motion.Length == 0)
        {
            StatusMessage = "먼저 프레임을 캡처하거나 카메라 모션을 선택하세요.";
            Raise();
            return false;
        }
        _camera.QueueWorkspaceTransfer(new CameraWorkspaceTransfer(motion, Intensity, Speed));
        StatusMessage = framed ? "카메라 프레임 및 모션을 작업실로 전송했습니다." : "카메라 모션을 작업실로 전송했습니다.";
        Raise();
        return true;
    }

    public void RefreshTelemetry()
    {
        var t = _camera.GetTelemetry();
        CurrentFps = t.Fps;
        LatencyMs = t.LatencyMs;
        BitrateKbps = t.BitrateKbps;
        ResolutionDisplay = string.IsNullOrEmpty(t.Resolution) ? "—" : t.Resolution;
        Changed?.Invoke();
    }

    private void Raise()
    {
        Interlocked.Increment(ref _stateVersion);
        Changed?.Invoke();
    }
}
