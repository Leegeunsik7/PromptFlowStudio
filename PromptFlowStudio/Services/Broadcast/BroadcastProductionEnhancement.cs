using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Media;     // FontResolver

namespace PromptFlowStudio.Services.Broadcast;

/// <summary>
/// [PromptFlowStudio] 방송 스위칭 카운트다운 타이머 비주얼(v2.8.0 자율 발상 ①) — FFmpeg drawtext/drawbox(외부 라이브러리 0).
///   ▸ 도입부에 <b>3·2·1 방송 스위칭 카운트다운</b>(대형 중앙 숫자 + 하단 "LIVE SWITCHING" 캡션)을 합성해 생방송 큐 전환 연출을 준다.
/// </summary>
public sealed class SwitchingCountdownService
{
    /// <summary>카운트다운 <c>-vf</c>(초당 대형 숫자 시간 게이트 + 하단 라이브 캡션).</summary>
    public static string BuildCountdownVf(int outW, int outH, string? escFont, int seconds = 3)
    {
        var n = Math.Clamp(seconds, 1, 9);
        var big = Math.Max(80, outH / 3);
        var capFs = Math.Max(22, outH / 30);
        var fontHead = escFont is not null ? $"fontfile={escFont}:" : "";
        var parts = new List<string>();
        for (var k = n; k >= 1; k--)
        {
            var t0 = (n - k).ToString(CultureInfo.InvariantCulture);
            var t1 = (n - k + 1).ToString(CultureInfo.InvariantCulture);
            parts.Add($"drawtext={fontHead}text={k}:fontcolor=white:fontsize={big}:borderw=8:bordercolor=black@0.8:" +
                      $"x=(w-text_w)/2:y=(h-text_h)/2:enable=between(t\\,{t0}\\,{t1})");
        }
        // 하단 라이브 스위칭 캡션(카운트다운 구간 내내 · 빨간 점 펄스).
        parts.Add($"drawtext={fontHead}text=● LIVE SWITCHING:fontcolor=0xff3b30:fontsize={capFs}:borderw=2:bordercolor=black:" +
                  $"x=(w-text_w)/2:y=h-text_h-{Math.Max(24, outH / 12)}:alpha='0.55+0.45*sin(2*PI*t)':enable=between(t\\,0\\,{n})");
        return string.Join(",", parts);
    }

    /// <summary>카운트다운을 영상 도입부에 합성한다(Graceful).</summary>
    public async Task<bool> ApplyAsync(string input, string output, int outW, int outH, int seconds = 3, CancellationToken ct = default)
        => await BroadcastFilterRunner.RunVfAsync(input, output, BuildCountdownVf(outW, outH, BroadcastFilterRunner.Font(), seconds), ct).ConfigureAwait(false);
}

/// <summary>
/// [PromptFlowStudio] 4K ON AIR 글래스모피즘 뱃지(v2.8.0 자율 발상 ②) — FFmpeg drawbox/drawtext(외부 라이브러리 0).
///   ▸ 최종 영상 우측 상단에 <b>반투명 글래스 패널 + 빨간 테두리 + 펄스하는 ● ON AIR</b> 뱃지를 합성해 방송 온에어 인상을 준다.
/// </summary>
public sealed class OnAirBadgeService
{
    /// <summary>ON AIR 뱃지 <c>-vf</c>(우측 상단 글래스 패널 + 펄스 텍스트).</summary>
    public static string BuildOnAirVf(int outW, int outH, string? escFont)
    {
        var scale = Math.Clamp(outW / 1920.0, 0.5, 3.0);
        int P(double v) => (int)Math.Round(v * scale);
        int bw = P(240), bh = P(70), bx = outW - bw - P(36), by = P(36), fs = P(34);
        var fontHead = escFont is not null ? $"fontfile={escFont}:" : "";
        return
            $"drawbox=x={bx}:y={by}:w={bw}:h={bh}:color=black@0.32:t=fill," +           // 글래스 패널(반투명)
            $"drawbox=x={bx}:y={by}:w={bw}:h={bh}:color=0xff3b30@0.9:t={Math.Max(2, P(3))}," + // 빨간 테두리
            $"drawtext={fontHead}text=● ON AIR:fontcolor=white:fontsize={fs}:x={bx}+({bw}-text_w)/2:y={by}+({bh}-text_h)/2:" +
            $"alpha='0.6+0.4*sin(2*PI*t)'";                                              // 펄스(온에어 점멸)
    }

    /// <summary>ON AIR 뱃지를 영상에 합성한다(Graceful).</summary>
    public async Task<bool> ApplyAsync(string input, string output, int outW, int outH, CancellationToken ct = default)
        => await BroadcastFilterRunner.RunVfAsync(input, output, BuildOnAirVf(outW, outH, BroadcastFilterRunner.Font()), ct).ConfigureAwait(false);
}

/// <summary>방송 오버레이 공용 실행기 — <c>-vf</c> 한 줄을 libx264 로 번인(내부 공유).</summary>
internal static class BroadcastFilterRunner
{
    internal static string? Font()
    {
        var f = FontResolver.FindKoreanFont(bold: true);
        return f is not null ? FontResolver.EscapeDrawPath(f) : null;
    }

    internal static async Task<bool> RunVfAsync(string input, string output, string vf, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input) || string.IsNullOrEmpty(vf)) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;
        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
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
        catch (Exception ex) { Program.Log("Broadcast.RunVf", ex); return false; }
    }

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
