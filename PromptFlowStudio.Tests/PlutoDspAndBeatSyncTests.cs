using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Pluto.Dsp;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🥁🎛️ 플루토 DSP — FFT 스펙트럴 플럭스 비트 검출 · 비트 동기 양자화 · BS.1770-4 라우드니스 · 4배 폴리페이즈 트루피크 리미터 · 익스포터 결선.
/// </summary>
public sealed class PlutoDspAndBeatSyncTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_dsp_" + Guid.NewGuid().ToString("N"));

    public PlutoDspAndBeatSyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ══════════════════════ 🥁 비트 검출 ══════════════════════

    [Fact]
    public void AudioBeatDetector_DetectsBpmAndBeats_FromSynthesizedPulseTrack()
    {
        var wav = PlutoPcmWav.EncodePcm16(PulseTrack(120, 0.25, 15.0), default, 1, Rate);
        var grid = PlutoAudioBeatDetector.Shared.Analyze(wav);

        Assert.False(grid.IsFallback);
        Assert.InRange(grid.Bpm, 118.0, 122.0);
        Assert.InRange(grid.Beats.Count, 29, 31);
        foreach (var b in grid.Beats)
        {
            var nearest = 0.25 + Math.Round((b - 0.25) / 0.5) * 0.5;
            Assert.True(Math.Abs(b - nearest) <= 0.03, $"beat {b:F4} ↔ pulse {nearest:F2}");
            Assert.InRange(b, 0.0, PlutoAudioBeatDetector.TimelineSec);
        }
        Assert.Equal(grid.Beats, PlutoAudioBeatDetector.Shared.DetectBeats(wav));
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(128.0)]
    public void AudioBeatDetector_TracksOtherTempos_InStereoFloatWav(double bpm)
    {
        var x = PulseTrack(bpm, 0.1, 15.0);
        var wav = EncodeFloat32Stereo(x, x);   // BGM 캐시 포맷(float32 · 스테레오)
        var grid = PlutoAudioBeatDetector.Shared.Analyze(wav);
        Assert.False(grid.IsFallback);
        Assert.InRange(grid.Bpm, bpm - 2, bpm + 2);
    }

    [Fact]
    public void AudioBeatDetector_FallsBackToDeterministicGrid_OnSilenceOrGarbage()
    {
        var expected = Enumerable.Range(0, 16).Select(i => (double)i).ToArray();
        Assert.Equal(expected, PlutoAudioBeatDetector.Shared.DetectBeats(new byte[64]));
        Assert.Equal(expected, PlutoAudioBeatDetector.Shared.DetectBeats(PlutoPcmWav.EncodePcm16(new float[Rate * 3], default, 1, Rate)));
        Assert.Equal(expected, PlutoAudioBeatDetector.Shared.DetectBeats(ReadOnlySpan<byte>.Empty));
        Assert.True(PlutoAudioBeatDetector.Shared.Analyze(new byte[64]).IsFallback);
    }

    [Fact]
    public void Dsp_Fft_MatchesNaiveDft()
    {
        const int n = 64;
        var rnd = new Random(7);
        var re = new float[n];
        var im = new float[n];
        for (var i = 0; i < n; i++) re[i] = (float)(rnd.NextDouble() * 2 - 1);
        var src = (float[])re.Clone();
        PlutoAudioBeatDetector.Fft(re, im);
        for (var k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (var t = 0; t < n; t++)
            {
                var a = -2 * Math.PI * k * t / n;
                sr += src[t] * Math.Cos(a);
                si += src[t] * Math.Sin(a);
            }
            Assert.Equal(sr, re[k], 3);
            Assert.Equal(si, im[k], 3);
        }
        Assert.Throws<ArgumentException>(() => PlutoAudioBeatDetector.Fft(new float[12], new float[12]));
    }

    // ══════════════════════ 🧲 비트 동기 양자화 ══════════════════════

    [Fact]
    public void BeatSyncQuantizer_SnapsMotionAndSfx_WithinTolerance()
    {
        var beats = Grid(0.1, 0.5, 15.0);   // 0.1, 0.6, …, 14.6
        var baseTraj = PlutoMotionCompiler.SynthesizeFourAct(PlutoMotionCompiler.BabyFocal);
        var q = PlutoBeatSyncQuantizer.Shared.QuantizeTrajectory(baseTraj, beats);

        Assert.Equal(new[] { 4.1, 8.1, 12.1 }, q.Segments.Take(3).Select(s => Math.Round(s.EndTimeSec, 6)));
        for (var i = 1; i < q.Segments.Count; i++) Assert.Equal(q.Segments[i - 1].EndTimeSec, q.Segments[i].StartTimeSec);
        Assert.Equal(0.0, q.Segments[0].StartTimeSec);
        Assert.Equal(15.0, q.DurationSec);
        Assert.Equal(PlutoMotionCompiler.TotalFrames(baseTraj), PlutoMotionCompiler.TotalFrames(q));
        for (var i = 0; i < q.Segments.Count; i++)
        {
            Assert.Equal(baseTraj.Segments[i].StartZoom, q.Segments[i].StartZoom);
            Assert.Equal(baseTraj.Segments[i].EndZoom, q.Segments[i].EndZoom);
            Assert.Equal(baseTraj.Segments[i].FocalCenter, q.Segments[i].FocalCenter);
        }

        // C¹ — 재배치 경계 양쪽 프레임 간 줌 변화량 ≈ 0(이징 도함수 0) · 위치 연속.
        var fb = (int)Math.Round(4.1 * PlutoMotionCompiler.DefaultFps);
        var z0 = PlutoMotionCompiler.EvaluateFrame(q, fb - 1).Zoom;
        var z1 = PlutoMotionCompiler.EvaluateFrame(q, fb).Zoom;
        var z2 = PlutoMotionCompiler.EvaluateFrame(q, fb + 1).Zoom;
        Assert.Equal(1.10, z1, 6);
        Assert.True(Math.Abs(z1 - z0) < 1e-4 && Math.Abs(z2 - z1) < 1e-4, $"{z0} {z1} {z2}");

        var cues = PlutoBeatSyncQuantizer.Shared.QuantizeSfxCues(PlutoSfxDirector.Shared.BuildDefault15sCueSheet(), beats);
        Assert.Equal(new[] { 600, 5600, 13100 }, cues.Select(c => c.OffsetMs));
        Assert.Equal(new[] { 0.7, 0.6, 0.55 }, cues.Select(c => c.VolumeRatio));
        Assert.Equal(new[] { PlutoSfxType.ShutterClick, PlutoSfxType.ChimeSparkle, PlutoSfxType.BedtimeTone }, cues.Select(c => c.SfxType));
    }

    [Fact]
    public void BeatSyncQuantizer_LeavesTimingUntouched_OutsideMagneticWindow()
    {
        var sparse = new[] { 2.0, 6.0, 10.0, 14.5 };   // 모든 경계/큐에서 창 밖
        var baseTraj = PlutoMotionCompiler.SynthesizeFourAct((0.4, 0.4));
        Assert.Same(baseTraj, PlutoBeatSyncQuantizer.Shared.QuantizeTrajectory(baseTraj, sparse));
        Assert.Same(baseTraj, PlutoBeatSyncQuantizer.Shared.QuantizeTrajectory(baseTraj, Array.Empty<double>()));
        var cues = PlutoBeatSyncQuantizer.Shared.QuantizeSfxCues(PlutoSfxDirector.Shared.BuildDefault15sCueSheet(), sparse);
        Assert.Equal(new[] { 800, 5500, 13200 }, cues.Select(c => c.OffsetMs));

        // 창 경계: 모션 ±0.25s · SFX ±0.35s.
        Assert.True(PlutoBeatSyncQuantizer.TrySnap(4.0, new[] { 4.25 }, PlutoBeatSyncQuantizer.MotionSnapToleranceSec, out var s1));
        Assert.Equal(4.25, s1);
        Assert.False(PlutoBeatSyncQuantizer.TrySnap(4.0, new[] { 4.26 }, PlutoBeatSyncQuantizer.MotionSnapToleranceSec, out _));
        Assert.True(PlutoBeatSyncQuantizer.TrySnap(5.5, new[] { 5.15, double.NaN }, PlutoBeatSyncQuantizer.SfxSnapToleranceSec, out var s2));
        Assert.Equal(5.15, s2);
    }

    [Fact]
    public void BeatSync_RetimeBoundaries_RejectsCollapsingOrInvalidLayouts()
    {
        var t = PlutoMotionCompiler.SynthesizeFourAct(PlutoMotionCompiler.BabyFocal);
        Assert.Same(t, PlutoMotionCompiler.RetimeBoundaries(t, new[] { 4.0, 3.9, 12.0 }));     // 비단조
        Assert.Same(t, PlutoMotionCompiler.RetimeBoundaries(t, new[] { 4.0, 8.0 }));           // 개수 불일치
        Assert.Same(t, PlutoMotionCompiler.RetimeBoundaries(t, new[] { 4.0, 8.0, 14.8 }));     // 마지막 막 < 0.5s
        Assert.Same(t, PlutoMotionCompiler.RetimeBoundaries(t, new[] { 4.0, double.NaN, 12.0 }));
        var ok = PlutoMotionCompiler.RetimeBoundaries(t, new[] { 3.8, 8.2, 11.9 });
        Assert.Equal(new[] { 0.0, 3.8, 8.2, 11.9 }, ok.Segments.Select(s => s.StartTimeSec));
        Assert.Equal(15.0, ok.DurationSec);
    }

    // ══════════════════════ 🎛️ BS.1770-4 · 트루피크 ══════════════════════

    [Fact]
    public void PolyphaseTruePeakDetector_CatchesInterSampleOvershoot()
    {
        // fs/4 사인 · 위상 π/4 → 샘플은 ±1.0(0 dBFS)이지만 연속 파형 피크는 √2(+3.01 dBTP).
        var n = Rate;
        var x = new float[n];
        for (var i = 0; i < n; i++) x[i] = (float)(Math.Sqrt(2) * Math.Sin(2 * Math.PI * 12000.0 / Rate * i + Math.PI / 4));
        var samplePeak = x.Max(v => Math.Abs(v));
        Assert.InRange(PlutoTruePeakMasteringEngine.ToDb(samplePeak), -0.01, 0.01);
        var tp = PlutoTruePeakMasteringEngine.ToDb(PlutoTruePeakMasteringEngine.MeasureTruePeak(x));
        Assert.True(tp > 2.0, $"TP {tp:F2} dBTP");
        Assert.True(tp < 3.2, $"TP {tp:F2} dBTP");

        // 저주파 사인은 오버슈트 없음(−6.02 dBFS ± 0.1).
        for (var i = 0; i < n; i++) x[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000.0 / Rate * i));
        Assert.InRange(PlutoTruePeakMasteringEngine.MeasureTruePeakDbtp(x, default), -6.12, -5.92);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(44100)]
    public void Dsp_IntegratedLoudness_MatchesBs1770Reference(int rate)
    {
        // 997 Hz 풀스케일 사인: 모노 −3.01 LUFS · 스테레오(양 채널 동일) 0.0 LUFS.
        var x = new float[rate * 5];
        for (var i = 0; i < x.Length; i++) x[i] = (float)Math.Sin(2 * Math.PI * 997.0 / rate * i);
        Assert.InRange(PlutoTruePeakMasteringEngine.MeasureIntegratedLoudness(x, default, rate), -3.11, -2.91);
        Assert.InRange(PlutoTruePeakMasteringEngine.MeasureIntegratedLoudness(x, x, rate), -0.1, 0.1);
        Assert.Equal(double.NegativeInfinity, PlutoTruePeakMasteringEngine.MeasureIntegratedLoudness(new float[rate], default, rate));
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.08f)]
    public void TruePeakMasteringEngine_EnforcesCeilingAndLoudnessTarget(float level)
    {
        var (l, r) = DenseTransientMix(12.0, level);
        var wav = PlutoPcmWav.EncodePcm16(l, r, 2, Rate);
        Assert.True(PlutoTruePeakMasteringEngine.TryMasterWavBuffer(wav, out var mastered, out var report));

        Assert.True(PlutoPcmWav.TryReadHeader(mastered, out var info));
        Assert.Equal((2, Rate, 16, l.Length), (info.Channels, info.SampleRate, info.BitsPerSample, info.Frames));
        var ol = new float[info.Frames];
        var or = new float[info.Frames];
        PlutoPcmWav.DecodeChannel(mastered, in info, 0, ol);
        PlutoPcmWav.DecodeChannel(mastered, in info, 1, or);

        var tp = PlutoTruePeakMasteringEngine.MeasureTruePeakDbtp(ol, or);
        var lufs = PlutoTruePeakMasteringEngine.MeasureIntegratedLoudness(ol, or, Rate);
        Assert.True(tp <= -1.00, $"TP {tp:F3} dBTP");
        Assert.True(Math.Max(ol.Max(v => Math.Abs(v)), or.Max(v => Math.Abs(v))) <= PlutoTruePeakMasteringEngine.CeilingLinear);
        Assert.True(Math.Abs(lufs + 14.0) <= 0.5, $"LUFS {lufs:F2} · {report}");
        Assert.True(report.LoudnessOnTarget, report.ToString());
        Assert.Equal(lufs, report.OutputLufs, 6);
        Assert.True(report.InputTruePeakDbtp > -1.0 || level < 1f);   // 입력은 천장 초과(리미팅 실제 동작)
        Assert.True(report.MaxGainReductionDb < -0.5, report.ToString());   // 프리게인 후 트랜지언트 리미팅 발생
    }

    [Fact]
    public void TruePeakMasteringEngine_PreservesInput_OnGarbageOrSilence()
    {
        var garbage = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Equal(garbage, PlutoTruePeakMasteringEngine.MasterWavBuffer(garbage));
        var silent = PlutoPcmWav.EncodePcm16(new float[Rate], default, 1, Rate);
        Assert.False(PlutoTruePeakMasteringEngine.TryMasterWavBuffer(silent, out _, out _));
        Assert.Equal(silent, PlutoTruePeakMasteringEngine.MasterWavBuffer(silent));
    }

    // ══════════════════════ 🔌 익스포터 결선 ══════════════════════

    [Fact]
    public async Task Dsp_ExporterMux_FeedsMasteredBgm_AndBeatSnapsSfxCues()
    {
        var src = Path.Combine(_dir, "render.mp4");
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);
        var bgm = Path.Combine(_dir, "pulse.wav");
        var pulse = PulseTrack(120, 0.2, 16.0);   // 비트 0.2 + 0.5k → 셔터 0.7 · 차임 5.7 · 벨 13.2
        File.WriteAllBytes(bgm, EncodeFloat32Stereo(pulse, pulse));
        var masterDir = Path.Combine(_dir, "master");
        var calls = new List<string[]>();
        string? fedBgm = null;
        long fedBgmLen = 0;
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            var bgmArg = arr[Array.IndexOf(arr, "-i", Array.IndexOf(arr, "-i") + 1) + 1];
            if (File.Exists(bgmArg)) { fedBgm = bgmArg; fedBgmLen = new FileInfo(bgmArg).Length; }
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = (_, _) => bgm,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            SfxTempDir = Path.Combine(_dir, "sfx"),
            MasteringTempDir = masterDir,
            BabyBgm = BabyBgmTrack.Lullaby,
            IncludeSfx = true,
        };
        var r = await exp.ExportAsync(src);
        Assert.True(r.Ok, r.Message);

        Assert.NotNull(fedBgm);
        Assert.StartsWith(masterDir, fedBgm!, StringComparison.OrdinalIgnoreCase);   // 마스터링 사본이 FFmpeg 에 투입
        Assert.True(fedBgmLen > PlutoPcmWav.HeaderBytes);
        Assert.Empty(Directory.GetFiles(masterDir));                                    // 임시 마스터 WAV 정리
        var graph = calls[^1][Array.IndexOf(calls[^1], "-filter_complex") + 1];
        var delays = Regex.Matches(graph, @"adelay=(\d+)\|").Select(m => int.Parse(m.Groups[1].Value)).ToArray();
        Assert.Equal(3, delays.Length);
        Assert.InRange(delays[0], 670, 730);
        Assert.InRange(delays[1], 5670, 5730);
        Assert.InRange(delays[2], 13170, 13230);
    }

    [Fact]
    public async Task Dsp_PrepareBgm_FallsBackToOriginal_WhenDisabledOrInvalid()
    {
        var exp = new StockSuiteExporter(() => _dir) { MasteringTempDir = Path.Combine(_dir, "m") };
        var bad = Path.Combine(_dir, "bad.wav");
        File.WriteAllBytes(bad, new byte[64]);
        Assert.Equal((null, null), await exp.PrepareBgmDspAsync(bad, true, CancellationToken.None));

        var good = Path.Combine(_dir, "good.wav");
        File.WriteAllBytes(good, PlutoPcmWav.EncodePcm16(PulseTrack(120, 0.25, 6.0), default, 1, Rate));
        exp.TruePeakMasteringEnabled = false;
        var (path, beats) = await exp.PrepareBgmDspAsync(good, true, CancellationToken.None);
        Assert.Null(path);
        Assert.NotNull(beats);
        Assert.Equal((null, null), await exp.PrepareBgmDspAsync(good, false, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exp.PrepareBgmDspAsync(good, true, new CancellationToken(true)));
    }

    // ══════════════════════ 🔧 합성 헬퍼 ══════════════════════

    private static double[] Grid(double phase, double period, double end)
    {
        var list = new List<double>();
        for (var t = phase; t <= end + 1e-9; t += period) list.Add(Math.Round(t, 6));
        return list.ToArray();
    }

    /// <summary>bpm 펄스(880Hz 톤 + 노이즈 · 15ms 지수 감쇠) 모노 트랙.</summary>
    private static float[] PulseTrack(double bpm, double phase, double seconds)
    {
        var n = (int)(seconds * Rate);
        var x = new float[n];
        var period = 60.0 / bpm;
        uint s = 12345;
        for (var k = 0; ; k++)
        {
            var t0 = phase + k * period;
            if (t0 >= seconds) break;
            var start = (int)Math.Round(t0 * Rate);
            for (var i = 0; i < (int)(0.08 * Rate) && start + i < n; i++)
            {
                var tt = i / (double)Rate;
                s = s * 1664525 + 1013904223;
                var noise = (s >> 9) / (double)(1 << 23) * 2 - 1;
                x[start + i] += (float)(Math.Exp(-tt / 0.015) * (0.6 * Math.Sin(2 * Math.PI * 880 * tt) + 0.3 * noise));
            }
        }
        return x;
    }

    /// <summary>킥(피치 드롭) · 스네어 · 8분 하이햇 · 패드 — 피크 대비 바디가 얇은 고크레스트 스테레오 믹스.</summary>
    private static (float[] L, float[] R) DenseTransientMix(double seconds, float level)
    {
        var n = (int)(seconds * Rate);
        float[] l = new float[n], r = new float[n];
        uint s = 987654321;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            s = s * 1664525 + 1013904223;
            var noise = (s >> 9) / (double)(1 << 23) * 2 - 1;
            var kp = t % 0.5;
            var kick = 0.95 * Math.Exp(-kp / 0.06) * Math.Sin(2 * Math.PI * (55 * kp + 1.6 * (1 - Math.Exp(-kp / 0.02))));
            var hp = t % 0.25;
            var hat = 0.35 * Math.Exp(-hp / 0.008) * noise;
            var sp = (t + 0.5) % 1.0;
            var snare = 0.6 * Math.Exp(-sp / 0.05) * (0.5 * noise + 0.5 * Math.Sin(2 * Math.PI * 190 * sp));
            var pad = 0.04 * Math.Sin(2 * Math.PI * 220 * t);
            l[i] = (float)Math.Clamp(level * (kick + hat + 0.9 * snare + pad), -0.99, 0.99);
            r[i] = (float)Math.Clamp(level * (kick + 0.8 * hat + snare + pad), -0.99, 0.99);
        }
        return (l, r);
    }

    /// <summary>float32 스테레오 WAV(SoundDsp.WriteStereoWav 와 동일 포맷).</summary>
    private static byte[] EncodeFloat32Stereo(float[] l, float[] r)
    {
        var n = Math.Min(l.Length, r.Length);
        var data = n * 8;
        var b = new byte[44 + data];
        var sp = b.AsSpan();
        "RIFF"u8.CopyTo(sp);
        BitConverter.TryWriteBytes(sp.Slice(4), 36 + data);
        "WAVEfmt "u8.CopyTo(sp.Slice(8));
        BitConverter.TryWriteBytes(sp.Slice(16), 16);
        BitConverter.TryWriteBytes(sp.Slice(20), (short)3);
        BitConverter.TryWriteBytes(sp.Slice(22), (short)2);
        BitConverter.TryWriteBytes(sp.Slice(24), Rate);
        BitConverter.TryWriteBytes(sp.Slice(28), Rate * 8);
        BitConverter.TryWriteBytes(sp.Slice(32), (short)8);
        BitConverter.TryWriteBytes(sp.Slice(34), (short)32);
        "data"u8.CopyTo(sp.Slice(36));
        BitConverter.TryWriteBytes(sp.Slice(40), data);
        for (var i = 0; i < n; i++)
        {
            BitConverter.TryWriteBytes(sp.Slice(44 + i * 8), l[i]);
            BitConverter.TryWriteBytes(sp.Slice(48 + i * 8), r[i]);
        }
        return b;
    }
}
