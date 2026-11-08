using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Prompt;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>⭐ 사용자 프리셋 영속(저장·조회·삭제) · 🔇 StockSuite HTS 제로-오디오(-an) 내보내기 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class HtsHudPresetAndZeroAudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_htsza_" + Guid.NewGuid().ToString("N"));

    public HtsHudPresetAndZeroAudioTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void PresetManagerService_SavesAndLoadsUserPresetsSuccessfully()
    {
        var path = Path.Combine(_dir, "sub", PresetManagerService.FileName);
        var svc = new PresetManagerService(path);
        var changed = 0;
        svc.Changed += () => changed++;
        Assert.Empty(svc.GetAll());

        var a = svc.Save("심야 OLED 퀀트 다크", "dark trading floor, slow dolly", "blurry, text", "슬로우 돌리", "OLED 다크", VideoDurationMode.Sec15_AmbientSlow);
        var b = svc.Save("  골든아워  ", "golden hour skyline", null, null, null, VideoDurationMode.Sec5_Single);
        Assert.Equal("골든아워", b.Name);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(2, changed);

        // 재로드(새 인스턴스) — 전 필드 영속 · 최신 저장 순
        var re = new PresetManagerService(path);
        var all = re.GetAll();
        Assert.Equal(2, all.Count);
        Assert.Equal(b.Id, all[0].Id);
        var ra = re.Find(a.Id);
        Assert.NotNull(ra);
        Assert.Equal("dark trading floor, slow dolly", ra!.Prompt);
        Assert.Equal("blurry, text", ra.NegativePrompt);
        Assert.Equal("슬로우 돌리", ra.CameraMotion);
        Assert.Equal("OLED 다크", ra.Style);
        Assert.Equal(VideoDurationMode.Sec15_AmbientSlow, ra.DurationMode);
        Assert.Equal("", all[0].NegativePrompt);

        // 동일 이름 재저장 = 덮어쓰기(Id 유지 · 중복 차단)
        var a2 = re.Save("심야 OLED 퀀트 다크", "updated prompt", "", "", "", VideoDurationMode.Sec10_Dual);
        Assert.Equal(a.Id, a2.Id);
        Assert.Equal(2, re.GetAll().Count);
        Assert.Equal("updated prompt", new PresetManagerService(path).Find(a.Id)!.Prompt);

        // 유효성
        Assert.Throws<ArgumentException>(() => re.Save(" ", "p", null, null, null, VideoDurationMode.Sec5_Single));
        Assert.Throws<ArgumentException>(() => re.Save("n", "  ", null, null, null, VideoDurationMode.Sec5_Single));

        // 삭제
        Assert.True(re.Delete(a.Id));
        Assert.False(re.Delete(a.Id));
        var after = new PresetManagerService(path).GetAll();
        Assert.Single(after);
        Assert.Equal(b.Id, after[0].Id);

        // 손상 파일 → 빈 목록 + .corrupt.bak 보존
        File.WriteAllText(path, "{ not json");
        Assert.Empty(new PresetManagerService(path).GetAll());
        Assert.True(File.Exists(path + ".corrupt.bak"));
    }

    [Fact]
    public async Task StockSuiteExporter_AppliesAudioDropOption_ForHtsOptimizedExport()
    {
        // 인자 빌더 — MP4/WebM 모두 -an 강제, 비디오는 무손실 copy
        var mp4Args = StockSuiteExporter.BuildZeroAudioArgs("in.mp4", "out.mp4");
        Assert.Contains("-an", mp4Args);
        Assert.Equal("copy", mp4Args[Array.IndexOf(mp4Args, "-c:v") + 1]);
        Assert.Contains("+faststart", mp4Args);
        Assert.Equal("out.mp4", mp4Args[^1]);
        Assert.Contains("-an", StockSuiteExporter.BuildZeroAudioArgs("in.webm", "out.webm"));
        Assert.DoesNotContain("+faststart", StockSuiteExporter.BuildZeroAudioArgs("in.webm", "out.webm"));
        Assert.Contains("-an", StockSuiteExporter.BuildWebMArgs("in.mp4", "out.webm"));

        var src = Path.Combine(_dir, "render.mp4");
        File.WriteAllBytes(src, new byte[] { 0, 0, 0, 0x18, 1, 2, 3 });
        var target = Path.Combine(_dir, "StockSuite");
        Directory.CreateDirectory(target);

        // 주입 러너 — 호출 인자 캡처 + 출력 파일 생성(무음 결과 모사)
        var calls = new List<string[]>();
        FfmpegRunner runner = (exe, args, ct) =>
        {
            var arr = args.ToArray();
            calls.Add(arr);
            File.WriteAllBytes(arr[^1], new byte[AmbientLoopService.MinValidOutputBytes + 16]);
            return Task.FromResult(true);
        };
        var exp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", runner) { SilentContainerVerifier = static _ => PromptFlowStudio.Services.Export.SilentVerdict.Silent };
        var r = await exp.ExportAsync(src);
        Assert.True(r.Ok, r.Message);
        Assert.Contains("무음", r.Message);
        var dest = Path.Combine(target, StockSuiteExporter.DefaultAssetName);
        Assert.Equal(dest, r.TargetPath);
        Assert.Equal(AmbientLoopService.MinValidOutputBytes + 16, new FileInfo(dest).Length);
        Assert.Single(calls);
        Assert.Contains("-an", calls[0]);
        Assert.Equal(src, calls[0][Array.IndexOf(calls[0], "-i") + 1]);
        Assert.Empty(Directory.GetFiles(target, "*.~pfs*"));
        Assert.True(exp.IsApplied(src));

        // ffmpeg 실패 → 원본 복사 폴백(내보내기 자체는 성공 보존)
        var failExp = new StockSuiteExporter(() => target, () => "mp4", () => "ffmpeg.exe", (_, _, _) => Task.FromResult(false)) { SilentContainerVerifier = static _ => PromptFlowStudio.Services.Export.SilentVerdict.Silent };
        var fb = await failExp.ExportAsync(src);
        Assert.True(fb.Ok, fb.Message);
        Assert.Equal(File.ReadAllBytes(src), File.ReadAllBytes(dest));
        Assert.Empty(Directory.GetFiles(target, "*.~pfs*"));

        // ffmpeg 부재 → 원본 복사 폴백
        var none = await new StockSuiteExporter(() => target, () => "mp4", () => null, runner) { SilentContainerVerifier = static _ => PromptFlowStudio.Services.Export.SilentVerdict.Silent }.ExportAsync(src);
        Assert.True(none.Ok, none.Message);
        Assert.Single(calls);
    }
}
