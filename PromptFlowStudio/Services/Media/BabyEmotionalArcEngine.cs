using System;
using System.Collections.Generic;
using System.Globalization;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Media;

/// <summary>아기 감정 5단계 마이크로 아크.</summary>
public enum EmotionStage { Wonder, PureJoy, Giggling, Dreamy, Peaceful }

/// <summary>아크 1지점 — 컷 인덱스/단계/위치/긴장도 + FFmpeg 연출 파라미터(줌속도·렌즈블러·조명펄스) + 색/라벨.</summary>
public sealed record ArcPoint(int Index, EmotionStage Stage, double Pos, int Tension,
    double ZoomSpeed, double GblurSigma, double LightPulseHz, string Hex, string Label);

/// <summary>
/// [PromptFlowStudio] v3.7.0 5단계 아기 감정 마이크로 아크 엔진(Task 2 · 순수 BCL → FFmpeg 파라미터).
///   ▸ 컷 흐름을 <b>Wonder(호기심) → PureJoy(환희) → Giggling(까르르) → Dreamy(몽글) → Peaceful(안식)</b> 5단계로 매핑.
///   ▸ 단계마다 <b>카메라 줌속도</b>(zoompan)·<b>시네마틱 렌즈 블러</b>(gblur)·<b>조명 펄스 파형</b>(eq brightness sin)을 자동 산출.
///   ▸ Giggling 은 가장 빠른 줌 + 강한 조명 펄스, Dreamy 는 강한 소프트 포커스, Peaceful 은 가장 느린 안정 연출.
/// </summary>
public static class BabyEmotionalArcEngine
{
    /// <summary>감정 진행 순서(5단계).</summary>
    public static readonly IReadOnlyList<EmotionStage> Order = new[]
    {
        EmotionStage.Wonder, EmotionStage.PureJoy, EmotionStage.Giggling, EmotionStage.Dreamy, EmotionStage.Peaceful,
    };

    /// <summary>단계 한글 라벨.</summary>
    public static string Label(EmotionStage s) => s switch
    {
        EmotionStage.Wonder => "호기심",
        EmotionStage.PureJoy => "환희",
        EmotionStage.Giggling => "까르르",
        EmotionStage.Dreamy => "몽글몽글",
        _ => "안식",
    };

    /// <summary>단계 색상 HEX(감정 텐션 그래프 · 조명 틴트).</summary>
    public static string Hex(EmotionStage s) => s switch
    {
        EmotionStage.Wonder => "#7dd3fc",
        EmotionStage.PureJoy => "#fbbf24",
        EmotionStage.Giggling => "#f472b6",
        EmotionStage.Dreamy => "#c4b5fd",
        _ => "#86efac",
    };

    // 단계별 연출 파라미터: (줌 증가량/프레임, gblur sigma, 조명 펄스 Hz, 긴장도).
    private static (double Zoom, double Blur, double Pulse, int Tension) Params(EmotionStage s) => s switch
    {
        EmotionStage.Wonder => (0.0009, 0.6, 0.25, 30),
        EmotionStage.PureJoy => (0.0015, 0.3, 0.60, 62),
        EmotionStage.Giggling => (0.0022, 0.2, 1.10, 88),
        EmotionStage.Dreamy => (0.0011, 1.6, 0.35, 54),
        _ => (0.0006, 0.9, 0.15, 24),   // Peaceful
    };

    /// <summary>정규화 위치(0~1) → 감정 단계(5등분).</summary>
    public static EmotionStage StageForPos(double pos)
    {
        var p = pos < 0 ? 0 : pos > 1 ? 1 : pos;
        var idx = (int)(p * Order.Count);
        if (idx >= Order.Count) idx = Order.Count - 1;
        return Order[idx];
    }

    /// <summary>컷 수에 맞춰 감정 아크(컷별 <see cref="ArcPoint"/>)를 연산한다.</summary>
    public static IReadOnlyList<ArcPoint> Arc(int cutCount)
    {
        var n = Math.Max(1, cutCount);
        var list = new List<ArcPoint>(n);
        for (var i = 0; i < n; i++)
        {
            var pos = n == 1 ? 0.0 : i / (double)(n - 1);
            var stage = StageForPos(pos);
            var (z, b, pulse, t) = Params(stage);
            list.Add(new ArcPoint(i, stage, pos, t, z, b, pulse, Hex(stage), Label(stage)));
        }
        return list;
    }

    // ══════════════════════ FFmpeg 필터 조립 ══════════════════════

    private static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);

    /// <summary>단계 줌속도 → zoompan(느린 시네마틱 줌 · 상한 1.5배 · 중앙 고정).</summary>
    public static string BuildZoompanFilter(double zoomSpeed, int w, int h, int frames)
    {
        var zs = F(Math.Clamp(zoomSpeed, 0.0001, 0.01));
        var d = Math.Max(1, frames);
        return $"zoompan=z='min(zoom+{zs},1.5)':d={d}:x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':s={w}x{h}:fps=25";
    }

    /// <summary>렌즈 블러(시네마틱 소프트 포커스). sigma≤0.05 면 빈 문자열(무효).</summary>
    public static string BuildGblurFilter(double sigma)
        => sigma <= 0.05 ? "" : $"gblur=sigma={F(Math.Clamp(sigma, 0.06, 6.0))}";

    /// <summary>조명 펄스 파형 — eq brightness 를 sin 파형으로 미세 진동(마이크로 텐션 라이팅).</summary>
    public static string BuildLightPulseFilter(double hz, double amplitude = 0.06)
    {
        var f = F(Math.Clamp(hz, 0.0, 4.0));
        var a = F(Math.Clamp(amplitude, 0.0, 0.2));
        return $"eq=brightness='{a}*sin(2*PI*{f}*t)'";
    }

    /// <summary>아크 1지점 → 단일 클립 vf 체인(zoompan + 선택적 gblur + 조명 펄스).</summary>
    public static string BuildStageFilterChain(ArcPoint p, int w, int h, int frames)
    {
        var parts = new List<string> { BuildZoompanFilter(p.ZoomSpeed, w, h, frames) };
        var blur = BuildGblurFilter(p.GblurSigma);
        if (blur.Length > 0) parts.Add(blur);
        parts.Add(BuildLightPulseFilter(p.LightPulseHz));
        return string.Join(",", parts);
    }

    /// <summary>프로젝트 컷의 긴장도(PresetTension)를 감정 아크로 덮어써 감정선 BGM/연출과 정합시킨다(적용 컷 수 반환).</summary>
    public static int ApplyArc(StoryboardProject project)
    {
        if (project is null || project.Scenes.Count == 0) return 0;
        var arc = Arc(project.Scenes.Count);
        for (var i = 0; i < project.Scenes.Count; i++)
            project.Scenes[i].PresetTension = arc[i].Tension;
        return arc.Count;
    }
}
