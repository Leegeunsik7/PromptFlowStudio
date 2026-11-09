using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Publishing;

namespace PromptFlowStudio.Services.Export;

/// <summary>
/// 📱 SNS 패스트트랙 실필터 — YouTube Shorts(-14 LUFS loudnorm) · Instagram Reels(30fps + 소프트 필름 그레인 noise).
/// </summary>
public sealed partial class StockSuiteExporter
{
    /// <summary>🔴 YouTube Shorts 라우드니스 정규화(-14 LUFS · LRA 7 · TP -1.5).</summary>
    public const string YouTubeLoudnormFilter = "loudnorm=I=-14:LRA=7:TP=-1.5";
    /// <summary>🟣 Instagram Reels 소프트 홈필름 그레인.</summary>
    public const string ReelsFilmGrainFilter = "noise=alls=8:allf=t+u";

    /// <summary>프리셋 오디오 필터(라우드니스 목표 없음 → null).</summary>
    public static string? SocialAudioFilter(SocialExportPreset? p)
    {
        if (p?.LoudnessLufs is not { } l) return null;
        if (Math.Abs(l - (-14.0)) < 1e-9) return YouTubeLoudnormFilter;
        return "loudnorm=I=" + l.ToString("0.#", CultureInfo.InvariantCulture) + ":LRA=7:TP=-1.5";
    }

    /// <summary>프리셋 비디오 필터 꼬리(그레인 미사용 → null).</summary>
    public static string? SocialVideoFilter(SocialExportPreset? p) => p is { FilmGrain: true } ? ReelsFilmGrainFilter : null;

    /// <summary>기존 체인 뒤에 프리셋 비디오 필터 결합.</summary>
    public static string AppendSocialVideoFilter(string baseFilter, SocialExportPreset? p)
        => SocialVideoFilter(p) is { } v ? (string.IsNullOrEmpty(baseFilter) ? v : baseFilter + "," + v) : baseFilter;

    /// <summary>기존 오디오 체인 뒤에 프리셋 라우드니스 결합(<paramref name="baseFilter"/> null 허용).</summary>
    public static string? AppendSocialAudioFilter(string? baseFilter, SocialExportPreset? p)
        => SocialAudioFilter(p) is { } a ? (string.IsNullOrEmpty(baseFilter) ? a : baseFilter + "," + a) : baseFilter;

    /// <summary>
    /// 📱 SNS 프리셋 렌더 인자 — 9:16 크롭/스케일(+그레인) · <c>-r fps</c> · H.264 · (오디오 있으면) loudnorm + AAC.
    /// 오디오 스트림 부재 대비 <c>-map 0:a:0?</c>(선택 매핑).
    /// </summary>
    public static string[] BuildSocialArgs(string inputPath, string outputPath, SocialExportPreset preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(preset);
        var inv = CultureInfo.InvariantCulture;
        var vf = AppendSocialVideoFilter("crop=min(iw\\,ih*9/16):ih,scale=" + preset.Width.ToString(inv) + ":" + preset.Height.ToString(inv) + ":flags=lanczos", preset);
        var a = new List<string>(40) { "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath, "-map", "0:v:0", "-map", "0:a:0?", "-vf", vf, "-r", preset.Fps.ToString(inv),
            "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p" };
        if (SocialAudioFilter(preset) is { } af) { a.Add("-af"); a.Add(af); }
        a.Add("-c:a"); a.Add("aac"); a.Add("-b:a"); a.Add(AudioMuxService.AudioBitrate);
        a.Add("-sn"); a.Add("-dn");
        if (outputPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) { a.Add("-movflags"); a.Add("+faststart"); }
        a.Add(outputPath);
        return a.ToArray();
    }

    /// <summary>프리셋 산출물 경로 — 원본 옆 <c>{name}_shorts.mp4</c> | <c>{name}_reels.mp4</c>.</summary>
    public static string SocialOutputPath(string sourcePath, SocialExportPreset preset)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        var suffix = preset.Kind == SocialPresetKind.InstagramReels ? "_reels.mp4" : "_shorts.mp4";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(sourcePath) + suffix);
    }

    /// <summary>
    /// 📱 SNS 프리셋 렌더 실행 — 성공 시 산출 경로, ffmpeg 부재/실패 시 Ok=false(호출부 원본 유지 [부분 구제]). 절대 throw 하지 않음(취소 제외).
    /// </summary>
    public async Task<StockSuiteExportResult> RenderSocialAsync(string sourcePath, SocialExportPreset preset, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath) || preset is null)
            return new(false, null, false, "원본 영상을 찾을 수 없습니다.");
        string output;
        try { output = SocialOutputPath(sourcePath, preset); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return new(false, null, false, ex.Message); }
        var err = await TryFfmpegAsync(BuildSocialArgs(sourcePath, output, preset), output, ct, ExportProfile.SnsShorts).ConfigureAwait(false);
        return err is null
            ? new(true, output, false, "📱 " + preset.Label + " 렌더 완료(" + preset.SpecSummary + ") → " + output)
            : new(false, null, false, "⚠ " + preset.Label + " 렌더 실패(" + err + ") — 원본으로 진행");
    }
}
