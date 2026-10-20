using System;
using System.IO;
using System.Text;

namespace PromptFlowStudio.Shared.Store;

/// <summary>OTA 배포 산출물 경로.</summary>
public sealed record OtaBundle(bool Ok, string? PlistPath, string? HtmlPath, string ItmsUrl, string Message);

/// <summary>
/// [PromptFlow Studio v4.4.0] iOS Ad-Hoc 무선 설치(OTA) 패키저(Task 5 · 순수 BCL).
///   ▸ 사내 브라우저에서 원클릭 무선 설치가 되도록 <c>manifest.plist</c> + <c>itms-services://</c> 링크 HTML 인덱스를 생성.
///   ▸ HTTPS 호스팅 필요(iOS 요건). 아이콘/디스플레이 이미지는 선택.
/// </summary>
public static class IosOtaManifestGenerator
{
    /// <summary>manifest.plist 생성 — software-package(ipa) + 메타(bundle-id/version/title).</summary>
    public static string BuildManifestPlist(string bundleId, string version, string title, string ipaUrl, string? iconUrl = null)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\"><dict><key>items</key><array><dict>\n");
        sb.Append("<key>assets</key><array>\n");
        sb.Append("<dict><key>kind</key><string>software-package</string><key>url</key><string>").Append(Xml(ipaUrl)).Append("</string></dict>\n");
        if (!string.IsNullOrWhiteSpace(iconUrl))
        {
            sb.Append("<dict><key>kind</key><string>display-image</string><key>url</key><string>").Append(Xml(iconUrl!)).Append("</string></dict>\n");
            sb.Append("<dict><key>kind</key><string>full-size-image</string><key>url</key><string>").Append(Xml(iconUrl!)).Append("</string></dict>\n");
        }
        sb.Append("</array>\n<key>metadata</key><dict>\n");
        sb.Append("<key>bundle-identifier</key><string>").Append(Xml(bundleId)).Append("</string>\n");
        sb.Append("<key>bundle-version</key><string>").Append(Xml(version)).Append("</string>\n");
        sb.Append("<key>kind</key><string>software</string>\n");
        sb.Append("<key>title</key><string>").Append(Xml(title)).Append("</string>\n");
        sb.Append("</dict></dict></array></dict></plist>\n");
        return sb.ToString();
    }

    /// <summary>itms-services 무선 설치 링크 HTML 인덱스.</summary>
    public static string BuildInstallHtml(string title, string version, string manifestUrl)
    {
        var itms = ItmsUrl(manifestUrl);
        return "<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\">" +
               "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + Xml(title) + " 설치</title>" +
               "<style>body{font-family:-apple-system,system-ui,sans-serif;background:#0b0d14;color:#e6edff;display:grid;place-items:center;min-height:100vh;margin:0}" +
               ".card{text-align:center;padding:32px}.btn{display:inline-block;margin-top:20px;padding:16px 28px;border-radius:14px;font-weight:800;font-size:18px;color:#fff;text-decoration:none;background:linear-gradient(135deg,#6366f1,#a855f7)}</style></head>" +
               "<body><div class=\"card\"><h1>📱 " + Xml(title) + "</h1><p>v" + Xml(version) + " · 사내 무선 설치(Ad-Hoc)</p>" +
               "<a class=\"btn\" href=\"" + Xml(itms) + "\">🚀 이 기기에 설치</a>" +
               "<p style=\"color:#6b7392;font-size:13px;margin-top:18px\">iOS Safari 에서 열어 주세요. 설치 후 [설정 ▸ 일반 ▸ VPN 및 기기 관리]에서 신뢰하세요.</p></div></body></html>";
    }

    /// <summary>manifest URL → itms-services 무선 설치 URL(URL 인코딩).</summary>
    public static string ItmsUrl(string manifestUrl)
        => "itms-services://?action=download-manifest&url=" + Uri.EscapeDataString(manifestUrl);

    /// <summary>OTA 번들(manifest.plist + install.html)을 디렉터리에 생성한다.</summary>
    public static OtaBundle WriteBundle(string outDir, string bundleId, string version, string title, string ipaUrl, string manifestUrl, string? iconUrl = null)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            var plistPath = Path.Combine(outDir, "manifest.plist");
            var htmlPath = Path.Combine(outDir, "install.html");
            File.WriteAllText(plistPath, BuildManifestPlist(bundleId, version, title, ipaUrl, iconUrl), new UTF8Encoding(false));
            File.WriteAllText(htmlPath, BuildInstallHtml(title, version, manifestUrl), new UTF8Encoding(false));
            return new OtaBundle(true, plistPath, htmlPath, ItmsUrl(manifestUrl), "OTA manifest.plist + install.html 생성 완료");
        }
        catch (Exception ex) { return new OtaBundle(false, null, null, "", ex.Message); }
    }

    private static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
