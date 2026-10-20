using System;
using System.Collections.Generic;
using System.Text;
using PromptFlowStudio.Services.Autonomous;

namespace PromptFlowStudio.Services.MultiEngine;

/// <summary>v16.29.0 프롬프트 방언(엔진별 구문 계열).</summary>
public enum PromptDialect : byte { WanTags, SoraNarrative, RunwayCamera, LumaConcise, Passthrough }

/// <summary>v16.29.0 표준 연출 사양(트랜스파일 중간 표현).</summary>
public sealed record DirectionSpec(string Subject, string? Camera, string? Lighting, string? Style, string? Mood, IReadOnlyList<string> Details, string? Negative, int CameraIndex, int LightingIndex);

/// <summary>v16.29.0 트랜스파일 결과.</summary>
public sealed record TranspiledPrompt(PromptDialect Dialect, string Prompt, string? Negative, int MaxLength, bool Truncated);

/// <summary>
/// [PromptFlowStudio] v16.29.0 🔤 엔진별 프롬프트 자동 변환 &amp; 문맥 트랜스파일러 — 표준 연출 프롬프트 → 중간 표현(<see cref="DirectionSpec"/>) → 엔진 방언.
///   ▸ 파싱(<see cref="Parse"/>): 쉼표/마침표 절 분해 · 카메라 무빙/조명 사전(v16.28.0 <see cref="PerformanceFeedbackLoopEngine"/> 한/영 동의어 · Span 매칭)
///     · 스타일/무드 사전 · <c>--no</c>/<c>negative:</c>/<c>제외:</c> 네거티브 분리 · 첫 비-연출 절 = 주 피사체.
///   ▸ 방언:
///     - <b>Wan 2.1</b> 태그 조합형 — "subject, details…, camera, lighting, style, quality tags" (800자 · 네거티브 별도 500자).
///     - <b>Sora 2</b> 디테일 문장형 — 장면 서술 + "The camera …" + "Lit by …" + 분위기 문장(2000자 · 네거티브는 "Avoid …" 문장).
///     - <b>Runway Gen-3</b> 카메라 우선 키워드 — "[Camera move]: subject. details. lighting. style." (1000자 · 네거티브 미지원 → 생략).
///     - <b>Luma Dream Machine</b> 간결 자연어 — 1~2문장(600자).
///   ▸ 길이 초과는 단어 경계 절단(<see cref="Truncated"/> 표시) · 제어문자 제거.
/// </summary>
public sealed class GenerativePromptTranspilerEngine
{
    private static readonly string[][] StyleLexicon =
    {
        new[] { "cinematic", "cinematic", "film", "영화", "시네마틱" },
        new[] { "anime style", "anime", "애니", "만화" },
        new[] { "photorealistic", "photorealistic", "realistic", "실사", "사실적" },
        new[] { "3D animation", "3d", "pixar", "3D 애니" },
        new[] { "documentary", "documentary", "다큐" },
        new[] { "vintage film look", "vintage", "retro", "film grain", "빈티지", "레트로" },
    };

    private static readonly string[][] MoodLexicon =
    {
        new[] { "warm and heartwarming", "heartwarming", "warm", "cozy", "따뜻", "포근", "감동" },
        new[] { "energetic and dynamic", "energetic", "dynamic", "exciting", "역동", "신나" },
        new[] { "calm and serene", "calm", "serene", "peaceful", "고요", "평화", "잔잔" },
        new[] { "mysterious", "mysterious", "eerie", "신비", "미스터리" },
        new[] { "playful", "playful", "cute", "fun", "귀여", "장난" },
    };

    public static readonly string[] WanQualityTags = { "high quality", "highly detailed", "smooth motion" };

    public static int MaxLengthOf(PromptDialect d) => d switch
    {
        PromptDialect.WanTags => 800, PromptDialect.SoraNarrative => 2000, PromptDialect.RunwayCamera => 1000, PromptDialect.LumaConcise => 600, _ => 2000,
    };

