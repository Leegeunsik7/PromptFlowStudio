using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

public sealed class PlutoSfxDirectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_sfx_" + Guid.NewGuid().ToString("N"));

    public PlutoSfxDirectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData(PlutoSfxType.ShutterClick, 150)]
    [InlineData(PlutoSfxType.ChimeSparkle, 800)]
    [InlineData(PlutoSfxType.BedtimeTone, 1200)]
    public void PlutoAudioSynthesizer_GeneratesValidWavHeaders_AndPlayableByteStreams(PlutoSfxType type, int expectedMs)
    {
        var wav = PlutoAudioSynthesizer.Generate(type);
        var s = wav.AsSpan();
        Assert.True(s.Slice(0, 4).SequenceEqual("RIFF"u8));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(s.Slice(4)));
        Assert.True(s.Slice(8, 4).SequenceEqual("WAVE"u8));
        Assert.True(s.Slice(12, 4).SequenceEqual("fmt "u8));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(s.Slice(16)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(s.Slice(20)));       // PCM
        var ch = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(22));
        Assert.InRange(ch, (short)1, (short)2);
        Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(s.Slice(24)));
        Assert.Equal(48000 * ch * 2, BinaryPrimitives.ReadInt32LittleEndian(s.Slice(28)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(s.Slice(34)));
        Assert.True(s.Slice(36, 4).SequenceEqual("data"u8));
        var data = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(40));
        Assert.Equal(wav.Length - PlutoAudioSynthesizer.WavHeaderBytes, data);
        Assert.Equal(48000 * expectedMs / 1000 * ch * 2, data);

        // 재생 가능한 비무음 페이로드 · 클리핑 없음(피크 ≤ −1 dBFS 근방) · 끝 샘플 페이드(클릭 방지) · 결정론.
        int peak = 0;
        for (var p = PlutoAudioSynthesizer.WavHeaderBytes; p < wav.Length; p += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(s.Slice(p))));
        Assert.InRange(peak, 8000, 29500);
        Assert.InRange(Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(s.Slice(wav.Length - 2))), 0, 600);
        Assert.Equal(wav, PlutoAudioSynthesizer.Generate(type));
    }

    [Fact]
    public void PlutoAudioSynthesizer_AdsrEnvelope_FollowsAttackDecaySustainRelease()
    {
        Assert.Equal(0, PlutoAudioSynthesizer.Adsr(-0.1, 0.01, 0.1, 0.5, 0.1, 1.0));
        Assert.Equal(0.5, PlutoAudioSynthesizer.Adsr(0.005, 0.01, 0.1, 0.5, 0.1, 1.0), 6);         // attack 중간
        Assert.InRange(PlutoAudioSynthesizer.Adsr(0.6, 0.01, 0.1, 0.5, 0.1, 1.0), 0.49, 0.51);     // sustain 수렴
        Assert.InRange(PlutoAudioSynthesizer.Adsr(0.95, 0.01, 0.1, 0.5, 0.1, 1.0), 0.24, 0.26);    // release 절반
        Assert.Equal(0, PlutoAudioSynthesizer.Adsr(1.0, 0.01, 0.1, 0.5, 0.1, 1.0));
    }

    [Fact]
    public void PlutoSfxDirector_BuildsAccurateTimestamps_For15sTimeline()
    {
        var cues = PlutoSfxDirector.Shared.BuildDefault15sCueSheet();
        Assert.Equal(3, cues.Count);
        Assert.Equal(new PlutoSfxCue(PlutoSfxType.ShutterClick, 800, 0.7), cues[0]);
        Assert.Equal(new PlutoSfxCue(PlutoSfxType.ChimeSparkle, 5500, 0.6), cues[1]);
        Assert.Equal(new PlutoSfxCue(PlutoSfxType.BedtimeTone, 13200, 0.55), cues[2]);
        for (var i = 0; i < cues.Count; i++)
        {
            Assert.InRange(cues[i].VolumeRatio, PlutoSfxDirector.MinVolumeRatio, PlutoSfxDirector.MaxVolumeRatio);
            Assert.True(cues[i].OffsetMs + PlutoAudioSynthesizer.DurationMs(cues[i].SfxType) <= 15000);   // 15초 안에서 종료
            if (i > 0) Assert.True(cues[i].OffsetMs >= cues[i - 1].OffsetMs + PlutoAudioSynthesizer.DurationMs(cues[i - 1].SfxType));   // 겹침 없음
        }
        // 길이 미상 = 전체 · 10초 영상 = 13.2초 벨 제외 · 0.5초 = 없음.
        Assert.Same(cues, PlutoSfxDirector.Shared.BuildCueSheet(0));
        Assert.Equal(new[] { PlutoSfxType.ShutterClick, PlutoSfxType.ChimeSparkle }, PlutoSfxDirector.Shared.BuildCueSheet(10).Select(c => c.SfxType));
        Assert.Empty(PlutoSfxDirector.Shared.BuildCueSheet(0.5));
    }

    [Fact]
    public void StockSuiteExporter_ConstructsValidMultiTrackAmixFilterGraph()
    {
        var cues = PlutoSfxDirector.Shared.BuildDefault15sCueSheet();
        var g = StockSuiteExporter.BuildBabyBgmSfxFilterGraph(15, cues);
        Assert.StartsWith("[1:a]loudnorm=I=-14:LRA=11:TP=-1.5,volume=0.25,afade=t=in:st=0:d=1.5,afade=t=out:st=13.50:d=1.5,", g, StringComparison.Ordinal);
        Assert.Contains("[2:a]aformat=sample_rates=48000:channel_layouts=stereo,volume=0.7,adelay=800|800[d0];", g, StringComparison.Ordinal);
        Assert.Contains("[3:a]aformat=sample_rates=48000:channel_layouts=stereo,volume=0.6,adelay=5500|5500[d1];", g, StringComparison.Ordinal);
        Assert.Contains("[4:a]aformat=sample_rates=48000:channel_layouts=stereo,volume=0.55,adelay=13200|13200[d2];", g, StringComparison.Ordinal);
        Assert.EndsWith("[bgm][d0][d1][d2]amix=inputs=4:duration=first:dropout_transition=0:normalize=0," + StockSuiteExporter.SfxLimiter + "[aout]", g, StringComparison.Ordinal);
        Assert.DoesNotContain("0,7", g, StringComparison.Ordinal);

        var sfx = new[] { "a.wav", "b.wav", "c.wav" };
        var args = StockSuiteExporter.BuildBabyBgmSfxMixArgs("in.mp4", "bgm.wav", sfx, cues, 15, "out.mp4");
        Assert.Equal(new[] { "in.mp4", "bgm.wav", "a.wav", "b.wav", "c.wav" }, args.Select((a, i) => (a, i)).Where(x => x.i > 0 && args[x.i - 1] == "-i").Select(x => x.a));
        Assert.Equal("-1", args[Array.IndexOf(args, "-stream_loop") + 1]);
        Assert.Equal(g, args[Array.IndexOf(args, "-filter_complex") + 1]);
        Assert.Equal("[aout]", args[Array.LastIndexOf(args, "-map") + 1]);
        Assert.Equal("aac", args[Array.IndexOf(args, "-c:a") + 1]);
        Assert.Equal("15", args[Array.IndexOf(args, "-t") + 1]);
        Assert.DoesNotContain("-af", args);
        Assert.Equal("out.mp4", args[^1]);
        Assert.Throws<ArgumentException>(() => StockSuiteExporter.BuildBabyBgmSfxMixArgs("in.mp4", "bgm.wav", new[] { "a.wav" }, cues, 15, "out.mp4"));
    }

    private (StockSuiteExporter Exp, List<string[]> Calls, string Src, string Target, string SfxDir) Setup(Func<string[], bool>? ok = null)
    {
        var src = Path.Combine(_dir, "render.mp4");
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);
        var bgm = Path.Combine(_dir, "lullaby.wav");
        File.WriteAllBytes(bgm, new byte[64]);
        var sfxDir = Path.Combine(_dir, "sfx");
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            if (ok is not null && !ok(arr)) return Task.FromResult(false);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = (_, _) => bgm,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            SfxTempDir = sfxDir,
            BabyBgm = BabyBgmTrack.Lullaby,
        };
        return (exp, calls, src, target, sfxDir);
    }

    [Fact]
    public async Task StockSuiteExporter_SfxOn_MixesTimelineCues_AndCleansTempWavs()
    {
        var (exp, calls, src, target, sfxDir) = Setup();
        exp.IncludeSfx = true;
        var r = await exp.ExportAsync(src);

        Assert.True(r.Ok, r.Message);
        Assert.True(r.HasAudio);
        Assert.Contains(StockSuiteExporter.SfxTag + "3큐", r.Message, StringComparison.Ordinal);
        var mix = calls[^1];
        Assert.Contains("-filter_complex", mix);
        Assert.Contains("adelay=13200|13200", mix[Array.IndexOf(mix, "-filter_complex") + 1], StringComparison.Ordinal);
        Assert.Equal(3, mix.Count(a => a.EndsWith(".wav", StringComparison.Ordinal) && Path.GetFileName(a).StartsWith("sfx_", StringComparison.Ordinal)));
        Assert.Empty(Directory.GetFiles(sfxDir));                    // 임시 SFX WAV 정리
        Assert.Empty(Directory.GetFiles(target, "*.~bgm"));
    }

    [Fact]
    public async Task StockSuiteExporter_SfxMixFailure_FallsBackToBgmOnly()
    {
        var (exp, calls, src, target, sfxDir) = Setup(ok: a => !a.Contains("-filter_complex"));
        exp.IncludeSfx = true;
        var r = await exp.ExportAsync(src);

        Assert.True(r.Ok, r.Message);
        Assert.True(r.HasAudio);
        Assert.Contains(StockSuiteExporter.BabyBgmTag, r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StockSuiteExporter.SfxTag, r.Message, StringComparison.Ordinal);
        Assert.Contains("-filter_complex", calls[^2]);               // SFX 시도 → 실패
        Assert.Contains("-af", calls[^1]);                           // BGM 단독 폴백
        Assert.Empty(Directory.GetFiles(sfxDir));
        Assert.Empty(Directory.GetFiles(target, "*.~bgm"));
    }

    [Fact]
    public async Task StockSuiteExporter_SfxOff_KeepsLegacyBgmOnlyPath()
    {
        var (exp, calls, src, _, sfxDir) = Setup();
        Assert.False(exp.IncludeSfx);   // 엔진 기본 OFF(UI 가 결선)
        var r = await exp.ExportAsync(src);

        Assert.True(r.Ok, r.Message);
        Assert.DoesNotContain(calls, c => c.Contains("-filter_complex"));
        Assert.DoesNotContain(StockSuiteExporter.SfxTag, r.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(sfxDir));
    }
}
