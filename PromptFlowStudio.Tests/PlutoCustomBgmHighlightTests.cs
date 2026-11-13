using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Pluto.Dsp;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🎵 커스텀 BGM — 하이라이트 추출(클라이맥스 · 비트 스냅 · 경계) · 디코드 서비스 · 익스포터/프리뷰 결선.</summary>
public sealed class PlutoCustomBgmHighlightTests : IDisposable
{
    private const int Rate = 48000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_custombgm_" + Guid.NewGuid().ToString("N"));

    public PlutoCustomBgmHighlightTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// 합성 트랙 — 전 구간 저음량 패드(0.02) + [loudStart, loudEnd) 고음량 패드 · 120 BPM 퍼커시브 킥(노이즈 + 60Hz · 첫 킥 = loudStart + beatOffset).
    /// </summary>
    private static byte[] SynthTrack(double totalSec, double loudStart, double loudEnd, double beatOffset = 0.0)
    {
        var n = (int)Math.Round(totalSec * Rate);
        var l = new float[n];
        var r = new float[n];
        var rng = new Random(7);
        var firstKick = loudStart + beatOffset;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / Rate;
            var v = 0.02 * Math.Sin(2 * Math.PI * 220 * t);
            var noise = rng.NextDouble() * 2 - 1;
            if (t >= loudStart && t < loudEnd)
            {
                v += 0.12 * Math.Sin(2 * Math.PI * 330 * t);
                if (t >= firstKick)
                {
                    var dt = (t - firstKick) % 0.5;
                    var env = Math.Exp(-dt * 28);
                    v += env * (0.55 * Math.Sin(2 * Math.PI * 60 * dt) + 0.35 * noise);
                }
            }
            l[i] = (float)v;
            r[i] = (float)(v * 0.9);
        }
        return PlutoPcmWav.EncodePcm16(l, r, 2, Rate);
    }

    private static int ExpectedBytes(double sec, int channels = 2) => PlutoPcmWav.HeaderBytes + (int)Math.Round(sec * Rate) * channels * 2;

    [Fact]
    public void HighlightExtractor_LocatesHighEnergyChorus_InLongSyntheticTrack()
    {
        var wav = SynthTrack(45, 20, 35);

        var hl = PlutoAudioHighlightExtractor.Shared.ExtractBestHighlight(wav);

        Assert.InRange(hl.StartOffsetSec, 19.0, 21.0);
        Assert.Equal(hl.StartOffsetSec + 15.0, hl.EndOffsetSec, 3);
        Assert.Equal(45.0, hl.TotalSourceDurationSec, 3);
        Assert.Equal(ExpectedBytes(15.0), hl.CroppedWavBytes.Length);
        Assert.True(PlutoPcmWav.TryReadHeader(hl.CroppedWavBytes, out var info));
        Assert.Equal(Rate, info.SampleRate);
        Assert.Equal(2, info.Channels);
        Assert.Equal(15 * Rate, info.Frames);
        Assert.InRange(hl.DetectedBpm, 115.0, 125.0);
        Assert.NotEmpty(hl.RelativeBeatTimestampsSec);
        Assert.All(hl.RelativeBeatTimestampsSec, b => Assert.InRange(b, 0.0, 15.0));
        Assert.InRange(hl.ExcitementScore, 0.0, 1.0);

        // 무클릭 페이드 — 첫 샘플 0 · 마지막 샘플 0.
        var l = new float[info.Frames];
        PlutoPcmWav.DecodeChannel(hl.CroppedWavBytes, in info, 0, l);
        Assert.Equal(0f, l[0]);
        Assert.Equal(0f, l[^1]);
    }

    [Fact]
    public void HighlightExtractor_SnapsStartOffset_ToNearestDownbeat()
    {
        // 고음량 구간은 20.00s 에 시작하지만 킥은 20.15s 부터 0.5s 간격 → 원시 피크 창(20.00) 이 ±0.5s 내 최근접 비트로 이동해야 함.
        var wav = SynthTrack(45, 20, 35, beatOffset: 0.15);

        var hl = PlutoAudioHighlightExtractor.Shared.ExtractBestHighlight(wav);

        var k = Math.Round((hl.StartOffsetSec - 20.15) / 0.5);
        var nearestPulse = 20.15 + k * 0.5;
        Assert.True(Math.Abs(hl.StartOffsetSec - nearestPulse) <= 0.05, "start=" + hl.StartOffsetSec + " pulse=" + nearestPulse);
        Assert.True(Math.Abs(hl.StartOffsetSec - 20.0) > 0.05, "스냅 미적용: " + hl.StartOffsetSec);
        Assert.InRange(hl.StartOffsetSec, 19.5, 20.5);
        // 상대 비트 0 근처에 첫 비트(시작점 = 비트).
        Assert.Contains(hl.RelativeBeatTimestampsSec, b => b < 0.06);
    }

    [Fact]
    public void HighlightExtractor_ManualNudgeAndShortTrack_RespectBounds()
    {
        var ex = PlutoAudioHighlightExtractor.Shared;
        var wav = SynthTrack(45, 20, 35);

        var late = ex.ExtractWindowAt(wav, 100.0);
        Assert.Equal(30.0, late.StartOffsetSec, 3);
        Assert.Equal(45.0, late.EndOffsetSec, 3);
        Assert.Equal(ExpectedBytes(15.0), late.CroppedWavBytes.Length);

        var early = ex.ExtractWindowAt(wav, -5.0);
        Assert.Equal(0.0, early.StartOffsetSec, 3);
        Assert.Equal(ExpectedBytes(15.0), early.CroppedWavBytes.Length);

        var nudged = ex.ExtractWindowAt(wav, 21.0);
        Assert.Equal(21.0, nudged.StartOffsetSec, 3);
        Assert.Equal(ExpectedBytes(15.0), ex.ExtractWindowAt(wav, double.NaN).CroppedWavBytes.Length);

        // 15초보다 짧은 트랙 → 0초 시작 · 0 패딩으로 정확히 15.00s.
        var shortWav = SynthTrack(6, 0, 6);
        var best = ex.ExtractBestHighlight(shortWav);
        Assert.Equal(0.0, best.StartOffsetSec);
        Assert.Equal(6.0, best.TotalSourceDurationSec, 3);
        Assert.Equal(ExpectedBytes(15.0), best.CroppedWavBytes.Length);
        var manual = ex.ExtractWindowAt(shortWav, 3.0);
        Assert.Equal(0.0, manual.StartOffsetSec);
        Assert.Equal(ExpectedBytes(15.0), manual.CroppedWavBytes.Length);
        Assert.True(PlutoPcmWav.TryReadHeader(manual.CroppedWavBytes, out var info));
        var l = new float[info.Frames];
        PlutoPcmWav.DecodeChannel(manual.CroppedWavBytes, in info, 0, l);
        Assert.All(l.Skip(7 * Rate).Take(Rate), s => Assert.Equal(0f, s));   // 패딩 구간 무음

        // 모노 · 극단적으로 짧은 트랙도 예외 없음.
        var mono = PlutoPcmWav.EncodePcm16(new float[1000], default, 1, Rate);
        Assert.Equal(ExpectedBytes(15.0, 1), ex.ExtractBestHighlight(mono).CroppedWavBytes.Length);
        Assert.Throws<ArgumentException>(() => ex.ExtractBestHighlight(new byte[16]));
    }

    [Fact]
    public async Task CustomBgmService_ProcessesRawWavBytes_EndToEnd()
    {
        var wav = SynthTrack(45, 20, 35);
        var svc = new PlutoCustomBgmService(null, () => null, null, Path.Combine(_dir, "tmp"));

        var hl = await svc.LoadAndExtractHighlightAsync("song.wav", wav);
        Assert.InRange(hl.StartOffsetSec, 19.0, 21.0);
        Assert.Equal(ExpectedBytes(15.0), hl.CroppedWavBytes.Length);
        Assert.NotEmpty(hl.RelativeBeatTimestampsSec);
        Assert.True(hl.RelativeBeatTimestampsSec.Zip(hl.RelativeBeatTimestampsSec.Skip(1)).All(p => p.Second > p.First));
        Assert.InRange(hl.DetectedBpm, 115.0, 125.0);

        var nudged = await svc.LoadAndExtractHighlightAsync("song.wav", wav, hl.StartOffsetSec + 1.0);
        Assert.Equal(hl.StartOffsetSec + 1.0, nudged.StartOffsetSec, 2);

        // 압축 포맷 + FFmpeg 부재 → 안내 예외 · 용량 초과/빈 파일/미지원 확장자 → 안내 예외.
        var noFfmpeg = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LoadAndExtractHighlightAsync("song.mp3", new byte[] { 0x49, 0x44, 0x33, 0, 0, 0 }));
        Assert.Contains("FFmpeg", noFfmpeg.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LoadAndExtractHighlightAsync("big.wav", new byte[PlutoCustomBgmService.MaxUploadBytes + 1]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LoadAndExtractHighlightAsync("empty.wav", ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.LoadAndExtractHighlightAsync("doc.txt", new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.LoadAndExtractHighlightAsync("song.wav", wav, null, new CancellationToken(true)));

        // FFmpeg 디코드 경로 — 인자 계약 · 임시 파일 정리 · 같은 파일 재요청은 캐시(1회 실행).
        var runs = new List<string[]>();
        var ffSvc = new PlutoCustomBgmService(null, () => "ffmpeg.exe", (exe, args, ct) =>
        {
            var a = args.ToArray();
            runs.Add(a);
            Assert.True(File.Exists(a[Array.IndexOf(a, "-i") + 1]));
            File.WriteAllBytes(a[^1], wav);
            return Task.FromResult(new ProcessResult(true, 0, "", ""));
        }, Path.Combine(_dir, "ff"));
        var mp3 = new byte[] { 0x49, 0x44, 0x33, 4, 0, 0, 0, 0, 0, 0 };
        var a1 = await ffSvc.LoadAndExtractHighlightAsync("track.mp3", mp3);
        var a2 = await ffSvc.LoadAndExtractHighlightAsync("track.mp3", mp3, a1.StartOffsetSec - 1.0);
        Assert.Single(runs);
        var argv = runs[0];
        Assert.Equal("48000", argv[Array.IndexOf(argv, "-ar") + 1]);
        Assert.Equal("2", argv[Array.IndexOf(argv, "-ac") + 1]);
        Assert.Equal("pcm_s16le", argv[Array.IndexOf(argv, "-c:a") + 1]);
        Assert.Contains("-y", argv);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "ff")));
        Assert.Equal(a1.StartOffsetSec - 1.0, a2.StartOffsetSec, 2);

        // FFmpeg 실패 → 안내 예외 · 임시 파일 정리.
        var failSvc = new PlutoCustomBgmService(null, () => "ffmpeg.exe", (_, _, _) => Task.FromResult(new ProcessResult(true, 1, "", "Invalid data found")), Path.Combine(_dir, "fail"));
        var fail = await Assert.ThrowsAsync<InvalidOperationException>(() => failSvc.LoadAndExtractHighlightAsync("bad.ogg", mp3));
        Assert.Contains("Invalid data", fail.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "fail")));
    }

    [Fact]
    public async Task StockSuiteExporter_UsesCustomBgmWav_InMotionAndMontageExports()
    {
        var custom = PlutoAudioHighlightExtractor.Shared.ExtractBestHighlight(SynthTrack(45, 20, 35)).CroppedWavBytes;
        var fallbackBgm = Path.Combine(_dir, "lullaby.wav");
        File.WriteAllBytes(fallbackBgm, new byte[64]);   // 비정상 WAV — 커스텀 미사용 시 마스터링 실패 → loudnorm 폴백
        var outDir = Path.Combine(_dir, "out");
        var masterDir = Path.Combine(_dir, "master");
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        Func<BabyBgmTrack, double, string?> resolver = (_, _) => fallbackBgm;
        var exp = new StockSuiteExporter(() => null, () => "mp4", () => "ffmpeg.exe", runner)
        {
            BabyBgmResolver = resolver,
            DurationProbe = (_, _) => Task.FromResult(15.0),
            SfxTempDir = Path.Combine(_dir, "sfx"),
            MasteringTempDir = masterDir,
        };
        var photo = Path.Combine(_dir, "baby.jpg");
        File.WriteAllBytes(photo, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });

        // 🎥 단일 사진 모션 — 커스텀 WAV → 트루피크 마스터링 성공(loudnorm 우회) · 커스텀 라벨.
        var motion = await exp.ExportMotionVideoFromPhotoAsync(photo, PlutoMotionCompiler.SynthesizeFourAct((0.5, 0.4)), new ExportOptions { OutputDirectory = outDir },
            customBgmWavBytes: custom);
        Assert.True(motion.Ok, motion.Message);
        Assert.True(motion.HasAudio);
        Assert.Contains(StockSuiteExporter.CustomBgmLabel, motion.Message, StringComparison.Ordinal);
        var mix = calls[^1];
        Assert.DoesNotContain("loudnorm", mix[Array.IndexOf(mix, "-filter_complex") + 1], StringComparison.Ordinal);
        Assert.Same(resolver, exp.BabyBgmResolver);   // 해석기 복원
        Assert.Empty(Directory.Exists(masterDir) ? Directory.GetFiles(masterDir) : Array.Empty<string>());

        // 🎞️ 몽타주 — 동일 경로.
        calls.Clear();
        var photos = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var p = Path.Combine(_dir, "m" + i + ".jpg");
            File.WriteAllBytes(p, new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            photos.Add(p);
        }
        var montage = await exp.ExportMontageVideoFromPhotosAsync(photos, null, new ExportOptions { OutputDirectory = outDir }, customBgmWavBytes: custom);
        Assert.True(montage.Ok, montage.Message);
        Assert.True(montage.HasAudio);
        Assert.Contains(StockSuiteExporter.CustomBgmLabel, montage.Message, StringComparison.Ordinal);
        mix = calls[^1];
        Assert.DoesNotContain("loudnorm", mix[Array.IndexOf(mix, "-filter_complex") + 1], StringComparison.Ordinal);
        Assert.Same(resolver, exp.BabyBgmResolver);
        Assert.Empty(Directory.GetFiles(masterDir));

        // 커스텀 없음 → 기존 경로(비정상 BGM → 레거시 loudnorm 폴백) · 비정상 커스텀 WAV → 기본 트랙 폴백.
        calls.Clear();
        var legacy = await exp.ExportMotionVideoFromPhotoAsync(photo, PlutoMotionTrajectory.Empty, new ExportOptions { OutputDirectory = outDir }, customBgmWavBytes: new byte[] { 1, 2, 3 });
        Assert.True(legacy.Ok, legacy.Message);
        Assert.DoesNotContain(StockSuiteExporter.CustomBgmLabel, legacy.Message, StringComparison.Ordinal);
        Assert.Contains("loudnorm", calls[^1][Array.IndexOf(calls[^1], "-filter_complex") + 1], StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_And_Previewer_Wire_CustomBgm()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) d = d.Parent;
        Assert.NotNull(d);
        var root = Path.Combine(d!.FullName, "PromptFlowStudio");
        var ws = File.ReadAllText(Path.Combine(root, "Blazor", "Pages", "Workspace.razor"));
        var comp = File.ReadAllText(Path.Combine(root, "Blazor", "Components", "PlutoMotionPreviewer.razor"));
        var js = File.ReadAllText(Path.Combine(root, "wwwroot", "js", "pluto-motion-previewer.js"));
        var main = File.ReadAllText(Path.Combine(root, "MainForm.cs"));
        var maui = File.ReadAllText(Path.Combine(d.FullName, "PromptFlowStudio.Mobile", "MauiProgram.cs"));

        Assert.Contains("accept=\".mp3,.wav,.m4a,.aac,.ogg\"", ws, StringComparison.Ordinal);
        Assert.Contains("🎵 내 BGM 업로드", ws, StringComparison.Ordinal);
        Assert.Contains("◀ -1s", ws, StringComparison.Ordinal);
        Assert.Contains("+1s ▶", ws, StringComparison.Ordinal);
        Assert.Contains("✕ 기본 BGM", ws, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(ws, "CustomBgmDataUrl=\"@_customBgmUrl\""));
        Assert.Equal(2, CountOf(ws, "customBgmWavBytes: _customBgm?.CroppedWavBytes"));
        Assert.Contains("[Parameter] public string? CustomBgmDataUrl", comp, StringComparison.Ordinal);
        Assert.Contains("plutoMotionPreviewer.setCustomBgm", comp, StringComparison.Ordinal);
        Assert.Contains("plutoMotionPreviewer.clearCustomBgm", comp, StringComparison.Ordinal);
        Assert.Contains("setCustomBgm: function (root, dataUrl)", js, StringComparison.Ordinal);
        Assert.Contains("clearCustomBgm: function (root)", js, StringComparison.Ordinal);
        Assert.Contains("IPlutoCustomBgmService", main, StringComparison.Ordinal);
        Assert.Contains("IPlutoCustomBgmService", maui, StringComparison.Ordinal);
    }

    private static int CountOf(string s, string token)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(token, i, StringComparison.Ordinal)) >= 0) { n++; i += token.Length; }
        return n;
    }
}
