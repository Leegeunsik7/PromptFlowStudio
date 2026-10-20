using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.SpatialXr;

/// <summary>
/// [PromptFlow Studio v16.97.0] 🥽 AI 공간 컴퓨팅 &amp; WebXR 스튜디오 러너.
/// 전용 스레드 = 디바이스 주사율 틱: 포즈 폴링(시뮬레이션 머리 운동 또는 브라우저 WebXR 세션 포즈) → 양안 뷰 예측 갱신 → 손 제스처/햅틱 →
/// 스트림 fps마다 원격 렌더(네트워크 지연만큼 앞선 예측 포즈 · 안구 레이캐스터) → 시선 포비에이티드 부호화/복호 → 매 틱 최신 포즈로 재투영(ATW/PTW).
/// 시뮬레이션 디바이스 연결 시 무작위 시선 단속(saccade) · 오른손 핀치-드래그 · 왼손 포인트 데모.
/// </summary>
public sealed class SpatialComputingStudioRunner : IDisposable
{
    public const int EyeW = 112, EyeH = 112;

    private readonly object _gate = new();
    private readonly Vector3[] _hand = new Vector3[SpatialHandGestureHapticGuard.Joints];
    private Thread? _loop;
    private volatile bool _running;
    private int _device = -1;
    private long _tick, _frames, _externalPoses, _startUs, _lastStreamUs, _lastExternalUs;
    private Vector3 _gazeTarget = new(0, 0, -1), _gaze = new(0, 0, -1);
    private long _nextSaccadeUs;
    private readonly Random _rng = new(1697);

    public SpatialComputingStudioRunner(IServiceProvider sp)
        : this((OpenXrWebXrSpatialTrackingEngine?)sp.GetService(typeof(OpenXrWebXrSpatialTrackingEngine)) ?? new OpenXrWebXrSpatialTrackingEngine(),
               sp.GetService(typeof(FoveatedCompressionReprojectionSynthesizer)) as FoveatedCompressionReprojectionSynthesizer,
               (SpatialHandGestureHapticGuard?)sp.GetService(typeof(SpatialHandGestureHapticGuard)) ?? new SpatialHandGestureHapticGuard()) { }

    public SpatialComputingStudioRunner(OpenXrWebXrSpatialTrackingEngine tracking, FoveatedCompressionReprojectionSynthesizer? synth, SpatialHandGestureHapticGuard hands)
    {
        Tracking = tracking;
        Synth = synth ?? new FoveatedCompressionReprojectionSynthesizer(EyeW, EyeH);
        Hands = hands;
        if (Hands.PropCount == 0) SeedProps();
    }

    public event Action? OnChange;

    public OpenXrWebXrSpatialTrackingEngine Tracking { get; }
    public FoveatedCompressionReprojectionSynthesizer Synth { get; }
    public SpatialHandGestureHapticGuard Hands { get; }
    public bool IsRunning => _running;
    public int Device => Volatile.Read(ref _device);
    public bool Connected => Device >= 0;
    public bool ExternalSession => Device == XrDeviceProfile.All.Length - 1;
    public double NetworkLatencyMs { get; set; } = 25;
    public bool SimulateHands { get; set; } = true;
    public float MotionIntensity { get; set; } = 1f;
    public long Ticks => Interlocked.Read(ref _tick);
    public long StreamFrames => Interlocked.Read(ref _frames);
    public long ExternalPoses => Interlocked.Read(ref _externalPoses);
    public double TickUs { get; private set; }
    public double RenderUs { get; private set; }
    public double FrameAgeMs { get; private set; }
    /// <summary>모션-투-포톤(재투영 경로) = 최신 포즈 표본 → 재투영 완료.</summary>
    public double MotionToPhotonMs { get; private set; }
    /// <summary>재투영 없을 때의 모션-투-포톤 = 네트워크 지연 + 프레임 나이.</summary>
    public double MotionToPhotonRawMs { get; private set; }
    public Vector3 GazeDir => _gaze;
    public string? LastAction { get; private set; }

    /// <summary>기본 소품(OpenUSD/3DGS 미결선 시) — 눈앞 1~2 m 구 5개.</summary>
    public void SeedProps()
    {
        Hands.ClearProps();
        Hands.AddProp("큐브 소품", new Vector3(0.18f, 1.45f, -0.45f), 0.06f);
        Hands.AddProp("구 소품", new Vector3(-0.25f, 1.55f, -0.9f), 0.12f);
        Hands.AddProp("램프", new Vector3(0.55f, 1.3f, -1.4f), 0.18f);
        Hands.AddProp("화분", new Vector3(-0.7f, 0.5f, -1.6f), 0.3f);
        Hands.AddProp("조각상", new Vector3(0.1f, 1.0f, -2.6f), 0.45f);
    }

