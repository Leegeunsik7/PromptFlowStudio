using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// v16.22.0 캐릭터 프로필 — 얼굴(사진·영역·특징 벡터·dHash) · 음성(톤·Edge 보이스) · 의상/소품 앵커 · 고정 스타일/시드 · 정체성 어댑터.
/// </summary>
public sealed class CharacterProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "우리 아기";
    public string? FaceImagePath { get; set; }
    public FaceRegion Region { get; set; } = FaceRegion.Default;
    public float[]? Features { get; set; }
    public ulong FaceHash { get; set; }
    public NarrationTone VoiceTone { get; set; } = NarrationTone.FairyTale;
    public string? EdgeVoice { get; set; }
    public List<StyleAnchor> Anchors { get; set; } = new();
    public string StyleTokens { get; set; } = "";
    public int Seed { get; set; }
    public IdentityAdapter Adapter { get; set; } = IdentityAdapter.PuLid;
    public double AdapterWeight { get; set; } = 0.85;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public bool HasFace => Features is { Length: CharacterIdentityLockService.Dimensions } && !string.IsNullOrWhiteSpace(FaceImagePath);
}

/// <summary>v16.22.0 프로젝트에 고정된 캐릭터 스냅숏(직렬화 · 프로필 삭제 후에도 재현).</summary>
public sealed class CharacterLockSnapshot
{
    public string ProfileId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? FaceImagePath { get; set; }
    public List<StyleAnchor> Anchors { get; set; } = new();
    public string StyleTokens { get; set; } = "";
    public int Seed { get; set; }
    public NarrationTone VoiceTone { get; set; } = NarrationTone.FairyTale;
    public IdentityAdapter Adapter { get; set; } = IdentityAdapter.PuLid;
    public double AdapterWeight { get; set; } = 0.85;
    public string? SeriesId { get; set; }
    public string? EpisodeId { get; set; }
}

/// <summary>v16.22.0 에피소드.</summary>
public sealed class SeriesEpisode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public string? ProjectId { get; set; }
    public string? ProjectPath { get; set; }
    public double? AvgConfidence { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>v16.22.0 시즌.</summary>
public sealed class SeriesSeason
{
    public int Number { get; set; } = 1;
    public string Title { get; set; } = "";
    public List<SeriesEpisode> Episodes { get; set; } = new();
}

/// <summary>v16.22.0 연작 시리즈 — 캐릭터 프로필 · 시리즈 고정 스타일/시드 · 시즌/에피소드.</summary>
public sealed class StorySeries
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];
    public string Title { get; set; } = "새 시리즈";
    public string ProfileId { get; set; } = "";
    public string StyleTokens { get; set; } = "";
    public int Seed { get; set; }
    public List<SeriesSeason> Seasons { get; set; } = new();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public int EpisodeCount { get { var n = 0; foreach (var s in Seasons) n += s.Episodes.Count; return n; } }
}

/// <summary>v16.22.0 컷 스타일 드리프트 진단.</summary>
public readonly record struct SceneDrift(int Index, int MissingTokens, int Conflicts, double Score);

/// <summary>v16.22.0 프로젝트 드리프트 보고 — 컷별 · 평균 · 억제(수정) 컷 수.</summary>
public sealed record DriftReport(IReadOnlyList<SceneDrift> Scenes, double AverageScore, int Corrected);

/// <summary>
/// [PromptFlowStudio] v16.22.0 📚 멀티 에피소드 연속 시리즈 빌더 — 캐릭터 프로필/시리즈 영속 · 에피소드 생성 · 프로필 자동 전파 · 스타일 드리프트 억제.
///   ▸ 저장: %LocalAppData%\PromptFlowStudio\Characters\{profiles,series}.json(원자적 tmp→교체 · 상한 프로필 50 · 시리즈 100 · 시리즈당 에피소드 200).
///   ▸ 전파(<see cref="Propagate"/>): 프로젝트 앵커 = 얼굴 사진(전 컷 I2V) · <see cref="CharacterLockSnapshot"/>(의상/소품·스타일·시드·음성) · 아기 이름.
///     실제 프롬프트 주입은 제출 시점 비영속(<see cref="ComposeScenePrompt"/> · JobManager) — 컷 원문 보존.
///   ▸ 드리프트: 렌더 스타일 계열(실사 vs 애니/3D/수채 등) 충돌 단어 · 시리즈 스타일 토큰 누락 → 컷 점수(0=일관) · 억제 시 충돌 제거 + 토큰 보강.
///   ▸ 변경은 lock 보호 + OnChange(예외 격리). 파일 실패는 [부분 구제](메모리 상태 유지).
/// </summary>
public sealed class EpisodicStorySeriesBuilder
{
    public const int MaxProfiles = 50, MaxSeries = 100, MaxEpisodes = 200, MaxScenesPerEpisode = 12;

