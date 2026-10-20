using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // ProcessRunner / CliPathResolver
using PromptFlowStudio.Shared.Deploy;

namespace PromptFlowStudio.Services.Deploy;

/// <summary>
/// [PromptFlow Studio v4.4.0] 데스크톱 패키징 런처 — PowerShell(ps1)/Bash(sh) 스크립트를 ProcessRunner 로 체이닝 구동
///   (공용 RCL <see cref="IBuildLauncher"/> 구현). 실행기 부재/실패는 Graceful.
/// </summary>
public sealed class DesktopBuildLauncher : IBuildLauncher
{
    public bool IsSupported => true;

    public async Task<BuildRunResult> RunAsync(BuildStep step, string outputDir, Action<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            var exe = ResolveRunner(step.Runner);
            if (exe is null) return new BuildRunResult(step.Target, false, "", $"{step.Runner} 실행기를 찾을 수 없습니다(설치/PATH 확인).");
            // pwsh 는 -File 로 스크립트 실행, bash 는 스크립트를 첫 인자로 전달.
            IEnumerable<string> args = step.Runner == "bash" ? step.Args : new[] { "-File" }.Concat(step.Args);
            var res = await ProcessRunner.RunAsync(exe, args, workingDir: null, onLine: (_, line) => log?.Invoke(line), ct: ct).ConfigureAwait(false);
            var outPath = Path.Combine(outputDir, step.OutputName);
            return new BuildRunResult(step.Target, res.Ok, outPath, res.Ok ? "패키징 성공" : $"실패(exit {res.ExitCode})");
        }
        catch (OperationCanceledException) { return new BuildRunResult(step.Target, false, "", "취소됨"); }
        catch (Exception ex) { Program.Log("DesktopBuildLauncher.Run", ex); return new BuildRunResult(step.Target, false, "", ex.Message); }
    }

    private static string? ResolveRunner(string runner)
    {
        try
        {
            if (runner == "bash")
                return CliPathResolver.GetExecutablePath("bash", "bash.exe");
            return CliPathResolver.GetExecutablePath("pwsh", "pwsh.exe")
                ?? CliPathResolver.GetExecutablePath("powershell", "powershell.exe");
        }
        catch { return null; }
    }
}
