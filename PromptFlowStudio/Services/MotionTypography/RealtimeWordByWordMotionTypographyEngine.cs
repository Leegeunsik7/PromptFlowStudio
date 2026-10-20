namespace PromptFlowStudio.Services.MotionTypography;

public enum TypoPreset : byte { Pop, Typewriter, Bounce }
public enum TypoLang : byte { Ko, En, Ja, Es }

/// <summary>단어 1개(값 형식) — 텍스트 범위 · 타임코드 · em 폭 · 페이지/줄/슬롯 배치.</summary>
public struct TypoWord
{
    public int Start, Length, Page, Line;
    public float StartMs, EndMs, WidthEm, SlotX;
}

/// <summary>
/// [PromptFlow Studio v17.52.0] 🔤 실시간 9:16 단어 단위 모션 타이포그래피 엔진(<see cref="Render"/> 무할당 · SDF).
/// <b>타임라인</b>: v17.32 더빙 엔진이 단어 타임코드를 노출하지 않으므로 언어별 음절 속도(KO 6.0 · EN 4.2 · ES 5.5 · JA 7.0 음절/s · v17.47 더빙 보이스와 동일 계열)와
/// 단어 음절 수(한글 음절 · 가나 모라 · 라틴 모음군)로 발화 구간을 추정한다 — 외부 타임코드(<see cref="SetTiming"/>)가 있으면 그것을 쓴다.
/// <b>레이아웃</b>: 최대 2 줄 페이지 · 단어마다 최대 배율(Pop 1.5) 슬롯을 예약해 현재 단어 확대 시 재배치(지터) 없이 블록 안에 머문다 → 블록 박스를 세이프 존 가드로 맞춤.
/// <b>프리셋</b>: Pop(현재 단어 120 ms easeOutCubic 1.0→1.5 · 노랑/형광 교대 하이라이트) · Typewriter(글자 단위 노출 · 1.15) · Bounce(1.3 · 감쇠 바운스).
/// <b>래스터</b>: SDF 쌍선형 표본 → 채움(0.5 등치선 · 화면 공간 AA) + 0.12 em 검은 외곽선을 사전 곱 RGBA 레이어에 over 합성.
/// </summary>
public sealed class RealtimeWordByWordMotionTypographyEngine
{
    public const int MaxWords = 96, TextCap = 1024, MaxLines = 2;
    public const float PopScale = 1.5f, OutlineEm = 0.12f, SpaceEm = 0.28f, LineGap = 1.12f;
    private static readonly float[] SylRate = { 6.0f, 4.2f, 7.0f, 5.5f };
    private readonly TypoWord[] _w = new TypoWord[MaxWords];
    private readonly char[] _text = new char[TextCap];
    private readonly float[] _pageW = new float[MaxWords], _lineW = new float[MaxWords * MaxLines];
    private int _n, _len, _pages;
    private float _emTop, _emBottom = 1f;

    public RealtimeWordByWordMotionTypographyEngine(SdfGlyphAtlas atlas) => Atlas = atlas;

    public SdfGlyphAtlas Atlas { get; }
    public TypoPreset Preset { get; set; } = TypoPreset.Pop;
    public float FontPx { get; set; } = 92f;
    public float AnchorY { get; set; } = 0.62f;
    public int WordCount => _n;
    public int PageCount => _pages;
    public ref readonly TypoWord Word(int i) => ref _w[i];
    public ReadOnlySpan<char> WordText(int i) => _text.AsSpan(_w[i].Start, _w[i].Length);
    public float TotalMs => _n > 0 ? _w[_n - 1].EndMs : 0;
    public int CurrentWord { get; private set; } = -1;
    public float CurrentScale { get; private set; } = 1f;
    public float LastBlockScale { get; private set; } = 1f;
    public PxRect LastBlock { get; private set; }
    public PxRect LastDrawnBounds { get; private set; }
    public double LastRenderUs { get; private set; }
    public static (float R, float G, float B) Highlight(int k) => (k & 1) == 0 ? (1f, 0.902f, 0f) : (0.224f, 1f, 0.078f);

    public static int Syllables(ReadOnlySpan<char> w)
    {
        int n = 0; var vowel = false;
        foreach (var c in w)
        {
            if (c is >= '가' and <= '힣' or >= '぀' and <= 'ヿ' or >= '一' and <= '鿿') { n++; vowel = false; continue; }
            var v = "aeiouyáéíóúAEIOUYÁÉÍÓÚ".Contains(c);
            if (v && !vowel) n++;
            vowel = v;
        }
        return Math.Max(n, 1);
    }

