using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace PromptFlowStudio.Shared.Store;

/// <summary>DTD 선언(plist DOCTYPE)을 안전하게 무시하는 XML 로더(외부 DTD 미조회 · 순수 BCL).</summary>
internal static class StoreXml
{
    public static XDocument Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });
        return XDocument.Load(reader);
    }
}

/// <summary>대상 스토어.</summary>
public enum StorePlatform { Android, iOS }

/// <summary>검수 심각도.</summary>
public enum CheckSeverity { Pass, Warn, Fail }

/// <summary>검수 항목 1건.</summary>
public sealed record StoreCheck(string Id, string Title, CheckSeverity Severity, string Detail);

/// <summary>스토어별 검수 리포트 — 점수(0~100)/통과·경고·실패 수/요약.</summary>
public sealed record StoreReport(StorePlatform Platform, IReadOnlyList<StoreCheck> Checks, int Score, int PassCount, int WarnCount, int FailCount, string Summary)
{
    public bool HasRejectionRisk => FailCount > 0;
}

/// <summary>AndroidManifest 요약.</summary>
public sealed record AndroidManifestInfo(string PackageName, int MinSdk, int TargetSdk, IReadOnlyList<string> Permissions);

/// <summary>iOS Privacy Manifest 요약.</summary>
public sealed record PrivacyManifestInfo(bool Tracking, IReadOnlyList<string> AccessedApiCategories, IReadOnlyList<string> TrackingDomains);

/// <summary>project.json 스키마 호환 결과.</summary>
public sealed record SchemaCompatResult(bool Ok, int DetectedVersionMarkers, string Message);

/// <summary>번들 용량 검사 결과.</summary>
public sealed record BundleSizeResult(long Bytes, long LimitBytes, bool Ok)
{
    public double Mb => Bytes / 1024.0 / 1024.0;
    public int Percent => LimitBytes <= 0 ? 0 : (int)Math.Round(Bytes * 100.0 / LimitBytes);
}

/// <summary>양대 스토어 통합 프리플라이트 리포트.</summary>
public sealed record PreflightReport(StoreReport Android, StoreReport Ios, IReadOnlyList<StoreCheck> Global, int Readiness, string Summary);

/// <summary>기기 스크린샷 규격.</summary>
public sealed record DeviceScreenshotProfile(string Key, string Name, StorePlatform Platform, int W, int H, bool Required);

/// <summary>스크린샷 1컷 계획(기기×로케일).</summary>
public sealed record ScreenshotShot(string Device, string Locale, int W, int H, string FileName);

/// <summary>스토어 등록 정보(언어별).</summary>
public sealed record StoreListing(string LangCode, string Title, string Subtitle, string Keywords, string Description, string WhatsNew);

/// <summary>검수 리포트 조립기(점수/집계 · 순수 함수).</summary>
public static class StoreReportFactory
{
    /// <summary>Pass=1 · Warn=0.5 · Fail=0 가중 점수(0~100)로 리포트를 조립한다.</summary>
    public static StoreReport Build(StorePlatform platform, IReadOnlyList<StoreCheck> checks)
    {
        var pass = checks.Count(c => c.Severity == CheckSeverity.Pass);
        var warn = checks.Count(c => c.Severity == CheckSeverity.Warn);
        var fail = checks.Count(c => c.Severity == CheckSeverity.Fail);
        var score = checks.Count == 0 ? 0 : (int)Math.Round((pass + 0.5 * warn) / checks.Count * 100);
        var summary = fail > 0 ? $"❌ 거절 위험 {fail}건" : warn > 0 ? $"⚠ 경고 {warn}건 — 제출 가능하나 보완 권장" : "✅ 제출 준비 완료";
        return new StoreReport(platform, checks, score, pass, warn, fail, summary);
    }
}
