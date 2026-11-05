using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Render;

/// <summary>앰비언트 루프 방식 — <see cref="ForwardCrossfade"/>(기본) 는 시간 역행 없이 꼬리→머리 xfade 오버랩으로 이음새 제거.</summary>
public enum LoopMode { SlowStretch, PingPong, ForwardCrossfade }

/// <summary>
/// [PromptFlowStudio] 15초 앰비언트 슬로우 루프 후처리 — 단일 5초 추론 산출물을 무음 15초 배경 영상(<c>{project}_ambient15s.mp4</c>)으로 변환.
///   ▸ 기본(고속): <c>setpts=3.0*PTS,fps=30</c> 프레임 스트레칭 — minterpolate 대비 CPU 부하 극소, 15초 수 초 내 인코딩.
///   ▸ 고품질(옵트인): <c>setpts=3.0*PTS,minterpolate='fps=30:mi_mode=mci'</c> 모션 보간.
///   ▸ 핑퐁: 1.5배 슬로우 → 정방향+역방향 concat(시작·끝 프레임 일치 → 무한 반복 시 이음새 없음) — 총 15초.
///   ▸ 항상 <c>-an</c> 무음 · <c>-t 15</c> 클램프 · libx264/yuv420p. 단순 필터는 <c>-vf</c>, 핑퐁(복합 스트림)은 반드시 <c>-filter_complex</c> + <c>-map [outv]</c>.
///   ▸ 결과 검증: 출력 실존 + <see cref="MinValidOutputBytes"/> 초과. ffmpeg 부재·비정상 종료·출력 누락은 예외 없이 원본 5초 경로를 <see cref="VideoStitchResult.OutputPath"/> 로 반환(<see cref="FallbackNotice"/>).
/// </summary>
public sealed class AmbientLoopService
{
    public const double SlowFactor = 3.0;
    public const double PingPongSlowFactor = 1.5;
    public const int TargetFps = 30;
    public const int TargetSeconds = 15;
    /// <summary>유효 산출물 최소 크기(바이트) — 이하이면 ffmpeg 가 헤더만 쓰고 죽은 것으로 간주.</summary>
    public const long MinValidOutputBytes = 1024;
    /// <summary>후처리 실패 시 로그·작업 메타에 기록되는 대체 보존 안내.</summary>
    public const string FallbackNotice = "FFmpeg 후처리 실패/미설치로 인해 원본 5s 비디오로 대체 보존";

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

    /// <summary>핑퐁 출력 라벨 — <c>-map</c> 대상.</summary>
    public const string PingPongOutLabel = "[outv]";

    /// <summary>
    /// 핑퐁 필터 그래프(<c>-filter_complex</c> 전용) — <c>[0:v]슬로우,split=2[v0][v1];[v1]reverse[v2];[v0][v2]concat=n=2:v=1:a=0[outv]</c>.
    /// 슬로우 배율은 <see cref="PingPongSlowFactor"/>(5s×1.5=7.5s ×2 = 15s) — 3.0 배면 정방향만으로 15초를 채워 <c>-t 15</c> 에 역방향이 잘려 루프 이음새가 생김.
    /// </summary>
    public static string BuildPingPongFilterGraph(bool highQuality = false)
        => "[0:v]" + (highQuality ? BuildSlowMotionFilter(PingPongSlowFactor) : BuildFastSlowFilter(PingPongSlowFactor))
           + ",split=2[v0][v1];[v1]reverse[v2];[v0][v2]concat=n=2:v=1:a=0" + PingPongOutLabel;

    /// <summary>정방향 크로스페이드 기본 오버랩(초).</summary>
    public const double DefaultCrossfadeSeconds = 1.5;
    /// <summary>앰비언트 입력(단일 추론) 기본 길이(초).</summary>
    public const double DefaultSourceSeconds = 5.0;

