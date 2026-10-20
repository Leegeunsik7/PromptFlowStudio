using System.Globalization;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.DigitalTwin;
using PromptFlowStudio.Services.Newsroom;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.VTuber;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Commerce;

public enum HostTone : byte { Energetic, Friendly, Premium, Humorous }

public enum HostLineKind : byte { Intro, SellingPoint, Demo, Offer, Answer, Urgency, SoldOut, Surge, ShoutOut, Transition, Closing }

public enum ChatIntent : byte { Price, Stock, Option, Shipping, Coupon, Purchase, Faq, Other }

/// <summary>쇼호스트 멘트(시각 · 종류 · 상품 · 대사 · 표정 · 출처).</summary>
public sealed record HostLine(double TimeMs, HostLineKind Kind, string Sku, string Text, FacialEmotion Emotion, string Source);

/// <summary>시청자 질문 대기(정규화 키 · 의도).</summary>
public readonly record struct PendingQuestion(string Viewer, string Text, string Key, ChatIntent Intent, double AtMs);

/// <summary>
/// [PromptFlow Studio v16.48.0] 🎙️ 자율형 AI 라이브 쇼호스트 &amp; 세일즈 엔진.
///   ▸ 상품 DB(가격 · 옵션 · 소구점 · FAQ · 실시간 재고) 기반 런다운: 오프닝 → 소구점 강조 → 시연 → 특가 오퍼 → 상품 전환(품절 건너뜀) · 톤(에너지/친근/프리미엄/유머) · 에너지 0~1 → 멘트 간격.
///   ▸ 실시간 Q&amp;A: 채팅 의도 분류(가격/재고/옵션/배송/쿠폰/구매/FAQ) · FAQ 바이그램 유사도 매칭 · 중복 질문 병합 · 대기열 상한 · 런다운보다 우선(최소 간격 준수).
///   ▸ 커머스 이벤트 즉시 반응: 품절 → 비상 품절 멘트 + 다음 상품 전환 · 재고 임박 → 긴급 멘트 · 주문 폭주 → 폭주 멘트 · 구매 확정 → 닉네임 샤라웃(묶음 · 마스킹).
///   ▸ Gemini 톤 재작성(JSON 스키마 · 데드라인 · 숫자 보존 검증 — 가격/재고 환각 차단 · 실패 시 BCL) · v16.38 라이브 브릿지 립싱크 발화 · v16.36 디지털 트윈 호스트 바인딩.
///   ▸ Tick: 발화 시점 외 무할당.
/// </summary>
public sealed class AiLiveShowHostEngine : IDisposable
{
    public const int QueueCap = 20, HistoryCap = 200;
    public const double MinGapMs = 2500, ShoutGapMs = 6000;

    private readonly LiveCommerceInventoryOrderBridge _bridge;
    private readonly Func<string, CancellationToken, Task<string?>>? _llm;
    private readonly LiveVTuberBroadcastingBridge? _avatar;
    private readonly object _gate = new();
    private readonly List<HostLine> _history = new();
    private readonly List<PendingQuestion> _queue = new(QueueCap + 1);
    private readonly List<string> _buyers = new(64);
    private readonly Queue<HostLine> _urgent = new();
    private int _slot = -1, _step, _buyerExtra;
    private double _nextAt, _lastLineAt = double.NegativeInfinity, _lastShout = double.NegativeInfinity;
    private bool _live;
    private long _lines, _answers, _llmLines, _merged, _dropped;

    public AiLiveShowHostEngine(IServiceProvider sp)
        : this((LiveCommerceInventoryOrderBridge)sp.GetService(typeof(LiveCommerceInventoryOrderBridge))!,
               sp.GetService(typeof(GeminiClient)) is GeminiClient g ? async (p, ct) => { if (!g.Enabled) return null; var r = await g.GenerateAsync(p, null, 0.7, ct).ConfigureAwait(false); return r.Ok ? r.Text : null; } : null,
               sp.GetService(typeof(LiveVTuberBroadcastingBridge)) as LiveVTuberBroadcastingBridge) { }

