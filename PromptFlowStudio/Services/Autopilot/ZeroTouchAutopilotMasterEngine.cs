using System.Globalization;
using PromptFlowStudio.Services.LiveToReels;
using PromptFlowStudio.Services.Publisher;
using PromptFlowStudio.Services.SemanticChat;
using PromptFlowStudio.Services.Showrunner;
using PromptFlowStudio.Services.StationMaster;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.Autopilot;

public enum AutopilotStage : byte { Idle, Planning, Broadcasting, Reacting, Clipping, BurningIn, Publishing, Notifying, Cooldown }

/// <summary>v17.73.0 번-인 완료 릴(재시도 업로드용 보관 · 링 8).</summary>
public sealed class BurnedReel : IDisposable
{
    public int ReelId { get; init; }
    public SegmentedChunkStream Media { get; init; } = null!;
    public SocialPostMetadata Meta { get; init; } = null!;
    public int Frames { get; init; }
    public double BurnMs { get; init; }
    public string Backend { get; init; } = "";
    public int PublishedMask { get; internal set; }
    public void Dispose() => Media.Dispose();
}

/// <summary>
/// [PromptFlow Studio v17.73.0] 🤖 24/7 제로-터치 자율 오토파일럿 마스터 엔진(폐루프 FSM · 10 Hz 틱 무할당 · 무거운 단계 = 백그라운드 태스크).
///   ▸ 순환: Planning(v17.71 편성 추천 → v17.70 즉시 편성) → Broadcasting(v17.70 방송국 · v17.66 MCR · v17.72 시맨틱 날씨 · v17.69 클라이맥스 감지 가동) →
///     Reacting(v17.67 피격 · v17.72 채팅 감성 → 클라이맥스 릴 대기 · 타임아웃 시 수동 하이라이트) → Clipping(무손실 릴 패키지) →
///     BurningIn(v17.71 AI 제목/설명 → <see cref="HardwareSubtitleBurnInEngine"/> GPU 하드서브 재인코딩) →
///     Publishing(<see cref="SmartPlatformQuotaResilienceGuard"/> 라우팅 → YT/IG 게이트웨이 · TikTok v17.69 업로드 가드 · 실패 = 로컬 재시도 큐) →
///     Notifying(<see cref="RemoteCreatorTelemetryDispatcher"/> 웹훅) → Cooldown → Planning.
///   ▸ 감시(매 틱): v17.70 페일오버 진입/복구 · v17.67 쇼러너 비상 개입 증가 → 비동기 알림. 재시도 큐 도래 항목 → 백그라운드 재업로드.
///   ▸ 장애 내성: 단계 태스크 예외/타임아웃 → [부분 구제] 후 Cooldown(자가 치유) · 방송 스레드는 결코 대기하지 않음(쿼터 오류 = 큐 격리만).
/// </summary>
public sealed class ZeroTouchAutopilotMasterEngine : IDisposable
{
    public const int Stages = 9, BurnRing = 8, LogCap = 24, RetryBatch = 4;

    private readonly MasterStationNetworkStudioRunner? _station;
    private readonly LiveToReelsFactoryStudioRunner? _reels;
    private readonly OmniChannelPublisherStudioRunner? _pub;
    private readonly SemanticChatWeatherStudioRunner? _chat;
    private readonly InteractiveShowrunnerStudioRunner? _showrunner;
    private readonly BurnedReel?[] _burned = new BurnedReel?[BurnRing];
    private readonly long[] _stageEnter = new long[Stages], _stageCount = new long[Stages];
    private readonly double[] _stageMs = new double[Stages];
    private readonly QuotaRetryItem[] _due = new QuotaRetryItem[RetryBatch];
    private readonly (long At, AutopilotStage Stage, string Text)[] _log = new (long, AutopilotStage, string)[LogCap];
    private int _burnHead, _logHead, _logN, _reelId, _lastSeenReel;
    private long _enteredAt, _lastAdlibs = -1;
    private bool _standbySeen;
    private Task? _task;
    private Task? _retryTask;

