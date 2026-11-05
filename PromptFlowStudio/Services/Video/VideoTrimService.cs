using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Video;

/// <summary>무손실 트림 결과 — 실패 시 Path=null · Message 에 사유.</summary>
public sealed record VideoTrimResult(bool Ok, string? Path, string Message);

/// <summary>
/// ✂️ 캔버스 무손실 초고속 키프레임 트리머 — <c>ffmpeg -ss {s} -to {e} -i {in} -c copy -avoid_negative_ts make_zero {out}</c>.
///   ▸ 재인코딩 0(스트림 copy) → 화질 손실 0% · 생성 왜곡 프레임(시작/끝) 즉시 컷.
///   ▸ 모든 실행은 <see cref="IProcessRunner"/> 단일 게이트웨이 경유(장중 BelowNormal · 동시 1 게이트 · 취소 트리 kill).
///   ▸ 덮어쓰기는 임시 파일 산출 → 검증 → 원자적 교체(실패 시 원본 보존 · 임시 파일 정리).
/// </summary>
public sealed class VideoTrimService
{
    /// <summary>최소 산출 구간(초).</summary>
    public const double MinSpanSeconds = 0.1;

    private readonly Func<string?> _resolveFfmpeg;
    private readonly IProcessRunner _runner;

    public VideoTrimService() : this(null, null) { }

    /// <param name="resolveFfmpeg">ffmpeg 경로 조회(null 반환 = 부재). null 이면 <see cref="FfmpegLocator"/>.</param>
    /// <param name="processRunner">🛡️ 실행 게이트웨이. null 이면 <see cref="SystemProcessRunner.Shared"/>.</param>
    public VideoTrimService(Func<string?>? resolveFfmpeg, IProcessRunner? processRunner)
    {
        _resolveFfmpeg = resolveFfmpeg ?? (static () => FfmpegLocator.Locate().Path);
        _runner = processRunner ?? SystemProcessRunner.Shared;
    }

    /// <summary>초 → ffmpeg 타임코드(InvariantCulture · 밀리초 정밀도).</summary>
    public static string FormatSeconds(double seconds)
        => Math.Max(0, seconds).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>무손실 트림 인자 조립(순수 함수).</summary>
    public static string[] BuildLosslessTrimArgs(string inputPath, string outputPath, double startSeconds, double endSeconds) =>
    [
        "-y", "-hide_banner",
        "-ss", FormatSeconds(startSeconds),
        "-to", FormatSeconds(endSeconds),
        "-i", inputPath,
        "-c", "copy",
        "-avoid_negative_ts", "make_zero",
        outputPath,
    ];

    /// <summary>구간 검증 — 오류 문구(정상 null). <paramref name="durationSeconds"/> 미상이면 상한 검사 생략.</summary>
    public static string? ValidateRange(double startSeconds, double endSeconds, double? durationSeconds = null)
    {
        if (double.IsNaN(startSeconds) || double.IsNaN(endSeconds) || double.IsInfinity(startSeconds) || double.IsInfinity(endSeconds)) return "시간 값이 올바르지 않습니다.";
        if (startSeconds < 0) return "시작 시간은 0 이상이어야 합니다.";
        if (endSeconds - startSeconds < MinSpanSeconds) return "종료 시간은 시작 시간보다 0.1초 이상 뒤여야 합니다.";
        if (durationSeconds is { } d && d > 0 && startSeconds >= d) return "시작 시간이 영상 길이를 넘었습니다.";
        return null;
    }

    /// <summary>새 클립 경로 — <c>{원본}_trim_{s}-{e}s{ext}</c>(충돌 시 _2, _3 …).</summary>
    public static string BuildClipPath(string directory, string inputPath, double startSeconds, double endSeconds)
    {
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";
        var stem = name + "_trim_" + startSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "-" + endSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s";
        var path = Path.Combine(directory, stem + ext);
        for (var i = 2; File.Exists(path) && i < 1000; i++) path = Path.Combine(directory, stem + "_" + i.ToString(CultureInfo.InvariantCulture) + ext);
        return path;
    }

    /// <summary>무손실 트림 → <paramref name="outputPath"/>.</summary>
    public async Task<VideoTrimResult> TrimAsync(string inputPath, string outputPath, double startSeconds, double endSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return new(false, null, "원본 영상을 찾지 못했습니다.");
        if (string.IsNullOrWhiteSpace(outputPath)) return new(false, null, "출력 경로가 비어 있습니다.");
        if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            return new(false, null, "입력과 출력 경로가 같습니다(덮어쓰기는 TrimInPlaceAsync).");
        if (ValidateRange(startSeconds, endSeconds) is { } err) return new(false, null, err);

        string? ffmpeg;
        try { ffmpeg = _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("VideoTrim.Locate", ex); ffmpeg = null; }
        if (string.IsNullOrWhiteSpace(ffmpeg)) return new(false, null, "FFmpeg 미설치 — 설정에서 경로를 지정하세요.");

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var r = await _runner.RunAsync(ffmpeg, BuildLosslessTrimArgs(inputPath, outputPath, startSeconds, endSeconds), ct: ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested || r.ExitCode == -2) { TryDelete(outputPath); return new(false, null, "자르기가 취소되었습니다."); }
            if (!r.Ok) { TryDelete(outputPath); return new(false, null, "FFmpeg 실패(종료코드 " + r.ExitCode.ToString(CultureInfo.InvariantCulture) + ")"); }
            if (!AmbientLoopService.IsValidOutput(outputPath)) { TryDelete(outputPath); return new(false, null, "출력 파일 없음/손상"); }
            return new(true, outputPath, "✂️ " + FormatSeconds(startSeconds) + "s ~ " + FormatSeconds(endSeconds) + "s 무손실 컷 완료");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("VideoTrim.Trim", ex);
            TryDelete(outputPath);
            return new(false, null, "자르기 실패: " + ex.Message);
        }
    }

    /// <summary>현재 영상에 덮어쓰기 — 임시 산출 → 검증 → 원자적 교체(실패 시 원본 무손상).</summary>
    public async Task<VideoTrimResult> TrimInPlaceAsync(string inputPath, double startSeconds, double endSeconds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return new(false, null, "원본 영상을 찾지 못했습니다.");
        var ext = Path.GetExtension(inputPath);
        var tmp = Path.Combine(Path.GetDirectoryName(inputPath) ?? "", Path.GetFileNameWithoutExtension(inputPath) + ".trim-" + Guid.NewGuid().ToString("N")[..8] + (string.IsNullOrEmpty(ext) ? ".mp4" : ext));
        var r = await TrimAsync(inputPath, tmp, startSeconds, endSeconds, ct).ConfigureAwait(false);
        if (!r.Ok) return r;
        try
        {
            File.Move(tmp, inputPath, overwrite: true);
            return new(true, inputPath, r.Message + " · 현재 영상 덮어쓰기");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("VideoTrim.Replace", ex);
            TryDelete(tmp);
            return new(false, null, "덮어쓰기 실패(파일 사용 중) — 새 클립으로 저장하세요.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("VideoTrim.Cleanup", ex); }
    }
}
