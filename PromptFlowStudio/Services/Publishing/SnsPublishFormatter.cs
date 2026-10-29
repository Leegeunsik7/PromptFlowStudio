using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.Publishing;

/// <summary>씬 1개 대본 + 길이(초) — 설명 타임스탬프/캡션 요약 원천.</summary>
public readonly record struct ScriptBeat(string Script, double DurationSec);

/// <summary>
/// [PromptFlowStudio] SNS 발행 텍스트 포맷터(순수 함수 · 테스트 잠금) — 훅 기반 제목 · 씬 대본 타임스탬프 설명 · 태그/해시태그 · 릴스 캡션.
///   ▸ YouTube: 제목 ≤100자(<c>#Shorts #AI</c> 접미) · 설명 ≤5000자 · 태그 합계 ≤500자/30개(<c>Shorts</c> 필수).
///   ▸ Instagram: 캡션 ≤2200자 · 해시태그 ≤30개(<see cref="SocialMedia.InstagramReelsService.ValidateCaption"/> 규격).
/// </summary>
public static class SnsPublishFormatter
{
    public const string ShortsSuffix = " #Shorts #AI";
    public const int MaxTitle = 100;
    public const int MaxDescription = 5000;
    public const int MaxTags = 30;
    public const int MaxTagChars = 500;
    public const int MaxCaption = 2200;
    public const int MaxHashtags = 30;
    private const int MaxBeatChars = 80;
    private static readonly char[] SentenceEnds = { '\n', '.', '!', '?', '。' };
    private static readonly char[] TagSeparators = { ',', ' ', '#', '\n', '\r', '\t', ';' };

    /// <summary>훅 첫 문장 핵심 + <c>#Shorts #AI</c>(총 100자 이내). 빈 훅은 "AI Shorts".</summary>
    public static string BuildTitle(string? hook)
    {
        var core = FirstSentence(hook);
        if (core.Length == 0) core = "AI Shorts";
        var room = MaxTitle - ShortsSuffix.Length;
        if (core.Length > room) core = core[..(room - 1)].TrimEnd() + "…";
        return core + ShortsSuffix;
    }

    /// <summary>훅 + 씬별 대본 요약 타임스탬프(<c>0:00 …</c>) + 해시태그(≤5000자).</summary>
    public static string BuildDescription(string? hook, IReadOnlyList<ScriptBeat>? beats)
    {
        var sb = new StringBuilder(512);
        var h = (hook ?? "").Trim();
        if (h.Length > 0) sb.Append(h).Append("\n\n");
        if (beats is { Count: > 0 })
        {
            sb.Append("⏱ 타임스탬프\n");
            double t = 0;
            for (var i = 0; i < beats.Count; i++)
            {
                var line = Summarize(beats[i].Script);
                if (line.Length > 0) sb.Append(FormatTimestamp(t)).Append(' ').Append(line).Append('\n');
                t += Math.Max(0, beats[i].DurationSec);
            }
            sb.Append('\n');
        }
        sb.Append("#Shorts #AI");
        var s = sb.ToString();
        return s.Length <= MaxDescription ? s : s[..MaxDescription];
    }

    /// <summary>태그 — <c>Shorts</c>·<c>AI</c> 선두 + 훅 키워드 + 추가 태그(중복 제거 · ≤30개 · 합계 ≤500자).</summary>
    public static IReadOnlyList<string> BuildTags(string? hook, IEnumerable<string>? extra = null)
    {
        var list = new List<string>(MaxTags);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chars = 0;
        void Add(string? raw)
        {
            var t = (raw ?? "").Trim().TrimStart('#').Trim();
            if (t.Length < 2 || t.Length > 60 || list.Count >= MaxTags || chars + t.Length > MaxTagChars || !seen.Add(t)) return;
            list.Add(t);
            chars += t.Length;
        }
        Add("Shorts");
        Add("AI");
        if (extra is not null) foreach (var e in extra) Add(e);
        foreach (var w in (hook ?? "").Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (list.Count >= 10) break;
            Add(w.Trim('"', '\'', '“', '”', '(', ')', '[', ']', '.', '!', '?', ':'));
        }
        return list;
    }

    /// <summary>쉼표/공백/# 구분 태그 문자열 파싱(UI 입력).</summary>
    public static IReadOnlyList<string> ParseTags(string? text) => BuildTags(null, (text ?? "").Split(TagSeparators, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>태그 → 해시태그 문자열(<c>#a #b</c> · ≤30개 · 공백 제거).</summary>
    public static string ToHashtags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0) return "";
        var sb = new StringBuilder(tags.Count * 8);
        var n = 0;
        foreach (var t in tags)
        {
            if (n >= MaxHashtags) break;
            var clean = t.Replace(" ", "", StringComparison.Ordinal).TrimStart('#');
            if (clean.Length == 0) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append('#').Append(clean);
            n++;
        }
        return sb.ToString();
    }

    /// <summary>릴스 캡션 — 훅 + 대본 요약 + 해시태그(≤2200자 · 해시태그 ≤30개, 본문 '#'는 제거해 한도 보호).</summary>
    public static string BuildReelsCaption(string? hook, IReadOnlyList<ScriptBeat>? beats, IReadOnlyList<string>? tags)
    {
        var hashtags = ToHashtags(tags);
        var body = new StringBuilder(512);
        var h = (hook ?? "").Replace("#", "", StringComparison.Ordinal).Trim();
        if (h.Length > 0) body.Append(h);
        if (beats is { Count: > 0 })
            foreach (var b in beats)
            {
                var line = Summarize(b.Script).Replace("#", "", StringComparison.Ordinal);
                if (line.Length == 0) continue;
                if (body.Length > 0) body.Append('\n');
                body.Append("▸ ").Append(line);
            }
        var room = MaxCaption - (hashtags.Length > 0 ? hashtags.Length + 2 : 0);
        var text = body.ToString();
        if (text.Length > room) text = text[..Math.Max(0, room - 1)] + "…";
        return hashtags.Length == 0 ? text : text.Length == 0 ? hashtags : text + "\n\n" + hashtags;
    }

    /// <summary>공개 범위 정규화 — private/unlisted/public, 그 외 private(안전 기본값).</summary>
    public static string NormalizePrivacy(string? p)
    {
        var v = (p ?? "").Trim().ToLowerInvariant();
        return v is "public" or "unlisted" ? v : "private";
    }

    /// <summary>초 → <c>m:ss</c>(InvariantCulture).</summary>
    public static string FormatTimestamp(double sec)
    {
        var s = (int)Math.Max(0, Math.Floor(sec));
        return (s / 60).ToString(CultureInfo.InvariantCulture) + ":" + (s % 60).ToString("00", CultureInfo.InvariantCulture);
    }

    private static string FirstSentence(string? text)
    {
        var t = (text ?? "").Trim();
        var i = t.IndexOfAny(SentenceEnds);
        if (i > 0) t = t[..(t[i] == '\n' ? i : i + 1)].Trim();
        return t;
    }

    private static string Summarize(string? script)
    {
        var t = FirstSentence(script).Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= MaxBeatChars ? t : t[..(MaxBeatChars - 1)].TrimEnd() + "…";
    }
}
