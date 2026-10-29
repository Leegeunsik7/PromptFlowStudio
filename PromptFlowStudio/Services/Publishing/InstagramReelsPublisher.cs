using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Configuration;
using PromptFlowStudio.Services.SocialMedia;

namespace PromptFlowStudio.Services.Publishing;

/// <summary>
/// [PromptFlowStudio] 🚀 Instagram Reels 원클릭 발행 — 순수 BCL. 업로드 파이프라인은 기존 <see cref="InstagramReelsService"/> 위임(중복 구현 0).
///   ▸ 경로 선택: ① 공개 HTTPS URL + Graph 자격 → <c>POST /{ig_user_id}/media(media_type=REELS)</c> → FINISHED 폴링 → <c>media_publish</c>
///     ② 웹훅 브릿지 URL(Zapier/Make/자체 서버) → JSON 디스패치(토큰 미포함) ③ Graph 자격 + 공개 동의 → 로컬 업로드 브릿지 ④ 미구성 → 모의 발행.
///   ▸ 자격: ⚙️ 환경설정 SNS 저장소 우선(<see cref="InstagramReelsService.UseOptions"/> 메모리 주입) → 환경변수/appsettings 폴백.
///   ▸ 웹훅은 30초 타임아웃 · 429/5xx 지수 백오프 3회.
/// </summary>
public sealed class InstagramReelsPublisher : IInstagramReelsPublisher, IDisposable
{
    public const string WebhookEvent = "instagram.reels.publish";
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly InstagramReelsService _reels;
    private readonly Func<SnsCredentials> _creds;
    private readonly HttpClient _http;

    public static InstagramReelsPublisher Create(AppSettingsManager settings, InstagramReelsService reels) => new(reels, () => settings.Sns);

    /// <param name="handler">웹훅 테스트 주입.</param>
    public InstagramReelsPublisher(InstagramReelsService reels, Func<SnsCredentials> creds, HttpMessageHandler? handler = null)
    {
        _reels = reels ?? throw new ArgumentNullException(nameof(reels));
        _creds = creds ?? throw new ArgumentNullException(nameof(creds));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public double BackoffBaseSeconds { get; set; } = 1.0;

    public bool Enabled
    {
        get
        {
            var c = SafeCreds();
            return c.InstagramConfigured || c.InstagramBridgeConfigured || _reels.IsConfigured;
        }
    }

    // ── 순수 함수(테스트 잠금) ──

    /// <summary>REELS 컨테이너 생성 폼(<c>media_type=REELS</c> · video_url · caption · share_to_feed).</summary>
    public static Dictionary<string, string> BuildContainerPayload(string videoUrl, string? caption, bool shareToFeed = true)
        => InstagramReelsService.BuildContainerForm(videoUrl, caption, shareToFeed);

    /// <summary>웹훅 브릿지 JSON — 액세스 토큰은 절대 포함하지 않는다(브릿지가 자체 연동 보유).</summary>
    public static string BuildWebhookPayload(string videoPath, string caption, string? igUserId, DateTimeOffset at)
        => new JsonObject
        {
            ["event"] = WebhookEvent,
            ["platform"] = "instagram",
            ["media_type"] = "REELS",
            ["video_path"] = videoPath,
            ["file_name"] = Path.GetFileName(videoPath),
            ["caption"] = caption,
            ["ig_user_id"] = igUserId ?? "",
            ["share_to_feed"] = true,
            ["requested_at"] = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
        }.ToJsonString();

    /// <summary>웹훅 URL 허용 — 절대 http(s) 만.</summary>
    public static bool IsValidWebhookUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    // ── 오케스트레이션 ──

    public async Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return new(false, false, ReelsRoute.None, null, "게시할 영상 파일이 없습니다.");
        if (InstagramReelsService.ValidateCaption(caption) is { } capErr) return new(false, false, ReelsRoute.None, null, capErr);

        var c = SafeCreds();
        if (c.InstagramConfigured) _reels.UseOptions(new InstagramReelsOptions(c.InstagramUserId, c.InstagramAccessToken));
        var graph = _reels.IsConfigured;

        try
        {
            if (graph && InstagramReelsService.IsPublicVideoUrl(publicVideoUrl))
                return FromReels(await RunTrackedAsync(() => _reels.PublishFromUrlAsync(publicVideoUrl!, caption, true, ct), progress).ConfigureAwait(false), ReelsRoute.GraphApi);

            if (c.InstagramBridgeConfigured)
                return await DispatchWebhookAsync(c, videoPath, caption, progress, ct).ConfigureAwait(false);

            if (graph)
            {
                if (!publicConsent) return new(false, false, ReelsRoute.LocalBridge, null, "로컬 파일 게시는 공개 게시 동의가 필요합니다.");
                return FromReels(await RunTrackedAsync(() => _reels.PublishFileAsync(videoPath, caption, true, ct), progress).ConfigureAwait(false), ReelsRoute.LocalBridge);
            }

            progress?.Report(1);
            return new(true, true, ReelsRoute.Mock, "mock-" + Guid.NewGuid().ToString("N")[..11],
                "🧪 모의 발행 — Instagram 자격/웹훅 미구성(⚙️ 환경설정 > SNS 채널 연동)");
        }
        catch (OperationCanceledException) { return new(false, false, ReelsRoute.None, null, "취소됨"); }
        catch (Exception ex) { Program.Log("InstagramReelsPublisher.Publish", ex); return new(false, false, ReelsRoute.None, null, "발행 예외: " + ex.Message); }
    }

