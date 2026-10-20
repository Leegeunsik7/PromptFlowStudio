using System.Buffers.Binary;
using System.Numerics;
using PromptFlowStudio.Services.OpenUsd;

namespace PromptFlowStudio.Services.DigitalTwinWorld;

/// <summary>앵커 입력 출처: 센서(실제 이동 가능) · 물리(드리프트 → 복귀).</summary>
public enum AnchorSource : byte { Sensor, Physics }

/// <summary>앵커 판정.</summary>
public enum AnchorVerdict : byte { Held, JitterSuppressed, OutlierRejected, DriftSnapped, Relocked, Unlocked }

/// <summary>라이브 세션 통계(클라이언트별).</summary>
public sealed class LiveSession
{
    internal LiveSession(int id, string name) { Id = id; Name = name; Replica = new UsdLayer(name + ".usda"); }
    public int Id { get; }
    public string Name { get; }
    public UsdLayer Replica { get; }
    public bool Connected { get; internal set; } = true;
    public uint Expected { get; internal set; } = 1;
    public long Applied { get; internal set; }
    public long Dropped { get; internal set; }
    public long Corrupted { get; internal set; }
    public long Reordered { get; internal set; }
    public long Nacks { get; internal set; }
    public long Resyncs { get; internal set; }
    public double LastLatencyMs { get; internal set; }
    internal readonly byte[][] Hold = NewHold();
    internal readonly int[] HoldLen = new int[OmniverseLiveUsdSyncAnchorGuard.ReorderWindow];
    internal readonly uint[] HoldSeq = new uint[OmniverseLiveUsdSyncAnchorGuard.ReorderWindow];
    private static byte[][] NewHold() { var h = new byte[OmniverseLiveUsdSyncAnchorGuard.ReorderWindow][]; for (var i = 0; i < h.Length; i++) h[i] = new byte[OmniverseLiveUsdSyncAnchorGuard.PacketBytes]; return h; }
}

/// <summary>
/// [PromptFlow Studio v16.99.0] 🌐 옴니버스 라이브 USD 동기화 &amp; 공간 앵커 락 가드.
/// · Nucleus 대역(인프로세스 서버): 권위 UsdLayer 스냅샷 비교 → 프림 추가(경로 · 타입) + 속성 델타(translate/scale/color/radius/size/mass/visibility 비트마스크 · float 비트 그대로 = 무손실)
///   → 패킷(seq · 레코드 · CRC-32) · 재전송 링(256).
/// · 채널 시뮬레이션: 결정적 손실/순서 뒤바뀜/비트 오류 · 수신: CRC 검증 → 순서 버퍼(8) → 공백 NACK 재전송 → 링 밖 = 전체 재동기화(스냅샷).
/// · 무손실 검증: 서버/복제본 상태 다이제스트(FNV-1a 64 · float 비트) 일치.
/// · 앵커 락: 데드밴드 내 지터 고정 · 단발 점프 이상치 거부 · 물리 드리프트 = 락 위치 복귀 · 센서 지속 이동(K프레임 일관) = 재고정.
/// </summary>
public sealed class OmniverseLiveUsdSyncAnchorGuard
{
    public const int PacketBytes = 2048, RetransmitRing = 256, ReorderWindow = 8, MaxSessions = 8, MaxAnchors = 64;
    private const byte OpAdd = 1, OpAttr = 2;
    private const ushort FT = 1, FS = 2, FC = 4, FR = 8, FZ = 16, FM = 32, FV = 64;
    private static readonly uint[] CrcTable = BuildCrc();

    private readonly object _gate = new();
    private readonly UsdLayer _src;
    // 스냅샷(마지막 송신 상태)
    private readonly float[] _snap = new float[UsdLayer.MaxPrims * 13];
    private int _snapCount;
    private readonly byte[][] _ring = new byte[RetransmitRing][];
    private readonly int[] _ringLen = new int[RetransmitRing];
    private readonly uint[] _ringSeq = new uint[RetransmitRing];
    private readonly LiveSession?[] _sessions = new LiveSession?[MaxSessions];
    private readonly byte[] _wire = new byte[PacketBytes], _scratch = new byte[PacketBytes];
    private uint _seq;
    private ulong _rng = 0x1699_0B5EUL;
    private long _sentPackets, _sentBytes, _records;
    // 앵커
    private readonly int[] _aPrim = new int[MaxAnchors];
    private readonly Vector3[] _aLock = new Vector3[MaxAnchors], _aEma = new Vector3[MaxAnchors];
    private readonly int[] _aConsist = new int[MaxAnchors];
    private readonly bool[] _aLocked = new bool[MaxAnchors];
    private readonly AnchorVerdict[] _aLast = new AnchorVerdict[MaxAnchors];
    private readonly long[] _aCount = new long[MaxAnchors * 6];
    private int _anchors;

