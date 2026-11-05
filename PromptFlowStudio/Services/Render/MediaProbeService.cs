using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner

namespace PromptFlowStudio.Services.Render;

/// <summary>실측 미디어 기술 규격 — 코덱·해상도·FPS·비트레이트·크기·오디오 트랙 유무.</summary>
public sealed record MediaSpec(
    string Container, string VideoCodec, string CodecTag, int Width, int Height, double Fps,
    long BitrateBps, long FileSizeBytes, double DurationSec, bool HasAudio, string Source)
{
    /// <summary><c>H.264 (avc1)</c> · <c>VP9</c> · <c>H.265 (hvc1)</c> · 미상은 대문자 코덱명.</summary>
    public string CodecLabel => MediaProbeService.DescribeCodec(VideoCodec, CodecTag);

    /// <summary><c>1920x1080 @ 30fps</c>(FPS 미상이면 해상도만).</summary>
    public string ResolutionLabel
    {
        get
        {
            if (Width <= 0 || Height <= 0) return "—";
            var res = Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture);
            if (Fps <= 0) return res;
            var r = Math.Round(Fps);
            var fps = Math.Abs(Fps - r) < 0.01 ? r.ToString("0", CultureInfo.InvariantCulture) : Fps.ToString("0.##", CultureInfo.InvariantCulture);
            return res + " @ " + fps + "fps";
        }
    }

    /// <summary><c>2.8 Mbps</c> · <c>850 kbps</c>.</summary>
    public string BitrateLabel => BitrateBps <= 0 ? "—"
        : BitrateBps >= 1_000_000 ? (BitrateBps / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + " Mbps"
        : (BitrateBps / 1000d).ToString("0", CultureInfo.InvariantCulture) + " kbps";

    /// <summary><c>5.4 MB</c>(1 MiB 단위).</summary>
    public string SizeLabel => MediaProbeService.FormatBytes(FileSizeBytes);

    /// <summary>무음(-an) 적용 여부 라벨.</summary>
    public string AudioLabel => HasAudio ? "Audio Present 🔊" : "Muted 🔇 (HTS 최적화)";
}

