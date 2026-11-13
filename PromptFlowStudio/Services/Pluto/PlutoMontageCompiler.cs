using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🎞️ 몽타주 클립 1개 — 길이(초) · 다음 클립으로의 xfade(초 · 마지막 0) · 카메라 궤적 · 색보정 · <c>[i:v]…[vi]</c> 체인 본문.</summary>
public sealed record PlutoMontageClipSpec(int Index, string ImagePath, double DurationSec, double TransitionOutSec, string TransitionType,
    PlutoMotionTrajectory Trajectory, PlutoColorGradeProfile ColorProfile, string VideoFilterChain)
{
    /// <summary>출력 타임라인 기준 클립 시작(= 직전 xfade offset · 첫 클립 0).</summary>
    public double StartSec { get; init; }
    /// <summary>zoompan 출력 프레임 수(= 길이 × fps · 정수).</summary>
    public int Frames { get; init; }
}

/// <summary>🎞️ 멀티컷 몽타주 플랜 — <see cref="FilterComplexGraph"/> 는 <c>-filter_complex</c> 값 그대로 · 최종 비디오 패드 <see cref="FinalVideoPadLabel"/>.</summary>
public sealed record PlutoMontagePlan(IReadOnlyList<PlutoMontageClipSpec> Clips, double TotalDurationSec, string FilterComplexGraph, string FinalVideoPadLabel)
{
    /// <summary>xfade 길이(초).</summary>
    public double TransitionSec { get; init; }
    /// <summary>내부 컷 시각(초) = xfade 중앙(블렌드 50%) — 비트 스냅 기준점.</summary>
    public IReadOnlyList<double> CutPointsSec { get; init; } = Array.Empty<double>();
    /// <summary>xfade offset(초 · 출력 타임라인) — 순증가.</summary>
    public IReadOnlyList<double> TransitionOffsetsSec { get; init; } = Array.Empty<double>();
    /// <summary>비트에 스냅된 컷 수.</summary>
    public int SnappedCuts { get; init; }
    public int Fps { get; init; }
}

/// <summary>🎞️ 2~5장 사진 → 비트 양자화 15초 몽타주 <c>filter_complex</c>.</summary>
public interface IPlutoMontageCompiler
{
    PlutoMontagePlan CompileMontagePlan(IReadOnlyList<string> imagePaths, IReadOnlyList<(double X, double Y)>? focalPoints, IReadOnlyList<double>? beatTimestampsSec,
        IReadOnlyList<PlutoColorGradeProfile>? colorProfiles, double totalDurationSec = 15.0, int width = 1080, int height = 1920, int fps = 60);
}

/// <summary>
/// 🎞️ Pluto 멀티컷 몽타주 컴파일러 — 컷 중앙 c_i 를 기준으로 클립 길이를 역산해 ΣT_i − (N−1)·d = 총 길이를 구성상 정확히 보장.
///   ▸ T_0 = c_1 + d/2 · T_i = c_{i+1} − c_i + d · T_{N−1} = 총 − c_{N−1} + d/2 (모두 프레임 그리드 정렬).
///   ▸ 컷 c_i 는 ±<see cref="BeatSnapToleranceSec"/> 내 최근접 비트로 스냅(최소 클립 <see cref="MinClipSec"/> 위반 시 스냅 생략).
///   ▸ 카메라: Push-In → Gentle Drift → Pull-Back 순환(<see cref="PlutoMotionCompiler"/> 슈퍼샘플 zoompan) + 클립별 색보정 → xfade 체인 → <c>[vout]</c>.
/// </summary>
public sealed class PlutoMontageCompiler : IPlutoMontageCompiler
{
    public static readonly PlutoMontageCompiler Shared = new();

    public const int MinClips = 2, MaxClips = 5;
    public const double DefaultTransitionSec = 0.35, BeatSnapToleranceSec = 0.25, MinClipSec = 1.8;
    public const string FinalPad = "vout";
    /// <summary>xfade 전환 순환(부드러운 감성 전환 위주).</summary>
    public static readonly string[] TransitionCycle = { "fade", "smoothleft", "dissolve", "circleopen" };

    public enum KineticVector : byte { PushIn, GentleDrift, PullBack }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static KineticVector VectorFor(int index) => (KineticVector)(index % 3);

