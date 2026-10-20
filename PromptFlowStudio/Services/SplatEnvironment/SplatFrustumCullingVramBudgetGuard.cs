using System.Numerics;
namespace PromptFlowStudio.Services.SplatEnvironment;

/// <summary>
/// [PromptFlow Studio v17.55.0] 🛡 스플랫 프러스텀 조기 컬링 &amp; VRAM 예산 폭주 방어 가드(<see cref="Cull"/> 무할당 · Vector&lt;float&gt; SoA).
/// <b>조기 컬링</b>(투영 · SH · 공분산 연산 전): 불투명도 ≤ 0.02 · 뷰 공간 z 근/원 평면 · 좌우/상하 평면(3σ 여유)을 SIMD 마스크로 판정하고
/// 통과 스플랫만 인덱스 + 16 비트 선형 깊이 키로 내보낸다(스칼라 경로와 비트 동일 · 16 384 청크 워커 병렬 스캔 + 순서 보존 압축).
/// <b>VRAM 예산</b>: 추정 = 상주(근경 + 원경/2^LOD) × 236 B × 복제 배수 + 작업 버퍼(가시 × 64 B + 타일 엔트리 × 4 B + 프레임버퍼).
/// 예산의 95 % 를 넘으면 같은 프레임 안에서 LOD 를 올려 재컬링(원경 = LOD 거리 밖 해시 솎음 1/2^LOD · 스케일 ×(1 − 0.12·LOD))하므로 예산 초과 프레임이 생기지 않고,
/// 60 % 미만이 30 프레임 지속되면 한 단계 내린다. 가시 수가 작업 버퍼 상한을 넘을 때도 같은 방식으로 LOD 를 올린다.
/// </summary>
public sealed class SplatFrustumCullingVramBudgetGuard
{
    public const float MinOpacity = 0.02f, SigmaMargin = 3f, HighWater = 0.95f, LowWater = 0.6f;
    public const int MaxLod = 6, WorkingBytesPerVisible = 64, Chunk = 16384;
    public const long DefaultBudgetBytes = 8L << 30;

    public bool Enabled { get; set; } = true;
    public bool UseSimd { get; set; } = Vector.IsHardwareAccelerated;
    public long BudgetBytes { get; set; } = DefaultBudgetBytes;
    /// <summary>상주 장면 복제 배수(대형 세트 VRAM 압력 시뮬레이션).</summary>
    public float Replication { get; set; } = 1f;
    public float LodDistance { get; set; } = 18f;
    public int LodLevel { get; private set; }
    public float FarScale => 1f - 0.12f * LodLevel;
    public int Total { get; private set; }
    public int Visible { get; private set; }
    public int CulledOpacity { get; private set; }
    public int CulledFrustum { get; private set; }
    public int CulledLod { get; private set; }
    public int CulledCapacity { get; private set; }
    public int FarSplats { get; private set; }
    public float ReductionPct => Total > 0 ? 100f * (Total - Visible) / Total : 0f;
    public long EstimateBytes { get; private set; }
    public float VramPct => BudgetBytes > 0 ? 100f * EstimateBytes / BudgetBytes : 0f;
    public long PeakEstimateBytes { get; private set; }
    public long OverBudgetFrames { get; private set; }
    public long LodRaises { get; private set; }
    public long LodDrops { get; private set; }
    public long Frames { get; private set; }
    public double LastCullUs { get; private set; }
    private int _calm;

    public void ResetMetrics() { OverBudgetFrames = LodRaises = LodDrops = Frames = 0; PeakEstimateBytes = 0; }
    public void Reset() { ResetMetrics(); LodLevel = 0; _calm = 0; }

    private static uint Hash(uint i) { i ^= i >> 16; i *= 0x7feb352du; i ^= i >> 15; i *= 0x846ca68bu; i ^= i >> 16; return i; }

    /// <summary>작업 버퍼 추정(가시 수 · 타일 엔트리 · 프레임버퍼).</summary>
    public static long WorkingBytes(int visible, int tileEntries) => (long)visible * WorkingBytesPerVisible + (long)tileEntries * 4 + (long)RealtimeGaussianSplatRasterizerEngine.W * RealtimeGaussianSplatRasterizerEngine.H * 16;

    private long Estimate(int near, int far, int visible, int tileEntries)
    {
        var resident = (near + (double)far / (1 << LodLevel)) * GaussianSplatScene.GpuBytesPerSplat * Math.Max(Replication, 0f);
        return (long)resident + WorkingBytes(visible, tileEntries);
    }

