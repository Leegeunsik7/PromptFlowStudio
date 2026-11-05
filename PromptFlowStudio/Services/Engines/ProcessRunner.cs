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

/// <summary>🛡️ 외부 프로세스 분류 — 장중 듀얼 트랙 게이트 라우팅 기준.</summary>
public enum ProcessCategory
{
    /// <summary>중량 인코딩(렌더·60fps 보간·4K 업스케일·VP9) — 장중 단일 동시 게이트(<see cref="ProcessRunner.EncoderGate"/>).</summary>
    HeavyEncoding = 0,
    /// <summary>경량 프로브(ffprobe·메타데이터·1프레임 추출) — 중량 게이트 우회 · 독립 단기 퍼밋(<see cref="ProcessRunner.ProbeGate"/>).</summary>
    LightweightProbe = 1,
    /// <summary>셸 외부 실행(브라우저·탐색기·외부 앱) — 게이트 없음.</summary>
    ShellExternal = 2,
}

/// <summary>
/// [PromptFlowStudio] 외부 오픈소스 AI 엔진 CLI 공용 실행기 — 순수 BCL <see cref="Process"/>(외부 NuGet 0).
///   ▸ stdout/stderr 를 <b>비동기로 동시 드레인</b>해 파이프 버퍼 가득참에 의한 데드락을 차단(BeginXxxReadLine + 라인 콜백).
///   ▸ 취소 시 프로세스 트리 kill. 인자는 <see cref="ProcessStartInfo.ArgumentList"/> — 공백 포함 경로/한국어 경로 따옴표 문제 원천 차단.
///   ▸ 도구 경로 해석(설정 경로 우선 → PATH), 설정 인자 템플릿({key} 치환)까지 한곳에 모아 4대 엔진 서비스가 공유한다.
/// </summary>
public static class ProcessRunner
{
    /// <summary>🛡️ 실행 직후 우선순위 결정 훅(exe → 우선순위 · null = 변경 없음) — <c>TradingSafetyGuardService.Attach</c> 결선.</summary>
    public static Func<string, ProcessPriorityClass?>? PriorityResolver { get; set; }

    /// <summary>🛡️ 동시 실행 게이트 훅(exe · 실행 본체 · 취소) — 장중 인코딩 1개 제한. null = 즉시 실행.</summary>
    public static Func<string, Func<CancellationToken, Task<ProcessResult>>, CancellationToken, Task<ProcessResult>>? EncoderGate { get; set; }

    /// <summary>🛡️ 경량 프로브 독립 퍼밋 훅(exe · 실행 본체 · 취소) — 중량 게이트와 분리. null = 즉시 실행.</summary>
    public static Func<string, Func<CancellationToken, Task<ProcessResult>>, CancellationToken, Task<ProcessResult>>? ProbeGate { get; set; }

    /// <summary>
    /// 🛡️ 호출 자동 분류 — ffprobe 계열 또는 단일 프레임 출력(<c>-frames:v 1</c> · <c>-vframes 1</c> · <c>-frames 1</c>) = 경량 프로브, 그 외 중량 인코딩.
    /// </summary>
    public static ProcessCategory Classify(string? exe, IReadOnlyList<string>? args)
    {
        if (!string.IsNullOrWhiteSpace(exe)
            && Path.GetFileNameWithoutExtension(exe.Trim().Trim('"')).StartsWith("ffprobe", StringComparison.OrdinalIgnoreCase))
            return ProcessCategory.LightweightProbe;
        if (args is null) return ProcessCategory.HeavyEncoding;
        for (int i = 0; i + 1 < args.Count; i++)
        {
            var a = args[i];
            if ((string.Equals(a, "-frames:v", StringComparison.Ordinal) || string.Equals(a, "-vframes", StringComparison.Ordinal) || string.Equals(a, "-frames", StringComparison.Ordinal))
                && string.Equals(args[i + 1], "1", StringComparison.Ordinal))
                return ProcessCategory.LightweightProbe;
        }
        return ProcessCategory.HeavyEncoding;
    }

    /// <summary>
    /// CLI 를 실행하고 종료까지 대기한다. <paramref name="onLine"/>(isErr, line) 로 각 출력 라인을 실시간 전달(진행률 % 파싱용).
    ///   예외/취소는 삼키고 결과 record 로 반환(호출부가 Graceful 처리).
    /// </summary>
    public static Task<ProcessResult> RunAsync(
        string exe, IEnumerable<string> args, string? workingDir = null,
        Action<bool, string>? onLine = null, CancellationToken ct = default)
        => RunWithEnvAsync(exe, args, null, workingDir, onLine, ct);

