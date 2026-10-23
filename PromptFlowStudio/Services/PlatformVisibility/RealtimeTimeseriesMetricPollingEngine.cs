using System.Buffers;
using System.Buffers.Text;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.LivePublish;
using PromptFlowStudio.Services.PostPublishSplitTest;

namespace PromptFlowStudio.Services.PlatformVisibility;

/// <summary>폴링 1 표본 — 누적 지표 + 직전 표본 대비 분당 증가 속도(Velocity) · 값 타입 · 링 슬롯 재사용.</summary>
public struct VelocitySample
{
    public long TSec, Views, Likes, Comments, Shares;
    public float Completion, ViewsPerMin, LikesPerMin, CommentsPerMin;
}

/// <summary>추적 영상 1 개의 90 분 시계열 링(19 표본 · 생성 시 1 회 할당).</summary>
public sealed class PolledVideoTrack
{
    private readonly VelocitySample[] _ring = new VelocitySample[RealtimeTimeseriesMetricPollingEngine.Capacity];
    private int _head, _count;

    public bool Active { get; internal set; }
    public int Slot { get; internal set; }
    public bool FeedSplit { get; internal set; }
    public long PublishedUnix { get; internal set; }
    public long NextDueUnix { get; internal set; }
    public long Polls { get; internal set; }
    public long Errors { get; internal set; }
    public int Count => _count;
    public VelocitySample Last => _count == 0 ? default : _ring[(_head - 1 + _ring.Length) % _ring.Length];

    /// <summary>시간순 표본(0 = 최초).</summary>
    public ref readonly VelocitySample At(int i) => ref _ring[(_head - _count + i + _ring.Length * 2) % _ring.Length];

    internal void Push(in VelocitySample s)
    {
        _ring[_head] = s;
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
    }

    internal void Reset(int slot, long publishedUnix, bool feed)
    {
        _head = _count = 0;
        Active = true; Slot = slot; FeedSplit = feed; PublishedUnix = publishedUnix;
        NextDueUnix = publishedUnix + RealtimeTimeseriesMetricPollingEngine.IntervalSec;
        Polls = Errors = 0;
    }
}

/// <summary>
/// [PromptFlow Studio v17.94.0] 📈 실시간 시계열 메트릭 폴러 &amp; 유속 피딩 엔진.
///   ▸ 발행 후 90 분 골든타임 동안 5 분 간격으로 YouTube <c>videos.list?part=statistics</c>(최대 50 ID 일괄 · 1 unit) + Analytics <c>averageViewPercentage</c>(완청률),
///     Instagram <c>/{media-id}/insights?metric=views,likes,comments,shares</c> 를 <see cref="ProductionPlatformVisibilityGatewayEngine"/> 가드 경로로 비동기 폴링.
///   ▸ 누적 → 분당 증가 속도(조회 · 좋아요 · 댓글)를 <see cref="VelocitySample"/> 로 변환해 영상별 고정 링(19)에 무할당 기록.
///   ▸ <see cref="Sink"/>(v17.93 <see cref="PostPublishSplitTestingEngine"/>)로 A/B 변형 표본을 실시간 스트리밍 — 공유수 미제공(YouTube)은 댓글수로 대체.
///   ▸ 분석 지연(Analytics 행 없음) 시 완청률은 직전 값 유지.
/// </summary>
public sealed class RealtimeTimeseriesMetricPollingEngine
{
    public const long IntervalSec = PostPublishSplitTestingEngine.IntervalSec, WindowSec = PostPublishSplitTestingEngine.GoldenWindowSec;
    public const int Capacity = PostPublishSplitTestingEngine.Capacity, MaxVideos = 16, MaxBatch = 50;