    // ══════════════════════ 파싱 ══════════════════════

    /// <summary>표준 프롬프트 → 연출 사양.</summary>
    public static DirectionSpec Parse(string? prompt)
    {
        var text = Clean(prompt);
        string? negative = null;
        foreach (var marker in new[] { "--no ", "negative:", "Negative:", "제외:" })
        {
            var i = text.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) continue;
            negative = text[(i + marker.Length)..].Trim().Trim(',', '.');
            text = text[..i].Trim().TrimEnd(',', '.');
            break;
        }
        var camIdx = PerformanceFeedbackLoopEngine.DetectFeature(text, PerformanceFeedbackLoopEngine.CameraLexicon);
        var litIdx = PerformanceFeedbackLoopEngine.DetectFeature(text, PerformanceFeedbackLoopEngine.LightingLexicon);
        var styIdx = PerformanceFeedbackLoopEngine.DetectFeature(text, StyleLexicon);
        var moodIdx = PerformanceFeedbackLoopEngine.DetectFeature(text, MoodLexicon);
        var clauses = text.Split(new[] { ',', '.', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string subject = "";
        var details = new List<string>(clauses.Length);
        foreach (var c in clauses)
        {
            if (c.Length == 0) continue;
            if (IsDirective(c)) continue;
            if (subject.Length == 0) subject = c;
            else if (!IsQualityTag(c)) details.Add(c);
        }
        if (subject.Length == 0) subject = text.Length > 0 ? text : "a scene";
        return new DirectionSpec(subject,
            camIdx >= 0 ? PerformanceFeedbackLoopEngine.CameraLexicon[camIdx][0] : null,
            litIdx >= 0 ? PerformanceFeedbackLoopEngine.LightingLexicon[litIdx][0] : null,
            styIdx >= 0 ? StyleLexicon[styIdx][0] : null,
            moodIdx >= 0 ? MoodLexicon[moodIdx][0] : null,
            details, string.IsNullOrWhiteSpace(negative) ? null : negative, camIdx, litIdx);
    }

    /// <summary>절이 연출 지시(카메라/조명/스타일/무드) 단독인지 — 해당 절은 주 피사체/디테일에서 제외.</summary>
    private static bool IsDirective(string clause)
        => clause.Length <= 40 && (Hit(clause, PerformanceFeedbackLoopEngine.CameraLexicon) || Hit(clause, PerformanceFeedbackLoopEngine.LightingLexicon)
            || Hit(clause, StyleLexicon) || Hit(clause, MoodLexicon));

    private static bool Hit(string clause, string[][] lex) => PerformanceFeedbackLoopEngine.DetectFeature(clause, lex) >= 0;

    private static bool IsQualityTag(string c)
    {
        foreach (var q in new[] { "high quality", "4k", "8k", "masterpiece", "highly detailed", "best quality", "고화질" })
            if (c.Equals(q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>제어문자 제거 · 공백 정규화(줄바꿈 → 쉼표 구분).</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (ch == '\n' || ch == '\r') { if (sb.Length > 0 && sb[^1] != ',') sb.Append(','); space = true; continue; }
            if (char.IsControl(ch)) continue;
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString().Trim(' ', ',');
    }

    // ══════════════════════ 방언 생성 ══════════════════════

    public TranspiledPrompt Transpile(string? prompt, PromptDialect dialect) => Emit(Parse(prompt), dialect, prompt);

    /// <summary>사양 → 엔진 방언.</summary>
    public static TranspiledPrompt Emit(DirectionSpec s, PromptDialect dialect, string? original = null)
    {
        var max = MaxLengthOf(dialect);
        string body; string? neg = null;
        switch (dialect)
        {
            case PromptDialect.WanTags:
            {
                var sb = new StringBuilder(256).Append(s.Subject);
                foreach (var d in s.Details) sb.Append(", ").Append(d);
                if (s.Camera is not null) sb.Append(", ").Append(s.Camera);
                if (s.Lighting is not null) sb.Append(", ").Append(s.Lighting);
                if (s.Style is not null) sb.Append(", ").Append(s.Style);
                if (s.Mood is not null) sb.Append(", ").Append(s.Mood);
                foreach (var q in WanQualityTags) sb.Append(", ").Append(q);
                body = sb.ToString();
                neg = s.Negative is null ? null : Truncate(s.Negative, 500, out _);
                break;
            }
            case PromptDialect.SoraNarrative:
            {
                var sb = new StringBuilder(512).Append(Sentence(s.Subject));
                if (s.Details.Count > 0)
                {
                    sb.Append(' ');
                    for (var i = 0; i < s.Details.Count; i++) sb.Append(i == 0 ? Cap(s.Details[i]) : s.Details[i]).Append(i == s.Details.Count - 1 ? ". " : ", ");
                }
                if (s.Camera is not null) sb.Append(" The camera moves with a ").Append(s.Camera).Append(", keeping the subject in sharp focus.");
                if (s.Lighting is not null) sb.Append(" The scene is lit by ").Append(s.Lighting).Append('.');
                if (s.Style is not null || s.Mood is not null)
                    sb.Append(" The overall look is ").Append(s.Style ?? "cinematic").Append(s.Mood is null ? "" : ", with a " + s.Mood + " atmosphere").Append('.');
                if (s.Negative is not null) sb.Append(" Avoid ").Append(s.Negative.TrimEnd('.')).Append('.');
                body = Squash(sb.ToString());
                break;
            }
            case PromptDialect.RunwayCamera:
            {
                var sb = new StringBuilder(256);
                sb.Append(s.Camera is null ? "Static shot" : Cap(s.Camera)).Append(": ").Append(Sentence(s.Subject));
                if (s.Details.Count > 0) { sb.Append(' '); sb.Append(Sentence(string.Join(", ", s.Details))); }
                if (s.Lighting is not null) sb.Append(' ').Append(Cap(s.Lighting)).Append('.');
                if (s.Style is not null) sb.Append(' ').Append(Cap(s.Style)).Append('.');
                if (s.Mood is not null) sb.Append(' ').Append(Cap(s.Mood)).Append(" mood.");
                body = Squash(sb.ToString());
                break;
            }
            case PromptDialect.LumaConcise:
            {
                var sb = new StringBuilder(256).Append(Sentence(s.Subject + (s.Details.Count > 0 ? ", " + s.Details[0] : "")));
                var extras = new List<string>(3);
                if (s.Camera is not null) extras.Add(s.Camera);
                if (s.Lighting is not null) extras.Add(s.Lighting);
                if (s.Mood is not null) extras.Add(s.Mood + " mood");
                if (extras.Count > 0) sb.Append(' ').Append(Sentence(Cap(string.Join(", ", extras))));
                body = Squash(sb.ToString());
                break;
            }
            default:
                body = Clean(original);
                break;
        }
        var outText = Truncate(body, max, out var cut);
        return new TranspiledPrompt(dialect, outText, neg, max, cut);
    }

    /// <summary>단어 경계 절단(최대 길이 · 절단 시 true).</summary>
    public static string Truncate(string s, int max, out bool truncated)
    {
        truncated = s.Length > max;
        if (!truncated) return s;
        var cut = s.LastIndexOf(' ', Math.Max(0, max - 1));
        return (cut > max / 2 ? s[..cut] : s[..max]).TrimEnd(',', ' ');
    }

    private static string Sentence(string s)
    {
        s = s.Trim().TrimEnd(',', ';');
        if (s.Length == 0) return s;
        s = Cap(s);
        return s[^1] is '.' or '!' or '?' ? s : s + ".";
    }

    private static string Cap(string s) => s.Length == 0 || !char.IsLower(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch == ' ' && sb.Length > 0 && sb[^1] == ' ') continue;
            sb.Append(ch);
        }
        return sb.ToString().Replace(" .", ".", StringComparison.Ordinal).Replace("..", ".", StringComparison.Ordinal).Trim();
    }
}
