using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Storyboard;

namespace PromptFlowStudio.Services.Wan;

/// <summary>v16.17.0 직접 게시 대상.</summary>
public enum SocialPublishTarget { YouTubeShorts, TikTok, InstagramReels }

/// <summary>v16.17.0 게시 작업 상태.</summary>
public enum SocialPublishState { Queued, Scheduled, Inspecting, QaBlocked, Uploading, Processing, Published, Failed, Canceled }

/// <summary>
/// v16.17.0 게시 요청. 기본은 비공개(YouTube private · TikTok SELF_ONLY) — 공개는 <paramref name="PublicVisibility"/> 명시 동의.
/// Instagram Graph API 는 비공개 게시가 없으므로 PublicVisibility=true 일 때만 게시한다.
/// </summary>
public sealed record SocialPublishRequest(
    SocialPublishTarget Target, string VideoPath, string Title, string Description, IReadOnlyList<string> Tags,
    DateTime? ScheduleAtUtc = null, bool PublicVisibility = false, bool SkipQa = false, QaSubtitleProbe? Subtitles = null);

/// <summary>v16.17.0 게시 결과.</summary>
public sealed record SocialPublishResult(
    SocialPublishTarget Target, bool Ok, SocialPublishState State, string? PostId, string? Url, string Message, QaInspectionReport? Qa = null);

/// <summary>v16.17.0 게시 작업(허브 모니터링용 · 상태는 브릿지만 갱신).</summary>
public sealed class SocialPublishJob
{
    public Guid Id { get; } = Guid.NewGuid();
    public required SocialPublishRequest Request { get; init; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
    public SocialPublishState State { get; internal set; }
    public double Progress { get; internal set; }
    public SocialPublishResult? Result { get; internal set; }
}

/// <summary>v16.17.0 TikTok 청크 계획(FILE_UPLOAD 규격: 64MB 이하 단일 청크 · 초과 시 고정 청크 + 마지막 청크가 나머지 흡수).</summary>
public readonly record struct TikTokChunkPlan(long VideoSize, long ChunkSize, int TotalChunks)
{
    public (long Start, long End) RangeOf(int index)
    {
        var start = index * ChunkSize;
        var end = index == TotalChunks - 1 ? VideoSize - 1 : start + ChunkSize - 1;
        return (start, end);
    }
}

/// <summary>v16.17.0 OAuth 액세스 토큰(만료 시각 포함).</summary>
public sealed record OAuthToken(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken)
{
    /// <summary>만료 5분 전부터 갱신 대상.</summary>
    public bool IsFresh(DateTimeOffset now) => AccessToken.Length > 0 && now < ExpiresAt - TimeSpan.FromMinutes(5);
}

/// <summary>
/// [PromptFlowStudio] v16.17.0 🚀 소셜 직접 게시 브릿지 — YouTube Shorts · TikTok · Instagram Reels 원클릭/예약 업로드(순수 BCL · HttpClient + System.Text.Json).
///   ▸ YouTube: 기존 <see cref="IYouTubePublishService"/>(Data API v3 resumable) 위임 · 제목 #Shorts 보강 · 예약은 네이티브 publishAt.
///   ▸ TikTok: Content Posting API — OAuth refresh(<c>/v2/oauth/token/</c>) → <c>/v2/post/publish/video/init/</c>(FILE_UPLOAD) → 청크 PUT(Content-Range) → status/fetch 폴링.
///   ▸ Instagram: Graph API — fb_exchange_token 갱신 → REELS 컨테이너(upload_type=resumable) → rupload 바이트 POST → status_code 폴링 → media_publish.
///   ▸ 게시 전 <see cref="AutomatedVideoQaInspector"/> 품질 게이트(거부 판정이면 업로드 차단 · SkipQa 로 우회 가능).
///   ▸ 자격: 환경변수 우선(<c>PFS_TIKTOK_*</c> · <c>PFS_IG_*</c>) → appsettings(TikTok:* · Instagram:*) — 하드코딩 0 · 회전된 토큰은 메모리 캐시만.
///   ▸ 429/503/5xx: 지수 백오프(<see cref="QueueResourceGovernor.BackoffSeconds"/> · Retry-After 우선) 3회 · 메타 요청 30초 타임아웃 · 업로드는 ct 취소.
///   ▸ TikTok/Instagram 예약은 API 미지원 → 로컬 스케줄러(30초 틱 · 앱 실행 중에만 발행). 실패는 결과 record 로 반환(예외 0) · 일괄 게시는 [부분 구제].
/// </summary>
public sealed class DirectSocialPublisherBridge : IDisposable
{
    public const long TikTokSingleChunkMax = 64L * 1024 * 1024;
    public const long TikTokChunkSize = 10L * 1024 * 1024;
    public const string TikTokApi = "https://open.tiktokapis.com/v2/";
    public const string GraphApi = "https://graph.facebook.com/v21.0/";
    public const string RuploadApi = "https://rupload.facebook.com/ig-api-upload/v21.0/";
    private const int MaxJobs = 100;
    private const int MaxAttempts = 3;
    private const int BufferSize = 262144;
    private static readonly TimeSpan MetaTimeout = TimeSpan.FromSeconds(30);

