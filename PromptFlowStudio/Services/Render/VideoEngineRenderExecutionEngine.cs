using System;
using System.Collections.Generic;
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
public sealed class VideoEngineRenderExecutionEngine : IRenderExecutionEngine
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

    public bool IsAvailable => TryGetActive() is { Enabled: true };

    public string EngineKind => KindOf(TryGetActive());

    public async Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var engine = TryGetActive();
        var kind = KindOf(engine);
        if (engine is not { Enabled: true }) return new RenderExecutionResult(false, kind, null, "렌더 엔진 미설정");

        var request = BuildRequest(job);
        var relay = progress is null ? null : new WanProgressRelay(progress);
        WanGenerationResult r;
        using (ct.Register(static s => { var (self, k) = ((VideoEngineRenderExecutionEngine, string))s!; self.FireInterrupt(k); }, (this, kind)))
            r = await engine.GenerateAsync(request, relay, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!r.Ok || string.IsNullOrWhiteSpace(r.VideoUrl))
            return new RenderExecutionResult(false, kind, null, string.IsNullOrWhiteSpace(r.Message) ? "엔진 생성 실패" : r.Message);

        var output = r.VideoUrl;
        if (!string.IsNullOrWhiteSpace(job.OutputPath) && Path.IsPathRooted(output) && File.Exists(output)
            && !string.Equals(Path.GetFullPath(output), Path.GetFullPath(job.OutputPath), StringComparison.OrdinalIgnoreCase))
        {
            var dir = Path.GetDirectoryName(job.OutputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(output, job.OutputPath, overwrite: true);
            output = job.OutputPath;
        }
        output = await ApplyCommercialOverlayAsync(job, output, ct).ConfigureAwait(false);
        return new RenderExecutionResult(true, kind, output, r.Message);
    }

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

    /// <summary>ComfyUI 서버 실행 중 프롬프트 중단(<c>PFS_COMFY_URL</c> · 기본 127.0.0.1:8188). 서버 미기동은 무시.</summary>
    private static async Task DefaultInterruptAsync(string kind, CancellationToken ct)
    {
        if (!string.Equals(kind, RenderEngineKind.ComfyUi, StringComparison.Ordinal)) return;
        var baseUrl = Environment.GetEnvironmentVariable("PFS_COMFY_URL") is { Length: > 0 } u ? u.TrimEnd('/') : "http://127.0.0.1:8188";
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
    };

    /// <summary>실존 로컬 이미지면 전체 경로, 아니면 null(T2V).</summary>
    internal static string? ResolveReferenceImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        try { return Path.IsPathRooted(p) && File.Exists(p) ? Path.GetFullPath(p) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>"1280x720" → 엔진 규격 "1280*720".</summary>
    internal static string? ToEngineSize(string? resolution)
        => string.IsNullOrWhiteSpace(resolution) ? null : resolution.Trim().Replace('x', '*').Replace('X', '*');

    private IVideoGenerationEngine? TryGetActive()
    {
        if (_selector is null || _selector.All.Count == 0) return null;
        try { return _selector.Active; } catch (Exception) { return null; }
    }

    private static string KindOf(IVideoGenerationEngine? engine)
        => engine is null ? RenderEngineKind.Mock
         : engine is ComfyUiVideoEngine ? RenderEngineKind.ComfyUi
         : RenderEngineKind.Cloud;

    private sealed class WanProgressRelay(IProgress<int> target) : IProgress<WanProgress>
    {
        public void Report(WanProgress value) => target.Report(value.Percent);
    }
}
