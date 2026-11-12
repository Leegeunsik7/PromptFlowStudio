using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🔥 바이럴 트렌드 분석 축 — 훅(첫 1.5초) · 렌즈/앵글 · 조명 · 페이싱.</summary>
public enum ViralTrendFacet
{
    Hook = 0,
    Lens = 1,
    Lighting = 2,
    Pacing = 3,
}

/// <summary>🔥 바이럴 트렌드 토큰 1건 — 태그(<c>#POV</c> 등) · 축 · 영문 프롬프트 모디파이어 · 점수(0 초과).</summary>
public sealed record ViralTrendToken(string Tag, ViralTrendFacet Facet, string Modifier, double Score);

/// <summary>🔥 메모리에 적재된 활성 바이럴 트렌드(가중치 내림차순 조회 결과).</summary>
public readonly record struct ActiveViralTrend(string Tag, double Weight);

/// <summary>🔥 카테고리 바이럴 분석 결과 — 축별 대표 지시문 + 점수 내림차순 토큰.</summary>
public sealed record ViralTrendAnalysis(
    StudioThemeCategory Category,
    string HookStructure,
    string LensAngle,
    string Lighting,
    string Pacing,
    IReadOnlyList<ViralTrendToken> Tokens);

/// <summary>
/// 🔥 Pluto 바이럴 트렌드 분석기 — YouTube Shorts · Instagram Reels 아기/반려동물 숏폼의 훅 구조(첫 1.5초) · 렌즈/앵글 · 조명 · 페이싱 패턴을
/// 태그(<c>#POV</c> · <c>#ASMR_Whisper</c> · <c>#MicroZoomHook</c> · <c>#VintageFilm16mm</c> · <c>#GoldenHourGaze</c>)로 분류.
/// 카테고리 사전 점수 + 표본 카드(프롬프트 · 카메라 · 훅 · 해시태그) 키워드 적중 보너스. 순수 결정론 · 외부 호출 없음.
/// </summary>
public static class PlutoTrendAnalyzer
{
    public const string Pov = "#POV";
    public const string AsmrWhisper = "#ASMR_Whisper";
    public const string MicroZoomHook = "#MicroZoomHook";
    public const string VintageFilm16mm = "#VintageFilm16mm";
    public const string GoldenHourGaze = "#GoldenHourGaze";

    /// <summary>표본 카드 1건당 키워드 적중 보너스.</summary>
    public const double HitBonus = 0.15;
    /// <summary>태그당 보너스 상한.</summary>
    public const double MaxBonus = 0.6;

    private sealed record TagDef(string Tag, ViralTrendFacet Facet, string Modifier, string[] Keywords);

    /// <summary>🎯 태그 사전(고정 순서 — 동점 시 안정 정렬 기준).</summary>
    private static readonly TagDef[] Tags =
    {
        new(Pov, ViralTrendFacet.Hook,
            "first-person POV opening shot that pulls the viewer in within the first 1.5s",
            new[] { "pov", "first-person", "point of view", "eye-level", "시점" }),
        new(MicroZoomHook, ViralTrendFacet.Hook,
            "macro micro-zoom snap onto the subject's eyes in the first 1.5s hook",
            new[] { "zoom", "macro", "close-up", "closeup", "push-in", "클로즈업", "줌" }),
        new(AsmrWhisper, ViralTrendFacet.Pacing,
            "ASMR whisper pacing, close-mic soft ambience with lingering 1.2s cuts",
            new[] { "asmr", "whisper", "soft sound", "tapping", "속삭", "소리" }),
        new(VintageFilm16mm, ViralTrendFacet.Lens,
            "16mm vintage film lens with fine grain and gentle halation",
            new[] { "16mm", "film grain", "vintage", "retro", "home video", "레트로", "필름" }),
        new(GoldenHourGaze, ViralTrendFacet.Lighting,
            "golden-hour backlight with a direct warm gaze into the lens",
            new[] { "golden hour", "golden-hour", "sunset", "backlight", "window light", "노을", "햇살" }),
    };

    /// <summary>카테고리별 사전 점수(Tags 순서와 동일 · 0~1).</summary>
    private static double[] Priors(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.PetsCompanions => new[] { 0.90, 0.80, 0.60, 0.40, 0.60 },
        StudioThemeCategory.HealingNature => new[] { 0.40, 0.30, 0.90, 0.60, 0.85 },
        StudioThemeCategory.DailyVlog => new[] { 0.90, 0.50, 0.50, 0.80, 0.60 },
        _ => new[] { 0.60, 0.90, 0.50, 0.60, 0.80 },   // BabyParenting
    };

