using System.Numerics;
using PromptFlowStudio.Services.BroadcastMcr;
using PromptFlowStudio.Services.SplatEnvironment;

namespace PromptFlowStudio.Services.SemanticChat;

public enum WeatherPreset : byte { Clear, NeonFireworks, GoldenBloom, RainFog, RedStorm }

/// <summary>v17.72.0 날씨/조명 매개변수(연속 보간 대상 · 값 형식).</summary>
public struct WeatherParams
{
    public Vector3 Tint, FogColor;
    public float Exposure, Neon, Fireworks, Rain, Fog, Lightning, Warm;

    public static WeatherParams For(WeatherPreset p) => p switch
    {
        WeatherPreset.NeonFireworks => new() { Tint = new(0.95f, 0.85f, 1.15f), Exposure = 1.1f, Neon = 1f, Fireworks = 1f, FogColor = new(0.25f, 0.05f, 0.35f), Fog = 0.12f },
        WeatherPreset.GoldenBloom => new() { Tint = new(1.18f, 1.05f, 0.82f), Exposure = 1.15f, Warm = 1f, FogColor = new(1f, 0.85f, 0.55f), Fog = 0.1f },
        WeatherPreset.RainFog => new() { Tint = new(0.72f, 0.82f, 1.12f), Exposure = 0.8f, Rain = 1f, Fog = 0.55f, FogColor = new(0.42f, 0.5f, 0.62f) },
        WeatherPreset.RedStorm => new() { Tint = new(1.25f, 0.62f, 0.55f), Exposure = 0.75f, Rain = 0.7f, Lightning = 1f, Fog = 0.3f, FogColor = new(0.35f, 0.06f, 0.05f) },
        _ => new() { Tint = Vector3.One, Exposure = 1f, FogColor = new(0.5f, 0.55f, 0.6f) },
    };

    public static WeatherParams Lerp(in WeatherParams a, in WeatherParams b, float t) => new()
    {
        Tint = Vector3.Lerp(a.Tint, b.Tint, t), FogColor = Vector3.Lerp(a.FogColor, b.FogColor, t), Exposure = a.Exposure + (b.Exposure - a.Exposure) * t,
        Neon = a.Neon + (b.Neon - a.Neon) * t, Fireworks = a.Fireworks + (b.Fireworks - a.Fireworks) * t, Rain = a.Rain + (b.Rain - a.Rain) * t,
        Fog = a.Fog + (b.Fog - a.Fog) * t, Lightning = a.Lightning + (b.Lightning - a.Lightning) * t, Warm = a.Warm + (b.Warm - a.Warm) * t,
    };
}

/// <summary>
/// [PromptFlow Studio v17.72.0] 🌩️ 시청자 감성 주도형 3DGS 볼류메트릭 날씨 엔진(60 Hz · 무할당).
///   ▸ 매핑: 감성 벡터 → 프리셋 가중 혼합(Hype = 네온/폭죽 · Joy = 골든 블룸 · Sadness = 비/안개/푸른 톤 · Anger = 붉은 폭풍우 · 번개) · 지배 감정 &lt; <see cref="Threshold"/> = 맑음 비중 ↑ ·
///     목표 → 현재 지수 평활(τ = <see cref="TransitionSec"/>) = 절차적 트랜지션(급변 없음). 강제 오버라이드(프리셋 · 강도) 지원.
///   ▸ 조명(3DGS): v17.55 래스터라이저 SH 방사휘도 출력 직후 <see cref="ISplatRadianceModulator"/> 체인(기존 v17.56 그림자 변조 뒤) — 틴트 × 노출 + 네온 발광(위치 해시 4 색 · 맥동) + 번개 섬광.
///     프레임 시작 시 매개변수 스냅숏(워커 병렬 읽기 전용) · 스플랫당 곱셈 · 덧셈 수 회.
///   ▸ 볼류메트릭/파티클(PGM 320×192 RGB · <see cref="IMcrProgramOverlay"/> 체인): 높이 지수 안개 · 사선 빗줄기 풀(600) · 폭죽 버스트 풀(720 · 중력 · 감쇠 가산) · 번개 볼트(랜덤 워크 · 섬광) — 고정 풀 · 무할당.
/// </summary>
public sealed class ChatDrivenVolumetricWeatherEngine : ISplatRadianceModulator, IMcrProgramOverlay
{
    public const int RainCap = 600, SparkCap = 720, BoltPts = 24, W = UnifiedGaussianSceneCompositorEngine.W, H = UnifiedGaussianSceneCompositorEngine.H;

