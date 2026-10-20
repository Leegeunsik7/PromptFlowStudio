using System.Numerics;
using PromptFlowStudio.Services.MasterControl;
using PromptFlowStudio.Services.MassiveCrowd;

namespace PromptFlowStudio.Services.ArGraphics;

[Flags]
public enum ArLayer
{
    None = 0, Rings = 1, Trail = 2, Tags = 4, Distance = 8, Arrow = 16, Offside = 32, Telestrator = 64,
    LowerThird = 128, PlayerCard = 256, ScoreBug = 512, CrowdHud = 1024,
}

public enum FillMode : byte { Premultiplied, Straight }

/// <summary>[PromptFlow Studio v17.06.0] 5×7 비트맵 글꼴(ASCII 대문자 · 숫자 · 기호) — 무할당 텍스트 래스터.</summary>
public static class ArFont
{
    public const int GlyphW = 5, GlyphH = 7;
    private const string Chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ.:/-%' ";
    private static readonly byte[] Rows =
    {
        0x0E,0x11,0x13,0x15,0x19,0x11,0x0E, 0x04,0x0C,0x04,0x04,0x04,0x04,0x0E, 0x0E,0x11,0x01,0x02,0x04,0x08,0x1F, 0x1F,0x02,0x04,0x02,0x01,0x11,0x0E,
        0x02,0x06,0x0A,0x12,0x1F,0x02,0x02, 0x1F,0x10,0x1E,0x01,0x01,0x11,0x0E, 0x06,0x08,0x10,0x1E,0x11,0x11,0x0E, 0x1F,0x01,0x02,0x04,0x08,0x08,0x08,
        0x0E,0x11,0x11,0x0E,0x11,0x11,0x0E, 0x0E,0x11,0x11,0x0F,0x01,0x02,0x0C,
        0x0E,0x11,0x11,0x11,0x1F,0x11,0x11, 0x1E,0x11,0x11,0x1E,0x11,0x11,0x1E, 0x0E,0x11,0x10,0x10,0x10,0x11,0x0E, 0x1C,0x12,0x11,0x11,0x11,0x12,0x1C,
        0x1F,0x10,0x10,0x1E,0x10,0x10,0x1F, 0x1F,0x10,0x10,0x1E,0x10,0x10,0x10, 0x0E,0x11,0x10,0x17,0x11,0x11,0x0F, 0x11,0x11,0x11,0x1F,0x11,0x11,0x11,
        0x0E,0x04,0x04,0x04,0x04,0x04,0x0E, 0x07,0x02,0x02,0x02,0x02,0x12,0x0C, 0x11,0x12,0x14,0x18,0x14,0x12,0x11, 0x10,0x10,0x10,0x10,0x10,0x10,0x1F,
        0x11,0x1B,0x15,0x15,0x11,0x11,0x11, 0x11,0x11,0x19,0x15,0x13,0x11,0x11, 0x0E,0x11,0x11,0x11,0x11,0x11,0x0E, 0x1E,0x11,0x11,0x1E,0x10,0x10,0x10,
        0x0E,0x11,0x11,0x11,0x15,0x12,0x0D, 0x1E,0x11,0x11,0x1E,0x14,0x12,0x11, 0x0F,0x10,0x10,0x0E,0x01,0x01,0x1E, 0x1F,0x04,0x04,0x04,0x04,0x04,0x04,
        0x11,0x11,0x11,0x11,0x11,0x11,0x0E, 0x11,0x11,0x11,0x11,0x11,0x0A,0x04, 0x11,0x11,0x11,0x15,0x15,0x15,0x0A, 0x11,0x11,0x0A,0x04,0x0A,0x11,0x11,
        0x11,0x11,0x11,0x0A,0x04,0x04,0x04, 0x1F,0x01,0x02,0x04,0x08,0x10,0x1F,
        0x00,0x00,0x00,0x00,0x00,0x0C,0x0C, 0x00,0x0C,0x0C,0x00,0x0C,0x0C,0x00, 0x00,0x01,0x02,0x04,0x08,0x10,0x00, 0x00,0x00,0x00,0x1F,0x00,0x00,0x00,
        0x18,0x19,0x02,0x04,0x08,0x13,0x03, 0x04,0x04,0x08,0x00,0x00,0x00,0x00, 0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    };

    public static int Glyph(char c)
    {
        if (c >= 'a' && c <= 'z') c = (char)(c - 32);
        var i = Chars.IndexOf(c);
        return i < 0 ? Chars.Length - 1 : i;
    }

    public static bool Pixel(int glyph, int x, int y) => (Rows[glyph * GlyphH + y] >> (GlyphW - 1 - x) & 1) != 0;
    public static int Width(int chars, int scale) => chars <= 0 ? 0 : (chars * (GlyphW + 1) - 1) * scale;

