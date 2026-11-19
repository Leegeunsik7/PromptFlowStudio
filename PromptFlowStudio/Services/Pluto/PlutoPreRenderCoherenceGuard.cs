using System;
using System.Collections.Generic;
using System.Globalization;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧩 렌더 직전 교차 모달 정합성 감사 입력.</summary>
public sealed record PlutoPreRenderCoherenceRequest(PlutoPromptDomain PromptDomain, string? PromptText, string? NegativePrompt, IReadOnlyList<SubtitleCue>? SubtitleCues,
    string? HookStyle, string? ColorLookId, string? BgmMood, string? AspectRatio);

/// <summary>
/// 🧩 정합성 감사 결과 — 점수 0~100(100 = 모순 없음) · 자동 보정 여부 · 한국어 경고.
/// 보정본(<see cref="SanitizedNegativePrompt"/> · <see cref="SanitizedSubtitleCues"/>)은 항상 채워진다(보정 없음 → 입력 그대로).
/// </summary>
public sealed record PlutoCoherenceAuditReport(int CoherenceScore0To100, bool WasAutoSanitized, IReadOnlyList<string> WarningsKorean)
{
    public static readonly PlutoCoherenceAuditReport Clean = new(100, false, Array.Empty<string>());

    /// <summary>보정된 네거티브 프롬프트(누출 도메인 토큰 · 중복 구문 제거).</summary>
    public string SanitizedNegativePrompt { get; init; } = string.Empty;
    /// <summary>보정된 자막 큐(빈 큐 · 연속 중복 큐 제거 · 입력 null → 빈 목록).</summary>
    public IReadOnlyList<SubtitleCue> SanitizedSubtitleCues { get; init; } = Array.Empty<SubtitleCue>();
    /// <summary>자동 보정 내역(한국어).</summary>
    public IReadOnlyList<string> SanitizationsKorean { get; init; } = Array.Empty<string>();
    /// <summary>🚫 오답 노트 차단 조합/훅 사용(자동 보정 불가 · 사용자 판단 필요).</summary>
    public bool HasAntiSynergyConflict { get; init; }
    /// <summary>보정 또는 경고가 1건 이상.</summary>
    public bool HasFindings => WasAutoSanitized || WarningsKorean.Count > 0;

    /// <summary>🧩 배지 1줄(결정론 한국어) — 예: <c>🧩 정합성 82점 · 자동 보정 2건 · 경고 1건</c>.</summary>
    public string SummaryKorean
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var s = "🧩 정합성 " + CoherenceScore0To100.ToString(inv) + "점";
            if (SanitizationsKorean.Count > 0) s += " · 자동 보정 " + SanitizationsKorean.Count.ToString(inv) + "건";
            if (WarningsKorean.Count > 0) s += " · 경고 " + WarningsKorean.Count.ToString(inv) + "건";
            return HasFindings ? s : s + " · 모순 없음";
        }
    }
}

/// <summary>🧩 렌더 직전 교차 모달 정합성 · 후회 방지 게이트.</summary>
public interface IPlutoPreRenderCoherenceGuard
{
    /// <summary>감사(순수 · 동기 · 예외 미전파 — 내부 실패는 해당 검사만 생략).</summary>
    PlutoCoherenceAuditReport Audit(PlutoPreRenderCoherenceRequest request);
}

/// <summary>
/// 🧩 <see cref="IPlutoPreRenderCoherenceGuard"/> 구현(순수 BCL · 결정론 · 네트워크/디스크 0).
///   ▸ 자동 보정(복구 가능): GeneralCinematic 네거티브의 아기 가드/아기 토큰 구문 · 중복 네거티브 구문 · 빈/연속 중복 자막 큐.
///   ▸ 경고(사용자 판단): 프롬프트 ↔ 네거티브 자기모순 · 오답 노트 차단 훅/조합(역시너지) · 9:16 자막 폭 초과 · 읽기 속도 초과 ·
///     자막 시간 역전/겹침 · 도메인 ↔ BGM 불일치 · BabyStudio 얼굴 보호 가드 누락.
/// 점수 = 100 − Σ감점(하한 0). 보정된 항목도 감점한다(입력이 얼마나 어긋나 있었는지의 지표).
/// </summary>
public sealed class PlutoPreRenderCoherenceGuard : IPlutoPreRenderCoherenceGuard
{
    /// <summary>9:16 자막 1큐 최대 표시 폭(전각 = 2 · 반각 = 1) — 2줄 × 한글 18자.</summary>
    public const int VerticalCaptionMaxWidth = 72;
    /// <summary>16:9 자막 1큐 최대 표시 폭 — 2줄 × 한글 32자.</summary>
    public const int HorizontalCaptionMaxWidth = 128;
    /// <summary>읽기 속도 상한(표시 폭/초 · 한글 약 10자/초).</summary>
    public const double MaxReadingWidthPerSec = 20.0;
    public const int MaxWarnings = 12;

