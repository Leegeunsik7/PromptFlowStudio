using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.SocialMedia;

/// <summary>v17.30.1 릴스 발행 단계.</summary>
public enum ReelsPublishStage { Idle, CreatingContainer, Uploading, Polling, Publishing, Published, Failed, Canceled }

/// <summary>v17.30.1 발행 결과(예외 0 — 실패도 결과로 반환).</summary>
public sealed record ReelsPublishResult(bool Ok, ReelsPublishStage Stage, string? ContainerId, string? MediaId, string Message);

/// <summary>
/// v17.30.1 Instagram 자격 — 환경변수 우선(<c>PFS_IG_USER_ID</c>/<c>IG_USER_ID</c> · <c>PFS_IG_ACCESS_TOKEN</c>/<c>IG_ACCESS_TOKEN</c>)
/// → appsettings(<c>Instagram:UserId</c> · <c>Instagram:AccessToken</c>). 하드코딩 0 · <see cref="ToString"/> 은 토큰 마스킹.
/// </summary>
public sealed record InstagramReelsOptions(string UserId, string AccessToken, string ApiVersion = InstagramReelsService.DefaultApiVersion)
{
    public bool IsConfigured => UserId.Length > 0 && AccessToken.Length > 0;

    public static InstagramReelsOptions Resolve(IConfiguration? cfg) => new(
        Pick(cfg, "Instagram:UserId", "PFS_IG_USER_ID", "IG_USER_ID"),
        Pick(cfg, "Instagram:AccessToken", "PFS_IG_ACCESS_TOKEN", "IG_ACCESS_TOKEN"),
        Pick(cfg, "Instagram:ApiVersion", "PFS_IG_API_VERSION") is { Length: > 0 } v ? v : InstagramReelsService.DefaultApiVersion);

    private static string Pick(IConfiguration? cfg, string key, params string[] envs)
    {
        foreach (var e in envs)
        {
            var v = Environment.GetEnvironmentVariable(e);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        }
        return cfg?[key]?.Trim() ?? "";
    }

    public override string ToString() => "InstagramReelsOptions { UserId = " + UserId + ", AccessToken = ***, ApiVersion = " + ApiVersion + " }";
}

/// <summary>
/// [PromptFlowStudio] v17.30.1 📲 Instagram Reels 발행 서비스 — Meta Graph API(v19+) 3단계 파이프라인(순수 BCL · HttpClient + System.Text.Json).
///   ▸ ① <c>POST /{ig-user-id}/media</c>(media_type=REELS · video_url · caption · share_to_feed) → ② <c>GET /{container}?fields=status_code</c>
///     FINISHED 폴링(ERROR/EXPIRED 즉시 실패) → ③ <c>POST /{ig-user-id}/media_publish</c>(creation_id).
///   ▸ 로컬 MP4 는 기존 <see cref="DirectSocialPublisherBridge"/>(resumable rupload · QA 게이트) 위임 — 업로드 경로 중복 구현 0. 브릿지 미등록(모바일) 시 Graceful 실패.
///   ▸ 토큰은 Authorization 헤더로만 전송(URL/로그 노출 0) · 요청별 30초 타임아웃 · 429/5xx 지수 백오프 3회(Retry-After 우선).
///   ▸ 단계/진행률은 <see cref="OnChange"/> 로 통지 — 구독자 예외는 격리.
/// </summary>
public sealed class InstagramReelsService : IDisposable
{
    public const string DefaultApiVersion = "v21.0";
    public const string GraphHost = "https://graph.facebook.com/";
    public const int MaxCaptionLength = 2200;
    public const int MaxHashtags = 30;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IConfiguration? _cfg;
    private readonly IServiceProvider? _sp;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InstagramReelsOptions? _override;
    private int _disposed;

    public InstagramReelsService(IConfiguration? cfg = null, IServiceProvider? sp = null, HttpMessageHandler? handler = null)
    {
        _cfg = cfg;
        _sp = sp;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 요청별 CTS 로 제한
    }

    /// <summary>DI 팩토리 — 구성은 선택(없으면 환경변수만).</summary>
    public static InstagramReelsService Create(IServiceProvider sp) => new(sp.GetService<IConfiguration>(), sp);

    /// <summary>폴링 간격(테스트에서 0 으로 단축).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public int MaxPolls { get; set; } = 60;
    /// <summary>백오프 기준(초) — 2^n 배. 테스트에서 0.</summary>
    public double BackoffBaseSeconds { get; set; } = 1.0;

    public ReelsPublishStage Stage { get; private set; }
    public double Progress { get; private set; }
    public string LastMessage { get; private set; } = "대기";
    public string? LastMediaId { get; private set; }
    public bool IsBusy => Stage is ReelsPublishStage.CreatingContainer or ReelsPublishStage.Uploading or ReelsPublishStage.Polling or ReelsPublishStage.Publishing;

    public event Action? OnChange;

    /// <summary>현재 자격(테스트/설정 화면 주입 우선).</summary>
    public InstagramReelsOptions Options => _override ?? InstagramReelsOptions.Resolve(_cfg);

