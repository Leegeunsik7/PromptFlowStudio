using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Chart;

/// <summary>차트 스틸 생성 결과 — 성공 시 PNG 절대 경로.</summary>
public sealed record ChartStillResult(bool Ok, string? Path, string Message);

/// <summary>차트 스틸 옵션 — 1280×720(기본) 또는 1920×1080 · 캔들 수.</summary>
public sealed record ChartStillOptions
{
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public int Candles { get; init; } = 90;
}

/// <summary>
/// 📊 티커 기반 퀀트 차트 스틸 생성기(Pure BCL — 자체 래스터라이저 + PNG 인코더).
/// OLED 딥블랙 배경 · 네온 그린/레드 캔들 · MA20/MA60 · 볼린저 밴드(20, 2σ) · 피보나치 되돌림 · 거래량 바 · 10호가 그리드.
/// 시세는 티커+날짜 시드 결정적 기하 브라운 운동(동일 티커·동일 일자 = 동일 차트) — I2V 시작 프레임 바인딩용.
/// </summary>
public sealed class ChartStillGeneratorService
{
    public const int MinWidth = 320, MinHeight = 180, MaxWidth = 3840, MaxHeight = 2160;
    public const int MaWindowFast = 20, MaWindowSlow = 60, BollingerWindow = 20;
    public const double BollingerK = 2.0;
    public const int OrderBookLevels = 10;
    public static readonly double[] FibLevels = { 0.0, 0.236, 0.382, 0.5, 0.618, 0.786, 1.0 };

    /// <summary>현재 시각 공급자(테스트 주입) — 시드의 일자 성분.</summary>
    public Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    /// <summary>파일명 안전 티커(영숫자·<c>.-_</c> 외 제거 · 대문자 · 최대 16자 · 빈 값 = CHART).</summary>
    public static string SanitizeTicker(string? ticker)
    {
        var sb = new StringBuilder(16);
        if (ticker is not null)
            foreach (var c in ticker.Trim())
            {
                if (sb.Length >= 16) break;
                if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_') sb.Append(char.ToUpperInvariant(c));
            }
        return sb.Length == 0 ? "CHART" : sb.ToString();
    }

