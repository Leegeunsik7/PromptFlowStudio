using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Account;

/// <summary>시리얼 라이선스 1건 — 키, 발급일, 사용여부, 매핑 이메일, 사용일.</summary>
public sealed record SerialLicense(string Key, string IssuedAt, bool Used, string Email, string UsedAt);

/// <summary>
/// [PromptFlowStudio] 시리얼 라이선스 발급/검증 — 관리자가 앱에서 직접 <c>XXXX-XXXX-XXXX-XXXX</c> 키를 발급하고
///   <c>%LocalAppData%\PromptFlowStudio\license_registry.json</c> 에 Used/Unused 로 관리한다(순수 BCL · 난수 기반).
///   회원가입은 <b>미사용 키</b>인지 검증하고, 가입 완료 시 해당 키를 <see cref="MarkUsed"/> 로 소진한다.
///   ⚠ 기존 RSA 서명 방식(LicenseService.IssueLicense/VerifyKey)과 별개 — 이 앱은 이 시리얼 방식으로 게이트한다.
/// </summary>
public sealed class SerialLicenseService
{
    private readonly IConfiguration _cfg;
    private readonly string _path;
    private readonly object _lock = new();
    public SerialLicenseService(IConfiguration cfg)
    {
        _cfg = cfg;
        _path = Path.Combine(LicenseService.DataAreaRoot(), "license_registry.json");
        SeedIfFirstRun();   // 첫 실행(파일 없음)이면 초기 미사용 키 3개 시드
    }

    /// <summary>라이선스 게이트 on/off(appsettings License:Enabled).</summary>
    public bool EnforcementOn => string.Equals(_cfg["License:Enabled"], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>비상 마스터 키(appsettings License:MasterKey). 비어 있으면 비활성.</summary>
    public string MasterKey => (_cfg["License:MasterKey"] ?? "").Trim();

    /// <summary>입력 키가 마스터 키인지(소진 없이 관리자 통과용).</summary>
    public bool IsMasterKey(string? key) => MasterKey.Length > 0 && Norm(key) == Norm(MasterKey);

    /// <summary>저장 파일 경로(화면 표시용).</summary>
    public string RegistryPath => _path;

    /// <summary>첫 실행(파일 미존재)에만 초기 미사용 키 3개를 시드한다 — 관리자가 바로 나눠줄 수 있게(UX). 파일이 있으면(비어 있어도) 손대지 않는다.</summary>
    private void SeedIfFirstRun()
    {
        try
        {
            if (File.Exists(_path)) return;
            lock (_lock)
            {
                if (File.Exists(_path)) return;
                var list = new List<SerialLicense>();
                while (list.Count < 3)
                {
                    var k = GenerateKey();
                    if (list.All(x => x.Key != k))
                        list.Add(new SerialLicense(k, DateTime.Now.ToString("yyyy-MM-dd HH:mm"), false, "", ""));
                }
                Save(list);
            }
        }
        catch (Exception ex) { Program.Log("SerialLicense.Seed", ex); }
    }

    /// <summary>새 시리얼 키 발급(고유 보장) → 저장 후 키 반환.</summary>
    public string Issue()
    {
        lock (_lock)
        {
            var list = Load();
            string key;
            do { key = GenerateKey(); } while (list.Any(l => l.Key == key));
            list.Add(new SerialLicense(key, DateTime.Now.ToString("yyyy-MM-dd HH:mm"), false, "", ""));
            Save(list);
            return key;
        }
    }

    /// <summary>발급된 키 목록(최신순).</summary>
    public List<SerialLicense> List()
    {
        lock (_lock) { var l = Load(); l.Reverse(); return l; }
    }

    /// <summary>키 상태 확인 — (존재?, 미사용?, 레코드).</summary>
    public (bool Exists, bool Unused, SerialLicense? Rec) Check(string? key)
    {
        var k = Norm(key);
        if (k.Length == 0) return (false, false, null);
        lock (_lock)
        {
            var rec = Load().FirstOrDefault(l => l.Key == k);
            return rec is null ? (false, false, null) : (true, !rec.Used, rec);
        }
    }

    /// <summary>키를 사용 처리(이메일 매핑). 미존재/이미사용이면 false.</summary>
    public bool MarkUsed(string? key, string email)
    {
        var k = Norm(key);
        if (k.Length == 0) return false;
        lock (_lock)
        {
            var list = Load();
            var i = list.FindIndex(l => l.Key == k);
            if (i < 0 || list[i].Used) return false;
            list[i] = list[i] with { Used = true, Email = (email ?? "").Trim(), UsedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm") };
            Save(list);
            return true;
        }
    }

    /// <summary>키 삭제(명부에서 제거).</summary>
    public bool Remove(string key)
    {
        lock (_lock)
        {
            var list = Load();
            if (list.RemoveAll(l => l.Key == Norm(key)) == 0) return false;
            Save(list);
            return true;
        }
    }

    // ── 내부 ──
    private static string Norm(string? k) => (k ?? "").Trim().ToUpperInvariant().Replace(" ", "");

    /// <summary>혼동 문자(I·O·0·1) 제외한 대문자+숫자로 XXXX-XXXX-XXXX-XXXX 생성(암호학적 난수).</summary>
    private static string GenerateKey()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // 32자 · I,O,0,1 제외
        var buf = RandomNumberGenerator.GetBytes(16);
        var sb = new StringBuilder(19);
        for (var i = 0; i < 16; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(alphabet[buf[i] % alphabet.Length]);
        }
        return sb.ToString();
    }

    private List<SerialLicense> Load()
    {
        try
        {
            return File.Exists(_path)
                ? (JsonSerializer.Deserialize(File.ReadAllText(_path), PromptFlowJsonContext.Default.SerialLicenseList) ?? new())
                : new();
        }
        catch (Exception ex) { Program.Log("SerialLicense.Load", ex); return new(); }
    }

    private void Save(List<SerialLicense> list)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, PromptFlowJsonContext.Default.SerialLicenseList));
            File.Copy(tmp, _path, overwrite: true);
            try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
        }
        catch (Exception ex) { Program.Log("SerialLicense.Save", ex); }
    }
}
