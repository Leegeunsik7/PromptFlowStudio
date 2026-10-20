using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.LensOptics;

public enum LightOrigin : byte { Stage, Pyro, Practical, Flash }

/// <summary>렌즈로 들어오는 광원(월드) — 카메라 방향 광도 cd.</summary>
public struct LensLightSource
{
    public Vector3 Position, Color;
    public float Candela;
    public LightOrigin Origin;
}

/// <summary>스크린 스페이스 플레어 스프라이트(링 슬롯).</summary>
public struct FlareSprite
{
    public Vector2 Pixel;
    public Vector3 Color;
    public float Energy, CocPx, Depth, StreakPx;
    public LightOrigin Origin;
    public byte Ghost;
}

/// <summary>
/// [PromptFlow Studio v17.43.0] ✨ 물리 광원 플레어 &amp; 조리개 보케·할레이션 합성기(<see cref="Compose"/> 무할당).
///   ▸ 광원(v17.41 무빙헤드 · 워시 · 스트로브 / v17.42 파이로 클러스터 · 화염광 / 실용광 · 플래시) → 렌즈 투영 · 조도 E = I/d²(역제곱) · 노출 K·(2.8/T)².
///   ▸ 스프라이트 링(512 슬롯 · 프레임 헤드 전진 · 초과 시 최고령 덮어쓰기 카운트) → 평면 HDR 래스터:
///     코어 가우스 · 조리개 날 N각형 회절 타원 보케(세로 = CoC · 가로 = CoC/스퀴즈 · 에너지 보존 · 비누방울 림) ·
///     아나모픽 수평 블루 스트릭(길이 ∝ log E · 강도 ∝ (S − 1)) · 할레이션(적주황 광륜 σ 5 px) · 광축 대칭 고스트 2개.
/// </summary>
public sealed class PhysicalFlareHalationBokehSynthesizer
{
    public const int MaxSources = 64, Ring = 512, MaxCoc = 40;
    public const float BaseExposure = 0.02f, CoreSigma = 0.8f, CoreArea = 2f * MathF.PI * CoreSigma * CoreSigma, HaloSigma = 5f, PyroGain = 400f;
    private const int W = RealtimeAnamorphicLensOpticsEngine.W, H = RealtimeAnamorphicLensOpticsEngine.H;

    private static readonly double TickUs = 1_000_000.0 / Stopwatch.Frequency;
    private static readonly Vector3 HaloTint = new(1f, 0.32f, 0.12f);
    private readonly LensLightSource[] _src = new LensLightSource[MaxSources];
    private readonly FlareSprite[] _ring = new FlareSprite[Ring];
    private int _head, _frameStart;

    public int SourceCount { get; private set; }
    public long SourceDrops { get; private set; }
    public int SpriteCount { get; private set; }
    public long RingWraps { get; private set; }
    public long Sprites { get; private set; }
    public float StreakEnergy { get; private set; }
    /// <summary>스트릭 강도 미터 0..1 = 1 − e^(−E/40).</summary>
    public float StreakMeter => 1f - MathF.Exp(-StreakEnergy / 40f);
    public float BokehEnergy { get; private set; }
    public float HaloEnergy { get; private set; }
    public float PeakIlluminance { get; private set; }
    public float MaxCocPx { get; private set; }
    public double LastComposeUs { get; private set; }
    public ref readonly FlareSprite Sprite(int k) => ref _ring[(_frameStart + k) & (Ring - 1)];
    public ref readonly LensLightSource Source(int i) => ref _src[i];

    public void Begin() => SourceCount = 0;

    public bool Add(Vector3 position, Vector3 color, float candela, LightOrigin origin)
    {
        if (!(candela > 1e-3f) || !float.IsFinite(position.X + position.Y + position.Z)) return false;
        if (SourceCount >= MaxSources) { SourceDrops++; return false; }
        ref var s = ref _src[SourceCount++];
        s.Position = position; s.Color = color; s.Candela = candela; s.Origin = origin;
        return true;
    }

