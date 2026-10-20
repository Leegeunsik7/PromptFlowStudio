using System;
using System.Diagnostics;

namespace PromptFlowStudio.Services.Platform;

/// <summary>오디오 딥링크 대상 플랫폼.</summary>
public enum AudioSearchPlatform { YouTube, Instagram, TikTok }

/// <summary>
/// [PromptFlowStudio] 플랫폼 음원 검색 딥링크 서비스 — 순수 BCL(외부 NuGet 0).
///   ▸ 트렌딩 오디오 태그를 각 플랫폼 검색/음원 라이브러리 URL 로 인코딩해, 시스템 <b>기본 브라우저</b>로 즉시 이동(<see cref="Process"/> UseShellExecute).
///   ▸ 유튜브: results?search_query · 인스타: explore 키워드 검색 · 틱톡: search/music.
///   ▸ 안전: <c>http/https</c> URL 만 허용(스킴 위조 차단) · 쿼리는 <see cref="Uri.EscapeDataString"/> 인코딩 · 실패는 false(Graceful).
/// </summary>
public sealed class PlatformDeepLinkService
{
    /// <summary>플랫폼별 오디오/음원 검색 URL 을 만든다(쿼리 인코딩).</summary>
    public string BuildSearchUrl(AudioSearchPlatform platform, string? query)
    {
        var q = Uri.EscapeDataString((query ?? "").Trim());
        return platform switch
        {
            AudioSearchPlatform.YouTube   => $"https://www.youtube.com/results?search_query={q}",
            AudioSearchPlatform.Instagram => $"https://www.instagram.com/explore/search/keyword/?q={q}",
            AudioSearchPlatform.TikTok    => $"https://www.tiktok.com/search/music?q={q}",
            _                             => $"https://www.google.com/search?q={q}",
        };
    }

    /// <summary>플랫폼 검색 페이지를 기본 브라우저로 연다(빈 쿼리/실패는 false).</summary>
    public bool OpenSearch(AudioSearchPlatform platform, string? query)
        => !string.IsNullOrWhiteSpace(query) && OpenUrl(BuildSearchUrl(platform, query));

    /// <summary>8대 플랫폼 표시명 → 업로드/스튜디오 홈 URL(OSMU 딥링크). 알 수 없으면 구글 검색으로 폴백.</summary>
    public string PlatformHomeUrl(string? platform) => (platform ?? "").Trim() switch
    {
        "YouTube Shorts"    => "https://www.youtube.com/upload",
        "Instagram Reels"   => "https://www.instagram.com/",
        "TikTok"            => "https://www.tiktok.com/upload",
        "Naver Clip"        => "https://clip.naver.com/",
        "Karrot Story"      => "https://www.daangn.com/",
        "Xiaohongshu (RED)" => "https://creator.xiaohongshu.com/",
        "Pinterest"         => "https://www.pinterest.com/pin-builder/",
        "Chzzk"             => "https://chzzk.naver.com/",
        _                   => "https://www.google.com/search?q=" + Uri.EscapeDataString((platform ?? "").Trim()),
    };

    /// <summary>플랫폼 업로드/홈 페이지를 기본 브라우저로 연다(http/https만 · 실패는 false · Graceful).</summary>
    public bool OpenPlatform(string? platform) => OpenUrl(PlatformHomeUrl(platform));

    /// <summary>http/https URL 을 기본 브라우저로 연다. 그 외 스킴/실패는 false(Graceful).</summary>
    public bool OpenUrl(string? url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                return false;   // 스킴 위조(file:/javascript: 등) 차단
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("PlatformDeepLink.OpenUrl", ex); return false; }
    }
}
