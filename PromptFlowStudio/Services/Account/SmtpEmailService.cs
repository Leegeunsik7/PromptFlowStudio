using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Account;

/// <summary>
/// [PromptFlowStudio] 이메일 실발송 — BCL <see cref="System.Net.Mail.SmtpClient"/> 만 사용(외부 NuGet 0).
///   발송 시점에 <see cref="ISmtpConfigProvider"/> 로 유효 설정을 동적 조회(관리자 저장본 우선, 없으면 appsettings 폴백).
///   → 관리자가 환경설정에서 저장/토글하면 <b>앱 재시작 없이 즉시 반영</b>. 미구성/OFF 면 실발송 대신 false(호출부가 데모 처리).
///   모니터링 오토메이션(SoundStack) SmtpEmailService 이식본 — PromptFlowStudio 브랜딩 · 거버넌스 로그 의존 제거.
/// </summary>
public sealed class SmtpEmailService
{
    private readonly ISmtpConfigProvider _provider;

    public SmtpEmailService(ISmtpConfigProvider provider) => _provider = provider;

    /// <summary>현재 유효 설정으로 실발송 가능 여부(관리자 저장본 또는 appsettings). false 면 데모 모드.</summary>
    public bool Enabled => _provider.GetEffective().CanSend;

    /// <summary>인증 메일 발송(가입/비번찾기). 성공 true / 미구성·실패 false(false 면 호출부가 데모 코드 인라인 표출).</summary>
    public async Task<bool> SendOtpAsync(string to, string code)
    {
        var cfg = _provider.GetEffective();
        if (!cfg.CanSend || string.IsNullOrWhiteSpace(to)) return false;
        try
        {
            using var msg = BuildOtpMessage(cfg.From, to.Trim(), code);
            await SendCoreAsync(cfg, msg).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// [관리자 테스트 발송] 주어진 설정(아직 저장 전 값도 가능)으로 테스트 메일 1통 발송.
    ///   (성공여부, 사용자 표출 메시지) 반환 — 저장 여부와 무관하게 입력값 검증·실연결 확인용.
    /// </summary>
    public async Task<(bool ok, string message)> SendTestAsync(SmtpConfig cfg, string to)
    {
        var host = (cfg.Host ?? "").Trim();
        var from = (cfg.From ?? "").Trim();
        var pass = (cfg.AppPassword ?? "").Replace(" ", "");
        var target = string.IsNullOrWhiteSpace(to) ? from : to.Trim();

        if (host.Length == 0 || from.Length == 0 || pass.Length == 0)
            return (false, "SMTP 호스트 · 보내는 사람 이메일 · 앱 비밀번호를 모두 입력해 주세요.");
        if (string.IsNullOrWhiteSpace(target))
            return (false, "테스트 수신 메일 주소가 없습니다.");

        try
        {
            var effective = cfg with { Host = host, From = from, AppPassword = pass };
            using var msg = BuildTestMessage(from, target);
            await SendCoreAsync(effective, msg).ConfigureAwait(false);
            return (true, $"[SMTP 연결 성공] {target} 으로 테스트 메일이 성공적으로 전송되었습니다.");
        }
        catch (Exception ex)
        {
            return (false, $"[SMTP 발송 실패] {ex.Message}");
        }
    }

    /// <summary>[통보] 임의 수신자에게 알림 메일 발송(가입 통보·설치 통보 등). 성공 true / 미구성·실패 false.</summary>
    public async Task<bool> SendNoticeAsync(string to, string subject, string htmlBody)
    {
        var cfg = _provider.GetEffective();
        if (!cfg.CanSend || string.IsNullOrWhiteSpace(to)) return false;
        try
        {
            using var msg = new MailMessage(cfg.From.Trim(), to.Trim(), subject, htmlBody) { IsBodyHtml = true };
            await SendCoreAsync(cfg, msg).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// [통보+첨부] 관리자 통보 메일 — 본문(HTML) + 텍스트 첨부 1개(예: 회원 프로필 JSON). 성공 true / 미구성·실패 false.
    /// </summary>
    public async Task<bool> SendNoticeWithAttachmentAsync(
        string to, string subject, string htmlBody, string attachmentName, string attachmentContent)
    {
        var cfg = _provider.GetEffective();
        if (!cfg.CanSend || string.IsNullOrWhiteSpace(to)) return false;
        try
        {
            using var msg = new MailMessage(cfg.From.Trim(), to.Trim(), subject, htmlBody) { IsBodyHtml = true };
            // ⚠ 첨부는 msg 에 넘기고 별도 using 을 걸지 않는다 — MailMessage.Dispose 가 첨부까지 정리한다(이중 dispose 회피).
            msg.Attachments.Add(Attachment.CreateAttachmentFromString(
                attachmentContent ?? "", attachmentName, System.Text.Encoding.UTF8, "application/json"));
            await SendCoreAsync(cfg, msg).ConfigureAwait(false);
            return true;
        }
        catch { return false; }
    }

    private static async Task SendCoreAsync(SmtpConfig cfg, MailMessage msg)
    {
        using var client = new SmtpClient(cfg.Host.Trim(), cfg.Port)
        {
            EnableSsl = cfg.EnableSsl,                      // STARTTLS/SSL(기본 ON)
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new NetworkCredential(cfg.AuthUser.Trim(), cfg.AppPassword),   // 네이버=아이디, Gmail=이메일
            Timeout = 20000,                               // 서버 무응답 시 기본 100초 행 방지(20초 상한)
        };
        await client.SendMailAsync(msg).ConfigureAwait(false);
    }

    private static MailMessage BuildOtpMessage(string from, string to, string code)
    {
        var body =
            "<div style='font-family:Pretendard,Segoe UI,sans-serif;max-width:480px'>" +
            "<h2 style='color:#d97706;margin:0 0 12px'>🛡️ PromptFlowStudio 이메일 본인인증</h2>" +
            "<p>요청하신 인증번호는 아래와 같습니다. <b>3분 이내</b>에 입력해 주세요.</p>" +
            $"<div style='font-size:2rem;font-weight:800;letter-spacing:.3em;color:#111;background:#fff7ed;border:1px solid #fed7aa;border-radius:10px;padding:16px;text-align:center;margin:12px 0'>{code}</div>" +
            "<p style='color:#888;font-size:.8rem'>본인이 요청하지 않았다면 이 메일을 무시하세요. · PromptFlowStudio 현장 안전·위험 예측 자동화</p></div>";
        return new MailMessage(from, to, "[PromptFlowStudio] 이메일 본인인증 번호", body) { IsBodyHtml = true };
    }

    private static MailMessage BuildTestMessage(string from, string to)
    {
        var body =
            "<div style='font-family:Pretendard,Segoe UI,sans-serif;max-width:480px'>" +
            "<h2 style='color:#d97706;margin:0 0 12px'>✅ PromptFlowStudio SMTP 연결 테스트</h2>" +
            "<p>이 메일이 보인다면 환경설정 화면에서 지정한 SMTP 서버로 <b>정상 발송</b>이 확인된 것입니다.</p>" +
            $"<p style='color:#555;font-size:.85rem'>보내는 사람: <b>{from}</b> · 수신자: <b>{to}</b></p>" +
            "<p style='color:#888;font-size:.8rem'>PromptFlowStudio 현장 안전·위험 예측 자동화 · 메일 발송 설정 검증</p></div>";
        return new MailMessage(from, to, "[PromptFlowStudio] SMTP 테스트 메일", body) { IsBodyHtml = true };
    }
}
