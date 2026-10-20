using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Autonomous;
using PromptFlowStudio.Services.Broadcast;
using PromptFlowStudio.Services.Enterprise;
using PromptFlowStudio.Services.Interactive;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.MultiEngine;
using PromptFlowStudio.Services.Security;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.Telemetry;
using PromptFlowStudio.Services.Wan;
using Timer = System.Threading.Timer;

namespace PromptFlowStudio.Services.Master;

public enum ModuleHealth : byte { Online, Busy, Idle, Degraded, Unavailable }

/// <summary>v16.30.0 모듈 상태 셀(매트릭스 1칸).</summary>
public sealed record ModuleStatus(string Id, string Version, string Name, string Category, ModuleHealth Health, string Metric, string? Activity);

/// <summary>v16.30.0 마스터 파이프라인 단계(실행 순서).</summary>
public enum MasterStage : byte { Plan, CharacterId, Generate, LipSync, Dubbing, Provenance, Qa, Publish, Live, Interactive, Director, DeltaSync }

public enum StageOutcome : byte { Pending, Running, Done, Skipped, Failed }

public sealed record StageResult(MasterStage Stage, StageOutcome Outcome, string Message, TimeSpan Duration, string? Artifact = null);

/// <summary>v16.30.0 마스터 실행 요청 — 단계별 옵션.</summary>
public sealed record MasterRunRequest(
    StoryboardProject Project, string Theme = "general", bool ReuseExistingVideo = true, bool LockIdentity = true, string? NarrationAudioPath = null,
    IReadOnlyList<string>? DubbingLocales = null, bool SignProvenance = true, IReadOnlyList<SocialPublishTarget>? PublishTargets = null, bool PublishPublic = false,
    bool AddToLiveLoop = false, bool BuildInteractive = false, bool FeedDirector = true, string? DeltaStoreFolder = null);

/// <summary>v16.30.0 단계 실행 컨텍스트(단계 간 산출물 전달).</summary>
public sealed class MasterContext
{
    public required MasterRunRequest Request { get; init; }
    public StoryboardProject Project => Request.Project;
    public string? MasterVideo { get; set; }
    public bool QaRejected { get; set; }
    public List<string> PostIds { get; } = new();
    public List<string> Artifacts { get; } = new();
}

/// <summary>
/// [PromptFlowStudio] v16.30.0 👑 유니버설 마스터 스튜디오 오케스트레이터 — 30개 모듈 상태 매트릭스 + 기획→생성→립싱크→캐릭터ID→더빙→C2PA→QA→게시→라이브→인터랙티브→자율 디렉터→델타 동기화 단일 파이프라인.
///   ▸ 모듈 매트릭스: 서비스 제공자에서 지연 해석(<see cref="IServiceProvider.GetService"/> · 미등록 = Unavailable · 모바일 안전) · 2초 동기 틱 스냅샷 캐시(HUD 는 캐시만 읽음 · 예외 격리).
///   ▸ 파이프라인: 단계별 실행기(기본 = 실제 서비스 결선 · <see cref="SetExecutor"/> 로 교체 가능) · 제로 트러스트 인가(pipeline:{단계}:run) · 일시 오류 1회 재시도
///     · 핵심 단계(Generate) 실패 또는 QA Reject 시 이후 게시/라이브 차단 · 그 외 실패는 [부분 구제](산출물 보존 후 다음 단계) · CTS 취소 · 단계 결과 이력.
/// </summary>
public sealed class UniversalMasterStudioOrchestrator : IDisposable
{
    public static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(2);
    public static readonly MasterStage[] Order =
    {
        MasterStage.Plan, MasterStage.CharacterId, MasterStage.Generate, MasterStage.LipSync, MasterStage.Dubbing, MasterStage.Provenance,
        MasterStage.Qa, MasterStage.Publish, MasterStage.Live, MasterStage.Interactive, MasterStage.Director, MasterStage.DeltaSync,
    };

    private sealed record ModuleDef(string Id, string Version, string Name, string Category, Type Service, Func<object, (ModuleHealth Health, string Metric, string? Activity)> Probe);

