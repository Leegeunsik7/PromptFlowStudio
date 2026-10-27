using System;
using System.Collections.Generic;
using System.Globalization;

namespace PromptFlowStudio.Services.Live;

public enum PromotionType { FlashSale, TimeAttack, LimitedEdition }

public enum LivePitchPhase { Hook, ProblemAgitation, SolutionIntro, SocialProof, CallToAction }

/// <summary>릴레이 세그먼트 종류 — 세일즈 피치 본편 · FAQ 응답 컷 · BGM 루프 컷.</summary>
public enum LiveSegmentKind { Pitch, Faq, BgmLoop }

public sealed record LiveProductSpec(
    string ProductName,
    decimal Price,
    int DiscountRate,
    List<string> KeySellingPoints,
    PromotionType PromotionType);

public sealed class LivePitchSegment
{
    public Guid SegmentId { get; init; } = Guid.NewGuid();
    public LivePitchPhase Phase { get; init; }
    public LiveSegmentKind Kind { get; init; } = LiveSegmentKind.Pitch;
    /// <summary>20~30초 분량 쇼호스트 대본(컴플라이언스 보정 완료).</summary>
    public string HostScript { get; init; } = "";
    /// <summary>실시간 렌더/가상 아바타 제스처용 Wan 2.1 프롬프트.</summary>
    public string VisualPrompt { get; init; } = "";
    public string RecommendedCameraMotion { get; init; } = "";
}

/// <summary>
/// [PromptFlowStudio] 4번 워크플로우 【24/7 AI 라이브 스튜디오】 쇼호스트 엔진 — 제품 스펙 → 5단계 세일즈 피치(Hook~CTA) 생성 + 24/7 릴레이 버퍼.
///   ▸ 대본·셀링포인트·비주얼 프롬프트는 <see cref="SanitizeClaims"/> 로 과장광고/금지어를 대체 표현으로 보정.
///   ▸ <see cref="GetNextSegment"/> 는 피치 소진 시 FAQ 응답 컷 ↔ BGM 루프 컷을 교대로 끼운 뒤 피치를 재적재 — 절대 null 을 반환하지 않음.
///   ⚠ 생성/릴레이 상태는 내부 잠금 하에서만 변경(UI 스레드 생성 · 타이머 스레드 진행 동시 접근).
/// </summary>
public sealed class LiveStudioHostService
{
    // 긴 표현 우선 — 부분 문자열이 먼저 치환되어 대체어가 깨지는 것을 방지.
    private static readonly (string Banned, string Replacement)[] ComplianceRules =
    {
        ("100% 완치", "꾸준한 관리에 도움"),
        ("100% 효과", "많은 분들이 체감한 효과"),
        ("무조건 최고", "많은 분들이 만족한"),
        ("부작용 없는", "자극을 줄인"),
        ("부작용 제로", "자극을 줄인"),
        ("평생 보장", "제조사 보증 기준 지원"),
        ("세계 최초", "새롭게 선보이는"),
        ("즉시 효과", "빠르게 느껴지는 변화"),
        ("기적의", "주목받는"),
        ("완치", "개선에 도움"),
        ("무조건", "충분히"),
        ("최고급", "프리미엄"),
    };

    private static readonly string[] CameraMotions = { "Dolly Zoom", "Handheld Push-In", "Orbit", "Slow Pan", "CloseUp" };

    private readonly object _gate = new();
    private readonly Queue<LivePitchSegment> _buffer = new();
    private LivePitchSegment[] _pitch = Array.Empty<LivePitchSegment>();
    private LivePitchSegment[] _faq = Array.Empty<LivePitchSegment>();
    private int _faqCursor;
    private long _cycle;
    private string _productName = "";

    /// <summary>피치 전체 1회 송출이 끝나 재적재된 횟수.</summary>
    public long CycleCount { get { lock (_gate) return _cycle; } }
    public int BufferDepth { get { lock (_gate) return _buffer.Count; } }

