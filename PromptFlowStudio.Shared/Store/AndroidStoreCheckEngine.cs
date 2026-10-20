using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;

namespace PromptFlowStudio.Shared.Store;

/// <summary>
/// [PromptFlow Studio v4.3.0] Google Play Console 제출 규격 검수(Task 1 · 순수 BCL · System.IO.Compression/Xml/ELF).
///   ▸ Target SDK(API 34/35+) · <b>16KB 메모리 페이지 얼라인먼트(ELF p_align 실파싱)</b> · Play App Signing · Data Safety 선언 · 미사용 권한 진단.
///   ▸ AAB(zip) 엔트리 검사 + 네이티브 .so ELF 프로그램 헤더의 정렬을 오프라인으로 정량 검증(외부 SDK 0).
/// </summary>
public static class AndroidStoreCheckEngine
{
    /// <summary>2026 최소 Target SDK(Play).</summary>
    public const int MinTargetSdk = 34;
    /// <summary>16KB 페이지(0x4000).</summary>
    public const long Page16k = 16384;

    private const string Android = "http://schemas.android.com/apk/res/android";

    /// <summary>스토어 정책상 사유 승인/재검토가 필요한 민감·과다 권한.</summary>
    public static readonly IReadOnlyList<string> SensitivePermissions = new[]
    {
        "android.permission.QUERY_ALL_PACKAGES",
        "android.permission.SYSTEM_ALERT_WINDOW",
        "android.permission.REQUEST_INSTALL_PACKAGES",
        "android.permission.MANAGE_EXTERNAL_STORAGE",
        "android.permission.SCHEDULE_EXACT_ALARM",
    };

    /// <summary>텍스트 AndroidManifest.xml → 요약(uses-sdk · uses-permission).</summary>
    public static AndroidManifestInfo ParseManifestXml(string xml)
    {
        try
        {
            var doc = StoreXml.Load(xml);
            var manifest = doc.Root;
            var pkg = manifest?.Attribute("package")?.Value ?? "";
            var usesSdk = manifest?.Element("uses-sdk");
            int Min(string a) => int.TryParse(usesSdk?.Attribute(XName.Get(a, Android))?.Value, out var v) ? v : 0;
            var perms = manifest?.Elements("uses-permission")
                .Select(e => e.Attribute(XName.Get("name", Android))?.Value ?? "")
                .Where(s => s.Length > 0).ToList() ?? new List<string>();
            return new AndroidManifestInfo(pkg, Min("minSdkVersion"), Min("targetSdkVersion"), perms);
        }
        catch { return new AndroidManifestInfo("", 0, 0, Array.Empty<string>()); }
    }

