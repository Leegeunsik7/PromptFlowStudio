using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.NeuralCodec;

public enum NvcFrameType : byte { Intra, Inter }
public enum NvcMbMode : byte { Skip, Inter, Intra }

/// <summary>
/// [v17.02.0] 코덱 SIMD 커널 — Vector&lt;float&gt; 경로와 스칼라 경로가 원소별 비트 동일(축약 FMA 없음 · 비교/선택 클램프).
/// 리덕션(Sad)만 합산 순서 차이로 허용 오차 비교.
/// </summary>
public static class NvcSimd
{
    public static void Sub(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> d)
    {
        var n = d.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
            for (var w = Vector<float>.Count; i <= n - w; i += w) (new Vector<float>(a.Slice(i)) - new Vector<float>(b.Slice(i))).CopyTo(d.Slice(i));
        for (; i < n; i++) d[i] = a[i] - b[i];
    }

    public static void SubScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> d)
    {
        for (var i = 0; i < d.Length; i++) d[i] = a[i] - b[i];
    }

    /// <summary>d = clamp(p + r, lo, hi) — Min/Max 대신 비교·선택(±0 비트 동일).</summary>
    public static void AddClamp(ReadOnlySpan<float> p, ReadOnlySpan<float> r, Span<float> d, float lo, float hi)
    {
        var n = d.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vl = new Vector<float>(lo); var vh = new Vector<float>(hi);
            for (var w = Vector<float>.Count; i <= n - w; i += w)
            {
                var v = new Vector<float>(p.Slice(i)) + new Vector<float>(r.Slice(i));
                v = Vector.ConditionalSelect(Vector.LessThan(v, vl), vl, v);
                v = Vector.ConditionalSelect(Vector.GreaterThan(v, vh), vh, v);
                v.CopyTo(d.Slice(i));
            }
        }
        for (; i < n; i++) { var v = p[i] + r[i]; v = v < lo ? lo : v; d[i] = v > hi ? hi : v; }
    }

    public static void AddClampScalar(ReadOnlySpan<float> p, ReadOnlySpan<float> r, Span<float> d, float lo, float hi)
    {
        for (var i = 0; i < d.Length; i++) { var v = p[i] + r[i]; v = v < lo ? lo : v; d[i] = v > hi ? hi : v; }
    }

    /// <summary>데드존 양자화 q = sign(x)·⌊|x|·inv + off⌋ (균일 스텝) — 0 아닌 계수 개수 반환.</summary>
    public static int QuantizeUniform(ReadOnlySpan<float> x, Span<int> q, float inv, float off)
    {
        var n = q.Length; var i = 0; var zeros = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vi = new Vector<float>(inv); var vo = new Vector<float>(off); var acc = Vector<int>.Zero;
            for (var w = Vector<float>.Count; i <= n - w; i += w)
            {
                var vx = new Vector<float>(x.Slice(i));
                var m = Vector.ConvertToInt32(Vector.Abs(vx) * vi + vo);
                m = Vector.ConditionalSelect(Vector.LessThan(vx, Vector<float>.Zero), -m, m);
                m.CopyTo(q.Slice(i));
                acc += Vector.Equals(m, Vector<int>.Zero);
            }
            zeros = -Vector.Sum(acc);
        }
        for (; i < n; i++) { var m = (int)(MathF.Abs(x[i]) * inv + off); q[i] = x[i] < 0f ? -m : m; if (m == 0) zeros++; }
        return n - zeros;
    }

    public static int QuantizeUniformScalar(ReadOnlySpan<float> x, Span<int> q, float inv, float off)
    {
        var nz = 0;
        for (var i = 0; i < q.Length; i++) { var m = (int)(MathF.Abs(x[i]) * inv + off); q[i] = x[i] < 0f ? -m : m; if (m != 0) nz++; }
        return nz;
    }

    /// <summary>주파수 가중 데드존 양자화(계수별 inv).</summary>
    public static int QuantizeWeighted(ReadOnlySpan<float> x, ReadOnlySpan<float> inv, Span<int> q, float off)
    {
        var n = q.Length; var i = 0; var zeros = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vo = new Vector<float>(off); var acc = Vector<int>.Zero;
            for (var w = Vector<float>.Count; i <= n - w; i += w)
            {
                var vx = new Vector<float>(x.Slice(i));
                var m = Vector.ConvertToInt32(Vector.Abs(vx) * new Vector<float>(inv.Slice(i)) + vo);
                m = Vector.ConditionalSelect(Vector.LessThan(vx, Vector<float>.Zero), -m, m);
                m.CopyTo(q.Slice(i));
                acc += Vector.Equals(m, Vector<int>.Zero);
            }
            zeros = -Vector.Sum(acc);
        }
        for (; i < n; i++) { var m = (int)(MathF.Abs(x[i]) * inv[i] + off); q[i] = x[i] < 0f ? -m : m; if (m == 0) zeros++; }
        return n - zeros;
    }

    public static int QuantizeWeightedScalar(ReadOnlySpan<float> x, ReadOnlySpan<float> inv, Span<int> q, float off)
    {
        var nz = 0;
        for (var i = 0; i < q.Length; i++) { var m = (int)(MathF.Abs(x[i]) * inv[i] + off); q[i] = x[i] < 0f ? -m : m; if (m != 0) nz++; }
        return nz;
    }

    public static void DequantWeighted(ReadOnlySpan<int> q, ReadOnlySpan<float> step, Span<float> d)
    {
        var n = d.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
            for (var w = Vector<float>.Count; i <= n - w; i += w) (Vector.ConvertToSingle(new Vector<int>(q.Slice(i))) * new Vector<float>(step.Slice(i))).CopyTo(d.Slice(i));
        for (; i < n; i++) d[i] = q[i] * step[i];
    }

    public static void DequantWeightedScalar(ReadOnlySpan<int> q, ReadOnlySpan<float> step, Span<float> d)
    {
        for (var i = 0; i < d.Length; i++) d[i] = q[i] * step[i];
    }

    /// <summary>d = p + q·step (제자리 p == d 허용).</summary>
    public static void DequantAddUniform(ReadOnlySpan<int> q, ReadOnlySpan<float> p, Span<float> d, float step)
    {
        var n = d.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vs = new Vector<float>(step);
            for (var w = Vector<float>.Count; i <= n - w; i += w) (new Vector<float>(p.Slice(i)) + Vector.ConvertToSingle(new Vector<int>(q.Slice(i))) * vs).CopyTo(d.Slice(i));
        }
        for (; i < n; i++) d[i] = p[i] + q[i] * step;
    }

    public static void DequantAddUniformScalar(ReadOnlySpan<int> q, ReadOnlySpan<float> p, Span<float> d, float step)
    {
        for (var i = 0; i < d.Length; i++) d[i] = p[i] + q[i] * step;
    }

    public static float Sad(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length; var i = 0; var s = 0f;
        if (Vector.IsHardwareAccelerated)
        {
            var acc = Vector<float>.Zero;
            for (var w = Vector<float>.Count; i <= n - w; i += w) acc += Vector.Abs(new Vector<float>(a.Slice(i)) - new Vector<float>(b.Slice(i)));
            s = Vector.Sum(acc);
        }
        for (; i < n; i++) s += MathF.Abs(a[i] - b[i]);
        return s;
    }

    public static float SadScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length; i++) s += MathF.Abs(a[i] - b[i]);
        return s;
    }
}

