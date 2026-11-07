using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Export;

/// <summary>📦 번들 매니페스트 미디어 항목 — <see cref="Rel"/> = MediaRoot 기준 상대 경로('/' 구분).</summary>
public sealed record AssetBundleEntry(string Rel, long Bytes, string Sha256, bool HasMeta);

public sealed record AssetBundleExportResult(bool Ok, string? BundlePath, int MediaCount, int PresetCount, IReadOnlyList<string> Skipped, string Message);

public sealed record AssetBundleImportResult(bool Ok, IReadOnlyList<string> Imported, int DuplicatesSkipped, int PlaylistSlotsMerged, int PresetsRestored, string Message);

/// <summary>
/// 📦 포터블 에셋 번들러 — 미디어 · <c>.meta.json</c> · <c>playlist.json</c> · 디스플레이 설정을 단일 <c>.pfsbundle</c>(표준 ZIP)로 패키징/복원.
/// 가져오기: 매니페스트 스키마 검증 → 경로 탈출(Zip-Slip) 차단 → SHA-256 무결성 검증 → MediaRoot 추출(동일 해시 = 중복 건너뜀 · 이름 충돌 = 접미사 보존)
/// → 플레이리스트 슬롯 키 병합(기존 슬롯 덮어쓰기 금지). 개별 실패 격리([부분 구제]).
/// </summary>
public sealed class AssetBundleService
{
    public const string Extension = ".pfsbundle";
    public const string ManifestEntry = "manifest.json";
    public const string FormatId = "pfsbundle";
    public const int FormatVersion = 1;
    private const string MediaPrefix = "media/";
    private const string PresetPrefix = "presets/";

    public static readonly string[] PresetFiles =
        [StockSuitePlaylistService.PlaylistFileName, StockSuiteExporter.DisplayConfigName, StockSuiteExporter.DisplaySyncName];

    private readonly Func<string> _mediaRoot;
    private readonly Func<string?> _presetDir;

    public AssetBundleService(ILocalMediaService media, StockSuiteExporter exporter)
        : this(() => media.MediaRoot, () => exporter.ConfiguredDirectory) { }

    /// <param name="mediaRoot">미디어 루트 공급자.</param>
    /// <param name="presetDir">플레이리스트/디스플레이 설정 폴더(StockSuite 에셋 경로 · null = 프리셋 생략).</param>
    public AssetBundleService(Func<string> mediaRoot, Func<string?> presetDir)
    {
        _mediaRoot = mediaRoot ?? throw new ArgumentNullException(nameof(mediaRoot));
        _presetDir = presetDir ?? (static () => null);
    }

    public string MediaRoot => Path.GetFullPath(_mediaRoot());

