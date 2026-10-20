using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver

namespace PromptFlowStudio.Services.Broadcast;

/// <summary>탐지된 GPU 디바이스 — 인덱스·이름·VRAM(MB).</summary>
public sealed record GpuDevice(int Index, string Name, int VramMB);

/// <summary>분산 렌더링 결과 — 성공/전체 · 디바이스 수 · 메시지.</summary>
public sealed record ClusterRenderResult(int Ok, int Total, int DeviceCount, string Message);

/// <summary>
/// [PromptFlowStudio] 멀티 GPU(Dual NVENC) 분산 렌더링 클러스터 엔진(v2.8.0 · 순수 BCL + nvidia-smi/FFmpeg CLI · 외부 NuGet 0).
///   ▸ 장착된 N개 NVIDIA GPU(<c>cuda:0, cuda:1 …</c>)를 nvidia-smi 로 자동 탐지(<see cref="DetectGpusAsync"/> · 1회 캐시).
///   ▸ 컷별 후처리/모션 보간 작업을 <b>라운드로빈 로드 밸런싱</b>(<see cref="AssignRoundRobin"/>)으로 디바이스에 분산, 디바이스별 병렬 처리(<see cref="DistributeAsync"/>).
///   ▸ 각 작업은 <c>-hwaccel_device {i}</c> 디코드 + <c>h264_nvenc -gpu {i}</c> 인코드로 지정 GPU 에 고정 → Dual NVENC 를 동시 가동해 인코딩 처리량 극대화.
///   ▸ GPU 1개 이하면 분산 없이 단일 처리(역호환). 실패 디바이스/작업은 건너뛰고 계속(Graceful).
/// </summary>
public sealed class MultiGpuClusterEngine
{
    private readonly object _lock = new();
    private IReadOnlyList<GpuDevice>? _devices;

    /// <summary>탐지된 디바이스(미탐지면 빈 목록).</summary>
    public IReadOnlyList<GpuDevice> Devices => _devices ?? Array.Empty<GpuDevice>();
    /// <summary>탐지된 GPU 수.</summary>
    public int DeviceCount => Devices.Count;
    /// <summary>2개 이상이면 분산 가동 가능.</summary>
    public bool CanDistribute => DeviceCount >= 2;

    /// <summary>GPU 목록을 탐지해 캐시한다(중복 호출 시 캐시 반환).</summary>
    public async Task<IReadOnlyList<GpuDevice>> DetectGpusAsync(CancellationToken ct = default)
    {
        lock (_lock) { if (_devices is not null) return _devices; }
        var list = new List<GpuDevice>();
        try
        {
            var nvsmi = CliPathResolver.GetExecutablePath("nvidia-smi", "nvidia-smi.exe");
            if (nvsmi is not null)
            {
                var res = await ProcessRunner.RunAsync(nvsmi, new[] { "--query-gpu=index,name,memory.total", "--format=csv,noheader,nounits" }, null, null, ct).ConfigureAwait(false);
                if (res.Ok) list = ParseGpuCsv(res.StdOut);
            }
        }
        catch (Exception ex) { Program.Log("MultiGpu.Detect", ex); }
        lock (_lock) { _devices ??= list; return _devices; }
    }

    /// <summary>nvidia-smi CSV(index,name,memory.total) 파싱(테스트 대상).</summary>
    public static List<GpuDevice> ParseGpuCsv(string? csv)
    {
        var list = new List<GpuDevice>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (var line in csv.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) continue;
            int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var vram);
            list.Add(new GpuDevice(idx, parts[1].Trim(), vram));
        }
        return list;
    }

    /// <summary>작업 수 → GPU 인덱스 라운드로빈 배정(순수 · 테스트 대상). gpuCount≤1 이면 전부 0.</summary>
    public static int[] AssignRoundRobin(int count, int gpuCount)
    {
        var g = Math.Max(1, gpuCount);
        var a = new int[Math.Max(0, count)];
        for (var i = 0; i < a.Length; i++) a[i] = i % g;
        return a;
    }

    /// <summary>
    /// 항목들을 GPU 디바이스에 라운드로빈 분산하고, 디바이스별로 <b>병렬</b> 처리한다(디바이스 내부는 순차).
    ///   worker(item, gpuIndex)가 각 작업을 지정 GPU 로 수행. 디바이스 0개면 단일(gpu 0) 처리.
    /// </summary>
    public async Task DistributeAsync<T>(IReadOnlyList<T> items, Func<T, int, CancellationToken, Task> worker, CancellationToken ct = default)
    {
        if (items is null || items.Count == 0) return;
        var gpuCount = Math.Max(1, DeviceCount);
        var assign = AssignRoundRobin(items.Count, gpuCount);

        // 디바이스별 작업 그룹 → 그룹 간 병렬, 그룹 내 순차(한 GPU 동시 1작업).
        var groups = Enumerable.Range(0, gpuCount)
            .Select(g => Enumerable.Range(0, items.Count).Where(i => assign[i] == g).ToList())
            .ToList();

        var tasks = groups.Select(async (idxList, gpu) =>
        {
            foreach (var i in idxList)
            {
                ct.ThrowIfCancellationRequested();
                try { await worker(items[i], gpu, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Program.Log("MultiGpu.Worker", ex); }   // 실패 작업 건너뜀
            }
        });
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    /// <summary>지정 GPU 디바이스에서 60fps 모션 보간(NVENC)한다. 실패는 false(호출부가 원본 유지 · Graceful).</summary>
    public async Task<bool> InterpolateOnGpuAsync(string input, string output, int fps, int gpuIndex, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) return false;
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null) return false;
        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var res = await ProcessRunner.RunAsync(ffmpeg, BuildDeviceArgs(input, output, fps, gpuIndex), null, null, ct).ConfigureAwait(false);
            return res.Ok && File.Exists(output);
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Program.Log("MultiGpu.Interpolate", ex); return false; }
    }

    /// <summary>지정 GPU 디바이스 고정 인코딩 인자(테스트 대상) — <c>-hwaccel_device i</c> + <c>-gpu i</c>.</summary>
    public static IReadOnlyList<string> BuildDeviceArgs(string input, string output, int fps, int gpuIndex)
    {
        var f = fps.ToString(CultureInfo.InvariantCulture);
        var g = Math.Max(0, gpuIndex).ToString(CultureInfo.InvariantCulture);
        return new List<string>
        {
            "-y", "-hwaccel", "cuda", "-hwaccel_device", g, "-i", input,
            "-vf", $"minterpolate=fps={f}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1",
            "-c:v", "h264_nvenc", "-gpu", g, "-preset", "p5", "-rc", "vbr", "-cq", "20",
            "-pix_fmt", "yuv420p", "-c:a", "copy", "-movflags", "+faststart", output,
        };
    }

    private static string? ResolveFfmpeg()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return CliPathResolver.GetExecutablePath("ffmpeg", "ffmpeg.exe");
        }
        catch { return null; }
    }
}
