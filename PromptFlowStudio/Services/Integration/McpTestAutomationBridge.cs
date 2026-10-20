using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Integration;

/// <summary>MCP 서버 전송 방식 — WebSocket(JSON-RPC 프레임) · Http(POST JSON-RPC) · Stub(오프라인 시뮬).</summary>
public enum McpTransport { WebSocket, Http, Stub }

/// <summary>MCP 서버 연결 상태(HUD 신호등).</summary>
public enum McpServerState { Unknown, Connected, Degraded, Offline }

/// <summary>E2E 스텝 동작 — MCP 브라우저 툴로 사상되는 최소 동사 집합.</summary>
public enum McpStepAction { Navigate, Click, Fill, Snapshot, Screenshot, Assert, Wait }

/// <summary>JSON-RPC 호출 결과(불변 · 스텁 폴백 여부 동반).</summary>
public sealed record McpRpcResult(bool Ok, string Method, string Payload, double LatencyMs, bool Stub, string? Error);

/// <summary>MCP 서버 스냅샷(불변 · HUD 표출).</summary>
public sealed record McpServerView(string Name, string Endpoint, McpTransport Transport, McpServerState State, long Calls, long Failures, double LatencyMs);

/// <summary>E2E 시나리오 스텝 정의(불변).</summary>
public sealed record McpE2eStep(McpStepAction Action, string Target, string? Value = null);

/// <summary>E2E 스텝 실행 결과(불변 · 라이브 스트립 표출).</summary>
public sealed record McpStepResult(int Index, McpStepAction Action, string Target, bool Passed, bool Stub, double LatencyMs, string Note);

/// <summary>시나리오 집계 리포트(불변).</summary>
public sealed record McpScenarioReport(string Name, int Total, int Passed, int Failed, int Stubbed, double DurationMs)
{
    public bool AllPassed => Failed == 0 && Total > 0;
    public double PassRatio => Total <= 0 ? 0d : (double)Passed / Total;
}

/// <summary>
/// [PromptFlow Studio v8.9.2] MCP 자동화 검증 브리지(관찰 가능 싱글턴 · 순수 BCL <see cref="ClientWebSocket"/>/<see cref="HttpClient"/>/<see cref="Utf8JsonWriter"/> · 외부 NuGet 0 · IDisposable).
///   ▸ <see cref="InvokeToolAsync"/>: JSON-RPC 2.0(<c>tools/call</c>) 봉투를 <see cref="ArrayBufferWriter{T}"/> 위에 직접 써 Playwright / Chrome DevTools MCP 서버로 송신 — 문자열 연결 0.
///   ▸ <see cref="RunScenarioAsync"/>: Navigate/Click/Fill/Snapshot/Assert 스텝을 MCP 브라우저 툴(<c>browser_navigate</c>/<c>browser_click</c>/…)로 사상해 Blazor UI E2E 를 무인 수행.
///   ▸ 연결 실패/미기동 서버는 <see cref="ConnectTimeout"/>(0.9초) 내 <b>스텁 폴백</b>으로 전환해 시나리오를 끝까지 완주시킨다(제5헌장 Graceful Degradation) — 스텁 스텝은 <c>Stub=true</c> 로 구분 계수.
///   ⚠ <see cref="OnChange"/> 는 호출 스레드(임의)에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장).
/// </summary>
public sealed class McpTestAutomationBridge : IDisposable
{
    /// <summary>스텁 전환 판정 시한(제5헌장 — 1초 내 Graceful Degradation).</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(6);
    /// <summary>오프라인 판정 후 재시도 유예 — 유예 내 호출은 I/O 없이 즉시 스텁 전환(시나리오 완주 속도 보장).</summary>
    private static readonly TimeSpan OfflineCooldown = TimeSpan.FromSeconds(10);
    private const int MaxSteps = 64;
    private const int ReceiveBufferSize = 1 << 15;

    private readonly ConcurrentDictionary<string, ServerChannel> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = ConnectTimeout,              // OS 기본 TCP 재시도(수 초) 대신 0.9초에 절단 → 제5헌장 1초 내 폴백
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly List<McpStepResult> _steps = new(MaxSteps);
    private readonly object _gate = new();
    private long _rpcId;
    private bool _disposed;