    private readonly ProductionPlatformVisibilityGatewayEngine _gw;
    private readonly PolledVideoTrack[] _tracks = new PolledVideoTrack[MaxVideos];
    private readonly GuardedRequest _req = new();
    private readonly int[] _due = new int[MaxVideos];
    private readonly long[] _views = new long[MaxVideos], _likes = new long[MaxVideos], _comments = new long[MaxVideos], _shares = new long[MaxVideos];
    private readonly float[] _completion = new float[MaxVideos];
    private readonly bool[] _got = new bool[MaxVideos];

    public RealtimeTimeseriesMetricPollingEngine(ProductionPlatformVisibilityGatewayEngine gateway)
    {
        _gw = gateway ?? throw new ArgumentNullException(nameof(gateway));
        for (var i = 0; i < MaxVideos; i++) _tracks[i] = new PolledVideoTrack();
    }

    public PostPublishSplitTestingEngine? Sink { get; set; }
    public bool FetchCompletion { get; set; } = true;
    public long Rounds { get; private set; }
    public long Requests { get; private set; }
    public long Samples { get; private set; }
    public long Fed { get; private set; }
    public long SinkRejected { get; private set; }
    public long Errors { get; private set; }
    public int LastStatus { get; private set; }
    public int TrackCount { get { var n = 0; foreach (var t in _tracks) if (t.Active) n++; return n; } }

    public PolledVideoTrack Track(int i) => _tracks[i];

    /// <summary>게이트웨이 슬롯 추적 등록 — 같은 슬롯은 리셋. 반환 = 트랙 인덱스(-1 = 용량 초과).</summary>
    public int Add(int slot, long publishedUnix, bool feedSplit)
    {
        var idx = -1;
        for (var i = 0; i < MaxVideos && idx < 0; i++) if (_tracks[i].Active && _tracks[i].Slot == slot) idx = i;
        for (var i = 0; i < MaxVideos && idx < 0; i++) if (!_tracks[i].Active) idx = i;
        if (idx >= 0) _tracks[idx].Reset(slot, publishedUnix, feedSplit);
        return idx;
    }

    public void Clear() { foreach (var t in _tracks) t.Active = false; }

    /// <summary>폴링 대상(활성 · 미삭제 · 골든타임 내 · 주기 도래) 여부.</summary>
    private bool Due(PolledVideoTrack t, long now)
    {
        if (!t.Active || now < t.NextDueUnix || now > t.PublishedUnix + WindowSec) return false;
        var b = _gw.Binding(t.Slot);
        return b.InUse && b.Visibility != VideoVisibility.Deleted;
    }

    // ══════════════════════ 폴링 라운드(비동기 · 시뮬 경로 동기 완료 · 무할당) ══════════════════════

    /// <summary>주기 도래 영상 일괄 폴링 → 유속 변환 · 링 기록 · 스플릿 테스터 피딩. 반환 = 기록 표본 수.</summary>
    public async ValueTask<int> PollDueAsync(long nowUnix, CancellationToken ct = default)
    {
        Rounds++;
        var recorded = 0;
        var nd = 0;
        for (var i = 0; i < MaxVideos; i++)
        {
            _got[i] = false;
            if (Due(_tracks[i], nowUnix) && _gw.Binding(_tracks[i].Slot).Provider == LiveProvider.YouTube && nd < MaxBatch) _due[nd++] = i;
        }
        if (nd > 0)
        {
            BuildYouTubeStats(nd);
            var st = await Send(LiveProvider.YouTube, TokenLifecycleRateLimitBackoffGuard.CostYtList, ct).ConfigureAwait(false);
            if (st == 200) ParseYouTubeStats(nd);
            else MarkErrors(nd);
            if (FetchCompletion)
                for (var k = 0; k < nd; k++)
                {
                    var i = _due[k];
                    if (!_got[i]) continue;
                    BuildAnalytics(i, nowUnix);
                    if (await Send(LiveProvider.YouTube, TokenLifecycleRateLimitBackoffGuard.CostAnalytics, ct).ConfigureAwait(false) == 200) ParseAnalytics(i);
                }
        }
        for (var i = 0; i < MaxVideos; i++)
        {
            if (!Due(_tracks[i], nowUnix) || _gw.Binding(_tracks[i].Slot).Provider != LiveProvider.Instagram) continue;
            BuildInsights(i);
            if (await Send(LiveProvider.Instagram, TokenLifecycleRateLimitBackoffGuard.CostIg, ct).ConfigureAwait(false) == 200) ParseInsights(i);
            else { _tracks[i].Errors++; Errors++; }
        }
        for (var i = 0; i < MaxVideos; i++) if (_got[i]) { Commit(i, nowUnix); recorded++; }
        return recorded;
    }

