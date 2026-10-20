using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace PromptFlowStudio.Services;

/// <summary>
/// [v17.63.2] AES-GCM 플랫폼 격리 시임 — iOS/MacCatalyst/tvOS 는 <see cref="AesGcm"/> 미지원(PlatformNotSupportedException).
///   네이티브 지원 OS(Windows/Android/Linux/macOS) = <see cref="AesGcm"/> 그대로 위임(동작·포맷 불변).
///   미지원 OS = 관리형 GCM 폴백(NIST SP 800-38D · AES-ECB 블록 + GHASH 상수시간 GF(2^128) 곱) — 동일 키·논스·AAD 에서 바이트 단위 동일 출력.
///   API 표면은 <see cref="AesGcm"/> 의 Span 오버로드와 동일 → 호출부는 타입명만 교체. 핫패스 할당 0(stackalloc).
/// </summary>
public sealed class AesGcmCompat : IDisposable
{
    private const int NonceSize = 12;
    private const int BlockSize = 16;
    private const int CtrChunk = 256;

    private readonly AesGcm? _native;
    private readonly Aes? _ecb;
    private readonly ulong _hHi, _hLo;
    private readonly int _tagSize;

    /// <summary>관리형 폴백이 있으므로 모든 플랫폼에서 true.</summary>
    public static bool IsSupported => true;

