using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.ProcessBridge;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>KRX 휴장일 캘린더 · 갤러리 스냅샷 분류 · FFmpeg 실행기 일원화 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class GovernanceKrxGallerySnapshotTests : IDisposable
{
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_gov_" + Guid.NewGuid().ToString("N"));

    public GovernanceKrxGallerySnapshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void TradingSafetyGuardService_IdentifiesKrxHolidays_AndDisablesGuard()
    {
        // 고정 휴장일(평일)
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 1, 1)));    // 신정(목)
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 5, 1)));    // 근로자의 날(금) — KRX 휴장
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 5, 5)));    // 어린이날(화)
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2025, 8, 15)));   // 광복절(금)
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 10, 9)));   // 한글날(금)
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 12, 25)));  // 성탄절(금)

        // 음력 — 설날 2026-02-17 · 추석 2026-09-25 · 부처님오신날 2026-05-24(일)
        Assert.Equal(D(2026, 2, 17), KrxHolidayCalendar.FromLunar(2026, 1, 1));
        Assert.Equal(D(2026, 9, 25), KrxHolidayCalendar.FromLunar(2026, 8, 15));
        Assert.Equal(D(2026, 5, 24), KrxHolidayCalendar.FromLunar(2026, 4, 8));
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 2, 16)));
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 2, 18)));
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 9, 24)));

        // 대체공휴일 — 광복절 2026-08-15(토) → 08-17(월) · 부처님오신날(일) → 05-25(월) · 2025 추석 10-05(일) → 10-08(수)
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 8, 17)));
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 5, 25)));
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2025, 10, 8)));
        Assert.False(KrxHolidayCalendar.IsHoliday(D(2026, 9, 28)));   // 추석 연휴 토요일 겹침 = 대체 없음

        // 연말 폐장일 — 2026-12-31(목) · 2022-12-31(토) → 직전 영업일 12-30(금)
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2026, 12, 31)));
        Assert.True(KrxHolidayCalendar.IsHoliday(D(2022, 12, 30)));
        Assert.False(KrxHolidayCalendar.IsHoliday(D(2022, 12, 29)));

        // 일반 영업일 · 주말
        Assert.False(KrxHolidayCalendar.IsHoliday(D(2026, 11, 5)));
        Assert.False(KrxHolidayCalendar.IsHoliday(D(2026, 11, 7)));   // 토요일 자체는 '평일 휴장' 아님
        Assert.True(KrxHolidayCalendar.IsClosed(D(2026, 11, 7)));

        // 비정기 휴장(선거일 등) 주입/제거
        var election = D(2026, 6, 3);
        Assert.False(KrxHolidayCalendar.IsHoliday(election));
        KrxHolidayCalendar.AddClosure(election);
        try { Assert.True(KrxHolidayCalendar.IsHoliday(election)); }
        finally { Assert.True(KrxHolidayCalendar.RemoveClosure(election)); }
        Assert.False(KrxHolidayCalendar.IsHoliday(election));

        // 세이프가드 — 휴장일 10:00 KST 자동 해제 · 영업일 10:00 가동
        Assert.False(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 5, 1, 10, 0, 0, Kst)));
        Assert.True(TradingSafetyGuardService.IsMarketHours(new DateTimeOffset(2026, 11, 5, 10, 0, 0, Kst)));
        using var guard = new TradingSafetyGuardService(static () => true);
        foreach (var day in new[] { D(2026, 5, 1), D(2026, 1, 1), D(2025, 8, 15) })
        {
            var at = new DateTimeOffset(day.Year, day.Month, day.Day, 10, 0, 0, Kst);
            guard.UtcNow = () => at;
            Assert.True(guard.IsHolidayNow);
            Assert.False(guard.IsThrottling);
            Assert.Null(guard.ResolvePriority("ffmpeg.exe"));
            Assert.Equal(int.MaxValue, guard.MaxConcurrentEncodes);
        }
        guard.RespectKrxHolidays = false;   // 휴장일 무시 옵션 → 평일 장중이면 가동
        Assert.True(guard.IsThrottling);
        guard.RespectKrxHolidays = true;
        guard.UtcNow = static () => new DateTimeOffset(2026, 11, 5, 10, 0, 0, Kst);
        Assert.True(guard.IsThrottling);
        Assert.Equal(ProcessPriorityClass.BelowNormal, guard.ResolvePriority("ffmpeg.exe"));
    }

    [Fact]
    public void GalleryMediaService_ScansStillSnapshots_AndFiltersCorrectly()
    {
        var stills = Path.Combine(_dir, GalleryMediaService.StillsFolder);
        Directory.CreateDirectory(stills);
        var snap = Path.Combine(stills, "005930_snapshot.png");
        File.WriteAllBytes(snap, MinimalPng());
        var render = Path.Combine(_dir, "render_1280x720.png");   // 루트 PNG = 스냅샷 아님
        File.WriteAllBytes(render, MinimalPng());
        File.WriteAllBytes(Path.Combine(stills, "note.jpg"), new byte[] { 0xFF, 0xD8, 0xFF });   // _stills 내 비-PNG = 스냅샷 아님

        Assert.True(GalleryMediaService.IsStillSnapshot(_dir, snap));
        Assert.False(GalleryMediaService.IsStillSnapshot(_dir, render));

        var svc = new GalleryMediaService();
        var items = svc.ScanGalleryItems(_dir);
        var s = Assert.Single(items, static i => i.IsStill);
        Assert.Equal(Path.GetFullPath(snap), Path.GetFullPath(s.FilePath));
        Assert.Equal(GalleryMediaType.Still, s.MediaType);
        Assert.Equal("🖼️ 스냅샷", s.AssetTag);
        Assert.Contains(items, static i => !i.IsStill && i.MediaType == GalleryMediaType.Image);

        Assert.True(GalleryViewModel.Matches(s, GalleryFilter.Snapshots, ""));
        Assert.False(GalleryViewModel.Matches(s, GalleryFilter.AiRender, ""));
        Assert.True(GalleryViewModel.Matches(s, GalleryFilter.All, ""));
        foreach (var it in items)
            if (!it.IsStill) Assert.False(GalleryViewModel.Matches(it, GalleryFilter.Snapshots, ""));

        var vm = new GalleryViewModel(svc);
        vm.Refresh(_dir);
        vm.SetFilter(GalleryFilter.Snapshots);
        Assert.Single(vm.MediaItems);
        Assert.True(vm.MediaItems[0].IsStill);

        // 스냅샷 삭제 → 디스크 제거
        Assert.True(vm.Delete(vm.MediaItems[0]));
        Assert.False(File.Exists(snap));
    }

    /// <summary>호출을 기록하며 장중 세이프가드 게이트·우선순위 판정을 적용하는 테스트 실행기.</summary>
    private sealed class GovernedRecorder(TradingSafetyGuardService guard) : IProcessRunner
    {
        public readonly List<(string Exe, ProcessPriorityClass? Priority, int Slots)> Calls = new();

        public Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string? workingDir = null,
            Action<bool, string>? onLine = null, CancellationToken ct = default)
            => guard.RunGatedAsync(_ =>
            {
                lock (Calls) Calls.Add((exe, guard.ResolvePriority(exe), guard.ActiveSlots));
                string? last = null;
                foreach (var a in args) last = a;
                if (last is not null && last.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) File.WriteAllBytes(last, new byte[] { 1, 2, 3 });
                return Task.FromResult(new ProcessResult(true, 0, "", ""));
            }, ct);
    }

    [Fact]
    public async Task AudioMuxAndStitchServices_ExecuteViaProcessRunner_RespectingPriority()
    {
        using var guard = new TradingSafetyGuardService(static () => true) { UtcNow = static () => new DateTimeOffset(2026, 11, 5, 10, 0, 0, Kst) };
        Assert.True(guard.IsThrottling);
        var runner = new GovernedRecorder(guard);

        // 🎵 BGM 믹서 — DI 실행기 경유
        var video = Path.Combine(_dir, "clip.mp4");
        var bgm = Path.Combine(_dir, "song.mp3");
        File.WriteAllBytes(video, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(bgm, new byte[] { 4, 5, 6 });
        var mux = new AudioMuxService(static () => @"C:\tools\ffmpeg.exe", runner, _dir);
        var m = await mux.MuxAsync(video, SnsBgmPreset.CustomFile, bgm, 10);
        Assert.True(m.Ok, m.Message);

        // 🎬 4컷 Concat — DI 실행기 경유(리졸버는 기본 FfmpegLocator 대신 주입)
        var cuts = new StoryboardStitchCut[4];
        for (var i = 0; i < 4; i++)
        {
            var p = Path.Combine(_dir, "cut" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ".mp4");
            File.WriteAllBytes(p, new byte[] { 0, 0, 0, 0x18, (byte)i });
            cuts[i] = new StoryboardStitchCut(p, "p", 5);
        }
        var stitch = new StoryboardStitchService(static () => "/usr/bin/ffmpeg", runner.AsFfmpegRunner());
        var s = await stitch.StitchAsync(cuts, Path.Combine(_dir, "full.mp4"));
        Assert.True(s.Ok, s.Message);

        // 모든 호출 = ffmpeg · BelowNormal · 게이트 슬롯 1 점유 상태에서 실행
        Assert.True(runner.Calls.Count >= 2);
        foreach (var (exe, pri, slots) in runner.Calls)
        {
            Assert.True(TradingSafetyGuardService.IsEncoderExecutable(exe));
            Assert.Equal(ProcessPriorityClass.BelowNormal, pri);
            Assert.Equal(1, slots);
        }
        Assert.Equal(0, guard.ActiveSlots);

        // DI 생성자(IProcessRunner) 경로 + 기본 실행기는 정적 ProcessRunner(전역 훅) 위임
        Assert.NotNull(new StoryboardStitchService(runner));
        Assert.IsAssignableFrom<IProcessRunner>(SystemProcessRunner.Shared);
        var missing = await SystemProcessRunner.Shared.RunAsync(Path.Combine(_dir, "no_such_ffmpeg.exe"), Array.Empty<string>());
        Assert.False(missing.Started);
    }

    private static byte[] MinimalPng()
    {
        // 시그니처 + IHDR(16x16) — 크기 판독용 최소 헤더
        return new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x10, 0x00, 0x00, 0x00, 0x10,
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x91, 0x68, 0x36,
        };
    }
}