    /// <summary>광원 → 스프라이트 링 → 평면 HDR(R/G/B, 배경 위 가산).</summary>
    public void Compose(RealtimeAnamorphicLensOpticsEngine lens, in LensCamera cam, Span<float> r, Span<float> g, Span<float> b)
    {
        var t0 = Stopwatch.GetTimestamp();
        var spec = lens.Spec; var sq = lens.Squeeze;
        var exposure = BaseExposure * (2.8f / lens.TStop) * (2.8f / lens.TStop);
        _frameStart = _head;
        var n = 0; float streak = 0f, bokeh = 0f, halo = 0f, peak = 0f, maxCoc = 0f;
        for (var i = 0; i < SourceCount; i++)
        {
            ref readonly var s = ref _src[i];
            var px = lens.Project(in cam, s.Position, out var depth);
            if (depth < 0.1f || px.X < -40f || px.X > W + 40f || px.Y < -40f || px.Y > H + 40f) continue;
            var d2 = Vector3.DistanceSquared(s.Position, cam.Position);
            var e = s.Candela / MathF.Max(d2, 0.01f);
            if (e > peak) peak = e;
            var energy = e * exposure;
            var coc = MathF.Min(lens.CocPx(depth), MaxCoc);
            if (coc > maxCoc) maxCoc = coc;
            var sl = spec.Streak * (sq - 1f) > 0f ? MathF.Min(30f + 25f * MathF.Log(1f + energy), 2f * W) : 0f;
            Push(px, s.Color, energy, coc, depth, sl, s.Origin, 0, ref n);
            if (spec.Ghosts > 0f && energy > 2f)
            {
                var c = new Vector2(0.5f * W - 0.5f, 0.5f * H - 0.5f);
                Push(c - (px - c) * 0.45f, s.Color, energy * spec.Ghosts * 0.04f, 3f + 2f * coc, depth, 0f, s.Origin, 1, ref n);
                Push(c - (px - c) * 1.1f, s.Color, energy * spec.Ghosts * 0.02f, 6f + 3f * coc, depth, 0f, s.Origin, 2, ref n);
            }
        }
        SpriteCount = Math.Min(n, Ring);
        for (var k = 0; k < SpriteCount; k++)
        {
            ref readonly var sp = ref Sprite(k);
            if (sp.Ghost != 0) { bokeh += Oval(r, g, b, sp.Pixel, sp.CocPx, sq, spec.Blades, 0f, sp.Color * sp.Energy * CoreArea); continue; }
            if (sp.CocPx <= 1.2f) Core(r, g, b, sp.Pixel, sp.Color * sp.Energy);
            else bokeh += Oval(r, g, b, sp.Pixel, sp.CocPx, sq, spec.Blades, spec.BokehRim, sp.Color * sp.Energy * CoreArea);
            if (sp.StreakPx > 0f) streak += Streak(r, g, b, sp.Pixel, sp.StreakPx, spec.StreakTint * (sp.Energy * 0.12f * spec.Streak * (sq - 1f)));
            if (spec.Halation > 0f && sp.Energy > 1f) halo += Halo(r, g, b, sp.Pixel, HaloTint * (spec.Halation * 0.05f * MathF.Min(sp.Energy, 400f)));
        }
        StreakEnergy = streak; BokehEnergy = bokeh; HaloEnergy = halo; PeakIlluminance = peak; MaxCocPx = maxCoc;
        LastComposeUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
    }

    private void Push(Vector2 px, Vector3 color, float energy, float coc, float depth, float streak, LightOrigin o, byte ghost, ref int n)
    {
        ref var sp = ref _ring[_head];
        sp.Pixel = px; sp.Color = color; sp.Energy = energy; sp.CocPx = coc; sp.Depth = depth; sp.StreakPx = streak; sp.Origin = o; sp.Ghost = ghost;
        _head = (_head + 1) & (Ring - 1);
        if (_head == 0) RingWraps++;
        if (++n > Ring) _frameStart = _head; // 프레임 내 초과 → 최고령 덮어쓰기(가장 최근 Ring 개만 래스터)
        Sprites++;
    }

