using System.Security.Cryptography;

namespace PromptFlowStudio.Services.Sovereign.Pqc;

/// <summary>
/// [PromptFlow Studio v16.53.0] ML-DSA-65 (FIPS 204 · CRYSTALS-Dilithium 표준화판) 순수 C# 구현.
///   ▸ q=8380417 · d=13 · τ=49 · λ=192 · γ1=2^19 · γ2=(q−1)/32 · (k,l)=(6,5) · η=4 · β=196 · ω=55 — pk 1952B · sk 4032B · sig 3309B.
///   ▸ 순수 서명(M' = 0 ‖ |ctx| ‖ ctx ‖ M · ctx ≤ 255B) · 헤지드(rnd 32B 난수) / 결정적(rnd=0) · 내부 변형 _Internal(KAT 검증용).
///   ▸ 해시: SHAKE128(ExpandA) · SHAKE256(H · ExpandS · ExpandMask · SampleInBall) — <see cref="KeccakState"/>.
///   ⚠ 부채널(타이밍) 하드닝은 최선 노력 — 감사받은 구현 대체 아님.
/// </summary>
public static class MlDsa65
{
    public const int N = 256, Q = 8380417, D = 13, Tau = 49, Lambda = 192, Gamma1 = 1 << 19, Gamma2 = (Q - 1) / 32, K = 6, L = 5, Eta = 4, Beta = Tau * Eta, Omega = 55;
    public const int CTildeSize = Lambda / 4;
    public const int PublicKeySize = 32 + 320 * K, PrivateKeySize = 128 + 128 * (K + L) + 416 * K, SignatureSize = CTildeSize + 640 * L + Omega + K;

    private static readonly int[] Zetas = new int[256];

    static MlDsa65()
    {
        for (var i = 0; i < 256; i++) Zetas[i] = (int)ModPow(1753, BitRev8(i));
    }

    private static int BitRev8(int x) { var r = 0; for (var i = 0; i < 8; i++) r |= ((x >> i) & 1) << (7 - i); return r; }

    private static long ModPow(long b, int e) { long r = 1; b %= Q; while (e > 0) { if ((e & 1) != 0) r = r * b % Q; b = b * b % Q; e >>= 1; } return r; }

    private static int Mod(long a) { var r = (int)(a % Q); return r < 0 ? r + Q : r; }

    /// <summary>r mod± α: (−α/2, α/2] 대표값.</summary>
    private static int ModPm(int r, int alpha) { var x = r % alpha; if (x < 0) x += alpha; return x > alpha / 2 ? x - alpha : x; }

    // ══════════════════════ 공개 API ══════════════════════

    public static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
    {
        Span<byte> xi = stackalloc byte[32];
        RandomNumberGenerator.Fill(xi);
        try { return KeyGenInternal(xi); }
        finally { CryptographicOperations.ZeroMemory(xi); }
    }

    public static byte[] Sign(ReadOnlySpan<byte> sk, ReadOnlySpan<byte> message, ReadOnlySpan<byte> context = default, bool deterministic = false)
    {
        if (context.Length > 255) throw new ArgumentException("ctx ≤ 255B", nameof(context));
        if (sk.Length != PrivateKeySize) throw new CryptographicException("ML-DSA: 개인키 길이 오류");
        Span<byte> rnd = stackalloc byte[32];
        if (deterministic) rnd.Clear(); else RandomNumberGenerator.Fill(rnd);
        return SignInternal(sk, FormatMessage(message, context), rnd);
    }

    public static bool Verify(ReadOnlySpan<byte> pk, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature, ReadOnlySpan<byte> context = default)
    {
        if (context.Length > 255 || pk.Length != PublicKeySize || signature.Length != SignatureSize) return false;
        return VerifyInternal(pk, FormatMessage(message, context), signature);
    }

