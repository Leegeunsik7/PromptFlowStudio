using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;
using PromptFlowStudio.Services.Configuration;

namespace PromptFlowStudio.Services.Pluto;

public enum PlutoDriveFileStatus { Healthy = 0, Missing = 1, CorruptOrEmpty = 2, DriveUnavailable = 3 }

public sealed record PlutoDriveFileHealthItem(string Category, string FileName, string FullPath, PlutoDriveFileStatus Status, long SizeBytes, DateTimeOffset? LastModifiedUtc, string? ErrorDetail);

public sealed record PlutoDriveHealthReport(bool IsDriveMounted, int HealthyCount, int TotalCount, string? SettingsMirrorError, string? MemberMirrorError, IReadOnlyList<PlutoDriveFileHealthItem> Items, DateTimeOffset CheckedAtUtc)
{
    /// <summary>🩺 Step 20 — [지금 전체 드라이브 백업 검증] 의 라이브 마운트 왕복 프로브(단순 점검 · 저장소 미연결 → null).</summary>
    public PlutoLiveDriveProbe? LiveProbe { get; init; }

    /// <summary>
    /// 🔁 Step 30 — 동반 동기화 파일 2종(<see cref="PlutoDriveHealthService.CompanionSyncFiles"/>) 상태. 핵심 허브 9종 집계(<see cref="HealthyCount"/> ·
    /// <see cref="TotalCount"/> · <see cref="Items"/>)와 분리 — 동반 파일이 허브 수 · "전체 정상" 판정 · 새 PC 복원 대상 수를 바꾸지 않는다.
    /// </summary>
    public IReadOnlyList<PlutoCompanionSyncItem> Companions { get; init; } = Array.Empty<PlutoCompanionSyncItem>();

    /// <summary>동반 파일 중 동기화 일치 수.</summary>
    public int CompanionInSyncCount
    {
        get { var n = 0; foreach (var c in Companions) if (c.State == PlutoCompanionSyncState.InSync) n++; return n; }
    }

    /// <summary>한국어 1줄 요약(경로 없음).</summary>
    public string CompanionSummaryKorean
    {
        get
        {
            if (Companions.Count == 0) return "🔁 동반 동기화 파일 — 점검 안 함";
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder(160);
            sb.Append("🔁 동반 동기화 ").Append(CompanionInSyncCount.ToString(inv)).Append('/').Append(Companions.Count.ToString(inv)).Append(" 일치");
            foreach (var c in Companions) sb.Append(" · ").Append(c.FileName).Append(' ').Append(c.StateKorean).Append(' ').Append(c.EntryCount.ToString(inv)).Append("건");
            return sb.ToString();
        }
    }
}

/// <summary>🔁 Step 30 — 동반 동기화 파일 상태(SHA-256 · 동기화 장부 기준선 판정).</summary>
public enum PlutoCompanionSyncState
{
    /// <summary>로컬 · Drive 내용 SHA-256 일치.</summary>
    InSync = 0,
    /// <summary>아직 어느 쪽에도 없음(해당 기능 미사용 · 정상).</summary>
    NotCreated = 1,
    /// <summary>로컬에만 있음 → 다음 쓰기 반환 대기.</summary>
    LocalOnly = 2,
    /// <summary>Drive 에만 있음 → 다음 미러 복원 대기.</summary>
    DriveOnly = 3,
    /// <summary>로컬만 기준선 이후 변경(Drive = 기준선) → 쓰기 반환 대기(충돌 아님).</summary>
    LocalAhead = 4,
    /// <summary>Drive 만 기준선 이후 변경(로컬 = 기준선) → 미러 갱신 대기(충돌 아님).</summary>
    DriveAhead = 5,
    /// <summary>양쪽 모두 기준선과 다름(또는 기준선 없음) → 분기 — 병합 대상.</summary>
    Diverged = 6,
    /// <summary>JSON 손상 · 0 바이트 · 읽기 실패.</summary>
    Corrupt = 7,
    /// <summary>Drive 폴더 비활성/없음(로컬 사본만 점검).</summary>
    DriveUnavailable = 8,
}

