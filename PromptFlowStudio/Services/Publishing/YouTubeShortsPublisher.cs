using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Configuration;

namespace PromptFlowStudio.Services.Publishing;

/// <summary>
/// [PromptFlowStudio] 🚀 YouTube Shorts 원클릭 발행 — 순수 BCL(HttpClient + System.Text.Json · 외부 NuGet 0).
///   ▸ ① OAuth2 리프레시 → 액세스 토큰(만료 60초 전 자동 갱신 · 401 시 1회 강제 갱신 재시도)
///     ② resumable 업로드(메타데이터 POST → Location 에 바이트 스트리밍 PUT · 진행률 0~1)
///     ③ 영상 옆 <c>.srt</c> 가 있으면 <c>captions.insert</c>(multipart/related)로 자막 트랙 등록(한글 감지 → ko, 그 외 en).
///   ▸ 자격: ⚙️ 환경설정 SNS 저장소(<see cref="AppSettingsManager.Sns"/> · 머신바운드 암호화) → appsettings <c>YouTube:*</c> 폴백.
///     미구성이면 오프라인 모의 발행(IsMock=true · 실제 전송 0)으로 UI 흐름만 검증.
///   ▸ 제어 요청은 30초 타임아웃 · 429/5xx 지수 백오프 3회. 자막 등록 실패는 업로드 성공을 무효화하지 않음([부분 구제]).
/// </summary>
public sealed class YouTubeShortsPublisher : IYouTubeShortsPublisher, IDisposable
{
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string UploadEndpoint = "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status";
    public const string CaptionEndpoint = "https://www.googleapis.com/upload/youtube/v3/captions?uploadType=multipart&part=snippet";
    private const int BufferSize = 262144;
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly Func<SnsCredentials> _creds;
    private readonly string _categoryId;
    private readonly HttpClient _http;
    private string? _token;
    private DateTime _tokenExpiresUtc;
    private string? _tokenOwner;

    /// <summary>DI — SNS 저장소 우선, 비면 appsettings <c>YouTube:ClientId/ClientSecret/RefreshToken</c>.</summary>
    public static YouTubeShortsPublisher Create(AppSettingsManager settings, IConfiguration? cfg)
        => new(() => settings.Sns.YouTubeConfigured ? settings.Sns : FromConfig(cfg),
               string.IsNullOrWhiteSpace(cfg?["YouTube:CategoryId"]) ? "22" : cfg!["YouTube:CategoryId"]!.Trim());

    /// <param name="handler">테스트 주입(가짜 Google 엔드포인트).</param>
    public YouTubeShortsPublisher(Func<SnsCredentials> creds, string categoryId = "22", HttpMessageHandler? handler = null)
    {
        _creds = creds ?? throw new ArgumentNullException(nameof(creds));
        _categoryId = categoryId;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;   // 대용량 업로드 — 요청별 CTS/ct 가 제한
    }

    /// <summary>백오프 기준(초) — 2^n 배. 테스트에서 0.</summary>
    public double BackoffBaseSeconds { get; set; } = 1.0;

    public bool Enabled => SafeCreds().YouTubeConfigured;

    // ── 순수 함수(테스트 잠금) ──

    /// <summary>훅·대본 → 쇼츠 메타데이터(제목 #Shorts · 타임스탬프 설명 · 태그 · 공개 범위 정규화).</summary>
    public static ShortsMetadata BuildMetadata(string? hook, IReadOnlyList<ScriptBeat>? beats, string? privacy = "private", DateTime? publishAtUtc = null)
        => new(SnsPublishFormatter.BuildTitle(hook), SnsPublishFormatter.BuildDescription(hook, beats),
               SnsPublishFormatter.BuildTags(hook), SnsPublishFormatter.NormalizePrivacy(privacy), publishAtUtc);

