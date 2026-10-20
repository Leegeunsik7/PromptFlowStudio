using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Neural3d;

namespace PromptFlowStudio.Services.Holo;

/// <summary>
/// [PromptFlow Studio v16.77.0] 📡 실시간 볼류메트릭 홀로그램 스트리밍 &amp; 공간 압축 엔진.
/// 3DGS 스플랫(USD 바인딩 프림 포함) + 모캡 관절 + 메시 정점을 하나의 점 배열로 합쳐 SIMD AABB → SIMD 공간 양자화(8~16 bit/축) →
/// 키프레임 = 비트 패킹 위치 + 속성(RGB · 불투명도 · log2 스케일 · int8 쿼터니언) / 델타 = 양자화 단위 지그재그 varint →
/// CRC32 봉인 · MTU(1200 B) 분할 데이터그램. 수신부는 재조립 → CRC 검증 → 동일 양자 상태 재구성(참조 유실 시 키프레임 요청). 전 구간 무할당.
/// </summary>
public sealed class RealtimeVolumetricHologramStreamerEngine
{
    public const int MaxSplats = 4096, MaxJoints = 32, MaxMesh = 2048, MaxPoints = MaxSplats + MaxJoints + MaxMesh;
    public const int Mtu = 1200, FragHeader = 12, FrameHeader = 64, MaxFrameBytes = FrameHeader + MaxPoints * 18 + MaxSplats * 11 + 64;
    public const int MinBits = 8, MaxBits = 16, AttrBytes = 11, MaxFrags = 256;
    public const uint Magic = 0x56484650; // "PFHV"
    private const byte FlagKey = 1;

    public static int SimdLanes => Vector<float>.Count;

    private readonly object _gate = new();
    // 송신 상태
    private readonly float[] _pts = new float[MaxPoints * 3];
    private readonly int[] _q = new int[MaxPoints * 3], _refQ = new int[MaxPoints * 3];
    private readonly byte[] _frame = new byte[MaxFrameBytes];
    private int _refCount = -1, _refBits, _sinceKey;
    private Vector3 _refMin, _refMax;
    private uint _seq;
    private bool _forceKey = true;
    // 수신 상태
    private readonly byte[] _rx = new byte[MaxFrameBytes];
    private readonly int[] _rxQ = new int[MaxPoints * 3];
    private readonly float[] _rxPts = new float[MaxPoints * 3];
    private readonly byte[] _rxAttr = new byte[MaxSplats * AttrBytes];
    private uint _rxSeq = uint.MaxValue, _rxLastDecoded = uint.MaxValue;
    private int _rxFrags, _rxGot, _rxLen, _rxCount = -1, _rxSplats, _rxJoints, _rxMesh, _rxBits;
    private readonly ulong[] _rxMask = new ulong[MaxFrags / 64];
    private Vector3 _rxMin, _rxMax;
    private long _rxSentMicros;

    public int QuantBits { get; set; } = 12;
    public int KeyframeInterval { get; set; } = 30;

    // 텔레메트리
    public long FramesEncoded { get; private set; }
    public long FramesDecoded { get; private set; }
    public long FramesDropped { get; private set; }
    public long KeyframeRequests { get; private set; }
    public long CrcFailures { get; private set; }
    public int LastRawBytes { get; private set; }
    public int LastEncodedBytes { get; private set; }
    public int LastFragments { get; private set; }
    public bool LastWasKeyframe { get; private set; }
    public float CompressionRatio => LastEncodedBytes > 0 ? (float)LastRawBytes / LastEncodedBytes : 0f;
    public float EncodeMs { get; private set; }
    public float DecodeMs { get; private set; }
    public float MaxErrorMm { get; private set; }
    public float StepMm { get; private set; }
    public long LastSentMicros { get; private set; }
    public long LastDecodedSentMicros => _rxSentMicros;
    public bool NeedsKeyframe => _forceKey;

    // 수신 결과
    public int ReceivedSplats => _rxSplats;
    public int ReceivedJoints => _rxJoints;
    public int ReceivedMesh => _rxMesh;
    public uint ReceivedSeq => _rxLastDecoded;
    public ReadOnlySpan<Vector3> ReceivedPoints => MemoryMarshal.Cast<float, Vector3>(_rxPts.AsSpan(0, Math.Max(0, _rxCount) * 3));
    public ReadOnlySpan<Vector3> ReceivedJointPositions => ReceivedPoints.Slice(_rxSplats, _rxJoints);
    public ReadOnlySpan<Vector3> ReceivedMeshPositions => ReceivedPoints.Slice(_rxSplats + _rxJoints, _rxMesh);
    public ReadOnlySpan<byte> EncodedFrame => _frame.AsSpan(0, LastEncodedBytes);