    /// <summary>카테고리별 컷 페이싱 기준.</summary>
    public static string PacingFor(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.PetsCompanions => "0.8s snap cuts on every reaction beat",
        StudioThemeCategory.HealingNature => "1.5s slow dissolves with breathing room",
        StudioThemeCategory.DailyVlog => "0.9s jump cuts synced to the beat",
        _ => "1.0s beat cuts landing on each smile",
    };

    /// <summary>🎯 지원 태그 전체(고정 순서).</summary>
    public static IReadOnlyList<string> KnownTags { get; } = new[] { Pov, MicroZoomHook, AsmrWhisper, VintageFilm16mm, GoldenHourGaze };

    /// <summary>태그 → 프롬프트 모디파이어(미지 태그 → null).</summary>
    public static string? ModifierFor(string? tag)
    {
        if (tag is null) return null;
        foreach (var d in Tags)
            if (string.Equals(d.Tag, tag, StringComparison.OrdinalIgnoreCase)) return d.Modifier;
        return null;
    }

    /// <summary>태그 → 분석 축(미지 태그 → null).</summary>
    public static ViralTrendFacet? FacetFor(string? tag)
    {
        if (tag is null) return null;
        foreach (var d in Tags)
            if (string.Equals(d.Tag, tag, StringComparison.OrdinalIgnoreCase)) return d.Facet;
        return null;
    }

    /// <summary>🔥 카테고리 + 표본 트렌드 카드 → 바이럴 토큰(점수 내림차순 · 동점 사전 순). 표본 없음 → 카테고리 사전 점수만.</summary>
    public static ViralTrendAnalysis Analyze(StudioThemeCategory category, IReadOnlyList<PlutoTrend>? samples = null)
    {
        var priors = Priors(category);
        var scores = new double[Tags.Length];
        for (var i = 0; i < Tags.Length; i++)
        {
            var bonus = 0.0;
            if (samples is not null)
                for (var s = 0; s < samples.Count && bonus < MaxBonus; s++)
                    if (samples[s] is { } t && Hits(t, Tags[i].Keywords)) bonus += HitBonus;
            scores[i] = priors[i] + Math.Min(bonus, MaxBonus);
        }

        var idx = new int[Tags.Length];
        for (var i = 0; i < idx.Length; i++) idx[i] = i;
        Array.Sort(idx, (a, b) => { var c = scores[b].CompareTo(scores[a]); return c != 0 ? c : a.CompareTo(b); });

        var tokens = new ViralTrendToken[idx.Length];
        for (var i = 0; i < idx.Length; i++)
        {
            var d = Tags[idx[i]];
            tokens[i] = new ViralTrendToken(d.Tag, d.Facet, d.Modifier, Math.Round(scores[idx[i]], 4));
        }
        return new ViralTrendAnalysis(category,
            TopModifier(tokens, ViralTrendFacet.Hook, "eye-catching reaction close-up within the first 1.5s"),
            TopModifier(tokens, ViralTrendFacet.Lens, "eye-level 35mm vertical framing"),
            TopModifier(tokens, ViralTrendFacet.Lighting, "soft natural window light"),
            TopModifier(tokens, ViralTrendFacet.Pacing, "") is { Length: > 0 } p ? PacingFor(category) + ", " + p : PacingFor(category),
            tokens);
    }

    /// <summary>🌐 실시간 키워드 분류 — 날씨/계절 · 간식/먹거리 · 밈/챌린지 · 기타 일상.</summary>
    public enum LiveTrendKind
    {
        Lifestyle = 0,
        Weather = 1,
        Snack = 2,
        Meme = 3,
    }

    private static readonly string[] WeatherKeywords =
    {
        "날씨", "비", "눈", "폭설", "한파", "폭염", "태풍", "장마", "미세먼지", "황사", "기온", "추위", "더위", "단풍", "벚꽃", "첫눈", "소나기",
        "크리스마스", "추석", "설날", "연휴", "봄", "여름", "가을", "겨울", "weather", "rain", "snow", "storm",
    };

    private static readonly string[] SnackKeywords =
    {
        "과자", "간식", "라면", "치킨", "디저트", "빵", "케이크", "쿠키", "초콜릿", "아이스크림", "커피", "음료", "맛집", "레시피", "탕후루", "붕어빵",
        "편의점", "신상", "snack", "food", "recipe",
    };

    private static readonly string[] MemeKeywords =
    {
        "챌린지", "밈", "유행", "짤", "댄스", "릴스", "쇼츠", "틱톡", "바이럴", "challenge", "meme", "viral", "dance",
    };

