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
}