/// <summary>MSB 우선 비트 기록기(Exp-Golomb ue/se) — 사전 할당 버퍼 · 초과 시 Overflow 플래그.</summary>
public sealed class NvcBitWriter
{
    private byte[] _buf = Array.Empty<byte>();
    private int _start, _pos, _n;
    private ulong _acc;

    public bool Overflow { get; private set; }
    public int BitCount => (_pos - _start) * 8 + _n;

    public void Reset(byte[] buf, int offset) { _buf = buf; _start = _pos = offset; _n = 0; _acc = 0; Overflow = false; }

    public void Put(uint v, int bits)
    {
        if (bits <= 0) return;
        _acc = (_acc << bits) | (v & (uint)((1UL << bits) - 1));
        _n += bits;
        while (_n >= 8) { _n -= 8; Emit((byte)(_acc >> _n)); }
        _acc &= (1UL << _n) - 1;
    }

    public void PutUe(uint v)
    {
        var x = (ulong)v + 1;
        var len = 64 - BitOperations.LeadingZeroCount(x);
        Put(0, len - 1);
        Put((uint)x, len);
    }

    public void PutSe(int v) => PutUe(v > 0 ? (uint)(2 * v - 1) : (uint)(-2 * v));

    /// <summary>바이트 정렬 후 기록 바이트 수.</summary>
    public int Finish()
    {
        if (_n > 0) { Emit((byte)(_acc << (8 - _n))); _n = 0; _acc = 0; }
        return _pos - _start;
    }

    private void Emit(byte b)
    {
        if (_pos >= _buf.Length) { Overflow = true; return; }
        _buf[_pos++] = b;
    }
}

/// <summary>비트 판독기 — 끝 초과 · 비정상 접두(32+) 시 Error(예외 없음).</summary>
public sealed class NvcBitReader
{
    private byte[] _buf = Array.Empty<byte>();
    private int _pos, _end, _n;
    private ulong _acc;

    public bool Error { get; private set; }

    public void Reset(byte[] buf, int offset, int length) { _buf = buf; _pos = offset; _end = offset + length; _n = 0; _acc = 0; Error = false; }

    public uint Get(int bits)
    {
        if (bits <= 0) return 0;
        while (_n < bits)
        {
            if (_pos >= _end) { Error = true; return 0; }
            _acc = (_acc << 8) | _buf[_pos++];
            _n += 8;
        }
        _n -= bits;
        var v = (uint)(_acc >> _n) & (uint)((1UL << bits) - 1);
        _acc &= (1UL << _n) - 1;
        return v;
    }

    public uint GetUe()
    {
        var z = 0;
        while (Get(1) == 0)
        {
            if (Error || ++z > 31) { Error = true; return 0; }
        }
        return z == 0 ? 0u : (uint)(((1UL << z) | Get(z)) - 1);
    }

    public int GetSe()
    {
        var k = GetUe();
        return (k & 1) != 0 ? (int)((k + 1) >> 1) : -(int)(k >> 1);
    }
}

/// <summary>3DGS 스플랫 프레임(SoA 10 속성 평면: 위치 3 · 스케일 3 · 불투명도 · RGB).</summary>
public sealed class VolumetricSplatFrame
{
    public const int Attributes = 10;
    public static readonly string[] AttributeNames = { "Px", "Py", "Pz", "Sx", "Sy", "Sz", "α", "R", "G", "B" };
    public readonly float[][] Plane;
    public int Count;

    public VolumetricSplatFrame(int capacity)
    {
        Plane = new float[Attributes][];
        for (var a = 0; a < Attributes; a++) Plane[a] = new float[capacity];
    }

    public int Capacity => Plane[0].Length;

    public void CopyFrom(VolumetricSplatFrame s)
    {
        var n = Math.Min(s.Count, Capacity);
        for (var a = 0; a < Attributes; a++) s.Plane[a].AsSpan(0, n).CopyTo(Plane[a]);
        Count = n;
    }

    public void Clear(int n)
    {
        for (var a = 0; a < Attributes; a++) Array.Clear(Plane[a], 0, Math.Min(n, Capacity));
    }
}

