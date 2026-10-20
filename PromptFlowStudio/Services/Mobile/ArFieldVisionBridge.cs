using System;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID
using Android.Content;
using Android.Views;
using Android.Widget;
using AndroidX.Camera.Core;
using AndroidX.Camera.Lifecycle;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Lifecycle;
#endif
#if IOS
using AVFoundation;
using CoreAnimation;
using Foundation;
using UIKit;
using WebKit;
#endif

namespace PromptFlowStudio.Services.Mobile;

/// <summary>AR 세션 시작 옵션 — 후면 카메라 여부 · CAD 분해도 오버레이 · 텔레메트리 HUD 표출.</summary>
public sealed record ArStartOptions(bool RearCamera = true, bool ShowCadOverlay = true, bool ShowTelemetryHud = true);

/// <summary>AR 세션 상태 — 활성 여부 · 카메라 On · 사유.</summary>
public sealed record ArSessionState(bool Active, bool CameraOn, string Message)
{
    /// <summary>비활성 표준값.</summary>
    public static readonly ArSessionState Inactive = new(false, false, "AR 세션 비활성");
}

/// <summary>
/// [PromptFlowStudio] v5.0 Task1 · AR 공간 텔레메트리 뷰어 플랫폼 시임 — 무거운 AR SDK(Unity/Vuforia) 배제, <b>Light-AR</b> 구조.
///   ▸ 네이티브 <b>카메라 패스스루</b>(Android CameraX / iOS AVFoundation)를 BlazorWebView <b>뒤</b>에 깔고, WebView 를 <c>transparent</c> 로 만들어
///     그 위에 CSS 3D CAD 분해도 + Modbus 텔레메트리 HUD(Blazor 레이어)를 투명 오버레이 → 증강현실 융합.
///   ▸ 데스크톱(net8.0-windows)은 <see cref="DesktopArFieldVision"/>(<c>IsSupported=false</c>)로 Graceful(시뮬레이터 배경). 모바일 MAUI 헤드가 네이티브 구현으로 대체.
/// </summary>
public interface IArFieldVisionBridge : IDisposable
{
    /// <summary>카메라 패스스루 지원 여부(모바일에서만 true).</summary>
    bool IsSupported { get; }
    /// <summary>현재 AR 세션 상태.</summary>
    ArSessionState State { get; }
    /// <summary>세션 상태 변경 방송(UI 오버레이 토글).</summary>
    event Action<ArSessionState>? OnStateChanged;
    /// <summary>카메라 패스스루를 시작하고 WebView 를 투명화한다(네이티브). 데스크톱은 시뮬레이터 안내.</summary>
    Task<ArSessionState> StartAsync(ArStartOptions options, CancellationToken ct = default);
    /// <summary>카메라 세션을 정지하고 WebView 불투명 복원 + 리소스 해제(멱등).</summary>
    Task StopAsync();
}

/// <summary>
/// 데스크톱 폴백 — 카메라 패스스루 미지원(<c>IsSupported=false</c>). AR 켜기 시도 시 시뮬레이터 배경 안내로 Graceful Degradation.
/// </summary>
public sealed class DesktopArFieldVision : IArFieldVisionBridge
{
    private bool _disposed;

    public bool IsSupported => false;
    public ArSessionState State { get; private set; } = ArSessionState.Inactive;
    public event Action<ArSessionState>? OnStateChanged;

    public Task<ArSessionState> StartAsync(ArStartOptions options, CancellationToken ct = default)
    {
        State = new ArSessionState(true, false, "🖥️ 데스크톱: 실카메라 패스스루는 모바일 전용 — 시뮬레이터 배경으로 오버레이를 미리봅니다.");
        OnStateChanged?.Invoke(State);
        return Task.FromResult(State);
    }

    public Task StopAsync()
    {
        State = ArSessionState.Inactive;
        OnStateChanged?.Invoke(State);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;   // 멱등 해제
        _disposed = true;
        OnStateChanged = null;
    }
}