    private struct Drop { public float X, Y, Speed, Len; }
    private struct Spark { public float X, Y, Vx, Vy, Life, R, G, B; }

    private readonly Drop[] _rain = new Drop[RainCap];
    private readonly Spark[] _spark = new Spark[SparkCap];
    private readonly short[] _boltX = new short[BoltPts], _boltY = new short[BoltPts];
    private readonly Vector3[] _neon = new Vector3[4];
    private WeatherParams _cur = WeatherParams.For(WeatherPreset.Clear), _target, _frame;
    private uint _rng = 0xBEEFu;
    private float _t, _flash, _boltLife, _fwAcc, _pulse;
    private int _sparkNext, _boltN;

    public ChatDrivenVolumetricWeatherEngine()
    {
        _target = _cur; _frame = _cur;
        for (var i = 0; i < RainCap; i++) { _rain[i].X = Rand() * W; _rain[i].Y = Rand() * H; _rain[i].Speed = 180 + Rand() * 140; _rain[i].Len = 4 + Rand() * 6; }
    }

    /// <summary>v17.56 그림자 등 선행 변조기(체인).</summary>
    public ISplatRadianceModulator? Inner { get; set; }
    /// <summary>v17.68 CTA 배너 등 선행 PGM 오버레이(체인 · 날씨 뒤 = 그래픽은 안개/비 위).</summary>
    public IMcrProgramOverlay? NextOverlay { get; set; }
    public float Threshold { get; set; } = 0.35f;
    public float TransitionSec { get; set; } = 1.5f;
    public bool Enabled { get; set; } = true;
    public WeatherPreset? Override { get; private set; }
    public float OverrideIntensity { get; private set; } = 1f;
    public WeatherParams Current => _cur;
    public WeatherParams Target => _target;
    public WeatherPreset Dominant { get; private set; } = WeatherPreset.Clear;
    public float Flash => _flash;
    public int ActiveSparks { get; private set; }
    public long Steps { get; private set; }
    public long Bolts { get; private set; }
    public long Bursts { get; private set; }
    public long Modulated { get; private set; }
    public long Overlays { get; private set; }
    public double LastStepUs { get; private set; }
    public double LastOverlayUs { get; private set; }
    public double MaxOverlayUs { get; private set; }

    public static string PresetName(WeatherPreset p) => p switch
    {
        WeatherPreset.NeonFireworks => "네온 · 폭죽", WeatherPreset.GoldenBloom => "골든 블룸", WeatherPreset.RainFog => "비 · 안개", WeatherPreset.RedStorm => "붉은 폭풍 · 번개", _ => "맑음",
    };
    public static string PresetIcon(WeatherPreset p) => p switch { WeatherPreset.NeonFireworks => "🎆", WeatherPreset.GoldenBloom => "🌅", WeatherPreset.RainFog => "🌧️", WeatherPreset.RedStorm => "⛈️", _ => "☀️" };
    public static WeatherPreset PresetOf(CrowdEmotion e) => e switch { CrowdEmotion.Hype => WeatherPreset.NeonFireworks, CrowdEmotion.Joy => WeatherPreset.GoldenBloom, CrowdEmotion.Sadness => WeatherPreset.RainFog, _ => WeatherPreset.RedStorm };

