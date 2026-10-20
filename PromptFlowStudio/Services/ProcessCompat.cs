using System.Diagnostics;
using System.Runtime.Versioning;

namespace PromptFlowStudio.Services;

/// <summary>
/// [v17.63.2] 외부 프로세스 플랫폼 격리 시임 — iOS/MacCatalyst/tvOS 는 <see cref="Process"/> 기동이 불가(PlatformNotSupportedException).
///   Services 는 모바일 source-link 이므로 직접 호출 대신 이 시임을 경유한다.
///   미지원 플랫폼: <see cref="StartCompat(Process)"/> = false(호출부 기존 실패 폴백 경로로 합류) · <see cref="KillCompat"/> = no-op.
///   데스크톱/Android 는 원 API 그대로 위임(동작 불변 · 할당 0).
/// </summary>
public static class ProcessCompat
{
    /// <summary>현 OS 에서 외부 프로세스 기동/제어(및 동적 어셈블리 로드·FileSystemWatcher)가 가능한가.</summary>
    [UnsupportedOSPlatformGuard("ios")]
    [UnsupportedOSPlatformGuard("maccatalyst")]
    [UnsupportedOSPlatformGuard("tvos")]
    [UnsupportedOSPlatformGuard("browser")]
    public static bool IsSupported =>
        !(OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsTvOS() || OperatingSystem.IsBrowser());

    /// <summary><see cref="Process.Start()"/> 위임 — 미지원 플랫폼은 false.</summary>
    public static bool StartCompat(this Process p) => IsSupported && p.Start();

    /// <summary><see cref="Process.Start(ProcessStartInfo)"/> 위임 — 미지원 플랫폼은 null.</summary>
    public static Process? Start(ProcessStartInfo psi) => IsSupported ? Process.Start(psi) : null;

    /// <summary><see cref="Process.Kill(bool)"/> 위임 — 미지원 플랫폼은 no-op(기동된 프로세스가 존재할 수 없음).</summary>
    public static void KillCompat(this Process p, bool entireProcessTree = false)
    {
        if (IsSupported) p.Kill(entireProcessTree);
    }

    /// <summary><see cref="Process.TotalProcessorTime"/> 위임 — 미지원 플랫폼은 0.</summary>
    public static TimeSpan TotalProcessorTimeCompat(this Process p) => IsSupported ? p.TotalProcessorTime : TimeSpan.Zero;

    /// <summary><see cref="Process.StartTime"/> 위임 — 미지원 플랫폼은 현재 시각.</summary>
    public static DateTime StartTimeCompat(this Process p) => IsSupported ? p.StartTime : DateTime.Now;

    /// <summary><see cref="Process.GetProcessesByName(string)"/> 위임 — 미지원 플랫폼은 빈 배열.</summary>
    public static Process[] GetProcessesByName(string name) => IsSupported ? Process.GetProcessesByName(name) : Array.Empty<Process>();
}
