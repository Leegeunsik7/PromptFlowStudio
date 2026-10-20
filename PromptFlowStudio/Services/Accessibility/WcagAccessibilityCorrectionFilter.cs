using System.Globalization;
using System.Text;
using PromptFlowStudio.Services.Newsroom;

namespace PromptFlowStudio.Services.Accessibility;

public enum ColorVisionDeficiency : byte { None, Protanopia, Deuteranopia, Tritanopia }

/// <summary>인지 친화 자막 스타일(난독증 폰트 · 자간/행간 · 딥 블랙 배경 · 줄 길이/읽기 속도 상한).</summary>
public sealed record CognitiveSubtitleStyle(string FontFamily = "OpenDyslexic", string FallbackFamily = "Verdana", int SizePx = 34, float LetterSpacingEm = 0.12f,
    float LineHeight = 1.5f, uint Foreground = 0xFFF5B8, uint Background = 0x000000, int BackgroundAlpha = 255, int MaxCharsPerLine = 32, int MaxLines = 2, float MaxCps = 14f);

/// <summary>자막 가독성 위반(큐 · 사유).</summary>
public readonly record struct ReadabilityIssue(int Cue, string Reason);

/// <summary>
/// [PromptFlow Studio v16.47.0] 🎨 WCAG 색약 보정 &amp; 인지 친화 자막 렌더러.
///   ▸ 색각 시뮬레이션(Viénot/Brettel LMS 투영 · 1·2·3형 색각 · 심도 0~1) · Daltonization 보정(손실 오차 → 식별 가능 채널 재분배) — 선형 RGB(sRGB 감마 LUT) 3×3 행렬 합성 1회 · 픽셀 경로 무할당.
///   ▸ 혼동 분석: 정상 시각 대비 시뮬레이션 후 색차 붕괴 비율(샘플 쌍) → 자동 보정 판단 · WCAG 2.x 상대 휘도/명암비 · AA(4.5)/AAA(7) 자동 대비 확보.
///   ▸ 인지 친화 자막: OpenDyslexic(미설치 시 폴백) · 자간/행간 · 딥 블랙 배경 · 단어 경계 줄바꿈 · 줄/길이 상한 분할 · 초당 글자(CPS) 검사 · 마스크 캐시 후 합성 무할당.
///   ▸ 내보내기: 보정 3D LUT(.cube 17³ · NLE/OBS 적용) · 스타일 포함 WebVTT.
/// </summary>
public sealed class WcagAccessibilityCorrectionFilter
{
    public const float AaRatio = 4.5f, AaaRatio = 7f;

    private static readonly float[] ToLinear = BuildToLinear();
    private static readonly byte[] ToSrgb = BuildToSrgb();

    private readonly object _gate = new();
    private readonly float[] _m = new float[9];
    private readonly Dictionary<string, TextMask> _masks = new(StringComparer.Ordinal);
    private ColorVisionDeficiency _mode;
    private float _severity = 1f;
    private bool _simulate;
    private long _pixels, _subtitleFrames;

    public WcagAccessibilityCorrectionFilter() => Configure(ColorVisionDeficiency.None, 1f, false);

    public event Action? OnChange;

    public ColorVisionDeficiency Mode => _mode;
    public float Severity => _severity;
    public bool SimulateOnly => _simulate;
    public CognitiveSubtitleStyle Style { get; set; } = new();
    public long PixelsProcessed => Interlocked.Read(ref _pixels);
    public long SubtitleFrames => Interlocked.Read(ref _subtitleFrames);
    public string ResolvedFont { get; private set; } = "";
    public string? LastAction { get; private set; }

    // ══════════════════════ 감마 LUT ══════════════════════

