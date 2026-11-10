using System;
using System.Collections.Generic;
using System.Text;
using PromptFlowStudio.Services.AI;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🗜 로컬 게이트웨이용 압축 프롬프트 — <c>System</c> = 불변 접두(규칙+스키마) · <c>User</c> = <c>---</c> + 동적 컨텍스트 + 요청.</summary>
public readonly record struct PlutoCompactPrompt(string System, string User);

/// <summary>
/// 🗜 Pluto 프롬프트 토큰 다이어트 + KV-캐시 정렬 — 3B~7B 로컬 SLM(llama.cpp/Ollama 계열) TTFT 최소화.
///   ▸ 알려진 Pluto 지시문 → 저토큰 규칙 + 컴팩트 JSON 스키마(키·열거값은 원본과 동일 → 파서 호환).
///   ▸ 불변 우선 배치: <c>[STATIC RULE + SCHEMA]</c>(system, 입력과 무관하게 바이트 동일) → <c>---</c> → <c>[CTX]</c> 페르소나 앵커 → <c>[REQ]</c> 메모/요청.
///     앵커는 같은 아이 프로필에서 반복 고정이므로 메모보다 앞에 둬 캐시 재사용 구간을 연장.
///   ▸ Gemini(클라우드)는 원본 서술형 지시문 유지 — 게이트웨이 단계에서만 적용(<see cref="PlutoLlmBridge"/>).
/// </summary>
public static class PlutoPromptCompressor
{
    public const string Separator = "---";
    public const string ContextHeader = "[CTX]";
    public const string RequestHeader = "[REQ]";

    private const string Acts = "Acts: hook 0-4s, climax 4-11s, smile 11-15s. ";
    private const string Bgm3 = "\"lullaby|musicbox|acoustic\"";

    private static readonly Dictionary<string, string> CompactSystems = new(StringComparer.Ordinal)
    {
        [PlutoCopilot.LlmScriptInstruction] =
            "Pluto: memo -> 15s 3-act baby/pet short. " + Acts + "JSON only:\n" +
            "{\"hookTitle\":\"KO+1 emoji<=40\",\"milestone\":\"KO\",\"acts\":[{\"label\":\"KO\",\"prompt\":\"EN t2v\"}]x3,\"camera\":\"EN\",\"bgm\":" + Bgm3 + "}",
        [PlutoCopilot.LlmTripletInstruction] =
            "Pluto: same 15s 3-act memo in 3 styles. studio=85mm bokeh rim light; pixar=3D pastel big eyes; retro=16mm grain handheld. " + Acts + "JSON only:\n" +
            "{\"variants\":[{\"style\":\"studio|pixar|retro\",\"hookTitle\":\"KO+1 emoji<=40\",\"act1\":\"EN\",\"act2\":\"EN\",\"act3\":\"EN\",\"camera\":\"EN\",\"bgm\":" + Bgm3 + "}]x3}",
        [PlutoGenerativeEngine.LlmTrendInstruction] =
            "Pluto: fresh viral Shorts/Reels concepts; each differs in subject/action/setting. JSON only:\n" +
            "{\"trends\":[{\"title\":\"KO<=30\",\"hook\":\"KO+emoji\",\"prompt\":\"EN 3-act t2v w/ style tokens\",\"camera\":\"EN\",\"bgm\":\"EN\",\"hashtags\":[\"#..\"x4-8]}]}",
        [PlutoMilestoneOrchestrator.LlmMilestoneInstruction] =
            "Pluto: 4 scenes of one day, same subject/wardrobe/light/room. s1 0-4s morning hook, s2 4-8s play, s3 8-12s big smile, s4 12-15s sleep. JSON only:\n" +
            "{\"scenes\":[{\"slot\":1,\"title\":\"KO<=30\",\"prompt\":\"EN<=400 no names\",\"camera\":\"EN\",\"bgm\":\"EN\"}]x4}",
        [PlutoSubtitleDirector.LlmLetterInstruction] =
            "Pluto: KO+EN 3-line letter parent->subject; en=poetic EN of same line. act1 1-4s opening image, act2 5-10s today's growth, act3 11-14s loving promise with name. ko<=40, en<=70 chars, no emoji/quotes/hashtags. JSON array only:\n" +
            "[{\"act\":1,\"ko\":\"\",\"en\":\"\"}]x3",
        [PlutoSocialCopywriter.LlmCaptionInstruction] =
            "Pluto: KO SNS caption, warm parent tone, emojis. JSON only:\n" +
            "{\"hook\":\"<=80\",\"story\":[\"2-3 lines\"],\"hashtags\":[\"#KO/EN x12-15\"],\"pinnedComment\":\"question<=120\"}",
        [PlutoSocialRadarEngine.LlmRadarInstruction] =
            "Pluto radar: viral family/pet Shorts/Reels archetypes; no real creators. JSON only:\n" +
            "{\"insights\":[{\"title\":\"EN\",\"category\":\"BabyMilestones|PetHealing|DailyAesthetic|EmotionalASMR\",\"mechanism\":\"EN\",\"camera\":\"EN\",\"lighting\":\"EN\",\"action\":\"EN\",\"pacing\":\"0.8-1.2s\",\"bgm\":\"lullaby_lofi|acoustic|musicbox\",\"hashtags\":[\"#..\"x4-10]}]}",
        [GeminiPromptService.SnsSeoInstruction] =
            "SEO: KO metadata for platform. JSON only:\n" +
            "{\"title\":\"<=90\",\"description\":\"1-2 sentences<=300\",\"hashtags\":[\"#..\"x5-8]}",
    };

