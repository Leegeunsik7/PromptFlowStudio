using System;
using System.Buffers;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Pluto.Dsp;

/// <summary>
/// 🎵 커스텀 BGM 하이라이트 클립 — <see cref="CroppedWavBytes"/> = 정확히 목표 길이(기본 15.00s) PCM16 WAV(30ms 페이드 인 · 250ms 페이드 아웃).
/// <see cref="RelativeBeatTimestampsSec"/> = 클립 0초 기준 비트(비트 검출 폴백 → 빈 목록 · <see cref="DetectedBpm"/> 0).
/// </summary>
public sealed record PlutoHighlightClipResult(byte[] CroppedWavBytes, double StartOffsetSec, double EndOffsetSec, double TotalSourceDurationSec,
    double DetectedBpm, IReadOnlyList<double> RelativeBeatTimestampsSec, double ExcitementScore);

/// <summary>🎵 전곡 WAV → 최고 흥분도 구간(코러스/클라이맥스) 추출 계약.</summary>
public interface IPlutoAudioHighlightExtractor
{
    /// <summary>자동 — 슬라이딩 윈도(0.25s) 최고 점수 구간 → ±0.5s 내 최근접 비트 스냅 → 크롭 + 페이드.</summary>
    PlutoHighlightClipResult ExtractBestHighlight(ReadOnlySpan<byte> fullWavBytes, double targetDurationSec = 15.0);
    /// <summary>수동 — 지정 시작점(트랙 경계 클램프 · 스냅 없음) 크롭 + 페이드.</summary>
    PlutoHighlightClipResult ExtractWindowAt(ReadOnlySpan<byte> fullWavBytes, double startOffsetSec, double targetDurationSec = 15.0);
}

/// <summary>
/// 🎵 순수 C# 음향 클라이맥스 추출기 — 외부 라이브러리 0.
///   ▸ 프레임 N=2048 · 홉 H=512 · Hann → <see cref="PlutoAudioBeatDetector.Fft"/> → RMS · 스펙트럴 플럭스 · 스펙트럴 센트로이드(각 최대값 정규화).
///   ▸ 합성 흥분도 <c>0.50·RMS + 0.35·Flux + 0.15·Centroid</c> → 누적합 O(1) 윈도 평균 · 25s 초과 트랙은 도입/종결 2s 경계 윈도 −0.08 페널티.
///   ▸ 최적 시작점을 <see cref="IPlutoAudioBeatDetector"/> 비트 격자 ±0.5s 내 최근접 비트로 스냅 → 정확한 목표 길이 크롭(부족분 0 패딩).
/// 전곡 PCM 을 통째로 디코드하지 않고 프레임 단위 스트리밍 디코드(대용량 곡도 O(프레임 수) 메모리) · 작업 버퍼 <see cref="ArrayPool{T}"/> 대여.
/// </summary>
public sealed class PlutoAudioHighlightExtractor : IPlutoAudioHighlightExtractor
{
    public static readonly PlutoAudioHighlightExtractor Shared = new();

    public const double DefaultDurationSec = 15.0;
    public const double WindowStepSec = 0.25;
    public const double SnapRadiusSec = 0.5;
    public const double EdgePenalty = 0.08, EdgeGuardSec = 2.0, EdgePenaltyMinTrackSec = 25.0;
    public const double FadeInSec = 0.030, FadeOutSec = 0.250;
    public const double WeightRms = 0.50, WeightFlux = 0.35, WeightCentroid = 0.15;
    private const int N = PlutoAudioBeatDetector.FrameSize, H = PlutoAudioBeatDetector.HopSize, Bins = PlutoAudioBeatDetector.Bins;

    private static readonly float[] s_hann = BuildHann();
    private readonly IPlutoAudioBeatDetector _detector;

    public PlutoAudioHighlightExtractor(IPlutoAudioBeatDetector? detector = null) => _detector = detector ?? PlutoAudioBeatDetector.Shared;

