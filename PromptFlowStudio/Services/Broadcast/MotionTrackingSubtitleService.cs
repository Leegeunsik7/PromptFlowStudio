using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Media;     // FontResolver

namespace PromptFlowStudio.Services.Broadcast;

/// <summary>추적 궤적 1점 — 시각(초) + 피사체 중심(정규화 0~1).</summary>
public sealed record TrackPoint(double TimeSec, double XNorm, double YNorm);

/// <summary>모션 트랙 — 궤적 점 목록 + 시작 ROI.</summary>
public sealed record MotionTrack(IReadOnlyList<TrackPoint> Points, double RoiXNorm, double RoiYNorm)
{
    public bool HasData => Points.Count >= 1;
}

/// <summary>
/// [PromptFlowStudio] 오프라인 픽셀 모션 트래킹 &amp; 동적 추종 자막(v2.8.0) — 순수 C# BCL 블록 매칭 + FFmpeg drawtext(외부 ML 라이브러리 0).
///   ▸ FFmpeg 로 프레임을 <c>gray rawvideo</c>(160×90)로 다중 디코드해 관심 영역(ROI)을 <b>블록 매칭(SAD 최소화)</b>으로 프레임 간 추적 → XY 궤적 스트림.
///   ▸ 궤적을 FFmpeg <c>drawtext</c> 좌표식(키프레임 시간 게이트)으로 변환 → 지적 라벨/자막이 <b>피사체를 매끄럽게 추종</b>.
///   ▸ 옵티컬 플로우 근사: 이전 프레임 ROI 블록을 현재 프레임 탐색창에서 최소 SAD 변위로 정합(순수 정수 연산). ffmpeg/디코드 실패는 Graceful.
/// </summary>
public sealed class MotionTrackingSubtitleService
{
    private const int W = 160, H = 90;     // 추적 해상도(연산량↓·궤적 충분)
    private const int SampleFps = 6;
    private const int MaxFrames = 60;
    private const int BlockRadius = 7;     // ROI 블록 반경(15×15)
    private const int SearchRadius = 10;   // 프레임 간 탐색창 반경

    // ══════════════════════ 트래킹 ══════════════════════

