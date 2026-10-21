using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using PromptFlowStudio.Services.GraphBridge;

namespace PromptFlowStudio.Services.LivePublish;

public enum YouTubeUploadPhase : byte { Idle, InitSession, Uploading, QueryOffset, UpdatingPrivacy, Completed, Failed, Canceled }
public enum PlatformRejection : byte { None, Copyright, Metadata, Quota, Auth, Duplicate, Forbidden, Processing, Instagram, Unknown }

/// <summary>
/// [PromptFlow Studio v17.79.0] 📤 프로덕션 이어올리기(Resumable) 숏폼 업로더(<see cref="Step"/> 무할당 상태 기계 · in-flight 1 슬롯).
/// <b>YouTube</b>: ① <c>POST upload/youtube/v3/videos?uploadType=resumable&amp;part=snippet,status</c>(메타데이터 JSON · <c>X-Upload-Content-Length/Type</c>)
/// → <c>Location</c> 세션 URI(접두사 스키마 즉시 검증). ② 256 KB 배수(256 KB~1 MB) 청크를 고정 버퍼로 읽어 <c>PUT</c> + <c>Content-Range: bytes a-b/N</c>.
/// <b>308 Resume Incomplete</b> 는 서버 <c>Range: bytes=0-k</c> 를 권위값으로 채택(부분 수신분부터 재개). ③ 연결 단절/5xx/429 는 지수 백오프 후
/// <c>Content-Range: bytes */N</c> 로 마지막 확정 오프셋 조회 → 그 바이트부터 재전송. 404(세션 만료)는 1 회 세션 재생성. ④ 완료 응답의
/// <c>status.uploadStatus=rejected/failed</c> · <c>rejectionReason</c>(copyright 등) · 4xx <c>errors[].reason</c> 을 <see cref="PlatformRejection"/> 으로 분류.
/// ⑤ <c>PUT youtube/v3/videos?part=status</c> 공개 범위 승격/롤백.
/// <b>Instagram Reels</b>: v17.48 <see cref="ChunkedReelsPublishingDispatcherSynthesizer"/>(컨테이너 → rupload 바이너리 → status_code 폴링 → media_publish)를
/// 볼트 토큰으로 재암호화한 <see cref="TokenBucketRateLimitOAuthSecurityGuard"/> 와 함께 구동.
/// </summary>
public sealed class ProductionResumableVideoUploaderEngine : IDisposable
{
    public const int MinChunk = 256 * 1024, MaxChunk = 1024 * 1024, MetaCap = 8192, IdCap = 32, MaxRetries = 8;
    public const double BackoffBaseSec = 1, BackoffMaxSec = 64, RateEma = 0.3;
    public static ReadOnlySpan<byte> InitUrl => "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status"u8;
    public static ReadOnlySpan<byte> SessionPrefix => "https://www.googleapis.com/upload/youtube/v3/videos"u8;
    public static ReadOnlySpan<byte> VideosUrl => "https://www.googleapis.com/youtube/v3/videos?part=status"u8;

    private readonly ProductionOAuth2TokenVaultGateway _vault;
    private readonly byte[] _chunk = new byte[MaxChunk], _meta = new byte[MetaCap], _session = new byte[GraphResponseBuffer.LocationCap], _videoId = new byte[IdCap], _upd = new byte[256];
    private readonly GraphCall _call = new();
    private Stream? _src;
    private int _chunkBytes = MinChunk, _sessionLen, _videoIdLen, _chunkLen, _metaLen, _updLen, _retries;
    private long _chunkT0;
    private double _nextAt;
    private bool _sessionRestarted;
    private uint _rng = 0x68E31DA4u;
    private string? _videoIdText;
    private CancellationTokenSource? _cts;
    private ReelsPublishPhase _lastReels;

    public ProductionResumableVideoUploaderEngine(ProductionOAuth2TokenVaultGateway vault) => _vault = vault;

