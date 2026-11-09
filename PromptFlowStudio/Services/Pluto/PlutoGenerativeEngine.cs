using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🌗 생성 컨텍스트 시드 — 계절 · 시간대.</summary>
public readonly record struct PlutoContextSeed(string SeasonEn, string SeasonKo, string TimeEn, string TimeKo, string LightingEn, string HashtagKo);

/// <summary>
/// ✨ Pluto Generative Engine — 고정 카드 대신 (계절·시간대 컨텍스트 × 테마 모티프 × DNA 렉시콘 상위 토큰 × 바이럴 3막 구조)
/// 조합으로 매 요청마다 새 트렌드 카드를 합성(로컬 · 외부 호출 없음). 70% 학습 토큰 활용 / 30% 시드 어휘 탐색.
/// </summary>
public sealed class PlutoGenerativeEngine
{
    public const double ExploitRate = 0.70;
    public const string IdPrefix = "gen-";
    private const int MaxRecent = 64;

    private readonly PlutoPreferenceLexicon? _lexicon;
    private readonly Random _rng;
    private readonly object _sync = new();
    private readonly List<PlutoTrend> _recent = new();
    private long _serial;

    public PlutoGenerativeEngine() : this(null, null) { }

    public PlutoGenerativeEngine(PlutoPreferenceLexicon? lexicon, Random? rng = null)
    {
        _lexicon = lexicon;
        _rng = rng ?? new Random();
    }

    /// <summary>테마 모티프 — 주체 행동(Hook) · 감정 정점(Core) · 여운(Outro) · 한글 제목 · 주제 태그.</summary>
    private readonly record struct Motif(string Subject, string Hook, string Core, string Outro, string TitleKo, string BeatKo, string Tag);

    private static readonly Motif[] BabyMotifs =
    {
        new("baby", "reaching tiny hands toward a floating soap bubble", "bursting into a gummy giggle", "resting a chubby cheek on a parent's shoulder", "비눗방울 첫 만남", "까르르 웃음이 터졌어요", "#아기웃음"),
        new("toddler", "stacking colorful wooden blocks with focused eyes", "cheering as the wobbly tower stands", "clapping tiny hands in slow motion", "첫 블록 쌓기", "탑을 완성했어요", "#아기놀이"),
        new("newborn", "yawning and stretching inside a swaddle", "curling a fist around a parent's finger", "drifting back to sleep with a sigh", "신생아 기지개", "손가락을 꼭 잡았어요", "#신생아"),
        new("baby", "tasting a first spoon of puree", "scrunching up a surprised face", "grinning with messy cheeks", "첫 이유식 리액션", "표정이 대박이에요", "#이유식"),
        new("baby", "crawling across a soft rug toward a plush toy", "grabbing the toy in triumph", "rolling onto the back with a proud smile", "첫 배밀이 도전", "드디어 성공했어요", "#배밀이"),
        new("baby", "splashing in a tiny bathtub full of foam", "squealing with pure joy", "wrapped snugly in a hooded bunny towel", "첫 목욕 놀이", "물놀이에 신났어요", "#아기목욕"),
    };

    private static readonly Motif[] PetMotifs =
    {
        new("puppy", "pouncing on falling leaves", "spinning in a happy circle", "flopping down for belly rubs", "낙엽 사냥꾼", "빙글빙글 신났어요", "#댕댕이"),
        new("kitten", "stalking a feather toy behind a curtain", "leaping mid-air in slow motion", "kneading a fluffy blanket", "숨바꼭질 사냥", "점프 성공", "#아기고양이"),
        new("golden retriever", "waiting at the door with a toy in mouth", "greeting the owner with a full-body wiggle", "sighing contentedly at the owner's feet", "퇴근 마중", "온몸으로 반겨요", "#리트리버"),
        new("cat", "peeking out of a cardboard box", "booping the camera lens with a paw", "falling asleep in a sunbeam", "박스 속 냥이", "솜방망이 펀치", "#냥스타그램"),
        new("corgi", "trotting on short legs through a meadow", "turning back with a big open-mouth smile", "resting the chin on a picnic blanket", "코기 산책", "뒤돌아 웃어줘요", "#코기"),
        new("rabbit", "nibbling a fresh carrot leaf", "doing a joyful binky hop", "loafing with half-closed eyes", "토끼 간식타임", "점프를 했어요", "#토끼"),
    };

