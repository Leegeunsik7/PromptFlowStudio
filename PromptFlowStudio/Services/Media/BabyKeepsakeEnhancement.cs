using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;      // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Media;

/// <summary>기념 카드 스펙 — 카드에 각인할 값(전부 카드 생성 시점에 고정 → 결정론).</summary>
public sealed record KeepsakeSpec(
    string Name, string DDay, string Birth, string ArtKey, string Theme,
    string Resolution, int Cuts, string Issued, int Seed);

/// <summary>기념 카드 발급 결과 — PNG/PDF 경로.</summary>
public sealed record KeepsakeCardResult(bool Ok, string? PngPath, string? PdfPath, string Message);

/// <summary>
/// [PromptFlowStudio] v3.7.0 4K 프리미엄 디지털 성장 기념 카드 발급(Task 5 · 클라우드 자율 발상 · 순수 BCL).
///   ▸ 영상 완성 스펙(이름·D+Day·탄생일·아트 스타일·테마·해상도·컷 수)을 <b>한 장의 소장용 작품 카드</b>로 발급.
///   ▸ 순수 BCL 로 <b>4K 그라데이션 배경 + 24K 골드 이중 프레임 + 내장 5×7 비트맵 폰트</b> 텍스트를 직접 래스터 → PNG.
///   ▸ 동일 RGB 버퍼를 <b>FlateDecode 이미지 XObject</b>로 임베드한 유효 PDF 발급(외부 PDF/이미지 라이브러리 0 · 완전 오프라인).
///   ▸ 한글 이름은 ffmpeg drawtext 로 상단에 골드 각인(있을 때) · ffmpeg/폰트 부재는 Graceful(라틴 스펙 카드 유지).
/// </summary>
public sealed class DigitalKeepsakeCardService
{
    /// <summary>기본 카드 해상도(4K급 세로 카드).</summary>
    public const int CardW = 2160;
    public const int CardH = 2700;

    // ══════════════════════ 스펙 ══════════════════════

    /// <summary>프로젝트 → 기념 카드 스펙(발급 시점 고정).</summary>
    public static KeepsakeSpec BuildSpec(StoryboardProject p)
    {
        var art = string.IsNullOrWhiteSpace(p.BabyArtStyle) ? "MIXED" : p.BabyArtStyle.ToUpperInvariant();
        var theme = string.IsNullOrWhiteSpace(p.PresetKey) ? "BABY" : p.PresetKey!.ToUpperInvariant();
        return new KeepsakeSpec(
            Name: (p.BabyName ?? "").Trim(),
            DDay: BabyKeepsakeOverlayEngine.ComputeDDay(p.BabyBirthDate, DateTime.Now),
            Birth: (p.BabyBirthDate ?? "").Trim(),
            ArtKey: art,
            Theme: theme,
            Resolution: (p.ExportResolution ?? "").ToUpperInvariant().Replace("*", "X"),
            Cuts: p.Scenes.Count,
            Issued: DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Seed: p.BabyArtSeed);
    }

    // ══════════════════════ 4K 카드 래스터(순수 BCL) ══════════════════════

    /// <summary>스펙 → 4K 카드 RGB(w×h×3) 버퍼(그라데이션 + 골드 프레임 + 5×7 폰트 텍스트).</summary>
    public static byte[] BuildCardRgb(KeepsakeSpec spec, int w, int h)
    {
        var rgb = new byte[w * h * 3];

        // ── 배경: 세로 그라데이션(딥 플럼 → 딥 네이비) + 상단 소프트 글로우 ──
        (int R, int G, int B) top = (26, 16, 48), bot = (11, 13, 20);
        (int R, int G, int B) glow = (124, 92, 255);
        for (var y = 0; y < h; y++)
        {
            var ty = h <= 1 ? 0.0 : y / (double)(h - 1);
            var br = (int)(top.R + (bot.R - top.R) * ty);
            var bg = (int)(top.G + (bot.G - top.G) * ty);
            var bb = (int)(top.B + (bot.B - top.B) * ty);
            for (var x = 0; x < w; x++)
            {
                var dx = (x - w / 2.0) / w;
                var dy = (y - h * 0.34) / h;
                var g = Math.Max(0.0, 1.0 - (dx * dx + dy * dy) * 3.4) * 0.22;   // 상단 중앙 글로우
                var i = (y * w + x) * 3;
                rgb[i] = (byte)Math.Clamp(br + glow.R * g, 0, 255);
                rgb[i + 1] = (byte)Math.Clamp(bg + glow.G * g, 0, 255);
                rgb[i + 2] = (byte)Math.Clamp(bb + glow.B * g, 0, 255);
            }
        }

        // ── 24K 골드 이중 프레임 ──
        (int R, int G, int B) dark = (150, 106, 12), midG = (245, 196, 81), lightG = (255, 246, 200);
        var band = Math.Clamp(Math.Min(w, h) / 18, 16, Math.Min(w, h) / 3);
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var d = Math.Min(Math.Min(x, y), Math.Min(w - 1 - x, h - 1 - y));
                var paint = false; (int r, int g, int b) col = midG;
                if (d < band)   // 외곽 골드 밴드(그라데이션)
                {
                    var t = d / (double)band;
                    col = t < 0.5 ? Lerp(dark, lightG, t / 0.5) : Lerp(lightG, midG, (t - 0.5) / 0.5);
                    paint = true;
                }
                else if (d >= band + 20 && d < band + 26)   // 내부 얇은 이중선
                {
                    col = lightG; paint = true;
                }
                if (paint)
                {
                    var i = (y * w + x) * 3;
                    rgb[i] = (byte)col.r; rgb[i + 1] = (byte)col.g; rgb[i + 2] = (byte)col.b;
                }
            }