    private static byte[] FormatMessage(ReadOnlySpan<byte> m, ReadOnlySpan<byte> ctx)
    {
        var mp = new byte[2 + ctx.Length + m.Length];
        mp[0] = 0; mp[1] = (byte)ctx.Length;
        ctx.CopyTo(mp.AsSpan(2)); m.CopyTo(mp.AsSpan(2 + ctx.Length));
        return mp;
    }

    // ══════════════════════ 내부 ══════════════════════

    public static (byte[] PublicKey, byte[] PrivateKey) KeyGenInternal(ReadOnlySpan<byte> xi)
    {
        Span<byte> seedIn = stackalloc byte[34];
        xi.CopyTo(seedIn); seedIn[32] = K; seedIn[33] = L;
        Span<byte> seeds = stackalloc byte[128];
        KeccakState.Shake256(seedIn, seeds);
        var rho = seeds[..32].ToArray(); var rhoP = seeds.Slice(32, 64); var key = seeds.Slice(96, 32);
        var a = ExpandA(rho);
        var s1 = new int[L * N]; var s2 = new int[K * N];
        ExpandS(rhoP, s1, s2);
        var s1Hat = (int[])s1.Clone();
        for (var i = 0; i < L; i++) Ntt(s1Hat.AsSpan(i * N, N));
        var t = new int[K * N];
        MatVec(a, s1Hat, t);
        for (var i = 0; i < K; i++) InvNtt(t.AsSpan(i * N, N));
        for (var i = 0; i < K * N; i++) t[i] = Mod((long)t[i] + s2[i]);
        var t1 = new int[K * N]; var t0 = new int[K * N];
        for (var i = 0; i < K * N; i++) { var r0 = ModPm(t[i], 1 << D); t0[i] = r0; t1[i] = (t[i] - r0) >> D; }
        var pk = new byte[PublicKeySize];
        rho.CopyTo(pk, 0);
        for (var i = 0; i < K; i++) SimpleBitPack(t1.AsSpan(i * N, N), 10, pk.AsSpan(32 + 320 * i, 320));
        Span<byte> tr = stackalloc byte[64];
        KeccakState.Shake256(pk, tr);
        var sk = new byte[PrivateKeySize];
        var o = 0;
        rho.CopyTo(sk, o); o += 32;
        key.CopyTo(sk.AsSpan(o)); o += 32;
        tr.CopyTo(sk.AsSpan(o)); o += 64;
        for (var i = 0; i < L; i++, o += 128) BitPack(s1.AsSpan(i * N, N), Eta, Eta, 4, sk.AsSpan(o, 128));
        for (var i = 0; i < K; i++, o += 128) BitPack(s2.AsSpan(i * N, N), Eta, Eta, 4, sk.AsSpan(o, 128));
        for (var i = 0; i < K; i++, o += 416) BitPack(t0.AsSpan(i * N, N), (1 << (D - 1)) - 1, 1 << (D - 1), 13, sk.AsSpan(o, 416));
        Array.Clear(s1); Array.Clear(s2); Array.Clear(s1Hat); Array.Clear(t0);
        CryptographicOperations.ZeroMemory(seeds);
        return (pk, sk);
    }

