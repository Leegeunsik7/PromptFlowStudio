using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Commercial;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// [PromptFlowStudio] <see cref="VideoEngineSelector"/> 활성 엔진(ComfyUI 로컬 · Wan/멀티 엔진 루터 클라우드)으로 렌더 작업 실행.
///   ▸ 선택기 미등록(모바일·테스트) 또는 활성 엔진 비활성(키/실행파일 미구성)이면 <see cref="IsAvailable"/>=false → 큐 시뮬레이션 폴백.
///   ▸ 로컬 엔진 산출물은 <see cref="BatchRenderJob.OutputPath"/> 지정 시 그 경로로 복사(갤러리 메타 동반 기록).
///   ▸ 커머셜 후처리: 메타에 로고/디스클레이머가 있으면 추론 직후 ffmpeg 번인 → 합성본을 최종 산출물로 확정(<see cref="CommercialBrandService.MetaOverlayApplied"/>).
///   ▸ 취소 전파: 큐 CTS 취소 → 엔진 ct(로컬 CLI 는 <see cref="PromptFlowStudio.Services.Engines.ProcessRunner"/> 프로세스 트리 종료)
///     + ComfyUI 서버 <c>POST /interrupt</c>(GPU 즉시 해제 · 2초 타임아웃 · 실패 격리).
/// </summary>
public sealed class VideoEngineRenderExecutionEngine : IRenderExecutionEngine, IMockRenderOutputProvider
{
    private static readonly TimeSpan InterruptTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient s_http = new() { Timeout = InterruptTimeout };

    private readonly VideoEngineSelector? _selector;
    private readonly FFmpegVideoStitchService? _post;
    private readonly Func<string, CancellationToken, Task> _interrupt;

    public VideoEngineRenderExecutionEngine(VideoEngineSelector? selector) : this(selector, new FFmpegVideoStitchService(), null) { }

    /// <param name="post">커머셜 번인 후처리기(null = 생략).</param>
    /// <param name="interrupt">취소 시 엔진 중단 신호(엔진 종류 · 타임아웃 토큰). null 이면 ComfyUI <c>/interrupt</c>.</param>
    public VideoEngineRenderExecutionEngine(VideoEngineSelector? selector, FFmpegVideoStitchService? post, Func<string, CancellationToken, Task>? interrupt)
    {
        _selector = selector;
        _post = post;
        _interrupt = interrupt ?? DefaultInterruptAsync;
    }

    /// <summary>true 반환 시 실물 엔진을 비활성으로 보고해 큐가 시뮬레이션(Mock)으로 처리 — [환경설정] 기본 렌더 모드 Mock.</summary>
    public Func<bool>? ForceSimulation { get; init; }

    /// <summary>ComfyUI 베이스 URL 공급자(호출 시점 평가 → [환경설정] 저장 즉시 반영). null 이면 <c>PFS_COMFY_URL</c> → 127.0.0.1:8188.</summary>
    public Func<string>? ComfyBaseUrl { get; init; }

    /// <summary>
    /// [환경설정] 렌더 모드 공급자(ComfyUI/Cloud/Mock · 디스패치 시점마다 평가). ComfyUI → 로컬 엔진, Cloud → 비-ComfyUI 활성 엔진(DashScope/Wan).
    /// null 이면 선택기 활성 엔진 그대로.
    /// </summary>
    public Func<string>? RenderMode { get; init; }

    /// <summary>원격 산출물 다운로더(URL · 로컬 경로 · ct). null 이면 내장 HTTP(타임아웃·지수 백오프 3회).</summary>
    public Func<string, string, CancellationToken, Task>? Downloader { get; init; }

    /// <summary>Mock 샘플 MP4 생성용 ffmpeg 경로 공급자 — null 이면 Mock 샘플 생성 비활성(기존 빈 완료).</summary>
    public Func<string?>? MockFfmpeg { get; init; }

