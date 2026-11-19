using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🪐 사용자 상호작용 종류 — 학습 가중치 증감.</summary>
public enum InteractionType
{
    Adopted = 0,
    ExportCompleted = 1,
    ThumbsDown = 2,
    ThumbsUp = 3,
}

/// <summary>
/// 🪐 Pluto 자가 적응 학습 — 채택/내보내기/평가 → 트렌드별 가중치를 <c>pluto_trend_memory.json</c> 에 영속.
/// 순위: 80% 활용(가중치 내림차순) + 20% 탐색(상위 2개 밖 최소 상호작용 카드를 2번째 슬롯으로 승격).
/// </summary>
public sealed class PlutoLearningService : IDisposable
{
    public const double AdoptedDelta = 0.05;
    public const double ExportDelta = 0.15;
    public const double ThumbsDownDelta = -0.10;
    public const double ThumbsUpDelta = 0.05;
    public const double ExplorationRate = 0.20;
    public const double MinWeight = -1.0;
    public const double MaxWeight = 5.0;
    public const string FileName = "pluto_trend_memory.json";
    /// <summary>🧠 XP 가중 — 채택 1 · 내보내기 완료 3 · 👍 1. 레벨당 10 XP.</summary>
    public const int AdoptedXp = 1;
    public const int ExportXp = 3;
    public const int ThumbsUpXp = 1;
    public const int XpPerLevel = 10;
    /// <summary>🔥 바이럴 트렌드 메모리 — 키 접두 · 발굴 1회당 XP · 재학습 감쇠 · 활성 임계 가중치.</summary>
    public const string ViralKeyPrefix = "viral:";
    public const string TrendIngestSourceKey = "xp:trend-ingest";
    public const int TrendIngestXp = 2;
    public const double ViralDecay = 0.6;
    public const double ViralActiveThreshold = 0.05;

    private readonly string _path;
    private readonly IPlutoStorageManager? _storage;   // ☁️ 주입 시 Drive 볼트(폴백 포함) 경로·기록 위임
    private readonly Random _rng;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private volatile bool _loaded;
    private volatile bool _readOnly;   // 🛡️ 적재 불가(잠금·권한) 세션 — 기존 파일 덮어쓰기 금지
    private int _disposed;

    public PlutoLearningService() : this((string?)null, null) { }

    /// <summary>☁️ <see cref="IPlutoStorageManager"/> 경유 — <c>G:\내 드라이브\DATA\Pluto</c> 우선 · 로컬 폴백.</summary>
    public PlutoLearningService(IPlutoStorageManager storage, Random? rng = null) : this((string?)null, rng)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoLearningService(string? storagePath, Random? rng = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", FileName)
            : storagePath;
        _rng = rng ?? new Random();
    }

    public string StoragePath => _storage?.ResolveDataPath(FileName) ?? _path;

    /// <summary>🔥 최근 바이럴 트렌드 적재 시각(UTC · 수동 발굴/자율 수확 공통) — 없음 → null.</summary>
    public DateTime? LastViralIngestUtc
    {
        get
        {
            lock (_sync)
                return _entries.TryGetValue(TrendIngestSourceKey, out var e) && e.LastUtc != default
                    ? (e.LastUtc.Kind == DateTimeKind.Local ? e.LastUtc.ToUniversalTime() : DateTime.SpecifyKind(e.LastUtc, DateTimeKind.Utc))
                    : null;
        }
    }

    /// <summary>상태 변경 통지(백그라운드 스레드 가능).</summary>
    public event Action? Changed;

    /// <summary>🛡️ 오답 노트 — 👎/소프트 스킵 → 카드 패턴 토큰 벌점 · 👍/채택 → 벌점 상쇄(미주입 → 가중치 학습만).</summary>
    public IPlutoMistakeNotebookService? MistakeNotebook { get; init; }

