using System.Buffers.Text;
using System.Text.Json;
namespace PromptFlowStudio.Services.GraphBridge;

public enum ReelsPublishPhase : byte { Idle, CreateContainer, Uploading, Polling, Publishing, Published, Failed, Canceled }

/// <summary>릴스 소스 영상 규격(호출자 신고값 + MP4 시그니처 검사).</summary>
public struct ReelsVideoSpec
{
    public int Width, Height;
    public float Fps, DurationSec;
    public long Bytes;
}

/// <summary>
/// [PromptFlow Studio v17.48.0] 🔗 분할 업로드 세션 &amp; 릴스 자동 발행 디스패처 합성기(<see cref="Step"/> 60 Hz 무할당 상태 기계).
/// ① <c>POST /{ig-user-id}/media</c>(media_type=REELS · upload_type=resumable · caption · share_to_feed) → 컨테이너 ID · rupload URI.
/// ② <c>POST rupload.facebook.com/ig-api-upload/v21.0/{container}</c> — 4 MB 고정 버퍼로 청크를 읽어 <c>Authorization: OAuth</c> · <c>offset</c> · <c>file_size</c>
/// 헤더와 함께 전송, 실패 시 같은 offset 부터 재개(Resumable). ③ <c>GET /{container}?fields=status_code,status</c> 폴링(FINISHED/ERROR/EXPIRED).
/// ④ <c>POST /{ig-user-id}/media_publish?creation_id=</c>. 모든 Graph 호출은 가드 토큰 버킷/백오프를 통과해야 하며(rupload 제외),
/// 요청은 in-flight 1 슬롯 — 동기 완료 전송(시뮬레이터)은 같은 틱에 처리, 비동기 전송(HttpClient)은 이후 틱에서 완료를 수확한다.
/// 규격: 9:16(±1%) · 23~60 fps · 3~900 초 · ≤ 300 MB · MP4(ftyp) · 권장 1080×1920.
/// </summary>
public sealed class ChunkedReelsPublishingDispatcherSynthesizer
{
    public const int ChunkBytes = 4 * 1024 * 1024, FormCap = 8192, IdCap = 32, UriCap = 512, MaxRetries = 6, MaxPolls = 120;
    public const long MaxVideoBytes = 300L * 1024 * 1024;
    public static ReadOnlySpan<byte> RuploadHost => "https://rupload.facebook.com/ig-api-upload/v21.0/"u8;
    private readonly byte[] _chunk = new byte[ChunkBytes], _form = new byte[FormCap], _caption = new byte[FormCap / 3], _container = new byte[IdCap], _uri = new byte[UriCap];
    private readonly GraphCall _call = new();
    private Stream? _src;
    private int _captionLen, _containerLen, _uriLen, _chunkLen, _retries;
    private bool _shareToFeed;
    private double _nextAt;
    private CancellationTokenSource? _cts;

    public ReelsPublishPhase Phase { get; private set; }
    public double PollIntervalSec { get; set; } = 3;
    public long FileSize { get; private set; }
    public long Offset { get; private set; }
    public int ChunksSent { get; private set; }
    public int ChunkRetries { get; private set; }
    public int TotalChunks => (int)((FileSize + ChunkBytes - 1) / ChunkBytes);
    public double Progress => FileSize > 0 ? (double)Offset / FileSize : 0;
    public int Polls { get; private set; }
    public ulong MediaId { get; private set; }
    public ulong ContainerId { get; private set; }
    public string? LastError { get; private set; }
    public int LastStatus { get; private set; }
    public long Deferred { get; private set; }
    public double StartedAt { get; private set; }
    public double FinishedAt { get; private set; }
    public double LastStepUs { get; private set; }
    public bool IsActive => Phase is ReelsPublishPhase.CreateContainer or ReelsPublishPhase.Uploading or ReelsPublishPhase.Polling or ReelsPublishPhase.Publishing;
    public bool InFlight => _call.InFlight;
    public static string PhaseName(ReelsPublishPhase p) => p switch
    {
        ReelsPublishPhase.CreateContainer => "컨테이너 생성",
        ReelsPublishPhase.Uploading => "청크 업로드",
        ReelsPublishPhase.Polling => "처리 폴링",
        ReelsPublishPhase.Publishing => "게시",
        ReelsPublishPhase.Published => "게시 완료",
        ReelsPublishPhase.Failed => "실패",
        ReelsPublishPhase.Canceled => "취소",
        _ => "대기",
    };

