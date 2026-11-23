using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Timer = System.Threading.Timer;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📥 Step 23 — 다운로드 폴더 자동 수집 1건(학습 영향이 있을 때만 발행).</summary>
public sealed record PlutoDownloadsAutoIngestEvent(string FileName, string Sha256Hex, PlutoTelemetryBatchImportResult Result, DateTimeOffset AtUtc)
{
    public int NewlyLearned => Result.MatchedAndLearnedCount;
    public int Matured => Result.MaturityDeltaCount;
    public bool HasImpact => NewlyLearned + Matured > 0;
    /// <summary>📦 Step 37 — 이번 수집 직후 <c>Pluto_Ingested/</c> 로 정리됨(자율 활동 피드 표기용).</summary>
    public bool Archived { get; init; }

    /// <summary>토스트 1줄.</summary>
    public string ToastKorean => "📥 유튜브 자동 수집 완료: 신규 " + NewlyLearned.ToString(CultureInfo.InvariantCulture) + "건 · 성숙 보정 "
        + Matured.ToString(CultureInfo.InvariantCulture) + "건 (" + FileName + ")";
}

/// <summary>📥 Step 23 감시기 옵션 — 테스트는 <see cref="Directory"/>/<see cref="StatePath"/>로 격리.</summary>
public sealed class YouTubeDownloadsWatcherOptions
{
    /// <summary>감시 폴더(명시 시 격리 차단 무시). null → 사용자 Downloads.</summary>
    public string? Directory { get; init; }
    /// <summary>해시 장부·토글 상태 파일. null → <c>AppDataPaths/PromptFlowStudio/Pluto/downloads_watcher.json</c>.</summary>
    public string? StatePath { get; init; }
    public TimeSpan Debounce { get; init; } = TimeSpan.FromMilliseconds(1300);
    /// <summary>주기 재조정 스윕(≤0 → 끔 · 시작/오버플로 스윕은 유지).</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(60);
    public int ReadinessRetries { get; init; } = 8;
    public TimeSpan ReadinessRetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>크기 안정성 확인 간격.</summary>
    public TimeSpan StabilityProbe { get; init; } = TimeSpan.FromMilliseconds(150);
    /// <summary>스윕 대상 최대 경과(오래된 내보내기 일괄 재수집 방지).</summary>
    public TimeSpan MaxFileAge { get; init; } = TimeSpan.FromDays(30);
    /// <summary>동일 해시 일시 실패 허용 횟수 — 초과 시 거부 장부로 격리(스윕마다 재시도 폭주 차단).</summary>
    public int MaxFailuresPerFile { get; init; } = 3;
    public string Platform { get; init; } = "YouTube Shorts";
    /// <summary>FileSystemWatcher 사용(false → 스윕 전용 · 모바일/미지원 OS 폴백).</summary>
    public bool UseFileSystemWatcher { get; init; } = true;
    /// <summary>🧹 Step 24 — 장부(수집 · 거부/격리 각각) 최대 항목 수(초과 → 가장 오래된 것부터 LRU 축출).</summary>
    public int LedgerMaxEntries { get; init; } = YouTubeDownloadsAutoWatcherService.LedgerCapacity;
    /// <summary>🧹 Step 24 — 장부 TTL. 실효값은 항상 <see cref="MaxFileAge"/> + 1일 이상(스윕 대상 파일의 해시를 먼저 잊어 재수집하는 일 방지).</summary>
    public TimeSpan LedgerTtl { get; init; } = TimeSpan.FromDays(90);
    /// <summary>시계(테스트 주입 · null → UTC 현재).</summary>
    public Func<DateTimeOffset>? Clock { get; init; }
}

/// <summary>🩺 Step 24 — 감시기 상태 스냅샷.</summary>
public sealed record PlutoDownloadsWatcherHealth(bool IsEnabled, bool IsRunning, bool IsIsolationBlocked, string? WatchedDirectory, int IngestedLedgerCount,
    int IgnoredLedgerCount, int QuarantinedCount, int PendingFailureCount, long PrunedTotal, long IngestedCount, long OverflowRecoveryCount, DateTimeOffset? LastIngestUtc, string? LastError)
{
    /// <summary>한국어 1줄.</summary>
    public string StatusKorean
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var s = IsIsolationBlocked ? "🔒 격리(감시 안 함)" : !IsEnabled ? "⏸ 꺼짐" : IsRunning ? "👁️ 감시 중" : "⚠ 폴더 없음/미기동";
            s += " · 장부 " + IngestedLedgerCount.ToString(inv) + "건";
            if (QuarantinedCount > 0) s += " · 🧯 격리 " + QuarantinedCount.ToString(inv) + "건";
            if (PendingFailureCount > 0) s += " · 재시도 대기 " + PendingFailureCount.ToString(inv) + "건";
            if (LastIngestUtc is { } t) s += " · 최근 수집 " + t.ToLocalTime().ToString("MM-dd HH:mm", inv);
            if (ArchiveEnabled) s += " · 📦 정리 " + ArchivedCount.ToString(inv) + "건";
            return s;
        }
    }

    /// <summary>📦 Step 30 — 수집 후 폴더 정리 토글.</summary>
    public bool ArchiveEnabled { get; init; }
    /// <summary>📦 Step 30 — 이번 세션 정리(이동) 건수.</summary>
    public long ArchivedCount { get; init; }
}

/// <summary>📥 Step 23 — Downloads 폴더 YouTube Studio 내보내기 자동 감시 · 디바운스 · SHA-256 중복 차단 · 자동 학습.</summary>
public interface IYouTubeDownloadsAutoWatcherService : IDisposable, IAsyncDisposable
{
    /// <summary>사용자 토글(영속 · 기본 ON).</summary>
    bool IsEnabled { get; }
    bool IsRunning { get; }
    /// <summary>격리 환경변수로 실 Downloads 감시가 차단됨.</summary>
    bool IsIsolationBlocked { get; }
    string? WatchedDirectory { get; }
    long IngestedCount { get; }
    long OverflowRecoveryCount { get; }
    long SuppressedNoImpactCount { get; }

    /// <summary>새 학습(신규/성숙)이 있을 때만 발행. 구독자 예외는 개별 격리.</summary>
    event Action<PlutoDownloadsAutoIngestEvent>? OnExportAutoIngested;
    /// <summary>토글/실행 상태 변경.</summary>
    event Action? StateChanged;

