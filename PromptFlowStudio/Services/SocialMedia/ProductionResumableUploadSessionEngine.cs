using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using PromptFlowStudio.Services.PlatformVisibility;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Services.SocialMedia;

/// <summary>업로드 1 회 결과(성공 시 플랫폼 ID · 퍼머링크).</summary>
public readonly struct UploadResult
{
    public readonly bool Ok;
    public readonly UploadPlatform Platform;
    public readonly string VideoId, Permalink;
    public readonly string? Error;
    public readonly int Status, Chunks, Resumes, RateLimitWaits;
    public readonly long Bytes;

    public UploadResult(bool ok, UploadPlatform platform, string videoId, string permalink, string? error, int status, int chunks, int resumes, int rateLimitWaits, long bytes)
    {
        Ok = ok; Platform = platform; VideoId = videoId; Permalink = permalink; Error = error;
        Status = status; Chunks = chunks; Resumes = resumes; RateLimitWaits = rateLimitWaits; Bytes = bytes;
    }
}

/// <summary>
/// Resumable 청크 커서(무할당) — 서버 확인 오프셋 기준으로만 전진(308 <c>Range</c> · 상태 질의 응답).
/// 슬라이스는 원본 <see cref="ReadOnlyMemory{T}"/> 뷰(힙 복사 0).
/// </summary>
public struct ResumableChunkCursor
{
    public readonly long Total;
    public readonly int ChunkSize;
    public long Offset;

    public ResumableChunkCursor(long total, int chunkSize)
    {
        Total = total < 0 ? 0 : total;
        ChunkSize = chunkSize <= 0 ? ProductionResumableUploadSessionEngine.ChunkQuantum : chunkSize;
        Offset = 0;
    }

    public readonly bool Done => Offset >= Total;
    public readonly int Chunks => Total == 0 ? 0 : (int)((Total + ChunkSize - 1) / ChunkSize);
    /// <summary>현재 전송 청크 번호(1-기준 · 재개 시 서버 오프셋 기준).</summary>
    public readonly int CurrentChunk => Total == 0 ? 0 : (int)Math.Min(Offset / ChunkSize + 1, Chunks);
    public readonly int NextLength => (int)Math.Min(ChunkSize, Total - Offset);

    public readonly ReadOnlyMemory<byte> Slice(ReadOnlyMemory<byte> video) => video.Slice((int)Offset, NextLength);

    /// <summary>308 · 상태 질의 <c>Range</c> 반영 — 형식 위반 · 총량 초과는 false(오프셋 불변).</summary>
    public bool Acknowledge(ReadOnlySpan<char> range)
    {
        if (!UploadNetworkRecoverySafetyGuard.TryParseResumeRange(range, out var next) || next > Total) return false;
        Offset = next;
        return true;
    }

    public void Complete() => Offset = Total;
}

/// <summary>
/// [PromptFlow Studio v18.0.5] 📡 프로덕션 Resumable 업로드 세션 엔진 — v18.0.4 조립 페이로드를 실 플랫폼 엔드포인트로 전송.
///   ▸ YouTube Data API v3 Resumable: POST 초기화(Location = 세션 URI) → <see cref="ChunkSize"/>(기본 8 MB · 256 KB 배수) 청크 PUT(Content-Range) →
///     308 Resume Incomplete(<c>Range: bytes=0-N</c>) → 최종 200/201 JSON <c>id</c> 파싱.
///   ▸ Instagram Graph Reels: 컨테이너 생성(upload_type=resumable) → rupload 바이너리 스트리밍(offset · file_size) → <c>status_code</c> FINISHED 폴링 → <c>media_publish</c> → 퍼머링크 조회.
///   ▸ 청크 바디 = <see cref="ReadOnlyMemory{T}"/> 슬라이스 스트리밍(256 KB 단위 기록 · 진행률 방출) — 영상 바이트 힙 복사 0.
///   ▸ 단절 · 429 · 5xx 는 <see cref="UploadNetworkRecoverySafetyGuard"/> 경유 복구. 토큰은 환경변수(하드코딩 없음) — LIVE 미설정 시 <see cref="ResumableUploadMockHandler"/> 결정적 폴백.
/// </summary>
public sealed class ProductionResumableUploadSessionEngine : IDisposable
{
    public const int ChunkQuantum = 256 * 1024, DefaultChunkSize = 8 * 1024 * 1024, StreamSlice = 256 * 1024, MaxProcessingPolls = 60;
    public const string EnvLive = "PROMPTFLOW_UPLOAD_LIVE", EnvYouTubeToken = "PROMPTFLOW_YT_UPLOAD_TOKEN", EnvInstagramToken = "PROMPTFLOW_IG_UPLOAD_TOKEN", EnvInstagramUser = "PROMPTFLOW_IG_USER_ID";
    public const string YouTubeInitUrl = "https://" + ProductionSocialUploadPayloadBuilder.YouTubeHost + ProductionSocialUploadPayloadBuilder.YouTubeResumablePath;
    public const string GraphBaseUrl = "https://graph.facebook.com/v21.0/", RuploadBaseUrl = "https://" + ProductionSocialUploadPayloadBuilder.InstagramRuploadHost + ProductionSocialUploadPayloadBuilder.InstagramRuploadPath;
    public const string YouTubeShortsBase = "https://youtube.com/shorts/";
    public const string SimulatedToken = "sim-upload-token", SimulatedInstagramUser = "17841400000000001";

