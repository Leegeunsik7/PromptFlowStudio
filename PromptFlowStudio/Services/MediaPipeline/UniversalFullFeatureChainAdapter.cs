using System.Diagnostics;
using System.Globalization;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Neuro;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.Vfx;
using PromptFlowStudio.Services.Wan;
using PromptFlowStudio.Services.World;

namespace PromptFlowStudio.Services.MediaPipeline;

public enum ChainPhase : byte { PreRender, Live, PostRender }
public enum StageStatus : byte { Applied, NoChange, Skipped, Unavailable, Pending, Failed, TimedOut }

/// <summary>체인 단계 정의 — 서비스 타입(가용성 탐지) · 선행 단계.</summary>
public sealed record ChainStage(string Id, string Version, string Label, ChainPhase Phase, Type? Service, string[] DependsOn);

public sealed record StageOutcome(string Id, string Label, string Version, ChainPhase Phase, StageStatus Status, int Changes, double Ms, string Detail);

public sealed record ChainReport(IReadOnlyList<StageOutcome> Outcomes, double TotalMs)
{
    public int Count(StageStatus s) { var n = 0; foreach (var o in Outcomes) if (o.Status == s) n++; return n; }
    public int Applied => Count(StageStatus.Applied);
    public int Failed => Count(StageStatus.Failed) + Count(StageStatus.TimedOut);
}

public sealed record ChainOptions(bool MaxQuality = true, bool BoostPrompts = true, bool ReplacePromptWithTuned = false, int StageTimeoutMs = 5000);

/// <summary>단계 실행 컨텍스트(프로젝트 · 옵션 · 이전 단계 산출물).</summary>
public sealed class ChainContext
{
    internal ChainContext(StoryboardProject project, ChainOptions options, IServiceProvider sp) { Project = project; Options = options; Services = sp; }
    public StoryboardProject Project { get; }
    public ChainOptions Options { get; }
    public IServiceProvider Services { get; }
    /// <summary>컷 Id → 오토 프리셋(오토 튜닝 단계 산출).</summary>
    public Dictionary<string, AutoPreset> Presets { get; } = new(StringComparer.Ordinal);
}

public delegate Task<(StageStatus Status, int Changes, string Detail)> ChainExecutor(ChainContext ctx, CancellationToken ct);

/// <summary>
/// [PromptFlow Studio v16.57.0] 🔗 전체 모듈 오케스트레이션 파이프라인 어댑터.
///   ▸ 카탈로그: v16.x 핵심 기능 단계(오토 튜닝 · 4D 장면 그래프 · 프롬프트 부스트 · 표정/긴장 · 립싱크 · 스마트 SFX/덕킹 · 4K 업스케일 · 오토 디렉터 QA · BCI 변조 ·
///     Atmos 마스터 · VFX 리라이팅 · 멀티캠 · 8K 업스케일 렌더) → 위상 정렬(순환 검출).
///   ▸ 가용성: DI 서비스 해석 실패 단계는 Unavailable(체인 계속) · 선행 실패 시 Skipped · 단계별 타임아웃 · 예외 격리([부분 구제] — 앞선 단계 적용분 보존).
///   ▸ 프리렌더/라이브 단계는 내장 실행기로 프로젝트에 즉시 적용. 포스트렌더 단계는 렌더 산출물이 필요하므로 <see cref="RegisterExecutor"/> 결선 전까지 Pending.
/// </summary>
public sealed class UniversalFullFeatureChainAdapter
{
    private readonly IServiceProvider _sp;
    private readonly List<ChainStage> _stages = new();
    private readonly Dictionary<string, ChainExecutor> _exec = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private long _runs;

