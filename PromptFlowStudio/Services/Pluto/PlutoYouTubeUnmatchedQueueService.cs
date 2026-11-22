using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📌 Step 24 — 자동 연결에 실패한 YouTube Studio 행 1개(텔레메트리 원본 보존).</summary>
public sealed record PlutoUnmatchedYouTubeRow(YouTubeAnalyticsRow Row, string? Reason, string? SourceName, DateTimeOffset QueuedAtUtc)
{
    public string YouTubeVideoId => Row.YouTubeVideoId ?? string.Empty;
    public string Title => Row.Title ?? string.Empty;
}

/// <summary>📌 페어링 후보 1개(점수 = 제목 유사도 + 게시 창 보너스).</summary>
public sealed record PlutoUnmatchedPairSuggestion(PlutoRenderedClipRecord Clip, double Score);

/// <summary>📌 원클릭 페어링 결과 상태.</summary>
public enum PlutoUnmatchedPairStatus
{
    /// <summary>연결 + 학습 반영 완료(대기열에서 제거).</summary>
    Paired,
    /// <summary>대기열에 없음(이미 처리 · 더블 클릭).</summary>
    NotQueued,
    /// <summary>카탈로그에 클립 없음.</summary>
    ClipNotFound,
    /// <summary>클립이 이미 다른 YouTube ID에 연결됨.</summary>
    ClipBoundElsewhere,
    /// <summary>이 YouTube ID가 이미 다른 클립에 연결됨(자동 연결 선행) → 대기열에서 정리.</summary>
    AlreadyLinkedElsewhere,
    /// <summary>연결은 됐으나 학습 반영 실패 → 대기열 유지(재시도 시 멱등).</summary>
    LearnFailed,
}

/// <summary>📌 원클릭 페어링 결과.</summary>
public sealed record PlutoUnmatchedPairResult(PlutoUnmatchedPairStatus Status, PlutoYouTubeRowApplyResult? Applied, string MessageKorean)
{
    public bool Succeeded => Status == PlutoUnmatchedPairStatus.Paired;
}

/// <summary>📊 연결된 행 반영 갈래.</summary>
public enum PlutoYouTubeRowApplyKind
{
    /// <summary>최초 학습.</summary>
    Learned,
    /// <summary>성숙 증분 학습.</summary>
    Matured,
    /// <summary>기반영 · 덜 성숙 → 0건 강화.</summary>
    AlreadyRegistered,
}

/// <summary>📊 연결된 행 반영 결과.</summary>
public sealed record PlutoYouTubeRowApplyResult(PlutoYouTubeRowApplyKind Kind, bool GuardActivated, PlutoReinforcementOutcome? Outcome);

/// <summary>📌 Step 24 — 미매칭 YouTube 행 대기열 · 원클릭 카탈로그 페어링.</summary>
public interface IPlutoYouTubeUnmatchedQueueService
{
    /// <summary>대기 중 행 수.</summary>
    int Count { get; }

    /// <summary>대기열 변경 통지(백그라운드 스레드 가능).</summary>
    event Action? Changed;

    Task EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>최신 대기순 최대 <paramref name="maxCount"/>건.</summary>
    IReadOnlyList<PlutoUnmatchedYouTubeRow> GetPending(int maxCount = PlutoYouTubeUnmatchedQueueService.Capacity);

    /// <summary>
    /// 보존(YouTube ID 중복 제거 · 더 많이 조회된 스냅샷 우선 · 상한 <see cref="PlutoYouTubeUnmatchedQueueService.Capacity"/>건 · 가장 오래된 항목부터 축출).
    /// 반환 = 새로 들어오거나 갱신된 행 수.
    /// </summary>
    Task<int> EnqueueAsync(IEnumerable<PlutoUnmatchedYouTubeRow> rows, CancellationToken ct = default);

    /// <summary>🧹 다른 경로(자동 연결 · 다른 PC)로 이미 연결된 YouTube ID를 대기열에서 제거. 반환 = 제거 수.</summary>
    Task<int> ResolveLinkedAsync(IEnumerable<string> youTubeVideoIds, CancellationToken ct = default);

