using System.Diagnostics;
using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.Icvfx;

/// <summary>
/// [PromptFlow Studio v16.59.0] 🎬 ICVFX 가상 제작 러너 — 에이전틱 디렉터 틱(카메라/조명/VFX) → LED 프러스텀 렌더 → 콘텐츠 프리필터 → 월 평균색 → 필 라이트 피드백 →
/// LED 출력(Rec.2100 PQ) · 월 전개도/인-카메라 프리뷰(ACES SDR) · 30Hz PeriodicTimer · <see cref="TickOnce"/> 무할당.
/// </summary>
public sealed class VirtualProductionStudioRunner : IDisposable
{
    public const int CamWidth = 192, CamHeight = 108, TickHz = 30;

    private readonly object _gate = new();
    private readonly IcvfxLedWallFrustumRendererEngine _frustum;
    private readonly AgenticAutonomousVirtualProductionDirector _director;
    private readonly AcesColorManagementAndMoireSuppressionEngine _color;
    private readonly float[] _camLinear = new float[CamWidth * CamHeight * 3];
    private readonly byte[] _camFront = new byte[CamWidth * CamHeight * 3];
    private byte[] _wallFront = Array.Empty<byte>(), _wallPq = Array.Empty<byte>();
    private int _wallW, _wallH;
    private CancellationTokenSource? _cts;
    private long _frameSeq;
    private float _inCameraCoverage;

    public const string DemoScript =
        "EXT. 사막 협곡 - 골든아워\n" +
        "주인공이 천천히 협곡 가장자리로 걸어간다. 고요한 바람.\n" +
        "주인공: 여기가 마지막 신호가 잡힌 곳이야.\n" +
        "숨죽인 정적 속, 무언가 다가온다.\n" +
        "폭풍이 몰아치고 번개가 하늘을 가른다. 비가 쏟아진다.\n" +
        "거대한 탑이 모습을 드러낸다.\n" +
        "탑이 폭발하며 붕괴한다! 주인공이 빠르게 달려 피한다.\n" +
        "EXT. 사막 - 밤\n" +
        "잔잔한 밤하늘 아래 주인공이 휴식한다.\n";

    public VirtualProductionStudioRunner(IServiceProvider sp)
        : this((IcvfxLedWallFrustumRendererEngine?)sp.GetService(typeof(IcvfxLedWallFrustumRendererEngine)) ?? new IcvfxLedWallFrustumRendererEngine(),
               (AgenticAutonomousVirtualProductionDirector?)sp.GetService(typeof(AgenticAutonomousVirtualProductionDirector)),
               (AcesColorManagementAndMoireSuppressionEngine?)sp.GetService(typeof(AcesColorManagementAndMoireSuppressionEngine)) ?? new AcesColorManagementAndMoireSuppressionEngine()) { }

    public VirtualProductionStudioRunner(IcvfxLedWallFrustumRendererEngine frustum, AgenticAutonomousVirtualProductionDirector? director, AcesColorManagementAndMoireSuppressionEngine color)
    {
        _frustum = frustum;
        _director = director ?? new AgenticAutonomousVirtualProductionDirector(frustum);
        _color = color;
        EnsureWall();
        if (_director.Beats.Count == 0) _director.LoadScript(DemoScript);
    }

    public event Action? OnChange;

    public IcvfxLedWallFrustumRendererEngine Frustum => _frustum;
    public AgenticAutonomousVirtualProductionDirector Director => _director;
    public AcesColorManagementAndMoireSuppressionEngine Color => _color;
    public bool IsRunning => _cts is not null;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public int WallWidth => _wallW;
    public int WallHeight => _wallH;
    public float InCameraCoverage => Volatile.Read(ref _inCameraCoverage);
    public ColorSpace LedOutput { get; set; } = ColorSpace.Rec2100Pq;
    public bool ShowFrustumOutline { get; set; } = true;
    public string? LastAction { get; private set; }