/// <summary>
/// [PromptFlow Studio v17.02.0] 📡 실시간 볼류메트릭 비디오 코덱(평면 Y + 3DGS 스플랫 클라우드 · 폐루프 인코더 + 독립 디코더).
/// 비디오: 16×16 MB 다이아몬드 움직임 탐색(SIMD SAD) → Skip/Inter/Intra(DC) 모드 결정(λ·SAD) → 8×8 직교 분석 변환 → 지각 가중 데드존 양자화(SIMD) →
/// 지그재그 run/level Exp-Golomb 비트스트림 · 합성 변환 재구성이 인코더/디코더 비트 동일. 스플랫: 직전 재구성 대비 시공간 잔차 양자화(SIMD) · 무변화 스플랫 런 스킵.
/// 레이트 제어: 온라인 학습 R-Q 모델(log2 비트 = a − b·QP · LMS 편향 갱신)로 ABR 목표 비트에 수렴. 해상도는 256×144 프록시 · 비트레이트는 16K(15360×8640) 픽셀 환산 표기.
/// 학습된 신경망 가중치는 없음 — 학습 요소는 온라인 R-Q 모델뿐(신경 변환 결선점: 분석/합성 변환 교체).
/// </summary>
public sealed class NeuralVolumetricVideoCodecEngine
{
    public const int Width = 256, Height = 144, Pixels = Width * Height, Mb = 16, MbCols = Width / Mb, MbRows = Height / Mb, MbCount = MbCols * MbRows;
    public const int MaxMv = 12, MinQp = 8, MaxQp = 51, GopLength = 90, MaxSplats = 8192, AuCapacity = 1 << 19;
    public const byte Magic = 0xC7;
    public const double Upscale16K = 15360.0 * 8640.0 / Pixels;
    public static readonly float[] SplatBaseStep = { 0.001f, 0.001f, 0.001f, 0.0005f, 0.0005f, 0.0005f, 1f / 255f, 1f / 255f, 1f / 255f, 1f / 255f };
    public static readonly string[] ModeNames = { "Skip", "Inter", "Intra" };

    private static readonly float[] Basis = BuildBasis();
    private static readonly byte[] ZigZag =
    {
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    };
    private static readonly float[] FreqWeight = BuildWeight();

    // ── 인코더 ──
    private float[] _rec = new float[Pixels], _recWork = new float[Pixels];
    private readonly int[] _encMvX = new int[MbCount], _encMvY = new int[MbCount];
    private readonly NvcMbMode[] _encMode = new NvcMbMode[MbCount];
    private readonly VolumetricSplatFrame _encSplat = new(MaxSplats);
    private readonly int[][] _sq = new int[VolumetricSplatFrame.Attributes][];
    private readonly float[] _sres = new float[MaxSplats];
    private readonly float[] _step = new float[64], _inv = new float[64];
    private readonly byte[] _au = new byte[AuCapacity];
    private readonly NvcBitWriter _bw = new();
    private int _auLen, _encSplatCount, _lastInterQp = 30;
    private bool _forceKey, _encHasRef;
    private float _qstep, _lambda;
    private float _rcAKey = 20f, _rcAInter = 16.5f;

    // ── 디코더 ──
    private float[] _dec = new float[Pixels], _decWork = new float[Pixels];
    private readonly int[] _decMvX = new int[MbCount], _decMvY = new int[MbCount];
    private readonly NvcMbMode[] _decMode = new NvcMbMode[MbCount];
    private VolumetricSplatFrame _decSplat = new(MaxSplats), _decSplatPrev = new(MaxSplats), _decSplatWork = new(MaxSplats);
    private readonly float[] _dStep = new float[64];
    private readonly byte[] _rx = new byte[AuCapacity];
    private readonly NvcBitReader _br = new();
    private bool _decHasRef;

    // ── 블록 스크래치 ──
    private readonly float[] _cur = new float[Mb * Mb], _pred = new float[Mb * Mb], _res = new float[Mb * Mb], _resRec = new float[Mb * Mb], _out = new float[Mb * Mb];
    private readonly float[] _blk = new float[64], _coef = new float[64], _tmp = new float[64];
    private readonly int[] _q = new int[4 * 64], _nz = new int[4];

    public NeuralVolumetricVideoCodecEngine()
    {
        for (var a = 0; a < VolumetricSplatFrame.Attributes; a++) _sq[a] = new int[MaxSplats];
        Array.Fill(_dec, 128f); Array.Fill(_rec, 128f);
    }

    /// <summary>프레임당 비디오 목표 비트(프록시 해상도 · 0 = 고정 QP). 스플랫 비트는 별도(원 해상도 그대로).</summary>
    public float TargetBitsPerFrame { get; set; }
    public int FixedQp { get; set; } = 28;
    public float KeyBoost { get; set; } = 4f;
    public double FrameRate { get; set; } = 30;

    public long Frames { get; private set; }
    public long KeyFrames { get; private set; }
    public NvcFrameType LastType { get; private set; }
    public int LastQp { get; private set; }
    public int LastBits => _auLen * 8;
    public int LastVideoBits { get; private set; }
    public int LastSplatBits { get; private set; }
    public float SkipRatio { get; private set; }
    public float IntraRatio { get; private set; }
    public float SplatChangedRatio { get; private set; }
    public float EncoderPsnr { get; private set; }
    public double EncodeUs { get; private set; }
    public double DecodeUs { get; private set; }
    public float AvgInterBits { get; private set; }
    public int LastKeyBits { get; private set; }
    public long TotalBits { get; private set; }
    public long DecodedFrames { get; private set; }
    public long DecodeErrors { get; private set; }
    public long ConcealedFrames { get; private set; }
    public bool LastDecodedKey { get; private set; }
    public int DecodedSplatCount => _decSplat.Count;

