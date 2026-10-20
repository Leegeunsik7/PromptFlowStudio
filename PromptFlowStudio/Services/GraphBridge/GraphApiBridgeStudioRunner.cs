using System.Globalization;
using PromptFlowStudio.Services.Diagnostics;
namespace PromptFlowStudio.Services.GraphBridge;

public enum GraphLogKind : byte { IngestOk, IngestError, RateLimited, Webhook, WebhookRejected, Chunk, Published, PublishFailed, TokenRefreshed, TokenError }

/// <summary>관제 로그 1건(값 형식) — 문자열은 UI 에서 지연 포맷.</summary>
public struct GraphLogEntry
{
    public double T;
    public GraphLogKind Kind;
    public int Code;
    public long Value;
    public ulong Id;
}

/// <summary>
/// [PromptFlow Studio v17.48.0] 🔗 AI 인스타그램 API 연동 스튜디오 러너(60 Hz 전용 스레드).
/// 매 프레임: 토큰 갱신 요청(필요 시) → 트렌딩 인제스천(해시태그 검색 → top_media ↔ 본인 media 교대, 기본 20 초 간격) → 발행 디스패처 틱 →
/// 시뮬레이션 웹훅(서명 검증) → 쿼터/사용량/진행률 시계열. 모든 Graph 호출은 하나의 가드(토큰 버킷 · 백오프)를 공유한다.
/// 기본은 오프라인 <see cref="SimulatedMetaGraphServer"/>, 실 모드는 자격(IG 사용자 ID · 장기 토큰) 설정 후 <see cref="HttpGraphTransport"/>.
/// 리믹스 발행은 v17.47 오리지널리티 가드가 Ready 일 때만 허용한다.
/// </summary>
public sealed class GraphApiBridgeStudioRunner : IDisposable
{
    public const int Hz = 60, Hist = 300, LogCap = 64;
    public const double Dt = 1.0 / Hz;
    public static ReadOnlySpan<byte> SimToken => "IGSIMTOKEN0001ABCDEFGH"u8;
    public static ReadOnlySpan<byte> SimAppSecret => "sim-app-secret-0001"u8;
    private static ReadOnlySpan<byte> RefreshUrl => "https://graph.instagram.com/refresh_access_token?grant_type=ig_refresh_token&access_token="u8;
    private readonly object _gate = new();
    private readonly float[] _quotaS = new float[Hist], _usageS = new float[Hist], _progS = new float[Hist], _msS = new float[Hist];
    private readonly GraphLogEntry[] _log = new GraphLogEntry[LogCap];
    private readonly GraphCall _ingestCall = new(), _refreshCall = new(), _insightCall = new();
    private ulong _insightWant, _insightAsked;
    private GraphMediaInsights _insights;
    private bool _insightReady;
    private readonly byte[] _whBody = new byte[2048], _whSig = new byte[80];
    private readonly ViralReels.ViralReelsStudioRunner? _reels;
    private HttpGraphTransport? _http;
    private Stream? _src;
    private GraphQueryKind _ingestKind;
    private int _head, _series, _logHead, _logCount, _lastChunks;
    private double _time, _nextIngestAt, _nextRefreshAt, _nextWebhookAt;
    private long _whSeq, _tick;
    private Thread? _loop;
    private volatile bool _running;

    public GraphApiBridgeStudioRunner(IServiceProvider sp) : this() => _reels = sp.GetService(typeof(ViralReels.ViralReelsStudioRunner)) as ViralReels.ViralReelsStudioRunner;
    public GraphApiBridgeStudioRunner()
    {
        Sim = new SimulatedMetaGraphServer(SimToken, SimAppSecret);
        Span<char> tok = stackalloc char[SimToken.Length];
        for (var i = 0; i < tok.Length; i++) tok[i] = (char)SimToken[i];
        Guard.SetToken(tok, 0);
        Span<char> sec = stackalloc char[SimAppSecret.Length];
        for (var i = 0; i < sec.Length; i++) sec[i] = (char)SimAppSecret[i];
        Guard.SetAppSecret(sec);
        IgUserId = 17841400000000001; AppId = 1234567890;
        Dispatcher.PollIntervalSec = 1;
    }

