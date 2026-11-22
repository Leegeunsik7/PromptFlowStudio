using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🛡️ 오답 노트 활성 가드 스냅샷 — 패턴 키 · 실패 차원 · 한국어 진단 사유 · 현재 유효 벌점 · 최근 기록 시각.</summary>
public sealed record PlutoMistakeRuleSnapshot(string PatternKey, string FailureDimension, string DiagnosticReasonKorean, double PenaltyWeight, DateTimeOffset RecordedAtUtc)
{
    /// <summary>🩻 Step 28 — 자동 사후 분석(기획안 실측 미달) 출처 여부.</summary>
    public bool IsPostMortem { get; init; }
    /// <summary>⏳ 이 규칙에 적용되는 반감기(일) — 사후 분석 30 · 그 밖 21.</summary>
    public double HalfLifeDays { get; init; } = PlutoMistakeNotebookService.HalfLifeDays;
    /// <summary>⏳ 재범 없을 때 자연 해제(&lt; 해제 임계)까지 남은 일수.</summary>
    public double DaysUntilRelease { get; init; }
}

/// <summary>🌱 Step 28 — 유기적 복권 결과(감쇠된 조합 키 · 클립 중복/인과 위반으로 건너뛴 키 수).</summary>
public sealed record PlutoRehabilitationResult(IReadOnlyList<string> AttenuatedKeys, int SkippedDuplicate, int SkippedCausality)
{
    public static readonly PlutoRehabilitationResult None = new(Array.Empty<string>(), 0, 0);
    public bool Any => AttenuatedKeys.Count > 0;
}

/// <summary>🌱 Step 29 — 복권 장부 ↔ 오답 노트 조정 결과(재생 · 세대 가드 · 포화 가드 · 레거시 역충전 수).</summary>
public sealed record PlutoLedgerReconcileResult(int Replayed, int SkippedEpoch, int SkippedSaturated, int Backfilled)
{
    public static readonly PlutoLedgerReconcileResult None = new(0, 0, 0, 0);
    /// <summary>엔트리 또는 장부가 바뀜(영속 필요).</summary>
    public bool Changed => Replayed + Backfilled > 0;
}

/// <summary>
/// 🛡️ Pluto 오답 노트 — 👎/소프트 스킵 패턴 토큰(바이럴 태그 · 카메라 앵글 · BGM 무드)에 벌점 누적 → 21일 반감기 감쇠 →
/// 슈미트 트리거 히스테리시스(차단 ≥ 1.8 · 해제 &lt; 0.8)로 자율 회피. 👍/채택은 벌점 상쇄.
/// </summary>
public interface IPlutoMistakeNotebookService
{
    /// <summary>영속 노트 선적재(멱등).</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>👎 명시적 비선호 = 1.0 · 소프트 스킵/즉시 재생성 = 0.4 벌점 가산(호출 내 중복 토큰 1회).</summary>
    Task RecordNegativeFeedbackAsync(IEnumerable<string> tokens, double penaltyWeight = 1.0, string? reason = null, CancellationToken ct = default);

    /// <summary>👍 / 스튜디오 적용 → 원시 벌점 차감(하한 0).</summary>
    Task RecordPositiveReliefAsync(IEnumerable<string> tokens, double reliefWeight = 1.2, CancellationToken ct = default);

    /// <summary>현재 시각 기준 유효 벌점(미추적 → 0).</summary>
    double GetEffectivePenalty(string token);

    /// <summary>차단 중 토큰(대소문자 무시 집합) — 조회 시 감쇠로 해제 임계 미만이면 즉시 해제.</summary>
    IReadOnlySet<string> GetSuppressedTokens();

    /// <summary>LLM 부정 제약 1줄(예: <c>Avoid motifs/styles: a, b</c>) — 차단 없음 → 빈 문자열.</summary>
    string BuildNegativeConstraintPrompt(int maxTokens = 3);

    /// <summary>오답 노트 전체 초기화 + 영속.</summary>
    Task ClearAllAsync(CancellationToken ct = default);

    /// <summary>🩺 구조화 진단 벌점 — <paramref name="failureDimension"/>(<c>HookRetention</c> · <c>CompletionDrop</c> · <c>LowCtr</c> · <c>GeneralPenalty</c>)과 한국어 사유를 함께 영속.</summary>
    Task RecordDiagnosedMistakeAsync(IEnumerable<string> tokens, double penaltyWeight, string failureDimension, string? reason = null, CancellationToken ct = default)
        => RecordNegativeFeedbackAsync(tokens, penaltyWeight, reason, ct);

    /// <summary>🛡️ 활성 가드(차단 중 패턴) 최신순 · 최대 <paramref name="maxCount"/>개.</summary>
    IReadOnlyList<PlutoMistakeRuleSnapshot> GetActiveMistakeRules(int maxCount = 30) => Array.Empty<PlutoMistakeRuleSnapshot>();