    /// <summary>수신 스플랫 i 의 RGB(0..1).</summary>
    public Vector3 ReceivedColor(int i) => new(_rxAttr[i * AttrBytes] / 255f, _rxAttr[i * AttrBytes + 1] / 255f, _rxAttr[i * AttrBytes + 2] / 255f);
    public float ReceivedOpacity(int i) => _rxAttr[i * AttrBytes + 3] / 255f;
    public Vector3 ReceivedScale(int i) => new(DecodeScale(_rxAttr[i * AttrBytes + 4]), DecodeScale(_rxAttr[i * AttrBytes + 5]), DecodeScale(_rxAttr[i * AttrBytes + 6]));

    public void RequestKeyframe() { lock (_gate) { _forceKey = true; KeyframeRequests++; } }

    // ══════════════════════ 인코더 ══════════════════════

    /// <summary>
    /// 1프레임 인코딩(무할당) → <see cref="EncodedFrame"/>. splats = 3DGS(USD 바인딩 포함) · joints = 모캡 관절 · mesh = 메시 정점. 반환 = 바이트 수.
    /// </summary>
    public int EncodeFrame(GaussianScene splats, ReadOnlySpan<Vector3> joints, ReadOnlySpan<Vector3> mesh, long nowMicros)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Holo.Encode");
            var t0 = Stopwatch.GetTimestamp();
            var ns = Math.Min(splats.Count, MaxSplats);
            var nj = Math.Min(joints.Length, MaxJoints);
            var nm = Math.Min(mesh.Length, MaxMesh);
            var n = ns + nj + nm;
            var bits = Math.Clamp(QuantBits, MinBits, MaxBits);
            splats.Pos.AsSpan(0, ns * 3).CopyTo(_pts);
            MemoryMarshal.Cast<Vector3, float>(joints[..nj]).CopyTo(_pts.AsSpan(ns * 3));
            MemoryMarshal.Cast<Vector3, float>(mesh[..nm]).CopyTo(_pts.AsSpan((ns + nj) * 3));
            var pts = _pts.AsSpan(0, n * 3);

            SimdBounds(pts, out var mn, out var mx);
            var key = _forceKey || n != _refCount || bits != _refBits || ++_sinceKey >= KeyframeInterval
                      || mn.X < _refMin.X || mn.Y < _refMin.Y || mn.Z < _refMin.Z || mx.X > _refMax.X || mx.Y > _refMax.Y || mx.Z > _refMax.Z;
            if (key)
            {
                // 키프레임: 여유 5cm 를 둔 AABB 로 양자 격자 재설정(이후 델타 프레임은 격자 고정)
                _refMin = mn - new Vector3(0.05f); _refMax = mx + new Vector3(0.05f);
                _sinceKey = 0;
            }
            var maxQ = (1 << bits) - 1;
            var ext = Vector3.Max(_refMax - _refMin, new Vector3(1e-4f));
            var scale = new Vector3(maxQ) / ext;
            SimdQuantize(pts, _q.AsSpan(0, n * 3), _refMin, scale, maxQ);

            // 재구성 오차(최대 · mm) — 격자 반 칸 상한 검증용
            var step = ext / maxQ;
            StepMm = MathF.Max(step.X, MathF.Max(step.Y, step.Z)) * 1000f;
            float err = 0;
            for (var k = 0; k < n * 3; k++)
            {
                var c = k % 3;
                var r = (c == 0 ? _refMin.X : c == 1 ? _refMin.Y : _refMin.Z) + _q[k] * (c == 0 ? step.X : c == 1 ? step.Y : step.Z);
                err = MathF.Max(err, MathF.Abs(r - pts[k]));
            }
            MaxErrorMm = err * 1000f;

