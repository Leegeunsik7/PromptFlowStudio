using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>☁️ Step 38 — 기동 1회 멀티 PC Drive 조정 + 미매칭 대기열 조정 결과.</summary>
/// <param name="Attempted">Drive 허브 조정을 실제로 수행했는지(격리 스위치 · 미연결 → false).</param>
/// <param name="DriveSkipReason">Drive 조정 생략 사유(수행 → null).</param>
/// <param name="PushedToDrive">☁️⬆️ 오프라인 로컬 학습분 쓰기 반환 수.</param>
/// <param name="MirrorRefreshed">☁️⬇️ 로컬 미러 복원/갱신 수(이 PC 의 지난 세션 Drive 기록도 포함 → 단독으로는 피드에 기록하지 않음).</param>
/// <param name="NewConflictCopies">⚔️ 이번 조정에서 새로 보관된 분기 충돌 사본 수.</param>
/// <param name="ReloadedHubs">♻️ Drive 사본이 바뀌어 실행 중 즉시 재적재된 허브 파일명.</param>
/// <param name="UnresolvedConflicts">⚔️ 조정 직후 미해결 충돌 사본 수(설정 화면 처리 대기).</param>
/// <param name="ConflictFingerprint">미해결 충돌 집합 지문(경로 없음 · 같은 집합 → 같은 값 · 0건 → null).</param>
/// <param name="Unmatched">📌 미매칭 대기열 조정 결과(미등록/실패 → null).</param>
public sealed record PlutoStartupDriveSyncReport(bool Attempted, string? DriveSkipReason, int PushedToDrive, int MirrorRefreshed, int NewConflictCopies,
    IReadOnlyList<string> ReloadedHubs, int UnresolvedConflicts, string? ConflictFingerprint, PlutoUnmatchedReconcileResult? Unmatched, DateTimeOffset AtUtc)
{
    /// <summary>조정 실패 섹션(격리됨 · 사유 = 예외 형식명만).</summary>
    public string? DriveError { get; init; }
    public string? UnmatchedError { get; init; }

    /// <summary>다른 PC 와의 의미 있는 변화(쓰기 반환 · 재적재 · 새 충돌 · 대기열 정리/병합) — 미러 갱신만으로는 false.</summary>
    public bool HasMeaningfulChange => PushedToDrive > 0 || ReloadedHubs.Count > 0 || NewConflictCopies > 0 || Unmatched is { Changed: true };

    public static PlutoStartupDriveSyncReport Skipped(string why, DateTimeOffset at) => new(false, why, 0, 0, 0, Array.Empty<string>(), 0, null, null, at);

    public string SummaryKorean
    {
        get
        {
            var inv = CultureInfo.InvariantCulture;
            var s = "☁️ 기동 Drive 조정 " + (Attempted
                ? "⬆️" + PushedToDrive.ToString(inv) + " · ⬇️" + MirrorRefreshed.ToString(inv) + " · ♻️" + ReloadedHubs.Count.ToString(inv) + " · ⚔️" + UnresolvedConflicts.ToString(inv)
                : "생략(" + (DriveSkipReason ?? "—") + ")");
            if (Unmatched is { } u) s += " · 📌 정리 " + (u.PrunedLinked + u.PrunedResolved).ToString(inv) + " · 병합 " + u.MergedFromPeers.ToString(inv);
            return s;
        }
    }
}

/// <summary>
/// ☁️ Step 38 — 기동 1회 멀티 PC 조정(단일 비행): Drive 허브 쓰기 반환 → 미러 복원 → 바뀐 허브 즉시 재적재 → 미매칭 대기열 조정.
/// 호스트 초기화(<c>MainForm</c>)와 백그라운드 자율 데몬이 같은 작업에 합류한다(동시 조정 · 이중 쓰기 반환 없음).
/// </summary>
public interface IPlutoStartupDriveSyncService
{
    /// <summary>완료된 조정 결과(미완 → null).</summary>
    PlutoStartupDriveSyncReport? LastReport { get; }
    /// <summary>실제 조정 본체 실행 수(멱등 → 최대 1).</summary>
    int RunCount { get; }
    /// <summary>단일 비행 — 재호출 · 동시 호출은 같은 작업에 합류. <paramref name="ct"/> 는 이 호출자의 대기만 끊는다(공유 작업은 계속).</summary>
    Task<PlutoStartupDriveSyncReport> RunOnceAsync(CancellationToken ct = default);
}

