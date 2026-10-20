using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Wan;
using PromptFlowStudio.Services.Cad;     // v2.6.0 CAD 홀로그램 와이어프레임
using PromptFlowStudio.Services.Trade;   // v2.6.0 바이어 엠블럼 각인 · PDF 스펙 시트
using PromptFlowStudio.Services.Studio;  // v2.7.0 상용 라이선스/쿼터 가드
using PromptFlowStudio.Services.Broadcast; // v2.8.0 멀티GPU · 모션트래킹 · 방송 오버레이
using PromptFlowStudio.Services.Security;  // v3.0.0 엔터프라이즈 감사 로그

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 스토리보드 배치 생성 매니저(관찰 가능 싱글턴) — 여러 컷을 큐로 연쇄 생성한다.
///   컷마다 <see cref="IWanApiService.GenerateAsync"/>(제출→폴링) → <see cref="ILocalMediaService.DownloadAsync"/>(로컬 자산화)를
///   수행하고, 진행/상태를 각 <see cref="StoryboardScene"/> 에 제자리 반영하며 <see cref="OnChange"/> 로 UI 에 방송한다.
///   ▸ 기본은 <b>순차(Sequential)</b> — Rate Limit 안전. <paramref name="parallel"/>=true 면 SemaphoreSlim 으로 동시성 제한한 병렬(Task.WhenAll).
///   ▸ Graceful: 한 컷이 실패해도 배치는 계속 진행(다음 컷) — 종료 시 완료/실패 수를 집계.
///   ▸ 이미 완료(Completed)된 컷은 재실행 시 건너뜀(재생성/과금 방지) — 전체 초기화는 <see cref="ResetAll"/>.
///   ⚠ OnChange 는 백그라운드 스레드에서 올 수 있다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링할 것.
/// </summary>
public sealed class StoryboardJobManager
{
    private readonly VideoEngineSelector _engines;
    private readonly ILocalMediaService _media;
    private readonly IVideoMergeService _merge;
    private readonly IVoiceoverService _voice;
    private readonly ILocalTtsService _localTts;
    private readonly ILipSyncService _lip;
    private readonly EpisodicStorySeriesBuilder? _series;       // v16.22.0 캐릭터 프로필(컷 정합성 신뢰도 측정)
    private readonly AiAudioDrivenLipSyncService? _aiLip;      // v16.20.0 원격 Wav2Lip/MuseTalk → LatentSync 폴백 체인 + 감정 입모양
    private readonly ICharacterAnchorService _anchor;
    private readonly IVideoUpscaleService _upscale;
    private readonly IGeminiDirectorService _director;
    private readonly IGeminiVideoAnalyzerService _analyzer;
    private readonly IGeminiKineticSyncService _kinetic;
    private readonly IGeminiColorGradingService _grader;
    private readonly IGeminiEmotionPacingService _emotion;
    private readonly IGeminiTransitionDirectorService _transition;
    private readonly IGenAudioFoleyService _foley;
    private readonly VisualSfxCaptionService _visualSfx;
    private readonly WanVideoPostEngine _post;   // v1.8.0 무료 결함 자동 보정(디플리커/모션/샤픈)
    private readonly NvencGpuMotionEngine _gpuMotion;   // v2.0.0 GPU 가속 60fps 모션 보간(NVENC/CUDA → CPU 이중 폴백)
    private readonly MultiPlatformRenderEngine _multiRender;   // v2.1.0 멀티 플랫폼 일괄 동시 렌더링
    private readonly TelemetryHudOverlayEngine _telemetryHud;   // v2.5.0 검사/시험 CSV → 계기 HUD 번인
    private readonly MultiLightingBuildingEngine _multiLighting;   // v2.5.0 사옥 3색 조명 멀티배치
    private readonly CadWireframeOverlayEngine _wireframe;      // v2.6.0 CAD 3D 홀로그램 와이어프레임
    private readonly GlobalMultiDubbingEngine _dubbing;         // v2.6.0 글로벌 다국어 더빙/자막
    private readonly AiQualityGuardService _qualityGuard;       // v2.6.0 AI 퀄리티 감수 + 자동 재발주
    private readonly BuyerEmblemEngraveService _emblem;         // v2.6.0 바이어 엠블럼 음각 각인
    private readonly SpecSheetPdfExporter _specSheet;           // v2.6.0 PDF 스펙 시트
    private readonly LicenseQuotaGuard _license;                // v2.7.0 상용 라이선스/쿼터 가드
    private readonly MultiGpuClusterEngine _cluster;            // v2.8.0 멀티 GPU 분산 렌더링
    private readonly MotionTrackingSubtitleService _motionTrack; // v2.8.0 픽셀 모션 트래킹 추종 자막
    private readonly SwitchingCountdownService _countdown;      // v2.8.0 방송 스위칭 카운트다운
    private readonly OnAirBadgeService _onAir;                  // v2.8.0 ON AIR 뱃지
    private readonly VirtualStudioCompositor _virtualStudio;    // v3.0.0 3D 버추얼 스튜디오 크로마키 합성
    private readonly EnterpriseSecurityAudit _audit;           // v3.0.0 변조불가 감사 로그
    private CancellationTokenSource? _cts;

    public StoryboardJobManager(VideoEngineSelector engines, ILocalMediaService media, IVideoMergeService merge,
                                IVoiceoverService voice, ILocalTtsService localTts, ILipSyncService lip,
                                ICharacterAnchorService anchor, IVideoUpscaleService upscale,
                                IGeminiDirectorService director, IGeminiVideoAnalyzerService analyzer,
                                IGeminiKineticSyncService kinetic, IGeminiColorGradingService grader,
                                IGeminiEmotionPacingService emotion, IGeminiTransitionDirectorService transition,
                                IGenAudioFoleyService foley, VisualSfxCaptionService visualSfx,
                                WanVideoPostEngine post, NvencGpuMotionEngine gpuMotion,
                                MultiPlatformRenderEngine multiRender,
                                TelemetryHudOverlayEngine telemetryHud, MultiLightingBuildingEngine multiLighting,
                                CadWireframeOverlayEngine wireframe, GlobalMultiDubbingEngine dubbing,
                                AiQualityGuardService qualityGuard, BuyerEmblemEngraveService emblem, SpecSheetPdfExporter specSheet,
                                LicenseQuotaGuard license,
                                MultiGpuClusterEngine cluster, MotionTrackingSubtitleService motionTrack,
                                SwitchingCountdownService countdown, OnAirBadgeService onAir,
                                VirtualStudioCompositor virtualStudio, EnterpriseSecurityAudit audit,
                                AiAudioDrivenLipSyncService? aiLip = null, EpisodicStorySeriesBuilder? series = null)
    {
        _engines = engines;
        _media = media;
        _merge = merge;
        _voice = voice;
        _localTts = localTts;
        _lip = lip;
        _aiLip = aiLip;
        _series = series;
        _anchor = anchor;
        _upscale = upscale;
        _director = director;
        _analyzer = analyzer;
        _kinetic = kinetic;
        _grader = grader;
        _emotion = emotion;
        _transition = transition;
        _foley = foley;
        _visualSfx = visualSfx;
        _post = post;
        _gpuMotion = gpuMotion;
        _multiRender = multiRender;
        _telemetryHud = telemetryHud;
        _multiLighting = multiLighting;
        _wireframe = wireframe;
        _dubbing = dubbing;
        _qualityGuard = qualityGuard;
        _emblem = emblem;
        _specSheet = specSheet;
        _license = license;
        _cluster = cluster;
        _motionTrack = motionTrack;
        _countdown = countdown;
        _onAir = onAir;
        _virtualStudio = virtualStudio;
        _audit = audit;
    }

    /// <summary>진행/상태 변경 방송(구독: 컴포넌트 OnInitialized / 해지: Dispose).</summary>
    public event Action? OnChange;
    /// <summary>모든 컷 처리 종료(성공/실패/취소 무관) 시 1회 방송.</summary>
    public event Action? OnCompleted;

    public bool Enabled => _engines.Active.Enabled;
    public bool IsRunning { get; private set; }
    /// <summary>ffmpeg 병합 진행 중 여부(생성 종료 후 자동 병합 단계).</summary>
    public bool IsMerging { get; private set; }
    /// <summary>병합 진행률(0–100).</summary>
    public int MergePercent { get; private set; }
    /// <summary>병합 결과/상태 메시지(성공·실패·건너뜀). 실패 시 "병합 실패 (ffmpeg.exe 확인 필요)".</summary>
    public string? MergeMessage { get; private set; }
    /// <summary>ffmpeg 사용 가능 여부(UI 힌트).</summary>
    public bool CanMerge => _merge.IsAvailable;
    public StoryboardProject? Current { get; private set; }

    public int TotalCount => Current?.Scenes.Count ?? 0;
    public int CompletedCount => Current?.Scenes.Count(s => s.Status == SceneStatus.Completed) ?? 0;
    public int FailedCount => Current?.Scenes.Count(s => s.Status == SceneStatus.Failed) ?? 0;
    /// <summary>전체 진행률(0–100) — 컷별 진행률 평균(종료 컷은 100).</summary>
    public int OverallPercent { get; private set; }

