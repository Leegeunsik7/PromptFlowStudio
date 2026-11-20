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
}

/// <summary>
/// 📥 <see cref="IYouTubeDownloadsAutoWatcherService"/> 순수 BCL 구현.
/// FileSystemWatcher(Created/Renamed/Changed) + 주기 스윕 이중 탐지 → 파일별 디바운스 → 크기 안정 · <c>FileShare.Read</c> 열기 재시도 →
/// SHA-256(영속 장부) → 스키마 확인(비 YouTube 조용히 무시) → 단일 게이트 안에서 Step 22 수집기 호출.
/// </summary>
public sealed class YouTubeDownloadsAutoWatcherService : IYouTubeDownloadsAutoWatcherService
{
    public const string DisableEnvVar = "PROMPTFLOW_DISABLE_DOWNLOADS_WATCHER";
    public const int LedgerCapacity = 4096;
    public const int StateSchemaVersion = 1;

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
    private readonly LinkedList<string> _ingestedOrder = new();
    private readonly HashSet<string> _ingested = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _rejectedOrder = new();
    private readonly HashSet<string> _rejected = new(StringComparer.Ordinal);
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
    {
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _opt = options ?? new YouTubeDownloadsWatcherOptions();
        _explicitDirectory = !string.IsNullOrWhiteSpace(_opt.Directory);
        WatchedDirectory = _explicitDirectory ? _opt.Directory!.Trim() : IsIsolatedByEnvironment() ? null : DefaultDownloadsDirectory();   // 🔒 격리 → 실 경로 미해석
        _statePath = string.IsNullOrWhiteSpace(_opt.StatePath)
            ? Path.Combine(Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", "Pluto", "downloads_watcher.json")
            : _opt.StatePath!;
        LoadState();
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
        lock (_sync) return _ingested.Contains(sha256Hex) || _rejected.Contains(sha256Hex);
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
            foreach (var f in System.IO.Directory.EnumerateFiles(dir))
            {
                if (!IsCandidateName(f)) continue;
                FileInfo fi;
                try { fi = new FileInfo(f); if (fi.LastWriteTimeUtc < cutoff) continue; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (_ignoredSignatures.TryGetValue(f, out var sig) && sig == Signature(fi)) continue;   // 이미 판정된 동일 파일 → 재열기 없음
                if (!_pending.ContainsKey(f)) { _pending[f] = Environment.TickCount64; }
            }
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

    /// <summary>파일 1건 처리. 학습 영향 이벤트를 발행했으면 true.</summary>
    private async Task<bool> ProcessPathAsync(string path, CancellationToken ct)
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
                catch (InvalidDataException)
                {
                    await RecordAsync(sha, rejected: true, ct).ConfigureAwait(false);
                    Remember(path);
                    return false;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] import " + name + ": " + ex.Message);
                    if (CountFailure(sha) >= Math.Max(1, _opt.MaxFailuresPerFile)) { await RecordAsync(sha, rejected: true, ct).ConfigureAwait(false); Remember(path); }
                    return false;   // 일시 실패 → 다음 스윕에서 재시도(상한 후 격리)
                }
                await RecordAsync(sha, rejected: false, ct).ConfigureAwait(false);
                Remember(path);
                Interlocked.Increment(ref _ingestedCount);
                var evt = new PlutoDownloadsAutoIngestEvent(name, sha, r, DateTimeOffset.UtcNow);
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
        lock (_sync) { _failures.TryGetValue(sha, out var n); _failures[sha] = ++n; return n; }
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
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await import(ct).ConfigureAwait(false);
            await RecordAsync(sha, rejected: false, ct).ConfigureAwait(false);   // 수동 반영분 → 감시기 재수집/재토스트 차단
            return r;
        }
        finally { _gate.Release(); }
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

    private async Task RecordAsync(string sha, bool rejected, CancellationToken ct)
    {
        lock (_sync)
        {
            if (rejected) AddBounded(_rejected, _rejectedOrder, sha);
            else { _rejected.Remove(sha); AddBounded(_ingested, _ingestedOrder, sha); }
            _failures.Remove(sha);
        }
        await SaveStateAsync(ct).ConfigureAwait(false);
    }

    private static void AddBounded(HashSet<string> set, LinkedList<string> order, string sha)
    {
        if (!set.Add(sha)) return;
        order.AddLast(sha);
        while (order.Count > LedgerCapacity) { set.Remove(order.First!.Value); order.RemoveFirst(); }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var node = JsonNode.Parse(File.ReadAllText(_statePath));
            if (node is not JsonObject o) return;
            if (o["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en)) _enabled = en;
            Fill(o["ingested"] as JsonArray, _ingested, _ingestedOrder);
            Fill(o["rejected"] as JsonArray, _rejected, _rejectedOrder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] state load (기본값 사용): " + ex.Message);
        }

        static void Fill(JsonArray? arr, HashSet<string> set, LinkedList<string> order)
        {
            if (arr is null) return;
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length == 64) AddBounded(set, order, s.ToUpperInvariant());
        }
    }

    private async Task SaveStateAsync(CancellationToken ct)
    {
        string json;
        lock (_sync)
        {
            var ing = new JsonArray(); foreach (var s in _ingestedOrder) ing.Add(s);
            var rej = new JsonArray(); foreach (var s in _rejectedOrder) rej.Add(s);
            json = new JsonObject { ["v"] = StateSchemaVersion, ["enabled"] = _enabled, ["ingested"] = ing, ["rejected"] = rej }.ToJsonString();
        }
        await _stateIo.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
            var tmp = _statePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _statePath, overwrite: true);   // 원자적 교체 — 중간 크래시에도 장부 파손 없음
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.DownloadsWatcher] state save: " + ex.Message);
        }
        finally { _stateIo.Release(); }
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
        Dispose();
    }
}
