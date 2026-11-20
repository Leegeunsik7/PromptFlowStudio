using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📊 YouTube Studio 표 데이터 1행을 정규화한 값. 모든 퍼센트는 [0, 100] 범위.</summary>
public sealed record YouTubeAnalyticsRow(string? YouTubeVideoId, string? Title, DateTimeOffset? PublishedAtUtc, long Views,
    double Hook3sRetention, bool HookEstimated, double CompletionRate, double RawAveragePercentViewed, double Ctr, bool CtrEstimated)
{
    /// <summary>노출수(열 없음이면 -1).</summary>
    public long Impressions { get; init; } = -1;
    /// <summary>좋아요(열 없음이면 -1).</summary>
    public long Likes { get; init; } = -1;
}

/// <summary>📊 정규화된 YouTube Studio 보고서.</summary>
public sealed record YouTubeAnalyticsReport(IReadOnlyList<YouTubeAnalyticsRow> Rows, int SkippedNoiseCount, IReadOnlyList<string> InvalidRows, string Language, string? SourceEntryName)
{
    /// <summary>인식된 YouTube Studio 표 헤더 없음.</summary>
    public bool HeaderRecognized { get; init; } = true;
}

/// <summary>
/// 📊 YouTube Studio 표 데이터 정규화기(순수 BCL). 입력은 <c>.csv</c>(<c>표 데이터.csv</c> / <c>Table data.csv</c>) 또는 <c>.zip</c> 내보내기.
///   ▸ 한/영 헤더 매핑, 천 단위 콤마·<c>%</c> 제거, 로캘별 날짜 파싱.
///   ▸ <c>스치듯 넘기지 않고 시청함(%)</c> / <c>Stayed to watch (%)</c> → <see cref="YouTubeAnalyticsRow.Hook3sRetention"/>(열 없으면 완주율로 보수 추정).
///   ▸ <c>평균 조회율(%)</c> 100% 초과(반복 재생 Shorts) → 무릎점 위 지수 포화. 단조 증가이고 100에 닿지 않는다.
///   ▸ 조회수 <see cref="MinViews"/> 미만 행은 노이즈로 건너뛰고 별도 집계.
///   ▸ ZIP: 메모리 안에서만 풀고 디스크에 쓰지 않음. 해제 크기 상한 적용. 차트(날짜별)·합계 CSV는 내용으로 걸러냄.
/// </summary>
public static class YouTubeStudioAnalyticsNormalizer
{
    public const long MinViews = 10;
    /// <summary>완주율 무릎점. 이 값 이하는 그대로 둔다(폐루프 판정 임계 30·55 보존).</summary>
    public const double CompletionKneePct = 70.0;
    /// <summary>무릎점 위 포화 척도(%p).</summary>
    public const double CompletionSaturationScale = 60.0;
    /// <summary>CTR 열이 없을 때 쓰는 중립값. 부진 임계(2.2 %)와 같아 벌점도 돌파도 일으키지 않는다.</summary>
    public const double NeutralCtrPct = PlutoClosedLoopReinforcementService.PoorCtrPct;
    /// <summary>평균 조회율 열이 없을 때 시청 유지(%)에서 완주율을 보수적으로 근사하는 계수.</summary>
    public const double StayedToCompletionProxy = 0.5;
    public const long MaxZipEntryBytes = 8L * 1024 * 1024;
    public const long MaxInputBytes = 32L * 1024 * 1024;
    public const int MaxZipEntries = 64;

    /// <summary>열 인덱스(-1 = 없음).</summary>
    public readonly record struct Columns(int Id, int Title, int Publish, int Views, int AvgPct, int Impressions, int Ctr, int Stayed, int Likes, int Date)
    {
        public bool IsTable => (Id >= 0 || Title >= 0) && Views >= 0 && (AvgPct >= 0 || Stayed >= 0 || Ctr >= 0);
    }