    /// <summary>규격 검증 — 정상이면 null(리터럴 사유 · 무할당).</summary>
    public static string? Validate(in ReelsVideoSpec s, ReadOnlySpan<byte> head)
    {
        if (head.Length < 12 || !head.Slice(4, 4).SequenceEqual("ftyp"u8)) return "MP4(ftyp) 아님";
        if (s.Bytes <= 0 || s.Bytes > MaxVideoBytes) return "파일 크기 0 또는 300 MB 초과";
        if (s.Width <= 0 || s.Height <= 0 || MathF.Abs((float)s.Width / s.Height - 9f / 16f) > 0.01f * 9f / 16f) return "9:16 비율 아님";
        if (s.Fps < 23f || s.Fps > 60f) return "프레임레이트 23~60 fps 범위 밖";
        if (s.DurationSec < 3f || s.DurationSec > 900f) return "길이 3~900 초 범위 밖";
        return null;
    }

    /// <summary>발행 작업 시작(할당 허용 · 비핫패스) — 실패 시 false + <see cref="LastError"/>.</summary>
    public bool Begin(Stream src, in ReelsVideoSpec spec, string caption, bool shareToFeed, double now, TokenBucketRateLimitOAuthSecurityGuard guard)
    {
        if (IsActive) { LastError = "이미 진행 중"; return false; }
        if (!guard.Token.IsSet || guard.TokenInvalid) { LastError = "토큰 미설정/무효"; return false; }
        if (!guard.CanPublish(now)) { LastError = "24시간 발행 한도(50) 소진"; return false; }
        Span<byte> head = stackalloc byte[12];
        src.Position = 0;
        var hn = src.ReadAtLeast(head, 12, throwOnEndOfStream: false);
        if (Validate(in spec, head[..hn]) is { } err) { LastError = err; Phase = ReelsPublishPhase.Failed; return false; }
        if (spec.Bytes != src.Length) { LastError = "신고 크기와 스트림 길이 불일치"; Phase = ReelsPublishPhase.Failed; return false; }
        if (System.Text.Encoding.UTF8.GetByteCount(caption) > _caption.Length) { LastError = "캡션 과다"; Phase = ReelsPublishPhase.Failed; return false; }
        _captionLen = System.Text.Encoding.UTF8.GetBytes(caption, _caption);
        _src = src; _shareToFeed = shareToFeed;
        FileSize = spec.Bytes; Offset = 0; ChunksSent = 0; ChunkRetries = 0; Polls = 0; MediaId = 0; ContainerId = 0; _containerLen = 0; _uriLen = 0; _retries = 0; Deferred = 0;
        LastError = null; StartedAt = now; FinishedAt = 0; _nextAt = now;
        var old = Interlocked.Exchange(ref _cts, new CancellationTokenSource());
        old?.Cancel(); old?.Dispose();
        Phase = ReelsPublishPhase.CreateContainer;
        return true;
    }

    public void Cancel()
    {
        if (!IsActive) return;
        _cts?.Cancel();
        Phase = ReelsPublishPhase.Canceled;
        LastError = "사용자 취소";
    }