    /// <summary>민감/과다 권한 탐지.</summary>
    public static IReadOnlyList<string> DetectSensitivePermissions(IReadOnlyList<string> permissions)
        => permissions.Where(p => SensitivePermissions.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();

    /// <summary>ELF64 .so 의 모든 PT_LOAD 세그먼트 p_align ≥ 16KB 인지(리틀엔디안 arm64).</summary>
    public static bool CheckElf16kAlignment(byte[]? so)
    {
        if (so is null || so.Length < 64) return false;
        if (so[0] != 0x7F || so[1] != (byte)'E' || so[2] != (byte)'L' || so[3] != (byte)'F') return false;
        if (so[4] != 2) return false;                          // ELFCLASS64
        var phoff = (long)BitConverter.ToUInt64(so, 32);       // e_phoff
        int phentsize = BitConverter.ToUInt16(so, 54);         // e_phentsize
        int phnum = BitConverter.ToUInt16(so, 56);             // e_phnum
        if (phoff <= 0 || phentsize < 56) return false;
        var anyLoad = false;
        for (var i = 0; i < phnum; i++)
        {
            var off = phoff + (long)i * phentsize;
            if (off + 56 > so.Length) break;
            var pType = BitConverter.ToUInt32(so, (int)off);   // p_type (PT_LOAD=1)
            if (pType != 1) continue;
            anyLoad = true;
            var pAlign = BitConverter.ToUInt64(so, (int)off + 48);   // p_align
            if (pAlign < (ulong)Page16k) return false;
        }
        return anyLoad;
    }

    /// <summary>메타데이터 기반 종합 검수(선언 값 입력 · UI/CI 공용).</summary>
    public static StoreReport Check(AndroidManifestInfo manifest, bool pageAligned16k, bool playAppSigning, bool dataSafetyDeclared)
    {
        var checks = new List<StoreCheck>
        {
            manifest.TargetSdk >= MinTargetSdk
                ? new("target-sdk", $"Target SDK {manifest.TargetSdk}", CheckSeverity.Pass, $"API {MinTargetSdk}+ 충족")
                : new("target-sdk", $"Target SDK {manifest.TargetSdk}", CheckSeverity.Fail, $"API {MinTargetSdk} 미만 — Play 거절"),
            pageAligned16k
                ? new("page-16k", "16KB 페이지 얼라인먼트", CheckSeverity.Pass, "네이티브 .so 16KB 정렬")
                : new("page-16k", "16KB 페이지 얼라인먼트", CheckSeverity.Fail, "2026 필수 — 재빌드(-Wl,-z,max-page-size=16384) 필요"),
            playAppSigning
                ? new("play-signing", "Play App Signing", CheckSeverity.Pass, "업로드 키 서명 확인")
                : new("play-signing", "Play App Signing", CheckSeverity.Fail, "미서명/미등록 — 업로드 키 등록 필요"),
            dataSafetyDeclared
                ? new("data-safety", "Data Safety 선언", CheckSeverity.Pass, "데이터 보안 양식 작성")
                : new("data-safety", "Data Safety 선언", CheckSeverity.Fail, "Play Console 데이터 보안 선언 누락"),
            manifest.MinSdk is >= 21 and <= 34
                ? new("min-sdk", $"Min SDK {manifest.MinSdk}", CheckSeverity.Pass, "합리적 최소 버전")
                : new("min-sdk", $"Min SDK {manifest.MinSdk}", CheckSeverity.Warn, "최소 SDK 확인 권장"),
        };
        foreach (var p in DetectSensitivePermissions(manifest.Permissions))
            checks.Add(new("perm:" + p, "민감 권한: " + p, CheckSeverity.Warn, "사용 사유 선언 필요(미사용 시 제거)"));
        return StoreReportFactory.Build(StorePlatform.Android, checks);
    }

    /// <summary>AAB(zip) 산출물 오프라인 검사 — 엔트리 존재 + 네이티브 .so ELF 16KB 정렬 정량 검증.</summary>
    public static StoreReport InspectAab(string aabPath)
    {
        var checks = new List<StoreCheck>();
        try
        {
            using var zip = new ZipArchive(File.OpenRead(aabPath), ZipArchiveMode.Read);
            bool Has(string suffix) => zip.Entries.Any(e => e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            checks.Add(Has("BundleConfig.pb")
                ? new("aab-config", "BundleConfig.pb", CheckSeverity.Pass, "AAB 번들 구성 존재")
                : new("aab-config", "BundleConfig.pb", CheckSeverity.Fail, "AAB 구조 아님(APK?)"));
            checks.Add(Has("manifest/AndroidManifest.xml")
                ? new("aab-manifest", "base 매니페스트", CheckSeverity.Pass, "존재")
                : new("aab-manifest", "base 매니페스트", CheckSeverity.Warn, "base/manifest 미검출"));

            var soEntries = zip.Entries.Where(e => e.FullName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)).ToList();
            if (soEntries.Count == 0)
                checks.Add(new("aab-so", "네이티브 라이브러리", CheckSeverity.Warn, ".so 없음(순수 관리 코드?)"));
            else
            {
                var bad = new List<string>();
                foreach (var e in soEntries)
                {
                    using var s = e.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    if (!CheckElf16kAlignment(ms.ToArray())) bad.Add(Path.GetFileName(e.FullName));
                }
                checks.Add(bad.Count == 0
                    ? new("aab-16k", $"16KB 정렬({soEntries.Count} .so)", CheckSeverity.Pass, "전 .so PT_LOAD 16KB 정렬")
                    : new("aab-16k", "16KB 정렬 위반", CheckSeverity.Fail, $"미정렬: {string.Join(", ", bad)}"));
            }
        }
        catch (Exception ex) { checks.Add(new("aab-open", "AAB 열기", CheckSeverity.Fail, "읽기 실패: " + ex.Message)); }
        return StoreReportFactory.Build(StorePlatform.Android, checks);
    }
}