    /// <summary>🕊️ 오탐 용서 — 패턴 규칙 제거 + 원자 영속(제거됨 → true · 미추적 → false).</summary>
    Task<bool> RemoveMistakeRuleAsync(string patternKey, CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>⏳ 지정 시각(<paramref name="asOfUtc"/>) 기준 유효 벌점 — 재현 가능한 감쇠 평가(기본 구현 → 현재 시각).</summary>
    double GetEffectivePenalty(string token, DateTime asOfUtc) => GetEffectivePenalty(token);

    /// <summary>🔗 차단 중 조합 키(<c>pair:</c> · <c>domain_hook:</c>) — 단일 특성 차단 집합(<see cref="GetSuppressedTokens"/>)과 분리.</summary>
    IReadOnlySet<string> GetSuppressedCompositeKeys() => PlutoMistakeDecayStatus.NoKeys;

    /// <summary>⏳ 시간 감쇠 현황(추적 · 가드 · 회복 중 · 감쇠율 · 다음 자연 해제 예상).</summary>
    PlutoMistakeDecayStatus GetDecayStatus() => PlutoMistakeDecayStatus.Empty;

    /// <summary>🩻 Step 28 — 자동 사후 분석 벌점(30일 반감기 출처 표식 · 기본 구현 → 구조화 진단 벌점).</summary>
    Task RecordPostMortemMistakeAsync(IEnumerable<string> tokens, double penaltyWeight, string failureDimension, string? reason = null, CancellationToken ct = default)
        => RecordDiagnosedMistakeAsync(tokens, penaltyWeight, failureDimension, reason, ct);

    /// <summary>
    /// 🌱 Step 28 — 유기적 복권: 벌점 조합 키(<c>pair:</c> · <c>domain_hook:</c>)를 쓴 클립이 기대 대비 ≥ <see cref="PlutoMistakeNotebookService.RehabilitationDeltaPoints"/>p 초과 달성 →
    /// 유효 벌점 × <see cref="PlutoMistakeNotebookService.RehabilitationFactor"/>(클립당 1회 · 벌점 기록 이후 렌더된 클립만). 기본 구현 = 미지원.
    /// </summary>
    Task<PlutoRehabilitationResult> RecordOrganicRecoveryAsync(IEnumerable<string> compositeKeys, string clipId, double surpriseDelta, DateTime clipRenderedUtc, CancellationToken ct = default)
        => Task.FromResult(PlutoRehabilitationResult.None);

    /// <summary>상태 변경 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// ⏳ 오답 노트 시간 감쇠 현황 — 추적 토큰 · 활성 가드(조합 키 포함) · 조합 가드 · 회복 중(벌점 잔존 · 비차단) ·
/// 감쇠로 소멸한 벌점 비율(%) · 반감기 · 가장 가까운 자연 해제까지 남은 일수(가드 없음 → null).
/// </summary>
public sealed record PlutoMistakeDecayStatus(int TrackedCount, int ActiveGuardCount, int CompositeGuardCount, int RecoveringCount, double DecayedAwayPct, double HalfLifeDays, double? DaysUntilNextRelease)
{
    public static readonly PlutoMistakeDecayStatus Empty = new(0, 0, 0, 0, 0.0, PlutoMistakeNotebookService.HalfLifeDays, null);
    internal static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// 🛡️ <see cref="IPlutoMistakeNotebookService"/> 구현 — <c>pluto_mistake_notebook.json</c>(☁️ Drive 볼트 · 로컬 폴백 · 원자 기록).
/// 유효 벌점 P_eff(t) = P_raw × 0.5^(Δt_days / 21). 원시 벌점은 갱신 시점에 감쇠 반영 후 재기준(LastUtc = now).
/// 조합 키(<c>pair:</c> · <c>domain_hook:</c>)는 같은 감쇠·히스테리시스를 따르되 단일 특성 차단 집합 · LLM 부정 제약에는 노출하지 않는다.
/// </summary>
public sealed class PlutoMistakeNotebookService : IPlutoMistakeNotebookService, IPlutoHotReloadable, IDisposable
{
    public const string FileName = "pluto_mistake_notebook.json";
    /// <summary>⏳ 벌점 반감기(일) — 재범 없으면 21일마다 절반으로 감쇠해 점진 복권.</summary>
    public const double HalfLifeDays = 21.0;
    /// <summary>⏳ Step 28 — 자동 사후 분석 벌점 반감기(일).</summary>
    public const double PostMortemHalfLifeDays = 30.0;
    /// <summary>🌱 유기적 복권 최소 SurpriseDelta(포인트).</summary>
    public const double RehabilitationDeltaPoints = 8.0;
    /// <summary>🌱 복권 1회당 유효 벌점 배수(2.0 → 0.7 &lt; 해제 임계 0.8 → 갓 생긴 사후 분석 차단도 1회로 해제).</summary>
    public const double RehabilitationFactor = 0.35;
    /// <summary>🌱 엔트리당 복권 클립 ID 보관 상한(FIFO).</summary>
    public const int MaxRehabilitatedClipIds = 32;
    /// <summary>엔트리 출처 — 자동 사후 분석 · 피드백(👎 · 스킵 · 진단).</summary>
    public const string OriginPostMortem = "postmortem", OriginFeedback = "feedback";
    /// <summary>Step 27 사후 분석 사유 접두(레거시 출처 추론 — <see cref="PlutoBriefOutcomeAttributionService.PostMortemReasonPrefix"/>와 동일).</summary>
    public const string LegacyPostMortemReasonPrefix = "brief-postmortem: ";
    /// <summary>회복 중 판정 최소 유효 벌점(미만 → 사실상 소멸).</summary>
    public const double RecoveringFloor = 0.05;
    public const double BlockThreshold = 1.8;
    public const double ReleaseThreshold = 0.8;
    public const double DislikePenalty = 1.0;
    public const double SoftSkipPenalty = 0.4;
    public const double DefaultRelief = 1.2;
    public const string ConstraintPrefix = "Avoid motifs/styles: ";
    /// <summary>토큰 최대 길이(초과 → 절단) — 노트 비대화 방지.</summary>
    public const int MaxTokenLength = 120;

    /// <summary>🩺 실패 차원 키.</summary>
    public const string DimensionHookRetention = "HookRetention", DimensionCompletionDrop = "CompletionDrop", DimensionLowCtr = "LowCtr", DimensionGeneral = "GeneralPenalty";
    public const string ReasonHookRetention = "초반 3초 훅 이탈 (유지율 기준 미달)";
    public const string ReasonCompletionDrop = "중반 전개 이탈 (완주율 기준 미달)";
    public const string ReasonLowCtr = "썸네일·제목 클릭 유인 부족 (CTR 미달)";
    public const string ReasonGeneral = "일반 비선호 벌점 (👎·스킵 누적)";

    /// <summary>차원 키 정규화(대소문자 무시 · 미지 → <see cref="DimensionGeneral"/>).</summary>
    public static string NormalizeDimension(string? dimension)
    {
        if (string.Equals(dimension, DimensionHookRetention, StringComparison.OrdinalIgnoreCase)) return DimensionHookRetention;
        if (string.Equals(dimension, DimensionCompletionDrop, StringComparison.OrdinalIgnoreCase)) return DimensionCompletionDrop;
        if (string.Equals(dimension, DimensionLowCtr, StringComparison.OrdinalIgnoreCase)) return DimensionLowCtr;
        return DimensionGeneral;
    }

    /// <summary>차원 → 한국어 진단 사유.</summary>
    public static string DiagnosticReasonFor(string? dimension) => NormalizeDimension(dimension) switch
    {
        DimensionHookRetention => ReasonHookRetention,
        DimensionCompletionDrop => ReasonCompletionDrop,
        DimensionLowCtr => ReasonLowCtr,
        _ => ReasonGeneral,
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly IPlutoStorageManager? _storage;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _loaded;
    private volatile bool _readOnly;   // 🛡️ 적재 불가(잠금·권한) 세션 — 기존 파일 덮어쓰기 금지
    private volatile bool _hasUnappliedDiskUpdate;   // 🚧 Step 21 — 잠긴 재적재 이후 기록 펜스(_gate 안에서만 전환)
    private int _disposed;

    /// <summary>☁️ <see cref="IPlutoStorageManager"/> 경유 — <c>G:\내 드라이브\DATA\Pluto</c> 우선 · 로컬 폴백.</summary>
    public PlutoMistakeNotebookService(IPlutoStorageManager storage, Func<DateTime>? utcNow = null) : this((string?)null, utcNow)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoMistakeNotebookService(string? storagePath, Func<DateTime>? utcNow = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", FileName)
            : storagePath;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    public string StoragePath => _storage?.ResolveDataPath(FileName) ?? _path;

    public event Action? Changed;

    /// <summary>직렬화 엔트리 — 원시 벌점 · 기준 시각 · 히스테리시스 상태.</summary>
    public sealed class Entry
    {
        public string Label { get; set; } = string.Empty;
        public double RawPenalty { get; set; }
        public DateTime LastUtc { get; set; }
        public bool IsSuppressed { get; set; }
        public int Hits { get; set; }
        public string? LastReason { get; set; }
        /// <summary>🩺 실패 차원(구버전 파일 → null = 일반 벌점).</summary>
        public string? FailureDimension { get; set; }
        /// <summary>🩺 한국어 진단 사유(구버전 파일 → null).</summary>
        public string? DiagnosticReason { get; set; }
        /// <summary>🩻 Step 28 출처(<see cref="OriginPostMortem"/> · <see cref="OriginFeedback"/> · 구버전 → null = 사유 접두로 추론).</summary>
        public string? Origin { get; set; }
        /// <summary>🌱 마지막 벌점 가산 시각(인과 가드 · 구버전 → null = <see cref="LastUtc"/>).</summary>
        public DateTime? PenalizedAtUtc { get; set; }
        /// <summary>🌱 이미 복권에 반영한 클립 ID(클립당 1회).</summary>
        public List<string>? RehabilitatedClipIds { get; set; }
    }

    /// <summary>🩻 자동 사후 분석 엔트리 — 명시 출처 우선 · 레거시(출처 없음)는 조합 키 + Step 27 사유 접두로 추론.</summary>
    public static bool IsAutomatedPostMortem(Entry e)
    {
        if (e is null) return false;
        if (e.Origin is { Length: > 0 } o) return string.Equals(o, OriginPostMortem, StringComparison.Ordinal);
        return PlutoPairAttribution.IsCompositeKey(e.Label) && e.LastReason is { } r && r.StartsWith(LegacyPostMortemReasonPrefix, StringComparison.Ordinal);
    }

    /// <summary>⏳ 엔트리별 반감기(사후 분석 30일 · 그 밖 21일).</summary>
    public static double HalfLifeOf(Entry e) => IsAutomatedPostMortem(e) ? PostMortemHalfLifeDays : HalfLifeDays;

    /// <summary>엔트리 유효 벌점(엔트리별 반감기).</summary>
    public static double DecayOf(Entry e, DateTime nowUtc) => Decay(e.RawPenalty, e.LastUtc, nowUtc, HalfLifeOf(e));

    /// <summary>⏳ 유효 벌점 → 해제 임계 미만까지 남은 일수(이미 미만 → 0).</summary>
    public static double DaysUntilRelease(double effective, double halfLifeDays)
        => effective > ReleaseThreshold && double.IsFinite(effective) ? Math.Max(0.0, halfLifeDays * Math.Log2(effective / ReleaseThreshold)) : 0.0;

    /// <summary>영속 스냅샷(버전 필드로 향후 스키마 확장).</summary>
    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Entry>? Entries { get; set; }
    }

    /// <summary>P_eff = P_raw × 0.5^(Δt_days / 21) — 음수 Δt(시계 역행) → 감쇠 없음.</summary>
    public static double Decay(double raw, DateTime lastUtc, DateTime nowUtc) => Decay(raw, lastUtc, nowUtc, HalfLifeDays);

    /// <summary>P_eff = P_raw × 0.5^(Δt_days / halfLife) — 반감기 비정상 → 기본 21일.</summary>
    public static double Decay(double raw, DateTime lastUtc, DateTime nowUtc, double halfLifeDays)
    {
        if (!(raw > 0) || !double.IsFinite(raw)) return 0;
        var days = (nowUtc - lastUtc).TotalDays;
        if (!(days > 0)) return raw;
        var hl = double.IsFinite(halfLifeDays) && halfLifeDays > 0 ? halfLifeDays : HalfLifeDays;
        return raw * Math.Pow(0.5, days / hl);
    }

    /// <summary>슈미트 트리거 — 해제 상태는 ≥ 차단 임계에서만 차단 · 차단 상태는 &lt; 해제 임계에서만 해제.</summary>
    public static bool NextSuppressedState(bool current, double effective)
        => current ? effective >= ReleaseThreshold : effective >= BlockThreshold;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded && !_hasUnappliedDiskUpdate) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_hasUnappliedDiskUpdate) { await ReloadCoreAsync(ct).ConfigureAwait(false); if (_loaded) return; }   // 🚧 변경 적용 전 병합본 재적재
            if (_loaded) return;
            var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.Mistake", ct).ConfigureAwait(false);
            _readOnly = status == PlutoLoadStatus.Unreadable;
            var map = BuildMap(data);
            lock (_sync) _entries = map;
            await LoadLedgerAndReconcileCoreAsync(ct).ConfigureAwait(false);   // 🌱 Step 29 — 다른 PC 복권 재생
            _loaded = true;
        }
        finally { _gate.Release(); }
        await FlushReconcileAsync(ct).ConfigureAwait(false);
    }

