using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Spatial;   // ReadExactAsync

namespace PromptFlowStudio.Services.Vfx;

/// <summary>VFX 공용 — 외부 CLI 해석 · ffmpeg rawvideo 디코드→프레임 처리→인코드 파이프라인(버퍼 대여 · 실패 시 출력 삭제).</summary>
internal static class VfxProcess
{
    /// <summary>환경변수 → PATH 순 실행 파일 탐색(부재 null).</summary>
    public static string? ResolveCli(string envVar, params string[] names)
    {
        try
        {
            var env = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                foreach (var n in names)
                {
                    var p = Path.Combine(dir, n);
                    if (File.Exists(p)) return p;
                }
        }
        catch (Exception ex) { Program.Log("Vfx.ResolveCli", ex); }
        return null;
    }

    public static Process Start(string exe, IEnumerable<string> args, bool stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = !stdin, RedirectStandardInput = stdin, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi };
        if (!p.Start()) { p.Dispose(); throw new InvalidOperationException("프로세스 시작 실패: " + Path.GetFileName(exe)); }
        _ = p.StandardError.ReadToEndAsync(ct);
        return p;
    }

    public static List<string> DecodeArgs(string input, int w, int h, double? fps)
    {
        var ci = CultureInfo.InvariantCulture;
        var vf = (fps is { } f ? "fps=" + f.ToString("0.###", ci) + "," : "") + "scale=" + w.ToString(ci) + ":" + h.ToString(ci);
        return new List<string> { "-hide_banner", "-v", "error", "-i", input, "-vf", vf, "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" };
    }

    /// <summary>rgb24 파이프 → H.264(원본 오디오 선택 매핑).</summary>
    public static List<string> EncodeArgs(int w, int h, double fps, string? audioFrom, string output, int crf = 17)
    {
        var ci = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", w.ToString(ci) + "x" + h.ToString(ci), "-r", fps.ToString("0.###", ci), "-i", "pipe:0" };
        if (audioFrom is not null) a.AddRange(new[] { "-i", audioFrom, "-map", "0:v:0", "-map", "1:a:0?", "-c:a", "aac", "-b:a", "192k", "-shortest" });
        a.AddRange(new[] { "-c:v", "libx264", "-crf", crf.ToString(ci), "-preset", "medium", "-pix_fmt", "yuv420p", "-movflags", "+faststart", output });
        return a;
    }

    /// <summary>
    /// 디코드 → process(src, dst) → 인코드. 프레임 버퍼 대여 1회(프레임당 할당 0). 반환 = 처리 프레임 수(실패 0 · 출력 삭제).
    /// </summary>
    public static async Task<int> PipeAsync(string ffmpeg, string input, int inW, int inH, int outW, int outH, double outFps, double? decodeFps,
        Action<byte[], byte[]> process, string output, int maxFrames, Action<int>? progress, CancellationToken ct)
    {
        Process? dec = null, enc = null;
        var ok = false;
        var inN = inW * inH * 3; var outN = outW * outH * 3;
        var src = ArrayPool<byte>.Shared.Rent(inN);
        var dst = ArrayPool<byte>.Shared.Rent(outN);
        var frames = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            dec = Start(ffmpeg, DecodeArgs(input, inW, inH, decodeFps), false, ct);
            enc = Start(ffmpeg, EncodeArgs(outW, outH, outFps, input, output), true, ct);
            var sink = enc.StandardInput.BaseStream;
            while (frames < maxFrames && await Spatial3dGaussianSplattingEngine.ReadExactAsync(dec.StandardOutput.BaseStream, src, inN, ct).ConfigureAwait(false))
            {
                process(src, dst);
                await sink.WriteAsync(dst.AsMemory(0, outN), ct).ConfigureAwait(false);
                frames++;
                if ((frames & 15) == 0) progress?.Invoke(frames);
            }
            await sink.FlushAsync(ct).ConfigureAwait(false);
            sink.Close();
            await enc.WaitForExitAsync(ct).ConfigureAwait(false);
            ok = frames > 0 && enc.ExitCode == 0 && File.Exists(output);
            return ok ? frames : 0;
        }
        finally
        {
            foreach (var p in new[] { dec, enc })
            {
                if (p is null) continue;
                try { if (!p.HasExited) p.Kill(true); } catch { }
                p.Dispose();
            }
            ArrayPool<byte>.Shared.Return(src);
            ArrayPool<byte>.Shared.Return(dst);
            if (!ok) DeleteQuiet(output);
        }
    }

    public static void DeleteQuiet(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception ex) { Program.Log("Vfx.Cleanup", ex); }
    }

    public static void DeleteDirQuiet(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) { Program.Log("Vfx.CleanupDir", ex); }
    }

    public static void Raise(Action? h, string site)
    {
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log(site, ex); }
        }
    }
}