    public ReadOnlySpan<byte> AccessUnit => _au.AsSpan(0, _auLen);
    public ReadOnlySpan<float> Reconstructed => _rec;
    public ReadOnlySpan<float> Decoded => _dec;
    public VolumetricSplatFrame DecodedSplats => _decSplat;
    public VolumetricSplatFrame EncoderSplats => _encSplat;
    public NvcMbMode EncoderMode(int mb) => _encMode[mb];
    public int EncoderMvX(int mb) => _encMvX[mb];
    public int EncoderMvY(int mb) => _encMvY[mb];

    /// <summary>원본 비트 / AU 비트(Y 8 bpp + 스플랫 10×fp32).</summary>
    public float CompressionRatio => _auLen == 0 ? 0f : (float)((Pixels * 8.0 + _encSplatCount * VolumetricSplatFrame.Attributes * 32.0) / (_auLen * 8.0));
    /// <summary>직전 키프레임 대비 인터 프레임 비디오 비트 절감률(시간 중복 제거 효과).</summary>
    public float InterSavings => LastKeyBits <= 0 || AvgInterBits <= 0 ? 0f : 1f - AvgInterBits / LastKeyBits;
    /// <summary>16K 환산 비트레이트(Mbps) = 비디오 비트 × 픽셀 배율 + 스플랫 비트(배율 없음).</summary>
    public double Mbps16K => (LastVideoBits * Upscale16K + LastSplatBits) * FrameRate / 1e6;

    public void ForceKeyframe() => _forceKey = true;

    private static float[] BuildBasis()
    {
        var c = new float[64];
        for (var u = 0; u < 8; u++)
            for (var x = 0; x < 8; x++)
                c[u * 8 + x] = (float)((u == 0 ? Math.Sqrt(0.125) : 0.5) * Math.Cos((2 * x + 1) * u * Math.PI / 16));
        return c;
    }

    private static float[] BuildWeight()
    {
        var w = new float[64];
        for (var u = 0; u < 8; u++) for (var v = 0; v < 8; v++) w[u * 8 + v] = 1f + 0.12f * (u + v);
        return w;
    }

    public static float QStep(int qp) => 0.625f * MathF.Pow(2f, qp / 6f);
    public static float SplatScale(int qp) => Math.Clamp(MathF.Pow(2f, (qp - 26) / 12f), 0.25f, 8f);

    /// <summary>분석 변환 Y = C·X·Cᵀ (직교 · 스칼라 결정적).</summary>
    private void Forward(float[] x, float[] y)
    {
        var c = Basis; var t = _tmp;
        for (var u = 0; u < 8; u++)
            for (var j = 0; j < 8; j++)
            {
                var s = 0f;
                for (var k = 0; k < 8; k++) s += c[u * 8 + k] * x[k * 8 + j];
                t[u * 8 + j] = s;
            }
        for (var u = 0; u < 8; u++)
            for (var v = 0; v < 8; v++)
            {
                var s = 0f;
                for (var k = 0; k < 8; k++) s += t[u * 8 + k] * c[v * 8 + k];
                y[u * 8 + v] = s;
            }
    }

    /// <summary>합성 변환 X = Cᵀ·Y·C.</summary>
    private void Inverse(float[] y, float[] x)
    {
        var c = Basis; var t = _tmp;
        for (var i = 0; i < 8; i++)
            for (var v = 0; v < 8; v++)
            {
                var s = 0f;
                for (var u = 0; u < 8; u++) s += c[u * 8 + i] * y[u * 8 + v];
                t[i * 8 + v] = s;
            }
        for (var i = 0; i < 8; i++)
            for (var j = 0; j < 8; j++)
            {
                var s = 0f;
                for (var v = 0; v < 8; v++) s += t[i * 8 + v] * c[v * 8 + j];
                x[i * 8 + j] = s;
            }
    }

    private static void SetQuant(int qp, float[] step, float[]? inv)
    {
        var qs = QStep(qp);
        for (var k = 0; k < 64; k++)
        {
            step[k] = qs * FreqWeight[k];
            if (inv is not null) inv[k] = 1f / step[k];
        }
    }

    private static float DcPred(float[] f, int x0, int y0)
    {
        var s = 0f; var n = 0;
        if (y0 > 0) { var o = (y0 - 1) * Width + x0; for (var i = 0; i < Mb; i++) s += f[o + i]; n += Mb; }
        if (x0 > 0) { for (var i = 0; i < Mb; i++) s += f[(y0 + i) * Width + x0 - 1]; n += Mb; }
        return n == 0 ? 128f : s / n;
    }

    private static void MvPredictor(NvcMbMode[] mode, int[] mx, int[] my, int mbx, int mby, out int px, out int py)
    {
        px = py = 0;
        var mb = mby * MbCols + mbx;
        if (mbx > 0 && mode[mb - 1] != NvcMbMode.Intra) { px = mx[mb - 1]; py = my[mb - 1]; }
        var x0 = mbx * Mb; var y0 = mby * Mb;
        px = Math.Clamp(px, -x0, Width - Mb - x0);
        py = Math.Clamp(py, -y0, Height - Mb - y0);
    }

    private static bool MvValid(int x0, int y0, int mx, int my) =>
        mx >= -MaxMv && mx <= MaxMv && my >= -MaxMv && my <= MaxMv && x0 + mx >= 0 && x0 + mx <= Width - Mb && y0 + my >= 0 && y0 + my <= Height - Mb;

    private static void LoadBlock(float[] f, int x, int y, float[] dst)
    {
        for (var r = 0; r < Mb; r++) f.AsSpan((y + r) * Width + x, Mb).CopyTo(dst.AsSpan(r * Mb, Mb));
    }

    // ═════════════════════════════ 인코더 ═════════════════════════════