    private float Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng & 0xFFFFFF) / 16777216f; }

    public void SetOverride(WeatherPreset? p, float intensity = 1f) { Override = p; OverrideIntensity = Math.Clamp(intensity, 0f, 1f); }

    /// <summary>감성 벡터 → 목표 매개변수(가중 혼합 · 맑음 기저).</summary>
    public static WeatherParams TargetFor(in SentimentVector v, float threshold)
    {
        var clear = WeatherParams.For(WeatherPreset.Clear);
        var max = v.Max;
        if (max < threshold * 0.5f) return clear;
        // 감정별 초과분(임계 대비)만 가중 — 지배 감정이 약하면 맑음 비중
        Span<float> w = stackalloc float[4];
        var sum = 0f;
        for (var e = 0; e < 4; e++) { w[e] = MathF.Max(0f, v[e] - threshold * 0.5f); w[e] *= w[e]; sum += w[e]; }
        if (sum <= 1e-6f) return clear;
        var strength = Math.Clamp((max - threshold * 0.5f) / (1f - threshold * 0.5f), 0f, 1f);
        var acc = clear;
        var mix = new WeatherParams();
        for (var e = 0; e < 4; e++)
        {
            var p = WeatherParams.For(PresetOf((CrowdEmotion)e));
            var k = w[e] / sum;
            mix.Tint += p.Tint * k; mix.FogColor += p.FogColor * k; mix.Exposure += p.Exposure * k; mix.Neon += p.Neon * k; mix.Fireworks += p.Fireworks * k;
            mix.Rain += p.Rain * k; mix.Fog += p.Fog * k; mix.Lightning += p.Lightning * k; mix.Warm += p.Warm * k;
        }
        return WeatherParams.Lerp(in acc, in mix, strength);
    }

    // ══════════════════════ 틱(MCR 스레드 · 무할당) ══════════════════════
    public void Step(in SentimentVector v, float dt)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Steps++; _t += dt;
        if (Override is { } o) { var c = WeatherParams.For(WeatherPreset.Clear); var p = WeatherParams.For(o); _target = WeatherParams.Lerp(in c, in p, OverrideIntensity); Dominant = o; }
        else { _target = TargetFor(in v, Threshold); Dominant = v.Max >= Threshold ? PresetOf(v.Dominant) : WeatherPreset.Clear; }
        var a = 1f - MathF.Exp(-dt / MathF.Max(0.05f, TransitionSec));
        _cur = WeatherParams.Lerp(in _cur, in _target, a);
        _pulse = 0.5f + 0.5f * MathF.Sin(_t * 6.2832f * 1.5f);
        // 네온 4 색(시간 회전)
        for (var i = 0; i < 4; i++)
        {
            var h = (_t * 0.15f + i * 0.25f) % 1f;
            _neon[i] = new Vector3(Hue(h), Hue(h + 0.333f), Hue(h + 0.667f)) * (0.35f + 0.25f * _pulse);
        }
        // 비
        if (_cur.Rain > 0.02f)
            for (var i = 0; i < RainCap; i++)
            {
                ref var d = ref _rain[i];
                d.Y += d.Speed * dt; d.X += d.Speed * 0.18f * dt;
                if (d.Y > H) { d.Y -= H + d.Len; d.X = Rand() * (W + 40) - 40; }
            }
        // 폭죽(초당 버스트 ∝ Fireworks)
        _fwAcc += _cur.Fireworks * 2.2f * dt;
        while (_fwAcc >= 1f) { _fwAcc -= 1f; Burst(); }
        var active = 0;
        for (var i = 0; i < SparkCap; i++)
        {
            ref var s = ref _spark[i];
            if (s.Life <= 0f) continue;
            s.Vy += 60f * dt; s.X += s.Vx * dt; s.Y += s.Vy * dt; s.Vx *= 0.985f; s.Life -= dt * 0.8f;
            active++;
        }
        ActiveSparks = active;
        // 번개(포아송 · 강도 ∝ Lightning)
        _flash = MathF.Max(0f, _flash - dt * 4f);
        _boltLife = MathF.Max(0f, _boltLife - dt);
        if (_cur.Lightning > 0.05f && Rand() < _cur.Lightning * 0.9f * dt) Strike();
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static float Hue(float x) { x = x % 1f; var v = MathF.Abs(x * 6f - 3f) - 1f; return Math.Clamp(v, 0f, 1f); }

    private void Burst()
    {
        Bursts++;
        float cx = 30 + Rand() * (W - 60), cy = 15 + Rand() * (H * 0.45f);
        var h = Rand();
        float r = Hue(h) * 0.7f + 0.3f, g = Hue(h + 0.333f) * 0.7f + 0.3f, b = Hue(h + 0.667f) * 0.7f + 0.3f;
        for (var k = 0; k < 48; k++)
        {
            ref var s = ref _spark[_sparkNext]; _sparkNext = (_sparkNext + 1) % SparkCap;
            var ang = k / 48f * 6.2832f; var sp = 35 + Rand() * 30;
            s.X = cx; s.Y = cy; s.Vx = MathF.Cos(ang) * sp; s.Vy = MathF.Sin(ang) * sp; s.Life = 1f; s.R = r; s.G = g; s.B = b;
        }
    }

    private void Strike()
    {
        Bolts++; _flash = 1f; _boltLife = 0.18f;
        float x = 20 + Rand() * (W - 40), y = 0;
        _boltN = 0;
        while (_boltN < BoltPts && y < H * 0.85f) { _boltX[_boltN] = (short)x; _boltY[_boltN] = (short)y; _boltN++; x += (Rand() - 0.5f) * 18f; y += 4 + Rand() * 9; }
    }

    // ══════════════════════ ISplatRadianceModulator(래스터 워커 · 무할당) ══════════════════════
    public void BeginFrame(GaussianSplatScene scene, in SplatCamera cam, int workers)
    {
        Inner?.BeginFrame(scene, in cam, workers);
        _frame = _cur;   // 워커 공유 읽기 전용 스냅숏
        _frame.Lightning = _flash;
        Modulated++;
    }

    public Vector3 Modulate(int worker, int splat, Vector3 position, Vector3 color)
    {
        var c = Inner is null ? color : Inner.Modulate(worker, splat, position, color);
        if (!Enabled) return c;
        ref readonly var f = ref _frame;
        c = c * f.Tint * f.Exposure;
        if (f.Neon > 0.01f)
        {
            var k = (int)(MathF.Abs(position.X * 1.7f + position.Z * 0.9f + position.Y * 0.6f)) & 3;
            c += _neon[k] * (f.Neon * (0.25f + 0.75f * MathF.Min(1f, (c.X + c.Y + c.Z) * 0.6f)));
        }
        if (f.Lightning > 0.01f) c += new Vector3(0.75f, 0.8f, 1f) * (f.Lightning * 0.9f) + c * f.Lightning;
        return c;
    }

    public void EndFrame() => Inner?.EndFrame();

    // ══════════════════════ IMcrProgramOverlay(PGM RGB24 · 무할당) ══════════════════════
    public void Apply(Span<byte> rgb, int width, int height, long frame)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Enabled && width == W && height == H)
        {
            Overlays++;
            var p = _cur;
            // 높이 지수 안개(하단 짙음) — 행 단위 계수
            if (p.Fog > 0.01f)
            {
                byte fr = (byte)(Math.Clamp(p.FogColor.X, 0f, 1f) * 255), fg = (byte)(Math.Clamp(p.FogColor.Y, 0f, 1f) * 255), fb = (byte)(Math.Clamp(p.FogColor.Z, 0f, 1f) * 255);
                for (var y = 0; y < H; y++)
                {
                    var hgt = y / (float)H;
                    var a = (int)(256f * Math.Clamp(p.Fog * (0.35f + 0.9f * hgt * hgt), 0f, 0.85f));
                    if (a <= 0) continue;
                    var ia = 256 - a; var row = y * W * 3;
                    for (var x = 0; x < W; x++)
                    {
                        var o = row + x * 3;
                        rgb[o] = (byte)((rgb[o] * ia + fr * a) >> 8); rgb[o + 1] = (byte)((rgb[o + 1] * ia + fg * a) >> 8); rgb[o + 2] = (byte)((rgb[o + 2] * ia + fb * a) >> 8);
                    }
                }
            }
            // 빗줄기(밀도 ∝ Rain)
            if (p.Rain > 0.02f)
            {
                var n = (int)(RainCap * Math.Clamp(p.Rain, 0f, 1f));
                var lum = (byte)(140 + 60 * p.Rain);
                for (var i = 0; i < n; i++)
                {
                    ref readonly var d = ref _rain[i];
                    for (var k = 0; k < (int)d.Len; k++)
                    {
                        int x = (int)(d.X - k * 0.18f), y = (int)(d.Y - k);
                        if ((uint)x >= W || (uint)y >= H) continue;
                        var o = (y * W + x) * 3;
                        rgb[o] = (byte)((rgb[o] + lum) >> 1); rgb[o + 1] = (byte)((rgb[o + 1] + lum) >> 1); rgb[o + 2] = (byte)Math.Min(255, (rgb[o + 2] + lum + 20) >> 1);
                    }
                }
            }
            // 폭죽(가산)
            if (ActiveSparks > 0)
                for (var i = 0; i < SparkCap; i++)
                {
                    ref readonly var s = ref _spark[i];
                    if (s.Life <= 0f) continue;
                    int x = (int)s.X, y = (int)s.Y;
                    if ((uint)x >= W - 1 || (uint)y >= H - 1) continue;
                    var e = s.Life * 255f;
                    Add(rgb, (y * W + x) * 3, s.R * e, s.G * e, s.B * e);
                    Add(rgb, (y * W + x + 1) * 3, s.R * e * 0.5f, s.G * e * 0.5f, s.B * e * 0.5f);
                    Add(rgb, ((y + 1) * W + x) * 3, s.R * e * 0.5f, s.G * e * 0.5f, s.B * e * 0.5f);
                }
            // 번개 볼트 + 섬광
            if (_boltLife > 0f && _boltN > 1)
                for (var i = 0; i + 1 < _boltN; i++) Line(rgb, _boltX[i], _boltY[i], _boltX[i + 1], _boltY[i + 1]);
            if (_flash > 0.05f)
            {
                var add = (int)(_flash * 70f);
                for (var o = 0; o < W * H * 3; o++) { var v = rgb[o] + add; rgb[o] = (byte)(v > 255 ? 255 : v); }
            }
        }
        NextOverlay?.Apply(rgb, width, height, frame);
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastOverlayUs = us; if (us > MaxOverlayUs && Overlays > 3) MaxOverlayUs = us;
    }

    private static void Add(Span<byte> rgb, int o, float r, float g, float b)
    {
        var vr = rgb[o] + (int)r; var vg = rgb[o + 1] + (int)g; var vb = rgb[o + 2] + (int)b;
        rgb[o] = (byte)(vr > 255 ? 255 : vr); rgb[o + 1] = (byte)(vg > 255 ? 255 : vg); rgb[o + 2] = (byte)(vb > 255 ? 255 : vb);
    }

    private static void Line(Span<byte> rgb, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
        for (var guard = 0; guard < 512; guard++)
        {
            if ((uint)x0 < W && (uint)y0 < H) { var o = (y0 * W + x0) * 3; rgb[o] = 235; rgb[o + 1] = 240; rgb[o + 2] = 255; }
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }
}
