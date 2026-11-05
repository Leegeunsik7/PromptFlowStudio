using System;
using System.Globalization;
using System.IO;

namespace PromptFlowStudio.Services.Export;

/// <summary>📊 라이브 티커 배지 데이터 — 시세/등락률 번인용.</summary>
public sealed record TickerBadgeData(string Ticker, string? CompanyName, decimal Price, double ChangePercent);

public sealed partial class StockSuiteExporter
{
    public const string TickerUpColor = "0x39FF14";     // 네온 그린(+)
    public const string TickerDownColor = "0xDC143C";   // 크림슨 레드(−)
    public const string TickerBadgeTag = "📊 티커 배지 · ";

    /// <summary>📊 단건 내보내기 우하단 라이브 티커 HUD 배지 번인 여부.</summary>
    public bool BurnTickerBadge { get; set; }

    /// <summary>배지 데이터(null/빈 티커 → 배지 생략 폴백).</summary>
    public TickerBadgeData? TickerBadge { get; set; }

    /// <summary>하단 줄 텍스트 — 예: <c>$185.50 ▲ +2.4%</c>(−: <c>▼ -1.3%</c>).</summary>
    public static string FormatTickerPriceLine(TickerBadgeData d)
    {
        var up = d.ChangePercent >= 0;
        return "$" + d.Price.ToString("0.00", CultureInfo.InvariantCulture) + (up ? " ▲ +" : " ▼ ")
            + d.ChangePercent.ToString("0.0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// 📊 우하단 반투명 카드(drawbox) + 2줄 drawtext — 상단 티커·사명(흰색 Bold 20pt) · 하단 시세·등락(+ 네온 그린 / − 크림슨).
    /// 데이터 null/빈 티커 → null. <paramref name="fontFile"/> null → fontfile 생략(ffmpeg 기본 폰트). 4K 는 2배 스케일.
    /// </summary>
    public static string? BuildTickerBadgeFilter(TickerBadgeData? data, string? fontFile, StockSuiteResolution resolution = StockSuiteResolution.Fhd1080)
    {
        if (data is null || string.IsNullOrWhiteSpace(data.Ticker) || double.IsNaN(data.ChangePercent) || double.IsInfinity(data.ChangePercent)) return null;
        var k = resolution == StockSuiteResolution.Uhd4K ? 2 : 1;
        var font = string.IsNullOrWhiteSpace(fontFile) ? "" : "fontfile='" + fontFile.Replace("\\", "/", StringComparison.Ordinal).Replace(":", "\\:", StringComparison.Ordinal) + "':";
        var title = data.Ticker.Trim().ToUpperInvariant() + (string.IsNullOrWhiteSpace(data.CompanyName) ? "" : " · " + data.CompanyName.Trim());
        var color = data.ChangePercent >= 0 ? TickerUpColor : TickerDownColor;
        var m = (40 * k).ToString(CultureInfo.InvariantCulture);
        var cw = (360 * k).ToString(CultureInfo.InvariantCulture);
        var ch = (84 * k).ToString(CultureInfo.InvariantCulture);
        var pad = (16 * k).ToString(CultureInfo.InvariantCulture);
        var fs = (20 * k).ToString(CultureInfo.InvariantCulture);
        var fsPrice = (24 * k).ToString(CultureInfo.InvariantCulture);
        var topY = (40 + 84 - 12) * k;     // 카드 상단 + 12
        var botY = (40 + 84 - 46) * k;     // 카드 상단 + 46
        var box = ":box=1:boxcolor=black@0.55:boxborderw=" + (8 * k).ToString(CultureInfo.InvariantCulture);
        return "drawbox=x=iw-" + cw + "-" + m + ":y=ih-" + ch + "-" + m + ":w=" + cw + ":h=" + ch + ":color=black@0.55:t=fill"
            + ",drawtext=" + font + "text='" + EscapeDrawText(title) + "':fontsize=" + fs + ":fontcolor=white"
            + ":x=w-" + cw + "-" + m + "+" + pad + ":y=h-" + topY.ToString(CultureInfo.InvariantCulture) + box
            + ",drawtext=" + font + "text='" + EscapeDrawText(FormatTickerPriceLine(data)) + "':fontsize=" + fsPrice + ":fontcolor=" + color
            + ":x=w-" + cw + "-" + m + "+" + pad + ":y=h-" + botY.ToString(CultureInfo.InvariantCulture) + box;
    }

    /// <summary>Bold 폰트 우선(맑은 고딕 Bold → Arial Bold) → 일반 폰트 → null(fontfile 생략).</summary>
    public static string? ResolveTickerBadgeFont()
    {
        try
        {
            var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrEmpty(fonts))
            {
                foreach (var f in new[] { "malgunbd.ttf", "arialbd.ttf", "segoeuib.ttf" })
                {
                    var p = Path.Combine(fonts, f);
                    if (File.Exists(p)) return p;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { Program.Log("StockSuite.TickerFont", ex); }
        return ResolveWatermarkFont();
    }

    /// <summary>기본 체인 + 배지(유효 데이터일 때만) 결합.</summary>
    internal static string? AppendTickerBadge(string? baseFilter, TickerBadgeData? data, string? fontFile, StockSuiteResolution resolution)
    {
        var badge = BuildTickerBadgeFilter(data, fontFile, resolution);
        if (badge is null) return baseFilter;
        return baseFilter is null ? badge : baseFilter + "," + badge;
    }
}
