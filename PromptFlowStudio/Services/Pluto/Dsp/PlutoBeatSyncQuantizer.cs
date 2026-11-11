using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto.Dsp;

/// <summary>🧲 비트 동기 양자화 계약 — 모션 막 경계 · SFX 큐를 BGM 비트에 자석 스냅.</summary>
public interface IPlutoBeatSyncQuantizer
{
    PlutoMotionTrajectory QuantizeTrajectory(PlutoMotionTrajectory baseTrajectory, IReadOnlyList<double> beats);
    IReadOnlyList<PlutoSfxCue> QuantizeSfxCues(IReadOnlyList<PlutoSfxCue> baseCues, IReadOnlyList<double> beats);
}

/// <summary>
/// 🧲 리듬 모션 · SFX 양자화기 — 허용창 안의 최근접 비트로만 스냅(창 밖 = 원래 시각 유지).
///   ▸ 모션: 막 경계(공칭 4 · 8 · 12s) ±<see cref="MotionSnapToleranceSec"/> · 총 길이(15.0s) 불변 · <see cref="PlutoMotionCompiler.RetimeBoundaries"/>로 C¹ 연속 보존.
///   ▸ SFX: 큐 시작(셔터 0.8 · 차임 5.5 · 벨 13.2s) ±<see cref="SfxSnapToleranceSec"/>.
/// 상태 없음(스레드 안전).
/// </summary>
public sealed class PlutoBeatSyncQuantizer : IPlutoBeatSyncQuantizer
{
    public static readonly PlutoBeatSyncQuantizer Shared = new();

    public const double MotionSnapToleranceSec = 0.25, SfxSnapToleranceSec = 0.35;

    public PlutoMotionTrajectory QuantizeTrajectory(PlutoMotionTrajectory baseTrajectory, IReadOnlyList<double> beats)
    {
        if (baseTrajectory?.Segments is not { Count: > 1 } segs || beats is null || beats.Count == 0) return baseTrajectory ?? PlutoMotionTrajectory.Empty;
        Span<double> bounds = segs.Count <= 64 ? stackalloc double[segs.Count - 1] : new double[segs.Count - 1];
        var changed = false;
        for (var i = 0; i < bounds.Length; i++)
        {
            var t = segs[i]?.EndTimeSec ?? double.NaN;
            if (!double.IsFinite(t)) return baseTrajectory;
            bounds[i] = TrySnap(t, beats, MotionSnapToleranceSec, out var s) ? s : t;
            changed |= bounds[i] != t;
        }
        return changed ? PlutoMotionCompiler.RetimeBoundaries(baseTrajectory, bounds) : baseTrajectory;
    }

    public IReadOnlyList<PlutoSfxCue> QuantizeSfxCues(IReadOnlyList<PlutoSfxCue> baseCues, IReadOnlyList<double> beats)
    {
        if (baseCues is null || baseCues.Count == 0) return Array.Empty<PlutoSfxCue>();
        if (beats is null || beats.Count == 0) return baseCues;
        var outCues = new PlutoSfxCue[baseCues.Count];
        for (var i = 0; i < outCues.Length; i++)
        {
            var c = baseCues[i];
            outCues[i] = TrySnap(c.OffsetMs / 1000.0, beats, SfxSnapToleranceSec, out var s)
                ? c with { OffsetMs = Math.Max(0, (int)Math.Round(s * 1000.0, MidpointRounding.AwayFromZero)) }
                : c;
        }
        return outCues;
    }

    /// <summary>|beat − t| ≤ tol 인 최근접 비트(동률 → 이른 비트). 비유한 비트는 무시.</summary>
    public static bool TrySnap(double t, IReadOnlyList<double> beats, double toleranceSec, out double snapped)
    {
        snapped = t;
        if (beats is null || !double.IsFinite(t)) return false;
        var best = double.PositiveInfinity;
        for (var i = 0; i < beats.Count; i++)
        {
            var b = beats[i];
            if (!double.IsFinite(b)) continue;
            var d = Math.Abs(b - t);
            if (d <= toleranceSec + 1e-9 && d < best) { best = d; snapped = b; }
        }
        return !double.IsPositiveInfinity(best);
    }
}
