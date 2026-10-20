using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.WorldExpansion;

/// <summary>프롬프트에서 해석한 월드 스타일.</summary>
public readonly record struct WorldStyle(string Name, Vector3 Ground, Vector3 Accent, float HeightAmp, float Density, float Roughness, float Terrace);

/// <summary>월드 확장 SIMD 커널 — 모든 경로 public · 스칼라 쌍둥이와 비트 동일.</summary>
public static class WorldKernels
{
    /// <summary>y[o·n + b] = act(bias[o] + Σᵢ w[o·inDim + i]·x[i·n + b]) — 특징 우선(feature-major) 배치 · i 오름차순 누적.</summary>
    public static void DenseScalar(ReadOnlySpan<float> w, ReadOnlySpan<float> bias, ReadOnlySpan<float> x, int inDim, int outDim, int n, Span<float> y, bool relu)
    {
        for (var o = 0; o < outDim; o++)
            for (var b = 0; b < n; b++)
            {
                var acc = bias[o];
                for (var i = 0; i < inDim; i++) acc += w[o * inDim + i] * x[i * n + b];
                y[o * n + b] = relu ? MathF.Max(acc, 0f) : acc;
            }
    }

    public static void DenseSimd(ReadOnlySpan<float> w, ReadOnlySpan<float> bias, ReadOnlySpan<float> x, int inDim, int outDim, int n, Span<float> y, bool relu)
    {
        var vw = Vector<float>.Count;
        if (!Vector.IsHardwareAccelerated || !BitConverter.IsLittleEndian || n < vw) { DenseScalar(w, bias, x, inDim, outDim, n, y, relu); return; }
        ref var rx = ref MemoryMarshal.GetReference(x);
        ref var ry = ref MemoryMarshal.GetReference(y);
        for (var o = 0; o < outDim; o++)
        {
            var b = 0;
            for (; b <= n - vw; b += vw)
            {
                var acc = new Vector<float>(bias[o]);
                for (var i = 0; i < inDim; i++) acc += new Vector<float>(w[o * inDim + i]) * Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref rx, i * n + b)));
                if (relu) acc = Vector.Max(acc, Vector<float>.Zero);
                Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref ry, o * n + b)), acc);
            }
            for (; b < n; b++)
            {
                var acc = bias[o];
                for (var i = 0; i < inDim; i++) acc += w[o * inDim + i] * x[i * n + b];
                y[o * n + b] = relu ? MathF.Max(acc, 0f) : acc;
            }
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.27.0] 🌍 실시간 생성형 3D 신경망 월드 확장 엔진(<see cref="Evaluate"/> · <see cref="GenerateTile"/> 무할당).
///   ▸ Instant-NGP 스타일 다해상도 해시 그리드(2D · 8 레벨 · 4096 엔트리 · 특징 2 · 해상도 0.25→6.7 /m 기하 증가) → 이중선형 보간 16 차원 인코딩.
///   ▸ 소형 MLP 16→16(ReLU)→5 (높이 잔차 · 알베도 RGB · 식생 밀도) — 특징 우선 배치 Vector&lt;float&gt; 밀집 층(스칼라 쌍둥이 비트 동일).
///   ▸ 프롬프트 → 키워드 스타일(숲 · 사막 · 설원 · 화산 · 도시) + FNV 시드 → 해시 테이블/가중치 결정적 재생성 · 기존 세트 팔레트(v17.08 HDRI 세트) 30 % 혼합.
///   ▸ 타일(8 m) × 옥트리 LoD(0–3: 16² · 8² · 4² · 2² 가우시안) — 정점 (res+1)² 높이장 → 셀 중심 가우시안(v17.20 형식: 위치 · 알베도 · 법선 · 스케일 · 불투명도).
/// </summary>
public sealed class GenerativeNeuralWorldExpansionEngine
{
    public const int Levels = 8, TableBits = 12, TableSize = 1 << TableBits, Feat = 2, EncDim = Levels * Feat, Hidden = 16, Outputs = 5, MaxBatch = 320, MaxLod = 3;
    public const float TileSize = 8f, BaseRes = 0.25f, ResGrowth = 1.6f;