    private async ValueTask<int> Send(LiveProvider p, int cost, CancellationToken ct)
    {
        Requests++;
        var st = await _gw.SendGuardedAsync(_req, p, cost, ct).ConfigureAwait(false);
        LastStatus = st;
        return st;
    }

    private void MarkErrors(int nd) { for (var k = 0; k < nd; k++) _tracks[_due[k]].Errors++; Errors++; }

    private void BuildYouTubeStats(int nd)
    {
        var r = _req; var u = r.Url.AsSpan(); var n = 0;
        r.Method = GraphHttpMethod.Get; r.BodyLen = 0; r.Json = false;
        GraphBytes.Put(u, ref n, ProductionPlatformVisibilityGatewayEngine.YouTubeVideosUrl);
        GraphBytes.Put(u, ref n, "?part=statistics&id="u8);
        for (var k = 0; k < nd; k++)
        {
            if (k > 0) GraphBytes.Put(u, ref n, "%2C"u8);
            GraphBytes.Put(u, ref n, _gw.Binding(_tracks[_due[k]].Slot).IdSpan);
        }
        r.UrlLen = n;
    }

    /// <summary>items 배열에서 영상별 <c>"id":"…"</c> 블록을 찾아 statistics 문자열 숫자 파싱 — 누락 ID 는 오류 집계.</summary>
    private void ParseYouTubeStats(int nd)
    {
        var body = _req.Resp.BodySpan;
        Span<byte> key = stackalloc byte[PlatformVideoBinding.IdCap + 8];
        for (var k = 0; k < nd; k++)
        {
            var i = _due[k];
            var id = _gw.Binding(_tracks[i].Slot).IdSpan;
            var n = 0;
            GraphBytes.Put(key, ref n, "\"id\":\""u8); GraphBytes.Put(key, ref n, id); GraphBytes.Put(key, ref n, "\""u8);
            var at = body.IndexOf(key[..n]);
            if (at < 0) { _tracks[i].Errors++; Errors++; continue; }
            var seg = body[(at + n)..];
            var next = seg.IndexOf("\"id\":\""u8);
            if (next >= 0) seg = seg[..next];
            var v = PlatformJson.Long(seg, "\"viewCount\""u8);
            if (v < 0) { _tracks[i].Errors++; Errors++; continue; }
            _views[i] = v;
            _likes[i] = Math.Max(0, PlatformJson.Long(seg, "\"likeCount\""u8));
            _comments[i] = Math.Max(0, PlatformJson.Long(seg, "\"commentCount\""u8));
            _shares[i] = _comments[i];
            _completion[i] = _tracks[i].Last.Completion;
            _got[i] = true;
        }
    }

