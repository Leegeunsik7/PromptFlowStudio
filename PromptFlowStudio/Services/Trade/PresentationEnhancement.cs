using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PromptFlowStudio.Services.Trade;

/// <summary>ROI 입력 — 수동/자동 검사시간(분/대)·일 생산량·인건비·연 근무일·시스템 도입비.</summary>
public sealed record RoiInput(double ManualMinPerUnit, double AutoMinPerUnit, int UnitsPerDay, double LaborCostPerHour, int WorkdaysPerYear, double SystemCost);

/// <summary>ROI 결과 — 절감 시간/비용·회수기간·1년차 ROI·처리량 향상.</summary>
public sealed record RoiResult(double TimeSavedPerUnitMin, double DailyHoursSaved, double AnnualLaborSaving, double PaybackMonths, double YearOneRoiPct, double ThroughputGainPct, string Summary);

/// <summary>
/// [PromptFlowStudio] B2B 수주 ROI 계산 연산기(v3.2.0 자율 발상 · static · 순수 BCL).
///   ▸ 실증 수동 검사 대비 자동화 공정의 <b>시간·인건비 절감</b>과 <b>회수기간(Payback)·1년차 ROI·처리량 향상률</b>을 즉석 산출해 수주 설득력을 높인다.
/// </summary>
public static class B2bRoiCalculator
{
    /// <summary>대표 프리셋(수동 12분 → 자동 3분/대 · 일 400대 · 인건비 25,000 · 250일 · 도입비 1.2억).</summary>
    public static RoiInput Default => new(12, 3, 400, 25000, 250, 120_000_000);

    /// <summary>ROI 지표를 계산한다(결정론적 · 0/음수 방어).</summary>
    public static RoiResult Compute(RoiInput i)
    {
        var savedPerUnit = Math.Max(0, i.ManualMinPerUnit - i.AutoMinPerUnit);
        var dailyHrs = savedPerUnit * Math.Max(0, i.UnitsPerDay) / 60.0;
        var annualHrs = dailyHrs * Math.Max(0, i.WorkdaysPerYear);
        var annualSaving = annualHrs * Math.Max(0, i.LaborCostPerHour);
        var payback = annualSaving > 0 ? i.SystemCost / (annualSaving / 12.0) : double.PositiveInfinity;
        var roi = i.SystemCost > 0 ? (annualSaving - i.SystemCost) / i.SystemCost * 100.0 : 0;
        var throughput = i.AutoMinPerUnit > 0 ? (i.ManualMinPerUnit / i.AutoMinPerUnit - 1.0) * 100.0 : 0;

        var paybackTxt = double.IsInfinity(payback) ? "N/A" : $"{payback:0.0}개월";
        var summary = $"연 {annualSaving / 100_000_000.0:0.0}억 절감 · 회수 {paybackTxt} · 1년차 ROI {roi:0}% · 처리량 +{throughput:0}%";
        return new RoiResult(savedPerUnit, Math.Round(dailyHrs, 1), Math.Round(annualSaving, 0), Math.Round(payback, 1), Math.Round(roi, 0), Math.Round(throughput, 0), summary);
    }
}

/// <summary>스펙 비교 행 — 항목·기존 방식·PromptFlow·우위.</summary>
public sealed record SpecRow(string Feature, string Conventional, string PromptFlow, string Advantage);

/// <summary>
/// [PromptFlowStudio] 영문 대화형 제품 스펙 비교표 자동 생성기(v3.2.0 자율 발상 · static · 순수 BCL).
///   ▸ 기존(외주/수동) 방식 대비 PromptFlow AI 미디어 스튜디오의 우위를 <b>영문 비교표</b>(Markdown/HTML)로 즉석 생성 — 글로벌 바이어 설득용.
/// </summary>
public static class SpecComparisonBuilder
{
    /// <summary>장비/과제명 기반 기본 비교 행(영문 · B2B 수주용).</summary>
    public static IReadOnlyList<SpecRow> BuildDefault(string equipmentName)
    {
        var eq = string.IsNullOrWhiteSpace(equipmentName) ? "the equipment" : equipmentName;
        return new List<SpecRow>
        {
            new("Turnaround", "2-3 weeks (agency)", "Same-day AI generation", "~20x faster"),
            new("Cost / promo", "USD 5,000-15,000", "In-house, near-zero marginal", ">90% lower"),
            new("Multi-language", "Extra fee per language", "EN/DE/ZH/JA one-click dubbing", "4 markets at once"),
            new("3D visualization", "Manual CAD render outsourcing", $"CAD/STL hologram of {eq}", "Instant, offline"),
            new("Telemetry HUD", "Not available", "Gauge / Scope / Dyno overlay from CSV", "Data-proven trust"),
            new("Revisions", "Billable, days of delay", "Voice-directed, real-time", "Unlimited, instant"),
            new("Delivery & audit", "Files over email", "ZIP master + AES-256 audit log", "Traceable, secure"),
        };
    }

    /// <summary>비교표 → Markdown.</summary>
    public static string RenderMarkdown(IReadOnlyList<SpecRow> rows, string title = "PromptFlow Studio vs Conventional Production")
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(title).Append("\n\n");
        sb.Append("| Feature | Conventional | PromptFlow Studio | Advantage |\n|---|---|---|---|\n");
        foreach (var r in rows) sb.Append($"| {r.Feature} | {r.Conventional} | {r.PromptFlow} | **{r.Advantage}** |\n");
        return sb.ToString();
    }

    /// <summary>비교표 → 자체 완결 HTML(피치/인쇄용).</summary>
    public static string RenderHtml(IReadOnlyList<SpecRow> rows, string title = "PromptFlow Studio vs Conventional Production")
    {
        var body = new StringBuilder();
        foreach (var r in rows)
            body.Append($"<tr><td>{Esc(r.Feature)}</td><td class=c>{Esc(r.Conventional)}</td><td class=p>{Esc(r.PromptFlow)}</td><td class=a>{Esc(r.Advantage)}</td></tr>");
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>" + Esc(title) + "</title><style>" +
            "body{font-family:Segoe UI,system-ui,sans-serif;background:#0b0d14;color:#e6edff;max-width:820px;margin:0 auto;padding:26px}" +
            "h2{font-size:18px;background:linear-gradient(90deg,#fde68a,#f5c451);-webkit-background-clip:text;background-clip:text;color:transparent}" +
            "table{width:100%;border-collapse:collapse;font-size:13px;margin-top:14px}th,td{border-bottom:1px solid #2b2f42;padding:9px 11px;text-align:left}" +
            "th{color:#f5c451;font-size:11px;text-transform:uppercase;letter-spacing:.06em}.c{color:#9aa6c8}.p{color:#7dd3fc}.a{color:#86efac;font-weight:700}" +
            "</style></head><body><h2>" + Esc(title) + "</h2><table><thead><tr><th>Feature</th><th>Conventional</th><th>PromptFlow Studio</th><th>Advantage</th></tr></thead><tbody>" +
            body + "</tbody></table></body></html>";
    }

    private static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
