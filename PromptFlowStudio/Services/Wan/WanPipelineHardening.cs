using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // CliPathResolver

namespace PromptFlowStudio.Services.Wan;

/// <summary>VRAM 탐지 결과 — 총 VRAM(MB)·탐지 성공 여부·타임아웃 여부·출처. 실패는 항상 0MB(Graceful).</summary>
public sealed record VramProbe(int TotalMb, bool Detected, bool TimedOut, string Source)
{
    /// <summary>8GB 미만이면 저VRAM(Low VRAM) 로컬 연산 모드로 판정.</summary>
    public bool LowVram => TotalMb < 8000;
    /// <summary>미탐지/실패 표준값(0MB · CPU 또는 최저 오프로딩 모드).</summary>
    public static readonly VramProbe None = new(0, false, false, "미탐지");
    /// <summary>타임아웃 표준값(무응답 nvidia-smi 를 강제 종료 → 0MB).</summary>
    public static readonly VramProbe Timeout = new(0, false, true, "타임아웃");
}

/// <summary>하이브리드 실행 경로 — 클라우드 성공 · 로컬 폴백 · 전부 실패.</summary>
public enum HybridPath { CloudSucceeded, LocalFallback, Failed }

/// <summary>하이브리드 실행 결과 — 경로·산출값·클라우드 시도횟수·사유.</summary>
public sealed record HybridOutcome<T>(HybridPath Path, T? Value, int CloudAttempts, string Message)
{
    /// <summary>어떤 경로로든 산출물을 얻었는지(클라우드 또는 로컬).</summary>
    public bool Ok => Path is HybridPath.CloudSucceeded or HybridPath.LocalFallback;
}

/// <summary>
/// [PromptFlowStudio] v4.4.3 Task2 · Wan 로컬/클라우드 하이브리드 폴백 하드닝 — 순수 BCL(외부 NuGet·Polly 0).
///   ▸ <b>VRAM 탐지 무응답(Hang) 차단</b>: <see cref="Process"/>+<see cref="ProcessStartInfo"/> 로 nvidia-smi 를 호출하되
///     <b>명시적 <see cref="TimeSpan"/> 타임아웃</b>으로 감싸고(stdout·stderr 동시 드레인 → 파이프 데드락 차단), 무응답이면 프로세스 트리를 강제 종료하고
///     <b>0MB 로 Graceful Fallback</b>(<see cref="DetectVramMbAsync"/>).
///   ▸ <b>클라우드 단선 흡수</b>: 렌더 클라우드 호출을 요청 1건 타임아웃으로 감싸 5xx/타임아웃/소켓 오류를 감지하고,
///     <b>순수 지수 백오프(+지터) 재시도</b> 후 소진 시 즉시 <b>로컬(Low VRAM) 연산으로 폴백</b>(<see cref="RunWithFallbackAsync{T}"/>).
///   ▸ 어떤 실패(미설치·권한·예외)도 예외를 밖으로 던지지 않는다 — 파이프라인은 항상 산출물 또는 명확한 실패 사유로 귀결된다.
/// </summary>
public sealed class WanPipelineHardening
{
    /// <summary>VRAM 탐지 기본 타임아웃 — 무응답 nvidia-smi 를 이 시간 안에 강제 종료.</summary>
    public static readonly TimeSpan DefaultVramTimeout = TimeSpan.FromSeconds(3);

    /// <summary>클라우드 최대 시도 횟수(1 이상). 소진 시 로컬 폴백.</summary>
    public int MaxCloudAttempts { get; init; } = 3;
    /// <summary>지수 백오프 기준 간격(1회차).</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(800);
    /// <summary>백오프 상한(폭주 방지).</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary>클라우드 요청 1건 타임아웃(HttpClient 자체 타임아웃과 별개로 강제 상한).</summary>
    public TimeSpan PerAttemptTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // ══════════════════════ VRAM 탐지(TimeSpan 타임아웃 → 0MB Graceful) ══════════════════════

