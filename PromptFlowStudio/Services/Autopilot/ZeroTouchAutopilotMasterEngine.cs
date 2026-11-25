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

    // 업로드 임대(in-flight) — 임대 중 링 덮어쓰기/엔진 Dispose = 지연 해제(마지막 Release 시 실제 해제)
    private readonly object _life = new();
    private int _leases;
    private bool _disposeRequested, _disposed;
    public int Leases { get { lock (_life) return _leases; } }
    public bool IsDisposed { get { lock (_life) return _disposed; } }
    public bool DisposePending { get { lock (_life) return _disposeRequested && !_disposed; } }

    /// <summary>업로드 임대 획득 — 해제 요청/해제됨이면 false.</summary>
    public bool TryAcquire() { lock (_life) { if (_disposeRequested) return false; _leases++; return true; } }

    /// <summary>임대 반납 — 마지막 임대 + 해제 요청 대기 시 미디어 실제 해제.</summary>
    public void Release()
    {
        lock (_life)
        {
            if (_leases == 0) return;
            if (--_leases > 0 || !_disposeRequested || _disposed) return;
            _disposed = true;
        }
        Media.Dispose();
    }

    public void Dispose()
    {
        lock (_life)
        {
            _disposeRequested = true;
            if (_leases > 0 || _disposed) return;   // 업로드 중 — 마지막 Release 가 해제
            _disposed = true;
        }
        Media.Dispose();
    }
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
    // 오토파일럿 가동 중 릴스 AutoPublish · 퍼블리셔 AutoUpload 억제(원본/번-인 중복 업로드 방지) — 진입 전 상태 스냅숏 → Disable 시 복원
    private readonly object _autoGate = new();
    private bool _autoSuppressed, _prevReelsAutoPublish, _prevPubAutoUpload;
    private Task? _task;
    private Task? _retryTask;
    // 사이클 스코프 취소(Fault · Disable · Dispose) · 재시도 업로드 = 가동 스코프 취소(Disable · Dispose — 사이클 Fault 와 무관)
    private CancellationTokenSource? _cycleCts, _lifeCts;

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
    /// <summary>TikTok 레그(v17.69 가드) 실제 완료 확인 대기 한도(실시간 ms) — 초과 시 미확인(재시도 큐 미등록: 가드가 레그를 계속 소유 → 중복 방지).</summary>
    public int TikTokConfirmTimeoutMs { get; set; } = 120_000;
    /// <summary>억제 중(진입 전 상태 스냅숏 보유) 여부.</summary>
    public bool AutoPublishSuppressed { get { lock (_autoGate) return _autoSuppressed; } }
    public double MaxBurnSec { get; set; } = 15;
    public int BurnFps { get; set; } = 30;
    public bool AllowMediaFoundation { get; set; } = true;
    /// <summary>v17.74 동적 모션 번-인 — 릴 클립 디코드 가능 시 실제 영상 프레임 위 자막(불가 = PGM 정지 화면 폴백).</summary>
    public bool MotionBurnIn { get; set; } = true;
    public long MotionBurns { get; private set; }
    public long StageEntries(AutopilotStage s) => _stageCount[(int)s];
    public double StageLastMs(AutopilotStage s) => _stageMs[(int)s];
    public double StageAgeMs => Clock() - _enteredAt;
    public int LogCount => _logN;
    public (long At, AutopilotStage Stage, string Text) Log(int i) => _log[(_logHead - 1 - i + 2 * LogCap) % LogCap];
    public BurnedReel? Burned(int reelId) { foreach (var b in _burned) if (b?.ReelId == reelId) return b; return null; }
    public bool TaskRunning => _task is { IsCompleted: false };
    /// <summary>현재 사이클 토큰(사이클 미시작 = None).</summary>
    public CancellationToken CycleToken => Volatile.Read(ref _cycleCts)?.Token ?? CancellationToken.None;
    public long CycleCancels { get; private set; }
    public long CanceledUploads => Interlocked.Read(ref _canceledUploads);
    private long _canceledUploads;
    /// <summary>TikTok 레그 미확인 시 가드 소유 미디어 임대 보존 최대(ms) — 초과 시 반납.</summary>
    public int LegHoldMaxMs { get; set; } = 1_800_000;
    /// <summary>테스트 결선점 — 번-인 단계 초안/트랜스코드 이전 대기(사이클 토큰 관측 회귀용).</summary>
    internal Func<CancellationToken, Task>? BurnHook { get; set; }

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
        Interlocked.Exchange(ref _lifeCts, new CancellationTokenSource())?.Dispose();
        _lastSeenReel = LatestReelId();
        Enter(AutopilotStage.Planning, "오토파일럿 가동");
        Telemetry.Publish(CreatorAlertKind.AutopilotStage, AlertSeverity.Info, "제로-터치 오토파일럿 가동");
    }

    public void Disable()
    {
        if (!Enabled) return;
        Enabled = false;
        CancelCycle();
        CancelSafe(Volatile.Read(ref _lifeCts));
        RestoreAutoPublish();
        Enter(AutopilotStage.Idle, "오토파일럿 정지");
        Telemetry.Publish(CreatorAlertKind.AutopilotStage, AlertSeverity.Warning, "제로-터치 오토파일럿 정지");
    }

    private int LatestReelId() { var r = _reels?.Reel(0); return r?.Id ?? 0; }

    /// <summary>새 사이클 토큰 — 이전 사이클 CTS 해제(그 태스크는 완료 또는 이미 취소 관측).</summary>
    private void BeginCycle() => Interlocked.Exchange(ref _cycleCts, new CancellationTokenSource())?.Dispose();

    /// <summary>현재 사이클 취소(멱등 · 콜백 예외 격리). CTS 는 다음 BeginCycle/Dispose 에서 해제 — 진행 중 태스크의 토큰 등록 경합 방지.</summary>
    private void CancelCycle()
    {
        var c = Volatile.Read(ref _cycleCts);
        if (c is null || c.IsCancellationRequested) return;
        CycleCancels++;
        CancelSafe(c);
    }

    private static void CancelSafe(CancellationTokenSource? c)
    {
        if (c is null) return;
        try { c.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException ex) { PromptFlowStudio.Program.Log("Autopilot.Cancel", ex); }
    }

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
                BeginCycle();
                Plan();
                Enter(AutopilotStage.Broadcasting, "편성 확정");
                break;
            case AutopilotStage.Broadcasting:
            {
                // 방송 중 감지된 릴 ID 보존 — NewReel()이 _lastSeenReel을 전진시키므로 버리면 Reacting이 릴을 잃고 타임아웃 → ManualClip 폴백
                var seen = age >= BroadcastMinMs ? 0 : NewReel();
                if (seen > 0) _reelId = seen;
                if (age >= BroadcastMinMs || seen > 0) Enter(AutopilotStage.Reacting, "클라이맥스 감시");
                break;
            }
            case AutopilotStage.Reacting:
            {
                var id = _reelId > 0 ? _reelId : NewReel();
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
                if (r is { Media: not null, Phase: not (FactoryReelPhase.Clipping or FactoryReelPhase.Packaging) })
                {
                    if (!StartBurn(r)) { Fault("릴 미디어 해제됨"); break; }
                    Enter(AutopilotStage.BurningIn, "릴 #" + r.Id.ToString(CultureInfo.InvariantCulture) + " 자막 번-인"); }
                else if (r?.Phase == FactoryReelPhase.Failed || age >= ClipTimeoutMs) Fault("클립 실패/타임아웃");
                break;
            }
            case AutopilotStage.BurningIn:
                if (_task is { IsCompleted: true } bt)
                {
                    if (bt.IsFaulted || Burned(_reelId) is null) { Fault("번-인 실패: " + (bt.Exception?.InnerException?.Message ?? LastError)); break; }
                    if (!StartPublish(Burned(_reelId)!)) { Fault("번-인 릴 해제됨"); break; }
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
        CancelCycle();   // 고아 번-인/업로드 태스크 중단 — 미게시 타깃은 재시도 큐로 [부분 구제]
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
        SuppressAutoPublish();
        if (_reels is { IsRunning: false } rs) rs.Start();
        if (_chat is { Attached: false } ch) ch.Attach();
    }

    /// <summary>릴스 이미 가동 중이어도 원본 즉시 배포 · 퍼블리셔 원본 자동 업로드 차단(번-인 릴만 오토파일럿이 게시). 최초 1 회 스냅숏 · 매 사이클 재확인.</summary>
    private void SuppressAutoPublish()
    {
        lock (_autoGate)
        {
            if (!Enabled) return;   // Disable 경합 — 복원 이후 재억제 금지
            if (!_autoSuppressed)
            {
                _prevReelsAutoPublish = _reels?.AutoPublish ?? false;
                _prevPubAutoUpload = _pub?.AutoUpload ?? false;
                _autoSuppressed = true;
            }
            if (_reels is { AutoPublish: true } r) r.SetAutoPublish(false);
            if (_pub is { AutoUpload: true } p) p.SetAutoUpload(false, startWorker: false);
        }
    }

    /// <summary>진입 전 AutoPublish · AutoUpload 복원(멱등). 워커 기동 상태는 건드리지 않음.</summary>
    private void RestoreAutoPublish()
    {
        lock (_autoGate)
        {
            if (!_autoSuppressed) return;
            _autoSuppressed = false;
            try
            {
                if (_reels is { } r && r.AutoPublish != _prevReelsAutoPublish) r.SetAutoPublish(_prevReelsAutoPublish);
                if (_pub is { } p && p.AutoUpload != _prevPubAutoUpload) p.SetAutoUpload(_prevPubAutoUpload, startWorker: false);
            }
            catch (ObjectDisposedException ex) { PromptFlowStudio.Program.Log("Autopilot.Restore", ex); }
        }
    }

    // ══════════════════════ 백그라운드 단계 ══════════════════════
    /// <summary>번-인 태스크 기동 — 원본 릴 핀(축출/Dispose 지연) · 사이클 토큰 관측. 핀 실패(이미 해제) = false.</summary>
    private bool StartBurn(FactoryReel reel)
    {
        if (!reel.TryPin()) return false;
        // PGM 스냅숏(방송 화면) — MCR 미가동이면 그라데이션 폴백
        IBurnInFrameSource src = Fallback;
        if (_station is { } s && s.Mcr.Mcr.VideoFrames > 0) { PgmSource.Capture(s.Mcr.Mcr.ProgramOut); src = PgmSource; }
        var dur = Math.Clamp(reel.DurationSec > 0 ? reel.DurationSec : reel.Request.Seconds, 3, MaxBurnSec);
        var id = reel.Id;
        var ct = CycleToken;
        var hook = BurnHook;
        _task = Task.Run(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (hook is not null) await hook(ct).ConfigureAwait(false);
                var meta = new SocialPostMetadata();
                if (_pub is not null)
                {
                    var d = await _pub.GenerateDraftAsync(id, _pub.ContextFor(reel), ct).ConfigureAwait(false);
                    meta = d.Meta;
                }
                else { meta.Title = reel.Caption + " #Shorts"; meta.Description = reel.Caption; }
                ct.ThrowIfCancellationRequested();
                var output = new SegmentedChunkStream();
                int frames;
                using (var clip = MotionBurnIn && reel.Media is { } m ? VisualPolish.DecodedClipFrameSource.TryOpen(m) : null)
                {
                    if (clip is not null) MotionBurns++;
                    lock (BurnIn)
                    {
                        BurnIn.SetCaptionTrack((meta.Title + ". " + meta.Description).AsSpan(), (float)(dur * 1000));
                        frames = BurnIn.BurnReel(clip ?? src, dur, BurnFps, output, 6000, AllowMediaFoundation);
                    }
                }
                if (frames == 0) { output.Dispose(); throw new InvalidOperationException("트랜스코더 열기 실패"); }
                if (ct.IsCancellationRequested) { output.Dispose(); ct.ThrowIfCancellationRequested(); }   // 취소된 사이클의 결과물은 링에 넣지 않음
                output.Position = 0;
                var b = new BurnedReel { ReelId = id, Media = output, Meta = meta, Frames = frames, BurnMs = BurnIn.LastReelMs, Backend = BurnIn.LastBackend ?? "" };
                lock (_burned) { _burned[_burnHead]?.Dispose(); _burned[_burnHead] = b; _burnHead = (_burnHead + 1) % BurnRing; }   // 임대 중 슬롯 = 지연 해제
            }
            finally { reel.Unpin(); }
        });
        return true;
    }

    /// <summary>업로드 태스크 기동 — 번-인 릴 임대(링 덮어쓰기/Dispose 지연) · 사이클 토큰. 임대 실패 = false.</summary>
    private bool StartPublish(BurnedReel b)
    {
        if (!b.TryAcquire()) return false;
        var mask = Quota.Route(b.ReelId, RequestMask, RerouteMask, out var rerouted);
        if (rerouted != 0) Telemetry.Publish(CreatorAlertKind.QuotaExhausted, AlertSeverity.Warning, "장애 플랫폼 격리 → 대체 플랫폼 우회 송출", rerouted, b.ReelId);
        var ct = CycleToken;
        _task = Task.Run(() => PublishAsync(b, mask, false, ct));
        return true;
    }

    /// <summary>마스크 플랫폼 순차 업로드 → 쿼터 관측 · 실패 = 재시도 큐 · 성공 알림. 호출자가 획득한 임대를 반납(TikTok 미확인 레그 = 가드 종결 후 반납).
    /// 취소 = 미게시 타깃 재시도 큐 보존(쿼터 관측 없음).</summary>
    private async Task PublishAsync(BurnedReel b, int mask, bool retry, CancellationToken ct)
    {
        ReelsUploadJob? held = null;
        try { await PublishCoreAsync(b, mask, retry, ct, j => held = j).ConfigureAwait(false); }
        finally
        {
            if (held is null) b.Release();
            else _ = ReleaseAfterLegAsync(b, held, (int)ReelsPlatform.TikTok);
        }
    }

    /// <summary>가드가 미디어를 계속 소유하는 미확인 레그 — 종결(또는 보존 한도) 후 임대 반납.</summary>
    private async Task ReleaseAfterLegAsync(BurnedReel b, ReelsUploadJob job, int leg)
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (job.Legs[leg].Phase is not (ReelsUploadPhase.Done or ReelsUploadPhase.Failed or ReelsUploadPhase.Skipped) && sw.ElapsedMilliseconds < LegHoldMaxMs)
                await Task.Delay(250).ConfigureAwait(false);
        }
        finally { b.Release(); }
    }

    private async Task PublishCoreAsync(BurnedReel b, int mask, bool retry, CancellationToken ct, Action<ReelsUploadJob> hold)
    {
        for (var t = 0; t < SmartPlatformQuotaResilienceGuard.Targets; t++)
        {
            if ((mask & (1 << t)) == 0 || (b.PublishedMask & (1 << t)) != 0) continue;
            var target = (PublishTarget)t;
            if (ct.IsCancellationRequested) { Quota.Enqueue(b.ReelId, target); Interlocked.Increment(ref _canceledUploads); continue; }
            int status; string? mediaId = null; string? err = null; int retryAfter = 0; byte[]? body = null; var inFlight = false;
            if (target == PublishTarget.TikTok)
            {
                // TikTok 레그만 큐잉(IG/YT 레그 = Skipped → 게이트웨이 업로드와 교차 중복 방지) · 큐잉 ≠ 성공: 레그 완료 확인 후 200
                var job = _reels?.Guard.Enqueue(b.ReelId, b.Media, 1 << (int)ReelsPlatform.TikTok);
                if (job is null) { status = 503; err = _reels is null ? "릴스 팩토리 미결선" : "업로드 슬롯 포화"; }
                else
                {
                    var phase = await AwaitLegAsync(job, (int)ReelsPlatform.TikTok, ct).ConfigureAwait(false);
                    if (phase == ReelsUploadPhase.Done) { status = 200; mediaId = "tiktok-" + b.ReelId.ToString(CultureInfo.InvariantCulture); }
                    else if (phase == ReelsUploadPhase.Failed) { status = 503; err = "TikTok 레그 실패"; }
                    else { status = 504; err = "TikTok 레그 완료 미확인(가드 처리 중)"; inFlight = true; hold(job); }   // 가드가 미디어 소유 — 임대 유지
                }
            }
            else if (_pub is not null)
            {
                var p = target == PublishTarget.YouTube ? SocialPlatform.YouTube : SocialPlatform.Instagram;
                if (!_pub.Connected(p)) { status = 401; err = "계정 미연동"; }
                else
                {
                    // 반환값만 판독 — 게이트 해제 후 공유 Last* 조회 시 동시 업로드(재시도 태스크 등)의 상태가 섞여 오분류
                    var r = await _pub.Gateway.UploadAsync(p, b.Media, b.Meta, ct).ConfigureAwait(false);
                    if (!r.Ok && ct.IsCancellationRequested) { Quota.Enqueue(b.ReelId, target); Interlocked.Increment(ref _canceledUploads); continue; }   // 취소 ≠ 플랫폼 장애 — 쿼터 관측 생략
                    status = UploadOutcome(in r, out retryAfter, out body); mediaId = r.MediaId; err = r.Error;
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
                if (h != TargetHealth.AuthError && !inFlight) Quota.Enqueue(b.ReelId, target);   // 가드 시계 축(엔진 TickCount64 혼용 금지)
                Telemetry.Publish(h == TargetHealth.QuotaExhausted ? CreatorAlertKind.QuotaExhausted : CreatorAlertKind.UploadFailed, h == TargetHealth.QuotaExhausted ? AlertSeverity.Critical : AlertSeverity.Warning,
                    SmartPlatformQuotaResilienceGuard.TargetName(target) + " " + SmartPlatformQuotaResilienceGuard.HealthName(h) + " · " + (err ?? "HTTP " + status.ToString(CultureInfo.InvariantCulture)) + " → 로컬 재시도 큐", status, b.ReelId);
            }
        }
    }

    /// <summary>업로드 결과 → 쿼터 관측 입력(상태 · Retry-After · 오류 본문)을 반환값에서만 도출. 성공 = 2xx 보장 · 실패 = 결과 고유 상태(0 = HTTP 미도달 → 일시 백오프).</summary>
    public static int UploadOutcome(in SocialUploadResult r, out int retryAfterSec, out byte[]? body)
    {
        retryAfterSec = r.RetryAfterSec;
        body = r.ErrorBody;
        if (r.Ok) return r.Status is >= 200 and < 300 ? r.Status : 200;
        return r.Status is >= 200 and < 300 ? 0 : r.Status;   // 실패인데 2xx 기록(분할 업로드 중간 단계) → 성공 오인 금지
    }

    /// <summary>가드 레그 종결 대기(백그라운드 · 실시간 한도). 반환 = 종결 단계 또는 미종결 시 현재 단계.</summary>
    private async Task<ReelsUploadPhase> AwaitLegAsync(ReelsUploadJob job, int leg, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var ph = job.Legs[leg].Phase;
            // 취소 = 미종결 단계 반환 → 미확인(가드가 레그 소유 · 재시도 큐 미등록 = 중복 방지)
            if (ph is ReelsUploadPhase.Done or ReelsUploadPhase.Failed or ReelsUploadPhase.Skipped || sw.ElapsedMilliseconds >= TikTokConfirmTimeoutMs || ct.IsCancellationRequested) return ph;
            await Task.Delay(20).ConfigureAwait(false);
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
    private Task StartRetry(BurnedReel target, int mask)
    {
        if (!target.TryAcquire()) return Task.CompletedTask;
        var ct = Volatile.Read(ref _lifeCts)?.Token ?? CancellationToken.None;
        return Task.Run(() => PublishAsync(target, mask, true, ct));
    }

    public void Dispose()
    {
        Enabled = false;
        CancelCycle();
        var life = Interlocked.Exchange(ref _lifeCts, null);
        CancelSafe(life);
        RestoreAutoPublish();
        lock (_burned) for (var i = 0; i < BurnRing; i++) { _burned[i]?.Dispose(); _burned[i] = null; }   // 업로드 임대 중 = 마지막 Release 에서 해제
        Interlocked.Exchange(ref _cycleCts, null)?.Dispose();
        life?.Dispose();
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
        Engine.Disable();   // 제어 스레드 정지 = 오토파일럿 정지 → AutoPublish · AutoUpload 진입 전 상태 복원(멱등)
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
