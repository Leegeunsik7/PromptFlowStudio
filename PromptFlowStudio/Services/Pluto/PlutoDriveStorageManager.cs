using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>☁️ Pluto 영속 JSON 저장소 — Google Drive 볼트 우선 · 로컬 폴백 · 원자적 기록.</summary>
public interface IPlutoStorageManager
{
    /// <summary>현재 활성 루트(Drive 또는 로컬 폴백) 기준 파일 경로 — 디렉터리 자동 생성 · 미존재 시 레거시/폴백 사본 1회 시드.</summary>
    string ResolveDataPath(string filename);

    /// <summary>텍스트 읽기 — 파일 없음/IO 실패 → 빈 문자열(예외 미전파 · 취소만 전파).</summary>
    Task<string> ReadAllTextAsync(string filename, CancellationToken ct = default);

    /// <summary>원자적 기록(<c>.tmp</c> → 교체) — Drive 실패 시 로컬 폴백 재기록. 반환 = 어느 한 곳이라도 성공.</summary>
    Task<bool> WriteAllTextAtomicAsync(string filename, string content, CancellationToken ct = default);

    /// <summary>Drive 볼트 활성 여부.</summary>
    bool IsCloudSynced { get; }

    /// <summary>현재 활성 루트 디렉터리.</summary>
    string ActiveRoot { get; }

    /// <summary>UI 배지 문구(☁️ 연동됨 / 💾 로컬 폴백).</summary>
    string StatusLabel { get; }

    /// <summary>활성 루트 전환(Drive ↔ 로컬) 통지.</summary>
    event Action? Changed;
}

