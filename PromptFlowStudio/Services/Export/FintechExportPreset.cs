using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PromptFlowStudio.Services.Export;

/// <summary>⚡ 핀테크 내보내기 퀵 프리셋 키.</summary>
public enum FintechPresetKind { OledHts, MobileShorts, Ultrawide32x9, Custom }

/// <summary>
/// ⚡ 핀테크 내보내기 퀵 프리셋 — 발행 모달의 인코딩 스위치 11종 스냅샷(포맷·샤프닝·60fps·해상도·워터마크·티커 배지·LUT·BGM/라우드니스·멀티 HTS 비율·YouTube·Instagram).
/// 직렬화 <c>System.Text.Json</c>(사용자 프리셋 Prefs 저장).
/// </summary>
public sealed record FintechExportPreset(
    StockSuiteExportFormat Format,
    bool Sharpen,
    bool Smooth60,
    StockSuiteResolution Resolution,
    bool Watermark,
    bool TickerBadge,
    LutProfile Lut,
    SnsBgmPreset Bgm,
    HtsUltraAspect MultiHtsAspect,
    bool ToYouTube,
    bool ToInstagram,
    string? Name = null)
{
    /// <summary>💾 사용자 프리셋 목록 최대 보관 수(초과 시 가장 오래된 항목부터 제거).</summary>
    public const int MaxCustomPresets = 20;

    /// <summary>🖥️ OLED HTS — 1080p60 · Pure Black LUT · 무음 · 티커 배지.</summary>
    public static readonly FintechExportPreset OledHts = new(StockSuiteExportFormat.Mp4, true, true, StockSuiteResolution.Fhd1080, false, true,
        LutProfile.PureOledBlack, SnsBgmPreset.None, HtsUltraAspect.Wide16x9, false, false);

    /// <summary>📱 Mobile Shorts — 9:16 쇼츠/릴스 발행 · 기본 색감 · BGM 합성 시 loudnorm -14 LUFS · 워터마크.</summary>
    public static readonly FintechExportPreset MobileShorts = new(StockSuiteExportFormat.Mp4, true, false, StockSuiteResolution.Vertical9x16, true, false,
        LutProfile.None, SnsBgmPreset.FintechSynthwave, HtsUltraAspect.Wide16x9, true, true);

    /// <summary>🌌 32:9 Ultrawide Desk — 3840x1080 @ 60fps · Matrix Neon LUT.</summary>
    public static readonly FintechExportPreset Ultrawide32x9 = new(StockSuiteExportFormat.Mp4, true, true, StockSuiteResolution.Super32x9, false, false,
        LutProfile.MatrixNeon, SnsBgmPreset.None, HtsUltraAspect.Super32x9, false, false);

    public static FintechExportPreset? BuiltIn(FintechPresetKind k) => k switch
    {
        FintechPresetKind.OledHts => OledHts,
        FintechPresetKind.MobileShorts => MobileShorts,
        FintechPresetKind.Ultrawide32x9 => Ultrawide32x9,
        _ => null,
    };

    public static string Key(FintechPresetKind k) => k switch
    {
        FintechPresetKind.OledHts => "oled",
        FintechPresetKind.MobileShorts => "shorts",
        FintechPresetKind.Ultrawide32x9 => "uw329",
        _ => "custom",
    };

    public static FintechPresetKind? ParseKind(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
    {
        "oled" => FintechPresetKind.OledHts,
        "shorts" => FintechPresetKind.MobileShorts,
        "uw329" => FintechPresetKind.Ultrawide32x9,
        "custom" => FintechPresetKind.Custom,
        _ => null,
    };

    public static string Label(FintechPresetKind k) => k switch
    {
        FintechPresetKind.OledHts => "🖥️ OLED HTS (1080p60 · Pure Black LUT · Muted · Ticker Badge)",
        FintechPresetKind.MobileShorts => "📱 Mobile Shorts (9:16 · Normal · Loudnorm -14 LUFS · Watermark)",
        FintechPresetKind.Ultrawide32x9 => "🌌 32:9 Ultrawide Desk (3840x1080 @ 60fps · Matrix Neon LUT)",
        _ => "💾 Custom Preset",
    };

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>역직렬화 — 손상/부재 시 null(예외 전파 없음).</summary>
    public static FintechExportPreset? TryDeserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FintechExportPreset>(json); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>사용자 프리셋 목록 직렬화(null 항목 제외).</summary>
    public static string SerializeList(IReadOnlyList<FintechExportPreset>? list)
        => JsonSerializer.Serialize(list ?? Array.Empty<FintechExportPreset>());

    /// <summary>사용자 프리셋 목록 역직렬화 — 손상/부재 시 빈 목록 · 이름 없는/null 항목 제거.</summary>
    public static List<FintechExportPreset> TryDeserializeList(string? json)
    {
        var r = new List<FintechExportPreset>();
        if (string.IsNullOrWhiteSpace(json)) return r;
        try
        {
            var arr = JsonSerializer.Deserialize<List<FintechExportPreset?>>(json);
            if (arr is null) return r;
            foreach (var p in arr)
                if (p is not null && !string.IsNullOrWhiteSpace(p.Name)) r.Add(p);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException) { r.Clear(); }
        return r;
    }
}
