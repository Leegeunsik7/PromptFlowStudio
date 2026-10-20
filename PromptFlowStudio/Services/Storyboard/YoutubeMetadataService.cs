using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>유튜브 숏츠 업로드용 메타데이터 결과(제목·설명·해시태그줄 + 정제된 태그 목록).</summary>
public sealed record YoutubeMetadata(string Title, string Description, string Hashtags, IReadOnlyList<string> Tags)
{
    /// <summary>생성할 컷/데이터가 없을 때의 빈 결과(Graceful).</summary>
    public static readonly YoutubeMetadata Empty = new("", "", "", Array.Empty<string>());

    /// <summary>표시할 내용이 있는지(제목이 채워졌는지).</summary>
    public bool HasContent => !string.IsNullOrEmpty(Title);
}

/// <summary>스토리보드 프로젝트에서 유튜브 숏츠 메타데이터를 자동 생성하는 계약.</summary>
public interface IYoutubeMetadataService
{
    /// <summary><see cref="StoryboardProject"/> 의 제목·컷별 프롬프트·나레이션을 분석해 제목/설명/해시태그를 생성한다. 컷이 없으면 <see cref="YoutubeMetadata.Empty"/>.</summary>
    YoutubeMetadata Generate(StoryboardProject? project);
}

/// <summary>
/// [PromptFlowStudio] 유튜브 숏츠 메타데이터 자동 생성 — 순수 BCL(정규식 + StringBuilder), 외부 NuGet 0.
///   ▸ 제목: 프로젝트 제목 + 첫 핵심 키워드를 임팩트형 템플릿으로 조합(유튜브 100자 제한 준수).
///   ▸ 설명: 장면 수 요약 + 나레이션 모음 + 컷별 구성 + 제작도구 템플릿 + 해시태그(설명 상단 3개가 제목 위 칩으로 노출됨).
///   ▸ 해시태그: 고정 기본 태그(#Shorts 등) + 프롬프트/대사에서 추출한 핵심 키워드(한글 조사 제거·불용어 필터·빈도순) + 주제 확장 맵.
///   ⚠ 형태소 분석기 없이 정규식 휴리스틱이라 완벽하진 않다 — 결과는 편집 전제(사용자가 다듬어 업로드). 데이터 없으면 Empty(Graceful).
/// </summary>
public sealed class YoutubeMetadataService : IYoutubeMetadataService
{
    // 항상 포함하는 고정 태그(브랜드/도구).
    private static readonly string[] BaseTags = { "Shorts", "숏츠", "AI", "AI영상", "Wan21", "PromptFlowStudio" };

