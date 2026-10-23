using System.Buffers.Text;
using System.Security.Cryptography;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.LivePublish;
using PromptFlowStudio.Services.PostPublishSplitTest;

namespace PromptFlowStudio.Services.PlatformVisibility;

public enum VideoVisibility : byte { Unknown, Public, Unlisted, Private, Hidden, Deleted }

/// <summary>콘텐츠(변형) ↔ 플랫폼 영상 ID 결선 1 슬롯 — 생성 시 1 회 할당 · 재바인딩은 슬롯 재사용.</summary>
public sealed class PlatformVideoBinding
{
    public const int IdCap = 64;
    internal readonly byte[] Id = new byte[IdCap];
    internal int IdLen;

    public bool InUse { get; internal set; }
    public LiveProvider Provider { get; internal set; }
    public int ContentId { get; internal set; }
    public SplitVariant Variant { get; internal set; }
    public int Channel { get; internal set; }
    public string VideoId { get; internal set; } = "";
    public VideoVisibility Visibility { get; internal set; }
    public int LastStatus { get; internal set; }
    public long Ops { get; internal set; }
    public ReadOnlySpan<byte> IdSpan => Id.AsSpan(0, IdLen);
}

/// <summary>재사용 요청 슬롯 — URL · 헤더 · JSON 본문 · 응답 버퍼(생성 시 1 회 할당).</summary>
public sealed class GuardedRequest
{
    public const int UrlCap = 2048, HdrCap = 2048, BodyCap = 512;
    public readonly byte[] Url = new byte[UrlCap], Hdr = new byte[HdrCap], Body = new byte[BodyCap];
    public readonly GraphResponseBuffer Resp = new();
    public int UrlLen, BodyLen;
    public GraphHttpMethod Method;
    public bool Json;
}

/// <summary>UTF-8 JSON 스팬 탐색(무할당) — 키 다음 문자열/숫자 값.</summary>
public static class PlatformJson
{
    /// <summary><c>"key":"value"</c> 의 value(따옴표 제외). 없으면 false.</summary>
    public static bool TryString(ReadOnlySpan<byte> s, ReadOnlySpan<byte> quotedKey, out ReadOnlySpan<byte> value)
    {
        value = default;
        var k = s.IndexOf(quotedKey);
        if (k < 0) return false;
        var r = s[(k + quotedKey.Length)..].TrimStart(" :"u8);
        if (r.IsEmpty || r[0] != (byte)'"') return false;
        r = r[1..];
        var e = r.IndexOf((byte)'"');
        if (e < 0) return false;
        value = r[..e];
        return true;
    }

    /// <summary><c>"key":123</c> 또는 <c>"key":"123"</c>(YouTube 통계는 문자열 숫자) — 없으면 -1.</summary>
    public static long Long(ReadOnlySpan<byte> s, ReadOnlySpan<byte> quotedKey)
    {
        var k = s.IndexOf(quotedKey);
        if (k < 0) return -1;
        var r = s[(k + quotedKey.Length)..].TrimStart(" :\""u8);
        return Utf8Parser.TryParse(r, out long v, out _) ? v : -1;
    }

    /// <summary>실수 값(없으면 NaN).</summary>
    public static double Double(ReadOnlySpan<byte> s, ReadOnlySpan<byte> quotedKey)
    {
        var k = s.IndexOf(quotedKey);
        if (k < 0) return double.NaN;
        var r = s[(k + quotedKey.Length)..].TrimStart(" :\"["u8);
        return Utf8Parser.TryParse(r, out double v, out _) ? v : double.NaN;
    }

    public static ReadOnlySpan<byte> PrivacyToken(VideoVisibility v) => v switch
    {
        VideoVisibility.Public => "public"u8,
        VideoVisibility.Unlisted => "unlisted"u8,
        _ => "private"u8,
    };

    public static VideoVisibility ParsePrivacy(ReadOnlySpan<byte> v) =>
        v.SequenceEqual("public"u8) ? VideoVisibility.Public : v.SequenceEqual("unlisted"u8) ? VideoVisibility.Unlisted : v.SequenceEqual("private"u8) ? VideoVisibility.Private : VideoVisibility.Unknown;

    /// <summary>플랫폼 영상 ID 스키마(영숫자 · '-' · '_' · 1~64) — URL 주입 차단.</summary>
    public static bool IsValidId(ReadOnlySpan<char> id)
    {
        if (id.IsEmpty || id.Length > PlatformVideoBinding.IdCap) return false;
        foreach (var c in id) if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        return true;
    }
}

