using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Account;

/// <summary>라이선스 검증 결과.</summary>
public sealed record LicenseInfo(string Licensee, string IssuedAt, string Expiry, bool Valid, string Reason);

/// <summary>발급 명부 레코드(배포자 관리용).</summary>
public sealed record LicenseRecord(string Id, string Licensee, string Key, string IssuedAt, string Expiry);

/// <summary>
/// [PromptFlowStudio v1] 오프라인 배포 라이선스(회원가입 게이트) — 중앙 서버 없이 무단 설치 차단.
///   • 배포자(발급자)만 RSA 개인키로 라이선스 키를 발급(발급기 EXE는 개인키 내장, 앱 관리자 재발급은 license_signing.dat 사용).
///   • 모든 수령자 앱은 배포본 내장 공개키(appsettings License:PublicKey)로 서명 검증 → 유효 키 없으면 가입 불가.
///   외부 NuGet 0(System.Security.Cryptography BCL RSA만). 모니터링/스마트팜/PLAI/UAV/Stock와 동일 설계 · PromptFlowStudio 전용 키.
/// </summary>
public sealed class LicenseService
{
    private readonly IConfiguration _cfg;
    private readonly string _privPath;      // 배포자 서명키(고정시드 암호화 · 이 PC에만)
    private readonly string _registryPath;  // 발급 명부(관리용)
    private readonly object _lock = new();

    public LicenseService(IConfiguration cfg)
    {
        _cfg = cfg;
        var root = DataAreaRoot();
        _privPath = Path.Combine(root, "license_signing.dat");
        _registryPath = Path.Combine(root, "license_registry.json");
    }

