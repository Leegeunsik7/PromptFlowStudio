using System;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Account;

/// <summary>이메일 본인인증 발송 결과. Sent=실발송 성공 · Demo=SMTP 미구성 데모모드(코드 인라인 노출) · Error=실패사유.</summary>
public sealed record OtpSendResult(bool Sent, bool Demo, string? DemoCode, string? Error);

/// <summary>
/// [PromptFlowStudio] 회원가입 이메일 본인인증 OTP 발송 — BCL System.Net.Mail.SmtpClient 만 사용(외부 NuGet 0).
///   [환경설정 연동] 유효 SMTP 설정을 <see cref="SmtpConfigService"/> 에서 동적 조회한다 — 관리자 저장본(암호화 파일) 우선,
///   없으면 appsettings <c>Smtp:*</c> 폴백. 따라서 환경설정 › 메일(SMTP) 발송 설정을 저장하면 <b>재시작 없이</b> 인증 메일에도 즉시 반영된다.
///   미구성/비활성이면 <b>데모 모드</b>로 코드를 화면에 인라인 노출해 오프라인에서도 인증을 완료한다. 모니터링/스마트팜/PLAI/UAV/Stock와 동일 설계.
/// </summary>
public sealed class SiteEmailOtpService
{
    private readonly SmtpConfigService _smtp;
    public SiteEmailOtpService(SmtpConfigService smtp) => _smtp = smtp;

    /// <summary>실발송 가능 여부 — 유효 설정(저장본 또는 appsettings)이 실발송 조건을 모두 충족할 때 true. false 면 데모.</summary>
    public bool SmtpConfigured => _smtp.GetEffective().CanSend;

    public async Task<OtpSendResult> SendOtpAsync(string to, string code)
    {
        if (string.IsNullOrWhiteSpace(to)) return new OtpSendResult(false, false, null, "수신 이메일이 없습니다.");
        var cfg = _smtp.GetEffective();
        if (!cfg.CanSend) return new OtpSendResult(false, true, code, null);   // 데모 모드 — 코드 인라인 노출
        try
        {
            using var msg = BuildOtpMessage(cfg.From.Trim(), to.Trim(), code);
            using var client = new SmtpClient(cfg.Host.Trim(), cfg.Port)
            {
                EnableSsl = cfg.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Credentials = new NetworkCredential(cfg.AuthUser.Trim(), cfg.AppPassword),
                Timeout = 20000,
            };
            await client.SendMailAsync(msg).ConfigureAwait(false);
            return new OtpSendResult(true, false, null, null);
        }
        catch (Exception ex)
        {
            return new OtpSendResult(false, false, null, ex.Message);
        }
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
}
