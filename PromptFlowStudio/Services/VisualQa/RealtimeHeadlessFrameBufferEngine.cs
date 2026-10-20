#pragma warning disable BL0006 // RenderTree 프레임 직접 순회(헤드리스 구조 래스터 — 의도적 사용)
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using PromptFlowStudio.Services.MotionMatching;

namespace PromptFlowStudio.Services.VisualQa;

/// <summary>픽셀 사각형(클립 영역).</summary>
public readonly record struct PixelRect(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
}

/// <summary>원근 카메라(look-at · 수직 FOV) → 뷰포트 픽셀 투영.</summary>
public readonly struct Camera3D
{
    private readonly Vector3 _eye, _r, _u, _f;
    private readonly float _focal;

    public Camera3D(Vector3 eye, Vector3 target, float fovDeg, PixelRect viewport)
    {
        _eye = eye;
        _f = Vector3.Normalize(target - eye);
        _r = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, _f));
        _u = Vector3.Cross(_f, _r);
        Clip = viewport;
        _focal = viewport.H * 0.5f / MathF.Tan(fovDeg * MathF.PI / 360f);
    }

    public PixelRect Clip { get; }

    /// <summary>월드 → 픽셀(근평면 0.05 m 클램프 · 반환 = 깊이).</summary>
    public float Project(Vector3 w, out float x, out float y)
    {
        var d = w - _eye;
        var z = MathF.Max(Vector3.Dot(d, _f), 0.05f);
        x = Clip.X + Clip.W * 0.5f + Vector3.Dot(d, _r) * _focal / z;
        y = Clip.Y + Clip.H * 0.5f - Vector3.Dot(d, _u) * _focal / z;
        return z;
    }
}