    private static void Core(Span<float> r, Span<float> g, Span<float> b, Vector2 c, Vector3 amp)
    {
        int x0 = Math.Max(0, (int)(c.X - 3f)), x1 = Math.Min(W - 1, (int)(c.X + 3f)), y0 = Math.Max(0, (int)(c.Y - 3f)), y1 = Math.Min(H - 1, (int)(c.Y + 3f));
        const float k = -0.5f / (CoreSigma * CoreSigma);
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var dx = x - c.X; var dy = y - c.Y;
            var w = MathF.Exp(k * (dx * dx + dy * dy));
            var i = y * W + x;
            r[i] += amp.X * w; g[i] += amp.Y * w; b[i] += amp.Z * w;
        }
    }

    /// <summary>N각 조리개 타원 보케 — 세로 반경 coc · 가로 coc/S · 총 에너지 = flux(면적 정규화).</summary>
    public static float Oval(Span<float> r, Span<float> g, Span<float> b, Vector2 c, float coc, float squeeze, int blades, float rim, Vector3 flux)
    {
        var ry = MathF.Max(coc, 1f); var rx = MathF.Max(coc / squeeze, 1f);
        var seg = MathF.Tau / blades; var apo = MathF.Cos(MathF.PI / blades);
        var area = 0.5f * blades * MathF.Sin(seg) * rx * ry;
        var amp = flux / area;
        int x0 = Math.Max(0, (int)(c.X - rx - 1)), x1 = Math.Min(W - 1, (int)(c.X + rx + 1)), y0 = Math.Max(0, (int)(c.Y - ry - 1)), y1 = Math.Min(H - 1, (int)(c.Y + ry + 1));
        var sum = 0f;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var u = (x - c.X) / rx; var v = (y - c.Y) / ry;
            var q = MathF.Sqrt(u * u + v * v);
            if (q > 1f) continue;
            var th = MathF.Atan2(v, u) + MathF.PI;
            var a = th - seg * MathF.Floor(th / seg) - 0.5f * seg;
            var edge = apo / MathF.Cos(a);
            if (q > edge) continue;
            var t = q / edge;
            var w = 1f - rim + 2f * rim * t * t;
            var i = y * W + x;
            r[i] += amp.X * w; g[i] += amp.Y * w; b[i] += amp.Z * w;
            sum += w;
        }
        return sum * (amp.X + amp.Y + amp.Z) / 3f;
    }

    private static float Streak(Span<float> r, Span<float> g, Span<float> b, Vector2 c, float len, Vector3 amp)
    {
        int y0 = Math.Max(0, (int)c.Y - 1), y1 = Math.Min(H - 1, (int)c.Y + 2);
        int x0 = Math.Max(0, (int)(c.X - len)), x1 = Math.Min(W - 1, (int)(c.X + len));
        var inv = 3f / len; var sum = 0f;
        for (var y = y0; y <= y1; y++)
        {
            var dy = y - c.Y; var wy = MathF.Exp(-dy * dy * 1.4f);
            for (var x = x0; x <= x1; x++)
            {
                var w = wy * MathF.Exp(-MathF.Abs(x - c.X) * inv);
                var i = y * W + x;
                r[i] += amp.X * w; g[i] += amp.Y * w; b[i] += amp.Z * w;
                sum += w;
            }
        }
        return sum * (amp.X + amp.Y + amp.Z) / 3f;
    }

    private static float Halo(Span<float> r, Span<float> g, Span<float> b, Vector2 c, Vector3 amp)
    {
        const int R = 15; const float k = -0.5f / (HaloSigma * HaloSigma);
        int x0 = Math.Max(0, (int)c.X - R), x1 = Math.Min(W - 1, (int)c.X + R), y0 = Math.Max(0, (int)c.Y - R), y1 = Math.Min(H - 1, (int)c.Y + R);
        var sum = 0f;
        for (var y = y0; y <= y1; y++)
        for (var x = x0; x <= x1; x++)
        {
            var dx = x - c.X; var dy = y - c.Y;
            var w = MathF.Exp(k * (dx * dx + dy * dy));
            var i = y * W + x;
            r[i] += amp.X * w; g[i] += amp.Y * w; b[i] += amp.Z * w;
            sum += w;
        }
        return sum * amp.X;
    }
}
