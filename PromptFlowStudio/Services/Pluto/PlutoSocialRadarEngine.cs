using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 📈 Pluto Social Trend Radar — YouTube Shorts 급상승 공식 · Instagram Reels 감성 무드 채널 아키타입 분해(훅 페이싱 · 15초 루프 · BGM · 컬러).
/// 🧠 <see cref="IPlutoLlmClient"/> 우선(3.5초 예산) · 미설정/예외/초과/스키마 불일치 → 내장 12개 벤치마크로 무중단 폴백.
/// 학습(<see cref="LearnChannelStyleAsync"/>) → 채널 친숙도 가중치를 <c>pluto_social_radar.json</c> 에 원자적 영속 + Pluto XP +2.
/// </summary>
public sealed class PlutoSocialRadarEngine : IDisposable
{
    public const string FileName = "pluto_social_radar.json";
    public const string IdPrefix = "srl-";
    public const int LearnXp = 2;
    public const double LearnDelta = 1.0;
    public const double MaxWeight = 20.0;
    public const int MaxLlmInsights = 6;
    private const int MaxLlmCache = 64;

    private readonly string _path;
    private readonly IPlutoLlmClient? _llm;
    private readonly PlutoLearningService? _learning;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<string, SocialTrendInsight> _known = new(StringComparer.Ordinal);
    private readonly List<string> _llmIds = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private bool _loaded;
    private long _serial;
    private int _disposed;

    public PlutoSocialRadarEngine() : this(null, null, null) { }

    public PlutoSocialRadarEngine(string? storagePath, IPlutoLlmClient? llm = null, PlutoLearningService? learning = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", FileName)
            : storagePath;
        _llm = llm;
        _learning = learning;
        foreach (var b in Benchmarks) _known[b.Id] = b;
    }

    public string StoragePath => _path;

    /// <summary>🧠 마지막 <see cref="GetTrendingSocialInsightsAsync"/> 가 LLM 결과를 사용했는지.</summary>
    public bool LastUsedLlm { get; private set; }

    /// <summary>학습 상태 변경 통지(백그라운드 스레드 가능).</summary>
    public event Action? Changed;

    /// <summary>영속 엔트리 — 친숙도 가중치 + 인사이트 스냅샷(LLM 인사이트도 재시작 후 패턴 주입 가능).</summary>
    public sealed class Entry
    {
        public double Weight { get; set; }
        public int Learned { get; set; }
        public DateTime LastUtc { get; set; }
        public SocialPlatform Platform { get; set; }
        public SocialChannelCategory Category { get; set; }
        public string Title { get; set; } = "";
        public string ViralMechanism { get; set; } = "";
        public string PacingGuideline { get; set; } = "";
        public string Camera { get; set; } = "";
        public string Lighting { get; set; } = "";
        public string SubjectAction { get; set; } = "";
        public SocialBgmStyle Bgm { get; set; }
        public List<string> Hashtags { get; set; } = new();
    }

    // ══════════════════════ 📚 내장 벤치마크 12종(오프라인 폴백) ══════════════════════

    private static SocialTrendInsight B(string id, SocialPlatform p, SocialChannelCategory c, string title, string mech, string cam, string light, string action, string pacing, SocialBgmStyle bgm, params string[] tags)
        => new(id, p, c, title, mech, new SocialPromptTags(cam, light, action), pacing, bgm, tags);