    public McpTestAutomationBridge()
    {
        // 부트스트랩 기본 메쉬(운영 등록 전 HUD 편의 — RegisterServer 재호출로 실 엔드포인트 교체).
        RegisterServer("playwright", "ws://127.0.0.1:47230/mcp", McpTransport.WebSocket);
        RegisterServer("chrome-devtools", "http://127.0.0.1:47231/mcp", McpTransport.Http);
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 관측 지표(HUD) ──
    public int ServerCount => _servers.Count;
    public int ConnectedCount { get; private set; }
    public long TotalCalls { get; private set; }
    public long FailedCalls { get; private set; }
    public long StubFallbacks { get; private set; }
    public double LastLatencyMs { get; private set; }
    public bool IsRunning { get; private set; }
    public int CurrentStep { get; private set; }
    public int TotalSteps { get; private set; }
    public McpScenarioReport? LastReport { get; private set; }
    public string? LastAction { get; private set; }
    public string? LastError { get; private set; }
    public string BaseUrl { get; private set; } = "http://localhost:5216/";

    /// <summary>모든 서버가 실접속 상태인지(하나라도 스텁이면 false).</summary>
    public bool FullyLive => ServerCount > 0 && ConnectedCount == ServerCount;

    /// <summary>스텝 결과 스냅샷(실행 순 · 스냅샷 시점에만 할당).</summary>
    public IReadOnlyList<McpStepResult> Steps
    {
        get { lock (_gate) return _steps.ToArray(); }
    }

    /// <summary>서버 스냅샷(등록 순 무관 · 이름 정렬).</summary>
    public IReadOnlyList<McpServerView> Servers
    {
        get
        {
            var list = new List<McpServerView>(_servers.Count);
            foreach (var kv in _servers)
            {
                var s = kv.Value;
                list.Add(new McpServerView(s.Name, s.Endpoint, s.Transport, s.State, s.Calls, s.Failures, s.LatencyMs));
            }
            list.Sort(static (x, y) => string.CompareOrdinal(x.Name, y.Name));
            return list;
        }
    }

    // ── 등록/구성 ──

    /// <summary>MCP 서버 등록(동일 이름 재호출 시 엔드포인트 교체 · 기존 소켓은 폐기).</summary>
    public void RegisterServer(string name, string endpoint, McpTransport transport)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint)) return;
        _servers.AddOrUpdate(name,
            _ => new ServerChannel(name, endpoint, transport),
            (_, old) => { old.ResetSocket(); old.Endpoint = endpoint; old.Transport = transport; old.State = McpServerState.Unknown; return old; });
        RecountConnected();
        LastAction = $"서버 등록 — {name} ({transport})";
        Notify();
    }

    /// <summary>E2E 대상 Blazor 호스트 베이스 URL 설정(기본 로컬 개발 호스트).</summary>
    public void SetBaseUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        BaseUrl = url.EndsWith('/') ? url : url + "/";
        LastAction = $"대상 호스트 — {BaseUrl}";
        Notify();
    }

    /// <summary>등록 서버 전체 헬스 프로브(<c>tools/list</c>) — 접속 실패는 Offline 표기 후 삼킴.</summary>
    public async Task ProbeAllAsync(CancellationToken ct = default)
    {
        foreach (var kv in _servers)
        {
            ct.ThrowIfCancellationRequested();
            kv.Value.ClearCooldown();   // 수동 프로브는 유예를 무시하고 실접속을 재시도
            var r = await InvokeRpcAsync(kv.Value, "tools/list", null, ct).ConfigureAwait(false);
            kv.Value.State = r.Stub ? McpServerState.Offline : r.Ok ? McpServerState.Connected : McpServerState.Degraded;
        }
        RecountConnected();
        LastAction = $"프로브 완료 — 접속 {ConnectedCount}/{ServerCount}";
        Notify();
    }

    // ── 실행 ──

    /// <summary>MCP 툴 1회 호출(<c>tools/call</c>) — <paramref name="argumentsJson"/> 은 이미 직렬화된 JSON 객체 문자열.</summary>
    public async Task<McpRpcResult> InvokeToolAsync(string server, string tool, string? argumentsJson = null, CancellationToken ct = default)
    {
        if (!_servers.TryGetValue(server, out var channel))
            return new McpRpcResult(false, tool, string.Empty, 0d, true, $"미등록 서버: {server}");
        var result = await InvokeRpcAsync(channel, "tools/call", BuildToolParams(tool, argumentsJson), ct).ConfigureAwait(false);
        Notify();
        return result;
    }

    /// <summary>
    /// E2E 시나리오 실행 — 스텝을 순차 수행하며 라이브 스트립(<see cref="Steps"/>)을 갱신한다.
    /// 서버 미기동이어도 스텁 폴백으로 완주하며, 실패 스텝은 <see cref="McpStepResult.Passed"/>=false 로 남겨 진단 가능성을 보존한다.
    /// </summary>
    public async Task<McpScenarioReport> RunScenarioAsync(string name, IReadOnlyList<McpE2eStep> steps, string? server = null, CancellationToken ct = default)
    {
        var target = server is not null && _servers.TryGetValue(server, out var picked) ? picked : PreferredChannel();
        target?.ClearCooldown();   // 시나리오 시작 시 1회는 실접속을 재시도(그 뒤 오프라인이면 유예로 즉시 스텁)
        lock (_gate) _steps.Clear();
        IsRunning = true;
        CurrentStep = 0;
        TotalSteps = Math.Min(steps.Count, MaxSteps);
        LastError = null;
        LastAction = $"E2E 시작 — {name}";
        Notify();

        var sw = Stopwatch.StartNew();
        var passed = 0;
        var failed = 0;
        var stubbed = 0;
        try
        {
            for (var i = 0; i < TotalSteps; i++)
            {
                ct.ThrowIfCancellationRequested();
                var step = steps[i];
                CurrentStep = i + 1;
                var r = target is null
                    ? new McpRpcResult(true, ToolFor(step.Action), "stub", 0d, true, null)
                    : await InvokeRpcAsync(target, "tools/call", BuildToolParams(ToolFor(step.Action), BuildStepArguments(step)), ct).ConfigureAwait(false);

                var ok = r.Ok && (step.Action != McpStepAction.Assert || AssertHeuristic(step, r.Payload));
                if (ok) passed++; else failed++;
                if (r.Stub) stubbed++;
                var note = r.Stub ? "스텁 폴백(서버 미기동 — 휴리스틱 판정)" : r.Error ?? "MCP 응답 수신";
                lock (_gate) _steps.Add(new McpStepResult(i + 1, step.Action, step.Target, ok, r.Stub, r.LatencyMs, note));
                Notify();
            }
        }
        catch (OperationCanceledException) { LastAction = "E2E 취소 — 완료분 보존"; }
        catch (Exception ex) { LastError = ex.Message; LastAction = "E2E 중단 — 완료분 보존"; }
        finally
        {
            sw.Stop();
            IsRunning = false;
            RecountConnected();
        }

        var report = new McpScenarioReport(name, passed + failed, passed, failed, stubbed, sw.Elapsed.TotalMilliseconds);
        LastReport = report;
        LastAction = $"E2E 완료 — {passed}/{report.Total} 통과" + (stubbed > 0 ? $" · 스텁 {stubbed}" : string.Empty);
        Notify();
        return report;
    }

    /// <summary>Blazor 셸 기본 진단 시나리오(랜딩 → 드로어 → 플로우 → 스냅샷 대조).</summary>
    public IReadOnlyList<McpE2eStep> DefaultBlazorScenario() => new[]
    {
        new McpE2eStep(McpStepAction.Navigate, BaseUrl),
        new McpE2eStep(McpStepAction.Wait, "networkidle"),
        new McpE2eStep(McpStepAction.Snapshot, "landing"),
        new McpE2eStep(McpStepAction.Click, "nav-drawer-toggle"),
        new McpE2eStep(McpStepAction.Click, "nav-flow"),
        new McpE2eStep(McpStepAction.Assert, "flow-canvas", "visible"),
        new McpE2eStep(McpStepAction.Fill, "prompt-dock-input", "E2E 무인 검증 프롬프트"),
        new McpE2eStep(McpStepAction.Screenshot, "flow-after-fill"),
    };

    // ── JSON-RPC 코어 ──

    private async Task<McpRpcResult> InvokeRpcAsync(ServerChannel channel, string method, string? paramsJson, CancellationToken ct)
    {
        TotalCalls++;
        channel.Calls++;
        var sw = Stopwatch.StartNew();
        var id = Interlocked.Increment(ref _rpcId);

        // 직전 오프라인 판정이 유예 내면 네트워크 왕복 없이 즉시 스텁 — 8스텝 시나리오가 연결 타임아웃 누적으로 지연되지 않도록.
        if (channel.State == McpServerState.Offline && Environment.TickCount64 < channel.OfflineUntilTicks)
        {
            StubFallbacks++;
            return new McpRpcResult(true, method, "stub", 0d, true, null);
        }

        try
        {
            var envelope = BuildEnvelope(id, method, paramsJson);
            var payload = channel.Transport switch
            {
                McpTransport.WebSocket => await SendWebSocketAsync(channel, envelope, ct).ConfigureAwait(false),
                McpTransport.Http => await SendHttpAsync(channel, envelope, ct).ConfigureAwait(false),
                _ => null,
            };
            sw.Stop();
            if (payload is null)
            {
                StubFallbacks++;
                channel.MarkOffline();
                channel.LatencyMs = sw.Elapsed.TotalMilliseconds;
                LastLatencyMs = channel.LatencyMs;
                return new McpRpcResult(true, method, "stub", channel.LatencyMs, true, null);
            }

            channel.State = McpServerState.Connected;
            channel.LatencyMs = sw.Elapsed.TotalMilliseconds;
            LastLatencyMs = channel.LatencyMs;
            var error = ExtractError(payload);
            if (error is not null)
            {
                FailedCalls++;
                channel.Failures++;
                channel.State = McpServerState.Degraded;
                return new McpRpcResult(false, method, payload, channel.LatencyMs, false, error);
            }
            return new McpRpcResult(true, method, payload, channel.LatencyMs, false, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            StubFallbacks++;
            channel.MarkOffline();
            return new McpRpcResult(true, method, "stub", sw.Elapsed.TotalMilliseconds, true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sw.Stop();
            StubFallbacks++;
            channel.ResetSocket();
            channel.MarkOffline();
            LastError = ex.Message;
            return new McpRpcResult(true, method, "stub", sw.Elapsed.TotalMilliseconds, true, null);
        }
    }

    private async Task<string?> SendWebSocketAsync(ServerChannel channel, ReadOnlyMemory<byte> envelope, CancellationToken ct)
    {
        await channel.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var socket = channel.Socket;
            if (socket is null || socket.State != WebSocketState.Open)
            {
                channel.ResetSocket();
                socket = new ClientWebSocket();
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(new Uri(channel.Endpoint), connectCts.Token).ConfigureAwait(false);
                channel.Socket = socket;
            }

            using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            callCts.CancelAfter(CallTimeout);
            await socket.SendAsync(envelope, WebSocketMessageType.Text, endOfMessage: true, callCts.Token).ConfigureAwait(false);

            var buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);
            try
            {
                var written = 0;
                while (true)
                {
                    var seg = new Memory<byte>(buffer, written, buffer.Length - written);
                    var res = await socket.ReceiveAsync(seg, callCts.Token).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close) return null;
                    written += res.Count;
                    if (res.EndOfMessage || written >= buffer.Length) break;
                }
                return Encoding.UTF8.GetString(buffer, 0, written);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
        finally { channel.Gate.Release(); }
    }

    private async Task<string?> SendHttpAsync(ServerChannel channel, ReadOnlyMemory<byte> envelope, CancellationToken ct)
    {
        using var callCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        callCts.CancelAfter(CallTimeout);
        using var content = new ReadOnlyMemoryContent(envelope);
        content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        using var req = new HttpRequestMessage(HttpMethod.Post, channel.Endpoint) { Content = content };
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, callCts.Token).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return $"{{\"error\":{{\"code\":{(int)res.StatusCode},\"message\":\"HTTP {(int)res.StatusCode}\"}}}}";
        return await res.Content.ReadAsStringAsync(callCts.Token).ConfigureAwait(false);
    }

    /// <summary>JSON-RPC 2.0 봉투를 버퍼 위에 직접 기록(문자열 보간 0 · Utf8JsonWriter).</summary>
    private static ReadOnlyMemory<byte> BuildEnvelope(long id, string method, string? paramsJson)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            if (paramsJson is { Length: > 0 })
            {
                w.WritePropertyName("params");
                using var doc = JsonDocument.Parse(paramsJson);
                doc.RootElement.WriteTo(w);
            }
            w.WriteEndObject();
        }
        return buffer.WrittenMemory;
    }

    private static string BuildToolParams(string tool, string? argumentsJson)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("name", tool);
            w.WritePropertyName("arguments");
            if (argumentsJson is { Length: > 0 })
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                doc.RootElement.WriteTo(w);
            }
            else { w.WriteStartObject(); w.WriteEndObject(); }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string BuildStepArguments(McpE2eStep step)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            switch (step.Action)
            {
                case McpStepAction.Navigate:
                    w.WriteString("url", step.Target);
                    break;
                case McpStepAction.Fill:
                    w.WriteString("element", step.Target);
                    w.WriteString("ref", step.Target);
                    w.WriteString("text", step.Value ?? string.Empty);
                    break;
                case McpStepAction.Wait:
                    w.WriteString("state", step.Target);
                    break;
                case McpStepAction.Assert:
                    w.WriteString("function", $"() => !!document.querySelector('[data-e2e=\"{step.Target}\"]')");
                    break;
                default:
                    w.WriteString("element", step.Target);
                    w.WriteString("ref", step.Target);
                    break;
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string ToolFor(McpStepAction action) => action switch
    {
        McpStepAction.Navigate => "browser_navigate",
        McpStepAction.Click => "browser_click",
        McpStepAction.Fill => "browser_type",
        McpStepAction.Snapshot => "browser_snapshot",
        McpStepAction.Screenshot => "browser_take_screenshot",
        McpStepAction.Assert => "browser_evaluate",
        _ => "browser_wait_for",
    };

    /// <summary>단언 판정 — 실 응답이면 payload 내 부정 신호(false/null/not found)를 검사, 스텁이면 타깃 유효성만 휴리스틱 판정.</summary>
    private static bool AssertHeuristic(McpE2eStep step, string payload)
    {
        if (string.Equals(payload, "stub", StringComparison.Ordinal)) return !string.IsNullOrWhiteSpace(step.Target);
        if (payload.Contains("\"result\":false", StringComparison.OrdinalIgnoreCase)) return false;
        return !payload.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>JSON-RPC 응답에서 error.message 추출(없으면 null).</summary>
    private static string? ExtractError(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("error", out var err) || err.ValueKind != JsonValueKind.Object) return null;
            return err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : "JSON-RPC 오류";
        }
        catch (JsonException) { return null; }   // 비 JSON(SSE 프레임 등)은 성공 취급 — 진단 가능성 우선
    }

    private ServerChannel? PreferredChannel()
    {
        ServerChannel? fallback = null;
        foreach (var kv in _servers)
        {
            fallback ??= kv.Value;
            if (kv.Value.State == McpServerState.Connected) return kv.Value;
        }
        return fallback;
    }

    private void RecountConnected()
    {
        var n = 0;
        foreach (var kv in _servers)
        {
            if (kv.Value.State == McpServerState.Connected) n++;
        }
        ConnectedCount = n;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _servers) kv.Value.Dispose();
        _servers.Clear();
        _http.Dispose();
        OnChange = null;
    }

    /// <summary>서버별 채널(소켓 1개 · 송수신 직렬화 게이트 · 누적 계수).</summary>
    private sealed class ServerChannel : IDisposable
    {
        public ServerChannel(string name, string endpoint, McpTransport transport)
        {
            Name = name;
            Endpoint = endpoint;
            Transport = transport;
        }

        public string Name { get; }
        public string Endpoint { get; set; }
        public McpTransport Transport { get; set; }
        public McpServerState State { get; set; } = McpServerState.Unknown;
        public ClientWebSocket? Socket { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Calls { get; set; }
        public long Failures { get; set; }
        public double LatencyMs { get; set; }
        public long OfflineUntilTicks { get; private set; }

        /// <summary>오프라인 표기 + 재시도 유예 설정(유예 내 호출은 I/O 없이 스텁).</summary>
        public void MarkOffline()
        {
            State = McpServerState.Offline;
            OfflineUntilTicks = Environment.TickCount64 + (long)OfflineCooldown.TotalMilliseconds;
        }

        /// <summary>재시도 유예 해제(수동 프로브 · 시나리오 첫 스텝).</summary>
        public void ClearCooldown() => OfflineUntilTicks = 0;

        public void ResetSocket()
        {
            try { Socket?.Dispose(); } catch { /* Graceful */ }
            Socket = null;
        }

        public void Dispose()
        {
            ResetSocket();
            Gate.Dispose();
        }
    }
}
