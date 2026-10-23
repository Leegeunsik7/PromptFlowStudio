using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.RetentionHeatmap;

/// <summary>수집 출처 — 실데이터(Live)와 그 외를 UI에서 반드시 구분 표기.</summary>
public enum RetentionSource : byte
{
    None = 0,
    /// <summary>YouTube Analytics API 실응답.</summary>
    Live = 1,
    /// <summary>자격 증명 미설정 — 모의 수집 엔진(결정적 프리셋 곡선 · 실데이터 아님).</summary>
    Mock = 2,
    /// <summary>장애(429 · 단절 · 인증 · 파싱) — 직전 실응답 캐시.</summary>
    FallbackCache = 3,
    /// <summary>장애 + 캐시 없음 — 세이프티 프리셋 곡선(실데이터 아님).</summary>
    FallbackPreset = 4,
}

/// <summary>마지막 수집 실패 원인.</summary>
public enum RetentionFault : byte
{
    None = 0,
    NoCredentials = 1,
    RateLimited = 2,
    CooldownActive = 3,
    Network = 4,
    Timeout = 5,
    Unauthorized = 6,
    HttpError = 7,
    Malformed = 8,
    EmptyReport = 9,
}

/// <summary>자격 증명 — 환경 변수 전용(소스/설정 파일 저장 금지).</summary>
public readonly record struct YouTubeAnalyticsCredentials(string? AccessToken, string? ClientId, string? ClientSecret, string? RefreshToken, string? VideoId)
{
    public const string EnvAccessToken = "PROMPTFLOW_YT_ACCESS_TOKEN", EnvClientId = "PROMPTFLOW_YT_CLIENT_ID", EnvClientSecret = "PROMPTFLOW_YT_CLIENT_SECRET",
                        EnvRefreshToken = "PROMPTFLOW_YT_REFRESH_TOKEN", EnvVideoId = "PROMPTFLOW_YT_VIDEO_ID";

    public bool CanRefresh => !string.IsNullOrEmpty(ClientId) && !string.IsNullOrEmpty(ClientSecret) && !string.IsNullOrEmpty(RefreshToken);
    public bool IsConfigured => !string.IsNullOrEmpty(VideoId) && (!string.IsNullOrEmpty(AccessToken) || CanRefresh);

    public static YouTubeAnalyticsCredentials FromEnvironment() => new(
        Environment.GetEnvironmentVariable(EnvAccessToken), Environment.GetEnvironmentVariable(EnvClientId), Environment.GetEnvironmentVariable(EnvClientSecret),
        Environment.GetEnvironmentVariable(EnvRefreshToken), Environment.GetEnvironmentVariable(EnvVideoId));
}

/// <summary>
/// [PromptFlow Studio v17.99.1] 📡 YouTube Analytics 시청 지속률 수집기(<c>audienceWatchRatio</c> × <c>elapsedVideoTimeRatio</c>).
///   ▸ 자격 증명(환경 변수 OAuth2 액세스 토큰 또는 client_id/secret/refresh_token) 미설정 → <see cref="RetentionSource.Mock"/> — 실데이터로 위장 금지.
///   ▸ HTTP 429 → Retry-After 쿨다운(쿨다운 중 요청 0 · 자동 재시도 없음) · 단절/타임아웃/401/파싱 실패 → 직전 실응답 캐시 → 없으면 세이프티 프리셋.
///   ▸ 응답 파싱 = <see cref="Utf8JsonReader"/>(columnHeaders 이름으로 열 인덱스 결정 · 0.01~1.00 비율 100 점 → 초 단위 곡선 선형 보간).
/// </summary>
public sealed class YouTubeAnalyticsRetentionCollector : IDisposable
{
    public const string ReportsEndpoint = "https://youtubeanalytics.googleapis.com/v2/reports";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const int MaxPoints = 128, DefaultCooldownSec = 60, MaxCooldownSec = 3600;
    public const float MaxWatchRatio = 1f;

    private readonly HttpClient _http;
    private readonly Func<YouTubeAnalyticsCredentials> _creds;
    private readonly Func<DateTimeOffset> _clock;
    private readonly float[] _x = new float[MaxPoints], _y = new float[MaxPoints];
    private readonly float[] _cache = new float[AudienceRetentionDropoffDetectorEngine.MaxSec + 1];
    private readonly object _gate = new();
    private string? _token;
    private DateTimeOffset _tokenExpiry, _cooldownUntil;
    private int _cacheLen;

