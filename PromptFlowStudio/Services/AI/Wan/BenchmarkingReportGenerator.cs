using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>저장 결과 — 성공 여부, 저장 경로 2종(JSON/Markdown), 사유.</summary>
public sealed record ReportSaveResult(bool Ok, string? JsonPath, string? MarkdownPath, string Message);

/// <summary>JSON 리포트 봉투(원본 익명 타입 대체 · 소스 생성 직렬화 호환 명명 타입 · 키 이름은 기존과 동일 유지).</summary>
internal sealed record BenchmarkReportEnvelope(
    [property: JsonPropertyName("report")] BenchmarkSweepReport Report,
    [property: JsonPropertyName("tuner")] TunerSnapshot? Tuner);

/// <summary>
/// v10.2.3 벤치마킹 리포트 생성기 — <see cref="BenchmarkSweepReport"/>(+선택적 <see cref="TunerSnapshot"/>)를
/// JSON(기계 판독용 · <see cref="System.Text.Json"/> 순수 BCL)과 Markdown(사람 판독용 표) 양쪽으로 직렬화하고
/// <c>%LocalAppData%\PromptFlowStudio\Reports</c>(<see cref="Media.LocalMediaService.MediaRoot"/> 와 동일 베이스
/// 규약의 형제 폴더)에 타임스탬프 파일명으로 저장한다. 파일 I/O 만 사용해 하드웨어 비의존 · 모바일 등록 가능.
/// </summary>
public sealed class BenchmarkingReportGenerator
{
    private long _generated, _saveFailures;

    public event Action? OnChange;

    public string? LastAction { get; private set; }
    public string? LastJsonPath { get; private set; }
    public string? LastMarkdownPath { get; private set; }
    public long Generated => Interlocked.Read(ref _generated);
    public long SaveFailures => Interlocked.Read(ref _saveFailures);

    /// <summary>리포트 저장 루트(형제 폴더 규약 — <c>LocalMediaService.MediaRoot</c> 와 동일 베이스 · 컨피그 오버라이드 없음).</summary>
    public static string ReportsRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Reports");

    /// <summary>JSON 직렬화(들여쓰기 · 순수 데이터).</summary>
    public static string ToJson(BenchmarkSweepReport report, TunerSnapshot? tuner = null)
        => JsonSerializer.Serialize(new BenchmarkReportEnvelope(report, tuner), PromptFlowJsonContext.Default.BenchmarkReportEnvelope);

    /// <summary>Markdown 표 리포트 — 사람이 바로 읽을 수 있는 요약 + 티어별 표.</summary>
    public static string ToMarkdown(BenchmarkSweepReport report, TunerSnapshot? tuner = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Wan 2.1 저사양 가속 벤치마크 리포트");
        sb.AppendLine();
        sb.AppendLine($"- 생성 시각(UTC): {report.At:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- 모델 티어: `{report.ModelTierId}` · 스펙: {report.Width}x{report.Height}@{report.Frames}f");
        sb.AppendLine($"- 스윕 소요: {report.SweepElapsedMs.ToString("0.##", CultureInfo.InvariantCulture)}ms");
        sb.AppendLine($"- 최고 처리량 예산: {report.BestThroughputBudgetMb}MB · 최저 처리량 예산: {report.WorstThroughputBudgetMb}MB");
        sb.AppendLine();
        sb.AppendLine("| 가상 VRAM(MB) | 결과 | 양자화 | 레지던트 블록 | 타일 수 | 타일당 ms | 총 디코드ms | 피크VRAM(MB) | 처리량(fps) |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var t in report.Tiers)
        {
            sb.AppendLine($"| {t.BudgetMb} | {(t.Ok ? "✅" : "❌")} | {t.QuantUsed} | {t.ResidentBlocks}/{t.TotalBlocks} | {t.TileCount} | " +
                $"{t.DecodeMsPerTile.ToString("0.###", CultureInfo.InvariantCulture)} | {t.TotalDecodeMs.ToString("0.##", CultureInfo.InvariantCulture)} | " +
                $"{t.PeakVramMb.ToString("0.#", CultureInfo.InvariantCulture)} | {t.ThroughputFramesPerSec.ToString("0.##", CultureInfo.InvariantCulture)} |");
        }
        if (tuner is { } tu)
        {
            sb.AppendLine();
            sb.AppendLine("## 라이브 튜너 스냅샷");
            sb.AppendLine($"- EWMA 지연: {tu.EwmaMs.ToString("0.##", CultureInfo.InvariantCulture)}ms(목표 {tu.TargetMs.ToString("0.#", CultureInfo.InvariantCulture)}ms) · 현재 양자화: {tu.CurrentQuant}");
            sb.AppendLine($"- 관측 프레임: {tu.FramesObserved} · 다운그레이드: {tu.Downgrades} · 업그레이드: {tu.Upgrades}");
        }
        return sb.ToString();
    }

    /// <summary>JSON+Markdown 동시 저장 — 폴더 생성/쓰기 실패는 Graceful(예외 삼키고 실패 사유 반환).</summary>
    public ReportSaveResult Save(BenchmarkSweepReport report, TunerSnapshot? tuner = null)
    {
        try
        {
            Directory.CreateDirectory(ReportsRoot);
            var stamp = report.At.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var jsonPath = Path.Combine(ReportsRoot, $"wan-benchmark-{stamp}.json");
            var mdPath = Path.Combine(ReportsRoot, $"wan-benchmark-{stamp}.md");
            File.WriteAllText(jsonPath, ToJson(report, tuner), Encoding.UTF8);
            File.WriteAllText(mdPath, ToMarkdown(report, tuner), Encoding.UTF8);

            Interlocked.Increment(ref _generated);
            LastJsonPath = jsonPath; LastMarkdownPath = mdPath;
            LastAction = $"리포트 저장 완료 · {Path.GetFileName(jsonPath)}";
            var result = new ReportSaveResult(true, jsonPath, mdPath, "저장 완료");
            OnChange?.Invoke();
            return result;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _saveFailures);
            LastAction = "리포트 저장 실패: " + ex.Message;
            var result = new ReportSaveResult(false, null, null, ex.Message);
            OnChange?.Invoke();
            return result;
        }
    }

    public void ResetCounters()
    {
        _generated = 0; _saveFailures = 0; LastJsonPath = null; LastMarkdownPath = null; LastAction = "리포트 생성기 초기화";
        OnChange?.Invoke();
    }
}