    private readonly IConfiguration _cfg;
    private readonly IYouTubePublishService _youtube;
    private readonly AutomatedVideoQaInspector _qa;
    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly List<SocialPublishJob> _jobs = new(MaxJobs + 1);
    private readonly System.Threading.Timer _scheduler;
    private readonly CancellationTokenSource _life = new();
    private OAuthToken? _tiktok;      // 참조 교체(Volatile) — 토큰 3필드 찢어진 읽기 방지
    private OAuthToken? _instagram;
    private int _schedTick;
    private int _disposed;

    public DirectSocialPublisherBridge(IConfiguration cfg, IYouTubePublishService youtube, AutomatedVideoQaInspector qa, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _youtube = youtube;
        _qa = qa;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 대용량 업로드 — 메타 요청은 요청별 CTS 로 제한
        _scheduler = new System.Threading.Timer(OnSchedulerTick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>폴링 간격(테스트에서 0 으로 단축).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxPolls { get; set; } = 60;

    public event Action? OnChange;

    public IReadOnlyList<SocialPublishJob> Jobs { get { lock (_gate) return _jobs.ToArray(); } }

    // ── 자격(환경변수 우선 → 구성) ──
    private string Secret(string env, string key)
    {
        var v = Environment.GetEnvironmentVariable(env);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return (_cfg[key] ?? "").Trim();
    }

    private string TikTokClientKey => Secret("PFS_TIKTOK_CLIENT_KEY", "TikTok:ClientKey");
    private string TikTokClientSecret => Secret("PFS_TIKTOK_CLIENT_SECRET", "TikTok:ClientSecret");
    private string TikTokRefreshToken => Secret("PFS_TIKTOK_REFRESH_TOKEN", "TikTok:RefreshToken");
    private string IgUserId => Secret("PFS_IG_USER_ID", "Instagram:UserId");
    private string IgAccessToken => Secret("PFS_IG_ACCESS_TOKEN", "Instagram:AccessToken");
    private string IgAppId => Secret("PFS_IG_APP_ID", "Instagram:AppId");
    private string IgAppSecret => Secret("PFS_IG_APP_SECRET", "Instagram:AppSecret");

    /// <summary>대상별 자격 구성 여부.</summary>
    public bool IsEnabled(SocialPublishTarget t) => t switch
    {
        SocialPublishTarget.YouTubeShorts => _youtube.Enabled,
        SocialPublishTarget.TikTok => TikTokClientKey.Length > 0 && TikTokClientSecret.Length > 0 && (TikTokRefreshToken.Length > 0 || Volatile.Read(ref _tiktok) is not null),
        SocialPublishTarget.InstagramReels => IgUserId.Length > 0 && IgAccessToken.Length > 0,
        _ => false,
    };

    // ── 순수 페이로드 빌더(테스트 대상) ──

    /// <summary>TikTok FILE_UPLOAD 청크 계획.</summary>
    public static TikTokChunkPlan PlanTikTokChunks(long size)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (size <= TikTokSingleChunkMax) return new(size, size, 1);
        var count = (int)(size / TikTokChunkSize);   // 나머지는 마지막 청크가 흡수(< 2×청크)
        return new(size, TikTokChunkSize, count);
    }

    /// <summary>캡션 = 제목 + 해시태그(# 정규화 · 중복 제거) · 상한 절단.</summary>
    public static string BuildCaption(string title, IReadOnlyList<string>? tags, int maxLength)
    {
        var sb = new StringBuilder(256);
        sb.Append((title ?? "").Trim());
        if (tags is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in tags)
            {
                var t = (raw ?? "").Trim().TrimStart('#');
                if (t.Length == 0 || t.Contains(' ') || !seen.Add(t)) continue;
                if (sb.Length + t.Length + 2 > maxLength) break;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append('#').Append(t);
            }
        }
        return sb.Length <= maxLength ? sb.ToString() : sb.ToString(0, maxLength);
    }

