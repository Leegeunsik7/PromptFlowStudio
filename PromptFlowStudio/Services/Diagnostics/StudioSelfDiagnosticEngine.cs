using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // CliPathResolver / ProcessRunner

namespace PromptFlowStudio.Services.Diagnostics;

/// <summary>FFmpeg 프로세스 진단 — PID·CPU 델타(ms)·워킹셋(MB)·수명(초)·정체(hang) 여부.</summary>
public sealed record FfmpegProcInfo(int Pid, double CpuDeltaMs, long WorkingSetMb, int AgeSeconds, bool Stalled);

/// <summary>시스템 진단 스냅샷 — 헬스 점수·RAM 부하·파편화·워킹셋·ffmpeg 수·정체 수·VRAM.</summary>
public sealed record DiagSnapshot(int HealthScore, double RamLoadPct, double FragPct, long WorkingSetMb, int FfmpegCount, int StalledCount, double VramUsedMb, double VramTotalMb);

/// <summary>자가 치유 결과 — 정독 고스트 수·메모리 전후·해제량·파편화 전후·VRAM·메시지.</summary>
public sealed record SelfHealResult(int GhostsKilled, long RamBeforeMb, long RamAfterMb, long FreedMb, double FragBefore, double FragAfter, double VramUsedMb, string Message);

/// <summary>
/// [PromptFlowStudio] 스튜디오 자율 진단 &amp; 세션 자가 치유 엔진(v3.4.0 · 관찰 가능 싱글턴 · 순수 BCL + nvidia-smi CLI · 외부 NuGet 0).
///   ▸ FFmpeg 프로세스의 <b>CPU 델타 0% 지속</b>(데드락/Hang)을 핑으로 감지(<see cref="ScanFfmpegAsync"/>) → 고스트 프로세스 강제 정독(Purge).
///   ▸ <see cref="GC"/> 적극 수집 + LOH 압축으로 메모리 해제, nvidia-smi 로 VRAM 사용량 계측(고스트 종료 = 점유 VRAM 반환).
///   ▸ 마지막 정상 <b>체크포인트</b>(완료 컷 인덱스)를 보관 → 복구 시 그 지점부터 재가동(JobManager 는 완료 컷을 건너뜀). ffmpeg 부재/권한 실패는 Graceful.
/// </summary>
public sealed class StudioSelfDiagnosticEngine
{
    private const int StallSeconds = 5;          // 이 수명 이상 + CPU 정체면 hang 후보
    private const double StallCpuMs = 2.0;       // 핑 구간 CPU 델타 이 미만이면 정체

    private readonly object _lock = new();
    private readonly Dictionary<string, (int Index, DateTimeOffset At)> _checkpoints = new(StringComparer.Ordinal);

    /// <summary>상태 변경 방송(진단 카드 갱신).</summary>
    public event Action? OnChange;

    /// <summary>마지막 자가 치유 결과.</summary>
    public SelfHealResult? LastHeal { get; private set; }

    // ══════════════════════ 진단 ══════════════════════

    /// <summary>ffmpeg 프로세스를 핑 샘플링해 CPU 정체(hang)를 감지한다.</summary>
    public async Task<IReadOnlyList<FfmpegProcInfo>> ScanFfmpegAsync(int pingMs = 600, CancellationToken ct = default)
    {
        var procs = SafeGetFfmpeg();
        if (procs.Count == 0) return Array.Empty<FfmpegProcInfo>();

        var t0 = procs.Select(p => (p, cpu: SafeCpu(p), start: SafeStart(p))).ToList();
        try { await Task.Delay(pingMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }

        var outp = new List<FfmpegProcInfo>();
        foreach (var (p, cpu0, start) in t0)
        {
            var cpu1 = SafeCpu(p);
            var delta = Math.Max(0, cpu1 - cpu0);
            var age = (int)Math.Max(0, (DateTimeOffset.Now - start).TotalSeconds);
            var ws = SafeWorkingSetMb(p);
            outp.Add(new FfmpegProcInfo(SafePid(p), delta, ws, age, IsHang(delta, age)));
        }
        return outp;
    }

    /// <summary>CPU 델타(ms) + 수명(초)으로 hang(데드락) 판정(순수 · 테스트 대상).</summary>
    public static bool IsHang(double cpuDeltaMs, int ageSeconds) => ageSeconds >= StallSeconds && cpuDeltaMs < StallCpuMs;

    /// <summary>시스템 진단 스냅샷을 만든다(RAM 부하·파편화·ffmpeg·VRAM).</summary>
    public async Task<DiagSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var (ramLoad, frag, wsMb) = MemStats();
        var procs = await ScanFfmpegAsync(400, ct).ConfigureAwait(false);
        var stalled = procs.Count(p => p.Stalled);
        var (vramUsed, vramTotal) = await VramAsync(ct).ConfigureAwait(false);
        var score = ComputeHealthScore(ramLoad, frag, stalled, procs.Count);
        return new DiagSnapshot(score, Math.Round(ramLoad, 1), Math.Round(frag, 1), wsMb, procs.Count, stalled, vramUsed, vramTotal);
    }