    /// <summary>videos.insert 리소스 JSON — 태그에 Shorts 보장 · 설명에 #Shorts 보장 · 예약 시 private 강제.</summary>
    public static string BuildVideoResource(ShortsMetadata meta, string categoryId = "22")
    {
        var tags = new JsonArray();
        var hasShorts = false;
        foreach (var t in meta.Tags ?? Array.Empty<string>())
        {
            if (tags.Count >= SnsPublishFormatter.MaxTags) break;
            hasShorts |= string.Equals(t, "Shorts", StringComparison.OrdinalIgnoreCase);
            tags.Add((JsonNode)t);
        }
        if (!hasShorts) tags.Insert(0, (JsonNode)"Shorts");

        var desc = (meta.Description ?? "").Trim();
        if (desc.IndexOf("#Shorts", StringComparison.OrdinalIgnoreCase) < 0) desc = (desc + "\n\n#Shorts").Trim();
        var privacy = meta.PublishAtUtc is null ? SnsPublishFormatter.NormalizePrivacy(meta.PrivacyStatus) : "private";
        var status = new JsonObject { ["privacyStatus"] = privacy, ["selfDeclaredMadeForKids"] = false };
        if (meta.PublishAtUtc is { } at)
            status["publishAt"] = at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        return new JsonObject
        {
            ["snippet"] = new JsonObject
            {
                ["title"] = Trunc(meta.Title, SnsPublishFormatter.MaxTitle),
                ["description"] = Trunc(desc, SnsPublishFormatter.MaxDescription),
                ["categoryId"] = categoryId,
                ["tags"] = tags,
            },
            ["status"] = status,
        }.ToJsonString();
    }

    /// <summary>captions.insert snippet JSON.</summary>
    public static string BuildCaptionResource(string videoId, string language, string name)
        => new JsonObject
        {
            ["snippet"] = new JsonObject { ["videoId"] = videoId, ["language"] = language, ["name"] = name, ["isDraft"] = false },
        }.ToJsonString();

    /// <summary>영상 옆 자막 탐색 — <c>{name}.srt</c> → (<c>_full</c> 접미 제거) <c>{project}.srt</c>. 없으면 null.</summary>
    public static string? ResolveSrtPath(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return null;
        var a = Path.ChangeExtension(videoPath, ".srt");
        if (File.Exists(a)) return a;
        var stem = Path.GetFileNameWithoutExtension(videoPath);
        if (stem.EndsWith("_full", StringComparison.OrdinalIgnoreCase))
        {
            var b = Path.Combine(Path.GetDirectoryName(videoPath) ?? "", stem[..^5] + ".srt");
            if (File.Exists(b)) return b;
        }
        return null;
    }

    /// <summary>자막 언어 — 한글 음절이 있으면 ko, 아니면 en.</summary>
    public static string DetectCaptionLanguage(string? srtText)
    {
        foreach (var c in srtText ?? "")
            if (c >= '가' && c <= '힣') return "ko";
        return "en";
    }

    // ── 오케스트레이션 ──

    public async Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return new(false, false, null, null, false, "업로드할 영상 파일이 없습니다.");
        if (meta is null) return new(false, false, null, null, false, "메타데이터가 없습니다.");
        var srt = srtPath is { Length: > 0 } && File.Exists(srtPath) ? srtPath : ResolveSrtPath(videoPath);

        var creds = SafeCreds();
        if (!creds.YouTubeConfigured)
        {
            progress?.Report(1);
            var mockId = "mock-" + Guid.NewGuid().ToString("N")[..11];
            return new(true, true, mockId, null, false,
                "🧪 모의 발행 — YouTube 자격 미구성(⚙️ 환경설정 > SNS 채널 연동)" + (srt is null ? "" : " · 자막 " + Path.GetFileName(srt) + " 감지"));
        }

