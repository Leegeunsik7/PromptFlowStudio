using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.HolographicTelepresence;

/// <summary>청크 전송 정밀도(작을수록 고정밀).</summary>
public enum StreamLod : byte { F16 = 0, Q8 = 1, Q8Sparse = 2, Proxy = 3 }

/// <summary>체적 스트리밍 SIMD 커널 — 모든 경로 public · 스칼라 쌍둥이와 비트 동일.</summary>
public static class VolumetricKernels
{
    /// <summary>q = (int)clamp((x − min)·s + 0.5, 0, 255) — 스칼라 기준 구현.</summary>
    public static void QuantizeScalar(ReadOnlySpan<float> src, float min, float s, Span<byte> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("dst too short", nameof(dst));
        for (var i = 0; i < src.Length; i++) dst[i] = (byte)(uint)MathF.Min(MathF.Max((src[i] - min) * s + 0.5f, 0f), 255f);
    }

    /// <summary>Vector&lt;float&gt; 4묶음 → ConvertToUInt32 → Narrow×2 로 바이트 벡터 1개 출력 · 꼬리 스칼라.</summary>
    public static void QuantizeSimd(ReadOnlySpan<float> src, float min, float s, Span<byte> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("dst too short", nameof(dst));
        var i = 0;
        var vb = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && src.Length >= vb)
        {
            var w = Vector<float>.Count;
            var vmin = new Vector<float>(min);
            var vs = new Vector<float>(s);
            var half = new Vector<float>(0.5f);
            var hi = new Vector<float>(255f);
            ref var rs = ref MemoryMarshal.GetReference(src);
            ref var rd = ref MemoryMarshal.GetReference(dst);
            for (; i <= src.Length - vb; i += vb)
            {
                var a = Vector.ConvertToUInt32(Vector.Min(Vector.Max((Load(ref rs, i) - vmin) * vs + half, Vector<float>.Zero), hi));
                var b = Vector.ConvertToUInt32(Vector.Min(Vector.Max((Load(ref rs, i + w) - vmin) * vs + half, Vector<float>.Zero), hi));
                var c = Vector.ConvertToUInt32(Vector.Min(Vector.Max((Load(ref rs, i + 2 * w) - vmin) * vs + half, Vector<float>.Zero), hi));
                var d = Vector.ConvertToUInt32(Vector.Min(Vector.Max((Load(ref rs, i + 3 * w) - vmin) * vs + half, Vector<float>.Zero), hi));
                var bytes = Vector.Narrow(Vector.Narrow(a, b), Vector.Narrow(c, d));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref rd, i), bytes);
            }
        }
        for (; i < src.Length; i++) dst[i] = (byte)(uint)MathF.Min(MathF.Max((src[i] - min) * s + 0.5f, 0f), 255f);
    }

    /// <summary>dst[i] = min + q[i]·inv — 스칼라 기준 구현.</summary>
    public static void DequantizeScalar(ReadOnlySpan<byte> src, float min, float inv, Span<float> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("dst too short", nameof(dst));
        for (var i = 0; i < src.Length; i++) dst[i] = min + src[i] * inv;
    }

    /// <summary>바이트 벡터 1개 → Widen×2 → ConvertToSingle 4묶음 · 꼬리 스칼라.</summary>
    public static void DequantizeSimd(ReadOnlySpan<byte> src, float min, float inv, Span<float> dst)
    {
        if (dst.Length < src.Length) throw new ArgumentException("dst too short", nameof(dst));
        var i = 0;
        var vb = Vector<byte>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && src.Length >= vb)
        {
            var w = Vector<float>.Count;
            var vmin = new Vector<float>(min);
            var vinv = new Vector<float>(inv);
            ref var rs = ref MemoryMarshal.GetReference(src);
            ref var rd = ref MemoryMarshal.GetReference(dst);
            for (; i <= src.Length - vb; i += vb)
            {
                Vector.Widen(Unsafe.ReadUnaligned<Vector<byte>>(ref Unsafe.Add(ref rs, i)), out var lo, out var hi);
                Vector.Widen(lo, out var a, out var b);
                Vector.Widen(hi, out var c, out var d);
                Store(ref rd, i, vmin + Vector.ConvertToSingle(a) * vinv);
                Store(ref rd, i + w, vmin + Vector.ConvertToSingle(b) * vinv);
                Store(ref rd, i + 2 * w, vmin + Vector.ConvertToSingle(c) * vinv);
                Store(ref rd, i + 3 * w, vmin + Vector.ConvertToSingle(d) * vinv);
            }
        }
        for (; i < src.Length; i++) dst[i] = min + src[i] * inv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store(ref float r, int i, Vector<float> v) => Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref r, i)), v);

    public static void MinMaxScalar(ReadOnlySpan<float> src, out float min, out float max)
    {
        min = float.PositiveInfinity; max = float.NegativeInfinity;
        for (var i = 0; i < src.Length; i++) { var v = src[i]; if (v < min) min = v; if (v > max) max = v; }
    }

    public static void MinMaxSimd(ReadOnlySpan<float> src, out float min, out float max)
    {
        min = float.PositiveInfinity; max = float.NegativeInfinity;
        var i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && src.Length >= w)
        {
            ref var rs = ref MemoryMarshal.GetReference(src);
            var vmin = new Vector<float>(float.PositiveInfinity);
            var vmax = new Vector<float>(float.NegativeInfinity);
            for (; i <= src.Length - w; i += w) { var v = Load(ref rs, i); vmin = Vector.Min(vmin, v); vmax = Vector.Max(vmax, v); }
            for (var k = 0; k < w; k++) { if (vmin[k] < min) min = vmin[k]; if (vmax[k] > max) max = vmax[k]; }
        }
        for (; i < src.Length; i++) { var v = src[i]; if (v < min) min = v; if (v > max) max = v; }
    }

    /// <summary>dst[i] = src[i] + add(청크 강체 오프셋 적용).</summary>
    public static void AddScalarSimd(ReadOnlySpan<float> src, float add, Span<float> dst)
    {
        var i = 0;
        var w = Vector<float>.Count;
        if (Vector.IsHardwareAccelerated && BitConverter.IsLittleEndian && src.Length >= w)
        {
            ref var rs = ref MemoryMarshal.GetReference(src);
            ref var rd = ref MemoryMarshal.GetReference(dst);
            var va = new Vector<float>(add);
            for (; i <= src.Length - w; i += w) Unsafe.WriteUnaligned(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref rd, i)), Load(ref rs, i) + va);
        }
        for (; i < src.Length; i++) dst[i] = src[i] + add;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector<float> Load(ref float r, int i) => Unsafe.ReadUnaligned<Vector<float>>(ref Unsafe.As<float, byte>(ref Unsafe.Add(ref r, i)));
}