    private readonly IServiceProvider _sp;
    private readonly ZeroTrustSecurityShieldService? _shield;
    private readonly object _gate = new();
    private readonly List<ModuleDef> _modules = new();
    private readonly Dictionary<MasterStage, Func<MasterContext, CancellationToken, Task<StageResult>>> _exec = new();
    private readonly HashSet<MasterStage> _disabled = new();
    private readonly StageResult[] _stages = new StageResult[Order.Length];
    private ModuleStatus[] _snapshot = Array.Empty<ModuleStatus>();
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private int _runs, _failedRuns;
    private bool _disposed;

    public UniversalMasterStudioOrchestrator(IServiceProvider sp, ZeroTrustSecurityShieldService? shield = null)
    {
        _sp = sp;
        _shield = shield;
        RegisterModules();
        RegisterDefaultExecutors();
        ResetStages();
    }

    public event Action? OnChange;

    /// <summary>파이프라인 실행에 제로 트러스트 세션 필수(기본 켜짐).</summary>
    public bool RequireAuthorization { get; set; } = true;
    public bool IsRunning => Volatile.Read(ref _cts) is not null;
    public int Runs => Volatile.Read(ref _runs);
    public int FailedRuns => Volatile.Read(ref _failedRuns);
    public MasterStage? CurrentStage { get; private set; }
    public string? LastAction { get; private set; }
    public IReadOnlyList<StageResult> Stages { get { lock (_gate) return (StageResult[])_stages.Clone(); } }
    public IReadOnlyList<ModuleStatus> Matrix => Volatile.Read(ref _snapshot);
    public int ModuleCount => _modules.Count;

    public int OnlineModules
    {
        get
        {
            var n = 0;
            foreach (var m in Volatile.Read(ref _snapshot)) if (m.Health is ModuleHealth.Online or ModuleHealth.Busy or ModuleHealth.Idle) n++;
            return n;
        }
    }

    public bool IsStageEnabled(MasterStage s) { lock (_gate) return !_disabled.Contains(s); }
    public void SetStageEnabled(MasterStage s, bool on) { lock (_gate) { if (on) _disabled.Remove(s); else _disabled.Add(s); } Raise(); }
    public void SetExecutor(MasterStage s, Func<MasterContext, CancellationToken, Task<StageResult>> exec) { lock (_gate) _exec[s] = exec; }

    public static string StageLabel(MasterStage s) => s switch
    {
        MasterStage.Plan => "🧭 기획", MasterStage.CharacterId => "🧬 캐릭터 ID", MasterStage.Generate => "🎬 AI 생성", MasterStage.LipSync => "👄 립싱크",
        MasterStage.Dubbing => "🌐 다국어 더빙", MasterStage.Provenance => "🔏 C2PA", MasterStage.Qa => "🔍 QA", MasterStage.Publish => "🚀 소셜 게시",
        MasterStage.Live => "📺 24/7 라이브", MasterStage.Interactive => "🔀 인터랙티브", MasterStage.Director => "🤖 자율 디렉터", _ => "☁️ 델타 동기화",
    };

    // ══════════════════════ 모듈 매트릭스 ══════════════════════

    private void Reg<T>(string id, string ver, string name, string cat, Func<T, (ModuleHealth, string, string?)> probe) where T : class
        => _modules.Add(new ModuleDef(id, ver, name, cat, typeof(T), o => probe((T)o)));