    /// <summary><see cref="RunAsync"/> + 자식 프로세스 전용 환경변수(부모 프로세스 환경 오염 없음).</summary>
    public static async Task<ProcessResult> RunWithEnvAsync(
        string exe, IEnumerable<string> args, IReadOnlyDictionary<string, string?>? env, string? workingDir = null,
        Action<bool, string>? onLine = null, CancellationToken ct = default)
    {
        var list = args as IReadOnlyList<string> ?? new List<string>(args);
        return await RunCategorizedAsync(exe, list, Classify(exe, list), env, workingDir, onLine, ct).ConfigureAwait(false);
    }

    /// <summary><see cref="RunAsync(string, IEnumerable{string}, string?, Action{bool, string}?, CancellationToken)"/> + 명시 분류(자동 분류 무시).</summary>
    public static Task<ProcessResult> RunAsync(
        string exe, IEnumerable<string> args, ProcessCategory category, string? workingDir = null,
        Action<bool, string>? onLine = null, CancellationToken ct = default)
        => RunCategorizedAsync(exe, args as IReadOnlyList<string> ?? new List<string>(args), category, null, workingDir, onLine, ct);

    private static async Task<ProcessResult> RunCategorizedAsync(
        string exe, IReadOnlyList<string> args, ProcessCategory category, IReadOnlyDictionary<string, string?>? env, string? workingDir,
        Action<bool, string>? onLine, CancellationToken ct)
    {
        var gate = ResolveGate(category);
        if (gate is null) return await RunCoreAsync(exe, args, workingDir, onLine, env, ct).ConfigureAwait(false);
        try { return await gate(exe, c => RunCoreAsync(exe, args, workingDir, onLine, env, c), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(false, -2, "", "취소됨"); }   // 슬롯 대기 중 취소
    }

    /// <summary>분류 → 게이트(중량 = <see cref="EncoderGate"/> · 경량 = <see cref="ProbeGate"/> · 셸 = 없음).</summary>
    private static Func<string, Func<CancellationToken, Task<ProcessResult>>, CancellationToken, Task<ProcessResult>>? ResolveGate(ProcessCategory category)
        => category switch
        {
            ProcessCategory.HeavyEncoding => EncoderGate,
            ProcessCategory.LightweightProbe => ProbeGate,
            _ => null,
        };

    private static async Task<ProcessResult> RunCoreAsync(
        string exe, IEnumerable<string> args, string? workingDir,
        Action<bool, string>? onLine, IReadOnlyDictionary<string, string?>? env, CancellationToken ct)
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
        if (env is not null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

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

            if (!proc.StartCompat()) return new(false, -1, "", "프로세스를 시작하지 못했습니다.");
            ApplyPriority(proc, exe);
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
            try { if (proc is not null && !proc.HasExited) proc.KillCompat(entireProcessTree: true); } catch { /* 무시 */ }
            proc?.Dispose();
        }
    }

    /// <summary>
    /// 🛡️ 바이너리 스트리밍 프로세스(rawvideo stdin/stdout 파이프) 시작 — 라인 기반 <see cref="RunAsync"/> 로 표현 불가한 경로용.
    ///   거버넌스 우선순위(<see cref="PriorityResolver"/>) 적용 · stderr 는 자동 드레인(파이프 데드락 차단). 시작 실패 null.
    ///   ⚠ 호출부가 Dispose/Kill 책임. 동시 실행 제한은 <see cref="RunGatedAsync"/> 로 감싼다.
    /// </summary>
    public static Process? StartStreaming(string exe, IEnumerable<string> args, bool redirectStdin, bool redirectStdout, CancellationToken ct = default)
        => StartStreaming(exe, args, redirectStdin, redirectStdout, null, ct);

    /// <summary><see cref="StartStreaming(string, IEnumerable{string}, bool, bool, CancellationToken)"/> + stderr 라인 콜백(진단 로그 tail 수집용 · 콜백 예외 격리).</summary>
    public static Process? StartStreaming(string exe, IEnumerable<string> args, bool redirectStdin, bool redirectStdout, Action<string>? onStderrLine, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = new Process { StartInfo = psi };
        try
        {
            if (!proc.StartCompat()) { proc.Dispose(); return null; }
        }
        catch (System.ComponentModel.Win32Exception ex) { Program.Log("ProcessRunner.Stream", ex); proc.Dispose(); return null; }
        ApplyPriority(proc, exe);
        _ = onStderrLine is null ? DrainAsync(proc.StandardError, ct) : DrainLinesAsync(proc.StandardError, onStderrLine, ct);
        return proc;
    }

