using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🌗 생성 컨텍스트 시드 — 계절 · 시간대.</summary>
public readonly record struct PlutoContextSeed(string SeasonEn, string SeasonKo, string TimeEn, string TimeKo, string LightingEn, string HashtagKo);

/// <summary>
/// ✨ Pluto Generative Engine — 고정 카드 대신 (계절·시간대 컨텍스트 × 테마 모티프 × DNA 렉시콘 상위 토큰 × 바이럴 3막 구조)
/// 조합으로 매 요청마다 새 트렌드 카드를 합성. 70% 학습 토큰 활용 / 30% 시드 어휘 탐색.
/// 🧠 <see cref="IPlutoLlmClient"/> 주입 시 <see cref="GenerateDynamicTrendsAsync"/> 가 Gemini 추론 우선 · 실패 시 로컬 조합기 무중단 폴백.
/// </summary>
public sealed class PlutoGenerativeEngine
{
    public const double ExploitRate = 0.70;
    public const string IdPrefix = "gen-";
    private const int MaxRecent = 64;

    private readonly PlutoPreferenceLexicon? _lexicon;
    private readonly IPlutoLlmClient? _llm;
    private readonly PlutoSocialRadarEngine? _radar;
    private readonly IPlutoMistakeNotebookService? _mistakes;
    private readonly Random _rng;
    private readonly object _sync = new();
    private readonly List<PlutoTrend> _recent = new();
    private long _serial;

    public PlutoGenerativeEngine() : this(null, null) { }

    public PlutoGenerativeEngine(PlutoPreferenceLexicon? lexicon, Random? rng = null) : this(lexicon, null, rng) { }

    public PlutoGenerativeEngine(PlutoPreferenceLexicon? lexicon, IPlutoLlmClient? llm, Random? rng = null) : this(lexicon, llm, null, rng) { }

    /// <summary>📈 <paramref name="radar"/> 주입 시 SNS 상위 바이럴 패턴(페이싱 · 훅 구조)을 LLM 요청 · 로컬 프롬프트에 반영.</summary>
    /// <summary>🛡️ <paramref name="mistakeNotebook"/> 주입 시 차단 토큰을 LLM 인스트럭션 말미에 부정 제약으로 덧붙임.</summary>
    public PlutoGenerativeEngine(PlutoPreferenceLexicon? lexicon, IPlutoLlmClient? llm, PlutoSocialRadarEngine? radar, Random? rng = null, IPlutoMistakeNotebookService? mistakeNotebook = null)
    {
        _lexicon = lexicon;
        _llm = llm;
        _radar = radar;
        _mistakes = mistakeNotebook;
        _rng = rng ?? new Random();
    }

    /// <summary>🍼 아이덴티티 볼트 — 활성 프로필 구분(Baby/Pet)과 테마 일치 시 캐릭터 일관성 앵커를 프롬프트 선두에 삽입.</summary>
    public PlutoIdentityVault? Identity { get; init; }

