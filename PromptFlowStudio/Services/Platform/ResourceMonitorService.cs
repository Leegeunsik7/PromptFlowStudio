using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Platform;

/// <summary>리소스 샘플 1회 — CPU 코어/메모리(작업셋·GC·총량) + GPU(nvidia-smi 있을 때만).</summary>
public sealed record ResourceSample(
    int Cores, long ManagedMb, long WorkingSetMb, long TotalMemMb,
    double? GpuUtilPct, double? VramUsedMb, double? VramTotalMb, string? GpuName, bool GpuAvailable);

/// <summary>
/// [PromptFlowStudio] 렌더링 리소스 모니터 — 순수 BCL(<see cref="Process"/> nvidia-smi 선택 · 외부 NuGet 0).
///   ▸ CPU 코어 수 + 프로세스 작업셋/GC 관리 힙 + 총 가용 메모리(<see cref="GC.GetGCMemoryInfo()"/>) 는 BCL 로 즉시.
///   ▸ GPU 사용률/VRAM 은 <c>nvidia-smi</c>(설치 시)로 조회 — 없으면 1회 판정 후 캐시하고 GPU 미표기(Graceful, CPU 렌더 모드).
/// </summary>
public sealed class ResourceMonitorService
{
    /// <summary>GPU 조회 캐시 TTL — 이 시간 내 재호출은 nvidia-smi 스폰 없이 캐시값 반환.</summary>
    public static readonly TimeSpan GpuCacheTtl = TimeSpan.FromMilliseconds(1500);

    private volatile bool _gpuChecked;
    private volatile bool _gpuAvailable = true;   // 첫 조회 전엔 시도

    private readonly Func<CancellationToken, Task<(string Name, double Util, double Used, double Total)?>> _query;
    private readonly object _gpuGate = new();
    private (string Name, double Util, double Used, double Total)? _gpuCached;
    private long _gpuCachedAt;                     // Stopwatch 타임스탬프(0 = 없음)
    private Task<(string Name, double Util, double Used, double Total)?>? _gpuInflight;   // 동시 호출 합류
    private int _gpuQueries;

    public ResourceMonitorService() : this(QueryNvidiaAsync) { }

    /// <summary>테스트용 — GPU 조회 대체.</summary>
    internal ResourceMonitorService(Func<CancellationToken, Task<(string Name, double Util, double Used, double Total)?>> query) => _query = query;

    /// <summary>실제 GPU 조회(프로세스 스폰) 누적 횟수.</summary>
    public int GpuQueryCount => Volatile.Read(ref _gpuQueries);

    /// <summary>현재 리소스 샘플을 반환한다(GPU 는 nvidia-smi 있을 때만 · <see cref="GpuCacheTtl"/> 캐시). 실패는 Graceful.</summary>
    public async Task<ResourceSample> SampleAsync(CancellationToken ct = default)
    {
        var cores = Environment.ProcessorCount;
        long managed = GC.GetTotalMemory(false) / (1024 * 1024);
        long ws;
        try { ws = Environment.WorkingSet / (1024 * 1024); } catch { ws = managed; }
        long total;
        try { total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024); } catch { total = 0; }
        if (total <= 0) total = Math.Max(ws * 4, 8192);

        if (_gpuChecked && !_gpuAvailable)
            return new(cores, managed, ws, total, null, null, null, null, false);

        var gpu = await GetGpuCachedAsync(ct).ConfigureAwait(false);
        _gpuChecked = true;
        if (gpu is null) { _gpuAvailable = false; return new(cores, managed, ws, total, null, null, null, null, false); }
        _gpuAvailable = true;
        return new(cores, managed, ws, total, gpu.Value.Util, gpu.Value.Used, gpu.Value.Total, gpu.Value.Name, true);
    }

    /// <summary>TTL 내면 캐시 반환 · 진행 중 조회가 있으면 합류 · 아니면 1회 조회 후 캐시 갱신.</summary>
    private async Task<(string Name, double Util, double Used, double Total)?> GetGpuCachedAsync(CancellationToken ct)
    {
        Task<(string Name, double Util, double Used, double Total)?> task;
        lock (_gpuGate)
        {
            if (_gpuCachedAt != 0 && Stopwatch.GetElapsedTime(_gpuCachedAt) < GpuCacheTtl) return _gpuCached;
            task = _gpuInflight ?? StartQuery(ct);
        }
        return await task.ConfigureAwait(false);
    }

    /// <summary>_gpuGate 보유 상태에서 호출 — 동기 완료 시(재진입 lock 으로 이미 정리됨) 진행 중 슬롯에 남기지 않는다.</summary>
    private Task<(string Name, double Util, double Used, double Total)?> StartQuery(CancellationToken ct)
    {
        Interlocked.Increment(ref _gpuQueries);
        var t = RunQueryAsync(ct);
        if (!t.IsCompleted) _gpuInflight = t;
        return t;
    }

    private async Task<(string Name, double Util, double Used, double Total)?> RunQueryAsync(CancellationToken ct)
    {
        (string Name, double Util, double Used, double Total)? r = null;
        try { r = await _query(ct).ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("ResourceMonitor.Query", ex); }
        lock (_gpuGate)
        {
            _gpuCached = r;
            _gpuCachedAt = Stopwatch.GetTimestamp();
            _gpuInflight = null;
        }
        return r;
    }

    private static async Task<(string Name, double Util, double Used, double Total)?> QueryNvidiaAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var proc = ProcessCompat.Start(psi);
            if (proc is null) return null;
            var outTask = proc.StandardOutput.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(2500);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch { try { if (!proc.HasExited) proc.KillCompat(entireProcessTree: true); } catch { } return null; }
            var outp = await outTask.ConfigureAwait(false);

            var line = outp.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
            if (line is null) return null;
            var p = line.Split(',');
            if (p.Length < 4) return null;
            var ci = CultureInfo.InvariantCulture;
            double.TryParse(p[1].Trim(), NumberStyles.Float, ci, out var util);
            double.TryParse(p[2].Trim(), NumberStyles.Float, ci, out var used);
            double.TryParse(p[3].Trim(), NumberStyles.Float, ci, out var total);
            return (p[0].Trim(), util, used, total);
        }
        catch (Exception ex) { Program.Log("ResourceMonitor.Nvidia", ex); return null; }
    }
}