    /// <summary>영상의 ROI(정규화)를 블록 매칭으로 추적해 궤적을 만든다. ffmpeg/저신뢰는 빈 트랙(Graceful).</summary>
    public async Task<MotionTrack> TrackAsync(string videoPath, double roiX, double roiY, double roiW, double roiH, CancellationToken ct = default)
    {
        var empty = new MotionTrack(Array.Empty<TrackPoint>(), roiX, roiY);
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath) || roiW <= 0 || roiH <= 0) return empty;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return empty;

        try
        {
            var frames = await DecodeGrayFramesAsync(ffmpeg, videoPath, ct).ConfigureAwait(false);
            if (frames.Count < 1) return empty;
            return Track(frames, W, H, roiX + roiW / 2, roiY + roiH / 2, SampleFps);
        }
        catch (OperationCanceledException) { return empty; }
        catch (Exception ex) { Program.Log("MotionTrack.Track", ex); return empty; }
    }

    /// <summary>그레이 프레임 시퀀스에서 시작 중심(정규화)을 블록 매칭 추적한다(순수 · 테스트 대상).</summary>
    public static MotionTrack Track(IReadOnlyList<byte[]> frames, int w, int h, double startCxNorm, double startCyNorm, int fps)
    {
        var pts = new List<TrackPoint>();
        if (frames.Count == 0) return new(pts, startCxNorm, startCyNorm);

        int cx = (int)Math.Clamp(startCxNorm * w, 0, w - 1);
        int cy = (int)Math.Clamp(startCyNorm * h, 0, h - 1);
        pts.Add(new TrackPoint(0, cx / (double)w, cy / (double)h));

        for (var i = 1; i < frames.Count; i++)
        {
            var (dx, dy, _) = BlockMatch(frames[i - 1], frames[i], w, h, cx, cy, BlockRadius, SearchRadius);
            cx = Math.Clamp(cx + dx, 0, w - 1);
            cy = Math.Clamp(cy + dy, 0, h - 1);
            pts.Add(new TrackPoint(i / (double)fps, cx / (double)w, cy / (double)h));
        }
        return new MotionTrack(pts, startCxNorm, startCyNorm);
    }

    /// <summary>이전→현재 프레임 블록 매칭 — 중심(cx,cy) 블록을 탐색창에서 최소 SAD 변위로 정합(dx,dy). 순수 정수 연산.</summary>
    public static (int Dx, int Dy, long Sad) BlockMatch(byte[] prev, byte[] cur, int w, int h, int cx, int cy, int block, int search)
    {
        long best = long.MaxValue; int bdx = 0, bdy = 0;
        for (var dy = -search; dy <= search; dy++)
            for (var dx = -search; dx <= search; dx++)
            {
                long sad = 0;
                for (var by = -block; by <= block; by++)
                    for (var bx = -block; bx <= block; bx++)
                    {
                        var px = Math.Clamp(cx + bx, 0, w - 1);
                        var py = Math.Clamp(cy + by, 0, h - 1);
                        var qx = Math.Clamp(cx + bx + dx, 0, w - 1);
                        var qy = Math.Clamp(cy + by + dy, 0, h - 1);
                        sad += Math.Abs(prev[py * w + px] - cur[qy * w + qx]);
                    }
                // 동점이면 변위가 작은 쪽 선호(지터 억제).
                if (sad < best || (sad == best && dx * dx + dy * dy < bdx * bdx + bdy * bdy))
                { best = sad; bdx = dx; bdy = dy; }
            }
        return (bdx, bdy, best);
    }

    // ══════════════════════ 추종 자막 필터 ══════════════════════

    /// <summary>궤적 → 피사체 추종 <c>drawtext</c> <c>-vf</c>(키프레임 시간 게이트 · 라벨을 점 위에 배치).</summary>
    public static string BuildFollowDrawtext(MotionTrack track, string label, int outW, int outH, string? escFont, int maxKeyframes = 40)
    {
        if (track is null || !track.HasData || string.IsNullOrWhiteSpace(label)) return "";
        var pts = Resample(track.Points, maxKeyframes);
        var fs = Math.Max(20, outH / 26);
        var gap = fs + 10;                 // 라벨을 점 위로 띄우는 간격
        var fontHead = escFont is not null ? $"fontfile={escFont}:" : "";
        var text = EscapeText(label);
        var parts = new List<string>(pts.Count);
        for (var i = 0; i < pts.Count; i++)
        {
            var (p, t0, t1) = pts[i];
            var px = (int)Math.Round(p.XNorm * outW);
            var py = (int)Math.Round(p.YNorm * outH);
            parts.Add($"drawtext={fontHead}text={text}:fontcolor=white:fontsize={fs}:borderw=3:bordercolor=black@0.85:box=1:boxcolor=0x22d3ee@0.35:boxborderw=6:" +
                      $"x={px}-text_w/2:y={Math.Max(0, py - gap)}:enable=between(t\\,{Num(t0)}\\,{Num(t1)})");
        }
        return string.Join(",", parts);
    }

    /// <summary>추종 라벨을 영상에 번인한다. 트랙/라벨/ffmpeg 부재는 false(Graceful).</summary>
    public async Task<bool> BurnAsync(string input, string output, string label, MotionTrack track, int outW, int outH, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input) || track is null || !track.HasData) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;
        try
        {
            var font = FontResolver.FindKoreanFont(bold: true);
            var escFont = font is not null ? FontResolver.EscapeDrawPath(font) : null;
            var vf = BuildFollowDrawtext(track, label, outW, outH, escFont);
            if (vf.Length == 0) return false;

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
        catch (Exception ex) { Program.Log("MotionTrack.Burn", ex); return false; }
    }

    // ══════════════════════ 헬퍼 ══════════════════════

    private static List<(TrackPoint P, double T0, double T1)> Resample(IReadOnlyList<TrackPoint> src, int max)
    {
        var picks = new List<TrackPoint>();
        if (src.Count <= max) picks.AddRange(src);
        else for (var i = 0; i < max; i++) picks.Add(src[Math.Clamp((int)Math.Round(i * (src.Count - 1) / (double)(max - 1)), 0, src.Count - 1)]);

        var outp = new List<(TrackPoint, double, double)>(picks.Count);
        for (var i = 0; i < picks.Count; i++)
        {
            var t0 = i == 0 ? 0.0 : (picks[i].TimeSec + picks[i - 1].TimeSec) / 2.0;
            var t1 = i == picks.Count - 1 ? 1e6 : (picks[i].TimeSec + picks[i + 1].TimeSec) / 2.0;
            outp.Add((picks[i], Math.Max(0, t0), Math.Max(t0 + 0.05, t1)));
        }
        return outp;
    }

    private static string EscapeText(string s) => s.Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "’").Replace("%", "\\%");
    private static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<List<byte[]>> DecodeGrayFramesAsync(string ffmpeg, string video, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-y", "-i", video, "-vf", $"fps={SampleFps},scale={W}:{H}", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1" })
            psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var list = new List<byte[]>();
        if (!proc.StartCompat()) return list;

        using var ms = new MemoryStream();
        var copy = proc.StandardOutput.BaseStream.CopyToAsync(ms, 81920, ct);
        var drain = proc.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(copy, drain).ConfigureAwait(false);
        try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { if (!proc.HasExited) proc.KillCompat(true); } catch { } throw; }

        var bytes = ms.ToArray();
        var frameSize = W * H;
        var count = bytes.Length / frameSize;
        var step = Math.Max(1, count / MaxFrames);
        for (var f = 0; f < count && list.Count < MaxFrames; f += step)
        {
            var buf = new byte[frameSize];
            Buffer.BlockCopy(bytes, f * frameSize, buf, 0, frameSize);
            list.Add(buf);
        }
        return list;
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
