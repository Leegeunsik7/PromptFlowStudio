using System.Diagnostics;
using PromptFlowStudio.Services.GlobalDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.MotionTypography;

namespace PromptFlowStudio.Services.MatrixSyndication;

/// <summary>
/// [PromptFlow Studio v17.81.0] 🎞️ 채널별 맞춤 실질 변형 렌더러(RGB24 · 1080p · 무할당 핫패스).
///   ▸ 화면비: 9:16(1080×1920 원본 유지) · 1:1(1080×1080) 스마트 블러 패딩 — 원본 중앙 정사각 크롭을 1/8 박스 축소 → 분리형 박스 블러 3 패스 → 55 % 감광 →
///     쌍선형 확대 배경 위에 원본 전체를 높이 맞춤(608×1080) 쌍선형 축소로 중앙 배치.
///   ▸ 브랜딩 범퍼: 채널 강조색 대각 와이프 스팅어(인트로 0.5~1.0 s 걷힘 · 아웃트로 덮음) + 중앙 대형 로고 · 상시 코너 로고 워터마크(정수 α 블렌딩).
///   ▸ 다국어 자막: v17.64 <see cref="RealtimeMultilingualTranslationPhonemeEngine.Translate"/> 로 채널 언어 번역 → <see cref="SdfGlyphAtlas"/> 채움/외곽선 α 스프라이트 사전 굽기(비핫패스) →
///     프레임마다 하단 세이프존에 블렌딩(번-인).
///   ▸ 의도적으로 '사람이 보고 구별되는' 편집 축만 다룬다 — 검출 회피용 미세 픽셀 변조 · 메타데이터 위장 기능 없음.
/// </summary>
public sealed class ChannelVariantMultiFormatRenderer
{
    public const int W = 1080, H = 1920, Sq = 1080, Down = 8, Bg = Sq / Down, LogoPx = 128, SubW = 1000, SubH = 150, TextCap = 192, Slots = CompliantMultiAccountMatrixRouterEngine.MaxChannels;
    public const float FontPx = 58f, OutlineEm = 0.14f, EmTop = -0.12f, EmBottom = 1.12f, MinBumper = 0.5f, MaxBumper = 1.0f;
    private const float ToEm = SdfGlyphAtlas.Spread / 127f * 255f / SdfGlyphAtlas.Em;
    private const float OffX = (SdfGlyphAtlas.Spread * SdfGlyphAtlas.Hi + 4f) / SdfGlyphAtlas.Hi, OffY = (SdfGlyphAtlas.Cell - SdfGlyphAtlas.Em) / 2f;
    private const int Brands = 3;

    private readonly byte[] _bg = new byte[Bg * Bg * 3], _bgTmp = new byte[Bg * Bg * 3];
    private readonly byte[][] _logo = new byte[Brands][];
    private readonly byte[][] _subFill = new byte[Slots][], _subLine = new byte[Slots][];
    private readonly int[] _subW = new int[Slots], _subH = new int[Slots], _textLen = new int[Slots];
    private readonly char[][] _text = new char[Slots][];
    private readonly DubLanguage[] _lang = new DubLanguage[Slots];

    public ChannelVariantMultiFormatRenderer()
    {
        for (var b = 0; b < Brands; b++) { _logo[b] = new byte[LogoPx * LogoPx]; BuildLogo((BrandLogo)b, _logo[b]); }
        for (var s = 0; s < Slots; s++) { _subFill[s] = new byte[SubW * SubH]; _subLine[s] = new byte[SubW * SubH]; _text[s] = new char[TextCap]; }
    }

    public SdfGlyphAtlas Atlas { get; } = new();
    public long Frames { get; private set; }
    public double LastRenderUs { get; private set; }
    public double MaxRenderUs { get; private set; }
    public double AvgRenderUs { get; private set; }
    public long SubtitleBakes { get; private set; }

