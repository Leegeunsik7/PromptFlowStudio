using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.VideoDiffusion;

public enum NoiseMode : byte { Independent, Shared, FlowWarped }

/// <summary>프롬프트 팔레트(선형 RGB 0..1) · 텍스처 · 대비.</summary>
public struct DiffusionPalette
{
    public Vector3 SkyTop, SkyHorizon, GroundNear, GroundFar, Subject, Fog;
    public float Texture, Contrast, Haze;
    public string Name;

    public static DiffusionPalette Default => new()
    {
        Name = "주광", SkyTop = new(0.25f, 0.45f, 0.85f), SkyHorizon = new(0.75f, 0.82f, 0.92f), GroundNear = new(0.30f, 0.45f, 0.18f), GroundFar = new(0.45f, 0.55f, 0.40f),
        Subject = new(0.75f, 0.2f, 0.15f), Fog = new(0.78f, 0.82f, 0.88f), Texture = 0.18f, Contrast = 1f, Haze = 0.35f,
    };
}

/// <summary>디퓨전 SIMD 커널(Vector&lt;float&gt; · 꼬리 스칼라 — 스칼라 판과 비트 일치).</summary>
public static class DiffusionSimd
{
    private static int Body(int n) => Vector.IsHardwareAccelerated && n >= Vector<float>.Count ? n - n % Vector<float>.Count : 0;

    /// <summary>
    /// CFG 가우시안 사후 DDIM 1스텝(제자리): x̂0c = μ + k(x − aμ) · k = a v/(a²v + s²) · x̂0u = k x(무조건부 = 동일 텍스처 분산 · 평균 0 → 가이던스가 레이아웃만 증폭 · 텍스처 붕괴 방지) ·
    /// x̂0 = clamp(x̂0u + w(x̂0c − x̂0u), ±c) · ε = (x − a x̂0)/s · x ← a' x̂0 + s' ε · x0out = x̂0.
    /// </summary>
    public static void GuidedStep(Span<float> x, ReadOnlySpan<float> mu, ReadOnlySpan<float> v, Span<float> x0out, float a, float s, float ap, float sp, float w, float c)
    {
        var n = x.Length; var m = Body(n);
        if (m > 0)
        {
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x[..m]); var vm = MemoryMarshal.Cast<float, Vector<float>>(mu[..m]);
            var vv = MemoryMarshal.Cast<float, Vector<float>>(v[..m]); var vo = MemoryMarshal.Cast<float, Vector<float>>(x0out[..m]);
            Vector<float> A = new(a), S2 = new(s * s), Is = new(1f / s), Ap = new(ap), Sp = new(sp), W = new(w), C = new(c), Cn = new(-c);
            for (var k = 0; k < vx.Length; k++)
            {
                var xx = vx[k]; var mm = vm[k]; var av = A * vv[k];
                var kc = av / (A * av + S2);
                var x0c = mm + kc * (xx - A * mm);
                var x0u = kc * xx;
                var x0 = Vector.Min(Vector.Max(x0u + W * (x0c - x0u), Cn), C);
                var eps = (xx - A * x0) * Is;
                vx[k] = Ap * x0 + Sp * eps;
                vo[k] = x0;
            }
        }
        for (var i = m; i < n; i++) GuidedScalarOne(ref x[i], mu[i], v[i], out x0out[i], a, s, ap, sp, w, c);
    }

    private static void GuidedScalarOne(ref float x, float mu, float v, out float x0o, float a, float s, float ap, float sp, float w, float c)
    {
        float s2 = s * s, inv = 1f / s;
        var av = a * v;
        var kc = av / (a * av + s2);
        var x0c = mu + kc * (x - a * mu);
        var x0u = kc * x;
        var x0 = MathF.Min(MathF.Max(x0u + w * (x0c - x0u), -c), c);
        var eps = (x - a * x0) * inv;
        x = ap * x0 + sp * eps;
        x0o = x0;
    }

    public static void GuidedStepScalar(Span<float> x, ReadOnlySpan<float> mu, ReadOnlySpan<float> v, Span<float> x0out, float a, float s, float ap, float sp, float w, float c)
    {
        for (var i = 0; i < x.Length; i++) GuidedScalarOne(ref x[i], mu[i], v[i], out x0out[i], a, s, ap, sp, w, c);
    }

    /// <summary>dst = a + (b − a)·t(요소별 가중).</summary>
    public static void Blend(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> t)
    {
        var n = dst.Length; var m = Body(n);
        if (m > 0)
        {
            var d = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]); var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]); var vt = MemoryMarshal.Cast<float, Vector<float>>(t[..m]);
            for (var k = 0; k < d.Length; k++) d[k] = va[k] + (vb[k] - va[k]) * vt[k];
        }
        for (var i = m; i < n; i++) dst[i] = a[i] + (b[i] - a[i]) * t[i];
    }

    public static void BlendScalar(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> b, ReadOnlySpan<float> t)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = a[i] + (b[i] - a[i]) * t[i];
    }

    /// <summary>절대차 합(SAD).</summary>
    public static float Sad(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length; var m = Body(n); var acc = Vector<float>.Zero;
        if (m > 0)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]);
            for (var k = 0; k < va.Length; k++) acc += Vector.Abs(va[k] - vb[k]);
        }
        var s = Vector.Dot(acc, Vector<float>.One);
        for (var i = m; i < n; i++) s += MathF.Abs(a[i] - b[i]);
        return s;
    }
}

