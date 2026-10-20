using System;

namespace PromptFlowStudio.Services.Wan;

/// <summary>Wan 2.1 시네마틱 프롬프트 부스터 계약 — 입력 프롬프트에 고품질 시네마틱 태그를 자동 결합.</summary>
public interface IWanPromptBoosterService
{
    /// <summary>프롬프트 뒤에 시네마틱 태그를 붙인다(한글 원문 보존 + 영문 태그 바인딩). 이미 부스팅됐으면 그대로.</summary>
    string Boost(string? prompt);
    /// <summary>이미 부스팅된 프롬프트인지(중복 방지 판정).</summary>
    bool IsBoosted(string? prompt);
    /// <summary>붙는 시네마틱 태그 원문(미리보기용).</summary>
    string Tags { get; }
}

/// <summary>
/// [PromptFlowStudio] Wan 2.1 시네마틱 프롬프트 부스터 — 순수 BCL 문자열 처리(외부 NuGet 0).
///   ▸ 입력 프롬프트(한글/영문 무관) 뒤에 카메라/조명/화질 시네마틱 태그를 결합해 Wan 알고리즘 품질을 끌어올린다.
///   ▸ 한글 직배송 모드: 한국어 원문을 번역하지 않고 그대로 보존 + 영문 시네마틱 태그만 덧붙인다(Wan 다국어 처리).
///   ▸ 멱등(idempotent): 마지막 태그(<see cref="Marker"/>)가 이미 있으면 재부스팅하지 않아 중복 누적을 막는다.
/// </summary>
public sealed class WanPromptBoosterService : IWanPromptBoosterService
{
    private const string BoostTags =
        "8k resolution, cinematic lighting, highly detailed texture, 35mm lens, smooth camera movement, photorealistic masterwork";
    private const string Marker = "photorealistic masterwork";   // 중복 부스팅 감지(마지막 태그)

    /// <inheritdoc/>
    public string Tags => BoostTags;

    /// <inheritdoc/>
    public bool IsBoosted(string? prompt)
        => !string.IsNullOrEmpty(prompt) && prompt.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public string Boost(string? prompt)
    {
        var p = (prompt ?? "").Trim().TrimEnd(',', ' ', '\t');
        if (p.Length == 0) return BoostTags;   // 빈 프롬프트면 태그만
        if (IsBoosted(p)) return p;            // 이미 부스팅 → 그대로(중복 방지)
        return $"{p}, {BoostTags}";            // 원문(한/영) 보존 + 시네마틱 영문 태그
    }
}
