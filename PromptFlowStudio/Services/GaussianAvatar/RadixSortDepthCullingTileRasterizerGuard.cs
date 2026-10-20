using System.Numerics;

namespace PromptFlowStudio.Services.GaussianAvatar;

public enum SplatView : byte { Ellipsoid = 0, Point, Density }

/// <summary>
/// [PromptFlow Studio v17.20.0] ⚡ 서브 밀리초 래딕스 정렬 &amp; 타일 스플래팅 뎁스 가드.
/// 시점 공간 깊이를 16 비트 정수 키로 양자화 → 8 비트 2 패스 LSD 래딕스 정렬(안정 · 사전 할당 핑퐁 버퍼)로 앞 → 뒤 순서를 만들고,
/// 프러스텀 밖(근/원 평면 · 3σ 여유 측면) · 저불투명(&lt; 1/255) 타원체를 컬링한다.
/// 래스터는 EWA 투영 공분산(JΣJᵀ + 0.3 저역) 원뿔 계수로 앞 → 뒤 알파 합성하며, 픽셀 누적 알파가 0.999 에 이르면 픽셀 · 16 px 타일을 포화 처리해
/// 포화 타일만 덮는 스플랫을 조기 종료하고, 1024 스플랫마다 경과 시간을 확인해 프레임 예산(16.6 ms) 초과 전 잔여 원경 스플랫을 절단한다.
/// </summary>
public sealed class RadixSortDepthCullingTileRasterizerGuard
{
    public const int W = 240, H = 320, Tile = 16, TilesX = W / Tile, TilesY = H / Tile, TileCount = TilesX * TilesY, MaxRadius = 24;
    public const float AlphaLimit = 0.999f, FrameBudgetMs = 16.6f, MinAlpha = 1f / 255f, SubPixelLambda = 1.4f, PointSigmaPx = 0.85f, PowerCut = -4.5f;   // 투영 σ(저역 0.3 포함) < 1.2 px → 1 px · 3σ 컷

    private const int ExpBins = 255;
    /// <summary>exp(p) · p ∈ [PowerCut, 0] 256 단 LUT(빈 중앙값 · 상대 오차 &lt; 1 %) — 픽셀당 MathF.Exp 제거.</summary>
    private static readonly float[] ExpLut = BuildExpLut();

    private static float[] BuildExpLut()
    {
        var t = new float[ExpBins + 1];
        for (var k = 0; k <= ExpBins; k++) t[k] = MathF.Exp(PowerCut * MathF.Min(ExpBins, k + 0.5f) / ExpBins);
        t[0] = 1f;
        return t;
    }

    public readonly int N;
    private readonly ushort[] _keys, _keysTmp;
    private readonly int[] _vals, _valsTmp;
    private readonly int[] _hist = new int[256];
    /// <summary>정렬 후 랜덤 접근을 캐시 라인 1 개로 묶는 순차 패킹(컬링 단계 · 원본 인덱스 순).</summary>
    private struct Splat { public float Sx, Sy, Z, Op, R, G, B, SMax; public int Index; }
    private readonly Splat[] _splats;
    private readonly float[] _cr = new float[W * H], _cg = new float[W * H], _cb = new float[W * H], _t = new float[W * H];
    private readonly int[] _tileLeft = new int[TileCount];
    private readonly bool[] _tileDone = new bool[TileCount];
    private readonly float[] _density = new float[W * H];
    private Vector3 _right, _up, _fwd;

    public RadixSortDepthCullingTileRasterizerGuard(int count)
    {
        N = count;
        _keys = new ushort[count]; _keysTmp = new ushort[count]; _vals = new int[count]; _valsTmp = new int[count];
        _splats = new Splat[count];
        SetCamera(new Vector3(0f, 1f, 3f), new Vector3(0f, 1f, 0f));
    }

