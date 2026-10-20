using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Media;

/// <summary>숏츠 커버(썸네일) 생성 계약 — 포스터/영상 위에 대형 문구를 합성한다.</summary>
public interface IShortsCoverService
{
    /// <summary>ffmpeg 사용 가능 여부(커버 생성 가능).</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 배경 이미지(또는 영상 1초 지점 프레임) 위에 <paramref name="coverText"/> 를 대형 굵은 글씨(스트로크·반투명 박스)로 합성해 저장한다.
    ///   실패(ffmpeg 부재/에러/빈 문구)는 예외 없이 null 반환(Graceful).
    /// </summary>
    /// <param name="source">배경 — 포스터 이미지(.jpg/.png…) 또는 영상(.mp4). 영상이면 1초 지점 프레임을 사용.</param>
    /// <param name="coverText">커버 문구(줄바꿈 허용 · 길면 자동 줄맞춤 · 최대 4줄).</param>
    /// <param name="outputPath">출력 이미지 경로(cover.jpg).</param>
    /// <param name="fontColor">글자색(예: yellow, white). 기본 yellow.</param>
    /// <param name="boxBackground">반투명 검은 배경 상자 여부. 기본 true.</param>
    Task<string?> GenerateAsync(string source, string coverText, string outputPath,
        string fontColor = "yellow", bool boxBackground = true, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 숏츠 커버 썸네일 — 순수 BCL <see cref="Process"/> 로 ffmpeg <c>drawtext</c> 합성(외부 NuGet 0).
///   ▸ ffmpeg 경로는 <see cref="IVideoMergeService.ResolvedFfmpegPath"/> 재사용(중복 탐색 방지).
///   ▸ 배경을 가로 1080 으로 스케일(세로 비율 유지)해 폰트 크기를 일관되게 → 화면 중앙에 굵은 글씨 + 검은 테두리(4px) + 반투명 박스.
///   ▸ 문구는 임시 UTF-8 파일(textfile=)로 넘겨 한글/특수문자 인라인 이스케이프를 회피(자막과 동일 전략), 경로는 InvariantCulture/이스케이프 안전.
///   ▸ 길면 자동 줄맞춤(최대 4줄)·글자수에 맞춰 폰트 크기 조절. 어떤 실패든 null(Graceful).
/// </summary>
public sealed class ShortsCoverService(IVideoMergeService merge) : IShortsCoverService
{
    private const int TargetWidth = 1080;    // 스케일 기준 폭(폰트 크기 일관성)
    private const int MaxLines = 4;
    private const int MaxCharsPerLine = 11;  // 대략 1줄 글자수(가로 1080 · 대형 폰트 기준)

    /// <inheritdoc/>
    public bool IsAvailable => merge.IsAvailable;

    /// <inheritdoc/>
    public async Task<string?> GenerateAsync(string source, string coverText, string outputPath,
        string fontColor = "yellow", bool boxBackground = true, CancellationToken ct = default)
    {
        var text = (coverText ?? "").Trim();
        if (text.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return null;

        var ffmpeg = merge.ResolvedFfmpegPath;
        if (ffmpeg is null) return null;

        var color = string.IsNullOrWhiteSpace(fontColor) ? "yellow" : fontColor.Trim();
        string? txtFile = null;
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 자동 줄맞춤 → 임시 텍스트 파일(UTF-8, BOM 없음).
            var wrapped = WrapText(text, MaxCharsPerLine, MaxLines);
            var maxLen = 1;
            foreach (var ln in wrapped.Split('\n')) maxLen = Math.Max(maxLen, ln.Length);
            var lineCount = wrapped.Split('\n').Length;

            var tmpDir = Path.Combine(Path.GetTempPath(), "promptflow-cover");
            Directory.CreateDirectory(tmpDir);
            txtFile = Path.Combine(tmpDir, $"cover_{Guid.NewGuid():N}.txt");
            File.WriteAllText(txtFile, wrapped, new UTF8Encoding(false));

            // 폰트 크기: 가장 긴 줄이 폭 안에 들어오도록(가로 1080 기준) · 줄 수가 많으면 세로 넘침 방지.
            var fsByWidth  = 980.0 / Math.Max(6, maxLen);
            var fsByHeight = 1500.0 / Math.Max(1, lineCount) / 1.35;   // 세로 여유(세로 쇼츠 1920 기준 넉넉)
            var fontSize = (int)Math.Clamp(Math.Min(fsByWidth, fsByHeight), 44, 108);

            var isVideo = IsVideo(source);
            var fontFile = FindKoreanFontBold();

            var vf = new StringBuilder();
            vf.Append($"scale={TargetWidth}:-2,");
            vf.Append("drawtext=");
            if (fontFile is not null) vf.Append($"fontfile={EscDrawPath(fontFile)}:");
            vf.Append($"textfile={EscDrawPath(txtFile)}:");
            vf.Append($"fontcolor={color}:fontsize={fontSize}:");
            vf.Append("borderw=4:bordercolor=black:");
            if (boxBackground) vf.Append("box=1:boxcolor=black@0.45:boxborderw=28:");
            vf.Append($"line_spacing=14:x=(w-text_w)/2:y=(h-text_h)/2");

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg, RedirectStandardError = true, RedirectStandardOutput = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-y");
            if (isVideo) { psi.ArgumentList.Add("-ss"); psi.ArgumentList.Add("00:00:01"); }   // 영상은 1초 지점 프레임
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(source);
            psi.ArgumentList.Add("-vf"); psi.ArgumentList.Add(vf.ToString());
            psi.ArgumentList.Add("-frames:v"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-q:v"); psi.ArgumentList.Add("2");
            psi.ArgumentList.Add(outputPath);

            using var proc = new Process { StartInfo = psi };
            proc.ErrorDataReceived += (_, __) => { };   // 버퍼 드레인(데드락 방지)
            proc.OutputDataReceived += (_, __) => { };
            if (!proc.Start()) return null;
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } return null; }

            return proc.ExitCode == 0 && File.Exists(outputPath) ? outputPath : null;
        }
        catch (Exception ex) { Program.Log("ShortsCoverService.Generate", ex); return null; }
        finally { try { if (txtFile is not null && File.Exists(txtFile)) File.Delete(txtFile); } catch { /* 무시 */ } }
    }

    // ── 자동 줄맞춤 — 사용자 줄바꿈 우선, 각 줄이 max 초과 시 공백(없으면 글자수) 기준으로 접고, 최대 줄 수 제한 ──
    private static string WrapText(string text, int max, int maxLines)
    {
        var outLines = new List<string>();
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.Length <= max) { outLines.Add(line); continue; }

            // 공백이 있으면 단어 단위로, 없으면(한글 문장 등) 글자수로 하드랩.
            if (line.Contains(' '))
            {
                var sb = new StringBuilder();
                foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (sb.Length == 0) sb.Append(word);
                    else if (sb.Length + 1 + word.Length <= max) sb.Append(' ').Append(word);
                    else { outLines.Add(sb.ToString()); sb.Clear(); sb.Append(word); }
                }
                if (sb.Length > 0) outLines.Add(sb.ToString());
            }
            else
            {
                for (var i = 0; i < line.Length; i += max)
                    outLines.Add(line.Substring(i, Math.Min(max, line.Length - i)));
            }
        }

        if (outLines.Count == 0) return text.Trim();
        if (outLines.Count > maxLines)
        {
            var kept = outLines.GetRange(0, maxLines);
            kept[maxLines - 1] = Clip(kept[maxLines - 1], max);   // 마지막 줄 말줄임
            outLines = kept;
        }
        return string.Join("\n", outLines);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max - 1)] + "…";

    private static bool IsVideo(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mov" or ".mkv" or ".webm" or ".avi" or ".m4v";
    }

    /// <summary>drawtext 경로 이스케이프 — 공유 <see cref="FontResolver"/>(역슬래시→'/', 콜론→'\:' · 로캘 무관).</summary>
    private static string EscDrawPath(string p) => FontResolver.EscapeDrawPath(p);

    /// <summary>한글 굵은 번인 폰트 탐색 — 공유 <see cref="FontResolver"/>(데이터폴더 시딩 → 앱번들 → 시스템 → null).</summary>
    private static string? FindKoreanFontBold() => FontResolver.FindKoreanFont(bold: true);
}
