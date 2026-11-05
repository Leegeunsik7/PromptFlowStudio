using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Render;

/// <summary>FFmpeg 비디오 인코더 가속 벤더 — 우선순위 NVENC → QSV → AMF → CPU(소프트웨어).</summary>
public enum GpuEncoderVendor : byte { Cpu, Nvenc, Qsv, Amf }

/// <summary>
/// [PromptFlowStudio] 🚀 FFmpeg GPU 하드웨어 가속 자동 감지기.
///   ▸ 런타임 1회 <c>ffmpeg -hide_banner -encoders</c> 프로브 → NVENC(<c>h264_nvenc</c>/<c>hevc_nvenc</c>) · QSV(<c>h264_qsv</c>/<c>hevc_qsv</c>) · AMF(<c>h264_amf</c>) 목록 판별.
///   ▸ 목록 존재 ≠ 실사용 가능(범용 빌드는 GPU 부재여도 나열) → 후보별 1프레임 실검증 인코드(lavfi → null) 통과 벤더만 채택.
///   ▸ ffmpeg 부재·프로브 실패·검증 실패·실인코딩 실패(<see cref="MarkFailed"/>) → <see cref="GpuEncoderVendor.Cpu"/> 무중단 폴백(libx264).
/// </summary>
public sealed class HardwareAccelDetector
{
    /// <summary>앱 공용 인스턴스(FfmpegLocator 기반).</summary>
    public static HardwareAccelDetector Shared { get; } = new();

    private readonly Func<string?> _resolveFfmpeg;
    private readonly Func<string, CancellationToken, Task<string?>> _listEncoders;
    private readonly FfmpegRunner _verify;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _probed;
    private volatile bool _failed;
    private GpuEncoderVendor _vendor;

    public HardwareAccelDetector() : this(null) { }

    /// <param name="resolveFfmpeg">ffmpeg 경로(null = <see cref="FfmpegLocator"/>).</param>
    /// <param name="listEncoders">ffmpeg <c>-encoders</c> stdout 공급자(테스트 주입 · null = ProcessRunner).</param>
    /// <param name="verifier">1프레임 검증 인코드 실행기(테스트 주입 · null = ProcessRunner).</param>
    public HardwareAccelDetector(Func<string?>? resolveFfmpeg, Func<string, CancellationToken, Task<string?>>? listEncoders = null, FfmpegRunner? verifier = null)
    {
        _resolveFfmpeg = resolveFfmpeg ?? (static () => FfmpegLocator.Locate().Path);
        _listEncoders = listEncoders ?? DefaultListAsync;
        _verify = verifier ?? DefaultRunAsync;
    }

    /// <summary>프로브 완료 여부.</summary>
    public bool IsProbed => _probed;

    /// <summary>현재 채택 벤더(미프로브/실패 강등 = CPU).</summary>
    public GpuEncoderVendor Active => _probed && !_failed ? _vendor : GpuEncoderVendor.Cpu;

    public static string Label(GpuEncoderVendor v) => v switch
    {
        GpuEncoderVendor.Nvenc => "🟩 NVIDIA NVENC",
        GpuEncoderVendor.Qsv => "🟦 Intel QuickSync",
        GpuEncoderVendor.Amf => "🟥 AMD AMF",
        _ => "💻 CPU(libx264)",
    };

    /// <summary>벤더별 H.264 인코더 이름(CPU = libx264).</summary>
    public static string H264Encoder(GpuEncoderVendor v) => v switch
    {
        GpuEncoderVendor.Nvenc => "h264_nvenc",
        GpuEncoderVendor.Qsv => "h264_qsv",
        GpuEncoderVendor.Amf => "h264_amf",
        _ => "libx264",
    };

    /// <summary>벤더별 HEVC 인코더 이름(AMF/CPU = null · 미사용).</summary>
    public static string? HevcEncoder(GpuEncoderVendor v) => v switch
    {
        GpuEncoderVendor.Nvenc => "hevc_nvenc",
        GpuEncoderVendor.Qsv => "hevc_qsv",
        _ => null,
    };

