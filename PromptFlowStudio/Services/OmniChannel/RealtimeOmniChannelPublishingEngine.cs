using System.Buffers.Text;
using System.Text.Json;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.VerticalTranscode;
namespace PromptFlowStudio.Services.OmniChannel;

public enum OmniLegPhase : byte { Idle, Waiting, Deferred, Init, Uploading, Processing, Done, Error, Canceled }

/// <summary>플랫폼 배포 레그 상태(값 형식).</summary>
public struct OmniLeg
{
    public OmniPlatform Platform;
    public OmniLegPhase Phase;
    public long Offset, Size;
    public int ChunkLen, ChunkSize, ChunkIndex, ChunkCount, Retries, Polls, LastStatus;
    public double NextAt, StartedAt, FinishedAt;
    public bool NeedStatusQuery;
    public int IdLen, UrlLen;
}

/// <summary>마스터 MP4 공유 소유권 — 분기 뷰가 모두 닫히면 원본 세그먼트 반환.</summary>
public sealed class OmniMasterMedia
{
    private int _refs;
    public OmniMasterMedia(SegmentedChunkStream master) { Master = master; _refs = 1; }
    public SegmentedChunkStream Master { get; }
    public bool Released { get; private set; }
    public OmniBranchStream Branch() { Interlocked.Increment(ref _refs); return new OmniBranchStream(this); }
    public void Release() { if (Interlocked.Decrement(ref _refs) == 0) { Master.Dispose(); Released = true; } }
}

/// <summary>마스터 분기 판독 뷰(독립 위치 · 쓰기 불가) — Dispose 시 참조 해제.</summary>
public sealed class OmniBranchStream : Stream
{
    private readonly OmniMasterMedia _m;
    private long _pos;
    private int _closed;
    internal OmniBranchStream(OmniMasterMedia m) => _m = m;
    public override bool CanRead => _closed == 0;
    public override bool CanSeek => _closed == 0;
    public override bool CanWrite => false;
    public override long Length => _m.Master.Length;
    public override long Position { get => _pos; set => _pos = Math.Clamp(value, 0, Length); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> dst) { var k = _m.Master.ReadAt(_pos, dst); _pos += k; return k; }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => Length + offset };
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (Interlocked.Exchange(ref _closed, 1) == 0) _m.Release(); base.Dispose(disposing); }
}

/// <summary>
/// [PromptFlow Studio v17.51.0] 🌐 실시간 옴니채널 규격 변환 &amp; 멀티 플랫폼 분기 배포 엔진(<see cref="Step"/> 무할당).
/// v17.49 마스터 fMP4(4 MB 세그먼트)를 참조 계수 분기 뷰로 3 레그에 동시 공급한다.
/// <b>Instagram</b>: v17.48 브릿지 <c>PublishEncoded</c>(오리지널리티 게이트 · rupload 4 MB). <b>YouTube Shorts</b>: Data API v3 재개 업로드 —
/// <c>POST upload/youtube/v3/videos?uploadType=resumable&amp;part=snippet,status</c>(메타 JSON · X-Upload-Content-Length) → Location →
/// 4 MB(256 KiB 배수) <c>PUT Content-Range</c> · 308 Range 로 확정 오프셋 재개 · 실패 시 <c>bytes */N</c> 상태 질의. <b>TikTok</b>: Content Posting API v2 —
/// <c>POST /v2/post/publish/video/init/</c>(post_info: 캡션 · 공개 범위 · video_cover_timestamp_ms / source_info: FILE_UPLOAD · chunk_size · total_chunk_count) →
/// upload_url <c>PUT Content-Range</c>(TikTok 규칙: 64 MB 이하 단일 청크, 초과 시 10 MB 청크 · 마지막 청크가 잔여 흡수 — 5 MB 미만 청크 금지라 4 MB 고정 분할 불가) →
/// <c>status/fetch</c> PUBLISH_COMPLETE 폴링. 레그는 각자 in-flight 1 슬롯으로 병렬 진행되며 쿼터 가드를 통과해야 시작한다.
/// </summary>
public sealed class RealtimeOmniChannelPublishingEngine
{
    public const int YtChunk = 4 * 1024 * 1024, TtChunk = 10 * 1024 * 1024, TtSingleMax = 64 * 1024 * 1024, MetaCap = 24 * 1024, MaxRetries = 6, MaxPolls = 120;
    public static ReadOnlySpan<byte> YtInitUrl => "https://www.googleapis.com/upload/youtube/v3/videos?uploadType=resumable&part=snippet,status"u8;
    public static ReadOnlySpan<byte> TtInitUrl => "https://open.tiktokapis.com/v2/post/publish/video/init/"u8;
    public static ReadOnlySpan<byte> TtStatusUrl => "https://open.tiktokapis.com/v2/post/publish/status/fetch/"u8;
    private readonly OmniLeg[] _legs = new OmniLeg[3];
    private readonly GraphCall[] _calls = { new(), new(), new() };
    private readonly byte[][] _url = { new byte[1024], new byte[1024], new byte[1024] }, _id = { new byte[128], new byte[128], new byte[128] };
    private readonly byte[] _ytMeta = new byte[MetaCap], _ttMeta = new byte[8192], _chunk = new byte[TtChunk + TtChunk / 2];
    private readonly char[] _ytTitle = new char[OmniMetadata.YtTitleMax], _ytDesc = new char[OmniMetadata.YtDescMax], _ytTags = new char[OmniMetadata.YtTagsMax], _ttCap = new char[OmniMetadata.CaptionMax], _igCap = new char[OmniMetadata.CaptionMax];
    private int _ytTitleN, _ytDescN, _ytTagsN, _ttCapN, _igCapN, _ytMetaN, _ttMetaN, _ytTagCount, _ttTags, _igTags, _category, _coverMs;
    private OmniMasterMedia? _media;
    private OmniBranchStream?[] _branch = new OmniBranchStream?[3];

