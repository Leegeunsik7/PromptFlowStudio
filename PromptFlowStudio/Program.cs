using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;

namespace PromptFlowStudio;

internal static class Program
{
    /// <summary>
    /// 셸 로그 위치. 설치 폴더가 아니라 사용자 프로필에 둔다
    /// (Program Files 는 쓰기 권한이 없다).
    /// </summary>
    private static string LogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PromptFlowStudio", "logs", "shell.log");

    [STAThread]
    private static void Main(string[] args)
    {
        // ⚠ 예외를 반드시 파일에 남긴다 — 데스크톱 앱이 메시지 없이 사라지면
        //   현장에서 원인을 알 방법이 없다. 이 로그가 유일한 단서가 된다.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("AppDomain.UnhandledException", e.ExceptionObject as Exception);

        Application.ThreadException += (_, e) =>
        {
            Log("Application.ThreadException", e.Exception);
            ShowCrash(e.Exception);
        };
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        // v16.0.0 Titan: CI/CD 서명 모드 — UI 없이 퍼블리시 산출물에 RSA 매니페스트를 기록하고 종료 코드로 결과 보고.
        if (args.Length == 2 && args[0] == Services.Security.AppIntegrityGuard.SignCommand)
        {
            Environment.ExitCode = Services.Security.AppIntegrityGuard.RunSignCommand(args[1], out var signMessage);
            Console.Out.WriteLine(signMessage);
            Log("서명 모드: " + signMessage, null);
            return;
        }
        // v16.3.0: 릴리스 검인 모드 — 내장 공개키로 매니페스트·바이너리 검증 결과를 종료 코드로 보고(UI 없음).
        if (args.Length == 2 && args[0] == Services.Security.AppIntegrityGuard.VerifyCommand)
        {
            Environment.ExitCode = Services.Security.AppIntegrityGuard.RunVerifyCommand(args[1], out var verifyMessage);
            Console.Out.WriteLine(verifyMessage);
            return;
        }

        // v18.3.0: 배포 번들 SHA-256 무결성 매니페스트 기록 모드(release-checksums.sha256 · UI 없음).
        if (args.Length == 2 && args[0] == Services.Deploy.ReleaseBundleChecksumGenerator.Command)
        {
            Environment.ExitCode = Services.Deploy.ReleaseBundleChecksumGenerator.RunCommand(args[1], out var sumMessage);
            Console.Out.WriteLine(sumMessage);
            return;
        }

        ApplicationConfiguration.Initialize();

        // v18.3.0: 헤드리스 스모크 — DI 전수 해석 + 32 워크스페이스 확인 후 윈도우 없이 종료 코드로 보고.
        if (args.Length >= 1 && args[0] == SmokeTestBootstrap.Command)
        {
            var smokeCode = SmokeTestBootstrap.Run(args.Length == 2 && args[1] == SmokeTestBootstrap.DeepOption, out var smokeMessage);
            Console.Out.WriteLine(smokeMessage);
            Console.Out.Flush();
            if (smokeCode != 0) Log("스모크 실패: " + smokeMessage, null);
            Environment.Exit(smokeCode);
        }

        // v15.17.0 LTS: 바이너리 자체 무결성 검증 — 변조 감지 시 UI/서비스 기동 전에 안전 종료.
        var integrity = Services.Security.AppIntegrityGuard.VerifyInstalled();
        if (integrity.ShouldShutdown)
        {
            Log("무결성 위반: " + integrity.FirstFailure + " (" + integrity.Failed + "/" + integrity.Checked + ")", null);
            MessageBox.Show(
                "프로그램 파일이 변조되었거나 손상되었습니다. 정식 배포본으로 다시 설치하세요." + Environment.NewLine + Environment.NewLine
                    + "자세한 내용: " + LogPath,
                "PromptFlow Studio",
                MessageBoxButtons.OK,
                MessageBoxIcon.Stop);
            Environment.ExitCode = 3;
            return;
        }
        if (integrity.Verdict == Services.Security.IntegrityVerdict.KeyUnavailable)
            Log("무결성 검증 생략: 내장 공개키 손상", null);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .Build();

        try
        {
            Log("시작", null);
            Application.Run(new MainForm(config));
            Log("정상 종료", null);
        }
        catch (Exception ex)
        {
            Log("Main", ex);
            ShowCrash(ex);
        }
    }

    private static void ShowCrash(Exception ex)
    {
        MessageBox.Show(
            "오류가 발생했습니다." + Environment.NewLine + Environment.NewLine
                + ex.Message + Environment.NewLine + Environment.NewLine
                + "자세한 내용: " + LogPath,
            "PromptFlow Studio",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    private static readonly object LogLock = new();

    // v14.0.1: 로그 홍수 가드 - 같은 태그·예외형·메시지는 60초에 1건만 기록(1초 주기 틱의 반복 예외가 1MB 상한을 채워 최초 원인 기록을 지우는 것 방지).
    private const long LogDedupWindowMs = 60_000;
    private static readonly System.Collections.Generic.Dictionary<string, (long AtMs, int Suppressed)> LogDedup = new(StringComparer.Ordinal);

    /// <summary>기록 여부 판정(LogLock 안에서 호출). 억제 구간이 끝나 다시 기록할 때 그동안 생략된 건수를 돌려준다.</summary>
    internal static bool ShouldEmitLog(System.Collections.Generic.Dictionary<string, (long AtMs, int Suppressed)> table, string key, long nowMs, out int suppressed)
    {
        suppressed = 0;
        if (table.TryGetValue(key, out var e))
        {
            if (nowMs - e.AtMs < LogDedupWindowMs) { table[key] = (e.AtMs, e.Suppressed + 1); return false; }
            suppressed = e.Suppressed;
        }
        else if (table.Count >= 256) table.Clear();   // 키 폭증 상한
        table[key] = (nowMs, 0);
        return true;
    }

    /// <summary>로그 기록. 로그를 쓰다 실패해도 앱을 죽이지 않는다.</summary>
    internal static void Log(string tag, Exception? ex)
    {
        try
        {
            var path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("  [").Append(tag).Append("]  ");

            if (ex is null) sb.AppendLine("-");
            else sb.AppendLine(ex.ToString());

            lock (LogLock)
            {
                if (ex is not null)
                {
                    if (!ShouldEmitLog(LogDedup, tag + "|" + ex.GetType().FullName + "|" + ex.Message, Environment.TickCount64, out var skipped)) return;
                    if (skipped > 0) sb.Append("  (직전 60초 동일 오류 ").Append(skipped).AppendLine("건 생략)");
                }

                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1024 * 1024)
                    File.WriteAllText(path, "(이전 로그는 크기 초과로 정리했습니다)\n", Encoding.UTF8);

                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 로그 실패는 삼킨다 — 진단 수단이 앱을 죽이면 안 된다.
        }
    }
}