    /// <summary>헬스 점수(0~100) — RAM 부하·파편화·정체 프로세스·ffmpeg 과다를 감점(순수 · 테스트 대상).</summary>
    public static int ComputeHealthScore(double ramLoadPct, double fragPct, int stalled, int ffmpegCount)
    {
        double score = 100;
        score -= Math.Clamp(ramLoadPct - 70, 0, 30);          // RAM 70% 초과분 감점(최대 30)
        score -= Math.Clamp(fragPct, 0, 20);                  // 파편화 %만큼 감점(최대 20)
        score -= stalled * 18;                                 // 정체(hang) 프로세스당 -18
        score -= Math.Max(0, ffmpegCount - 4) * 4;             // 동시 ffmpeg 4개 초과분 -4씩
        return (int)Math.Clamp(score, 0, 100);
    }

    // ══════════════════════ 자가 치유 ══════════════════════

    /// <summary>고스트(hang) ffmpeg 정독 + GC/LOH 압축 + VRAM 반환. 진단 후 안전하게 복구 준비.</summary>
    public async Task<SelfHealResult> SelfHealAsync(bool purgeStalled = true, CancellationToken ct = default)
    {
        var ramBefore = GC.GetTotalMemory(false) / (1024 * 1024);
        var (_, fragBefore, _) = MemStats();

        var killed = 0;
        if (purgeStalled)
        {
            var procs = await ScanFfmpegAsync(600, ct).ConfigureAwait(false);
            foreach (var p in procs.Where(x => x.Stalled))
                if (SafeKill(p.Pid)) killed++;
        }

        // 적극 GC + LOH 압축(파편화 해제).
        try
        {
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }
        catch (Exception ex) { Program.Log("SelfHeal.GC", ex); }

        var ramAfter = GC.GetTotalMemory(true) / (1024 * 1024);
        var (_, fragAfter, _) = MemStats();
        var (vramUsed, _) = await VramAsync(ct).ConfigureAwait(false);
        var freed = Math.Max(0, ramBefore - ramAfter);

        var result = new SelfHealResult(killed, ramBefore, ramAfter, freed, Math.Round(fragBefore, 1), Math.Round(fragAfter, 1), vramUsed,
            $"자가 치유 완료 · 고스트 {killed}개 정독 · 메모리 {freed}MB 해제 · 파편화 {fragBefore:0.0}%→{fragAfter:0.0}%");
        LastHeal = result;
        OnChange?.Invoke();
        return result;
    }

    // ══════════════════════ 체크포인트(세션 복구) ══════════════════════

    /// <summary>프로젝트의 마지막 정상 완료 컷 인덱스를 체크포인트로 저장.</summary>
    public void SetCheckpoint(string projectId, int completedIndex)
    {
        if (string.IsNullOrWhiteSpace(projectId)) return;
        lock (_lock) _checkpoints[projectId] = (Math.Max(0, completedIndex), DateTimeOffset.Now);
        OnChange?.Invoke();
    }

    /// <summary>복구 시작 지점(체크포인트 다음 컷 인덱스). 없으면 0(처음부터).</summary>
    public int GetResumeIndex(string projectId)
    {
        lock (_lock) return _checkpoints.TryGetValue(projectId, out var cp) ? cp.Index + 1 : 0;
    }

    /// <summary>체크포인트 제거(완주 후).</summary>
    public void ClearCheckpoint(string projectId) { lock (_lock) _checkpoints.Remove(projectId); }

    // ══════════════════════ 헬퍼 ══════════════════════

    private static (double RamLoadPct, double FragPct, long WorkingSetMb) MemStats()
    {
        try
        {
            var gi = GC.GetGCMemoryInfo();
            var ramLoad = gi.TotalAvailableMemoryBytes > 0 ? gi.MemoryLoadBytes / (double)gi.TotalAvailableMemoryBytes * 100 : 0;
            var frag = gi.HeapSizeBytes > 0 ? gi.FragmentedBytes / (double)gi.HeapSizeBytes * 100 : 0;
            var ws = Environment.WorkingSet / (1024 * 1024);
            return (ramLoad, frag, ws);
        }
        catch { return (0, 0, 0); }
    }

    private static async Task<(double UsedMb, double TotalMb)> VramAsync(CancellationToken ct)
    {
        try
        {
            var nvsmi = CliPathResolver.GetExecutablePath("nvidia-smi", "nvidia-smi.exe");
            if (nvsmi is null) return (0, 0);
            var res = await ProcessRunner.RunAsync(nvsmi, new[] { "--query-gpu=memory.used,memory.total", "--format=csv,noheader,nounits" }, null, null, ct).ConfigureAwait(false);
            if (!res.Ok) return (0, 0);
            var parts = (res.StdOut.Split('\n').FirstOrDefault() ?? "").Split(',');
            double used = parts.Length > 0 && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var u) ? u : 0;
            double total = parts.Length > 1 && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 0;
            return (used, total);
        }
        catch { return (0, 0); }
    }

    private static List<Process> SafeGetFfmpeg()
    {
        try { return ProcessCompat.GetProcessesByName("ffmpeg").ToList(); }
        catch { return new List<Process>(); }
    }
    private static double SafeCpu(Process p) { try { return p.TotalProcessorTimeCompat().TotalMilliseconds; } catch { return 0; } }
    private static DateTimeOffset SafeStart(Process p) { try { return p.StartTimeCompat(); } catch { return DateTimeOffset.Now; } }
    private static long SafeWorkingSetMb(Process p) { try { return p.WorkingSet64 / (1024 * 1024); } catch { return 0; } }
    private static int SafePid(Process p) { try { return p.Id; } catch { return 0; } }
    private static bool SafeKill(int pid)
    {
        try { using var p = Process.GetProcessById(pid); p.KillCompat(true); return true; }
        catch (Exception ex) { Program.Log("SelfHeal.Kill", ex); return false; }
    }
}
