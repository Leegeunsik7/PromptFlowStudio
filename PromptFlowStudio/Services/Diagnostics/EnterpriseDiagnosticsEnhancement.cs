using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PromptFlowStudio.Services.Diagnostics;

/// <summary>시스템 헬스 진단 보고서 모델.</summary>
public sealed record HealthReportModel(
    int Score, string Grade, double RamLoadPct, double FragPct, long WorkingSetMb,
    int FfmpegCount, int StalledCount, double VramUsedMb, double VramTotalMb,
    int RecoveryCount, int RecoveredCount, int AssetCount, string GeneratedAt, IReadOnlyList<RecoveryEvent> RecentEvents);

/// <summary>
/// [PromptFlowStudio] 시스템 헬스 진단 보고서 자동 발급기(v3.4.0 자율 발상 · static · 순수 BCL).
///   ▸ 진단 스냅샷 + 복구 이력 + 자산 통계를 <b>인쇄용 진단서(HTML/Markdown)</b>로 발급 — 장애 사후 분석·엔터프라이즈 운영 리포트.
/// </summary>
public static class SystemHealthReport
{
    /// <summary>스냅샷/복구/자산 통계로 보고서 모델을 구성한다.</summary>
    public static HealthReportModel BuildModel(DiagSnapshot snap, IReadOnlyList<RecoveryEvent> events, int recovered, int assetCount)
    {
        var grade = snap.HealthScore >= 85 ? "S" : snap.HealthScore >= 70 ? "A" : snap.HealthScore >= 55 ? "B" : snap.HealthScore >= 40 ? "C" : "D";
        return new HealthReportModel(
            snap.HealthScore, grade, snap.RamLoadPct, snap.FragPct, snap.WorkingSetMb,
            snap.FfmpegCount, snap.StalledCount, snap.VramUsedMb, snap.VramTotalMb,
            events?.Count ?? 0, recovered, assetCount,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            (events ?? Array.Empty<RecoveryEvent>()).Take(10).ToList());
    }

    /// <summary>진단 보고서 → Markdown.</summary>
    public static string RenderMarkdown(HealthReportModel m)
    {
        var sb = new StringBuilder();
        sb.Append("# 🩺 PromptFlow Studio — System Health Report\n\n");
        sb.Append($"**종합 헬스 점수: {m.Score} / 100 · 등급 {m.Grade}** · 생성 {m.GeneratedAt}\n\n");
        sb.Append("| 지표 | 값 |\n|---|---|\n");
        sb.Append($"| RAM 부하 | {m.RamLoadPct:0.0}% |\n");
        sb.Append($"| 힙 파편화 | {m.FragPct:0.0}% |\n");
        sb.Append($"| 워킹셋 | {m.WorkingSetMb} MB |\n");
        sb.Append($"| VRAM | {m.VramUsedMb:0} / {m.VramTotalMb:0} MB |\n");
        sb.Append($"| 활성 FFmpeg | {m.FfmpegCount} (정체 {m.StalledCount}) |\n");
        sb.Append($"| 복구 이벤트 | {m.RecoveredCount}/{m.RecoveryCount} 복구 |\n");
        sb.Append($"| 인덱싱 자산 | {m.AssetCount} |\n\n");
        if (m.RecentEvents.Count > 0)
        {
            sb.Append("## 복구 이력\n\n| 시각 | 라벨 | 사유 | 조치 | 결과 |\n|---|---|---|---|---|\n");
            foreach (var e in m.RecentEvents)
                sb.Append($"| {e.Time:HH:mm:ss} | {e.Label} | {e.Reason} | {e.Action} | {(e.Recovered ? "복구✓" : "실패✗")} |\n");
        }
        return sb.ToString();
    }

    /// <summary>진단 보고서 → 자체 완결 HTML(인쇄/뷰용).</summary>
    public static string RenderHtml(HealthReportModel m)
    {
        var rows = new StringBuilder();
        foreach (var e in m.RecentEvents)
            rows.Append($"<tr><td>{e.Time:HH:mm:ss}</td><td>{Esc(e.Label)}</td><td>{Esc(e.Reason)}</td><td>{Esc(e.Action)}</td><td class=\"{(e.Recovered ? "ok" : "no")}\">{(e.Recovered ? "복구 ✓" : "실패 ✗")}</td></tr>");
        var scoreColor = m.Score >= 70 ? "#22c55e" : m.Score >= 40 ? "#f59e0b" : "#ef4444";
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>System Health Report</title><style>" +
            "body{font-family:Segoe UI,system-ui,sans-serif;background:#0b0d14;color:#e6edff;max-width:820px;margin:0 auto;padding:26px}" +
            "h1{font-size:19px}.score{font-size:44px;font-weight:800;color:" + scoreColor + "}.g{font-size:13px;color:#9aa6c8}" +
            "table{width:100%;border-collapse:collapse;font-size:13px;margin-top:12px}th,td{border-bottom:1px solid #2b2f42;padding:8px 10px;text-align:left}" +
            "th{color:#7dd3fc;font-size:11px;text-transform:uppercase;letter-spacing:.06em}.ok{color:#86efac}.no{color:#fca5a5}" +
            ".meta{display:grid;grid-template-columns:1fr 1fr;gap:6px 18px;font-size:13px;margin:10px 0 18px}.meta b{color:#9aa6c8}" +
            "</style></head><body><h1>🩺 System Health Report</h1>" +
            $"<div class=score>{m.Score}<span class=g> / 100 · 등급 {m.Grade}</span></div><div class=g>생성 {Esc(m.GeneratedAt)}</div>" +
            "<div class=meta>" +
            $"<div><b>RAM 부하</b> {m.RamLoadPct:0.0}%</div><div><b>힙 파편화</b> {m.FragPct:0.0}%</div>" +
            $"<div><b>워킹셋</b> {m.WorkingSetMb} MB</div><div><b>VRAM</b> {m.VramUsedMb:0}/{m.VramTotalMb:0} MB</div>" +
            $"<div><b>활성 FFmpeg</b> {m.FfmpegCount} (정체 {m.StalledCount})</div><div><b>복구</b> {m.RecoveredCount}/{m.RecoveryCount}</div>" +
            $"<div><b>인덱싱 자산</b> {m.AssetCount}</div></div>" +
            (m.RecentEvents.Count > 0
                ? "<h2 style=\"font-size:15px\">복구 이력</h2><table><thead><tr><th>시각</th><th>라벨</th><th>사유</th><th>조치</th><th>결과</th></tr></thead><tbody>" + rows + "</tbody></table>"
                : "<div class=g>복구 이벤트 없음 — 안정 운영 중.</div>") +
            "</body></html>";
    }

    private static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