    public static readonly WorldStyle[] Styles =
    {
        new("숲", new Vector3(0.22f, 0.36f, 0.14f), new Vector3(0.08f, 0.22f, 0.07f), 3.5f, 0.75f, 0.5f, 0f),
        new("사막", new Vector3(0.78f, 0.64f, 0.42f), new Vector3(0.66f, 0.48f, 0.3f), 2.5f, 0.05f, 0.25f, 0f),
        new("설원", new Vector3(0.9f, 0.92f, 0.96f), new Vector3(0.62f, 0.7f, 0.8f), 5f, 0.15f, 0.4f, 0f),
        new("화산", new Vector3(0.16f, 0.12f, 0.11f), new Vector3(0.75f, 0.2f, 0.05f), 6f, 0.2f, 0.9f, 0f),
        new("도시", new Vector3(0.45f, 0.46f, 0.5f), new Vector3(0.28f, 0.3f, 0.36f), 1.5f, 0.4f, 0.2f, 1.5f),
    };
    private static readonly string[][] Keys =
    {
        new[] { "숲", "forest", "정글", "jungle", "나무", "tree" },
        new[] { "사막", "desert", "모래", "sand", "dune" },
        new[] { "설원", "snow", "눈", "ice", "빙하", "glacier" },
        new[] { "화산", "volcan", "용암", "lava", "magma" },
        new[] { "도시", "city", "urban", "건물", "street", "거리" },
    };

    private readonly float[] _table = new float[Levels * TableSize * Feat];
    private readonly float[] _res = new float[Levels];
    private readonly float[] _w1 = new float[Hidden * EncDim], _b1 = new float[Hidden], _w2 = new float[Outputs * Hidden], _b2 = new float[Outputs];
    private readonly float[] _enc = new float[EncDim * MaxBatch], _hid = new float[Hidden * MaxBatch], _out = new float[Outputs * MaxBatch];
    private readonly float[] _px = new float[MaxBatch], _pz = new float[MaxBatch];
    private readonly float[] _hgt = new float[MaxBatch], _cr = new float[MaxBatch], _cg = new float[MaxBatch], _cb = new float[MaxBatch], _den = new float[MaxBatch];
    private readonly float[] _vh = new float[MaxBatch];   // 정점 높이장((res+1)² ≤ 289)

    public GenerativeNeuralWorldExpansionEngine()
    {
        for (var l = 0; l < Levels; l++) _res[l] = BaseRes * MathF.Pow(ResGrowth, l);
        SetPrompt("안개 낀 침엽수 숲과 완만한 언덕");
    }

    public string Prompt { get; private set; } = "";
    public WorldStyle Style { get; private set; } = Styles[0];
    public int StyleIndex { get; private set; }
    public uint Seed { get; private set; }
    public int Epoch { get; private set; }
    public bool UseSimd { get; set; } = true;
    public Vector3 SetPalette { get; set; } = new(0.5f, 0.48f, 0.44f);
    public long Evaluations { get; private set; }
    public long TilesGenerated { get; private set; }
    public double LastGenerateUs { get; private set; }
    public float Resolution(int level) => _res[level];

    public static int PointsPerSide(int lod) => 16 >> lod;
    public static int PointCount(int lod) => PointsPerSide(lod) * PointsPerSide(lod);

    /// <summary>프롬프트 → 스타일 키워드 매칭 · FNV-1a 시드 → 해시 테이블/MLP 결정적 재생성(UI 경로).</summary>
    public void SetPrompt(string prompt)
    {
        prompt = string.IsNullOrWhiteSpace(prompt) ? "숲" : prompt.Trim();
        var lower = prompt.ToLowerInvariant();
        var best = 0;
        var bestPos = int.MaxValue;
        for (var s = 0; s < Keys.Length; s++)
            foreach (var k in Keys[s])
            {
                var p = lower.IndexOf(k, StringComparison.Ordinal);
                if (p >= 0 && p < bestPos) { bestPos = p; best = s; }
            }
        var h = 2166136261u;
        foreach (var ch in lower) { h ^= ch; h *= 16777619u; }
        Prompt = prompt; StyleIndex = best; Style = Styles[best]; Seed = h == 0 ? 1u : h;
        var r = Seed;
        for (var i = 0; i < _table.Length; i++) _table[i] = (Rand(ref r) * 2f - 1f) * 0.5f;
        var s1 = MathF.Sqrt(2f / EncDim);
        for (var i = 0; i < _w1.Length; i++) _w1[i] = (Rand(ref r) * 2f - 1f) * s1 * 1.7f;
        for (var i = 0; i < _b1.Length; i++) _b1[i] = (Rand(ref r) * 2f - 1f) * 0.1f;
        var s2 = MathF.Sqrt(2f / Hidden);
        for (var i = 0; i < _w2.Length; i++) _w2[i] = (Rand(ref r) * 2f - 1f) * s2 * 1.7f;
        Array.Clear(_b2);
        Epoch++;
    }

