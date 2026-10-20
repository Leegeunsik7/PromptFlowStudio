using System.Numerics;
using PromptFlowStudio.Services.GaussianAvatar;
namespace PromptFlowStudio.Services.SplatEnvironment;

/// <summary>
/// [PromptFlow Studio v17.55.0] 🌌 실시간 3D 가우시안 스플래팅 타일 기반 래스터라이저 엔진(<see cref="Render"/> 무할당 · 320×192 · 16×16 타일 20×12).
/// ① <see cref="SplatFrustumCullingVramBudgetGuard"/> SIMD 조기 컬링 + 16 비트 선형 깊이 키 → ② EWA 투영 · SH 색(컬링 순 = 장면 순차 접근 · 슬롯 기록) →
/// ③ v17.20 8 비트 2 패스 LSD 래딕스 정렬(슬롯 번호 운반 · 안정 · 앞 → 뒤) + 워커 병렬 정렬 순 패킹 · EWA 상세(Σ' = J·W·Σ·Wᵀ·Jᵀ + 0.3 px² 저역 통과 · 시야 1.3× 클램프 · 불투명도 반영 축별 범위 ≤ 48 px · 원경 LOD 스케일) →
/// ④ 타일 비닝(카운트 → 누적합 → 정렬 순 채움이라 타일별 목록이 자동으로 깊이 순 · 엔트리 상한 초과 시 원경부터 절단) →
/// ⑤ 행별 타원 구간 해석해(꼬리 · 사각 모서리 제외) · 상주 워커 풀(코어 − 1 · 최대 7) 이 투영/SH 슬롯 청크와 타일을 원자 카운터로 나눠 처리 · 타일별 전방-후방 알파 블렌딩(α = min(0.99, o·e^power) · exp 4096 LUT · α &lt; 1/255 건너뜀 · T &lt; 10⁻⁴ 픽셀 조기 종료 · 타일 전 픽셀 포화 시 목록 종료).
/// 출력 = 사전 곱 RGB + 투과율 T(배경은 표시 단계에서 합성) · 타일별 처리 엔트리/포화율(패스 인디케이터).
/// </summary>
public sealed class RealtimeGaussianSplatRasterizerEngine : IDisposable
{
    public const int W = 320, H = 192, Tile = 16, TilesX = W / Tile, TilesY = H / Tile, TileCount = TilesX * TilesY, Px = W * H, MaxRadiusPx = 48, LutSize = 4096;
    public const float MinAlpha = 1f / 255f, MaxAlpha = 0.99f, TerminateT = 1e-4f, LowPass = 0.3f, PowerCut = -5.6f;
    public static readonly Vector3 Background = new(0.02f, 0.025f, 0.04f);
    private static readonly float[] ExpLut = BuildLut();

    /// <summary>정렬 순 패킹 스플랫(48 B · 타일 블렌딩이 앞 → 뒤 순차 접근).</summary>
    private struct Packed { public float U, V, Qa, B, Qc, Op, R, G, Bc, Rx, Ry, Cut; public int Rect, Src; }

    public readonly int VisibleCap, TileEntryCap;
    private readonly ushort[] _keys, _tk;
    private readonly int[] _vals, _tv, _order, _hist = new int[256];
    private readonly int[] _entries, _src;
    private readonly Packed[] _pk, _slot;
    private readonly int[] _tileCount = new int[TileCount], _tileStart = new int[TileCount + 1], _tileFill = new int[TileCount], _tilePass = new int[TileCount];
    private readonly byte[] _tileSat = new byte[TileCount];
    private readonly float[] _cr = new float[Px], _cg = new float[Px], _cbl = new float[Px], _t = new float[Px];
    private readonly SplatWorkerPool _pool;
    private readonly float[] _wTile, _wBasis;
    private readonly long[] _wStat;
    private GaussianSplatScene? _jScene;
    private SplatCamera _jCam;
    private SplatFrustumCullingVramBudgetGuard? _jGuard;
    private SphericalHarmonicsLightingSynthesizer? _jSh;
    private readonly Action<int> _parallelCull;
    private const int TT = Tile * Tile, StatN = 8, JobProject = 0, JobBlend = 1, JobGather = 2, JobCull = 3;
    private int _entryN;

