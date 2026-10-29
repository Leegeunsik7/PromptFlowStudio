using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PromptFlowStudio.Services.Export;

/// <summary>자막 컷 1개 — 재생 길이(초)와 대본. 시작 시각은 앞 컷 길이 누적.</summary>
public readonly record struct SubtitleCut(double DurationSec, string? Script);

/// <summary>
/// [PromptFlowStudio] 컷 타임라인 → 표준 SubRip(.srt) 생성기.
///   ▸ 시작 = 앞 컷 길이 누적, 종료 = 시작 + 길이(<c>HH:mm:ss,fff</c> · 밀리초 반올림).
///   ▸ 대본이 빈 컷은 번호 없이 시간만 소비(자막 공백 구간). 줄바꿈은 CRLF 정규화, 대본 내 빈 줄은 제거(블록 분리자 보호).
///   ▸ 합본 비디오 옆 <c>{project}_full.srt</c> 원자적 기록(UTF-8 BOM 없음).
/// </summary>
public sealed class SubtitleGeneratorService
{
    /// <summary><c>00:00:05,000</c> — 음수는 0, 24시간 초과도 누적 시 표기.</summary>
    public static string FormatTimestamp(double seconds)
    {
        var ms = (long)Math.Round(Math.Max(0, seconds) * 1000.0, MidpointRounding.AwayFromZero);
        var h = ms / 3_600_000; ms -= h * 3_600_000;
        var m = ms / 60_000; ms -= m * 60_000;
        var s = ms / 1000; ms -= s * 1000;
        return string.Create(CultureInfo.InvariantCulture, $"{h:00}:{m:00}:{s:00},{ms:000}");
    }

    public static string BuildSrt(IReadOnlyList<SubtitleCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        var sb = new StringBuilder(cuts.Count * 64);
        var start = 0.0;
        var no = 0;
        for (var i = 0; i < cuts.Count; i++)
        {
            var dur = cuts[i].DurationSec > 0 && double.IsFinite(cuts[i].DurationSec) ? cuts[i].DurationSec : 0;
            var text = NormalizeText(cuts[i].Script);
            if (text.Length > 0 && dur > 0)
            {
                no++;
                sb.Append(no.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                  .Append(FormatTimestamp(start)).Append(" --> ").Append(FormatTimestamp(start + dur)).Append("\r\n")
                  .Append(text).Append("\r\n\r\n");
            }
            start += dur;
        }
        return sb.ToString();
    }

    /// <summary><c>{dir}\{name}_full.mp4</c> → <c>{dir}\{name}_full.srt</c>.</summary>
    public static string SrtPathFor(string videoPath) => Path.ChangeExtension(videoPath, ".srt");

    /// <summary>SRT 원자적 기록 — 자막 블록이 없으면 기록 생략(null). 실패 격리(null).</summary>
    public string? WriteSrt(IReadOnlyList<SubtitleCut> cuts, string srtPath)
    {
        var body = BuildSrt(cuts);
        if (body.Length == 0 || string.IsNullOrWhiteSpace(srtPath)) return null;
        var full = Path.GetFullPath(srtPath);
        var tmp = full + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, body, new UTF8Encoding(false));
            File.Move(tmp, full, overwrite: true);
            return full;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("Subtitle.Write", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return null;
        }
    }

    private static string NormalizeText(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return "";
        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder(script.Length);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0) sb.Append("\r\n");
            sb.Append(line);
        }
        return sb.ToString();
    }
}