    public static int OutWidth(VariantAspect a) => W;
    public static int OutHeight(VariantAspect a) => a == VariantAspect.Vertical916 ? H : Sq;
    public static int OutBytes(VariantAspect a) => W * OutHeight(a) * 3;
    public static string LogoName(BrandLogo l) => l switch { BrandLogo.Ring => "링", BrandLogo.Hexagon => "헥사곤", _ => "다이아" };
    public static string LangName(DubLanguage l) => l switch
    {
        DubLanguage.Ko => "한국어", DubLanguage.En => "English", DubLanguage.Ja => "日本語", DubLanguage.Es => "Español",
        DubLanguage.Zh => "中文", DubLanguage.Fr => "Français", DubLanguage.De => "Deutsch", _ => l.ToString(),
    };

    public DubLanguage SubtitleLang(int slot) => _lang[slot];
    public ReadOnlySpan<char> SubtitleText(int slot) => _text[slot].AsSpan(0, _textLen[slot]);
    public int SubtitleWidth(int slot) => _subW[slot];

    // ══════════════════════ 자막 프리셋(비핫패스) ══════════════════════
    /// <summary>슬롯 자막 설정 — 한국어 원문 → 채널 언어 번역(v17.64) → SDF 스프라이트 굽기. 반환 = 번역 문자 수.</summary>
    public int SetSubtitle(int slot, ReadOnlySpan<char> korean, DubLanguage lang)
    {
        var dst = _text[slot].AsSpan();
        int n;
        var li = RealtimeMultilingualTranslationPhonemeEngine.IndexOf(lang);
        if (lang == DubLanguage.Ko || li < 0) { n = Math.Min(korean.Length, dst.Length); korean[..n].CopyTo(dst); }
        else n = RealtimeMultilingualTranslationPhonemeEngine.Translate(li, korean, dst, out _);
        _textLen[slot] = n; _lang[slot] = lang;
        Atlas.Ensure(dst[..n]);
        Bake(slot);
        SubtitleBakes++;
        return n;
    }

    private float Measure(ReadOnlySpan<char> s, float px)
    {
        var w = 0f;
        for (var k = 0; k < s.Length; k++)
        {
            int cp = s[k];
            if (char.IsHighSurrogate(s[k]) && k + 1 < s.Length) { cp = char.ConvertToUtf32(s[k], s[k + 1]); k++; }
            w += Atlas.TryGet(cp, out var gi) ? Atlas.Glyph(gi).Advance * px : 0.5f * px;
        }
        return w;
    }

    private void Bake(int slot)
    {
        var text = SubtitleText(slot);
        var fill = _subFill[slot]; var line = _subLine[slot];
        Array.Clear(fill); Array.Clear(line);
        var px = FontPx;
        var width = Measure(text, px) + 2 * OutlineEm * px + 4;
        if (width > SubW) { px *= SubW / width; width = SubW; }
        var sw = Math.Min(SubW, (int)MathF.Ceiling(width));
        var sh = Math.Min(SubH, (int)MathF.Ceiling((EmBottom - EmTop + 2 * OutlineEm) * px) + 2);
        _subW[slot] = sw; _subH[slot] = sh;
        var pen = OutlineEm * px + 1f; var y = (OutlineEm - EmTop) * px + 1f;
        var aaEm = 0.75f / MathF.Max(px, 1f); var inv = 1f / px;
        for (var k = 0; k < text.Length; k++)
        {
            int cp = text[k];
            if (char.IsHighSurrogate(text[k]) && k + 1 < text.Length) { cp = char.ConvertToUtf32(text[k], text[k + 1]); k++; }
            if (!Atlas.TryGet(cp, out var gi)) { pen += 0.5f * px; continue; }
            ref readonly var gl = ref Atlas.Glyph(gi);
            var pad = OutlineEm + aaEm;
            int x0 = Math.Max(0, (int)MathF.Floor(pen + (gl.Left - pad) * px)), x1 = Math.Min(sw, (int)MathF.Ceiling(pen + (gl.Left + gl.Width + pad) * px));
            int y0 = Math.Max(0, (int)MathF.Floor(y + (gl.Top - pad) * px)), y1 = Math.Min(sh, (int)MathF.Ceiling(y + (gl.Top + gl.Height + pad) * px));
            for (var yy = y0; yy < y1; yy++)
            {
                var v = (yy + 0.5f - y) * inv * SdfGlyphAtlas.Em + OffY;
                for (var xx = x0; xx < x1; xx++)
                {
                    var u = (xx + 0.5f - pen) * inv * SdfGlyphAtlas.Em + OffX;
                    var dEm = (0.5f - Atlas.Sample(gi, u, v)) * ToEm;
                    var oa = Math.Clamp((OutlineEm - dEm) / aaEm * 0.5f + 0.5f, 0f, 1f);
                    if (oa <= 0f) continue;
                    var fa = Math.Clamp(-dEm / aaEm * 0.5f + 0.5f, 0f, 1f);
                    var o = yy * SubW + xx;
                    line[o] = Math.Max(line[o], (byte)(oa * 255f + 0.5f));
                    fill[o] = Math.Max(fill[o], (byte)(fa * 255f + 0.5f));
                }
            }
            pen += gl.Advance * px;
        }
    }