    /// <param name="workers">상주 워커 수(−1 = 코어 수 − 1, 최대 7 · 0 = 단일 스레드).</param>
    public RealtimeGaussianSplatRasterizerEngine(int visibleCap = 1 << 18, int tileEntryCap = 1 << 20, int workers = -1)
    {
        var wn = workers < 0 ? Math.Clamp(Environment.ProcessorCount - 1, 0, 7) : Math.Min(workers, 15);
        _pool = new SplatWorkerPool(wn, RunChunk, "PF.SplatEnv.W");
        _wTile = new float[(wn + 1) * 4 * TT]; _wBasis = new float[(wn + 1) * 16]; _wStat = new long[(wn + 1) * StatN];
        _parallelCull = chunks => _pool.Run(JobCull, chunks, 1);
        VisibleCap = Math.Max(visibleCap, 16); TileEntryCap = Math.Max(tileEntryCap, 16);
        _keys = new ushort[VisibleCap]; _tk = new ushort[VisibleCap]; _vals = new int[VisibleCap]; _tv = new int[VisibleCap]; _order = new int[VisibleCap];
        _src = new int[VisibleCap]; _pk = new Packed[VisibleCap]; _slot = new Packed[VisibleCap];
        _entries = new int[TileEntryCap];
        Array.Fill(_t, 1f);
    }

    private static float[] BuildLut()
    {
        var l = new float[LutSize + 1];
        for (var k = 0; k <= LutSize; k++) l[k] = MathF.Exp(PowerCut * (k + 0.5f) / LutSize);
        l[0] = 1f;
        return l;
    }

    public bool EarlyTermination { get; set; } = true;
    /// <summary>v17.56 스플랫 방사휘도 변조기(그림자 · 상호 반사) — 투영 통과 스플랫의 SH 색을 워커에서 변조.</summary>
    public ISplatRadianceModulator? Modulator { get; set; }
    public int Visible { get; private set; }
    public int Projected { get; private set; }
    public int Offscreen { get; private set; }
    public int RadiusClamped { get; private set; }
    public int TileEntries => _entryN;
    public int TileOverflowSplats { get; private set; }
    public long PixelOps { get; private set; }
    public long BlendedFragments { get; private set; }
    public int EarlyExitTiles { get; private set; }
    public int SaturatedPixels { get; private set; }
    public double LastCullUs { get; private set; }
    public double LastSortUs { get; private set; }
    public double LastProjectUs { get; private set; }
    public double LastShUs { get; private set; }
    public double LastBinUs { get; private set; }
    public double LastBlendUs { get; private set; }
    public double LastFrameMs { get; private set; }
    public long Frames { get; private set; }
    public int Workers => _pool.Workers;
    public ReadOnlySpan<float> ColorR => _cr;
    public ReadOnlySpan<float> ColorG => _cg;
    public ReadOnlySpan<float> ColorB => _cbl;
    public ReadOnlySpan<float> Transmittance => _t;
    public ReadOnlySpan<int> SortedIndices => _vals.AsSpan(0, Visible);
    public ReadOnlySpan<ushort> SortedKeys => _keys.AsSpan(0, Visible);
    public int TilePass(int t) => _tilePass[t];
    public int TileList(int t) => _tileCount[t];
    public float TileSaturation(int t) => _tileSat[t] / 255f;
    public Vector3 Pixel(int x, int y) { var p = y * W + x; return new Vector3(_cr[p], _cg[p], _cbl[p]) + _t[p] * Background; }

