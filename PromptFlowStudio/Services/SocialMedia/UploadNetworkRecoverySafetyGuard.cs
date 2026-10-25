using System.Globalization;
using System.Net.Http;
using System.Net.Sockets;

namespace PromptFlowStudio.Services.SocialMedia;

public enum UploadPlatform : byte { YouTube, Instagram }

public enum UploadPhase : byte { Idle, Initiating, Uploading, Recovering, Processing, Publishing, Completed, Failed, Canceled }

/// <summary>업로드 진행 1 표본 — 값 타입(이벤트 방출 시 박싱 없음).</summary>
public readonly struct UploadProgress
{
    public readonly UploadPlatform Platform;
    public readonly UploadPhase Phase;
    public readonly long Sent, Total;
    public readonly int Chunk, Chunks, Resumes, RateLimitWaits;
    /// <summary>EMA 평활 전송 속도(MB/s · 1 MB = 1,048,576 B).</summary>
    public readonly double MBps;
    /// <summary>0~100.</summary>
    public readonly double Percent;
    /// <summary>잔여 시간(초 · 속도 미확정 = -1).</summary>
    public readonly double EtaSec;

    public UploadProgress(UploadPlatform platform, UploadPhase phase, long sent, long total, int chunk, int chunks, int resumes, int rateLimitWaits, double mbps, double percent, double etaSec)
    {
        Platform = platform; Phase = phase; Sent = sent; Total = total; Chunk = chunk; Chunks = chunks;
        Resumes = resumes; RateLimitWaits = rateLimitWaits; MBps = mbps; Percent = percent; EtaSec = etaSec;
    }
}

/// <summary>전송률 · 속도 · ETA 산출기(무할당 · Stopwatch 틱 입력).</summary>
public struct UploadProgressMeter
{
    /// <summary>순간 속도 EMA 가중치.</summary>
    public const double Alpha = 0.3;

    private long _total, _lastTicks, _lastBytes;
    private double _bps;

    public readonly long Total => _total;
    public readonly double BytesPerSec => _bps;

    public void Start(long total, long ticks)
    {
        _total = total < 0 ? 0 : total;
        _lastTicks = ticks;
        _lastBytes = 0;
        _bps = 0;
    }

    /// <summary>재개 시 서버 확인 오프셋으로 기준점만 이동(속도 이력 유지).</summary>
    public void Rebase(long sent, long ticks)
    {
        _lastBytes = sent;
        _lastTicks = ticks;
    }

    public UploadProgress Update(UploadPlatform platform, UploadPhase phase, long sent, int chunk, int chunks, int resumes, int rateLimitWaits, long ticks, long ticksPerSecond)
    {
        if (sent < 0) sent = 0;
        if (sent > _total) sent = _total;
        var dt = ticks - _lastTicks;
        var db = sent - _lastBytes;
        if (dt > 0 && db > 0)
        {
            var inst = db * (double)ticksPerSecond / dt;
            _bps = _bps <= 0 ? inst : _bps + Alpha * (inst - _bps);
            _lastTicks = ticks;
            _lastBytes = sent;
        }
        var pct = _total == 0 ? 0 : sent * 100.0 / _total;
        var eta = sent >= _total && _total > 0 ? 0 : _bps > 0 ? (_total - sent) / _bps : -1;
        return new UploadProgress(platform, phase, sent, _total, chunk, chunks, resumes, rateLimitWaits, _bps / 1_048_576.0, pct, eta);
    }
}

/// <summary>
/// [PromptFlow Studio v18.0.5] 🛡 업로드 네트워크 자동 복원 &amp; 프로그레스 가드.
///   ▸ 소켓 단절(HttpRequestException · IOException · SocketException · 호출자 비취소 TaskCanceled) → <c>Content-Range: bytes */Total</c> 상태 질의 →
///     서버 <c>Range: bytes=0-N</c> 기준 N+1 부터 즉시 이어올리기(최대 <see cref="MaxResumeAttempts"/> 연속).
///   ▸ 429 · 5xx → 지수 백오프(Retry-After 우선 · 최대 <see cref="MaxRateLimitRetries"/> 회) 안전 대기.
///   ▸ 전송률 · MB/s · ETA 를 <see cref="UploadProgress"/> 구조체로 <see cref="OnProgress"/> 방출(구독자 예외 격리).
/// </summary>
public sealed class UploadNetworkRecoverySafetyGuard
{
    public const int MaxRateLimitRetries = 3, MaxResumeAttempts = 5;

    private long _rateLimitWaits, _resumes, _statusQueries, _connectionLosses, _emitted;

    public event Action<UploadProgress>? OnProgress;

    /// <summary>백오프 기준 지연(시도 n → Base × 2^n · 상한 <see cref="MaxDelay"/>).</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(32);
    /// <summary>대기 실행기(테스트 · 시뮬레이션 주입용).</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = static (d, ct) => Task.Delay(d, ct);

    public long RateLimitWaits => Interlocked.Read(ref _rateLimitWaits);
    public long Resumes => Interlocked.Read(ref _resumes);
    public long StatusQueries => Interlocked.Read(ref _statusQueries);
    public long ConnectionLosses => Interlocked.Read(ref _connectionLosses);
    public long Emitted => Interlocked.Read(ref _emitted);
    public UploadProgress Last { get; private set; }

