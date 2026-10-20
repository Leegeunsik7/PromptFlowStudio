using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>A/B 테스트용 제목 1종(유형 + 문구).</summary>
public sealed record SeoTitle(string Type, string Text);

/// <summary>육아 SEO 기획 결과. Ok=false 면 실패. Titles=A/B 3종, Keywords=주입 검색어, SavedPath=metadata_seo.json.</summary>
public sealed record BabySeoResult(
    bool Ok, string ThemeKey, IReadOnlyList<string> Keywords, IReadOnlyList<SeoTitle> Titles,
    string Description, IReadOnlyList<string> Hashtags, string ThumbnailText, string? SavedPath, string Message);

/// <summary>
/// [PromptFlowStudio] 육아 A/B SEO 에이전트(MarketerAgent 고도화) — 순수 BCL(외부 NuGet 0 · <b>완전 오프라인</b>, Gemini 불필요).
///   ▸ 육아 고효율 검색어(수면교육/이유식/돌전아기/터미타임/까꿍 등)를 테마별로 자동 탐색·주입.
///   ▸ CTR 극대화용 A/B 제목 <b>3종</b>: ①호기심 유발형 ②정보 제공형 ③B급 유머형.
///   ▸ 알고리즘 맞춤 메타데이터를 <c>%LocalAppData%/PromptFlowStudio/Projects/{id}/metadata_seo.json</c> 로 배출(양산 재사용).
///   ▸ 프리셋(<see cref="StoryboardProject.PresetKey"/>)이면 테마 전용 SEO 시드로 고품질, 아니면 제목/대사 기반 일반 생성.
/// </summary>
public sealed class BabySeoAgent
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // 전 육아 콘텐츠 공통 고효율 검색어.
    private static readonly string[] Universal = { "육아", "아기", "육아일상", "아기표정", "베이비", "아기영상" };

    private sealed record Seed(string[] Keywords, string Curiosity, string Info, string Humor, string[] Hashtags, string Thumb);

    // 테마별 SEO 시드(프리셋 키 기준).
    private static readonly Dictionary<string, Seed> Seeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lemon"] = new(new[]{"이유식","첫이유식","아기먹방","신맛반응","돌전아기"},
            "🍋 3초 뒤 표정 실화? 첫 레몬 먹은 아기 반응",
            "생후 6개월 아기 첫 레몬 시식 반응 총정리 | 신맛 표정 변화",
            "레몬 먹은 아기 = 인간 자격 박탈 ㅋㅋㅋ 표정 대참사",
            new[]{"아기","baby","먹방아기","이유식","돌전아기","babyshorts"},"첫 레몬 시식 😝"),
        ["naptime"] = new(new[]{"수면교육","아기잠","낮잠","잠투정","통잠"},
            "😴 절대 안 잔다던 아기의 최후 3초",
            "낮잠 거부하는 아기 수면교육 팁 | 잠투정 극복 실전",
            "아기 vs 잠 = 자존심 싸움 결말 ㅋㅋ (픽 쓰러짐 주의)",
            new[]{"아기","baby","수면교육","낮잠","육아꿀팁","babyshorts"},"낮잠 거부 😪"),
        ["csi"] = new(new[]{"웃긴아기","아기짤","아기표정","육아밈"},
            "🕵️ 사라진 쿠키의 진실… 유력 용의자는 본인",
            "아기 표정 변화 관찰 영상 | 진지 → 반전 표정 모음",
            "쿠키 훔친 범인 잡는 아기 형사 ㅋㅋ 자백해라 (실화)",
            new[]{"아기","baby","웃긴아기","아기짤","babyshorts","funnybaby"},"아기 명탐정 🔍"),
        ["penguin"] = new(new[]{"걸음마","첫걸음마","아기걸음마","발달","돌아기"},
            "🐧 2배속으로 보면 완벽한 펭귄입니다",
            "돌 아기 첫 걸음마 발달 영상 | 아장아장 균형 잡기",
            "펭귄인 줄… 아기 걸음마 2배속 = 남극 다큐 ㅋㅋ",
            new[]{"아기","baby","걸음마","첫걸음마","돌아기","babyshorts"},"펭귄 걸음마 🐧"),
        ["mukbang"] = new(new[]{"이유식","아기먹방","첫이유식","먹방아기","이유식레시피"},
            "🍚 생애 첫 이유식… 표정이 다 말해줍니다",
            "생후 6개월 첫 이유식 반응 & 시작 시기 총정리",
            "이유식 먹방 아기 = 온 얼굴 범벅 ㅋㅋ 츄릅 장인",
            new[]{"아기","baby","이유식","먹방아기","아기먹방","babyshorts"},"첫 이유식 🥄"),
        ["gym"] = new(new[]{"터미타임","발달","아기운동","목가누기","개월아기"},
            "💪 아기 헬스장 오픈… 프로의 터미타임 자세",
            "아기 터미타임 목 가누기 발달 운동 가이드",
            "아기 헬스 3세트 ㅋㅋ 부들부들 근성 실화냐",
            new[]{"아기","baby","터미타임","발달","육아","babyshorts"},"아기 헬스장 💪"),
        ["ceo"] = new(new[]{"웃긴아기","아기짤","육아밈","아기표정"},
            "👔 긴급 이사회 소집… 안건은 '간식'",
            "아기 진지한 표정 모음 | 리액션 영상",
            "아기 CEO 화상회의 ㅋㅋ 결론은 낮잠 (프로 직장인)",
            new[]{"아기","baby","웃긴아기","아기짤","funnybaby","babyshorts"},"아기 CEO 👔"),
        ["bath"] = new(new[]{"아기목욕","목욕놀이","목욕","개월아기","육아일상"},
            "🛁 평화롭던 목욕 시간… 3초 뒤 물바다",
            "아기 목욕 놀이 & 물 적응 시키는 법 총정리",
            "목욕탕 물장구 대참사 ㅋㅋ 온 집안 물바다 실화",
            new[]{"아기","baby","아기목욕","목욕놀이","육아","babyshorts"},"목욕 대참사 💦"),
        ["peekaboo"] = new(new[]{"까꿍","아기놀이","상호작용","발달놀이","돌전아기"},
            "🙈 까꿍… 다음 표정을 아무도 예측 못 함",
            "까꿍 놀이 아기 반응 & 두뇌 발달 효과",
            "까꿍에 진심인 아기 ㅋㅋ 리액션 미쳤다 (무한반복)",
            new[]{"아기","baby","까꿍","아기놀이","발달놀이","babyshorts"},"까꿍 반전 🙈"),
        ["snow"] = new(new[]{"첫눈","겨울아기","아기반응","눈오는날","개월아기"},
            "❄️ 생애 처음 본 눈… 아기의 철학이 시작된다",
            "아기 첫 눈 반응 영상 | 겨울 외출 준비물 팁",
            "첫 눈 만난 아기 = 세상 진지한 과학자 ㅋㅋ",
            new[]{"아기","baby","첫눈","겨울아기","육아일상","babyshorts"},"생애 첫 눈 ❄️"),
    };

    /// <summary>프로젝트에서 A/B SEO 패키지를 생성한다(오프라인). 프리셋이면 테마 시드, 아니면 제목/대사 기반 일반 생성. 실패 없이 항상 결과 반환.</summary>
    public BabySeoResult Generate(StoryboardProject project)
    {
        var key = (project?.PresetKey ?? "").Trim();
        if (project is not null && !string.IsNullOrEmpty(key) && Seeds.TryGetValue(key, out var seed))
        {
            var kws = seed.Keywords.Concat(Universal).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            var titles = new List<SeoTitle>
            {
                new("호기심 유발형", seed.Curiosity),
                new("정보 제공형",   seed.Info),
                new("B급 유머형",    seed.Humor),
            };
            var tags = seed.Hashtags.Concat(new[]{"shorts","아기짤"}).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
            var desc = BuildDescription(project.Title, titles, kws, tags);
            return new(true, key, kws, titles, desc, tags, seed.Thumb, null, "SEO 생성 완료");
        }

        // ── 일반(비프리셋) — 제목/대사에서 키워드 추출 + 유니버설 육아 키워드 ──
        var extracted = ExtractKeywords(project);
        var kws2 = extracted.Concat(Universal).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        var baseTitle = string.IsNullOrWhiteSpace(project?.Title) ? "아기 영상" : project!.Title.Trim();
        var titles2 = new List<SeoTitle>
        {
            new("호기심 유발형", $"😳 3초 뒤 반전 주의 — {baseTitle}"),
            new("정보 제공형",   $"{baseTitle} 반응 총정리 | 육아 일상"),
            new("B급 유머형",    $"{baseTitle} ㅋㅋㅋ 실화냐 (표정 대참사)"),
        };
        var tags2 = new[]{"아기","baby","육아","babyshorts","shorts","아기짤"}.ToList();
        var desc2 = BuildDescription(baseTitle, titles2, kws2, tags2);
        return new(true, "", kws2, titles2, desc2, tags2, baseTitle, null, "SEO 생성 완료(일반)");
    }

    /// <summary>SEO 결과를 metadata_seo.json 으로 영속화하고 경로를 반환(실패 시 null). trendingAudio 있으면 플랫폼별 유행 오디오 추천 태그도 합성.</summary>
    public string? Persist(string projectId, BabySeoResult seo, JsonObject? trendingAudio = null)
    {
        try
        {
            var dir = Path.Combine(LicenseService.DataAreaRoot(), "Projects", SafeSeg(projectId));
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "metadata_seo.json");
            var node = new JsonObject
            {
                ["themeKey"] = seo.ThemeKey,
                ["keywords"] = new JsonArray(seo.Keywords.Select(k => (JsonNode)k!).ToArray()),
                ["titles"] = new JsonArray(seo.Titles.Select(t => (JsonNode)new JsonObject { ["type"] = t.Type, ["text"] = t.Text }).ToArray()),
                ["description"] = seo.Description,
                ["hashtags"] = new JsonArray(seo.Hashtags.Select(h => (JsonNode)h!).ToArray()),
                ["hashtagLine"] = string.Join(" ", seo.Hashtags.Select(h => "#" + h)),
                ["thumbnailText"] = seo.ThumbnailText,
                ["generatedAtUtc"] = DateTime.UtcNow.ToString("o"),
            };
            if (trendingAudio is not null) node["trendingAudio"] = trendingAudio;   // 플랫폼별 유행 밈 BGM 추천 태그
            File.WriteAllText(path, node.ToJsonString(JsonOpts), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex) { Program.Log("BabySeoAgent.Persist", ex); return null; }
    }

    /// <summary>생성 + 영속화 한 번에(플랫폼별 유행 오디오 추천 태그 자동 합성 포함).</summary>
    public BabySeoResult GenerateAndSave(StoryboardProject project)
    {
        var r = Generate(project);
        if (!r.Ok || project is null) return r;
        var saved = Persist(project.Id, r, BuildTrendingAudio(project));
        return r with { SavedPath = saved };
    }

    // 플랫폼별 유행 밈 BGM 추천 태그(오프라인 authored). 실제 음원이 아닌 '추천 태그' — 사용 전 각 플랫폼 오디오 라이브러리/저작권 확인 필요.
    private static readonly string[] YtFast = { "Oh No (Kreepa) 밈 사운드", "Funny Quirky Comedy BGM", "귀여운 코믹 효과음 팩", "Baby Meme Sound Effect" };
    private static readonly string[] YtSlow = { "Warm Acoustic Vlog BGM", "잔잔한 브이로그 피아노", "Heartwarming Family Music", "Soft Lo-fi Chill" };
    private static readonly string[] IgFast = { "Trending Reels Funny Audio", "귀염뽀짝 릴스 밈 사운드", "Cute Baby Viral Audio", "Comedy Zoom SFX" };
    private static readonly string[] IgSlow = { "Aesthetic Lo-fi Reels", "감성 릴스 배경음", "Soft Piano Trending Audio", "Warm Vlog Ambient" };
    private static readonly string[] TtFast = { "Oh No Oh No No No 틱톡 밈", "TikTok Funny Viral Sound", "귀염뽀짝 틱톡 사운드", "Baby Comedy Trending" };
    private static readonly string[] TtSlow = { "Emotional Trending Sound", "잔잔 감성 틱톡 오디오", "Wholesome Baby BGM", "Aesthetic Soft Audio" };

    /// <summary>플랫폼별 유행 오디오 추천 태그(UI/JSON 공용 소스) — Energy 라벨 + 3 플랫폼 추천 밈 BGM 키워드 + 저작권 안내.</summary>
    public sealed record TrendingAudioTags(string Energy, string EnergyLabel,
        IReadOnlyList<string> YouTubeShorts, IReadOnlyList<string> InstagramReels, IReadOnlyList<string> TikTok, string Note);

    /// <summary>테마 배속(에너지)에 맞춘 플랫폼별 유행 오디오 추천 태그. 빠른 테마(배속&gt;1.2)=코믹 밈, 느린 테마=감성.</summary>
    public static TrendingAudioTags GetTrendingAudio(StoryboardProject? project)
    {
        var fast = (project?.PlaybackSpeed ?? 1.0) > 1.2;   // 테마 의도 에너지(배속) 기준
        return new TrendingAudioTags(
            fast ? "comedic-fast" : "wholesome-slow",
            fast ? "코믹·고속 (밈)" : "감성·잔잔",
            fast ? YtFast : YtSlow, fast ? IgFast : IgSlow, fast ? TtFast : TtSlow,
            "추천 태그(실제 음원 아님) — 각 플랫폼 오디오 라이브러리에서 유사 트렌딩 사운드 검색·저작권 확인 후 사용");
    }

    /// <summary>metadata_seo.json 배출용 트렌딩 오디오 JSON(<see cref="GetTrendingAudio"/> 공용 소스 직렬화).</summary>
    private static JsonObject BuildTrendingAudio(StoryboardProject project)
    {
        var t = GetTrendingAudio(project);
        static JsonArray Arr(IReadOnlyList<string> xs) => new(xs.Select(x => (JsonNode)x!).ToArray());
        return new JsonObject
        {
            ["energy"] = t.Energy,
            ["energyLabel"] = t.EnergyLabel,
            ["note"] = t.Note,
            ["youtubeShorts"] = Arr(t.YouTubeShorts),
            ["instagramReels"] = Arr(t.InstagramReels),
            ["tiktok"] = Arr(t.TikTok),
        };
    }

    private static string BuildDescription(string title, IReadOnlyList<SeoTitle> titles, IReadOnlyList<string> kws, IReadOnlyList<string> tags)
    {
        var sb = new StringBuilder();
        sb.AppendLine(titles[0].Text).AppendLine();
        sb.AppendLine("👶 우리 아기의 리얼 반응을 담았어요. 소리 켜고 보면 더 귀여워요 🔊").AppendLine();
        sb.Append("🔎 검색 키워드: ").AppendLine(string.Join(", ", kws.Take(8)));
        sb.AppendLine("🎬 제작: PromptFlow Studio · Wan 2.1 4K · Kokoro 나레이션 · Gen-Foley").AppendLine();
        sb.AppendLine(string.Join(" ", tags.Select(t => "#" + t)));
        return sb.ToString().Trim();
    }

    // 비프리셋: 제목 + 컷 대사에서 한글 명사 후보 추출(2글자+ 토큰, 상위 빈도).
    private static List<string> ExtractKeywords(StoryboardProject? project)
    {
        if (project is null) return new();
        var text = project.Title + " " + string.Join(" ", project.Scenes.Select(s => s.VoiceText));
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var raw in text.Split(new[] { ' ', '\n', '\t', ',', '.', '!', '?', '…' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (w.Length < 2 || Stop.Contains(w)) continue;
            freq[w] = freq.TryGetValue(w, out var c) ? c + 1 : 1;
        }
        return freq.OrderByDescending(kv => kv.Value).Take(6).Select(kv => kv.Key).ToList();
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    { "그리고","하지만","그런데","이것","저것","오늘","다시","정말","진짜","이제","우리","자꾸","그냥","한번","다음" };

    private static string SafeSeg(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "project";
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Trim();
    }
}