/// <summary>
/// [PromptFlow Studio v17.94.0] 🌐 프로덕션 플랫폼 비지빌리티 게이트웨이 — v17.93 <see cref="IVideoVisibilityGateway"/> 실체 구현.
///   ▸ YouTube Data API v3 : <c>PUT videos?part=status</c>(privacyStatus public/unlisted/private) · <c>DELETE videos?id=</c> · <c>GET videos?part=status</c>.
///   ▸ Instagram Graph API v21.0 : <c>GET /{media-id}</c> 상태 조회 · <c>POST /{media-id}?comment_enabled=false</c> 숨김(공식 비공개 전환 API 부재 → 상호작용 잠금) · <c>DELETE /{media-id}</c>.
///   ▸ 모든 호출은 <see cref="TokenLifecycleRateLimitBackoffGuard"/> 경유(토큰 5 분 전 갱신 · 429 지수 백오프 · 일일 쿼터 90 % 안전 정지) — 401 은 토큰 폐기 후 1 회 재발급.
///   ▸ URL · 헤더 · 본문은 재사용 버퍼에 UTF-8 직접 조립, 응답은 스팬 파싱 — 시뮬레이터 경로 무할당. Bearer 헤더는 전송 직후 제로화.
///   ▸ <see cref="LiveEndpoints"/> 비활성(기본 · 환경변수 <c>PROMPTFLOW_PVG_LIVE=1</c> 미설정) 시 <see cref="PlatformApiContractSimulator"/> 로 100 % 결정적 폴백.
/// </summary>
public sealed class ProductionPlatformVisibilityGatewayEngine : IVideoVisibilityGateway, IDisposable
{
    public const int Capacity = 32, MaxAttempts = 4;
    public const string EnvLive = "PROMPTFLOW_PVG_LIVE";
    public static ReadOnlySpan<byte> YouTubeVideosUrl => "https://www.googleapis.com/youtube/v3/videos"u8;
    public static ReadOnlySpan<byte> YouTubeAnalyticsUrl => "https://youtubeanalytics.googleapis.com/v2/reports"u8;
    public static ReadOnlySpan<byte> GraphBaseUrl => "https://graph.facebook.com/v21.0/"u8;

    private readonly PlatformVideoBinding[] _slots = new PlatformVideoBinding[Capacity];
    private readonly GuardedRequest _req = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IGraphTransport? _injectedLive;
    private HttpGraphTransport? _http;

    public ProductionPlatformVisibilityGatewayEngine(TokenLifecycleRateLimitBackoffGuard guard, Func<double> clock, bool? liveEndpoints = null, IGraphTransport? liveTransport = null, Func<string, string?>? env = null)
    {
        Guard = guard ?? throw new ArgumentNullException(nameof(guard));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _injectedLive = liveTransport;
        LiveEndpoints = liveEndpoints ?? (env ?? Environment.GetEnvironmentVariable)(EnvLive) == "1";
        Simulator = new PlatformApiContractSimulator(clock);
        for (var i = 0; i < Capacity; i++) _slots[i] = new PlatformVideoBinding();
    }

    public TokenLifecycleRateLimitBackoffGuard Guard { get; }
    public PlatformApiContractSimulator Simulator { get; }
    public Func<double> Clock { get; }
    public bool LiveEndpoints { get; set; }
    public IGraphTransport Transport => LiveEndpoints ? _injectedLive ?? (_http ??= new HttpGraphTransport()) : Simulator;
    public string ModeName => LiveEndpoints ? "LIVE" : "시뮬레이션";
    public long Calls { get; private set; }
    public long Privates { get; private set; }
    public long Publics { get; private set; }
    public long Deletes { get; private set; }
    public long Hides { get; private set; }
    public long Retries { get; private set; }
    public long Backoffs { get; private set; }
    public long DuplicateSkips { get; private set; }
    public long SchemaRejections { get; private set; }
    public long Failures { get; private set; }
    public int LastStatus { get; private set; }
    public int BindingCount { get { var n = 0; foreach (var s in _slots) if (s.InUse) n++; return n; } }

    public PlatformVideoBinding Binding(int slot) => _slots[slot];

    public static string VisibilityName(VideoVisibility v) => v switch
    {
        VideoVisibility.Public => "공개",
        VideoVisibility.Unlisted => "일부 공개",
        VideoVisibility.Private => "비공개",
        VideoVisibility.Hidden => "숨김",
        VideoVisibility.Deleted => "삭제됨",
        _ => "미확인",
    };

    public static string ProviderName(LiveProvider p) => p == LiveProvider.YouTube ? "YouTube" : "Instagram";

    // ══════════════════════ 결선(비핫패스) ══════════════════════