    /// <summary>활성 · 격리 통과 시 감시 시작(멱등). 시작됐으면 true.</summary>
    bool Start();
    void Stop();
    /// <summary>토글 영속 → 켜면 시작 · 끄면 정지.</summary>
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
    /// <summary>폴더 즉시 재조정 → 준비된 파일 처리. 반환 = 이번에 학습 영향이 있던 파일 수.</summary>
    Task<int> SweepNowAsync(CancellationToken ct = default);
    bool IsKnownHash(string sha256Hex);

    /// <summary>
    /// 🔒 수동 가져오기 직렬화 — 감시기와 같은 단일 게이트 안에서 <paramref name="import"/> 실행 후 해시를 장부에 기록
    /// (같은 파일을 수동 클릭 + 백그라운드 스윕이 동시에 이중 반영하거나 뒤늦게 다시 토스트하는 경합 차단).
    /// </summary>
    Task<T> RunExclusiveImportAsync<T>(byte[] bytes, string? fileName, Func<CancellationToken, Task<T>> import, CancellationToken ct = default);

    /// <summary>감시기 오류(버퍼 오버플로 등) 처리 — 즉시 전체 재스캔 · 감시기 재구성.</summary>
    void HandleWatcherFault(Exception? error);

    /// <summary>🧯 Step 24 — 격리(형식 오류 · 일시 실패 상한 초과) 해시와 재시도 카운터를 지우고 즉시 재스캔. 반환 = 해제한 격리 수.</summary>
    Task<int> ClearQuarantinedAsync(CancellationToken ct = default);

    /// <summary>🧹 Step 24 — TTL/상한 장부 정리 후 영속. 반환 = 제거 수.</summary>
    Task<int> PruneLedgerAsync(CancellationToken ct = default);

    /// <summary>🩺 Step 24 — 상태 스냅샷.</summary>
    PlutoDownloadsWatcherHealth GetHealth();

    /// <summary>📦 Step 30 — 수집 완료 내보내기를 <c>{감시 폴더}/Pluto_Ingested/</c> 로 정리(영속 · 기본 OFF). 미구현 → false.</summary>
    bool ArchiveIngestedExports => false;
    /// <summary>📦 Step 30 — 정리 토글 영속(미구현 → 무동작).</summary>
    Task SetArchiveIngestedExportsAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>📦 Step 30 — 이번 세션 정리(이동) 건수.</summary>
    long ArchivedCount => 0;
}

/// <summary>📦 Step 30 — 수집 후 폴더 정리 1건 판정.</summary>
public enum PlutoIngestArchiveOutcome
{
    /// <summary><c>Pluto_Ingested/</c> 로 이동함.</summary>
    Archived = 0,
    /// <summary>토글 꺼짐(기본) — 아무것도 하지 않음.</summary>
    Disabled = 1,
    /// <summary>원본이 이미 없음(사용자가 옮김/삭제).</summary>
    SourceMissing = 2,
    /// <summary>🧬 수집 후 같은 이름으로 다른 내용이 덮어써짐 → 수집 안 된 내용이므로 이동 거부.</summary>
    ContentChanged = 3,
    /// <summary>다른 프로세스가 쓰는 중/잠김 → 그대로 둠.</summary>
    Locked = 4,
    /// <summary>정리 폴더가 파일 · 심볼릭 링크/정션이거나 원본이 링크 · 감시 폴더 밖 → 거부.</summary>
    Unsafe = 5,
    /// <summary>🧯 장부 · 미매칭 대기열 영속 실패 → 복구 단서 보존을 위해 이동 거부.</summary>
    NotDurable = 6,
    /// <summary>같은 이름 접미(<c>_2</c>…<c>_99</c>) 소진.</summary>
    NameExhausted = 7,
    /// <summary>그 밖 IO 실패(격리됨).</summary>
    Failed = 8,
}

/// <summary>
/// 📥 <see cref="IYouTubeDownloadsAutoWatcherService"/> 순수 BCL 구현.
/// FileSystemWatcher(Created/Renamed/Changed) + 주기 스윕 이중 탐지 → 파일별 디바운스 → 크기 안정 · <c>FileShare.Read</c> 열기 재시도 →
/// SHA-256(영속 장부) → 스키마 확인(비 YouTube 조용히 무시) → 단일 게이트 안에서 Step 22 수집기 호출.
/// </summary>
public sealed class YouTubeDownloadsAutoWatcherService : IYouTubeDownloadsAutoWatcherService
{
    public const string DisableEnvVar = "PROMPTFLOW_DISABLE_DOWNLOADS_WATCHER";
    /// <summary>🧹 Step 24 — 장부별 기본 상한(Step 23의 4096 → 500 · 수년치 일일 사용도 수십 KB).</summary>
    public const int LedgerCapacity = 500;
    public const int StateSchemaVersion = 2;

