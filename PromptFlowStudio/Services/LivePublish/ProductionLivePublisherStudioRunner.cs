using System.Globalization;
using PromptFlowStudio.Services.Autopilot;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.Security;

namespace PromptFlowStudio.Services.LivePublish;

/// <summary>
/// [PromptFlow Studio v17.79.0] 🚀 실제 SNS 라이브 배포 콕핏 러너 — 볼트 · 이어올리기 업로더 · 프리플라이트/롤백 가드 소유 + 10 Hz 제어 루프.
///   ▸ 자격증명 환경변수가 있으면 실 Google/Meta 엔드포인트(HttpClient), 없으면 계약 Mock — 플랫폼별 독립 전환.
///   ▸ 볼트 키는 <see cref="SecureSecretVault"/>(DPAPI 봉인) · 토큰 변경 시 루프에서 AES-GCM 파일 영속화. 알림은 v17.73 오토파일럿 웹훅 큐 공유.
/// </summary>
public sealed class ProductionLivePublisherStudioRunner : IDisposable
{
    public const int TickMs = 100, DemoBytes = 3 * 1024 * 1024 + 123_457;
    public const string VaultKeyName = "LIVE_OAUTH_VAULT_KEY";
    private readonly RemoteCreatorTelemetryDispatcher? _ownedAlerts;
    private readonly bool _persist;
    private readonly object _gate = new();
    private MemoryStream? _demo;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _restored;
    private LivePublishStage _lastStage;