    /// <summary>영상 ID 결선 — 같은 (콘텐츠 · 변형 · 플랫폼) 은 기존 슬롯 갱신(중복 등록 차단). 반환 = 슬롯(-1 = ID 스키마 위반 · 용량 초과).</summary>
    public int Bind(LiveProvider p, string videoId, int contentId, SplitVariant variant, int channel, VideoVisibility initial = VideoVisibility.Public)
    {
        if (!PlatformJson.IsValidId(videoId)) { SchemaRejections++; return -1; }
        var slot = Find(contentId, variant, p);
        if (slot < 0) for (var i = 0; i < Capacity && slot < 0; i++) if (!_slots[i].InUse) slot = i;
        if (slot < 0) return -1;
        var b = _slots[slot];
        b.InUse = true; b.Provider = p; b.ContentId = contentId; b.Variant = variant; b.Channel = channel;
        b.VideoId = videoId; b.IdLen = System.Text.Encoding.ASCII.GetBytes(videoId, b.Id);
        b.Visibility = initial; b.LastStatus = 0; b.Ops = 0;
        return slot;
    }

    public void Unbind(int slot) { if ((uint)slot < Capacity) _slots[slot].InUse = false; }
    public void Clear() { foreach (var s in _slots) s.InUse = false; }

    private int Find(int contentId, SplitVariant v, LiveProvider p)
    {
        for (var i = 0; i < Capacity; i++)
        {
            var s = _slots[i];
            if (s.InUse && s.ContentId == contentId && s.Variant == v && s.Provider == p) return i;
        }
        return -1;
    }

    /// <summary>v17.93 회수기 → 슬롯 탐색(YouTube 우선).</summary>
    private int FindAny(int contentId, SplitVariant v)
    {
        var s = Find(contentId, v, LiveProvider.YouTube);
        return s >= 0 ? s : Find(contentId, v, LiveProvider.Instagram);
    }

    // ══════════════════════ IVideoVisibilityGateway(동기 계약) ══════════════════════

    /// <summary>v17.93 패자 회수기 동기 계약 — 비공개/삭제 실행 · HTTP 상태 반환(미결선 404). 시뮬레이터 경로는 동기 완료.</summary>
    public int Apply(int contentId, int channel, SplitVariant variant, PruneAction action)
    {
        var slot = FindAny(contentId, variant);
        if (slot < 0) return 404;
        var vt = ApplyAsync(slot, action == PruneAction.Delete ? VideoVisibility.Deleted : VideoVisibility.Private, CancellationToken.None);
        return vt.IsCompletedSuccessfully ? vt.Result : vt.AsTask().GetAwaiter().GetResult();
    }

    // ══════════════════════ 가시성 전환 · 상태 조회(무할당 핫패스) ══════════════════════