    /// <summary>Mock 샘플 ffmpeg 실행기(테스트 주입). null 이면 <see cref="ProcessRunner"/>.</summary>
    public FfmpegRunner? MockRunner { get; init; }

    /// <summary>Cloud(DashScope) 오류 접두 — 작업실 인라인 오류 식별용.</summary>
    public const string CloudErrorPrefix = "☁️ DashScope 오류: ";

    /// <summary>
    /// 실행 가능 — 활성 엔진 구성됨, 또는 [환경설정] 렌더 모드가 명시적 Cloud(엔진 미구성이어도 true → <see cref="ExecuteAsync"/> 가 명시 오류로 Failed 처리,
    /// 큐가 Mock 컬러바로 조용히 폴백하지 않게 함).
    /// </summary>
    public bool IsAvailable => !(ForceSimulation?.Invoke() ?? false) && (TryGetActive() is { Enabled: true } || IsExplicitCloud());

    public string EngineKind => IsExplicitCloud() && TryGetActive() is not { Enabled: true } ? RenderEngineKind.Cloud : KindOf(TryGetActive());

    /// <summary>Cloud 엔진 미구성 시 오류 본문(작업실 배너 노출).</summary>
    public const string CloudNotConfiguredMessage = "[인증 실패] Cloud(DashScope Wan 2.1) 엔진이 비활성입니다 — API 키 미구성/소진. [환경설정]에서 DashScope 키를 확인하세요 (Mock 대체 생성 안 함)";