    public Vector3 CameraPos { get; private set; }
    public Vector3 CameraTarget { get; private set; }
    public float FovYDeg { get; set; } = 38f;
    public float Near { get; set; } = 0.1f;
    public float Far { get; set; } = 10f;
    public float Fy => H * 0.5f / MathF.Tan(FovYDeg * MathF.PI / 360f);
    public SplatView View { get; set; }
    public bool EarlyTermination { get; set; } = true;
    public bool AdaptiveLod { get; set; } = true;
    public const float MaxLod = 3f;
    /// <summary>포인트 LOD 임계 배율(1 = 기본 · 예산 초과 시 상향).</summary>
    public float LodScale { get; set; } = 1f;
    public Vector3 Background { get; set; } = new(0.02f, 0.025f, 0.045f);
    public int Visible { get; private set; }
    public int CulledFrustum { get; private set; }
    public int CulledAlpha { get; private set; }
    public int Splatted { get; private set; }
    public int SubPixelSplats { get; private set; }
    public int EarlySkipped { get; private set; }
    public int BudgetSkipped { get; private set; }
    public int SaturatedTiles { get; private set; }
    public long SaturatedPixels { get; private set; }
    public long PixelOps { get; private set; }
    public bool BudgetExceeded { get; private set; }
    public double LastCullUs { get; private set; }
    public double LastSortUs { get; private set; }
    public double LastRasterUs { get; private set; }
    public double PeakSortUs { get; private set; }
    public ReadOnlySpan<int> SortedIndices => _vals.AsSpan(0, Visible);
    public ReadOnlySpan<ushort> SortedKeys => _keys.AsSpan(0, Visible);
    public float Transmittance(int x, int y) => _t[y * W + x];
    public Vector3 Pixel(int x, int y) { var p = y * W + x; return new Vector3(_cr[p], _cg[p], _cb[p]); }

    public void SetCamera(Vector3 pos, Vector3 target)
    {
        CameraPos = pos; CameraTarget = target;
        var f = target - pos;
        var fl = f.Length();
        _fwd = fl > 1e-6f ? f / fl : -Vector3.UnitZ;
        var r = Vector3.Cross(_fwd, Vector3.UnitY);
        var rl = r.Length();
        _right = rl > 1e-6f ? r / rl : Vector3.UnitX;
        _up = Vector3.Cross(_right, _fwd);
    }

    /// <summary>16 비트 깊이 키(근 → 0 · 원 → 65535).</summary>
    public static ushort DepthKey(float z, float near, float far)
    {
        var u = (z - near) / (far - near);
        return (ushort)Math.Clamp((int)(u * 65535f), 0, 65535);
    }

    /// <summary>8 비트 2 패스 LSD 래딕스 정렬(안정 · 무할당). 결과는 <paramref name="keys"/>/<paramref name="vals"/> 에 오름차순.</summary>
    public static void RadixSort16(Span<ushort> keys, Span<int> vals, Span<ushort> tmpK, Span<int> tmpV, Span<int> hist)
    {
        var n = keys.Length;
        for (var pass = 0; pass < 2; pass++)
        {
            var shift = pass * 8;
            Span<ushort> sk = pass == 0 ? keys : tmpK, dk = pass == 0 ? tmpK : keys;
            Span<int> sv = pass == 0 ? vals : tmpV, dv = pass == 0 ? tmpV : vals;
            hist.Slice(0, 256).Clear();
            for (var i = 0; i < n; i++) hist[(sk[i] >> shift) & 0xFF]++;
            var sum = 0;
            for (var b = 0; b < 256; b++) { var c = hist[b]; hist[b] = sum; sum += c; }
            for (var i = 0; i < n; i++)
            {
                var k = sk[i];
                var d = hist[(k >> shift) & 0xFF]++;
                dk[d] = k; dv[d] = sv[i];
            }
        }
    }

