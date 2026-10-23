using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using PromptFlowStudio.Services.MotionTypography;

namespace PromptFlowStudio.Services.VodRepurposing;

public enum CardCanvas : byte { Square, OpenGraph }

/// <summary>
/// [PromptFlow Studio v17.88.0] 🖼️ 바이럴 카드뉴스 &amp; 오픈그래프(OG) 이미지 래스터라이저.
///   ▸ 대표 프레임(외부 RGB 주입 또는 절차적 스튜디오 키프레임) → 커버 핏 쌍선형 샘플 → 어두운 세로 그래디언트 + 비네트 오버레이 →
///     액센트 칩(키커) · 중앙 펀치라인(폭 맞춤 자동 크기 · 공백 우선 줄바꿈 ≤ 4 줄) · 슬라이드 카운터 · 푸터.
///   ▸ 규격: 1080×1080 카드뉴스(5 장 세트) · 1200×630(1.91:1) OG 배너. 텍스트는 v17.52 <see cref="SdfGlyphAtlas"/> + <see cref="SdfText"/>.
///   ▸ <see cref="RenderCard"/>/<see cref="RenderOg"/> 는 고정 RGBA 캔버스에 직접 합성 — 할당 0(글리프 준비 <see cref="Prepare"/> 는 비핫패스).
///   ▸ 출력: 인메모리 PNG(RGBA · Sub 필터 · zlib) 슬롯 6 개(카드 0~4 · OG 5) + 1/3 축소 미리보기 PNG — 파일 I/O 없음, <see cref="OpenPng"/> 로 다운로드 스트림.
/// </summary>
public sealed class ViralCardNewsOgRasterizerEngine : IDisposable
{
    public const int CardW = 1080, CardH = 1080, OgW = 1200, OgH = 630, Slides = 5, OgSlot = Slides, Slots = Slides + 1, PreviewDiv = 3, MaxLines = 4;
    public const int KeyW = 480, KeyH = 270;

    private readonly byte[] _card = new byte[CardW * CardH * 4], _og = new byte[OgW * OgH * 4];
    private readonly byte[] _key = new byte[KeyW * KeyH * 3];
    private readonly byte[] _row = new byte[Math.Max(CardW, OgW) * 4 + 1], _prev = new byte[Math.Max(CardW, OgW) * 4 + 1];
    private readonly byte[] _small = new byte[(CardW / PreviewDiv) * (CardH / PreviewDiv) * 4];
    private readonly MemoryStream[] _png = new MemoryStream[Slots], _preview = new MemoryStream[Slots];
    private readonly int[] _lineStart = new int[MaxLines], _lineLen = new int[MaxLines];
    private readonly SdfGlyphAtlas _atlas;
    private RgbaOverlayLayer? _cardText, _ogText;
    private int _keyW = KeyW, _keyH = KeyH;

    public ViralCardNewsOgRasterizerEngine(SdfGlyphAtlas? atlas = null)
    {
        _atlas = atlas ?? new SdfGlyphAtlas();
        for (var i = 0; i < Slots; i++) { _png[i] = new MemoryStream(); _preview[i] = new MemoryStream(); }
        SynthesizeKeyframe(0);
    }

    public ReadOnlySpan<byte> CardRgba => _card;
    public ReadOnlySpan<byte> OgRgba => _og;
    public SdfGlyphAtlas Atlas => _atlas;
    public long Renders { get; private set; }
    public double LastRenderMs { get; private set; }
    public double LastEncodeMs { get; private set; }
    public int LastLines { get; private set; }
    public float LastFontPx { get; private set; }
    public bool ExternalKeyframe { get; private set; }
    /// <summary>슬롯별 인코딩 완료 여부(바이트 수).</summary>
    public long PngBytes(int slot) => _png[slot].Length;
    public long PreviewBytes(int slot) => _preview[slot].Length;

    // ══════════════════════ 키프레임 ══════════════════════