    /// <summary>
    /// 조기 컬링 + 예산 판정(무할당). 통과 스플랫 인덱스/깊이 키를 outIdx/outKey 에 원본 인덱스 오름차순으로 기록하고 개수를 반환한다.
    /// tileEntriesHint = 직전 프레임 타일 엔트리 수(작업 버퍼 추정용). parallelFor(청크 수) 가 주어지면 청크(16 384) 병렬 스캔 후 순서 보존 압축.
    /// </summary>
    public int Cull(GaussianSplatScene s, in SplatCamera cam, Span<int> outIdx, Span<ushort> outKey, int tileEntriesHint, Action<int>? parallelFor = null)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Frames++;
        var cap = Math.Min(outIdx.Length, outKey.Length);
        int n;
        long est;
        while (true)
        {
            n = Pass(s, cam, outIdx, outKey, cap, parallelFor);
            est = Estimate(s.Count - FarSplats, FarSplats, n, tileEntriesHint);
            if (!Enabled || LodLevel >= MaxLod || (est <= BudgetBytes * HighWater && CulledCapacity == 0)) break;
            LodLevel++; LodRaises++; _calm = 0;
        }
        EstimateBytes = est;
        if (est > PeakEstimateBytes) PeakEstimateBytes = est;
        if (est > BudgetBytes) OverBudgetFrames++;
        if (Enabled && LodLevel > 0 && est < BudgetBytes * LowWater && CulledCapacity == 0) { if (++_calm >= 30) { LodLevel--; LodDrops++; _calm = 0; } }
        else _calm = 0;
        LastCullUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        return n;
    }

    // ── 프레임 파라미터(Pass 시작 시 고정 · 청크 스캔이 공유) ──
    private GaussianSplatScene? _s;
    private Vector3 _e, _f, _r, _u;
    private float _tanH, _tanV, _secH, _secV, _near, _far, _keyScale, _lodD2;
    private uint _keep;
    private int[] _chIdx = Array.Empty<int>(), _chN = Array.Empty<int>(), _chStat = Array.Empty<int>();
    private ushort[] _chKey = Array.Empty<ushort>();

    /// <summary>청크 병렬 스캔용 스크래치 확보(장면 용량 증가 시에만 할당 · 비핫패스).</summary>
    public void EnsureScratch(int splats)
    {
        if (_chIdx.Length >= splats) return;
        _chIdx = new int[splats]; _chKey = new ushort[splats];
        var chunks = (splats + Chunk - 1) / Chunk;
        _chN = new int[chunks]; _chStat = new int[chunks * 4];
    }

    public int ChunkCount(GaussianSplatScene s) => (s.Count + Chunk - 1) / Chunk;

    private int Pass(GaussianSplatScene s, in SplatCamera cam, Span<int> outIdx, Span<ushort> outKey, int cap, Action<int>? parallelFor)
    {
        Total = s.Count; CulledOpacity = CulledFrustum = CulledLod = CulledCapacity = FarSplats = 0;
        _s = s; _e = cam.Position; _f = cam.Forward; _r = cam.Right; _u = cam.Up;
        _tanH = cam.TanHalfH; _tanV = cam.TanHalfV; _near = cam.Near; _far = cam.Far;
        // 측면 평면 법선 보정(3σ 구가 평면을 넘는지 — z·tan + m·√(1+tan²))
        _secH = MathF.Sqrt(1f + _tanH * _tanH); _secV = MathF.Sqrt(1f + _tanV * _tanV);
        _keyScale = 65535f / MathF.Max(_far - _near, 1e-3f);
        _lodD2 = LodDistance * LodDistance;
        _keep = LodLevel > 0 ? (uint)(65536 >> LodLevel) : 65536u;
        int n;
        if (parallelFor is not null && _chIdx.Length >= s.Count && s.Count > Chunk)
        {
            var chunks = ChunkCount(s);
            parallelFor(chunks);
            // 순서 보존 압축(원본 인덱스 오름차순 = 직렬 결과와 동일)
            n = 0;
            for (var c = 0; c < chunks; c++)
            {
                var o = c * 4;
                CulledOpacity += _chStat[o]; CulledFrustum += _chStat[o + 1]; CulledLod += _chStat[o + 2]; FarSplats += _chStat[o + 3];
                var m = _chN[c]; var take = Math.Min(m, cap - n);
                if (take > 0) { _chIdx.AsSpan(c * Chunk, take).CopyTo(outIdx.Slice(n)); _chKey.AsSpan(c * Chunk, take).CopyTo(outKey.Slice(n)); n += take; }
                CulledCapacity += m - Math.Max(take, 0);
            }
        }
        else
        {
            n = Scan(0, s.Count, outIdx, outKey, cap, out var op, out var fr, out var lod, out var far, out var capOut);
            CulledOpacity = op; CulledFrustum = fr; CulledLod = lod; FarSplats = far; CulledCapacity = capOut;
        }
        Visible = n;
        return n;
    }

    /// <summary>청크 c 스캔(워커 · 무할당) — 결과는 스크래치의 c·Chunk 구간.</summary>
    public void CullChunk(int c)
    {
        var s = _s!;
        var st = c * Chunk; var en = Math.Min(st + Chunk, s.Count);
        _chN[c] = Scan(st, en, _chIdx.AsSpan(st, en - st), _chKey.AsSpan(st, en - st), en - st, out var op, out var fr, out var lod, out var far, out _);
        var o = c * 4;
        _chStat[o] = op; _chStat[o + 1] = fr; _chStat[o + 2] = lod; _chStat[o + 3] = far;
    }

    private int Scan(int start, int end, Span<int> outIdx, Span<ushort> outKey, int cap, out int cOp, out int cFr, out int cLod, out int cFar, out int cCap)
    {
        var s = _s!;
        Vector3 e = _e, f = _f, r = _r, u = _u;
        float tanH = _tanH, tanV = _tanV, secH = _secH, secV = _secV, near = _near, far = _far, keyScale = _keyScale, lodD2 = _lodD2;
        var keep = _keep;
        int n = 0, op = 0, fr = 0, lod = 0, fa = 0, cp = 0, i = start;
        float[] X = s.X, Y = s.Y, Z = s.Z, O = s.Opacity, S = s.MaxSigma;
        if (UseSimd && Vector.IsHardwareAccelerated)
        {
            var w = Vector<float>.Count;
            Vector<float> ex = new(e.X), ey = new(e.Y), ez = new(e.Z), fx = new(f.X), fy = new(f.Y), fz = new(f.Z), rx = new(r.X), ry = new(r.Y), rz = new(r.Z), ux = new(u.X), uy = new(u.Y), uz = new(u.Z);
            Vector<float> vth = new(tanH), vtv = new(tanV), vsh = new(secH * SigmaMargin), vsv = new(secV * SigmaMargin), vn = new(near), vf = new(far), vm = new(SigmaMargin), vop = new(MinOpacity), vlod = new(lodD2);
            for (; i <= end - w; i += w)
            {
                var dx = new Vector<float>(X, i) - ex; var dy = new Vector<float>(Y, i) - ey; var dz = new Vector<float>(Z, i) - ez;
                var sg = new Vector<float>(S, i);
                var vz = dx * fx + dy * fy + dz * fz;
                var vx = dx * rx + dy * ry + dz * rz;
                var vy = dx * ux + dy * uy + dz * uz;
                var opOk = Vector.GreaterThan(new Vector<float>(O, i), vop);
                var inF = Vector.GreaterThan(vz + vm * sg, vn) & Vector.LessThan(vz - vm * sg, vf)
                    & Vector.LessThanOrEqual(Vector.Abs(vx), vz * vth + vsh * sg) & Vector.LessThanOrEqual(Vector.Abs(vy), vz * vtv + vsv * sg);
                var farM = Vector.GreaterThan(dx * dx + dy * dy + dz * dz, vlod);
                for (var k = 0; k < w; k++)
                {
                    var isFar = farM[k] != 0;
                    if (isFar) fa++;
                    if (opOk[k] == 0) { op++; continue; }
                    if (inF[k] == 0) { fr++; continue; }
                    Emit(i + k, vz[k], isFar, keep, near, keyScale, outIdx, outKey, ref n, cap, ref lod, ref cp);
                }
            }
        }
        for (; i < end; i++)
        {
            float dx = X[i] - e.X, dy = Y[i] - e.Y, dz = Z[i] - e.Z, sg = S[i];
            var vz = dx * f.X + dy * f.Y + dz * f.Z;
            var vx = dx * r.X + dy * r.Y + dz * r.Z;
            var vy = dx * u.X + dy * u.Y + dz * u.Z;
            var isFar = dx * dx + dy * dy + dz * dz > lodD2;
            if (isFar) fa++;
            if (!(O[i] > MinOpacity)) { op++; continue; }
            if (!(vz + SigmaMargin * sg > near && vz - SigmaMargin * sg < far && MathF.Abs(vx) <= vz * tanH + secH * SigmaMargin * sg && MathF.Abs(vy) <= vz * tanV + secV * SigmaMargin * sg)) { fr++; continue; }
            Emit(i, vz, isFar, keep, near, keyScale, outIdx, outKey, ref n, cap, ref lod, ref cp);
        }
        cOp = op; cFr = fr; cLod = lod; cFar = fa; cCap = cp;
        return n;
    }

    private static void Emit(int i, float vz, bool isFar, uint keep, float near, float keyScale, Span<int> outIdx, Span<ushort> outKey, ref int n, int cap, ref int lod, ref int capOut)
    {
        if (isFar && keep < 65536u && (Hash((uint)i) & 0xFFFFu) >= keep) { lod++; return; }
        if (n >= cap) { capOut++; return; }
        outIdx[n] = i;
        outKey[n] = (ushort)Math.Clamp((int)((vz - near) * keyScale), 0, 65535);
        n++;
    }
}
