using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// [PromptFlowStudio] 15초 앰비언트 슬로우 루프 후처리 — 단일 5초 추론 산출물을 무음 15초 배경 영상(<c>{project}_ambient15s.mp4</c>)으로 변환.
///   ▸ 기본(고속): <c>setpts=3.0*PTS,fps=30</c> 프레임 스트레칭 — minterpolate 대비 CPU 부하 극소, 15초 수 초 내 인코딩.
///   ▸ 고품질(옵트인): <c>setpts=3.0*PTS,minterpolate='fps=30:mi_mode=mci'</c> 모션 보간.
///   ▸ 핑퐁: 1.5배 슬로우 → 정방향+역방향 concat(시작·끝 프레임 일치 → 무한 반복 시 이음새 없음) — 총 15초.
///   ▸ 항상 <c>-an</c> 무음 · <c>-t 15</c> 클램프 · libx264/yuv420p. ffmpeg 부재/실패는 원본 유지(Graceful · IsVirtual).
/// </summary>
public sealed class AmbientLoopService
{
    public const double SlowFactor = 3.0;
    public const double PingPongSlowFactor = 1.5;
    public const int TargetFps = 30;
    public const int TargetSeconds = 15;

    private readonly Func<string?> _resolveFfmpeg;
    private readonly FfmpegRunner _run;

    public AmbientLoopService() : this(null, null) { }

    /// <param name="resolveFfmpeg">ffmpeg 경로 조회(null = 부재). null 이면 <see cref="FfmpegLocator"/>.</param>
    /// <param name="runner">ffmpeg 실행기. null 이면 <see cref="ProcessRunner"/>.</param>
    public AmbientLoopService(Func<string?>? resolveFfmpeg, FfmpegRunner? runner)
    {
        _resolveFfmpeg = resolveFfmpeg ?? (static () => FfmpegLocator.Locate().Path);
        _run = runner ?? DefaultRunAsync;
    }

    /// <summary>고속 슬로우 필터(기본) — <c>setpts={f}*PTS,fps=30</c>. 모션 보간 없이 프레임 스트레칭만 수행(CPU 병목 없음).</summary>
    public static string BuildFastSlowFilter(double factor = SlowFactor)
        => "setpts=" + factor.ToString("0.0##", CultureInfo.InvariantCulture) + "*PTS,fps=" + TargetFps.ToString(CultureInfo.InvariantCulture);

    /// <summary>고품질 슬로우 필터(옵트인) — <c>setpts={f}*PTS,minterpolate='fps=30:mi_mode=mci'</c>.</summary>
    public static string BuildSlowMotionFilter(double factor = SlowFactor)
        => "setpts=" + factor.ToString("0.0##", CultureInfo.InvariantCulture) + "*PTS,minterpolate='fps="
           + TargetFps.ToString(CultureInfo.InvariantCulture) + ":mi_mode=mci'";

    /// <summary>핑퐁 필터 그래프 — 슬로우 → split → reverse → concat → <c>[vout]</c>. 기본 고속 필터, <paramref name="highQuality"/> 시 minterpolate.</summary>
    public static string BuildPingPongFilterGraph(bool highQuality = false)
        => "[0:v]" + (highQuality ? BuildSlowMotionFilter(PingPongSlowFactor) : BuildFastSlowFilter(PingPongSlowFactor))
           + ",split[fw][bw];[bw]reverse[rv];[fw][rv]concat=n=2:v=1:a=0,format=yuv420p[vout]";

    /// <summary>앰비언트 15초 인자 — 기본 <c>-vf</c> 고속 슬로우, 핑퐁은 <c>-filter_complex</c> + <c>-map [vout]</c>. <paramref name="highQuality"/> 시 minterpolate 보간.</summary>
    public static string[] BuildAmbientArgs(string inputPath, string outputPath, bool pingPong = false, bool highQuality = false)
    {
        var args = new List<string>(28) { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath };
        if (pingPong) { args.Add("-filter_complex"); args.Add(BuildPingPongFilterGraph(highQuality)); args.Add("-map"); args.Add("[vout]"); }
        else { args.Add("-vf"); args.Add(highQuality ? BuildSlowMotionFilter() : BuildFastSlowFilter()); }
        args.AddRange(new[]
        {
            "-an", "-t", TargetSeconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", outputPath,
        });
        return args.ToArray();
    }

    /// <summary>후처리 실행 — 성공 시 Ok + 출력 경로. ffmpeg 부재 IsVirtual=true, 입력 누락·실패 Ok=false(원본 유지는 호출자 책임).</summary>
    public async Task<VideoStitchResult> RenderAsync(string inputPath, string outputPath, bool pingPong = false, CancellationToken ct = default, bool highQuality = false)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return new(false, false, null, 0, "앰비언트 입력 누락: " + inputPath);
        if (string.IsNullOrWhiteSpace(outputPath)) return new(false, false, null, 1, "출력 경로가 비어 있습니다.");
        string? ffmpeg;
        try { ffmpeg = _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("Ambient.Resolve", ex); ffmpeg = null; }
        if (ffmpeg is null) return new(false, true, null, 1, "ffmpeg 미탐지 — 앰비언트 후처리 생략(원본 유지)");

        var full = Path.GetFullPath(outputPath);
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var ok = await _run(ffmpeg, BuildAmbientArgs(Path.GetFullPath(inputPath), full, pingPong, highQuality), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return new(false, false, null, 1, "취소됨");
            if (!ok || !File.Exists(full) || new FileInfo(full).Length == 0) return new(false, false, null, 1, "ffmpeg 앰비언트 루프 실패");
            return new(true, false, full, 1, "앰비언트 15초 루프 완료" + (pingPong ? "(핑퐁)" : "(3x 고속 슬로우)"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("Ambient.Render", ex);
            return new(false, false, null, 1, "앰비언트 후처리 실패: " + ex.Message);
        }
    }

    private static async Task<bool> DefaultRunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
        return r.Ok && r.ExitCode == 0;
    }
}