/// <summary>
/// [PromptFlow Studio v17.25.0] 📡 실시간 4D 가우시안 체적 스트리밍 &amp; 동적 LoD 압축 엔진(<see cref="Animate"/> · <see cref="Plan"/> · <see cref="Encode"/> · <see cref="Decode"/> 무할당).
///   ▸ v17.20 아바타 규모(<see cref="GaussianAvatar.DeformableGaussianSkinningBindingEngine.DefaultCount"/>) 가우시안을 깊이 4 옥트리 리프(16³ 셀) 모턴(Z-order) 순서로 정렬 — 채널 우선 SoA(위치 3 · 로그 스케일 3 · 쿼터니언 4 · RGB 3 · 불투명도 1).
///   ▸ 원격 참가자(최대 3)별 대역폭 예산(Mbps → 프레임 바이트) 안에서 거리·밀도 우선순위 점진 정제: 전체 프록시 → 희소 8bit(1/4) → 8bit → 16bit Half.
///   ▸ 8bit: 위치는 청크 AABB 범위, 그 외는 전역 고정 범위로 양자화(Vector&lt;float&gt; → Narrow×2 SIMD) · 디코더 재구성 RMS/최대 위치 오차 측정.
///   ▸ 데이터그램(청크 단위) 손실 시 수신측은 직전 재구성 유지(stale) — QUIC/WebTransport 비신뢰 스트림 모사.
/// </summary>
public sealed class NeuralVolumetricGaussianStreamingEngine
{
    public const int Channels = 14, Depth = 4, Side = 1 << Depth, FineBits = 10, FineSide = 1 << FineBits, Cells = Side * Side * Side, MaxViewers = 3, Hz = 60;
    public const int FrameHeader = 6, ChunkHeader = 5, CenterBytes = 12, RangeBytes = 24, BytesF16 = Channels * 2, BytesQ8 = Channels, SparseStride = 4, Levels = 4;

    private static readonly float[] GMin = { 0f, 0f, 0f, -7f, -7f, -7f, -1f, -1f, -1f, -1f, 0f, 0f, 0f, 0f };
    private static readonly float[] GMax = { 0f, 0f, 0f, -2f, -2f, -2f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f };
    public static readonly string[] LevelNames = { "F16", "Q8", "Q8/4", "프록시" };

