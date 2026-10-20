using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio.Services.Wan;

/// <summary>유튜브 업로드 결과. Ok=true 면 성공(VideoId/Url). 실패는 Message.</summary>
public sealed record YouTubeUploadResult(bool Ok, string? VideoId, string? Url, string Message);

/// <summary>유튜브 자동 퍼블리싱(Data API v3 · REST) 계약.</summary>
public interface IYouTubePublishService
{
    /// <summary>OAuth 자격(ClientId/Secret/RefreshToken)이 구성돼 업로드 가능한지.</summary>
    bool Enabled { get; }

    /// <summary>
    /// 완성 영상(.mp4)과 메타데이터를 YouTube Data API v3(resumable upload)로 업로드한다.
    ///   ⚠ 공개 발행은 되돌리기 어려운 외부 작업 — 기본 <paramref name="privacyStatus"/>="private"(안전). 예약 발행은 <paramref name="publishAtUtc"/>(private 필수).
    ///   키/자격 누락·실패는 Graceful(Ok=false).
    /// </summary>
    Task<YouTubeUploadResult> UploadAsync(
        string videoPath, string title, string description, IReadOnlyList<string> tags,
        string privacyStatus = "private", DateTime? publishAtUtc = null, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// [PromptFlowStudio] 유튜브 자동 퍼블리싱 — 순수 BCL(HttpClient + System.Text.Json, 외부 NuGet 0).
///   ▸ OAuth2 리프레시 토큰으로 액세스 토큰을 발급(<c>oauth2.googleapis.com/token</c>)한 뒤,
///     <b>resumable upload</b>(2단계: 메타데이터 POST → Location 에 바이트 PUT)로 영상을 업로드(대용량 스트리밍).
///   ▸ 자격(appsettings <c>YouTube:ClientId/ClientSecret/RefreshToken</c>)이 없으면 <see cref="Enabled"/>=false → 업로드 기능만 비활성(로컬 파이프라인 정상).
///   ▸ <b>안전</b>: 기본 privacyStatus="private"(사용자가 명시적으로 public/예약 선택). 사용자 자신의 채널 자격으로만 동작(에이전시가 임의 발행하지 않음).
/// </summary>
public sealed class YouTubePublishService(IConfiguration cfg) : IYouTubePublishService
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };   // 대용량 업로드 — 취소는 ct 담당
    private const int BufferSize = 262144;

    private string ClientId => (cfg["YouTube:ClientId"] ?? "").Trim();
    private string ClientSecret => (cfg["YouTube:ClientSecret"] ?? "").Trim();
    private string RefreshToken => (cfg["YouTube:RefreshToken"] ?? "").Trim();
    private string CategoryId => string.IsNullOrWhiteSpace(cfg["YouTube:CategoryId"]) ? "22" : cfg["YouTube:CategoryId"]!.Trim();

    /// <inheritdoc/>
    public bool Enabled => ClientId.Length > 0 && ClientSecret.Length > 0 && RefreshToken.Length > 0;

    /// <inheritdoc/>
    public async Task<YouTubeUploadResult> UploadAsync(
        string videoPath, string title, string description, IReadOnlyList<string> tags,
        string privacyStatus = "private", DateTime? publishAtUtc = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Enabled) return new(false, null, null, "YouTube 자격(ClientId/Secret/RefreshToken)이 없습니다.");
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return new(false, null, null, "업로드할 영상 파일이 없습니다.");

