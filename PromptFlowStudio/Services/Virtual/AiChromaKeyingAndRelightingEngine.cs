using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Interactive;   // ShoppableVideoOverlayEngine.ProbeAsync
using PromptFlowStudio.Services.Media;         // IVideoMergeService
using PromptFlowStudio.Services.Spatial;       // SpatialImageCodec · ReadExactAsync

namespace PromptFlowStudio.Services.Virtual;

/// <summary>키 색(RGB + BT.601 CbCr) — IsBlue = 블루스크린.</summary>
public readonly record struct KeyColor(byte R, byte G, byte B, bool IsBlue)
{
    public static readonly KeyColor ChromaGreen = new(0, 177, 64, false);
    public float Y => 0.299f * R + 0.587f * G + 0.114f * B;
    public float Cb => 128f - 0.168736f * R - 0.331264f * G + 0.5f * B;
    public float Cr => 128f + 0.5f * R - 0.418688f * G - 0.081312f * B;
    public string Hex => "#" + R.ToString("X2", CultureInfo.InvariantCulture) + G.ToString("X2", CultureInfo.InvariantCulture) + B.ToString("X2", CultureInfo.InvariantCulture);
}

/// <summary>키잉 설정 — 감도(유사도) · 경계 부드러움 · 스필 억제 · 매트 수축(px) · 페더(px) · 리라이팅 강도 · 라이트 랩.</summary>
public readonly record struct KeySettings(float Similarity, float Smoothness, float Spill, int Choke, int Feather, float Relight, float LightWrap, bool AutoKey, KeyColor Manual)
{
    public static readonly KeySettings Default = new(0.18f, 0.08f, 0.7f, 0, 1, 0.5f, 0.3f, true, KeyColor.ChromaGreen);

    public KeySettings Clamp() => this with
    {
        Similarity = Math.Clamp(Similarity, 0.01f, 0.8f), Smoothness = Math.Clamp(Smoothness, 0.005f, 0.5f), Spill = Math.Clamp(Spill, 0f, 1f),
        Choke = Math.Clamp(Choke, 0, 2), Feather = Math.Clamp(Feather, 0, 4), Relight = Math.Clamp(Relight, 0f, 1f), LightWrap = Math.Clamp(LightWrap, 0f, 1f),
    };
}

/// <summary>조명 프로파일 — 채널 평균 · 휘도 평균/표준편차 · 좌우/상하 밝기 균형(-1~1 · + = 우/하 밝음).</summary>
public readonly record struct LightingProfile(float MeanR, float MeanG, float MeanB, float MeanL, float StdL, float BalanceX, float BalanceY);

public readonly record struct KeyFrameStats(KeyColor Key, float Coverage, LightingProfile Foreground, LightingProfile Background, double Milliseconds);

public sealed record ChromaPreview(int Width, int Height, string MatteDataUrl, string CompositeDataUrl, KeyFrameStats Stats);

public sealed record ChromaVideoResult(bool Ok, string? OutputPath, int Frames, string Message);

/// <summary>
/// [PromptFlow Studio v16.32.0] AI 자동 크로마키 디키잉 &amp; 조명 매칭 엔진(순수 BCL CPU 셰이더 · 외부 DLL 0).
///   ▸ 스마트 키: 테두리 밴드에서 그린/블루 지배 픽셀 다수결로 키 색 자동 검출(없으면 표준 크로마 그린).
///   ▸ 매트: BT.601 CbCr 거리 smoothstep(감도 → 감도+부드러움) + 휘도 가드(그림자/하이라이트 보호) → 3×3 최소 필터 수축 → 박스 페더.
///   ▸ 스필 억제: 그린(블루) 채널을 나머지 최대 채널로 제한(강도 비례).
///   ▸ 리라이팅: 전경(알파 가중)·배경 조명 프로파일 → 채널 평균/대비 전이(Reinhard 계열) + 배경 광원 방향 그라디언트 셰이딩 + 경계 라이트 랩.
///   ▸ <see cref="ProcessFrame"/>: 인스턴스 버퍼 1회 확보 후 재사용(프레임당 할당 0 · LINQ/박싱 0).
///   ▸ 영상: ffmpeg 전경/배경(이미지 루프·영상 루프) rawvideo 디코드 → 합성 → H.264 인코드(전경 오디오 유지) · 프리뷰 BMP.
/// </summary>
public sealed class AiChromaKeyingAndRelightingEngine
{
    public const int PreviewWidth = 480;
    public const int MaxFrames = 36_000;
    public static readonly TimeSpan VideoTimeout = TimeSpan.FromMinutes(30);

