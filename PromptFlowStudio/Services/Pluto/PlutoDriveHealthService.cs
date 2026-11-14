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

public sealed record PlutoDriveHealthReport(bool IsDriveMounted, int HealthyCount, int TotalCount, string? SettingsMirrorError, string? MemberMirrorError, IReadOnlyList<PlutoDriveFileHealthItem> Items, DateTimeOffset CheckedAtUtc);

/// <summary>☁️ Google Drive 7대 영구 백업 파일 헬스 점검(PC 이전 준비) — 존재 · 크기 &gt; 0 · JSON 파싱 검증.</summary>
public interface IPlutoDriveHealthService
{
    PlutoDriveHealthReport CheckHealth(string? plutoDriveDir = null, string? studioDriveDir = null);
    Task<PlutoDriveHealthReport> ForceSyncAndVerifyAsync(CancellationToken ct = default);
}

/// <summary>
/// Pluto AI 볼트 5종(<c>G:\내 드라이브\DATA\Pluto</c>) + 계정·설정 2종(<c>G:\내 드라이브\DATA\PromptFlowStudio</c> · <c>PROMPTFLOW_AUTH_MIRROR</c> 우선) 점검.
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
    };

    public static readonly string[] StudioFiles = { "members.json", AppSettingsManager.FileName };

    private readonly AppSettingsManager? _settings;
    private readonly MemberRegistryService? _members;
    private readonly IPlutoStorageManager? _storage;

    public PlutoDriveHealthService(AppSettingsManager? settings = null, MemberRegistryService? members = null, IPlutoStorageManager? storage = null)
    {
        _settings = settings;
        _members = members;
        _storage = storage;
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
            _settings?.LastMirrorError, _members?.LastMirrorError, items, DateTimeOffset.UtcNow);
    }

    public async Task<PlutoDriveHealthReport> ForceSyncAndVerifyAsync(CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            try { _settings?.FlushMirror(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] settings flush: " + ex.Message); }
            ct.ThrowIfCancellationRequested();
            try { _members?.FlushMirror(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] members flush: " + ex.Message); }
            ct.ThrowIfCancellationRequested();
            // Drive 활성 시 ResolveDataPath 가 부재 파일을 로컬 폴백/레거시 사본에서 시드(기존 Drive 파일 덮어쓰기 없음).
            if (_storage is not null && _storage.IsCloudSynced)
                foreach (var f in PlutoFiles)
                {
                    try { _ = _storage.ResolveDataPath(f); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { System.Diagnostics.Debug.WriteLine("[Pluto.DriveHealth] seed: " + ex.Message); }
                }
        }, ct).ConfigureAwait(false);
        return CheckHealth();
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