/// <summary>
/// RGBA(0xAARRGGBB 리틀엔디안 = 바이트 B,G,R,A) 소프트웨어 래스터 캔버스(구조체 · 무할당).
/// 불투명 채움 = <see cref="Span{T}.Fill"/>(내부 벡터화) · 반투명 = 정수 알파 블렌드 · 선 = Bresenham(두께) · 삼각형 = 에지 함수 · 원 · 3×5 비트맵 글꼴(×배율).
/// </summary>
public readonly struct HeadlessCanvas
{
    private static readonly ushort[] Glyphs = BuildGlyphs();
    private readonly uint[] _px;

    public HeadlessCanvas(uint[] px, int w, int h) { _px = px; W = w; H = h; }

    public int W { get; }
    public int H { get; }
    public uint[] Pixels => _px;
    public PixelRect Bounds => new(0, 0, W, H);

    public void Clear(uint c) => _px.AsSpan(0, W * H).Fill(c);

    public void FillRect(int x, int y, int w, int h, uint c) => FillRect(x, y, w, h, c, Bounds);

    public void FillRect(int x, int y, int w, int h, uint c, PixelRect clip)
    {
        int x0 = Math.Max(x, Math.Max(0, clip.X)), y0 = Math.Max(y, Math.Max(0, clip.Y));
        int x1 = Math.Min(x + w, Math.Min(W, clip.Right)), y1 = Math.Min(y + h, Math.Min(H, clip.Bottom));
        if (x1 <= x0 || y1 <= y0) return;
        var a = c >> 24;
        for (var yy = y0; yy < y1; yy++)
        {
            var row = _px.AsSpan(yy * W + x0, x1 - x0);
            if (a == 255) row.Fill(c);
            else for (var i = 0; i < row.Length; i++) row[i] = Blend(row[i], c);
        }
    }

    public void StrokeRect(int x, int y, int w, int h, uint c)
    {
        FillRect(x, y, w, 1, c); FillRect(x, y + h - 1, w, 1, c);
        FillRect(x, y, 1, h, c); FillRect(x + w - 1, y, 1, h, c);
    }

    public static uint Blend(uint dst, uint src)
    {
        var a = src >> 24;
        if (a == 255) return src;
        if (a == 0) return dst;
        var ia = 255 - a;
        var rb = ((src & 0x00FF00FFu) * a + (dst & 0x00FF00FFu) * ia) >> 8 & 0x00FF00FFu;
        var g = ((src & 0x0000FF00u) * a + (dst & 0x0000FF00u) * ia) >> 8 & 0x0000FF00u;
        return 0xFF000000u | rb | g;
    }

    public void Plot(int x, int y, uint c, PixelRect clip)
    {
        if (x < clip.X || y < clip.Y || x >= clip.Right || y >= clip.Bottom || x < 0 || y < 0 || x >= W || y >= H) return;
        ref var p = ref _px[y * W + x];
        p = c >> 24 == 255 ? c : Blend(p, c);
    }

    public void Line(float x0, float y0, float x1, float y1, uint c, int thick, PixelRect clip)
    {
        if (!float.IsFinite(x0 + y0 + x1 + y1)) return;
        int ax = (int)MathF.Round(x0), ay = (int)MathF.Round(y0), bx = (int)MathF.Round(x1), by = (int)MathF.Round(y1);
        if (Math.Abs(bx - ax) > 8192 || Math.Abs(by - ay) > 8192) return;
        int dx = Math.Abs(bx - ax), dy = -Math.Abs(by - ay), sx = ax < bx ? 1 : -1, sy = ay < by ? 1 : -1, err = dx + dy;
        var r = thick / 2;
        while (true)
        {
            if (thick <= 1) Plot(ax, ay, c, clip);
            else FillRect(ax - r, ay - r, thick, thick, c, clip);
            if (ax == bx && ay == by) break;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; ax += sx; }
            if (e2 <= dx) { err += dx; ay += sy; }
        }
    }

    public void FillCircle(float cx, float cy, float r, uint c, PixelRect clip)
    {
        if (!float.IsFinite(cx + cy)) return;
        int y0 = (int)MathF.Floor(cy - r), y1 = (int)MathF.Ceiling(cy + r);
        for (var y = y0; y <= y1; y++)
        {
            var dy = y - cy;
            var h = r * r - dy * dy;
            if (h < 0f) continue;
            var dx = MathF.Sqrt(h);
            FillRect((int)MathF.Round(cx - dx), y, (int)MathF.Round(2f * dx) + 1, 1, c, clip);
        }
    }

    public void FillTriangle(float x0, float y0, float x1, float y1, float x2, float y2, uint c, PixelRect clip)
    {
        if (!float.IsFinite(x0 + y0 + x1 + y1 + x2 + y2)) return;
        int minX = (int)MathF.Max(MathF.Floor(MathF.Min(x0, MathF.Min(x1, x2))), MathF.Max(0, clip.X));
        int maxX = (int)MathF.Min(MathF.Ceiling(MathF.Max(x0, MathF.Max(x1, x2))), MathF.Min(W, clip.Right) - 1);
        int minY = (int)MathF.Max(MathF.Floor(MathF.Min(y0, MathF.Min(y1, y2))), MathF.Max(0, clip.Y));
        int maxY = (int)MathF.Min(MathF.Ceiling(MathF.Max(y0, MathF.Max(y1, y2))), MathF.Min(H, clip.Bottom) - 1);
        if (maxX < minX || maxY < minY || (maxX - minX) * (maxY - minY) > 4_000_000) return;
        var area = (x1 - x0) * (y2 - y0) - (y1 - y0) * (x2 - x0);
        if (MathF.Abs(area) < 1e-6f) return;
        var sgn = area > 0f ? 1f : -1f;
        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5f;
                var w0 = ((x1 - px) * (y2 - py) - (y1 - py) * (x2 - px)) * sgn;
                var w1 = ((x2 - px) * (y0 - py) - (y2 - py) * (x0 - px)) * sgn;
                var w2 = ((x0 - px) * (y1 - py) - (y0 - py) * (x1 - px)) * sgn;
                if (w0 >= 0f && w1 >= 0f && w2 >= 0f)
                {
                    ref var d = ref _px[y * W + x];
                    d = c >> 24 == 255 ? c : Blend(d, c);
                }
            }
        }
    }

    /// <summary>3×5 비트맵 글꼴(ASCII 대문자/숫자/기호 · 소문자 → 대문자 · 비ASCII = 반투명 블록) · 반환 = 끝 x.</summary>
    public int DrawText(int x, int y, ReadOnlySpan<char> text, uint c, int scale, PixelRect clip)
    {
        var adv = 4 * scale;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == ' ') { x += adv; continue; }
            var g = GlyphOf(ch);
            var col = g == 0x7FFF && ch > 127 ? (c & 0x00FFFFFFu) | 0x66000000u : c;
            for (var gy = 0; gy < 5; gy++)
                for (var gx = 0; gx < 3; gx++)
                    if ((g >> (gy * 3 + gx) & 1) != 0) FillRect(x + gx * scale, y + gy * scale, scale, scale, col, clip);
            x += adv;
        }
        return x;
    }

    public static int TextWidth(int chars, int scale) => chars * 4 * scale;

    private static ushort GlyphOf(char ch)
    {
        if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 32);
        return ch is >= ' ' and <= 'Z' ? Glyphs[ch - ' '] : (ushort)0x7FFF;
    }

    private static ushort[] BuildGlyphs()
    {
        // 행 5개(상→하) × 열 3개 · 문자 ' '(0x20) ~ 'Z'(0x5A)
        string[] rows =
        {
            "...|...|...|...|...", ".#.|.#.|.#.|...|.#.", "#.#|#.#|...|...|...", "#.#|###|#.#|###|#.#", "###|##.|###|.##|###", "#..|..#|.#.|#..|..#",
            ".#.|#.#|.#.|#.#|.##", ".#.|.#.|...|...|...", "..#|.#.|.#.|.#.|..#", "#..|.#.|.#.|.#.|#..", "#.#|.#.|###|.#.|#.#", "...|.#.|###|.#.|...",
            "...|...|...|.#.|#..", "...|...|###|...|...", "...|...|...|...|.#.", "..#|..#|.#.|#..|#..",
            "###|#.#|#.#|#.#|###", ".#.|##.|.#.|.#.|###", "###|..#|###|#..|###", "###|..#|###|..#|###", "#.#|#.#|###|..#|..#", "###|#..|###|..#|###",
            "###|#..|###|#.#|###", "###|..#|.#.|.#.|.#.", "###|#.#|###|#.#|###", "###|#.#|###|..#|###",
            "...|.#.|...|.#.|...", "...|.#.|...|.#.|#..", "..#|.#.|#..|.#.|..#", "...|###|...|###|...", "#..|.#.|..#|.#.|#..", "###|..#|.##|...|.#.",
            "###|#.#|###|#..|###",
            ".#.|#.#|###|#.#|#.#", "##.|#.#|##.|#.#|##.", ".##|#..|#..|#..|.##", "##.|#.#|#.#|#.#|##.", "###|#..|##.|#..|###", "###|#..|##.|#..|#..",
            ".##|#..|#.#|#.#|.##", "#.#|#.#|###|#.#|#.#", "###|.#.|.#.|.#.|###", "..#|..#|..#|#.#|.#.", "#.#|#.#|##.|#.#|#.#", "#..|#..|#..|#..|###",
            "#.#|###|###|#.#|#.#", "##.|#.#|#.#|#.#|#.#", ".#.|#.#|#.#|#.#|.#.", "##.|#.#|##.|#..|#..", ".#.|#.#|#.#|##.|.##", "##.|#.#|##.|#.#|#.#",
            ".##|#..|.#.|..#|##.", "###|.#.|.#.|.#.|.#.", "#.#|#.#|#.#|#.#|###", "#.#|#.#|#.#|#.#|.#.", "#.#|#.#|###|###|#.#", "#.#|#.#|.#.|#.#|#.#",
            "#.#|#.#|.#.|.#.|.#.", "###|..#|.#.|#..|###",
        };
        var g = new ushort[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var bits = 0; var r = rows[i].Split('|');
            for (var y = 0; y < 5; y++)
                for (var x = 0; x < 3; x++)
                    if (r[y][x] == '#') bits |= 1 << (y * 3 + x);
            g[i] = (ushort)bits;
        }
        return g;
    }
}