    /// <summary>YouTube Shorts 제목 — #Shorts 미포함 시 보강(100자 상한 내).</summary>
    public static string ShortsTitle(string title)
    {
        var t = (title ?? "").Trim();
        if (t.Contains("#shorts", StringComparison.OrdinalIgnoreCase)) return t.Length <= 100 ? t : t[..100];
        const string suffix = " #Shorts";
        return (t.Length + suffix.Length <= 100 ? t : t[..(100 - suffix.Length)]) + suffix;
    }

    /// <summary>TikTok video/init 페이로드(JSON).</summary>
    public static string BuildTikTokInitPayload(SocialPublishRequest req, TikTokChunkPlan plan)
    {
        var post = new JsonObject
        {
            ["title"] = BuildCaption(req.Title, req.Tags, 2200),
            ["privacy_level"] = req.PublicVisibility ? "PUBLIC_TO_EVERYONE" : "SELF_ONLY",
            ["disable_duet"] = false,
            ["disable_comment"] = false,
            ["disable_stitch"] = false,
        };
        var source = new JsonObject
        {
            ["source"] = "FILE_UPLOAD",
            ["video_size"] = plan.VideoSize,
            ["chunk_size"] = plan.ChunkSize,
            ["total_chunk_count"] = plan.TotalChunks,
        };
        return new JsonObject { ["post_info"] = post, ["source_info"] = source }.ToJsonString();
    }

    /// <summary>Instagram REELS 컨테이너 생성 폼 필드.</summary>
    public static Dictionary<string, string> BuildInstagramContainerForm(SocialPublishRequest req) => new(4)
    {
        ["media_type"] = "REELS",
        ["upload_type"] = "resumable",
        ["caption"] = BuildCaption(string.IsNullOrWhiteSpace(req.Description) ? req.Title : req.Title + "\n\n" + req.Description.Trim(), req.Tags, 2200),
        ["share_to_feed"] = "true",
    };

    /// <summary>OAuth 토큰 응답 스키마 즉시 검증(access_token 문자열 필수 · expires_in 없으면 1시간).</summary>
    public static bool TryParseToken(string json, DateTimeOffset now, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out OAuthToken? token)
    {
        token = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            if (!root.TryGetProperty("access_token", out var at) || at.ValueKind != JsonValueKind.String) return false;
            var access = at.GetString();
            if (string.IsNullOrEmpty(access)) return false;
            var secs = root.TryGetProperty("expires_in", out var ex) && ex.ValueKind == JsonValueKind.Number && ex.TryGetInt64(out var e) && e > 0 ? e : 3600;
            var refresh = root.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String ? rt.GetString() : null;
            token = new(access, now.AddSeconds(secs), refresh);
            return true;
        }
        catch (JsonException) { return false; }
    }

