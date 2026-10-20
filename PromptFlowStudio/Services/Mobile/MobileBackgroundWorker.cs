using System;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
#endif
#if IOS
using BackgroundTasks;
using Foundation;
#endif

namespace PromptFlowStudio.Services.Mobile;

/// <summary>백그라운드 스캔 틱 — 타임스탬프(ms)·누적 스캔 수·비고.</summary>
public sealed record BackgroundScanTick(long TimestampMs, int Count, string Note);

/// <summary>
/// [PromptFlowStudio] v5.2 Task4 · 백그라운드 상시 감시 시임 — 순수 BCL 루프 + 네이티브 라이프사이클(외부 NuGet 0).
///   ▸ 화면이 꺼진 슬립에서도 BLE/Modbus PLC 스캔·이상 감지 루프가 끊기지 않도록 Android <b>Foreground Service</b>(상시 알림) /
///     iOS <b>Background Tasks</b> 로 프로세스를 유지한다.
///   ▸ 데스크톱은 <see cref="DesktopMobileBackgroundWorker"/>(<see cref="System.Threading.PeriodicTimer"/> 인프로세스 루프 · 백그라운드 미지원).
///   ▸ 스캔 위임(<c>Func&lt;CancellationToken,Task&gt;</c>)은 MobileVisionAiEngine 등에 연결해 사용.
/// </summary>
public interface IMobileBackgroundWorker : IDisposable
{
    /// <summary>루프 실행 중.</summary>
    bool IsRunning { get; }
    /// <summary>앱 종료/슬립 후에도 감시 지속 가능한지(모바일 true · 데스크톱 false).</summary>
    bool IsBackgroundCapable { get; }
    /// <summary>스캔 틱 방송(관찰).</summary>
    event Action<BackgroundScanTick>? OnTick;
    /// <summary>주기 스캔 루프 시작(멱등). <paramref name="scan"/> 은 매 주기 호출.</summary>
    Task StartAsync(TimeSpan interval, Func<CancellationToken, Task> scan, CancellationToken ct = default);
    /// <summary>루프 정지 + 네이티브 서비스 해제(멱등).</summary>
    Task StopAsync();
}

/// <summary>공용 주기 루프(순수 BCL) — 네이티브 워커도 이 루프를 재사용한다.</summary>
internal static class BackgroundLoop
{
    public static async Task RunAsync(TimeSpan interval, Func<CancellationToken, Task> scan, Action<int> onTick, CancellationToken ct)
    {
        var iv = interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(5);
        using var timer = new PeriodicTimer(iv);
        var count = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await scan(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Program.Log("BgWorker.Scan", ex); }   // 스캔 예외가 루프를 끊지 않음
                onTick(++count);
            }
        }
        catch (OperationCanceledException) { /* 정상 종료 */ }
    }
}

/// <summary>데스크톱 폴백 — 인프로세스 PeriodicTimer 루프(앱 실행 중만 · 백그라운드 미지원).</summary>
public sealed class DesktopMobileBackgroundWorker : IMobileBackgroundWorker
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public bool IsRunning => _loop is { IsCompleted: false };
    public bool IsBackgroundCapable => false;
    public event Action<BackgroundScanTick>? OnTick;

    public Task StartAsync(TimeSpan interval, Func<CancellationToken, Task> scan, CancellationToken ct = default)
    {
        if (_disposed || IsRunning || scan is null) return Task.CompletedTask;   // 멱등 · null 방어
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _loop = BackgroundLoop.RunAsync(interval, scan,
            n => OnTick?.Invoke(new BackgroundScanTick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), n, "in-process")), token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _loop = null;
        _cts?.Dispose(); _cts = null;
    }

    public void Dispose()
    {
        if (_disposed) return;   // 멱등
        _disposed = true;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        OnTick = null;
    }
}

#if ANDROID
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI Android 헤드 전용] — 포그라운드 서비스로 프로세스를 유지하며 슬립에서도 스캔 루프 지속.
// ════════════════════════════════════════════════════════════════════════════

public sealed class AndroidMobileBackgroundWorker : IMobileBackgroundWorker
{
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public bool IsRunning => _loop is { IsCompleted: false };
    public bool IsBackgroundCapable => true;
    public event Action<BackgroundScanTick>? OnTick;

