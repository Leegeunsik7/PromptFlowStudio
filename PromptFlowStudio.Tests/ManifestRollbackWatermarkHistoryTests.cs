using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🧾 매니페스트 엄격 매칭/↩️ 롤백 · 🖥️ 4K+📺 워터마크 · 🕒 프롬프트 타임머신 회귀 잠금.</summary>
public class ManifestRollbackWatermarkHistoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_mrwh_" + Guid.NewGuid().ToString("N"));

    public ManifestRollbackWatermarkHistoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string rel, int len, byte fill)
    {
        var p = Path.GetFullPath(Path.Combine(_dir, rel));
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var b = new byte[len];
        Array.Fill(b, fill);
        File.WriteAllBytes(p, b);
        return p;
    }

    /// <summary>ffmpeg 부재 → 원본 복사 경로(바이트 동일).</summary>
    private StockSuiteExporter CopyExporter(string target)
        => new(() => target, null, () => null) { SwapRetryDelay = TimeSpan.FromMilliseconds(1), SilentContainerVerifier = static _ => PromptFlowStudio.Services.Export.SilentVerdict.Silent };

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_WritesStrictManifest_AndPreventsFalseMatching()
    {
        var target = Path.Combine(_dir, "ss");
        Directory.CreateDirectory(target);
        var src = Write("lib/w_20261105_120000.mp4", 500, 7);
        var exp = CopyExporter(target);
        var r = await exp.ExportAsync(src);
        Assert.True(r.Ok, r.Message);

        var m = StockSuiteExporter.TryReadManifest(target);
        Assert.NotNull(m);
        Assert.Equal("bg_ambient.mp4", m!.ActiveAsset);
        Assert.Equal("w_20261105_120000", m.SourceRenderId);
        Assert.Equal(StockSuiteExporter.ComputeSha256Hex(src), m.SourceSha256);
        Assert.Equal(64, m.SourceSha256.Length);
        Assert.Null(m.BackupAsset);

        // 활성 에셋을 렌더 결과(내용 다름)로 교체 — 원본 마커는 무효화(삭제).
        File.WriteAllBytes(Path.Combine(target, "bg_ambient.mp4"), Enumerable.Repeat((byte)9, 777).ToArray());
        File.Delete(Path.Combine(target, "bg_ambient.mp4" + StockSuiteExporter.SourceMarkerSuffix));

        // 오탐 후보: 이름만 같은 갤러리 파일 · 렌더 ID 동일하나 내용(SHA) 다른 파일
        var sameName = Write("gal/bg_ambient.mp4", 300, 3);
        var sameRenderIdWrongSha = Write("gal/w_20261105_120000.mp4", 500, 8);
        // 진짜 출처(사본) — 매니페스트 SHA 일치
        var genuine = Write("gal/copy_of_source.mp4", 500, 7);

        var svc = new GalleryMediaService(() => target);
        var items = svc.ScanGalleryItems(Path.Combine(_dir, "gal"));
        Assert.False(items.Single(i => i.FilePath == sameName).IsExported);
        Assert.False(items.Single(i => i.FilePath == sameRenderIdWrongSha).IsExported);
        Assert.True(items.Single(i => i.FilePath == genuine).IsExported);
        Assert.Equal(1, svc.LastHealedCount);

        // 손상 매니페스트 → 매칭 비활성(예외 없음)
        File.WriteAllText(Path.Combine(target, StockSuiteExporter.ManifestName), "{ not json");
        Assert.Null(StockSuiteExporter.TryReadManifest(target));
        var other = Write("gal2/x.mp4", 500, 7);
        var items2 = new GalleryMediaService(() => target).ScanGalleryItems(Path.Combine(_dir, "gal2"));
        Assert.False(items2.Single(i => i.FilePath == other).IsExported);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_RollbackPreviousAsset_RestoresBackupSuccessfully()
    {
        var target = Path.Combine(_dir, "ss");
        Directory.CreateDirectory(target);
        var a = Write("lib/a.mp4", 100, 1);
        var b = Write("lib/b.mp4", 200, 2);
        var exp = CopyExporter(target);
        var active = Path.Combine(target, "bg_ambient.mp4");
        var backup = Path.Combine(target, "bg_ambient.backup.mp4");

        Assert.False(exp.CanRollback());
        Assert.False(exp.RollbackPrevious().Ok);

        Assert.True((await exp.ExportAsync(a)).Ok);
        Assert.False(File.Exists(backup));
        // 동일 출처 재내보내기 → 백업 생략
        Assert.True((await exp.ExportAsync(a)).Ok);
        Assert.False(File.Exists(backup));

        Assert.True((await exp.ExportAsync(b)).Ok);
        Assert.True(File.Exists(backup));
        Assert.Equal(100, new FileInfo(backup).Length);
        Assert.Equal(200, new FileInfo(active).Length);
        var m = StockSuiteExporter.TryReadManifest(target)!;
        Assert.Equal("b", m.SourceRenderId);
        Assert.Equal("bg_ambient.backup.mp4", m.BackupAsset);
        Assert.Equal("a", m.BackupRenderId);
        Assert.True(exp.CanRollback());
        Assert.True(exp.IsApplied(b));

        var rb = exp.RollbackPrevious();
        Assert.True(rb.Ok, rb.Message);
        Assert.Equal(100, new FileInfo(active).Length);
        Assert.Equal(200, new FileInfo(backup).Length);
        m = StockSuiteExporter.TryReadManifest(target)!;
        Assert.Equal("a", m.SourceRenderId);
        Assert.Equal(StockSuiteExporter.ComputeSha256Hex(a), m.SourceSha256);
        Assert.Equal("b", m.BackupRenderId);
        Assert.True(exp.IsApplied(a));
        Assert.False(exp.IsApplied(b));
        Assert.True(File.Exists(Path.Combine(target, StockSuiteExporter.HotReloadTriggerName)));

        // 재롤백 = 되돌리기(맞교환)
        Assert.True(exp.RollbackPrevious().Ok);
        Assert.Equal(200, new FileInfo(active).Length);
        Assert.Equal("b", StockSuiteExporter.TryReadManifest(target)!.SourceRenderId);

        // 백업은 갤러리 치유 대상이 아님 — a 사본은 백업과만 일치하므로 미표시
        var aCopy = Write("gal/a_copy.mp4", 100, 1);
        var items = new GalleryMediaService(() => target).ScanGalleryItems(Path.Combine(_dir, "gal"));
        Assert.False(items.Single(i => i.FilePath == aCopy).IsExported);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task StockSuiteExporter_Builds4KUhdAndWatermarkFilters_WhenRequested()
    {
        Assert.Null(StockSuiteExporter.BuildVideoFilter(false, false, StockSuiteResolution.Fhd1080, false, null));
        Assert.Equal(StockSuiteExporter.SharpenFilter, StockSuiteExporter.BuildVideoFilter(true, false, StockSuiteResolution.Fhd1080, false, null));
        Assert.Equal("scale=3840:2160:flags=lanczos", StockSuiteExporter.BuildVideoFilter(false, false, StockSuiteResolution.Uhd4K, false, null));
        Assert.StartsWith("scale=3840:2160:flags=lanczos,unsharp", StockSuiteExporter.BuildVideoFilter(true, false, StockSuiteResolution.Uhd4K, false, null));

        var wm = StockSuiteExporter.BuildWatermarkFilter(@"C:\Windows\Fonts\arial.ttf");
        Assert.Contains("fontfile='C\\:/Windows/Fonts/arial.ttf'", wm, StringComparison.Ordinal);
        Assert.Contains("text='ANG QUANT LAB · REALTIME STREAM':x=40:y=40:fontsize=24:fontcolor=white@0.7:box=1:boxcolor=black@0.4:boxborderw=6", wm, StringComparison.Ordinal);
        Assert.Contains("text='● LIVE KST':x=w-tw-40", wm, StringComparison.Ordinal);
        Assert.DoesNotContain("fontfile", StockSuiteExporter.BuildWatermarkFilter(null), StringComparison.Ordinal);
        Assert.Contains("fontsize=48", StockSuiteExporter.BuildWatermarkFilter(null, StockSuiteResolution.Uhd4K), StringComparison.Ordinal);

        var full = StockSuiteExporter.BuildVideoFilter(true, true, StockSuiteResolution.Uhd4K, true, null)!;
        var iScale = full.IndexOf("scale=3840", StringComparison.Ordinal);
        var iMotion = full.IndexOf("minterpolate", StringComparison.Ordinal);
        var iText = full.IndexOf("drawtext", StringComparison.Ordinal);
        Assert.True(iScale >= 0 && iScale < iMotion && iMotion < iText, full);

        // 실제 결선: 워터마크 체인 실패 → 워터마크 제외 체인으로 재시도
        var target = Path.Combine(_dir, "ss");
        Directory.CreateDirectory(target);
        var src = Write("lib/s.mp4", 64, 4);
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            lock (calls) calls.Add(arr);
            var vf = Array.IndexOf(arr, "-vf");
            if (vf >= 0 && arr[vf + 1].Contains("drawtext", StringComparison.Ordinal)) return Task.FromResult(false);
            File.WriteAllBytes(arr[^1], new byte[4096]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => target, null, () => "ffmpeg.exe", runner) { SwapRetryDelay = TimeSpan.FromMilliseconds(1) };
        var r = await exp.ExportAsync(src, sharpen: true, smooth60: false, resolution: StockSuiteResolution.Uhd4K, watermark: true);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("4K UHD", r.Message, StringComparison.Ordinal);
        Assert.Contains("워터마크 생략", r.Message, StringComparison.Ordinal);
        Assert.Equal(2, calls.Count);
        Assert.Contains("drawtext", calls[0][Array.IndexOf(calls[0], "-vf") + 1], StringComparison.Ordinal);
        Assert.Equal(StockSuiteExporter.Uhd4KSharpenFilter, calls[1][Array.IndexOf(calls[1], "-vf") + 1]);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PromptHistoryService_PushesAndRestoresSnapshotsCorrectly()
    {
        var t = new DateTimeOffset(2026, 11, 5, 9, 15, 0, TimeSpan.FromHours(9));
        var h = new PromptHistoryService { Clock = () => t };
        var raised = 0;
        h.Changed += () => raised++;
        h.Changed += () => throw new InvalidOperationException("구독자 예외 격리");

        Assert.False(h.Push("빈", "  ", null, 1, false));
        Assert.True(h.Push("원본 한글", "노을 해변", "Pan Left", 11, false));
        Assert.False(h.Push("중복", "노을 해변", "Pan Left", 11, false));   // 직전과 동일 → 무시
        Assert.Equal(1, h.Count);
        Assert.Equal(1, raised);

        for (var i = 1; i <= 12; i++)
        {
            t = t.AddMinutes(1);
            Assert.True(h.Push("v" + i, "prompt " + i, null, 100 + i, i % 2 == 0));
        }
        Assert.Equal(PromptHistoryService.Capacity, h.Count);
        var snaps = h.Snapshots;
        Assert.Equal(10, snaps.Count);
        Assert.Equal("prompt 12", snaps[0].Prompt);
        Assert.Equal("prompt 3", snaps[9].Prompt);   // 가장 오래된 원본 + v1·v2 축출
        Assert.DoesNotContain(snaps, s => s.Prompt == "노을 해변");

        var back = h.Restore(4)!;
        Assert.Equal("prompt 8", back.Prompt);
        Assert.Equal(108, back.Seed);
        Assert.True(back.SeedPinned);
        Assert.Equal("v8", back.Label);
        Assert.StartsWith("[", back.Display, StringComparison.Ordinal);
        Assert.Contains("v8]", back.Display, StringComparison.Ordinal);
        Assert.Null(h.Restore(10));
        Assert.Null(h.Restore(-1));
        Assert.Equal(10, h.Count);   // 복원은 히스토리 보존

        h.Clear();
        Assert.Equal(0, h.Count);
        Assert.Empty(h.Snapshots);
    }
}