    public const int PenaltyDomainLeak = 12, PenaltyDuplicateNegative = 3, PenaltySelfContradiction = 15, PenaltyEmptyCue = 6, PenaltyDuplicateCue = 4,
        PenaltyCueTiming = 8, PenaltyCaptionOverflow = 8, PenaltyReadingSpeed = 5, PenaltyBlockedHook = 20, PenaltyAntiSynergy = 25, PenaltyBgmMismatch = 5,
        PenaltyMissingBabyGuard = 5, PenaltyDomainMismatch = 6;
    /// <summary>같은 종류 검사의 누적 감점 상한(큐 수십 개가 점수를 0으로 만들지 않도록).</summary>
    public const int PerCheckPenaltyCap = 24;

    private static readonly string[] BabyBgmKeywords = { "lullaby", "musicbox", "music box", "자장가", "오르골" };
    private static readonly string[] HighEnergyBgmKeywords = { "energetic", "hype", "dance", "신나" };

    private readonly IPlutoMistakeNotebookService? _notebook;

    public PlutoPreRenderCoherenceGuard(IPlutoMistakeNotebookService? notebook = null) => _notebook = notebook;

    public PlutoCoherenceAuditReport Audit(PlutoPreRenderCoherenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var warnings = new List<string>(4);
        var fixes = new List<string>(4);
        var penalty = 0;
        var general = request.PromptDomain == PlutoPromptDomain.GeneralCinematic;

        // ① 네거티브 — 도메인 누출 · 중복 · 자기모순
        var negative = SanitizeNegative(request.NegativePrompt, general, fixes, ref penalty);
        AuditSelfContradiction(request.PromptText, negative, warnings, ref penalty);
        if (!general && negative.Length > 0 && !negative.Contains(BabyStudioMatrix.FacialSafetyNegative, StringComparison.Ordinal))
        {
            Warn(warnings, "아기 스튜디오 도메인인데 얼굴 보호 네거티브가 없습니다 — 얼굴 왜곡 위험");
            penalty += PenaltyMissingBabyGuard;
        }
        if (general && PromptDomainClassifier.ContainsBabyToken(request.PromptText))
        {
            Warn(warnings, "일반 시네마틱 도메인으로 지정됐지만 프롬프트에 아기/육아 토큰이 있습니다 — 도메인 확인 필요");
            penalty += PenaltyDomainMismatch;
        }

        // ② 자막 큐 — 빈/중복 보정 · 시간/폭/읽기 속도 경고
        var cues = SanitizeCues(request.SubtitleCues, fixes, ref penalty);
        AuditCues(cues, IsVertical(request.AspectRatio), warnings, ref penalty);

        // ③ 오답 노트 역시너지(자동 보정 불가)
        var anti = AuditAntiSynergy(request, warnings, ref penalty);

        // ④ 도메인 ↔ BGM 무드
        if (!string.IsNullOrWhiteSpace(request.BgmMood))
        {
            if (general && ContainsAny(request.BgmMood, BabyBgmKeywords))
            {
                Warn(warnings, "일반 시네마틱 영상에 아기용 BGM 무드('" + request.BgmMood.Trim() + "')가 선택됐습니다");
                penalty += PenaltyBgmMismatch;
            }
            else if (!general && ContainsAny(request.BgmMood, HighEnergyBgmKeywords))
            {
                Warn(warnings, "아기 스튜디오 영상에 고에너지 BGM 무드('" + request.BgmMood.Trim() + "')가 선택됐습니다");
                penalty += PenaltyBgmMismatch;
            }
        }

        return new PlutoCoherenceAuditReport(Math.Clamp(100 - penalty, 0, 100), fixes.Count > 0, warnings)
        {
            SanitizedNegativePrompt = negative,
            SanitizedSubtitleCues = cues,
            SanitizationsKorean = fixes,
            HasAntiSynergyConflict = anti,
        };
    }