    private static async Task DrainLinesAsync(StreamReader r, Action<string> onLine, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await r.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
            {
                try { onLine(line); } catch (Exception ex) { Program.Log("ProcessRunner.StderrLine", ex); }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { /* 프로세스 종료/취소 */ }
    }

    private static async Task DrainAsync(StreamReader r, CancellationToken ct)
    {
        try { _ = await r.ReadToEndAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException) { /* 프로세스 종료/취소 */ }
    }

    /// <summary>🛡️ 스트리밍 작업 본체를 장중 동시 실행 게이트(<see cref="EncoderGate"/>) 슬롯 안에서 실행 — 게이트 미설정 시 즉시 실행.</summary>
    public static Task<ProcessResult> RunGatedAsync(string exe, Func<CancellationToken, Task<ProcessResult>> body, CancellationToken ct = default)
        => RunGatedAsync(exe, ProcessCategory.HeavyEncoding, body, ct);

    /// <summary>🛡️ 분류별 게이트 실행 — 경량 프로브는 중량 슬롯을 기다리지 않고 독립 퍼밋(<see cref="ProbeGate"/>) 경유.</summary>
    public static async Task<ProcessResult> RunGatedAsync(string exe, ProcessCategory category, Func<CancellationToken, Task<ProcessResult>> body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var gate = ResolveGate(category);
        if (gate is null) return await body(ct).ConfigureAwait(false);
        return await gate(exe, body, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 셸 연결 프로그램으로 대상(URL·파일·폴더)을 연다(<c>UseShellExecute=true</c>) — 반환된 <see cref="Process"/> 핸들은 즉시 Dispose(핸들 누수 0).
    ///   실패(Win32Exception 등)는 로그 후 false.
    /// </summary>
    public static bool ShellOpen(string target) => LaunchExternal(target, "", openWithShell: true);

    /// <summary>
    /// 🚀 외부 대상(URL·파일·폴더·실행 파일) 실행 — 전 서비스 <c>Process.Start</c> 단일 관문(게이트 없음 · <see cref="ProcessCategory.ShellExternal"/>).
    ///   ▸ 검증: 빈 대상 / 제어문자(NUL·개행) / "://" 포함 시 http·https 외 스킴 / 루트 경로 미존재 → false.
    ///   ▸ 루트 파일 경로는 작업 디렉터리 = 파일 폴더. 핸들 즉시 Dispose. 예외(실행 파일 부재 등)는 로그 후 false — 절대 throw 하지 않음.
    /// </summary>
    public static bool LaunchExternal(string? target, string? arguments = "", bool openWithShell = true)
    {
        if (!ValidateExternalTarget(target, arguments) || !ProcessCompat.IsSupported) return false;
        var t = target!.Trim();
        try
        {
            var psi = new ProcessStartInfo(t) { UseShellExecute = openWithShell };
            if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;
            if (!openWithShell) psi.CreateNoWindow = true;
            if (Path.IsPathRooted(t) && File.Exists(t)) psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(t)) ?? "";
            using var p = Process.Start(psi);
            return p is not null || openWithShell;   // 셸 위임(기존 인스턴스 재사용) 시 null 이어도 성공
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException
                                       or FileNotFoundException or ObjectDisposedException or ArgumentException or UnauthorizedAccessException)
        {
            Program.Log("ProcessRunner.LaunchExternal", ex);
            return false;
        }
    }

    /// <summary><see cref="LaunchExternal"/> 비동기 래퍼(동기 완료 · 취소 선요청 시 false).</summary>
    public static Task<bool> LaunchExternalAsync(string? target, string? arguments = "", bool openWithShell = true, CancellationToken ct = default)
        => ct.IsCancellationRequested ? Task.FromResult(false) : Task.FromResult(LaunchExternal(target, arguments, openWithShell));

    /// <summary>외부 실행 대상/인자 안전 검증(순수 판정 · 실행 없음).</summary>
    public static bool ValidateExternalTarget(string? target, string? arguments = "")
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        var t = target.Trim();
        if (HasControlChar(t) || (arguments is not null && HasControlChar(arguments))) return false;
        if (t.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(t, UriKind.Absolute, out var u)) return false;
            return u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp;   // file:/javascript: 등 위조 스킴 차단
        }
        if (t.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
        try
        {
            if (Path.IsPathRooted(t)) return File.Exists(t) || Directory.Exists(t);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
        return true;   // PATH 해석 대상(explorer.exe 등)
    }

    private static bool HasControlChar(string s)
    {
        foreach (var c in s) if (c is '\0' or '\r' or '\n') return true;
        return false;
    }

    private static void ApplyPriority(Process proc, string exe)
    {
        var resolver = PriorityResolver;
        if (resolver is null) return;
        try
        {
            var p = resolver(exe);
            if (p is { } v && !proc.HasExited) proc.PriorityClass = v;
        }
        catch (Exception ex) { Program.Log("ProcessRunner.Priority", ex); }   // 우선순위 실패는 실행에 영향 없음
    }

    /// <summary>프로세스와 그 자식 트리를 즉시 종료하고 실제 종료를 짧게 대기(자식 reap 보장).</summary>
    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.KillCompat(entireProcessTree: true);
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