    /// <summary>시점 변환 · 프러스텀/알파 컬링 · 16 비트 키 래딕스 정렬(무할당).</summary>
    public void CullAndSort(DeformableGaussianSkinningBindingEngine e, SphericalHarmonicsNeuralRelightingSynthesizer s)
    {
        var f = System.Diagnostics.Stopwatch.Frequency / 1e6;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var fy = Fy; var fx = fy;
        float tanX = W * 0.5f / fx * 1.15f, tanY = H * 0.5f / fy * 1.15f;
        var cam = CameraPos; Vector3 r = _right, u = _up, fw = _fwd;
        float near = Near, far = Far;
        int m = 0, cf = 0, ca = 0;
        for (var i = 0; i < e.N; i++)
        {
            if (e.Opacity[i] < MinAlpha) { ca++; continue; }
            float px = e.X[i] - cam.X, py = e.Y[i] - cam.Y, pz = e.Z[i] - cam.Z;
            var z = px * fw.X + py * fw.Y + pz * fw.Z;
            if (z <= near || z >= far) { cf++; continue; }
            var x = px * r.X + py * r.Y + pz * r.Z;
            var y = px * u.X + py * u.Y + pz * u.Z;
            var sMax = MathF.Max(e.ScaleX[i], MathF.Max(e.ScaleY[i], e.ScaleZ[i]));
            var ext = 3f * sMax;
            if (MathF.Abs(x) - ext > tanX * z || MathF.Abs(y) - ext > tanY * z) { cf++; continue; }
            _keys[m] = DepthKey(z, near, far); _vals[m] = m;
            ref var sp = ref _splats[m];
            sp.Sx = W * 0.5f + fx * x / z; sp.Sy = H * 0.5f - fy * y / z; sp.Z = z; sp.Op = e.Opacity[i];
            sp.R = s.R[i]; sp.G = s.G[i]; sp.B = s.B[i]; sp.SMax = sMax; sp.Index = i;
            m++;
        }
        Visible = m; CulledFrustum = cf; CulledAlpha = ca;
        var t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        RadixSort16(_keys.AsSpan(0, m), _vals.AsSpan(0, m), _keysTmp.AsSpan(0, m), _valsTmp.AsSpan(0, m), _hist);
        var t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        LastCullUs = (t1 - t0) / f; LastSortUs = (t2 - t1) / f;
        if (LastSortUs > PeakSortUs) PeakSortUs = LastSortUs;
    }

