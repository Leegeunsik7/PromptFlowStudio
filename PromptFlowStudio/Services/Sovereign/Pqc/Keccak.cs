using System.Numerics;

namespace PromptFlowStudio.Services.Sovereign.Pqc;

/// <summary>
/// [PromptFlow Studio v16.53.0] FIPS 202 Keccak-f[1600] 순수 C# 구현 — SHA3-256/512 · SHAKE128/256(증분 XOF).
///   Windows 10 CNG 는 SHA3/SHAKE 미지원(SHA3_256.IsSupported=false) → ML-KEM/ML-DSA 용 BCL 폴백. 상태는 값 형식(무할당 흡수/압출).
/// </summary>
public struct KeccakState
{
    private static readonly ulong[] RoundConstants =
    {
        0x0000000000000001UL, 0x0000000000008082UL, 0x800000000000808AUL, 0x8000000080008000UL, 0x000000000000808BUL, 0x0000000080000001UL,
        0x8000000080008081UL, 0x8000000000008009UL, 0x000000000000008AUL, 0x0000000000000088UL, 0x0000000080008009UL, 0x000000008000000AUL,
        0x000000008000808BUL, 0x800000000000008BUL, 0x8000000000008089UL, 0x8000000000008003UL, 0x8000000000008002UL, 0x8000000000000080UL,
        0x000000000000800AUL, 0x800000008000000AUL, 0x8000000080008081UL, 0x8000000000008080UL, 0x0000000080000001UL, 0x8000000080008008UL,
    };

    private static readonly int[] Rho = { 0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14 };

    private Lanes _a;
    private int _rate, _pos;
    private byte _suffix;
    private bool _squeezing;

    [System.Runtime.CompilerServices.InlineArray(25)]
    private struct Lanes { private ulong _e0; }

    public static KeccakState Create(int rateBytes, byte suffix) => new() { _rate = rateBytes, _suffix = suffix };

    public static KeccakState Shake128() => Create(168, 0x1F);
    public static KeccakState Shake256() => Create(136, 0x1F);
    public static KeccakState Sha3_256() => Create(136, 0x06);
    public static KeccakState Sha3_512() => Create(72, 0x06);

    public void Absorb(ReadOnlySpan<byte> data)
    {
        if (_squeezing) throw new InvalidOperationException("압출 시작 후 흡수 불가");
        Span<byte> st = System.Runtime.InteropServices.MemoryMarshal.AsBytes((Span<ulong>)_a);
        foreach (var b in data)
        {
            st[_pos++] ^= b;
            if (_pos == _rate) { Permute(); _pos = 0; }
        }
    }

    public void Squeeze(Span<byte> output)
    {
        Span<byte> st = System.Runtime.InteropServices.MemoryMarshal.AsBytes((Span<ulong>)_a);
        if (!_squeezing)
        {
            st[_pos] ^= _suffix;
            st[_rate - 1] ^= 0x80;
            Permute();
            _pos = 0; _squeezing = true;
        }
        for (var i = 0; i < output.Length; i++)
        {
            if (_pos == _rate) { Permute(); _pos = 0; }
            output[i] = st[_pos++];
        }
    }

    private void Permute()
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Keccak: 리틀 엔디언 전용");
        Span<ulong> a = _a;
        Span<ulong> c = stackalloc ulong[5];
        Span<ulong> b = stackalloc ulong[25];
        for (var round = 0; round < 24; round++)
        {
            for (var x = 0; x < 5; x++) c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
            for (var x = 0; x < 5; x++)
            {
                var d = c[(x + 4) % 5] ^ BitOperations.RotateLeft(c[(x + 1) % 5], 1);
                for (var y = 0; y < 25; y += 5) a[y + x] ^= d;
            }
            for (var x = 0; x < 5; x++)
                for (var y = 0; y < 5; y++)
                    b[y + 5 * ((2 * x + 3 * y) % 5)] = BitOperations.RotateLeft(a[x + 5 * y], Rho[x + 5 * y]);
            for (var y = 0; y < 25; y += 5)
                for (var x = 0; x < 5; x++)
                    a[y + x] = b[y + x] ^ (~b[y + (x + 1) % 5] & b[y + (x + 2) % 5]);
            a[0] ^= RoundConstants[round];
        }
    }

    // ── 원샷 헬퍼 ──
    public static void Sha3_256(ReadOnlySpan<byte> data, Span<byte> dst32) { var k = Sha3_256(); k.Absorb(data); k.Squeeze(dst32[..32]); }
    public static void Sha3_512(ReadOnlySpan<byte> data, Span<byte> dst64) { var k = Sha3_512(); k.Absorb(data); k.Squeeze(dst64[..64]); }
    public static void Shake128(ReadOnlySpan<byte> data, Span<byte> dst) { var k = Shake128(); k.Absorb(data); k.Squeeze(dst); }
    public static void Shake256(ReadOnlySpan<byte> data, Span<byte> dst) { var k = Shake256(); k.Absorb(data); k.Squeeze(dst); }

}