/// <summary>
/// [PromptFlow Studio v16.87.0] 🎞 실시간 라텐트 비디오 디퓨전 생성 엔진(라텐트 4×64×36 → 256×144 · 무할당 프레임 · 결정적 RNG).
///   ▸ 스케줄: T = 1000 스케일드 선형 β(0.00085 → 0.012) · ᾱ 누적곱 · DDIM(η = 0) S 스텝(2차 간격 타임스텝).
///   ▸ 디노이저(학습 모델 미사용 · 해석적): 조건부 = 프롬프트 팔레트 + ControlNet 잔차로 만든 목표 라텐트 μ, 텍스처 분산 v(근거리 ∝ 디테일) 의 가우시안 사전 → 정확한 사후 평균 x̂0 ·
///     무조건부 = 동일 분산 · 평균 0 · CFG(w) 결합 · 동적 클램프 · ControlNet 적용 구간(start..end) 밖은 프롬프트 전용 μ.
///   ▸ 라텐트 채널: 0 = 휘도(2Y−1) · 1 = 2(B−Y) · 2 = 2(R−Y) · 3 = 디테일 → 디코더: 쌍선형 4× 업샘플 → RGB + 디테일 그레인(장면 고정 좌표).
///   ▸ 프레임 노이즈: 독립 · 공유(고정 시드) · 플로 워핑(이전 노이즈를 라텐트 플로로 역워핑 → 분산 재정규화 + √(1−ρ²) 신규 혼합).
///   ▸ 스텝 모니터: σ_t · x̂0 변화량 · 체크포인트 4개 x̂0 휘도 썸네일 16×9.
/// </summary>
public sealed class RealtimeLatentVideoDiffusionEngine
{
    public const int LW = ControlNetSpatialConditioningSynthesizer.LW, LH = ControlNetSpatialConditioningSynthesizer.LH, LC = 4, Plane = LW * LH, LatentSize = Plane * LC;
    public const int Scale = 4, W = LW * Scale, H = LH * Scale, TrainSteps = 1000, MaxSteps = 50, ThumbW = 16, ThumbH = 9, Checkpoints = 4;