/// <summary>☁️ <see cref="IPlutoStartupDriveSyncService"/> 구현(순수 BCL · 모든 단계 예외 격리).</summary>
public sealed class PlutoStartupDriveSyncService : IPlutoStartupDriveSyncService, IDisposable
{
    private readonly IPlutoStorageManager? _storage;
    private readonly Func<IPlutoYouTubeUnmatchedQueueService?>? _queue;
    private readonly Func<IPlutoDriveHealthService?>? _health;
    private readonly Func<IReadOnlyList<IPlutoHotReloadable>>? _hubs;
    private readonly Func<string, string?> _env;
    private readonly TimeProvider _time;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _life = new();
    private Task<PlutoStartupDriveSyncReport>? _run;
    private int _runCount;
    private bool _disposed;

    /// <param name="queue">지연 해석(DI 생성 비용을 실제 실행 시점으로 미룸).</param>
    /// <param name="health">미해결 충돌 목록 원천(지연 해석).</param>
    /// <param name="hotReloadHubs">재적재 허브 해석기 — Drive 사본이 바뀐 허브가 있을 때만 호출.</param>
    public PlutoStartupDriveSyncService(IPlutoStorageManager? storage, Func<IPlutoYouTubeUnmatchedQueueService?>? queue = null,
        Func<IPlutoDriveHealthService?>? health = null, Func<IReadOnlyList<IPlutoHotReloadable>>? hotReloadHubs = null,
        Func<string, string?>? environment = null, TimeProvider? timeProvider = null)
    {
        _storage = storage;
        _queue = queue;
        _health = health;
        _hubs = hotReloadHubs;
        _env = environment ?? Environment.GetEnvironmentVariable;
        _time = timeProvider ?? TimeProvider.System;
    }

    public PlutoStartupDriveSyncReport? LastReport { get; private set; }
    public int RunCount => Volatile.Read(ref _runCount);

    /// <summary>🔒 <c>PROMPTFLOW_PLUTO_DRIVE=off</c> → 저장소 · 대기열 · 충돌 폴더 전부 무접촉.</summary>
    public bool OfflineHarness => string.Equals(_env(PlutoDriveStorageManager.DriveEnvVar)?.Trim(), "off", StringComparison.OrdinalIgnoreCase);

    public Task<PlutoStartupDriveSyncReport> RunOnceAsync(CancellationToken ct = default)
    {
        Task<PlutoStartupDriveSyncReport> t;
        lock (_sync)
        {
            if (_disposed) return Task.FromResult(PlutoStartupDriveSyncReport.Skipped("종료됨", _time.GetUtcNow()));
            if (_run is null)
            {
                var token = _life.Token;   // 잠금 안에서 캡처 — 폐기 경합 시 ObjectDisposedException 없음
                _run = Task.Run(() => RunCoreAsync(token));
            }
            t = _run;
        }
        return ct.CanBeCanceled ? t.WaitAsync(ct) : t;
    }

