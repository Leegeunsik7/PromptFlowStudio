namespace PromptFlowStudio.Services.MotionTypography;

/// <summary>아틀라스 글리프 메트릭(em = 1 정규화 · 셀 좌표).</summary>
public struct SdfGlyph
{
    public int CellX, CellY;
    public float Advance, Left, Top, Width, Height;
}

/// <summary>
/// [PromptFlow Studio v17.52.0] 🔤 SDF(Signed Distance Field) 글리프 아틀라스 — 2048² 단일 채널 · 셀 64 px · em 44 px · 확산 ±8 px.
/// 글리프 생성(<see cref="Ensure"/>)은 캡션 설정 시점의 비핫패스: Windows 는 GDI+ GraphicsPath 로 4× 해상도 이진 마스크를 그린 뒤
/// Felzenszwalb 1-D 거리 변환(내부/외부 2 회)으로 부호 거리를 구해 셀 중심 표본으로 축소한다. 비-Windows(모바일 소스 링크)는 둥근 상자 대체 글리프.
/// 렌더 핫패스는 <see cref="TryGet"/>(Dictionary&lt;int,int&gt; 조회) · <see cref="Sample"/>(쌍선형) 만 사용 — GC 할당 0.
/// 한글/가나/한자는 항상 'Malgun Gothic' 으로, 그 외는 선택 폰트로 그린다.
/// </summary>
public sealed class SdfGlyphAtlas
{
    public const int Size = 2048, Cell = 64, Em = 44, Spread = 8, Cols = Size / Cell, MaxGlyphs = Cols * Cols, Hi = 4;
    private readonly byte[] _tex = new byte[Size * Size];
    private readonly SdfGlyph[] _glyphs = new SdfGlyph[MaxGlyphs];
    private readonly Dictionary<int, int> _map = new(512);
    private int _count;

    public SdfGlyphAtlas(string latinFont = "Malgun Gothic") => LatinFont = latinFont;

    public string LatinFont { get; private set; }
    public int Count => _count;
    public long Misses { get; private set; }
    public bool UsingFallback { get; private set; }
    public ReadOnlySpan<byte> Texture => _tex;
    public ref readonly SdfGlyph Glyph(int i) => ref _glyphs[i];

    /// <summary>폰트 교체 — 아틀라스 초기화(비핫패스).</summary>
    public void SetFont(string latinFont)
    {
        if (latinFont == LatinFont) return;
        LatinFont = latinFont;
        _map.Clear(); _count = 0; Array.Clear(_tex);
    }

    public bool TryGet(int codepoint, out int index) => _map.TryGetValue(codepoint, out index);