    /// <summary>1 프레임 + 스플랫 → 액세스 유닛(무할당). 반환 = AU 바이트.</summary>
    public int Encode(ReadOnlySpan<float> frame, VolumetricSplatFrame? splats)
    {
        if (frame.Length < Pixels) throw new ArgumentException("frame < Pixels", nameof(frame));
        var t0 = Stopwatch.GetTimestamp();
        var n = splats is null ? 0 : Math.Min(splats.Count, MaxSplats);
        var key = _forceKey || !_encHasRef || Frames % GopLength == 0 || n != _encSplatCount;
        _forceKey = false;
        var qp = ChooseQp(key);
        SetQuant(qp, _step, _inv);
        _qstep = QStep(qp);
        _lambda = 0.368f * _qstep;

        _bw.Reset(_au, 4);
        _bw.Put(key ? 1u : 0u, 1);
        _bw.Put((uint)(Frames & 0xFFFF), 16);
        _bw.Put((uint)qp, 6);
        int skips = 0, intras = 0;
        for (var mby = 0; mby < MbRows; mby++)
            for (var mbx = 0; mbx < MbCols; mbx++)
            {
                var m = EncodeMb(frame, mbx, mby, key);
                if (m == NvcMbMode.Skip) skips++; else if (m == NvcMbMode.Intra) intras++;
            }
        var vlen = _bw.Finish();
        _au[0] = Magic; _au[1] = (byte)vlen; _au[2] = (byte)(vlen >> 8); _au[3] = (byte)(vlen >> 16);

        _bw.Reset(_au, 4 + vlen);
        EncodeSplats(splats, n, key, qp);
        var slen = _bw.Finish();
        _auLen = 4 + vlen + slen;
        (_rec, _recWork) = (_recWork, _rec);
        _encHasRef = true;

        LastType = key ? NvcFrameType.Intra : NvcFrameType.Inter;
        LastQp = qp;
        LastVideoBits = vlen * 8;
        LastSplatBits = slen * 8;
        SkipRatio = skips / (float)MbCount;
        IntraRatio = intras / (float)MbCount;
        EncoderPsnr = Psnr(frame.Slice(0, Pixels), _rec);
        TotalBits += LastBits;
        UpdateRate(key, qp, LastVideoBits);
        if (key) { KeyFrames++; LastKeyBits = LastVideoBits; }
        else { _lastInterQp = qp; AvgInterBits = AvgInterBits <= 0 ? LastVideoBits : AvgInterBits * 0.9f + LastVideoBits * 0.1f; }
        Frames++;
        EncodeUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        return _auLen;
    }

    private int ChooseQp(bool key)
    {
        if (TargetBitsPerFrame <= 0f) return Math.Clamp(FixedQp, MinQp, MaxQp);
        var target = MathF.Max(64f, key ? TargetBitsPerFrame * KeyBoost : TargetBitsPerFrame);
        var a = key ? _rcAKey : _rcAInter;
        var qp = (int)MathF.Round((a - MathF.Log2(target)) / RcSlope);
        if (!key) qp = Math.Clamp(qp, _lastInterQp - 4, _lastInterQp + 4);
        return Math.Clamp(qp, MinQp, MaxQp);
    }

    /// <summary>log2 비트 ≈ a − b·QP (b = 1/6 · 6 QP당 절반) — a를 LMS로 온라인 학습.</summary>
    public const float RcSlope = 1f / 6f;
    public float RateModelBias => _rcAInter;
    public float RateModelError { get; private set; }

    private void UpdateRate(bool key, int qp, int bits)
    {
        var e = MathF.Log2(MathF.Max(8, bits)) - ((key ? _rcAKey : _rcAInter) - RcSlope * qp);
        RateModelError = e;
        if (key) _rcAKey += 0.6f * e; else _rcAInter += 0.35f * e;
    }

    private NvcMbMode EncodeMb(ReadOnlySpan<float> src, int mbx, int mby, bool key)
    {
        int x0 = mbx * Mb, y0 = mby * Mb, mb = mby * MbCols + mbx;
        for (var r = 0; r < Mb; r++) src.Slice((y0 + r) * Width + x0, Mb).CopyTo(_cur.AsSpan(r * Mb, Mb));
        MvPredictor(_encMode, _encMvX, _encMvY, mbx, mby, out var px, out var py);
        var dc = DcPred(_recWork, x0, y0);
        var mode = NvcMbMode.Intra;
        int mvx = 0, mvy = 0;
        if (!key)
        {
            var sadIntra = 0f;
            for (var i = 0; i < Mb * Mb; i++) sadIntra += MathF.Abs(_cur[i] - dc);
            var j = MotionSearch(x0, y0, px, py, mb, out mvx, out mvy);
            if (j <= sadIntra + _lambda * 6f) mode = NvcMbMode.Inter;
            else { mvx = 0; mvy = 0; }
        }
        if (mode == NvcMbMode.Inter) LoadBlock(_rec, x0 + mvx, y0 + mvy, _pred);
        else Array.Fill(_pred, dc);

        NvcSimd.Sub(_cur, _pred, _res);
        var off = mode == NvcMbMode.Intra ? 1f / 3f : 1f / 6f;
        var coded = 0;
        for (var b = 0; b < 4; b++)
        {
            int bx = (b & 1) * 8, by = (b >> 1) * 8;
            for (var r = 0; r < 8; r++) _res.AsSpan((by + r) * Mb + bx, 8).CopyTo(_blk.AsSpan(r * 8, 8));
            Forward(_blk, _coef);
            var q = _q.AsSpan(b * 64, 64);
            var nz = NvcSimd.QuantizeWeighted(_coef, _inv, q, off);
            _nz[b] = nz;
            if (nz > 0)
            {
                coded++;
                NvcSimd.DequantWeighted(q, _step, _coef);
                Inverse(_coef, _blk);
            }
            else Array.Clear(_blk);
            for (var r = 0; r < 8; r++) _blk.AsSpan(r * 8, 8).CopyTo(_resRec.AsSpan((by + r) * Mb + bx, 8));
        }
        if (mode == NvcMbMode.Inter && coded == 0 && mvx == px && mvy == py) mode = NvcMbMode.Skip;

        NvcSimd.AddClamp(_pred, _resRec, _out, 0f, 255f);
        for (var r = 0; r < Mb; r++) _out.AsSpan(r * Mb, Mb).CopyTo(_recWork.AsSpan((y0 + r) * Width + x0, Mb));

        if (!key) _bw.PutUe((uint)mode);
        if (mode == NvcMbMode.Inter) { _bw.PutSe(mvx - px); _bw.PutSe(mvy - py); }
        if (mode != NvcMbMode.Skip)
            for (var b = 0; b < 4; b++)
            {
                _bw.Put(_nz[b] > 0 ? 1u : 0u, 1);
                if (_nz[b] > 0) WriteCoefs(_q.AsSpan(b * 64, 64), _nz[b]);
            }
        _encMode[mb] = mode;
        _encMvX[mb] = mode == NvcMbMode.Intra ? 0 : mvx;
        _encMvY[mb] = mode == NvcMbMode.Intra ? 0 : mvy;
        return mode;
    }