    public ChunkedReelsPublishingDispatcherSynthesizer Reels { get; } = new();
    public TokenBucketRateLimitOAuthSecurityGuard ReelsGuard { get; } = new();
    public YouTubeUploadPhase Phase { get; private set; }
    /// <summary>청크 크기 — 256 KB 배수로 내림 · 256 KB~1 MB 클램프(YouTube 규격).</summary>
    public int ChunkBytes { get => _chunkBytes; set => _chunkBytes = Math.Clamp(value / MinChunk * MinChunk, MinChunk, MaxChunk); }
    /// <summary>Step 1 회당 최대 요청 연쇄(동기 전송) — UI 데모 진행 표시용 감속.</summary>
    public int MaxHopsPerStep { get; set; } = 4;
    public long FileSize { get; private set; }
    /// <summary>서버 확정 오프셋(다음 전송 시작 바이트).</summary>
    public long Offset { get; private set; }
    public long BytesSent { get; private set; }
    public int ChunksSent { get; private set; }
    public int ChunkRetries { get; private set; }
    public int Resumes { get; private set; }
    public int PartialResumes { get; private set; }
    public int SessionRestarts { get; private set; }
    public int LastStatus { get; private set; }
    public double BytesPerSec { get; private set; }
    public double LastStepUs { get; private set; }
    public double StartedAt { get; private set; }
    public double FinishedAt { get; private set; }
    public string? LastError { get; private set; }
    public PlatformRejection Rejection { get; private set; }
    public string RequestedPrivacy { get; private set; } = "—";
    public string ConfirmedPrivacy { get; private set; } = "—";
    public double Progress => FileSize > 0 ? (double)Offset / FileSize : 0;
    public int TotalChunks => FileSize > 0 ? (int)((FileSize + _chunkBytes - 1) / _chunkBytes) : 0;
    public bool IsActive => Phase is YouTubeUploadPhase.InitSession or YouTubeUploadPhase.Uploading or YouTubeUploadPhase.QueryOffset or YouTubeUploadPhase.UpdatingPrivacy;
    public bool HasVideo => _videoIdLen > 0;
    public ReadOnlySpan<byte> VideoIdSpan => _videoId.AsSpan(0, _videoIdLen);
    public string VideoId => _videoIdLen == 0 ? "—" : _videoIdText ??= System.Text.Encoding.ASCII.GetString(_videoId, 0, _videoIdLen);
    public long ReelsPublished { get; private set; }
    public long ReelsFailed { get; private set; }

    public static string PhaseName(YouTubeUploadPhase p) => p switch
    {
        YouTubeUploadPhase.InitSession => "세션 생성",
        YouTubeUploadPhase.Uploading => "청크 전송",
        YouTubeUploadPhase.QueryOffset => "오프셋 조회(재개)",
        YouTubeUploadPhase.UpdatingPrivacy => "공개 범위 갱신",
        YouTubeUploadPhase.Completed => "완료",
        YouTubeUploadPhase.Failed => "실패",
        YouTubeUploadPhase.Canceled => "중단",
        _ => "대기",
    };

    public static string RejectionName(PlatformRejection r) => r switch
    {
        PlatformRejection.Copyright => "저작권 차단",
        PlatformRejection.Metadata => "메타데이터 거부",
        PlatformRejection.Quota => "할당량 초과",
        PlatformRejection.Auth => "인증 만료",
        PlatformRejection.Duplicate => "중복 업로드",
        PlatformRejection.Forbidden => "정책 위반/권한 없음",
        PlatformRejection.Processing => "처리 실패",
        PlatformRejection.Instagram => "Instagram 발행 실패",
        PlatformRejection.Unknown => "알 수 없는 거부",
        _ => "없음",
    };