    public RealtimeOmniChannelPublishingEngine() { for (var i = 0; i < 3; i++) _legs[i].Platform = (OmniPlatform)i; }

    public string PrivacyYouTube { get; set; } = "private";
    public string PrivacyTikTok { get; set; } = "SELF_ONLY";
    public ref readonly OmniLeg Leg(OmniPlatform p) => ref _legs[(int)p];
    public ReadOnlySpan<char> YouTubeTitle => _ytTitle.AsSpan(0, _ytTitleN);
    public ReadOnlySpan<char> YouTubeDescription => _ytDesc.AsSpan(0, _ytDescN);
    public ReadOnlySpan<char> YouTubeTags => _ytTags.AsSpan(0, _ytTagsN);
    public ReadOnlySpan<char> TikTokCaption => _ttCap.AsSpan(0, _ttCapN);
    public ReadOnlySpan<char> InstagramCaption => _igCap.AsSpan(0, _igCapN);
    public int YouTubeCategoryId => OmniMetadata.YouTubeCategory(_category);
    public int YouTubeTagCount => _ytTagCount;
    public int TikTokTags => _ttTags;
    public int InstagramTags => _igTags;
    public int CoverMs => _coverMs;
    public ReadOnlySpan<byte> PlatformId(OmniPlatform p) => _id[(int)p].AsSpan(0, _legs[(int)p].IdLen);
    public bool HasMedia => _media is not null;
    public OmniMasterMedia? Media => _media;
    public bool IsActive { get { for (var i = 0; i < 3; i++) if (_legs[i].Phase is >= OmniLegPhase.Waiting and <= OmniLegPhase.Processing) return true; return false; } }
    public double LastStepUs { get; private set; }
    public static string PhaseName(OmniLegPhase p) => p switch
    {
        OmniLegPhase.Waiting => "대기",
        OmniLegPhase.Deferred => "쿼터 지연",
        OmniLegPhase.Init => "세션 생성",
        OmniLegPhase.Uploading => "업로드",
        OmniLegPhase.Processing => "처리 중",
        OmniLegPhase.Done => "완료",
        OmniLegPhase.Error => "에러",
        OmniLegPhase.Canceled => "취소",
        _ => "—",
    };
    public static string PlatformName(OmniPlatform p) => p switch { OmniPlatform.Instagram => "Instagram Reels", OmniPlatform.YouTube => "YouTube Shorts", _ => "TikTok" };