    private float BlockSad(int x, int y)
    {
        var s = 0f;
        for (var r = 0; r < Mb; r++) s += NvcSimd.Sad(_cur.AsSpan(r * Mb, Mb), _rec.AsSpan((y + r) * Width + x, Mb));
        return s;
    }

    private float MvCost(int x0, int y0, int mx, int my, int px, int py) =>
        MvValid(x0, y0, mx, my) ? BlockSad(x0 + mx, y0 + my) + _lambda * (Math.Abs(mx - px) + Math.Abs(my - py)) : float.MaxValue;

    /// <summary>후보(0 · 예측 · 이전 프레임 동위치) → 소형 다이아몬드 정제(최대 16회).</summary>
    private float MotionSearch(int x0, int y0, int px, int py, int mb, out int bx, out int by)
    {
        bx = 0; by = 0;
        var best = MvCost(x0, y0, 0, 0, px, py);
        var c = MvCost(x0, y0, px, py, px, py);
        if (c < best) { best = c; bx = px; by = py; }
        int cx = _encMvX[mb], cy = _encMvY[mb];
        c = MvCost(x0, y0, cx, cy, px, py);
        if (c < best) { best = c; bx = cx; by = cy; }
        for (var it = 0; it < 16; it++)
        {
            int nx = bx, ny = by;
            for (var d = 0; d < 4; d++)
            {
                var tx = bx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                var ty = by + (d == 2 ? 1 : d == 3 ? -1 : 0);
                c = MvCost(x0, y0, tx, ty, px, py);
                if (c < best) { best = c; nx = tx; ny = ty; }
            }
            if (nx == bx && ny == by) break;
            bx = nx; by = ny;
        }
        return best;
    }

    private void WriteCoefs(ReadOnlySpan<int> q, int nz)
    {
        _bw.PutUe((uint)(nz - 1));
        var last = -1;
        for (var z = 0; z < 64; z++)
        {
            var v = q[ZigZag[z]];
            if (v == 0) continue;
            _bw.PutUe((uint)(z - last - 1));
            _bw.PutSe(v);
            last = z;
        }
    }

    private void EncodeSplats(VolumetricSplatFrame? s, int n, bool key, int qp)
    {
        _bw.PutUe((uint)n);
        _encSplatCount = n;
        if (n == 0 || s is null) { SplatChangedRatio = 0f; return; }
        var refr = _encSplat;
        if (key) refr.Clear(n);
        refr.Count = n;
        var scale = SplatScale(qp);
        var res = _sres.AsSpan(0, n);
        for (var a = 0; a < VolumetricSplatFrame.Attributes; a++)
        {
            var step = SplatBaseStep[a] * scale;
            var p = refr.Plane[a].AsSpan(0, n);
            NvcSimd.Sub(s.Plane[a].AsSpan(0, n), p, res);
            var q = _sq[a].AsSpan(0, n);
            NvcSimd.QuantizeUniform(res, q, 1f / step, 0.5f);
            NvcSimd.DequantAddUniform(q, p, p, step);
        }
        var changed = 0;
        for (var i = 0; i < n; i++) if (Changed(i)) changed++;
        _bw.PutUe((uint)changed);
        var last = -1;
        for (var i = 0; i < n; i++)
        {
            if (!Changed(i)) continue;
            _bw.PutUe((uint)(i - last - 1));
            for (var a = 0; a < VolumetricSplatFrame.Attributes; a++) _bw.PutSe(_sq[a][i]);
            last = i;
        }
        SplatChangedRatio = changed / (float)n;
    }

    private bool Changed(int i)
    {
        for (var a = 0; a < VolumetricSplatFrame.Attributes; a++) if (_sq[a][i] != 0) return true;
        return false;
    }

    // ═════════════════════════════ 디코더 ═════════════════════════════