    public ZeroTouchAutopilotMasterEngine(MasterStationNetworkStudioRunner? station, LiveToReelsFactoryStudioRunner? reels, OmniChannelPublisherStudioRunner? pub,
        SemanticChatWeatherStudioRunner? chat, InteractiveShowrunnerStudioRunner? showrunner, HardwareSubtitleBurnInEngine burnIn, SmartPlatformQuotaResilienceGuard quota, RemoteCreatorTelemetryDispatcher telemetry)
    {
        _station = station; _reels = reels; _pub = pub; _chat = chat; _showrunner = showrunner;
        BurnIn = burnIn; Quota = quota; Telemetry = telemetry;
    }

    public HardwareSubtitleBurnInEngine BurnIn { get; }
    public SmartPlatformQuotaResilienceGuard Quota { get; }
    public RemoteCreatorTelemetryDispatcher Telemetry { get; }
    public McrProgramFrameSource PgmSource { get; } = new();
    public GradientFrameSource Fallback { get; } = new();
    public Func<long> Clock { get; set; } = static () => Environment.TickCount64;
    public bool Enabled { get; private set; }
    public AutopilotStage Stage { get; private set; }
    public long Cycles { get; private set; }
    public long Ticks { get; private set; }
    public long Faults { get; private set; }
    public long Published { get; private set; }
    public long RetryUploads { get; private set; }
    public long ForcedClips { get; private set; }
    public int CurrentReel => _reelId;
    public string? LastError { get; private set; }
    public string? LastAction { get; private set; }
    public double LastTickUs { get; private set; }
    public double MaxTickUs { get; private set; }
    /// <summary>요청 플랫폼(비트 0 = YouTube · 1 = Instagram · 2 = TikTok) · 우회 대상.</summary>
    public int RequestMask { get; set; } = 0b011;
    public int RerouteMask { get; set; } = 0b100;
    public long BroadcastMinMs { get; set; } = 60_000;
    public long ReactTimeoutMs { get; set; } = 180_000;
    public long ClipTimeoutMs { get; set; } = 30_000;
    public long BurnTimeoutMs { get; set; } = 120_000;
    public long PublishTimeoutMs { get; set; } = 300_000;
    public long CooldownMs { get; set; } = 30_000;
    public double MaxBurnSec { get; set; } = 15;
    public int BurnFps { get; set; } = 30;
    public bool AllowMediaFoundation { get; set; } = true;
    public long StageEntries(AutopilotStage s) => _stageCount[(int)s];
    public double StageLastMs(AutopilotStage s) => _stageMs[(int)s];
    public double StageAgeMs => Clock() - _enteredAt;
    public int LogCount => _logN;
    public (long At, AutopilotStage Stage, string Text) Log(int i) => _log[(_logHead - 1 - i + 2 * LogCap) % LogCap];
    public BurnedReel? Burned(int reelId) { foreach (var b in _burned) if (b?.ReelId == reelId) return b; return null; }
    public bool TaskRunning => _task is { IsCompleted: false };

    public static string StageName(AutopilotStage s) => s switch
    {
        AutopilotStage.Planning => "기획 · 편성", AutopilotStage.Broadcasting => "3DGS 방송", AutopilotStage.Reacting => "시청자 반응", AutopilotStage.Clipping => "클라이맥스 클립",
        AutopilotStage.BurningIn => "GPU 자막 번-인", AutopilotStage.Publishing => "옴니채널 업로드", AutopilotStage.Notifying => "웹훅 알림", AutopilotStage.Cooldown => "쿨다운", _ => "대기",
    };
    public static string StageIcon(AutopilotStage s) => s switch
    {
        AutopilotStage.Planning => "🧭", AutopilotStage.Broadcasting => "📡", AutopilotStage.Reacting => "💥", AutopilotStage.Clipping => "✂️", AutopilotStage.BurningIn => "🔥",
        AutopilotStage.Publishing => "🚀", AutopilotStage.Notifying => "📣", AutopilotStage.Cooldown => "⏳", _ => "⏸",
    };

    // ══════════════════════ 제어(비핫패스) ══════════════════════
    public void Enable()
    {
        if (Enabled) return;
        Enabled = true; Telemetry.Start();
        _lastSeenReel = LatestReelId();
        Enter(AutopilotStage.Planning, "오토파일럿 가동");
        Telemetry.Publish(CreatorAlertKind.AutopilotStage, AlertSeverity.Info, "제로-터치 오토파일럿 가동");
    }

