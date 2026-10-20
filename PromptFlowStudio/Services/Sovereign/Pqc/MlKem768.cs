using System.Security.Cryptography;

namespace PromptFlowStudio.Services.Sovereign.Pqc;

/// <summary>
/// [PromptFlow Studio v16.53.0] ML-KEM-768 (FIPS 203 · CRYSTALS-Kyber 표준화판) 순수 C# 구현.
///   ▸ n=256 · q=3329 · k=3 · η1=η2=2 · du=10 · dv=4 — ek 1184B · dk 2400B · ct 1088B · K 32B.
///   ▸ KeyGen/Encaps/Decaps(+ 내부 결정적 변형 _Internal: KAT 검증용) · 암시적 거부(J(z‖c)) · 상수 시간 ct 비교(FixedTimeEquals) · ek 모듈러스 검사 · dk 해시 검사.
///   ▸ 해시: SHA3-256(H) · SHA3-512(G) · SHAKE128(XOF) · SHAKE256(PRF/J) — <see cref="KeccakState"/>.
///   ⚠ 부채널(캐시 타이밍) 하드닝은 BCL 범위 내 최선 노력 — 감사받은 구현 대체 아님.
/// </summary>
public static class MlKem768
{
    public const int N = 256, Q = 3329, K = 3, Eta1 = 2, Eta2 = 2, Du = 10, Dv = 4;
    public const int EncapsulationKeySize = 384 * K + 32, DecapsulationKeySize = 768 * K + 96, CiphertextSize = 32 * (Du * K + Dv), SharedSecretSize = 32;

    private static readonly short[] Zetas = new short[128];
    private static readonly short[] Gammas = new short[128];

    static MlKem768()
    {
        for (var i = 0; i < 128; i++)
        {
            Zetas[i] = (short)ModPow(17, BitRev7(i));
            Gammas[i] = (short)ModPow(17, 2 * BitRev7(i) + 1);
        }
    }

    private static int BitRev7(int x) { var r = 0; for (var i = 0; i < 7; i++) r |= ((x >> i) & 1) << (6 - i); return r; }

    private static int ModPow(int b, int e) { long r = 1, x = b; while (e > 0) { if ((e & 1) != 0) r = r * x % Q; x = x * x % Q; e >>= 1; } return (int)r; }

    private static int Mod(int a) { var r = a % Q; return r < 0 ? r + Q : r; }

    // ══════════════════════ 공개 API ══════════════════════

    public static (byte[] Ek, byte[] Dk) GenerateKeyPair()
    {
        Span<byte> d = stackalloc byte[32]; Span<byte> z = stackalloc byte[32];
        RandomNumberGenerator.Fill(d); RandomNumberGenerator.Fill(z);
        try { return KeyGenInternal(d, z); }
        finally { CryptographicOperations.ZeroMemory(d); CryptographicOperations.ZeroMemory(z); }
    }

    public static (byte[] SharedSecret, byte[] Ciphertext) Encapsulate(ReadOnlySpan<byte> ek)
    {
        if (!CheckEncapsulationKey(ek)) throw new CryptographicException("ML-KEM: 캡슐화 키 검증 실패(길이/모듈러스)");
        Span<byte> m = stackalloc byte[32];
        RandomNumberGenerator.Fill(m);
        try { return EncapsInternal(ek, m); }
        finally { CryptographicOperations.ZeroMemory(m); }
    }

    public static byte[] Decapsulate(ReadOnlySpan<byte> dk, ReadOnlySpan<byte> c)
    {
        if (c.Length != CiphertextSize) throw new CryptographicException("ML-KEM: 암호문 길이 오류");
        if (!CheckDecapsulationKey(dk)) throw new CryptographicException("ML-KEM: 복호화 키 검증 실패(길이/해시)");
        return DecapsInternal(dk, c);
    }

    /// <summary>FIPS 203 §7.2 모듈러스 검사: ByteEncode12(ByteDecode12(ek)) == ek.</summary>
    public static bool CheckEncapsulationKey(ReadOnlySpan<byte> ek)
    {
        if (ek.Length != EncapsulationKeySize) return false;
        for (var i = 0; i < K; i++)
        {
            var seg = ek.Slice(384 * i, 384);
            for (var j = 0; j < N / 2; j++)
            {
                int a = seg[3 * j] | (seg[3 * j + 1] & 0x0F) << 8, b = seg[3 * j + 1] >> 4 | seg[3 * j + 2] << 4;
                if (a >= Q || b >= Q) return false;
            }
        }
        return true;
    }

