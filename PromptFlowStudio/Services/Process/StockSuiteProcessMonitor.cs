using System;
using System.IO;
#if WINDOWS
using System.Runtime.InteropServices;
#endif
using System.Threading;

namespace PromptFlowStudio.Services.ProcessBridge;

/// <summary>StockSuite 프로세스 스냅샷 — 미실행 = <see cref="StockSuiteProcessSnapshot.NotRunning"/>.</summary>
public readonly record struct StockSuiteProcessSnapshot(bool IsRunning, int Pid, long WorkingSetBytes, IntPtr MainWindowHandle)
{
    public static StockSuiteProcessSnapshot NotRunning => default;
    public double WorkingSetMb => WorkingSetBytes / (1024d * 1024d);
}

/// <summary>
/// [PromptFlowStudio] 🟢 StockSuite 라이브 프로세스 모니터 + 윈도우 브릿지.
///   ▸ 2초 주기 백그라운드 폴링(<see cref="System.Threading.Timer"/>) — 생존 여부 · PID · WorkingSet. 상태 변화 시에만 <see cref="OnChanged"/> 발화.
///   ▸ <see cref="BringStockSuiteToFront"/>: User32 <c>ShowWindow(SW_RESTORE)</c> + <c>SetForegroundWindow</c>(WINDOWS 전용 · 그 외 스텁 false).
///   ▸ 프로세스 조회 예외(권한/플랫폼 미지원/경합 종료)는 격리 → 미실행으로 간주.
/// </summary>
public sealed class StockSuiteProcessMonitor : IDisposable
{
    public const string DefaultProcessName = "StockSuite";
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2);

    private readonly Func<string?> _exePath;
    private readonly Func<string, StockSuiteProcessSnapshot> _probe;
    private readonly Func<IntPtr, bool> _activate;
    private readonly TimeSpan _interval;
    private readonly object _lock = new();
    private System.Threading.Timer? _timer;
    private int _ticking;
    private bool _disposed;
    private StockSuiteProcessSnapshot _current;

    /// <summary>상태 변화(실행/종료/PID/메모리 1MB 단위) 통지 — 백그라운드 스레드에서 호출됨.</summary>
    public event Action? OnChanged;

    /// <param name="exePathProvider">StockSuite.exe 경로 공급자(프로세스 이름 유도 · null/빈 값 = <see cref="DefaultProcessName"/>).</param>
    /// <param name="probe">프로세스 조회기(테스트 주입 · null = <see cref="System.Diagnostics.Process.GetProcessesByName(string)"/>).</param>
    /// <param name="activator">창 활성화기(테스트 주입 · null = User32 P/Invoke).</param>
    /// <param name="interval">폴링 주기(null = 2초).</param>
    public StockSuiteProcessMonitor(Func<string?>? exePathProvider = null, Func<string, StockSuiteProcessSnapshot>? probe = null, Func<IntPtr, bool>? activator = null, TimeSpan? interval = null)
    {
        _exePath = exePathProvider ?? (static () => null);
        _probe = probe ?? DefaultProbe;
        _activate = activator ?? ActivateWindow;
        _interval = interval is { } i && i > TimeSpan.Zero ? i : DefaultInterval;
    }

    public StockSuiteProcessSnapshot Current { get { lock (_lock) return _current; } }
    public bool IsRunning => Current.IsRunning;
    public bool IsMonitoring { get { lock (_lock) return _timer is not null; } }

    /// <summary>경로 → 프로세스 이름(확장자 제거 · 부재/오류 시 기본값).</summary>
    public static string ResolveProcessName(string? exePath)
    {
        var p = (exePath ?? "").Trim().Trim('"');
        if (p.Length == 0) return DefaultProcessName;
        try
        {
            var n = Path.GetFileNameWithoutExtension(p);
            return string.IsNullOrWhiteSpace(n) ? DefaultProcessName : n;
        }
        catch (ArgumentException) { return DefaultProcessName; }
    }

    public string ProcessName
    {
        get
        {
            try { return ResolveProcessName(_exePath()); }
            catch (Exception ex) { Program.Log("StockSuiteMon.Path", ex); return DefaultProcessName; }
        }
    }

    /// <summary>2초 주기 폴링 시작(멱등). 첫 틱 즉시 실행.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _timer is not null) return;
            _timer = new System.Threading.Timer(static s => ((StockSuiteProcessMonitor)s!).Tick(), this, TimeSpan.Zero, _interval);
        }
    }

    public void Stop()
    {
        System.Threading.Timer? t;
        lock (_lock) { t = _timer; _timer = null; }
        t?.Dispose();
    }

    /// <summary>즉시 1회 폴링 — 변화 시 true(+<see cref="OnChanged"/>). 절대 throw 하지 않음.</summary>
    public bool Refresh()
    {
        StockSuiteProcessSnapshot snap;
        try { snap = _probe(ProcessName); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.Log("StockSuiteMon.Probe", ex);
            snap = StockSuiteProcessSnapshot.NotRunning;
        }
        bool changed;
        lock (_lock)
        {
            if (_disposed) return false;
            changed = snap.IsRunning != _current.IsRunning || snap.Pid != _current.Pid
                || (snap.WorkingSetBytes >> 20) != (_current.WorkingSetBytes >> 20);
            _current = snap;
        }
        if (changed)
        {
            try { OnChanged?.Invoke(); }
            catch (Exception ex) { Program.Log("StockSuiteMon.Notify", ex); }
        }
        return changed;
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;   // 재진입 차단(느린 프로세스 열거)
        try { Refresh(); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    /// <summary>
    /// 🪟 실행 중인 StockSuite 창을 복원 + 최상단 활성화. 미실행/핸들 없음/비 Windows → false. 절대 throw 하지 않음.
    /// </summary>
    public bool BringStockSuiteToFront()
    {
        Refresh();
        var s = Current;
        if (!s.IsRunning || s.MainWindowHandle == IntPtr.Zero) return false;
        try { return _activate(s.MainWindowHandle); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Program.Log("StockSuiteMon.Front", ex);
            return false;
        }
    }

    private static StockSuiteProcessSnapshot DefaultProbe(string processName)
    {
        System.Diagnostics.Process[] ps;
        try { ps = System.Diagnostics.Process.GetProcessesByName(processName); }
        catch (Exception ex) when (ex is PlatformNotSupportedException or InvalidOperationException or NotSupportedException) { return StockSuiteProcessSnapshot.NotRunning; }
        var best = StockSuiteProcessSnapshot.NotRunning;
        foreach (var p in ps)
        {
            try
            {
                if (p.HasExited) continue;
                p.Refresh();
                var h = p.MainWindowHandle;
                var snap = new StockSuiteProcessSnapshot(true, p.Id, p.WorkingSet64, h);
                // 창 핸들 보유 프로세스 우선(멀티 인스턴스/자식 프로세스)
                if (!best.IsRunning || (best.MainWindowHandle == IntPtr.Zero && h != IntPtr.Zero)) best = snap;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or PlatformNotSupportedException) { }
            finally { p.Dispose(); }
        }
        return best;
    }

#if WINDOWS
    private const int SW_RESTORE = 9;
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr hWnd);

    private static bool ActivateWindow(IntPtr hWnd)
    {
        if (!PromptFlowStudio.Services.Platform.NativePlatformGuard.IsWindows || hWnd == IntPtr.Zero) return false;   // 🛡️ 런타임 OS 가드(비 Windows = no-op)
        try
        {
            if (IsIconic(hWnd)) ShowWindow(hWnd, SW_RESTORE);
            return SetForegroundWindow(hWnd);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            Program.Log("StockSuiteMon.Activate", ex);
            return false;
        }
    }
#else
    private static bool ActivateWindow(IntPtr hWnd) => false;   // 비 Windows 스텁
#endif

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        OnChanged = null;
    }
}
