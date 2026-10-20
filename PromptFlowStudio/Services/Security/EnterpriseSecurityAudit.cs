using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot

namespace PromptFlowStudio.Services.Security;

/// <summary>감사 로그 1건 — 순번·UTC시각·분류·행위자·동작·상세 + 해시 체인(이전 해시/현재 해시).</summary>
public sealed record AuditEntry(long Seq, string TimestampUtc, string Category, string Actor, string Action, string Detail, string PrevHash, string Hash);

/// <summary>체인 무결성 검증 결과.</summary>
public sealed record AuditVerify(bool Ok, long TamperedSeq, string Message);

/// <summary>
/// [PromptFlowStudio] 엔터프라이즈 보안 감사 로그(v3.0.0 자율 발상 · 관찰 가능 싱글턴 · 순수 BCL 암호화 · 외부 NuGet 0).
///   ▸ 모든 프로젝트 변경·음성 명령·클라우드 동기화 이력을 <b>해시 체인</b>(각 항목이 이전 항목 해시를 포함)으로 연결해 <b>변조를 검출</b>한다(<see cref="Verify"/>).
///   ▸ 로그 파일은 <b>AES-256</b>(PBKDF2 키 유도 · salt+IV 선두)로 오프라인 암호화 저장 → 파일 탈취/편집 시 복호 실패로 무결성 보호.
///   ▸ <see cref="Log"/> 로 append-only 기록. 한 항목이라도 변경되면 이후 체인 전체 해시가 어긋나 <see cref="AuditVerify.TamperedSeq"/> 로 지목된다.
/// </summary>
public sealed class EnterpriseSecurityAudit
{
    private const string Genesis = "GENESIS";
    private const string AppSecret = "PromptFlowStudio::AuditVault::v3.0.0";   // ⚠ 배포 시 OS 키스토어(DPAPI) 권장
    private const int Pbkdf2Iterations = 100_000;

    private readonly object _lock = new();
    private readonly List<AuditEntry> _entries = new();

    /// <summary>로그 변경 방송.</summary>
    public event Action? OnChange;

    /// <summary>전체 감사 로그(최신 우선).</summary>
    public IReadOnlyList<AuditEntry> Entries { get { lock (_lock) return _entries.AsEnumerable().Reverse().ToList(); } }
    /// <summary>기록 건수.</summary>
    public int Count { get { lock (_lock) return _entries.Count; } }

    private string LogPath => Path.Combine(LicenseService.DataAreaRoot(), "audit.log.enc");

    public EnterpriseSecurityAudit() => TryLoad();

    /// <summary>감사 항목을 append 한다(해시 체인 연결 + 암호화 저장). 최신 항목 반환.</summary>
    public AuditEntry Log(string category, string actor, string action, string detail)
    {
        AuditEntry entry;
        lock (_lock)
        {
            var prev = _entries.Count > 0 ? _entries[^1].Hash : Genesis;
            var seq = (_entries.Count > 0 ? _entries[^1].Seq : 0) + 1;
            var ts = DateTime.UtcNow.ToString("o");
            var hash = ComputeHash(seq, ts, category, actor, action, detail, prev);
            entry = new AuditEntry(seq, ts, category ?? "", actor ?? "", action ?? "", detail ?? "", prev, hash);
            _entries.Add(entry);
            TrySave();
        }
        OnChange?.Invoke();
        return entry;
    }

    /// <summary>체인 무결성을 검증한다(어긋난 첫 순번 지목).</summary>
    public AuditVerify Verify()
    {
        lock (_lock) return VerifyChain(_entries);
    }

    /// <summary>항목 목록의 해시 체인을 검증한다(순수 · 테스트 대상).</summary>
    public static AuditVerify VerifyChain(IReadOnlyList<AuditEntry> entries)
    {
        var prev = Genesis;
        foreach (var e in entries.OrderBy(x => x.Seq))
        {
            if (e.PrevHash != prev) return new(false, e.Seq, $"체인 단절: {e.Seq}번 항목의 이전 해시 불일치(변조 의심).");
            var expected = ComputeHash(e.Seq, e.TimestampUtc, e.Category, e.Actor, e.Action, e.Detail, prev);
            if (e.Hash != expected) return new(false, e.Seq, $"해시 불일치: {e.Seq}번 항목이 변조되었습니다.");
            prev = e.Hash;
        }
        return new(true, 0, $"무결성 확인 · {entries.Count}건 체인 정상.");
    }

    /// <summary>항목 필드 → 해시(SHA-256 hex). 이전 해시를 포함해 체인을 형성.</summary>
    public static string ComputeHash(long seq, string ts, string category, string actor, string action, string detail, string prevHash)
    {
        var payload = $"{seq}{ts}{category}{actor}{action}{detail}{prevHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    // ══════════════════════ AES-256 암복호(순수 BCL · 테스트 대상) ══════════════════════

    /// <summary>문자열을 AES-256(PBKDF2 키 유도)로 암호화한다 → base64(salt16+IV16+cipher).</summary>
    public static string Encrypt(string plain, string passphrase)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.KeySize = 256;
        using var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        aes.Key = kdf.GetBytes(32);
        aes.GenerateIV();
        using var enc = aes.CreateEncryptor();
        var body = Encoding.UTF8.GetBytes(plain ?? "");
        var cipher = enc.TransformFinalBlock(body, 0, body.Length);
        var outp = new byte[16 + 16 + cipher.Length];
        Buffer.BlockCopy(salt, 0, outp, 0, 16);
        Buffer.BlockCopy(aes.IV, 0, outp, 16, 16);
        Buffer.BlockCopy(cipher, 0, outp, 32, cipher.Length);
        return Convert.ToBase64String(outp);
    }

    /// <summary>Encrypt 의 역 — 복호화(키/데이터 손상 시 예외).</summary>
    public static string Decrypt(string b64, string passphrase)
    {
        var raw = Convert.FromBase64String(b64);
        if (raw.Length < 32) throw new CryptographicException("손상된 암호문");
        var salt = raw[..16];
        var iv = raw[16..32];
        using var aes = Aes.Create();
        aes.KeySize = 256;
        using var kdf = new Rfc2898DeriveBytes(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        aes.Key = kdf.GetBytes(32);
        aes.IV = iv;
        using var dec = aes.CreateDecryptor();
        var plain = dec.TransformFinalBlock(raw, 32, raw.Length - 32);
        return Encoding.UTF8.GetString(plain);
    }

    // ══════════════════════ 영속(암호화 파일) ══════════════════════

    private void TrySave()
    {
        try { File.WriteAllText(LogPath, Encrypt(JsonSerializer.Serialize(_entries, PromptFlowJsonContext.Default.SecurityAuditEntryList), AppSecret), new UTF8Encoding(false)); }
        catch (Exception ex) { Program.Log("Audit.Save", ex); }
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            var json = Decrypt(File.ReadAllText(LogPath), AppSecret);
            var list = JsonSerializer.Deserialize(json, PromptFlowJsonContext.Default.SecurityAuditEntryList);
            if (list is not null) { lock (_lock) { _entries.Clear(); _entries.AddRange(list); } }
        }
        catch (Exception ex) { Program.Log("Audit.Load", ex); }   // 복호 실패(변조/키 불일치) → 빈 로그로 시작
    }
}
