using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.DigitalTwin;
using PromptFlowStudio.Services.Xr;

namespace PromptFlowStudio.Services.Teleport;

/// <summary>동적 볼류메트릭 메시 프레임(사전 할당 · 재사용) — 월드 좌표(m · Y 상향 · 우수계) + 0xRRGGBB + 삼각형 인덱스(포인트 클라우드면 IndexCount=0).</summary>
public sealed class TeleportMeshFrame
{
    public const int MaxVertices = 4096, MaxIndices = 24576;
    public readonly float[] Positions = new float[MaxVertices * 3];
    public readonly uint[] Colors = new uint[MaxVertices];
    public readonly int[] Indices = new int[MaxIndices];
    public int VertexCount, IndexCount;
    public uint Seq;

    public void Clear() { VertexCount = 0; IndexCount = 0; }

    public void CopyTo(TeleportMeshFrame d)
    {
        Positions.AsSpan(0, VertexCount * 3).CopyTo(d.Positions);
        Colors.AsSpan(0, VertexCount).CopyTo(d.Colors);
        Indices.AsSpan(0, IndexCount).CopyTo(d.Indices);
        d.VertexCount = VertexCount; d.IndexCount = IndexCount; d.Seq = Seq;
    }
}

/// <summary>
/// 무할당 동적 메시 코덱(인코더/디코더 동일 상태 머신 · 인스턴스당 1 스트림).
/// ▸ 키프레임: 적응 AABB(입방 · 10% 여유) 16bit 양자화 · 정점 공간 델타 · 인덱스 델타 · RGB565.
/// ▸ 인터프레임: 복원 상태 기준 시간 델타(드리프트 0) · 무변화 정점 런 길이 스킵 · 색 변경 시에만 색 블록.
/// ▸ 노이즈 게이트: 데드존(NoiseThresholdMeters 이하 미세 떨림 → 0) · 스파이크 홀드(SpikeMeters 초과 단발 점프 최대 2프레임 보류).
/// 헤더 14B: "PFTM" · ver · flags(1=Key · 2=Color · 4=Topology) · vcount u16 · icount u16 · seq u32 (+ 키: 원점 xyz · step f32 16B).
/// </summary>
public sealed class TeleportMeshCodec
{
    public const int HeaderSize = 14, BoundsSize = 16;
    public const byte Version = 1, FlagKey = 1, FlagColor = 2, FlagTopology = 4;
    public static int MaxEncodedSize(int vertices, int indices) => HeaderSize + BoundsSize + vertices * 12 + indices * 5;
    public static readonly int MaxFrameBytes = MaxEncodedSize(TeleportMeshFrame.MaxVertices, TeleportMeshFrame.MaxIndices);

    private readonly ushort[] _q = new ushort[TeleportMeshFrame.MaxVertices * 3];
    private readonly ushort[] _prevQ = new ushort[TeleportMeshFrame.MaxVertices * 3];
    private readonly ushort[] _prevC = new ushort[TeleportMeshFrame.MaxVertices];
    private readonly int[] _prevIdx = new int[TeleportMeshFrame.MaxIndices];
    private readonly byte[] _hold = new byte[TeleportMeshFrame.MaxVertices];
    private float _ox, _oy, _oz, _step = 1f;
    private int _prevV = -1, _prevI, _sinceKey;
    private uint _prevSeq;

    public float NoiseThresholdMeters { get; set; }
    public float SpikeMeters { get; set; } = 0.35f;
    public int KeyInterval { get; set; } = 60;
    public bool ForceKey { get; set; }
    public bool LastKey { get; private set; }
    public int LastSuppressed { get; private set; }
    public int LastSpikes { get; private set; }
    public float Step => _step;

    public void Reset() { _prevV = -1; _sinceKey = 0; }

    public static ushort ToRgb565(uint rgb) => (ushort)(((rgb >> 8) & 0xF800) | ((rgb >> 5) & 0x07E0) | ((rgb >> 3) & 0x001F));
    public static uint FromRgb565(ushort c) { uint r = (uint)(c >> 11) & 31, g = (uint)(c >> 5) & 63, b = (uint)c & 31; return ((r * 255 / 31) << 16) | ((g * 255 / 63) << 8) | (b * 255 / 31); }

    private ushort Q(float v, float o) => (ushort)Math.Clamp((int)MathF.Round((v - o) / _step), 0, 65535);

