namespace PromptFlowStudio.Services.Ui;

/// <summary>트렌드 피드 출처.</summary>
public enum TrendFeedSource : byte
{
    /// <summary>라이브 플래그 미설정 — 내장 Mock 피드(네트워크 호출 0).</summary>
    Mock,
    /// <summary>실 피드 수신 · 스키마 검증 통과.</summary>
    Live,
    /// <summary>라이브 시도 실패(타임아웃 · HTTP 오류 · 스키마 위반) → Mock 폴백.</summary>
    Fallback,
}

/// <summary>
/// [PromptFlow Studio v18.0.7] 📡 실시간 트렌드 RSS 라이브 수신 클라이언트(Pure BCL HttpClient).
///   ▸ 환경변수 <see cref="EnvFlag"/>=1 일 때만 실 네트워크 호출(Google Trends KR RSS → <see cref="Feeds"/> 순서 시도).
///     미설정이면 <see cref="RealtimeTrendRssAndAffiliateValidator.MockFeed"/> 를 동기 완료(0 ms · 네트워크 0)로 반환.
///   ▸ 외부 실패 1 초 내 폴백: 전체 예산 <see cref="BudgetMs"/> 연결 CTS · 시도 간 지수 백오프 · 예산 소진/오류/스키마 위반 = Mock 폴백.
///   ▸ 스키마 즉시 검증: &lt;rss 루트 + <see cref="RealtimeTrendRssAndAffiliateValidator.Parse"/> 1 건 이상.
///   ▸ 메모리: 수신 char 버퍼 1 개(<see cref="MaxFeedChars"/>)를 생성 시 1 회 확보 · 매 수신 재사용(StreamReader 블록 읽기 → 동일 버퍼 누적).
///     반환 <see cref="ReadOnlyMemory{T}"/> 는 다음 <see cref="FetchAsync"/> 전까지 유효(0-카피).
/// </summary>
public sealed class LiveTrendRssHttpFetcher : IDisposable
{
    public const string EnvFlag = "PROMPTFLOW_TREND_LIVE";
    public const string GoogleTrendsKr = "https://trends.google.com/trends/trending/rss?geo=KR";
    public const int MaxFeedChars = 256 * 1024;
    private const int ReadBlockChars = 8 * 1024;

    private readonly HttpClient _http;
    private readonly char[] _buf = new char[MaxFeedChars];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TrendSpike[] _probe = new TrendSpike[RealtimeTrendRssAndAffiliateValidator.MaxSpikes];
    private int _len;

    public LiveTrendRssHttpFetcher() : this(null) { }

    /// <summary>handler 주입(테스트용 · null = 기본 SocketsHttpHandler).</summary>
    public LiveTrendRssHttpFetcher(HttpMessageHandler? handler)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan; // 예산은 연결 CTS 로 관리
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("PromptFlowStudio/18.0");
    }

    /// <summary>시도 순서(첫 성공 채택) — 기본 Google Trends KR · 플랫폼 급상승 피드 추가 가능.</summary>
    public List<string> Feeds { get; } = new() { GoogleTrendsKr };
    /// <summary>외부 호출 전체 예산(ms) — 초과 시 Mock 폴백.</summary>
    public int BudgetMs { get; set; } = 1000;
    public int MaxAttempts { get; set; } = 3;
    public int BackoffBaseMs { get; set; } = 50;
    /// <summary>테스트용 라이브 강제(null = 환경변수 판정).</summary>
    public bool? ForceLive { get; set; }

    public bool IsLive => ForceLive ?? string.Equals(Environment.GetEnvironmentVariable(EnvFlag), "1", StringComparison.Ordinal);
    public TrendFeedSource LastSource { get; private set; } = TrendFeedSource.Mock;
    public string? LastError { get; private set; }
    public string? LastUrl { get; private set; }
    public int LastSpikes { get; private set; }
    public long LiveFetches { get; private set; }
    public long Fallbacks { get; private set; }

    /// <summary>
    /// 트렌드 피드 수신: 라이브 미설정 = Mock 동기 완료 · 라이브 = 예산 내 시도(지수 백오프) → 실패 시 Mock 폴백.
    /// </summary>
    public ValueTask<ReadOnlyMemory<char>> FetchAsync(CancellationToken ct = default)
    {
        if (!IsLive)
        {
            LastSource = TrendFeedSource.Mock; LastError = null; LastUrl = null;
            return new ValueTask<ReadOnlyMemory<char>>(RealtimeTrendRssAndAffiliateValidator.MockFeed.AsMemory());
        }
        return FetchLiveAsync(ct);
    }

    private async ValueTask<ReadOnlyMemory<char>> FetchLiveAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(Math.Max(1, BudgetMs));
            string? err = null;
            for (var attempt = 0; attempt < Math.Max(1, MaxAttempts); attempt++)
            {
                for (var f = 0; f < Feeds.Count; f++)
                {
                    try
                    {
                        if (await TryReceiveAsync(Feeds[f], budget.Token).ConfigureAwait(false))
                        {
                            LastSource = TrendFeedSource.Live; LastError = null; LastUrl = Feeds[f];
                            LiveFetches++;
                            return _buf.AsMemory(0, _len);
                        }
                        err = "스키마 위반(" + Feeds[f] + ")";
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { err = "예산 " + BudgetMs + " ms 초과"; goto fallback; }
                    catch (HttpRequestException ex) { err = ex.Message; }
                    catch (IOException ex) { err = ex.Message; }
                }
                try { await Task.Delay(BackoffBaseMs << attempt, budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { err ??= "예산 " + BudgetMs + " ms 초과"; break; }
            }
        fallback:
            LastSource = TrendFeedSource.Fallback; LastError = err; LastUrl = null;
            Fallbacks++;
            return RealtimeTrendRssAndAffiliateValidator.MockFeed.AsMemory();
        }
        finally { _gate.Release(); }
    }

    /// <summary>1 피드 수신 → 재사용 버퍼 누적 → 스키마 검증. 성공 = true.</summary>
    private async Task<bool> TryReceiveAsync(string url, CancellationToken ct)
    {
        using var rsp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!rsp.IsSuccessStatusCode) throw new HttpRequestException("HTTP " + (int)rsp.StatusCode, null, rsp.StatusCode);
        await using var stream = await rsp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: ReadBlockChars);
        var len = 0;
        while (len < _buf.Length)
        {
            var n = await reader.ReadAsync(_buf.AsMemory(len, Math.Min(ReadBlockChars, _buf.Length - len)), ct).ConfigureAwait(false);
            if (n == 0) break;
            len += n;
        }
        _len = len;
        return Validate(_buf.AsSpan(0, len));
    }

    private bool Validate(ReadOnlySpan<char> feed)
    {
        if (feed.IndexOf("<rss", StringComparison.Ordinal) < 0) { LastSpikes = 0; return false; }
        LastSpikes = RealtimeTrendRssAndAffiliateValidator.Parse(feed, _probe);
        return LastSpikes > 0;
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }
}