    private static float[] BuildToLinear()
    {
        var t = new float[256];
        for (var i = 0; i < 256; i++) { var c = i / 255.0; t[i] = (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4)); }
        return t;
    }

    private static byte[] BuildToSrgb()
    {
        var t = new byte[4096];
        for (var i = 0; i < 4096; i++) { var c = i / 4095.0; var s = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055; t[i] = (byte)Math.Clamp(Math.Round(s * 255), 0, 255); }
        return t;
    }

    public static float SrgbToLinear(byte v) => ToLinear[v];

    public static byte LinearToSrgb(float v) => ToSrgb[(int)(Math.Clamp(v, 0f, 1f) * 4095f + 0.5f)];

    // ══════════════════════ 행렬 ══════════════════════

    private static readonly float[] RgbToLms = { 17.8824f, 43.5161f, 4.11935f, 3.45565f, 27.1554f, 3.86714f, 0.0299566f, 0.184309f, 1.46709f };
    private static readonly float[] LmsToRgb = { 0.0809444479f, -0.130504409f, 0.116721066f, -0.0102485335f, 0.0540193266f, -0.113614708f, -0.000365296938f, -0.00412161469f, 0.693511405f };

    private static void Mul(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> o)
    {
        Span<float> t = stackalloc float[9];
        for (var r = 0; r < 3; r++)
            for (var c = 0; c < 3; c++)
                t[r * 3 + c] = a[r * 3] * b[c] + a[r * 3 + 1] * b[3 + c] + a[r * 3 + 2] * b[6 + c];
        t.CopyTo(o);
    }

    /// <summary>색각 시뮬레이션 행렬(선형 RGB · 심도 보간).</summary>
    public static void SimulationMatrix(ColorVisionDeficiency d, float severity, Span<float> m)
    {
        if (d == ColorVisionDeficiency.None || severity <= 0) { m.Clear(); m[0] = m[4] = m[8] = 1; return; }
        Span<float> lms = stackalloc float[9];
        lms.Clear(); lms[0] = lms[4] = lms[8] = 1;
        switch (d)
        {
            case ColorVisionDeficiency.Protanopia: lms[0] = 0; lms[1] = 2.02344f; lms[2] = -2.52581f; break;
            case ColorVisionDeficiency.Deuteranopia: lms[4] = 0; lms[3] = 0.494207f; lms[5] = 1.24827f; break;
            case ColorVisionDeficiency.Tritanopia: lms[8] = 0; lms[6] = -0.395913f; lms[7] = 0.801109f; break;
        }
        Mul(lms, RgbToLms, m);
        Mul(LmsToRgb, m, m);
        var s = Math.Clamp(severity, 0f, 1f);
        for (var i = 0; i < 9; i++) m[i] = (i % 4 == 0 ? 1 : 0) * (1 - s) + m[i] * s;
    }

    /// <summary>Daltonization 보정 행렬 = I + Shift·(I − Sim).</summary>
    public static void CorrectionMatrix(ColorVisionDeficiency d, float severity, Span<float> m)
    {
        if (d == ColorVisionDeficiency.None) { m.Clear(); m[0] = m[4] = m[8] = 1; return; }
        Span<float> sim = stackalloc float[9];
        SimulationMatrix(d, severity, sim);
        Span<float> err = stackalloc float[9];
        for (var i = 0; i < 9; i++) err[i] = (i % 4 == 0 ? 1 : 0) - sim[i];
        Span<float> shift = stackalloc float[9];
        shift.Clear();
        if (d == ColorVisionDeficiency.Tritanopia) { shift[0] = 1; shift[2] = 0.7f; shift[4] = 1; shift[5] = 0.7f; }
        else { shift[3] = 0.7f; shift[4] = 1; shift[6] = 0.7f; shift[8] = 1; }
        Mul(shift, err, m);
        m[0] += 1; m[4] += 1; m[8] += 1;
    }

    public void Configure(ColorVisionDeficiency d, float severity, bool simulateOnly)
    {
        lock (_gate)
        {
            _mode = d; _severity = Math.Clamp(severity, 0, 1); _simulate = simulateOnly;
            if (simulateOnly) SimulationMatrix(d, _severity, _m); else CorrectionMatrix(d, _severity, _m);
        }
        LastAction = (simulateOnly ? "👁 색각 시뮬레이션 · " : "🎨 색약 보정 · ") + Label(d);
        Raise();
    }

    // ══════════════════════ 픽셀 경로(무할당) ══════════════════════

    /// <summary>RGBA(stride 4) 제자리 적용 — 현재 행렬.</summary>
    public void Apply(Span<byte> rgba)
    {
        Span<float> m = stackalloc float[9];
        lock (_gate) _m.CopyTo(m);
        ApplyMatrix(rgba, 4, m);
        Interlocked.Add(ref _pixels, rgba.Length / 4);
    }

    /// <summary>임의 행렬 적용(bpp 3 = rgb24 · 4 = RGBA).</summary>
    public static void ApplyMatrix(Span<byte> px, int bpp, ReadOnlySpan<float> m)
    {
        float m0 = m[0], m1 = m[1], m2 = m[2], m3 = m[3], m4 = m[4], m5 = m[5], m6 = m[6], m7 = m[7], m8 = m[8];
        var lin = ToLinear; var srgb = ToSrgb;
        for (var i = 0; i + 2 < px.Length; i += bpp)
        {
            float r = lin[px[i]], g = lin[px[i + 1]], b = lin[px[i + 2]];
            var nr = m0 * r + m1 * g + m2 * b; var ng = m3 * r + m4 * g + m5 * b; var nb = m6 * r + m7 * g + m8 * b;
            px[i] = srgb[(int)(Math.Clamp(nr, 0f, 1f) * 4095f + 0.5f)];
            px[i + 1] = srgb[(int)(Math.Clamp(ng, 0f, 1f) * 4095f + 0.5f)];
            px[i + 2] = srgb[(int)(Math.Clamp(nb, 0f, 1f) * 4095f + 0.5f)];
        }
    }

    public static uint Transform(uint rgb, ReadOnlySpan<float> m)
    {
        Span<byte> p = stackalloc byte[3] { (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb };
        ApplyMatrix(p, 3, m);
        return (uint)(p[0] << 16 | p[1] << 8 | p[2]);
    }

    // ══════════════════════ 혼동 분석 · WCAG ══════════════════════

    /// <summary>CIE76 근사 색차(선형 → 간이 Lab).</summary>
    public static float DeltaE(uint a, uint b)
    {
        Lab(a, out var l1, out var a1, out var b1); Lab(b, out var l2, out var a2, out var b2);
        return MathF.Sqrt((l1 - l2) * (l1 - l2) + (a1 - a2) * (a1 - a2) + (b1 - b2) * (b1 - b2));
    }

    private static void Lab(uint c, out float L, out float A, out float B)
    {
        float r = ToLinear[(byte)(c >> 16)], g = ToLinear[(byte)(c >> 8)], b = ToLinear[(byte)c];
        var x = (0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.95047f; var y = 0.2126f * r + 0.7152f * g + 0.0722f * b; var z = (0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.08883f;
        static float F(float t) => t > 0.008856f ? MathF.Cbrt(t) : 7.787f * t + 16f / 116f;
        float fx = F(x), fy = F(y), fz = F(z);
        L = 116 * fy - 16; A = 500 * (fx - fy); B = 200 * (fy - fz);
    }

    /// <summary>혼동 비율: 정상 색차 ≥ 20 인 샘플 쌍 중 시뮬레이션 후 색차 &lt; 10 으로 붕괴한 비율(0~1). 무할당.</summary>
    public static float ConfusionRatio(ReadOnlySpan<byte> rgb, int w, int h, ColorVisionDeficiency d, int samples = 24)
    {
        if (d == ColorVisionDeficiency.None) return 0;
        Span<float> sim = stackalloc float[9];
        SimulationMatrix(d, 1, sim);
        Span<uint> c = stackalloc uint[Math.Min(64, samples * samples)];
        var n = 0;
        var sx = Math.Max(1, w / samples); var sy = Math.Max(1, h / samples);
        for (var y = sy / 2; y < h && n < c.Length; y += sy)
            for (var x = sx / 2; x < w && n < c.Length; x += sx)
            {
                var o = (y * w + x) * 3;
                c[n++] = (uint)(rgb[o] << 16 | rgb[o + 1] << 8 | rgb[o + 2]);
            }
        int distinct = 0, collapsed = 0;
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
            {
                if (DeltaE(c[i], c[j]) < 20) continue;
                distinct++;
                if (DeltaE(Transform(c[i], sim), Transform(c[j], sim)) < 10) collapsed++;
            }
        return distinct == 0 ? 0 : collapsed / (float)distinct;
    }

    public static float RelativeLuminance(uint rgb) => 0.2126f * ToLinear[(byte)(rgb >> 16)] + 0.7152f * ToLinear[(byte)(rgb >> 8)] + 0.0722f * ToLinear[(byte)rgb];

    public static float ContrastRatio(uint a, uint b)
    {
        float la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05f) / (Math.Min(la, lb) + 0.05f);
    }

    /// <summary>전경색을 흰/검 방향으로 이동해 목표 명암비 확보(색상 유지 · 최소 변경).</summary>
    public static uint EnsureContrast(uint fg, uint bg, float target)
    {
        if (ContrastRatio(fg, bg) >= target) return fg;
        var towardWhite = RelativeLuminance(bg) < 0.4f;
        for (var k = 1; k <= 20; k++)
        {
            var t = k / 20f;
            var r = Mix((byte)(fg >> 16), towardWhite, t); var g = Mix((byte)(fg >> 8), towardWhite, t); var b = Mix((byte)fg, towardWhite, t);
            var c = (uint)(r << 16 | g << 8 | b);
            if (ContrastRatio(c, bg) >= target) return c;
        }
        return towardWhite ? 0xFFFFFFu : 0u;
    }

    private static int Mix(byte v, bool white, float t) => (int)Math.Round(white ? v + (255 - v) * t : v * (1 - t));

    // ══════════════════════ 인지 친화 자막 ══════════════════════

    /// <summary>단어 경계 줄바꿈(긴 단어는 강제 분할).</summary>
    public static List<string> WrapLines(string text, int maxChars)
    {
        var lines = new List<string>();
        var sb = new StringBuilder();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = word;
            while (w.Length > maxChars) { if (sb.Length > 0) { lines.Add(sb.ToString()); sb.Clear(); } lines.Add(w[..maxChars]); w = w[maxChars..]; }
            if (sb.Length > 0 && sb.Length + 1 + w.Length > maxChars) { lines.Add(sb.ToString()); sb.Clear(); }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(w);
        }
        if (sb.Length > 0) lines.Add(sb.ToString());
        return lines;
    }

    /// <summary>큐 → 인지 친화 큐(줄 상한 초과 시 글자 비율로 시간 분할 · 줄바꿈 삽입).</summary>
    public static List<A11yCue> CognitiveCues(IReadOnlyList<A11yCue> cues, CognitiveSubtitleStyle st)
    {
        var res = new List<A11yCue>();
        foreach (var c in cues)
        {
            var lines = WrapLines(c.Text, st.MaxCharsPerLine);
            if (lines.Count == 0) continue;
            var chunks = (lines.Count + st.MaxLines - 1) / st.MaxLines;
            var total = 0; foreach (var l in lines) total += l.Length;
            var t = c.Start;
            for (var k = 0; k < chunks; k++)
            {
                var part = lines.GetRange(k * st.MaxLines, Math.Min(st.MaxLines, lines.Count - k * st.MaxLines));
                var len = 0; foreach (var l in part) len += l.Length;
                var d = (c.End - c.Start) * len / Math.Max(1, total);
                var end = k == chunks - 1 ? c.End : t + d;
                res.Add(new A11yCue(t, end, string.Join("\n", part)));
                t = end;
            }
        }
        return res;
    }

    /// <summary>가독성 검사(줄 길이 · 줄 수 · CPS · 전경/배경 명암비 AAA).</summary>
    public static List<ReadabilityIssue> CheckReadability(IReadOnlyList<A11yCue> cues, CognitiveSubtitleStyle st)
    {
        var list = new List<ReadabilityIssue>();
        if (ContrastRatio(st.Foreground, st.Background) < AaaRatio) list.Add(new ReadabilityIssue(-1, "명암비 AAA 미달"));
        for (var i = 0; i < cues.Count; i++)
        {
            var lines = cues[i].Text.Split('\n');
            if (lines.Length > st.MaxLines) list.Add(new ReadabilityIssue(i, "줄 수 초과"));
            var chars = 0;
            foreach (var l in lines) { if (l.Length > st.MaxCharsPerLine) list.Add(new ReadabilityIssue(i, "줄 길이 초과")); chars += l.Length; }
            var dur = cues[i].End - cues[i].Start;
            if (dur <= 0 || chars / dur > st.MaxCps) list.Add(new ReadabilityIssue(i, "읽기 속도 초과"));
        }
        return list;
    }

    /// <summary>난독증 폰트 설치 확인(미설치 → 폴백).</summary>
    public string ResolveFont()
    {
        var f = Style.FontFamily;
#if WINDOWS
        try
        {
            using var col = new System.Drawing.Text.InstalledFontCollection();
            var found = false; var fb = false;
            foreach (var fam in col.Families) { if (string.Equals(fam.Name, f, StringComparison.OrdinalIgnoreCase)) found = true; if (string.Equals(fam.Name, Style.FallbackFamily, StringComparison.OrdinalIgnoreCase)) fb = true; }
            ResolvedFont = found ? f : fb ? Style.FallbackFamily : "Malgun Gothic";
            return ResolvedFont;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException) { PromptFlowStudio.Program.Log("Wcag.Font", ex); }
#endif
        ResolvedFont = Style.FallbackFamily;
        return ResolvedFont;
    }

    /// <summary>자간 반영 래스터(Windows GDI · 문자별 배치) — 그 외 블록 폴백.</summary>
    public TextMask RasterizeSpaced(string text, int px, float letterSpacingEm)
    {
        if (ResolvedFont.Length == 0) ResolveFont();
        if (text.Length == 0) return new TextMask { Width = 1, Height = px, Alpha = new byte[px] };
#if WINDOWS
        try
        {
            using var font = new System.Drawing.Font(ResolvedFont, px * 0.75f, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            var fmt = System.Drawing.StringFormat.GenericTypographic;
            var spacing = px * letterSpacingEm;
            Span<float> adv = stackalloc float[Math.Min(text.Length, 512)];
            float total = 0, hgt = px;
            using (var probe = new System.Drawing.Bitmap(1, 1))
            using (var g = System.Drawing.Graphics.FromImage(probe))
                for (var i = 0; i < adv.Length; i++)
                {
                    var s = g.MeasureString(text[i] == ' ' ? " " : text[i].ToString(), font, int.MaxValue, fmt);
                    adv[i] = s.Width + spacing; total += adv[i]; hgt = Math.Max(hgt, s.Height);
                }
            int w = Math.Clamp((int)Math.Ceiling(total) + 4, 1, 8192), h = Math.Clamp((int)Math.Ceiling(hgt) + 2, 1, 512);
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                float x = 1;
                for (var i = 0; i < adv.Length; i++) { g.DrawString(text[i].ToString(), font, System.Drawing.Brushes.White, x, 1, fmt); x += adv[i]; }
            }
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[data.Stride]; var alpha = new byte[w * h];
                for (var y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    for (var x = 0; x < w; x++) alpha[y * w + x] = row[x * 4 + 3];
                }
                return new TextMask { Alpha = alpha, Width = w, Height = h };
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or TypeInitializationException) { PromptFlowStudio.Program.Log("Wcag.Raster", ex); }
#endif
        return NewsMotionGraphicsOverlayEngine.BlockFallback(text, px);
    }

    /// <summary>자막 사전 래스터(줄별 마스크 캐시 · 상한 64).</summary>
    public void Prepare(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            lock (_gate) if (_masks.ContainsKey(line)) continue;
            var m = RasterizeSpaced(line, Style.SizePx, Style.LetterSpacingEm);
            lock (_gate) { if (_masks.Count > 64) _masks.Clear(); _masks[line] = m; }
        }
    }

    /// <summary>딥 블랙 배경 자막 합성(하단 중앙 · 행간). Prepare 이후 무할당 — 미준비 줄은 건너뜀.</summary>
    public int RenderSubtitle(Span<byte> rgba, int w, int h, int stride, string text)
    {
        var st = Style;
        var fg = EnsureContrast(st.Foreground, st.Background, AaaRatio);
        var lineH = (int)(st.SizePx * st.LineHeight);
        var pad = st.SizePx / 2;
        var lines = 1;
        foreach (var c in text) if (c == '\n') lines++;
        var bottom = h - (int)(h * 0.06f);
        var top = bottom - lines * lineH - pad;
        var drawn = 0;
        lock (_gate)
        {
            var start = 0; var li = 0;
            for (var i = 0; i <= text.Length; i++)
            {
                if (i < text.Length && text[i] != '\n') continue;
                var key = text.AsSpan(start, i - start);
                TextMask? m = null;
                foreach (var kv in _masks) if (key.SequenceEqual(kv.Key)) { m = kv.Value; break; }
                if (m is not null)
                {
                    var x = (w - m.Width) / 2; var y = top + pad / 2 + li * lineH + (lineH - m.Height) / 2;
                    NewsMotionGraphicsOverlayEngine.FillRect(rgba, w, h, stride, x - pad, y - (lineH - m.Height) / 2, m.Width + pad * 2, lineH, st.Background, st.BackgroundAlpha);
                    NewsMotionGraphicsOverlayEngine.DrawMask(rgba, w, h, stride, m, x, y, fg, 255, 0);
                    drawn++;
                }
                start = i + 1; li++;
            }
        }
        Interlocked.Increment(ref _subtitleFrames);
        return drawn;
    }

    // ══════════════════════ 내보내기 ══════════════════════

    /// <summary>보정 3D LUT(.cube · 기본 17³).</summary>
    public static string ExportCube(ColorVisionDeficiency d, float severity, int size = 17)
    {
        Span<float> m = stackalloc float[9];
        CorrectionMatrix(d, severity, m);
        var sb = new StringBuilder();
        sb.Append("TITLE \"PFS Daltonize ").Append(Label(d)).Append("\"\nLUT_3D_SIZE ").Append(size.ToString(CultureInfo.InvariantCulture)).Append('\n');
        Span<byte> p = stackalloc byte[3];
        for (var b = 0; b < size; b++)
            for (var g = 0; g < size; g++)
                for (var r = 0; r < size; r++)
                {
                    p[0] = (byte)Math.Round(r * 255.0 / (size - 1)); p[1] = (byte)Math.Round(g * 255.0 / (size - 1)); p[2] = (byte)Math.Round(b * 255.0 / (size - 1));
                    ApplyMatrix(p, 3, m);
                    sb.Append((p[0] / 255.0).ToString("0.000000", CultureInfo.InvariantCulture)).Append(' ').Append((p[1] / 255.0).ToString("0.000000", CultureInfo.InvariantCulture)).Append(' ').Append((p[2] / 255.0).ToString("0.000000", CultureInfo.InvariantCulture)).Append('\n');
                }
        return sb.ToString();
    }

    public static string ToStyledVtt(IReadOnlyList<A11yCue> cues, CognitiveSubtitleStyle st)
    {
        var sb = new StringBuilder("WEBVTT\n\nSTYLE\n::cue {\n  font-family: \"").Append(st.FontFamily).Append("\", \"").Append(st.FallbackFamily).Append("\", sans-serif;\n  color: #")
            .Append(st.Foreground.ToString("X6", CultureInfo.InvariantCulture)).Append(";\n  background-color: rgba(0,0,0,").Append((st.BackgroundAlpha / 255.0).ToString("0.##", CultureInfo.InvariantCulture))
            .Append(");\n  letter-spacing: ").Append(st.LetterSpacingEm.ToString("0.##", CultureInfo.InvariantCulture)).Append("em;\n  line-height: ").Append(st.LineHeight.ToString("0.##", CultureInfo.InvariantCulture)).Append(";\n}\n\n");
        for (var i = 0; i < cues.Count; i++)
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append('\n').Append(GenerativeAudioDescriptionEngine.VttTime(cues[i].Start)).Append(" --> ").Append(GenerativeAudioDescriptionEngine.VttTime(cues[i].End)).Append(" line:85%\n").Append(cues[i].Text).Append("\n\n");
        return sb.ToString();
    }

    public static string Label(ColorVisionDeficiency d) => d switch
    {
        ColorVisionDeficiency.Protanopia => "1형 적색맹(Protanopia)", ColorVisionDeficiency.Deuteranopia => "2형 녹색맹(Deuteranopia)", ColorVisionDeficiency.Tritanopia => "3형 청황색맹(Tritanopia)", _ => "정상",
    };

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Wcag.OnChange", ex); } }
}
