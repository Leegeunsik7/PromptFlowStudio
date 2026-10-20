using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace PromptFlowStudio.Shared.Store;

/// <summary>
/// [PromptFlow Studio v4.3.0] Apple App Store Connect / TestFlight 검수(Task 2 · 순수 BCL · Compression/Xml).
///   ▸ IPA(zip) 내 <c>PrivacyInfo.xcprivacy</c> 존재/파싱 · Required Reason API(파일 타임스탬프·부팅 시간 등) 유효 선언 ·
///     Distribution Provisioning Profile 만료일 · App Store Connect 사전 점검을 오프라인 정량 진단(외부 SDK 0).
/// </summary>
public static class IosStoreCheckEngine
{
    /// <summary>Apple Required Reason API 카테고리(선언 필수 대상).</summary>
    public static readonly IReadOnlyList<string> RequiredReasonCategories = new[]
    {
        "NSPrivacyAccessedAPICategoryFileTimestamp",
        "NSPrivacyAccessedAPICategorySystemBootTime",
        "NSPrivacyAccessedAPICategoryDiskSpace",
        "NSPrivacyAccessedAPICategoryUserDefaults",
        "NSPrivacyAccessedAPICategoryActiveKeyboards",
    };

    /// <summary>PrivacyInfo.xcprivacy(plist XML) → 요약(추적 여부 · 접근 API 카테고리 · 추적 도메인).</summary>
    public static PrivacyManifestInfo ParsePrivacyManifest(string plistXml)
    {
        try
        {
            var dict = RootDict(plistXml);
            var tracking = BoolFor(dict, "NSPrivacyTracking");
            var categories = new List<string>();
            if (ValueFor(dict, "NSPrivacyAccessedAPITypes") is XElement arr && arr.Name.LocalName == "array")
                foreach (var d in arr.Elements("dict"))
                    if (StringFor(d, "NSPrivacyAccessedAPIType") is { Length: > 0 } cat) categories.Add(cat);
            var domains = new List<string>();
            if (ValueFor(dict, "NSPrivacyTrackingDomains") is XElement td && td.Name.LocalName == "array")
                domains.AddRange(td.Elements("string").Select(s => s.Value));
            return new PrivacyManifestInfo(tracking, categories, domains);
        }
        catch { return new PrivacyManifestInfo(false, Array.Empty<string>(), Array.Empty<string>()); }
    }

    /// <summary>embedded.mobileprovision(CMS+plist) 텍스트에서 ExpirationDate 를 추출한다(없으면 null).</summary>
    public static DateTimeOffset? ParseProfileExpiry(string mobileProvisionText)
    {
        try
        {
            var start = mobileProvisionText.IndexOf("<?xml", StringComparison.Ordinal);
            var end = mobileProvisionText.IndexOf("</plist>", StringComparison.Ordinal);
            if (start < 0 || end < 0) return null;
            var xml = mobileProvisionText.Substring(start, end - start + "</plist>".Length);
            var dict = RootDict(xml);
            if (ValueFor(dict, "ExpirationDate") is XElement d && d.Name.LocalName == "date"
                && DateTimeOffset.TryParse(d.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                return dt;
            return null;
        }
        catch { return null; }
    }

    /// <summary>메타데이터 기반 종합 검수.</summary>
    public static StoreReport Check(PrivacyManifestInfo? privacy, DateTimeOffset? profileExpiry, DateTimeOffset now)
    {
        var checks = new List<StoreCheck>();
        if (privacy is null)
            checks.Add(new("privacy-manifest", "PrivacyInfo.xcprivacy", CheckSeverity.Fail, "필수 개인정보 매니페스트 누락 — 심사 거절"));
        else
        {
            checks.Add(new("privacy-manifest", "PrivacyInfo.xcprivacy", CheckSeverity.Pass, "개인정보 매니페스트 존재"));
            var declared = privacy.AccessedApiCategories.Intersect(RequiredReasonCategories, StringComparer.Ordinal).ToList();
            checks.Add(declared.Count > 0
                ? new("required-reason", $"Required Reason API {declared.Count}종", CheckSeverity.Pass, string.Join(", ", declared.Select(Short)))
                : new("required-reason", "Required Reason API", CheckSeverity.Warn, "선언 없음 — 사용 API 있으면 거절(File timestamp/Boot time 등)"));
            if (privacy.Tracking && privacy.TrackingDomains.Count == 0)
                checks.Add(new("tracking", "추적 도메인", CheckSeverity.Warn, "NSPrivacyTracking=true 인데 도메인 미선언"));
        }

        if (profileExpiry is null)
            checks.Add(new("provisioning", "프로비저닝 프로파일", CheckSeverity.Warn, "만료일 확인 불가(임베드 프로파일 없음?)"));
        else
        {
            var days = (int)(profileExpiry.Value - now).TotalDays;
            checks.Add(days < 0
                ? new("provisioning", "프로비저닝 프로파일", CheckSeverity.Fail, $"만료됨({-days}일 경과) — 재발급 필요")
                : days <= 14
                    ? new("provisioning", "프로비저닝 프로파일", CheckSeverity.Warn, $"{days}일 후 만료 — 갱신 권장")
                    : new("provisioning", "프로비저닝 프로파일", CheckSeverity.Pass, $"유효({days}일 남음)"));
        }
        return StoreReportFactory.Build(StorePlatform.iOS, checks);
    }

    /// <summary>IPA(zip) 산출물 오프라인 검사 — PrivacyInfo.xcprivacy + embedded.mobileprovision 추출·진단.</summary>
    public static StoreReport InspectIpa(string ipaPath, DateTimeOffset now)
    {
        try
        {
            using var zip = new ZipArchive(File.OpenRead(ipaPath), ZipArchiveMode.Read);
            var privEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("PrivacyInfo.xcprivacy", StringComparison.OrdinalIgnoreCase));
            var provEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("embedded.mobileprovision", StringComparison.OrdinalIgnoreCase));
            PrivacyManifestInfo? privacy = privEntry is null ? null : ParsePrivacyManifest(ReadText(privEntry));
            DateTimeOffset? expiry = provEntry is null ? null : ParseProfileExpiry(ReadText(provEntry));
            return Check(privacy, expiry, now);
        }
        catch (Exception ex)
        {
            return StoreReportFactory.Build(StorePlatform.iOS, new List<StoreCheck> { new("ipa-open", "IPA 열기", CheckSeverity.Fail, "읽기 실패: " + ex.Message) });
        }
    }

    // ── plist 헬퍼 ──
    private static XElement RootDict(string plistXml)
    {
        var doc = StoreXml.Load(plistXml);
        return doc.Root?.Element("dict") ?? new XElement("dict");
    }
    private static XElement? ValueFor(XElement dict, string key)
    {
        var keys = dict.Elements("key").ToList();
        for (var i = 0; i < keys.Count; i++)
            if (keys[i].Value == key) return keys[i].ElementsAfterSelf().FirstOrDefault();
        return null;
    }
    private static bool BoolFor(XElement dict, string key) => ValueFor(dict, key)?.Name.LocalName == "true";
    private static string StringFor(XElement dict, string key) => ValueFor(dict, key) is XElement e && e.Name.LocalName == "string" ? e.Value : "";
    private static string Short(string cat) => cat.Replace("NSPrivacyAccessedAPICategory", "");
    private static string ReadText(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var r = new StreamReader(s, System.Text.Encoding.UTF8);
        return r.ReadToEnd();
    }
}