    private static readonly float[] AlphaBar = BuildSchedule();
    private readonly object _gate = new();
    private readonly float[] _x = new float[LatentSize], _x0 = new float[LatentSize], _prevX0 = new float[LatentSize];
    private readonly float[] _muPrompt = new float[LatentSize], _muControl = new float[LatentSize], _var = new float[LatentSize];
    private readonly float[] _noise = new float[LatentSize], _noiseTmp = new float[LatentSize];
    private readonly float[] _stepSigma = new float[MaxSteps], _stepDelta = new float[MaxSteps];
    private readonly float[] _thumbs = new float[Checkpoints * ThumbW * ThumbH];
    public readonly float[] Frame = new float[W * H * 3];
    private ulong _rng, _sharedSeed;
    private bool _hasNoise;
    private DiffusionPalette _pal = DiffusionPalette.Default;

    public RealtimeLatentVideoDiffusionEngine(ulong seed = 0x1687_D1FFu) { _rng = seed | 1; _sharedSeed = seed ^ 0xABCDEFu; }

    // ── 설정 ──
    public int Steps { get; set; } = 20;
    public float GuidanceScale { get; set; } = 3f;
    public float ClampRange { get; set; } = 1.6f;
    public NoiseMode Noise { get; set; } = NoiseMode.FlowWarped;
    public float NoisePersistence { get; set; } = 0.97f;
    public float Grain { get; set; } = 0.03f;
    public DiffusionPalette Palette { get => _pal; set => _pal = value; }

    // ── 계측 ──
    public long Frames { get; private set; }
    public double LastGenerateMs { get; private set; }
    public double LastDecodeMs { get; private set; }
    public int LastSteps { get; private set; }
    public int ControlledSteps { get; private set; }
    public float LatentMeanAbsError { get; private set; }
    public ReadOnlySpan<float> Latent => _x;
    public ReadOnlySpan<float> Target => _muControl;
    public ReadOnlySpan<float> PromptTarget => _muPrompt;
    public ReadOnlySpan<float> StepSigma => _stepSigma.AsSpan(0, LastSteps);
    public ReadOnlySpan<float> StepDelta => _stepDelta.AsSpan(0, LastSteps);
    public ReadOnlySpan<float> Thumbnail(int k) => _thumbs.AsSpan(k * ThumbW * ThumbH, ThumbW * ThumbH);
    public Vector2 GrainOffset { get; set; }

    public static float AlphaBarAt(int t) => AlphaBar[Math.Clamp(t, 0, TrainSteps - 1)];

    private static float[] BuildSchedule()
    {
        var ab = new float[TrainSteps]; double p = 1;
        double b0 = Math.Sqrt(0.00085), b1 = Math.Sqrt(0.012);
        for (var t = 0; t < TrainSteps; t++)
        {
            var sb = b0 + (b1 - b0) * t / (TrainSteps - 1);
            p *= 1 - sb * sb; ab[t] = (float)p;
        }
        return ab;
    }

    /// <summary>2차 간격 타임스텝(저잡음 구간 밀집 → 마지막 x̂0 축소 편향 억제).</summary>
    public static int Timestep(int i, int steps) { var u = 1.0 - (double)i / steps; return (int)Math.Round((TrainSteps - 1) * u * u); }

    // ══════════════════════ 색공간 ══════════════════════

    public static Vector4 Encode(Vector3 rgb, float detail)
    {
        var y = 0.299f * rgb.X + 0.587f * rgb.Y + 0.114f * rgb.Z;
        return new Vector4(2f * y - 1f, 2f * (rgb.Z - y), 2f * (rgb.X - y), 2f * detail - 1f);
    }

    public static Vector3 Decode(float l0, float l1, float l2)
    {
        var y = (l0 + 1f) * 0.5f; var b = y + 0.5f * l1; var r = y + 0.5f * l2;
        var g = (y - 0.299f * r - 0.114f * b) / 0.587f;
        return new Vector3(r, g, b);
    }

    // ══════════════════════ 프롬프트 ══════════════════════

