using System.Numerics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.SplatEnvironment;
namespace PromptFlowStudio.Services.GaussianFaceHand;

/// <summary>안면 가우시안 부위(정렬 순서 = 저장 순서 · 블렌드쉐이프 영향 구간이 연속 구간이 되도록 배치).</summary>
public enum FaceRegion : byte { EyeBall, LidUpper, LidLower, Brow, Forehead, Temple, Nose, Cheek, LipUpper, LipLower, TeethUpper, TeethLower, Tongue, Cavity, Chin }

/// <summary>
/// v17.58 음소 타임코드 링버퍼(64 · 무할당) — v17.32 더빙 음소(<see cref="Push"/>) 또는 내장 절차 문장(<see cref="Procedural"/>)을
/// 끝 시각에 이어 붙이고, <see cref="Evaluate"/> 가 현재 음소 → 다음 음소를 마지막 35 % 구간에서 스무스스텝 동시조음 블렌드해
/// v17.32 입모양 표(<see cref="PhonemeStreamProceduralLipSyncSynthesizer.TableWeight"/>) 18 형태를 ARKit 52 계수에 기록한다.
/// </summary>
public sealed class FacialPhonemeRingTimeline
{
    public const int Ring = 64;
    private static readonly byte[] PatPh =
    {
        0, (byte)LipPhoneme.A, (byte)LipPhoneme.N, (byte)LipPhoneme.I, (byte)LipPhoneme.O, (byte)LipPhoneme.N, 0, (byte)LipPhoneme.A, (byte)LipPhoneme.Sz, (byte)LipPhoneme.E,
        (byte)LipPhoneme.I, (byte)LipPhoneme.O, 0, (byte)LipPhoneme.Pbm, (byte)LipPhoneme.U, (byte)LipPhoneme.R, (byte)LipPhoneme.O, (byte)LipPhoneme.Pbm, (byte)LipPhoneme.U,
        (byte)LipPhoneme.Td, (byte)LipPhoneme.U, (byte)LipPhoneme.Fv, (byte)LipPhoneme.L, (byte)LipPhoneme.O, (byte)LipPhoneme.W, 0,
    };
    private static readonly float[] PatMs = { 120, 140, 90, 110, 130, 80, 60, 130, 90, 120, 100, 140, 150, 90, 120, 80, 120, 70, 100, 80, 90, 80, 90, 140, 150, 260 };
    private readonly byte[] _ph = new byte[Ring];
    private readonly double[] _end = new double[Ring];
    private long _head, _tail;
    private double _endMs;
    private int _pat;

    public bool Procedural { get; set; } = true;
    public float Gain { get; set; } = 1f;
    public long Pushed { get; private set; }
    public long Dropped { get; private set; }
    public int Pending => (int)(_head - _tail);
    public LipPhoneme Current { get; private set; }
    public LipPhoneme Next { get; private set; }
    public float Coarticulation { get; private set; }

    public void Reset() { _head = _tail = 0; _endMs = 0; _pat = 0; Current = Next = LipPhoneme.Rest; }

    /// <summary>음소 1 개를 타임라인 끝(또는 현재 시각 이후)에 추가 — 가득 차면 가장 오래된 것을 덮어씀(드롭 계수).</summary>
    public void Push(LipPhoneme p, float durMs, double nowMs = 0)
    {
        if (_head - _tail >= Ring) { _tail++; Dropped++; }
        if (_endMs < nowMs) _endMs = nowMs;
        _endMs += Math.Max(durMs, 1f);
        var s = (int)(_head % Ring);
        _ph[s] = (byte)p; _end[s] = _endMs;
        _head++; Pushed++;
    }

