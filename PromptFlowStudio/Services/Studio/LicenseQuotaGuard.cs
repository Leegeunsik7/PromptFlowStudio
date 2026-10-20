using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot

namespace PromptFlowStudio.Services.Studio;

/// <summary>라이선스 페이로드 — 서명 대상(사용자·에디션·만료일·일/월 쿼터).</summary>
public sealed record LicensePayload(string Licensee, string Edition, string Expiry, int DailyQuota, int MonthlyQuota);

/// <summary>라이선스/쿼터 상태 — 배포 적격성 + 카드 표시용 상세.</summary>
public sealed record LicenseStatus(
    bool Ok, bool Licensed, bool Expired, bool QuotaExceeded,
    string Licensee, string Edition, DateTime? ExpiryDate,
    int DailyUsed, int DailyLimit, int MonthlyUsed, int MonthlyLimit, string Tier, string Message);

/// <summary>
/// [PromptFlowStudio] 상용 라이선스 &amp; 오프라인 쿼터 가드(v2.7.0 · 관찰 가능 싱글턴 · 순수 BCL <see cref="RSA"/>/<see cref="SHA256"/> · 외부 NuGet 0).
///   ▸ <b>오프라인 RSA/SHA256 서명</b> 라이선스 토큰(<c>payloadB64.signatureB64</c>)을 임베드 공개키로 검증 — 네트워크 없이 정품/만료 판정.
///   ▸ <b>일일/월간 생성 쿼터</b>를 로컬 파일(<see cref="Services.Account.LicenseService.DataAreaRoot"/>)에 기록·롤오버 관리(<see cref="RecordGeneration"/>).
///   ▸ 라이선스 만료/쿼터 초과 시 <see cref="LicenseStatus.Ok"/>=false → 대시보드가 직관적 경고 카드를 표출.
///   ▸ 공개키 미구성 또는 토큰 부재 시 <b>체험판(Trial)</b> 모드로 동작(소량 쿼터). 기존 라이선스 게이트(SerialLicenseService)와 별개.
/// </summary>
public sealed class LicenseQuotaGuard
{
    // 배포 시 실제 공개키(Base64 SPKI DER)로 교체. 비어 있으면 체험판 모드로만 동작한다.
    private const string EmbeddedPublicKeyB64 = "";

    private const int TrialDaily = 3, TrialMonthly = 30;

    private readonly object _lock = new();

    /// <summary>검증에 사용할 공개키(Base64 SPKI). 기본은 임베드 키. 배포/테스트에서 주입 가능.</summary>
    public string PublicKeyB64 { get; set; } = EmbeddedPublicKeyB64;

    /// <summary>상태 변경 방송(경고 카드/쿼터 바 갱신).</summary>
    public event Action? OnChange;

    /// <summary>현재 라이선스/쿼터 상태.</summary>
    public LicenseStatus Status { get; private set; }

    public LicenseQuotaGuard() { Status = Evaluate(LoadToken()); }

    private bool IsKeyConfigured => PublicKeyB64.Trim().Length >= 100;   // 임베드 키 구성 여부(placeholder 방어)

    private string TokenPath => Path.Combine(LicenseService.DataAreaRoot(), "commercial_license.key");
    private string QuotaPath => Path.Combine(LicenseService.DataAreaRoot(), "commercial_quota.json");

    // ══════════════════════ 라이선스 토큰 ══════════════════════

    /// <summary>라이선스 토큰을 저장하고 상태를 갱신한다.</summary>
    public LicenseStatus SetLicense(string? token)
    {
        try { File.WriteAllText(TokenPath, (token ?? "").Trim(), new UTF8Encoding(false)); } catch (Exception ex) { Program.Log("License.Set", ex); }
        return Refresh();
    }

    /// <summary>현재 저장 토큰 + 쿼터로 상태를 재평가·방송한다.</summary>
    public LicenseStatus Refresh()
    {
        Status = Evaluate(LoadToken());
        OnChange?.Invoke();
        return Status;
    }

    private string? LoadToken()
    {
        try { return File.Exists(TokenPath) ? File.ReadAllText(TokenPath).Trim() : null; }
        catch { return null; }
    }

    /// <summary>토큰 + 현재 쿼터로 라이선스 상태를 계산한다(순수 · 부작용 없음).</summary>
    public LicenseStatus Evaluate(string? token)
    {
        var (_, dCount, __, mCount) = LoadQuota();

        if (string.IsNullOrWhiteSpace(token) || !IsKeyConfigured)
            return Trial(dCount, mCount);

        var (ok, payload, msg) = VerifyToken(token!, PublicKeyB64);
        if (!ok || payload is null)
            return new(false, false, false, false, "-", "Invalid", null, dCount, 0, mCount, 0, "✗", "라이선스 서명 검증 실패: " + msg);

        DateTime? exp = TryDate(payload.Expiry, out var e) ? e : null;
        var expired = exp is { } ex2 && ex2.Date < DateTime.Today;
        var dLimit = payload.DailyQuota; var mLimit = payload.MonthlyQuota;
        var quotaExceeded = (dLimit > 0 && dCount >= dLimit) || (mLimit > 0 && mCount >= mLimit);
        var okAll = !expired && !quotaExceeded;

        var message = expired ? $"라이선스 만료됨({payload.Expiry}) — 갱신이 필요합니다."
            : quotaExceeded ? "생성 쿼터를 초과했습니다 — 상위 플랜 또는 내일 재시도."
            : $"정품 인증 · {payload.Edition} · {payload.Licensee}";
        return new(okAll, true, expired, quotaExceeded, payload.Licensee, payload.Edition, exp,
            dCount, dLimit, mCount, mLimit, TierOf(payload.Edition), message);
    }

