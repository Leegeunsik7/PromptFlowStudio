using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace PromptFlowStudio.Services.Account;

/// <summary>회원 목록 표시용 뷰(비밀번호 제외).</summary>
public sealed record MemberView(string Id, string Name, string Email, string Phone, string Role, string Status,
    DateTime CreatedAt, string Birth, string LicenseKey, string Gender, string Company, string MemberId);

/// <summary>로그인/가입 결과.</summary>
public sealed record MemberAuthResult(bool Ok, string Message, string? Role);

/// <summary>정밀 5단계 회원가입 요청. Platform=업종 · Facility=원가 산출 방식.</summary>
public sealed record MemberRegisterRequest(
    string Id, string Password, string Name, string Birth, string Gender,
    string Email, string Phone, string LicenseKey, string MemberId,
    string Company, string Zip, string Address, string AddrDetail, string Platform, string Facility);

/// <summary>아이디/비밀번호 찾기 결과.</summary>
public sealed record MemberFindResult(bool Ok, string Message, string? MaskedId, string? TempPassword);

/// <summary>
/// [PromptFlowStudio] 회원 계정 관리(Identity Engine, 싱글턴) — PBKDF2(SHA-256·10만 회) 안전 해싱 + <b>파일 영구 저장</b>
///   (%LocalAppData%\PromptFlowStudio\members.json). 정밀 5단계 회원가입(라이선스 키·생년월일·성별·회사/주소 포함)·로그인·
///   아이디/비밀번호 찾기·관리자 회원 관리(권한/승인/잠금/리셋 + 라이선스 재발급). 하드코딩 기본 계정 없음 — <b>최초 가입자가 자동 관리자</b>(First-User 온보딩).
///   외부망 없이 동작(BCL만). 모니터링/스마트팜/PLAI/UAV/Stock와 동일 설계.
/// </summary>
public sealed class MemberRegistryService
{
    internal sealed class MemberEntity
    {
        public string Id { get; set; } = "";
        public string Hash { get; set; } = "";
        public string Salt { get; set; } = "";
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Role { get; set; } = "Operator";
        public string Status { get; set; } = "active";
        public string Birth { get; set; } = "";
        public string LicenseKey { get; set; } = "";
        public string Gender { get; set; } = "";
        public string Company { get; set; } = "";
        public string Zip { get; set; } = "";
        public string Address { get; set; } = "";
        public string AddrDetail { get; set; } = "";
        public string Platform { get; set; } = "";
        public string Facility { get; set; } = "";
        public string MemberId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
    }

    private readonly List<MemberEntity> _members = new();
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public event Action? Changed;