    // ── 🌱 Step 29 — 복권 장부(동반 동기화 · 합집합) ──

    private Dictionary<string, PlutoRehabilitationLedger.Record> _ledger = new(StringComparer.OrdinalIgnoreCase);   // _sync 보호
    private volatile bool _ledgerReadOnly;   // 장부 잠김(적재 불가) → 덮어쓰기 금지
    private int _reconcileDirty;             // 재생/역충전 후 미영속
    private PlutoLedgerReconcileResult _lastReconcile = PlutoLedgerReconcileResult.None;

    /// <summary>복권 장부 경로 — Drive 볼트 동반 파일 · 경로 지정 생성자는 노트 옆 <c>{stem}_rehab_ledger.json</c>(기본 파일명이면 <see cref="PlutoRehabilitationLedger.FileName"/>).</summary>
    public string LedgerStoragePath
    {
        get
        {
            if (_storage is not null) return _storage.ResolveDataPath(PlutoRehabilitationLedger.FileName);
            var stem = Path.GetFileNameWithoutExtension(_path);
            var name = string.Equals(Path.GetFileName(_path), FileName, StringComparison.OrdinalIgnoreCase) ? PlutoRehabilitationLedger.FileName : stem + "_rehab_ledger.json";
            return Path.Combine(Path.GetDirectoryName(_path) ?? string.Empty, name);
        }
    }

