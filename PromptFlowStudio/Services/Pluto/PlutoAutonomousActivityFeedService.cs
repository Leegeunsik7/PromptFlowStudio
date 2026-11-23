using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🛰️ Step 37 — 자율 활동 종류.</summary>
public enum PlutoActivityKind
{
    /// <summary>YouTube Data API 동기화(새 연결 · 대기열 회수).</summary>
    ApiSync = 0,
    /// <summary>API 정지(쿼터 · 예산 · 인증).</summary>
    ApiPause = 1,
    /// <summary>Downloads 감시 자동 수집(선택 <c>Pluto_Ingested/</c> 정리).</summary>
    DownloadsIngest = 2,
    /// <summary>적용 기획안 실측 채점.</summary>
    BriefGrading = 3,
    /// <summary>심각 미달(−12p) 자동 사후 분석 → 오답 노트 벌점.</summary>
    BriefPostMortem = 4,
    /// <summary>유기적 복권(+8p) → 오답 노트 벌점 감쇠.</summary>
    OrganicRehabilitation = 5,
}

/// <summary>🛰️ 자율 활동 1건(메시지는 이미 경로 · 비밀값 가림 처리됨).</summary>
public sealed record PlutoActivityEntry(long Seq, DateTimeOffset AtUtc, PlutoActivityKind Kind, string MessageKorean)
{
    /// <summary>중복 차단 키(파일 해시 · 클립 ID 등 — 비밀값 아님).</summary>
    public string? DedupeKey { get; init; }

    public string Icon => Kind switch
    {
        PlutoActivityKind.ApiSync => "🔄",
        PlutoActivityKind.ApiPause => "⏸",
        PlutoActivityKind.DownloadsIngest => "📥",
        PlutoActivityKind.BriefGrading => "🎯",
        PlutoActivityKind.BriefPostMortem => "🩻",
        PlutoActivityKind.OrganicRehabilitation => "🌱",
        _ => "•",
    };

    /// <summary>현지 시각 · 아이콘 · 메시지 1줄.</summary>
    public string ToLine() => AtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + Icon + " " + MessageKorean;
}

/// <summary>
/// 🛰️ Step 37 — 통합 자율 활동 피드: 백그라운드 API 동기화 · Downloads 자동 수집 · 기획 채점/사후 분석 · 유기적 복권을 최근 <see cref="PlutoAutonomousActivityFeedService.Capacity"/>건으로 보관.
/// 로컬 전용(<c>PlutoLocalOnly</c>) — Google Drive 동기화 허브가 아니다.
/// </summary>
public interface IPlutoAutonomousActivityFeedService
{
    /// <summary>현재 보관 건수(≤ 30).</summary>
    int Count { get; }
    /// <summary>가장 최근 활동(없음 → null).</summary>
    PlutoActivityEntry? Latest { get; }
    /// <summary>최신 순 스냅샷.</summary>
    IReadOnlyList<PlutoActivityEntry> GetRecent(int max = PlutoAutonomousActivityFeedService.Capacity);
    /// <summary>항목 변경(백그라운드 스레드 가능 · 구독자별 예외 격리).</summary>
    event Action? Changed;

    /// <summary>임의 활동 기록 — 메시지는 가림 처리 후 저장. 같은 <paramref name="dedupeKey"/> 가 보관 중이면 무시(false).</summary>
    bool Record(PlutoActivityKind kind, string? message, string? dedupeKey = null);
    /// <summary>API 동기화 결과 — 새 연결 · 대기열 회수 · 쿼터/예산/인증 정지만 기록.</summary>
    bool RecordApiSync(PlutoYouTubeApiSyncResult? result, bool background);
    /// <summary>Downloads 자동 수집 1건(파일 해시로 중복 차단).</summary>
    bool RecordDownloadsIngest(PlutoDownloadsAutoIngestEvent? evt);
    /// <summary>기획 채점 스윕 — 채점 요약 · 사후 분석 · 유기적 복권. 반환 = 새로 기록한 수.</summary>
    int RecordBriefSweep(PlutoBriefSweepResult? sweep);

