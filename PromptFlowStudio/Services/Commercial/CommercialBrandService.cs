using System;
using System.Globalization;
using System.Text;
using PromptFlowStudio.Services.Batch;

namespace PromptFlowStudio.Services.Commercial;

public enum WatermarkPlacement { TopLeft, TopRight, BottomRight, CenterWatermark }

/// <summary>커머셜 광고 포맷 — 6초 범퍼 훅 · 15초 SNS 피드/릴스 · 30초 정식 커머셜.</summary>
public enum CommercialAdFormat { Bumper6s, Feed15s, Narrative30s }

public sealed record BrandKitProfile(
    string BrandName,
    string LogoImagePath,
    WatermarkPlacement WatermarkPlacement,
    string BrandPrimaryColorHex,
    string DisclaimerText);

public sealed record CommercialAdSpec(
    CommercialAdFormat Format,
    float TargetDurationSec,
    string AspectRatios,
    string CtaText);

/// <summary>렌더 후 합성 단계에 넘길 로고·디스클레이머·CTA 오버레이 메타데이터.</summary>
public sealed record CommercialOverlayMetadata(
    string BrandName,
    string LogoImagePath,
    WatermarkPlacement Placement,
    string ColorHex,
    string DisclaimerText,
    string CtaText,
    string AspectRatio,
    string Resolution);

/// <summary>합성 결과 — 최종 프롬프트 · 오버레이 메타데이터 · 렌더 큐 전달용 작업(Pending).</summary>
public sealed record CommercialPromptPackage(
    string Prompt,
    CommercialOverlayMetadata Overlay,
    BatchRenderJob Job);

/// <summary>
/// [PromptFlowStudio] 5번 워크플로우 【커머셜 브랜드 스튜디오】 — 브랜드 킷 + 광고 규격 → 커머셜 프롬프트 합성.
///   ▸ 브랜드 컬러 톤 · 포맷별 시네마틱 룩앤필을 프롬프트에 자동 인젝션.
///   ▸ 로고/디스클레이머/CTA 는 <see cref="CommercialOverlayMetadata"/> 로 패키징(디스클레이머 미입력 시 "유료 광고 포함" 기본 표기).
///   ▸ 결과는 <see cref="BatchRenderJob"/> 규격 — 길이는 포맷 프리셋, 해상도는 화면비에서 유도.
/// </summary>
public sealed class CommercialBrandService
{
    public const string DefaultDisclaimer = "유료 광고 포함";
    public const string DefaultColorHex = "#FF3366";
    public const string Vertical = "9:16 Vertical";
    public const string Landscape = "16:9 Landscape";
    public const string Square = "1:1 Square";

    /// <summary>포맷 프리셋 — 6초 / 15초 / 30초.</summary>
    public static float DurationFor(CommercialAdFormat format) => format switch
    {
        CommercialAdFormat.Bumper6s => 6.0f,
        CommercialAdFormat.Feed15s => 15.0f,
        CommercialAdFormat.Narrative30s => 30.0f,
        _ => 15.0f,
    };

    public static string DefaultAspectFor(CommercialAdFormat format) => format == CommercialAdFormat.Narrative30s ? Landscape : Vertical;

    public static CommercialAdSpec Preset(CommercialAdFormat format, string? aspectRatio = null, string? ctaText = null) =>
        new(format, DurationFor(format), string.IsNullOrWhiteSpace(aspectRatio) ? DefaultAspectFor(format) : aspectRatio.Trim(),
            string.IsNullOrWhiteSpace(ctaText) ? "지금 구매하기" : ctaText.Trim());

    public static string ResolutionFor(string? aspectRatio)
    {
        if (string.IsNullOrEmpty(aspectRatio)) return "1080x1920";
        if (aspectRatio.StartsWith("16:9", StringComparison.Ordinal)) return "1920x1080";
        if (aspectRatio.StartsWith("1:1", StringComparison.Ordinal)) return "1080x1080";
        return "1080x1920";
    }