    [UnsupportedOSPlatformGuard("ios")]
    [UnsupportedOSPlatformGuard("maccatalyst")]
    [UnsupportedOSPlatformGuard("tvos")]
    [UnsupportedOSPlatformGuard("browser")]
    private static bool NativeAvailable
    {
        get
        {
            if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS() || OperatingSystem.IsBrowser()) return false;
            return AesGcm.IsSupported;
        }
    }

    /// <summary>현재 인스턴스가 관리형 폴백 경로인가(진단/테스트용).</summary>
    public bool IsManaged => _native is null;

    public AesGcmCompat(ReadOnlySpan<byte> key, int tagSizeInBytes) : this(key, tagSizeInBytes, forceManaged: false) { }

    /// <summary><paramref name="forceManaged"/>=true 면 네이티브 지원 OS 에서도 관리형 경로 강제(교차 검증용).</summary>
    internal AesGcmCompat(ReadOnlySpan<byte> key, int tagSizeInBytes, bool forceManaged)
    {
        if (tagSizeInBytes < 12 || tagSizeInBytes > BlockSize) throw new ArgumentException("Invalid tag size.", nameof(tagSizeInBytes));
        if (key.Length is not (16 or 24 or 32)) throw new CryptographicException("Specified key is not a valid size for this algorithm.");
        _tagSize = tagSizeInBytes;
        if (!forceManaged && NativeAvailable)
        {
            _native = new AesGcm(key, tagSizeInBytes);
            return;
        }
        _ecb = Aes.Create();
        _ecb.Key = key.ToArray();   // 생성 시 1회(핫패스 아님)
        Span<byte> zero = stackalloc byte[BlockSize];
        Span<byte> h = stackalloc byte[BlockSize];
        zero.Clear();
        _ecb.EncryptEcb(zero, h, PaddingMode.None);
        _hHi = BinaryPrimitives.ReadUInt64BigEndian(h);
        _hLo = BinaryPrimitives.ReadUInt64BigEndian(h[8..]);
        CryptographicOperations.ZeroMemory(h);
    }

    public void Encrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> plaintext, Span<byte> ciphertext, Span<byte> tag, ReadOnlySpan<byte> associatedData = default)
    {
        if (NativeAvailable && _native is not null) { _native.Encrypt(nonce, plaintext, ciphertext, tag, associatedData); return; }
        Validate(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        Ctr(nonce, plaintext, ciphertext);
        ComputeTag(nonce, ciphertext, associatedData, tag);
    }

    public void Decrypt(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag, Span<byte> plaintext, ReadOnlySpan<byte> associatedData = default)
    {
        if (NativeAvailable && _native is not null) { _native.Decrypt(nonce, ciphertext, tag, plaintext, associatedData); return; }
        Validate(nonce, plaintext.Length, ciphertext.Length, tag.Length);
        Span<byte> expected = stackalloc byte[BlockSize];
        ComputeTag(nonce, ciphertext, associatedData, expected[..tag.Length]);
        if (!CryptographicOperations.FixedTimeEquals(expected[..tag.Length], tag))
        {
            plaintext.Clear();
            throw new AuthenticationTagMismatchException();
        }
        Ctr(nonce, ciphertext, plaintext);
    }

    public void Dispose()
    {
        if (NativeAvailable && _native is not null) _native.Dispose();
        _ecb?.Dispose();
    }

    private void Validate(ReadOnlySpan<byte> nonce, int ptLen, int ctLen, int tagLen)
    {
        if (nonce.Length != NonceSize) throw new ArgumentException("The specified nonce is not a valid size for this algorithm.", nameof(nonce));
        if (ptLen != ctLen) throw new ArgumentException("Plaintext and ciphertext must have the same length.");
        if (tagLen != _tagSize) throw new ArgumentException("The specified tag is not a valid size for this algorithm.", nameof(tagLen));
    }

    /// <summary>GCTR: J0+1 부터 inc32 카운터 블록을 청크 단위 ECB 일괄 암호화 후 XOR(입출력 동일 버퍼 허용).</summary>
    private void Ctr(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> ctr = stackalloc byte[CtrChunk];
        Span<byte> ks = stackalloc byte[CtrChunk];
        uint c = 1;
        var off = 0;
        while (off < input.Length)
        {
            var n = Math.Min(CtrChunk, input.Length - off);
            var blocks = (n + BlockSize - 1) / BlockSize;
            for (var b = 0; b < blocks; b++)
            {
                var blk = ctr.Slice(b * BlockSize, BlockSize);
                nonce.CopyTo(blk);
                c = unchecked(c + 1);
                BinaryPrimitives.WriteUInt32BigEndian(blk[NonceSize..], c);
            }
            _ecb!.EncryptEcb(ctr[..(blocks * BlockSize)], ks, PaddingMode.None);
            for (var i = 0; i < n; i++) output[off + i] = (byte)(input[off + i] ^ ks[i]);
            off += n;
        }
        CryptographicOperations.ZeroMemory(ks);
    }

    /// <summary>T = MSB_t(E_K(J0) ⊕ GHASH_H(A ‖ C ‖ len(A) ‖ len(C))).</summary>
    private void ComputeTag(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> aad, Span<byte> tag)
    {
        ulong yHi = 0, yLo = 0;
        Ghash(ref yHi, ref yLo, aad);
        Ghash(ref yHi, ref yLo, ciphertext);
        yHi ^= (ulong)aad.Length * 8;
        yLo ^= (ulong)ciphertext.Length * 8;
        GfMul(ref yHi, ref yLo, _hHi, _hLo);

        Span<byte> j0 = stackalloc byte[BlockSize];
        Span<byte> ek = stackalloc byte[BlockSize];
        nonce.CopyTo(j0);
        BinaryPrimitives.WriteUInt32BigEndian(j0[NonceSize..], 1);
        _ecb!.EncryptEcb(j0, ek, PaddingMode.None);
        BinaryPrimitives.WriteUInt64BigEndian(j0, yHi);
        BinaryPrimitives.WriteUInt64BigEndian(j0[8..], yLo);
        for (var i = 0; i < tag.Length; i++) tag[i] = (byte)(ek[i] ^ j0[i]);
        CryptographicOperations.ZeroMemory(ek);
    }

    private void Ghash(ref ulong yHi, ref ulong yLo, ReadOnlySpan<byte> data)
    {
        Span<byte> pad = stackalloc byte[BlockSize];
        while (data.Length > 0)
        {
            scoped ReadOnlySpan<byte> blk;
            if (data.Length >= BlockSize) { blk = data[..BlockSize]; data = data[BlockSize..]; }
            else { pad.Clear(); data.CopyTo(pad); blk = pad; data = default; }
            yHi ^= BinaryPrimitives.ReadUInt64BigEndian(blk);
            yLo ^= BinaryPrimitives.ReadUInt64BigEndian(blk[8..]);
            GfMul(ref yHi, ref yLo, _hHi, _hLo);
        }
    }

    /// <summary>GF(2^128) 곱(GCM 비트 반사 표현 · R = 0xE1‖0^120) — 분기 없는 마스크 연산(상수시간).</summary>
    private static void GfMul(ref ulong xHi, ref ulong xLo, ulong hHi, ulong hLo)
    {
        ulong zHi = 0, zLo = 0, vHi = hHi, vLo = hLo;
        for (var i = 0; i < 128; i++)
        {
            var bit = i < 64 ? (xHi >> (63 - i)) & 1UL : (xLo >> (127 - i)) & 1UL;
            var m = 0UL - bit;
            zHi ^= vHi & m;
            zLo ^= vLo & m;
            var r = 0UL - (vLo & 1UL);
            vLo = (vLo >> 1) | (vHi << 63);
            vHi = (vHi >> 1) ^ (0xE100000000000000UL & r);
        }
        xHi = zHi;
        xLo = zLo;
    }
}