        // ── 텍스트(내장 5×7 폰트 · 전부 ASCII 안전) ──
        var s = Math.Max(2, w / 360);                        // 기본 스케일 단위
        DrawCentered(rgb, w, h, "PROMPTFLOW STUDIO", (int)(h * 0.14), s * 2, midG);
        DrawCentered(rgb, w, h, "DIGITAL KEEPSAKE CARD", (int)(h * 0.14) + s * 2 * 9, s * 3, lightG);

        DrawCentered(rgb, w, h, string.IsNullOrEmpty(spec.DDay) ? "D-DAY" : spec.DDay, (int)(h * 0.34), s * 12, midG);

        // 이름 자리(한글은 ffmpeg 각인) — ASCII 이름이면 직접 각인, 아니면 골드 밑줄 플레이스홀더.
        var nameY = (int)(h * 0.30) - s * 8;
        if (IsAscii(spec.Name) && spec.Name.Length > 0)
            DrawCentered(rgb, w, h, spec.Name.ToUpperInvariant(), nameY, s * 5, lightG);
        else
            DrawUnderline(rgb, w, h, (int)(w * 0.30), (int)(w * 0.70), nameY + s * 5, Math.Max(2, s), midG);

        // 스펙 블록.
        var by = (int)(h * 0.60); var line = s * 7 + s * 3;
        DrawCentered(rgb, w, h, "BIRTH  " + Safe(spec.Birth), by, s * 3, lightG); by += line;
        DrawCentered(rgb, w, h, "THEME  " + Safe(spec.Theme), by, s * 3, midG); by += line;
        DrawCentered(rgb, w, h, "ART  " + Safe(spec.ArtKey), by, s * 3, midG); by += line;
        DrawCentered(rgb, w, h, "FORMAT  " + Safe(spec.Resolution) + "  " + spec.Cuts + " CUTS", by, s * 3, lightG); by += line;
        DrawCentered(rgb, w, h, "ISSUED  " + Safe(spec.Issued), by, s * 3, lightG);