    public event Action? OnChange;
    public RealtimeMetaGraphApiIngestionEngine Ingest { get; } = new();
    public ChunkedReelsPublishingDispatcherSynthesizer Dispatcher { get; } = new();
    public TokenBucketRateLimitOAuthSecurityGuard Guard { get; } = new();
    public SimulatedMetaGraphServer Sim { get; private set; }
    public bool Live { get; private set; }
    public ulong IgUserId { get; private set; }
    public ulong AppId { get; private set; }
    public double IngestIntervalSec { get; set; } = 20;
    public bool SimWebhooks { get; set; } = true;
    public double WebhookIntervalSec { get; set; } = 2;
    public bool ReelsLinked => _reels is not null;
    /// <summary>업로드 소스 스트림 보유 여부(v17.50 누수 진단).</summary>
    public bool HoldsUploadStream { get { lock (_gate) return _src is not null; } }
    public double Now => _time;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public double MaxTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int SeriesCount => _series;
    public float QuotaSeries(int i) => _quotaS[Ix(i)];
    public float UsageSeries(int i) => _usageS[Ix(i)];
    public float ProgressSeries(int i) => _progS[Ix(i)];
    public float TickMsSeries(int i) => _msS[Ix(i)];
    private int Ix(int i) => (_head - 1 - i + 2 * Hist) % Hist;
    public int LogCount => _logCount;
    /// <summary>i = 0 이 최신.</summary>
    public GraphLogEntry Log(int i) { lock (_gate) return _log[(_logHead - 1 - i + 2 * LogCap) % LogCap]; }
    private IGraphTransport Transport => Live && _http is not null ? _http : Sim;

    // ══════════════════════ 제어(UI · 할당 허용) ══════════════════════
    /// <summary>실 자격 적용 — 토큰/시크릿은 가드 암호 슬롯으로만 보관. 시뮬레이션 모드면 시뮬레이터도 같은 자격으로 재키잉.</summary>
    public bool SetCredentials(string appId, string igUserId, string token, string appSecret, double tokenDaysLeft = 60)
    {
        if (!ulong.TryParse(igUserId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid == 0) { LastAction = "⚠ IG 사용자 ID 형식 오류"; Raise(); return false; }
        ulong.TryParse(appId.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var aid);
        var tok = token.AsSpan().Trim();
        if (tok.Length < 16 || tok.Length > GraphSecretSlot.Cap / 2) { LastAction = "⚠ 토큰 길이 오류"; Raise(); return false; }
        lock (_gate)
        {
            IgUserId = uid; AppId = aid;
            Guard.SetToken(tok, _time, Math.Clamp(tokenDaysLeft, 0, 60) * TokenBucketRateLimitOAuthSecurityGuard.DaySec);
            if (appSecret.Length > 0) Guard.SetAppSecret(appSecret.AsSpan().Trim());
            if (!Live) RekeySim();
        }
        LastAction = "🔐 자격 적용 · 토큰 " + Guard.TokenMasked;
        Raise();
        return true;
    }

    private void RekeySim()
    {
        Span<byte> t = stackalloc byte[GraphSecretSlot.Cap];
        Span<byte> k = stackalloc byte[GraphSecretSlot.Cap];
        try
        {
            var tl = Guard.Token.CopyTo(t); var kl = Guard.AppSecret.CopyTo(k);
            Sim = new SimulatedMetaGraphServer(t[..tl], k[..kl]);
        }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(t); System.Security.Cryptography.CryptographicOperations.ZeroMemory(k); }
    }

    public bool SetLive(bool on)
    {
        lock (_gate)
        {
            if (on && (!Guard.Token.IsSet || IgUserId == 0)) { LastAction = "⚠ 실 모드: 자격 미설정"; Raise(); return false; }
            if (Dispatcher.IsActive) { LastAction = "⚠ 발행 진행 중 — 모드 전환 불가"; Raise(); return false; }
            Live = on;
            if (on) _http ??= new HttpGraphTransport();
            Ingest.HashtagId = 0;
        }
        LastAction = on ? "🌐 실 Graph API 모드" : "🧪 시뮬레이션 모드";
        Raise();
        return true;
    }

    public bool SetHashtag(string tag)
    {
        Span<byte> b = stackalloc byte[RealtimeMetaGraphApiIngestionEngine.TagCap * 4];
        var t = tag.AsSpan().Trim().TrimStart('#');
        if (t.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(t) > RealtimeMetaGraphApiIngestionEngine.TagCap) return false;
        var n = System.Text.Encoding.UTF8.GetBytes(t, b);
        bool ok;
        lock (_gate) { ok = Ingest.SetHashtag(b[..n]); if (ok) _nextIngestAt = _time; }
        LastAction = ok ? "#️⃣ 해시태그 #" + t.ToString() : "⚠ 해시태그 형식 오류";
        Raise();
        return ok;
    }

