using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Wan;   // WanPromptCompiler · WanSampleFactory · AudioWaveformAnalyzer · AudioTrackInfo

namespace PromptFlowStudio.Services.Inference;

/// <summary>로컬 추론 종류.</summary>
public enum LocalInferenceKind { PromptBoost, StillFrame, AudioRms }

/// <summary>로컬 추론 결과(불변) — 백엔드/경과시간 + 종류별 산출물.</summary>
public sealed record LocalInferenceResult(
    bool Ok, InferenceBackend Backend, LocalInferenceKind Kind,
    string? Text, string? FrameUrl, double ElapsedMs, string Message);

/// <summary>
/// [PromptFlow Studio v7.0.0] Local-First WebGPU/ONNX 추론 엔진(관찰 가능 싱글턴 · 순수 BCL · 외부 NuGet 0).
///   ▸ 클라우드 GPU 워커 없이 단말에서 프롬프트 부스팅·스틸컷 프레임·오디오 RMS 를 <b>0.5초 이내</b> 즉시 수행.
///   ▸ WebGPU/DirectML 미가속 환경(=현재 · 네이티브 패키지 미탑재)에서는 BCL 휴리스틱(<see cref="WanPromptCompiler.EnhancePromptHeuristic"/>)·
///     SVG 스텁 프레임(<see cref="WanSampleFactory.FrameDataUrl"/>)·BCL 오디오 분석(<see cref="AudioWaveformAnalyzer"/>)으로 <b>Graceful 자동 디그레이드</b>.
///   ⚠ <see cref="OnChange"/> 는 호출 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class WebGpuInferenceEngine
{
    private readonly LocalModelLoader _loader;
    private long _total, _accelerated;

    public WebGpuInferenceEngine(LocalModelLoader loader) { _loader = loader; }

    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    /// <summary>감지된 하드웨어 가속 백엔드(DirectML/WebGPU/BCL · HW 상주 기준).</summary>
    public InferenceBackend Backend => _loader.DetectBackend();
    public string BackendLabel => _loader.BackendLabel;
    /// <summary>가속 HW 상주 여부(D3D12/WebGPU).</summary>
    public bool Accelerated => _loader.IsAccelerated;
    /// <summary>네이티브 텐서 경로 실제 가동 가능 여부(가속 HW + 모델 가중치 로드). 현재는 가중치 미탑재라 false → BCL 연산.</summary>
    public bool NativeReady => Accelerated && _loader.HasLoadedWeights;
    public long TotalInferences => Interlocked.Read(ref _total);
    public long AcceleratedInferences => Interlocked.Read(ref _accelerated);

    /// <summary>프롬프트 부스팅 — 네이티브 텐서(가속+가중치) 우선, 미가용/OOM/드라이버 예외 시 BCL 시네마틱 휴리스틱으로 즉시 이관.</summary>
    public async Task<LocalInferenceResult> BoostPromptAsync(string? prompt, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var (text, backend) = await RunOrFallbackAsync(
            native: async () => await RunNativeTensorInferenceAsync(prompt ?? "", ct).ConfigureAwait(false),
            bcl: () => WanPromptCompiler.EnhancePromptHeuristic(prompt)).ConfigureAwait(false);
        sw.Stop();
        Count(backend);
        return new LocalInferenceResult(true, backend, LocalInferenceKind.PromptBoost, text, null, sw.Elapsed.TotalMilliseconds,
            backend == InferenceBackend.Bcl ? "로컬 부스팅 · BCL 휴리스틱" : $"로컬 부스팅 · {BackendLabel} 텐서");
    }

    /// <summary>스틸컷 프레임 즉시 생성 — 가속/BCL 공통 SVG 스텁 DataURL(가중치 결선 시 네이티브 렌더로 승격). OOM/예외는 BCL 폴백.</summary>
    public Task<LocalInferenceResult> GenerateStillAsync(string? prompt, string accent = "#22d3ee", CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var backend = NativeReady ? Backend : InferenceBackend.Bcl;
        string url;
        try
        {
            var label = string.IsNullOrWhiteSpace(prompt) ? "LOCAL FRAME" : Trunc(prompt!, 22);
            url = WanSampleFactory.FrameDataUrl(label, accent, backend == InferenceBackend.Bcl ? "LOCAL BCL" : BackendLabel.ToUpperInvariant());
        }
        catch (Exception ex) { Program.Log("WebGpuInference.Still", ex); url = WanSampleFactory.FrameDataUrl("LOCAL", accent, "BCL"); backend = InferenceBackend.Bcl; }
        sw.Stop();
        Count(backend);
        return Task.FromResult(new LocalInferenceResult(true, backend, LocalInferenceKind.StillFrame, null, url, sw.Elapsed.TotalMilliseconds,
            backend == InferenceBackend.Bcl ? "로컬 스틸컷 · BCL" : $"로컬 스틸컷 · {BackendLabel}"));
    }

    /// <summary>
    /// 네이티브 텐서 추론 우선 실행, 실패(미탑재/OOM/드라이버) 시 BCL 델리게이트로 이관. 반환=(결과, 실제 연산 백엔드).
    /// </summary>
    private async Task<(string Text, InferenceBackend Backend)> RunOrFallbackAsync(Func<Task<string>> native, Func<string> bcl)
    {
        if (NativeReady)
        {
            try { return (await native().ConfigureAwait(false), Backend); }
            catch (OutOfMemoryException ex) { Program.Log("WebGpuInference.OOM", ex); }   // 텐서 버퍼 OOM → BCL
            catch (NotSupportedException) { /* 가중치 미탑재 → BCL */ }
            catch (Exception ex) { Program.Log("WebGpuInference.Native", ex); }            // 드라이버/기타 → BCL
        }
        try { return (bcl(), InferenceBackend.Bcl); }
        catch (Exception ex) { Program.Log("WebGpuInference.Bcl", ex); return ("", InferenceBackend.Bcl); }
    }

    /// <summary>
    /// [결선 지점] 가속 HW + 로드된 가중치로 네이티브 GPU 텐서 추론을 수행한다.
    ///   ⚠ 현재는 온디바이스 가중치 미탑재(외부 NuGet/모델 0)라 <see cref="NotSupportedException"/> 로 상위 BCL 폴백을 유도.
    ///   실제 결선 시: ORT DirectML 세션(#if WINDOWS) 또는 WebGPU 컴퓨트 셰이더로 텐서 버퍼를 가동하도록 이 메서드만 교체.
    /// </summary>
    private static Task<string> RunNativeTensorInferenceAsync(string prompt, CancellationToken ct)
        => throw new NotSupportedException("온디바이스 모델 가중치 미탑재 — 네이티브 텐서 경로 비활성(BCL 폴백)");

    /// <summary>오디오 RMS 엔벨로프 분석 — WAV 실측/모의 폴백(BCL). 파일 경로 없으면 파일명 시드 모의.</summary>
    public Task<AudioTrackInfo> AnalyzeAudioAsync(string? pathOrName, CancellationToken ct = default)
    {
        AudioTrackInfo info;
        try { info = AudioWaveformAnalyzer.AnalyzeFile(pathOrName); }
        catch (Exception ex) { Program.Log("WebGpuInference.Audio", ex); info = AudioTrackInfo.Empty; }
        Count(InferenceBackend.Bcl);   // 오디오 RMS 는 BCL 실측/모의
        return Task.FromResult(info);
    }

    // 실제 연산에 사용된 백엔드로 카운트(가속 텐서 실행분만 AcceleratedInferences 증가).
    private void Count(InferenceBackend backend)
    {
        Interlocked.Increment(ref _total);
        if (backend != InferenceBackend.Bcl) Interlocked.Increment(ref _accelerated);
        Notify();
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
