using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>
/// 🛡️ Pluto 오답 노트 — 👎/소프트 스킵 패턴 토큰(바이럴 태그 · 카메라 앵글 · BGM 무드)에 벌점 누적 → 14일 반감기 감쇠 →
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

    /// <summary>상태 변경 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;
}

/// <summary>
/// 🛡️ <see cref="IPlutoMistakeNotebookService"/> 구현 — <c>pluto_mistake_notebook.json</c>(☁️ Drive 볼트 · 로컬 폴백 · 원자 기록).
/// 유효 벌점 P_eff(t) = P_raw × 2^(−Δt_days / 14). 원시 벌점은 갱신 시점에 감쇠 반영 후 재기준(LastUtc = now).
/// </summary>
public sealed class PlutoMistakeNotebookService : IPlutoMistakeNotebookService, IDisposable
{
    public const string FileName = "pluto_mistake_notebook.json";
    public const double HalfLifeDays = 14.0;
    public const double BlockThreshold = 1.8;
    public const double ReleaseThreshold = 0.8;
    public const double DislikePenalty = 1.0;
    public const double SoftSkipPenalty = 0.4;
    public const double DefaultRelief = 1.2;
    public const string ConstraintPrefix = "Avoid motifs/styles: ";
    /// <summary>토큰 최대 길이(초과 → 절단) — 노트 비대화 방지.</summary>
    public const int MaxTokenLength = 120;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _path;
    private readonly IPlutoStorageManager? _storage;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _loaded;
    private volatile bool _readOnly;   // 🛡️ 적재 불가(잠금·권한) 세션 — 기존 파일 덮어쓰기 금지
    private int _disposed;

    /// <summary>☁️ <see cref="IPlutoStorageManager"/> 경유 — <c>G:\내 드라이브\DATA\Pluto</c> 우선 · 로컬 폴백.</summary>
    public PlutoMistakeNotebookService(IPlutoStorageManager storage, Func<DateTime>? utcNow = null) : this((string?)null, utcNow)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoMistakeNotebookService(string? storagePath, Func<DateTime>? utcNow = null)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", FileName)
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
    }

    /// <summary>영속 스냅샷(버전 필드로 향후 스키마 확장).</summary>
    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, Entry>? Entries { get; set; }
    }

    /// <summary>P_eff = P_raw × 2^(−Δt_days / 14) — 음수 Δt(시계 역행) → 감쇠 없음.</summary>
    public static double Decay(double raw, DateTime lastUtc, DateTime nowUtc)
    {
        if (!(raw > 0) || !double.IsFinite(raw)) return 0;
        var days = (nowUtc - lastUtc).TotalDays;
        if (!(days > 0)) return raw;
        return raw * Math.Pow(2.0, -days / HalfLifeDays);
    }

    /// <summary>슈미트 트리거 — 해제 상태는 ≥ 차단 임계에서만 차단 · 차단 상태는 &lt; 해제 임계에서만 해제.</summary>
    public static bool NextSuppressedState(bool current, double effective)
        => current ? effective >= ReleaseThreshold : effective >= BlockThreshold;

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.Mistake", ct).ConfigureAwait(false);
            _readOnly = status == PlutoLoadStatus.Unreadable;
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
            lock (_sync) _entries = map;
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    public async Task RecordNegativeFeedbackAsync(IEnumerable<string> tokens, double penaltyWeight = DislikePenalty, string? reason = null, CancellationToken ct = default)
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
                var eff = Decay(e.RawPenalty, e.LastUtc, now) + penaltyWeight;
                e.RawPenalty = eff;
                e.LastUtc = now;
                e.Hits++;
                if (reason is { Length: > 0 }) e.LastReason = reason.Length > MaxTokenLength ? reason[..MaxTokenLength] : reason;
                e.IsSuppressed = NextSuppressedState(e.IsSuppressed, eff);
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
                var eff = Math.Max(0.0, Decay(e.RawPenalty, e.LastUtc, now) - reliefWeight);
                e.IsSuppressed = NextSuppressedState(e.IsSuppressed, eff);
                if (eff <= 0 && !e.IsSuppressed) _entries.Remove(key);   // 완전 상쇄 → 노트에서 삭제
                else { e.RawPenalty = eff; e.LastUtc = now; }
                changed++;
            }
        }
        if (changed == 0) return;
        await PersistAsync(ct).ConfigureAwait(false);
        RaiseChanged();
    }

    public double GetEffectivePenalty(string token)
    {
        var key = Normalize(token);
        if (key is null) return 0;
        lock (_sync) return _entries.TryGetValue(key, out var e) ? Decay(e.RawPenalty, e.LastUtc, _utcNow()) : 0;
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

    public IReadOnlySet<string> GetSuppressedTokens()
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
                if (e.IsSuppressed) set.Add(e.Label);
            }
        }
        return set;
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
                if (e.IsSuppressed) list.Add((e.Label, eff));
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