    public OmniverseLiveUsdSyncAnchorGuard(UsdLayer source)
    {
        _src = source;
        for (var i = 0; i < RetransmitRing; i++) _ring[i] = new byte[PacketBytes];
    }

    public UsdLayer Source => _src;
    public double LossRate { get; set; }
    public double ReorderRate { get; set; }
    public double CorruptRate { get; set; }
    public float SimLatencyMs { get; set; } = 12f;
    public uint Sequence => _seq;
    public long SentPackets => Interlocked.Read(ref _sentPackets);
    public long SentBytes => Interlocked.Read(ref _sentBytes);
    public long Records => Interlocked.Read(ref _records);
    public int LastPacketBytes { get; private set; }
    public int LastRecords { get; private set; }
    public float AnchorDeadbandM { get; set; } = 0.01f;
    public float AnchorMaxJumpM { get; set; } = 0.4f;
    public float AnchorConsistM { get; set; } = 0.015f;
    public int AnchorRelockFrames { get; set; } = 10;

    public LiveSession? Session(int i) => _sessions[i];
    public int SessionCount { get { var c = 0; foreach (var s in _sessions) if (s is not null) c++; return c; } }

    // ── 세션 ──
    /// <summary>세션 참가(늦은 참가 = 전체 스냅샷 동기화).</summary>
    public LiveSession? Join(string name)
    {
        lock (_gate)
        {
            for (var i = 0; i < MaxSessions; i++)
            {
                if (_sessions[i] is { } e && e.Name == name) { e.Connected = true; FullResync(e); return e; }
                if (_sessions[i] is not null) continue;
                var s = new LiveSession(i, name);
                _sessions[i] = s;
                FullResync(s);
                return s;
            }
            return null;
        }
    }

    public void SetConnected(int id, bool on)
    {
        lock (_gate)
        {
            if (_sessions[id] is not { } s) return;
            s.Connected = on;
            if (on) Catchup(s);
        }
    }

    public void Leave(int id) { lock (_gate) _sessions[id] = null; }

    private void FullResync(LiveSession s)
    {
        CopyLayer(_src, s.Replica);
        s.Expected = _seq + 1;
        s.Resyncs++;
    }

    /// <summary>재접속: 링 안이면 누락 패킷 재전송 · 밖이면 전체 재동기화.</summary>
    private void Catchup(LiveSession s)
    {
        if (_seq + 1 - s.Expected > RetransmitRing - 1) { FullResync(s); return; }
        while (s.Expected <= _seq)
        {
            var slot = (int)(s.Expected % RetransmitRing);
            if (_ringSeq[slot] != s.Expected) { FullResync(s); return; }
            s.Nacks++;
            Apply(s, _ring[slot], _ringLen[slot]);
        }
    }