    private readonly HttpClient _http;
    private readonly Func<string, string?> _env;
    private int _busy;
    private UploadProgressMeter _meter;
    private UploadPlatform _platform;
    private int _chunk, _chunks, _resumes, _waits;
    private long _sent, _sessions, _completed, _failures, _chunksSent, _bytesSent;

    public ProductionResumableUploadSessionEngine(HttpMessageHandler? handler = null, UploadNetworkRecoverySafetyGuard? guard = null, int chunkSize = DefaultChunkSize, Func<string, string?>? env = null)
    {
        if (chunkSize < ChunkQuantum || chunkSize % ChunkQuantum != 0) throw new ArgumentOutOfRangeException(nameof(chunkSize), "청크는 256 KB 배수여야 합니다(YouTube Resumable 규격).");
        _env = env ?? Environment.GetEnvironmentVariable;
        ChunkSize = chunkSize;
        Live = handler is null && _env(EnvLive) == "1";
        Mock = handler as ResumableUploadMockHandler ?? (handler is null && !Live ? new ResumableUploadMockHandler { Latency = TimeSpan.FromMilliseconds(15) } : null);
        Guard = guard ?? new UploadNetworkRecoverySafetyGuard();
        // 내장 시뮬레이션 — 백오프 단축(UI 체감용)
        if (handler is null && !Live && guard is null) { Guard.BaseDelay = TimeSpan.FromMilliseconds(20); Guard.MaxDelay = TimeSpan.FromMilliseconds(200); PollInterval = TimeSpan.FromMilliseconds(50); }
        _http = new HttpClient(handler ?? (Live ? new HttpClientHandler() : Mock!), disposeHandler: handler is null) { Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>단계 전환 · 완료 통지(진행률 표본은 <see cref="UploadNetworkRecoverySafetyGuard.OnProgress"/>).</summary>
    public event Action? OnChange;

    public UploadNetworkRecoverySafetyGuard Guard { get; }
    public ResumableUploadMockHandler? Mock { get; }
    public bool Live { get; }
    public string ModeName => Live ? "LIVE" : "시뮬레이션";
    public int ChunkSize { get; }
    /// <summary>Instagram 컨테이너 처리 상태 폴링 간격.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public UploadPhase Phase { get; private set; }
    public bool Busy => Volatile.Read(ref _busy) != 0;
    public long Sessions => Interlocked.Read(ref _sessions);
    public long Completed => Interlocked.Read(ref _completed);
    public long Failures => Interlocked.Read(ref _failures);
    public long ChunksSent => Interlocked.Read(ref _chunksSent);
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public UploadResult LastResult { get; private set; }

    // ══════════════════════ 진입점 ══════════════════════

    /// <summary>인계 페이로드 → 플랫폼 규격 메타데이터 조립(풀 버퍼) → 업로드. 토큰은 LIVE 시 환경변수.</summary>
    public async Task<UploadResult> UploadAsync(UploadPlatform platform, SocialPublishPayload payload, string privacy = "private", CancellationToken ct = default)
    {
        if (payload.IsEmpty || payload.Video.IsEmpty) return Reject(platform, "인계 영상 없음");
        var token = Live ? _env(platform == UploadPlatform.YouTube ? EnvYouTubeToken : EnvInstagramToken) : SimulatedToken;
        if (string.IsNullOrWhiteSpace(token)) return Reject(platform, "업로드 토큰 미설정(" + (platform == UploadPlatform.YouTube ? EnvYouTubeToken : EnvInstagramToken) + ")");
        var buf = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            if (platform == UploadPlatform.YouTube)
            {
                if (!ProductionSocialUploadPayloadBuilder.TryWriteYouTubeMetadataJson(payload, privacy, buf, out var n)) return Reject(platform, "메타데이터 조립 실패");
                return await UploadYouTubeAsync(payload.Video, buf.AsMemory(0, n), token, ct).ConfigureAwait(false);
            }
            var user = Live ? _env(EnvInstagramUser) : SimulatedInstagramUser;
            if (string.IsNullOrWhiteSpace(user)) return Reject(platform, "IG 사용자 ID 미설정(" + EnvInstagramUser + ")");
            if (!ProductionSocialUploadPayloadBuilder.TryWriteInstagramReelsContainerForm(payload, default, true, buf, out var f)) return Reject(platform, "컨테이너 폼 조립 실패");
            return await UploadInstagramReelsAsync(payload.Video, buf.AsMemory(0, f), user, token, ct).ConfigureAwait(false);
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }

    /// <summary>YouTube Resumable — 초기화 → 청크 PUT/308 → 완료 ID.</summary>
    public async Task<UploadResult> UploadYouTubeAsync(ReadOnlyMemory<byte> video, ReadOnlyMemory<byte> metadataJson, string token, CancellationToken ct = default)
    {
        if (!Enter(UploadPlatform.YouTube, video.Length)) return Reject(UploadPlatform.YouTube, "다른 업로드 진행 중");
        var cursor = new ResumableChunkCursor(video.Length, ChunkSize);
        try
        {
            if (video.IsEmpty) return Fail("빈 영상", 0, cursor);
            SetPhase(UploadPhase.Initiating);
            var init = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, YouTubeInitUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/mp4");
                req.Headers.TryAddWithoutValidation("X-Upload-Content-Length", video.Length.ToString(CultureInfo.InvariantCulture));
                var c = new ReadOnlyMemoryContent(metadataJson);
                c.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "UTF-8" };
                req.Content = c;
                return req;
            }, ct).ConfigureAwait(false);
            if (init.Code is not (200 or 201) || init.Location is null) return Fail("세션 초기화 실패", init.Code, cursor);
            var session = init.Location;
            if (session.Scheme != Uri.UriSchemeHttps) return Fail("세션 URI 스키마 위반", init.Code, cursor);

            SetPhase(UploadPhase.Uploading);
            int retry = 0, drops = 0, stalls = 0;
            var query = false;
            while (true)
            {
                Reply r;
                try
                {
                    _chunk = cursor.CurrentChunk;
                    r = query ? await QueryStatusAsync(session, token, cursor.Total, ct).ConfigureAwait(false) : await PutChunkAsync(session, token, video, cursor, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (UploadNetworkRecoverySafetyGuard.IsConnectionLoss(ex, ct))
                {
                    // 소켓 단절 → bytes */Total 상태 질의로 서버 수신 오프셋 확인 후 이어올리기
                    SetPhase(UploadPhase.Recovering);
                    if (!await Guard.BeginRecoveryAsync(drops++, ct).ConfigureAwait(false)) return Fail("네트워크 복구 한도 초과", 0, cursor);
                    query = true;
                    continue;
                }

                if (r.Code is 200 or 201)
                {
                    var id = TopLevelString(r.Body, "id"u8);
                    if (id is null || !PlatformJson.IsValidId(id)) return Fail("완료 응답 스키마 위반(id)", r.Code, cursor);
                    if (!query) Interlocked.Increment(ref _chunksSent);
                    cursor.Complete();
                    return Succeed(id, YouTubeShortsBase + id, r.Code, cursor);
                }
                if (r.Code == 308)
                {
                    var before = cursor.Offset;
                    if (!cursor.Acknowledge(r.Range)) return Fail("308 Range 형식 위반", r.Code, cursor);
                    // 서버가 진척 없이 308 반복 → 무한 루프 차단
                    if (!query && cursor.Offset <= before && ++stalls > UploadNetworkRecoverySafetyGuard.MaxResumeAttempts) return Fail("308 진척 없음", r.Code, cursor);
                    if (query) { Guard.CountResume(); _resumes++; _meter.Rebase(cursor.Offset, Stopwatch.GetTimestamp()); SetPhase(UploadPhase.Uploading); }
                    else Interlocked.Increment(ref _chunksSent);
                    query = false;
                    retry = drops = 0;
                    _chunk = cursor.CurrentChunk;
                    Report(cursor.Offset);
                    continue;
                }
                if (UploadNetworkRecoverySafetyGuard.IsRetryableStatus(r.Code))
                {
                    if (!await Guard.WaitRetryAsync(retry++, r.RetryAfter, ct).ConfigureAwait(false)) return Fail("재시도 한도(" + UploadNetworkRecoverySafetyGuard.MaxRateLimitRetries.ToString(CultureInfo.InvariantCulture) + ") 초과", r.Code, cursor);
                    _waits++;
                    query = r.Code >= 500;   // 5xx 는 수신 여부 불명 → 상태 질의 · 429 는 동일 청크 재전송
                    continue;
                }
                return Fail(r.Code is 404 or 410 ? "업로드 세션 만료" : "청크 전송 거부", r.Code, cursor);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Cancel(cursor); }
        catch (Exception ex) { return Fail(ex.Message, 0, cursor); }
        finally { Exit(); }
    }

    /// <summary>Instagram Reels — 컨테이너 → rupload 스트리밍 → FINISHED 폴링 → media_publish → 퍼머링크.</summary>
    public async Task<UploadResult> UploadInstagramReelsAsync(ReadOnlyMemory<byte> video, ReadOnlyMemory<byte> containerForm, string igUserId, string token, CancellationToken ct = default)
    {
        if (!Enter(UploadPlatform.Instagram, video.Length)) return Reject(UploadPlatform.Instagram, "다른 업로드 진행 중");
        var cursor = new ResumableChunkCursor(video.Length, ChunkSize);
        try
        {
            if (video.IsEmpty) return Fail("빈 영상", 0, cursor);
            if (!PlatformJson.IsValidId(igUserId)) return Fail("IG 사용자 ID 스키마 위반", 0, cursor);
            SetPhase(UploadPhase.Initiating);
            var created = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, GraphBaseUrl + igUserId + "/media");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var c = new ReadOnlyMemoryContent(containerForm);
                c.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                req.Content = c;
                return req;
            }, ct).ConfigureAwait(false);
            var container = created.Code == 200 ? TopLevelString(created.Body, "id"u8) : null;
            if (container is null || !PlatformJson.IsValidId(container)) return Fail("컨테이너 생성 실패", created.Code, cursor);

            // rupload — 남은 바이트 단일 스트림(256 KB 기록 단위 진행률) · 단절 시 서버 offset 질의 후 재개
            SetPhase(UploadPhase.Uploading);
            int retry = 0, drops = 0;
            while (true)
            {
                Reply r;
                try { r = await RuploadAsync(container, token, video, cursor, ct).ConfigureAwait(false); }
                catch (Exception ex) when (UploadNetworkRecoverySafetyGuard.IsConnectionLoss(ex, ct))
                {
                    SetPhase(UploadPhase.Recovering);
                    if (!await Guard.BeginRecoveryAsync(drops++, ct).ConfigureAwait(false)) return Fail("네트워크 복구 한도 초과", 0, cursor);
                    var st = await SendWithRetryAsync(() =>
                    {
                        var req = new HttpRequestMessage(HttpMethod.Get, RuploadBaseUrl + container);
                        req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
                        return req;
                    }, ct).ConfigureAwait(false);
                    Guard.CountStatusQuery();
                    var off = TopLevelLong(st.Body, "offset"u8);
                    if (st.Code != 200 || off < 0 || off > cursor.Total) return Fail("업로드 오프셋 질의 실패", st.Code, cursor);
                    cursor.Offset = off;
                    Guard.CountResume(); _resumes++;
                    _meter.Rebase(off, Stopwatch.GetTimestamp());
                    SetPhase(UploadPhase.Uploading);
                    continue;
                }
                if (r.Code == 200) { Interlocked.Increment(ref _chunksSent); cursor.Complete(); Report(cursor.Offset); break; }
                if (UploadNetworkRecoverySafetyGuard.IsRetryableStatus(r.Code) && await Guard.WaitRetryAsync(retry++, r.RetryAfter, ct).ConfigureAwait(false)) { _waits++; continue; }
                return Fail("rupload 거부", r.Code, cursor);
            }

            SetPhase(UploadPhase.Processing);
            for (var i = 0; ; i++)
            {
                if (i >= MaxProcessingPolls) return Fail("컨테이너 처리 시간 초과", 0, cursor);
                var st = await SendWithRetryAsync(() => Bearer(HttpMethod.Get, GraphBaseUrl + container + "?fields=status_code", token), ct).ConfigureAwait(false);
                var code = st.Code == 200 ? TopLevelString(st.Body, "status_code"u8) : null;
                if (code == "FINISHED") break;
                if (code is null or "ERROR" or "EXPIRED") return Fail("컨테이너 처리 실패(" + (code ?? "HTTP " + st.Code.ToString(CultureInfo.InvariantCulture)) + ")", st.Code, cursor);
                await Guard.Delay(PollInterval, ct).ConfigureAwait(false);
            }

            SetPhase(UploadPhase.Publishing);
            var pub = await SendWithRetryAsync(() => Bearer(HttpMethod.Post, GraphBaseUrl + igUserId + "/media_publish?creation_id=" + container, token), ct).ConfigureAwait(false);
            var media = pub.Code == 200 ? TopLevelString(pub.Body, "id"u8) : null;
            if (media is null || !PlatformJson.IsValidId(media)) return Fail("media_publish 실패", pub.Code, cursor);
            var pl = await SendWithRetryAsync(() => Bearer(HttpMethod.Get, GraphBaseUrl + media + "?fields=permalink", token), ct).ConfigureAwait(false);
            var link = pl.Code == 200 ? TopLevelString(pl.Body, "permalink"u8) : null;
            if (link is null || !link.StartsWith("https://", StringComparison.Ordinal)) link = "https://www.instagram.com/";
            return Succeed(media, link, pub.Code, cursor);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Cancel(cursor); }
        catch (Exception ex) { return Fail(ex.Message, 0, cursor); }
        finally { Exit(); }
    }

    // ══════════════════════ 요청 ══════════════════════

    private readonly record struct Reply(int Code, byte[]? Body, string Range, Uri? Location, TimeSpan? RetryAfter);

    private async Task<Reply> PutChunkAsync(Uri session, string token, ReadOnlyMemory<byte> video, ResumableChunkCursor cursor, CancellationToken ct)
    {
        var len = cursor.NextLength;
        using var req = new HttpRequestMessage(HttpMethod.Put, session);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var c = new SliceUploadContent(cursor.Slice(video), this, cursor.Offset);
        c.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        c.Headers.TryAddWithoutValidation("Content-Range", ContentRange(cursor.Offset, len, cursor.Total));
        req.Content = c;
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<Reply> QueryStatusAsync(Uri session, string token, long total, CancellationToken ct)
    {
        Guard.CountStatusQuery();
        using var req = new HttpRequestMessage(HttpMethod.Put, session);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var c = new ReadOnlyMemoryContent(ReadOnlyMemory<byte>.Empty);
        c.Headers.TryAddWithoutValidation("Content-Range", StatusRange(total));
        req.Content = c;
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    private async Task<Reply> RuploadAsync(string container, string token, ReadOnlyMemory<byte> video, ResumableChunkCursor cursor, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, RuploadBaseUrl + container);
        req.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
        req.Headers.TryAddWithoutValidation("offset", cursor.Offset.ToString(CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("file_size", cursor.Total.ToString(CultureInfo.InvariantCulture));
        var c = new SliceUploadContent(video[(int)cursor.Offset..], this, cursor.Offset);
        c.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content = c;
        return await SendAsync(req, ct).ConfigureAwait(false);
    }

    /// <summary>멱등 제어 호출(초기화 · 폴링 · 게시) — 429/5xx 백오프 · 단절 재시도.</summary>
    private async Task<Reply> SendWithRetryAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        int retry = 0, drops = 0;
        while (true)
        {
            Reply r;
            try
            {
                using var req = make();
                r = await SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (UploadNetworkRecoverySafetyGuard.IsConnectionLoss(ex, ct))
            {
                if (!await Guard.BeginRecoveryAsync(drops++, ct).ConfigureAwait(false)) throw;
                continue;
            }
            if (!UploadNetworkRecoverySafetyGuard.IsRetryableStatus(r.Code) || !await Guard.WaitRetryAsync(retry++, r.RetryAfter, ct).ConfigureAwait(false)) return r;
            _waits++;
        }
    }

    private async Task<Reply> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var code = (int)resp.StatusCode;
        var range = resp.Headers.NonValidated.TryGetValues("Range", out var rv) ? rv.ToString() : "";
        var body = code is 200 or 201 ? await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false) : null;
        return new Reply(code, body, range, resp.Headers.Location is { } loc ? (loc.IsAbsoluteUri ? loc : new Uri(req.RequestUri!, loc)) : null, UploadNetworkRecoverySafetyGuard.RetryAfter(resp));
    }

    private static HttpRequestMessage Bearer(HttpMethod m, string url, string token)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static string ContentRange(long offset, int length, long total)
    {
        Span<char> s = stackalloc char[64];
        return UploadNetworkRecoverySafetyGuard.TryWriteContentRange(offset, length, total, s, out var n) ? new string(s[..n]) : "";
    }

    private static string StatusRange(long total)
    {
        Span<char> s = stackalloc char[32];
        return UploadNetworkRecoverySafetyGuard.TryWriteStatusQueryRange(total, s, out var n) ? new string(s[..n]) : "";
    }

    // ══════════════════════ JSON 스키마 검증 ══════════════════════

    /// <summary>최상위 객체의 문자열 속성(중첩 동명 키 무시).</summary>
    public static string? TopLevelString(byte[]? body, ReadOnlySpan<byte> name)
    {
        if (body is null) return null;
        try
        {
            var r = new Utf8JsonReader(body);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return null;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var hit = r.ValueTextEquals(name);
                if (!r.Read()) return null;
                if (hit && r.TokenType == JsonTokenType.String) return r.GetString();
                r.Skip();
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>최상위 정수 속성(문자열 숫자 허용) — 없으면 -1.</summary>
    public static long TopLevelLong(byte[]? body, ReadOnlySpan<byte> name)
    {
        if (body is null) return -1;
        try
        {
            var r = new Utf8JsonReader(body);
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return -1;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                var hit = r.ValueTextEquals(name);
                if (!r.Read()) return -1;
                if (hit && r.TokenType == JsonTokenType.Number && r.TryGetInt64(out var v)) return v;
                if (hit && r.TokenType == JsonTokenType.String && long.TryParse(r.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out v)) return v;
                r.Skip();
            }
        }
        catch (JsonException) { }
        return -1;
    }

    // ══════════════════════ 상태 · 진행률 ══════════════════════

    private bool Enter(UploadPlatform p, long total)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        Interlocked.Increment(ref _sessions);
        _platform = p;
        _resumes = _waits = 0;
        _sent = 0;
        _chunks = new ResumableChunkCursor(total, ChunkSize).Chunks;
        _chunk = _chunks == 0 ? 0 : 1;
        _meter.Start(total, Stopwatch.GetTimestamp());
        return true;
    }

    private void Exit() => Volatile.Write(ref _busy, 0);

    /// <summary>청크 스트림 기록 진행(전송 스레드 · 무할당).</summary>
    internal void Report(long absoluteSent)
    {
        var chunk = _chunks == 0 ? 0 : (int)Math.Min(Math.Max(absoluteSent - 1, 0) / ChunkSize + 1, _chunks);
        if (chunk > _chunk) _chunk = chunk;
        _sent = absoluteSent;
        var p = _meter.Update(_platform, Phase, absoluteSent, _chunk, _chunks, _resumes, _waits, Stopwatch.GetTimestamp(), Stopwatch.Frequency);
        Guard.Emit(p);
    }

    internal void Streamed(int bytes) => Interlocked.Add(ref _bytesSent, bytes);

    private void SetPhase(UploadPhase p)
    {
        Phase = p;
        Report(_sent);
        Raise();
    }

    private UploadResult Succeed(string id, string link, int code, ResumableChunkCursor cursor)
    {
        Interlocked.Increment(ref _completed);
        Phase = UploadPhase.Completed;
        _chunk = _chunks;
        Report(cursor.Total);
        return Finish(new UploadResult(true, _platform, id, link, null, code, _chunks, _resumes, _waits, cursor.Total));
    }

    private UploadResult Fail(string error, int code, ResumableChunkCursor cursor)
    {
        Interlocked.Increment(ref _failures);
        Phase = UploadPhase.Failed;
        Report(cursor.Offset);
        return Finish(new UploadResult(false, _platform, "", "", code > 0 ? error + " · HTTP " + code.ToString(CultureInfo.InvariantCulture) : error, code, _chunks, _resumes, _waits, cursor.Offset));
    }

    private UploadResult Cancel(ResumableChunkCursor cursor)
    {
        Phase = UploadPhase.Canceled;
        Report(cursor.Offset);
        return Finish(new UploadResult(false, _platform, "", "", "취소됨", 0, _chunks, _resumes, _waits, cursor.Offset));
    }

    /// <summary>세션 미진입 거부(진행 중 세션 상태 불변).</summary>
    private static UploadResult Reject(UploadPlatform p, string error) => new(false, p, "", "", error, 0, 0, 0, 0, 0);

    private UploadResult Finish(in UploadResult r)
    {
        LastResult = r;
        Raise();
        return r;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); }
        catch (Exception) { /* 구독자 예외 격리 */ }
    }

    public void Dispose()
    {
        _http.Dispose();
        OnChange = null;
    }

    /// <summary>ReadOnlyMemory 슬라이스 직접 스트리밍(256 KB 단위 기록 → 진행률) — 버퍼 복사 0.</summary>
    private sealed class SliceUploadContent : HttpContent
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly ProductionResumableUploadSessionEngine _owner;
        private readonly long _base;

        public SliceUploadContent(ReadOnlyMemory<byte> data, ProductionResumableUploadSessionEngine owner, long baseOffset)
        {
            _data = data; _owner = owner; _base = baseOffset;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var sent = 0;
            while (sent < _data.Length)
            {
                var n = Math.Min(StreamSlice, _data.Length - sent);
                await stream.WriteAsync(_data.Slice(sent, n), cancellationToken).ConfigureAwait(false);
                sent += n;
                _owner.Streamed(n);
                _owner.Report(_base + sent);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _data.Length;
            return true;
        }
    }
}