    public static byte[] SignInternal(ReadOnlySpan<byte> sk, ReadOnlySpan<byte> mPrime, ReadOnlySpan<byte> rnd)
    {
        var rho = sk[..32]; var key = sk.Slice(32, 32); var tr = sk.Slice(64, 64);
        var s1 = new int[L * N]; var s2 = new int[K * N]; var t0 = new int[K * N];
        var o = 128;
        for (var i = 0; i < L; i++, o += 128) BitUnpack(sk.Slice(o, 128), Eta, Eta, 4, s1.AsSpan(i * N, N));
        for (var i = 0; i < K; i++, o += 128) BitUnpack(sk.Slice(o, 128), Eta, Eta, 4, s2.AsSpan(i * N, N));
        for (var i = 0; i < K; i++, o += 416) BitUnpack(sk.Slice(o, 416), (1 << (D - 1)) - 1, 1 << (D - 1), 13, t0.AsSpan(i * N, N));
        for (var i = 0; i < L; i++) Ntt(s1.AsSpan(i * N, N));
        for (var i = 0; i < K; i++) { Ntt(s2.AsSpan(i * N, N)); Ntt(t0.AsSpan(i * N, N)); }
        var a = ExpandA(rho);
        Span<byte> mu = stackalloc byte[64];
        var h = KeccakState.Shake256(); h.Absorb(tr); h.Absorb(mPrime); h.Squeeze(mu);
        Span<byte> rhoPP = stackalloc byte[64];
        var h2 = KeccakState.Shake256(); h2.Absorb(key); h2.Absorb(rnd); h2.Absorb(mu); h2.Squeeze(rhoPP);
        var y = new int[L * N]; var yHat = new int[L * N]; var w = new int[K * N]; var w1 = new int[K * N];
        var cs1 = new int[L * N]; var cs2 = new int[K * N]; var ct0 = new int[K * N];
        var z = new int[L * N]; var hint = new int[K * N];
        var w1Enc = new byte[128 * K];
        Span<byte> cTilde = stackalloc byte[CTildeSize];
        Span<int> c = stackalloc int[N];
        for (var kappa = 0; kappa < 1000 * L; kappa += L)
        {
            ExpandMask(rhoPP, kappa, y);
            Array.Copy(y, yHat, y.Length);
            for (var i = 0; i < L; i++) Ntt(yHat.AsSpan(i * N, N));
            MatVec(a, yHat, w);
            for (var i = 0; i < K; i++) InvNtt(w.AsSpan(i * N, N));
            for (var i = 0; i < K * N; i++) w1[i] = HighBits(w[i]);
            for (var i = 0; i < K; i++) SimpleBitPack(w1.AsSpan(i * N, N), 4, w1Enc.AsSpan(128 * i, 128));
            var hc = KeccakState.Shake256(); hc.Absorb(mu); hc.Absorb(w1Enc); hc.Squeeze(cTilde);
            SampleInBall(cTilde, c);
            Ntt(c);
            for (var i = 0; i < L; i++) { PointMul(c, s1.AsSpan(i * N, N), cs1.AsSpan(i * N, N)); InvNtt(cs1.AsSpan(i * N, N)); }
            for (var i = 0; i < K; i++) { PointMul(c, s2.AsSpan(i * N, N), cs2.AsSpan(i * N, N)); InvNtt(cs2.AsSpan(i * N, N)); }
            for (var i = 0; i < L * N; i++) z[i] = Mod((long)y[i] + cs1[i]);
            if (InfNorm(z) >= Gamma1 - Beta) continue;
            var reject = false;
            for (var i = 0; i < K * N && !reject; i++) { var r0 = LowBits(Mod((long)w[i] - cs2[i])); if (Math.Abs(r0) >= Gamma2 - Beta) reject = true; }
            if (reject) continue;
            for (var i = 0; i < K; i++) { PointMul(c, t0.AsSpan(i * N, N), ct0.AsSpan(i * N, N)); InvNtt(ct0.AsSpan(i * N, N)); }
            if (InfNorm(ct0) >= Gamma2) continue;
            var ones = 0;
            for (var i = 0; i < K * N; i++)
            {
                var r = Mod((long)w[i] - cs2[i] + ct0[i]);
                hint[i] = HighBits(r) != HighBits(Mod((long)r - ct0[i])) ? 1 : 0;    // MakeHint(−ct0, w − cs2 + ct0)
                ones += hint[i];
            }
            if (ones > Omega) continue;
            var sig = new byte[SignatureSize];
            cTilde.CopyTo(sig);
            for (var i = 0; i < L; i++)
            {
                var zp = z.AsSpan(i * N, N);
                for (var j = 0; j < N; j++) zp[j] = ModPm(zp[j], Q);
                BitPack(zp, Gamma1 - 1, Gamma1, 20, sig.AsSpan(CTildeSize + 640 * i, 640));
            }
            HintBitPack(hint, sig.AsSpan(CTildeSize + 640 * L));
            Array.Clear(s1); Array.Clear(s2); Array.Clear(t0); Array.Clear(y); Array.Clear(yHat);
            return sig;
        }
        throw new CryptographicException("ML-DSA: 서명 반복 상한 초과");
    }