    /// <summary>프롬프트(한/영) → 팔레트 · 스텝 · 가이던스(반영 키워드 수).</summary>
    public int Configure(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return 0;
        static bool Has(string s, string a, string b) => s.Contains(a, StringComparison.OrdinalIgnoreCase) || s.Contains(b, StringComparison.OrdinalIgnoreCase);
        var p = DiffusionPalette.Default; var n = 0;
        if (Has(prompt, "석양", "sunset") || Has(prompt, "노을", "golden hour"))
        { p.Name = "석양"; p.SkyTop = new(0.35f, 0.25f, 0.55f); p.SkyHorizon = new(0.98f, 0.55f, 0.25f); p.GroundNear = new(0.25f, 0.18f, 0.12f); p.GroundFar = new(0.55f, 0.35f, 0.3f); p.Fog = new(0.95f, 0.6f, 0.4f); n++; }
        else if (Has(prompt, "밤", "night") || Has(prompt, "달빛", "moonlit"))
        { p.Name = "야경"; p.SkyTop = new(0.02f, 0.03f, 0.10f); p.SkyHorizon = new(0.08f, 0.12f, 0.25f); p.GroundNear = new(0.04f, 0.06f, 0.08f); p.GroundFar = new(0.08f, 0.1f, 0.16f); p.Fog = new(0.1f, 0.12f, 0.2f); p.Subject = new(0.6f, 0.65f, 0.8f); n++; }
        else if (Has(prompt, "사이버", "cyberpunk") || Has(prompt, "네온", "neon"))
        { p.Name = "네온"; p.SkyTop = new(0.1f, 0.0f, 0.2f); p.SkyHorizon = new(0.8f, 0.1f, 0.6f); p.GroundNear = new(0.05f, 0.08f, 0.15f); p.GroundFar = new(0.1f, 0.5f, 0.7f); p.Fog = new(0.5f, 0.2f, 0.6f); p.Subject = new(0.1f, 0.95f, 0.9f); n++; }
        if (Has(prompt, "숲", "forest") || Has(prompt, "정글", "jungle")) { p.GroundNear = new(0.08f, 0.3f, 0.08f); p.GroundFar = new(0.2f, 0.42f, 0.25f); p.Texture = 0.24f; n++; }
        else if (Has(prompt, "사막", "desert") || Has(prompt, "모래", "dune")) { p.GroundNear = new(0.8f, 0.6f, 0.35f); p.GroundFar = new(0.9f, 0.75f, 0.55f); p.Texture = 0.12f; n++; }
        else if (Has(prompt, "설원", "snow") || Has(prompt, "눈", "winter")) { p.GroundNear = new(0.88f, 0.9f, 0.95f); p.GroundFar = new(0.8f, 0.85f, 0.92f); p.Texture = 0.08f; n++; }
        else if (Has(prompt, "바다", "ocean") || Has(prompt, "해변", "beach")) { p.GroundNear = new(0.1f, 0.35f, 0.5f); p.GroundFar = new(0.25f, 0.5f, 0.65f); n++; }
        if (Has(prompt, "안개", "fog") || Has(prompt, "몽환", "misty")) { p.Haze = 0.85f; n++; }
        if (Has(prompt, "수채화", "watercolor")) { p.Texture = 0.05f; p.Contrast = 0.8f; n++; }
        else if (Has(prompt, "필름 그레인", "film grain") || Has(prompt, "거친", "gritty")) { p.Texture = 0.3f; Grain = 0.08f; n++; }
        if (Has(prompt, "빨간", "red")) { p.Subject = new(0.85f, 0.1f, 0.1f); n++; }
        else if (Has(prompt, "파란", "blue")) { p.Subject = new(0.1f, 0.3f, 0.9f); n++; }
        else if (Has(prompt, "흰", "white")) { p.Subject = new(0.92f, 0.92f, 0.9f); n++; }
        if (Has(prompt, "고품질", "high quality")) { Steps = 40; n++; }
        else if (Has(prompt, "빠른", "fast")) { Steps = 8; n++; }
        if (n > 0) _pal = p;
        return n;
    }

