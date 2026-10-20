using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Account;

/// <summary>중앙 명부로 보낼 회원 <b>프로필</b> — 비밀번호(해시 포함)는 담지 않는다.</summary>
public sealed record MemberDirectoryEntry(
    string Id, string Name, string Email, string Phone,
    string Company, string Address, string License, string MemberId, string RegisteredAt);

/// <summary>
/// [PromptFlowStudio] 신규 가입 통보 — 가입 성공 시 회원 <b>프로필</b>을 <b>관리자 메일로 통보</b>하고
///   <c>member_&lt;id&gt;.json</c> 을 첨부한다. 관리자(Gmail/Workspace)에서 Apps Script 로 그 첨부를
///   구글 드라이브에 자동 저장하면 관리자 PC 의 <c>G:\내 드라이브</c> 로 동기화된다.
/// </summary>
/// <remarks>
/// <para>전송은 기존 <see cref="SmtpEmailService"/>(메일(SMTP) 설정)를 그대로 쓴다 — 운영자 PC 는 배포 씨앗(deploy_smtp.dat)으로 SMTP 가 구성돼 있어 어느 PC 에서도 발송된다.</para>
/// <para>⚠ <b>비밀번호 해시는 절대 보내지 않는다.</b> 중앙 명부는 "누가 가입했나" 가시성용이다.</para>
/// <para>수신 주소 미설정 또는 SMTP 미구성이면 아무 것도 하지 않는다. 통보 실패(오프라인 등)는 <b>가입을 막지 않는다</b>(best-effort).</para>
/// </remarks>
public sealed class MemberDirectorySyncService
{
    private readonly string _to;
    private readonly SmtpEmailService _email;
    private readonly ISmtpConfigProvider _smtp;

    public MemberDirectorySyncService(IConfiguration cfg, SmtpEmailService email, ISmtpConfigProvider smtp)
    {
        _to = (cfg["Members:AdminNotifyEmail"] ?? "").Trim();
        _email = email;
        _smtp = smtp;
    }

    /// <summary>관리자 통보를 보낼 수 있는가 — 수신 주소 + SMTP 실발송 구성.</summary>
    public bool Configured => _to.Length > 0 && _smtp.GetEffective().CanSend;

    /// <summary>통보 수신 관리자 주소(화면 표시용).</summary>
    public string AdminEmail => _to;

    /// <summary>회원 프로필을 관리자 메일로 통보(+JSON 첨부). 성공 true. 미구성/실패는 false(가입 자체는 영향 없음).</summary>
    public async Task<bool> PushAsync(MemberDirectoryEntry m)
    {
        if (!Configured) return false;
        try
        {
            var json = BuildJson(m);
            var body = BuildBody(m);
            // 첨부 파일명은 프로젝트명 고정 — 관리자 쪽 Apps Script 가 이 첨부를 읽어
            //   드라이브의 마스터 PromptFlowStudio.json(전체 회원 배열)에 합친다.
            const string file = "PromptFlowStudio.json";
            var subject = $"[PromptFlowStudio] 신규 회원가입 · {m.Id} ({m.Name})";
            return await _email.SendNoticeWithAttachmentAsync(_to, subject, body, file, json).ConfigureAwait(false);
        }
        catch { return false; }
    }

    private static string BuildJson(MemberDirectoryEntry m) => JsonSerializer.Serialize(m, PromptFlowJsonContext.Default.MemberDirectoryEntry);

    private static string BuildBody(MemberDirectoryEntry m) =>
        "<div style='font-family:Pretendard,Segoe UI,sans-serif;max-width:520px'>" +
        "<h2 style='color:#6d5efc;margin:0 0 12px'>🎬 PromptFlow Studio 신규 회원가입</h2>" +
        "<p>새 회원이 가입했습니다. 첨부된 <b>member_*.json</b> 이 중앙 명부(구글 드라이브)에 자동 저장됩니다.</p>" +
        "<table style='border-collapse:collapse;font-size:13px'>" +
        Row("아이디", m.Id) + Row("이름", m.Name) + Row("이메일", m.Email) + Row("연락처", m.Phone) +
        Row("소속", m.Company) + Row("주소", string.IsNullOrWhiteSpace(m.Address) ? "-" : m.Address) +
        Row("라이선스", string.IsNullOrWhiteSpace(m.License) ? "-" : m.License) +
        Row("회원번호", m.MemberId) + Row("가입일시", m.RegisteredAt) +
        "</table>" +
        "<p style='color:#888;font-size:.8rem;margin-top:12px'>비밀번호는 포함되지 않습니다(각 PC 로컬에만 안전 보관). · PromptFlow Studio · 프롬프트→영상 생성 워크플로우</p></div>";

    private static string Row(string k, string v) =>
        $"<tr><td style='padding:4px 12px 4px 0;color:#888'>{k}</td><td style='padding:4px 0;font-weight:700'>{System.Net.WebUtility.HtmlEncode(v ?? "")}</td></tr>";
}