    /// <summary>📚 검증된 채널 아키타입 12종 — YouTube Shorts 6 · Instagram Reels 6(4대 카테고리 커버).</summary>
    public static readonly IReadOnlyList<SocialTrendInsight> Benchmarks = new[]
    {
        B("srl-yt-01", SocialPlatform.YouTubeShorts, SocialChannelCategory.BabyMilestones, "1-Second Hook Toddler Zoom",
            "0-2s rapid eye-contact cut + 15s seamless audio loop", "fast punch-in zoom to eye level", "bright soft daylight", "toddler turning to the lens with wide surprised eyes",
            "0.8s - 1.2s rapid pacing", SocialBgmStyle.MusicBoxReverb, "#Shorts", "#YouTubeShorts", "#아기쇼츠", "#육아브이로그", "#babyshorts", "#cutebaby"),
        B("srl-yt-02", SocialPlatform.YouTubeShorts, SocialChannelCategory.BabyMilestones, "First Steps Countdown Reveal",
            "3-2-1 on-screen countdown + payoff at 6s + replay loop", "low-angle tracking dolly", "warm golden window light", "baby wobbling into first steps toward a parent",
            "1.0s - 1.5s build-up cuts", SocialBgmStyle.AcousticWarmth, "#Shorts", "#첫걸음", "#육아스타그램", "#babyfirststeps", "#milestone"),
        B("srl-yt-03", SocialPlatform.YouTubeShorts, SocialChannelCategory.PetHealing, "Puppy Reaction Freeze-Frame",
            "freeze-frame on peak reaction + zoom punch + text caption", "handheld snap zoom", "natural afternoon light", "puppy tilting its head at a squeaky toy",
            "0.6s - 1.0s snappy cuts", SocialBgmStyle.LullabyLoFi, "#Shorts", "#댕댕이", "#강아지쇼츠", "#puppy", "#doglover"),
        B("srl-yt-04", SocialPlatform.YouTubeShorts, SocialChannelCategory.PetHealing, "Cat POV Before/After Split",
            "split-screen before/after transformation at 3s", "static top-down then whip pan", "cozy indoor lamp light", "kitten curling up into a perfect loaf",
            "1.0s - 1.2s split beats", SocialBgmStyle.MusicBoxReverb, "#Shorts", "#냥스타그램", "#고양이쇼츠", "#catsofyoutube", "#kitten"),
        B("srl-yt-05", SocialPlatform.YouTubeShorts, SocialChannelCategory.EmotionalASMR, "Satisfying Rain Window Loop",
            "no-talk ambient hook + perfect loop endpoint for rewatch", "locked-off macro on glass", "overcast diffused blue light", "raindrops racing down a cafe window",
            "2.0s - 3.0s slow dissolves", SocialBgmStyle.LullabyLoFi, "#Shorts", "#ASMR", "#빗소리", "#rainasmr", "#relaxing"),
        B("srl-yt-06", SocialPlatform.YouTubeShorts, SocialChannelCategory.DailyAesthetic, "60-Second Morning Routine Speedrun",
            "timestamp overlay + jump-cut montage + reveal at end", "overhead and POV jump cuts", "crisp morning sunlight", "making a latte and opening curtains in sequence",
            "0.5s - 0.8s jump-cut rhythm", SocialBgmStyle.AcousticWarmth, "#Shorts", "#모닝루틴", "#브이로그", "#morningroutine", "#dayinmylife"),
        B("srl-ig-01", SocialPlatform.InstagramReels, SocialChannelCategory.DailyAesthetic, "Warm Sunlight Nostalgia Reel",
            "film-grain warm grade + slow 15s loop with soft text overlay", "slow push-in through sheer curtains", "golden hour sun flare", "hands arranging dried flowers on a linen table",
            "2.0s - 2.5s lingering shots", SocialBgmStyle.LullabyLoFi, "#Reels", "#감성릴스", "#일상기록", "#aestheticreels", "#slowliving"),
        B("srl-ig-02", SocialPlatform.InstagramReels, SocialChannelCategory.BabyMilestones, "Pastel Baby Vignette Diary",
            "pastel color grade + handwritten caption beats + soft vignette", "gentle handheld float", "soft pastel window light", "baby giggling while holding a parent's finger",
            "1.5s - 2.0s soft beats", SocialBgmStyle.MusicBoxReverb, "#Reels", "#아기릴스", "#육아일기", "#babyreels", "#motherhood"),
        B("srl-ig-03", SocialPlatform.InstagramReels, SocialChannelCategory.PetHealing, "Cozy Pet Nap Cinemagraph",
            "cinemagraph-style stillness + single moving element + loop", "locked-off eye-level macro", "warm blanket ambient glow", "cat breathing slowly while napping in a sunbeam",
            "3.0s held frames", SocialBgmStyle.LullabyLoFi, "#Reels", "#반려동물", "#냥스타그램", "#petreels", "#cozyvibes"),
        B("srl-ig-04", SocialPlatform.InstagramReels, SocialChannelCategory.EmotionalASMR, "Tea Pour ASMR Mood",
            "close-up sound-led hook + steam detail + slow-motion pour", "slow-motion macro tilt", "candlelit amber tone", "hot tea pouring into a ceramic cup with rising steam",
            "2.5s - 3.0s slow pacing", SocialBgmStyle.AcousticWarmth, "#Reels", "#티타임", "#힐링", "#asmrreels", "#teatime"),
        B("srl-ig-05", SocialPlatform.InstagramReels, SocialChannelCategory.DailyAesthetic, "Photo-Dump Beat-Sync Carousel",
            "beat-synced photo-dump cuts + warm preset + last-frame callback", "quick stills with micro zoom", "warm film-preset tones", "weekend café, flowers and sunset snapshots in sequence",
            "0.4s - 0.6s beat-sync cuts", SocialBgmStyle.AcousticWarmth, "#Reels", "#포토덤프", "#주말일상", "#photodump", "#weekendvibes"),
        B("srl-ig-06", SocialPlatform.InstagramReels, SocialChannelCategory.EmotionalASMR, "Healing Forest Slow Reel",
            "nature ambience hook + slow vertical tilt + breathing text", "slow vertical tilt up through trees", "misty morning light rays", "sunbeams filtering through a quiet pine forest",
            "3.0s - 4.0s meditative holds", SocialBgmStyle.MusicBoxReverb, "#Reels", "#숲멍", "#힐링릴스", "#naturereels", "#calm"),
    };