    /// <summary>FIPS 203 §7.3 해시 검사: H(ek) == dk[768k+32 : 768k+64].</summary>
    public static bool CheckDecapsulationKey(ReadOnlySpan<byte> dk)
    {
        if (dk.Length != DecapsulationKeySize) return false;
        Span<byte> h = stackalloc byte[32];
        KeccakState.Sha3_256(dk.Slice(384 * K, EncapsulationKeySize), h);
        return CryptographicOperations.FixedTimeEquals(h, dk.Slice(768 * K + 32, 32));
    }

    // ══════════════════════ 내부(결정적) ══════════════════════

    public static (byte[] Ek, byte[] Dk) KeyGenInternal(ReadOnlySpan<byte> d, ReadOnlySpan<byte> z)
    {
        var ek = new byte[EncapsulationKeySize];
        var dkPke = new byte[384 * K];
        PkeKeyGen(d, ek, dkPke);
        var dk = new byte[DecapsulationKeySize];
        dkPke.CopyTo(dk, 0);
        ek.CopyTo(dk, 384 * K);
        KeccakState.Sha3_256(ek, dk.AsSpan(768 * K + 32, 32));
        z.CopyTo(dk.AsSpan(768 * K + 64, 32));
        CryptographicOperations.ZeroMemory(dkPke);
        return (ek, dk);
    }

    public static (byte[] SharedSecret, byte[] Ciphertext) EncapsInternal(ReadOnlySpan<byte> ek, ReadOnlySpan<byte> m)
    {
        Span<byte> gIn = stackalloc byte[64]; Span<byte> kr = stackalloc byte[64];
        m.CopyTo(gIn);
        KeccakState.Sha3_256(ek, gIn[32..]);
        KeccakState.Sha3_512(gIn, kr);
        var c = new byte[CiphertextSize];
        PkeEncrypt(ek, m, kr[32..], c);
        var key = kr[..32].ToArray();
        CryptographicOperations.ZeroMemory(kr);
        return (key, c);
    }

    public static byte[] DecapsInternal(ReadOnlySpan<byte> dk, ReadOnlySpan<byte> c)
    {
        var dkPke = dk[..(384 * K)];
        var ek = dk.Slice(384 * K, EncapsulationKeySize);
        var h = dk.Slice(768 * K + 32, 32);
        var z = dk.Slice(768 * K + 64, 32);
        Span<byte> m = stackalloc byte[32];
        PkeDecrypt(dkPke, c, m);
        Span<byte> gIn = stackalloc byte[64]; Span<byte> kr = stackalloc byte[64];
        m.CopyTo(gIn); h.CopyTo(gIn[32..]);
        KeccakState.Sha3_512(gIn, kr);
        var kBar = new byte[32];
        var j = KeccakState.Shake256(); j.Absorb(z); j.Absorb(c); j.Squeeze(kBar);
        var c2 = new byte[CiphertextSize];
        PkeEncrypt(ek, m, kr[32..], c2);
        var equal = CryptographicOperations.FixedTimeEquals(c, c2);
        // 상수 시간 선택: equal ? K' : K̄
        var mask = (byte)(equal ? 0xFF : 0x00);
        var key = new byte[32];
        for (var i = 0; i < 32; i++) key[i] = (byte)(kr[i] & mask | kBar[i] & ~mask);
        CryptographicOperations.ZeroMemory(kr); CryptographicOperations.ZeroMemory(m); CryptographicOperations.ZeroMemory(kBar);
        return key;
    }

    // ══════════════════════ K-PKE ══════════════════════

    private static void PkeKeyGen(ReadOnlySpan<byte> d, Span<byte> ek, Span<byte> dkPke)
    {
        Span<byte> gIn = stackalloc byte[33]; Span<byte> rs = stackalloc byte[64];
        d.CopyTo(gIn); gIn[32] = K;
        KeccakState.Sha3_512(gIn, rs);
        var rho = rs[..32]; var sigma = rs[32..];
        var a = new short[K * K * N];
        ExpandA(rho, a);
        var s = new short[K * N]; var e = new short[K * N];
        byte nonce = 0;
        for (var i = 0; i < K; i++) SampleCbd(sigma, nonce++, Eta1, s.AsSpan(i * N, N));
        for (var i = 0; i < K; i++) SampleCbd(sigma, nonce++, Eta1, e.AsSpan(i * N, N));
        for (var i = 0; i < K; i++) { Ntt(s.AsSpan(i * N, N)); Ntt(e.AsSpan(i * N, N)); }
        Span<short> acc = stackalloc short[N]; Span<short> tmp = stackalloc short[N];
        for (var i = 0; i < K; i++)
        {
            acc.Clear();
            for (var j = 0; j < K; j++) { MulNtt(a.AsSpan((i * K + j) * N, N), s.AsSpan(j * N, N), tmp); Add(acc, tmp); }
            Add(acc, e.AsSpan(i * N, N));
            Encode12(acc, ek.Slice(384 * i, 384));
        }
        rho.CopyTo(ek[(384 * K)..]);
        for (var i = 0; i < K; i++) Encode12(s.AsSpan(i * N, N), dkPke.Slice(384 * i, 384));
        Array.Clear(s); CryptographicOperations.ZeroMemory(rs);
    }