    private void BuildAnalytics(int i, long nowUnix)
    {
        var r = _req; var u = r.Url.AsSpan(); var n = 0;
        r.Method = GraphHttpMethod.Get; r.BodyLen = 0; r.Json = false;
        GraphBytes.Put(u, ref n, ProductionPlatformVisibilityGatewayEngine.YouTubeAnalyticsUrl);
        GraphBytes.Put(u, ref n, "?ids=channel%3D%3DMINE&startDate=2005-02-14&endDate="u8);
        var d = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, nowUnix)).UtcDateTime;
        Utf8Formatter.TryFormat(d.Year, u[n..], out var w, new StandardFormat('D', 4)); n += w; u[n++] = (byte)'-';
        Utf8Formatter.TryFormat(d.Month, u[n..], out w, new StandardFormat('D', 2)); n += w; u[n++] = (byte)'-';
        Utf8Formatter.TryFormat(d.Day, u[n..], out w, new StandardFormat('D', 2)); n += w;
        GraphBytes.Put(u, ref n, "&metrics=averageViewPercentage&filters=video%3D%3D"u8);
        GraphBytes.Put(u, ref n, _gw.Binding(_tracks[i].Slot).IdSpan);
        r.UrlLen = n;
    }

    private void ParseAnalytics(int i)
    {
        var pct = PlatformJson.Double(_req.Resp.BodySpan, "\"rows\""u8);
        if (!double.IsNaN(pct)) _completion[i] = (float)Math.Clamp(pct / 100.0, 0, 1);
    }

    private void BuildInsights(int i)
    {
        var r = _req; var u = r.Url.AsSpan(); var n = 0;
        r.Method = GraphHttpMethod.Get; r.BodyLen = 0; r.Json = false;
        GraphBytes.Put(u, ref n, ProductionPlatformVisibilityGatewayEngine.GraphBaseUrl);
        GraphBytes.Put(u, ref n, _gw.Binding(_tracks[i].Slot).IdSpan);
        GraphBytes.Put(u, ref n, "/insights?metric=views,likes,comments,shares"u8);
        r.UrlLen = n;
    }

    private void ParseInsights(int i)
    {
        var body = _req.Resp.BodySpan;
        var v = Metric(body, "\"name\":\"views\""u8);
        if (v < 0) { _tracks[i].Errors++; Errors++; return; }
        _views[i] = v;
        _likes[i] = Math.Max(0, Metric(body, "\"name\":\"likes\""u8));
        _comments[i] = Math.Max(0, Metric(body, "\"name\":\"comments\""u8));
        _shares[i] = Math.Max(0, Metric(body, "\"name\":\"shares\""u8));
        _completion[i] = _tracks[i].Last.Completion;
        _got[i] = true;
    }

    private static long Metric(ReadOnlySpan<byte> body, ReadOnlySpan<byte> name)
    {
        var k = body.IndexOf(name);
        return k < 0 ? -1 : PlatformJson.Long(body[(k + name.Length)..], "\"value\""u8);
    }

    /// <summary>유속 변환 · 링 기록 · 다음 주기 예약 · 스플릿 피딩.</summary>
    private void Commit(int i, long now)
    {
        var t = _tracks[i];
        var prev = t.Last;
        var dt = t.Count == 0 ? now - t.PublishedUnix : now - prev.TSec;
        var perMin = dt <= 0 ? 0f : 60f / dt;
        VelocitySample s;
        s.TSec = now;
        s.Views = Math.Max(_views[i], prev.Views);
        s.Likes = Math.Max(_likes[i], prev.Likes);
        s.Comments = Math.Max(_comments[i], prev.Comments);
        s.Shares = Math.Max(_shares[i], prev.Shares);
        s.Completion = _completion[i];
        s.ViewsPerMin = (s.Views - prev.Views) * perMin;
        s.LikesPerMin = (s.Likes - prev.Likes) * perMin;
        s.CommentsPerMin = (s.Comments - prev.Comments) * perMin;
        t.Push(s);
        t.Polls++;
        Samples++;
        while (t.NextDueUnix <= now) t.NextDueUnix += IntervalSec;
        var sink = Sink;
        if (!t.FeedSplit || sink is null) return;
        if (sink.Record(_gw.Binding(t.Slot).Variant, now, s.Views, s.Completion, (int)Math.Min(int.MaxValue, s.Shares))) Fed++;
        else SinkRejected++;
    }
}
