using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace PromptFlowStudio.Services.HoloTelepresence;

/// <summary>[PromptFlow Studio v17.00.0] 라이트필드 SIMD 커널(Vector&lt;float&gt; · 스칼라 꼬리 마감 · 스칼라 경로와 비트 동일).</summary>
public static class HoloSimd
{
    /// <summary>원근 계수: inv = D/(D − min(z, D − 0.05)) → a = x·inv · b = 1 − inv (시점 독립 · 프레임당 1회).</summary>
    public static void Perspective(ReadOnlySpan<float> x, ReadOnlySpan<float> z, float d, Span<float> a, Span<float> b)
    {
        var n = a.Length; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var m = n - n % Vector<float>.Count;
            Vector<float> vd = new(d), lim = new(d - 0.05f), one = Vector<float>.One;
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x[..m]); var vz = MemoryMarshal.Cast<float, Vector<float>>(z[..m]);
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]);
            for (var k = 0; k < va.Length; k++) { var inv = vd / (vd - Vector.Min(vz[k], lim)); va[k] = vx[k] * inv; vb[k] = one - inv; }
            i = m;
        }
        for (; i < n; i++) { var inv = d / (d - MathF.Min(z[i], d - 0.05f)); a[i] = x[i] * inv; b[i] = 1f - inv; }
    }

    public static void PerspectiveScalar(ReadOnlySpan<float> x, ReadOnlySpan<float> z, float d, Span<float> a, Span<float> b)
    {
        for (var i = 0; i < a.Length; i++) { var inv = d / (d - MathF.Min(z[i], d - 0.05f)); a[i] = x[i] * inv; b[i] = 1f - inv; }
    }

    /// <summary>시점별 전단(off-axis) 투영: dst = (a + ox·b)·scale + offset — 초점면(z = 0)은 모든 시점에서 고정.</summary>
    public static void ShearProject(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> b, float ox, float scale, float offset)
    {
        var n = dst.Length; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var m = n - n % Vector<float>.Count;
            Vector<float> vo = new(ox), vs = new(scale), vf = new(offset);
            var vd = MemoryMarshal.Cast<float, Vector<float>>(dst[..m]);
            var va = MemoryMarshal.Cast<float, Vector<float>>(a[..m]); var vb = MemoryMarshal.Cast<float, Vector<float>>(b[..m]);
            for (var k = 0; k < vd.Length; k++) vd[k] = (va[k] + vo * vb[k]) * vs + vf;
            i = m;
        }
        for (; i < n; i++) dst[i] = (a[i] + ox * b[i]) * scale + offset;
    }

    public static void ShearProjectScalar(Span<float> dst, ReadOnlySpan<float> a, ReadOnlySpan<float> b, float ox, float scale, float offset)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] = (a[i] + ox * b[i]) * scale + offset;
    }

    /// <summary>6DoF 강체(+균일 축척) 일괄 변환 — System.Numerics 행 벡터 규약(v·M).</summary>
    public static void Transform(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, Span<float> ox, Span<float> oy, Span<float> oz, in Matrix4x4 m)
    {
        var n = ox.Length; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var c = n - n % Vector<float>.Count;
            Vector<float> m11 = new(m.M11), m12 = new(m.M12), m13 = new(m.M13), m21 = new(m.M21), m22 = new(m.M22), m23 = new(m.M23);
            Vector<float> m31 = new(m.M31), m32 = new(m.M32), m33 = new(m.M33), m41 = new(m.M41), m42 = new(m.M42), m43 = new(m.M43);
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x[..c]); var vy = MemoryMarshal.Cast<float, Vector<float>>(y[..c]); var vz = MemoryMarshal.Cast<float, Vector<float>>(z[..c]);
            var rx = MemoryMarshal.Cast<float, Vector<float>>(ox[..c]); var ry = MemoryMarshal.Cast<float, Vector<float>>(oy[..c]); var rz = MemoryMarshal.Cast<float, Vector<float>>(oz[..c]);
            for (var k = 0; k < rx.Length; k++)
            {
                var px = vx[k]; var py = vy[k]; var pz = vz[k];
                rx[k] = px * m11 + py * m21 + pz * m31 + m41;
                ry[k] = px * m12 + py * m22 + pz * m32 + m42;
                rz[k] = px * m13 + py * m23 + pz * m33 + m43;
            }
            i = c;
        }
        for (; i < n; i++)
        {
            var px = x[i]; var py = y[i]; var pz = z[i];
            ox[i] = px * m.M11 + py * m.M21 + pz * m.M31 + m.M41;
            oy[i] = px * m.M12 + py * m.M22 + pz * m.M32 + m.M42;
            oz[i] = px * m.M13 + py * m.M23 + pz * m.M33 + m.M43;
        }
    }

    public static void TransformScalar(ReadOnlySpan<float> x, ReadOnlySpan<float> y, ReadOnlySpan<float> z, Span<float> ox, Span<float> oy, Span<float> oz, in Matrix4x4 m)
    {
        for (var i = 0; i < ox.Length; i++)
        {
            var px = x[i]; var py = y[i]; var pz = z[i];
            ox[i] = px * m.M11 + py * m.M21 + pz * m.M31 + m.M41;
            oy[i] = px * m.M12 + py * m.M22 + pz * m.M32 + m.M42;
            oz[i] = px * m.M13 + py * m.M23 + pz * m.M33 + m.M43;
        }
    }

    /// <summary>햅틱 파형 포화(±lim) — 액추에이터 과구동 차단.</summary>
    public static void Clamp(Span<float> x, float lim)
    {
        var n = x.Length; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var m = n - n % Vector<float>.Count;
            Vector<float> hi = new(lim), lo = new(-lim);
            var v = MemoryMarshal.Cast<float, Vector<float>>(x[..m]);
            for (var k = 0; k < v.Length; k++) v[k] = Vector.Min(Vector.Max(v[k], lo), hi);
            i = m;
        }
        for (; i < n; i++) x[i] = MathF.Min(MathF.Max(x[i], -lim), lim);
    }
}