    private static void PkeEncrypt(ReadOnlySpan<byte> ek, ReadOnlySpan<byte> m, ReadOnlySpan<byte> r, Span<byte> c)
    {
        var t = new short[K * N];
        for (var i = 0; i < K; i++) Decode12(ek.Slice(384 * i, 384), t.AsSpan(i * N, N));
        var a = new short[K * K * N];
        ExpandA(ek[(384 * K)..], a);
        var y = new short[K * N]; var e1 = new short[K * N];
        Span<short> e2 = stackalloc short[N];
        byte nonce = 0;
        for (var i = 0; i < K; i++) SampleCbd(r, nonce++, Eta1, y.AsSpan(i * N, N));
        for (var i = 0; i < K; i++) SampleCbd(r, nonce++, Eta2, e1.AsSpan(i * N, N));
        SampleCbd(r, nonce, Eta2, e2);
        for (var i = 0; i < K; i++) Ntt(y.AsSpan(i * N, N));
        Span<short> acc = stackalloc short[N]; Span<short> tmp = stackalloc short[N];
        for (var i = 0; i < K; i++)
        {
            acc.Clear();
            for (var j = 0; j < K; j++) { MulNtt(a.AsSpan((j * K + i) * N, N), y.AsSpan(j * N, N), tmp); Add(acc, tmp); }   // Âᵀ
            InvNtt(acc);
            Add(acc, e1.AsSpan(i * N, N));
            CompressEncode(acc, Du, c.Slice(32 * Du * i, 32 * Du));
        }
        acc.Clear();
        for (var j = 0; j < K; j++) { MulNtt(t.AsSpan(j * N, N), y.AsSpan(j * N, N), tmp); Add(acc, tmp); }
        InvNtt(acc);
        Add(acc, e2);
        for (var i = 0; i < N; i++) acc[i] = (short)Mod(acc[i] + ((m[i >> 3] >> (i & 7)) & 1) * ((Q + 1) / 2));   // Decompress1
        CompressEncode(acc, Dv, c[(32 * Du * K)..]);
        Array.Clear(y);
    }

    private static void PkeDecrypt(ReadOnlySpan<byte> dkPke, ReadOnlySpan<byte> c, Span<byte> m)
    {
        var u = new short[K * N];
        for (var i = 0; i < K; i++) DecodeDecompress(c.Slice(32 * Du * i, 32 * Du), Du, u.AsSpan(i * N, N));
        Span<short> v = stackalloc short[N];
        DecodeDecompress(c[(32 * Du * K)..], Dv, v);
        Span<short> s = stackalloc short[N]; Span<short> acc = stackalloc short[N]; Span<short> tmp = stackalloc short[N];
        acc.Clear();
        for (var i = 0; i < K; i++)
        {
            Decode12(dkPke.Slice(384 * i, 384), s);
            Ntt(u.AsSpan(i * N, N));
            MulNtt(s, u.AsSpan(i * N, N), tmp);
            Add(acc, tmp);
        }
        InvNtt(acc);
        m.Clear();
        for (var i = 0; i < N; i++)
        {
            var w = Mod(v[i] - acc[i]);
            var bit = (int)(((long)w * 2 + Q / 2) / Q) & 1;                                    // Compress1 = round(2w/q) mod 2
            m[i >> 3] |= (byte)(bit << (i & 7));
        }
        s.Clear();
    }

    // ══════════════════════ 샘플링 ══════════════════════

    private static void ExpandA(ReadOnlySpan<byte> rho, short[] a)
    {
        Span<byte> seed = stackalloc byte[34];
        rho[..32].CopyTo(seed);
        Span<byte> buf = stackalloc byte[168];
        for (var i = 0; i < K; i++)
            for (var j = 0; j < K; j++)
            {
                seed[32] = (byte)j; seed[33] = (byte)i;                                          // Â[i,j] = SampleNTT(ρ‖j‖i)
                var xof = KeccakState.Shake128(); xof.Absorb(seed);
                var f = a.AsSpan((i * K + j) * N, N);
                var n = 0;
                while (n < N)
                {
                    xof.Squeeze(buf);
                    for (var p = 0; p + 2 < buf.Length && n < N; p += 3)
                    {
                        int d1 = buf[p] | (buf[p + 1] & 0x0F) << 8, d2 = buf[p + 1] >> 4 | buf[p + 2] << 4;
                        if (d1 < Q) f[n++] = (short)d1;
                        if (d2 < Q && n < N) f[n++] = (short)d2;
                    }
                }
            }
    }

