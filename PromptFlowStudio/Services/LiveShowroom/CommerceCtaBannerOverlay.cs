using PromptFlowStudio.Services.BroadcastMcr;

namespace PromptFlowStudio.Services.LiveShowroom;

/// <summary>
/// [PromptFlow Studio v17.68.0] 커머스 CTA 모션 배너 — v17.66 MCR PGM RGB24 버스 오버레이(<see cref="IMcrProgramOverlay"/> · 무할당).
///   ▸ 하단 띠(슬라이드 인 0.4 s · 펄스): 할인율 · 할인가 · "BUY NOW" / 타임세일 "SALE mm:ss" · 잔여 "LEFT n" 막대 / 품절 "SOLD OUT"(적색 · 깜빡임).
///   ▸ 가격 정합성 실패(<see cref="PriceBlocked"/>) 시 쇼핑 배너 전체 비노출 — 차단 프레임 집계.
///   ▸ 글리프 = 5×7 비트맵(숫자 · 대문자 · : % - , ₩ 대용 W) — GDI/폰트 비의존(모바일 동일).
/// </summary>
public sealed class CommerceCtaBannerOverlay : IMcrProgramOverlay
{
    private static readonly ushort[] Glyph = BuildFont();
    private readonly char[] _txt = new char[48];
    private long _shownSince = -1;

    public LiveSalesPitchReactionSynthesizer? Sales { get; set; }
    public bool Enabled { get; set; } = true;
    public bool PriceBlocked { get; set; }
    public long DrawnFrames { get; private set; }
    public long BlockedFrames { get; private set; }
    public long SoldOutFrames { get; private set; }
    public int LastBannerTop { get; private set; } = -1;

    public void Apply(Span<byte> rgb, int width, int height, long frame)
    {
        var s = Sales;
        var p = s?.Product;
        var active = Enabled && s is not null && p is not null && s.Phase != SalesPhase.Idle;
        if (!active) { _shownSince = -1; LastBannerTop = -1; return; }
        if (PriceBlocked && !s!.SoldOut) { BlockedFrames++; _shownSince = -1; LastBannerTop = -1; return; }
        if (_shownSince < 0) _shownSince = frame;
        var age = (frame - _shownSince) / 60f;
        var slide = MathF.Min(1f, age / 0.4f); slide = 1f - (1f - slide) * (1f - slide);
        const int Band = 34;
        var top = height - (int)(Band * slide);
        LastBannerTop = top;
        if (s!.SoldOut)
        {
            SoldOutFrames++;
            var on = ((frame / 15) & 1) == 0;
            Fill(rgb, width, height, 0, top, width, height, on ? (byte)200 : (byte)150, 20, 30, 0.88f);
            var n = Put(0, "SOLD OUT");
            DrawText(rgb, width, height, _txt.AsSpan(0, n), (width - n * 12) / 2, top + 6, 2, 255, 255, 255);
            DrawnFrames++;
            return;
        }
        var pulse = 0.82f + 0.12f * MathF.Sin(age * MathF.Tau * 1.2f);
        Fill(rgb, width, height, 0, top, width, height, 20, 24, 40, 0.86f);
        // 좌: 할인율 배지
        Fill(rgb, width, height, 4, top + 4, 62, top + Band - 4, 235, 40, 80, pulse);
        var k = PutInt(0, p!.DisplayedPercent); k = Put(k, "%");
        DrawText(rgb, width, height, _txt.AsSpan(0, k), 8, top + 10, 2, 255, 255, 255);
        // 중: 할인가 · 타이머 · 잔여
        k = Put(0, "W"); k = PutInt(k, p.SalePrice);
        DrawText(rgb, width, height, _txt.AsSpan(0, k), 70, top + 5, 1, 255, 230, 120);
        if (s.Phase is SalesPhase.TimeSale or SalesPhase.LowStock)
        {
            var sec = (int)MathF.Ceiling(s.RemainingSeconds);
            k = Put(0, "SALE "); k = Pad2(k, sec / 60); k = Put(k, ":"); k = Pad2(k, sec % 60);
            DrawText(rgb, width, height, _txt.AsSpan(0, k), 70, top + 15, 1, 255, 255, 255);
        }
        k = Put(0, "LEFT "); k = PutInt(k, s.Stock);
        var low = s.LowStock;
        DrawText(rgb, width, height, _txt.AsSpan(0, k), 70, top + 25, 1, low ? (byte)255 : (byte)180, low ? (byte)80 : (byte)220, low ? (byte)80 : (byte)180);
        var barX0 = 150; var barX1 = 230;
        Fill(rgb, width, height, barX0, top + 27, barX1, top + 31, 60, 60, 70, 1f);
        Fill(rgb, width, height, barX0, top + 27, barX0 + (int)((barX1 - barX0) * s.StockRatio), top + 31, low ? (byte)240 : (byte)80, low ? (byte)60 : (byte)200, 80, 1f);
        // 우: BUY NOW 버튼(펄스)
        Fill(rgb, width, height, width - 84, top + 6, width - 6, top + Band - 6, 40, 200, 110, pulse);
        k = Put(0, "BUY NOW");
        DrawText(rgb, width, height, _txt.AsSpan(0, k), width - 80, top + 13, 1, 10, 30, 20);
        DrawnFrames++;
    }