    /// <summary>틱(무할당) — 완료 수확 → 판정 → 다음 요청 발행(동기 완료 시 같은 틱에 연쇄 최대 4 회).</summary>
    public void Step(double now, ulong igUserId, IGraphTransport t, TokenBucketRateLimitOAuthSecurityGuard guard)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (var hop = 0; hop < 4; hop++)
        {
            if (_call.InFlight)
            {
                if (!_call.TryComplete(out var st)) break;
                Handle(st, now, guard);
                continue;
            }
            if (!IsActive || now < _nextAt) break;
            if (!Issue(now, igUserId, t, guard)) break;
        }
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private bool Issue(double now, ulong uid, IGraphTransport t, TokenBucketRateLimitOAuthSecurityGuard guard)
    {
        var upload = Phase == ReelsPublishPhase.Uploading;
        if (guard.IsBlocked(now)) return false;
        if (!upload && !guard.TryAcquire(now)) { Deferred++; return false; }
        var url = _call.Url.AsSpan(); var hdr = _call.Hdr.AsSpan();
        int n = 0, h = guard.WriteAuthHeader(hdr, upload);
        switch (Phase)
        {
            case ReelsPublishPhase.CreateContainer:
                GraphBytes.Put(url, ref n, RealtimeMetaGraphApiIngestionEngine.GraphHost); GraphBytes.Put(url, ref n, uid); GraphBytes.Put(url, ref n, "/media"u8);
                var f = 0;
                GraphBytes.Put(_form, ref f, "media_type=REELS&upload_type=resumable&share_to_feed="u8); GraphBytes.Put(_form, ref f, _shareToFeed ? "true"u8 : "false"u8);
                if (_captionLen > 0) { GraphBytes.Put(_form, ref f, "&caption="u8); GraphBytes.PutEncoded(_form, ref f, _caption.AsSpan(0, _captionLen)); }
                GraphBytes.Put(hdr, ref h, "Content-Type: application/x-www-form-urlencoded\r\n"u8);
                _call.Method = GraphHttpMethod.Post; _call.Body = _form.AsMemory(0, f);
                break;
            case ReelsPublishPhase.Uploading:
                if (_uriLen > 0) GraphBytes.Put(url, ref n, _uri.AsSpan(0, _uriLen));
                else { GraphBytes.Put(url, ref n, RuploadHost); GraphBytes.Put(url, ref n, _container.AsSpan(0, _containerLen)); }
                _src!.Position = Offset;
                _chunkLen = _src.ReadAtLeast(_chunk.AsSpan(0, (int)Math.Min(ChunkBytes, FileSize - Offset)), (int)Math.Min(ChunkBytes, FileSize - Offset), throwOnEndOfStream: false);
                GraphBytes.Put(hdr, ref h, "offset: "u8); GraphBytes.Put(hdr, ref h, Offset); GraphBytes.Put(hdr, ref h, "\r\nfile_size: "u8); GraphBytes.Put(hdr, ref h, FileSize);
                GraphBytes.Put(hdr, ref h, "\r\nContent-Type: application/octet-stream\r\n"u8);
                _call.Method = GraphHttpMethod.Post; _call.Body = _chunk.AsMemory(0, _chunkLen);
                break;
            case ReelsPublishPhase.Polling:
                GraphBytes.Put(url, ref n, RealtimeMetaGraphApiIngestionEngine.GraphHost); GraphBytes.Put(url, ref n, _container.AsSpan(0, _containerLen)); GraphBytes.Put(url, ref n, "?fields=status_code,status"u8);
                _call.Method = GraphHttpMethod.Get; _call.Body = default;
                break;
            default:
                GraphBytes.Put(url, ref n, RealtimeMetaGraphApiIngestionEngine.GraphHost); GraphBytes.Put(url, ref n, uid); GraphBytes.Put(url, ref n, "/media_publish?creation_id="u8); GraphBytes.Put(url, ref n, _container.AsSpan(0, _containerLen));
                _call.Method = GraphHttpMethod.Post; _call.Body = default;
                break;
        }
        _call.UrlLen = n; _call.HdrLen = h;
        _call.Send(t, _cts?.Token ?? default);
        return true;
    }

