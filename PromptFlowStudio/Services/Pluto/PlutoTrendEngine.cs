using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🪐 스튜디오 테마 카테고리 — 아기·반려동물·힐링·브이로그.</summary>
public enum StudioThemeCategory
{
    BabyParenting = 0,
    PetsCompanions = 1,
    HealingNature = 2,
    DailyVlog = 3,
}

/// <summary>🪐 대상 SNS 플랫폼 플래그.</summary>
[Flags]
public enum SocialPlatform
{
    None = 0,
    YouTubeShorts = 1,
    InstagramReels = 2,
    Both = YouTubeShorts | InstagramReels,
}

/// <summary>🪐 플루토 큐레이션 트렌드 카드 1건.</summary>
public sealed record PlutoTrend(
    string Id,
    StudioThemeCategory Category,
    string Title,
    string ViralHook,
    string VideoPrompt,
    string CameraMotion,
    string BgmStyle,
    SocialPlatform Platforms,
    IReadOnlyList<string> YouTubeHashtags,
    IReadOnlyList<string> InstagramHashtags)
{
    /// <summary>📋 복사용 — 훅 제목 + 플랫폼 해시태그(중복 제거).</summary>
    public string BuildCopyText()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder(ViralHook.Length + 128);
        sb.Append(ViralHook).Append('\n').Append('\n');
        var first = true;
        void Add(IReadOnlyList<string> tags)
        {
            for (var i = 0; i < tags.Count; i++)
            {
                if (!seen.Add(tags[i])) continue;
                if (!first) sb.Append(' ');
                sb.Append(tags[i]);
                first = false;
            }
        }
        if ((Platforms & SocialPlatform.YouTubeShorts) != 0) Add(YouTubeHashtags);
        if ((Platforms & SocialPlatform.InstagramReels) != 0) Add(InstagramHashtags);
        return sb.ToString();
    }

    /// <summary>🚀 스튜디오 주입용 프롬프트(테마 + 프롬프트 + 카메라).</summary>
    public string BuildStudioPrompt() => VideoPrompt + ", " + CameraMotion;
}

/// <summary>
/// 🪐 Pluto AI Trend &amp; Inspiration Engine — 4개 테마별 고전환 트렌드 카드 큐레이션(로컬 카탈로그 · 외부 호출 없음).
/// <see cref="PlutoLearningService"/> 주입 시 학습 가중치(80% 활용 · 20% 탐색)로 순위 재정렬.
/// </summary>
public sealed class PlutoTrendEngine
{
    private static readonly string[] YtBase = { "#Shorts", "#YouTubeShorts" };
    private static readonly string[] IgBase = { "#Reels", "#InstaReels" };

    private readonly PlutoLearningService? _learning;
    private readonly PlutoGenerativeEngine? _generator;
    private readonly PlutoTrend[] _catalog;

    public PlutoTrendEngine(PlutoLearningService? learning = null, PlutoGenerativeEngine? generator = null)
    {
        _learning = learning;
        _generator = generator;
        _catalog = BuildCatalog();
    }

    /// <summary>✨ 생성형 엔진 결선 여부(미주입 시 고정 카탈로그만).</summary>
    public bool CanDiscover => _generator is not null;

