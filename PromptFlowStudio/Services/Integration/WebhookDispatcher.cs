using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Mobile;   // RemoteRenderClient.IsValidWorkerUrl(URL 검증 재사용)

namespace PromptFlowStudio.Services.Integration;

/// <summary>video.completed 웹훅 페이로드(원본 익명 타입 대체 · 소스 생성 직렬화 호환 명명 타입).</summary>
internal sealed record VideoCompletedWebhookPayload(string Project, string Label, string Url, string Kind, long AtMs);

/// <summary>
/// [PromptFlow Studio v6.1.0] B2B Webhook 비동기 디스패처(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0 · IDisposable).
///   ▸ <see cref="Channel{T}"/> 무제한 큐 + 단일 백그라운드 소비 루프 → UI/렌더 파이프라인을 <b>블로킹하지 않고</b> 이벤트를 순차 발송(FireAndForget).
///   ▸ 각 페이로드는 <see cref="WebhookSigner"/> HMAC-SHA256 서명 후 등록된 모든 대상 URL 로 POST(X-PromptFlow-Signature 헤더).
///   ▸ 5xx/타임아웃/단선은 <b>지수 백오프</b>(1·2·4·8초) 최대 4회 재시도 후 실패 정산 · 4xx 는 재시도 무의미하므로 즉시 실패.
///   ▸ Graceful: 대상 미구성 시 로컬 로그만 보관(발송 성공으로 간주) · 어떤 예외도 밖으로 던지지 않는다.
///   ⚠ <see cref="OnChange"/> 는 백그라운드(발송) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링할 것.
/// </summary>
public sealed class WebhookDispatcher : IDisposable
{
    private const int MaxAttempts = 5;      // 최초 1 + 재시도 4(백오프 1·2·4·8초)
    private const int LogCap = 200;

    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly Channel<WebhookPayload> _channel =
        Channel.CreateUnbounded<WebhookPayload>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    private readonly object _gate = new();
    private readonly List<WebhookDelivery> _log = new();
    private readonly List<string> _targets = new();
    private string _secret = "";
    private int _queued;