/// <summary>UI 구조 래스터 표시 목록 항목.</summary>
public struct UiBox
{
    public int X, Y, W, H, Depth, TextStart, TextLength;
    public uint Fill, Border;
    public byte Kind;   // 0 블록 · 1 인라인 · 2 텍스트 · 3 SVG/마크업 · 4 입력
}

/// <summary>
/// [PromptFlow Studio v17.34.0] 🖥 실시간 헤드리스 오프스크린 프레임버퍼 &amp; UI 캡처 엔진(1920×1080 RGBA · 링 3 · <see cref="Render"/> 무할당).
///   ▸ UI: 실제 Blazor 컴포넌트를 헤드리스 <see cref="Renderer"/> 서브클래스로 마운트(실 DI · IJSRuntime 스텁) → RenderTree(자식 컴포넌트 재귀) 구조 레이아웃
///     (블록 = 줄바꿈 · 인라인 = 흐름 · 텍스트 3×5 글꼴 ×2 줄바꿈 · class 해시 색 · style/script 생략 · display:none 생략) → 표시 목록 → UI 레이어 래스터(제어 평면 · 변경 시에만).
///     ※ 브라우저 CSS 엔진이 아닌 구조 래스터 — 요소 추가/삭제 · 순서 · 텍스트 길이 · 클래스 변경 등 레이아웃 붕괴를 검출(픽셀 단위 WebView2 동일 렌더 아님).
///   ▸ 3D: v17.33 모션 매칭 액터 · 지형 격자 · 궤적 호 → 원근 카메라(우상단 640×540) + ZMP 오버레이(우하단 탑뷰) — 핫패스 무할당.
///   ▸ 프레임: 링 슬롯 = UI 레이어 Span 복사(memmove 벡터화) → 3D 뷰포트 + 분석 프로브(오버레이 제외 AOV 320×270) → 오버레이 → 게시(시퀀스).
///   ▸ 결함 주입(검증용): 레이아웃 붕괴(화면 1/3 지점 이후 표시 목록 40 px 이동) · 3D 팝핑(1 프레임 80 cm 순간이동).
/// </summary>
public sealed class RealtimeHeadlessFrameBufferEngine
{
    public const int W = 1920, H = 1080, Ring = 3, MaxBoxes = 6000, MaxText = 1 << 17, UiWidth = 1280;
    public const float PopMeters = 0.8f;
    public const int ProbeW = 320, ProbeH = 270;
    public static readonly PixelRect UiRegion = new(0, 0, UiWidth, H);
    public static readonly PixelRect SceneRegion = new(UiWidth, 0, W - UiWidth, 540);
    public static readonly PixelRect InsetRegion = new(UiWidth, 540, W - UiWidth, 540);
    private static readonly uint[] Palette = { 0xFF1E3A8Au, 0xFF14532Du, 0xFF7C2D12u, 0xFF581C87u, 0xFF134E4Au, 0xFF713F12u, 0xFF831843u, 0xFF1E293Bu, 0xFF3F6212u, 0xFF0C4A6Eu, 0xFF4C1D95u, 0xFF7F1D1Du };