/// <summary>🔁 Step 30 — 동반 동기화 파일 1건(경로 미포함 · 공유 안전).</summary>
public sealed record PlutoCompanionSyncItem(string FileName, bool LocalPresent, bool DrivePresent, PlutoCompanionSyncState State, int EntryCount,
    long LocalSizeBytes, long DriveSizeBytes, string? LocalSha256, string? DriveSha256, DateTimeOffset? DriveModifiedUtc, string? ErrorDetail)
{
    /// <summary>정상 범주(일치 · 미생성 · 단방향 대기).</summary>
    public bool IsHealthy => State is PlutoCompanionSyncState.InSync or PlutoCompanionSyncState.NotCreated or PlutoCompanionSyncState.LocalOnly
        or PlutoCompanionSyncState.DriveOnly or PlutoCompanionSyncState.LocalAhead or PlutoCompanionSyncState.DriveAhead;

    public string StateKorean => State switch
    {
        PlutoCompanionSyncState.InSync => "✅ 일치",
        PlutoCompanionSyncState.NotCreated => "⚪ 미생성",
        PlutoCompanionSyncState.LocalOnly => "⬆️ 로컬만(반환 대기)",
        PlutoCompanionSyncState.DriveOnly => "⬇️ Drive만(복원 대기)",
        PlutoCompanionSyncState.LocalAhead => "⬆️ 로컬 최신(반환 대기)",
        PlutoCompanionSyncState.DriveAhead => "⬇️ Drive 최신(미러 대기)",
        PlutoCompanionSyncState.Diverged => "⚔️ 분기",
        PlutoCompanionSyncState.Corrupt => "❌ 손상",
        _ => "⛔ 드라이브 없음",
    };
}

/// <summary>☁️ Google Drive 영구 백업 파일 헬스 점검(PC 이전 준비 · Pluto 핵심 허브 9종 + 계정·설정 2종) — 존재 · 크기 &gt; 0 · JSON 파싱 검증.</summary>
public interface IPlutoDriveHealthService
{
    PlutoDriveHealthReport CheckHealth(string? plutoDriveDir = null, string? studioDriveDir = null);
    Task<PlutoDriveHealthReport> ForceSyncAndVerifyAsync(CancellationToken ct = default);

    /// <summary>⚔️ 미해결 분기 충돌 사본(최신순 · 저장소 미연결 → 빈 목록).</summary>
    IReadOnlyList<PlutoConflictCopy> ListConflicts() => Array.Empty<PlutoConflictCopy>();
    /// <summary>📥 다음 적재 때 적용될 해결 수.</summary>
    int PendingConflictResolutions => 0;
    /// <summary>🔀 의미 병합 예약.</summary>
    PlutoConflictResolution MergeConflict(string conflictPath) => new(PlutoConflictAction.None, "", "⚠ 저장소 미연결");
    /// <summary>↩️ 충돌 사본으로 복원 예약.</summary>
    PlutoConflictResolution RestoreConflict(string conflictPath) => new(PlutoConflictAction.None, "", "⚠ 저장소 미연결");
    /// <summary>🗑️ 충돌 사본 무시(삭제).</summary>
    PlutoConflictResolution DismissConflict(string conflictPath) => new(PlutoConflictAction.None, "", "⚠ 저장소 미연결");

    /// <summary>♻️ Step 20 — 병합 예약 후 해당 허브를 실행 중 재적재(재시작 불필요 · 재적재 불가 허브 → 예약 결과 그대로).</summary>
    Task<PlutoConflictResolution> MergeConflictLiveAsync(string conflictPath, CancellationToken ct = default) => Task.FromResult(MergeConflict(conflictPath));
    /// <summary>♻️ Step 20 — 사본 복원 예약 후 해당 허브를 실행 중 재적재.</summary>
    Task<PlutoConflictResolution> RestoreConflictLiveAsync(string conflictPath, CancellationToken ct = default) => Task.FromResult(RestoreConflict(conflictPath));
    /// <summary>♻️ Step 20 — 허브가 실행 중 재적재됨(인자 = 허브 파일명 · 백그라운드 스레드 가능).</summary>
    event Action<string>? HubReloaded { add { } remove { } }
}