    /// <summary>🧹 전부 비우기(영속).</summary>
    Task ClearAsync(CancellationToken ct = default);
    /// <summary>대기 중 영속 완료까지 대기.</summary>
    Task FlushAsync(CancellationToken ct = default);
}

/// <summary>
/// 🛰️ <see cref="IPlutoAutonomousActivityFeedService"/> 구현(순수 BCL).
///   ▸ 유계 링(30) · 최신 순 · 단조 증가 Seq · 재시작 후 복원.
///   ▸ 🛡️ 기록 경로는 메모리 추가만(감시기 단일 수집 게이트 안에서 호출돼도 디스크 I/O 로 막지 않음) → 코얼레싱 원자 영속(임시 파일 + 교체).
///   ▸ 🔒 저장 · 표시 전 절대 경로/비밀값 가림 · 적재 시 재가림(구버전 · 수정된 파일 방어) · 클라우드 동기화 경로 저장 거부(메모리 전용).
/// </summary>
public sealed class PlutoAutonomousActivityFeedService : IPlutoAutonomousActivityFeedService, IDisposable, IAsyncDisposable
{
    public const int Capacity = 30;
    public const int MaxMessageLength = 240;
    public const string FeedFileName = "pluto_autonomous_activity.json";
    public const int SchemaVersion = 1;

    private readonly object _sync = new();
    private readonly List<PlutoActivityEntry> _entries = new(Capacity + 1);   // 오래된 → 최신
    private readonly SemaphoreSlim _io = new(1, 1);
    private readonly TimeProvider _time;
    private long _nextSeq = 1;
    private bool _persistRunning, _persistDirty, _disposed;
    private Task _persistTask = Task.CompletedTask;
    private object? _lastSweep;

    /// <summary>영속 파일(거부됨 → null · 메모리 전용).</summary>
    public string? StorePath { get; }
    /// <summary>🔒 클라우드 동기화 경로라서 저장을 거부함.</summary>
    public bool StoreRejected { get; }
    /// <summary>마지막 영속 실패(격리됨).</summary>
    public string? LastPersistError { get; private set; }

    public event Action? Changed;

    public PlutoAutonomousActivityFeedService(string? storePath = null, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        var path = string.IsNullOrWhiteSpace(storePath) ? DefaultStorePath() : storePath.Trim();
        if (PlutoYouTubeApiSyncService.IsCloudSyncedPath(path)) { StoreRejected = true; StorePath = null; }
        else { StorePath = path; Load(); }
    }