    private static ModuleHealth H(bool busy, bool available = true) => !available ? ModuleHealth.Degraded : busy ? ModuleHealth.Busy : ModuleHealth.Idle;

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private void RegisterModules()
    {
        Reg<SecureSecretVault>("vault", "10.7", "시크릿 볼트(DPAPI)", "보안", s => (ModuleHealth.Online, "해석 " + N(s.Resolved), s.LastAction));
        Reg<StoryboardJobManager>("jobs", "core", "스토리보드 잡 매니저", "생성", s => (s.Enabled ? H(s.IsRunning || s.IsMerging) : ModuleHealth.Degraded, s.IsRunning ? "생성 중" : "대기", null));
        Reg<IWanApiService>("wan", "core", "Alibaba Wan 2.1", "생성", s => s is IVideoGenerationEngine e && e.Enabled ? (ModuleHealth.Online, "API 키 구성", null) : (ModuleHealth.Degraded, "키 없음", null));
        Reg<ComfyUiVideoEngine>("comfy", "15.14", "로컬 GPU ComfyUI", "생성", s => s.Enabled ? (ModuleHealth.Online, "실행 파일 구성", null) : (ModuleHealth.Degraded, "미구성", null));
        Reg<GeminiClient>("gemini", "core", "Gemini 멀티모달", "AI", s => s.Enabled ? (ModuleHealth.Online, "키 구성", null) : (ModuleHealth.Degraded, "키 없음", null));
        Reg<DirectSocialPublisherBridge>("social", "16.17", "소셜 직접 게시", "배포", s => (ModuleHealth.Idle, "작업 " + N(s.Jobs.Count), null));
        Reg<AutomatedVideoQaInspector>("qa", "16.17", "AI 품질 검수", "품질", s => (H(s.IsBusy, s.IsAvailable), s.IsAvailable ? "ffmpeg 준비" : "ffmpeg 없음", null));
        Reg<ProductionCostTrackerService>("cost", "16.18", "비용·예산 추적", "운영", s => (s.State == BudgetState.Exceeded ? ModuleHealth.Degraded : ModuleHealth.Online, "오늘 $" + s.TodayUsd.ToString("0.00", CultureInfo.InvariantCulture), s.State.ToString()));
        Reg<CommercialBrandCampaignService>("brand", "16.19", "브랜드 캠페인", "마케팅", s => (H(s.IsBusy, s.IsAvailable), "캠페인 " + N(s.TotalCampaigns), s.LastStage));
        Reg<AiAudioDrivenLipSyncService>("lipsync", "16.20", "AI 립싱크", "후반", s => (H(false, s.IsAvailable), s.IsAvailable ? "준비" : "ffmpeg 없음", null));
        Reg<CharacterIdentityLockService>("identity", "16.22", "캐릭터 Face-ID 락", "후반", s => (H(false, s.IsAvailable), "정체성 구문 주입", null));
        Reg<ViralHookVariantGeneratorService>("viral", "16.23", "바이럴 훅 디렉터", "마케팅", s => (H(s.IsBusy, s.IsAvailable), s.IsAvailable ? "준비" : "제한", s.LastAction));
        Reg<MultilingualVoiceCloningDubbingService>("dubbing", "16.24", "다국어 더빙", "후반", s => (H(s.IsBusy, s.IsAvailable), "34 로캘", s.LastAction));
        Reg<AiLiveLoopStreamerService>("liveloop", "16.25", "24/7 루프 송출", "라이브", s => (s.IsLive ? ModuleHealth.Busy : ModuleHealth.Idle, s.State.ToString(), s.LastMessage));
        Reg<LiveChatAiReactionEngine>("livechat", "16.25", "라이브 채팅 AI", "라이브", s => (s.IsRunning ? ModuleHealth.Busy : ModuleHealth.Idle, "응답 " + N(s.RepliedCount), s.LastAction));
        Reg<RealtimeAvatarPuppeteerService>("avatar", "16.25", "실시간 아바타", "라이브", s => (ModuleHealth.Idle, "발화 " + N(s.SpokenCount), s.LastAction));
        Reg<InteractiveChoiceBranchingService>("branch", "16.26", "분기형 비디오", "인터랙티브", s => (H(s.IsBusy), "노드 " + N(s.NodeCount), s.LastAction));
        Reg<ShoppableVideoOverlayEngine>("shop", "16.26", "쇼핑 핫스팟", "인터랙티브", s => (H(s.IsBusy), "태그 " + N(s.TagCount), s.LastAction));
        Reg<InteractiveConversionAnalytics>("ixstats", "16.26", "전환율 분석", "인터랙티브", s => (ModuleHealth.Online, "이벤트 " + N(s.TotalEvents), s.LastAction));
        Reg<CloudRenderFarmDispatcherService>("farm", "16.27", "클라우드 렌더 팜", "엔터프라이즈", s => (s.IsRunning ? ModuleHealth.Busy : ModuleHealth.Idle, "노드 " + N(s.OnlineNodes) + "/" + N(s.Nodes.Count), s.LastAction));
        Reg<TeamCollaborationSessionBridge>("team", "16.27", "팀 협업 RBAC", "엔터프라이즈", s => (s.Mode == TeamBridgeMode.Offline ? ModuleHealth.Idle : ModuleHealth.Online, "접속 " + N(s.ConnectionCount), s.LastAction));
        Reg<C2paContentProvenanceEngine>("c2pa", "16.27", "C2PA 출처 증명", "보안", s => (H(s.IsBusy, s.FfmpegAvailable), "서명 " + N(s.SignedCount), s.LastAction));
        Reg<AutonomousCampaignDirectorService>("director", "16.28", "AI 자율 디렉터", "자율", s => (s.IsAutonomous ? ModuleHealth.Busy : ModuleHealth.Idle, "게시 " + N(s.PublishedTotal), s.LastAction));
        Reg<PerformanceFeedbackLoopEngine>("feedback", "16.28", "성과 피드백 루프", "자율", s => (H(s.IsBusy), "G" + N(s.Generation), s.LastAction));
        Reg<CampaignRoiOptimizationEngine>("roi", "16.28", "ROI 최적화", "자율", s => (ModuleHealth.Online, "재배분 " + N(s.Rebalances), s.LastAction));
        Reg<GenerativeVideoMultiEngineRouter>("router", "16.29", "멀티 엔진 루터", "생성", s => (s.Enabled ? ModuleHealth.Online : ModuleHealth.Degraded, "라우팅 " + N(s.Routed), s.LastAction));
        Reg<GenerativePromptTranspilerEngine>("transpiler", "16.29", "프롬프트 트랜스파일러", "생성", _ => (ModuleHealth.Online, "4 방언", null));
        Reg<PluginExtensionMarketplaceFramework>("plugins", "16.29", "플러그인 런타임", "확장", s => (ModuleHealth.Online, "플러그인 " + N(s.Plugins.Count), s.LastAction));
        Reg<ZeroTrustSecurityShieldService>("shield", "16.30", "제로 트러스트 쉴드", "보안", s => (s.IsUnlocked ? ModuleHealth.Online : ModuleHealth.Degraded, s.KeyProtection, s.LastAction));
        Reg<CloudDeltaAssetSyncEngine>("delta", "16.30", "델타 자산 동기화", "보안", s => (H(s.IsBusy), "전송 " + N(s.TotalSentChunks) + " · 중복 " + N(s.TotalDedupedChunks), s.LastAction));
    }