    public UniversalFullFeatureChainAdapter(IServiceProvider sp)
    {
        _sp = sp;
        Add("auto-tune", "16.57.0", "✨ 컨텍스트 오토 튜닝(카메라·조명·FPS·템포·표정)", ChainPhase.PreRender, typeof(AutoPresetContextTuner));
        Add("scene-graph", "16.54.0", "🌐 4D 장면 그래프 정제 · 네거티브", ChainPhase.PreRender, typeof(NeuralSceneGraphPromptOptimizer), "auto-tune");
        Add("prompt-boost", "16.3.0", "⚡ 시네마틱 프롬프트 부스트", ChainPhase.PreRender, typeof(IWanPromptBoosterService), "auto-tune", "scene-graph");
        Add("lip-sync", "16.20.0", "👄 AI 정밀 립싱크", ChainPhase.PreRender, typeof(AiAudioDrivenLipSyncService), "auto-tune");
        Add("audio-mix", "16.33.0", "🔊 스마트 SFX · 덕킹 · 페이드", ChainPhase.PreRender, null, "auto-tune");
        Add("upscale-4k", "16.34.0", "🔍 4K 업스케일 플래그", ChainPhase.PreRender, typeof(SuperResolution8kUpscalerEngine));
        Add("auto-director", "16.17.0", "🎬 오토 디렉터 QA(재생성 임계)", ChainPhase.PreRender, null, "auto-tune");
        Add("bci-modifier", "16.56.0", "🧠 BCI 바이오피드백 변조 의도", ChainPhase.Live, typeof(BioFeedbackGenerativeMediaModifier), "auto-tune");
        Add("atmos-master", "16.33.0", "🎧 Dolby Atmos 공간 음향 마스터", ChainPhase.PostRender, typeof(PromptFlowStudio.Services.Sound.DolbyAtmosSpatialAudioMapper), "audio-mix");
        Add("vfx-relight", "16.34.0", "💡 VFX 뉴럴 리라이팅", ChainPhase.PostRender, typeof(AiNeuralVfxRelightingEngine));
        Add("multicam", "16.49.0", "🎥 자동 멀티캠 편집", ChainPhase.PostRender, typeof(PromptFlowStudio.Services.Podcast.AutonomousPodcastVideoMultiCamEngine));
        Add("upscale-render", "16.34.0", "🖼 8K/4K 업스케일 렌더", ChainPhase.PostRender, typeof(SuperResolution8kUpscalerEngine), "upscale-4k", "vfx-relight");
        TopoSort();
        RegisterBuiltIns();
    }

    public event Action? OnChange;

    public IReadOnlyList<ChainStage> Stages => _stages;
    public long Runs => Interlocked.Read(ref _runs);
    public ChainReport? LastReport { get; private set; }
    public string? LastAction { get; private set; }

    private void Add(string id, string ver, string label, ChainPhase phase, Type? svc, params string[] deps) => _stages.Add(new ChainStage(id, ver, label, phase, svc, deps));

    /// <summary>선행 관계 위상 정렬(Kahn · 원래 순서 안정) — 순환/미정의 선행은 예외.</summary>
    private void TopoSort()
    {
        var ordered = new List<ChainStage>(_stages.Count);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(_stages.Select(s => s.Id), StringComparer.Ordinal);
        foreach (var s in _stages) foreach (var d in s.DependsOn) if (!ids.Contains(d)) throw new InvalidOperationException("미정의 선행 단계: " + d);
        while (ordered.Count < _stages.Count)
        {
            var progressed = false;
            foreach (var s in _stages)
            {
                if (done.Contains(s.Id) || !s.DependsOn.All(done.Contains)) continue;
                ordered.Add(s); done.Add(s.Id); progressed = true;
            }
            if (!progressed) throw new InvalidOperationException("체인 순환 감지");
        }
        _stages.Clear();
        _stages.AddRange(ordered);
    }

    /// <summary>단계 실행기 결선(외부 렌더 백엔드 · 포스트 처리). 이미 등록된 경우 replace=false 면 거부(중복 차단).</summary>
    public bool RegisterExecutor(string stageId, ChainExecutor executor, bool replace = false)
    {
        lock (_gate)
        {
            if (_stages.All(s => s.Id != stageId)) return false;
            if (_exec.ContainsKey(stageId) && !replace) return false;
            _exec[stageId] = executor;
            return true;
        }
    }

    public bool IsAvailable(ChainStage s) => s.Service is null || _sp.GetService(s.Service) is not null;

    public bool HasExecutor(string id) { lock (_gate) return _exec.ContainsKey(id); }