    /// <summary><c>%LOCALAPPDATA%\PromptFlowStudio\PlutoLocalOnly\pluto_autonomous_activity.json</c>.</summary>
    public static string DefaultStorePath()
        => Path.Combine(Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", PlutoYouTubeApiSyncService.LocalOnlyFolderName, FeedFileName);

    public int Count { get { lock (_sync) return _entries.Count; } }

    public PlutoActivityEntry? Latest { get { lock (_sync) return _entries.Count == 0 ? null : _entries[^1]; } }

    public IReadOnlyList<PlutoActivityEntry> GetRecent(int max = Capacity)
    {
        lock (_sync)
        {
            var n = Math.Min(Math.Max(0, max), _entries.Count);
            var list = new List<PlutoActivityEntry>(n);
            for (var i = _entries.Count - 1; i >= 0 && list.Count < n; i--) list.Add(_entries[i]);
            return list;
        }
    }

    // ── 🔒 가림 ──

    private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(250);
    private const RegexOptions RxOpt = RegexOptions.CultureInvariant | RegexOptions.Compiled;
    // 경로 = 접두 + (폴더 세그먼트: 공백 허용 · '=' '*' '·' 불가)* + 마지막 이름(공백 불가) — 비밀값을 먼저 '***'로 바꾼 뒤 적용해 경로 매칭이 토큰 꼬리를 '이름'으로 남기지 않게 한다.
    private const string Segs = @"(?:[^\\/\r\n""'<>|=*·]*[\\/])*[^\\/\s""'<>|·]*";
    private static readonly Regex WinPath = new(@"(?:\b[A-Za-z]:[\\/]|\\\\[^\\/\s""'<>|]+[\\/])" + Segs, RxOpt, RxTimeout);
    private static readonly Regex FileUri = new(@"file:/{2,3}" + Segs, RxOpt | RegexOptions.IgnoreCase, RxTimeout);
    private static readonly Regex UnixPath = new(@"(?<![\w.:/])/(?:Users|home|root|var|tmp|private|mnt|media|Volumes|storage|sdcard|data|opt|etc|srv)/" + Segs, RxOpt, RxTimeout);
    private static readonly Regex GoogleKey = new(@"AIza[0-9A-Za-z_\-]{16,}", RxOpt, RxTimeout);
    private static readonly Regex OAuthAccess = new(@"ya29\.[0-9A-Za-z_\-\.]+", RxOpt, RxTimeout);
    private static readonly Regex OAuthRefresh = new(@"1//[0-9A-Za-z_\-]{8,}", RxOpt, RxTimeout);
    private static readonly Regex ClientSecret = new(@"GOCSPX-[0-9A-Za-z_\-]+", RxOpt, RxTimeout);
    private static readonly Regex Bearer = new(@"(?i)\bbearer\s+[A-Za-z0-9_\-\.=+/]+", RxOpt, RxTimeout);
    private static readonly Regex KeyValue = new(@"(?i)\b(api[_\-]?key|key|access_token|refresh_token|id_token|client_secret|token|password|passwd|pwd|secret|sig|signature)\s*[=:]\s*[^&\s""',;]+", RxOpt, RxTimeout);
    private static readonly Regex LongBlob = new(@"[A-Za-z0-9+/_\-]{40,}={0,2}", RxOpt, RxTimeout);

    /// <summary>
    /// 🔒 절대 경로(Windows 드라이브 · UNC · <c>file://</c> · 홈/시스템 루트 Unix) → <c>‹경로›</c> + 마지막 이름만 유지 ·
    /// API 키 · OAuth 토큰 · 클라이언트 시크릿 · <c>key=…</c> · Bearer · 40자 이상 토큰형 문자열 → <c>***</c> · 제어 문자 제거 · 길이 상한.
    /// </summary>
    public static string Redact(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return string.Empty;
        var s = message;
        try
        {
            s = GoogleKey.Replace(s, "AIza***");
            s = OAuthAccess.Replace(s, "ya29.***");
            s = OAuthRefresh.Replace(s, "1//***");
            s = ClientSecret.Replace(s, "GOCSPX-***");
            s = Bearer.Replace(s, "Bearer ***");
            s = KeyValue.Replace(s, static m => m.Groups[1].Value + "=***");
            s = FileUri.Replace(s, static m => KeepLeaf(m.Value));
            s = WinPath.Replace(s, static m => KeepLeaf(m.Value));
            s = UnixPath.Replace(s, static m => KeepLeaf(m.Value));
            s = LongBlob.Replace(s, "***");
        }
        catch (RegexMatchTimeoutException) { return "‹가림 처리 실패 — 메시지 생략›"; }   // 보수: 가림을 끝내지 못한 원문은 절대 저장 · 표시하지 않음
        var sb = new StringBuilder(Math.Min(s.Length, MaxMessageLength + 1));
        foreach (var ch in s)
        {
            if (sb.Length >= MaxMessageLength) break;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.ToString().Trim();
    }

    /// <summary>경로 일치 구간 → <c>‹경로›</c> + 마지막 구분자 뒤(파일 이름 + 뒤따르는 문구)만.</summary>
    private static string KeepLeaf(string matched)
    {
        var cut = Math.Max(matched.LastIndexOf('\\'), matched.LastIndexOf('/'));
        var leaf = cut >= 0 ? matched[(cut + 1)..] : string.Empty;
        return "‹경로›" + (leaf.Length > 0 ? "\\" + leaf : string.Empty);
    }

    // ── 기록 ──

    /// <summary>중복 차단 키 — 표시하지 않는 내부 식별자(파일 해시 · 클립 ID). 경로 형태만 가리고(해시는 보존) 길이 상한.</summary>
    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim();
        if (k.Contains('\\') || k.Contains('/')) k = "h:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(k)));
        return k.Length > 128 ? k[..128] : k;
    }

    public bool Record(PlutoActivityKind kind, string? message, string? dedupeKey = null)
    {
        var text = Redact(message);
        if (text.Length == 0) return false;
        var key = NormalizeKey(dedupeKey);
        lock (_sync)
        {
            if (_disposed) return false;
            if (key is not null)
                for (var i = 0; i < _entries.Count; i++)
                    if (string.Equals(_entries[i].DedupeKey, key, StringComparison.Ordinal)) return false;   // 🛡️ 중복 구독 · 재발행 · 재시작 재생 차단
            _entries.Add(new PlutoActivityEntry(_nextSeq++, _time.GetUtcNow(), kind, text) { DedupeKey = key });
            while (_entries.Count > Capacity) _entries.RemoveAt(0);
        }
        SchedulePersist();
        RaiseChanged();
        return true;
    }

    public bool RecordApiSync(PlutoYouTubeApiSyncResult? result, bool background)
    {
        if (result is null) return false;
        var inv = CultureInfo.InvariantCulture;
        var mode = background ? "자동" : "수동";
        switch (result.State)
        {
            case PlutoYouTubeApiSyncState.QuotaCooldown:
            case PlutoYouTubeApiSyncState.BudgetExhausted:
            case PlutoYouTubeApiSyncState.AuthFailed:
                var why = result.State switch
                {
                    PlutoYouTubeApiSyncState.QuotaCooldown => "쿼터 소진(quotaExceeded) — 태평양 자정까지 정지",
                    PlutoYouTubeApiSyncState.BudgetExhausted => "일일 자체 예산 소진 — 태평양 자정까지 정지",
                    _ => "인증 실패 — 자격 증명 재저장 전까지 정지",
                };
                // 같은 날 · 같은 정지 사유는 1건만(재시도 · 수동 재클릭이 피드를 덮지 않도록)
                return Record(PlutoActivityKind.ApiPause, "API 동기화(" + mode + ") ⏸ " + why,
                    "api-pause:" + result.State.ToString() + ":" + PlutoYouTubeApiSyncService.QuotaDayKey(_time.GetUtcNow()));
        }
        if (!result.Succeeded || result.Import is not { } imp) return false;
        var linked = imp.NewlyLinkedCount;
        var recovered = imp.QueueRecoveredCount;
        if (linked <= 0 && recovered <= 0) return false;   // 조회수 갱신만 → 기록하지 않음(30건 창을 소음으로 밀어내지 않음)
        var msg = "API 동기화(" + mode + ")";
        if (linked > 0) msg += " · 📺 새 연결 " + linked.ToString(inv) + "건";
        if (recovered > 0) msg += " · 📌→✅ 미매칭 대기열 회수 " + recovered.ToString(inv) + "건";
        msg += " · 👁 관측 " + imp.ApiObservedCount.ToString(inv) + "건";
        if (result.State == PlutoYouTubeApiSyncState.Partial) msg += " · [부분 구제]";
        return Record(PlutoActivityKind.ApiSync, msg);
    }

    public bool RecordDownloadsIngest(PlutoDownloadsAutoIngestEvent? evt)
    {
        if (evt is null || !evt.HasImpact) return false;
        var inv = CultureInfo.InvariantCulture;
        string name;
        try { name = Path.GetFileName(evt.FileName ?? string.Empty); }
        catch (ArgumentException) { name = string.Empty; }
        var msg = "Downloads 자동 수집 · " + (name.Length > 0 ? name : "내보내기") + " · 신규 " + evt.NewlyLearned.ToString(inv) + "건 · 성숙 보정 " + evt.Matured.ToString(inv) + "건";
        if (evt.Archived) msg += " · 📦 " + YouTubeDownloadsAutoWatcherService.ArchiveFolderName + "/ 정리";
        return Record(PlutoActivityKind.DownloadsIngest, msg, string.IsNullOrEmpty(evt.Sha256Hex) ? null : "dl:" + evt.Sha256Hex);
    }

    public int RecordBriefSweep(PlutoBriefSweepResult? sweep)
    {
        if (sweep is null) return 0;
        lock (_sync)
        {
            if (ReferenceEquals(_lastSweep, sweep)) return 0;   // 🛡️ 같은 스윕 결과 재통지(Changed 경합 · 이중 구독) → 1회만
            _lastSweep = sweep;
        }
        var inv = CultureInfo.InvariantCulture;
        var added = 0;
        var graded = 0;
        string? firstGraded = null;
        foreach (var v in sweep.Verdicts)
        {
            if (v is null) continue;
            if (v.IsSevere)
            {
                var msg = "기획 사후 분석 · " + v.BriefId + " Δ" + v.SurpriseDelta.ToString("+0.0;-0.0;0.0", inv) + "p(≤ " + PlutoBriefOutcomeAttributionService.SevereDeltaPoints.ToString("0", inv) + "pt)"
                    + (string.IsNullOrEmpty(v.FailureDimension) ? "" : " · 약점 " + v.FailureDimension)
                    + (v.PostMortemRecorded ? " · 🛡️ 오답 노트 벌점 " + v.PostMortemKeys.Count.ToString(inv) + "개 조합" : " · 형제 편 기존 판정 유지");
                if (Record(PlutoActivityKind.BriefPostMortem, msg, "brief:" + v.ClipVideoId)) added++;
                continue;
            }
            graded++;
            firstGraded ??= v.ClipVideoId;
        }
        if (graded > 0)
        {
            var hits = 0;
            foreach (var v in sweep.Verdicts) if (v is not null && !v.IsSevere && v.IsHit) hits++;
            var msg = "기획안 실측 채점 " + graded.ToString(inv) + "건 · 적중 " + hits.ToString(inv) + "건";
            if (Record(PlutoActivityKind.BriefGrading, msg, "grade:" + firstGraded + ":" + graded.ToString(inv))) added++;
        }
        if (sweep.RehabilitatedKeys.Count > 0)
        {
            var keys = new StringBuilder();
            for (var i = 0; i < sweep.RehabilitatedKeys.Count && i < 3; i++) keys.Append(i > 0 ? ", " : "").Append(sweep.RehabilitatedKeys[i]);
            if (sweep.RehabilitatedKeys.Count > 3) keys.Append(" 외 ").Append((sweep.RehabilitatedKeys.Count - 3).ToString(inv)).Append("개");
            var msg = "유기적 복권(≥ +" + PlutoMistakeNotebookService.RehabilitationDeltaPoints.ToString("0", inv) + "pt) · 벌점 감쇠 " + sweep.RehabilitatedKeys.Count.ToString(inv) + "개 · " + keys;
            if (Record(PlutoActivityKind.OrganicRehabilitation, msg)) added++;
        }
        return added;
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_disposed) return;
            _entries.Clear();
            _lastSweep = null;
        }
        SchedulePersist();
        RaiseChanged();
        await FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task t;
            lock (_sync) { t = _persistTask; if (!_persistRunning && !_persistDirty) return; }
            await t.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // ── 영속(코얼레싱 · 원자 교체 · 실패 격리) ──