    private static double Us(long t0, long t1) => (t1 - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;

    /// <summary>프레임 렌더(무할당).</summary>
    public void Render(GaussianSplatScene s, in SplatCamera cam, SplatFrustumCullingVramBudgetGuard guard, SphericalHarmonicsLightingSynthesizer sh)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _jGuard = guard;
        guard.EnsureScratch(s.Capacity);
        var n = guard.Cull(s, cam, _src, _keys, _entryN, _pool.Workers > 0 ? _parallelCull : null);
        Visible = n;
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        // 투영 · SH 는 컬링 순(원본 인덱스 오름차순 = 장면 SoA 순차 접근)으로 슬롯 j 에 기록하고, 정렬은 슬롯 번호를 운반한다
        _jScene = s; _jCam = cam; _jGuard = guard; _jSh = sh;
        Array.Clear(_wStat);
        var mod = Modulator;
        mod?.BeginFrame(s, cam, _pool.Workers + 1);
        _pool.Run(JobProject, n, 2048);
        mod?.EndFrame();
        int projected = 0, offscreen = 0, clamped = 0; long shEval = 0, shTicks = 0;
        for (var w = 0; w <= _pool.Workers; w++) { var o = w * StatN; projected += (int)_wStat[o]; offscreen += (int)_wStat[o + 1]; clamped += (int)_wStat[o + 2]; shEval += _wStat[o + 3]; shTicks = Math.Max(shTicks, _wStat[o + 4]); }
        Projected = projected; Offscreen = offscreen; RadiusClamped = clamped;
        sh.AddEvaluations(shEval);
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        var ts = t2;
        for (var j = 0; j < n; j++) _order[j] = j;
        RadixSortDepthCullingTileRasterizerGuard.RadixSort16(_keys.AsSpan(0, n), _order.AsSpan(0, n), _tk, _tv, _hist);
        _pool.Run(JobGather, n, 4096);
        var t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        Bin(n);
        var t4 = System.Diagnostics.Stopwatch.GetTimestamp();
        Blend();
        var t5 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastCullUs = Us(t0, t1); LastProjectUs = Us(t1, t2); LastShUs = shTicks * 1e6 / System.Diagnostics.Stopwatch.Frequency; LastSortUs = Us(ts, t3); LastBinUs = Us(t3, t4); LastBlendUs = Us(t4, t5);
        LastFrameMs = Us(t0, t5) / 1000.0;
        Frames++;
    }

    private void RunChunk(int job, int w, int s, int e)
    {
        if (job == JobProject) ProjectRange(w, s, e);
        else if (job == JobGather) GatherRange(s, e);
        else if (job == JobCull) { for (var c = s; c < e; c++) _jGuard!.CullChunk(c); }
        else BlendRange(w, s, e);
    }

