using System.Globalization;
using System.Numerics;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.SplatEnvironment;
using PromptFlowStudio.Services.StreamGateway;
using PromptFlowStudio.Services.Talkshow;

namespace PromptFlowStudio.Services.LiveShowroom;

/// <summary>
/// [PromptFlow Studio v17.68.0] 🛍️ 버추얼 라이브 커머스 스튜디오 러너 — v17.66 MCR 추가 장면 레이어(<see cref="IMcrSceneLayer"/>).
///   ▸ 구매 수신: v17.62 게이트웨이 커머스 탭(`!buy [수량]` · 후원 금액 ≥ 할인가 × 수량) · UI/테스트 <see cref="Purchase"/> → 세일즈 FSM 링.
///   ▸ MCR 틱: 세일즈 FSM(멘트 · 제스처 · 품절/타이머) → 팔 포즈 주입 → (컴포지터 갱신은 MCR) → 다음 틱 손목 앵커 상품 기록 ·
///     쇼케이스 진입 시 MCR 인서트 캠 상품 클로즈업 컷 · PGM CTA 배너(가격 정합성 실패 시 비노출).
///   ▸ 모든 멘트는 호스트(아바타 0) 강제 발언권 애드리브 → 토크쇼 발화 필터(광고 심의)로 대본 · 쇼러너 애드리브와 함께 검사.
/// </summary>
public sealed class LiveCommerceStudioRunner : IMcrSceneLayer, ISalesPitchOutput, IChatDispatchTarget, IDisposable
{
    public const int Hist = 240;

    public static readonly ShowroomProduct[] Catalog =
    {
        new(1, "루미에르 오드퍼퓸 50ml", ProductShape.Perfume, 129_000, 89_000, 31, 120, new Vector3(0.95f, 0.72f, 0.80f), 90f, 0.15f),
        new(2, "네오폰 X 울트라", ProductShape.Smartphone, 1_590_000, 1_190_000, 25, 40, new Vector3(0.18f, 0.20f, 0.24f), 140f, 0.85f) { Splats = 50_000 },
        new(3, "벨벳 매트 립스틱 #21", ProductShape.Lipstick, 38_000, 26_600, 30, 300, new Vector3(0.75f, 0.06f, 0.14f), 40f, 0.05f),
        new(4, "시그니처 토트백", ProductShape.Handbag, 459_000, 321_300, 30, 25, new Vector3(0.45f, 0.26f, 0.14f), 25f, 0.0f),
        new(5, "에어러너 스니커즈", ProductShape.Sneaker, 179_000, 125_300, 30, 80, new Vector3(0.20f, 0.45f, 0.85f), 18f, 0.0f),
    };

    private readonly BroadcastMcrStudioRunner _mcr;
    private readonly bool _ownMcr;
    private readonly StreamGatewayClipperStudioRunner? _gateway;
    private readonly float[] _revS = new float[Hist], _layerS = new float[Hist];
    private readonly char[] _cmdUser = new char[LiveSalesPitchReactionSynthesizer.UserCap];
    private int _sHead, _sN;
    private float _gestureLeft;
    private HoldPose _basePose = HoldPose.Rest;
    private bool _showcaseCue;
    private long _layerFrames;

