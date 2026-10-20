using System.Globalization;
using System.Text.Json;
using PromptFlowStudio.Services.VTuber;

namespace PromptFlowStudio.Services.Newsroom;

/// <summary>알파 마스크(텍스트 래스터 · 캐시).</summary>
public sealed class TextMask
{
    public byte[] Alpha { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }
}

/// <summary>
/// [PromptFlow Studio v16.44.0] 🎞 방송용 모션 그래픽 &amp; 하단 자막 오버레이 엔진.
///   ▸ 등급별(BREAKING 적 · URGENT 주황 · EXCLUSIVE 보라 · NEWS 남색) Dynamic Lower-Third(이즈아웃 슬라이드인 · 홀드 · 슬라이드아웃) · 등급 배지(점멸) · LIVE 표시 ·
///     뉴스 티커(무한 스크롤) · 인포그래픽 막대 차트(수치 주장 정규화 · 성장 애니메이션) · 화자 자막(앵커/리포터).
///   ▸ Render(RGBA Span · t): 사각/그라데이션/텍스트 마스크 알파 합성 — 워밍업(마스크 캐시) 후 무할당. 텍스트 래스터는 내용 변경 시 1회(Windows GDI · 그 외 블록 폴백).
///   ▸ OBS 브라우저 소스: 루프백 SSE 오버레이(HTML/CSS 애니메이션) 상태 JSON 변경 시 송출.
/// </summary>
public sealed class NewsMotionGraphicsOverlayEngine : IDisposable
{
    public const int DefaultPort = 17866, MaxBars = 6;
    private const double SlideSec = 0.45;

    private readonly object _gate = new();
    private readonly Dictionary<string, TextMask> _cache = new(StringComparer.Ordinal);
    private readonly OverlaySseServer _server;
    private TextMask? _titleMask, _subMask, _badgeMask, _tickerMask, _speakerMask, _speakerRoleMask, _liveMask, _chartTitleMask;
    private readonly TextMask?[] _barLabels = new TextMask?[MaxBars];
    private readonly float[] _barValues = new float[MaxBars];
    private int _barCount;
    private NewsGrade _grade;
    private double _l3Start = double.NaN, _l3Hold = 8, _chartStart = double.NaN, _speakerStart = double.NaN;
    private string _ticker = "", _title = "", _subtitle = "", _speaker = "", _speakerRole = "";
    private string? _chartTitle;
    private long _frames;

    public NewsMotionGraphicsOverlayEngine() : this(null) { }

    public NewsMotionGraphicsOverlayEngine(string? mediaRoot)
        => _server = new OverlaySseServer(mediaRoot ?? Path.Combine(AppContext.BaseDirectory, "overlay_media"), NewsOverlayHtml);

    public event Action? OnChange;
    public double TickerSpeedPx { get; set; } = 140;
    public bool ShowLive { get; set; } = true;
    public long Frames => Interlocked.Read(ref _frames);
    public bool ServerRunning => _server.IsRunning;
    public string Url => _server.Url;
    public int Clients => _server.Clients;
    public NewsGrade Grade => _grade;
    public string Title => _title;
    public string Ticker => _ticker;
    public string? LastAction { get; private set; }
    public int CachedMasks { get { lock (_gate) return _cache.Count; } }

    public static uint GradeColor(NewsGrade g) => g switch { NewsGrade.Breaking => 0xD7263D, NewsGrade.Urgent => 0xF28C28, NewsGrade.Exclusive => 0x7B2CBF, _ => 0x1D4E89 };

    // ══════════════════════ 텍스트 래스터(캐시) ══════════════════════

