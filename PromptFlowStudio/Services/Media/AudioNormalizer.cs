using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// [PromptFlowStudio] 오디오 규격 정규화 — 생성 직후 TTS 오디오를 44.1kHz·스테레오로 강제 리샘플(외부 NuGet 0, ffmpeg).
///   ▸ Kokoro-82M(24kHz 모노)/F5-TTS/Edge TTS 출력이 BGM 과 <c>sidechaincompress</c> 덕킹 믹싱될 때
///     샘플레이트/채널 불일치로 생기는 피치 왜곡·노이즈를 원천 방어(병합 필터의 aresample 과 이중 안전망).
///   ▸ Graceful: ffmpeg 부재/실패 시 원본 경로를 그대로 반환(정규화 생략) — 병합 단계가 재차 aresample 하므로 안전.
/// </summary>
public static class AudioNormalizer
{
    /// <summary>
    /// <paramref name="input"/> 오디오를 44.1kHz·스테레오 WAV(pcm_s16le)로 변환해 경로 반환.
    ///   ffmpeg 없음/실패면 <paramref name="input"/> 그대로 반환(원본 유지 폴백).
    /// </summary>
    public static async Task<string> ToStereo44kAsync(string? ffmpeg, string input, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ffmpeg) || string.IsNullOrWhiteSpace(input) || !File.Exists(input))
                return input;

            var output = Path.ChangeExtension(input, null) + ".n44.wav";
            if (File.Exists(output) && new FileInfo(output).Length > 0) return output;   // 캐시 히트

            var r = await ProcessRunner.RunAsync(ffmpeg!, new[]
            {
                "-y", "-i", input,
                "-ar", "44100", "-ac", "2",           // 44.1kHz · 스테레오 강제
                "-c:a", "pcm_s16le",
                output,
            }, ct: ct).ConfigureAwait(false);

            return r.Ok && File.Exists(output) && new FileInfo(output).Length > 0 ? output : input;
        }
        catch (Exception ex) { Program.Log("AudioNormalizer.ToStereo44k", ex); return input; }
    }
}
