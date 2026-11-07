using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

public class EnterprisePolishTests
{
    private const string Listing = "Encoders:\n V....D libx264  libx264 H.264\n V....D h264_nvenc  NVIDIA NVENC H.264 encoder\n";

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_AutomaticallyFallsBackToCpu_WhenGpuEncodeFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "pfs-gpufb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var src = Path.Combine(root, "src.mp4");
            File.WriteAllBytes(src, new byte[4096]);
            var dest = Path.Combine(root, "dest");
            var calls = new List<string[]>();
            FfmpegRunner runner = (_, args, _) =>
            {
                var a = (string[])args;
                calls.Add(a);
                // NV_ENC_ERR_OUT_OF_MEMORY 시뮬레이션 — GPU 인코드는 exit 1
                if (a[Array.IndexOf(a, "-c:v") + 1] == "h264_nvenc") return Task.FromResult(false);
                File.WriteAllBytes(a[^1], new byte[8192]);
                return Task.FromResult(true);
            };
            var accel = new HardwareAccelDetector(() => "ff", (_, _) => Task.FromResult<string?>(Listing), (_, _, _) => Task.FromResult(true));
            var ex = new StockSuiteExporter(() => dest, null, () => "ff", runner, sharpenProvider: () => true) { Accel = accel };

            var r = await ex.ExportAsync(src);

            Assert.True(r.Ok, r.Message);
            Assert.EndsWith(StockSuiteExporter.CpuFallbackTag, r.Message, StringComparison.Ordinal);
            Assert.Equal(2, calls.Count);
            var cpu = calls[1];
            Assert.Equal("libx264", cpu[Array.IndexOf(cpu, "-c:v") + 1]);
            Assert.Equal("medium", cpu[Array.IndexOf(cpu, "-preset") + 1]);
            Assert.Equal("20", cpu[Array.IndexOf(cpu, "-crf") + 1]);
            Assert.Equal(GpuEncoderVendor.Cpu, accel.Active);
            Assert.True(File.Exists(r.TargetPath));

            // 이후 세션은 CPU 고정 — 일반 CPU 경로는 폴백 표식 없음
            var r2 = await ex.ExportAsync(src);
            Assert.True(r2.Ok, r2.Message);
            Assert.DoesNotContain(StockSuiteExporter.CpuFallbackTag, r2.Message, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void StockSuiteExporter_CpuFallbackArgs_UseSafeLibx264Preset()
    {
        var a = StockSuiteExporter.BuildCpuFallbackArgs("in.mp4", "out.mp4", StockSuiteExporter.SharpenFilter, StockSuiteResolution.Ultra21x9);
        Assert.Equal("libx264", a[Array.IndexOf(a, "-c:v") + 1]);
        Assert.Equal("medium", a[Array.IndexOf(a, "-preset") + 1]);
        Assert.Equal("20", a[Array.IndexOf(a, "-crf") + 1]);
        Assert.Contains("-maxrate", a);
        Assert.Equal("out.mp4", a[^1]);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task DiagnosticReportService_GeneratesValidZipBundle_WithRequiredLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), "pfs-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var log = Path.Combine(root, "shell.log");
            var sb = new StringBuilder();
            for (var i = 0; i < 700; i++) sb.Append("line-").Append(i).AppendLine();
            File.WriteAllText(log, sb.ToString());
            var accel = new HardwareAccelDetector(() => "ff", (_, _) => Task.FromResult<string?>(Listing), (_, _, _) => Task.FromResult(true));
            var svc = new DiagnosticReportService(accel, null, () => log,
                () => new[] { new KeyValuePair<string, string?>("openai.apiKey", "sk-live-SECRET"), new KeyValuePair<string, string?>("stocksuite.format", "mp4") },
                () => null)
            { UtcNow = () => new DateTimeOffset(2026, 11, 8, 9, 30, 15, TimeSpan.Zero) };

            var zip = await svc.GenerateReportBundleAsync(Path.Combine(root, "out"));

            Assert.Equal("pfs_diagnostic_20261108_093015.zip", Path.GetFileName(zip));
            Assert.Null(DiagnosticReportService.VerifyBundle(zip));
            using (var z = ZipFile.OpenRead(zip))
            {
                string Read(string n) { using var r = new StreamReader(z.GetEntry(n)!.Open()); return r.ReadToEnd(); }
                var sys = Read(DiagnosticReportService.SystemEntry);
                Assert.Contains("os:", sys);
                Assert.Contains("os_arch:", sys);
                var logs = Read(DiagnosticReportService.LogEntry);
                Assert.Contains("line-699", logs);
                Assert.Contains("line-200", logs);
                Assert.DoesNotContain("line-199" + Environment.NewLine, logs);
                Assert.Contains("h264_nvenc", Read(DiagnosticReportService.EncoderEntry));
                var cfg = Read(DiagnosticReportService.ConfigEntry);
                Assert.DoesNotContain("sk-live-SECRET", cfg);
                Assert.Contains(DiagnosticReportService.Redacted, cfg);
                Assert.Contains("mp4", cfg);
                Assert.Contains("not configured", Read(DiagnosticReportService.HealthEntry));
            }

            // 변조 감지 — 엔트리 내용 바꾸면 무결성 실패
            using (var z = ZipFile.Open(zip, ZipArchiveMode.Update))
            {
                z.GetEntry(DiagnosticReportService.SystemEntry)!.Delete();
                using var w = new StreamWriter(z.CreateEntry(DiagnosticReportService.SystemEntry).Open());
                w.Write("tampered");
            }
            Assert.NotNull(DiagnosticReportService.VerifyBundle(zip));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