    private readonly uint[][] _ring = new uint[Ring][];
    private readonly long[] _slotSeq = new long[Ring];
    private readonly uint[] _ui = new uint[W * H], _probe = new uint[ProbeW * ProbeH];
    private readonly UiBox[] _boxes = new UiBox[MaxBoxes];
    private readonly char[] _text = new char[MaxText];
    private readonly Vector3[] _pose = new Vector3[BipedSkeleton.Count], _arc = new Vector3[NeuralMotionMatchingStudioRunner.ArcPoints];
    private readonly float[] _grid = new float[NeuralMotionMatchingStudioRunner.Grid * NeuralMotionMatchingStudioRunner.Grid];
    private readonly object _uiGate = new();
    private int _boxCount, _textCount, _head = -1, _faultFrom = -1;
    private long _seq;
    private bool _popPending;

    private static readonly (int A, int B, uint C)[] Bones =
    {
        (0, 1, 0xFFA3E635u), (1, 2, 0xFFA3E635u), (2, 3, 0xFFA3E635u), (0, 4, 0xFF38BDF8u), (4, 5, 0xFF38BDF8u), (5, 6, 0xFF38BDF8u), (6, 7, 0xFF38BDF8u),
        (0, 8, 0xFFF472B6u), (8, 9, 0xFFF472B6u), (9, 10, 0xFFF472B6u), (10, 11, 0xFFF472B6u), (2, 12, 0xFF38BDF8u), (12, 13, 0xFF38BDF8u), (13, 14, 0xFF38BDF8u),
        (2, 15, 0xFFF472B6u), (15, 16, 0xFFF472B6u), (16, 17, 0xFFF472B6u),
    };

    public RealtimeHeadlessFrameBufferEngine()
    {
        for (var i = 0; i < Ring; i++) _ring[i] = new uint[W * H];
        new HeadlessCanvas(_ui, W, H).Clear(0xFF0F172Au);
        RasterUiLayer();
    }

    public long FramesRendered => Interlocked.Read(ref _seq);
    public int LatestSlot => Volatile.Read(ref _head);
    public double LastRenderUs { get; private set; }
    public double MaxRenderUs { get; private set; }
    public int BoxCount => _boxCount;
    public string? CapturedComponent { get; private set; }
    public double LastCaptureMs { get; private set; }
    public int CapturedComponents { get; private set; }
    public string? CaptureError { get; private set; }
    public bool LayoutFaultActive => _faultFrom >= 0;

    /// <summary>최신 게시 프레임(읽기 전용 · 다음 2 프레임 동안 유효).</summary>
    public ReadOnlySpan<uint> Latest => _head < 0 ? ReadOnlySpan<uint>.Empty : _ring[_head];
    public uint[] LatestArray => _ring[Math.Max(0, _head)];
    /// <summary>3D 분석 프로브(오버레이 제외 AOV · 최신 프레임과 동기).</summary>
    public ReadOnlySpan<uint> Probe => _probe;

    public void CopyLatest(Span<uint> dst) { var h = Volatile.Read(ref _head); if (h >= 0) _ring[h].AsSpan().CopyTo(dst); }

    public void ResetTiming() => MaxRenderUs = 0;

    // ══════════════════════ 결함 주입 ══════════════════════

    /// <summary>레이아웃 붕괴 주입: 화면 1/3 지점 이후 표시 목록을 40 px 아래 · 24 px 오른쪽으로 이동(겹침 · 밀림) → UI 레이어 재래스터.</summary>
    public void InjectLayoutFault(bool on)
    {
        lock (_uiGate) { _faultFrom = on ? FaultStart() : -1; RasterUiLayer(); }
    }

    /// <summary>화면 내(y ∈ [H/3, H)) 첫 표시 목록 항목 — 이후 전부 이동.</summary>
    private int FaultStart()
    {
        for (var i = 0; i < _boxCount; i++) if (_boxes[i].Y >= H / 3 && _boxes[i].Y < H) return i;
        return _boxCount / 2;
    }

    /// <summary>다음 1 프레임 액터 80 cm 순간이동(팝핑 결함).</summary>
    public void InjectPop() => _popPending = true;