#if ANDROID
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI Android 헤드 전용] — net8.0-windows 빌드에서는 #if ANDROID 로 제외된다.
//  CameraX(AndroidX.Camera) PreviewView 를 BlazorWebView 뒤에 배치하고 WebView 를 투명화.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Android CameraX 패스스루 브릿지 — PreviewView 를 WebView 뒤에 두고 background transparent 로 융합.</summary>
public sealed class AndroidArFieldVision : IArFieldVisionBridge
{
    private readonly Context _ctx;
    private readonly Func<Android.Webkit.WebView?> _webViewAccessor;   // 호스트가 현재 BlazorWebView 를 공급
    private readonly ILifecycleOwner _lifecycleOwner;
    private readonly IMobileVisionAiEngine? _vision;                   // v5.2.1: 있으면 ImageAnalysis 로 프레임 결선
    private readonly FrameSkipGate _skip = new(100);                   // ≈10fps 분석(CPU 스파이크 방지)
    private PreviewView? _previewView;
    private ImageAnalysis? _imageAnalysis;
    private IProcessCameraProvider? _cameraProvider;
    private bool _disposed;

    public bool IsSupported => true;
    public ArSessionState State { get; private set; } = ArSessionState.Inactive;
    public event Action<ArSessionState>? OnStateChanged;

    public AndroidArFieldVision(Context ctx, ILifecycleOwner lifecycleOwner, Func<Android.Webkit.WebView?> webViewAccessor, IMobileVisionAiEngine? vision = null)
    {
        _ctx = ctx; _lifecycleOwner = lifecycleOwner; _webViewAccessor = webViewAccessor; _vision = vision;
    }

    public Task<ArSessionState> StartAsync(ArStartOptions options, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<ArSessionState>();
        var future = ProcessCameraProvider.GetInstance(_ctx);
        future.AddListener(new Java.Lang.Runnable(() =>
        {
            try
            {
                _cameraProvider = (IProcessCameraProvider)future.Get()!;
                _previewView ??= new PreviewView(_ctx);

                // WebView 를 최상단으로 올리고 투명화 → 그 아래 카메라 프리뷰가 비친다(Light-AR 융합).
                var web = _webViewAccessor();
                if (web is not null)
                {
                    web.SetBackgroundColor(Android.Graphics.Color.Transparent);   // ★ background: transparent
                    (web.Parent as ViewGroup)?.AddView(_previewView, 0);          // 카메라를 맨 뒤(index 0)로
                    web.BringToFront();
                }

                var preview = new Preview.Builder().Build();
                preview.SetSurfaceProvider(_previewView.SurfaceProvider);
                var selector = options.RearCamera ? CameraSelector.DefaultBackCamera : CameraSelector.DefaultFrontCamera;
                _cameraProvider.UnbindAll();

                // v5.2.1: 비전 엔진이 주입돼 있으면 ImageAnalysis(최신 프레임만 · 백프레셔) 로 프레임 피드 결선.
                if (_vision is not null)
                {
                    _imageAnalysis = new ImageAnalysis.Builder()
                        .SetBackpressureStrategy(ImageAnalysis.StrategyKeepOnlyLatest)
                        .Build();
                    _imageAnalysis.SetAnalyzer(ContextCompat.GetMainExecutor(_ctx), new ArVisionAnalyzer(_vision, _skip));
                    _cameraProvider.BindToLifecycle(_lifecycleOwner, selector, preview, _imageAnalysis);
                }
                else
                {
                    _cameraProvider.BindToLifecycle(_lifecycleOwner, selector, preview);
                }

                State = new ArSessionState(true, true, "AR 카메라 패스스루 활성");
                OnStateChanged?.Invoke(State);
                tcs.TrySetResult(State);
            }
            catch (Exception ex)
            {
                State = new ArSessionState(false, false, "카메라 시작 실패: " + ex.Message);
                OnStateChanged?.Invoke(State);
                tcs.TrySetResult(State);
            }
        }), ContextCompat.GetMainExecutor(_ctx));
        return tcs.Task;
    }