    private readonly int _n, _chunks, _maxChunk;
    private readonly float[] _rest, _cur;                 // [c·N + i] 모턴 순서
    private readonly int[] _cStart;                       // 청크 시작(+1 = 끝)
    private readonly ushort[] _cCell;
    private readonly float[] _cMin, _cMax;                // 정지 자세 청크 AABB(3 × 청크)
    private readonly Vector3[] _cOff, _cWorld;
    private readonly float[] _prio, _dist, _scratch;
    private readonly int[] _order;
    private readonly Viewer[] _v = new Viewer[MaxViewers];
    private Vector3 _origin;
    private float _yawCos = 1f, _yawSin;

    private sealed class Viewer
    {
        public Vector3 Pos;
        public float Mbps = 250f;
        public byte[] Packet = Array.Empty<byte>();
        public StreamLod[] Level = Array.Empty<StreamLod>();
        public float[] Recon = Array.Empty<float>();
        public readonly int[] Hist = new int[Levels];
        public int Bytes, Lost, Planned;
        public long TotalLost;
        public float RmsErrMm, MaxErrMm;
    }

    public NeuralVolumetricGaussianStreamingEngine() : this(GaussianAvatar.DeformableGaussianSkinningBindingEngine.DefaultCount) { }

    public NeuralVolumetricGaussianStreamingEngine(int count, int seed = 1725)
    {
        _n = Math.Max(64, count);
        var raw = new float[Channels * _n];
        Generate(raw, _n, seed);

        // 옥트리 리프 셀(모턴 키) 카운팅 정렬
        float bx0 = float.MaxValue, by0 = float.MaxValue, bz0 = float.MaxValue, bx1 = float.MinValue, by1 = float.MinValue, bz1 = float.MinValue;
        for (var i = 0; i < _n; i++)
        {
            bx0 = MathF.Min(bx0, raw[i]); bx1 = MathF.Max(bx1, raw[i]);
            by0 = MathF.Min(by0, raw[_n + i]); by1 = MathF.Max(by1, raw[_n + i]);
            bz0 = MathF.Min(bz0, raw[2 * _n + i]); bz1 = MathF.Max(bz1, raw[2 * _n + i]);
        }
        var ext = MathF.Max(bx1 - bx0, MathF.Max(by1 - by0, bz1 - bz0)) * 1.001f + 1e-4f;
        BoundsMin = new Vector3(bx0, by0, bz0);
        BoundsSize = ext;
        var key = new int[_n];
        var hist = new int[Cells + 1];
        for (var i = 0; i < _n; i++)
        {
            var cx = Math.Clamp((int)((raw[i] - bx0) / ext * FineSide), 0, FineSide - 1) >> (FineBits - Depth);
            var cy = Math.Clamp((int)((raw[_n + i] - by0) / ext * FineSide), 0, FineSide - 1) >> (FineBits - Depth);
            var cz = Math.Clamp((int)((raw[2 * _n + i] - bz0) / ext * FineSide), 0, FineSide - 1) >> (FineBits - Depth);
            key[i] = Morton(cx, cy, cz);
            hist[key[i] + 1]++;
        }
        for (var k = 0; k < Cells; k++) hist[k + 1] += hist[k];
        // 청크 내부는 10 bit/축 정밀 모턴 순서(상위 12 bit = 리프 셀) → 희소 표본 간격이 공간적으로 인접
        var fine = new long[_n];
        var order = new int[_n];
        for (var i = 0; i < _n; i++)
        {
            var fx = Math.Clamp((int)((raw[i] - bx0) / ext * FineSide), 0, FineSide - 1);
            var fy = Math.Clamp((int)((raw[_n + i] - by0) / ext * FineSide), 0, FineSide - 1);
            var fz = Math.Clamp((int)((raw[2 * _n + i] - bz0) / ext * FineSide), 0, FineSide - 1);
            fine[i] = Morton(fx, fy, fz, FineBits);
            order[i] = i;
        }
        Array.Sort(fine, order);
        var dstIdx = new int[_n];
        for (var k = 0; k < _n; k++) dstIdx[order[k]] = k;
        _rest = new float[Channels * _n];
        for (var c = 0; c < Channels; c++)
            for (var i = 0; i < _n; i++) _rest[c * _n + dstIdx[i]] = raw[c * _n + i];
        _cur = (float[])_rest.Clone();

        var nonEmpty = 0;
        for (var k = 0; k < Cells; k++) if (hist[k + 1] > hist[k]) nonEmpty++;
        _chunks = nonEmpty;
        _cStart = new int[_chunks + 1];
        _cCell = new ushort[_chunks];
        var j = 0;
        for (var k = 0; k < Cells; k++)
        {
            if (hist[k + 1] == hist[k]) continue;
            _cStart[j] = hist[k]; _cCell[j] = (ushort)k; j++;
            _maxChunk = Math.Max(_maxChunk, hist[k + 1] - hist[k]);
        }
        _cStart[_chunks] = _n;
        _cMin = new float[3 * _chunks]; _cMax = new float[3 * _chunks];
        for (var c = 0; c < _chunks; c++)
            for (var a = 0; a < 3; a++)
            {
                VolumetricKernels.MinMaxScalar(_rest.AsSpan(a * _n + _cStart[c], _cStart[c + 1] - _cStart[c]), out var mn, out var mx);
                _cMin[3 * c + a] = mn; _cMax[3 * c + a] = mx;
            }
        _cOff = new Vector3[_chunks]; _cWorld = new Vector3[_chunks];
        _prio = new float[_chunks]; _dist = new float[_chunks]; _order = new int[_chunks];
        _scratch = new float[_maxChunk / SparseStride + 1];
        var maxPacket = FrameHeader + _chunks * (ChunkHeader + RangeBytes + CenterBytes + BytesF16) + _n * BytesF16;
        for (var v = 0; v < MaxViewers; v++)
            _v[v] = new Viewer { Packet = new byte[maxPacket], Level = new StreamLod[_chunks], Recon = new float[Channels * _n] };
        SetPlacement(Vector3.Zero, 0f);
    }