    public AiLiveShowHostEngine(LiveCommerceInventoryOrderBridge bridge, Func<string, CancellationToken, Task<string?>>? llm = null, LiveVTuberBroadcastingBridge? avatar = null)
    {
        _bridge = bridge; _llm = llm; _avatar = avatar;
        _bridge.OnEvent += OnCommerceEvent;
    }

    public event Action? OnChange;
    public event Action<HostLine>? OnLine;

    public string HostName { get; set; } = "AI 쇼호스트 루나";
    public HostTone Tone { get; set; } = HostTone.Energetic;
    public float Energy { get; set; } = 0.7f;
    public string? CouponCode { get; set; }
    public int CouponPercent { get; set; }
    public bool UseLlm { get; set; } = true;
    public bool Speak { get; set; } = true;
    public int LlmDeadlineMs { get; set; } = 4000;
    public bool LlmAvailable => _llm is not null;
    public bool IsLive => _live;
    public int CurrentSlot => _slot;
    public string? TwinName { get; private set; }
    public long LinesSpoken => Interlocked.Read(ref _lines);
    public long Answers => Interlocked.Read(ref _answers);
    public long LlmLines => Interlocked.Read(ref _llmLines);
    public long MergedQuestions => Interlocked.Read(ref _merged);
    public long DroppedQuestions => Interlocked.Read(ref _dropped);
    public int QueueLength { get { lock (_gate) return _queue.Count; } }
    public IReadOnlyList<HostLine> History { get { lock (_gate) return _history.ToArray(); } }
    public HostLine? Last { get { lock (_gate) return _history.Count > 0 ? _history[^1] : null; } }
    public Task? PendingDelivery { get; private set; }
    public string? LastAction { get; private set; }

    public double StepMs => 9000 - 5000 * Math.Clamp(Energy, 0, 1);

    public void BindTwin(DigitalTwinAsset twin) { TwinName = twin.Name; LastAction = "🧍 쇼호스트 트윈 · " + twin.Name; Raise(); }

    // ══════════════════════ 방송 제어 ══════════════════════

    public void Start(double nowMs)
    {
        var first = NextInStock(-1);
        if (first < 0) throw new InvalidOperationException("판매 가능한 상품이 없습니다.");
        lock (_gate) { _live = true; _slot = first; _step = 0; _nextAt = nowMs; }
        LastAction = "🔴 쇼퍼테인먼트 방송 시작";
        Raise();
    }

    public void Stop(double nowMs)
    {
        if (!_live) return;
        Emit(nowMs, HostLineKind.Closing, _slot, Styled("오늘 방송 함께해 주셔서 감사합니다. 다음 라이브에서 또 만나요", FacialEmotion.HappySmile), FacialEmotion.HappySmile);
        lock (_gate) _live = false;
        LastAction = "⏹ 방송 종료";
        Raise();
    }

    public void SwitchTo(int slot, double nowMs)
    {
        if ((uint)slot >= (uint)_bridge.ProductCount) return;
        lock (_gate) { _slot = slot; _step = 0; _nextAt = nowMs; }
    }

    private int NextInStock(int from)
    {
        var n = _bridge.ProductCount;
        for (var k = 1; k <= n; k++) { var i = ((from < 0 ? -1 : from) + k) % n; if (!_bridge.IsSoldOut(i)) return i; }
        return -1;
    }

