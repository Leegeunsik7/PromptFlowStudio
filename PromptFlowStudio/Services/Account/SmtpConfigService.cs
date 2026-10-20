using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Security;   // 범용 LocalCipher(머신바운드 AES-256-GCM)

namespace PromptFlowStudio.Services.Account;

/// <summary>
/// [PromptFlowStudio] 동적 SMTP 발송 설정 — 환경설정 화면에서 편집하여 로컬 <b>암호화 파일</b>(%LocalAppData%\PromptFlowStudio\smtp_config.json)에 적재.
///   앱 비밀번호를 포함한 전체 페이로드를 <b>AES-256-GCM(머신·사용자 바운드 키)</b>으로 암호화하므로, 파일이 유출돼도 타 PC 에서는 복호화가 불가능하다.
///   모니터링 오토메이션(SoundStack)의 SmtpConfigService 를 EF Core → <b>순수 파일 저장</b>으로 이식(PromptFlowStudio 은 DB 없음 · 외부 NuGet 0 · 완전 오프라인).
/// </summary>
public sealed record SmtpConfig(bool Enabled, string Host, int Port, string From, string AppPassword)
{
    /// <summary>SMTP 로그인 아이디(네이버=아이디만, Gmail/Office365=전체 이메일). 비우면 From 을 인증 아이디로 사용.</summary>
    public string User { get; init; } = "";

    /// <summary>SSL/STARTTLS 사용 여부(기본 true). appsettings Smtp:EnableSsl=false 로만 해제.</summary>
    public bool EnableSsl { get; init; } = true;

    /// <summary>실제 인증에 쓸 로그인 아이디 — User 지정 시 그것, 아니면 From.</summary>
    [JsonIgnore]
    public string AuthUser => string.IsNullOrWhiteSpace(User) ? From : User;

    /// <summary>초기 기본값(네이버 SMTP 프리셋).</summary>
    public static SmtpConfig Empty => new(false, "smtp.naver.com", 587, "", "");

    /// <summary>실발송 가능 여부(활성 + 호스트 + 보내는사람 + 앱비번 모두 구성). false 면 데모 모드.</summary>
    [JsonIgnore]
    public bool CanSend => Enabled
                        && !string.IsNullOrWhiteSpace(Host)
                        && !string.IsNullOrWhiteSpace(From)
                        && !string.IsNullOrEmpty(AppPassword);
}

/// <summary>발송 시점의 유효 SMTP 설정 공급자 — 관리자 저장본(smtp_config.json) 우선, 없으면 appsettings Smtp:* 폴백.</summary>
public interface ISmtpConfigProvider
{
    SmtpConfig GetEffective();
}

/// <summary>
/// [PromptFlowStudio] SMTP 설정 저장소(로컬 암호화 파일) + 유효설정 공급자.
///   저장: smtp_config.json 단일 봉투 — 전체 SmtpConfig 를 JSON 직렬화 후 AES-256-GCM(머신·사용자 바운드)으로 통째 암호화.
///   조회: 저장본 우선 → 없으면 appsettings Smtp:*(Enabled/Host/Port/User/Password/From/EnableSsl) 폴백(무설정 시 데모).
///   복호화 키가 이 서비스(<see cref="SmtpLocalCipher"/>)에만 있으므로 설정 파일을 다른 PC 로 복사해도 열 수 없다.
/// </summary>
public sealed class SmtpConfigService : ISmtpConfigProvider
{
    private readonly IConfiguration _cfg;
    private readonly string _path;
    private readonly object _lock = new();
    private SmtpConfig? _cache;
    private bool _loaded;

    /// <summary>마지막 저장 시각(화면 표시용). 저장본이 없으면 null.</summary>
    public DateTime? SavedAt { get; private set; }

    /// <summary>설정 저장 위치(환경설정 화면 표시용).</summary>
    public string FilePath => _path;

