using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // EngineConfigService / ProcessRunner / EngineKeys / CliPathResolver
using PromptFlowStudio.Services.Media;     // IVideoMergeService / ILocalMediaService

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>자동 썸네일 결과. Ok=false 면 실패. Path=thumbnail.png 절대경로, Upscaled=Real-ESRGAN 4K 적용 여부.</summary>
public sealed record ThumbnailResult(bool Ok, string? Path, bool Upscaled, string Message);

/// <summary>
/// [PromptFlowStudio] 표정 감지 자동 썸네일 크롭 & 4K 업스케일러 — 순수 BCL(<see cref="Process"/> + ffmpeg/Real-ESRGAN CLI, 외부 NuGet 0).
///   ▸ 렌더링된 컷 중 <b>긴장도(Tension) 피크</b>(또는 표정 반전 구간 Cut 3~4)의 핵심 프레임을 자동 캡처.
///   ▸ 아기 얼굴 영역을 <b>1.5배 매크로 크롭</b>(중앙-상단 얼굴 위치 휴리스틱 · ML 얼굴검출은 외부 의존이라 제외)한 뒤
///     <see cref="EngineKeys.RealEsrgan"/> CLI(<see cref="CliPathResolver"/>로 자동 탐색)로 4K 초고화질 <c>thumbnail.png</c> 배출.
///   ▸ Graceful: ffmpeg/Real-ESRGAN 부재 시 — ffmpeg 없으면 실패 메시지, ESRGAN 없으면 ffmpeg 고화질 스케일로 폴백(썸네일은 항상 산출 시도).
/// </summary>
public sealed class AutoThumbnailService(IVideoMergeService merge, EngineConfigService engines, ILocalMediaService media)
{
    private const double CaptureSec = 2.2;   // 컷 내 표정 피크 근사 시점(5초 컷 · 소스 기준)