    /// <summary>주기 틱: 긴급 → 샤라웃 → 질문 → 런다운 순 발화(최소 간격). 반환 = 발화 여부. 발화 외 무할당.</summary>
    public bool Tick(double nowMs)
    {
        if (!_live || nowMs - _lastLineAt < MinGapMs) return false;
        HostLine? urgent = null;
        PendingQuestion q = default; var hasQ = false;
        var shout = false;
        lock (_gate)
        {
            if (_urgent.Count > 0) urgent = _urgent.Dequeue();
            else if (_buyers.Count > 0 && nowMs - _lastShout >= ShoutGapMs) shout = true;
            else if (_queue.Count > 0) { q = _queue[0]; _queue.RemoveAt(0); hasQ = true; }
            else if (nowMs < _nextAt) return false;
        }
        if (urgent is not null) { Publish(urgent with { TimeMs = nowMs }); return true; }
        if (shout) { ShoutOut(nowMs); return true; }
        if (hasQ) { Answer(q, nowMs); return true; }
        RundownStep(nowMs);
        return true;
    }

    private void RundownStep(double nowMs)
    {
        var slot = _slot;
        var p = _bridge.Product(slot);
        var stock = _bridge.StockOf(slot);
        var sp = p.SellingPoints;
        var step = _step;
        string text; HostLineKind kind; FacialEmotion emo;
        if (step == 0) { kind = HostLineKind.Intro; emo = FacialEmotion.Surprised; text = "지금 소개해 드릴 상품은 바로 " + p.Name + "입니다"; }
        else if (step <= sp.Count) { kind = HostLineKind.SellingPoint; emo = FacialEmotion.HappySmile; text = (step == 1 ? "첫 번째 포인트! " : step == sp.Count ? "마지막 포인트! " : "그리고 ") + sp[step - 1]; }
        else if (step == sp.Count + 1) { kind = HostLineKind.Demo; emo = FacialEmotion.Curious; text = "제가 직접 보여드릴게요. " + (p.Options.Count > 0 ? "옵션은 " + string.Join(", ", p.Options) + " 준비되어 있어요" : p.Name + " 실제 모습 화면으로 확인해 보세요"); }
        else if (step == sp.Count + 2) { kind = HostLineKind.Offer; emo = FacialEmotion.Surprised; text = OfferText(p, stock); }
        else
        {
            var next = NextInStock(slot);
            kind = HostLineKind.Transition; emo = FacialEmotion.HappySmile;
            text = next >= 0 && next != slot ? "다음 상품도 정말 좋아요. 이어서 " + _bridge.Product(next).Name + " 보여드릴게요" : p.Name + " 아직 구매 안 하셨다면 지금이 기회예요";
            lock (_gate) { if (next >= 0) _slot = next; _step = 0; _nextAt = nowMs + StepMs; }
            Emit(nowMs, kind, slot, Styled(text, emo), emo);
            return;
        }
        lock (_gate) { _step = step + 1; _nextAt = nowMs + StepMs; }
        Emit(nowMs, kind, slot, Styled(text, emo), emo);
    }

    public static string Won(decimal v) => v.ToString("#,0", CultureInfo.InvariantCulture) + "원";

    public static int DiscountPercent(CommerceProduct p) => p.SalePrice is { } s && p.Price > 0 && s < p.Price ? (int)Math.Round((p.Price - s) / p.Price * 100) : 0;

    private string OfferText(CommerceProduct p, int stock)
    {
        var sb = new StringBuilder();
        if (p.SalePrice is { } s && s < p.Price) sb.Append("오늘 방송 특가! 정가 ").Append(Won(p.Price)).Append("에서 ").Append(DiscountPercent(p).ToString(CultureInfo.InvariantCulture)).Append("% 할인된 ").Append(Won(s));
        else sb.Append("방송 중 구매가 ").Append(Won(p.Price));
        if (CouponCode is { Length: > 0 } && CouponPercent > 0) sb.Append(", 쿠폰 ").Append(CouponCode).Append(" 입력하시면 ").Append(CouponPercent.ToString(CultureInfo.InvariantCulture)).Append("% 추가 할인");
        sb.Append(". 남은 수량 ").Append(stock.ToString(CultureInfo.InvariantCulture)).Append("개, 화면의 QR 코드로 바로 구매하세요");
        return sb.ToString();
    }

    // ══════════════════════ 톤 ══════════════════════