    private bool InBounds(ReadOnlySpan<float> p)
    {
        var hi = _step * 65535f;
        for (var i = 0; i < p.Length; i += 3)
        {
            float x = p[i] - _ox, y = p[i + 1] - _oy, z = p[i + 2] - _oz;
            if (x < 0 || y < 0 || z < 0 || x > hi || y > hi || z > hi) return false;
        }
        return true;
    }

    private void FitBounds(ReadOnlySpan<float> p)
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        for (var i = 0; i < p.Length; i += 3)
        {
            minX = MathF.Min(minX, p[i]); maxX = MathF.Max(maxX, p[i]);
            minY = MathF.Min(minY, p[i + 1]); maxY = MathF.Max(maxY, p[i + 1]);
            minZ = MathF.Min(minZ, p[i + 2]); maxZ = MathF.Max(maxZ, p[i + 2]);
        }
        if (p.Length == 0) { minX = minY = minZ = 0; maxX = maxY = maxZ = 0; }
        var ext = MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ));
        var pad = ext * 0.1f + 0.05f;
        _ox = minX - pad; _oy = minY - pad; _oz = minZ - pad;
        _step = (ext + 2f * pad) / 65535f;
    }

    /// <summary>프레임 인코딩(무할당). 반환 = 기록 바이트(버퍼 부족 · 상한 초과 시 0).</summary>
    public int Encode(TeleportMeshFrame f, Span<byte> dst)
    {
        int n = f.VertexCount, m = f.IndexCount;
        if (n > TeleportMeshFrame.MaxVertices || m > TeleportMeshFrame.MaxIndices || dst.Length < MaxEncodedSize(n, m)) return 0;
        var pos = f.Positions.AsSpan(0, n * 3);
        var key = ForceKey || _prevV != n || _prevI != m || _sinceKey >= KeyInterval;
        if (!key && !f.Indices.AsSpan(0, m).SequenceEqual(_prevIdx.AsSpan(0, m))) key = true;
        if (!key && !InBounds(pos)) key = true;
        if (key) FitBounds(pos);

        var thr = (int)(NoiseThresholdMeters / _step);
        var spike = (int)MathF.Min(65535f, SpikeMeters / _step);
        int suppressed = 0, spikes = 0;
        for (var i = 0; i < n; i++)
        {
            var k = i * 3;
            ushort qx = Q(pos[k], _ox), qy = Q(pos[k + 1], _oy), qz = Q(pos[k + 2], _oz);
            if (key) { _q[k] = qx; _q[k + 1] = qy; _q[k + 2] = qz; _hold[i] = 0; continue; }
            int dx = qx - _prevQ[k], dy = qy - _prevQ[k + 1], dz = qz - _prevQ[k + 2];
            var mx = Math.Max(Math.Abs(dx), Math.Max(Math.Abs(dy), Math.Abs(dz)));
            if (mx > spike && _hold[i] < 2)
            {
                _hold[i]++; spikes++;
                _q[k] = _prevQ[k]; _q[k + 1] = _prevQ[k + 1]; _q[k + 2] = _prevQ[k + 2];
                continue;
            }
            _hold[i] = 0;
            if (Math.Abs(dx) <= thr) { if (dx != 0) suppressed++; qx = _prevQ[k]; }
            if (Math.Abs(dy) <= thr) { if (dy != 0) suppressed++; qy = _prevQ[k + 1]; }
            if (Math.Abs(dz) <= thr) { if (dz != 0) suppressed++; qz = _prevQ[k + 2]; }
            _q[k] = qx; _q[k + 1] = qy; _q[k + 2] = qz;
        }
        var color = key;
        if (!key) for (var i = 0; i < n; i++) if (ToRgb565(f.Colors[i]) != _prevC[i]) { color = true; break; }

        "PFTM"u8.CopyTo(dst);
        dst[4] = Version;
        dst[5] = (byte)((key ? FlagKey : 0) | (color ? FlagColor : 0) | (key && m > 0 ? FlagTopology : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(dst[6..], (ushort)n);
        BinaryPrimitives.WriteUInt16LittleEndian(dst[8..], (ushort)m);
        BinaryPrimitives.WriteUInt32LittleEndian(dst[10..], f.Seq);
        var p = HeaderSize;
        if (key)
        {
            BinaryPrimitives.WriteSingleLittleEndian(dst[p..], _ox);
            BinaryPrimitives.WriteSingleLittleEndian(dst[(p + 4)..], _oy);
            BinaryPrimitives.WriteSingleLittleEndian(dst[(p + 8)..], _oz);
            BinaryPrimitives.WriteSingleLittleEndian(dst[(p + 12)..], _step);
            p += BoundsSize;
            for (var k = 0; k < n * 3; k++) p += WriteVar(dst[p..], ZigZag(_q[k] - (k < 3 ? 32768 : _q[k - 3])));
            var prev = 0;
            for (var j = 0; j < m; j++) { p += WriteVar(dst[p..], ZigZag(f.Indices[j] - prev)); prev = f.Indices[j]; }
            f.Indices.AsSpan(0, m).CopyTo(_prevIdx);
        }
        else
        {
            var run = 0;
            for (var i = 0; i < n; i++)
            {
                var k = i * 3;
                if (_q[k] == _prevQ[k] && _q[k + 1] == _prevQ[k + 1] && _q[k + 2] == _prevQ[k + 2]) { run++; continue; }
                p += WriteVar(dst[p..], (uint)run);
                run = 0;
                p += WriteVar(dst[p..], ZigZag(_q[k] - _prevQ[k]));
                p += WriteVar(dst[p..], ZigZag(_q[k + 1] - _prevQ[k + 1]));
                p += WriteVar(dst[p..], ZigZag(_q[k + 2] - _prevQ[k + 2]));
            }
            if (run > 0) p += WriteVar(dst[p..], (uint)run);
        }
        if (color)
        {
            for (var i = 0; i < n; i++)
            {
                var c = ToRgb565(f.Colors[i]);
                BinaryPrimitives.WriteUInt16LittleEndian(dst[p..], c);
                p += 2;
                _prevC[i] = c;
            }
        }
        _q.AsSpan(0, n * 3).CopyTo(_prevQ);
        _prevV = n; _prevI = m; _prevSeq = f.Seq;
        _sinceKey = key ? 1 : _sinceKey + 1;
        ForceKey = false;
        LastKey = key; LastSuppressed = suppressed; LastSpikes = spikes;
        return p;
    }

    /// <summary>디코딩(무할당). 인터프레임은 직전 seq+1 · 동일 토폴로지만 수용 — 실패 시 false(키프레임 요청 필요).</summary>
    public bool Decode(ReadOnlySpan<byte> src, TeleportMeshFrame dst)
    {
        if (src.Length < HeaderSize || !src[..4].SequenceEqual("PFTM"u8) || src[4] != Version) return false;
        var flags = src[5];
        int n = BinaryPrimitives.ReadUInt16LittleEndian(src[6..]), m = BinaryPrimitives.ReadUInt16LittleEndian(src[8..]);
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(src[10..]);
        if (n > TeleportMeshFrame.MaxVertices || m > TeleportMeshFrame.MaxIndices) return false;
        var key = (flags & FlagKey) != 0;
        if (!key && (_prevV != n || _prevI != m || seq != _prevSeq + 1)) return false;
        var p = HeaderSize;
        if (key)
        {
            if (src.Length < p + BoundsSize) return false;
            _ox = BinaryPrimitives.ReadSingleLittleEndian(src[p..]);
            _oy = BinaryPrimitives.ReadSingleLittleEndian(src[(p + 4)..]);
            _oz = BinaryPrimitives.ReadSingleLittleEndian(src[(p + 8)..]);
            _step = BinaryPrimitives.ReadSingleLittleEndian(src[(p + 12)..]);
            p += BoundsSize;
            for (var k = 0; k < n * 3; k++)
            {
                if (!TryReadVar(src, ref p, out var z)) return false;
                _q[k] = (ushort)((k < 3 ? 32768 : _q[k - 3]) + UnZigZag(z));
            }
            var prev = 0;
            for (var j = 0; j < m; j++)
            {
                if (!TryReadVar(src, ref p, out var z)) return false;
                prev += UnZigZag(z);
                if ((uint)prev >= (uint)n) return false;
                _prevIdx[j] = prev;
            }
        }
        else
        {
            _prevQ.AsSpan(0, n * 3).CopyTo(_q);
            var i = 0;
            while (i < n)
            {
                if (!TryReadVar(src, ref p, out var skip)) return false;
                i += (int)skip;
                if (i >= n) break;
                var k = i * 3;
                for (var a = 0; a < 3; a++)
                {
                    if (!TryReadVar(src, ref p, out var z)) return false;
                    _q[k + a] = (ushort)(_prevQ[k + a] + UnZigZag(z));
                }
                i++;
            }
        }
        if ((flags & FlagColor) != 0)
        {
            if (p + n * 2 > src.Length) return false;
            for (var i = 0; i < n; i++) { _prevC[i] = BinaryPrimitives.ReadUInt16LittleEndian(src[p..]); p += 2; }
        }
        else if (key) return false;
        for (var i = 0; i < n; i++)
        {
            var k = i * 3;
            dst.Positions[k] = _ox + _q[k] * _step;
            dst.Positions[k + 1] = _oy + _q[k + 1] * _step;
            dst.Positions[k + 2] = _oz + _q[k + 2] * _step;
            dst.Colors[i] = FromRgb565(_prevC[i]);
        }
        _prevIdx.AsSpan(0, m).CopyTo(dst.Indices);
        dst.VertexCount = n; dst.IndexCount = m; dst.Seq = seq;
        _q.AsSpan(0, n * 3).CopyTo(_prevQ);
        _prevV = n; _prevI = m; _prevSeq = seq;
        return true;
    }

    private static uint ZigZag(int v) => (uint)((v << 1) ^ (v >> 31));
    private static int UnZigZag(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    private static int WriteVar(Span<byte> d, uint v)
    {
        var i = 0;
        while (v >= 0x80) { d[i++] = (byte)(v | 0x80); v >>= 7; }
        d[i++] = (byte)v;
        return i;
    }

    private static bool TryReadVar(ReadOnlySpan<byte> s, ref int p, out uint v)
    {
        v = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            if (p >= s.Length) return false;
            var b = s[p++];
            v |= (uint)(b & 0x7F) << shift;
            if (b < 0x80) return true;
        }
        return false;
    }
}

public readonly record struct TeleportPacketHeader(byte Flags, ushort Stream, uint Seq, ushort Index, ushort Count, uint Timestamp, ushort Length, int FrameBytes)
{
    public bool IsKey => (Flags & TeleportPacket.FlagKey) != 0;
    public bool IsParity => (Flags & TeleportPacket.FlagParity) != 0;
}

/// <summary>
/// 초저지연 패킷화 — MTU 1200 분할 · 프레임당 XOR 패리티 1조각(FEC: 조각 1개 손실 즉시 복원, 재전송 왕복 0).
/// 헤더 24B: "TP" · ver · flags(1=Key · 2=Parity · 4=Last) · stream u16 · seq u32 · index u16 · count u16 · ts µs u32(송신측 시계) · len u16 · frameBytes u32.
/// </summary>
public static class TeleportPacket
{
    public const int Mtu = 1200, HeaderSize = 24, MaxPayload = Mtu - HeaderSize, MaxFragments = 160;
    public const byte Version = 1, FlagKey = 1, FlagParity = 2, FlagLast = 4;

    public static int MaxPackets(int frameBytes) => (frameBytes + MaxPayload - 1) / MaxPayload + 1;

    private static void WriteHeader(Span<byte> d, byte flags, ushort stream, uint seq, int index, int count, uint ts, int len, int frameBytes)
    {
        d[0] = (byte)'T'; d[1] = (byte)'P'; d[2] = Version; d[3] = flags;
        BinaryPrimitives.WriteUInt16LittleEndian(d[4..], stream);
        BinaryPrimitives.WriteUInt32LittleEndian(d[6..], seq);
        BinaryPrimitives.WriteUInt16LittleEndian(d[10..], (ushort)index);
        BinaryPrimitives.WriteUInt16LittleEndian(d[12..], (ushort)count);
        BinaryPrimitives.WriteUInt32LittleEndian(d[14..], ts);
        BinaryPrimitives.WriteUInt16LittleEndian(d[18..], (ushort)len);
        BinaryPrimitives.WriteInt32LittleEndian(d[20..], frameBytes);
    }

    /// <summary>프레임 → 패킷(무할당). packets 는 Mtu 간격 슬롯 · lengths 에 패킷 길이. 반환 = 패킷 수(0 = 공간 부족/상한 초과).</summary>
    public static int Packetize(ReadOnlySpan<byte> frame, ushort stream, uint seq, bool key, uint ts, bool parity, Span<byte> packets, Span<int> lengths)
    {
        var frags = (frame.Length + MaxPayload - 1) / MaxPayload;
        if (frags == 0 || frags > MaxFragments) return 0;
        var total = frags + (parity ? 1 : 0);
        if (packets.Length < total * Mtu || lengths.Length < total) return 0;
        var par = parity ? packets.Slice(frags * Mtu + HeaderSize, MaxPayload) : Span<byte>.Empty;
        par.Clear();
        var baseFlags = key ? FlagKey : (byte)0;
        for (var f = 0; f < frags; f++)
        {
            var off = f * MaxPayload;
            var len = Math.Min(MaxPayload, frame.Length - off);
            var pk = packets.Slice(f * Mtu, Mtu);
            WriteHeader(pk, (byte)(baseFlags | (f == frags - 1 ? FlagLast : 0)), stream, seq, f, frags, ts, len, frame.Length);
            var body = frame.Slice(off, len);
            body.CopyTo(pk[HeaderSize..]);
            if (parity) Xor(par, body);
            lengths[f] = HeaderSize + len;
        }
        if (parity)
        {
            WriteHeader(packets.Slice(frags * Mtu, Mtu), (byte)(baseFlags | FlagParity), stream, seq, frags, frags, ts, MaxPayload, frame.Length);
            lengths[frags] = Mtu;
        }
        return total;
    }

    public static bool TryReadHeader(ReadOnlySpan<byte> p, out TeleportPacketHeader h)
    {
        h = default;
        if (p.Length < HeaderSize || p[0] != (byte)'T' || p[1] != (byte)'P' || p[2] != Version) return false;
        h = new TeleportPacketHeader(p[3], BinaryPrimitives.ReadUInt16LittleEndian(p[4..]), BinaryPrimitives.ReadUInt32LittleEndian(p[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[10..]), BinaryPrimitives.ReadUInt16LittleEndian(p[12..]), BinaryPrimitives.ReadUInt32LittleEndian(p[14..]),
            BinaryPrimitives.ReadUInt16LittleEndian(p[18..]), BinaryPrimitives.ReadInt32LittleEndian(p[20..]));
        return h.Count is > 0 and <= MaxFragments && h.Index <= h.Count && h.Length <= MaxPayload && p.Length >= HeaderSize + h.Length
            && h.FrameBytes > 0 && h.FrameBytes <= h.Count * MaxPayload;
    }

    public static void Xor(Span<byte> acc, ReadOnlySpan<byte> src)
    {
        for (var i = 0; i < src.Length; i++) acc[i] ^= src[i];
    }
}

public enum AssembleResult : byte { Pending, Complete, Recovered, Stale, Invalid }

/// <summary>스트림별 프레임 재조립기(무할당) — 순서 무관 조각 수집 · XOR 패리티로 1조각 손실 복원 · 새 seq 도착 시 미완 프레임 폐기(Lost++).</summary>
public sealed class TeleportFrameAssembler
{
    private readonly byte[] _buf = new byte[TeleportPacket.MaxFragments * TeleportPacket.MaxPayload];
    private readonly byte[] _parity = new byte[TeleportPacket.MaxPayload];
    private readonly bool[] _have = new bool[TeleportPacket.MaxFragments];
    private bool _active, _done, _hasParity, _any;
    private int _count, _got, _frameBytes;

    public uint Seq { get; private set; }
    public uint Timestamp { get; private set; }
    public bool IsKey { get; private set; }
    public long Completed, Recovered, Lost, StalePackets;
    public ReadOnlySpan<byte> Frame => _buf.AsSpan(0, _frameBytes);

    private void Begin(in TeleportPacketHeader h)
    {
        if (_active && !_done) Lost++;
        _active = true; _done = false; _hasParity = false; _any = true;
        Seq = h.Seq; Timestamp = h.Timestamp; IsKey = h.IsKey;
        _count = h.Count; _got = 0; _frameBytes = h.FrameBytes;
        Array.Clear(_have, 0, _count);
    }

    private int FragLen(int i) => i == _count - 1 ? _frameBytes - i * TeleportPacket.MaxPayload : TeleportPacket.MaxPayload;

    public AssembleResult Accept(ReadOnlySpan<byte> packet)
    {
        if (!TeleportPacket.TryReadHeader(packet, out var h)) return AssembleResult.Invalid;
        if (!_any || (int)(h.Seq - Seq) > 0) Begin(h);
        else if (h.Seq != Seq || _done) { StalePackets++; return AssembleResult.Stale; }
        if (h.Count != _count || h.FrameBytes != _frameBytes) return AssembleResult.Invalid;
        var body = packet.Slice(TeleportPacket.HeaderSize, h.Length);
        if (h.IsParity) { body.CopyTo(_parity); _hasParity = true; }
        else if (h.Index < _count && !_have[h.Index])
        {
            if (h.Length != FragLen(h.Index)) return AssembleResult.Invalid;
            body.CopyTo(_buf.AsSpan(h.Index * TeleportPacket.MaxPayload));
            _have[h.Index] = true;
            _got++;
        }
        if (_got == _count) { _done = true; Completed++; return AssembleResult.Complete; }
        if (_got == _count - 1 && _hasParity)
        {
            var miss = 0;
            while (_have[miss]) miss++;
            var acc = _parity.AsSpan();
            for (var i = 0; i < _count; i++)
                if (i != miss) TeleportPacket.Xor(acc, _buf.AsSpan(i * TeleportPacket.MaxPayload, FragLen(i)));
            acc[..FragLen(miss)].CopyTo(_buf.AsSpan(miss * TeleportPacket.MaxPayload));
            _have[miss] = true; _got++; _done = true; Recovered++;
            return AssembleResult.Recovered;
        }
        return AssembleResult.Pending;
    }
}

/// <summary>
/// [PromptFlow Studio v16.55.0] 📡 실시간 AI 볼류메트릭 텔레포테이션 메시 인코더 &amp; 스트리머(Pure BCL · 무할당 핫패스).
///   ▸ 소스: v16.36.0 디지털 트윈 메시(정점 · 인덱스 · 피부색 → 호흡/고개 애니메이션 동적 메시) · v16.40.0 포인트 클라우드 · 내장 절차 인체 메시 폴백.
///   ▸ 압축: <see cref="TeleportMeshCodec"/> 적응 AABB 16bit 양자화 · 시간 델타 · 런 스킵 · 노이즈 게이트(슬라이더 감도) · 스파이크 홀드.
///   ▸ 패킷: <see cref="TeleportPacket"/> MTU 분할 + XOR 패리티 FEC → <see cref="HolographicTelepresenceRelayBridge"/> 송수신.
/// </summary>
public sealed class AiVolumetricTeleportationMeshEncoder
{
    public const float MaxNoiseThresholdMeters = 0.006f;
    private const int BodyRows = 20, BodyCols = 16;

    private readonly AiDigitalTwinGeneratorEngine? _twins;
    private float _sensitivity = 0.35f;
    private long _frames, _keyFrames, _rawBytes, _encodedBytes, _packets, _suppressed, _spikes;
    private double _encodeMicros;
    private int _lastBytes, _lastVertices, _lastTriangles;

    public AiVolumetricTeleportationMeshEncoder(IServiceProvider sp)
        : this(sp.GetService(typeof(AiDigitalTwinGeneratorEngine)) as AiDigitalTwinGeneratorEngine) { }

    public AiVolumetricTeleportationMeshEncoder(AiDigitalTwinGeneratorEngine? twins = null) => _twins = twins;

    public event Action? OnChange;

    /// <summary>홀로그램 메시 노이즈 감도(0 = 무손실 근사 · 1 = 데드존 6mm).</summary>
    public float NoiseSensitivity
    {
        get => Volatile.Read(ref _sensitivity);
        set { Volatile.Write(ref _sensitivity, Math.Clamp(value, 0f, 1f)); LastAction = "🎚 노이즈 감도 " + value.ToString("0.00", CultureInfo.InvariantCulture); Raise(); }
    }

    public float NoiseThresholdMeters => NoiseSensitivity * MaxNoiseThresholdMeters;
    public int KeyInterval { get; set; } = 60;
    public long Frames => Interlocked.Read(ref _frames);
    public long KeyFrames => Interlocked.Read(ref _keyFrames);
    public long Packets => Interlocked.Read(ref _packets);
    public long EncodedBytes => Interlocked.Read(ref _encodedBytes);
    public long SuppressedJitter => Interlocked.Read(ref _suppressed);
    public long SpikesHeld => Interlocked.Read(ref _spikes);
    public double LastEncodeMicros => Volatile.Read(ref _encodeMicros);
    public int LastBytes => Volatile.Read(ref _lastBytes);
    public int LastVertices => Volatile.Read(ref _lastVertices);
    public int LastTriangles => Volatile.Read(ref _lastTriangles);
    public double CompressionRatio { get { var e = Interlocked.Read(ref _encodedBytes); return e == 0 ? 0 : Interlocked.Read(ref _rawBytes) / (double)e; } }
    public bool HasTwin => _twins?.Current?.Mesh is not null;
    public string? LastAction { get; private set; }

    /// <summary>원시 크기(float xyz + RGBA + int 인덱스) — 압축률 분모.</summary>
    public static int RawSize(TeleportMeshFrame f) => f.VertexCount * 16 + f.IndexCount * 4;

    /// <summary>프레임 인코딩 + 통계(무할당). extraThresholdMeters = 릴레이 적응 레이트 가산분.</summary>
    public int Encode(TeleportMeshCodec codec, TeleportMeshFrame f, Span<byte> dst, float extraThresholdMeters = 0)
    {
        codec.NoiseThresholdMeters = NoiseThresholdMeters + extraThresholdMeters;
        codec.KeyInterval = KeyInterval;
        var t0 = Stopwatch.GetTimestamp();
        var n = codec.Encode(f, dst);
        if (n == 0) return 0;
        Volatile.Write(ref _encodeMicros, (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency);
        Interlocked.Increment(ref _frames);
        if (codec.LastKey) Interlocked.Increment(ref _keyFrames);
        Interlocked.Add(ref _rawBytes, RawSize(f));
        Interlocked.Add(ref _encodedBytes, n);
        Interlocked.Add(ref _suppressed, codec.LastSuppressed);
        Interlocked.Add(ref _spikes, codec.LastSpikes);
        Volatile.Write(ref _lastBytes, n);
        Volatile.Write(ref _lastVertices, f.VertexCount);
        Volatile.Write(ref _lastTriangles, f.IndexCount / 3);
        return n;
    }

    /// <summary>인코딩 → 패킷화(무할당). scratch ≥ MaxEncodedSize · packets ≥ MaxPackets×Mtu. 반환 = 패킷 수.</summary>
    public int EncodeAndPacketize(TeleportMeshCodec codec, TeleportMeshFrame f, ushort stream, uint tsMicros, Span<byte> scratch, Span<byte> packets, Span<int> lengths, bool parity = true, float extraThresholdMeters = 0)
    {
        var n = Encode(codec, f, scratch, extraThresholdMeters);
        if (n == 0) return 0;
        var c = TeleportPacket.Packetize(scratch[..n], stream, f.Seq, codec.LastKey, tsMicros, parity, packets, lengths);
        Interlocked.Add(ref _packets, c);
        return c;
    }

    /// <summary>라이브 캡처(무할당) — 디지털 트윈이 있으면 트윈 메시, 없으면 절차 인체 메시.</summary>
    public void CaptureLive(double tSec, float phase, TeleportMeshFrame dst, float sensorNoise = 0)
    {
        var twin = _twins?.Current;
        if (twin?.Mesh is { } mesh && mesh.VertexCount <= TeleportMeshFrame.MaxVertices && mesh.Indices.Length <= TeleportMeshFrame.MaxIndices)
            CaptureFromTwin(mesh, ParseSkin(twin.SkinHex), tSec, phase, dst, sensorNoise);
        else CaptureProcedural(tSec, phase, dst, sensorNoise);
    }

    private static uint ParseSkin(string? hex)
    {
        var s = (hex ?? "").AsSpan().TrimStart('#');
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v & 0xFFFFFF : 0xE0B89Au;
    }

    /// <summary>결정적 센서 노이즈(-1..1) — 해시 기반 무할당.</summary>
    private static float Noise(int i, uint seq)
    {
        var h = (uint)i * 0x9E3779B1u ^ seq * 0x85EBCA77u;
        h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
        return (h & 0xFFFF) / 32767.5f - 1f;
    }

    /// <summary>v16.36.0 트윈 메시 → 동적 메시(고개 요/피치 · 호흡 · 피부 셰이딩 · 선택적 센서 노이즈).</summary>
    public static void CaptureFromTwin(TwinMesh mesh, uint skin, double tSec, float phase, TeleportMeshFrame dst, float sensorNoise = 0)
    {
        var v = mesh.Vertices;
        var n = Math.Min(v.Length / 3, TeleportMeshFrame.MaxVertices);
        var t = (float)tSec;
        float yaw = 0.3f * MathF.Sin(t * 0.9f + phase), pitch = 0.08f * MathF.Sin(t * 1.3f + phase * 0.7f), breath = 1f + 0.012f * MathF.Sin(t * 1.6f + phase);
        float cy = MathF.Cos(yaw), sy = MathF.Sin(yaw), cp = MathF.Cos(pitch), sp = MathF.Sin(pitch);
        uint sr = (skin >> 16) & 255, sg = (skin >> 8) & 255, sb = skin & 255;
        var pos = dst.Positions;
        for (var i = 0; i < n; i++)
        {
            float x = v[i * 3] * breath, y = v[i * 3 + 1] * 0.6f * breath, z = v[i * 3 + 2] * breath;
            float y2 = y * cp - z * sp, z2 = y * sp + z * cp;
            float x3 = x * cy + z2 * sy, z3 = -x * sy + z2 * cy;
            pos[i * 3] = x3 + (sensorNoise > 0 ? Noise(i * 3, dst.Seq) * sensorNoise : 0);
            pos[i * 3 + 1] = 1.55f + y2 + (sensorNoise > 0 ? Noise(i * 3 + 1, dst.Seq) * sensorNoise : 0);
            pos[i * 3 + 2] = z3 + (sensorNoise > 0 ? Noise(i * 3 + 2, dst.Seq) * sensorNoise : 0);
            var shade = Math.Clamp(0.55f + v[i * 3 + 2] * 0.9f, 0.3f, 1f);
            dst.Colors[i] = ((uint)(sr * shade) << 16) | ((uint)(sg * shade) << 8) | (uint)(sb * shade);
        }
        var m = Math.Min(mesh.Indices.Length, TeleportMeshFrame.MaxIndices);
        var idx = mesh.Indices.AsSpan(0, m);
        idx.CopyTo(dst.Indices);
        for (var j = 0; j < m; j++) if ((uint)idx[j] >= (uint)n) { m = 0; break; }
        dst.VertexCount = n; dst.IndexCount = m;
    }

    /// <summary>절차 인체 메시(20×16 회전체 · 머리/몸통 프로파일 · 스웨이/호흡) — 트윈 미생성 폴백.</summary>
    public static void CaptureProcedural(double tSec, float phase, TeleportMeshFrame dst, float sensorNoise = 0)
    {
        var t = (float)tSec;
        float sway = 0.06f * MathF.Sin(t * 0.8f + phase), breath = 1f + 0.02f * MathF.Sin(t * 1.7f + phase), turn = 0.25f * MathF.Sin(t * 0.5f + phase);
        var pos = dst.Positions;
        var vi = 0;
        for (var r = 0; r < BodyRows; r++)
        {
            var h = r / (float)(BodyRows - 1);
            var y = 0.05f + h * 1.7f;
            var rad = h > 0.86f ? 0.11f * MathF.Sqrt(MathF.Max(0f, 1f - MathF.Pow((h - 0.93f) / 0.07f, 2))) + 0.01f
                : h > 0.8f ? 0.06f
                : h > 0.45f ? 0.17f * breath
                : 0.12f;
            var lean = sway * h;
            for (var c = 0; c < BodyCols; c++)
            {
                var a = c / (float)BodyCols * MathF.Tau + turn;
                var k = vi * 3;
                pos[k] = MathF.Cos(a) * rad + lean + (sensorNoise > 0 ? Noise(k, dst.Seq) * sensorNoise : 0);
                pos[k + 1] = y + (sensorNoise > 0 ? Noise(k + 1, dst.Seq) * sensorNoise : 0);
                pos[k + 2] = MathF.Sin(a) * rad * 0.7f + (sensorNoise > 0 ? Noise(k + 2, dst.Seq) * sensorNoise : 0);
                var lum = 0.55f + 0.45f * MathF.Max(0f, MathF.Sin(a));
                dst.Colors[vi] = ((uint)(70 * lum) << 16) | ((uint)(200 * lum) << 8) | (uint)(255 * lum);
                vi++;
            }
        }
        var ii = 0;
        for (var r = 0; r + 1 < BodyRows; r++)
        {
            for (var c = 0; c < BodyCols; c++)
            {
                int a = r * BodyCols + c, b = r * BodyCols + (c + 1) % BodyCols, d = a + BodyCols, e = b + BodyCols;
                dst.Indices[ii++] = a; dst.Indices[ii++] = d; dst.Indices[ii++] = b;
                dst.Indices[ii++] = b; dst.Indices[ii++] = d; dst.Indices[ii++] = e;
            }
        }
        dst.VertexCount = vi; dst.IndexCount = ii;
    }

    /// <summary>v16.40.0 포인트 클라우드 → 메시 프레임(인덱스 0 · 상한 절단).</summary>
    public static void CaptureFromPointCloud(PointCloudFrame src, TeleportMeshFrame dst)
    {
        var n = Math.Min(src.Count, TeleportMeshFrame.MaxVertices);
        src.Xyz.AsSpan(0, n * 3).CopyTo(dst.Positions);
        src.Rgb.AsSpan(0, n).CopyTo(dst.Colors);
        dst.VertexCount = n; dst.IndexCount = 0;
    }

    public void ResetStats()
    {
        Interlocked.Exchange(ref _frames, 0); Interlocked.Exchange(ref _keyFrames, 0); Interlocked.Exchange(ref _rawBytes, 0);
        Interlocked.Exchange(ref _encodedBytes, 0); Interlocked.Exchange(ref _packets, 0); Interlocked.Exchange(ref _suppressed, 0); Interlocked.Exchange(ref _spikes, 0);
        LastAction = "♻ 인코더 통계 초기화";
        Raise();
    }

    internal void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Teleport.Encoder.OnChange", ex); }
    }
}
