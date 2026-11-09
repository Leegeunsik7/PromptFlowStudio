using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📣 Pluto 소셜 게시글 초안 — 제목 · 훅 인트로 · 감성 스토리 · 해시태그 · 유튜브 고정 댓글.</summary>
public sealed record PlutoSocialPost(
    SocialPlatform Platform,
    string Title,
    string HookIntro,
    IReadOnlyList<string> StoryLines,
    IReadOnlyList<string> Hashtags,
    string PinnedComment)
{
    /// <summary>📋 전체 본문 + 해시태그(복사용).</summary>
    public string FullText
    {
        get
        {
            var sb = new System.Text.StringBuilder(512);
            sb.Append(Title).Append('\n').Append(HookIntro).Append('\n').Append('\n');
            foreach (var l in StoryLines) sb.Append(l).Append('\n');
            sb.Append('\n').Append(string.Join(' ', Hashtags));
            if ((Platform & SocialPlatform.YouTubeShorts) != 0 && PinnedComment.Length > 0)
                sb.Append('\n').Append('\n').Append("📌 고정 댓글: ").Append(PinnedComment);
            return sb.ToString();
        }
    }
}

/// <summary>
/// 📣 Pluto Viral Social Copywriter — 영상 프롬프트/훅 → 플랫폼(Shorts·Reels)별 감성 캡션(이모지) + 10~15개 타깃 해시태그 + 고정 댓글(로컬 템플릿).
/// </summary>
public static class PlutoSocialCopywriter
{
    public const int MinTags = 10;
    public const int MaxTags = 15;

    private static readonly string[] BabyTags = { "#육아스타그램", "#베이비릴스", "#성장일기", "#돌준맘", "#육아브이로그", "#아기스타그램", "#육아일기", "#딸스타그램", "#아들스타그램", "#babymilestone", "#babyreels", "#momlife", "#육아소통" };
    private static readonly string[] PetTags = { "#반려동물", "#댕댕이", "#냥스타그램", "#멍스타그램", "#펫스타그램", "#강아지일상", "#고양이일상", "#반려견", "#dogsofinstagram", "#catsofinstagram", "#petreels", "#cutepets" };
    private static readonly string[] HealingTags = { "#힐링", "#힐링영상", "#감성영상", "#자연", "#asmr", "#relaxing", "#멍때리기", "#휴식", "#naturelovers", "#healingvibes", "#감성스타그램", "#peaceful" };
    private static readonly string[] VlogTags = { "#일상브이로그", "#브이로그", "#일상", "#데일리", "#소확행", "#vlog", "#dailyvlog", "#aesthetic", "#일상스타그램", "#감성브이로그", "#morningroutine", "#lifestyle" };

    /// <summary>텍스트(영문 프롬프트·한글 메모) → 테마 추정(기본 = 아기 & 육아).</summary>
    public static StudioThemeCategory InferCategory(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return StudioThemeCategory.BabyParenting;
        var s = text.ToLowerInvariant();
        if (Any(s, "baby", "toddler", "newborn", "아기", "아이", "육아", "신생아")) return StudioThemeCategory.BabyParenting;
        if (Any(s, "puppy", "dog", "cat", "kitten", "corgi", "retriever", "rabbit", "강아지", "고양이", "댕댕", "냥")) return StudioThemeCategory.PetsCompanions;
        if (Any(s, "forest", "ocean", "rain", "campfire", "nature", "stream", "lake", "snow", "힐링", "숲", "바다", "빗소리")) return StudioThemeCategory.HealingNature;
        if (Any(s, "vlog", "café", "cafe", "desk", "routine", "kitchen", "브이로그", "일상", "카페")) return StudioThemeCategory.DailyVlog;
        return StudioThemeCategory.BabyParenting;
    }