    public YouTubeAnalyticsRetentionCollector(Func<YouTubeAnalyticsCredentials>? credentials = null, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null, TimeSpan? timeout = null)
    {
        _creds = credentials ?? YouTubeAnalyticsCredentials.FromEnvironment;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public RetentionSource LastSource { get; private set; }
    public RetentionFault LastFault { get; private set; }
    public int LastHttpStatus { get; private set; }
    public int LastPoints { get; private set; }
    public long Requests { get; private set; }
    public long LiveSuccesses { get; private set; }
    public long Fallbacks { get; private set; }
    public long RateLimitHits { get; private set; }
    public long TokenRefreshes { get; private set; }
    public DateTimeOffset CooldownUntil { get { lock (_gate) return _cooldownUntil; } }
    public bool HasCache { get { lock (_gate) return _cacheLen > 0; } }
    public bool IsConfigured => _creds().IsConfigured;

    public static string SourceName(RetentionSource s) => s switch
    {
        RetentionSource.Live => "🟢 YouTube Analytics 실데이터",
        RetentionSource.Mock => "🧪 모의 수집(자격 증명 없음 · 실데이터 아님)",
        RetentionSource.FallbackCache => "🟡 장애 폴백 — 직전 실응답 캐시",
        RetentionSource.FallbackPreset => "🟠 장애 폴백 — 세이프티 프리셋(실데이터 아님)",
        _ => "⚪ 미수집",
    };

    public static string FaultName(RetentionFault f) => f switch
    {
        RetentionFault.None => "정상",
        RetentionFault.NoCredentials => "자격 증명 미설정",
        RetentionFault.RateLimited => "HTTP 429 할당량 초과",
        RetentionFault.CooldownActive => "429 쿨다운 중(요청 생략)",
        RetentionFault.Network => "네트워크 단절",
        RetentionFault.Timeout => "응답 시간 초과",
        RetentionFault.Unauthorized => "인증 실패(401/403)",
        RetentionFault.HttpError => "HTTP 오류",
        RetentionFault.Malformed => "응답 형식 오류",
        RetentionFault.EmptyReport => "빈 리포트",
        _ => "알 수 없음",
    };

    /// <summary>
    /// 수집 1회 — <paramref name="dst"/>[0..durationSec] 에 초 단위 지속률을 기록. 반환 = 기록 샘플 수(durationSec + 1).
    /// 실패 시에도 폴백 곡선을 기록하며 <see cref="LastSource"/>로 출처를 알린다. <paramref name="preset"/> = 모의/세이프티 곡선.
    /// </summary>
    public async Task<int> CollectAsync(int durationSec, ReadOnlyMemory<float> preset, Memory<float> dst, CancellationToken ct = default)
    {
        durationSec = Math.Clamp(durationSec, 1, AudienceRetentionDropoffDetectorEngine.MaxSec);
        var n = durationSec + 1;
        if (dst.Length < n) throw new ArgumentException("dst too small", nameof(dst));
        var creds = _creds();
        if (!creds.IsConfigured)
        {
            Finish(RetentionSource.Mock, RetentionFault.NoCredentials, 0);
            return CopyPreset(preset.Span, dst.Span, n);
        }
        var now = _clock();
        lock (_gate)
        {
            if (now < _cooldownUntil) return Fallback(RetentionFault.CooldownActive, 429, preset.Span, dst.Span, n);
        }
        Requests++;
        try
        {
            var token = await GetTokenAsync(creds, ct).ConfigureAwait(false);
            if (token is null) return Fallback(RetentionFault.Unauthorized, LastHttpStatus, preset.Span, dst.Span, n);
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildReportUri(creds.VideoId!, now));
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                RateLimitHits++;
                var wait = RetryAfter(resp, now);
                lock (_gate) _cooldownUntil = now + wait;
                return Fallback(RetentionFault.RateLimited, status, preset.Span, dst.Span, n);
            }
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                lock (_gate) { _token = null; _tokenExpiry = default; }
                return Fallback(RetentionFault.Unauthorized, status, preset.Span, dst.Span, n);
            }
            if (!resp.IsSuccessStatusCode) return Fallback(RetentionFault.HttpError, status, preset.Span, dst.Span, n);
            var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            int points;
            lock (_gate) points = ParseReport(body, _x, _y);
            if (points < 0) return Fallback(RetentionFault.Malformed, status, preset.Span, dst.Span, n);
            if (points == 0) return Fallback(RetentionFault.EmptyReport, status, preset.Span, dst.Span, n);
            lock (_gate)
            {
                Resample(_x.AsSpan(0, points), _y.AsSpan(0, points), dst.Span[..n]);
                dst.Span[..n].CopyTo(_cache);
                _cacheLen = n;
            }
            LastPoints = points;
            LiveSuccesses++;
            Finish(RetentionSource.Live, RetentionFault.None, status);
            return n;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Fallback(RetentionFault.Timeout, 0, preset.Span, dst.Span, n);
        }
        catch (HttpRequestException ex)
        {
            return Fallback(RetentionFault.Network, ex.StatusCode is null ? 0 : (int)ex.StatusCode.Value, preset.Span, dst.Span, n);
        }
    }

    /// <summary>쿨다운/캐시/토큰 초기화(설정 변경 후).</summary>
    public void Reset()
    {
        lock (_gate) { _cooldownUntil = default; _cacheLen = 0; _token = null; _tokenExpiry = default; }
        LastSource = RetentionSource.None; LastFault = RetentionFault.None; LastHttpStatus = LastPoints = 0;
        Requests = LiveSuccesses = Fallbacks = RateLimitHits = TokenRefreshes = 0;
    }

    public static Uri BuildReportUri(string videoId, DateTimeOffset now)
    {
        var sb = new StringBuilder(ReportsEndpoint, 320);
        sb.Append("?ids=channel%3D%3DMINE&startDate=2005-02-14&endDate=").Append(now.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.Append("&metrics=audienceWatchRatio%2CrelativeRetentionPerformance&dimensions=elapsedVideoTimeRatio&filters=video%3D%3D").Append(Uri.EscapeDataString(videoId));
        return new Uri(sb.ToString());
    }

    /// <summary>
    /// resultTable 파싱 — columnHeaders 의 elapsedVideoTimeRatio · audienceWatchRatio 열을 찾아 rows 를 (x, y)로. 반환 = 점 수 · −1 = 형식 오류.
    /// x 는 (0, 1] 오름차순 · y 는 [0, MaxWatchRatio] 로 클램프(재시청으로 1 초과 가능).
    /// </summary>
    public static int ParseReport(ReadOnlySpan<byte> json, Span<float> xs, Span<float> ys)
    {
        try
        {
            var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            int xCol = -1, yCol = -1, n = 0;
            var sawRows = false;
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return -1;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("columnHeaders"u8))
                {
                    if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return -1;
                    var col = 0;
                    while (r.Read() && r.TokenType == JsonTokenType.StartObject)
                    {
                        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                        {
                            var isName = r.ValueTextEquals("name"u8);
                            if (!r.Read()) return -1;
                            if (isName && r.TokenType == JsonTokenType.String)
                            {
                                if (r.ValueTextEquals("elapsedVideoTimeRatio"u8)) xCol = col;
                                else if (r.ValueTextEquals("audienceWatchRatio"u8)) yCol = col;
                            }
                            else r.Skip();
                        }
                        col++;
                    }
                }
                else if (r.ValueTextEquals("rows"u8))
                {
                    if (xCol < 0 || yCol < 0) return -1;   // 헤더가 rows 보다 먼저 와야 함(API 응답 순서)
                    if (!r.Read()) return -1;
                    if (r.TokenType == JsonTokenType.Null) { sawRows = true; continue; }
                    if (r.TokenType != JsonTokenType.StartArray) return -1;
                    sawRows = true;
                    while (r.Read() && r.TokenType == JsonTokenType.StartArray)
                    {
                        float x = float.NaN, y = float.NaN;
                        var col = 0;
                        while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                        {
                            if (r.TokenType == JsonTokenType.Number)
                            {
                                if (col == xCol) x = (float)r.GetDouble();
                                else if (col == yCol) y = (float)r.GetDouble();
                            }
                            else if (r.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject) r.Skip();
                            col++;
                        }
                        if (float.IsNaN(x) || float.IsNaN(y) || !float.IsFinite(x) || !float.IsFinite(y) || x < 0f || x > 1f) return -1;
                        if (n >= xs.Length || n >= ys.Length) return -1;
                        if (n > 0 && x <= xs[n - 1]) return -1;   // 오름차순 위반
                        xs[n] = x; ys[n] = Math.Clamp(y, 0f, MaxWatchRatio);
                        n++;
                    }
                }
                else
                {
                    if (!r.Read()) return -1;
                    r.Skip();
                }
            }
            return sawRows ? n : (xCol >= 0 && yCol >= 0 ? 0 : -1);
        }
        catch (JsonException) { return -1; }
        catch (InvalidOperationException) { return -1; }
    }

    /// <summary>비율 축(0~1) 점 → 초 단위 곡선(dst.Length = 초 + 1) 선형 보간 · 0 s = 첫 점 이전 1.0 시작.</summary>
    public static void Resample(ReadOnlySpan<float> xs, ReadOnlySpan<float> ys, Span<float> dst)
    {
        var sec = dst.Length - 1;
        var j = 0;
        for (var s = 0; s <= sec; s++)
        {
            var x = sec == 0 ? 0f : s / (float)sec;
            while (j < xs.Length && xs[j] < x) j++;
            float v;
            if (j == 0) v = xs.Length == 0 ? 1f : xs[0] <= 0f ? ys[0] : 1f + (ys[0] - 1f) * (x / xs[0]);
            else if (j >= xs.Length) v = ys[^1];
            else
            {
                var t = (x - xs[j - 1]) / Math.Max(1e-6f, xs[j] - xs[j - 1]);
                v = ys[j - 1] + (ys[j] - ys[j - 1]) * t;
            }
            dst[s] = Math.Clamp(v, 0f, MaxWatchRatio);
        }
    }

    // ══════════════════════ 내부 ══════════════════════

    private async Task<string?> GetTokenAsync(YouTubeAnalyticsCredentials c, CancellationToken ct)
    {
        var now = _clock();
        lock (_gate) if (_token is not null && now < _tokenExpiry) return _token;
        if (!c.CanRefresh) return c.AccessToken;
        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("client_id", c.ClientId!),
            new KeyValuePair<string, string>("client_secret", c.ClientSecret!),
            new KeyValuePair<string, string>("refresh_token", c.RefreshToken!),
        });
        using var resp = await _http.PostAsync(TokenEndpoint, form, ct).ConfigureAwait(false);
        LastHttpStatus = (int)resp.StatusCode;
        if (!resp.IsSuccessStatusCode) return string.IsNullOrEmpty(c.AccessToken) ? null : c.AccessToken;
        var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!TryParseToken(body, out var token, out var expiresIn)) return string.IsNullOrEmpty(c.AccessToken) ? null : c.AccessToken;
        TokenRefreshes++;
        lock (_gate) { _token = token; _tokenExpiry = now + TimeSpan.FromSeconds(Math.Max(0, expiresIn - 60)); }
        return token;
    }

    public static bool TryParseToken(ReadOnlySpan<byte> json, out string? token, out int expiresIn)
    {
        token = null; expiresIn = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals("access_token"u8)) { r.Read(); if (r.TokenType == JsonTokenType.String) token = r.GetString(); }
                else if (r.ValueTextEquals("expires_in"u8)) { r.Read(); if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var e)) expiresIn = e; }
                else { r.Read(); r.Skip(); }
            }
            return !string.IsNullOrEmpty(token);
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static TimeSpan RetryAfter(HttpResponseMessage resp, DateTimeOffset now)
    {
        var ra = resp.Headers.RetryAfter;
        var sec = DefaultCooldownSec;
        if (ra?.Delta is { } d) sec = (int)Math.Ceiling(d.TotalSeconds);
        else if (ra?.Date is { } at) sec = (int)Math.Ceiling((at - now).TotalSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(sec, 1, MaxCooldownSec));
    }

    private int Fallback(RetentionFault fault, int status, ReadOnlySpan<float> preset, Span<float> dst, int n)
    {
        Fallbacks++;
        lock (_gate)
        {
            if (_cacheLen > 0)
            {
                // 캐시 길이가 요청 길이와 다르면 비율 축으로 재보간
                if (_cacheLen == n) _cache.AsSpan(0, n).CopyTo(dst);
                else
                {
                    Span<float> xs = stackalloc float[_cacheLen];
                    for (var i = 0; i < _cacheLen; i++) xs[i] = i / (float)(_cacheLen - 1);
                    Resample(xs, _cache.AsSpan(0, _cacheLen), dst[..n]);
                }
                Finish(RetentionSource.FallbackCache, fault, status);
                return n;
            }
        }
        Finish(RetentionSource.FallbackPreset, fault, status);
        return CopyPreset(preset, dst, n);
    }

    private static int CopyPreset(ReadOnlySpan<float> preset, Span<float> dst, int n)
    {
        if (preset.Length >= n) { preset[..n].CopyTo(dst); return n; }
        if (preset.Length < 2) { dst[..n].Fill(preset.Length == 1 ? preset[0] : 1f); return n; }
        Span<float> xs = preset.Length <= 256 ? stackalloc float[preset.Length] : new float[preset.Length];
        for (var i = 0; i < preset.Length; i++) xs[i] = i / (float)(preset.Length - 1);
        Resample(xs, preset, dst[..n]);
        return n;
    }

    private void Finish(RetentionSource s, RetentionFault f, int status) { LastSource = s; LastFault = f; LastHttpStatus = status; }

    public void Dispose() => _http.Dispose();
}