    /// <summary>키워드 → 분류(우선순위: 밈 → 간식 → 날씨 → 일상). 단음절 키워드("비"·"눈")는 전체 일치/공백 토큰 일치만 인정.</summary>
    public static LiveTrendKind ClassifyLiveKeyword(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return LiveTrendKind.Lifestyle;
        if (MatchAny(keyword, MemeKeywords)) return LiveTrendKind.Meme;
        if (MatchAny(keyword, SnackKeywords)) return LiveTrendKind.Snack;
        if (MatchAny(keyword, WeatherKeywords)) return LiveTrendKind.Weather;
        return LiveTrendKind.Lifestyle;
    }

    private static bool MatchAny(string keyword, string[] dict)
    {
        foreach (var k in dict)
        {
            if (k.Length <= 1)
            {
                if (string.Equals(keyword, k, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var part in keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    if (string.Equals(part, k, StringComparison.OrdinalIgnoreCase)) return true;
                continue;
            }
            if (keyword.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 🌐 실시간 급상승 키워드 → 니치(아기/반려동물/일상) 숏폼 표본 카드 — 분류별 훅/카메라 문구가 태그 사전 키워드를 포함해
    /// <see cref="Analyze"/> 적중 보너스로 이어짐(날씨→골든아워·창가 빛 · 간식→매크로 줌·ASMR · 밈→POV · 일상→레트로 필름). 빈 입력 → 빈 목록.
    /// </summary>
    public static IReadOnlyList<PlutoTrend> BuildLiveTrendSamples(StudioThemeCategory niche, IReadOnlyList<PlutoLiveTrendKeyword>? keywords)
    {
        if (keywords is null || keywords.Count == 0) return Array.Empty<PlutoTrend>();
        var subject = niche switch
        {
            StudioThemeCategory.PetsCompanions => "a curious puppy and kitten",
            StudioThemeCategory.BabyParenting => "a giggling baby",
            _ => "a cozy everyday moment",
        };
        var nicheTag = niche switch
        {
            StudioThemeCategory.PetsCompanions => "#반려동물",
            StudioThemeCategory.BabyParenting => "#육아",
            _ => "#일상",
        };
        var list = new List<PlutoTrend>(keywords.Count);
        for (var i = 0; i < keywords.Count; i++)
        {
            var kw = keywords[i]?.Keyword;
            if (string.IsNullOrWhiteSpace(kw)) continue;
            var (hook, prompt, camera) = ClassifyLiveKeyword(kw) switch
            {
                LiveTrendKind.Weather => ("오늘 '" + kw + "' 날씨 속 " + subject + " 반응",
                    subject + " watching today's " + kw + " weather by the window, golden hour backlight, warm window light",
                    "slow push-in at eye-level"),
                LiveTrendKind.Snack => ("요즘 핫한 '" + kw + "' 첫 맛 리액션",
                    subject + " discovering the trending " + kw + " snack, macro close-up on tiny hands or paws, soft sound ASMR crunch",
                    "macro micro-zoom close-up"),
                LiveTrendKind.Meme => ("'" + kw + "' 밈 따라하기 POV",
                    subject + " recreating the viral " + kw + " meme, first-person POV reaction",
                    "handheld POV eye-level"),
                _ => ("오늘의 화제 '" + kw + "' × " + subject,
                    subject + " in a playful nod to today's trending topic " + kw + ", vintage home video film grain look",
                    "gentle handheld retro 16mm drift"),
            };
            var tag = "#" + kw.Replace(" ", "", StringComparison.Ordinal);
            list.Add(new PlutoTrend("live-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), niche,
                kw, hook, prompt, camera, "trending", SocialPlatform.Both,
                new[] { "#Shorts", tag, nicheTag }, new[] { "#Reels", tag, nicheTag }));
        }
        return list;
    }

    private static string TopModifier(ViralTrendToken[] tokens, ViralTrendFacet facet, string fallback)
    {
        foreach (var t in tokens) if (t.Facet == facet) return t.Modifier;   // 점수 내림차순 → 첫 적중이 최상위
        return fallback;
    }

    private static bool Hits(PlutoTrend t, string[] keywords)
    {
        foreach (var k in keywords)
        {
            if (Has(t.VideoPrompt, k) || Has(t.CameraMotion, k) || Has(t.ViralHook, k) || Has(t.Title, k)) return true;
            if (AnyHas(t.YouTubeHashtags, k) || AnyHas(t.InstagramHashtags, k)) return true;
        }
        return false;
    }

    private static bool Has(string? s, string k) => s is not null && s.Contains(k, StringComparison.OrdinalIgnoreCase);

    private static bool AnyHas(IReadOnlyList<string>? list, string k)
    {
        if (list is null) return false;
        for (var i = 0; i < list.Count; i++) if (Has(list[i], k)) return true;
        return false;
    }
}
