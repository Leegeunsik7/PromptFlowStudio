using System;
using System.Security.Cryptography;
using System.Text;

namespace PromptFlowStudio.Services.Integration;

/// <summary>Webhook 1건의 발송 생명주기 — 대기/발송중/완료/실패.</summary>
public enum WebhookDeliveryStatus { Queued, Delivering, Delivered, Failed }

/// <summary>
/// [PromptFlow Studio v6.1.0] 외부 B2B 엔드포인트로 보낼 서명된 Webhook 페이로드(불변 값).
///   ▸ <see cref="Signature"/> = HMAC-SHA256(secret, "{timestampMs}.{body}") 16진 소문자 — 수신 서버가 위·변조 검증.
/// </summary>
public sealed record WebhookPayload(
    string Id, string EventType, string JsonBody, DateTimeOffset TimestampUtc, string Signature)
{
    /// <summary>서명 헤더 값(sha256=...) — 수신 서버 표준 형식.</summary>
    public string SignatureHeader => "sha256=" + Signature;
    /// <summary>타임스탬프(Unix ms) — 리플레이 방지 창 검증용.</summary>
    public long TimestampMs => TimestampUtc.ToUnixTimeMilliseconds();
}

/// <summary>
/// [PromptFlow Studio v6.1.0] 발송 로그 1건(관찰용 · 상태가 제자리 갱신되는 살아있는 엔티티라 record 아님).
/// </summary>
public sealed class WebhookDelivery
{
    public string Id { get; init; } = "";
    public string EventType { get; init; } = "";
    public string TargetUrl { get; set; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Queued;
    public int Attempts { get; set; }
    public int? HttpStatus { get; set; }
    public string? Error { get; set; }
    /// <summary>서명 앞 12자(로그 식별·전체 노출 방지).</summary>
    public string SignatureShort { get; init; } = "";

    public bool IsTerminal => Status is WebhookDeliveryStatus.Delivered or WebhookDeliveryStatus.Failed;
}

/// <summary>
/// [PromptFlow Studio v6.1.0] HMAC-SHA256 Webhook 서명 엔진(순수 BCL · 외부 NuGet 0).
///   ▸ 비밀키가 비어 있으면 고정 기본 시크릿으로 폴백(Graceful · 데모/오프라인).
/// </summary>
public static class WebhookSigner
{
    private const string DefaultSecret = "promptflow-b2b-default-secret";

    /// <summary>메시지를 HMAC-SHA256 으로 서명하고 16진 소문자 문자열을 반환한다.</summary>
    public static string Sign(string? secret, string? message)
    {
        var key = string.IsNullOrWhiteSpace(secret) ? DefaultSecret : secret!;
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var hash = h.ComputeHash(Encoding.UTF8.GetBytes(message ?? ""));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>서명을 상수시간 비교로 검증한다(타이밍 공격 방어).</summary>
    public static bool Verify(string? secret, string? message, string? signatureHex)
    {
        var expected = Sign(secret, message);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(signatureHex ?? ""));
    }

    /// <summary>발송 메시지 정규형: "{timestampMs}.{body}"(타임스탬프 바인딩 서명).</summary>
    public static string CanonicalMessage(long timestampMs, string body) => timestampMs + "." + (body ?? "");
}