    /// <summary>🎥 클립 궤적 — Push-In 1.00→1.18 · Gentle Drift 1.10→1.12(초점 ±4% 수평 이동) · Pull-Back 1.20→1.02(중앙 25% 복귀).</summary>
    public static PlutoMotionTrajectory BuildClipTrajectory(KineticVector vector, (double X, double Y) focal, double durationSec)
    {
        var f = PlutoMotionCompiler.NormalizeFocal(focal, PlutoMotionCompiler.BabyFocal);
        var d = double.IsFinite(durationSec) && durationSec > 0 ? durationSec : 1.0;
        return vector switch
        {
            KineticVector.GentleDrift => new PlutoMotionTrajectory
            {
                OriginFocal = (Math.Clamp(f.X - 0.04, 0, 1), f.Y),
                Segments = new[] { new PlutoMotionSegment { StartTimeSec = 0, EndTimeSec = d, StartZoom = 1.10, EndZoom = 1.12, FocalCenter = (Math.Clamp(f.X + 0.04, 0, 1), f.Y), EasingFunction = PlutoEasingFunction.HermiteSmootherstep } },
            },
            KineticVector.PullBack => new PlutoMotionTrajectory
            {
                OriginFocal = f,
                Segments = new[] { new PlutoMotionSegment { StartTimeSec = 0, EndTimeSec = d, StartZoom = 1.20, EndZoom = 1.02, FocalCenter = (f.X + (0.5 - f.X) * PlutoMotionCompiler.PullBackRecenter, f.Y + (0.5 - f.Y) * PlutoMotionCompiler.PullBackRecenter) } },
            },
            _ => new PlutoMotionTrajectory
            {
                OriginFocal = f,
                Segments = new[] { new PlutoMotionSegment { StartTimeSec = 0, EndTimeSec = d, StartZoom = 1.00, EndZoom = 1.18, FocalCenter = f } },
            },
        };
    }