    /// <summary>마지막 적재/재적재 장부 조정 결과(진단 · 테스트).</summary>
    public PlutoLedgerReconcileResult LastLedgerReconcile => Volatile.Read(ref _lastReconcile);

    /// <summary>복권 장부 기록 수.</summary>
    public int LedgerCount
    {
        get { lock (_sync) return _ledger.Count; }
    }

    /// <summary>(키, 클립) 복권이 장부에 있는지(대소문자 무시).</summary>
    public bool LedgerContains(string compositeKey, string clipId)
    {
        if (Normalize(compositeKey) is not { } k || Normalize(clipId) is not { } c) return false;
        lock (_sync) return _ledger.ContainsKey(PlutoRehabilitationLedger.IdOf(k, c));
    }

    /// <summary>장부 스냅샷(결정론 순서).</summary>
    public PlutoRehabilitationLedger.Snapshot GetLedgerSnapshot()
    {
        List<PlutoRehabilitationLedger.Record> recs;
        lock (_sync) recs = new List<PlutoRehabilitationLedger.Record>(_ledger.Values);
        return PlutoRehabilitationLedger.Merge(new[] { new PlutoRehabilitationLedger.Snapshot { Records = recs } });
    }

    /// <summary>
    /// (<see cref="_gate"/> 보유) 대기 병합 승격 → 디스크 장부 ∪ 메모리 장부(성장 전용 · 기록 실패분 보존) → 조정.
    /// 장부 잠김 → 메모리 장부 유지 + 이번 세션 장부 덮어쓰기 금지.
    /// </summary>
    private async Task LoadLedgerAndReconcileCoreAsync(CancellationToken ct)
    {
        PlutoHotReload.PromotePending(_storage, PlutoRehabilitationLedger.FileName);
        var (disk, status) = await PlutoJsonStore.TryLoadAsync<PlutoRehabilitationLedger.Snapshot>(LedgerStoragePath, "Pluto.RehabLedger", ct).ConfigureAwait(false);
        _ledgerReadOnly = status == PlutoLoadStatus.Unreadable;
        lock (_sync)
        {
            var merged = PlutoRehabilitationLedger.Merge(new[] { disk, new PlutoRehabilitationLedger.Snapshot { Records = new List<PlutoRehabilitationLedger.Record>(_ledger.Values) } });
            var map = new Dictionary<string, PlutoRehabilitationLedger.Record>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in merged.Records!) map[PlutoRehabilitationLedger.IdOf(r.Key, r.ClipId)] = r;
            _ledger = map;
            var result = ReconcileLocked(_utcNow());
            Volatile.Write(ref _lastReconcile, result);
            if (result.Changed) Volatile.Write(ref _reconcileDirty, 1);
        }
    }

    /// <summary>
    /// 🔀 장부 ↔ 엔트리 조정(<see cref="_sync"/> 보유) —
    ///   ① 재생: 장부에 있고 엔트리 목록에 없는 (키, 클립) → × 0.35 1회(같은 PC 계보의 원시 벌점과 목록은 한 쌍이므로 '목록 부재 = 미반영').
    ///   ② ⏱️ 세대 가드: 클립 렌더 ≤ 엔트리 현재 벌점 시각(다른 PC 의 새 벌점이 이김) · 렌더 시각 미상 → 재생 안 함.
    ///   ③ 🧱 포화 가드: 엔트리 목록이 FIFO 상한에 닿음 → '목록 부재'가 미반영을 증명하지 못함 → 재생 안 함.
    ///   ④ 역충전: 엔트리 목록에만 있는(레거시 Step 28) 클립 → 장부에 기록(렌더 시각 미상).
    /// </summary>
    private PlutoLedgerReconcileResult ReconcileLocked(DateTime now)
    {
        int replayed = 0, epoch = 0, saturated = 0, backfilled = 0;
        List<PlutoRehabilitationLedger.Record>? sorted = null;
        if (_ledger.Count > 0)
        {
            sorted = new List<PlutoRehabilitationLedger.Record>(_ledger.Values);
            sorted.Sort(static (x, y) => { var c = x.AppliedAtUtc.CompareTo(y.AppliedAtUtc); return c != 0 ? c : string.CompareOrdinal(PlutoRehabilitationLedger.IdOf(x.Key, x.ClipId), PlutoRehabilitationLedger.IdOf(y.Key, y.ClipId)); });
        }
        if (sorted is not null)
            foreach (var r in sorted)
            {
                if (!_entries.TryGetValue(r.Key, out var e) || ContainsClip(e.RehabilitatedClipIds, r.ClipId)) continue;
                if ((e.RehabilitatedClipIds?.Count ?? 0) >= MaxRehabilitatedClipIds) { saturated++; continue; }
                if (r.ClipRenderedUtc == default || r.ClipRenderedUtc <= (e.PenalizedAtUtc ?? e.LastUtc)) { epoch++; continue; }
                var decayed = DecayOf(e, now);
                if (decayed < RecoveringFloor) continue;
                ApplyRehabilitationLocked(e, decayed, r.ClipId, now);
                replayed++;
            }
        foreach (var e in _entries.Values)
        {
            if (e.RehabilitatedClipIds is not { Count: > 0 } ids || Normalize(e.Label) is not { } label) continue;
            foreach (var clip in ids)
            {
                if (Normalize(clip) is not { } c) continue;
                var id = PlutoRehabilitationLedger.IdOf(label, c);
                if (_ledger.ContainsKey(id)) continue;
                _ledger[id] = new PlutoRehabilitationLedger.Record { Key = label, ClipId = c, AppliedAtUtc = e.LastUtc };
                backfilled++;
            }
        }
        return replayed + epoch + saturated + backfilled == 0 ? PlutoLedgerReconcileResult.None : new PlutoLedgerReconcileResult(replayed, epoch, saturated, backfilled);
    }

    /// <summary>복권 1회 적용(<see cref="_sync"/> 보유) — 히스테리시스 정산 → × 0.35 → 목록 FIFO.</summary>
    private static void ApplyRehabilitationLocked(Entry e, double decayed, string clip, DateTime now)
    {
        var eff = decayed * RehabilitationFactor;
        e.IsSuppressed = NextSuppressedState(NextSuppressedState(e.IsSuppressed, decayed), eff);
        e.RawPenalty = eff;
        e.LastUtc = now;
        var ids = e.RehabilitatedClipIds ??= new List<string>(2);
        ids.Add(clip);
        if (ids.Count > MaxRehabilitatedClipIds) ids.RemoveRange(0, ids.Count - MaxRehabilitatedClipIds);
    }

    /// <summary>조정 결과 영속(게이트 밖) — 노트 → 장부 순(중간 실패해도 다음 적재의 역충전/재생이 수렴).</summary>
    private async Task FlushReconcileAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _reconcileDirty, 0) == 0) return;
        await PersistAsync(ct).ConfigureAwait(false);
        await PersistLedgerAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>장부 원자 기록(실패/잠김 → false · [부분 구제] 메모리 장부 유지).</summary>
    private async Task<bool> PersistLedgerAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await WriteLedgerCoreAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>장부 기록 본체(호출자가 <see cref="_gate"/> 보유).</summary>
    private async Task<bool> WriteLedgerCoreAsync(CancellationToken ct)
    {
        try
        {
            if (_ledgerReadOnly) return false;
            string json;
            lock (_sync) json = JsonSerializer.Serialize(PlutoRehabilitationLedger.Merge(new[] { new PlutoRehabilitationLedger.Snapshot { Records = new List<PlutoRehabilitationLedger.Record>(_ledger.Values) } }), JsonOpts);
            return _storage is not null
                ? await _storage.WriteAllTextAtomicAsync(PlutoRehabilitationLedger.FileName, json, ct).ConfigureAwait(false)
                : await PlutoJsonStore.WriteAtomicAsync(LedgerStoragePath, json, "Pluto.RehabLedger", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.RehabLedger] persist: " + ex.Message); return false; }
    }

    private static Dictionary<string, Entry> BuildMap(Snapshot? data)
    {
        var map = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (data?.Entries is { } src)
            foreach (var kv in src)
            {
                var key = Normalize(kv.Key);
                if (key is null || kv.Value is not { } e || !double.IsFinite(e.RawPenalty) || e.RawPenalty < 0) continue;
                if (string.IsNullOrWhiteSpace(e.Label)) e.Label = key;
                if (e.LastUtc.Kind != DateTimeKind.Utc)
                    e.LastUtc = e.LastUtc.Kind == DateTimeKind.Local ? e.LastUtc.ToUniversalTime() : DateTime.SpecifyKind(e.LastUtc, DateTimeKind.Utc);
                map[key] = e;
            }
        return map;
    }

    // ── ♻️ Step 20 — 실행 중 재적재 ──

    public string HubFileName => FileName;

    /// <summary>♻️ 게이트 안에서 대기 해결 승격 → 파싱 → 원자 교체(손상 · 부재 · 잠김 · 읽기 전용 세션 → 라이브 노트 유지 + false).</summary>
    public async Task<bool> ReloadFromDiskAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readOnly) return false;   // 메모리에만 있는 세션 기록을 지우지 않는다
            PlutoHotReload.PromotePending(_storage, FileName);
            if (!await ReloadCoreAsync(ct).ConfigureAwait(false)) return false;
        }
        finally { _gate.Release(); }
        await FlushReconcileAsync(ct).ConfigureAwait(false);
        RaiseChanged();
        return true;
    }

    /// <summary>🚧 Step 21 재적재 대기 기록 펜스(잠긴 재적재 이후 낡은 메모리 덮어쓰기 차단).</summary>
    public bool HasUnappliedDiskUpdate => _hasUnappliedDiskUpdate;

    /// <summary>♻️ 재적재 본체(호출자가 <see cref="_gate"/> 보유) — 잠김 → 펜스 설정 · 디스크 읽힘(정상/손상/부재) → 펜스 해제.</summary>
    private async Task<bool> ReloadCoreAsync(CancellationToken ct)
    {
        var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.Mistake.Reload", ct).ConfigureAwait(false);
        _hasUnappliedDiskUpdate = status == PlutoLoadStatus.Unreadable;
        if (status != PlutoLoadStatus.Ok || data is null) return false;
        var map = BuildMap(data);
        lock (_sync) _entries = map;
        await LoadLedgerAndReconcileCoreAsync(ct).ConfigureAwait(false);   // 🌱 병합본 노트 + 병합본 장부 → 재생
        _loaded = true;
        return true;
    }

    public Task RecordNegativeFeedbackAsync(IEnumerable<string> tokens, double penaltyWeight = DislikePenalty, string? reason = null, CancellationToken ct = default)
        => RecordCoreAsync(tokens, penaltyWeight, null, reason, OriginFeedback, ct);

    public Task RecordDiagnosedMistakeAsync(IEnumerable<string> tokens, double penaltyWeight, string failureDimension, string? reason = null, CancellationToken ct = default)
        => RecordCoreAsync(tokens, penaltyWeight, NormalizeDimension(failureDimension), reason, OriginFeedback, ct);

    public Task RecordPostMortemMistakeAsync(IEnumerable<string> tokens, double penaltyWeight, string failureDimension, string? reason = null, CancellationToken ct = default)
        => RecordCoreAsync(tokens, penaltyWeight, NormalizeDimension(failureDimension), reason, OriginPostMortem, ct);

    private async Task RecordCoreAsync(IEnumerable<string> tokens, double penaltyWeight, string? dimension, string? reason, string origin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (!double.IsFinite(penaltyWeight) || penaltyWeight <= 0) throw new ArgumentOutOfRangeException(nameof(penaltyWeight));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var changed = 0;
        lock (_sync)
        {
            var now = _utcNow();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in tokens)
            {
                var key = Normalize(raw);
                if (key is null || !seen.Add(key)) continue;
                if (!_entries.TryGetValue(key, out var e)) { e = new Entry { Label = key }; _entries[key] = e; }
                var decayed = DecayOf(e, now);   // ⏳ 기존 출처의 반감기로 재기준 → 그 뒤 출처 전환(유효 벌점 연속)
                var settled = NextSuppressedState(e.IsSuppressed, decayed);   // 🩹 감쇠로 이미 해제됐어야 할 낡은 래치를 먼저 정산(조회 유무와 무관한 히스테리시스)
                var eff = decayed + penaltyWeight;
                e.RawPenalty = eff;
                e.LastUtc = now;
                e.PenalizedAtUtc = now;
                e.Origin = origin;
                e.Hits++;
                if (reason is { Length: > 0 }) e.LastReason = reason.Length > MaxTokenLength ? reason[..MaxTokenLength] : reason;
                if (dimension is not null || e.FailureDimension is null)   // 일반 벌점은 기존 구조화 진단을 덮지 않음
                {
                    e.FailureDimension = dimension ?? DimensionGeneral;
                    e.DiagnosticReason = DiagnosticReasonFor(e.FailureDimension);
                }
                e.IsSuppressed = NextSuppressedState(settled, eff);
                changed++;
            }
        }
        if (changed == 0) return;
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task RecordPositiveReliefAsync(IEnumerable<string> tokens, double reliefWeight = DefaultRelief, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (!double.IsFinite(reliefWeight) || reliefWeight <= 0) throw new ArgumentOutOfRangeException(nameof(reliefWeight));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var changed = 0;
        lock (_sync)
        {
            var now = _utcNow();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in tokens)
            {
                var key = Normalize(raw);
                if (key is null || !seen.Add(key) || !_entries.TryGetValue(key, out var e)) continue;   // 미추적 토큰 → 상쇄 대상 없음
                var decayed = DecayOf(e, now);
                var eff = Math.Max(0.0, decayed - reliefWeight);
                e.IsSuppressed = NextSuppressedState(NextSuppressedState(e.IsSuppressed, decayed), eff);
                if (eff <= 0 && !e.IsSuppressed) _entries.Remove(key);   // 완전 상쇄 → 노트에서 삭제
                else { e.RawPenalty = eff; e.LastUtc = now; }
                changed++;
            }
        }
        if (changed == 0) return;
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    public double GetEffectivePenalty(string token) => GetEffectivePenalty(token, _utcNow());

    public double GetEffectivePenalty(string token, DateTime asOfUtc)
    {
        var key = Normalize(token);
        if (key is null) return 0;
        lock (_sync) return _entries.TryGetValue(key, out var e) ? DecayOf(e, asOfUtc) : 0;
    }

    /// <summary>토큰 차단 여부(감쇠 반영 · 해제 임계 미만 → 해제 전이).</summary>
    public bool IsSuppressed(string token)
    {
        var key = Normalize(token);
        if (key is null) return false;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var e) || !e.IsSuppressed) return false;
            e.IsSuppressed = NextSuppressedState(true, DecayOf(e, _utcNow()));
            return e.IsSuppressed;
        }
    }

    public IReadOnlySet<string> GetSuppressedTokens() => CollectSuppressed(composite: false);

    public IReadOnlySet<string> GetSuppressedCompositeKeys() => CollectSuppressed(composite: true);

    /// <summary>차단 집합 — 단일 특성 / 조합 키 분리(조합 키가 부분 일치 차단 · LLM 부정 제약에 새지 않도록).</summary>
    private HashSet<string> CollectSuppressed(bool composite)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_sync)
        {
            if (_entries.Count == 0) return set;
            var now = _utcNow();
            foreach (var kv in _entries)
            {
                var e = kv.Value;
                if (!e.IsSuppressed) continue;
                e.IsSuppressed = NextSuppressedState(true, DecayOf(e, now));   // 반감기 경과 → 자연 해제
                if (e.IsSuppressed && PlutoPairAttribution.IsCompositeKey(e.Label) == composite) set.Add(e.Label);
            }
        }
        return set;
    }

    public PlutoMistakeDecayStatus GetDecayStatus()
    {
        int tracked, guards = 0, composite = 0, recovering = 0;
        double raw = 0, effSum = 0;
        double? nextRelease = null;
        lock (_sync)
        {
            tracked = _entries.Count;
            if (tracked == 0) return PlutoMistakeDecayStatus.Empty;
            var now = _utcNow();
            foreach (var kv in _entries)
            {
                var e = kv.Value;
                var eff = DecayOf(e, now);
                if (double.IsFinite(e.RawPenalty) && e.RawPenalty > 0) { raw += e.RawPenalty; effSum += eff; }
                if (e.IsSuppressed) e.IsSuppressed = NextSuppressedState(true, eff);
                if (e.IsSuppressed)
                {
                    guards++;
                    if (PlutoPairAttribution.IsCompositeKey(e.Label)) composite++;
                    var days = DaysUntilRelease(eff, HalfLifeOf(e));   // eff × 0.5^(d / HL) = 해제 임계
                    if (nextRelease is null || days < nextRelease.Value) nextRelease = days;
                }
                else if (eff >= RecoveringFloor) recovering++;
            }
        }
        var decayedPct = raw > 0 ? Math.Clamp((1.0 - effSum / raw) * 100.0, 0.0, 100.0) : 0.0;
        return new PlutoMistakeDecayStatus(tracked, guards, composite, recovering, Math.Round(decayedPct, 1, MidpointRounding.AwayFromZero), HalfLifeDays,
            nextRelease is { } d ? Math.Round(d, 1, MidpointRounding.AwayFromZero) : null);
    }

    public string BuildNegativeConstraintPrompt(int maxTokens = 3)
    {
        if (maxTokens <= 0) return string.Empty;
        var list = new List<(string Label, double Eff)>(8);
        lock (_sync)
        {
            if (_entries.Count == 0) return string.Empty;
            var now = _utcNow();
            foreach (var kv in _entries)
            {
                var e = kv.Value;
                if (!e.IsSuppressed) continue;
                var eff = DecayOf(e, now);
                e.IsSuppressed = NextSuppressedState(true, eff);
                if (e.IsSuppressed && !PlutoPairAttribution.IsCompositeKey(e.Label)) list.Add((e.Label, eff));   // 조합 키는 LLM 제약 문구에서 제외
            }
        }
        if (list.Count == 0) return string.Empty;
        list.Sort(static (a, b) => { var c = b.Eff.CompareTo(a.Eff); return c != 0 ? c : string.CompareOrdinal(a.Label, b.Label); });
        var n = Math.Min(maxTokens, list.Count);
        var sb = new StringBuilder(ConstraintPrefix.Length + n * 24).Append(ConstraintPrefix);
        for (var i = 0; i < n; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(list[i].Label);
        }
        return sb.ToString();
    }

    public async Task ClearAllAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        lock (_sync) _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    public IReadOnlyList<PlutoMistakeRuleSnapshot> GetActiveMistakeRules(int maxCount = 30)
    {
        if (maxCount <= 0) return Array.Empty<PlutoMistakeRuleSnapshot>();
        var list = new List<(PlutoMistakeRuleSnapshot Rule, DateTime At)>(8);
        lock (_sync)
        {
            if (_entries.Count == 0) return Array.Empty<PlutoMistakeRuleSnapshot>();
            var now = _utcNow();
            foreach (var kv in _entries)
            {
                var e = kv.Value;
                if (!e.IsSuppressed) continue;
                var eff = DecayOf(e, now);
                e.IsSuppressed = NextSuppressedState(true, eff);
                if (!e.IsSuppressed) continue;
                var dim = NormalizeDimension(e.FailureDimension);
                var reason = string.IsNullOrWhiteSpace(e.DiagnosticReason) ? DiagnosticReasonFor(dim) : e.DiagnosticReason;
                var hl = HalfLifeOf(e);
                list.Add((new PlutoMistakeRuleSnapshot(e.Label, dim, reason, Math.Round(eff, 2, MidpointRounding.AwayFromZero), new DateTimeOffset(e.LastUtc, TimeSpan.Zero))
                {
                    IsPostMortem = IsAutomatedPostMortem(e), HalfLifeDays = hl, DaysUntilRelease = Math.Round(DaysUntilRelease(eff, hl), 1, MidpointRounding.AwayFromZero),
                }, e.LastUtc));
            }
        }
        list.Sort(static (a, b) => { var c = b.At.CompareTo(a.At); return c != 0 ? c : string.CompareOrdinal(a.Rule.PatternKey, b.Rule.PatternKey); });
        var n = Math.Min(maxCount, list.Count);
        var result = new PlutoMistakeRuleSnapshot[n];
        for (var i = 0; i < n; i++) result[i] = list[i].Rule;
        return result;
    }

    public async Task<PlutoRehabilitationResult> RecordOrganicRecoveryAsync(IEnumerable<string> compositeKeys, string clipId, double surpriseDelta, DateTime clipRenderedUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(compositeKeys);
        var clip = Normalize(clipId);
        if (clip is null || !double.IsFinite(surpriseDelta) || surpriseDelta < RehabilitationDeltaPoints) return PlutoRehabilitationResult.None;
        if (clipRenderedUtc.Kind != DateTimeKind.Utc)
            clipRenderedUtc = clipRenderedUtc.Kind == DateTimeKind.Local ? clipRenderedUtc.ToUniversalTime() : DateTime.SpecifyKind(clipRenderedUtc, DateTimeKind.Utc);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        List<string>? attenuated = null;
        int dup = 0, causal = 0;
        var ledgerChanged = false;
        lock (_sync)
        {
            var now = _utcNow();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in compositeKeys)
            {
                var key = Normalize(raw);
                if (key is null || !PlutoPairAttribution.IsCompositeKey(key) || !seen.Add(key) || !_entries.TryGetValue(key, out var e)) continue;   // 조합 키만 · 미추적 → 대상 없음
                var decayed = DecayOf(e, now);
                if (decayed < RecoveringFloor) continue;
                if (ContainsClip(e.RehabilitatedClipIds, clip)) { dup++; continue; }   // 🔁 클립당 1회
                var ledgerId = PlutoRehabilitationLedger.IdOf(key, clip);
                var inLedger = _ledger.ContainsKey(ledgerId);
                // 🧱 Step 29 — 목록이 FIFO 상한에 닿았는데 장부엔 있음 → 목록에서 밀려난 기반영 클립(재감쇠 금지)
                if (inLedger && (e.RehabilitatedClipIds?.Count ?? 0) >= MaxRehabilitatedClipIds) { dup++; continue; }
                if (clipRenderedUtc <= (e.PenalizedAtUtc ?? e.LastUtc)) { causal++; continue; }   // ⏱️ 벌점 이전에 렌더된 클립은 그 벌점을 반박하지 못함
                ApplyRehabilitationLocked(e, decayed, clip, now);
                if (!inLedger)
                {
                    _ledger[ledgerId] = new PlutoRehabilitationLedger.Record { Key = key, ClipId = clip, ClipRenderedUtc = clipRenderedUtc, AppliedAtUtc = now, SurpriseDelta = surpriseDelta };
                    ledgerChanged = true;
                }
                (attenuated ??= new List<string>(2)).Add(e.Label);
            }
        }
        if (attenuated is null) return dup + causal == 0 ? PlutoRehabilitationResult.None : new PlutoRehabilitationResult(Array.Empty<string>(), dup, causal);
        await PersistAsync(ct).ConfigureAwait(false);   // 실패 → [부분 구제] 메모리 복권 유지
        if (ledgerChanged) await PersistLedgerAsync(ct).ConfigureAwait(false);   // 🌱 Step 29 — 동반 장부(노트 다음 · 중간 실패 → 다음 적재 역충전)
        RaiseChanged();
        return new PlutoRehabilitationResult(attenuated, dup, causal);
    }

    private static bool ContainsClip(List<string>? ids, string clip)
    {
        if (ids is null) return false;
        for (var i = 0; i < ids.Count; i++) if (string.Equals(ids[i], clip, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public async Task<bool> RemoveMistakeRuleAsync(string patternKey, CancellationToken ct = default)
    {
        var key = Normalize(patternKey);
        if (key is null) return false;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        bool removed;
        lock (_sync) removed = _entries.Remove(key);
        if (!removed) return false;
        await PersistAsync(ct).ConfigureAwait(false);   // 실패 → [부분 구제] 메모리 해제 유지
        RaiseChanged();
        return true;
    }

    /// <summary>추적 중 토큰 수(진단용).</summary>
    public int TrackedCount
    {
        get { lock (_sync) return _entries.Count; }
    }

    /// <summary>공백 정리 · 빈 값 → null · 과도한 길이 절단.</summary>
    private static string? Normalize(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var t = token.Trim();
        return t.Length > MaxTokenLength ? t[..MaxTokenLength] : t;
    }

    /// <summary>게이트 획득 후 최신 상태 직렬화 → 원자적 기록(Lost-Update 방지). 실패/읽기 불가 → false([부분 구제] 메모리 상태 유지).</summary>
    private async Task<bool> PersistAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_readOnly) return false;
            if (_hasUnappliedDiskUpdate)   // 🚧 펜스 — 디스크 병합본 재적재 먼저 · 여전히 잠김 → 덮어쓰기 포기
            {
                if (await ReloadCoreAsync(ct).ConfigureAwait(false))
                {
                    if (Interlocked.Exchange(ref _reconcileDirty, 0) == 0) return true;   // 메모리 = 디스크 병합본(재기록 불필요)
                    await WriteLedgerCoreAsync(ct).ConfigureAwait(false);   // 🌱 병합 장부 재생분 → 노트와 함께 기록
                }
                if (_hasUnappliedDiskUpdate) { System.Diagnostics.Debug.WriteLine("[Pluto.Mistake] save aborted — write fence"); return false; }
            }
            string json;
            lock (_sync) json = JsonSerializer.Serialize(new Snapshot { Entries = _entries }, JsonOpts);
            return _storage is not null
                ? await _storage.WriteAllTextAtomicAsync(FileName, json, ct).ConfigureAwait(false)
                : await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.Mistake", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Mistake] persist: " + ex.Message); return false; }
        finally { _gate.Release(); }
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Mistake] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