    // ══════════════════════ 목표 라텐트(ControlNet 잔차) ══════════════════════

    /// <summary>
    /// 프롬프트 전용 μ(행 기반 지평선 레이아웃) + 컨트롤 μ(깊이 → 하늘/지면/안개 원근 · 포즈 → 피사체 색 · 경계 → 디테일).
    /// 깊이/포즈 가중치 = ControlNet 잔차 스케일.
    /// </summary>
    public void BuildTargets(ControlNetSpatialConditioningSynthesizer c)
    {
        lock (_gate)
        {
            var p = _pal;
            float wd = Math.Clamp(c.DepthWeight, 0f, 1.5f), wp = Math.Clamp(c.PoseWeight, 0f, 1.5f);
            for (var y = 0; y < LH; y++)
            {
                var v = (y + 0.5f) / LH;
                for (var x = 0; x < LW; x++)
                {
                    var i = y * LW + x;
                    // 프롬프트 전용: 지평선 0.45 고정
                    Vector3 colP; float detP;
                    if (v < 0.45f) { colP = Vector3.Lerp(p.SkyTop, p.SkyHorizon, v / 0.45f); detP = 0.05f; }
                    else { var g = (v - 0.45f) / 0.55f; colP = Vector3.Lerp(Vector3.Lerp(p.GroundFar, p.Fog, p.Haze * 0.5f), p.GroundNear, g); detP = 0.3f + 0.6f * g; }
                    // 깊이 컨디션: 하늘 = 고도 그라데이션 · 지면 = 근/원 보간 + 대기 원근(안개)
                    var d = c.Depth[i];
                    Vector3 colD; float detD;
                    if (d >= 0.999f) { colD = Vector3.Lerp(p.SkyTop, p.SkyHorizon, MathF.Min(1f, v / 0.5f)); detD = 0.05f; }
                    else
                    {
                        var ground = Vector3.Lerp(p.GroundNear, p.GroundFar, d);
                        var fog = MathF.Min(1f, p.Haze * d * d * 1.4f);
                        colD = Vector3.Lerp(ground, p.Fog, fog); detD = (1f - d) * 0.9f + 0.4f * c.Edge[i];
                    }
                    var col = Vector3.Lerp(colP, colD, MathF.Min(wd, 1f)) + (colD - colP) * MathF.Max(wd - 1f, 0f);
                    var det = detP + (detD - detP) * MathF.Min(wd, 1f);
                    var pm = MathF.Min(1f, c.Pose[i] * wp);
                    col = Vector3.Lerp(col, p.Subject, pm); det = det + (0.6f - det) * pm;
                    if (p.Contrast != 1f) { col = new Vector3(0.5f) + (col - new Vector3(0.5f)) * p.Contrast; colP = new Vector3(0.5f) + (colP - new Vector3(0.5f)) * p.Contrast; }
                    var lp = Encode(Vector3.Clamp(colP, Vector3.Zero, Vector3.One), detP); var lc = Encode(Vector3.Clamp(col, Vector3.Zero, Vector3.One), Math.Clamp(det, 0f, 1f));
                    _muPrompt[i] = lp.X; _muPrompt[Plane + i] = lp.Y; _muPrompt[2 * Plane + i] = lp.Z; _muPrompt[3 * Plane + i] = lp.W;
                    _muControl[i] = lc.X; _muControl[Plane + i] = lc.Y; _muControl[2 * Plane + i] = lc.Z; _muControl[3 * Plane + i] = lc.W;
                    var tex = p.Texture * (0.25f + 0.75f * Math.Clamp(det, 0f, 1f));
                    _var[i] = tex * tex; _var[Plane + i] = tex * tex * 0.25f; _var[2 * Plane + i] = tex * tex * 0.25f; _var[3 * Plane + i] = 0.0025f;
                }
            }
        }
    }

