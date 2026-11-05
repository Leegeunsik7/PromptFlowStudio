using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.ProcessBridge;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

public class PerfProGpuProcessLoudnormTests
{
    private const string Listing =
        "Encoders:\n V..... = Video\n ------\n V....D libx264              libx264 H.264 / AVC\n V....D h264_amf             AMD AMF H.264 Encoder\n V....D h264_nvenc           NVIDIA NVENC H.264 encoder\n V....D h264_qsv             H.264 (Intel Quick Sync Video acceleration)\n V....D hevc_nvenc           NVIDIA NVENC hevc encoder\n";

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task HardwareAccelDetector_DetectsAvailableEncoders_OrFallsBackToCpu()
    {
        // 목록 파싱 — 우선순위 NVENC → QSV → AMF, 부분 문자열 오탐 없음
        Assert.Equal(new[] { GpuEncoderVendor.Nvenc, GpuEncoderVendor.Qsv, GpuEncoderVendor.Amf }, HardwareAccelDetector.ParseCandidates(Listing));
        Assert.Empty(HardwareAccelDetector.ParseCandidates(" V....D libx264  H.264\n"));
        Assert.Empty(HardwareAccelDetector.ParseCandidates(null));
        Assert.False(HardwareAccelDetector.ListsEncoder(" V....D h264_nvenc_x  x\n", "h264_nvenc"));
        Assert.Equal("hevc_nvenc", HardwareAccelDetector.HevcEncoder(GpuEncoderVendor.Nvenc));

        // NVENC 검증 실패 → QSV 검증 성공 채택(1회 프로브 캐시)
        int lists = 0;
        var verified = new List<string>();
        var d = new HardwareAccelDetector(() => "ffmpeg.exe",
            (_, _) => { lists++; return Task.FromResult<string?>(Listing); },
            (_, args, _) => { var enc = args[Array.IndexOf((string[])args, "-c:v") + 1]; verified.Add(enc); return Task.FromResult(enc == "h264_qsv"); });
        Assert.Equal(GpuEncoderVendor.Qsv, await d.DetectAsync());
        Assert.Equal(GpuEncoderVendor.Qsv, await d.DetectAsync());
        Assert.Equal(1, lists);
        Assert.Equal(new[] { "h264_nvenc", "h264_qsv" }, verified);

        // 실인코딩 실패 강등 → CPU
        d.MarkFailed();
        Assert.Equal(GpuEncoderVendor.Cpu, d.Active);

        // ffmpeg 부재 / 목록 예외 / 전원 검증 실패 → CPU
        Assert.Equal(GpuEncoderVendor.Cpu, await new HardwareAccelDetector(() => null).DetectAsync());
        Assert.Equal(GpuEncoderVendor.Cpu, await new HardwareAccelDetector(() => "ff", (_, _) => throw new IOException("x")).DetectAsync());
        Assert.Equal(GpuEncoderVendor.Cpu, await new HardwareAccelDetector(() => "ff", (_, _) => Task.FromResult<string?>(Listing), (_, _, _) => Task.FromResult(false)).DetectAsync());

        // 인코더 인자 바인딩 — NVENC p4/cq22 · CPU 는 기존 libx264 인자 그대로
        var nv = string.Join(' ', StockSuiteExporter.BuildSharpenArgs("in.mp4", "out.mp4", "fps=60", GpuEncoderVendor.Nvenc));
        Assert.Contains("-c:v h264_nvenc -preset p4", nv);
        Assert.Contains("-cq 22", nv);
        Assert.Contains("-an -sn -dn -movflags +faststart out.mp4", nv);
        Assert.Equal(StockSuiteExporter.BuildSharpenArgs("in.mp4", "out.mp4", "fps=60"), StockSuiteExporter.BuildSharpenArgs("in.mp4", "out.mp4", "fps=60", GpuEncoderVendor.Cpu));
        Assert.Contains("h264_qsv", HardwareAccelDetector.VideoCodecArgs(GpuEncoderVendor.Qsv));
        Assert.Contains("h264_amf", HardwareAccelDetector.VideoCodecArgs(GpuEncoderVendor.Amf));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_GpuEncodeFailure_FallsBackToLibx264()
    {
        var root = Path.Combine(Path.GetTempPath(), "pfs-gpu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var src = Path.Combine(root, "src.mp4");
            File.WriteAllBytes(src, new byte[4096]);
            var dest = Path.Combine(root, "dest");
            var encoders = new List<string>();
            FfmpegRunner runner = (_, args, _) =>
            {
                var enc = args[Array.IndexOf((string[])args, "-c:v") + 1];
                encoders.Add(enc);
                if (enc == "h264_nvenc") return Task.FromResult(false);
                File.WriteAllBytes(args[^1], new byte[8192]);
                return Task.FromResult(true);
            };
            var accel = new HardwareAccelDetector(() => "ff", (_, _) => Task.FromResult<string?>(Listing), (_, _, _) => Task.FromResult(true));
            var ex = new StockSuiteExporter(() => dest, null, () => "ff", runner, sharpenProvider: () => true) { Accel = accel };
            var r = await ex.ExportAsync(src);
            Assert.True(r.Ok, r.Message);
            Assert.Equal("h264_nvenc", encoders[0]);
            Assert.Equal("libx264", encoders[1]);
            Assert.Equal(GpuEncoderVendor.Cpu, accel.Active);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void StockSuiteProcessMonitor_DetectsProcessState_AndHandlesNonExistentProcess()
    {
        Assert.Equal("StockSuite", StockSuiteProcessMonitor.ResolveProcessName(null));
        Assert.Equal("MyHts", StockSuiteProcessMonitor.ResolveProcessName("\"C:\\Apps\\MyHts.exe\""));

        var snap = StockSuiteProcessSnapshot.NotRunning;
        IntPtr activated = IntPtr.Zero;
        string? probedName = null;
        using var mon = new StockSuiteProcessMonitor(() => @"C:\x\StockSuite.exe", n => { probedName = n; return snap; }, h => { activated = h; return true; });
        int changes = 0;
        mon.OnChanged += () => changes++;

        Assert.False(mon.Refresh());                 // 미실행 → 미실행: 변화 없음
        Assert.False(mon.IsRunning);
        Assert.False(mon.BringStockSuiteToFront());  // 미실행 창 활성화 → false
        Assert.Equal("StockSuite", probedName);

        snap = new StockSuiteProcessSnapshot(true, 4242, 300L << 20, (IntPtr)0x1234);
        Assert.True(mon.Refresh());
        Assert.Equal(4242, mon.Current.Pid);
        Assert.Equal(300, mon.Current.WorkingSetMb, 3);
        Assert.False(mon.Refresh());                 // 동일 상태 → 재통지 없음
        Assert.True(mon.BringStockSuiteToFront());
        Assert.Equal((IntPtr)0x1234, activated);
        Assert.Equal(1, changes);

        // 조회 예외 격리 → 미실행 처리
        using var bad = new StockSuiteProcessMonitor(null, _ => throw new InvalidOperationException("access denied"));
        Assert.False(bad.Refresh());
        Assert.False(bad.IsRunning);

        // 실제 OS 조회 — 존재하지 않는 프로세스 이름은 예외 없이 미실행
        using var real = new StockSuiteProcessMonitor(() => "pfs_nonexistent_" + Guid.NewGuid().ToString("N") + ".exe");
        real.Refresh();
        Assert.False(real.IsRunning);
        Assert.False(real.BringStockSuiteToFront());

        // Start/Stop/Dispose 멱등
        real.Start(); real.Start();
        Assert.True(real.IsMonitoring);
        real.Stop();
        Assert.False(real.IsMonitoring);
        real.Dispose(); real.Dispose();
        real.Start();
        Assert.False(real.IsMonitoring);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void AudioMuxService_AppliesLoudnormFilter_WhenMuxingSnsShorts()
    {
        var args = AudioMuxService.BuildMuxArgs("in.mp4", "bgm.wav", 10, "out_sns.mp4");
        var af = args[Array.IndexOf(args, "-af") + 1];
        Assert.Equal("loudnorm=I=-14:LRA=11:TP=-1.5,afade=t=out:st=8.5:d=1.5", af);
        Assert.Equal("48000", args[Array.IndexOf(args, "-ar") + 1]);
        Assert.Equal("copy", args[Array.IndexOf(args, "-c:v") + 1]);

        var unknown = AudioMuxService.BuildMuxArgs("a", "b", 0, "c");
        Assert.Equal(AudioMuxService.LoudnormFilter, unknown[Array.IndexOf(unknown, "-af") + 1]);
        Assert.DoesNotContain("-t", unknown);
    }
}
