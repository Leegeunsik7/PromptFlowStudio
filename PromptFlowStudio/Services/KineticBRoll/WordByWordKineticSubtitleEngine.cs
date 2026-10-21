using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.VisualHooks;

namespace PromptFlowStudio.Services.KineticBRoll;

public enum HormoziPreset : byte { HormoziYellow, NeonCyber, MinimalWhite }

public enum KineticWordState : byte { Upcoming, Active, Past }

/// <summary>단어 레이아웃(값 형식) — 청크(윈도우) · 청크 내 슬롯 x(px) · 폭(px).</summary>
public struct KineticWordSlot
{
    public int Chunk;
    public float SlotX, WidthPx;
}

/// <summary>
/// [PromptFlow Studio v17.78.0] 🔤 호르모지 스타일 단어 단위 키네틱 자막 엔진(<see cref="Render"/> 무할당).
/// v17.52 <see cref="RealtimeWordByWordMotionTypographyEngine"/> 의 음절 속도 타이밍(또는 <see cref="SetTiming"/> 외부 단어 타임스탬프)을 그대로 쓰되,
/// 문장/페이지가 아니라 최대 <see cref="WindowWords"/> 단어 · 세이프 폭 이내의 <b>슬라이딩 윈도우 청크</b>만 한 줄로 크게 띄운다.
/// 활성 단어 = 스프링 바운스(정착 1.15× · 첫 오버슈트 ≈ 1.19×) + 프리셋 고대비 틴트(형광 노랑/녹색 교대 등) · 지나간 단어 = 그레이스케일 디밍 · 다음 단어 = 기본색.
/// 단어 SDF 래스터(채움 α + 0.14 em 외곽선 α)는 v17.52 아틀라스를 표본화해 <see cref="ZeroAllocRenderFrameCacheGuard"/> 스프라이트 풀에 1 회 굽고,
/// 프레임마다 배율 · 틴트만 바꿔 블렌딩한다(재래스터 0). 청크 상자는 v17.77 <see cref="ReelsUiSafeZoneReframingGuard"/> 로 세이프존 안으로 시프트한다.
/// </summary>
public sealed class WordByWordKineticSubtitleEngine
{
    public const int WindowWords = 3, MaxWords = RealtimeWordByWordMotionTypographyEngine.MaxWords;
    public const float ActiveScale = 1.15f, OutlineEm = 0.14f, GapEm = 0.30f, EmTop = -0.12f, EmBottom = 1.12f, HoldAfterMs = 350f;
    public const double BounceHz = 9.0, BounceZeta = 0.55;
    private const float OffX = (SdfGlyphAtlas.Spread * SdfGlyphAtlas.Hi + 4f) / SdfGlyphAtlas.Hi, OffY = (SdfGlyphAtlas.Cell - SdfGlyphAtlas.Em) / 2f;
    private const float ToEm = SdfGlyphAtlas.Spread / 127f * 255f / SdfGlyphAtlas.Em;

    /// <summary>프리셋 색 [활성 A, 활성 B, 다음, 지나감(그레이)].</summary>
    public static readonly uint[][] PresetColors =
    {
        new[] { 0xFFFFE600u, 0xFF39FF14u, 0xFFFFFFFFu, 0xFF8C8C8Cu },
        new[] { 0xFF00F0FFu, 0xFFFF2BD6u, 0xFFE0E7FFu, 0xFF7A7A7Au },
        new[] { 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFB4B4B4u, 0xFF6E6E6Eu },
    };

    private readonly KineticWordSlot[] _slot = new KineticWordSlot[MaxWords];
    private readonly float[] _chunkW = new float[MaxWords];
    private readonly ulong[] _key = new ulong[MaxWords];
    private int _chunks;

    public WordByWordKineticSubtitleEngine(SdfGlyphAtlas atlas, ZeroAllocRenderFrameCacheGuard cache)
    {
        Timeline = new RealtimeWordByWordMotionTypographyEngine(atlas);
        Cache = cache;
    }

