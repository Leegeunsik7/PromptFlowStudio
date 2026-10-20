using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace PromptFlowStudio.Services.NeuralCodec;

public enum FrameOutcome : byte { Clean, Repaired, Concealed, Drift, Frozen, None }

/// <summary>
/// [v17.02.0] GF(2⁸) 산술(원시 다항식 x⁸+x⁴+x³+x²+1 = 0x11D) · 버퍼 곱-누산 dst ^= c·src.
/// SSSE3 니블 분할 셔플(PSHUFB 16 B/명령) · c = 1은 Vector&lt;byte&gt; XOR · 그 외 스칼라 로그/지수표 — 모두 비트 동일.
/// </summary>
public static class Gf256
{
    public static readonly byte[] Exp = new byte[512], Log = new byte[256];
    private static readonly byte[] Nib = new byte[256 * 32];

    static Gf256()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x; Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0) x ^= 0x11D;
        }
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        for (var c = 0; c < 256; c++)
            for (var i = 0; i < 16; i++)
            {
                Nib[c * 32 + i] = Mul((byte)c, (byte)i);
                Nib[c * 32 + 16 + i] = Mul((byte)c, (byte)(i << 4));
            }
    }

    public static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
    public static byte Inv(byte a) => Exp[255 - Log[a]];

    public static void MulXor(byte c, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        if (c == 0) return;
        var n = dst.Length; var i = 0;
        if (c == 1)
        {
            if (Vector.IsHardwareAccelerated)
                for (var w = Vector<byte>.Count; i <= n - w; i += w) (new Vector<byte>(dst.Slice(i)) ^ new Vector<byte>(src.Slice(i))).CopyTo(dst.Slice(i));
            for (; i < n; i++) dst[i] ^= src[i];
            return;
        }
        if (Ssse3.IsSupported && n >= 16)
        {
            ref var tb = ref MemoryMarshal.GetArrayDataReference(Nib);
            var lo = Vector128.LoadUnsafe(ref tb, (nuint)(c * 32));
            var hi = Vector128.LoadUnsafe(ref tb, (nuint)(c * 32 + 16));
            var mask = Vector128.Create((byte)0x0F);
            ref var s = ref MemoryMarshal.GetReference(src);
            ref var d = ref MemoryMarshal.GetReference(dst);
            for (; i <= n - 16; i += 16)
            {
                var x = Vector128.LoadUnsafe(ref s, (nuint)i);
                var p = Ssse3.Shuffle(lo, x & mask) ^ Ssse3.Shuffle(hi, Vector128.ShiftRightLogical(x.AsUInt16(), 4).AsByte() & mask);
                (Vector128.LoadUnsafe(ref d, (nuint)i) ^ p).StoreUnsafe(ref d, (nuint)i);
            }
        }
        var lc = Log[c];
        for (; i < n; i++) { var v = src[i]; if (v != 0) dst[i] ^= Exp[lc + Log[v]]; }
    }

    public static void MulXorScalar(byte c, ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (var i = 0; i < dst.Length; i++) dst[i] ^= Mul(c, src[i]);
    }

    /// <summary>코시 행렬 계수 1/(xᵢ ⊕ yⱼ) — 패리티 행 p(x = p) · 데이터 열 j(y = 8 + j) · 임의 정방 부분 행렬 가역(MDS).</summary>
    public static byte Cauchy(int p, int j) => Inv((byte)(p ^ (PacketLossConcealmentFecGuard.MaxM + j)));
}

/// <summary>
/// [PromptFlow Studio v17.02.0] 🛡 패킷 손실 은닉 &amp; 전진 오류 수정 가드(UDP 전송 시뮬레이션 · 무할당).
/// AU → 1200 B 패킷화 → FEC 블록(k ≤ 16)마다 체계적 리드-솔로몬(코시 GF(2⁸) 다항식 · 패리티 m 적응: 손실 추정 EWMA) →
/// 블록 간 인터리빙 송출 → Gilbert-Elliott 버스트 손실 + 지터 지연 채널(적응형 지터 버퍼 기한 초과 = 지연 손실) →
/// 소거 위치 기반 GF 가우스-조르당 역행렬 복원 → 디코드. 복원 불가 시 움직임 보상 외삽 프레임 생성(은닉) + PLI(RTT 후 키프레임 요청) · 키프레임 전까지 드리프트 표기.
/// </summary>
public sealed class PacketLossConcealmentFecGuard
{
    public const int Payload = 1200, MaxK = 16, MaxM = 8, Stride = MaxK + MaxM, MaxBlocks = 24, MaxData = MaxBlocks * MaxK;
    public static readonly string[] OutcomeNames = { "무결", "FEC 복원", "은닉(프레임 생성)", "드리프트", "프리즈", "—" };