    /// <summary>안정 시드(FNV-1a · 프로세스 무관) — 티커 + 일자.</summary>
    public static int StableSeed(string ticker, DateTimeOffset day)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var c in ticker) { h ^= c; h *= 16777619u; }
            h ^= (uint)(day.UtcDateTime.Date.Ticks / TimeSpan.TicksPerDay);
            h *= 16777619u;
            return (int)(h & 0x7FFFFFFF);
        }
    }

    /// <summary>비동기 생성 — <paramref name="outputDirectory"/> 에 <c>chart_{TICKER}_{yyyyMMdd_HHmmss}.png</c> 저장. 예외 없이 결과 반환.</summary>
    public Task<ChartStillResult> GenerateAsync(string? ticker, string outputDirectory, ChartStillOptions? options = null, CancellationToken ct = default)
        => Task.Run(() => Generate(ticker, outputDirectory, options, ct), ct);

    /// <summary>동기 생성(작업 스레드에서 호출).</summary>
    public ChartStillResult Generate(string? ticker, string outputDirectory, ChartStillOptions? options = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory)) return new(false, null, "출력 경로가 비어 있습니다.");
        var o = options ?? new ChartStillOptions();
        var w = Math.Clamp(o.Width, MinWidth, MaxWidth);
        var h = Math.Clamp(o.Height, MinHeight, MaxHeight);
        var n = Math.Clamp(o.Candles, 30, 400);
        var sym = SanitizeTicker(ticker);
        var now = UtcNow();
        string? temp = null;
        try
        {
            var dir = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "chart_" + sym + "_" + now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".png");
            var rgb = Render(sym, w, h, n, StableSeed(sym, now));
            ct.ThrowIfCancellationRequested();
            temp = path + ".~pfs";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                WritePng(fs, rgb, w, h);
            File.Move(temp, path, overwrite: true);
            temp = null;
            return new(true, path, "📊 " + sym + " 차트 스틸 생성 완료 (" + w.ToString(CultureInfo.InvariantCulture) + "×" + h.ToString(CultureInfo.InvariantCulture) + ")");
        }
        catch (OperationCanceledException) { return new(false, null, "취소됨"); }
        catch (Exception ex)
        {
            Program.Log("ChartStill.Generate", ex);
            return new(false, null, "차트 스틸 생성 실패: " + ex.Message);
        }
        finally
        {
            if (temp is not null) try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ══════════════════════ 시세 합성 ══════════════════════

    private static void Synthesize(int seed, int n, double[] open, double[] high, double[] low, double[] close, double[] vol)
    {
        var rnd = new Random(seed);
        var price = 50 + rnd.NextDouble() * 450;
        var drift = (rnd.NextDouble() - 0.45) * 0.004;
        for (var i = 0; i < n; i++)
        {
            var sigma = 0.012 + 0.01 * Math.Abs(Math.Sin(i * 0.07 + seed % 13));
            var z = Gauss(rnd);
            var o = price;
            var c = o * Math.Exp(drift + sigma * z);
            var hi = Math.Max(o, c) * (1 + Math.Abs(Gauss(rnd)) * sigma * 0.5);
            var lo = Math.Min(o, c) * (1 - Math.Abs(Gauss(rnd)) * sigma * 0.5);
            open[i] = o; close[i] = c; high[i] = hi; low[i] = lo;
            vol[i] = (0.4 + rnd.NextDouble()) * (1 + Math.Abs(z) * 1.5);
            price = c;
        }
    }

    private static double Gauss(Random r)
    {
        var u1 = 1.0 - r.NextDouble();
        var u2 = r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static void MovingAverage(double[] src, int n, int window, double[] dst)
    {
        double sum = 0;
        for (var i = 0; i < n; i++)
        {
            sum += src[i];
            if (i >= window) sum -= src[i - window];
            dst[i] = sum / Math.Min(i + 1, window);
        }
    }

    // ══════════════════════ 래스터라이저 ══════════════════════

    private sealed class Canvas
    {
        public readonly int W, H;
        public readonly byte[] Px;
        public Canvas(int w, int h) { W = w; H = h; Px = new byte[w * h * 3]; }

        public void Blend(int x, int y, uint rgb, double a)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H || a <= 0) return;
            var i = (y * W + x) * 3;
            if (a >= 1) { Px[i] = (byte)(rgb >> 16); Px[i + 1] = (byte)(rgb >> 8); Px[i + 2] = (byte)rgb; return; }
            Px[i] = (byte)(Px[i] + ((byte)(rgb >> 16) - Px[i]) * a);
            Px[i + 1] = (byte)(Px[i + 1] + ((byte)(rgb >> 8) - Px[i + 1]) * a);
            Px[i + 2] = (byte)(Px[i + 2] + ((byte)rgb - Px[i + 2]) * a);
        }

        public void Fill(int x0, int y0, int x1, int y1, uint rgb, double a = 1)
        {
            if (x0 > x1) (x0, x1) = (x1, x0);
            if (y0 > y1) (y0, y1) = (y1, y0);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(W - 1, x1); y1 = Math.Min(H - 1, y1);
            for (var y = y0; y <= y1; y++)
                for (var x = x0; x <= x1; x++) Blend(x, y, rgb, a);
        }

        public void Line(double x0, double y0, double x1, double y1, uint rgb, double a, int thick = 1, int dash = 0)
        {
            var dx = x1 - x0; var dy = y1 - y0;
            var steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
            if (steps <= 0) steps = 1;
            var r0 = -(thick - 1) / 2;
            for (var s = 0; s <= steps; s++)
            {
                if (dash > 0 && (s / dash & 1) == 1) continue;
                var px = (int)Math.Round(x0 + dx * s / steps);
                var py = (int)Math.Round(y0 + dy * s / steps);
                for (var oy = 0; oy < thick; oy++)
                    for (var ox = 0; ox < thick; ox++) Blend(px + r0 + ox, py + r0 + oy, rgb, a);
            }
        }

        public void Text(string s, int x, int y, int scale, uint rgb, double a = 1)
        {
            var cx = x;
            foreach (var ch in s)
            {
                var gi = Glyphs.Charset.IndexOf(char.ToUpperInvariant(ch));
                if (gi >= 0)
                    for (var row = 0; row < 7; row++)
                    {
                        var bits = Glyphs.Data[gi * 7 + row];
                        for (var col = 0; col < 5; col++)
                            if ((bits >> (4 - col) & 1) != 0) Fill(cx + col * scale, y + row * scale, cx + col * scale + scale - 1, y + row * scale + scale - 1, rgb, a);
                    }
                cx += 6 * scale;
            }
        }

        public static int TextWidth(string s, int scale) => s.Length * 6 * scale;
    }

    private const uint Grid = 0x161A1F, Up = 0x00FF88, Down = 0xFF3355, MaFast = 0xFFB000, MaSlow = 0x00D0FF,
        Band = 0xB066FF, Fib = 0xFFD54A, Axis = 0x8A93A0, Head = 0xE8EEF5;

    private static string Price(double v) => v.ToString(v >= 1000 ? "0" : "0.00", CultureInfo.InvariantCulture);

    /// <summary>RGB 버퍼 렌더 — 테스트/미리보기 공용.</summary>
    internal static byte[] Render(string sym, int w, int h, int n, int seed)
    {
        var open = new double[n]; var high = new double[n]; var low = new double[n]; var close = new double[n]; var vol = new double[n];
        Synthesize(seed, n, open, high, low, close, vol);
        var ma20 = new double[n]; var ma60 = new double[n];
        MovingAverage(close, n, MaWindowFast, ma20);
        MovingAverage(close, n, MaWindowSlow, ma60);
        var bbU = new double[n]; var bbL = new double[n];
        for (var i = 0; i < n; i++)
        {
            var from = Math.Max(0, i - BollingerWindow + 1);
            double ss = 0;
            for (var k = from; k <= i; k++) { var d = close[k] - ma20[i]; ss += d * d; }
            var sd = Math.Sqrt(ss / (i - from + 1));
            bbU[i] = ma20[i] + BollingerK * sd; bbL[i] = ma20[i] - BollingerK * sd;
        }

        var c = new Canvas(w, h);
        var u = Math.Max(1, (int)Math.Round(h / 720.0));
        var fs = Math.Max(1, h / 360);
        var pad = 16 * u;
        var bookW = (int)(w * 0.18);
        var plotL = pad; var plotR = w - bookW - pad - Canvas.TextWidth("00000.00", fs) - 6 * u;
        var plotT = pad + 7 * fs * 2 + 14 * u; var volB = h - pad;
        var plotB = plotT + (int)((volB - plotT) * 0.76);
        var volT = plotB + 8 * u;

        double pMax = double.MinValue, pMin = double.MaxValue, vMax = 0, hiAll = double.MinValue, loAll = double.MaxValue;
        for (var i = 0; i < n; i++)
        {
            pMax = Math.Max(pMax, Math.Max(high[i], bbU[i])); pMin = Math.Min(pMin, Math.Min(low[i], bbL[i]));
            hiAll = Math.Max(hiAll, high[i]); loAll = Math.Min(loAll, low[i]); vMax = Math.Max(vMax, vol[i]);
        }
        var span = pMax - pMin; pMax += span * 0.04; pMin -= span * 0.04; span = pMax - pMin;
        double Y(double p) => plotT + (pMax - p) / span * (plotB - plotT);
        var slot = (plotR - plotL) / (double)n;
        double X(int i) => plotL + slot * (i + 0.5);

        // 그리드
        for (var g = 0; g <= 6; g++)
        {
            var gy = plotT + (plotB - plotT) * g / 6;
            c.Line(plotL, gy, plotR, gy, Grid, 1);
            c.Text(Price(pMax - span * g / 6), plotR + 6 * u, gy - 3 * fs, fs, Axis, 0.9);
        }
        for (var i = 0; i < n; i += 10) c.Line(X(i), plotT, X(i), volB, Grid, 1);
        c.Line(plotL, volT - 4 * u, plotR, volT - 4 * u, Grid, 1);

        // 볼린저 밴드 채움 + 경계
        for (var x = plotL; x <= plotR; x++)
        {
            var fi = (x - plotL) / slot - 0.5;
            var i0 = Math.Clamp((int)Math.Floor(fi), 0, n - 1); var i1 = Math.Min(n - 1, i0 + 1);
            var t = Math.Clamp(fi - i0, 0, 1);
            var yu = Y(bbU[i0] + (bbU[i1] - bbU[i0]) * t); var yl = Y(bbL[i0] + (bbL[i1] - bbL[i0]) * t);
            c.Fill(x, (int)yu, x, (int)yl, Band, 0.07);
        }
        for (var i = 1; i < n; i++)
        {
            c.Line(X(i - 1), Y(bbU[i - 1]), X(i), Y(bbU[i]), Band, 0.55, u);
            c.Line(X(i - 1), Y(bbL[i - 1]), X(i), Y(bbL[i]), Band, 0.55, u);
        }

        // 피보나치 되돌림
        foreach (var lv in FibLevels)
        {
            var p = hiAll - (hiAll - loAll) * lv;
            var fy = Y(p);
            c.Line(plotL, fy, plotR, fy, Fib, 0.45, 1, 6 * u);
            c.Text((lv * 100).ToString("0.0", CultureInfo.InvariantCulture), plotL + 4 * u, (int)fy - 7 * fs - 2 * u, fs, Fib, 0.75);
        }

        // 캔들 + 거래량
        var body = Math.Max(1, (int)(slot * 0.62));
        for (var i = 0; i < n; i++)
        {
            var col = close[i] >= open[i] ? Up : Down;
            var cx = (int)X(i);
            c.Line(cx, Y(high[i]), cx, Y(low[i]), col, 0.95, Math.Max(1, u));
            int yo = (int)Y(open[i]), yc = (int)Y(close[i]);
            if (Math.Abs(yo - yc) < u) yc = yo + u;
            c.Fill(cx - body / 2, yo, cx - body / 2 + body - 1, yc, col);
            var vh = (int)((volB - volT) * vol[i] / vMax);
            c.Fill(cx - body / 2, volB - vh, cx - body / 2 + body - 1, volB, col, 0.5);
        }

        // 이동평균
        for (var i = 1; i < n; i++)
        {
            c.Line(X(i - 1), Y(ma20[i - 1]), X(i), Y(ma20[i]), MaFast, 0.95, Math.Max(1, 2 * u));
            if (i >= MaWindowSlow / 2) c.Line(X(i - 1), Y(ma60[i - 1]), X(i), Y(ma60[i]), MaSlow, 0.9, Math.Max(1, 2 * u));
        }

        // 현재가 라인
        var last = close[n - 1];
        var lastCol = last >= open[n - 1] ? Up : Down;
        c.Line(plotL, Y(last), plotR, Y(last), lastCol, 0.6, 1, 3 * u);

        // 10호가 그리드
        var bookL = w - bookW; var bookT = plotT; var bookB = volB;
        c.Fill(bookL, bookT, w - pad, bookB, 0x05080B);
        var rows = OrderBookLevels * 2;
        var rowH = (bookB - bookT) / (double)rows;
        var tick = Math.Max(0.01, Math.Round(last * 0.0005, 2));
        var rnd = new Random(seed ^ 0x5F3759DF);
        var depth = new double[rows]; double dMax = 0;
        for (var r = 0; r < rows; r++) { depth[r] = 0.2 + rnd.NextDouble() * (1 + (r == OrderBookLevels - 1 || r == OrderBookLevels ? 0.8 : 0)); dMax = Math.Max(dMax, depth[r]); }
        var bookInnerR = w - pad - 2 * u;
        for (var r = 0; r < rows; r++)
        {
            var ry = bookT + rowH * r;
            var ask = r < OrderBookLevels;
            var lvl = ask ? OrderBookLevels - r : r - OrderBookLevels + 1;
            var p = ask ? last + tick * lvl : last - tick * (lvl - 1);
            var bw = (int)((bookInnerR - bookL) * 0.62 * depth[r] / dMax);
            c.Fill(bookInnerR - bw, (int)ry + u, bookInnerR, (int)(ry + rowH) - u, ask ? Down : Up, 0.28);
            c.Line(bookL, ry, w - pad, ry, Grid, 1);
            c.Text(Price(p), bookL + 4 * u, (int)(ry + rowH / 2) - 7 * fs / 2, fs, ask ? Down : Up, 0.95);
        }
        c.Line(bookL, bookT + rowH * OrderBookLevels, w - pad, bookT + rowH * OrderBookLevels, Head, 0.8, u);

        // 헤더
        var chg = (last / open[0] - 1) * 100;
        var hs = fs * 2;
        c.Text(sym, pad, pad, hs, Head);
        var px = pad + Canvas.TextWidth(sym + " ", hs);
        c.Text(Price(last), px, pad, hs, lastCol);
        px += Canvas.TextWidth(Price(last) + " ", hs);
        c.Text((chg >= 0 ? "+" : "") + chg.ToString("0.00", CultureInfo.InvariantCulture) + "%", px, pad, hs, lastCol);
        var legend = "MA20  MA60  BB20 2  FIB";
        var lx = plotR - Canvas.TextWidth(legend, fs);
        c.Text("MA20", lx, pad + 2 * u, fs, MaFast);
        c.Text("MA60", lx + Canvas.TextWidth("MA20  ", fs), pad + 2 * u, fs, MaSlow);
        c.Text("BB20 2", lx + Canvas.TextWidth("MA20  MA60  ", fs), pad + 2 * u, fs, Band);
        c.Text("FIB", lx + Canvas.TextWidth("MA20  MA60  BB20 2  ", fs), pad + 2 * u, fs, Fib);
        c.Text("ORDER BOOK", bookL + 4 * u, plotT - 7 * fs - 6 * u, fs, Axis);
        return c.Px;
    }

    // ══════════════════════ PNG 인코더 ══════════════════════

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

    internal static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> hdr = stackalloc byte[8];
        BinaryPrimitives.WriteInt32BigEndian(hdr, data.Length);
        for (var i = 0; i < 4; i++) hdr[4 + i] = (byte)type[i];
        s.Write(hdr);
        s.Write(data);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(hdr.Slice(4), data));
        s.Write(crc);
    }

    /// <summary>8-bit RGB(컬러 타입 2) PNG 인코딩 — 필터 0 + zlib(<see cref="ZLibStream"/>).</summary>
    public static void WritePng(Stream s, byte[] rgb, int w, int h)
    {
        ReadOnlySpan<byte> sig = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        s.Write(sig);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.Slice(4), h);
        ihdr[8] = 8; ihdr[9] = 2; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(s, "IHDR", ihdr);
        using var ms = new MemoryStream(rgb.Length / 4 + 1024);
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            var stride = w * 3;
            for (var y = 0; y < h; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * stride, stride);
            }
        }
        WriteChunk(s, "IDAT", ms.GetBuffer().AsSpan(0, (int)ms.Length));
        WriteChunk(s, "IEND", ReadOnlySpan<byte>.Empty);
    }

    // ══════════════════════ 5×7 비트맵 글꼴 ══════════════════════

    private static class Glyphs
    {
        public const string Charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.%-+:/ ";
        public static readonly byte[] Data =
        {
            0x0E,0x11,0x13,0x15,0x19,0x11,0x0E, 0x04,0x0C,0x04,0x04,0x04,0x04,0x0E, 0x0E,0x11,0x01,0x02,0x04,0x08,0x1F, 0x1F,0x02,0x04,0x02,0x01,0x11,0x0E,
            0x02,0x06,0x0A,0x12,0x1F,0x02,0x02, 0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E, 0x06,0x08,0x10,0x1E,0x11,0x11,0x0E, 0x1F,0x01,0x02,0x04,0x08,0x08,0x08,
            0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E, 0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C,
            0x0E,0x11,0x11,0x1F,0x11,0x11,0x11, 0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E, 0x0E,0x11,0x10,0x10,0x10,0x11,0x0E, 0x1C,0x12,0x11,0x11,0x11,0x12,0x1C,
            0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F, 0x1F,0x10,0x10,0x1E,0x10,0x10,0x10, 0x0E,0x11,0x10,0x17,0x11,0x11,0x0F, 0x11,0x11,0x11,0x1F,0x11,0x11,0x11,
            0x0E,0x04,0x04,0x04,0x04,0x04,0x0E, 0x07,0x02,0x02,0x02,0x02,0x12,0x0C, 0x11,0x12,0x14,0x18,0x14,0x12,0x11, 0x10,0x10,0x10,0x10,0x10,0x10,0x1F,
            0x11,0x1B,0x15,0x15,0x11,0x11,0x11, 0x11,0x11,0x19,0x15,0x13,0x11,0x11, 0x0E,0x11,0x11,0x11,0x11,0x11,0x0E, 0x1E,0x11,0x11,0x1E,0x10,0x10,0x10,
            0x0E,0x11,0x11,0x11,0x15,0x12,0x0D, 0x1E,0x11,0x11,0x1E,0x14,0x12,0x11, 0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E, 0x1F,0x04,0x04,0x04,0x04,0x04,0x04,
            0x11,0x11,0x11,0x11,0x11,0x11,0x0E, 0x11,0x11,0x11,0x11,0x11,0x0A,0x04, 0x11,0x11,0x11,0x15,0x15,0x15,0x0A, 0x11,0x11,0x0A,0x04,0x0A,0x11,0x11,
            0x11,0x11,0x11,0x0A,0x04,0x04,0x04, 0x1F,0x01,0x02,0x04,0x08,0x10,0x1F,
            0x00,0x00,0x00,0x00,0x00,0x0C,0x0C, 0x18,0x19,0x02,0x04,0x08,0x13,0x03, 0x00,0x00,0x00,0x1F,0x00,0x00,0x00, 0x00,0x04,0x04,0x1F,0x04,0x04,0x00,
            0x00,0x0C,0x0C,0x00,0x0C,0x0C,0x00, 0x00,0x01,0x02,0x04,0x08,0x10,0x00, 0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        };
    }
}
