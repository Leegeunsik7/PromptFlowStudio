using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Services.Render;

/// <summary>
/// [PromptFlowStudio] <see cref="VideoEngineSelector"/> 활성 엔진(ComfyUI 로컬 · Wan/멀티 엔진 루터 클라우드)으로 렌더 작업 실행.
///   ▸ 선택기 미등록(모바일·테스트) 또는 활성 엔진 비활성(키/실행파일 미구성)이면 <see cref="IsAvailable"/>=false → 큐 시뮬레이션 폴백.
///   ▸ 로컬 엔진 산출물은 <see cref="BatchRenderJob.OutputPath"/> 지정 시 그 경로로 복사(갤러리 메타 동반 기록).
/// </summary>
public sealed class VideoEngineRenderExecutionEngine : IRenderExecutionEngine
{
    private readonly VideoEngineSelector? _selector;

    public VideoEngineRenderExecutionEngine(VideoEngineSelector? selector) => _selector = selector;

    public bool IsAvailable => TryGetActive() is { Enabled: true };

    public string EngineKind => KindOf(TryGetActive());

    public async Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var engine = TryGetActive();
        var kind = KindOf(engine);
        if (engine is not { Enabled: true }) return new RenderExecutionResult(false, kind, null, "렌더 엔진 미설정");

        var request = new WanVideoRequest
        {
            Prompt = job.Prompt,
            Size = ToEngineSize(job.Resolution),
            Seed = job.Seed,
        };
        var relay = progress is null ? null : new WanProgressRelay(progress);
        var r = await engine.GenerateAsync(request, relay, ct).ConfigureAwait(false);
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
        return new RenderExecutionResult(true, kind, output, r.Message);
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