    /// <summary>
    /// H.264 비디오 코덱 인자(<c>-c:v</c> 포함) — 고품질 VBR/CQ 22 계열. CPU 는 기존 <c>libx264 -preset medium -crf 18</c> 그대로.
    /// </summary>
    public static string[] VideoCodecArgs(GpuEncoderVendor v) => v switch
    {
        GpuEncoderVendor.Nvenc => ["-c:v", "h264_nvenc", "-preset", "p4", "-rc", "vbr", "-cq", "22", "-b:v", "0"],
        GpuEncoderVendor.Qsv => ["-c:v", "h264_qsv", "-preset", "medium", "-global_quality", "22"],
        GpuEncoderVendor.Amf => ["-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", "22", "-qp_p", "22"],
        _ => ["-c:v", "libx264", "-preset", "medium", "-crf", "18"],
    };

    /// <summary><c>-encoders</c> 출력에서 인코더 이름 토큰(2번째 열) 일치 여부.</summary>
    public static bool ListsEncoder(string? encodersOutput, string name)
    {
        if (string.IsNullOrEmpty(encodersOutput)) return false;
        var s = encodersOutput.AsSpan();
        while (s.Length > 0)
        {
            var nl = s.IndexOf('\n');
            var line = (nl < 0 ? s : s[..nl]).Trim();
            s = nl < 0 ? default : s[(nl + 1)..];
            var sp = line.IndexOf(' ');
            if (sp <= 0) continue;
            var rest = line[(sp + 1)..].TrimStart();
            var end = rest.IndexOf(' ');
            var tok = end < 0 ? rest : rest[..end];
            if (tok.Equals(name, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>목록상 후보 벤더(우선순위 순) — NVENC → QSV → AMF.</summary>
    public static List<GpuEncoderVendor> ParseCandidates(string? encodersOutput)
    {
        var list = new List<GpuEncoderVendor>(3);
        if (ListsEncoder(encodersOutput, "h264_nvenc")) list.Add(GpuEncoderVendor.Nvenc);
        if (ListsEncoder(encodersOutput, "h264_qsv")) list.Add(GpuEncoderVendor.Qsv);
        if (ListsEncoder(encodersOutput, "h264_amf")) list.Add(GpuEncoderVendor.Amf);
        return list;
    }

    /// <summary>1프레임 실검증 인자 — lavfi 256×256 검정 → 대상 인코더 → null 머저.</summary>
    public static string[] BuildVerifyArgs(GpuEncoderVendor v)
        => ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=black:s=256x256:d=0.1", "-frames:v", "1", "-c:v", H264Encoder(v), "-f", "null", "-"];

    /// <summary>
    /// 1회 프로브(이후 캐시). 절대 throw 하지 않음(취소 제외 → CPU 반환·재프로브 허용).
    /// </summary>
    public async Task<GpuEncoderVendor> DetectAsync(CancellationToken ct = default)
    {
        if (_probed) return Active;
        try { await _gate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return GpuEncoderVendor.Cpu; }
        try
        {
            if (_probed) return Active;
            var vendor = GpuEncoderVendor.Cpu;
            string? ff;
            try { ff = _resolveFfmpeg(); }
            catch (Exception ex) { Program.Log("HwAccel.Locate", ex); ff = null; }
            if (!string.IsNullOrWhiteSpace(ff))
            {
                string? listing;
                try { listing = await _listEncoders(ff, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return GpuEncoderVendor.Cpu; }
                catch (Exception ex) { Program.Log("HwAccel.List", ex); listing = null; }
                var cands = ParseCandidates(listing);
                for (var i = 0; i < cands.Count; i++)
                {
                    bool ok;
                    try { ok = await _verify(ff, BuildVerifyArgs(cands[i]), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return GpuEncoderVendor.Cpu; }
                    catch (Exception ex) { Program.Log("HwAccel.Verify", ex); ok = false; }
                    if (ok) { vendor = cands[i]; break; }
                }
            }
            _vendor = vendor;
            _probed = true;
            return Active;
        }
        finally { _gate.Release(); }
    }

    /// <summary>실인코딩 실패 → 이후 세션 CPU 고정(무중단 폴백).</summary>
    public void MarkFailed() => _failed = true;

    private static async Task<string?> DefaultListAsync(string ffmpeg, CancellationToken ct)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        to.CancelAfter(TimeSpan.FromSeconds(10));
        var r = await Engines.ProcessRunner.RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, null, null, to.Token).ConfigureAwait(false);
        return r.Ok ? r.StdOut : null;
    }

    private static async Task<bool> DefaultRunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
        to.CancelAfter(TimeSpan.FromSeconds(15));
        var r = await Engines.ProcessRunner.RunAsync(exe, args, ct: to.Token).ConfigureAwait(false);
        return r.Ok && r.ExitCode == 0;
    }
}
