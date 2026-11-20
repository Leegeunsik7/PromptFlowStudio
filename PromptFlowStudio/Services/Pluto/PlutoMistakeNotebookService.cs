using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🛡️ 오답 노트 활성 가드 스냅샷 — 패턴 키 · 실패 차원 · 한국어 진단 사유 · 현재 유효 벌점 · 최근 기록 시각.</summary>
public sealed record PlutoMistakeRuleSnapshot(string PatternKey, string FailureDimension, string DiagnosticReasonKorean, double PenaltyWeight, DateTimeOffset RecordedAtUtc);

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
    }

    /// <summary>영속 스냅샷(버전 필드로 향후 스키마 확장).</summary>
    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Entry>? Entries { get; set; }
    }

    /// <summary>P_eff = P_raw × 0.5^(Δt_days / 21) — 음수 Δt(시계 역행) → 감쇠 없음.</summary>
    public static double Decay(double raw, DateTime lastUtc, DateTime nowUtc)
    {
        if (!(raw > 0) || !double.IsFinite(raw)) return 0;
        var days = (nowUtc - lastUtc).TotalDays;
        if (!(days > 0)) return raw;
        return raw * Math.Pow(0.5, days / HalfLifeDays);
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
            _loaded = true;
        }
        finally { _gate.Release(); }
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
        _loaded = true;
        return true;
    }

    public Task RecordNegativeFeedbackAsync(IEnumerable<string> tokens, double penaltyWeight = DislikePenalty, string? reason = null, CancellationToken ct = default)
        => RecordCoreAsync(tokens, penaltyWeight, null, reason, ct);

    public Task RecordDiagnosedMistakeAsync(IEnumerable<string> tokens, double penaltyWeight, string failureDimension, string? reason = null, CancellationToken ct = default)
        => RecordCoreAsync(tokens, penaltyWeight, NormalizeDimension(failureDimension), reason, ct);

    private async Task RecordCoreAsync(IEnumerable<string> tokens, double penaltyWeight, string? dimension, string? reason, CancellationToken ct)
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
                var decayed = Decay(e.RawPenalty, e.LastUtc, now);
                var settled = NextSuppressedState(e.IsSuppressed, decayed);   // 🩹 감쇠로 이미 해제됐어야 할 낡은 래치를 먼저 정산(조회 유무와 무관한 히스테리시스)
                var eff = decayed + penaltyWeight;
                e.RawPenalty = eff;
                e.LastUtc = now;
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
                var decayed = Decay(e.RawPenalty, e.LastUtc, now);
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
        lock (_sync) return _entries.TryGetValue(key, out var e) ? Decay(e.RawPenalty, e.LastUtc, asOfUtc) : 0;
    }

    /// <summary>토큰 차단 여부(감쇠 반영 · 해제 임계 미만 → 해제 전이).</summary>
    public bool IsSuppressed(string token)
    {
        var key = Normalize(token);
        if (key is null) return false;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var e) || !e.IsSuppressed) return false;
            e.IsSuppressed = NextSuppressedState(true, Decay(e.RawPenalty, e.LastUtc, _utcNow()));
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
                e.IsSuppressed = NextSuppressedState(true, Decay(e.RawPenalty, e.LastUtc, now));   // 반감기 경과 → 자연 해제
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
                var eff = Decay(e.RawPenalty, e.LastUtc, now);
                if (double.IsFinite(e.RawPenalty) && e.RawPenalty > 0) { raw += e.RawPenalty; effSum += eff; }
                if (e.IsSuppressed) e.IsSuppressed = NextSuppressedState(true, eff);
                if (e.IsSuppressed)
                {
                    guards++;
                    if (PlutoPairAttribution.IsCompositeKey(e.Label)) composite++;
                    var days = Math.Max(0.0, HalfLifeDays * Math.Log2(eff / ReleaseThreshold));   // eff × 0.5^(d / HL) = 해제 임계
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
                var eff = Decay(e.RawPenalty, e.LastUtc, now);
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
                var eff = Decay(e.RawPenalty, e.LastUtc, now);
                e.IsSuppressed = NextSuppressedState(true, eff);
                if (!e.IsSuppressed) continue;
                var dim = NormalizeDimension(e.FailureDimension);
                var reason = string.IsNullOrWhiteSpace(e.DiagnosticReason) ? DiagnosticReasonFor(dim) : e.DiagnosticReason;
                list.Add((new PlutoMistakeRuleSnapshot(e.Label, dim, reason, Math.Round(eff, 2, MidpointRounding.AwayFromZero), new DateTimeOffset(e.LastUtc, TimeSpan.Zero)), e.LastUtc));
            }
        }
        list.Sort(static (a, b) => { var c = b.At.CompareTo(a.At); return c != 0 ? c : string.CompareOrdinal(a.Rule.PatternKey, b.Rule.PatternKey); });
        var n = Math.Min(maxCount, list.Count);
        var result = new PlutoMistakeRuleSnapshot[n];
        for (var i = 0; i < n; i++) result[i] = list[i].Rule;
        return result;
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
                if (await ReloadCoreAsync(ct).ConfigureAwait(false)) return true;   // 메모리 = 디스크 병합본(재기록 불필요)
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
