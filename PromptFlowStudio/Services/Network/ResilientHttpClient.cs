using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Network;

/// <summary>
/// 🛡️ 지능형 지수 백오프(Jitter Retry) — DashScope/Gemini 일시 오류(429·502·503·504·<see cref="HttpRequestException"/>) 자동 재시도.
///   ▸ 지연 = 2^(n-1) 초 + Random Jitter(0~500ms) · 최대 <see cref="MaxRetries"/>회.
///   ▸ 재시도 직전 <see cref="OnRetryAttempt"/>(current, max, delay) 통지 — 작업실 ⏳ 알림. 구독자 예외 격리.
///   ▸ 요청 메시지는 재전송 불가 → 팩토리로 매 시도 재생성. 비일시 오류(4xx 등)는 즉시 반환.
/// </summary>
public sealed class ResilientHttpClient
{
    public const int MaxRetries = 3;
    public const int MaxJitterMs = 500;

    /// <summary>프로세스 공유 인스턴스(DI 싱글턴 · 정적 다운로더 공용).</summary>
    public static ResilientHttpClient Shared { get; } = new();

    /// <summary>지연 실행기(테스트 주입 — 실시간 대기 생략).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = static (d, ct) => Task.Delay(d, ct);
    /// <summary>지터 공급자(0~MaxJitterMs).</summary>
    public Func<int> Jitter { get; init; } = static () => Random.Shared.Next(0, MaxJitterMs + 1);

    /// <summary>재시도 통지(current 1부터, max, 대기 시간).</summary>
    public event Action<int, int, TimeSpan>? OnRetryAttempt;

    public static bool IsTransient(HttpStatusCode code)
        => code is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    /// <summary>n번째 재시도(1부터) 지연 = 2^(n-1)초 + jitter.</summary>
    public static TimeSpan ComputeDelay(int retry, int jitterMs)
    {
        if (retry < 1) retry = 1;
        if (retry > 10) retry = 10;
        if (jitterMs < 0) jitterMs = 0; else if (jitterMs > MaxJitterMs) jitterMs = MaxJitterMs;
        return TimeSpan.FromMilliseconds((1000L << (retry - 1)) + jitterMs);
    }

    /// <summary>재시도 직전 대기 + 통지(외부 루프 결선용).</summary>
    public async Task WaitBeforeRetryAsync(int retry, CancellationToken ct)
    {
        int j;
        try { j = Jitter(); } catch (Exception ex) { Program.Log("Resilient.Jitter", ex); j = 0; }
        var d = ComputeDelay(retry, j);
        Raise(retry, d);
        await Delay(d, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 일시 오류 재시도 송신 — 성공·비일시 응답은 즉시 반환(호출측 Dispose 책임).
    /// 재시도 소진 시 마지막 응답 반환 또는 마지막 <see cref="HttpRequestException"/> 재던짐.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpClient http, Func<HttpRequestMessage> requestFactory, CancellationToken ct = default,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(requestFactory);
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            HttpResponseMessage? resp = null;
            using var req = requestFactory();
            try
            {
                resp = await http.SendAsync(req, completion, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < MaxRetries && !ct.IsCancellationRequested)
            {
                await WaitBeforeRetryAsync(attempt + 1, ct).ConfigureAwait(false);
                continue;
            }
            if (attempt >= MaxRetries || !IsTransient(resp.StatusCode)) return resp;
            resp.Dispose();
            await WaitBeforeRetryAsync(attempt + 1, ct).ConfigureAwait(false);
        }
    }

    private void Raise(int current, TimeSpan delay)
    {
        var h = OnRetryAttempt;
        if (h is null) return;
        try { h(current, MaxRetries, delay); } catch (Exception ex) { Program.Log("Resilient.OnRetry", ex); }
    }
}