/// <summary>
/// [PromptFlow Studio v17.00.0] 🔮 실시간 광시야각 홀로그래픽 라이트필드 &amp; 볼류메트릭 텔레프레전스 엔진(무할당 · 결정적).
///   ▸ 송신(캡처 사이트): 볼류메트릭/3DGS 스플랫 SoA(최대 4096 · 캡처 볼륨 로컬 좌표 + ARGB) → 16비트 양자화(0.04 mm) → 키 프레임(10 B/스플랫) ·
///     델타 프레임(이동 스플랫만 8 B · 적응 데드밴드) · 6DoF 자세 헤더 → 대역폭 예산(Mbps) 초과 시 데드밴드 배증 + 하드 캡(나머지는 다음 프레임 이월).
///   ▸ 수신(디스플레이 사이트): 패킷 길이/매직 검증 → 역양자화 → 6DoF 자세 + 홀로그램 축척 Vector&lt;float&gt; 일괄 강체 변환.
///   ▸ 라이트필드 슬라이싱: 45 시점(수평 시차 · 시야각 40°) 퀼트(9×5 · 타일 64×48) — 원근 계수는 프레임당 1회 SIMD, 시점별 전단 투영 SIMD → 깊이 버퍼 래스터.
///     초점면(z = 0) 스플랫은 전 시점 고정, 앞/뒤 스플랫은 반대 방향 시차.
///   ▸ 계측: 단계별 µs · 패킷 B · EWMA Mbps · 재구성 최대 오차(mm) · 시점별 커버리지.
/// </summary>
public sealed class HolographicLightFieldTelepresenceEngine
{
    public const int MaxSplats = 4096, Views = 45, TileW = 64, TileH = 48, QuiltCols = 9, QuiltRows = 5;
    public const int QuiltW = TileW * QuiltCols, QuiltH = TileH * QuiltRows;
    public const int HeaderBytes = 40, KeyRecord = 10, DeltaRecord = 8, MaxPacket = HeaderBytes + MaxSplats * KeyRecord;
    public const uint Magic = 0x3146_4C48; // "HLF1"
    public const float ViewConeDeg = 40f, ViewerDistance = 2f, HalfWidth = 0.6f, HalfHeight = 0.45f;
    public static readonly Vector3 BoundsLo = new(-1.2f, 0f, -1.2f), BoundsHi = new(1.2f, 2.4f, 1.2f);
    private static readonly int Pad = (MaxSplats + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
    private static readonly double TickUs = 1e6 / Stopwatch.Frequency;
    private static readonly float[] ViewOx = BuildViewOffsets();

    private readonly object _gate = new();
    // 송신 원본(로컬) · 송신측 기준 양자화(수신측 복제본 추정)
    private readonly float[] _sx = new float[MaxSplats], _sy = new float[MaxSplats], _sz = new float[MaxSplats];
    private readonly uint[] _sc = new uint[MaxSplats];
    private readonly ushort[] _refX = new ushort[MaxSplats], _refY = new ushort[MaxSplats], _refZ = new ushort[MaxSplats];
    // 수신 복원(로컬) · 월드 · 투영 스크래치
    private readonly float[] _lx = new float[Pad], _ly = new float[Pad], _lz = new float[Pad];
    private readonly uint[] _rc = new uint[MaxSplats];
    private readonly float[] _wx = new float[Pad], _wy = new float[Pad], _wz = new float[Pad];
    private readonly float[] _a = new float[Pad], _b = new float[Pad], _ay = new float[Pad], _px = new float[Pad], _py = new float[Pad];
    private readonly uint[] _quilt = new uint[QuiltW * QuiltH];
    private readonly float[] _depth = new float[QuiltW * QuiltH];
    private readonly int[] _coverage = new int[Views];
    private readonly byte[] _packet = new byte[MaxPacket];
    private int _count, _rxCount, _packetLen, _sinceKey;
    private bool _rxValid, _countChanged = true;
    private uint _seq;
    private Quaternion _q = Quaternion.Identity, _rxQ = Quaternion.Identity;
    private Vector3 _t, _rxT;

    public HolographicLightFieldTelepresenceEngine() => _depth.AsSpan().Fill(float.NegativeInfinity);

    // ── 설정 ──
    public float BudgetMbps { get; set; } = 40f;
    public float StreamFps { get; set; } = 60f;
    public int KeyInterval { get; set; } = 120;
    public int MinDeadbandQ { get; set; } = 2;
    public bool ForceKey { get; set; }
    public float HologramScale { get; set; } = 0.45f;

    // ── 계측 ──
    public int SplatCount => _count;
    public int ReceivedCount => _rxCount;
    public bool ReceiverValid => _rxValid;
    public int DeadbandQ { get; private set; } = 2;
    public long Frames { get; private set; }
    public long KeyFrames { get; private set; }
    public long Rejected { get; private set; }
    public long Deferred { get; private set; }
    public long TotalBytes { get; private set; }
    public int LastPacketBytes { get; private set; }
    public int LastRecords { get; private set; }
    public bool LastKey { get; private set; }
    public float Mbps { get; private set; }
    public float PeakMbps { get; private set; }
    public float MaxErrorMm { get; private set; }
    public double EncodeUs { get; private set; }
    public double DecodeUs { get; private set; }
    public double TransformUs { get; private set; }
    public double SliceUs { get; private set; }
    public double FrameUs => EncodeUs + DecodeUs + TransformUs + SliceUs;
    public ReadOnlySpan<uint> Quilt => _quilt;
    public ReadOnlySpan<byte> Packet => _packet.AsSpan(0, _packetLen);
    public int Coverage(int view) => _coverage[view];
    public uint Pixel(int view, int x, int y) => _quilt[(view / QuiltCols * TileH + y) * QuiltW + view % QuiltCols * TileW + x];
    public Vector3 WorldSplat(int i) => new(_wx[i], _wy[i], _wz[i]);
    public Quaternion PoseRotation => _q;
    public Vector3 PoseTranslation => _t;
    public float BudgetBytes => BudgetMbps * 1e6f / 8f / MathF.Max(1f, StreamFps);

    private static float[] BuildViewOffsets()
    {
        var o = new float[Views];
        for (var v = 0; v < Views; v++) o[v] = ViewerDistance * MathF.Tan(ViewAngleDeg(v) * MathF.PI / 180f);
        return o;
    }

    /// <summary>시점 v의 수평 시야각(°) — 0 = 좌측 끝 · 44 = 우측 끝.</summary>
    public static float ViewAngleDeg(int v) => -ViewConeDeg * 0.5f + ViewConeDeg * v / (Views - 1);
    public static float ViewOffsetX(int v) => ViewOx[v];
    public static int ViewForAngle(float deg) => Math.Clamp((int)MathF.Round((deg + ViewConeDeg * 0.5f) / ViewConeDeg * (Views - 1)), 0, Views - 1);

    private static ushort Quant(float v, float lo, float hi) => (ushort)Math.Clamp((int)((v - lo) / (hi - lo) * 65535f + 0.5f), 0, 65535);
    private static float Dequant(ushort q, float lo, float hi) => lo + q * ((hi - lo) / 65535f);

    // ── 캡처 입력 ──
    public void SetSplatCount(int n)
    {
        lock (_gate) { n = Math.Clamp(n, 0, MaxSplats); if (n != _count) _countChanged = true; _count = n; }
    }

    public void SetSplat(int i, Vector3 local, uint argb) { _sx[i] = local.X; _sy[i] = local.Y; _sz[i] = local.Z; _sc[i] = argb | 0xFF00_0000u; }
    public void SetSplatPosition(int i, Vector3 local) { _sx[i] = local.X; _sy[i] = local.Y; _sz[i] = local.Z; }
    public Vector3 SourceSplat(int i) => new(_sx[i], _sy[i], _sz[i]);

    /// <summary>원격 퍼포머 6DoF 자세(캡처 볼륨 → 디스플레이 공간).</summary>
    public void SetPose(Quaternion q, Vector3 t) { lock (_gate) { _q = Quaternion.Normalize(q); _t = t; } }

    /// <summary>캡처 → 패킷(키/델타 · 예산 적응) — 무할당. 헤더 40 B: 매직 · seq · 플래그 · 레코드 수 · 자세(q xyzw · t xyz).</summary>
    public int Encode()
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            var p = _packet.AsSpan();
            var key = ForceKey || !_rxValid || _countChanged || _sinceKey >= KeyInterval;
            var o = HeaderBytes; var recs = 0;
            Vector3 lo = BoundsLo, hi = BoundsHi;
            if (key)
            {
                for (var i = 0; i < _count; i++)
                {
                    ushort qx = Quant(_sx[i], lo.X, hi.X), qy = Quant(_sy[i], lo.Y, hi.Y), qz = Quant(_sz[i], lo.Z, hi.Z);
                    BinaryPrimitives.WriteUInt16LittleEndian(p[o..], qx); BinaryPrimitives.WriteUInt16LittleEndian(p[(o + 2)..], qy); BinaryPrimitives.WriteUInt16LittleEndian(p[(o + 4)..], qz);
                    BinaryPrimitives.WriteUInt32LittleEndian(p[(o + 6)..], _sc[i]);
                    _refX[i] = qx; _refY[i] = qy; _refZ[i] = qz;
                    o += KeyRecord;
                }
                recs = _count; _sinceKey = 0; _countChanged = false; ForceKey = false; KeyFrames++;
            }
            else
            {
                var cap = (int)BudgetBytes; var db = DeadbandQ; var deferred = 0;
                for (var i = 0; i < _count; i++)
                {
                    ushort qx = Quant(_sx[i], lo.X, hi.X), qy = Quant(_sy[i], lo.Y, hi.Y), qz = Quant(_sz[i], lo.Z, hi.Z);
                    var d = Math.Max(Math.Abs(qx - _refX[i]), Math.Max(Math.Abs(qy - _refY[i]), Math.Abs(qz - _refZ[i])));
                    if (d <= db) continue;
                    if (o + DeltaRecord > cap) { deferred++; continue; }
                    BinaryPrimitives.WriteUInt16LittleEndian(p[o..], (ushort)i);
                    BinaryPrimitives.WriteUInt16LittleEndian(p[(o + 2)..], qx); BinaryPrimitives.WriteUInt16LittleEndian(p[(o + 4)..], qy); BinaryPrimitives.WriteUInt16LittleEndian(p[(o + 6)..], qz);
                    _refX[i] = qx; _refY[i] = qy; _refZ[i] = qz;
                    o += DeltaRecord; recs++;
                }
                Deferred += deferred;
                // 적응 데드밴드: 예산 95% 초과(또는 이월) → 배증 · 50% 미만 → 반감
                if (deferred > 0 || o > cap * 0.95f) DeadbandQ = Math.Min(DeadbandQ * 2, 4096);
                else if (o < cap * 0.5f && DeadbandQ > MinDeadbandQ) DeadbandQ = Math.Max(MinDeadbandQ, DeadbandQ / 2);
                _sinceKey++;
            }
            // 헤더: 매직 · seq · 플래그 · 레코드 수 · 자세(q xyzw · t xyz)
            BinaryPrimitives.WriteUInt32LittleEndian(p, Magic);
            BinaryPrimitives.WriteUInt32LittleEndian(p[4..], ++_seq);
            p[8] = (byte)(key ? 1 : 0); p[9] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(p[10..], (ushort)recs);
            BinaryPrimitives.WriteSingleLittleEndian(p[12..], _q.X); BinaryPrimitives.WriteSingleLittleEndian(p[16..], _q.Y);
            BinaryPrimitives.WriteSingleLittleEndian(p[20..], _q.Z); BinaryPrimitives.WriteSingleLittleEndian(p[24..], _q.W);
            BinaryPrimitives.WriteSingleLittleEndian(p[28..], _t.X); BinaryPrimitives.WriteSingleLittleEndian(p[32..], _t.Y); BinaryPrimitives.WriteSingleLittleEndian(p[36..], _t.Z);
            _packetLen = o; LastPacketBytes = o; LastRecords = recs; LastKey = key; TotalBytes += o; Frames++;
            var mbps = o * 8f * StreamFps / 1e6f;
            Mbps = Frames == 1 ? mbps : Mbps * 0.9f + mbps * 0.1f;
            if (mbps > PeakMbps) PeakMbps = mbps;
            EncodeUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
            return o;
        }
    }

    /// <summary>수신 패킷 검증 · 복원(키 = 전체 교체 · 델타 = 인덱스 갱신). 손상/선행 키 없음 = 거부.</summary>
    public bool Decode(ReadOnlySpan<byte> p)
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            var ok = DecodeCore(p);
            if (!ok) Rejected++;
            DecodeUs = (Stopwatch.GetTimestamp() - t0) * TickUs;
            return ok;
        }
    }

    private bool DecodeCore(ReadOnlySpan<byte> p)
    {
        if (p.Length < HeaderBytes || BinaryPrimitives.ReadUInt32LittleEndian(p) != Magic) return false;
        var key = p[8] == 1; int recs = BinaryPrimitives.ReadUInt16LittleEndian(p[10..]);
        var size = key ? KeyRecord : DeltaRecord;
        if (p.Length != HeaderBytes + recs * size || (key && recs > MaxSplats) || (!key && !_rxValid)) return false;
        Vector3 lo = BoundsLo, hi = BoundsHi;
        var o = HeaderBytes;
        if (key)
        {
            for (var i = 0; i < recs; i++, o += KeyRecord)
            {
                _lx[i] = Dequant(BinaryPrimitives.ReadUInt16LittleEndian(p[o..]), lo.X, hi.X);
                _ly[i] = Dequant(BinaryPrimitives.ReadUInt16LittleEndian(p[(o + 2)..]), lo.Y, hi.Y);
                _lz[i] = Dequant(BinaryPrimitives.ReadUInt16LittleEndian(p[(o + 4)..]), lo.Z, hi.Z);
                _rc[i] = BinaryPrimitives.ReadUInt32LittleEndian(p[(o + 6)..]);
            }
            _rxCount = recs; _rxValid = true;
        }
        else
        {
            for (var r = 0; r < recs; r++, o += DeltaRecord)
            {
                int i = BinaryPrimitives.ReadUInt16LittleEndian(p[o..]);
                if (i >= _rxCount) return false;
                _lx[i] = Dequant(BinaryPrimitives.ReadUInt16LittleEndian(p[(o + 2)..]), lo.X, hi.X);
                _ly[i] = Dequant(BinaryPrimitives.ReadUInt16LittleEndian(p[(o + 4)..]), lo.Y, hi.Y);
                _lz[i] = Dequant(BinaryPrimitives.ReadUInt16LittleEndian(p[(o + 6)..]), lo.Z, hi.Z);
            }
        }
        _rxQ = new Quaternion(BinaryPrimitives.ReadSingleLittleEndian(p[12..]), BinaryPrimitives.ReadSingleLittleEndian(p[16..]),
                              BinaryPrimitives.ReadSingleLittleEndian(p[20..]), BinaryPrimitives.ReadSingleLittleEndian(p[24..]));
        _rxT = new Vector3(BinaryPrimitives.ReadSingleLittleEndian(p[28..]), BinaryPrimitives.ReadSingleLittleEndian(p[32..]), BinaryPrimitives.ReadSingleLittleEndian(p[36..]));
        return true;
    }

    /// <summary>수신 복원 → 6DoF 변환 → 45 시점 슬라이싱(무할당).</summary>
    public void Synthesize()
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            var n = _rxCount;
            var m = Matrix4x4.CreateScale(HologramScale) * Matrix4x4.CreateFromQuaternion(_rxQ) * Matrix4x4.CreateTranslation(_rxT);
            HoloSimd.Transform(_lx.AsSpan(0, n), _ly.AsSpan(0, n), _lz.AsSpan(0, n), _wx.AsSpan(0, n), _wy.AsSpan(0, n), _wz.AsSpan(0, n), m);
            var t1 = Stopwatch.GetTimestamp();
            TransformUs = (t1 - t0) * TickUs;
            // 원근 계수(시점 독립) · 세로 픽셀(수평 시차 전용 → 시점 공통)
            HoloSimd.Perspective(_wx.AsSpan(0, n), _wz.AsSpan(0, n), ViewerDistance, _a.AsSpan(0, n), _b.AsSpan(0, n));
            HoloSimd.Perspective(_wy.AsSpan(0, n), _wz.AsSpan(0, n), ViewerDistance, _ay.AsSpan(0, n), _px.AsSpan(0, n));
            HoloSimd.ShearProject(_py.AsSpan(0, n), _ay.AsSpan(0, n), _b.AsSpan(0, n), 0f, -0.5f * TileH / HalfHeight, 0.5f * TileH);
            _quilt.AsSpan().Clear();
            _depth.AsSpan().Fill(float.NegativeInfinity);
            const float sx = 0.5f * TileW / HalfWidth, ox0 = 0.5f * TileW;
            for (var v = 0; v < Views; v++)
            {
                HoloSimd.ShearProject(_px.AsSpan(0, n), _a.AsSpan(0, n), _b.AsSpan(0, n), ViewOx[v], sx, ox0);
                int col0 = v % QuiltCols * TileW, row0 = v / QuiltCols * TileH, cov = 0;
                for (var i = 0; i < n; i++)
                {
                    float fx = _px[i], fy = _py[i];
                    if (!(fx >= 0f && fx < TileW && fy >= 0f && fy < TileH)) continue;
                    var idx = (row0 + (int)fy) * QuiltW + col0 + (int)fx;
                    var z = _wz[i];
                    if (z <= _depth[idx]) continue;
                    if (_quilt[idx] == 0) cov++;
                    _depth[idx] = z; _quilt[idx] = _rc[i];
                }
                _coverage[v] = cov;
            }
            SliceUs = (Stopwatch.GetTimestamp() - t1) * TickUs;
        }
    }

    /// <summary>1프레임: 인코딩 → (전송) → 디코딩 → 합성 · 재구성 오차 측정.</summary>
    public void ProcessFrame()
    {
        lock (_gate)
        {
            Encode();
            Decode(Packet);
            Synthesize();
            var e = 0f;
            for (var i = 0; i < _rxCount && i < _count; i++)
            {
                var d = MathF.Max(MathF.Abs(_lx[i] - _sx[i]), MathF.Max(MathF.Abs(_ly[i] - _sy[i]), MathF.Abs(_lz[i] - _sz[i])));
                if (d > e) e = d;
            }
            MaxErrorMm = e * 1000f;
        }
    }

    public const int BmpHeader = 54, ViewBmpBytes = BmpHeader + TileW * TileH * 4;

    /// <summary>시점 타일 → 32비트 BMP(하향식) — UI 프리뷰용.</summary>
    public int WriteViewBmp(int view, Span<byte> dst)
    {
        if (dst.Length < ViewBmpBytes) return 0;
        dst[..BmpHeader].Clear();
        dst[0] = (byte)'B'; dst[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(dst[2..], ViewBmpBytes);
        BinaryPrimitives.WriteInt32LittleEndian(dst[10..], BmpHeader);
        BinaryPrimitives.WriteInt32LittleEndian(dst[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(dst[18..], TileW);
        BinaryPrimitives.WriteInt32LittleEndian(dst[22..], -TileH);
        BinaryPrimitives.WriteInt16LittleEndian(dst[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(dst[28..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(dst[34..], TileW * TileH * 4);
        int col0 = view % QuiltCols * TileW, row0 = view / QuiltCols * TileH;
        for (var y = 0; y < TileH; y++)
            MemoryMarshal.AsBytes(_quilt.AsSpan((row0 + y) * QuiltW + col0, TileW)).CopyTo(dst[(BmpHeader + y * TileW * 4)..]);
        return ViewBmpBytes;
    }
}