    /// <summary>헤더 정규화(소문자 · 문자/숫자만).</summary>
    public static string NormHeader(string h)
    {
        var sb = new StringBuilder(h.Length);
        foreach (var ch in h)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>한/영 헤더 매핑. 구체적인 열부터 판정한다(예: <c>노출 클릭률</c>을 <c>노출수</c>보다 먼저).</summary>
    public static Columns MapHeader(string[] header, out string language)
    {
        int id = -1, title = -1, publish = -1, views = -1, avg = -1, impr = -1, ctr = -1, stayed = -1, likes = -1, date = -1;
        var ko = 0;
        for (var i = 0; i < header.Length; i++)
        {
            var h = NormHeader(header[i]);
            if (h.Length == 0) continue;
            foreach (var ch in h) if (ch >= '가' && ch <= '힣') { ko++; break; }
            if (ctr < 0 && (h.Contains("클릭률", StringComparison.Ordinal) || h.Contains("clickthrough", StringComparison.Ordinal))) ctr = i;
            else if (stayed < 0 && (h.Contains("스치듯", StringComparison.Ordinal) || h.Contains("stayedtowatch", StringComparison.Ordinal))) stayed = i;
            else if (avg < 0 && (h.Contains("평균조회율", StringComparison.Ordinal) || h.Contains("averagepercentageviewed", StringComparison.Ordinal))) avg = i;
            else if (publish < 0 && (h.Contains("게시", StringComparison.Ordinal) || h.Contains("publish", StringComparison.Ordinal))) publish = i;
            else if (title < 0 && (h.Contains("제목", StringComparison.Ordinal) || h.Contains("title", StringComparison.Ordinal))) title = i;
            else if (impr < 0 && (h is "노출수" or "impressions")) impr = i;
            else if (views < 0 && (h is "조회수" or "views")) views = i;
            else if (likes < 0 && (h is "좋아요" or "likes" or "좋아요수")) likes = i;
            else if (date < 0 && (h is "날짜" or "date" or "일자")) date = i;
            else if (id < 0 && (h is "동영상" or "videoid" or "video" or "content" or "콘텐츠" or "동영상id")) id = i;
        }
        language = ko > 0 ? "ko" : "en";
        return new Columns(id, title, publish, views, avg, impr, ctr, stayed, likes, date);
    }

    /// <summary>정수 카운트 파싱(천 단위 콤마/공백/점 구분 허용 · 음수 거부).</summary>
    public static bool TryParseCount(string? raw, out long value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim())
        {
            if (ch is ',' or ' ' or ' ' or '\'' or '_') continue;
            sb.Append(ch);
        }
        var s = sb.ToString();
        // "1.234.567"(점 천 단위) — 점이 2개 이상이거나 점 뒤가 정확히 3자리 정수부면 구분자로 간주
        var dot = s.IndexOf('.');
        if (dot >= 0 && (s.IndexOf('.', dot + 1) >= 0 || (s.Length - dot - 1 == 3 && dot > 0))) s = s.Replace(".", "", StringComparison.Ordinal);
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d) || d < 0) return false;
        value = (long)Math.Round(d, MidpointRounding.AwayFromZero);
        return true;
    }

    /// <summary>퍼센트 원시값 파싱(<c>%</c>·공백 제거 · 단독 소수점 콤마 허용 · 상한 클램프 없음 · 음수/NaN 거부).</summary>
    public static bool TryParseRawPct(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().Replace("%", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (s.IndexOf(',') >= 0 && s.IndexOf('.') < 0) s = s.Replace(',', '.');
        else s = s.Replace(",", "", StringComparison.Ordinal);
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || !double.IsFinite(v) || v < 0) return false;
        value = v;
        return true;
    }

    /// <summary>
    /// 평균 조회율 → 완주율 [0, 100). 무릎점(70 %) 이하는 그대로 두고, 위쪽은 <c>K + (100−K)(1 − e^−(p−K)/S)</c>로 포화시킨다.
    /// 연속이고 단조 증가라서 반복 재생 Shorts(115 % vs 165 %)의 우열이 유지되며 100에 닿지 않는다.
    /// </summary>
    public static double SaturateCompletion(double averagePercentViewed)
    {
        if (!double.IsFinite(averagePercentViewed) || averagePercentViewed <= 0) return 0.0;
        if (averagePercentViewed <= CompletionKneePct) return averagePercentViewed;
        var over = averagePercentViewed - CompletionKneePct;
        return CompletionKneePct + (100.0 - CompletionKneePct) * (1.0 - Math.Exp(-over / CompletionSaturationScale));
    }

    private static readonly string[] EnDateFormats =
    {
        "MMM d, yyyy", "MMMM d, yyyy", "MMM dd, yyyy", "d MMM yyyy", "dd MMM yyyy", "MMM d yyyy",
    };

    /// <summary>게시일 파싱: ISO, <c>2026. 11. 3.</c>, <c>2026년 11월 3일</c>, <c>Nov 3, 2026</c>, <c>11/3/2026</c>. 날짜만 있으면 UTC 자정으로 둔다.</summary>
    public static bool TryParsePublishDate(string? raw, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim();
        if (DateTimeOffset.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value)) return true;
        var hasLetter = false;
        foreach (var ch in s) if (ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z') { hasLetter = true; break; }
        if (hasLetter)
        {
            var en = CultureInfo.GetCultureInfo("en-US");
            if (DateTimeOffset.TryParseExact(s, EnDateFormats, en, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out value)) return true;
            return DateTimeOffset.TryParse(s, en, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
        }
        // 숫자 3개 추출: 첫 수가 31 초과면 년-월-일, 아니면 월/일/년
        Span<int> nums = stackalloc int[3];
        var n = 0;
        var cur = -1;
        foreach (var ch in s)
        {
            if (ch is >= '0' and <= '9') { cur = (cur < 0 ? 0 : cur * 10) + (ch - '0'); if (cur > 99999) return false; }
            else if (cur >= 0) { if (n >= 3) return false; nums[n++] = cur; cur = -1; }
        }
        if (cur >= 0) { if (n >= 3) return false; nums[n++] = cur; }
        if (n != 3) return false;
        int y, m, d;
        if (nums[0] > 31) { y = nums[0]; m = nums[1]; d = nums[2]; }
        else { m = nums[0]; d = nums[1]; y = nums[2]; if (y < 100) y += 2000; }
        if (y < 2005 || y > 2100 || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(y, m)) return false;
        value = new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero);
        return true;
    }

    private static string? Cell(string[] row, int idx) => idx >= 0 && idx < row.Length && row[idx].Length > 0 ? row[idx] : null;

    private static bool IsTotalRow(string? key)
        => key is not null && (key.Equals("합계", StringComparison.Ordinal) || key.Equals("total", StringComparison.OrdinalIgnoreCase) || key.Equals("총계", StringComparison.Ordinal)
            || key.Equals("totals", StringComparison.OrdinalIgnoreCase));

    /// <summary>YouTube 동영상 ID 형식(11자 · 영문/숫자/<c>-</c>/<c>_</c>).</summary>
    public static bool IsYouTubeVideoId(string? s)
    {
        if (s is null || s.Length != 11) return false;
        foreach (var ch in s)
            if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        return true;
    }

    /// <summary>CSV 텍스트 → 정규화 보고서. 헤더 미인식이면 <see cref="YouTubeAnalyticsReport.HeaderRecognized"/> = false.</summary>
    public static YouTubeAnalyticsReport ParseCsv(string csvText, string? sourceName = null)
    {
        var rows = new List<YouTubeAnalyticsRow>();
        var invalid = new List<string>();
        if (string.IsNullOrWhiteSpace(csvText))
            return new YouTubeAnalyticsReport(rows, 0, invalid, "en", sourceName) { HeaderRecognized = false };
        var text = csvText.Length > PlutoTelemetryBatchImporter.MaxTextLength ? csvText[..PlutoTelemetryBatchImporter.MaxTextLength] : csvText;
        var table = PlutoTelemetryBatchImporter.ParseRows(text, PlutoTelemetryBatchImporter.DetectDelimiter(text), PlutoTelemetryBatchImporter.MaxRows + 2);
        if (table.Count == 0) return new YouTubeAnalyticsReport(rows, 0, invalid, "en", sourceName) { HeaderRecognized = false };
        var c = MapHeader(table[0], out var lang);
        if (!c.IsTable) return new YouTubeAnalyticsReport(rows, 0, invalid, lang, sourceName) { HeaderRecognized = false };

        var noise = 0;
        for (var r = 1; r < table.Count && rows.Count + invalid.Count + noise < PlutoTelemetryBatchImporter.MaxRows; r++)
        {
            var row = table[r];
            var id = Cell(row, c.Id);
            var title = Cell(row, c.Title);
            if (IsTotalRow(id) || (id is null && IsTotalRow(title))) continue;
            if (id is null && title is null) continue;
            var label = id ?? title!;
            if (!TryParseCount(Cell(row, c.Views), out var views)) { invalid.Add(label + " (조회수 오류)"); continue; }
            if (views < MinViews) { noise++; continue; }

            var hasAvg = TryParseRawPct(Cell(row, c.AvgPct), out var rawAvg);
            var hasStayed = TryParseRawPct(Cell(row, c.Stayed), out var stayed);
            var hasCtr = TryParseRawPct(Cell(row, c.Ctr), out var ctr);
            if (!hasAvg && !hasStayed) { invalid.Add(label + " (유지율 열 없음)"); continue; }
            var completion = hasAvg ? SaturateCompletion(rawAvg) : Math.Min(stayed, 100.0) * StayedToCompletionProxy;   // 평균 조회율 열 없음 → 보수 프록시
            var hook = hasStayed ? Math.Clamp(stayed, 0.0, 100.0) : PlutoTelemetryBatchImporter.EstimateHookFromCompletion(completion);
            var ctrPct = hasCtr ? Math.Clamp(ctr, 0.0, 100.0) : NeutralCtrPct;
            DateTimeOffset? published = TryParsePublishDate(Cell(row, c.Publish), out var p) ? p : null;
            var ytId = id is not null && IsYouTubeVideoId(id.Trim()) ? id.Trim() : null;
            var yr = new YouTubeAnalyticsRow(ytId, title, published, views, hook, !hasStayed, completion, hasAvg ? rawAvg : -1, ctrPct, !hasCtr);
            if (TryParseCount(Cell(row, c.Impressions), out var impr)) yr = yr with { Impressions = impr };
            if (TryParseCount(Cell(row, c.Likes), out var likes)) yr = yr with { Likes = likes };
            rows.Add(yr);
        }
        return new YouTubeAnalyticsReport(rows, noise, invalid, lang, sourceName);
    }

    /// <summary>바이트 → 텍스트(UTF-8/UTF-16 BOM 감지 · BOM 없으면 UTF-8).</summary>
    public static string DecodeText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var sr = new StreamReader(ms, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    /// <summary>ZIP 시그니처(<c>PK\x03\x04</c>/빈 아카이브 <c>PK\x05\x06</c>).</summary>
    public static bool LooksLikeZip(ReadOnlySpan<byte> bytes)
        => bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && (bytes[2] == 0x03 && bytes[3] == 0x04 || bytes[2] == 0x05 && bytes[3] == 0x06);

    /// <summary>
    /// 🗜️ ZIP 안의 표 데이터 CSV 선택. 이름이 아니라 헤더로 고른다: 표 헤더 인식 + 날짜 열 없음(차트 데이터 제외) + 행 단위 ID/제목(합계 제외).
    /// 같은 조건이면 이름에 <c>table</c>/<c>표</c>가 든 항목을 우선한다. 항목 수와 해제 크기에 상한을 둔다(ZIP 폭탄 차단).
    /// </summary>
    public static YouTubeAnalyticsReport ParseZip(byte[] zipBytes)
    {
        using var ms = new MemoryStream(zipBytes, writable: false);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);
        YouTubeAnalyticsReport? best = null;
        var bestScore = int.MinValue;
        var seen = 0;
        foreach (var entry in zip.Entries)
        {
            if (++seen > MaxZipEntries) break;
            if (!entry.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Length <= 0 || entry.Length > MaxZipEntryBytes) continue;
            string text;
            try { text = DecodeText(ReadBounded(entry)); }
            catch (InvalidDataException) { continue; }
            var head = PlutoTelemetryBatchImporter.ParseRows(text, PlutoTelemetryBatchImporter.DetectDelimiter(text), 1);
            if (head.Count == 0) continue;
            var cols = MapHeader(head[0], out _);
            if (!cols.IsTable || cols.Date >= 0) continue;   // 차트 데이터(날짜별 행)는 동영상 행이 중복 집계됨
            var name = entry.Name;
            var score = name.Contains("table", StringComparison.OrdinalIgnoreCase) || name.Contains("표", StringComparison.Ordinal) ? 2 : 0;
            if (name.Contains("chart", StringComparison.OrdinalIgnoreCase) || name.Contains("차트", StringComparison.Ordinal) || name.Contains("total", StringComparison.OrdinalIgnoreCase)
                || name.Contains("합계", StringComparison.Ordinal)) score -= 3;
            var report = ParseCsv(text, entry.FullName);
            if (report.Rows.Count + report.SkippedNoiseCount == 0) score -= 5;   // 합계 전용 CSV
            if (score > bestScore) { bestScore = score; best = report; }
        }
        return best ?? new YouTubeAnalyticsReport(Array.Empty<YouTubeAnalyticsRow>(), 0, Array.Empty<string>(), "en", null) { HeaderRecognized = false };
    }

    private static byte[] ReadBounded(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var buf = new MemoryStream((int)Math.Min(entry.Length, MaxZipEntryBytes));
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = s.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buf.Length + read > MaxZipEntryBytes) throw new InvalidDataException("zip entry exceeds limit");   // 헤더 길이 위조 대비
            buf.Write(chunk, 0, read);
        }
        return buf.ToArray();
    }

    /// <summary>파일 바이트(CSV 또는 ZIP) → 정규화 보고서. 형식 판별은 시그니처 우선, 다음 확장자.</summary>
    public static YouTubeAnalyticsReport Parse(byte[] bytes, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.LongLength > MaxInputBytes) throw new InvalidDataException("input exceeds " + MaxInputBytes.ToString(CultureInfo.InvariantCulture) + " bytes");
        if (LooksLikeZip(bytes)) return ParseZip(bytes);
        return ParseCsv(DecodeText(bytes), fileName);
    }

    /// <summary>
    /// 📂 다운로드 폴더에서 가장 최근 YouTube Studio 내보내기 파일(<c>.zip</c>/<c>.csv</c>) 찾기. 받는 중인 임시 파일(<c>.crdownload</c>/<c>.part</c>)은
    /// 확장자로 제외된다. 이름에 YouTube/Studio/콘텐츠/Content/표 데이터/Table data가 있어야 한다. 없으면 null.
    /// </summary>
    public static string? FindNewestExport(string? downloadsDir = null)
    {
        try
        {
            var dir = downloadsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;
            string? best = null;
            var bestTime = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(f);
                if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)) continue;
                if (!LooksLikeStudioExportName(Path.GetFileName(f))) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > bestTime) { bestTime = t; best = f; }
            }
            return best;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or PlatformNotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.YouTubeStudio] downloads scan: " + ex.Message);
            return null;
        }
    }

    /// <summary>YouTube Studio 내보내기 파일명 휴리스틱.</summary>
    public static bool LooksLikeStudioExportName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        return name.Contains("youtube", StringComparison.OrdinalIgnoreCase) || name.Contains("studio", StringComparison.OrdinalIgnoreCase)
            || name.Contains("콘텐츠", StringComparison.Ordinal) || name.Contains("content", StringComparison.OrdinalIgnoreCase)
            || name.Contains("표 데이터", StringComparison.Ordinal) || name.Contains("table data", StringComparison.OrdinalIgnoreCase)
            || name.Contains("동영상", StringComparison.Ordinal);
    }
}
