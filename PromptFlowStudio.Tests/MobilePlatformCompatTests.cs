using System;
using System.Security.Cryptography;
using PromptFlowStudio.Services;

namespace PromptFlowStudio.Tests;

/// <summary>v17.63.2 — 모바일(iOS 계열) 격리 시임: 관리형 GCM 폴백이 네이티브 AesGcm 과 바이트 단위 동일해야 한다.</summary>
public class AesGcmCompatTests
{
    [Theory]
    [InlineData(32, 0, 0)]
    [InlineData(32, 1, 0)]
    [InlineData(32, 16, 13)]
    [InlineData(16, 255, 20)]
    [InlineData(24, 1000, 33)]
    public void Managed_Matches_Native(int keyLen, int ptLen, int aadLen)
    {
        var rng = new Random(keyLen * 7919 + ptLen * 31 + aadLen);
        var key = new byte[keyLen]; rng.NextBytes(key);
        var nonce = new byte[12]; rng.NextBytes(nonce);
        var pt = new byte[ptLen]; rng.NextBytes(pt);
        var aad = new byte[aadLen]; rng.NextBytes(aad);

        var ctN = new byte[ptLen]; var tagN = new byte[16];
        using (var n = new AesGcm(key, 16)) n.Encrypt(nonce, pt, ctN, tagN, aad);

        var ctM = new byte[ptLen]; var tagM = new byte[16];
        using var m = new AesGcmCompat(key, 16, forceManaged: true);
        Assert.True(m.IsManaged);
        m.Encrypt(nonce, pt, ctM, tagM, aad);
        Assert.Equal(ctN, ctM);
        Assert.Equal(tagN, tagM);

        var back = new byte[ptLen];
        m.Decrypt(nonce, ctM, tagM, back, aad);
        Assert.Equal(pt, back);
    }

    [Fact]
    public void Managed_Rejects_Tampered_Tag()
    {
        var key = new byte[32]; var nonce = new byte[12]; var pt = new byte[40];
        var ct = new byte[40]; var tag = new byte[16];
        using var m = new AesGcmCompat(key, 16, forceManaged: true);
        m.Encrypt(nonce, pt, ct, tag);
        tag[3] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => m.Decrypt(nonce, ct, tag, new byte[40]));
    }

    [Fact]
    public void Desktop_Uses_Native_And_Process_Supported()
    {
        using var g = new AesGcmCompat(new byte[32], 16);
        Assert.False(g.IsManaged);
        Assert.True(ProcessCompat.IsSupported);
    }
}
