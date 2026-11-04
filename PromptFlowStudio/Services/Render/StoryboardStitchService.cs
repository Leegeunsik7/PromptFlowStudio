using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;

namespace PromptFlowStudio.Services.Render;

/// <summary>스토리보드 합성 컷 1개 — 로컬 영상 · 컷 프롬프트 · 길이(초).</summary>
public sealed record StoryboardStitchCut(string VideoPath, string? Prompt, double DurationSec);

/// <summary>합성 결과 — Normalized=true 면 스트림 복사 실패로 1280x720 정규화 재인코딩. MetaPath = 컷별 프롬프트·타임스탬프 사이드카.</summary>
public sealed record StoryboardStitchResult(bool Ok, string? OutputPath, string? MetaPath, bool Normalized, string Message);

/// <summary>사이드카 컷 항목(<c>.storyboard.json</c>).</summary>
public sealed record StoryboardStitchMetaCut(int Index, string Prompt, double StartSec, double EndSec, string Source);

/// <summary>사이드카 루트 — 생성 시각(UTC) · 총 길이 · 컷 목록.</summary>
public sealed record StoryboardStitchMeta(string Output, DateTime CreatedUtc, double TotalSec, bool Normalized, IReadOnlyList<StoryboardStitchMetaCut> Cuts);

/// <summary>
/// [PromptFlowStudio] 🎬 스토리보드 4컷 자동 합성(Stitch) — 순수 BCL <c>ffmpeg</c> concat demuxer.
///   ▸ 1차: <c>-f concat -safe 0 -c:v copy -c:a copy</c> 무손실 스트림 복사.
///   ▸ 2차(실패/해상도 불일치): <c>scale=1280:720</c>(+레터박스 pad) <c>libx264/aac</c> 정규화 재인코딩.
///   ▸ 산출물 <c>storyboard_{yyyyMMdd_HHmmss}_full20s.mp4</c> — 임시 파일 → 실물 검증(존재·크기&gt;0) 후 원자적 이동.
///   ▸ 사이드카 <c>{output}.storyboard.json</c> — 컷별 프롬프트 · 시작/종료 타임스탬프 보존.
/// </summary>
public sealed class StoryboardStitchService
{
    public const int AutoStitchCutCount = 4;
    public const int NormWidth = 1280, NormHeight = 720;
    public const string FilePrefix = "storyboard_";
    public const string FileSuffix = "_full20s.mp4";
    public const string MetaExt = ".storyboard.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly Func<string?> _resolveFfmpeg;
    private readonly FfmpegRunner _run;

    public StoryboardStitchService() : this(null, null) { }

    /// <param name="resolveFfmpeg">ffmpeg 경로(null 반환 = 부재). null 이면 <see cref="FfmpegLocator"/>.</param>
    /// <param name="runner">ffmpeg 실행기(테스트 주입). null 이면 <see cref="ProcessRunner"/>.</param>
    public StoryboardStitchService(Func<string?>? resolveFfmpeg, FfmpegRunner? runner)
    {
        _resolveFfmpeg = resolveFfmpeg ?? DefaultResolve;
        _run = runner ?? DefaultRunAsync;
    }

    public bool IsFfmpegAvailable => SafeResolve() is not null;

    /// <summary>자동 합성 조건 — 정확히 4컷 · 전부 로컬 산출물 실존.</summary>
    public static bool ShouldAutoStitch(IReadOnlyList<StoryboardStitchCut>? cuts)
    {
        if (cuts is null || cuts.Count != AutoStitchCutCount) return false;
        for (var i = 0; i < cuts.Count; i++)
            if (string.IsNullOrWhiteSpace(cuts[i].VideoPath) || !File.Exists(cuts[i].VideoPath)) return false;
        return true;
    }

    /// <summary>렌더 큐 그룹 완성 시 컷 수집(CutIndex 순) — 프롬프트는 작업 <see cref="PromptFlowStudio.Services.Batch.BatchRenderJob.Prompt"/>.</summary>
    public static bool TryCollectGroupCuts(IReadOnlyList<PromptFlowStudio.Services.Batch.BatchRenderJob> jobs, string? groupId, out StoryboardStitchCut[] cuts)
    {
        cuts = Array.Empty<StoryboardStitchCut>();
        if (!FFmpegVideoStitchService.TryCollectCompletedCuts(jobs, groupId, out var done)) return false;
        var prompts = new Dictionary<string, string>(done.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < jobs.Count; i++)
            if (FFmpegVideoStitchService.LocalOutputOf(jobs[i]) is { } p) prompts[p] = jobs[i].Prompt;
        cuts = new StoryboardStitchCut[done.Length];
        for (var i = 0; i < done.Length; i++)
            cuts[i] = new StoryboardStitchCut(done[i].VideoPath, prompts.TryGetValue(done[i].VideoPath, out var pr) ? pr : null, done[i].DurationSec);
        return true;
    }

    /// <summary><c>{dir}\storyboard_{yyyyMMdd_HHmmss}_full20s.mp4</c>.</summary>
    public static string BuildOutputPath(string dir, DateTime timestamp) =>
        Path.Combine(dir, FilePrefix + timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + FileSuffix);