    /// <summary>런타임 자격 주입(메모리만 · 영속 0). null 이면 환경변수/설정으로 복귀.</summary>
    public void UseOptions(InstagramReelsOptions? options) => _override = options;

    public bool IsConfigured => Options.IsConfigured;

    // ── 순수 함수(테스트 잠금) ──

    /// <summary>캡션 검증 — 오류 메시지(정상이면 null).</summary>
    public static string? ValidateCaption(string? caption)
    {
        if (caption is null) return null;
        if (caption.Length > MaxCaptionLength) return "캡션은 " + MaxCaptionLength.ToString(CultureInfo.InvariantCulture) + "자 이하여야 합니다.";
        var tags = 0;
        foreach (var c in caption) if (c == '#') tags++;
        return tags > MaxHashtags ? "해시태그는 " + MaxHashtags.ToString(CultureInfo.InvariantCulture) + "개 이하여야 합니다." : null;
    }

    /// <summary>video_url 은 공개 HTTPS 만 허용(Graph API 가 직접 fetch).</summary>
    public static bool IsPublicVideoUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !u.IsLoopback;

    /// <summary>REELS 컨테이너 생성 폼.</summary>
    public static Dictionary<string, string> BuildContainerForm(string videoUrl, string? caption, bool shareToFeed, double? coverOffsetSeconds = null)
    {
        var f = new Dictionary<string, string>(5)
        {
            ["media_type"] = "REELS",
            ["video_url"] = videoUrl,
            ["share_to_feed"] = shareToFeed ? "true" : "false",
        };
        if (!string.IsNullOrEmpty(caption)) f["caption"] = caption;
        if (coverOffsetSeconds is { } c && c >= 0) f["thumb_offset"] = ((long)(c * 1000)).ToString(CultureInfo.InvariantCulture);
        return f;
    }

    public static string MediaEndpoint(InstagramReelsOptions o) => GraphHost + o.ApiVersion + "/" + Uri.EscapeDataString(o.UserId) + "/media";
    public static string PublishEndpoint(InstagramReelsOptions o) => GraphHost + o.ApiVersion + "/" + Uri.EscapeDataString(o.UserId) + "/media_publish";
    public static string StatusEndpoint(InstagramReelsOptions o, string containerId) =>
        GraphHost + o.ApiVersion + "/" + Uri.EscapeDataString(containerId) + "?fields=status_code,status";