    private static readonly Motif[] HealingMotifs =
    {
        new("forest stream", "flowing over mossy stones", "catching glints of light on ripples", "fading into soft mist between trees", "숲속 계곡", "물빛이 반짝여요", "#숲멍"),
        new("ocean shore", "gentle waves rolling onto pebbles", "sea foam sparkling in the light", "the horizon dissolving into calm", "파도 소리", "마음이 잔잔해져요", "#바다멍"),
        new("wildflower field", "petals swaying in the breeze", "a butterfly landing on a bloom", "clouds drifting slowly overhead", "들꽃 언덕", "나비가 앉았어요", "#꽃멍"),
        new("lakeside cabin", "steam rising from a mug on the porch", "a lone boat gliding across still water", "lights flickering on at dusk", "호숫가 오두막", "고요가 내려앉아요", "#힐링여행"),
        new("tea ceremony", "hot water pouring into a ceramic cup", "tea leaves unfurling slowly", "a quiet sip by the window", "차 한 잔의 여유", "찻잎이 피어나요", "#티타임"),
        new("snowy pine forest", "snowflakes falling through branches", "footprints appearing in fresh powder", "a warm cabin window glowing in the distance", "설경 산책", "발자국이 남아요", "#눈멍"),
    };

    private static readonly Motif[] VlogMotifs =
    {
        new("home café", "grinding coffee beans by hand", "pouring silky latte art", "first sip with a satisfied smile", "홈카페 오픈", "라떼아트 성공", "#홈카페"),
        new("journaling desk", "opening a fresh notebook", "stamping and taping a collage page", "closing the book with a gentle pat", "다꾸 타임", "페이지가 완성됐어요", "#다꾸"),
        new("plant corner", "misting leafy houseplants", "spotting a brand-new sprout", "arranging pots on a sunny shelf", "식집사 일상", "새싹이 났어요", "#식집사"),
        new("weekend market", "browsing fresh produce stalls", "picking the perfect bouquet", "walking home with a tote bag", "주말 장보기", "꽃다발 득템", "#주말일상"),
        new("bookshop nook", "running fingers along book spines", "discovering a hidden favorite", "reading by the window seat", "책방 탐방", "인생책 발견", "#책스타그램"),
        new("kitchen", "chopping colorful vegetables", "plating a homemade brunch", "enjoying the first bite", "브런치 만들기", "플레이팅 완성", "#집밥"),
    };

    private static readonly string[] HookTemplatesKo =
    {
        "{ctx}의 {title}, 이 순간 놓치지 마세요 ✨",
        "{beat} 🥹 끝까지 보세요!",
        "{ctx} 감성 한 스푼 — {title} 🎬",
        "3초 만에 빠져드는 {title} 💫",
        "오늘의 {title}, {beat} 💛",
    };

    private static readonly (string Name, string Ko)[] BgmPool =
    {
        ("Warm piano lullaby", "따뜻한 피아노"), ("Music box melody", "오르골"), ("Uplifting acoustic guitar", "어쿠스틱 기타"),
        ("Lo-fi chill beat", "로파이"), ("Ambient cinematic pads", "앰비언트"), ("Playful ukulele", "우쿨렐레"),
    };

    public static PlutoContextSeed ContextFor(DateTime now, bool rainy = false)
    {
        var (sEn, sKo, sTag) = now.Month switch
        {
            3 or 4 or 5 => ("spring blossom breeze", "봄날", "#봄"),
            6 or 7 or 8 => ("bright summer air", "여름날", "#여름"),
            9 or 10 or 11 => ("falling autumn leaves", "가을날", "#가을"),
            _ => ("frosty winter window", "겨울날", "#겨울"),
        };
        var h = now.Hour;
        if (rainy) return new PlutoContextSeed(sEn, sKo, "rainy afternoon", "비 오는 오후", "overcast diffused light", sTag);
        return h switch
        {
            >= 5 and < 11 => new PlutoContextSeed(sEn, sKo, "early morning", "아침", "soft morning rays", sTag),
            >= 11 and < 16 => new PlutoContextSeed(sEn, sKo, "lazy afternoon", "오후", "dappled sunlight", sTag),
            >= 16 and < 20 => new PlutoContextSeed(sEn, sKo, "sunset", "노을 무렵", "golden hour", sTag),
            _ => new PlutoContextSeed(sEn, sKo, "quiet night", "밤", "warm desk lamp", sTag),
        };
    }

    private static Motif[] MotifsFor(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.BabyParenting => BabyMotifs,
        StudioThemeCategory.PetsCompanions => PetMotifs,
        StudioThemeCategory.HealingNature => HealingMotifs,
        _ => VlogMotifs,
    };

    private static string CategoryTag(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.BabyParenting => "#육아스타그램",
        StudioThemeCategory.PetsCompanions => "#반려동물",
        StudioThemeCategory.HealingNature => "#힐링",
        _ => "#일상브이로그",
    };