        DrawCentered(rgb, w, h, "ONE-OF-A-KIND  SEED-" + spec.Seed.ToString(CultureInfo.InvariantCulture), (int)(h * 0.86), s * 2, dark);
        return rgb;
    }

    private static string Safe(string? v) => string.IsNullOrWhiteSpace(v) ? "-" : v!;
    private static bool IsAscii(string? s) { if (string.IsNullOrEmpty(s)) return false; foreach (var c in s) if (c > 126) return false; return true; }

    private static (int, int, int) Lerp((int R, int G, int B) a, (int R, int G, int B) b, double t)
    {
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        return ((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // ══════════════════════ 발급 ══════════════════════

    /// <summary>기념 카드 PNG + PDF 를 발급한다(성공 경로 반환). ffmpeg 있으면 한글 이름을 PNG 상단에 골드 각인.</summary>
    public async Task<KeepsakeCardResult> IssueAsync(StoryboardProject project, string outDir, CancellationToken ct = default)
    {
        try
        {
            if (project is null) return new(false, null, null, "프로젝트 없음");
            Directory.CreateDirectory(outDir);
            var spec = BuildSpec(project);
            var rgb = BuildCardRgb(spec, CardW, CardH);

            var pngPath = Path.Combine(outDir, "keepsake_card.png");
            BabyPngWriter.WriteRgb(pngPath, CardW, CardH, rgb);

            // 한글 이름 골드 각인(선택 · ffmpeg + 폰트 있을 때).
            if (!IsAscii(spec.Name) && spec.Name.Length > 0)
                await TryBurnNameAsync(pngPath, spec.Name, CardW, CardH, ct).ConfigureAwait(false);

            var pdfPath = Path.Combine(outDir, "keepsake_card.pdf");
            File.WriteAllBytes(pdfPath, BuildImagePdf(rgb, CardW, CardH));

            return new(true, pngPath, pdfPath, "4K 기념 카드 PNG/PDF 발급 완료");
        }
        catch (Exception ex) { Program.Log("KeepsakeCard.Issue", ex); return new(false, null, null, ex.Message); }
    }

    private static async Task TryBurnNameAsync(string pngPath, string name, int w, int h, CancellationToken ct)
    {
        try
        {
            var ffmpeg = ResolveFfmpeg();
            var font = FontResolver.FindKoreanFont(bold: true);
            if (ffmpeg is null || font is null) return;
            var fontOpt = $"fontfile='{FontResolver.EscapeDrawPath(font)}':";
            var size = Math.Max(40, w / 12);
            var vf = $"drawtext={fontOpt}text='{BabyKeepsakeOverlayEngine.DrawTextEscape(name)}':fontcolor=0xF5C451:fontsize={size}:" +
                     $"borderw=3:bordercolor=0x2A1B06:shadowx=2:shadowy=2:shadowcolor=0x00000088:x=(w-text_w)/2:y={(int)(h * 0.24)}";
            var tmp = pngPath + ".name.png";
            var args = new List<string> { "-y", "-i", pngPath, "-vf", vf, "-frames:v", "1", tmp };
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            if (res.Ok && File.Exists(tmp)) { File.Copy(tmp, pngPath, overwrite: true); File.Delete(tmp); }
        }
        catch { /* 이름 각인 실패는 무시(라틴 카드 유지) */ }
    }

    // ══════════════════════ 순수 BCL PDF(이미지 임베드) ══════════════════════

    /// <summary>RGB 버퍼를 FlateDecode /DeviceRGB 이미지로 임베드한 단일 페이지 PDF(순수 BCL) 바이트를 만든다.</summary>
    public static byte[] BuildImagePdf(byte[] rgb, int w, int h)
    {
        byte[] img;
        using (var comp = new MemoryStream())
        {
            using (var z = new ZLibStream(comp, CompressionLevel.Fastest, leaveOpen: true)) z.Write(rgb, 0, rgb.Length);
            img = comp.ToArray();
        }
        const int pw = 480;
        var ph = (int)Math.Round(480.0 * h / w);
        var content = Encoding.Latin1.GetBytes($"q {pw} 0 0 {ph} 0 0 cm /Im0 Do Q\n");

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        var off = new long[6];

        W("%PDF-1.4\n%âãÏÓ\n");
        off[1] = ms.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        off[2] = ms.Position; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        off[3] = ms.Position; W($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pw} {ph}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        off[4] = ms.Position;
        W($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {w} /Height {h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {img.Length} >>\nstream\n");
        ms.Write(img, 0, img.Length); W("\nendstream\nendobj\n");
        off[5] = ms.Position;
        W($"5 0 obj\n<< /Length {content.Length} >>\nstream\n"); ms.Write(content, 0, content.Length); W("\nendstream\nendobj\n");

        var xref = ms.Position;
        const int count = 6;
        W($"xref\n0 {count}\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++) W(off[i].ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        W($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return ms.ToArray();
    }

    // ══════════════════════ 내장 5×7 비트맵 폰트 ══════════════════════

    private const int GW = 5, GH = 7;

    /// <summary>글리프 존재 여부(테스트/조회용).</summary>
    public static bool HasGlyph(char c) => Font.ContainsKey(NormalizeChar(c));

    private static char NormalizeChar(char c)
    {
        if (c >= 'a' && c <= 'z') return (char)(c - 32);   // 소문자 → 대문자
        if (c == '·') return '.';                      // 가운뎃점 → 마침표
        if (c == '_') return '-';
        return c;
    }

    private static void DrawCentered(byte[] rgb, int w, int h, string text, int y, int scale, (int R, int G, int B) c)
    {
        var width = TextWidth(text, scale);
        DrawText(rgb, w, h, text, (w - width) / 2, y, scale, c);
    }

    /// <summary>렌더 텍스트 폭(px).</summary>
    public static int TextWidth(string text, int scale)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return text.Length * GW * scale + (text.Length - 1) * scale;
    }

    private static void DrawText(byte[] rgb, int w, int h, string text, int x0, int y0, int scale, (int R, int G, int B) c)
    {
        var cx = x0;
        foreach (var raw in text)
        {
            var ch = NormalizeChar(raw);
            if (Font.TryGetValue(ch, out var rows))
            {
                for (var gy = 0; gy < GH; gy++)
                {
                    var bits = rows[gy];
                    for (var gx = 0; gx < GW; gx++)
                        if ((bits & (1 << (GW - 1 - gx))) != 0)
                            FillBlock(rgb, w, h, cx + gx * scale, y0 + gy * scale, scale, c);
                }
            }
            cx += (GW + 1) * scale;
        }
    }

    private static void DrawUnderline(byte[] rgb, int w, int h, int x0, int x1, int y, int thick, (int R, int G, int B) c)
    {
        for (var y2 = y; y2 < y + thick; y2++)
            for (var x = x0; x < x1; x++)
                Plot(rgb, w, h, x, y2, c);
    }

    private static void FillBlock(byte[] rgb, int w, int h, int x, int y, int scale, (int R, int G, int B) c)
    {
        for (var yy = 0; yy < scale; yy++)
            for (var xx = 0; xx < scale; xx++)
                Plot(rgb, w, h, x + xx, y + yy, c);
    }

    private static void Plot(byte[] rgb, int w, int h, int x, int y, (int R, int G, int B) c)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        var i = (y * w + x) * 3;
        rgb[i] = (byte)c.R; rgb[i + 1] = (byte)c.G; rgb[i + 2] = (byte)c.B;
    }

    // 5비트×7행 글리프(대문자/숫자/기호). 행 문자열 '#'=켜짐.
    private static readonly Dictionary<char, byte[]> Font = BuildFont();

    private static Dictionary<char, byte[]> BuildFont()
    {
        var d = new Dictionary<char, byte[]>();
        void G(char c, params string[] rows)
        {
            var b = new byte[GH];
            for (var y = 0; y < GH; y++)
            {
                byte v = 0;
                var r = rows[y];
                for (var x = 0; x < GW; x++) if (x < r.Length && r[x] == '#') v |= (byte)(1 << (GW - 1 - x));
                b[y] = v;
            }
            d[c] = b;
        }

        G(' ', ".....", ".....", ".....", ".....", ".....", ".....", ".....");
        G('+', ".....", "..#..", "..#..", "#####", "..#..", "..#..", ".....");
        G('-', ".....", ".....", ".....", "#####", ".....", ".....", ".....");
        G('.', ".....", ".....", ".....", ".....", ".....", ".##..", ".##..");
        G(':', ".....", ".##..", ".##..", ".....", ".##..", ".##..", ".....");
        G('/', "....#", "....#", "...#.", "..#..", ".#...", "#....", "#....");
        G('0', ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###.");
        G('1', "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###.");
        G('2', ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####");
        G('3', "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###.");
        G('4', "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#.");
        G('5', "#####", "#....", "####.", "....#", "....#", "#...#", ".###.");
        G('6', ".###.", "#....", "#....", "####.", "#...#", "#...#", ".###.");
        G('7', "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#...");
        G('8', ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###.");
        G('9', ".###.", "#...#", "#...#", ".####", "....#", "....#", ".###.");
        G('A', ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
        G('B', "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####.");
        G('C', ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###.");
        G('D', "###..", "#..#.", "#...#", "#...#", "#...#", "#..#.", "###..");
        G('E', "#####", "#....", "#....", "####.", "#....", "#....", "#####");
        G('F', "#####", "#....", "#....", "####.", "#....", "#....", "#....");
        G('G', ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".###.");
        G('H', "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
        G('I', ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###.");
        G('J', "..###", "...#.", "...#.", "...#.", "#..#.", "#..#.", ".##..");
        G('K', "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#");
        G('L', "#....", "#....", "#....", "#....", "#....", "#....", "#####");
        G('M', "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#");
        G('N', "#...#", "##..#", "#.#.#", "#.#.#", "#..##", "#...#", "#...#");
        G('O', ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
        G('P', "####.", "#...#", "#...#", "####.", "#....", "#....", "#....");
        G('Q', ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#");
        G('R', "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#");
        G('S', ".####", "#....", "#....", ".###.", "....#", "....#", "####.");
        G('T', "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#..");
        G('U', "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
        G('V', "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#..");
        G('W', "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#");
        G('X', "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#");
        G('Y', "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#..");
        G('Z', "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####");
        return d;
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