    /// <summary>세로(9:16) 판정 — <c>9:16</c> · <c>720*1280</c> · <c>1080x1920</c> 등 세로 &gt; 가로.</summary>
    public static bool IsVertical(string? aspectRatio)
    {
        if (string.IsNullOrWhiteSpace(aspectRatio)) return false;
        var p = aspectRatio.Trim().Split(new[] { ':', '*', 'x', 'X', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return p.Length == 2 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
               && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) && w > 0 && h > w;
    }

    /// <summary>표시 폭(전각/한글/CJK/이모지 = 2 · 그 외 = 1 · 제어 문자 = 0).</summary>
    public static int DisplayWidth(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var w = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v < 0x20 || v == 0x200D || (v >= 0xFE00 && v <= 0xFE0F)) continue;
            w += v >= 0x1100 && (v <= 0x115F || (v >= 0x2E80 && v <= 0xA4CF) || (v >= 0xAC00 && v <= 0xD7A3) || (v >= 0xF900 && v <= 0xFAFF)
                                 || (v >= 0xFE30 && v <= 0xFE4F) || (v >= 0xFF00 && v <= 0xFF60) || (v >= 0xFFE0 && v <= 0xFFE6) || v >= 0x1F300) ? 2 : 1;
        }
        return w;
    }

    private static string SanitizeNegative(string? raw, bool general, List<string> fixes, ref int penalty)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var text = raw;
        var leaked = 0;
        if (general && text.Contains(BabyStudioMatrix.FacialSafetyNegative, StringComparison.Ordinal))
        {
            text = PromptDomainClassifier.StripBabyGuard(text);
            leaked++;
        }
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var kept = new List<string>(parts.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (general && PromptDomainClassifier.ContainsBabyToken(part)) { leaked++; continue; }
            if (!seen.Add(part)) { duplicates++; continue; }
            kept.Add(part);
        }
        if (leaked > 0)
        {
            fixes.Add("일반 시네마틱 네거티브에 섞인 아기 도메인 구문 " + leaked.ToString(CultureInfo.InvariantCulture) + "건 제거");
            penalty += Math.Min(PerCheckPenaltyCap, PenaltyDomainLeak * leaked);
        }
        if (duplicates > 0)
        {
            fixes.Add("중복 네거티브 구문 " + duplicates.ToString(CultureInfo.InvariantCulture) + "건 정리");
            penalty += Math.Min(PerCheckPenaltyCap, PenaltyDuplicateNegative * duplicates);
        }
        return leaked == 0 && duplicates == 0 ? raw.Trim() : string.Join(", ", kept);
    }

    /// <summary>네거티브 구문(2단어 이상 또는 6자 이상)이 프롬프트에 그대로 등장 → 요청과 금지가 충돌.</summary>
    private static void AuditSelfContradiction(string? prompt, string negative, List<string> warnings, ref int penalty)
    {
        if (string.IsNullOrWhiteSpace(prompt) || negative.Length == 0) return;
        var parts = negative.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = 0;
        for (var i = 0; i < parts.Length && hits < 2; i++)
        {
            var part = parts[i];
            if (part.Length < 6 && part.IndexOf(' ') < 0) continue;   // 'blur' 같은 짧은 단어는 부분 일치 오탐이 많다
            if (prompt.Contains(part, StringComparison.OrdinalIgnoreCase))
            {
                Warn(warnings, "프롬프트가 요청한 '" + part + "'을(를) 네거티브가 금지합니다 — 한쪽을 지우세요");
                hits++;
            }
        }
        penalty += PenaltySelfContradiction * hits;
    }

    private static IReadOnlyList<SubtitleCue> SanitizeCues(IReadOnlyList<SubtitleCue>? cues, List<string> fixes, ref int penalty)
    {
        if (cues is null || cues.Count == 0) return Array.Empty<SubtitleCue>();
        var kept = new List<SubtitleCue>(cues.Count);
        int empty = 0, dup = 0;
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            if (string.IsNullOrWhiteSpace(c.Text) && string.IsNullOrWhiteSpace(c.SecondaryText)) { empty++; continue; }
            if (kept.Count > 0 && string.Equals(Norm(kept[^1].Text), Norm(c.Text), StringComparison.Ordinal)
                && string.Equals(Norm(kept[^1].SecondaryText), Norm(c.SecondaryText), StringComparison.Ordinal))
            {
                // 연속 중복 → 앞 큐의 표시 구간을 뒤 큐 끝까지 연장(화면 공백 없이 1큐로 병합)
                var prev = kept[^1];
                if (c.EffectiveEndSec > prev.EffectiveEndSec)
                    kept[^1] = prev.StartPreciseSec is not null || c.EndPreciseSec is not null
                        ? SubtitleCue.Precise(prev.EffectiveStartSec, c.EffectiveEndSec, prev.Text, prev.SecondaryText)
                        : prev with { EndSec = c.EndSec };
                dup++;
                continue;
            }
            kept.Add(c);
        }
        if (empty > 0)
        {
            fixes.Add("빈 자막 큐 " + empty.ToString(CultureInfo.InvariantCulture) + "건 제거");
            penalty += Math.Min(PerCheckPenaltyCap, PenaltyEmptyCue * empty);
        }
        if (dup > 0)
        {
            fixes.Add("연속 중복 자막 큐 " + dup.ToString(CultureInfo.InvariantCulture) + "건 병합");
            penalty += Math.Min(PerCheckPenaltyCap, PenaltyDuplicateCue * dup);
        }
        return empty == 0 && dup == 0 ? cues : kept;
    }

    private static string Norm(string? s) => string.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();

    private static void AuditCues(IReadOnlyList<SubtitleCue> cues, bool vertical, List<string> warnings, ref int penalty)
    {
        if (cues.Count == 0) return;
        var inv = CultureInfo.InvariantCulture;
        var maxWidth = vertical ? VerticalCaptionMaxWidth : HorizontalCaptionMaxWidth;
        int timing = 0, overflow = 0, fast = 0, firstOverflow = -1, firstTiming = -1, firstFast = -1, worstWidth = 0;
        for (var i = 0; i < cues.Count; i++)
        {
            var c = cues[i];
            var dur = c.EffectiveEndSec - c.EffectiveStartSec;
            var bad = !(dur > 0) || c.EffectiveStartSec < 0 || (i > 0 && c.EffectiveStartSec < cues[i - 1].EffectiveEndSec - 1e-6);
            if (bad) { timing++; if (firstTiming < 0) firstTiming = i; }
            var width = Math.Max(DisplayWidth(c.Text), DisplayWidth(c.SecondaryText));
            if (width > maxWidth) { overflow++; if (width > worstWidth) { worstWidth = width; firstOverflow = i; } }
            else if (dur > 0 && width / dur > MaxReadingWidthPerSec) { fast++; if (firstFast < 0) firstFast = i; }
        }
        if (timing > 0)
        {
            Warn(warnings, "자막 큐 " + timing.ToString(inv) + "건의 표시 구간이 역전되거나 앞 큐와 겹칩니다(첫 발생 #" + (firstTiming + 1).ToString(inv) + ")");
            penalty += Math.Min(PerCheckPenaltyCap, PenaltyCueTiming * timing);
        }
        if (overflow > 0)
        {
            Warn(warnings, (vertical ? "9:16 세로 화면" : "가로 화면") + "에서 자막 " + overflow.ToString(inv) + "건이 안전 폭을 넘습니다(#" + (firstOverflow + 1).ToString(inv)
                + " 폭 " + worstWidth.ToString(inv) + " > " + maxWidth.ToString(inv) + ") — 잘림/3줄 넘침 위험");
            penalty += Math.Min(PerCheckPenaltyCap, PenaltyCaptionOverflow * overflow);
        }
        if (fast > 0)
        {
            Warn(warnings, "자막 " + fast.ToString(inv) + "건이 읽기 속도 상한을 넘습니다(첫 발생 #" + (firstFast + 1).ToString(inv) + ") — 표시 시간을 늘리거나 문장을 줄이세요");
            penalty += Math.Min(PerCheckPenaltyCap, PenaltyReadingSpeed * fast);
        }
    }

    private bool AuditAntiSynergy(PlutoPreRenderCoherenceRequest r, List<string> warnings, ref int penalty)
    {
        if (_notebook is null || string.IsNullOrWhiteSpace(r.HookStyle)) return false;
        var found = false;
        try
        {
            var composite = _notebook.GetSuppressedCompositeKeys();
            if (composite is { Count: > 0 })
            {
                if (PlutoPairAttribution.PairKey(r.HookStyle, r.ColorLookId) is { } pair && composite.Contains(pair))
                {
                    Warn(warnings, "오답 노트가 차단한 조합입니다 — 훅 '" + r.HookStyle.Trim() + "' × 룩 '" + r.ColorLookId!.Trim() + "'(과거 부진) · 룩 또는 훅 교체 권장");
                    penalty += PenaltyAntiSynergy;
                    found = true;
                }
                if (PlutoPairAttribution.DomainHookKey(r.PromptDomain.ToString(), r.HookStyle) is { } dh && composite.Contains(dh))
                {
                    Warn(warnings, "이 도메인(" + r.PromptDomain + ")에서 부진했던 훅입니다 — '" + r.HookStyle.Trim() + "' 교체 권장");
                    penalty += PenaltyAntiSynergy;
                    found = true;
                }
            }
            var hook = r.HookStyle.Trim();
            if (hook.Length > PlutoMistakeNotebookService.MaxTokenLength) hook = hook[..PlutoMistakeNotebookService.MaxTokenLength];
            var single = _notebook.GetSuppressedTokens();
            if (single is { Count: > 0 } && single.Contains(hook))
            {
                Warn(warnings, "오답 노트가 차단한 훅입니다 — '" + hook + "'(3초 이탈/저성과 누적) · 다른 훅 권장");
                penalty += PenaltyBlockedHook;
                found = true;
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Coherence] anti-synergy: " + ex.Message); }   // [부분 구제] 이 검사만 생략
        return found;
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        for (var i = 0; i < keywords.Length; i++)
            if (text.Contains(keywords[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void Warn(List<string> warnings, string message)
    {
        if (warnings.Count < MaxWarnings) warnings.Add(message);
    }
}
