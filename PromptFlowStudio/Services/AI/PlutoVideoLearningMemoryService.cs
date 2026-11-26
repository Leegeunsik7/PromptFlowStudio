using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.AI;

/// <summary>👎 어색함 피드백 사유 태그.</summary>
public enum PlutoFeedbackReason
{
    /// <summary>인체/동작 왜곡.</summary>
    BodyMotionDistortion = 0,
    /// <summary>구도/시점 튐.</summary>
    FramingJump = 1,
    /// <summary>조명/가독성 문제.</summary>
    LightingReadability = 2,
    /// <summary>서사 중복.</summary>
    NarrativeRepetition = 3,
}

/// <summary>🧠 학습 규칙 1건(직렬화 DTO · System.Text.Json).</summary>
public sealed class PlutoLearnedRule
{
    public string Id { get; set; } = "";
    /// <summary>seed · recipe(👍 성공 레시피) · avoid(👎 실패 회피).</summary>
    public string Kind { get; set; } = "seed";
    /// <summary>유사 프롬프트 매칭 키워드(비어 있으면 범용 규칙).</summary>
    public List<string> Keywords { get; set; } = new();
    /// <summary>프롬프트에 덧붙일 앵커 구문(없으면 null).</summary>
    public string? PromptAnchor { get; set; }
    /// <summary>네거티브에 병합할 용어.</summary>
    public List<string> NegativeTerms { get; set; } = new();
    /// <summary>👎 사유 라벨(avoid 전용).</summary>
    public string? Reason { get; set; }
    public bool AmbientOnly { get; set; }
    public double Weight { get; set; } = 1.0;
    public int Hits { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>디스크 문서(스키마 버전 포함).</summary>
public sealed class PlutoDirectorMemoryDocument
{
    public int SchemaVersion { get; set; } = PlutoVideoLearningMemoryService.SchemaVersion;
    public List<PlutoLearnedRule> Rules { get; set; } = new();
    /// <summary>📥 갤러리 흡수 완료 프롬프트 해시(정규화 SHA-256 앞 16자) — 재실행 멱등.</summary>
    public List<string> ImportedPromptHashes { get; set; } = new();
}

/// <summary>📥 기존 갤러리 학습 흡수 결과.</summary>
public sealed record PlutoGalleryBootstrapResult(int Scanned, int Imported, int Duplicates, int Skipped)
{
    public string SummaryKo => "📥 갤러리 " + Scanned.ToString(CultureInfo.InvariantCulture) + "건 스캔 · 신규 흡수 " + Imported.ToString(CultureInfo.InvariantCulture)
        + "건 · 중복 " + Duplicates.ToString(CultureInfo.InvariantCulture) + "건 · 제외 " + Skipped.ToString(CultureInfo.InvariantCulture) + "건";
}

/// <summary>프롬프트 1건에 대한 학습 가이던스 — 앵커 · 네거티브 · 적용된 학습(비 seed) 규칙 수.</summary>
public sealed record PlutoLearnedGuidance(
    IReadOnlyList<string> PromptAnchors,
    IReadOnlyList<string> NegativeTerms,
    IReadOnlyList<string> RuleIds,
    int LearnedCount)
{
    public static readonly PlutoLearnedGuidance Empty = new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 0);
    public bool IsEmpty => PromptAnchors.Count == 0 && NegativeTerms.Count == 0;
}

/// <summary>
/// 🧠 플루토 자기학습 비디오 디렉터 메모리 — 👍 성공 레시피 · 👎 실패 회피 규칙을
/// <c>%LocalAppData%\PromptFlowStudio\pluto_director_memory.json</c> 에 영속(테스트는 주입 경로).
/// 순수 BCL · 스레드 안전(lock) · IO 실패는 메모리 내 유지(절대 throw 하지 않음).
/// </summary>
public sealed class PlutoVideoLearningMemoryService
{
    public const int SchemaVersion = 1;
    public const string FileName = "pluto_director_memory.json";
    public const int MaxRules = 200;
    public const int MaxRecipesPerGuidance = 3;
    public const int MaxAvoidPerGuidance = 4;