    // ══════════════════════ 로고 마스크(비핫패스) ══════════════════════
    private static void BuildLogo(BrandLogo kind, byte[] mask)
    {
        const float c = (LogoPx - 1) * 0.5f, r = LogoPx * 0.40f, th = LogoPx * 0.075f;
        for (var y = 0; y < LogoPx; y++)
            for (var x = 0; x < LogoPx; x++)
            {
                float dx = x - c, dy = y - c;
                var shape = kind switch
                {
                    BrandLogo.Ring => MathF.Sqrt(dx * dx + dy * dy),
                    BrandLogo.Hexagon => MathF.Max(MathF.Abs(dy) * 0.8660254f + MathF.Abs(dx) * 0.5f, MathF.Abs(dx)) * 1.07f,
                    _ => (MathF.Abs(dx) + MathF.Abs(dy)) * 0.75f,
                };
                var edge = MathF.Abs(shape - r) - th;
                var dot = MathF.Sqrt(dx * dx + dy * dy) - LogoPx * 0.12f;
                var d = MathF.Min(edge, dot);
                mask[y * LogoPx + x] = (byte)(Math.Clamp(0.5f - d, 0f, 1f) * 255f + 0.5f);
            }
    }

    // ══════════════════════ 프레임 렌더(핫패스 · 무할당) ══════════════════════
    /// <summary>원본 9:16 RGB24(1080×1920) → 채널 변형 프레임. t = 클립 내 시각 · dur = 클립 길이(s).</summary>
    public void Render(in ChannelVariantSpec spec, int slot, ReadOnlySpan<byte> src, Span<byte> dst, float t, float dur)
    {
        var t0 = Stopwatch.GetTimestamp();
        var oh = OutHeight(spec.Aspect);
        if (spec.Aspect == VariantAspect.Vertical916) src[..(W * H * 3)].CopyTo(dst);
        else SquarePad(src, dst);
        var ar = (byte)(spec.Accent >> 16); var ag = (byte)(spec.Accent >> 8); var ab = (byte)spec.Accent;
        var subY = (int)(oh * (spec.Aspect == VariantAspect.Vertical916 ? 0.70f : 0.80f));
        BlendSubtitle(slot, dst, oh, subY);
        int lx, ly;
        switch (spec.Logo)
        {
            case BrandLogo.Ring: lx = W - LogoPx - 36; ly = 36; break;
            case BrandLogo.Hexagon: lx = 36; ly = oh - LogoPx - 36; break;
            default: lx = 36; ly = 36; break;
        }
        BlendLogo(spec.Logo, dst, oh, lx, ly, 255, 255, 255, 200);
        var intro = Math.Clamp(spec.IntroSec, MinBumper, MaxBumper); var outro = Math.Clamp(spec.OutroSec, MinBumper, MaxBumper);
        var cover = t < intro ? 1f - t / intro : t > dur - outro ? (t - (dur - outro)) / outro : 0f;
        if (cover > 0f)
        {
            Stinger(dst, oh, Math.Clamp(cover, 0f, 1f), ar, ag, ab);
            if (cover > 0.5f) BlendLogo(spec.Logo, dst, oh, (W - LogoPx) / 2, (oh - LogoPx) / 2, 255, 255, 255, 255);
        }
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        LastRenderUs = us; if (us > MaxRenderUs) MaxRenderUs = us;
        Frames++;
        AvgRenderUs = Frames == 1 ? us : AvgRenderUs * 0.9 + us * 0.1;
    }

