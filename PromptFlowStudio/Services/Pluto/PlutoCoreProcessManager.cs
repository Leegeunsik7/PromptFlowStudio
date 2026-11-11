using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🪐 PlutoCore 허브 상태 스냅샷 — 디렉터리 · 생존 · PID · 엔드포인트 · 프로브 지연.</summary>
public sealed record PlutoCoreStatus(bool DirectoryExists, bool IsRunning, int? ProcessId, string EndpointUrl, TimeSpan? Latency)
{
    /// <summary>탐지된 실행 파일(없으면 null → 기동 불가).</summary>
    public string? ExecutablePath { get; init; }
    /// <summary>이 스튜디오가 기동한 프로세스인지(<see cref="PlutoCoreProcessManager.StopAsync"/> 대상).</summary>
    public bool LaunchedByStudio { get; init; }
    public bool CanLaunch => DirectoryExists && !IsRunning && ExecutablePath is not null;

    public static PlutoCoreStatus Unknown(string endpoint) => new(false, false, null, endpoint, null);
}

/// <summary>
/// 🪐 PlutoCore 설정 — 환경변수: PFS_PLUTOCORE_DIR / PFS_PLUTOCORE_EXE / PFS_PLUTOCORE_PIPE / PFS_PLUTOCORE_HEALTH_URL(선택 · 루프백만) / PLUTOCORE_API_KEY(시크릿 · 직렬화 제외).
/// </summary>
public sealed class PlutoCoreOptions
{
    public const string DefaultRootDirectory = @"D:\PROGRAM\PlutoCore";
    public const string DefaultPipeName = "PlutoCore_SecurePipe";
    public const string DefaultProcessName = "PlutoCore";
    public const string AppName = "PromptFlowStudio";
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromMilliseconds(1500);
    public static readonly TimeSpan DefaultLaunchTimeout = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);

    public string RootDirectory { get; set; } = DefaultRootDirectory;
    public string? ExecutablePath { get; set; }
    public string PipeName { get; set; } = DefaultPipeName;
    public string ProcessName { get; set; } = DefaultProcessName;
    /// <summary>선택 HTTP 헬스 엔드포인트(예: http://127.0.0.1:8080/health) — 비루프백은 무시(프라이버시 게이트).</summary>
    public string? HealthUrl { get; set; }
    public string? ApiKey { get; set; }
    public TimeSpan ProbeTimeout { get; set; } = DefaultProbeTimeout;
    public TimeSpan LaunchTimeout { get; set; } = DefaultLaunchTimeout;
    public TimeSpan RequestTimeout { get; set; } = DefaultRequestTimeout;

    public string PipeEndpoint => "pipe://./" + PipeName;

    public static PlutoCoreOptions FromEnvironment()
    {
        static string? Env(string k) { var v = Environment.GetEnvironmentVariable(k); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
        return new PlutoCoreOptions
        {
            RootDirectory = Env("PFS_PLUTOCORE_DIR") ?? DefaultRootDirectory,
            ExecutablePath = Env("PFS_PLUTOCORE_EXE"),
            PipeName = Env("PFS_PLUTOCORE_PIPE") ?? DefaultPipeName,
            HealthUrl = Env("PFS_PLUTOCORE_HEALTH_URL"),
            ApiKey = Env("PLUTOCORE_API_KEY"),
        };
    }

    internal bool TryResolveHealthUri(out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(HealthUrl) || !Uri.TryCreate(HealthUrl, UriKind.Absolute, out var u)) return false;
        if ((u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) || !u.IsLoopback) return false;
        uri = u;
        return true;
    }
}

/// <summary>
/// 🪐 외부 독립 엔진 PlutoCore(D:\PROGRAM\PlutoCore · Named Pipe 허브) 수명 관리자.
///   ▸ 상태: 디렉터리 → 파이프 존재 프로브(WaitNamedPipe · 슬롯 비점유) → (선택) 루프백 HTTP 헬스 1.5초 → 프로세스 PID.
///   ▸ 기동: 실행 파일 탐지 → 콘솔 창 없는 분리 프로세스 → 8초 준비 대기. 중지: 스튜디오가 기동한 경우에만 CloseMainWindow → 3초 후 Kill.
///   ▸ 모니터: 5초 주기 폴링 · 상태 변화 시에만 <see cref="OnChanged"/>. 콜백/타이머 예외 격리 · 절대 throw 하지 않음(취소 제외).
/// 비 Windows(모바일 링크) → 디렉터리/파이프 미존재 스텁.
/// </summary>
public sealed class PlutoCoreProcessManager : IDisposable
{
    public static readonly TimeSpan DefaultMonitorInterval = TimeSpan.FromSeconds(5);
    private static readonly string[] s_relativeExeCandidates =
    {
        "PlutoCore.exe",
        @"PlutoCore\PlutoCore.exe",
        @"publish\PlutoCore.exe",
        @"PlutoCore\bin\Release\net8.0-windows\publish\PlutoCore.exe",
        @"PlutoCore\bin\Release\net8.0-windows\PlutoCore.exe",
        @"PlutoCore\bin\Debug\net8.0-windows\PlutoCore.exe",
    };