    /// <summary>최근 생성 카드(최신 우선 · Id 조회용).</summary>
    public PlutoTrend? FindRecent(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_sync)
            for (var i = _recent.Count - 1; i >= 0; i--)
                if (string.Equals(_recent[i].Id, id, StringComparison.Ordinal)) return _recent[i];
        return null;
    }

    /// <summary>✨ 새 트렌드 N건 합성 — 모티프 중복 없음 · DNA 상위 토큰 우선(70%) · 3막(Hook → Core Emotion → Lingering Outro).</summary>
    public async Task<IReadOnlyList<PlutoTrend>> SynthesizeAsync(StudioThemeCategory category, int count = 4, DateTime? now = null, CancellationToken ct = default)
    {
        if (_lexicon is not null)
        {
            try { await _lexicon.EnsureLoadedAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Gen] lexicon load: " + ex.Message); }   // [부분 구제] 시드 어휘만으로 생성
        }
        return Synthesize(category, count, now ?? DateTime.Now);
    }

    /// <summary>동기 합성(렉시콘은 이미 적재된 상태를 사용).</summary>
    public IReadOnlyList<PlutoTrend> Synthesize(StudioThemeCategory category, int count, DateTime now)
    {
        var motifs = MotifsFor(category);
        count = Math.Clamp(count, 1, motifs.Length);
        var order = new int[motifs.Length];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        lock (_rng)
        {
            for (var i = order.Length - 1; i > 0; i--) { var j = _rng.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        }
        var topL = TopOrEmpty(DnaBucket.Lighting);
        var topC = TopOrEmpty(DnaBucket.Camera);
        var topA = TopOrEmpty(DnaBucket.Atmosphere);
        var stamp = now.ToString("yyMMddHHmm", CultureInfo.InvariantCulture);

        var result = new List<PlutoTrend>(count);
        for (var n = 0; n < count; n++)
        {
            var m = motifs[order[n]];
            bool rainy;
            lock (_rng) rainy = _rng.NextDouble() < 0.25;
            var ctx = ContextFor(now, rainy);
            var lighting = Pick(topL, PlutoPreferenceLexicon.Seeds(DnaBucket.Lighting), ctx.LightingEn, n);
            var camera = Pick(topC, PlutoPreferenceLexicon.Seeds(DnaBucket.Camera), null, n);
            var atmosphere = Pick(topA, PlutoPreferenceLexicon.Seeds(DnaBucket.Atmosphere), null, n);
            var (bgm, _) = BgmPool[RngNext(BgmPool.Length)];

            var prompt = "Hook: " + m.Subject + " " + m.Hook + ", " + ctx.TimeEn + " with " + ctx.SeasonEn + ", " + lighting
                + "; Core emotion: " + m.Subject + " " + m.Core + ", " + atmosphere
                + "; Lingering outro: " + m.Outro + ", " + atmosphere + " mood";
            var template = HookTemplatesKo[RngNext(HookTemplatesKo.Length)];
            var hook = template.Replace("{ctx}", ctx.SeasonKo + " " + ctx.TimeKo, StringComparison.Ordinal)
                .Replace("{title}", m.TitleKo, StringComparison.Ordinal)
                .Replace("{beat}", m.BeatKo, StringComparison.Ordinal);
            var title = ctx.TimeKo + " · " + m.TitleKo;
            var serial = Interlocked.Increment(ref _serial);
            var id = IdPrefix + ((int)category).ToString(CultureInfo.InvariantCulture) + "-" + stamp + "-" + serial.ToString(CultureInfo.InvariantCulture);
            var platforms = (n % 3) switch { 0 => SocialPlatform.Both, 1 => SocialPlatform.InstagramReels, _ => SocialPlatform.YouTubeShorts };
            var topical = new[] { m.Tag, CategoryTag(category), ctx.HashtagKo, "#플루토추천" };
            var yt = new string[2 + topical.Length];
            yt[0] = "#Shorts"; yt[1] = "#YouTubeShorts"; Array.Copy(topical, 0, yt, 2, topical.Length);
            var ig = new string[2 + topical.Length];
            ig[0] = "#Reels"; ig[1] = "#InstaReels"; Array.Copy(topical, 0, ig, 2, topical.Length);
            result.Add(new PlutoTrend(id, category, title, hook, prompt, camera, bgm, platforms, yt, ig));
        }

        lock (_sync)
        {
            _recent.AddRange(result);
            if (_recent.Count > MaxRecent) _recent.RemoveRange(0, _recent.Count - MaxRecent);
        }
        return result;
    }

    private IReadOnlyList<string> TopOrEmpty(DnaBucket b) => _lexicon is null ? Array.Empty<string>() : _lexicon.TopTokens(b, 3);

    private int RngNext(int max) { lock (_rng) return _rng.Next(max); }

    /// <summary>학습 토큰 활용(70%, 카드마다 순환) / 컨텍스트 기본값 / 시드 어휘 탐색.</summary>
    private string Pick(IReadOnlyList<string> top, IReadOnlyList<string> seeds, string? contextDefault, int slot)
    {
        double roll;
        lock (_rng) roll = _rng.NextDouble();
        if (top.Count > 0 && roll < ExploitRate) return top[slot % top.Count];
        if (contextDefault is not null && roll < 0.85) return contextDefault;
        return seeds[RngNext(seeds.Count)];
    }
}