    /// <summary>배포 씨앗 파일명 — 설치본에서 exe 옆에 놓여, 운영자 PC 최초 실행 시 메일설정을 provisioning 한다.</summary>
    public const string DeploySeedFileName = "deploy_smtp.dat";

    public SmtpConfigService(IConfiguration cfg)
    {
        _cfg = cfg;
        _path = Path.Combine(LicenseService.DataAreaRoot(), "smtp_config.json");
    }

    /// <summary>관리자가 저장한 설정. 없거나 손상(타 PC 복사 등) 시 null(→ appsettings 폴백).</summary>
    public SmtpConfig? LoadSaved()
    {
        lock (_lock)
        {
            if (_loaded) return _cache;
            _loaded = true;
            try
            {
                _cache = LoadFromFile();
                // 최초 실행 provisioning — 이 PC 의 머신바운드 저장본이 없고 exe 옆에 배포 씨앗이
                //   있으면, 씨앗을 복호화해 이 PC 의 머신바운드 저장본으로 1회 재봉인한다.
                _cache ??= TryRehydrateFromDeploySeed();
            }
            catch { _cache = null; }
            return _cache;
        }
    }

    /// <summary>smtp_config.json(암호문 봉투) → SmtpConfig 복원(전체 복호화).</summary>
    private SmtpConfig? LoadFromFile()
    {
        if (!File.Exists(_path)) return null;
        var env = JsonSerializer.Deserialize(File.ReadAllText(_path), PromptFlowJsonContext.Default.SmtpEnvelope);
        if (env is null || string.IsNullOrEmpty(env.Enc)) return null;
        var json = SmtpLocalCipher.Unprotect(env.Enc);
        if (json.Length == 0) return null;                      // 키 불일치(타 PC)·손상 → 폴백
        var cfg = JsonSerializer.Deserialize(json, PromptFlowJsonContext.Default.SmtpConfig);
        if (cfg is not null && !string.IsNullOrEmpty(env.SavedAt)
            && DateTime.TryParse(env.SavedAt, out var dt)) SavedAt = dt;
        return cfg;
    }

