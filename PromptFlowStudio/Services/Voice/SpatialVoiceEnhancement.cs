using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PromptFlowStudio.Services.Media;        // SpatialPoint
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject
using PromptFlowStudio.Services.Trade;        // SpecSheetPdfExporter (순수 BCL PDF)
using PromptFlowStudio.Services.Wan;          // HybridRenderPlan

namespace PromptFlowStudio.Services.Voice;

/// <summary>키네틱 자막 1큐 — 텍스트/타임코드 + 파형 피크(0..1)에 반응한 폰트 크기·상승 오프셋.</summary>
public sealed record KineticSubtitleCue(string Text, int StartMs, int EndMs, double Peak, int FontSize, int YOffsetPx);

/// <summary>
/// [PromptFlowStudio] v3.8.0 자율 발상 ① — 나레이션 파형 피크에 반응하는 <b>키네틱 타이포그래피 자막 생성기</b>(순수 BCL).
///   ▸ 감정 나레이션 세그먼트 + 컷 긴장도(파형 피크 근사)로 <b>피크가 높을수록 자막이 커지고 위로 팝</b>하는 drawtext 큐를 산출.
/// </summary>
public static class KineticTypographySubtitleBuilder
{
    /// <summary>세그먼트 인덱스 + 긴장도 → 파형 피크 근사(0..1 · 약한 진동 합성).</summary>
    public static double PeakFor(int index, int tension)
    {
        var t = Math.Clamp(tension, 0, 100) / 100.0;
        var wobble = 0.15 * Math.Abs(Math.Sin(index * 1.3 + 0.5));
        return Math.Clamp(t * 0.85 + wobble, 0.0, 1.0);
    }

    /// <summary>피크 → 폰트 크기(기본 크기의 1.0~1.6배).</summary>
    public static int FontSizeFor(double peak, int baseSize)
        => (int)Math.Round(Math.Max(8, baseSize) * (1.0 + Math.Clamp(peak, 0.0, 1.0) * 0.6));

    /// <summary>피크 → 하단 기준 상승 오프셋(px · 피크 높을수록 위로).</summary>
    public static int YOffsetFor(double peak, int height)
        => (int)Math.Round(Math.Max(2, height) * (0.10 + Math.Clamp(peak, 0.0, 1.0) * 0.06));

    /// <summary>세그먼트(+컷 긴장도)를 키네틱 자막 큐로 변환한다.</summary>
    public static IReadOnlyList<KineticSubtitleCue> BuildCues(IReadOnlyList<VoiceSegment> segments, IReadOnlyList<int>? tensions, int width, int height)
    {
        var baseSize = Math.Max(24, width / 24);
        var list = new List<KineticSubtitleCue>(segments?.Count ?? 0);
        if (segments is null) return list;
        for (var i = 0; i < segments.Count; i++)
        {
            var tension = tensions is not null && i < tensions.Count ? tensions[i] : 60;
            var peak = PeakFor(i, tension);
            list.Add(new KineticSubtitleCue(segments[i].Text, segments[i].StartMs, segments[i].EndMs, peak,
                FontSizeFor(peak, baseSize), YOffsetFor(peak, height)));
        }
        return list;
    }

    /// <summary>키네틱 큐 → 시간 게이트 drawtext(피크 폰트 크기 + 상승 y · UTF-8 textfile 참조).</summary>
    public static string BuildDrawtextExpr(KineticSubtitleCue cue, string? escFontPath, string escTextFile)
    {
        var fontHead = string.IsNullOrEmpty(escFontPath) ? "" : $"fontfile={escFontPath}:";
        var t0 = (cue.StartMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        var t1 = (cue.EndMs / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
        return $"drawtext={fontHead}textfile={escTextFile}:fontcolor=white:fontsize={cue.FontSize}:borderw=3:bordercolor=black@0.85:" +
               $"x=(w-text_w)/2:y=h-text_h-{cue.YOffsetPx}:enable=between(t\\,{t0}\\,{t1})";
    }
}

/// <summary>
/// [PromptFlowStudio] v3.8.0 자율 발상 ② — 입체 음향/하이브리드 렌더 <b>스펙 시트 발급기</b>(순수 BCL PDF).
///   ▸ 완 하이브리드 모델·감정 보이스·컷별 3D 팬/원근 좌표를 요약한 단일 페이지 PDF 를 <see cref="SpecSheetPdfExporter"/>로 발급.
/// </summary>
public static class SpatialAudioSpecSheetExporter
{
    /// <summary>스펙 행(라벨, 값) — ASCII 안전(SpecSheet Esc 가 비ASCII 치환).</summary>
    public static IReadOnlyList<(string Label, string Value)> BuildRows(
        StoryboardProject p, HybridRenderPlan plan, VoicePersona persona, IReadOnlyList<SpatialPoint> points)
    {
        var near = points.Count(pt => pt.Depth < 0.4);
        var far = points.Count(pt => pt.Depth > 0.7);
        var avgPan = points.Count > 0 ? points.Average(pt => pt.Pan) : 0.0;
        return new List<(string, string)>
        {
            ("Project ID", p.Id),
            ("Wan Render Mode", plan.Mode),
            ("Wan Model", plan.Model.Id),
            ("Local Free (0 KRW)", plan.Model.LocalFree ? "YES" : "NO"),
            ("Detected VRAM", p.DetectedVramMb > 0 ? p.DetectedVramMb + " MB" : "n/a"),
            ("Quality Boost", string.IsNullOrEmpty(plan.EnhanceChain) ? "off" : $"on ({plan.Fps}fps minterpolate+cas+eq)"),
            ("Est. Cost", plan.CostKrw.ToString("N0", CultureInfo.InvariantCulture) + " KRW"),
            ("Voice Persona", persona.Key + " / " + persona.Tone),
            ("Voice Language", string.IsNullOrWhiteSpace(p.VoiceLanguage) ? "ko" : p.VoiceLanguage),
            ("Spatial Audio", p.SpatialAudio ? "on (binaural pan)" : "off"),
            ("Kinetic Subtitle", p.KineticSpatialSubtitle ? "on" : "off"),
            ("Cuts", p.Scenes.Count.ToString(CultureInfo.InvariantCulture)),
            ("Spatial Pan (avg)", avgPan.ToString("0.00", CultureInfo.InvariantCulture) + $"  (near {near} / far {far})"),
            ("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>스펙 시트 PDF 를 파일로 발급한다(성공 경로 · 실패 null).</summary>
    public static string? Export(StoryboardProject project, HybridRenderPlan plan, VoicePersona persona,
        IReadOnlyList<SpatialPoint> points, string outPath)
    {
        try
        {
            var rows = BuildRows(project, plan, persona, points);
            var bytes = SpecSheetPdfExporter.BuildPdf("PromptFlow Studio", "SPATIAL VOICE & WAN HYBRID - SPEC SHEET", rows);
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outPath, bytes);
            return File.Exists(outPath) ? outPath : null;
        }
        catch (Exception ex) { Program.Log("SpatialSpecSheet.Export", ex); return null; }
    }
}