    private void SchedulePersist()
    {
        if (StorePath is null) return;
        lock (_sync)
        {
            _persistDirty = true;
            if (_persistRunning) return;
            _persistRunning = true;
            _persistTask = Task.Run(PersistLoopAsync);
        }
    }

    private async Task PersistLoopAsync()
    {
        while (true)
        {
            PersistDto dto;
            lock (_sync)
            {
                if (!_persistDirty) { _persistRunning = false; return; }
                _persistDirty = false;
                dto = Snapshot();
            }
            await _io.WaitAsync().ConfigureAwait(false);
            try { WriteAtomic(StorePath!, dto); LastPersistError = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or JsonException)
            {
                LastPersistError = ex.GetType().Name;   // [부분 구제] 메모리 피드는 유지
                System.Diagnostics.Debug.WriteLine("[Pluto.ActivityFeed] persist: " + ex.GetType().Name);
            }
            finally { _io.Release(); }
        }
    }

    private PersistDto Snapshot()
    {
        var items = new List<EntryDto>(_entries.Count);
        foreach (var e in _entries) items.Add(new EntryDto { Seq = e.Seq, At = e.AtUtc, Kind = e.Kind.ToString(), Msg = e.MessageKorean, Key = e.DedupeKey });
        return new PersistDto { V = SchemaVersion, NextSeq = _nextSeq, Entries = items };
    }