    public Task StopAsync()
    {
        try
        {
            _cameraProvider?.UnbindAll();
            var web = _webViewAccessor();
            if (web is not null && _previewView is not null) (web.Parent as ViewGroup)?.RemoveView(_previewView);
        }
        catch { /* 무시 */ }
        State = ArSessionState.Inactive;
        OnStateChanged?.Invoke(State);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _cameraProvider?.UnbindAll(); } catch { }
        _previewView?.Dispose();
        OnStateChanged = null;
    }
}
#endif

#if IOS
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI iOS 헤드 전용] — AVFoundation AVCaptureVideoPreviewLayer 를 WKWebView 뒤에 두고 Opaque=false.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>iOS AVFoundation 패스스루 브릿지 — 카메라 프리뷰 레이어 위에 투명 WKWebView 를 얹어 융합.</summary>
public sealed class IosArFieldVision : IArFieldVisionBridge
{
    private readonly Func<WKWebView?> _webViewAccessor;
    private AVCaptureSession? _session;
    private AVCaptureVideoPreviewLayer? _previewLayer;
    private bool _disposed;

    public bool IsSupported => true;
    public ArSessionState State { get; private set; } = ArSessionState.Inactive;
    public event Action<ArSessionState>? OnStateChanged;

    public IosArFieldVision(Func<WKWebView?> webViewAccessor) => _webViewAccessor = webViewAccessor;

    public Task<ArSessionState> StartAsync(ArStartOptions options, CancellationToken ct = default)
    {
        try
        {
            _session = new AVCaptureSession { SessionPreset = AVCaptureSession.PresetHigh };
            var pos = options.RearCamera ? AVCaptureDevicePosition.Back : AVCaptureDevicePosition.Front;
            var device = AVCaptureDeviceDiscoverySession.Create(
                new[] { AVCaptureDeviceType.BuiltInWideAngleCamera }, AVMediaTypes.Video, pos).Devices.FirstOrDefault()
                ?? AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
            if (device is null) return Task.FromResult(new ArSessionState(false, false, "카메라 장치를 찾을 수 없습니다."));

            var input = AVCaptureDeviceInput.FromDevice(device, out var err);
            if (err is not null || input is null) return Task.FromResult(new ArSessionState(false, false, "카메라 입력 실패: " + err?.LocalizedDescription));
            _session.AddInput(input);

            var web = _webViewAccessor();
            if (web is not null)
            {
                web.Opaque = false;                                  // ★ WebView 투명
                web.BackgroundColor = UIColor.Clear;
                web.ScrollView.BackgroundColor = UIColor.Clear;
                _previewLayer = AVCaptureVideoPreviewLayer.FromSession(_session);
                _previewLayer.VideoGravity = AVLayerVideoGravity.ResizeAspectFill;
                _previewLayer.Frame = web.Bounds;
                web.Superview?.Layer.InsertSublayerBelow(_previewLayer, web.Layer);   // 카메라를 WebView 뒤로
            }
            _session.StartRunning();
            State = new ArSessionState(true, true, "AR 카메라 패스스루 활성");
            OnStateChanged?.Invoke(State);
            return Task.FromResult(State);
        }
        catch (Exception ex)
        {
            State = new ArSessionState(false, false, "카메라 시작 실패: " + ex.Message);
            OnStateChanged?.Invoke(State);
            return Task.FromResult(State);
        }
    }

    public Task StopAsync()
    {
        try { _session?.StopRunning(); _previewLayer?.RemoveFromSuperLayer(); } catch { }
        State = ArSessionState.Inactive;
        OnStateChanged?.Invoke(State);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _session?.StopRunning(); } catch { }
        _previewLayer?.Dispose(); _session?.Dispose();
        OnStateChanged = null;
    }
}
#endif