    private static string Sec(double v) => v.ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>
    /// ⏩ 정방향 크로스페이드 필터 그래프(<c>-filter_complex</c> 전용) — <c>reverse</c> 없음(캔들·파티클 항상 미래 방향).
    /// 슬로우 배율 = (15 + N) / 원본 길이 → 슬로우 총 길이 D = 15 + N.
    /// <c>[0:v]슬로우,split=2[v1][v2];[v1]trim=start=0:end={D-N}[main];[v2]trim=start={D-N}:end={D}[tail];[tail][main]xfade=transition=fade:duration=N:offset=0[outv]</c>
    /// 출력 15초: 시작은 꼬리(t=D-N) → 머리로 페이드, 끝은 main(t=D-N) → 루프 재시작 시 꼬리 첫 프레임(t=D-N)과 동일 시점 → 무이음.
    /// </summary>
    public static string BuildForwardCrossfadeFilterGraph(double sourceSeconds = DefaultSourceSeconds, double crossfadeSeconds = DefaultCrossfadeSeconds, bool highQuality = false)
    {
        if (!(sourceSeconds > 0)) sourceSeconds = DefaultSourceSeconds;
        if (!(crossfadeSeconds > 0) || crossfadeSeconds >= TargetSeconds) crossfadeSeconds = DefaultCrossfadeSeconds;
        var total = TargetSeconds + crossfadeSeconds;
        var factor = Math.Round(total / sourceSeconds, 3);
        var cut = Sec(TargetSeconds);
        var n = Sec(crossfadeSeconds);
        return "[0:v]" + (highQuality ? BuildSlowMotionFilter(factor) : BuildFastSlowFilter(factor))
               + ",split=2[v1][v2];[v1]trim=start=0:end=" + cut + ",setpts=PTS-STARTPTS[main];"
               + "[v2]trim=start=" + cut + ":end=" + Sec(total) + ",setpts=PTS-STARTPTS[tail];"
               + "[tail][main]xfade=transition=fade:duration=" + n + ":offset=0" + PingPongOutLabel;
    }

    /// <summary>모드 지정 인자 — <see cref="LoopMode.ForwardCrossfade"/>·<see cref="LoopMode.PingPong"/> 는 <c>-filter_complex</c> + <c>-map [outv]</c>, <see cref="LoopMode.SlowStretch"/> 는 <c>-vf</c>.</summary>
    public static string[] BuildAmbientArgs(string inputPath, string outputPath, LoopMode mode, bool highQuality = false, double sourceSeconds = DefaultSourceSeconds, double crossfadeSeconds = DefaultCrossfadeSeconds)
    {
        if (mode != LoopMode.ForwardCrossfade) return BuildAmbientArgs(inputPath, outputPath, mode == LoopMode.PingPong, highQuality);
        var args = BuildAmbientArgs(inputPath, outputPath, true, highQuality);
        args[Array.IndexOf(args, "-filter_complex") + 1] = BuildForwardCrossfadeFilterGraph(sourceSeconds, crossfadeSeconds, highQuality);
        return args;
    }

    /// <summary>메타 문자열 → 모드(미지정/무효 = <see cref="LoopMode.ForwardCrossfade"/>).</summary>
    public static LoopMode ParseLoopMode(string? v)
        => Enum.TryParse<LoopMode>(v?.Trim(), true, out var m) && Enum.IsDefined(m) ? m : LoopMode.ForwardCrossfade;

    /// <summary>앰비언트 15초 인자 — 기본 <c>-vf</c> 고속 슬로우, 핑퐁은 <c>-filter_complex</c> + <c>-map [outv]</c>. <paramref name="highQuality"/> 시 minterpolate 보간.</summary>
    public static string[] BuildAmbientArgs(string inputPath, string outputPath, bool pingPong = false, bool highQuality = false)
    {
        var args = new List<string>(28) { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath };
        if (pingPong) { args.Add("-filter_complex"); args.Add(BuildPingPongFilterGraph(highQuality)); args.Add("-map"); args.Add(PingPongOutLabel); }
        else { args.Add("-vf"); args.Add(highQuality ? BuildSlowMotionFilter() : BuildFastSlowFilter()); }
        args.AddRange(new[]
        {
            "-an", "-t", TargetSeconds.ToString(CultureInfo.InvariantCulture),
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", outputPath,
        });
        return args.ToArray();
    }