    private void SquarePad(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        // 배경: 중앙 정사각(y 420~1500) → 1/8 박스 축소
        var top = (H - Sq) / 2;
        for (var by = 0; by < Bg; by++)
            for (var bx = 0; bx < Bg; bx++)
            {
                int r = 0, g = 0, b = 0;
                for (var yy = 0; yy < Down; yy++)
                {
                    var o = ((top + by * Down + yy) * W + bx * Down) * 3;
                    for (var xx = 0; xx < Down; xx++, o += 3) { r += src[o]; g += src[o + 1]; b += src[o + 2]; }
                }
                var d = (by * Bg + bx) * 3;
                _bg[d] = (byte)(r >> 6); _bg[d + 1] = (byte)(g >> 6); _bg[d + 2] = (byte)(b >> 6);
            }
        for (var pass = 0; pass < 3; pass++) { BoxBlur(_bg, _bgTmp, true); BoxBlur(_bgTmp, _bg, false); }
        // 콘텐츠 띠: 높이 맞춤 608 px 중앙
        const int cw = W * Sq / H, cx0 = (Sq - cw) / 2;
        const int stepBg = (Bg - 1) * 65536 / (Sq - 1), stepSrc = (H - 1) * 65536 / (Sq - 1);
        for (var y = 0; y < Sq; y++)
        {
            var row = y * W * 3;
            int fy = y * stepBg, y0 = fy >> 16, wy = (fy >> 8) & 255, y1 = Math.Min(Bg - 1, y0 + 1);
            for (var x = 0; x < Sq; x++)
            {
                var o = row + x * 3;
                if (x >= cx0 && x < cx0 + cw)
                {
                    int sx = (x - cx0) * stepSrc, sy = y * stepSrc;
                    Bilinear(src, W, H, sx, sy, dst.Slice(o, 3), 256);
                    continue;
                }
                int fx = x * stepBg, x0 = fx >> 16, wx = (fx >> 8) & 255, x1 = Math.Min(Bg - 1, x0 + 1);
                for (var ch = 0; ch < 3; ch++)
                {
                    int a = _bg[(y0 * Bg + x0) * 3 + ch], b = _bg[(y0 * Bg + x1) * 3 + ch], c = _bg[(y1 * Bg + x0) * 3 + ch], d = _bg[(y1 * Bg + x1) * 3 + ch];
                    var top2 = a * 256 + (b - a) * wx; var bot = c * 256 + (d - c) * wx;
                    var v = (top2 * 256 + (bot - top2) * wy) >> 16;
                    dst[o + ch] = (byte)(v * 141 >> 8);
                }
            }
        }
    }

    private static void Bilinear(ReadOnlySpan<byte> src, int w, int h, int fx, int fy, Span<byte> px, int gain)
    {
        int x0 = fx >> 16, y0 = fy >> 16, wx = (fx >> 8) & 255, wy = (fy >> 8) & 255;
        int x1 = Math.Min(w - 1, x0 + 1), y1 = Math.Min(h - 1, y0 + 1);
        int oa = (y0 * w + x0) * 3, ob = (y0 * w + x1) * 3, oc = (y1 * w + x0) * 3, od = (y1 * w + x1) * 3;
        for (var ch = 0; ch < 3; ch++)
        {
            int a = src[oa + ch], b = src[ob + ch], c = src[oc + ch], d = src[od + ch];
            var top = a * 256 + (b - a) * wx; var bot = c * 256 + (d - c) * wx;
            px[ch] = (byte)((((top * 256 + (bot - top) * wy) >> 16) * gain) >> 8);
        }
    }