    /// <summary>슬롯 [s, e) EWA 투영 + SH 색(워커 w · 컬링 순 = 장면 순차 접근 · 무할당).</summary>
    private void ProjectRange(int w, int js, int je)
    {
        var s = _jScene!; var guard = _jGuard!; var sh = _jSh!;
        ref readonly var cam = ref _jCam;
        var e = cam.Position; var F = cam.Forward; var R = cam.Right; var U = cam.Up;
        float tanH = cam.TanHalfH, tanV = cam.TanHalfV;
        float fx = W * 0.5f / tanH, fy = H * 0.5f / tanV, limX = 1.3f * tanH, limY = 1.3f * tanV;
        var lodD2 = guard.LodDistance * guard.LodDistance; var farS2 = guard.LodLevel > 0 ? guard.FarScale * guard.FarScale : 1f;
        var basis = _wBasis.AsSpan(w * 16, 16);
        var mod = Modulator;
        int projected = 0, offscreen = 0, clamped = 0; long shTicks = 0;
        for (var j = js; j < je; j++)
        {
            var i = _src[j];
            var p = new Vector3(s.X[i], s.Y[i], s.Z[i]);
            var d = p - e;
            var tz = Vector3.Dot(d, F);
            ref var sl = ref _slot[j];
            sl.Rect = -1; sl.Src = i;
            if (tz < cam.Near) { offscreen++; continue; }
            var tx = Vector3.Dot(d, R); var ty = Vector3.Dot(d, U);
            var iz = 1f / tz;
            var u = W * 0.5f + fx * tx * iz; var v = H * 0.5f - fy * ty * iz;
            var cx = Math.Clamp(tx * iz, -limX, limX) * tz; var cy = Math.Clamp(ty * iz, -limY, limY) * tz;
            var T0 = fx * iz * R - fx * cx * iz * iz * F;
            var T1 = -fy * iz * U + fy * cy * iz * iz * F;
            var k = guard.LodLevel > 0 && d.LengthSquared() > lodD2 ? farS2 : 1f;
            float c00 = s.C00[i] * k, c01 = s.C01[i] * k, c02 = s.C02[i] * k, c11 = s.C11[i] * k, c12 = s.C12[i] * k, c22 = s.C22[i] * k;
            var S0 = new Vector3(c00 * T0.X + c01 * T0.Y + c02 * T0.Z, c01 * T0.X + c11 * T0.Y + c12 * T0.Z, c02 * T0.X + c12 * T0.Y + c22 * T0.Z);
            var S1 = new Vector3(c00 * T1.X + c01 * T1.Y + c02 * T1.Z, c01 * T1.X + c11 * T1.Y + c12 * T1.Z, c02 * T1.X + c12 * T1.Y + c22 * T1.Z);
            var a = Vector3.Dot(T0, S0) + LowPass; var b = Vector3.Dot(T0, S1); var c = Vector3.Dot(T1, S1) + LowPass;
            var det = a * c - b * b;
            if (!(det > 1e-12f)) { offscreen++; continue; }
            // 축별 범위(타원 외접 사각형): 불투명도 반영 k·σ — o·e^(−k²/2) = 1/255 인 k(최대 3.33)
            var op = s.Opacity[i];
            var kk = MathF.Sqrt(2f * MathF.Log(MathF.Max(255f * op, 1.0001f)));
            float rx = MathF.Ceiling(kk * MathF.Sqrt(a)), ry = MathF.Ceiling(kk * MathF.Sqrt(c));
            if (rx > MaxRadiusPx || ry > MaxRadiusPx) { rx = MathF.Min(rx, MaxRadiusPx); ry = MathF.Min(ry, MaxRadiusPx); clamped++; }
            if (u + rx < 0f || u - rx >= W || v + ry < 0f || v - ry >= H) { offscreen++; continue; }
            int x0 = Math.Clamp((int)((u - rx) / Tile), 0, TilesX - 1), x1 = Math.Clamp((int)((u + rx) / Tile), 0, TilesX - 1);
            int y0 = Math.Clamp((int)((v - ry) / Tile), 0, TilesY - 1), y1 = Math.Clamp((int)((v + ry) / Tile), 0, TilesY - 1);
            var id = 1f / det;
            sl.U = u; sl.V = v; sl.Qa = -0.5f * c * id; sl.B = -b * id; sl.Qc = -0.5f * a * id; sl.Rx = rx; sl.Ry = ry; sl.Op = op;
            sl.Cut = op > MinAlpha ? MathF.Max(PowerCut, MathF.Log(MinAlpha / op)) : 0f;
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var col = sh.Color(s, i, d, basis);
            shTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
            if (mod is not null) col = mod.Modulate(w, i, p, col);
            sl.R = col.X; sl.G = col.Y; sl.Bc = col.Z;
            sl.Rect = x0 | (y0 << 8) | (x1 << 16) | (y1 << 24);
            projected++;
        }
        var so = w * StatN;
        _wStat[so] += projected; _wStat[so + 1] += offscreen; _wStat[so + 2] += clamped; _wStat[so + 3] += projected; _wStat[so + 4] += shTicks;
    }

    /// <summary>정렬 순 패킹 [s, e) — 슬롯 구조체 무작위 읽기를 워커로 분산(메모리 지연 은닉).</summary>
    private void GatherRange(int s, int e)
    {
        for (var k = s; k < e; k++) { ref var pk = ref _pk[k]; pk = _slot[_order[k]]; _vals[k] = pk.Src; }
    }