    private async Task<PlutoStartupDriveSyncReport> RunCoreAsync(CancellationToken token)
    {
        Interlocked.Increment(ref _runCount);
        if (OfflineHarness) return LastReport = PlutoStartupDriveSyncReport.Skipped("격리 스위치", _time.GetUtcNow());

        // 1) ☁️ Drive 허브 양방향 조정 — 기존 MainForm 기동 순서 그대로(기준선 → 쓰기 반환 → 미러 복원 · 핵심 허브 → 동반 파일)
        var attempted = false;
        string? skip = null, driveError = null;
        int pushed = 0, mirrored = 0, newConflicts = 0;
        var reloaded = new List<string>(2);
        var dm = _storage as PlutoDriveStorageManager;
        if (dm is null) skip = "저장소 미연결";
        else
        {
            try
            {
                if (!dm.IsCloudSynced) skip = "Drive 미연결";
                else
                {
                    attempted = true;
                    var before = Signatures(dm.PrimaryRoot, PlutoDriveHealthService.PlutoFiles);
                    var conflictsBefore = dm.ConflictCopyCount;
                    dm.BootstrapSyncManifest(PlutoDriveHealthService.PlutoFiles);
                    pushed += dm.WriteBackNewerLocalToDrive(PlutoDriveHealthService.PlutoFiles);
                    mirrored += dm.RestoreCoreHubsToLocal(PlutoDriveHealthService.PlutoFiles);
                    dm.BootstrapSyncManifest(PlutoDriveHealthService.CompanionSyncFiles);
                    pushed += dm.WriteBackNewerLocalToDrive(PlutoDriveHealthService.CompanionSyncFiles);
                    mirrored += dm.RestoreCoreHubsToLocal(PlutoDriveHealthService.CompanionSyncFiles);
                    newConflicts = Math.Max(0, dm.ConflictCopyCount - conflictsBefore);
                    var after = Signatures(dm.PrimaryRoot, PlutoDriveHealthService.PlutoFiles);
                    // ♻️ 실행 중 허브가 이미 읽은 Drive 사본이 바뀜(쓰기 반환 · Drive 데몬 도착) → 허브 잠금 안 재적재(낡은 메모리가 새 사본을 되덮는 경로 차단)
                    List<string>? changed = null;
                    for (var i = 0; i < before.Length; i++)
                        if (before[i] != after[i]) (changed ??= new List<string>(2)).Add(PlutoDriveHealthService.PlutoFiles[i]);
                    if (changed is not null && !token.IsCancellationRequested) await HotReloadAsync(changed, reloaded, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { driveError = ex.GetType().Name; Log("Pluto.StartupSync.Drive", ex); }   // [부분 구제] 대기열 조정은 계속
        }

        // 2) 📌 미매칭 대기열 — 미러 복원 뒤(다른 PC 연결분이 로컬 사본에 반영된 다음) 조정 → 폴러 기동 전에 이미 연결된 ID 정리
        PlutoUnmatchedReconcileResult? unmatched = null;
        string? unmatchedError = null;
        if (!token.IsCancellationRequested)
        {
            try
            {
                if (_queue?.Invoke() is { } q) unmatched = await q.ReconcileAsync(null, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { unmatchedError = ex.GetType().Name; Log("Pluto.StartupSync.Unmatched", ex); }
        }

        // 3) ⚔️ 미해결 충돌(설정 화면 처리 대기)
        var unresolved = 0;
        string? fingerprint = null;
        if (attempted)
        {
            try
            {
                if (_health?.Invoke() is { } h)
                {
                    var list = h.ListConflicts();
                    unresolved = list.Count;
                    fingerprint = ConflictFingerprintOf(list);
                }
            }
            catch (Exception ex) { Log("Pluto.StartupSync.Conflicts", ex); }
        }

        var report = new PlutoStartupDriveSyncReport(attempted, skip, pushed, mirrored, newConflicts, reloaded, unresolved, fingerprint, unmatched, _time.GetUtcNow())
        { DriveError = driveError, UnmatchedError = unmatchedError };
        LastReport = report;
        System.Diagnostics.Debug.WriteLine("[Pluto.StartupSync] " + report.SummaryKorean);
        return report;
    }

    private async Task HotReloadAsync(List<string> changed, List<string> reloaded, CancellationToken token)
    {
        IReadOnlyList<IPlutoHotReloadable>? hubs = null;
        try { hubs = _hubs?.Invoke(); }
        catch (Exception ex) { Log("Pluto.StartupSync.HubResolve", ex); }
        if (hubs is null) return;
        foreach (var f in changed)
        {
            var hub = PlutoHotReload.Find(hubs, f);
            if (hub is null) continue;
            try { if (await hub.ReloadFromDiskAsync(token).ConfigureAwait(false)) reloaded.Add(f); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log("Pluto.StartupSync.HotReload", ex); }   // 실패 허브는 자체 재적재 펜스가 다음 기록 전 재시도
        }
    }

    /// <summary>Drive 사본 서명(크기 · 수정 시각) — IO 실패/부재는 고정 값.</summary>
    private static (long Length, long Ticks)[] Signatures(string? root, IReadOnlyList<string> files)
    {
        var sig = new (long, long)[files.Count];
        if (string.IsNullOrEmpty(root)) return sig;
        for (var i = 0; i < files.Count; i++)
        {
            try
            {
                var fi = new FileInfo(Path.Combine(root, files[i]));
                sig[i] = fi.Exists ? (fi.Length, fi.LastWriteTimeUtc.Ticks) : (-1L, 0L);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { sig[i] = (-2L, 0L); }
        }
        return sig;
    }

    /// <summary>
    /// 🛡️ 미해결 충돌 집합 지문 — 허브 · 보관 시각 · 크기만(경로 없음)으로 정렬 후 SHA-256 앞 16자.
    /// 같은 충돌이 남아 있는 재시작은 같은 지문 → 피드 중복 차단 · 새 충돌 사본이 생기거나 일부만 해결되면 지문이 바뀌어 다시 알린다.
    /// </summary>
    public static string? ConflictFingerprintOf(IReadOnlyList<PlutoConflictCopy>? conflicts)
    {
        if (conflicts is null || conflicts.Count == 0) return null;
        var parts = new List<string>(conflicts.Count);
        foreach (var c in conflicts)
            if (c is not null) parts.Add((c.HubFileName ?? "").ToLowerInvariant() + "@" + c.CapturedUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "#" + c.SizeBytes.ToString(CultureInfo.InvariantCulture));
        if (parts.Count == 0) return null;
        parts.Sort(StringComparer.Ordinal);
        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    private static void Log(string tag, Exception ex)
    {
        try { Program.Log(tag, ex); }
        catch (Exception) { System.Diagnostics.Debug.WriteLine("[" + tag + "] " + ex.GetType().Name); }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        _life.Dispose();
    }
}
