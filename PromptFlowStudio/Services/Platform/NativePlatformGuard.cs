using System;
using System.Threading;

namespace PromptFlowStudio.Services.Platform;

/// <summary>
/// 🛡️ Win32 P/Invoke 런타임 플랫폼 가드 — <c>#if WINDOWS</c> 컴파일 분기 + <see cref="OperatingSystem.IsWindows"/> 런타임 판정 이중화.
///   비 Windows(Android/macOS/Linux)에서는 네이티브 호출 전 false 반환 → <see cref="PlatformNotSupportedException"/>/<see cref="DllNotFoundException"/> 원천 차단.
///   <see cref="SimulateNonWindows"/> 는 AsyncLocal 범위 테스트 시임(병렬 테스트 간 누수 없음).
/// </summary>
public static class NativePlatformGuard
{
    private static readonly AsyncLocal<bool> _simulateNonWindows = new();

    /// <summary>Win32 네이티브 호출 허용 여부(Windows 이면서 비 Windows 시뮬레이션 아님).</summary>
    public static bool IsWindows => !_simulateNonWindows.Value && OperatingSystem.IsWindows();

    /// <summary>현재 비동기 흐름에서 비 Windows 로 가장(테스트 전용) — Dispose 시 복원.</summary>
    internal static IDisposable SimulateNonWindows()
    {
        var prev = _simulateNonWindows.Value;
        _simulateNonWindows.Value = true;
        return new Scope(prev);
    }

    private sealed class Scope(bool prev) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) _simulateNonWindows.Value = prev;
        }
    }
}