    // ══════════════════════ 🔁 매핑/레이블 ══════════════════════

    public static StudioThemeCategory ToTheme(SocialChannelCategory c) => c switch
    {
        SocialChannelCategory.BabyMilestones => StudioThemeCategory.BabyParenting,
        SocialChannelCategory.PetHealing => StudioThemeCategory.PetsCompanions,
        SocialChannelCategory.EmotionalASMR => StudioThemeCategory.HealingNature,
        _ => StudioThemeCategory.DailyVlog,
    };

    public static BabyBgmTrack ToBgmTrack(SocialBgmStyle s) => s switch
    {
        SocialBgmStyle.AcousticWarmth => BabyBgmTrack.Acoustic,
        SocialBgmStyle.MusicBoxReverb => BabyBgmTrack.MusicBox,
        _ => BabyBgmTrack.Lullaby,
    };

    public static string BgmLabel(SocialBgmStyle s) => s switch
    {
        SocialBgmStyle.AcousticWarmth => "🎸 어쿠스틱 웜",
        SocialBgmStyle.MusicBoxReverb => "🎠 오르골 리버브",
        _ => "🌙 자장가 로파이",
    };

    public static string CategoryLabel(SocialChannelCategory c) => c switch
    {
        SocialChannelCategory.BabyMilestones => "🍼 아기 마일스톤",
        SocialChannelCategory.PetHealing => "🐾 반려동물 힐링",
        SocialChannelCategory.EmotionalASMR => "🌧 감성 ASMR",
        _ => "☕ 데일리 감성",
    };

    // ══════════════════════ 🧠 트렌드 추출(LLM 우선 · 벤치마크 폴백) ══════════════════════

    public const string LlmRadarInstruction =
        "You are Pluto's social trend radar. Analyze top-performing YouTube Shorts and Instagram Reels creative archetypes for family/pet/lifestyle content. " +
        "Decompose each archetype into its viral mechanics: hook timing, cut pacing, loop structure, color grade and trending audio style. " +
        "Respond with JSON only: {\"insights\":[{\"title\":string,\"category\":\"BabyMilestones\"|\"PetHealing\"|\"DailyAesthetic\"|\"EmotionalASMR\"," +
        "\"mechanism\":string,\"camera\":string,\"lighting\":string,\"action\":string,\"pacing\":string,\"bgm\":\"lullaby_lofi\"|\"acoustic\"|\"musicbox\",\"hashtags\":[string]}]} - " +
        "title: short English archetype name; mechanism: concise viral mechanism (e.g. '0-2s rapid eye contact cut + 15s seamless audio loop'); " +
        "camera/lighting/action: English text-to-video prompt tags; pacing: cut rhythm in seconds (e.g. '0.8s - 1.2s rapid pacing'); " +
        "hashtags: 4-10 high-reach Korean/English platform hashtags starting with '#'. Never name real creators or channels.";