    public static bool VerifyInternal(ReadOnlySpan<byte> pk, ReadOnlySpan<byte> mPrime, ReadOnlySpan<byte> sig)
    {
        var rho = pk[..32];
        var t1 = new int[K * N];
        for (var i = 0; i < K; i++) SimpleBitUnpack(pk.Slice(32 + 320 * i, 320), 10, t1.AsSpan(i * N, N));
        var cTilde = sig[..CTildeSize];
        var z = new int[L * N];
        for (var i = 0; i < L; i++) BitUnpack(sig.Slice(CTildeSize + 640 * i, 640), Gamma1 - 1, Gamma1, 20, z.AsSpan(i * N, N));
        var hint = new int[K * N];
        if (!HintBitUnpack(sig[(CTildeSize + 640 * L)..], hint)) return false;
        for (var i = 0; i < L * N; i++) if (Math.Abs(z[i]) >= Gamma1 - Beta) return false;
        var a = ExpandA(rho);
        Span<byte> tr = stackalloc byte[64];
        KeccakState.Shake256(pk, tr);
        Span<byte> mu = stackalloc byte[64];
        var h = KeccakState.Shake256(); h.Absorb(tr); h.Absorb(mPrime); h.Squeeze(mu);
        Span<int> c = stackalloc int[N];
        SampleInBall(cTilde, c);
        Ntt(c);
        var zHat = new int[L * N];
        for (var i = 0; i < L * N; i++) zHat[i] = Mod(z[i]);
        for (var i = 0; i < L; i++) Ntt(zHat.AsSpan(i * N, N));
        var w = new int[K * N];
        MatVec(a, zHat, w);
        Span<int> ct1 = stackalloc int[N];
        for (var i = 0; i < K; i++)
        {
            var t1i = t1.AsSpan(i * N, N);
            for (var j = 0; j < N; j++) ct1[j] = Mod((long)t1i[j] << D);
            Ntt(ct1);
            PointMul(c, ct1, ct1);
            var wi = w.AsSpan(i * N, N);
            for (var j = 0; j < N; j++) wi[j] = Mod((long)wi[j] - ct1[j]);
            InvNtt(wi);
        }
        var w1Enc = new byte[128 * K];
        var w1 = new int[N];
        for (var i = 0; i < K; i++)
        {
            for (var j = 0; j < N; j++) w1[j] = UseHint(hint[i * N + j], w[i * N + j]);
            SimpleBitPack(w1, 4, w1Enc.AsSpan(128 * i, 128));
        }
        Span<byte> c2 = stackalloc byte[CTildeSize];
        var hc = KeccakState.Shake256(); hc.Absorb(mu); hc.Absorb(w1Enc); hc.Squeeze(c2);
        return CryptographicOperations.FixedTimeEquals(cTilde, c2);
    }

    // ══════════════════════ 샘플링 ══════════════════════

    private static int[] ExpandA(ReadOnlySpan<byte> rho)
    {
        var a = new int[K * L * N];
        Span<byte> seed = stackalloc byte[34];
        rho[..32].CopyTo(seed);
        Span<byte> buf = stackalloc byte[168];
        for (var r = 0; r < K; r++)
            for (var s = 0; s < L; s++)
            {
                seed[32] = (byte)s; seed[33] = (byte)r;
                var xof = KeccakState.Shake128(); xof.Absorb(seed);
                var f = a.AsSpan((r * L + s) * N, N);
                var n = 0;
                while (n < N)
                {
                    xof.Squeeze(buf);
                    for (var p = 0; p + 2 < buf.Length && n < N; p += 3)
                    {
                        var v = buf[p] | buf[p + 1] << 8 | (buf[p + 2] & 0x7F) << 16;
                        if (v < Q) f[n++] = v;
                    }
                }
            }
        return a;
    }

