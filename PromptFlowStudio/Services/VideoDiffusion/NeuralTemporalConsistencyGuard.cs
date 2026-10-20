using System.Numerics;

namespace PromptFlowStudio.Services.VideoDiffusion;

/// <summary>
/// [PromptFlow Studio v16.87.0] 🧷 시공간 옵티컬 플로 &amp; 프레임 일관성 가드(256×144 · 무할당).
///   ▸ 플로: ½ 해상도 휘도 · 8×8 블록 SAD 정수 탐색(±6 · 카메라 플로 힌트 중심 + 근접 페널티) → 포물선 서브픽셀 → 3×3 신뢰 가중 평활 → 쌍선형 조밀 플로(전 해상도).
///   ▸ 가림: 전/후방 플로 일관성 |f(p) + g(p − f(p))| &gt; 1.5 px → 가림 블록 · 프레임 밖 참조 → 제외.
///   ▸ 보정: 이전 출력 역워핑(쌍선형) → 전역 휘도 디플리커(가시 영역 평균비) → 픽셀별 α = 강도 × (1 − 가림) × exp(−(ΔY/τ)²) 시간 블렌드(SIMD).
///   ▸ 계측: 원본/출력 워핑 오차(평균 |Y_t − W(Y_{t−1})|) → 플리커 감소율 · 휘도 지터 · 평균 플로 · 가림률 · 라텐트 플로(엔진 노이즈 워핑 피드백).
/// </summary>
public sealed class NeuralTemporalConsistencyGuard
{
    public const int W = RealtimeLatentVideoDiffusionEngine.W, H = RealtimeLatentVideoDiffusionEngine.H, N = W * H;
    public const int HW = W / 2, HH = H / 2, Bs = 8, BX = HW / Bs, BY = HH / Bs, Blocks = BX * BY, Radius = 6;
    private const int LW = RealtimeLatentVideoDiffusionEngine.LW, LH = RealtimeLatentVideoDiffusionEngine.LH;

    private readonly object _gate = new();
    private readonly float[] _yCur = new float[N], _yPrevRaw = new float[N], _yPrevOut = new float[N], _yOut = new float[N];
    private readonly float[] _hCur = new float[HW * HH], _hPrev = new float[HW * HH];
    private readonly float[] _fx = new float[Blocks], _fy = new float[Blocks], _gx = new float[Blocks], _gy = new float[Blocks], _conf = new float[Blocks], _sx = new float[Blocks], _sy = new float[Blocks];
    private readonly float[] _occ = new float[Blocks];
    private readonly float[] _dfx = new float[N], _dfy = new float[N];
    private readonly float[] _prevOut = new float[N * 3], _warp = new float[N * 3], _adj = new float[N * 3], _alpha = new float[N * 3];
    public readonly float[] Output = new float[N * 3];
    public readonly byte[] OutputBytes = new byte[N * 3], RawBytes = new byte[N * 3];
    public readonly float[] LatentFlow = new float[2 * LW * LH];
    private bool _hasPrev;
    private double _rawSum, _outSum;
    private long _metricN;
    private float _prevMeanRaw, _prevMeanOut;

    public bool Enabled { get; set; } = true;
    public float Strength { get; set; } = 0.85f;
    public float Tau { get; set; } = 0.08f;
    public float Deflicker { get; set; } = 0.8f;
    public float FbThreshold { get; set; } = 1.5f;
    public float HintPenalty { get; set; } = 0.02f;

    public long Frames { get; private set; }
    public float RawWarpError { get; private set; }
    public float OutWarpError { get; private set; }
    public float FlickerReduction { get; private set; }
    public float MeanRawWarpError => _metricN > 0 ? (float)(_rawSum / _metricN) : 0f;
    public float MeanOutWarpError => _metricN > 0 ? (float)(_outSum / _metricN) : 0f;
    public float MeanFlickerReduction => MeanRawWarpError > 1e-6f ? 1f - MeanOutWarpError / MeanRawWarpError : 0f;
    public float RawLumaJitter { get; private set; }
    public float OutLumaJitter { get; private set; }
    public float OcclusionRatio { get; private set; }
    public float MeanBlend { get; private set; }
    public float DeflickerGain { get; private set; } = 1f;
    public Vector2 MeanFlow { get; private set; }
    public double LastFlowMs { get; private set; }
    public double LastProcessMs { get; private set; }
    public Vector2 BlockFlow(int bx, int by) => new(_fx[by * BX + bx] * 2f, _fy[by * BX + bx] * 2f);
    public bool BlockOccluded(int bx, int by) => _occ[by * BX + bx] > 0.5f;
    public Vector2 FlowAt(int x, int y) => new(_dfx[y * W + x], _dfy[y * W + x]);