    public Task StartAsync(TimeSpan interval, Func<CancellationToken, Task> scan, CancellationToken ct = default)
    {
        if (_disposed || IsRunning || scan is null) return Task.CompletedTask;
        try
        {
            var ctx = Android.App.Application.Context;
            var intent = new Intent(ctx, typeof(PfsForegroundService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) ctx.StartForegroundService(intent);
            else ctx.StartService(intent);
        }
        catch (Exception ex) { Program.Log("BgWorker.StartFGS", ex); }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = BackgroundLoop.RunAsync(interval, scan,
            n => OnTick?.Invoke(new BackgroundScanTick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), n, "fgs")), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _loop = null; _cts?.Dispose(); _cts = null;
        try { var ctx = Android.App.Application.Context; ctx.StopService(new Intent(ctx, typeof(PfsForegroundService))); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        OnTick = null;
    }
}

/// <summary>상시 알림을 표시하는 포그라운드 서비스 — 프로세스를 살려 스캔 루프가 슬립에서도 지속되게 한다.</summary>
[Service(Exported = false, ForegroundServiceType = Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class PfsForegroundService : Service
{
    private const string ChannelId = "pfs_field_monitor";
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var mgr = (NotificationManager?)GetSystemService(NotificationService);
                var ch = new NotificationChannel(ChannelId, "현장 감시", NotificationImportance.Low);
                mgr?.CreateNotificationChannel(ch);
            }
            var notif = new NotificationCompat.Builder(this, ChannelId)
                .SetContentTitle("PromptFlow 현장 감시")
                .SetContentText("BLE/Modbus PLC 이상 감시 중…")
                .SetSmallIcon(Android.Resource.Drawable.IcMenuInfoDetails)
                .SetOngoing(true)
                .Build();
            StartForeground(1001, notif);
        }
        catch (Exception ex) { Program.Log("PfsForegroundService.Start", ex); }
        return StartCommandResult.Sticky;   // 시스템이 죽여도 재기동
    }
}
#endif

#if IOS
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI iOS 헤드 전용] — BGTaskScheduler 로 주기 갱신 태스크 등록(iOS 백그라운드 실행 제약 준수).
//  ⚠ Info.plist 에 UIBackgroundModes(processing/fetch) + BGTaskSchedulerPermittedIdentifiers 필요.
// ════════════════════════════════════════════════════════════════════════════

public sealed class IosMobileBackgroundWorker : IMobileBackgroundWorker
{
    private const string TaskId = "com.angtec.promptflowstudio.fieldscan";
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public bool IsRunning => _loop is { IsCompleted: false };
    public bool IsBackgroundCapable => true;
    public event Action<BackgroundScanTick>? OnTick;

    public Task StartAsync(TimeSpan interval, Func<CancellationToken, Task> scan, CancellationToken ct = default)
    {
        if (_disposed || IsRunning || scan is null) return Task.CompletedTask;
        try
        {
            // 백그라운드 처리 태스크 등록(앱 시작 시 1회여야 하나, 멱등 등록 시도).
            BGTaskScheduler.Shared.Register(TaskId, null, task =>
            {
                var bg = (BGProcessingTask)task;
                _ = scan(CancellationToken.None).ContinueWith(_ => bg.SetTaskCompleted(false));
                ScheduleNext();
            });
            ScheduleNext();
        }
        catch (Exception ex) { Program.Log("BgWorker.iOS.Register", ex); }

        // 포그라운드 활성 중에는 인프로세스 루프도 병행(백그라운드 진입 시 BGTask 가 이어받음).
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = BackgroundLoop.RunAsync(interval, scan,
            n => OnTick?.Invoke(new BackgroundScanTick(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), n, "bgtask")), _cts.Token);
        return Task.CompletedTask;
    }

    private static void ScheduleNext()
    {
        try
        {
            var req = new BGProcessingTaskRequest(TaskId) { EarliestBeginDate = (NSDate)DateTime.Now.AddMinutes(15) };
            BGTaskScheduler.Shared.Submit(req, out _);
        }
        catch (Exception ex) { Program.Log("BgWorker.iOS.Schedule", ex); }
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch { } }
        _loop = null; _cts?.Dispose(); _cts = null;
        try { BGTaskScheduler.Shared.Cancel(TaskId); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        OnTick = null;
    }
}
#endif