    /// <summary>페어링 후보(아직 YouTube ID 없는 최근 렌더 · 편 충돌 제외 · 점수순).</summary>
    Task<IReadOnlyList<PlutoUnmatchedPairSuggestion>> SuggestClipsAsync(string youTubeVideoId, int maxCount = 3, CancellationToken ct = default);

    /// <summary>원클릭 페어링 — 카탈로그에 YouTube ID 1:1 영속 → 폐루프 반영 → 대기열 제거.</summary>
    Task<PlutoUnmatchedPairResult> PairAsync(string youTubeVideoId, string clipVideoId, string platform = "YouTube Shorts", CancellationToken ct = default);

    /// <summary>사용자 무시(다른 채널 영상 등) — 제거됨 → true.</summary>
    Task<bool> DismissAsync(string youTubeVideoId, CancellationToken ct = default);

    /// <summary>
    /// 🔁 Step 25 — 멀티 PC 조정: 디스크 사본(☁️ Drive · 💾 로컬 미러 · <paramref name="peerSnapshotPaths"/>)과 메모리를 합집합 병합(같은 ID → 더 많이 조회된 스냅샷) ·
    /// 처리 기록(무시 · 연결) 전파 · 어느 PC 카탈로그에서든 이미 연결된 YouTube ID 정리. 기본 구현 = 미지원(빈 결과).
    /// </summary>
    Task<PlutoUnmatchedReconcileResult> ReconcileAsync(IEnumerable<string>? peerSnapshotPaths = null, CancellationToken ct = default)
        => Task.FromResult(new PlutoUnmatchedReconcileResult(0, 0, 0, Count));
}

/// <summary>🔁 Step 25 — 멀티 PC 대기열 조정 결과.</summary>
/// <param name="MergedFromPeers">다른 사본에서 새로 들어오거나 더 성숙한 스냅샷으로 갱신된 행 수.</param>
/// <param name="PrunedLinked">어느 PC 카탈로그에서든 이미 연결돼 정리된 행 수.</param>
/// <param name="PrunedResolved">다른 PC의 처리 기록(무시 · 페어링)으로 정리된 행 수.</param>
/// <param name="Remaining">조정 후 대기 행 수.</param>
public sealed record PlutoUnmatchedReconcileResult(int MergedFromPeers, int PrunedLinked, int PrunedResolved, int Remaining)
{
    public bool Changed => MergedFromPeers + PrunedLinked + PrunedResolved > 0;
}

/// <summary>
/// 📌 <see cref="IPlutoYouTubeUnmatchedQueueService"/> 구현 — <c>pluto_youtube_unmatched.json</c>(☁️ Drive 볼트 · 로컬 폴백 · 원자 기록).
/// 적재 불가(잠금 · 권한) 세션은 디스크 덮어쓰기 금지 · 영속 실패 → [부분 구제] 메모리 유지. 페어링은 단일 게이트 안에서 끝까지 수행(더블 클릭 이중 반영 차단).
/// </summary>
public sealed class PlutoYouTubeUnmatchedQueueService : IPlutoYouTubeUnmatchedQueueService, IDisposable
{
    public const string FileName = "pluto_youtube_unmatched.json";
    public const int Capacity = 50;
    public const int MaxTitleLength = 300;

    private readonly IPlutoRenderCatalogService _catalog;
    private readonly IPlutoClosedLoopReinforcementService _loop;
    private readonly IPlutoStorageManager? _storage;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _clock;
    private List<PlutoUnmatchedYouTubeRow> _rows = new();   // 최신 대기순
    private Dictionary<string, TombstoneDto> _tombs = new(StringComparer.Ordinal);   // 🪦 처리 기록(_gate 안에서만 변경)
    private volatile bool _loaded;
    private volatile bool _readOnly;
    private int _disposed;

    public PlutoYouTubeUnmatchedQueueService(IPlutoStorageManager storage, IPlutoRenderCatalogService catalog, IPlutoClosedLoopReinforcementService loop, Func<DateTimeOffset>? clock = null)
        : this((string?)null, catalog, loop, clock)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public PlutoYouTubeUnmatchedQueueService(string? storagePath, IPlutoRenderCatalogService catalog, IPlutoClosedLoopReinforcementService loop, Func<DateTimeOffset>? clock = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", FileName)
            : storagePath;
    }

