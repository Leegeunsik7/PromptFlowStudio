using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;      // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Trade;

/// <summary>
/// [PromptFlowStudio] 바이어 엠블럼 음각 각인 서비스(v2.6.0 자율 발상 ①) — FFmpeg convolution 음각 커널(외부 라이브러리 0).
///   ▸ 글로벌 바이어의 사명 로고/엠블럼을 최종 홍보본 <b>좌측 하단</b>에 <b>음각(engrave)</b> 질감으로 각인해 "이 제안은 귀사 전용"이라는 프리미엄 인상을 준다.
///   ▸ 3D 양각(<see cref="Services.Media.EmbossWatermarkPipeline"/>)의 반대 방향 커널 → 표면에 새겨진 듯한 음각. 우측 상단 자사 워터마크와 시각적으로 구분.
/// </summary>
public sealed class BuyerEmblemEngraveService
{
    /// <summary>음각 컨볼루션 커널(양각의 부호 반전 · 3×3).</summary>
    public const string EngraveKernel = "2 1 0 1 1 -1 0 -1 -2";

    /// <summary>
    /// 바이어 엠블럼(입력 <paramref name="logoInputIdx"/>)을 좌측 하단에 음각 각인하는 filter_complex 조각을 만든다.
    ///   엠블럼 스케일(출력폭/8) → 음각 컨볼루션 → 미세 블러 → 좌측 하단(24,24) 오버레이.
    /// </summary>
    public static string BuildEngraveOverlay(int logoInputIdx, int outWidth, string baseLabel, string outLabel)
    {
        var logoW = Math.Max(64, outWidth / 8);
        return
            $"[{logoInputIdx}:v]scale={logoW}:-1,format=rgba,convolution='{EngraveKernel}':'{EngraveKernel}':'{EngraveKernel}':'0 0 0 0 1 0 0 0 0',gblur=sigma=0.4[emblem];" +
            $"[{baseLabel}][emblem]overlay=24:H-h-24:format=auto[{outLabel}]";
    }

    /// <summary>바이어 엠블럼을 영상 좌측 하단에 음각 각인 합성한다. 엠블럼/ffmpeg 부재·실패는 false(원본 유지 · Graceful).</summary>
    public async Task<bool> ApplyAsync(string video, string emblemPath, string outPath, int outWidth = 1080, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(video) || !File.Exists(video)) return false;
        if (string.IsNullOrWhiteSpace(emblemPath) || !File.Exists(emblemPath)) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;
        try
        {
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var chain = BuildEngraveOverlay(1, outWidth, "0:v", "vout");
            var args = new List<string>
            {
                "-y", "-i", video, "-i", emblemPath, "-filter_complex", chain, "-map", "[vout]", "-map", "0:a?",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "copy", "-shortest", "-movflags", "+faststart", outPath,
            };
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(outPath);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("BuyerEmblem.Apply", ex); return false; }
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

/// <summary>
/// [PromptFlowStudio] B2B 수주 제안서용 PDF 스펙 시트 내보내기(v2.6.0 자율 발상 ②) — <b>순수 C# BCL PDF 라이터</b>(외부 PDF 라이브러리 0).
///   ▸ 프로젝트의 장비/포맷/해상도/컷 수/더빙 언어/바이어 정보를 요약한 <b>단일 페이지 A-레터 PDF</b>를 직접 바이트로 생성한다.
///   ▸ PDF 1.4 최소 구조(Catalog/Pages/Page/Font/Contents + xref/trailer) · Helvetica 텍스트 · 글로벌 바이어용 <b>영문 스펙</b>.
///   ▸ 비ASCII(한글)는 안전 치환 → 어떤 PDF 뷰어에서도 열리는 유효 파일 보장.
/// </summary>
public sealed class SpecSheetPdfExporter
{
    /// <summary>프로젝트로부터 스펙 시트 PDF를 파일로 내보낸다(성공 시 경로 반환 · 실패 null).</summary>
    public string? Export(StoryboardProject project, string outPath)
    {
        try
        {
            var rows = BuildRows(project);
            var bytes = BuildPdf("PromptFlow Studio", "B2B EQUIPMENT PROMO — SPEC SHEET", rows);
            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(outPath, bytes);
            return File.Exists(outPath) ? outPath : null;
        }
        catch (Exception ex) { Program.Log("SpecSheetPdf.Export", ex); return null; }
    }

