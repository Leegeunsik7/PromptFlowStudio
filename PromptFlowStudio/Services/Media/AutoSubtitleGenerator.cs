using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.14.0 자막 1줄(블록) — 1-기반 번호 · 시작/끝(초) · 표시 텍스트(줄바꿈 '\n').</summary>
public sealed record CaptionCue(int Index, double Start, double End, string Text);

/// <summary>v16.14.0 자막 서체/외곽선 스타일 — FontSize 0 이면 세이프존 계산값(높이 비례) 사용. 색은 ASS &amp;HAABBGGRR.</summary>
public sealed record SubtitleStyle(
    string FontName = "Malgun Gothic", int FontSize = 0, bool Bold = true,
    string PrimaryColour = "&H00FFFFFF", string OutlineColour = "&H00000000", string BackColour = "&H64000000",
    double Outline = 0, double Shadow = 1.5, int MaxCharsPerLine = 16, int MaxLines = 2);

/// <summary>v16.14.0 숏폼 세이프존 자막 배치 — 하단 여백(MarginV) · 좌우 여백(우측 액션 버튼 회피) · 폰트 크기(px, PlayRes 기준).</summary>
public sealed record SubtitleLayout(SafePlatform Platform, int Width, int Height, int MarginV, int MarginL, int MarginR, int FontSize, double Outline);

/// <summary>
/// [PromptFlowStudio] v16.14.0 💬 숏폼 세이프존 자동 자막 생성기 — 나레이션 타임라인 → SRT/ASS + ffmpeg <c>subtitles</c> 번인 필터(순수 BCL).
///   ▸ 세이프존: <see cref="MultiPlatformSafeZoneService.CaptionBottomFraction"/> 로 하단 UI(제목·계정·음악) 높이를 MarginV 로 환산,
///     TikTok/Shorts/Reels 우측 좋아요·댓글 버튼 열은 <see cref="RightRailFraction"/> 만큼 좌우 대칭 여백(중앙 정렬 유지).
///   ▸ 분할: 문장 부호 → 줄당 글자 상한(어절 경계) → 최대 줄 수 단위 블록 · 블록 시간은 글자 수 비례 배분(최소 <see cref="MinCueSeconds"/>).
///   ▸ 스타일: 아동 동화 가독성 기본값(굵은 흰 글자 · 검은 외곽선 높이/360 · 그림자) · ASS 오버라이드 중괄호는 무해화.
///   ▸ 문자열 생성은 비핫패스(내보내기 1회) — 숫자는 InvariantCulture 고정.
/// </summary>
public sealed class AutoSubtitleGenerator
{
    public const double MinCueSeconds = 0.8;

    /// <summary>플랫폼 세이프존 계산(순수 함수).</summary>
    public static SubtitleLayout ComputeLayout(SafePlatform platform, int width, int height, SubtitleStyle? style = null)
    {
        var st = style ?? new SubtitleStyle();
        var w = Math.Max(2, width);
        var h = Math.Max(2, height);
        var marginV = (int)Math.Round(h * MultiPlatformSafeZoneService.CaptionBottomFraction(platform));
        var side = (int)Math.Round(w * Math.Max(0.05, RightRailFraction(platform)));
        var font = st.FontSize > 0 ? st.FontSize : Math.Max(24, (int)Math.Round(h / (h > w ? 26.0 : 18.0)));
        var outline = st.Outline > 0 ? st.Outline : Math.Max(2, Math.Round(h / 360.0, 1));
        return new(platform, w, h, marginV, side, side, font, outline);
    }

    /// <summary>우측 액션 버튼 열 폭(화면 너비 비율) — 좌우 대칭 여백으로 적용.</summary>
    public static double RightRailFraction(SafePlatform p) => p switch
    {
        SafePlatform.TikTok => 0.15,
        SafePlatform.YouTubeShorts => 0.13,
        SafePlatform.InstagramReels => 0.12,
        SafePlatform.None => 0.05,
        _ => 0.10,
    };