    /// <summary>
    /// 📈 플랫폼별 급상승 인사이트 — Gemini 추출 우선(3.5초 예산). LLM 미설정/예외/초과/스키마 불일치 → 내장 벤치마크(플랫폼 필터) 무중단 폴백.
    /// LLM 결과가 부족하면 벤치마크로 보충([부분 구제]). <see cref="SocialPlatform.None"/>/<see cref="SocialPlatform.Both"/> 는 양 플랫폼.
    /// </summary>
    public async Task<IReadOnlyList<SocialTrendInsight>> GetTrendingSocialInsightsAsync(SocialPlatform platform, CancellationToken ct = default)
    {
        if (platform == SocialPlatform.None) platform = SocialPlatform.Both;
        try { await EnsureLoadedAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Radar] load: " + ex.Message); }   // [부분 구제] 학습 없이 진행

        var local = BenchmarksFor(platform);
        LastUsedLlm = false;
        var text = await PlutoLlmBridge.TryCompleteAsync(_llm, LlmRadarInstruction, BuildLlmRequest(platform), 0.7, ct).ConfigureAwait(false);
        var parsed = text is null ? null : ParseLlmInsights(text, platform, MaxLlmInsights);
        if (parsed is null) return local;

        LastUsedLlm = true;
        lock (_sync)
        {
            for (var i = 0; i < parsed.Count; i++)
            {
                _known[parsed[i].Id] = parsed[i];
                _llmIds.Add(parsed[i].Id);
            }
            while (_llmIds.Count > MaxLlmCache) { _known.Remove(_llmIds[0]); _llmIds.RemoveAt(0); }
        }
        if (parsed.Count >= local.Count) return parsed;
        var merged = new List<SocialTrendInsight>(local.Count);   // [부분 구제] 부족분 벤치마크 보충
        merged.AddRange(parsed);
        for (var i = 0; merged.Count < local.Count && i < local.Count; i++) merged.Add(local[i]);
        return merged;
    }

    /// <summary>플랫폼 벤치마크(내장 순서 유지).</summary>
    public static IReadOnlyList<SocialTrendInsight> BenchmarksFor(SocialPlatform platform)
    {
        var list = new List<SocialTrendInsight>(Benchmarks.Count);
        for (var i = 0; i < Benchmarks.Count; i++)
            if ((Benchmarks[i].Platform & platform) != 0) list.Add(Benchmarks[i]);
        return list;
    }