    public string Styled(string text, FacialEmotion emo)
    {
        var e = Math.Clamp(Energy, 0, 1);
        return Tone switch
        {
            HostTone.Energetic => (e > 0.6f ? "여러분~! " : "") + text + (e > 0.8f ? "!!" : "!"),
            HostTone.Premium => text + ". 품격 있는 선택, 놓치지 마세요.",
            HostTone.Humorous => text + (emo == FacialEmotion.Surprised ? "! 이 가격 실화인가요?" : "! 제 지갑이 먼저 반응하네요."),
            _ => text + "요~ 편하게 물어봐 주세요.",
        };
    }

    // ══════════════════════ 채팅 Q&A ══════════════════════

    public static ChatIntent Classify(string text)
    {
        var t = text.ToLowerInvariant();
        static bool Any(string s, params string[] ks) { foreach (var k in ks) if (s.Contains(k, StringComparison.Ordinal)) return true; return false; }
        if (Any(t, "쿠폰", "할인코드", "coupon", "promo")) return ChatIntent.Coupon;
        if (Any(t, "얼마", "가격", "price", "how much", "원이")) return ChatIntent.Price;
        if (Any(t, "재고", "남았", "품절", "몇 개", "stock", "left")) return ChatIntent.Stock;
        if (Any(t, "사이즈", "색상", "컬러", "옵션", "size", "color")) return ChatIntent.Option;
        if (Any(t, "배송", "택배", "도착", "shipping", "delivery")) return ChatIntent.Shipping;
        if (Any(t, "살게", "구매", "주문", "어떻게 사", "buy", "order")) return ChatIntent.Purchase;
        return t.Contains('?') || Any(t, "요?", "나요", "까요", "인가") ? ChatIntent.Faq : ChatIntent.Other;
    }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>시청자 채팅 수신 → 질문이면 대기열(중복 병합 · 상한 초과 시 가장 오래된 질문 드롭). 반환 = 의도.</summary>
    public ChatIntent IngestChat(string viewer, string text, double nowMs)
    {
        var intent = Classify(text);
        if (intent == ChatIntent.Other) return intent;
        var key = Normalize(text);
        lock (_gate)
        {
            foreach (var q in _queue) if (q.Key == key) { Interlocked.Increment(ref _merged); return intent; }
            _queue.Add(new PendingQuestion(viewer, text, key, intent, nowMs));
            if (_queue.Count > QueueCap) { _queue.RemoveAt(0); Interlocked.Increment(ref _dropped); }
        }
        Raise();
        return intent;
    }

    /// <summary>FAQ 최적 매칭(바이그램 중첩 계수 ≥ 0.35).</summary>
    public static int MatchFaq(CommerceProduct p, string question, out double score)
    {
        score = 0; var best = -1;
        var qg = RealtimeNewsFeedFactCheckEngine.TitleGrams(question);
        for (var i = 0; i < p.Faqs.Count; i++)
        {
            var s = RealtimeNewsFeedFactCheckEngine.Overlap(qg, RealtimeNewsFeedFactCheckEngine.TitleGrams(p.Faqs[i].Q));
            if (s > score) { score = s; best = i; }
        }
        return score >= 0.35 ? best : -1;
    }