    public RealtimeWordByWordMotionTypographyEngine Timeline { get; }
    public SdfGlyphAtlas Atlas => Timeline.Atlas;
    public ZeroAllocRenderFrameCacheGuard Cache { get; }
    public ReelsUiSafeZoneReframingGuard? SafeZone { get; set; }
    public HormoziPreset Preset { get; set; } = HormoziPreset.HormoziYellow;
    public float FontPx { get; private set; } = 30f;
    public float AnchorY { get; set; } = 0.64f;
    public int FrameWidth { get; private set; } = 216;
    public int WordCount => Timeline.WordCount;
    public int ChunkCount => _chunks;
    public ref readonly KineticWordSlot Slot(int i) => ref _slot[i];
    public int ActiveWord { get; private set; } = -1;
    public float ActiveScaleNow { get; private set; } = 1f;
    public HookBox LastBlock { get; private set; }
    public ReframeVerdict LastVerdict { get; private set; }
    public long Renders { get; private set; }
    public long WordSwitches { get; private set; }
    public double LastRenderUs { get; private set; }

    /// <summary>스프링 계단 응답(0 → 1 · 오버슈트) — 활성 경과 ms.</summary>
    public static float Bounce(float sinceMs)
    {
        if (sinceMs <= 0) return 0f;
        var t = sinceMs / 1000.0; var w = 2 * Math.PI * BounceHz; var wd = w * Math.Sqrt(1 - BounceZeta * BounceZeta);
        return (float)(1 - Math.Exp(-BounceZeta * w * t) * (Math.Cos(wd * t) + BounceZeta * w / wd * Math.Sin(wd * t)));
    }

    public static float ActiveScaleAt(float sinceMs) => 1f + (ActiveScale - 1f) * Bounce(sinceMs);

    /// <summary>대본 설정(비핫패스) — 아틀라스 글리프 확보 · 음절 타이밍 · 청크 레이아웃.</summary>
    public void SetScript(ReadOnlySpan<char> text, TypoLang lang, float startMs = 0, float gapMs = 40)
    {
        Timeline.SetCaption(text, lang, startMs, gapMs);
        Relayout(FrameWidth, FontPx);
    }

    /// <summary>외부 단어 타임스탬프(ms).</summary>
    public void SetTiming(int i, float startMs, float endMs) => Timeline.SetTiming(i, startMs, endMs);

    /// <summary>폭(px) · 글자 크기(px) 기준 청크 분할(최대 3 단어 · 세이프 폭 · 활성 배율 슬롯 예약 → 지터 0).</summary>
    public void Relayout(int frameWidth, float fontPx)
    {
        FrameWidth = frameWidth; FontPx = fontPx;
        var safeW = (SafeZone?.SafeRect.W ?? 0.92f) * frameWidth;
        var gap = GapEm * fontPx;
        _chunks = 0;
        var x = 0f; var inChunk = 0;
        for (var i = 0; i < Timeline.WordCount; i++)
        {
            var word = Timeline.WordText(i);
            var w = (SdfText.MeasureEm(Atlas, word) + 2 * OutlineEm) * fontPx;
            var slotW = w * ActiveScale;
            if (inChunk > 0 && (inChunk >= WindowWords || x + gap + slotW > safeW)) { _chunkW[_chunks - 1] = x; x = 0; inChunk = 0; }
            if (inChunk == 0) _chunks++;
            if (inChunk > 0) x += gap;
            _slot[i] = new KineticWordSlot { Chunk = _chunks - 1, SlotX = x, WidthPx = w };
            _key[i] = ZeroAllocRenderFrameCacheGuard.Key(word, (int)(fontPx * 16));
            x += slotW; inChunk++;
        }
        if (_chunks > 0) _chunkW[_chunks - 1] = x;
    }

    public float ChunkWidth(int c) => _chunkW[c];