    public PlutoHighlightClipResult ExtractBestHighlight(ReadOnlySpan<byte> fullWavBytes, double targetDurationSec = DefaultDurationSec)
    {
        var info = ReadHeader(fullWavBytes);
        var dur = NormalizeDuration(targetDurationSec);
        var total = (double)info.Frames / info.SampleRate;
        var frames = info.Frames >= N ? 1 + (info.Frames - N) / H : 0;
        var pool = ArrayPool<double>.Shared;
        var prefix = pool.Rent(frames + 1);
        try
        {
            ComputeExcitementPrefix(fullWavBytes, in info, frames, prefix);
            if (total <= dur || frames == 0) return Build(fullWavBytes, in info, 0.0, dur, total, WindowScore(prefix, frames, info.SampleRate, 0.0, dur));

            double maxStart = total - dur, best = double.NegativeInfinity, bestT = 0.0;
            var steps = (int)Math.Floor(maxStart / WindowStepSec + 1e-9);
            for (var i = 0; i <= steps + 1; i++)
            {
                var t0 = Math.Min(i * WindowStepSec, maxStart);
                var s = WindowScore(prefix, frames, info.SampleRate, t0, dur);
                if (total > EdgePenaltyMinTrackSec && (t0 < EdgeGuardSec || t0 + dur > total - EdgeGuardSec)) s -= EdgePenalty;
                if (s > best + 1e-12) { best = s; bestT = t0; }
                if (t0 >= maxStart) break;
            }
            var start = Math.Clamp(SnapToBeat(fullWavBytes, in info, bestT, total), 0.0, maxStart);
            return Build(fullWavBytes, in info, start, dur, total, WindowScore(prefix, frames, info.SampleRate, start, dur));
        }
        finally { pool.Return(prefix); }
    }

    public PlutoHighlightClipResult ExtractWindowAt(ReadOnlySpan<byte> fullWavBytes, double startOffsetSec, double targetDurationSec = DefaultDurationSec)
    {
        var info = ReadHeader(fullWavBytes);
        var dur = NormalizeDuration(targetDurationSec);
        var total = (double)info.Frames / info.SampleRate;
        var start = double.IsFinite(startOffsetSec) ? Math.Clamp(startOffsetSec, 0.0, Math.Max(0.0, total - dur)) : 0.0;
        var frames = info.Frames >= N ? 1 + (info.Frames - N) / H : 0;
        var pool = ArrayPool<double>.Shared;
        var prefix = pool.Rent(frames + 1);
        try
        {
            ComputeExcitementPrefix(fullWavBytes, in info, frames, prefix);
            return Build(fullWavBytes, in info, start, dur, total, WindowScore(prefix, frames, info.SampleRate, start, dur));
        }
        finally { pool.Return(prefix); }
    }

    // ══════════════════════ 🔧 내부 ══════════════════════

    private static PlutoPcmWavInfo ReadHeader(ReadOnlySpan<byte> wav)
    {
        if (!PlutoPcmWav.TryReadHeader(wav, out var info)) throw new ArgumentException("지원되지 않는 WAV 형식입니다(PCM 16/24/32 · float32 만 지원).", nameof(wav));
        return info;
    }

    private static double NormalizeDuration(double d) => double.IsFinite(d) && d > 0.5 ? Math.Min(d, 120.0) : DefaultDurationSec;

    /// <summary>프레임 단위 스트리밍 디코드 → 3 특징 → 최대값 정규화 합성 → 누적합 <paramref name="prefix"/>[0..frames].</summary>
    private static void ComputeExcitementPrefix(ReadOnlySpan<byte> wav, in PlutoPcmWavInfo info, int frames, double[] prefix)
    {
        prefix[0] = 0;
        if (frames == 0) return;
        var fp = ArrayPool<float>.Shared;
        float[] buf = fp.Rent(N), re = fp.Rent(N), im = fp.Rent(N), prev = fp.Rent(Bins), rms = fp.Rent(frames), flux = fp.Rent(frames), cen = fp.Rent(frames);
        try
        {
            Array.Clear(prev, 0, Bins);
            float maxR = 0, maxF = 0, maxC = 0;
            for (var m = 0; m < frames; m++)
            {
                DecodeMonoRange(wav, in info, m * H, N, buf);
                double energy = 0;
                for (var i = 0; i < N; i++) { var x = buf[i]; energy += x * x; re[i] = x * s_hann[i]; im[i] = 0f; }
                PlutoAudioBeatDetector.Fft(re.AsSpan(0, N), im.AsSpan(0, N));
                double f = 0, magSum = 0, weighted = 0;
                for (var k = 0; k < Bins; k++)
                {
                    var v = MathF.Sqrt(re[k] * re[k] + im[k] * im[k]);
                    var d = v - prev[k];
                    if (d > 0) f += d;
                    magSum += v;
                    weighted += k * (double)v;
                    prev[k] = v;
                }
                rms[m] = (float)Math.Sqrt(energy / N);
                flux[m] = (float)f;
                cen[m] = magSum > 1e-9 ? (float)(weighted / magSum / (Bins - 1)) : 0f;
                if (rms[m] > maxR) maxR = rms[m];
                if (flux[m] > maxF) maxF = flux[m];
                if (cen[m] > maxC) maxC = cen[m];
            }
            double ir = maxR > 1e-9f ? 1.0 / maxR : 0, iF = maxF > 1e-9f ? 1.0 / maxF : 0, ic = maxC > 1e-9f ? 1.0 / maxC : 0;
            for (var m = 0; m < frames; m++)
                prefix[m + 1] = prefix[m] + WeightRms * rms[m] * ir + WeightFlux * flux[m] * iF + WeightCentroid * cen[m] * ic;
        }
        finally
        {
            fp.Return(buf); fp.Return(re); fp.Return(im); fp.Return(prev); fp.Return(rms); fp.Return(flux); fp.Return(cen);
        }
    }

