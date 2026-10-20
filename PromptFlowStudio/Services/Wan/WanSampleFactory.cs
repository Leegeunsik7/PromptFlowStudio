using System;
using System.Text;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlow Studio v5.6] 베이스 샘플/모의 프레임 생성기 — 순수 BCL(외부 SDK 0).
///   ▸ 렌더 전 대표 레퍼런스, 로컬 스텁 모의 렌더 결과, 템플릿 썸네일을 <b>SVG data URL</b>(image/svg+xml)로 즉시 생성.
///   ▸ 네트워크·GPU 없이 노드 카드에서 즉시 표출 가능(무분별 API 호출 방지 UX).
/// </summary>
public static class WanSampleFactory
{
    /// <summary>라벨/강조색/부제 → 재생 카드 스타일 SVG data URL(640×360).</summary>
    public static string FrameDataUrl(string? label, string accent = "#7c5cff", string? sub = "BASE SAMPLE")
    {
        var l = Escape(label);
        var s = Escape(sub);
        var svg =
            "<svg xmlns='http://www.w3.org/2000/svg' width='640' height='360' viewBox='0 0 640 360'>" +
            "<defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'>" +
            $"<stop offset='0' stop-color='{accent}'/><stop offset='1' stop-color='#0b0d14'/></linearGradient></defs>" +
            "<rect width='640' height='360' fill='url(#g)'/>" +
            "<rect x='16' y='16' width='608' height='328' rx='14' fill='none' stroke='rgba(255,255,255,.18)'/>" +
            "<circle cx='320' cy='150' r='52' fill='rgba(255,255,255,.14)'/>" +
            "<polygon points='306,124 306,176 350,150' fill='#ffffff' opacity='.9'/>" +
            $"<text x='320' y='248' font-family='sans-serif' font-size='25' font-weight='800' fill='#ffffff' text-anchor='middle'>{l}</text>" +
            $"<text x='320' y='282' font-family='monospace' font-size='13' fill='rgba(255,255,255,.6)' text-anchor='middle'>{s}</text>" +
            "</svg>";
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    /// <summary>data URL 이 이미지(svg/png/jpg)인지 — 프리뷰가 &lt;video&gt; 대신 &lt;img&gt; 로 렌더할지 판정.</summary>
    public static bool IsImageUrl(string? url)
        => !string.IsNullOrEmpty(url) &&
           (url!.StartsWith("data:image", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || url.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));

    private static string Escape(string? v) => (v ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
