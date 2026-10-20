using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Wan;

/// <summary>
/// [PromptFlow Studio v5.7.2] FFmpeg 실프레임 추출 — 순수 BCL <see cref="System.Diagnostics.Process"/>(외부 C++ DLL 0).
///   ▸ 로컬 동영상의 지정 타임스탬프(초)에서 1프레임을 JPG로 추출 → <paramref name="toPlaybackUrl"/> 델리게이트로 가상호스트 URL 변환.
///   ▸ FFmpeg 미설치 / 모바일 / 파일 부재 / 프로세스 예외 시 <see cref="WanSampleFactory.FrameDataUrl"/> 스텁 DataURL 로 자동 폴백(회복성).
///   ⚠ 무주입(정적) — ILocalMediaService 정적참조 불가라 caller 가 <c>Media.ToPlaybackUrl</c> 메서드 그룹을 델리게이트로 주입(참고파일 오류 교정).
///   ⚠ ffmpeg 프로세스 실행은 <c>#if WINDOWS</c> 격리(Services 는 모바일 source-link → 모바일은 스텁 경로).
/// </summary>
public static class FfmpegFrameExtractor
{
    /// <summary>비디오 경로 + 타임스탬프(초) → 대표 키프레임 URL(실프레임=가상호스트 · 폴백=스텁 DataURL). <paramref name="customFfmpegPath"/>가 존재하면 우선 실행.</summary>
    public static async Task<string> ExtractKeyframeAsync(
        string? videoPath, double timestampSec, string outputDir,
        string? customFfmpegPath = null, Func<string, string>? toPlaybackUrl = null, CancellationToken ct = default)
    {
        var stub = WanSampleFactory.FrameDataUrl($"Keyframe @{timestampSec:F0}s", "#38bdf8", "9:16 Frame · Stub Fallback");
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return stub;

#if WINDOWS
        try
        {
            Directory.CreateDirectory(outputDir);
            var outputPath = Path.Combine(outputDir, $"frame_{DateTime.Now:yyyyMMdd_HHmmss}_{timestampSec:F0}s.jpg");
            // v5.7.3: 커스텀 경로(파일 존재)가 있으면 우선, 없으면 시스템 PATH의 "ffmpeg".
            var ffmpegExe = !string.IsNullOrWhiteSpace(customFfmpegPath) && File.Exists(customFfmpegPath) ? customFfmpegPath! : "ffmpeg";
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = ffmpegExe;
            // -ss(빠른 시크) → -frames:v 1(1프레임) → -q:v 2(고품질 JPG). 로케일 콤마 방지(InvariantCulture).
            proc.StartInfo.Arguments =
                $"-y -ss {timestampSec.ToString("F2", CultureInfo.InvariantCulture)} -i \"{videoPath}\" -frames:v 1 -q:v 2 \"{outputPath}\"";
            proc.StartInfo.CreateNoWindow = true;
            proc.StartInfo.UseShellExecute = false;
            proc.Start();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                return toPlaybackUrl?.Invoke(outputPath) ?? outputPath;   // 실프레임 → 가상호스트 URL(델리게이트)
        }
        catch { /* ffmpeg 미설치/프로세스 예외 → 스텁 폴백 */ }
#else
        await Task.CompletedTask; // 모바일: ffmpeg 프로세스 미지원 → 스텁 폴백
#endif
        return stub;
    }
}