/// <summary>
/// [PromptFlowStudio] 미디어 테크니컬 규격 인스펙터 — 순수 BCL(System.Text.Json · 외부 NuGet 0).
///   ▸ 1순위 ffprobe(<c>-of json</c>) → 2순위 ffmpeg <c>-i</c> 배너(stderr) 경량 헤더 분석 → 3순위 파일 크기·확장자만(Graceful).
///   ▸ 중앙 차트 영역 평균 밝기(<see cref="MeasureCenterLumaAsync"/>) — ffmpeg <c>crop+signalstats</c> YAVG 샘플 평균(0~1).
///   ▸ 외부 프로세스는 타임아웃(10s) · 취소 시 트리 kill(<see cref="ProcessRunner"/>) · 모든 실패는 null/폴백(예외 전파 없음).
/// </summary>
public sealed class MediaProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private readonly Func<string?> _ffmpeg;

    public MediaProbeService() : this(null) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(설정 경로 우선) — null 이면 <see cref="FfmpegLocator"/>.</param>
    public MediaProbeService(Func<string?>? ffmpegPath) => _ffmpeg = ffmpegPath ?? (() => FfmpegLocator.Locate().Path);

    /// <summary>대상 비디오 규격 추출 — 파일 부재 null · 도구 부재 시 크기/확장자만 담은 폴백 규격.</summary>
    public async Task<MediaSpec?> ProbeAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        long size;
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return null;
            size = fi.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        var ffmpeg = SafeFfmpeg();
        try
        {
            var r = await ProcessRunner.RunAsync(ResolveProbe(ffmpeg),
                ["-v", "error", "-show_entries",
                 "format=format_name,duration,bit_rate:stream=codec_type,codec_name,codec_tag_string,width,height,avg_frame_rate,r_frame_rate",
                 "-of", "json", path], ct: cts.Token).ConfigureAwait(false);
            if (r.Ok && ParseFfprobeJson(r.StdOut, size) is { } spec) return spec;

            if (!string.IsNullOrEmpty(ffmpeg))
            {
                var b = await ProcessRunner.RunAsync(ffmpeg, ["-hide_banner", "-i", path], ct: cts.Token).ConfigureAwait(false);
                if (ParseFfmpegBanner(b.StdErr, size) is { } bs) return bs;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("MediaProbe.Probe", ex); }
        return Fallback(path, size);
    }

    /// <summary>중앙 50% 영역 평균 휘도(0~1, 8bit YAVG/255) — ffmpeg 부재·실패 시 null.</summary>
    public async Task<double?> MeasureCenterLumaAsync(string? path, CancellationToken ct = default)
    {
        var ffmpeg = SafeFfmpeg();
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(ffmpeg) || !File.Exists(path)) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ProbeTimeout);
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg,
                ["-hide_banner", "-i", path, "-vf", "fps=2,crop=iw/2:ih/2,scale=96:-2,signalstats,metadata=print:key=lavfi.signalstats.YAVG",
                 "-frames:v", "20", "-an", "-f", "null", "-"], ct: cts.Token).ConfigureAwait(false);
            return ParseSignalStatsYavg(r.StdErr + "\n" + r.StdOut);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("MediaProbe.Luma", ex); return null; }
    }

    private string? SafeFfmpeg()
    {
        try { return _ffmpeg(); }
        catch (Exception ex) { Program.Log("MediaProbe.Locate", ex); return null; }
    }

    /// <summary>ffmpeg 옆 ffprobe 우선, 없으면 PATH 의 ffprobe.</summary>
    public static string ResolveProbe(string? ffmpeg)
    {
        var name = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        try
        {
            var dir = string.IsNullOrEmpty(ffmpeg) ? null : Path.GetDirectoryName(ffmpeg);
            if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, name))) return Path.Combine(dir, name);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        return name;
    }

    /// <summary>ffprobe <c>-of json</c> 파싱 — 첫 video 스트림 필수(없으면 null) · bit_rate 부재 시 크기/길이로 산출.</summary>
    public static MediaSpec? ParseFfprobeJson(string? json, long fileSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string codec = "", tag = "";
            int w = 0, h = 0;
            double fps = 0;
            var audio = false;
            var video = false;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type");
                    if (type == "audio") { audio = true; continue; }
                    if (type != "video" || video) continue;
                    video = true;
                    codec = Str(s, "codec_name");
                    tag = Str(s, "codec_tag_string");
                    w = Int(s, "width");
                    h = Int(s, "height");
                    fps = ParseRate(Str(s, "avg_frame_rate"));
                    if (fps <= 0) fps = ParseRate(Str(s, "r_frame_rate"));
                }
            if (!video) return null;
            string container = "";
            double dur = 0;
            long br = 0;
            if (root.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.Object)
            {
                container = Str(fmt, "format_name");
                double.TryParse(Str(fmt, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out dur);
                long.TryParse(Str(fmt, "bit_rate"), NumberStyles.Integer, CultureInfo.InvariantCulture, out br);
            }
            if (br <= 0 && dur > 0 && fileSizeBytes > 0) br = (long)(fileSizeBytes * 8 / dur);
            return new MediaSpec(container, codec, tag, w, h, fps, br, fileSizeBytes, dur, audio, "ffprobe");
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// ffmpeg <c>-i</c> 배너(stderr) 경량 파싱 —
    /// <c>Duration: 00:00:05.04, start: 0.000000, bitrate: 2850 kb/s</c> · <c>Stream #0:0: Video: h264 (High) (avc1 / 0x31637661), yuv420p, 1920x1080, ..., 30 fps</c> · <c>Audio:</c>.
    /// </summary>
    public static MediaSpec? ParseFfmpegBanner(string? stderr, long fileSizeBytes)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        string container = "", codec = "", tag = "";
        int w = 0, h = 0;
        double fps = 0, dur = 0;
        long br = 0;
        bool audio = false, video = false;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Input #", StringComparison.Ordinal))
            {
                var a = line.IndexOf(", ", StringComparison.Ordinal);
                var b = line.LastIndexOf(", from", StringComparison.Ordinal);
                if (a > 0 && b > a) container = line.Substring(a + 2, b - a - 2);
            }
            else if (line.StartsWith("Duration:", StringComparison.Ordinal))
            {
                var d = line.Substring(9).Split(',')[0].Trim();
                if (TimeSpan.TryParse(d, CultureInfo.InvariantCulture, out var ts)) dur = ts.TotalSeconds;
                var bi = line.IndexOf("bitrate:", StringComparison.Ordinal);
                if (bi >= 0)
                {
                    var t = line.Substring(bi + 8).Trim().Split(' ');
                    if (t.Length >= 2 && long.TryParse(t[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
                        br = t[1].StartsWith("Mb", StringComparison.Ordinal) ? kb * 1_000_000 : kb * 1000;
                }
            }
            else if (line.StartsWith("Stream #", StringComparison.Ordinal))
            {
                if (line.Contains(": Audio:", StringComparison.Ordinal)) { audio = true; continue; }
                var vi = line.IndexOf(": Video:", StringComparison.Ordinal);
                if (vi < 0 || video) continue;
                video = true;
                var parts = line.Substring(vi + 8).Split(',');
                var head = parts[0].Trim();
                var sp = head.IndexOf(' ');
                codec = sp > 0 ? head.Substring(0, sp) : head;
                var tg = head.LastIndexOf('(');
                if (tg >= 0)
                {
                    var slash = head.IndexOf(" / ", tg, StringComparison.Ordinal);
                    if (slash > tg) tag = head.Substring(tg + 1, slash - tg - 1).Trim();
                }
                for (var i = 1; i < parts.Length; i++)
                {
                    var p = parts[i].Trim();
                    var pe = p.IndexOf(' ');
                    var tok = pe > 0 ? p.Substring(0, pe) : p;
                    var x = tok.IndexOf('x');
                    if (w == 0 && x > 0
                        && int.TryParse(tok.AsSpan(0, x), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pw)
                        && int.TryParse(tok.AsSpan(x + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ph)
                        && pw > 0 && ph > 0) { w = pw; h = ph; }
                    else if (p.EndsWith(" fps", StringComparison.Ordinal)
                        && double.TryParse(p.AsSpan(0, p.Length - 4), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) fps = f;
                }
            }
        }
        if (!video) return null;
        if (br <= 0 && dur > 0 && fileSizeBytes > 0) br = (long)(fileSizeBytes * 8 / dur);
        return new MediaSpec(container, codec, tag, w, h, fps, br, fileSizeBytes, dur, audio, "ffmpeg");
    }

    /// <summary><c>lavfi.signalstats.YAVG=123.4</c> 라인 평균 → 0~1(없으면 null).</summary>
    public static double? ParseSignalStatsYavg(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        const string Key = "lavfi.signalstats.YAVG=";
        double sum = 0;
        var n = 0;
        var idx = 0;
        while ((idx = text.IndexOf(Key, idx, StringComparison.Ordinal)) >= 0)
        {
            idx += Key.Length;
            var end = idx;
            while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
            if (double.TryParse(text.AsSpan(idx, end - idx), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { sum += v; n++; }
            idx = end;
        }
        return n == 0 ? null : Math.Clamp(sum / n / 255d, 0d, 1d);
    }

    /// <summary>코덱 표시명 — h264→<c>H.264 (avc1)</c>, vp9→<c>VP9</c>, hevc→<c>H.265 (hvc1)</c>.</summary>
    public static string DescribeCodec(string? codec, string? tag)
    {
        var c = (codec ?? "").Trim().ToLowerInvariant();
        var t = CleanTag(tag);
        var name = c switch
        {
            "h264" or "avc" or "avc1" => "H.264",
            "hevc" or "h265" => "H.265",
            "vp9" => "VP9",
            "vp8" => "VP8",
            "av1" => "AV1",
            "" => "—",
            _ => c.ToUpperInvariant(),
        };
        return t.Length > 0 && name is "H.264" or "H.265" ? name + " (" + t + ")" : name;
    }

    private static string CleanTag(string? tag)
    {
        var t = (tag ?? "").Trim();
        if (t.Length == 0 || t.Length > 8 || t.StartsWith('[') || t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return "";
        foreach (var ch in t) if (!char.IsAsciiLetterOrDigit(ch)) return "";
        return t;
    }

    /// <summary>바이트 → <c>5.4 MB</c> · <c>820 KB</c> · <c>1.2 GB</c>(1024 단위).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
        if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1L << 10) return (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }

    private static MediaSpec Fallback(string path, long size)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var codec = ext == "webm" ? "vp9" : "";
        return new MediaSpec(ext, codec, "", 0, 0, 0, 0, size, 0, false, "header");
    }

    private static double ParseRate(string r)
    {
        if (r.Length == 0) return 0;
        var s = r.IndexOf('/');
        if (s < 0) return double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return double.TryParse(r.AsSpan(0, s), NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(r.AsSpan(s + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? n / d : 0;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}