    /// <summary>고정소수 숫자 서식(무할당 · 반환 = 길이).</summary>
    public static int Fixed(Span<char> d, float v, int decimals)
    {
        var n = 0;
        if (v < 0) { d[n++] = '-'; v = -v; }
        var mul = decimals == 0 ? 1 : decimals == 1 ? 10 : 100;
        var q = (long)MathF.Round(v * mul);
        var ip = q / mul; var fp = (int)(q % mul);
        n += Int(d.Slice(n), ip);
        if (decimals > 0)
        {
            d[n++] = '.';
            if (decimals == 2) { d[n++] = (char)('0' + fp / 10); d[n++] = (char)('0' + fp % 10); }
            else d[n++] = (char)('0' + fp);
        }
        return n;
    }

    public static int Int(Span<char> d, long v)
    {
        if (v < 0) { d[0] = '-'; return 1 + Int(d.Slice(1), -v); }
        var len = 1;
        for (var t = v; t >= 10; t /= 10) len++;
        for (var i = len - 1; i >= 0; i--) { d[i] = (char)('0' + v % 10); v /= 10; }
        return len;
    }

    public static int Copy(Span<char> d, ReadOnlySpan<char> s) { s.CopyTo(d); return s.Length; }
}

/// <summary>HUD 화면 요소 사각형(타이틀 세이프 검증용).</summary>
public readonly record struct HudRect(ArLayer Layer, int X, int Y, int W, int H);

/// <summary>
/// [PromptFlow Studio v17.06.0] 🏟 온에어 3D AR 텔레스트레이션 &amp; 스포츠 텔레메트리 HUD 합성기.
/// 트래킹 카메라(FreeD + 렌즈 왜곡) 기준으로 SLAM 지면 평면(v16.98)에 고정된 3D 그래픽(선수 링 · 공 궤적 · 거리 측정 · 전술 화살표 · 오프사이드 라인 ·
/// 텔레스트레이터 스트로크)을 짧은 지면 구간 단위로 투영(왜곡 곡률 반영) · 안티앨리어싱 래스터 · 선수 실루엣 매트로 가림 처리하고,
/// 화면 공간 HUD(이름표 · 스코어 버그 · v17.04 군중 흥분 HUD · 로어 서드 · 플레이어 카드 히트맵)를 프레임 지연 없이 합성.
/// 출력 = Fill(프리멀티/스트레이트) + Key(8비트 알파 복제). 카메라 피드 시뮬레이터(실측 자세 · ST 맵 역투영 지면 렌더) 포함. 전 경로 무할당.
/// </summary>
public sealed class OnAirArTelestrationSportsTelemetrySynthesizer
{
    public const int Players = BroadcastArenaScene.Players, TrailLen = 90, MaxStroke = 256, HeatW = 20, HeatH = 13, MaxHud = 16;
    public const float PlayerHeight = 1.85f, PlayerRadius = 0.32f;
    public static readonly string[] Names = { "SON", "KIM", "LEE", "PARK", "HWANG", "SILVA", "MULLER", "ROSSI", "GARCIA", "SMITH" };
    public static readonly int[] Numbers = { 7, 3, 10, 6, 11, 9, 13, 8, 5, 4 };

    private readonly float[] _r, _g, _b, _a;
    private readonly byte[] _matte, _fill, _key, _feed;
    private readonly float[] _trailX = new float[TrailLen], _trailZ = new float[TrailLen];
    private readonly Vector3[] _stroke = new Vector3[MaxStroke];
    private readonly int[] _strokeBreak = new int[MaxStroke];
    private readonly float[] _dist = new float[Players], _top = new float[Players], _speed = new float[Players];
    private readonly float[] _heat = new float[Players * HeatW * HeatH];
    private readonly HudRect[] _hud = new HudRect[MaxHud];
    private readonly char[] _txt = new char[64];
    private readonly char[] _caption = new char[48];
    private int _trailHead, _trailCount, _strokeN, _hudN, _captionN;
    private float _lowerT;
    private Vector3 _n = Vector3.UnitY;
    private float _d;

    public OnAirArTelestrationSportsTelemetrySynthesizer(int width = 256, int height = 144)
    {
        Width = width; Height = height;
        var n = width * height;
        _r = new float[n]; _g = new float[n]; _b = new float[n]; _a = new float[n];
        _matte = new byte[n];
        _fill = new byte[n * 4]; _key = new byte[n * 4]; _feed = new byte[n * 4];
        SetCaption("LIVE FROM PROMPTFLOW ARENA");
    }