    private readonly IPlutoTelemetryBatchImporter _importer;
    private readonly YouTubeDownloadsWatcherOptions _opt;
    private readonly string _statePath;
    private readonly bool _explicitDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);          // 수집 단일 게이트(감시 · 스윕 · 수동)
    private readonly SemaphoreSlim _stateIo = new(1, 1);       // 장부 파일 쓰기 직렬화
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.OrdinalIgnoreCase);    // 경로 → 디바운스 만기(TickCount64)
    private readonly ConcurrentDictionary<string, byte> _inFlightHashes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _ignoredSignatures = new(StringComparer.OrdinalIgnoreCase);   // 경로 → 크기|mtime (비 YouTube · 기처리)
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LedgerEntry> _ingested = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LedgerEntry> _rejected = new(StringComparer.Ordinal);
    private long _prunedTotal;
    private bool _pruneDirty;   // 정리됐으나 아직 디스크 미반영(적재 직후 정리 등)
    private DateTimeOffset? _lastIngestUtc;
    private string? _lastError;

    /// <summary>장부 항목 — 기록 시각(UTC ticks) · 격리 여부(거부 장부만 · false = 비 YouTube 무시).</summary>
    private readonly record struct LedgerEntry(long AtTicks, bool Quarantined);
    private readonly CancellationTokenSource _life = new();

    private FileSystemWatcher? _fsw;
    private Timer? _debounceTimer;
    private Timer? _sweepTimer;
    private int _pumping;
    private int _sweeping;
    private bool _enabled = true;
    private bool _running;
    private bool _disposed;
    private long _ingestedCount, _overflowRecoveries, _suppressed;
    private Task _lastWork = Task.CompletedTask;

    public YouTubeDownloadsAutoWatcherService(IPlutoTelemetryBatchImporter importer, YouTubeDownloadsWatcherOptions? options = null)
        : this(importer, options, null) { }

    /// <summary>🎯 Step 29 — <paramref name="briefOutcomes"/> 연결 시 학습 영향이 있는 수집(자동 · 수동) 직후 기획 채점 · 유기적 복권 스윕.</summary>
    public YouTubeDownloadsAutoWatcherService(IPlutoTelemetryBatchImporter importer, YouTubeDownloadsWatcherOptions? options, IPlutoBriefOutcomeAttributionService? briefOutcomes)
    {
        _briefOutcomes = briefOutcomes;
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _opt = options ?? new YouTubeDownloadsWatcherOptions();
        _explicitDirectory = !string.IsNullOrWhiteSpace(_opt.Directory);
        WatchedDirectory = _explicitDirectory ? _opt.Directory!.Trim() : IsIsolatedByEnvironment() ? null : DefaultDownloadsDirectory();   // 🔒 격리 → 실 경로 미해석
        _statePath = string.IsNullOrWhiteSpace(_opt.StatePath)
            ? Path.Combine(Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", "Pluto", "downloads_watcher.json")
            : _opt.StatePath!;
        LoadState();
        lock (_sync) PruneLocked();   // 🧹 적재 직후 정리(다음 기록 때 디스크에도 반영)
    }

    public bool IsEnabled { get { lock (_sync) return _enabled; } }
    public bool IsRunning { get { lock (_sync) return _running; } }
    public bool IsIsolationBlocked => !_explicitDirectory && IsIsolatedByEnvironment();
    public string? WatchedDirectory { get; }
    public long IngestedCount => Interlocked.Read(ref _ingestedCount);
    public long OverflowRecoveryCount => Interlocked.Read(ref _overflowRecoveries);
    public long SuppressedNoImpactCount => Interlocked.Read(ref _suppressed);

    public event Action<PlutoDownloadsAutoIngestEvent>? OnExportAutoIngested;
    public event Action? StateChanged;

    /// <summary>사용자 Downloads(존재할 때만).</summary>
    public static string? DefaultDownloadsDirectory()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home)) return null;
            var d = Path.Combine(home, "Downloads");
            return System.IO.Directory.Exists(d) ? d : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }

    /// <summary>🔒 <c>PROMPTFLOW_PLUTO_DRIVE=off</c> 또는 <c>PROMPTFLOW_DISABLE_DOWNLOADS_WATCHER=1</c>.</summary>
    public static bool IsIsolatedByEnvironment()
    {
        var drive = Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar);
        if (drive is not null && drive.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)) return true;
        var dis = Environment.GetEnvironmentVariable(DisableEnvVar);
        return dis is not null && (dis.Trim() == "1" || dis.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>부분 다운로드 · 임시 · 비 대상 확장자 판별(이름만).</summary>
    public static bool IsCandidateName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var name = Path.GetFileName(path);
        if (name.Length == 0 || name[0] == '.' || name.StartsWith("~$", StringComparison.Ordinal)) return false;
        var ext = Path.GetExtension(name);
        return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);
        // .crdownload/.part/.tmp/.download 는 확장자에서 걸러지고, 완료 후 Renamed 이벤트로 최종 이름이 들어온다.
    }

    public static string ComputeSha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public bool IsKnownHash(string sha256Hex)
    {
        lock (_sync) return _ingested.ContainsKey(sha256Hex) || _rejected.ContainsKey(sha256Hex);
    }

    // ── 수명 ──

    public bool Start()
    {
        lock (_sync)
        {
            if (_disposed || !_enabled) return false;
            if (_running) return true;
            if (IsIsolationBlocked) return false;   // 🔒 테스트/오프 환경 → 실 Downloads 무접촉
            var dir = WatchedDirectory;
            if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir)) return false;
            _debounceTimer ??= new Timer(static s => ((YouTubeDownloadsAutoWatcherService)s!).OnDebounceTick(), this, Timeout.Infinite, Timeout.Infinite);
            if (_opt.SweepInterval > TimeSpan.Zero)
                _sweepTimer ??= new Timer(static s => ((YouTubeDownloadsAutoWatcherService)s!).OnSweepTick(), this, _opt.SweepInterval, _opt.SweepInterval);
            if (_opt.UseFileSystemWatcher) CreateWatcherLocked(dir);
            _running = true;
        }
        RaiseStateChanged();
        OnSweepTick();   // ▶ 시작 재조정(꺼져 있는 동안 받은 파일)
        return true;
    }

    public void Stop()
    {
        bool changed;
        lock (_sync)
        {
            changed = _running;
            _running = false;
            DisposeWatcherLocked();
            _sweepTimer?.Dispose(); _sweepTimer = null;
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _pending.Clear();
        }
        if (changed) RaiseStateChanged();
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken ct = default)
    {
        bool changed;
        lock (_sync) { changed = _enabled != enabled; _enabled = enabled; }
        if (changed) await SaveStateAsync(ct).ConfigureAwait(false);
        if (enabled) Start(); else Stop();
        if (changed) RaiseStateChanged();
    }

    private void CreateWatcherLocked(string dir)
    {
        DisposeWatcherLocked();
        try
        {
            var w = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,   // 최대치 — 대량 다운로드 버스트 대비(오버플로는 재스캔으로 복구)
            };
            w.Created += OnFsEvent;
            w.Changed += OnFsEvent;
            w.Renamed += OnFsRenamed;
            w.Error += OnFsError;
            w.EnableRaisingEvents = true;
            _fsw = w;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] watcher unavailable → sweep-only: " + ex.Message);
            _fsw = null;   // 스윕 전용 폴백
        }
    }

    private void DisposeWatcherLocked()
    {
        var w = _fsw;
        _fsw = null;
        if (w is null) return;
        try
        {
            w.EnableRaisingEvents = false;
            w.Created -= OnFsEvent; w.Changed -= OnFsEvent; w.Renamed -= OnFsRenamed; w.Error -= OnFsError;
            w.Dispose();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException) { }
    }

    // ── 이벤트 → 디바운스 ──

    private void OnFsEvent(object? sender, FileSystemEventArgs e) => Enqueue(e.FullPath);
    private void OnFsRenamed(object? sender, RenamedEventArgs e) => Enqueue(e.FullPath);   // .crdownload → 최종 이름
    private void OnFsError(object? sender, ErrorEventArgs e) => HandleWatcherFault(e.GetException());

    /// <summary>
    /// 🛟 자체 추가 ① — 오버플로(<see cref="InternalBufferOverflowException"/>)는 이벤트 유실을 뜻하므로 즉시 전체 재스캔,
    /// 그 밖의 오류(폴더 삭제/재마운트로 감시기 사망)는 감시기를 재구성한다. 어느 쪽도 예외를 밖으로 내지 않는다.
    /// </summary>
    public void HandleWatcherFault(Exception? error)
    {
        try
        {
            Interlocked.Increment(ref _overflowRecoveries);
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] fault → rescan: " + error?.Message);
            if (error is not InternalBufferOverflowException)
            {
                lock (_sync)
                {
                    if (_running && !_disposed && _opt.UseFileSystemWatcher && WatchedDirectory is { } dir && System.IO.Directory.Exists(dir))
                        CreateWatcherLocked(dir);
                }
            }
            OnSweepTick();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] fault recovery: " + ex.Message); }
    }

    private void Enqueue(string? path)
    {
        if (!IsCandidateName(path)) return;
        lock (_sync) { if (!_running || _disposed) return; }
        var due = Environment.TickCount64 + (long)_opt.Debounce.TotalMilliseconds;
        _pending[path!] = due;   // 연속 이벤트 → 만기 연장(파일별 디바운스)
        ArmDebounce(_opt.Debounce);
    }

    private void ArmDebounce(TimeSpan delay)
    {
        try { _debounceTimer?.Change(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private void OnDebounceTick()
    {
        if (Interlocked.Exchange(ref _pumping, 1) == 1) return;   // 이미 펌프 중 → 끝에서 재무장
        _lastWork = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            var now = Environment.TickCount64;
            var due = new List<string>();
            foreach (var kv in _pending) if (kv.Value <= now) due.Add(kv.Key);
            for (var i = 0; i < due.Count; i++)
            {
                if (_life.IsCancellationRequested) return;
                if (!_pending.TryRemove(due[i], out _)) continue;
                await ProcessPathAsync(due[i], _life.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] pump: " + ex.Message); }
        finally
        {
            Volatile.Write(ref _pumping, 0);
            if (!_pending.IsEmpty && !_life.IsCancellationRequested)
            {
                var min = long.MaxValue;
                foreach (var kv in _pending) if (kv.Value < min) min = kv.Value;
                ArmDebounce(TimeSpan.FromMilliseconds(Math.Max(10, min - Environment.TickCount64)));
            }
        }
    }

    // ── 스윕 ──

    private void OnSweepTick()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            var dir = WatchedDirectory;
            lock (_sync) { if (!_running || _disposed) return; }
            if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir)) return;
            var cutoff = DateTime.UtcNow - _opt.MaxFileAge;
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in System.IO.Directory.EnumerateFiles(dir))
            {
                if (!IsCandidateName(f)) continue;
                present.Add(f);
                FileInfo fi;
                try { fi = new FileInfo(f); if (fi.LastWriteTimeUtc < cutoff) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (_ignoredSignatures.TryGetValue(f, out var sig) && sig == Signature(fi)) continue;   // 이미 판정된 동일 파일 → 재열기 없음
                if (!_pending.ContainsKey(f)) { _pending[f] = Environment.TickCount64; }
            }
            PruneIgnoredSignatures(present);
            if (!_pending.IsEmpty) ArmDebounce(TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ObjectDisposedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] sweep: " + ex.Message);
        }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    public async Task<int> SweepNowAsync(CancellationToken ct = default)
    {
        var dir = WatchedDirectory;
        lock (_sync) { if (!_running || _disposed) return 0; }
        if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir)) return 0;
        var files = new List<string>();
        foreach (var f in System.IO.Directory.EnumerateFiles(dir)) if (IsCandidateName(f)) files.Add(f);
        var impact = 0;
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            _pending.TryRemove(files[i], out _);
            if (await ProcessPathAsync(files[i], ct).ConfigureAwait(false)) impact++;
        }
        return impact;
    }

    private static string Signature(FileInfo fi) => fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    // ── 준비 게이트 → 수집 ──

    /// <summary>파일 1건 처리. 학습 영향 이벤트를 발행했으면 true → 🎯 Step 29 수집 게이트 해제 후 기획 채점 · 유기적 복권 스윕(격리).</summary>
    private async Task<bool> ProcessPathAsync(string path, CancellationToken ct)
    {
        var impact = await ProcessPathCoreAsync(path, ct).ConfigureAwait(false);
        if (impact) await TriggerPostIngestSweepAsync().ConfigureAwait(false);
        return impact;
    }

    private async Task<bool> ProcessPathCoreAsync(string path, CancellationToken ct)
    {
        var bytes = await ReadWhenReadyAsync(path, ct).ConfigureAwait(false);
        if (bytes is null) return false;
        var name = Path.GetFileName(path);
        var sha = ComputeSha256Hex(bytes);
        if (IsKnownHash(sha)) { Remember(path); return false; }   // ♻️ 재시작 후에도 같은 내보내기 무시
        if (!_inFlightHashes.TryAdd(sha, 0)) return false;          // 이벤트 + 스윕 동시 진입
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (IsKnownHash(sha)) { Remember(path); return false; }   // 게이트 대기 중 수동 가져오기가 처리함
                if (!LooksLikeStudioExport(bytes, name))
                {
                    await RecordAsync(sha, rejected: true, ct).ConfigureAwait(false);   // 🔇 비 YouTube → 조용히 무시(재열기 없음)
                    Remember(path);
                    return false;
                }
                PlutoTelemetryBatchImportResult r;
                try { r = await _importer.ImportYouTubeStudioFileAsync(bytes, name, _opt.Platform, ct).ConfigureAwait(false); }
                catch (InvalidDataException ex)
                {
                    _lastError = name + ": " + ex.Message;
                    await RecordAsync(sha, rejected: true, ct, quarantined: true).ConfigureAwait(false);
                    Remember(path);
                    return false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] import " + name + ": " + ex.Message);
                    _lastError = name + ": " + ex.Message;
                    if (CountFailure(sha) >= Math.Max(1, _opt.MaxFailuresPerFile)) { await RecordAsync(sha, rejected: true, ct, quarantined: true).ConfigureAwait(false); Remember(path); }
                    return false;   // 일시 실패 → 다음 스윕에서 재시도(상한 후 격리)
                }
                var ledgerDurable = await RecordAsync(sha, rejected: false, ct).ConfigureAwait(false);
                Remember(path);
                var archived = ArchiveAfterIngest(path, sha, r, ledgerDurable);   // 📦 Step 30 — 완전 수집 · 영속 확인 후에만(기본 OFF · 실패 격리)
                Interlocked.Increment(ref _ingestedCount);
                _lastIngestUtc = Now();
                var evt = new PlutoDownloadsAutoIngestEvent(name, sha, r, DateTimeOffset.UtcNow) { Archived = archived };
                if (!evt.HasImpact) { Interlocked.Increment(ref _suppressed); return false; }   // 🔕 0건 학습 → 토스트 생략
                RaiseIngested(evt);
                return true;
            }
            finally { _gate.Release(); }
        }
        finally { _inFlightHashes.TryRemove(sha, out _); }
    }

    private void Remember(string path)
    {
        try { var fi = new FileInfo(path); if (fi.Exists) _ignoredSignatures[path] = Signature(fi); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private int CountFailure(string sha)
    {
        lock (_sync)
        {
            if (!_failures.ContainsKey(sha) && _failures.Count >= MaxLedger) _failures.Clear();   // 🧹 유계(손상 파일 대량 유입에도 메모리 상한)
            _failures.TryGetValue(sha, out var n); _failures[sha] = ++n; return n;
        }
    }

    /// <summary>
    /// 브라우저 다운로드 준비 게이트 — 숨김/시스템/0바이트/상한 초과 제외 · 크기·mtime 안정 · <c>FileShare.Read</c> 열기(쓰기 중이면 공유 위반) 재시도.
    /// 끝내 준비되지 않으면 null(오류 토스트 없음 · 다음 이벤트/스윕에서 재시도).
    /// </summary>
    public async Task<byte[]?> ReadWhenReadyAsync(string path, CancellationToken ct)
    {
        var attempts = Math.Max(1, _opt.ReadinessRetries);
        for (var i = 0; i < attempts; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) return null;
                if (fi.Length <= 0) { if (i == attempts - 1) return null; await Task.Delay(_opt.ReadinessRetryDelay, ct).ConfigureAwait(false); continue; }
                if (fi.Length > YouTubeStudioAnalyticsNormalizer.MaxInputBytes) { Remember(path); return null; }
                var len = fi.Length; var mt = fi.LastWriteTimeUtc;
                await Task.Delay(_opt.StabilityProbe, ct).ConfigureAwait(false);
                fi.Refresh();
                if (!fi.Exists) return null;
                if (fi.Length != len || fi.LastWriteTimeUtc != mt) { await Task.Delay(_opt.ReadinessRetryDelay, ct).ConfigureAwait(false); continue; }   // 아직 기록 중
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (fs.Length > YouTubeStudioAnalyticsNormalizer.MaxInputBytes) return null;
                var buf = new byte[fs.Length];
                var read = 0;
                while (read < buf.Length)
                {
                    var n = await fs.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
                    if (n == 0) break;
                    read += n;
                }
                if (read != buf.Length) { await Task.Delay(_opt.ReadinessRetryDelay, ct).ConfigureAwait(false); continue; }
                return buf;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (ex is FileNotFoundException or DirectoryNotFoundException) return null;
                await Task.Delay(_opt.ReadinessRetryDelay, ct).ConfigureAwait(false);   // 🔒 잠김 → 유계 재시도
            }
        }
        return null;
    }

    /// <summary>🔇 비 YouTube 판별 — 이름 서명 또는 CSV 헤더 스키마(조회수 + 유지/CTR 열) 엿보기. ZIP은 이름 서명 필수.</summary>
    public static bool LooksLikeStudioExport(byte[] bytes, string? fileName)
    {
        try
        {
            var byName = YouTubeStudioAnalyticsNormalizer.LooksLikeStudioExportName(fileName);
            if (YouTubeStudioAnalyticsNormalizer.LooksLikeZip(bytes))
                return byName && YouTubeStudioAnalyticsNormalizer.Parse(bytes, fileName).HeaderRecognized;
            var peekLen = Math.Min(bytes.Length, 8192);
            var head = YouTubeStudioAnalyticsNormalizer.DecodeText(bytes.AsSpan(0, peekLen).ToArray());
            var nl = head.IndexOf('\n');
            var first = (nl >= 0 ? head.Substring(0, nl) : head).Trim('\r', '﻿', ' ');
            if (first.Length == 0) return false;
            var cols = YouTubeStudioAnalyticsNormalizer.MapHeader(SplitHeader(first), out _);
            return cols.IsTable;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or System.Text.DecoderFallbackException)
        {
            return false;
        }
    }

    private static string[] SplitHeader(string line)
    {
        var delim = line.IndexOf('\t') >= 0 ? '\t' : line.IndexOf(';') >= 0 && line.IndexOf(',') < 0 ? ';' : ',';
        var parts = new List<string>();
        var sb = new System.Text.StringBuilder();
        var q = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') { if (q && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else q = !q; }
            else if (c == delim && !q) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        return parts.ToArray();
    }

    public async Task<T> RunExclusiveImportAsync<T>(byte[] bytes, string? fileName, Func<CancellationToken, Task<T>> import, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(import);
        var sha = ComputeSha256Hex(bytes);
        T result;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await import(ct).ConfigureAwait(false);
            await RecordAsync(sha, rejected: false, ct).ConfigureAwait(false);   // 수동 반영분 → 감시기 재수집/재토스트 차단
            result = r;
        }
        finally { _gate.Release(); }
        // 🎯 Step 29 — 수동 가져오기도 카탈로그 실측을 갱신 → 게이트 밖에서 채점 · 복권 스윕(학습 0건으로 확인된 결과만 생략)
        if (result is not PlutoTelemetryBatchImportResult br || br.MatchedAndLearnedCount + br.MaturityDeltaCount > 0)
            await TriggerPostIngestSweepAsync().ConfigureAwait(false);
        return result;
    }

    // ── 🎯 Step 29 — 수집 후 자동 채점 · 유기적 복권 스윕(단일 비행 · 후행 1회 재실행 · 실패 격리) ──

    private readonly IPlutoBriefOutcomeAttributionService? _briefOutcomes;
    private bool _postSweepRunning, _postSweepDirty;   // _sync 보호
    private Task _postSweepTask = Task.CompletedTask;
    private long _postSweepRuns, _postSweepFailures;

    /// <summary>수집 후 스윕 실행 횟수(코얼레싱 후).</summary>
    public long PostIngestSweepCount => Interlocked.Read(ref _postSweepRuns);
    /// <summary>수집 후 스윕 실패 횟수(격리됨 · 수집 장부 · 격리 판정에 영향 없음).</summary>
    public long PostIngestSweepFailureCount => Interlocked.Read(ref _postSweepFailures);
    /// <summary>마지막 수집 후 스윕 결과(없음 → null).</summary>
    public PlutoBriefSweepResult? LastPostIngestSweep { get; private set; }

    /// <summary>
    /// 단일 비행 — 실행 중이면 '더러움'만 표시하고 진행 중 작업을 반환(그 작업이 끝나기 전 후행 1회 재실행 → 트리거 유실 없음 · 무한 적층 없음).
    /// 반환 작업은 결코 실패하지 않는다(예외 격리 · 폐기 시 조용히 종료).
    /// </summary>
    private Task TriggerPostIngestSweepAsync()
    {
        if (_briefOutcomes is null) return Task.CompletedTask;
        lock (_sync)
        {
            if (_disposed) return Task.CompletedTask;
            if (_postSweepRunning) { _postSweepDirty = true; return _postSweepTask; }
            _postSweepRunning = true;
            _postSweepDirty = false;
            _postSweepTask = Task.Run(PostIngestSweepLoopAsync);
            return _postSweepTask;
        }
    }

    private async Task PostIngestSweepLoopAsync()
    {
        while (true)
        {
            CancellationToken token;
            try { token = _life.Token; }
            catch (ObjectDisposedException) { lock (_sync) _postSweepRunning = false; return; }
            try
            {
                var r = await _briefOutcomes!.EvaluatePendingBriefOutcomesAndRehabilitationAsync(token).ConfigureAwait(false);
                LastPostIngestSweep = r;
                Interlocked.Increment(ref _postSweepRuns);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { lock (_sync) _postSweepRunning = false; return; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _postSweepFailures);
                System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] post-ingest sweep: " + ex.Message);   // 콜백 예외 격리
            }
            lock (_sync)
            {
                if (!_postSweepDirty || _disposed) { _postSweepRunning = false; return; }
                _postSweepDirty = false;
            }
        }
    }

    // ── 📦 Step 30 — 수집 후 폴더 정리(선택 · 기본 OFF) ──

    /// <summary>정리 폴더 이름(감시 폴더 바로 아래 · 스윕/감시는 최상위만 보므로 재수집 대상 아님).</summary>
    public const string ArchiveFolderName = "Pluto_Ingested";
    /// <summary>같은 이름 충돌 접미 최대값(<c>_2</c> … <c>_99</c>).</summary>
    public const int MaxArchiveSuffix = 99;

    private bool _archive;   // _sync 보호
    private long _archivedCount, _archiveSkipped;

    public bool ArchiveIngestedExports { get { lock (_sync) return _archive; } }
    public long ArchivedCount => Interlocked.Read(ref _archivedCount);
    /// <summary>정리 대상이었지만 안전 규칙으로 건너뛴 건수(내용 변경 · 잠김 · 영속 실패 · 링크 등).</summary>
    public long ArchiveSkippedCount => Interlocked.Read(ref _archiveSkipped);
    /// <summary>마지막 정리 판정(없음 → null).</summary>
    public PlutoIngestArchiveOutcome? LastArchiveOutcome { get; private set; }

    public async Task SetArchiveIngestedExportsAsync(bool enabled, CancellationToken ct = default)
    {
        bool changed;
        lock (_sync) { changed = _archive != enabled; _archive = enabled; }
        if (!changed) return;
        await SaveStateAsync(ct).ConfigureAwait(false);
        RaiseStateChanged();
    }

    /// <summary>수집 게이트 안 · 장부 기록 직후 — 토글 ON + 영속 확인 시에만 이동(예외 비전파).</summary>
    private bool ArchiveAfterIngest(string path, string sha, PlutoTelemetryBatchImportResult result, bool ledgerDurable)
    {
        if (!ArchiveIngestedExports) return false;
        PlutoIngestArchiveOutcome outcome;
        try
        {
            outcome = !ledgerDurable || result.UnmatchedQueuePersistFailed
                ? PlutoIngestArchiveOutcome.NotDurable
                : TryArchiveIngestedFile(WatchedDirectory, path, sha, out _);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] archive: " + ex.Message);
            outcome = PlutoIngestArchiveOutcome.Failed;
        }
        LastArchiveOutcome = outcome;
        if (outcome == PlutoIngestArchiveOutcome.Archived)
        {
            Interlocked.Increment(ref _archivedCount);
            _ignoredSignatures.TryRemove(path, out _);   // 자리에 새 파일이 오면 다시 판정
            return true;
        }
        Interlocked.Increment(ref _archiveSkipped);
        return false;
    }

    /// <summary>
    /// 📦 순수 정리 이동(동기 · 예외 비전파) —
    /// ① 원본은 감시 폴더 <b>바로 아래</b>의 일반 파일만(링크 · 하위 폴더 · 다른 폴더 거부)
    /// ② <c>Pluto_Ingested</c> 가 파일이거나 재분석 지점(심볼릭 링크/정션)이면 거부(생성 직후 재확인)
    /// ③ 🧬 쓰기 거부 공유(<c>FileShare.Read | Delete</c>)로 연 채 SHA-256 재계산 → 수집한 해시와 다르면 거부(브라우저가 같은 이름으로 새 내보내기를 덮어쓴 경우)
    /// ④ 핸들을 쥔 채 <c>File.Move(overwrite: false)</c> — 대상이 파일 · 폴더 · 링크로 점유되면 <c>_2</c>, <c>_3</c> … 접미(덮어쓰기 없음).
    /// </summary>
    public static PlutoIngestArchiveOutcome TryArchiveIngestedFile(string? watchedDirectory, string path, string expectedSha256Hex, out string? archivedPath)
    {
        archivedPath = null;
        if (string.IsNullOrWhiteSpace(watchedDirectory) || string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(expectedSha256Hex)) return PlutoIngestArchiveOutcome.Unsafe;
        string root, src, archiveDir;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(watchedDirectory));
            src = Path.GetFullPath(path);
            var parent = Path.GetDirectoryName(src);
            if (parent is null || !string.Equals(Path.TrimEndingDirectorySeparator(parent), root, StringComparison.OrdinalIgnoreCase)) return PlutoIngestArchiveOutcome.Unsafe;
            if (!IsCandidateName(src)) return PlutoIngestArchiveOutcome.Unsafe;
            var fi = new FileInfo(src);
            if (!fi.Exists) return PlutoIngestArchiveOutcome.SourceMissing;
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) return PlutoIngestArchiveOutcome.Unsafe;   // 🔗 링크 자체를 옮기면 대상 내용 검증이 무의미
            archiveDir = Path.Combine(root, ArchiveFolderName);
            if (File.Exists(archiveDir)) return PlutoIngestArchiveOutcome.Unsafe;
            var di = new DirectoryInfo(archiveDir);
            if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0) return PlutoIngestArchiveOutcome.Unsafe;
            if (!di.Exists)
            {
                System.IO.Directory.CreateDirectory(archiveDir);
                di.Refresh();
                if (!di.Exists || (di.Attributes & FileAttributes.ReparsePoint) != 0) return PlutoIngestArchiveOutcome.Unsafe;   // 경합 — 그 사이 링크로 바뀜
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] archive prepare: " + ex.Message);
            return PlutoIngestArchiveOutcome.Failed;
        }

        FileStream? hold = null;
        try
        {
            try { hold = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.SequentialScan); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return PlutoIngestArchiveOutcome.SourceMissing; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return PlutoIngestArchiveOutcome.Locked; }
            var actual = Convert.ToHexString(SHA256.HashData(hold));
            if (!string.Equals(actual, expectedSha256Hex.Trim(), StringComparison.OrdinalIgnoreCase)) return PlutoIngestArchiveOutcome.ContentChanged;
            var stem = Path.GetFileNameWithoutExtension(src);
            var ext = Path.GetExtension(src);
            for (var suffix = 1; suffix <= MaxArchiveSuffix; suffix++)
            {
                var target = Path.Combine(archiveDir, suffix == 1 ? stem + ext : stem + "_" + suffix.ToString(CultureInfo.InvariantCulture) + ext);
                if (PlutoSystemDiagnosticService.IsPathOccupied(target)) continue;   // 🔒 파일 · 폴더 · 링크 → 다음 접미
                try { File.Move(src, target, overwrite: false); }
                catch (IOException) when (PlutoSystemDiagnosticService.IsPathOccupied(target)) { continue; }   // 경합 — 그 사이 생김
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return PlutoIngestArchiveOutcome.SourceMissing; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] archive move: " + ex.Message); return PlutoIngestArchiveOutcome.Locked; }
                archivedPath = target;
                return PlutoIngestArchiveOutcome.Archived;
            }
            return PlutoIngestArchiveOutcome.NameExhausted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] archive: " + ex.Message);
            return PlutoIngestArchiveOutcome.Failed;
        }
        finally { hold?.Dispose(); }
    }

    // ── 이벤트 발행(구독자 격리) ──

    /// <summary>
    /// 🧯 자체 추가 ③ — 구독자별 격리 호출. 회로가 끊긴 Blazor 페이지 핸들러가 <see cref="ObjectDisposedException"/>/
    /// <see cref="InvalidOperationException"/>을 던져도 다른 구독자 · 감시 루프 · 게이트 해제에 영향 없음.
    /// </summary>
    private void RaiseIngested(PlutoDownloadsAutoIngestEvent evt)
    {
        var h = OnExportAutoIngested;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action<PlutoDownloadsAutoIngestEvent>)d)(evt); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] subscriber: " + ex.Message); }
        }
    }

    private void RaiseStateChanged()
    {
        var h = StateChanged;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] state subscriber: " + ex.Message); }
        }
    }

    // ── 영속 장부(원자적 교체 · 유계) ──

    private DateTimeOffset Now() => _opt.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
    private int MaxLedger => Math.Max(1, _opt.LedgerMaxEntries);

    /// <summary>🧹 실효 TTL — 스윕 최대 경과(<see cref="YouTubeDownloadsWatcherOptions.MaxFileAge"/>) + 1일 미만으로 내려가지 않음.</summary>
    public TimeSpan EffectiveLedgerTtl
    {
        get
        {
            var floor = _opt.MaxFileAge + TimeSpan.FromDays(1);
            return _opt.LedgerTtl < floor ? floor : _opt.LedgerTtl;
        }
    }

    /// <summary>장부 기록 + 영속 — 디스크 반영 성공 → true(📦 Step 30 폴더 정리 거부권에 사용).</summary>
    private async Task<bool> RecordAsync(string sha, bool rejected, CancellationToken ct, bool quarantined = false)
    {
        lock (_sync)
        {
            var e = new LedgerEntry(Now().UtcTicks, quarantined);
            if (rejected) _rejected[sha] = e;
            else { _rejected.Remove(sha); _ingested[sha] = e; }
            _failures.Remove(sha);
            PruneLocked();
        }
        return await SaveStateAsync(ct).ConfigureAwait(false);
    }

    /// <summary>TTL 초과 제거 → 상한 초과분은 가장 오래된 것부터(LRU) 축출. 반환 = 제거 수.</summary>
    private int PruneLocked()
    {
        var cutoff = (Now() - EffectiveLedgerTtl).UtcTicks;
        var removed = PruneOne(_ingested, cutoff, MaxLedger) + PruneOne(_rejected, cutoff, MaxLedger);
        if (removed > 0) { _prunedTotal += removed; _pruneDirty = true; }
        return removed;
    }

    private static int PruneOne(Dictionary<string, LedgerEntry> map, long cutoffTicks, int max)
    {
        List<string>? stale = null;
        foreach (var kv in map) if (kv.Value.AtTicks < cutoffTicks) (stale ??= new List<string>()).Add(kv.Key);
        var removed = 0;
        if (stale is not null) foreach (var k in stale) if (map.Remove(k)) removed++;
        if (map.Count <= max) return removed;
        var order = SortedByAge(map);
        for (var i = 0; i < order.Count - max; i++) if (map.Remove(order[i].Key)) removed++;
        return removed;
    }

    private static List<KeyValuePair<string, LedgerEntry>> SortedByAge(Dictionary<string, LedgerEntry> map)
    {
        var order = new List<KeyValuePair<string, LedgerEntry>>(map);
        order.Sort(static (a, b) => { var d = a.Value.AtTicks.CompareTo(b.Value.AtTicks); return d != 0 ? d : string.CompareOrdinal(a.Key, b.Key); });   // 결정론 · 오래된 순
        return order;
    }

    /// <summary>🧹 사라진 파일의 판정 서명 제거(세션 메모리 유계).</summary>
    private void PruneIgnoredSignatures(HashSet<string> present)
    {
        foreach (var k in _ignoredSignatures.Keys) if (!present.Contains(k)) _ignoredSignatures.TryRemove(k, out _);
    }

    public async Task<int> PruneLedgerAsync(CancellationToken ct = default)
    {
        int removed;
        bool dirty;
        lock (_sync) { removed = PruneLocked(); dirty = _pruneDirty; }
        if (dirty) await SaveStateAsync(ct).ConfigureAwait(false);
        return removed;
    }

    public async Task<int> ClearQuarantinedAsync(CancellationToken ct = default)
    {
        int cleared;
        lock (_sync)
        {
            List<string>? q = null;
            foreach (var kv in _rejected) if (kv.Value.Quarantined) (q ??= new List<string>()).Add(kv.Key);
            cleared = q?.Count ?? 0;
            if (q is not null) foreach (var k in q) _rejected.Remove(k);
            cleared += _failures.Count;   // 격리 전 재시도 대기분도 초기화
            _failures.Clear();
            _lastError = null;
        }
        _ignoredSignatures.Clear();   // 고친 파일을 다음 스윕이 다시 연다(비 YouTube는 해시 장부로 재차단)
        await SaveStateAsync(ct).ConfigureAwait(false);
        RaiseStateChanged();
        if (IsRunning) OnSweepTick();
        return cleared;
    }

    public PlutoDownloadsWatcherHealth GetHealth()
    {
        lock (_sync)
        {
            var q = 0;
            foreach (var kv in _rejected) if (kv.Value.Quarantined) q++;
            return new PlutoDownloadsWatcherHealth(_enabled, _running, IsIsolationBlocked, WatchedDirectory, _ingested.Count, _rejected.Count - q, q, _failures.Count,
                _prunedTotal, IngestedCount, OverflowRecoveryCount, _lastIngestUtc, _lastError) { ArchiveEnabled = _archive, ArchivedCount = ArchivedCount };
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var node = JsonNode.Parse(File.ReadAllText(_statePath));
            if (node is not JsonObject o) return;
            if (o["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en)) _enabled = en;
            if (o["archive"] is JsonValue av && av.TryGetValue<bool>(out var ar)) _archive = ar;   // 📦 Step 30 — 없으면 기본 OFF(기존 동작 100% 보존)
            var now = Now().UtcTicks;
            Fill(o["ingested"] as JsonArray, _ingested, now);
            Fill(o["rejected"] as JsonArray, _rejected, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] state load (기본값 사용): " + ex.Message);
        }

        // v1: 문자열 배열(시각 없음 → 적재 시각으로 이관) · v2: { h, t, q }
        static void Fill(JsonArray? arr, Dictionary<string, LedgerEntry> map, long now)
        {
            if (arr is null) return;
            foreach (var n in arr)
            {
                string? h = null;
                var t = now;
                var q = false;
                if (n is JsonValue v && v.TryGetValue<string>(out var s1)) h = s1;
                else if (n is JsonObject eo)
                {
                    if (eo["h"] is JsonValue hv && hv.TryGetValue<string>(out var s2)) h = s2;
                    if (eo["t"] is JsonValue tv && tv.TryGetValue<long>(out var tt) && tt > 0 && tt <= DateTime.MaxValue.Ticks) t = tt;
                    if (eo["q"] is JsonValue qv && qv.TryGetValue<bool>(out var qq)) q = qq;
                }
                if (h is { Length: 64 }) map[h.ToUpperInvariant()] = new LedgerEntry(t, q);
            }
        }
    }

    private async Task<bool> SaveStateAsync(CancellationToken ct)
    {
        string json;
        lock (_sync)
        {
            _pruneDirty = false;
            json = new JsonObject { ["v"] = StateSchemaVersion, ["enabled"] = _enabled, ["archive"] = _archive, ["ingested"] = ToJson(_ingested), ["rejected"] = ToJson(_rejected) }.ToJsonString();
        }
        await _stateIo.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var tmp = _statePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _statePath, overwrite: true);   // 원자적 교체 — 중간 크래시에도 장부 파손 없음
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] state save: " + ex.Message);
            return false;
        }
        finally { _stateIo.Release(); }

        static JsonArray ToJson(Dictionary<string, LedgerEntry> map)
        {
            var arr = new JsonArray();
            foreach (var kv in SortedByAge(map))
            {
                var e = new JsonObject { ["h"] = kv.Key, ["t"] = kv.Value.AtTicks };
                if (kv.Value.Quarantined) e["q"] = true;
                arr.Add(e);
            }
            return arr;
        }
    }

    // ── 폐기 ──

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        _debounceTimer?.Dispose(); _debounceTimer = null;
        OnExportAutoIngested = null;
        StateChanged = null;
        _life.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        var work = _lastWork;
        lock (_sync) { if (_disposed) return; }
        Stop();
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        try { await work.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        Task post;
        lock (_sync) post = _postSweepTask;
        await post.ConfigureAwait(false);   // 🎯 Step 29 — 진행 중 수집 후 스윕 종료 대기(취소 · 예외 격리 → 실패 없음)
        Dispose();
    }
}