    public int Count => _n;
    public int ChunkCount => _chunks;
    public int MaxChunkSize => _maxChunk;
    public Vector3 BoundsMin { get; }
    public float BoundsSize { get; }
    public long Frame { get; private set; }
    public bool UseSimd { get; set; } = true;
    public float NearM { get; set; } = 1.5f;
    public float MidM { get; set; } = 2.2f;
    public double LastEncodeMs { get; private set; }
    public double LastDecodeMs { get; private set; }

    public int ChunkStart(int c) => _cStart[c];
    public int ChunkSize(int c) => _cStart[c + 1] - _cStart[c];
    public int ChunkCell(int c) => _cCell[c];
    public Vector3 ChunkCenterLocal(int c) => new Vector3(_cMin[3 * c] + _cMax[3 * c], _cMin[3 * c + 1] + _cMax[3 * c + 1], _cMin[3 * c + 2] + _cMax[3 * c + 2]) * 0.5f + _cOff[c];
    public Vector3 ChunkCenterWorld(int c) => _cWorld[c];
    public float Value(int channel, int i) => _cur[channel * _n + i];
    public float Reconstructed(int viewer, int channel, int i) => _v[viewer].Recon[channel * _n + i];
    public Vector3 ViewerPosition(int v) => _v[v].Pos;
    public void SetViewerPosition(int v, Vector3 p) => _v[v].Pos = p;
    public float BudgetMbps(int v) => _v[v].Mbps;
    public void SetBudgetMbps(int v, float mbps) => _v[v].Mbps = Math.Clamp(mbps, 5f, 2000f);
    public float BudgetBytes(int v) => _v[v].Mbps * 1e6f / 8f / Hz;
    public StreamLod Level(int v, int c) => _v[v].Level[c];
    public int LevelCount(int v, StreamLod l) => _v[v].Hist[(int)l];
    public int PacketBytes(int v) => _v[v].Bytes;
    public int PlannedBytes(int v) => _v[v].Planned;
    public float SentMbps(int v) => _v[v].Bytes * 8f * Hz / 1e6f;
    public float RmsErrorMm(int v) => _v[v].RmsErrMm;
    public float MaxErrorMm(int v) => _v[v].MaxErrMm;
    public int LostChunks(int v) => _v[v].Lost;
    public long TotalLostChunks(int v) => _v[v].TotalLost;
    public ReadOnlySpan<byte> Packet(int v) => _v[v].Packet.AsSpan(0, _v[v].Bytes);
    public static float RawBytesPerFrame(int count) => count * (float)(Channels * sizeof(float));

    /// <summary>아바타 배치(좌석 원점 · y축 회전).</summary>
    public void SetPlacement(Vector3 origin, float yaw)
    {
        _origin = origin; _yawCos = MathF.Cos(yaw); _yawSin = MathF.Sin(yaw);
    }

    public Vector3 ToWorld(Vector3 p) => _origin + new Vector3(p.X * _yawCos + p.Z * _yawSin, p.Y, -p.X * _yawSin + p.Z * _yawCos);