    /// <summary>플랫폼 메타 변환(무할당) — 원본 제목 · 캡션 · 카테고리 · 커버 ms.</summary>
    public void PrepareMetadata(ReadOnlySpan<char> title, ReadOnlySpan<char> caption, int category, int coverMs, double durationSec)
    {
        _category = category;
        _coverMs = (int)Math.Clamp(coverMs, 0, Math.Max(durationSec * 1000 - 1, 0));
        _ytTitleN = OmniMetadata.YouTubeTitle(title, _ytTitle, out var inTitle);
        _ytDescN = OmniMetadata.YouTubeDescription(caption, inTitle, _ytDesc);
        _ytTagsN = OmniMetadata.YouTubeTags(caption, _ytTags, out _ytTagCount);
        _ttCapN = OmniMetadata.Caption(caption, OmniMetadata.TikTokTagMax, OmniMetadata.CaptionMax, _ttCap, out _ttTags);
        _igCapN = OmniMetadata.Caption(caption, OmniMetadata.IgTagMax, OmniMetadata.CaptionMax, _igCap, out _igTags);
    }

    /// <summary>배포 시작(비핫패스) — 마스터 소유권 인수 · 레그 마스크(bit0 IG · bit1 YT · bit2 TT).</summary>
    public bool Begin(SegmentedChunkStream master, int mask, double now)
    {
        if (IsActive) return false;
        ReleaseBranches();
        _media?.Release();
        _media = new OmniMasterMedia(master);
        for (var i = 0; i < 3; i++)
        {
            ref var l = ref _legs[i];
            var on = (mask >> i & 1) != 0;
            l = default; l.Platform = (OmniPlatform)i;
            l.Phase = on ? OmniLegPhase.Waiting : OmniLegPhase.Idle;
            l.Size = master.Length; l.StartedAt = now; l.NextAt = now;
            if (on) _branch[i] = _media.Branch();
        }
        BuildYouTubeMeta(); BuildTikTokMeta(master.Length);
        _media.Release(); // 엔진 자신의 참조 해제 — 이후 분기 뷰가 소유
        return true;
    }

    public void CancelAll()
    {
        for (var i = 0; i < 3; i++) if (_legs[i].Phase is >= OmniLegPhase.Waiting and <= OmniLegPhase.Processing) { _legs[i].Phase = OmniLegPhase.Canceled; CloseBranch(i); }
    }

    private void CloseBranch(int i) { _branch[i]?.Dispose(); _branch[i] = null; }
    private void ReleaseBranches() { for (var i = 0; i < 3; i++) CloseBranch(i); }

    private void BuildYouTubeMeta()
    {
        var b = _ytMeta.AsSpan(); var n = 0;
        OmniMetadata.Raw(b, ref n, "{\"snippet\":{\"title\":"u8); OmniMetadata.Str(b, ref n, YouTubeTitle);
        OmniMetadata.Raw(b, ref n, ",\"description\":"u8); OmniMetadata.Str(b, ref n, YouTubeDescription);
        OmniMetadata.Raw(b, ref n, ",\"tags\":["u8);
        var tags = YouTubeTags; var first = true;
        while (!tags.IsEmpty)
        {
            var c = tags.IndexOf(',');
            var t = c < 0 ? tags : tags[..c];
            tags = c < 0 ? default : tags[(c + 1)..];
            if (!first) b[n++] = (byte)',';
            OmniMetadata.Str(b, ref n, t); first = false;
        }
        OmniMetadata.Raw(b, ref n, "],\"categoryId\":\""u8); OmniMetadata.Num(b, ref n, YouTubeCategoryId);
        OmniMetadata.Raw(b, ref n, "\"},\"status\":{\"privacyStatus\":"u8); OmniMetadata.Str(b, ref n, PrivacyYouTube);
        OmniMetadata.Raw(b, ref n, ",\"selfDeclaredMadeForKids\":false}}"u8);
        _ytMetaN = n;
    }

    /// <summary>TikTok 청크 계획 — 64 MB 이하 단일, 초과 시 10 MB · 마지막 청크 잔여 흡수.</summary>
    public static void TikTokPlan(long size, out int chunkSize, out int count)
    {
        if (size <= TtSingleMax) { chunkSize = (int)size; count = 1; return; }
        chunkSize = TtChunk; count = (int)(size / TtChunk);
    }