    private readonly Func<string?> _ffmpeg;
    private readonly object _gate = new();
    private byte[]? _alpha;
    private float[]? _f1, _f2;
    private byte[]? _pvFg, _pvBg;
    private int _pvW, _pvH, _busy;
    private long _frames;

    public AiChromaKeyingAndRelightingEngine(IVideoMergeService merge) : this(() => merge.ResolvedFfmpegPath) { }

    public AiChromaKeyingAndRelightingEngine(Func<string?> ffmpeg) => _ffmpeg = ffmpeg;

    public event Action? OnChange;

    public KeySettings Settings { get; set; } = KeySettings.Default;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public bool FfmpegAvailable => _ffmpeg() is not null;
    public long FramesKeyed => Interlocked.Read(ref _frames);
    public KeyFrameStats? LastStats { get; private set; }
    public ChromaPreview? LastPreview { get; private set; }
    public string? LastAction { get; private set; }

    // ══════════════════════ 스마트 키 색 검출 ══════════════════════

    /// <summary>테두리 밴드(짧은 변 4%)의 그린/블루 지배 픽셀 다수결 평균(무할당).</summary>
    public static KeyColor DetectKeyColor(ReadOnlySpan<byte> rgb, int w, int h)
    {
        if (w < 2 || h < 2 || rgb.Length < w * h * 3) return KeyColor.ChromaGreen;
        var band = Math.Max(1, Math.Min(w, h) / 25);
        long gr = 0, gg = 0, gb = 0, br = 0, bg = 0, bb = 0;
        int gn = 0, bn = 0;
        for (var y = 0; y < h; y++)
        {
            var full = y < band || y >= h - band;
            for (var x = 0; x < w; x++)
            {
                if (!full && x >= band && x < w - band) { x = w - band - 1; continue; }
                var j = (y * w + x) * 3;
                int r = rgb[j], g = rgb[j + 1], b = rgb[j + 2];
                if (g > r + 24 && g > b + 24) { gr += r; gg += g; gb += b; gn++; }
                else if (b > r + 24 && b > g + 24) { br += r; bg += g; bb += b; bn++; }
            }
        }
        if (gn == 0 && bn == 0) return KeyColor.ChromaGreen;
        return gn >= bn
            ? new((byte)(gr / gn), (byte)(gg / gn), (byte)(gb / gn), false)
            : new((byte)(br / bn), (byte)(bg / bn), (byte)(bb / bn), true);
    }

    // ══════════════════════ 매트 ══════════════════════