    /// <summary>PromptFlowStudio 데이터 영역(쓰기 가능) — %LocalAppData%\PromptFlowStudio.</summary>
    public static string DataAreaRoot()
    {
        var b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(b)) b = Path.GetTempPath();
        var d = Path.Combine(b, "PromptFlowStudio");
        try { Directory.CreateDirectory(d); } catch { /* 경로만 반환 */ }
        return d;
    }

    /// <summary>배포 라이선스 게이트 활성 여부(appsettings License:Enabled).</summary>
    public bool EnforcementOn => string.Equals(_cfg["License:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>이 PC가 발급자 머신인지 — 서명키 파일 보유 시 true.</summary>
    public bool IsIssuerMachine => File.Exists(_privPath);

    /// <summary>발급 명부 파일 경로(관리자 표시용).</summary>
    public string RegistryPath => _registryPath;

    // ─────────────── 검증(모든 PC) ───────────────
    public LicenseInfo VerifyKey(string? licenseKey, string? publicKeyOverride = null)
    {
        try
        {
            var parts = (licenseKey ?? "").Trim().Split('.');
            if (parts.Length != 2) return new("", "", "", false, "형식 오류(payload.signature 아님)");
            var payloadBytes = FromB64Url(parts[0]);
            var sig = FromB64Url(parts[1]);

            var pub = string.IsNullOrWhiteSpace(publicKeyOverride) ? EffectivePublicKey() : publicKeyOverride!;
            if (string.IsNullOrWhiteSpace(pub)) return new("", "", "", false, "공개키 미설정(License:PublicKey)");

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pub), out _);
            if (!rsa.VerifyData(payloadBytes, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return new("", "", "", false, "서명 불일치(위조되었거나 다른 배포자 키)");

            var p = JsonSerializer.Deserialize(Encoding.UTF8.GetString(payloadBytes), PromptFlowJsonContext.Default.LicenseServicePayload);
            if (p is null) return new("", "", "", false, "페이로드 파싱 실패");

            if (!string.IsNullOrEmpty(p.exp)
                && DateTime.TryParseExact(p.exp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exp)
                && DateTime.Now.Date > exp.Date)
                return new(p.n ?? "", p.iat ?? "", p.exp ?? "", false, $"만료됨({FormatYmd(p.exp)})");

            return new(p.n ?? "", p.iat ?? "", p.exp ?? "", true, "유효");
        }
        catch (Exception ex) { return new("", "", "", false, "검증 오류: " + ex.Message); }
    }

    // ─────────────── 발급(관리자 재발급 · 발급자 PC) ───────────────
    /// <summary>라이선스 키 발급(개인키 서명) — 대상명 + 만료일(선택). 수령자에게 전달할 문자열 반환.</summary>
    public string IssueLicense(string licensee, DateTime? expiry)
    {
        lock (_lock)
        {
            using var rsa = LoadPrivate();
            var p = new Payload
            {
                n = (licensee ?? "").Trim(),
                iat = DateTime.Now.ToString("yyyyMMdd"),
                exp = expiry?.ToString("yyyyMMdd") ?? "",
                id = Guid.NewGuid().ToString("N").Substring(0, 8),
            };
            var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(p, PromptFlowJsonContext.Default.LicenseServicePayload);
            var sig = rsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var key = ToB64Url(payloadBytes) + "." + ToB64Url(sig);
            AppendRegistry(new LicenseRecord(p.id ?? "", p.n ?? "", key, p.iat ?? "", p.exp ?? ""));
            return key;
        }
    }

    /// <summary>이 PC에서 발급한 라이선스 목록(최신순).</summary>
    public List<LicenseRecord> ListIssued()
    {
        try
        {
            if (!File.Exists(_registryPath)) return new();
            var list = JsonSerializer.Deserialize(File.ReadAllText(_registryPath), PromptFlowJsonContext.Default.LicenseRecordList) ?? new();
            list.Reverse();
            return list;
        }
        catch { return new(); }
    }

    public bool RemoveIssued(string id)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_registryPath)) return false;
                var list = JsonSerializer.Deserialize(File.ReadAllText(_registryPath), PromptFlowJsonContext.Default.LicenseRecordList) ?? new();
                if (list.RemoveAll(r => r.Id == id) == 0) return false;
                WriteRegistry(list);
                return true;
            }
            catch { return false; }
        }
    }

    private void AppendRegistry(LicenseRecord rec)
    {
        try
        {
            var list = File.Exists(_registryPath)
                ? (JsonSerializer.Deserialize(File.ReadAllText(_registryPath), PromptFlowJsonContext.Default.LicenseRecordList) ?? new())
                : new();
            list.Add(rec);
            WriteRegistry(list);
        }
        catch { /* 명부 적재 실패는 발급 자체를 막지 않음 */ }
    }

    private void WriteRegistry(List<LicenseRecord> list)
    {
        var dir = Path.GetDirectoryName(_registryPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _registryPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, PromptFlowJsonContext.Default.LicenseRecordList));
        File.Copy(tmp, _registryPath, overwrite: true);
        try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
    }

    private RSA LoadPrivate()
    {
        if (!File.Exists(_privPath))
            throw new InvalidOperationException("서명키(license_signing.dat)가 없습니다 — 발급자 PC에서만 발급할 수 있습니다.");
        var dec = LicenseKeyCipher.Unprotect(File.ReadAllText(_privPath));
        if (dec.Length == 0)
            throw new InvalidOperationException("서명키 복호화 실패 — 발급자 본인 PC/계정에서 실행 중인지 확인하세요.");
        var r = RSA.Create();
        r.ImportPkcs8PrivateKey(Convert.FromBase64String(dec), out _);
        return r;
    }

    private string EffectivePublicKey() => _cfg["License:PublicKey"] ?? "";

    private static string FormatYmd(string? ymd)
        => (ymd is not null && ymd.Length == 8) ? $"{ymd[..4]}-{ymd.Substring(4, 2)}-{ymd.Substring(6, 2)}" : (ymd ?? "");

    private static string ToB64Url(byte[] b)
        => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromB64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }

    internal sealed class Payload
    {
        public string? n { get; set; }
        public string? iat { get; set; }
        public string? exp { get; set; }
        public string? id { get; set; }
    }
}

/// <summary>
/// 라이선스 서명키 전용 고정 시드 암호화 — 발급기(CostKeyGen)와 시드가 반드시 동일해야 함.
///   머신·유저 바운드를 쓰지 않아 프로세스/컨텍스트가 달라도 동일하게 복호화(드리프트 방지). 서명키 파일은 배포되지 않음.
/// </summary>
internal static class LicenseKeyCipher
{
    private const int NonceLen = 12;
    private const int TagLen = 16;
    private const string Seed = "PROMPTFLOWSTUDIO::SAFERISK::LICENSE-SIGNING::v1-fixed";

    private static byte[] Key() => SHA256.HashData(Encoding.UTF8.GetBytes(Seed));

    public static string Unprotect(string b64)
    {
        try
        {
            var buf = Convert.FromBase64String(b64 ?? "");
            if (buf.Length < NonceLen + TagLen) return "";
            var key = Key();
            var nonce = buf.AsSpan(0, NonceLen);
            var tag = buf.AsSpan(NonceLen, TagLen);
            var ct = buf.AsSpan(NonceLen + TagLen);
            var pt = new byte[ct.Length];
            using (var aes = new AesGcmCompat(key, TagLen))
                aes.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return ""; }
    }
}