    /// <summary>
    /// 🛡️ 카드 피드백 — 가중치 학습(<see cref="RecordInteractionAsync"/>) + 오답 노트 전달(👎 → 벌점 1.0 · 👍/채택 → 상쇄 1.2 · 내보내기 → 노트 미변경).
    /// 노트 실패는 격리([부분 구제] 가중치 학습 결과 유지). 반환 = 갱신 후 가중치.
    /// </summary>
    public async Task<double> RecordCardFeedbackAsync(PlutoTrend trend, InteractionType type, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(trend);
        var w = await RecordInteractionAsync(trend.Id, type, ct).ConfigureAwait(false);
        if (MistakeNotebook is not { } nb) return w;
        try
        {
            var tokens = ExtractMistakeTokens(trend);
            if (tokens.Count == 0) return w;
            if (type == InteractionType.ThumbsDown)
                await nb.RecordNegativeFeedbackAsync(tokens, PlutoMistakeNotebookService.DislikePenalty, "👎 " + trend.Id, ct).ConfigureAwait(false);
            else if (type is InteractionType.ThumbsUp or InteractionType.Adopted)
                await nb.RecordPositiveReliefAsync(tokens, PlutoMistakeNotebookService.DefaultRelief, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Mistake] feedback: " + ex.Message); }
        return w;
    }

    /// <summary>
    /// 🛡️ 소프트 스킵(평가·채택 없이 즉시 재생성) — 배치 내 카드 토큰 합집합에 벌점 0.4(토큰당 1회) · 가중치 불변.
    /// 미주입/빈 배치 → 무동작. 실패 격리.
    /// </summary>
    public async Task RecordSoftSkipAsync(IReadOnlyList<PlutoTrend>? skipped, CancellationToken ct = default)
    {
        if (MistakeNotebook is not { } nb || skipped is null || skipped.Count == 0) return;
        try
        {
            var tokens = new List<string>(skipped.Count * 4);
            for (var i = 0; i < skipped.Count; i++)
                if (skipped[i] is { } t) tokens.AddRange(ExtractMistakeTokens(t));
            if (tokens.Count == 0) return;
            await nb.RecordNegativeFeedbackAsync(tokens, PlutoMistakeNotebookService.SoftSkipPenalty, "skip", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Mistake] skip: " + ex.Message); }
    }

    /// <summary>🛡️ 카드 → 오답 노트 토큰(적중 바이럴 태그 + 카메라 앵글 + BGM 무드 · 빈 값 제외).</summary>
    public static IReadOnlyList<string> ExtractMistakeTokens(PlutoTrend trend)
    {
        ArgumentNullException.ThrowIfNull(trend);
        var tags = PlutoTrendAnalyzer.MatchTags(trend);
        var list = new List<string>(tags.Count + 2);
        for (var i = 0; i < tags.Count; i++) list.Add(tags[i]);
        if (!string.IsNullOrWhiteSpace(trend.CameraMotion)) list.Add(trend.CameraMotion.Trim());
        if (!string.IsNullOrWhiteSpace(trend.BgmStyle)) list.Add(trend.BgmStyle.Trim());
        return list;
    }

    public sealed class Entry
    {
        public double Weight { get; set; }
        public int Interactions { get; set; }
        public DateTime LastUtc { get; set; }
        public int Adopted { get; set; }
        public int Exports { get; set; }
        public int ThumbsUp { get; set; }
        public int ThumbsDown { get; set; }
        /// <summary>📈 외부 학습 보너스 XP(SNS 레이더 채널 학습 등).</summary>
        public int BonusXp { get; set; }
        /// <summary>🎰 성과 패턴 시행 수(돌파 · 부진 · 평균 모두 +1 · 갱신 시 반감기 감쇠 후 재기준 · 구버전 파일 → 0 = <see cref="Interactions"/> 폴백).</summary>
        public double Trials { get; set; }

        // ── Step 13(선택 필드 · 기본값은 직렬화 생략 → Step 12 이하 파일과 양방향 호환) ──
        /// <summary>📐 EWMA 평균 — <c>ctx:</c> = 컨텍스트 기준선(합성 점수 0~1) · <c>pattern:</c> = 잔차 알파 기대치 · <c>drift:</c> = PH 누적 평균 x̄_T.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Mean { get; set; }
        /// <summary>📐 EWMA 분산(<c>ctx:</c> · <c>pattern:</c>).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Var { get; set; }
        /// <summary>📐 <see cref="Mean"/> 표본 수(<c>drift:</c> = PH 재시작 이후 관측 수).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Samples { get; set; }
        /// <summary>🌊 Page-Hinkley 누적합 m_T(<c>drift:</c> 전용).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double PhSum { get; set; }
        /// <summary>🌊 Page-Hinkley 누적 최소 M_T(<c>drift:</c> 전용).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double PhMin { get; set; }
        /// <summary>🌊 탐색 서지 수준 0~1(<c>drift:</c> 전용 · <see cref="LastUtc"/> 기준 7일 반감기).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double Surge { get; set; }
        /// <summary>🌊 누적 드리프트 감지 횟수(<c>drift:</c> 전용).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Drifts { get; set; }
        /// <summary>🌊 마지막 드리프트 감지 시각(<c>drift:</c> 전용).</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public DateTime DriftUtc { get; set; }

        // ── Step 15(선택 필드 · 기본값 직렬화 생략) — ⚖️ 다목적 성과 EWMA(<c>pattern:</c> 전용 · 각 축 0~1) ──
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double ObjH { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double ObjC { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double ObjT { get; set; }
        /// <summary>⚖️ 다목적 EWMA 표본 수.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public double ObjN { get; set; }
    }

    private static double Finite(double v, double min = double.NegativeInfinity) => double.IsFinite(v) && v >= min ? v : 0.0;

    /// <summary>🛡️ Step 13 선택 필드 정규화 — 손상/수기 편집 값(NaN · 음수 표본 · 범위 밖 서지)이 통계에 스며들지 않게 0 으로.</summary>
    private static void SanitizeExtended(Entry e)
    {
        e.Mean = Finite(e.Mean); e.Var = Finite(e.Var, 0.0); e.Samples = Finite(e.Samples, 0.0);
        e.PhSum = Finite(e.PhSum); e.PhMin = Finite(e.PhMin);
        e.Surge = double.IsFinite(e.Surge) ? Math.Clamp(e.Surge, 0.0, 1.0) : 0.0;
        if (e.Drifts < 0) e.Drifts = 0;
        if (!double.IsFinite(e.Trials) || e.Trials < 0) e.Trials = 0.0;
        e.ObjH = Unit(e.ObjH); e.ObjC = Unit(e.ObjC); e.ObjT = Unit(e.ObjT); e.ObjN = Finite(e.ObjN, 0.0);
    }

    private static double Unit(double v) => double.IsFinite(v) ? Math.Clamp(v, 0.0, 1.0) : 0.0;

    /// <summary>🧠 누적 XP = 채택×1 + 내보내기×3 + 👍×1.</summary>
    public static int ComputeXp(int adopted, int exports, int thumbsUp)
        => Math.Max(0, adopted) * AdoptedXp + Math.Max(0, exports) * ExportXp + Math.Max(0, thumbsUp) * ThumbsUpXp;

    /// <summary>🧠 레벨 = 1 + XP / 10.</summary>
    public static int LevelFor(int xp) => 1 + Math.Max(0, xp) / XpPerLevel;

    /// <summary>🧠 누적 XP(전체 트렌드 합산).</summary>
    public int CurrentXP
    {
        get
        {
            int a = 0, x = 0, u = 0, b = 0;
            lock (_sync)
                foreach (var kv in _entries) { a += kv.Value.Adopted; x += kv.Value.Exports; u += kv.Value.ThumbsUp; b += Math.Max(0, kv.Value.BonusXp); }
            return ComputeXp(a, x, u) + b;
        }
    }

    /// <summary>🧠 현재 레벨.</summary>
    public int CurrentLevel => LevelFor(CurrentXP);

    /// <summary>🧠 다음 레벨 도달 XP(누적 기준).</summary>
    public int NextLevelXP => CurrentLevel * XpPerLevel;

    /// <summary>🧠 누적 피드백 횟수(전 유형).</summary>
    public int TotalInteractions
    {
        get
        {
            var n = 0;
            lock (_sync) foreach (var kv in _entries) n += kv.Value.Interactions;
            return n;
        }
    }

    public static double Delta(InteractionType t) => t switch
    {
        InteractionType.Adopted => AdoptedDelta,
        InteractionType.ExportCompleted => ExportDelta,
        InteractionType.ThumbsDown => ThumbsDownDelta,
        InteractionType.ThumbsUp => ThumbsUpDelta,
        _ => 0,
    };

    public double GetWeight(string trendId)
    {
        lock (_sync) return _entries.TryGetValue(trendId, out var e) ? e.Weight : 0;
    }

    public int GetInteractions(string trendId)
    {
        lock (_sync) return _entries.TryGetValue(trendId, out var e) ? e.Interactions : 0;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            // 손상 JSON → .corrupt 격리 후 빈 메모리 · 일시적 잠금 → 재시도 · 끝내 읽기 불가 → 디스크 덮어쓰기 금지(누적 XP 보호)
            var (data, status) = await PlutoJsonStore.TryLoadAsync<Dictionary<string, Entry>>(StoragePath, "Pluto", ct).ConfigureAwait(false);
            _readOnly = status == PlutoLoadStatus.Unreadable;
            lock (_sync)
            {
                _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                if (data is not null)
                    foreach (var kv in data)
                        if (kv.Value is not null && !string.IsNullOrWhiteSpace(kv.Key) && double.IsFinite(kv.Value.Weight))
                        {
                            SanitizeExtended(kv.Value);
                            _entries[kv.Key] = kv.Value;
                        }
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    /// <summary>상호작용 기록 → 가중치 갱신(±클램프) → 즉시 원자적 영속. 반환 = 갱신 후 가중치.</summary>
    public async Task<double> RecordInteractionAsync(string trendId, InteractionType type, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trendId)) throw new ArgumentException("trendId required", nameof(trendId));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        double w;
        lock (_sync)
        {
            if (!_entries.TryGetValue(trendId, out var e)) { e = new Entry(); _entries[trendId] = e; }
            e.Weight = Math.Clamp(e.Weight + Delta(type), MinWeight, MaxWeight);
            e.Interactions++;
            switch (type)
            {
                case InteractionType.Adopted: e.Adopted++; break;
                case InteractionType.ExportCompleted: e.Exports++; break;
                case InteractionType.ThumbsUp: e.ThumbsUp++; break;
                case InteractionType.ThumbsDown: e.ThumbsDown++; break;
            }
            e.LastUtc = DateTime.UtcNow;
            w = e.Weight;
        }
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
        return w;
    }

    /// <summary>
    /// 📈 보너스 XP 적립(예: SNS 레이더 채널 학습 +2) — 출처 키 엔트리에 누적 · 가중치 불변 · 상호작용 +1 · 즉시 원자적 영속. 반환 = 갱신 후 누적 XP.
    /// </summary>
    public async Task<int> AddBonusXpAsync(string sourceKey, int xp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceKey)) throw new ArgumentException("sourceKey required", nameof(sourceKey));
        if (xp <= 0) throw new ArgumentOutOfRangeException(nameof(xp));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_sync)
        {
            if (!_entries.TryGetValue(sourceKey, out var e)) { e = new Entry(); _entries[sourceKey] = e; }
            e.BonusXp = Math.Max(0, e.BonusXp) + xp;
            e.Interactions++;
            e.LastUtc = DateTime.UtcNow;
        }
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
        return CurrentXP;
    }

    /// <summary>
    /// 🔥 바이럴 트렌드 토큰 적재 — 태그별 가중치 = 기존×<see cref="ViralDecay"/> + 점수(0~<see cref="MaxWeight"/> 클램프 · 최근 발굴 우대)
    /// + 발굴 보너스 <see cref="TrendIngestXp"/> XP · 1회 원자적 영속. 유효 토큰 0건 → XP 미적립. 반환 = 갱신 후 누적 XP.
    /// </summary>
    public async Task<int> IngestViralTrendsAsync(IReadOnlyList<ViralTrendToken> tokens, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var accepted = 0;
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t is null || string.IsNullOrWhiteSpace(t.Tag) || !double.IsFinite(t.Score) || t.Score <= 0) continue;
                var key = ViralKeyPrefix + t.Tag;
                if (!_entries.TryGetValue(key, out var e)) { e = new Entry(); _entries[key] = e; }
                e.Weight = Math.Clamp(Math.Max(0, e.Weight) * ViralDecay + t.Score, 0, MaxWeight);
                e.LastUtc = now;
                accepted++;
            }
            if (accepted > 0)
            {
                if (!_entries.TryGetValue(TrendIngestSourceKey, out var x)) { x = new Entry(); _entries[TrendIngestSourceKey] = x; }
                x.BonusXp = Math.Max(0, x.BonusXp) + TrendIngestXp;
                x.Interactions++;
                x.LastUtc = now;
            }
        }
        if (accepted == 0) return CurrentXP;
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
        return CurrentXP;
    }

    /// <summary>📈 성과 패턴 키 접두사(훅/스타일/LUT/BGM) — 트렌드 · 바이럴 키와 분리.</summary>
    public const string PatternKeyPrefix = "pattern:";
    /// <summary>📈 성과 패턴 기본 가중치(미추적) · 상한.</summary>
    public const double PatternBaseWeight = 1.0, PatternMaxWeight = 2.5;

    /// <summary>⏳ 성과 패턴 보상 반감기(일) — 최근 텔레메트리가 오래된 이력을 압도.</summary>
    public const double PatternHalfLifeDays = 14.0;

    /// <summary>🕰️ 결정론 시계 주입(테스트 · 재현용) — 미지정 → <see cref="DateTime.UtcNow"/>. 성과 패턴 감쇠 · 재기준에만 사용.</summary>
    public Func<DateTime>? UtcNowProvider { get; init; }

    private DateTime PatternNow() => UtcNowProvider?.Invoke() ?? DateTime.UtcNow;

    /// <summary>⏳ 반감기 계수 <c>0.5^(elapsedDays / halfLifeDays)</c> — 기준 시각 미상(default) · 시계 역행 → 1(감쇠 없음).</summary>
    public static double HalfLifeFactor(DateTime lastUtc, DateTime nowUtc, double halfLifeDays)
    {
        if (lastUtc == default || !(halfLifeDays > 0)) return 1.0;
        var days = (nowUtc - lastUtc).TotalDays;
        return days > 0 ? Math.Pow(0.5, days / halfLifeDays) : 1.0;
    }

    /// <summary>
    /// ⏳ 성과 패턴 가중치 시간 감쇠 — 기준(1.0) 대비 초과 보상에 <c>w(t) = w0 × 0.5^(Δt / 14일)</c> 적용 → 재확인 없는 과거 승자는 기본 탐색으로 수렴.
    /// </summary>
    public static double DecayPatternWeight(double weight, DateTime lastUtc, DateTime nowUtc)
    {
        if (!double.IsFinite(weight) || weight <= 0) return PatternBaseWeight;
        return PatternBaseWeight + (weight - PatternBaseWeight) * HalfLifeFactor(lastUtc, nowUtc, PatternHalfLifeDays);
    }

    private static double EffectiveTrials(Entry e, DateTime nowUtc)
    {
        var raw = e.Trials > 0 && double.IsFinite(e.Trials) ? e.Trials : Math.Max(0, e.Interactions);   // 구버전 → 강화 횟수
        return raw * HalfLifeFactor(e.LastUtc, nowUtc, PatternHalfLifeDays);
    }

    /// <summary>📈 성과 패턴 가중치(미추적 → <see cref="PatternBaseWeight"/>) — <paramref name="asOfUtc"/>(null → 주입 시계) 기준 반감기 감쇠 반영.</summary>
    public double GetPatternWeight(string patternToken, DateTime? asOfUtc = null)
    {
        if (string.IsNullOrWhiteSpace(patternToken)) return PatternBaseWeight;
        var now = asOfUtc ?? PatternNow();
        lock (_sync) return _entries.TryGetValue(PatternKeyPrefix + patternToken, out var e) && e.Weight > 0 ? DecayPatternWeight(e.Weight, e.LastUtc, now) : PatternBaseWeight;
    }

    /// <summary>🎰 성과 패턴 유효 시행 수(반감기 감쇠 · 미추적 → 0) — UCB1 불확실성 항의 armTrials.</summary>
    public double GetPatternTrials(string patternToken, DateTime? asOfUtc = null)
    {
        if (string.IsNullOrWhiteSpace(patternToken)) return 0.0;
        var now = asOfUtc ?? PatternNow();
        lock (_sync) return _entries.TryGetValue(PatternKeyPrefix + patternToken, out var e) ? EffectiveTrials(e, now) : 0.0;
    }

    /// <summary>📈 학습된 성과 패턴 스냅샷 — <paramref name="kindPrefix"/>(예: <c>hook:</c>)로 시작하는 토큰 → (접두사 제외 값, 감쇠 반영 가중치).</summary>
    public IReadOnlyList<KeyValuePair<string, double>> GetPatternWeights(string kindPrefix, DateTime? asOfUtc = null)
    {
        ArgumentNullException.ThrowIfNull(kindPrefix);
        var full = PatternKeyPrefix + kindPrefix;
        var now = asOfUtc ?? PatternNow();
        var list = new List<KeyValuePair<string, double>>();
        lock (_sync)
            foreach (var kv in _entries)
            {
                if (!kv.Key.StartsWith(full, StringComparison.Ordinal) || kv.Key.Length == full.Length || kv.Value is null) continue;
                list.Add(new KeyValuePair<string, double>(kv.Key[full.Length..], DecayPatternWeight(kv.Value.Weight, kv.Value.LastUtc, now)));
            }
        list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));   // 결정론 순서
        return list;
    }

    /// <summary>
    /// 🎰 성과 패턴 시행 기록(부진 · 평균 성과 영상) — 가중치 변화 없이 시행 수만 +1(감쇠 후 재기준) → UCB1이 '검증됐지만 평범한' 후보와 '미검증' 후보를 구분.
    /// 유효 토큰 0건 → 무변경.
    /// </summary>
    public async Task RecordPatternTrialsAsync(IReadOnlyList<string> patternTokens, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patternTokens);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var touched = 0;
        lock (_sync)
        {
            var now = PatternNow();
            for (var i = 0; i < patternTokens.Count; i++)
            {
                var t = patternTokens[i];
                if (string.IsNullOrWhiteSpace(t)) continue;
                var key = PatternKeyPrefix + t.Trim();
                if (!_entries.TryGetValue(key, out var e)) { e = new Entry { Weight = PatternBaseWeight }; _entries[key] = e; }
                e.Trials = EffectiveTrials(e, now) + 1.0;
                e.Weight = DecayPatternWeight(e.Weight, e.LastUtc, now);   // 기준 시각 이동 → 가중치도 재기준
                e.LastUtc = now;
                touched++;
            }
        }
        if (touched == 0) return;
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>
    /// 📈 폐루프 성과 강화 — 패턴 토큰별 가중치 ×<paramref name="multiplier"/>(기준 1.0 · 상한 <see cref="PatternMaxWeight"/>) + 출처 키 보너스 XP를 1회 원자적 영속.
    /// 유효 토큰 0건 + XP 0 → 무변경. 반환 = 갱신 후 누적 XP.
    /// </summary>
    public async Task<int> ApplyPatternReinforcementAsync(IReadOnlyList<string> patternTokens, double multiplier, string sourceKey, int xp, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patternTokens);
        if (string.IsNullOrWhiteSpace(sourceKey)) throw new ArgumentException("sourceKey required", nameof(sourceKey));
        if (!double.IsFinite(multiplier) || multiplier <= 0) throw new ArgumentOutOfRangeException(nameof(multiplier));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var touched = 0;
        lock (_sync)
        {
            var now = PatternNow();
            for (var i = 0; i < patternTokens.Count; i++)
            {
                var t = patternTokens[i];
                if (string.IsNullOrWhiteSpace(t)) continue;
                var key = PatternKeyPrefix + t.Trim();
                if (!_entries.TryGetValue(key, out var e)) { e = new Entry { Weight = PatternBaseWeight }; _entries[key] = e; }
                var basis = DecayPatternWeight(e.Weight, e.LastUtc, now);   // ⏳ 오래된 보상은 감쇠 후 재강화
                e.Trials = EffectiveTrials(e, now) + 1.0;
                e.Weight = Math.Clamp(basis * multiplier, 0.1, PatternMaxWeight);
                e.Interactions++;
                e.LastUtc = now;
                touched++;
            }
            if (xp > 0)
            {
                if (!_entries.TryGetValue(sourceKey, out var x)) { x = new Entry(); _entries[sourceKey] = x; }
                x.BonusXp = Math.Max(0, x.BonusXp) + xp;
                x.Interactions++;
                x.LastUtc = now;
            }
        }
        if (touched == 0 && xp <= 0) return CurrentXP;
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
        return CurrentXP;
    }

    // ── 📐 컨텍스트 기준선(잔차 알파) · 🌊 Page-Hinkley 레짐 드리프트 ──

    /// <summary>패턴 기대치 표본 상한(EWMA 스텝 하한 α 가 최근성을 담당 — 카운터 폭주 방지).</summary>
    public const double MaxExpectancySamples = 1000.0;

    /// <summary>
    /// 📐🌊 성과 관측 1건을 컨텍스트 기준선 · 패턴 기대치 · 레짐 드리프트 검정에 반영(1회 원자적 영속).
    ///   ① 잔차 알파 = <paramref name="score"/> − 유효 기준선(관측 <b>이전</b> 컨텍스트 EWMA ⊕ 전역 EWMA 부분 풀링).
    ///   ② 예측 잔차 x = (기준선 + 패턴 알파 기대치 평균) − 관측 → Page-Hinkley 갱신 · 감지 시 탐색 서지 1.0 + 연루 패턴 초과 가중치 표적 망각.
    ///   ③ 그 뒤에 컨텍스트 · 전역 기준선과 패턴 기대치를 갱신(관측이 자기 자신의 기준을 오염시키지 않음).
    /// <paramref name="expectancyTokens"/> = <c>hook:…</c> · <c>look:…</c> 패턴 토큰. 비유한 점수 → 무변경.
    /// </summary>
    public async Task<PlutoContextualObservation> RecordContextualObservationAsync(string? domain, string? renderMode, double score, IReadOnlyList<string>? expectancyTokens,
        CancellationToken ct = default, PlutoObjectiveVector? objectives = null)
    {
        if (!double.IsFinite(score)) return PlutoContextualObservation.None;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        score = Math.Clamp(score, 0.0, 1.0);
        PlutoContextualObservation obs;
        lock (_sync)
        {
            var now = PatternNow();
            var key = PlutoContextualBaseline.ContextKey(domain, renderMode);
            _entries.TryGetValue(key, out var ctx);
            _entries.TryGetValue(PlutoContextualBaseline.GlobalContextKey, out var glob);
            var nCtx = ctx?.Samples ?? 0.0;
            var nGlob = glob?.Samples ?? 0.0;
            var established = PlutoContextualBaseline.IsExplicit(domain, renderMode) && nCtx >= PlutoContextualBaseline.MinContextSamples;
            double baseline;
            if (established) baseline = ctx!.Mean;   // 성립한 컨텍스트는 자기 기준선만 사용(전역 수축은 서로 다른 포맷 편향을 다시 섞는다)
            else if (nCtx > 0 && nGlob > 0) baseline = (nCtx * ctx!.Mean + PlutoContextualBaseline.PoolingKappa * glob!.Mean) / (nCtx + PlutoContextualBaseline.PoolingKappa);
            else if (nCtx > 0) baseline = ctx!.Mean;
            else if (nGlob > 0) baseline = glob!.Mean;
            else baseline = score;   // 최초 관측 — 정보 없음 → 잔차 0
            var alpha = score - baseline;
            var z = established ? alpha / Math.Max(PlutoContextualBaseline.MinStdDev, Math.Sqrt(Math.Max(0.0, ctx!.Var))) : 0.0;

            // 🌊 예측 잔차 — 기대치가 성립한 패턴만 참여
            double expSum = 0; var expN = 0;
            if (expectancyTokens is not null)
                for (var i = 0; i < expectancyTokens.Count; i++)
                {
                    var t = expectancyTokens[i];
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    if (_entries.TryGetValue(PatternKeyPrefix + t.Trim(), out var pe) && pe.Samples >= PlutoContextualBaseline.MinExpectancySamples) { expSum += pe.Mean; expN++; }
                }
            var drift = false;
            var surge = 0.0;
            _entries.TryGetValue(PlutoContextualBaseline.DriftStateKey, out var de);
            if (expN > 0 || de is not null)
            {
                if (de is null) { de = new Entry(); _entries[PlutoContextualBaseline.DriftStateKey] = de; }
                var st = new PlutoPageHinkleyState
                {
                    Count = de.Samples, Mean = de.Mean, Cumulative = de.PhSum, Minimum = de.PhMin, Drifts = de.Drifts,
                    Surge = PlutoRegimeDriftDetector.EffectiveSurge(de.Surge, de.LastUtc, now),
                };
                if (expN > 0) drift = PlutoRegimeDriftDetector.Update(ref st, baseline + expSum / expN, score);
                de.Samples = st.Count; de.Mean = st.Mean; de.PhSum = st.Cumulative; de.PhMin = st.Minimum; de.Drifts = st.Drifts; de.Surge = st.Surge;
                de.LastUtc = now;
                if (drift) de.DriftUtc = now;
                surge = st.Surge;
            }

            if (expectancyTokens is not null)
                for (var i = 0; i < expectancyTokens.Count; i++)
                {
                    var t = expectancyTokens[i];
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    var pk = PatternKeyPrefix + t.Trim();
                    if (!_entries.TryGetValue(pk, out var pe)) { pe = new Entry { Weight = PatternBaseWeight }; _entries[pk] = pe; }
                    if (drift && pe.Weight > PatternBaseWeight)
                    {
                        // 🌊 표적 망각 — 레짐 전환에 연루된 낡은 승자의 초과 가중치를 즉시 절반으로(감쇠 기준 시각 재설정)
                        var decayed = DecayPatternWeight(pe.Weight, pe.LastUtc, now);
                        pe.Trials = EffectiveTrials(pe, now);
                        pe.Weight = PatternBaseWeight + (decayed - PatternBaseWeight) * PlutoRegimeDriftDetector.RegimeHaircut;
                        pe.LastUtc = now;
                    }
                    UpdateEwma(pe, alpha, PlutoContextualBaseline.PatternAlpha);
                    if (objectives is { IsFinite: true } ov) UpdateObjectives(pe, ov);   // ⚖️ 다목적(훅 · 완주 · CTR) EWMA
                }

            if (ctx is null) { ctx = new Entry(); _entries[key] = ctx; }
            UpdateEwma(ctx, score, PlutoContextualBaseline.Alpha);
            ctx.LastUtc = now;
            if (!string.Equals(key, PlutoContextualBaseline.GlobalContextKey, StringComparison.Ordinal))
            {
                if (glob is null) { glob = new Entry(); _entries[PlutoContextualBaseline.GlobalContextKey] = glob; }
                UpdateEwma(glob, score, PlutoContextualBaseline.Alpha);
                glob.LastUtc = now;
            }
            obs = new PlutoContextualObservation(key, score, baseline, alpha, z, nCtx, established, drift, surge);
        }
        await PersistAsync(ct).ConfigureAwait(false);
        return obs;
    }

    /// <summary>EWMA 평균 · 분산 증분 갱신(초기 구간 = 단순 평균 · 이후 스텝 α).</summary>
    private static void UpdateEwma(Entry e, double x, double alpha)
    {
        var n = Math.Min(MaxExpectancySamples, e.Samples + 1.0);
        var step = PlutoContextualBaseline.StepFor(n, alpha);
        var d = x - e.Mean;
        e.Mean += step * d;
        e.Var = Math.Max(0.0, (1.0 - step) * (e.Var + step * d * d));
        e.Samples = n;
    }

    /// <summary>⚖️ 다목적 EWMA 증분 갱신(초기 구간 = 단순 평균 · 이후 스텝 <see cref="PlutoContextualBaseline.PatternAlpha"/>).</summary>
    private static void UpdateObjectives(Entry e, PlutoObjectiveVector v)
    {
        var n = Math.Min(MaxExpectancySamples, e.ObjN + 1.0);
        var step = PlutoContextualBaseline.StepFor(n, PlutoContextualBaseline.PatternAlpha);
        e.ObjH += step * (Unit(v.Hook) - e.ObjH);
        e.ObjC += step * (Unit(v.Completion) - e.ObjC);
        e.ObjT += step * (Unit(v.Ctr) - e.ObjT);
        e.ObjN = n;
    }

    /// <summary>⚖️ 패턴 다목적 성과(훅 · 완주 · CTR EWMA · 미추적 → 표본 0).</summary>
    public (PlutoObjectiveVector Objectives, double Samples) GetPatternObjectives(string patternToken)
    {
        if (string.IsNullOrWhiteSpace(patternToken)) return (default, 0.0);
        lock (_sync)
            return _entries.TryGetValue(PatternKeyPrefix + patternToken, out var e) && e.ObjN > 0
                ? (new PlutoObjectiveVector(e.ObjH, e.ObjC, e.ObjT), e.ObjN)
                : (default, 0.0);
    }

    /// <summary>
    /// ⚖️ 다목적 성과만 기록(A/B 변형처럼 컨텍스트 관측을 거치지 않는 경로) — 가중치 · 시행 · 기대치 불변 · 유효 토큰 0건/비유한 → 무변경.
    /// </summary>
    public async Task RecordPatternObjectivesAsync(IReadOnlyList<string> patternTokens, PlutoObjectiveVector objectives, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(patternTokens);
        if (!objectives.IsFinite) return;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var touched = 0;
        lock (_sync)
        {
            for (var i = 0; i < patternTokens.Count; i++)
            {
                var t = patternTokens[i];
                if (string.IsNullOrWhiteSpace(t)) continue;
                var key = PatternKeyPrefix + t.Trim();
                if (!_entries.TryGetValue(key, out var e)) { e = new Entry { Weight = PatternBaseWeight }; _entries[key] = e; }
                UpdateObjectives(e, objectives);
                touched++;
            }
        }
        if (touched == 0) return;
        await PersistAsync(ct).ConfigureAwait(false);
    }

    /// <summary>📐 컨텍스트 기준선 스냅샷(전역 <c>ctx:*</c> 제외) — 표본 내림차순 · 키 사전순.</summary>
    public IReadOnlyList<PlutoContextBaselineStat> GetContextBaselines()
    {
        var list = new List<PlutoContextBaselineStat>(8);
        lock (_sync)
            foreach (var kv in _entries)
            {
                if (kv.Value is null || kv.Value.Samples <= 0 || !kv.Key.StartsWith(PlutoContextualBaseline.ContextKeyPrefix, StringComparison.Ordinal)) continue;
                if (!PlutoContextualBaseline.TrySplit(kv.Key, out var domain, out var mode)) continue;   // 전역 키(ctx:*)는 구분자 없음 → 제외
                list.Add(new PlutoContextBaselineStat(kv.Key, domain, mode, kv.Value.Mean, Math.Sqrt(Math.Max(0.0, kv.Value.Var)), kv.Value.Samples));
            }
        list.Sort(static (a, b) =>
        {
            var c = b.Samples.CompareTo(a.Samples);
            return c != 0 ? c : string.CompareOrdinal(a.ContextKey, b.ContextKey);
        });
        return list;
    }

    /// <summary>📐 전역 기준선(모든 컨텍스트 EWMA · 없음 → null).</summary>
    public double? GetGlobalBaseline()
    {
        lock (_sync) return _entries.TryGetValue(PlutoContextualBaseline.GlobalContextKey, out var e) && e.Samples > 0 ? e.Mean : null;
    }

    /// <summary>📐 패턴 잔차 알파 기대치(EWMA · 미추적 → (0, 0)).</summary>
    public (double Alpha, double Samples) GetPatternAlpha(string patternToken)
    {
        if (string.IsNullOrWhiteSpace(patternToken)) return (0.0, 0.0);
        lock (_sync) return _entries.TryGetValue(PatternKeyPrefix + patternToken, out var e) ? (e.Mean, e.Samples) : (0.0, 0.0);
    }

    /// <summary>
    /// 🎯 반사실 예측 스냅샷(읽기 전용 · 관측 반영 <b>이전</b>에 호출) — 메타 보정 감사용.
    ///   ▸ 예측 = 유효 기준선(<see cref="RecordContextualObservationAsync"/> 와 동일 규칙) + 선택 훅·룩 알파 기대치 평균(성립 패턴만).
    ///   ▸ 최선 대안 = 같은 기준선 + 종류별 '선택하지 않은' 최고 알파 패턴으로 교체한 구성(대안 없는 종류는 선택 패턴 유지).
    /// 기준선 정보가 전혀 없으면 <see cref="PlutoExpectancyForecast.None"/> · 대안이 하나도 없으면 대안 점수 NaN.
    /// </summary>
    public PlutoExpectancyForecast ForecastExpectancy(string? domain, string? renderMode, string? hook, string? look)
    {
        lock (_sync)
        {
            _entries.TryGetValue(PlutoContextualBaseline.ContextKey(domain, renderMode), out var ctx);
            _entries.TryGetValue(PlutoContextualBaseline.GlobalContextKey, out var glob);
            var nCtx = ctx?.Samples ?? 0.0;
            var nGlob = glob?.Samples ?? 0.0;
            if (nCtx <= 0 && nGlob <= 0) return PlutoExpectancyForecast.None;
            double baseline;
            if (PlutoContextualBaseline.IsExplicit(domain, renderMode) && nCtx >= PlutoContextualBaseline.MinContextSamples) baseline = ctx!.Mean;
            else if (nCtx > 0 && nGlob > 0) baseline = (nCtx * ctx!.Mean + PlutoContextualBaseline.PoolingKappa * glob!.Mean) / (nCtx + PlutoContextualBaseline.PoolingKappa);
            else baseline = nCtx > 0 ? ctx!.Mean : glob!.Mean;

            double chosenSum = 0, altSum = 0; int chosenN = 0, altN = 0;
            string? altLabel = null; var altBest = double.NegativeInfinity; var anyAlternative = false;
            ForecastKind("hook:", hook, ref chosenSum, ref chosenN, ref altSum, ref altN, ref altLabel, ref altBest, ref anyAlternative);
            ForecastKind("look:", look, ref chosenSum, ref chosenN, ref altSum, ref altN, ref altLabel, ref altBest, ref anyAlternative);
            var predicted = Math.Clamp(baseline + (chosenN > 0 ? chosenSum / chosenN : 0.0), 0.0, 1.0);
            var alternative = anyAlternative ? Math.Clamp(baseline + altSum / altN, 0.0, 1.0) : double.NaN;
            return new PlutoExpectancyForecast(true, baseline, predicted, alternative, anyAlternative ? altLabel : null, chosenN);
        }
    }

    /// <summary>종류(훅/룩) 1개의 선택 알파 · 최고 미선택 알파 누적(호출자가 <c>_sync</c> 보유 · 동률 → 키 사전순).</summary>
    private void ForecastKind(string kindPrefix, string? chosenLabel, ref double chosenSum, ref int chosenN, ref double altSum, ref int altN,
        ref string? altLabel, ref double altBest, ref bool anyAlternative)
    {
        var full = PatternKeyPrefix + kindPrefix;
        var chosenKey = string.IsNullOrWhiteSpace(chosenLabel) ? null : full + chosenLabel.Trim();
        double? chosenAlpha = null;
        if (chosenKey is not null && _entries.TryGetValue(chosenKey, out var ce) && ce.Samples >= PlutoContextualBaseline.MinExpectancySamples)
        {
            chosenAlpha = ce.Mean;
            chosenSum += ce.Mean; chosenN++;
        }
        string? bestKey = null; var best = double.NegativeInfinity;
        foreach (var kv in _entries)
        {
            if (kv.Value is null || kv.Value.Samples < PlutoContextualBaseline.MinExpectancySamples || !double.IsFinite(kv.Value.Mean)) continue;
            if (kv.Key.Length <= full.Length || !kv.Key.StartsWith(full, StringComparison.Ordinal)) continue;
            if (chosenKey is not null && string.Equals(kv.Key, chosenKey, StringComparison.Ordinal)) continue;
            if (kv.Value.Mean > best || (kv.Value.Mean == best && string.CompareOrdinal(kv.Key, bestKey) < 0)) { best = kv.Value.Mean; bestKey = kv.Key; }
        }
        if (bestKey is not null)
        {
            anyAlternative = true;
            altSum += best; altN++;
            if (best > altBest) { altBest = best; altLabel = bestKey[PatternKeyPrefix.Length..]; }
        }
        else if (chosenAlpha is { } keep) { altSum += keep; altN++; }
    }

    /// <summary>🌊 현재 탐색 서지 수준 0~1(7일 반감기 시간 감쇠 반영).</summary>
    public double GetExplorationSurge(DateTime? asOfUtc = null)
    {
        var now = asOfUtc ?? PatternNow();
        lock (_sync) return _entries.TryGetValue(PlutoContextualBaseline.DriftStateKey, out var e) ? PlutoRegimeDriftDetector.EffectiveSurge(e.Surge, e.LastUtc, now) : 0.0;
    }

    /// <summary>🌊 레짐 드리프트 현황(이력 없음 → <see cref="PlutoRegimeDriftStatus.Stable"/>).</summary>
    public PlutoRegimeDriftStatus GetRegimeDriftStatus(DateTime? asOfUtc = null)
    {
        var now = asOfUtc ?? PatternNow();
        lock (_sync)
        {
            if (!_entries.TryGetValue(PlutoContextualBaseline.DriftStateKey, out var e)) return PlutoRegimeDriftStatus.Stable;
            var surge = PlutoRegimeDriftDetector.EffectiveSurge(e.Surge, e.LastUtc, now);
            return new PlutoRegimeDriftStatus(surge > 0, surge, PlutoRegimeDriftDetector.ExplorationTemperature(surge), Math.Max(0.0, e.PhSum - e.PhMin),
                PlutoRegimeDriftDetector.Threshold, e.Drifts, e.Samples, e.DriftUtc == default ? null : e.DriftUtc);
        }
    }

    /// <summary>🔥 활성 바이럴 트렌드 풀 —가중치 ≥ <see cref="ViralActiveThreshold"/> · 내림차순(동점 태그 사전순) 상위 <paramref name="max"/>건. 미적재 → 빈 목록.</summary>
    public IReadOnlyList<ActiveViralTrend> GetActiveViralTrends(int max = 3)
    {
        if (max <= 0) return Array.Empty<ActiveViralTrend>();
        var list = new List<ActiveViralTrend>(8);
        lock (_sync)
            foreach (var kv in _entries)
                if (kv.Key.StartsWith(ViralKeyPrefix, StringComparison.Ordinal) && kv.Value.Weight >= ViralActiveThreshold)
                    list.Add(new ActiveViralTrend(kv.Key[ViralKeyPrefix.Length..], kv.Value.Weight));
        list.Sort((a, b) => { var c = b.Weight.CompareTo(a.Weight); return c != 0 ? c : string.CompareOrdinal(a.Tag, b.Tag); });
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        return list;
    }

    /// <summary>
    /// 게이트 획득 후 "최신" 상태를 직렬화해 원자적 기록 — 스냅샷을 게이트 밖에서 뜨면 동시 기록(피드백 연타 + 내보내기 완료) 시 오래된 스냅샷이 나중에 기록되는 Lost-Update 발생.
    /// 실패/읽기 불가 세션 → false([부분 구제] 메모리 내 상태 유지).
    /// </summary>
    private async Task<bool> PersistAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readOnly) return false;
            string json;
            lock (_sync) json = JsonSerializer.Serialize(_entries);
            return _storage is not null
                ? await _storage.WriteAllTextAtomicAsync(FileName, json, ct).ConfigureAwait(false)
                : await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto", ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>80% 활용(가중치 내림차순 · 안정 정렬) + 20% 탐색(상위 2개 밖 최소 상호작용 카드 → 2번째 슬롯).</summary>
    public IReadOnlyList<PlutoTrend> Rank(IReadOnlyList<PlutoTrend> trends)
    {
        ArgumentNullException.ThrowIfNull(trends);
        var n = trends.Count;
        var list = new List<PlutoTrend>(n);
        for (var i = 0; i < n; i++) list.Add(trends[i]);
        if (n < 2) return list;

        var weights = new double[n];
        var counts = new int[n];
        var idx = new int[n];
        lock (_sync)
        {
            for (var i = 0; i < n; i++)
            {
                idx[i] = i;
                if (_entries.TryGetValue(list[i].Id, out var e)) { weights[i] = e.Weight; counts[i] = e.Interactions; }
            }
        }
        Array.Sort(idx, (a, b) => { var c = weights[b].CompareTo(weights[a]); return c != 0 ? c : a.CompareTo(b); });
        var ranked = new List<PlutoTrend>(n);
        for (var i = 0; i < n; i++) ranked.Add(list[idx[i]]);

        double roll;
        lock (_rng) roll = _rng.NextDouble();
        if (roll < ExplorationRate)
        {
            var pick = -1;   // 상위 2개 밖에서 가장 덜 노출된(신선한) 카드
            for (var i = 2; i < n; i++)
                if (pick < 0 || counts[idx[i]] < counts[idx[pick]]) pick = i;
            if (pick >= 2)
            {
                var t = ranked[pick];
                ranked.RemoveAt(pick);
                ranked.Insert(1, t);
            }
        }
        return ranked;
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