    /// <summary>시험용: 목표/분산 직접 설정(상수).</summary>
    public void SetUniformTarget(float mu, float variance)
    {
        lock (_gate) { Array.Fill(_muPrompt, mu); Array.Fill(_muControl, mu); Array.Fill(_var, variance); }
    }

    // ══════════════════════ 노이즈 ══════════════════════

    private float Gauss(ref ulong s)
    {
        s ^= s >> 12; s ^= s << 25; s ^= s >> 27; var u1 = (((s * 0x2545F4914F6CDD1DUL) >> 40) + 0.5f) * (1f / (1 << 24));
        s ^= s >> 12; s ^= s << 25; s ^= s >> 27; var u2 = ((s * 0x2545F4914F6CDD1DUL) >> 40) * (1f / (1 << 24));
        return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(MathF.Tau * u2);
    }

    /// <summary>프레임 노이즈 준비 · flow = 라텐트 플로(라텐트 px · 이전 → 현재 · [dx 평면 | dy 평면]).</summary>
    private void PrepareNoise(ReadOnlySpan<float> flow)
    {
        switch (Noise)
        {
            case NoiseMode.Shared:
                {
                    var s = _sharedSeed | 1;
                    for (var i = 0; i < LatentSize; i++) _noise[i] = Gauss(ref s);
                    break;
                }
            case NoiseMode.FlowWarped when _hasNoise && flow.Length >= 2 * Plane:
                {
                    // 역워핑: 현재 픽셀 p ← 이전 p − f(p)(쌍선형) · 분산 축소 → 채널별 재정규화 · 신규 노이즈 혼합
                    var rho = Math.Clamp(NoisePersistence, 0f, 0.999f); var fresh = MathF.Sqrt(1f - rho * rho);
                    for (var ch = 0; ch < LC; ch++)
                    {
                        var o = ch * Plane; double sum = 0, sq = 0;
                        for (var y = 0; y < LH; y++)
                            for (var x = 0; x < LW; x++)
                            {
                                var i = y * LW + x;
                                float sx = Math.Clamp(x - flow[i], 0f, LW - 1.001f), sy = Math.Clamp(y - flow[Plane + i], 0f, LH - 1.001f);
                                int x0 = (int)sx, y0 = (int)sy; float fx = sx - x0, fy = sy - y0;
                                var k = o + y0 * LW + x0;
                                var a = _noise[k] + (_noise[k + 1] - _noise[k]) * fx; var b = _noise[k + LW] + (_noise[k + LW + 1] - _noise[k + LW]) * fx;
                                var vv = a + (b - a) * fy;
                                _noiseTmp[o + i] = vv; sum += vv; sq += vv * vv;
                            }
                        var mean = (float)(sum / Plane); var std = (float)Math.Sqrt(Math.Max(sq / Plane - mean * mean, 1e-8));
                        for (var i = 0; i < Plane; i++) _noise[o + i] = rho * (_noiseTmp[o + i] - mean) / std + fresh * Gauss(ref _rng);
                    }
                    break;
                }
            case NoiseMode.FlowWarped:
            case NoiseMode.Independent:
                for (var i = 0; i < LatentSize; i++) _noise[i] = Gauss(ref _rng);
                break;
        }
        _hasNoise = true;
    }

