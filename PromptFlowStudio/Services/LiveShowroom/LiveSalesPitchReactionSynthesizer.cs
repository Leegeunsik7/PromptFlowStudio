namespace PromptFlowStudio.Services.LiveShowroom;

public enum SalesPhase : byte { Idle, Intro, Showcase, TimeSale, LowStock, SoldOut, Ended }
public enum SalesCue : byte { Purchase, Intro, Showcase, TimeSaleStart, LowStock, SoldOut, TimeSaleEnd, Refused }

/// <summary>v17.68.0 구매 이벤트(게이트웨이 스레드 → 링 · 무할당).</summary>
public struct PurchaseEvent
{
    public int Qty, UserLen;
    public long AmountWon, AtTick;
}

/// <summary>v17.68.0 세일즈 FSM 출력 결선(MCR 틱 스레드 · 무할당).</summary>
public interface ISalesPitchOutput
{
    bool Say(ReadOnlySpan<char> line);
    void Gesture(HoldPose pose, float seconds);
    void Showcase(bool on);
}

/// <summary>
/// [PromptFlow Studio v17.68.0] 실시간 세일즈 피치 &amp; 구매 리액션 FSM 합성기(무할당).
///   ▸ 위상: Idle → Intro(소개) → Showcase(360°) → TimeSale(카운트다운) → LowStock(잔여 ≤ 10 % · 1 회 긴급 멘트) → SoldOut(품절 멘트 · 배너) / Ended(타이머 0).
///   ▸ 구매: 링(64) → 틱마다 소비 → 재고 차감(초과 수량 = 잔여까지 · 품절 후 거부) · 누적 판매액 · 구매자 피드 → 축하 제스처(박수 ↔ 엄지척 교대 1.2 s) + 감사 멘트("OO님 [상품] n개 구매 감사합니다!") 즉시 인터럽트.
///     같은 틱 다중 구매는 1 멘트로 합산("OO님 외 k분"). 구매 → 멘트 지연은 수신 틱 기준 측정(<see cref="LastReactionMs"/>).
///   ▸ 멘트는 v17.65 토크쇼 강제 발언권 애드리브 → <see cref="EcommerceAdComplianceSafetyGuard"/> 심의 필터 통과.
/// </summary>
public sealed class LiveSalesPitchReactionSynthesizer
{
    public const int Ring = 64, UserCap = 24, FeedCap = 32, LineCap = 200;
    public const float LowStockRatio = 0.1f, Hz = 60f;

    private readonly object _q = new();
    private readonly PurchaseEvent[] _ev = new PurchaseEvent[Ring];
    private readonly char[] _evUser = new char[Ring * UserCap];
    private readonly PurchaseEvent[] _feed = new PurchaseEvent[FeedCap];
    private readonly char[] _feedUser = new char[FeedCap * UserCap];
    private readonly char[] _line = new char[LineCap];
    private readonly char[] _first = new char[UserCap];
    private readonly char[] _num = new char[24];
    private int _head, _count, _feedHead, _feedN, _gestureFlip;
    private long _tick, _endTick;
    private ShowroomProduct? _product;
    private bool _lowSaid;

    public SalesPhase Phase { get; private set; }
    public ShowroomProduct? Product => _product;
    public int Stock { get; private set; }
    public int InitialStock { get; private set; }
    public long Revenue { get; private set; }
    public long UnitsSold { get; private set; }
    public long Orders { get; private set; }
    public long Refused { get; private set; }
    public long Dropped { get; private set; }
    public long Reactions { get; private set; }
    public float LastReactionMs { get; private set; }
    public float MaxReactionMs { get; private set; }
    public long SoldOutTick { get; private set; } = -1;
    public float RemainingSeconds => Phase is SalesPhase.TimeSale or SalesPhase.LowStock ? Math.Max(0, _endTick - _tick) / Hz : 0f;
    public float StockRatio => InitialStock > 0 ? Stock / (float)InitialStock : 0f;
    public bool LowStock => Phase == SalesPhase.LowStock || (Stock > 0 && StockRatio <= LowStockRatio);
    public bool SoldOut => Phase == SalesPhase.SoldOut;
    public int FeedCount => _feedN;
    public ref readonly PurchaseEvent Feed(int i) => ref _feed[(_feedHead - 1 - i + 2 * FeedCap) % FeedCap];
    public ReadOnlySpan<char> FeedUser(int i) { var k = (_feedHead - 1 - i + 2 * FeedCap) % FeedCap; return _feedUser.AsSpan(k * UserCap, _feed[k].UserLen); }
    public SalesCue LastCue { get; private set; }
    public long Tick => _tick;