/// <summary>
/// [v18.0.5] 오프라인 · 단위 테스트용 플랫폼 계약 Mock — YouTube Resumable(POST 세션 · PUT Content-Range · 308 Range · bytes */N 상태 질의 · 200 id) ·
/// Instagram(컨테이너 · rupload offset/file_size · GET offset · status_code 폴링 · media_publish · permalink). 수신 바이트는 원본 대조용으로 재조립.
/// 장애 주입: <see cref="DropAtData"/>(n 번째 데이터 요청에서 256 KB 만 수신 후 소켓 단절) · <see cref="RateLimitBursts"/>(다음 n 회 429).
/// </summary>
public sealed class ResumableUploadMockHandler : HttpMessageHandler
{
    private readonly object _gate = new();
    private byte[] _yt = Array.Empty<byte>(), _ig = Array.Empty<byte>();
    private long _ytCommitted, _igCommitted, _seq;
    private string? _session, _container;
    private int _polls;
    private bool _finished;

    /// <summary>1-기준 데이터 요청 순번(YouTube 청크 PUT · IG rupload)에서 단절 · 0 = 없음.</summary>
    public int DropAtData { get; set; }
    public int RateLimitBursts { get; set; }
    public int ProcessingPolls { get; set; } = 2;
    public TimeSpan Latency { get; set; }

