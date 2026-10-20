using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Wan;

/// <summary>제로카피 변환 결과. Backend="cuda-zerocopy"(scale_cuda 풀VRAM)/"nvenc-crop"(GPU 크롭)/"cpu"/"none".</summary>
public sealed record ZeroCopyResult(bool Ok, bool Applied, bool GpuUsed, string? OutputPath, string Backend, string Message);

/// <summary>
/// [PromptFlowStudio] Full VRAM Zero-Copy CUDA 가속 엔진 — 순수 FFmpeg CLI(외부 NuGet 0 · <see cref="ProcessRunner"/> 공유).
///   ▸ <b>제로카피 스케일</b>(<see cref="ScaleZeroCopyAsync"/>): <c>-hwaccel cuda -hwaccel_output_format cuda</c> 디코드 → <c>scale_cuda</c>(GPU) → <c>h264_nvenc</c>(GPU) —
///     프레임이 <b>디바이스(VRAM) 메모리에 상주</b>해 시스템 메모리 왕복이 없다(동일 화면비 해상도/60fps 변환에서 CPU 대비 3배↑).
///   ▸ <b>크롭 리타겟</b>(<see cref="CropRetargetAsync"/>): 화면비가 다른 플랫폼(2:3·3:4)은 GPU 디코드 + scale/crop + <c>h264_nvenc</c> 인코드로 가속(크롭은 CPU 단계).
///   ▸ <b>3단 폴백</b>: GPU 경로 실패/미지원 → CPU(scale/crop + libx264) → 원본 유지(예외 없음 · Graceful).
///   ▸ GPU 탐지는 <see cref="NvencGpuMotionEngine"/> 재사용(nvidia-smi + h264_nvenc). 인자는 <b>내부 상수 조합</b>만(인젝션 표면 없음).
/// </summary>
public sealed class CudaZeroCopyEngine(NvencGpuMotionEngine gpu)
{
    private const double ClipSeconds = 5.0;

    /// <summary>동일 화면비 해상도/프레임레이트 변환을 <b>풀 VRAM 제로카피</b>(scale_cuda)로 수행 → GPU 실패/미지원 시 CPU 스케일 폴백.</summary>
    public async Task<ZeroCopyResult> ScaleZeroCopyAsync(string input, string output, int width, int height, int fps = 0,
        IProgress<double>? progress = null, CancellationToken ct = default)
        => await RunWithFallbackAsync(input, output,
            () => BuildZeroCopyArgs(input, output, width, height, fps), "cuda-zerocopy",
            () => BuildCpuArgs(input, output, width, height, fps, cropToCover: false), progress, ct).ConfigureAwait(false);

    /// <summary>화면비가 다른 규격(2:3·3:4 등)으로 <b>커버 크롭 리타겟</b> → GPU 디코드+NVENC(크롭은 CPU) → CPU 폴백.
    ///   <paramref name="yBias"/>(0=상단·0.5=중앙·1=하단)로 세이프존 Y-앵커에 맞춰 크롭 창을 편향(얼굴/자막 보존).</summary>
    public async Task<ZeroCopyResult> CropRetargetAsync(string input, string output, int width, int height, int fps = 0,
        double yBias = 0.5, IProgress<double>? progress = null, CancellationToken ct = default)
        => await RunWithFallbackAsync(input, output,
            () => BuildGpuCropArgs(input, output, width, height, fps, yBias), "nvenc-crop",
            () => BuildCpuArgs(input, output, width, height, fps, cropToCover: true, yBias), progress, ct).ConfigureAwait(false);