    private static readonly char[] s_pathSeparators = { '\\', '/' };
    private readonly PlutoCoreOptions _options;
    private readonly HttpClient? _http;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _launchGate = new(1, 1);
    private System.Threading.Timer? _timer;
    private Process? _launched;
    private PlutoCoreStatus _current;
    private int _ticking;
    private bool _disposed;

    public event Action? OnChanged;

    public PlutoCoreProcessManager(PlutoCoreOptions? options = null, HttpClient? http = null)
    {
        _options = options ?? PlutoCoreOptions.FromEnvironment();
        _http = http;
        _current = PlutoCoreStatus.Unknown(_options.PipeEndpoint);
    }

    public PlutoCoreOptions Options => _options;
    public string RootDirectory => _options.RootDirectory;
    public PlutoCoreStatus Current { get { lock (_lock) return _current; } }
    public bool IsRunning => Current.IsRunning;
    public bool IsMonitoring { get { lock (_lock) return _timer is not null; } }

    /// <summary>🔍 실행 파일 탐지 — 명시 경로 → 루트 하위 후보(배포 → Release → Debug). 없으면 null.</summary>
    public string? FindExecutable()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.ExecutablePath) && File.Exists(_options.ExecutablePath)) return _options.ExecutablePath;
            var root = _options.RootDirectory;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
            foreach (var rel in s_relativeExeCandidates)
            {
                var p = Path.Combine(root, rel);
                if (File.Exists(p)) return p;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { Log(ex); }
        return null;
    }

    /// <summary>🩺 상태 점검 — 파이프/HTTP 프로브(1.5초) · 예외 없음(취소만 전파).</summary>
    public async Task<PlutoCoreStatus> CheckStatusAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bool dirExists;
        try { dirExists = !string.IsNullOrWhiteSpace(_options.RootDirectory) && Directory.Exists(_options.RootDirectory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { dirExists = false; }

        var endpoint = _options.PipeEndpoint;
        TimeSpan? latency = null;
        var sw = Stopwatch.StartNew();
        var running = IsPipePresent(_options.PipeName);
        if (running) latency = sw.Elapsed;
        else if (_options.TryResolveHealthUri(out var health))
        {
            sw.Restart();
            if (await ProbeHttpAsync(health, ct).ConfigureAwait(false))
            {
                running = true;
                latency = sw.Elapsed;
                endpoint = health.ToString();
            }
        }

        int? pid = running ? FindProcessId() : null;
        var launched = false;
        lock (_lock)
        {
            if (_launched is { } lp)
            {
                try
                {
                    if (lp.HasExited) { lp.Dispose(); _launched = null; }
                    else { launched = true; pid ??= lp.Id; }
                }
                catch (InvalidOperationException) { _launched = null; }
            }
        }
        var status = new PlutoCoreStatus(dirExists, running, pid, endpoint, latency)
        {
            ExecutablePath = dirExists || !string.IsNullOrWhiteSpace(_options.ExecutablePath) ? FindExecutable() : null,
            LaunchedByStudio = launched,
        };
        Publish(status);
        return status;
    }

    /// <summary>🚀 기동 — 이미 실행 중이면 true · 실행 파일 없음/비 Windows → false · 창 없는 분리 프로세스 후 준비 대기(최대 8초).</summary>
    public async Task<bool> TryLaunchAsync(CancellationToken ct = default)
    {
        if (_disposed) return false;
        await _launchGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var status = await CheckStatusAsync(ct).ConfigureAwait(false);
            if (status.IsRunning) return true;
            if (!OperatingSystem.IsWindows()) return false;
            var exe = status.ExecutablePath ?? FindExecutable();
            if (exe is null) return false;

            Process? p;
            try
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? _options.RootDirectory,
                };
                p = Process.Start(psi);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
            {
                Log(ex);
                return false;
            }
            if (p is null) return false;
            lock (_lock) { _launched?.Dispose(); _launched = p; }

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < _options.LaunchTimeout)
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
                bool exited;
                try { exited = p.HasExited; } catch (InvalidOperationException) { exited = true; }
                if ((await CheckStatusAsync(ct).ConfigureAwait(false)).IsRunning) return true;
                if (exited) return false;   // 단일 인스턴스 뮤텍스 충돌 · 즉시 종료
            }
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log(ex); return false; }
        finally { _launchGate.Release(); }
    }

    /// <summary>⏹ 스튜디오가 기동한 프로세스만 정상 종료(CloseMainWindow → 3초 → Kill). 외부 기동 인스턴스는 건드리지 않음.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        Process? p;
        lock (_lock) { p = _launched; _launched = null; }
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                try { p.CloseMainWindow(); } catch (InvalidOperationException) { }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { Log(ex); }
                }
            }
        }
        catch (InvalidOperationException) { }
        finally { p.Dispose(); }
        try { await CheckStatusAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    /// <summary>🟢 주기 헬스체크 시작(멱등).</summary>
    public void StartMonitoring(TimeSpan? interval = null)
    {
        lock (_lock)
        {
            if (_disposed || _timer is not null) return;
            var iv = interval is { } v && v > TimeSpan.Zero ? v : DefaultMonitorInterval;
            _timer = new System.Threading.Timer(static s => ((PlutoCoreProcessManager)s!).Tick(), this, TimeSpan.Zero, iv);
        }
    }

    public void StopMonitoring()
    {
        System.Threading.Timer? t;
        lock (_lock) { t = _timer; _timer = null; }
        t?.Dispose();
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        _ = TickCoreAsync();   // async void 금지 — 관측 Task 로 격리
    }

    private async Task TickCoreAsync()
    {
        try { await CheckStatusAsync().ConfigureAwait(false); }
        catch (Exception ex) { Log(ex); }   // 타이머 콜백 예외 격리
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private void Publish(PlutoCoreStatus status)
    {
        bool changed;
        lock (_lock)
        {
            var old = _current;
            changed = old.IsRunning != status.IsRunning || old.DirectoryExists != status.DirectoryExists || old.ProcessId != status.ProcessId
                      || old.LaunchedByStudio != status.LaunchedByStudio || !string.Equals(old.ExecutablePath, status.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                      || !string.Equals(old.EndpointUrl, status.EndpointUrl, StringComparison.Ordinal);
            _current = status;
        }
        if (!changed) return;
        try { OnChanged?.Invoke(); }
        catch (Exception ex) { Log(ex); }
    }

    private async Task<bool> ProbeHttpAsync(Uri uri, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_options.ProbeTimeout);
        var owned = _http is null ? new HttpClient { Timeout = Timeout.InfiniteTimeSpan } : null;
        try
        {
            using var resp = await (_http ?? owned!).GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException) { return false; }
        finally { owned?.Dispose(); }
    }

    private int? FindProcessId()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(_options.ProcessName)) return null;
        Process[] ps;
        try { ps = Process.GetProcessesByName(_options.ProcessName); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception) { return null; }
        int? pid = null;
        for (var i = 0; i < ps.Length; i++)
        {
            if (pid is null) { try { pid = ps[i].Id; } catch (InvalidOperationException) { } }
            ps[i].Dispose();
        }
        return pid;
    }

    /// <summary>🔌 파이프 존재 여부 — WaitNamedPipe(1ms): 연결하지 않으므로 허브 슬롯 비점유. 바쁨(세마포 타임아웃)도 생존으로 판정.</summary>
    public static bool IsPipePresent(string pipeName)
    {
        if (string.IsNullOrWhiteSpace(pipeName) || pipeName.IndexOfAny(s_pathSeparators) >= 0) return false;
#if WINDOWS
        if (WaitNamedPipe(@"\\.\pipe\" + pipeName, 1)) return true;
        var err = Marshal.GetLastWin32Error();
        return err == ErrorSemTimeout || err == ErrorPipeBusy;
#else
        return false;   // 비 Windows 스텁
#endif
    }

#if WINDOWS
    private const int ErrorSemTimeout = 121, ErrorPipeBusy = 231;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WaitNamedPipeW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipe(string name, uint timeoutMs);
#endif

    private static void Log(Exception ex) => Debug.WriteLine("[PlutoCore] " + ex.Message);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        StopMonitoring();
        OnChanged = null;
        Process? p;
        lock (_lock) { p = _launched; _launched = null; }
        p?.Dispose();   // 핸들만 해제 — 허브는 타 앱과 공유되므로 프로세스는 유지
        _launchGate.Dispose();
    }
}
