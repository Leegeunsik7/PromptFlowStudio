using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.VisualQa;

/// <summary>
/// [PromptFlow Studio v17.34.0] 👁️ AI 헤드리스 시각 QA &amp; 디버깅 스튜디오 러너(60 Hz 전용 스레드 · GUI 창 불필요).
/// 매 프레임 전용 v17.33 모션 액터(계단 보행 · ZMP 가드) → 오프스크린 프레임버퍼(UI 레이어 + 3D 뷰포트 + ZMP 오버레이) → SSIM/픽셀 회귀 판독 순으로 처리한다.
/// UI 캡처는 실제 Blazor 스튜디오 모달을 전용 서비스(이 러너 소유 인스턴스)로 헤드리스 마운트 — 앱 싱글턴 러너를 정지시키지 않는다.
/// </summary>
public sealed class HeadlessVisualQaStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, PreviewW = 640, PreviewH = 360;

    public static readonly (string Name, Type Type)[] Targets =
    {
        ("🏃 모션 매칭 스튜디오", typeof(PromptFlowStudio.Blazor.Components.Common.NeuralMotionMatchingStudioModal)),
        ("🗣️ 다국어 더빙 스튜디오", typeof(PromptFlowStudio.Blazor.Components.Common.MultilingualDubbingLipSyncStudioModal)),
    };

    private readonly object _gate = new();
    private readonly float[] _ssimS = new float[SeriesLen], _tempS = new float[SeriesLen], _zmpS = new float[SeriesLen], _msS = new float[SeriesLen];
    private readonly QaServices _services;
    private int _seriesHead, _seriesCount;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public HeadlessVisualQaStudioRunner(IServiceProvider sp) : this() { }

    public HeadlessVisualQaStudioRunner()
    {
        Motion = new NeuralMotionMatchingStudioRunner();
        Motion.SetTerrain(TerrainKind.Stairs);
        Motion.SetInput(90f, 1.2f);
        FrameBuffer = new RealtimeHeadlessFrameBufferEngine();
        Regression = new PixelPerfectVisualRegressionSynthesizer();
        _services = new QaServices(Motion);
        LastAction = "📋 대기 · 1920×1080 오프스크린 · 링 3 · UI 대상 " + Targets.Length + " · 계단 보행 액터";
    }

    public event Action? OnChange;

    public NeuralMotionMatchingStudioRunner Motion { get; }
    public RealtimeHeadlessFrameBufferEngine FrameBuffer { get; }
    public PixelPerfectVisualRegressionSynthesizer Regression { get; }
    public ZmpStairBalanceVisualDebuggingGuard Zmp => Motion.StairBalance;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public int Target { get; private set; }
    public bool Capturing { get; private set; }
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float SsimSeries(int i) => _ssimS[Idx(i)];
    public float TemporalSeries(int i) => _tempS[Idx(i)];
    public float ZmpSeries(int i) => _zmpS[Idx(i)];
    public float TickMsSeries(int i) => _msS[Idx(i)];

    public static string BaselineDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "visual_baselines");

    // ══════════════════════ 프레임(무할당) ══════════════════════

    /// <summary>1 프레임: 액터 → 오프스크린 합성 → 회귀 판독.</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("VisualQa.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var tick = Interlocked.Increment(ref _tick);
            Motion.Step();
            FrameBuffer.Render(Motion, Motion.StairBalance);
            Regression.Compare(FrameBuffer.Latest, FrameBuffer.Probe, tick);
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _ssimS[h] = Regression.UiSsim; _tempS[h] = Regression.SceneTemporalDiff; _zmpS[h] = Motion.Ik.ZmpOffset; _msS[h] = ms;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            LastTickMs = ms;
            if (ms > MaxTickMs && tick > 10) MaxTickMs = ms;
        }
    }

    // ══════════════════════ 제어 ══════════════════════

    /// <summary>실 Blazor 컴포넌트 헤드리스 캡처 → UI 레이어 교체(기준 없으면 자동 기준 등록).</summary>
    public async Task<bool> CaptureAsync(int target, CancellationToken ct = default)
    {
        target = Math.Clamp(target, 0, Targets.Length - 1);
        Target = target; Capturing = true; Raise();
        bool ok;
        try { ok = await FrameBuffer.CaptureComponentAsync(Targets[target].Type, _services, ct).ConfigureAwait(false); }
        finally { Capturing = false; }
        if (ok)
        {
            if (!_running) Step();
            if (!Regression.HasBaseline) SetBaseline(Targets[target].Type.Name);
            LastAction = "📸 헤드리스 캡처 " + Targets[target].Name + " · 표시 목록 " + FrameBuffer.BoxCount + " · " + FrameBuffer.LastCaptureMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms";
        }
        else LastAction = "⚠ 캡처 실패 — " + FrameBuffer.CaptureError;
        Raise();
        return ok;
    }

    public void SetBaseline(string name)
    {
        lock (_gate)
        {
            if (FrameBuffer.FramesRendered == 0) Step();
            Regression.CaptureBaseline(FrameBuffer.Latest, name);
        }
        LastAction = "📌 기준 프레임 등록 · " + Regression.BaselineName;
        Raise();
    }

    public string SaveBaseline()
    {
        var path = Path.Combine(BaselineDir, Sanitize(Regression.BaselineName) + ".pfsb");
        Regression.SaveBaseline(path);
        LastAction = "💾 기준 저장 · " + path;
        Raise();
        return path;
    }

    public bool LoadBaseline(string name)
    {
        var ok = Regression.LoadBaseline(Path.Combine(BaselineDir, Sanitize(name) + ".pfsb"), name);
        LastAction = ok ? "📂 기준 복원 · " + name : "⚠ 기준 파일 없음 · " + name;
        Raise();
        return ok;
    }

    private static string Sanitize(string s)
    {
        Span<char> buf = stackalloc char[Math.Min(64, Math.Max(1, s.Length))];
        var n = 0;
        foreach (var ch in s) { if (n >= buf.Length) break; buf[n++] = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_'; }
        return n == 0 ? "baseline" : new string(buf[..n]);
    }

    public void InjectLayoutFault(bool on) { FrameBuffer.InjectLayoutFault(on); LastAction = on ? "💥 레이아웃 붕괴 주입(화면 1/3 이후 표시 목록 이동)" : "🧹 레이아웃 결함 해제"; Raise(); }
    public void InjectPop() { FrameBuffer.InjectPop(); LastAction = "💥 3D 팝핑 주입(1 프레임 80 cm 순간이동)"; Raise(); }
    public void SetOverlay(bool on) { Zmp.OverlayEnabled = on; LastAction = on ? "🟢 ZMP 다각형 오버레이 ON" : "⚪ 오버레이 OFF"; Raise(); }
    public void SetStairGuard(bool on) { Motion.SetStairBalance(on); LastAction = Motion.LastAction; Raise(); }
    public void SetTerrain(TerrainKind k) { Motion.SetTerrain(k); Motion.SetInput(90f, 1.2f); Zmp.ResetMetrics(); LastAction = "🗺 액터 지형 → " + NeuralMotionMatchingStudioRunner.TerrainName(k); Raise(); }

    public void ResetMetrics()
    {
        lock (_gate) { Regression.ResetMetrics(); Zmp.ResetMetrics(); FrameBuffer.ResetTiming(); MaxTickMs = 0; }
        LastAction = "↺ 지표 초기화";
        Raise();
    }

    // ══════════════════════ 미리보기 PNG(제어 평면 · UI 전용) ══════════════════════

    /// <summary>최신 프레임 640×360 축소 PNG data URL.</summary>
    public string FramePng()
    {
        var rgba = new byte[PreviewW * PreviewH * 4];
        lock (_gate)
        {
            var src = FrameBuffer.LatestArray;
            for (var y = 0; y < PreviewH; y++)
                for (var x = 0; x < PreviewW; x++)
                    Put(rgba, (y * PreviewW + x) * 4, src[(y * 3) * RealtimeHeadlessFrameBufferEngine.W + x * 3]);
        }
        return Png(rgba, PreviewW, PreviewH);
    }

    public string HeatmapPng()
    {
        const int w = PixelPerfectVisualRegressionSynthesizer.LW, h = PixelPerfectVisualRegressionSynthesizer.LH;
        var px = new uint[w * h];
        Regression.CopyHeatmap(px);
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i++) Put(rgba, i * 4, px[i]);
        return Png(rgba, w, h);
    }

    public string ThumbPng(int slot)
    {
        const int w = PixelPerfectVisualRegressionSynthesizer.ThumbW, h = PixelPerfectVisualRegressionSynthesizer.ThumbH;
        var src = Regression.Thumb(slot);
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++) { rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = src[i]; rgba[i * 4 + 3] = 255; }
        return Png(rgba, w, h);
    }

    private static void Put(byte[] rgba, int o, uint p) { rgba[o] = (byte)(p >> 16); rgba[o + 1] = (byte)(p >> 8); rgba[o + 2] = (byte)p; rgba[o + 3] = 255; }

    private static string Png(byte[] rgba, int w, int h)
    {
        using var ms = new MemoryStream(w * h);
        PromptFlowStudio.Services.Media.BabyPngWriter.WriteRgba(ms, w, h, rgba);
        return "data:image/png;base64," + Convert.ToBase64String(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.VisualQa" };
            _loop.Start();
            LastAction = "▶ 헤드리스 60 Hz · 1920×1080 · SSIM 판독 · ZMP 오버레이";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("VisualQa.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("VisualQa.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();

    /// <summary>캡처 전용 서비스: 이 러너 소유 액터 · 지연 생성 더빙 러너만 제공(앱 싱글턴 비접촉).</summary>
    private sealed class QaServices : IServiceProvider
    {
        private readonly NeuralMotionMatchingStudioRunner _motion;
        private MultilingualDubbingLipSyncStudioRunner? _dub;

        public QaServices(NeuralMotionMatchingStudioRunner motion) => _motion = motion;

        public object? GetService(Type t)
        {
            if (t == typeof(NeuralMotionMatchingStudioRunner)) return _motion;
            if (t == typeof(MultilingualDubbingLipSyncStudioRunner)) return _dub ??= new MultilingualDubbingLipSyncStudioRunner();
            return null;
        }
    }
}