    /// <summary>🧠 마지막 <see cref="GenerateDynamicTrendsAsync"/> 가 LLM 결과를 사용했는지(false = 로컬 조합기).</summary>
    public bool LastUsedLlm { get; private set; }

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
        await EnsureRadarAsync(ct).ConfigureAwait(false);
        await EnsureIdentityAsync(ct).ConfigureAwait(false);
        return Synthesize(category, count, now ?? DateTime.Now);
    }

    private async Task EnsureRadarAsync(CancellationToken ct)
    {
        if (_radar is null) return;
        try { await _radar.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Gen] radar load: " + ex.Message); }   // [부분 구제] 벤치마크 패턴만 사용
    }

    private async Task EnsureIdentityAsync(CancellationToken ct)
    {
        if (Identity is null) return;
        try { await Identity.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Gen] identity load: " + ex.Message); }   // [부분 구제] 앵커 없이 생성
    }

    private async Task EnsureMistakesAsync(CancellationToken ct)
    {
        if (_mistakes is null) return;
        try { await _mistakes.EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Gen] mistakes load: " + ex.Message); }   // [부분 구제] 부정 제약 없이 생성
    }

    /// <summary>🛡️ LLM 인스트럭션 — 오답 노트 차단 토큰이 있으면 <c>Avoid motifs/styles: …</c> 부정 제약을 말미에 덧붙임(예외 격리).</summary>
    public string BuildLlmTrendInstruction()
    {
        if (_mistakes is null) return LlmTrendInstruction;
        string constraint;
        try { constraint = _mistakes.BuildNegativeConstraintPrompt(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Gen] mistakes: " + ex.Message); return LlmTrendInstruction; }
        return string.IsNullOrWhiteSpace(constraint) ? LlmTrendInstruction : LlmTrendInstruction + " " + constraint + ".";
    }

    /// <summary>🍼 활성 프로필 앵커(미주입/불일치 테마 → 빈 문자열).</summary>
    private string IdentityAnchor(StudioThemeCategory category)
    {
        if (Identity is null) return "";
        try { return PlutoIdentityVault.AnchorFor(Identity.ActiveProfile, category); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Gen] identity: " + ex.Message); return ""; }
    }

    public const string LlmTrendInstruction =
        "You are Pluto, a viral short-form video creative director for YouTube Shorts and Instagram Reels. " +
        "Using the given category, season, time of day and the creator's learned style tokens, invent fresh, non-repetitive vertical video concepts. " +
        "Respond with JSON only: {\"trends\":[{\"title\":string,\"hook\":string,\"prompt\":string,\"camera\":string,\"bgm\":string,\"hashtags\":[string]}]} - " +
        "title: short Korean title (max 30 chars); hook: Korean viral hook line with 1-2 emojis; " +
        "prompt: English text-to-video prompt in 3 acts (Hook -> Core emotion -> Lingering outro) weaving in the style tokens; " +
        "camera: English camera directive; bgm: English BGM style; hashtags: 4-8 Korean/English topical hashtags starting with '#'. " +
        "Every concept must differ in subject, action and setting.";

    /// <summary>
    /// 🧠 LLM 트렌드 발굴 — Gemini에 카테고리 · DNA 상위 토큰 · 계절 · 시간대 컨텍스트를 주어 N건(기본 4) 바이럴 컨셉 합성.
    /// LLM 미설정/예외/3.5초 초과/스키마 불일치 → 로컬 조합기로 무중단 폴백. 부족분은 로컬로 보충([부분 구제]).
    /// </summary>
    public async Task<IReadOnlyList<PlutoTrend>> GenerateDynamicTrendsAsync(StudioThemeCategory category, int count = 4, DateTime? now = null, CancellationToken ct = default)
    {
        if (_lexicon is not null)
        {
            try { await _lexicon.EnsureLoadedAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Gen] lexicon load: " + ex.Message); }
        }
        await EnsureRadarAsync(ct).ConfigureAwait(false);
        await EnsureIdentityAsync(ct).ConfigureAwait(false);
        await EnsureMistakesAsync(ct).ConfigureAwait(false);
        var t = now ?? DateTime.Now;
        var n = Math.Clamp(count, 1, MotifsFor(category).Length);
        LastUsedLlm = false;

        var text = await PlutoLlmBridge.TryCompleteAsync(_llm, BuildLlmTrendInstruction(), BuildLlmTrendRequest(category, n, t), 0.95, ct).ConfigureAwait(false);
        var parsed = text is null ? null : ParseLlmTrends(text, category, n, t);
        if (parsed is null) return Synthesize(category, n, t);

        LastUsedLlm = true;
        lock (_sync)
        {
            _recent.AddRange(parsed);
            if (_recent.Count > MaxRecent) _recent.RemoveRange(0, _recent.Count - MaxRecent);
        }
        if (parsed.Count >= n) return parsed;
        var merged = new List<PlutoTrend>(n);   // [부분 구제] 부족분 로컬 보충
        merged.AddRange(parsed);
        merged.AddRange(Synthesize(category, n - parsed.Count, t));
        return merged;
    }

    private string BuildLlmTrendRequest(StudioThemeCategory category, int count, DateTime now)
    {
        var ctx = ContextFor(now);
        var sb = new System.Text.StringBuilder(512);
        sb.Append("Category: ").Append(CategoryLabel(category)).Append('\n')
          .Append("Season: ").Append(ctx.SeasonEn).Append('\n')
          .Append("Time of day: ").Append(ctx.TimeEn).Append(" (").Append(ctx.LightingEn).Append(")\n")
          .Append("Count: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        var anchor = IdentityAnchor(category);
        if (anchor.Length > 0)
            sb.Append("Subject identity anchor (begin every prompt with exactly this to keep the same character): ").Append(anchor).Append('\n');
        AppendTokens(sb, "Learned lighting tokens", TopOrEmpty(DnaBucket.Lighting));
        AppendTokens(sb, "Learned camera tokens", TopOrEmpty(DnaBucket.Camera));
        AppendTokens(sb, "Learned atmosphere tokens", TopOrEmpty(DnaBucket.Atmosphere));
        var patterns = ViralPatterns(category);
        if (patterns.Count > 0)
        {
            sb.Append("Proven SNS viral formulas to weave into pacing and hooks: ");
            for (var i = 0; i < patterns.Count; i++)
            {
                if (i > 0) sb.Append(" | ");
                sb.Append(patterns[i].Title).Append(" — ").Append(PlutoSocialRadarEngine.PatternDirective(patterns[i]));
            }
            sb.Append('\n');
        }
        lock (_sync)
        {
            if (_recent.Count > 0)
            {
                sb.Append("Avoid repeating these recent titles: ");
                var from = Math.Max(0, _recent.Count - 8);
                for (var i = from; i < _recent.Count; i++) { if (i > from) sb.Append(" | "); sb.Append(_recent[i].Title); }
                sb.Append('\n');
            }
        }
        return sb.ToString();

        static void AppendTokens(System.Text.StringBuilder sb, string label, IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0) return;
            sb.Append(label).Append(": ");
            for (var i = 0; i < tokens.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(tokens[i]); }
            sb.Append('\n');
        }
    }

    private static string CategoryLabel(StudioThemeCategory c) => c switch
    {
        StudioThemeCategory.BabyParenting => "Baby & parenting (babies, toddlers, family milestones)",
        StudioThemeCategory.PetsCompanions => "Pets & companions (dogs, cats, small animals)",
        StudioThemeCategory.HealingNature => "Healing nature (calm landscapes, ASMR ambience)",
        _ => "Daily aesthetic vlog (routines, cafes, hobbies)",
    };

    /// <summary>LLM 트렌드 JSON 스키마 즉시 검증 — 프롬프트/훅 누락 항목 제외 · 중복 프롬프트 제거 · 최대 <paramref name="max"/>건. 유효 0건이면 null.</summary>
    public IReadOnlyList<PlutoTrend>? ParseLlmTrends(string json, StudioThemeCategory category, int max, DateTime now)
    {
        using var doc = PlutoLlmBridge.TryParse(json);
        if (doc is null) return null;
        var root = doc.RootElement;
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("trends", out arr) || arr.ValueKind != JsonValueKind.Array) return null;

        var ctx = ContextFor(now);
        var stamp = now.ToString("yyMMddHHmm", CultureInfo.InvariantCulture);
        var anchor = IdentityAnchor(category);
        var result = new List<PlutoTrend>(Math.Max(max, 1));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in arr.EnumerateArray())
        {
            if (result.Count >= max) break;
            var prompt = PlutoLlmBridge.Str(e, "prompt", 900);
            var hook = PlutoLlmBridge.Str(e, "hook", 120);
            if (prompt.Length < 12 || hook.Length == 0 || !seen.Add(prompt)) continue;
            prompt = PlutoIdentityVault.ApplyAnchor(prompt, anchor);   // 🍼 LLM 누락 대비 앵커 보장(멱등)
            var title = PlutoLlmBridge.Str(e, "title", 40);
            if (title.Length == 0) title = ctx.TimeKo + " · 플루토 추천";
            var camera = PlutoLlmBridge.Str(e, "camera", 160);
            if (camera.Length == 0) camera = "slow push-in";
            var bgm = PlutoLlmBridge.Str(e, "bgm", 80);
            if (bgm.Length == 0) bgm = BgmPool[RngNext(BgmPool.Length)].Name;

            var topical = new List<string>(10);
            var tagSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "#Shorts", "#YouTubeShorts", "#Reels", "#InstaReels" };
            if (e.TryGetProperty("hashtags", out var h) && h.ValueKind == JsonValueKind.Array)
                foreach (var x in h.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) PlutoLlmBridge.AddTag(topical, tagSeen, x.GetString(), 8);
            PlutoLlmBridge.AddTag(topical, tagSeen, CategoryTag(category), 9);
            PlutoLlmBridge.AddTag(topical, tagSeen, "#플루토추천", 10);

            var serial = Interlocked.Increment(ref _serial);
            var id = IdPrefix + "llm-" + ((int)category).ToString(CultureInfo.InvariantCulture) + "-" + stamp + "-" + serial.ToString(CultureInfo.InvariantCulture);
            var platforms = (result.Count % 3) switch { 0 => SocialPlatform.Both, 1 => SocialPlatform.InstagramReels, _ => SocialPlatform.YouTubeShorts };
            var yt = new string[2 + topical.Count];
            yt[0] = "#Shorts"; yt[1] = "#YouTubeShorts"; topical.CopyTo(yt, 2);
            var ig = new string[2 + topical.Count];
            ig[0] = "#Reels"; ig[1] = "#InstaReels"; topical.CopyTo(ig, 2);
            result.Add(new PlutoTrend(id, category, title, hook, prompt, camera, bgm, platforms, yt, ig));
        }
        return result.Count == 0 ? null : result;
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
        var patterns = ViralPatterns(category);
        var anchor = IdentityAnchor(category);

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
            if (patterns.Count > 0) prompt += "; SNS pacing: " + PlutoSocialRadarEngine.PatternDirective(patterns[n % patterns.Count]);
            prompt = PlutoIdentityVault.ApplyAnchor(prompt, anchor);   // 🍼 캐릭터 일관성 앵커 선두 삽입
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

    /// <summary>📈 레이더 상위 바이럴 패턴(미주입/예외 → 빈 목록).</summary>
    private IReadOnlyList<SocialTrendInsight> ViralPatterns(StudioThemeCategory category)
    {
        if (_radar is null) return Array.Empty<SocialTrendInsight>();
        try { return _radar.TopViralPatterns(category, 2); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Gen] radar: " + ex.Message); return Array.Empty<SocialTrendInsight>(); }
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