    /// <summary>[환경설정] 렌더 모드가 Cloud 로 명시됐는지(공급자 미설정/예외 = false).</summary>
    internal bool IsExplicitCloud()
    {
        string? mode;
        try { mode = RenderMode?.Invoke(); } catch (Exception ex) { Program.Log("RenderExec.Mode", ex); return false; }
        return !string.IsNullOrWhiteSpace(mode)
               && string.Equals(PromptFlowStudio.Services.Configuration.AppSettingsManager.NormalizeMode(mode), RenderEngineKind.Cloud, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var engine = TryGetActive();
        var kind = KindOf(engine);
        if (engine is not { Enabled: true })
            return IsExplicitCloud()
                ? new RenderExecutionResult(false, RenderEngineKind.Cloud, null, CloudErrorPrefix + CloudNotConfiguredMessage)
                : new RenderExecutionResult(false, kind, null, "렌더 엔진 미설정");

        var request = BuildRequest(job);
        // ☁️ 클라우드 발번 즉시 저널링(task_id 첫 보고 1회) — 단절·재시작 복구용
        var journal = string.Equals(kind, RenderEngineKind.Cloud, StringComparison.Ordinal) ? Journal : null;
        JournalingRelay? jrelay = journal is null ? null : new JournalingRelay(progress, journal, job, kind);
        IProgress<WanProgress>? relay = jrelay is not null ? jrelay : progress is null ? null : new WanProgressRelay(progress);
        WanGenerationResult r;
        try
        {
            using (ct.Register(static s => { var (self, k) = ((VideoEngineRenderExecutionEngine, string))s!; self.FireInterrupt(k); }, (this, kind)))
                r = await engine.GenerateAsync(request, relay, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // 사용자 [취소] → UserCanceled(자동 회수 제외) · 종료/단절 → Interrupted(재시작 시 복구)
            journal?.MarkStatus(jrelay!.TaskId, job.UserCancelRequested ? TaskJournalService.StatusUserCanceled : TaskJournalService.StatusInterrupted);
            throw;
        }
        var journalTaskId = jrelay?.TaskId ?? r.TaskId;
        if (journal is not null && !string.IsNullOrWhiteSpace(r.TaskId) && jrelay!.TaskId is null)
            journal.Record(TaskJournalService.EntryFor(job, r.TaskId!, kind, DateTimeOffset.UtcNow));
        if (!r.Ok || string.IsNullOrWhiteSpace(r.VideoUrl))
        {
            if (r.Status is WanTaskStatus.Failed or WanTaskStatus.Canceled) journal?.Remove(journalTaskId);   // 원격 종료 실패 — 복구 대상 아님
            return new RenderExecutionResult(false, kind, null, DescribeFailure(kind, r.Message, r.TaskId));
        }

        var output = r.VideoUrl;
        if (IsRemote(output) && !string.IsNullOrWhiteSpace(job.OutputPath) && Path.IsPathRooted(job.OutputPath))
        {
            // 클라우드 결과 URL → 작업 OutputPath 로 실물 다운로드(앰비언트·합본·플레이어가 로컬 파일 필요)
            try
            {
                var dir = Path.GetDirectoryName(job.OutputPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                await (Downloader ?? DefaultDownloadAsync)(output, job.OutputPath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Program.Log("RenderExec.Download", ex);
                return new RenderExecutionResult(false, kind, null, CloudErrorPrefix + "결과 영상 다운로드 실패 — " + ex.Message);
            }
            ct.ThrowIfCancellationRequested();
            if (!RenderQueueService.HasLocalOutput(job.OutputPath))
                return new RenderExecutionResult(false, kind, null, CloudErrorPrefix + "결과 영상 다운로드 실패 — 파일이 비어 있습니다");
            output = job.OutputPath;
        }
        if (!string.IsNullOrWhiteSpace(job.OutputPath) && Path.IsPathRooted(output) && File.Exists(output)
            && !string.Equals(Path.GetFullPath(output), Path.GetFullPath(job.OutputPath), StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(job.OutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(output, job.OutputPath, overwrite: true);
            output = job.OutputPath;
        }
        output = await ApplyCommercialOverlayAsync(job, output, ct).ConfigureAwait(false);
        output = await ApplyAmbientLoopAsync(job, output, ct, cloud: string.Equals(kind, RenderEngineKind.Cloud, StringComparison.Ordinal)).ConfigureAwait(false);
        journal?.Remove(journalTaskId);   // 로컬 저장 완료 → 저널 안전 제거
        return new RenderExecutionResult(true, kind, output, r.Message);
    }

    /// <summary>☁️ 클라우드 태스크 복구 저널(null = 미사용).</summary>
    public TaskJournalService? Journal { get; init; }

    /// <summary>
    /// 커머셜 번인 후처리 — 대상 메타 · 로컬 산출물일 때만. 성공 시 합성본(<c>{이름}_branded.mp4</c>)을 원본 자리로 교체해 경로 유지,
    /// ffmpeg 부재/실패는 원본 유지(Graceful). 결과는 작업 메타 <see cref="CommercialBrandService.MetaOverlayApplied"/> 로 갱신.
    /// </summary>
    internal async Task<string> ApplyCommercialOverlayAsync(BatchRenderJob job, string output, CancellationToken ct)
    {
        if (_post is null || !CommercialBrandService.TryGetOverlayRequest(job.Metadata, out var logo, out var disclaimer, out var placement)) return output;
        if (!Path.IsPathRooted(output) || !File.Exists(output)) return output;

        var branded = Path.Combine(Path.GetDirectoryName(output) ?? "", Path.GetFileNameWithoutExtension(output) + "_branded.mp4");
        var res = await _post.BurnInOverlayAsync(output, logo, disclaimer, placement, branded, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var state = res.Ok ? "burned" : res.IsVirtual ? "virtual" : "failed";
        if (res.Ok && res.OutputPath is not null)
        {
            try { File.Move(res.OutputPath, output, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("RenderExec.Overlay", ex); output = res.OutputPath; }
        }
        else if (!res.IsVirtual) Program.Log("RenderExec.Overlay", new InvalidOperationException(res.Message));

        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        if (job.Metadata is not null) foreach (var kv in job.Metadata) meta[kv.Key] = kv.Value;
        meta[CommercialBrandService.MetaOverlayApplied] = state;
        job.Metadata = meta;
        return output;
    }

    /// <summary>앰비언트 슬로우 루프 후처리기(null = 생략) — 기본 ctor 는 ffmpeg 실물.</summary>
    public AmbientLoopService? Ambient { get; init; } = new();

    /// <summary>앰비언트 메타 — 핑퐁 루프 사용("true").</summary>
    public const string MetaAmbientPingPong = "ambient.pingpong";
    /// <summary>앰비언트 메타 — 후처리 결과(applied · virtual · failed).</summary>
    public const string MetaAmbientApplied = "ambient.applied";
    /// <summary>앰비언트 메타 — 루프 모드(<see cref="LoopMode"/> 이름 · 미지정 = 정방향 크로스페이드). 핑퐁 메타가 우선.</summary>
    public const string MetaAmbientLoopMode = "ambient.loopmode";
    /// <summary>앰비언트 메타 — 대체 보존 사유(<see cref="AmbientLoopService.FallbackNotice"/> 포함). 성공 시 미기록.</summary>
    public const string MetaAmbientNotice = "ambient.notice";

    /// <summary>
    /// <see cref="VideoDurationMode.Sec15_AmbientSlow"/> 후처리 — 로컬 5초 산출물을 <c>{project}_ambient15s.mp4</c> 무음 15초 루프로 변환해 최종 산출물로 확정.
    /// ffmpeg 부재/실패는 원본 5초 유지(Graceful). 결과는 메타 <see cref="MetaAmbientApplied"/>.
    /// <paramref name="cloud"/>=true(Wan 2.1 5초 클립): 원속도 3세그먼트 — 핑퐁 체크 시 정→역→정(<see cref="LoopMode.TriplePingPong"/>), 해제 시 0.75s 크로스디졸브(<see cref="LoopMode.TripleCrossfade"/>).
    /// </summary>
    internal async Task<string> ApplyAmbientLoopAsync(BatchRenderJob job, string output, CancellationToken ct, bool cloud = false)
    {
        var ambient = Ambient;
        if (ambient is null || !job.IsAmbientLoop || !Path.IsPathRooted(output) || !File.Exists(output)) return output;
        var project = MultiDurationPlanner.MetaOf(job, MultiDurationPlanner.MetaProject);
        var target = MultiDurationPlanner.BuildOutputPath(Path.GetDirectoryName(output) ?? "", project, VideoDurationMode.Sec15_AmbientSlow);
        var pingPong = string.Equals(MultiDurationPlanner.MetaOf(job, MetaAmbientPingPong), "true", StringComparison.OrdinalIgnoreCase);
        var mode = cloud ? (pingPong ? LoopMode.TriplePingPong : LoopMode.TripleCrossfade)
                 : pingPong ? LoopMode.PingPong : AmbientLoopService.ParseLoopMode(MultiDurationPlanner.MetaOf(job, MetaAmbientLoopMode));
        var res = await ambient.RenderAsync(output, target, mode, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var state = res.Ok ? "applied" : res.IsVirtual ? "virtual" : "failed";
        if (!res.Ok && !res.IsVirtual) Program.Log("RenderExec.Ambient", new InvalidOperationException(res.Message));
        job.Metadata = MultiDurationPlanner.WithMeta(job.Metadata, MetaAmbientApplied, state);
        if (!res.Ok) job.Metadata = MultiDurationPlanner.WithMeta(job.Metadata, MetaAmbientNotice, res.Message);
        // 성공본 또는 대체 보존 원본 — 실존 확인 후 채택, 아니면 원본 5초 유지(유실 금지)
        return res.OutputPath is { } p && File.Exists(p) ? p : output;
    }

    /// <summary>취소 콜백 — 중단 신호 fire-and-forget(예외 격리 · 콜백 스레드 비차단).</summary>
    private void FireInterrupt(string kind)
    {
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(InterruptTimeout);
            try { await _interrupt(kind, cts.Token).ConfigureAwait(false); }
            catch (Exception ex) { Program.Log("RenderExec.Interrupt", ex); }
        });
    }

    /// <summary>ComfyUI 서버 실행 중 프롬프트 중단(<see cref="ComfyBaseUrl"/> → <c>PFS_COMFY_URL</c> · 기본 127.0.0.1:8188). 서버 미기동은 무시.</summary>
    private async Task DefaultInterruptAsync(string kind, CancellationToken ct)
    {
        if (!string.Equals(kind, RenderEngineKind.ComfyUi, StringComparison.Ordinal)) return;
        var baseUrl = ComfyBaseUrl?.Invoke() is { Length: > 0 } cfg ? cfg.TrimEnd('/')
            : Environment.GetEnvironmentVariable("PFS_COMFY_URL") is { Length: > 0 } u ? u.TrimEnd('/') : "http://127.0.0.1:8188";
        try
        {
            using var content = new StringContent("");
            using var _ = await s_http.PostAsync(baseUrl + "/interrupt", content, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException) { }
    }

    /// <summary>
    /// 렌더 작업 → 엔진 요청. <see cref="BatchRenderJob.ReferenceImagePath"/> 가 실존 로컬 파일이면 <see cref="WanVideoRequest.ImagePath"/> 로 주입해
    /// I2V(첫 프레임 참조)로 분기 — 캡처 롤 · 스토리보드 1번 컷 앵커 · 커머셜 제품 사진. 파일 누락 시 T2V 로 폴백.
    /// </summary>
    internal static WanVideoRequest BuildRequest(BatchRenderJob job) => new()
    {
        Prompt = job.Prompt,
        Size = ToEngineSize(job.Resolution),
        Seed = job.Seed,
        ImagePath = ResolveReferenceImage(job.ReferenceImagePath),
        NegativePrompt = MultiDurationPlanner.MetaOf(job, MetaNegativePrompt) is { Length: > 0 } neg ? neg : null,
        // 🌫️ 앰비언트 15초: Wan 2.1 은 5초만 허용 → 항상 5초 요청 후 FFmpeg 로 15초 루프 확장(15 직접 요청 금지)
        Duration = job.IsAmbientLoop ? AmbientCloudClipSeconds : null,
    };

    /// <summary>앰비언트 Cloud 요청 클립 길이(초) — Wan 2.1 허용 최대치.</summary>
    public const int AmbientCloudClipSeconds = 5;

    /// <summary>작업 메타 — 네거티브 프롬프트(작업실 → 엔진 요청 전달).</summary>
    public const string MetaNegativePrompt = "prompt.negative";

    /// <summary>실존 로컬 이미지면 전체 경로, 아니면 null(T2V).</summary>
    internal static string? ResolveReferenceImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        try { return Path.IsPathRooted(p) && File.Exists(p) ? Path.GetFullPath(p) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>CUDA OOM 판정 — <c>CUDA out of memory</c>/<c>out of memory</c>(대소문자 무시) · <c>OutOfMemoryError</c> · 대문자 <c>OOM</c>.</summary>
    public static bool IsCudaOom(string? message)
        => !string.IsNullOrEmpty(message)
           && (message.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
               || message.Contains("OutOfMemoryError", StringComparison.OrdinalIgnoreCase)
               || message.Contains("OOM", StringComparison.Ordinal));

    /// <summary>
    /// OOM 재시도용 해상도 1단계 하향 — 짧은 변 &gt;720 → 720, &gt;540 → 540(종횡비·방향 유지, 짝수 정렬).
    /// 예) 1920x1080 → 1280x720, 1280x720 → 960x540, 1080x1920 → 720x1280. 파싱 불가/더 내릴 수 없으면 null.
    /// </summary>
    public static string? DownscaleResolution(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return null;
        var s = resolution.Trim();
        var i = s.IndexOfAny(SizeSeparators);
        if (i <= 0 || !int.TryParse(s.AsSpan(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                   || !int.TryParse(s.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                   || w <= 0 || h <= 0) return null;
        var shortSide = Math.Min(w, h);
        var target = shortSide > 720 ? 720 : shortSide > 540 ? 540 : 0;
        if (target == 0) return null;
        var longSide = (int)Math.Round(Math.Max(w, h) * (double)target / shortSide, MidpointRounding.AwayFromZero) & ~1;
        return w >= h
            ? longSide.ToString(CultureInfo.InvariantCulture) + "x" + target.ToString(CultureInfo.InvariantCulture)
            : target.ToString(CultureInfo.InvariantCulture) + "x" + longSide.ToString(CultureInfo.InvariantCulture);
    }

    private static readonly char[] SizeSeparators = { 'x', 'X', '*' };

    /// <summary>"1280x720" → 엔진 규격 "1280*720".</summary>
    internal static string? ToEngineSize(string? resolution)
        => string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim().Replace('x', '*').Replace('X', '*');

    /// <summary>
    /// 활성 엔진 — [환경설정] 렌더 모드를 디스패치 시점에 실시간 반영. ComfyUI 모드 → ComfyUI 엔진, Cloud 모드 → 선택기 활성이 ComfyUI 면 첫 비-ComfyUI 엔진.
    /// 해당 엔진이 없으면 선택기 활성 폴백.
    /// </summary>
    private IVideoGenerationEngine? TryGetActive()
    {
        if (_selector is null || _selector.All.Count == 0) return null;
        IVideoGenerationEngine? active;
        try { active = _selector.Active; } catch (Exception) { return null; }
        string? mode;
        try { mode = RenderMode?.Invoke(); } catch (Exception ex) { Program.Log("RenderExec.Mode", ex); mode = null; }
        if (string.IsNullOrWhiteSpace(mode)) return active;
        mode = PromptFlowStudio.Services.Configuration.AppSettingsManager.NormalizeMode(mode);   // 대소문자·별칭(dashscope/wan/local) 흡수
        var all = _selector.All;
        if (string.Equals(mode, RenderEngineKind.ComfyUi, StringComparison.OrdinalIgnoreCase))
        {
            if (active is ComfyUiVideoEngine) return active;
            for (var i = 0; i < all.Count; i++) if (all[i] is ComfyUiVideoEngine) return all[i];
        }
        else if (string.Equals(mode, RenderEngineKind.Cloud, StringComparison.OrdinalIgnoreCase))
        {
            if (active is not ComfyUiVideoEngine) return active;
            for (var i = 0; i < all.Count; i++) if (all[i] is not ComfyUiVideoEngine) return all[i];
        }
        return active;
    }

    /// <summary>실패 메시지 — Cloud 는 DashScope 응답 원문(인증·할당량 등) 그대로 접두 표기, 그 외 원문.</summary>
    internal static string DescribeFailure(string kind, string? message, string? taskId)
    {
        var msg = string.IsNullOrWhiteSpace(message) ? "엔진 생성 실패" : message.Trim();
        if (!string.Equals(kind, RenderEngineKind.Cloud, StringComparison.Ordinal)) return msg;
        var hint = ClassifyCloudError(msg);
        return CloudErrorPrefix + (hint is null ? "" : "[" + hint + "] ") + msg
               + (string.IsNullOrWhiteSpace(taskId) ? "" : " (task_id=" + taskId + ")");
    }

    /// <summary>DashScope 오류 분류 — 인증 · 할당량 · 속도 제한 · 시간 초과. 미분류 null.</summary>
    internal static string? ClassifyCloudError(string? message)
    {
        if (string.IsNullOrEmpty(message)) return null;
        if (message.Contains("InvalidApiKey", StringComparison.OrdinalIgnoreCase) || message.Contains("401", StringComparison.Ordinal)
            || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) || message.Contains("API 키", StringComparison.Ordinal)
            || message.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase)) return "인증 실패";
        if (message.Contains("429", StringComparison.Ordinal) || message.Contains("Throttl", StringComparison.OrdinalIgnoreCase)
            || message.Contains("속도 제한", StringComparison.Ordinal)) return "속도 제한";
        if (message.Contains("Arrearage", StringComparison.OrdinalIgnoreCase) || message.Contains("quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("할당량", StringComparison.Ordinal) || message.Contains("FreeTier", StringComparison.OrdinalIgnoreCase)) return "할당량 부족";
        if (message.Contains("시간 초과", StringComparison.Ordinal) || message.Contains("timeout", StringComparison.OrdinalIgnoreCase)) return "시간 초과";
        return null;
    }

    private static bool IsRemote(string? path)
        => path is not null && (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static readonly HttpClient s_download = new() { Timeout = TimeSpan.FromMinutes(5) };

    /// <summary>원격 영상 다운로드 — 임시 파일 → 원자 이동, 실패 시 지수 백오프(1s·2s) 최대 3회. HTTP 오류는 상태 코드 포함 예외.</summary>
    internal static async Task DefaultDownloadAsync(string url, string dest, CancellationToken ct)
    {
        var tmp = dest + ".part";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using (var res = await s_download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!res.IsSuccessStatusCode)
                        throw new HttpRequestException("HTTP " + ((int)res.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + res.ReasonPhrase, null, res.StatusCode);
                    await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    await src.CopyToAsync(fs, ct).ConfigureAwait(false);
                }
                File.Move(tmp, dest, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < PromptFlowStudio.Services.Network.ResilientHttpClient.MaxRetries && !ct.IsCancellationRequested
                && (ex is IOException or TaskCanceledException || (ex is HttpRequestException h && (h.StatusCode is null || PromptFlowStudio.Services.Network.ResilientHttpClient.IsTransient(h.StatusCode.Value)))))
            {
                // 🛡️ 2^n 초 + Jitter 지수 백오프(⏳ 작업실 재시도 알림)
                await PromptFlowStudio.Services.Network.ResilientHttpClient.Shared.WaitBeforeRetryAsync(attempt + 1, ct).ConfigureAwait(false);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Mock 샘플 길이(초).</summary>
    public const int MockDurationSec = 5;

    /// <summary>
    /// Mock 샘플 ffmpeg 인자 — <c>-y -f lavfi -i testsrc=duration=5:size=WxH:rate=30 -c:v libx264 -pix_fmt yuv420p {out}</c>.
    /// 해상도 파싱 실패 시 1280x720.
    /// </summary>
    internal static string[] BuildMockArgs(string? resolution, string output, bool ambient = false)
    {
        var size = ToEngineSize(resolution)?.Replace('*', 'x') ?? "1280x720";
        if (!IsValidSize(size)) size = "1280x720";
        if (ambient)
            return ["-y", "-f", "lavfi", "-i", "color=c=0x07090E:s=" + ObsidianBaseSize + ":r=30:d=" + MockDurationSec.ToString(CultureInfo.InvariantCulture),
                    "-vf", BuildObsidianPulseFilter(size), "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", output];
        return ["-y", "-f", "lavfi", "-i", "testsrc=duration=" + MockDurationSec.ToString(CultureInfo.InvariantCulture) + ":size=" + size + ":rate=30",
                "-c:v", "libx264", "-pix_fmt", "yuv420p", output];
    }

    /// <summary>
    /// 🌑 Mock 앰비언트 — 옵시디언(#07090E) 바탕 동심원 펄스(앰버/시안 교차 · 중심 감쇠) + 비네트. 방송 컬러바 대체.
    /// geq 는 픽셀당 연산이 무거워 <see cref="ObsidianBaseSize"/> 저해상도로 생성 후 bicubic 업스케일(링이 부드러워 손실 없음).
    /// </summary>
    internal static string BuildObsidianPulseFilter(string size)
        => "format=gbrp,geq="
           + "r='" + ObsidianRing + "7+30*ld(2)+6*ld(3)'"
           + ":g='" + ObsidianRing + "9+20*ld(2)+24*ld(3)'"
           + ":b='" + ObsidianRing + "14+6*ld(2)+30*ld(3)'"
           + ",scale=" + size.Replace('x', ':') + ":flags=bicubic,vignette=PI/4,format=yuv420p";

    /// <summary>Mock 앰비언트 geq 생성 해상도.</summary>
    internal const string ObsidianBaseSize = "320x180";

    // ld(0)=중심 거리 · ld(1)=중심 감쇠 · ld(2)=앰버 링 · ld(3)=시안 링(반 위상)
    private const string ObsidianRing =
        "st(0,hypot(X-W/2,Y-H/2));st(1,exp(-ld(0)/(W*0.3)));"
        + "st(2,pow(0.5+0.5*sin(ld(0)/5.5-T*2.2),6)*ld(1));st(3,pow(0.5+0.5*sin(ld(0)/5.5-T*2.2+PI),6)*ld(1));";

    private static bool IsValidSize(string s)
    {
        var i = s.IndexOf('x');
        return i > 0 && int.TryParse(s.AsSpan(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w > 0
               && int.TryParse(s.AsSpan(i + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h > 0;
    }

    /// <summary>
    /// Mock 실물 보장 — ffmpeg testsrc 5초 MP4 를 <see cref="BatchRenderJob.OutputPath"/> 에 생성 후 앰비언트 후처리(15초 루프)까지 적용.
    /// <see cref="MockFfmpeg"/> 미구성 · OutputPath 비로컬 · ffmpeg 부재/실패면 null(Graceful).
    /// </summary>
    public async Task<string?> RenderMockAsync(BatchRenderJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (MockFfmpeg is null || string.IsNullOrWhiteSpace(job.OutputPath) || !Path.IsPathRooted(job.OutputPath)) return null;
        var exe = MockFfmpeg();
        if (string.IsNullOrWhiteSpace(exe)) return null;
        var output = job.OutputPath;
        var dir = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var ok = await (MockRunner ?? DefaultMockRunAsync)(exe, BuildMockArgs(job.Resolution, output, job.IsAmbientLoop), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!ok || !RenderQueueService.HasLocalOutput(output))
        {
            Program.Log("RenderExec.Mock", new InvalidOperationException("Mock 샘플 MP4 생성 실패: " + output));
            return null;
        }
        return await ApplyAmbientLoopAsync(job, output, ct).ConfigureAwait(false);
    }

    private static async Task<bool> DefaultMockRunAsync(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var r = await PromptFlowStudio.Services.Engines.ProcessRunner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
        return r.Ok;
    }

    private static string KindOf(IVideoGenerationEngine? engine)
        => engine is null ? RenderEngineKind.Mock
         : engine is ComfyUiVideoEngine ? RenderEngineKind.ComfyUi
         : RenderEngineKind.Cloud;

    private sealed class WanProgressRelay(IProgress<int> target) : IProgress<WanProgress>
    {
        public void Report(WanProgress value) => target.Report(value.Percent);
    }

    /// <summary>진행 중계 + 첫 task_id 보고 시 저널 1회 기록(예외 격리).</summary>
    private sealed class JournalingRelay(IProgress<int>? target, TaskJournalService journal, BatchRenderJob job, string kind) : IProgress<WanProgress>
    {
        private string? _taskId;
        public string? TaskId => Volatile.Read(ref _taskId);

        public void Report(WanProgress value)
        {
            target?.Report(value.Percent);
            if (value.TaskId is not { Length: > 0 } id || Interlocked.CompareExchange(ref _taskId, id, null) is not null) return;
            try { journal.Record(TaskJournalService.EntryFor(job, id, kind, DateTimeOffset.UtcNow)); }
            catch (Exception ex) { Program.Log("RenderExec.Journal", ex); }
        }
    }
}
