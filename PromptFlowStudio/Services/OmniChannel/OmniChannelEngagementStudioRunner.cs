using System.Globalization;
using System.Text.Json;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.VerticalTranscode;
namespace PromptFlowStudio.Services.OmniChannel;

/// <summary>
/// [PromptFlow Studio v17.51.0] 🌐 옴니채널 크로스 배포 &amp; 댓글 스튜디오 러너(10 Hz · 시뮬레이션 시계 1 틱 = 1 초 기본).
/// 마스터 확보(v17.49 준비 MP4 인수 · 없으면 녹화 구동) → 메타 변환 → 3 레그 병렬 배포(IG = v17.48 브릿지 · YT/TT = 엔진) →
/// 레그 완료 3 분 내 고정 질문 훅(YT commentThreads.insert · IG /{media}/comments · TikTok 은 댓글 API 부재로 수동) →
/// 댓글 수집(YT commentThreads.list 폴링 · IG/TikTok 시뮬레이션 주입 — 실 IG 는 v17.48 웹훅이 ID 만 전달) → 분류 · 답글 제안 →
/// 자동 전송(기본 OFF · 가드 유사도/페이싱/시간당 상한 통과 시 · YT comments.insert / IG /{comment}/replies · TikTok 수동).
/// </summary>
public sealed class OmniChannelEngagementStudioRunner : IDisposable
{
    public const int Hz = 10, IdCap = 64, Hist = 240;
    public static ReadOnlySpan<byte> SimYtToken => "ya29.sim-youtube-token-0001"u8;
    public static ReadOnlySpan<byte> SimTtToken => "act.sim-tiktok-token-0001"u8;
    private readonly object _gate = new();
    private readonly VerticalTranscoderStudioRunner? _trans;
    private readonly GraphApiBridgeStudioRunner? _bridge;
    private readonly NarrativeTimeline.NarrativeTimelineStudioRunner? _narr;
    private readonly ViralReels.ViralReelsStudioRunner? _reels;
    private readonly GraphCall _cmt = new();
    private readonly byte[] _rawIds = new byte[AiAudienceCommentInteractionSynthesizer.Cap * IdCap], _body = new byte[4096];
    private readonly int[] _rawLen = new int[AiAudienceCommentInteractionSynthesizer.Cap];
    private readonly char[] _hook = new char[AiAudienceCommentInteractionSynthesizer.TextCap], _txt = new char[AiAudienceCommentInteractionSynthesizer.TextCap];
    private readonly bool[] _hookPosted = new bool[3];
    private readonly double[] _hookDueBy = new double[3];
    private readonly float[] _ytQS = new float[Hist], _sentS = new float[Hist];
    private HttpGraphTransport? _http;
    private int _pendingSlot = -1, _pendingKind, _head, _series, _hookLen;
    private double _time, _nextPollAt, _nextSimCommentAt;
    private ulong _rng = 0x2545F4914F6CDD1DUL;
    private bool _waitingMaster;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public OmniChannelEngagementStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(VerticalTranscoderStudioRunner)) as VerticalTranscoderStudioRunner,
        sp.GetService(typeof(GraphApiBridgeStudioRunner)) as GraphApiBridgeStudioRunner,
        sp.GetService(typeof(NarrativeTimeline.NarrativeTimelineStudioRunner)) as NarrativeTimeline.NarrativeTimelineStudioRunner,
        sp.GetService(typeof(ViralReels.ViralReelsStudioRunner)) as ViralReels.ViralReelsStudioRunner) { }

    public OmniChannelEngagementStudioRunner(VerticalTranscoderStudioRunner? trans, GraphApiBridgeStudioRunner? bridge, NarrativeTimeline.NarrativeTimelineStudioRunner? narr = null, ViralReels.ViralReelsStudioRunner? reels = null)
    {
        _trans = trans; _bridge = bridge; _narr = narr; _reels = reels;
        Sim = new SimulatedOmniPlatformServer(SimYtToken, SimTtToken);
        Span<char> t = stackalloc char[64];
        for (var i = 0; i < SimYtToken.Length; i++) t[i] = (char)SimYtToken[i];
        Guard.YouTubeToken.Set(t[..SimYtToken.Length]);
        for (var i = 0; i < SimTtToken.Length; i++) t[i] = (char)SimTtToken[i];
        Guard.TikTokToken.Set(t[..SimTtToken.Length]);
    }

    public event Action? OnChange;
    public RealtimeOmniChannelPublishingEngine Engine { get; } = new();
    public AiAudienceCommentInteractionSynthesizer Comments { get; } = new();
    public OmniPlatformQuotaSpamSafetyGuard Guard { get; } = new();
    public SimulatedOmniPlatformServer Sim { get; private set; }
    public bool Live { get; private set; }
    public bool AutoSend { get; private set; }
    public int Mask { get; private set; } = 7;
    public double SecondsPerTick { get; set; } = 1;
    public string Title { get; set; } = "AI 시네마틱 리믹스 — 3초 후 반전 주의";
    public int Category { get; set; }
    public double Now => _time;
    public bool IsRunning => _running;
    public bool WaitingForMaster => _waitingMaster;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public long HooksPosted { get; private set; }
    public long RepliesSent { get; private set; }
    public long ManualQueued { get; private set; }
    public long PollErrors { get; private set; }
    public int SeriesCount => _series;
    public float YtQuotaSeries(int i) => _ytQS[(_head - 1 - i + 2 * Hist) % Hist];
    public float SentSeries(int i) => _sentS[(_head - 1 - i + 2 * Hist) % Hist];
    public bool HookPosted(OmniPlatform p) => _hookPosted[(int)p];
    /// <summary>Instagram(v17.48 가드) 토큰 버킷 잔여 %.</summary>
    public double InstagramQuotaPct => _bridge?.Guard.QuotaPct ?? 0;
    public ReadOnlySpan<char> HookText => _hook.AsSpan(0, _hookLen);
    private IGraphTransport Transport => Live && _http is not null ? _http : Sim;

    // ══════════════════════ 제어(할당 허용) ══════════════════════
    public void SetMask(int m) { lock (_gate) Mask = m & 7; Raise(); }
    public void SetAutoSend(bool on) { lock (_gate) AutoSend = on; LastAction = on ? "🤖 자동 답글 ON(가드 통과분만)" : "✋ 자동 답글 OFF(원클릭 승인)"; Raise(); }
    public void SetAiSignature(bool on) { lock (_gate) Comments.AiSignature = on; Raise(); }

    public bool SetCredentials(string youtubeToken, string tiktokToken)
    {
        lock (_gate)
        {
            if (youtubeToken.Trim().Length >= 16) Guard.YouTubeToken.Set(youtubeToken.AsSpan().Trim());
            if (tiktokToken.Trim().Length >= 16) Guard.TikTokToken.Set(tiktokToken.AsSpan().Trim());
        }
        LastAction = "🔐 YouTube/TikTok 토큰 적용(AES-GCM 메모리 보관)";
        Raise();
        return true;
    }

    public bool SetLive(bool on)
    {
        lock (_gate)
        {
            if (Engine.IsActive) { LastAction = "⚠ 배포 중 — 모드 전환 불가"; Raise(); return false; }
            Live = on;
            if (on) _http ??= new HttpGraphTransport();
        }
        LastAction = on ? "🌐 실 API 모드(YouTube Data v3 · TikTok v2)" : "🧪 시뮬레이션 모드";
        Raise();
        return true;
    }

    /// <summary>원클릭 옴니채널 배포 — v17.49 준비 MP4 를 인수하거나, 없으면 트랜스코더 녹화를 시작해 완료 시 자동 배포.</summary>
    public bool PublishAll()
    {
        lock (_gate)
        {
            if (Engine.IsActive) { LastAction = "⚠ 이미 배포 중"; Raise(); return false; }
            if (_trans is null) { LastAction = "⚠ v17.49 트랜스코더 미연동"; Raise(); return false; }
            if (_trans.State == TranscodeSessionState.Ready && _trans.TakeReady() is SegmentedChunkStream ready) return BeginWith(ready);
            if (_trans.State != TranscodeSessionState.Recording && !_trans.BeginRecording()) { LastAction = "⚠ 마스터 인코딩 시작 실패"; Raise(); return false; }
            _waitingMaster = true;
        }
        LastAction = "🎞 v17.49 마스터 인코딩 대기";
        Raise();
        return true;
    }

    /// <summary>외부 마스터 직접 공급(테스트 · 다른 파이프라인).</summary>
    public bool PublishMaster(SegmentedChunkStream master) { lock (_gate) return BeginWith(master); }

    private bool BeginWith(SegmentedChunkStream master)
    {
        _waitingMaster = false;
        var dur = Gb(master, out var cover);
        var caption = _reels?.BuildCaption() ?? "🎬 Cinematic AI remix #reels #remix #cinematic #ai #shorts #viral #fyp #trending";
        Engine.PrepareMetadata(Title, caption, Category, cover, dur);
        if (!Engine.Begin(master, Mask, _time)) { master.Dispose(); LastAction = "⚠ 배포 시작 실패"; Raise(); return false; }
        for (var i = 0; i < 3; i++) { _hookPosted[i] = false; _hookDueBy[i] = 0; }
        Sim.YouTubePublishedAt = double.MaxValue;
        LastAction = "🚀 3 플랫폼 분기 배포 · " + (master.Length / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        Raise();
        return true;
    }

    private double Gb(SegmentedChunkStream m, out int coverMs)
    {
        coverMs = (int)(_reels?.Synth.RemixHookMs ?? 100);
        m.Position = 0;
        var dur = GraphBridge.Mp4Probe.TryProbe(m, out var spec) ? spec.DurationSec : 6;
        m.Position = 0;
        return dur;
    }

    public void Cancel() { lock (_gate) { Engine.CancelAll(); _waitingMaster = false; } LastAction = "⏹ 배포 취소"; Raise(); }

    /// <summary>원클릭 답글 전송(i = 0 최신) — 가드 판정 결과 반환(0 전송 · 1 유사 · 2 페이싱 · 3 수동/불가).</summary>
    public int SendReply(int i)
    {
        int r;
        lock (_gate) r = TrySend(i);
        LastAction = r switch { 0 => "💬 답글 전송", 1 => "⛔ 최근 답글과 유사(≥ 40%) — 다른 문구 필요", 2 => "⏳ 페이싱 대기 " + Math.Max(Guard.NextReplyAt - _time, 0).ToString("0", CultureInfo.InvariantCulture) + " s", _ => "✋ 수동 처리 필요(TikTok · 스팸 · 대기 중 호출)" };
        Raise();
        return r;
    }

    public void Regenerate(int i)
    {
        lock (_gate) Comments.SynthesizeReply(i, Scene(), Tension(), Guard);
        Raise();
    }

    private int Scene() => _narr is null ? 1 : Math.Max(_narr.SceneIndex + 1, 1);
    private float Tension() => _narr?.Arc.Tension ?? 0.5f;

    // ══════════════════════ 틱(정상 상태 무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("OmniChannel.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _time += SecondsPerTick;
            Sim.Now = _time;
            if (_waitingMaster && _trans is not null)
            {
                if (!_trans.IsRunning) for (var i = 0; i < 8 && _trans.State == TranscodeSessionState.Recording; i++) _trans.Step();
                if (_trans.State == TranscodeSessionState.Ready && _trans.TakeReady() is SegmentedChunkStream ready) BeginWith(ready);
                else if (_trans.State == TranscodeSessionState.Failed) { _waitingMaster = false; LastAction = "⚠ 마스터 인코딩 실패"; }
            }
            if (_bridge is not null && !_bridge.IsRunning && _bridge.Dispatcher.IsActive) { _bridge.AdvanceClock(SecondsPerTick - 1.0 / 60); for (var i = 0; i < 30 && _bridge.Dispatcher.IsActive; i++) _bridge.Step(); }
            Engine.Step(_time, Transport, Guard, _bridge);
            for (var p = 0; p < 3; p++)
            {
                ref readonly var l = ref Engine.Leg((OmniPlatform)p);
                if (l.Phase == OmniLegPhase.Done && _hookDueBy[p] == 0) { _hookDueBy[p] = _time + 180; if (p == 1) Sim.YouTubePublishedAt = _time; }
            }
            CommentTick();
            _ytQS[_head] = (float)Guard.YtQuotaPct; _sentS[_head] = RepliesSent;
            _head = (_head + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    private void CommentTick()
    {
        if (_cmt.InFlight)
        {
            if (!_cmt.TryComplete(out var st)) return;
            Complete(st);
            return;
        }
        // ① 고정 질문 훅(게시 3 분 내)
        for (var p = 0; p < 3; p++)
        {
            if (_hookDueBy[p] == 0 || _hookPosted[p]) continue;
            var lang = p == 1 ? CommentLang.En : CommentLang.Ko;
            _hookLen = Comments.WritePinnedHook(lang, Category + p, _hook);
            if (p == 2) { _hookPosted[p] = true; ManualQueued++; continue; } // TikTok: 댓글 API 없음 → 수동
            if (p == 1 && !Guard.TrySpendYouTube(OmniPlatformQuotaSpamSafetyGuard.YtCommentCost, _time)) continue;
            if (IssueComment((OmniPlatform)p, -1, _hook.AsSpan(0, _hookLen))) { _pendingKind = 10 + p; return; }
        }
        // ② YouTube 댓글 폴링(30 초)
        ref readonly var yt = ref Engine.Leg(OmniPlatform.YouTube);
        if (yt.Phase == OmniLegPhase.Done && _time >= _nextPollAt && Guard.TrySpendYouTube(OmniPlatformQuotaSpamSafetyGuard.YtListCost, _time))
        {
            _nextPollAt = _time + 30;
            var url = _cmt.Url.AsSpan(); var n = 0;
            GraphBytes.Put(url, ref n, "https://www.googleapis.com/youtube/v3/commentThreads?part=snippet&videoId="u8); GraphBytes.Put(url, ref n, Engine.PlatformId(OmniPlatform.YouTube));
            GraphBytes.Put(url, ref n, "&order=time&maxResults=20&textFormat=plainText"u8);
            _cmt.UrlLen = n; _cmt.HdrLen = AuthHeader(OmniPlatform.YouTube, _cmt.Hdr); _cmt.Method = GraphHttpMethod.Get; _cmt.Body = default;
            _pendingKind = 1;
            _cmt.Send(Transport, default);
            if (_cmt.InFlight && _cmt.TryComplete(out var st)) Complete(st);
            return;
        }
        // ③ IG/TikTok 시뮬레이션 댓글(실 IG 는 웹훅 경로)
        if (!Live && _time >= _nextSimCommentAt)
        {
            _nextSimCommentAt = _time + 15 + Rand() * 30;
            var p = Engine.Leg(OmniPlatform.Instagram).Phase == OmniLegPhase.Done && Rand() < 0.5 ? OmniPlatform.Instagram : Engine.Leg(OmniPlatform.TikTok).Phase == OmniLegPhase.Done ? OmniPlatform.TikTok : OmniPlatform.Instagram;
            if (Engine.Leg(p).Phase == OmniLegPhase.Done)
            {
                var k = (int)(Rand() * SimPhrases.Texts.Length);
                var id = (ulong)(_tick * 31 + k) | (ulong)p << 60;
                var slot = Comments.Ingest(p, id, (ulong)k % 9, SimPhrases.Texts[k], _time);
                if (slot >= 0) { WriteRawId(slot, id); Comments.SynthesizeReply(0, Scene(), Tension(), Guard); }
            }
        }
        // ④ 자동 전송(가장 오래된 Suggested 1 건)
        if (AutoSend)
            for (var i = Comments.Count - 1; i >= 0; i--)
                if (Comments.Comment(i).Reply == ReplyState.Suggested) { if (TrySend(i) == 0) return; break; }
    }

    private void WriteRawId(int slot, ulong id)
    {
        System.Buffers.Text.Utf8Formatter.TryFormat(id, _rawIds.AsSpan(slot * IdCap, IdCap), out var w);
        _rawLen[slot] = w;
    }

    private int TrySend(int i)
    {
        if (i < 0 || i >= Comments.Count) return 3;
        ref readonly var c = ref Comments.Comment(i);
        if (c.Reply is not (ReplyState.Suggested or ReplyState.Waiting) || c.ReplyLen == 0) return 3;
        if (c.Platform == OmniPlatform.TikTok) { Comments.SetReplyState(i, ReplyState.Manual); ManualQueued++; return 3; }
        if (_cmt.InFlight) return 3;
        var reply = Comments.ReplyText(i);
        var g = Guard.CheckReply(reply, _time);
        if (g != 0) { Comments.SetReplyState(i, g == 1 ? ReplyState.BlockedSimilar : ReplyState.Waiting); return g; }
        if (c.Platform == OmniPlatform.YouTube && !Guard.TrySpendYouTube(OmniPlatformQuotaSpamSafetyGuard.YtCommentCost, _time)) { Comments.SetReplyState(i, ReplyState.Waiting); return 2; }
        if (!IssueComment(c.Platform, Comments.Slot(i), reply)) return 3;
        Guard.CommitReply(reply, _time);
        Comments.SetReplyState(i, ReplyState.Sent);
        RepliesSent++;
        _pendingSlot = Comments.Slot(i); _pendingKind = 2;
        return 0;
    }

    private int AuthHeader(OmniPlatform p, Span<byte> hdr)
    {
        if (p == OmniPlatform.Instagram) return _bridge?.Guard.WriteAuthHeader(hdr, false) ?? 0;
        var n = 0;
        GraphBytes.Put(hdr, ref n, "Authorization: Bearer "u8);
        n += (p == OmniPlatform.YouTube ? Guard.YouTubeToken : Guard.TikTokToken).CopyTo(hdr[n..]);
        GraphBytes.Put(hdr, ref n, "\r\n"u8);
        return n;
    }

    /// <summary>댓글/답글 POST — parentSlot &lt; 0 이면 미디어 최상위 댓글(고정 훅).</summary>
    private bool IssueComment(OmniPlatform p, int parentSlot, ReadOnlySpan<char> text)
    {
        var url = _cmt.Url.AsSpan(); var n = 0; var b = _body.AsSpan(); var m = 0;
        var h = AuthHeader(p, _cmt.Hdr);
        if (p == OmniPlatform.YouTube)
        {
            if (parentSlot < 0)
            {
                GraphBytes.Put(url, ref n, "https://www.googleapis.com/youtube/v3/commentThreads?part=snippet"u8);
                OmniMetadata.Raw(b, ref m, "{\"snippet\":{\"videoId\":\""u8); OmniMetadata.Raw(b, ref m, Engine.PlatformId(OmniPlatform.YouTube));
                OmniMetadata.Raw(b, ref m, "\",\"topLevelComment\":{\"snippet\":{\"textOriginal\":"u8); OmniMetadata.Str(b, ref m, text); OmniMetadata.Raw(b, ref m, "}}}}"u8);
            }
            else
            {
                GraphBytes.Put(url, ref n, "https://www.googleapis.com/youtube/v3/comments?part=snippet"u8);
                OmniMetadata.Raw(b, ref m, "{\"snippet\":{\"parentId\":\""u8); OmniMetadata.Raw(b, ref m, _rawIds.AsSpan(parentSlot * IdCap, _rawLen[parentSlot]));
                OmniMetadata.Raw(b, ref m, "\",\"textOriginal\":"u8); OmniMetadata.Str(b, ref m, text); OmniMetadata.Raw(b, ref m, "}}"u8);
            }
            GraphBytes.Put(_cmt.Hdr, ref h, "Content-Type: application/json; charset=UTF-8\r\n"u8);
        }
        else
        {
            GraphBytes.Put(url, ref n, RealtimeMetaGraphApiIngestionEngine.GraphHost);
            if (parentSlot < 0) { GraphBytes.Put(url, ref n, Engine.PlatformId(OmniPlatform.Instagram)); GraphBytes.Put(url, ref n, "/comments"u8); }
            else { GraphBytes.Put(url, ref n, _rawIds.AsSpan(parentSlot * IdCap, _rawLen[parentSlot])); GraphBytes.Put(url, ref n, "/replies"u8); }
            OmniMetadata.Raw(b, ref m, "message="u8);
            Span<byte> u = stackalloc byte[AiAudienceCommentInteractionSynthesizer.TextCap * 4];
            var k = System.Text.Encoding.UTF8.GetBytes(text, u);
            GraphBytes.PutEncoded(b, ref m, u[..k]);
            GraphBytes.Put(_cmt.Hdr, ref h, "Content-Type: application/x-www-form-urlencoded\r\n"u8);
        }
        _cmt.UrlLen = n; _cmt.HdrLen = h; _cmt.Method = GraphHttpMethod.Post; _cmt.Body = _body.AsMemory(0, m);
        _cmt.Send(Transport, default);
        if (_cmt.InFlight && _cmt.TryComplete(out var st)) { var kind = _pendingKind; _pendingKind = parentSlot < 0 ? 10 + (int)p : 2; Complete(st); _pendingKind = kind; }
        return true;
    }

    private void Complete(int st)
    {
        var ok = st is >= 200 and < 300;
        if (_pendingKind >= 10) { var p = _pendingKind - 10; if (ok) { _hookPosted[p] = true; HooksPosted++; } }
        else if (_pendingKind == 1) { if (ok) ParseYouTubeComments(_cmt.Resp.BodySpan); else PollErrors++; }
        _pendingKind = 0; _pendingSlot = -1;
    }

    /// <summary>commentThreads.list 응답 판독(무할당) — items[].snippet.topLevelComment.{id, snippet.textOriginal, authorChannelId.value}.</summary>
    private void ParseYouTubeComments(ReadOnlySpan<byte> json)
    {
        Span<byte> id = stackalloc byte[IdCap];
        Span<byte> author = stackalloc byte[64];
        int idLen = 0, txtLen = 0, auLen = 0;
        try
        {
            var r = new Utf8JsonReader(json);
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.StartObject && r.CurrentDepth == 2) { idLen = txtLen = auLen = 0; continue; }
                if (r.TokenType == JsonTokenType.EndObject && r.CurrentDepth == 2 && idLen > 0 && txtLen > 0)
                {
                    var hid = Hash(id[..idLen]);
                    var slot = Comments.Ingest(OmniPlatform.YouTube, hid, Hash(author[..auLen]), _txt.AsSpan(0, txtLen), _time);
                    if (slot >= 0)
                    {
                        id[..idLen].CopyTo(_rawIds.AsSpan(slot * IdCap)); _rawLen[slot] = idLen;
                        Comments.SynthesizeReply(0, Scene(), Tension(), Guard);
                    }
                    continue;
                }
                if (r.TokenType != JsonTokenType.PropertyName) continue;
                if (r.CurrentDepth == 5 && r.ValueTextEquals("id"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= IdCap) idLen = r.CopyString(id); }
                else if (r.CurrentDepth == 6 && r.ValueTextEquals("textOriginal"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= _txt.Length) txtLen = r.CopyString(_txt); }
                else if (r.CurrentDepth == 7 && r.ValueTextEquals("value"u8)) { if (r.Read() && r.TokenType == JsonTokenType.String && r.ValueSpan.Length <= author.Length) auLen = r.CopyString(author); }
            }
        }
        catch (JsonException) { PollErrors++; }
    }

    private static ulong Hash(ReadOnlySpan<byte> s) => SimulatedMetaGraphServer.Fnv(14695981039346656037UL, s);

    private double Rand() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return (_rng >> 11) * (1.0 / 9007199254740992.0); }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.OmniChannel" };
            _loop.Start();
        }
        LastAction = "▶ 옴니채널 루프";
        Raise();
    }
    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(5000);
        LastAction = "⏸ 정지";
        Raise();
    }
    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException or ObjectDisposedException) { PromptFlowStudio.Program.Log("OmniChannel.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -2000) { sw.Restart(); n = 0; }
            if (n % 3 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("OmniChannel.Runner.OnChange", ex); }
    }
    public void Dispose()
    {
        Stop();
        lock (_gate) { Engine.CancelAll(); _http?.Dispose(); Guard.Dispose(); }
    }
}