    public void IngestNow() { lock (_gate) _nextIngestAt = _time; }

    /// <summary>v17.50 가속 시뮬레이션 결합 — 브릿지 시계(토큰 버킷 · 백오프 · 폴링)를 시뮬레이션 시간만큼 진행. 실 모드 무시.</summary>
    public void AdvanceClock(double seconds) { lock (_gate) { if (Live || seconds <= 0) return; _time += seconds; Sim.Now = _time; } }
    public void RefreshTokenNow() { lock (_gate) { _nextRefreshAt = _time; Guard.RefreshThresholdSec = double.MaxValue; } }

    /// <summary>v17.47 리믹스(시뮬레이션 MP4 스트림) 발행 — 실 모드에서는 렌더 파일 경로를 사용해야 한다.</summary>
    public bool PublishRemix(long bytes = 18L * 1024 * 1024 + 4321, string? caption = null)
    {
        if (Live) { LastAction = "⚠ 실 모드: 렌더된 MP4 파일 경로로 발행하세요"; Raise(); return false; }
        if (!OriginalityOk(out var why)) { LastAction = why; Raise(); return false; }
        var frame = new byte[ViralReels.ViralReelsStudioRunner.BmpBytes];
        _reels?.WriteBmp(frame, true);
        return Begin(new SyntheticReelVideoStream(bytes, frame), caption ?? _reels?.BuildCaption() ?? "PromptFlow Studio remix #reels");
    }

    /// <summary>v17.49 트랜스코더 직결 — 인코딩 완료 MP4 스트림(소유권 이전) 발행. 실 모드 허용(실제 H.264/AAC).</summary>
    public bool PublishEncoded(Stream encoded, string caption)
    {
        if (!OriginalityOk(out var why)) { encoded.Dispose(); LastAction = why; Raise(); return false; }
        return Begin(encoded, caption);
    }

    public bool PublishFile(string path, string caption)
    {
        if (!OriginalityOk(out var why)) { LastAction = why; Raise(); return false; }
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { LastAction = "⚠ 파일 열기 실패"; Raise(); return false; }
        return Begin(fs, caption);
    }

    private bool OriginalityOk(out string why)
    {
        why = "";
        if (_reels is null || _reels.Guard.Verdict == ViralReels.ReelsPublishVerdict.Ready) return true;
        why = "⛔ v17.47 오리지널리티 가드 미통과: " + ViralReels.ReelsOriginalityAntiShadowbanGuard.VerdictName(_reels.Guard.Verdict);
        return false;
    }

    private bool Begin(Stream src, string caption)
    {
        bool ok;
        lock (_gate)
        {
            if (!Mp4Probe.TryProbe(src, out var spec)) { src.Dispose(); LastAction = "⚠ MP4 헤더 판독 실패"; Raise(); return false; }
            _src?.Dispose();
            _src = src;
            _lastChunks = 0;
            ok = Dispatcher.Begin(src, in spec, caption, true, _time, Guard);
        }
        LastAction = ok ? "🚀 발행 시작 · " + (Dispatcher.FileSize / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB · 청크 " + Dispatcher.TotalChunks.ToString(CultureInfo.InvariantCulture) : "⚠ " + Dispatcher.LastError;
        Raise();
        return ok;
    }

    public void CancelPublish() { lock (_gate) Dispatcher.Cancel(); LastAction = "⏹ 발행 취소"; Raise(); }

    // ══════════════════════ 프레임(무할당) ══════════════════════
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("GraphBridge.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _tick);
            _time += Dt;
            Sim.Now = _time;
            var t = Transport;
            StepRefresh(t);
            StepIngest(t);
            StepInsights(t);
            var phase = Dispatcher.Phase;
            Dispatcher.Step(_time, IgUserId, t, Guard);
            if (Dispatcher.ChunksSent != _lastChunks) { _lastChunks = Dispatcher.ChunksSent; Add(GraphLogKind.Chunk, Dispatcher.LastStatus, Dispatcher.Offset, Dispatcher.ContainerId); }
            if (phase != Dispatcher.Phase)
            {
                if (Dispatcher.Phase == ReelsPublishPhase.Published) Add(GraphLogKind.Published, 200, Dispatcher.FileSize, Dispatcher.MediaId);
                else if (Dispatcher.Phase == ReelsPublishPhase.Failed) Add(GraphLogKind.PublishFailed, Dispatcher.LastStatus, 0, Dispatcher.ContainerId);
                if (!Dispatcher.IsActive && _src is not null) { _src.Dispose(); _src = null; } // v17.50: 종료 즉시 업로드 스트림(4 MB 세그먼트) 반환
            }
            if (!Live && SimWebhooks && _time >= _nextWebhookAt)
            {
                _nextWebhookAt = _time + WebhookIntervalSec;
                var n = Sim.WriteWebhook(_whBody, _whSig, ++_whSeq);
                var k = Ingest.IngestWebhook(_whBody.AsSpan(0, n), _whSig.AsSpan(0, 71), Guard);
                if (k > 0) { ref readonly var ev = ref Ingest.Event(0); Add(GraphLogKind.Webhook, (int)ev.Field, (long)ev.ItemId, ev.MediaId); }
                else Add(GraphLogKind.WebhookRejected, 0, _whSeq, 0);
            }
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            _quotaS[_head] = (float)Guard.QuotaPct; _usageS[_head] = Guard.UsagePct; _progS[_head] = (float)Dispatcher.Progress; _msS[_head] = ms;
            _head = (_head + 1) % Hist;
            if (_series < Hist) _series++;
            LastTickMs = ms;
            if (ms > MaxTickMs && _tick > 10) MaxTickMs = ms;
        }
    }

