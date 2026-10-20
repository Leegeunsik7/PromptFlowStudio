using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject
using PromptFlowStudio.Services.Wan;          // WanSampleFactory

namespace PromptFlowStudio.Services.Flow;

/// <summary>원클릭 파이프라인 템플릿 정의 — 세그먼트 수·프롬프트·모션 프리셋·강조색.</summary>
public sealed record ChainTemplate(string Key, string Emoji, string Name, string Summary, int Segments, string PromptText, string MotionPreset, string Accent);

/// <summary>
/// [PromptFlow Studio v5.6 · Task1] 원클릭 파이프라인 템플릿 라이브러리 — 대표 체인을 즉시 캔버스에 배치(순수 BCL).
/// </summary>
public static class ChainTemplateLibrary
{
    /// <summary>내장 대표 템플릿.</summary>
    public static IReadOnlyList<ChainTemplate> All { get; } = new[]
    {
        new ChainTemplate("sf4", "🚀", "시네마틱 SF 4컷 체인",
            "네온 우주선·홀로그램 인테리어를 4컷 연속 I2V 로 확장", 4,
            "epic sci-fi spaceship interior, neon holograms, volumetric light, cinematic, 8k", "zoom-in", "#7c5cff"),
        new ChainTemplate("ad3", "🛍️", "상업 광고 줌 3컷",
            "프리미엄 제품 광고 매크로 줌 3컷", 3,
            "premium product commercial, glossy studio lighting, macro detail, luxury, 4k", "zoom-in", "#f5c451"),
        new ChainTemplate("gyro3", "🎥", "자이로 모션 카메라 믹스",
            "핸드헬드 자이로 도심 야경 모션 3컷", 3,
            "dynamic handheld gyro camera, urban night, motion energy, cinematic", "pan-right", "#22d3ee"),
    };

    public static ChainTemplate? Get(string? key) => key is null ? null : All.FirstOrDefault(t => t.Key == key);

    /// <summary>대표 레퍼런스 샘플(SVG data URL) — 템플릿 카드 썸네일/베이스 프리라이트.</summary>
    public static string SampleFor(ChainTemplate t) => WanSampleFactory.FrameDataUrl(t.Name, t.Accent, $"{t.Segments}컷 · {t.MotionPreset}");

    /// <summary>템플릿을 엔진에 배치 — <see cref="FlowExecutionPipeline.BuildChainInto"/> 후 프롬프트/모션 파라미터 주입.</summary>
    public static void Apply(ChainTemplate t, NodeGraphEngine engine, FlowExecutionPipeline pipeline, StoryboardProject? project)
    {
        if (t is null || engine is null || pipeline is null) return;
        pipeline.BuildChainInto(engine, t.Segments, project);
        foreach (var n in engine.Nodes)
        {
            if (n.Kind == "prompt") n.Param = t.PromptText;
            else if (n.Kind == "motion") n.Param = t.MotionPreset;
        }
    }
}