    public int Inits { get; private set; }
    public int DataRequests { get; private set; }
    public int StatusQueries { get; private set; }
    public int RateLimited { get; private set; }
    public int Drops { get; private set; }
    public int Polls => _polls;
    public int Publishes { get; private set; }
    public string LastAuthorization { get; private set; } = "";
    public string LastContentRange { get; private set; } = "";
    public string LastVideoId { get; private set; } = "";
    public long Committed(UploadPlatform p) => p == UploadPlatform.YouTube ? _ytCommitted : _igCommitted;
    public ReadOnlySpan<byte> Received(UploadPlatform p) => p == UploadPlatform.YouTube ? _yt.AsSpan(0, (int)_ytCommitted) : _ig.AsSpan(0, (int)_igCommitted);

    public void Reset()
    {
        lock (_gate)
        {
            _yt = _ig = Array.Empty<byte>();
            _ytCommitted = _igCommitted = 0;
            _session = _container = null;
            _polls = 0; _finished = false;
            DropAtData = RateLimitBursts = 0;
            Inits = DataRequests = StatusQueries = RateLimited = Drops = Publishes = 0;
            LastAuthorization = LastContentRange = LastVideoId = "";
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var u = request.RequestUri!;
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (Latency > TimeSpan.Zero && body.Length > 0) await Task.Delay(Latency, cancellationToken).ConfigureAwait(false);
        LastAuthorization = request.Headers.Authorization?.ToString() ?? (request.Headers.TryGetValues("Authorization", out var a) ? string.Join(",", a) : "");
        lock (_gate)
        {
            if (u.Host == ProductionSocialUploadPayloadBuilder.YouTubeHost) return YouTube(request, u, body);
            if (u.Host == ProductionSocialUploadPayloadBuilder.InstagramRuploadHost) return Rupload(request, u, body);
            if (u.Host == "graph.facebook.com") return Graph(request, u, body);
        }
        return Json(HttpStatusCode.NotFound, "{\"error\":\"unknown host\"}");
    }

    private HttpResponseMessage YouTube(HttpRequestMessage req, Uri u, byte[] body)
    {
        if (req.Method == HttpMethod.Post)
        {
            Inits++;
            if (!LastAuthorization.StartsWith("Bearer ", StringComparison.Ordinal)) return Json(HttpStatusCode.Unauthorized, "{\"error\":\"auth\"}");
            if (!req.Headers.TryGetValues("X-Upload-Content-Length", out var lv) || !long.TryParse(lv.First(), NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total <= 0)
                return Json(HttpStatusCode.BadRequest, "{\"error\":\"length\"}");
            try
            {
                using var j = JsonDocument.Parse(body);
                if (!j.RootElement.TryGetProperty("snippet", out var sn) || !sn.TryGetProperty("title", out _)) return Json(HttpStatusCode.BadRequest, "{\"error\":\"snippet\"}");
            }
            catch (JsonException) { return Json(HttpStatusCode.BadRequest, "{\"error\":\"json\"}"); }
            _yt = new byte[total];
            _ytCommitted = 0;
            _session = "pfmock" + (++_seq).ToString(CultureInfo.InvariantCulture);
            var r = Json(HttpStatusCode.OK, "{}");
            r.Headers.Location = new Uri(ProductionResumableUploadSessionEngine.YouTubeInitUrl + "&upload_id=" + _session);
            return r;
        }
        if (req.Method != HttpMethod.Put || _session is null || !u.Query.Contains("upload_id=" + _session, StringComparison.Ordinal)) return Json(HttpStatusCode.NotFound, "{\"error\":\"session\"}");
        if (!LastAuthorization.StartsWith("Bearer ", StringComparison.Ordinal)) return Json(HttpStatusCode.Unauthorized, "{\"error\":\"auth\"}");
        var range = req.Content?.Headers.ContentRange;
        LastContentRange = range?.ToString() ?? "";
        if (range is null || range.Length != _yt.Length) return Json(HttpStatusCode.BadRequest, "{\"error\":\"range\"}");
        if (range.From is null)
        {
            StatusQueries++;
            return _ytCommitted == _yt.Length ? YouTubeDone() : Resume(_ytCommitted);
        }
        DataRequests++;
        if (RateLimitBursts > 0) { RateLimitBursts--; RateLimited++; return Json((HttpStatusCode)429, "{\"error\":{\"code\":429,\"message\":\"rateLimitExceeded\"}}"); }
        var from = range.From.Value;
        if (from != _ytCommitted || range.To!.Value - from + 1 != body.Length) return Resume(_ytCommitted);
        if (DropAtData == DataRequests) return Drop(body, from, _yt, ref _ytCommitted);
        body.CopyTo(_yt.AsSpan((int)from));
        _ytCommitted = from + body.Length;
        return _ytCommitted == _yt.Length ? YouTubeDone() : Resume(_ytCommitted);
    }

    private HttpResponseMessage YouTubeDone()
    {
        LastVideoId = "PF" + _seq.ToString("000000000", CultureInfo.InvariantCulture);
        return Json(HttpStatusCode.OK, "{\"kind\":\"youtube#video\",\"etag\":\"mock\",\"id\":\"" + LastVideoId + "\",\"snippet\":{\"channelId\":\"UCpfmock\",\"id\":\"nested\"},\"status\":{\"uploadStatus\":\"uploaded\"}}");
    }

    private static HttpResponseMessage Resume(long committed)
    {
        var r = new HttpResponseMessage((HttpStatusCode)308) { Content = new ByteArrayContent(Array.Empty<byte>()) };
        if (committed > 0) r.Headers.TryAddWithoutValidation("Range", "bytes=0-" + (committed - 1).ToString(CultureInfo.InvariantCulture));
        return r;
    }

    /// <summary>256 KB 까지만 수신 후 연결 리셋(서버 커밋은 유지).</summary>
    private HttpResponseMessage Drop(byte[] body, long from, byte[] dst, ref long committed)
    {
        Drops++;
        var keep = Math.Min(ProductionResumableUploadSessionEngine.ChunkQuantum, body.Length - 1);
        body.AsSpan(0, keep).CopyTo(dst.AsSpan((int)from));
        committed = from + keep;
        throw new HttpRequestException("모의 소켓 단절(connection reset)", new IOException("connection reset by peer"));
    }

    private HttpResponseMessage Rupload(HttpRequestMessage req, Uri u, byte[] body)
    {
        var id = u.AbsolutePath[ProductionSocialUploadPayloadBuilder.InstagramRuploadPath.Length..];
        if (_container is null || id != _container) return Json(HttpStatusCode.NotFound, "{\"error\":\"container\"}");
        if (req.Method == HttpMethod.Get) { StatusQueries++; return Json(HttpStatusCode.OK, "{\"offset\":" + _igCommitted.ToString(CultureInfo.InvariantCulture) + "}"); }
        if (!LastAuthorization.StartsWith("OAuth ", StringComparison.Ordinal)) return Json(HttpStatusCode.Unauthorized, "{\"error\":\"auth\"}");
        if (!long.TryParse(Header(req, "file_size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0 ||
            !long.TryParse(Header(req, "offset"), NumberStyles.None, CultureInfo.InvariantCulture, out var off)) return Json(HttpStatusCode.BadRequest, "{\"error\":\"headers\"}");
        if (_ig.Length != size) { _ig = new byte[size]; _igCommitted = 0; }
        DataRequests++;
        if (RateLimitBursts > 0) { RateLimitBursts--; RateLimited++; return Json((HttpStatusCode)429, "{\"error\":\"rate\"}"); }
        if (off != _igCommitted || off + body.Length != size) return Json(HttpStatusCode.BadRequest, "{\"error\":\"offset\"}");
        if (DropAtData == DataRequests) return Drop(body, off, _ig, ref _igCommitted);
        body.CopyTo(_ig.AsSpan((int)off));
        _igCommitted = size;
        return Json(HttpStatusCode.OK, "{\"success\":true,\"message\":\"Upload successful.\"}");
    }

    private HttpResponseMessage Graph(HttpRequestMessage req, Uri u, byte[] body)
    {
        if (!LastAuthorization.StartsWith("Bearer ", StringComparison.Ordinal)) return Json(HttpStatusCode.Unauthorized, "{\"error\":\"auth\"}");
        var path = u.AbsolutePath;
        if (req.Method == HttpMethod.Post && path.EndsWith("/media", StringComparison.Ordinal))
        {
            Inits++;
            var form = System.Text.Encoding.ASCII.GetString(body);
            if (!form.Contains("media_type=REELS", StringComparison.Ordinal) || !form.Contains("upload_type=resumable", StringComparison.Ordinal)) return Json(HttpStatusCode.BadRequest, "{\"error\":\"form\"}");
            _container = "1790" + (++_seq).ToString("00000000000", CultureInfo.InvariantCulture);
            _ig = Array.Empty<byte>(); _igCommitted = 0; _polls = 0; _finished = false;
            return Json(HttpStatusCode.OK, "{\"id\":\"" + _container + "\",\"uri\":\"" + ProductionResumableUploadSessionEngine.RuploadBaseUrl + _container + "\"}");
        }
        if (req.Method == HttpMethod.Post && path.EndsWith("/media_publish", StringComparison.Ordinal))
        {
            if (!_finished || !u.Query.Contains("creation_id=" + _container, StringComparison.Ordinal)) return Json(HttpStatusCode.BadRequest, "{\"error\":\"not ready\"}");
            Publishes++;
            LastVideoId = "1791" + _seq.ToString("00000000000", CultureInfo.InvariantCulture);
            return Json(HttpStatusCode.OK, "{\"id\":\"" + LastVideoId + "\"}");
        }
        if (req.Method == HttpMethod.Get && u.Query.Contains("fields=status_code", StringComparison.Ordinal) && _container is not null && path.EndsWith(_container, StringComparison.Ordinal))
        {
            _polls++;
            _finished = _igCommitted > 0 && _igCommitted == _ig.Length && _polls > ProcessingPolls;
            return Json(HttpStatusCode.OK, "{\"status_code\":\"" + (_finished ? "FINISHED" : "IN_PROGRESS") + "\",\"id\":\"" + _container + "\"}");
        }
        if (req.Method == HttpMethod.Get && u.Query.Contains("fields=permalink", StringComparison.Ordinal))
            return Json(HttpStatusCode.OK, "{\"permalink\":\"https://www.instagram.com/reel/PFmock" + _seq.ToString(CultureInfo.InvariantCulture) + "/\",\"id\":\"" + LastVideoId + "\"}");
        return Json(HttpStatusCode.NotFound, "{\"error\":\"route\"}");
    }

    private static string Header(HttpRequestMessage req, string name) => req.Headers.TryGetValues(name, out var v) ? v.First() : "";

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}
