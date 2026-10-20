using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Wan;

/// <summary>GPU 모션 보간 결과. <see cref="Backend"/>="nvenc"(GPU)/"cpu"(폴백)/"none"(미적용). Applied=false 면 원본 유지.</summary>
public sealed record GpuMotionResult(bool Ok, bool Applied, string? OutputPath, string Backend, string Message);

/// <summary>
/// [PromptFlowStudio] NVENC/CUDA GPU 가속 모션 보간 엔진 — 순수 FFmpeg CLI(외부 NuGet 0 · <see cref="ProcessRunner"/> 공유).
///   ▸ NVIDIA GPU 환경을 자동 탐지(<c>nvidia-smi</c> + ffmpeg 빌드의 <c>h264_nvenc</c> 인코더)하여 <c>-hwaccel cuda</c> 디코드 + <c>h264_nvenc</c> 인코드로
///     60fps 모션 보간을 <b>초고속</b> 처리한다.
///   ▸ <b>이중 폴백</b>: ①GPU 미탐지/CLI 실패 → ②CPU <c>minterpolate</c>(libx264) → ③둘 다 실패 시 원본 유지(예외 없음 · Graceful).
///   ▸ 탐지는 1회 캐시(<see cref="DetectGpuAsync"/>). 보간 수식은 <c>minterpolate</c>(CPU 필터)이며, GPU 경로는 디코드/인코드를 GPU 로 옮겨 전체 속도를 끌어올린다.
///   ⚠ 인자는 <b>내부 상수 조합</b>만 사용(외부 입력 미개입) — 필터그래프 인젝션 표면이 없다.
/// </summary>
public sealed class NvencGpuMotionEngine
{
    private const double ClipSeconds = 5.0;   // Wan 컷 길이(진행률 추정 분모)

    private readonly SemaphoreSlim _detectLock = new(1, 1);
    private bool? _gpu;
    private string? _gpuName;

    /// <summary>GPU 가속 가능 여부(탐지 완료 후 유효 · 미탐지 시 false). UI 힌트용.</summary>
    public bool GpuDetected => _gpu ?? false;

    /// <summary>탐지된 GPU 이름(nvidia-smi). 없으면 null.</summary>
    public string? GpuName => _gpuName;

    /// <summary>
    /// NVIDIA GPU 가속 가능 여부를 1회 탐지·캐시한다(nvidia-smi 존재 AND ffmpeg 에 h264_nvenc 인코더 존재).
    /// </summary>
    public async Task<bool> DetectGpuAsync(CancellationToken ct = default)
    {
        if (_gpu is bool cached) return cached;
        await _detectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_gpu is bool c2) return c2;
            var ffmpeg = ResolveFfmpeg();
            if (ffmpeg is null) { _gpu = false; return false; }