    public void Reset() { lock (_gate) { _hasPrev = false; _rawSum = _outSum = 0; _metricN = 0; Frames = 0; Array.Clear(LatentFlow); } }

    private static float Luma(float r, float g, float b) => 0.299f * r + 0.587f * g + 0.114f * b;

    private static void Downsample(ReadOnlySpan<float> y, Span<float> h)
    {
        for (var j = 0; j < HH; j++)
            for (var i = 0; i < HW; i++)
            {
                var k = 2 * j * W + 2 * i;
                h[j * HW + i] = 0.25f * (y[k] + y[k + 1] + y[k + W] + y[k + W + 1]);
            }
    }

    /// <summary>블록 SAD(대상 블록 b @ (bx0, by0) · 참조 r @ (bx0 − dx, by0 − dy)).</summary>
    private static float BlockSad(ReadOnlySpan<float> tgt, ReadOnlySpan<float> refImg, int x0, int y0, int dx, int dy)
    {
        float s = 0f;
        if (x0 - dx >= 0 && x0 - dx + Bs <= HW && y0 - dy >= 0 && y0 - dy + Bs <= HH)
        {
            for (var r = 0; r < Bs; r++)
                s += DiffusionSimd.Sad(tgt.Slice((y0 + r) * HW + x0, Bs), refImg.Slice((y0 + r - dy) * HW + x0 - dx, Bs));
            return s;
        }
        // 경계: 참조 좌표 클램프(가장자리 복제) → 프레임 진입 콘텐츠도 부분 정합
        for (var r = 0; r < Bs; r++)
        {
            var ry = Math.Clamp(y0 + r - dy, 0, HH - 1);
            for (var c = 0; c < Bs; c++) s += MathF.Abs(tgt[(y0 + r) * HW + x0 + c] - refImg[ry * HW + Math.Clamp(x0 + c - dx, 0, HW - 1)]);
        }
        return s;
    }

