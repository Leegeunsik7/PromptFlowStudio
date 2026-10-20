using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Media;

/// <summary>v16.19.0 누끼 방식 — 원본 알파 유지(PNG/WebP 투명 배경) · 테두리 배경색 플러드 키잉(흰/단색 배경 상품 사진).</summary>
public enum ProductMaskMode { ExistingAlpha, BorderFloodKey }

/// <summary>v16.19.0 키잉 옵션 — 배경색 허용 거리(RGB 유클리드 0~441) · 페더 폭 · 원본 알파 우선 여부.</summary>
public sealed record ProductMaskOptions(int Tolerance = 42, int Feather = 28, bool PreserveExistingAlpha = true);

/// <summary>v16.19.0 알파 가중 색 통계(0~255) — 채널 평균 · 휘도 평균/표준편차 · 표본 수.</summary>
public readonly record struct ColorStats(double R, double G, double B, double Luma, double LumaStd, int Samples)
{
    public bool IsEmpty => Samples <= 0;
}

/// <summary>v16.19.0 마스크 추출 결과 — 방식 · 전경(알파&gt;0) 비율 · 추정 배경색.</summary>
public readonly record struct ProductMaskStats(ProductMaskMode Mode, double Coverage, byte BgR, byte BgG, byte BgB);

/// <summary>v16.19.0 조명 매칭 파라미터 — 채널 색온도 게인 · 대비(피벗 기준) · 휘도 이동.</summary>
public readonly record struct LightingMatch(double GainR, double GainG, double GainB, double Contrast, double Pivot, double Shift)
{
    public static readonly LightingMatch Identity = new(1, 1, 1, 1, 128, 0);
}

/// <summary>v16.19.0 누끼 + 조명 매칭 결과.</summary>
public sealed record ProductSegmentResult(
    bool Ok, string? CutoutPath, int Width, int Height, ProductMaskStats Mask,
    ColorStats Product, ColorStats Background, LightingMatch Match, string BackgroundSource, string Message);

/// <summary>
/// [PromptFlowStudio] v16.19.0 ✂️ 협찬 제품 누끼 추출 &amp; 조명 매칭 엔진 — 아동용품 사진 → 투명 PNG(알파 마스크) + 장면 색감/조명 정합(순수 BCL · ffmpeg 디코드/인코드).
///   ▸ 디코드: ffmpeg 로 원본 크기 탐지(stderr "WxH") → 1024 박스 맞춤 → RGBA raw(임시 파일).
///   ▸ 마스크: 원본에 의미 있는 알파가 있으면 유지, 아니면 테두리 평균 배경색에서 4-연결 플러드 필 — 테두리와 연결된 배경만 제거하므로
///     흰색 제품 내부(흰 젖병·기저귀)는 보존. 허용 거리~+페더 구간은 부분 알파 + 배경색 스필 제거(언프리멀티플라이).
///   ▸ 조명: 장면 영상 1프레임(64x36) 통계 ↔ 제품 알파 가중 통계 → 색온도 게인·대비·휘도 이동(보수적 클램프 · 제품 색 왜곡 방지).
///     장면이 없거나 실패하면 <see cref="ThemeLighting"/> 프로필 폴백(204+ 테마 시네마틱 메타 연동).
///   ▸ 픽셀 루프는 Span + ArrayPool 스크래치(무할당) · 공개 순수 함수로 합성 이미지 검증 가능 · 실패는 결과 record(예외 0).
/// </summary>
public sealed class CommercialProductSegmenterService
{
    /// <summary>디코드 박스 상한(px).</summary>
    public const int MaxEdge = 1024;
    /// <summary>장면 조명 샘플 해상도.</summary>
    public const int SampleW = 64, SampleH = 36;

    private readonly Func<string?> _ffmpeg;