    /// <summary>프로젝트의 렌더 완료 컷에서 표정 피크 프레임을 잡아 얼굴 매크로 크롭 + 4K 업스케일한 thumbnail.png 를 만든다.</summary>
    public async Task<ThumbnailResult> GenerateThumbnailAsync(StoryboardProject project, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (project is null) return new(false, null, false, "프로젝트가 없습니다.");
        var ffmpeg = merge.ResolvedFfmpegPath;
        if (ffmpeg is null) return new(false, null, false, "ffmpeg 를 찾을 수 없습니다.");

        // ── 표정 피크 컷 선정: Tension 최대 → 없으면 반전 구간(Cut 3~4=index 2~3) 가중 ──
        var scenes = project.Scenes;
        StoryboardScene? peak = null; var bestScore = -1;
        for (var i = 0; i < scenes.Count; i++)
        {
            var s = scenes[i];
            if (string.IsNullOrEmpty(s.LocalPath) || !File.Exists(s.LocalPath)) continue;
            var t = s.PresetTension ?? s.EmotionTension ?? (i is 2 or 3 ? 72 : 40);   // 반전 구간 가중
            if (t > bestScore) { bestScore = t; peak = s; }
        }
        if (peak?.LocalPath is null) return new(false, null, false, "썸네일을 만들 완료된 컷(로컬 파일)이 없습니다.");

        try
        {
            var dir = Path.Combine(media.MediaRoot, project.Id);
            Directory.CreateDirectory(dir);
            var cropPng = Path.Combine(dir, "_thumb_crop.png");
            var outPng = Path.Combine(dir, "thumbnail.png");

            // ── ① 프레임 캡처 + 1.5배 얼굴 매크로 크롭(중앙-상단) + 720w 스케일 ──
            //   crop=iw/1.5:ih/1.5 → 1.5배 줌 / x 중앙 / y 상단 30%(아기 얼굴 위치). scale=720:-2(짝수).
            log?.Report("표정 피크 프레임 캡처 + 얼굴 매크로 크롭…");
            var vf = "crop=iw/1.5:ih/1.5:(iw-iw/1.5)/2:(ih-ih/1.5)*0.30,scale=720:-2";
            if (!await RunFfmpegAsync(ffmpeg, new[] { "-y", "-ss", Inv(CaptureSec), "-i", peak.LocalPath!, "-vf", vf, "-frames:v", "1", "-q:v", "2", cropPng }, ct).ConfigureAwait(false)
                || !File.Exists(cropPng))
                return new(false, null, false, "프레임 캡처/크롭에 실패했습니다.");

            // ── ② Real-ESRGAN 4K 업스케일(설치 시) → 없으면 ffmpeg 고화질 스케일 폴백 ──
            var esrgan = engines.Resolve(EngineKeys.RealEsrgan);   // CliPathResolver 3단계 자동 탐색(Tools\ 번들 포함)
            if (esrgan is not null)
            {
                log?.Report("Real-ESRGAN 4K 업스케일 중…");
                var model = engines.Extra(EngineKeys.RealEsrgan, "Model", "realesrgan-x4plus") ?? "realesrgan-x4plus";
                var args = ProcessRunner.Substitute(engines.Args(EngineKeys.RealEsrgan), new Dictionary<string, string>
                { ["in"] = cropPng, ["out"] = outPng, ["scale"] = "4", ["model"] = model });
                var r = await ProcessRunner.RunAsync(esrgan, args, null, null, ct).ConfigureAwait(false);
                if (r.Started && r.ExitCode == 0 && File.Exists(outPng))
                {
                    TryDelete(cropPng);
                    return new(true, outPng, true, "썸네일 4K 업스케일 완료(Real-ESRGAN).");
                }
                log?.Report("Real-ESRGAN 실패 → ffmpeg 고화질 스케일로 폴백.");
            }

            // 폴백: ffmpeg lanczos 로 ~2160w 확대(4K급) → thumbnail.png.
            if (await RunFfmpegAsync(ffmpeg, new[] { "-y", "-i", cropPng, "-vf", "scale=2160:-2:flags=lanczos", "-frames:v", "1", "-q:v", "2", outPng }, ct).ConfigureAwait(false)
                && File.Exists(outPng))
            {
                TryDelete(cropPng);
                return new(true, outPng, false, esrgan is null
                    ? "썸네일 생성 완료(Real-ESRGAN 미설치 → ffmpeg 고화질 스케일)."
                    : "썸네일 생성 완료(ffmpeg 폴백).");
            }

            // 최후: 크롭본 자체를 썸네일로.
            if (File.Exists(cropPng)) { try { File.Copy(cropPng, outPng, true); TryDelete(cropPng); } catch { } }
            return File.Exists(outPng)
                ? new(true, outPng, false, "썸네일 생성 완료(크롭본).")
                : new(false, null, false, "썸네일 산출에 실패했습니다.");
        }
        catch (OperationCanceledException) { return new(false, null, false, "취소되었습니다."); }
        catch (Exception ex) { Program.Log("AutoThumbnail.Generate", ex); return new(false, null, false, "썸네일 실패: " + ex.Message); }
    }

    /// <summary>썸네일 재생 URL(가상호스트) — 미리보기용. 없으면 null.</summary>
    public string? PlaybackUrl(string? path) => string.IsNullOrEmpty(path) || !File.Exists(path) ? null : media.ToPlaybackUrl(path);

    private static async Task<bool> RunFfmpegAsync(string ffmpeg, string[] args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = new Process { StartInfo = psi };
            proc.ErrorDataReceived += (_, __) => { };
            proc.OutputDataReceived += (_, __) => { };
            if (!proc.Start()) return false;
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } return false; }
            return proc.ExitCode == 0;
        }
        catch (Exception ex) { Program.Log("AutoThumbnail.Ffmpeg", ex); return false; }
    }

    private static string Inv(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
}
