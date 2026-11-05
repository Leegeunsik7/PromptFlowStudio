using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.ProcessBridge;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Video;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>핵심 비디오 서비스 IProcessRunner 단일 게이트웨이 · 무손실 트리머 · KRX 4단계 세션 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class ProcessRunnerGovernanceTests : IDisposable
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_prg_" + Guid.NewGuid().ToString("N"));

    public ProcessRunnerGovernanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>호출 기록 + 마지막 인자(출력 경로)에 더미 산출물 기록.</summary>
    private sealed class RecordingRunner(bool writeOutput = true, string stdOut = "", string stdErr = "") : IProcessRunner
    {
        public readonly List<(string Exe, string[] Args)> Calls = new();

        public Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string? workingDir = null,
            Action<bool, string>? onLine = null, CancellationToken ct = default)
        {
            var a = new List<string>(args).ToArray();
            lock (Calls) Calls.Add((exe, a));
            if (writeOutput && a.Length > 0 && Path.IsPathRooted(a[^1])) File.WriteAllBytes(a[^1], new byte[4096]);
            return Task.FromResult(new ProcessResult(true, 0, stdOut, stdErr));
        }
    }

    private string Src(string name = "clip.mp4")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, new byte[2048]);
        return p;
    }

    [Fact]
    public async Task ProcessRunnerGovernance_AmbientLoop_RoutesThroughIProcessRunner()
    {
        var runner = new RecordingRunner();
        var r = await new AmbientLoopService(runner, () => "ffmpeg").RenderAsync(Src(), Path.Combine(_dir, "amb.mp4"), LoopMode.ForwardCrossfade);
        Assert.True(r.Ok);
        Assert.Single(runner.Calls);
        Assert.Equal("ffmpeg", runner.Calls[0].Exe);
        Assert.Throws<ArgumentNullException>(() => new AmbientLoopService((IProcessRunner)null!));
    }

    [Fact]
    public async Task ProcessRunnerGovernance_VideoMerge_PosterAndFrame_RouteThroughIProcessRunner()
    {
        var runner = new RecordingRunner();
        var svc = new VideoMergeService(new ConfigurationBuilder().Build(), runner, () => "ffmpeg");
        var src = Src();
        Assert.True(await svc.ExtractPosterAsync(src));
        var frame = Path.Combine(_dir, "f.jpg");
        Assert.Equal(frame, await svc.ExtractFrameAsync(src, 1.25, frame));
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, c => Assert.Equal("ffmpeg", c.Exe));
        Assert.Contains("1.25", runner.Calls[1].Args);
    }

    [Fact]
    public async Task ProcessRunnerGovernance_MediaProbe_RoutesThroughIProcessRunner()
    {
        const string json = "{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1280,\"height\":720,\"avg_frame_rate\":\"30/1\"}],\"format\":{\"format_name\":\"mov,mp4\",\"duration\":\"5.0\"}}";
        var runner = new RecordingRunner(writeOutput: false, stdOut: json);
        var svc = new MediaProbeService(() => Path.Combine(_dir, "ffmpeg.exe"), runner);
        var spec = await svc.ProbeAsync(Src());
        Assert.NotNull(spec);
        Assert.NotEmpty(runner.Calls);
        Assert.Contains("ffprobe", Path.GetFileName(runner.Calls[0].Exe), StringComparison.OrdinalIgnoreCase);
        await svc.MeasureCenterLumaAsync(Src("b.mp4"));
        Assert.Contains(runner.Calls, c => c.Exe.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessRunnerGovernance_GuardResolvesBelowNormal_DuringRegularSessionOnly()
    {
        using var guard = new TradingSafetyGuardService(() => true);
        guard.UtcNow = () => new DateTimeOffset(2026, 11, 4, 10, 0, 0, Kst);   // 수 10:00 정규장
        Assert.Equal(ProcessPriorityClass.BelowNormal, guard.ResolvePriority("ffmpeg"));
        Assert.Null(guard.ResolvePriority("python"));
        guard.UtcNow = () => new DateTimeOffset(2026, 11, 4, 16, 0, 0, Kst);   // 시간외
        Assert.Null(guard.ResolvePriority("ffmpeg"));
    }

    [Fact]
    public void VideoTrimService_BuildsLosslessCopyArguments_Correctly()
    {
        var a = VideoTrimService.BuildLosslessTrimArgs(@"C:\in put\a.mp4", @"C:\out\b.mp4", 0.5, 14.5);
        Assert.Equal(new[] { "-y", "-hide_banner", "-ss", "0.5", "-to", "14.5", "-i", @"C:\in put\a.mp4", "-c", "copy", "-avoid_negative_ts", "make_zero", @"C:\out\b.mp4" }, a);
        Assert.True(Array.IndexOf(a, "-ss") < Array.IndexOf(a, "-i"));   // 입력 seek(키프레임 고속)
        Assert.Equal("1.235", VideoTrimService.FormatSeconds(1.2345));
        Assert.Equal("0", VideoTrimService.FormatSeconds(-3));

        Assert.Null(VideoTrimService.ValidateRange(0.5, 14.5, 15));
        Assert.NotNull(VideoTrimService.ValidateRange(-1, 2));
        Assert.NotNull(VideoTrimService.ValidateRange(3, 3.05));
        Assert.NotNull(VideoTrimService.ValidateRange(16, 17, 15));
        Assert.NotNull(VideoTrimService.ValidateRange(double.NaN, 1));

        var clip = VideoTrimService.BuildClipPath(_dir, Path.Combine(_dir, "v.mp4"), 0.5, 14.5);
        Assert.Equal(Path.Combine(_dir, "v_trim_0.5-14.5s.mp4"), clip);
        File.WriteAllText(clip, "x");
        Assert.Equal(Path.Combine(_dir, "v_trim_0.5-14.5s_2.mp4"), VideoTrimService.BuildClipPath(_dir, Path.Combine(_dir, "v.mp4"), 0.5, 14.5));
    }

    [Fact]
    public async Task VideoTrimService_TrimAndInPlace_UseRunner_AndPreserveOnFailure()
    {
        var runner = new RecordingRunner();
        var svc = new VideoTrimService(() => "ffmpeg", runner);
        var src = Src();
        var outPath = Path.Combine(_dir, "o.mp4");
        var r = await svc.TrimAsync(src, outPath, 0.5, 4.5);
        Assert.True(r.Ok);
        Assert.Contains("copy", runner.Calls[0].Args);

        var inPlace = await svc.TrimInPlaceAsync(src, 0.5, 4.5);
        Assert.True(inPlace.Ok);
        Assert.Equal(4096, new FileInfo(src).Length);   // 교체됨
        Assert.Empty(Directory.GetFiles(_dir, "*.trim-*"));   // 임시 파일 정리

        var missing = await new VideoTrimService(() => null, runner).TrimAsync(src, outPath + "2", 0, 1);
        Assert.False(missing.Ok);
        var bad = await svc.TrimAsync(src, outPath, 2, 1);
        Assert.False(bad.Ok);
        Assert.False((await svc.TrimAsync(src, src, 0, 1)).Ok);   // 입출력 동일 차단
    }

    [Fact]
    public void TradingSafetyGuardService_ResolvesFourSessionPhases_Correctly()
    {
        static KrxSessionStatus At(int y, int mo, int d, int h, int mi) => TradingSafetyGuardService.ResolveSession(new DateTimeOffset(y, mo, d, h, mi, 0, Kst)).Status;

        Assert.Equal(KrxSessionStatus.Closed, At(2026, 11, 4, 8, 29));
        Assert.Equal(KrxSessionStatus.PreMarket, At(2026, 11, 4, 8, 30));
        Assert.Equal(KrxSessionStatus.PreMarket, At(2026, 11, 4, 8, 59));
        Assert.Equal(KrxSessionStatus.RegularMarket, At(2026, 11, 4, 9, 0));
        Assert.Equal(KrxSessionStatus.RegularMarket, At(2026, 11, 4, 15, 29));
        Assert.Equal(KrxSessionStatus.PostMarket, At(2026, 11, 4, 15, 30));
        Assert.Equal(KrxSessionStatus.PostMarket, At(2026, 11, 4, 17, 59));
        Assert.Equal(KrxSessionStatus.Closed, At(2026, 11, 4, 18, 0));
        Assert.Equal(KrxSessionStatus.Closed, At(2026, 11, 7, 10, 0));    // 토요일
        Assert.Equal(KrxSessionStatus.Closed, At(2026, 5, 5, 10, 0));     // 어린이날

        // 남은 시간 · 다음 전환 — 금 18:30 → 월 08:30
        var fri = TradingSafetyGuardService.ResolveSession(new DateTimeOffset(2026, 11, 6, 18, 30, 0, Kst));
        Assert.Equal(new DateTimeOffset(2026, 11, 9, 8, 30, 0, Kst), fri.NextChangeKst);
        Assert.Equal(TimeSpan.FromHours(62), fri.Remaining);
        var reg = TradingSafetyGuardService.ResolveSession(new DateTimeOffset(2026, 11, 4, 14, 0, 0, Kst));
        Assert.Equal(TimeSpan.FromMinutes(90), reg.Remaining);
        Assert.Equal("1시간 30분", TradingSafetyGuardService.FormatRemaining(reg.Remaining));
        Assert.Equal("🟢 야간 자유 모드 (GPU 100%)", TradingSafetyGuardService.SessionLabel(KrxSessionStatus.Closed));
        Assert.Equal("🛡️ 정규장 안전 모드 가동 중", TradingSafetyGuardService.SessionLabel(KrxSessionStatus.RegularMarket));
    }

    [Fact]
    public void KrxClosuresJson_AppliesCsatDelayAndTemporaryHoliday()
    {
        var csat = new DateOnly(2031, 11, 13);       // 가상 수능일(목)
        var temp = new DateOnly(2031, 10, 14);       // 가상 임시공휴일(화)
        var early = new DateOnly(2031, 12, 29);      // 가상 조기 폐장(월)
        var json = "{\"closures\":[\"2031-10-14\",{\"date\":\"bad\"}],\"sessions\":[{\"date\":\"2031-11-13\",\"open\":\"10:00\",\"close\":\"16:30\",\"note\":\"수능\"},{\"date\":\"2031-12-29\",\"close\":\"13:00\",\"postClose\":\"14:00\"},{\"date\":\"2031-12-30\",\"open\":\"16:00\",\"close\":\"15:00\"}]}";
        var path = Path.Combine(_dir, KrxHolidayCalendar.ClosuresFileName);
        File.WriteAllText(path, json);
        try
        {
            Assert.Equal(3, KrxHolidayCalendar.LoadClosures(path));   // 휴장 1 + 세션 2(불량 2건 건너뜀)
            Assert.True(KrxHolidayCalendar.IsHoliday(temp));

            static KrxSessionStatus At(DateOnly d, int h, int m) => TradingSafetyGuardService.ResolveSession(new DateTimeOffset(d.Year, d.Month, d.Day, h, m, 0, Kst)).Status;
            Assert.Equal(KrxSessionStatus.Closed, At(csat, 9, 0));          // 09:30 이전
            Assert.Equal(KrxSessionStatus.PreMarket, At(csat, 9, 45));
            Assert.Equal(KrxSessionStatus.RegularMarket, At(csat, 16, 0));
            Assert.Equal(KrxSessionStatus.PostMarket, At(csat, 16, 30));
            Assert.Equal(KrxSessionStatus.Closed, At(temp, 10, 0));
            Assert.Equal(KrxSessionStatus.PostMarket, At(early, 13, 30));
            Assert.Equal(KrxSessionStatus.Closed, At(early, 14, 0));
            Assert.False(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2031, 11, 13, 9, 30, 0, Kst)));

            Assert.Equal(0, KrxHolidayCalendar.LoadClosures(Path.Combine(_dir, "none.json")));
            Assert.Equal(-1, KrxHolidayCalendar.LoadClosuresFromJson("{broken"));
        }
        finally
        {
            KrxHolidayCalendar.RemoveClosure(temp);
            KrxHolidayCalendar.ClearSessionHours(csat);
            KrxHolidayCalendar.ClearSessionHours(early);
        }
    }
}
