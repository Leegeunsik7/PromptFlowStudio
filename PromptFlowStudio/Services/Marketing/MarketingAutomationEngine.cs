using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot

namespace PromptFlowStudio.Services.Marketing;

/// <summary>웹훅 송출 대상 — 이름·URL·인증 토큰·플랫폼.</summary>
public sealed record WebhookTarget(string Id, string Name, string Url, string AuthToken, string Platform);

/// <summary>발행 페이로드 — 캡션·해시태그·플랫폼·영상 참조·예약 시각.</summary>
public sealed record MarketingPayload(string Caption, IReadOnlyList<string> Hashtags, string Platform, string VideoRef, string? ScheduledAtIso);

/// <summary>발행 결과 — 대상·성공·상태코드·메시지.</summary>
public sealed record PublishResult(string Target, bool Ok, int StatusCode, string Message);

/// <summary>예약 발행 항목.</summary>
public sealed record ScheduledPost(WebhookTarget Target, MarketingPayload Payload, DateTimeOffset ScheduledAt);

/// <summary>
/// [PromptFlowStudio] 웹훅 기반 실시간 SNS 자율 발행 파이프라인(v3.5.0 · 관찰 가능 싱글턴 · 순수 BCL <see cref="HttpClient"/> · 외부 SDK 0).
///   ▸ 렌더 완료 즉시 메타데이터(해시태그·타깃 캡션)와 함께 지정 미디어 서버/SNS 엔드포인트로 <b>Custom Webhook + REST(Bearer)</b> POST 송출.
///   ▸ 웹훅 대상 프로필 영속(DataAreaRoot/webhook_targets.json)·다중 대상 <b>병렬 발행</b>·<b>예약 발행 큐</b>(도래분 자동 처리).
///   ▸ 서버 부재/오프라인은 Graceful(결과 실패로 기록·정상 흐름 유지). 페이로드는 System.Text.Json 무손실 직렬화.
/// </summary>
public sealed class MarketingAutomationEngine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly object _lock = new();
    private readonly List<WebhookTarget> _targets = new();
    private readonly List<ScheduledPost> _scheduled = new();
    private readonly List<PublishResult> _log = new();

    /// <summary>상태 변경 방송.</summary>
    public event Action? OnChange;

    public MarketingAutomationEngine() => TryLoad();

    private string StorePath => Path.Combine(LicenseService.DataAreaRoot(), "webhook_targets.json");

    /// <summary>등록된 웹훅 대상.</summary>
    public IReadOnlyList<WebhookTarget> Targets { get { lock (_lock) return _targets.ToList(); } }
    /// <summary>발행 로그(최신 우선).</summary>
    public IReadOnlyList<PublishResult> Log { get { lock (_lock) return _log.AsEnumerable().Reverse().ToList(); } }
    /// <summary>예약 발행 대기 수.</summary>
    public int ScheduledCount { get { lock (_lock) return _scheduled.Count; } }

    // ══════════════════════ 대상 관리 ══════════════════════

    /// <summary>웹훅 대상 등록/갱신(ID 없으면 발급).</summary>
    public WebhookTarget SaveTarget(WebhookTarget t)
    {
        var norm = t with { Id = string.IsNullOrWhiteSpace(t.Id) ? "wh_" + Guid.NewGuid().ToString("N")[..6] : t.Id };
        lock (_lock) { var i = _targets.FindIndex(x => x.Id == norm.Id); if (i >= 0) _targets[i] = norm; else _targets.Add(norm); }
        TrySave(); OnChange?.Invoke();
        return norm;
    }

    /// <summary>웹훅 대상 삭제.</summary>
    public bool RemoveTarget(string id) { bool r; lock (_lock) r = _targets.RemoveAll(x => x.Id == id) > 0; if (r) { TrySave(); OnChange?.Invoke(); } return r; }

    // ══════════════════════ 발행 ══════════════════════

    /// <summary>단일 대상으로 즉시 발행한다(웹훅 POST · Bearer). 오프라인/오류는 실패 결과(Graceful).</summary>
    public async Task<PublishResult> PublishAsync(WebhookTarget target, MarketingPayload payload, CancellationToken ct = default)
    {
        if (!IsValidWebhook(target.Url)) return LogResult(new(target.Name, false, 0, "웹훅 URL 무효"));
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, target.Url)
            { Content = new StringContent(BuildPayloadJson(payload), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrWhiteSpace(target.AuthToken)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.AuthToken);
            req.Headers.TryAddWithoutValidation("X-PromptFlow-Platform", payload.Platform);
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            return LogResult(new(target.Name, resp.IsSuccessStatusCode, (int)resp.StatusCode, resp.IsSuccessStatusCode ? "발행 완료" : $"실패({(int)resp.StatusCode})"));
        }
        catch (Exception ex) { Program.Log("Marketing.Publish", ex); return LogResult(new(target.Name, false, 0, "오프라인/오류: " + ex.Message)); }
    }

    /// <summary>여러 대상으로 병렬 발행한다.</summary>
    public async Task<IReadOnlyList<PublishResult>> PublishAllAsync(IEnumerable<WebhookTarget> targets, MarketingPayload payload, int maxParallel = 3, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = (targets ?? Array.Empty<WebhookTarget>()).Select(async t =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await PublishAsync(t, payload, ct).ConfigureAwait(false); } finally { gate.Release(); }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }

    // ══════════════════════ 예약 ══════════════════════

    /// <summary>예약 발행 등록.</summary>
    public void Schedule(WebhookTarget target, MarketingPayload payload, DateTimeOffset at)
    {
        lock (_lock) _scheduled.Add(new ScheduledPost(target, payload, at));
        OnChange?.Invoke();
    }

    /// <summary>도래한 예약을 반환(큐에서 제거 · 순수 시간 비교).</summary>
    public IReadOnlyList<ScheduledPost> DueNow(DateTimeOffset now)
    {
        lock (_lock)
        {
            var due = _scheduled.Where(s => s.ScheduledAt <= now).ToList();
            foreach (var d in due) _scheduled.Remove(d);
            return due;
        }
    }

    /// <summary>도래한 예약을 실제 발행 처리한다.</summary>
    public async Task<IReadOnlyList<PublishResult>> ProcessDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = DueNow(now);
        var results = new List<PublishResult>();
        foreach (var d in due) results.Add(await PublishAsync(d.Target, d.Payload, ct).ConfigureAwait(false));
        return results;
    }

    // ══════════════════════ 메타데이터 빌더(순수 · 테스트 대상) ══════════════════════

    /// <summary>페이로드 → JSON(무손실).</summary>
    public static string BuildPayloadJson(MarketingPayload p) => JsonSerializer.Serialize(p, PromptFlowJsonContext.Default.MarketingPayload);

    /// <summary>웹훅 URL 유효성(http/https).</summary>
    public static bool IsValidWebhook(string? url)
        => !string.IsNullOrWhiteSpace(url) && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>플랫폼/주제 기반 해시태그 자동 생성.</summary>
    public static IReadOnlyList<string> BuildHashtags(string platform, string? topic)
    {
        var tags = new List<string> { "#PromptFlowStudio", "#AI영상", "#B2B" };
        var plat = (platform ?? "").ToLowerInvariant();
        if (plat.Contains("youtube")) tags.Add("#Shorts");
        else if (plat.Contains("tiktok")) tags.Add("#틱톡");
        else if (plat.Contains("insta")) tags.Add("#릴스");
        if (!string.IsNullOrWhiteSpace(topic))
            foreach (var w in topic.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Take(3))
                tags.Add("#" + w.Trim());
        return tags.Distinct().ToList();
    }

    /// <summary>타깃 캡션 자동 생성.</summary>
    public static string BuildCaption(string? title, string platform)
        => $"{(string.IsNullOrWhiteSpace(title) ? "PromptFlow Studio 신규 홍보" : title)} — AI로 제작한 {platform} 홍보 영상 🚀";

    private PublishResult LogResult(PublishResult r)
    {
        lock (_lock) _log.Add(r);
        OnChange?.Invoke();
        return r;
    }

    private void TrySave()
    {
        try { List<WebhookTarget> snap; lock (_lock) snap = _targets.ToList(); File.WriteAllText(StorePath, JsonSerializer.Serialize(snap, PromptFlowJsonContext.Default.WebhookTargetList), new UTF8Encoding(false)); }
        catch (Exception ex) { Program.Log("Marketing.Save", ex); }
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize(File.ReadAllText(StorePath), PromptFlowJsonContext.Default.WebhookTargetList);
            if (list is not null) lock (_lock) { _targets.Clear(); _targets.AddRange(list); }
        }
        catch (Exception ex) { Program.Log("Marketing.Load", ex); }
    }
}
