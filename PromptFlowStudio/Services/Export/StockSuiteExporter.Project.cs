using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Export;

/// <summary>
/// 📁 프로젝트별 하위 폴더 내보내기 — <c>{StockSuite 루트}\{프로젝트슬러그}_{yyyyMMdd}\</c> 에 영상(원본 파일명) · 자막 SRT · <c>project_manifest.json</c>.
/// 프로젝트명이 비면 기존 루트 <c>bg_ambient.mp4</c> 라이브 교체(핫리로드) 경로 그대로.
/// </summary>
public sealed partial class StockSuiteExporter
{
    public const string ProjectManifestName = "project_manifest.json";
    public const int MaxProjectSlugLength = 40;
    public const string DefaultProjectSlug = "PromptFlow";

    /// <summary>프로젝트명 → 안전한 폴더 슬러그(파일명 금지 문자·공백·구두점 → '_' · 연속 '_' 축약 · 최대 40자 · 빈 값 → <see cref="DefaultProjectSlug"/>).</summary>
    public static string SanitizeProjectName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultProjectSlug;
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(Math.Min(raw.Length, MaxProjectSlugLength * 2));
        var lastUnderscore = true;   // 선행 '_' 억제
        foreach (var ch in raw.Trim())
        {
            var bad = char.IsWhiteSpace(ch) || char.IsControl(ch) || Array.IndexOf(invalid, ch) >= 0 || ch is '.' or ',' or '#' or '%' or '&' or '{' or '}' or '$' or '!' or '\'' or '@' or '+' or '`' or '=' or '·';
            if (bad)
            {
                if (!lastUnderscore) { sb.Append('_'); lastUnderscore = true; }
                continue;
            }
            sb.Append(ch);
            lastUnderscore = ch == '_';
            if (sb.Length >= MaxProjectSlugLength) break;
        }
        var slug = sb.ToString().Trim('_');
        if (slug.Length > MaxProjectSlugLength) slug = slug[..MaxProjectSlugLength].TrimEnd('_');
        // 예약 장치명(CON, NUL 등) 회피
        if (slug.Length is 3 or 4 && (slug.StartsWith("CON", StringComparison.OrdinalIgnoreCase) || slug.StartsWith("PRN", StringComparison.OrdinalIgnoreCase)
            || slug.StartsWith("AUX", StringComparison.OrdinalIgnoreCase) || slug.StartsWith("NUL", StringComparison.OrdinalIgnoreCase)
            || slug.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || slug.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            slug = "_" + slug;
        return slug.Length == 0 ? DefaultProjectSlug : slug;
    }

    /// <summary><c>{슬러그}_{yyyyMMdd}</c>(로컬 날짜).</summary>
    public static string BuildProjectFolderName(string? projectName, DateTime localNow)
        => SanitizeProjectName(projectName) + "_" + localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>로컬 시각 공급자(테스트 주입).</summary>
    public Func<DateTime> LocalNow { get; set; } = static () => DateTime.Now;

    /// <summary>
    /// 📁 자막 번인 + 프로젝트 폴더 내보내기. <paramref name="projectName"/> 공백 → 기존 <see cref="ExportWithSubtitlesAsync(string?, IReadOnlyList{SubtitleCue}?, CancellationToken)"/>(루트 라이브 교체).
    /// 루트 미설정/부재 → <see cref="StockSuiteExportResult.NeedsPathSetup"/>. 매니페스트 기록 실패는 격리(영상 결과 보존).
    /// </summary>
    public async Task<StockSuiteExportResult> ExportWithSubtitlesAsync(string? sourcePath, IReadOnlyList<SubtitleCue>? cues, string? projectName,
        CancellationToken ct = default, PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit? snsKit = null, string? prompt = null)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return await ExportWithSubtitlesAsync(sourcePath, cues, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return new(false, null, false, "원본 영상을 찾을 수 없습니다.");
        var root = ConfiguredDirectory;
        if (root is null) return new(false, null, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");
        string fullRoot;
        try { fullRoot = Path.GetFullPath(root.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(false, null, true, "StockSuite 에셋 경로가 올바르지 않습니다: " + root); }
        if (!Directory.Exists(fullRoot)) return new(false, null, true, "StockSuite 에셋 경로가 존재하지 않습니다: " + fullRoot + " — 환경설정에서 경로를 재설정하세요.");

        var now = LocalNow();
        var folder = BuildProjectFolderName(projectName, now);
        var dir = Path.Combine(fullRoot, folder);
        var asset = Path.GetFileName(sourcePath);

        BurnSubtitles = cues is { Count: > 0 };
        SubtitleCues = BurnSubtitles ? cues : null;
        StockSuiteExportResult r;
        try { r = await ExportAsync(sourcePath, dir, asset, ct).ConfigureAwait(false); }
        finally { BurnSubtitles = false; SubtitleCues = null; }
        if (!r.Ok || r.TargetPath is not { } target) return r;

        var manifest = WriteProjectManifest(dir, projectName!.Trim(), folder, target, sourcePath, snsKit, prompt, new DateTimeOffset(now));
        return r with { Message = r.Message + " · 📁 " + folder + (manifest is null ? "" : " · 🧾 " + ProjectManifestName) };
    }

    /// <summary>
    /// 🧾 <c>project_manifest.json</c> 원자 기록 — 프로젝트 메타 + 내보내기 이력(같은 파일 재내보내기 → 항목 갱신).
    /// 반환: 매니페스트 경로(실패 → null · 격리).
    /// </summary>
    internal static string? WriteProjectManifest(string dir, string projectName, string folder, string targetPath, string sourcePath,
        PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit? snsKit, string? prompt, DateTimeOffset now)
    {
        var path = Path.Combine(dir, ProjectManifestName);
        var tmp = path + TempExt;
        try
        {
            var prior = new List<string>();   // 기존 exports 원문(JSON) — 같은 파일 항목 제외
            var created = now;
            var file = Path.GetFileName(targetPath);
            if (File.Exists(path))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("createdAt", out var c) && c.TryGetDateTimeOffset(out var cd)) created = cd;
                    if (doc.RootElement.TryGetProperty("exports", out var ex) && ex.ValueKind == JsonValueKind.Array)
                        foreach (var e in ex.EnumerateArray())
                            if (!(e.TryGetProperty("file", out var f) && string.Equals(f.GetString(), file, StringComparison.OrdinalIgnoreCase)))
                                prior.Add(e.GetRawText());
                }
                catch (JsonException) { /* 손상 → 새로 기록 */ }
            }

            var srt = Path.ChangeExtension(targetPath, ".srt");
            var buf = new System.Buffers.ArrayBufferWriter<byte>(1024);
            using (var w = new Utf8JsonWriter(buf, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("projectName", projectName);
                w.WriteString("folder", folder);
                w.WriteString("createdAt", created.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                w.WriteString("updatedAt", now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(prompt)) w.WriteString("prompt", prompt.Trim());
                if (snsKit is not null)
                {
                    w.WriteStartObject("sns");
                    w.WriteStartArray("titles");
                    foreach (var t in snsKit.Titles) w.WriteStringValue(t);
                    w.WriteEndArray();
                    w.WriteString("captionKo", snsKit.CaptionKo);
                    w.WriteString("captionEn", snsKit.CaptionEn);
                    w.WriteStartArray("hashtags");
                    foreach (var h in snsKit.Hashtags) w.WriteStringValue(h);
                    w.WriteEndArray();
                    w.WriteString("postingWindow", snsKit.RecommendedPostingWindow);
                    w.WriteEndObject();
                }
                w.WriteStartArray("exports");
                foreach (var raw in prior)
                {
                    using var d = JsonDocument.Parse(raw);
                    d.RootElement.WriteTo(w);
                }
                w.WriteStartObject();
                w.WriteString("file", file);
                w.WriteString("source", Path.GetFullPath(sourcePath));
                long bytes;
                try { bytes = new FileInfo(targetPath).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { bytes = 0; }
                w.WriteNumber("bytes", bytes);
                if (File.Exists(srt)) w.WriteString("subtitles", Path.GetFileName(srt));
                w.WriteString("exportedAt", now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllBytes(tmp, buf.WrittenSpan.ToArray());
            File.Move(tmp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Program.Log("StockSuite.ProjectManifest", ex);
            TryDelete(tmp);
            return null;
        }
    }

    public const string SeriesManifestName = "series_manifest.json";

    /// <summary><c>Ep01</c> · <c>Ep02</c> … 편 폴더명.</summary>
    public static string EpisodeFolderName(int episodeNumber) => "Ep" + episodeNumber.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>
    /// 📦 시리즈 전체 일괄 내보내기 — <c>{루트}\{시리즈테마슬러그}_{yyyyMMdd}\Ep01\</c>… 에 편별 렌더 영상 + <c>project_manifest.json</c>(편 자막 · SNS 키트),
    /// 루트에 <c>series_manifest.json</c>. <paramref name="episodeVideoPaths"/>[i] 는 i번째 편 영상(누락/부재 편은 건너뛰고 [부분 구제]).
    /// 루트 미설정/부재 → <see cref="StockSuiteExportResult.NeedsPathSetup"/>. 성공 시 <c>TargetPath</c> = 시리즈 폴더.
    /// </summary>
    public async Task<StockSuiteExportResult> ExportSeriesBatchToStockSuiteAsync(string stockSuiteRoot, PromptFlowStudio.Services.Pluto.PlutoSeriesBatchResult seriesBatch,
        IReadOnlyList<string> episodeVideoPaths, CancellationToken ct = default)
    {
        if (seriesBatch is null || seriesBatch.Episodes.Count == 0) return new(false, null, false, "내보낼 시리즈가 없습니다.");
        if (episodeVideoPaths is null) return new(false, null, false, "편별 렌더 영상이 없습니다.");
        if (string.IsNullOrWhiteSpace(stockSuiteRoot)) return new(false, null, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");
        string fullRoot;
        try { fullRoot = Path.GetFullPath(stockSuiteRoot.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(false, null, true, "StockSuite 에셋 경로가 올바르지 않습니다: " + stockSuiteRoot); }
        if (!Directory.Exists(fullRoot)) return new(false, null, true, "StockSuite 에셋 경로가 존재하지 않습니다: " + fullRoot + " — 환경설정에서 경로를 재설정하세요.");

        var now = LocalNow();
        var stamp = new DateTimeOffset(now);
        var folder = BuildProjectFolderName(seriesBatch.SeriesTheme, now);
        var seriesDir = Path.Combine(fullRoot, folder);
        var exported = new List<(int Number, string Folder, string File, string Title)>(seriesBatch.Episodes.Count);
        var skipped = 0;
        try
        {
            Directory.CreateDirectory(seriesDir);
            for (var i = 0; i < seriesBatch.Episodes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ep = seriesBatch.Episodes[i];
                var src = i < episodeVideoPaths.Count ? episodeVideoPaths[i] : null;
                if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) { skipped++; continue; }
                var epFolder = EpisodeFolderName(ep.EpisodeNumber);
                var epDir = Path.Combine(seriesDir, epFolder);
                try
                {
                    Directory.CreateDirectory(epDir);
                    var target = Path.Combine(epDir, Path.GetFileName(src));
                    var tmp = target + TempExt;
                    await using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
                    await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    File.Move(tmp, target, overwrite: true);
                    WriteEpisodeManifest(epDir, seriesBatch, ep, epFolder, target, src, stamp);
                    exported.Add((ep.EpisodeNumber, epFolder, Path.GetFileName(target), ep.EpisodeTitle));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Program.Log("StockSuite.SeriesEpisode", ex);   // [부분 구제] 다음 편 계속
                    skipped++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.SeriesBatch", ex);
            return new(false, null, false, "시리즈 폴더를 만들 수 없습니다: " + ex.Message);
        }
        if (exported.Count == 0) return new(false, null, false, "렌더된 편 영상이 없습니다 — [🎞️ 전편 순차 출력] 후 다시 시도하세요.");

        var manifest = WriteSeriesManifest(seriesDir, seriesBatch, folder, exported, stamp);
        var msg = "📦 시리즈 " + exported.Count.ToString(CultureInfo.InvariantCulture) + "/" + seriesBatch.Episodes.Count.ToString(CultureInfo.InvariantCulture)
                  + "편 내보내기 · 📁 " + folder + (manifest is null ? "" : " · 🧾 " + SeriesManifestName)
                  + (skipped > 0 ? " · [부분 구제] " + skipped.ToString(CultureInfo.InvariantCulture) + "편 누락" : "");
        return new(true, seriesDir, false, msg);
    }

    private static readonly JsonWriterOptions s_manifestJson = new() { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static void WriteSnsKit(Utf8JsonWriter w, PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit? kit)
    {
        if (kit is null) return;
        w.WriteStartObject("sns");
        w.WriteStartArray("titles");
        foreach (var t in kit.Titles) w.WriteStringValue(t);
        w.WriteEndArray();
        w.WriteString("captionKo", kit.CaptionKo);
        w.WriteString("captionEn", kit.CaptionEn);
        w.WriteStartArray("hashtags");
        foreach (var h in kit.Hashtags) w.WriteStringValue(h);
        w.WriteEndArray();
        w.WriteString("postingWindow", kit.RecommendedPostingWindow);
        w.WriteEndObject();
    }

    private static void WriteJsonAtomic(string path, System.Buffers.ArrayBufferWriter<byte> buf)
    {
        var tmp = path + TempExt;
        try
        {
            File.WriteAllBytes(tmp, buf.WrittenSpan.ToArray());
            File.Move(tmp, path, overwrite: true);
        }
        catch { TryDelete(tmp); throw; }
    }

    /// <summary>편 폴더 <c>project_manifest.json</c> — 편 메타 · "n/N편" 자막 · 편별 SNS 키트 · 영상 파일. 실패 격리(로그).</summary>
    internal static string? WriteEpisodeManifest(string epDir, PromptFlowStudio.Services.Pluto.PlutoSeriesBatchResult series, PromptFlowStudio.Services.Pluto.PlutoSeriesEpisodePlan ep,
        string epFolder, string targetPath, string sourcePath, DateTimeOffset now)
    {
        var path = Path.Combine(epDir, ProjectManifestName);
        try
        {
            var buf = new System.Buffers.ArrayBufferWriter<byte>(2048);
            using (var w = new Utf8JsonWriter(buf, s_manifestJson))
            {
                var ts = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("projectName", ep.EpisodeTitle);
                w.WriteString("seriesTheme", series.SeriesTheme);
                w.WriteNumber("episodeNumber", ep.EpisodeNumber);
                w.WriteNumber("totalEpisodes", ep.TotalEpisodes);
                w.WriteString("folder", epFolder);
                w.WriteString("createdAt", ts);
                w.WriteString("updatedAt", ts);
                w.WriteStartArray("subtitles");
                foreach (var c in ep.Subtitles)
                {
                    w.WriteStartObject();
                    w.WriteNumber("startSec", Math.Round(c.EffectiveStartSec, 3));
                    w.WriteNumber("endSec", Math.Round(c.EffectiveEndSec, 3));
                    w.WriteString("text", c.Text);
                    if (!string.IsNullOrWhiteSpace(c.SecondaryText)) w.WriteString("secondaryText", c.SecondaryText);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                WriteSnsKit(w, ep.PublishingKit);
                w.WriteStartArray("exports");
                w.WriteStartObject();
                w.WriteString("file", Path.GetFileName(targetPath));
                w.WriteString("source", Path.GetFullPath(sourcePath));
                long bytes;
                try { bytes = new FileInfo(targetPath).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { bytes = 0; }
                w.WriteNumber("bytes", bytes);
                w.WriteString("exportedAt", ts);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            WriteJsonAtomic(path, buf);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Program.Log("StockSuite.EpisodeManifest", ex);
            return null;
        }
    }

    /// <summary>시리즈 루트 <c>series_manifest.json</c> — 테마 · 편 수 · 고유 컷 집계 · 편 목록(폴더 · 파일 · 제목).</summary>
    internal static string? WriteSeriesManifest(string seriesDir, PromptFlowStudio.Services.Pluto.PlutoSeriesBatchResult series, string folder,
        IReadOnlyList<(int Number, string Folder, string File, string Title)> exported, DateTimeOffset now)
    {
        var path = Path.Combine(seriesDir, SeriesManifestName);
        try
        {
            var buf = new System.Buffers.ArrayBufferWriter<byte>(1024);
            using (var w = new Utf8JsonWriter(buf, s_manifestJson))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("seriesTheme", series.SeriesTheme);
                w.WriteString("folder", folder);
                w.WriteNumber("totalEpisodes", series.Episodes.Count);
                w.WriteNumber("exportedEpisodes", exported.Count);
                w.WriteNumber("totalInputPhotos", series.TotalInputPhotos);
                w.WriteNumber("totalUsedUniquePhotos", series.TotalUsedUniquePhotos);
                w.WriteString("exportedAt", now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                w.WriteStartArray("episodes");
                foreach (var e in exported)
                {
                    w.WriteStartObject();
                    w.WriteNumber("episodeNumber", e.Number);
                    w.WriteString("title", e.Title);
                    w.WriteString("folder", e.Folder);
                    w.WriteString("file", e.File);
                    w.WriteString("manifest", e.Folder + "/" + ProjectManifestName);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            WriteJsonAtomic(path, buf);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Program.Log("StockSuite.SeriesManifest", ex);
            return null;
        }
    }
}