    // ══════════════════════ YouTube 시작(비핫패스) ══════════════════════
    /// <summary>업로드 시작 — 메타데이터 JSON 조립(할당 허용) · 실패 시 false + <see cref="LastError"/>.</summary>
    public bool BeginYouTube(Stream src, string title, string description, string privacy, double now, string? tags = null)
    {
        if (IsActive) { LastError = "YouTube 업로드 진행 중"; return false; }
        if (!_vault.Account(LiveProvider.YouTube).IsConnected) { LastError = "YouTube 계정 미연결/토큰 만료"; Rejection = PlatformRejection.Auth; return false; }
        if (privacy is not ("private" or "unlisted" or "public")) { LastError = "공개 범위 값 오류"; return false; }
        if (!src.CanSeek || src.Length <= 0) { LastError = "탐색 가능한 비어있지 않은 스트림 필요"; return false; }
        try
        {
            using var ms = new MemoryStream(_meta, true);
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteStartObject("snippet");
                w.WriteString("title", title.Length > 100 ? title[..100] : title);
                w.WriteString("description", description.Length > 4900 ? description[..4900] : description);
                w.WriteString("categoryId", "22");
                if (!string.IsNullOrWhiteSpace(tags))
                {
                    w.WriteStartArray("tags");
                    foreach (var t in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) w.WriteStringValue(t);
                    w.WriteEndArray();
                }
                w.WriteEndObject();
                w.WriteStartObject("status");
                w.WriteString("privacyStatus", privacy);
                w.WriteBoolean("selfDeclaredMadeForKids", false);
                w.WriteBoolean("embeddable", true);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            _metaLen = (int)ms.Position;
        }
        catch (NotSupportedException) { LastError = "메타데이터 8 KB 초과"; return false; }
        _src = src; FileSize = src.Length;
        Offset = 0; BytesSent = 0; ChunksSent = 0; ChunkRetries = 0; Resumes = 0; PartialResumes = 0; SessionRestarts = 0; _retries = 0;
        _sessionLen = 0; _videoIdLen = 0; _videoIdText = null; _sessionRestarted = false; BytesPerSec = 0;
        Rejection = PlatformRejection.None; LastError = null; RequestedPrivacy = privacy; ConfirmedPrivacy = "—";
        StartedAt = now; FinishedAt = 0; _nextAt = now;
        ResetCts();
        Phase = YouTubeUploadPhase.InitSession;
        return true;
    }

    /// <summary>업로드 완료 영상의 공개 범위 변경(승격/롤백) — 무할당.</summary>
    public bool BeginPrivacyUpdate(string privacy, double now)
    {
        if (IsActive || _videoIdLen == 0) { LastError = "갱신 대상 영상 없음/진행 중"; return false; }
        var n = 0; var d = _upd.AsSpan();
        GraphBytes.Put(d, ref n, "{\"id\":\""u8); GraphBytes.Put(d, ref n, _videoId.AsSpan(0, _videoIdLen)); GraphBytes.Put(d, ref n, "\",\"status\":{\"privacyStatus\":\""u8);
        GraphBytes.Put(d, ref n, privacy == "public" ? "public"u8 : privacy == "unlisted" ? "unlisted"u8 : "private"u8);
        GraphBytes.Put(d, ref n, "\",\"selfDeclaredMadeForKids\":false}}"u8);
        _updLen = n; _retries = 0; _nextAt = now;
        RequestedPrivacy = privacy == "public" ? "public" : privacy == "unlisted" ? "unlisted" : "private";
        ResetCts();
        Phase = YouTubeUploadPhase.UpdatingPrivacy;
        return true;
    }

    // ══════════════════════ Instagram 시작(비핫패스) ══════════════════════
    public bool BeginInstagram(Stream src, in ReelsVideoSpec spec, string caption, bool shareToFeed, double now)
    {
        var a = _vault.Account(LiveProvider.Instagram);
        if (!a.IsConnected || a.IgUserId == 0) { LastError = "Instagram 비즈니스 계정 미연결"; return false; }
        Span<byte> tok = stackalloc byte[GraphSecretSlot.Cap]; Span<char> chars = stackalloc char[GraphSecretSlot.Cap];
        try
        {
            var l = a.Access.CopyTo(tok);
            var c = System.Text.Encoding.ASCII.GetChars(tok[..l], chars);
            ReelsGuard.SetToken(chars[..c], now, a.ExpiresAt - now);
        }
        finally { CryptographicOperations.ZeroMemory(tok); chars.Clear(); }
        _lastReels = ReelsPublishPhase.Idle;
        if (Reels.Begin(src, in spec, caption, shareToFeed, now, ReelsGuard)) return true;
        LastError = Reels.LastError;
        return false;
    }

    public void CancelAll()
    {
        if (IsActive) { _cts?.Cancel(); Phase = YouTubeUploadPhase.Canceled; LastError ??= "배포 중단"; }
        Reels.Cancel();
    }

    private void ResetCts()
    {
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old?.Cancel(); old?.Dispose();
    }

