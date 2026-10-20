using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Engines;

/// <summary>외부 CLI 실행 결과. Started=false 면 프로세스조차 못 띄움(경로/권한).</summary>
public sealed record ProcessResult(bool Started, int ExitCode, string StdOut, string StdErr)
{
    public bool Ok => Started && ExitCode == 0;
}

/// <summary>
/// [PromptFlowStudio] 외부 오픈소스 AI 엔진 CLI 공용 실행기 — 순수 BCL <see cref="Process"/>(외부 NuGet 0).
///   ▸ stdout/stderr 를 <b>비동기로 동시 드레인</b>해 파이프 버퍼 가득참에 의한 데드락을 차단(BeginXxxReadLine + 라인 콜백).
///   ▸ 취소 시 프로세스 트리 kill. 인자는 <see cref="ProcessStartInfo.ArgumentList"/> — 공백 포함 경로/한국어 경로 따옴표 문제 원천 차단.
///   ▸ 도구 경로 해석(설정 경로 우선 → PATH), 설정 인자 템플릿({key} 치환)까지 한곳에 모아 4대 엔진 서비스가 공유한다.
/// </summary>
public static class ProcessRunner
{
    /// <summary>
    /// CLI 를 실행하고 종료까지 대기한다. <paramref name="onLine"/>(isErr, line) 로 각 출력 라인을 실시간 전달(진행률 % 파싱용).
    ///   예외/취소는 삼키고 결과 record 로 반환(호출부가 Graceful 처리).
    /// </summary>
    public static async Task<ProcessResult> RunAsync(
        string exe, IEnumerable<string> args, string? workingDir = null,
        Action<bool, string>? onLine = null, CancellationToken ct = default)
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(workingDir) && Directory.Exists(workingDir)) psi.WorkingDirectory = workingDir!;

        var so = new StringBuilder();
        var se = new StringBuilder();
        Process? proc = null;

        try
        {
            proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (so.Length < 200_000) so.AppendLine(e.Data);   // tail 캡(장시간 로그 누수 방지)
                onLine?.Invoke(false, e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                if (se.Length < 200_000) se.AppendLine(e.Data);
                onLine?.Invoke(true, e.Data);
            };

            if (!proc.Start()) return new(false, -1, "", "프로세스를 시작하지 못했습니다.");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            try { await proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                KillTree(proc);   // 취소 즉시 프로세스 트리 완전 종료(자식까지)
                return new(true, -2, so.ToString(), "취소됨");
            }

            return new(true, proc.ExitCode, so.ToString(), se.ToString());
        }
        catch (System.ComponentModel.Win32Exception ex) { return new(false, -1, "", "실행 불가: " + ex.Message); }
        catch (Exception ex) { Program.Log("ProcessRunner.Run", ex); return new(false, -1, "", ex.Message); }
        finally
        {
            // ⚠ 어떤 경로(취소·예외·조기반환)로 빠져나가도 살아있는 프로세스 트리를 남기지 않는다(Orphan 방지).
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 무시 */ }
            proc?.Dispose();
        }
    }

    /// <summary>프로세스와 그 자식 트리를 즉시 종료하고 실제 종료를 짧게 대기(자식 reap 보장).</summary>
    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            try { proc.WaitForExit(2000); } catch { /* 무시 */ }
        }
        catch { /* 이미 종료/권한 등 — 무시 */ }
    }

    /// <summary>도구 경로 결정 — 설정 경로(파일 존재) 우선 → PATH 스캔 → null(미설치).</summary>
    public static string? Resolve(string? configuredPath, params string[] pathExeNames)
    {
        try
        {
            var p = (configuredPath ?? "").Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(p))
            {
                if (File.Exists(p)) return p;
                // 설정이 이름만(예: "python")이면 PATH 에서 찾는다.
                var byName = OnPath(p);
                if (byName is not null) return byName;
                return null;   // 설정했지만 못 찾음
            }
            foreach (var name in pathExeNames)
            {
                var hit = OnPath(name);
                if (hit is not null) return hit;
            }
        }
        catch { /* 무시 → null */ }
        return null;
    }

    private static string? OnPath(string exe)
    {
        try
        {
            if (Path.IsPathRooted(exe)) return File.Exists(exe) ? exe : null;
            var withExe = OperatingSystem.IsWindows() && !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe + ".exe" : exe;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try
                {
                    var cand = Path.Combine(dir.Trim(), withExe);
                    if (File.Exists(cand)) return cand;
                    var cand2 = Path.Combine(dir.Trim(), exe);
                    if (File.Exists(cand2)) return cand2;
                }
                catch { /* 잘못된 PATH 항목 무시 */ }
            }
        }
        catch { /* 무시 */ }
        return null;
    }

    /// <summary>인자 템플릿의 {key} 를 값으로 치환(요소 단위 — 공백 포함 경로 안전). 빈 값 요소는 제거.</summary>
    public static List<string> Substitute(IEnumerable<string> args, IReadOnlyDictionary<string, string> vars)
    {
        var outp = new List<string>();
        foreach (var raw in args)
        {
            var a = raw;
            foreach (var kv in vars) a = a.Replace("{" + kv.Key + "}", kv.Value);
            if (a.Length > 0) outp.Add(a);
        }
        return outp;
    }
}