    /// <summary>BCL 답변(상품 사실만 사용 · 미확인 정보는 상세 페이지 안내).</summary>
    public string BuildAnswer(int slot, PendingQuestion q)
    {
        var p = _bridge.Product(slot);
        var stock = _bridge.StockOf(slot);
        var faq = MatchFaq(p, q.Text, out _);
        var who = LiveCommerceInventoryOrderBridge.MaskBuyer(q.Viewer) + "님, ";
        if (faq >= 0 && q.Intent is ChatIntent.Faq or ChatIntent.Shipping or ChatIntent.Option) return who + p.Faqs[faq].A;
        return q.Intent switch
        {
            ChatIntent.Price => who + p.Name + " " + (p.SalePrice is { } s && s < p.Price ? "방송 특가 " + Won(s) + "이에요! 정가 " + Won(p.Price) + "에서 " + DiscountPercent(p).ToString(CultureInfo.InvariantCulture) + "% 할인" : Won(p.Price) + "이에요"),
            ChatIntent.Stock => who + (stock <= 0 ? p.Name + " 아쉽게도 품절됐어요. 재입고 알림을 신청해 주세요" : "현재 " + p.Name + " 잔여 " + stock.ToString(CultureInfo.InvariantCulture) + "개 남았어요"),
            ChatIntent.Option => who + (p.Options.Count > 0 ? "옵션은 " + string.Join(", ", p.Options) + " 있어요" : "단일 옵션 상품이에요"),
            ChatIntent.Coupon => who + (CouponCode is { Length: > 0 } && CouponPercent > 0 ? "쿠폰 코드 " + CouponCode + " 입력하시면 " + CouponPercent.ToString(CultureInfo.InvariantCulture) + "% 추가 할인돼요" : "지금은 진행 중인 쿠폰이 없어요. 방송 특가를 확인해 주세요"),
            ChatIntent.Purchase => who + "화면 오른쪽 아래 QR 코드를 스캔하시면 바로 구매하실 수 있어요",
            ChatIntent.Shipping => who + "배송 일정은 상품 상세 페이지 배송 안내를 확인해 주세요",
            _ => who + (p.SellingPoints.Count > 0 ? "좋은 질문이에요! " + p.SellingPoints[0] : "상세 페이지에서 자세히 확인하실 수 있어요"),
        };
    }

    private void Answer(PendingQuestion q, double nowMs)
    {
        Interlocked.Increment(ref _answers);
        var text = BuildAnswer(_slot, q);
        Emit(nowMs, HostLineKind.Answer, _slot, text, FacialEmotion.Curious);
    }

    /// <summary>숫자 보존 검증: 원문의 모든 숫자열이 재작성문에 존재(가격/재고/할인율 환각 차단).</summary>
    public static bool PreservesNumbers(string original, string rewritten)
    {
        var digits = new StringBuilder();
        for (var i = 0; i <= original.Length; i++)
        {
            var c = i < original.Length ? original[i] : ' ';
            if (char.IsAsciiDigit(c) || c == ',' && digits.Length > 0) { digits.Append(c); continue; }
            if (digits.Length > 0)
            {
                var num = digits.ToString().TrimEnd(',');
                if (!rewritten.Contains(num, StringComparison.Ordinal) && !rewritten.Contains(num.Replace(",", "", StringComparison.Ordinal), StringComparison.Ordinal)) return false;
                digits.Clear();
            }
        }
        return true;
    }

    public static bool TryParseHostJson(string? text, out string line)
    {
        line = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = GeminiClient.StripFence(text);
        var a = s.IndexOf('{'); var b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return false;
        try
        {
            using var doc = JsonDocument.Parse(s[a..(b + 1)]);
            if (!doc.RootElement.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) return false;
            line = t.GetString()!.Trim();
            return line.Length is > 0 and <= 300;
        }
        catch (JsonException) { return false; }
    }

    // ══════════════════════ 커머스 이벤트 ══════════════════════