    /// <summary>나레이션 큐 → 자막 블록(시간순 · 블록 간 겹침 없음).</summary>
    public static List<CaptionCue> BuildCues(IReadOnlyList<VoiceCue> voice, SubtitleStyle? style = null)
    {
        var st = style ?? new SubtitleStyle();
        var perLine = Math.Max(4, st.MaxCharsPerLine);
        var maxLines = Math.Max(1, st.MaxLines);
        var list = new List<CaptionCue>(voice.Count * 2);
        var lines = new List<string>(8);
        var sb = new StringBuilder(64);
        for (var v = 0; v < voice.Count; v++)
        {
            var cue = voice[v];
            var dur = Math.Max(MinCueSeconds, cue.DurationSeconds);
            var limit = v + 1 < voice.Count ? Math.Min(cue.AtSeconds + dur, voice[v + 1].AtSeconds) : cue.AtSeconds + dur;
            lines.Clear();
            Wrap(cue.Text, perLine, lines);
            if (lines.Count == 0) continue;

            var total = 0;
            foreach (var l in lines) total += l.Length;
            var span = Math.Max(MinCueSeconds, limit - cue.AtSeconds);
            var t = cue.AtSeconds;
            for (var i = 0; i < lines.Count; i += maxLines)
            {
                sb.Clear();
                var chars = 0;
                for (var k = i; k < lines.Count && k < i + maxLines; k++)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(lines[k]);
                    chars += lines[k].Length;
                }
                var last = i + maxLines >= lines.Count;
                var end = last ? cue.AtSeconds + span : Math.Min(cue.AtSeconds + span, t + Math.Max(MinCueSeconds, span * chars / Math.Max(1, total)));
                list.Add(new CaptionCue(list.Count + 1, Math.Round(t, 3), Math.Round(end, 3), sb.ToString()));
                t = end;
            }
        }
        return list;
    }

    /// <summary>어절 경계 줄바꿈 — 문장 부호 뒤에서 우선 끊고, 상한 초과 어절은 강제 분할.</summary>
    public static void Wrap(string? text, int perLine, List<string> into)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var line = new StringBuilder(perLine + 8);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in words)
        {
            var w = raw;
            while (w.Length > perLine)
            {
                Flush(line, into);
                into.Add(w[..perLine]);
                w = w[perLine..];
            }
            if (line.Length > 0 && line.Length + 1 + w.Length > perLine) Flush(line, into);
            if (line.Length > 0) line.Append(' ');
            line.Append(w);
            var end = w[^1];
            if (end is '.' or '!' or '?' or '…') Flush(line, into);
        }
        Flush(line, into);

        static void Flush(StringBuilder sb, List<string> to)
        {
            if (sb.Length == 0) return;
            to.Add(sb.ToString());
            sb.Clear();
        }
    }

    /// <summary>SRT 직렬화(CRLF · HH:MM:SS,mmm).</summary>
    public static string ToSrt(IReadOnlyList<CaptionCue> cues)
    {
        var sb = new StringBuilder(cues.Count * 64);
        foreach (var c in cues)
        {
            sb.Append(c.Index.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            AppendTime(sb, c.Start, ',', 3).Append(" --> ");
            AppendTime(sb, c.End, ',', 3).Append("\r\n");
            sb.Append(c.Text.Replace("\n", "\r\n", StringComparison.Ordinal)).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    /// <summary>ASS 직렬화 — PlayRes = 영상 해상도 · 하단 중앙(Alignment 2) · 세이프존 여백 · 외곽선/그림자 스타일.</summary>
    public static string ToAss(IReadOnlyList<CaptionCue> cues, SubtitleLayout layout, SubtitleStyle? style = null)
    {
        var st = style ?? new SubtitleStyle();
        var sb = new StringBuilder(1024 + cues.Count * 96);
        sb.Append("[Script Info]\r\nScriptType: v4.00+\r\nWrapStyle: 2\r\nScaledBorderAndShadow: yes\r\nYCbCr Matrix: TV.709\r\n")
          .Append("PlayResX: ").Append(layout.Width.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
          .Append("PlayResY: ").Append(layout.Height.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n")
          .Append("[V4+ Styles]\r\n")
          .Append("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\r\n")
          .Append("Style: Narration,").Append(Clean(st.FontName)).Append(',').Append(layout.FontSize.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(st.PrimaryColour).Append(",&H000000FF,").Append(st.OutlineColour).Append(',').Append(st.BackColour).Append(',')
          .Append(st.Bold ? "-1" : "0").Append(",0,0,0,100,100,0,0,1,")
          .Append(FfmpegVideoMixerService.Inv(layout.Outline)).Append(',').Append(FfmpegVideoMixerService.Inv(st.Shadow)).Append(",2,")
          .Append(layout.MarginL.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(layout.MarginR.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(layout.MarginV.ToString(CultureInfo.InvariantCulture)).Append(",1\r\n\r\n")
          .Append("[Events]\r\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\r\n");
        foreach (var c in cues)
        {
            sb.Append("Dialogue: 0,");
            AppendTime(sb, c.Start, '.', 2).Append(',');
            AppendTime(sb, c.End, '.', 2).Append(",Narration,,0,0,0,,");
            sb.Append(Clean(c.Text).Replace("\n", "\\N", StringComparison.Ordinal)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>ffmpeg 번인 필터(<c>subtitles</c>/libass) — 경로 콜론·역슬래시·따옴표 이스케이프.</summary>
    public static string BuildBurnFilter(string assPath, string? fontsDir = null)
    {
        var sb = new StringBuilder("subtitles=filename='", 96).Append(EscapeFilterPath(assPath)).Append('\'');
        if (!string.IsNullOrWhiteSpace(fontsDir)) sb.Append(":fontsdir='").Append(EscapeFilterPath(fontsDir)).Append('\'');
        return sb.ToString();
    }

    /// <summary>SRT + ASS 를 같은 이름으로 기록(UTF-8 BOM — 플레이어/libass 한글 인식). 반환: ASS 경로.</summary>
    public static async Task<string> WriteAsync(string basePath, IReadOnlyList<CaptionCue> cues, SubtitleLayout layout,
        SubtitleStyle? style = null, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(basePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var enc = new UTF8Encoding(true);
        var srt = Path.ChangeExtension(basePath, ".srt");
        var ass = Path.ChangeExtension(basePath, ".ass");
        await File.WriteAllTextAsync(srt, ToSrt(cues), enc, ct).ConfigureAwait(false);
        await File.WriteAllTextAsync(ass, ToAss(cues, layout, style), enc, ct).ConfigureAwait(false);
        return ass;
    }

    private static string EscapeFilterPath(string p) => (p ?? "").Replace('\\', '/').Replace(":", "\\:", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    // ASS 오버라이드 블록/필드 구분자 무해화(대본에 중괄호·쉼표 이름 등).
    private static string Clean(string s) => (s ?? "").Replace('{', '(').Replace('}', ')').Replace("\r", "", StringComparison.Ordinal);

    private static StringBuilder AppendTime(StringBuilder sb, double seconds, char sep, int fracDigits)
    {
        var unit = fracDigits == 3 ? 1000L : 100L;
        var total = (long)Math.Round(Math.Max(0, seconds) * unit);
        var frac = total % unit;
        var s = total / unit;
        var hh = s / 3600;
        if (fracDigits == 3 || hh >= 10) sb.Append((hh / 10).ToString(CultureInfo.InvariantCulture));
        sb.Append((hh % 10).ToString(CultureInfo.InvariantCulture)).Append(':')
          .Append((s / 60 % 60).ToString("00", CultureInfo.InvariantCulture)).Append(':')
          .Append((s % 60).ToString("00", CultureInfo.InvariantCulture)).Append(sep)
          .Append(frac.ToString(fracDigits == 3 ? "000" : "00", CultureInfo.InvariantCulture));
        return sb;
    }
}
