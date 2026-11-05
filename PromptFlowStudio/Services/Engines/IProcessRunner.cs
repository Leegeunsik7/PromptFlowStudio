using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Engines;

/// <summary>
/// 🛡️ 외부 CLI 실행 추상화(DI 주입) — 모든 FFmpeg 호출을 단일 실행기로 일원화해 장중 세이프가드(BelowNormal · 동시 1개 게이트)를 누락 없이 적용.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string? workingDir = null,
        Action<bool, string>? onLine = null, CancellationToken ct = default);

    /// <summary>🛡️ 바이너리 파이프(rawvideo) 스트리밍 프로세스 시작 — 거버넌스 우선순위 적용(기본 = <see cref="ProcessRunner.StartStreaming"/>). 실패 null.</summary>
    System.Diagnostics.Process? StartStreaming(string exe, IEnumerable<string> args, bool redirectStdin, bool redirectStdout, CancellationToken ct = default)
        => ProcessRunner.StartStreaming(exe, args, redirectStdin, redirectStdout, ct);

    /// <summary>🛡️ 스트리밍 작업을 장중 동시 실행 게이트 안에서 수행(기본 = <see cref="ProcessRunner.RunGatedAsync"/>).</summary>
    Task<ProcessResult> RunGatedAsync(string exe, Func<CancellationToken, Task<ProcessResult>> body, CancellationToken ct = default)
        => ProcessRunner.RunGatedAsync(exe, body, ct);
}

/// <summary>기본 구현 — 정적 <see cref="ProcessRunner"/> 위임(전역 <c>PriorityResolver</c>/<c>EncoderGate</c> 훅 경유).</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public static readonly SystemProcessRunner Shared = new();

    public Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, string? workingDir = null,
        Action<bool, string>? onLine = null, CancellationToken ct = default)
        => ProcessRunner.RunAsync(exe, args, workingDir, onLine, ct);
}

public static class ProcessRunnerExtensions
{
    /// <summary><see cref="IProcessRunner"/> → <see cref="FfmpegRunner"/> 어댑터(종료 코드 0 = 성공).</summary>
    public static FfmpegRunner AsFfmpegRunner(this IProcessRunner runner)
    {
        ArgumentNullException.ThrowIfNull(runner);
        return async (exe, args, ct) =>
        {
            var r = await runner.RunAsync(exe, args, ct: ct).ConfigureAwait(false);
            return r.Ok;
        };
    }

    /// <summary>
    /// 종료 코드 반환 실행(레거시 <c>int RunAsync</c> 호환) — 미시작 −1 · 타임아웃 −2 · 취소는 <see cref="OperationCanceledException"/> 전파.
    /// </summary>
    public static async Task<int> RunExitCodeAsync(this IProcessRunner runner, string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        using var d = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero) d.CancelAfter(timeout);
        var r = await runner.RunAsync(exe, args, ct: d.Token).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!r.Started) return -1;
        return r.ExitCode;
    }
}