    private static readonly string[] StopWords =
    {
        "the", "and", "with", "from", "into", "onto", "over", "under", "for", "that", "this", "then", "while", "its", "his", "her", "their", "are", "was",
        "shot", "camera", "video", "scene", "clip", "very", "slowly", "slow", "some", "has", "have", "who", "out", "off", "through", "across", "near",
        "natural", "consistent", "stable", "lighting", "motion", "physics", "throughout", "after", "only", "each", "exactly", "once", "single",
    };

    private readonly object _gate = new();
    private readonly PlutoDirectorMemoryDocument _doc;

    /// <summary>사용자 라이브 경로(%LocalAppData%\PromptFlowStudio\pluto_director_memory.json).</summary>
    public PlutoVideoLearningMemoryService() : this(DefaultPath()) { }

    /// <summary>주입 경로(xUnit 격리용).</summary>
    public PlutoVideoLearningMemoryService(string path)
    {
        FilePath = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
        _doc = Load(FilePath) ?? new PlutoDirectorMemoryDocument();
        if (EnsureSeeds(_doc)) Save();
    }

    public string FilePath { get; }

    /// <summary>학습 변경 알림(UI 갱신용 · 구독자 예외 격리).</summary>
    public event Action? Changed;

    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
        return Path.Combine(root, "PromptFlowStudio", FileName);
    }

    /// <summary>👎 사유 한국어 라벨.</summary>
    public static string ReasonLabel(PlutoFeedbackReason r) => r switch
    {
        PlutoFeedbackReason.BodyMotionDistortion => "인체/동작 왜곡",
        PlutoFeedbackReason.FramingJump => "구도/시점 튐",
        PlutoFeedbackReason.LightingReadability => "조명/가독성 문제",
        _ => "서사 중복",
    };

    public static readonly PlutoFeedbackReason[] AllReasons =
    {
        PlutoFeedbackReason.BodyMotionDistortion, PlutoFeedbackReason.FramingJump, PlutoFeedbackReason.LightingReadability, PlutoFeedbackReason.NarrativeRepetition,
    };

    /// <summary>라벨/태그 문자열 → 사유(미인식 null).</summary>
    public static PlutoFeedbackReason? ParseReason(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var t = tag.Trim();
        foreach (var r in AllReasons)
            if (t.Contains(ReasonLabel(r), StringComparison.Ordinal) || string.Equals(t, r.ToString(), StringComparison.OrdinalIgnoreCase)) return r;
        if (t.Contains("인체", StringComparison.Ordinal) || t.Contains("동작", StringComparison.Ordinal)) return PlutoFeedbackReason.BodyMotionDistortion;
        if (t.Contains("구도", StringComparison.Ordinal) || t.Contains("시점", StringComparison.Ordinal)) return PlutoFeedbackReason.FramingJump;
        if (t.Contains("조명", StringComparison.Ordinal) || t.Contains("가독", StringComparison.Ordinal)) return PlutoFeedbackReason.LightingReadability;
        if (t.Contains("서사", StringComparison.Ordinal) || t.Contains("중복", StringComparison.Ordinal)) return PlutoFeedbackReason.NarrativeRepetition;
        return null;
    }

    /// <summary>사유별 회피 앵커 + 네거티브.</summary>
    public static (string Anchor, string[] Negative) AvoidanceFor(PlutoFeedbackReason r) => r switch
    {
        PlutoFeedbackReason.BodyMotionDistortion => ("anatomically correct body proportions with natural joint motion, hands and limbs stay intact",
            new[] { "deformed body", "twisted limbs", "fused fingers", "unnatural joint bending" }),
        PlutoFeedbackReason.FramingJump => ("one continuous steady camera viewpoint without sudden framing jumps",
            new[] { "jump cut", "abrupt camera shift", "shaky camera", "sudden zoom" }),
        PlutoFeedbackReason.LightingReadability => ("clear even exposure with a readable subject silhouette against the background",
            new[] { "underexposed", "overexposed", "flickering light", "murky shadows" }),
        _ => ("each story beat happens exactly once in forward order without repeated actions",
            new[] { "repeated action", "looping motion", "duplicate scene" }),
    };

