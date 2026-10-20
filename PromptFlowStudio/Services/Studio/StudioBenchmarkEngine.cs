using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver
using PromptFlowStudio.Services.Wan;       // NvencGpuMotionEngine
using PromptFlowStudio.Services.Storyboard; // StoryboardProject

namespace PromptFlowStudio.Services.Studio;

/// <summary>벤치마크 결과 — 하드웨어/네트워크 측정치 + 자동 교정 권고(병렬 스레드·보간·티어·점수).</summary>
public sealed record BenchmarkResult(
    int CpuThreads, double RamGB, double VramGB, bool NvencAvailable, string? GpuName,
    double NvencFps, int ApiLatencyMs, int Score, int RecommendedParallel, bool RecommendInterpolation, string Tier, string Message)
{
    public static BenchmarkResult Empty => new(Environment.ProcessorCount, 0, 0, false, null, 0, -1, 0, 2, false, "-", "미측정");
}

/// <summary>
/// [PromptFlowStudio] 엔드투엔드 파이프라인 벤치마킹 &amp; 자동 교정 엔진(v2.7.0 · 관찰 가능 싱글턴 · 순수 BCL + FFmpeg/nvidia-smi CLI · 외부 NuGet 0).
///   ▸ <b>Wan API 지연</b>(HTTP 왕복), <b>NVENC 인코딩 throughput</b>(testsrc 실측 FPS), <b>VRAM/RAM</b>(nvidia-smi / GC)를 측정한다.
///   ▸ 측정치로 <b>하드웨어 동적 교정</b>: 병렬 가동 스레드 수(<see cref="BenchmarkResult.RecommendedParallel"/>)와 GPU 모션 보간 프리셋을 자동 스케일링(<see cref="Recommend"/>).
///   ▸ <see cref="ApplyTo"/> 로 프로젝트에 권고를 적용(AutoInterpolate · TunedParallelThreads). 측정 실패는 Graceful(보수적 기본값 유지).
/// </summary>
public sealed class StudioBenchmarkEngine
{
    private readonly NvencGpuMotionEngine _gpu;
    public StudioBenchmarkEngine(NvencGpuMotionEngine gpu) { _gpu = gpu; }

    /// <summary>상태 변경 방송(대시보드 게이지 갱신).</summary>
    public event Action? OnChange;

    /// <summary>마지막 측정 결과(미측정이면 Empty).</summary>
    public BenchmarkResult Last { get; private set; } = BenchmarkResult.Empty;
    /// <summary>측정 진행 중 여부.</summary>
    public bool IsRunning { get; private set; }
    /// <summary>진행 단계 메시지(카드 부제).</summary>
    public string Stage { get; private set; } = "";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary>전체 벤치마크 실행(그레이스풀). apiProbeUrl 미지정 시 API 지연 측정 생략.</summary>
    public async Task<BenchmarkResult> RunAsync(string? apiProbeUrl = null, CancellationToken ct = default)
    {
        IsRunning = true; Stage = "측정 준비…"; OnChange?.Invoke();
        try
        {
            var cores = Environment.ProcessorCount;

            Stage = "메모리 측정…"; OnChange?.Invoke();
            var ramGB = SafeRamGB();

            Stage = "GPU/VRAM 감지…"; OnChange?.Invoke();
            var nvenc = await SafeDetectNvencAsync(ct).ConfigureAwait(false);
            var vramGB = await SafeVramGBAsync(ct).ConfigureAwait(false);

            Stage = "NVENC 인코딩 throughput 측정…"; OnChange?.Invoke();
            var nvencFps = await SafeNvencFpsAsync(nvenc, ct).ConfigureAwait(false);

            Stage = "Wan API 지연 측정…"; OnChange?.Invoke();
            var apiMs = await SafeApiLatencyAsync(apiProbeUrl, ct).ConfigureAwait(false);

            var (parallel, interp, score, tier) = Recommend(cores, ramGB, vramGB, nvencFps, apiMs);
            var msg = $"티어 {tier} · 점수 {score} · 권장 병렬 {parallel}스레드 · 보간 {(interp ? "GPU 60fps" : "OFF")}";
            Last = new BenchmarkResult(cores, ramGB, vramGB, nvenc, _gpu.GpuName, nvencFps, apiMs, score, parallel, interp, tier, msg);
            Stage = "완료"; return Last;
        }
        catch (OperationCanceledException) { Stage = "취소됨"; return Last; }
        catch (Exception ex) { Program.Log("StudioBenchmark.Run", ex); Stage = "오류(보수 기본값 유지)"; return Last; }
        finally { IsRunning = false; OnChange?.Invoke(); }
    }

    /// <summary>권고를 프로젝트에 적용(GPU 보간 ON/OFF · 튜닝된 병렬 스레드 수). 역호환 필드만 건드린다.</summary>
    public void ApplyTo(StoryboardProject project)
    {
        if (project is null) return;
        project.AutoInterpolate = Last.RecommendInterpolation;
        project.TunedParallelThreads = Last.RecommendedParallel;
        OnChange?.Invoke();
    }

