using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.AI;

/// <summary>
/// 🛡️ 프리플라이트 프롬프트 보정 결과 — 원문 · 보정본 · 병합 네거티브 · 1줄 한국어 요약 · 적용 규칙 목록.
/// <see cref="UsedFallback"/>=true 면 로컬 규칙 엔진(오프라인/키 없음/호출·스키마 실패).
/// </summary>
public sealed record PromptPreflightResult(
    string RawPrompt,
    string RefinedPrompt,
    string MergedNegative,
    bool UsedFallback,
    string SummaryKo,
    IReadOnlyList<string> Changes)
{
    /// <summary>보정으로 프롬프트가 바뀌었는지.</summary>
    public bool Changed => !string.Equals(RawPrompt.Trim(), RefinedPrompt.Trim(), StringComparison.Ordinal);

    /// <summary>🧠 적용된 플루토 학습 패턴(👍 레시피 · 👎 회피 규칙 · seed 제외) 수.</summary>
    public int LearnedRulesApplied { get; init; }
}

/// <summary>
/// [PromptFlowStudio] 도메인 무관 범용 프리플라이트 프롬프트 디렉터 — 순수 BCL · 결정적 로컬 규칙 엔진.
/// 장르(인물 · 라이프스타일 · 제품 · 자연 · SF · 애니메이션 · 스포츠 · 추상 배경)에 무관하게 다음 4개 범용 규칙만 적용한다.
///   ① 동작-길이 보정: 과밀 다중 이벤트를 클립 길이(5s/10s/15s/30s)에 맞는 비트 수로 압축(첫 절 · 마지막 결과 절 보존).
///   ② 피사체 · 의상/오브젝트 · 카메라 앵커: 프레이밍 미지정 시 eye-level medium-wide, 외형/재질/색 일관, 안정 조명(얼굴 가림 글레어 금지)
///      — 앰비언트 루프면 고정 와이드 + 로우키 네거티브 스페이스.
///   ③ 인과 · 물리 연속성: 자연스러운 순방향 물리, 일관된 접촉/궤적, 결과 반응(환호 · 완료 · 감정 전환)은 동작 완료 후에만.
///   ④ 범용 안티 아티팩트 네거티브: 기존 네거티브와 중복 없이 병합.
/// 모든 규칙은 멱등(이미 보정된 프롬프트를 다시 넣어도 동일 결과).
/// </summary>
public static class PromptPreflightDirector
{
    /// <summary>범용 안티 아티팩트 네거티브 용어.</summary>
    public static readonly string[] AntiArtifactTerms =
    {
        "extra limbs", "distorted hands", "duplicate objects", "morphing artifacts", "reverse motion", "harsh lens flare", "text", "watermark",
    };

    public const string CameraAnchor = "eye-level medium-wide shot, stable camera framing";
    public const string AmbientCameraAnchor = "static locked-off wide shot, slow steady camera drift";
    public const string SubjectAnchor = "consistent primary subject appearance, wardrobe and object colors and materials throughout";
    public const string LightingAnchor = "stable soft natural lighting, no face-obscuring glare";
    public const string AmbientLightingAnchor = "low-key lighting with calm negative space";
    public const string PhysicsAnchor = "natural forward motion and physics, consistent object contact and trajectory";
    public const string OutcomeOrderAnchor = "the outcome reaction happens only after the action is fully completed";
    public const string LocalNotice = "로컬 규칙 엔진으로 보정되었습니다";
    public const string NoChangeSummary = "🛡️ 보정 불필요 — 원문 유지";

    private static readonly string[] FramingKeys =
    {
        "eye-level", "eye level", "medium shot", "medium-wide", "wide shot", "close-up", "closeup", "close up", "full shot", "full body shot", "low angle",
        "high angle", "aerial", "overhead", "top-down", "macro", "establishing shot", "over-the-shoulder", "pov", "locked-off", "tracking shot", "dolly",
        "클로즈업", "와이드", "풀샷", "미디엄", "항공", "부감", "로우 앵글", "하이 앵글", "아이레벨", "매크로",
    };

    private static readonly string[] LightingKeys =
    {
        "lighting", "light", "sunlight", "sunset", "sunrise", "golden hour", "neon", "backlit", "low-key", "high-key", "studio lit", "candlelit", "moonlight",
        "조명", "햇빛", "역광", "노을", "네온", "자연광", "달빛",
    };

