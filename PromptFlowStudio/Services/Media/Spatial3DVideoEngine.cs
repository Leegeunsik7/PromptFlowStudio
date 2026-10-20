using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PromptFlowStudio.Services.Timeline;   // MultiTrackTimelineState · TimelineTrackKind · TimelineClip

namespace PromptFlowStudio.Services.Media;

/// <summary>공간 3D 변환된 클립 1개 — 깊이 레이어 + SBS 시차 + Binaural 방위.</summary>
public sealed record SpatialClip(
    int Index, string Label, double DurationSec, int DepthLayers, double IpdPx, double BinauralAzimuthDeg, string DepthProfile);

/// <summary>공간 3D 변환 계획 — 클립 목록 + SBS 프리뷰 SVG.</summary>
public sealed record SpatialPlan(bool Ok, IReadOnlyList<SpatialClip> Clips, string PreviewSvgDataUrl, string Message);

/// <summary>
/// [PromptFlow Studio v8.0.0] 공간 3D 비디오 &amp; Binaural 오디오 엔진(관찰 가능 싱글턴 · 순수 BCL · 외부 3D DLL 0).
///   ▸ 2D 비디오 클립을 깊이 지도(Depth Map) 벡터 연산으로 Side-by-Side(SBS) 스테레오스코픽 레이아웃(Vision Pro / Quest 호환)으로 변환.
///   ▸ 겹치는 자이로 모션 궤적에 반응하는 Binaural 3D 오디오 방위(azimuth)를 매핑.
///   ▸ 순수 수학 연산(BCL) + SVG 프리뷰. 실제 뎁스 추정 결선 시 <see cref="EstimateDepthLayers"/> 만 교체.
/// </summary>
public sealed class Spatial3DVideoEngine
{
    private const double BaselineIpdPx = 63;   // 평균 동공 간 거리 근사(스테레오 시차 기준)

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    public long TranscodedClips { get; private set; }
    public SpatialPlan? LastPlan { get; private set; }

    /// <summary>타임라인 비디오 트랙 → 공간 3D 변환 계획(깊이·SBS 시차·Binaural 방위) + SBS 프리뷰.</summary>
    public SpatialPlan Transcode(MultiTrackTimelineState state)
    {
        if (state is null) return Fail("타임라인이 없습니다.");
        var videos = state.ClipsFor(TimelineTrackKind.Video).OrderBy(c => c.StartSec).ToList();
        if (videos.Count == 0) return Fail("비디오 컷이 없습니다 — 먼저 컷을 배치하세요.");
        var gyros = state.ClipsFor(TimelineTrackKind.Gyro);
        var prompts = state.ClipsFor(TimelineTrackKind.Prompt);

        var clips = new List<SpatialClip>(videos.Count);
        for (var i = 0; i < videos.Count; i++)
        {
            var v = videos[i];
            var promptText = string.Join(" ", prompts.Where(p => Overlaps(p, v)).Select(p => p.Label));
            var layers = EstimateDepthLayers(promptText);
            var gy = gyros.FirstOrDefault(g => Overlaps(g, v));
            var az = BinauralAzimuth(gy?.Label);
            // 깊이감이 클수록(레이어↑) 시차(IPD)도 소폭 강화.
            var ipd = BaselineIpdPx * (0.85 + layers * 0.06);
            clips.Add(new SpatialClip(i + 1, v.Label, v.DurationSec, layers, Math.Round(ipd, 1), az, DepthProfile(layers)));
        }

        TranscodedClips += clips.Count;
        var plan = new SpatialPlan(true, clips, BuildSbsPreview(clips), $"공간 3D 변환 — {clips.Count}컷 · SBS 스테레오 + Binaural");
        LastPlan = plan;
        Notify();
        return plan;
    }

    /// <summary>깊이 레이어 수 추정(프롬프트 풍부도 휴리스틱 · 3~7). 결선 지점: 실제 MiDaS/뎁스넷으로 교체.</summary>
    public static int EstimateDepthLayers(string? promptText)
    {
        var t = (promptText ?? "").ToLowerInvariant();
        var score = 3;
        if (t.Contains("wide") || t.Contains("aerial") || t.Contains("landscape") || t.Contains("epic")) score += 2;   // 원경 깊이
        if (t.Contains("close") || t.Contains("macro") || t.Contains("portrait")) score += 1;                          // 전경 분리
        if (t.Contains("volumetric") || t.Contains("fog") || t.Contains("depth")) score += 1;
        return Math.Clamp(score, 3, 7);
    }

    /// <summary>자이로 모션 라벨 → Binaural 방위각(도 · 좌 -, 우 +).</summary>
    public static double BinauralAzimuth(string? gyroLabel)
    {
        var l = gyroLabel ?? "";
        if (l.Contains("오빗")) return 45;      // 궤도 회전 → 우측 스윕
        if (l.Contains("푸시인") || l.Contains("핸드헬드")) return 0;   // 정면 접근
        if (l.Contains("팬업") || l.Contains("부양")) return 20;
        if (l.Contains("풀아웃")) return -30;   // 후퇴 → 좌측 확산
        return 0;
    }

    private static string DepthProfile(int layers) => layers >= 6 ? "원경 깊음" : layers >= 4 ? "중간 깊이" : "얕은 입체";

    /// <summary>SBS(좌/우 눈) 프리뷰 SVG DataURL — 시차만큼 어긋난 두 프레임(외부 에셋 0).</summary>
    private static string BuildSbsPreview(IReadOnlyList<SpatialClip> clips)
    {
        var ci = CultureInfo.InvariantCulture;
        var avgIpd = clips.Count == 0 ? BaselineIpdPx : clips.Average(c => c.IpdPx);
        var shift = Math.Clamp(avgIpd / 20.0, 1, 6);   // 프리뷰용 축소 시차
        var sb = new StringBuilder(320);
        sb.Append("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 200 100'>");
        sb.Append("<rect width='200' height='100' fill='#06080e'/>");
        // 좌/우 눈 프레임(각각 100폭) + 시차 오프셋 사각형.
        for (var eye = 0; eye < 2; eye++)
        {
            var ox = eye * 100 + (eye == 0 ? shift : -shift);
            sb.Append("<rect x='").Append((eye * 100 + 4).ToString("0.#", ci)).Append("' y='6' width='92' height='88' rx='6' fill='none' stroke='#1e2d3d'/>");
            sb.Append("<rect x='").Append((ox + 30).ToString("0.#", ci)).Append("' y='34' width='36' height='36' rx='4' fill='")
              .Append(eye == 0 ? "#22d3ee" : "#ec4899").Append("' opacity='0.6'/>");
            sb.Append("<text x='").Append((eye * 100 + 8).ToString("0.#", ci)).Append("' y='20' fill='#7dd3fc' font-size='8'>")
              .Append(eye == 0 ? "L" : "R").Append("</text>");
        }
        sb.Append("</svg>");
        return "data:image/svg+xml," + Uri.EscapeDataString(sb.ToString());
    }

    private SpatialPlan Fail(string msg) { var p = new SpatialPlan(false, Array.Empty<SpatialClip>(), "", msg); LastPlan = p; Notify(); return p; }
    private static bool Overlaps(TimelineClip a, TimelineClip b) => a.StartSec < b.EndSec && a.EndSec > b.StartSec;
}