    /// <summary>AU 해독 → Decoded/DecodedSplats(무할당). 손상/참조 결손 시 false(참조 버퍼 보존).</summary>
    public bool Decode(ReadOnlySpan<byte> au)
    {
        var t0 = Stopwatch.GetTimestamp();
        if (au.Length < 4 || au.Length > AuCapacity || au[0] != Magic) return Fail();
        var vlen = au[1] | (au[2] << 8) | (au[3] << 16);
        if (4 + vlen > au.Length) return Fail();
        au.CopyTo(_rx);
        _br.Reset(_rx, 4, vlen);
        var key = _br.Get(1) == 1;
        _br.Get(16);
        var qp = (int)_br.Get(6);
        if (_br.Error || qp < MinQp || qp > MaxQp || (!key && !_decHasRef)) return Fail();
        SetQuant(qp, _dStep, null);
        for (var mby = 0; mby < MbRows; mby++)
            for (var mbx = 0; mbx < MbCols; mbx++)
                if (!DecodeMb(mbx, mby, key)) return Fail();
        if (_br.Error) return Fail();
        _br.Reset(_rx, 4 + vlen, au.Length - 4 - vlen);
        if (!DecodeSplats(key, qp)) return Fail();
        (_dec, _decWork) = (_decWork, _dec);
        (_decSplatPrev, _decSplat, _decSplatWork) = (_decSplat, _decSplatWork, _decSplatPrev);
        _decHasRef = true;
        LastDecodedKey = key;
        DecodedFrames++;
        DecodeUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        return true;
    }

    private bool Fail() { DecodeErrors++; return false; }

    private bool DecodeMb(int mbx, int mby, bool key)
    {
        int x0 = mbx * Mb, y0 = mby * Mb, mb = mby * MbCols + mbx;
        var mode = NvcMbMode.Intra;
        if (!key)
        {
            var m = _br.GetUe();
            if (_br.Error || m > 2) return false;
            mode = (NvcMbMode)m;
        }
        MvPredictor(_decMode, _decMvX, _decMvY, mbx, mby, out var px, out var py);
        int mvx = 0, mvy = 0;
        if (mode == NvcMbMode.Inter) { mvx = px + _br.GetSe(); mvy = py + _br.GetSe(); }
        else if (mode == NvcMbMode.Skip) { mvx = px; mvy = py; }
        if (_br.Error || !MvValid(x0, y0, mvx, mvy)) return false;
        if (mode == NvcMbMode.Intra) Array.Fill(_pred, DcPred(_decWork, x0, y0));
        else LoadBlock(_dec, x0 + mvx, y0 + mvy, _pred);

        Array.Clear(_resRec);
        if (mode != NvcMbMode.Skip)
            for (var b = 0; b < 4; b++)
            {
                if (_br.Get(1) == 0) continue;
                var q = _q.AsSpan(0, 64);
                if (!ReadCoefs(q)) return false;
                NvcSimd.DequantWeighted(q, _dStep, _coef);
                Inverse(_coef, _blk);
                int bx = (b & 1) * 8, by = (b >> 1) * 8;
                for (var r = 0; r < 8; r++) _blk.AsSpan(r * 8, 8).CopyTo(_resRec.AsSpan((by + r) * Mb + bx, 8));
            }
        if (_br.Error) return false;
        NvcSimd.AddClamp(_pred, _resRec, _out, 0f, 255f);
        for (var r = 0; r < Mb; r++) _out.AsSpan(r * Mb, Mb).CopyTo(_decWork.AsSpan((y0 + r) * Width + x0, Mb));
        _decMode[mb] = mode;
        _decMvX[mb] = mode == NvcMbMode.Intra ? 0 : mvx;
        _decMvY[mb] = mode == NvcMbMode.Intra ? 0 : mvy;
        return true;
    }

    private bool ReadCoefs(Span<int> q)
    {
        q.Clear();
        var n = _br.GetUe() + 1;
        if (_br.Error || n > 64) return false;
        var pos = -1;
        for (var i = 0; i < n; i++)
        {
            pos += (int)_br.GetUe() + 1;
            var lv = _br.GetSe();
            if (_br.Error || pos > 63 || lv == 0) return false;
            q[ZigZag[pos]] = lv;
        }
        return true;
    }

    private bool DecodeSplats(bool key, int qp)
    {
        var n = (int)_br.GetUe();
        var w = _decSplatWork;
        if (_br.Error || n > MaxSplats || (!key && n != _decSplat.Count)) return false;
        if (n == 0) { w.Count = 0; return true; }
        if (key) w.Clear(n); else w.CopyFrom(_decSplat);
        w.Count = n;
        var scale = SplatScale(qp);
        var changed = (int)_br.GetUe();
        if (_br.Error || changed > n) return false;
        var idx = -1;
        for (var c = 0; c < changed; c++)
        {
            idx += (int)_br.GetUe() + 1;
            if (_br.Error || idx >= n) return false;
            for (var a = 0; a < VolumetricSplatFrame.Attributes; a++)
            {
                var q = _br.GetSe();
                w.Plane[a][idx] = w.Plane[a][idx] + q * (SplatBaseStep[a] * scale);
            }
        }
        return !_br.Error;
    }

    /// <summary>
    /// 프레임 손실 은닉(프레임 생성): 직전 해독 움직임장으로 참조를 외삽 와프 · 스플랫 위치 등속 외삽.
    /// 결과가 다음 참조가 되므로 키프레임 수신 전까지 드리프트(→ PLI).
    /// </summary>
    public bool Conceal()
    {
        if (!_decHasRef) return false;
        for (var mby = 0; mby < MbRows; mby++)
            for (var mbx = 0; mbx < MbCols; mbx++)
            {
                int x0 = mbx * Mb, y0 = mby * Mb, mb = mby * MbCols + mbx;
                int mx = _decMvX[mb], my = _decMvY[mb];
                if (!MvValid(x0, y0, mx, my)) { mx = 0; my = 0; }
                for (var r = 0; r < Mb; r++) _dec.AsSpan((y0 + my + r) * Width + x0 + mx, Mb).CopyTo(_decWork.AsSpan((y0 + r) * Width + x0, Mb));
            }
        (_dec, _decWork) = (_decWork, _dec);
        var cur = _decSplat; var prev = _decSplatPrev; var w = _decSplatWork;
        w.CopyFrom(cur);
        if (prev.Count == cur.Count)
            for (var a = 0; a < 3; a++)
            {
                var pw = w.Plane[a]; var pp = prev.Plane[a]; var pc = cur.Plane[a];
                for (var i = 0; i < cur.Count; i++) pw[i] = pc[i] + (pc[i] - pp[i]);
            }
        (_decSplatPrev, _decSplat, _decSplatWork) = (_decSplat, _decSplatWork, _decSplatPrev);
        ConcealedFrames++;
        return true;
    }