    // ── 공통 실행: GPU 시도(가능할 때) → CPU 폴백 → 원본 유지 ──
    private async Task<ZeroCopyResult> RunWithFallbackAsync(
        string input, string output,
        Func<IReadOnlyList<string>> gpuArgs, string gpuBackend,
        Func<IReadOnlyList<string>> cpuArgs,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input))
            return new(false, false, false, input, "none", "입력 영상을 찾을 수 없습니다.");
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return new(true, false, false, input, "none", "ffmpeg 미탐지 — 원본 유지.");

        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            if (await gpu.DetectGpuAsync(ct).ConfigureAwait(false))
            {
                var gr = await RunFfmpegAsync(ffmpeg, gpuArgs(), progress, ct).ConfigureAwait(false);
                if (gr.ok && File.Exists(output)) { progress?.Report(100); return new(true, true, true, output, gpuBackend, $"GPU {gpuBackend} 완료{(gpu.GpuName is null ? "" : " · " + gpu.GpuName)}"); }
                if (ct.IsCancellationRequested) return new(false, false, false, input, "none", "취소됨(원본 유지).");
                try { if (File.Exists(output)) File.Delete(output); } catch { }
            }

            var cr = await RunFfmpegAsync(ffmpeg, cpuArgs(), progress, ct).ConfigureAwait(false);
            if (cr.ok && File.Exists(output)) { progress?.Report(100); return new(true, true, false, output, "cpu", "CPU 변환 완료(GPU 미지원/실패 폴백)."); }

            try { if (File.Exists(output)) File.Delete(output); } catch { }
            return new(false, false, false, input, "none", "GPU·CPU 변환 모두 실패 — 원본 유지: " + Tail(cr.err));
        }
        catch (OperationCanceledException) { return new(false, false, false, input, "none", "취소됨(원본 유지)."); }
        catch (Exception ex) { Program.Log("CudaZeroCopyEngine", ex); return new(false, false, false, input, "none", "변환 실패 — 원본 유지: " + ex.Message); }
    }

    /// <summary>풀 VRAM 제로카피 인자 — <c>-hwaccel_output_format cuda</c>(프레임 VRAM 상주) + <c>scale_cuda</c> + <c>h264_nvenc</c>.</summary>
    public static IReadOnlyList<string> BuildZeroCopyArgs(string input, string output, int w, int h, int fps)
    {
        var args = new List<string>
        {
            "-y",
            "-hwaccel", "cuda", "-hwaccel_output_format", "cuda",   // 디코드 결과가 VRAM 에 상주(제로카피)
            "-i", input,
            "-vf", $"scale_cuda={Even(w)}:{Even(h)}:interp_algo=lanczos",
        };
        if (fps > 0) { args.Add("-r"); args.Add(fps.ToString(CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "20",
            "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart", "-progress", "pipe:1", output });
        return args;
    }

    /// <summary>GPU 크롭 리타겟 인자 — CUDA 디코드 + scale/crop(커버·세이프존 Y편향) + NVENC 인코드(화면비 변경 규격용).</summary>
    public static IReadOnlyList<string> BuildGpuCropArgs(string input, string output, int w, int h, int fps, double yBias = 0.5)
    {
        var args = new List<string>
        {
            "-y", "-hwaccel", "cuda", "-i", input,
            "-vf", CoverCropVf(w, h, yBias),
        };
        if (fps > 0) { args.Add("-r"); args.Add(fps.ToString(CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-c:v", "h264_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", "20",
            "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart", "-progress", "pipe:1", output });
        return args;
    }

    /// <summary>CPU 폴백 인자 — 스케일(+커버 크롭·세이프존 Y편향) + libx264.</summary>
    public static IReadOnlyList<string> BuildCpuArgs(string input, string output, int w, int h, int fps, bool cropToCover, double yBias = 0.5)
    {
        var vf = cropToCover ? CoverCropVf(w, h, yBias) : $"scale={Even(w)}:{Even(h)}:flags=lanczos";
        var args = new List<string> { "-y", "-i", input, "-vf", vf };
        if (fps > 0) { args.Add("-r"); args.Add(fps.ToString(CultureInfo.InvariantCulture)); }
        args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart", "-progress", "pipe:1", output });
        return args;
    }

    /// <summary>커버 스케일 + 세이프존 Y편향 크롭 vf(0=상단·0.5=중앙·1=하단). 크롭 창을 세이프존 중심으로 이동해 피사체/자막을 보존.</summary>
    private static string CoverCropVf(int w, int h, double yBias)
    {
        var yb = Math.Clamp(yBias, 0.0, 1.0).ToString("0.###", CultureInfo.InvariantCulture);
        return $"scale={Even(w)}:{Even(h)}:force_original_aspect_ratio=increase:flags=lanczos,crop={Even(w)}:{Even(h)}:(iw-ow)/2:(ih-oh)*{yb}";
    }

    private static int Even(int v) => (v > 0 ? v : 2) & ~1;

    private async Task<(bool ok, string err)> RunFfmpegAsync(string ffmpeg, IReadOnlyList<string> args, IProgress<double>? progress, CancellationToken ct)
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