    public PlutoMontagePlan CompileMontagePlan(IReadOnlyList<string> imagePaths, IReadOnlyList<(double X, double Y)>? focalPoints, IReadOnlyList<double>? beatTimestampsSec,
        IReadOnlyList<PlutoColorGradeProfile>? colorProfiles, double totalDurationSec = 15.0, int width = 1080, int height = 1920, int fps = 60)
    {
        if (imagePaths is null || imagePaths.Count < MinClips || imagePaths.Count > MaxClips)
            throw new ArgumentException("몽타주는 사진 2~5장이 필요합니다.", nameof(imagePaths));
        for (var i = 0; i < imagePaths.Count; i++)
            if (string.IsNullOrWhiteSpace(imagePaths[i])) throw new ArgumentException("빈 사진 경로(" + i.ToString(Inv) + ")", nameof(imagePaths));
        if (!double.IsFinite(totalDurationSec) || totalDurationSec <= 0) throw new ArgumentOutOfRangeException(nameof(totalDurationSec));
        fps = Math.Clamp(fps, 1, 240);
        var n = imagePaths.Count;
        var total = totalDurationSec;

        // ── 전환 길이 d(프레임 정렬) · 균등 클립 길이 · 최소 길이(짧은 총 길이에서도 실현 가능하도록 완화) ──
        var tEq0 = (total + (n - 1) * DefaultTransitionSec) / n;
        var d = Snap(Math.Clamp(Math.Min(DefaultTransitionSec, tEq0 * 0.25), 1.0 / fps, DefaultTransitionSec), fps);
        var tEq = (total + (n - 1) * d) / n;
        var minClip = Math.Min(MinClipSec, tEq * 0.9);

        // ── 컷 중앙 c_1..c_{N-1}(균등) → 비트 스냅 ──
        var cuts = new double[n - 1];
        for (var i = 0; i < cuts.Length; i++) cuts[i] = tEq - d / 2 + i * (tEq - d);
        var snapped = 0;
        if (beatTimestampsSec is { Count: > 0 } beats)
            for (var i = 0; i < cuts.Length; i++)
            {
                var best = double.NaN;
                var bestDist = BeatSnapToleranceSec + 1e-9;
                for (var b = 0; b < beats.Count; b++)
                {
                    var bt = beats[b];
                    if (!double.IsFinite(bt)) continue;
                    var dist = Math.Abs(bt - cuts[i]);
                    if (dist <= bestDist) { bestDist = dist; best = bt; }
                }
                if (double.IsNaN(best)) continue;
                var prev = i == 0 ? double.NaN : cuts[i - 1];
                var next = i == cuts.Length - 1 ? double.NaN : cuts[i + 1];
                if (ClipLen(prev, best, total, d) >= minClip - 1e-9 && ClipLen(best, next, total, d) >= minClip - 1e-9)
                {
                    cuts[i] = best;
                    snapped++;
                }
            }

        // ── 프레임 그리드 정렬(xfade offset = c − d/2 를 프레임 경계로) → 클립 길이 역산 ──
        var offsets = new double[n - 1];
        for (var i = 0; i < offsets.Length; i++)
        {
            offsets[i] = Snap(cuts[i] - d / 2, fps);
            cuts[i] = offsets[i] + d / 2;
        }
        var durations = new double[n];
        for (var i = 0; i < n; i++)
        {
            var start = i == 0 ? 0.0 : offsets[i - 1];
            var end = i == n - 1 ? total : offsets[i] + d;
            durations[i] = end - start;
        }

        // ── 클립별 체인 [i:v] zoompan → 색보정 → yuv420p · PTS 리셋 [vi] ──
        var uhd = width >= 2160;
        var ss = uhd ? 2 : PlutoMotionCompiler.DefaultSupersample;
        var clips = new PlutoMontageClipSpec[n];
        var sb = new StringBuilder(4096 * n);
        for (var i = 0; i < n; i++)
        {
            var focal = focalPoints is not null && i < focalPoints.Count ? focalPoints[i] : PlutoMotionCompiler.BabyFocal;
            var traj = BuildClipTrajectory(VectorFor(i), focal, durations[i]);
            var grade = colorProfiles is not null && i < colorProfiles.Count && colorProfiles[i] is { } cp ? cp : PlutoColorGradingCompiler.NaturalPastel;
            var frames = (int)Math.Round(durations[i] * fps, MidpointRounding.AwayFromZero);
            var chain = PlutoMotionCompiler.CompileFfmpegZoomPanFilter(traj, width, height, fps, ss) + "," + PlutoColorGradingCompiler.Compile(grade)
                        + ",format=yuv420p,setpts=PTS-STARTPTS";
            var ii = i.ToString(Inv);
            if (i > 0) sb.Append(';');
            sb.Append('[').Append(ii).Append(":v]").Append(chain).Append("[v").Append(ii).Append(']');
            clips[i] = new PlutoMontageClipSpec(i, imagePaths[i], durations[i], i < n - 1 ? d : 0.0, i < n - 1 ? TransitionCycle[i % TransitionCycle.Length] : "none", traj, grade, chain)
            {
                StartSec = i == 0 ? 0.0 : offsets[i - 1],
                Frames = frames,
            };
        }

        // ── xfade 체인 [v0][v1]→[vx1] … → [vout] ──
        var prevLabel = "v0";
        for (var i = 1; i < n; i++)
        {
            var outLabel = i == n - 1 ? FinalPad : "vx" + i.ToString(Inv);
            sb.Append(";[").Append(prevLabel).Append("][v").Append(i.ToString(Inv)).Append("]xfade=transition=").Append(clips[i - 1].TransitionType)
              .Append(":duration=").Append(d.ToString("0.000", Inv))
              .Append(":offset=").Append(offsets[i - 1].ToString("0.000", Inv))
              .Append('[').Append(outLabel).Append(']');
            prevLabel = outLabel;
        }

        return new PlutoMontagePlan(clips, total, sb.ToString(), FinalPad)
        {
            TransitionSec = d,
            CutPointsSec = cuts,
            TransitionOffsetsSec = offsets,
            SnappedCuts = snapped,
            Fps = fps,
        };
    }

    /// <summary>클립 길이(컷 중앙 좌표계) — 좌/우 컷이 NaN 이면 타임라인 시작/끝.</summary>
    private static double ClipLen(double leftCut, double rightCut, double total, double d)
    {
        var start = double.IsNaN(leftCut) ? 0.0 : leftCut - d / 2;
        var end = double.IsNaN(rightCut) ? total : rightCut + d / 2;
        return end - start;
    }

    private static double Snap(double sec, int fps) => Math.Round(sec * fps, MidpointRounding.AwayFromZero) / fps;
}