    // ══════════════════════ 순수 교정 알고리즘(테스트 대상) ══════════════════════

    /// <summary>측정치 → (권장 병렬 스레드, GPU 보간 여부, 점수 0~100, 티어 S~D). 결정론적·부작용 없음.</summary>
    public static (int Parallel, bool Interp, int Score, string Tier) Recommend(int cores, double ramGB, double vramGB, double nvencFps, int apiMs)
    {
        cores = Math.Max(1, cores);

        // 병렬 스레드: CPU·VRAM·NVENC throughput 의 최소 병목으로 산정, 네트워크 지연 시 1 감산.
        var byCpu = Math.Max(1, cores * 3 / 4);
        var byVram = vramGB > 0 ? Math.Max(1, (int)(vramGB / 1.5)) : 2;
        var byNvenc = nvencFps > 0 ? Math.Max(1, (int)(nvencFps / 22.0)) : 2;
        var byRam = ramGB > 0 ? Math.Max(1, (int)(ramGB / 3.0)) : 2;
        var p = Math.Min(Math.Min(byCpu, byVram), Math.Min(byNvenc, byRam));
        if (apiMs > 900) p = Math.Max(1, p - 1);                    // 네트워크 바운드 → 동시 요청 축소
        var parallel = Math.Clamp(p, 1, 8);

        // GPU 모션 보간: NVENC 가용 + VRAM 6GB↑ + 실측 40fps↑ 일 때만(저사양 오동작 방지).
        var interp = nvencFps >= 40 && vramGB >= 6;

        // 점수(0~100): NVENC + VRAM + CPU + API 지연.
        double sNvenc = nvencFps > 0 ? Math.Clamp(nvencFps / 1.2, 0, 40) : 8;
        double sVram = Math.Clamp(vramGB * 2.2, 0, 25);
        double sCpu = Math.Clamp(cores * 1.6, 0, 20);
        double sApi = apiMs <= 0 ? 8 : Math.Clamp(15 - apiMs / 80.0, 0, 15);
        var score = (int)Math.Round(Math.Clamp(sNvenc + sVram + sCpu + sApi, 0, 100));

        var tier = score >= 85 ? "S" : score >= 70 ? "A" : score >= 55 ? "B" : score >= 40 ? "C" : "D";
        return (parallel, interp, score, tier);
    }

    // ══════════════════════ 측정(그레이스풀) ══════════════════════

    private static double SafeRamGB()
    {
        try { return Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1_000_000_000.0, 1); }
        catch { return 0; }
    }

    private async Task<bool> SafeDetectNvencAsync(CancellationToken ct)
    {
        try { return await _gpu.DetectGpuAsync(ct).ConfigureAwait(false); }
        catch { return false; }
    }

    private static async Task<double> SafeVramGBAsync(CancellationToken ct)
    {
        try
        {
            var nvsmi = CliPathResolver.GetExecutablePath("nvidia-smi", "nvidia-smi.exe");
            if (nvsmi is null) return 0;
            var res = await ProcessRunner.RunAsync(nvsmi, new[] { "--query-gpu=memory.total", "--format=csv,noheader,nounits" }, null, null, ct).ConfigureAwait(false);
            if (!res.Ok) return 0;
            var first = res.StdOut.Split('\n').FirstOrDefault()?.Trim();
            return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mb) ? Math.Round(mb / 1024.0, 1) : 0;
        }
        catch { return 0; }
    }

    private static async Task<double> SafeNvencFpsAsync(bool nvenc, CancellationToken ct)
    {
        try
        {
            var ffmpeg = ResolveFfmpeg();
            if (ffmpeg is null) return 0;
            const int frames = 60;   // testsrc2 30fps × 2초
            var codec = nvenc ? new[] { "-c:v", "h264_nvenc", "-preset", "p1" } : new[] { "-c:v", "libx264", "-preset", "ultrafast" };
            var args = new System.Collections.Generic.List<string> { "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-t", "2" };
            args.AddRange(codec);
            args.AddRange(new[] { "-f", "null", "-" });

            var sw = Stopwatch.StartNew();
            var res = await ProcessRunner.RunAsync(ffmpeg, args, null, null, ct).ConfigureAwait(false);
            sw.Stop();
            if (!res.Ok) return 0;
            var sec = Math.Max(0.05, sw.Elapsed.TotalSeconds);
            return Math.Round(frames / sec, 1);   // 실측 throughput(프레임/실경과)
        }
        catch { return 0; }
    }

    private static async Task<int> SafeApiLatencyAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return -1;
        try
        {
            var sw = Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch { return -1; }   // 오프라인/도달 불가 → 미측정
    }

    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (System.IO.File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }
}