    public CommercialProductSegmenterService(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    /// <param name="ffmpegPath">ffmpeg 경로 공급자(테스트 주입용).</param>
    public CommercialProductSegmenterService(Func<string?> ffmpegPath) => _ffmpeg = ffmpegPath;

    public bool IsAvailable => _ffmpeg() is not null;

    // ── 순수 함수(픽셀) ─────────────────────────────────────────────────────

    /// <summary>테두리 1px 의 불투명 픽셀 평균색. 불투명 테두리가 없으면 false.</summary>
    public static bool EstimateBorderColor(ReadOnlySpan<byte> rgba, int w, int h, out byte r, out byte g, out byte b)
    {
        Validate(rgba.Length, w, h);
        long sr = 0, sg = 0, sb = 0, n = 0;
        for (var x = 0; x < w; x++) { Acc(rgba, x, 0, w, ref sr, ref sg, ref sb, ref n); if (h > 1) Acc(rgba, x, h - 1, w, ref sr, ref sg, ref sb, ref n); }
        for (var y = 1; y < h - 1; y++) { Acc(rgba, 0, y, w, ref sr, ref sg, ref sb, ref n); if (w > 1) Acc(rgba, w - 1, y, w, ref sr, ref sg, ref sb, ref n); }
        if (n == 0) { r = g = b = 255; return false; }
        r = (byte)(sr / n); g = (byte)(sg / n); b = (byte)(sb / n);
        return true;
    }

    /// <summary>원본 알파가 누끼로 쓸 만한지 — 투명(&lt;250) 픽셀 1% 이상 + 테두리 과반 투명.</summary>
    public static bool HasMeaningfulAlpha(ReadOnlySpan<byte> rgba, int w, int h)
    {
        Validate(rgba.Length, w, h);
        var total = w * h;
        var translucent = 0;
        for (var i = 3; i < total * 4; i += 4) if (rgba[i] < 250) translucent++;
        if (translucent * 100 < total) return false;
        int border = 0, clear = 0;
        for (var x = 0; x < w; x++)
        {
            border += 2;
            if (rgba[x * 4 + 3] < 128) clear++;
            if (rgba[((h - 1) * w + x) * 4 + 3] < 128) clear++;
        }
        return clear * 2 >= border;
    }

    /// <summary>
    /// 알파 마스크 추출(제자리 · 무할당 — 스크래치는 ArrayPool). 반환: 방식 · 전경 비율 · 추정 배경색.
    /// </summary>
    public static ProductMaskStats ExtractAlphaMask(Span<byte> rgba, int w, int h, ProductMaskOptions? options = null)
    {
        Validate(rgba.Length, w, h);
        var o = options ?? new ProductMaskOptions();
        if (o.PreserveExistingAlpha && HasMeaningfulAlpha(rgba, w, h))
            return new ProductMaskStats(ProductMaskMode.ExistingAlpha, Coverage(rgba), 0, 0, 0);

        EstimateBorderColor(rgba, w, h, out var br, out var bg, out var bb);
        var tol = Math.Clamp(o.Tolerance, 1, 400);
        var feather = Math.Clamp(o.Feather, 1, 200);
        var loose = tol + feather;
        var looseSq = loose * loose;
        var total = w * h;
        var queue = ArrayPool<int>.Shared.Rent(total);
        var seen = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            seen.AsSpan(0, total).Clear();
            int head = 0, tail = 0;
            for (var x = 0; x < w; x++) { Seed(rgba, x, 0, w, br, bg, bb, looseSq, seen, queue, ref tail); Seed(rgba, x, h - 1, w, br, bg, bb, looseSq, seen, queue, ref tail); }
            for (var y = 1; y < h - 1; y++) { Seed(rgba, 0, y, w, br, bg, bb, looseSq, seen, queue, ref tail); Seed(rgba, w - 1, y, w, br, bg, bb, looseSq, seen, queue, ref tail); }
            while (head < tail)
            {
                var p = queue[head++];
                var px = p % w;
                var py = p / w;
                if (px > 0) Seed(rgba, px - 1, py, w, br, bg, bb, looseSq, seen, queue, ref tail);
                if (px < w - 1) Seed(rgba, px + 1, py, w, br, bg, bb, looseSq, seen, queue, ref tail);
                if (py > 0) Seed(rgba, px, py - 1, w, br, bg, bb, looseSq, seen, queue, ref tail);
                if (py < h - 1) Seed(rgba, px, py + 1, w, br, bg, bb, looseSq, seen, queue, ref tail);
            }
            for (var i = 0; i < tail; i++)
            {
                var o4 = queue[i] * 4;
                var d = Math.Sqrt(DistSq(rgba, o4, br, bg, bb));
                if (d <= tol) { rgba[o4 + 3] = 0; continue; }
                var a = (d - tol) / feather;
                var a8 = (byte)Math.Clamp((int)(a * rgba[o4 + 3] + 0.5), 0, 255);
                rgba[o4 + 3] = a8;
                // 배경색 스필 제거: c = (c − bg·(1−a)) / a
                var inv = 1.0 - a;
                rgba[o4] = Clamp8((rgba[o4] - br * inv) / a);
                rgba[o4 + 1] = Clamp8((rgba[o4 + 1] - bg * inv) / a);
                rgba[o4 + 2] = Clamp8((rgba[o4 + 2] - bb * inv) / a);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<byte>.Shared.Return(seen);
        }
        return new ProductMaskStats(ProductMaskMode.BorderFloodKey, Coverage(rgba), br, bg, bb);
    }

    /// <summary>알파 가중 색 통계(알파 &lt; minAlpha 무시). 불투명 영상 프레임은 전 픽셀 동일 가중.</summary>
    public static ColorStats ComputeStats(ReadOnlySpan<byte> rgba, byte minAlpha = 16)
    {
        double wsum = 0, sr = 0, sg = 0, sb = 0, sl = 0, sl2 = 0;
        var n = 0;
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            var a = rgba[i + 3];
            if (a < minAlpha) continue;
            double wgt = a / 255.0;
            double r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
            var l = Luma(r, g, b);
            wsum += wgt; sr += r * wgt; sg += g * wgt; sb += b * wgt; sl += l * wgt; sl2 += l * l * wgt;
            n++;
        }
        if (n == 0 || wsum <= 0) return default;
        var ml = sl / wsum;
        return new ColorStats(sr / wsum, sg / wsum, sb / wsum, ml, Math.Sqrt(Math.Max(0, sl2 / wsum - ml * ml)), n);
    }

