using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;
namespace PromptFlowStudio.Services.VerticalTranscode;

public enum TranscodeSessionState : byte { Idle, Recording, Ready, Handed, Failed }

/// <summary>
/// [PromptFlow Studio v17.49.0] 🎞️ AI 9:16 하드웨어 트랜스코더 스튜디오 러너(전용 스레드 · 프레임 단위 오프라인 타임라인).
/// 녹화 중 매 틱: 직결 합성기 Render(v17.46/43/47 → NV12 · v17.32/38 스템 → 스테레오) → 트랜스코더 EncodeFrame(가드 QP/비트레이트) →
/// 목표 길이 도달 시 Finish(4 MB 세그먼트 fMP4) → 자동 연동 시 v17.48 Graph API 브릿지 PublishEncoded 로 소유권 이전.
/// 타임스탬프는 프레임 인덱스 기반이라 인코딩이 실시간보다 느려도 결과 길이는 정확하다. 로컬 웹훅 리스너는 v17.48 가드(앱 시크릿)로 서명을 검증해 이벤트를 적재한다.
/// </summary>
public sealed class VerticalTranscoderStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300, PreviewW = 135, PreviewH = 240, PreviewBytes = 54 + PreviewW * PreviewH * 3;
    private readonly object _gate = new();
    private readonly float[] _kbpsS = new float[Hist], _encS = new float[Hist], _occS = new float[Hist], _renderS = new float[Hist];
    private readonly ViralReels.ViralReelsStudioRunner? _reels;
    private readonly SetExtension.VirtualSetExtensionStudioRunner? _set;
    private readonly LensOptics.CinematicLensOpticsStudioRunner? _lens;
    private readonly GraphBridge.GraphApiBridgeStudioRunner? _bridge;
    private StudioDirectPipelineFrameSynthesizer _synth;
    private Stream? _ready;
    private int _head, _series, _targetFrames;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public VerticalTranscoderStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(ViralReels.ViralReelsStudioRunner)) as ViralReels.ViralReelsStudioRunner,
        sp.GetService(typeof(SetExtension.VirtualSetExtensionStudioRunner)) as SetExtension.VirtualSetExtensionStudioRunner,
        sp.GetService(typeof(LensOptics.CinematicLensOpticsStudioRunner)) as LensOptics.CinematicLensOpticsStudioRunner,
        sp.GetService(typeof(GraphBridge.GraphApiBridgeStudioRunner)) as GraphBridge.GraphApiBridgeStudioRunner) { }

    public VerticalTranscoderStudioRunner(ViralReels.ViralReelsStudioRunner? reels = null, SetExtension.VirtualSetExtensionStudioRunner? set = null, LensOptics.CinematicLensOpticsStudioRunner? lens = null, GraphBridge.GraphApiBridgeStudioRunner? bridge = null)
    {
        _reels = reels; _set = set; _lens = lens; _bridge = bridge;
        _synth = new StudioDirectPipelineFrameSynthesizer(Width, Height, Fps, reels, set, lens);
        Listener = new LocalWebhookListener(bridge is null ? null : bridge.InjectWebhook);
        VerifyToken = "pfv-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        Listener.SetVerifyToken(VerifyToken);
    }

    public event Action? OnChange;
    public RealtimeHardwareVerticalTranscoderEngine Engine { get; } = new();
    public StudioDirectPipelineFrameSynthesizer Synth => _synth;
    /// <summary>v17.52 모션 타이포그래피/오버레이 — 설정 시 녹화 프레임마다 인코딩 직전 합성.</summary>
    public IVideoFrameOverlay? Overlay { get; set; }
    public LocalWebhookListener Listener { get; }
    /// <summary>Meta 앱 대시보드 웹훅 "확인 토큰" 에 입력할 값(실행마다 무작위 생성 · UI 에서 교체 가능).</summary>
    public string VerifyToken { get; private set; }
    public LocalHttpsWebhookBitrateBudgetGuard Guard => Engine.Guard;
    public int Width { get; private set; } = 1080;
    public int Height { get; private set; } = 1920;
    public int Fps { get; private set; } = 30;
    public int TargetKbps { get; private set; } = 6000;
    public double DurationSec { get; private set; } = 6;
    public bool PreferHardware { get; private set; } = true;
    public bool AllowMediaFoundation { get; private set; } = true;
    public bool AutoHandoff { get; private set; }
    public TranscodeSessionState State { get; private set; }
    public long ReadyBytes { get; private set; }
    public double Progress => _targetFrames > 0 ? (double)Engine.Frames / _targetFrames : 0;
    public bool BridgeLinked => _bridge is not null;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float KbpsSeries(int i) => _kbpsS[Ix(i)];
    public float EncodeMsSeries(int i) => _encS[Ix(i)];
    public float OccupancySeries(int i) => _occS[Ix(i)];
    public float RenderMsSeries(int i) => _renderS[Ix(i)];
    private int Ix(int i) => (_head - 1 - i + 2 * Hist) % Hist;
    public static string StateName(TranscodeSessionState s) => s switch
    {
        TranscodeSessionState.Recording => "인코딩 중",
        TranscodeSessionState.Ready => "MP4 준비됨",
        TranscodeSessionState.Handed => "v17.48 발행 이관",
        TranscodeSessionState.Failed => "실패",
        _ => "대기",
    };

    // ══════════════════════ 제어(할당 허용) ══════════════════════
    public bool Configure(int width, int height, int fps, int kbps, double seconds, bool preferHw, bool allowMf)
    {
        lock (_gate)
        {
            if (State == TranscodeSessionState.Recording) { LastAction = "⚠ 인코딩 중 — 설정 변경 불가"; return false; }
            if (width != Width || height != Height || fps != Fps) _synth = new StudioDirectPipelineFrameSynthesizer(width, height, fps, _reels, _set, _lens);
            Width = width; Height = height; Fps = fps;
            TargetKbps = Math.Clamp(kbps, 500, LocalHttpsWebhookBitrateBudgetGuard.CapKbps);
            DurationSec = Math.Clamp(seconds, 3, 90);
            PreferHardware = preferHw; AllowMediaFoundation = allowMf;
        }
        LastAction = "⚙ " + width.ToString(CultureInfo.InvariantCulture) + "×" + height.ToString(CultureInfo.InvariantCulture) + " · " + fps.ToString(CultureInfo.InvariantCulture) + " fps · " + TargetKbps.ToString(CultureInfo.InvariantCulture) + " kbps";
        Raise();
        return true;
    }

    public void SetTargetKbps(int kbps)
    {
        lock (_gate) { TargetKbps = Math.Clamp(kbps, 500, LocalHttpsWebhookBitrateBudgetGuard.CapKbps); Guard.TargetKbps = TargetKbps; }
        Raise();
    }

    public void SetAutoHandoff(bool on) { lock (_gate) AutoHandoff = on; LastAction = on ? "🔗 완료 시 v17.48 자동 발행" : "🔗 수동 발행"; Raise(); }

    public bool BeginRecording()
    {
        lock (_gate)
        {
            if (State == TranscodeSessionState.Recording) return false;
            _ready?.Dispose(); _ready = null; ReadyBytes = 0;
            var ok = Engine.Open(new SegmentedChunkStream(), Width, Height, Fps, TargetKbps, PreferHardware, AllowMediaFoundation);
            State = ok ? TranscodeSessionState.Recording : TranscodeSessionState.Failed;
            _targetFrames = (int)Math.Round(DurationSec * Fps);
        }
        LastAction = State == TranscodeSessionState.Recording ? "⏺ 인코딩 시작 · " + RealtimeHardwareVerticalTranscoderEngine.BackendName(Engine.Backend) + (Engine.FallbackReason is { } r ? " (" + r + ")" : "") : "⚠ 인코더 열기 실패";
        Raise();
        return State == TranscodeSessionState.Recording;
    }

    public void CancelRecording()
    {
        lock (_gate)
        {
            if (State != TranscodeSessionState.Recording) return;
            var s = Engine.Finish(); s?.Dispose();
            State = TranscodeSessionState.Idle;
        }
        LastAction = "⏹ 인코딩 취소";
        Raise();
    }

    /// <summary>준비된 MP4 를 v17.48 브릿지로 이관(소유권 이전).</summary>
    public bool Handoff(string? caption = null)
    {
        Stream? s;
        lock (_gate) { if (State != TranscodeSessionState.Ready || _ready is null || _bridge is null) return false; s = _ready; _ready = null; }
        var ok = _bridge.PublishEncoded(s, caption ?? _reels?.BuildCaption() ?? "PromptFlow Studio 9:16 #reels");
        lock (_gate) State = ok ? TranscodeSessionState.Handed : TranscodeSessionState.Failed;
        LastAction = ok ? "🚀 v17.48 발행 이관 · " + (ReadyBytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : "⚠ 이관 실패: " + _bridge.LastAction;
        Raise();
        return ok;
    }

    /// <summary>준비된 MP4 를 파일로 저장(UI · 할당 허용).</summary>
    public bool SaveTo(string path)
    {
        lock (_gate)
        {
            if (_ready is null) return false;
            try
            {
                using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                _ready.Position = 0; _ready.CopyTo(f); _ready.Position = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { LastAction = "⚠ 저장 실패"; return false; }
        }
        LastAction = "💾 저장 · " + Path.GetFileName(path);
        Raise();
        return true;
    }

    /// <summary>준비된 MP4 스트림 탈취(테스트/외부 소비 · 소유권 이전).</summary>
    public Stream? TakeReady() { lock (_gate) { var s = _ready; _ready = null; return s; } }

    public bool StartWebhook(int port, bool tls)
    {
        var ok = Listener.Start(port, tls);
        LastAction = ok ? "📡 웹훅 리스너 " + (tls ? "https" : "http") + "://127.0.0.1:" + Listener.Port.ToString(CultureInfo.InvariantCulture) + Listener.Path : "⚠ 리스너 시작 실패: " + Listener.LastError;
        Raise();
        return ok;
    }

    public void SetVerifyToken(string token)
    {
        var t = token.Trim();
        if (t.Length is < 8 or > 64) { LastAction = "⚠ 확인 토큰 8~64 자"; Raise(); return; }
        VerifyToken = t; Listener.SetVerifyToken(t);
        LastAction = "🔑 웹훅 확인 토큰 변경";
        Raise();
    }

    public void StopWebhook() { Listener.Stop(); LastAction = "📡 리스너 정지"; Raise(); }

    /// <summary>UI 프리뷰 — 1/8 해상도 BMP(중간 합성 RGB 를 2×2 평균).</summary>
    public void WritePreviewBmp(Span<byte> dst)
    {
        dst[..54].Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[2..], PreviewBytes);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[10..], 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[14..], 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[18..], PreviewW);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst[22..], PreviewH);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[26..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(dst[28..], 24);
        lock (_gate)
        {
            var s = _synth; var r = s.PreviewR; var g = s.PreviewG; var b = s.PreviewB; var iw = s.IW; var ih = s.IH;
            for (var y = 0; y < PreviewH; y++)
            {
                var d = 54 + y * PreviewW * 3;
                var sy = Math.Min((PreviewH - 1 - y) * ih / PreviewH, ih - 1);
                for (var x = 0; x < PreviewW; x++)
                {
                    var i = sy * iw + Math.Min(x * iw / PreviewW, iw - 1);
                    dst[d + x * 3] = To8(b[i]); dst[d + x * 3 + 1] = To8(g[i]); dst[d + x * 3 + 2] = To8(r[i]);
                }
            }
        }
    }

    private static byte To8(float v) => (byte)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);

    // ══════════════════════ 틱(무할당 · 완료 틱 제외) ══════════════════════
    public void Step()
    {
        var finished = false;
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("VerticalTranscode.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _synth.Render();
            if (State == TranscodeSessionState.Recording)
            {
                if (Overlay is { } ov) _synth.ApplyOverlay(ov, Engine.Frames);
                Engine.EncodeFrame(_synth.Y, _synth.UV, _synth.Left, _synth.Right);
                if (Engine.Frames >= _targetFrames) finished = true;
            }
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _kbpsS[_head] = (float)Guard.WindowKbps; _encS[_head] = (float)Engine.LastEncodeMs; _occS[_head] = (float)Guard.Occupancy; _renderS[_head] = (float)_synth.LastRenderMs;
            _head = (_head + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = ms;
        }
        if (finished) Complete();
    }

    private void Complete()
    {
        lock (_gate)
        {
            _ready = Engine.Finish();
            ReadyBytes = _ready?.Length ?? 0;
            State = _ready is not null ? TranscodeSessionState.Ready : TranscodeSessionState.Failed;
        }
        LastAction = "✅ MP4 완료 · " + (ReadyBytes / 1048576.0).ToString("0.00", CultureInfo.InvariantCulture) + " MB · " + Engine.EncodedFrames.ToString(CultureInfo.InvariantCulture) + " 프레임";
        Raise();
        if (AutoHandoff) Handoff();
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.VerticalTranscode" };
            _loop.Start();
        }
        LastAction = "▶ 트랜스코더 루프";
        Raise();
    }
    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(5000);
        LastAction = "⏸ 정지";
        Raise();
    }
    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        var hz = Fps;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException) { PromptFlowStudio.Program.Log("VerticalTranscode.Step", ex); }
            n++;
            var wait = n * 1000.0 / hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("VerticalTranscode.Runner.OnChange", ex); }
    }
    public void Dispose()
    {
        Stop();
        lock (_gate) { Engine.Close(); _ready?.Dispose(); _ready = null; }
        Listener.Dispose();
    }
}