    /// <summary>전체 체인 실행(프리렌더 → 라이브 → 포스트렌더 순 · 위상 정렬 순서).</summary>
    public async Task<ChainReport> RunAsync(StoryboardProject project, ChainOptions? options = null, CancellationToken ct = default)
    {
        var opt = options ?? new ChainOptions();
        var ctx = new ChainContext(project, opt, _sp);
        var outcomes = new List<StageOutcome>(_stages.Count);
        var status = new Dictionary<string, StageStatus>(StringComparer.Ordinal);
        var total = Stopwatch.StartNew();
        foreach (var s in _stages)
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            (StageStatus St, int N, string D) r;
            var blocked = s.DependsOn.FirstOrDefault(d => status.TryGetValue(d, out var ds) && ds is StageStatus.Failed or StageStatus.TimedOut or StageStatus.Skipped);
            ChainExecutor? exec;
            lock (_gate) _exec.TryGetValue(s.Id, out exec);
            if (blocked is not null) r = (StageStatus.Skipped, 0, "선행 단계 " + blocked + " 미완료");
            else if (!IsAvailable(s)) r = (StageStatus.Unavailable, 0, "서비스 미등록 — 건너뜀");
            else if (exec is null) r = (StageStatus.Pending, 0, s.Phase == ChainPhase.PostRender ? "렌더 산출물 단계 — 백엔드 결선 대기" : "실행기 미결선");
            else
            {
                using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
                d.CancelAfter(opt.StageTimeoutMs);
                try { r = await exec(ctx, d.Token).WaitAsync(d.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { r = (StageStatus.TimedOut, 0, "타임아웃 " + opt.StageTimeoutMs.ToString(CultureInfo.InvariantCulture) + "ms"); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    PromptFlowStudio.Program.Log("Chain." + s.Id, ex);
                    r = (StageStatus.Failed, 0, "[부분 구제] " + ex.GetType().Name + ": " + ex.Message);
                }
            }
            status[s.Id] = r.St;
            outcomes.Add(new StageOutcome(s.Id, s.Label, s.Version, s.Phase, r.St, r.N, sw.Elapsed.TotalMilliseconds, r.D));
        }
        var report = new ChainReport(outcomes, total.Elapsed.TotalMilliseconds);
        Interlocked.Increment(ref _runs);
        LastReport = report;
        LastAction = "🔗 체인 " + report.Applied.ToString(CultureInfo.InvariantCulture) + "/" + outcomes.Count.ToString(CultureInfo.InvariantCulture) + " 적용"
            + (report.Failed > 0 ? " · 실패 " + report.Failed.ToString(CultureInfo.InvariantCulture) : "");
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Chain.OnChange", ex); }
        return report;
    }

    private static Task<(StageStatus, int, string)> Done(int n, string detail) => Task.FromResult(n > 0 ? (StageStatus.Applied, n, detail) : (StageStatus.NoChange, 0, detail));

    private void RegisterBuiltIns()
    {
        _exec["auto-tune"] = (ctx, ct) =>
        {
            var tuner = (AutoPresetContextTuner)ctx.Services.GetService(typeof(AutoPresetContextTuner))!;
            var n = 0;
            foreach (var sc in ctx.Project.Scenes)
            {
                ct.ThrowIfCancellationRequested();
                MediaProbe? probe = null; ImageStats? stats = null;
                var img = sc.ImagePath;
                if (!string.IsNullOrEmpty(img) && File.Exists(img))
                {
                    var p = UnlimitedMediaStreamBufferPipeline.Probe(img);
                    probe = p;
                    if (p.Kind == MediaKind.Image)
                    {
                        UnlimitedMediaStreamBufferPipeline.AnalyzeImage(img, p, 0, out var st);
                        if (st.Valid) stats = st;
                    }
                }
                var preset = tuner.Tune(sc.Prompt, stats, probe);
                ctx.Presets[sc.Id] = preset;
                n += AutoPresetContextTuner.ApplyToScene(sc, preset, ctx.Options.ReplacePromptWithTuned);
                n += AutoPresetContextTuner.ApplyToProject(ctx.Project, preset);
            }
            return Done(n, ctx.Project.Scenes.Count.ToString(CultureInfo.InvariantCulture) + "컷 튜닝");
        };
        _exec["scene-graph"] = (ctx, ct) =>
        {
            var opt = (NeuralSceneGraphPromptOptimizer)ctx.Services.GetService(typeof(NeuralSceneGraphPromptOptimizer))!;
            var n = 0;
            foreach (var sc in ctx.Project.Scenes)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(sc.Prompt)) continue;
                var (_, op) = opt.AnalyzeAndOptimize(sc.Prompt);
                var merged = MergeTokens(sc.NegativePrompt, op.Negative);
                if (!string.Equals(merged, sc.NegativePrompt, StringComparison.Ordinal)) { sc.NegativePrompt = merged; n++; }
            }
            return Done(n, "네거티브 병합 " + n.ToString(CultureInfo.InvariantCulture) + "컷");
        };
        _exec["prompt-boost"] = (ctx, ct) =>
        {
            if (!ctx.Options.BoostPrompts) return Task.FromResult((StageStatus.Skipped, 0, "옵션 꺼짐"));
            var b = (IWanPromptBoosterService)ctx.Services.GetService(typeof(IWanPromptBoosterService))!;
            var n = 0;
            foreach (var sc in ctx.Project.Scenes)
                if (!string.IsNullOrWhiteSpace(sc.Prompt) && !b.IsBoosted(sc.Prompt)) { sc.Prompt = b.Boost(sc.Prompt); n++; }
            return Done(n, "부스트 " + n.ToString(CultureInfo.InvariantCulture) + "컷");
        };
        _exec["lip-sync"] = (ctx, ct) =>
        {
            var lip = (AiAudioDrivenLipSyncService)ctx.Services.GetService(typeof(AiAudioDrivenLipSyncService))!;
            if (!lip.AiAvailable) return Task.FromResult((StageStatus.Skipped, 0, "립싱크 엔진 미구성"));
            var n = 0;
            foreach (var sc in ctx.Project.Scenes)
                if (!sc.UseLipSync && !string.IsNullOrWhiteSpace(sc.VoiceText) && ctx.Presets.TryGetValue(sc.Id, out var p) && p.Subject is ShotSubject.Person or ShotSubject.Baby) { sc.UseLipSync = true; n++; }
            return Done(n, "나레이션 인물 컷 " + n.ToString(CultureInfo.InvariantCulture));
        };
        _exec["audio-mix"] = (ctx, ct) =>
        {
            var pr = ctx.Project;
            var n = 0;
            if (!pr.EnableSmartSfx) { pr.EnableSmartSfx = true; n++; }
            if (!pr.EnableAudioDucking) { pr.EnableAudioDucking = true; n++; }
            if (!pr.UseFadeEffect && pr.Scenes.Count > 1) { pr.UseFadeEffect = true; n++; }
            return Done(n, "SFX · 덕킹 · 페이드");
        };
        _exec["upscale-4k"] = (ctx, ct) =>
        {
            if (!ctx.Options.MaxQuality) return Task.FromResult((StageStatus.Skipped, 0, "최고 화질 옵션 꺼짐"));
            var up = (SuperResolution8kUpscalerEngine)ctx.Services.GetService(typeof(SuperResolution8kUpscalerEngine))!;
            if (!up.AiAvailable) return Task.FromResult((StageStatus.Skipped, 0, "Real-ESRGAN 미설치"));
            if (ctx.Project.UpscaleTo4K) return Done(0, "이미 켜짐");
            ctx.Project.UpscaleTo4K = true;
            return Done(1, "4K 업스케일 켜짐");
        };
        _exec["auto-director"] = (ctx, ct) =>
        {
            var pr = ctx.Project;
            var n = 0;
            if (!pr.AutoDirectorMode) { pr.AutoDirectorMode = true; n++; }
            var th = ctx.Options.MaxQuality ? 80 : 70;
            if (pr.QualityThreshold < th) { pr.QualityThreshold = th; n++; }
            return Done(n, "QA 임계 " + pr.QualityThreshold.ToString(CultureInfo.InvariantCulture));
        };
        _exec["bci-modifier"] = (ctx, ct) =>
        {
            var mod = (BioFeedbackGenerativeMediaModifier)ctx.Services.GetService(typeof(BioFeedbackGenerativeMediaModifier))!;
            if (ctx.Presets.Count == 0) return Done(0, "프리셋 없음");
            double e = 0;
            foreach (var p in ctx.Presets.Values) e += p.Energy;
            e /= ctx.Presets.Count;
            var intent = e > 0.65 ? DirectorIntent.Thrill : e < 0.3 ? DirectorIntent.Calm : DirectorIntent.Balanced;
            if (mod.Intent == intent) return Done(0, "의도 유지 " + intent);
            mod.Intent = intent;
            return Done(1, "변조 의도 → " + intent + "(평균 에너지 " + e.ToString("0.00", CultureInfo.InvariantCulture) + ")");
        };
    }

    private static string MergeTokens(string? a, string b)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in new[] { a ?? "", b })
            foreach (var t in src.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (seen.Add(t)) list.Add(t);
        return string.Join(", ", list);
    }
}
