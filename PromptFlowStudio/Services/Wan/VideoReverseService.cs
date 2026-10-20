using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Wan;

/// <summary>v16.5.0 영상 반전 결과. <see cref="Ok"/>=false 면 원본을 그대로 쓰라는 뜻(ffmpeg 부재/실패/취소 · Graceful).</summary>
public sealed record VideoReverseResult(bool Ok, string? OutputPath, string Message, long ElapsedMs);

/// <summary>
/// [PromptFlowStudio] v16.5.0 🔄 1-Click 비디오 역재생 후처리 — FFmpeg <c>-vf reverse</c>(재생성 과금 0 · 순수 BCL <see cref="ProcessRunner"/> 공유).
///   ▸ I2V 가 역방향 모션으로 생성된 클립을 로컬에서 즉시 반전(5초 클립 기준 ultrafast 인코딩 ≈1초).
///   ▸ 오디오는 제거(<c>-an</c>) — 반전 오디오는 의미가 없고 Wan 클립은 무음이 기본.
///   ▸ 출력은 원본 옆 <c>{이름}_reversed.mp4</c>. 반전본을 다시 반전하면 <c>_reversed</c> 접미사를 떼어 원본명으로 되돌린다.
///   ▸ Graceful: ffmpeg 부재/실패/취소/타임아웃 시 예외 없이 <see cref="VideoReverseResult.Ok"/>=false.
///   ⚠ 필터 문자열은 내부 상수만 사용(외부 입력 미개입) — 필터그래프 인젝션 표면 0.
/// </summary>
public static class VideoReverseService
{
    /// <summary>역재생 필터(내부 상수).</summary>
    public const string ReverseFilter = "reverse";

    private const string ReversedSuffix = "_reversed";

    /// <summary>프로세스 상한(초) — 긴 클립/저사양에서도 UI 가 무한 대기하지 않도록.</summary>
    public const int TimeoutSeconds = 30;

    /// <summary>입력 경로 → 반전본 출력 경로(<c>_reversed</c> 토글).</summary>
    public static string BuildOutputPath(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        stem = stem.EndsWith(ReversedSuffix, StringComparison.OrdinalIgnoreCase)
            ? stem[..^ReversedSuffix.Length] + "_restored"
            : stem + ReversedSuffix;
        return Path.Combine(dir, stem + ".mp4");
    }

    /// <summary>ffmpeg 인자 목록 — ArgumentList 로 전달(공백/한글 경로 따옴표 문제 원천 차단).</summary>
    public static List<string> BuildArguments(string inputPath, string outputPath) => new()
    {
        "-y",
        "-i", inputPath,
        "-vf", ReverseFilter,
        "-an",
        "-c:v", "libx264",
        "-pix_fmt", "yuv420p",
        "-preset", "ultrafast",    // 1초 내 반전(5초 클립) — 화질은 crf 로 유지
        "-crf", "18",
        "-movflags", "+faststart",
        outputPath,
    };

    /// <summary>
    /// 로컬 영상을 역재생으로 반전해 저장한다. <paramref name="customFfmpegPath"/>가 존재하면 우선 실행.
    /// </summary>
    public static async Task<VideoReverseResult> ReverseAsync(
        string? inputPath, string? customFfmpegPath = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            return new(false, null, "반전할 로컬 영상을 찾을 수 없습니다.", 0);

        var ffmpeg = !string.IsNullOrWhiteSpace(customFfmpegPath) && File.Exists(customFfmpegPath) ? customFfmpegPath! : ResolveFfmpeg();
        if (ffmpeg is null)
            return new(false, null, "ffmpeg 미탐지 — 영상 반전을 건너뜁니다(원본 유지).", 0);

        var outputPath = BuildOutputPath(inputPath);
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            var res = await ProcessRunner.RunAsync(ffmpeg, BuildArguments(inputPath, outputPath), workingDir: null, onLine: null, ct: cts.Token).ConfigureAwait(false);
            sw.Stop();

            if (res.ExitCode == -2)
                return new(false, null, ct.IsCancellationRequested ? "영상 반전이 취소되었습니다(원본 유지)." : "영상 반전 시간 초과(원본 유지).", sw.ElapsedMilliseconds);
            if (!res.Ok || !File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                return new(false, null, "ffmpeg 반전 실패 — 원본을 유지합니다: " + Tail(res.StdErr), sw.ElapsedMilliseconds);

            return new(true, outputPath, "영상 반전 완료", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return new(false, null, "영상 반전이 취소되었습니다(원본 유지).", sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            Program.Log("VideoReverseService.Reverse", ex);
            return new(false, null, "영상 반전 실패 — 원본을 유지합니다: " + ex.Message, sw.ElapsedMilliseconds);
        }
    }

    /// <summary>ffmpeg 위치 결정 — exe 옆(배포 관례) 우선 → CliPathResolver 3단계(번들/로컬앱데이터/PATH). 못 찾으면 null.</summary>
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

    private static string Tail(string? s, int n = 400) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());
}