    private static readonly string[] PhotoFamily = { "photorealistic", "realistic photo", "cinematic photo", "live action", "dslr" };
    private static readonly string[] StylizedFamily = { "anime", "cartoon", "3d render", "pixar", "clay", "claymation", "watercolor", "illustration", "cel shaded", "comic" };

    private readonly CharacterIdentityLockService _identity;
    private readonly Func<StoryboardProject, string?> _save;
    private readonly string _root;
    private readonly object _gate = new();
    private readonly List<CharacterProfile> _profiles = new();
    private readonly List<StorySeries> _series = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public EpisodicStorySeriesBuilder(CharacterIdentityLockService identity, StoryboardRepository repo)
        : this(identity, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Characters"), repo.Save) { }

    public EpisodicStorySeriesBuilder(CharacterIdentityLockService identity, string root, Func<StoryboardProject, string?> save)
    {
        _identity = identity;
        _root = root;
        _save = save;
        Load();
    }

    public event Action? OnChange;

    public CharacterIdentityLockService Identity => _identity;
    public IReadOnlyList<CharacterProfile> Profiles { get { lock (_gate) return _profiles.ToArray(); } }
    public IReadOnlyList<StorySeries> Series { get { lock (_gate) return _series.ToArray(); } }
    public string? LastError { get; private set; }

    public CharacterProfile? FindProfile(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) foreach (var p in _profiles) if (p.Id == id) return p;
        return null;
    }

