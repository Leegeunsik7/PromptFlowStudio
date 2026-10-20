using System;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.Security;

/// <summary>
/// [PromptFlowStudio] 범용 로컬 암호기(외부 NuGet 0) — AES-256-GCM · <b>머신·사용자 바운드</b>.
///   키 = SHA-256(purpose + "|" + MachineName + "|" + UserName) → <b>동일 사용자·PC 에서만</b> 복호화 가능(설정 파일 이식 방지).
///   출력 = base64(nonce[12] | tag[16] | ciphertext). <paramref name="purpose"/> 로 용도별 키를 분리(SMTP/Wan 등).
///   ⚠ 머신·OS 변경 시 복호화가 실패한다 — 호출부는 반드시 빈 문자열 폴백을 처리할 것(<see cref="Unprotect"/> 는 예외를 던지지 않음).
///   ※ 기존 <c>SmtpLocalCipher</c> 의 알고리즘·포맷을 그대로 일반화한 것으로, 동일 purpose 면 바이트 단위로 동일한 키가 유도된다.
/// </summary>
public static class LocalCipher
{
    private const int NonceLen = 12;
    private const int TagLen = 16;

    /// <summary>평문을 용도(purpose) 바운드 키로 암호화 → base64 봉투(nonce|tag|cipher).</summary>
    public static string Protect(string plain, string purpose)
    {
        var key = DeriveKey(purpose);
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var pt = Encoding.UTF8.GetBytes(plain ?? "");
        var ct = new byte[pt.Length];
        var tag = new byte[TagLen];
        using (var aes = new AesGcm(key, TagLen))
            aes.Encrypt(nonce, pt, ct, tag);

        var buf = new byte[NonceLen + TagLen + ct.Length];
        Buffer.BlockCopy(nonce, 0, buf, 0, NonceLen);
        Buffer.BlockCopy(tag, 0, buf, NonceLen, TagLen);
        Buffer.BlockCopy(ct, 0, buf, NonceLen + TagLen, ct.Length);
        return Convert.ToBase64String(buf);
    }

    /// <summary>base64 봉투를 복호화 → 평문. ⚠ 키 불일치(타 PC/OS 재설치)·손상 시 <b>빈 문자열</b>(예외 없음 · Graceful).</summary>
    public static string Unprotect(string b64, string purpose)
    {
        try
        {
            var buf = Convert.FromBase64String(b64 ?? "");
            if (buf.Length < NonceLen + TagLen) return "";
            var key = DeriveKey(purpose);
            var nonce = buf.AsSpan(0, NonceLen);
            var tag = buf.AsSpan(NonceLen, TagLen);
            var ct = buf.AsSpan(NonceLen + TagLen);
            var pt = new byte[ct.Length];
            using (var aes = new AesGcm(key, TagLen))
                aes.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return ""; }   // 키 불일치·손상 → 빈 문자열(호출부가 폴백)
    }

    /// <summary>용도·머신·사용자 바운드 32바이트 키 유도(AES-256).</summary>
    private static byte[] DeriveKey(string purpose) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(
            (purpose ?? "") + "|" + SafeEnv(() => Environment.MachineName) + "|" + SafeEnv(() => Environment.UserName)));

    private static string SafeEnv(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }
}