    public string StoragePath => _storage?.ResolveDataPath(FileName) ?? _path;

    public event Action? Changed;

    public int Count { get { var r = _rows; return r.Count; } }

    // ── 직렬화 DTO ──

    public sealed class RowDto
    {
        public string YouTubeVideoId { get; set; } = string.Empty;
        public string? Title { get; set; }
        public DateTimeOffset? PublishedAtUtc { get; set; }
        public long Views { get; set; }
        public double Hook { get; set; }
        public bool HookEstimated { get; set; }
        public double Completion { get; set; }
        public double RawAvg { get; set; }
        public double Ctr { get; set; }
        public bool CtrEstimated { get; set; }
        public long Impressions { get; set; } = -1;
        public long Likes { get; set; } = -1;
        public string? Reason { get; set; }
        public string? Source { get; set; }
        public DateTimeOffset QueuedAtUtc { get; set; }
    }

    /// <summary>🪦 Step 25 — 처리 기록(무시 · 연결 · 페어링) — 합집합 병합이 다른 PC 사본에서 처리된 행을 되살리지 않게 함.</summary>
    public sealed class TombstoneDto
    {
        public string YouTubeVideoId { get; set; } = string.Empty;
        public string? Kind { get; set; }
        public DateTimeOffset AtUtc { get; set; }
    }

    public sealed class Snapshot
    {
        public int Version { get; set; } = 2;
        public List<RowDto>? Rows { get; set; }
        /// <summary>🪦 Step 25 — 처리 기록(구버전 파일 → null).</summary>
        public List<TombstoneDto>? Resolved { get; set; }
    }

    /// <summary>🪦 처리 기록 종류.</summary>
    public const string TombstoneDismissed = "dismissed", TombstoneLinked = "linked", TombstonePaired = "paired";
    /// <summary>🪦 처리 기록 상한(최신 우선).</summary>
    public const int TombstoneCapacity = 200;
    /// <summary>🪦 처리 기록 보존 기간 — YouTube Studio 기간 내보내기(최대 90일 + 여유)가 같은 행을 다시 실어 와도 되살아나지 않게.</summary>
    public static readonly TimeSpan TombstoneTtl = TimeSpan.FromDays(120);