    public LiveCommerceStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(BroadcastMcrStudioRunner)) as BroadcastMcrStudioRunner,
        sp.GetService(typeof(StreamGatewayClipperStudioRunner)) as StreamGatewayClipperStudioRunner) { }

    public LiveCommerceStudioRunner(BroadcastMcrStudioRunner? mcr = null, StreamGatewayClipperStudioRunner? gateway = null)
    {
        _ownMcr = mcr is null;
        _mcr = mcr ?? new BroadcastMcrStudioRunner();
        _gateway = gateway;
        Banner.Sales = Sales;
        Select(Catalog[0]);
    }

    public event Action? OnChange;
    public BroadcastMcrStudioRunner Mcr => _mcr;
    public MultiAvatarTalkshowStudioRunner Talkshow => _mcr.Talkshow;
    public ShowroomProductInspectionEngine Inspection { get; } = new();
    public LiveSalesPitchReactionSynthesizer Sales { get; } = new();
    public EcommerceAdComplianceSafetyGuard Compliance { get; } = new();
    public CommerceCtaBannerOverlay Banner { get; } = new();
    public bool Attached { get; private set; }
    public bool GatewayAvailable => _gateway is not null;
    public bool LinkGateway { get; private set; }
    public bool IsRunning => _mcr.IsRunning;
    public int SelectedIndex { get; private set; }
    public long CloseUps { get; private set; }
    public long SaidLines { get; private set; }
    public long LayerOverBudget { get; private set; }
    public long GcStalls { get; private set; }
    public double LastLayerMs { get; private set; }
    public double MaxLayerMs { get; private set; }
    public long LayerFrames => _layerFrames;
    public string? LastAction { get; private set; }
    public int SeriesCount => _sN;
    private int Idx(int i) => (_sHead - 1 - i + 2 * Hist) % Hist;
    public float RevenueSeries(int i) => _revS[Idx(i)];
    public float LayerMsSeries(int i) => _layerS[Idx(i)];

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    /// <summary>MCR · 토크쇼 결선(레이어 · PGM 오버레이 · 발화 심의 필터).</summary>
    public void Attach()
    {
        _mcr.AttachLayer(this);
        _mcr.Mcr.Overlay = Banner;
        _mcr.Talkshow.LineFilter = Compliance;
        Attached = true;
        LastAction = "🔗 MCR 레이어 · CTA 배너 · 광고 심의 필터 결선"; Raise();
    }

    public void Detach()
    {
        _mcr.DetachLayer(this);
        if (ReferenceEquals(_mcr.Mcr.Overlay, Banner)) _mcr.Mcr.Overlay = null;
        if (ReferenceEquals(_mcr.Talkshow.LineFilter, Compliance)) _mcr.Talkshow.LineFilter = null;
        Inspection.SetPose(HoldPose.Rest);
        Attached = false;
    }

    public void Select(ShowroomProduct p)
    {
        SelectedIndex = Array.IndexOf(Catalog, p);
        Inspection.SetProduct(p);
        Sales.Load(p);
        Banner.PriceBlocked = !Compliance.ValidatePrice(p.ListPrice, p.SalePrice, p.DisplayedPercent);
        LastAction = "🛍 " + p.Name + " · " + ShowroomProductInspectionEngine.ShapeName(p.Shape) + " " + Inspection.SplatCount.ToString("N0", CultureInfo.InvariantCulture) + " 스플랫"
            + (Banner.PriceBlocked ? " · ⛔ 가격 " + EcommerceAdComplianceSafetyGuard.PriceName(Compliance.LastPrice) + " → 배너 비노출" : "");
        Raise();
    }

    public void Select(int index) => Select(Catalog[Math.Clamp(index, 0, Catalog.Length - 1)]);

    /// <summary>표시 가격 수정(관리자 입력) — 정합성 재검증 · 불일치면 배너 즉시 비노출.</summary>
    public bool EditPrice(long list, long sale, int percent)
    {
        var cur = Sales.Product ?? Catalog[0];
        var p = cur with { ListPrice = list, SalePrice = sale, DisplayedPercent = percent };
        var ok = Compliance.ValidatePrice(list, sale, percent);
        Banner.PriceBlocked = !ok;
        Inspection.SetProduct(p); Sales.UpdatePrice(p);
        LastAction = ok ? "💲 가격 정합 · 배너 노출" : "⛔ " + EcommerceAdComplianceSafetyGuard.PriceName(Compliance.LastPrice) + " → 쇼핑 배너 차단";
        Raise();
        return ok;
    }

    public void StartPitch() { lock (_cmdUser) { _pending = PendingCmd.Intro; } LastAction = "🎙 상품 소개 시작"; Raise(); }
    public void BeginShowcase() { lock (_cmdUser) { _pending = PendingCmd.Showcase; } LastAction = "🔄 360° 쇼케이스 · 상품 클로즈업"; Raise(); }
    public void StartTimeSale(float seconds = 180f) { lock (_cmdUser) { _pending = PendingCmd.TimeSale; _pendingSec = seconds; } LastAction = "⏱ 타임세일 " + seconds.ToString("0", CultureInfo.InvariantCulture) + " s"; Raise(); }
    public bool Purchase(string user, int qty = 1) => Sales.OfferPurchase(user, qty, (Sales.Product?.SalePrice ?? 0) * qty);
    public void Start() { if (!Attached) Attach(); _mcr.Start(); LastAction = "▶ MCR · 라이브 커머스"; Raise(); }
    public void Stop() { _mcr.Stop(); LastAction = "⏸ 정지"; Raise(); }
    public void Step() { if (!Attached) Attach(); _mcr.Step(); }

    public void SetLinkGateway(bool on)
    {
        if (on && _gateway is null) return;
        LinkGateway = on;
        if (_gateway is not null) _gateway.CommerceTap = on ? this : null;
        LastAction = on ? "🔗 v17.62 채팅 결제(`!buy n`) 결선" : "🔗 게이트웨이 해제"; Raise();
    }

    private enum PendingCmd : byte { None, Intro, Showcase, TimeSale }
    private PendingCmd _pending;
    private float _pendingSec;

    // ══════════════════════ IChatDispatchTarget(게이트웨이 스레드 · 무할당) ══════════════════════
    public bool Deliver(ChatPlatform platform, ReadOnlySpan<char> user, ReadOnlySpan<char> text, int amount, ChatRole role, long nowMs)
    {
        var t = text.Trim();
        if (!t.StartsWith("!buy", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("!구매", StringComparison.Ordinal)) return false;
        var sp = t.IndexOf(' ');
        var qty = 1;
        if (sp > 0 && int.TryParse(t[(sp + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q)) qty = Math.Clamp(q, 1, 99);
        var price = Sales.Product?.SalePrice ?? long.MaxValue;
        if (amount < price * qty) return false;   // 결제 금액 부족 → 무시
        return Sales.OfferPurchase(user, qty, amount);
    }

    // ══════════════════════ IMcrSceneLayer(MCR 틱 · 무할당) ══════════════════════
    public void Step(UnifiedGaussianSceneCompositorEngine comp, in SplatCamera pgm, double nowMs, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var gc0 = GC.CollectionCount(0);
        Compliance.Frame = Sales.Tick;
        PendingCmd cmd; float sec;
        lock (_cmdUser) { cmd = _pending; sec = _pendingSec; _pending = PendingCmd.None; }
        if (cmd == PendingCmd.Intro) Sales.Start(this);
        else if (cmd == PendingCmd.Showcase) Sales.BeginShowcase(this);
        else if (cmd == PendingCmd.TimeSale) Sales.StartTimeSale(this, sec);
        Sales.Step(this);
        if (_gestureLeft > 0f) { _gestureLeft -= dt; if (_gestureLeft <= 0f) Inspection.SetPose(_basePose); }
        // 직전 컴포지터 갱신 관절로 상품 앵커 기록 → 이번 틱 팔 포즈 주입(컴포지터 Update 는 레이어 뒤)
        Inspection.Write(comp, in pgm, dt);
        Inspection.PreUpdate(comp, dt);
        if (_showcaseCue && Inspection.SplatCount > 0) { _showcaseCue = false; _mcr.CueInsert(Inspection.Center, 0.55f); CloseUps++; }
        else if (Inspection.Showcase) _mcr.InsertTarget = Inspection.Center;
        var ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _layerFrames++;
        LastLayerMs = ms; if (ms > MaxLayerMs && _layerFrames > 3) MaxLayerMs = ms;
        if (GC.CollectionCount(0) != gc0) GcStalls++;
        else if (ms > 1000.0 / 60) LayerOverBudget++;
        _revS[_sHead] = Sales.Revenue; _layerS[_sHead] = (float)ms; _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
    }

    // ══════════════════════ ISalesPitchOutput(MCR 틱 · 무할당) ══════════════════════
    public bool Say(ReadOnlySpan<char> line) { SaidLines++; return _mcr.Talkshow.InjectAdlib(0, line); }

    public void Gesture(HoldPose pose, float seconds)
    {
        if (seconds <= 0f) { _basePose = pose; if (_gestureLeft <= 0f) Inspection.SetPose(pose); return; }
        Inspection.SetPose(pose); _gestureLeft = seconds;
    }

    public void Showcase(bool on)
    {
        _basePose = on ? HoldPose.Showcase : HoldPose.Hold;
        if (_gestureLeft <= 0f) Inspection.SetPose(_basePose);
        if (on) _showcaseCue = true;
        else _mcr.InsertTarget = null;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Commerce.OnChange", ex); }
    }

    public void Dispose()
    {
        Detach();
        if (_gateway is not null && ReferenceEquals(_gateway.CommerceTap, this)) _gateway.CommerceTap = null;
        if (_ownMcr) _mcr.Dispose();
    }

    public string Summary() =>
        "판매 " + Sales.UnitsSold.ToString(CultureInfo.InvariantCulture) + "개 · ₩" + Sales.Revenue.ToString("N0", CultureInfo.InvariantCulture) + " · 재고 " + Sales.Stock.ToString(CultureInfo.InvariantCulture)
        + " · " + LiveSalesPitchReactionSynthesizer.PhaseName(Sales.Phase) + " · 심의 치환 " + Compliance.Replaced.ToString(CultureInfo.InvariantCulture) + " · 차단 " + Compliance.Blocked.ToString(CultureInfo.InvariantCulture);
}