    // ── 송신 ──
    /// <summary>권위 레이어 델타 → 패킷 발행(무할당 · 변경 없음 = 0) → 레코드 수.</summary>
    public int Publish()
    {
        lock (_gate)
        {
            var w = _wire.AsSpan();
            var o = 10; // [seq u32][count u16][crc u32] 헤더
            int recs = 0, total = 0;
            var l = _src;
            for (var i = 0; i < l.Count; i++)
            {
                if (i >= _snapCount)
                {
                    var path = l.Path(i);
                    if (o + 5 + path.Length * 2 > PacketBytes - 64) { total += recs; FlushPacket(w, ref o, ref recs); }
                    w[o++] = OpAdd;
                    BinaryPrimitives.WriteInt16LittleEndian(w[o..], (short)l.ParentOf(i)); o += 2;
                    w[o++] = (byte)l.TypeOf(i);
                    w[o++] = (byte)Math.Min(255, path.Length);
                    for (var c = 0; c < Math.Min(255, path.Length); c++) { BinaryPrimitives.WriteUInt16LittleEndian(w[o..], path[c]); o += 2; }
                    recs++;
                    _snapCount = i + 1;
                    for (var k = 0; k < 13; k++) _snap[i * 13 + k] = float.NaN; // 전 속성 송신 강제
                }
                ushort mask = 0;
                var b = i * 13;
                var (tx, ty, tz) = l.Translate(i); var (sx, sy, sz) = l.ScaleOf(i); var (cr, cg, cb) = l.Color(i);
                if (Diff(b, tx, ty, tz)) mask |= FT;
                if (Diff(b + 3, sx, sy, sz)) mask |= FS;
                if (Diff(b + 6, cr, cg, cb)) mask |= FC;
                if (Diff1(b + 9, l.RadiusOf(i))) mask |= FR;
                if (Diff1(b + 10, l.SizeOf(i))) mask |= FZ;
                if (Diff1(b + 11, l.MassOf(i))) mask |= FM;
                if (Diff1(b + 12, l.IsInvisible(i) ? 1f : 0f)) mask |= FV;
                if (mask == 0) continue;
                if (o + 5 + 13 * 4 > PacketBytes - 8) { total += recs; FlushPacket(w, ref o, ref recs); }
                w[o++] = OpAttr;
                BinaryPrimitives.WriteUInt16LittleEndian(w[o..], (ushort)i); o += 2;
                BinaryPrimitives.WriteUInt16LittleEndian(w[o..], mask); o += 2;
                if ((mask & FT) != 0) { o = Put3(w, o, tx, ty, tz); Snap3(b, tx, ty, tz); }
                if ((mask & FS) != 0) { o = Put3(w, o, sx, sy, sz); Snap3(b + 3, sx, sy, sz); }
                if ((mask & FC) != 0) { o = Put3(w, o, cr, cg, cb); Snap3(b + 6, cr, cg, cb); }
                if ((mask & FR) != 0) { o = Put1(w, o, l.RadiusOf(i)); _snap[b + 9] = l.RadiusOf(i); }
                if ((mask & FZ) != 0) { o = Put1(w, o, l.SizeOf(i)); _snap[b + 10] = l.SizeOf(i); }
                if ((mask & FM) != 0) { o = Put1(w, o, l.MassOf(i)); _snap[b + 11] = l.MassOf(i); }
                if ((mask & FV) != 0) { var v = l.IsInvisible(i) ? 1f : 0f; o = Put1(w, o, v); _snap[b + 12] = v; }
                recs++;
            }
            total += recs;
            if (recs > 0) FlushPacket(w, ref o, ref recs);
            LastRecords = total;
            return total;
        }
    }

    private void FlushPacket(Span<byte> w, ref int o, ref int recs)
    {
        var seq = ++_seq;
        BinaryPrimitives.WriteUInt32LittleEndian(w, seq);
        BinaryPrimitives.WriteUInt16LittleEndian(w[4..], (ushort)recs);
        BinaryPrimitives.WriteUInt32LittleEndian(w[6..], Crc(w[10..o]));
        var slot = (int)(seq % RetransmitRing);
        w[..o].CopyTo(_ring[slot]); _ringLen[slot] = o; _ringSeq[slot] = seq;
        _sentPackets++; _sentBytes += o; _records += recs;
        LastPacketBytes = o;
        for (var s = 0; s < MaxSessions; s++) if (_sessions[s] is { Connected: true } ss) Deliver(ss, _ring[slot], o);
        o = 10; recs = 0;
    }