    private void Handle(int status, double now, TokenBucketRateLimitOAuthSecurityGuard guard)
    {
        LastStatus = status;
        if (Phase == ReelsPublishPhase.Canceled) return;
        var o = guard.Observe(status, now, _call.Resp);
        if (o != GraphOutcome.Ok)
        {
            if (o is GraphOutcome.RateLimited or GraphOutcome.Retry && ++_retries <= MaxRetries)
            {
                if (Phase == ReelsPublishPhase.Uploading) ChunkRetries++;
                _nextAt = guard.BlockedUntil;
                return;
            }
            Fail(o == GraphOutcome.AuthError ? "토큰 만료/무효(190)" : o == GraphOutcome.Fatal ? "요청 거부(4xx)" : "재시도 한도 초과", now);
            return;
        }
        _retries = 0;
        var body = _call.Resp.BodySpan;
        switch (Phase)
        {
            case ReelsPublishPhase.CreateContainer:
                if (!ReadContainer(body)) { Fail("컨테이너 응답 스키마 불일치", now); return; }
                Phase = ReelsPublishPhase.Uploading;
                break;
            case ReelsPublishPhase.Uploading:
                Offset += _chunkLen; ChunksSent++;
                if (Offset >= FileSize) { Phase = ReelsPublishPhase.Polling; _nextAt = now + PollIntervalSec; }
                break;
            case ReelsPublishPhase.Polling:
                Polls++;
                var code = StatusCode(body);
                if (code == 1) Phase = ReelsPublishPhase.Publishing;
                else if (code < 0) Fail("Meta 처리 오류(ERROR/EXPIRED)", now);
                else if (Polls >= MaxPolls) Fail("처리 대기 시간 초과", now);
                else _nextAt = now + PollIntervalSec;
                break;
            case ReelsPublishPhase.Publishing:
                if (!ReadId(body, out var id)) { Fail("게시 응답 스키마 불일치", now); return; }
                MediaId = id; Phase = ReelsPublishPhase.Published; FinishedAt = now;
                guard.RecordPublish(now);
                break;
        }
    }

    private void Fail(string why, double now) { LastError = why; Phase = ReelsPublishPhase.Failed; FinishedAt = now; }

    private bool ReadContainer(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != 1) continue;
                if (r.ValueTextEquals("id"u8))
                {
                    if (!r.Read() || r.TokenType != JsonTokenType.String || r.ValueSpan.Length > IdCap || !Utf8Parser.TryParse(r.ValueSpan, out ulong cid, out var used) || used != r.ValueSpan.Length) return false;
                    r.ValueSpan.CopyTo(_container); _containerLen = r.ValueSpan.Length; ContainerId = cid;
                }
                else if (r.ValueTextEquals("uri"u8))
                {
                    if (r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= UriCap)
                    {
                        var len = r.CopyString(_uri);
                        _uriLen = _uri.AsSpan(0, len).StartsWith("https://rupload.facebook.com/"u8) ? len : 0;
                    }
                }
            }
        }
        catch (JsonException) { return false; }
        return _containerLen > 0;
    }

    /// <summary>1 = FINISHED/PUBLISHED · 0 = IN_PROGRESS · −1 = ERROR/EXPIRED.</summary>
    private static int StatusCode(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
                if (r.TokenType == JsonTokenType.PropertyName && r.CurrentDepth == 1 && r.ValueTextEquals("status_code"u8) && r.Read())
                    return r.ValueTextEquals("FINISHED"u8) || r.ValueTextEquals("PUBLISHED"u8) ? 1 : r.ValueTextEquals("ERROR"u8) || r.ValueTextEquals("EXPIRED"u8) ? -1 : 0;
        }
        catch (JsonException) { }
        return 0;
    }

    private static bool ReadId(ReadOnlySpan<byte> json, out ulong id)
    {
        id = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
                if (r.TokenType == JsonTokenType.PropertyName && r.CurrentDepth == 1 && r.ValueTextEquals("id"u8) && r.Read())
                    return Utf8Parser.TryParse(r.ValueSpan, out id, out _) && id != 0;
        }
        catch (JsonException) { }
        return false;
    }
}