    private static float Rand(ref uint r)
    {
        r ^= r << 13; r ^= r >> 17; r ^= r << 5;
        return (r >> 8) * (1f / 16777216f);
    }

    /// <summary>n ≤ <see cref="MaxBatch"/> 점의 해시 인코딩 → MLP → 높이 · 알베도 · 밀도.</summary>
    public void Evaluate(ReadOnlySpan<float> x, ReadOnlySpan<float> z, int n, Span<float> height, Span<float> r, Span<float> g, Span<float> b, Span<float> density)
    {
        if (n > MaxBatch) throw new ArgumentOutOfRangeException(nameof(n));
        var st = Style;
        Span<float> lw = stackalloc float[Levels];
        for (var l = 0; l < Levels; l++) lw[l] = st.HeightAmp / (1f + l * l * (1.2f - st.Roughness));
        for (var p = 0; p < n; p++)
        {
            var coarse = 0f;
            for (var l = 0; l < Levels; l++)
            {
                var res = _res[l];
                var fx = x[p] * res; var fz = z[p] * res;
                var ix = (int)MathF.Floor(fx); var iz = (int)MathF.Floor(fz);
                float tx = fx - ix, tz = fz - iz;
                var basis = l * TableSize * Feat;
                int h00 = Hash(ix, iz), h10 = Hash(ix + 1, iz), h01 = Hash(ix, iz + 1), h11 = Hash(ix + 1, iz + 1);
                for (var f = 0; f < Feat; f++)
                {
                    var v = (1f - tx) * (1f - tz) * _table[basis + h00 * Feat + f] + tx * (1f - tz) * _table[basis + h10 * Feat + f]
                          + (1f - tx) * tz * _table[basis + h01 * Feat + f] + tx * tz * _table[basis + h11 * Feat + f];
                    _enc[(l * Feat + f) * n + p] = v;
                    if (f == 0) coarse += lw[l] * v;
                }
            }
            _hgt[p] = coarse;
        }
        var enc = _enc.AsSpan(0, EncDim * n);
        var hid = _hid.AsSpan(0, Hidden * n);
        var outp = _out.AsSpan(0, Outputs * n);
        if (UseSimd) { WorldKernels.DenseSimd(_w1, _b1, enc, EncDim, Hidden, n, hid, true); WorldKernels.DenseSimd(_w2, _b2, hid, Hidden, Outputs, n, outp, false); }
        else { WorldKernels.DenseScalar(_w1, _b1, enc, EncDim, Hidden, n, hid, true); WorldKernels.DenseScalar(_w2, _b2, hid, Hidden, Outputs, n, outp, false); }
        var pal = SetPalette;
        for (var p = 0; p < n; p++)
        {
            var hh = _hgt[p] + 0.3f * MathF.Tanh(outp[p]);
            if (st.Terrace > 0f) hh = MathF.Round(hh / st.Terrace) * st.Terrace;   // 도시: 계단식 블록
            height[p] = hh;
            var dn = 1f / (1f + MathF.Exp(-(outp[4 * n + p] * 2f + (st.Density - 0.5f) * 6f)));
            density[p] = dn;
            var mix = Math.Clamp(0.5f + 0.5f * MathF.Tanh(outp[n + p]), 0f, 1f);
            var c = Vector3.Lerp(st.Ground, st.Accent, mix * (0.4f + 0.6f * dn));
            c = 0.7f * c + 0.3f * pal * (c.X + c.Y + c.Z) / 1.5f;
            r[p] = Math.Clamp(c.X + 0.06f * MathF.Tanh(outp[2 * n + p]), 0f, 1f);
            g[p] = Math.Clamp(c.Y + 0.06f * MathF.Tanh(outp[3 * n + p]), 0f, 1f);
            b[p] = Math.Clamp(c.Z + 0.04f * MathF.Tanh(outp[2 * n + p] - outp[3 * n + p]), 0f, 1f);
        }
        Evaluations += n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(int x, int z) => (int)(((uint)x * 1u ^ (uint)z * 2654435761u) & (TableSize - 1));

    /// <summary>단일 지점 지형 높이(카메라 지면 추종용 · 배치 1).</summary>
    public float Height(float x, float z)
    {
        _px[0] = x; _pz[0] = z;
        Evaluate(_px, _pz, 1, _hgt.AsSpan(0, 1), _cr, _cg, _cb, _den);
        return _hgt[0];
    }

    /// <summary>타일 (tx, tz) · LoD 의 가우시안을 풀 슬랩들에 기록(크레이터 내부는 불투명도 0). 반환 = 기록 점 수.</summary>
    public int GenerateTile(int tx, int tz, int lod, GaussianWorldPool pool, ReadOnlySpan<int> slabs, ReadOnlySpan<Vector4> craters)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var res = PointsPerSide(lod);
        var vs = res + 1;
        var cell = TileSize / res;
        float ox = tx * TileSize, oz = tz * TileSize;
        // 1) 정점 높이장 (res+1)²
        var nv = vs * vs;
        for (var k = 0; k < nv; k++) { _px[k] = ox + k % vs * cell; _pz[k] = oz + k / vs * cell; }
        Evaluate(_px, _pz, nv, _vh.AsSpan(0, nv), _cr, _cg, _cb, _den);
        // 2) 셀 중심(결정적 지터) 알베도 · 밀도
        var np = res * res;
        var jr = (uint)(tx * 73856093 ^ tz * 19349663 ^ lod * 83492791) | 1u;
        for (var k = 0; k < np; k++)
        {
            _px[k] = ox + (k % res + 0.5f + (Rand(ref jr) - 0.5f) * 0.5f) * cell;
            _pz[k] = oz + (k / res + 0.5f + (Rand(ref jr) - 0.5f) * 0.5f) * cell;
        }
        Evaluate(_px, _pz, np, _hgt.AsSpan(0, np), _cr, _cg, _cb, _den);
        var scale = cell * 0.75f;
        for (var k = 0; k < np; k++)
        {
            int i = k % res, j = k / res;
            float h00 = _vh[j * vs + i], h10 = _vh[j * vs + i + 1], h01 = _vh[(j + 1) * vs + i], h11 = _vh[(j + 1) * vs + i + 1];
            float u = (_px[k] - ox) / cell - i, w = (_pz[k] - oz) / cell - j;
            u = Math.Clamp(u, 0f, 1f); w = Math.Clamp(w, 0f, 1f);
            var hy = (1f - u) * (1f - w) * h00 + u * (1f - w) * h10 + (1f - u) * w * h01 + u * w * h11;
            var veg = _den[k] > 0.55f ? (_den[k] - 0.55f) * 3f * scale : 0f;   // 식생/바위 융기
            var nrm = Vector3.Normalize(new Vector3(-((h10 - h00) + (h11 - h01)) * 0.5f / cell, 1f, -((h01 - h00) + (h11 - h10)) * 0.5f / cell));
            var pos = new Vector3(_px[k], hy + veg, _pz[k]);
            var op = 0.95f;
            for (var c = 0; c < craters.Length; c++)
                if (Vector3.DistanceSquared(pos, new Vector3(craters[c].X, craters[c].Y, craters[c].Z)) < craters[c].W * craters[c].W) { op = 0f; break; }
            var slot = slabs[k / GaussianWorldPool.SlabSize] * GaussianWorldPool.SlabSize + k % GaussianWorldPool.SlabSize;
            pool.Write(slot, pos, new Vector3(_cr[k], _cg[k], _cb[k]), nrm, scale * (1f + veg), op);
        }
        TilesGenerated++;
        LastGenerateUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return np;
    }
}