    /// <summary>시각 → 활성 단어(발화 중 또는 다음 단어 시작 전까지 유지 · 마지막 단어 뒤 350 ms 유지).</summary>
    public int ActiveAt(float tMs)
    {
        var n = Timeline.WordCount;
        if (n == 0 || tMs < Timeline.Word(0).StartMs) return -1;
        int lo = 0, hi = n - 1, k = 0;
        while (lo <= hi) { var m = (lo + hi) >> 1; if (Timeline.Word(m).StartMs <= tMs) { k = m; lo = m + 1; } else hi = m - 1; }
        return k == n - 1 && tMs > Timeline.Word(k).EndMs + HoldAfterMs ? -1 : k;
    }

    public KineticWordState StateOf(int word, int active) => word < active ? KineticWordState.Past : word == active ? KineticWordState.Active : KineticWordState.Upcoming;

    public uint ColorOf(int word, int active)
    {
        var c = PresetColors[(int)Preset];
        return word < active ? c[3] : word == active ? c[(word & 1) == 0 ? 0 : 1] : c[2];
    }

    /// <summary>프레임 렌더(0xAARRGGBB · 제자리) — 무할당 · 반환 = 그린 단어 수.</summary>
    public int Render(Span<uint> dst, int w, int h, float tMs, bool nearest = false)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var a = ActiveAt(tMs);
        if (a != ActiveWord) { ActiveWord = a; if (a >= 0) WordSwitches++; }
        ActiveScaleNow = 1f;
        if (a < 0) { LastRenderUs = 0; return 0; }
        var chunk = _slot[a].Chunk;
        var cw = _chunkW[chunk];
        var lineH = (EmBottom - EmTop + 2 * OutlineEm) * FontPx * ActiveScale;
        var box = HookBox.Centered(0.5f, AnchorY, cw / w, lineH / h);
        LastVerdict = SafeZone?.Clamp(ref box) ?? ReframeVerdict.Inside;
        LastBlock = box;
        var bx = box.X * w; var cy = box.Cy * h;
        var drawn = 0;
        for (var i = 0; i < Timeline.WordCount; i++)
        {
            ref readonly var s = ref _slot[i];
            if (s.Chunk != chunk) { if (s.Chunk > chunk) break; continue; }
            var scale = 1f;
            if (i == a) { scale = ActiveScaleAt(tMs - Timeline.Word(i).StartMs); ActiveScaleNow = scale; }
            var slot = EnsureSprite(i);
            var cx = bx + s.SlotX + s.WidthPx * ActiveScale * 0.5f;
            Blit(dst, w, h, slot, cx, cy, scale, ColorOf(i, a), nearest);
            drawn++;
        }
        Renders++;
        LastRenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return drawn;
    }

    /// <summary>단어 스프라이트 — 캐시 적중 = 재사용 · 미스 = 아틀라스 SDF 표본으로 채움/외곽선 α 굽기.</summary>
    private int EnsureSprite(int i)
    {
        if (Cache.TryGetSprite(_key[i], out var slot)) return slot;
        var px = FontPx;
        var sw = (int)MathF.Ceiling(_slot[i].WidthPx) + 2;
        var sh = (int)MathF.Ceiling((EmBottom - EmTop + 2 * OutlineEm) * px) + 2;
        slot = Cache.AcquireSprite(_key[i], sw, sh);
        sw = Cache.SpriteWidth(slot); sh = Cache.SpriteHeight(slot);
        var fill = Cache.Fill(slot); var line = Cache.Outline(slot);
        var word = Timeline.WordText(i);
        var pen = (OutlineEm * px) + 1f; var y = (OutlineEm - EmTop) * px + 1f;
        var aaEm = 0.75f / MathF.Max(px, 1f);
        var inv = 1f / px;
        long ops = 0;
        for (var k = 0; k < word.Length; k++)
        {
            int cp = word[k];
            if (char.IsHighSurrogate(word[k]) && k + 1 < word.Length) { cp = char.ConvertToUtf32(word[k], word[k + 1]); k++; }
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
                    ops++;
                    var oa = Math.Clamp((OutlineEm - dEm) / aaEm * 0.5f + 0.5f, 0f, 1f);
                    if (oa <= 0f) continue;
                    var fa = Math.Clamp(-dEm / aaEm * 0.5f + 0.5f, 0f, 1f);
                    var o = yy * sw + xx;
                    line[o] = Math.Max(line[o], (byte)(oa * 255f + 0.5f));
                    fill[o] = Math.Max(fill[o], (byte)(fa * 255f + 0.5f));
                }
            }
            pen += gl.Advance * px;
        }
        Cache.CommitSprite(slot, ops);
        return slot;
    }

    /// <summary>스프라이트 배율 블렌딩 — 외곽선 = 검정 · 채움 = 틴트(쌍선형 또는 최근접).</summary>
    private void Blit(Span<uint> dst, int w, int h, int slot, float cx, float cy, float scale, uint color, bool nearest)
    {
        var sw = Cache.SpriteWidth(slot); var sh = Cache.SpriteHeight(slot);
        var fill = Cache.Fill(slot); var line = Cache.Outline(slot);
        var dw = sw * scale; var dh = sh * scale;
        var ox = cx - dw * 0.5f; var oy = cy - dh * 0.5f;
        int x0 = Math.Max(0, (int)ox), x1 = Math.Min(w, (int)MathF.Ceiling(ox + dw));
        int y0 = Math.Max(0, (int)oy), y1 = Math.Min(h, (int)MathF.Ceiling(oy + dh));
        var inv = 1f / scale;
        uint cr = (color >> 16) & 0xFF, cg = (color >> 8) & 0xFF, cb = color & 0xFF;
        for (var y = y0; y < y1; y++)
        {
            var sy = (y + 0.5f - oy) * inv - 0.5f;
            if (sy < -0.5f || sy > sh - 0.5f) continue;
            var row = dst.Slice(y * w, w);
            for (var x = x0; x < x1; x++)
            {
                var sx = (x + 0.5f - ox) * inv - 0.5f;
                if (sx < -0.5f || sx > sw - 0.5f) continue;
                int la, fa;
                if (nearest)
                {
                    var o = Math.Clamp((int)(sy + 0.5f), 0, sh - 1) * sw + Math.Clamp((int)(sx + 0.5f), 0, sw - 1);
                    la = line[o]; fa = fill[o];
                }
                else
                {
                    var ix = Math.Clamp((int)MathF.Floor(sx), 0, sw - 1); var iy = Math.Clamp((int)MathF.Floor(sy), 0, sh - 1);
                    var ix1 = Math.Min(ix + 1, sw - 1); var iy1 = Math.Min(iy + 1, sh - 1);
                    var fx = Math.Clamp(sx - ix, 0f, 1f); var fy = Math.Clamp(sy - iy, 0f, 1f);
                    la = (int)Bil(line[iy * sw + ix], line[iy * sw + ix1], line[iy1 * sw + ix], line[iy1 * sw + ix1], fx, fy);
                    fa = (int)Bil(fill[iy * sw + ix], fill[iy * sw + ix1], fill[iy1 * sw + ix], fill[iy1 * sw + ix1], fx, fy);
                }
                if (la == 0) continue;
                var p = row[x];
                uint r = (p >> 16) & 0xFF, g = (p >> 8) & 0xFF, b = p & 0xFF;
                var il = 255 - la;                                   // 외곽선(검정) over
                r = (uint)(r * il + 127) / 255; g = (uint)(g * il + 127) / 255; b = (uint)(b * il + 127) / 255;
                if (fa > 0)
                {
                    var ifa = (uint)(255 - fa);                      // 채움(틴트) over
                    r = (cr * (uint)fa + r * ifa + 127) / 255; g = (cg * (uint)fa + g * ifa + 127) / 255; b = (cb * (uint)fa + b * ifa + 127) / 255;
                }
                row[x] = 0xFF000000u | r << 16 | g << 8 | b;
            }
        }
    }

    private static float Bil(byte a, byte b, byte c, byte d, float fx, float fy)
    {
        var top = a + (b - a) * fx; var bot = c + (d - c) * fx;
        return top + (bot - top) * fy + 0.5f;
    }

    public static string PresetName(HormoziPreset p) => p switch { HormoziPreset.HormoziYellow => "호르모지 옐로우", HormoziPreset.NeonCyber => "네온 사이버", _ => "미니멀 화이트" };
}