    /// <summary>JSON 최상위 문자열 필드(스키마 불일치 → null).</summary>
    public static string? ReadString(string body, string name)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Graph 오류 메시지(<c>error.message</c>) — 없으면 null.</summary>
    public static string? ReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e)
                && e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    public static bool IsRetryable(int status) => status is 429 or 500 or 502 or 503 or 504;

    // ── 오케스트레이션 ──

    /// <summary>공개 URL 영상 발행 — 컨테이너 → FINISHED 폴링 → media_publish. 동시 발행은 1건으로 직렬화.</summary>
    public async Task<ReelsPublishResult> PublishFromUrlAsync(string videoUrl, string? caption, bool shareToFeed = true, CancellationToken ct = default)
    {
        var o = Options;
        if (!o.IsConfigured) return Finish(Fail(null, "Instagram 자격이 구성되지 않았습니다(PFS_IG_USER_ID · PFS_IG_ACCESS_TOKEN)."));
        if (!IsPublicVideoUrl(videoUrl)) return Finish(Fail(null, "video_url 은 공개 HTTPS 주소여야 합니다(로컬 파일은 PublishFileAsync)."));
        if (ValidateCaption(caption) is { } capErr) return Finish(Fail(null, capErr));

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return Fail(null, "다른 발행이 진행 중입니다.");
        string? containerId = null;
        try
        {
            Set(ReelsPublishStage.CreatingContainer, 0.05, "① REELS 컨테이너 생성");
            var form = BuildContainerForm(videoUrl, caption, shareToFeed);
            var (cst, cbody) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, MediaEndpoint(o)) { Content = new FormUrlEncodedContent(form) }, o, ct).ConfigureAwait(false);
            containerId = cst == 200 ? ReadString(cbody, "id") : null;
            if (string.IsNullOrEmpty(containerId))
                return Finish(Fail(null, "컨테이너 생성 실패(HTTP " + cst.ToString(CultureInfo.InvariantCulture) + ")" + Detail(cbody)));

            var ready = false;
            for (var poll = 0; poll < MaxPolls && !ready; poll++)
            {
                Set(ReelsPublishStage.Polling, 0.1 + 0.8 * poll / Math.Max(1, MaxPolls), "② 처리 상태 폴링 " + (poll + 1).ToString(CultureInfo.InvariantCulture));
                var (st, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, StatusEndpoint(o, containerId)), o, ct).ConfigureAwait(false);
                var code = st == 200 ? ReadString(body, "status_code") : null;
                if (code is "ERROR" or "EXPIRED")
                    return Finish(Fail(containerId, "컨테이너 처리 실패(" + code + ")" + (ReadString(body, "status") is { } s ? " — " + s : "")));
                ready = code is "FINISHED" or "PUBLISHED";
                if (!ready && PollInterval > TimeSpan.Zero) await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
            if (!ready) return Finish(Fail(containerId, "처리 대기 시간 초과 — 컨테이너 " + containerId + " 로 재시도하세요."));

            Set(ReelsPublishStage.Publishing, 0.95, "③ media_publish");
            var (pst, pbody) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, PublishEndpoint(o))
            { Content = new FormUrlEncodedContent(new Dictionary<string, string>(1) { ["creation_id"] = containerId }) }, o, ct).ConfigureAwait(false);
            var mediaId = pst == 200 ? ReadString(pbody, "id") : null;
            if (string.IsNullOrEmpty(mediaId))
                return Finish(Fail(containerId, "게시 실패(HTTP " + pst.ToString(CultureInfo.InvariantCulture) + ")" + Detail(pbody)));
            LastMediaId = mediaId;
            return Finish(new(true, ReelsPublishStage.Published, containerId, mediaId, "Instagram Reels 게시 완료"));
        }
        catch (OperationCanceledException) { return Finish(new(false, ReelsPublishStage.Canceled, containerId, null, "취소됨")); }
        catch (Exception ex)
        {
            Program.Log("InstagramReels.PublishFromUrl", ex);
            return Finish(Fail(containerId, "발행 예외: " + ex.Message));
        }
        finally { _gate.Release(); }
    }

    /// <summary>로컬 MP4 발행 — <see cref="DirectSocialPublisherBridge"/>(resumable 업로드·QA 게이트) 위임. 공개 게시 명시 동의 필요.</summary>
    public async Task<ReelsPublishResult> PublishFileAsync(string videoPath, string? caption, bool publicConsent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath)) return Finish(Fail(null, "게시할 영상 파일이 없습니다."));
        if (ValidateCaption(caption) is { } capErr) return Finish(Fail(null, capErr));
        var bridge = _sp?.GetService<DirectSocialPublisherBridge>();
        if (bridge is null) return Finish(Fail(null, "로컬 업로드 브릿지 미탑재 — 공개 URL(PublishFromUrlAsync)로 발행하세요."));

        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false)) return Fail(null, "다른 발행이 진행 중입니다.");
        try
        {
            Set(ReelsPublishStage.Uploading, 0, "로컬 업로드(resumable)");
            var prog = new Progress<double>(p => Set(ReelsPublishStage.Uploading, Math.Clamp(p, 0, 1) * 0.9, "업로드 " + (p * 100).ToString("0", CultureInfo.InvariantCulture) + "%"));
            var r = await bridge.PublishAsync(new SocialPublishRequest(SocialPublishTarget.InstagramReels, videoPath, "", caption ?? "",
                Array.Empty<string>(), PublicVisibility: publicConsent), prog, ct).ConfigureAwait(false);
            if (r.Ok && r.State == SocialPublishState.Published) LastMediaId = r.PostId;
            return Finish(r.Ok
                ? new(true, r.State == SocialPublishState.Published ? ReelsPublishStage.Published : ReelsPublishStage.Polling, null, r.PostId, r.Message)
                : new(false, r.State == SocialPublishState.Canceled ? ReelsPublishStage.Canceled : ReelsPublishStage.Failed, null, null, r.Message));
        }
        catch (OperationCanceledException) { return Finish(new(false, ReelsPublishStage.Canceled, null, null, "취소됨")); }
        catch (Exception ex)
        {
            Program.Log("InstagramReels.PublishFile", ex);
            return Finish(Fail(null, "발행 예외: " + ex.Message));
        }
        finally { _gate.Release(); }
    }

    // ── 내부 ──

    private async Task<(int Status, string Body)> SendAsync(Func<HttpRequestMessage> factory, InstagramReelsOptions o, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            int status;
            string body;
            TimeSpan? retryAfter;
            try
            {
                using var req = factory();
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.AccessToken);
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                status = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                retryAfter = resp.Headers.RetryAfter?.Delta;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = 504; body = ""; retryAfter = null; }   // 요청 타임아웃 → 재시도 대상
            catch (HttpRequestException) { status = 503; body = ""; retryAfter = null; }

            if (!IsRetryable(status) || attempt >= MaxAttempts) return (status, body);
            var delay = retryAfter ?? TimeSpan.FromSeconds(BackoffBaseSeconds * (1 << (attempt - 1)));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private static string Detail(string body) => ReadError(body) is { } e ? " — " + e : "";

    private static ReelsPublishResult Fail(string? containerId, string msg) => new(false, ReelsPublishStage.Failed, containerId, null, msg);

    private ReelsPublishResult Finish(ReelsPublishResult r)
    {
        Set(r.Stage, r.Ok ? 1 : Progress, r.Message);
        return r;
    }

    private void Set(ReelsPublishStage stage, double progress, string message)
    {
        Stage = stage;
        Progress = progress;
        LastMessage = message;
        var h = OnChange;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { Program.Log("InstagramReels.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        OnChange = null;
        _http.Dispose();
        _gate.Dispose();
    }
}