    private readonly byte[] _pkt = new byte[MaxBlocks * Stride * Payload];
    private readonly bool[] _rcv = new bool[MaxBlocks * Stride];
    private readonly int[] _k = new int[MaxBlocks], _m = new int[MaxBlocks];
    private readonly byte[] _rx = new byte[MaxData * Payload];
    private readonly byte[] _mat = new byte[MaxM * MaxM], _inv = new byte[MaxM * MaxM], _syn = new byte[MaxM * Payload];
    private readonly int[] _miss = new int[MaxM], _rows = new int[MaxM];
    private ulong _rng = 0xD1B54A32D192ED03UL;
    private bool _bad, _drift;
    private double _pliAt = -1, _stormUntil = -1;
    private float _lossEst, _delayMean = -1f, _prevDelay = -1f, _jit;
    private int _run;

    public bool FecEnabled { get; set; } = true;
    public bool ConcealEnabled { get; set; } = true;
    public bool Interleave { get; set; } = true;
    public float PathLoss { get; private set; } = 0.002f;
    public float RttMs { get; private set; } = 40f;
    public float PathJitterMs { get; private set; } = 2f;
    public bool StormActive { get; private set; }

    public long PacketsSent { get; private set; }
    public long DataPackets { get; private set; }
    public long ParityPackets { get; private set; }
    public long Lost { get; private set; }
    public long Late { get; private set; }
    public long Recovered { get; private set; }
    public long Unrecovered { get; private set; }
    public long FramesClean { get; private set; }
    public long FramesRepaired { get; private set; }
    public long FramesConcealed { get; private set; }
    public long FramesDrift { get; private set; }
    public long FramesFrozen { get; private set; }
    public long Oversize { get; private set; }
    public long PliSent { get; private set; }
    public int MaxBurst { get; private set; }
    public int LastBlocks { get; private set; }
    public int LastK { get; private set; }
    public int LastM { get; private set; }
    public int LastLost { get; private set; }
    public FrameOutcome LastOutcome { get; private set; } = FrameOutcome.None;
    public double LastRecoverUs { get; private set; }
    public float LossEstimate => _lossEst;
    public float JitterEstimateMs => _jit;
    public float JitterBufferMs => (_delayMean < 0 ? RttMs * 0.5f : _delayMean) + 4f * _jit + 2f;
    public bool Drifting => _drift;
    public bool PliPending => _pliAt >= 0;
    public float FecOverhead => DataPackets == 0 ? 0f : ParityPackets / (float)DataPackets;
    public float RawLossRate => PacketsSent == 0 ? 0f : (Lost + Late) / (float)PacketsSent;
    /// <summary>손실 데이터 패킷 중 FEC로 복원된 비율.</summary>
    public float RecoveryRate => Recovered + Unrecovered == 0 ? 1f : Recovered / (float)(Recovered + Unrecovered);
    public long FramesTotal => FramesClean + FramesRepaired + FramesConcealed + FramesDrift + FramesFrozen;
    /// <summary>무결 + 복원 프레임 비율(시청자 체감 무손실).</summary>
    public float IntactRate => FramesTotal == 0 ? 1f : (FramesClean + FramesRepaired) / (float)FramesTotal;

    public void SetPath(float loss, float rttMs, float jitterMs)
    {
        PathLoss = Math.Clamp(loss, 0f, 0.5f); RttMs = MathF.Max(1f, rttMs); PathJitterMs = MathF.Max(0.1f, jitterMs);
    }

    /// <summary>버스트 손실 폭풍(무선 핸드오버 · 혼잡 붕괴 모사).</summary>
    public void Storm(double now, double seconds) => _stormUntil = now + seconds;

    public bool TakePli(double now)
    {
        if (_pliAt < 0 || now < _pliAt) return false;
        _pliAt = -1;
        return true;
    }

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    private int AdaptiveM(int k)
    {
        if (!FecEnabled) return 0;
        var m = (int)MathF.Ceiling(k * _lossEst * 2.2f) + 1;
        if (_lossEst > 0.03f) m++;
        return Math.Clamp(m, 1, MaxM);
    }

    private Span<byte> Packet(int b, int s) => _pkt.AsSpan((b * Stride + s) * Payload, Payload);