    // ── 오케스트레이션 ──

    /// <summary>1건 게시(예외 0). 미래 예약(TikTok/Instagram)은 로컬 스케줄러에 등록 후 Scheduled 반환.</summary>
    public Task<SocialPublishResult> PublishAsync(SocialPublishRequest req, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var job = Register(req);
        if (req.ScheduleAtUtc is { } at && req.Target != SocialPublishTarget.YouTubeShorts && at.ToUniversalTime() > DateTime.UtcNow.AddMinutes(1))
        {
            var r = new SocialPublishResult(req.Target, true, SocialPublishState.Scheduled, null, null,
                "로컬 예약 등록 — " + at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC(앱 실행 중 발행)");
            Update(job, SocialPublishState.Scheduled, 0, r);
            return Task.FromResult(r);
        }
        return RunJobAsync(job, progress, ct);
    }

    /// <summary>다중 대상 순차 게시 — 대상 실패는 건너뛰고 계속([부분 구제]).</summary>
    public async Task<IReadOnlyList<SocialPublishResult>> PublishAllAsync(IReadOnlyList<SocialPublishRequest> requests, CancellationToken ct = default)
    {
        var results = new List<SocialPublishResult>(requests.Count);
        foreach (var req in requests)
        {
            if (ct.IsCancellationRequested) { results.Add(new(req.Target, false, SocialPublishState.Canceled, null, null, "취소됨")); continue; }
            results.Add(await PublishAsync(req, null, ct).ConfigureAwait(false));
        }
        return results;
    }

    /// <summary>실패/거부 작업 재시도(QA 우회 없이 동일 요청 재실행).</summary>
    public Task<SocialPublishResult>? Retry(Guid jobId, CancellationToken ct = default)
    {
        SocialPublishJob? job = null;
        lock (_gate) foreach (var j in _jobs) if (j.Id == jobId) { job = j; break; }
        if (job is null || job.State is not (SocialPublishState.Failed or SocialPublishState.QaBlocked or SocialPublishState.Canceled)) return null;
        return RunJobAsync(job, null, ct);
    }

    private async Task<SocialPublishResult> RunJobAsync(SocialPublishJob job, IProgress<double>? progress, CancellationToken ct)
    {
        var req = job.Request;
        SocialPublishResult result;
        try
        {
            if (string.IsNullOrWhiteSpace(req.VideoPath) || !File.Exists(req.VideoPath))
                result = Fail(req, "게시할 영상 파일이 없습니다.");
            else if (!IsEnabled(req.Target))
                result = Fail(req, LabelOf(req.Target) + " 자격이 구성되지 않았습니다(환경변수/설정 확인).");
            else if (req.Target == SocialPublishTarget.InstagramReels && !req.PublicVisibility)
                result = Fail(req, "Instagram 은 비공개 게시를 지원하지 않습니다 — 공개 게시에 명시 동의(PublicVisibility)가 필요합니다.");
            else
            {
                QaInspectionReport? qa = null;
                if (!req.SkipQa)
                {
                    Update(job, SocialPublishState.Inspecting, 0, null);
                    qa = await _qa.InspectAsync(req.VideoPath, req.Subtitles, ct).ConfigureAwait(false);
                    if (!qa.CanPublish)
                    {
                        result = new(req.Target, false, SocialPublishState.QaBlocked, null, null, "QA 게이트 차단 — " + qa.Message, qa);
                        Update(job, result.State, 0, result);
                        return result;
                    }
                }
                Update(job, SocialPublishState.Uploading, 0, null);
                var prog = new Progress<double>(p => { job.Progress = p; progress?.Report(p); });
                result = req.Target switch
                {
                    SocialPublishTarget.YouTubeShorts => await PublishYouTubeAsync(req, prog, ct).ConfigureAwait(false),
                    SocialPublishTarget.TikTok => await PublishTikTokAsync(job, prog, ct).ConfigureAwait(false),
                    _ => await PublishInstagramAsync(job, prog, ct).ConfigureAwait(false),
                };
                if (qa is not null) result = result with { Qa = qa };
            }
        }
        catch (OperationCanceledException) { result = new(req.Target, false, SocialPublishState.Canceled, null, null, "게시가 취소되었습니다."); }
        catch (Exception ex) { Program.Log("SocialPublisher.Run", ex); result = Fail(req, "게시 실패: " + ex.Message); }
        Update(job, result.State, result.Ok ? 100 : job.Progress, result);
        return result;
    }

    private async Task<SocialPublishResult> PublishYouTubeAsync(SocialPublishRequest req, IProgress<double> progress, CancellationToken ct)
    {
        var r = await _youtube.UploadAsync(req.VideoPath, ShortsTitle(req.Title), req.Description ?? "", req.Tags ?? Array.Empty<string>(),
            req.PublicVisibility ? "public" : "private", req.ScheduleAtUtc, progress, ct).ConfigureAwait(false);
        return r.Ok
            ? new(req.Target, true, req.ScheduleAtUtc is null ? SocialPublishState.Published : SocialPublishState.Scheduled, r.VideoId, r.Url, r.Message)
            : Fail(req, r.Message);
    }

    private async Task<SocialPublishResult> PublishTikTokAsync(SocialPublishJob job, IProgress<double> progress, CancellationToken ct)
    {
        var req = job.Request;
        var token = await GetTikTokTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return Fail(req, "TikTok 토큰 갱신 실패(자격/네트워크 확인).");

        var size = new FileInfo(req.VideoPath).Length;
        var plan = PlanTikTokChunks(size);
        var payload = BuildTikTokInitPayload(req, plan);
        var (initStatus, initBody) = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, TikTokApi + "post/publish/video/init/")
            { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return m;
        }, ct).ConfigureAwait(false);
        if (initStatus != 200) return Fail(req, "TikTok 업로드 초기화 실패(HTTP " + initStatus.ToString(CultureInfo.InvariantCulture) + ").");
        if (!TryReadTikTokData(initBody, out var data, out var err) || data is null) return Fail(req, "TikTok 초기화 응답 오류: " + err);
        var publishId = data["publish_id"]?.GetValue<string>();
        var uploadUrl = data["upload_url"]?.GetValue<string>();
        if (string.IsNullOrEmpty(publishId) || string.IsNullOrEmpty(uploadUrl) || !uploadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Fail(req, "TikTok 초기화 응답에 publish_id/upload_url 이 없습니다.");

        await using (var fs = new FileStream(req.VideoPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous))
        {
            for (var i = 0; i < plan.TotalChunks; i++)
            {
                var (start, end) = plan.RangeOf(i);
                using var put = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
                { Content = new FileRangeContent(fs, start, end - start + 1, size, progress, ct) };
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                put.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, size);
                using var resp = await _http.SendAsync(put, ct).ConfigureAwait(false);
                var code = (int)resp.StatusCode;
                if (code is not (200 or 201 or 206))
                    return Fail(req, "TikTok 청크 " + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + plan.TotalChunks.ToString(CultureInfo.InvariantCulture) + " 업로드 실패(HTTP " + code.ToString(CultureInfo.InvariantCulture) + ").");
            }
        }

