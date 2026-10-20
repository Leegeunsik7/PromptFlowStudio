using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Integration;

/// <summary>C2PA 클레임(불변) — 테넌트/프롬프트 시드/생성 시각/생성기.</summary>
public sealed record C2paClaim(string TenantId, string PromptSeed, long TimestampMs, string Generator = "PromptFlow Studio v8");

/// <summary>서명된 C2PA 매니페스트 — 클레임 JSON + 콘텐츠 해시(하드 바인딩) + 서명 + JUMBF 박스(base64).</summary>
public sealed record C2paManifest(string ClaimJson, string ContentHashHex, string SignatureHex, string JumbfBase64);

/// <summary>
/// [PromptFlow Studio v8.0.0] C2PA 위변조 방지 암호화 워터마크 엔진(관찰 가능 싱글턴 · 순수 BCL <see cref="System.Security.Cryptography"/> · 외부 C2PA DLL 0).
///   ▸ 최종 자산에 테넌트 ID·타임스탬프·프롬프트 시드를 담은 클레임을 만들고, 콘텐츠 SHA-256 <b>하드 바인딩</b> + HMAC-SHA256 서명으로 봉인.
///   ▸ 매니페스트를 최소 JUMBF 슈퍼박스로 직렬화(base64) — C2PA 규격 구조 근사.
///   ▸ <see cref="VerifyManifest"/> 검증 실패(서명/해시 불일치) 시 <see cref="OnSecurityAlert"/> + <see cref="OnChange"/> 로 즉시 보안 경고 발화(HUD 소비).
/// </summary>
public sealed class C2PaWatermarkEngine
{
    private string _secret = "promptflow-c2pa-provenance-secret";

    public event Action? OnChange;
    /// <summary>검증 실패 보안 경고(HUD 즉시 표출).</summary>
    public event Action<string>? OnSecurityAlert;

    public long SignedCount { get; private set; }
    public long VerifiedOk { get; private set; }
    public long TamperCount { get; private set; }
    public string? LastAlert { get; private set; }
    public string? LastSignedTenant { get; private set; }

    private void Notify() => OnChange?.Invoke();

    /// <summary>서명 시크릿 설정(테넌트별 프로비넌스 키).</summary>
    public void ConfigureSecret(string? secret) { if (!string.IsNullOrWhiteSpace(secret)) _secret = secret!.Trim(); }

    /// <summary>
    /// 클레임을 서명해 C2PA 매니페스트를 생성한다. <paramref name="assetBytes"/> 가 있으면 그 해시로 하드 바인딩, 없으면 클레임 해시.
    /// </summary>
    public C2paManifest SignManifest(C2paClaim claim, byte[]? assetBytes = null)
    {
        var claimJson = JsonSerializer.Serialize(claim, PromptFlowJsonContext.Default.C2paClaim);
        var contentHash = Sha256Hex(assetBytes ?? Encoding.UTF8.GetBytes(claimJson));
        var signature = HmacHex(_secret, claimJson + "." + contentHash);
        var jumbf = BuildJumbf(claimJson, signature);
        SignedCount++; LastSignedTenant = claim.TenantId;
        Notify();
        return new C2paManifest(claimJson, contentHash, signature, jumbf);
    }

    /// <summary>
    /// 매니페스트 검증 — 서명 재계산(상수시간 비교) + (선택) 현재 자산 해시 대조. 실패 시 보안 경고 발화. 반환=무결 여부.
    /// </summary>
    public bool VerifyManifest(C2paManifest manifest, byte[]? currentAssetBytes = null)
    {
        if (manifest is null) { RaiseAlert("매니페스트 없음"); return false; }
        var expectedSig = HmacHex(_secret, manifest.ClaimJson + "." + manifest.ContentHashHex);
        var sigOk = FixedEquals(expectedSig, manifest.SignatureHex);
        if (!sigOk) { RaiseAlert("C2PA 서명 불일치 — 매니페스트 위변조 의심"); return false; }

        if (currentAssetBytes is not null)
        {
            var nowHash = Sha256Hex(currentAssetBytes);
            if (!FixedEquals(nowHash, manifest.ContentHashHex)) { RaiseAlert("콘텐츠 해시 불일치 — 자산 변조 감지"); return false; }
        }
        VerifiedOk++; Notify();
        return true;
    }

    private void RaiseAlert(string message)
    {
        TamperCount++; LastAlert = message;
        try { OnSecurityAlert?.Invoke(message); } catch { /* 구독자 예외 격리 */ }
        Notify();
    }

    // ── 암호화 헬퍼(BCL) ──
    private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data ?? Array.Empty<byte>())).ToLowerInvariant();

    private static string HmacHex(string secret, string message)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    private static bool FixedEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b ?? ""));

    /// <summary>최소 JUMBF 슈퍼박스 직렬화(길이 + "jumb" + 타입 UUID + 페이로드) → base64. C2PA 박스 구조 근사.</summary>
    private static string BuildJumbf(string claimJson, string signature)
    {
        var payload = Encoding.UTF8.GetBytes("c2pa.claim\0" + claimJson + "\0sig:" + signature);
        // JUMBF 박스 헤더: [4B length][4B "jumb"] + 슈퍼박스 타입 uuid(16B) + payload.
        var typeUuid = new byte[16];
        var tag = "c2pa-claimblk";
        for (var i = 0; i < tag.Length && i < 16; i++) typeUuid[i] = (byte)tag[i];
        var total = 8 + 16 + payload.Length;
        var box = new byte[total];
        var len = BitConverter.GetBytes(total);
        if (BitConverter.IsLittleEndian) Array.Reverse(len);   // JUMBF 는 빅엔디언 길이
        Array.Copy(len, 0, box, 0, 4);
        Encoding.ASCII.GetBytes("jumb").CopyTo(box, 4);
        Array.Copy(typeUuid, 0, box, 8, 16);
        Array.Copy(payload, 0, box, 24, payload.Length);
        return Convert.ToBase64String(box);
    }
}