    /// <summary>블록 정합: tgt(p) ≈ ref(p − f) 인 f(½ 해상도 px).</summary>
    private void Match(ReadOnlySpan<float> tgt, ReadOnlySpan<float> refImg, Span<float> fx, Span<float> fy, Span<float> conf, Vector2 hint)
    {
        Span<float> sad = stackalloc float[(2 * Radius + 1) * (2 * Radius + 1)];
        int hx = (int)MathF.Round(hint.X * 0.5f), hy = (int)MathF.Round(hint.Y * 0.5f);
        for (var by = 0; by < BY; by++)
            for (var bx = 0; bx < BX; bx++)
            {
                int x0 = bx * Bs, y0 = by * Bs;
                float best = float.MaxValue, second = float.MaxValue; int bdx = 0, bdy = 0;
                for (var dy = -Radius; dy <= Radius; dy++)
                    for (var dx = -Radius; dx <= Radius; dx++)
                    {
                        int cx = dx + hx, cy = dy + hy;
                        var si = (dy + Radius) * (2 * Radius + 1) + dx + Radius;
                        if (x0 - cx + Bs <= 0 || x0 - cx >= HW || y0 - cy + Bs <= 0 || y0 - cy >= HH) { sad[si] = float.MaxValue; continue; }
                        var v = BlockSad(tgt, refImg, x0, y0, cx, cy) + HintPenalty * Bs * Bs * (MathF.Abs(dx) + MathF.Abs(dy)) * 0.05f;
                        sad[si] = v;
                        if (v < best) { second = best; best = v; bdx = dx; bdy = dy; } else if (v < second) second = v;
                    }
                // 포물선 서브픽셀
                float sxp = 0f, syp = 0f;
                if (bdx > -Radius && bdx < Radius)
                {
                    float l = sad[(bdy + Radius) * (2 * Radius + 1) + bdx + Radius - 1], c = sad[(bdy + Radius) * (2 * Radius + 1) + bdx + Radius], r = sad[(bdy + Radius) * (2 * Radius + 1) + bdx + Radius + 1];
                    var den = l - 2f * c + r;
                    if (l < float.MaxValue && r < float.MaxValue && den > 1e-6f) sxp = Math.Clamp(0.5f * (l - r) / den, -0.5f, 0.5f);
                }
                if (bdy > -Radius && bdy < Radius)
                {
                    float u = sad[(bdy + Radius - 1) * (2 * Radius + 1) + bdx + Radius], c = sad[(bdy + Radius) * (2 * Radius + 1) + bdx + Radius], d = sad[(bdy + Radius + 1) * (2 * Radius + 1) + bdx + Radius];
                    var den = u - 2f * c + d;
                    if (u < float.MaxValue && d < float.MaxValue && den > 1e-6f) syp = Math.Clamp(0.5f * (u - d) / den, -0.5f, 0.5f);
                }
                var bi = by * BX + bx;
                fx[bi] = bdx + hx + sxp; fy[bi] = bdy + hy + syp;
                conf[bi] = second < float.MaxValue ? MathF.Min(1f, (second - best) / (best + 1e-3f) + 0.05f) : 0.05f;
            }
        // 3×3 신뢰 가중 평활(이상치 완화 · 자기 가중 2배)
        for (var by = 0; by < BY; by++)
            for (var bx = 0; bx < BX; bx++)
            {
                float sw = 0f, ax = 0f, ay = 0f;
                for (var j = -1; j <= 1; j++)
                    for (var i = -1; i <= 1; i++)
                    {
                        int x = bx + i, y = by + j;
                        if (x < 0 || y < 0 || x >= BX || y >= BY) continue;
                        var k = y * BX + x; var w = conf[k] * (i == 0 && j == 0 ? 2f : 1f);
                        ax += fx[k] * w; ay += fy[k] * w; sw += w;
                    }
                _sx[by * BX + bx] = ax / sw; _sy[by * BX + bx] = ay / sw;
            }
        for (var k = 0; k < Blocks; k++) { fx[k] = _sx[k]; fy[k] = _sy[k]; }
    }

    private static float Sample(ReadOnlySpan<float> img, int stride, int h, float x, float y)
    {
        x = Math.Clamp(x, 0f, stride - 1.001f); y = Math.Clamp(y, 0f, h - 1.001f);
        int x0 = (int)x, y0 = (int)y; float fx = x - x0, fy = y - y0; var k = y0 * stride + x0;
        var a = img[k] + (img[k + 1] - img[k]) * fx; var b = img[k + stride] + (img[k + stride + 1] - img[k + stride]) * fx;
        return a + (b - a) * fy;
    }

    /// <summary>블록 플로(½ 해상도) → 전 해상도 조밀 플로(블록 중심 쌍선형).</summary>
    private void Densify()
    {
        for (var y = 0; y < H; y++)
        {
            var by = Math.Clamp((y * 0.5f - Bs * 0.5f + 0.25f) / Bs, 0f, BY - 1.001f); int b0 = (int)by; var fy = by - b0;
            for (var x = 0; x < W; x++)
            {
                var bx = Math.Clamp((x * 0.5f - Bs * 0.5f + 0.25f) / Bs, 0f, BX - 1.001f); int a0 = (int)bx; var fx = bx - a0;
                var k = b0 * BX + a0;
                float Lerp2(float[] f) { var a = f[k] + (f[k + 1] - f[k]) * fx; var b = f[k + BX] + (f[k + BX + 1] - f[k + BX]) * fx; return 2f * (a + (b - a) * fy); }
                _dfx[y * W + x] = Lerp2(_fx); _dfy[y * W + x] = Lerp2(_fy);
            }
        }
    }

