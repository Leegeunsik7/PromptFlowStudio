using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PromptFlowStudio.Shared.Cross;

/// <summary>기능 스냅샷을 캡처하는 프로브 시임 — 호스트별 구현(데스크톱=실 서비스 · 모바일=WebView 브릿지).</summary>
public interface IParityProbe
{
    /// <summary>현재 호스트가 실제로 노출하는 기능 스냅샷.</summary>
    FeatureParitySnapshot Capture();
}

/// <summary>기본 프로브 — 완전 스냅샷(웹/미지정 호스트 · 실 호스트가 override).</summary>
public sealed class DefaultParityProbe : IParityProbe
{
    public FeatureParitySnapshot Capture() => MobileSimQaEngine.ExpectedBaseline(DevicePlatform.Web);
}

/// <summary>
/// [PromptFlow Studio v4.2] 모바일 시뮬레이터 QA 샌드박스(Task 1 · 순수 BCL · 오프라인 · thread-safe static).
///   ▸ 데스크톱 스튜디오 기능(157종 테마 · 3D CAD 분해도 · 텔레메트리 HUD · Wan 하이브리드 · 감정보이스 · 입체음향 등)이
///     모바일 Blazor WebView 로 <b>손실 없이</b> 전달되는지 스냅샷 비교로 <b>정량 검증</b>한다.
///   ▸ <see cref="VerifyAgainstBaseline"/>(기준 대비 누락 탐지) / <see cref="VerifyParity"/>(데스크톱↔모바일 대조).
/// </summary>
public static class MobileSimQaEngine
{
    /// <summary>기대 테마 총수(112 아기 + 12 기업 + 33 자동차 = 157).</summary>
    public const int ExpectedThemeCount = 157;

    /// <summary>완전 기능 기준 스냅샷.</summary>
    public static FeatureParitySnapshot ExpectedBaseline(DevicePlatform platform)
        => new(platform, ExpectedThemeCount, true, true, true, true, true, true, true, 1);

    /// <summary>스냅샷을 기대 기준과 대조해 PC 기능 누락을 정량 검증한다.</summary>
    public static QaReport VerifyAgainstBaseline(FeatureParitySnapshot s)
    {
        var checks = new List<QaCheck>
        {
            Num("테마 개수", ExpectedThemeCount, s.ThemeCount),
            Flag("3D CAD 분해도", s.CadExplodedView),
            Flag("텔레메트리 HUD", s.TelemetryHud),
            Flag("Wan 하이브리드 파이프라인", s.WanHybrid),
            Flag("감정 보이스", s.EmotionalVoice),
            Flag("3D 입체 음향", s.SpatialAudio),
            Flag("리모트 렌더 오프로딩", s.RemoteRender),
            Flag("골드 각인/기념 카드", s.GoldKeepsake),
            new QaCheck("스키마 필드", ">=1", s.SchemaFieldCount.ToString(CultureInfo.InvariantCulture), s.SchemaFieldCount >= 1),
        };
        return Build(checks, $"{s.Platform} 기능 패리티");
    }

    /// <summary>데스크톱 스냅샷 기준으로 모바일 스냅샷이 손실 없이 커버하는지 대조한다.</summary>
    public static QaReport VerifyParity(FeatureParitySnapshot desktop, FeatureParitySnapshot mobile)
    {
        var checks = new List<QaCheck>
        {
            new QaCheck("테마 개수", $">={desktop.ThemeCount}", mobile.ThemeCount.ToString(CultureInfo.InvariantCulture), mobile.ThemeCount >= desktop.ThemeCount),
            Cover("3D CAD 분해도", desktop.CadExplodedView, mobile.CadExplodedView),
            Cover("텔레메트리 HUD", desktop.TelemetryHud, mobile.TelemetryHud),
            Cover("Wan 하이브리드", desktop.WanHybrid, mobile.WanHybrid),
            Cover("감정 보이스", desktop.EmotionalVoice, mobile.EmotionalVoice),
            Cover("3D 입체 음향", desktop.SpatialAudio, mobile.SpatialAudio),
            Cover("리모트 렌더", desktop.RemoteRender, mobile.RemoteRender),
            Cover("골드 각인", desktop.GoldKeepsake, mobile.GoldKeepsake),
        };
        return Build(checks, $"{desktop.Platform}→{mobile.Platform} 크로스 패리티");
    }

    /// <summary>누락(실패) 기능 목록.</summary>
    public static IReadOnlyList<string> MissingFeatures(QaReport report)
        => report.Checks.Where(c => !c.Pass).Select(c => c.Feature).ToList();

    // ── 내부 ──
    private static QaCheck Num(string f, int expected, int actual) => new(f, $">={expected}", actual.ToString(CultureInfo.InvariantCulture), actual >= expected);
    private static QaCheck Flag(string f, bool present) => new(f, "지원", present ? "지원" : "누락", present);
    private static QaCheck Cover(string f, bool desktop, bool mobile) => new(f, desktop ? "지원" : "-", mobile ? "지원" : "누락", !desktop || mobile);

    private static QaReport Build(List<QaCheck> checks, string label)
    {
        var passed = checks.Count(c => c.Pass);
        var rate = checks.Count == 0 ? 0.0 : passed / (double)checks.Count;
        var summary = passed == checks.Count ? $"✅ {label} — 손실 없음({passed}/{checks.Count})" : $"⚠ {label} — {checks.Count - passed}종 누락({passed}/{checks.Count})";
        return new QaReport(checks.Count, passed, rate, checks, summary);
    }
}