    public void Evaluate(double tMs, Span<float> w52)
    {
        if (Procedural) while (_endMs < tMs + 400 && _head - _tail < Ring - 1) { Push((LipPhoneme)PatPh[_pat], PatMs[_pat], tMs); _pat = (_pat + 1) % PatPh.Length; }
        while (_head - _tail > 0 && _end[(int)(_tail % Ring)] <= tMs) _tail++;
        LipPhoneme cur = LipPhoneme.Rest, nxt = LipPhoneme.Rest; var a = 0f;
        if (_head - _tail > 0)
        {
            var s = (int)(_tail % Ring);
            cur = (LipPhoneme)_ph[s];
            var start = _head - _tail > 0 && _tail > 0 ? _end[(int)((_tail - 1) % Ring)] : _end[s] - 100;
            var dur = Math.Max(_end[s] - start, 1);
            var ph = (float)Math.Clamp((tMs - start) / dur, 0, 1);
            if (_head - _tail > 1) nxt = (LipPhoneme)_ph[(int)((_tail + 1) % Ring)];
            var x = Math.Clamp((ph - 0.65f) / 0.35f, 0f, 1f); a = x * x * (3f - 2f * x);
        }
        Current = cur; Next = nxt; Coarticulation = a;
        for (var m = 0; m < PhonemeStreamProceduralLipSyncSynthesizer.MouthCount; m++)
        {
            var v = (PhonemeStreamProceduralLipSyncSynthesizer.TableWeight(cur, m) * (1f - a) + PhonemeStreamProceduralLipSyncSynthesizer.TableWeight(nxt, m) * a) * Gain;
            ref var d = ref w52[(int)PhonemeStreamProceduralLipSyncSynthesizer.Mouth[m]];
            if (v > d) d = v;
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.58.0] 😊 실시간 ARKit 52 가우시안 페이셜 블렌드쉐이프 델타 엔진(<see cref="Blend"/> · <see cref="Commit"/> 무할당 · 60 fps).
/// 머리 로컬 프레임(원점 = v17.57 머리 분절 중심 · x = 좌→우 · y = 위 · z = 전방) 타원면 안면 마스크 위 15 000 가우시안(피부 · 눈꺼풀 · 눈썹 · 입술 ·
/// 안구 · 치아 · 혀 · 구강). 표정 k 마다 기저 델타 Δμ_k(평활 변위장 · 안구 응시/턱 개구는 강체 회전 현) 와 국소 회전 벡터 ω_k
/// (야코비안 J_k = I + ∂Δ_k/∂x(중앙 차분) 의 그람-슈미트 회전 성분 → 축·각) 를 영향 연속 구간 [lo, hi) 에만 SoA 로 저장하고,
/// 프레임마다 μ = μ₀ + Σ w_k Δμ_k · ω = Σ w_k ω_k 를 Vector&lt;float&gt; 로 선형 중첩한다(스칼라 꼬리 동일 수식).
/// <see cref="Commit"/> = 회전 공분산 Σ = A Σ₀ Aᵀ(A = R_head·F · (I + K + ½K²), K = [ω]×) — 어떤 중첩에도 양정치 —
/// 와 머리 강체 μ_w = A_head μ + t 를 장면 SoA 에 기록(SIMD). 개구도 → 치아 · 혀 · 구강 불투명도(입술 차폐).
/// </summary>
public sealed class RealtimeGaussianFacialBlendshapeEngine
{
    public const int K = NeuralFacialMuscleSimulator.ShapeCount, DefaultCount = 15_000, Interior = 3_800;
    public const float VMouth = -0.46f, MouthHalfW = 0.26f, EyeU = 0.30f, EyeV = 0.20f, EyeRadius = 0.012f, JawAngle = 0.36f;
    public static readonly Vector3 Axes = new(0.092f, 0.122f, 0.105f), JawPivot = new(0f, -0.02f, -0.01f);
    private const float Us = 1.30f, Vs = 1.0f;
    public readonly int Count;
    // 휴지(로컬) · 누적(로컬) SoA
    private readonly float[][] _rest = new float[9][], _acc = new float[6][];
    private readonly float[][][] _delta = new float[K][][];
    private readonly int[] _lo = new int[K], _len = new int[K];
    private readonly float[] _op0, _w = new float[K];
    private readonly FaceRegion[] _reg;
    private readonly Vector3[] _nrm;
    private readonly int[] _regStart = new int[16];
    private readonly Vector3[] _eyeC = new Vector3[2];

    public RealtimeGaussianFacialBlendshapeEngine(int count = DefaultCount, uint seed = 0xFACE5u)
    {
        Count = Math.Max(count, Interior + 2000);
        for (var c = 0; c < 9; c++) _rest[c] = new float[Count];
        for (var c = 0; c < 6; c++) _acc[c] = new float[Count];
        _op0 = new float[Count]; _reg = new FaceRegion[Count]; _nrm = new Vector3[Count];
        Colors = new Vector3[Count];
        Build(seed);
        for (var k = 0; k < K; k++) BuildDelta(k);
    }

    public bool UseSimd { get; set; } = Vector.IsHardwareAccelerated;
    public float[] X => _acc[0];
    public float[] Y => _acc[1];
    public float[] Z => _acc[2];
    /// <summary>누적 국소 회전 벡터 ω(c = 0 x · 1 y · 2 z · 라디안 축각).</summary>
    public float[] Omega(int c) => _acc[3 + c];
    public float RestCov(int c, int i) => _rest[3 + c][i];
    public Vector3 OmegaOf(int i) => new(_acc[3][i], _acc[4][i], _acc[5][i]);
    public Vector3 RestLocal(int i) => new(_rest[0][i], _rest[1][i], _rest[2][i]);
    public Vector3 Local(int i) => new(_acc[0][i], _acc[1][i], _acc[2][i]);
    public Vector3 RestNormal(int i) => _nrm[i];
    public FaceRegion Region(int i) => _reg[i];
    public int RegionStart(FaceRegion r) => _regStart[(int)r];
    public int RegionEnd(FaceRegion r) => _regStart[(int)r + 1];
    public Vector3 EyeCenter(int side) => _eyeC[side];
    public Vector3[] Colors { get; }
    public float RestOpacity(int i) => _op0[i];
    public ReadOnlySpan<float> Weights => _w;
    public float Weight(ArkitBlendshape k) => _w[(int)k];
    public int SpanLength(int k) => _len[k];
    public int ActiveShapes { get; private set; }
    public long BlendedLanes { get; private set; }
    public float Aperture { get; private set; }
    public float InteriorOpacity { get; private set; }
    public double LastBlendUs { get; private set; }
    public double LastCommitUs { get; private set; }
    public long Frames { get; private set; }

    public void SetWeights(ReadOnlySpan<float> w52) { for (var k = 0; k < K; k++) _w[k] = float.IsFinite(w52[k]) ? Math.Clamp(w52[k], -0.5f, 2.5f) : 0f; }
    public void SetWeight(ArkitBlendshape k, float w) => _w[(int)k] = float.IsFinite(w) ? Math.Clamp(w, -0.5f, 2.5f) : 0f;

    // ══════════════════════ 기하(비핫패스) ══════════════════════
    public static Vector3 Surface(float u, float v)
    {
        float th = u * Us, ph = v * Vs;
        return new Vector3(Axes.X * MathF.Sin(th) * MathF.Cos(ph), Axes.Y * MathF.Sin(ph), Axes.Z * MathF.Cos(th) * MathF.Cos(ph));
    }

    public static Vector3 SurfaceNormal(Vector3 p) => Vector3.Normalize(new Vector3(p.X / (Axes.X * Axes.X), p.Y / (Axes.Y * Axes.Y), p.Z / (Axes.Z * Axes.Z)));

    public static void UV(Vector3 p, out float u, out float v)
    {
        float x = p.X / Axes.X, y = p.Y / Axes.Y, z = p.Z / Axes.Z;
        u = MathF.Atan2(x, z) / Us; v = MathF.Atan2(y, MathF.Sqrt(x * x + z * z)) / Vs;
    }

    private static float Sm(float x) { x = Math.Clamp(x, 0f, 1f); return x * x * (3f - 2f * x); }
    private static float G(float du, float dv, float su, float sv) => MathF.Exp(-(du * du) / (su * su) - (dv * dv) / (sv * sv));
    private static float LipUp(float u) { var q = u / MouthHalfW; return q * q >= 1f ? 0f : 0.065f * MathF.Sqrt(1f - q * q) + 0.004f; }
    private static float LipLo(float u) { var q = u / MouthHalfW; return q * q >= 1f ? 0f : 0.075f * MathF.Sqrt(1f - q * q) + 0.004f; }

    private static FaceRegion Classify(float u, float v, out bool reject)
    {
        reject = false;
        var au = MathF.Abs(u);
        float eu = au - EyeU, ev = v - EyeV;
        var eo = eu * eu / (0.12f * 0.12f) + ev * ev / (0.05f * 0.05f);
        if (eo < 1f) { reject = true; return FaceRegion.Cheek; }
        if (eu * eu / (0.17f * 0.17f) + ev * ev / (0.13f * 0.13f) < 1f) return ev >= 0f ? FaceRegion.LidUpper : FaceRegion.LidLower;
        var arch = 0.40f + 0.03f * (1f - MathF.Abs(au - 0.30f) / 0.2f);
        if (au is > 0.10f and < 0.50f && MathF.Abs(v - arch) < 0.04f) return FaceRegion.Brow;
        if (au < MouthHalfW)
        {
            if (v >= VMouth && v < VMouth + LipUp(u)) return FaceRegion.LipUpper;
            if (v < VMouth && v > VMouth - LipLo(u)) return FaceRegion.LipLower;
        }
        if (au < 0.11f && v is > -0.26f and < 0.12f) return FaceRegion.Nose;
        if (v > 0.32f) return au > 0.62f ? FaceRegion.Temple : FaceRegion.Forehead;
        if (v < VMouth - 0.04f && au < 0.42f) return FaceRegion.Chin;
        return au > 0.62f && v > 0.08f ? FaceRegion.Temple : FaceRegion.Cheek;
    }

    private static Vector3 RegionColor(FaceRegion r) => r switch
    {
        FaceRegion.Brow => new(0.18f, 0.12f, 0.08f),
        FaceRegion.LipUpper or FaceRegion.LipLower => new(0.72f, 0.33f, 0.33f),
        FaceRegion.TeethUpper or FaceRegion.TeethLower => new(0.93f, 0.91f, 0.85f),
        FaceRegion.Tongue => new(0.80f, 0.36f, 0.40f),
        FaceRegion.Cavity => new(0.22f, 0.05f, 0.06f),
        FaceRegion.LidUpper or FaceRegion.LidLower => new(0.76f, 0.56f, 0.47f),
        _ => new(0.82f, 0.62f, 0.52f),
    };

    private void Build(uint seed)
    {
        var rng = seed == 0 ? 1u : seed;
        float R() { rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5; return (rng >> 8) * (1f / 16777216f); }
        var n = Count;
        var pos = new Vector3[n]; var nrm = new Vector3[n]; var reg = new FaceRegion[n]; var col = new Vector3[n]; var sc = new Vector3[n];
        var m = 0;
        var skin = n - Interior;
        var disc = MathF.Sqrt(0.043f / skin) * 0.8f;
        while (m < skin)
        {
            float u = 2f * R() - 1f, v = 2f * R() - 1f;
            if (R() > MathF.Cos(v * Vs)) continue;
            var r = Classify(u, v, out var rej);
            if (rej || MathF.Abs(u) > 0.92f) continue;
            var p = Surface(u, v); var nn = SurfaceNormal(p);
            // 코 융기 · 입술 볼륨(휴지 형상)
            p += nn * (0.018f * G(u, v + 0.15f, 0.075f, 0.17f) + 0.004f * G(u, v - VMouth, 0.24f, 0.08f));
            pos[m] = p; nrm[m] = nn; reg[m] = r; col[m] = RegionColor(r);
            sc[m] = new Vector3(disc, disc, disc * 0.2f);
            m++;
        }
        for (var side = 0; side < 2; side++)
        {
            var s0 = Surface(side == 0 ? -EyeU : EyeU, EyeV); var en = SurfaceNormal(s0);
            _eyeC[side] = s0 - en * (EyeRadius + 0.0015f);
        }
        void Inset(FaceRegion r, int cnt, float u0, float u1, float v0, float v1, float depth, float sz)
        {
            for (var j = 0; j < cnt; j++)
            {
                float u = u0 + (u1 - u0) * R(), v = v0 + (v1 - v0) * R();
                var p = Surface(u, v); var nn = SurfaceNormal(p);
                pos[m] = p - nn * depth; nrm[m] = nn; reg[m] = r; col[m] = RegionColor(r); sc[m] = new Vector3(sz, sz, sz * 0.4f); m++;
            }
        }
        for (var e = 0; e < 1400; e++)
        {
            var side = e & 1; var c = _eyeC[side]; var en = SurfaceNormal(Surface(side == 0 ? -EyeU : EyeU, EyeV));
            Vector3 d;
            do { d = new Vector3(2f * R() - 1f, 2f * R() - 1f, 2f * R() - 1f); } while (d.LengthSquared() is > 1f or < 1e-4f || Vector3.Dot(Vector3.Normalize(d), en) < 0.25f);
            d = Vector3.Normalize(d);
            var ang = MathF.Acos(Math.Clamp(Vector3.Dot(d, en), -1f, 1f));
            pos[m] = c + d * EyeRadius; nrm[m] = d; reg[m] = FaceRegion.EyeBall;
            col[m] = ang < 0.16f ? new Vector3(0.02f, 0.02f, 0.02f) : ang < 0.42f ? new Vector3(0.28f, 0.17f, 0.08f) : new Vector3(0.92f, 0.92f, 0.90f);
            sc[m] = new Vector3(0.0011f, 0.0011f, 0.0003f); m++;
        }
        Inset(FaceRegion.TeethUpper, 600, -0.17f, 0.17f, VMouth + 0.004f, VMouth + 0.034f, 0.0072f, 0.0012f);
        Inset(FaceRegion.TeethLower, 600, -0.16f, 0.16f, VMouth - 0.034f, VMouth - 0.004f, 0.0075f, 0.0012f);
        Inset(FaceRegion.Tongue, 500, -0.12f, 0.12f, VMouth - 0.06f, VMouth - 0.015f, 0.016f, 0.0018f);
        Inset(FaceRegion.Cavity, n - m, -0.22f, 0.22f, VMouth - 0.08f, VMouth + 0.06f, 0.026f, 0.0028f);
        // 부위 순 계수 정렬 → 연속 구간
        Span<int> cnt = stackalloc int[16];
        for (var i = 0; i < n; i++) cnt[(int)reg[i]]++;
        for (var r = 0; r < 15; r++) _regStart[r + 1] = _regStart[r] + cnt[r];
        Span<int> fill = stackalloc int[16]; _regStart.AsSpan(0, 16).CopyTo(fill);
        for (var i = 0; i < n; i++)
        {
            var o = fill[(int)reg[i]]++;
            var p = pos[i]; var nn = nrm[i];
            var ta = Vector3.Normalize(Vector3.Cross(MathF.Abs(nn.Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX, nn)); var tb = Vector3.Cross(nn, ta);
            var s = sc[i]; float s0 = s.X * s.X, s1 = s.Y * s.Y, s2 = s.Z * s.Z;
            _rest[0][o] = p.X; _rest[1][o] = p.Y; _rest[2][o] = p.Z;
            _rest[3][o] = ta.X * ta.X * s0 + tb.X * tb.X * s1 + nn.X * nn.X * s2;
            _rest[4][o] = ta.X * ta.Y * s0 + tb.X * tb.Y * s1 + nn.X * nn.Y * s2;
            _rest[5][o] = ta.X * ta.Z * s0 + tb.X * tb.Z * s1 + nn.X * nn.Z * s2;
            _rest[6][o] = ta.Y * ta.Y * s0 + tb.Y * tb.Y * s1 + nn.Y * nn.Y * s2;
            _rest[7][o] = ta.Y * ta.Z * s0 + tb.Y * tb.Z * s1 + nn.Y * nn.Z * s2;
            _rest[8][o] = ta.Z * ta.Z * s0 + tb.Z * tb.Z * s1 + nn.Z * nn.Z * s2;
            _reg[o] = reg[i]; _nrm[o] = nn; Colors[o] = col[i];
            _op0[o] = reg[i] is FaceRegion.Cavity ? 0.9f : 0.96f;
        }
        for (var c = 0; c < 3; c++) _rest[c].AsSpan().CopyTo(_acc[c]);
    }

    /// <summary>턱 개구 마스크 — 아랫입술 열(입꼬리 0 · 중앙 1) · 입선 아래 거리 · 하악 내부 강체.</summary>
    private static float JawMask(FaceRegion r, float u, float v)
    {
        if (r is FaceRegion.TeethLower) return 1f;
        if (r is FaceRegion.Tongue) return 0.9f;
        if (v >= VMouth || r is FaceRegion.LipUpper or FaceRegion.TeethUpper) return 0f;
        var lip = Sm((0.28f - MathF.Abs(u)) / 0.14f);
        var below = Sm((VMouth - v) / 0.30f);
        return r == FaceRegion.Cavity ? 0.9f * lip : MathF.Max(lip, below);
    }

    private static Vector3 Rot(Vector3 axis, float ang, Vector3 p, Vector3 c) => Vector3.Transform(p - c, Quaternion.CreateFromAxisAngle(axis, ang)) + c - p;

    /// <summary>표정 k 의 휴지 위치 p(부위 r) 변위(w = 1).</summary>
    private Vector3 Field(int k, FaceRegion r, Vector3 p)
    {
        UV(p, out var u, out var v);
        var au = MathF.Abs(u);
        var wl = Sm(0.5f - u / 0.12f); var wr = 1f - wl;
        var interior = r is FaceRegion.EyeBall or FaceRegion.TeethUpper or FaceRegion.TeethLower or FaceRegion.Tongue or FaceRegion.Cavity;
        bool lips = r is FaceRegion.LipUpper or FaceRegion.LipLower, peri = lips || r is FaceRegion.Cheek or FaceRegion.Chin or FaceRegion.Nose;
        bool brow = r is FaceRegion.Brow or FaceRegion.Forehead or FaceRegion.LidUpper or FaceRegion.Temple;
        var nn = SurfaceNormal(p);
        Vector3 Y = Vector3.UnitY, X = Vector3.UnitX, Zf = Vector3.UnitZ;
        var a = (ArkitBlendshape)k;
        switch (a)
        {
            case ArkitBlendshape.BrowDownLeft: case ArkitBlendshape.BrowDownRight:
            { if (!brow) return default; var s = a == ArkitBlendshape.BrowDownLeft ? -1f : 1f; return (s < 0 ? wl : wr) * G(u - s * 0.28f, v - 0.40f, 0.26f, 0.16f) * (-0.006f * Y + 0.0012f * Zf - 0.0015f * s * X); }
            case ArkitBlendshape.BrowInnerUp: return brow ? G(au - 0.14f, v - 0.40f, 0.13f, 0.16f) * 0.007f * Y : default;
            case ArkitBlendshape.BrowOuterUpLeft: case ArkitBlendshape.BrowOuterUpRight:
            { if (!brow) return default; var s = a == ArkitBlendshape.BrowOuterUpLeft ? -1f : 1f; return (s < 0 ? wl : wr) * G(u - s * 0.40f, v - 0.40f, 0.15f, 0.16f) * 0.007f * Y; }
            case ArkitBlendshape.CheekPuff: return peri ? G(au - 0.42f, v + 0.32f, 0.22f, 0.22f) * 0.009f * nn : default;
            case ArkitBlendshape.CheekSquintLeft: case ArkitBlendshape.CheekSquintRight:
            { if (r is not (FaceRegion.Cheek or FaceRegion.LidLower)) return default; var s = a == ArkitBlendshape.CheekSquintLeft ? -1f : 1f; return (s < 0 ? wl : wr) * G(u - s * 0.33f, v - 0.02f, 0.17f, 0.14f) * (0.004f * Y + 0.0015f * nn); }
            case ArkitBlendshape.EyeBlinkLeft: case ArkitBlendshape.EyeBlinkRight:
            case ArkitBlendshape.EyeSquintLeft: case ArkitBlendshape.EyeSquintRight:
            case ArkitBlendshape.EyeWideLeft: case ArkitBlendshape.EyeWideRight:
            {
                var s = a is ArkitBlendshape.EyeBlinkLeft or ArkitBlendshape.EyeSquintLeft or ArkitBlendshape.EyeWideLeft ? -1f : 1f;
                if (r is not (FaceRegion.LidUpper or FaceRegion.LidLower or FaceRegion.Cheek or FaceRegion.Brow) || u * s < 0f) return default;
                var du = (u - s * EyeU) / 0.17f; var tl = Math.Clamp(1f - du * du, 0f, 1f);
                var dv = v - EyeV;
                if (a is ArkitBlendshape.EyeBlinkLeft or ArkitBlendshape.EyeBlinkRight)
                {
                    if (r == FaceRegion.LidUpper) { var f = Math.Clamp(1f - (dv - 0.05f) / 0.09f, 0f, 1f) * tl; return f * (-0.0105f * Y + 0.0012f * nn); }
                    return r == FaceRegion.LidLower ? Math.Clamp(1f - (-dv - 0.05f) / 0.08f, 0f, 1f) * tl * 0.0015f * Y : default;
                }
                if (a is ArkitBlendshape.EyeSquintLeft or ArkitBlendshape.EyeSquintRight)
                    return r == FaceRegion.LidLower ? 0.003f * tl * Y : r == FaceRegion.LidUpper ? -0.0015f * tl * Y : r == FaceRegion.Cheek ? G(u - s * EyeU, dv + 0.12f, 0.15f, 0.08f) * 0.0015f * Y : default;
                return r == FaceRegion.LidUpper ? 0.003f * tl * Math.Clamp(1f - (dv - 0.05f) / 0.09f, 0f, 1f) * Y : r == FaceRegion.Brow ? 0.001f * G(u - s * EyeU, 0f, 0.2f, 1f) * Y : default;
            }
            case >= ArkitBlendshape.EyeLookDownLeft and <= ArkitBlendshape.EyeLookUpRight:
            {
                var left = ((int)a - (int)ArkitBlendshape.EyeLookDownLeft) % 2 == 0;
                if (r != FaceRegion.EyeBall || (u < 0f) != left) return r == FaceRegion.LidUpper && (u < 0f) == left && a is ArkitBlendshape.EyeLookUpLeft or ArkitBlendshape.EyeLookUpRight ? 0.002f * Y * Math.Clamp(1f - ((u - (left ? -EyeU : EyeU)) / 0.17f) * ((u - (left ? -EyeU : EyeU)) / 0.17f), 0f, 1f) : default;
                var c = _eyeC[left ? 0 : 1];
                return a switch
                {
                    ArkitBlendshape.EyeLookDownLeft or ArkitBlendshape.EyeLookDownRight => Rot(X, 0.40f, p, c),
                    ArkitBlendshape.EyeLookUpLeft or ArkitBlendshape.EyeLookUpRight => Rot(X, -0.35f, p, c),
                    ArkitBlendshape.EyeLookInLeft or ArkitBlendshape.EyeLookOutRight => Rot(Y, 0.40f, p, c),
                    _ => Rot(Y, -0.40f, p, c),
                };
            }
            case ArkitBlendshape.JawOpen: { var m = JawMask(r, u, v); return m > 0f ? m * Rot(X, JawAngle, p, JawPivot) : default; }
            case ArkitBlendshape.JawForward: return JawMask(r, u, v) * 0.007f * Zf;
            case ArkitBlendshape.JawLeft: return JawMask(r, u, v) * -0.007f * X;
            case ArkitBlendshape.JawRight: return JawMask(r, u, v) * 0.007f * X;
            case ArkitBlendshape.TongueOut:
                return r == FaceRegion.Tongue ? (0.032f * Zf - 0.006f * Y) * Sm(0.3f + (v - (VMouth - 0.06f)) / 0.045f) : default;
        }
        if (interior || !peri) return default;
        var dvm = v - VMouth;
        var lipW = lips ? 1f : G(0f, dvm, 1f, 0.12f) * Sm((0.42f - au) / 0.2f + 0.5f);
        switch (a)
        {
            case ArkitBlendshape.MouthClose:
            {
                var col = Sm((0.28f - au) / 0.14f);
                return dvm < 0f ? 0.012f * col * Sm(1f + dvm / 0.18f) * (Y + 0.4f * Zf) : -0.004f * col * Sm(1f - dvm / 0.12f) * Y;
            }
            case ArkitBlendshape.MouthDimpleLeft: case ArkitBlendshape.MouthDimpleRight:
            { var s = a == ArkitBlendshape.MouthDimpleLeft ? -1f : 1f; return G(u - s * MouthHalfW, dvm, 0.12f, 0.10f) * (-0.004f * nn + 0.002f * s * X); }
            case ArkitBlendshape.MouthFrownLeft: case ArkitBlendshape.MouthFrownRight:
            { var s = a == ArkitBlendshape.MouthFrownLeft ? -1f : 1f; return G(u - s * MouthHalfW, dvm, 0.16f, 0.14f) * -0.006f * Y; }
            case ArkitBlendshape.MouthFunnel:
                return lipW * G(u, dvm, 0.30f, 0.16f) * 0.006f * Zf + (r == FaceRegion.LipUpper ? 0.003f * Y : r == FaceRegion.LipLower ? -0.004f * Y : default);
            case ArkitBlendshape.MouthLeft: return G(u, dvm, 0.40f, 0.18f) * -0.008f * X;
            case ArkitBlendshape.MouthRight: return G(u, dvm, 0.40f, 0.18f) * 0.008f * X;
            case ArkitBlendshape.MouthLowerDownLeft: case ArkitBlendshape.MouthLowerDownRight:
            { if (dvm > 0f) return default; var s = a == ArkitBlendshape.MouthLowerDownLeft ? -1f : 1f; return G(u - s * 0.12f, dvm + 0.04f, 0.16f, 0.08f) * (-0.006f * Y + 0.001f * Zf); }
            case ArkitBlendshape.MouthPressLeft: case ArkitBlendshape.MouthPressRight:
            { if (!lips) return default; var s = a == ArkitBlendshape.MouthPressLeft ? -1f : 1f; return (s < 0 ? wl : wr) * ((r == FaceRegion.LipUpper ? -0.0015f : 0.0015f) * Y - 0.001f * nn); }
            case ArkitBlendshape.MouthPucker: return lipW * G(u, dvm, 0.32f, 0.17f) * new Vector3(-0.30f * p.X, 0f, 0.008f);
            case ArkitBlendshape.MouthRollLower: return r == FaceRegion.LipLower ? -0.004f * G(u, 0f, 0.32f, 1f) * nn : default;
            case ArkitBlendshape.MouthRollUpper: return r == FaceRegion.LipUpper ? -0.0035f * G(u, 0f, 0.32f, 1f) * nn : default;
            case ArkitBlendshape.MouthShrugLower: return dvm < 0f ? G(u, dvm + 0.06f, 0.30f, 0.12f) * (0.003f * Y + 0.002f * Zf) : default;
            case ArkitBlendshape.MouthShrugUpper: return dvm >= 0f ? G(u, dvm - 0.04f, 0.30f, 0.10f) * 0.002f * Y : default;
            case ArkitBlendshape.MouthSmileLeft: case ArkitBlendshape.MouthSmileRight:
            { var s = a == ArkitBlendshape.MouthSmileLeft ? -1f : 1f; return G(u - s * MouthHalfW, dvm, 0.20f, 0.16f) * (0.008f * s * X + 0.007f * Y - 0.002f * Zf) + G(u - s * 0.38f, v + 0.15f, 0.2f, 0.2f) * 0.003f * Y; }
            case ArkitBlendshape.MouthStretchLeft: case ArkitBlendshape.MouthStretchRight:
            { var s = a == ArkitBlendshape.MouthStretchLeft ? -1f : 1f; return G(u - s * MouthHalfW, dvm, 0.20f, 0.16f) * (0.009f * s * X - 0.003f * Y); }
            case ArkitBlendshape.MouthUpperUpLeft: case ArkitBlendshape.MouthUpperUpRight:
            { if (dvm < 0f) return default; var s = a == ArkitBlendshape.MouthUpperUpLeft ? -1f : 1f; return G(u - s * 0.12f, dvm - 0.04f, 0.16f, 0.08f) * 0.004f * Y; }
            case ArkitBlendshape.NoseSneerLeft: case ArkitBlendshape.NoseSneerRight:
            { var s = a == ArkitBlendshape.NoseSneerLeft ? -1f : 1f; return G(u - s * 0.09f, v + 0.10f, 0.10f, 0.12f) * 0.003f * Y; }
        }
        return default;
    }

    private void BuildDelta(int k)
    {
        const float eps = 0.0005f;
        int lo = -1, hi = -1;
        for (var i = 0; i < Count; i++)
        {
            var dd = Field(k, _reg[i], RestLocal(i));
            if (dd.LengthSquared() > 1e-12f) { if (lo < 0) lo = i; hi = i + 1; }
        }
        if (lo < 0) { _delta[k] = Array.Empty<float[]>(); return; }
        var len = hi - lo;
        var arr = new float[6][];
        for (var c = 0; c < 6; c++) arr[c] = new float[len];
        for (var j = 0; j < len; j++)
        {
            var i = lo + j; var r = _reg[i]; var p = RestLocal(i);
            var dm = Field(k, r, p);
            arr[0][j] = dm.X; arr[1][j] = dm.Y; arr[2][j] = dm.Z;
            if (dm.LengthSquared() <= 1e-12f) continue;
            var jx = (Field(k, r, p + new Vector3(eps, 0, 0)) - Field(k, r, p - new Vector3(eps, 0, 0))) / (2 * eps);
            var jy = (Field(k, r, p + new Vector3(0, eps, 0)) - Field(k, r, p - new Vector3(0, eps, 0))) / (2 * eps);
            var jz = (Field(k, r, p + new Vector3(0, 0, eps)) - Field(k, r, p - new Vector3(0, 0, eps))) / (2 * eps);
            // J = I + [jx jy jz](열) → 그람-슈미트 회전 성분 R → 축각 ω(선형 중첩 대상)
            var e0 = Vector3.Normalize(new Vector3(1 + jx.X, jx.Y, jx.Z)); var c1 = new Vector3(jy.X, 1 + jy.Y, jy.Z);
            var e1 = Vector3.Normalize(c1 - e0 * Vector3.Dot(c1, e0)); var e2 = Vector3.Cross(e0, e1);
            var q = Quaternion.CreateFromRotationMatrix(new Matrix4x4(e0.X, e0.Y, e0.Z, 0f, e1.X, e1.Y, e1.Z, 0f, e2.X, e2.Y, e2.Z, 0f, 0f, 0f, 0f, 1f));
            if (q.W < 0f) q = -q;
            var v = new Vector3(q.X, q.Y, q.Z); var s = v.Length();
            var om = s > 1e-9f ? v * (2f * MathF.Atan2(s, q.W) / s) : Vector3.Zero;
            arr[3][j] = om.X; arr[4][j] = om.Y; arr[5][j] = om.Z;
        }
        _delta[k] = arr; _lo[k] = lo; _len[k] = len;
    }

    public Vector3 Delta(int k, int i) { var j = i - _lo[k]; return (uint)j < (uint)_len[k] ? new Vector3(_delta[k][0][j], _delta[k][1][j], _delta[k][2][j]) : default; }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    /// <summary>μ = μ₀ + Σ w Δμ · ω = Σ w ω_k(로컬 누적 SoA) · 개구도 → 내부 차폐 불투명도.</summary>
    public void Blend()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var c = 0; c < 3; c++) _rest[c].AsSpan().CopyTo(_acc[c]);
        for (var c = 3; c < 6; c++) Array.Clear(_acc[c]);
        var act = 0; long lanes = 0;
        for (var k = 0; k < K; k++)
        {
            var w = _w[k]; var len = _len[k];
            if (len == 0 || MathF.Abs(w) < 1e-4f) continue;
            act++; lanes += len;
            var d = _delta[k]; var lo = _lo[k];
            for (var c = 0; c < 6; c++) AddScaled(_acc[c], lo, d[c], len, w);
        }
        ActiveShapes = act; BlendedLanes = lanes;
        float W(ArkitBlendshape b) => _w[(int)b];
        var ap = W(ArkitBlendshape.JawOpen) + 0.3f * W(ArkitBlendshape.MouthFunnel) + 0.2f * (W(ArkitBlendshape.MouthLowerDownLeft) + W(ArkitBlendshape.MouthLowerDownRight))
            + 0.15f * (W(ArkitBlendshape.MouthUpperUpLeft) + W(ArkitBlendshape.MouthUpperUpRight)) + 0.6f * W(ArkitBlendshape.TongueOut)
            - W(ArkitBlendshape.MouthClose) - 0.25f * (W(ArkitBlendshape.MouthPressLeft) + W(ArkitBlendshape.MouthPressRight));
        Aperture = Math.Clamp(ap, 0f, 1f);
        InteriorOpacity = Sm(Aperture / 0.12f);
        LastBlendUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void AddScaled(float[] dst, int lo, float[] src, int len, float w)
    {
        var j = 0;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var vc = Vector<float>.Count; var vw = new Vector<float>(w);
            for (; j <= len - vc; j += vc) (new Vector<float>(dst, lo + j) + vw * new Vector<float>(src, j)).CopyTo(dst, lo + j);
        }
        for (; j < len; j++) dst[lo + j] += w * src[j];
    }

    /// <summary>머리 강체(로컬 → 월드 · M 열 = 로컬 축의 월드 방향) · 회전 공분산으로 장면 [start, start+Count) 기록(Vector&lt;float&gt;).</summary>
    public void Commit(GaussianSplatScene sc, int start, in Matrix4x4 m, Vector3 t)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // System.Numerics 행 벡터 규약 → 열 형식 r_ij
        float r00 = m.M11, r01 = m.M21, r02 = m.M31, r10 = m.M12, r11 = m.M22, r12 = m.M32, r20 = m.M13, r21 = m.M23, r22 = m.M33;
        float[] X = _acc[0], Y = _acc[1], Z = _acc[2], Wx = _acc[3], Wy = _acc[4], Wz = _acc[5];
        float[] A = _rest[3], B = _rest[4], C = _rest[5], D = _rest[6], F = _rest[7], Gc = _rest[8];
        var k = 0; var n = Count; var o = start;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var vc = Vector<float>.Count;
            Vector<float> v00 = new(r00), v01 = new(r01), v02 = new(r02), v10 = new(r10), v11 = new(r11), v12 = new(r12), v20 = new(r20), v21 = new(r21), v22 = new(r22);
            Vector<float> tx = new(t.X), ty = new(t.Y), tz = new(t.Z), one = Vector<float>.One, half = new(0.5f);
            for (; k <= n - vc; k += vc)
            {
                var px = new Vector<float>(X, k); var py = new Vector<float>(Y, k); var pz = new Vector<float>(Z, k);
                (v00 * px + v01 * py + v02 * pz + tx).CopyTo(sc.X, o + k);
                (v10 * px + v11 * py + v12 * pz + ty).CopyTo(sc.Y, o + k);
                (v20 * px + v21 * py + v22 * pz + tz).CopyTo(sc.Z, o + k);
                // M = I + K + ½K² · K = [ω]× · K² = ωωᵀ − θ²I
                var wx = new Vector<float>(Wx, k); var wy = new Vector<float>(Wy, k); var wz = new Vector<float>(Wz, k);
                var th = wx * wx + wy * wy + wz * wz;
                var hxy = half * wx * wy; var hxz = half * wx * wz; var hyz = half * wy * wz;
                var q00 = one + half * (wx * wx - th); var q01 = hxy - wz; var q02 = hxz + wy;
                var q10 = hxy + wz; var q11 = one + half * (wy * wy - th); var q12 = hyz - wx;
                var q20 = hxz - wy; var q21 = hyz + wx; var q22 = one + half * (wz * wz - th);
                // A = R·M
                var a00 = v00 * q00 + v01 * q10 + v02 * q20; var a01 = v00 * q01 + v01 * q11 + v02 * q21; var a02 = v00 * q02 + v01 * q12 + v02 * q22;
                var a10 = v10 * q00 + v11 * q10 + v12 * q20; var a11 = v10 * q01 + v11 * q11 + v12 * q21; var a12 = v10 * q02 + v11 * q12 + v12 * q22;
                var a20 = v20 * q00 + v21 * q10 + v22 * q20; var a21 = v20 * q01 + v21 * q11 + v22 * q21; var a22 = v20 * q02 + v21 * q12 + v22 * q22;
                var a = new Vector<float>(A, k); var b = new Vector<float>(B, k); var c = new Vector<float>(C, k);
                var d = new Vector<float>(D, k); var f = new Vector<float>(F, k); var g = new Vector<float>(Gc, k);
                var m00 = a00 * a + a01 * b + a02 * c; var m01 = a00 * b + a01 * d + a02 * f; var m02 = a00 * c + a01 * f + a02 * g;
                var m10 = a10 * a + a11 * b + a12 * c; var m11 = a10 * b + a11 * d + a12 * f; var m12 = a10 * c + a11 * f + a12 * g;
                var m20 = a20 * a + a21 * b + a22 * c; var m21 = a20 * b + a21 * d + a22 * f; var m22 = a20 * c + a21 * f + a22 * g;
                var c00 = m00 * a00 + m01 * a01 + m02 * a02; var c11 = m10 * a10 + m11 * a11 + m12 * a12; var c22 = m20 * a20 + m21 * a21 + m22 * a22;
                c00.CopyTo(sc.C00, o + k); c11.CopyTo(sc.C11, o + k); c22.CopyTo(sc.C22, o + k);
                (m00 * a10 + m01 * a11 + m02 * a12).CopyTo(sc.C01, o + k);
                (m00 * a20 + m01 * a21 + m02 * a22).CopyTo(sc.C02, o + k);
                (m10 * a20 + m11 * a21 + m12 * a22).CopyTo(sc.C12, o + k);
                Vector.SquareRoot(Vector.Max(c00 + c11 + c22, Vector<float>.Zero)).CopyTo(sc.MaxSigma, o + k);
            }
        }
        for (; k < n; k++)
        {
            float px = X[k], py = Y[k], pz = Z[k];
            sc.X[o + k] = r00 * px + r01 * py + r02 * pz + t.X;
            sc.Y[o + k] = r10 * px + r11 * py + r12 * pz + t.Y;
            sc.Z[o + k] = r20 * px + r21 * py + r22 * pz + t.Z;
            float wx = Wx[k], wy = Wy[k], wz = Wz[k];
            var th = wx * wx + wy * wy + wz * wz;
            float hxy = 0.5f * wx * wy, hxz = 0.5f * wx * wz, hyz = 0.5f * wy * wz;
            float q00 = 1f + 0.5f * (wx * wx - th), q01 = hxy - wz, q02 = hxz + wy;
            float q10 = hxy + wz, q11 = 1f + 0.5f * (wy * wy - th), q12 = hyz - wx;
            float q20 = hxz - wy, q21 = hyz + wx, q22 = 1f + 0.5f * (wz * wz - th);
            float a00 = r00 * q00 + r01 * q10 + r02 * q20, a01 = r00 * q01 + r01 * q11 + r02 * q21, a02 = r00 * q02 + r01 * q12 + r02 * q22;
            float a10 = r10 * q00 + r11 * q10 + r12 * q20, a11 = r10 * q01 + r11 * q11 + r12 * q21, a12 = r10 * q02 + r11 * q12 + r12 * q22;
            float a20 = r20 * q00 + r21 * q10 + r22 * q20, a21 = r20 * q01 + r21 * q11 + r22 * q21, a22 = r20 * q02 + r21 * q12 + r22 * q22;
            float a = A[k], b = B[k], c = C[k], d = D[k], f = F[k], g = Gc[k];
            float m00 = a00 * a + a01 * b + a02 * c, m01 = a00 * b + a01 * d + a02 * f, m02 = a00 * c + a01 * f + a02 * g;
            float m10 = a10 * a + a11 * b + a12 * c, m11 = a10 * b + a11 * d + a12 * f, m12 = a10 * c + a11 * f + a12 * g;
            float m20 = a20 * a + a21 * b + a22 * c, m21 = a20 * b + a21 * d + a22 * f, m22 = a20 * c + a21 * f + a22 * g;
            float c00 = m00 * a00 + m01 * a01 + m02 * a02, c11 = m10 * a10 + m11 * a11 + m12 * a12, c22 = m20 * a20 + m21 * a21 + m22 * a22;
            sc.C00[o + k] = c00; sc.C11[o + k] = c11; sc.C22[o + k] = c22;
            sc.C01[o + k] = m00 * a10 + m01 * a11 + m02 * a12;
            sc.C02[o + k] = m00 * a20 + m01 * a21 + m02 * a22;
            sc.C12[o + k] = m10 * a20 + m11 * a21 + m12 * a22;
            sc.MaxSigma[o + k] = MathF.Sqrt(MathF.Max(c00 + c11 + c22, 0f));
        }
        // 입술 차폐: 치아 · 혀 · 구강 불투명도
        var ia = _regStart[(int)FaceRegion.TeethUpper]; var ib = _regStart[(int)FaceRegion.Cavity + 1];
        for (var i = ia; i < ib; i++) sc.Opacity[o + i] = _op0[i] * InteriorOpacity;
        Frames++;
        LastCommitUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>장면 슬롯 초기화(색 · 불투명도 · 종류) — 비핫패스.</summary>
    public void WriteStatic(GaussianSplatScene sc, int start)
    {
        for (var i = 0; i < Count; i++)
            sc.Write(start + i, RestLocal(i), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, new Vector3(0.002f), _op0[i], Colors[i], 6);
    }
}