    // ══════════════════════ 프레임 합성(무할당 핫패스) ══════════════════════

    /// <summary>UI 레이어 → 링 슬롯 → 3D 뷰포트 · ZMP 오버레이 → 게시.</summary>
    public void Render(NeuralMotionMatchingStudioRunner motion, ZmpStairBalanceVisualDebuggingGuard? zmp)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var slot = (_head + 1 + Ring) % Ring;
        var px = _ring[slot];
        lock (_uiGate) _ui.AsSpan().CopyTo(px);
        var c = new HeadlessCanvas(px, W, H);

        motion.CopyPose(_pose); motion.CopyArc(_arc); motion.CopyGrid(_grid);
        var root = motion.RootPoint; var heading = motion.RootHeading; var gc = motion.GridCenter;
        if (_popPending)
        {
            var off = BipedSkeleton.Right(heading) * PopMeters;
            for (var j = 0; j < _pose.Length; j++) _pose[j] += off;
            _popPending = false;
        }
        var target = new Vector3(root.X, _pose[0].Y - 0.3f, root.Z);
        var eye = target + new Vector3(MathF.Sin(heading + 0.9f) * 3.6f, 1.6f, MathF.Cos(heading + 0.9f) * 3.6f);
        var cam = new Camera3D(eye, target, 50f, SceneRegion);
        c.FillRect(SceneRegion.X, SceneRegion.Y, SceneRegion.W, SceneRegion.H, 0xFF020617u);

        // 지형 격자
        const int n = NeuralMotionMatchingStudioRunner.Grid; const float st = NeuralMotionMatchingStudioRunner.GridStep;
        for (var j = 0; j < n; j++)
            for (var i = 0; i < n; i++)
            {
                var p = new Vector3(gc.X + (i - n / 2) * st, _grid[j * n + i], gc.Y + (j - n / 2) * st);
                cam.Project(p, out var ax, out var ay);
                if (i + 1 < n) { cam.Project(new Vector3(p.X + st, _grid[j * n + i + 1], p.Z), out var bx, out var by); c.Line(ax, ay, bx, by, 0xFF334155u, 1, cam.Clip); }
                if (j + 1 < n) { cam.Project(new Vector3(p.X, _grid[(j + 1) * n + i], p.Z + st), out var bx, out var by); c.Line(ax, ay, bx, by, 0xFF334155u, 1, cam.Clip); }
            }
        // 궤적 호
        cam.Project(new Vector3(root.X, _arc[0].Y, root.Z), out var px0, out var py0);
        for (var i = 0; i < _arc.Length; i++)
        {
            cam.Project(_arc[i], out var qx, out var qy);
            c.Line(px0, py0, qx, qy, 0xFFFACC15u, 2, cam.Clip);
            px0 = qx; py0 = qy;
        }
        // 분석 프로브(AOV): 오버레이 없는 지형 + 골격 ½ 해상도 — 팝핑 판독 전용
        var pc = new HeadlessCanvas(_probe, ProbeW, ProbeH);
        pc.Clear(0xFF020617u);
        var pcam = new Camera3D(eye, target, 50f, new PixelRect(0, 0, ProbeW, ProbeH));
        for (var j = 0; j < n; j += 2)
            for (var i = 0; i + 1 < n; i++)
            {
                pcam.Project(new Vector3(gc.X + (i - n / 2) * st, _grid[j * n + i], gc.Y + (j - n / 2) * st), out var ax, out var ay);
                pcam.Project(new Vector3(gc.X + (i + 1 - n / 2) * st, _grid[j * n + i + 1], gc.Y + (j - n / 2) * st), out var bx, out var by);
                pc.Line(ax, ay, bx, by, 0xFF334155u, 1, pcam.Clip);
            }
        foreach (var (a, b, col) in Bones)
        {
            pcam.Project(_pose[a], out var ax, out var ay); pcam.Project(_pose[b], out var bx, out var by);
            pc.Line(ax, ay, bx, by, col, 2, pcam.Clip);
        }
        pcam.Project(_pose[3], out var phx, out var phy);
        pc.FillCircle(phx, phy, 4, 0xFFF8FAFCu, pcam.Clip);

        if (zmp is not null) zmp.DrawOverlay(c, cam, motion.Ik, root, heading, InsetRegion.X + 10, InsetRegion.Y + 10, InsetRegion.H - 20);
        // 골격
        foreach (var (a, b, col) in Bones)
        {
            cam.Project(_pose[a], out var ax, out var ay); cam.Project(_pose[b], out var bx, out var by);
            c.Line(ax, ay, bx, by, col, 3, cam.Clip);
        }
        cam.Project(_pose[3], out var hx, out var hy);
        c.FillCircle(hx, hy, 9, 0xFFF8FAFCu, cam.Clip);
        c.DrawText(SceneRegion.X + 10, SceneRegion.Y + 10, "HEADLESS 3D VIEWPORT", 0xFF94A3B8u, 2, cam.Clip);