    private void BuildTikTokMeta(long size)
    {
        TikTokPlan(size, out var cs, out var cc);
        var b = _ttMeta.AsSpan(); var n = 0;
        OmniMetadata.Raw(b, ref n, "{\"post_info\":{\"title\":"u8); OmniMetadata.Str(b, ref n, TikTokCaption);
        OmniMetadata.Raw(b, ref n, ",\"privacy_level\":"u8); OmniMetadata.Str(b, ref n, PrivacyTikTok);
        OmniMetadata.Raw(b, ref n, ",\"disable_duet\":false,\"disable_comment\":false,\"disable_stitch\":false,\"video_cover_timestamp_ms\":"u8); OmniMetadata.Num(b, ref n, _coverMs);
        OmniMetadata.Raw(b, ref n, "},\"source_info\":{\"source\":\"FILE_UPLOAD\",\"video_size\":"u8); OmniMetadata.Num(b, ref n, size);
        OmniMetadata.Raw(b, ref n, ",\"chunk_size\":"u8); OmniMetadata.Num(b, ref n, cs);
        OmniMetadata.Raw(b, ref n, ",\"total_chunk_count\":"u8); OmniMetadata.Num(b, ref n, cc);
        OmniMetadata.Raw(b, ref n, "}}"u8);
        _ttMetaN = n;
        _legs[2].ChunkSize = cs; _legs[2].ChunkCount = cc;
    }

    // ══════════════════════ 틱(무할당) ══════════════════════
    public void Step(double now, IGraphTransport t, OmniPlatformQuotaSpamSafetyGuard guard, GraphApiBridgeStudioRunner? bridge, ReadOnlySpan<char> igCaptionOverride = default)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        StepInstagram(now, bridge);
        for (var i = 1; i < 3; i++)
        {
            for (var hop = 0; hop < 3; hop++)
            {
                ref var l = ref _legs[i];
                var c = _calls[i];
                if (c.InFlight) { if (!c.TryComplete(out var st)) break; Handle(i, st, now, guard); continue; }
                if (l.Phase is not (OmniLegPhase.Waiting or OmniLegPhase.Deferred or OmniLegPhase.Init or OmniLegPhase.Uploading or OmniLegPhase.Processing) || now < l.NextAt) break;
                if (!Issue(i, now, t, guard)) break;
            }
        }
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void StepInstagram(double now, GraphApiBridgeStudioRunner? bridge)
    {
        ref var l = ref _legs[0];
        if (l.Phase == OmniLegPhase.Waiting)
        {
            if (bridge is null) { l.Phase = OmniLegPhase.Error; CloseBranch(0); return; }
            if (bridge.Dispatcher.IsActive) return;
            var s = _branch[0]; _branch[0] = null;
            if (s is null) { l.Phase = OmniLegPhase.Error; return; }
            var cap = _igCap.AsSpan(0, _igCapN).ToString(); // 비핫패스(레그 시작 1 회)
            l.Phase = bridge.PublishEncoded(s, cap) ? OmniLegPhase.Uploading : OmniLegPhase.Error; // 브릿지가 스트림 소유(종료 시 Dispose)
            return;
        }
        if (l.Phase is OmniLegPhase.Uploading or OmniLegPhase.Processing && bridge is not null)
        {
            var d = bridge.Dispatcher;
            l.Offset = d.Offset;
            l.Phase = d.Phase switch
            {
                ReelsPublishPhase.Polling or ReelsPublishPhase.Publishing => OmniLegPhase.Processing,
                ReelsPublishPhase.Published => OmniLegPhase.Done,
                ReelsPublishPhase.Failed or ReelsPublishPhase.Canceled => OmniLegPhase.Error,
                _ => OmniLegPhase.Uploading,
            };
            if (l.Phase == OmniLegPhase.Done)
            {
                l.FinishedAt = now; l.Offset = l.Size;
                Utf8Formatter.TryFormat(d.MediaId, _id[0], out var w); l.IdLen = w;
            }
            if (l.Phase == OmniLegPhase.Error) l.FinishedAt = now;
        }
    }