    /// <summary>가시성 전환 — 이미 목표 상태면 호출 생략(200), 삭제된 영상은 410. 반환 = 최종 HTTP 상태.</summary>
    public async ValueTask<int> ApplyAsync(int slot, VideoVisibility target, CancellationToken ct = default)
    {
        if ((uint)slot >= Capacity || !_slots[slot].InUse) return 404;
        var b = _slots[slot];
        if (b.Visibility == VideoVisibility.Deleted) return 410;
        if (b.Visibility == target || (target == VideoVisibility.Private && b.Visibility == VideoVisibility.Hidden)) { DuplicateSkips++; return 200; }
        if (target == VideoVisibility.Unlisted && b.Provider == LiveProvider.Instagram) return 422;   // IG 일부 공개 개념 없음
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BuildMutation(b, target);
            var cost = b.Provider == LiveProvider.YouTube ? (target == VideoVisibility.Deleted ? TokenLifecycleRateLimitBackoffGuard.CostYtDelete : TokenLifecycleRateLimitBackoffGuard.CostYtUpdate) : TokenLifecycleRateLimitBackoffGuard.CostIg;
            var status = await SendGuardedAsync(_req, b.Provider, cost, ct).ConfigureAwait(false);
            if (status is >= 200 and < 300) status = Confirm(b, target, status);
            b.LastStatus = LastStatus = status; b.Ops++;
            if (status is < 200 or >= 300) Failures++;
            return status;
        }
        finally { _gate.Release(); }
    }

    /// <summary>현재 공개 상태 조회 — YouTube 빈 items · Graph 코드 100 은 삭제로 확정.</summary>
    public async ValueTask<int> RefreshStatusAsync(int slot, CancellationToken ct = default)
    {
        if ((uint)slot >= Capacity || !_slots[slot].InUse) return 404;
        var b = _slots[slot];
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BuildStatus(b);
            var status = await SendGuardedAsync(_req, b.Provider, b.Provider == LiveProvider.YouTube ? TokenLifecycleRateLimitBackoffGuard.CostYtList : TokenLifecycleRateLimitBackoffGuard.CostIg, ct).ConfigureAwait(false);
            status = ParseStatus(b, status);
            b.LastStatus = LastStatus = status; b.Ops++;
            return status;
        }
        finally { _gate.Release(); }
    }

    private void BuildStatus(PlatformVideoBinding b)
    {
        var r = _req; var u = r.Url.AsSpan(); var n = 0;
        r.Method = GraphHttpMethod.Get; r.BodyLen = 0; r.Json = false;
        if (b.Provider == LiveProvider.YouTube) { GraphBytes.Put(u, ref n, YouTubeVideosUrl); GraphBytes.Put(u, ref n, "?part=status&id="u8); GraphBytes.Put(u, ref n, b.IdSpan); }
        else { GraphBytes.Put(u, ref n, GraphBaseUrl); GraphBytes.Put(u, ref n, b.IdSpan); GraphBytes.Put(u, ref n, "?fields=id,media_type,permalink"u8); }
        r.UrlLen = n;
    }

    private int ParseStatus(PlatformVideoBinding b, int status)
    {
        var body = _req.Resp.BodySpan;
        if (b.Provider == LiveProvider.YouTube)
        {
            if (status != 200) return status;
            if (PlatformJson.TryString(body, "\"privacyStatus\""u8, out var pv)) b.Visibility = PlatformJson.ParsePrivacy(pv);
            else if (body.IndexOf("\"items\""u8) >= 0) b.Visibility = VideoVisibility.Deleted;
            else { SchemaRejections++; return 502; }
        }
        else if (status == 200)
        {
            if (body.IndexOf("\"id\""u8) < 0) { SchemaRejections++; return 502; }
            if (b.Visibility is VideoVisibility.Unknown or VideoVisibility.Deleted) b.Visibility = VideoVisibility.Public;
        }
        else if (TokenLifecycleRateLimitBackoffGuard.GraphCode(body) == 100) b.Visibility = VideoVisibility.Deleted;
        return status;
    }

    private void BuildMutation(PlatformVideoBinding b, VideoVisibility target)
    {
        var r = _req; var u = r.Url.AsSpan(); var n = 0; var k = 0;
        r.BodyLen = 0; r.Json = false;
        if (b.Provider == LiveProvider.YouTube)
        {
            GraphBytes.Put(u, ref n, YouTubeVideosUrl);
            if (target == VideoVisibility.Deleted) { r.Method = GraphHttpMethod.Delete; GraphBytes.Put(u, ref n, "?id="u8); GraphBytes.Put(u, ref n, b.IdSpan); }
            else
            {
                r.Method = GraphHttpMethod.Put; r.Json = true;
                GraphBytes.Put(u, ref n, "?part=status"u8);
                var d = r.Body.AsSpan();
                GraphBytes.Put(d, ref k, "{\"id\":\""u8); GraphBytes.Put(d, ref k, b.IdSpan);
                GraphBytes.Put(d, ref k, "\",\"status\":{\"privacyStatus\":\""u8); GraphBytes.Put(d, ref k, PlatformJson.PrivacyToken(target));
                GraphBytes.Put(d, ref k, "\"}}"u8);
                r.BodyLen = k;
            }
        }
        else
        {
            GraphBytes.Put(u, ref n, GraphBaseUrl); GraphBytes.Put(u, ref n, b.IdSpan);
            if (target == VideoVisibility.Deleted) r.Method = GraphHttpMethod.Delete;
            else { r.Method = GraphHttpMethod.Post; GraphBytes.Put(u, ref n, target == VideoVisibility.Public ? "?comment_enabled=true"u8 : "?comment_enabled=false"u8); }
        }
        r.UrlLen = n;
    }

    /// <summary>2xx 응답 스키마 즉시 검증 → 로컬 상태 확정(불일치 502).</summary>
    private int Confirm(PlatformVideoBinding b, VideoVisibility target, int status)
    {
        var body = _req.Resp.BodySpan;
        if (target == VideoVisibility.Deleted)
        {
            if (b.Provider == LiveProvider.Instagram && status != 204 && body.IndexOf("\"success\":true"u8) < 0) { SchemaRejections++; return 502; }
            b.Visibility = VideoVisibility.Deleted; Deletes++;
            return status;
        }
        if (b.Provider == LiveProvider.YouTube)
        {
            if (!PlatformJson.TryString(body, "\"privacyStatus\""u8, out var pv) || PlatformJson.ParsePrivacy(pv) != target) { SchemaRejections++; return 502; }
        }
        else if (body.IndexOf("\"success\":true"u8) < 0) { SchemaRejections++; return 502; }
        b.Visibility = b.Provider == LiveProvider.Instagram && target == VideoVisibility.Private ? VideoVisibility.Hidden : target;
        if (b.Visibility == VideoVisibility.Hidden) Hides++;
        else if (target == VideoVisibility.Private) Privates++;
        else if (target == VideoVisibility.Public) Publics++;
        return status;
    }

    // ══════════════════════ 가드 경유 송신 루프(폴러 공유) ══════════════════════

    /// <summary>
    /// 토큰 보장 → 쿼터 허가 → Bearer 헤더 조립 → 송신 → 헤더 제로화. 429/레이트 본문 · 5xx · 네트워크 실패는 지수 백오프 후 재시도(최대 4 회),
    /// 401 은 토큰 폐기 후 재발급 재시도, quotaExceeded 는 당일 소진 처리 후 즉시 중단. 로컬 차단 상태: 401(토큰 없음) · 403(안전 정지).
    /// </summary>
    internal async ValueTask<int> SendGuardedAsync(GuardedRequest r, LiveProvider p, int cost, CancellationToken ct)
    {
        var status = 0;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1) Retries++;
            var now = Clock();
            if (!Guard.EnsureToken(p, now)) return 401;
            var d = Guard.TryAcquire(p, cost, now);
            if (d == QuotaDecision.SafetyThrottled) return 403;
            if (d == QuotaDecision.NoToken) return 401;
            if (d == QuotaDecision.BackingOff)
            {
                await Guard.Delay(Math.Max(0.001, Guard.BlockedUntil(p) - now), ct).ConfigureAwait(false);
                d = Guard.TryAcquire(p, cost, Clock());
                if (d != QuotaDecision.Allowed) return d == QuotaDecision.SafetyThrottled ? 403 : d == QuotaDecision.NoToken ? 401 : 429;
            }
            var hn = WriteHeaders(r, p);
            Calls++;
            r.Resp.Reset();
            try
            {
                var vt = Transport.SendAsync(r.Method, r.Url.AsMemory(0, r.UrlLen), r.Hdr.AsMemory(0, hn), r.Body.AsMemory(0, r.BodyLen), r.Resp, ct);
                status = vt.IsCompletedSuccessfully ? vt.Result : await vt.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
            {
                status = 0;
                PromptFlowStudio.Program.Log("PlatformVisibility.Send", ex);
            }
            finally { Wipe(r.Hdr, hn); }

            switch (Classify(r, p, status))
            {
                case Outcome.Done: return status;
                case Outcome.Reauth: continue;
            }
            if (attempt == MaxAttempts) break;
            Backoffs++;
            var wait = Guard.OnRateLimited(p, Clock(), r.Resp.RetryAfterSec);
            await Guard.Delay(wait, ct).ConfigureAwait(false);
        }
        return status;
    }

    private enum Outcome : byte { Done, Reauth, Backoff }

    private static void Wipe(byte[] a, int n) => CryptographicOperations.ZeroMemory(a.AsSpan(0, n));

    private int WriteHeaders(GuardedRequest r, LiveProvider p)
    {
        var h = r.Hdr.AsSpan(); var hn = Guard.WriteBearer(p, h);
        if (r.Json) GraphBytes.Put(h, ref hn, "Content-Type: application/json\r\n"u8);
        GraphBytes.Put(h, ref hn, "Accept: application/json\r\n"u8);
        return hn;
    }

    /// <summary>응답 분류 — 2xx · quotaExceeded · 일반 4xx = 종료, 401 = 재발급, 429/레이트 본문 · 5xx · 네트워크 실패 = 백오프.</summary>
    private Outcome Classify(GuardedRequest r, LiveProvider p, int status)
    {
        var body = r.Resp.BodySpan;
        if (status is >= 200 and < 300) { Guard.OnSuccess(p); return Outcome.Done; }
        if (status == 401) { Guard.Invalidate(p); return Outcome.Reauth; }
        if (TokenLifecycleRateLimitBackoffGuard.IsQuotaBody(body)) { Guard.OnQuotaExhausted(p, Clock()); return Outcome.Done; }
        var rate = status == 429 || ((status is 400 or 403) && TokenLifecycleRateLimitBackoffGuard.IsRateLimitBody(body));
        return !rate && status is > 0 and < 500 ? Outcome.Done : Outcome.Backoff;   // 그 외 4xx 는 재시도 무의미
    }

    public void Dispose()
    {
        _http?.Dispose();
        _gate.Dispose();
    }
}