    /// <summary>웹훅 외부 주입(검증 실패 시 −1).</summary>
    /// <summary>v17.50 — 미디어 인사이트 요청(1 슬롯 · 가드 토큰 버킷 경유). 같은 ID 재요청은 무시.</summary>
    public void RequestInsights(ulong mediaId) { lock (_gate) { if (_insightWant == mediaId) return; _insightWant = mediaId; _insightReady = false; _insightAsked = 0; } }

    /// <summary>요청한 인사이트 수신 시 true(1 회 소비).</summary>
    public bool TryTakeInsights(ulong mediaId, out GraphMediaInsights m)
    {
        lock (_gate)
        {
            m = _insights;
            if (!_insightReady || _insights.MediaId != mediaId) return false;
            _insightReady = false; _insightWant = 0; _insightAsked = 0;
            return true;
        }
    }

    public int InjectWebhook(ReadOnlySpan<byte> body, ReadOnlySpan<byte> sig)
    {
        lock (_gate)
        {
            var k = Ingest.IngestWebhook(body, sig, Guard);
            if (k < 0) Add(GraphLogKind.WebhookRejected, 0, 0, 0);
            return k;
        }
    }

    private void StepRefresh(IGraphTransport t)
    {
        if (_refreshCall.InFlight)
        {
            if (!_refreshCall.TryComplete(out var st)) return;
            var o = Guard.Observe(st, _time, _refreshCall.Resp);
            if (o == GraphOutcome.Ok && Guard.ApplyRefresh(_refreshCall.Resp.BodySpan, _time))
            {
                Guard.RefreshThresholdSec = 60 * TokenBucketRateLimitOAuthSecurityGuard.DaySec;
                Add(GraphLogKind.TokenRefreshed, st, (long)Guard.TokenDaysLeft(_time), 0);
            }
            else { _nextRefreshAt = _time + 3600; Add(GraphLogKind.TokenError, st, 0, 0); }
            CryptographicClear(_refreshCall.Resp);
            return;
        }
        if (_time < _nextRefreshAt || !Guard.NeedsRefresh(_time) && Guard.RefreshThresholdSec != double.MaxValue) return;
        if (!Guard.Token.IsSet || Guard.TokenInvalid || !Guard.TryAcquire(_time)) return;
        var url = _refreshCall.Url.AsSpan();
        var n = 0;
        GraphBytes.Put(url, ref n, RefreshUrl);
        n += Guard.Token.CopyTo(url[n..]);
        _refreshCall.UrlLen = n; _refreshCall.UrlHasSecret = true; _refreshCall.HdrLen = 0;
        _refreshCall.Method = GraphHttpMethod.Get; _refreshCall.Body = default;
        _refreshCall.Send(t, default);
        if (_refreshCall.InFlight) StepRefresh(t);
    }

    private static void CryptographicClear(GraphResponseBuffer r) => System.Security.Cryptography.CryptographicOperations.ZeroMemory(r.Body.AsSpan(0, r.Length));

