using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>📥 다건 성과 일괄 반영 결과.</summary>
public sealed record PlutoTelemetryBatchImportResult(int TotalRows, int MatchedAndLearnedCount, int UnmatchedCount, int MistakeGuardsTriggered, IReadOnlyList<string> UnmatchedIdentifiers)
{
    /// <summary>카탈로그 매칭됐으나 이미 반영된 영상(alreadyRegistered · 재학습 안 함).</summary>
    public int AlreadyRegisteredCount { get; init; }

    /// <summary>📈 Step 22 — 성숙 스냅샷 증분 반영 건수.</summary>
    public int MaturityDeltaCount { get; init; }

    /// <summary>🔇 Step 22 — 조회수 하한 미만으로 건너뛴 노이즈 행(<see cref="TotalRows"/>에 포함되지 않음).</summary>
    public int SkippedNoiseCount { get; init; }

    /// <summary>📺 Step 22 — 이번 반영에서 카탈로그에 새로 영속한 YouTube ID 연결 수.</summary>
    public int NewlyLinkedCount { get; init; }

    /// <summary>📊 YouTube Studio 보고서 경로로 처리됨.</summary>
    public bool IsYouTubeStudioReport { get; init; }

    /// <summary>원본 이름(ZIP 내부 항목 · 파일명).</summary>
    public string? SourceName { get; init; }

    /// <summary>한국어 요약 1줄.</summary>
    public string SummaryKorean
    {
        get
        {
            var s = "📥 " + TotalRows.ToString(CultureInfo.InvariantCulture) + "행 중 ✅ " + MatchedAndLearnedCount.ToString(CultureInfo.InvariantCulture)
                + "건 학습 · ❓ 미매칭 " + UnmatchedCount.ToString(CultureInfo.InvariantCulture) + "건";
            if (AlreadyRegisteredCount > 0) s += " · ⏭ 기반영 " + AlreadyRegisteredCount.ToString(CultureInfo.InvariantCulture) + "건";
            if (MaturityDeltaCount > 0) s += " · 📈 성숙 증분 " + MaturityDeltaCount.ToString(CultureInfo.InvariantCulture) + "건";
            if (NewlyLinkedCount > 0) s += " · 📺 새 연결 " + NewlyLinkedCount.ToString(CultureInfo.InvariantCulture) + "건";
            if (SkippedNoiseCount > 0) s += " · 🔇 저조회 노이즈 " + SkippedNoiseCount.ToString(CultureInfo.InvariantCulture) + "건 제외";
            if (MistakeGuardsTriggered > 0) s += " · 🛡️ 오답 가드 " + MistakeGuardsTriggered.ToString(CultureInfo.InvariantCulture) + "건 발동";
            return s;
        }
    }
}

/// <summary>📥 CSV/TSV 다건 성과 → 렌더 카탈로그 매칭 → 폐루프 강화.</summary>
public interface IPlutoTelemetryBatchImporter
{
    /// <summary>
    /// 압축 4열(<c>VideoId,HookRetentionPct,CompletionPct,CtrPct</c> · 헤더 선택) 또는 YouTube Studio 분석 CSV(한/영 헤더)를 파싱 →
    /// 카탈로그 VideoId/정규화 제목 매칭 → 강화 반영 · 학습 완료 표시. 행 단위 실패는 미매칭 처리(배치 중단 없음) · 취소만 전파.
    /// </summary>
    Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default);

    /// <summary>
    /// 📋 미학습 영상 CSV 템플릿 — 최근 카탈로그 100건 중 <c>TelemetryLogged == false</c> 클립(최대 <paramref name="maxRows"/>건)을
    /// 압축 4열 헤더 + 기본값 행(<c>{VideoId},42.0,30.0,1.5</c>)으로 생성. 미학습 없음 → 헤더만.
    /// </summary>
    Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default);

    /// <summary>
    /// 📊 Step 22 — YouTube Studio 내보내기(<c>.csv</c> 또는 <c>.zip</c> 바이트) → 정규화 → 카탈로그 연결(Tier 1/2/3) → 최초 반영 또는 성숙도 증분 반영.
    /// 같은 스냅샷 재반영은 0건 강화(멱등). 형식 오류 → <see cref="System.IO.InvalidDataException"/>.
    /// </summary>
    Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default);
}