    /// <summary>알파 매트(0=키 배경 · 255=전경) — scratch1/2 ≥ w·h. 무할당.</summary>
    public static float ExtractMatte(ReadOnlySpan<byte> rgb, int w, int h, KeyColor key, in KeySettings settings, Span<byte> alpha, Span<float> s1, Span<float> s2)
    {
        var n = w * h;
        if (w < 3 || h < 3 || rgb.Length < n * 3 || alpha.Length < n || s1.Length < n || s2.Length < n) throw new ArgumentException("버퍼 크기 불일치");
        var s = settings.Clamp();
        float kcb = key.Cb, kcr = key.Cr, ky = key.Y;
        var lo = s.Similarity;
        var inv = 1f / s.Smoothness;
        for (int i = 0, j = 0; i < n; i++, j += 3)
        {
            float r = rgb[j], g = rgb[j + 1], b = rgb[j + 2];
            var cb = 128f - 0.168736f * r - 0.331264f * g + 0.5f * b;
            var cr = 128f + 0.5f * r - 0.418688f * g - 0.081312f * b;
            float dcb = cb - kcb, dcr = cr - kcr;
            var d = MathF.Sqrt(dcb * dcb + dcr * dcr) * (1f / 128f);
            var t = Math.Clamp((d - lo) * inv, 0f, 1f);
            var a = t * t * (3 - 2 * t);
            var dy = MathF.Abs(0.299f * r + 0.587f * g + 0.114f * b - ky) * (1f / 255f);
            if (dy > 0.45f) a = MathF.Max(a, Math.Clamp((dy - 0.45f) * 4f, 0f, 1f));   // 휘도 가드
            s1[i] = a;
        }
        var cur = s1; var other = s2;
        for (var c = 0; c < s.Choke; c++)
        {
            MinFilter3(cur, other, w, h);
            var tmp = cur; cur = other; other = tmp;
        }
        if (s.Feather > 0) BoxBlur(cur, other, w, h, s.Feather);
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            var v = Math.Clamp(cur[i], 0f, 1f);
            alpha[i] = (byte)(v * 255f + 0.5f);
            sum += v;
        }
        return (float)(sum / n);
    }

    private static void MinFilter3(Span<float> src, Span<float> dst, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
            for (var x = 0; x < w; x++)
            {
                int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1);
                var m = 1f;
                for (var yy = y0; yy <= y1; yy++)
                    for (var xx = x0; xx <= x1; xx++) m = MathF.Min(m, src[yy * w + xx]);
                dst[y * w + x] = m;
            }
        }
    }

    private static void BoxBlur(Span<float> data, Span<float> tmp, int w, int h, int r)
    {
        var inv = 1f / (2 * r + 1);
        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            var sum = 0f;
            for (var k = -r; k <= r; k++) sum += data[row + Math.Clamp(k, 0, w - 1)];
            for (var x = 0; x < w; x++)
            {
                tmp[row + x] = sum * inv;
                sum += data[row + Math.Min(x + r + 1, w - 1)] - data[row + Math.Max(x - r, 0)];
            }
        }
        for (var x = 0; x < w; x++)
        {
            var sum = 0f;
            for (var k = -r; k <= r; k++) sum += tmp[Math.Clamp(k, 0, h - 1) * w + x];
            for (var y = 0; y < h; y++)
            {
                data[y * w + x] = sum * inv;
                sum += tmp[Math.Min(y + r + 1, h - 1) * w + x] - tmp[Math.Max(y - r, 0) * w + x];
            }
        }
    }

    // ══════════════════════ 조명 분석 · 합성 ══════════════════════

    /// <summary>조명 프로파일(weights 비어 있으면 균등 · 알파 가중). 무할당.</summary>
    public static LightingProfile AnalyzeLighting(ReadOnlySpan<byte> rgb, ReadOnlySpan<byte> weights, int w, int h)
    {
        var n = w * h;
        if (w < 2 || h < 2 || rgb.Length < n * 3) return new(128, 128, 128, 128, 40, 0, 0);
        var useW = weights.Length >= n;
        double sw = 0, sr = 0, sg = 0, sb = 0, sl = 0, sl2 = 0, left = 0, right = 0, top = 0, bottom = 0, wl = 0, wr = 0, wt = 0, wb = 0;
        var halfX = w / 2; var halfY = h / 2;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                double wt0 = useW ? weights[i] * (1.0 / 255) : 1.0;
                if (wt0 <= 0) continue;
                var j = i * 3;
                double r = rgb[j], g = rgb[j + 1], b = rgb[j + 2];
                var l = 0.299 * r + 0.587 * g + 0.114 * b;
                sw += wt0; sr += r * wt0; sg += g * wt0; sb += b * wt0; sl += l * wt0; sl2 += l * l * wt0;
                if (x < halfX) { left += l * wt0; wl += wt0; } else { right += l * wt0; wr += wt0; }
                if (y < halfY) { top += l * wt0; wt += wt0; } else { bottom += l * wt0; wb += wt0; }
            }
        }
        if (sw < 1e-6) return new(128, 128, 128, 128, 40, 0, 0);
        var ml = sl / sw;
        var std = Math.Sqrt(Math.Max(0, sl2 / sw - ml * ml));
        double ll = wl > 0 ? left / wl : ml, rr = wr > 0 ? right / wr : ml, tt = wt > 0 ? top / wt : ml, bb = wb > 0 ? bottom / wb : ml;
        return new((float)(sr / sw), (float)(sg / sw), (float)(sb / sw), (float)ml, (float)std,
            (float)((rr - ll) / (rr + ll + 1e-6)), (float)((bb - tt) / (bb + tt + 1e-6)));
    }

    /// <summary>스필 억제 → 리라이팅(평균/대비 전이 + 방향 셰이딩) → 라이트 랩 → 알파 합성. 무할당.</summary>
    public static void Composite(ReadOnlySpan<byte> fg, ReadOnlySpan<byte> bg, ReadOnlySpan<byte> alpha, int w, int h, KeyColor key, in KeySettings settings,
        in LightingProfile fgP, in LightingProfile bgP, Span<byte> outRgb)
    {
        var n = w * h;
        if (fg.Length < n * 3 || bg.Length < n * 3 || alpha.Length < n || outRgb.Length < n * 3) throw new ArgumentException("버퍼 크기 불일치");
        var s = settings.Clamp();
        var cs = Math.Clamp(bgP.StdL / MathF.Max(1f, fgP.StdL), 0.6f, 1.6f);
        var rl = s.Relight;
        float ix = w > 1 ? 2f / (w - 1) : 0, iy = h > 1 ? 2f / (h - 1) : 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                var j = i * 3;
                var a = alpha[i] * (1f / 255f);
                float br = bg[j], bgc = bg[j + 1], bb = bg[j + 2];
                if (a <= 0f) { outRgb[j] = bg[j]; outRgb[j + 1] = bg[j + 1]; outRgb[j + 2] = bg[j + 2]; continue; }
                float r = fg[j], g = fg[j + 1], b = fg[j + 2];
                if (key.IsBlue) { var m = MathF.Max(r, g); if (b > m) b = m + (b - m) * (1f - s.Spill); }
                else { var m = MathF.Max(r, b); if (g > m) g = m + (g - m) * (1f - s.Spill); }
                if (rl > 0)
                {
                    var shade = 1f + rl * 0.35f * (bgP.BalanceX * (x * ix - 1f) + bgP.BalanceY * (y * iy - 1f));
                    r += rl * (((r - fgP.MeanR) * cs + bgP.MeanR) - r); r *= shade;
                    g += rl * (((g - fgP.MeanG) * cs + bgP.MeanG) - g); g *= shade;
                    b += rl * (((b - fgP.MeanB) * cs + bgP.MeanB) - b); b *= shade;
                }
                var wrap = s.LightWrap * 4f * a * (1f - a);
                r += (br - r) * wrap; g += (bgc - g) * wrap; b += (bb - b) * wrap;
                outRgb[j] = (byte)Math.Clamp(r * a + br * (1f - a) + 0.5f, 0f, 255f);
                outRgb[j + 1] = (byte)Math.Clamp(g * a + bgc * (1f - a) + 0.5f, 0f, 255f);
                outRgb[j + 2] = (byte)Math.Clamp(b * a + bb * (1f - a) + 0.5f, 0f, 255f);
            }
        }
    }

    /// <summary>프레임 1장 전 과정(키 검출 → 매트 → 프로파일 → 합성). 인스턴스 버퍼 재사용 · 무할당(첫 호출 제외).</summary>
    public KeyFrameStats ProcessFrame(ReadOnlySpan<byte> fg, ReadOnlySpan<byte> bg, int w, int h, in KeySettings settings, Span<byte> outRgb, Span<byte> alphaOut = default)
    {
        var n = w * h;
        var t0 = Stopwatch.GetTimestamp();
        KeyFrameStats st;
        lock (_gate)
        {
            if (_alpha is null || _alpha.Length < n) { _alpha = new byte[n]; _f1 = new float[n]; _f2 = new float[n]; }
            var alpha = _alpha.AsSpan(0, n);
            var key = settings.AutoKey ? DetectKeyColor(fg, w, h) : settings.Manual;
            var cov = ExtractMatte(fg, w, h, key, settings, alpha, _f1!.AsSpan(0, n), _f2!.AsSpan(0, n));
            var fgP = AnalyzeLighting(fg, alpha, w, h);
            var bgP = AnalyzeLighting(bg, default, w, h);
            Composite(fg, bg, alpha, w, h, key, settings, fgP, bgP, outRgb);
            if (alphaOut.Length >= n) alpha.CopyTo(alphaOut);
            st = new(key, cov, fgP, bgP, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
        }
        Interlocked.Increment(ref _frames);
        LastStats = st;
        return st;
    }

    // ══════════════════════ 프리뷰 ══════════════════════

    /// <summary>전경/배경 t초 프레임 디코드(480폭 · 배경은 cover 크롭) → 캐시 후 프리뷰.</summary>
    public async Task<ChromaPreview?> PreviewAsync(string fgPath, string bgPath, double seconds, CancellationToken ct = default)
    {
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null || !File.Exists(fgPath) || !File.Exists(bgPath)) { LastAction = "⚠ 프리뷰 불가(ffmpeg/파일 확인)"; Raise(); return null; }
        try
        {
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, fgPath, ct).ConfigureAwait(false);
            if (probe is null) return null;
            var w = PreviewWidth;
            var h = Math.Max(16, (int)Math.Round(PreviewWidth * (double)probe.Value.H / probe.Value.W) & ~1);
            var t = probe.Value.Duration > 0 ? Math.Clamp(seconds, 0, Math.Max(0, probe.Value.Duration - 0.05)) : 0;
            var fg = await GrabAsync(ffmpeg, fgPath, t, w, h, false, ct).ConfigureAwait(false);
            var bg = await GrabAsync(ffmpeg, bgPath, 0, w, h, true, ct).ConfigureAwait(false);
            if (fg is null || bg is null) { LastAction = "⚠ 프레임 디코드 실패"; Raise(); return null; }
            return SetPreview(fg, bg, w, h);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex) { Program.Log("Chroma.Preview", ex); LastAction = "⚠ 프리뷰 실패: " + ex.Message; Raise(); return null; }
    }

    /// <summary>프레임 주입 프리뷰(테스트/라이브 프레임).</summary>
    public ChromaPreview SetPreview(byte[] fg, byte[] bg, int w, int h)
    {
        lock (_gate) { _pvFg = fg; _pvBg = bg; _pvW = w; _pvH = h; }
        return RebuildPreview()!;
    }

    /// <summary>라이브 수신 프레임을 전경으로 교체(캐시 배경 크기 불일치 시 중성 회색 배경).</summary>
    public ChromaPreview LiveForeground(byte[] fg, int w, int h)
    {
        byte[]? bg;
        lock (_gate) bg = _pvBg is { } b && _pvW == w && _pvH == h ? b : null;
        if (bg is null) { bg = new byte[w * h * 3]; Array.Fill(bg, (byte)96); }
        return SetPreview(fg, bg, w, h);
    }

    /// <summary>슬라이더 변경 → 캐시 프레임 재합성(없으면 null).</summary>
    public ChromaPreview? RebuildPreview()
    {
        byte[]? fg, bg; int w, h;
        lock (_gate) { fg = _pvFg; bg = _pvBg; w = _pvW; h = _pvH; }
        if (fg is null || bg is null) return null;
        var outRgb = new byte[w * h * 3];
        var alpha = new byte[w * h];
        var st = ProcessFrame(fg, bg, w, h, Settings, outRgb, alpha);
        var matte = new byte[w * h * 3];
        for (int i = 0, j = 0; i < alpha.Length; i++, j += 3) matte[j] = matte[j + 1] = matte[j + 2] = alpha[i];
        var p = new ChromaPreview(w, h, SpatialImageCodec.BmpDataUrl(matte, w, h), SpatialImageCodec.BmpDataUrl(outRgb, w, h), st);
        LastPreview = p;
        LastAction = "🟩 키 " + st.Key.Hex + " · 전경 " + (st.Coverage * 100).ToString("0", CultureInfo.InvariantCulture) + "% · " + st.Milliseconds.ToString("0.0", CultureInfo.InvariantCulture) + "ms";
        Raise();
        return p;
    }

    // ══════════════════════ 영상 합성 ══════════════════════

    public static bool IsImage(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp" or ".tif" or ".tiff";
    }

    public static List<string> BuildBackgroundArgs(string bgPath, int w, int h, double fps)
    {
        var ci = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-v", "error" };
        a.AddRange(IsImage(bgPath) ? new[] { "-loop", "1" } : new[] { "-stream_loop", "-1" });
        a.AddRange(new[] { "-i", bgPath, "-vf", "fps=" + fps.ToString("0.###", ci) + ",scale=" + w.ToString(ci) + ":" + h.ToString(ci) + ":force_original_aspect_ratio=increase,crop=" + w.ToString(ci) + ":" + h.ToString(ci),
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" });
        return a;
    }

    public static List<string> BuildEncodeArgs(string fgPath, int w, int h, double fps, string output)
    {
        var ci = CultureInfo.InvariantCulture;
        return new List<string> { "-hide_banner", "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", w.ToString(ci) + "x" + h.ToString(ci),
            "-r", fps.ToString("0.###", ci), "-i", "pipe:0", "-i", fgPath, "-map", "0:v:0", "-map", "1:a:0?", "-c:v", "libx264", "-crf", "18", "-preset", "medium",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-shortest", "-movflags", "+faststart", output };
    }

    /// <summary>그린스크린 전경 영상 + 가상 배경(이미지/영상 루프) → 키잉·리라이팅 합성 MP4. 실패 시 부분 출력 삭제.</summary>
    public async Task<ChromaVideoResult> KeyVideoAsync(string fgPath, string bgPath, string output, int width = 1280, double fps = 30, CancellationToken ct = default)
    {
        if (!File.Exists(fgPath) || !File.Exists(bgPath)) return Done(new(false, null, 0, "전경/배경 파일이 없습니다."));
        var ffmpeg = _ffmpeg();
        if (ffmpeg is null) return Done(new(false, null, 0, "ffmpeg 실행 파일을 찾을 수 없습니다."));
        if (Interlocked.Exchange(ref _busy, 1) == 1) return new(false, null, 0, "다른 합성이 진행 중입니다.");
        LastAction = "🟩 크로마키 합성 중 · " + Path.GetFileName(fgPath);
        Raise();
        Process? dfg = null, dbg = null, enc = null;
        var ok = false;
        try
        {
            using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
            d.CancelAfter(VideoTimeout);
            var probe = await ShoppableVideoOverlayEngine.ProbeAsync(ffmpeg, fgPath, d.Token).ConfigureAwait(false);
            if (probe is null) return Done(new(false, null, 0, "전경 해상도를 읽지 못했습니다."));
            var w = Math.Clamp(width, 160, 3840) & ~1;
            var h = Math.Max(16, (int)Math.Round(w * (double)probe.Value.H / probe.Value.W) & ~1);
            fps = Math.Clamp(fps, 1, 60);
            var ci = CultureInfo.InvariantCulture;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            dfg = Start(ffmpeg, new List<string> { "-hide_banner", "-v", "error", "-i", fgPath, "-vf", "fps=" + fps.ToString("0.###", ci) + ",scale=" + w.ToString(ci) + ":" + h.ToString(ci),
                "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" }, false, d.Token);
            dbg = Start(ffmpeg, BuildBackgroundArgs(bgPath, w, h, fps), false, d.Token);
            enc = Start(ffmpeg, BuildEncodeArgs(fgPath, w, h, fps, output), true, d.Token);
            var n = w * h * 3;
            var fg = ArrayPool<byte>.Shared.Rent(n);
            var bg = ArrayPool<byte>.Shared.Rent(n);
            var outBuf = ArrayPool<byte>.Shared.Rent(n);
            var frames = 0;
            try
            {
                var settings = Settings;
                var haveBg = false;
                var sink = enc.StandardInput.BaseStream;
                while (frames < MaxFrames && await Spatial3dGaussianSplattingEngine.ReadExactAsync(dfg.StandardOutput.BaseStream, fg, n, d.Token).ConfigureAwait(false))
                {
                    if (await Spatial3dGaussianSplattingEngine.ReadExactAsync(dbg.StandardOutput.BaseStream, bg, n, d.Token).ConfigureAwait(false)) haveBg = true;
                    else if (!haveBg) return Done(new(false, null, 0, "배경 디코드 실패"));
                    ProcessFrame(fg.AsSpan(0, n), bg.AsSpan(0, n), w, h, settings, outBuf.AsSpan(0, n));
                    await sink.WriteAsync(outBuf.AsMemory(0, n), d.Token).ConfigureAwait(false);
                    frames++;
                    if ((frames & 31) == 0) Raise();
                }
                await sink.FlushAsync(d.Token).ConfigureAwait(false);
                sink.Close();
                await enc.WaitForExitAsync(d.Token).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(fg);
                ArrayPool<byte>.Shared.Return(bg);
                ArrayPool<byte>.Shared.Return(outBuf);
            }
            if (frames == 0 || enc.ExitCode != 0 || !File.Exists(output)) return Done(new(false, null, frames, "인코드 실패"));
            ok = true;
            LastAction = "🟩 크로마키 합성 완료 · " + frames.ToString(ci) + "프레임";
            return Done(new(true, output, frames, "합성 완료"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Done(new(false, null, 0, "취소됨")); }
        catch (OperationCanceledException) { return Done(new(false, null, 0, "시간 초과")); }
        catch (Exception ex) { Program.Log("Chroma.KeyVideo", ex); return Done(new(false, null, 0, "합성 실패: " + ex.Message)); }
        finally
        {
            foreach (var p in new[] { dfg, dbg, enc })
            {
                if (p is null) continue;
                try { if (!p.HasExited) p.KillCompat(true); } catch { }
                p.Dispose();
            }
            if (!ok) { try { if (File.Exists(output)) File.Delete(output); } catch (Exception ex) { Program.Log("Chroma.Cleanup", ex); } }
            Volatile.Write(ref _busy, 0);
            Raise();
        }
    }

    private static Process Start(string exe, List<string> args, bool stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = !stdin, RedirectStandardInput = stdin, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = new Process { StartInfo = psi };
        if (!p.StartCompat()) { p.Dispose(); throw new InvalidOperationException("ffmpeg 시작 실패"); }
        _ = p.StandardError.ReadToEndAsync(ct);
        return p;
    }

    private static async Task<byte[]?> GrabAsync(string ffmpeg, string path, double t, int w, int h, bool cover, CancellationToken ct)
    {
        var ci = CultureInfo.InvariantCulture;
        var vf = "scale=" + w.ToString(ci) + ":" + h.ToString(ci) + (cover ? ":force_original_aspect_ratio=increase,crop=" + w.ToString(ci) + ":" + h.ToString(ci) : "");
        using var p = Start(ffmpeg, new List<string> { "-hide_banner", "-v", "error", "-ss", t.ToString("0.###", ci), "-i", path, "-frames:v", "1", "-vf", vf,
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1" }, false, ct);
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        d.CancelAfter(TimeSpan.FromSeconds(30));
        var buf = new byte[w * h * 3];
        try { return await Spatial3dGaussianSplattingEngine.ReadExactAsync(p.StandardOutput.BaseStream, buf, buf.Length, d.Token).ConfigureAwait(false) ? buf : null; }
        finally { try { if (!p.HasExited) p.KillCompat(true); } catch { } }
    }

    private ChromaVideoResult Done(ChromaVideoResult r)
    {
        if (!r.Ok) LastAction = "⚠ 크로마키 " + r.Message;
        Raise();
        return r;
    }

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Chroma.OnChange", ex); }
        }
    }
}