    /// <summary>캡션 텍스트 → 단어 · 추정 타임라인 · 레이아웃(비핫패스 · 글리프 준비 포함).</summary>
    public void SetCaption(ReadOnlySpan<char> text, TypoLang lang, float startMs = 0, float gapMs = 60)
    {
        _len = Math.Min(text.Length, TextCap);
        text[.._len].CopyTo(_text);
        Atlas.Ensure(_text.AsSpan(0, _len));
        _n = 0;
        var t = startMs;
        var rate = SylRate[(int)lang];
        for (var i = 0; i < _len && _n < MaxWords;)
        {
            while (i < _len && char.IsWhiteSpace(_text[i])) i++;
            var s = i;
            while (i < _len && !char.IsWhiteSpace(_text[i])) i++;
            if (i == s) break;
            ref var w = ref _w[_n++];
            w = default; w.Start = s; w.Length = i - s;
            var dur = 1000f * Syllables(_text.AsSpan(s, i - s)) / rate;
            w.StartMs = t; w.EndMs = t + MathF.Max(dur, 160f);
            t = w.EndMs + gapMs;
        }
        Layout(9999f);
    }

    /// <summary>
    /// v17.53 — 단어 시작을 순서대로 그리드(비트/8 분 등 오름차순 ms)에 양자화(무할당). 앞 단어가 차지한 지점 이후의 가장 가까운 지점이
    /// ±toleranceMs 안이면 스냅하고, 겹치면 앞 단어 꼬리를 잘라 길이 누적 밀림을 막는다. 재호출해도 안정(이미 그리드 위면 유지). 스냅 수 반환.
    /// </summary>
    public int SnapToBeats(ReadOnlySpan<double> beatMs, float toleranceMs)
    {
        var snapped = 0; var prevStart = double.NegativeInfinity; var j = 0;
        for (var i = 0; i < _n; i++)
        {
            ref var w = ref _w[i];
            var dur = w.EndMs - w.StartMs;
            while (j < beatMs.Length && beatMs[j] <= prevStart) j++;
            var k = j;
            while (k + 1 < beatMs.Length && beatMs[k + 1] <= w.StartMs) k++;
            if (k + 1 < beatMs.Length && Math.Abs(beatMs[k + 1] - w.StartMs) < Math.Abs(beatMs[k] - w.StartMs)) k++;
            if (k < beatMs.Length && Math.Abs(beatMs[k] - w.StartMs) <= toleranceMs) { w.StartMs = (float)beatMs[k]; snapped++; }
            else if (w.StartMs <= prevStart) w.StartMs = (float)prevStart + 1f;
            w.EndMs = w.StartMs + dur;
            if (i > 0) { ref var p = ref _w[i - 1]; if (p.EndMs > w.StartMs) p.EndMs = MathF.Max(w.StartMs, p.StartMs + 1f); }
            prevStart = w.StartMs;
        }
        return snapped;
    }

    /// <summary>외부 단어 타임코드 적용(i 번째 단어 · ms).</summary>
    public void SetTiming(int i, float startMs, float endMs) { if ((uint)i < (uint)_n) { _w[i].StartMs = startMs; _w[i].EndMs = MathF.Max(endMs, startMs + 1); } }

    /// <summary>em 폭(슬롯 = 폭 × 최대 배율)으로 2 줄 페이지 구성 — maxLineEm 은 세이프 폭 / FontPx.</summary>
    public void Layout(float maxLineEm)
    {
        var limit = MathF.Max(maxLineEm, 1f);
        for (var i = 0; i < _n; i++)
        {
            ref var w = ref _w[i];
            var em = 0f;
            var span = _text.AsSpan(w.Start, w.Length);
            for (var k = 0; k < span.Length; k++)
            {
                int cp = span[k];
                if (char.IsHighSurrogate(span[k]) && k + 1 < span.Length) { cp = char.ConvertToUtf32(span[k], span[k + 1]); k++; }
                em += Atlas.TryGet(cp, out var gi) ? Atlas.Glyph(gi).Advance : 0.5f;
            }
            w.WidthEm = em;
        }
        _emTop = 0f; _emBottom = 1f;
        for (var k = 0; k < _len; k++)
        {
            int cp = _text[k];
            if (char.IsHighSurrogate(_text[k]) && k + 1 < _len) { cp = char.ConvertToUtf32(_text[k], _text[k + 1]); k++; }
            if (!Atlas.TryGet(cp, out var gi)) continue;
            ref readonly var gg = ref Atlas.Glyph(gi);
            if (gg.Height <= 0) continue;
            _emTop = MathF.Min(_emTop, gg.Top); _emBottom = MathF.Max(_emBottom, gg.Top + gg.Height);
        }
        _pages = 0;
        int page = 0, line = 0; float x = 0;
        for (var i = 0; i < _n; i++)
        {
            ref var w = ref _w[i];
            var slot = w.WidthEm * PopScale;
            if (x > 0 && x + SpaceEm + slot > limit) { line++; x = 0; if (line >= MaxLines) { page++; line = 0; } }
            if (x > 0) x += SpaceEm;
            w.Page = page; w.Line = line; w.SlotX = x;
            x += slot;
            _lineW[page * MaxLines + line] = x;
            if (line == 0) _lineW[page * MaxLines + 1] = 0;
        }
        _pages = _n > 0 ? page + 1 : 0;
        for (var p = 0; p < _pages; p++) _pageW[p] = MathF.Max(_lineW[p * MaxLines], _lineW[p * MaxLines + 1]);
    }

