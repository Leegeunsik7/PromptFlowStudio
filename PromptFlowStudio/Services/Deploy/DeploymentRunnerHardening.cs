using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Engines;   // CliPathResolver

namespace PromptFlowStudio.Services.Deploy;

/// <summary>배포 스크립트 셸 종류 — PowerShell(ps1) 또는 Bash(sh).</summary>
public enum ScriptShell { PowerShell, Bash }

/// <summary>외부 스크립트/프로세스 실행 결과 — 시작·종료코드·타임아웃·강제종료·표준출력/에러·사유.</summary>
public sealed record ScriptRunResult(bool Started, int ExitCode, bool TimedOut, bool Killed, string StdOut, string StdErr, string Message)
{
    /// <summary>정상 완료(시작됨 · 타임아웃 아님 · 종료코드 0).</summary>
    public bool Ok => Started && !TimedOut && ExitCode == 0;
    /// <summary>검증/시작 실패 표준 결과(프로세스 미기동).</summary>
    public static ScriptRunResult Rejected(string why) => new(false, -1, false, false, "", "", why);
}

/// <summary>
/// [PromptFlowStudio] v4.4.4 Task1 · 배포 스크립트 실행 보안 &amp; 좀비 프로세스 방어 — 순수 BCL <see cref="Process"/>(외부 NuGet 0).
///   ▸ <b>셸 이스케이프 방어</b>: 인자를 <see cref="ProcessStartInfo.ArgumentList"/> 로 전달(셸 미경유 → 메타문자 해석 원천 차단)하고,
///     제어문자(NUL/CR/LF) 주입을 거부한다. 부득이 커맨드 문자열을 구성할 때를 위한 <see cref="EscapePowerShellArgument"/>/<see cref="EscapeBashArgument"/> 도 제공.
///   ▸ <b>경로 이탈 차단</b>: 스크립트 경로를 <see cref="Path.GetFullPath(string)"/> 로 정규화해 허용 루트 밖(<c>..</c> 이탈)이면 거부(<see cref="IsWithinRoot"/>).
///     PowerShell 은 <c>-Command</c> 를 절대 쓰지 않고 <c>-File</c> 로만 실행(임의 코드 문자열 실행 차단).
///   ▸ <b>좀비 프로세스 방어</b>: <see cref="CancellationToken"/> + <see cref="TimeSpan"/>(기본 10분) 결합. 스크립트가 멈춰도 호스트 스레드는 영원히 대기하지 않고
///     <see cref="Process.Kill(bool)"/>(<c>entireProcessTree: true</c>)로 자식까지 확실히 종료(Win/맥/리눅스 공통).
/// </summary>
public sealed class DeploymentRunnerHardening
{
    /// <summary>기본 실행 타임아웃 — 이 시간 안에 끝나지 않으면 프로세스 트리를 강제 종료.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    // ══════════════════════ 셸 이스케이프 · 검증(순수 · 테스트 대상) ══════════════════════

    /// <summary>PowerShell 리터럴 이스케이프 — 작은따옴표로 감싸고 내부 <c>'</c> 를 <c>''</c> 로 이중화(모든 메타문자 무력화).</summary>
    public static string EscapePowerShellArgument(string? arg)
        => "'" + (arg ?? "").Replace("'", "''") + "'";

    /// <summary>POSIX 셸(Bash) 리터럴 이스케이프 — 작은따옴표로 감싸고 내부 <c>'</c> 를 <c>'\''</c> 로 치환.</summary>
    public static string EscapeBashArgument(string? arg)
        => "'" + (arg ?? "").Replace("'", "'\\''") + "'";

    /// <summary>인자/경로에 인젝션 위험 제어문자(NUL·CR·LF·기타 C0)가 있는지(탭은 허용).</summary>
    public static bool HasDangerousControlChars(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
            if (ch == '\0' || ch == '\r' || ch == '\n' || (ch < 0x20 && ch != '\t')) return true;
        return false;
    }