    private static readonly string[] OutcomeKeys =
    {
        "celebrat", "cheer", "react", "victory", "wins", "triumph", "smiles after", "finish", "complet", "success", "applause", "relief", "tears of joy",
        "환호", "축하", "세리머니", "완성", "성공", "기뻐", "승리",
    };

    /// <summary>클립 길이 → 물리적으로 달성 가능한 최대 시각 비트 수(5s=2 · 10s=3 · 15s=4 · 30s=6).</summary>
    public static int MaxBeatsFor(int targetDurationSeconds) => targetDurationSeconds switch
    {
        <= 5 => 2,
        <= 10 => 3,
        <= 15 => 4,
        _ => 6,
    };

    /// <summary>
    /// 동작-길이 보정 — 묘사/스타일 절은 전부 보존하고, 동작 절(<see cref="MultiDurationPlanner.IsActionClause"/>)만
    /// 중복 제거 후 최대 비트를 넘으면 첫 동작 · 마지막(결과) 동작을 보존하고 중간을 균등 샘플링.
    /// 반환 (보정 절 목록, 원래 동작 수(중복 포함), 보존 동작 수, 문장 단위 분할 여부).
    /// </summary>
    public static (List<string> Clauses, int OriginalActions, int KeptActions, bool BySentence) CalibrateBeats(string? prompt, int targetDurationSeconds)
    {
        var all = MultiDurationPlanner.SplitClauses(prompt, false, out var bySentence);
        var originalActions = 0;
        foreach (var c in all) if (MultiDurationPlanner.IsActionClause(c)) originalActions++;
        var clauses = MultiDurationPlanner.SplitClauses(prompt, true, out _);
        var actionIdx = new List<int>(clauses.Count);
        for (var i = 0; i < clauses.Count; i++) if (MultiDurationPlanner.IsActionClause(clauses[i])) actionIdx.Add(i);
        var max = MaxBeatsFor(targetDurationSeconds);
        if (actionIdx.Count <= max) return (clauses, originalActions, actionIdx.Count, bySentence);

        var keep = new bool[clauses.Count];
        for (var i = 0; i < keep.Length; i++) keep[i] = true;
        foreach (var i in actionIdx) keep[i] = false;
        keep[actionIdx[0]] = true;
        keep[actionIdx[^1]] = true;
        var middle = max - 2;
        var span = actionIdx.Count - 2;
        for (var k = 0; k < middle; k++)
            keep[actionIdx[1 + (int)((long)k * span / middle)]] = true;
        var kept = new List<string>(clauses.Count);
        var keptActions = 0;
        for (var i = 0; i < clauses.Count; i++)
            if (keep[i]) { kept.Add(clauses[i]); if (MultiDurationPlanner.IsActionClause(clauses[i])) keptActions++; }
        return (kept, originalActions, keptActions, bySentence);
    }

    private static readonly string[] AllAnchors = { CameraAnchor, AmbientCameraAnchor, SubjectAnchor, LightingAnchor, AmbientLightingAnchor, PhysicsAnchor, OutcomeOrderAnchor };

    /// <summary>이전 보정이 붙인 앵커 토큰 제거(멱등 재보정용).</summary>
    public static string StripAnchors(string? prompt)
    {
        var s = prompt ?? "";
        foreach (var a in AllAnchors)
        {
            s = s.Replace(", " + a, "", StringComparison.OrdinalIgnoreCase);
            s = s.Replace(a, "", StringComparison.OrdinalIgnoreCase);
        }
        return s.Trim().TrimEnd(',', ' ');
    }

    /// <summary>로컬 결정적 보정(도메인 무관 · 멱등 · 절대 throw 하지 않음).</summary>
    public static PromptPreflightResult RefineLocal(string? rawPrompt, int targetDurationSeconds, string? aspectRatio, bool isAmbientLoop, string? existingNegative = null)
        => RefineLocal(rawPrompt, targetDurationSeconds, aspectRatio, isAmbientLoop, existingNegative, null);