    private void Bin(int n)
    {
        Array.Clear(_tileCount);
        var total = 0; var overflow = 0;
        for (var k = 0; k < n; k++)
        {
            ref var pk = ref _pk[k];
            var r = pk.Rect;
            if (r < 0) continue;
            int x0 = r & 0xFF, y0 = (r >> 8) & 0xFF, x1 = (r >> 16) & 0xFF, y1 = (r >> 24) & 0xFF;
            var span = (x1 - x0 + 1) * (y1 - y0 + 1);
            if (total + span > TileEntryCap) { pk.Rect = -1; overflow++; continue; }
            total += span;
            for (var ty = y0; ty <= y1; ty++) for (var tx = x0; tx <= x1; tx++) _tileCount[ty * TilesX + tx]++;
        }
        var sum = 0;
        for (var t = 0; t < TileCount; t++) { _tileStart[t] = sum; _tileFill[t] = sum; sum += _tileCount[t]; }
        _tileStart[TileCount] = sum;
        for (var k = 0; k < n; k++)
        {
            var r = _pk[k].Rect;
            if (r < 0) continue;
            int x0 = r & 0xFF, y0 = (r >> 8) & 0xFF, x1 = (r >> 16) & 0xFF, y1 = (r >> 24) & 0xFF;
            for (var ty = y0; ty <= y1; ty++) for (var tx = x0; tx <= x1; tx++) _entries[_tileFill[ty * TilesX + tx]++] = k;
        }
        _entryN = total; TileOverflowSplats = overflow;
    }

    private void Blend()
    {
        Array.Clear(_wStat);
        _pool.Run(JobBlend, TileCount, 2);
        long ops = 0, frags = 0, early = 0, sat = 0;
        for (var w = 0; w <= _pool.Workers; w++) { var o = w * StatN; ops += _wStat[o + 5]; frags += _wStat[o + 6]; early += _wStat[o + 7]; sat += _wStat[o + 3]; }
        PixelOps = ops; BlendedFragments = frags; EarlyExitTiles = (int)early; SaturatedPixels = (int)sat;
    }

    /// <summary>타일 [ts, te) 전방-후방 알파 블렌딩(워커 w 전용 타일 스크래치 · 무할당).</summary>
    private void BlendRange(int w, int ts, int te)
    {
        var lt = _wTile.AsSpan(w * 4 * TT, TT); var lr = _wTile.AsSpan(w * 4 * TT + TT, TT); var lg = _wTile.AsSpan(w * 4 * TT + 2 * TT, TT); var lb = _wTile.AsSpan(w * 4 * TT + 3 * TT, TT);
        long ops = 0, frags = 0, early = 0, satTotal = 0;
        for (var t = ts; t < te; t++)
        {
            int tx0 = (t % TilesX) * Tile, ty0 = (t / TilesX) * Tile;
            lt.Fill(1f); lr.Clear(); lg.Clear(); lb.Clear();
            var alive = TT; var processed = 0;
            int s0 = _tileStart[t], s1 = _tileStart[t + 1];
            for (var q = s0; q < s1; q++)
            {
                processed++;
                ref readonly var pk = ref _pk[_entries[q]];
                float u = pk.U, v = pk.V, op = pk.Op, qa = pk.Qa, B = pk.B, qc = pk.Qc, cut = pk.Cut;
                float pr = pk.R, pg = pk.G, pb = pk.Bc;
                int px0 = Math.Max(tx0, (int)(u - pk.Rx)), px1 = Math.Min(tx0 + Tile - 1, (int)(u + pk.Rx));
                int py0 = Math.Max(ty0, (int)(v - pk.Ry)), py1 = Math.Min(ty0 + Tile - 1, (int)(v + pk.Ry));
                var inv2a = 0.5f / qa;
                for (var py = py0; py <= py1; py++)
                {
                    var dy = py + 0.5f - v;
                    var row = (py - ty0) * Tile - tx0;
                    float qb = -B * dy, q0 = qc * dy * dy;
                    // 행 구간 해석해: qa·dx² + qb·dx + q0 ≥ cut (qa < 0) → 타원 내부 dx 범위만 순회(모서리 · 꼬리 제거)
                    var disc = qb * qb - 4f * qa * (q0 - cut);
                    if (!(disc > 0f)) continue;
                    var sq = MathF.Sqrt(disc);
                    float r0 = (-qb + sq) * inv2a, r1 = (-qb - sq) * inv2a;
                    int rx0 = Math.Max(px0, (int)MathF.Ceiling(u + MathF.Min(r0, r1) - 0.5f)), rx1 = Math.Min(px1, (int)MathF.Floor(u + MathF.Max(r0, r1) - 0.5f));
                    for (var px = rx0; px <= rx1; px++)
                    {
                        var li = row + px;
                        var T = lt[li];
                        if (T < TerminateT) continue;
                        ops++;
                        var dx = px + 0.5f - u;
                        var power = (qa * dx + qb) * dx + q0;
                        if (power > 0f || power < cut) continue;
                        var alpha = MathF.Min(MaxAlpha, op * ExpLut[(int)(power * (LutSize / PowerCut))]);
                        if (alpha < MinAlpha) continue;
                        var wgt = T * alpha;
                        lr[li] += wgt * pr; lg[li] += wgt * pg; lb[li] += wgt * pb;
                        T *= 1f - alpha;
                        lt[li] = T;
                        frags++;
                        if (T < TerminateT) alive--;
                    }
                }
                if (EarlyTermination && alive == 0) { early++; break; }
            }
            _tilePass[t] = processed;
            var sat = TT - alive; satTotal += sat;
            _tileSat[t] = (byte)(sat * 255 / TT);
            for (var y = 0; y < Tile; y++)
            {
                var o = (ty0 + y) * W + tx0;
                lr.Slice(y * Tile, Tile).CopyTo(_cr.AsSpan(o, Tile));
                lg.Slice(y * Tile, Tile).CopyTo(_cg.AsSpan(o, Tile));
                lb.Slice(y * Tile, Tile).CopyTo(_cbl.AsSpan(o, Tile));
                lt.Slice(y * Tile, Tile).CopyTo(_t.AsSpan(o, Tile));
            }
        }
        var so = w * StatN;
        _wStat[so + 3] += satTotal; _wStat[so + 5] += ops; _wStat[so + 6] += frags; _wStat[so + 7] += early;
    }