/// <summary>
/// ☁️ <c>G:\내 드라이브\DATA\Pluto</c> 볼트 — Drive 클라이언트 미마운트/오프라인(<c>G:\내 드라이브\DATA</c> 부재) 또는 기록 실패 시
/// <c>%LOCALAPPDATA%\PromptFlowStudio\Data\Pluto</c> 로 IO 예외 없이 폴백. 가용성 판정은 <see cref="ProbeInterval"/> 캐시(핫패스 디스크 프로브 억제).
/// 첫 해석 시 대상 파일이 없으면 폴백 → 레거시(<c>%LOCALAPPDATA%\PromptFlowStudio</c>) 사본을 복사 시드(누적 XP/프로필 보존 · 원본 삭제 안 함).
/// 비 Windows(모바일) → Drive 경로 비활성.
/// </summary>
public sealed class PlutoDriveStorageManager : IPlutoStorageManager
{
    public const string DriveVaultRoot = @"G:\내 드라이브\DATA";
    public const string SubFolder = "Pluto";
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);
    /// <summary>이 인스턴스의 가용성 캐시 · 실패 후 차단 구간(기본 <see cref="ProbeInterval"/> · 테스트에서 재연결 경로를 빠르게 검증하기 위해 주입).</summary>
    public TimeSpan ProbeWindow { get; init; } = ProbeInterval;

    private readonly string? _primary;
    private readonly string _fallback;
    private readonly string? _legacy;
    private readonly object _sync = new();
    private long _nextProbeTick;
    private long _primaryDownUntilTick;
    private bool _primaryOk;
    private bool _probed;
    private readonly object _ioLock = new();                                                     // 🔁 양방향 동기화 IO 직렬화(로드 경로 · 기동 조정 · 재연결 푸시)
    private readonly HashSet<string> _reconciled = new(StringComparer.OrdinalIgnoreCase);       // ⚡ 이번 온라인 구간에 '첫 해석 쓰기 반환'을 마친 파일
    private readonly HashSet<string> _stranded = new(StringComparer.OrdinalIgnoreCase);         // 📴 오프라인 중 로컬 폴백에만 기록된 파일(재연결 시 즉시 푸시)
    private Task _strandedPush = Task.CompletedTask;

    /// <summary>🧾 마지막 동기화 시각 장부(로컬 폴백 폴더 · 핵심 허브 아님 · Drive 로 올리지 않음) — 분기(양쪽 모두 변경) 감지용.</summary>
    public const string SyncManifestFileName = "pluto_sync_manifest.json";
    /// <summary>⚔️ 분기 충돌 시 덮이기 직전 로컬 사본 보관 폴더(로컬 폴백 하위).</summary>
    public const string ConflictFolderName = "conflicts";
    public const int MaxConflictCopiesPerFile = 3;

    public PlutoDriveStorageManager() : this(DefaultPrimaryRoot(), null, DefaultLegacyRoot()) { }

    /// <param name="primaryRoot">Drive 측 Pluto 폴더(부모 폴더가 있어야 활성) · null → Drive 비활성.</param>
    /// <param name="fallbackRoot">로컬 폴백 폴더 · null → <c>%LOCALAPPDATA%\PromptFlowStudio\Data\Pluto</c>.</param>
    /// <param name="legacyRoot">구 저장 위치(시드 원천) · null → 시드 안 함.</param>
    public PlutoDriveStorageManager(string? primaryRoot, string? fallbackRoot = null, string? legacyRoot = null)
    {
        _primary = string.IsNullOrWhiteSpace(primaryRoot) ? null : primaryRoot;
        _fallback = string.IsNullOrWhiteSpace(fallbackRoot)
            ? Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio", "Data", SubFolder)
            : fallbackRoot;
        _legacy = string.IsNullOrWhiteSpace(legacyRoot) ? null : legacyRoot;
    }

    /// <summary>Drive 볼트 비활성 스위치 — 값이 "off"면 Drive 미사용(테스트 하네스가 실 백업 접근 차단용으로 설정).</summary>
    public const string DriveEnvVar = "PROMPTFLOW_PLUTO_DRIVE";

    public static string? DefaultPrimaryRoot()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(DriveEnvVar)?.Trim(), "off", StringComparison.OrdinalIgnoreCase)) return null;
        return OperatingSystem.IsWindows() ? Path.Combine(DriveVaultRoot, SubFolder) : null;
    }

    public static string DefaultLegacyRoot() => Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio");

    public string? PrimaryRoot => _primary;
    public string FallbackRoot => _fallback;

    public event Action? Changed;

    public bool IsCloudSynced => PrimaryAvailable();

    public string ActiveRoot => PrimaryAvailable() ? _primary! : EnsureDir(_fallback);

    /// <summary>UI 배지 문구.</summary>
    public string StatusLabel => IsCloudSynced ? "☁️ " + _primary + " 연동됨" : "💾 로컬 폴백";

    public string ResolveDataPath(string filename)
    {
        ValidateName(filename);
        var root = ActiveRoot;
        var path = Path.Combine(root, filename);
        if (!File.Exists(path)) Seed(path, filename, root);
        else if (!string.Equals(root, _fallback, StringComparison.OrdinalIgnoreCase) && MarkReconciled(filename)) TryWriteBack(filename);   // ⚡ 서비스 적재 '이전'에 오프라인 학습분을 Drive 로
        return path;
    }

    /// <summary>이번 온라인 구간에서 처음 해석하는 파일이면 true(재연결 시 초기화).</summary>
    private bool MarkReconciled(string filename)
    {
        lock (_sync) return _reconciled.Add(filename);
    }

    public async Task<string> ReadAllTextAsync(string filename, CancellationToken ct = default)
    {
        var path = ResolveDataPath(filename);
        var text = await TryReadAsync(path, ct).ConfigureAwait(false);
        if (text is not null) return text;
        // Drive 읽기 실패(오프라인 전환 직후 등) → 로컬 사본
        var local = Path.Combine(_fallback, filename);
        if (!string.Equals(local, path, StringComparison.OrdinalIgnoreCase))
            return await TryReadAsync(local, ct).ConfigureAwait(false) ?? string.Empty;
        return string.Empty;
    }

    public async Task<bool> WriteAllTextAtomicAsync(string filename, string content, CancellationToken ct = default)
    {
        ValidateName(filename);
        ArgumentNullException.ThrowIfNull(content);
        if (PrimaryAvailable())
        {
            if (await PlutoJsonStore.WriteAtomicAsync(Path.Combine(_primary!, filename), content, "Pluto.Drive", ct).ConfigureAwait(false))
                return true;
            MarkPrimaryDown();   // 클라우드 동기화 잠금·오프라인 → 로컬 폴백
        }
        var ok = await PlutoJsonStore.WriteAtomicAsync(Path.Combine(EnsureDir(_fallback), filename), content, "Pluto.Local", ct).ConfigureAwait(false);
        if (ok && _primary is not null) lock (_sync) _stranded.Add(filename);   // 📴 Drive 복귀 시 쓰기 반환 대상
        return ok;
    }

    private bool PrimaryAvailable()
    {
        if (_primary is null) return false;
        bool changed, ok;
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (_probed && now < _nextProbeTick) return _primaryOk;
            var prev = _primaryOk;
            ok = now >= _primaryDownUntilTick && Probe(_primary);
            changed = _probed && prev != ok;
            _primaryOk = ok;
            _probed = true;
            _nextProbeTick = now + (long)ProbeWindow.TotalMilliseconds;
            if (changed && ok) _reconciled.Clear();   // 새 온라인 구간 — 파일마다 첫 해석 시 다시 쓰기 반환 검사
        }
        if (changed && ok) ScheduleStrandedPush();
        if (changed) RaiseChanged();
        return ok;
    }

    /// <summary>📴→☁️ 재연결 — 오프라인 중 로컬에만 기록된 파일을 백그라운드로 Drive 에 반환(더 새로운 Drive 사본은 보호).</summary>
    private void ScheduleStrandedPush()
    {
        string[] files;
        lock (_sync)
        {
            if (_stranded.Count == 0) return;
            files = new string[_stranded.Count];
            _stranded.CopyTo(files);
            _stranded.Clear();
        }
        lock (_ioLock)
            _strandedPush = Task.Run(() =>
            {
                try { WriteBackNewerLocalToDrive(files); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Drive] stranded push: " + ex.Message); }
            });
    }

    /// <summary>재연결 푸시 완료 대기(테스트 · 종료 시).</summary>
    public Task WaitForStrandedPushAsync()
    {
        lock (_ioLock) return _strandedPush;
    }

    /// <summary>📴 오프라인 중 로컬에만 기록되어 Drive 반환을 기다리는 파일 수.</summary>
    public int StrandedCount
    {
        get { lock (_sync) return _stranded.Count; }
    }

    private void MarkPrimaryDown()
    {
        bool changed;
        lock (_sync)
        {
            var now = Environment.TickCount64;
            changed = _primaryOk;
            _primaryOk = false;
            _probed = true;
            _primaryDownUntilTick = now + (long)ProbeWindow.TotalMilliseconds;
            _nextProbeTick = _primaryDownUntilTick;
        }
        if (changed) RaiseChanged();
    }

    /// <summary>부모(볼트 루트) 존재 시에만 하위 Pluto 폴더 생성 — 미마운트 드라이브에 임의 폴더 생성 방지.</summary>
    private static bool Probe(string primary)
    {
        try
        {
            var parent = Path.GetDirectoryName(primary);
            if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent)) return false;
            Directory.CreateDirectory(primary);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] probe failed: " + ex.Message);
            return false;
        }
    }

    private static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.Drive] mkdir failed: " + ex.Message); }
        return dir;
    }

    /// <summary>대상 부재 → (Drive 활성 시) 로컬 폴백 사본 → 레거시 사본 순으로 복사 시드. 실패 무시(빈 상태로 시작).</summary>
    private void Seed(string target, string filename, string root)
    {
        // Drive 시드는 쓰기 반환과 같은 경로(비어 있지 않은 유효 JSON · 임시 파일 → 원자 교체) — 손상/0 B 로컬 사본이나 반쯤 복사된 파일이 Drive 로 퍼지지 않는다
        if (!string.Equals(root, _fallback, StringComparison.OrdinalIgnoreCase))
        {
            MarkReconciled(filename);
            if (TryWriteBack(filename)) return;
        }
        if (_legacy is not null) TryCopy(Path.Combine(_legacy, filename), target);
    }

    private static bool TryCopy(string src, string dst)
    {
        try
        {
            if (!File.Exists(src)) return false;
            File.Copy(src, dst, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] seed failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 🔁 핵심 허브 복원 — Drive 활성 시 Drive 에 있고 로컬 폴백에 없는 파일을 로컬로 복사(실패 무시).
    /// 🪞 Step 17: 로컬 사본이 이미 있어도 Drive 사본이 확실히 더 새로우면(<see cref="IsMirrorStale"/>) 로컬 미러를 갱신한다 —
    /// 첫 복원 이후 미러가 그 시점에 얼어붙어, 오프라인 전환 시 몇 주 전 학습 상태로 조용히 되돌아가던 문제 차단. 더 새로운 로컬 사본은 절대 덮어쓰지 않는다.
    /// ⚔️ Step 18: 동기화 장부 기준 분기(오프라인 로컬 변경 + 더 새로운 Drive)면 덮기 직전 로컬 사본을 <c>conflicts/</c> 에 보관.
    /// 새 PC 첫 실행/오프라인 전환 대비. Drive 비활성(<c>PROMPTFLOW_PLUTO_DRIVE=off</c> · 미마운트 · 비 Windows) → 0.
    /// 쓰기 반환(로컬 → Drive)은 <see cref="WriteBackNewerLocalToDrive"/> — 양방향 조정은 쓰기 반환 → 복원 순서로 호출한다.
    /// </summary>
    /// <returns>복원 + 갱신된 파일 수.</returns>
    public int RestoreCoreHubsToLocal(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!PrimaryAvailable()) return 0;
        var local = EnsureDir(_fallback);
        if (string.Equals(local, _primary, StringComparison.OrdinalIgnoreCase)) return 0;
        var restored = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            try { ValidateName(f); }
            catch (ArgumentException) { continue; }
            var dst = Path.Combine(local, f);
            var src = Path.Combine(_primary!, f);
            lock (_ioLock)
            {
                if (!File.Exists(dst))
                {
                    if (TryCopy(src, dst)) { restored++; RecordSynced(f, SafeUtc(dst)); }
                    continue;
                }
                if (!IsMirrorStale(src, dst)) continue;
                // ⚔️ 분기 — 마지막 동기화 이후 오프라인으로 바뀐 로컬 사본이 더 새로운 Drive 사본(다른 PC)에 덮이기 전에 보관
                if (IsDiverged(LoadManifest().TryGetValue(f, out var synced) ? synced : null, SafeUtc(dst)) && PreserveConflictCopy(f, dst) is not null) ConflictCopyCount++;
                if (TryRefreshMirror(src, dst)) { restored++; RecordSynced(f, SafeUtc(dst)); }
            }
        }
        return restored;
    }

    private static DateTime SafeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return default; }
    }

    /// <summary>
    /// ☁️⬆️ 오프라인 쓰기 반환 — Drive 활성 시, 로컬 폴백 사본이 비어 있지 않은 유효 JSON 이고 Drive 사본보다 <see cref="MirrorStalenessTolerance"/> 넘게 더 새롭거나
    /// Drive 사본이 없으면 Drive 로 원자 반환(수정 시각 보존 → 이후 미러 갱신과 핑퐁 없음). 더 새로운 Drive 사본은 절대 덮지 않는다.
    /// Drive 비활성(<c>PROMPTFLOW_PLUTO_DRIVE=off</c> · 미마운트 · 비 Windows) → 0.
    /// </summary>
    /// <returns>Drive 로 반환된 파일 수.</returns>
    public int WriteBackNewerLocalToDrive(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!PrimaryAvailable()) return 0;
        if (string.Equals(_fallback, _primary, StringComparison.OrdinalIgnoreCase)) return 0;
        var pushed = 0;
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            try { ValidateName(f); }
            catch (ArgumentException) { continue; }
            MarkReconciled(f);
            if (TryWriteBack(f)) pushed++;
        }
        return pushed;
    }

    /// <summary>
    /// ⬆️ 쓰기 반환 판정(순수 · IO 실패 → false) — 로컬이 존재 · 비어 있지 않음 · 유효 JSON 이고, Drive 가 없거나 로컬이 허용 오차를 넘겨 더 새로울 때만 true.
    /// </summary>
    public static bool IsLocalNewerForWriteBack(string localPath, string drivePath)
    {
        try
        {
            var src = new FileInfo(localPath);
            if (!src.Exists || src.Length == 0) return false;
            var dst = new FileInfo(drivePath);
            if (dst.Exists && src.LastWriteTimeUtc - dst.LastWriteTimeUtc <= MirrorStalenessTolerance) return false;
            return IsValidJson(localPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] write-back probe: " + ex.Message);
            return false;
        }
    }

    private static bool IsValidJson(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = System.Text.Json.JsonDocument.Parse(fs);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// ⬆️ 파일 1건 쓰기 반환 — Drive 폴더의 임시 파일로 복사 → (비교 후 교체) Drive 사본이 판정 이후 바뀌었으면(동시 서비스 기록 · 다른 PC 동기화) 포기.
    /// </summary>
    private bool TryWriteBack(string filename)
    {
        if (_primary is null) return false;
        var src = Path.Combine(_fallback, filename);
        var dst = Path.Combine(_primary, filename);
        lock (_ioLock)
        {
            if (!IsLocalNewerForWriteBack(src, dst)) return false;
            var before = Snapshot(dst);
            var tmp = dst + ".writeback.tmp";
            try
            {
                var srcTime = File.GetLastWriteTimeUtc(src);
                File.Copy(src, tmp, overwrite: true);
                File.SetLastWriteTimeUtc(tmp, srcTime);
                if (Snapshot(dst) != before) { TryDelete(tmp); return false; }   // 비교 후 교체 — 그 사이 Drive 가 갱신됨
                File.Move(tmp, dst, overwrite: true);
                RecordSynced(filename, srcTime);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("[Pluto.Drive] write-back failed: " + ex.Message);
                TryDelete(tmp);
                return false;
            }
        }
    }

    private static (bool Exists, long Length, DateTime Utc) Snapshot(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? (true, fi.Length, fi.LastWriteTimeUtc) : (false, 0L, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (false, -1L, default);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.Drive] tmp cleanup: " + ex.Message); }
    }

    // ── 🧾 동기화 장부 · ⚔️ 분기 충돌 보관 ──

    private string ManifestPath => Path.Combine(_fallback, SyncManifestFileName);

    private Dictionary<string, DateTime> LoadManifest()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(ManifestPath));
            return map is null ? new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, DateTime>(map, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] manifest read: " + ex.Message);
            return new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>파일이 마지막으로 동기화된 내용의 수정 시각(UTC · 기록 없음 → null).</summary>
    public DateTime? GetLastSyncedUtc(string filename)
    {
        lock (_ioLock) return LoadManifest().TryGetValue(filename, out var t) ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null;
    }

    /// <summary>동기화 장부 기록(호출자가 <c>_ioLock</c> 보유 · 실패 무시).</summary>
    private void RecordSynced(string filename, DateTime contentUtc)
    {
        try
        {
            var map = LoadManifest();
            map[filename] = DateTime.SpecifyKind(contentUtc, DateTimeKind.Utc);
            var dir = EnsureDir(_fallback);
            var tmp = Path.Combine(dir, SyncManifestFileName + ".tmp");
            File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(map));
            File.Move(tmp, ManifestPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] manifest write: " + ex.Message);
        }
    }

    /// <summary>
    /// ⚔️ 분기 판정(순수) — 마지막 동기화 이후 로컬이 바뀌었는데(오프라인 기록) Drive 도 더 새로운 경우. 동기화 기록이 없으면 판정 불가 → false(구버전 설치 호환).
    /// </summary>
    public static bool IsDiverged(DateTime? lastSyncedUtc, DateTime localUtc)
        => lastSyncedUtc is { } synced && localUtc - synced > MirrorStalenessTolerance;

    /// <summary>⚔️ 덮이기 직전 로컬 사본을 <c>conflicts/</c> 에 보관(파일당 최근 <see cref="MaxConflictCopiesPerFile"/>개) — 반환 = 보관 경로(실패 → null).</summary>
    private string? PreserveConflictCopy(string filename, string localPath)
    {
        try
        {
            var dir = EnsureDir(Path.Combine(_fallback, ConflictFolderName));
            var stem = Path.GetFileNameWithoutExtension(filename);
            var ext = Path.GetExtension(filename);
            var stamp = File.GetLastWriteTimeUtc(localPath).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var target = Path.Combine(dir, stem + ".conflict-" + stamp + ext);
            File.Copy(localPath, target, overwrite: true);
            var copies = Directory.GetFiles(dir, stem + ".conflict-*" + ext);
            if (copies.Length > MaxConflictCopiesPerFile)
            {
                Array.Sort(copies, StringComparer.Ordinal);   // 시각 접미 → 사전순 = 시간순
                for (var i = 0; i < copies.Length - MaxConflictCopiesPerFile; i++) TryDelete(copies[i]);
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] conflict copy: " + ex.Message);
            return null;
        }
    }

    /// <summary>⚔️ 이번 세션에 보관한 분기 충돌 사본 수.</summary>
    public int ConflictCopyCount { get; private set; }

    /// <summary>🪞 로컬 미러 신선도 허용 오차 — 파일 시스템 타임스탬프 해상도(FAT 2초 · Drive 가상 FS) 흡수.</summary>
    public static readonly TimeSpan MirrorStalenessTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 🪞 로컬 미러가 낡았는지(순수 판정 · IO 실패 → false) — Drive 사본이 <see cref="MirrorStalenessTolerance"/> 넘게 더 새롭고 · 비어 있지 않고 · 유효한 JSON 일 때만 true.
    /// 로컬이 같거나 더 새로우면(오프라인 중 기록 포함) 절대 덮어쓰지 않는다 · 동기화 도중의 손상/빈 Drive 사본도 정상 로컬 미러를 대체하지 못한다.
    /// </summary>
    public static bool IsMirrorStale(string drivePath, string localPath)
    {
        try
        {
            var src = new FileInfo(drivePath);
            var dst = new FileInfo(localPath);
            if (!src.Exists || !dst.Exists || src.Length == 0) return false;
            if (src.LastWriteTimeUtc - dst.LastWriteTimeUtc <= MirrorStalenessTolerance) return false;
            using var fs = new FileStream(drivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = System.Text.Json.JsonDocument.Parse(fs);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or System.Text.Json.JsonException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] mirror probe: " + ex.Message);
            return false;
        }
    }

    /// <summary>🪞 Drive → 로컬 미러 갱신(임시 파일 → 원자 교체 · 수정 시각 보존 → 멱등) — 실패 시 기존 로컬 사본 유지.</summary>
    private static bool TryRefreshMirror(string src, string dst)
    {
        var tmp = dst + ".mirror.tmp";
        try
        {
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, dst, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Drive] mirror refresh failed: " + ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { System.Diagnostics.Debug.WriteLine("[Pluto.Drive] mirror tmp cleanup: " + ex2.Message); }
            return false;
        }
    }

    private static async Task<string?> TryReadAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                using var sr = new StreamReader(fs);
                return await sr.ReadToEndAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= PlutoJsonStore.MaxAttempts)
                {
                    System.Diagnostics.Debug.WriteLine("[Pluto.Drive] read failed: " + ex.Message);
                    return null;
                }
                await Task.Delay(PlutoJsonStore.RetryDelay * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateName(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename) || filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("invalid file name", nameof(filename));
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Drive] Changed handler: " + ex.Message); }
        }
    }
}