    public MemberRegistryService()
    {
        _path = Path.Combine(LicenseService.DataAreaRoot(), "members.json");
        Load();
        // ⚠ 하드코딩 기본 계정 없음(보안). 원장이 비어 있으면 <첫 가입자>가 자동 관리자(First-User 온보딩) —
        //   RegisterFull 이 Count==0 을 락 안에서 원자적으로 판정해 최초 가입자에게 Admin 을 부여한다.
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var list = JsonSerializer.Deserialize(File.ReadAllText(_path), PromptFlowJsonContext.Default.MemberEntityList);
                if (list is not null) { _members.Clear(); _members.AddRange(list); }
            }
        }
        catch { /* 손상 시 빈 상태로 시작(아래 관리자 시드) */ }
    }

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_members, PromptFlowJsonContext.Default.MemberEntityList));
                File.Copy(tmp, _path, overwrite: true);
                try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
            }
            catch { /* 저장 실패는 앱 동작을 막지 않음 */ }
        }
    }

    public IReadOnlyList<MemberView> Members =>
        _members.Select(u => new MemberView(u.Id, u.Name, u.Email, u.Phone, u.Role, u.Status, u.CreatedAt, u.Birth, u.LicenseKey, u.Gender, u.Company, u.MemberId)).ToList();
    public int Count => _members.Count;
    public int PendingCount => _members.Count(u => u.Status == "pending");

    public bool Exists(string id) => Get(id) is not null;

    public MemberAuthResult RegisterFull(MemberRegisterRequest r)
    {
        var id = (r.Id ?? "").Trim();
        if (id.Length < 3 || (r.Password ?? "").Length < 4) return new MemberAuthResult(false, "아이디 3자·비밀번호 4자 이상 입력하세요.", null);

        MemberAuthResult res;
        // ⚠ 중복확인·최초여부판단·추가를 한 락 안에서 원자적으로 — 동시 가입 경쟁 조건 방지.
        lock (_lock)
        {
            if (_members.Any(u => u.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                return new MemberAuthResult(false, "이미 존재하는 아이디입니다.", null);

            // 최초 가입자(원장이 비어 있음)는 라이선스 없이 자동 관리자 — 락아웃 방지 부트스트랩.
            var isFirst = _members.Count == 0;
            var role = isFirst ? "Admin" : "Viewer";

            var (h, s) = HashPassword(r.Password!);
            _members.Add(new MemberEntity
            {
                Id = id, Hash = h, Salt = s, Name = (r.Name ?? "").Trim(), Email = (r.Email ?? "").Trim(),
                Phone = (r.Phone ?? "").Trim(), Role = role, Status = "active",
                Birth = (r.Birth ?? "").Trim(), LicenseKey = (r.LicenseKey ?? "").Trim(),
                Gender = (r.Gender ?? "").Trim(), Company = (r.Company ?? "").Trim(),
                Zip = (r.Zip ?? "").Trim(), Address = (r.Address ?? "").Trim(), AddrDetail = (r.AddrDetail ?? "").Trim(),
                Platform = (r.Platform ?? "").Trim(), Facility = (r.Facility ?? "").Trim(),
                MemberId = (r.MemberId ?? "").Trim(), CreatedAt = DateTime.Now,
            });
            Save();   // Monitor 재진입(재진입 허용) — 파일 기록
            res = new MemberAuthResult(true,
                isFirst ? $"'{id}' 최초 관리자 계정으로 가입 완료 — 바로 로그인할 수 있습니다."
                        : $"'{id}' 가입 완료 — 바로 로그인할 수 있습니다.",
                role);
        }
        Changed?.Invoke();   // 락 밖에서 방송(구독자 재진입 안전)
        return res;
    }

    public bool UpdateLicense(string id, string newLicenseKey)
    {
        var u = Get(id);
        if (u is null) return false;
        u.LicenseKey = (newLicenseKey ?? "").Trim();
        Save();
        Changed?.Invoke();
        return true;
    }

    public MemberAuthResult Authenticate(string id, string pw)
    {
        var u = _members.FirstOrDefault(x => x.Id.Equals((id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        if (u is null) return new MemberAuthResult(false, "존재하지 않는 아이디입니다.", null);
        if (u.Status == "locked") return new MemberAuthResult(false, "잠긴 계정입니다. 관리자에게 문의하세요.", null);
        if (u.Status == "pending") return new MemberAuthResult(false, "관리자 승인 대기 중인 계정입니다.", null);
        if (!Verify(pw ?? "", u.Hash, u.Salt)) return new MemberAuthResult(false, "비밀번호가 일치하지 않습니다.", null);
        return new MemberAuthResult(true, $"{u.Name}님 환영합니다.", u.Role);
    }

    /// <summary>
    /// [관리자 설정 진입 게이트] 입력한 비밀번호가 <b>활성 Admin 계정</b>의 것과 일치하면 true.
    ///   현재 로그인 사용자가 누구든(테스트 계정 포함), Admin 비밀번호를 알아야만 통과한다.
    /// </summary>
    public bool VerifyAdminPassword(string pw)
        => !string.IsNullOrEmpty(pw)
           && _members.Any(u => u.Role == "Admin" && u.Status == "active" && Verify(pw, u.Hash, u.Salt));

    public MemberFindResult FindId(string name, string phone)
    {
        var u = Match(name, phone);
        return u is null
            ? new MemberFindResult(false, "이름과 전화번호가 일치하는 계정이 없습니다.", null, null)
            : new MemberFindResult(true, "가입된 아이디를 찾았습니다.", MaskId(u.Id), null);
    }

    public MemberFindResult ResetPassword(string name, string phone)
    {
        var u = Match(name, phone);
        if (u is null) return new MemberFindResult(false, "이름과 전화번호가 일치하는 계정이 없습니다.", null, null);
        var temp = TempPassword();
        var (h, s) = HashPassword(temp);
        u.Hash = h; u.Salt = s; u.Status = "active";
        Save();
        Changed?.Invoke();
        return new MemberFindResult(true, $"'{MaskId(u.Id)}' 임시 비밀번호가 발급되었습니다.", MaskId(u.Id), temp);
    }

    /// <summary>등록된 이메일 주소 조회(비밀번호 재설정 OTP 발송·마스킹용). 없으면 null.</summary>
    public string? GetEmail(string id) => Get(id)?.Email;

    /// <summary>
    /// [셀프 비밀번호 재설정 · 이메일 인증 후] 아이디의 비밀번호를 새 값으로 설정한다.
    ///   ⚠ 호출 전 <b>가입 이메일로 보낸 인증번호(OTP) 검증</b>을 반드시 통과해야 한다(호출부가 게이트).
    ///   이름·연락처만으로는 재설정할 수 없다 — 타인이 신상만 알아도 비밀번호를 바꾸지 못하게 한다.
    /// </summary>
    /// <returns>성공 true. 계정 없음·잠김·짧은 비밀번호면 false.</returns>
    public bool SetPasswordForId(string id, string newPassword)
    {
        if ((newPassword ?? "").Length < 4) return false;
        var u = Get(id);
        if (u is null) return false;
        if (u.Status == "locked") return false;   // 잠긴 계정은 관리자만 해제
        var (h, s) = HashPassword(newPassword!);
        u.Hash = h; u.Salt = s;
        Save();
        Changed?.Invoke();
        return true;
    }

    public void SetRole(string id, string role) { var u = Get(id); if (u is not null && new[] { "Admin", "Operator", "Viewer" }.Contains(role)) { u.Role = role; Save(); Changed?.Invoke(); } }
    /// <summary>
    /// 회원 프로필(이름·연락처·소속) 수정.
    ///
    /// <para>
    /// ⚠ <b>아이디와 권한은 여기서 바꾸지 않는다.</b> 아이디는 감사 로그의 주체
    ///   식별자이고, 권한은 <see cref="SetRole"/> 가 값을 검증한다.
    ///   한 함수가 둘 다 건드리면 "이름 고치려다 권한이 바뀌는" 사고가 난다.
    /// </para>
    /// <para>
    /// ⚠ 빈 문자열은 <b>의도적 지움</b>으로 취급한다(null 과 구별). 다만 이름은
    ///   비우지 않는다 — 감사 화면에서 누구인지 알 수 없게 된다.
    /// </para>
    /// </summary>
    /// <returns>수정된 항목이 있으면 true.</returns>
    public bool UpdateProfile(string id, string? name, string? email, string? phone, string? company)
    {
        var u = Get(id);
        if (u is null) return false;

        var n = (name ?? "").Trim();
        var e = (email ?? "").Trim();
        var p = (phone ?? "").Trim();
        var c = (company ?? "").Trim();

        // 이름은 비울 수 없다. 나머지는 비워도 된다.
        if (n.Length == 0) return false;

        var changed = u.Name != n || u.Email != e || u.Phone != p || u.Company != c;
        if (!changed) return false;

        u.Name = n; u.Email = e; u.Phone = p; u.Company = c;
        Save();
        Changed?.Invoke();
        return true;
    }

    public void ToggleStatus(string id) { var u = Get(id); if (u is null) return; u.Status = u.Status == "active" ? "locked" : "active"; Save(); Changed?.Invoke(); }
    public void Approve(string id) { var u = Get(id); if (u is not null) { u.Status = "active"; Save(); Changed?.Invoke(); } }
    public string ForceReset(string id) { var u = Get(id); if (u is null) return ""; var t = TempPassword(); var (h, s) = HashPassword(t); u.Hash = h; u.Salt = s; Save(); Changed?.Invoke(); return t; }
    public bool Delete(string id) { var u = Get(id); if (u is null || u.Role == "Admin") return false; _members.Remove(u); Save(); Changed?.Invoke(); return true; }

    private MemberEntity? Get(string id) => _members.FirstOrDefault(u => u.Id.Equals((id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
    private MemberEntity? Match(string name, string phone)
    {
        string n = (name ?? "").Trim(), p = Digits(phone);
        return _members.FirstOrDefault(u => u.Name == n && Digits(u.Phone) == p && p.Length >= 4);
    }

    private static string Digits(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());

    private static string MaskId(string id)
    {
        if (id.Length <= 2) return id[..1] + "*";
        int keepEnd = Math.Min(1, id.Length - 2);
        return id[..2] + new string('*', id.Length - 2 - keepEnd) + id[^keepEnd..];
    }

    private static string TempPassword() => "T" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + "!";

    private static (string hash, string salt) HashPassword(string pw)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool Verify(string pw, string hashB64, string saltB64)
    {
        try
        {
            byte[] salt = Convert.FromBase64String(saltB64);
            byte[] expected = Convert.FromBase64String(hashB64);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(pw, salt, 100_000, HashAlgorithmName.SHA256, 32);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }
}