    public void Disable()
    {
        if (!Enabled) return;
        Enabled = false;
        Enter(AutopilotStage.Idle, "오토파일럿 정지");
        Telemetry.Publish(CreatorAlertKind.AutopilotStage, AlertSeverity.Warning, "제로-터치 오토파일럿 정지");
    }

    private int LatestReelId() { var r = _reels?.Reel(0); return r?.Id ?? 0; }

    private void Enter(AutopilotStage s, string why)
    {
        var now = Clock();
        _stageMs[(int)Stage] = now - _enteredAt;
        Stage = s; _enteredAt = now; _stageEnter[(int)s] = now; _stageCount[(int)s]++;
        LastAction = StageIcon(s) + " " + StageName(s) + " · " + why;
        _log[_logHead] = (now, s, why); _logHead = (_logHead + 1) % LogCap; if (_logN < LogCap) _logN++;
    }

    // ══════════════════════ 10 Hz 틱(무할당 — 전이 순간 제외) ══════════════════════
    public void Tick()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Ticks++;
        Watch();
        if (Enabled)
        {
            try { Advance(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or ObjectDisposedException) { Fault("틱 예외: " + ex.Message); }
            PumpRetries();
        }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastTickUs = us; if (us > MaxTickUs && Ticks > 3 && _task is null) MaxTickUs = us;
    }

    /// <summary>방송 이벤트 감시 — 페일오버 진입/복구 · 쇼러너 비상 애드리브.</summary>
    private void Watch()
    {
        if (_station is not null)
        {
            var sb = _station.Failover.StandbyEngaged;
            if (sb != _standbySeen)
            {
                _standbySeen = sb;
                if (sb) Telemetry.Publish(CreatorAlertKind.Failover, AlertSeverity.Critical, "MCR 스탠바이 루프 송출 — 원인: 페일오버 가드 감지");
                else Telemetry.Publish(CreatorAlertKind.Recovered, AlertSeverity.Success, "방송 정상 복귀");
            }
        }
        if (_showrunner is not null)
        {
            var n = _showrunner.Showrunner.Interventions;
            if (_lastAdlibs >= 0 && n > _lastAdlibs) Telemetry.Publish(CreatorAlertKind.EmergencyAdlib, AlertSeverity.Warning, "쇼러너 비상 애드리브 개입", n);
            _lastAdlibs = n;
        }
    }

    private void Advance()
    {
        var age = Clock() - _enteredAt;
        switch (Stage)
        {
            case AutopilotStage.Planning:
                Plan();
                Enter(AutopilotStage.Broadcasting, "편성 확정");
                break;
            case AutopilotStage.Broadcasting:
                if (age >= BroadcastMinMs || NewReel() > 0) Enter(AutopilotStage.Reacting, "클라이맥스 감시");
                break;
            case AutopilotStage.Reacting:
            {
                var id = NewReel();
                if (id > 0) { _reelId = id; Enter(AutopilotStage.Clipping, "릴 #" + id.ToString(CultureInfo.InvariantCulture) + " 감지"); }
                else if (age >= ReactTimeoutMs)
                {
                    ForcedClips++;
                    if (_reels is not null && _reels.ManualClip(20)) Enter(AutopilotStage.Clipping, "반응 타임아웃 → 수동 하이라이트");
                    else Fault("클립 요청 실패");
                }
                break;
            }
            case AutopilotStage.Clipping:
            {
                if (_reelId == 0) { var id = NewReel(); if (id > 0) _reelId = id; }
                var r = _reelId > 0 ? _reels?.FindReel(_reelId) : null;
                if (r is { Media: not null, Phase: not (FactoryReelPhase.Clipping or FactoryReelPhase.Packaging) }) { StartBurn(r); Enter(AutopilotStage.BurningIn, "릴 #" + r.Id.ToString(CultureInfo.InvariantCulture) + " 자막 번-인"); }
                else if (r?.Phase == FactoryReelPhase.Failed || age >= ClipTimeoutMs) Fault("클립 실패/타임아웃");
                break;
            }
            case AutopilotStage.BurningIn:
                if (_task is { IsCompleted: true } bt)
                {
                    if (bt.IsFaulted || Burned(_reelId) is null) { Fault("번-인 실패: " + (bt.Exception?.InnerException?.Message ?? LastError)); break; }
                    StartPublish(Burned(_reelId)!);
                    Enter(AutopilotStage.Publishing, "쿼터 라우팅 · 업로드");
                }
                else if (age >= BurnTimeoutMs) Fault("번-인 타임아웃");
                break;
            case AutopilotStage.Publishing:
                if (_task is { IsCompleted: true } pt) { if (pt.IsFaulted) LastError = pt.Exception?.InnerException?.Message; Enter(AutopilotStage.Notifying, "업로드 결과 알림"); }
                else if (age >= PublishTimeoutMs) Fault("업로드 타임아웃");
                break;
            case AutopilotStage.Notifying:
                _task = null;
                Enter(AutopilotStage.Cooldown, "다음 사이클 대기");
                break;
            case AutopilotStage.Cooldown:
                if (age >= CooldownMs) { Cycles++; _reelId = 0; Enter(AutopilotStage.Planning, "사이클 " + Cycles.ToString(CultureInfo.InvariantCulture) + " 완료"); }
                break;
        }
    }

