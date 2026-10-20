using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Shared.Hardening;

/// <summary>하드닝 점검 항목.</summary>
public sealed record HardenCheck(string Id, string Title, bool Pass, string Detail);

/// <summary>하드닝 점검 리포트.</summary>
public sealed record HardenReport(IReadOnlyList<HardenCheck> Checks, int PassCount, int Total, string Summary)
{
    public bool AllPass => Total > 0 && PassCount == Total;
    public int Percent => Total == 0 ? 0 : (int)Math.Round(PassCount * 100.0 / Total);
}

/// <summary>NLU 안전 파싱 결과 — 성공/타임아웃(ReDoS 폴백)/토큰/인텐트/정규화문.</summary>
public sealed record NluParseResult(bool Ok, bool TimedOut, IReadOnlyList<string> Tokens, IReadOnlyList<string> Intents, string Normalized);

/// <summary>대용량 리스트 페이징 계획.</summary>
public sealed record PagePlan(int Total, int PageSize, int PageCount)
{
    /// <summary>페이지(0-base) → 슬라이스 [Start, End).</summary>
    public (int Start, int Count) Slice(int page)
    {
        if (PageSize <= 0 || Total <= 0) return (0, 0);
        var p = Math.Clamp(page, 0, Math.Max(0, PageCount - 1));
        var start = p * PageSize;
        return (start, Math.Min(PageSize, Total - start));
    }
}

/// <summary>하드닝 리포트 조립기.</summary>
public static class HardenReportFactory
{
    public static HardenReport Build(IReadOnlyList<HardenCheck> checks)
    {
        var pass = checks.Count(c => c.Pass);
        var summary = pass == checks.Count ? $"✅ 전 항목 하드닝 통과({pass}/{checks.Count})" : $"⚠ {checks.Count - pass}건 보완 필요({pass}/{checks.Count})";
        return new HardenReport(checks, pass, checks.Count, summary);
    }
}