    private void OnCommerceEvent(CommerceEvent e)
    {
        if (!_live) return;
        switch (e.Kind)
        {
            case CommerceEventKind.Order:
                lock (_gate)
                {
                    if (_buyers.Count < 3) _buyers.Add(LiveCommerceInventoryOrderBridge.MaskBuyer(e.Buyer));
                    else _buyerExtra++;
                }
                break;
            case CommerceEventKind.SoldOut:
            {
                var p = _bridge.Product(e.Slot);
                var next = NextInStock(e.Slot);
                var text = "🚨 " + p.Name + " 방금 전량 품절됐습니다! 성원에 감사드려요" + (next >= 0 ? ". 바로 " + _bridge.Product(next).Name + "로 넘어갈게요" : "");
                Enqueue(new HostLine(e.TimeMs, HostLineKind.SoldOut, p.Sku, text, FacialEmotion.Surprised, "BCL"));
                if (e.Slot == _slot && next >= 0) lock (_gate) { _slot = next; _step = 0; _nextAt = e.TimeMs + MinGapMs; }
                break;
            }
            case CommerceEventKind.LowStock:
            {
                var p = _bridge.Product(e.Slot);
                Enqueue(new HostLine(e.TimeMs, HostLineKind.Urgency, p.Sku, Styled(p.Name + " 품절 임박! 단 " + e.Remaining.ToString(CultureInfo.InvariantCulture) + "개 남았어요. 서두르세요", FacialEmotion.Surprised), FacialEmotion.Surprised, "BCL"));
                break;
            }
            case CommerceEventKind.SurgeStart:
                Enqueue(new HostLine(e.TimeMs, HostLineKind.Surge, _slot >= 0 ? _bridge.Product(_slot).Sku : "", Styled("지금 주문이 폭주하고 있어요! 분당 " + _bridge.OrdersPerMinute.ToString(CultureInfo.InvariantCulture) + "건 돌파", FacialEmotion.Surprised), FacialEmotion.Surprised, "BCL"));
                break;
        }
    }

    private void Enqueue(HostLine l)
    {
        lock (_gate) { _urgent.Enqueue(l); while (_urgent.Count > 8) _urgent.Dequeue(); }
    }

    private void ShoutOut(double nowMs)
    {
        string text;
        lock (_gate)
        {
            text = string.Join(", ", _buyers) + "님" + (_buyerExtra > 0 ? " 외 " + _buyerExtra.ToString(CultureInfo.InvariantCulture) + "명" : "") + " 구매 감사합니다";
            _buyers.Clear(); _buyerExtra = 0; _lastShout = nowMs;
        }
        Emit(nowMs, HostLineKind.ShoutOut, _slot, Styled(text, FacialEmotion.HappySmile), FacialEmotion.HappySmile);
    }

    // ══════════════════════ 발화 ══════════════════════

    private void Emit(double nowMs, HostLineKind kind, int slot, string text, FacialEmotion emo)
        => Publish(new HostLine(nowMs, kind, (uint)slot < (uint)_bridge.ProductCount ? _bridge.Product(slot).Sku : "", text, emo, "BCL"));

    private void Publish(HostLine line)
    {
        lock (_gate)
        {
            _history.Add(line);
            if (_history.Count > HistoryCap) _history.RemoveAt(0);
            _lastLineAt = line.TimeMs;
        }
        Interlocked.Increment(ref _lines);
        LastAction = "🎙 " + line.Text;
        try { OnLine?.Invoke(line); } catch (Exception ex) { PromptFlowStudio.Program.Log("Host.OnLine", ex); }
        if ((_llm is not null && UseLlm && line.Kind is HostLineKind.Answer or HostLineKind.SellingPoint or HostLineKind.Intro) || (Speak && _avatar is not null))
            PendingDelivery = DeliverAsync(line);
        Raise();
    }

