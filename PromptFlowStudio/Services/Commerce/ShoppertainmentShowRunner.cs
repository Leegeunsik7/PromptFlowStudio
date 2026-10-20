using System.Diagnostics;

namespace PromptFlowStudio.Services.Commerce;

/// <summary>
/// [PromptFlow Studio v16.48.0] ▶ 1-Click 쇼퍼테인먼트 방송 러너.
///   ▸ 커머스 이벤트 → 오버레이(구매 토스트 · 재고 게이지) · 호스트 상품 전환 → 오버레이 상품/QR 갱신 · 15fps 틱(데모 트래픽 · 재고 폴링 주기 · 호스트 · 합성 프레임).
///   ▸ 전용 스레드 · 틱 예외 격리 · 정지 시 Join.
/// </summary>
public sealed class ShoppertainmentShowRunner : IDisposable
{
    public const int Fps = 15, FrameW = 640, FrameH = 360;

    private readonly LiveCommerceInventoryOrderBridge _bridge;
    private readonly AiLiveShowHostEngine _host;
    private readonly ShoppertainmentOverlayEngine _overlay;
    private readonly byte[] _bg = new byte[FrameW * FrameH * 4], _frame = new byte[FrameW * FrameH * 4];
    private readonly object _frameGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Thread? _thread;
    private volatile bool _run;
    private int _shownSlot = -1;
    private long _frames;

    public ShoppertainmentShowRunner(IServiceProvider sp)
        : this((LiveCommerceInventoryOrderBridge)sp.GetService(typeof(LiveCommerceInventoryOrderBridge))!, (AiLiveShowHostEngine)sp.GetService(typeof(AiLiveShowHostEngine))!,
               (ShoppertainmentOverlayEngine)sp.GetService(typeof(ShoppertainmentOverlayEngine))!) { Guard = sp.GetService(typeof(PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard)) as PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard; Guard?.SetBudget(GuardSite, 32 * 1024); }   // v16.51.0 루프 프로브(이벤트성 발화/토스트 할당 허용 예산)

    /// <summary>v16.51.0 핫패스 프로파일링 가드(선택) · 사이트 인덱스.</summary>
    public PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard? Guard { get; set; }
    public const int GuardSite = 48;

    public ShoppertainmentShowRunner(LiveCommerceInventoryOrderBridge bridge, AiLiveShowHostEngine host, ShoppertainmentOverlayEngine overlay)
    {
        _bridge = bridge; _host = host; _overlay = overlay;
        _bridge.OnEvent += OnEvent;
        for (var y = 0; y < FrameH; y++)
            for (var x = 0; x < FrameW; x++)
            {
                var o = (y * FrameW + x) * 4;
                _bg[o] = (byte)(40 + x * 40 / FrameW); _bg[o + 1] = (byte)(28 + y * 30 / FrameH); _bg[o + 2] = (byte)(60 + y * 60 / FrameH); _bg[o + 3] = 255;
            }
    }

    public event Action? OnFrame;

    public bool IsLive => _thread is not null;
    public bool DemoTraffic { get; set; } = true;
    public double DemoOrdersPerSec { get; set; } = 1.2;
    public double PollIntervalSec { get; set; } = 15;
    public long Frames => Interlocked.Read(ref _frames);
    public double NowMs => _clock.Elapsed.TotalMilliseconds;

    public void CopyFrame(Span<byte> rgba) { lock (_frameGate) _frame.CopyTo(rgba); }

    public void Start()
    {
        if (_thread is not null) return;
        if (_bridge.ProductCount == 0) foreach (var p in AiLiveShowHostEngine.DemoCatalog()) _bridge.AddProduct(p);
        _host.Start(NowMs);
        SyncProduct();
        _run = true;
        var th = new Thread(Loop) { IsBackground = true, Name = "PFS.LiveCommerce" };
        _thread = th;
        th.Start();
    }

    public void Stop()
    {
        var th = Interlocked.Exchange(ref _thread, null);
        if (th is null) return;
        _run = false;
        th.Join(1500);
        _host.Stop(NowMs);
    }

    private void OnEvent(CommerceEvent e)
    {
        if (e.Kind == CommerceEventKind.Order && (uint)e.Slot < (uint)_bridge.ProductCount) _overlay.PushBuyer(e.Buyer, _bridge.Product(e.Slot).Name, e.Qty, e.TimeMs);
        if (e.Slot == _host.CurrentSlot) _overlay.SetStock(e.Remaining);
    }

    /// <summary>호스트 현재 상품 ↔ 오버레이 동기(전환 시 QR/텍스처 재생성).</summary>
    public void SyncProduct()
    {
        var slot = _host.CurrentSlot;
        if (slot < 0 || slot == _shownSlot) return;
        _shownSlot = slot;
        _overlay.SetProduct(_bridge.Product(slot), _bridge.StockOf(slot), _host.CouponCode);
    }

    /// <summary>러너 1틱(테스트/수동 구동 가능).</summary>
    public void Step(Random rng, double dtSec, double nowMs)
    {
        var slot = _host.CurrentSlot;
        if (DemoTraffic && slot >= 0) _bridge.SimulateTraffic(rng, slot, DemoOrdersPerSec, dtSec, nowMs);
        else _bridge.Tick(nowMs);
        _host.Tick(nowMs);
        SyncProduct();
        lock (_frameGate)
        {
            _bg.CopyTo(_frame.AsSpan());
            _overlay.Render(_frame, FrameW, FrameH, FrameW * 4, nowMs);
        }
        Interlocked.Increment(ref _frames);
    }

    private void Loop()
    {
        var rng = new Random(42);
        var period = 1000.0 / Fps;
        var last = NowMs; var nextPoll = NowMs + PollIntervalSec * 1000;
        while (_run)
        {
            var now = NowMs;
            try
            {
                var probe = Guard?.Begin(GuardSite);
                Step(rng, (now - last) / 1000, now);
                if (probe is { } pr) Guard!.End(pr);
                if (now >= nextPoll && _bridge.BaseUrl.Length > 0) { nextPoll = now + PollIntervalSec * 1000; _ = _bridge.PollInventoryAsync(now); }
                OnFrame?.Invoke();
            }
            catch (Exception ex) { PromptFlowStudio.Program.Log("LiveCommerce.Tick", ex); }
            last = now;
            var wait = period - (NowMs - now);
            if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    public void Dispose()
    {
        Stop();
        _bridge.OnEvent -= OnEvent;
    }
}