    private static void WriteAtomic(string path, PersistDto dto)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(dto));
        File.Move(tmp, path, overwrite: true);
    }

    private void Load()
    {
        try
        {
            if (StorePath is null || !File.Exists(StorePath)) return;
            var dto = JsonSerializer.Deserialize<PersistDto>(File.ReadAllBytes(StorePath));
            if (dto?.Entries is not { } list) return;
            long maxSeq = 0;
            var loaded = new List<PlutoActivityEntry>(list.Count);
            foreach (var e in list)
            {
                if (e is null || !Enum.TryParse<PlutoActivityKind>(e.Kind, ignoreCase: false, out var kind) || !Enum.IsDefined(kind)) continue;
                var msg = Redact(e.Msg);   // 🔒 적재 시 재가림 — 구버전 · 수동 수정 파일의 경로/비밀값도 화면에 나오지 않음
                if (msg.Length == 0) continue;
                loaded.Add(new PlutoActivityEntry(e.Seq, e.At, kind, msg) { DedupeKey = NormalizeKey(e.Key) });
                if (e.Seq > maxSeq) maxSeq = e.Seq;
            }
            loaded.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
            var skip = Math.Max(0, loaded.Count - Capacity);
            lock (_sync)
            {
                for (var i = skip; i < loaded.Count; i++) _entries.Add(loaded[i]);
                _nextSeq = Math.Max(Math.Max(dto.NextSeq, maxSeq + 1), 1);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.ActivityFeed] load: " + ex.GetType().Name);   // 손상 → 빈 피드로 시작(다음 기록이 덮어씀)
        }
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.ActivityFeed] subscriber: " + ex.GetType().Name); }   // 끊긴 회로 구독자 격리
        }
    }

    private sealed class PersistDto
    {
        public int V { get; set; }
        public long NextSeq { get; set; }
        public List<EntryDto>? Entries { get; set; }
    }

    private sealed class EntryDto
    {
        public long Seq { get; set; }
        public DateTimeOffset At { get; set; }
        public string? Kind { get; set; }
        public string? Msg { get; set; }
        public string? Key { get; set; }
    }

    public void Dispose()
    {
        Task t;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            t = _persistTask;
        }
        Changed = null;
        try { t.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }   // 종료 직전 마지막 기록 보존(상한)
    }

    public async ValueTask DisposeAsync()
    {
        Task t;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            t = _persistTask;
        }
        Changed = null;
        try { await t.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (TimeoutException) { }
    }
}
