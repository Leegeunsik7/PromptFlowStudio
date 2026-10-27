using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Commercial;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>커머셜 브랜드 스튜디오 — 브랜드 톤/디스클레이머 인젝션 · 포맷 프리셋 길이 · 렌더 큐 전송 회귀 잠금.</summary>
public class CommercialBrandTests
{
    [Fact]
    public void SynthesizeCommercialPrompt_InjectsBrandToneAndDisclaimer()
    {
        var svc = new CommercialBrandService();
        var kit = new BrandKitProfile("루미에르", @"C:\brand\logo.png", WatermarkPlacement.TopRight, "#ff3366", "유료 광고 포함 · 심의필");
        var spec = CommercialBrandService.Preset(CommercialAdFormat.Feed15s, ctaText: "앱 다운로드");

        var pkg = svc.SynthesizeCommercialPrompt("저자극 수분 선크림", kit, spec);

        Assert.StartsWith("Wan2.1", pkg.Prompt);
        Assert.Contains("루미에르", pkg.Prompt);
        Assert.Contains("저자극 수분 선크림", pkg.Prompt);
        Assert.Contains("#FF3366", pkg.Prompt);   // 대문자 정규화 후 컬러 톤 인젝션
        Assert.Contains("15-second social feed reel", pkg.Prompt);
        Assert.Contains("top-right corner", pkg.Prompt);
        Assert.Contains("유료 광고 포함 · 심의필", pkg.Prompt);
        Assert.Contains("앱 다운로드", pkg.Prompt);

        Assert.Equal(@"C:\brand\logo.png", pkg.Overlay.LogoImagePath);
        Assert.Equal(WatermarkPlacement.TopRight, pkg.Overlay.Placement);
        Assert.Equal("유료 광고 포함 · 심의필", pkg.Overlay.DisclaimerText);
        Assert.Equal("#FF3366", pkg.Overlay.ColorHex);
        Assert.Equal(pkg.Prompt, pkg.Job.Prompt);
        Assert.Equal(BatchJobStatus.Pending, pkg.Job.Status);

        // 디스클레이머 미입력 → 기본 표기 · 잘못된 헥스 → 기본 컬러 · 시드 결정적
        var bare = svc.SynthesizeCommercialPrompt("저자극 수분 선크림", kit with { DisclaimerText = " ", BrandPrimaryColorHex = "zz" }, spec);
        Assert.Equal(CommercialBrandService.DefaultDisclaimer, bare.Overlay.DisclaimerText);
        Assert.Equal(CommercialBrandService.DefaultColorHex, bare.Overlay.ColorHex);
        Assert.Equal(pkg.Job.Seed, bare.Job.Seed);
        Assert.Equal("#AABBCC", CommercialBrandService.NormalizeHex("abc"));
    }

    [Theory]
    [InlineData(CommercialAdFormat.Bumper6s, 6.0f, CommercialBrandService.Vertical, "1080x1920")]
    [InlineData(CommercialAdFormat.Feed15s, 15.0f, CommercialBrandService.Vertical, "1080x1920")]
    [InlineData(CommercialAdFormat.Narrative30s, 30.0f, CommercialBrandService.Landscape, "1920x1080")]
    public void AdFormatPresets_ValidateCorrectDurations(CommercialAdFormat format, float seconds, string aspect, string resolution)
    {
        var spec = CommercialBrandService.Preset(format);
        Assert.Equal(seconds, spec.TargetDurationSec);
        Assert.Equal(seconds, CommercialBrandService.DurationFor(format));
        Assert.Equal(aspect, spec.AspectRatios);

        var pkg = new CommercialBrandService().SynthesizeCommercialPrompt("컨셉",
            new BrandKitProfile("B", "", WatermarkPlacement.BottomRight, "#000000", ""), spec);
        Assert.Equal(seconds, pkg.Job.DurationSec);
        Assert.Equal(resolution, pkg.Job.Resolution);
        Assert.Equal("1080x1080", CommercialBrandService.ResolutionFor(CommercialBrandService.Square));
    }

    [Fact]
    public void ExportToRenderQueue_EnqueuesCommercialJobWithCorrectMetadata()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var vm = new CommercialStudioViewModel(new CommercialBrandService(), q)
        {
            BrandName = "루미에르",
            PrimaryColorHex = "#FF3366",
            SelectedPlacement = WatermarkPlacement.BottomRight,
            DisclaimerText = "유료 광고 포함",
            ProductConcept = "저자극 수분 선크림",
            SelectedFormat = CommercialAdFormat.Narrative30s,
        };
        var changed = 0;
        vm.Changed += () => changed++;

        Assert.Equal(CommercialBrandService.Landscape, vm.AspectRatio);   // 포맷 → 기본 화면비 동기화
        vm.ToggleAspect();
        Assert.Equal(CommercialBrandService.Vertical, vm.AspectRatio);

        var pkg = vm.GenerateAdPrompt();
        Assert.True(vm.ExportToRenderQueue());

        var job = Assert.Single(q.Queue);
        Assert.Equal(pkg.Job.JobId, job.JobId);
        Assert.Equal(BatchJobStatus.Queued, job.Status);
        Assert.Equal(30.0f, job.DurationSec);
        Assert.Equal("1080x1920", job.Resolution);
        Assert.Contains("루미에르", job.Prompt);
        Assert.Contains("유료 광고 포함", job.Prompt);
        Assert.Equal(1, vm.DispatchedCount);

        // 재전송은 새 JobId 로 재합성 — 큐 중복 차단에 걸리지 않음
        Assert.True(vm.ExportToRenderQueue());
        Assert.Equal(2, q.Queue.Count);
        Assert.NotEqual(q.Queue[0].JobId, q.Queue[1].JobId);
        Assert.Equal(2, vm.DispatchedCount);
        Assert.True(changed >= 4);
    }
}
