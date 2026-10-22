using System.Diagnostics;

namespace PromptFlowStudio.Services.LiveEngagement;

/// <summary>v17.86.0 정수 픽셀 사각형(포함 X/Y · 배타 X+W/Y+H).</summary>
public struct PixelRect
{
    public int X, Y, W, H;
    public PixelRect(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
    public readonly int Right => X + W;
    public readonly int Bottom => Y + H;
    public readonly bool IsEmpty => W <= 0 || H <= 0;
    public readonly long Area => IsEmpty ? 0 : (long)W * H;
    public readonly bool Intersects(in PixelRect o) => !IsEmpty && !o.IsEmpty && X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public static PixelRect Union(in PixelRect a, in PixelRect b)
    {
        if (a.IsEmpty) return b;
        if (b.IsEmpty) return a;
        int x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new PixelRect(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    public static PixelRect Clip(in PixelRect r, int w, int h)
    {
        int x0 = Math.Max(0, r.X), y0 = Math.Max(0, r.Y), x1 = Math.Min(w, r.Right), y1 = Math.Min(h, r.Bottom);
        return x1 <= x0 || y1 <= y0 ? default : new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }
}

/// <summary>
/// [PromptFlow Studio v17.86.0] 무할당 실시간 알파 오버레이 합성기(Porter-Duff Over).
///   ▸ 픽셀 = 32-bit RGBA(리틀엔디언 바이트 R,G,B,A → <c>uint</c> 0xAABBGGRR) · 오버레이 캔버스는 프리멀티플라이드 알파.
///   ▸ 위젯은 <see cref="AddRegion"/> 로 경계 박스를 등록하고 프리미티브(사각형 · 원 · 링)를 캔버스에 그린다 → 직전 프레임 영역만 국소 소거.
///   ▸ <see cref="Composite"/>: 등록 영역 병합(겹침 = 합집합 · 이중 블렌딩 방지) → 영역 내부만 dst = src + dst·(1 − αs) 정수 블렌딩 · 전 경로 무할당.
///   ▸ 1080p 60 fps 예산 — 프레임당 합성 µs 를 계측해 <see cref="BudgetUs"/>(1.5 ms) 초과 횟수를 집계.
/// </summary>
public sealed class ZeroAllocAlphaOverlayCompositorEngine
{
    public const int Width = 1920, Height = 1080, MaxRegions = 16;
    public const double BudgetUs = 1500;

    private readonly PixelRect[] _regions = new PixelRect[MaxRegions];
    private readonly PixelRect[] _prev = new PixelRect[MaxRegions];
    private readonly PixelRect[] _merged = new PixelRect[MaxRegions];
    private uint[]? _canvas;
    private int _regionN, _prevN, _mergedN;
    private double _sumUs;

    public long Frames { get; private set; }
    public long OverBudget { get; private set; }
    public long BlendedPixels { get; private set; }
    public long OpaqueSkips { get; private set; }
    public double LastUs { get; private set; }
    public double MaxUs { get; private set; }
    public double AvgUs => Frames == 0 ? 0 : _sumUs / Frames;
    public int RegionCount => _regionN;
    public ref readonly PixelRect Region(int i) => ref _regions[i];
    /// <summary>직전 합성 영역(소스 비디오 복원 대상 — 이번 프레임에서 사라진 위젯 잔상 제거용).</summary>
    public int PrevCount => _prevN;
    public ref readonly PixelRect Prev(int i) => ref _prev[i];
    public int MergedCount => _mergedN;
    public ref readonly PixelRect Merged(int i) => ref _merged[i];
    /// <summary>직전 합성의 더티 면적 / 전체 프레임 면적.</summary>
    public float DirtyRatio { get; private set; }

    /// <summary>오버레이 캔버스(지연 1 회 할당 · 8 MB).</summary>
    public Span<uint> Canvas => (_canvas ??= new uint[Width * Height]).AsSpan();

    public static uint Rgba(byte r, byte g, byte b, byte a) => r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

    /// <summary>스트레이트 RGBA → 프리멀티플라이드.</summary>
    public static uint Premul(uint c)
    {
        var a = c >> 24;
        if (a == 255) return c;
        uint r = Mul(c & 0xFF, a), g = Mul((c >> 8) & 0xFF, a), b = Mul((c >> 16) & 0xFF, a);
        return r | g << 8 | b << 16 | a << 24;
    }

    /// <summary>프리멀티플라이드 색상에 0~255 투명도 계수 적용.</summary>
    public static uint Fade(uint premul, uint k)
    {
        if (k >= 255) return premul;
        return Mul(premul & 0xFF, k) | Mul((premul >> 8) & 0xFF, k) << 8 | Mul((premul >> 16) & 0xFF, k) << 16 | Mul(premul >> 24, k) << 24;
    }

    /// <summary>x·y/255 정수 근사(오차 ≤ 1).</summary>
    private static uint Mul(uint x, uint y) { var t = x * y + 128; return (t + (t >> 8)) >> 8; }

    /// <summary>프리멀티플라이드 Over: src + dst·(1 − αs) — 4 채널 패킹 2 회 곱셈.</summary>
    public static uint Over(uint src, uint dst)
    {
        var inv = 255 - (src >> 24);
        if (inv == 0) return src;
        var rb = (dst & 0x00FF00FFu) * inv + 0x00800080u;
        rb = ((rb + ((rb >> 8) & 0x00FF00FFu)) >> 8) & 0x00FF00FFu;
        var ga = ((dst >> 8) & 0x00FF00FFu) * inv + 0x00800080u;
        ga = (ga + ((ga >> 8) & 0x00FF00FFu)) & 0xFF00FF00u;
        return src + (rb | ga);
    }

    // ══════════════════════ 프레임 오버레이 기록 ══════════════════════

    /// <summary>새 오버레이 프레임 — 직전 프레임 영역만 국소 소거(전체 8 MB 소거 없음).</summary>
    public void BeginOverlay()
    {
        var c = Canvas;
        for (var i = 0; i < _prevN; i++)
        {
            ref readonly var r = ref _prev[i];
            for (var y = r.Y; y < r.Bottom; y++) c.Slice(y * Width + r.X, r.W).Clear();
        }
        _regionN = 0;
    }

    /// <summary>위젯 경계 박스 등록(클리핑 · 상한 초과 시 마지막 영역과 합집합). 반환 = 클리핑된 영역.</summary>
    public PixelRect AddRegion(in PixelRect r)
    {
        var c = PixelRect.Clip(r, Width, Height);
        if (c.IsEmpty) return c;
        if (_regionN < MaxRegions) _regions[_regionN++] = c;
        else _regions[MaxRegions - 1] = PixelRect.Union(_regions[MaxRegions - 1], c);
        return c;
    }

    /// <summary>캔버스 사각형 칠(프리멀티플라이드 Over).</summary>
    public void FillRect(in PixelRect r, uint premul)
    {
        var c = PixelRect.Clip(r, Width, Height);
        if (c.IsEmpty || premul == 0) return;
        var cv = Canvas;
        for (var y = c.Y; y < c.Bottom; y++)
        {
            var row = cv.Slice(y * Width + c.X, c.W);
            if (premul >> 24 == 255) row.Fill(premul);
            else for (var x = 0; x < row.Length; x++) row[x] = Over(premul, row[x]);
        }
    }

    /// <summary>안티앨리어스 원(가장자리 1 px 커버리지 감쇠).</summary>
    public void FillCircle(float cx, float cy, float radius, uint premul)
    {
        if (radius <= 0.25f || premul == 0) return;
        int x0 = Math.Max(0, (int)(cx - radius - 1)), x1 = Math.Min(Width - 1, (int)(cx + radius + 1));
        int y0 = Math.Max(0, (int)(cy - radius - 1)), y1 = Math.Min(Height - 1, (int)(cy + radius + 1));
        if (x1 < x0 || y1 < y0) return;
        var cv = Canvas;
        var rIn = Math.Max(0f, radius - 0.5f); var rIn2 = rIn * rIn; var rOut = radius + 0.5f; var rOut2 = rOut * rOut;
        for (var y = y0; y <= y1; y++)
        {
            var dy = y + 0.5f - cy; var dy2 = dy * dy;
            if (dy2 > rOut2) continue;
            var row = y * Width;
            for (var x = x0; x <= x1; x++)
            {
                var dx = x + 0.5f - cx; var d2 = dx * dx + dy2;
                if (d2 >= rOut2) continue;
                var src = d2 <= rIn2 ? premul : Fade(premul, (uint)(255f * (rOut - MathF.Sqrt(d2))));
                cv[row + x] = Over(src, cv[row + x]);
            }
        }
    }

    // ══════════════════════ 합성(무할당 핫패스) ══════════════════════

    /// <summary>영역 병합 → 비디오 프레임(stride = <see cref="Width"/>)에 국소 Over 합성. 반환 = 블렌딩 픽셀 수.</summary>
    public long Composite(Span<uint> frame)
    {
        if (frame.Length < Width * Height) return 0;
        var t0 = Stopwatch.GetTimestamp();
        MergeRegions();
        var cv = Canvas;
        long px = 0, skips = 0, area = 0;
        for (var i = 0; i < _mergedN; i++)
        {
            ref readonly var r = ref _merged[i];
            area += r.Area;
            for (var y = r.Y; y < r.Bottom; y++)
            {
                var o = y * Width + r.X;
                var src = cv.Slice(o, r.W);
                var dst = frame.Slice(o, r.W);
                for (var x = 0; x < src.Length; x++)
                {
                    var s = src[x];
                    if (s == 0) continue;
                    if (s >> 24 == 255) { dst[x] = s; skips++; }
                    else dst[x] = Over(s, dst[x]);
                    px++;
                }
            }
        }
        // 이번 영역 → 다음 프레임 소거 대상
        for (var i = 0; i < _regionN; i++) _prev[i] = _regions[i];
        _prevN = _regionN;
        var us = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        LastUs = us; if (us > MaxUs) MaxUs = us;
        _sumUs += us; Frames++;
        if (us > BudgetUs) OverBudget++;
        BlendedPixels = px; OpaqueSkips += skips;
        DirtyRatio = (float)(area / (double)(Width * Height));
        return px;
    }

    /// <summary>겹치는 영역 합집합 반복(n ≤ 16 · O(n²)).</summary>
    private void MergeRegions()
    {
        _mergedN = 0;
        for (var i = 0; i < _regionN; i++) _merged[_mergedN++] = _regions[i];
        bool changed;
        do
        {
            changed = false;
            for (var i = 0; i < _mergedN && !changed; i++)
                for (var j = i + 1; j < _mergedN; j++)
                {
                    if (!_merged[i].Intersects(_merged[j])) continue;
                    _merged[i] = PixelRect.Union(_merged[i], _merged[j]);
                    _merged[j] = _merged[--_mergedN];
                    changed = true;
                    break;
                }
        } while (changed);
    }

    public void ResetStats()
    {
        Frames = OverBudget = BlendedPixels = OpaqueSkips = 0;
        LastUs = MaxUs = _sumUs = 0;
        DirtyRatio = 0;
    }

    /// <summary>캔버스 · 영역 전체 초기화(비핫패스).</summary>
    public void Reset()
    {
        if (_canvas is not null) Array.Clear(_canvas);
        _regionN = _prevN = _mergedN = 0;
        ResetStats();
    }
}
