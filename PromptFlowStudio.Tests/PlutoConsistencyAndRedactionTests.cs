using System;
using System.Collections.Generic;
using System.IO;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🧹 일관성 패치 — 10단계 자가진단 문구 · 진단서 §1~§6 툴팁 · 예외 토스트 경로/비밀 가림 · 토글 실효 상태 · 403 재확인 · 마스터 불일치 경고.</summary>
public class PlutoConsistencyAndRedactionTests
{
    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    private static string Src(params string[] parts) => File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", Path.Combine(parts)));

    [Fact]
    public void SelfTest_StageCountWording_Is10_InSettingsAndService()
    {
        Assert.Equal(10, PlutoFlywheelSelfTestService.StageCount);
        Assert.Equal(PlutoFlywheelSelfTestService.StageCount, PlutoFlywheelSelfTestService.StageNames.Length);
        var settings = Src("Blazor", "Pages", "Settings.razor");
        var svc = Src("Services", "Pluto", "PlutoFlywheelSelfTestService.cs");
        Assert.DoesNotContain("7단계", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("7단계", svc, StringComparison.Ordinal);
        Assert.Contains("10단계를 실행하고", settings, StringComparison.Ordinal);
        Assert.Contains("1클릭 10단계 플라이휠", settings, StringComparison.Ordinal);
        Assert.Contains("10단계 실행", svc, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticCopyTooltip_ListsAllSixSections()
    {
        var settings = Src("Blazor", "Pages", "Settings.razor");
        var a = settings.IndexOf("data-pluto-diagnostic-copy=\"1\"", StringComparison.Ordinal);
        Assert.True(a >= 0);
        var tip = settings.Substring(a, settings.IndexOf('>', a) - a);
        foreach (var sec in new[] { "§1", "§2", "§3", "§4", "§5 용어집", "§6 최근 활동" })
            Assert.Contains(sec, tip, StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorToasts_RedactRawExceptionMessages()
    {
        var settings = Src("Blazor", "Pages", "Settings.razor");
        var ws = Src("Blazor", "Pages", "Workspace.razor");
        Assert.DoesNotContain("\"⚠ 진단 수집 실패: \" + ex.Message", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("\"⚠ 전 언어 일괄 실패: \" + ex.Message", ws, StringComparison.Ordinal);
        Assert.DoesNotContain("\"⚠ 전 언어 일괄 렌더 실패: \" + ex.Message", ws, StringComparison.Ordinal);
        Assert.Contains("\"⚠ 진단 수집 실패: \" + PromptFlowStudio.Services.Pluto.PlutoAutonomousActivityFeedService.Redact(ex.Message)", settings, StringComparison.Ordinal);
        Assert.Contains("\"⚠ 전 언어 일괄 실패: \" + PromptFlowStudio.Services.Pluto.PlutoAutonomousActivityFeedService.Redact(ex.Message)", ws, StringComparison.Ordinal);
        Assert.Contains("\"⚠ 전 언어 일괄 렌더 실패: \" + PromptFlowStudio.Services.Pluto.PlutoAutonomousActivityFeedService.Redact(ex.Message)", ws, StringComparison.Ordinal);

        // 헬퍼 동작: 절대 경로 → 파일명만 · 비밀값 → ***
        var leaf = "C:\\Users\\someone\\AppData\\Local\\Pluto\\catalog.json";
        var red = PlutoAutonomousActivityFeedService.Redact("Access denied: " + leaf + " key=AIzaSyA1234567890abcdefghijklmnopqrstu");
        Assert.DoesNotContain("someone", red, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData", red, StringComparison.Ordinal);
        Assert.Contains("catalog.json", red, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890abcdef", red, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishPackToasts_RedactRawExceptionMessages_AndHubInvariantsHold()
    {
        var ws = Src("Blazor", "Pages", "Workspace.razor");
        foreach (var label in new[] { "⚠ 제목 복사 실패: ", "⚠ 패키지 저장 실패: ", "⚠ 업로드 패키지 생성 실패: " })
        {
            Assert.DoesNotContain("\"" + label + "\" + ex.Message", ws, StringComparison.Ordinal);
            Assert.Contains("\"" + label + "\" + PromptFlowStudio.Services.Pluto.PlutoAutonomousActivityFeedService.Redact(ex.Message)", ws, StringComparison.Ordinal);
        }

        var red = PlutoAutonomousActivityFeedService.Redact("IOException: D:\\Users\\someone\\Videos\\pack\\upload.json Bearer abc.def-ghi");
        Assert.DoesNotContain("someone", red, StringComparison.Ordinal);
        Assert.Contains("upload.json", red, StringComparison.Ordinal);
        Assert.DoesNotContain("abc.def-ghi", red, StringComparison.Ordinal);

        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    [Fact]
    public void WorkspaceExceptionToasts_AllRouteThroughRedact_AndMaskPathsAndSecrets()
    {
        var ws = Src("Blazor", "Pages", "Workspace.razor");
        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(ws, @"(?<!Redact\()\bex\.Message\b"),
            "Workspace.razor에 Redact를 거치지 않은 ex.Message가 남아 있음");
        foreach (var label in new[]
        {
            "⚠ 몽타주 렌더 실패: ", "⚠ A/B 변형 생성 실패: ", "⚠ 승자 판정 실패: ",
            "⚠ 시리즈 분할 실패: ", "⚠ 시리즈 일괄 내보내기 실패: ", "⚠ 회수 실패: ", "⚠ 재시도 실패: ",
        })
        {
            Assert.DoesNotContain("\"" + label + "\" + ex.Message", ws, StringComparison.Ordinal);
            Assert.Contains("\"" + label + "\" + PromptFlowStudio.Services.Pluto.PlutoAutonomousActivityFeedService.Redact(ex.Message)", ws, StringComparison.Ordinal);
        }

        var red = PlutoAutonomousActivityFeedService.Redact("Render failed: C:\\Users\\someone\\Videos\\montage\\out.mp4 token=ya29.A0abcdefghijklmnopqrstuvwxyz0123456789");
        Assert.DoesNotContain("someone", red, StringComparison.Ordinal);
        Assert.Contains("out.mp4", red, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123456789", red, StringComparison.Ordinal);

        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    [Fact]
    public void Toggles_BindToEffectiveRuntimeState_NotRawPreference()
    {
        var settings = Src("Blazor", "Pages", "Settings.razor");
        Assert.DoesNotContain("[⏱️ API 주기 자동 동기화 ON]", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("[📊 CTR · 유지율 정밀 동기화 ON]", settings, StringComparison.Ordinal);
        Assert.Contains("_ytAuto?.ToggleStateKorean", settings, StringComparison.Ordinal);
        Assert.Contains("ypr.ToggleStateKorean", settings, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"@(ypr.EffectiveOn", settings, StringComparison.Ordinal);
        Assert.Contains("aria-pressed=\"@(_ytAuto?.EffectiveOn", settings, StringComparison.Ordinal);

        static PlutoYouTubeApiAutoSyncStatus Auto(bool on, PlutoYouTubeApiAutoSyncPhase ph)
            => new(on, false, ph, null, 0, null, null, 0, 0, 0, TimeSpan.FromMinutes(30));
        Assert.False(Auto(true, PlutoYouTubeApiAutoSyncPhase.Unavailable).EffectiveOn);
        Assert.Equal("ON(중지됨 · 사용 불가)", Auto(true, PlutoYouTubeApiAutoSyncPhase.Unavailable).ToggleStateKorean);
        Assert.Equal("ON", Auto(true, PlutoYouTubeApiAutoSyncPhase.Scheduled).ToggleStateKorean);
        Assert.Equal("OFF", Auto(false, PlutoYouTubeApiAutoSyncPhase.Off).ToggleStateKorean);

        static PlutoYouTubeAnalyticsPrecisionStatus Prec(PlutoYouTubeAnalyticsPrecisionMode m, bool on = true) => new(m, on, 0, 1, null, null, 0);
        Assert.False(Prec(PlutoYouTubeAnalyticsPrecisionMode.Disabled).EffectiveOn);
        Assert.False(Prec(PlutoYouTubeAnalyticsPrecisionMode.NotConfigured).EffectiveOn);
        Assert.True(Prec(PlutoYouTubeAnalyticsPrecisionMode.Active).EffectiveOn);
        Assert.Contains("킬 스위치", Prec(PlutoYouTubeAnalyticsPrecisionMode.Disabled).BadgeKorean, StringComparison.Ordinal);
        Assert.Equal("OFF", Prec(PlutoYouTubeAnalyticsPrecisionMode.Active, on: false).ToggleStateKorean);
    }

    [Fact]
    public void ScopeModeAfterLatch_ExpiredLatchWithoutSuccess_IsRecheckingNotActive()
    {
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.AwaitingScope, PlutoYouTubeApiSyncService.ScopeModeAfterLatch(1, 0, 1));
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.RecheckingScope, PlutoYouTubeApiSyncService.ScopeModeAfterLatch(0, 1, 1));
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.RecheckingScope, PlutoYouTubeApiSyncService.ScopeModeAfterLatch(1, 1, 2));
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Active, PlutoYouTubeApiSyncService.ScopeModeAfterLatch(0, 1, 2));   // 한 채널은 범위 OK
        Assert.Equal(PlutoYouTubeAnalyticsPrecisionMode.Active, PlutoYouTubeApiSyncService.ScopeModeAfterLatch(0, 0, 1));
        var badge = new PlutoYouTubeAnalyticsPrecisionStatus(PlutoYouTubeAnalyticsPrecisionMode.RecheckingScope, true, 0, 1, null, null, 0);
        Assert.Equal(3, badge.Severity);
        Assert.DoesNotContain("활성", badge.BadgeKorean, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchRender_WorkspaceMismatch_IsSurfaced()
    {
        var photos = new List<string> { "a.jpg", "b.jpg" };
        var cues = new[] { new SubtitleCue(0, 2, "안녕"), new SubtitleCue(2, 4, "숲") };
        var fp = PlutoMultiLocaleBatchRenderService.ComputeWorkspaceFingerprint(photos, cues);
        Assert.Equal(fp, PlutoMultiLocaleBatchRenderService.ComputeWorkspaceFingerprint(new[] { "a.jpg", "b.jpg" }, new[] { new SubtitleCue(0, 2, "안녕"), new SubtitleCue(2, 4, "숲") }));
        var changedCue = PlutoMultiLocaleBatchRenderService.ComputeWorkspaceFingerprint(photos, new[] { new SubtitleCue(0, 2, "안녕"), new SubtitleCue(2, 4, "바다") });
        var reordered = PlutoMultiLocaleBatchRenderService.ComputeWorkspaceFingerprint(new[] { "b.jpg", "a.jpg" }, cues);
        Assert.NotEqual(fp, changedCue);
        Assert.NotEqual(fp, reordered);

        Assert.Null(PlutoMultiLocaleBatchRenderService.DescribeWorkspaceMismatch("clip1", "CLIP1", fp, fp));
        var changed = PlutoMultiLocaleBatchRenderService.DescribeWorkspaceMismatch("clip1", "clip1", fp, changedCue);
        Assert.NotNull(changed);
        Assert.StartsWith(PlutoMultiLocaleBatchRenderService.WorkspaceMismatchPrefix, changed, StringComparison.Ordinal);
        Assert.Contains("자막/미디어", changed, StringComparison.Ordinal);
        Assert.StartsWith(PlutoMultiLocaleBatchRenderService.WorkspaceMismatchPrefix, PlutoMultiLocaleBatchRenderService.DescribeWorkspaceMismatch("clip2", "clip1", fp, fp), StringComparison.Ordinal);
        Assert.NotNull(PlutoMultiLocaleBatchRenderService.DescribeWorkspaceMismatch("clip1", null, null, fp));

        var ws = Src("Blazor", "Pages", "Workspace.razor");
        Assert.Contains("DescribeWorkspaceMismatch(clip.VideoId, _renderedMasterVideoId, _renderedMasterFingerprint", ws, StringComparison.Ordinal);
        Assert.Contains("mismatch + \" · \" + result.SummaryKorean", ws, StringComparison.Ordinal);
    }
}