    /// <summary>
    /// ✨ 새로운 트렌드 발굴 — 생성형 엔진으로 4건 합성 후 학습 순위 적용. 생성기 부재/실패 시 고정 카탈로그로 1초 내 폴백([부분 구제]).
    /// </summary>
    public async Task<IReadOnlyList<PlutoTrend>> DiscoverFreshTrendsAsync(StudioThemeCategory category, int count = 4, CancellationToken ct = default)
    {
        if (_generator is null) return await GetCuratedTrendsAsync(category, ct).ConfigureAwait(false);
        try
        {
            var fresh = await _generator.SynthesizeAsync(category, count, null, ct).ConfigureAwait(false);
            if (_learning is null) return fresh;
            await _learning.EnsureLoadedAsync(ct).ConfigureAwait(false);
            return _learning.Rank(fresh);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto] discover fallback: " + ex.Message);
            return await GetCuratedTrendsAsync(category, ct).ConfigureAwait(false);
        }
    }

    public static IReadOnlyList<StudioThemeCategory> Categories { get; } = new[]
    {
        StudioThemeCategory.BabyParenting, StudioThemeCategory.PetsCompanions,
        StudioThemeCategory.HealingNature, StudioThemeCategory.DailyVlog,
    };

    public static string Label(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.BabyParenting => "🍼 아기 & 육아",
        StudioThemeCategory.PetsCompanions => "🐾 반려동물",
        StudioThemeCategory.HealingNature => "🌿 감성 힐링",
        StudioThemeCategory.DailyVlog => "☕ 일상 브이로그",
        _ => c.ToString(),
    };

    public static string EnglishName(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.BabyParenting => "Baby & Parenting",
        StudioThemeCategory.PetsCompanions => "Pets & Companions",
        StudioThemeCategory.HealingNature => "Healing & Nature",
        StudioThemeCategory.DailyVlog => "Daily Aesthetic Vlog",
        _ => c.ToString(),
    };

    /// <summary>전체 카탈로그(테스트·진단용).</summary>
    public IReadOnlyList<PlutoTrend> All => _catalog;

    public PlutoTrend? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var t in _catalog) if (string.Equals(t.Id, id, StringComparison.Ordinal)) return t;
        return _generator?.FindRecent(id);
    }

    /// <summary>카테고리별 3~4건 — 학습 서비스가 있으면 동적 순위.</summary>
    public async Task<IReadOnlyList<PlutoTrend>> GetCuratedTrendsAsync(StudioThemeCategory category, CancellationToken ct = default)
    {
        var list = new List<PlutoTrend>(4);
        foreach (var t in _catalog) if (t.Category == category) list.Add(t);
        if (_learning is null) return list;
        try
        {
            await _learning.EnsureLoadedAsync(ct).ConfigureAwait(false);
            return _learning.Rank(list);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto] rank fallback: " + ex.Message);
            return list;   // [부분 구제] 학습 실패 → 기본 순서
        }
    }

    private static string[] Tags(string[] baseTags, params string[] extra)
    {
        var r = new string[baseTags.Length + extra.Length];
        Array.Copy(baseTags, r, baseTags.Length);
        Array.Copy(extra, 0, r, baseTags.Length, extra.Length);
        return r;
    }

    private static PlutoTrend T(string id, StudioThemeCategory c, string title, string hook, string prompt, string cam, string bgm, SocialPlatform p, string[] topical)
        => new(id, c, title, hook, prompt, cam, bgm, p, Tags(YtBase, topical), Tags(IgBase, topical));

    private static PlutoTrend[] BuildCatalog() => new[]
    {
        // 🍼 Baby & Parenting
        T("baby-first-steps", StudioThemeCategory.BabyParenting, "첫 걸음마 순간", "드디어 첫 발을 뗐어요 👣 끝까지 보세요!",
            "cinematic shot of a baby taking first wobbly steps toward parent's open arms, warm living room, soft window light", "low-angle slow dolly-in", "Warm piano lullaby", SocialPlatform.Both,
            new[] { "#첫걸음마", "#육아브이로그", "#babyfirststeps", "#babymilestone" }),
        T("baby-sleeping-angel", StudioThemeCategory.BabyParenting, "잠자는 천사", "이 평화로움, 3초만 보고 가세요 😴",
            "newborn sleeping peacefully in a cream knit blanket, dreamy golden haze, tiny fingers curled", "slow overhead push-in", "Music box lullaby", SocialPlatform.Both,
            new[] { "#신생아", "#육아스타그램", "#sleepingbaby", "#newborn" }),
        T("baby-tummy-time", StudioThemeCategory.BabyParenting, "터미타임 챌린지", "오늘의 터미타임 기록은 몇 초? ⏱️",
            "baby doing tummy time on a soft play mat, lifting head with determined face, pastel nursery", "eye-level gentle orbit", "Playful ukulele", SocialPlatform.InstagramReels,
            new[] { "#터미타임", "#아기성장", "#tummytime", "#babydevelopment" }),
        T("baby-milestone-month", StudioThemeCategory.BabyParenting, "월령 성장 앨범", "0개월 → 12개월, 1년이 15초에 🍼",
            "month-by-month baby growth montage on the same teddy-bear backdrop, consistent framing, soft studio light", "locked-off center frame with match cut", "Uplifting acoustic guitar", SocialPlatform.Both,
            new[] { "#성장일기", "#월령사진", "#babymonthly", "#growingup" }),

        // 🐾 Pets & Companions
        T("pet-puppy-zoomies", StudioThemeCategory.PetsCompanions, "강아지 우다다", "갑자기 시작된 우다다 타임 🐶💨",
            "fluffy puppy doing zoomies across a sunny backyard lawn, ears flapping, playful energy", "fast tracking pan with slow-motion peak", "Bouncy upbeat pop", SocialPlatform.Both,
            new[] { "#강아지", "#우다다", "#puppyzoomies", "#dogsofinstagram" }),
        T("pet-sleepy-cat", StudioThemeCategory.PetsCompanions, "졸린 고양이", "눈 감기는 거 실시간 중계 🐱💤",
            "sleepy tabby cat slowly closing eyes on a windowsill, soft afternoon light, dust particles floating", "macro slow push-in", "Lo-fi chill beat", SocialPlatform.Both,
            new[] { "#고양이", "#냥스타그램", "#sleepycat", "#catsofinstagram" }),
        T("pet-sunlight-nap", StudioThemeCategory.PetsCompanions, "햇살 낮잠", "햇살 자리는 원래 내 거였어요 ☀️",
            "dog and cat napping together in a warm patch of sunlight on wooden floor, cozy home", "slow lateral slide", "Soft acoustic lounge", SocialPlatform.InstagramReels,
            new[] { "#반려동물", "#낮잠", "#cozypets", "#petnap" }),
        T("pet-treat-begging", StudioThemeCategory.PetsCompanions, "간식 앞 눈빛 공격", "이 눈빛을 이길 수 있는 사람? 🥺",
            "puppy with big pleading eyes staring at a treat, shallow depth of field, kitchen background", "slow zoom-in on eyes", "Comedic pizzicato", SocialPlatform.YouTubeShorts,
            new[] { "#간식", "#댕댕이", "#puppyeyes", "#dogtreats" }),

        // 🌿 Healing & Nature
        T("heal-rain-window", StudioThemeCategory.HealingNature, "창밖 빗소리", "1분만 쉬어가요 🌧️ 소리 켜고 보세요",
            "raindrops trickling down a window pane, blurred city lights bokeh, warm desk lamp inside", "static frame with subtle rack focus", "Rain ambience + soft piano", SocialPlatform.Both,
            new[] { "#빗소리", "#힐링", "#rainyday", "#asmr" }),
        T("heal-cozy-cafe", StudioThemeCategory.HealingNature, "아늑한 카페", "비 오는 날 이 카페 어때요? ☕",
            "cozy coffee shop interior, latte art pour in slow motion, steam rising, warm wood tones", "slow dolly across counter", "Jazz café bossa nova", SocialPlatform.InstagramReels,
            new[] { "#카페", "#감성카페", "#cozycafe", "#coffeetime" }),
        T("heal-golden-hour", StudioThemeCategory.HealingNature, "골든아워 글로우", "하루 중 가장 예쁜 10분 🌅",
            "golden hour sunlight through tall grass field, lens flare, gentle breeze, warm glow", "slow crane rise", "Ambient cinematic pads", SocialPlatform.Both,
            new[] { "#골든아워", "#노을", "#goldenhour", "#naturelovers" }),
        T("heal-campfire", StudioThemeCategory.HealingNature, "모닥불 멍", "불멍 15초, 마음이 녹아요 🔥",
            "gentle campfire crackling under a starry night sky, sparks drifting upward, forest silhouette", "low static close-up with slow pull-back", "Crackling fire + acoustic", SocialPlatform.YouTubeShorts,
            new[] { "#불멍", "#캠핑", "#campfire", "#relaxing" }),

        // ☕ Daily Aesthetic Vlog
        T("vlog-minimal-desk", StudioThemeCategory.DailyVlog, "미니멀 데스크 셋업", "생산성 200% 책상 공개 🖥️",
            "minimalist desk setup with warm lamp, plant, keyboard, morning light, clean aesthetic", "smooth top-down slide", "Lo-fi study beat", SocialPlatform.Both,
            new[] { "#데스크셋업", "#미니멀", "#desksetup", "#productivity" }),
        T("vlog-baking-cookies", StudioThemeCategory.DailyVlog, "쿠키 굽는 날", "집에서 만드는 카페 쿠키 🍪",
            "baking chocolate chip cookies, dough scooping, oven glow, cookies cooling on rack, cozy kitchen", "close-up handheld with match cuts", "Cheerful acoustic", SocialPlatform.InstagramReels,
            new[] { "#홈베이킹", "#쿠키", "#homebaking", "#cookies" }),
        T("vlog-clean-room", StudioThemeCategory.DailyVlog, "방 정리 비포애프터", "정리 전후 차이 실화? ✨",
            "satisfying room cleaning before-and-after, folding clothes, tidy shelves, bright airy room", "time-lapse wide with wipe transition", "Satisfying upbeat house", SocialPlatform.Both,
            new[] { "#방정리", "#청소브이로그", "#cleanwithme", "#roomaesthetic" }),
        T("vlog-morning-routine", StudioThemeCategory.DailyVlog, "모닝 루틴", "6시 기상 루틴 따라해보세요 🌞",
            "aesthetic morning routine, opening curtains, pouring coffee, journaling by window light", "gentle handheld follow", "Soft indie pop", SocialPlatform.YouTubeShorts,
            new[] { "#모닝루틴", "#일상브이로그", "#morningroutine", "#dailyvlog" }),
    };
}