/// <summary>
/// Pluto AI 볼트 9종(<see cref="CorePlutoFileCount"/> · <c>G:\내 드라이브\DATA\Pluto</c>) + 계정·설정 2종(<c>G:\내 드라이브\DATA\PromptFlowStudio</c> · <c>PROMPTFLOW_AUTH_MIRROR</c> 우선) 점검.
/// 모든 IO 예외 격리(항목별 <see cref="PlutoDriveFileStatus.CorruptOrEmpty"/>) · 의존성 null 허용(모바일).
/// </summary>
public sealed class PlutoDriveHealthService : IPlutoDriveHealthService
{
    public const string CategoryPluto = "Pluto AI 볼트";
    public const string CategoryStudio = "계정·설정";

    public static readonly string[] PlutoFiles =
    {
        PlutoLearningService.FileName,                       // pluto_trend_memory.json
        PlutoAutonomousTrendHarvester.PatternsFileName,      // pluto_viral_patterns.json
        PlutoSocialRadarEngine.FileName,                     // pluto_social_radar.json
        PlutoIdentityVault.FileName,                         // pluto_identity_vault.json
        PlutoMistakeNotebookService.FileName,                // pluto_mistake_notebook.json
        PlutoExperimentLedgerService.FileName,               // pluto_experiment_ledger.json (🏆 A/B 실험 장부)
        PlutoRenderCatalogService.FileName,                  // pluto_render_catalog.json (🎞️ 영속 렌더 카탈로그)
        PlutoMetaCalibrationService.FileName,                // pluto_calibration_state.json (🎯 메타 보정 · 반사실 후회)
        PlutoSeriesArcMemoryService.FileName,                // pluto_series_arc.json (📺 시리즈 아크 · 시즌 · 편 간 유지율)
    };

    /// <summary>검증 · 미러 · 새 PC 자동 복원 대상 Pluto 핵심 허브 파일 수(Step 17 = 9 · 시리즈 아크 편입).</summary>
    public const int CorePlutoFileCount = 9;

    /// <summary>
    /// 🔁 Step 25 — 핵심 허브(9종 불변) 밖의 동반 동기화 파일 — 쓰기 반환 · 미러 · 새 PC 복원에만 참여(상태 점검 · 허브 수에는 미포함).
    /// <c>pluto_youtube_unmatched.json</c>: 미매칭 YouTube 행 대기열(멀티 PC 조정 대상).
    /// </summary>
    public static readonly string[] CompanionSyncFiles = { PlutoYouTubeUnmatchedQueueService.FileName, PlutoRehabilitationLedger.FileName };   // 🌱 Step 29 — <c>pluto_rehab_ledger.json</c>: 유기적 복권 장부(합집합 병합)

    /// <summary>🔁 쓰기 반환 · 미러 · 복원 대상 전체 = 핵심 허브 + 동반 파일.</summary>
    public static readonly string[] SyncFiles = BuildSyncFiles();

    private static string[] BuildSyncFiles()
    {
        var all = new string[PlutoFiles.Length + CompanionSyncFiles.Length];
        PlutoFiles.CopyTo(all, 0);
        CompanionSyncFiles.CopyTo(all, PlutoFiles.Length);
        return all;
    }

    public static readonly string[] StudioFiles = { "members.json", AppSettingsManager.FileName };

    private readonly AppSettingsManager? _settings;
    private readonly MemberRegistryService? _members;
    private readonly IPlutoStorageManager? _storage;

    private readonly PlutoConflictMergeService? _conflicts;