    private void EnsureWall()
    {
        int w = 0, h = 0;
        var v = _frustum.Volume;
        for (var i = 0; i < v.Count; i++) { w += v[i].ResX; h = Math.Max(h, v[i].ResY); }
        if (w == _wallW && h == _wallH) return;
        _wallW = w; _wallH = h;
        _wallFront = new byte[w * h * 3];
        _wallPq = new byte[w * h * 3];
    }

    /// <summary>1틱(무할당 · 볼륨 해상도 변경 시만 버퍼 재할당).</summary>
    public void TickOnce(double dt)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Icvfx.Runner.Tick");
            _director.Tick(dt);
            _frustum.RenderFrame();
            EnsureWall();
            var v = _frustum.Volume;
            Vector3 sum = Vector3.Zero; var cnt = 0;
            var x0 = 0;
            for (var s = 0; s < v.Count; s++)
            {
                var seg = v[s];
                _color.PrefilterContent(seg.Linear, seg.ResX, seg.ResY);
                for (var y = 0; y < seg.ResY; y++)
                    for (var x = 0; x < seg.ResX; x++)
                    {
                        var k = (y * seg.ResX + x) * 3;
                        var c = new Vector3(seg.Linear[k], seg.Linear[k + 1], seg.Linear[k + 2]);
                        var m = seg.Mask[y * seg.ResX + x];
                        if (m < 0.5f) { sum += c; cnt++; }
                        var disp = _color.FromAcesCg(c, ColorSpace.SRgbDisplayAces);
                        var pq = _color.FromAcesCg(c, LedOutput);
                        if (ShowFrustumOutline && m > 0.02f && m < 0.98f) disp = new Vector3(1f, 0.85f, 0.1f);
                        var o = (y * _wallW + x0 + x) * 3;
                        _wallFront[o] = (byte)Math.Clamp((int)(disp.X * 255f + 0.5f), 0, 255);
                        _wallFront[o + 1] = (byte)Math.Clamp((int)(disp.Y * 255f + 0.5f), 0, 255);
                        _wallFront[o + 2] = (byte)Math.Clamp((int)(disp.Z * 255f + 0.5f), 0, 255);
                        _wallPq[o] = (byte)Math.Clamp((int)(pq.X * 255f + 0.5f), 0, 255);
                        _wallPq[o + 1] = (byte)Math.Clamp((int)(pq.Y * 255f + 0.5f), 0, 255);
                        _wallPq[o + 2] = (byte)Math.Clamp((int)(pq.Z * 255f + 0.5f), 0, 255);
                    }
                x0 += seg.ResX;
            }
            if (cnt > 0) _director.SetWallFill(sum / cnt);
            var cam = _frustum.TrackedCamera(CamWidth, CamHeight);
            Volatile.Write(ref _inCameraCoverage, _frustum.CaptureInCamera(cam, _camLinear));
            _color.Encode(_camLinear, _camFront, ColorSpace.SRgbDisplayAces);
            Interlocked.Increment(ref _frameSeq);
        }
    }

    public void CopyCamera(Span<byte> dst) { lock (_gate) _camFront.AsSpan().CopyTo(dst); }

    /// <summary>월 전개도 복사(dst ≥ WallWidth×WallHeight×3).</summary>
    public void CopyWall(Span<byte> dst, bool pqSignal = false) { lock (_gate) (pqSignal ? _wallPq : _wallFront).AsSpan().CopyTo(dst); }

    public void Restart(string? script = null)
    {
        lock (_gate)
        {
            if (script is not null) _director.LoadScript(script);
            _director.Seek(0);
        }
        LastAction = "⏮ 처음부터";
        Raise();
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 자율 디렉팅";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TickHz));
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(0.2, (now - last) / (double)Stopwatch.Frequency);
                last = now;
                TickOnce(dt);
                if ((FrameSeq & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Icvfx.Runner.Loop", ex); }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Icvfx.Runner.OnChange", ex); }
    }

    public void Dispose() { Stop(); _director.Dispose(); }
}