    /// <summary>모듈 매트릭스 스냅샷 갱신(프로브 예외 격리 · 미등록 = Unavailable).</summary>
    public IReadOnlyList<ModuleStatus> RefreshMatrix()
    {
        var arr = new ModuleStatus[_modules.Count];
        for (var i = 0; i < _modules.Count; i++)
        {
            var m = _modules[i];
            object? svc = null;
            try { svc = _sp.GetService(m.Service); }
            catch (Exception ex) { Program.Log("Master.Resolve." + m.Id, ex); }
            if (svc is null) { arr[i] = new ModuleStatus(m.Id, m.Version, m.Name, m.Category, ModuleHealth.Unavailable, "미등록", null); continue; }
            try
            {
                var (h, metric, act) = m.Probe(svc);
                arr[i] = new ModuleStatus(m.Id, m.Version, m.Name, m.Category, h, metric, act);
            }
            catch (Exception ex)
            {
                Program.Log("Master.Probe." + m.Id, ex);
                arr[i] = new ModuleStatus(m.Id, m.Version, m.Name, m.Category, ModuleHealth.Degraded, "프로브 오류", ex.Message);
            }
        }
        Volatile.Write(ref _snapshot, arr);
        return arr;
    }

    /// <summary>2초 주기 매트릭스 관제 시작(동기 틱 · 예외 격리 · 멱등).</summary>
    public void StartMonitoring()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null) return;
            _timer = new Timer(static s => ((UniversalMasterStudioOrchestrator)s!).OnTick(), this, TimeSpan.Zero, SnapshotInterval);
        }
    }

    public void StopMonitoring()
    {
        Timer? t;
        lock (_gate) { t = _timer; _timer = null; }
        t?.Dispose();
    }

    private void OnTick()
    {
        try { RefreshMatrix(); Raise(); }
        catch (Exception ex) { Program.Log("Master.Tick", ex); }
    }

    // ══════════════════════ 파이프라인 ══════════════════════

    private void ResetStages()
    {
        lock (_gate) for (var i = 0; i < Order.Length; i++) _stages[i] = new StageResult(Order[i], StageOutcome.Pending, "", TimeSpan.Zero);
    }

    private void SetStage(StageResult r)
    {
        lock (_gate) _stages[Array.IndexOf(Order, r.Stage)] = r;
        Raise();
    }

    /// <summary>
    /// 마스터 파이프라인 실행 — 단계 순서대로 인가 → 실행(일시 오류 1회 재시도) → 결과. 실행 중이면 null.
    /// </summary>
    public async Task<IReadOnlyList<StageResult>?> RunAsync(MasterRunRequest request, string? sessionToken, CancellationToken ct = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_gate)
        {
            if (_disposed || _cts is not null) { cts.Dispose(); return null; }
            _cts = cts;
        }
        ResetStages();
        _pipelineToken = sessionToken;
        Interlocked.Increment(ref _runs);
        var ctx = new MasterContext { Request = request, MasterVideo = request.ReuseExistingVideo && File.Exists(request.Project.FinalVideoPath) ? request.Project.FinalVideoPath : null };
        var failed = false;
        try
        {
            foreach (var stage in Order)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (!IsStageEnabled(stage)) { SetStage(new(stage, StageOutcome.Skipped, "사용자 비활성", TimeSpan.Zero)); continue; }
                if (RequireAuthorization)
                {
                    var d = _shield?.Authorize(sessionToken, "pipeline:" + stage.ToString().ToLowerInvariant(), "run") ?? new ZtDecision(false, "제로 트러스트 쉴드 미등록");
                    if (!d.Allowed) { SetStage(new(stage, StageOutcome.Failed, "⛔ 인가 거부: " + d.Reason, TimeSpan.Zero)); failed = true; break; }
                }
                if ((stage is MasterStage.Publish or MasterStage.Live) && (ctx.QaRejected || ctx.MasterVideo is null))
                {
                    SetStage(new(stage, StageOutcome.Skipped, ctx.QaRejected ? "QA 반려 — 배포 차단" : "마스터 영상 없음", TimeSpan.Zero));
                    continue;
                }
                Func<MasterContext, CancellationToken, Task<StageResult>>? exec;
                lock (_gate) _exec.TryGetValue(stage, out exec);
                if (exec is null) { SetStage(new(stage, StageOutcome.Skipped, "실행기 없음", TimeSpan.Zero)); continue; }
                CurrentStage = stage;
                SetStage(new(stage, StageOutcome.Running, "실행 중", TimeSpan.Zero));
                LastAction = StageLabel(stage) + " 실행";
                var sw = Stopwatch.StartNew();
                StageResult r;
                try { r = await exec(ctx, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or TimeoutException)
                {
                    Program.Log("Master.Stage." + stage, ex);
                    try { r = await exec(ctx, cts.Token).ConfigureAwait(false); }   // 일시 오류 1회 재시도
                    catch (Exception ex2) when (ex2 is not OperationCanceledException) { r = new(stage, StageOutcome.Failed, "재시도 실패: " + ex2.Message, sw.Elapsed); }
                }
                catch (Exception ex) { Program.Log("Master.Stage." + stage, ex); r = new(stage, StageOutcome.Failed, ex.Message, sw.Elapsed); }
                r = r with { Duration = sw.Elapsed };
                SetStage(r);
                if (r.Artifact is not null) ctx.Artifacts.Add(r.Artifact);
                if (r.Outcome == StageOutcome.Failed)
                {
                    failed = true;
                    if (stage == MasterStage.Generate && ctx.MasterVideo is null) break;   // 핵심 단계 — 이후 진행 불가
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (CurrentStage is { } cs) SetStage(new(cs, StageOutcome.Failed, "취소됨", TimeSpan.Zero));
            failed = true;
        }
        finally
        {
            CurrentStage = null;
            lock (_gate) { if (ReferenceEquals(_cts, cts)) _cts = null; }
            cts.Dispose();
            if (failed) Interlocked.Increment(ref _failedRuns);
            LastAction = failed ? "⚠ 마스터 파이프라인 완료([부분 구제] 포함)" : "✅ 마스터 파이프라인 완료";
            Raise();
        }
        return Stages;
    }

    public void Cancel()
    {
        try { Volatile.Read(ref _cts)?.Cancel(); } catch (ObjectDisposedException) { }
    }

    // ══════════════════════ 기본 실행기(실제 서비스 결선) ══════════════════════

    private T? Svc<T>() where T : class
    {
        try { return _sp.GetService(typeof(T)) as T; } catch (Exception ex) { Program.Log("Master.Svc", ex); return null; }
    }

    private static StageResult Done(MasterStage s, string m, string? artifact = null) => new(s, StageOutcome.Done, m, TimeSpan.Zero, artifact);
    private static StageResult Skip(MasterStage s, string m) => new(s, StageOutcome.Skipped, m, TimeSpan.Zero);
    private static StageResult Fail(MasterStage s, string m) => new(s, StageOutcome.Failed, m, TimeSpan.Zero);

    private void RegisterDefaultExecutors()
    {
        _exec[MasterStage.Plan] = async (c, ct) =>
        {
            if (c.Project.Scenes.Count > 0) return Done(MasterStage.Plan, "기존 스토리보드 " + N(c.Project.Scenes.Count) + "컷 사용");
            var dir = Svc<AutonomousCampaignDirectorService>();
            if (dir is null) return Fail(MasterStage.Plan, "스토리보드가 비었고 자율 디렉터가 없습니다.");
            var built = await dir.BuildStoryboardAsync(new ScheduleSlot { Theme = c.Request.Theme, Keyword = c.Request.Theme, Format = "story", At = DateTimeOffset.Now }, ct).ConfigureAwait(false);
            foreach (var s in built.Scenes) c.Project.Scenes.Add(s);
            if (string.IsNullOrWhiteSpace(c.Project.Title) || c.Project.Title == "새 스토리보드") c.Project.Title = built.Title;
            return Done(MasterStage.Plan, "AI 기획 " + N(built.Scenes.Count) + "컷 · " + built.Title);
        };
        _exec[MasterStage.CharacterId] = (c, ct) =>
        {
            if (!c.Request.LockIdentity) return Task.FromResult(Skip(MasterStage.CharacterId, "정체성 락 꺼짐"));
            var n = 0;
            foreach (var s in c.Project.Scenes)
            {
                var (p, neg) = CharacterIdentityLockService.ApplyPrompt(s.Prompt, s.NegativePrompt);
                if (p != s.Prompt || neg != s.NegativePrompt) n++;
                s.Prompt = p; s.NegativePrompt = neg;
            }
            return Task.FromResult(Done(MasterStage.CharacterId, "정체성 구문 주입 " + N(n) + "컷"));
        };
        _exec[MasterStage.Generate] = async (c, ct) =>
        {
            if (c.MasterVideo is not null) return Done(MasterStage.Generate, "기존 최종 영상 재사용", c.MasterVideo);
            var jobs = Svc<StoryboardJobManager>();
            if (jobs is null || !jobs.Enabled) return Fail(MasterStage.Generate, "생성 엔진 미구성(데스크톱 전용)");
            if (jobs.IsRunning || jobs.IsMerging) return Fail(MasterStage.Generate, "다른 생성 작업 진행 중");
            using var reg = ct.Register(jobs.Cancel);
            await jobs.StartAsync(c.Project).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(c.Project.FinalVideoPath) || !File.Exists(c.Project.FinalVideoPath)) return Fail(MasterStage.Generate, "생성/병합 실패");
            c.MasterVideo = c.Project.FinalVideoPath;
            return Done(MasterStage.Generate, "생성·병합 완료", c.MasterVideo);
        };
        _exec[MasterStage.LipSync] = async (c, ct) =>
        {
            if (c.Request.NarrationAudioPath is not { } audio || !File.Exists(audio)) return Skip(MasterStage.LipSync, "나레이션 오디오 없음");
            if (c.MasterVideo is null) return Skip(MasterStage.LipSync, "마스터 영상 없음");
            var lip = Svc<AiAudioDrivenLipSyncService>();
            if (lip is null) return Skip(MasterStage.LipSync, "모듈 미등록");
            var outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(c.MasterVideo))!, Path.GetFileNameWithoutExtension(c.MasterVideo) + "_lipsync.mp4");
            var r = await lip.SyncAsync(new AudioLipSyncRequest(c.MasterVideo, audio, outPath), null, ct).ConfigureAwait(false);
            if (!r.Ok || r.OutputPath is null) return Fail(MasterStage.LipSync, "[부분 구제] " + r.Message);
            c.MasterVideo = r.OutputPath;
            return Done(MasterStage.LipSync, "립싱크 " + AiAudioDrivenLipSyncService.LabelOf(r.Backend), r.OutputPath);
        };
        _exec[MasterStage.Dubbing] = async (c, ct) =>
        {
            if (c.Request.DubbingLocales is not { Count: > 0 } locales) return Skip(MasterStage.Dubbing, "더빙 로캘 미지정");
            if (c.MasterVideo is null) return Skip(MasterStage.Dubbing, "마스터 영상 없음");
            var dub = Svc<MultilingualVoiceCloningDubbingService>();
            if (dub is null || !dub.IsAvailable) return Skip(MasterStage.Dubbing, "모듈 미등록/ffmpeg 없음");
            var r = await dub.ExportAsync(c.Project, c.MasterVideo, new DubbingRequest(locales), null, ct).ConfigureAwait(false);
            return r.Ok ? Done(MasterStage.Dubbing, r.Message, r.OutputPath) : Fail(MasterStage.Dubbing, "[부분 구제] " + r.Message);
        };
        _exec[MasterStage.Provenance] = async (c, ct) =>
        {
            if (!c.Request.SignProvenance) return Skip(MasterStage.Provenance, "서명 꺼짐");
            if (c.MasterVideo is null) return Skip(MasterStage.Provenance, "마스터 영상 없음");
            var c2 = Svc<C2paContentProvenanceEngine>();
            if (c2 is null || !c2.FfmpegAvailable) return Skip(MasterStage.Provenance, "모듈 미등록/ffmpeg 없음");
            var team = Svc<TeamCollaborationSessionBridge>();
            var ids = new List<string>(c.Project.Scenes.Count);
            foreach (var s in c.Project.Scenes) ids.Add(s.Id);
            var req = C2paContentProvenanceEngine.RequestFromProject(c.Project, team?.Tenant ?? "local", team?.Display ?? Environment.UserName, team?.AllApproved(ids) == true);
            var r = await c2.ProtectAsync(c.MasterVideo, req, null, ct).ConfigureAwait(false);
            if (!r.Ok || r.VideoPath is null) return Fail(MasterStage.Provenance, "[부분 구제] " + r.Message);
            c.MasterVideo = r.VideoPath;
            return Done(MasterStage.Provenance, "C2PA 서명 · WM " + r.WatermarkId, r.ManifestPath);
        };
        _exec[MasterStage.Qa] = async (c, ct) =>
        {
            if (c.MasterVideo is null) return Skip(MasterStage.Qa, "마스터 영상 없음");
            var qa = Svc<AutomatedVideoQaInspector>();
            if (qa is null || !qa.IsAvailable) return Skip(MasterStage.Qa, "모듈 미등록/ffmpeg 없음");
            var r = await qa.InspectAsync(c.MasterVideo, null, ct).ConfigureAwait(false);
            c.QaRejected = !r.CanPublish;
            return r.CanPublish ? Done(MasterStage.Qa, "QA " + r.Verdict + " · " + r.Message) : Fail(MasterStage.Qa, "QA 반려 · " + r.Message);
        };
        _exec[MasterStage.Publish] = async (c, ct) =>
        {
            if (c.Request.PublishTargets is not { Count: > 0 } targets) return Skip(MasterStage.Publish, "게시 대상 미지정");
            var pub = Svc<DirectSocialPublisherBridge>();
            if (pub is null) return Skip(MasterStage.Publish, "게시 브릿지 미등록(데스크톱 전용)");
            var ok = 0; var msgs = new List<string>();
            foreach (var t in targets)
            {
                var r = await pub.PublishAsync(new SocialPublishRequest(t, c.MasterVideo!, c.Project.Title, c.Project.Title, new[] { c.Request.Theme.Replace(" ", "", StringComparison.Ordinal), "shorts" },
                    null, c.Request.PublishPublic, SkipQa: true), null, ct).ConfigureAwait(false);
                if (r.Ok) { ok++; if (r.PostId is { } id) c.PostIds.Add(id); }
                msgs.Add(AutonomousCampaignDirectorService.PlatformLabel(t) + (r.Ok ? " ✓" : " ✗ " + r.Message));
            }
            var m = (c.Request.PublishPublic ? "공개" : "비공개") + " 게시 " + N(ok) + "/" + N(targets.Count) + " · " + string.Join(" · ", msgs);
            return ok == targets.Count ? Done(MasterStage.Publish, m) : Fail(MasterStage.Publish, "[부분 구제] " + m);
        };
        _exec[MasterStage.Live] = (c, ct) =>
        {
            if (!c.Request.AddToLiveLoop) return Task.FromResult(Skip(MasterStage.Live, "라이브 루프 추가 꺼짐"));
            var live = Svc<AiLiveLoopStreamerService>();
            if (live is null) return Task.FromResult(Skip(MasterStage.Live, "모듈 미등록"));
            return Task.FromResult(live.AddToPlaylist(c.MasterVideo) ? Done(MasterStage.Live, "24/7 플레이리스트 추가 (" + N(live.PlaylistCount) + "개)") : Skip(MasterStage.Live, "이미 플레이리스트에 있음"));
        };
        _exec[MasterStage.Interactive] = async (c, ct) =>
        {
            if (!c.Request.BuildInteractive) return Skip(MasterStage.Interactive, "인터랙티브 패키지 꺼짐");
            var br = Svc<InteractiveChoiceBranchingService>();
            if (br is null) return Skip(MasterStage.Interactive, "모듈 미등록");
            if (br.NodeCount == 0) br.SeedFromProject(c.Project);
            var root = Path.GetDirectoryName(Path.GetFullPath(c.MasterVideo ?? Path.Combine(Path.GetTempPath(), "x.mp4")))!;
            var r = await br.ExportPackageAsync(root, ct).ConfigureAwait(false);
            return r.Ok ? Done(MasterStage.Interactive, r.Message, r.Folder) : Fail(MasterStage.Interactive, "[부분 구제] " + r.Message);
        };
        _exec[MasterStage.Director] = (c, ct) =>
        {
            if (!c.Request.FeedDirector) return Task.FromResult(Skip(MasterStage.Director, "학습 연동 꺼짐"));
            var fb = Svc<PerformanceFeedbackLoopEngine>();
            if (fb is null) return Task.FromResult(Skip(MasterStage.Director, "모듈 미등록"));
            var prompt = c.Project.Scenes.Count > 0 ? c.Project.Scenes[0].Prompt : "";
            foreach (var id in c.PostIds) fb.Ingest(new VideoPerformance(id, c.Request.Theme, prompt, "YouTube", 0, 0, 0, 0, 0, 0, DateTimeOffset.Now));
            return Task.FromResult(c.PostIds.Count > 0 ? Done(MasterStage.Director, "성과 학습 큐 등록 " + N(c.PostIds.Count) + "편") : Skip(MasterStage.Director, "게시 ID 없음 — 학습 대기"));
        };
        _exec[MasterStage.DeltaSync] = async (c, ct) =>
        {
            if (c.Request.DeltaStoreFolder is not { } folder) return Skip(MasterStage.DeltaSync, "동기화 저장소 미지정");
            if (c.MasterVideo is null) return Skip(MasterStage.DeltaSync, "마스터 영상 없음");
            var sync = Svc<CloudDeltaAssetSyncEngine>();
            if (sync is null) return Skip(MasterStage.DeltaSync, "모듈 미등록");
            var asset = "master_" + c.Project.Id;
            var r = await sync.UploadAsync(c.MasterVideo, asset, new LocalFolderChunkStore(folder), _pipelineToken, ct).ConfigureAwait(false);
            return r.Ok ? Done(MasterStage.DeltaSync, r.Message) : Fail(MasterStage.DeltaSync, "[부분 구제] " + r.Message);
        };
    }

    private string? _pipelineToken;

    /// <summary>파이프라인 하위 서비스(델타 동기화 등)에 전달할 세션 토큰.</summary>
    public void BindSession(string? token) => _pipelineToken = token;

    private void Raise()
    {
        var h = OnChange;
        if (h is null) return;
        foreach (var d in h.GetInvocationList())
        {
            try { ((Action)d)(); } catch (Exception ex) { Program.Log("Master.OnChange", ex); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        StopMonitoring();
        Cancel();
        OnChange = null;
    }
}