    public static string PhaseName(SalesPhase p) => p switch
    {
        SalesPhase.Intro => "상품 소개", SalesPhase.Showcase => "360° 쇼케이스", SalesPhase.TimeSale => "타임세일", SalesPhase.LowStock => "품절 임박", SalesPhase.SoldOut => "품절", SalesPhase.Ended => "세일 종료", _ => "대기",
    };

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void Load(ShowroomProduct p)
    {
        _product = p; Stock = InitialStock = Math.Max(0, p.Stock);
        Revenue = UnitsSold = Orders = Refused = 0; _lowSaid = false; SoldOutTick = -1; _feedN = 0;
        Phase = SalesPhase.Idle;
    }

    /// <summary>표시 가격만 교체(위상 · 재고 · 실적 유지).</summary>
    public void UpdatePrice(ShowroomProduct p) { if (_product is not null && p.Id == _product.Id) _product = p; }

    // ══════════════════════ 구매 수신(임의 스레드 · 무할당) ══════════════════════
    public bool OfferPurchase(ReadOnlySpan<char> user, int qty, long amountWon)
    {
        lock (_q)
        {
            if (_count >= Ring) { Dropped++; return false; }
            var k = (_head + _count) % Ring;
            ref var e = ref _ev[k];
            e.Qty = Math.Clamp(qty, 1, 99); e.AmountWon = amountWon; e.AtTick = _tick; e.UserLen = Math.Min(user.Length, UserCap);
            user[..e.UserLen].CopyTo(_evUser.AsSpan(k * UserCap));
            _count++;
            return true;
        }
    }

    // ══════════════════════ 틱(MCR 스레드 · 무할당) ══════════════════════
    public void Start(ISalesPitchOutput o)
    {
        if (_product is null) return;
        Phase = SalesPhase.Intro; LastCue = SalesCue.Intro;
        var n = 0; n = Put(n, "오늘 소개해 드릴 상품은 "); n = Put(n, _product.Name); n = Put(n, "입니다! 지금부터 자세히 보여드릴게요.");
        o.Say(_line.AsSpan(0, n)); o.Gesture(HoldPose.Hold, 0f);
    }

    public void BeginShowcase(ISalesPitchOutput o)
    {
        if (_product is null || Phase is SalesPhase.SoldOut) return;
        Phase = SalesPhase.Showcase; LastCue = SalesCue.Showcase;
        var n = 0; n = Put(n, _product.Name); n = Put(n, " 디테일 한번 돌려서 보여드릴게요, 광택 보이시죠?");
        o.Say(_line.AsSpan(0, n)); o.Showcase(true);
    }

    public void StartTimeSale(ISalesPitchOutput o, float seconds)
    {
        if (_product is null || Stock <= 0) return;
        Phase = StockRatio <= LowStockRatio ? SalesPhase.LowStock : SalesPhase.TimeSale; LastCue = SalesCue.TimeSaleStart;
        _endTick = _tick + (long)(Math.Max(1f, seconds) * Hz);
        var n = 0; n = Put(n, "지금부터 "); n = PutInt(n, (long)MathF.Round(seconds / 60f)); n = Put(n, "분 특가! "); n = PutInt(n, _product.DisplayedPercent); n = Put(n, "% 할인, 수량 "); n = PutInt(n, Stock); n = Put(n, "개 한정입니다!");
        o.Say(_line.AsSpan(0, n)); o.Showcase(false); o.Gesture(HoldPose.Hold, 0f);
    }