    public static bool ContainsViolation(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        for (var i = 0; i < ComplianceRules.Length; i++)
            if (text.Contains(ComplianceRules[i].Banned, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string SanitizeClaims(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var s = text;
        for (var i = 0; i < ComplianceRules.Length; i++)
            s = s.Replace(ComplianceRules[i].Banned, ComplianceRules[i].Replacement, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    public static decimal SalePrice(decimal price, int discountRate)
    {
        var rate = Math.Clamp(discountRate, 0, 100);
        return Math.Round(price * (100 - rate) / 100m, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>제품 스펙 → 5단계 세일즈 피치 생성 후 릴레이 버퍼를 새 시퀀스로 교체.</summary>
    public List<LivePitchSegment> GeneratePitchSequence(LiveProductSpec product)
    {
        ArgumentNullException.ThrowIfNull(product);
        var name = SanitizeClaims(string.IsNullOrWhiteSpace(product.ProductName) ? "오늘의 상품" : product.ProductName.Trim());
        var points = new List<string>();
        if (product.KeySellingPoints is not null)
            for (var i = 0; i < product.KeySellingPoints.Count; i++)
            {
                var p = product.KeySellingPoints[i];
                if (!string.IsNullOrWhiteSpace(p)) points.Add(SanitizeClaims(p.Trim()));
            }
        if (points.Count == 0) points.Add("일상에서 바로 체감하는 편리함");

        var rate = Math.Clamp(product.DiscountRate, 0, 100);
        var list = Won(Math.Max(0m, product.Price));
        var sale = Won(SalePrice(Math.Max(0m, product.Price), rate));
        var promo = PromoLabel(product.PromotionType);
        var p0 = points[0];
        var p1 = points.Count > 1 ? points[1] : p0;
        var p2 = points.Count > 2 ? points[2] : p1;
        var rateText = rate.ToString(CultureInfo.InvariantCulture);

        string[] scripts =
        {
            "여러분, 잠깐만 화면 고정해 주세요! 지금부터 단 한 번뿐인 " + promo + "이 시작됩니다. 오늘 소개할 주인공은 바로 " + name + "! "
                + "정가 " + list + "짜리를 " + rateText + "% 할인된 가격으로 만나실 수 있는 기회, 끝까지 함께해 주세요.",
            "혹시 이런 고민 해보신 적 있으시죠? 매번 아쉬운 제품에 돈만 쓰고, 기대만큼 만족하지 못했던 경험이요. "
                + "저도 똑같았어요. 그래서 오늘은 정말 꼼꼼하게 비교해 보고 자신 있게 들고 나왔습니다.",
            "그 해답이 바로 " + name + "입니다. 첫째, " + p0 + ". 둘째, " + p1 + ". 셋째, " + p2 + ". "
                + "직접 보여드릴게요, 이 디테일 한번 보세요. 사용해 보시면 차이를 바로 느끼실 거예요.",
            "벌써 채팅창에 구매 인증이 올라오고 있어요! 먼저 써보신 분들이 " + p0 + " 부분을 가장 많이 칭찬해 주셨고요, "
                + "재구매 문의도 꾸준히 들어오고 있습니다. 후기는 개인 사용 경험이며 결과는 사람마다 다를 수 있어요.",
            "자, 이제 결정하실 시간입니다! " + promo + " 특가 " + sale + ", 정가 대비 " + rateText + "% 할인! "
                + "수량과 시간이 정해져 있으니 지금 화면 아래 구매 버튼을 눌러 주세요. 놓치시면 다음 기회는 장담 못 드려요!",
        };
        string[] gestures =
        {
            "energetic opening gesture, pointing at camera with a bright smile",
            "empathetic expression, hand on chest, nodding slowly",
            "presenting the product with both hands, demonstrating key details",
            "reading live chat on a tablet, cheerful applause gesture",
            "urgent countdown gesture, pointing down at the buy button",
        };

        var segments = new List<LivePitchSegment>(5);
        for (var i = 0; i < scripts.Length; i++)
        {
            segments.Add(new LivePitchSegment
            {
                Phase = (LivePitchPhase)i,
                HostScript = SanitizeClaims(scripts[i]),
                VisualPrompt = SanitizeClaims("Wan2.1, virtual live-commerce show host avatar, " + gestures[i] + ", product \"" + name
                    + "\" on a studio display table, " + CameraMotions[i].ToLowerInvariant() + " shot, broadcast lighting, 1080p, 24fps"),
                RecommendedCameraMotion = CameraMotions[i],
            });
        }

        var faq = new[]
        {
            Faq("가격이 얼마인가요?", "지금 " + promo + " 특가로 " + sale + "입니다. 정가 " + list + " 대비 " + rateText + "% 할인된 가격이에요."),
            Faq("배송은 언제 오나요?", "주문 순서대로 순차 출고되며, 정확한 일정은 상품 상세 페이지의 배송 안내를 확인해 주세요."),
            Faq(name + " 어떤 점이 좋아요?", "가장 많이 사랑받는 포인트는 " + p0 + "입니다. 자세한 사양은 상세 페이지에서 확인하실 수 있어요."),
        };

        lock (_gate)
        {
            _productName = name;
            _pitch = segments.ToArray();
            _faq = faq;
            _faqCursor = 0;
            _cycle = 0;
            _buffer.Clear();
            for (var i = 0; i < _pitch.Length; i++) _buffer.Enqueue(_pitch[i]);
        }
        return segments;
    }

    /// <summary>
    /// 24/7 릴레이 — 버퍼가 비면 [FAQ 또는 BGM 루프 컷(사이클 교대)] + 피치 시퀀스를 재적재. 피치 미생성 시 BGM 루프만 반환.
    /// </summary>
    public LivePitchSegment GetNextSegment()
    {
        lock (_gate)
        {
            if (_buffer.Count == 0)
            {
                if (_pitch.Length == 0) return BgmLoop(_productName);
                var useFaq = _faq.Length > 0 && (_cycle & 1) == 0;
                if (useFaq)
                {
                    _buffer.Enqueue(_faq[_faqCursor]);
                    _faqCursor = (_faqCursor + 1) % _faq.Length;
                }
                else _buffer.Enqueue(BgmLoop(_productName));
                for (var i = 0; i < _pitch.Length; i++) _buffer.Enqueue(_pitch[i]);
                _cycle++;
            }
            return _buffer.Dequeue();
        }
    }

    private static LivePitchSegment Faq(string question, string answer) => new()
    {
        Phase = LivePitchPhase.SocialProof,
        Kind = LiveSegmentKind.Faq,
        HostScript = SanitizeClaims("채팅으로 질문 주셨어요. \"" + question + "\" " + answer),
        VisualPrompt = "Wan2.1, virtual live-commerce show host avatar, answering a viewer question while glancing at live chat, friendly nod, medium shot, broadcast lighting, 1080p",
        RecommendedCameraMotion = "Medium Shot",
    };

    private static LivePitchSegment BgmLoop(string productName) => new()
    {
        Phase = LivePitchPhase.Hook,
        Kind = LiveSegmentKind.BgmLoop,
        HostScript = string.IsNullOrEmpty(productName)
            ? "잠시 음악과 함께 쉬어 갈게요. 곧 다음 방송이 이어집니다."
            : "잠시 음악과 함께 " + productName + " 디테일 컷 보시죠. 곧 다시 특가 소개 이어갑니다!",
        VisualPrompt = "Wan2.1, seamless looping product b-roll, soft studio lights, slow turntable rotation, lo-fi background music mood, 1080p",
        RecommendedCameraMotion = "Slow Orbit",
    };

    private static string PromoLabel(PromotionType t) => t switch
    {
        PromotionType.TimeAttack => "타임어택 특가",
        PromotionType.LimitedEdition => "한정판 에디션",
        _ => "플래시 세일",
    };

    private static string Won(decimal v) => v.ToString("N0", CultureInfo.InvariantCulture) + "원";
}