    private string BuildLlmRequest(SocialPlatform platform)
    {
        var sb = new System.Text.StringBuilder(384);
        sb.Append("Platform: ").Append(platform switch
        {
            SocialPlatform.YouTubeShorts => "YouTube Shorts (high-retention hooks, rapid pacing, rewatch loops)",
            SocialPlatform.InstagramReels => "Instagram Reels (warm color grading, lo-fi BGM, aesthetic vignettes)",
            _ => "YouTube Shorts and Instagram Reels",
        }).Append('\n')
          .Append("Count: ").Append(MaxLlmInsights.ToString(CultureInfo.InvariantCulture)).Append('\n')
          .Append("Categories: BabyMilestones, PetHealing, DailyAesthetic, EmotionalASMR\n");
        var top = TopLearned(3);
        if (top.Count > 0)
        {
            sb.Append("Creator already learned these formulas (offer fresh variations): ");
            for (var i = 0; i < top.Count; i++) { if (i > 0) sb.Append(" | "); sb.Append(top[i].Title); }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>LLM 인사이트 JSON 스키마 즉시 검증 — 제목·메커니즘·페이싱 필수 · 제목 중복 제거 · 최대 <paramref name="max"/>건. 유효 0건이면 null.</summary>
    public IReadOnlyList<SocialTrendInsight>? ParseLlmInsights(string json, SocialPlatform platform, int max)
    {
        using var doc = PlutoLlmBridge.TryParse(json);
        if (doc is null) return null;
        var root = doc.RootElement;
        JsonElement arr;
        if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("insights", out arr) || arr.ValueKind != JsonValueKind.Array) return null;

        if (platform == SocialPlatform.None) platform = SocialPlatform.Both;
        var result = new List<SocialTrendInsight>(Math.Max(max, 1));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stamp = DateTime.UtcNow.ToString("yyMMddHHmmssfff", CultureInfo.InvariantCulture);
        foreach (var e in arr.EnumerateArray())
        {
            if (result.Count >= max) break;
            var title = PlutoLlmBridge.Str(e, "title", 60);
            var mech = PlutoLlmBridge.Str(e, "mechanism", 160);
            var pacing = PlutoLlmBridge.Str(e, "pacing", 60);
            if (title.Length == 0 || mech.Length < 6 || pacing.Length == 0 || !seen.Add(title)) continue;

            var cat = Enum.TryParse<SocialChannelCategory>(PlutoLlmBridge.Str(e, "category", 30), true, out var c) && Enum.IsDefined(c) ? c : SocialChannelCategory.DailyAesthetic;
            var camera = Or(PlutoLlmBridge.Str(e, "camera", 120), "slow push-in");
            var lighting = Or(PlutoLlmBridge.Str(e, "lighting", 120), "warm natural light");
            var action = Or(PlutoLlmBridge.Str(e, "action", 200), "subject reacting with a genuine smile");
            var bgm = ParseBgm(PlutoLlmBridge.Str(e, "bgm", 40));

            var p = platform == SocialPlatform.Both
                ? (result.Count % 2 == 0 ? SocialPlatform.YouTubeShorts : SocialPlatform.InstagramReels)
                : platform;
            var tags = new List<string>(12);
            var tagSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PlutoLlmBridge.AddTag(tags, tagSeen, p == SocialPlatform.YouTubeShorts ? "#Shorts" : "#Reels", 12);
            if (e.TryGetProperty("hashtags", out var h) && h.ValueKind == JsonValueKind.Array)
                foreach (var x in h.EnumerateArray())
                    if (x.ValueKind == JsonValueKind.String) PlutoLlmBridge.AddTag(tags, tagSeen, x.GetString(), 12);

            var serial = Interlocked.Increment(ref _serial);
            var id = IdPrefix + "llm-" + (p == SocialPlatform.YouTubeShorts ? "yt-" : "ig-") + stamp + "-" + serial.ToString(CultureInfo.InvariantCulture);   // 세션 간 Id 충돌 방지(영속 스냅샷 보호)
            result.Add(new SocialTrendInsight(id, p, cat, title, mech, new SocialPromptTags(camera, lighting, action), pacing, bgm, tags) { FromLlm = true });
        }
        return result.Count == 0 ? null : result;

        static string Or(string s, string d) => s.Length == 0 ? d : s;
    }

    private static SocialBgmStyle ParseBgm(string s)
    {
        if (s.Contains("music", StringComparison.OrdinalIgnoreCase) || s.Contains("box", StringComparison.OrdinalIgnoreCase)) return SocialBgmStyle.MusicBoxReverb;
        if (s.Contains("acoustic", StringComparison.OrdinalIgnoreCase) || s.Contains("guitar", StringComparison.OrdinalIgnoreCase)) return SocialBgmStyle.AcousticWarmth;
        return SocialBgmStyle.LullabyLoFi;
    }

    // ══════════════════════ 🧠 학습 · 영속 ══════════════════════

    /// <summary>Id → 인사이트(벤치마크 · 캐시된 LLM · 영속 스냅샷 순).</summary>
    public SocialTrendInsight? Find(string? insightId)
    {
        if (string.IsNullOrWhiteSpace(insightId)) return null;
        lock (_sync)
        {
            if (_known.TryGetValue(insightId, out var k)) return k;
            return _entries.TryGetValue(insightId, out var e) ? FromEntry(insightId, e) : null;
        }
    }

    public double GetWeight(string insightId)
    {
        lock (_sync) return _entries.TryGetValue(insightId, out var e) ? e.Weight : 0;
    }

    public int GetLearnedCount(string insightId)
    {
        lock (_sync) return _entries.TryGetValue(insightId, out var e) ? e.Learned : 0;
    }

    /// <summary>누적 학습 횟수(전 채널).</summary>
    public int TotalLearned
    {
        get
        {
            var n = 0;
            lock (_sync) foreach (var kv in _entries) n += kv.Value.Learned;
            return n;
        }
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            Dictionary<string, Entry>? data = null;
            try
            {
                if (File.Exists(_path))
                {
                    await using var fs = File.OpenRead(_path);
                    data = await JsonSerializer.DeserializeAsync<Dictionary<string, Entry>>(fs, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("[Pluto.Radar] load failed: " + ex.Message);   // 손상 파일 → 빈 지식으로 재시작
            }
            lock (_sync)
            {
                _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                if (data is not null)
                    foreach (var kv in data)
                        if (kv.Value is not null && !string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value.Weight))
                        {
                            kv.Value.Hashtags ??= new List<string>();
                            _entries[kv.Key] = kv.Value;
                        }
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 🧠 채널 공식 학습 — 친숙도 가중치 +1(상한 20) · 스냅샷 원자적 영속 · Pluto XP +2(<see cref="PlutoLearningService.AddBonusXpAsync"/>).
    /// 빈 Id → <see cref="ArgumentException"/>, 미확인 Id → <see cref="KeyNotFoundException"/>. 반환 = 갱신 후 가중치.
    /// </summary>
    public async Task<double> LearnChannelStyleAsync(string insightId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(insightId)) throw new ArgumentException("insightId required", nameof(insightId));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var insight = Find(insightId) ?? throw new KeyNotFoundException("unknown social insight: " + insightId);
        double w;
        string json;
        lock (_sync)
        {
            if (!_entries.TryGetValue(insightId, out var e)) { e = new Entry(); _entries[insightId] = e; }
            Snapshot(e, insight);
            e.Weight = Math.Min(MaxWeight, e.Weight + LearnDelta);
            e.Learned++;
            e.LastUtc = DateTime.UtcNow;
            w = e.Weight;
            json = JsonSerializer.Serialize(_entries);
        }
        await PersistAsync(json, ct).ConfigureAwait(false);
        if (_learning is not null)
        {
            try { await _learning.AddBonusXpAsync("social:" + insightId, LearnXp, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Radar] xp: " + ex.Message); }   // [부분 구제] 가중치는 유지
        }
        RaiseChanged();
        return w;
    }

    /// <summary>
    /// 📈 생성 엔진/코파일럿 주입용 상위 바이럴 패턴 — 학습 가중치 내림차순(동일 테마 우선) · 미학습 시 해당 테마 벤치마크 1건.
    /// </summary>
    public IReadOnlyList<SocialTrendInsight> TopViralPatterns(StudioThemeCategory theme, int max = 2)
    {
        if (max <= 0) return Array.Empty<SocialTrendInsight>();
        var learned = TopLearned(16);
        var result = new List<SocialTrendInsight>(max);
        for (var pass = 0; pass < 2 && result.Count < max; pass++)
            for (var i = 0; i < learned.Count && result.Count < max; i++)
            {
                var match = learned[i].ThemeCategory == theme;
                if ((pass == 0) == match) result.Add(learned[i]);
            }
        if (result.Count > 0) return result;
        for (var i = 0; i < Benchmarks.Count; i++)
            if (Benchmarks[i].ThemeCategory == theme) { result.Add(Benchmarks[i]); break; }
        return result;
    }

    /// <summary>학습된(가중치 &gt; 0) 패턴 상위 N — 가중치 내림차순 · 최근 학습 우선.</summary>
    public IReadOnlyList<SocialTrendInsight> TopLearned(int max)
    {
        var keys = new List<(string Id, double W, DateTime T)>();
        lock (_sync)
            foreach (var kv in _entries)
                if (kv.Value.Weight > 0) keys.Add((kv.Key, kv.Value.Weight, kv.Value.LastUtc));
        keys.Sort(static (a, b) => { var c = b.W.CompareTo(a.W); return c != 0 ? c : b.T.CompareTo(a.T); });
        var result = new List<SocialTrendInsight>(Math.Min(max, keys.Count));
        for (var i = 0; i < keys.Count && result.Count < max; i++)
            if (Find(keys[i].Id) is { } s) result.Add(s);
        return result;
    }

    /// <summary>프롬프트 주입 문구 — "pacing (mechanism)".</summary>
    public static string PatternDirective(SocialTrendInsight s) => s.PacingGuideline + " (" + s.ViralMechanism + ")";

    private static void Snapshot(Entry e, SocialTrendInsight s)
    {
        e.Platform = s.Platform;
        e.Category = s.ChannelCategory;
        e.Title = s.Title;
        e.ViralMechanism = s.ViralMechanism;
        e.PacingGuideline = s.PacingGuideline;
        e.Camera = s.RecommendedPromptTags.Camera;
        e.Lighting = s.RecommendedPromptTags.Lighting;
        e.SubjectAction = s.RecommendedPromptTags.SubjectAction;
        e.Bgm = s.TrendingBgmStyle;
        e.Hashtags = new List<string>(s.ViralHashtagSet);
    }

    private static SocialTrendInsight? FromEntry(string id, Entry e)
    {
        if (e.Title.Length == 0 || e.ViralMechanism.Length == 0) return null;
        return new SocialTrendInsight(id, e.Platform, e.Category, e.Title, e.ViralMechanism,
            new SocialPromptTags(e.Camera, e.Lighting, e.SubjectAction), e.PacingGuideline, e.Bgm, e.Hashtags.ToArray())
        { FromLlm = id.StartsWith(IdPrefix + "llm-", StringComparison.Ordinal) };
    }

    private async Task PersistAsync(string json, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Radar] persist failed: " + ex.Message);   // [부분 구제] 메모리 내 가중치 유지
        }
        finally { _gate.Release(); }
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Radar] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
