using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Cad;

/// <summary>와이어프레임 렌더 결과 — 프레임 디렉터리·패턴·프레임 수·fps.</summary>
public sealed record WireframeRender(bool Ok, string Dir, string Pattern, int Frames, int Fps, string Message);

/// <summary>
/// [PromptFlowStudio] CAD 3D 홀로그램 와이어프레임 렌더러 &amp; 오버레이 엔진(v2.6.0) — 순수 C# BCL 소프트웨어 렌더 + FFmpeg CLI(외부 3D/이미지 라이브러리 0).
///   ▸ <see cref="StlMeshParser"/>가 디코드한 메시를 <b>회전 투영 행렬</b>(Y축 회전 + X 틸트 + 원근 분할)으로 프레임마다 2D 투시도로 변환.
///   ▸ 엣지를 <b>Bresenham 라인</b>으로 네온 컬러 래스터 → 순수 BCL <b>PNG 인코더</b>(IHDR/IDAT zlib/CRC32)로 <c>wire_%03d.png</c> 시퀀스 출력.
///   ▸ FFmpeg <c>colorkey</c>(검정 배경 제거) + <c>gblur</c> 글로우 → 비디오 위에 <b>빛나는 3D 홀로그램 와이어프레임 회전 샷</b> 합성.
///   ▸ 네온 컬러 3종(시안/마젠타/에메랄드)은 래스터 단계에서 베이크 → 필터는 키잉+글로우만. ffmpeg/메시 부재는 Graceful.
/// </summary>
public sealed class CadWireframeOverlayEngine
{
    private const int DefaultFrames = 40;
    private const int DefaultSize = 460;
    private const int DefaultFps = 20;

    /// <summary>네온 프리셋 → RGB(래스터 베이크용).</summary>
    public static (byte R, byte G, byte B) NeonRgb(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "magenta" => (217, 70, 239),   // #d946ef
        "emerald" => (16, 185, 129),   // #10b981
        "gold" => (245, 196, 81),      // #f5c451 (v3.2.0 피치 모드 골드)
        _ => (34, 211, 238),           // #22d3ee 시안(기본)
    };

    // ══════════════════════ 오케스트레이션 ══════════════════════

    /// <summary>
    /// STL 메시를 회전 홀로그램 와이어프레임으로 렌더해 비디오 위에 합성한다(colorkey+글로우 오버레이).
    ///   메시/ffmpeg 부재 또는 실패는 false(호출부가 원본 유지 · Graceful).
    /// </summary>
    public async Task<bool> AddWireframeAsync(string videoPath, string stlPath, string outPath, string? neon,
        int frames = DefaultFrames, int size = DefaultSize, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return false;
        var mesh = StlMeshParser.Parse(stlPath);
        if (mesh is null || !mesh.IsValid) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;

        string? seqDir = null;
        try
        {
            seqDir = Path.Combine(Path.GetTempPath(), "promptflow-wire", Guid.NewGuid().ToString("N")[..8]);
            var render = RenderSequence(mesh, seqDir, frames, size, neon, ct);
            if (!render.Ok) return false;

            var dir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var chain = BuildOverlayFilter(1, "0:v", "vout", glowSigma: 7);
            var args = new List<string>
            {
                "-y", "-i", videoPath,
                "-stream_loop", "-1", "-framerate", render.Fps.ToString(CultureInfo.InvariantCulture), "-i", render.Pattern,
                "-filter_complex", chain, "-map", "[vout]", "-map", "0:a?",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "copy", "-shortest", "-movflags", "+faststart", outPath,
            };
            var res = await ProcessRunner.RunAsync(ffmpeg, args, workingDir: null, onLine: null, ct: ct).ConfigureAwait(false);
            return res.Ok && File.Exists(outPath);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("CadWireframe.Add", ex); return false; }
        finally { try { if (seqDir is not null && Directory.Exists(seqDir)) Directory.Delete(seqDir, true); } catch { } }
    }

    /// <summary>메시를 회전 와이어프레임 PNG 시퀀스로 렌더한다(순수 BCL). 비디오 없이 프리뷰 용도로도 사용 가능.</summary>
    public WireframeRender RenderSequence(CadMesh mesh, string outDir, int frames = DefaultFrames, int size = DefaultSize, string? neon = null, CancellationToken ct = default)
    {
        try
        {
            if (mesh is null || !mesh.IsValid) return new(false, "", "", 0, DefaultFps, "메시가 비어 있습니다.");
            frames = Math.Clamp(frames, 8, 120);
            size = Math.Clamp(size, 128, 1080);
            Directory.CreateDirectory(outDir);
            var (nr, ng, nb) = NeonRgb(neon);

            for (var f = 0; f < frames; f++)
            {
                ct.ThrowIfCancellationRequested();
                var angle = 2.0 * Math.PI * f / frames;
                var pts = Project(mesh, angle, size);
                var rgb = new byte[size * size * 3];   // 검정 배경(colorkey 대상)
                foreach (var (a, b) in mesh.Edges)
                {
                    var pa = pts[a]; var pb = pts[b];
                    DrawLine(rgb, size, size, pa.X, pa.Y, pb.X, pb.Y, nr, ng, nb);
                }
                WritePngRgb(Path.Combine(outDir, $"wire_{f:000}.png"), size, size, rgb);
            }
            return new(true, outDir, Path.Combine(outDir, "wire_%03d.png"), frames, DefaultFps, $"{frames} 프레임 렌더");
        }
        catch (OperationCanceledException) { return new(false, outDir, "", 0, DefaultFps, "취소됨"); }
        catch (Exception ex) { Program.Log("CadWireframe.Render", ex); return new(false, outDir, "", 0, DefaultFps, ex.Message); }
    }