    public PlutoDriveHealthService(AppSettingsManager? settings = null, MemberRegistryService? members = null, IPlutoStorageManager? storage = null)
    {
        _settings = settings;
        _members = members;
        _storage = storage;
        _conflicts = storage is PlutoDriveStorageManager dm ? new PlutoConflictMergeService(dm) : null;
    }

    public IReadOnlyList<PlutoConflictCopy> ListConflicts() => _conflicts?.ListConflicts() ?? Array.Empty<PlutoConflictCopy>();
    public int PendingConflictResolutions => _conflicts?.PendingCount ?? 0;
    public PlutoConflictResolution MergeConflict(string conflictPath) => _conflicts?.StageMerge(conflictPath) ?? new(PlutoConflictAction.None, "", "⚠ 저장소 미연결");
    public PlutoConflictResolution RestoreConflict(string conflictPath) => _conflicts?.StageRestore(conflictPath) ?? new(PlutoConflictAction.None, "", "⚠ 저장소 미연결");
    public PlutoConflictResolution DismissConflict(string conflictPath) => _conflicts?.Dismiss(conflictPath) ?? new(PlutoConflictAction.None, "", "⚠ 저장소 미연결");

    // ── ♻️ Step 20 — 무재시작 충돌 해결 ──

    /// <summary>♻️ 재적재 가능한 허브 해석기(DI 지연 해석 · 미설정 → 즉시 반영 없음 = 다음 적재 때 적용).</summary>
    public Func<IReadOnlyList<IPlutoHotReloadable>>? HotReloadHubs { get; init; }

    public event Action<string>? HubReloaded;

    public Task<PlutoConflictResolution> MergeConflictLiveAsync(string conflictPath, CancellationToken ct = default) => ApplyLiveAsync(MergeConflict(conflictPath), ct);

    public Task<PlutoConflictResolution> RestoreConflictLiveAsync(string conflictPath, CancellationToken ct = default) => ApplyLiveAsync(RestoreConflict(conflictPath), ct);

    /// <summary>
    /// ♻️ 예약 → 그 허브의 <see cref="IPlutoHotReloadable.ReloadFromDiskAsync"/>(허브 잠금 안에서 <c>sync_pending/</c> 승격 + 재적재).
    /// 승격을 허브 잠금 안에서 하는 이유: 밖에서 디스크만 바꾸면 이미 메모리에 상태를 든 서비스의 다음 기록이 병합 결과를 되덮는다.
    /// 재적재 불가 허브(아이덴티티 · 레이더 등) · 해석 실패 · 읽기 전용 세션 → 예약 유지(다음 적재 때 적용).
    /// </summary>
    private async Task<PlutoConflictResolution> ApplyLiveAsync(PlutoConflictResolution staged, CancellationToken ct)
    {
        if (staged.Action is not (PlutoConflictAction.MergeStaged or PlutoConflictAction.RestoreStaged)) return staged;
        IPlutoHotReloadable? hub = null;
        try { hub = PlutoHotReload.Find(HotReloadHubs?.Invoke(), staged.HubFileName); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] hub resolve: " + ex.Message); }
        if (hub is null) return staged;
        bool ok;
        try { ok = await hub.ReloadFromDiskAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] hot reload: " + ex.Message); ok = false; }
        if (!ok) return staged with { MessageKorean = staged.MessageKorean + " · ⚠ 즉시 반영 보류(다음 적재 때 적용)" };
        RaiseHubReloaded(staged.HubFileName);
        var merge = staged.Action == PlutoConflictAction.MergeStaged;
        return new(merge ? PlutoConflictAction.MergeApplied : PlutoConflictAction.RestoreApplied, staged.HubFileName,
            (merge ? "🔀 '" + staged.HubFileName + "' 병합 완료" : "↩️ '" + staged.HubFileName + "' 사본 복원 완료") + " — 실행 중인 세션에 즉시 반영(재시작 불필요)");
    }

    private void RaiseHubReloaded(string hub)
    {
        var h = HubReloaded;
        if (h is null) return;
        foreach (Action<string> a in h.GetInvocationList())
        {
            try { a(hub); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] HubReloaded handler: " + ex.Message); }
        }
    }

