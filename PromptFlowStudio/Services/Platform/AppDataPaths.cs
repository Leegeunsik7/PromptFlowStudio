namespace PromptFlowStudio.Services.Platform;

/// <summary>
/// 로컬 앱 데이터 기준 경로 단일 해석점. Windows 의 <see cref="Environment.GetFolderPath(Environment.SpecialFolder)"/> 는
/// 프로세스 <c>LOCALAPPDATA</c> 환경변수를 무시(SHGetKnownFolderPath)하므로, 테스트 격리용 전용 오버라이드를 둔다.
/// </summary>
public static class AppDataPaths
{
    /// <summary>설정 시(공백 아님) <c>%LOCALAPPDATA%</c> 대신 사용할 기준 폴더 — 테스트 하네스 전용.</summary>
    public const string OverrideEnvVar = "PROMPTFLOW_LOCALAPPDATA";

    /// <summary><see cref="OverrideEnvVar"/> → 없으면 OS LocalApplicationData.</summary>
    public static string LocalAppData
    {
        get
        {
            var o = Environment.GetEnvironmentVariable(OverrideEnvVar);
            return string.IsNullOrWhiteSpace(o)
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : o.Trim();
        }
    }
}