/// <summary>고정 용량 가우시안 풀(SoA · 슬랩 4개 단위 · 런타임 확장 없음).</summary>
public sealed class GaussianWorldPool
{
    public const int SlabSize = 4;

    public GaussianWorldPool(int capacity)
    {
        Capacity = capacity / SlabSize * SlabSize;
        Slabs = Capacity / SlabSize;
        X = new float[Capacity]; Y = new float[Capacity]; Z = new float[Capacity];
        R = new float[Capacity]; G = new float[Capacity]; B = new float[Capacity];
        NX = new float[Capacity]; NY = new float[Capacity]; NZ = new float[Capacity];
        Scale = new float[Capacity]; Opacity = new float[Capacity];
    }

    public int Capacity { get; }
    public int Slabs { get; }
    public readonly float[] X, Y, Z, R, G, B, NX, NY, NZ, Scale, Opacity;
    /// <summary>풀 고정 메모리(바이트) — 11 채널 float.</summary>
    public long Bytes => (long)Capacity * 11 * sizeof(float);

    public void Write(int i, Vector3 p, Vector3 c, Vector3 n, float s, float op)
    {
        X[i] = p.X; Y[i] = p.Y; Z[i] = p.Z; R[i] = c.X; G[i] = c.Y; B[i] = c.Z; NX[i] = n.X; NY[i] = n.Y; NZ[i] = n.Z; Scale[i] = s; Opacity[i] = op;
    }
}
