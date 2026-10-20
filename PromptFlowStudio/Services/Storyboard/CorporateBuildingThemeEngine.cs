using System.Collections.Generic;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 사옥 이미지 기반 AI 시네마틱 브랜드 홍보 엔진 — 순수 BCL(외부 NuGet 0).
///   ▸ 등록된 회사 사옥/공장 정경 이미지(앵커)를 <b>I2V 레퍼런스</b>로 삼아, 드론 360도 궤적 · 홀로그램 로고 빔 · 야경 인텔리전스 반사 등
///     AI 비주얼 연출을 입힌 <b>15초 명품 기업 홍보 숏츠(6컷)</b> 프롬프트를 일괄 자동 생성한다.
///   ▸ 내부적으로 사옥 쇼케이스 <see cref="BabyThemeDef"/>(카테고리 <c>building</c>)를 만들어 <see cref="VideoFormatFactory"/>로 확장 →
///     앵커가 있으므로 전 컷 I2V, 카테고리 <c>building</c> → 3D 엠보싱 워터마크 자동 ON.
///   ▸ 사옥 이미지(앵커) 미등록 시 false(호출부가 안내).
/// </summary>
public sealed class CorporateBuildingThemeEngine(VideoFormatFactory factory)
{
    /// <summary>사옥 쇼케이스 테마(6컷 · I2V 사옥 연출 authored).</summary>
    private static readonly BabyThemeDef BuildingTheme = new(
        "building_showcase", "building", "🏢", "사옥 AI 브랜드 홍보",
        "사옥 사진 1장으로 명품 기업 홍보 숏츠 자동 생성 🏢",
        "드론 360도 · 홀로그램 로고 빔 · 야경 반사 시네마틱", 1.0, BuildBeats());

    /// <summary>사옥 이미지(앵커)가 등록돼 있는지.</summary>
    public bool HasBuilding(StoryboardProject? project)
        => project is not null && !string.IsNullOrEmpty(project.AnchorImagePath);

    /// <summary>사옥 홍보 숏츠(6컷)를 구성한다. 사옥 이미지(앵커) 없으면 false.</summary>
    public bool Apply(StoryboardProject project)
    {
        if (!HasBuilding(project)) return false;                 // 사옥 이미지 필수(I2V 레퍼런스)
        return factory.Apply(project, BuildingTheme, VideoFormat.Shorts);
    }

    // 사옥 정경(I2V)에 얹는 시네마틱 연출 6비트 — 등장→드론상승→유리반사→홀로그램→야경→각인.
    private static IReadOnlyList<BabyBeat> BuildBeats() => new[]
    {
        new BabyBeat("우리가 만들어온 시간이 여기 서 있습니다", "시간",
            "cinematic 24mm establishing shot of the same corporate headquarters building at golden hour, majestic low-angle, volumetric sun rays, premium brand grandeur, film grain",
            30, "fade", "drumroll@C,sparkle@R25"),
        new BabyBeat("하늘에서 그 위용을 마주합니다", "위용",
            "sweeping aerial drone shot orbiting 360 degrees around the same building rising into the sky, smooth cinematic motion, glass facade gleaming, epic scale",
            50, "zoomin", "whoosh@L30,ding@R25"),
        new BabyBeat("유리창마다 미래가 비칩니다", "미래",
            "extreme detail of the same building's glass curtain wall reflecting drifting clouds and a glowing city, intelligent shimmering reflections, sleek high-tech sheen",
            62, "slideleft", "sparkle@C,pop@R25"),
        new BabyBeat("우리의 이름이 빛으로 새겨집니다", "이름",
            "the same building at blue hour as a glowing holographic logo beam projects and materializes above the rooftop, neon light particles, futuristic brand reveal",
            78, "hblur", "ding@C,whoosh@L30,sparkle@R25"),
        new BabyBeat("밤이 되면 도시가 우리를 주목합니다", "주목",
            "dramatic night shot of the same building lit with neon laser-grid accents and warm interior glow, reflections on wet plaza, cinematic city-landmark presence",
            86, "slideleft", "whoosh@C,sparkle@L20,drumroll@R25"),
        new BabyBeat("신뢰의 이름 그 여정은 계속됩니다", "신뢰",
            "majestic slow pull-out revealing the same illuminated headquarters against a glowing skyline with the brand logo embossed in light, prestigious corporate finale, matched framing for a seamless loop",
            35, "fade", "sparkle@C,drumroll@R25"),
    };
}