    private bool Diff(int b, float x, float y, float z) => !Same(_snap[b], x) || !Same(_snap[b + 1], y) || !Same(_snap[b + 2], z);
    private bool Diff1(int b, float x) => !Same(_snap[b], x);
    private static bool Same(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    private void Snap3(int b, float x, float y, float z) { _snap[b] = x; _snap[b + 1] = y; _snap[b + 2] = z; }
    private static int Put1(Span<byte> w, int o, float v) { BinaryPrimitives.WriteSingleLittleEndian(w[o..], v); return o + 4; }
    private static int Put3(Span<byte> w, int o, float a, float b, float c) { o = Put1(w, o, a); o = Put1(w, o, b); return Put1(w, o, c); }

    private double NextRand() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 11) * (1.0 / (1UL << 53)); }

    /// <summary>채널 시뮬레이션 전달(손실/비트 오류/순서 뒤바뀜).</summary>
    private void Deliver(LiveSession s, byte[] pkt, int len)
    {
        if (NextRand() < LossRate) { s.Dropped++; return; }
        if (NextRand() < CorruptRate)
        {
            // 비트 오류 사본 → CRC 검증 실패 = 폐기(이후 공백 NACK로 재전송)
            pkt.AsSpan(0, len).CopyTo(_scratch);
            _scratch[10 + (int)(NextRand() * Math.Max(1, len - 10))] ^= 0x10;
            if (!Verify(_scratch, len)) { s.Corrupted++; return; }
        }
        if (NextRand() < ReorderRate) { s.Reordered++; Stash(s, pkt, len); return; }
        Receive(s, pkt, len);
    }

    private static bool Verify(byte[] p, int len) => len >= 10 && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(6)) == Crc(p.AsSpan(10, len - 10));

    private void Stash(LiveSession s, byte[] pkt, int len)
    {
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(pkt);
        var k = (int)(seq % ReorderWindow);
        pkt.AsSpan(0, len).CopyTo(s.Hold[k]); s.HoldLen[k] = len; s.HoldSeq[k] = seq;
    }

    /// <summary>수신: 기대 seq = 적용 · 미래 = 보관 + 공백 NACK 재전송 · 과거 = 중복 무시.</summary>
    private void Receive(LiveSession s, byte[] pkt, int len)
    {
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(pkt);
        if (seq < s.Expected) return;
        if (seq > s.Expected)
        {
            Stash(s, pkt, len);
            // NACK: 공백 재전송(링)
            while (s.Expected < seq)
            {
                var slot = (int)(s.Expected % RetransmitRing);
                if (_ringSeq[slot] != s.Expected) { FullResync(s); return; }
                s.Nacks++;
                Apply(s, _ring[slot], _ringLen[slot]);
            }
        }
        if (seq == s.Expected) Apply(s, pkt, len);
        Drain(s);
    }

    private void Drain(LiveSession s)
    {
        for (var guard = 0; guard < ReorderWindow; guard++)
        {
            var k = (int)(s.Expected % ReorderWindow);
            if (s.HoldSeq[k] != s.Expected || s.HoldLen[k] == 0) return;
            var len = s.HoldLen[k];
            s.HoldLen[k] = 0;
            Apply(s, s.Hold[k], len);
        }
    }

    /// <summary>주기 처리: 보관 중 순서 패킷 만료(재전송으로 채움) — 지연 도착 대체.</summary>
    public void Pump()
    {
        lock (_gate)
            for (var i = 0; i < MaxSessions; i++)
                if (_sessions[i] is { Connected: true } s && s.Expected <= _seq) Catchup(s);
    }

    private void Apply(LiveSession s, byte[] pkt, int len)
    {
        var seq = BinaryPrimitives.ReadUInt32LittleEndian(pkt);
        if (seq != s.Expected || !Verify(pkt, len)) return;
        var r = pkt.AsSpan(0, len);
        var o = 10; var l = s.Replica;
        Span<char> path = stackalloc char[255];
        while (o < len)
        {
            var op = r[o++];
            if (op == OpAdd)
            {
                o += 2; // parent(경로로 해석)
                var type = (UsdPrimType)r[o++];
                int n = r[o++];
                for (var c = 0; c < n; c++) { path[c] = (char)BinaryPrimitives.ReadUInt16LittleEndian(r[o..]); o += 2; }
                l.AddPath(path[..n], UsdSpec.Def, type);
                continue;
            }
            var i = BinaryPrimitives.ReadUInt16LittleEndian(r[o..]); o += 2;
            var mask = BinaryPrimitives.ReadUInt16LittleEndian(r[o..]); o += 2;
            if ((mask & FT) != 0) { l.SetTranslate(i, F(r, o), F(r, o + 4), F(r, o + 8)); o += 12; }
            if ((mask & FS) != 0) { l.SetScale(i, F(r, o), F(r, o + 4), F(r, o + 8)); o += 12; }
            if ((mask & FC) != 0) { l.SetColor(i, F(r, o), F(r, o + 4), F(r, o + 8)); o += 12; }
            if ((mask & FR) != 0) { l.SetRadius(i, F(r, o)); o += 4; }
            if ((mask & FZ) != 0) { l.SetSize(i, F(r, o)); o += 4; }
            if ((mask & FM) != 0) { l.SetMass(i, F(r, o)); o += 4; }
            if ((mask & FV) != 0) { l.SetVisibility(i, F(r, o) == 0f); o += 4; }
        }
        s.Expected = seq + 1;
        s.Applied++;
        s.LastLatencyMs = SimLatencyMs;
    }

    private static float F(ReadOnlySpan<byte> r, int o) => BinaryPrimitives.ReadSingleLittleEndian(r[o..]);

    private static void CopyLayer(UsdLayer src, UsdLayer dst)
    {
        dst.Clear();
        for (var i = 0; i < src.Count; i++)
        {
            var d = dst.AddPath(src.Path(i), UsdSpec.Def, src.TypeOf(i));
            if (d < 0) continue;
            var (tx, ty, tz) = src.Translate(i); dst.SetTranslate(d, tx, ty, tz);
            var (sx, sy, sz) = src.ScaleOf(i); dst.SetScale(d, sx, sy, sz);
            var (r, g, b) = src.Color(i); dst.SetColor(d, r, g, b);
            dst.SetRadius(d, src.RadiusOf(i)); dst.SetSize(d, src.SizeOf(i)); dst.SetMass(d, src.MassOf(i)); dst.SetVisibility(d, !src.IsInvisible(i));
        }
    }

    /// <summary>상태 다이제스트(FNV-1a 64 · 경로 + float 비트) — 무손실 검증.</summary>
    public static ulong Digest(UsdLayer l)
    {
        var h = 14695981039346656037UL;
        void Mix(uint v) { for (var k = 0; k < 4; k++) { h ^= (byte)(v >> (8 * k)); h *= 1099511628211UL; } }
        for (var i = 0; i < l.Count; i++)
        {
            foreach (var c in l.Path(i)) Mix(c);
            Mix((uint)l.TypeOf(i));
            var (tx, ty, tz) = l.Translate(i); var (sx, sy, sz) = l.ScaleOf(i); var (r, g, b) = l.Color(i);
            Mix(B(tx)); Mix(B(ty)); Mix(B(tz)); Mix(B(sx)); Mix(B(sy)); Mix(B(sz)); Mix(B(r)); Mix(B(g)); Mix(B(b));
            Mix(B(l.RadiusOf(i))); Mix(B(l.SizeOf(i))); Mix(B(l.MassOf(i))); Mix(l.IsInvisible(i) ? 1u : 0u);
        }
        return h;
    }

    private static uint B(float f) => (uint)BitConverter.SingleToInt32Bits(f);

    public bool InSync(int session) => _sessions[session] is { } s && Digest(s.Replica) == Digest(_src);

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++) { var c = i; for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; t[i] = c; }
        return t;
    }

    public static uint Crc(ReadOnlySpan<byte> d)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in d) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }

    // ── 앵커 락 ──
    public int AnchorCount => _anchors;
    public int AnchorPrim(int a) => _aPrim[a];
    public Vector3 AnchorLocked(int a) => _aLock[a];
    public bool AnchorIsLocked(int a) => _aLocked[a];
    public AnchorVerdict AnchorLast(int a) => _aLast[a];
    public long AnchorCountOf(int a, AnchorVerdict v) => _aCount[a * 6 + (int)v];

    public int AddAnchor(int prim)
    {
        lock (_gate)
        {
            if (_anchors >= MaxAnchors || prim < 0) return -1;
            var a = _anchors++;
            var (x, y, z) = _src.Translate(prim);
            _aPrim[a] = prim; _aLock[a] = _aEma[a] = new Vector3(x, y, z); _aLocked[a] = true; _aConsist[a] = 0;
            return a;
        }
    }

    public void SetLocked(int a, bool on) { lock (_gate) { _aLocked[a] = on; if (on) { var (x, y, z) = _src.Translate(_aPrim[a]); _aLock[a] = _aEma[a] = new Vector3(x, y, z); } } }

    /// <summary>후보 위치 가드(무할당) → 권위 레이어에 기록할 위치 반환 + 판정.</summary>
    public AnchorVerdict Propose(int a, Vector3 candidate, AnchorSource src, out Vector3 accepted)
    {
        lock (_gate)
        {
            AnchorVerdict v;
            if (!_aLocked[a]) { accepted = candidate; v = AnchorVerdict.Unlocked; }
            else
            {
                var lk = _aLock[a];
                var d = (candidate - lk).Length();
                accepted = lk;
                if (d <= AnchorDeadbandM) { v = AnchorVerdict.JitterSuppressed; _aConsist[a] = 0; _aEma[a] = lk; }
                else if (d > AnchorMaxJumpM) { v = AnchorVerdict.OutlierRejected; _aConsist[a] = 0; }
                else if (src == AnchorSource.Physics) { v = AnchorVerdict.DriftSnapped; _aConsist[a] = 0; }
                else
                {
                    var prev = _aEma[a];
                    _aEma[a] = Vector3.Lerp(prev, candidate, 0.35f);
                    _aConsist[a] = (candidate - prev).Length() <= AnchorConsistM * 3 && (_aEma[a] - lk).Length() > AnchorDeadbandM ? _aConsist[a] + 1 : 0;
                    if (_aConsist[a] >= AnchorRelockFrames) { _aLock[a] = _aEma[a]; accepted = _aLock[a]; _aConsist[a] = 0; v = AnchorVerdict.Relocked; }
                    else v = AnchorVerdict.Held;
                }
            }
            _aLast[a] = v;
            _aCount[a * 6 + (int)v]++;
            _src.SetTranslate(_aPrim[a], accepted.X, accepted.Y, accepted.Z);
            return v;
        }
    }
}