        Update(job, SocialPublishState.Processing, 99, null);
        var statusPayload = new JsonObject { ["publish_id"] = publishId }.ToJsonString();
        for (var poll = 0; poll < MaxPolls; poll++)
        {
            var (st, body) = await SendAsync(() =>
            {
                var m = new HttpRequestMessage(HttpMethod.Post, TikTokApi + "post/publish/status/fetch/")
                { Content = new StringContent(statusPayload, Encoding.UTF8, "application/json") };
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return m;
            }, ct).ConfigureAwait(false);
            if (st == 200 && TryReadTikTokData(body, out var sd, out _) && sd is not null)
            {
                var status = sd["status"]?.GetValue<string>() ?? "";
                if (status is "PUBLISH_COMPLETE" or "SEND_TO_USER_INBOX")
                    return new(req.Target, true, SocialPublishState.Published, publishId, null,
                        status == "PUBLISH_COMPLETE" ? "TikTok 게시 완료" : "TikTok 업로드 완료(앱 받은편지함에서 최종 게시)");
                if (status == "FAILED")
                    return Fail(req, "TikTok 처리 실패: " + (sd["fail_reason"]?.GetValue<string>() ?? "unknown"));
            }
            if (PollInterval > TimeSpan.Zero) await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        return new(req.Target, true, SocialPublishState.Processing, publishId, null, "TikTok 업로드 완료 · 처리 대기 중(상태 확인 시간 초과)");
    }

