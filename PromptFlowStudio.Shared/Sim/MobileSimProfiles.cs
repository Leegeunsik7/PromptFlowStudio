using System;
using System.Collections.Generic;
using PromptFlowStudio.Shared.Cross;   // DevicePlatform (v4.2 재사용)

namespace PromptFlowStudio.Shared.Sim;

/// <summary>시뮬레이터 방향.</summary>
public enum SimOrientation { Portrait, Landscape }

/// <summary>[v5.6.7] 노치/펀치홀 위치 — 세로(Portrait)는 Top 고정, 가로(Landscape) 피벗 시 이동할 측면(Left/Right).</summary>
public enum NotchPosition { Top, Left, Right }

/// <summary>시뮬레이터 디바이스 스킨 — 논리 해상도(dp) + 노치/다이내믹 아일랜드 + 가로 피벗 시 노치 이동 측면.</summary>
public sealed record SimDevice(string Key, string Name, DevicePlatform Platform, int LogicalW, int LogicalH, bool Notch, bool DynamicIsland, string Emoji, NotchPosition NotchSide = NotchPosition.Left);

/// <summary>
/// [PromptFlow Studio v4.4.0] 모바일 시뮬레이터 디바이스 프로파일 & fit 스케일(Task 6 core · 순수 BCL).
///   ▸ 갤럭시 S26 Ultra / iPhone 15 Pro / iPad Pro 논리 해상도를 정확히 에뮬레이션(Aspect Ratio 보존).
///   ▸ PC 사이드 패널 크기에 맞춰 CSS transform:scale 계수를 산출(레이아웃 깨짐 없이 축소 렌더).
/// </summary>
public static class MobileSimProfiles
{
    /// <summary>시뮬 디바이스 스킨(논리 dp).</summary>
    public static readonly IReadOnlyList<SimDevice> Devices = new[]
    {
        new SimDevice("galaxys26u", "Galaxy S26 Ultra", DevicePlatform.Android, 412, 915, true, false, "🤖"),
        new SimDevice("iphone15pro", "iPhone 15 Pro", DevicePlatform.iOS, 393, 852, false, true, "🍎"),
        new SimDevice("ipadpro13", "iPad Pro 13\"", DevicePlatform.iOS, 1024, 1366, false, false, "🍎"),
    };

    /// <summary>키로 디바이스 조회(없으면 첫 항목).</summary>
    public static SimDevice For(string? key)
    {
        foreach (var d in Devices) if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)) return d;
        return Devices[0];
    }

    /// <summary>방향 반영 논리 크기(dp).</summary>
    public static (int W, int H) Logical(SimDevice d, SimOrientation o)
        => o == SimOrientation.Landscape ? (d.LogicalH, d.LogicalW) : (d.LogicalW, d.LogicalH);

    /// <summary>패널(paneW×paneH)에 디바이스를 꽉 맞추는 스케일(≤1 · 여백 margin px 반영).</summary>
    public static double FitScale(SimDevice d, int paneW, int paneH, SimOrientation o = SimOrientation.Portrait, int margin = 24)
    {
        var (w, h) = Logical(d, o);
        if (w <= 0 || h <= 0) return 1.0;
        var availW = Math.Max(1, paneW - margin);
        var availH = Math.Max(1, paneH - margin);
        var scale = Math.Min(availW / (double)w, availH / (double)h);
        return scale <= 0 ? 0.1 : Math.Min(1.0, scale);
    }

    /// <summary>스케일 후 실제 표시 픽셀 크기.</summary>
    public static (int W, int H) ScaledSize(SimDevice d, double scale, SimOrientation o = SimOrientation.Portrait)
    {
        var (w, h) = Logical(d, o);
        return ((int)Math.Round(w * scale), (int)Math.Round(h * scale));
    }
}