    /// <summary>재시도 대상 상태(429 레이트 리밋 · 일시 서버 오류).</summary>
    public static bool IsRetryableStatus(int status) => status is 429 or 500 or 502 or 503 or 504;

    /// <summary>연결 유실(재개 대상) — 호출자 취소는 제외.</summary>
    public static bool IsConnectionLoss(Exception ex, CancellationToken ct) => ex switch
    {
        OperationCanceledException => !ct.IsCancellationRequested,
        HttpRequestException or IOException or SocketException => true,
        _ => false,
    };

    /// <summary>지수 백오프 — Retry-After 가 있으면 우선(상한 적용).</summary>
    public TimeSpan BackoffFor(int attempt, TimeSpan? retryAfter = null)
    {
        if (retryAfter is { } ra && ra > TimeSpan.Zero) return ra < MaxDelay ? ra : MaxDelay;
        var shift = attempt < 0 ? 0 : attempt > 16 ? 16 : attempt;
        var ticks = BaseDelay.Ticks << shift;
        return ticks <= 0 || ticks > MaxDelay.Ticks ? MaxDelay : TimeSpan.FromTicks(ticks);
    }

    /// <summary>429/5xx 1 회 대기 — 한도 초과면 false(호출자 실패 처리).</summary>
    public async Task<bool> WaitRetryAsync(int attempt, TimeSpan? retryAfter, CancellationToken ct)
    {
        if (attempt >= MaxRateLimitRetries) return false;
        Interlocked.Increment(ref _rateLimitWaits);
        await Delay(BackoffFor(attempt, retryAfter), ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>연결 유실 1 회 기록 · 짧은 재연결 대기 — 연속 한도 초과면 false.</summary>
    public async Task<bool> BeginRecoveryAsync(int consecutive, CancellationToken ct)
    {
        Interlocked.Increment(ref _connectionLosses);
        if (consecutive >= MaxResumeAttempts) return false;
        await Delay(BackoffFor(consecutive), ct).ConfigureAwait(false);
        return true;
    }

    internal void CountStatusQuery() => Interlocked.Increment(ref _statusQueries);
    internal void CountResume() => Interlocked.Increment(ref _resumes);

    /// <summary>
    /// 308 <c>Range</c> 값(<c>bytes=0-N</c>) → 다음 전송 오프셋(N+1). 비어 있으면 0(서버 수신 없음). 형식 위반 · 0 이 아닌 시작은 false.
    /// </summary>
    public static bool TryParseResumeRange(ReadOnlySpan<char> value, out long nextOffset)
    {
        nextOffset = 0;
        value = value.Trim();
        if (value.IsEmpty) return true;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        value = value[6..];
        var dash = value.IndexOf('-');
        if (dash <= 0) return false;
        if (!long.TryParse(value[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var first) || first != 0) return false;
        if (!long.TryParse(value[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var last) || last < first) return false;
        nextOffset = last + 1;
        return true;
    }

    /// <summary>상태 질의 헤더 값 <c>bytes */Total</c>(무할당 기록).</summary>
    public static bool TryWriteStatusQueryRange(long total, Span<char> dst, out int written)
    {
        written = 0;
        if (total <= 0 || !"bytes */".TryCopyTo(dst)) return false;
        if (!total.TryFormat(dst[8..], out var n, default, CultureInfo.InvariantCulture)) return false;
        written = 8 + n;
        return true;
    }

    /// <summary>청크 헤더 값 <c>bytes a-b/Total</c>(무할당 기록).</summary>
    public static bool TryWriteContentRange(long offset, int length, long total, Span<char> dst, out int written)
    {
        written = 0;
        if (offset < 0 || length <= 0 || offset + length > total || !"bytes ".TryCopyTo(dst)) return false;
        var p = 6;
        if (!offset.TryFormat(dst[p..], out var n, default, CultureInfo.InvariantCulture)) return false;
        p += n;
        if (p >= dst.Length) return false;
        dst[p++] = '-';
        if (!(offset + length - 1).TryFormat(dst[p..], out n, default, CultureInfo.InvariantCulture)) return false;
        p += n;
        if (p >= dst.Length) return false;
        dst[p++] = '/';
        if (!total.TryFormat(dst[p..], out n, default, CultureInfo.InvariantCulture)) return false;
        written = p + n;
        return true;
    }

    /// <summary>진행 표본 방출(구독자 예외 격리).</summary>
    public void Emit(in UploadProgress p)
    {
        Last = p;
        Interlocked.Increment(ref _emitted);
        try { OnProgress?.Invoke(p); }
        catch (Exception) { /* UI 구독자 결함 격리 — 전송은 계속 */ }
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _rateLimitWaits, 0);
        Interlocked.Exchange(ref _resumes, 0);
        Interlocked.Exchange(ref _statusQueries, 0);
        Interlocked.Exchange(ref _connectionLosses, 0);
        Interlocked.Exchange(ref _emitted, 0);
        Last = default;
    }

    /// <summary>응답 Retry-After(초 · 날짜) 해석.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra is null) return null;
        if (ra.Delta is { } d) return d;
        if (ra.Date is { } at) { var w = at - DateTimeOffset.UtcNow; return w > TimeSpan.Zero ? w : TimeSpan.Zero; }
        return null;
    }
}