    private LicenseStatus Trial(int dCount, int mCount)
    {
        var exceeded = dCount >= TrialDaily || mCount >= TrialMonthly;
        return new(!exceeded, false, false, exceeded, "Trial User", "Trial", null,
            dCount, TrialDaily, mCount, TrialMonthly, "T",
            exceeded ? "체험판 쿼터 소진 — 상용 라이선스를 등록하세요." : "체험판 모드 · 상용 라이선스 미등록");
    }

    // ══════════════════════ RSA/SHA256 서명(테스트/배포 도구) ══════════════════════

    /// <summary>토큰(payloadB64.sigB64)을 공개키로 검증한다. (성공, 페이로드, 메시지). 순수·부작용 없음.</summary>
    public static (bool Ok, LicensePayload? Payload, string Message) VerifyToken(string token, string publicKeyB64)
    {
        try
        {
            var dot = (token ?? "").IndexOf('.');
            if (dot <= 0 || dot >= token!.Length - 1) return (false, null, "토큰 형식 오류");
            var payloadBytes = Convert.FromBase64String(token[..dot]);
            var sig = Convert.FromBase64String(token[(dot + 1)..]);

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyB64), out _);
            if (!rsa.VerifyData(payloadBytes, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                return (false, null, "서명 불일치");

            var payload = JsonSerializer.Deserialize(payloadBytes, PromptFlowJsonContext.Default.QuotaLicensePayload);
            return payload is null ? (false, null, "페이로드 파싱 실패") : (true, payload, "OK");
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    /// <summary>페이로드를 개인키(Base64 PKCS8)로 서명해 토큰을 만든다(라이선스 발급/테스트 도구).</summary>
    public static string SignToken(LicensePayload payload, string privateKeyB64)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, PromptFlowJsonContext.Default.QuotaLicensePayload);
        using var rsa = RSA.Create();
        rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyB64), out _);
        var sig = rsa.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Convert.ToBase64String(bytes) + "." + Convert.ToBase64String(sig);
    }

    // ══════════════════════ 쿼터(로컬 파일 · 일/월 롤오버) ══════════════════════

    /// <summary>생성 건수를 누적한다(일/월 자동 롤오버). 상태 갱신·방송.</summary>
    public LicenseStatus RecordGeneration(int count = 1)
    {
        lock (_lock)
        {
            var (day, dCount, month, mCount) = LoadQuota();
            var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var thisMonth = DateTime.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            if (day != today) { day = today; dCount = 0; }
            if (month != thisMonth) { month = thisMonth; mCount = 0; }
            dCount += Math.Max(0, count); mCount += Math.Max(0, count);
            SaveQuota(day, dCount, month, mCount);
        }
        return Refresh();
    }

    /// <summary>쿼터 상태 로드(일/월 롤오버 반영해 반환 · 저장은 하지 않음).</summary>
    private (string Day, int DailyCount, string Month, int MonthlyCount) LoadQuota()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var thisMonth = DateTime.Today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        try
        {
            if (File.Exists(QuotaPath))
            {
                var q = JsonSerializer.Deserialize(File.ReadAllText(QuotaPath), PromptFlowJsonContext.Default.QuotaState);
                if (q is not null)
                {
                    var d = q.Day == today ? q.DailyCount : 0;
                    var m = q.Month == thisMonth ? q.MonthlyCount : 0;
                    return (today, d, thisMonth, m);
                }
            }
        }
        catch (Exception ex) { Program.Log("License.LoadQuota", ex); }
        return (today, 0, thisMonth, 0);
    }

    private void SaveQuota(string day, int dCount, string month, int mCount)
    {
        try { File.WriteAllText(QuotaPath, JsonSerializer.Serialize(new QuotaState(day, dCount, month, mCount), PromptFlowJsonContext.Default.QuotaState), new UTF8Encoding(false)); }
        catch (Exception ex) { Program.Log("License.SaveQuota", ex); }
    }

    internal sealed record QuotaState(string Day, int DailyCount, string Month, int MonthlyCount);

    private static bool TryDate(string? s, out DateTime dt)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out dt);

    private static string TierOf(string? edition) => (edition ?? "").ToLowerInvariant() switch
    {
        "enterprise" => "E", "studio" => "S", "commercial" => "C", "pro" => "P", _ => "L",
    };
}
