using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Media;

/// <summary>플랫폼별 최적 업로드 시간 가이드(KST 기준).</summary>
public sealed record UploadWindow(SafePlatform Platform, string Icon, string Name, string BestTimesKst, string Note);

/// <summary>
/// [PromptFlowStudio] 8대 플랫폼 최적 업로드 타임존 가이드(v3.3.0 자율 발상 · static · 순수 BCL).
///   ▸ 플랫폼별 시청 피크 시간대(KST)와 근거를 안내해 양산 배포 시 노출 극대화 타이밍을 코칭.
/// </summary>
public static class UploadTimingAdvisor
{
    /// <summary>플랫폼별 권장 업로드 창(KST).</summary>
    public static readonly IReadOnlyList<UploadWindow> Windows = new[]
    {
        new UploadWindow(SafePlatform.YouTubeShorts, "▶️", "YouTube Shorts", "평일 18:00–21:00 · 주말 11:00", "퇴근/저녁 시청 피크 30분 전 업로드"),
        new UploadWindow(SafePlatform.InstagramReels, "📸", "Instagram Reels", "평일 12:00 · 19:00–21:00", "점심·저녁 스크롤 타임 직전"),
        new UploadWindow(SafePlatform.TikTok, "🎵", "TikTok", "화·목 19:00–23:00", "야간 체류 시간 최고조"),
        new UploadWindow(SafePlatform.NaverClip, "🟢", "NaverClip", "평일 08:00 · 12:00", "출근·점심 검색 유입"),
        new UploadWindow(SafePlatform.KarrotStory, "🥕", "당근 스토리", "평일 10:00 · 15:00", "지역 생활 활동 시간대"),
        new UploadWindow(SafePlatform.Chzzk, "🎮", "치지직", "평일 20:00–24:00", "게임/라이브 시청 야간 집중"),
        new UploadWindow(SafePlatform.Pinterest, "📌", "Pinterest", "주말 20:00–23:00", "주말 밤 아이디어 탐색 피크"),
        new UploadWindow(SafePlatform.Xiaohongshu, "📕", "샤오홍슈", "평일 12:30 · 21:00 (CST)", "중국 점심·야간 (KST+1h 보정)"),
    };

    /// <summary>플랫폼 권장 창(없으면 null).</summary>
    public static UploadWindow? Advise(SafePlatform p) => Windows.FirstOrDefault(w => w.Platform == p);

    /// <summary>선택 플랫폼들의 권장 창.</summary>
    public static IReadOnlyList<UploadWindow> AdviseAll(IEnumerable<SafePlatform> platforms)
        => (platforms ?? Array.Empty<SafePlatform>()).Select(Advise).Where(w => w is not null).Select(w => w!).ToList();
}

/// <summary>썸네일 포착 결과.</summary>
public sealed record ThumbnailResult(bool Ok, string? Path, string Message);

/// <summary>
/// [PromptFlowStudio] 플랫폼별 썸네일 자동 포착 캡처(v3.3.0 자율 발상) — FFmpeg <c>thumbnail</c> 필터(외부 라이브러리 0).
///   ▸ 영상에서 <b>대표 프레임</b>(장면 대표성 높은 프레임)을 자동 선별해 썸네일 PNG로 추출. 규격(폭/높이) 지정 가능.
/// </summary>
public sealed class ThumbnailCaptureService
{
    /// <summary>대표 썸네일을 캡처한다(ffmpeg thumbnail 필터). 실패는 Graceful.</summary>
    public async Task<ThumbnailResult> CaptureAsync(string video, string outPng, int width = 1080, int height = 1920, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(video) || !File.Exists(video)) return new(false, null, "영상 없음");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(false, null, "ffmpeg 없음");
        try
        {
            var dir = Path.GetDirectoryName(outPng);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var res = await ProcessRunner.RunAsync(ffmpeg, BuildThumbnailArgs(video, outPng, width, height), workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(outPng) ? new(true, outPng, "썸네일 포착 완료") : new(false, null, "썸네일 캡처 실패");
        }
        catch (OperationCanceledException) { return new(false, null, "취소됨"); }
        catch (Exception ex) { Program.Log("Thumbnail.Capture", ex); return new(false, null, ex.Message); }
    }

    /// <summary>썸네일 캡처 ffmpeg 인자(테스트 대상) — thumbnail 대표 프레임 + 커버 스케일 크롭.</summary>
    public static IReadOnlyList<string> BuildThumbnailArgs(string video, string outPng, int w, int h)
        => new[]
        {
            "-y", "-i", video,
            "-vf", $"thumbnail=100,scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}",
            "-frames:v", "1", outPng,
        };

    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }
}