    private int NewReel()
    {
        if (_reels is null) return 0;
        for (var i = _reels.ReelCount - 1; i >= 0; i--)
        {
            var r = _reels.Reel(i);
            if (r is null || r.Id <= _lastSeenReel) continue;
            _lastSeenReel = r.Id;
            return r.Id;
        }
        return 0;
    }

    private void Fault(string why)
    {
        Faults++; LastError = why; _task = null;
        Telemetry.Publish(CreatorAlertKind.AutopilotStage, AlertSeverity.Warning, why);
        Enter(AutopilotStage.Cooldown, "⚠ " + why);
    }

    /// <summary>v17.71 추천 → v17.70 즉시 편성 · 방송국/릴스/시맨틱 가동 보장.</summary>
    private void Plan()
    {
        if (_pub is not null && _station is not null)
        {
            var n = _pub.Recommender.Recommend(DateTime.Now.Hour);
            if (n > 0) _station.CueProgram(_pub.Recommender.Recommendation(0).Kind);
        }
        if (_station is { IsRunning: false } st) st.Start();
        if (_reels is { IsRunning: false } rs) { rs.SetAutoPublish(false); rs.Start(); }
        if (_chat is { Attached: false } ch) ch.Attach();
    }

    // ══════════════════════ 백그라운드 단계 ══════════════════════
    private void StartBurn(FactoryReel reel)
    {
        // PGM 스냅숏(방송 화면) — MCR 미가동이면 그라데이션 폴백
        IBurnInFrameSource src = Fallback;
        if (_station is { } s && s.Mcr.Mcr.VideoFrames > 0) { PgmSource.Capture(s.Mcr.Mcr.ProgramOut); src = PgmSource; }
        var dur = Math.Clamp(reel.DurationSec > 0 ? reel.DurationSec : reel.Request.Seconds, 3, MaxBurnSec);
        var id = reel.Id;
        _task = Task.Run(async () =>
        {
            var meta = new SocialPostMetadata();
            if (_pub is not null)
            {
                var d = await _pub.GenerateDraftAsync(id, _pub.ContextFor(reel)).ConfigureAwait(false);
                meta = d.Meta;
            }
            else { meta.Title = reel.Caption + " #Shorts"; meta.Description = reel.Caption; }
            var output = new SegmentedChunkStream();
            int frames;
            lock (BurnIn)
            {
                BurnIn.SetCaptionTrack((meta.Title + ". " + meta.Description).AsSpan(), (float)(dur * 1000));
                frames = BurnIn.BurnReel(src, dur, BurnFps, output, 6000, AllowMediaFoundation);
            }
            if (frames == 0) { output.Dispose(); throw new InvalidOperationException("트랜스코더 열기 실패"); }
            output.Position = 0;
            var b = new BurnedReel { ReelId = id, Media = output, Meta = meta, Frames = frames, BurnMs = BurnIn.LastReelMs, Backend = BurnIn.LastBackend ?? "" };
            lock (_burned) { _burned[_burnHead]?.Dispose(); _burned[_burnHead] = b; _burnHead = (_burnHead + 1) % BurnRing; }
        });
    }

    private void StartPublish(BurnedReel b)
    {
        var mask = Quota.Route(b.ReelId, RequestMask, RerouteMask, out var rerouted);
        if (rerouted != 0) Telemetry.Publish(CreatorAlertKind.QuotaExhausted, AlertSeverity.Warning, "장애 플랫폼 격리 → 대체 플랫폼 우회 송출", rerouted, b.ReelId);
        _task = Task.Run(() => PublishAsync(b, mask, false));
    }