    private static void ExpandS(ReadOnlySpan<byte> rhoP, int[] s1, int[] s2)
    {
        Span<byte> seed = stackalloc byte[66];
        rhoP[..64].CopyTo(seed);
        Span<byte> buf = stackalloc byte[136];
        for (var r = 0; r < L + K; r++)
        {
            seed[64] = (byte)r; seed[65] = (byte)(r >> 8);
            var xof = KeccakState.Shake256(); xof.Absorb(seed);
            var f = r < L ? s1.AsSpan(r * N, N) : s2.AsSpan((r - L) * N, N);
            var n = 0;
            while (n < N)
            {
                xof.Squeeze(buf);
                for (var p = 0; p < buf.Length && n < N; p++)
                {
                    int z0 = buf[p] & 0x0F, z1 = buf[p] >> 4;
                    if (z0 < 9) f[n++] = Eta - z0;
                    if (z1 < 9 && n < N) f[n++] = Eta - z1;
                }
            }
            for (var i = 0; i < N; i++) f[i] = Mod(f[i]);
        }
    }

    private static void ExpandMask(ReadOnlySpan<byte> rhoPP, int kappa, int[] y)
    {
        Span<byte> seed = stackalloc byte[66];
        rhoPP[..64].CopyTo(seed);
        Span<byte> v = stackalloc byte[640];
        for (var r = 0; r < L; r++)
        {
            var idx = kappa + r;
            seed[64] = (byte)idx; seed[65] = (byte)(idx >> 8);
            KeccakState.Shake256(seed, v);
            BitUnpack(v, Gamma1 - 1, Gamma1, 20, y.AsSpan(r * N, N));
            for (var i = 0; i < N; i++) y[r * N + i] = Mod(y[r * N + i]);
        }
    }

    private static void SampleInBall(ReadOnlySpan<byte> rho, Span<int> c)
    {
        c.Clear();
        var xof = KeccakState.Shake256(); xof.Absorb(rho);
        Span<byte> s = stackalloc byte[8];
        xof.Squeeze(s);
        var signs = BitConverter.ToUInt64(s);
        Span<byte> one = stackalloc byte[1];
        for (var i = N - Tau; i < N; i++)
        {
            int j;
            do { xof.Squeeze(one); j = one[0]; } while (j > i);
            c[i] = c[j];
            c[j] = ((signs >> (i + Tau - N)) & 1) == 0 ? 1 : Q - 1;
        }
    }

    // ══════════════════════ 산술 ══════════════════════

    private static int HighBits(int r) { Decompose(r, out var r1, out _); return r1; }
    private static int LowBits(int r) { Decompose(r, out _, out var r0); return r0; }

    private static void Decompose(int r, out int r1, out int r0)
    {
        var rp = Mod(r);
        r0 = ModPm(rp, 2 * Gamma2);
        if (rp - r0 == Q - 1) { r1 = 0; r0 -= 1; }
        else r1 = (rp - r0) / (2 * Gamma2);
    }

    private static int UseHint(int h, int r)
    {
        const int m = (Q - 1) / (2 * Gamma2);
        Decompose(r, out var r1, out var r0);
        if (h == 1 && r0 > 0) return (r1 + 1) % m;
        if (h == 1 && r0 <= 0) return (r1 - 1 + m) % m;
        return r1;
    }

    private static int InfNorm(int[] v) { var m = 0; foreach (var x in v) { var a = Math.Abs(ModPm(x, Q)); if (a > m) m = a; } return m; }

    private static void MatVec(int[] a, int[] vHat, int[] dst)
    {
        Span<int> tmp = stackalloc int[N];
        for (var r = 0; r < K; r++)
        {
            var acc = dst.AsSpan(r * N, N);
            acc.Clear();
            for (var s = 0; s < L; s++)
            {
                PointMul(a.AsSpan((r * L + s) * N, N), vHat.AsSpan(s * N, N), tmp);
                for (var j = 0; j < N; j++) acc[j] = Mod((long)acc[j] + tmp[j]);
            }
        }
    }