    /// <summary>Pluto 볼트 Drive 경로 — 저장소 관리자 Primary → 기본 상수(Windows 전용).</summary>
    public string? DefaultPlutoDir()
        => (_storage as PlutoDriveStorageManager)?.PrimaryRoot ?? PlutoDriveStorageManager.DefaultPrimaryRoot();

    /// <summary>계정·설정 미러 경로 — <c>PROMPTFLOW_AUTH_MIRROR</c>(빈값/off → 비활성) → 기본 Drive 경로.</summary>
    public static string? DefaultStudioDir()
    {
        var env = Environment.GetEnvironmentVariable(MemberRegistryService.MirrorEnvVar);
        if (env is not null)
        {
            env = env.Trim();
            return env.Length == 0 || env.Equals("off", StringComparison.OrdinalIgnoreCase) ? null : env;
        }
        return OperatingSystem.IsWindows() ? MemberRegistryService.DefaultMirrorDir : null;
    }

    public PlutoDriveHealthReport CheckHealth(string? plutoDriveDir = null, string? studioDriveDir = null)
    {
        var plutoDir = string.IsNullOrWhiteSpace(plutoDriveDir) ? DefaultPlutoDir() : plutoDriveDir;
        var studioDir = string.IsNullOrWhiteSpace(studioDriveDir) ? DefaultStudioDir() : studioDriveDir;
        bool plutoOk = DirExists(plutoDir), studioOk = DirExists(studioDir);

        var items = new List<PlutoDriveFileHealthItem>(PlutoFiles.Length + StudioFiles.Length);
        var healthy = 0;
        foreach (var f in PlutoFiles) Add(items, CategoryPluto, plutoDir, plutoOk, f, ref healthy);
        foreach (var f in StudioFiles) Add(items, CategoryStudio, studioDir, studioOk, f, ref healthy);

        return new PlutoDriveHealthReport(plutoOk || studioOk, healthy, items.Count,
            _settings?.LastMirrorError, _members?.LastMirrorError, items, DateTimeOffset.UtcNow)
        {
            Companions = InspectCompanions(plutoDir, plutoOk, LocalMirrorRoot ?? (_storage as PlutoDriveStorageManager)?.FallbackRoot, _storage as PlutoDriveStorageManager),
        };
    }

    // ── 🔁 Step 30 — 동반 동기화 파일 관측(핵심 허브 9종 집계와 분리) ──

    /// <summary>로컬 미러 폴더 재정의(테스트 · 자가진단 샌드박스) — null → 저장소 관리자 폴백 루트.</summary>
    public string? LocalMirrorRoot { get; init; }

    /// <summary>동반 파일 JSON 최대 검사 크기(초과 → 손상 판정 · 무한 메모리 차단).</summary>
    public const long MaxCompanionParseBytes = 16L * 1024 * 1024;

    /// <summary>
    /// 🔁 동반 파일 2종 상태 — 존재 · 크기 · SHA-256 · 항목 수(대기열 <c>Rows</c> · 장부 <c>Records</c>).
    /// 양쪽 해시가 다르면 동기화 장부 기준선(<see cref="PlutoDriveStorageManager.GetSyncRecord"/>)으로 '단방향 대기'와 '진짜 분기'를 구분한다.
    /// 모든 IO 예외 격리 · 경로는 결과에 넣지 않음(진단 공유 안전).
    /// </summary>
    public static IReadOnlyList<PlutoCompanionSyncItem> InspectCompanions(string? driveDir, bool driveOk, string? localDir, PlutoDriveStorageManager? manifest = null)
    {
        var list = new PlutoCompanionSyncItem[CompanionSyncFiles.Length];
        var localOk = DirExists(localDir);
        for (var i = 0; i < CompanionSyncFiles.Length; i++)
            list[i] = InspectCompanion(CompanionSyncFiles[i], driveOk ? driveDir : null, localOk ? localDir : null, driveOk, manifest);
        return list;
    }

