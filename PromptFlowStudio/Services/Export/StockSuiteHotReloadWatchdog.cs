using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Export;

/// <summary>🩺 핫리로드 워치독 결과 종류.</summary>
public enum HotReloadOutcome { Acknowledged = 0, RolledBack, RollbackFailed, Superseded }

/// <summary>🩺 핫리로드 워치독 결과 — <see cref="Error"/> = StockSuite 보고 오류(있을 때).</summary>
public sealed record HotReloadWatchResult(HotReloadOutcome Outcome, string Message, string? Error = null)
{
    public bool RolledBack => Outcome == HotReloadOutcome.RolledBack;
}

/// <summary>
/// 🩺 StockSuite 핫리로드 헬스 워치독 — <c>hotreload.trigger</c> 기록 후 <see cref="Timeout"/>(기본 5초) 내
/// IPC <c>HotReloadAck</c> 또는 <c>hotreload.ack</c> 파일 터치를 대기. 미응답·오류 보고 시 이전 배경(<c>*.backup.*</c>) 자동 롤백.
/// 신규 트리거는 진행 중 감시를 대체(Superseded). 콜백 예외 격리.
/// </summary>
public sealed class StockSuiteHotReloadWatchdog : IDisposable
{
    public const string AckFileName = "hotreload.ack";
    public const string RollbackWarning = "⚠️ Hot-reload unacknowledged. Automatically rolled back to previous background.";
    public const string RollbackFailedPrefix = "⚠️ Hot-reload unacknowledged. Rollback unavailable: ";
    public const string AckMessage = "✅ StockSuite hot-reload acknowledged.";
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    private const int MaxAckBytes = 4096;

    private readonly Func<string?, Task<StockSuiteExportResult>> _rollback;
    private readonly Func<bool>? _isLive;
    private readonly object _gate = new();
    private Arming? _current;
    private int _disposed;

    /// <param name="rollback">대상 폴더 → 이전 배경 복원(<see cref="StockSuiteExporter.RollbackPreviousAssetAsync"/>).</param>
    /// <param name="isStockSuiteLive">StockSuite 실행/연결 여부(false = 감시 생략 — 미실행 시 정상 내보내기 롤백 방지). null = 항상 감시.</param>
    public StockSuiteHotReloadWatchdog(Func<string?, Task<StockSuiteExportResult>> rollback, Func<bool>? isStockSuiteLive = null)
    {
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _isLive = isStockSuiteLive;
    }

    public TimeSpan Timeout { get; set; } = DefaultTimeout;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(100);
    public bool IsWatching { get { lock (_gate) return _current is not null; } }

    /// <summary>감시 종료 알림(백그라운드 스레드 — UI 는 InvokeAsync).</summary>
    public event Action<HotReloadWatchResult>? OnCompleted;

    /// <summary>무장 토큰 — <see cref="Arm"/> → <see cref="RunAsync"/>.</summary>
    public sealed class Arming
    {
        internal Arming(string dir) => Dir = dir;
        public string Dir { get; }
        internal readonly TaskCompletionSource<(bool Ok, string? Error)> Ack = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource Cts = new();
    }

    public static string AckPathFor(string dir) => Path.Combine(dir, AckFileName);