    /// <summary>설정 저장 — 전체를 AES-256-GCM 암호화하여 단일 봉투로 원자적 기록(.tmp → 교체). 성공 시 true + 캐시 갱신.</summary>
    public bool Save(SmtpConfig c)
    {
        lock (_lock)
        {
            try
            {
                var clean = c with
                {
                    Host = (c.Host ?? "").Trim(),
                    From = (c.From ?? "").Trim(),
                    AppPassword = (c.AppPassword ?? "").Replace(" ", ""),   // 구글 16자리 앱비번 공백 제거
                };

                var json = JsonSerializer.Serialize(clean, PromptFlowJsonContext.Default.SmtpConfig);
                var env = new Envelope { V = 1, Enc = SmtpLocalCipher.Protect(json), SavedAt = DateTime.Now.ToString("o") };

                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(env, PromptFlowJsonContext.Default.SmtpEnvelope));
                File.Copy(tmp, _path, overwrite: true);
                try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }

                _cache = clean;
                _loaded = true;
                SavedAt = DateTime.Now;
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>발송 유효 설정 — 저장본 우선, 없으면 appsettings Smtp:* 폴백(구성 없으면 Enabled=false → 데모).</summary>
    public SmtpConfig GetEffective()
    {
        var saved = LoadSaved();
        if (saved is not null) return saved;

        var host = string.IsNullOrWhiteSpace(_cfg["Smtp:Host"]) ? "smtp.naver.com" : _cfg["Smtp:Host"]!;
        var port = int.TryParse(_cfg["Smtp:Port"], out var p) ? p : 587;
        var user = _cfg["Smtp:User"] ?? "";
        var from = string.IsNullOrWhiteSpace(_cfg["Smtp:From"]) ? user : _cfg["Smtp:From"]!;
        var pass = (_cfg["Smtp:Password"] ?? "").Replace(" ", "");
        var enabled = string.Equals(_cfg["Smtp:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
        var ssl = !string.Equals(_cfg["Smtp:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase);
        return new SmtpConfig(enabled, host, port, from, pass) { User = user, EnableSsl = ssl };
    }

    /// <summary>
    /// [배포 씨앗 복원] exe 옆 deploy_smtp.dat 를 portable 복호화해 이 PC 의 머신바운드 저장본으로 재봉인한다.
    ///   운영자 PC <b>최초 실행 1회</b>만 동작한다(이후엔 머신바운드 저장본이 있어 이 경로로 오지 않는다).
    /// </summary>
    /// <returns>씨앗을 복원해 저장했으면 그 설정, 씨앗이 없거나 손상됐으면 null(→ appsettings 폴백).</returns>
    private SmtpConfig? TryRehydrateFromDeploySeed()
    {
        try
        {
            var seedPath = Path.Combine(AppContext.BaseDirectory, DeploySeedFileName);
            if (!File.Exists(seedPath)) return null;
            var json = SmtpPortableCipher.Unprotect(File.ReadAllText(seedPath));
            if (json.Length == 0) return null;
            var cfg = JsonSerializer.Deserialize(json, PromptFlowJsonContext.Default.SmtpConfig);
            if (cfg is null) return null;
            Save(cfg);          // 이 PC 의 머신바운드 저장본으로 재봉인(_cache·_loaded·SavedAt 갱신)
            _cache ??= cfg;     // 저장 실패(디스크 권한 등)해도 이번 세션은 파싱본으로 동작
            return _cache;
        }
        catch { return null; }
    }

    /// <summary>
    /// [배포 씨앗 생성 · 패키징 전용] 발급자(개발) PC 의 머신바운드 저장본(smtp_config.json)을 읽어
    ///   portable 암호화로 <paramref name="destDir"/>\deploy_smtp.dat 에 기록한다.
    /// </summary>
    /// <param name="destDir">설치 페이로드 폴더(exe 와 같은 위치에 씨앗을 둔다).</param>
    /// <returns>씨앗을 생성했으면 true. UI 저장본이 없거나 실발송 불가 설정이면 false.</returns>
    /// <remarks>
    /// <para>UI 로 저장한 설정이 없으면(=appsettings Smtp:* 만 구성) false — appsettings.json 이
    ///   이미 설치본에 동봉되므로 씨앗이 불필요하다.</para>
    /// <para>⚠ 씨앗은 <b>고정시드 암호화</b>(<see cref="SmtpPortableCipher"/>)라 어느 PC 에서나 열린다.
    ///   이는 '난독화'이지 기밀 보장이 아니다 — 담기는 값은 revocable 한 SMTP 앱비밀번호이며,
    ///   사내 배포 provisioning 전용이다.</para>
    /// </remarks>
    public static bool ExportDeploySeed(string destDir)
    {
        try
        {
            var savedPath = Path.Combine(LicenseService.DataAreaRoot(), "smtp_config.json");
            if (!File.Exists(savedPath)) return false;
            var env = JsonSerializer.Deserialize(File.ReadAllText(savedPath), PromptFlowJsonContext.Default.SmtpEnvelope);
            if (env is null || string.IsNullOrEmpty(env.Enc)) return false;
            var json = SmtpLocalCipher.Unprotect(env.Enc);     // 발급자 PC 에서만 열림(머신바운드)
            if (json.Length == 0) return false;
            var cfg = JsonSerializer.Deserialize(json, PromptFlowJsonContext.Default.SmtpConfig);
            if (cfg is null || !cfg.CanSend) return false;      // 실발송 불가 설정은 나르지 않는다
            Directory.CreateDirectory(destDir);
            File.WriteAllText(
                Path.Combine(destDir, DeploySeedFileName),
                SmtpPortableCipher.Protect(json));              // 어느 PC 에서나 열리는 씨앗으로 재암호화
            return true;
        }
        catch { return false; }
    }

    /// <summary>암호화 봉투 — smtp_config.json 저장 포맷.</summary>
    internal sealed class Envelope
    {
        public int V { get; set; }
        public string? Enc { get; set; }       // AES-256-GCM(base64: nonce12|tag16|cipher)
        public string? SavedAt { get; set; }
    }
}

/// <summary>
/// [PromptFlowStudio] SMTP 로컬 암호화기 — 범용 <see cref="LocalCipher"/>(머신바운드 AES-256-GCM)에 위임하는 얇은 래퍼.
///   ⚠ <see cref="Purpose"/> 는 기존 시드("MS2E::PromptFlowStudio::SMTP::v1|"+Machine+"|"+User)와 <b>바이트 단위로 동일한</b>
///     키를 유도하도록 고정한다 — 이미 저장된 smtp_config.json 이 그대로 복호화되어야 하므로 <b>절대 변경 금지</b>.
///   출력 포맷(base64: nonce|tag|cipher)도 LocalCipher 와 동일해 하위호환된다.
/// </summary>
internal static class SmtpLocalCipher
{
    private const string Purpose = "MS2E::PromptFlowStudio::SMTP::v1";
    public static string Protect(string plain) => LocalCipher.Protect(plain, Purpose);
    public static string Unprotect(string b64) => LocalCipher.Unprotect(b64, Purpose);
}

/// <summary>
/// [PromptFlowStudio] 배포 씨앗 전용 portable 암호화기 — AES-256-GCM, <b>고정 시드</b>.
///   <see cref="SmtpLocalCipher"/> 가 머신·사용자 바운드(타 PC 복호화 불가)인 것과 달리, 이 씨앗은
///   <b>어느 PC 에서나</b> 열려야 운영자 PC 최초 실행 시 메일설정을 provisioning 할 수 있다.
///   ⚠ 고정 시드는 바이너리에 박혀 있으므로 이는 기밀 보장이 아니라 <b>난독화</b>다. 담기는 값은
///     revocable 한 SMTP 앱비밀번호이며, 사내 배포 provisioning 전용이다.
///   출력 = base64(nonce[12] | tag[16] | ciphertext) — SmtpLocalCipher 와 동일 포맷.
/// </summary>
internal static class SmtpPortableCipher
{
    private const int NonceLen = 12;
    private const int TagLen = 16;

    // 고정 시드(머신·사용자 무관). SmtpLocalCipher 의 머신바운드 시드와 <의도적으로> 다르다.
    private static byte[] DeriveKey() =>
        SHA256.HashData(Encoding.UTF8.GetBytes("MS2E::PromptFlowStudio::SMTP::deploy-seed::v1"));

    public static string Protect(string plain)
    {
        var key = DeriveKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var pt = Encoding.UTF8.GetBytes(plain ?? "");
        var ct = new byte[pt.Length];
        var tag = new byte[TagLen];
        using (var aes = new AesGcmCompat(key, TagLen))
            aes.Encrypt(nonce, pt, ct, tag);

        var buf = new byte[NonceLen + TagLen + ct.Length];
        Buffer.BlockCopy(nonce, 0, buf, 0, NonceLen);
        Buffer.BlockCopy(tag, 0, buf, NonceLen, TagLen);
        Buffer.BlockCopy(ct, 0, buf, NonceLen + TagLen, ct.Length);
        return Convert.ToBase64String(buf);
    }

    public static string Unprotect(string b64)
    {
        try
        {
            var buf = Convert.FromBase64String(b64 ?? "");
            if (buf.Length < NonceLen + TagLen) return "";
            var key = DeriveKey();
            var nonce = buf.AsSpan(0, NonceLen);
            var tag = buf.AsSpan(NonceLen, TagLen);
            var ct = buf.AsSpan(NonceLen + TagLen);
            var pt = new byte[ct.Length];
            using (var aes = new AesGcmCompat(key, TagLen))
                aes.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return ""; }   // 시드 불일치·손상 → 빈 문자열(호출부가 폴백)
    }
}
