using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.Publisher;
using PromptFlowStudio.Services.StationMaster;

namespace PromptFlowStudio.Services.VisualPolish;

public enum AbVariant : byte { A, B }
public enum ThumbnailMood : byte { Joy, Surprise, Hype, Calm }

/// <summary>v17.74.0 A/B 메타데이터 쌍(배포 대기열 항목) · CTR 계측.</summary>
public sealed class AbVariantPair
{
    public int ReelId { get; init; }
    public ThumbnailMood Mood { get; init; }
    public SocialPostMetadata A { get; } = new();
    public SocialPostMetadata B { get; } = new();
    public long ImpressionsA, ImpressionsB, ClicksA, ClicksB;
    public AbVariant? Chosen { get; internal set; }
    public double CtrA => ImpressionsA > 0 ? (double)ClicksA / ImpressionsA : 0;
    public double CtrB => ImpressionsB > 0 ? (double)ClicksB / ImpressionsB : 0;
    public SocialPostMetadata Get(AbVariant v) => v == AbVariant.A ? A : B;
}

/// <summary>
/// [PromptFlow Studio v17.74.0] 🧪 CTR 극대화 A/B 테스트 메타데이터 생성기.
///   ▸ 변형 A = 호기심 유발형(질문 · 반전 · 미완결 훅: "이게 진짜 가능하다고?") · 변형 B = 정보 제공형(숫자 · 방법 · 결과: "3초 만에 끝내는 법") —
///     영상 문맥(v17.71 <see cref="ShortformContext"/> · 클라이맥스 원인 · 상품 · 길이) + 썸네일 분위기(골든 프레임 표정: 미소 = Joy · 입벌림/눈크게 = Surprise · 부스트 = Hype)로 템플릿 선택(결정적 해시).
///   ▸ 해시태그: 변형별 성향 세트(#소름 #실화 … / #꿀팁 #방법 …) + 문맥 태그 → v17.71 정규화(정확히 5 · # 접두 · 중복 제거) · 제목 ≤ 100 · #Shorts.
///   ▸ 배포 대기열(링 32) · CTR 계측(노출/클릭) → 베타 사후 P(B &gt; A)(정규 근사) · 최소 노출 이상이고 확신 ≥ 95 % 면 승자 자동 확정.
/// </summary>
public sealed class AbTestingMetadataVariantGenerator
{
    public const int QueueCap = 32, MinImpressions = 200;
    public const double Confidence = 0.95;

    public static readonly string[] CuriosityHooks =
    {
        "이게 진짜 가능하다고?", "끝까지 보면 소름 돋는 이유", "아무도 예상 못 한 마지막 3초", "방금 무슨 일이 일어난 거죠?", "이 장면 실화 맞나요?", "왜 다들 이 순간에 멈췄을까",
    };
    public static readonly string[] InfoHooks =
    {
        "3초 만에 끝내는 법", "핵심만 {0}초 요약", "{1} 제대로 쓰는 3가지 방법", "라이브 하이라이트 TOP 1 정리", "이 순간 반응 {2}배 터진 비결", "초보도 바로 따라 하는 방법",
    };
    public static readonly string[] CuriosityTags = { "#소름", "#실화", "#반전", "#레전드", "#궁금증" };
    public static readonly string[] InfoTags = { "#꿀팁", "#정보", "#방법", "#요약", "#하이라이트" };

    private readonly AbVariantPair?[] _q = new AbVariantPair?[QueueCap];
    private readonly AiContentTrendRecommendationSynthesizer _norm = new();
    private int _head, _n;

    public long Generated { get; private set; }
    public long Decided { get; private set; }
    public int Count => _n;
    public AbVariantPair? At(int i) => (uint)i < (uint)_n ? _q[(_head - 1 - i + 2 * QueueCap) % QueueCap] : null;
    public AbVariantPair? Find(int reelId) { for (var i = 0; i < _n; i++) if (At(i)?.ReelId == reelId) return At(i); return null; }

    public static string MoodName(ThumbnailMood m) => m switch { ThumbnailMood.Joy => "웃음", ThumbnailMood.Surprise => "놀람", ThumbnailMood.Hype => "열광", _ => "차분" };

    /// <summary>골든 프레임 표정 → 썸네일 분위기.</summary>
    public static ThumbnailMood MoodOf(GoldenFrameSnapshot? g)
    {
        if (g is null || g.Avatars == 0) return ThumbnailMood.Calm;
        var w = g.Face.AsSpan(g.PeakAvatar * GoldenFrameSnapshot.K, GoldenFrameSnapshot.K);
        var smile = 0.5f * (w[(int)ArkitBlendshape.MouthSmileLeft] + w[(int)ArkitBlendshape.MouthSmileRight]);
        var surprise = w[(int)ArkitBlendshape.JawOpen] + 0.5f * (w[(int)ArkitBlendshape.EyeWideLeft] + w[(int)ArkitBlendshape.EyeWideRight]);
        if (g.Boost > 0.6f && g.Boost >= smile && g.Boost >= surprise) return ThumbnailMood.Hype;
        if (smile < 0.15f && surprise < 0.15f) return ThumbnailMood.Calm;
        return smile >= surprise ? ThumbnailMood.Joy : ThumbnailMood.Surprise;
    }

    private static int Hash(int a, int b, int c) { unchecked { var h = (uint)(a * 73856093) ^ (uint)(b * 19349663) ^ (uint)(c * 83492791); h ^= h >> 13; h *= 0x5bd1e995; return (int)(h & 0x7FFFFFFF); } }

