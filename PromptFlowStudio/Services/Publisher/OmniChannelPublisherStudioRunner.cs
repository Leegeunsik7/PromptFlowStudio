using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PromptFlowStudio.Services.BeatSync;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.LiveShowroom;
using PromptFlowStudio.Services.LiveToReels;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Showrunner;
using PromptFlowStudio.Services.StationMaster;
using PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Services.Publisher;

/// <summary>v17.71.0 릴 1 개의 퍼블리시 초안(AI 메타데이터 · 편집 · 플랫폼별 결과).</summary>
public sealed class PublishDraft
{
    public int ReelId { get; init; }
    public ShortformContext Context { get; init; } = new();
    public SocialPostMetadata Meta { get; } = new();
    public bool Generated { get; internal set; }
    public bool LlmFallback { get; internal set; }
    public SocialUploadResult? YouTube { get; internal set; }
    public SocialUploadResult? Instagram { get; internal set; }
    public bool Uploading { get; internal set; }
    public string TagsText { get { var sb = new StringBuilder(); for (var i = 0; i < Meta.TagCount; i++) { if (i > 0) sb.Append(' '); sb.Append(Meta.Tags[i]); } return sb.ToString(); } }
}

/// <summary>
/// [PromptFlow Studio v17.71.0] 📱 AI 옴니채널 퍼블리셔 &amp; 크리에이터 QoL 러너.
///   ▸ 계정: OAuth2 PKCE 인가 URL · 코드 교환 · 토큰 직접 입력(IG 장기 토큰) · 시뮬레이터 연동 → <see cref="OAuthTokenVault"/>(DPAPI 봉인 키 AES-GCM 파일).
///   ▸ 자동 업로드(2 Hz 워커): v17.69 릴(Ready/Published) → 문맥(핵심 대사 · v17.67 물리 로그 · v17.68 상품/판매) → AI 제목/설명/해시태그 → 연동 플랫폼 업로드 → 편성 추천 성과 귀속.
///   ▸ 1-Click: 프리셋 적용 → 릴스 팩토리 · 방송국 24/7 · 자동 업로드 가동(체크리스트).
///   ▸ 전송: 크리덴셜(환경변수) 존재 = 실 HTTPS(<see cref="HttpGraphTransport"/>) · 부재 = 규격 시뮬레이터(<see cref="SimulatedSocialApiServer"/>).
/// </summary>
public sealed class OmniChannelPublisherStudioRunner : ICreatorPresetTarget, IDisposable
{
    public const int MaxDrafts = 24;

    private readonly LiveToReelsFactoryStudioRunner? _reels;
    private readonly MasterStationNetworkStudioRunner? _station;
    private readonly InteractiveShowrunnerStudioRunner? _showrunner;
    private readonly LiveCommerceStudioRunner? _commerce;
    private readonly BeatSyncAudioMixStudioRunner? _beat;
    private readonly List<PublishDraft> _drafts = new(MaxDrafts);
    private readonly HttpGraphTransport? _http;
    private readonly byte[]? _ephemeralKey;
    private int _lastReel;
    private Thread? _loop;
    private volatile bool _running;
    private Task? _work;