    /// <summary>마스크 플랫폼 순차 업로드 → 쿼터 관측 · 실패 = 재시도 큐 · 성공 알림.</summary>
    private async Task PublishAsync(BurnedReel b, int mask, bool retry)
    {
        for (var t = 0; t < SmartPlatformQuotaResilienceGuard.Targets; t++)
        {
            if ((mask & (1 << t)) == 0 || (b.PublishedMask & (1 << t)) != 0) continue;
            var target = (PublishTarget)t;
            int status; string? mediaId = null; string? err = null; int retryAfter = 0; byte[]? body = null;
            if (target == PublishTarget.TikTok)
            {
                var job = _reels?.Guard.Enqueue(b.ReelId, b.Media);
                status = job is not null ? 200 : 503; mediaId = job is not null ? "tiktok-q-" + b.ReelId.ToString(CultureInfo.InvariantCulture) : null;
            }
            else if (_pub is not null)
            {
                var p = target == PublishTarget.YouTube ? SocialPlatform.YouTube : SocialPlatform.Instagram;
                if (!_pub.Connected(p)) { status = 401; err = "계정 미연동"; }
                else
                {
                    var r = await _pub.Gateway.UploadAsync(p, b.Media, b.Meta).ConfigureAwait(false);
                    status = r.Ok ? 200 : _pub.Gateway.LastStatus(p); mediaId = r.MediaId; err = r.Error;
                    retryAfter = _pub.Gateway.LastRetryAfterSec(p); body = _pub.Gateway.LastErrorBody(p).ToArray();
                }
            }
            else { status = 503; err = "퍼블리셔 미결선"; }
            var h = Quota.Observe(target, status, body, retryAfter);
            if (status is >= 200 and < 300)
            {
                b.PublishedMask |= 1 << t; Published++; if (retry) RetryUploads++;
                Telemetry.Publish(CreatorAlertKind.ReelPublished, AlertSeverity.Success, SmartPlatformQuotaResilienceGuard.TargetName(target) + " 업로드 성공 · " + (mediaId ?? "") + " · " + b.Meta.Title, b.Frames, b.ReelId);
            }
            else
            {
                if (h != TargetHealth.AuthError) Quota.Enqueue(b.ReelId, target, Clock());
                Telemetry.Publish(h == TargetHealth.QuotaExhausted ? CreatorAlertKind.QuotaExhausted : CreatorAlertKind.UploadFailed, h == TargetHealth.QuotaExhausted ? AlertSeverity.Critical : AlertSeverity.Warning,
                    SmartPlatformQuotaResilienceGuard.TargetName(target) + " " + SmartPlatformQuotaResilienceGuard.HealthName(h) + " · " + (err ?? "HTTP " + status.ToString(CultureInfo.InvariantCulture)) + " → 로컬 재시도 큐", status, b.ReelId);
            }
        }
    }

    /// <summary>재시도 큐 도래 항목 → 보관 번-인 릴 재업로드(백그라운드 1 개).</summary>
    private void PumpRetries()
    {
        if (_retryTask is { IsCompleted: false } || Quota.QueueDepth == 0) return;
        var n = Quota.PumpDue(_due);
        if (n == 0) return;
        var mask0 = 0; var reel = _due[0].ReelId;
        for (var i = 0; i < n; i++)
        {
            var b = Burned(_due[i].ReelId);
            if (b is null) continue;
            if (_due[i].ReelId == reel) mask0 |= 1 << (int)_due[i].Target;
            else Quota.Requeue(in _due[i]);
        }
        var target = Burned(reel);
        if (target is null || mask0 == 0) return;
        _retryTask = StartRetry(target, mask0);
    }

    /// <summary>람다 캡처를 별도 메서드로 격리 — PumpRetries 진입 시 클로저 표시 클래스 할당(조기 반환 경로 포함) 방지.</summary>
    private Task StartRetry(BurnedReel target, int mask) => Task.Run(() => PublishAsync(target, mask, true));

    public void Dispose()
    {
        Enabled = false;
        lock (_burned) for (var i = 0; i < BurnRing; i++) { _burned[i]?.Dispose(); _burned[i] = null; }
    }
}

