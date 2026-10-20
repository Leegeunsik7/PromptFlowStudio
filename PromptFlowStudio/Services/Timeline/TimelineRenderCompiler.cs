using System;
using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Wan;   // WanRenderClient · WanPromptBuild · WanRenderRequest

namespace PromptFlowStudio.Services.Timeline;

/// <summary>컴파일된 세그먼트 1개(표시용) — 비디오 컷 기준 4트랙 병합 결과.</summary>
public sealed record CompiledSegment(
    int Index, double StartSec, double DurationSec, string Prompt, bool HasVocal, string? GyroMotion, int MotionIntensity);

/// <summary>타임라인 컴파일 결과 — 세그먼트 목록 + GPU 그리드 이관용 요청 배열.</summary>
public sealed record TimelineCompileResult(
    bool Ok, IReadOnlyList<WanRenderRequest> Requests, IReadOnlyList<CompiledSegment> Segments, string Message);

/// <summary>
/// [PromptFlow Studio v7.3.0] 타임라인 렌더 컴파일러(순수 BCL · 외부 NuGet 0).
///   ▸ <b>비디오 트랙을 스파인(spine)</b>으로 삼아, 각 비디오 컷의 타임코드에 겹치는 보컬/자이로/프롬프트 클립을 감지·병합(Flattening)하여
///     하나의 멱등 렌더 페이로드(<see cref="WanRenderRequest"/>)로 압축한다.
///   ▸ 결과 배열은 v7.0.3 <see cref="Inference.HybridNodeMeshScheduler"/> 또는 v6.0.1 <see cref="DistributedRenderGrid"/> 로 직접 이관 가능.
///   ▸ 100% 결정적(같은 타임라인 → 같은 요청) · 예외 없이 결과 반환(Graceful).
/// </summary>
public sealed class TimelineRenderCompiler
{
    /// <summary>타임라인 4트랙 → 비디오 컷 기준 병합 세그먼트 + WanRenderRequest 배열로 컴파일.</summary>
    public TimelineCompileResult Compile(MultiTrackTimelineState state)
    {
        if (state is null) return new(false, Array.Empty<WanRenderRequest>(), Array.Empty<CompiledSegment>(), "타임라인이 없습니다.");

        var videos = state.ClipsFor(TimelineTrackKind.Video).OrderBy(c => c.StartSec).ThenBy(c => c.Id).ToList();
        if (videos.Count == 0)
            return new(false, Array.Empty<WanRenderRequest>(), Array.Empty<CompiledSegment>(), "비디오 컷이 없습니다 — 먼저 컷을 배치하세요.");

        var prompts = state.ClipsFor(TimelineTrackKind.Prompt);
        var vocals = state.ClipsFor(TimelineTrackKind.Vocal);
        var gyros = state.ClipsFor(TimelineTrackKind.Gyro);

        var segs = new List<CompiledSegment>(videos.Count);
        var reqs = new List<WanRenderRequest>(videos.Count);

        for (var i = 0; i < videos.Count; i++)
        {
            var v = videos[i];

            // ── 오버랩 감지 & 병합 ──
            var parts = new List<string>();
            foreach (var p in prompts.Where(p => Overlaps(p, v)).OrderBy(p => p.StartSec))
                if (!string.IsNullOrWhiteSpace(p.Label)) parts.Add(p.Label.Trim());

            var gy = gyros.Where(g => Overlaps(g, v)).OrderByDescending(g => OverlapSec(g, v)).FirstOrDefault();
            if (gy is not null) parts.Add(GyroPhrase(gy.Label));

            var hasVocal = vocals.Any(a => Overlaps(a, v));
            if (hasVocal) parts.Add("clear lip-sync, expressive speaking");

            if (parts.Count == 0) parts.Add("cinematic, natural light, steady");

            var prompt = string.Join(", ", parts.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
            var intensity = gy is not null ? 60 : 25;

            reqs.Add(WanRenderClient.BuildRequest(new WanPromptBuild(prompt, intensity, Array.Empty<string>(), null), null));
            segs.Add(new CompiledSegment(i + 1, v.StartSec, v.DurationSec, prompt, hasVocal, gy?.Label, intensity));
        }

        return new(true, reqs, segs, $"{reqs.Count}세그먼트 컴파일 완료(4트랙 시간순 병합)");
    }

    /// <summary>두 클립의 타임코드 겹침 여부.</summary>
    public static bool Overlaps(TimelineClip a, TimelineClip b) => a.StartSec < b.EndSec && a.EndSec > b.StartSec;

    /// <summary>겹침 구간 길이(초) — 지배 자이로 선택용.</summary>
    private static double OverlapSec(TimelineClip a, TimelineClip b)
        => Math.Max(0, Math.Min(a.EndSec, b.EndSec) - Math.Max(a.StartSec, b.StartSec));

    /// <summary>자이로 모션 라벨(한국어) → Wan 카메라 영문 문구.</summary>
    private static string GyroPhrase(string? label)
    {
        var l = label ?? "";
        if (l.Contains("오빗")) return "orbiting sweep camera movement";
        if (l.Contains("푸시인") || l.Contains("핸드헬드")) return "handheld push-in, subtle shake";
        if (l.Contains("팬업") || l.Contains("부양")) return "upward tilt pan, floating rise";
        if (l.Contains("풀아웃")) return "slow dolly pull-out";
        return "dynamic camera movement";
    }
}