    /// <summary>디렉터 범용 앵커 문구(의 일부)인지(대소문자 무시).</summary>
    public static bool IsDirectorAnchor(string? clause)
    {
        if (string.IsNullOrWhiteSpace(clause)) return false;
        var c = clause.Trim();
        foreach (var a in AllAnchors)
            if (a.Contains(c, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>🧠 학습 앵커 → 디렉터 범용 앵커와 겹치는 부분을 뺀 추가 구문(빈 문자열 = 추가 없음).</summary>
    public static string LearnedAnchorExtra(string? anchor) => StripAnchors(anchor).TrimStart(',', ' ').TrimEnd(',', '.', ' ');

    /// <summary>기존 네거티브 + 🧠 학습 네거티브 용어(콤마 결합 · 중복 제거는 <see cref="MergeNegative"/>).</summary>
    public static string? WithLearnedNegative(string? existing, PlutoLearnedGuidance? learned)
    {
        if (learned is null || learned.NegativeTerms.Count == 0) return existing;
        var sb = new StringBuilder(existing ?? "", 256);
        foreach (var t in learned.NegativeTerms) { if (sb.Length > 0) sb.Append(", "); sb.Append(t); }
        return sb.ToString();
    }

    /// <summary>로컬 결정적 보정 + 🧠 플루토 학습 가이던스(👍 레시피 · 👎 회피 앵커/네거티브) 주입 — 학습 앵커도 재보정 시 제거 후 재부착(멱등).</summary>
    public static PromptPreflightResult RefineLocal(string? rawPrompt, int targetDurationSeconds, string? aspectRatio, bool isAmbientLoop, string? existingNegative,
        PlutoLearnedGuidance? learned)
    {
        var raw = rawPrompt ?? "";
        var src = raw.Trim();
        var merged = MergeNegative(WithLearnedNegative(existingNegative, learned), out var negAdded);
        if (src.Length == 0) return new PromptPreflightResult(raw, raw, merged, true, NoChangeSummary, Array.Empty<string>());

        var changes = new List<string>(6);
        var stripped = StripLearned(StripAnchors(src), learned);
        var (clauses, originalActions, keptActions, bySentence) = CalibrateBeats(stripped, targetDurationSeconds);
        string body;
        if (keptActions < originalActions && clauses.Count > 0)
        {
            body = JoinClauses(clauses, bySentence);
            changes.Add("동작 " + originalActions.ToString(System.Globalization.CultureInfo.InvariantCulture) + "→" + keptActions.ToString(System.Globalization.CultureInfo.InvariantCulture) + "비트 압축");
        }
        else body = stripped.TrimEnd(',', '.', ' ');

        var sb = new StringBuilder(body.Length + 512).Append(body);
        var lower = body.ToLowerInvariant();
        if (!ContainsAny(lower, FramingKeys) && AppendOnce(sb, isAmbientLoop ? AmbientCameraAnchor : CameraAnchor)) changes.Add("카메라 프레이밍 고정");
        if (AppendOnce(sb, SubjectAnchor)) changes.Add("피사체·의상 일관");
        if (isAmbientLoop)
        {
            if (AppendOnce(sb, AmbientLightingAnchor)) changes.Add("로우키 네거티브 스페이스");
        }
        else if (!ContainsAny(lower, LightingKeys) && AppendOnce(sb, LightingAnchor)) changes.Add("조명 안정화");
        if (AppendOnce(sb, PhysicsAnchor)) changes.Add("물리 연속성");
        if (ContainsAny(lower, OutcomeKeys) && AppendOnce(sb, OutcomeOrderAnchor)) changes.Add("인과 순서(결과는 동작 후)");
        var learnedApplied = learned?.LearnedCount ?? 0;
        if (learned is not null)
        {
            foreach (var a in learned.PromptAnchors)
            {
                var extra = LearnedAnchorExtra(a);
                if (extra.Length > 0) AppendOnce(sb, extra);
            }
            if (learnedApplied > 0) changes.Add(LearnedLabel(learnedApplied));
        }
        if (negAdded > 0) changes.Add("네거티브 +" + negAdded.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var refined = Cap(sb.ToString());
        if (string.Equals(refined, src.TrimEnd(',', '.', ' '), StringComparison.Ordinal)) refined = src;
        return new PromptPreflightResult(raw, refined, merged, true, Summarize(changes), changes) { LearnedRulesApplied = learnedApplied };
    }

    /// <summary>"🧠 학습 패턴 N건" 변경 라벨.</summary>
    public static string LearnedLabel(int n) => "🧠 학습 패턴 " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + "건";

    private static string StripLearned(string s, PlutoLearnedGuidance? learned)
    {
        if (learned is null) return s;
        foreach (var a in learned.PromptAnchors)
        {
            var extra = LearnedAnchorExtra(a);
            if (extra.Length == 0) continue;
            s = s.Replace(", " + extra, "", StringComparison.OrdinalIgnoreCase).Replace(extra, "", StringComparison.OrdinalIgnoreCase);
        }
        return s.Trim().TrimEnd(',', ' ');
    }

    /// <summary>1줄 한국어 요약 배지 문구.</summary>
    public static string Summarize(IReadOnlyList<string> changes)
    {
        if (changes.Count == 0) return NoChangeSummary;
        var sb = new StringBuilder(96).Append("🛡️ 보정: ");
        for (var i = 0; i < changes.Count; i++) { if (i > 0) sb.Append(" · "); sb.Append(changes[i]); }
        return sb.ToString();
    }

    /// <summary>기존 네거티브 + 안티 아티팩트 용어(대소문자 무시 중복 제거 · 순서 보존). <paramref name="added"/> = 새로 붙은 용어 수.</summary>
    public static string MergeNegative(string? existing, out int added)
    {
        var terms = new List<string>(16);
        if (!string.IsNullOrWhiteSpace(existing))
            foreach (var t in existing.Split(','))
            {
                var s = t.Trim();
                if (s.Length > 0 && !ContainsTerm(terms, s)) terms.Add(s);
            }
        added = 0;
        foreach (var t in AntiArtifactTerms)
            if (!ContainsTerm(terms, t)) { terms.Add(t); added++; }
        return string.Join(", ", terms);
    }

    /// <summary>AI 응답 JSON(<c>{"prompt","negative","summary_ko"}</c>) 스키마 검증 — 실패 null.</summary>
    public static (string Prompt, string? Negative, string? SummaryKo)? ParseAiResponse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var text = json.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var a = text.IndexOf('{');
            var b = text.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            text = text[a..(b + 1)];
        }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("prompt", out var p) || p.ValueKind != JsonValueKind.String) return null;
            var prompt = (p.GetString() ?? "").Trim();
            if (prompt.Length < 8) return null;
            string? neg = root.TryGetProperty("negative", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            string? sum = root.TryGetProperty("summary_ko", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return (prompt, neg, sum);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>AI 지시문(도메인 무관 4개 규칙 · JSON 스키마).</summary>
    public static string BuildInstruction(int targetDurationSeconds, string? aspectRatio, bool isAmbientLoop)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return "You are a pre-flight video prompt director for a text-to-video model. The prompt may be about ANY subject (people, lifestyle, products, nature, sci-fi, animation, sports, abstract backgrounds); never apply genre-specific rules. "
            + "Rewrite it in English so it renders coherently as ONE clip of " + targetDurationSeconds.ToString(inv) + " seconds at aspect ratio " + (string.IsNullOrWhiteSpace(aspectRatio) ? "16:9" : aspectRatio.Trim()) + ". Rules: "
            + "1) Action-to-duration calibration: condense overcrowded multi-event descriptions to at most " + MaxBeatsFor(targetDurationSeconds).ToString(inv) + " physically achievable, sequential visual beats so actions never smear or repeat. "
            + "2) Anchoring: state a clear camera framing (eye-level/medium/wide), keep the primary subject's appearance, wardrobe and object colors/materials consistent, "
            + (isAmbientLoop ? "use low-key lighting with calm negative space and a static or very slow camera for a seamless ambient loop. " : "use stable lighting without face-obscuring glare. ")
            + "3) Cause-and-effect: natural forward physics, consistent object contact and trajectory; outcome reactions (celebration, completion, emotional shift) happen only after the action is completed. "
            + "4) Keep the user's intent and subject; do not add new characters or brands. "
            + "Respond ONLY with JSON: {\"prompt\": \"refined English prompt\", \"negative\": \"comma-separated negative terms\", \"summary_ko\": \"one short Korean line describing what was refined\"}.";
    }

    /// <summary>AI 지시문 + 🧠 플루토 학습 가이던스(성공 레시피 · 과거 실패 회피 규칙).</summary>
    public static string BuildInstruction(int targetDurationSeconds, string? aspectRatio, bool isAmbientLoop, PlutoLearnedGuidance? learned)
    {
        var baseText = BuildInstruction(targetDurationSeconds, aspectRatio, isAmbientLoop);
        if (learned is null || learned.IsEmpty) return baseText;
        var sb = new StringBuilder(baseText, baseText.Length + 512).Append(" 5) Learned director memory from this user's past renders: ");
        if (learned.PromptAnchors.Count > 0)
        {
            sb.Append("apply these proven recipes and failure-avoidance rules: ");
            for (var i = 0; i < learned.PromptAnchors.Count; i++) { if (i > 0) sb.Append("; "); sb.Append(learned.PromptAnchors[i]); }
            sb.Append(". ");
        }
        if (learned.NegativeTerms.Count > 0)
        {
            sb.Append("Include in negative: ");
            for (var i = 0; i < learned.NegativeTerms.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(learned.NegativeTerms[i]); }
            sb.Append('.');
        }
        return sb.ToString();
    }

    /// <summary>AI 결과 확정 — 네거티브 병합(안티 아티팩트 보장) · 길이 상한 · 요약 1줄.</summary>
    public static PromptPreflightResult FromAi(string rawPrompt, string aiPrompt, string? aiNegative, string? summaryKo, string? existingNegative)
        => FromAi(rawPrompt, aiPrompt, aiNegative, summaryKo, existingNegative, null);

    /// <summary>AI 결과 확정 + 🧠 학습 앵커/네거티브 보장(AI 가 누락해도 부착).</summary>
    public static PromptPreflightResult FromAi(string rawPrompt, string aiPrompt, string? aiNegative, string? summaryKo, string? existingNegative, PlutoLearnedGuidance? learned)
    {
        var applied = learned?.LearnedCount ?? 0;
        if (applied > 0)
        {
            var sbp = new StringBuilder(aiPrompt.Trim(), aiPrompt.Length + 256);
            foreach (var a in learned!.PromptAnchors)
            {
                var extra = LearnedAnchorExtra(a);
                if (extra.Length > 0) AppendOnce(sbp, extra);
            }
            aiPrompt = sbp.ToString();
        }
        existingNegative = WithLearnedNegative(existingNegative, learned);
        var baseNeg = string.IsNullOrWhiteSpace(existingNegative) ? aiNegative : existingNegative + (string.IsNullOrWhiteSpace(aiNegative) ? "" : ", " + aiNegative);
        var merged = MergeNegative(baseNeg, out _);
        var line = (summaryKo ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (line.Length > 80) line = line[..80].TrimEnd() + "…";
        var summary = line.Length == 0 ? "🛡️ AI 보정 완료" : "🛡️ AI 보정: " + line;
        if (applied > 0) summary += " · " + LearnedLabel(applied);
        return new PromptPreflightResult(rawPrompt, Cap(aiPrompt.Trim()), merged, false, summary,
            applied > 0 ? new[] { "AI 디렉터 보정", LearnedLabel(applied) } : new[] { "AI 디렉터 보정" })
        { LearnedRulesApplied = applied };
    }

    private static string JoinClauses(List<string> clauses, bool bySentence)
    {
        var sb = new StringBuilder(256);
        for (var i = 0; i < clauses.Count; i++)
        {
            if (i > 0) sb.Append(bySentence ? ". " : ", ");
            sb.Append(clauses[i]);
        }
        return sb.ToString();
    }

    private static bool AppendOnce(StringBuilder sb, string tokens)
    {
        if (sb.ToString().Contains(tokens, StringComparison.OrdinalIgnoreCase)) return false;
        if (sb.Length > 0) sb.Append(", ");
        sb.Append(tokens);
        return true;
    }

    private static bool ContainsAny(string lower, string[] keys)
    {
        foreach (var k in keys) if (lower.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool ContainsTerm(List<string> terms, string t)
    {
        foreach (var x in terms) if (string.Equals(x, t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string Cap(string s)
        => s.Length <= PromptSynthesizerService.MaxCharacterLimit ? s : s[..PromptSynthesizerService.MaxCharacterLimit].TrimEnd();
}