    /// <summary>Graph 자격 유효성 — <c>GET /{ig_user_id}?fields=id,username</c> 200 + id 일치면 true(토큰은 헤더로만 · 30초 타임아웃).</summary>
    public async Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default)
    {
        var uid = (userId ?? "").Trim();
        var tok = (accessToken ?? "").Trim();
        if (uid.Length == 0 || tok.Length == 0) return false;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                InstagramReelsService.GraphHost + InstagramReelsService.DefaultApiVersion + "/" + Uri.EscapeDataString(uid) + "?fields=id,username");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tok);
            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return string.Equals(InstagramReelsService.ReadString(body, "id"), uid, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { return false; }
    }

    // ── 내부 ──

    private async Task<ReelsPublishOutcome> DispatchWebhookAsync(SnsCredentials c, string videoPath, string caption, IProgress<double>? progress, CancellationToken ct)
    {
        if (!IsValidWebhookUrl(c.InstagramWebhookUrl)) return new(false, false, ReelsRoute.Webhook, null, "웹훅 URL 형식이 올바르지 않습니다(http/https).");
        var json = BuildWebhookPayload(videoPath, caption, c.InstagramUserId, DateTimeOffset.UtcNow);
        progress?.Report(0.1);
        for (var attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            int status;
            TimeSpan? retryAfter = null;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, c.InstagramWebhookUrl) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("X-PFS-Event", WebhookEvent);
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                status = (int)resp.StatusCode;
                retryAfter = resp.Headers.RetryAfter?.Delta;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = 504; }
            catch (HttpRequestException) { status = 503; }

            if (status is >= 200 and < 300)
            {
                progress?.Report(1);
                return new(true, false, ReelsRoute.Webhook, null, "웹훅 브릿지 전송 완료 — 브릿지(Zapier/Make)가 릴스를 게시합니다.");
            }
            if (status is not (429 or 500 or 502 or 503 or 504) || attempt >= MaxAttempts)
                return new(false, false, ReelsRoute.Webhook, null, "웹훅 전송 실패(HTTP " + status.ToString(CultureInfo.InvariantCulture) + ").");
            var delay = retryAfter ?? TimeSpan.FromSeconds(BackoffBaseSeconds * (1 << (attempt - 1)));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    /// <summary>InstagramReelsService 진행률(OnChange)을 호출자 progress 로 중계 — 구독/해제 쌍.</summary>
    private async Task<ReelsPublishResult> RunTrackedAsync(Func<Task<ReelsPublishResult>> run, IProgress<double>? progress)
    {
        void Relay() => progress?.Report(Math.Clamp(_reels.Progress, 0, 1));
        _reels.OnChange += Relay;
        try { return await run().ConfigureAwait(false); }
        finally { _reels.OnChange -= Relay; }
    }

    private static ReelsPublishOutcome FromReels(ReelsPublishResult r, ReelsRoute route) => new(r.Ok, false, route, r.MediaId, r.Message);

    private SnsCredentials SafeCreds()
    {
        try { return _creds() ?? SnsCredentials.Empty; }
        catch (Exception ex) { Program.Log("InstagramReelsPublisher.Creds", ex); return SnsCredentials.Empty; }
    }

    public void Dispose() => _http.Dispose();
}