    private static bool Any(string s, params string[] keys)
    {
        foreach (var k in keys) if (s.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string[] TagsFor(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.BabyParenting => BabyTags,
        StudioThemeCategory.PetsCompanions => PetTags,
        StudioThemeCategory.HealingNature => HealingTags,
        _ => VlogTags,
    };

    /// <summary>
    /// 📣 게시글 초안 생성. <paramref name="title"/> 비면 테마 기본 제목.
    /// 해시태그 = 플랫폼 필수(#Shorts / #Reels) + 테마 풀 + 추가 태그 — 중복 제거 · 10~15개.
    /// </summary>
    public static PlutoSocialPost Compose(string? title, string? sourceText, SocialPlatform platform, IReadOnlyList<string>? extraTags = null)
    {
        if (platform == SocialPlatform.None) platform = SocialPlatform.Both;
        var cat = InferCategory((title ?? "") + " " + (sourceText ?? ""));
        var t = string.IsNullOrWhiteSpace(title) ? DefaultTitle(cat) : title.Trim();
        if (t.Length > 90) t = t[..90];

        var (hook, story, pin) = cat switch
        {
            StudioThemeCategory.BabyParenting => ("🍼 이 작은 순간이 우리에겐 가장 큰 기적이었어요",
                new[] { "하루하루 자라는 게 보일 때마다 마음이 몽글몽글해져요 🥹", "서툴지만 끝까지 해내는 모습에 엄마 아빠는 매일 감동 중 💛", "여러분 아기의 첫 순간도 댓글로 들려주세요 👶✨" },
                "🍼 여러분 아기의 '첫 순간'은 언제였나요? 댓글로 알려주세요! 구독하고 성장일기 함께 봐요 💛"),
            StudioThemeCategory.PetsCompanions => ("🐾 오늘도 우리 집 귀염둥이가 하루를 책임졌어요",
                new[] { "눈만 마주쳐도 웃음이 나는 이 존재 🥰", "말은 못 해도 마음은 다 통하는 사이 🐶🐱", "여러분 반려친구 자랑도 댓글로 부탁해요 💕" },
                "🐾 우리 아이 이름을 댓글로 알려주세요! 매일 귀여움 업로드 중 💕"),
            StudioThemeCategory.HealingNature => ("🌿 15초만 쉬어가요, 소리 켜고 보세요",
                new[] { "바쁜 하루 속 잠깐의 숨 고르기 🍃", "눈을 감으면 바람 소리가 들리는 것 같아요 🌙", "오늘 하루도 정말 수고 많았어요 🤍" },
                "🌿 지금 어디서 보고 계신가요? 다음 힐링 장소 추천도 환영해요 🍃"),
            _ => ("☕ 평범해서 더 소중한 오늘의 기록",
                new[] { "작은 루틴이 하루를 바꿔요 ✨", "느리게, 그러나 단단하게 채워가는 일상 🌤️", "여러분의 오늘은 어땠나요? 💬" },
                "☕ 여러분의 소확행 루틴을 댓글로 공유해 주세요! ✨"),
        };

        var tags = new List<string>(MaxTags);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string tag)
        {
            if (tags.Count >= MaxTags || string.IsNullOrWhiteSpace(tag)) return;
            var x = tag.Trim();
            if (x[0] != '#') x = "#" + x;
            if (x.Length < 2 || x.Contains(' ', StringComparison.Ordinal)) return;
            if (seen.Add(x)) tags.Add(x);
        }
        if ((platform & SocialPlatform.YouTubeShorts) != 0) Add("#Shorts");
        if ((platform & SocialPlatform.InstagramReels) != 0) Add("#Reels");
        if (extraTags is not null) foreach (var e in extraTags) Add(e);
        foreach (var x in TagsFor(cat)) Add(x);
        if ((platform & SocialPlatform.YouTubeShorts) != 0) Add("#YouTubeShorts");
        if ((platform & SocialPlatform.InstagramReels) != 0) Add("#InstaReels");
        foreach (var x in TagsFor(StudioThemeCategory.DailyVlog)) { if (tags.Count >= MinTags) break; Add(x); }

        var pinned = (platform & SocialPlatform.YouTubeShorts) != 0 ? pin : "";
        return new PlutoSocialPost(platform, t, hook, story, tags, pinned);
    }

    private static string DefaultTitle(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.BabyParenting => "우리 아기 성장일기 🍼",
        StudioThemeCategory.PetsCompanions => "오늘의 반려친구 🐾",
        StudioThemeCategory.HealingNature => "15초 힐링 타임 🌿",
        _ => "오늘의 일상 기록 ☕",
    };
}