    private static PlutoCompanionSyncItem InspectCompanion(string file, string? driveDir, string? localDir, bool driveOk, PlutoDriveStorageManager? manifest)
    {
        var local = ProbeCompanion(localDir, file);
        var drive = ProbeCompanion(driveDir, file);
        var count = drive.Valid ? drive.Count : local.Valid ? local.Count : 0;
        var err = drive.Error ?? local.Error;
        PlutoCompanionSyncState state;
        if ((drive.Present && !drive.Valid) || (local.Present && !local.Valid)) state = PlutoCompanionSyncState.Corrupt;
        else if (!driveOk) state = PlutoCompanionSyncState.DriveUnavailable;
        else if (!local.Present && !drive.Present) state = PlutoCompanionSyncState.NotCreated;
        else if (local.Present && !drive.Present) state = PlutoCompanionSyncState.LocalOnly;
        else if (!local.Present) state = PlutoCompanionSyncState.DriveOnly;
        else if (string.Equals(local.Sha, drive.Sha, StringComparison.Ordinal)) state = PlutoCompanionSyncState.InSync;
        else
        {
            // 🧾 기준선 판정 — 마지막 동기화 해시와 같은 쪽은 '안 바뀐 쪽'(다른 쪽만 전진 = 단방향 대기 · 충돌 아님)
            string? baseline = null;
            try { baseline = manifest?.GetSyncRecord(file)?.Sha256; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { baseline = null; }
            state = baseline is null ? PlutoCompanionSyncState.Diverged
                : string.Equals(baseline, drive.Sha, StringComparison.OrdinalIgnoreCase) ? PlutoCompanionSyncState.LocalAhead
                : string.Equals(baseline, local.Sha, StringComparison.OrdinalIgnoreCase) ? PlutoCompanionSyncState.DriveAhead
                : PlutoCompanionSyncState.Diverged;
        }
        return new PlutoCompanionSyncItem(file, local.Present, drive.Present, state, count, local.Size, drive.Size, local.Sha, drive.Sha, drive.Modified, err);
    }

    private readonly record struct CompanionProbe(bool Present, bool Valid, long Size, string? Sha, int Count, DateTimeOffset? Modified, string? Error);

    /// <summary>파일 1개 — 한 번 읽은 바이트로 해시 · 파싱(읽는 사이 교체돼도 해시와 항목 수가 같은 내용 기준).</summary>
    private static CompanionProbe ProbeCompanion(string? dir, string file)
    {
        if (dir is null) return default;
        var path = Path.Combine(dir, file);
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return default;
            var modified = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
            if (fi.Length == 0) return new CompanionProbe(true, false, 0, null, 0, modified, "빈 파일(0 B)");
            if (fi.Length > MaxCompanionParseBytes) return new CompanionProbe(true, false, fi.Length, null, 0, modified, "크기 상한 초과");
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bytes = new byte[fs.Length];
                var read = 0;
                while (read < bytes.Length) { var n = fs.Read(bytes, read, bytes.Length - read); if (n == 0) break; read += n; }
                if (read != bytes.Length) Array.Resize(ref bytes, read);
            }
            var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
            int count;
            try { count = CountEntries(bytes); }
            catch (JsonException ex) { return new CompanionProbe(true, false, bytes.LongLength, sha, 0, modified, "JSON 손상: " + ex.Message); }
            return new CompanionProbe(true, true, bytes.LongLength, sha, count, modified, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new CompanionProbe(true, false, 0, null, 0, null, ex.Message);
        }
    }

