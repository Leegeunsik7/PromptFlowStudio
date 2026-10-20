using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Media;

/// <summary>업스케일 엔진 — 실사(Real-ESRGAN) / 2D·애니(Real-CUGAN).</summary>
public enum UpscaleEngine { RealEsrgan, RealCugan }

/// <summary>업스케일 결과. 실패 시 Ok=false + Message.</summary>
public sealed record UpscaleResult(bool Ok, string? OutputPath, string Message);

/// <summary>비디오 딥러닝 업스케일(4K) 계약 — ffmpeg 프레임 분해 → ncnn-vulkan 업스케일 → ffmpeg 재조립.</summary>
public interface IVideoUpscaleService
{
    /// <summary>해당 엔진 CLI 가 설정/발견되었는지.</summary>
    bool IsAvailable(UpscaleEngine engine);
    /// <summary>둘 중 하나라도 사용 가능한지(+ ffmpeg 필요).</summary>
    bool AnyAvailable { get; }
    /// <summary>영상을 <paramref name="scale"/> 배로 업스케일해 저장. 진행률(0–100)/상태 문자열 보고. 실패는 Graceful.</summary>
    Task<UpscaleResult> UpscaleAsync(string inputVideo, string outputVideo, int scale = 4,
        UpscaleEngine engine = UpscaleEngine.RealEsrgan,
        IProgress<double>? progress = null, IProgress<string>? status = null, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 4K 비디오 업스케일 — Real-ESRGAN(BSD 3-Clause)/Real-CUGAN(MIT) ncnn-vulkan CLI 연동(외부 NuGet 0, 순수 BCL).
///   3단계 파이프라인: ①ffmpeg 로 PNG 프레임 분해 → ②ncnn-vulkan 으로 폴더 일괄 업스케일 → ③ffmpeg 로 원본 오디오와 함께 재조립(libx264).
///   ▸ 진행률: 업스케일 단계는 출력 폴더의 프레임 수를 폴링해 10~90%로 매핑(엔진 %출력 유무와 무관하게 견고). 재조립은 90~99%.
///   ▸ ffmpeg 경로는 <see cref="IVideoMergeService.ResolvedFfmpegPath"/> 재사용. 재조립 fps 는 원본 stderr 에서 파싱(없으면 <c>Merge:Fps</c>/30).
///   ▸ 임시 프레임 폴더는 종료 시 정리. 엔진/ffmpeg 부재·실패는 예외 없이 결과 record 로 원인 반환(Graceful).
/// </summary>
public sealed class VideoUpscaleService(IConfiguration cfg, EngineConfigService engines, IVideoMergeService merge) : IVideoUpscaleService
{
    private int Fps => int.TryParse(cfg["Merge:Fps"], out var v) && v > 0 ? v : 30;

    private string? EsrganExe => engines.Resolve(EngineKeys.RealEsrgan);
    private string? CuganExe  => engines.Resolve(EngineKeys.RealCugan);

    private string? ExeOf(UpscaleEngine e) => e == UpscaleEngine.RealEsrgan ? EsrganExe : CuganExe;

    /// <inheritdoc/>
    public bool IsAvailable(UpscaleEngine engine) => merge.IsAvailable && ExeOf(engine) is not null;

    /// <inheritdoc/>
    public bool AnyAvailable => merge.IsAvailable && (EsrganExe is not null || CuganExe is not null);

    /// <inheritdoc/>
    public async Task<UpscaleResult> UpscaleAsync(string inputVideo, string outputVideo, int scale = 4,
        UpscaleEngine engine = UpscaleEngine.RealEsrgan,
        IProgress<double>? progress = null, IProgress<string>? status = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputVideo) || !File.Exists(inputVideo))
            return new(false, null, "입력 영상을 찾을 수 없습니다.");
        var ffmpeg = merge.ResolvedFfmpegPath;
        if (ffmpeg is null) return new(false, null, "ffmpeg.exe 를 찾을 수 없습니다.");
        var exe = ExeOf(engine);
        if (exe is null) return new(false, null, $"{engine} CLI 가 설정되지 않았습니다(관리자 설정에서 경로 지정).");
        var s = Math.Clamp(scale <= 0 ? 4 : scale, 2, 4);

        var work = Path.Combine(Path.GetTempPath(), "promptflow-upscale", Guid.NewGuid().ToString("N")[..8]);
        var framesIn  = Path.Combine(work, "in");
        var framesOut = Path.Combine(work, "out");
        try
        {
            Directory.CreateDirectory(framesIn);
            Directory.CreateDirectory(framesOut);
            var outDir = Path.GetDirectoryName(outputVideo);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

            // ── ① 프레임 분해 + fps 파싱 ──
            status?.Report("프레임 분해 중…"); progress?.Report(2);
            double srcFps = Fps;
            var extract = await ProcessRunner.RunAsync(ffmpeg,
                new[] { "-y", "-i", inputVideo, "-vsync", "0", Path.Combine(framesIn, "f%08d.png") },
                onLine: (_, line) => { var m = FpsRx.Match(line); if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f > 0) srcFps = f; },
                ct: ct).ConfigureAwait(false);
            if (!extract.Started) return new(false, null, "ffmpeg 프레임 분해 실행 실패.");
            var total = SafeCount(framesIn);
            if (total == 0) return new(false, null, "프레임을 추출하지 못했습니다.");
            progress?.Report(8);

            // ── ② ncnn-vulkan 폴더 일괄 업스케일(진행률=출력 프레임 폴링) ──
            status?.Report($"AI 업스케일 중… ({(engine == UpscaleEngine.RealEsrgan ? "Real-ESRGAN" : "Real-CUGAN")} ×{s})");
            var name = engine == UpscaleEngine.RealEsrgan ? EngineKeys.RealEsrgan : EngineKeys.RealCugan;
            var model = engines.Extra(name, "Model", engine == UpscaleEngine.RealEsrgan ? "realesrgan-x4plus" : "models-se")!;
            var template = engines.Args(name);
            var args = ProcessRunner.Substitute(template, new Dictionary<string, string>
            {
                ["in"] = framesIn, ["out"] = framesOut, ["scale"] = s.ToString(CultureInfo.InvariantCulture), ["model"] = model,
            });

            var upTask = ProcessRunner.RunAsync(exe, args, workingDir: Path.GetDirectoryName(exe), ct: ct);
            while (!upTask.IsCompleted)
            {
                var delay = Task.Delay(600, ct);
                try { await Task.WhenAny(upTask, delay).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                var done = SafeCount(framesOut);
                progress?.Report(8 + 82.0 * Math.Min(done, total) / total);
            }
            var up = await upTask.ConfigureAwait(false);
            if (!up.Ok) return new(false, null, $"업스케일 실패: {Tail(up.StdErr)}");
            if (SafeCount(framesOut) == 0) return new(false, null, "업스케일 결과 프레임이 없습니다.");
            progress?.Report(90);

            // ── ③ 재조립(원본 오디오 합성) ──
            status?.Report("4K 인코딩 중…");
            var reasm = await ProcessRunner.RunAsync(ffmpeg, new[]
            {
                "-y",
                "-framerate", srcFps.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", Path.Combine(framesOut, "f%08d.png"),
                "-i", inputVideo,
                "-map", "0:v:0", "-map", "1:a:0?",          // 오디오는 있으면 사용(? = 없으면 무시)
                "-c:v", "libx264", "-pix_fmt", "yuv420p", "-preset", "medium", "-crf", "17",
                "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-shortest",
                outputVideo,
            }, ct: ct).ConfigureAwait(false);
            if (!reasm.Ok || !File.Exists(outputVideo))
                return new(false, null, $"4K 재조립 실패: {Tail(reasm.StdErr)}");

            progress?.Report(100);
            try { await merge.ExtractPosterAsync(outputVideo, ct).ConfigureAwait(false); } catch { /* 썸네일 실패 무시 */ }
            return new(true, outputVideo, "4K 업스케일 완료");
        }
        catch (OperationCanceledException) { return new(false, null, "업스케일이 취소되었습니다."); }
        catch (Exception ex) { Program.Log("VideoUpscale.Upscale", ex); return new(false, null, "업스케일 실패: " + ex.Message); }
        finally { try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch { /* 무시 */ } }
    }

    private static int SafeCount(string dir)
    { try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.png").Count() : 0; } catch { return 0; } }

    private static string Tail(string s, int n = 400) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s.Trim() : "…" + s[^n..].Trim());

    private static readonly Regex FpsRx = new(@",\s*([\d.]+)\s*fps", RegexOptions.Compiled);
}