    /// <summary>4D 애니메이션: 청크별 강체 오프셋(호흡 · 흔들림 · 고개 끄덕임)을 위치 채널에 SIMD 가산.</summary>
    public void Animate(double ms)
    {
        var t = (float)(ms / 1000.0);
        var sway = MathF.Sin(2f * MathF.PI * 0.4f * t);
        var breath = MathF.Sin(2f * MathF.PI * 0.25f * t);
        var nod = MathF.Sin(2f * MathF.PI * 0.9f * t);
        var top = BoundsMin.Y + BoundsSize;
        for (var c = 0; c < _chunks; c++)
        {
            var cy = (_cMin[3 * c + 1] + _cMax[3 * c + 1]) * 0.5f;
            var h = Math.Clamp((cy - 0.55f) / (top - 0.55f), 0f, 1f);
            var head = cy > 1.22f ? 1f : 0f;
            var off = new Vector3(0.025f * sway * h, 0.004f * breath * h + 0.006f * nod * head, 0.012f * nod * head + 0.006f * breath * h);
            _cOff[c] = off;
            int s = _cStart[c], n = _cStart[c + 1] - s;
            AddScalar(_rest.AsSpan(s, n), off.X, _cur.AsSpan(s, n));
            AddScalar(_rest.AsSpan(_n + s, n), off.Y, _cur.AsSpan(_n + s, n));
            AddScalar(_rest.AsSpan(2 * _n + s, n), off.Z, _cur.AsSpan(2 * _n + s, n));
            _cWorld[c] = ToWorld(ChunkCenterLocal(c));
        }
        Frame++;
    }

    /// <summary>청크 전송 비용(바이트).</summary>
    public int Cost(int c, StreamLod l)
    {
        var n = _cStart[c + 1] - _cStart[c];
        return l switch
        {
            StreamLod.F16 => ChunkHeader + CenterBytes + n * BytesF16,
            StreamLod.Q8 => ChunkHeader + RangeBytes + n * BytesQ8,
            StreamLod.Q8Sparse => ChunkHeader + RangeBytes + SparseCount(n) * BytesQ8,
            _ => ChunkHeader + BytesF16,
        };
    }

    public static int SparseCount(int n) => (n + SparseStride - 1) / SparseStride;

    /// <summary>거리 허용 상한(가까울수록 고정밀).</summary>
    public StreamLod AllowedBest(float d) => d < NearM ? StreamLod.F16 : d < MidM ? StreamLod.Q8 : StreamLod.Q8Sparse;

    /// <summary>예산 내 점진 정제: 전체 프록시 → 우선순위(밀도/거리²) 순으로 희소 → 8bit → 16bit 승급.</summary>
    public void Plan(int v)
    {
        var vw = _v[v];
        var budget = BudgetBytes(v) - FrameHeader;
        var used = 0;
        for (var c = 0; c < _chunks; c++)
        {
            vw.Level[c] = StreamLod.Proxy;
            used += Cost(c, StreamLod.Proxy);
            var d = Vector3.Distance(_cWorld[c], vw.Pos);
            _dist[c] = d;
            _prio[c] = -(_cStart[c + 1] - _cStart[c]) / MathF.Max(d * d, 0.01f);
            _order[c] = c;
        }
        _prio.AsSpan(0, _chunks).Sort(_order.AsSpan(0, _chunks));
        for (var pass = (int)StreamLod.Q8Sparse; pass >= 0; pass--)
        {
            var lvl = (StreamLod)pass;
            for (var k = 0; k < _chunks; k++)
            {
                var c = _order[k];
                if (vw.Level[c] <= lvl || AllowedBest(_dist[c]) > lvl) continue;
                var extra = Cost(c, lvl) - Cost(c, vw.Level[c]);
                if (used + extra > budget) continue;
                vw.Level[c] = lvl;
                used += extra;
            }
        }
        Array.Clear(vw.Hist);
        for (var c = 0; c < _chunks; c++) vw.Hist[(int)vw.Level[c]]++;
        vw.Planned = used + FrameHeader;
    }