    private static void PointMul(ReadOnlySpan<int> a, ReadOnlySpan<int> b, Span<int> dst) { for (var j = 0; j < N; j++) dst[j] = (int)((long)a[j] * b[j] % Q); }

    private static void Ntt(Span<int> w)
    {
        var m = 0;
        for (var len = 128; len >= 1; len >>= 1)
            for (var start = 0; start < N; start += 2 * len)
            {
                long z = Zetas[++m];
                for (var j = start; j < start + len; j++)
                {
                    var t = (int)(z * w[j + len] % Q);
                    w[j + len] = Mod((long)w[j] - t);
                    w[j] = Mod((long)w[j] + t);
                }
            }
    }

    private static void InvNtt(Span<int> w)
    {
        var m = 256;
        for (var len = 1; len < 256; len <<= 1)
            for (var start = 0; start < N; start += 2 * len)
            {
                long z = Q - Zetas[--m];
                for (var j = start; j < start + len; j++)
                {
                    var t = w[j];
                    w[j] = Mod((long)t + w[j + len]);
                    w[j + len] = (int)(z * Mod((long)t - w[j + len]) % Q);
                }
            }
        const long f = 8347681;
        for (var j = 0; j < N; j++) w[j] = (int)(f * w[j] % Q);
    }

    // ══════════════════════ 비트 패킹 ══════════════════════

    private static void SimpleBitPack(ReadOnlySpan<int> w, int bits, Span<byte> dst)
    {
        dst.Clear();
        var bit = 0;
        foreach (var x in w) for (var b = 0; b < bits; b++, bit++) dst[bit >> 3] |= (byte)(((x >> b) & 1) << (bit & 7));
    }

    private static void SimpleBitUnpack(ReadOnlySpan<byte> src, int bits, Span<int> w)
    {
        var bit = 0;
        for (var i = 0; i < N; i++) { var x = 0; for (var b = 0; b < bits; b++, bit++) x |= ((src[bit >> 3] >> (bit & 7)) & 1) << b; w[i] = x; }
    }

    /// <summary>BitPack(w, a, b): b − w_i 를 bits 비트로(입력 w 는 mod q 또는 부호 정수 모두 허용).</summary>
    private static void BitPack(ReadOnlySpan<int> w, int a, int b, int bits, Span<byte> dst)
    {
        dst.Clear();
        var bit = 0;
        foreach (var x0 in w)
        {
            var x = ModPm(x0, Q);
            var v = b - x;
            for (var k = 0; k < bits; k++, bit++) dst[bit >> 3] |= (byte)(((v >> k) & 1) << (bit & 7));
        }
    }

    /// <summary>BitUnpack → 부호 정수 w_i = b − v.</summary>
    private static void BitUnpack(ReadOnlySpan<byte> src, int a, int b, int bits, Span<int> w)
    {
        var bit = 0;
        for (var i = 0; i < N; i++) { var v = 0; for (var k = 0; k < bits; k++, bit++) v |= ((src[bit >> 3] >> (bit & 7)) & 1) << k; w[i] = b - v; }
    }

    private static void HintBitPack(int[] h, Span<byte> y)
    {
        y[..(Omega + K)].Clear();
        var index = 0;
        for (var i = 0; i < K; i++)
        {
            for (var j = 0; j < N; j++) if (h[i * N + j] != 0) y[index++] = (byte)j;
            y[Omega + i] = (byte)index;
        }
    }

    private static bool HintBitUnpack(ReadOnlySpan<byte> y, int[] h)
    {
        Array.Clear(h);
        var index = 0;
        for (var i = 0; i < K; i++)
        {
            if (y[Omega + i] < index || y[Omega + i] > Omega) return false;
            var first = index;
            while (index < y[Omega + i])
            {
                if (index > first && y[index - 1] >= y[index]) return false;
                h[i * N + y[index]] = 1;
                index++;
            }
        }
        for (var i = index; i < Omega; i++) if (y[i] != 0) return false;
        return true;
    }
}
