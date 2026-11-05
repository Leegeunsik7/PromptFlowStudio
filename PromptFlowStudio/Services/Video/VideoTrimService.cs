using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Video;

/// <summary>무손실 트림 결과 — 실패 시 Path=null · Message 에 사유 · <see cref="DurationSeconds"/> = 산출 클립 재생시간(메타 기록값).</summary>
public sealed record VideoTrimResult(bool Ok, string? Path, string Message, double? DurationSeconds = null);

/// <summary>
/// 🔓 재생 중 파일 핫스왑 훅 — WebView2 <c>&lt;video&gt;</c> 가 쥔 OS 파일 핸들을 교체 직전에 놓고(Release) 교체 후 캐시 버스트 URL 로 재마운트(Reload).
///   Reload 인자 = 성공 여부(실패 시 원본 재마운트). 훅 예외는 격리(교체 흐름 무영향).
/// </summary>
public sealed record VideoHandleHooks(Func<CancellationToken, Task>? Release, Func<bool, CancellationToken, Task>? Reload);

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
            // 새 클립은 원본 메타(제목/프롬프트 등) 계승 + 재생시간/트림 이력 기록(임시 산출물은 교체 후 기록).
            var dur = IsTempPath(outputPath) ? ComputeTrimmedDuration(startSeconds, endSeconds, ReadMetaDuration(inputPath)) : UpdateTrimMetadata(inputPath, outputPath, startSeconds, endSeconds, DateTime.UtcNow);
            return new(true, outputPath, "✂️ " + FormatSeconds(startSeconds) + "s ~ " + FormatSeconds(endSeconds) + "s 무손실 컷 완료", dur);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Program.Log("VideoTrim.Trim", ex);
            TryDelete(outputPath);
            return new(false, null, "자르기 실패: " + ex.Message);
        }
    }

    /// <summary>현재 영상에 덮어쓰기 — 임시 산출 → 검증 → 원자적 교체(실패 시 원본 무손상).</summary>
    public Task<VideoTrimResult> TrimInPlaceAsync(string inputPath, double startSeconds, double endSeconds, CancellationToken ct = default)
        => TrimInPlaceAsync(inputPath, startSeconds, endSeconds, null, ct);

    /// <summary>파일 교체 재시도 횟수/간격 — 핸들 해제 직후 OS 가 잠금을 늦게 푸는 경우 흡수.</summary>
    public const int ReplaceRetryCount = 8;
    public static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// 🔓 재생 중 파일 덮어쓰기 — ① 임시 파일로 무손실 컷(재생 유지) → ② <paramref name="hooks"/>.Release 로 플레이어 핸들 해제
    ///   → ③ 원자적 교체(공유 위반 시 짧은 재시도) → ④ 메타 재생시간/트림 이력 갱신 → ⑤ Reload(성공 여부) 로 캐시 버스트 재마운트.
    ///   FFmpeg 실패 시 플레이어를 건드리지 않으며, 교체 실패 시 원본 그대로 재마운트.
    /// </summary>
    public async Task<VideoTrimResult> TrimInPlaceAsync(string inputPath, double startSeconds, double endSeconds, VideoHandleHooks? hooks, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath)) return new(false, null, "원본 영상을 찾지 못했습니다.");
        var ext = Path.GetExtension(inputPath);
        var tmp = Path.Combine(Path.GetDirectoryName(inputPath) ?? "", Path.GetFileNameWithoutExtension(inputPath) + TempMarker + Guid.NewGuid().ToString("N")[..8] + (string.IsNullOrEmpty(ext) ? ".mp4" : ext));
        var r = await TrimAsync(inputPath, tmp, startSeconds, endSeconds, ct).ConfigureAwait(false);
        if (!r.Ok) return r;

        await InvokeHookAsync(hooks?.Release, ct).ConfigureAwait(false);
        var replaced = false;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try { File.Move(tmp, inputPath, overwrite: true); replaced = true; break; }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReplaceRetryCount && !ct.IsCancellationRequested)
                {
                    try { await Task.Delay(ReplaceRetryDelay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("VideoTrim.Replace", ex);
        }

        if (!replaced)
        {
            TryDelete(tmp);
            await InvokeReloadAsync(hooks?.Reload, false).ConfigureAwait(false);
            return new(false, null, ct.IsCancellationRequested ? "자르기가 취소되었습니다." : "덮어쓰기 실패(파일 사용 중) — 잠시 후 다시 시도하세요.");
        }

        var dur = UpdateTrimMetadata(inputPath, inputPath, startSeconds, endSeconds, DateTime.UtcNow);
        await InvokeReloadAsync(hooks?.Reload, true).ConfigureAwait(false);
        return new(true, inputPath, r.Message + " · 현재 영상 덮어쓰기", dur ?? r.DurationSeconds);
    }

    // ══════ 🗂️ 트림 메타데이터(.meta.json) 동기화 ══════

    private const string TempMarker = ".trim-";

    private static bool IsTempPath(string path) => Path.GetFileName(path).Contains(TempMarker, StringComparison.Ordinal);

    /// <summary>트림 산출 재생시간 = min(end, 원본길이) − start(원본길이 미상이면 end − start · 밀리초 반올림).</summary>
    public static double ComputeTrimmedDuration(double startSeconds, double endSeconds, double? sourceDurationSeconds)
    {
        var end = sourceDurationSeconds is { } d && d > 0 ? Math.Min(endSeconds, d) : endSeconds;
        return Math.Round(Math.Max(0, end - Math.Max(0, startSeconds)), 3);
    }

    /// <summary>미디어 사이드카 메타의 <c>durationSeconds</c>(없음/손상 null).</summary>
    public static double? ReadMetaDuration(string mediaPath)
    {
        try
        {
            var meta = Gallery.GalleryMediaService.MetaPathFor(mediaPath);
            if (!File.Exists(meta)) return null;
            return JsonNode.Parse(File.ReadAllText(meta)) is JsonObject o && o["durationSeconds"] is JsonValue v && v.TryGetValue<double>(out var d) && d > 0 ? d : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 원본 메타(<paramref name="sourceMediaPath"/>) 를 기반으로 대상 메타(<paramref name="targetMediaPath"/>) 의 <c>durationSeconds</c> 재계산 +
    ///   <c>trimHistory</c> 에 <c>{start,end,trimmedAt}</c> 추가(기존 필드 보존 · 새 클립은 즐겨찾기/내보냄 플래그 미계승 · 임시파일 원자 교체).
    ///   반환 = 기록된 재생시간(쓰기 실패 null).
    /// </summary>
    public static double? UpdateTrimMetadata(string sourceMediaPath, string targetMediaPath, double startSeconds, double endSeconds, DateTime trimmedAtUtc)
    {
        var srcMeta = Gallery.GalleryMediaService.MetaPathFor(sourceMediaPath);
        var dstMeta = Gallery.GalleryMediaService.MetaPathFor(targetMediaPath);
        try
        {
            JsonObject obj;
            try { obj = File.Exists(srcMeta) ? JsonNode.Parse(File.ReadAllText(srcMeta)) as JsonObject ?? new() : new(); }
            catch (JsonException) { obj = new(); }   // 손상 메타 → 새로 작성

            double? prior = obj["durationSeconds"] is JsonValue pv && pv.TryGetValue<double>(out var pd) && pd > 0 ? pd : null;
            var dur = ComputeTrimmedDuration(startSeconds, endSeconds, prior);
            var sameFile = string.Equals(Path.GetFullPath(sourceMediaPath), Path.GetFullPath(targetMediaPath), StringComparison.OrdinalIgnoreCase);
            if (!sameFile)
            {
                obj.Remove("isFavorite");
                obj.Remove("isExported");
                obj.Remove("thumbnail");
                obj["title"] = Path.GetFileNameWithoutExtension(targetMediaPath);
                obj["createdAt"] = trimmedAtUtc.ToString("o", CultureInfo.InvariantCulture);
            }
            obj["durationSeconds"] = dur;
            var hist = obj["trimHistory"] as JsonArray ?? new JsonArray();
            hist.Add(new JsonObject
            {
                ["start"] = Math.Round(Math.Max(0, startSeconds), 3),
                ["end"] = Math.Round(startSeconds + dur, 3),
                ["trimmedAt"] = trimmedAtUtc.ToString("o", CultureInfo.InvariantCulture),
            });
            obj["trimHistory"] = hist;

            var tmp = dstMeta + ".~tmp";
            File.WriteAllText(tmp, obj.ToJsonString(MetaJsonOptions));
            File.Move(tmp, dstMeta, overwrite: true);
            return dur;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            Program.Log("VideoTrim.Meta", ex);
            return null;
        }
    }

    private static readonly JsonSerializerOptions MetaJsonOptions = new() { WriteIndented = true };

    private static async Task InvokeHookAsync(Func<CancellationToken, Task>? hook, CancellationToken ct)
    {
        if (hook is null) return;
        try { await hook(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("VideoTrim.Release", ex); }
        catch (OperationCanceledException) { /* 해제 취소 — 교체 재시도 루프가 판단 */ }
    }

    private static async Task InvokeReloadAsync(Func<bool, CancellationToken, Task>? hook, bool ok)
    {
        if (hook is null) return;
        try { await hook(ok, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("VideoTrim.Reload", ex); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("VideoTrim.Cleanup", ex); }
    }
}