    private bool Issue(int i, double now, IGraphTransport t, OmniPlatformQuotaSpamSafetyGuard guard)
    {
        ref var l = ref _legs[i];
        var c = _calls[i];
        var url = c.Url.AsSpan(); var hdr = c.Hdr.AsSpan(); int n = 0, h = 0;
        var slot = i == 1 ? guard.YouTubeToken : guard.TikTokToken;
        if (l.Phase is OmniLegPhase.Waiting or OmniLegPhase.Deferred)
        {
            var ok = i == 1 ? guard.TryReserveYouTubeUpload(now) : guard.TryTikTokInit(now);
            if (!ok)
            {
                l.Phase = OmniLegPhase.Deferred;
                l.NextAt = i == 1 ? OmniPlatformQuotaSpamSafetyGuard.NextPacificMidnight(now) : now + Math.Max(guard.TikTokWaitSec(now), 60);
                return false;
            }
            l.Phase = OmniLegPhase.Init;
        }
        GraphBytes.Put(hdr, ref h, "Authorization: Bearer "u8); h += slot.CopyTo(hdr[h..]); GraphBytes.Put(hdr, ref h, "\r\n"u8);
        if (l.Phase == OmniLegPhase.Init)
        {
            if (i == 1)
            {
                GraphBytes.Put(url, ref n, YtInitUrl);
                GraphBytes.Put(hdr, ref h, "Content-Type: application/json; charset=UTF-8\r\nX-Upload-Content-Type: video/mp4\r\nX-Upload-Content-Length: "u8); GraphBytes.Put(hdr, ref h, l.Size); GraphBytes.Put(hdr, ref h, "\r\n"u8);
                c.Body = _ytMeta.AsMemory(0, _ytMetaN);
            }
            else
            {
                GraphBytes.Put(url, ref n, TtInitUrl);
                GraphBytes.Put(hdr, ref h, "Content-Type: application/json; charset=UTF-8\r\n"u8);
                c.Body = _ttMeta.AsMemory(0, _ttMetaN);
            }
            c.Method = GraphHttpMethod.Post;
        }
        else if (l.Phase == OmniLegPhase.Uploading)
        {
            GraphBytes.Put(url, ref n, _url[i].AsSpan(0, l.UrlLen));
            if (l.NeedStatusQuery)
            {
                GraphBytes.Put(hdr, ref h, "Content-Range: bytes */"u8); GraphBytes.Put(hdr, ref h, l.Size); GraphBytes.Put(hdr, ref h, "\r\n"u8);
                c.Body = default; l.ChunkLen = 0;
            }
            else
            {
                long len = i == 1 ? Math.Min(YtChunk, l.Size - l.Offset) : l.ChunkIndex == l.ChunkCount - 1 ? l.Size - l.Offset : l.ChunkSize;
                l.ChunkLen = (int)len;
                var got = _branch[i]!.Media(l.Offset, _chunk.AsSpan(0, l.ChunkLen));
                GraphBytes.Put(hdr, ref h, "Content-Type: video/mp4\r\nContent-Range: bytes "u8); GraphBytes.Put(hdr, ref h, l.Offset); hdr[h++] = (byte)'-'; GraphBytes.Put(hdr, ref h, l.Offset + got - 1);
                hdr[h++] = (byte)'/'; GraphBytes.Put(hdr, ref h, l.Size); GraphBytes.Put(hdr, ref h, "\r\n"u8);
                c.Body = _chunk.AsMemory(0, got);
            }
            c.Method = GraphHttpMethod.Put;
        }
        else
        {
            GraphBytes.Put(url, ref n, TtStatusUrl);
            GraphBytes.Put(hdr, ref h, "Content-Type: application/json; charset=UTF-8\r\n"u8);
            var b = _ttMeta.AsSpan(); var m = 0;
            GraphBytes.Put(b, ref m, "{\"publish_id\":\""u8); GraphBytes.Put(b, ref m, _id[2].AsSpan(0, l.IdLen)); GraphBytes.Put(b, ref m, "\"}"u8);
            c.Body = _ttMeta.AsMemory(0, m);
            c.Method = GraphHttpMethod.Post;
        }
        c.UrlLen = n; c.HdrLen = h;
        c.Send(t, default);
        return true;
    }