    /// <summary>A/B 쌍 생성 → 대기열 보관.</summary>
    public AbVariantPair Generate(int reelId, ShortformContext ctx, ThumbnailMood mood)
    {
        var p = new AbVariantPair { ReelId = reelId, Mood = mood };
        var h = Hash(reelId, ctx.Cause, (int)ctx.Program);
        // ── A: 호기심 ──
        var ca = mood switch { ThumbnailMood.Surprise => 3, ThumbnailMood.Hype => 1, ThumbnailMood.Joy => 5, _ => h % CuriosityHooks.Length };
        var hookA = CuriosityHooks[(ca + h % 2) % CuriosityHooks.Length];
        var subj = ctx.Product is { Length: > 0 } pr ? pr : MasterStationEpgSchedulerEngine.KindName(ctx.Program);
        p.A.Title = hookA + " " + (ctx.Cause == 1 ? "😱" : ctx.Cause == 2 ? "🛒" : "🔥");
        p.A.Description = "\"" + (ctx.KeyLine.Length > 0 ? ctx.KeyLine[..Math.Min(ctx.KeyLine.Length, 60)] : ctx.Caption) + "\" — 끝까지 보면 이유가 나옵니다. 라이브 원본은 채널에서!";
        // ── B: 정보 ──
        var ib = ctx.Product is { Length: > 0 } ? 2 : ctx.Cause == 2 ? 4 : ctx.Cause == 1 ? 3 : h % InfoHooks.Length;
        var hookB = string.Format(System.Globalization.CultureInfo.InvariantCulture, InfoHooks[ib], (int)Math.Clamp(ctx.DurationSec, 3, 60), subj, Math.Max(2, (int)(ctx.Peak / 30)));
        p.B.Title = subj + " · " + hookB;
        p.B.Description = subj + " 하이라이트 " + ((int)ctx.DurationSec).ToString(System.Globalization.CultureInfo.InvariantCulture) + "초 정리: 바이럴 지수 " + ((int)ctx.Peak).ToString(System.Globalization.CultureInfo.InvariantCulture)
            + (ctx.UnitsSold > 0 ? " · 판매 " + ctx.UnitsSold.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개" : "") + (ctx.Hits > 0 ? " · 물리 개입 " + ctx.Hits.ToString(System.Globalization.CultureInfo.InvariantCulture) + "회" : "") + ".";
        Normalize(p.A, ctx, CuriosityTags, h);
        Normalize(p.B, ctx, InfoTags, h >> 3);
        _q[_head] = p; _head = (_head + 1) % QueueCap; if (_n < QueueCap) _n++;
        Generated++;
        return p;
    }

    private void Normalize(SocialPostMetadata m, ShortformContext ctx, string[] lean, int h)
    {
        _norm.Reset();
        for (var i = 0; i < 3; i++) _norm.AddTag(lean[(h + i) % lean.Length]);
        _norm.AddTag(LocalHeuristicContentLlm.ProgramTags[(int)ctx.Program]);
        _norm.AddTag(LocalHeuristicContentLlm.CategoryTags[(int)ctx.Category]);
        _norm.Normalize(ctx);
        m.TagCount = _norm.TagCount;
        for (var i = 0; i < _norm.TagCount; i++) m.Tags[i] = new string(_norm.Tag(i));
        var t = m.Title.Contains("#Shorts", StringComparison.OrdinalIgnoreCase) ? m.Title : m.Title + " #Shorts";
        if (t.Length > 100) t = m.Title[..(100 - 8)] + " #Shorts";
        m.Title = t;
    }

    // ══════════════════════ CTR 계측 · 승자 판정 ══════════════════════
    public void RecordImpression(AbVariantPair p, AbVariant v, long n = 1) { if (v == AbVariant.A) p.ImpressionsA += n; else p.ImpressionsB += n; }
    public void RecordClick(AbVariantPair p, AbVariant v, long n = 1) { if (v == AbVariant.A) p.ClicksA += n; else p.ClicksB += n; }

    /// <summary>P(CTR_B &gt; CTR_A) — 베타(1+클릭, 1+미클릭) 사후 정규 근사.</summary>
    public static double ProbabilityBBeatsA(AbVariantPair p) => ProbabilityBBeatsA(p.ClicksA, p.ImpressionsA, p.ClicksB, p.ImpressionsB);

    /// <summary>v17.75 계수 직접 판정(요인 주변합 · 무할당).</summary>
    public static double ProbabilityBBeatsA(long clicksA, long impressionsA, long clicksB, long impressionsB)
    {
        static (double M, double V) Beta(long c, long n) { double a = 1 + c, b = 1 + Math.Max(0, n - c), s = a + b; return (a / s, a * b / (s * s * (s + 1))); }
        var (ma, va) = Beta(clicksA, impressionsA); var (mb, vb) = Beta(clicksB, impressionsB);
        var z = (mb - ma) / Math.Sqrt(Math.Max(va + vb, 1e-12));
        return 0.5 * (1 + Erf(z / Math.Sqrt(2)));
    }

    private static double Erf(double x)
    {
        var t = 1 / (1 + 0.3275911 * Math.Abs(x));
        var y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return x >= 0 ? y : -y;
    }

    /// <summary>승자 자동 확정(최소 노출 · 확신 95 %) — 확정 시 변형 반환.</summary>
    public AbVariant? Decide(AbVariantPair p)
    {
        if (p.Chosen is { } c) return c;
        if (p.ImpressionsA < MinImpressions || p.ImpressionsB < MinImpressions) return null;
        var pb = ProbabilityBBeatsA(p);
        AbVariant? w = pb >= Confidence ? AbVariant.B : pb <= 1 - Confidence ? AbVariant.A : null;
        if (w is { } v) { p.Chosen = v; Decided++; }
        return w;
    }

    public void Choose(AbVariantPair p, AbVariant v) { p.Chosen = v; Decided++; }
}