    /// <summary>
    /// 제품 → 장면 조명 정합 파라미터(보수적). strength 0~1(기본 0.6): 색온도 게인 ±15% · 대비 ±15% · 휘도 이동 ±40.
    /// </summary>
    public static LightingMatch MatchLighting(ColorStats product, ColorStats target, double strength = 0.6)
    {
        if (product.IsEmpty || target.IsEmpty) return LightingMatch.Identity;
        var s = Math.Clamp(strength, 0, 1) * 0.5;
        var pl = Math.Max(1, product.Luma);
        var tl = Math.Max(1, target.Luma);
        double Tint(double pc, double tc) => Math.Clamp(Lerp(1, (Math.Max(1, tc) / tl) / (Math.Max(1, pc) / pl), s), 0.85, 1.15);
        var contrast = Math.Clamp(Lerp(1, (target.LumaStd + 8) / (product.LumaStd + 8), s), 0.85, 1.15);
        var shift = Math.Clamp((Lerp(pl, tl, s) - pl), -40, 40);
        return new LightingMatch(Tint(product.R, target.R), Tint(product.G, target.G), Tint(product.B, target.B), contrast, pl, shift);
    }

    /// <summary>조명 매칭 적용(제자리 · 알파 0 픽셀 건너뜀).</summary>
    public static void ApplyLighting(Span<byte> rgba, LightingMatch m)
    {
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            if (rgba[i + 3] == 0) continue;
            rgba[i] = Clamp8((rgba[i] * m.GainR - m.Pivot) * m.Contrast + m.Pivot + m.Shift);
            rgba[i + 1] = Clamp8((rgba[i + 1] * m.GainG - m.Pivot) * m.Contrast + m.Pivot + m.Shift);
            rgba[i + 2] = Clamp8((rgba[i + 2] * m.GainB - m.Pivot) * m.Contrast + m.Pivot + m.Shift);
        }
    }

    /// <summary>테마 조명 프로필 → 목표 통계(장면 프레임 미확보 시 폴백).</summary>
    public static ColorStats LightingProfileOf(ThemeLighting l) => l switch
    {
        ThemeLighting.GoldenHour => new(214, 168, 112, 176, 52, 1),
        ThemeLighting.StudioSoft => new(206, 200, 204, 202, 34, 1),
        ThemeLighting.NaturalDaylight => new(178, 188, 196, 186, 48, 1),
        ThemeLighting.MoonlightBlue => new(82, 98, 140, 98, 40, 1),
        ThemeLighting.WarmTungsten => new(196, 140, 92, 152, 46, 1),
        _ => new(168, 110, 190, 134, 60, 1),
    };

    /// <summary>ffmpeg stderr 의 첫 영상 스트림 크기("Video: … 800x600") 파싱.</summary>
    public static bool TryParseVideoSize(string? stderr, out int w, out int h)
    {
        w = h = 0;
        if (string.IsNullOrEmpty(stderr)) return false;
        var s = stderr.AsSpan();
        var at = s.IndexOf("Video:", StringComparison.Ordinal);
        if (at < 0) return false;
        s = s[(at + 6)..];
        var eol = s.IndexOfAny('\r', '\n');
        if (eol >= 0) s = s[..eol];
        for (var i = 1; i < s.Length - 1; i++)
        {
            if (s[i] != 'x' || !char.IsAsciiDigit(s[i - 1]) || !char.IsAsciiDigit(s[i + 1])) continue;
            var a = i - 1;
            while (a > 0 && char.IsAsciiDigit(s[a - 1])) a--;
            var b = i + 1;
            while (b < s.Length - 1 && char.IsAsciiDigit(s[b + 1])) b++;
            if (int.TryParse(s[a..i], NumberStyles.None, CultureInfo.InvariantCulture, out w)
                && int.TryParse(s[(i + 1)..(b + 1)], NumberStyles.None, CultureInfo.InvariantCulture, out h) && w > 0 && h > 0) return true;
        }
        w = h = 0;
        return false;
    }

    /// <summary>박스 맞춤(비율 유지 · 짝수 · 확대 없음).</summary>
    public static (int W, int H) FitBox(int w, int h, int max = MaxEdge)
    {
        if (w <= 0 || h <= 0) return (2, 2);
        var k = Math.Min(1.0, (double)max / Math.Max(w, h));
        return (Math.Max(2, (int)(w * k) & ~1), Math.Max(2, (int)(h * k) & ~1));
    }

    // ── 실행(ffmpeg) ────────────────────────────────────────────────────────

    /// <summary>
    /// 누끼 + 조명 매칭 → 투명 PNG(예외 0). sceneVideo 가 있으면 1초 지점 프레임 통계, 없으면 fallbackLighting 프로필.
    /// </summary>
    public async Task<ProductSegmentResult> SegmentAsync(string imagePath, string outputPng, string? sceneVideo = null,
        ThemeLighting? fallbackLighting = null, ProductMaskOptions? options = null, double strength = 0.6, CancellationToken ct = default)
    {
        var exe = _ffmpeg();
        if (exe is null) return Fail("ffmpeg.exe 를 찾을 수 없습니다.");
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath)) return Fail("제품 사진이 없습니다.");
        if (string.IsNullOrWhiteSpace(outputPng)) return Fail("출력 경로가 비었습니다.");
        var tmp = Path.Combine(Path.GetTempPath(), "pfs_brand_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tmp);
            var probe = await ProcessRunner.RunAsync(exe, new List<string> { "-hide_banner", "-nostdin", "-i", imagePath }, ct: ct).ConfigureAwait(false);
            if (!TryParseVideoSize(probe.StdErr, out var sw, out var sh)) return Fail("이미지 크기를 읽지 못했습니다(지원되지 않는 형식).");
            var (w, h) = FitBox(sw, sh);
            var raw = Path.Combine(tmp, "product.rgba");
            var dec = await ProcessRunner.RunAsync(exe, new List<string>
            {
                "-y", "-hide_banner", "-nostdin", "-i", imagePath,
                "-vf", "scale=" + Inv(w) + ":" + Inv(h) + ":flags=lanczos,format=rgba",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", raw,
            }, ct: ct).ConfigureAwait(false);
            if (!dec.Ok || !File.Exists(raw)) return Fail("제품 사진 디코드 실패");
            var px = await File.ReadAllBytesAsync(raw, ct).ConfigureAwait(false);
            if (px.Length < w * h * 4) return Fail("디코드 크기 불일치");

            var mask = ExtractAlphaMask(px.AsSpan(0, w * h * 4), w, h, options);
            if (mask.Coverage < 0.01) return Fail("제품 영역을 찾지 못했습니다 — 배경과 제품 색이 너무 비슷합니다.");
            var prod = ComputeStats(px.AsSpan(0, w * h * 4));

            var (bgStats, source) = await SampleSceneAsync(exe, sceneVideo, tmp, ct).ConfigureAwait(false);
            if (bgStats.IsEmpty && fallbackLighting is { } fl) { bgStats = LightingProfileOf(fl); source = "테마 조명 프로필 · " + ThemeCinematicsCatalog.LightingLabel(fl); }
            var match = bgStats.IsEmpty ? LightingMatch.Identity : MatchLighting(prod, bgStats, strength);
            if (!bgStats.IsEmpty) ApplyLighting(px.AsSpan(0, w * h * 4), match);
            if (bgStats.IsEmpty) source = "조명 매칭 생략";

            await File.WriteAllBytesAsync(raw, px, ct).ConfigureAwait(false);
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPng));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var enc = await ProcessRunner.RunAsync(exe, new List<string>
            {
                "-y", "-hide_banner", "-nostdin", "-f", "rawvideo", "-pix_fmt", "rgba", "-s", Inv(w) + "x" + Inv(h), "-i", raw,
                "-frames:v", "1", "-pix_fmt", "rgba", outputPng,
            }, ct: ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return Fail("취소됨");
            if (!enc.Ok || !File.Exists(outputPng)) return Fail("투명 PNG 인코딩 실패");
            var msg = (mask.Mode == ProductMaskMode.ExistingAlpha ? "원본 알파 유지" : "배경 키잉") + " · 전경 "
                + (mask.Coverage * 100).ToString("0", CultureInfo.InvariantCulture) + "% · " + source;
            return new ProductSegmentResult(true, outputPng, w, h, mask, prod, bgStats, match, source, msg);
        }
        catch (OperationCanceledException) { return Fail("취소됨"); }
        catch (Exception ex) { Program.Log("ProductSegmenter.Segment", ex); return Fail("누끼 추출 실패: " + ex.Message); }
        finally { try { Directory.Delete(tmp, true); } catch { /* 임시 폴더 정리 실패 무시 */ } }
    }

    private static async Task<(ColorStats Stats, string Source)> SampleSceneAsync(string exe, string? video, string tmp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(video) || !File.Exists(video)) return (default, "");
        var raw = Path.Combine(tmp, "scene.rgba");
        foreach (var ss in SeekPoints)
        {
            var r = await ProcessRunner.RunAsync(exe, new List<string>
            {
                "-y", "-hide_banner", "-nostdin", "-ss", ss, "-i", video,
                "-vf", "scale=" + Inv(SampleW) + ":" + Inv(SampleH) + ",format=rgba",
                "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgba", raw,
            }, ct: ct).ConfigureAwait(false);
            if (!r.Ok || !File.Exists(raw)) continue;
            var px = await File.ReadAllBytesAsync(raw, ct).ConfigureAwait(false);
            if (px.Length < SampleW * SampleH * 4) continue;
            return (ComputeStats(px.AsSpan(0, SampleW * SampleH * 4), 0), "장면 프레임 " + ss + "초");
        }
        return (default, "");
    }

    private static readonly string[] SeekPoints = { "1", "0" };

    private static ProductSegmentResult Fail(string msg) => new(false, null, 0, 0, default, default, default, LightingMatch.Identity, "", msg);

    private static void Validate(int len, int w, int h)
    {
        if (w <= 0 || h <= 0 || len < w * h * 4) throw new ArgumentException("RGBA 버퍼 크기가 w×h×4 보다 작습니다.");
    }

    private static void Acc(ReadOnlySpan<byte> p, int x, int y, int w, ref long r, ref long g, ref long b, ref long n)
    {
        var o = (y * w + x) * 4;
        if (p[o + 3] < 128) return;
        r += p[o]; g += p[o + 1]; b += p[o + 2]; n++;
    }

    private static void Seed(Span<byte> p, int x, int y, int w, byte br, byte bg, byte bb, int looseSq, byte[] seen, int[] queue, ref int tail)
    {
        var i = y * w + x;
        if (seen[i] != 0) return;
        seen[i] = 1;
        var o = i * 4;
        if (p[o + 3] == 0) { queue[tail++] = i; return; }
        if (DistSq(p, o, br, bg, bb) > looseSq) return;
        queue[tail++] = i;
    }

    private static int DistSq(ReadOnlySpan<byte> p, int o, byte r, byte g, byte b)
    {
        int dr = p[o] - r, dg = p[o + 1] - g, db = p[o + 2] - b;
        return dr * dr + dg * dg + db * db;
    }

    private static double Coverage(ReadOnlySpan<byte> p)
    {
        var n = p.Length / 4;
        if (n == 0) return 0;
        var on = 0;
        for (var i = 3; i < n * 4; i += 4) if (p[i] > 0) on++;
        return (double)on / n;
    }

    private static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static byte Clamp8(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);
    private static string Inv(int v) => v.ToString(CultureInfo.InvariantCulture);
}