    private int Find(float tMs)
    {
        if (_n == 0 || tMs < _w[0].StartMs) return -1;
        for (var i = 0; i < _n; i++) if (tMs < _w[i].EndMs || i + 1 < _n && tMs < _w[i + 1].StartMs) return i;
        return -1;
    }

    /// <summary>프레임 렌더(무할당) — 현재 페이지를 세이프 존 안 블록으로 그린다.</summary>
    public void Render(float tMs, RgbaOverlayLayer layer, CrossPlatformUiSafeZoneGuard guard)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var cur = Find(tMs);
        CurrentWord = cur;
        LastDrawnBounds = default;
        if (cur < 0) { LastRenderUs = 0; return; }
        var page = _w[cur].Page;
        var lines = 1;
        for (var i = 0; i < _n; i++) if (_w[i].Page == page && _w[i].Line == 1) { lines = 2; break; }
        var px = FontPx;
        var pad = OutlineEm + 0.02f;
        var span = _emBottom - _emTop + 2 * pad; // 실제 글리프 수직 범위(외곽선 포함 · em)
        var lineH = px * PopScale * span * LineGap;
        var block = new PxRect(0, 0, (int)MathF.Ceiling(_pageW[page] * px + 2 * OutlineEm * px), (int)MathF.Ceiling(lines * lineH));
        block.X = (layer.Width - block.W) / 2; block.Y = (int)(layer.Height * AnchorY) - block.H / 2;
        var bs = guard.Fit(ref block);
        LastBlock = block; LastBlockScale = bs;
        px *= bs; lineH *= bs;
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = 0, by1 = 0;
        var hl = 0;
        for (var i = 0; i < _n; i++)
        {
            ref readonly var w = ref _w[i];
            if (w.Page != page) continue;
            var lw = _lineW[page * MaxLines + w.Line] * px;
            var lineX = block.X + (block.W - lw) * 0.5f;
            var since = tMs - w.StartMs;
            float scale = 1f, dy = 0f; var reveal = int.MaxValue;
            var isCur = i == cur;
            var past = i < cur;
            if (Preset == TypoPreset.Pop && isCur) scale = 1f + (PopScale - 1f) * EaseOut(since / 120f);
            else if (Preset == TypoPreset.Bounce && isCur) { scale = 1.3f; dy = -0.1f * px * span * MathF.Abs(MathF.Sin(MathF.PI * since / 180f)) * MathF.Exp(-since / 300f); }
            else if (Preset == TypoPreset.Typewriter)
            {
                if (isCur) { scale = 1.15f; reveal = (int)MathF.Ceiling(w.Length * Math.Clamp(since / MathF.Max(w.EndMs - w.StartMs, 1f), 0f, 1f)); }
                else if (!past) reveal = 0;
            }
            if (isCur) CurrentScale = scale;
            var slotW = w.WidthEm * PopScale * px;
            var ww = w.WidthEm * px * scale;
            var x = lineX + w.SlotX * px + (slotW - ww) * 0.5f;
            var baseY = block.Y + w.Line * lineH + (lineH - span * px * scale) * 0.5f - (_emTop - pad) * px * scale + dy;
            float r = 1f, g = 1f, b = 1f;
            if (isCur) (r, g, b) = Highlight(hl);
            if (isCur || past) hl++;
            var drawn = DrawWord(layer, _text.AsSpan(w.Start, w.Length), x, baseY, px * scale, r, g, b, reveal);
            if (!drawn.IsEmpty) { bx0 = Math.Min(bx0, drawn.X); by0 = Math.Min(by0, drawn.Y); bx1 = Math.Max(bx1, drawn.Right); by1 = Math.Max(by1, drawn.Bottom); }
        }
        if (bx1 > bx0) { LastDrawnBounds = new PxRect(bx0, by0, bx1 - bx0, by1 - by0); guard.Audit(LastDrawnBounds); }
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static float EaseOut(float u) { u = Math.Clamp(u, 0f, 1f); var v = 1f - u; return 1f - v * v * v; }

