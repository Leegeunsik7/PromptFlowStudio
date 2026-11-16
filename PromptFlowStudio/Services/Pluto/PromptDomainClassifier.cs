using System;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>프롬프트 도메인 — 아기/육아 스튜디오 vs 일반 시네마틱(건축·풍경·제품·매크로·테크 등).</summary>
public enum PlutoPromptDomain
{
    GeneralCinematic = 0,
    BabyStudio = 1,
}

/// <summary>
/// 🧭 도메인 분류기(순수 BCL · 0B) — 아기/육아 토큰 포함 또는 사용자가 아기 칩(단계·무드·키워드)을 명시 선택한 경우만
///   <see cref="PlutoPromptDomain.BabyStudio"/>. 그 외 일반 프롬프트에는 아기 전용 네거티브/자막 기본값을 주입하지 않는다.
/// </summary>
public static class PromptDomainClassifier
{
    /// <summary>일반 시네마틱 네거티브 — 아기 얼굴 보호 토큰 없이 범용 품질 가드만.</summary>
    public const string GeneralCinematicNegative = "blurry, lowres, watermark, distorted geometry, flicker, text artifacts, overexposed";

    /// <summary>아기/육아 도메인 토큰(KO/EN · 대소문자 무시 부분 일치).</summary>
    public static readonly string[] BabyTokens =
    {
        "아기", "신생아", "백일", "첫돌", "육아", "맘",
        "baby", "newborn", "toddler", "infant", "milestone",
    };

    /// <summary>텍스트에 아기/육아 토큰이 포함되어 있는지.</summary>
    public static bool ContainsBabyToken(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var span = text.AsSpan();
        for (var i = 0; i < BabyTokens.Length; i++)
            if (span.IndexOf(BabyTokens[i].AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    /// <summary>도메인 판정 — 명시적 아기 칩 선택이 토큰 판정보다 우선.</summary>
    public static PlutoPromptDomain Classify(string? prompt, bool explicitBabySelection = false)
        => explicitBabySelection || ContainsBabyToken(prompt) ? PlutoPromptDomain.BabyStudio : PlutoPromptDomain.GeneralCinematic;

    /// <summary>
    /// 요청 네거티브 확정 — BabyStudio 는 사용자 네거티브 그대로(빈 값 → 빈 문자열),
    ///   GeneralCinematic 은 잔류 아기 얼굴 보호 가드를 제거하고 일반 시네마틱 네거티브를 1회 결합(사용자 문구는 앞에 보존).
    /// </summary>
    public static string ResolveNegativePrompt(string? prompt, string? userNegative, bool explicitBabySelection)
    {
        if (Classify(prompt, explicitBabySelection) == PlutoPromptDomain.BabyStudio) return userNegative ?? "";
        var user = StripBabyGuard(userNegative);
        if (user.Length == 0) return GeneralCinematicNegative;
        return user.Contains(GeneralCinematicNegative, StringComparison.Ordinal) ? user : user + ", " + GeneralCinematicNegative;
    }

    /// <summary>네거티브에서 아기 얼굴 보호 가드 구문 제거 + 꼬리/머리 구분자 정리.</summary>
    public static string StripBabyGuard(string? negative)
    {
        if (string.IsNullOrWhiteSpace(negative)) return "";
        var s = negative.Contains(BabyStudioMatrix.FacialSafetyNegative, StringComparison.Ordinal)
            ? negative.Replace(BabyStudioMatrix.FacialSafetyNegative, "", StringComparison.Ordinal)
            : negative;
        s = s.Trim().Trim(',').Trim();
        while (s.Contains(", ,", StringComparison.Ordinal)) s = s.Replace(", ,", ",", StringComparison.Ordinal);
        return s;
    }

    /// <summary>🔭 트렌드 기획안 도메인 — 프롬프트·제목 모두 아기 토큰이 없으면 GeneralCinematic(명시 아기 플래그 해제 기준).</summary>
    public static bool IsGeneralBrief(string? prompt, string? title) => !ContainsBabyToken(prompt) && !ContainsBabyToken(title);

    /// <summary>네거티브 박스가 기본 아기 얼굴 보호 템플릿만 담고 있는지(사용자 문구 0).</summary>
    public static bool IsDefaultBabyNegative(string? negative)
        => !string.IsNullOrWhiteSpace(negative)
           && negative.Contains(BabyStudioMatrix.FacialSafetyNegative, StringComparison.Ordinal)
           && StripBabyGuard(negative).Length == 0;

    /// <summary>
    /// 🔎 네거티브 박스 투명화 — GeneralCinematic 에서 박스가 기본 아기 템플릿뿐이면 박스 자체를 일반 시네마틱 네거티브로 교체(미리보기 null),
    ///   사용자 문구가 있거나 비어 있어 실제 전송값과 다르면 박스는 보존하고 <c>Preview</c> = 실제 전송 네거티브. BabyStudio 는 항상 그대로.
    /// </summary>
    public static (string Box, string? Preview) SyncNegativeBox(string? prompt, string? boxText, bool explicitBabySelection)
    {
        var box = boxText ?? "";
        if (Classify(prompt, explicitBabySelection) == PlutoPromptDomain.BabyStudio) return (box, null);
        if (IsDefaultBabyNegative(box)) return (GeneralCinematicNegative, null);
        var sent = ResolveNegativePrompt(prompt, box, explicitBabySelection);
        return string.Equals(sent, box.Trim(), StringComparison.Ordinal) ? (box, null) : (box, sent);
    }
}