    /// <summary>
    /// 스토리보드 전체 생성 시작(실행 중이거나 컷이 없으면 무시). 완료된 컷은 건너뛴다.
    /// </summary>
    /// <param name="project">대상 프로젝트(컷 상태를 제자리 갱신).</param>
    /// <param name="parallel">true 면 병렬(동시 <paramref name="maxParallel"/>개), false 면 순차(기본).</param>
    /// <param name="maxParallel">병렬 시 동시 실행 상한(Rate Limit 보호 · 기본 2).</param>
    public async Task StartAsync(StoryboardProject project, bool parallel = false, int maxParallel = 2)
    {
        if (IsRunning || IsMerging || project.Scenes.Count == 0) return;
        if (!_engines.Active.Enabled) return;   // 엔진 미구성 — 호출부(UI)가 배너로 안내

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Current = project;
        IsRunning = true;
        project.FinalVideoPath = null;   // 이전 병합 결과 무효화
        MergeMessage = null;

        // 완료되지 않은 컷만 대기 상태로 리셋(완료 컷은 보존 → 재실행 시 재생성/과금 방지).
        foreach (var s in project.Scenes)
            if (s.Status != SceneStatus.Completed)
            {
                s.Status = SceneStatus.Pending; s.Percent = 0; s.StatusText = "대기"; s.Error = null;
            }

        RecomputeOverall();
        Changed();

        try
        {
            _audit.Log("project", project.TenantId.Length > 0 ? project.TenantId : "local", "generation-start", $"{project.Id} · {project.Scenes.Count}컷");   // v3.0.0 감사 로그
            // v2.7.0 하드웨어 자동 교정 병렬 스레드(벤치마크가 기록한 값이 있으면 우선).
            var eff = project.TunedParallelThreads > 0 ? project.TunedParallelThreads : maxParallel;
            if (parallel) await RunParallelAsync(project, eff, _cts.Token).ConfigureAwait(false);
            else          await RunSequentialAsync(project, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* 취소 — 각 컷 상태에 이미 반영 */ }
        finally
        {
            IsRunning = false;
            RecomputeOverall();
            Changed();
        }

        await TryAutoMergeAsync(project).ConfigureAwait(false);   // 모두 완료 시 ffmpeg 자동 병합(실패는 Graceful)
        OnCompleted?.Invoke();
    }

    /// <summary>진행 중 배치 취소(남은 컷 중단). 진행 중이던 컷은 Canceled 로 마감.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>모든 컷 상태를 초기화(완료본 포함 전체 재생성 준비). 실행/병합 중이면 무시.</summary>
    public void ResetAll()
    {
        if (IsRunning || IsMerging || Current is null) return;
        foreach (var s in Current.Scenes)
        {
            s.Status = SceneStatus.Pending; s.Percent = 0; s.StatusText = "대기";
            s.Error = null; s.VideoUrl = null; s.LocalPath = null;
        }
        Current.FinalVideoPath = null;
        MergeMessage = null;
        RecomputeOverall();
        Changed();
    }

    // ── 자동 병합(모든 컷 완료 시) ──
    /// <summary>모든 컷이 완료(로컬 파일 보유)면 ffmpeg 로 자동 병합해 FinalVideoPath 를 채운다. 실패는 Graceful(MergeMessage).</summary>
    private async Task TryAutoMergeAsync(StoryboardProject project)
    {
        var scenes = project.Scenes;
        if (scenes.Count == 0) return;
        if (scenes.Any(s => s.Status != SceneStatus.Completed)) return;   // 일부 미완료/실패 → 병합 안 함(조용히)

        // 완료 컷이 모두 로컬 파일을 가져야 병합(원격 폴백만 있는 컷이 있으면 건너뜀).
        var locals = scenes.Where(s => !string.IsNullOrEmpty(s.LocalPath)).Select(s => s.LocalPath!).ToList();
        if (locals.Count != scenes.Count)
        {
            MergeMessage = "일부 컷이 로컬에 저장되지 않아(원격 폴백) 병합을 건너뜁니다.";
            Changed();
            return;
        }
        // 1컷이라도 배속·자막·나레이션·BGM·페이드·워터마크·SFX 가 있으면 병합(후처리) 필요.
        //   ⚠ SFX 는 컷 2개 이상일 때만 효과가 있으나, 워터마크는 단일 컷도 오버레이 필요 → needsPost 로 승격.
        var anyVoice = scenes.Any(s => !string.IsNullOrWhiteSpace(s.VoiceText));
        var hasWatermark = !string.IsNullOrWhiteSpace(project.WatermarkImagePath) && File.Exists(project.WatermarkImagePath);
        var needsPost = project.UseFadeEffect || project.EffectiveSpeed > 1.001 || anyVoice
                        || !string.IsNullOrWhiteSpace(project.BgmPath) || project.UpscaleTo4K
                        || scenes.Any(s => s.UseLipSync)
                        || hasWatermark || (project.EnableSmartSfx && scenes.Count > 1)
                        || (project.UseAiColorGrading && _grader.Enabled)   // 컬러 그레이딩은 단일 컷도 재인코딩 필요
                        || (project.AutoFixVideo && _post.IsAvailable);     // v1.8.0 무료 결함 자동 보정(단일 컷도 디플리커/샤픈 필요)
        if (locals.Count == 1 && !needsPost)
        {
            project.FinalVideoPath = locals[0];   // 후처리 없음 → 그대로 사용
            MergeMessage = "컷이 1개라 병합 없이 사용합니다.";
            Changed();
            return;
        }
        if (!_merge.IsAvailable)
        {
            MergeMessage = "병합 실패 (ffmpeg.exe 확인 필요)";
            Changed();
            return;
        }

        IsMerging = true; MergePercent = 0; MergeMessage = "컷 병합 중…"; Changed();
        try
        {
            var outDir = Path.Combine(_media.MediaRoot, project.Id);   // 미디어 루트 하위 → 가상호스트로 재생
            Directory.CreateDirectory(outDir);
            var outPath = Path.Combine(outDir, "final.mp4");

            var (w, h) = StoryboardProject.ParseResolution(project.ExportResolution);   // 사용자 선택 해상도(쇼츠 포함)

            // ── 나레이션(오프라인 로컬 TTS 우선 → 온라인 Edge TTS 폴백) + 자막 준비 ── 컷 순서대로(locals 와 동일 순서).
            List<string?>? narrs = null;
            List<string?>? subs = null;
            if (anyVoice)
            {
                var offline = project.UseOfflineTts && _localTts.IsAvailable;
                MergeMessage = offline ? $"나레이션 음성 생성 중… ({_localTts.EngineName})" : "나레이션 음성 생성 중…"; Changed();
                narrs = new List<string?>(scenes.Count);
                subs = new List<string?>(scenes.Count);
                foreach (var s in scenes)
                {
                    var txt = (s.VoiceText ?? "").Trim();
                    subs.Add(txt.Length == 0 ? null : txt);
                    if (txt.Length == 0) { narrs.Add(null); continue; }
                    // 오프라인 우선 · 실패 시 Edge 폴백(둘 다 실패면 나레이션 없이 진행).
                    var wav = offline ? await _localTts.SynthesizeAsync(txt, null, CancellationToken.None).ConfigureAwait(false) : null;
                    wav ??= await _voice.SynthesizeAsync(txt, "ko-KR-SunHiNeural", null, CancellationToken.None).ConfigureAwait(false);
                    narrs.Add(wav);
                }

                // ── [파이프라인 순서 2] 립싱크 — 컷별로 나레이션 오디오에 입모양을 맞춘 새 클립으로 교체(엔진 설치 + 나레이션 있을 때) ──
                //    LatentSync 내부에서 30fps CFR 정규화 후 처리 → 업스케일(3)·번인(4) 이전.
                //    v16.20.0: 원격 AI 립싱크(Wav2Lip/MuseTalk) 설정 시 오케스트레이터 경유(실패 시 LatentSync → 원본 유지).
                if ((_lip.IsAvailable || _aiLip?.RemoteConfigured == true) && scenes.Any(x => x.UseLipSync))
                {
                    for (var i = 0; i < scenes.Count && i < locals.Count; i++)
                    {
                        if (!scenes[i].UseLipSync) continue;
                        var na = i < narrs.Count ? narrs[i] : null;
                        if (string.IsNullOrEmpty(na) || !File.Exists(na)) continue;
                        MergeMessage = $"립싱크 처리 중… (컷 {i + 1}/{scenes.Count})"; Changed();
                        var outClip = Path.Combine(_media.MediaRoot, project.Id, $"lipsync_{scenes[i].Id}.mp4");
                        if (_aiLip is not null && _aiLip.AiAvailable)
                        {
                            var ar = await _aiLip.SyncAsync(new AudioLipSyncRequest(locals[i], na!, outClip, scenes[i].VoiceText, scenes[i].Emotion, scenes[i].EmotionIntensity),
                                null, CancellationToken.None).ConfigureAwait(false);
                            // FFmpeg 정렬 폴백 결과는 입모양 미변형 → 병합 단계 오디오 배치와 이중 정렬되지 않게 채택 제외.
                            if (ar.Ok && ar.OutputPath is not null && ar.Backend != LipSyncBackend.FfmpegRealign) locals[i] = ar.OutputPath;
                            continue;
                        }
                        var lr = await _lip.ApplyAsync(locals[i], na!, outClip, null, CancellationToken.None).ConfigureAwait(false);
                        if (lr.Ok && lr.OutputPath is not null) locals[i] = lr.OutputPath;   // 립싱크 클립으로 교체
                    }
                }

                MergeMessage = "컷 병합 중…"; Changed();
            }

            // ── [파이프라인 순서 3] 4K 업스케일 — 병합(자막/UI 번인) '이전'에 각 컷을 딥러닝 업스케일 ──
            //    ⚠ 번인은 항상 마지막(4단계). 자막/오버레이가 업스케일보다 먼저 실행돼 폰트가 뭉개지는 것을 원천 차단.
            if (project.UpscaleTo4K)
            {
                var engine = string.Equals(project.UpscaleEngine, "RealCugan", StringComparison.OrdinalIgnoreCase)
                    ? UpscaleEngine.RealCugan : UpscaleEngine.RealEsrgan;
                if (_upscale.IsAvailable(engine))
                {
                    var upDir = Path.Combine(_media.MediaRoot, project.Id);
                    var anyUp = false;
                    for (var i = 0; i < locals.Count; i++)
                    {
                        var idx = i;
                        var up = new CallbackProgress<double>(pct =>
                        {
                            MergePercent = (int)pct;
                            MergeMessage = $"4K 업스케일 중… (컷 {idx + 1}/{locals.Count}) {(int)pct}%";
                            Changed();
                        });
                        var outUp = Path.Combine(upDir, $"up_{scenes[idx].Id}.mp4");
                        var ur = await _upscale.UpscaleAsync(locals[idx], outUp, 4, engine, up, null, CancellationToken.None).ConfigureAwait(false);
                        if (ur.Ok && ur.OutputPath is not null) { locals[idx] = ur.OutputPath; anyUp = true; }
                        // 실패 컷은 원본(720p) 유지 — 배치 전체는 계속(Graceful).
                    }
                    if (anyUp) { (w, h) = FitTo4K(w, h); }   // 4K 캔버스로 병합 → 자막이 4K 해상도에 번인
                    MergePercent = 0; MergeMessage = "컷 병합 중…"; Changed();
                }
            }

            // ── [파이프라인 순서 3.5] Wan 무료 결함 자동 보정 — 디플리커/모션/샤픈(AutoPilot · 업스케일 이후·번인 이전) ──
            //   ⚠ 항상 번인 이전에 실행돼 자막/UI 가 보정된 프레임 위에 선명하게 얹힌다. ffmpeg 부재/실패는 원본 유지(Graceful).
            if (project.AutoFixVideo && _post.IsAvailable)
            {
                // 보간은 GPU 엔진(NVENC/CUDA→CPU 이중 폴백)에 위임 → 화질 체인은 보간 제외로 구성.
                var postOpt = new WanPostOptions(
                    Deflicker: project.AutoDeflicker, Sharpen: project.AutoSharpen,
                    Interpolate: false, TargetFps: 30,
                    TemporalSr: project.AutoTemporalSr, Deband: project.AutoDeband);
                var fixDir = Path.Combine(_media.MediaRoot, project.Id);
                for (var i = 0; i < locals.Count; i++)
                {
                    var idx = i;
                    var cur = locals[idx];

                    // ① CPU 화질 체인(디플리커/시간적SR/디밴딩/샤픈) — 적용할 게 있으면.
                    var pp = new CallbackProgress<double>(pct =>
                    {
                        MergePercent = (int)pct;
                        MergeMessage = $"🩹 무료 결함 자동 보정 중… (컷 {idx + 1}/{locals.Count}) {(int)pct}%";
                        Changed();
                    });
                    var outFix = Path.Combine(fixDir, $"fix_{scenes[idx].Id}.mp4");
                    var pr = await _post.StabilizeAsync(cur, outFix, postOpt, pp, CancellationToken.None).ConfigureAwait(false);
                    if (pr.Ok && pr.Applied && pr.OutputPath is not null) cur = pr.OutputPath;

                    // ② GPU 가속 60fps 모션 보간(옵션) — NVENC/CUDA 우선 · CPU minterpolate 폴백 · 실패 시 원본 유지.
                    if (project.AutoInterpolate)
                    {
                        var pm = new CallbackProgress<double>(pct =>
                        {
                            MergePercent = (int)pct;
                            var gpu = _gpuMotion.GpuDetected ? "GPU" : "CPU";
                            MergeMessage = $"🎞️ {gpu} 모션 보간(60fps) 중… (컷 {idx + 1}/{locals.Count}) {(int)pct}%";
                            Changed();
                        });
                        var outMo = Path.Combine(fixDir, $"mo_{scenes[idx].Id}.mp4");
                        // v2.8.0 멀티 GPU 분산 — GPU 2개↑ + 옵션 ON 이면 컷별 라운드로빈으로 디바이스 고정 보간(Dual NVENC 동시 가동).
                        if (project.MultiGpuRender)
                        {
                            await _cluster.DetectGpusAsync(CancellationToken.None).ConfigureAwait(false);
                            if (_cluster.CanDistribute)
                            {
                                var dev = idx % _cluster.DeviceCount;
                                MergeMessage = $"🎞️ 멀티 GPU 분산 보간 중… (컷 {idx + 1}/{locals.Count} → cuda:{dev})"; Changed();
                                if (await _cluster.InterpolateOnGpuAsync(cur, outMo, 60, dev, CancellationToken.None).ConfigureAwait(false))
                                { cur = outMo; locals[idx] = cur; continue; }   // 성공 → 다음 컷(단일 경로 생략)
                            }
                        }
                        var gr = await _gpuMotion.InterpolateAsync(cur, outMo, 60, pm, CancellationToken.None).ConfigureAwait(false);
                        if (gr.Ok && gr.Applied && gr.OutputPath is not null) cur = gr.OutputPath;
                    }

                    locals[idx] = cur;   // 실패/미적용은 원본 경로 유지(Graceful)
                }
                MergePercent = 0; MergeMessage = "컷 병합 중…"; Changed();
            }

            // ── [고급 · Gemini 옵션] AI 컬러 그레이딩 · 키네틱 자막/SFX 정밀 타임코드(실패/키없음 시 기존 설정 100% 폴백) ──
            //   ⚠ 번인 직전(업스케일 이후)에 산출 — 그레이딩/키네틱 자막이 최종 해상도 프레임에 번인된다.
            List<string?>? colorGrades = null;
            if (project.UseAiColorGrading && _grader.Enabled)
            {
                MergeMessage = "🎨 AI 컬러 그레이딩 분석 중…"; Changed();
                // 영상 전체 톤 일관성 — 대표 프롬프트로 1회 산출 후 전 컷 동일 적용(컷마다 톤이 튀지 않게).
                var repPrompt = scenes.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.Prompt))?.Prompt ?? project.Title;
                var repFrame = File.Exists(locals[0] + ".jpg") ? locals[0] + ".jpg" : null;
                var grade = await SuggestGradeSafeAsync(repPrompt, repFrame, CancellationToken.None).ConfigureAwait(false);
                if (grade is not null)
                {
                    colorGrades = new List<string?>(locals.Count);
                    for (var i = 0; i < locals.Count; i++) colorGrades.Add(grade);
                    MergeMessage = "컷 병합 중…"; Changed();
                }
            }

            // 아기 원클릭 프리셋이면 authored 연출 데이터(키네틱/텐션/전환/폴리)를 Gemini 없이 오프라인으로 우선 사용.
            var authored = project.IsBabyPreset;

            List<IReadOnlyList<KineticCaptionWord>?>? kineticWords = null;
            List<IReadOnlyList<KineticSfxCue>?>? kineticSfx = null;
            if (project.UseKineticCaptions && authored && scenes.Any(s => s.PresetKineticWords is { Count: > 0 }))
            {
                // 프리셋 authored 키네틱 골드 자막(밀리초) — Gemini 불필요.
                kineticWords = scenes.Select(s => (IReadOnlyList<KineticCaptionWord>?)(s.PresetKineticWords is { Count: > 0 } ? s.PresetKineticWords : null)).ToList();
            }
            else if (project.UseKineticCaptions && _kinetic.Enabled && anyVoice)
            {
                MergeMessage = "✨ 키네틱 자막/SFX 타임코드 추출 중…"; Changed();
                kineticWords = new List<IReadOnlyList<KineticCaptionWord>?>(scenes.Count);
                kineticSfx = new List<IReadOnlyList<KineticSfxCue>?>(scenes.Count);
                for (var i = 0; i < scenes.Count; i++)
                {
                    var vt = (scenes[i].VoiceText ?? "").Trim();
                    if (vt.Length == 0) { kineticWords.Add(null); kineticSfx.Add(null); continue; }
                    var na = (narrs is not null && i < narrs.Count) ? narrs[i] : null;
                    var vid = i < locals.Count ? locals[i] : null;
                    var ks = await KineticSafeAsync(na, vid, vt, CancellationToken.None).ConfigureAwait(false);
                    kineticWords.Add(ks is { Words.Count: > 0 } ? ks.Words : null);
                    kineticSfx.Add(ks is { SfxTriggers.Count: > 0 } ? ks.SfxTriggers : null);
                }
                MergeMessage = "컷 병합 중…"; Changed();
            }

            // ── [감정선 동적 BGM 오케스트레이션] 컷별 Tension → BGM 볼륨 엔벨로프/덕킹/페이드 플랜 ──
            EmotionPacing? emotionPlan = null;
            if (project.UseEmotionPacing && authored && scenes.Any(s => s.PresetTension is not null))
            {
                // 프리셋 authored 긴장선 — Gemini 없이 BGM 오케스트레이션.
                foreach (var s in scenes) s.EmotionTension = s.PresetTension;
                var cues = scenes.Select(s => new EmotionCue(true, Math.Clamp(s.PresetTension ?? 50, 0, 100), "", "")).ToList();
                emotionPlan = _emotion.BuildPacing(cues);
            }
            else if (project.UseEmotionPacing && _emotion.Enabled)
            {
                MergeMessage = "🎭 감정선 분석 중…"; Changed();
                var cues = new List<EmotionCue>(scenes.Count);
                for (var i = 0; i < scenes.Count; i++)
                {
                    var na = (narrs is not null && i < narrs.Count) ? narrs[i] : null;
                    var vid = i < locals.Count ? locals[i] : null;
                    var vt = (scenes[i].VoiceText ?? "").Trim();
                    cues.Add(await EmotionSafeAsync(na, vid, vt, CancellationToken.None).ConfigureAwait(false));
                    scenes[i].EmotionMood = cues[i].Ok ? cues[i].Mood : null;
                    scenes[i].EmotionTension = cues[i].Ok ? cues[i].Tension : null;
                }
                if (cues.Any(c => c.Ok)) emotionPlan = _emotion.BuildPacing(cues);
                MergeMessage = "컷 병합 중…"; Changed();
            }

            // ── [지능형 스마트 트랜지션] 인접 컷의 엔딩/시작 프레임 시각분석 → 컷 경계 xfade 스펙 ──
            List<VideoTransition?>? transitions = null;
            if (project.UseSmartTransitions && locals.Count >= 2 && authored && scenes.Skip(1).Any(s => s.EnterTransition is not null))
            {
                // 프리셋 authored 전환 — 경계 i(cut i↔i+1) = scenes[i+1].EnterTransition. Gemini 프레임 분석 불필요.
                transitions = new List<VideoTransition?>(locals.Count - 1);
                for (var i = 0; i < locals.Count - 1; i++)
                    transitions.Add((i + 1) < scenes.Count ? scenes[i + 1].EnterTransition : null);
            }
            else if (project.UseSmartTransitions && _transition.Enabled && locals.Count >= 2)
            {
                MergeMessage = "🎬 스마트 트랜지션 설계 중…"; Changed();
                transitions = new List<VideoTransition?>(locals.Count - 1);
                var frameDir = Path.Combine(_media.MediaRoot, project.Id, "_frames");
                for (var i = 0; i < locals.Count - 1; i++)
                {
                    // 이전 컷 엔딩 프레임(~4.8s) + 현재 컷 시작 프레임(~0.1s).
                    var endF = await _merge.ExtractFrameAsync(locals[i], 4.8, Path.Combine(frameDir, $"end_{i}.jpg"), CancellationToken.None).ConfigureAwait(false);
                    var startF = await _merge.ExtractFrameAsync(locals[i + 1], 0.1, Path.Combine(frameDir, $"start_{i + 1}.jpg"), CancellationToken.None).ConfigureAwait(false);
                    transitions.Add(endF is not null && startF is not null
                        ? await TransitionSafeAsync(endF, startF, CancellationToken.None).ConfigureAwait(false)
                        : null);
                }
                MergeMessage = "컷 병합 중…"; Changed();
            }

            // ── [생성형 폴리 & 3D 공간 음향] 컷별 Gemini 오디오 프롬프트 → 엔진/Sfx 해결 + 공간 팬 ──
            List<IReadOnlyList<FoleyCue>?>? foleyCues = null;
            if (project.UseGenFoley && authored && scenes.Any(s => s.PresetFoley is { Count: > 0 }))
            {
                // 프리셋 authored 폴리 스펙 → SfxResolver 로 파일 해결(Gemini 불필요 · Sfx 없으면 Graceful).
                foleyCues = scenes.Select(s => ResolveAuthoredFoley(s.PresetFoley)).ToList();
            }
            else if (project.UseGenFoley && _foley.Enabled)
            {
                MergeMessage = "🔊 생성형 폴리(효과음) 설계 중…"; Changed();
                foleyCues = new List<IReadOnlyList<FoleyCue>?>(scenes.Count);
                for (var i = 0; i < scenes.Count; i++)
                {
                    var vid = i < locals.Count ? locals[i] : null;
                    var pr = (scenes[i].Prompt ?? "").Trim();
                    var cues = await FoleySafeAsync(vid, pr, CancellationToken.None).ConfigureAwait(false);
                    foleyCues.Add(cues is { Count: > 0 } ? cues : null);
                }
                MergeMessage = "컷 병합 중…"; Changed();
            }

            // ── [음소거 시청 대응 시각 SFX 뱃지] 컷별 폴리 트리거 → 이모지 뱃지(상단 중앙 팝업 번인) ──
            List<IReadOnlyList<VisualSfxBadge>?>? visualBadges = null;
            if (project.UseVisualSfxBadges)
            {
                visualBadges = new List<IReadOnlyList<VisualSfxBadge>?>(scenes.Count);
                for (var i = 0; i < scenes.Count; i++)
                {
                    var badges = _visualSfx.BuildBadges(scenes[i], 5000);   // 컷-로컬 소스 시간(배속 이전 · 키네틱과 동일 기준)
                    visualBadges.Add(badges is { Count: > 0 } ? badges : null);
                }
            }

            // ── [파이프라인 순서 4] FFmpeg 병합 — 트랜지션(xfade) + 배속 + 감정 덕킹/컬러/자막/폴리/뱃지/UI 번인(항상 마지막) ──
            var mp = new CallbackProgress<double>(pct => { MergePercent = (int)pct; Changed(); });   // 진행률 → UI
            var r = await _merge.MergeAsync(locals, outPath, w, h, project.BgmPath, project.UseFadeEffect, mp, null, CancellationToken.None,
                                            speed: project.EffectiveSpeed, narrations: narrs, subtitles: subs,
                                            ducking: project.EnableAudioDucking, bgmVolume: project.BgmVolume,
                                            watermarkPath: project.WatermarkImagePath, smartSfx: project.EnableSmartSfx,
                                            colorGrades: colorGrades, kineticWords: kineticWords, kineticSfx: kineticSfx,
                                            emotion: emotionPlan, transitions: transitions, foley: foleyCues,
                                            visualBadges: visualBadges, safePlatform: project.TargetPlatform,
                                            embossWatermark: project.EmbossWatermark).ConfigureAwait(false);
            if (r.Ok && r.OutputPath is not null)
            {
                project.FinalVideoPath = r.OutputPath;
                var masterOut = r.OutputPath;   // 이후 HUD/멀티배치 파이프라인의 마스터(단계별 갱신)
                MergeMessage = "병합 완료 — 최종 영상이 준비되었습니다.";

                // ── [v2.5.0] 텔레메트리 HUD 번인 — 자동차 검사/시험 CSV 지정 시 최종 영상 위에 계기 HUD 합성(Graceful) ──
                if (!string.IsNullOrWhiteSpace(project.TelemetryCsvPath))
                {
                    MergeMessage = "📊 텔레메트리 HUD 번인 중… (게이지·PASS/FAIL)"; Changed();
                    try
                    {
                        var hudOut = Path.Combine(outDir, "final_hud.mp4");
                        var hudOk = await _telemetryHud.BurnAsync(masterOut, hudOut, project.TelemetryCsvPath, project.TelemetryHudStyle, w, h, CancellationToken.None).ConfigureAwait(false);
                        if (hudOk) { masterOut = hudOut; project.FinalVideoPath = hudOut; MergeMessage = "병합 완료 · 텔레메트리 HUD 합성됨"; }
                        else MergeMessage = "병합 완료 · 텔레메트리 HUD 건너뜀(CSV/ffmpeg 확인)";
                        Changed();
                    }
                    catch (Exception ex) { Program.Log("Storyboard.TelemetryHud", ex); MergeMessage = "병합 완료 · 텔레메트리 HUD 실패(Graceful): " + ex.Message; }
                }

                // ── [v2.6.0] CAD 3D 홀로그램 와이어프레임 — STL 지정 시 장비 회전 와이어프레임을 마스터 위에 합성(Graceful) ──
                if (!string.IsNullOrWhiteSpace(project.CadModelPath))
                {
                    MergeMessage = "🧊 CAD 3D 홀로그램 와이어프레임 합성 중…"; Changed();
                    try
                    {
                        var wireOut = Path.Combine(outDir, "final_wire.mp4");
                        var wireOk = await _wireframe.AddWireframeAsync(masterOut, project.CadModelPath, wireOut, project.WireframeNeonColor, ct: CancellationToken.None).ConfigureAwait(false);
                        if (wireOk) { masterOut = wireOut; project.FinalVideoPath = wireOut; MergeMessage = "병합 완료 · CAD 홀로그램 와이어프레임 합성됨"; }
                        else MergeMessage = "병합 완료 · CAD 와이어프레임 건너뜀(STL/ffmpeg 확인)";
                        Changed();
                    }
                    catch (Exception ex) { Program.Log("Storyboard.Wireframe", ex); MergeMessage = "병합 완료 · CAD 와이어프레임 실패(Graceful): " + ex.Message; }
                }

                // ── [v2.6.0] 바이어 엠블럼 음각 각인 — 로고 지정 시 좌측 하단 engrave 합성(Graceful) ──
                if (!string.IsNullOrWhiteSpace(project.BuyerEmblemPath))
                {
                    MergeMessage = "🏷️ 바이어 엠블럼 음각 각인 중…"; Changed();
                    try
                    {
                        var embOut = Path.Combine(outDir, "final_emblem.mp4");
                        var embOk = await _emblem.ApplyAsync(masterOut, project.BuyerEmblemPath, embOut, w, CancellationToken.None).ConfigureAwait(false);
                        if (embOk) { masterOut = embOut; project.FinalVideoPath = embOut; MergeMessage = "병합 완료 · 바이어 엠블럼 각인됨"; }
                        Changed();
                    }
                    catch (Exception ex) { Program.Log("Storyboard.Emblem", ex); MergeMessage = "병합 완료 · 엠블럼 각인 실패(Graceful): " + ex.Message; }
                }

                // ── [v2.8.0] 모션 트래킹 추종 자막 — 라벨 + ROI(W/H>0) 지정 시 피사체를 추종하는 라벨 합성(Graceful) ──
                if (!string.IsNullOrWhiteSpace(project.MotionTrackLabel) && project.MotionRoiW > 0 && project.MotionRoiH > 0)
                {
                    MergeMessage = "🎯 모션 트래킹 추종 자막 합성 중… (블록 매칭)"; Changed();
                    try
                    {
                        var track = await _motionTrack.TrackAsync(masterOut, project.MotionRoiX, project.MotionRoiY, project.MotionRoiW, project.MotionRoiH, CancellationToken.None).ConfigureAwait(false);
                        if (track.HasData)
                        {
                            var trkOut = Path.Combine(outDir, "final_track.mp4");
                            if (await _motionTrack.BurnAsync(masterOut, trkOut, project.MotionTrackLabel, track, w, h, CancellationToken.None).ConfigureAwait(false))
                            { masterOut = trkOut; project.FinalVideoPath = trkOut; MergeMessage = "병합 완료 · 모션 트래킹 라벨 추종"; }
                        }
                        Changed();
                    }
                    catch (Exception ex) { Program.Log("Storyboard.MotionTrack", ex); MergeMessage = "병합 완료 · 모션 트래킹 실패(Graceful): " + ex.Message; }
                }

                // ── [v2.8.0] 스위칭 카운트다운 — 도입부 3·2·1 방송 스위칭 비주얼(Graceful) ──
                if (project.SwitchCountdown)
                {
                    MergeMessage = "⏱ 방송 스위칭 카운트다운 합성 중…"; Changed();
                    try
                    {
                        var cdOut = Path.Combine(outDir, "final_countdown.mp4");
                        if (await _countdown.ApplyAsync(masterOut, cdOut, w, h, 3, CancellationToken.None).ConfigureAwait(false))
                        { masterOut = cdOut; project.FinalVideoPath = cdOut; MergeMessage = "병합 완료 · 스위칭 카운트다운 합성됨"; }
                        Changed();
                    }
                    catch (Exception ex) { Program.Log("Storyboard.Countdown", ex); }
                }

                // ── [v2.8.0] ON AIR 뱃지 — 우측 상단 4K 글래스모피즘 온에어 뱃지(Graceful) ──
                if (project.OnAirBadge)
                {
                    MergeMessage = "🔴 ON AIR 뱃지 합성 중…"; Changed();
                    try
                    {
                        var oaOut = Path.Combine(outDir, "final_onair.mp4");
                        if (await _onAir.ApplyAsync(masterOut, oaOut, w, h, CancellationToken.None).ConfigureAwait(false))
                        { masterOut = oaOut; project.FinalVideoPath = oaOut; MergeMessage = "병합 완료 · ON AIR 뱃지 합성됨"; }
                        Changed();
                    }
                    catch (Exception ex) { Program.Log("Storyboard.OnAir", ex); }
                }

                // ── [v3.0.0] 3D 버추얼 스튜디오 크로마키 합성 — 템플릿 지정 시 크로마 배경 분리 + 가상 무대 합성(Graceful) ──
                if (!string.IsNullOrWhiteSpace(project.VirtualStudioTemplate))
                {
                    MergeMessage = "🎬 3D 버추얼 스튜디오 합성 중… (크로마키+despill)"; Changed();
                    try
                    {
                        var vsOut = Path.Combine(outDir, "final_stage.mp4");
                        var vr = await _virtualStudio.CompositeAsync(masterOut, vsOut, project.VirtualStudioTemplate, project.ChromaColor, w, h, null, CancellationToken.None).ConfigureAwait(false);
                        if (vr.Ok && vr.OutputPath is not null) { masterOut = vr.OutputPath; project.FinalVideoPath = vr.OutputPath; MergeMessage = "병합 완료 · " + vr.Message; }
                        else MergeMessage = "병합 완료 · 버추얼 스튜디오 건너뜀: " + vr.Message;
                        Changed();
                    }
                    catch (Exception ex) { Program.Log("Storyboard.VirtualStudio", ex); MergeMessage = "병합 완료 · 버추얼 스튜디오 실패(Graceful): " + ex.Message; }
                }

                _audit.Log("project", project.TenantId.Length > 0 ? project.TenantId : "local", "merge-done", $"{project.Id} → {Path.GetFileName(masterOut)}");   // v3.0.0 감사 로그

                // ── [v2.1.0] 멀티 플랫폼 일괄 동시 렌더링 — 선택 규격 영상을 platforms/{플랫폼}/ 로 배출(Graceful) ──
                var targets = project.SelectedRenderPlatforms;
                if (project.MultiPlatformExport && targets.Count > 0)
                {
                    MergeMessage = $"📦 멀티 플랫폼 렌더링 중… ({targets.Count}개 규격 동시)"; Changed();
                    var mrp = new CallbackProgress<(SafePlatform Platform, double Pct)>(t => { MergePercent = (int)t.Pct; Changed(); });
                    try
                    {
                        var mr = await _multiRender.RenderAllAsync(masterOut, outDir, w, h, targets, maxParallel: 2, progress: mrp, ct: CancellationToken.None).ConfigureAwait(false);
                        MergeMessage = mr.Ok
                            ? $"병합 완료 · {mr.Message} → platforms 폴더에 저장"
                            : "병합 완료 · 멀티 플랫폼 렌더링 건너뜀: " + mr.Message;
                    }
                    catch (Exception ex) { Program.Log("Storyboard.MultiRender", ex); MergeMessage = "병합 완료 · 멀티 플랫폼 렌더링 실패(Graceful): " + ex.Message; }
                }

                // ── [v2.5.0] 사옥 3색 조명 멀티배치 — 사옥 마스터를 주간/노을/야경으로 병렬 그레이딩해 lighting/{조명}/ 로 배출(Graceful) ──
                if (project.MultiLighting)
                {
                    MergeMessage = "🎨 사옥 3색 조명 멀티배치 중… (주간·노을·야경 동시)"; Changed();
                    var lp = new CallbackProgress<(string Key, double Pct)>(t => { MergePercent = (int)(t.Pct * 100); Changed(); });
                    try
                    {
                        var lr = await _multiLighting.RenderThreeAsync(masterOut, outDir, maxParallel: 2, progress: lp, ct: CancellationToken.None).ConfigureAwait(false);
                        MergeMessage = lr.Ok
                            ? $"병합 완료 · {lr.Message} → lighting 폴더에 저장"
                            : "병합 완료 · 3색 조명 건너뜀: " + lr.Message;
                    }
                    catch (Exception ex) { Program.Log("Storyboard.MultiLighting", ex); MergeMessage = "병합 완료 · 3색 조명 실패(Graceful): " + ex.Message; }
                }

                // ── [v2.6.0] 글로벌 다국어 더빙/자막 — 선택 국가 언어별 홍보본을 global/{code}/ 로 병렬 배출(Graceful) ──
                var dubCodes = project.SelectedDubbingLanguages;
                if (dubCodes.Count > 0)
                {
                    MergeMessage = $"🌐 글로벌 다국어 내보내기 중… ({dubCodes.Count}개국)"; Changed();
                    var dp = new CallbackProgress<(string Code, double Pct)>(t => { MergePercent = (int)(t.Pct * 100); Changed(); });
                    try
                    {
                        var dr = await _dubbing.ExportAllAsync(masterOut, outDir, project, dubCodes, totalMs: null, maxParallel: 2, progress: dp, ct: CancellationToken.None).ConfigureAwait(false);
                        MergeMessage = dr.Ok
                            ? $"병합 완료 · {dr.Message} → global 폴더에 저장"
                            : "병합 완료 · 다국어 내보내기 건너뜀: " + dr.Message;
                    }
                    catch (Exception ex) { Program.Log("Storyboard.Dubbing", ex); MergeMessage = "병합 완료 · 다국어 내보내기 실패(Graceful): " + ex.Message; }
                }

                // ── [v2.6.0] B2B 제안서용 PDF 스펙 시트 — 항상 생성(순수 BCL · spec_sheet.pdf) ──
                try
                {
                    var pdf = _specSheet.Export(project, Path.Combine(outDir, "spec_sheet.pdf"));
                    if (pdf is not null) { Changed(); }
                }
                catch (Exception ex) { Program.Log("Storyboard.SpecSheet", ex); }
            }
            else
            {
                MergeMessage = "병합 실패 (ffmpeg.exe 확인 필요): " + r.Message;
            }
        }
        catch (Exception ex)
        {
            Program.Log("StoryboardJobManager.Merge", ex);
            MergeMessage = "병합 실패 (ffmpeg.exe 확인 필요): " + ex.Message;
        }
        finally
        {
            IsMerging = false;
            Changed();
        }
    }

    /// <summary>업스케일 후 병합 캔버스를 4K(긴 변 3840)로 맞춘다(비율 유지·짝수). 자막이 4K 해상도에 번인되도록.</summary>
    private static (int W, int H) FitTo4K(int w, int h)
    {
        const int Long = 3840;
        if (w <= 0 || h <= 0) return (3840, 2160);
        int nw, nh;
        if (w >= h) { nw = Long; nh = (int)Math.Round(h * (double)Long / w); }
        else { nh = Long; nw = (int)Math.Round(w * (double)Long / h); }
        return (nw & ~1, nh & ~1);
    }

    // ── 순차 처리(기본 · Rate Limit 안전) ──
    private async Task RunSequentialAsync(StoryboardProject p, CancellationToken ct)
    {
        // 스냅샷으로 순회(실행 중 리스트 구조는 UI 가 잠그지만 방어적으로).
        foreach (var scene in p.Scenes.ToList())
        {
            if (ct.IsCancellationRequested) break;
            if (scene.Status == SceneStatus.Completed) continue;   // 완료본 건너뜀
            await RunSceneAsync(scene, ct).ConfigureAwait(false);
        }
    }

    // ── 병렬 처리(옵션 · 동시성 제한) ──
    private async Task RunParallelAsync(StoryboardProject p, int maxParallel, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(Math.Max(1, maxParallel));
        var tasks = p.Scenes.Where(s => s.Status != SceneStatus.Completed).Select(async scene =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { await RunSceneAsync(scene, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ── 컷 1개 생성(제출→폴링→다운로드) ──
    private async Task RunSceneAsync(StoryboardScene scene, CancellationToken ct)
    {
        // 참조 이미지 결정: 컷 자체 이미지 우선 → 프로젝트 앵커(캐릭터/스타일 일관성). null 이면 t2v.
        var refImg = Current is null ? (scene.IsI2v ? scene.ImagePath : null) : _anchor.ResolveReferenceImage(scene, Current);

        // 사전 검증 — 실패해도 배치 전체는 계속.
        if (scene.IsI2v && refImg is null)
        { Fail(scene, "i2v 컷에 참조 이미지가 없습니다(앵커 미등록)."); return; }
        if (refImg is null && string.IsNullOrWhiteSpace(scene.Prompt))
        { Fail(scene, "프롬프트가 비어 있습니다."); return; }

        try
        {
            // 재시도용 QA 필드 초기화.
            scene.QualityScore = null; scene.QAReason = null; scene.IsAutoHealed = false;

            // 앵커(프로젝트 대표 이미지)를 쓰는 컷은 OSS 캐시 URL 재사용(컷마다 재업로드 방지). 컷 자체 이미지는 개별 업로드.
            string? refUrl = null;
            if (Current is not null && refImg is not null && refImg == Current.AnchorImagePath)
                refUrl = await _anchor.GetOrUploadAnchorUrlAsync(Current, ct).ConfigureAwait(false);

            // [1차 생성] — 세로(쇼츠) + 스마트 프레이밍 ON 이면 얼굴 안전대 바운딩 키워드를 프롬프트에 주입(비영속·멱등).
            var (pw, ph) = StoryboardProject.ParseResolution(Current?.ExportResolution);
            var portrait = ph > pw;
            // v16.20.0 미세 표정 퍼핏 — 컷 감정 프리셋 표정 구문 + 정체성 유지 구문 주입(비영속·멱등 · Neutral 은 원문 그대로).
            // v16.22.0 캐릭터 아이덴티티 고정 — 스타일 드리프트 억제 + 정체성 구문 + 의상/소품 앵커(비영속·멱등 · 잠금 없으면 원문).
            var lockedPrompt = Current?.CharacterLock is { } lk ? EpisodicStorySeriesBuilder.ComposeScenePrompt(scene.Prompt, null, lk).Prompt : scene.Prompt;
            var emotive = FacialEmotionPuppeteerEngine.Apply(lockedPrompt, null, scene.Emotion, scene.EmotionIntensity).Prompt;
            var framed = PromptFlowStudio.Services.Wan.SmartFramingBoundingService.ApplyFraming(
                emotive, Current?.TargetPlatform ?? PromptFlowStudio.Services.Media.SafePlatform.None,
                enabled: (Current?.SmartFraming ?? false) && portrait);
            // v1.8.0 무료 결함 자동 보정 — 프롬프트 위생화 + 안정화 네거티브/구도 자동 주입(멱등·비영속 · AutoPilot 기본 ON).
            var sanitized = PromptFlowStudio.Services.Wan.WanPromptSanitizer.Sanitize(framed, portrait, enabled: Current?.AutoFixPrompt ?? true);
            var outcome = await GenerateOnceAsync(scene, sanitized.Prompt, refImg, refUrl, attempt: 0, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) { Cancelled(scene); return; }
            if (!outcome.Ok) { Fail(scene, outcome.Message); return; }
            ApplyOutcome(scene, outcome);
            _license.RecordGeneration(1);   // v2.7.0 상용 쿼터 — 생성 1건 누적(일/월 롤오버)
            RecomputeOverall(); Changed();

            // [네이티브 비디오 시간축 분석 & 모션 Re-roll] — 옵션 ON + Gemini 키 있을 때만(mp4 전체를 분석).
            if (Current?.UseVideoAnalysis == true && _analyzer.Enabled)
                await AnalyzeVideoTemporalAsync(scene, refImg, refUrl, ct).ConfigureAwait(false);

            // [AI 감독 자율 검수 & Re-roll] — 모드 ON + Gemini 키 있을 때만(없으면 그대로 채택 · Graceful).
            if (Current?.AutoDirectorMode == true && _director.Enabled)
                await AutoDirectAsync(scene, refImg, refUrl, ct).ConfigureAwait(false);

            // [v2.6.0 오프라인 AI 퀄리티 가드 & 자동 재발주] — 프레임 손상/안면 뭉개짐을 비트맵 감수 → 미달 시 QuotaRateGuard 연동 재생성.
            if (Current?.AutoQualityGuard == true)
                await QualityGuardAsync(scene, sanitized.Prompt, refImg, refUrl, ct).ConfigureAwait(false);

            // [v16.22.0 캐릭터 정합성 신뢰도] — 잠금 프로필 얼굴 특징 ↔ 최종 채택 컷(포스터/영상) 비교(실패는 미측정 · 채택 불변).
            await ScoreIdentityAsync(scene, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Cancelled(scene); }
        catch (Exception ex) { Fail(scene, ex.Message); }
    }

    /// <summary>v16.22.0 컷 네거티브 합성 — 테마 네거티브 → 정체성/의상 차단 토큰 → 표정 얼굴 보호(멱등 · 비영속).</summary>
    private string? LockedNegative(StoryboardScene scene)
    {
        var neg = scene.NegativePrompt;
        if (Current?.CharacterLock is { } lk) neg = EpisodicStorySeriesBuilder.ComposeScenePrompt("", neg, lk).Negative;
        if (scene.Emotion != FacialEmotion.Neutral) neg = FacialEmotionPuppeteerEngine.MergeNegative(neg);
        return string.IsNullOrWhiteSpace(neg) ? null : neg;
    }

    private async Task ScoreIdentityAsync(StoryboardScene scene, CancellationToken ct)
    {
        try
        {
            if (_series is null || Current?.CharacterLock is not { } lk || _series.FindProfile(lk.ProfileId) is not { HasFace: true } profile) return;
            var frame = !string.IsNullOrEmpty(scene.PosterPath) && File.Exists(scene.PosterPath) ? scene.PosterPath : scene.LocalPath;
            if (string.IsNullOrEmpty(frame)) return;
            var score = await _series.Identity.ScoreAsync(profile, frame, ct).ConfigureAwait(false);
            if (score.Verdict == IdentityVerdict.Unknown) return;
            scene.IdentityConfidence = score.Confidence;
            Changed();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Program.Log("JobManager.ScoreIdentity", ex); }
    }

    /// <summary>컷 1회 생성(제출→폴링→다운로드)해 결과를 반환(scene 상태/진행률은 갱신하되 최종 채택은 호출부가 결정). attempt&gt;0 은 Re-roll(별도 폴더).</summary>
    private async Task<GenOutcome> GenerateOnceAsync(StoryboardScene scene, string prompt, string? refImg, string? refUrl, int attempt, CancellationToken ct)
    {
        scene.Status = SceneStatus.Generating; scene.Percent = 0; scene.Error = null;
        scene.StatusText = attempt > 0 ? $"🎬 AI 감독 재생성 중 ({attempt})" : "생성 준비…";
        RecomputeOverall(); Changed();

        var req = new WanVideoRequest
        {
            Prompt = prompt,
            Size = scene.Size,
            ImagePath = refUrl is null ? refImg : null,   // 캐시 URL 있으면 경로 불필요
            ImageUrl = refUrl,                            // 앵커 OSS 캐시(있으면 재업로드 생략)
            NegativePrompt = LockedNegative(scene),                                                   // v16.6.0 테마 + v16.20.0 표정 + v16.22.0 정체성/의상
            Seed = Current?.CharacterLock is { Seed: > 0 } sk ? sk.Seed : null,                     // v16.22.0 시리즈 시드 고정(컷 간 외형 편차 축소)
        };

        var genProg = new CallbackProgress<WanProgress>(pr =>
        {
            if (scene.Status != SceneStatus.Generating) return;
            scene.Percent = Math.Min(pr.Percent, 95);
            scene.StatusText = attempt > 0 ? $"🎬 재생성 중 ({attempt})… {pr.Phase}" : pr.Phase;
            RecomputeOverall(); Changed();
        });

        var gen = await _engines.Active.GenerateAsync(req, genProg, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return new(false, null, null, null, "취소됨");
        if (!gen.Ok || gen.VideoUrl is null) return new(false, null, null, null, gen.Message);

        scene.Status = SceneStatus.Downloading; scene.StatusText = "다운로드 중…"; Changed();
        var dlProg = new CallbackProgress<double>(d =>
        {
            scene.Percent = (int)Math.Clamp(d, 0, 100);
            scene.StatusText = $"다운로드 중… {scene.Percent}%";
            RecomputeOverall(); Changed();
        });
        // ⚠ Re-roll 마다 다른 로컬 경로가 필요(덮어쓰기 방지) → attempt>0 은 폴더 접미사.
        var jobId = $"{Current!.Id}_{scene.Id}" + (attempt > 0 ? $"_r{attempt}" : "");
        var dl = await _media.DownloadAsync(jobId, gen.VideoUrl, dlProg, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return new(false, null, null, null, "취소됨");

        var local = dl.Ok ? dl.LocalPath : null;
        var poster = local is not null && File.Exists(local + ".jpg") ? local + ".jpg" : null;
        return new(true, local, dl.PlaybackUrl, poster, "완료");
    }

    /// <summary>생성 결과를 컷에 채택 반영(로컬경로·재생URL·포스터·완료).</summary>
    private void ApplyOutcome(StoryboardScene scene, GenOutcome o)
    {
        scene.LocalPath = o.LocalPath;
        scene.VideoUrl = o.PlaybackUrl;
        scene.PosterUrl = o.PosterFile is not null ? _media.ToPlaybackUrl(o.PosterFile) : null;
        scene.PosterPath = o.PosterFile;   // v5.7.0 pHash 정체성 대조용 원본 파일 경로
        scene.Percent = 100; scene.Status = SceneStatus.Completed; scene.StatusText = "완료";
    }

    /// <summary>
    /// [v2.6.0] 오프라인 AI 퀄리티 가드 — 채택 컷을 비트맵 감수(SSIM/선명도)해 프레임 손상·안면 뭉개짐이면 <see cref="QuotaRateGuardService"/> 쿨다운을 존중해 재발주.
    ///   최대 <see cref="StoryboardProject.SsimRetryLimit"/>회 재생성하며, 통과하거나 시도 소진 시 확정(항상 컷은 Completed 로 남김 · Graceful).
    /// </summary>
    private async Task QualityGuardAsync(StoryboardScene scene, string prompt, string? refImg, string? refUrl, CancellationToken ct)
    {
        var project = Current!;
        var limit = Math.Clamp(project.SsimRetryLimit, 0, 5);
        for (var attempt = 1; attempt <= limit + 1; attempt++)
        {
            if (ct.IsCancellationRequested) return;
            if (scene.LocalPath is null || !File.Exists(scene.LocalPath)) return;   // 파일 없으면 감수 불가

            scene.StatusText = "🛡️ AI 퀄리티 감수 중…"; Changed();
            var verdict = await _qualityGuard.AnalyzeAsync(scene.LocalPath, project.SsimQualityThreshold, ct).ConfigureAwait(false);
            scene.QAReason = "🛡️ " + verdict.Reason; Changed();

            if (verdict.Ok) { scene.Status = SceneStatus.Completed; scene.StatusText = "완료"; Changed(); return; }   // 통과
            if (attempt > limit) { scene.Status = SceneStatus.Completed; scene.StatusText = "완료(감수 경고)"; Changed(); return; }   // 재발주 소진 → 원본 유지

            await _qualityGuard.PrepareRetryAsync(ct).ConfigureAwait(false);   // API 쿨다운 존중
            scene.IsAutoHealed = true;
            scene.StatusText = $"🛡️ 퀄리티 미달 → 자동 재발주 ({attempt})"; Changed();
            var outcome = await GenerateOnceAsync(scene, prompt, refImg, refUrl, attempt, ct).ConfigureAwait(false);
            if (!outcome.Ok || outcome.LocalPath is null) { scene.Status = SceneStatus.Completed; scene.StatusText = "완료"; Changed(); return; }   // 재생성 실패 → 이전 유지
            ApplyOutcome(scene, outcome);
            RecomputeOverall(); Changed();
        }
    }

    /// <summary>
    /// AI 감독 자율 검수 루프 — 채택본 프레임을 Gemini 로 평가(QualityScore) → 기준 미만이면 보정 프롬프트로 Re-roll(최대 MaxReroll)하며 더 높은 점수만 채택.
    ///   Gemini 실패/네트워크 오류는 원본 컷 유지(Graceful). 컷은 마지막에 Completed 로 확정.
    /// </summary>
    private async Task AutoDirectAsync(StoryboardScene scene, string? refImg, string? refUrl, CancellationToken ct)
    {
        var project = Current!;
        var threshold = Math.Clamp(project.QualityThreshold, 0, 100);
        var maxReroll = Math.Clamp(project.MaxReroll, 0, 5);
        var hasAnchor = _anchor.HasAnchor(project);

        var poster = scene.LocalPath is not null && File.Exists(scene.LocalPath + ".jpg") ? scene.LocalPath + ".jpg" : null;
        if (poster is null) return;   // 프레임 없으면 검수 불가 → 원본 유지

        scene.StatusText = "🎬 AI 감독 검수 중…"; Changed();
        var review = await ReviewSafeAsync(poster, scene.Prompt?.Trim() ?? "", hasAnchor, ct).ConfigureAwait(false);
        if (review is null) { scene.Status = SceneStatus.Completed; scene.StatusText = "완료"; Changed(); return; }

        scene.QualityScore = review.Score; scene.QAReason = review.Reason; Changed();

        var bestScore = review.Score;
        var healed = review.HealedPrompt;

        for (var attempt = 1; attempt <= maxReroll; attempt++)
        {
            if (ct.IsCancellationRequested) break;
            if (bestScore >= threshold) break;                  // 기준 충족 → 채택
            if (string.IsNullOrWhiteSpace(healed)) break;       // 보정안 없음 → 중단

            var outcome = await GenerateOnceAsync(scene, healed!, refImg, refUrl, attempt, ct).ConfigureAwait(false);
            if (!outcome.Ok || outcome.PosterFile is null) break;   // 재생성 실패 → 이전 채택 유지

            var re = await ReviewSafeAsync(outcome.PosterFile, healed!, hasAnchor, ct).ConfigureAwait(false);
            var newScore = re?.Score ?? 0;
            if (newScore > bestScore)                            // 더 나은 결과만 채택
            {
                bestScore = newScore;
                ApplyOutcome(scene, outcome);
                scene.Prompt = healed!;                          // 보정 프롬프트를 채택본 프롬프트로 반영
                scene.QualityScore = newScore; scene.QAReason = re?.Reason; scene.IsAutoHealed = true;
            }
            healed = re?.HealedPrompt ?? healed;                 // 다음 시도용 보정안 갱신
            Changed();
        }

        scene.Status = SceneStatus.Completed; scene.StatusText = "완료"; RecomputeOverall(); Changed();
    }

    /// <summary>Gemini 검수 예외 방어 래퍼 — 실패 시 null(원본 유지).</summary>
    private async Task<DirectorReview?> ReviewSafeAsync(string poster, string prompt, bool hasAnchor, CancellationToken ct)
    {
        try
        {
            var r = await _director.ReviewFrameAsync(poster, prompt, hasAnchor, ct).ConfigureAwait(false);
            return r.Ok ? r : null;
        }
        catch (Exception ex) { Program.Log("Storyboard.AutoDirect.Review", ex); return null; }
    }

    /// <summary>
    /// 네이티브 MP4 통비디오 시간축 분석 & 모션 Re-roll — 프레임이 아닌 <b>영상 전체</b>를 Gemini 로 분석해 <see cref="StoryboardScene.VideoTemporalScore"/>를
    ///   기록하고, 기준 미만이면 모션 보정 프롬프트로 재생성(더 높은 시간축 점수만 채택). 실패/키없음/상한초과는 원본 유지(Graceful).
    /// </summary>
    private async Task AnalyzeVideoTemporalAsync(StoryboardScene scene, string? refImg, string? refUrl, CancellationToken ct)
    {
        var project = Current!;
        var threshold = Math.Clamp(project.QualityThreshold, 0, 100);
        var maxReroll = Math.Clamp(project.MaxReroll, 0, 5);
        var hasAnchor = _anchor.HasAnchor(project);

        if (string.IsNullOrEmpty(scene.LocalPath) || !File.Exists(scene.LocalPath)) return;   // 로컬 mp4 없으면 분석 불가

        scene.StatusText = "🎞️ 비디오 모션 분석 중…"; Changed();
        var analysis = await AnalyzeSafeAsync(scene.LocalPath, scene.Prompt?.Trim() ?? "", hasAnchor, ct).ConfigureAwait(false);
        if (analysis is null) { scene.Status = SceneStatus.Completed; scene.StatusText = "완료"; Changed(); return; }

        scene.VideoTemporalScore = analysis.TemporalScore; scene.TemporalReason = analysis.Reason; Changed();

        var bestScore = analysis.TemporalScore;
        var motion = analysis.MotionPrompt;

        for (var attempt = 1; attempt <= maxReroll; attempt++)
        {
            if (ct.IsCancellationRequested) break;
            if (bestScore >= threshold) break;                  // 시간축 기준 충족 → 채택
            if (string.IsNullOrWhiteSpace(motion)) break;       // 모션 보정안 없음 → 중단

            var outcome = await GenerateOnceAsync(scene, motion!, refImg, refUrl, attempt, ct).ConfigureAwait(false);
            if (!outcome.Ok || outcome.LocalPath is null) break;

            var re = await AnalyzeSafeAsync(outcome.LocalPath, motion!, hasAnchor, ct).ConfigureAwait(false);
            var newScore = re?.TemporalScore ?? 0;
            if (newScore > bestScore)                            // 더 자연스러운 모션만 채택
            {
                bestScore = newScore;
                ApplyOutcome(scene, outcome);
                scene.Prompt = motion!;
                scene.VideoTemporalScore = newScore; scene.TemporalReason = re?.Reason; scene.IsAutoHealed = true;
            }
            motion = re?.MotionPrompt ?? motion;
            Changed();
        }

        scene.Status = SceneStatus.Completed; scene.StatusText = "완료"; RecomputeOverall(); Changed();
    }

    /// <summary>Gemini 비디오 분석 예외 방어 래퍼 — 실패 시 null(원본 유지).</summary>
    private async Task<VideoAnalysis?> AnalyzeSafeAsync(string videoPath, string prompt, bool hasAnchor, CancellationToken ct)
    {
        try
        {
            var r = await _analyzer.AnalyzeAsync(videoPath, prompt, hasAnchor, ct).ConfigureAwait(false);
            return r.Ok ? r : null;
        }
        catch (Exception ex) { Program.Log("Storyboard.VideoAnalyze", ex); return null; }
    }

    /// <summary>Gemini 컬러 그레이딩 예외 방어 래퍼 — 검증 통과한 안전 필터만, 실패 시 null(원본 톤 유지).</summary>
    private async Task<string?> SuggestGradeSafeAsync(string prompt, string? framePath, CancellationToken ct)
    {
        try
        {
            var g = await _grader.SuggestAsync(prompt, framePath, ct).ConfigureAwait(false);
            return g.Ok && !string.IsNullOrWhiteSpace(g.FilterGraph) ? g.FilterGraph : null;
        }
        catch (Exception ex) { Program.Log("Storyboard.ColorGrade", ex); return null; }
    }

    /// <summary>Gemini 키네틱 자막/SFX 추출 예외 방어 래퍼 — 실패 시 null(정적 자막 폴백).</summary>
    private async Task<KineticSyncResult?> KineticSafeAsync(string? narrationAudio, string? videoPath, string voiceText, CancellationToken ct)
    {
        try
        {
            var k = await _kinetic.AnalyzeAsync(narrationAudio, videoPath, voiceText, 5000, ct).ConfigureAwait(false);
            return k.Ok ? k : null;
        }
        catch (Exception ex) { Program.Log("Storyboard.Kinetic", ex); return null; }
    }

    /// <summary>Gemini 감정 분석 예외 방어 래퍼 — 실패해도 EmotionCue.Fail(중립 처리)로 반환(믹싱 유지).</summary>
    private async Task<EmotionCue> EmotionSafeAsync(string? narrationAudio, string? videoPath, string voiceText, CancellationToken ct)
    {
        try { return await _emotion.AnalyzeAsync(narrationAudio, videoPath, voiceText, ct).ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("Storyboard.Emotion", ex); return EmotionCue.Fail(ex.Message); }
    }

    /// <summary>Gemini 트랜지션 산출 예외 방어 래퍼 — 실패 시 null(해당 경계는 전환 없이 concat).</summary>
    private async Task<VideoTransition?> TransitionSafeAsync(string prevEndFrame, string currStartFrame, CancellationToken ct)
    {
        try
        {
            var t = await _transition.SuggestAsync(prevEndFrame, currStartFrame, ct).ConfigureAwait(false);
            return t.Ok ? t.Transition : null;
        }
        catch (Exception ex) { Program.Log("Storyboard.Transition", ex); return null; }
    }

    /// <summary>생성형 폴리 예외 방어 래퍼 — 실패 시 빈 목록(폴리 없이 병합).</summary>
    private async Task<IReadOnlyList<FoleyCue>> FoleySafeAsync(string? videoPath, string cutPrompt, CancellationToken ct)
    {
        try { return await _foley.AnalyzeCutAsync(videoPath, cutPrompt, 5000, ct).ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("Storyboard.Foley", ex); return System.Array.Empty<FoleyCue>(); }
    }

    /// <summary>프리셋 authored 폴리 스펙(타입/시점/팬)을 SfxResolver 로 파일 해결해 FoleyCue 로. Sfx 파일 없으면 해당 큐 생략(Graceful).</summary>
    private static IReadOnlyList<FoleyCue>? ResolveAuthoredFoley(List<FoleySpec>? specs)
    {
        if (specs is null || specs.Count == 0) return null;
        var cues = new List<FoleyCue>();
        foreach (var sp in specs)
        {
            if (sp is null) continue;
            var path = SfxResolver.FindByType(sp.SfxType);
            if (path is null) continue;
            cues.Add(new FoleyCue(path, Math.Max(0, sp.TimestampMs), Math.Clamp(sp.Pan, -1.0, 1.0)));
        }
        return cues.Count > 0 ? cues : null;
    }

    /// <summary>컷 1회 생성 결과.</summary>
    private sealed record GenOutcome(bool Ok, string? LocalPath, string? PlaybackUrl, string? PosterFile, string Message);

    private void Fail(StoryboardScene s, string message)
    {
        s.Status = SceneStatus.Failed; s.Error = message; s.StatusText = "실패"; s.Percent = 0;
        RecomputeOverall(); Changed();
    }

    private void Cancelled(StoryboardScene s)
    {
        s.Status = SceneStatus.Canceled; s.StatusText = "취소됨";
        RecomputeOverall(); Changed();
    }

    private void RecomputeOverall()
    {
        var list = Current?.Scenes;
        OverallPercent = (list is null || list.Count == 0)
            ? 0
            : (int)list.Average(s => s.IsTerminal ? 100 : s.Percent);
    }

    private void Changed() => OnChange?.Invoke();

    /// <summary>람다 기반 동기 IProgress 어댑터(폴링 스레드에서 즉시 상태 갱신 · sync-context 미개입).</summary>
    private sealed class CallbackProgress<T>(Action<T> cb) : IProgress<T>
    {
        public void Report(T value) => cb(value);
    }
}