    /// <summary>디코더 상태 초기화(새 세션 · 키프레임 대기).</summary>
    public void ResetDecoder()
    {
        _decHasRef = false;
        Array.Fill(_dec, 128f);
        _decSplat.Count = _decSplatPrev.Count = _decSplatWork.Count = 0;
    }

    // ═════════════════════════════ 품질 · 합성 소스 ═════════════════════════════

    public static float Psnr(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) { var d = a[i] - b[i]; s += d * d; }
        var mse = s / Math.Max(1, a.Length);
        return mse < 1e-10 ? 99f : (float)(10.0 * Math.Log10(255.0 * 255.0 / mse));
    }

    /// <summary>8×8 비중첩 창 SSIM 평균(C1=(0.01·255)², C2=(0.03·255)²).</summary>
    public static float Ssim(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        const double c1 = 6.5025, c2 = 58.5225;
        double acc = 0; var n = 0;
        for (var y0 = 0; y0 + 8 <= Height; y0 += 8)
            for (var x0 = 0; x0 + 8 <= Width; x0 += 8)
            {
                double ma = 0, mb = 0;
                for (var y = 0; y < 8; y++) for (var x = 0; x < 8; x++) { var o = (y0 + y) * Width + x0 + x; ma += a[o]; mb += b[o]; }
                ma /= 64; mb /= 64;
                double va = 0, vb = 0, cv = 0;
                for (var y = 0; y < 8; y++)
                    for (var x = 0; x < 8; x++)
                    {
                        var o = (y0 + y) * Width + x0 + x;
                        var da = a[o] - ma; var db = b[o] - mb;
                        va += da * da; vb += db * db; cv += da * db;
                    }
                va /= 63; vb /= 63; cv /= 63;
                acc += (2 * ma * mb + c1) * (2 * cv + c2) / ((ma * ma + mb * mb + c1) * (va + vb + c2));
                n++;
            }
        return (float)(acc / n);
    }

    /// <summary>합성 테스트 소스(무할당 · 결정적): 1 px/프레임 패닝 배경 + 체커 텍스처 + 리사주 원판 3개.</summary>
    public static void SynthesizeFrame(Span<float> dst, double t)
    {
        var pan = (float)(t * 30.0);
        var tf = (float)t;
        float c0x = 128f + 80f * MathF.Sin(1.3f * tf), c0y = 72f + 40f * MathF.Sin(0.9f * tf);
        float c1x = 70f + 40f * MathF.Cos(0.7f * tf), c1y = 50f + 25f * MathF.Sin(1.7f * tf);
        float c2x = 200f + 30f * MathF.Sin(2.1f * tf), c2y = 100f + 20f * MathF.Cos(1.1f * tf);
        for (var y = 0; y < Height; y++)
        {
            var row = dst.Slice(y * Width, Width);
            for (var x = 0; x < Width; x++)
            {
                var u = x + pan;
                var v = 96f + 48f * MathF.Sin(0.045f * u + 0.03f * y) + 22f * MathF.Cos(0.07f * y - 0.02f * u);
                v += (((int)MathF.Floor(u * 0.125f) ^ (y >> 3)) & 1) == 0 ? 10f : -10f;
                v = Disc(v, x, y, c0x, c0y, 20f, 225f);
                v = Disc(v, x, y, c1x, c1y, 14f, 30f);
                v = Disc(v, x, y, c2x, c2y, 17f, 180f);
                row[x] = v;
            }
        }
    }

    private static float Disc(float v, int x, int y, float cx, float cy, float r, float val)
    {
        float dx = x - cx, dy = y - cy;
        var a = Math.Clamp(r - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0f, 1f);
        return v + (val - v) * a;
    }

    /// <summary>합성 볼류메트릭 배우 스플랫(무할당 · 결정적): 몸통 정지 · 25% 팔/머리 영역만 운동(시간 중복성).</summary>
    public static void SynthesizeSplats(VolumetricSplatFrame f, int n, double t)
    {
        n = Math.Min(n, f.Capacity);
        f.Count = n;
        var tf = (float)t;
        for (var i = 0; i < n; i++)
        {
            var h = (uint)i * 2654435761u;
            float u1 = (h & 0xFFFF) / 65536f, u2 = ((h >> 16) & 0xFFFF) / 65536f;
            var h2 = h ^ (h >> 13) * 0x5bd1e995u;
            var u3 = (h2 & 0xFFFF) / 65536f;
            var th = u1 * MathF.Tau;
            var y = 0.1f + 1.6f * u2;
            var rad = 0.18f + 0.06f * MathF.Sin(u2 * 9f);
            var x = rad * MathF.Cos(th); var z = rad * MathF.Sin(th);
            if ((i & 3) == 0) { x += 0.12f * MathF.Sin(2f * tf + y * 3f); z += 0.05f * MathF.Cos(1.5f * tf + y); }
            f.Plane[0][i] = x; f.Plane[1][i] = y; f.Plane[2][i] = z;
            f.Plane[3][i] = f.Plane[4][i] = f.Plane[5][i] = 0.008f + 0.02f * u3;
            f.Plane[6][i] = 0.55f + 0.4f * u1;
            f.Plane[7][i] = 0.3f + 0.6f * u2; f.Plane[8][i] = 0.45f + 0.3f * u3; f.Plane[9][i] = 0.7f - 0.4f * u2;
        }
    }
}