    /// <summary>1프레임 보정(무할당): 플로/가림 → 워핑 → 디플리커 → 시간 블렌드 → 계측.</summary>
    public void Process(ReadOnlySpan<float> frame, Vector2 cameraHintPx)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double meanRaw = 0;
            for (var i = 0; i < N; i++) { var y = Luma(frame[i * 3], frame[i * 3 + 1], frame[i * 3 + 2]); _yCur[i] = y; meanRaw += y; }
            meanRaw /= N;
            for (var i = 0; i < N * 3; i++) RawBytes[i] = (byte)(Math.Clamp(frame[i], 0f, 1f) * 255f + 0.5f);
            if (!_hasPrev)
            {
                frame.CopyTo(Output); frame.CopyTo(_prevOut);
                _yCur.AsSpan().CopyTo(_yPrevRaw); _yCur.AsSpan().CopyTo(_yPrevOut);
                Downsample(_yCur, _hPrev);
                _prevMeanRaw = _prevMeanOut = (float)meanRaw;
                _hasPrev = true; Frames++;
                Finish(t0);
                return;
            }
            // ── 플로(전/후방) · 가림 ──
            var tf = System.Diagnostics.Stopwatch.GetTimestamp();
            Downsample(_yCur, _hCur);
            Match(_hCur, _hPrev, _fx, _fy, _conf, cameraHintPx);
            Match(_hPrev, _hCur, _gx, _gy, _conf, -cameraHintPx);
            float occN = 0f, mfx = 0f, mfy = 0f;
            for (var by = 0; by < BY; by++)
                for (var bx = 0; bx < BX; bx++)
                {
                    var k = by * BX + bx;
                    // 후방 플로를 p − f 위치 블록에서 조회
                    var qx = Math.Clamp((int)MathF.Round(bx - _fx[k] / Bs), 0, BX - 1); var qy = Math.Clamp((int)MathF.Round(by - _fy[k] / Bs), 0, BY - 1);
                    var q = qy * BX + qx;
                    var e = MathF.Sqrt((_fx[k] + _gx[q]) * (_fx[k] + _gx[q]) + (_fy[k] + _gy[q]) * (_fy[k] + _gy[q]));
                    _occ[k] = e * 2f > FbThreshold ? 1f : 0f;
                    occN += _occ[k]; mfx += _fx[k]; mfy += _fy[k];
                }
            OcclusionRatio = occN / Blocks; MeanFlow = new Vector2(mfx * 2f / Blocks, mfy * 2f / Blocks);
            Densify();
            LastFlowMs = System.Diagnostics.Stopwatch.GetElapsedTime(tf).TotalMilliseconds;