    /// <summary>Gilbert-Elliott 2상태 손실 + 지터 지연(기한 초과 = 지연 손실). true = 수신.</summary>
    private bool Channel()
    {
        const float pbg = 0.3f;
        var target = PathLoss;
        var badLoss = 0.5f;
        if (StormActive) { target = MathF.Max(target, 0.12f); badLoss = 0.7f; }
        var piB = Math.Clamp(target * 0.7f / badLoss, 0f, 0.6f);
        var pgb = piB * pbg / MathF.Max(1e-4f, 1f - piB);
        _bad = _bad ? Next01() >= pbg : Next01() < pgb;
        var lossP = _bad ? badLoss : target * 0.3f;
        PacketsSent++;
        if (Next01() < lossP) { Lost++; _run++; if (_run > MaxBurst) MaxBurst = _run; return false; }
        var u1 = MathF.Max(Next01(), 1e-7f); var u2 = Next01();
        var g = MathF.Abs(MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(MathF.Tau * u2));
        var delay = RttMs * 0.5f + PathJitterMs * g + (_bad ? 2f * PathJitterMs : 0f);
        var deadline = JitterBufferMs;
        if (_prevDelay >= 0f) _jit += (MathF.Abs(delay - _prevDelay) - _jit) / 16f;
        _prevDelay = delay;
        _delayMean = _delayMean < 0f ? delay : _delayMean + (delay - _delayMean) / 32f;
        if (delay > deadline) { Late++; _run++; if (_run > MaxBurst) MaxBurst = _run; return false; }
        _run = 0;
        return true;
    }

    /// <summary>AU 1개 송출 → 채널 → FEC 복원 → 디코드/은닉(무할당).</summary>
    public FrameOutcome Deliver(ReadOnlySpan<byte> au, NeuralVolumetricVideoCodecEngine codec, double now)
    {
        StormActive = now < _stormUntil;
        var len = au.Length;
        var nData = (len + Payload - 1) / Payload;
        if (nData > MaxData || nData == 0)
        {
            Oversize++;
            return Lose(codec, now);
        }
        var blocks = (nData + MaxK - 1) / MaxK;
        LastBlocks = blocks;
        var off = 0; var maxS = 0;
        for (var b = 0; b < blocks; b++)
        {
            var k = nData / blocks + (b < nData % blocks ? 1 : 0);
            var m = AdaptiveM(k);
            _k[b] = k; _m[b] = m;
            for (var i = 0; i < k; i++)
            {
                var p = Packet(b, i);
                var c = Math.Min(Payload, len - off);
                au.Slice(off, c).CopyTo(p);
                if (c < Payload) p.Slice(c).Clear();
                off += c;
            }
            for (var r = 0; r < m; r++)
            {
                var par = Packet(b, MaxK + r);
                par.Clear();
                for (var i = 0; i < k; i++) Gf256.MulXor(Gf256.Cauchy(r, i), Packet(b, i), par);
            }
            DataPackets += k; ParityPackets += m;
            if (k + m > maxS) maxS = k + m;
        }
        LastK = _k[0]; LastM = _m[0];
        // ── 송출(인터리빙: 슬롯 우선 → 블록 순회) ──
        var lost = 0; var sent = 0;
        if (Interleave)
        {
            for (var s = 0; s < maxS; s++)
                for (var b = 0; b < blocks; b++)
                    if (s < _k[b] + _m[b]) { var ok = Channel(); _rcv[b * Stride + Slot(b, s)] = ok; sent++; if (!ok) lost++; }
        }
        else
        {
            for (var b = 0; b < blocks; b++)
                for (var s = 0; s < _k[b] + _m[b]; s++) { var ok = Channel(); _rcv[b * Stride + Slot(b, s)] = ok; sent++; if (!ok) lost++; }
        }
        LastLost = lost;
        _lossEst += (lost / (float)sent - _lossEst) * 0.08f;
        // ── 복원 ──
        var t0 = Stopwatch.GetTimestamp();
        var repaired = false; var intact = true;
        for (var b = 0; b < blocks && intact; b++)
        {
            var e = Recover(b);
            if (e < 0) intact = false;
            else if (e > 0) repaired = true;
        }
        LastRecoverUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
        if (!intact) return Lose(codec, now);
        off = 0;
        for (var b = 0; b < blocks; b++)
            for (var i = 0; i < _k[b]; i++) { var c = Math.Min(Payload, len - off); Packet(b, i).Slice(0, c).CopyTo(_rx.AsSpan(off)); off += c; }
        if (!codec.Decode(_rx.AsSpan(0, len))) return Lose(codec, now);
        if (codec.LastDecodedKey) _drift = false;
        FrameOutcome o;
        if (_drift) { o = FrameOutcome.Drift; FramesDrift++; }
        else if (repaired) { o = FrameOutcome.Repaired; FramesRepaired++; }
        else { o = FrameOutcome.Clean; FramesClean++; }
        return LastOutcome = o;
    }

    /// <summary>송출 순번 → 저장 슬롯(데이터 0..k−1 · 패리티 MaxK..).</summary>
    private int Slot(int b, int s) => s < _k[b] ? s : MaxK + s - _k[b];