    /// <summary>반경 2 분리형 박스 블러(Bg × Bg · 가장자리 클램프).</summary>
    private static void BoxBlur(byte[] s, byte[] d, bool horizontal)
    {
        for (var y = 0; y < Bg; y++)
            for (var x = 0; x < Bg; x++)
                for (var ch = 0; ch < 3; ch++)
                {
                    var sum = 0;
                    for (var k = -2; k <= 2; k++)
                    {
                        int xx = horizontal ? Math.Clamp(x + k, 0, Bg - 1) : x, yy = horizontal ? y : Math.Clamp(y + k, 0, Bg - 1);
                        sum += s[(yy * Bg + xx) * 3 + ch];
                    }
                    d[(y * Bg + x) * 3 + ch] = (byte)(sum / 5);
                }
    }

    private void BlendLogo(BrandLogo kind, Span<byte> dst, int oh, int lx, int ly, int r, int g, int b, int opacity)
    {
        var m = _logo[(int)kind];
        for (var y = 0; y < LogoPx; y++)
        {
            var dy = ly + y; if ((uint)dy >= (uint)oh) continue;
            for (var x = 0; x < LogoPx; x++)
            {
                var a = m[y * LogoPx + x] * opacity >> 8; if (a == 0) continue;
                var dx = lx + x; if ((uint)dx >= W) continue;
                var o = (dy * W + dx) * 3;
                dst[o] = (byte)(dst[o] + ((r - dst[o]) * a >> 8));
                dst[o + 1] = (byte)(dst[o + 1] + ((g - dst[o + 1]) * a >> 8));
                dst[o + 2] = (byte)(dst[o + 2] + ((b - dst[o + 2]) * a >> 8));
            }
        }
    }

    private void BlendSubtitle(int slot, Span<byte> dst, int oh, int y0)
    {
        if ((uint)slot >= Slots || _textLen[slot] == 0) return;
        var fill = _subFill[slot]; var line = _subLine[slot];
        int sw = _subW[slot], sh = _subH[slot], x0 = (W - sw) / 2;
        for (var y = 0; y < sh; y++)
        {
            var dy = y0 + y; if ((uint)dy >= (uint)oh) break;
            for (var x = 0; x < sw; x++)
            {
                int la = line[y * SubW + x], fa = fill[y * SubW + x];
                if (la == 0) continue;
                var o = (dy * W + x0 + x) * 3;
                for (var ch = 0; ch < 3; ch++)
                {
                    var v = dst[o + ch] - (dst[o + ch] * la >> 8);
                    dst[o + ch] = (byte)(v + ((255 - v) * fa >> 8));
                }
            }
        }
    }

    /// <summary>대각 와이프 스팅어 — 덮임 비율 cover(0~1) · 강조색 + 24 px 사선 줄무늬 음영.</summary>
    private static void Stinger(Span<byte> dst, int oh, float cover, byte r, byte g, byte b)
    {
        var span = W + oh / 2;
        var edge = (int)(cover * span);
        for (var y = 0; y < oh; y++)
        {
            var lim = Math.Min(W, edge - y / 2);
            if (lim <= 0) continue;
            var o = y * W * 3;
            for (var x = 0; x < lim; x++, o += 3)
            {
                var shade = (((x + y) / 24) & 1) == 0 ? 256 : 220;
                dst[o] = (byte)(r * shade >> 8); dst[o + 1] = (byte)(g * shade >> 8); dst[o + 2] = (byte)(b * shade >> 8);
            }
        }
    }

    /// <summary>프리뷰 썸네일(박스 축소 · 무할당).</summary>
    public static void Thumbnail(ReadOnlySpan<byte> frame, int w, int h, Span<byte> thumb, int tw, int th)
    {
        int sx = w / tw, sy = h / th, n = sx * sy;
        for (var ty = 0; ty < th; ty++)
            for (var tx = 0; tx < tw; tx++)
            {
                int r = 0, g = 0, b = 0;
                for (var yy = 0; yy < sy; yy++)
                {
                    var o = ((ty * sy + yy) * w + tx * sx) * 3;
                    for (var xx = 0; xx < sx; xx++, o += 3) { r += frame[o]; g += frame[o + 1]; b += frame[o + 2]; }
                }
                var d = (ty * tw + tx) * 3;
                thumb[d] = (byte)(r / n); thumb[d + 1] = (byte)(g / n); thumb[d + 2] = (byte)(b / n);
            }
    }
}