    public ProductionLivePublisherStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(ZeroTouchAutopilotStudioRunner)) as ZeroTouchAutopilotStudioRunner,
        sp.GetService(typeof(SecureSecretVault)) as SecureSecretVault) { }

    public ProductionLivePublisherStudioRunner(ZeroTouchAutopilotStudioRunner? autopilot = null, SecureSecretVault? secrets = null, IGraphTransport? transport = null, Func<string, string?>? env = null)
    {
        if (autopilot is null) _ownedAlerts = new RemoteCreatorTelemetryDispatcher(new SimulatedWebhookServer());
        Alerts = autopilot?.Telemetry ?? _ownedAlerts!;
        AlertsShared = autopilot is not null;
        _persist = secrets is not null;
        Vault = new ProductionOAuth2TokenVaultGateway(transport, env, secrets is null ? null : () => secrets.ResolveOrCreate(VaultKeyName));
        Uploader = new ProductionResumableVideoUploaderEngine(Vault);
        Guard = new LivePublishingPreflightRollbackGuard(Vault, Uploader, Alerts);
        if (!Vault.IsLive(LiveProvider.Instagram)) Uploader.Reels.PollIntervalSec = 1;
    }

    public event Action? OnChange;
    public ProductionOAuth2TokenVaultGateway Vault { get; }
    public ProductionResumableVideoUploaderEngine Uploader { get; }
    public LivePublishingPreflightRollbackGuard Guard { get; }
    public RemoteCreatorTelemetryDispatcher Alerts { get; }
    public bool AlertsShared { get; }
    public Func<double> Clock { get; set; } = static () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    public LiveTargets Targets { get; set; } = LiveTargets.Both;
    public bool Running => _loop is { IsCompleted: false };
    public string? LastAction { get; private set; }
    public long Ticks { get; private set; }

    public static LiveVideoSpec DemoSpec => new() { Width = 1080, Height = 1920, Fps = 60f, DurationSec = 24f, AudioHz = 48000, AudioChannels = 2, Bytes = DemoBytes };

    // ══════════════════════ 계정 ══════════════════════
    public void ConnectMock(LiveProvider p)
    {
        lock (_gate)
        {
            var ok = Vault.ConnectViaContract(p, Clock());
            var a = Vault.Account(p);
            LastAction = ok ? "🔗 " + Label(p) + " 연결(계약 Mock · PKCE S256) — " + a.ProfileName + " · " + a.ChannelId : "⚠ " + Label(p) + " 연결 실패 — " + a.LastError;
        }
        Raise();
    }

    public async Task ConnectAsync(LiveProvider p)
    {
        if (!Vault.IsLive(p)) { ConnectMock(p); return; }
        LastAction = "🌐 " + Label(p) + " 브라우저 동의 대기(루프백 · 180 s)"; Raise();
        bool ok;
        try { ok = await Vault.ConnectLiveAsync(p).ConfigureAwait(false); }
        catch (Exception ex) { ok = false; PromptFlowStudio.Program.Log("LivePublish.Connect", ex); }
        var a = Vault.Account(p);
        LastAction = ok ? "🔗 " + Label(p) + " 실계정 연결 — " + a.ProfileName : "⚠ " + Label(p) + " 연결 실패 — " + a.LastError;
        Raise();
    }

    public void Disconnect(LiveProvider p)
    {
        lock (_gate) Vault.Disconnect(p);
        LastAction = "⏏ " + Label(p) + " 연결 해제(토큰 제로화)"; Raise();
    }

    public static string Label(LiveProvider p) => p == LiveProvider.YouTube ? "YouTube" : "Instagram";

    // ══════════════════════ 배포 ══════════════════════
    /// <summary>데모 9:16 MP4(ftyp + 결정적 페이로드) 생성 → 미연결 Mock 계정 자동 연결 → 프리플라이트 → 배포 시작.</summary>
    public bool RunDemo(LiveVideoSpec? spec = null)
    {
        bool ok;
        lock (_gate)
        {
            var now = Clock();
            foreach (var p in new[] { LiveProvider.YouTube, LiveProvider.Instagram })
                if ((Targets & (p == LiveProvider.YouTube ? LiveTargets.YouTube : LiveTargets.Instagram)) != 0 && !Vault.Account(p).IsConnected && !Vault.IsLive(p)) Vault.ConnectViaContract(p, now);
            var s = spec ?? DemoSpec;
            _demo?.Dispose();
            _demo = BuildDemoMp4((int)Math.Clamp(s.Bytes, 16, 120L * 1024 * 1024));
            s.Bytes = _demo.Length;
            Uploader.MaxHopsPerStep = Vault.IsLive(LiveProvider.YouTube) ? 4 : 1;
            ok = Guard.Begin(_demo, in s, "PromptFlow Studio 숏폼 라이브 배포 #Shorts", "PromptFlow Studio v17.79 라이브 배포 콕핏 — 자동 생성 숏폼 #shorts #AI", Targets, now);
            LastAction = ok ? "🚀 배포 시작 — " + LivePublishingPreflightRollbackGuard.StageName(Guard.Stage) + " · " + (Guard.SafeMode ? "🛡 안전 모드 " + Guard.ActivePrivacy : "⚡ 라이브 public")
                            : "⛔ 배포 차단 — " + Guard.LastError;
        }
        Raise();
        return ok;
    }

    public static MemoryStream BuildDemoMp4(int bytes)
    {
        var buf = new byte[bytes];
        ReadOnlySpan<byte> ftyp = [0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'i', (byte)'s', (byte)'o', (byte)'2', (byte)'a', (byte)'v', (byte)'c', (byte)'1', (byte)'m', (byte)'p', (byte)'4', (byte)'1'];
        ftyp[..Math.Min(ftyp.Length, bytes)].CopyTo(buf);
        var x = 0x9E3779B9u;
        for (var i = ftyp.Length; i < bytes; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; buf[i] = (byte)x; }
        return new MemoryStream(buf, false);
    }

    public void ToggleSafeMode()
    {
        Guard.SafeMode = !Guard.SafeMode;
        LastAction = Guard.SafeMode ? "🛡 안전 모드 ON — YouTube " + Guard.ActivePrivacy + " 드라이런" : "⚡ 안전 모드 OFF — 2 단계 커밋(private 스테이징 → IG 성공 후 public)";
        Raise();
    }

    public void SetSafePrivacy(SafePrivacy p) { Guard.SafePrivacy = p; LastAction = "🛡 드라이런 공개 범위 → " + Guard.ActivePrivacy; Raise(); }

    public void Promote() { lock (_gate) LastAction = Guard.Promote(Clock()) ? "📣 YouTube public 승격 요청" : "⚠ 승격 불가(영상 없음/진행 중)"; Raise(); }
    public void Rollback() { lock (_gate) LastAction = Guard.Rollback(Clock()) ? "↩ YouTube private 롤백 요청" : "⚠ 롤백 불가(영상 없음/진행 중)"; Raise(); }
    public void Cancel() { lock (_gate) Guard.Cancel(Clock()); LastAction = "⏹ 배포 중단"; Raise(); }

    /// <summary>계약 Mock 장애 주입(데모) — 청크 단절 · 저작권 거부.</summary>
    public void InjectDrop() { Vault.Contract.DropChunkIndex = 2; LastAction = "🔌 다음 업로드 3번째 청크 연결 단절 주입"; Raise(); }
    public void InjectCopyright(bool on) { Vault.Contract.RejectCopyright = on; LastAction = on ? "©️ 저작권 거부 주입 ON" : "©️ 저작권 거부 주입 OFF"; Raise(); }

    // ══════════════════════ 루프 ══════════════════════
    /// <summary>1 틱(무할당) — 토큰 갱신 수확/발행 → 배포 단계 진행 → (변경 시) 볼트 영속화.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            var now = Clock();
            Vault.Tick(now);
            Guard.Step(now);
            Ticks++;
        }
        if (_persist && Vault.Dirty) Vault.SaveVault();
        if (Guard.Stage != _lastStage)
        {
            _lastStage = Guard.Stage;
            LastAction = "📡 " + LivePublishingPreflightRollbackGuard.StageName(Guard.Stage) + (Guard.LastError is { } e ? " — " + e : null);
            Raise();
        }
    }

    public void Start()
    {
        if (Running) return;
        if (!_restored && _persist) { _restored = true; if (Vault.LoadVault(Clock())) LastAction = "🔐 볼트 복원(AES-GCM 256)"; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            var tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    Tick();
                    if (++tick % 3 == 0 && Guard.IsBusy) Raise();
                }
                catch (Exception ex) { LastAction = "⚠ " + ex.Message; PromptFlowStudio.Program.Log("LivePublish.Loop", ex); }
                try { await Task.Delay(TickMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        LastAction ??= "▶ 콕핏 루프 가동(10 Hz · 만료 10 분 전 자동 갱신)";
        Raise();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(5_000); } catch (AggregateException) { }
        _cts?.Dispose(); _cts = null; _loop = null;
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("LivePublish.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        OnChange = null;
        Uploader.Dispose();
        Vault.Dispose();
        _demo?.Dispose();
        _ownedAlerts?.Dispose();
    }

    public string Summary()
    {
        var now = Clock();
        var y = Vault.Account(LiveProvider.YouTube); var g = Vault.Account(LiveProvider.Instagram);
        return "YT " + (y.IsConnected ? (y.SecondsLeft(now) / 60).ToString("0", CultureInfo.InvariantCulture) + "분" : "미연결") + " · IG " + (g.IsConnected ? (g.SecondsLeft(now) / 86400).ToString("0", CultureInfo.InvariantCulture) + "일" : "미연결")
            + " · 갱신 " + Vault.Refreshes.ToString(CultureInfo.InvariantCulture) + " · 청크 " + Uploader.ChunksSent.ToString(CultureInfo.InvariantCulture) + "/" + Uploader.TotalChunks.ToString(CultureInfo.InvariantCulture)
            + " · 재개 " + Uploader.Resumes.ToString(CultureInfo.InvariantCulture) + " · 롤백 " + Guard.Rollbacks.ToString(CultureInfo.InvariantCulture) + " · 알림 " + Guard.AlertsDispatched.ToString(CultureInfo.InvariantCulture);
    }
}