    /// <summary>트리거 기록 직전 호출 — 스테일 ack 제거 · 이전 감시 대체. StockSuite 미가동/폐기 = null(감시 생략).</summary>
    public Arming? Arm(string? targetPath)
    {
        if (Volatile.Read(ref _disposed) == 1 || string.IsNullOrWhiteSpace(targetPath)) return null;
        try { if (_isLive is not null && !_isLive()) return null; }
        catch (Exception ex) { Program.Log("HotReloadWatchdog.Live", ex); return null; }
        var dir = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (string.IsNullOrEmpty(dir)) return null;
        try { File.Delete(AckPathFor(dir)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("HotReloadWatchdog.StaleAck", ex); }
        var arm = new Arming(dir);
        Arming? prev;
        lock (_gate) { prev = _current; _current = arm; }
        if (prev is not null) TryCancel(prev);
        return arm;
    }

    /// <summary>IPC <c>HotReloadAck</c>/<c>HotReloadError</c> 수신 → 진행 중 감시 해제.</summary>
    public void Acknowledge(bool ok, string? error)
    {
        Arming? a;
        lock (_gate) a = _current;
        a?.Ack.TrySetResult((ok, error));
    }

    /// <summary>ack 대기 → 성공 = Acknowledged · 미응답/오류 = 자동 롤백.</summary>
    public async Task<HotReloadWatchResult> RunAsync(Arming arm)
    {
        HotReloadWatchResult res;
        try
        {
            var ack = await WaitAckAsync(arm).ConfigureAwait(false);
            if (ack is { Ok: true }) res = new(HotReloadOutcome.Acknowledged, AckMessage);
            else res = await RollbackAsync(arm.Dir, ack?.Error).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { res = new(HotReloadOutcome.Superseded, "Hot-reload watch superseded."); }
        finally
        {
            lock (_gate) if (ReferenceEquals(_current, arm)) _current = null;
            arm.Cts.Dispose();
        }
        var h = OnCompleted;
        if (h is not null)
        {
            try { h(res); } catch (Exception ex) { Program.Log("HotReloadWatchdog.OnCompleted", ex); }
        }
        return res;
    }

    private async Task<(bool Ok, string? Error)?> WaitAckAsync(Arming arm)
    {
        var ct = arm.Cts.Token;
        var sw = Stopwatch.StartNew();
        var poll = PollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(10) : PollInterval;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (arm.Ack.Task.IsCompleted) return arm.Ack.Task.Result;
            if (TryReadAckFile(arm.Dir) is { } fileAck) return fileAck;
            var left = Timeout - sw.Elapsed;
            if (left <= TimeSpan.Zero) return null;
            await Task.WhenAny(arm.Ack.Task, Task.Delay(left < poll ? left : poll, ct)).ConfigureAwait(false);
        }
    }

    /// <summary><c>hotreload.ack</c> 판독 — 부재/쓰기 중 = null · <c>"ok":false</c>/<c>"status":"error"</c> = 오류 · 그 외 = 성공.</summary>
    public static (bool Ok, string? Error)? TryReadAckFile(string dir)
    {
        var path = AckPathFor(dir);
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length > MaxAckBytes) return (false, "ack payload too large");
            var buf = new byte[(int)fs.Length];
            fs.ReadExactly(buf);
            return ParseAck(buf);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>ack 페이로드 판정(빈/비JSON 터치 = 성공).</summary>
    public static (bool Ok, string? Error) ParseAck(ReadOnlySpan<byte> payload)
    {
        if (payload.Trim(" \t\r\n"u8).Length == 0) return (true, null);
        try
        {
            var r = new Utf8JsonReader(payload);
            using var doc = JsonDocument.ParseValue(ref r);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (true, null);
            string? err = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var failed = (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                || (root.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && string.Equals(st.GetString(), "error", StringComparison.OrdinalIgnoreCase));
            return failed ? (false, err ?? "StockSuite reported hot-reload error") : (true, null);
        }
        catch (JsonException) { return (true, null); }
    }

    private async Task<HotReloadWatchResult> RollbackAsync(string dir, string? error)
    {
        try
        {
            var rb = await _rollback(dir).ConfigureAwait(false);
            if (rb.Ok) return new(HotReloadOutcome.RolledBack, RollbackWarning, error);
            return new(HotReloadOutcome.RollbackFailed, RollbackFailedPrefix + rb.Message, error);
        }
        catch (Exception ex)
        {
            Program.Log("HotReloadWatchdog.Rollback", ex);
            return new(HotReloadOutcome.RollbackFailed, RollbackFailedPrefix + ex.Message, error);
        }
    }

    private static void TryCancel(Arming a)
    {
        try { a.Cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Arming? a;
        lock (_gate) { a = _current; _current = null; }
        if (a is not null) TryCancel(a);
        OnCompleted = null;
    }
}