    public int Width { get; }
    public int Height { get; }
    public ArLayer Layers { get; set; } = ArLayer.Rings | ArLayer.Trail | ArLayer.Tags | ArLayer.Distance | ArLayer.Arrow | ArLayer.ScoreBug | ArLayer.CrowdHud | ArLayer.Telestrator;
    public FillMode Output { get; set; } = FillMode.Premultiplied;
    public int Selected { get; set; } = 0;
    public int Second { get; set; } = 6;
    public bool Occlusion { get; set; } = true;
    public long Frames { get; private set; }
    public double RenderUs { get; private set; }
    public double FeedUs { get; private set; }
    public int DrawnSegments { get; private set; }
    public int OccludedPixels { get; private set; }
    public float KeyCoverage { get; private set; }
    public int StrokePoints => _strokeN;
    public int HudCount => _hudN;
    public HudRect Hud(int i) => _hud[i];
    public ReadOnlySpan<byte> Fill => _fill;
    public ReadOnlySpan<byte> Key => _key;
    public ReadOnlySpan<byte> Feed => _feed;
    public ReadOnlySpan<byte> Matte => _matte;
    public ReadOnlySpan<char> Caption => _caption.AsSpan(0, _captionN);
    public float Speed(int p) => _speed[p];
    public float TopSpeed(int p) => _top[p];
    public float Distance(int p) => _dist[p];
    public float Heat(int p, int cx, int cz) => _heat[(p * HeatH + cz) * HeatW + cx];
    public Vector3 StrokePoint(int i) => _stroke[i];
    public Vector3 GroundNormal => _n;
    public float GroundD => _d;

    /// <summary>자막/로어 서드 문구(ASCII 48자 · 그 외 문자는 '?').</summary>
    public void SetCaption(string text)
    {
        var n = Math.Min(text.Length, _caption.Length);
        for (var i = 0; i < n; i++) { var c = text[i]; _caption[i] = c >= ' ' && c < 127 ? char.ToUpperInvariant(c) : '?'; }
        _captionN = n;
    }

    public void SetGround(Vector3 n, float d) { _n = n.Y < 0 ? -n : n; _d = n.Y < 0 ? -d : d; }

    public float GroundY(float x, float z) => MathF.Abs(_n.Y) < 1e-3f ? 0f : -(_n.X * x + _n.Z * z + _d) / _n.Y;
    public Vector3 G(float x, float z, float h = 0f) => new(x, GroundY(x, z) + h, z);

    // ── 텔레스트레이터 ─────────────────────────────────────

    /// <summary>화면 점 → 지면 고정 스트로크 점(역왜곡 광선 ∩ 지면). newStroke = 새 획 시작.</summary>
    public bool AddStroke(LensCamera cam, float u, float v, bool newStroke)
    {
        if (_strokeN >= MaxStroke || !cam.Intersect(cam.Ray(u, v), _n, _d, out var hit)) return false;
        _strokeBreak[_strokeN] = newStroke || _strokeN == 0 ? 1 : 0;
        _stroke[_strokeN++] = hit;
        return true;
    }

    public void ClearStrokes() => _strokeN = 0;

    // ── 통계(선수 추적) ────────────────────────────────────

    /// <summary>선수 속도 · 최고 속도 · 이동 거리 · 히트맵 · 공 궤적 갱신.</summary>
    public void Track(BroadcastArenaScene s, float dt)
    {
        for (var p = 0; p < Players; p++)
        {
            float vx = s.Vx(p), vz = s.Vz(p), sp = MathF.Sqrt(vx * vx + vz * vz) * 3.6f;
            _speed[p] = sp; if (sp > _top[p]) _top[p] = sp;
            _dist[p] += sp / 3.6f * dt;
            int cx = Math.Clamp((int)(s.X(p) / BroadcastArenaScene.FieldW * HeatW), 0, HeatW - 1), cz = Math.Clamp((int)(s.Z(p) / BroadcastArenaScene.FieldH * HeatH), 0, HeatH - 1);
            _heat[(p * HeatH + cz) * HeatW + cx] += dt;
        }
        _trailX[_trailHead] = s.X(BroadcastArenaScene.Ball); _trailZ[_trailHead] = s.Z(BroadcastArenaScene.Ball);
        _trailHead = (_trailHead + 1) % TrailLen;
        if (_trailCount < TrailLen) _trailCount++;
    }

    // ── 래스터 기본 도형(프리멀티 float 누적) ───────────────

    private void Blend(int i, float r, float g, float b, float a)
    {
        var ia = 1f - a;
        _r[i] = r * a + _r[i] * ia; _g[i] = g * a + _g[i] * ia; _b[i] = b * a + _b[i] * ia; _a[i] = a + _a[i] * ia;
    }

    /// <summary>두께 있는 AA 선분(occlude = 선수 매트 가림 적용).</summary>
    public void Segment(float x0, float y0, float x1, float y1, float halfW, float r, float g, float b, float a, bool occlude)
    {
        int bx0 = Math.Max(0, (int)MathF.Floor(MathF.Min(x0, x1) - halfW - 1)), bx1 = Math.Min(Width - 1, (int)MathF.Ceiling(MathF.Max(x0, x1) + halfW + 1));
        int by0 = Math.Max(0, (int)MathF.Floor(MathF.Min(y0, y1) - halfW - 1)), by1 = Math.Min(Height - 1, (int)MathF.Ceiling(MathF.Max(y0, y1) + halfW + 1));
        if (bx0 > bx1 || by0 > by1) return;
        float dx = x1 - x0, dy = y1 - y0, l2 = MathF.Max(1e-6f, dx * dx + dy * dy);
        DrawnSegments++;
        for (var y = by0; y <= by1; y++)
            for (var x = bx0; x <= bx1; x++)
            {
                float px = x - x0, py = y - y0, t = Math.Clamp((px * dx + py * dy) / l2, 0f, 1f);
                float ex = px - t * dx, ey = py - t * dy;
                var cov = halfW + 0.5f - MathF.Sqrt(ex * ex + ey * ey);
                if (cov <= 0) continue;
                var i = y * Width + x;
                if (occlude && Occlusion && _matte[i] != 0) { OccludedPixels++; continue; }
                Blend(i, r, g, b, a * MathF.Min(1f, cov));
            }
    }