    /// <summary>계획된 LoD 로 패킷 직렬화(채널 우선 · 리틀 엔디언).</summary>
    public void Encode(int v)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var vw = _v[v];
        var p = vw.Packet.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(p, (uint)Frame);
        BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(4), (ushort)_chunks);
        var o = FrameHeader;
        Span<float> mins = stackalloc float[3];
        Span<float> scales = stackalloc float[3];
        for (var c = 0; c < _chunks; c++)
        {
            int s = _cStart[c], n = _cStart[c + 1] - s;
            var lvl = vw.Level[c];
            BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(o), (ushort)c);
            p[o + 2] = (byte)lvl;
            BinaryPrimitives.WriteUInt16LittleEndian(p.Slice(o + 3), (ushort)n);
            o += ChunkHeader;
            if (lvl == StreamLod.F16)
            {
                // 위치는 청크 중심 기준 상대값(Half 유효 자릿수 확보)
                var ctr = ChunkCenterLocal(c);
                mins[0] = ctr.X; mins[1] = ctr.Y; mins[2] = ctr.Z;
                for (var a = 0; a < 3; a++) BinaryPrimitives.WriteSingleLittleEndian(p.Slice(o + 4 * a), mins[a]);
                o += CenterBytes;
                for (var ch = 0; ch < Channels; ch++)
                {
                    var src = _cur.AsSpan(ch * _n + s, n);
                    var bias = ch < 3 ? mins[ch] : 0f;
                    for (var i = 0; i < n; i++) { BinaryPrimitives.WriteHalfLittleEndian(p.Slice(o), (Half)(src[i] - bias)); o += 2; }
                }
            }
            else if (lvl == StreamLod.Proxy)
            {
                for (var ch = 0; ch < Channels; ch++) { BinaryPrimitives.WriteHalfLittleEndian(p.Slice(o), (Half)ProxyValue(c, ch)); o += 2; }
            }
            else
            {
                var sparse = lvl == StreamLod.Q8Sparse;
                var m = sparse ? SparseCount(n) : n;
                for (var a = 0; a < 3; a++)
                {
                    MinMax(_cur.AsSpan(a * _n + s, n), out var mn, out var mx);
                    mins[a] = mn; scales[a] = 255f / MathF.Max(mx - mn, 1e-6f);
                    BinaryPrimitives.WriteSingleLittleEndian(p.Slice(o), mn);
                    BinaryPrimitives.WriteSingleLittleEndian(p.Slice(o + 12), scales[a]);
                    o += 4;
                }
                o += 12;
                for (var ch = 0; ch < Channels; ch++)
                {
                    float mn, sc;
                    if (ch < 3) { mn = mins[ch]; sc = scales[ch]; }
                    else { mn = GMin[ch]; sc = 255f / (GMax[ch] - GMin[ch]); }
                    ReadOnlySpan<float> src;
                    if (sparse)
                    {
                        var basis = ch * _n + s;
                        for (int i = 0, k = 0; k < m; i += SparseStride, k++) _scratch[k] = _cur[basis + i];
                        src = _scratch.AsSpan(0, m);
                    }
                    else src = _cur.AsSpan(ch * _n + s, n);
                    if (UseSimd) VolumetricKernels.QuantizeSimd(src, mn, sc, p.Slice(o, m));
                    else VolumetricKernels.QuantizeScalar(src, mn, sc, p.Slice(o, m));
                    o += m;
                }
            }
        }
        vw.Bytes = o;
        LastEncodeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>수신측 디코드(청크 데이터그램 손실률 · xorshift 결정적) → 재구성 버퍼 · 위치 오차 측정.</summary>
    public void Decode(int v, float lossRate, ref uint rng)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var vw = _v[v];
        ReadOnlySpan<byte> p = vw.Packet.AsSpan(0, vw.Bytes);
        var rec = vw.Recon;
        var chunks = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(4));
        var o = FrameHeader;
        vw.Lost = 0;
        for (var j = 0; j < chunks; j++)
        {
            int c = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(o));
            var lvl = (StreamLod)p[o + 2];
            int n = BinaryPrimitives.ReadUInt16LittleEndian(p.Slice(o + 3));
            o += ChunkHeader;
            var s = _cStart[c];
            rng ^= rng << 13; rng ^= rng >> 17; rng ^= rng << 5;
            var lost = (rng >> 8) * (1f / 16777216f) < lossRate;
            if (lost) { vw.Lost++; vw.TotalLost++; }
            if (lvl == StreamLod.F16)
            {
                var co = o;
                o += CenterBytes;
                for (var ch = 0; ch < Channels; ch++)
                {
                    var b = ch * _n + s;
                    var bias = ch < 3 ? BinaryPrimitives.ReadSingleLittleEndian(p.Slice(co + 4 * ch)) : 0f;
                    if (!lost) for (var i = 0; i < n; i++) rec[b + i] = bias + (float)BinaryPrimitives.ReadHalfLittleEndian(p.Slice(o + 2 * i));
                    o += 2 * n;
                }
            }
            else if (lvl == StreamLod.Proxy)
            {
                for (var ch = 0; ch < Channels; ch++)
                {
                    if (!lost) rec.AsSpan(ch * _n + s, n).Fill((float)BinaryPrimitives.ReadHalfLittleEndian(p.Slice(o)));
                    o += 2;
                }
            }
            else
            {
                var sparse = lvl == StreamLod.Q8Sparse;
                var m = sparse ? SparseCount(n) : n;
                var ro = o;
                o += RangeBytes;
                for (var ch = 0; ch < Channels; ch++)
                {
                    if (!lost)
                    {
                        float mn, inv;
                        if (ch < 3) { mn = BinaryPrimitives.ReadSingleLittleEndian(p.Slice(ro + 4 * ch)); inv = 1f / BinaryPrimitives.ReadSingleLittleEndian(p.Slice(ro + 12 + 4 * ch)); }
                        else { mn = GMin[ch]; inv = (GMax[ch] - GMin[ch]) / 255f; }
                        var b = ch * _n + s;
                        if (sparse)
                        {
                            Dequantize(p.Slice(o, m), mn, inv, _scratch.AsSpan(0, m));
                            for (var i = 0; i < n; i++) rec[b + i] = _scratch[i >> 2];   // 희소 → 직전 표본 유지(정밀 모턴 인접)
                        }
                        else Dequantize(p.Slice(o, m), mn, inv, rec.AsSpan(b, n));
                    }
                    o += m;
                }
            }
        }
        // 위치 재구성 오차
        double sum = 0;
        var max = 0f;
        for (var i = 0; i < _n; i++)
        {
            var dx = rec[i] - _cur[i];
            var dy = rec[_n + i] - _cur[_n + i];
            var dz = rec[2 * _n + i] - _cur[2 * _n + i];
            var e2 = dx * dx + dy * dy + dz * dz;
            sum += e2;
            if (e2 > max) max = e2;
        }
        vw.RmsErrMm = (float)Math.Sqrt(sum / _n) * 1000f;
        vw.MaxErrMm = MathF.Sqrt(max) * 1000f;
        LastDecodeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>청크 c 의 수신 재구성 최대 위치 오차(m).</summary>
    public float ChunkMaxError(int v, int c)
    {
        var rec = _v[v].Recon;
        var max = 0f;
        for (var i = _cStart[c]; i < _cStart[c + 1]; i++)
            for (var a = 0; a < 3; a++) max = MathF.Max(max, MathF.Abs(rec[a * _n + i] - _cur[a * _n + i]));
        return max;
    }

    /// <summary>8bit 양자화 반 스텝 상한(m) — 청크 축 범위 / 255 / 2.</summary>
    public float ChunkQuantBound(int c)
    {
        var b = 0f;
        for (var a = 0; a < 3; a++)
        {
            MinMax(_cur.AsSpan(a * _n + _cStart[c], _cStart[c + 1] - _cStart[c]), out var mn, out var mx);
            b = MathF.Max(b, (mx - mn) / 255f * 0.5f);
        }
        return b;
    }

    private float ProxyValue(int c, int ch)
    {
        int s = _cStart[c], n = _cStart[c + 1] - s;
        if (ch >= 3 && ch < 6)
        {
            var a = ch - 3;
            return Math.Clamp(MathF.Log(MathF.Max((_cMax[3 * c + a] - _cMin[3 * c + a]) * 0.5f, 1e-3f)), GMin[ch], GMax[ch]);
        }
        if (ch >= 6 && ch < 10) return ch == 9 ? 1f : 0f;
        var sum = 0f;
        var src = _cur.AsSpan(ch * _n + s, n);
        for (var i = 0; i < n; i++) sum += src[i];
        return sum / n;
    }

    private void MinMax(ReadOnlySpan<float> src, out float mn, out float mx)
    {
        if (UseSimd) VolumetricKernels.MinMaxSimd(src, out mn, out mx);
        else VolumetricKernels.MinMaxScalar(src, out mn, out mx);
    }

    private void Dequantize(ReadOnlySpan<byte> src, float mn, float inv, Span<float> dst)
    {
        if (UseSimd) VolumetricKernels.DequantizeSimd(src, mn, inv, dst);
        else VolumetricKernels.DequantizeScalar(src, mn, inv, dst);
    }

    private void AddScalar(ReadOnlySpan<float> src, float add, Span<float> dst)
    {
        if (UseSimd) VolumetricKernels.AddScalarSimd(src, add, dst);
        else for (var i = 0; i < src.Length; i++) dst[i] = src[i] + add;
    }

    public static int Morton(int x, int y, int z) => (int)Morton(x, y, z, Depth);

    public static long Morton(int x, int y, int z, int bits)
    {
        var k = 0L;
        for (var b = 0; b < bits; b++) k |= ((long)((x >> b) & 1) << (3 * b)) | ((long)((y >> b) & 1) << (3 * b + 1)) | ((long)((z >> b) & 1) << (3 * b + 2));
        return k;
    }

    // 착석 아바타(정면 +z): 타원체 표면 스플랫 — 머리 · 머리카락 · 목 · 몸통 · 팔 · 손 · 허벅지 · 정강이
    private static void Generate(float[] raw, int n, int seed)
    {
        ReadOnlySpan<float> parts = stackalloc float[]
        {
            // cx, cy, cz, rx, ry, rz, r, g, b
            0f, 1.33f, 0.02f, 0.085f, 0.11f, 0.095f, 0.93f, 0.72f, 0.6f,
            0f, 1.39f, -0.01f, 0.095f, 0.08f, 0.1f, 0.18f, 0.12f, 0.08f,
            0f, 1.19f, 0f, 0.05f, 0.05f, 0.05f, 0.9f, 0.7f, 0.58f,
            0f, 0.95f, 0f, 0.17f, 0.24f, 0.11f, 0.2f, 0.35f, 0.65f,
            -0.21f, 0.98f, 0.06f, 0.05f, 0.2f, 0.05f, 0.2f, 0.35f, 0.65f,
            0.21f, 0.98f, 0.06f, 0.05f, 0.2f, 0.05f, 0.2f, 0.35f, 0.65f,
            -0.18f, 0.78f, 0.24f, 0.045f, 0.03f, 0.07f, 0.93f, 0.72f, 0.6f,
            0.18f, 0.78f, 0.24f, 0.045f, 0.03f, 0.07f, 0.93f, 0.72f, 0.6f,
            -0.1f, 0.62f, 0.2f, 0.075f, 0.07f, 0.22f, 0.15f, 0.2f, 0.35f,
            0.1f, 0.62f, 0.2f, 0.075f, 0.07f, 0.22f, 0.15f, 0.2f, 0.35f,
            -0.1f, 0.33f, 0.4f, 0.06f, 0.22f, 0.06f, 0.15f, 0.2f, 0.35f,
            0.1f, 0.33f, 0.4f, 0.06f, 0.22f, 0.06f, 0.15f, 0.2f, 0.35f,
        };
        const int pc = 12;
        Span<float> w = stackalloc float[pc];
        var tot = 0f;
        for (var k = 0; k < pc; k++)
        {
            var o = 9 * k;
            w[k] = parts[o + 3] * parts[o + 4] + parts[o + 4] * parts[o + 5] + parts[o + 3] * parts[o + 5];
            tot += w[k];
        }
        var rng = new Random(seed);
        float N01() => (float)(Math.Sqrt(-2.0 * Math.Log(1.0 - rng.NextDouble())) * Math.Cos(2.0 * Math.PI * rng.NextDouble()));
        var i = 0;
        for (var k = 0; k < pc && i < n; k++)
        {
            var cnt = k == pc - 1 ? n - i : (int)(n * w[k] / tot);
            var o = 9 * k;
            for (var q = 0; q < cnt && i < n; q++, i++)
            {
                var d = Vector3.Normalize(new Vector3(N01(), N01(), N01()) + new Vector3(1e-6f, 0f, 0f));
                var sh = 1f + 0.03f * N01();
                raw[i] = parts[o] + d.X * parts[o + 3] * sh;
                raw[n + i] = parts[o + 1] + d.Y * parts[o + 4] * sh;
                raw[2 * n + i] = parts[o + 2] + d.Z * parts[o + 5] * sh;
                for (var a = 0; a < 3; a++) raw[(3 + a) * n + i] = Math.Clamp(MathF.Log(0.006f) + 0.35f * N01(), -6.9f, -2.1f);
                var qv = Quaternion.Normalize(new Quaternion(N01(), N01(), N01(), N01() + 1e-6f));
                raw[6 * n + i] = qv.X; raw[7 * n + i] = qv.Y; raw[8 * n + i] = qv.Z; raw[9 * n + i] = qv.W;
                for (var a = 0; a < 3; a++) raw[(10 + a) * n + i] = Math.Clamp(parts[o + 6 + a] + 0.04f * N01(), 0f, 1f);
                raw[13 * n + i] = Math.Clamp(0.8f + 0.12f * N01(), 0.05f, 1f);
            }
        }
    }
}