    /// <summary>
    /// 🔁 순수 병합 — 행: YouTube ID 합집합 · 더 많이 조회된 스냅샷(동률 → 늦게 대기된 쪽) · 처리 기록: 합집합(ID별 최신) · TTL/상한 정리 ·
    /// 처리 기록이 있는 ID의 행 제거 · 최신 대기순 · 상한 <see cref="Capacity"/>. 손상 DTO는 건너뜀.
    /// </summary>
    public static Snapshot MergeSnapshots(IEnumerable<Snapshot?> sides, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sides);
        var rows = new Dictionary<string, PlutoUnmatchedYouTubeRow>(StringComparer.Ordinal);
        var tombs = new Dictionary<string, TombstoneDto>(StringComparer.Ordinal);
        foreach (var side in sides)
        {
            if (side is null) continue;
            if (side.Resolved is { } rs)
                foreach (var t in rs)
                {
                    if (t is null || !YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(t.YouTubeVideoId) || nowUtc - t.AtUtc > TombstoneTtl) continue;
                    if (!tombs.TryGetValue(t.YouTubeVideoId, out var cur) || t.AtUtc > cur.AtUtc)
                        tombs[t.YouTubeVideoId] = new TombstoneDto { YouTubeVideoId = t.YouTubeVideoId, Kind = t.Kind, AtUtc = t.AtUtc };
                }
            if (side.Rows is { } rr)
                foreach (var d in rr)
                {
                    if (FromDto(d) is not { } r) continue;
                    if (!rows.TryGetValue(r.YouTubeVideoId, out var cur) || r.Row.Views > cur.Row.Views || (r.Row.Views == cur.Row.Views && r.QueuedAtUtc > cur.QueuedAtUtc))
                        rows[r.YouTubeVideoId] = r;
                }
        }
        var list = new List<PlutoUnmatchedYouTubeRow>(rows.Count);
        foreach (var r in rows.Values) if (!tombs.ContainsKey(r.YouTubeVideoId)) list.Add(r);
        list.Sort(static (x, y) =>
        {
            var c = y.QueuedAtUtc.CompareTo(x.QueuedAtUtc);
            return c != 0 ? c : string.CompareOrdinal(x.YouTubeVideoId, y.YouTubeVideoId);
        });
        if (list.Count > Capacity) list.RemoveRange(Capacity, list.Count - Capacity);
        var snap = new Snapshot { Rows = new List<RowDto>(list.Count), Resolved = new List<TombstoneDto>(tombs.Count) };
        foreach (var r in list) snap.Rows.Add(ToDto(r));
        snap.Resolved.AddRange(tombs.Values);
        snap.Resolved.Sort(static (x, y) =>
        {
            var c = y.AtUtc.CompareTo(x.AtUtc);
            return c != 0 ? c : string.CompareOrdinal(x.YouTubeVideoId, y.YouTubeVideoId);
        });
        if (snap.Resolved.Count > TombstoneCapacity) snap.Resolved.RemoveRange(TombstoneCapacity, snap.Resolved.Count - TombstoneCapacity);
        return snap;
    }

    private static RowDto ToDto(PlutoUnmatchedYouTubeRow r) => new()
    {
        YouTubeVideoId = r.YouTubeVideoId, Title = r.Row.Title, PublishedAtUtc = r.Row.PublishedAtUtc, Views = r.Row.Views,
        Hook = r.Row.Hook3sRetention, HookEstimated = r.Row.HookEstimated, Completion = r.Row.CompletionRate, RawAvg = r.Row.RawAveragePercentViewed,
        Ctr = r.Row.Ctr, CtrEstimated = r.Row.CtrEstimated, Impressions = r.Row.Impressions, Likes = r.Row.Likes,
        Reason = r.Reason, Source = r.SourceName, QueuedAtUtc = r.QueuedAtUtc,
    };

    private static PlutoUnmatchedYouTubeRow? FromDto(RowDto? d)
    {
        if (d is null || !YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(d.YouTubeVideoId) || string.IsNullOrWhiteSpace(d.Title)) return null;
        if (!double.IsFinite(d.Hook) || !double.IsFinite(d.Completion) || !double.IsFinite(d.Ctr) || d.Views < 0) return null;
        var row = new YouTubeAnalyticsRow(d.YouTubeVideoId, d.Title, d.PublishedAtUtc, d.Views, d.Hook, d.HookEstimated, d.Completion, d.RawAvg, d.Ctr, d.CtrEstimated)
        { Impressions = d.Impressions, Likes = d.Likes };
        return new PlutoUnmatchedYouTubeRow(row, d.Reason, d.Source, d.QueuedAtUtc);
    }

    /// <summary>입력 행 정화 — YouTube ID 형식 · 제목 · 유한 지표 필수(그 밖 → null).</summary>
    public static PlutoUnmatchedYouTubeRow? Sanitize(PlutoUnmatchedYouTubeRow? r)
    {
        if (r?.Row is not { } row) return null;
        var id = row.YouTubeVideoId?.Trim();
        if (!YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(id) || string.IsNullOrWhiteSpace(row.Title)) return null;
        if (!double.IsFinite(row.Hook3sRetention) || !double.IsFinite(row.CompletionRate) || !double.IsFinite(row.Ctr) || row.Views < 0) return null;
        var title = row.Title.Trim();
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength];
        return r with { Row = row with { YouTubeVideoId = id, Title = title } };
    }

    // ── 적재 / 영속 ──

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await EnsureLoadedLockedAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedLockedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(StoragePath, "Pluto.UnmatchedQueue", ct).ConfigureAwait(false);
        _readOnly = status == PlutoLoadStatus.Unreadable;
        var list = new List<PlutoUnmatchedYouTubeRow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (data?.Rows is { } rows)
            foreach (var d in rows)
            {
                if (list.Count >= Capacity) break;
                if (FromDto(d) is { } r && seen.Add(r.YouTubeVideoId)) list.Add(r);
            }
        _rows = list;
        _tombs = TombMap(data?.Resolved, _clock());
        _loaded = true;
    }

    private static Dictionary<string, TombstoneDto> TombMap(List<TombstoneDto>? src, DateTimeOffset now)
    {
        var map = new Dictionary<string, TombstoneDto>(StringComparer.Ordinal);
        if (src is null) return map;
        foreach (var t in src)
        {
            if (t is null || !YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(t.YouTubeVideoId) || now - t.AtUtc > TombstoneTtl) continue;
            if (!map.TryGetValue(t.YouTubeVideoId, out var cur) || t.AtUtc > cur.AtUtc) map[t.YouTubeVideoId] = t;
            if (map.Count >= TombstoneCapacity) break;
        }
        return map;
    }

    /// <summary>🪦 처리 기록 추가(호출자가 _gate 보유 · 교체식 갱신 · 상한 초과 → 가장 오래된 기록부터 축출).</summary>
    private void TombstoneLocked(string youTubeVideoId, string kind)
    {
        if (!YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(youTubeVideoId)) return;
        var map = new Dictionary<string, TombstoneDto>(_tombs, StringComparer.Ordinal)
        {
            [youTubeVideoId] = new TombstoneDto { YouTubeVideoId = youTubeVideoId, Kind = kind, AtUtc = _clock() },
        };
        while (map.Count > TombstoneCapacity)
        {
            string? oldest = null;
            var at = DateTimeOffset.MaxValue;
            foreach (var kv in map) if (kv.Value.AtUtc < at) { at = kv.Value.AtUtc; oldest = kv.Key; }
            if (oldest is null) break;
            map.Remove(oldest);
        }
        _tombs = map;
    }

    /// <summary>🪦 처리(무시 · 연결 · 페어링)된 ID인지 — 교체식 스냅샷 읽기.</summary>
    public bool IsResolved(string youTubeVideoId) => !string.IsNullOrWhiteSpace(youTubeVideoId) && _tombs.ContainsKey(youTubeVideoId.Trim());

    private Snapshot BuildSnapshotLocked()
    {
        var snap = new Snapshot { Rows = new List<RowDto>(_rows.Count), Resolved = new List<TombstoneDto>(_tombs.Values) };
        foreach (var r in _rows) snap.Rows.Add(ToDto(r));
        snap.Resolved.Sort(static (x, y) =>   // 결정론적 순서 → 내용 비교 · 무변경 무기록
        {
            var c = y.AtUtc.CompareTo(x.AtUtc);
            return c != 0 ? c : string.CompareOrdinal(x.YouTubeVideoId, y.YouTubeVideoId);
        });
        return snap;
    }

    private async Task PersistLockedAsync(CancellationToken ct)
    {
        if (_readOnly) return;   // 🔒 적재 불가 세션 → 기존 파일 보호
        var json = JsonSerializer.Serialize(BuildSnapshotLocked());
        // ☁️ Step 25 — 저장소 관리자 경유: Drive 실패 → 로컬 폴백 + 재연결 시 쓰기 반환(📴 대기 목록) · 다른 PC로 전파
        if (_storage is not null) await _storage.WriteAllTextAtomicAsync(FileName, json, ct).ConfigureAwait(false);
        else await PlutoJsonStore.WriteAtomicAsync(_path, json, "Pluto.UnmatchedQueue", ct).ConfigureAwait(false);
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.UnmatchedQueue] subscriber: " + ex.Message); }
        }
    }

    private int IndexOfLocked(string youTubeVideoId)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (string.Equals(_rows[i].YouTubeVideoId, youTubeVideoId, StringComparison.Ordinal)) return i;
        return -1;
    }

    // ── 공개 API ──

    public IReadOnlyList<PlutoUnmatchedYouTubeRow> GetPending(int maxCount = Capacity)
    {
        var snap = _rows;   // 교체식 갱신 → 스냅샷 읽기 안전
        var n = Math.Min(Math.Max(0, maxCount), snap.Count);
        var result = new PlutoUnmatchedYouTubeRow[n];
        for (var i = 0; i < n; i++) result[i] = snap[i];
        return result;
    }

    public async Task<int> EnqueueAsync(IEnumerable<PlutoUnmatchedYouTubeRow> rows, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var changed = 0;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            var list = new List<PlutoUnmatchedYouTubeRow>(_rows);
            foreach (var raw in rows)
            {
                if (Sanitize(raw) is not { } r) continue;
                if (_tombs.ContainsKey(r.YouTubeVideoId)) continue;   // 🪦 무시/처리된 행 — 같은 기간 내보내기 재수집(자동 감시)으로 되살리지 않음
                // 🛡️ 대기 중 다른 경로로 이미 연결된 ID는 보존하지 않음(이중 학습 방지)
                if (await _catalog.FindByYouTubeVideoIdAsync(r.YouTubeVideoId, ct).ConfigureAwait(false) is not null) continue;
                var idx = -1;
                for (var i = 0; i < list.Count; i++) if (string.Equals(list[i].YouTubeVideoId, r.YouTubeVideoId, StringComparison.Ordinal)) { idx = i; break; }
                if (idx >= 0)
                {
                    if (r.Row.Views < list[idx].Row.Views) continue;   // 덜 성숙한(오래된) 내보내기 → 기존 스냅샷 유지
                    if (r.Row.Views == list[idx].Row.Views && string.Equals(r.Title, list[idx].Title, StringComparison.Ordinal)) continue;   // 같은 스냅샷 재수집 → 무변경
                    list.RemoveAt(idx);
                }
                list.Insert(0, r with { QueuedAtUtc = _clock() });   // 최신 대기 맨 앞(대기 시각은 서비스 시계 기준)
                changed++;
            }
            if (changed == 0) return 0;
            while (list.Count > Capacity) list.RemoveAt(list.Count - 1);   // 가장 오래된 대기부터 축출
            _rows = list;
            await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return changed;
    }

    public async Task<int> ResolveLinkedAsync(IEnumerable<string> youTubeVideoIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(youTubeVideoIds);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in youTubeVideoIds) if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());
        if (ids.Count == 0) return 0;
        int removed;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            var list = new List<PlutoUnmatchedYouTubeRow>(_rows.Count);
            foreach (var r in _rows)
            {
                if (!ids.Contains(r.YouTubeVideoId)) { list.Add(r); continue; }
                TombstoneLocked(r.YouTubeVideoId, TombstoneLinked);   // 🪦 다른 PC 사본이 이 행을 되살리지 않게
            }
            removed = _rows.Count - list.Count;
            if (removed == 0) return 0;
            _rows = list;
            await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return removed;
    }

    public async Task<IReadOnlyList<PlutoUnmatchedPairSuggestion>> SuggestClipsAsync(string youTubeVideoId, int maxCount = 3, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        var snap = _rows;
        PlutoUnmatchedYouTubeRow? entry = null;
        foreach (var r in snap) if (string.Equals(r.YouTubeVideoId, youTubeVideoId?.Trim(), StringComparison.Ordinal)) { entry = r; break; }
        if (entry is null) return Array.Empty<PlutoUnmatchedPairSuggestion>();
        var clips = await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false);
        var ranked = PlutoYouTubeCatalogLinker.RankPairSuggestions(entry.Row, clips, maxCount);
        var result = new PlutoUnmatchedPairSuggestion[ranked.Count];
        for (var i = 0; i < ranked.Count; i++) result[i] = new PlutoUnmatchedPairSuggestion(ranked[i].Clip, ranked[i].Score);
        return result;
    }

    public async Task<PlutoUnmatchedPairResult> PairAsync(string youTubeVideoId, string clipVideoId, string platform = "YouTube Shorts", CancellationToken ct = default)
    {
        var yt = youTubeVideoId?.Trim() ?? string.Empty;
        var clipId = clipVideoId?.Trim() ?? string.Empty;
        var plat = string.IsNullOrWhiteSpace(platform) ? "YouTube Shorts" : platform.Trim();
        PlutoUnmatchedPairResult result;
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);   // 🔒 끝까지 게이트 유지 → 더블 클릭 두 번째는 NotQueued
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            var idx = IndexOfLocked(yt);
            if (idx < 0) return new PlutoUnmatchedPairResult(PlutoUnmatchedPairStatus.NotQueued, null, "📌 대기열에 없는 YouTube 행입니다(이미 처리됨).");
            var entry = _rows[idx];
            PlutoRenderedClipRecord? clip = null;
            if (clipId.Length > 0)
                foreach (var c in await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false))
                    if (string.Equals(c.VideoId, clipId, StringComparison.OrdinalIgnoreCase)) { clip = c; break; }
            if (clip is null) return new PlutoUnmatchedPairResult(PlutoUnmatchedPairStatus.ClipNotFound, null, "⚠ 카탈로그에서 렌더 영상을 찾지 못했습니다.");

            var bind = await _catalog.TryBindYouTubeVideoIdAsync(clip.VideoId, yt, ct).ConfigureAwait(false);
            switch (bind)
            {
                case PlutoYouTubeBindResult.ClipBoundElsewhere:
                    return new PlutoUnmatchedPairResult(PlutoUnmatchedPairStatus.ClipBoundElsewhere, null, "⚠ 이 렌더 영상은 이미 다른 YouTube 영상에 연결되어 있습니다.");
                case PlutoYouTubeBindResult.IdTakenByOtherClip:
                    TombstoneLocked(yt, TombstoneLinked);
                    RemoveAtLocked(idx);   // 🧹 이미 다른 경로로 연결 → 대기 항목은 낡음(이중 학습 방지)
                    await PersistLockedAsync(ct).ConfigureAwait(false);
                    changed = true;
                    return new PlutoUnmatchedPairResult(PlutoUnmatchedPairStatus.AlreadyLinkedElsewhere, null, "📺 이 YouTube 영상은 이미 다른 렌더 영상에 연결되어 대기열에서 정리했습니다.");
                case PlutoYouTubeBindResult.NotFound:
                    return new PlutoUnmatchedPairResult(PlutoUnmatchedPairStatus.ClipNotFound, null, "⚠ 카탈로그에서 렌더 영상을 찾지 못했습니다.");
            }

            var bound = clip with { YouTubeVideoId = yt };
            PlutoYouTubeRowApplyResult applied;
            try { applied = await PlutoTelemetryBatchImporter.ApplyYouTubeRowAsync(_catalog, _loop, bound, entry.Row, plat, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Pluto.UnmatchedQueue] pair learn: " + ex.Message);
                // 연결은 유지 · 대기열 유지 → 재시도 시 AlreadyBound 경로로 멱등 재반영
                return new PlutoUnmatchedPairResult(PlutoUnmatchedPairStatus.LearnFailed, null, "⚠ 연결은 저장됐지만 학습 반영에 실패했습니다 — 다시 시도하세요: " + ex.Message);
            }
            TombstoneLocked(yt, TombstonePaired);
            RemoveAtLocked(idx);
            await PersistLockedAsync(ct).ConfigureAwait(false);
            changed = true;
            var label = string.IsNullOrWhiteSpace(clip.Title) ? clip.VideoId : clip.Title;
            var msg = applied.Kind switch
            {
                PlutoYouTubeRowApplyKind.Learned => "🔗 연결 + 학습 반영: " + label,
                PlutoYouTubeRowApplyKind.Matured => "🔗 연결 + 성숙 증분 반영: " + label,
                _ => "🔗 연결 완료(이미 반영된 성과 · 기준점 채택): " + label,
            };
            if (applied.GuardActivated) msg += " · 🛡️ 오답 가드 발동";
            result = new PlutoUnmatchedPairResult(PlutoUnmatchedPairStatus.Paired, applied, msg + " · 조회수 " + entry.Row.Views.ToString(CultureInfo.InvariantCulture));
        }
        finally
        {
            _gate.Release();
            if (changed) RaiseChanged();
        }
        return result;
    }

    public async Task<bool> DismissAsync(string youTubeVideoId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            var id = youTubeVideoId?.Trim() ?? string.Empty;
            var idx = IndexOfLocked(id);
            if (idx < 0) return false;
            TombstoneLocked(id, TombstoneDismissed);   // 🪦 다음 자동 수집 · 다른 PC 사본에서 되살아나지 않음
            RemoveAtLocked(idx);
            await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return true;
    }

    public async Task<PlutoUnmatchedReconcileResult> ReconcileAsync(IEnumerable<string>? peerSnapshotPaths = null, CancellationToken ct = default)
    {
        var paths = new List<string>(4);
        void AddPath(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            var t = p.Trim();
            foreach (var x in paths) if (string.Equals(x, t, StringComparison.OrdinalIgnoreCase)) return;
            paths.Add(t);
        }
        AddPath(StoragePath);
        if (_storage is PlutoDriveStorageManager dm) AddPath(Path.Combine(dm.FallbackRoot, FileName));   // 💾 로컬 미러(오프라인 구간 기록분)
        if (peerSnapshotPaths is not null) foreach (var p in peerSnapshotPaths) AddPath(p);

        int merged = 0, prunedLinked = 0, prunedResolved = 0, remaining;
        var changed = false;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedLockedAsync(ct).ConfigureAwait(false);
            var mine = BuildSnapshotLocked();
            var beforeJson = JsonSerializer.Serialize(mine);
            var sides = new List<Snapshot?>(paths.Count + 1) { mine };
            var catalogPaths = new List<string>(paths.Count);
            foreach (var p in paths)
            {
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) catalogPaths.Add(Path.Combine(dir, PlutoRenderCatalogService.FileName));
                if (!File.Exists(p)) continue;
                var (data, status) = await PlutoJsonStore.TryLoadAsync<Snapshot>(p, "Pluto.UnmatchedQueue.Reconcile", ct).ConfigureAwait(false);
                if (status == PlutoLoadStatus.Ok && data is not null) sides.Add(data);
            }
            var now = _clock();
            var union = MergeSnapshots(sides, now);

            // 🔗 어느 PC에서든 연결된 YouTube ID — 메모리 카탈로그 + 사본 옆 카탈로그 파일(다른 PC가 Drive로 올린 연결이 아직 재적재 전이어도 포착)
            var linked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in await _catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false))
                if (c.YouTubeVideoId is { Length: > 0 } y) linked.Add(y);
            var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cp in catalogPaths)
            {
                if (!scanned.Add(cp) || !File.Exists(cp)) continue;
                var (cat, cs) = await PlutoJsonStore.TryLoadAsync<PlutoRenderCatalogService.Snapshot>(cp, "Pluto.UnmatchedQueue.ReconcileCatalog", ct).ConfigureAwait(false);
                if (cs != PlutoLoadStatus.Ok || cat?.Clips is not { } clips) continue;
                foreach (var c in clips)
                    if (c?.YouTubeVideoId?.Trim() is { } y && YouTubeStudioAnalyticsNormalizer.IsYouTubeVideoId(y)) linked.Add(y);
            }

            var before = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var r in _rows) before[r.YouTubeVideoId] = r.Row.Views;
            _tombs = TombMap(union.Resolved, now);
            var rows = new List<PlutoUnmatchedYouTubeRow>(union.Rows?.Count ?? 0);
            if (union.Rows is { } ur)
                foreach (var d in ur)
                {
                    if (FromDto(d) is not { } r) continue;
                    if (linked.Contains(r.YouTubeVideoId)) { prunedLinked++; TombstoneLocked(r.YouTubeVideoId, TombstoneLinked); continue; }
                    rows.Add(r);
                    if (!before.TryGetValue(r.YouTubeVideoId, out var v) || r.Row.Views > v) merged++;
                }
            foreach (var id in before.Keys)
                if (_tombs.ContainsKey(id) && !linked.Contains(id)) prunedResolved++;
            _rows = rows;
            remaining = rows.Count;
            if (!string.Equals(beforeJson, JsonSerializer.Serialize(BuildSnapshotLocked()), StringComparison.Ordinal))
            {
                changed = true;
                await PersistLockedAsync(ct).ConfigureAwait(false);   // 내용이 같으면 무기록(Drive 동기화 핑퐁 방지)
            }
        }
        finally { _gate.Release(); }
        if (changed) RaiseChanged();
        return new PlutoUnmatchedReconcileResult(merged, prunedLinked, prunedResolved, remaining);
    }

    private void RemoveAtLocked(int idx)
    {
        var list = new List<PlutoUnmatchedYouTubeRow>(_rows);
        list.RemoveAt(idx);
        _rows = list;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Changed = null;
        _gate.Dispose();
    }
}
