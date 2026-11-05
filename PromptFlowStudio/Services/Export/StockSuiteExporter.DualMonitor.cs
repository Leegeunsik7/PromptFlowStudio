using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Export;

/// <summary>🖥️ 듀얼 모니터 분할 결과 — 좌/우 산출물(실패 슬롯 null · [부분 구제] 성공분 보존).</summary>
public sealed record DualMonitorSplitResult(string? LeftPath, string? RightPath, string? DisplayConfigPath, bool NeedsPathSetup, string Message)
{
    public bool Ok => LeftPath is not null && RightPath is not null;
}

public sealed partial class StockSuiteExporter
{
    public const int MonitorWidth = 1920;
    public const int MonitorHeight = 1080;
    public const int DefaultBezelGapPixels = 40;
    public const int MaxBezelGapPixels = 400;
    public const string DualScreenPrefix = "bg_screen_";
    public const string DisplayConfigName = "stocksuite_displays.json";

    /// <summary>⏩ 정방향 크로스페이드 루프 인자 — <see cref="AmbientLoopService.BuildAmbientArgs(string, string, LoopMode, bool, double, double)"/> 위임(역재생 없음).</summary>
    public static string[] BuildForwardCrossfadeLoopArgs(string inputPath, string outputPath, double sourceSeconds = AmbientLoopService.DefaultSourceSeconds, double crossfadeSeconds = AmbientLoopService.DefaultCrossfadeSeconds)
        => AmbientLoopService.BuildAmbientArgs(inputPath, outputPath, LoopMode.ForwardCrossfade, false, sourceSeconds, crossfadeSeconds);

    /// <summary>베젤 간격 클램프(0 ~ <see cref="MaxBezelGapPixels"/>).</summary>
    public static int ClampBezel(int bezelGapPixels) => bezelGapPixels < 0 ? 0 : bezelGapPixels > MaxBezelGapPixels ? MaxBezelGapPixels : bezelGapPixels;

    /// <summary>
    /// 좌/우 화면 크롭 필터 — 원본을 (2×1920 + 베젤) × 1080 캔버스로 채움 스케일(비율 유지·중앙 크롭) 후 화면별 1920×1080 크롭.
    /// 좌: <c>crop=1920:1080:0:0</c> · 우: <c>crop=1920:1080:{1920+베젤}:0</c> — 베젤 뒤로 숨는 픽셀을 건너뛰어 화면 간 선형 연속성 유지.
    /// </summary>
    public static string BuildDualMonitorCropFilter(int screenIndex, int bezelGapPixels = DefaultBezelGapPixels)
    {
        var gap = ClampBezel(bezelGapPixels);
        var cw = (MonitorWidth * 2 + gap).ToString(CultureInfo.InvariantCulture);
        var h = MonitorHeight.ToString(CultureInfo.InvariantCulture);
        var x = (screenIndex <= 0 ? 0 : MonitorWidth + gap).ToString(CultureInfo.InvariantCulture);
        return "scale=" + cw + ":" + h + ":force_original_aspect_ratio=increase:flags=lanczos,crop=" + cw + ":" + h
               + ",crop=" + MonitorWidth.ToString(CultureInfo.InvariantCulture) + ":" + h + ":" + x + ":0";
    }

    /// <summary>화면 산출물 이름 — <c>bg_screen_1.webm</c> / <c>bg_screen_2.mp4</c>(1-기반).</summary>
    public static string DualScreenAssetName(int screenIndex, StockSuiteExportFormat format)
        => DualScreenPrefix + (screenIndex + 1).ToString(CultureInfo.InvariantCulture) + (format == StockSuiteExportFormat.WebMVp9 ? ".webm" : ".mp4");

    /// <summary>화면별 ffmpeg 인자 — WebM 은 VP9, MP4 는 libx264 · 항상 <c>-an</c> 무음.</summary>
    public static string[] BuildDualMonitorSplitArgs(string inputPath, string outputPath, int screenIndex, int bezelGapPixels = DefaultBezelGapPixels, StockSuiteExportFormat format = StockSuiteExportFormat.WebMVp9)
    {
        var vf = BuildDualMonitorCropFilter(screenIndex, bezelGapPixels);
        return format == StockSuiteExportFormat.WebMVp9 ? BuildWebMArgs(inputPath, outputPath, vf) : BuildSharpenArgs(inputPath, outputPath, vf);
    }