    /// <summary>
    /// HTS 저사양 배경용 WebM(VP9) 초경량 변환 인자 — <c>-y -i {in} -c:v libvpx-vp9 -crf 32 -b:v 0 -an {out}</c>.
    /// 무음·CRF 품질 모드(비트레이트 상한 0)로 디코드 부하 최소 · 무한 루프 재생용.
    /// </summary>
    public static string[] BuildWebMArgs(string inputPath, string outputPath)
        => ["-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-an", outputPath];

    /// <summary>출력 무결성 — 실존 + <see cref="MinValidOutputBytes"/> 초과.</summary>
    public static bool IsValidOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return File.Exists(path) && new FileInfo(path).Length > MinValidOutputBytes; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    /// <summary>
    /// 후처리 실행 — 성공 시 Ok + 출력 경로. 입력 누락만 OutputPath=null.
    /// ffmpeg 부재(IsVirtual=true)·비정상 종료·출력 누락/손상(≤1KB)은 예외 없이 Ok=false + OutputPath=<b>원본 입력 경로</b>(대체 보존) + <see cref="FallbackNotice"/>.
    /// </summary>
    public Task<VideoStitchResult> RenderAsync(string inputPath, string outputPath, bool pingPong = false, CancellationToken ct = default, bool highQuality = false)
        => RenderAsync(inputPath, outputPath, pingPong ? LoopMode.PingPong : LoopMode.SlowStretch, ct, highQuality);

    /// <summary>모드 지정 후처리 — 실패/폴백 규약은 bool 오버로드와 동일.</summary>
    public async Task<VideoStitchResult> RenderAsync(string inputPath, string outputPath, LoopMode mode, CancellationToken ct = default, bool highQuality = false)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return new(false, false, null, 0, "앰비언트 입력 누락: " + inputPath);
        var source = Path.GetFullPath(inputPath);
        if (string.IsNullOrWhiteSpace(outputPath)) return Fallback(source, false, "출력 경로가 비어 있습니다.");
        string? ffmpeg;
        try { ffmpeg = _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("Ambient.Resolve", ex); ffmpeg = null; }
        if (ffmpeg is null) return Fallback(source, true, "ffmpeg 미탐지");

        var full = Path.GetFullPath(outputPath);
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var ok = await _run(ffmpeg, BuildAmbientArgs(source, full, mode, highQuality), ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { TryDeletePartial(full, source); return new(false, false, source, 1, "취소됨 — 원본 5s 유지"); }
            if (!ok || !IsValidOutput(full))
            {
                TryDeletePartial(full, source);
                return Fallback(source, false, ok ? "출력 파일 없음/손상(≤1KB)" : "ffmpeg 비정상 종료");
            }
            return new(true, false, full, 1, "앰비언트 15초 루프 완료" + (mode switch { LoopMode.PingPong => "(핑퐁)", LoopMode.ForwardCrossfade => "(정방향 크로스페이드)", _ => "(3x 고속 슬로우)" }));
        }
        catch (OperationCanceledException) { TryDeletePartial(full, source); return new(false, false, source, 1, "취소됨 — 원본 5s 유지"); }
        catch (Exception ex)
        {
            Program.Log("Ambient.Render", ex);
            TryDeletePartial(full, source);
            return Fallback(source, false, ex.Message);
        }
    }

    private static VideoStitchResult Fallback(string source, bool isVirtual, string reason)
    {
        var msg = FallbackNotice + " (" + reason + ")";
        if (!isVirtual) Program.Log("Ambient.Fallback", new InvalidOperationException(msg));
        return new(false, isVirtual, source, 1, msg);
    }

    /// <summary>실패 잔여물(0바이트·헤더만) 삭제 — 원본과 동일 경로면 절대 건드리지 않음.</summary>
    private static void TryDeletePartial(string output, string source)
    {
        if (string.Equals(output, source, StringComparison.OrdinalIgnoreCase)) return;
        try { if (File.Exists(output)) File.Delete(output); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("Ambient.Cleanup", ex); }
    }

    private static async Task<bool> DefaultRunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
        return r.Ok && r.ExitCode == 0;
    }
}
