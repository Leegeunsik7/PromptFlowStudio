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
        CancellationToken ct = default, PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit? snsKit = null, string? prompt = null,
        IReadOnlyList<SubtitleCue>? sidecarSubtitles = null)
    {
        if (string.IsNullOrWhiteSpace(projectName)) return await ExportWithSubtitlesAsync(sourcePath, cues, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return new(false, null, false, "원본 영상을 찾을 수 없습니다.");
        if (ExportMediaIntegrityGuard.IsIsoBmffPath(sourcePath))   // 🛡️ 잘린/손상 MP4 → 프로젝트 폴더 생성 전 차단
        {
            try { ExportMediaIntegrityGuard.EnsureValidMp4(sourcePath); }
            catch (InvalidOperationException ex) { return new(false, null, false, ex.Message); }
        }
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

        var bundle = await WriteCreatorSidecarsAsync(dir, target, sidecarSubtitles ?? cues, snsKit, projectName, ct).ConfigureAwait(false);   // 🎁 자막 · SNS 게시문 · 썸네일(격리)
        var manifest = WriteProjectManifest(dir, projectName!.Trim(), folder, target, sourcePath, snsKit, prompt, new DateTimeOffset(now), bundle);
        return r with
        {
            Message = r.Message + " · 📁 " + folder + (manifest is null ? "" : " · 🧾 " + ProjectManifestName),
            HasSubtitlesSrt = bundle.HasSubtitlesSrt, HasSnsCopyText = bundle.HasSnsCopyText, HasThumbnailJpg = bundle.HasThumbnailJpg,
        };
    }

    // ══════════════════════ 🎁 크리에이터 업로드 번들(자막 SRT · SNS 게시문 · 커버 썸네일) ══════════════════════
    public const string SubRipFileName = "subtitles.srt";
    public const string SnsCopyFileName = "SNS_게시문.txt";
    public const string ThumbnailFileName = "thumbnail.jpg";
    public const string ShortsCopyHeader = "[YouTube Shorts 업로드용 (제목 95자 이내)]";
    public const string ReelsCopyHeader = "[Instagram Reels / TikTok 업로드용]";
    /// <summary>썸네일 추출 1회 시도 타임아웃(초과 → 건너뜀 · 내보내기 비차단).</summary>
    public TimeSpan ThumbnailTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>🎁 번들 사이드카 생성 결과.</summary>
    public readonly record struct CreatorBundleResult(bool HasSubtitlesSrt, bool HasSnsCopyText, bool HasThumbnailJpg);

    /// <summary>
    /// 📝 표준 SubRip(CRLF) — <c>1\r\n00:00:00,000 --> 00:00:04,500\r\n텍스트\r\n\r\n</c>. 보조 자막(EN) 있으면 2번째 줄.
    /// 빈/역전/음수 큐 제외 · 유효 큐 없음 → 빈 문자열.
    /// </summary>
    public static string BuildSubRipContent(IReadOnlyList<SubtitleCue>? subtitles)
    {
        if (subtitles is null || subtitles.Count == 0) return "";
        var sb = new StringBuilder(128 * subtitles.Count);
        var n = 0;
        for (var i = 0; i < subtitles.Count; i++)
        {
            var c = subtitles[i];
            if (string.IsNullOrWhiteSpace(c.Text) || c.EffectiveEndSec <= c.EffectiveStartSec || c.EffectiveStartSec < 0) continue;
            n++;
            sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            AppendSrtTime(sb, c.EffectiveStartSec).Append(" --> ");
            AppendSrtTime(sb, c.EffectiveEndSec).Append("\r\n");
            sb.Append(c.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim().Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(c.SecondaryText))
                sb.Append(c.SecondaryText.Replace('\r', ' ').Replace('\n', ' ').Trim()).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>📋 <c>SNS_게시문.txt</c> 본문 — Shorts / Reels·TikTok 2개 섹션(CRLF · 메모장 붙여넣기 호환).</summary>
    public static string BuildSnsCopyText(PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        var sb = new StringBuilder(1024);
        sb.Append(ShortsCopyHeader).Append('\n').Append(PromptFlowStudio.Services.Pluto.PlutoMultiFormatPackager.FormatForYouTubeShorts(kit)).Append('\n').Append('\n');
        sb.Append(ReelsCopyHeader).Append('\n').Append(PromptFlowStudio.Services.Pluto.PlutoMultiFormatPackager.FormatForInstagramReels(kit)).Append('\n');
        return sb.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n").ToString();
    }

    /// <summary>
    /// 🎁 대상 폴더에 <see cref="SubRipFileName"/>(자막 있을 때) · <see cref="SnsCopyFileName"/>(키트 없으면 템플릿 자동 생성) · <see cref="ThumbnailFileName"/>(ffmpeg 있을 때) 기록.
    /// 각 단계 독립 격리 — 어떤 실패도 내보내기를 실패시키지 않는다.
    /// </summary>
    internal async Task<CreatorBundleResult> WriteCreatorSidecarsAsync(string dir, string videoPath, IReadOnlyList<SubtitleCue>? subtitles,
        PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit? kit, string? themeHint, CancellationToken ct)
    {
        var srt = false;
        var srtText = BuildSubRipContent(subtitles);
        if (srtText.Length > 0) srt = TryWriteTextAtomic(Path.Combine(dir, SubRipFileName), srtText, "StockSuite.BundleSrt");

        var sns = false;
        try
        {
            kit ??= PromptFlowStudio.Services.Pluto.PlutoMultiFormatPackager.BuildTemplateKit(themeHint, null, subtitles, null, UtcNow().UtcDateTime);
            sns = TryWriteTextAtomic(Path.Combine(dir, SnsCopyFileName), BuildSnsCopyText(kit), "StockSuite.BundleSns");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Program.Log("StockSuite.BundleSns", ex); }

        var thumb = await TryExtractThumbnailAsync(videoPath, dir, ct).ConfigureAwait(false);
        return new(srt, sns, thumb);
    }

    private static bool TryWriteTextAtomic(string path, string text, string logTag)
    {
        var tmp = path + TempExt;
        try
        {
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log(logTag, ex);
            return false;
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>
    /// 🖼️ 커버 프레임 <c>thumbnail.jpg</c> — 디스크에 ffmpeg 가 있을 때만 <c>00:00:01.000</c> → (짧은 클립) <c>00:00:00.200</c> 순 시도.
    /// ffmpeg 부재 · 합성 스텁 입력 · 비정상 종료 · 타임아웃 · 예외 → false(내보내기 비차단). JPEG SOI(FF D8) 검증.
    /// </summary>
    internal async Task<bool> TryExtractThumbnailAsync(string videoPath, string dir, CancellationToken ct)
    {
        var path = Path.Combine(dir, ThumbnailFileName);
        var tmp = Path.Combine(dir, "thumbnail" + TempExt + ".jpg");
        try
        {
            string? ffmpeg;
            try { ffmpeg = _resolveFfmpeg(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Program.Log("StockSuite.Thumbnail", ex); return false; }
            if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg) || !File.Exists(videoPath)) return false;
            foreach (var seek in s_thumbnailSeeks)
            {
                ct.ThrowIfCancellationRequested();
                TryDelete(tmp);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ThumbnailTimeout);
                bool ok;
                try
                {
                    ok = await _run(ffmpeg, new[] { "-y", "-hide_banner", "-loglevel", "error", "-ss", seek, "-i", videoPath, "-frames:v", "1", "-q:v", "2", "-an", "-update", "1", tmp }, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { ok = false; }   // ⏱ 타임아웃 → 다음 시점
                if (!ok || !IsJpeg(tmp)) continue;
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is not OperationCanceledException) Program.Log("StockSuite.Thumbnail", ex);
            return false;
        }
        finally { TryDelete(tmp); }
    }

    private static readonly string[] s_thumbnailSeeks = { "00:00:01.000", "00:00:00.200" };

    private static bool IsJpeg(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 4) return false;
            Span<byte> head = stackalloc byte[2];
            fs.ReadExactly(head);
            return head[0] == 0xFF && head[1] == 0xD8;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>📣 토스트용 번들 라벨 — 예: <c>영상 + 자막(.srt) + SNS 게시문 + 썸네일 저장 완료</c>.</summary>
    public static string DescribeCreatorBundle(bool srt, bool sns, bool thumb)
        => "영상" + (srt ? " + 자막(.srt)" : "") + (sns ? " + SNS 게시문" : "") + (thumb ? " + 썸네일" : "") + " 저장 완료";

    /// <summary>
    /// 🧾 <c>project_manifest.json</c> 원자 기록 — 프로젝트 메타 + 내보내기 이력(같은 파일 재내보내기 → 항목 갱신).
    /// 반환: 매니페스트 경로(실패 → null · 격리).
    /// </summary>
    internal static string? WriteProjectManifest(string dir, string projectName, string folder, string targetPath, string sourcePath,
        PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit? snsKit, string? prompt, DateTimeOffset now, CreatorBundleResult bundle = default)
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
                WriteBundleFlags(w, bundle);
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
        int skipped = 0, corrupt = 0, srtCount = 0, snsCount = 0, thumbCount = 0;
        try
        {
            Directory.CreateDirectory(seriesDir);
            for (var i = 0; i < seriesBatch.Episodes.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var ep = seriesBatch.Episodes[i];
                var src = i < episodeVideoPaths.Count ? episodeVideoPaths[i] : null;
                if (string.IsNullOrWhiteSpace(src) || !File.Exists(src)) { skipped++; continue; }
                if (ExportMediaIntegrityGuard.IsIsoBmffPath(src))   // 🛡️ 잘린/손상 편 → 편 폴더 복사 전 차단 · 다음 편 계속
                {
                    try { ExportMediaIntegrityGuard.EnsureValidMp4(src); }
                    catch (InvalidOperationException ex) { Program.Log("StockSuite.SeriesIntegrity", ex); skipped++; corrupt++; continue; }
                }
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
                    var bundle = await WriteCreatorSidecarsAsync(epDir, target, ep.Subtitles, ep.PublishingKit, ep.EpisodeTitle, ct).ConfigureAwait(false);   // 🎁 편별 번들(격리)
                    if (bundle.HasSubtitlesSrt) srtCount++;
                    if (bundle.HasSnsCopyText) snsCount++;
                    if (bundle.HasThumbnailJpg) thumbCount++;
                    WriteEpisodeManifest(epDir, seriesBatch, ep, epFolder, target, src, stamp, bundle);
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
        if (exported.Count == 0)
        {
            try { if (Directory.Exists(seriesDir) && Directory.GetFileSystemEntries(seriesDir).Length == 0) Directory.Delete(seriesDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("StockSuite.SeriesCleanup", ex); }
            return new(false, null, false, corrupt > 0
                ? "🛡️ 편 영상 " + corrupt.ToString(CultureInfo.InvariantCulture) + "개가 잘렸거나 손상되어 내보내기를 차단했습니다 — 다시 렌더한 뒤 시도하세요."
                : "렌더된 편 영상이 없습니다 — [🎞️ 전편 순차 출력] 후 다시 시도하세요.");
        }

        var manifest = WriteSeriesManifest(seriesDir, seriesBatch, folder, exported, stamp);
        var msg = "📦 시리즈 " + exported.Count.ToString(CultureInfo.InvariantCulture) + "/" + seriesBatch.Episodes.Count.ToString(CultureInfo.InvariantCulture)
                  + "편 내보내기 · 📁 " + folder + (manifest is null ? "" : " · 🧾 " + SeriesManifestName)
                  + " · 🎁 자막 " + srtCount.ToString(CultureInfo.InvariantCulture) + " · SNS 게시문 " + snsCount.ToString(CultureInfo.InvariantCulture) + " · 썸네일 " + thumbCount.ToString(CultureInfo.InvariantCulture)
                  + (skipped > 0 ? " · [부분 구제] " + skipped.ToString(CultureInfo.InvariantCulture) + "편 누락" + (corrupt > 0 ? "(🛡️ 손상 " + corrupt.ToString(CultureInfo.InvariantCulture) + "편 차단)" : "") : "");
        return new(true, seriesDir, false, msg) { HasSubtitlesSrt = srtCount > 0, HasSnsCopyText = snsCount > 0, HasThumbnailJpg = thumbCount > 0 };
    }

    // ══════════════════════ ⚖️ A/B 실험 짝 내보내기 ══════════════════════
    public const string AbExperimentManifestName = "ab_experiment_manifest.json";
    public const string AbVariantAFolder = "VariantA";
    public const string AbVariantBFolder = "VariantB";

    /// <summary><c>{테마슬러그}_AB_{yyyyMMdd}</c>(로컬 날짜).</summary>
    public static string BuildAbExperimentFolderName(string? themeHint, DateTime localNow)
        => SanitizeProjectName(themeHint) + "_AB_" + localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 📦 A/B 실험 짝 내보내기 — <c>{루트}\{테마슬러그}_AB_{yyyyMMdd}\VariantA\</c> · <c>\VariantB\</c> 에 변형별 영상 + 자막 SRT · SNS 게시문 · 썸네일(best-effort) · <c>project_manifest.json</c>,
    /// 루트에 <c>ab_experiment_manifest.json</c>(실험 ID · 가설 · 변형 특성). 각 영상은 <see cref="ExportMediaIntegrityGuard.EnsureValidMp4"/> 검증(손상/누락 → 해당 변형만 건너뜀 · [부분 구제]).
    /// 루트 미설정/부재 → <see cref="StockSuiteExportResult.NeedsPathSetup"/>. 성공 시 <c>TargetPath</c> = 실험 폴더.
    /// </summary>
    public async Task<StockSuiteExportResult> ExportABExperimentToStockSuiteAsync(string stockSuiteRoot, PromptFlowStudio.Services.Pluto.PlutoVariantABPlan plan,
        string? championVideoPath, string? challengerVideoPath, CancellationToken ct = default)
    {
        if (plan is null || string.IsNullOrWhiteSpace(plan.ExperimentId)) return new(false, null, false, "내보낼 A/B 실험이 없습니다.");
        if (string.IsNullOrWhiteSpace(championVideoPath) && string.IsNullOrWhiteSpace(challengerVideoPath)) return new(false, null, false, "A/B 변형 렌더 영상이 없습니다 — [⚡ A/B 두 변형 연속 렌더링] 후 다시 시도하세요.");
        if (string.IsNullOrWhiteSpace(stockSuiteRoot)) return new(false, null, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");
        string fullRoot;
        try { fullRoot = Path.GetFullPath(stockSuiteRoot.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(false, null, true, "StockSuite 에셋 경로가 올바르지 않습니다: " + stockSuiteRoot); }
        if (!Directory.Exists(fullRoot)) return new(false, null, true, "StockSuite 에셋 경로가 존재하지 않습니다: " + fullRoot + " — 환경설정에서 경로를 재설정하세요.");

        var now = LocalNow();
        var stamp = new DateTimeOffset(now);
        var folder = BuildAbExperimentFolderName(plan.ThemeHint, now);
        var abDir = Path.Combine(fullRoot, folder);
        var exported = new List<(string Slot, string Folder, string File, PromptFlowStudio.Services.Pluto.PlutoExperimentVariant Variant)>(2);
        int skipped = 0, corrupt = 0, srtCount = 0, snsCount = 0, thumbCount = 0;
        try
        {
            Directory.CreateDirectory(abDir);
            for (var i = 0; i < 2; i++)
            {
                ct.ThrowIfCancellationRequested();
                var variant = i == 0 ? plan.ChampionA : plan.ChallengerB;
                var slot = i == 0 ? AbVariantAFolder : AbVariantBFolder;
                var src = i == 0 ? championVideoPath : challengerVideoPath;
                if (string.IsNullOrWhiteSpace(src)) { skipped++; continue; }
                try { ExportMediaIntegrityGuard.EnsureValidMp4(src); }   // 🛡️ 잘림/손상/부재 → 해당 변형만 차단
                catch (InvalidOperationException ex) { Program.Log("StockSuite.AbIntegrity", ex); skipped++; corrupt++; continue; }
                var dir = Path.Combine(abDir, slot);
                try
                {
                    Directory.CreateDirectory(dir);
                    var target = Path.Combine(dir, Path.GetFileName(src));
                    var tmp = target + TempExt;
                    await using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
                    await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                        await input.CopyToAsync(output, ct).ConfigureAwait(false);
                    File.Move(tmp, target, overwrite: true);
                    var hint = string.IsNullOrWhiteSpace(plan.ThemeHint) ? variant.HookStyle : plan.ThemeHint;
                    var bundle = await WriteCreatorSidecarsAsync(dir, target, variant.Subtitles, null, hint, ct).ConfigureAwait(false);   // 🎁 변형별 번들(격리)
                    if (bundle.HasSubtitlesSrt) srtCount++;
                    if (bundle.HasSnsCopyText) snsCount++;
                    if (bundle.HasThumbnailJpg) thumbCount++;
                    WriteAbVariantManifest(dir, plan, variant, slot, target, src, stamp, bundle);
                    exported.Add((slot, slot, Path.GetFileName(target), variant));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Program.Log("StockSuite.AbVariant", ex);   // [부분 구제] 다른 변형 계속
                    skipped++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.AbExperiment", ex);
            return new(false, null, false, "A/B 실험 폴더를 만들 수 없습니다: " + ex.Message);
        }
        if (exported.Count == 0)
        {
            try { if (Directory.Exists(abDir) && Directory.GetFileSystemEntries(abDir).Length == 0) Directory.Delete(abDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("StockSuite.AbCleanup", ex); }
            return new(false, null, false, corrupt > 0
                ? "🛡️ A/B 변형 영상 " + corrupt.ToString(CultureInfo.InvariantCulture) + "개가 잘렸거나 손상되어 내보내기를 차단했습니다 — 다시 렌더한 뒤 시도하세요."
                : "렌더된 A/B 변형 영상이 없습니다 — [⚡ A/B 두 변형 연속 렌더링] 후 다시 시도하세요.");
        }

        var manifest = WriteAbExperimentManifest(abDir, plan, folder, exported, stamp);
        var msg = "📦 A/B 실험 " + exported.Count.ToString(CultureInfo.InvariantCulture) + "/2 변형 내보내기 · 📁 " + folder + (manifest is null ? "" : " · 🧾 " + AbExperimentManifestName)
                  + " · 🎁 자막 " + srtCount.ToString(CultureInfo.InvariantCulture) + " · SNS 게시문 " + snsCount.ToString(CultureInfo.InvariantCulture) + " · 썸네일 " + thumbCount.ToString(CultureInfo.InvariantCulture)
                  + (skipped > 0 ? " · [부분 구제] " + skipped.ToString(CultureInfo.InvariantCulture) + "변형 누락" + (corrupt > 0 ? "(🛡️ 손상 " + corrupt.ToString(CultureInfo.InvariantCulture) + "개 차단)" : "") : "");
        return new(true, abDir, false, msg) { HasSubtitlesSrt = srtCount > 0, HasSnsCopyText = snsCount > 0, HasThumbnailJpg = thumbCount > 0 };
    }

    // ══════════════════════ 🎬 16:9 + 9:16 듀얼 마스터 프로젝트 번들 ══════════════════════

    /// <summary><c>{프로젝트슬러그}_Dual_{yyyyMMdd}</c>(로컬 날짜).</summary>
    public static string BuildDualMasterFolderName(string? projectName, DateTime localNow)
        => SanitizeProjectName(projectName) + "_Dual_" + localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 📦 듀얼 마스터 번들 — <c>{루트}\{슬러그}_Dual_{yyyyMMdd}\</c> 에 16:9 · 9:16 영상 + 자막 SRT · SNS 게시문 · 커버 썸네일(best-effort, 9:16 우선) · <c>project_manifest.json</c>(<c>IsDualMaster</c>).
    /// 두 영상 모두 <see cref="ExportMediaIntegrityGuard.EnsureValidMp4"/> 통과해야 폴더 생성(한쪽 손상 → 전체 차단 · 짝 불일치 번들 방지).
    /// </summary>
    public async Task<StockSuiteExportResult> ExportDualMasterBundleToStockSuiteAsync(string stockSuiteRoot, string? projectName, string landscape16x9Path, string vertical9x16Path,
        IReadOnlyList<SubtitleCue>? subtitles = null, PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit? publishingKit = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(landscape16x9Path) || string.IsNullOrWhiteSpace(vertical9x16Path))
            return new(false, null, false, "듀얼 마스터 영상(16:9 · 9:16)이 모두 필요합니다 — [🎬 16:9 + 9:16 듀얼 마스터 동시 출력] 후 다시 시도하세요.");
        if (string.IsNullOrWhiteSpace(stockSuiteRoot)) return new(false, null, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");
        string fullRoot;
        try { fullRoot = Path.GetFullPath(stockSuiteRoot.Trim().Trim('"')); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new(false, null, true, "StockSuite 에셋 경로가 올바르지 않습니다: " + stockSuiteRoot); }
        if (!Directory.Exists(fullRoot)) return new(false, null, true, "StockSuite 에셋 경로가 존재하지 않습니다: " + fullRoot + " — 환경설정에서 경로를 재설정하세요.");

        try { ExportMediaIntegrityGuard.EnsureValidMp4(landscape16x9Path); }   // 🛡️ 폴더 생성 전 차단
        catch (InvalidOperationException ex) { Program.Log("StockSuite.DualIntegrity", ex); return new(false, null, false, "🛡️ 16:9 마스터 차단 · " + ex.Message); }
        try { ExportMediaIntegrityGuard.EnsureValidMp4(vertical9x16Path); }
        catch (InvalidOperationException ex) { Program.Log("StockSuite.DualIntegrity", ex); return new(false, null, false, "🛡️ 9:16 마스터 차단 · " + ex.Message); }

        var now = LocalNow();
        var stamp = new DateTimeOffset(now);
        var name = string.IsNullOrWhiteSpace(projectName) ? DefaultProjectSlug : projectName.Trim();
        var folder = BuildDualMasterFolderName(name, now);
        var dir = Path.Combine(fullRoot, folder);
        var landName = Path.GetFileName(landscape16x9Path);
        var vertName = Path.GetFileName(vertical9x16Path);
        if (string.Equals(landName, vertName, StringComparison.OrdinalIgnoreCase)) { landName = "16x9_" + landName; vertName = "9x16_" + vertName; }   // 파일명 충돌 방지
        string landTarget, vertTarget;
        try
        {
            Directory.CreateDirectory(dir);
            landTarget = await CopyAtomicAsync(landscape16x9Path, Path.Combine(dir, landName), ct).ConfigureAwait(false);
            vertTarget = await CopyAtomicAsync(vertical9x16Path, Path.Combine(dir, vertName), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuite.DualBundle", ex);
            return new(false, null, false, "듀얼 마스터 폴더에 영상을 복사할 수 없습니다: " + ex.Message);
        }

        var bundle = await WriteCreatorSidecarsAsync(dir, vertTarget, subtitles, publishingKit, name, ct).ConfigureAwait(false);   // 🎁 격리
        var manifest = WriteDualMasterManifest(dir, name, folder, landTarget, vertTarget, landscape16x9Path, vertical9x16Path, publishingKit, stamp, bundle);
        var msg = "📦 듀얼 마스터 번들 · 📁 " + folder + (manifest is null ? "" : " · 🧾 " + ProjectManifestName) + " · " + DescribeCreatorBundle(bundle.HasSubtitlesSrt, bundle.HasSnsCopyText, bundle.HasThumbnailJpg);
        return new(true, dir, false, msg) { HasSubtitlesSrt = bundle.HasSubtitlesSrt, HasSnsCopyText = bundle.HasSnsCopyText, HasThumbnailJpg = bundle.HasThumbnailJpg };
    }

    private static async Task<string> CopyAtomicAsync(string src, string target, CancellationToken ct)
    {
        var tmp = target + TempExt;
        try
        {
            await using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            File.Move(tmp, target, overwrite: true);
            return target;
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>듀얼 폴더 <c>project_manifest.json</c> — <c>IsDualMaster</c> · 16:9/9:16 파일명 · 번들 플래그 · SNS 키트. 실패 격리(로그).</summary>
    internal static string? WriteDualMasterManifest(string dir, string projectName, string folder, string landTarget, string vertTarget, string landSource, string vertSource,
        PromptFlowStudio.Services.Pluto.PlutoSnsPublishingKit? kit, DateTimeOffset now, CreatorBundleResult bundle)
    {
        var path = Path.Combine(dir, ProjectManifestName);
        try
        {
            var buf = new System.Buffers.ArrayBufferWriter<byte>(2048);
            using (var w = new Utf8JsonWriter(buf, s_manifestJson))
            {
                var ts = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("projectName", projectName);
                w.WriteString("folder", folder);
                w.WriteBoolean("IsDualMaster", true);
                w.WriteString("landscape16x9", Path.GetFileName(landTarget));
                w.WriteString("vertical9x16", Path.GetFileName(vertTarget));
                w.WriteString("createdAt", ts);
                w.WriteString("updatedAt", ts);
                WriteBundleFlags(w, bundle);
                WriteSnsKit(w, kit);
                w.WriteStartArray("exports");
                WriteDualExportEntry(w, landTarget, landSource, "16:9", ts);
                WriteDualExportEntry(w, vertTarget, vertSource, "9:16", ts);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            WriteJsonAtomic(path, buf);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Program.Log("StockSuite.DualManifest", ex);
            return null;
        }
    }

    private static void WriteDualExportEntry(Utf8JsonWriter w, string target, string source, string aspect, string ts)
    {
        w.WriteStartObject();
        w.WriteString("file", Path.GetFileName(target));
        w.WriteString("aspect", aspect);
        w.WriteString("source", Path.GetFullPath(source));
        long bytes;
        try { bytes = new FileInfo(target).Length; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { bytes = 0; }
        w.WriteNumber("bytes", bytes);
        w.WriteString("exportedAt", ts);
        w.WriteEndObject();
    }

    private static void WriteVariantTraits(Utf8JsonWriter w, PromptFlowStudio.Services.Pluto.PlutoExperimentVariant v)
    {
        w.WriteString("variantLabel", v.VariantLabel);
        w.WriteString("hookStyle", v.HookStyle);
        w.WriteString("colorLookId", v.ColorLookId);
        w.WriteString("bgmMood", v.BgmMood);
        if (!string.IsNullOrWhiteSpace(v.PromptSuffix)) w.WriteString("promptSuffix", v.PromptSuffix);
    }

    /// <summary>변형 폴더 <c>project_manifest.json</c> — 실험 ID · 변형 특성 · 자막 · 번들 플래그 · 영상 파일. 실패 격리(로그).</summary>
    internal static string? WriteAbVariantManifest(string dir, PromptFlowStudio.Services.Pluto.PlutoVariantABPlan plan, PromptFlowStudio.Services.Pluto.PlutoExperimentVariant v,
        string slot, string targetPath, string sourcePath, DateTimeOffset now, CreatorBundleResult bundle = default)
    {
        var path = Path.Combine(dir, ProjectManifestName);
        try
        {
            var buf = new System.Buffers.ArrayBufferWriter<byte>(2048);
            using (var w = new Utf8JsonWriter(buf, s_manifestJson))
            {
                var ts = now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("projectName", (string.IsNullOrWhiteSpace(plan.ThemeHint) ? DefaultProjectSlug : plan.ThemeHint) + " · " + slot);
                w.WriteString("experimentId", plan.ExperimentId);
                w.WriteString("variant", slot);
                WriteVariantTraits(w, v);
                w.WriteString("folder", slot);
                w.WriteString("createdAt", ts);
                w.WriteString("updatedAt", ts);
                WriteBundleFlags(w, bundle);
                w.WriteStartArray("subtitles");
                foreach (var c in v.Subtitles)
                {
                    w.WriteStartObject();
                    w.WriteNumber("startSec", Math.Round(c.EffectiveStartSec, 3));
                    w.WriteNumber("endSec", Math.Round(c.EffectiveEndSec, 3));
                    w.WriteString("text", c.Text);
                    if (!string.IsNullOrWhiteSpace(c.SecondaryText)) w.WriteString("secondaryText", c.SecondaryText);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
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
            Program.Log("StockSuite.AbVariantManifest", ex);
            return null;
        }
    }

    /// <summary>실험 루트 <c>ab_experiment_manifest.json</c> — 실험 ID · 가설 · 테마 · 변형 특성(A/B) · 내보낸 변형 수.</summary>
    internal static string? WriteAbExperimentManifest(string abDir, PromptFlowStudio.Services.Pluto.PlutoVariantABPlan plan, string folder,
        IReadOnlyList<(string Slot, string Folder, string File, PromptFlowStudio.Services.Pluto.PlutoExperimentVariant Variant)> exported, DateTimeOffset now)
    {
        var path = Path.Combine(abDir, AbExperimentManifestName);
        try
        {
            var buf = new System.Buffers.ArrayBufferWriter<byte>(1024);
            using (var w = new Utf8JsonWriter(buf, s_manifestJson))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WriteString("ExperimentId", plan.ExperimentId);
                w.WriteString("HypothesisSummary", plan.HypothesisSummary);
                w.WriteString("themeHint", plan.ThemeHint);
                w.WriteString("folder", folder);
                w.WriteNumber("exportedVariants", exported.Count);
                w.WriteString("exportedAt", now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
                w.WriteStartArray("variants");
                for (var i = 0; i < 2; i++)
                {
                    var slot = i == 0 ? AbVariantAFolder : AbVariantBFolder;
                    var v = i == 0 ? plan.ChampionA : plan.ChallengerB;
                    string? file = null;
                    foreach (var e in exported) if (string.Equals(e.Slot, slot, StringComparison.Ordinal)) { file = e.File; break; }
                    w.WriteStartObject();
                    w.WriteString("slot", slot);
                    w.WriteString("role", i == 0 ? PromptFlowStudio.Services.Pluto.PlutoExperimentLedgerService.WinnerChampion : PromptFlowStudio.Services.Pluto.PlutoExperimentLedgerService.WinnerChallenger);
                    WriteVariantTraits(w, v);
                    w.WriteBoolean("exported", file is not null);
                    if (file is not null)
                    {
                        w.WriteString("file", file);
                        w.WriteString("manifest", slot + "/" + ProjectManifestName);
                    }
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
            Program.Log("StockSuite.AbManifest", ex);
            return null;
        }
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

    /// <summary>🎁 번들 사이드카 플래그(<c>HasSubtitlesSrt</c> · <c>HasSnsCopyText</c> · <c>HasThumbnailJpg</c>) + 존재하는 파일 목록.</summary>
    private static void WriteBundleFlags(Utf8JsonWriter w, CreatorBundleResult b)
    {
        w.WriteBoolean("HasSubtitlesSrt", b.HasSubtitlesSrt);
        w.WriteBoolean("HasSnsCopyText", b.HasSnsCopyText);
        w.WriteBoolean("HasThumbnailJpg", b.HasThumbnailJpg);
        w.WriteStartArray("sidecars");
        if (b.HasSubtitlesSrt) w.WriteStringValue(SubRipFileName);
        if (b.HasSnsCopyText) w.WriteStringValue(SnsCopyFileName);
        if (b.HasThumbnailJpg) w.WriteStringValue(ThumbnailFileName);
        w.WriteEndArray();
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
        string epFolder, string targetPath, string sourcePath, DateTimeOffset now, CreatorBundleResult bundle = default)
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
                WriteBundleFlags(w, bundle);
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
