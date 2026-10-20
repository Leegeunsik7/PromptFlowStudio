using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Media;

/// <summary>플랫폼 규격별 자막 배치 — 폰트 크기 + 자막(하단 앵커)/뱃지(상단 앵커) Y 좌표(px).</summary>
public sealed record SubtitlePlacement(SafePlatform Platform, int Width, int Height, int FontSize, int CaptionYFromTop, int BadgeYFromTop, int BottomMarginPx);

/// <summary>
/// [PromptFlowStudio] 플랫폼 화면비별 동적 자막 재-번인 연산기 — 순수 BCL + FFmpeg CLI(외부 NuGet 0).
///   ▸ 각 플랫폼 규격(핀터레스트 2:3 · 샤오홍슈 3:4 · 세로 숏폼 9:16)의 <b>세이프존 Y-앵커</b>(<see cref="MultiPlatformSafeZoneService"/>)에 맞춰
///     <b>자막 위치와 폰트 크기를 동적으로 재계산</b>(<see cref="Compute"/>)하고, <c>drawtext</c> 로 번인한다(<see cref="BurnAsync"/>).
///   ▸ 폰트 크기는 규격 세로 높이에 비례(<see cref="VideoMergeService"/>와 동일 스케일 h/22) → 낮은 규격은 작게, 높은 규격은 크게 자동.
///   ▸ 한글 안전: 자막을 임시 UTF-8 textfile 로 기록해 drawtext <c>textfile=</c> 로 번인(인라인 이스케이프 회피). ffmpeg 부재/실패는 Graceful.
/// </summary>
public sealed class DynamicPlatformSubtitleService
{
    /// <summary>플랫폼 규격(width×height)에 맞는 자막/뱃지 Y-앵커와 폰트 크기를 계산한다.</summary>
    public SubtitlePlacement Compute(SafePlatform platform, int width, int height)
    {
        var w = Math.Max(2, width);
        var h = Math.Max(2, height);
        var bottomFrac = MultiPlatformSafeZoneService.CaptionBottomFraction(platform);   // 자막 하단 마진 비율(세이프존)
        var topFrac = MultiPlatformSafeZoneService.BadgeTopFraction(platform);           // 뱃지 상단 위치 비율
        var bottomMargin = (int)Math.Round(h * bottomFrac);
        var fontSize = Math.Max(22, (int)Math.Round(h / 22.0));                          // 규격 높이 비례(VideoMergeService 동일)
        var captionY = Math.Max(0, h - bottomMargin - fontSize);                         // 하단 앵커(텍스트 top 좌표)
        var badgeY = (int)Math.Round(h * topFrac);
        return new(platform, w, h, fontSize, captionY, badgeY, bottomMargin);
    }

    /// <summary>계산된 배치로 <c>drawtext</c> 필터 문자열을 만든다(중앙 하단 앵커 · 노란 글자·검은 테두리).</summary>
    public string BuildCaptionDrawtext(string? text, SubtitlePlacement p, string? fontFile, string? textFile = null)
    {
        var sb = new StringBuilder("drawtext=");
        if (fontFile is not null) sb.Append("fontfile=").Append(FontResolver.EscapeDrawPath(fontFile)).Append(':');
        if (textFile is not null) sb.Append("textfile=").Append(FontResolver.EscapeDrawPath(textFile)).Append(':');
        else sb.Append("text='").Append(EscapeInline(text ?? "")).Append("':");
        sb.Append("fontcolor=yellow:fontsize=").Append(p.FontSize)
          .Append(":borderw=2:bordercolor=black:x=(w-text_w)/2:y=h-text_h-").Append(p.BottomMarginPx)
          .Append(":line_spacing=8");
        return sb.ToString();
    }

    /// <summary>
    /// 규격 영상에 플랫폼 세이프존에 맞춰 자막을 번인한다. 빈 텍스트/ffmpeg 부재/실패는 false(호출부가 원본 유지 · Graceful).
    /// </summary>
    public async Task<bool> BurnAsync(string input, string output, string? text, SafePlatform platform, int width, int height, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(input) || !File.Exists(input)) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;

        var placement = Compute(platform, width, height);
        var fontFile = FontResolver.FindKoreanFont(bold: false);
        string? txtFile = null;
        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var tmpDir = Path.Combine(Path.GetTempPath(), "promptflow-platsub");
            Directory.CreateDirectory(tmpDir);
            txtFile = Path.Combine(tmpDir, $"sub_{Guid.NewGuid():N}.txt");
            File.WriteAllText(txtFile, text!.Trim(), new UTF8Encoding(false));

            var vf = BuildCaptionDrawtext(null, placement, fontFile, txtFile);
            var args = new List<string>
            {
                "-y", "-i", input, "-vf", vf,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "copy", "-movflags", "+faststart", output,
            };
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(output);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("DynamicPlatformSubtitle.Burn", ex); return false; }
        finally { try { if (txtFile is not null && File.Exists(txtFile)) File.Delete(txtFile); } catch { } }
    }

    private static string EscapeInline(string s) =>
        s.Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "’");   // 최소 이스케이프(가급적 textfile 사용)

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
}