    /// <summary>Gemini 톤 재작성(숫자 보존 검증) → 기록 교체 · 아바타 발화.</summary>
    private async Task DeliverAsync(HostLine line)
    {
        var final = line;
        try
        {
            if (_llm is not null && UseLlm && line.Kind is HostLineKind.Answer or HostLineKind.SellingPoint or HostLineKind.Intro)
            {
                using var d = new CancellationTokenSource(LlmDeadlineMs);
                var prompt = "You are " + HostName + ", a Korean live-commerce show host. Tone: " + Tone + ", energy " + Energy.ToString("0.0", CultureInfo.InvariantCulture)
                    + ". Rewrite the line below for live broadcast in Korean, max 120 chars. Keep every number, price and product name EXACTLY. Never add new facts. Reply ONLY JSON {\"text\":\"...\"}.\nLine: " + line.Text;
                var text = await _llm(prompt, d.Token).ConfigureAwait(false);
                if (TryParseHostJson(text, out var t) && PreservesNumbers(line.Text, t))
                {
                    final = line with { Text = t, Source = "Gemini" };
                    Interlocked.Increment(ref _llmLines);
                    lock (_gate) { var i = _history.LastIndexOf(line); if (i >= 0) _history[i] = final; }
                }
            }
            if (Speak && _avatar is not null)
            {
                var perf = await _avatar.PrepareSpeechAsync(final.Text, final.Emotion, null).ConfigureAwait(false);
                _avatar.Speak(perf);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or InvalidOperationException) { PromptFlowStudio.Program.Log("Host.Deliver", ex); }
        Raise();
    }

    public static string KindLabel(HostLineKind k) => k switch
    {
        HostLineKind.Intro => "오프닝", HostLineKind.SellingPoint => "소구점", HostLineKind.Demo => "시연", HostLineKind.Offer => "특가", HostLineKind.Answer => "Q&A", HostLineKind.Urgency => "임박",
        HostLineKind.SoldOut => "품절", HostLineKind.Surge => "폭주", HostLineKind.ShoutOut => "샤라웃", HostLineKind.Transition => "전환", _ => "클로징",
    };

    public static List<CommerceProduct> DemoCatalog() => new()
    {
        new CommerceProduct
        {
            Sku = "GLOW-SERUM", Name = "글로우 비타민 세럼", Category = "뷰티", Price = 39000, SalePrice = 25900, Color = 0xF59E0B, InitialStock = 120, LowStockThreshold = 15,
            Options = { "30ml", "50ml" }, SellingPoints = { "비타민C 15% 고함량으로 칙칙한 피부 톤을 환하게", "끈적임 없는 워터 제형이라 아침 메이크업 전에도 OK", "피부과 테스트 완료, 민감 피부도 안심" },
            Faqs = { ("민감한 피부도 쓸 수 있나요", "피부과 자극 테스트를 완료한 제품이라 민감 피부도 사용하실 수 있어요"), ("하루에 몇 번 바르나요", "아침저녁 세안 후 2~3방울이면 충분해요"), ("배송은 언제 오나요", "오늘 주문하시면 내일 출발해요") },
            PurchaseUrl = "https://shop.example.com/p/glow-serum",
        },
        new CommerceProduct
        {
            Sku = "AIR-FRYER", Name = "스마트 에어프라이어 5L", Category = "주방", Price = 129000, SalePrice = 89000, Color = 0x0EA5E9, InitialStock = 40, LowStockThreshold = 8,
            Options = { "화이트", "블랙" }, SellingPoints = { "앱 연동으로 레시피 자동 조리", "5리터 대용량으로 통닭 한 마리 OK", "바스켓 분리 세척 가능" },
            Faqs = { ("소음이 큰가요", "저소음 모터라 대화하면서도 사용하실 수 있어요"), ("전기세 많이 나오나요", "1회 조리 기준 일반 오븐보다 전력 소모가 적어요") },
            PurchaseUrl = "https://shop.example.com/p/air-fryer",
        },
        new CommerceProduct
        {
            Sku = "RUN-SHOES", Name = "에어 쿠션 러닝화", Category = "패션", Price = 89000, SalePrice = 69000, Color = 0x22C55E, InitialStock = 60, LowStockThreshold = 10,
            Options = { "230", "240", "250", "260", "270", "280" }, SellingPoints = { "에어 쿠션이 무릎 충격을 흡수", "니트 갑피로 통기성 최고", "무게 210g 초경량" },
            Faqs = { ("사이즈는 정사이즈인가요", "정사이즈로 나왔어요. 발볼이 넓으시면 반 사이즈 업을 추천해요") },
            PurchaseUrl = "https://shop.example.com/p/run-shoes",
        },
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Host.OnChange", ex); } }

    public void Dispose() => _bridge.OnEvent -= OnCommerceEvent;
}