    /// <summary>디바이스 연결(시뮬레이션 0..2 · 브라우저 WebXR = 마지막) / -1 = 해제.</summary>
    public void Connect(int device)
    {
        lock (_gate)
        {
            if (device >= 0)
            {
                Tracking.Profile = XrDeviceProfile.All[device];
                Tracking.Reset();
            }
            Volatile.Write(ref _device, device);
            LastAction = device >= 0 ? "🔗 " + XrDeviceProfile.All[device].Name + " 연결" : "⛔ 디바이스 해제";
            if (device < 0) { Hands.LoseHand(0); Hands.LoseHand(1); }
        }
        Raise();
    }

    /// <summary>브라우저 WebXR 세션 포즈(JS 브리지) — 타임스탬프는 로컬 단조 시계로 재기준.</summary>
    public void SubmitExternalPose(Vector3 p, Quaternion q, float ipdMm)
    {
        if (!ExternalSession) return;
        if (ipdMm > 40 && ipdMm < 80) Tracking.IpdMm = ipdMm;
        var now = OpenXrWebXrSpatialTrackingEngine.NowUs;
        if (Tracking.SubmitPose(now, p, q)) { Interlocked.Increment(ref _externalPoses); _lastExternalUs = now; }
    }

    /// <summary>시뮬레이션 머리 포즈(서 있는 사용자 · 리사주 요/피치 + 흔들림).</summary>
    public Vector3 SimHead(double t, out Quaternion q)
    {
        var k = MotionIntensity;
        var yaw = (float)(0.55 * Math.Sin(t * 0.7) + 0.12 * Math.Sin(t * 2.3)) * k;
        var pitch = (float)(0.18 * Math.Sin(t * 0.9 + 1.1)) * k;
        var roll = (float)(0.05 * Math.Sin(t * 1.3)) * k;
        q = Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        return new Vector3((float)(0.08 * Math.Sin(t * 0.5)) * k, 1.6f + (float)(0.02 * Math.Sin(t * 1.7)) * k, (float)(0.05 * Math.Sin(t * 0.4)) * k);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            if (_device < 0) Connect(0);
            _running = true;
            _startUs = OpenXrWebXrSpatialTrackingEngine.NowUs;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.XR.Compositor", Priority = ThreadPriority.AboveNormal };
            _loop.Start();
            LastAction = "▶ XR 컴포지터 가동 · " + Tracking.Profile.Name + " " + Tracking.Profile.RefreshHz + " Hz";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(2000);
        LastAction = "⏸ XR 컴포지터 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { TickOnce(OpenXrWebXrSpatialTrackingEngine.NowUs); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Xr.Tick", ex); }
            n++;
            var hz = Math.Max(30f, Tracking.Profile.RefreshHz);
            var due = n * 1000.0 / hz;
            var wait = due - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            if (n % 8 == 0) Raise();
        }
    }

    /// <summary>1 디스플레이 틱(무할당): 폴링 → 뷰 예측 → 손 → (스트림 프레임) → 재투영.</summary>
    public void TickOnce(long nowUs)
    {
        var t0 = Stopwatch.GetTimestamp();
        var dev = Device;
        if (dev < 0) return;
        if (_startUs == 0) _startUs = nowUs;
        var tSec = (nowUs - _startUs) * 1e-6;
        if (!ExternalSession)
        {
            var p = SimHead(tSec, out var q);
            Tracking.SubmitPose(nowUs, p, q);
        }
        var frameUs = (long)(1e6 / Math.Max(30f, Tracking.Profile.RefreshHz));
        Tracking.UpdateViews(nowUs + frameUs);
        UpdateGaze(nowUs);
        if (SimulateHands && Tracking.Profile.HandTracking) SimHands(tSec, nowUs);
        Hands.Update(nowUs);
        if (_lastStreamUs == 0 || nowUs - _lastStreamUs >= (long)(1e6 / Math.Max(1, Synth.StreamFps))) StreamFrame(nowUs);
        for (var e = 0; e < 2; e++) Synth.Reproject(e, Tracking.Eye(e), Synth.Mode);
        var sinceSample = Math.Max(0, nowUs - Tracking.LatestTimeUs) * 1e-3;
        var tickMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        FrameAgeMs = (nowUs - _lastStreamUs) * 1e-3;
        MotionToPhotonMs = Synth.Mode == ReprojectionMode.None ? NetworkLatencyMs + FrameAgeMs + tickMs : sinceSample + tickMs + frameUs * 1e-3;
        MotionToPhotonRawMs = NetworkLatencyMs + FrameAgeMs + frameUs * 1e-3;
        TickUs = tickMs * 1000;
        Interlocked.Increment(ref _tick);
    }

    /// <summary>원격 렌더 1프레임: 네트워크 지연만큼 앞선 예측 포즈로 양안 렌더 → 포비에이티드 부호화 → 예산 적응.</summary>
    public void StreamFrame(long nowUs)
    {
        var t0 = Stopwatch.GetTimestamp();
        Tracking.Predict(nowUs + (long)(NetworkLatencyMs * 1000), out var p, out var q);
        Span<XrEyeView> eyes = stackalloc XrEyeView[2];
        OpenXrWebXrSpatialTrackingEngine.ComputeEyes(p, q, Tracking.IpdMm, Tracking.Profile, Tracking.Near, Tracking.Far, eyes);
        for (var e = 0; e < 2; e++)
        {
            Synth.SetRenderEye(e, eyes[e]);
            SpatialSceneRaycaster.Render(eyes[e], Synth.Width, Synth.Height, Hands, Synth.Source(e), Synth.Distance(e));
            Synth.Encode(e, _gaze);
        }
        Synth.AdaptToBudget();
        RenderUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        _lastStreamUs = nowUs;
        Interlocked.Increment(ref _frames);
    }

    /// <summary>시선: 시선 추적 디바이스 = 소품/무작위 단속(250~900 ms) + 부드러운 추종 · 미지원 = 고정 포비에이션(정면).</summary>
    private void UpdateGaze(long nowUs)
    {
        if (!Tracking.Profile.EyeTracking) { _gaze = new Vector3(0, 0, -1); return; }
        if (nowUs >= _nextSaccadeUs)
        {
            _nextSaccadeUs = nowUs + 250_000 + _rng.Next(650_000);
            var n = Hands.PropCount;
            if (n > 0 && _rng.Next(3) != 0)
            {
                var c = Hands.PropCenter(_rng.Next(n));
                var local = Vector3.Transform(c - Tracking.HeadPosition, Quaternion.Conjugate(Tracking.HeadOrientation));
                _gazeTarget = local.Z < -0.05f ? Vector3.Normalize(local) : new Vector3(0, 0, -1);
            }
            else _gazeTarget = Vector3.Normalize(new Vector3((float)(_rng.NextDouble() - 0.5) * 0.8f, (float)(_rng.NextDouble() - 0.5) * 0.6f, -1f));
        }
        _gaze = Vector3.Normalize(Vector3.Lerp(_gaze, _gazeTarget, 0.35f));
    }

    /// <summary>시뮬레이션 손: 오른손 = 4 s 주기 접근→핀치→원 드래그→놓기 · 왼손 = 포인트.</summary>
    private void SimHands(double t, long nowUs)
    {
        var head = Tracking.HeadPosition;
        var phase = t % 4.0;
        var target = Hands.PropCenter(0);
        var home = head + new Vector3(0.22f, -0.3f, -0.35f);
        Vector3 palm; float pinch;
        if (phase < 1.0) { palm = Vector3.Lerp(home, target + new Vector3(0, 0.02f, 0.06f), (float)phase); pinch = 0; }
        else if (phase < 3.0)
        {
            var a = (phase - 1.0) * Math.PI;
            palm = target + new Vector3(0, 0.02f, 0.06f) + new Vector3((float)Math.Cos(a) * 0.05f - 0.05f, (float)Math.Sin(a) * 0.04f, 0);
            pinch = 1;
        }
        else { palm = Vector3.Lerp(Hands.Joint(1, XrHandJoint.Palm), home, (float)(phase - 3.0)); pinch = 0; }
        var rq = Quaternion.CreateFromYawPitchRoll(0, -0.6f, 0);
        SpatialHandGestureHapticGuard.PoseHand(_hand, 1, palm, rq, pinch > 0 ? 0.35f : 0.1f, 0.15f, pinch);
        Hands.SubmitHand(1, _hand, 0.95f, nowUs);
        var lp = head + new Vector3(-0.25f, -0.32f, -0.3f) + new Vector3(0, (float)(0.03 * Math.Sin(t * 1.5)), 0);
        SpatialHandGestureHapticGuard.PoseHand(_hand, 0, lp, Quaternion.CreateFromYawPitchRoll(0.4f * (float)Math.Sin(t * 0.6), -0.2f, 0), 0f, 1f, 0f);
        Hands.SubmitHand(0, _hand, 0.95f, nowUs);
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Xr.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