    // ══════════════════════ 3D 회전 투영 ══════════════════════

    /// <summary>프레임 각도로 모든 정점을 2D 화면 좌표로 투영한다(Y축 회전 + X 틸트 + 원근 분할 · 중심/스케일 정규화).</summary>
    public static (float X, float Y)[] Project(CadMesh mesh, double angle, int size)
    {
        var center = mesh.Center;
        var inv = 1f / mesh.Radius;                 // 반경 1로 정규화
        const double tilt = 0.30;                    // 고정 X 틸트(입체감)
        const double camDist = 3.2;                  // 원근 카메라 거리
        double projScale = size * 0.40;              // 반경1 → 화면의 40%
        double half = size / 2.0;
        double ca = Math.Cos(angle), sa = Math.Sin(angle), ct2 = Math.Cos(tilt), st = Math.Sin(tilt);

        var outp = new (float, float)[mesh.Vertices.Count];
        for (var i = 0; i < mesh.Vertices.Count; i++)
        {
            var v = (mesh.Vertices[i] - center) * inv;
            double x = v.X, y = v.Y, z = v.Z;
            // Y축 회전
            double x1 = x * ca + z * sa, z1 = -x * sa + z * ca, y1 = y;
            // X축 틸트
            double y2 = y1 * ct2 - z1 * st, z2 = y1 * st + z1 * ct2, x2 = x1;
            // 원근 분할
            double factor = camDist / (camDist - z2);
            var sx = (float)(half + x2 * factor * projScale);
            var sy = (float)(half - y2 * factor * projScale);
            outp[i] = (sx, sy);
        }
        return outp;
    }

    // ── Bresenham 라인(네온 · 경계 클리핑) ──
    private static void DrawLine(byte[] rgb, int w, int h, float x0f, float y0f, float x1f, float y1f, byte r, byte g, byte b)
    {
        int x0 = (int)MathF.Round(x0f), y0 = (int)MathF.Round(y0f), x1 = (int)MathF.Round(x1f), y1 = (int)MathF.Round(y1f);
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        var guard = 0; var maxSteps = (w + h) * 3;
        while (true)
        {
            Plot(rgb, w, h, x0, y0, r, g, b);
            if (x0 == x1 && y0 == y1) break;
            if (++guard > maxSteps) break;                 // 방어(비정상 좌표 무한루프)
            int e2 = err << 1;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void Plot(byte[] rgb, int w, int h, int x, int y, byte r, byte g, byte b)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        var i = (y * w + x) * 3;
        rgb[i] = r; rgb[i + 1] = g; rgb[i + 2] = b;
    }

    // ══════════════════════ FFmpeg colorkey + 글로우 오버레이 ══════════════════════

    /// <summary>
    /// 와이어프레임(검정 배경 네온 PNG)을 비디오 위에 합성하는 filter_complex 를 만든다.
    ///   colorkey 로 검정 배경 제거 → split → gblur 글로우 사본 → 선명 사본을 글로우 위에 합성 → 중앙 오버레이.
    /// </summary>
    public static string BuildOverlayFilter(int wireInputIdx, string baseLabel, string outLabel, int glowSigma = 7)
    {
        var g = Math.Clamp(glowSigma, 1, 40);
        return
            $"[{wireInputIdx}:v]colorkey=0x000000:0.16:0.05,format=rgba,split[wsharp][wglowsrc];" +
            $"[wglowsrc]gblur=sigma={g}[wglow];" +
            $"[wglow][wsharp]overlay=0:0:format=auto[wire];" +
            $"[{baseLabel}][wire]overlay=(W-w)/2:(H-h)/2:format=auto[{outLabel}]";
    }

    // ══════════════════════ 순수 BCL PNG 인코더(RGB · 8bit) ══════════════════════

    /// <summary>RGB(width×height×3) 버퍼를 PNG(color type 2)로 저장한다 — System.IO.Compression zlib + CRC32(외부 라이브러리 0).</summary>
    public static void WritePngRgb(string path, int width, int height, byte[] rgb)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        Span<byte> sig = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        fs.Write(sig);

        // IHDR
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width); WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 2;   // color type 2 = truecolor RGB
        ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;   // deflate / filter 0 / no interlace
        WriteChunk(fs, "IHDR", ihdr);

        // IDAT — 스캔라인마다 필터 0 prefix 후 zlib 압축
        byte[] idat;
        using (var raw = new MemoryStream())
        {
            var stride = width * 3;
            var scan = new byte[1 + stride];
            for (var y = 0; y < height; y++)
            {
                scan[0] = 0;   // filter type None
                Buffer.BlockCopy(rgb, y * stride, scan, 1, stride);
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
        var t = new byte[] { (byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3] };
        s.Write(t, 0, 4);
        if (data.Length > 0) s.Write(data, 0, data.Length);
        var crc = Crc32(t, data);
        Span<byte> c = stackalloc byte[4];
        WriteBE(c, 0, crc);
        s.Write(c);
    }

    private static void WriteBE(Span<byte> b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    // ── CRC32(PNG · 표준 다항식 0xEDB88320) ──
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