    /// <summary>프로젝트 → 스펙 행(라벨, 값). 값은 ASCII 안전화.</summary>
    public static IReadOnlyList<(string Label, string Value)> BuildRows(StoryboardProject p)
    {
        var langs = p.SelectedDubbingLanguages.Count > 0
            ? string.Join(", ", p.SelectedDubbingLanguages.Select(c => c.ToUpperInvariant()))
            : "KO (source only)";
        return new List<(string, string)>
        {
            ("Project ID", p.Id),
            ("Preset / Equipment", string.IsNullOrWhiteSpace(p.PresetKey) ? "-" : p.PresetKey),
            ("Cuts", p.Scenes.Count.ToString(CultureInfo.InvariantCulture)),
            ("Resolution", p.ExportResolution),
            ("Telemetry HUD", string.IsNullOrWhiteSpace(p.TelemetryHudStyle) ? "off" : p.TelemetryHudStyle),
            ("3D Wireframe", string.IsNullOrWhiteSpace(p.CadModelPath) ? "off" : $"on ({p.WireframeNeonColor})"),
            ("Global Dubbing", langs),
            ("Quality Guard", p.AutoQualityGuard ? $"on (SSIM>={p.SsimQualityThreshold.ToString("0.00", CultureInfo.InvariantCulture)})" : "off"),
            ("Buyer", string.IsNullOrWhiteSpace(p.BuyerName) ? "-" : p.BuyerName),
            ("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
        };
    }

    /// <summary>제목/부제/행으로 유효한 PDF 1.4 바이트를 만든다(순수 BCL).</summary>
    public static byte[] BuildPdf(string title, string subtitle, IReadOnlyList<(string Label, string Value)> rows)
    {
        // ── 콘텐츠 스트림 ──
        var c = new StringBuilder();
        c.Append("BT /F2 24 Tf 60 730 Td (").Append(Esc(title)).Append(") Tj ET\n");
        c.Append("BT /F2 13 Tf 60 706 Td (").Append(Esc(subtitle)).Append(") Tj ET\n");
        c.Append("0.13 0.83 0.93 rg 60 698 492 2 re f\n");                       // 사이언 룰
        c.Append("BT /F1 12 Tf 60 668 Td 20 TL\n");
        foreach (var (label, value) in rows)
            c.Append('(').Append(Esc(label + ":  " + value)).Append(") Tj T*\n");
        c.Append("ET\n");
        c.Append("0.42 0.45 0.57 rg BT /F1 9 Tf 60 70 Td (Generated by PromptFlow Studio - offline single-EXE AI media studio.) Tj ET\n");
        var content = Encoding.Latin1.GetBytes(c.ToString());

        // ── 오브젝트 조립(정확한 바이트 오프셋 xref) ──
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold >>",
        };

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        var offsets = new List<long>();

        W("%PDF-1.4\n%âãÏÓ\n");
        for (var i = 0; i < objs.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        // 6: 콘텐츠 스트림
        offsets.Add(ms.Position);
        W($"6 0 obj\n<< /Length {content.Length} >>\nstream\n");
        ms.Write(content);
        W("\nendstream\nendobj\n");

        // xref
        var xrefPos = ms.Position;
        var count = objs.Count + 2;   // 0 free + 6 objects
        W($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (var off in offsets) W(off.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        W($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xrefPos}\n%%EOF");
        return ms.ToArray();
    }

    // PDF 리터럴 이스케이프 + 비ASCII 안전 치환(한글 → '?').
    private static string Esc(string? s)
    {
        var sb = new StringBuilder();
        foreach (var ch in (s ?? ""))
        {
            if (ch == '(' || ch == ')' || ch == '\\') { sb.Append('\\').Append(ch); }
            else if (ch >= 32 && ch < 127) sb.Append(ch);
            else if (ch == '/' || ch == '-' || ch == '.') sb.Append(ch);
            else sb.Append(ch > 127 ? '?' : ' ');
        }
        return sb.ToString();
    }
}
