using System.Runtime.CompilerServices;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 테스트 프로세스 저장소 격리 — 어떤 서비스 정적 상태보다 먼저 실행되어 앱 데이터 루트를
/// <c>%TEMP%\pfs_tests_{pid}_{guid}</c> 로 돌리고, 실 Drive 백업(G:\내 드라이브) 미러/볼트를 비활성화한다. 종료 시 정리.
/// </summary>
internal static class TestStorageIsolation
{
    internal static string Root { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Root = Path.Combine(Path.GetTempPath(), "pfs_tests_" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Environment.SetEnvironmentVariable(PromptFlowStudio.Services.Platform.AppDataPaths.OverrideEnvVar, Root);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Root);   // 자식 프로세스·비Windows 경로용(Windows GetFolderPath 는 무시)
        Environment.SetEnvironmentVariable(PromptFlowStudio.Services.Account.MemberRegistryService.MirrorEnvVar, "off");
        Environment.SetEnvironmentVariable(PromptFlowStudio.Services.Pluto.PlutoDriveStorageManager.DriveEnvVar, "off");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch { /* 잠긴 파일은 OS 임시폴더 정리에 위임 */ }
        };
    }
}
