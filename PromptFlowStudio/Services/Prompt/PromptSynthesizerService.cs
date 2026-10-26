using System;
using System.Collections.Generic;
using System.Text;

namespace PromptFlowStudio.Services.Prompt;

/// <summary>프롬프트 합성 입력 — 사용자 텍스트 + 카메라/스타일 칩 + 피사체 고정 + 네거티브 + 아기 스튜디오 옵션.</summary>
public sealed record PromptContext(
    string? UserInput,
    string? CameraMotion = null,
    string? GraphicStyle = null,
    string? SubjectIdentity = null,
    bool IsIdentityLocked = false,
    string? NegativeInput = null,
    bool AutoBabyModifier = false);

/// <summary>합성 결과 — 최종 포지티브/네거티브 · 포지티브 글자 수 · 예산(800자) 초과로 절삭됐는지.</summary>
public sealed record SynthesizedResult(string FinalPositivePrompt, string FinalNegativePrompt, int CharacterCount, bool IsOverBudget);

/// <summary>
/// [PromptFlowStudio] 1번 워크플로우 프롬프트 합성기 — 토큰 충돌 방지 · Wan 2.1 표준 구문 결합(순수 BCL).
///   ▸ 순서: <c>[Subject: name]</c>(피사체 고정 시 최우선) → 메인 프롬프트 → 카메라 모션 → 그래픽 스타일.
///   ▸ 사용자 문장에 이미 들어 있는 카메라/스타일 토큰은 재결합하지 않음(중복·충돌 방지).
///   ▸ 포지티브는 <see cref="MaxCharacterLimit"/>자 예산 — 초과 시 꼬리를 쉼표/공백 경계에서 절삭하고 <c>IsOverBudget</c>=true.
///   ▸ 네거티브: 기본 왜곡 방지 → (옵션) 아기 테마 → 사용자 정의, <see cref="StringComparer.OrdinalIgnoreCase"/> 중복 제거.
/// </summary>
public sealed class PromptSynthesizerService
{
    /// <summary>Wan 2.1 프롬프트 최대 글자 수(입력창 maxlength 와 동일).</summary>
    public const int MaxCharacterLimit = 800;

    /// <summary>항상 결합되는 기본 왜곡 방지 네거티브 토큰.</summary>
    public static readonly string[] DefaultNegativeTokens = { "distorted face", "mutated limbs", "low quality" };

    /// <summary>아기 스튜디오 매트릭스 옵션 활성 시 추가되는 네거티브 토큰.</summary>
    public static readonly string[] BabyNegativeTokens = { "adult facial features", "harsh shadows", "scary expression", "unsafe objects" };

    private const string Separator = ", ";

    public SynthesizedResult Synthesize(PromptContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        var sb = new StringBuilder(256);
        var main = ctx.UserInput?.Trim() ?? "";

        if (ctx.IsIdentityLocked && !string.IsNullOrWhiteSpace(ctx.SubjectIdentity))
            sb.Append("[Subject: ").Append(ctx.SubjectIdentity.Trim()).Append(']');

        AppendPart(sb, main);

        var cam = ctx.CameraMotion?.Trim();
        if (!string.IsNullOrEmpty(cam) && !Contains(main, cam))
            AppendPart(sb, "camera motion: " + cam);

        var sty = ctx.GraphicStyle?.Trim();
        if (!string.IsNullOrEmpty(sty) && !Contains(main, sty))
            AppendPart(sb, sty + " render style");

        var over = sb.Length > MaxCharacterLimit;
        var positive = over ? Truncate(sb.ToString()) : sb.ToString();

        return new SynthesizedResult(positive, BuildNegative(ctx), positive.Length, over);
    }

    private static void AppendPart(StringBuilder sb, string part)
    {
        if (part.Length == 0) return;
        if (sb.Length > 0) sb.Append(Separator);
        sb.Append(part);
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Length > 0 && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>예산 내 마지막 쉼표/공백 경계에서 절삭(경계가 너무 앞이면 하드 컷) — 앞쪽 [Subject] 토큰은 항상 보존.</summary>
    private static string Truncate(string s)
    {
        var cut = s.AsSpan(0, MaxCharacterLimit);
        var b = cut.LastIndexOfAny(',', ' ');
        if (b >= MaxCharacterLimit / 2) cut = cut[..b];
        return cut.TrimEnd(", ").ToString();
    }

    private static string BuildNegative(PromptContext ctx)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder(160);
        AddTokens(sb, seen, DefaultNegativeTokens);
        if (ctx.AutoBabyModifier) AddTokens(sb, seen, BabyNegativeTokens);
        if (!string.IsNullOrWhiteSpace(ctx.NegativeInput))
            AddTokens(sb, seen, ctx.NegativeInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return sb.ToString();
    }

    private static void AddTokens(StringBuilder sb, HashSet<string> seen, string[] tokens)
    {
        foreach (var t in tokens)
        {
            if (t.Length == 0 || !seen.Add(t)) continue;
            if (sb.Length > 0) sb.Append(Separator);
            sb.Append(t);
        }
    }
}