    /// <summary>정규화된 <paramref name="path"/> 가 <paramref name="root"/> 하위(또는 동일)인지 — <c>..</c> 경로 이탈을 차단(순수).</summary>
    public static bool IsWithinRoot(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var rootFull = Path.GetFullPath(root);
            var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var sep = Path.DirectorySeparatorChar;
            var rootNorm = rootFull.TrimEnd(sep) + sep;
            var fullNorm = full.TrimEnd(sep) + sep;
            return fullNorm.StartsWith(rootNorm, cmp);
        }
        catch { return false; }
    }

    // ══════════════════════ 스크립트 실행(검증 → 바운디드 실행) ══════════════════════

    /// <summary>
    /// PowerShell/Bash 스크립트를 <b>검증 후</b> 실행한다 — 확장자·존재·제어문자·경로 이탈을 막고 인자를 리터럴로 전달,
    /// <paramref name="timeout"/>(기본 10분) 초과 시 트리 강제 종료. 어떤 실패도 예외를 던지지 않고 결과 record 로 귀결.
    /// </summary>
    /// <param name="allowedRoot">지정 시 스크립트가 이 루트 밖이면 거부(경로 이탈 차단). null 이면 검사 생략.</param>
    public async Task<ScriptRunResult> RunScriptAsync(
        ScriptShell shell, string scriptPath, IEnumerable<string>? scriptArgs = null,
        string? allowedRoot = null, TimeSpan? timeout = null, Action<string>? log = null, CancellationToken ct = default)
    {
        // ── 스크립트 경로 검증 ──
        if (string.IsNullOrWhiteSpace(scriptPath)) return ScriptRunResult.Rejected("스크립트 경로가 비어 있습니다.");
        if (HasDangerousControlChars(scriptPath)) return ScriptRunResult.Rejected("스크립트 경로에 제어문자가 포함되어 거부합니다(인젝션 방어).");

        string fullScript;
        try { fullScript = Path.GetFullPath(scriptPath); } catch { return ScriptRunResult.Rejected("스크립트 경로가 올바르지 않습니다."); }
        if (!File.Exists(fullScript)) return ScriptRunResult.Rejected("스크립트 파일을 찾을 수 없습니다: " + fullScript);

        var expectedExt = shell == ScriptShell.PowerShell ? ".ps1" : ".sh";
        if (!fullScript.EndsWith(expectedExt, StringComparison.OrdinalIgnoreCase))
            return ScriptRunResult.Rejected($"{shell} 스크립트 확장자({expectedExt})가 아닙니다: {Path.GetFileName(fullScript)}");

        if (allowedRoot is not null && !IsWithinRoot(fullScript, allowedRoot))
            return ScriptRunResult.Rejected("스크립트가 허용 루트를 벗어났습니다(경로 이탈 차단): " + fullScript);

        // ── 인자 검증(제어문자 거부). ArgumentList 로 전달하므로 셸 해석은 애초에 없음(추가 방어층) ──
        var argList = (scriptArgs ?? Enumerable.Empty<string>()).ToList();
        if (argList.Any(HasDangerousControlChars))
            return ScriptRunResult.Rejected("스크립트 인자에 제어문자가 포함되어 거부합니다(인젝션 방어).");

        // ── 러너 해석 ──
        var exe = ResolveRunner(shell);
        if (exe is null) return ScriptRunResult.Rejected($"{shell} 실행기를 찾을 수 없습니다(설치/PATH 확인).");

        // ── 인자 구성 — PowerShell 은 -File(절대 -Command 아님)로 리터럴 실행, Bash 는 스크립트 경로 + 인자 ──
        var procArgs = new List<string>();
        if (shell == ScriptShell.PowerShell)
        {
            procArgs.Add("-NoProfile");
            procArgs.Add("-NonInteractive");
            procArgs.Add("-ExecutionPolicy"); procArgs.Add("Bypass");
            procArgs.Add("-File"); procArgs.Add(fullScript);
        }
        else procArgs.Add(fullScript);
        procArgs.AddRange(argList);

        log?.Invoke($"▶ {shell} 실행: {Path.GetFileName(fullScript)} (타임아웃 {Friendly(timeout ?? DefaultTimeout)})");
        return await RunProcessBoundedAsync(exe, procArgs, timeout, Path.GetDirectoryName(fullScript), log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 임의 외부 프로세스를 <see cref="TimeSpan"/> 상한 안에서 실행한다(좀비 방어의 핵심). 인자는 <see cref="ProcessStartInfo.ArgumentList"/> 로만 전달.
    /// 타임아웃/취소 시 <see cref="Process.Kill(bool)"/> 로 트리 전체를 종료하고 <see cref="ScriptRunResult.TimedOut"/>/<see cref="ScriptRunResult.Killed"/> 로 표기.
    /// </summary>
    public async Task<ScriptRunResult> RunProcessBoundedAsync(
        string exe, IEnumerable<string> args, TimeSpan? timeout = null,
        string? workingDir = null, Action<string>? log = null, CancellationToken ct = default)
    {
        var budget = timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,     // ⚠ 셸 미경유 — 메타문자 해석/인젝션 원천 차단
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);   // 공백/한글/메타문자 경로도 리터럴로 안전 전달
        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir)) psi.WorkingDirectory = workingDir!;

        var so = new StringBuilder();
        var se = new StringBuilder();
        Process? proc = null;
        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is null) return; if (so.Length < 400_000) so.AppendLine(e.Data); log?.Invoke(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is null) return; if (se.Length < 400_000) se.AppendLine(e.Data); log?.Invoke(e.Data); };

            if (!proc.Start()) return ScriptRunResult.Rejected("프로세스를 시작하지 못했습니다.");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(budget);   // ⏱ TimeSpan 타임아웃(기본 10분)

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var killed = KillTree(proc);   // ⚠ 좀비 방어 — 트리 전체(Kill(true)) 강제 종료
                var timedOut = !ct.IsCancellationRequested;
                var msg = timedOut
                    ? $"타임아웃({Friendly(budget)}) — 스크립트 무응답으로 프로세스 트리를 강제 종료했습니다."
                    : "취소됨 — 프로세스 트리를 종료했습니다.";
                log?.Invoke("⏹ " + msg);
                return new ScriptRunResult(true, -1, timedOut, killed, so.ToString(), se.ToString(), msg);
            }

            var code = proc.ExitCode;
            return new ScriptRunResult(true, code, false, false, so.ToString(), se.ToString(), code == 0 ? "성공" : $"실패(exit {code})");
        }
        catch (System.ComponentModel.Win32Exception ex) { return ScriptRunResult.Rejected("실행 불가: " + ex.Message); }
        catch (Exception ex) { Program.Log("DeploymentRunnerHardening.Run", ex); return ScriptRunResult.Rejected(ex.Message); }
        finally
        {
            // 어떤 경로(취소·예외·조기반환)로 나가도 살아있는 프로세스 트리를 남기지 않는다(Orphan/Zombie 0).
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 무시 */ }
            proc?.Dispose();
        }
    }

    // ══════════════════════ 내부 ══════════════════════

    private static bool KillTree(Process proc)
    {
        try
        {
            if (proc.HasExited) return false;
            proc.Kill(entireProcessTree: true);   // Win/맥/리눅스 공통 — 자식까지 확실히 종료
            try { proc.WaitForExit(3000); } catch { /* 무시 */ }
            return true;
        }
        catch { return false; }
    }

    private static string? ResolveRunner(ScriptShell shell)
    {
        try
        {
            return shell == ScriptShell.Bash
                ? CliPathResolver.GetExecutablePath("bash", "bash.exe")
                : CliPathResolver.GetExecutablePath("pwsh", "pwsh.exe")
                  ?? CliPathResolver.GetExecutablePath("powershell", "powershell.exe");
        }
        catch { return null; }
    }

    private static string Friendly(TimeSpan ts)
        => ts.TotalMinutes >= 1 ? $"{ts.TotalMinutes:0.#}분" : $"{ts.TotalSeconds:0.#}초";
}