    public void FillRect(int x, int y, int w, int h, float r, float g, float b, float a)
    {
        int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
        for (var yy = y0; yy < y1; yy++) for (var xx = x0; xx < x1; xx++) Blend(yy * Width + xx, r, g, b, a);
    }

    /// <summary>삼각형 채우기(에지 함수 · 중심 표본).</summary>
    public void FillTriangle(float ax, float ay, float bx, float by, float cx, float cy, float r, float g, float b, float a, bool occlude)
    {
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)))), x1 = Math.Min(Width - 1, (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)))), y1 = Math.Min(Height - 1, (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
        var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (MathF.Abs(area) < 1e-4f) return;
        for (var y = y0; y <= y1; y++)
            for (var x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                var w0 = ((bx - px) * (cy - py) - (by - py) * (cx - px)) / area;
                var w1 = ((cx - px) * (ay - py) - (cy - py) * (ax - px)) / area;
                if (w0 < 0 || w1 < 0 || w0 + w1 > 1) continue;
                var i = y * Width + x;
                if (occlude && Occlusion && _matte[i] != 0) { OccludedPixels++; continue; }
                Blend(i, r, g, b, a);
            }
    }

    public int Text(ReadOnlySpan<char> s, int x, int y, int scale, float r, float g, float b, float a)
    {
        for (var c = 0; c < s.Length; c++)
        {
            var gl = ArFont.Glyph(s[c]);
            var ox = x + c * (ArFont.GlyphW + 1) * scale;
            for (var gy = 0; gy < ArFont.GlyphH; gy++)
                for (var gx = 0; gx < ArFont.GlyphW; gx++)
                {
                    if (!ArFont.Pixel(gl, gx, gy)) continue;
                    for (var sy = 0; sy < scale; sy++)
                        for (var sx = 0; sx < scale; sx++)
                        {
                            int px = ox + gx * scale + sx, py = y + gy * scale + sy;
                            if ((uint)px < (uint)Width && (uint)py < (uint)Height) Blend(py * Width + px, r, g, b, a);
                        }
                }
        }
        return ArFont.Width(s.Length, scale);
    }

    /// <summary>지면 구간(1 m 이하 세분 → 렌즈 왜곡 곡률 반영).</summary>
    public void GroundLine(LensCamera cam, float xa, float za, float xb, float zb, float halfW, float r, float g, float b, float a, float lift = 0.02f)
    {
        var len = MathF.Sqrt((xb - xa) * (xb - xa) + (zb - za) * (zb - za));
        var n = Math.Clamp((int)MathF.Ceiling(len), 1, 128);
        var ok0 = cam.Project(G(xa, za, lift), out var u0, out var v0);
        for (var k = 1; k <= n; k++)
        {
            float t = k / (float)n, x = xa + (xb - xa) * t, z = za + (zb - za) * t;
            var ok1 = cam.Project(G(x, z, lift), out var u1, out var v1);
            if (ok0 && ok1) Segment(u0, v0, u1, v1, halfW, r, g, b, a, true);
            ok0 = ok1; u0 = u1; v0 = v1;
        }
    }

    public void GroundRing(LensCamera cam, float cx, float cz, float radius, float halfW, float r, float g, float b, float a)
    {
        const int Seg = 40;
        var ok0 = cam.Project(G(cx + radius, cz, 0.02f), out var u0, out var v0);
        for (var k = 1; k <= Seg; k++)
        {
            var t = k * 2 * MathF.PI / Seg;
            var ok1 = cam.Project(G(cx + radius * MathF.Cos(t), cz + radius * MathF.Sin(t), 0.02f), out var u1, out var v1);
            if (ok0 && ok1) Segment(u0, v0, u1, v1, halfW, r, g, b, a, true);
            ok0 = ok1; u0 = u1; v0 = v1;
        }
    }

    private void AddHud(ArLayer l, int x, int y, int w, int h) { if (_hudN < MaxHud) _hud[_hudN++] = new HudRect(l, x, y, w, h); }

    /// <summary>선수 실루엣 매트(트래킹 카메라 기준 원통 투영) — 지면 그래픽 가림.</summary>
    private void BuildMatte(LensCamera cam, BroadcastArenaScene s)
    {
        Array.Clear(_matte);
        for (var p = 0; p < Players; p++)
        {
            var foot = G(s.X(p), s.Z(p));
            if (!cam.Project(foot, out var uf, out var vf) || !cam.Project(foot + new Vector3(0, PlayerHeight, 0), out var uh, out var vh)) continue;
            var hw = MathF.Max(1f, cam.Fx * PlayerRadius / MathF.Max(0.1f, cam.Depth(foot)));
            FillMatte(uf, vf, uh, vh, hw);
        }
    }

    private void FillMatte(float uf, float vf, float uh, float vh, float hw)
    {
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(vf, vh))), y1 = Math.Min(Height - 1, (int)MathF.Ceiling(MathF.Max(vf, vh)));
        for (var y = y0; y <= y1; y++)
        {
            var t = MathF.Abs(vh - vf) < 1e-3f ? 0f : (y - vf) / (vh - vf);
            var cx = uf + (uh - uf) * Math.Clamp(t, 0f, 1f);
            int x0 = Math.Max(0, (int)MathF.Round(cx - hw)), x1 = Math.Min(Width - 1, (int)MathF.Round(cx + hw));
            for (var x = x0; x <= x1; x++) _matte[y * Width + x] = 1;
        }
    }

    // ── 그래픽 합성 ────────────────────────────────────────

    /// <summary>1 프레임 그래픽 합성(무할당) → Fill/Key.</summary>
    public void Render(LensCamera cam, BroadcastArenaScene s, MassiveAgenticCrowdSimulationEngine? crowd, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        Array.Clear(_r); Array.Clear(_g); Array.Clear(_b); Array.Clear(_a);
        DrawnSegments = 0; OccludedPixels = 0; _hudN = 0;
        BuildMatte(cam, s);
        var L = Layers;
        int sel = Math.Clamp(Selected, 0, Players - 1), sec = Math.Clamp(Second, 0, Players - 1);
        if ((L & ArLayer.Offside) != 0)
        {
            float m1 = -1, m2 = -1;
            for (var p = Players / 2; p < Players; p++) { var x = s.X(p); if (x > m1) { m2 = m1; m1 = x; } else if (x > m2) m2 = x; }
            GroundLine(cam, m2, 0, m2, BroadcastArenaScene.FieldH, 1.2f, 1f, 0.9f, 0.1f, 0.65f);
        }
        if ((L & ArLayer.Trail) != 0 && _trailCount > 1)
            for (var k = 1; k < _trailCount; k++)
            {
                int i1 = (_trailHead - k + TrailLen) % TrailLen, i0 = (i1 - 1 + TrailLen) % TrailLen;
                if (MathF.Abs(_trailX[i1] - _trailX[i0]) + MathF.Abs(_trailZ[i1] - _trailZ[i0]) > 8f) continue;   // 킥오프 리셋 구간
                GroundLine(cam, _trailX[i0], _trailZ[i0], _trailX[i1], _trailZ[i1], 1f, 1f, 0.85f, 0.3f, 0.85f * (1f - k / (float)TrailLen));
            }
        if ((L & ArLayer.Rings) != 0)
            for (var p = 0; p < Players; p++)
            {
                var home = BroadcastArenaScene.Team(p) == 0;
                GroundRing(cam, s.X(p), s.Z(p), p == sel ? 1.2f + 0.15f * MathF.Sin(Frames * 0.2f) : 0.9f, p == sel ? 1.4f : 0.9f, home ? 1f : 0.25f, home ? 0.3f : 0.55f, home ? 0.3f : 1f, 0.9f);
            }
        if ((L & ArLayer.Distance) != 0 && sel != sec)
        {
            float ax = s.X(sel), az = s.Z(sel), bx = s.X(sec), bz = s.Z(sec), len = Vector3.Distance(G(ax, az), G(bx, bz));
            var segs = Math.Max(1, (int)(len / 1.5f));
            for (var k = 0; k < segs; k += 2)
                GroundLine(cam, ax + (bx - ax) * k / segs, az + (bz - az) * k / segs, ax + (bx - ax) * (k + 1) / segs, az + (bz - az) * (k + 1) / segs, 0.9f, 1f, 1f, 1f, 0.95f);
            if (cam.Project(G((ax + bx) / 2, (az + bz) / 2, 0.5f), out var mu, out var mv))
            {
                var n = ArFont.Fixed(_txt, len, 1); _txt[n++] = 'M';
                Label(_txt.AsSpan(0, n), (int)mu, (int)mv - 4, 0.05f, 0.05f, 0.05f);
            }
        }
        if ((L & ArLayer.Arrow) != 0)
        {
            float px = s.X(sel), pz = s.Z(sel);
            float tx = s.X(BroadcastArenaScene.Ball) + s.Vx(BroadcastArenaScene.Ball) * 0.8f, tz = s.Z(BroadcastArenaScene.Ball) + s.Vz(BroadcastArenaScene.Ball) * 0.8f;
            float dx = tx - px, dz = tz - pz, l = MathF.Sqrt(dx * dx + dz * dz);
            if (l > 3f)
            {
                dx /= l; dz /= l;
                float hx = tx - dx * 2.2f, hz = tz - dz * 2.2f;
                GroundLine(cam, px + dx * 1.3f, pz + dz * 1.3f, hx, hz, 1.6f, 0.2f, 1f, 0.6f, 0.85f);
                if (cam.Project(G(tx, tz, 0.02f), out var u0, out var v0) && cam.Project(G(hx - dz * 1.2f, hz + dx * 1.2f, 0.02f), out var u1, out var v1) && cam.Project(G(hx + dz * 1.2f, hz - dx * 1.2f, 0.02f), out var u2, out var v2))
                    FillTriangle(u0, v0, u1, v1, u2, v2, 0.2f, 1f, 0.6f, 0.85f, true);
            }
        }
        if ((L & ArLayer.Telestrator) != 0)
            for (var k = 1; k < _strokeN; k++)
            {
                if (_strokeBreak[k] != 0) continue;
                GroundLine(cam, _stroke[k - 1].X, _stroke[k - 1].Z, _stroke[k].X, _stroke[k].Z, 1.5f, 1f, 0.25f, 0.85f, 0.95f);
            }
        if ((L & ArLayer.Tags) != 0)
            for (var p = 0; p < Players; p++)
            {
                if (!cam.Project(G(s.X(p), s.Z(p), PlayerHeight + 0.5f), out var u, out var v)) continue;
                var n = ArFont.Int(_txt, Numbers[p]); _txt[n++] = ' ';
                n += ArFont.Copy(_txt.AsSpan(n), Names[p]);
                if (p == sel) { _txt[n++] = ' '; n += ArFont.Fixed(_txt.AsSpan(n), _speed[p], 0); }
                var home = BroadcastArenaScene.Team(p) == 0;
                Label(_txt.AsSpan(0, n), (int)u, (int)v - 8, home ? 0.55f : 0.08f, home ? 0.06f : 0.2f, home ? 0.06f : 0.55f);
            }
        if ((L & ArLayer.ScoreBug) != 0) ScoreBug(s);
        if ((L & ArLayer.CrowdHud) != 0 && crowd is not null) CrowdHud(crowd);
        _lowerT = (L & ArLayer.LowerThird) != 0 ? MathF.Min(1f, _lowerT + dt * 3f) : MathF.Max(0f, _lowerT - dt * 3f);
        if (_lowerT > 0) LowerThird(sel);
        if ((L & ArLayer.PlayerCard) != 0) PlayerCard(sel);
        Resolve();
        RenderUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void Label(ReadOnlySpan<char> s, int cx, int y, float r, float g, float b)
    {
        var w = ArFont.Width(s.Length, 1);
        int x = cx - w / 2;
        FillRect(x - 2, y - 2, w + 4, ArFont.GlyphH + 4, r, g, b, 0.85f);
        Text(s, x, y, 1, 1f, 1f, 1f, 1f);
    }

    private void ScoreBug(BroadcastArenaScene s)
    {
        int x = Width / 20 + 2, y = Height / 20 + 2;
        var n = ArFont.Copy(_txt, "HOME ");
        n += ArFont.Int(_txt.AsSpan(n), s.Score0); _txt[n++] = '-';
        n += ArFont.Int(_txt.AsSpan(n), s.Score1);
        n += ArFont.Copy(_txt.AsSpan(n), " AWAY ");
        var sec = s.Frame / 60;
        n += ArFont.Int(_txt.AsSpan(n), sec / 60 % 100); _txt[n++] = ':';
        _txt[n++] = (char)('0' + sec % 60 / 10); _txt[n++] = (char)('0' + sec % 10);
        var w = ArFont.Width(n, 1) + 6;
        FillRect(x, y, w, 11, 0.04f, 0.08f, 0.2f, 0.88f);
        FillRect(x, y + 11, w, 1, 0.9f, 0.75f, 0.1f, 1f);
        Text(_txt.AsSpan(0, n), x + 3, y + 2, 1, 1f, 1f, 1f, 1f);
        AddHud(ArLayer.ScoreBug, x, y, w, 12);
    }

    private void CrowdHud(MassiveAgenticCrowdSimulationEngine c)
    {
        const int w = 58, h = 28;
        int x = Width - Width / 20 - 2 - w, y = Height / 20 + 2;
        FillRect(x, y, w, h, 0.05f, 0.05f, 0.1f, 0.8f);
        var n = ArFont.Copy(_txt, "CROWD ");
        n += ArFont.Int(_txt.AsSpan(n), (long)MathF.Round(c.MeanArousal * 100)); _txt[n++] = '%';
        Text(_txt.AsSpan(0, n), x + 2, y + 2, 1, 1f, 0.8f, 0.3f, 1f);
        for (var sct = 0; sct < MassiveAgenticCrowdSimulationEngine.Sectors; sct++)
        {
            var a = Math.Clamp(c.SectorArousal(sct), 0f, 1f);
            var bh = Math.Max(1, (int)(a * 14));
            FillRect(x + 2 + sct * 3 + sct / 4, y + h - 2 - bh, 2, bh, 1f, 0.3f + 0.5f * (1 - a), 0.15f, 1f);
        }
        AddHud(ArLayer.CrowdHud, x, y, w, h);
    }

    private void LowerThird(int p)
    {
        var e = 1f - (1f - _lowerT) * (1f - _lowerT);
        int w = Width * 3 / 5, h = 24, x = Width / 20 + 2 - (int)((1f - e) * (w + 20)), y = Height - Height / 20 - 2 - h;
        FillRect(x, y, w, h, 0.03f, 0.1f, 0.3f, 0.9f * e);
        FillRect(x, y, 3, h, 0.95f, 0.7f, 0.1f, e);
        var n = ArFont.Int(_txt, Numbers[p]); _txt[n++] = ' ';
        n += ArFont.Copy(_txt.AsSpan(n), Names[p]);
        n += ArFont.Copy(_txt.AsSpan(n), BroadcastArenaScene.Team(p) == 0 ? " - HOME" : " - AWAY");
        Text(_txt.AsSpan(0, n), x + 7, y + 3, 1, 1f, 1f, 1f, e);
        Text(Caption, x + 7, y + 14, 1, 0.85f, 0.9f, 1f, e);
        AddHud(ArLayer.LowerThird, Math.Max(x, 0), y, w + Math.Min(x, 0), h);
    }

    private void PlayerCard(int p)
    {
        const int w = 70, h = 70;
        int x = Width - Width / 20 - 2 - w, y = Height - Height / 20 - 2 - h;
        FillRect(x, y, w, h, 0.05f, 0.07f, 0.15f, 0.9f);
        var n = ArFont.Int(_txt, Numbers[p]); _txt[n++] = ' ';
        n += ArFont.Copy(_txt.AsSpan(n), Names[p]);
        Text(_txt.AsSpan(0, n), x + 3, y + 3, 1, 1f, 0.85f, 0.2f, 1f);
        n = ArFont.Copy(_txt, "SPD "); n += ArFont.Fixed(_txt.AsSpan(n), _speed[p], 1);
        Text(_txt.AsSpan(0, n), x + 3, y + 13, 1, 1f, 1f, 1f, 1f);
        n = ArFont.Copy(_txt, "TOP "); n += ArFont.Fixed(_txt.AsSpan(n), _top[p], 1);
        Text(_txt.AsSpan(0, n), x + 3, y + 22, 1, 1f, 1f, 1f, 1f);
        n = ArFont.Copy(_txt, "DST "); n += ArFont.Int(_txt.AsSpan(n), (long)_dist[p]); _txt[n++] = 'M';
        Text(_txt.AsSpan(0, n), x + 3, y + 31, 1, 1f, 1f, 1f, 1f);
        var max = 1e-3f;
        for (var k = 0; k < HeatW * HeatH; k++) max = MathF.Max(max, _heat[p * HeatW * HeatH + k]);
        for (var cz = 0; cz < HeatH; cz++)
            for (var cx = 0; cx < HeatW; cx++)
            {
                var v = _heat[(p * HeatH + cz) * HeatW + cx] / max;
                FillRect(x + 3 + cx * 3, y + 41 + cz * 2, 3, 2, 0.1f + 0.9f * v, 0.25f + 0.5f * v * (1 - v) * 4 * 0.5f, 0.6f * (1 - v), 0.35f + 0.6f * v);
            }
        AddHud(ArLayer.PlayerCard, x, y, w, h);
    }

    /// <summary>float 프리멀티 누적 → 8비트 Fill(모드별) + Key(알파 4채널 복제).</summary>
    private void Resolve()
    {
        var keyed = 0;
        for (int i = 0, o = 0; i < _a.Length; i++, o += 4)
        {
            var a = Math.Clamp(_a[i], 0f, 1f);
            var k = (byte)(a * 255f + 0.5f);
            _key[o] = _key[o + 1] = _key[o + 2] = _key[o + 3] = k;
            if (k != 0) keyed++;
            if (Output == FillMode.Premultiplied)
            {
                _fill[o] = (byte)Math.Min(k, (int)(Math.Clamp(_r[i], 0f, 1f) * 255f + 0.5f));
                _fill[o + 1] = (byte)Math.Min(k, (int)(Math.Clamp(_g[i], 0f, 1f) * 255f + 0.5f));
                _fill[o + 2] = (byte)Math.Min(k, (int)(Math.Clamp(_b[i], 0f, 1f) * 255f + 0.5f));
                _fill[o + 3] = k;
            }
            else
            {
                var inv = a > 1e-4f ? 1f / a : 0f;
                _fill[o] = (byte)(Math.Clamp(_r[i] * inv, 0f, 1f) * 255f + 0.5f);
                _fill[o + 1] = (byte)(Math.Clamp(_g[i] * inv, 0f, 1f) * 255f + 0.5f);
                _fill[o + 2] = (byte)(Math.Clamp(_b[i] * inv, 0f, 1f) * 255f + 0.5f);
                _fill[o + 3] = 255;
            }
        }
        KeyCoverage = keyed / (float)_a.Length;
    }

    // ── 카메라 피드 시뮬레이터(실측 자세) ────────────────────

    /// <summary>실측 카메라(왜곡 포함)로 경기장 렌더 — ST 맵 역투영 → 지면 교차 → 피치/라인/스탠드, 선수 원통 · 공.</summary>
    public void RenderFeed(LensCamera cam, BroadcastArenaScene s, Vector3 gn, float gd, MassiveAgenticCrowdSimulationEngine? crowd)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var f = _feed;
        var exc = crowd is null ? 0.3f : crowd.MeanArousal;
        const float W = BroadcastArenaScene.FieldW, H = BroadcastArenaScene.FieldH, lw = 0.12f;
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                cam.GridRay(x, y, out var xu, out var yu);
                var dir = cam.Forward + cam.Right * xu + cam.Up * yu;
                int r, g, b;
                if (cam.Intersect(dir, gn, gd, out var hit) && hit.X > -25 && hit.X < W + 25 && hit.Z > -25 && hit.Z < H + 25)
                {
                    float wx = hit.X, wz = hit.Z;
                    if (wx >= 0 && wx <= W && wz >= 0 && wz <= H)
                    {
                        var stripe = ((int)(wx / 6f) & 1) == 0;
                        r = stripe ? 44 : 37; g = stripe ? 140 : 124; b = stripe ? 58 : 50;
                        float ccx = wx - W / 2, ccz = wz - H / 2;
                        if (wx < lw || wx > W - lw || wz < lw || wz > H - lw || MathF.Abs(ccx) < lw ||
                            MathF.Abs(MathF.Sqrt(ccx * ccx + ccz * ccz) - 9.15f) < lw ||
                            (MathF.Abs(wx - 16.5f) < lw || MathF.Abs(wx - (W - 16.5f)) < lw) && MathF.Abs(ccz) < 20.16f ||
                            (wx < 16.5f || wx > W - 16.5f) && MathF.Abs(MathF.Abs(ccz) - 20.16f) < lw) { r = g = b = 232; }
                    }
                    else { r = 52; g = 58; b = 66; }
                }
                else
                {
                    var hsh = (uint)(x * 73856093 ^ y * 19349663) >> 27;
                    var lift = (int)(exc * 60);
                    r = 30 + (int)hsh + lift; g = 26 + (int)hsh; b = 40 + (int)hsh;
                    if (yu > 0.25f) { r = 70; g = 110; b = 170; }
                }
                var o = (y * Width + x) * 4;
                f[o] = (byte)Math.Min(255, r); f[o + 1] = (byte)Math.Min(255, g); f[o + 2] = (byte)Math.Min(255, b); f[o + 3] = 255;
            }
        for (var p = 0; p <= Players; p++)
        {
            var gy = gn.Y < 1e-3f ? 0 : -(gn.X * s.X(p) + gn.Z * s.Z(p) + gd) / gn.Y;
            var foot = new Vector3(s.X(p), gy, s.Z(p));
            var ball = p == Players;
            var hgt = ball ? 0.22f : PlayerHeight;
            if (!cam.Project(foot, out var uf, out var vf) || !cam.Project(foot + new Vector3(0, hgt, 0), out var uh, out var vh)) continue;
            var hw = MathF.Max(0.8f, cam.Fx * (ball ? 0.11f : PlayerRadius) / MathF.Max(0.1f, cam.Depth(foot)));
            int cr = ball ? 255 : BroadcastArenaScene.Team(p) == 0 ? 220 : 40, cg = ball ? 240 : BroadcastArenaScene.Team(p) == 0 ? 40 : 80, cb = ball ? 200 : BroadcastArenaScene.Team(p) == 0 ? 40 : 225;
            int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(vf, vh))), y1 = Math.Min(Height - 1, (int)MathF.Ceiling(MathF.Max(vf, vh)));
            for (var y = y0; y <= y1; y++)
            {
                var t = MathF.Abs(vh - vf) < 1e-3f ? 0f : (y - vf) / (vh - vf);
                var cx = uf + (uh - uf) * Math.Clamp(t, 0f, 1f);
                var head = !ball && t > 0.82f;
                int x0 = Math.Max(0, (int)MathF.Round(cx - hw)), x1 = Math.Min(Width - 1, (int)MathF.Round(cx + hw));
                for (var x = x0; x <= x1; x++)
                {
                    var o = (y * Width + x) * 4;
                    f[o] = (byte)(head ? 230 : cr); f[o + 1] = (byte)(head ? 190 : cg); f[o + 2] = (byte)(head ? 160 : cb);
                }
            }
        }
        FeedUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }
}
