using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;      // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Media;

/// <summary>
/// [PromptFlowStudio] 순수 BCL PNG 라이터(RGB color type 2 / RGBA color type 6) — System.IO.Compression zlib + CRC32(외부 이미지 라이브러리 0).
///   v3.7.0 골드 아우라 오버레이 &amp; 디지털 기념 카드가 공유한다.
/// </summary>
public static class BabyPngWriter
{
    /// <summary>RGB(w×h×3) → PNG(color type 2).</summary>
    public static void WriteRgb(string path, int w, int h, byte[] rgb) => Write(path, w, h, rgb, alpha: false);

    /// <summary>RGBA(w×h×4) → PNG(color type 6).</summary>
    public static void WriteRgba(string path, int w, int h, byte[] rgba) => Write(path, w, h, rgba, alpha: true);

    /// <summary>RGBA(w×h×4) → PNG 스트림(v17.34 헤드리스 시각 QA 미리보기 · 메모리 인코딩).</summary>
    public static void WriteRgba(Stream s, int w, int h, byte[] rgba) => Write(s, w, h, rgba, alpha: true);

    private static void Write(string path, int w, int h, byte[] px, bool alpha)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Write(fs, w, h, px, alpha);
    }

    private static void Write(Stream fs, int w, int h, byte[] px, bool alpha)
    {
        Span<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        fs.Write(sig);

        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)w); WriteBE(ihdr, 4, (uint)h);
        ihdr[8] = 8;                       // bit depth
        ihdr[9] = (byte)(alpha ? 6 : 2);   // color type: 6 = RGBA, 2 = RGB
        WriteChunk(fs, "IHDR", ihdr);

        var channels = alpha ? 4 : 3;
        var stride = w * channels;
        byte[] idat;
        using (var raw = new MemoryStream())
        {
            var scan = new byte[1 + stride];
            for (var y = 0; y < h; y++)
            {
                scan[0] = 0;   // filter None
                Buffer.BlockCopy(px, y * stride, scan, 1, stride);
                raw.Write(scan, 0, scan.Length);
            }
            raw.Position = 0;
            using var comp = new MemoryStream();
            using (var z = new ZLibStream(comp, CompressionLevel.Fastest, leaveOpen: true)) raw.CopyTo(z);
            idat = comp.ToArray();
        }
        WriteChunk(fs, "IDAT", idat);
        WriteChunk(fs, "IEND", Array.Empty<byte>());
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var t = new[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        s.Write(t, 0, 4);
        if (data.Length > 0) s.Write(data, 0, data.Length);
        Span<byte> c = stackalloc byte[4];
        WriteBE(c, 0, Crc32(t, data));
        s.Write(c);
    }

    private static void WriteBE(Span<byte> b, int o, uint v)
    { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static readonly uint[] CrcTable = BuildCrcTable();
    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
    private static uint Crc32(byte[] type, byte[] data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}

/// <summary>
/// [PromptFlowStudio] v3.7.0 아기 성장 각인 오버레이 엔진(Task 3 · 순수 BCL PNG + FFmpeg filter_complex).
///   ▸ 아기 이름·탄생일·<b>D+Day</b>(예: D+100/D+365)와 <b>24K 골드 아우라 테두리</b>를 최종 컷에 합성해 평생 소장 명품 감성을 완성.
///   ▸ 골드 아우라는 순수 BCL 로 <b>알파 그라데이션 RGBA PNG</b>(투명 중앙 + 소프트 골드 이중 프레임)를 직접 생성 → overlay 합성.
///   ▸ 이름/D+Day/날짜는 FFmpeg <c>drawtext</c>(한글 폰트 = <see cref="FontResolver"/>)로 하단 중앙에 골드 캘리그래피 번인.
///   ▸ ffmpeg/폰트 부재는 Graceful(원본 유지). 이름·탄생일 모두 비면 비활성.
/// </summary>
public sealed class BabyKeepsakeOverlayEngine
{
    private const string GoldHex = "0xF5C451";

    /// <summary>탄생일("yyyy-MM-dd") + 기준일 → "D+n"(탄생일=D+1 · 한국 백일=D+100 관례). 미래면 "D-n", 무효면 "".</summary>
    public static string ComputeDDay(string? birthDate, DateTime now)
    {
        if (!DateTime.TryParse(birthDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var birth)) return "";
        var days = (now.Date - birth.Date).Days;
        if (days >= 0) return "D+" + (days + 1).ToString(CultureInfo.InvariantCulture);   // 탄생일 당일 = D+1
        return "D-" + (-days).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>이름 또는 탄생일이 있고 골드 각인이 켜졌는지.</summary>
    public static bool IsEnabled(StoryboardProject p)
        => p is not null && p.BabyGoldKeepsake && (!string.IsNullOrWhiteSpace(p.BabyName) || !string.IsNullOrWhiteSpace(p.BabyBirthDate));

    /// <summary>drawtext 텍스트 이스케이프(백슬래시/콜론/작은따옴표/퍼센트).</summary>
    public static string DrawTextEscape(string? s) => (s ?? "")
        .Replace("\\", "\\\\").Replace(":", "\\:").Replace("'", "\\'").Replace("%", "\\%");

    // ══════════════════════ 24K 골드 아우라 RGBA(순수 BCL) ══════════════════════

    /// <summary>
    /// 투명 중앙 + 24K 골드 이중 프레임 아우라(RGBA w×h×4)를 생성한다.
    ///   테두리 밴드에서 다크→라이트 골드 그라데이션 + 내부 하이라이트 이중선 + 안팎 소프트 페더(알파).
    /// </summary>
    public static byte[] BuildGoldAuraRgba(int w, int h, int thickness)
    {
        var buf = new byte[w * h * 4];   // 기본 0 = 완전 투명
        var band = Math.Clamp(thickness, 8, Math.Max(8, Math.Min(w, h) / 3));

        // 골드 램프 스톱.
        (int R, int G, int B) dark = (150, 106, 12), mid = (245, 196, 81), light = (255, 246, 200);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y));   // 가장 가까운 가장자리 거리
                if (d >= band) continue;                                            // 중앙은 투명
                var t = d / (double)band;                                           // 0=바깥 가장자리 → 1=안쪽

                // 색: 바깥 다크골드 → 중앙 라이트 → 안쪽 미드골드.
                (int r, int g, int b) c = t < 0.5
                    ? Lerp(dark, light, t / 0.5)
                    : Lerp(light, mid, (t - 0.5) / 0.5);

                // 알파: 안팎 소프트 페더, 그 외 불투명.
                double a = 255;
                if (t < 0.10) a = 255 * (t / 0.10);
                else if (t > 0.88) a = 255 * (1 - (t - 0.88) / 0.12);

                // 내부 하이라이트 이중선(얇은 밝은 링).
                if (Math.Abs(t - 0.72) < 0.045) { c = light; a = 255; }

                var i = (y * w + x) * 4;
                buf[i] = (byte)c.r; buf[i + 1] = (byte)c.g; buf[i + 2] = (byte)c.b;
                buf[i + 3] = (byte)Math.Clamp(a, 0, 255);
            }
        }
        return buf;
    }

    private static (int, int, int) Lerp((int R, int G, int B) a, (int R, int G, int B) b, double t)
    {
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        return ((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // ══════════════════════ filter_complex 조립 ══════════════════════

    /// <summary>
    /// 골드 아우라(입력 <paramref name="auraIdx"/>) overlay + 이름/D+Day/날짜 drawtext filter_complex 를 만든다.
    ///   폰트 경로 없으면 drawtext 기본 폰트(라틴). 이름/날짜 없으면 해당 줄 생략.
    /// </summary>
    public static string BuildOverlayFilter(StoryboardProject p, int w, int h, int auraIdx, string? fontPath)
    {
        var font = string.IsNullOrWhiteSpace(fontPath) ? "" : $"fontfile='{FontResolver.EscapeDrawPath(fontPath!)}':";
        var chain = new List<string> { $"[0:v][{auraIdx}:v]overlay=0:0:format=auto[bg]" };

        var draws = new List<string>();
        var name = (p.BabyName ?? "").Trim();
        var dday = ComputeDDay(p.BabyBirthDate, DateTime.Now);
        var birth = (p.BabyBirthDate ?? "").Trim();

        var nameSize = Math.Max(28, w / 16);
        var ddaySize = Math.Max(20, w / 24);
        var dateSize = Math.Max(14, w / 44);

        if (name.Length > 0)
            draws.Add($"drawtext={font}text='{DrawTextEscape(name)}':fontcolor={GoldHex}:fontsize={nameSize}:" +
                      $"borderw=2:bordercolor=0x2A1B06:shadowx=2:shadowy=2:shadowcolor=0x00000088:x=(w-text_w)/2:y=h-{(int)(h * 0.22)}");
        if (dday.Length > 0)
            draws.Add($"drawtext={font}text='{DrawTextEscape(dday)}':fontcolor=0xFFF6C8:fontsize={ddaySize}:" +
                      $"borderw=2:bordercolor=0x2A1B06:x=(w-text_w)/2:y=h-{(int)(h * 0.15)}");
        if (birth.Length > 0)
            draws.Add($"drawtext={font}text='{DrawTextEscape(birth)}':fontcolor=0xEED9A8:fontsize={dateSize}:" +
                      $"x=(w-text_w)/2:y=h-{(int)(h * 0.10)}");

        // drawtext 체인을 [bg] 에 순차 적용.
        var last = "bg";
        if (draws.Count == 0) return $"{chain[0]};[bg]null[vout]";
        for (var i = 0; i < draws.Count; i++)
        {
            var outLbl = i == draws.Count - 1 ? "vout" : $"t{i}";
            chain.Add($"[{last}]{draws[i]}[{outLbl}]");
            last = outLbl;
        }
        return string.Join(";", chain);
    }

    // ══════════════════════ 오케스트레이션 ══════════════════════

    /// <summary>골드 아우라 + 이름/D+Day 각인을 최종 영상에 합성한다. 비활성/부재/실패는 false(원본 유지 · Graceful).</summary>
    public async Task<bool> ApplyAsync(string video, StoryboardProject project, string outPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(video) || !File.Exists(video) || !IsEnabled(project)) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;

        string? auraPath = null;
        try
        {
            var (w, h) = StoryboardProject.ParseResolution(project.ExportResolution);
            var thickness = Math.Max(10, Math.Min(w, h) / 22);
            var aura = BuildGoldAuraRgba(w, h, thickness);
            auraPath = Path.Combine(Path.GetTempPath(), "promptflow-aura", Guid.NewGuid().ToString("N")[..8] + ".png");
            BabyPngWriter.WriteRgba(auraPath, w, h, aura);

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var font = FontResolver.FindKoreanFont(bold: true);
            var chain = BuildOverlayFilter(project, w, h, 1, font);
            var args = new List<string>
            {
                "-y", "-i", video, "-i", auraPath, "-filter_complex", chain, "-map", "[vout]", "-map", "0:a?",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "copy", "-shortest", "-movflags", "+faststart", outPath,
            };
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(outPath);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("BabyKeepsakeOverlay.Apply", ex); return false; }
        finally { try { if (auraPath is not null && File.Exists(auraPath)) File.Delete(auraPath); } catch { } }
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