    public TextMask Mask(string text, int px, bool bold = true)
    {
        var key = (bold ? "b" : "r") + px.ToString(CultureInfo.InvariantCulture) + "|" + text;
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var m)) return m;
            if (_cache.Count > 128) _cache.Clear();
            m = Rasterize(text, px, bold);
            _cache[key] = m;
            return m;
        }
    }

    public static TextMask Rasterize(string text, int px, bool bold)
    {
        if (text.Length == 0) return new TextMask { Width = 1, Height = Math.Max(1, px), Alpha = new byte[Math.Max(1, px)] };
#if WINDOWS
        try
        {
            using var font = new System.Drawing.Font("Malgun Gothic", px * 0.75f, bold ? System.Drawing.FontStyle.Bold : System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point);
            System.Drawing.SizeF size;
            using (var probe = new System.Drawing.Bitmap(1, 1))
            using (var pg = System.Drawing.Graphics.FromImage(probe)) size = pg.MeasureString(text, font, int.MaxValue, System.Drawing.StringFormat.GenericTypographic);
            int w = Math.Clamp((int)Math.Ceiling(size.Width) + 4, 1, 8192), h = Math.Clamp((int)Math.Ceiling(size.Height) + 2, 1, 512);
            using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(text, font, System.Drawing.Brushes.White, 1, 1, System.Drawing.StringFormat.GenericTypographic);
            }
            var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[data.Stride];
                var alpha = new byte[w * h];
                for (var y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    for (var x = 0; x < w; x++) alpha[y * w + x] = row[x * 4 + 3];
                }
                return new TextMask { Alpha = alpha, Width = w, Height = h };
            }
            finally { bmp.UnlockBits(data); }
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException or TypeInitializationException) { Program.Log("News.Rasterize", ex); }
#endif
        return BlockFallback(text, px);
    }

    /// <summary>비 Windows 폴백 — 글자당 외곽 블록(가독 대체 · OBS HTML 오버레이 권장).</summary>
    public static TextMask BlockFallback(string text, int px)
    {
        var cw = Math.Max(4, px * 6 / 10);
        var w = Math.Max(1, text.Length * cw); var h = Math.Max(1, px);
        var a = new byte[w * h];
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            for (var y = h / 5; y < h - h / 5; y++)
                for (var x = i * cw + 1; x < (i + 1) * cw - 1; x++)
                    if (y == h / 5 || y == h - h / 5 - 1 || x == i * cw + 1 || x == (i + 1) * cw - 2) a[y * w + x] = 255;
        }
        return new TextMask { Alpha = a, Width = w, Height = h };
    }

    // ══════════════════════ 상태 ══════════════════════

    public void ShowLowerThird(string title, string subtitle, NewsGrade grade, double startSec, double holdSec = 8)
    {
        var tm = Mask(title, 30); var sm = Mask(subtitle, 20, false); var bm = Mask(RealtimeNewsFeedFactCheckEngine.GradeLabel(grade), 18);
        lock (_gate) { _title = title; _subtitle = subtitle; _grade = grade; _titleMask = tm; _subMask = sm; _badgeMask = bm; _l3Start = startSec; _l3Hold = holdSec; }
        _liveMask ??= Mask("LIVE", 16);
        LastAction = "🟥 하단 자막 · " + RealtimeNewsFeedFactCheckEngine.GradeLabel(grade) + " · " + title;
        Push();
    }

    public void HideLowerThird() { lock (_gate) _l3Start = double.NaN; Push(); }

    public void SetTicker(IEnumerable<string> headlines)
    {
        var text = "   ◆   " + string.Join("   ◆   ", headlines) + "   ◆   ";
        var m = Mask(text, 20);
        lock (_gate) { _ticker = text; _tickerMask = m; }
        Push();
    }

    public void ShowSpeaker(string name, string role)
    {
        var nm = Mask(name, 22); var rm = Mask(role, 16, false);
        lock (_gate) { _speaker = name; _speakerRole = role; _speakerMask = nm; _speakerRoleMask = rm; _speakerStart = Environment.TickCount64 / 1000.0; }
        Push();
    }

    /// <summary>인포그래픽 막대(최대 6 · 값 정규화 · 성장 애니메이션).</summary>
    public void ShowChart(string title, IReadOnlyList<(string Label, double Value)> bars, double startSec)
    {
        var n = Math.Min(MaxBars, bars.Count);
        double max = 0;
        for (var i = 0; i < n; i++) max = Math.Max(max, bars[i].Value);
        var tm = Mask(title, 18);
        var labels = new TextMask[n];
        for (var i = 0; i < n; i++) labels[i] = Mask(bars[i].Label, 14, false);
        lock (_gate)
        {
            _chartTitle = title; _chartTitleMask = tm; _barCount = n; _chartStart = startSec;
            for (var i = 0; i < n; i++) { _barValues[i] = max <= 0 ? 0 : (float)(bars[i].Value / max); _barLabels[i] = labels[i]; }
        }
        Push();
    }

    public void HideChart() { lock (_gate) { _chartStart = double.NaN; _chartTitle = null; } Push(); }

    /// <summary>스토리 → 하단 자막 · 수치 인포그래픽 · 티커 갱신.</summary>
    public void ApplyStory(NewsStory st, IReadOnlyList<NewsStory> headlines, double nowSec)
    {
        ShowLowerThird(DualAiNewsAnchorGenerator.StripTitle(st.Lead.Title), st.Lead.Source + " · 팩트체크 " + st.Score.ToString(CultureInfo.InvariantCulture) + "% " + RealtimeNewsFeedFactCheckEngine.VerdictLabel(st.Verdict), st.Grade, nowSec);
        var bars = new List<(string, double)>();
        Span<double> f = stackalloc double[4];
        foreach (var it in st.Items)
        {
            var n = RealtimeNewsFeedFactCheckEngine.ExtractFigures(it.Title + " " + it.Summary, f);
            if (n > 0) bars.Add((it.Source, f[0]));
            if (bars.Count >= MaxBars) break;
        }
        if (bars.Count >= 2) ShowChart("보도별 수치 비교", bars, nowSec); else HideChart();
        var heads = new List<string>();
        foreach (var h in headlines) { heads.Add("[" + RealtimeNewsFeedFactCheckEngine.GradeLabel(h.Grade) + "] " + DualAiNewsAnchorGenerator.StripTitle(h.Lead.Title)); if (heads.Count >= 8) break; }
        if (heads.Count > 0) SetTicker(heads);
    }

    // ══════════════════════ 합성(무할당) ══════════════════════

    private static double EaseOut(double p) { p = Math.Clamp(p, 0, 1); var q = 1 - p; return 1 - q * q * q; }

    /// <summary>RGBA 프레임 위 합성(t = 초). 워밍업 후 무할당.</summary>
    public void Render(Span<byte> rgba, int w, int h, int stride, double t)
    {
        lock (_gate)
        {
            var color = GradeColor(_grade);
            var scale = h / 720.0;
            // 티커 밴드(하단 48px)
            var tickerH = (int)(48 * scale);
            var ty = h - tickerH;
            FillRect(rgba, w, h, stride, 0, ty, w, tickerH, 0x0B1220, 235);
            FillRect(rgba, w, h, stride, 0, ty, (int)(150 * scale), tickerH, color, 255);
            if (_tickerMask is { } tm)
            {
                var span = tm.Width + w;
                var x = w - (int)(t * TickerSpeedPx % span);
                DrawMask(rgba, w, h, stride, tm, x, ty + (tickerH - tm.Height) / 2, 0xFFFFFF, 255, (int)(150 * scale) + 6);
            }
            // 하단 자막(Lower-Third)
            if (!double.IsNaN(_l3Start) && _titleMask is { } title)
            {
                var el = t - _l3Start;
                var total = SlideSec * 2 + _l3Hold;
                if (el >= 0 && el <= total)
                {
                    var barW = (int)(Math.Max(title.Width + 60, (_subMask?.Width ?? 0) + 60) * 1.0);
                    barW = Math.Min(barW, w - (int)(80 * scale));
                    var p = el < SlideSec ? EaseOut(el / SlideSec) : el > total - SlideSec ? EaseOut((total - el) / SlideSec) : 1;
                    var x0 = (int)(40 * scale) - (int)((1 - p) * (barW + 60 * scale));
                    var y0 = ty - (int)(120 * scale);
                    var flash = _grade == NewsGrade.Breaking && ((int)(t * 2.5) & 1) == 0;
                    FillRect(rgba, w, h, stride, x0, y0, (int)(160 * scale), (int)(32 * scale), flash ? 0xFFFFFF : color, 255);
                    if (_badgeMask is { } bm) DrawMask(rgba, w, h, stride, bm, x0 + 10, y0 + ((int)(32 * scale) - bm.Height) / 2, flash ? color : 0xFFFFFF, 255, 0);
                    FillGradient(rgba, w, h, stride, x0, y0 + (int)(32 * scale), barW, (int)(56 * scale), color, 0x0B1220, 240);
                    DrawMask(rgba, w, h, stride, title, x0 + 14, y0 + (int)(32 * scale) + ((int)(56 * scale) - title.Height) / 2, 0xFFFFFF, 255, 0);
                    if (_subMask is { } sm)
                    {
                        FillRect(rgba, w, h, stride, x0, y0 + (int)(88 * scale), barW, (int)(30 * scale), 0xF1F5F9, 235);
                        DrawMask(rgba, w, h, stride, sm, x0 + 14, y0 + (int)(88 * scale) + ((int)(30 * scale) - sm.Height) / 2, 0x0B1220, 255, 0);
                    }
                }
            }
            // 화자 자막(우상단 하단부)
            if (_speakerMask is { } spk)
            {
                var sx = w - spk.Width - (int)(60 * scale);
                var sy = ty - (int)(170 * scale);
                FillRect(rgba, w, h, stride, sx - 12, sy - 4, spk.Width + 24 + (_speakerRoleMask?.Width ?? 0) + 10, spk.Height + 8, 0x111827, 200);
                FillRect(rgba, w, h, stride, sx - 12, sy - 4, 4, spk.Height + 8, color, 255);
                DrawMask(rgba, w, h, stride, spk, sx, sy, 0xFFFFFF, 255, 0);
                if (_speakerRoleMask is { } rm) DrawMask(rgba, w, h, stride, rm, sx + spk.Width + 10, sy + (spk.Height - rm.Height), 0x94A3B8, 255, 0);
            }
            // 인포그래픽(우상단)
            if (!double.IsNaN(_chartStart) && _barCount > 0)
            {
                var cw = (int)(300 * scale); var ch = (int)(40 + _barCount * 30 * scale);
                var cx = w - cw - (int)(30 * scale); var cy = (int)(30 * scale);
                FillRect(rgba, w, h, stride, cx, cy, cw, ch, 0x0B1220, 210);
                if (_chartTitleMask is { } ctm) DrawMask(rgba, w, h, stride, ctm, cx + 10, cy + 6, 0xFFFFFF, 255, 0);
                var grow = EaseOut((t - _chartStart) / 0.8);
                for (var i = 0; i < _barCount; i++)
                {
                    var by = cy + (int)(34 * scale) + i * (int)(30 * scale);
                    if (_barLabels[i] is { } lm) DrawMask(rgba, w, h, stride, lm, cx + 10, by + 2, 0xCBD5E1, 255, 0);
                    var bx = cx + (int)(110 * scale);
                    var bw = (int)((cw - (int)(124 * scale)) * _barValues[i] * grow);
                    FillRect(rgba, w, h, stride, bx, by + 4, Math.Max(1, bw), (int)(18 * scale), i == 0 ? color : 0x38BDF8, 255);
                }
            }
            // LIVE 배지(좌상단)
            if (ShowLive && _liveMask is { } live)
            {
                var lx = (int)(30 * scale); var ly = (int)(30 * scale);
                FillRect(rgba, w, h, stride, lx, ly, live.Width + 30, live.Height + 10, 0xDC2626, ((int)(t * 2) & 1) == 0 ? 255 : 200);
                DrawMask(rgba, w, h, stride, live, lx + 22, ly + 5, 0xFFFFFF, 255, 0);
                FillRect(rgba, w, h, stride, lx + 8, ly + live.Height / 2 + 1, 8, 8, 0xFFFFFF, 255);
            }
        }
        Interlocked.Increment(ref _frames);
    }

    public static void FillRect(Span<byte> d, int w, int h, int stride, int x, int y, int rw, int rh, uint rgb, int alpha)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(w, x + rw), y1 = Math.Min(h, y + rh);
        if (x0 >= x1 || y0 >= y1 || alpha <= 0) return;
        int r = (int)(rgb >> 16) & 255, g = (int)(rgb >> 8) & 255, b = (int)rgb & 255, a = Math.Min(255, alpha), ia = 255 - a;
        for (var yy = y0; yy < y1; yy++)
        {
            var o = yy * stride + x0 * 4;
            for (var xx = x0; xx < x1; xx++, o += 4)
            {
                d[o] = (byte)((r * a + d[o] * ia) / 255); d[o + 1] = (byte)((g * a + d[o + 1] * ia) / 255); d[o + 2] = (byte)((b * a + d[o + 2] * ia) / 255); d[o + 3] = 255;
            }
        }
    }

    private static void FillGradient(Span<byte> d, int w, int h, int stride, int x, int y, int rw, int rh, uint from, uint to, int alpha)
    {
        for (var i = 0; i < rw; i += 8)
        {
            var f = rw <= 1 ? 0 : i / (double)(rw - 1);
            uint Mix(int sh) => (uint)((((from >> sh) & 255) * (1 - f)) + (((to >> sh) & 255) * f)) << sh;
            FillRect(d, w, h, stride, x + i, y, Math.Min(8, rw - i), rh, Mix(16) | Mix(8) | Mix(0), alpha);
        }
    }

    /// <summary>알파 마스크 합성(clipLeft 왼쪽 클립 · 무할당).</summary>
    public static void DrawMask(Span<byte> d, int w, int h, int stride, TextMask m, int x, int y, uint rgb, int opacity, int clipLeft)
    {
        int r = (int)(rgb >> 16) & 255, g = (int)(rgb >> 8) & 255, b = (int)rgb & 255;
        int sx0 = Math.Max(0, Math.Max(clipLeft, 0) - x), sy0 = Math.Max(0, -y);
        int sx1 = Math.Min(m.Width, w - x), sy1 = Math.Min(m.Height, h - y);
        var a = m.Alpha;
        for (var sy = sy0; sy < sy1; sy++)
        {
            var o = (y + sy) * stride + (x + sx0) * 4;
            var mi = sy * m.Width + sx0;
            for (var sx = sx0; sx < sx1; sx++, o += 4, mi++)
            {
                var al = a[mi] * opacity / 255;
                if (al == 0) continue;
                var ia = 255 - al;
                d[o] = (byte)((r * al + d[o] * ia) / 255); d[o + 1] = (byte)((g * al + d[o + 1] * ia) / 255); d[o + 2] = (byte)((b * al + d[o + 2] * ia) / 255);
            }
        }
    }

    // ══════════════════════ OBS SSE 오버레이 ══════════════════════

    public void StartServer(int port = DefaultPort)
    {
        if (_server.IsRunning) return;
        _server.Start(port);
        LastAction = "📺 뉴스 그래픽 오버레이 ON · " + _server.Url;
        Push();
    }

    public void StopServer() { _server.Dispose(); LastAction = "⏹ 뉴스 그래픽 오버레이 OFF"; Raise(); }

    public string StateJson()
    {
        lock (_gate)
        {
            using var ms = new MemoryStream();
            using (var j = new Utf8JsonWriter(ms))
            {
                j.WriteStartObject();
                j.WriteString("grade", RealtimeNewsFeedFactCheckEngine.GradeLabel(_grade));
                j.WriteString("color", "#" + GradeColor(_grade).ToString("X6", CultureInfo.InvariantCulture));
                j.WriteBoolean("l3", !double.IsNaN(_l3Start));
                j.WriteString("title", _title);
                j.WriteString("sub", _subtitle);
                j.WriteString("ticker", _ticker);
                j.WriteString("speaker", _speaker);
                j.WriteString("role", _speakerRole);
                if (_chartTitle is not null)
                {
                    j.WriteString("chart", _chartTitle);
                    j.WriteStartArray("bars");
                    for (var i = 0; i < _barCount; i++) j.WriteNumberValue(Math.Round(_barValues[i], 3));
                    j.WriteEndArray();
                }
                j.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private void Push()
    {
        if (_server.IsRunning) { try { _server.Broadcast(StateJson()); } catch (Exception ex) { Program.Log("News.Push", ex); } }
        Raise();
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("NewsGfx.OnChange", ex); } }

    public void Dispose() => _server.Dispose();

    private const string NewsOverlayHtml = """
<!doctype html><html><head><meta charset="utf-8"><title>PromptFlow News Graphics</title>
<style>html,body{margin:0;background:transparent;overflow:hidden;font-family:'Pretendard','Malgun Gothic',sans-serif;color:#fff}
#l3{position:fixed;left:4vw;bottom:13vh;transform:translateX(-120%);transition:transform .45s cubic-bezier(.2,.9,.3,1)}#l3.on{transform:none}
#badge{display:inline-block;padding:.3em .9em;font-weight:900;letter-spacing:.08em}#title{font-size:3.6vh;font-weight:800;padding:.5em 1em;background:linear-gradient(90deg,var(--c),#0b1220)}
#sub{font-size:2.3vh;padding:.35em 1em;background:#f1f5f9;color:#0b1220}#tk{position:fixed;left:0;right:0;bottom:0;height:6.5vh;background:#0b1220ee;display:flex;align-items:center;overflow:hidden}
#tkl{background:var(--c);height:100%;display:flex;align-items:center;padding:0 1.2em;font-weight:900;z-index:2}#tkt{white-space:nowrap;font-size:2.5vh;animation:sc 40s linear infinite;padding-left:100%}
@keyframes sc{to{transform:translateX(-100%)}}#spk{position:fixed;right:4vw;bottom:22vh;background:#111827cc;border-left:.4em solid var(--c);padding:.4em 1em;font-size:2.6vh;font-weight:800}
#spk small{color:#94a3b8;margin-left:.6em;font-weight:400}#chart{position:fixed;right:3vw;top:4vh;background:#0b1220d0;padding:1em;min-width:28vw;display:none}#chart.on{display:block}
.bar{height:2.2vh;background:var(--c);margin:.5em 0;transition:width .8s ease-out}#live{position:fixed;left:3vw;top:4vh;background:#dc2626;padding:.3em .9em;font-weight:900}</style></head>
<body style="--c:#1D4E89"><div id="live">● LIVE</div><div id="l3"><div id="badge"></div><div id="title"></div><div id="sub"></div></div><div id="spk"></div><div id="chart"><b id="ct"></b><div id="bars"></div></div>
<div id="tk"><div id="tkl">NEWS</div><div id="tkt"></div></div>
<script>const es=new EventSource('/events');es.onmessage=e=>{const s=JSON.parse(e.data);document.body.style.setProperty('--c',s.color);badge.textContent=s.grade;badge.style.background=s.color;tkl.textContent=s.grade;
title.textContent=s.title;sub.textContent=s.sub;l3.classList.toggle('on',!!s.l3);tkt.textContent=s.ticker;spk.innerHTML=s.speaker?s.speaker+'<small>'+s.role+'</small>':'';
chart.classList.toggle('on',!!s.chart);ct.textContent=s.chart||'';bars.innerHTML=(s.bars||[]).map(v=>'<div class="bar" style="width:'+(v*100)+'%"></div>').join('')};</script></body></html>
""";
}