    /// <summary>단어 SDF 래스터(em 상자 좌상단 x,y · px/em) — 실제 그린 픽셀 경계 반환.</summary>
    public PxRect DrawWord(RgbaOverlayLayer layer, ReadOnlySpan<char> word, float x, float y, float px, float r, float g, float b, int reveal = int.MaxValue)
        => SdfText.Draw(Atlas, layer, word, x, y, px, r, g, b, reveal);
}

/// <summary>SDF 텍스트 래스터라이저(무할당 · 채움 + 외곽선 · 공용).</summary>
public static class SdfText
{
    private const float OffX = (SdfGlyphAtlas.Spread * SdfGlyphAtlas.Hi + 4f) / SdfGlyphAtlas.Hi, OffY = (SdfGlyphAtlas.Cell - SdfGlyphAtlas.Em) / 2f;

    public static float MeasureEm(SdfGlyphAtlas atlas, ReadOnlySpan<char> s)
    {
        var em = 0f;
        for (var k = 0; k < s.Length; k++)
        {
            int cp = s[k];
            if (char.IsHighSurrogate(s[k]) && k + 1 < s.Length) { cp = char.ConvertToUtf32(s[k], s[k + 1]); k++; }
            em += cp == ' ' ? RealtimeWordByWordMotionTypographyEngine.SpaceEm : atlas.TryGet(cp, out var gi) ? atlas.Glyph(gi).Advance : 0.5f;
        }
        return em;
    }

    public static PxRect Draw(SdfGlyphAtlas atlas, RgbaOverlayLayer layer, ReadOnlySpan<char> s, float x, float y, float px, float r, float g, float b, int reveal = int.MaxValue, float outlineEm = RealtimeWordByWordMotionTypographyEngine.OutlineEm)
    {
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = 0, by1 = 0;
        var pen = x; var shown = 0;
        var aaEm = 0.75f / MathF.Max(px, 1f);
        const float toEm = SdfGlyphAtlas.Spread / 127f * 255f / SdfGlyphAtlas.Em;
        for (var k = 0; k < s.Length; k++)
        {
            int cp = s[k];
            if (char.IsHighSurrogate(s[k]) && k + 1 < s.Length) { cp = char.ConvertToUtf32(s[k], s[k + 1]); k++; }
            if (cp == ' ') { pen += RealtimeWordByWordMotionTypographyEngine.SpaceEm * px; continue; }
            if (shown++ >= reveal) break;
            if (!atlas.TryGet(cp, out var gi)) { pen += 0.5f * px; continue; }
            ref readonly var gl = ref atlas.Glyph(gi);
            var pad = outlineEm + aaEm;
            int x0 = (int)MathF.Floor(pen + (gl.Left - pad) * px), x1 = (int)MathF.Ceiling(pen + (gl.Left + gl.Width + pad) * px);
            int y0 = (int)MathF.Floor(y + (gl.Top - pad) * px), y1 = (int)MathF.Ceiling(y + (gl.Top + gl.Height + pad) * px);
            x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, layer.Width); y1 = Math.Min(y1, layer.Height);
            var inv = 1f / px;
            for (var yy = y0; yy < y1; yy++)
            {
                var v = (yy + 0.5f - y) * inv * SdfGlyphAtlas.Em + OffY;
                for (var xx = x0; xx < x1; xx++)
                {
                    var u = (xx + 0.5f - pen) * inv * SdfGlyphAtlas.Em + OffX;
                    var dEm = (0.5f - atlas.Sample(gi, u, v)) * toEm;
                    var oa = Math.Clamp((outlineEm - dEm) / aaEm * 0.5f + 0.5f, 0f, 1f);
                    if (oa <= 0f) continue;
                    var fa = Math.Clamp(-dEm / aaEm * 0.5f + 0.5f, 0f, 1f);
                    layer.Over(xx, yy, r * fa, g * fa, b * fa, oa);
                    if (xx < bx0) bx0 = xx; if (xx >= bx1) bx1 = xx + 1; if (yy < by0) by0 = yy; if (yy >= by1) by1 = yy + 1;
                }
            }
            pen += gl.Advance * px;
        }
        if (bx1 <= bx0) return default;
        var rc = new PxRect(bx0, by0, bx1 - bx0, by1 - by0);
        layer.MarkRect(rc);
        return rc;
    }
}
