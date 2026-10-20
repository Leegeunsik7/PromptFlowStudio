using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PromptFlowStudio.Services.SelfFix;

public enum RepairPhase : byte { Idle, Snapshot, Applying, Building, Testing, Restoring, Committed, RolledBack, Failed }

/// <summary>외부 명령 결과(dotnet build/test).</summary>
public readonly record struct CommandResult(int ExitCode, string Output, double Ms, bool TimedOut);

/// <summary>폐루프 1회 결과.</summary>
public sealed class RepairReport
{
    public int Attempts, PatchesPlanned, PatchesCommitted, FilesCommitted, Conflicts, RestoredFiles;
    public bool Committed, BuildOk, TestsOk;
    public int TestsPassed = -1, TestsFailed = -1;
    public readonly List<string> ExcludedFiles = new();
    public readonly List<string> Log = new();
    public string? SafePointDigest;
    public double Ms;
}

/// <summary>
/// [PromptFlow Studio v16.93.0] 🔁 폐루프 회귀 검증 &amp; 자율 소스 복구 가드.
///   ▸ 안전 지점: 대상 파일 원본 바이트 + SHA-256 스냅샷(BOM/개행 보존 · 메모리 보관).
///   ▸ 적용 → <c>dotnet build -c Release -warnaserror</c> → (선택) <c>dotnet test -c Release</c> — 종료 코드 판정 · 타임아웃 시 프로세스 트리 종료.
///   ▸ 실패 시 즉시 복원(현재 해시 = 적용 해시일 때만 덮어씀 → 외부 편집 보호 = 충돌 집계) 후 안전 재수정:
///     빌드 오류 파일(경로(줄,열): error) 귀속 → 해당 파일 제외 재시도 · 귀속 불가/테스트 실패 → 이분(전반부만) 재시도 · 최대 시도 수.
///   ▸ 커밋: 성공 집합 유지 + 안전 지점 다이제스트 갱신.
/// </summary>
public sealed class ClosedLoopRegressionSelfRepairGuard
{
    private static readonly Regex ErrRx = new(@"^\s*(?<f>[^\r\n(]+?\.(?:cs|razor))\(\d+,\d+\):\s*error\b", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex TestRx = new(@"(?:실패|Failed)\s*:\s*(?<f>\d+)\s*,\s*(?:통과|Passed)\s*:\s*(?<p>\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string BuildArgs { get; set; } = "build \"PromptFlowStudio/PromptFlowStudio.csproj\" -c Release -warnaserror -nologo";
    public string TestArgs { get; set; } = "test \"PromptFlowStudio.Tests\" -c Release -nologo";
    public bool RunTests { get; set; } = true;
    public int MaxAttempts { get; set; } = 3;
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(15);
    /// <summary>명령 실행기(인자, 작업 폴더) — 기본 dotnet 프로세스 · 테스트 주입 가능.</summary>
    public Func<string, string, CancellationToken, Task<CommandResult>> Execute { get; set; }

    public RepairPhase Phase { get; private set; }
    public int Attempt { get; private set; }
    public RepairReport? Last { get; private set; }
    public string? LastOutputTail { get; private set; }
    public event Action? OnChange;

    public ClosedLoopRegressionSelfRepairGuard() => Execute = RunDotnetAsync;

    /// <summary>폐루프: 스냅샷 → 적용 → 빌드 → 테스트 → (실패) 복원 + 안전 재수정 → 커밋/롤백.</summary>
    public async Task<RepairReport> RepairAsync(string repoRoot, string sourceRoot, IReadOnlyDictionary<string, List<SourcePatch>> patchesByFile, CancellationToken ct)
    {
        var rep = new RepairReport();
        var t0 = Stopwatch.GetTimestamp();
        Last = rep;
        foreach (var kv in patchesByFile) rep.PatchesPlanned += kv.Value.Count;

        Set(RepairPhase.Snapshot);
        var snap = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in patchesByFile)
        {
            var abs = Path.GetFullPath(Path.Combine(sourceRoot, kv.Key));
            var bytes = await File.ReadAllBytesAsync(abs, ct).ConfigureAwait(false);
            snap[kv.Key] = new Snapshot(abs, bytes, SHA256.HashData(bytes));
        }
        var active = new List<string>(patchesByFile.Keys);
        active.Sort(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (Attempt = 1; Attempt <= MaxAttempts && active.Count > 0; Attempt++)
            {
                ct.ThrowIfCancellationRequested();
                rep.Attempts = Attempt;
                Set(RepairPhase.Applying);
                var applied = 0;
                foreach (var f in active)
                {
                    var s = snap[f];
                    var (bom, text) = Decode(s.Bytes);
                    var (patched, n, conflicts) = AutonomousCodeDefectSelfFixEngine.ApplyToText(text, patchesByFile[f]);
                    rep.Conflicts += conflicts; applied += n;
                    var outBytes = Encode(bom, patched);
                    await File.WriteAllBytesAsync(s.Path, outBytes, ct).ConfigureAwait(false);
                    s.Applied = SHA256.HashData(outBytes);
                }
                rep.Log.Add("시도 " + Attempt + ": 파일 " + active.Count + " · 패치 " + applied);

                Set(RepairPhase.Building);
                var build = await Execute(BuildArgs, repoRoot, ct).ConfigureAwait(false);
                LastOutputTail = Tail(build.Output);
                rep.BuildOk = build.ExitCode == 0 && !build.TimedOut;
                if (!rep.BuildOk)
                {
                    var culprits = Culprits(build.Output, active, snap);
                    rep.Log.Add("빌드 실패(" + build.ExitCode + (build.TimedOut ? " · 타임아웃" : "") + ") · 귀속 " + culprits.Count);
                    await RestoreAsync(active, snap, rep, ct).ConfigureAwait(false);
                    Narrow(active, culprits, rep, snap);
                    continue;
                }
                if (RunTests)
                {
                    Set(RepairPhase.Testing);
                    var test = await Execute(TestArgs, repoRoot, ct).ConfigureAwait(false);
                    LastOutputTail = Tail(test.Output);
                    var m = TestRx.Match(test.Output);
                    if (m.Success) { rep.TestsFailed = int.Parse(m.Groups["f"].Value, System.Globalization.CultureInfo.InvariantCulture); rep.TestsPassed = int.Parse(m.Groups["p"].Value, System.Globalization.CultureInfo.InvariantCulture); }
                    rep.TestsOk = test.ExitCode == 0 && !test.TimedOut;
                    if (!rep.TestsOk)
                    {
                        rep.Log.Add("테스트 실패(" + test.ExitCode + ") · 실패 " + rep.TestsFailed);
                        await RestoreAsync(active, snap, rep, ct).ConfigureAwait(false);
                        Narrow(active, new List<string>(), rep, snap);
                        continue;
                    }
                }
                rep.Committed = true;
                rep.FilesCommitted = active.Count;
                foreach (var f in active) rep.PatchesCommitted += patchesByFile[f].Count;
                rep.SafePointDigest = Digest(active, snap);
                rep.Log.Add("커밋: 파일 " + active.Count + " · 패치 " + rep.PatchesCommitted);
                Set(RepairPhase.Committed);
                return rep;
            }
            rep.Log.Add("커밋 불가 — 원본 유지");
            Set(RepairPhase.RolledBack);
            return rep;
        }
        catch (Exception ex)
        {
            // 취소/예외 → 적용 중 파일 전부 복원 후 전파
            await RestoreAsync(active, snap, rep, CancellationToken.None).ConfigureAwait(false);
            rep.Log.Add("중단 복원: " + ex.GetType().Name);
            Set(RepairPhase.Failed);
            throw;
        }
        finally { rep.Ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds; }
    }

    /// <summary>안전 재수정 집합 축소: 귀속 파일 제외 · 귀속 불가 시 전반부만.</summary>
    private static void Narrow(List<string> active, List<string> culprits, RepairReport rep, Dictionary<string, Snapshot> snap)
    {
        for (var i = active.Count - 1; i >= 0; i--) if (snap[active[i]].Conflicted) { rep.ExcludedFiles.Add(active[i]); active.RemoveAt(i); } // 외부 변경 파일은 재적용 금지
        if (culprits.Count > 0)
        {
            foreach (var c in culprits) { active.Remove(c); rep.ExcludedFiles.Add(c); }
            return;
        }
        if (active.Count <= 1) { rep.ExcludedFiles.AddRange(active); active.Clear(); return; }
        var half = active.Count / 2;
        for (var i = active.Count - 1; i >= half; i--) { rep.ExcludedFiles.Add(active[i]); active.RemoveAt(i); }
    }

    private static List<string> Culprits(string output, List<string> active, Dictionary<string, Snapshot> snap)
    {
        var list = new List<string>();
        foreach (Match m in ErrRx.Matches(output))
        {
            var p = m.Groups["f"].Value.Trim();
            foreach (var f in active)
            {
                if (list.Contains(f)) continue;
                var abs = snap[f].Path;
                if (p.Equals(abs, StringComparison.OrdinalIgnoreCase) || p.Replace('\\', '/').EndsWith(f, StringComparison.OrdinalIgnoreCase)) list.Add(f);
            }
        }
        return list;
    }

    /// <summary>복원: 현재 해시 = 적용 해시일 때만 원본 바이트 기록(외부 편집 = 충돌 · 보존).</summary>
    private async Task RestoreAsync(List<string> files, Dictionary<string, Snapshot> snap, RepairReport rep, CancellationToken ct)
    {
        Set(RepairPhase.Restoring);
        foreach (var f in files)
        {
            var s = snap[f];
            if (s.Applied is null) continue;
            try
            {
                var cur = SHA256.HashData(await File.ReadAllBytesAsync(s.Path, ct).ConfigureAwait(false));
                if (!CryptographicOperations.FixedTimeEquals(cur, s.Applied)) { rep.Conflicts++; s.Conflicted = true; rep.Log.Add("복원 생략(외부 변경): " + f); continue; }
                await File.WriteAllBytesAsync(s.Path, s.Bytes, ct).ConfigureAwait(false);
                s.Applied = null; rep.RestoredFiles++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rep.Log.Add("복원 실패: " + f + " — " + ex.Message); PromptFlowStudio.Program.Log("SelfFix.Restore", ex); }
        }
    }

    private static string Digest(List<string> files, Dictionary<string, Snapshot> snap)
    {
        var sb = new StringBuilder();
        foreach (var f in files) sb.Append(f).Append('|').Append(Convert.ToHexString(snap[f].Applied ?? snap[f].Hash)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static (bool Bom, string Text) Decode(byte[] b)
    {
        var bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;
        return (bom, Encoding.UTF8.GetString(b, bom ? 3 : 0, b.Length - (bom ? 3 : 0)));
    }

    private static byte[] Encode(bool bom, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        if (!bom) return body;
        var o = new byte[body.Length + 3]; o[0] = 0xEF; o[1] = 0xBB; o[2] = 0xBF;
        Buffer.BlockCopy(body, 0, o, 3, body.Length);
        return o;
    }

    private static string Tail(string s) => s.Length > 4000 ? s[^4000..] : s;

    private void Set(RepairPhase p)
    {
        Phase = p;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SelfFix.Guard.OnChange", ex); }
    }

    /// <summary>기본 실행기: dotnet 프로세스(출력 상한 256 KB · 타임아웃/취소 시 프로세스 트리 종료).</summary>
    public async Task<CommandResult> RunDotnetAsync(string args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workDir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        var sb = new StringBuilder(8192);
        void On(object? _, DataReceivedEventArgs e) { if (e.Data is null) return; lock (sb) { if (sb.Length > 256 * 1024) sb.Remove(0, 64 * 1024); sb.Append(e.Data).Append('\n'); } }
        var t0 = Stopwatch.GetTimestamp();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += On; p.ErrorDataReceived += On;
        if (!p.StartCompat()) return new CommandResult(-1, "프로세스 미지원 플랫폼", 0, false);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        using var lim = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lim.CancelAfter(CommandTimeout);
        var timedOut = false;
        try { await p.WaitForExitAsync(lim.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.KillCompat(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { PromptFlowStudio.Program.Log("SelfFix.Kill", ex); }
            if (ct.IsCancellationRequested) throw;
            timedOut = true;
        }
        string output; lock (sb) output = sb.ToString();
        return new CommandResult(timedOut ? -1 : p.ExitCode, output, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, timedOut);
    }

    private sealed class Snapshot
    {
        public Snapshot(string path, byte[] bytes, byte[] hash) { Path = path; Bytes = bytes; Hash = hash; }
        public string Path { get; }
        public byte[] Bytes { get; }
        public byte[] Hash { get; }
        public byte[]? Applied;
        public bool Conflicted;
    }
}