/// <summary>
/// [PromptFlow Studio v17.73.0] 🤖 제로-터치 자율 오토파일럿 콕핏 러너 — 엔진 · 번-인 · 쿼터 · 텔레메트리 소유 + 10 Hz 제어 스레드.
/// </summary>
public sealed class ZeroTouchAutopilotStudioRunner : IDisposable
{
    private Thread? _loop;
    private volatile bool _running;

    public ZeroTouchAutopilotStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(MasterStationNetworkStudioRunner)) as MasterStationNetworkStudioRunner,
        sp.GetService(typeof(LiveToReelsFactoryStudioRunner)) as LiveToReelsFactoryStudioRunner,
        sp.GetService(typeof(OmniChannelPublisherStudioRunner)) as OmniChannelPublisherStudioRunner,
        sp.GetService(typeof(SemanticChatWeatherStudioRunner)) as SemanticChatWeatherStudioRunner,
        sp.GetService(typeof(InteractiveShowrunnerStudioRunner)) as InteractiveShowrunnerStudioRunner) { }

    public ZeroTouchAutopilotStudioRunner(MasterStationNetworkStudioRunner? station = null, LiveToReelsFactoryStudioRunner? reels = null, OmniChannelPublisherStudioRunner? pub = null,
        SemanticChatWeatherStudioRunner? chat = null, InteractiveShowrunnerStudioRunner? showrunner = null, GraphBridge.IGraphTransport? webhookTransport = null)
    {
        Station = station; Reels = reels; Publisher = pub;
        Telemetry = new RemoteCreatorTelemetryDispatcher(webhookTransport);
        Engine = new ZeroTouchAutopilotMasterEngine(station, reels, pub, chat, showrunner, BurnIn, Quota, Telemetry);
    }

    public event Action? OnChange;
    public MasterStationNetworkStudioRunner? Station { get; }
    public LiveToReelsFactoryStudioRunner? Reels { get; }
    public OmniChannelPublisherStudioRunner? Publisher { get; }
    public HardwareSubtitleBurnInEngine BurnIn { get; } = new();
    public SmartPlatformQuotaResilienceGuard Quota { get; } = new();
    public RemoteCreatorTelemetryDispatcher Telemetry { get; }
    public ZeroTouchAutopilotMasterEngine Engine { get; }
    public bool IsRunning => _running;
    public bool Engaged => Engine.Enabled;
    public string? LastAction => Engine.LastAction;

    /// <summary>원클릭 마스터 스위치 — 엔진 활성 + 제어 스레드.</summary>
    public void SetMaster(bool on)
    {
        if (on) { Engine.Enable(); Start(); }
        else { Engine.Disable(); Stop(); }
        Raise();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Autopilot" };
        _loop.Start();
    }

    public void Stop()
    {
        _running = false;
        var t = _loop; _loop = null;
        if (t is not null && t != Thread.CurrentThread) t.Join(2000);
    }

    private void Loop()
    {
        var last = AutopilotStage.Idle; var n = 0;
        while (_running)
        {
            try { Engine.Tick(); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Autopilot.Tick", ex); }
            if (Engine.Stage != last || ++n % 10 == 0) { last = Engine.Stage; Raise(); }
            Thread.Sleep(100);
        }
    }

    /// <summary>데모 타이밍(단계 최소 시간 단축) · 실운영 기본값 복원.</summary>
    public void SetDemoTiming(bool demo)
    {
        Engine.BroadcastMinMs = demo ? 5_000 : 60_000; Engine.ReactTimeoutMs = demo ? 8_000 : 180_000; Engine.CooldownMs = demo ? 3_000 : 30_000;
        Raise();
    }

    public void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Autopilot.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Engine.Dispose();
        Telemetry.Dispose();
        OnChange = null;
    }

    public string Summary() =>
        ZeroTouchAutopilotMasterEngine.StageIcon(Engine.Stage) + " " + ZeroTouchAutopilotMasterEngine.StageName(Engine.Stage) + " · 사이클 " + Engine.Cycles.ToString(CultureInfo.InvariantCulture)
        + " · 업로드 " + Engine.Published.ToString(CultureInfo.InvariantCulture) + " · 재시도 큐 " + Quota.QueueDepth.ToString(CultureInfo.InvariantCulture) + " · 알림 " + Telemetry.Sent.ToString(CultureInfo.InvariantCulture);
}