    private void StepInsights(IGraphTransport t)
    {
        if (_insightCall.InFlight)
        {
            if (!_insightCall.TryComplete(out var st)) return;
            var o = Guard.Observe(st, _time, _insightCall.Resp);
            if (o == GraphOutcome.Ok && RealtimeMetaGraphApiIngestionEngine.ParseInsights(_insightCall.Resp.BodySpan, _insightAsked, out var m) && m.MediaId == _insightWant) { _insights = m; _insightReady = true; }
            else _insightAsked = 0; // 재시도(백오프는 가드)
            return;
        }
        if (_insightWant == 0 || _insightReady || _insightAsked == _insightWant || Guard.TokenInvalid || Guard.IsBlocked(_time) || !Guard.TryAcquire(_time)) return;
        _insightAsked = _insightWant;
        _insightCall.UrlLen = RealtimeMetaGraphApiIngestionEngine.BuildInsightsUrl(_insightWant, _insightCall.Url);
        _insightCall.HdrLen = Guard.WriteAuthHeader(_insightCall.Hdr, false);
        _insightCall.Method = GraphHttpMethod.Get; _insightCall.Body = default;
        _insightCall.Send(t, default);
        if (_insightCall.InFlight) StepInsights(t);
    }

    private void StepIngest(IGraphTransport t)
    {
        if (_ingestCall.InFlight)
        {
            if (!_ingestCall.TryComplete(out var st)) return;
            var o = Guard.Observe(st, _time, _ingestCall.Resp);
            var body = _ingestCall.Resp.BodySpan;
            if (o == GraphOutcome.Ok)
            {
                var k = _ingestKind == GraphQueryKind.HashtagSearch ? (Ingest.ParseHashtagSearch(body) ? 1 : -1) : Ingest.ParseMedia(body, _ingestKind == GraphQueryKind.OwnMedia);
                Add(k >= 0 ? GraphLogKind.IngestOk : GraphLogKind.IngestError, st, k, _ingestKind == GraphQueryKind.HashtagSearch ? Ingest.HashtagId : 0);
                _nextIngestAt = _ingestKind == GraphQueryKind.HashtagSearch ? _time : _time + IngestIntervalSec;
                if (_ingestKind != GraphQueryKind.HashtagSearch) _ingestKind = _ingestKind == GraphQueryKind.TopMedia ? GraphQueryKind.OwnMedia : GraphQueryKind.TopMedia;
            }
            else
            {
                TokenBucketRateLimitOAuthSecurityGuard.TryReadError(body, out var code, out _);
                Add(o == GraphOutcome.RateLimited ? GraphLogKind.RateLimited : GraphLogKind.IngestError, st, code, 0);
                _nextIngestAt = Math.Max(_time + (o == GraphOutcome.Fatal || o == GraphOutcome.AuthError ? IngestIntervalSec : 0), Guard.BlockedUntil);
            }
            return;
        }
        if (_time < _nextIngestAt || IgUserId == 0 || Guard.TokenInvalid || Guard.IsBlocked(_time)) return;
        if (Dispatcher.IsActive && Dispatcher.Phase != ReelsPublishPhase.Uploading && Guard.Tokens < 4) return; // 발행 우선
        if (!Guard.TryAcquire(_time)) { _nextIngestAt = _time + Guard.WaitSeconds(_time); return; }
        var q = Ingest.HashtagId == 0 ? GraphQueryKind.HashtagSearch : _ingestKind == GraphQueryKind.HashtagSearch ? GraphQueryKind.TopMedia : _ingestKind;
        _ingestKind = q;
        _ingestCall.UrlLen = Ingest.BuildUrl(q, IgUserId, _ingestCall.Url);
        _ingestCall.HdrLen = Guard.WriteAuthHeader(_ingestCall.Hdr, false);
        _ingestCall.Method = GraphHttpMethod.Get; _ingestCall.Body = default;
        _ingestCall.Send(t, default);
        if (_ingestCall.InFlight) StepIngest(t);
    }

    private void Add(GraphLogKind k, int code, long value, ulong id)
    {
        ref var e = ref _log[_logHead];
        e.T = _time; e.Kind = k; e.Code = code; e.Value = value; e.Id = id;
        _logHead = (_logHead + 1) % LogCap;
        if (_logCount < LogCap) _logCount++;
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.GraphBridge" };
            _loop.Start();
        }
        LastAction = Live ? "▶ Graph API 브릿지 60 Hz(실)" : "▶ Graph API 브릿지 60 Hz(시뮬레이션)";
        Raise();
    }
    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
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
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException or ObjectDisposedException) { PromptFlowStudio.Program.Log("GraphBridge.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }
            if (n % 6 == 0) Raise();
        }
    }
    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("GraphBridge.Runner.OnChange", ex); }
    }
    public void Dispose()
    {
        Stop();
        lock (_gate) { Dispatcher.Cancel(); _src?.Dispose(); _src = null; _http?.Dispose(); Guard.Dispose(); }
    }
}