    /// <summary>#RGB / #RRGGBB 정규화(대문자) — 형식 불일치 시 <see cref="DefaultColorHex"/>.</summary>
    public static string NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return DefaultColorHex;
        var s = hex.Trim();
        if (s[0] == '#') s = s.Substring(1);
        if (s.Length == 3) s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });
        if (s.Length != 6) return DefaultColorHex;
        for (var i = 0; i < 6; i++) if (!Uri.IsHexDigit(s[i])) return DefaultColorHex;
        return "#" + s.ToUpperInvariant();
    }

    public CommercialPromptPackage SynthesizeCommercialPrompt(string productConcept, BrandKitProfile brand, CommercialAdSpec spec)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(spec);

        var concept = string.IsNullOrWhiteSpace(productConcept) ? "신제품" : productConcept.Trim();
        var brandName = string.IsNullOrWhiteSpace(brand.BrandName) ? "브랜드" : brand.BrandName.Trim();
        var color = NormalizeHex(brand.BrandPrimaryColorHex);
        var disclaimer = string.IsNullOrWhiteSpace(brand.DisclaimerText) ? DefaultDisclaimer : brand.DisclaimerText.Trim();
        var cta = string.IsNullOrWhiteSpace(spec.CtaText) ? "지금 구매하기" : spec.CtaText.Trim();
        var aspect = string.IsNullOrWhiteSpace(spec.AspectRatios) ? DefaultAspectFor(spec.Format) : spec.AspectRatios.Trim();
        var duration = float.IsFinite(spec.TargetDurationSec) && spec.TargetDurationSec > 0f ? spec.TargetDurationSec : DurationFor(spec.Format);
        var resolution = ResolutionFor(aspect);

        var sb = new StringBuilder(512);
        sb.Append("Wan2.1, ").Append(LookFor(spec.Format))
          .Append(", premium commercial for \"").Append(brandName).Append("\" — ").Append(concept)
          .Append(", brand color grading ").Append(color).Append(" key accents and rim light")
          .Append(", ").Append(aspect).Append(" composition, ").Append(duration.ToString("0.#", CultureInfo.InvariantCulture)).Append("s")
          .Append(", keep ").Append(SafeZoneFor(brand.WatermarkPlacement)).Append(" clear for logo watermark")
          .Append(", end card CTA \"").Append(cta).Append('"')
          .Append(", disclaimer \"").Append(disclaimer).Append('"');
        var prompt = sb.ToString();

        var overlay = new CommercialOverlayMetadata(brandName, brand.LogoImagePath ?? "", brand.WatermarkPlacement, color, disclaimer, cta, aspect, resolution);
        var job = new BatchRenderJob
        {
            Prompt = prompt,
            Seed = StableSeed(brandName, concept),
            Resolution = resolution,
            DurationSec = duration,
        };
        return new CommercialPromptPackage(prompt, overlay, job);
    }

    private static string LookFor(CommercialAdFormat format) => format switch
    {
        CommercialAdFormat.Bumper6s => "punchy 6-second bumper hook, bold hero product reveal in first second, fast whip-pan",
        CommercialAdFormat.Feed15s => "15-second social feed reel, handheld lifestyle energy, snappy beat-synced cuts",
        CommercialAdFormat.Narrative30s => "30-second cinematic narrative commercial, anamorphic lens, slow dolly, story arc to product payoff",
        _ => "cinematic commercial",
    };

    private static string SafeZoneFor(WatermarkPlacement placement) => placement switch
    {
        WatermarkPlacement.TopLeft => "top-left corner",
        WatermarkPlacement.TopRight => "top-right corner",
        WatermarkPlacement.BottomRight => "bottom-right corner",
        _ => "center area",
    };

    /// <summary>프로세스 간 결정적 시드(FNV-1a) — string.GetHashCode 는 실행마다 달라짐.</summary>
    private static long StableSeed(string a, string b)
    {
        var h = 2166136261u;
        foreach (var c in a) h = unchecked((h ^ c) * 16777619u);
        h = unchecked((h ^ '|') * 16777619u);
        foreach (var c in b) h = unchecked((h ^ c) * 16777619u);
        return h & int.MaxValue;
    }
}