    /// <summary>긴 라벨 → 짧은 라벨(접두 일치). <c>anchor</c>/<c>name</c>은 페르소나 컨텍스트로 분류.</summary>
    private static readonly (string Long, string Short)[] Labels =
    {
        ("Subject identity anchor (keep this exact same character in every act): ", "anchor: "),
        ("Subject identity anchor (begin every prompt with exactly this to keep the same character): ", "anchor(prefix every prompt): "),
        ("Identity anchor (do not repeat, it is prepended automatically): ", "anchor(auto, don't repeat): "),
        ("Subject name: ", "name: "),
        ("Memo (Korean): ", "memo: "),
        ("Detected theme: ", "theme: "),
        ("Season / time: ", "when: "),
        ("Preferred lighting: ", "light: "),
        ("Preferred atmosphere: ", "mood: "),
        ("Proven SNS viral formula (apply its hook timing and cut pacing): ", "viral formula: "),
        ("Proven SNS viral formulas to weave into pacing and hooks: ", "viral formulas: "),
        ("Avoid repeating these recent titles: ", "avoid titles: "),
        ("Creator already learned these formulas (offer fresh variations): ", "known formulas(vary): "),
        ("Time of day: ", "time: "),
        ("Video prompt: ", "video: "),
        ("Parent note: ", "note: "),
    };

    /// <summary>알려진 Pluto 지시문이면 true(컴팩트 스키마 보유).</summary>
    public static bool IsKnown(string? system) => system is not null && CompactSystems.ContainsKey(system);

    /// <summary>불변 접두 — 알려진 지시문은 컴팩트 버전, 미지 지시문은 공백 압축만(의미 보존).</summary>
    public static string CompactSystem(string? system)
    {
        if (string.IsNullOrWhiteSpace(system)) return string.Empty;
        return CompactSystems.TryGetValue(system, out var c) ? c : CollapseWhitespace(system);
    }

    /// <summary>로컬 게이트웨이용 프롬프트 — system(불변) + user(<c>---</c> · [CTX] 앵커 · [REQ] 요청).</summary>
    public static PlutoCompactPrompt Compose(string? system, string? user)
    {
        var sys = CompactSystem(system);
        var ctx = new StringBuilder(128);
        var req = new StringBuilder((user?.Length ?? 0) + 16);
        if (!string.IsNullOrWhiteSpace(user))
        {
            foreach (var rawLine in user.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;
                var shortLine = ShortenLabel(line);
                var target = IsPersona(shortLine) ? ctx : req;
                target.Append(shortLine).Append('\n');
            }
        }
        var sb = new StringBuilder(ctx.Length + req.Length + 24).Append(Separator).Append('\n');
        if (ctx.Length > 0) sb.Append(ContextHeader).Append('\n').Append(ctx);
        sb.Append(RequestHeader).Append('\n').Append(req);
        return new PlutoCompactPrompt(sys, sb.ToString().TrimEnd());
    }

    /// <summary>근사 토큰 수 — ASCII 4자당 1토큰 · 비ASCII(한글·이모지) 문자당 1토큰.</summary>
    public static int EstimateTokens(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int ascii = 0, other = 0;
        foreach (var c in s)
        {
            if (c < 128) ascii++;
            else if (!char.IsLowSurrogate(c)) other++;
        }
        return (ascii + 3) / 4 + other;
    }

    private static string ShortenLabel(string line)
    {
        foreach (var (l, s) in Labels)
            if (line.StartsWith(l, StringComparison.Ordinal)) return s + line[l.Length..];
        return line;
    }

    private static bool IsPersona(string line)
        => line.StartsWith("anchor", StringComparison.Ordinal) || line.StartsWith("name: ", StringComparison.Ordinal);

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var ws = false;
        foreach (var c in s.Trim())
        {
            if (char.IsWhiteSpace(c) && c != '\n') { if (!ws) sb.Append(' '); ws = true; }
            else { sb.Append(c); ws = false; }
        }
        return sb.ToString();
    }
}