    private FrameOutcome Lose(NeuralVolumetricVideoCodecEngine codec, double now)
    {
        _drift = true;
        if (_pliAt < 0) { _pliAt = now + RttMs / 1000.0; PliSent++; }
        if (ConcealEnabled && codec.Conceal()) { FramesConcealed++; return LastOutcome = FrameOutcome.Concealed; }
        FramesFrozen++;
        return LastOutcome = FrameOutcome.Frozen;
    }

    /// <summary>블록 소거 복원: 반환 = 복원 패킷 수(0 = 무손실) · −1 = 복원 불가(잔여 손실 계수).</summary>
    private int Recover(int b)
    {
        int k = _k[b], m = _m[b], e = 0, avail = 0;
        for (var i = 0; i < k; i++) if (!_rcv[b * Stride + i]) { if (e < MaxM) _miss[e] = i; e++; }
        if (e == 0) return 0;
        for (var r = 0; r < m && avail < e; r++) if (_rcv[b * Stride + MaxK + r]) _rows[avail++] = r;
        if (e > m || avail < e) { Unrecovered += e; return -1; }
        // 신드롬 sᵣ = pᵣ ⊕ Σ_수신 cᵣⱼ·dⱼ = Σ_소거 cᵣⱼ·dⱼ
        for (var t = 0; t < e; t++)
        {
            var syn = _syn.AsSpan(t * Payload, Payload);
            Packet(b, MaxK + _rows[t]).CopyTo(syn);
            for (var j = 0; j < k; j++) if (_rcv[b * Stride + j]) Gf256.MulXor(Gf256.Cauchy(_rows[t], j), Packet(b, j), syn);
            for (var u = 0; u < e; u++) _mat[t * MaxM + u] = Gf256.Cauchy(_rows[t], _miss[u]);
        }
        if (!Invert(e)) { Unrecovered += e; return -1; }
        for (var u = 0; u < e; u++)
        {
            var d = Packet(b, _miss[u]);
            d.Clear();
            for (var t = 0; t < e; t++) Gf256.MulXor(_inv[u * MaxM + t], _syn.AsSpan(t * Payload, Payload), d);
            _rcv[b * Stride + _miss[u]] = true;
        }
        Recovered += e;
        return e;
    }

    /// <summary>GF(2⁸) 가우스-조르당(n ≤ 8) _mat → _inv.</summary>
    private bool Invert(int n)
    {
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++) _inv[i * MaxM + j] = (byte)(i == j ? 1 : 0);
        for (var c = 0; c < n; c++)
        {
            var p = c;
            while (p < n && _mat[p * MaxM + c] == 0) p++;
            if (p == n) return false;
            if (p != c)
                for (var j = 0; j < n; j++)
                {
                    (_mat[p * MaxM + j], _mat[c * MaxM + j]) = (_mat[c * MaxM + j], _mat[p * MaxM + j]);
                    (_inv[p * MaxM + j], _inv[c * MaxM + j]) = (_inv[c * MaxM + j], _inv[p * MaxM + j]);
                }
            var iv = Gf256.Inv(_mat[c * MaxM + c]);
            for (var j = 0; j < n; j++) { _mat[c * MaxM + j] = Gf256.Mul(_mat[c * MaxM + j], iv); _inv[c * MaxM + j] = Gf256.Mul(_inv[c * MaxM + j], iv); }
            for (var r = 0; r < n; r++)
            {
                if (r == c) continue;
                var f = _mat[r * MaxM + c];
                if (f == 0) continue;
                for (var j = 0; j < n; j++) { _mat[r * MaxM + j] ^= Gf256.Mul(f, _mat[c * MaxM + j]); _inv[r * MaxM + j] ^= Gf256.Mul(f, _inv[c * MaxM + j]); }
            }
        }
        return true;
    }

    /// <summary>테스트/진단: 블록 0 직접 소거 → 복원 결과(데이터 바이트 비교용).</summary>
    public int EncodeBlockForTest(ReadOnlySpan<byte> data, int k, int m)
    {
        _k[0] = k; _m[0] = m;
        for (var i = 0; i < k; i++) data.Slice(i * Payload, Payload).CopyTo(Packet(0, i));
        for (var r = 0; r < m; r++)
        {
            var par = Packet(0, MaxK + r);
            par.Clear();
            for (var i = 0; i < k; i++) Gf256.MulXor(Gf256.Cauchy(r, i), Packet(0, i), par);
        }
        for (var s = 0; s < Stride; s++) _rcv[s] = true;
        return k + m;
    }

    public void EraseForTest(int slot) { _rcv[slot] = false; if (slot < MaxK) Packet(0, slot).Fill(0xEE); }
    public int RecoverForTest() => Recover(0);
    public ReadOnlySpan<byte> DataPacketForTest(int i) => Packet(0, i);
}