    /// <summary>concat 목록 텍스트(<see cref="FFmpegVideoStitchService.BuildConcatList"/> 재사용).</summary>
    public static string BuildConcatList(IReadOnlyList<StoryboardStitchCut> cuts)
    {
        var parts = new string[cuts.Count];
        for (var i = 0; i < parts.Length; i++) parts[i] = Path.GetFullPath(cuts[i].VideoPath);
        return FFmpegVideoStitchService.BuildConcatList(parts);
    }

    public static string[] BuildCopyArgs(string listPath, string outputPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-f", "concat", "-safe", "0", "-i", listPath,
        "-c:v", "copy", "-c:a", "copy", "-movflags", "+faststart", outputPath,
    ];

    public static string[] BuildNormalizeArgs(string listPath, string outputPath) =>
    [
        "-y", "-hide_banner", "-loglevel", "error",
        "-f", "concat", "-safe", "0", "-i", listPath,
        "-vf", "scale=1280:720:force_original_aspect_ratio=decrease,pad=1280:720:(ow-iw)/2:(oh-ih)/2,setsar=1",
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", outputPath,
    ];

    /// <summary>컷별 누적 타임스탬프 메타(길이 0 이하는 5초 가정).</summary>
    public static StoryboardStitchMeta BuildMeta(IReadOnlyList<StoryboardStitchCut> cuts, string outputPath, bool normalized, DateTime createdUtc)
    {
        var list = new List<StoryboardStitchMetaCut>(cuts.Count);
        var t = 0d;
        for (var i = 0; i < cuts.Count; i++)
        {
            var d = cuts[i].DurationSec > 0 ? cuts[i].DurationSec : 5d;
            list.Add(new StoryboardStitchMetaCut(i + 1, cuts[i].Prompt?.Trim() ?? "", Math.Round(t, 3), Math.Round(t + d, 3), Path.GetFileName(cuts[i].VideoPath)));
            t += d;
        }
        return new StoryboardStitchMeta(Path.GetFileName(outputPath), createdUtc, Math.Round(t, 3), normalized, list);
    }

    /// <summary>컷 합성 — 스트림 복사 우선, 실패 시 1280x720 정규화. 결과 파일 실물 검증 후 원자적 확정.</summary>
    public async Task<StoryboardStitchResult> StitchAsync(IReadOnlyList<StoryboardStitchCut> cuts, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        if (string.IsNullOrWhiteSpace(outputPath)) return new(false, null, null, false, "출력 경로가 비어 있습니다.");
        if (cuts.Count == 0) return new(false, null, null, false, "합칠 컷이 없습니다.");
        for (var i = 0; i < cuts.Count; i++)
            if (string.IsNullOrWhiteSpace(cuts[i].VideoPath) || !File.Exists(cuts[i].VideoPath))
                return new(false, null, null, false, "컷 파일 누락: " + cuts[i].VideoPath);

        var ffmpeg = SafeResolve();
        if (ffmpeg is null) return new(false, null, null, false, "ffmpeg 미감지 — 환경설정에서 FFmpeg 경로를 지정하세요.");

        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = full + ".part.mp4";
        var list = full + ".concat.txt";
        try
        {
            File.WriteAllText(list, BuildConcatList(cuts), new UTF8Encoding(false));
            var normalized = false;
            var ok = await SafeRunAsync(ffmpeg, BuildCopyArgs(list, temp), ct).ConfigureAwait(false) && IsValidOutput(temp);
            if (!ok && !ct.IsCancellationRequested)
            {
                TryDelete(temp);
                normalized = true;
                ok = await SafeRunAsync(ffmpeg, BuildNormalizeArgs(list, temp), ct).ConfigureAwait(false) && IsValidOutput(temp);
            }
            if (ct.IsCancellationRequested) { TryDelete(temp); return new(false, null, null, normalized, "취소됨"); }
            if (!ok) { TryDelete(temp); return new(false, null, null, normalized, "ffmpeg 합성 실패(스트림 복사 · 정규화 재인코딩 모두 실패)."); }

            File.Move(temp, full, overwrite: true);
            if (!IsValidOutput(full)) return new(false, null, null, normalized, "합성 산출물 검증 실패: " + Path.GetFileName(full));

            string? metaPath = full + MetaExt;
            try { File.WriteAllText(metaPath, JsonSerializer.Serialize(BuildMeta(cuts, full, normalized, DateTime.UtcNow), JsonOpts), new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("StoryboardStitch.Meta", ex); metaPath = null; }

            var n = cuts.Count.ToString(CultureInfo.InvariantCulture);
            return new(true, full, metaPath, normalized, n + "컷 마스터 합성 완료" + (normalized ? "(1280x720 정규화)" : "(스트림 복사)"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StoryboardStitch", ex);
            TryDelete(temp);
            return new(false, null, null, false, "합성 실패: " + ex.Message);
        }
        finally { TryDelete(list); }
    }

    private static bool IsValidOutput(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch (IOException) { return false; }
    }

    private async Task<bool> SafeRunAsync(string exe, string[] args, CancellationToken ct)
    {
        try { return await _run(exe, args, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) { Program.Log("StoryboardStitch.Run", ex); return false; }
    }

    private string? SafeResolve()
    {
        try { return _resolveFfmpeg(); }
        catch (Exception ex) { Program.Log("StoryboardStitch.Resolve", ex); return null; }
    }

    private static string? DefaultResolve() => FfmpegLocator.Locate().Path;

    private static async Task<bool> DefaultRunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
        return r.Ok && r.ExitCode == 0;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