        try
        {
            // ① 액세스 토큰 발급(리프레시).
            var token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (token is null) return new(false, null, null, "액세스 토큰 발급 실패(자격/네트워크 확인).");

            // 예약 발행이면 반드시 private.
            var privacy = string.IsNullOrWhiteSpace(privacyStatus) ? "private" : privacyStatus.Trim().ToLowerInvariant();
            if (publishAtUtc is not null) privacy = "private";
            if (privacy is not ("private" or "public" or "unlisted")) privacy = "private";

            // ② 메타데이터 POST → 업로드 세션(Location) 획득.
            var status = new JsonObject { ["privacyStatus"] = privacy, ["selfDeclaredMadeForKids"] = false };
            if (publishAtUtc is not null) status["publishAt"] = publishAtUtc.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
            var snippet = new JsonObject
            {
                ["title"] = Trunc(title, 100),
                ["description"] = Trunc(description, 5000),
                ["categoryId"] = CategoryId,
                ["tags"] = new JsonArray((tags ?? Array.Empty<string>()).Take(30).Select(t => (JsonNode)t).ToArray()),
            };
            var meta = new JsonObject { ["snippet"] = snippet, ["status"] = status };

            var fileLen = new FileInfo(videoPath).Length;
            using var initReq = new HttpRequestMessage(HttpMethod.Post,
                "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status");
            initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            initReq.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "video/*");
            initReq.Headers.TryAddWithoutValidation("X-Upload-Content-Length", fileLen.ToString());
            initReq.Content = new StringContent(meta.ToJsonString(), Encoding.UTF8, "application/json");

            using var initResp = await Http.SendAsync(initReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!initResp.IsSuccessStatusCode)
                return new(false, null, null, $"업로드 세션 생성 실패(HTTP {(int)initResp.StatusCode}).");
            var location = initResp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location)) return new(false, null, null, "업로드 세션 URL 을 받지 못했습니다.");

            // ③ 바이트 PUT(스트리밍 · 진행률).
            await using var fs = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
            using var putReq = new HttpRequestMessage(HttpMethod.Put, location)
            {
                Content = new ProgressStreamContent(fs, fileLen, BufferSize, progress, ct),
            };
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("video/*");
            putReq.Content.Headers.ContentLength = fileLen;

            using var putResp = await Http.SendAsync(putReq, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            var body = await putResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!putResp.IsSuccessStatusCode) return new(false, null, null, $"업로드 실패(HTTP {(int)putResp.StatusCode}).");

            var id = ExtractId(body);
            progress?.Report(100);
            return id is null
                ? new(false, null, null, "업로드는 됐으나 영상 ID 를 확인하지 못했습니다.")
                : new(true, id, $"https://youtu.be/{id}", "업로드 완료");
        }
        catch (OperationCanceledException) { return new(false, null, null, "업로드가 취소되었습니다."); }
        catch (Exception ex) { Program.Log("YouTubePublish.Upload", ex); return new(false, null, null, "업로드 실패: " + ex.Message); }
    }

    /// <summary>리프레시 토큰으로 액세스 토큰 발급. 실패 시 null.</summary>
    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        try
        {
            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = RefreshToken,
                ["grant_type"] = "refresh_token",
            });
            using var resp = await Http.PostAsync("https://oauth2.googleapis.com/token", form, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("access_token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch (Exception ex) { Program.Log("YouTubePublish.Token", ex); return null; }
    }

    private static string? ExtractId(string body)
    {
        try { using var doc = JsonDocument.Parse(body); return doc.RootElement.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
        catch { return null; }
    }

    private static string Trunc(string s, int n)
    {
        s = (s ?? "").Trim();
        return s.Length <= n ? s : s[..n];
    }

    /// <summary>스트리밍 업로드 진행률 보고용 HttpContent(파일→네트워크 펌프, 파일 전체를 메모리에 올리지 않음).</summary>
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
            long done = 0; int lastPct = -1, read;
            while ((read = await _src.ReadAsync(buffer.AsMemory(0, _buf), _ct).ConfigureAwait(false)) > 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), _ct).ConfigureAwait(false);
                done += read;
                if (_progress is not null && _total > 0)
                {
                    var pct = (int)(done * 100L / _total);
                    if (pct != lastPct) { lastPct = pct; _progress.Report(Math.Min(99, pct)); }   // 100 은 완료 확인 후
                }
            }
        }

        protected override bool TryComputeLength(out long length) { length = _total; return true; }
    }
}