    private int Put(int n, ReadOnlySpan<char> s) { var k = Math.Min(s.Length, _txt.Length - n); s[..k].CopyTo(_txt.AsSpan(n)); return n + k; }
    private int PutInt(int n, long v) { v.TryFormat(_txt.AsSpan(n), out var w, "N0", System.Globalization.CultureInfo.InvariantCulture); return n + w; }
    private int Pad2(int n, int v) { _txt[n] = (char)('0' + v / 10 % 10); _txt[n + 1] = (char)('0' + v % 10); return n + 2; }

    private static void Fill(Span<byte> rgb, int w, int h, int x0, int y0, int x1, int y1, byte r, byte g, byte b, float a)
    {
        x0 = Math.Clamp(x0, 0, w); x1 = Math.Clamp(x1, 0, w); y0 = Math.Clamp(y0, 0, h); y1 = Math.Clamp(y1, 0, h);
        var ia = (int)(Math.Clamp(a, 0f, 1f) * 256f); var inv = 256 - ia;
        for (var y = y0; y < y1; y++)
        {
            var o = (y * w + x0) * 3;
            for (var x = x0; x < x1; x++, o += 3)
            {
                rgb[o] = (byte)((rgb[o] * inv + r * ia) >> 8); rgb[o + 1] = (byte)((rgb[o + 1] * inv + g * ia) >> 8); rgb[o + 2] = (byte)((rgb[o + 2] * inv + b * ia) >> 8);
            }
        }
    }

    private static void DrawText(Span<byte> rgb, int w, int h, ReadOnlySpan<char> s, int x, int y, int scale, byte r, byte g, byte b)
    {
        foreach (var ch in s)
        {
            var bits = GlyphOf(ch);
            for (var gy = 0; gy < 7; gy++)
                for (var gx = 0; gx < 5; gx++)
                {
                    if ((bits[gy] >> (4 - gx) & 1) == 0) continue;
                    for (var sy = 0; sy < scale; sy++)
                        for (var sx = 0; sx < scale; sx++)
                        {
                            int px = x + gx * scale + sx, py = y + gy * scale + sy;
                            if ((uint)px >= (uint)w || (uint)py >= (uint)h) continue;
                            var o = (py * w + px) * 3; rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b;
                        }
                }
            x += 6 * scale;
        }
    }

    private static ReadOnlySpan<ushort> GlyphOf(char c)
    {
        var i = c switch { >= '0' and <= '9' => c - '0', >= 'A' and <= 'Z' => 10 + c - 'A', ':' => 36, '%' => 37, '-' => 38, ',' => 39, _ => 40 };
        return Glyph.AsSpan(i * 7, 7);
    }

    private static ushort[] BuildFont()
    {
        string[] g =
        {
            "01110 10001 10011 10101 11001 10001 01110", "00100 01100 00100 00100 00100 00100 01110", "01110 10001 00001 00010 00100 01000 11111", "11110 00001 00001 01110 00001 00001 11110",
            "00010 00110 01010 10010 11111 00010 00010", "11111 10000 11110 00001 00001 10001 01110", "00110 01000 10000 11110 10001 10001 01110", "11111 00001 00010 00100 01000 01000 01000",
            "01110 10001 10001 01110 10001 10001 01110", "01110 10001 10001 01111 00001 00010 01100",
            "01110 10001 10001 11111 10001 10001 10001", "11110 10001 10001 11110 10001 10001 11110", "01110 10001 10000 10000 10000 10001 01110", "11110 10001 10001 10001 10001 10001 11110",
            "11111 10000 10000 11110 10000 10000 11111", "11111 10000 10000 11110 10000 10000 10000", "01110 10001 10000 10111 10001 10001 01111", "10001 10001 10001 11111 10001 10001 10001",
            "01110 00100 00100 00100 00100 00100 01110", "00111 00010 00010 00010 00010 10010 01100", "10001 10010 10100 11000 10100 10010 10001", "10000 10000 10000 10000 10000 10000 11111",
            "10001 11011 10101 10101 10001 10001 10001", "10001 10001 11001 10101 10011 10001 10001", "01110 10001 10001 10001 10001 10001 01110", "11110 10001 10001 11110 10000 10000 10000",
            "01110 10001 10001 10001 10101 10010 01101", "11110 10001 10001 11110 10100 10010 10001", "01111 10000 10000 01110 00001 00001 11110", "11111 00100 00100 00100 00100 00100 00100",
            "10001 10001 10001 10001 10001 10001 01110", "10001 10001 10001 10001 10001 01010 00100", "10001 10001 10001 10101 10101 10101 01010", "10001 10001 01010 00100 01010 10001 10001",
            "10001 10001 01010 00100 00100 00100 00100", "11111 00001 00010 00100 01000 10000 11111",
            "00000 01100 01100 00000 01100 01100 00000", "11001 11010 00010 00100 01000 01011 10011", "00000 00000 00000 11111 00000 00000 00000", "00000 00000 00000 00000 00110 00100 01000",
            "00000 00000 00000 00000 00000 00000 00000",
        };
        var f = new ushort[g.Length * 7];
        for (var i = 0; i < g.Length; i++)
        {
            var rows = g[i].Split(' ');
            for (var r = 0; r < 7; r++) f[i * 7 + r] = Convert.ToUInt16(rows[r], 2);
        }
        return f;
    }
}
