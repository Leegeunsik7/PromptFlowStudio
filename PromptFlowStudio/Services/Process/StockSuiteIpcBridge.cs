using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.ProcessBridge;

/// <summary>IPC 메시지 종류.</summary>
public enum StockSuiteIpcKind { Unknown = 0, TickerSelected, ThemeChanged, Ping }

/// <summary>파싱된 IPC 메시지(줄 단위 JSON 1건).</summary>
public sealed record StockSuiteIpcMessage(StockSuiteIpcKind Kind, string? Ticker, string? Name, string? Sector);

/// <summary>테마 브로드캐스트 전달 경로.</summary>
public enum IpcSendRoute { Failed = 0, Pipe, FileFallback }

/// <summary>
/// 🔌 StockSuite ↔ PFS 양방향 Named Pipe 브릿지(<c>\\.\pipe\StockSuiteBridge</c> · 줄 단위 UTF-8 JSON).
///   ▸ PFS = 듀플렉스 서버(현재 사용자 한정 ACL) · StockSuite = 클라이언트. 연결 끊김 시 자동 재대기(1초 백오프).
///   ▸ 수신: <c>{"type":"TickerSelected","ticker":"005930","name":"삼성전자"}</c> → <see cref="OnTickerSelected"/>.
///   ▸ 송신: <c>{"type":"ThemeChanged","theme":"OledBlack","dimmer":20,"ts":…}</c> — HTS 테마/Dimmer 변경 시 150ms 디바운스 브로드캐스트.
///   ▸ 파이프 미연결/쓰기 실패 → StockSuite 에셋 폴더 <c>hts_theme.json</c> + <c>hotreload.trigger</c> 파일 폴백(기존 경로 유지).
///   ▸ 콜백·IO 예외 격리, Dispose 시 CTS·파이프·타이머 해제.
/// </summary>
public sealed class StockSuiteIpcBridge : IDisposable
{
    public const string DefaultPipeName = "StockSuiteBridge";
    public const string ThemeFileName = "hts_theme.json";
    public const int MaxLineLength = 4096;
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(1);
    private const int DebounceMs = 150;
    private const int PipeBufferSize = 8192;

    private readonly string _pipeName;
    private readonly Func<string?>? _fallbackDir;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private NamedPipeServerStream? _pipe;
    private System.Threading.Timer? _debounce;
    private (HtsHudTheme Theme, int Dimmer) _queued;
    private int _connected, _disposed;

    public StockSuiteIpcBridge(Func<string?>? fallbackDirectory, string? pipeName = null)
    {
        _fallbackDir = fallbackDirectory;
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? DefaultPipeName : pipeName;
    }

    public string PipeName => _pipeName;
    public bool IsConnected => Volatile.Read(ref _connected) == 1;
    public bool IsRunning => Volatile.Read(ref _cts) is not null;
    public Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;
    /// <summary>마지막 송신 경로(진단 표시).</summary>
    public IpcSendRoute LastRoute { get; private set; }
    /// <summary>마지막 파이프 쓰기 오류(진단).</summary>
    public string? LastError { get; private set; }

    /// <summary>StockSuite 종목 선택 수신(백그라운드 스레드 — UI 는 InvokeAsync).</summary>
    public event Action<StockSuiteIpcMessage>? OnTickerSelected;
    /// <summary>연결 상태 변화.</summary>
    public event Action? OnConnectionChanged;