    private async Task<SocialPublishResult> PublishInstagramAsync(SocialPublishJob job, IProgress<double> progress, CancellationToken ct)
    {
        var req = job.Request;
        var token = await GetInstagramTokenAsync(ct).ConfigureAwait(false);
        if (token is null) return Fail(req, "Instagram 토큰 갱신 실패(자격/네트워크 확인).");
        var user = IgUserId;

        // ① REELS 컨테이너(resumable) 생성.
        var form = BuildInstagramContainerForm(req);
        var (cst, cbody) = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, GraphApi + Uri.EscapeDataString(user) + "/media") { Content = new FormUrlEncodedContent(form) };
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return m;
        }, ct).ConfigureAwait(false);
        if (cst != 200) return Fail(req, "Instagram 컨테이너 생성 실패(HTTP " + cst.ToString(CultureInfo.InvariantCulture) + ").");
        var containerId = ReadString(cbody, "id");
        if (string.IsNullOrEmpty(containerId)) return Fail(req, "Instagram 컨테이너 ID 를 받지 못했습니다.");

        // ② rupload 바이트 업로드(스트리밍).
        var size = new FileInfo(req.VideoPath).Length;
        await using (var fs = new FileStream(req.VideoPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous))
        {
            using var up = new HttpRequestMessage(HttpMethod.Post, RuploadApi + Uri.EscapeDataString(containerId))
            { Content = new FileRangeContent(fs, 0, size, size, progress, ct) };
            up.Headers.TryAddWithoutValidation("Authorization", "OAuth " + token);
            up.Headers.TryAddWithoutValidation("offset", "0");
            up.Headers.TryAddWithoutValidation("file_size", size.ToString(CultureInfo.InvariantCulture));
            using var resp = await _http.SendAsync(up, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return Fail(req, "Instagram 업로드 실패(HTTP " + ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture) + ").");
        }

        // ③ 컨테이너 처리 폴링.
        Update(job, SocialPublishState.Processing, 99, null);
        var ready = false;
        for (var poll = 0; poll < MaxPolls && !ready; poll++)
        {
            var (st, body) = await SendAsync(() =>
            {
                var m = new HttpRequestMessage(HttpMethod.Get, GraphApi + Uri.EscapeDataString(containerId) + "?fields=status_code");
                m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return m;
            }, ct).ConfigureAwait(false);
            var code = st == 200 ? ReadString(body, "status_code") : null;
            if (code is "ERROR" or "EXPIRED") return Fail(req, "Instagram 처리 실패(" + code + ").");
            ready = code is "FINISHED" or "PUBLISHED";
            if (!ready && PollInterval > TimeSpan.Zero) await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        if (!ready) return Fail(req, "Instagram 처리 대기 시간 초과 — 재시도해 주세요.");

        // ④ 게시.
        var (pst, pbody) = await SendAsync(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, GraphApi + Uri.EscapeDataString(user) + "/media_publish")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["creation_id"] = containerId }) };
            m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return m;
        }, ct).ConfigureAwait(false);
        var mediaId = pst == 200 ? ReadString(pbody, "id") : null;
        return string.IsNullOrEmpty(mediaId)
            ? Fail(req, "Instagram 게시 실패(HTTP " + pst.ToString(CultureInfo.InvariantCulture) + ").")
            : new(req.Target, true, SocialPublishState.Published, mediaId, null, "Instagram Reels 게시 완료");
    }

    // ── 토큰 갱신 파이프라인 ──

    private async Task<string?> GetTikTokTokenAsync(CancellationToken ct)
    {
        var cur = Volatile.Read(ref _tiktok);
        if (cur is not null && cur.IsFresh(DateTimeOffset.UtcNow)) return cur.AccessToken;
        var refresh = cur?.RefreshToken ?? TikTokRefreshToken;
        if (refresh.Length == 0) return null;
        var form = new Dictionary<string, string>
        {
            ["client_key"] = TikTokClientKey,
            ["client_secret"] = TikTokClientSecret,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
        };
        var (st, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, TikTokApi + "oauth/token/") { Content = new FormUrlEncodedContent(form) }, ct).ConfigureAwait(false);
        if (st != 200 || !TryParseToken(body, DateTimeOffset.UtcNow, out var tok)) return null;
        Volatile.Write(ref _tiktok, tok with { RefreshToken = tok.RefreshToken ?? refresh });   // 회전된 리프레시 토큰은 메모리 보관
        return tok.AccessToken;
    }

    private async Task<string?> GetInstagramTokenAsync(CancellationToken ct)
    {
        var cur = Volatile.Read(ref _instagram);
        if (cur is not null && cur.IsFresh(DateTimeOffset.UtcNow)) return cur.AccessToken;
        var seed = cur?.AccessToken is { Length: > 0 } c ? c : IgAccessToken;
        if (seed.Length == 0) return null;
        if (IgAppId.Length == 0 || IgAppSecret.Length == 0)
        {
            Volatile.Write(ref _instagram, new OAuthToken(seed, DateTimeOffset.UtcNow.AddHours(1), null));   // 앱 자격 없음 → 장기 토큰 그대로 사용
            return seed;
        }
        var url = GraphApi + "oauth/access_token?grant_type=fb_exchange_token&client_id=" + Uri.EscapeDataString(IgAppId)
            + "&client_secret=" + Uri.EscapeDataString(IgAppSecret) + "&fb_exchange_token=" + Uri.EscapeDataString(seed);
        var (st, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        if (st == 200 && TryParseToken(body, DateTimeOffset.UtcNow, out var tok)) { Volatile.Write(ref _instagram, tok); return tok.AccessToken; }
        Volatile.Write(ref _instagram, new OAuthToken(seed, DateTimeOffset.UtcNow.AddMinutes(10), null));   // 갱신 실패 → 기존 토큰으로 폴백(짧게 캐시)
        return seed;
    }

    // ── HTTP(타임아웃 · 429/503/5xx 지수 백오프) ──

    private async Task<(int Status, string Body)> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            int? retryAfter = null;
            int status;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(MetaTimeout);
                try
                {
                    using var msg = factory();
                    using var resp = await _http.SendAsync(msg, timeout.Token).ConfigureAwait(false);
                    status = (int)resp.StatusCode;
                    var body = await resp.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
                    if (!IsRetryable(status) || attempt >= MaxAttempts) return (status, body);
                    if (resp.Headers.RetryAfter?.Delta is { } delta) retryAfter = (int)Math.Ceiling(delta.TotalSeconds);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (attempt >= MaxAttempts) return (408, "");
                }
                catch (HttpRequestException)
                {
                    if (attempt >= MaxAttempts) return (0, "");
                }
            }
            var wait = PollInterval > TimeSpan.Zero ? QueueResourceGovernor.BackoffSeconds(attempt, retryAfter) : 0;
            if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
        }
    }

    public static bool IsRetryable(int status) => status is 429 or 503 or 502 or 504 or 500;

    private static bool TryReadTikTokData(string body, out JsonObject? data, out string error)
    {
        data = null;
        error = "invalid json";
        try
        {
            if (JsonNode.Parse(body) is not JsonObject root) return false;
            var code = root["error"]?["code"]?.GetValue<string>();
            if (code is not null && code != "ok") { error = code; return false; }
            data = root["data"] as JsonObject;
            error = data is null ? "data 누락" : "";
            return data is not null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    private static string? ReadString(string body, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // ── 작업 목록 · 로컬 스케줄러 ──

    private SocialPublishJob Register(SocialPublishRequest req)
    {
        var job = new SocialPublishJob { Request = req, State = SocialPublishState.Queued };
        lock (_gate)
        {
            _jobs.Insert(0, job);
            if (_jobs.Count > MaxJobs) _jobs.RemoveAt(_jobs.Count - 1);
        }
        Notify();
        return job;
    }

    private void Update(SocialPublishJob job, SocialPublishState state, double progress, SocialPublishResult? result)
    {
        job.State = state;
        job.Progress = progress;
        if (result is not null) job.Result = result;
        Notify();
    }

    /// <summary>예약 시각 도래 작업 발행(앱 실행 중 · 테스트에서 직접 호출 가능).</summary>
    public int DispatchDue(DateTime utcNow)
    {
        var due = new List<SocialPublishJob>(4);
        lock (_gate)
            foreach (var j in _jobs)
                if (j.State == SocialPublishState.Scheduled && j.Request.Target != SocialPublishTarget.YouTubeShorts
                    && j.Request.ScheduleAtUtc is { } at && at.ToUniversalTime() <= utcNow)
                {
                    j.State = SocialPublishState.Queued;   // 중복 발행 차단
                    due.Add(j);
                }
        foreach (var j in due) _ = RunJobAsync(j, null, _life.Token);   // RunJobAsync 는 예외를 결과로 흡수
        return due.Count;
    }

    private void OnSchedulerTick(object? _)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _schedTick, 1) != 0) return;
        try { DispatchDue(DateTime.UtcNow); }
        catch (Exception ex) { Program.Log("SocialPublisher.Scheduler", ex); }
        finally { Volatile.Write(ref _schedTick, 0); }
    }

    private void Notify()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("SocialPublisher.OnChange", ex); }
    }

    private static SocialPublishResult Fail(SocialPublishRequest req, string message) => new(req.Target, false, SocialPublishState.Failed, null, null, message);

    public static string LabelOf(SocialPublishTarget t) => t switch
    {
        SocialPublishTarget.YouTubeShorts => "YouTube Shorts",
        SocialPublishTarget.TikTok => "TikTok",
        _ => "Instagram Reels",
    };

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _scheduler.Dispose();
        try { _life.Cancel(); } catch (ObjectDisposedException) { }
        _life.Dispose();
        _http.Dispose();
    }

    /// <summary>파일 구간 스트리밍 업로드(전체 적재 0 · 풀 버퍼 · 전체 파일 대비 진행률).</summary>
    private sealed class FileRangeContent(Stream src, long offset, long length, long total, IProgress<double>? progress, CancellationToken ct) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                src.Seek(offset, SeekOrigin.Begin);
                long left = length;
                var lastPct = -1;
                while (left > 0)
                {
                    var read = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(BufferSize, left)), ct).ConfigureAwait(false);
                    if (read <= 0) throw new EndOfStreamException();
                    await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    left -= read;
                    if (progress is not null && total > 0)
                    {
                        var pct = (int)((offset + length - left) * 100L / total);
                        if (pct != lastPct) { lastPct = pct; progress.Report(Math.Min(99, pct)); }
                    }
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        protected override bool TryComputeLength(out long len) { len = length; return true; }
    }
}