    // ══════════════════════ 틱(무할당) ══════════════════════
    /// <summary>YouTube 상태 기계 + 릴스 디스패처 1 틱 — 완료 수확 → 판정 → 다음 요청(동기 전송은 연쇄 최대 <see cref="MaxHopsPerStep"/>).</summary>
    public void Step(double now)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var hop = 0; hop < MaxHopsPerStep * 2; hop++)
        {
            if (_call.InFlight)
            {
                if (!_call.TryComplete(out var st)) break;
                Handle(st, now);
                continue;
            }
            if (!IsActive || now < _nextAt || hop >= MaxHopsPerStep * 2 - 1) break;
            if (!Issue(now)) break;
        }
        if (Reels.IsActive || Reels.InFlight)
        {
            Reels.Step(now, _vault.Account(LiveProvider.Instagram).IgUserId, _vault.TransportFor(LiveProvider.Instagram), ReelsGuard);
        }
        if (Reels.Phase != _lastReels)
        {
            if (Reels.Phase == ReelsPublishPhase.Published) ReelsPublished++;
            else if (Reels.Phase == ReelsPublishPhase.Failed) ReelsFailed++;
            _lastReels = Reels.Phase;
        }
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private bool Issue(double now)
    {
        var url = _call.Url.AsSpan(); var hdr = _call.Hdr.AsSpan();
        var h = _vault.WriteBearer(LiveProvider.YouTube, hdr);
        if (h == 0) { Fail("YouTube 토큰 없음", PlatformRejection.Auth, now); return false; }
        var n = 0;
        switch (Phase)
        {
            case YouTubeUploadPhase.InitSession:
                GraphBytes.Put(url, ref n, InitUrl);
                GraphBytes.Put(hdr, ref h, "Content-Type: application/json; charset=UTF-8\r\nX-Upload-Content-Length: "u8); GraphBytes.Put(hdr, ref h, FileSize);
                GraphBytes.Put(hdr, ref h, "\r\nX-Upload-Content-Type: video/mp4\r\n"u8);
                _call.Method = GraphHttpMethod.Post; _call.Body = _meta.AsMemory(0, _metaLen);
                break;
            case YouTubeUploadPhase.Uploading:
                GraphBytes.Put(url, ref n, _session.AsSpan(0, _sessionLen));
                var want = (int)Math.Min(_chunkBytes, FileSize - Offset);
                _src!.Position = Offset;
                _chunkLen = _src.ReadAtLeast(_chunk.AsSpan(0, want), want, throwOnEndOfStream: false);
                if (_chunkLen != want) { Fail("원본 스트림 길이 변경", PlatformRejection.Unknown, now); return false; }
                GraphBytes.Put(hdr, ref h, "Content-Range: bytes "u8); GraphBytes.Put(hdr, ref h, Offset); hdr[h++] = (byte)'-'; GraphBytes.Put(hdr, ref h, Offset + _chunkLen - 1);
                hdr[h++] = (byte)'/'; GraphBytes.Put(hdr, ref h, FileSize);
                GraphBytes.Put(hdr, ref h, "\r\nContent-Type: video/mp4\r\n"u8);
                _call.Method = GraphHttpMethod.Put; _call.Body = _chunk.AsMemory(0, _chunkLen);
                _chunkT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                break;
            case YouTubeUploadPhase.QueryOffset:
                GraphBytes.Put(url, ref n, _session.AsSpan(0, _sessionLen));
                GraphBytes.Put(hdr, ref h, "Content-Range: bytes */"u8); GraphBytes.Put(hdr, ref h, FileSize); GraphBytes.Put(hdr, ref h, "\r\n"u8);
                _call.Method = GraphHttpMethod.Put; _call.Body = default;
                break;
            default:
                GraphBytes.Put(url, ref n, VideosUrl);
                GraphBytes.Put(hdr, ref h, "Content-Type: application/json; charset=UTF-8\r\n"u8);
                _call.Method = GraphHttpMethod.Put; _call.Body = _upd.AsMemory(0, _updLen);
                break;
        }
        _call.UrlLen = n; _call.HdrLen = h;
        _call.Send(_vault.TransportFor(LiveProvider.YouTube), _cts?.Token ?? default);
        return true;
    }

    private void Handle(int status, double now)
    {
        LastStatus = status;
        if (!IsActive) return;
        var resp = _call.Resp;
        if (status == 0 || status == 429 || status >= 500)
        {
            if (++_retries > MaxRetries) { Fail("재시도 한도 초과(네트워크/서버)", PlatformRejection.Unknown, now); return; }
            if (Phase == YouTubeUploadPhase.Uploading) { ChunkRetries++; Resumes++; Phase = YouTubeUploadPhase.QueryOffset; }
            _nextAt = now + Math.Max(resp.RetryAfterSec, Backoff(_retries));
            return;
        }
        if (status == 308 && Phase is YouTubeUploadPhase.Uploading or YouTubeUploadPhase.QueryOffset)
        {
            var confirmed = ParseRangeEnd(resp.RangeSpan) + 1;
            if (Phase == YouTubeUploadPhase.Uploading)
            {
                BytesSent += _chunkLen;
                if (confirmed > Offset) ChunksSent++;
                var dt = (System.Diagnostics.Stopwatch.GetTimestamp() - _chunkT0) / (double)System.Diagnostics.Stopwatch.Frequency;
                if (dt > 0) { var r = _chunkLen / dt; BytesPerSec = BytesPerSec <= 0 ? r : BytesPerSec + RateEma * (r - BytesPerSec); }
                _retries = 0;
            }
            else
            {
                if (confirmed % MinChunk != 0) PartialResumes++;
                Phase = YouTubeUploadPhase.Uploading;
            }
            Offset = Math.Clamp(confirmed, 0, FileSize);
            return;
        }
        if (status is 200 or 201)
        {
            _retries = 0;
            switch (Phase)
            {
                case YouTubeUploadPhase.InitSession:
                    var loc = resp.LocationSpan;
                    if (!loc.StartsWith(SessionPrefix) || loc.Length > _session.Length) { Fail("세션 URI 스키마 불일치", PlatformRejection.Unknown, now); return; }
                    loc.CopyTo(_session); _sessionLen = loc.Length;
                    Phase = YouTubeUploadPhase.Uploading;
                    return;
                case YouTubeUploadPhase.UpdatingPrivacy:
                    ReadVideo(resp.BodySpan, out _, out var pv);
                    ConfirmedPrivacy = pv == 3 ? "public" : pv == 2 ? "unlisted" : pv == 1 ? "private" : "—";
                    if (pv == 0) { Fail("공개 범위 응답 스키마 불일치", PlatformRejection.Unknown, now); return; }
                    Phase = YouTubeUploadPhase.Completed; FinishedAt = now;
                    return;
                default:
                    if (Phase == YouTubeUploadPhase.Uploading) { BytesSent += _chunkLen; ChunksSent++; }
                    var rej = ReadVideo(resp.BodySpan, out var idOk, out var pv2);
                    if (!idOk) { Fail("업로드 응답 스키마 불일치(id 없음)", PlatformRejection.Unknown, now); return; }
                    Offset = FileSize;
                    ConfirmedPrivacy = pv2 == 3 ? "public" : pv2 == 2 ? "unlisted" : "private";
                    if (rej != PlatformRejection.None) { Fail("플랫폼 거부(uploadStatus=rejected/failed)", rej, now); return; }
                    Phase = YouTubeUploadPhase.Completed; FinishedAt = now;
                    return;
            }
        }
        if (status == 404 && Phase is YouTubeUploadPhase.Uploading or YouTubeUploadPhase.QueryOffset && !_sessionRestarted)
        {
            _sessionRestarted = true; SessionRestarts++;
            Offset = 0; _sessionLen = 0; Phase = YouTubeUploadPhase.InitSession; _nextAt = now;
            return;
        }
        var reason = status == 401 ? PlatformRejection.Auth : ReadErrorReason(resp.BodySpan);
        Fail(status == 401 ? "토큰 만료/무효(401)" : status == 403 ? "요청 거부(403)" : status == 404 ? "대상 없음(404)" : "요청 거부(4xx)", reason, now);
    }

    private void Fail(string why, PlatformRejection r, double now)
    {
        LastError = why; Rejection = r == PlatformRejection.None ? PlatformRejection.Unknown : r;
        Phase = YouTubeUploadPhase.Failed; FinishedAt = now;
    }

    private double Backoff(int n)
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
        return Math.Min(BackoffBaseSec * Math.Pow(2, Math.Max(n - 1, 0)), BackoffMaxSec) * (0.75 + 0.25 * (_rng / (double)uint.MaxValue));
    }

    /// <summary><c>bytes=0-k</c> → k (없으면 −1) — 무할당.</summary>
    public static long ParseRangeEnd(ReadOnlySpan<byte> range)
    {
        var d = range.LastIndexOf((byte)'-');
        return d >= 0 && Utf8Parser.TryParse(range[(d + 1)..], out long k, out var used) && used == range.Length - d - 1 ? k : -1;
    }

    /// <summary>video 리소스 판독(무할당) — id 저장 · 공개 범위 코드 · 거부 분류.</summary>
    private PlatformRejection ReadVideo(ReadOnlySpan<byte> json, out bool idOk, out int privacy)
    {
        idOk = false; privacy = 0;
        var rej = PlatformRejection.None;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.CurrentDepth == 1 && r.ValueTextEquals("id"u8))
                {
                    if (r.Read() && r.TokenType == JsonTokenType.String && !r.ValueIsEscaped && r.ValueSpan.Length is > 0 and <= IdCap)
                    {
                        if (!r.ValueSpan.SequenceEqual(_videoId.AsSpan(0, _videoIdLen))) { r.ValueSpan.CopyTo(_videoId); _videoIdLen = r.ValueSpan.Length; _videoIdText = null; }
                        idOk = true;
                    }
                }
                else if (r.CurrentDepth == 2 && r.ValueTextEquals("privacyStatus"u8) && r.Read())
                    privacy = r.ValueTextEquals("private"u8) ? 1 : r.ValueTextEquals("unlisted"u8) ? 2 : r.ValueTextEquals("public"u8) ? 3 : 0;
                else if (r.CurrentDepth == 2 && r.ValueTextEquals("uploadStatus"u8) && r.Read())
                {
                    if (r.ValueTextEquals("rejected"u8) && rej == PlatformRejection.None) rej = PlatformRejection.Unknown;
                    else if (r.ValueTextEquals("failed"u8) && rej == PlatformRejection.None) rej = PlatformRejection.Processing;
                }
                else if (r.CurrentDepth == 2 && r.ValueTextEquals("rejectionReason"u8) && r.Read())
                    rej = r.ValueTextEquals("copyright"u8) ? PlatformRejection.Copyright : r.ValueTextEquals("duplicate"u8) ? PlatformRejection.Duplicate : PlatformRejection.Forbidden;
                else if (r.CurrentDepth == 2 && r.ValueTextEquals("failureReason"u8) && r.Read()) rej = PlatformRejection.Processing;
            }
        }
        catch (JsonException) { idOk = false; }
        return rej;
    }

    /// <summary>Google 오류 errors[].reason → 분류(무할당).</summary>
    public static PlatformRejection ReadErrorReason(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName || !r.ValueTextEquals("reason"u8) || !r.Read() || r.TokenType != JsonTokenType.String) continue;
                if (r.ValueTextEquals("quotaExceeded"u8) || r.ValueTextEquals("uploadLimitExceeded"u8) || r.ValueTextEquals("rateLimitExceeded"u8) || r.ValueTextEquals("userRateLimitExceeded"u8)) return PlatformRejection.Quota;
                if (r.ValueTextEquals("authError"u8) || r.ValueTextEquals("youtubeSignupRequired"u8)) return PlatformRejection.Auth;
                if (r.ValueTextEquals("forbidden"u8) || r.ValueTextEquals("insufficientPermissions"u8)) return PlatformRejection.Forbidden;
                var v = r.ValueSpan;
                if (v.StartsWith("invalid"u8) || GraphBytes.Contains(v, "Metadata"u8) || GraphBytes.Contains(v, "Title"u8) || GraphBytes.Contains(v, "Description"u8)) return PlatformRejection.Metadata;
                return PlatformRejection.Unknown;
            }
        }
        catch (JsonException) { }
        return PlatformRejection.Unknown;
    }

    public void Dispose()
    {
        CancelAll();
        _cts?.Dispose(); _cts = null;
        ReelsGuard.Dispose();
    }
}