            var f = _frame.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(f, Magic);
            f[4] = 1; f[5] = key ? FlagKey : (byte)0; f[6] = (byte)bits; f[7] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(f[8..], ++_seq);
            BinaryPrimitives.WriteInt64LittleEndian(f[12..], nowMicros);
            BinaryPrimitives.WriteUInt16LittleEndian(f[20..], (ushort)ns);
            BinaryPrimitives.WriteUInt16LittleEndian(f[22..], (ushort)nj);
            BinaryPrimitives.WriteUInt16LittleEndian(f[24..], (ushort)nm);
            BinaryPrimitives.WriteSingleLittleEndian(f[28..], _refMin.X); BinaryPrimitives.WriteSingleLittleEndian(f[32..], _refMin.Y); BinaryPrimitives.WriteSingleLittleEndian(f[36..], _refMin.Z);
            BinaryPrimitives.WriteSingleLittleEndian(f[40..], _refMax.X); BinaryPrimitives.WriteSingleLittleEndian(f[44..], _refMax.Y); BinaryPrimitives.WriteSingleLittleEndian(f[48..], _refMax.Z);
            var o = FrameHeader;
            if (key)
            {
                o = WriteBits(f, o, _q.AsSpan(0, n * 3), bits);
                for (var i = 0; i < ns; i++)
                {
                    var c = splats.BaseColor(i);
                    f[o++] = ToByte(c.X); f[o++] = ToByte(c.Y); f[o++] = ToByte(c.Z);
                    f[o++] = ToByte(splats.Opacity[i]);
                    f[o++] = EncodeScale(splats.Scale[i * 3]); f[o++] = EncodeScale(splats.Scale[i * 3 + 1]); f[o++] = EncodeScale(splats.Scale[i * 3 + 2]);
                    f[o++] = ToSByte(splats.Rot[i * 4]); f[o++] = ToSByte(splats.Rot[i * 4 + 1]); f[o++] = ToSByte(splats.Rot[i * 4 + 2]); f[o++] = ToSByte(splats.Rot[i * 4 + 3]);
                }
            }
            else
            {
                for (var k = 0; k < n * 3; k++) o = WriteVarint(f, o, ZigZag(_q[k] - _refQ[k]));
            }
            BinaryPrimitives.WriteInt32LittleEndian(f[52..], o);
            BinaryPrimitives.WriteUInt32LittleEndian(f[56..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(f[56..], Crc32(f[..o]));

            _q.AsSpan(0, n * 3).CopyTo(_refQ);
            _refCount = n; _refBits = bits; _forceKey = false;
            LastRawBytes = n * 12 + ns * 44; // float32 xyz + (RGB · 불투명도 · 스케일 · 쿼터니언) float32
            LastEncodedBytes = o;
            LastWasKeyframe = key;
            LastFragments = (o + Mtu - FragHeader - 1) / (Mtu - FragHeader);
            LastSentMicros = nowMicros;
            FramesEncoded++;
            EncodeMs = (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return o;
        }
    }

    /// <summary>프래그먼트 i 를 dst 에 기록(헤더 12 B: magic16 · seq32 · idx16 · cnt16 · len16). 반환 = 데이터그램 길이.</summary>
    public int WriteFragment(int i, Span<byte> dst)
    {
        lock (_gate)
        {
            var payload = Mtu - FragHeader;
            var off = i * payload;
            var len = Math.Min(payload, LastEncodedBytes - off);
            if (len <= 0) return 0;
            BinaryPrimitives.WriteUInt16LittleEndian(dst, 0x4846);
            BinaryPrimitives.WriteUInt32LittleEndian(dst[2..], _seq);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], (ushort)LastFragments);
            BinaryPrimitives.WriteUInt16LittleEndian(dst[10..], (ushort)len);
            _frame.AsSpan(off, len).CopyTo(dst[FragHeader..]);
            return FragHeader + len;
        }
    }

    // ══════════════════════ 디코더 ══════════════════════

    /// <summary>데이터그램 수신(재조립 · 완성 시 디코드). 반환 = 프레임 완성·디코드 성공 여부.</summary>
    public bool ReceiveDatagram(ReadOnlySpan<byte> d)
    {
        lock (_gate)
        {
            if (d.Length < FragHeader || BinaryPrimitives.ReadUInt16LittleEndian(d) != 0x4846) return false;
            var seq = BinaryPrimitives.ReadUInt32LittleEndian(d[2..]);
            int idx = BinaryPrimitives.ReadUInt16LittleEndian(d[6..]), cnt = BinaryPrimitives.ReadUInt16LittleEndian(d[8..]), len = BinaryPrimitives.ReadUInt16LittleEndian(d[10..]);
            if (cnt == 0 || cnt > MaxFrags || idx >= cnt || len > d.Length - FragHeader || (long)idx * (Mtu - FragHeader) + len > _rx.Length) return false;
            if (seq != _rxSeq)
            {
                if (_rxSeq != uint.MaxValue && _rxGot < _rxFrags && _rxSeq != _rxLastDecoded) FramesDropped++;
                _rxSeq = seq; _rxFrags = cnt; _rxGot = 0; Array.Clear(_rxMask); _rxLen = 0;
            }
            if ((_rxMask[idx >> 6] & (1UL << idx)) != 0) return false;
            _rxMask[idx >> 6] |= 1UL << idx; _rxGot++;
            d.Slice(FragHeader, len).CopyTo(_rx.AsSpan(idx * (Mtu - FragHeader)));
            _rxLen = Math.Max(_rxLen, idx * (Mtu - FragHeader) + len);
            return _rxGot == _rxFrags && Decode(_rx.AsSpan(0, _rxLen));
        }
    }

    /// <summary>프레임 직접 디코드(무손실 경로 · 재조립 이후 호출).</summary>
    public bool Decode(ReadOnlySpan<byte> f)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Holo.Decode");
            var t0 = Stopwatch.GetTimestamp();
            if (f.Length < FrameHeader || BinaryPrimitives.ReadUInt32LittleEndian(f) != Magic) return false;
            var total = BinaryPrimitives.ReadInt32LittleEndian(f[52..]);
            if (total > f.Length || total < FrameHeader) return false;
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(f[56..]);
            // CRC 는 crc 필드 = 0 상태로 계산되었음 → 필드 4 B 를 0 으로 간주해 재계산
            if (Crc32Skip(f[..total], 56) != crc) { CrcFailures++; _forceRemoteKey = true; return false; }
            var key = (f[5] & FlagKey) != 0;
            var bits = f[6];
            var seq = BinaryPrimitives.ReadUInt32LittleEndian(f[8..]);
            int ns = BinaryPrimitives.ReadUInt16LittleEndian(f[20..]), nj = BinaryPrimitives.ReadUInt16LittleEndian(f[22..]), nm = BinaryPrimitives.ReadUInt16LittleEndian(f[24..]);
            var n = ns + nj + nm;
            if (bits < MinBits || bits > MaxBits || ns > MaxSplats || nj > MaxJoints || nm > MaxMesh) return false;
            if (!key && (_rxCount != n || _rxBits != bits || seq != _rxLastDecoded + 1)) { _forceRemoteKey = true; FramesDropped++; return false; }
            var mn = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(f[28..]), BinaryPrimitives.ReadSingleLittleEndian(f[32..]), BinaryPrimitives.ReadSingleLittleEndian(f[36..]));
            var mx = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(f[40..]), BinaryPrimitives.ReadSingleLittleEndian(f[44..]), BinaryPrimitives.ReadSingleLittleEndian(f[48..]));
            var o = FrameHeader;
            var q = _rxQ.AsSpan(0, n * 3);
            if (key)
            {
                o = ReadBits(f, o, q, bits);
                if (o + ns * AttrBytes > total) return false;
                f.Slice(o, ns * AttrBytes).CopyTo(_rxAttr);
                o += ns * AttrBytes;
            }
            else
            {
                for (var k = 0; k < q.Length; k++)
                {
                    if (!ReadVarint(f[..total], ref o, out var z)) return false;
                    q[k] += UnZigZag(z);
                }
            }
            var maxQ = (1 << bits) - 1;
            var step = Vector3.Max(mx - mn, new Vector3(1e-4f)) / maxQ;
            for (var i = 0; i < n; i++)
            {
                _rxPts[i * 3] = mn.X + q[i * 3] * step.X;
                _rxPts[i * 3 + 1] = mn.Y + q[i * 3 + 1] * step.Y;
                _rxPts[i * 3 + 2] = mn.Z + q[i * 3 + 2] * step.Z;
            }
            _rxCount = n; _rxSplats = ns; _rxJoints = nj; _rxMesh = nm; _rxBits = bits; _rxMin = mn; _rxMax = mx;
            _rxLastDecoded = seq;
            _rxSentMicros = BinaryPrimitives.ReadInt64LittleEndian(f[12..]);
            _forceRemoteKey = false;
            FramesDecoded++;
            DecodeMs = (float)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return true;
        }
    }

    private bool _forceRemoteKey;

    /// <summary>수신부가 참조 유실/CRC 실패로 키프레임을 요구하는지(PLI 피드백 · 송신부가 <see cref="RequestKeyframe"/> 로 반영).</summary>
    public bool ReceiverWantsKeyframe => _forceRemoteKey;

    /// <summary>수신 관절 복사(UI 스레드 안전 스냅샷). 반환 = 관절 수.</summary>
    public int CopyReceivedJoints(Span<Vector3> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _rxJoints);
            MemoryMarshal.Cast<float, Vector3>(_rxPts.AsSpan(_rxSplats * 3, n * 3)).CopyTo(dst);
            return n;
        }
    }

    /// <summary>수신 스플랫을 GaussianScene 으로 재구성(용량 충분 시 무할당).</summary>
    public int CopyReceivedSplats(GaussianScene dst)
    {
        lock (_gate)
        {
            dst.Count = 0;
            for (var i = 0; i < _rxSplats; i++)
            {
                var a = i * AttrBytes;
                var rot = new Quaternion((sbyte)_rxAttr[a + 8] / 127f, (sbyte)_rxAttr[a + 9] / 127f, (sbyte)_rxAttr[a + 10] / 127f, (sbyte)_rxAttr[a + 7] / 127f);
                if (rot.LengthSquared() < 1e-6f) rot = Quaternion.Identity;
                dst.Add(new Vector3(_rxPts[i * 3], _rxPts[i * 3 + 1], _rxPts[i * 3 + 2]), rot, ReceivedScale(i), ReceivedOpacity(i), ReceivedColor(i));
            }
            dst.Version++;
            return dst.Count;
        }
    }

    // ══════════════════════ SIMD 커널 ══════════════════════

    /// <summary>xyz 인터리브 AABB(블록 = 3·W 실수 → 벡터 k 의 레인 l 은 성분 (k·W + l) mod 3 로 고정).</summary>
    public static void SimdBounds(ReadOnlySpan<float> xyz, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue); max = new Vector3(float.MinValue);
        if (xyz.Length < 3) { min = max = Vector3.Zero; return; }
        var w = Vector<float>.Count;
        var blk = 3 * w;
        var i = 0;
        if (Vector.IsHardwareAccelerated && xyz.Length >= blk)
        {
            Vector<float> mn0 = new(float.MaxValue), mn1 = mn0, mn2 = mn0, mx0 = new(float.MinValue), mx1 = mx0, mx2 = mx0;
            for (; i + blk <= xyz.Length; i += blk)
            {
                var a = new Vector<float>(xyz.Slice(i, w)); var b = new Vector<float>(xyz.Slice(i + w, w)); var c = new Vector<float>(xyz.Slice(i + 2 * w, w));
                mn0 = Vector.Min(mn0, a); mx0 = Vector.Max(mx0, a);
                mn1 = Vector.Min(mn1, b); mx1 = Vector.Max(mx1, b);
                mn2 = Vector.Min(mn2, c); mx2 = Vector.Max(mx2, c);
            }
            for (var l = 0; l < w; l++)
            {
                Fold(ref min, ref max, l % 3, mn0[l], mx0[l]);
                Fold(ref min, ref max, (w + l) % 3, mn1[l], mx1[l]);
                Fold(ref min, ref max, (2 * w + l) % 3, mn2[l], mx2[l]);
            }
        }
        for (; i + 2 < xyz.Length; i += 3)
        {
            var p = new Vector3(xyz[i], xyz[i + 1], xyz[i + 2]);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
    }

    private static void Fold(ref Vector3 min, ref Vector3 max, int c, float lo, float hi)
    {
        if (c == 0) { min.X = MathF.Min(min.X, lo); max.X = MathF.Max(max.X, hi); }
        else if (c == 1) { min.Y = MathF.Min(min.Y, lo); max.Y = MathF.Max(max.Y, hi); }
        else { min.Z = MathF.Min(min.Z, lo); max.Z = MathF.Max(max.Z, hi); }
    }

    /// <summary>xyz → 정수 격자(반올림 · [0, maxQ] 클램프). 블록 패턴 벡터(오프셋/배율)로 SIMD.</summary>
    public static void SimdQuantize(ReadOnlySpan<float> xyz, Span<int> dst, Vector3 origin, Vector3 scale, int maxQ)
    {
        var w = Vector<float>.Count;
        var blk = 3 * w;
        var i = 0;
        if (Vector.IsHardwareAccelerated && xyz.Length >= blk)
        {
            Span<float> o0 = stackalloc float[w], s0 = stackalloc float[w];
            for (var l = 0; l < w; l++) { o0[l] = Comp(origin, l % 3); s0[l] = Comp(scale, l % 3); }
            var off0 = new Vector<float>(o0); var sc0 = new Vector<float>(s0);
            for (var l = 0; l < w; l++) { o0[l] = Comp(origin, (w + l) % 3); s0[l] = Comp(scale, (w + l) % 3); }
            var off1 = new Vector<float>(o0); var sc1 = new Vector<float>(s0);
            for (var l = 0; l < w; l++) { o0[l] = Comp(origin, (2 * w + l) % 3); s0[l] = Comp(scale, (2 * w + l) % 3); }
            var off2 = new Vector<float>(o0); var sc2 = new Vector<float>(s0);
            var half = new Vector<float>(0.5f); var zero = Vector<float>.Zero; var top = new Vector<float>(maxQ);
            for (; i + blk <= xyz.Length; i += blk)
            {
                Vector.ConvertToInt32(Vector.Min(top, Vector.Max(zero, (new Vector<float>(xyz.Slice(i, w)) - off0) * sc0 + half))).CopyTo(dst.Slice(i, w));
                Vector.ConvertToInt32(Vector.Min(top, Vector.Max(zero, (new Vector<float>(xyz.Slice(i + w, w)) - off1) * sc1 + half))).CopyTo(dst.Slice(i + w, w));
                Vector.ConvertToInt32(Vector.Min(top, Vector.Max(zero, (new Vector<float>(xyz.Slice(i + 2 * w, w)) - off2) * sc2 + half))).CopyTo(dst.Slice(i + 2 * w, w));
            }
        }
        for (; i < xyz.Length; i++)
        {
            var c = i % 3;
            dst[i] = (int)Math.Clamp((xyz[i] - Comp(origin, c)) * Comp(scale, c) + 0.5f, 0f, maxQ);
        }
    }

    private static float Comp(Vector3 v, int c) => c == 0 ? v.X : c == 1 ? v.Y : v.Z;

    // ══════════════════════ 비트/엔트로피 ══════════════════════

    private static int WriteBits(Span<byte> f, int o, ReadOnlySpan<int> q, int bits)
    {
        ulong acc = 0; var n = 0;
        for (var k = 0; k < q.Length; k++)
        {
            acc |= (ulong)(uint)q[k] << n; n += bits;
            while (n >= 8) { f[o++] = (byte)acc; acc >>= 8; n -= 8; }
        }
        if (n > 0) f[o++] = (byte)acc;
        return o;
    }

    private static int ReadBits(ReadOnlySpan<byte> f, int o, Span<int> q, int bits)
    {
        ulong acc = 0; var n = 0; var mask = (1UL << bits) - 1;
        for (var k = 0; k < q.Length; k++)
        {
            while (n < bits && o < f.Length) { acc |= (ulong)f[o++] << n; n += 8; }
            q[k] = (int)(acc & mask); acc >>= bits; n -= bits;
        }
        return o;
    }

    private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));
    private static int UnZigZag(uint z) => (int)(z >> 1) ^ -(int)(z & 1);

    private static int WriteVarint(Span<byte> f, int o, uint v)
    {
        while (v >= 0x80) { f[o++] = (byte)(v | 0x80); v >>= 7; }
        f[o++] = (byte)v;
        return o;
    }

    private static bool ReadVarint(ReadOnlySpan<byte> f, ref int o, out uint v)
    {
        v = 0;
        for (var s = 0; s < 35; s += 7)
        {
            if (o >= f.Length) return false;
            var b = f[o++];
            v |= (uint)(b & 0x7F) << s;
            if (b < 0x80) return true;
        }
        return false;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0f, 255f);
    private static byte ToSByte(float v) => (byte)(sbyte)Math.Clamp(MathF.Round(v * 127f), -127f, 127f);
    private static byte EncodeScale(float s) => (byte)Math.Clamp((MathF.Log2(MathF.Max(s, 1e-6f)) + 12f) / 16f * 255f + 0.5f, 0f, 255f);
    private static float DecodeScale(byte b) => MathF.Pow(2f, b / 255f * 16f - 12f);

    // ══════════════════════ CRC32(IEEE) ══════════════════════

    private static readonly uint[] CrcTable = BuildCrc();

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data) => Crc32Skip(data, -1);

    /// <summary>skipAt 위치의 4 B 를 0 으로 간주하는 CRC32.</summary>
    private static uint Crc32Skip(ReadOnlySpan<byte> data, int skipAt)
    {
        var c = 0xFFFFFFFFu;
        for (var i = 0; i < data.Length; i++)
        {
            var b = skipAt >= 0 && i >= skipAt && i < skipAt + 4 ? (byte)0 : data[i];
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }
        return ~c;
    }
}