    /// <summary>
    /// nvidia-smi 로 총 VRAM(MB)을 탐지한다. <paramref name="timeout"/>(기본 <see cref="DefaultVramTimeout"/>) 안에 응답이 없으면
    /// 프로세스 트리를 강제 종료하고 <see cref="VramProbe.Timeout"/>(0MB)을 반환한다. 미설치/예외는 <see cref="VramProbe.None"/>(0MB).
    /// </summary>
    public async Task<VramProbe> DetectVramMbAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var budget = timeout is { } t && t > TimeSpan.Zero ? t : DefaultVramTimeout;
        var exe = ResolveNvidiaSmi();
        if (exe is null) return VramProbe.None;   // 미설치 → 0MB(CPU/최저 오프로딩 모드)

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("--query-gpu=memory.total");
            psi.ArgumentList.Add("--format=csv,noheader,nounits");

            proc = new Process { StartInfo = psi };
            if (!proc.StartCompat()) return VramProbe.None;

            // ⚠ stdout·stderr 를 동시에 비동기로 읽는다 — 한쪽 파이프 버퍼가 가득 차 WaitForExit 가 영원히 막히는 데드락을 차단.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);   // ⏱ TimeSpan 타임아웃 — 무응답이면 여기서 취소된다.

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(proc);   // 취소/타임아웃 → 프로세스 트리 완전 종료(Orphan 방지)
                return ct.IsCancellationRequested ? VramProbe.None : VramProbe.Timeout;
            }

            var outp = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);   // stderr 도 끝까지 읽어 파이프를 비운다.

            var mb = AlibabaWanHybridEngine.ParseNvidiaSmiVram(outp);   // 기존 파서 재사용(단일 진실 원천)
            return mb > 0 ? new VramProbe(mb, true, false, "nvidia-smi") : VramProbe.None;
        }
        catch (Exception ex)
        {
            Program.Log("WanPipelineHardening.DetectVram", ex);
            return VramProbe.None;   // 어떤 실패든 0MB Graceful.
        }
        finally
        {
            // 어떤 경로로 빠져나가도 살아있는 프로세스를 남기지 않는다.
            try { if (proc is not null && !proc.HasExited) proc.KillCompat(entireProcessTree: true); } catch { /* 무시 */ }
            proc?.Dispose();
        }
    }

    /// <summary>탐지된 VRAM 으로 권장 로컬 무료 티어를 고른다(<see cref="AlibabaWanHybridEngine.RecommendLocalTier"/> 위임).</summary>
    public async Task<WanModelTier> RecommendLocalTierAsync(CancellationToken ct = default)
    {
        var probe = await DetectVramMbAsync(null, ct).ConfigureAwait(false);
        return AlibabaWanHybridEngine.RecommendLocalTier(probe.TotalMb);   // 0MB(미탐지)도 최저 FP8 로 오프로딩
    }

    // ══════════════════════ 클라우드→로컬 하이브리드 폴백(지수 백오프) ══════════════════════

    /// <summary>
    /// 클라우드 렌더 호출을 요청 1건 타임아웃으로 감싸 실행하고, 일시 오류(5xx/타임아웃/단선)면 지수 백오프로 재시도한다.
    /// 시도를 소진하거나 비일시적 오류면 <paramref name="localFallback"/>(Low VRAM 로컬 연산)으로 전환한다.
    /// </summary>
    /// <param name="cloudCall">클라우드 렌더 1회 호출(전달된 토큰으로 취소·타임아웃 준수).</param>
    /// <param name="isTransient">예외가 재시도할 만한 일시 오류인지 판정(기본은 <see cref="IsTransientException"/>).</param>
    /// <param name="localFallback">클라우드 실패 시 로컬 폴백 연산.</param>
    /// <param name="progress">진행 메시지(선택).</param>
    public async Task<HybridOutcome<T>> RunWithFallbackAsync<T>(
        Func<CancellationToken, Task<T>> cloudCall,
        Func<Exception?, bool> isTransient,
        Func<CancellationToken, Task<T>> localFallback,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var max = Math.Max(1, MaxCloudAttempts);
        Exception? last = null;

        for (var attempt = 1; attempt <= max; attempt++)
        {
            if (ct.IsCancellationRequested)
                return new HybridOutcome<T>(HybridPath.Failed, default, attempt - 1, "취소됨");

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(PerAttemptTimeout);   // ⏱ 요청 1건 상한 — 무응답/단선 방어.

            try
            {
                progress?.Report($"클라우드 렌더 요청 ({attempt}/{max})…");
                var value = await cloudCall(attemptCts.Token).ConfigureAwait(false);
                return new HybridOutcome<T>(HybridPath.CloudSucceeded, value, attempt, "클라우드 성공");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new HybridOutcome<T>(HybridPath.Failed, default, attempt, "취소됨");   // 사용자 취소 — 폴백하지 않음.
            }
            catch (Exception ex)
            {
                last = ex;
                // attemptCts 타임아웃은 OCE 로 오지만 ct 는 취소되지 않았으므로 일시 오류로 취급.
                var transient = ex is OperationCanceledException || SafeIsTransient(isTransient, ex);
                if (!transient)
                {
                    progress?.Report("비일시적 클라우드 오류 — 로컬 폴백으로 전환합니다.");
                    break;   // 재시도 무의미 → 즉시 로컬 폴백.
                }
                if (attempt < max)
                {
                    var delay = BackoffFor(attempt);
                    progress?.Report($"⚠️ 네트워크 불안정({Reason(ex)}) — {delay.TotalSeconds:0.#}s 후 재시도");
                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return new HybridOutcome<T>(HybridPath.Failed, default, attempt, "취소됨"); }
                }
            }
        }

        // ── 클라우드 소진/비일시적 실패 → 로컬(Low VRAM) 폴백 ──
        try
        {
            progress?.Report("로컬(Low VRAM) 연산으로 폴백 실행 중…");
            var localVal = await localFallback(ct).ConfigureAwait(false);
            return new HybridOutcome<T>(HybridPath.LocalFallback, localVal, max, "로컬 폴백 성공 · 클라우드 사유: " + (last?.Message ?? "불가"));
        }
        catch (OperationCanceledException) { return new HybridOutcome<T>(HybridPath.Failed, default, max, "취소됨"); }
        catch (Exception ex)
        {
            Program.Log("WanPipelineHardening.LocalFallback", ex);
            return new HybridOutcome<T>(HybridPath.Failed, default, max, "클라우드/로컬 모두 실패: " + ex.Message);
        }
    }

    /// <summary>지수 백오프 간격(상한 + ±20% 지터로 동시 재시도 충돌 분산).</summary>
    public TimeSpan BackoffFor(int attempt)
    {
        var exp = BaseBackoff.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = Math.Min(exp, MaxBackoff.TotalMilliseconds);
        var jitter = capped * 0.2 * Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(capped + jitter);
    }

    // ══════════════════════ 일시 오류 판정(순수 · 테스트 대상) ══════════════════════

    /// <summary>HTTP 상태코드가 재시도 대상(5xx · 408 · 429)인지.</summary>
    public static bool IsTransientHttp(HttpStatusCode status)
        => (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>예외가 네트워크 단선/타임아웃 계열(재시도 가치 있음)인지 — 순수 BCL 분류.</summary>
    public static bool IsTransientException(Exception? ex) => ex switch
    {
        null => false,
        HttpRequestException => true,          // DNS/소켓/연결 거부
        TaskCanceledException => true,          // HttpClient 자체 타임아웃
        OperationCanceledException => true,     // 요청 1건 타임아웃(attemptCts)
        IOException => true,                    // 전송 중 스트림 단선
        SocketException => true,                // 저수준 소켓 오류
        _ => false,
    };

    private static bool SafeIsTransient(Func<Exception?, bool> pred, Exception ex)
    {
        try { return pred(ex); } catch { return false; }
    }

    private static string Reason(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "타임아웃",
        HttpRequestException h when h.StatusCode is { } s => $"HTTP {(int)s}",
        HttpRequestException => "연결 실패",
        SocketException => "소켓",
        IOException => "단선",
        _ => ex.GetType().Name,
    };

    // ══════════════════════ 도구 해석 ══════════════════════

    private static string? ResolveNvidiaSmi()
    {
        try { return CliPathResolver.GetExecutablePath("nvidia-smi", "nvidia-smi.exe"); }
        catch { return null; }
    }

    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.KillCompat(entireProcessTree: true);
            try { proc.WaitForExit(2000); } catch { /* 무시 */ }
        }
        catch { /* 이미 종료/권한 — 무시 */ }
    }
}