    /// <summary>앞 → 뒤 EWA 스플래팅 · 0.999 포화 조기 종료 · 예산 절단(무할당).</summary>
    public void Rasterize(DeformableGaussianSkinningBindingEngine e, SphericalHarmonicsNeuralRelightingSynthesizer s, float budgetMs)
    {
        var freq = System.Diagnostics.Stopwatch.Frequency;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var limit = t0 + (long)(MathF.Max(0.5f, budgetMs) * freq / 1000.0);
        Array.Clear(_cr); Array.Clear(_cg); Array.Clear(_cb);
        _t.AsSpan().Fill(1f);
        _tileLeft.AsSpan().Fill(Tile * Tile);
        Array.Clear(_tileDone);
        if (View == SplatView.Density) Array.Clear(_density);
        var fy = Fy; var fx = fy;
        Vector3 r = _right, u = _up, fw = _fwd;
        int splatted = 0, early = 0, budget = 0, satTiles = 0, subPx = 0; long satPx = 0, ops = 0;
        var exceeded = false;
        var m = Visible;
        var lod = LodScale;
        var pointK = fy * 1.25f / (PointSigmaPx * lod);
        var lamCut = SubPixelLambda * lod * lod;
        for (var k = 0; k < m; k++)
        {
            if ((k & 1023) == 1023 && System.Diagnostics.Stopwatch.GetTimestamp() > limit) { budget = m - k; exceeded = true; break; }
            ref readonly var sp = ref _splats[_vals[k]];
            var i = sp.Index;
            float cx = sp.Sx, cy = sp.Sy, z = sp.Z;
            float op = sp.Op, cr = sp.R, cg = sp.G, cb = sp.B;
            int x0, x1, y0, y1;
            float ca = 0f, cbn = 0f, cc = 0f;
            var point = false;
            var hs = 0;
            if (View == SplatView.Ellipsoid && sp.SMax * pointK < z)   // 보수적 상한 σ < 0.85 px × LOD → 공분산 생략
            {
                point = true; subPx++;
                hs = Math.Min(2, (int)(sp.SMax * fy / z));              // LOD 상향 시 σ 크기 박스로 구멍 메움
            }
            if (View == SplatView.Ellipsoid && !point)
            {
                // Σ(월드) → 시점 Σv = V Σ Vᵀ → 화면 Σ2 = J Σv Jᵀ + 0.3
                DeformableGaussianSkinningBindingEngine.Covariance(e.Qx[i], e.Qy[i], e.Qz[i], e.Qw[i], e.ScaleX[i], e.ScaleY[i], e.ScaleZ[i], out var s00, out var s01, out var s02, out var s11, out var s12, out var s22);
                Vector3 row0 = r, row1 = u, row2 = fw;
                float a0 = s00 * row0.X + s01 * row0.Y + s02 * row0.Z, a1 = s01 * row0.X + s11 * row0.Y + s12 * row0.Z, a2 = s02 * row0.X + s12 * row0.Y + s22 * row0.Z;
                float b0 = s00 * row1.X + s01 * row1.Y + s02 * row1.Z, b1 = s01 * row1.X + s11 * row1.Y + s12 * row1.Z, b2 = s02 * row1.X + s12 * row1.Y + s22 * row1.Z;
                float c0 = s00 * row2.X + s01 * row2.Y + s02 * row2.Z, c1 = s01 * row2.X + s11 * row2.Y + s12 * row2.Z, c2 = s02 * row2.X + s12 * row2.Y + s22 * row2.Z;
                float vxx = row0.X * a0 + row0.Y * a1 + row0.Z * a2, vxy = row1.X * a0 + row1.Y * a1 + row1.Z * a2, vxz = row2.X * a0 + row2.Y * a1 + row2.Z * a2;
                float vyy = row1.X * b0 + row1.Y * b1 + row1.Z * b2, vyz = row2.X * b0 + row2.Y * b1 + row2.Z * b2, vzz = row2.X * c0 + row2.Y * c1 + row2.Z * c2;
                // x/z = (sx − W/2)/fx · y/z = (H/2 − sy)/fy → 야코비안을 패킹 값만으로 구성
                float j00 = fx / z, j02 = -(cx - W * 0.5f) / z, j11 = -fy / z, j12 = (H * 0.5f - cy) / z;
                var sa = j00 * j00 * vxx + 2f * j00 * j02 * vxz + j02 * j02 * vzz + 0.3f;
                var sb = j00 * j11 * vxy + j00 * j12 * vxz + j02 * j11 * vyz + j02 * j12 * vzz;
                var sc = j11 * j11 * vyy + 2f * j11 * j12 * vyz + j12 * j12 * vzz + 0.3f;
                var det = sa * sc - sb * sb;
                if (!(det > 1e-12f)) continue;
                var idet = 1f / det;
                ca = sc * idet; cbn = -sb * idet; cc = sa * idet;
                var mid = 0.5f * (sa + sc);
                var lam = mid + MathF.Sqrt(MathF.Max(0.1f, mid * mid - det));
                if (lam < lamCut) { point = true; x0 = x1 = (int)cx; y0 = y1 = (int)cy; subPx++; }   // 서브픽셀 LOD — 중심 1 px 직접 합성(exp 생략)
                else
                {
                    // 축정렬 3σ 외접(√Σxx · √Σyy) — λmax 정사각형보다 타이트(가늘고 긴 헤어 스플랫)
                    float rx = MathF.Min(MaxRadius, 3f * MathF.Sqrt(sa)), ry = MathF.Min(MaxRadius, 3f * MathF.Sqrt(sc));
                    x0 = (int)(cx - rx); x1 = (int)(cx + rx); y0 = (int)(cy - ry); y1 = (int)(cy + ry);
                }
            }
            else { point = true; x0 = (int)cx - hs; x1 = (int)cx + hs; y0 = (int)cy - hs; y1 = (int)cy + hs; }
            if (x1 < 0 || y1 < 0 || x0 >= W || y0 >= H) continue;
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0); x1 = Math.Min(W - 1, x1); y1 = Math.Min(H - 1, y1);
            if (EarlyTermination && satTiles > 0)
            {
                var open = false;
                for (var ty = y0 / Tile; ty <= y1 / Tile && !open; ty++)
                    for (var tx = x0 / Tile; tx <= x1 / Tile; tx++) if (!_tileDone[ty * TilesX + tx]) { open = true; break; }
                if (!open) { early++; continue; }
            }
            splatted++;
            for (var py = y0; py <= y1; py++)
            {
                var row = py * W;
                var dy = py + 0.5f - cy;
                int rx0 = x0, rx1 = x1;
                if (!point)
                {
                    // 행 교차 구간: ca·dx² + 2·cbn·dy·dx + cc·dy² ≤ −2·PowerCut 의 근 — 가늘고 긴 타원의 빈 bbox 순회 제거
                    var bq = cbn * dy;
                    var disc = bq * bq - ca * (cc * dy * dy + 2f * PowerCut);
                    if (disc < 0f) continue;
                    var sq = MathF.Sqrt(disc);
                    rx0 = Math.Max(x0, (int)MathF.Floor(cx - 0.5f + (-bq - sq) / ca));
                    rx1 = Math.Min(x1, (int)MathF.Ceiling(cx - 0.5f + (-bq + sq) / ca));
                }
                for (var px = rx0; px <= rx1; px++)
                {
                    var p = row + px;
                    var t = _t[p];
                    if (t <= 1f - AlphaLimit && EarlyTermination) continue;   // 누적 알파 0.999 픽셀 조기 종료
                    float a;
                    if (!point)
                    {
                        var dx = px + 0.5f - cx;
                        var power = -0.5f * (ca * dx * dx + cc * dy * dy) - cbn * dx * dy;
                        if (power > 0f || power < PowerCut) continue;
                        a = MathF.Min(0.99f, op * ExpLut[(int)(power * (ExpBins / PowerCut))]);
                        if (a < MinAlpha) continue;
                    }
                    else a = MathF.Min(0.99f, op);
                    ops++;
                    if (View == SplatView.Density) { _density[p] += 1f; a *= 0.15f; }
                    var wgt = t * a;
                    _cr[p] += wgt * cr; _cg[p] += wgt * cg; _cb[p] += wgt * cb;
                    t *= 1f - a;
                    _t[p] = t;
                    if (t <= 1f - AlphaLimit)
                    {
                        satPx++;
                        var ti = py / Tile * TilesX + px / Tile;
                        if (--_tileLeft[ti] == 0) { _tileDone[ti] = true; satTiles++; }
                    }
                }
            }
        }
        // 적응형 LOD 가드: 예산 절단 → 다음 프레임 포인트 임계 ×1.25(≤ 3) · 여유(< 60 %) → ÷1.1(≥ 1)
        var spent = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / freq;
        if (AdaptiveLod)
        {
            if (exceeded) LodScale = MathF.Min(MaxLod, LodScale * 1.25f);
            else if (spent < budgetMs * 0.6) LodScale = MathF.Max(1f, LodScale / 1.1f);
        }
        Splatted = splatted; SubPixelSplats = subPx; EarlySkipped = early; BudgetSkipped = budget; SaturatedTiles = satTiles; SaturatedPixels = satPx; PixelOps = ops; BudgetExceeded = exceeded;
        LastRasterUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / freq;
    }

    private static byte B8(float v) => (byte)Math.Clamp((int)(MathF.Pow(Math.Clamp(v, 0f, 1f), 1f / 2.2f) * 255f + 0.5f), 0, 255);

    /// <summary>24 비트 하향 BMP(배경 합성 · 밀도 모드 = 스플랫 중첩 히트맵).</summary>
    public void WriteBmp(byte[] bmp)
    {
        var d = bmp.AsSpan();
        d.Slice(0, 54).Clear();
        d[0] = (byte)'B'; d[1] = (byte)'M';
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), bmp.Length);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), W);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -H);
        d[26] = 1; d[28] = 24;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), bmp.Length - 54);
        var bg = Background;
        for (var p = 0; p < W * H; p++)
        {
            float r, g, b;
            if (View == SplatView.Density)
            {
                var v = Math.Clamp(_density[p] / 24f, 0f, 1f);
                r = MathF.Min(1f, v * 2f); g = MathF.Max(0f, v * 1.6f - 0.4f); b = 0.15f + 0.6f * (1f - v) * MathF.Min(1f, _density[p]);
                if (_density[p] <= 0f) { r = bg.X; g = bg.Y; b = bg.Z; }
            }
            else
            {
                var t = _t[p];
                r = _cr[p] + t * bg.X; g = _cg[p] + t * bg.Y; b = _cb[p] + t * bg.Z;
            }
            var o = 54 + p * 3;
            bmp[o] = B8(b); bmp[o + 1] = B8(g); bmp[o + 2] = B8(r);
        }
    }
}