    /// <summary>외부 대표 프레임(RGB24) 주입 — 480×270 로 축소 보관(최근접).</summary>
    public void SetKeyframe(ReadOnlySpan<byte> rgb, int w, int h)
    {
        if (w <= 0 || h <= 0 || rgb.Length < w * h * 3) return;
        _keyW = KeyW; _keyH = KeyH;
        for (var y = 0; y < KeyH; y++)
        {
            var sy = y * h / KeyH;
            for (var x = 0; x < KeyW; x++)
            {
                var s = (sy * w + x * w / KeyW) * 3; var d = (y * KeyW + x) * 3;
                _key[d] = rgb[s]; _key[d + 1] = rgb[s + 1]; _key[d + 2] = rgb[s + 2];
            }
        }
        ExternalKeyframe = true;
    }

    /// <summary>절차적 스튜디오 키프레임(무할당) — 시드별 색조 · 소프트 조명 블롭 · 무대 바닥선.</summary>
    public void SynthesizeKeyframe(int seed)
    {
        _keyW = KeyW; _keyH = KeyH;
        var hue = (seed * 67 + 200) % 360 / 360f;
        Hsv(hue, 0.55f, 0.55f, out var r0, out var g0, out var b0);
        Hsv((hue + 0.12f) % 1f, 0.7f, 0.95f, out var r1, out var g1, out var b1);
        float bx0 = 0.25f + (seed * 37 % 50) / 100f, by0 = 0.35f, bx1 = 0.8f - (seed * 53 % 40) / 100f, by1 = 0.6f;
        for (var y = 0; y < KeyH; y++)
        {
            var fy = y / (float)KeyH;
            for (var x = 0; x < KeyW; x++)
            {
                var fx = x / (float)KeyW;
                var d0 = (fx - bx0) * (fx - bx0) * 3f + (fy - by0) * (fy - by0) * 5f;
                var d1 = (fx - bx1) * (fx - bx1) * 4f + (fy - by1) * (fy - by1) * 6f;
                var l0 = MathF.Exp(-d0 * 6f); var l1 = MathF.Exp(-d1 * 8f);
                var floor = fy > 0.78f ? 0.25f * (1f - (fy - 0.78f) * 3f) : 0f;
                var baseL = 0.18f + 0.22f * (1f - fy);
                var o = (y * KeyW + x) * 3;
                _key[o] = Px(r0 * baseL + r1 * l1 + l0 * 0.9f + floor);
                _key[o + 1] = Px(g0 * baseL + g1 * l1 + l0 * 0.8f + floor);
                _key[o + 2] = Px(b0 * baseL + b1 * l1 + l0 * 0.7f + floor);
            }
        }
        ExternalKeyframe = false;
    }

    private static byte Px(float v) => (byte)Math.Clamp((int)(v * 255f), 0, 255);