    /// <summary>텍스트의 모든 글리프 준비(비핫패스) — 새로 만든 수 반환.</summary>
    public int Ensure(ReadOnlySpan<char> text)
    {
        var made = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) { cp = char.ConvertToUtf32(text[i], text[i + 1]); i++; }
            if (cp is ' ' or '\n' || _map.ContainsKey(cp) || _count >= MaxGlyphs) continue;
            Build(cp);
            made++;
        }
        return made;
    }

    /// <summary>쌍선형 SDF 표본(0~1 · 0.5 = 윤곽선) — 셀 내부 좌표(px).</summary>
    public float Sample(int glyph, float u, float v)
    {
        ref readonly var g = ref _glyphs[glyph];
        u = Math.Clamp(u, 0f, Cell - 1.001f); v = Math.Clamp(v, 0f, Cell - 1.001f);
        int x0 = (int)u, y0 = (int)v;
        float fx = u - x0, fy = v - y0;
        var o = (g.CellY + y0) * Size + g.CellX + x0;
        float a = _tex[o], b = _tex[o + 1], c = _tex[o + Size], d = _tex[o + Size + 1];
        return (a + (b - a) * fx + (c - a + (a - b - c + d) * fx) * fy) * (1f / 255f);
    }

    private static bool IsCjk(int cp) => cp is >= 0xAC00 and <= 0xD7A3 or >= 0x3130 and <= 0x318F or >= 0x3040 and <= 0x30FF or >= 0x4E00 and <= 0x9FFF or >= 0xFF00 and <= 0xFFEF;

    private void Build(int cp)
    {
        var idx = _count++;
        ref var g = ref _glyphs[idx];
        g.CellX = idx % Cols * Cell; g.CellY = idx / Cols * Cell;
        const int hs = Cell * Hi;
        var mask = new bool[hs * hs];
        float adv, left, top, w, h;
        if (!Rasterize(cp, IsCjk(cp) ? "Malgun Gothic" : LatinFont, mask, out adv, out left, out top, out w, out h)) { Fallback(cp, mask, out adv, out left, out top, out w, out h); UsingFallback = true; }
        g.Advance = adv; g.Left = left; g.Top = top; g.Width = w; g.Height = h;
        var inside = new float[hs * hs]; var outside = new float[hs * hs];
        Edt(mask, outside, true, hs); Edt(mask, inside, false, hs);
        for (var y = 0; y < Cell; y++)
            for (var x = 0; x < Cell; x++)
            {
                var hx = x * Hi + Hi / 2; var hy = y * Hi + Hi / 2;
                var k = hy * hs + hx;
                var d = (MathF.Sqrt(outside[k]) - MathF.Sqrt(inside[k])) / Hi; // + 외부 · − 내부(px · 셀 해상도)
                _tex[(g.CellY + y) * Size + g.CellX + x] = (byte)Math.Clamp(128f - d * (127f / Spread), 0f, 255f);
            }
        _map[cp] = idx;
        Misses++;
    }

    /// <summary>제곱 유클리드 거리 변환(Felzenszwalb–Huttenlocher) — toInside 면 내부 픽셀까지, 아니면 외부 픽셀까지의 거리².</summary>
    private static void Edt(bool[] mask, float[] dist, bool toInside, int n)
    {
        const float Inf = 1e20f;
        for (var i = 0; i < dist.Length; i++) dist[i] = mask[i] == toInside ? 0f : Inf;
        var f = new float[n]; var d = new float[n]; var v = new int[n]; var z = new float[n + 1];
        for (var pass = 0; pass < 2; pass++)
            for (var line = 0; line < n; line++)
            {
                for (var i = 0; i < n; i++) f[i] = pass == 0 ? dist[i * n + line] : dist[line * n + i];
                var k = 0; v[0] = 0; z[0] = -Inf; z[1] = Inf;
                for (var q = 1; q < n; q++)
                {
                    var s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]);
                    while (s <= z[k]) { k--; s = (f[q] + q * q - (f[v[k]] + v[k] * v[k])) / (2f * q - 2f * v[k]); }
                    k++; v[k] = q; z[k] = s; z[k + 1] = Inf;
                }
                k = 0;
                for (var q = 0; q < n; q++)
                {
                    while (z[k + 1] < q) k++;
                    var dq = q - v[k];
                    d[q] = dq * dq + f[v[k]];
                }
                for (var i = 0; i < n; i++) { if (pass == 0) dist[i * n + line] = d[i]; else dist[line * n + i] = d[i]; }
            }
    }

    private static bool Rasterize(int cp, string font, bool[] mask, out float adv, out float left, out float top, out float w, out float h)
    {
        adv = left = top = w = h = 0;
#if WINDOWS
        if (!OperatingSystem.IsWindows()) return false;
        const int hs = Cell * Hi;
        try
        {
            var s = char.ConvertFromUtf32(cp);
            using var family = new System.Drawing.FontFamily(font);
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            float em = Em * Hi, ox = Spread * Hi + 4, oy = (Cell - Em) / 2f * Hi;
            path.AddString(s, family, (int)System.Drawing.FontStyle.Bold, em, new System.Drawing.PointF(ox, oy), System.Drawing.StringFormat.GenericTypographic);
            using var bmp = new System.Drawing.Bitmap(hs, hs, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var gr = System.Drawing.Graphics.FromImage(bmp))
            {
                gr.Clear(System.Drawing.Color.Black);
                gr.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
                gr.FillPath(System.Drawing.Brushes.White, path);
            }
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, hs, hs), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int minX = hs, minY = hs, maxX = -1, maxY = -1;
            try
            {
                var row = new byte[data.Stride];
                for (var y = 0; y < hs; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    for (var x = 0; x < hs; x++)
                    {
                        var on = row[x * 4 + 1] > 127;
                        mask[y * hs + x] = on;
                        if (on) { if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
                    }
                }
            }
            finally { bmp.UnlockBits(data); }
            float advPx;
            using (var gr = System.Drawing.Graphics.FromImage(bmp))
            using (var f = new System.Drawing.Font(family, em, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel))
                advPx = gr.MeasureString(s, f, int.MaxValue, System.Drawing.StringFormat.GenericTypographic).Width;
            if (maxX < 0) { adv = MathF.Max(advPx / (Em * Hi), 0.3f); return true; } // 공백류
            left = (minX - ox) / (Em * Hi); top = (minY - oy) / (Em * Hi); w = (maxX - minX + 1f) / (Em * Hi); h = (maxY - minY + 1f) / (Em * Hi);
            adv = MathF.Max(advPx / (Em * Hi), left + w + 0.04f);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or TypeInitializationException or PlatformNotSupportedException) { PromptFlowStudio.Program.Log("Sdf.Rasterize", ex); }
#endif
        return false;
    }

    /// <summary>대체 글리프(둥근 상자 · 폭 0.55 em) — GDI 미가용 플랫폼.</summary>
    private static void Fallback(int cp, bool[] mask, out float adv, out float left, out float top, out float w, out float h)
    {
        const int hs = Cell * Hi;
        float ox = Spread * Hi + 4, oy = (Cell - Em) / 2f * Hi;
        var gw = (cp < 128 ? 0.5f : 0.85f) * Em * Hi; var gh = 0.72f * Em * Hi; var gy = oy + 0.2f * Em * Hi; var r = 0.12f * Em * Hi;
        for (var y = 0; y < hs; y++)
            for (var x = 0; x < hs; x++)
            {
                float dx = MathF.Max(MathF.Max(ox + r - x, x - (ox + gw - r)), 0f), dy = MathF.Max(MathF.Max(gy + r - y, y - (gy + gh - r)), 0f);
                mask[y * hs + x] = dx * dx + dy * dy <= r * r;
            }
        left = 0; top = 0.2f; w = gw / (Em * Hi); h = 0.72f; adv = w + 0.08f;
    }
}