    private static void SampleCbd(ReadOnlySpan<byte> seed, byte nonce, int eta, Span<short> f)
    {
        Span<byte> input = stackalloc byte[33];
        seed[..32].CopyTo(input); input[32] = nonce;
        Span<byte> b = stackalloc byte[64 * eta];
        KeccakState.Shake256(input, b);
        for (var i = 0; i < N; i++)
        {
            int x = 0, y = 0;
            for (var j = 0; j < eta; j++)
            {
                var bx = 2 * i * eta + j; var by = 2 * i * eta + eta + j;
                x += (b[bx >> 3] >> (bx & 7)) & 1;
                y += (b[by >> 3] >> (by & 7)) & 1;
            }
            f[i] = (short)Mod(x - y);
        }
        b.Clear();
    }

    // ══════════════════════ NTT ══════════════════════

    private static void Ntt(Span<short> f)
    {
        var k = 1;
        for (var len = 128; len >= 2; len >>= 1)
            for (var start = 0; start < N; start += 2 * len)
            {
                int zeta = Zetas[k++];
                for (var j = start; j < start + len; j++)
                {
                    var t = zeta * f[j + len] % Q;
                    f[j + len] = (short)Mod(f[j] - t);
                    f[j] = (short)Mod(f[j] + t);
                }
            }
    }

    private static void InvNtt(Span<short> f)
    {
        var k = 127;
        for (var len = 2; len <= 128; len <<= 1)
            for (var start = 0; start < N; start += 2 * len)
            {
                int zeta = Zetas[k--];
                for (var j = start; j < start + len; j++)
                {
                    int t = f[j];
                    f[j] = (short)Mod(t + f[j + len]);
                    f[j + len] = (short)Mod(zeta * Mod(f[j + len] - t) % Q);
                }
            }
        for (var j = 0; j < N; j++) f[j] = (short)(f[j] * 3303 % Q);
    }

    private static void MulNtt(ReadOnlySpan<short> f, ReadOnlySpan<short> g, Span<short> h)
    {
        for (var i = 0; i < 128; i++)
        {
            long a0 = f[2 * i], a1 = f[2 * i + 1], b0 = g[2 * i], b1 = g[2 * i + 1];
            h[2 * i] = (short)((a0 * b0 + a1 * b1 % Q * Gammas[i]) % Q);
            h[2 * i + 1] = (short)((a0 * b1 + a1 * b0) % Q);
        }
    }

    private static void Add(Span<short> acc, ReadOnlySpan<short> x) { for (var i = 0; i < N; i++) acc[i] = (short)Mod(acc[i] + x[i]); }

    // ══════════════════════ 인코딩 ══════════════════════

    private static void Encode12(ReadOnlySpan<short> f, Span<byte> dst)
    {
        for (var i = 0; i < N / 2; i++)
        {
            int a = Mod(f[2 * i]), b = Mod(f[2 * i + 1]);
            dst[3 * i] = (byte)a; dst[3 * i + 1] = (byte)(a >> 8 | b << 4); dst[3 * i + 2] = (byte)(b >> 4);
        }
    }

    private static void Decode12(ReadOnlySpan<byte> src, Span<short> f)
    {
        for (var i = 0; i < N / 2; i++)
        {
            f[2 * i] = (short)((src[3 * i] | (src[3 * i + 1] & 0x0F) << 8) % Q);
            f[2 * i + 1] = (short)((src[3 * i + 1] >> 4 | src[3 * i + 2] << 4) % Q);
        }
    }

    /// <summary>Compress_d(x) = ⌈(2^d/q)·x⌋ mod 2^d → ByteEncode_d.</summary>
    private static void CompressEncode(ReadOnlySpan<short> f, int d, Span<byte> dst)
    {
        dst[..(32 * d)].Clear();
        var bit = 0;
        for (var i = 0; i < N; i++)
        {
            var x = Mod(f[i]);
            var y = (int)((((long)x << d) + Q / 2) / Q) & ((1 << d) - 1);
            for (var b = 0; b < d; b++, bit++) dst[bit >> 3] |= (byte)(((y >> b) & 1) << (bit & 7));
        }
    }

    /// <summary>ByteDecode_d → Decompress_d(y) = ⌈(q/2^d)·y⌋.</summary>
    private static void DecodeDecompress(ReadOnlySpan<byte> src, int d, Span<short> f)
    {
        var bit = 0;
        for (var i = 0; i < N; i++)
        {
            var y = 0;
            for (var b = 0; b < d; b++, bit++) y |= ((src[bit >> 3] >> (bit & 7)) & 1) << b;
            f[i] = (short)(((long)y * Q + (1 << (d - 1))) >> d);
        }
    }
}
