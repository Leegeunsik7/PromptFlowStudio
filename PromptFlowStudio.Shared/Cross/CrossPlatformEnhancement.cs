using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Shared.Cross;

/// <summary>
/// [PromptFlow Studio v4.2] 자율 멀티플랫폼 고도화(Task 5 · 순수 BCL · thread-safe static).
///   ▸ iOS/Android 화면비(16:9 · 19.5:9 · 4:3 iPad · 9:16 쇼츠) <b>자동 최적화 프리뷰</b>(레터/필러박스 fit).
///   ▸ 글로벌 기기 <b>기공(旣工) 시뮬레이션 리포트</b> 발급(Markdown/CSV · 기기별 렌더 박스 요약).
/// </summary>
public static class CrossPlatformEnhancement
{
    /// <summary>화면비 프로파일.</summary>
    public static readonly IReadOnlyList<AspectProfile> Aspects = new[]
    {
        new AspectProfile("16:9", "가로 와이드", 16, 9),
        new AspectProfile("19.5:9", "모던 폰 가로", 195, 90),
        new AspectProfile("4:3", "iPad", 4, 3),
        new AspectProfile("9:16", "세로 쇼츠", 9, 16),
        new AspectProfile("9:19.5", "모던 폰 세로", 90, 195),
    };

    /// <summary>대표 기기 프로파일(iOS/Android/데스크톱/웹).</summary>
    public static readonly IReadOnlyList<DeviceProfile> Devices = new[]
    {
        new DeviceProfile("iphone15pro", "iPhone 15 Pro", DevicePlatform.iOS, DeviceForm.Phone, 1179, 2556, "9:19.5"),
        new DeviceProfile("ipadpro13", "iPad Pro 13\"", DevicePlatform.iOS, DeviceForm.Tablet, 2064, 2752, "4:3"),
        new DeviceProfile("galaxys26u", "Galaxy S26 Ultra", DevicePlatform.Android, DeviceForm.Phone, 1440, 3120, "9:19.5"),
        new DeviceProfile("galaxytab", "Galaxy Tab S9", DevicePlatform.Android, DeviceForm.Tablet, 1600, 2560, "10:16"),
        new DeviceProfile("desktop", "Desktop 16:9", DevicePlatform.Windows, DeviceForm.Desktop, 1920, 1080, "16:9"),
        new DeviceProfile("web", "Web PWA", DevicePlatform.Web, DeviceForm.Desktop, 1280, 720, "16:9"),
    };

    /// <summary>키로 화면비 조회.</summary>
    public static AspectProfile? AspectFor(string? key)
    {
        foreach (var a in Aspects) if (string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    /// <summary>기기 화면비.</summary>
    public static double DeviceRatio(DeviceProfile d) => d.PxH == 0 ? 0 : (double)d.PxW / d.PxH;

    /// <summary>
    /// 콘텐츠(<paramref name="contentRatio"/> = W/H)를 캔버스(<paramref name="canvasW"/>×<paramref name="canvasH"/>)에 맞춰
    /// 중앙 정렬한 박스를 계산한다. 콘텐츠가 더 넓으면 레터박스(상하 여백), 더 좁으면 필러박스(좌우 여백).
    /// </summary>
    public static FitBox Fit(int canvasW, int canvasH, double contentRatio)
    {
        if (canvasW <= 0 || canvasH <= 0 || contentRatio <= 0) return new FitBox(0, 0, Math.Max(0, canvasW), Math.Max(0, canvasH), "none");
        var canvasRatio = (double)canvasW / canvasH;
        int w, h;
        string mode;
        if (contentRatio > canvasRatio) { w = canvasW; h = (int)Math.Round(canvasW / contentRatio); mode = "letterbox"; }
        else if (contentRatio < canvasRatio) { h = canvasH; w = (int)Math.Round(canvasH * contentRatio); mode = "pillarbox"; }
        else { w = canvasW; h = canvasH; mode = "exact"; }
        return new FitBox((canvasW - w) / 2, (canvasH - h) / 2, w, h, mode);
    }

    /// <summary>기기 화면에 콘텐츠 화면비를 맞춘 박스(자동 최적화 프리뷰).</summary>
    public static FitBox FitToDevice(DeviceProfile device, string contentAspectKey, int canvasW, int canvasH)
    {
        var a = AspectFor(contentAspectKey);
        return Fit(canvasW, canvasH, a?.Ratio ?? DeviceRatio(device));
    }

    // ══════════════════════ 시뮬레이션 리포트 ══════════════════════

    /// <summary>스냅샷 + QA + 언어 + 기기 목록 → 리포트 행.</summary>
    public static IReadOnlyList<SimReportRow> BuildSimReport(FeatureParitySnapshot snap, QaReport qa, AppLanguage lang, IReadOnlyList<DeviceProfile> devices)
    {
        var rows = new List<SimReportRow>
        {
            new("Platform", snap.Platform.ToString()),
            new("Language", lang.ToString()),
            new("Theme Count", snap.ThemeCount.ToString(CultureInfo.InvariantCulture)),
            new("QA Pass Rate", qa.PassPercent + "%"),
            new("QA Result", qa.AllPass ? "PASS (no loss)" : $"PARTIAL ({qa.Passed}/{qa.Total})"),
            new("Devices", devices.Count.ToString(CultureInfo.InvariantCulture)),
        };
        foreach (var d in devices)
            rows.Add(new SimReportRow($"Device · {d.Name}", $"{d.PxW}x{d.PxH} ({d.AspectKey}, {d.Platform})"));
        rows.Add(new SimReportRow("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        return rows;
    }

    /// <summary>리포트 행 → Markdown 표.</summary>
    public static string RenderMarkdown(IReadOnlyList<SimReportRow> rows)
    {
        var sb = new StringBuilder("| 항목 | 값 |\n| --- | --- |\n");
        foreach (var r in rows) sb.Append("| ").Append(r.Label).Append(" | ").Append(r.Value).Append(" |\n");
        return sb.ToString();
    }

    /// <summary>리포트 행 → CSV(UTF-8 · InvariantCulture).</summary>
    public static string RenderCsv(IReadOnlyList<SimReportRow> rows)
    {
        var sb = new StringBuilder("label,value\n");
        foreach (var r in rows) sb.Append(Csv(r.Label)).Append(',').Append(Csv(r.Value)).Append('\n');
        return sb.ToString();
    }

    private static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