    public WebhookDispatcher()
    {
        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    /// <summary>큐/로그 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;

    // ── 상태(HUD) ──
    public int QueuedCount => Volatile.Read(ref _queued);
    public bool HasTargets { get { lock (_gate) return _targets.Count > 0; } }
    public IReadOnlyList<string> Targets { get { lock (_gate) return _targets.ToList(); } }
    public IReadOnlyList<WebhookDelivery> RecentDeliveries { get { lock (_gate) return _log.Take(50).ToList(); } }
    public long TotalDelivered { get; private set; }
    public long TotalFailed { get; private set; }

    private void Notify() => OnChange?.Invoke();

    // ── 구성 ───────────────────────────────────────────────────────────────

    /// <summary>공유 시크릿 + 대상 목록을 한 번에 설정(둘 다 선택).</summary>
    public void Configure(string? secret, IEnumerable<string>? targets = null)
    {
        lock (_gate)
        {
            if (secret is not null) _secret = secret.Trim();
            if (targets is not null)
            {
                _targets.Clear();
                foreach (var t in targets)
                    if (RemoteRenderClient.IsValidWorkerUrl(t) && !_targets.Contains(t.Trim()))
                        _targets.Add(t.Trim());
            }
        }
        Notify();
    }

    /// <summary>대상 엔드포인트 추가(http/https/ws/wss · 중복 무시). 반환=추가 여부.</summary>
    public bool AddTarget(string? url)
    {
        var u = (url ?? "").Trim();
        if (!RemoteRenderClient.IsValidWorkerUrl(u)) return false;
        lock (_gate)
        {
            if (_targets.Contains(u)) return false;
            _targets.Add(u);
        }
        Notify();
        return true;
    }

    /// <summary>대상 제거.</summary>
    public void RemoveTarget(string url)
    {
        lock (_gate) _targets.Remove((url ?? "").Trim());
        Notify();
    }

    // ── 발송 ───────────────────────────────────────────────────────────────

    /// <summary>
    /// 이벤트를 서명해 백그라운드 큐에 넣는다(비블로킹). 렌더 파이프라인/UI 에서 fire-and-forget 로 호출.
    /// </summary>
    public void FireAndForget<T>(string eventType, T payloadObj, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            var body = JsonSerializer.Serialize(payloadObj, typeInfo);
            var ts = DateTimeOffset.UtcNow;
            string secret;
            lock (_gate) secret = _secret;
            var sig = WebhookSigner.Sign(secret, WebhookSigner.CanonicalMessage(ts.ToUnixTimeMilliseconds(), body));
            var p = new WebhookPayload(Guid.NewGuid().ToString("N")[..12], eventType, body, ts, sig);
            if (_channel.Writer.TryWrite(p))
            {
                Interlocked.Increment(ref _queued);
                Notify();
            }
        }
        catch (Exception ex) { Program.Log("Webhook.Enqueue", ex); }   // 발송 큐잉 실패는 파이프라인에 영향 없음(Graceful)
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var p in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queued);
                await DeliverAsync(p, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* 종료 */ }
        catch (Exception ex) { Program.Log("Webhook.Pump", ex); }
    }

    private async Task DeliverAsync(WebhookPayload p, CancellationToken ct)
    {
        List<string> targets;
        lock (_gate) targets = _targets.ToList();

        if (targets.Count == 0)
        {
            // 대상 미구성 → 로컬 로그만(발송 성공 간주 · Graceful).
            Record(p, "(로컬 로그)", WebhookDeliveryStatus.Delivered, 0, null, "엔드포인트 미구성 — 로컬 로그만 보관");
            TotalDelivered++;
            return;
        }

        foreach (var url in targets)
        {
            var d = Record(p, url, WebhookDeliveryStatus.Queued, 0, null, null);
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (ct.IsCancellationRequested) { d.Status = WebhookDeliveryStatus.Failed; d.Error = "취소"; Notify(); break; }

                d.Status = WebhookDeliveryStatus.Delivering; d.Attempts = attempt; Notify();
                var (ok, code, err, retriable) = await PostAsync(url, p, ct).ConfigureAwait(false);
                d.HttpStatus = code;

                if (ok) { d.Status = WebhookDeliveryStatus.Delivered; d.Error = null; TotalDelivered++; Notify(); break; }

                d.Error = err;
                if (!retriable || attempt >= MaxAttempts)
                {
                    d.Status = WebhookDeliveryStatus.Failed; TotalFailed++; Notify();
                    break;
                }
                // 지수 백오프 1·2·4·8초.
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { d.Status = WebhookDeliveryStatus.Failed; d.Error = "취소"; Notify(); return; }
            }
        }
    }

    private async Task<(bool Ok, int? Code, string? Err, bool Retriable)> PostAsync(string url, WebhookPayload p, CancellationToken ct)
    {
        try
        {
            using var content = new StringContent(p.JsonBody, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            req.Headers.TryAddWithoutValidation("X-PromptFlow-Event", p.EventType);
            req.Headers.TryAddWithoutValidation("X-PromptFlow-Timestamp", p.TimestampMs.ToString());
            req.Headers.TryAddWithoutValidation("X-PromptFlow-Signature", p.SignatureHeader);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _http.SendAsync(req, linked.Token).ConfigureAwait(false);

            var code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode) return (true, code, null, false);
            if (code is >= 400 and < 500) return (false, code, $"거부({code}) — 재시도 무의미", false);   // 4xx=재시도 안 함
            return (false, code, $"서버 오류({code})", true);                                              // 5xx=재시도
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return (false, null, "취소", false); }
        catch (OperationCanceledException) { return (false, null, "타임아웃", true); }
        catch (Exception ex) { Program.Log("Webhook.Post", ex); return (false, null, "단선: " + Short(ex), true); }
    }

    private WebhookDelivery Record(WebhookPayload p, string url, WebhookDeliveryStatus status, int attempts, int? code, string? err)
    {
        var d = new WebhookDelivery
        {
            Id = p.Id, EventType = p.EventType, TargetUrl = url, CreatedAt = p.TimestampUtc,
            Status = status, Attempts = attempts, HttpStatus = code, Error = err,
            SignatureShort = p.Signature.Length >= 12 ? p.Signature[..12] : p.Signature,
        };
        lock (_gate)
        {
            _log.Insert(0, d);
            if (_log.Count > LogCap) _log.RemoveRange(LogCap, _log.Count - LogCap);
        }
        Notify();
        return d;
    }

    private static string Short(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Length <= 100 ? m : m[..100] + "…";
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 무시 */ }
        try { _channel.Writer.TryComplete(); } catch { /* 무시 */ }
        try { _http.Dispose(); } catch { /* 무시 */ }
        try { _cts.Dispose(); } catch { /* 무시 */ }
    }
}