            var nvsmi = CliPathResolver.GetExecutablePath("nvidia-smi", "nvidia-smi.exe");
            var hasNvenc = false;
            var enc = await ProcessRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, null, null, ct).ConfigureAwait(false);
            if (enc.Started &&
                (enc.StdOut.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase) ||
                 enc.StdErr.Contains("h264_nvenc", StringComparison.OrdinalIgnoreCase)))
                hasNvenc = true;

            _gpu = nvsmi is not null && hasNvenc;
            if (_gpu == true) _gpuName = await QueryGpuNameAsync(nvsmi!, ct).ConfigureAwait(false);
            return _gpu.Value;
        }
        catch (Exception ex) { Program.Log("NvencGpuMotionEngine.Detect", ex); _gpu = false; return false; }
        finally { _detectLock.Release(); }
    }

    /// <summary>
    /// 입력 영상을 <paramref name="targetFps"/>(기본 60)로 모션 보간해 <paramref name="output"/> 로 저장한다.
    ///   GPU(NVENC/CUDA) 우선 → 실패/미지원 시 CPU minterpolate 폴백 → 둘 다 실패면 원본 유지(<see cref="GpuMotionResult.Applied"/>=false).
    /// </summary>
    public async Task<GpuMotionResult> InterpolateAsync(
        string input, string output, int targetFps = 60, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var fps = Math.Clamp(targetFps <= 0 ? 60 : targetFps, 24, 120);
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            return new(false, false, input, "none", "입력 영상을 찾을 수 없습니다.");

        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(true, false, input, "none", "ffmpeg 미탐지 — 보간을 건너뜁니다(원본 유지).");

        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // ── ① GPU(NVENC/CUDA) 우선 ──
            if (await DetectGpuAsync(ct).ConfigureAwait(false))
            {
                var gr = await RunFfmpegAsync(ffmpeg, BuildGpuArgs(input, output, fps), progress, ct).ConfigureAwait(false);
                if (gr.ok && File.Exists(output))
                {
                    progress?.Report(100);
                    return new(true, true, output, "nvenc", $"GPU 가속 {fps}fps 보간 완료{(GpuName is null ? "" : " · " + GpuName)}");
                }
                if (ct.IsCancellationRequested) return new(false, false, input, "none", "보간이 취소되었습니다(원본 유지).");
                try { if (File.Exists(output)) File.Delete(output); } catch { /* 부분 출력 정리 */ }
                Program.Log("NvencGpuMotionEngine", new Exception("GPU 보간 실패 → CPU 폴백: " + Tail(gr.err)));
            }

            // ── ② CPU minterpolate 폴백 ──
            var cr = await RunFfmpegAsync(ffmpeg, BuildCpuArgs(input, output, fps), progress, ct).ConfigureAwait(false);
            if (cr.ok && File.Exists(output))
            {
                progress?.Report(100);
                return new(true, true, output, "cpu", $"CPU minterpolate {fps}fps 보간 완료(GPU 미지원/실패 폴백).");
            }

            // ── ③ 둘 다 실패 → 원본 유지 ──
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            return new(false, false, input, "none", "GPU·CPU 보간 모두 실패 — 원본을 유지합니다: " + Tail(cr.err));
        }
        catch (OperationCanceledException) { return new(false, false, input, "none", "보간이 취소되었습니다(원본 유지)."); }
        catch (Exception ex)
        {
            Program.Log("NvencGpuMotionEngine.Interpolate", ex);
            return new(false, false, input, "none", "보간 실패 — 원본을 유지합니다: " + ex.Message);
        }
    }

    /// <summary>GPU 경로 인자 — <c>-hwaccel cuda</c> 디코드 + minterpolate(CPU 필터) + <c>h264_nvenc</c> GPU 인코드.</summary>
    public static IReadOnlyList<string> BuildGpuArgs(string input, string output, int fps)
    {
        var f = fps.ToString(CultureInfo.InvariantCulture);
        return new List<string>
        {
            "-y",
            "-hwaccel", "cuda",                 // GPU 디코드(출력은 시스템 메모리 → CPU 필터 호환)
            "-i", input,
            "-vf", $"minterpolate=fps={f}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1",
            "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "20",   // GPU 인코드(NVENC)
            "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart",
            "-progress", "pipe:1",
            output,
        };
    }

    /// <summary>CPU 폴백 인자 — minterpolate + libx264.</summary>
    public static IReadOnlyList<string> BuildCpuArgs(string input, string output, int fps)
    {
        var f = fps.ToString(CultureInfo.InvariantCulture);
        return new List<string>
        {
            "-y", "-i", input,
            "-vf", $"minterpolate=fps={f}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
            "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart",
            "-progress", "pipe:1",
            output,
        };
    }

    // ── 내부 ──
    private async Task<(bool ok, string err)> RunFfmpegAsync(
        string ffmpeg, IReadOnlyList<string> args, IProgress<double>? progress, CancellationToken ct)
    {
        var lastPct = -1;
        void OnLine(bool isErr, string line)
        {
            if (progress is null || line is null) return;
            var idx = line.IndexOf("out_time_us=", StringComparison.Ordinal);
            if (idx < 0) return;
            var val = line[(idx + "out_time_us=".Length)..].Trim();
            if (!long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) || us < 0) return;
            var pct = (int)Math.Clamp(us / 1_000_000.0 / ClipSeconds * 100.0, 0, 99);
            if (pct != lastPct) { lastPct = pct; progress.Report(pct); }
        }
        var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: OnLine, ct: ct).ConfigureAwait(false);
        return (res.Ok, res.StdErr ?? "");
    }

    private static async Task<string?> QueryGpuNameAsync(string nvsmi, CancellationToken ct)
    {
        try
        {
            var r = await ProcessRunner.RunAsync(nvsmi, new[] { "--query-gpu=name", "--format=csv,noheader" }, null, null, ct).ConfigureAwait(false);
            if (r.Ok)
            {
                var name = r.StdOut.Split('\n').FirstOrDefault()?.Trim();
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch { /* 무시 */ }
        return null;
    }

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