    /// <summary>윈도 [t0, t0+dur) 내 프레임(시작 시각 기준) 평균 흥분도(0~1).</summary>
    private static double WindowScore(double[] prefix, int frames, int rate, double t0, double dur)
    {
        if (frames == 0) return 0;
        var a = Math.Clamp((int)Math.Ceiling(t0 * rate / H - 1e-9), 0, frames);
        var b = Math.Clamp((int)Math.Ceiling((t0 + dur) * rate / H - 1e-9), 0, frames);
        if (b <= a) return 0;
        return Math.Round((prefix[b] - prefix[a]) / (b - a), 6);
    }

    /// <summary>[t0−0.5, …) 구간 비트 검출 → t0 ±0.5s 내 최근접 비트(없음/폴백 → t0).</summary>
    private double SnapToBeat(ReadOnlySpan<byte> wav, in PlutoPcmWavInfo info, double t0, double total)
    {
        var segStart = Math.Max(0.0, t0 - SnapRadiusSec);
        var segDur = Math.Min(PlutoAudioBeatDetector.TimelineSec + 1.0, total - segStart);
        var beats = DetectSegmentBeats(wav, in info, segStart, segDur, out _);
        double best = t0, bestDist = SnapRadiusSec + 1e-9;
        for (var i = 0; i < beats.Count; i++)
        {
            var abs = segStart + beats[i];
            var d = Math.Abs(abs - t0);
            if (d <= bestDist) { bestDist = d; best = abs; }
        }
        return Math.Round(best, 4);
    }

    /// <summary>구간 모노 → PCM16 WAV → 비트 검출(폴백 격자 → 빈 목록). 구간 0초 기준.</summary>
    private IReadOnlyList<double> DetectSegmentBeats(ReadOnlySpan<byte> wav, in PlutoPcmWavInfo info, double segStart, double segDur, out double bpm)
    {
        bpm = 0;
        var startFrame = (int)Math.Round(segStart * info.SampleRate);
        var n = Math.Min((int)Math.Round(segDur * info.SampleRate), info.Frames - startFrame);
        if (n < N) return Array.Empty<double>();
        var fp = ArrayPool<float>.Shared;
        var mono = fp.Rent(n);
        try
        {
            DecodeMonoRange(wav, in info, startFrame, n, mono);
            return DetectBeatsOn(PlutoPcmWav.EncodePcm16(mono.AsSpan(0, n), default, 1, info.SampleRate), out bpm);
        }
        finally { fp.Return(mono); }
    }

    private IReadOnlyList<double> DetectBeatsOn(byte[] wav, out double bpm)
    {
        bpm = 0;
        if (_detector is PlutoAudioBeatDetector concrete)
        {
            var grid = concrete.Analyze(wav);
            if (grid.IsFallback) return Array.Empty<double>();
            bpm = grid.Bpm;
            return grid.Beats;
        }
        var beats = _detector.DetectBeats(wav);
        if (beats is null || beats.Count < 2 || ReferenceEquals(beats, PlutoAudioBeatDetector.FallbackGrid)) return Array.Empty<double>();
        var period = (beats[^1] - beats[0]) / (beats.Count - 1);
        bpm = period > 1e-6 ? Math.Round(60.0 / period, 2) : 0;
        return beats;
    }