    public StorySeries? FindSeries(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate) foreach (var s in _series) if (s.Id == id) return s;
        return null;
    }

    // ── 프로필 ────────────────────────────────────────────────────────────────

    /// <summary>사진 → 프로필(특징 추출 실패해도 사진/앵커는 저장 — 신뢰도만 미측정).</summary>
    public async Task<(CharacterProfile? Profile, string Message)> CreateProfileAsync(string name, string faceImagePath, FaceRegion? region = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(faceImagePath) || !File.Exists(faceImagePath)) return (null, "얼굴 사진이 없습니다.");
        lock (_gate) if (_profiles.Count >= MaxProfiles) return (null, "프로필 상한(" + MaxProfiles.ToString(CultureInfo.InvariantCulture) + ") 초과");
        var p = new CharacterProfile
        {
            Name = string.IsNullOrWhiteSpace(name) ? "우리 아기" : name.Trim(),
            FaceImagePath = faceImagePath,
            Region = (region ?? FaceRegion.Default).Clamp(),
            Seed = Random.Shared.Next(1, int.MaxValue),
        };
        var ex = await _identity.ExtractAsync(faceImagePath, p.Region, ct).ConfigureAwait(false);
        string msg;
        if (ex is { } e) { p.Features = e.Features; p.FaceHash = e.Hash; msg = "얼굴 특징 추출 완료"; }
        else msg = "특징 추출 실패(ffmpeg 확인) — 사진 앵커만 사용";
        Upsert(p);
        return (p, msg);
    }

    /// <summary>얼굴 영역 변경 후 특징 재추출.</summary>
    public async Task<bool> RefreshFeaturesAsync(CharacterProfile p, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(p.FaceImagePath)) return false;
        var ex = await _identity.ExtractAsync(p.FaceImagePath, p.Region, ct).ConfigureAwait(false);
        if (ex is not { } e) return false;
        p.Features = e.Features;
        p.FaceHash = e.Hash;
        Upsert(p);
        return true;
    }

    public void Upsert(CharacterProfile p)
    {
        lock (_gate)
        {
            var i = _profiles.FindIndex(x => x.Id == p.Id);
            if (p.Anchors.Count > PropOutfitTransferEngine.MaxAnchors) p.Anchors.RemoveRange(PropOutfitTransferEngine.MaxAnchors, p.Anchors.Count - PropOutfitTransferEngine.MaxAnchors);
            if (i >= 0) _profiles[i] = p;
            else if (_profiles.Count < MaxProfiles) _profiles.Add(p);
        }
        Persist();
    }

    public bool RemoveProfile(string id)
    {
        bool removed;
        lock (_gate) removed = _profiles.RemoveAll(p => p.Id == id) > 0;
        if (removed) Persist();
        return removed;
    }

    // ── 시리즈/에피소드 ────────────────────────────────────────────────────────

    public StorySeries? CreateSeries(string title, string profileId, string styleTokens = "")
    {
        var profile = FindProfile(profileId);
        if (profile is null) return null;
        var s = new StorySeries
        {
            Title = string.IsNullOrWhiteSpace(title) ? profile.Name + " 시리즈" : title.Trim(),
            ProfileId = profileId,
            StyleTokens = string.IsNullOrWhiteSpace(styleTokens) ? profile.StyleTokens : styleTokens.Trim(),
            Seed = profile.Seed,
            Seasons = { new SeriesSeason { Number = 1, Title = "시즌 1" } },
        };
        lock (_gate)
        {
            if (_series.Count >= MaxSeries) return null;
            _series.Add(s);
        }
        Persist();
        return s;
    }

    public bool RemoveSeries(string id)
    {
        bool removed;
        lock (_gate) removed = _series.RemoveAll(s => s.Id == id) > 0;
        if (removed) Persist();
        return removed;
    }

    /// <summary>
    /// 에피소드 생성 — 컷 대본(줄당 1컷 · 상한 12) → 스토리보드 프로젝트 + 프로필 전파 + 저장. 반환: 에피소드 · 프로젝트(저장 실패 시 경로 null).
    /// </summary>
    public (SeriesEpisode? Episode, StoryboardProject? Project, string Message) AddEpisode(string seriesId, int seasonNumber, string title, string synopsis, IReadOnlyList<string> sceneLines)
    {
        var series = FindSeries(seriesId);
        if (series is null) return (null, null, "시리즈를 찾을 수 없습니다.");
        var profile = FindProfile(series.ProfileId);
        if (profile is null) return (null, null, "시리즈 캐릭터 프로필이 삭제되었습니다.");
        if (series.EpisodeCount >= MaxEpisodes) return (null, null, "에피소드 상한 초과");
        SeriesSeason season;
        lock (_gate)
        {
            season = series.Seasons.Find(s => s.Number == seasonNumber) ?? AddSeason(series, seasonNumber);
        }
        var ep = new SeriesEpisode
        {
            Number = season.Episodes.Count + 1,
            Title = string.IsNullOrWhiteSpace(title) ? "에피소드 " + (season.Episodes.Count + 1).ToString(CultureInfo.InvariantCulture) : title.Trim(),
            Synopsis = (synopsis ?? "").Trim(),
        };
        var project = new StoryboardProject
        {
            Title = series.Title + " S" + season.Number.ToString("00", CultureInfo.InvariantCulture) + "E" + ep.Number.ToString("00", CultureInfo.InvariantCulture) + " · " + ep.Title,
        };
        var count = 0;
        foreach (var line in sceneLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (++count > MaxScenesPerEpisode) break;
            project.Scenes.Add(new StoryboardScene { Prompt = line.Trim(), VoiceText = "" });
        }
        if (project.Scenes.Count == 0 && ep.Synopsis.Length > 0) project.Scenes.Add(new StoryboardScene { Prompt = ep.Synopsis });
        if (project.Scenes.Count == 0) return (null, null, "컷 대본이 비었습니다.");
        Propagate(project, profile, series, ep.Id);
        ep.ProjectId = project.Id;
        ep.ProjectPath = _save(project);
        lock (_gate) season.Episodes.Add(ep);
        Persist();
        return (ep, project, ep.ProjectPath is null ? "에피소드 생성(프로젝트 저장 실패 — 메모리에만 존재)" : "에피소드 생성 · 저장됨");
    }

    private static SeriesSeason AddSeason(StorySeries s, int n)
    {
        var season = new SeriesSeason { Number = Math.Max(1, n), Title = "시즌 " + Math.Max(1, n).ToString(CultureInfo.InvariantCulture) };
        s.Seasons.Add(season);
        s.Seasons.Sort(static (a, b) => a.Number.CompareTo(b.Number));
        return season;
    }

    public void RecordEpisodeConfidence(string episodeId, double avg)
    {
        lock (_gate)
            foreach (var s in _series)
                foreach (var se in s.Seasons)
                    foreach (var e in se.Episodes)
                        if (e.Id == episodeId) e.AvgConfidence = Math.Round(avg, 1);
        Persist();
    }

    // ── 전파 · 드리프트 (순수 정적) ─────────────────────────────────────────────

    /// <summary>프로필(+시리즈) → 프로젝트 전파: 앵커 이미지 · 잠금 스냅숏 · 아기 이름.</summary>
    public static void Propagate(StoryboardProject project, CharacterProfile profile, StorySeries? series = null, string? episodeId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(profile);
        if (!string.IsNullOrWhiteSpace(profile.FaceImagePath) && File.Exists(profile.FaceImagePath)) project.AnchorImagePath = profile.FaceImagePath;
        project.CharacterLock = new CharacterLockSnapshot
        {
            ProfileId = profile.Id,
            Name = profile.Name,
            FaceImagePath = profile.FaceImagePath,
            Anchors = new List<StyleAnchor>(profile.Anchors),
            StyleTokens = !string.IsNullOrWhiteSpace(series?.StyleTokens) ? series!.StyleTokens : profile.StyleTokens,
            Seed = series?.Seed > 0 ? series.Seed : profile.Seed,
            VoiceTone = profile.VoiceTone,
            Adapter = profile.Adapter,
            AdapterWeight = profile.AdapterWeight,
            SeriesId = series?.Id,
            EpisodeId = episodeId,
        };
        if (string.IsNullOrWhiteSpace(project.BabyName)) project.BabyName = profile.Name;
    }

    /// <summary>
    /// 제출용 컷 프롬프트 합성(비영속 · 멱등 · 순수) — 드리프트 억제 → 정체성 구문(얼굴 있을 때) → 의상/소품.
    /// </summary>
    public static (string Prompt, string Negative) ComposeScenePrompt(string? prompt, string? negative, CharacterLockSnapshot? lk)
    {
        var p = (prompt ?? "").Trim();
        var n = negative;
        if (lk is null) return (p, (n ?? "").Trim());
        p = SuppressPrompt(p, lk.StyleTokens, out _);
        if (!string.IsNullOrWhiteSpace(lk.FaceImagePath)) (p, n) = CharacterIdentityLockService.ApplyPrompt(p, n);
        var o = PropOutfitTransferEngine.Apply(p, n, lk.Anchors);
        return (o.Prompt, o.Negative);
    }

    /// <summary>스타일 토큰 파싱(쉼표 구분 · 공백 정리 · 상한 12).</summary>
    public static List<string> ParseTokens(string? tokens)
    {
        var list = new List<string>(8);
        if (string.IsNullOrWhiteSpace(tokens)) return list;
        foreach (var raw in tokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0 || list.Count >= 12) continue;
            var dup = false;
            foreach (var x in list) if (x.Equals(raw, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
            if (!dup) list.Add(raw);
        }
        return list;
    }

    /// <summary>시리즈 스타일이 속한 렌더 계열(−1 미정 · 0 실사 · 1 스타일화).</summary>
    public static int FamilyOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return -1;
        foreach (var w in PhotoFamily) if (ContainsWord(text, w)) return 0;
        foreach (var w in StylizedFamily) if (ContainsWord(text, w)) return 1;
        return -1;
    }

    /// <summary>컷 1개 진단(순수) — 누락 토큰 수 · 계열 충돌 단어 수 · 점수(0~1).</summary>
    public static SceneDrift Diagnose(int index, string? prompt, string? styleTokens)
    {
        var p = prompt ?? "";
        var tokens = ParseTokens(styleTokens);
        var missing = 0;
        foreach (var t in tokens) if (!p.Contains(t, StringComparison.OrdinalIgnoreCase)) missing++;
        var conflicts = CountConflicts(p, FamilyOf(styleTokens));
        var score = tokens.Count == 0 && conflicts == 0 ? 0 : Math.Min(1, (tokens.Count == 0 ? 0 : (double)missing / tokens.Count) * 0.5 + conflicts * 0.25);
        return new SceneDrift(index, missing, conflicts, Math.Round(score, 3));
    }

    private static int CountConflicts(string p, int family)
    {
        if (family < 0) return 0;
        var n = 0;
        foreach (var w in family == 0 ? StylizedFamily : PhotoFamily) if (ContainsWord(p, w)) n++;
        return n;
    }

    /// <summary>프롬프트 드리프트 억제(순수) — 반대 계열 단어 제거 + 누락 스타일 토큰 보강.</summary>
    public static string SuppressPrompt(string prompt, string? styleTokens, out bool changed)
    {
        var p = prompt ?? "";
        var family = FamilyOf(styleTokens);
        var original = p;
        if (family >= 0)
            foreach (var w in family == 0 ? StylizedFamily : PhotoFamily)
                p = RemoveWord(p, w);
        var tokens = ParseTokens(styleTokens);
        var sb = new StringBuilder(p.Length + 64).Append(Tidy(p));
        foreach (var t in tokens)
        {
            if (sb.ToString().Contains(t, StringComparison.OrdinalIgnoreCase)) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(t);
        }
        var result = sb.ToString();
        changed = !string.Equals(result, original.Trim(), StringComparison.Ordinal);
        return result;
    }

    /// <summary>프로젝트 드리프트 진단/억제(apply=true 면 컷 프롬프트 영속 수정).</summary>
    public static DriftReport AnalyzeDrift(StoryboardProject project, string? styleTokens, bool apply = false)
    {
        var list = new List<SceneDrift>(project.Scenes.Count);
        var corrected = 0;
        double sum = 0;
        for (var i = 0; i < project.Scenes.Count; i++)
        {
            var s = project.Scenes[i];
            var d = Diagnose(i, s.Prompt, styleTokens);
            if (apply && d.Score > 0)
            {
                s.Prompt = SuppressPrompt(s.Prompt, styleTokens, out var ch);
                if (ch) corrected++;
                d = Diagnose(i, s.Prompt, styleTokens);
            }
            list.Add(d);
            sum += d.Score;
        }
        return new DriftReport(list, list.Count == 0 ? 0 : Math.Round(sum / list.Count, 3), corrected);
    }

    private static bool ContainsWord(string text, string word)
    {
        var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var okL = i == 0 || !char.IsLetter(text[i - 1]);
            var end = i + word.Length;
            var okR = end >= text.Length || !char.IsLetter(text[end]);
            if (okL && okR) return true;
            i = text.IndexOf(word, end, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string RemoveWord(string text, string word)
    {
        var i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (i >= 0)
        {
            var end = i + word.Length;
            var okL = i == 0 || !char.IsLetter(text[i - 1]);
            var okR = end >= text.Length || !char.IsLetter(text[end]);
            if (okL && okR) { text = text.Remove(i, word.Length); i = text.IndexOf(word, i, StringComparison.OrdinalIgnoreCase); }
            else i = text.IndexOf(word, end, StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }

    private static string Tidy(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var part in s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var t = string.Join(' ', part.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(t);
        }
        return sb.ToString();
    }

    // ── 영속 ─────────────────────────────────────────────────────────────────

    private sealed record Store(int Schema, List<CharacterProfile> Profiles, List<StorySeries> Series);

    private string StorePath => Path.Combine(_root, "characters.json");

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var s = JsonSerializer.Deserialize<Store>(File.ReadAllText(StorePath), Json);
            if (s is null || s.Schema != 1) return;
            lock (_gate)
            {
                _profiles.Clear();
                if (s.Profiles is not null)
                    foreach (var p in s.Profiles)
                        if (_profiles.Count < MaxProfiles && p.Features is null or { Length: CharacterIdentityLockService.Dimensions }) _profiles.Add(p);
                _series.Clear();
                if (s.Series is not null)
                    foreach (var x in s.Series) if (_series.Count < MaxSeries) _series.Add(x);
            }
        }
        catch (Exception ex) { Program.Log("SeriesBuilder.Load", ex); LastError = "캐릭터 저장소 읽기 실패: " + ex.Message; }
    }

    private void Persist()
    {
        try
        {
            Store snap;
            lock (_gate) snap = new Store(1, new List<CharacterProfile>(_profiles), new List<StorySeries>(_series));
            Directory.CreateDirectory(_root);
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(snap, Json));
            File.Move(tmp, StorePath, overwrite: true);
            LastError = null;
        }
        catch (Exception ex) { Program.Log("SeriesBuilder.Persist", ex); LastError = "저장 실패(메모리 상태 유지): " + ex.Message; }
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("SeriesBuilder.OnChange", ex); }
    }
}