    /// <summary>StockSuite 멀티 디스플레이 설정 JSON(System.Text.Json) — 화면 배치·베젤·에셋 파일명.</summary>
    public static string BuildDisplayConfigJson(string leftName, string rightName, int bezelGapPixels, DateTimeOffset updatedAt)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("mode", "dual");
            w.WriteNumber("bezelGapPixels", ClampBezel(bezelGapPixels));
            w.WriteString("updatedAt", updatedAt.ToString("o", CultureInfo.InvariantCulture));
            w.WriteStartArray("screens");
            WriteScreen(w, 1, leftName, 0);
            WriteScreen(w, 2, rightName, MonitorWidth);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());

        static void WriteScreen(Utf8JsonWriter w, int index, string asset, int x)
        {
            w.WriteStartObject();
            w.WriteNumber("index", index);
            w.WriteString("asset", asset);
            w.WriteNumber("x", x);
            w.WriteNumber("y", 0);
            w.WriteNumber("width", MonitorWidth);
            w.WriteNumber("height", MonitorHeight);
            w.WriteEndObject();
        }
    }

    /// <summary>
    /// 🖥️ 듀얼 모니터 파노라마 분할 내보내기 — 32:9/21:9 원본을 좌/우 1920×1080 두 산출물(<c>bg_screen_1/2</c>)로 크롭 + 멀티 디스플레이 설정 갱신.
    /// 화면별 실패 격리([부분 구제] 성공분 보존) · 원자적 교체(임시 → Move). 둘 다 성공 시에만 설정 파일 갱신.
    /// </summary>
    public async Task<DualMonitorSplitResult> ExportDualMonitorSplitAsync(string ultraWideSourcePath, int bezelGapPixels = DefaultBezelGapPixels, string? targetDir = null, StockSuiteExportFormat? format = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ultraWideSourcePath) || !File.Exists(ultraWideSourcePath))
            return new(null, null, null, false, "원본 영상을 찾을 수 없습니다.");
        var dir = string.IsNullOrWhiteSpace(targetDir) ? ConfiguredDirectory : targetDir.Trim().Trim('"');
        if (dir is null) return new(null, null, null, true, "StockSuite 에셋 경로가 설정되지 않았습니다 — 환경설정에서 경로를 지정하세요.");

        var fmt = format ?? DefaultFormat;
        var source = Path.GetFullPath(ultraWideSourcePath);
        string fullDir;
        try
        {
            fullDir = Path.GetFullPath(dir);
            Directory.CreateDirectory(fullDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(null, null, null, true, "StockSuite 에셋 경로 접근 실패: " + ex.Message);
        }

        var outs = new string?[2];
        var errors = new StringBuilder();
        for (var i = 0; i < 2; i++)
        {
            var name = DualScreenAssetName(i, fmt);
            var target = Path.Combine(fullDir, name);
            var temp = Path.Combine(fullDir, Path.GetFileNameWithoutExtension(name) + TempExt + Path.GetExtension(name));
            try
            {
                var err = await TryFfmpegAsync(BuildDualMonitorSplitArgs(source, temp, i, bezelGapPixels, fmt), temp, ct).ConfigureAwait(false);
                if (err is null) { File.Move(temp, target, overwrite: true); outs[i] = target; }
                else { TryDelete(temp); errors.Append(i == 0 ? " 좌:" : " 우:").Append(err); }
            }
            catch (OperationCanceledException) { TryDelete(temp); throw; }
            catch (Exception ex)
            {
                Program.Log("StockSuite.DualSplit", ex);
                TryDelete(temp);
                errors.Append(i == 0 ? " 좌:" : " 우:").Append(ex.Message);
            }
        }

        string? cfg = null;
        if (outs[0] is not null && outs[1] is not null)
        {
            cfg = Path.Combine(fullDir, DisplayConfigName);
            var tmp = cfg + TempExt;
            try
            {
                File.WriteAllText(tmp, BuildDisplayConfigJson(Path.GetFileName(outs[0]!), Path.GetFileName(outs[1]!), bezelGapPixels, UtcNow()));
                File.Move(tmp, cfg, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Program.Log("StockSuite.DisplayConfig", ex);
                TryDelete(tmp);
                cfg = null;
            }
            return new(outs[0], outs[1], cfg, false, "🖥️ 듀얼 모니터 분할 완료 (" + FormatLabel(fmt) + " · 베젤 " + ClampBezel(bezelGapPixels).ToString(CultureInfo.InvariantCulture) + "px)");
        }
        var part = outs[0] is not null || outs[1] is not null ? "[부분 구제] 일부 화면만 생성 —" : "듀얼 모니터 분할 실패 —";
        return new(outs[0], outs[1], null, false, part + errors.ToString());
    }
}