    /// <summary>항목 수 — 대기열 <c>Rows</c> · 장부 <c>Records</c> 배열 길이 · 최상위 배열이면 그 길이 · 그 밖 0(JSON 손상 → <see cref="JsonException"/>).</summary>
    public static int CountEntries(ReadOnlySpan<byte> json)
    {
        if (json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF) json = json[3..];
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array) return root.GetArrayLength();
        if (root.ValueKind != JsonValueKind.Object) return 0;
        foreach (var p in root.EnumerateObject())
            if ((p.NameEquals("Rows") || p.NameEquals("Records") || p.NameEquals("rows") || p.NameEquals("records")) && p.Value.ValueKind == JsonValueKind.Array)
                return p.Value.GetArrayLength();
        return 0;
    }

    public async Task<PlutoDriveHealthReport> ForceSyncAndVerifyAsync(CancellationToken ct = default)
    {
        PlutoLiveDriveProbe? probe = null;
        await Task.Run(() =>
        {
            try { _settings?.FlushMirror(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] settings flush: " + ex.Message); }
            ct.ThrowIfCancellationRequested();
            try { _members?.FlushMirror(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] members flush: " + ex.Message); }
            ct.ThrowIfCancellationRequested();
            // 🧾 기준선 부트스트랩(내용이 같은 파일만) → ☁️⬆️ 오프라인 쓰기 반환(더 새로운 Drive 사본은 보호)
            (_storage as PlutoDriveStorageManager)?.BootstrapSyncManifest(SyncFiles);
            (_storage as PlutoDriveStorageManager)?.WriteBackNewerLocalToDrive(SyncFiles);
            ct.ThrowIfCancellationRequested();
            // Drive 활성 시 ResolveDataPath 가 부재 파일을 로컬 폴백/레거시 사본에서 시드(기존 Drive 파일 덮어쓰기 없음).
            if (_storage is not null && _storage.IsCloudSynced)
                foreach (var f in SyncFiles)
                {
                    try { _ = _storage.ResolveDataPath(f); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] seed: " + ex.Message); }
                }
            // 🔁 Drive → 로컬 미러(부재 시만 · 오프라인/새 PC 복원 대비)
            (_storage as PlutoDriveStorageManager)?.RestoreCoreHubsToLocal(SyncFiles);
            ct.ThrowIfCancellationRequested();
            // 🩺 라이브 마운트 왕복 프로브(Drive 비활성/루트 부재 → 디스크 무접촉 생략)
            try { probe = (_storage as PlutoDriveStorageManager)?.ProbeLiveDrive(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] live probe: " + ex.Message); }
        }, ct).ConfigureAwait(false);
        return CheckHealth() with { LiveProbe = probe };
    }

    private static void Add(List<PlutoDriveFileHealthItem> items, string category, string? dir, bool dirOk, string file, ref int healthy)
    {
        var item = Inspect(category, dir, dirOk, file);
        if (item.Status == PlutoDriveFileStatus.Healthy) healthy++;
        items.Add(item);
    }

    private static PlutoDriveFileHealthItem Inspect(string category, string? dir, bool dirOk, string file)
    {
        var path = dir is null ? file : Path.Combine(dir, file);
        if (!dirOk)
            return new(category, file, path, PlutoDriveFileStatus.DriveUnavailable, 0, null, dir is null ? "백업 경로 비활성" : "드라이브 폴더 없음");
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return new(category, file, path, PlutoDriveFileStatus.Missing, 0, null, null);
            var modified = new DateTimeOffset(fi.LastWriteTimeUtc, TimeSpan.Zero);
            if (fi.Length == 0) return new(category, file, path, PlutoDriveFileStatus.CorruptOrEmpty, 0, modified, "빈 파일(0 B)");
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var doc = JsonDocument.Parse(fs)) { _ = doc.RootElement.ValueKind; }
            return new(category, file, path, PlutoDriveFileStatus.Healthy, fi.Length, modified, null);
        }
        catch (JsonException ex)
        {
            return new(category, file, path, PlutoDriveFileStatus.CorruptOrEmpty, SafeLength(path), null, "JSON 손상: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new(category, file, path, PlutoDriveFileStatus.CorruptOrEmpty, SafeLength(path), null, ex.Message);
        }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return 0; }
    }

    private static bool DirExists(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try { return Directory.Exists(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
}