    /// <summary>전체 규칙 스냅샷(복사본).</summary>
    public IReadOnlyList<PlutoLearnedRule> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<PlutoLearnedRule>(_doc.Rules.Count);
            foreach (var r in _doc.Rules) list.Add(Clone(r));
            return list;
        }
    }

    /// <summary>학습(비 seed) 규칙 수.</summary>
    public int LearnedRuleCount
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var r in _doc.Rules) if (r.Kind != "seed") n++;
                return n;
            }
        }
    }

    /// <summary>
    /// 원문 프롬프트 → 관련 학습 가이던스. 범용 seed(앰비언트 여부 필터) + 키워드가 겹치는 성공 레시피(상위 3) · 실패 회피(상위 4), 가중치 내림차순.
    /// </summary>
    public PlutoLearnedGuidance GetLearnedGuidanceForPrompt(string? rawPrompt, bool isAmbientLoop = false)
    {
        var tokens = Tokenize(rawPrompt);
        var anchors = new List<string>(8);
        var negs = new List<string>(16);
        var ids = new List<string>(8);
        var learned = 0;
        lock (_gate)
        {
            foreach (var r in _doc.Rules)
            {
                if (r.Kind != "seed" || (r.AmbientOnly && !isAmbientLoop)) continue;
                Add(r, anchors, negs, ids);
            }
            learned += AddTop("recipe", tokens, MaxRecipesPerGuidance, anchors, negs, ids);
            learned += AddTop("avoid", tokens, MaxAvoidPerGuidance, anchors, negs, ids);
        }
        return new PlutoLearnedGuidance(anchors, negs, ids, learned);
    }

    /// <summary>
    /// 👍 잘 나옴 / 보관 · 내보내기 — 유사 프롬프트의 성공 레시피(보정본에서 원문에 없던 스타일 절)를 기록·강화하고, 함께 쓰인 회피 규칙 가중치도 올린다.
    /// 반환 = 강화된 레시피 규칙 Id(키워드 없음 → null).
    /// </summary>
    public string? RecordPositiveOutcome(string? rawPrompt, string? refinedPrompt = null)
    {
        var tokens = Tokenize(rawPrompt);
        if (tokens.Count == 0) return null;
        string id;
        lock (_gate)
        {
            foreach (var r in _doc.Rules)
                if (r.Kind == "avoid" && Overlap(r.Keywords, tokens) > 0) { r.Weight += 0.5; r.Hits++; r.UpdatedUtc = DateTime.UtcNow; }
            var recipe = BestMatch("recipe", null, tokens, 2);
            var hint = ExtractRecipeHint(rawPrompt, refinedPrompt);
            if (recipe is null)
            {
                recipe = new PlutoLearnedRule { Id = NewId("r"), Kind = "recipe", Keywords = tokens, PromptAnchor = hint, Weight = 1.0 };
                _doc.Rules.Add(recipe);
            }
            else
            {
                MergeKeywords(recipe.Keywords, tokens);
                recipe.Weight += 1.0;
                if (hint is not null) recipe.PromptAnchor = hint;
            }
            recipe.Hits++;
            recipe.UpdatedUtc = DateTime.UtcNow;
            id = recipe.Id;
            Prune();
        }
        Save();
        RaiseChanged();
        return id;
    }

    /// <summary>
    /// 👎 어색함 + 사유 — 유사 프롬프트용 내구성 회피 규칙을 추가/강화하고, 즉시 강화된 보정 프롬프트(로컬 결정적)를 돌려준다.
    /// </summary>
    public PromptPreflightResult RecordFailureAndGetRefinement(string? rawPrompt, PlutoFeedbackReason reason, int targetDurationSeconds = 5,
        string? aspectRatio = "16:9", bool isAmbientLoop = false, string? existingNegative = null)
    {
        var tokens = Tokenize(rawPrompt);
        var label = ReasonLabel(reason);
        var (anchor, neg) = AvoidanceFor(reason);
        lock (_gate)
        {
            var rule = BestMatch("avoid", label, tokens, 1);
            if (rule is null)
            {
                rule = new PlutoLearnedRule { Id = NewId("a"), Kind = "avoid", Reason = label, Keywords = tokens, PromptAnchor = anchor, NegativeTerms = new List<string>(neg), Weight = 1.0 };
                _doc.Rules.Add(rule);
            }
            else
            {
                MergeKeywords(rule.Keywords, tokens);
                rule.Weight += 1.0;
            }
            rule.Hits++;
            rule.UpdatedUtc = DateTime.UtcNow;
            // 같은 유사 프롬프트의 성공 레시피는 신뢰도 하향(0 미만 금지).
            foreach (var r in _doc.Rules)
                if (r.Kind == "recipe" && Overlap(r.Keywords, tokens) >= 2) r.Weight = Math.Max(0.1, r.Weight - 0.5);
            Prune();
        }
        Save();
        RaiseChanged();
        return PromptPreflightDirector.RefineLocal(rawPrompt, targetDurationSeconds, aspectRatio, isAmbientLoop, existingNegative,
            GetLearnedGuidanceForPrompt(rawPrompt, isAmbientLoop));
    }

    /// <summary>🩺 자동 QC 회피 규칙 사유 라벨.</summary>
    public const string QcReasonLabel = "자동 QC 결함";
    /// <summary>🩺 자동 QC 회피 앵커(정지 · 검정 · 손상 방지).</summary>
    public const string QcAvoidAnchor = "continuous visible subject motion from the first to the last frame with steady well-exposed lighting";
    public static readonly string[] QcAvoidNegative = { "frozen frame", "static still image", "black frames", "corrupted frames" };

    /// <summary>
    /// 🩺 렌더 후 QC 가 정지/검정/손상을 감지 — 유사 프롬프트용 진단 회피 규칙을 추가/강화(사유 = <see cref="QcReasonLabel"/> · 진단 요약 보존). 반환 = 규칙 Id.
    /// </summary>
    public string RecordRenderQualityDefect(string? rawPrompt, string? diagnosis)
    {
        var tokens = Tokenize(rawPrompt);
        string id;
        lock (_gate)
        {
            var rule = BestMatch("avoid", QcReasonLabel, tokens, 1);
            if (rule is null && tokens.Count == 0)
                foreach (var r in _doc.Rules)
                    if (r.Kind == "avoid" && r.Reason == QcReasonLabel && r.Keywords.Count == 0) { rule = r; break; }
            if (rule is null)
            {
                rule = new PlutoLearnedRule { Id = NewId("q"), Kind = "avoid", Reason = QcReasonLabel, Keywords = tokens, PromptAnchor = QcAvoidAnchor, NegativeTerms = new List<string>(QcAvoidNegative), Weight = 1.0 };
                _doc.Rules.Add(rule);
            }
            else
            {
                MergeKeywords(rule.Keywords, tokens);
                rule.Weight += 1.0;
            }
            rule.Hits++;
            rule.UpdatedUtc = DateTime.UtcNow;
            id = rule.Id;
            Prune();
        }
        Program.Log("PlutoLearn.QcDefect", new InvalidOperationException(id + " · " + (diagnosis ?? "QC 결함")));   // 진단 이력(로그)
        Save();
        RaiseChanged();
        return id;
    }

    /// <summary>프롬프트 정규화 해시(소문자 · 공백 축약 · SHA-256 앞 16 hex) — 갤러리 흡수 중복 판정.</summary>
    public static string PromptHash(string? prompt)
    {
        var sb = new StringBuilder(prompt?.Length ?? 0);
        var space = false;
        foreach (var c in (prompt ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>갤러리 흡수 기본 가중치(👍 1.0 보다 낮은 기준선).</summary>
    public const double GalleryBaselineWeight = 0.5;

    /// <summary>
    /// 📥 기존 갤러리 학습 흡수 — 저장된 프롬프트들을 긍정 기준선 레시피로 흡수(프롬프트 해시 중복 제거 · 재실행 멱등 · 빈/키워드 없음 제외 · 1회 저장).
    /// 레시피 힌트 = 첫 절(피사체)을 뺀 스타일 절(최대 3).
    /// </summary>
    public PlutoGalleryBootstrapResult ImportGalleryBaseline(IEnumerable<string?> prompts)
    {
        int scanned = 0, imported = 0, dup = 0, skipped = 0;
        lock (_gate)
        {
            foreach (var p in prompts ?? Array.Empty<string?>())
            {
                scanned++;
                var tokens = Tokenize(p);
                if (tokens.Count == 0) { skipped++; continue; }
                var h = PromptHash(p);
                if (_doc.ImportedPromptHashes.Contains(h)) { dup++; continue; }
                _doc.ImportedPromptHashes.Add(h);
                var text = p!.Trim();
                var comma = text.IndexOf(',');
                var hint = ExtractRecipeHint(comma > 0 ? text[..comma] : text, text);
                var recipe = BestMatch("recipe", null, tokens, 2);
                if (recipe is null)
                {
                    recipe = new PlutoLearnedRule { Id = NewId("g"), Kind = "recipe", Keywords = tokens, PromptAnchor = hint, Weight = GalleryBaselineWeight };
                    _doc.Rules.Add(recipe);
                }
                else
                {
                    MergeKeywords(recipe.Keywords, tokens);
                    recipe.Weight += GalleryBaselineWeight;
                    recipe.PromptAnchor ??= hint;
                }
                recipe.Hits++;
                recipe.UpdatedUtc = DateTime.UtcNow;
                imported++;
            }
            while (_doc.ImportedPromptHashes.Count > 4000) _doc.ImportedPromptHashes.RemoveAt(0);
            if (imported > 0) Prune();
        }
        if (imported > 0) { Save(); RaiseChanged(); }
        return new PlutoGalleryBootstrapResult(scanned, imported, dup, skipped);
    }

    /// <summary>
    /// 📥 디렉터리 스캔판 — <c>*.meta.json</c> 사이드카(갤러리 스키마 camelCase <c>prompt</c>)를 재귀 수집(최대 <paramref name="maxFiles"/>) ·
    /// Mock 산출물(<c>isMock</c>/<c>engine=Mock</c>) 제외 · 손상 JSON 은 건너뜀(throw 없음).
    /// </summary>
    public PlutoGalleryBootstrapResult ImportGalleryBaselineFromDirectory(string? root, int maxFiles = 2000)
    {
        var prompts = new List<string?>(64);
        var skipped = 0;
        if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
        {
            try
            {
                var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 6 };
                foreach (var file in Directory.EnumerateFiles(root, "*.meta.json", opts))
                {
                    if (prompts.Count + skipped >= maxFiles) break;
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(file));
                        var r = doc.RootElement;
                        if (r.ValueKind != JsonValueKind.Object) { skipped++; continue; }
                        if ((r.TryGetProperty("isMock", out var m) && m.ValueKind == JsonValueKind.True)
                            || (r.TryGetProperty("engine", out var e) && e.ValueKind == JsonValueKind.String && string.Equals(e.GetString(), "Mock", StringComparison.OrdinalIgnoreCase)))
                        { skipped++; continue; }
                        if (r.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String) prompts.Add(p.GetString());
                        else skipped++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { skipped++; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Program.Log("PlutoVideoLearningMemoryService.GalleryScan", ex);
            }
        }
        var r2 = ImportGalleryBaseline(prompts);
        return r2 with { Scanned = r2.Scanned + skipped, Skipped = r2.Skipped + skipped };
    }

    /// <summary>문자열 사유 태그 오버로드(라벨 · enum 이름 · 부분 키워드 인식, 미인식 → 서사 중복).</summary>
    public PromptPreflightResult RecordFailureAndGetRefinement(string? rawPrompt, string? reasonTag, int targetDurationSeconds = 5,
        string? aspectRatio = "16:9", bool isAmbientLoop = false, string? existingNegative = null)
        => RecordFailureAndGetRefinement(rawPrompt, ParseReason(reasonTag) ?? PlutoFeedbackReason.NarrativeRepetition, targetDurationSeconds, aspectRatio, isAmbientLoop, existingNegative);

    /// <summary>유사 매칭 키워드(소문자 · 영문 3자+/한글 2자+ · 불용어 제외 · 중복 제거 · 최대 10).</summary>
    public static List<string> Tokenize(string? text)
    {
        var list = new List<string>(10);
        if (string.IsNullOrWhiteSpace(text)) return list;
        var sb = new StringBuilder(24);
        var s = text.ToLowerInvariant();
        for (var i = 0; i <= s.Length; i++)
        {
            var c = i < s.Length ? s[i] : ' ';
            if (char.IsLetterOrDigit(c)) { sb.Append(c); continue; }
            if (sb.Length > 0) { TryAddToken(list, sb.ToString()); sb.Clear(); }
            if (list.Count >= 10) break;
        }
        return list;
    }

    private static void TryAddToken(List<string> list, string tok)
    {
        var hangul = tok[0] >= '가' && tok[0] <= '힣';
        if (tok.Length < (hangul ? 2 : 3)) return;
        foreach (var w in StopWords) if (w == tok) return;
        foreach (var x in list) if (x == tok) return;
        list.Add(tok);
    }

    private static int Overlap(List<string> keywords, List<string> tokens)
    {
        var n = 0;
        foreach (var k in keywords)
            foreach (var t in tokens)
                if (k == t) { n++; break; }
        return n;
    }

    private static void MergeKeywords(List<string> into, List<string> add)
    {
        foreach (var t in add)
        {
            if (into.Count >= 24) return;
            if (!into.Contains(t)) into.Add(t);
        }
    }

    private PlutoLearnedRule? BestMatch(string kind, string? reason, List<string> tokens, int minOverlap)
    {
        PlutoLearnedRule? best = null;
        var bestOv = 0;
        foreach (var r in _doc.Rules)
        {
            if (r.Kind != kind || (reason is not null && r.Reason != reason)) continue;
            var ov = Overlap(r.Keywords, tokens);
            if (ov >= minOverlap && ov > bestOv) { best = r; bestOv = ov; }
        }
        return best;
    }

    private int AddTop(string kind, List<string> tokens, int max, List<string> anchors, List<string> negs, List<string> ids)
    {
        if (tokens.Count == 0) return 0;
        var picked = new List<PlutoLearnedRule>(max);
        foreach (var r in _doc.Rules)
        {
            if (r.Kind != kind || Overlap(r.Keywords, tokens) == 0) continue;
            var i = 0;
            while (i < picked.Count && picked[i].Weight >= r.Weight) i++;
            if (i < max) { picked.Insert(i, r); if (picked.Count > max) picked.RemoveAt(max); }
        }
        foreach (var r in picked) Add(r, anchors, negs, ids);
        return picked.Count;
    }

    private static void Add(PlutoLearnedRule r, List<string> anchors, List<string> negs, List<string> ids)
    {
        if (!string.IsNullOrWhiteSpace(r.PromptAnchor) && !ContainsIgnoreCase(anchors, r.PromptAnchor)) anchors.Add(r.PromptAnchor);
        foreach (var n in r.NegativeTerms) if (!ContainsIgnoreCase(negs, n)) negs.Add(n);
        ids.Add(r.Id);
    }

    private static bool ContainsIgnoreCase(List<string> list, string s)
    {
        foreach (var x in list) if (string.Equals(x, s, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>보정본에서 원문에 없던 스타일 절(디렉터 범용 앵커 제외 · 최대 3절) → 레시피 힌트.</summary>
    public static string? ExtractRecipeHint(string? raw, string? refined)
    {
        if (string.IsNullOrWhiteSpace(refined)) return null;
        var rawLower = (raw ?? "").ToLowerInvariant();
        var stripped = PromptPreflightDirector.StripAnchors(refined);
        var sb = new StringBuilder(160);
        var n = 0;
        foreach (var part in stripped.Split(',', '.'))
        {
            var c = part.Trim();
            if (c.Length < 8 || c.Length > 120 || rawLower.Contains(c.ToLowerInvariant(), StringComparison.Ordinal)) continue;
            if (PromptPreflightDirector.IsDirectorAnchor(c)) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(c);
            if (++n >= 3) break;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static bool EnsureSeeds(PlutoDirectorMemoryDocument doc)
    {
        var added = false;
        added |= Seed(doc, "seed-subject", PromptPreflightDirector.SubjectAnchor, null, false);
        added |= Seed(doc, "seed-framing", PromptPreflightDirector.CameraAnchor + ", steady tracking when the subject moves", null, false);
        added |= Seed(doc, "seed-physics", PromptPreflightDirector.PhysicsAnchor + ", forward cause-and-effect", null, false);
        added |= Seed(doc, "seed-anti-distortion", null, new[] { "distorted face", "melting objects", "warped geometry" }, false);
        added |= Seed(doc, "seed-ambient", PromptPreflightDirector.AmbientLightingAnchor, null, true);
        return added;
    }

    private static bool Seed(PlutoDirectorMemoryDocument doc, string id, string? anchor, string[]? neg, bool ambientOnly)
    {
        foreach (var r in doc.Rules) if (r.Id == id) return false;
        doc.Rules.Add(new PlutoLearnedRule
        {
            Id = id, Kind = "seed", PromptAnchor = anchor, NegativeTerms = neg is null ? new List<string>() : new List<string>(neg), AmbientOnly = ambientOnly,
            Weight = 1.0, UpdatedUtc = DateTime.UtcNow,
        });
        return true;
    }

    private void Prune()
    {
        while (_doc.Rules.Count > MaxRules)
        {
            var idx = -1;
            for (var i = 0; i < _doc.Rules.Count; i++)
            {
                var r = _doc.Rules[i];
                if (r.Kind == "seed") continue;
                if (idx < 0 || r.Weight < _doc.Rules[idx].Weight || (r.Weight == _doc.Rules[idx].Weight && r.UpdatedUtc < _doc.Rules[idx].UpdatedUtc)) idx = i;
            }
            if (idx < 0) return;
            _doc.Rules.RemoveAt(idx);
        }
    }

    private static string NewId(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);

    private static PlutoLearnedRule Clone(PlutoLearnedRule r) => new()
    {
        Id = r.Id, Kind = r.Kind, Keywords = new List<string>(r.Keywords), PromptAnchor = r.PromptAnchor, NegativeTerms = new List<string>(r.NegativeTerms),
        Reason = r.Reason, AmbientOnly = r.AmbientOnly, Weight = r.Weight, Hits = r.Hits, UpdatedUtc = r.UpdatedUtc,
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static PlutoDirectorMemoryDocument? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var doc = JsonSerializer.Deserialize<PlutoDirectorMemoryDocument>(File.ReadAllText(path), JsonOpts);
            if (doc?.Rules is null) return null;
            doc.Rules.RemoveAll(r => r is null || string.IsNullOrWhiteSpace(r.Id));
            doc.ImportedPromptHashes ??= new List<string>();
            foreach (var r in doc.Rules) { r.Keywords ??= new List<string>(); r.NegativeTerms ??= new List<string>(); }
            return doc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Program.Log("PlutoVideoLearningMemoryService.Load", ex);
            return null;   // 손상 → seed 로 재시작(원본은 다음 저장 시 덮어씀)
        }
    }

    /// <summary>원자적 저장(temp → move) · IO 실패는 로그만.</summary>
    public void Save()
    {
        string json;
        lock (_gate) json = JsonSerializer.Serialize(_doc, JsonOpts);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("PlutoVideoLearningMemoryService.Save", ex);
        }
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Program.Log("PlutoVideoLearningMemoryService.Changed", ex); }
        }
    }

    /// <summary>디버그 요약(예: "학습 3건").</summary>
    public override string ToString() => "학습 " + LearnedRuleCount.ToString(CultureInfo.InvariantCulture) + "건";
}