            // ── 워핑(이전 출력 · 이전 원본 휘도) · 가시 마스크 ──
            double rawErr = 0, wsum = 0, meanW = 0, meanC = 0;
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var i = y * W + x;
                    float sx = x - _dfx[i], sy = y - _dfy[i];
                    var vis = sx >= 0f && sy >= 0f && sx <= W - 1 && sy <= H - 1 && _occ[Math.Min(y / (2 * Bs), BY - 1) * BX + Math.Min(x / (2 * Bs), BX - 1)] < 0.5f;
                    for (var c = 0; c < 3; c++) _warp[i * 3 + c] = SampleRgb(sx, sy, c);
                    _alpha[i * 3] = vis ? 1f : 0f;
                    if (!vis) continue;
                    var yr = Sample(_yPrevRaw, W, H, sx, sy);
                    rawErr += MathF.Abs(_yCur[i] - yr); wsum++;
                    meanW += Luma(_warp[i * 3], _warp[i * 3 + 1], _warp[i * 3 + 2]); meanC += _yCur[i];
                }
            // ── 디플리커 + α ──
            var gain = wsum > 0 && meanC > 1e-4 ? (float)(meanW / meanC) : 1f;
            gain = 1f + (Math.Clamp(gain, 0.5f, 2f) - 1f) * (Enabled ? Deflicker : 0f);
            DeflickerGain = gain;
            float asum = 0f;
            for (var i = 0; i < N; i++)
            {
                var vis = _alpha[i * 3];
                float r = frame[i * 3] * gain, g = frame[i * 3 + 1] * gain, b = frame[i * 3 + 2] * gain;
                _adj[i * 3] = r; _adj[i * 3 + 1] = g; _adj[i * 3 + 2] = b;
                var dY = MathF.Abs(Luma(r, g, b) - Luma(_warp[i * 3], _warp[i * 3 + 1], _warp[i * 3 + 2])) / Tau;
                var a = Enabled ? Strength * vis * MathF.Exp(-dY * dY) : 0f;
                _alpha[i * 3] = a; _alpha[i * 3 + 1] = a; _alpha[i * 3 + 2] = a; asum += a;
            }
            MeanBlend = asum / N;
            DiffusionSimd.Blend(Output, _adj, _warp, _alpha);

            // ── 계측(출력 워핑 오차 · 지터) ──
            double outErr = 0, meanOut = 0;
            for (var i = 0; i < N; i++) { var y = Luma(Output[i * 3], Output[i * 3 + 1], Output[i * 3 + 2]); _yOut[i] = y; meanOut += y; }
            meanOut /= N;
            for (var y = 0; y < H; y++)
                for (var x = 0; x < W; x++)
                {
                    var i = y * W + x;
                    float sx = x - _dfx[i], sy = y - _dfy[i];
                    if (!(sx >= 0f && sy >= 0f && sx <= W - 1 && sy <= H - 1) || _occ[Math.Min(y / (2 * Bs), BY - 1) * BX + Math.Min(x / (2 * Bs), BX - 1)] >= 0.5f) continue;
                    outErr += MathF.Abs(_yOut[i] - Sample(_yPrevOut, W, H, sx, sy));
                }
            if (wsum > 0)
            {
                RawWarpError = (float)(rawErr / wsum); OutWarpError = (float)(outErr / wsum);
                FlickerReduction = RawWarpError > 1e-6f ? 1f - OutWarpError / RawWarpError : 0f;
                _rawSum += RawWarpError; _outSum += OutWarpError; _metricN++;
            }
            RawLumaJitter = MathF.Abs((float)meanRaw - _prevMeanRaw); OutLumaJitter = MathF.Abs((float)meanOut - _prevMeanOut);
            _prevMeanRaw = (float)meanRaw; _prevMeanOut = (float)meanOut;
            // ── 상태 이월 ──
            Output.AsSpan().CopyTo(_prevOut);
            _yCur.AsSpan().CopyTo(_yPrevRaw); _yOut.AsSpan().CopyTo(_yPrevOut);
            _hCur.AsSpan().CopyTo(_hPrev);
            Frames++;
            Finish(t0);
        }
    }

    private float SampleRgb(float x, float y, int c)
    {
        x = Math.Clamp(x, 0f, W - 1.001f); y = Math.Clamp(y, 0f, H - 1.001f);
        int x0 = (int)x, y0 = (int)y; float fx = x - x0, fy = y - y0; var k = (y0 * W + x0) * 3 + c;
        var a = _prevOut[k] + (_prevOut[k + 3] - _prevOut[k]) * fx; var b = _prevOut[k + W * 3] + (_prevOut[k + W * 3 + 3] - _prevOut[k + W * 3]) * fx;
        return a + (b - a) * fy;
    }

    private void Finish(long t0)
    {
        for (var i = 0; i < N * 3; i++) OutputBytes[i] = (byte)(Math.Clamp(Output[i], 0f, 1f) * 255f + 0.5f);
        // 라텐트 플로(라텐트 px) = 조밀 플로 / 4 @ 라텐트 셀 중심
        for (var ly = 0; ly < LH; ly++)
            for (var lx = 0; lx < LW; lx++)
            {
                var p = (ly * 4 + 2) * W + lx * 4 + 2;
                LatentFlow[ly * LW + lx] = _hasPrev && Frames > 1 ? _dfx[p] / 4f : 0f;
                LatentFlow[LW * LH + ly * LW + lx] = _hasPrev && Frames > 1 ? _dfy[p] / 4f : 0f;
            }
        LastProcessMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }
}
