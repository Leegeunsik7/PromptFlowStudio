using System;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🔗 2-way 조합 시너지 스냅샷 — Score = 학습 배수(반감기 감쇠) × (1 − clamp(조합 감점, 0, 0.85)).</summary>
public sealed record PlutoPairSynergy(string PairKey, string First, string Second, double LearnedMultiplier, double PenaltyFactor, double Score)
{
    /// <summary>중립 조합(이력 없음).</summary>
    public static readonly PlutoPairSynergy Neutral = new(string.Empty, string.Empty, string.Empty, 1.0, 0.0, 1.0);
    /// <summary>강한 양(+)의 시너지(Score ≥ <see cref="PlutoPairAttribution.StrongSynergyScore"/>).</summary>
    public bool IsStrongSynergy => Score >= PlutoPairAttribution.StrongSynergyScore;
    /// <summary>역(−)시너지 — 조합 키에 오답 노트 감점 중.</summary>
    public bool IsAntiSynergy => PenaltyFactor > PlutoAdaptivePatternRanker.Epsilon;
}

/// <summary>
/// 🔗 2-way 상호작용 키 — <c>pair:{HookStyle}+{ColorLookId}</c> · <c>domain_hook:{Domain}+{HookStyle}</c>.
/// 학습 메모리(<c>pattern:</c> 접두)와 오답 노트가 같은 문자열을 공유한다. 길이 상한으로 오답 노트 토큰 절단(120자)과 무관하게 왕복 일치 보장.
/// </summary>
public static class PlutoPairAttribution
{
    public const string PairPrefix = "pair:";
    public const string DomainHookPrefix = "domain_hook:";
    public const char Separator = '+';
    /// <summary>훅 부분 최대 길이 — 접두 + 훅 + 구분자 + 룩/도메인이 <see cref="PlutoMistakeNotebookService.MaxTokenLength"/> 이내.</summary>
    public const int MaxHookPartLength = 72;
    public const int MaxSidePartLength = 32;
    /// <summary>강한 시너지 판정 점수(조합 배수 × 감점 반영).</summary>
    public const double StrongSynergyScore = 1.10;

    /// <summary><c>pair:{hook}+{look}</c> — 어느 한쪽이라도 비면 null.</summary>
    public static string? PairKey(string? hook, string? look)
    {
        var h = Part(hook, MaxHookPartLength);
        var l = Part(look, MaxSidePartLength);
        return h is null || l is null ? null : PairPrefix + h + Separator + l;
    }

    /// <summary><c>domain_hook:{domain}+{hook}</c> — 어느 한쪽이라도 비면 null.</summary>
    public static string? DomainHookKey(string? domain, string? hook)
    {
        var d = Part(domain, MaxSidePartLength);
        var h = Part(hook, MaxHookPartLength);
        return d is null || h is null ? null : DomainHookPrefix + d + Separator + h;
    }

    /// <summary>조합 키 여부(대소문자 무시) — 단일 특성 토큰(훅 · 태그 · 무드)과 분리 취급.</summary>
    public static bool IsCompositeKey(string? token)
        => token is not null && (token.StartsWith(PairPrefix, StringComparison.OrdinalIgnoreCase) || token.StartsWith(DomainHookPrefix, StringComparison.OrdinalIgnoreCase));

    /// <summary><c>pair:</c> 키 → (훅, 룩) — 룩 이름에는 '+'가 없으므로 마지막 구분자 기준.</summary>
    public static bool TrySplitPair(string? key, out string hook, out string look)
    {
        hook = look = string.Empty;
        if (key is null || !key.StartsWith(PairPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var body = key.AsSpan(PairPrefix.Length);
        var i = body.LastIndexOf(Separator);
        if (i <= 0 || i >= body.Length - 1) return false;
        hook = body[..i].ToString();
        look = body[(i + 1)..].ToString();
        return true;
    }

    /// <summary><c>domain_hook:</c> 키 → (도메인, 훅) — 도메인 이름에는 '+'가 없으므로 첫 구분자 기준.</summary>
    public static bool TrySplitDomainHook(string? key, out string domain, out string hook)
    {
        domain = hook = string.Empty;
        if (key is null || !key.StartsWith(DomainHookPrefix, StringComparison.OrdinalIgnoreCase)) return false;
        var body = key.AsSpan(DomainHookPrefix.Length);
        var i = body.IndexOf(Separator);
        if (i <= 0 || i >= body.Length - 1) return false;
        domain = body[..i].ToString();
        hook = body[(i + 1)..].ToString();
        return true;
    }

    /// <summary>조합 키 구성 요소 정규화(공백 정리 · 길이 절단) — 랭커 후보 라벨 비교에도 동일 규칙 사용.</summary>
    public static string? Part(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim();
        return v.Length > maxLength ? v[..maxLength] : v;
    }

    /// <summary>훅 부분 정규화(조합 키 내 훅과 비교용).</summary>
    public static string HookPart(string hook) => Part(hook, MaxHookPartLength) ?? string.Empty;

    /// <summary>룩/도메인 부분 정규화.</summary>
    public static string SidePart(string side) => Part(side, MaxSidePartLength) ?? string.Empty;
}