        var seq = Interlocked.Increment(ref _seq);
        _slotSeq[slot] = seq;
        Volatile.Write(ref _head, slot);
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastRenderUs = us;
        if (us > MaxRenderUs && seq > 3) MaxRenderUs = us;
    }

    // ══════════════════════ Blazor 헤드리스 캡처(제어 평면) ══════════════════════

    /// <summary>실 Blazor 컴포넌트 마운트 → RenderTree 구조 레이아웃 → UI 레이어 래스터. 실패 시 이전 레이어 보존.</summary>
    public async Task<bool> CaptureComponentAsync(Type componentType, IServiceProvider services, CancellationToken ct = default)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var r = new CaptureRenderer(new CaptureServices(services));
        try
        {
            ct.ThrowIfCancellationRequested();
            var root = await r.Dispatcher.InvokeAsync(() => r.Mount(componentType)).ConfigureAwait(false);
            await r.Dispatcher.InvokeAsync(() => r.RenderRoot(root, ParameterView.Empty)).ConfigureAwait(false);
            if (r.TakeError() is { } err) throw err;
            await r.Dispatcher.InvokeAsync(() =>
            {
                lock (_uiGate)
                {
                    _boxCount = 0; _textCount = 0;
                    var ctx = new Flow(24, 64, UiWidth - 48);
                    AddBox(16, 16, UiWidth - 32, 32, 0, 0xFF1E293Bu, 0xFF475569u, 0, componentType.Name);
                    LayoutComponent(r, root, ref ctx, 1);
                    if (_faultFrom >= 0) _faultFrom = FaultStart();
                    RasterUiLayer();
                }
            }).ConfigureAwait(false);
            CapturedComponent = componentType.Name; CaptureError = null; CapturedComponents++;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CaptureError = componentType.Name + ": " + ex.GetType().Name + " " + ex.Message;
            return false;
        }
        finally
        {
            try { await r.Dispatcher.InvokeAsync(async () => await r.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or AggregateException) { PromptFlowStudio.Program.Log("VisualQa.Capture.Dispose", ex); }
            LastCaptureMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private struct Flow
    {
        public int X, Y, Width, LineX, LineH;
        public Flow(int x, int y, int w) { X = x; Y = y; Width = w; LineX = x; LineH = 0; }
        public void Break() { if (LineX > X) { Y += LineH + 4; LineX = X; LineH = 0; } }
    }

    private static bool IsBlock(string e) => e switch
    {
        "div" or "section" or "table" or "thead" or "tbody" or "tr" or "ul" or "ol" or "li" or "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "form" or "header" or "footer" or "nav" or "main" or "article" or "aside" or "svg" or "canvas" or "pre" or "details" or "summary" => true,
        _ => false,
    };

    private void LayoutComponent(CaptureRenderer r, int componentId, ref Flow f, int depth)
    {
        var frames = r.Frames(componentId);
        LayoutRange(r, frames.Array, 0, frames.Count, ref f, depth);
    }

    private void LayoutRange(CaptureRenderer r, RenderTreeFrame[] a, int start, int end, ref Flow f, int depth)
    {
        for (var i = start; i < end; i++)
        {
            ref var fr = ref a[i];
            switch (fr.FrameType)
            {
                case RenderTreeFrameType.Element:
                    LayoutElement(r, a, i, ref f, depth);
                    i += fr.ElementSubtreeLength - 1;
                    break;
                case RenderTreeFrameType.Component:
                    LayoutComponent(r, fr.ComponentId, ref f, depth);
                    i += fr.ComponentSubtreeLength - 1;
                    break;
                case RenderTreeFrameType.Region:
                    LayoutRange(r, a, i + 1, i + fr.RegionSubtreeLength, ref f, depth);
                    i += fr.RegionSubtreeLength - 1;
                    break;
                case RenderTreeFrameType.Text:
                    LayoutText(fr.TextContent, ref f, depth, 0xFFE2E8F0u);
                    break;
                case RenderTreeFrameType.Markup:
                    if (!string.IsNullOrWhiteSpace(fr.MarkupContent))
                    {
                        f.Break();
                        var h = Math.Min(160, 24 + fr.MarkupContent.Length / 40);
                        AddBox(f.X, f.Y, f.Width, h, depth, 0x553B82F6u, 0xFF60A5FAu, 3, null);
                        f.Y += h + 4;
                    }
                    break;
            }
        }
    }

    private void LayoutElement(CaptureRenderer r, RenderTreeFrame[] a, int i, ref Flow f, int depth)
    {
        ref var el = ref a[i];
        var name = el.ElementName;
        if (name is "style" or "script" or "option") return;
        var end = i + el.ElementSubtreeLength;
        string? cls = null, type = null;
        var j = i + 1;
        for (; j < end && a[j].FrameType == RenderTreeFrameType.Attribute; j++)
        {
            ref var at = ref a[j];
            if (at.AttributeName == "class") cls = at.AttributeValue as string;
            else if (at.AttributeName == "type") type = at.AttributeValue as string;
            else if (at.AttributeName == "style" && at.AttributeValue is string sv && sv.Contains("display:none", StringComparison.Ordinal)) return;
        }
        var fill = ColorOf(cls, depth);
        var border = (fill & 0x00FFFFFFu) | 0xFF000000u;
        border = Lighten(border);
        if (name is "input" or "select" or "textarea")
        {
            var w = type == "range" ? 120 : type == "checkbox" ? 16 : 140;
            Inline(ref f, w, 16);
            AddBox(f.LineX, f.Y, w, 16, depth, 0xFF0F172Au, 0xFF64748Bu, 4, null);
            f.LineX += w + 6;
            return;
        }
        if (name is "svg" or "canvas" or "img")
        {
            f.Break();
            var h = name == "img" ? 64 : 140;
            AddBox(f.X, f.Y, f.Width, h, depth, 0x66312E81u, 0xFF818CF8u, 3, null);
            f.Y += h + 6;
            return;
        }
        if (IsBlock(name))
        {
            f.Break();
            var bi = AddBox(f.X, f.Y, f.Width, 0, depth, fill, border, 0, null);
            var inner = new Flow(f.X + 6, f.Y + 6, Math.Max(40, f.Width - 12));
            LayoutRange(r, a, j, end, ref inner, depth + 1);
            inner.Break();
            var h = Math.Max(14, inner.Y - f.Y + 4);
            if (bi >= 0) _boxes[bi].H = h;
            f.Y += h + 6;
            if (f.Y > H + 2000) f.Y = H + 2000;
            return;
        }
        // 인라인: 하위 텍스트 길이 → 폭
        var chars = CountText(r, a, j, end);
        var iw = Math.Min(f.Width, HeadlessCanvas.TextWidth(Math.Max(chars, 1), 2) + 12);
        Inline(ref f, iw, 18);
        var ib = AddBox(f.LineX, f.Y, iw, 18, depth, name is "button" or "a" ? 0xFF334155u : fill & 0x88FFFFFFu, name is "button" or "a" ? 0xFF94A3B8u : border, 1, null);
        var ts = _textCount;
        AppendText(r, a, j, end, Math.Max(0, (iw - 12) / 8));
        if (ib >= 0) { _boxes[ib].TextStart = ts; _boxes[ib].TextLength = _textCount - ts; }
        f.LineX += iw + 6;
    }

    private static void Inline(ref Flow f, int w, int h)
    {
        if (f.LineX + w > f.X + f.Width && f.LineX > f.X) f.Break();
        if (h > f.LineH) f.LineH = h;
    }

    private void LayoutText(string? s, ref Flow f, int depth, uint color)
    {
        if (string.IsNullOrWhiteSpace(s)) return;
        var t = s.AsSpan().Trim();
        while (t.Length > 0)
        {
            var room = Math.Max(1, (f.X + f.Width - f.LineX) / 8);
            if (room < 4 && f.LineX > f.X) { f.Break(); continue; }
            var take = Math.Min(room, t.Length);
            Inline(ref f, take * 8, 14);
            var ts = _textCount;
            for (var k = 0; k < take && _textCount < MaxText; k++) _text[_textCount++] = t[k];
            var bi = AddBox(f.LineX, f.Y, take * 8, 14, depth, 0, color, 2, null);
            if (bi >= 0) { _boxes[bi].TextStart = ts; _boxes[bi].TextLength = _textCount - ts; }
            f.LineX += take * 8 + 4;
            t = t[take..];
            if (t.Length > 0) f.Break();
        }
    }

    private static int CountText(CaptureRenderer r, RenderTreeFrame[] a, int start, int end)
    {
        var n = 0;
        for (var i = start; i < end; i++)
        {
            ref var fr = ref a[i];
            if (fr.FrameType == RenderTreeFrameType.Text && fr.TextContent is { } s) n += s.Trim().Length;
            else if (fr.FrameType == RenderTreeFrameType.Component)
            {
                var sub = r.Frames(fr.ComponentId);
                n += CountText(r, sub.Array, 0, sub.Count);
                i += fr.ComponentSubtreeLength - 1;
            }
        }
        return n;
    }

    private void AppendText(CaptureRenderer r, RenderTreeFrame[] a, int start, int end, int max)
    {
        for (var i = start; i < end && max > 0; i++)
        {
            ref var fr = ref a[i];
            if (fr.FrameType == RenderTreeFrameType.Text && fr.TextContent is { } s)
            {
                var t = s.AsSpan().Trim();
                for (var k = 0; k < t.Length && max > 0 && _textCount < MaxText; k++, max--) _text[_textCount++] = t[k];
                if (max > 0 && _textCount < MaxText) { _text[_textCount++] = ' '; max--; }
            }
            else if (fr.FrameType == RenderTreeFrameType.Component)
            {
                var sub = r.Frames(fr.ComponentId);
                AppendText(r, sub.Array, 0, sub.Count, max);
                i += fr.ComponentSubtreeLength - 1;
            }
        }
    }

    private int AddBox(int x, int y, int w, int h, int depth, uint fill, uint border, byte kind, string? label)
    {
        if (_boxCount >= MaxBoxes) return -1;
        var ts = _textCount;
        if (label is not null) for (var k = 0; k < label.Length && _textCount < MaxText; k++) _text[_textCount++] = label[k];
        _boxes[_boxCount] = new UiBox { X = x, Y = y, W = w, H = h, Depth = depth, Fill = fill, Border = border, Kind = kind, TextStart = ts, TextLength = _textCount - ts };
        return _boxCount++;
    }

    private static uint ColorOf(string? cls, int depth)
    {
        if (string.IsNullOrEmpty(cls)) return depth % 2 == 0 ? 0x22334155u : 0x221E293Bu;
        var h = 2166136261u;
        foreach (var ch in cls) { h ^= ch; h *= 16777619u; }
        return (Palette[h % (uint)Palette.Length] & 0x00FFFFFFu) | 0x99000000u;
    }

    private static uint Lighten(uint c)
    {
        uint r = Math.Min(255u, (c >> 16 & 255) + 70), g = Math.Min(255u, (c >> 8 & 255) + 70), b = Math.Min(255u, (c & 255) + 70);
        return 0xFF000000u | r << 16 | g << 8 | b;
    }

    /// <summary>표시 목록 → UI 레이어(UI 영역만 · 결함 주입 반영).</summary>
    private void RasterUiLayer()
    {
        var c = new HeadlessCanvas(_ui, W, H);
        c.FillRect(UiRegion.X, UiRegion.Y, UiRegion.W, UiRegion.H, 0xFF0F172Au);
        for (var i = 0; i < _boxCount; i++)
        {
            var b = _boxes[i];
            if (_faultFrom >= 0 && i >= _faultFrom) { b.Y += 40; b.X += 24; }
            if (b.Y > H || b.Y + b.H < 0) continue;
            switch (b.Kind)
            {
                case 2:
                    c.DrawText(b.X, b.Y + 2, _text.AsSpan(b.TextStart, b.TextLength), b.Border, 2, UiRegion);
                    break;
                default:
                    if (b.Fill != 0) c.FillRect(b.X, b.Y, b.W, b.H, b.Fill, UiRegion);
                    c.FillRect(b.X, b.Y, b.W, 1, b.Border, UiRegion); c.FillRect(b.X, b.Y + b.H - 1, b.W, 1, b.Border, UiRegion);
                    c.FillRect(b.X, b.Y, 1, b.H, b.Border, UiRegion); c.FillRect(b.X + b.W - 1, b.Y, 1, b.H, b.Border, UiRegion);
                    if (b.Kind == 3) for (var k = 8; k < b.W + b.H; k += 16) c.Line(b.X + Math.Max(0, k - b.H), b.Y + Math.Min(b.H, k), b.X + Math.Min(b.W, k), b.Y + Math.Max(0, k - b.W), 0x5560A5FAu, 1, new PixelRect(b.X, b.Y, b.W, b.H));
                    if (b.TextLength > 0) c.DrawText(b.X + 6, b.Y + 2, _text.AsSpan(b.TextStart, b.TextLength), 0xFFF8FAFCu, 2, new PixelRect(b.X, b.Y, b.W, b.H));
                    break;
            }
        }
        c.FillRect(UiRegion.Right - 2, 0, 2, H, 0xFF475569u);
    }

    private sealed class CaptureRenderer : Renderer
    {
        private readonly Dispatcher _dispatcher = Dispatcher.CreateDefault();
        private Exception? _error;

        public CaptureRenderer(IServiceProvider sp) : base(sp, NullLoggerFactory.Instance) { }

        public override Dispatcher Dispatcher => _dispatcher;
        public int Mount(Type t) => AssignRootComponentId(InstantiateComponent(t));
        public Task RenderRoot(int id, ParameterView p) => RenderRootComponentAsync(id, p);
        public ArrayRange<RenderTreeFrame> Frames(int id) => GetCurrentRenderTreeFrames(id);
        public Exception? TakeError() { var e = _error; _error = null; return e; }
        protected override void HandleException(Exception exception) => _error ??= exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }

    private sealed class CaptureServices : IServiceProvider
    {
        private readonly IServiceProvider _inner;
        public CaptureServices(IServiceProvider inner) => _inner = inner;
        public object? GetService(Type t) => t == typeof(IJSRuntime) ? StubJs.Instance : _inner.GetService(t);
    }

    private sealed class StubJs : IJSRuntime
    {
        public static readonly StubJs Instance = new();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }
}