    /// <summary>[start, start+dur) 크롭(채널 ≤ 2 · 원본 샘플레이트 · 부족분 0 패딩) → 페이드 → PCM16 + 상대 비트.</summary>
    private PlutoHighlightClipResult Build(ReadOnlySpan<byte> wav, in PlutoPcmWavInfo info, double start, double dur, double total, double score)
    {
        var rate = info.SampleRate;
        var outFrames = (int)Math.Round(dur * rate);
        var startFrame = Math.Clamp((int)Math.Round(start * rate), 0, info.Frames);
        var avail = Math.Min(outFrames, info.Frames - startFrame);
        var stereo = info.Channels >= 2;
        var fp = ArrayPool<float>.Shared;
        var l = fp.Rent(outFrames);
        var r = stereo ? fp.Rent(outFrames) : null;
        try
        {
            Array.Clear(l, 0, outFrames);
            if (r is not null) Array.Clear(r, 0, outFrames);
            if (avail > 0)
            {
                var sub = Range(in info, startFrame, avail);
                PlutoPcmWav.DecodeChannel(wav, in sub, 0, l);
                if (r is not null) PlutoPcmWav.DecodeChannel(wav, in sub, 1, r);
            }

            // 🥁 상대 비트 — 페이드 전 크롭 구간 기준.
            var mono = fp.Rent(outFrames);
            IReadOnlyList<double> beats;
            double bpm;
            try
            {
                for (var i = 0; i < outFrames; i++) mono[i] = r is null ? l[i] : 0.5f * (l[i] + r[i]);
                beats = DetectBeatsOn(PlutoPcmWav.EncodePcm16(mono.AsSpan(0, outFrames), default, 1, rate), out bpm);
            }
            finally { fp.Return(mono); }
            var rel = new List<double>(beats.Count);
            for (var i = 0; i < beats.Count; i++)
                if (beats[i] >= 0 && beats[i] <= dur + 1e-9) rel.Add(Math.Round(beats[i], 4));

            ApplyFades(l.AsSpan(0, outFrames), rate);
            if (r is not null) ApplyFades(r.AsSpan(0, outFrames), rate);
            var bytes = PlutoPcmWav.EncodePcm16(l.AsSpan(0, outFrames), r is null ? default : r.AsSpan(0, outFrames), stereo ? 2 : 1, rate);
            var s = Math.Round((double)startFrame / rate, 4);
            return new PlutoHighlightClipResult(bytes, s, Math.Round(s + dur, 4), Math.Round(total, 4), bpm, rel.AsReadOnly(), score);
        }
        finally
        {
            fp.Return(l);
            if (r is not null) fp.Return(r);
        }
    }

    /// <summary>30ms 선형 페이드 인 · 250ms 코사인 페이드 아웃(무클릭).</summary>
    public static void ApplyFades(Span<float> x, int rate)
    {
        var fin = Math.Min(x.Length, (int)Math.Round(FadeInSec * rate));
        for (var i = 0; i < fin; i++) x[i] *= (float)i / fin;
        var fout = Math.Min(x.Length, (int)Math.Round(FadeOutSec * rate));
        var o = x.Length - fout;
        for (var i = 0; i < fout; i++) x[o + i] *= (float)(0.5 * (1.0 + Math.Cos(Math.PI * (i + 1) / fout)));
    }

    private static PlutoPcmWavInfo Range(in PlutoPcmWavInfo info, int startFrame, int frames)
        => info with { DataOffset = info.DataOffset + startFrame * info.BlockAlign, DataLength = frames * info.BlockAlign };

    private static void DecodeMonoRange(ReadOnlySpan<byte> wav, in PlutoPcmWavInfo info, int startFrame, int frames, float[] dst)
    {
        var avail = Math.Clamp(info.Frames - startFrame, 0, frames);
        if (avail > 0)
        {
            var sub = Range(in info, startFrame, avail);
            PlutoPcmWav.DecodeMono(wav, in sub, dst);
        }
        if (avail < frames) Array.Clear(dst, avail, frames - avail);
    }

    private static float[] BuildHann()
    {
        var w = new float[N];
        for (var n = 0; n < N; n++) w[n] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * n / (N - 1)));
        return w;
    }
}