    public void Dispose() => _pool.Dispose();

    /// <summary>24 비트 하향 BMP(배경 합성 · tiles = 타일 패스 히트맵 오버레이) — dst 길이 = 54 + W·H·3.</summary>
    public void WriteBmp(byte[] dst, bool tiles)
    {
        const int Row = W * 3;
        if (dst.Length < 54 + Row * H) return;
        dst.AsSpan(0, 54).Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(2), 54 + Row * H);
        dst[10] = 54; dst[14] = 40;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(18), W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst.AsSpan(22), H);
        dst[26] = 1; dst[28] = 24;
        var maxPass = 1;
        if (tiles) for (var t = 0; t < TileCount; t++) maxPass = Math.Max(maxPass, _tilePass[t]);
        for (var y = 0; y < H; y++)
        {
            var o = 54 + (H - 1 - y) * Row;
            for (var x = 0; x < W; x++)
            {
                var p = y * W + x;
                float r = _cr[p] + _t[p] * Background.X, g = _cg[p] + _t[p] * Background.Y, b = _cbl[p] + _t[p] * Background.Z;
                if (tiles)
                {
                    var h = _tilePass[(y / Tile) * TilesX + x / Tile] / (float)maxPass;
                    r = r * 0.55f + 0.45f * h; g *= 0.55f; b = b * 0.55f + 0.45f * (1f - h) * 0.4f;
                    if (x % Tile == 0 || y % Tile == 0) { r = g = b = 0.15f; }
                }
                dst[o + x * 3] = Tone(b); dst[o + x * 3 + 1] = Tone(g); dst[o + x * 3 + 2] = Tone(r);
            }
        }
    }

    private static byte Tone(float v) => (byte)Math.Clamp((int)(MathF.Sqrt(MathF.Max(v, 0f) / (1f + MathF.Max(v, 0f) * 0.1f)) * 255f + 0.5f), 0, 255);
}