/// <summary>📥 <see cref="IPlutoTelemetryBatchImporter"/> 순수 BCL 구현(따옴표 필드 · 콤마/탭/세미콜론 구분자 · BOM 허용).</summary>
public sealed class PlutoTelemetryBatchImporter : IPlutoTelemetryBatchImporter
{
    public const int MaxRows = 500;
    public const int MaxTextLength = 1_000_000;
    public const double EstimatedHookMinPct = 15.0, EstimatedHookMaxPct = 85.0;

    private readonly IPlutoRenderCatalogService _catalog;
    private readonly IPlutoClosedLoopReinforcementService _loop;

    public PlutoTelemetryBatchImporter(IPlutoRenderCatalogService catalog, IPlutoClosedLoopReinforcementService loop)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
    }

    /// <summary>파싱된 성과 행(Hook 미제공 → 완주율 기반 보수 추정 · <see cref="HookEstimated"/>).</summary>
    public sealed record ParsedRow(string? VideoId, string? Title, double HookPct, double CompletionPct, double CtrPct, bool HookEstimated);

    /// <summary>3초 훅 열 부재 → 완주율을 [15, 85]로 클램프한 보수 추정치.</summary>
    public static double EstimateHookFromCompletion(double completionPct) => Math.Clamp(completionPct, EstimatedHookMinPct, EstimatedHookMaxPct);

    /// <summary>구분자 추정 — 첫 비어있지 않은 줄의 탭 &gt; 세미콜론(콤마 없음) &gt; 콤마.</summary>
    public static char DetectDelimiter(string text)
    {
        var end = text.IndexOf('\n');
        var line = end < 0 ? text : text[..end];
        if (line.IndexOf('\t') >= 0) return '\t';
        if (line.IndexOf(';') >= 0 && line.IndexOf(',') < 0) return ';';
        return ',';
    }

    /// <summary>RFC 4180 상태 기계 — 따옴표 필드(구분자·개행·<c>""</c> 이스케이프 포함) · CRLF/LF · 빈 줄 생략.</summary>
    public static List<string[]> ParseRows(string text, char delimiter, int maxRows = MaxRows + 1)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        if (text.Length > 0 && text[0] == '﻿') i = 1;
        for (; i < text.Length; i++)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(ch);
                continue;
            }
            if (ch == '"' && sb.Length == 0) { inQuotes = true; continue; }
            if (ch == delimiter) { fields.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                fields.Add(sb.ToString().Trim());
                sb.Clear();
                if (!IsBlank(fields)) rows.Add(fields.ToArray());
                fields.Clear();
                if (rows.Count >= maxRows) return rows;
                continue;
            }
            sb.Append(ch);
        }
        fields.Add(sb.ToString().Trim());
        if (!IsBlank(fields) && rows.Count < maxRows) rows.Add(fields.ToArray());
        return rows;
    }

    private static bool IsBlank(List<string> fields)
    {
        foreach (var f in fields) if (f.Length > 0) return false;
        return true;
    }

    /// <summary>퍼센트 수치 파싱(<c>%</c>·공백 허용 · 소수점 콤마 단독 허용 · 범위 0~100 클램프).</summary>
    public static bool TryParsePct(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().Replace("%", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (s.IndexOf(',') >= 0 && s.IndexOf('.') < 0) s = s.Replace(',', '.');
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || double.IsNaN(v) || double.IsInfinity(v)) return false;
        value = Math.Clamp(v, 0.0, 100.0);
        return true;
    }

    private static string NormHeader(string h)
    {
        var sb = new StringBuilder(h.Length);
        foreach (var ch in h)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>열 인덱스 매핑(-1 = 없음).</summary>
    public readonly record struct ColumnMap(int Id, int Title, int Hook, int Completion, int Ctr)
    {
        public bool HasMetrics => Completion >= 0 && Ctr >= 0 && (Id >= 0 || Title >= 0);
    }

    /// <summary>한/영 헤더 인식 — 인식 지표 열이 2개 미만이면 헤더 아님(null).</summary>
    public static ColumnMap? TryMapHeader(string[] header)
    {
        int id = -1, title = -1, hook = -1, completion = -1, ctr = -1;
        for (var i = 0; i < header.Length; i++)
        {
            var h = NormHeader(header[i]);
            if (h.Length == 0) continue;
            if (ctr < 0 && (h.Contains("클릭률", StringComparison.Ordinal) || h.Contains("clickthrough", StringComparison.Ordinal) || h.StartsWith("ctr", StringComparison.Ordinal))) ctr = i;
            else if (hook < 0 && (h.Contains("hook", StringComparison.Ordinal) || h.Contains("3초", StringComparison.Ordinal) || h.Contains("3s", StringComparison.Ordinal)
                || h.Contains("stayedtowatch", StringComparison.Ordinal) || h.Contains("intro", StringComparison.Ordinal))) hook = i;
            else if (completion < 0 && (h.Contains("평균조회율", StringComparison.Ordinal) || h.Contains("averagepercentageviewed", StringComparison.Ordinal)
                || h.Contains("completion", StringComparison.Ordinal) || h.Contains("완주", StringComparison.Ordinal))) completion = i;
            else if (title < 0 && (h.Contains("title", StringComparison.Ordinal) || h.Contains("제목", StringComparison.Ordinal))) title = i;
            else if (id < 0 && (h is "videoid" or "video" or "content" or "콘텐츠" or "동영상" or "영상id" or "id" or "동영상id")) id = i;
        }
        var metrics = (hook >= 0 ? 1 : 0) + (completion >= 0 ? 1 : 0) + (ctr >= 0 ? 1 : 0);
        return metrics >= 2 ? new ColumnMap(id, title, hook, completion, ctr) : null;
    }

    private static bool IsTotalRow(string? key)
        => key is not null && (key.Equals("합계", StringComparison.Ordinal) || key.Equals("total", StringComparison.OrdinalIgnoreCase) || key.Equals("총계", StringComparison.Ordinal));

    private static string? Cell(string[] row, int idx) => idx >= 0 && idx < row.Length && row[idx].Length > 0 ? row[idx] : null;

    /// <summary>전체 텍스트 → 성과 행(헤더/합계 행 제외 · 수치 파싱 실패 행 → <paramref name="invalid"/>).</summary>
    public static List<ParsedRow> Parse(string csvText, List<string> invalid)
    {
        var result = new List<ParsedRow>();
        if (string.IsNullOrWhiteSpace(csvText)) return result;
        var text = csvText.Length > MaxTextLength ? csvText[..MaxTextLength] : csvText;
        var rows = ParseRows(text, DetectDelimiter(text));
        if (rows.Count == 0) return result;

        var map = TryMapHeader(rows[0]);
        var start = map is null ? 0 : 1;
        // 헤더 미인식 + 첫 행 지표 비수치 → 알 수 없는 헤더 행 생략
        if (map is null && rows[0].Length >= 4 && !TryParsePct(rows[0][1], out _) && !TryParsePct(rows[0][2], out _)) start = 1;
        var m = map is { HasMetrics: true } mm ? mm : new ColumnMap(0, -1, 1, 2, 3);   // 압축 4열: VideoId,Hook,Completion,Ctr

        for (var r = start; r < rows.Count && result.Count + invalid.Count < MaxRows; r++)
        {
            var row = rows[r];
            var id = Cell(row, m.Id);
            var title = Cell(row, m.Title);
            if (IsTotalRow(id) || (id is null && IsTotalRow(title))) continue;
            if (id is null && title is null) continue;
            if (!TryParsePct(Cell(row, m.Completion), out var completion) || !TryParsePct(Cell(row, m.Ctr), out var ctr))
            {
                invalid.Add((id ?? title)! + " (수치 오류)");
                continue;
            }
            double hook;
            var estimated = false;
            if (m.Hook < 0 || !TryParsePct(Cell(row, m.Hook), out hook))
            {
                hook = EstimateHookFromCompletion(completion);
                estimated = true;
            }
            result.Add(new ParsedRow(id, title, hook, completion, ctr, estimated));
        }
        return result;
    }

    public const string CompactHeader = "VideoId,HookRetentionPct,CompletionPct,CtrPct";
    public const string TemplateDefaultMetrics = ",42.0,30.0,1.5";
    public const int TemplateScanCount = 100;

    public async Task<string> BuildUnlearnedCsvTemplateAsync(int maxRows = 15, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var sb = new StringBuilder(CompactHeader).Append('\n');
        if (maxRows <= 0) return sb.ToString();
        var clips = await _catalog.GetRecentClipsAsync(TemplateScanCount, ct).ConfigureAwait(false);
        var added = 0;
        for (var i = 0; i < clips.Count && added < maxRows; i++)
        {
            var c = clips[i];
            if (c.TelemetryLogged || string.IsNullOrWhiteSpace(c.VideoId)) continue;
            sb.Append(QuoteCsv(c.VideoId)).Append(TemplateDefaultMetrics).Append('\n');
            added++;
        }
        return sb.ToString();
    }

    private static readonly char[] QuoteTriggers = { ',', '"', '\n', '\r', '\t', ';' };

    private static string QuoteCsv(string v)
        => v.IndexOfAny(QuoteTriggers) < 0 ? v : "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public async Task<PlutoTelemetryBatchImportResult> ImportCsvAsync(string csvText, string platform = "YouTube Shorts", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var plat = string.IsNullOrWhiteSpace(platform) ? "YouTube Shorts" : platform.Trim();
        // 📊 Step 22 — 조회수 열이 있는 YouTube Studio 표를 붙여넣으면 정규화 · 연결 · 성숙도 경로로 보냄(100 % 초과 클램프 · 노이즈 유입 방지)
        var studio = YouTubeStudioAnalyticsNormalizer.ParseCsv(csvText ?? string.Empty, "paste");
        if (studio.HeaderRecognized) return await ImportYouTubeReportAsync(studio, plat, ct).ConfigureAwait(false);
        var unmatched = new List<string>();
        var rows = Parse(csvText ?? string.Empty, unmatched);
        int learned = 0, guards = 0, dup = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var label = row.VideoId ?? row.Title ?? "?";
            try
            {
                PlutoRenderedClipRecord? clip = null;
                if (row.VideoId is { } vid) clip = await _catalog.FindByVideoIdOrTitleAsync(vid, ct).ConfigureAwait(false);
                if (clip is null && row.Title is { } t) clip = await _catalog.FindByVideoIdOrTitleAsync(t, ct).ConfigureAwait(false);
                if (clip is null) { unmatched.Add(label); continue; }

                var telemetry = BuildTelemetry(clip, plat, row.HookPct, row.CompletionPct, row.CtrPct);
                var outcome = await _loop.IngestPerformanceAsync(telemetry, ct).ConfigureAwait(false);
                if (outcome.AlreadyRegistered) dup++;
                else
                {
                    learned++;
                    if (outcome.GuardActivated) guards++;
                }
                await _catalog.MarkTelemetryLoggedAsync(clip.VideoId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Pluto.BatchImport] row " + label + ": " + ex.Message);
                unmatched.Add(label + " (오류)");   // 행 격리 — 배치 계속
            }
        }

        // 모든 데이터 행은 학습 · 기반영 · 미매칭(수치 오류/행 오류 포함) 중 정확히 하나
        return new PlutoTelemetryBatchImportResult(learned + dup + unmatched.Count, learned, unmatched.Count, guards, unmatched.ToArray()) { AlreadyRegisteredCount = dup };
    }

    private static PlutoVideoPerformanceTelemetry BuildTelemetry(PlutoRenderedClipRecord clip, string plat, double hook, double completion, double ctr)
        => new(clip.VideoId, plat, clip.HookStyle, clip.ColorLookId, clip.BgmMood, clip.Tags, hook, completion, ctr, DateTimeOffset.UtcNow)
        {
            Domain = PromptDomainClassifier.Classify(clip.Title + " " + clip.HookStyle).ToString(),   // 🧭 domain_hook 상호작용 키
            RenderMode = string.IsNullOrWhiteSpace(clip.RenderMode) ? null : clip.RenderMode,   // 📐 컨텍스트 기준선(ctx:)
        };

    // ── 📊 Step 22 — YouTube Studio 내보내기 ──

    /// <summary>성숙 판정 — 조회수가 <see cref="MaturityViewsMultiplier"/>배 이상 · <see cref="MaturityMinViewGain"/>회 이상 증가.</summary>
    public const double MaturityViewsMultiplier = 2.0;
    public const long MaturityMinViewGain = 200;

    /// <summary>스냅샷이 이전 반영 대비 '실질적으로 성숙'했는지(둘 다 충족).</summary>
    public static bool IsMaterialMaturity(long previousViews, long currentViews)
        => currentViews - previousViews >= MaturityMinViewGain && currentViews >= previousViews * MaturityViewsMultiplier;

    public async Task<PlutoTelemetryBatchImportResult> ImportYouTubeStudioFileAsync(byte[] fileBytes, string? fileName = null, string platform = "YouTube Shorts", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fileBytes);
        ct.ThrowIfCancellationRequested();
        var report = YouTubeStudioAnalyticsNormalizer.Parse(fileBytes, fileName);
        if (!report.HeaderRecognized)
            throw new System.IO.InvalidDataException("YouTube Studio 표 데이터(조회수 · 평균 조회율/시청 유지 열)를 찾지 못했습니다.");
        var plat = string.IsNullOrWhiteSpace(platform) ? "YouTube Shorts" : platform.Trim();
        return await ImportYouTubeReportAsync(report, plat, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 정규화 보고서 반영. 행마다 정확히 한 갈래: 최초 학습 · 성숙 증분 · 기반영(멱등 생략) · 미매칭.
    /// 워터마크(조회수 · 단계)는 카탈로그에 저장되어 멀티 PC 병합 시 앞선 쪽이 유지된다.
    /// </summary>
    private async Task<PlutoTelemetryBatchImportResult> ImportYouTubeReportAsync(YouTubeAnalyticsReport report, string plat, CancellationToken ct)
    {
        var unmatched = new List<string>(report.InvalidRows);
        int learned = 0, guards = 0, dup = 0, matured = 0, linked = 0;
        var links = await new PlutoYouTubeCatalogLinker(_catalog).LinkAsync(report.Rows, ct).ConfigureAwait(false);

        for (var i = 0; i < report.Rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = report.Rows[i];
            var link = links[i];
            var label = row.YouTubeVideoId ?? row.Title ?? "?";
            if (link.Clip is not { } clip) { unmatched.Add(label + (link.Reason is { } why ? " (" + why + ")" : "")); continue; }
            if (link.NewlyBound) linked++;
            try
            {
                var telemetry = BuildTelemetry(clip, plat, row.Hook3sRetention, row.CompletionRate, row.Ctr);
                var now = DateTimeOffset.UtcNow;
                if (clip.YouTubeWatermark is not { } wm)
                {
                    var outcome = await _loop.IngestPerformanceAsync(telemetry, ct).ConfigureAwait(false);
                    if (outcome.AlreadyRegistered) dup++;   // 수동/압축 CSV로 이미 반영 → 이 스냅샷을 성숙도 기준점(0단계)으로 채택
                    else { learned++; if (outcome.GuardActivated) guards++; }
                    await _catalog.MarkTelemetryLoggedAsync(clip.VideoId, ct).ConfigureAwait(false);
                    await _catalog.UpdateYouTubeWatermarkAsync(clip.VideoId, new PlutoYouTubeIngestWatermark(row.Views, 0, row.Hook3sRetention, row.CompletionRate, row.Ctr, now), ct).ConfigureAwait(false);
                    continue;
                }
                var nextStage = wm.Stage + 1;
                if (!IsMaterialMaturity(wm.Views, row.Views) || nextStage > PlutoClosedLoopReinforcementService.MaxMaturityStages) { dup++; continue; }   // ⏭ 같은/덜 성숙한 스냅샷 → 0건 강화
                var previous = BuildTelemetry(clip, plat, wm.HookPct, wm.CompletionPct, wm.CtrPct);
                var delta = await _loop.IngestMaturityDeltaAsync(telemetry, previous, nextStage, PlutoClosedLoopReinforcementService.MaturityWeightForStage(nextStage), ct).ConfigureAwait(false);
                if (delta.AlreadyRegistered) dup++;
                else { matured++; if (delta.GuardActivated) guards++; }
                await _catalog.UpdateYouTubeWatermarkAsync(clip.VideoId, new PlutoYouTubeIngestWatermark(row.Views, nextStage, row.Hook3sRetention, row.CompletionRate, row.Ctr, now), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Pluto.BatchImport] youtube row " + label + ": " + ex.Message);
                unmatched.Add(label + " (오류)");   // 행 격리 — 배치 계속
            }
        }

        return new PlutoTelemetryBatchImportResult(learned + matured + dup + unmatched.Count, learned, unmatched.Count, guards, unmatched.ToArray())
        {
            AlreadyRegisteredCount = dup,
            MaturityDeltaCount = matured,
            SkippedNoiseCount = report.SkippedNoiseCount,
            NewlyLinkedCount = linked,
            IsYouTubeStudioReport = true,
            SourceName = report.SourceEntryName,
        };
    }
}