    // 주제 키워드 감지 시 관련 태그로 확장(양산형 SEO 보강).
    private static readonly Dictionary<string, string[]> ThemeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["고양이"] = new[] { "고양이", "cat", "냥스타그램" },
        ["강아지"] = new[] { "강아지", "dog", "댕댕이" },
        ["바다"]   = new[] { "바다", "ocean", "힐링" },
        ["해변"]   = new[] { "해변", "beach", "힐링" },
        ["우주"]   = new[] { "우주", "space", "galaxy" },
        ["요리"]   = new[] { "요리", "food", "먹방" },
        ["여행"]   = new[] { "여행", "travel", "브이로그" },
        ["게임"]   = new[] { "게임", "gaming", "game" },
        ["자연"]   = new[] { "자연", "nature", "힐링" },
        ["도시"]   = new[] { "도시", "city", "야경" },
        ["꽃"]     = new[] { "꽃", "flower", "봄" },
        ["로봇"]   = new[] { "로봇", "robot", "sci_fi" },
        ["자동차"] = new[] { "자동차", "car", "드라이브" },
    };

    // 한글 불용어(추출 제외) — 흔한 부사/영상 용어.
    private static readonly HashSet<string> StopKo = new(StringComparer.Ordinal)
    {
        "그리고","하지만","그러나","그런","이런","저런","여기","거기","저기","우리","너희","자신",
        "정말","진짜","완전","조금","매우","아주","위해","대한","이것","그것","저것","때문","동안",
        "하는","있는","되는","같은","많은","무슨","어떤","모든","다시","계속","바로","그냥","약간",
        "서서히","천천히","부드럽게","장면","영상","컷","프롬프트","카메라","스타일","분위기","느낌",
    };

    // 영문 불용어 — 관사/전치사/영상 촬영 용어.
    private static readonly HashSet<string> StopEn = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","and","for","with","this","that","from","your","you","are","was","were","have","has","had",
        "will","its","into","over","under","very","more","most","some","any","all","not","but","out","off",
        "then","than","also","onto","upon","cinematic","shot","camera","scene","video","view","style","slow",
        "soft","closeup","close","wide","angle","light","lighting","motion","background","foreground",
    };

    // 다중문자 조사(길이 내림차순 — 긴 것부터 제거). 명사 끝 음절과 겹칠 확률이 낮아 안전하게 절단.
    private static readonly string[] JosaMulti =
    {
        "으로써","으로서","에서는","에게서","에서","으로","에게","한테","까지","부터","처럼","보다",
        "마다","조차","라도","이나","이랑",
    };

    // 단일문자 조사 — 명사 끝 음절과 자주 겹치므로(고양'이', 포'도', 국'가') 무조건 떼지 않고, 어간이 확인될 때만 병합.
    private static readonly char[] JosaSingle = { '은','는','이','가','을','를','의','에','와','과','도','만','로','랑','께' };

    private static readonly Regex KoRx = new(@"[가-힣]{2,}", RegexOptions.Compiled);
    private static readonly Regex EnRx = new(@"[A-Za-z][A-Za-z0-9]{2,}", RegexOptions.Compiled);

    /// <inheritdoc/>
    public YoutubeMetadata Generate(StoryboardProject? project)
    {
        if (project is null || project.Scenes.Count == 0) return YoutubeMetadata.Empty;

        var scenes = project.Scenes;
        var titleBase = string.IsNullOrWhiteSpace(project.Title) || project.Title.Trim() == "새 스토리보드"
            ? "AI 숏츠" : project.Title.Trim();

        var keywords = ExtractKeywords(scenes, max: 8);
        var tags     = BuildTags(keywords);
        var hashtags = string.Join(" ", tags.Select(t => "#" + t));
        var title    = BuildTitle(titleBase, keywords.FirstOrDefault());
        var desc     = BuildDescription(scenes, hashtags);

        return new YoutubeMetadata(title, desc, hashtags, tags);
    }

    // ── 제목: "🔥 {제목} — {핵심키워드} | AI로 만든 놀라운 영상 #Shorts" (100자 제한) ──
    private static string BuildTitle(string baseName, string? hook)
    {
        var sb = new StringBuilder("🔥 ").Append(baseName);
        if (!string.IsNullOrEmpty(hook) && !baseName.Contains(hook, StringComparison.OrdinalIgnoreCase))
            sb.Append(" — ").Append(hook);
        sb.Append(" | AI로 만든 놀라운 영상 #Shorts");
        var t = sb.ToString();
        return t.Length <= 100 ? t : t[..97].TrimEnd() + "…";   // 유튜브 제목 100자 제한
    }

    // ── 설명: 요약 + 나레이션 모음 + 컷 구성 + 제작도구 템플릿 + 해시태그 ──
    private static string BuildDescription(IReadOnlyList<StoryboardScene> scenes, string hashtags)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"🎬 {scenes.Count}개의 장면으로 구성된 AI 생성 숏츠입니다.");
        sb.AppendLine();

        var narrations = scenes.Select(s => (s.VoiceText ?? "").Trim()).Where(v => v.Length > 0).ToList();
        if (narrations.Count > 0)
        {
            sb.AppendLine("📖 나레이션");
            foreach (var v in narrations) sb.AppendLine("• " + v);
            sb.AppendLine();
        }

        sb.AppendLine("🎞️ 장면 구성");
        for (var i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            var line = !string.IsNullOrWhiteSpace(s.VoiceText) ? s.VoiceText!.Trim()
                     : !string.IsNullOrWhiteSpace(s.Prompt)    ? s.Prompt.Trim()
                     : (s.IsI2v ? "(이미지 기반 장면)" : "(장면)");
            sb.AppendLine($"{i + 1}. {Clip(line, 80)}");
        }
        sb.AppendLine();

        sb.AppendLine("🛠️ 제작 도구: PromptFlow Studio (Alibaba Wan 2.1 AI)");
        sb.AppendLine("⚡ 프롬프트만으로 AI 영상을 자동 생성했습니다.");
        sb.AppendLine();
        sb.Append(hashtags);
        return sb.ToString();
    }

    // ── 키워드 추출(2-패스): ①다중문자 조사만 안전 절단해 토큰화 → ②단일문자 조사 변이를 어간으로 '확신할 때만' 병합 → 빈도순 ──
    private static List<string> ExtractKeywords(IReadOnlyList<StoryboardScene> scenes, int max)
    {
        // ── 1차: 토큰화(다중문자 조사만 제거 · 단일문자 조사는 손대지 않음 → 명사 오절단 방지) ──
        var freq  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var seq = 0;

        void Add(string token)
        {
            if (token.Length < 2) return;
            freq[token] = freq.TryGetValue(token, out var c) ? c + 1 : 1;
            if (!order.ContainsKey(token)) order[token] = seq++;
        }

        foreach (var s in scenes)
        {
            foreach (var text in new[] { s.Prompt, s.VoiceText })
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                foreach (Match m in KoRx.Matches(text))
                {
                    var w = StripJosaMulti(m.Value);
                    if (w.Length >= 2 && !StopKo.Contains(w)) Add(w);
                }
                foreach (Match m in EnRx.Matches(text))
                    if (!StopEn.Contains(m.Value)) Add(m.Value.ToLowerInvariant());
            }
        }

        // ── 2차: 단일문자 조사 변이 병합 ──
        //   각 토큰의 '어간'(끝 단일문자 조사를 뗀 형태, 3자 이상일 때만)을 계산해 그룹화.
        //   어간으로 정규화하는 건 '확신'될 때뿐: (a)어간이 실제 토큰으로도 존재하거나 (b)같은 어간의 서로 다른 조사 변이가 2개 이상.
        //   그 외(단독 등장)엔 원형 유지 — '포도','고양이','정확도'처럼 조사와 겹치는 명사를 훼손하지 않는다.
        string StemOf(string t) =>
            t.Length >= 3 && Array.IndexOf(JosaSingle, t[^1]) >= 0 ? t[..^1] : t;

        var variantsPerStem = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in freq.Keys)
        {
            var stem = StemOf(t);
            if (!variantsPerStem.TryGetValue(stem, out var set)) variantsPerStem[stem] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(t);
        }

        var merged  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var mOrder  = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, c) in freq)
        {
            var stem = StemOf(t);
            var normalize = stem != t && (freq.ContainsKey(stem) || variantsPerStem[stem].Count >= 2);
            var key = normalize ? stem : t;

            merged[key] = merged.TryGetValue(key, out var mc) ? mc + c : c;
            if (!mOrder.TryGetValue(key, out var mo) || order[t] < mo) mOrder[key] = order[t];   // 최초 등장 순서 유지
        }

        return merged.Keys
            .OrderByDescending(k => merged[k])
            .ThenBy(k => mOrder[k])
            .Take(max)
            .ToList();
    }

    // 다중문자 조사만 제거(조사 떼고도 2자 이상 남을 때만 · 긴 조사 우선).
    private static string StripJosaMulti(string w)
    {
        foreach (var j in JosaMulti)
            if (w.Length > j.Length + 1 && w.EndsWith(j, StringComparison.Ordinal))
                return w[..^j.Length];
        return w;
    }

    // ── 태그: 기본 태그 + 키워드 + 주제확장, 정제(한글/영숫자만)·중복제거·최대 14개 ──
    private static List<string> BuildTags(IReadOnlyList<string> keywords)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddTag(string t)
        {
            var clean = SanitizeTag(t);
            if (clean.Length < 2) return;
            if (seen.Add(clean)) result.Add(clean);
        }

        foreach (var b in BaseTags) AddTag(b);
        foreach (var k in keywords)
        {
            AddTag(k);
            if (ThemeMap.TryGetValue(k, out var extra))
                foreach (var e in extra) AddTag(e);
        }
        return result.Take(14).ToList();   // 유튜브 해시태그 과다(>15) 방지
    }

    // 해시태그는 공백/기호 불가 → 문자/숫자만 남긴다(한글은 IsLetter=true).
    private static string SanitizeTag(string t)
    {
        var sb = new StringBuilder(t.Length);
        foreach (var ch in t)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}
