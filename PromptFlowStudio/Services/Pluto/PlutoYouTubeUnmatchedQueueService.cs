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

    public sealed class Snapshot
    {
        public int Version { get; set; } = 1;
        public List<RowDto>? Rows { get; set; }
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
        _loaded = true;
    }

    private async Task PersistLockedAsync(CancellationToken ct)
    {
        if (_readOnly) return;   // 🔒 적재 불가 세션 → 기존 파일 보호
        var snap = new Snapshot { Rows = new List<RowDto>(_rows.Count) };
        foreach (var r in _rows) snap.Rows.Add(ToDto(r));
        await PlutoJsonStore.WriteAtomicAsync(StoragePath, JsonSerializer.Serialize(snap), "Pluto.UnmatchedQueue", ct).ConfigureAwait(false);
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
            foreach (var r in _rows) if (!ids.Contains(r.YouTubeVideoId)) list.Add(r);
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
            var idx = IndexOfLocked(youTubeVideoId?.Trim() ?? string.Empty);
            if (idx < 0) return false;
            RemoveAtLocked(idx);
            await PersistLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        RaiseChanged();
        return true;
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