    public OmniChannelPublisherStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(LiveToReelsFactoryStudioRunner)) as LiveToReelsFactoryStudioRunner,
        sp.GetService(typeof(MasterStationNetworkStudioRunner)) as MasterStationNetworkStudioRunner,
        sp.GetService(typeof(InteractiveShowrunnerStudioRunner)) as InteractiveShowrunnerStudioRunner,
        sp.GetService(typeof(LiveCommerceStudioRunner)) as LiveCommerceStudioRunner,
        sp.GetService(typeof(BeatSyncAudioMixStudioRunner)) as BeatSyncAudioMixStudioRunner,
        sp.GetService(typeof(SecureSecretVault)) as SecureSecretVault) { }

    public OmniChannelPublisherStudioRunner(LiveToReelsFactoryStudioRunner? reels = null, MasterStationNetworkStudioRunner? station = null, InteractiveShowrunnerStudioRunner? showrunner = null,
        LiveCommerceStudioRunner? commerce = null, BeatSyncAudioMixStudioRunner? beat = null, SecureSecretVault? secrets = null, string? root = null, bool? simulate = null)
    {
        _reels = reels; _station = station; _showrunner = showrunner; _commerce = commerce; _beat = beat;
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio");
        Func<byte[]> key;
        if (secrets is not null) { key = () => secrets.ResolveOrCreate("SOCIAL_OAUTH_VAULT"); KeySecure = true; }
        else { _ephemeralKey = RandomNumberGenerator.GetBytes(32); key = () => (byte[])_ephemeralKey.Clone(); KeySecure = false; }
        Vault = new OAuthTokenVault(Path.Combine(Root, "Vault", "social_oauth.bin"), key);
        Simulator = new SimulatedSocialApiServer(static () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var hasCreds = Environment.GetEnvironmentVariable("PROMPTFLOW_SECRET_YT_CLIENT_ID") is { Length: > 0 } || Environment.GetEnvironmentVariable("PROMPTFLOW_SECRET_META_APP_ID") is { Length: > 0 };
        Simulated = simulate ?? !hasCreds;
        if (!Simulated) _http = new HttpGraphTransport();
        Gateway = new SocialMediaApiGatewayEngine(Simulated ? Simulator : _http!, Vault)
        {
            InstagramUserId = Environment.GetEnvironmentVariable("PROMPTFLOW_SECRET_IG_USER_ID") ?? (Simulated ? "17841400000000000" : ""),
        };
        Presets = new CreatorQolPresetManager(Path.Combine(Root, "Presets", "creator_presets.json"));
        Presets.Load();
        Vault.Load();
    }

    public event Action? OnChange;
    public string Root { get; }
    public bool KeySecure { get; }
    public bool Simulated { get; private set; }
    public OAuthTokenVault Vault { get; }
    public SimulatedSocialApiServer Simulator { get; }
    public SocialMediaApiGatewayEngine Gateway { get; }
    public AiContentTrendRecommendationSynthesizer Recommender { get; } = new();
    public CreatorQolPresetManager Presets { get; }
    public bool ReelsAvailable => _reels is not null;
    public bool StationAvailable => _station is not null;
    public bool AutoUpload { get; private set; }
    public bool UploadYouTube { get; set; } = true;
    public bool UploadInstagram { get; set; } = true;
    public string Privacy { get; set; } = "private";
    public bool IsRunning => _running;
    public long AutoUploads { get; private set; }
    public long OneClicks { get; private set; }
    public string? LastAction { get; private set; }
    public IReadOnlyList<string> LastChecklist { get; private set; } = Array.Empty<string>();
    public int DraftCount { get { lock (_drafts) return _drafts.Count; } }
    public PublishDraft? Draft(int i) { lock (_drafts) return (uint)i < (uint)_drafts.Count ? _drafts[i] : null; }
    public PublishDraft? FindDraft(int reelId) { lock (_drafts) { foreach (var d in _drafts) if (d.ReelId == reelId) return d; } return null; }
    public bool Connected(SocialPlatform p) => Gateway.State(p) is SocialLinkState.Connected or SocialLinkState.Expiring;

    // ══════════════════════ 계정 연동(비핫패스) ══════════════════════
    public string AuthorizeUrl(SocialPlatform p) => Gateway.BuildAuthorizeUrl(p);

    public async Task<bool> ExchangeCodeAsync(SocialPlatform p, string code)
    {
        var ok = await Gateway.ExchangeCodeAsync(p, code.Trim()).ConfigureAwait(false);
        LastAction = ok ? "🔐 " + SocialMediaApiGatewayEngine.PlatformName(p) + " 연동 완료(볼트 봉인)" : "⚠ 코드 교환 실패: " + Gateway.LastError(p);
        Raise();
        return ok;
    }

    /// <summary>토큰 직접 입력(IG 장기 토큰 · 테스트) — 볼트 봉인 저장.</summary>
    public bool ConnectWithToken(SocialPlatform p, string access, string? refresh, long expiresInSec)
    {
        if (string.IsNullOrWhiteSpace(access) || access.Length > GraphSecretSlot.Cap) { LastAction = "⚠ 토큰 형식 오류"; Raise(); return false; }
        var now = Gateway.UnixNow();
        Vault.Store(p, access.Trim(), refresh?.Trim() ?? "", now + Math.Max(60, expiresInSec), now);
        var ok = Vault.Save();
        LastAction = "🔐 " + SocialMediaApiGatewayEngine.PlatformName(p) + " 토큰 저장" + (ok ? "(AES-GCM 봉인)" : "(⚠ 디스크 저장 실패 · 메모리만)") + (KeySecure ? "" : " · ⚠ 임시 키");
        Raise();
        return ok;
    }

    /// <summary>시뮬레이터 계정(데모) — 서버 발급 토큰을 볼트에 저장.</summary>
    public void ConnectSimulated(SocialPlatform p, long expiresInSec = 3600)
    {
        if (!Simulated) return;
        var now = Gateway.UnixNow();
        var access = p == SocialPlatform.YouTube ? "ya29.sim-initial" : "IGQV.sim-initial";
        Simulator.Issue(p, Encoding.ASCII.GetBytes(access), now + expiresInSec, p == SocialPlatform.YouTube ? "1//sim-refresh"u8 : default);
        Vault.Store(p, access, p == SocialPlatform.YouTube ? "1//sim-refresh" : "", now + expiresInSec, now, p == SocialPlatform.YouTube ? "UCsimChannel" : "17841400000000000");
        Vault.Save();
        LastAction = "🧪 " + SocialMediaApiGatewayEngine.PlatformName(p) + " 시뮬레이터 연동"; Raise();
    }

    public void Disconnect(SocialPlatform p) { Vault.Revoke(p); Vault.Save(); LastAction = "⏏ " + SocialMediaApiGatewayEngine.PlatformName(p) + " 연동 해제(볼트 삭제)"; Raise(); }

    public async Task<bool> RefreshAsync(SocialPlatform p)
    {
        var ok = await Gateway.RefreshAsync(p).ConfigureAwait(false);
        LastAction = ok ? "♻ " + SocialMediaApiGatewayEngine.PlatformName(p) + " 토큰 갱신" : "⚠ 갱신 실패 — 재인증 필요"; Raise();
        return ok;
    }

    // ══════════════════════ 초안 · 업로드 ══════════════════════
    /// <summary>릴 → 문맥(핵심 대사 · 물리 로그 · 상품/판매).</summary>
    public ShortformContext ContextFor(FactoryReel r)
    {
        var c = new ShortformContext
        {
            Caption = r.Caption, KeyLine = _station?.LastLine ?? r.Caption, Cause = (int)r.Request.Cause, Peak = r.Request.Peak, DurationSec = r.DurationSec > 0 ? r.DurationSec : r.Request.Seconds,
            Program = _station?.Active ?? StationProgramKind.Talkshow,
        };
        if (_showrunner is not null) { c.Hits = _showrunner.Spawner.Hits; c.HeadHits = _showrunner.Spawner.HeadHits; c.Confetti = _showrunner.SpawnedBy(DropKind.Confetti); }
        if (_commerce?.Sales.Product is { } p) { c.Product = p.Name; c.Category = CategoryOf(p.Shape); c.UnitsSold = _commerce.Sales.UnitsSold; }
        return c;
    }

    public static ProductCategory CategoryOf(ProductShape s) => s switch
    {
        ProductShape.Perfume or ProductShape.Lipstick => ProductCategory.Beauty, ProductShape.Smartphone => ProductCategory.Tech, _ => ProductCategory.Fashion,
    };

    /// <summary>문맥 → AI 메타데이터 초안(LLM → 1 s 폴백).</summary>
    public async Task<PublishDraft> GenerateDraftAsync(int reelId, ShortformContext ctx)
    {
        var d = FindDraft(reelId) ?? AddDraft(new PublishDraft { ReelId = reelId, Context = ctx });
        var fb0 = Recommender.Fallbacks;
        await Recommender.GenerateAsync(ctx).ConfigureAwait(false);
        lock (Recommender) Recommender.ExportTo(d.Meta);
        d.Meta.Privacy = Privacy;
        d.Generated = true; d.LlmFallback = Recommender.Fallbacks > fb0;
        LastAction = "🤖 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " 제목 · 해시태그 생성" + (d.LlmFallback ? "(로컬 폴백)" : ""); Raise();
        return d;
    }

    private PublishDraft AddDraft(PublishDraft d)
    {
        lock (_drafts)
        {
            if (_drafts.Count >= MaxDrafts) _drafts.RemoveAt(_drafts.Count - 1);
            _drafts.Insert(0, d);
        }
        return d;
    }

    /// <summary>편집 반영(UI) — 해시태그 문자열 재정규화(정확히 5 · # 접두).</summary>
    public void EditDraft(int reelId, string title, string description, string tags)
    {
        var d = FindDraft(reelId); if (d is null) return;
        d.Meta.Title = title.Length > 100 ? title[..100] : title;
        d.Meta.Description = description.Length > 2000 ? description[..2000] : description;
        lock (Recommender)
        {
            Recommender.Reset();
            foreach (var t in tags.Split(new[] { ' ', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)) Recommender.AddTag(t);
            Recommender.Normalize(d.Context);
            d.Meta.TagCount = Recommender.TagCount;
            for (var i = 0; i < Recommender.TagCount; i++) d.Meta.Tags[i] = new string(Recommender.Tag(i));
        }
        LastAction = "✏ 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " 메타데이터 수정"; Raise();
    }

    /// <summary>연동된 선택 플랫폼으로 업로드(순차 · 플랫폼별 결과).</summary>
    public async Task<int> UploadDraftAsync(int reelId, SegmentedChunkStream media)
    {
        var d = FindDraft(reelId);
        if (d is null || d.Uploading) return 0;
        d.Uploading = true; Raise();
        var ok = 0;
        try
        {
            if (UploadYouTube && Connected(SocialPlatform.YouTube))
            {
                if (Simulated) Simulator.ExpectMedia(Bytes(media));
                d.YouTube = await Gateway.UploadAsync(SocialPlatform.YouTube, media, d.Meta).ConfigureAwait(false);
                if (d.YouTube.Value.Ok) ok++;
            }
            if (UploadInstagram && Connected(SocialPlatform.Instagram))
            {
                if (Simulated) Simulator.ExpectMedia(Bytes(media));
                d.Instagram = await Gateway.UploadAsync(SocialPlatform.Instagram, media, d.Meta).ConfigureAwait(false);
                if (d.Instagram.Value.Ok) ok++;
            }
            if (ok > 0) Recommender.RecordOutcome(d.Context.Program, d.Context.Category, Math.Clamp(d.Context.Peak / 100f, 0f, 1f));
            LastAction = ok > 0 ? "✅ 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " 업로드 " + ok.ToString(CultureInfo.InvariantCulture) + " 플랫폼" : "⚠ 업로드 대상 없음/실패 — 계정 연동 확인";
        }
        finally { d.Uploading = false; Raise(); }
        return ok;
    }

    private SegmentedChunkStream? _sample;

    /// <summary>초안의 업로드 미디어 — v17.69 릴 패키지(없으면 샘플 초안용 합성 3 s 클립 · 지연 1 회 생성).</summary>
    public SegmentedChunkStream? MediaFor(int reelId)
    {
        if (_reels?.FindReel(reelId)?.Media is { } m) return m;
        if (reelId != 0) return null;
        return _sample ??= LiveToReelsFactoryStudioRunner.SyntheticClip(3);
    }

    private static byte[] Bytes(SegmentedChunkStream m) { var b = new byte[m.Length]; m.ReadAt(0, b); return b; }

    // ══════════════════════ 자동 업로드 워커 ══════════════════════
    public void SetAutoUpload(bool on, bool startWorker = true) { AutoUpload = on; if (on && startWorker) Start(); LastAction = on ? "🚀 릴 자동 업로드 ON" : "⏸ 자동 업로드 OFF"; Raise(); }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _loop = new Thread(Loop) { IsBackground = true, Name = "PF.Publisher" };
        try { _loop.Priority = ThreadPriority.BelowNormal; } catch (PlatformNotSupportedException) { }
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
        while (_running)
        {
            try { if (_work is null || _work.IsCompleted) _work = PollOnceAsync(); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Publisher.Loop", ex); }
            Thread.Sleep(500);
        }
    }

    /// <summary>신규 릴 → 초안 생성 → (자동) 업로드 1 회.</summary>
    public async Task PollOnceAsync()
    {
        if (_reels is null) return;
        for (var i = _reels.ReelCount - 1; i >= 0; i--)
        {
            var r = _reels.Reel(i);
            if (r is null || r.Id <= _lastReel || r.Media is null || r.Phase is FactoryReelPhase.Clipping or FactoryReelPhase.Packaging or FactoryReelPhase.Failed) continue;
            _lastReel = r.Id;
            var d = await GenerateDraftAsync(r.Id, ContextFor(r)).ConfigureAwait(false);
            if (AutoUpload && r.Media is { } m && await UploadDraftAsync(d.ReelId, m).ConfigureAwait(false) > 0) AutoUploads++;
        }
    }

    // ══════════════════════ 프리셋 · 1-Click ══════════════════════
    public void ApplyPreset(CreatorPreset p, List<string> log)
    {
        if (_station is not null)
        {
            var talk = _station.Mcr.Talkshow;
            if (talk.Active != p.Avatars) { talk.SetActive(p.Avatars); log.Add("👥 아바타 " + p.Avatars.ToString(CultureInfo.InvariantCulture) + " 인 배치"); }
            _station.Mcr.SetAutoDirect(p.AutoDirect); log.Add(p.AutoDirect ? "🤖 MCR 자동 디렉팅" : "🎛 MCR 비트 컷(수동 디렉팅)");
            _station.Failover.HoldMs = p.FailoverHoldMs; log.Add("🛡 페일오버 복귀 보류 " + (p.FailoverHoldMs / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s");
            if (_station.Mcr.EnvSplats != p.EnvSplats) log.Add("🌌 3DGS 세트 " + p.EnvSplats.ToString("N0", CultureInfo.InvariantCulture) + " 스플랫(현재 " + _station.Mcr.EnvSplats.ToString("N0", CultureInfo.InvariantCulture) + " · 다음 MCR 생성부터)");
        }
        else log.Add("⚠ 방송국 미결선 — 편성 · MCR 설정 건너뜀");
        if (_beat is not null) { _beat.SetTempo(p.BgmBpm); _beat.Bgm.Level = p.BgmLevel; log.Add("🥁 BGM " + p.BgmBpm.ToString("0", CultureInfo.InvariantCulture) + " BPM · 레벨 " + (p.BgmLevel * 100).ToString("0", CultureInfo.InvariantCulture) + "%"); }
        if (_reels is not null) { _reels.SetThreshold(p.ReelsThreshold); log.Add("🔥 릴스 클라이맥스 임계 " + p.ReelsThreshold.ToString("0", CultureInfo.InvariantCulture)); }
        UploadYouTube = p.UploadYouTube; UploadInstagram = p.UploadInstagram; Privacy = p.Privacy;
        log.Add("📤 업로드 대상 " + (p.UploadYouTube ? "YouTube " : "") + (p.UploadInstagram ? "Instagram " : "") + "· " + p.Privacy);
        if (_station is not null) { _station.CueProgram(p.Program); log.Add("🎬 즉시 편성 " + MasterStationEpgSchedulerEngine.KindName(p.Program)); }
    }

    /// <summary>[🚀 방송 시작 및 자동 업로드 활성화] — 프리셋 적용 → 릴스 팩토리 · 방송국 · 자동 업로드 가동.</summary>
    public IReadOnlyList<string> OneClickStart(CreatorPreset p)
    {
        var log = Presets.Apply(p, this);
        void Step(string ok, Action a)
        {
            try { a(); log.Add("✅ " + ok); }
            catch (Exception ex) when (ex is InvalidOperationException or ThreadStateException or OutOfMemoryException) { log.Add("⚠ " + ok + " 실패: " + ex.Message); PromptFlowStudio.Program.Log("Publisher.OneClick", ex); }
        }
        if (_reels is not null) Step("릴스 팩토리 가동(클라이맥스 감지 · 자동 배포)", () => { _reels.SetAutoPublish(true); _reels.Start(); });
        if (_station is not null) Step("24/7 방송국 송출", () => _station.Start());
        Step("릴 자동 업로드", () => SetAutoUpload(true));
        if (!Connected(SocialPlatform.YouTube) && !Connected(SocialPlatform.Instagram)) log.Add("ℹ 연동된 계정 없음 — 업로드는 계정 연동 후 자동 시작");
        OneClicks++;
        LastChecklist = log;
        LastAction = "🚀 1-Click · " + p.Icon + " " + p.Name + " · " + log.Count.ToString(CultureInfo.InvariantCulture) + " 단계"; Raise();
        return log;
    }

    public void StopAll()
    {
        SetAutoUpload(false);
        _station?.Stop();
        _reels?.Stop();
        Stop();
        LastAction = "⏹ 방송 · 자동 업로드 정지"; Raise();
    }

    /// <summary>현재 세팅 캡처 → 사용자 프리셋 저장.</summary>
    public bool SaveCurrentAsPreset(string name, string icon = "⭐")
    {
        var p = new CreatorPreset
        {
            Name = name.Trim(), Icon = icon, Kind = CreatorPresetKind.Custom, Program = _station?.Active ?? StationProgramKind.Talkshow,
            EnvSplats = _station?.Mcr.EnvSplats ?? 150_000, Avatars = _station?.Mcr.Talkshow.Active ?? 3, BgmBpm = _beat?.Bgm.Bpm ?? 120, BgmLevel = _beat?.Bgm.Level ?? 0.5f,
            AutoDirect = _station?.Mcr.AutoDirect ?? true, ReelsThreshold = _reels?.Detector.Threshold ?? 85f, UploadYouTube = UploadYouTube, UploadInstagram = UploadInstagram, Privacy = Privacy,
            FailoverHoldMs = _station?.Failover.HoldMs ?? 3_000,
        };
        var ok = Presets.Upsert(p);
        LastAction = ok ? "💾 프리셋 '" + p.Name + "' 저장" : "⚠ " + Presets.LastError; Raise();
        return ok;
    }

    public int RecommendNext() { var n = Recommender.Recommend(DateTime.Now.Hour); Raise(); return n; }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Publisher.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Gateway.Dispose();
        Vault.Dispose();
        _http?.Dispose();
        _sample?.Dispose();
        if (_ephemeralKey is not null) CryptographicOperations.ZeroMemory(_ephemeralKey);
        OnChange = null;
    }

    public string Summary() =>
        "YT " + SocialMediaApiGatewayEngine.StateName(Gateway.State(SocialPlatform.YouTube)) + " · IG " + SocialMediaApiGatewayEngine.StateName(Gateway.State(SocialPlatform.Instagram))
        + " · 업로드 " + (Gateway.Uploads(SocialPlatform.YouTube) + Gateway.Uploads(SocialPlatform.Instagram)).ToString(CultureInfo.InvariantCulture) + " · 초안 " + DraftCount.ToString(CultureInfo.InvariantCulture)
        + (Simulated ? " · 시뮬레이터" : " · 실 API");
}