        try
        {
            // ① 업로드 세션(401 → 토큰 강제 갱신 1회).
            var body = BuildVideoResource(meta, _categoryId);
            var fileLen = new FileInfo(videoPath).Length;
            string? location = null;
            for (var auth = 0; auth < 2 && location is null; auth++)
            {
                var token = await GetAccessTokenAsync(creds, force: auth > 0, ct).ConfigureAwait(false);
                if (token is null) return new(false, false, null, null, false, "액세스 토큰 발급 실패(자격/네트워크 확인).");
                var (st, _, loc) = await SendAsync(() =>
                {
                    var r = new HttpRequestMessage(HttpMethod.Post, UploadEndpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    r.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/*");
                    r.Headers.TryAddWithoutValidation("X-Upload-Content-Length", fileLen.ToString(CultureInfo.InvariantCulture));
                    return r;
                }, ct).ConfigureAwait(false);
                if (st == 401) { _token = null; continue; }
                if (st is < 200 or >= 300) return new(false, false, null, null, false, "업로드 세션 생성 실패(HTTP " + st.ToString(CultureInfo.InvariantCulture) + ").");
                location = loc;
                if (string.IsNullOrEmpty(location)) return new(false, false, null, null, false, "업로드 세션 URL 을 받지 못했습니다.");
            }
            if (location is null) return new(false, false, null, null, false, "인증 실패(401) — 리프레시 토큰을 다시 발급하세요.");

            // ② 바이트 스트리밍 PUT(파일 전체를 메모리에 올리지 않음).
            string putBody;
            int putStatus;
            await using (var fs = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous))
            using (var put = new HttpRequestMessage(HttpMethod.Put, location) { Content = new ProgressStreamContent(fs, fileLen, BufferSize, progress, ct) })
            {
                put.Content.Headers.ContentType = new MediaTypeHeaderValue("video/*");
                put.Content.Headers.ContentLength = fileLen;
                using var resp = await _http.SendAsync(put, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                putStatus = (int)resp.StatusCode;
                putBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            if (putStatus is < 200 or >= 300) return new(false, false, null, null, false, "업로드 실패(HTTP " + putStatus.ToString(CultureInfo.InvariantCulture) + ").");
            var id = ReadString(putBody, "id");
            if (id is null) return new(false, false, null, null, false, "업로드는 됐으나 영상 ID 를 확인하지 못했습니다.");
            var url = "https://youtube.com/shorts/" + id;

            // ③ 자막 트랙(실패해도 업로드 성공 유지).
            var captioned = false;
            var note = "";
            if (srt is not null)
            {
                captioned = await UploadCaptionAsync(creds, id, srt, ct).ConfigureAwait(false);
                note = captioned ? " · 자막 트랙 등록(" + Path.GetFileName(srt) + ")" : " · ⚠ 자막 등록 실패(영상은 업로드됨)";
            }
            progress?.Report(1);
            return new(true, false, id, url, captioned, "YouTube Shorts 업로드 완료" + note);
        }
        catch (OperationCanceledException) { return new(false, false, null, null, false, "업로드가 취소되었습니다."); }
        catch (Exception ex) { Program.Log("YouTubeShorts.Publish", ex); return new(false, false, null, null, false, "업로드 실패: " + ex.Message); }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        var c = SafeCreds();
        if (!c.YouTubeConfigured) return false;
        try { return await GetAccessTokenAsync(c, force: true, ct).ConfigureAwait(false) is not null; }
        catch (OperationCanceledException) { return false; }
    }

    // ── 내부 ──

    private async Task<bool> UploadCaptionAsync(SnsCredentials creds, string videoId, string srtPath, CancellationToken ct)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(srtPath, ct).ConfigureAwait(false);
            var lang = DetectCaptionLanguage(Encoding.UTF8.GetString(bytes));
            var meta = BuildCaptionResource(videoId, lang, lang == "ko" ? "한국어" : "English");
            for (var auth = 0; auth < 2; auth++)
            {
                var token = await GetAccessTokenAsync(creds, force: auth > 0, ct).ConfigureAwait(false);
                if (token is null) return false;
                var (st, _, _) = await SendAsync(() =>
                {
                    var mp = new MultipartContent("related");
                    var json = new StringContent(meta, Encoding.UTF8, "application/json");
                    var file = new ByteArrayContent(bytes);
                    file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    mp.Add(json);
                    mp.Add(file);
                    var r = new HttpRequestMessage(HttpMethod.Post, CaptionEndpoint) { Content = mp };
                    r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    return r;
                }, ct).ConfigureAwait(false);
                if (st == 401) { _token = null; continue; }
                return st is >= 200 and < 300;
            }
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Program.Log("YouTubeShorts.Caption", ex); return false; }
    }

    /// <summary>캐시 토큰(만료 60초 전까지) 재사용 · 만료/강제 시 리프레시. 실패 null.</summary>
    private async Task<string?> GetAccessTokenAsync(SnsCredentials c, bool force, CancellationToken ct)
    {
        var owner = c.YouTubeClientId + "|" + c.YouTubeRefreshToken;
        if (!force && _token is not null && owner == _tokenOwner && DateTime.UtcNow < _tokenExpiresUtc) return _token;
        var form = new Dictionary<string, string>(4)
        {
            ["client_id"] = c.YouTubeClientId,
            ["client_secret"] = c.YouTubeClientSecret,
            ["refresh_token"] = c.YouTubeRefreshToken,
            ["grant_type"] = "refresh_token",
        };
        var (st, body, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, TokenEndpoint) { Content = new FormUrlEncodedContent(form) }, ct).ConfigureAwait(false);
        if (st != 200) return null;
        var token = ReadString(body, "access_token");
        if (token is null) return null;
        var ttl = 3600;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("expires_in", out var e) && e.ValueKind == JsonValueKind.Number) ttl = e.GetInt32();
        }
        catch (JsonException) { }
        _token = token;
        _tokenOwner = owner;
        _tokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(0, ttl - 60));
        return token;
    }

    /// <summary>제어 요청 — 30초 타임아웃 · 429/5xx 지수 백오프(Retry-After 우선) · 예외는 상태코드로 환원.</summary>
    private async Task<(int Status, string Body, string? Location)> SendAsync(Func<HttpRequestMessage> factory, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RequestTimeout);
            int status;
            string body;
            string? location = null;
            TimeSpan? retryAfter = null;
            try
            {
                using var req = factory();
                using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                status = (int)resp.StatusCode;
                body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                location = resp.Headers.Location?.ToString();
                retryAfter = resp.Headers.RetryAfter?.Delta;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = 504; body = ""; }
            catch (HttpRequestException) { status = 503; body = ""; }

            if (status is not (429 or 500 or 502 or 503 or 504) || attempt >= MaxAttempts) return (status, body, location);
            var delay = retryAfter ?? TimeSpan.FromSeconds(BackoffBaseSeconds * (1 << (attempt - 1)));
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    private SnsCredentials SafeCreds()
    {
        try { return _creds() ?? SnsCredentials.Empty; }
        catch (Exception ex) { Program.Log("YouTubeShorts.Creds", ex); return SnsCredentials.Empty; }
    }

    private static SnsCredentials FromConfig(IConfiguration? cfg) => new(
        (cfg?["YouTube:ClientId"] ?? "").Trim(), (cfg?["YouTube:ClientSecret"] ?? "").Trim(), (cfg?["YouTube:RefreshToken"] ?? "").Trim(), "", "", "");

    private static string? ReadString(string body, string name)
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

    private static string Trunc(string? s, int n)
    {
        s = (s ?? "").Trim();
        return s.Length <= n ? s : s[..n];
    }

    public void Dispose() => _http.Dispose();

    /// <summary>스트리밍 업로드 진행률(0~0.95) — 파일→네트워크 펌프, 1.0 은 완료 확인 후.</summary>
    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _src;
        private readonly long _total;
        private readonly int _buf;
        private readonly IProgress<double>? _progress;
        private readonly CancellationToken _ct;

        public ProgressStreamContent(Stream src, long total, int buf, IProgress<double>? progress, CancellationToken ct)
        { _src = src; _total = total; _buf = buf; _progress = progress; _ct = ct; }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buffer = new byte[_buf];
            long done = 0;
            int lastPct = -1, read;
            while ((read = await _src.ReadAsync(buffer.AsMemory(0, _buf), _ct).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), _ct).ConfigureAwait(false);
                done += read;
                if (_progress is null || _total <= 0) continue;
                var pct = (int)(done * 100L / _total);
                if (pct != lastPct) { lastPct = pct; _progress.Report(Math.Min(0.95, pct / 100.0)); }
            }
        }

        protected override bool TryComputeLength(out long length) { length = _total; return true; }
    }
}