    private void Handle(int i, int st, double now, OmniPlatformQuotaSpamSafetyGuard guard)
    {
        ref var l = ref _legs[i];
        l.LastStatus = st;
        if (l.Phase == OmniLegPhase.Canceled) return;
        var r = _calls[i].Resp;
        var body = r.BodySpan;
        if (st == 429 || st == 0 || st >= 500)
        {
            if (++l.Retries > MaxRetries) { Fail(i, now, guard); return; }
            l.NextAt = now + Math.Max(r.RetryAfterSec, Math.Min(Math.Pow(2, l.Retries - 1), 60));
            if (l.Phase == OmniLegPhase.Uploading && i == 1) l.NeedStatusQuery = true;
            return;
        }
        if (l.Phase == OmniLegPhase.Init)
        {
            if (st is < 200 or >= 300) { Fail(i, now, guard); return; }
            if (i == 1)
            {
                if (r.LocationLength == 0) { Fail(i, now, guard); return; }
                r.LocationSpan.CopyTo(_url[1]); l.UrlLen = r.LocationLength;
            }
            else if (!ReadTikTokInit(body, ref l)) { Fail(i, now, guard); return; }
            l.Phase = OmniLegPhase.Uploading; l.Retries = 0;
            return;
        }
        if (l.Phase == OmniLegPhase.Uploading)
        {
            if (i == 1)
            {
                if (st == 308)
                {
                    l.Offset = RangeEnd(r.RangeSpan) + 1;
                    l.NeedStatusQuery = false; l.Retries = 0;
                    return;
                }
                if (st is 200 or 201)
                {
                    if (!ReadString(body, "id"u8, _id[1], out var len)) { Fail(i, now, guard); return; }
                    l.IdLen = len; l.Offset = l.Size; l.Phase = OmniLegPhase.Done; l.FinishedAt = now;
                    guard.CommitYouTubeUpload(true);
                    CloseBranch(1);
                    return;
                }
                if (st == 404) { l.Phase = OmniLegPhase.Init; l.Offset = 0; l.NeedStatusQuery = false; return; } // 세션 만료 → 재시작
                Fail(i, now, guard);
                return;
            }
            if (st is 200 or 201 or 206)
            {
                l.Offset += l.ChunkLen; l.ChunkIndex++; l.Retries = 0;
                if (l.ChunkIndex >= l.ChunkCount || l.Offset >= l.Size) { l.Phase = OmniLegPhase.Processing; l.NextAt = now + 1; CloseBranch(2); }
                return;
            }
            Fail(i, now, guard);
            return;
        }
        // TikTok 상태 폴링
        if (st is < 200 or >= 300) { Fail(i, now, guard); return; }
        l.Polls++;
        var s = TikTokStatus(body);
        if (s == 1) { l.Phase = OmniLegPhase.Done; l.FinishedAt = now; }
        else if (s < 0 || l.Polls >= MaxPolls) Fail(i, now, guard);
        else l.NextAt = now + 2;
    }

    private void Fail(int i, double now, OmniPlatformQuotaSpamSafetyGuard guard)
    {
        ref var l = ref _legs[i];
        if (i == 1 && l.Phase is OmniLegPhase.Init or OmniLegPhase.Uploading) guard.CommitYouTubeUpload(false);
        l.Phase = OmniLegPhase.Error; l.FinishedAt = now;
        CloseBranch(i);
    }

    private static long RangeEnd(ReadOnlySpan<byte> range)
    {
        var d = range.LastIndexOf((byte)'-');
        return d >= 0 && Utf8Parser.TryParse(range[(d + 1)..], out long e, out _) ? e : -1;
    }

    private bool ReadTikTokInit(ReadOnlySpan<byte> json, ref OmniLeg l)
    {
        var okCode = false;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType != JsonTokenType.PropertyName || r.CurrentDepth != 2) continue;
                if (r.ValueTextEquals("publish_id"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= _id[2].Length) { l.IdLen = r.CopyString(_id[2]); } }
                else if (r.ValueTextEquals("upload_url"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= _url[2].Length) l.UrlLen = r.CopyString(_url[2]); }
                else if (r.ValueTextEquals("code"u8)) { if (r.Read()) okCode = r.ValueTextEquals("ok"u8); }
            }
        }
        catch (JsonException) { return false; }
        return okCode && l.IdLen > 0 && l.UrlLen > 0;
    }

    /// <summary>1 = PUBLISH_COMPLETE · 0 = 처리 중 · −1 = FAILED.</summary>
    private static int TikTokStatus(ReadOnlySpan<byte> json)
    {
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
                if (r.TokenType == JsonTokenType.PropertyName && r.CurrentDepth == 2 && r.ValueTextEquals("status"u8) && r.Read())
                    return r.ValueTextEquals("PUBLISH_COMPLETE"u8) ? 1 : r.ValueTextEquals("FAILED"u8) ? -1 : 0;
        }
        catch (JsonException) { }
        return 0;
    }

    private static bool ReadString(ReadOnlySpan<byte> json, ReadOnlySpan<byte> key, Span<byte> dst, out int len)
    {
        len = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
                if (r.TokenType == JsonTokenType.PropertyName && r.CurrentDepth == 1 && r.ValueTextEquals(key) && r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= dst.Length)
                { len = r.CopyString(dst); return len > 0; }
        }
        catch (JsonException) { }
        return false;
    }
}

internal static class OmniBranchExt
{
    /// <summary>분기 뷰 위치 독립 판독(레그 오프셋 · 무할당).</summary>
    public static int Media(this OmniBranchStream s, long pos, Span<byte> dst) { s.Position = pos; return s.Read(dst); }
}