    /// <summary>서버 수신 루프 시작(멱등).</summary>
    public void Start()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => ServeLoopAsync(cts.Token));
    }

    private async Task ServeLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, PipeBufferSize, PipeBufferSize);   // 버퍼 0 이면 상대 미독 시 쓰기 블록
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                lock (_gate) _pipe = pipe;
                SetConnected(true);
                await ReadLoopAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or ObjectDisposedException or InvalidOperationException)
            {
                Program.Log("StockSuiteIpc.Serve", ex);
                if (ex is PlatformNotSupportedException) break;   // 파이프 미지원 플랫폼 → 파일 폴백 전용
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
            finally
            {
                lock (_gate) if (ReferenceEquals(_pipe, pipe)) _pipe = null;
                SetConnected(false);
                pipe?.Dispose();
            }
        }
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return;   // 클라이언트 종료
            DispatchLine(line);
        }
    }

    /// <summary>수신 1줄 처리 — 유효 TickerSelected 면 이벤트 발행(true).</summary>
    public bool DispatchLine(string? line)
    {
        var msg = ParseMessage(line);
        if (msg is null || msg.Kind != StockSuiteIpcKind.TickerSelected) return false;
        var h = OnTickerSelected;
        if (h is null) return true;
        try { h(msg); } catch (Exception ex) { Program.Log("StockSuiteIpc.OnTicker", ex); }
        return true;
    }

    /// <summary>줄 단위 JSON 파싱 + 스키마 즉시 검증(길이 · 티커 문자셋). 실패 = null.</summary>
    public static StockSuiteIpcMessage? ParseMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return null;
            var kind = t.GetString() switch
            {
                "TickerSelected" => StockSuiteIpcKind.TickerSelected,
                "ThemeChanged" => StockSuiteIpcKind.ThemeChanged,
                "Ping" => StockSuiteIpcKind.Ping,
                _ => StockSuiteIpcKind.Unknown,
            };
            if (kind == StockSuiteIpcKind.Unknown) return null;
            var ticker = Str(r, "ticker")?.Trim();
            if (kind == StockSuiteIpcKind.TickerSelected && !IsValidTicker(ticker)) return null;
            return new StockSuiteIpcMessage(kind, ticker, Str(r, "name")?.Trim(), Str(r, "sector")?.Trim());
        }
        catch (JsonException) { return null; }
    }

    public static bool IsValidTicker(string? ticker)
    {
        if (string.IsNullOrEmpty(ticker) || ticker.Length > 16) return false;
        foreach (var c in ticker)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '^')) return false;
        return true;
    }

    private static string? Str(JsonElement r, string name)
        => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static string ThemeKey(HtsHudTheme theme) => theme switch
    {
        HtsHudTheme.NavyQuant => "NavyQuant",
        HtsHudTheme.ModernMinimal => "ModernMinimal",
        _ => "OledBlack",
    };

    /// <summary>테마 패킷(1줄 JSON · 개행 미포함).</summary>
    public static string BuildThemePacket(HtsHudTheme theme, int dimmer, DateTimeOffset at)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteString("type", "ThemeChanged");
            w.WriteString("theme", ThemeKey(theme));
            w.WriteNumber("dimmer", Math.Clamp(dimmer, 0, 100));
            w.WriteString("ts", at);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    /// <summary>슬라이더 연속 입력 대비 150ms 디바운스 브로드캐스트(마지막 값만 송신).</summary>
    public void QueueThemeBroadcast(HtsHudTheme theme, int dimmer)
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        lock (_gate)
        {
            _queued = (theme, dimmer);
            _debounce ??= new System.Threading.Timer(static s => ((StockSuiteIpcBridge)s!).FlushQueued(), this, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private void FlushQueued()
    {
        (HtsHudTheme T, int D) q;
        lock (_gate) q = _queued;
        _ = SendThemeSafeAsync(q.T, q.D);
    }

    private async Task SendThemeSafeAsync(HtsHudTheme theme, int dimmer)
    {
        try { await SendThemeAsync(theme, dimmer).ConfigureAwait(false); }
        catch (Exception ex) { Program.Log("StockSuiteIpc.Broadcast", ex); }
    }

    /// <summary>테마/Dimmer 즉시 송신 — 파이프 우선, 실패/미연결 시 파일 폴백.</summary>
    public async Task<IpcSendRoute> SendThemeAsync(HtsHudTheme theme, int dimmer, CancellationToken ct = default)
    {
        var now = UtcNow();
        var packet = BuildThemePacket(theme, dimmer, now);
        var route = await TryWritePipeAsync(packet, ct).ConfigureAwait(false) ? IpcSendRoute.Pipe
                  : WriteFileFallback(packet, now) ? IpcSendRoute.FileFallback : IpcSendRoute.Failed;
        LastRoute = route;
        return route;
    }

    private async Task<bool> TryWritePipeAsync(string packet, CancellationToken ct)
    {
        NamedPipeServerStream? pipe;
        lock (_gate) pipe = _pipe;
        if (pipe is null || !IsConnected) return false;
        if (!await _writeLock.WaitAsync(WriteTimeout, ct).ConfigureAwait(false)) return false;
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(WriteTimeout);
            var bytes = Encoding.UTF8.GetBytes(packet + "\n");
            await pipe.WriteAsync(bytes, to.Token).ConfigureAwait(false);
            await pipe.FlushAsync(to.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
        {
            Program.Log("StockSuiteIpc.Write", ex);
            LastError = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally { try { _writeLock.Release(); } catch (ObjectDisposedException) { } }
    }

    /// <summary>기존 파일 기반 폴백 — <c>hts_theme.json</c> 원자 기록 + <c>hotreload.trigger</c> 갱신.</summary>
    private bool WriteFileFallback(string packet, DateTimeOffset now)
    {
        string? dir = null;
        try { dir = _fallbackDir?.Invoke(); } catch (Exception ex) { Program.Log("StockSuiteIpc.FallbackDir", ex); }
        if (string.IsNullOrWhiteSpace(dir)) return false;
        dir = dir.Trim().Trim('"');
        if (!Directory.Exists(dir)) return false;
        var path = Path.Combine(dir, ThemeFileName);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, packet);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Program.Log("StockSuiteIpc.FileFallback", ex);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception ex2) when (ex2 is IOException or UnauthorizedAccessException) { }
            return false;
        }
        StockSuiteExporter.WriteHotReloadTrigger(dir, ThemeFileName, now);
        return true;
    }

    private void SetConnected(bool on)
    {
        if (Interlocked.Exchange(ref _connected, on ? 1 : 0) == (on ? 1 : 0)) return;
        var h = OnConnectionChanged;
        if (h is null) return;
        try { h(); } catch (Exception ex) { Program.Log("StockSuiteIpc.OnConnection", ex); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } cts.Dispose(); }
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;
            try { _pipe?.Dispose(); } catch (IOException) { }
            _pipe = null;
        }
        _writeLock.Dispose();
        OnTickerSelected = null;
        OnConnectionChanged = null;
    }
}