    private static void Hsv(float h, float s, float v, out float r, out float g, out float b)
    {
        var i = (int)(h * 6f) % 6; var f = h * 6f - MathF.Floor(h * 6f);
        float p = v * (1 - s), q = v * (1 - f * s), t = v * (1 - (1 - f) * s);
        (r, g, b) = i switch { 0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t), 3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q) };
    }

    // ══════════════════════ 렌더(무할당 핫패스) ══════════════════════

    /// <summary>텍스트 글리프 준비(비핫패스 · GDI+/대체 글리프 생성).</summary>
    public int Prepare(ReadOnlySpan<char> text) => _atlas.Ensure(text);

    /// <summary>1080² 카드 1 장 합성.</summary>
    public void RenderCard(ReadOnlySpan<char> kicker, ReadOnlySpan<char> punchline, ReadOnlySpan<char> footer, int slide, int total)
    {
        _cardText ??= new RgbaOverlayLayer(CardW, CardH);
        Compose(_card, CardW, CardH, _cardText, kicker, punchline, footer, slide, total, 96f, 54f);
    }

    /// <summary>1200×630 OG 배너 합성.</summary>
    public void RenderOg(ReadOnlySpan<char> kicker, ReadOnlySpan<char> title, ReadOnlySpan<char> footer)
    {
        _ogText ??= new RgbaOverlayLayer(OgW, OgH);
        Compose(_og, OgW, OgH, _ogText, kicker, title, footer, -1, 0, 76f, 40f);
    }

    private void Compose(byte[] dst, int w, int h, RgbaOverlayLayer layer, ReadOnlySpan<char> kicker, ReadOnlySpan<char> punch, ReadOnlySpan<char> footer, int slide, int total, float maxPx, float minPx)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // 1) 커버 핏 쌍선형 + 그래디언트/비네트
        var scale = MathF.Max(w / (float)_keyW, h / (float)_keyH);
        float ox = (_keyW - w / scale) * 0.5f, oy = (_keyH - h / scale) * 0.5f;
        for (var y = 0; y < h; y++)
        {
            var fy = y / (float)h;
            var dark = 0.35f + 0.5f * fy * fy;
            var sy = Math.Clamp(oy + (y + 0.5f) / scale - 0.5f, 0f, _keyH - 1.001f);
            int y0 = (int)sy; var wy = sy - y0;
            for (var x = 0; x < w; x++)
            {
                var sx = Math.Clamp(ox + (x + 0.5f) / scale - 0.5f, 0f, _keyW - 1.001f);
                int x0 = (int)sx; var wx = sx - x0;
                var fx = x / (float)w - 0.5f;
                var a = Math.Min(0.92f, dark + 0.35f * fx * fx * 2f);
                var o = (y * w + x) * 4;
                var k00 = (y0 * _keyW + x0) * 3; var k01 = k00 + 3; var k10 = k00 + _keyW * 3; var k11 = k10 + 3;
                for (var c = 0; c < 3; c++)
                {
                    var top = _key[k00 + c] + (_key[k01 + c] - _key[k00 + c]) * wx;
                    var bot = _key[k10 + c] + (_key[k11 + c] - _key[k10 + c]) * wx;
                    var v = top + (bot - top) * wy;
                    var tint = c == 2 ? 26f : c == 1 ? 12f : 10f;
                    dst[o + c] = (byte)(v * (1f - a) + tint * a);
                }
                dst[o + 3] = 255;
            }
        }

        // 2) 텍스트 레이어
        layer.Begin();
        var margin = w * 0.08f;
        var kpx = h * 0.034f + 12f;
        if (!kicker.IsEmpty)
        {
            var kw = SdfText.MeasureEm(_atlas, kicker) * kpx;
            var chip = new PxRect((int)margin, (int)(h * 0.08f), (int)(kw + kpx * 1.2f), (int)(kpx * 1.7f));
            layer.FillRoundRect(chip, kpx * 0.5f, 1f, 0.3f, 0.43f, 0.95f);
            layer.MarkRect(chip);
            SdfText.Draw(_atlas, layer, kicker, chip.X + kpx * 0.6f, chip.Y + kpx * 0.35f, kpx, 1f, 1f, 1f, int.MaxValue, 0f);
        }
        if (slide >= 0 && total > 0)
        {
            Span<char> ctr = stackalloc char[12];
            var n = 0;
            (slide + 1).TryFormat(ctr, out var a1, default, CultureInfo.InvariantCulture); n += a1; ctr[n++] = ' '; ctr[n++] = '/'; ctr[n++] = ' ';
            total.TryFormat(ctr[n..], out var a2, default, CultureInfo.InvariantCulture); n += a2;
            var cw = SdfText.MeasureEm(_atlas, ctr[..n]) * kpx;
            SdfText.Draw(_atlas, layer, ctr[..n], w - margin - cw, h * 0.08f + kpx * 0.35f, kpx, 0.85f, 0.88f, 0.95f);
        }

        // 펀치라인: 최대 크기부터 ≤ MaxLines 맞춤
        var px = maxPx;
        var lines = 0;
        var maxW = w - margin * 2;
        for (; px >= minPx; px -= 4f)
        {
            lines = Wrap(punch, maxW, px, out var overflow);
            if (!overflow) break;
        }
        if (px < minPx) { px = minPx; lines = Wrap(punch, maxW, px, out _); }
        var lh = px * 1.3f;
        var ty = (h - lines * lh) * 0.5f + (slide < 0 ? -h * 0.02f : h * 0.02f);
        for (var i = 0; i < lines; i++)
        {
            var line = punch.Slice(_lineStart[i], _lineLen[i]);
            var lw = SdfText.MeasureEm(_atlas, line) * px;
            var hl = i == 0 && lines > 1;
            SdfText.Draw(_atlas, layer, line, (w - lw) * 0.5f, ty + i * lh, px, 1f, hl ? 0.86f : 1f, hl ? 0.3f : 1f);
        }
        // 액센트 밑줄
        var bar = new PxRect((int)(w * 0.5f - w * 0.06f), (int)(ty + lines * lh + px * 0.25f), (int)(w * 0.12f), Math.Max(4, (int)(px * 0.08f)));
        layer.FillRoundRect(bar, bar.H * 0.5f, 1f, 0.3f, 0.43f, 1f);
        layer.MarkRect(bar);
        if (!footer.IsEmpty)
        {
            var fpx = kpx * 0.9f;
            var fw = SdfText.MeasureEm(_atlas, footer) * fpx;
            if (fw > maxW) { fpx *= maxW / fw; fw = maxW; }
            SdfText.Draw(_atlas, layer, footer, (w - fw) * 0.5f, h * 0.88f, fpx, 0.82f, 0.85f, 0.92f);
        }
        LastLines = lines; LastFontPx = px;

        // 3) 프리멀티 RGBA over 캔버스
        var p = layer.Pixels;
        var dirty = layer.Dirty;
        for (var y = dirty.Y; y < dirty.Y + dirty.H; y++)
        {
            for (var x = dirty.X; x < dirty.X + dirty.W; x++)
            {
                var i = (y * w + x) * 4;
                var al = p[i + 3];
                if (al == 0) continue;
                var ia = 255 - al;
                dst[i] = (byte)(dst[i] * ia / 255 + p[i]); dst[i + 1] = (byte)(dst[i + 1] * ia / 255 + p[i + 1]); dst[i + 2] = (byte)(dst[i + 2] * ia / 255 + p[i + 2]);
            }
        }
        Renders++;
        LastRenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private float Adv(char c) => c == ' ' ? RealtimeWordByWordMotionTypographyEngine.SpaceEm : _atlas.TryGet(c, out var gi) ? _atlas.Glyph(gi).Advance : 0.5f;

    /// <summary>공백 우선 탐욕 줄바꿈(공백 없는 긴 어절은 글자 단위) — 줄 수 · 넘침 여부.</summary>
    private int Wrap(ReadOnlySpan<char> s, float maxW, float px, out bool overflow)
    {
        overflow = false;
        var lines = 0; var pos = 0;
        while (pos < s.Length)
        {
            while (pos < s.Length && s[pos] == ' ') pos++;
            if (pos >= s.Length) break;
            if (lines == MaxLines) { overflow = true; break; }
            var start = pos; var lastSpace = -1; var width = 0f; var end = s.Length;
            for (var i = pos; i < s.Length; i++)
            {
                if (s[i] == '\n') { end = i; break; }
                if (s[i] == ' ') lastSpace = i;
                width += Adv(s[i]) * px;
                if (width > maxW && i > start) { end = lastSpace > start ? lastSpace : i; break; }
            }
            _lineStart[lines] = start; _lineLen[lines] = end - start; lines++;
            pos = end < s.Length && s[end] == '\n' ? end + 1 : end;
        }
        return lines;
    }

    // ══════════════════════ PNG 인코딩(비핫패스 · 인메모리) ══════════════════════

    /// <summary>현재 캔버스 → 슬롯 PNG + 1/3 미리보기 PNG. 슬롯 &lt; 5 = 카드 · 5 = OG.</summary>
    public long Encode(int slot)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var og = slot == OgSlot;
        byte[] src = og ? _og : _card; int w = og ? OgW : CardW, h = og ? OgH : CardH;
        var full = _png[slot]; full.SetLength(0);
        WritePng(full, src, w, h);
        int sw = w / PreviewDiv, sh = h / PreviewDiv;
        for (var y = 0; y < sh; y++)
            for (var x = 0; x < sw; x++)
            {
                int r = 0, g = 0, b = 0;
                for (var j = 0; j < PreviewDiv; j++)
                    for (var i = 0; i < PreviewDiv; i++) { var o = ((y * PreviewDiv + j) * w + x * PreviewDiv + i) * 4; r += src[o]; g += src[o + 1]; b += src[o + 2]; }
                var d = (y * sw + x) * 4; const int Area = PreviewDiv * PreviewDiv;
                _small[d] = (byte)(r / Area); _small[d + 1] = (byte)(g / Area); _small[d + 2] = (byte)(b / Area); _small[d + 3] = 255;
            }
        var pv = _preview[slot]; pv.SetLength(0);
        WritePng(pv, _small, sw, sh);
        LastEncodeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return full.Length;
    }

    /// <summary>슬롯 PNG 읽기 전용 다운로드 스트림(버퍼 공유 · 복사 없음).</summary>
    public Stream OpenPng(int slot, bool preview = false)
    {
        var ms = preview ? _preview[slot] : _png[slot];
        return new MemoryStream(ms.GetBuffer(), 0, (int)ms.Length, false);
    }

    public ReadOnlySpan<byte> Png(int slot, bool preview = false)
    {
        var ms = preview ? _preview[slot] : _png[slot];
        return ms.GetBuffer().AsSpan(0, (int)ms.Length);
    }

    private static readonly uint[] Crc = BuildCrc();
    private static uint[] BuildCrc() { var t = new uint[256]; for (uint n = 0; n < 256; n++) { var c = n; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[n] = c; } return t; }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data) c = Crc[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    /// <summary>RGBA8 PNG(Sub/Up 행 적응 필터 · zlib Fastest).</summary>
    private void WritePng(MemoryStream dst, byte[] rgba, int w, int h)
    {
        ReadOnlySpan<byte> sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        dst.Write(sig);
        Span<byte> hdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(hdr, w); BinaryPrimitives.WriteInt32BigEndian(hdr[4..], h);
        hdr[8] = 8; hdr[9] = 6; hdr[10] = 0; hdr[11] = 0; hdr[12] = 0;
        Chunk(dst, "IHDR"u8, hdr);
        // IDAT 은 길이 선행 → 본문 위치 예약 후 압축 기록, 길이/CRC 사후 기입
        var lenPos = dst.Position;
        Span<byte> b4 = stackalloc byte[4];
        dst.Write(b4); dst.Write("IDAT"u8);
        var dataPos = dst.Position;
        var stride = w * 4;
        using (var z = new ZLibStream(dst, CompressionLevel.Fastest, true))
        {
            for (var y = 0; y < h; y++)
            {
                var s = rgba.AsSpan(y * stride, stride);
                // Sub vs Up: 절대합 작은 쪽
                long sumSub = 0, sumUp = 0;
                for (var i = 0; i < stride; i++)
                {
                    var sub = (byte)(s[i] - (i >= 4 ? s[i - 4] : 0)); var up = (byte)(s[i] - (y > 0 ? rgba[(y - 1) * stride + i] : 0));
                    _row[i + 1] = sub; _prev[i + 1] = up;
                    sumSub += sub < 128 ? sub : 256 - sub; sumUp += up < 128 ? up : 256 - up;
                }
                var useUp = y > 0 && sumUp < sumSub;
                var line = useUp ? _prev : _row;
                line[0] = useUp ? (byte)2 : (byte)1;
                z.Write(line, 0, stride + 1);
            }
        }
        var end = dst.Position;
        var dataLen = (int)(end - dataPos);
        var buf = dst.GetBuffer();
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan((int)lenPos), dataLen);
        BinaryPrimitives.WriteUInt32BigEndian(b4, Crc32("IDAT"u8, buf.AsSpan((int)dataPos, dataLen)));
        dst.Write(b4);
        Chunk(dst, "IEND"u8, default);
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, data.Length); s.Write(b); s.Write(type); s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(b, Crc32(type, data)); s.Write(b);
    }

    public void Dispose()
    {
        for (var i = 0; i < Slots; i++) { _png[i].Dispose(); _preview[i].Dispose(); }
    }
}