    /// <summary>ZIP 엔트리 경로 안전성 — 루트/드라이브/상위 탈출(..)/빈 세그먼트 거부.</summary>
    public static bool IsSafeRelative(string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || rel.Length > 400) return false;
        if (rel[0] == '/' || rel[0] == '\\' || rel.Contains(':', StringComparison.Ordinal)) return false;
        foreach (var seg in rel.Split('/', '\\'))
            if (seg.Length == 0 || seg == "." || seg == ".." || seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    private static string Sha256Hex(Stream s) => Convert.ToHexString(SHA256.HashData(s));

    private static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.SequentialScan);
        return Sha256Hex(fs);
    }

    /// <summary>미디어 ID(절대 경로 또는 MediaRoot 상대 경로) → (절대, 상대). 루트 밖 = 파일명만 사용.</summary>
    private (string Full, string Rel)? ResolveMedia(string id, string root)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        string full;
        try { full = Path.GetFullPath(Path.IsPathRooted(id) ? id : Path.Combine(root, id)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!File.Exists(full)) return null;
        var rel = Path.GetRelativePath(root, full);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) rel = Path.GetFileName(full);
        rel = rel.Replace('\\', '/');
        return IsSafeRelative(rel) ? (full, rel) : null;
    }

    public static string BuildManifestJson(IReadOnlyList<AssetBundleEntry> media, IReadOnlyList<string> presets, DateTimeOffset createdUtc)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("format", FormatId);
            w.WriteNumber("version", FormatVersion);
            w.WriteString("createdUtc", createdUtc.UtcDateTime);
            w.WriteNumber("mediaCount", media.Count);
            w.WriteStartArray("media");
            foreach (var m in media)
            {
                w.WriteStartObject();
                w.WriteString("rel", m.Rel);
                w.WriteNumber("bytes", m.Bytes);
                w.WriteString("sha256", m.Sha256);
                w.WriteBoolean("meta", m.HasMeta);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("presets");
            foreach (var p in presets) w.WriteStringValue(p);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>매니페스트 스키마 즉시 검증 — 형식/버전/건수/경로/해시 불일치 → null.</summary>
    public static (IReadOnlyList<AssetBundleEntry> Media, IReadOnlyList<string> Presets)? TryParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!r.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() != FormatId) return null;
            if (!r.TryGetProperty("version", out var v) || !v.TryGetInt32(out var ver) || ver < 1 || ver > FormatVersion) return null;
            if (!r.TryGetProperty("media", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            var media = new List<AssetBundleEntry>(arr.GetArrayLength());
            foreach (var e in arr.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) return null;
                var rel = e.TryGetProperty("rel", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null;
                var sha = e.TryGetProperty("sha256", out var se) && se.ValueKind == JsonValueKind.String ? se.GetString() : null;
                if (!IsSafeRelative(rel) || sha is not { Length: 64 }) return null;
                var bytes = e.TryGetProperty("bytes", out var be) && be.TryGetInt64(out var b) && b >= 0 ? b : -1;
                if (bytes < 0) return null;
                var meta = e.TryGetProperty("meta", out var me) && me.ValueKind == JsonValueKind.True;
                media.Add(new(rel!, bytes, sha, meta));
            }
            if (r.TryGetProperty("mediaCount", out var mc) && (!mc.TryGetInt32(out var n) || n != media.Count)) return null;
            var presets = new List<string>(3);
            if (r.TryGetProperty("presets", out var pa) && pa.ValueKind == JsonValueKind.Array)
                foreach (var p in pa.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.String && Array.IndexOf(PresetFiles, p.GetString()) >= 0) presets.Add(p.GetString()!);
            return (media, presets);
        }
        catch (JsonException) { return null; }
    }

    public Task<AssetBundleExportResult> ExportBundleAsync(string outputPath, IEnumerable<string> mediaIds, bool includePresets, CancellationToken ct = default)
        => ExportBundleAsync(outputPath, mediaIds, includePresets, null, ct);

    /// <summary>
    /// ⏳ 진행 보고 + 취소 지원 내보내기 — 항목마다 <see cref="BatchProgressReport"/>(완료 수 · 현재 파일명) 보고, 완료 시 (n/n, null).
    /// 취소 시 임시 파일 정리 후 <see cref="OperationCanceledException"/> 전파(완성 번들 미생성).
    /// </summary>
    public async Task<AssetBundleExportResult> ExportBundleAsync(string outputPath, IEnumerable<string> mediaIds, bool includePresets, IProgress<BatchProgressReport>? progress, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(mediaIds);
        var ids = mediaIds as IReadOnlyList<string> ?? new List<string>(mediaIds);
        var total = ids.Count;
        var full = Path.GetFullPath(outputPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? outputPath : outputPath + Extension);
        var tmp = full + ".~pfs";
        var root = MediaRoot;
        var entries = new List<AssetBundleEntry>();
        var presets = new List<string>(3);
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Path.GetDirectoryName(full) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                for (var i = 0; i < total; i++)
                {
                    var id = ids[i];
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new(i, total, SafeFileName(id)));
                    if (ResolveMedia(id, root) is not { } m || !seen.Add(m.Rel)) { skipped.Add(id); continue; }
                    try
                    {
                        var sha = Sha256File(m.Full);
                        await AddFileAsync(zip, m.Full, MediaPrefix + m.Rel, CompressionLevel.NoCompression, ct).ConfigureAwait(false);
                        var meta = ExportMetadata.MetaPathFor(m.Full);
                        var hasMeta = File.Exists(meta);
                        if (hasMeta) await AddFileAsync(zip, meta, MediaPrefix + ExportMetadata.MetaPathFor(m.Rel).Replace('\\', '/'), CompressionLevel.Optimal, ct).ConfigureAwait(false);
                        entries.Add(new(m.Rel, new FileInfo(m.Full).Length, sha, hasMeta));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("AssetBundle.Export", ex); skipped.Add(id); }
                }
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(total, total, null));
                if (includePresets && _presetDir() is { Length: > 0 } pd)
                {
                    foreach (var name in PresetFiles)
                    {
                        var p = Path.Combine(pd, name);
                        if (!File.Exists(p)) continue;
                        try { await AddFileAsync(zip, p, PresetPrefix + name, CompressionLevel.Optimal, ct).ConfigureAwait(false); presets.Add(name); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("AssetBundle.Preset", ex); }
                    }
                }
                var manifest = zip.CreateEntry(ManifestEntry, CompressionLevel.Optimal);
                await using var ms = manifest.Open();
                var bytes = System.Text.Encoding.UTF8.GetBytes(BuildManifestJson(entries, presets, DateTimeOffset.UtcNow));
                await ms.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
            if (entries.Count == 0 && presets.Count == 0)
            {
                TryDelete(tmp);
                return new(false, null, 0, 0, skipped, "번들에 담을 미디어/프리셋이 없습니다.");
            }
            File.Move(tmp, full, overwrite: true);
            return new(true, full, entries.Count, presets.Count, skipped,
                "📦 번들 내보내기 완료 — 미디어 " + entries.Count + "개 · 프리셋 " + presets.Count + "개" + (skipped.Count > 0 ? " · 건너뜀 " + skipped.Count : "") + " → " + full);
        }
        catch (OperationCanceledException) { TryDelete(tmp); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Program.Log("AssetBundle.Export", ex);
            TryDelete(tmp);
            return new(false, null, 0, 0, skipped, "번들 내보내기 실패: " + ex.Message);
        }
    }

    private static string? SafeFileName(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try { return Path.GetFileName(id); }
        catch (ArgumentException) { return id; }
    }

    public async Task<AssetBundleImportResult> ImportBundleAsync(string bundlePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
            return new(false, [], 0, 0, 0, "번들 파일을 찾을 수 없습니다.");
        var imported = new List<string>();
        int dup = 0, merged = 0, restored = 0;
        try
        {
            using var zip = ZipFile.OpenRead(bundlePath);
            var me = zip.GetEntry(ManifestEntry);
            if (me is null) return new(false, [], 0, 0, 0, "⚠ 매니페스트 없음 — 유효한 .pfsbundle 이 아닙니다.");
            string json;
            using (var sr = new StreamReader(me.Open())) json = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
            if (TryParseManifest(json) is not { } man) return new(false, [], 0, 0, 0, "⚠ 매니페스트 스키마 검증 실패.");

            var root = MediaRoot;
            Directory.CreateDirectory(root);
            foreach (var m in man.Media)
            {
                ct.ThrowIfCancellationRequested();
                var ze = zip.GetEntry(MediaPrefix + m.Rel);
                if (ze is null) { Program.Log("AssetBundle.Import", new InvalidDataException("엔트리 누락: " + m.Rel)); continue; }
                try
                {
                    var target = Path.GetFullPath(Path.Combine(root, m.Rel));
                    if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;   // Zip-Slip 이중 방어
                    if (File.Exists(target))
                    {
                        if (string.Equals(Sha256File(target), m.Sha256, StringComparison.OrdinalIgnoreCase)) { dup++; continue; }   // 동일 콘텐츠 = 중복
                        target = UniquePath(target);   // 이름 충돌 · 다른 콘텐츠 → 접미사로 보존(덮어쓰기 금지)
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    var tmp = target + ".~pfs";
                    await ExtractAsync(ze, tmp, ct).ConfigureAwait(false);
                    if (!string.Equals(Sha256File(tmp), m.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tmp);
                        Program.Log("AssetBundle.Import", new InvalidDataException("SHA-256 불일치: " + m.Rel));
                        continue;
                    }
                    File.Move(tmp, target, overwrite: false);
                    if (m.HasMeta && zip.GetEntry(MediaPrefix + ExportMetadata.MetaPathFor(m.Rel).Replace('\\', '/')) is { } mz)
                        await ExtractAsync(mz, ExportMetadata.MetaPathFor(target), ct).ConfigureAwait(false);
                    imported.Add(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Program.Log("AssetBundle.Import", ex); }
            }

            if (man.Presets.Count > 0 && _presetDir() is { Length: > 0 } pd)
            {
                Directory.CreateDirectory(pd);
                foreach (var name in man.Presets)
                {
                    if (zip.GetEntry(PresetPrefix + name) is not { } pz) continue;
                    try
                    {
                        if (name == StockSuitePlaylistService.PlaylistFileName)
                        {
                            string pj;
                            using (var sr = new StreamReader(pz.Open())) pj = await sr.ReadToEndAsync(ct).ConfigureAwait(false);
                            merged = MergePlaylist(pd, StockSuitePlaylistService.TryParse(pj));
                            continue;
                        }
                        var dst = Path.Combine(pd, name);
                        if (File.Exists(dst)) continue;   // 기존 디스플레이 설정 보존
                        await ExtractAsync(pz, dst, ct).ConfigureAwait(false);
                        restored++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Program.Log("AssetBundle.Preset", ex); }
                }
            }
            var ok = imported.Count > 0 || dup > 0 || merged > 0 || restored > 0;
            return new(ok, imported, dup, merged, restored,
                (ok ? "📥 번들 가져오기 완료 — " : "⚠ 가져온 항목 없음 — ") + "미디어 " + imported.Count + "개 · 중복 " + dup + " · 슬롯 병합 " + merged + " · 프리셋 " + restored);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Program.Log("AssetBundle.Import", ex);
            return new(false, imported, dup, merged, restored, "번들 가져오기 실패: " + ex.Message);
        }
    }

    /// <summary>슬롯 키 기준 병합 — 기존 키 유지(덮어쓰기 금지), 신규 키만 추가. 반환 = 추가 슬롯 수.</summary>
    public static int MergePlaylist(string directory, StockSuitePlaylist? incoming)
    {
        if (incoming is null || incoming.Slots.Count == 0) return 0;
        var path = Path.Combine(directory, StockSuitePlaylistService.PlaylistFileName);
        var existing = File.Exists(path) ? StockSuitePlaylistService.TryParse(File.ReadAllText(path)) : null;
        if (existing is null)
        {
            StockSuitePlaylistService.WritePlaylist(directory, incoming);
            return incoming.Slots.Count;
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var slots = new List<PlaylistSlot>(existing.Slots.Count + incoming.Slots.Count);
        foreach (var s in existing.Slots) { keys.Add(s.Key); slots.Add(s); }
        var added = 0;
        foreach (var s in incoming.Slots)
            if (keys.Add(s.Key)) { slots.Add(s); added++; }
        if (added > 0) StockSuitePlaylistService.WritePlaylist(directory, existing with { Slots = slots });
        return added;
    }

    private static string UniquePath(string target)
    {
        var dir = Path.GetDirectoryName(target)!;
        var stem = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var i = 1; i < 1000; i++)
        {
            var p = Path.Combine(dir, stem + " (bundle-" + i + ")" + ext);
            if (!File.Exists(p)) return p;
        }
        return Path.Combine(dir, stem + " (bundle-" + Guid.NewGuid().ToString("N")[..8] + ")" + ext);
    }

    private static async Task AddFileAsync(ZipArchive zip, string path, string entryName, CompressionLevel level, CancellationToken ct)
    {
        var e = zip.CreateEntry(entryName, level);
        await using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var dst = e.Open();
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static async Task ExtractAsync(ZipArchiveEntry e, string path, CancellationToken ct)
    {
        await using var src = e.Open();
        await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