    public void Step(ISalesPitchOutput o)
    {
        _tick++;
        if (_product is null) return;
        // ① 구매 소비 — 틱 내 합산 1 멘트
        var orders = 0; var units = 0; var firstLen = 0; long maxWait = 0;
        Span<char> user = stackalloc char[UserCap];
        while (true)
        {
            PurchaseEvent e; int ulen;
            lock (_q)
            {
                if (_count == 0) break;
                e = _ev[_head]; ulen = e.UserLen;
                _evUser.AsSpan(_head * UserCap, ulen).CopyTo(user);
                _head = (_head + 1) % Ring; _count--;
            }
            if (Stock <= 0 || Phase == SalesPhase.Ended) { Refused++; continue; }
            var q = Math.Min(e.Qty, Stock);
            Stock -= q; UnitsSold += q; Orders++; Revenue += _product.SalePrice * q;
            ref var f = ref _feed[_feedHead];
            f = e; f.Qty = q; f.AmountWon = _product.SalePrice * q;
            user[..ulen].CopyTo(_feedUser.AsSpan(_feedHead * UserCap));
            _feedHead = (_feedHead + 1) % FeedCap; if (_feedN < FeedCap) _feedN++;
            if (orders == 0) { firstLen = ulen; user[..ulen].CopyTo(_first); }
            orders++; units += q;
            maxWait = Math.Max(maxWait, _tick - e.AtTick);
        }
        if (orders > 0)
        {
            var n = 0;
            n = Put(n, _first.AsSpan(0, firstLen)); n = Put(n, "님");
            if (orders > 1) { n = Put(n, " 외 "); n = PutInt(n, orders - 1); n = Put(n, "분"); }
            n = Put(n, " "); n = Put(n, _product.Name); n = Put(n, " "); n = PutInt(n, units); n = Put(n, "개 구매 감사합니다!");
            if (Stock > 0 && Phase is SalesPhase.TimeSale or SalesPhase.LowStock) { n = Put(n, " 남은 수량 "); n = PutInt(n, Stock); n = Put(n, "개!"); }
            o.Say(_line.AsSpan(0, n));
            o.Gesture((_gestureFlip++ & 1) == 0 ? HoldPose.Clap : HoldPose.ThumbsUp, 1.2f);
            LastCue = SalesCue.Purchase; Reactions++;
            LastReactionMs = maxWait * 1000f / Hz; if (LastReactionMs > MaxReactionMs) MaxReactionMs = LastReactionMs;
        }
        // ② 품절 · 품절 임박 · 타이머
        if (Stock <= 0 && Phase != SalesPhase.SoldOut && InitialStock > 0)
        {
            Phase = SalesPhase.SoldOut; SoldOutTick = _tick; LastCue = SalesCue.SoldOut;
            var n = 0; n = Put(n, "와 여러분 "); n = Put(n, _product.Name); n = Put(n, " 방금 품절됐습니다! 성원에 정말 감사드려요!");
            o.Say(_line.AsSpan(0, n)); o.Gesture(HoldPose.Clap, 2f); o.Showcase(false);
            return;
        }
        if (Phase == SalesPhase.TimeSale && StockRatio <= LowStockRatio) Phase = SalesPhase.LowStock;
        if (Phase == SalesPhase.LowStock && !_lowSaid && orders == 0)
        {
            _lowSaid = true; LastCue = SalesCue.LowStock;
            var n = 0; n = Put(n, "품절 임박! 이제 "); n = PutInt(n, Stock); n = Put(n, "개밖에 안 남았어요, 서두르세요!");
            o.Say(_line.AsSpan(0, n));
        }
        if (Phase is SalesPhase.TimeSale or SalesPhase.LowStock && _tick >= _endTick)
        {
            Phase = SalesPhase.Ended; LastCue = SalesCue.TimeSaleEnd;
            o.Say("타임세일이 종료됐습니다, 함께해 주셔서 감사합니다!");
        }
    }

    private int Put(int n, ReadOnlySpan<char> s) { var k = Math.Min(s.Length, LineCap - n); s[..k].CopyTo(_line.AsSpan(n)); return n + k; }
    private int PutInt(int n, long v) { v.TryFormat(_num, out var w, "N0", System.Globalization.CultureInfo.InvariantCulture); return Put(n, _num.AsSpan(0, w)); }
}