    /// <summary>1프레임 생성(무할당): 노이즈 → S 스텝 CFG-DDIM(컨트롤 구간 전환) → 디코드.</summary>
    public void Generate(ReadOnlySpan<float> latentFlow, float controlStart, float controlEnd)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            PrepareNoise(latentFlow);
            var steps = Math.Clamp(Steps, 1, MaxSteps);
            var tT = Timestep(0, steps);
            // x_T = √ᾱ_T·0 + √(1−ᾱ_T)·z ≈ z(ᾱ_T ≈ 0.005 · 무조건부 평균 0)
            var sT = MathF.Sqrt(1f - AlphaBarAt(tT));
            for (var i = 0; i < LatentSize; i++) _x[i] = sT * _noise[i];
            var controlled = 0;
            for (var k = 0; k < steps; k++)
            {
                var t = Timestep(k, steps); var tp = k + 1 < steps ? Timestep(k + 1, steps) : -1;
                var ab = AlphaBarAt(t); var abp = tp < 0 ? 1f : AlphaBarAt(tp);
                float a = MathF.Sqrt(ab), s = MathF.Sqrt(1f - ab), ap = MathF.Sqrt(abp), sp = MathF.Sqrt(1f - abp);
                var frac = (float)k / steps;
                var useControl = frac >= controlStart && frac <= controlEnd;
                if (useControl) controlled++;
                DiffusionSimd.GuidedStep(_x, useControl ? _muControl : _muPrompt, _var, _x0, a, s, ap, sp, GuidanceScale, ClampRange);
                _stepSigma[k] = s / a;
                _stepDelta[k] = k == 0 ? 0f : DiffusionSimd.Sad(_x0, _prevX0) / LatentSize;
                _x0.AsSpan().CopyTo(_prevX0);
                var every = Math.Max(1, steps / Checkpoints);
                if (k == steps - 1) Thumb(Checkpoints - 1);
                else if ((k + 1) % every == 0 && (k + 1) / every - 1 < Checkpoints - 1) Thumb((k + 1) / every - 1);
            }
            LastSteps = steps; ControlledSteps = controlled;
            LatentMeanAbsError = DiffusionSimd.Sad(_x, _muControl) / LatentSize;
            LastGenerateMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            DecodeFrame();
            Frames++;
        }
    }

    private void Thumb(int cp)
    {
        var o = cp * ThumbW * ThumbH;
        for (var ty = 0; ty < ThumbH; ty++)
            for (var tx = 0; tx < ThumbW; tx++)
                _thumbs[o + ty * ThumbW + tx] = (_x0[(ty * LH / ThumbH) * LW + tx * LW / ThumbW] + 1f) * 0.5f;
    }

    private static float Hash(int x, int y)
    {
        var h = (uint)x * 374761393u + (uint)y * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return (h ^ (h >> 16)) * (1f / uint.MaxValue) - 0.5f;
    }

    /// <summary>디코더: 4채널 쌍선형 4× → RGB(0..1) + 디테일 그레인(장면 고정 오프셋).</summary>
    private void DecodeFrame()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int gx = (int)MathF.Round(GrainOffset.X), gy = (int)MathF.Round(GrainOffset.Y);
        for (var y = 0; y < H; y++)
        {
            var ly = Math.Clamp((y + 0.5f) / Scale - 0.5f, 0f, LH - 1.001f); int y0 = (int)ly; var fy = ly - y0;
            for (var x = 0; x < W; x++)
            {
                var lx = Math.Clamp((x + 0.5f) / Scale - 0.5f, 0f, LW - 1.001f); int x0 = (int)lx; var fx = lx - x0;
                var k = y0 * LW + x0;
                float S(int o) { var a = _x[o + k] + (_x[o + k + 1] - _x[o + k]) * fx; var b = _x[o + k + LW] + (_x[o + k + LW + 1] - _x[o + k + LW]) * fx; return a + (b - a) * fy; }
                var c = Decode(S(0), S(Plane), S(2 * Plane));
                var det = (S(3 * Plane) + 1f) * 0.5f;
                var g = Grain * det * Hash(x + gx, y + gy) * 2f;
                var i = (y * W + x) * 3;
                Frame[i] = Math.Clamp(c.X + g, 0f, 1f); Frame[i + 1] = Math.Clamp(c.Y + g, 0f, 1f); Frame[i + 2] = Math.Clamp(c.Z + g, 0f, 1f);
            }
        }
        LastDecodeMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
}
