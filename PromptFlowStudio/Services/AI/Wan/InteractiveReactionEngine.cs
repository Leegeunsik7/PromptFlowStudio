using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>리액션 트리거 종류 — OBS 자체 이벤트(SceneChange/AudioPeak)와, 외부 도구가 OBS CustomEvent 로
///   중계하는 관례적 종류(Donation/ChatCommand — OBS 자체엔 이 개념이 없음)를 함께 다룬다.</summary>
public enum EventTriggerType { Donation, ChatCommand, SceneChange, AudioPeak }

public enum ObsConnectionState { Disconnected, Connecting, Identified, Failed }

/// <summary>수신된 트리거 1건(큐 적재용 불변 레코드).</summary>
public sealed record ReactionTriggerEvent(EventTriggerType Type, string RawEventType, DateTimeOffset At, string? Detail);

/// <summary>트리거 1건 처리 결과 — HUD/감사용.</summary>
public sealed record ReactionDispatchResult(bool Ok, EventTriggerType Type, bool MotionInjected, bool LoraSwapped, double ElapsedMs, string Message);

/// <summary>
/// [PromptFlow Studio v11.2.0] OBS WebSocket 5.0 순수 BCL 클라이언트 + 리액션 디스패처.
///   ▸ ⚠ 실측 고지: obs-websocket v5 는 NDI 와 달리 공개 문서화된 프로토콜(JSON over WebSocket, Hello(op0)→
///     Identify(op1)→Identified(op2)→Event(op5))이라 순수 BCL(ClientWebSocket+System.Text.Json)로 재구현
///     가능하다. 인증(SHA256 이중 해시 챌린지-응답)도 공개 스펙을 따랐으나, 이 환경엔 실 OBS 인스턴스가 없어
///     실물 검증은 못했다 — 테스트는 동일 프로토콜을 말하는 자체 스텁 서버로 핸드셰이크/이벤트파싱 흐름만 검증.
///   ▸ ⚠ 명칭 정정: 지시서의 "LoRAHotSwapManager.RequestSwapAsync()"는 실재하지 않는다(전수 확인) — 실제 공개
///     API는 MergeAsync/UnmergeAsync 이며, 이걸 그대로 호출한다.
///   ▸ Donation/ChatCommand 는 OBS 고유 이벤트가 아니라 "CustomEvent"(외부 챗봇/도네이션 알림 도구가 OBS 로
///     중계하는 실무 관례, 예: Streamer.bot)의 eventData.kind 판별자로 구분한다. SceneChange 는
///     CurrentProgramSceneChanged, AudioPeak 는 InputVolumeMeters(둘 다 실 OBS 이벤트) 에 매핑한다.
///   ▸ Zero-Alloc 큐: ConcurrentQueue + 고정 상한(64) — 폭주 시 초과분은 예외 없이 드롭+카운터(Interlocked).
///     단일 백그라운드 소비 루프가 직렬로 디스패치해 LoRAHotSwapManager 동시 호출 경합을 피한다.
///   ▸ Fail-Safe Reset: 트리거 처리가 타임아웃(1.5s)/취소되면 모션을 즉시 중립(None/0)으로 원복 후 주입 —
///     부분 적용 상태로 남기지 않는다.
/// </summary>
public sealed class InteractiveReactionEngine : IDisposable
{
    private const int MaxQueueDepth = 64;
    private const int ReactionTimeoutMs = 1500;

    private readonly WanDynamicMotionController _motion;
    private readonly LoRAHotSwapManager _lora;
    private readonly ConcurrentQueue<ReactionTriggerEvent> _queue = new();
    private readonly object _gate = new();
    private readonly List<ReactionTriggerEvent> _recentEvents = new();
    private int _queueDepth;
    // v12.2.0: 소켓·루프 CTS·수신/디스패치 태스크를 한 "세대" 객체로 묶어 Interlocked.Exchange 로 원자 교체.
    //   이전(v12.1.0)엔 _ws/_lifetimeCts 두 필드를 따로 교체해 반쪽 교체 창이 있었고, 루프 태스크는 추적되지 않았다.
    private sealed class ObsConnection(ClientWebSocket ws, CancellationTokenSource cts)
    {
        public readonly ClientWebSocket Ws = ws;
        public readonly CancellationTokenSource Cts = cts;
        public Task ReceiveTask = Task.CompletedTask;
        public Task DispatchTask = Task.CompletedTask;
        private int _released;
        public bool TryMarkReleased() => Interlocked.Exchange(ref _released, 1) == 0;
    }
    private ObsConnection? _conn;
    private long _connectionsReleased;

    /// <summary>테스트/진단: 현재 세대 연결 자원(소켓·CTS)이 살아 있는지.</summary>
    internal bool HasLiveConnectionResources => Volatile.Read(ref _conn) is not null;
    /// <summary>테스트/진단: 해제 완료된 연결 세대 수(이중 해제는 1회로만 집계).</summary>
    internal long ConnectionsReleased => Interlocked.Read(ref _connectionsReleased);
    /// <summary>테스트/진단: 현재 세대의 수신·디스패치 루프 태스크(연결 없으면 null).</summary>
    internal (Task Receive, Task Dispatch)? ActiveLoopTasks => Volatile.Read(ref _conn) is { } c ? (c.ReceiveTask, c.DispatchTask) : null;
    private ObsConnectionState _connState = ObsConnectionState.Disconnected;
    private long _eventsReceived, _dispatched, _queueOverflows, _failSafeResets;
    private EventTriggerType? _activeMode;
    private bool _disposed;
    private Uri? _lastServerUri;
    private string? _lastPassword;   // 메모리 전용(디스크/로그 기록 금지) - 재연결 스위치용

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public ObsConnectionState ConnectionState { get { lock (_gate) return _connState; } }
    public EventTriggerType? ActiveMode { get { lock (_gate) return _activeMode; } }
    public IReadOnlyList<ReactionTriggerEvent> RecentEvents { get { lock (_gate) return _recentEvents.ToList(); } }
    public long EventsReceived => Interlocked.Read(ref _eventsReceived);
    public long Dispatched => Interlocked.Read(ref _dispatched);
    public long QueueOverflows => Interlocked.Read(ref _queueOverflows);
    public long FailSafeResets => Interlocked.Read(ref _failSafeResets);
    public int QueueDepth => Volatile.Read(ref _queueDepth);

    public InteractiveReactionEngine(WanDynamicMotionController motion, LoRAHotSwapManager lora)
    {
        _motion = motion;
        _lora = lora;
    }

    /// <summary>OBS WebSocket 5.0 서버에 접속 - Hello→Identify→Identified 핸드셰이크 후 수신 루프 시작.</summary>
    public async Task ConnectAsync(Uri serverUri, string? password = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await TeardownConnectionAsync().ConfigureAwait(false);   // 이전 세대: 원자 분리 → 취소 → 루프 종료 대기(1초 한도) → 해제
        lock (_gate) { _lastServerUri = serverUri; _lastPassword = password; }
        SetState(ObsConnectionState.Connecting);
        var conn = new ObsConnection(new ClientWebSocket(), CancellationTokenSource.CreateLinkedTokenSource(ct));
        Volatile.Write(ref _conn, conn);
        var ws = conn.Ws;
        var loopCt = conn.Cts.Token;

        try
        {
            await ws.ConnectAsync(serverUri, ct).ConfigureAwait(false);

            using var hello = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false);   // v12.2.0: JsonDocument 풀 버퍼 반환(이전엔 미해제)
            var helloD = hello.RootElement.GetProperty("d");
            var rpcVersion = helloD.GetProperty("rpcVersion").GetInt32();

            string? authResponse = null;
            if (password is not null && helloD.TryGetProperty("authentication", out var auth))
            {
                var salt = auth.GetProperty("salt").GetString() ?? "";
                var challenge = auth.GetProperty("challenge").GetString() ?? "";
                authResponse = ComputeAuthResponse(password, salt, challenge);
            }

            var identifyD = new JsonObject { ["rpcVersion"] = rpcVersion };
            if (authResponse is not null) identifyD["authentication"] = authResponse;
            var identify = new JsonObject { ["op"] = 1, ["d"] = identifyD };
            await SendJsonAsync(ws, identify, ct).ConfigureAwait(false);

            using var identified = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false);
            if (identified.RootElement.GetProperty("op").GetInt32() != 2)
                throw new InvalidOperationException("Identified(op=2) 응답을 받지 못함");

            SetState(ObsConnectionState.Identified);
            conn.DispatchTask = Task.Run(() => DispatchLoopAsync(loopCt));
            conn.ReceiveTask = Task.Run(() => ReceiveLoopAsync(conn, loopCt));
        }
        catch (Exception ex)
        {
            ReleaseIfCurrent(conn);   // v12.2.0: 핸드셰이크 실패 세대의 소켓/CTS 즉시 해제(이전엔 Failed 상태로 핸들 잔류)
            SetState(ObsConnectionState.Failed);
            Program.Log("InteractiveReaction.Connect", ex);
            throw;
        }
    }

    // obs-websocket v5 공개 인증 스펙: base64(sha256( base64(sha256(password+salt)) + challenge )).
    private static string ComputeAuthResponse(string password, string salt, string challenge)
    {
        var secretHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secretHash + challenge)));
    }

    // v12.1.0: 소켓을 인자로 고정 - 재연결 후 이전 세대 루프가 새 소켓을 읽거나, 뒤늦은 finally 가 새 연결의 Identified 를 Disconnected 로 덮지 않게 한다.
    private async Task ReceiveLoopAsync(ObsConnection conn, CancellationToken ct)
    {
        var ws = conn.Ws;
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                JsonDocument msg;
                try { msg = await ReceiveJsonAsync(ws, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }

                using (msg)
                {
                    if (!msg.RootElement.TryGetProperty("op", out var opEl) || opEl.GetInt32() != 5) continue;
                    var d = msg.RootElement.GetProperty("d");
                    var eventType = d.GetProperty("eventType").GetString() ?? "";
                    var eventData = d.TryGetProperty("eventData", out var ed) ? ed : default;
                    var mapped = MapEventType(eventType, eventData);
                    if (mapped is { } type) EnqueueTrigger(type, eventType);
                }
            }
        }
        catch (Exception ex) { Program.Log("InteractiveReaction.ReceiveLoop", ex); }
        // v12.2.0: 비정상 끊김(서버 크래시/RST) 시 이전엔 상태만 Disconnected 로 바꾸고 디스패치 루프(10ms 폴링)와 소켓/CTS 가
        //   영구 잔류했다 - 현재 세대일 때만 원자 분리 후 해제(DisconnectAsync/재연결이 먼저 분리했으면 no-op).
        finally { if (ReleaseIfCurrent(conn)) SetState(ObsConnectionState.Disconnected); }
    }

    private static EventTriggerType? MapEventType(string eventType, JsonElement eventData)
    {
        switch (eventType)
        {
            case "CurrentProgramSceneChanged":
            case "CurrentPreviewSceneChanged":
                return EventTriggerType.SceneChange;
            case "InputVolumeMeters":
                return EventTriggerType.AudioPeak;
            case "CustomEvent":
                if (eventData.ValueKind == JsonValueKind.Object && eventData.TryGetProperty("kind", out var kindEl))
                {
                    var kind = kindEl.GetString();
                    if (string.Equals(kind, "donation", StringComparison.OrdinalIgnoreCase)) return EventTriggerType.Donation;
                    if (string.Equals(kind, "chatCommand", StringComparison.OrdinalIgnoreCase)) return EventTriggerType.ChatCommand;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>트리거를 큐에 적재(Zero-Alloc 카운터 갱신) - 상한 초과는 예외 없이 드롭+카운터. 외부(테스트/수동)에서도 호출 가능.</summary>
    public bool EnqueueTrigger(EventTriggerType type, string rawEventType, string? detail = null)
    {
        Interlocked.Increment(ref _eventsReceived);
        if (Interlocked.Increment(ref _queueDepth) > MaxQueueDepth)
        {
            Interlocked.Decrement(ref _queueDepth);
            Interlocked.Increment(ref _queueOverflows);
            lock (_gate) LastAction = $"큐 오버플로우({MaxQueueDepth}) - {type} 드롭";
            OnChange?.Invoke();
            return false;
        }

        var evt = new ReactionTriggerEvent(type, rawEventType, DateTimeOffset.UtcNow, detail);
        _queue.Enqueue(evt);
        lock (_gate) { _recentEvents.Insert(0, evt); if (_recentEvents.Count > 20) _recentEvents.RemoveAt(_recentEvents.Count - 1); }
        OnChange?.Invoke();
        return true;
    }

    /// <summary>백그라운드 소비 루프 없이도 테스트/수동 호출로 큐 1건을 즉시 처리하고 싶을 때 사용. 큐가 비었으면 null.</summary>
    public async Task<ReactionDispatchResult?> DispatchNextAsync(CancellationToken ct = default)
    {
        if (!_queue.TryDequeue(out var evt)) return null;
        Interlocked.Decrement(ref _queueDepth);
        return await DispatchOneAsync(evt, ct).ConfigureAwait(false);
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_queue.TryDequeue(out var evt))
            {
                Interlocked.Decrement(ref _queueDepth);
                try { await DispatchOneAsync(evt, ct).ConfigureAwait(false); }
                catch (Exception ex) { Program.Log("InteractiveReaction.Dispatch", ex); }
            }
            else
            {
                try { await Task.Delay(10, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task<ReactionDispatchResult> DispatchOneAsync(ReactionTriggerEvent evt, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ReactionTimeoutMs);
        lock (_gate) _activeMode = evt.Type;

        var motionOk = false;
        try
        {
            var (zoomMag, panDir, panMag, loraId, loraStrength) = ProfileFor(evt.Type);
            _motion.SetZoom(ZoomDirection.In, zoomMag);
            _motion.SetPan(panDir, panMag);
            _motion.InjectNext();
            motionOk = true;

            var swap = await _lora.MergeAsync(loraId, loraStrength, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            Interlocked.Increment(ref _dispatched);
            var msg = $"{evt.Type} 반응 완료 - 모션 주입 + LoRA({loraId}) {(swap.Ok ? "병합" : "실패")}";
            lock (_gate) LastAction = msg;
            OnChange?.Invoke();
            return new ReactionDispatchResult(true, evt.Type, motionOk, swap.Ok, sw.Elapsed.TotalMilliseconds, msg);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _failSafeResets);
            _motion.SetZoom(ZoomDirection.None, 0);
            _motion.SetPan(PanDirection.None, 0);
            _motion.InjectNext();
            sw.Stop();
            var msg = $"{evt.Type} 타임아웃({ReactionTimeoutMs}ms) - Fail-Safe 원복";
            lock (_gate) LastAction = msg;
            OnChange?.Invoke();
            return new ReactionDispatchResult(false, evt.Type, motionOk, false, sw.Elapsed.TotalMilliseconds, msg);
        }
        finally { lock (_gate) _activeMode = null; }
    }

    // 트리거 종류별 결정적 모션/LoRA 프로필(자리표시자 - 이 프로젝트 전역의 "동일 추상화 수준" 관례).
    private static (double ZoomMag, PanDirection PanDir, double PanMag, string LoraId, double LoraStrength) ProfileFor(EventTriggerType type) => type switch
    {
        EventTriggerType.Donation => (0.9, PanDirection.Up, 0.6, "reaction-donation", 0.8),
        EventTriggerType.ChatCommand => (0.4, PanDirection.Left, 0.3, "reaction-chat", 0.4),
        EventTriggerType.SceneChange => (0.2, PanDirection.None, 0.0, "reaction-scene", 0.2),
        EventTriggerType.AudioPeak => (0.6, PanDirection.Right, 0.5, "reaction-audiopeak", 0.6),
        _ => (0.3, PanDirection.None, 0.0, "reaction-default", 0.3),
    };

    private static async Task SendJsonAsync(ClientWebSocket ws, JsonNode node, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("서버가 연결을 닫음");
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    private void SetState(ObsConnectionState state)
    {
        lock (_gate) { _connState = state; LastAction = $"연결 상태 -> {state}"; }
        OnChange?.Invoke();
    }

    /// <summary>마지막으로 접속했던 서버 URI(재연결 스위치 대상) - 한 번도 접속 시도가 없으면 null.</summary>
    public Uri? LastServerUri { get { lock (_gate) return _lastServerUri; } }

    /// <summary>v12.1.0: 명시적 연결 해제 - 수신/디스패치 루프 취소 + 소켓 정상 종료 시도(1초 한도) 후 Disconnected.</summary>
    public async Task DisconnectAsync()
    {
        var ws = Volatile.Read(ref _conn)?.Ws;
        if (ws is { State: WebSocketState.Open })
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client disconnect", closeCts.Token).ConfigureAwait(false);
            }
            catch { /* 상대가 이미 끊었거나 1초 초과 - Abort 로 정리 */ }
        }
        await TeardownConnectionAsync().ConfigureAwait(false);
        SetState(ObsConnectionState.Disconnected);
    }

    /// <summary>v12.1.0: 재연결 스위치 - 마지막 URI/비밀번호로 해제 후 재접속. 접속 이력이 없으면 InvalidOperationException.</summary>
    public async Task ReconnectAsync(CancellationToken ct = default)
    {
        Uri? uri; string? pw;
        lock (_gate) { uri = _lastServerUri; pw = _lastPassword; }
        if (uri is null) throw new InvalidOperationException("재연결 대상 없음 - ConnectAsync 를 먼저 호출해야 함");
        await DisconnectAsync().ConfigureAwait(false);
        await ConnectAsync(uri, pw, ct).ConfigureAwait(false);
    }

    // 현재 세대를 원자 분리 → 취소/Abort → 수신·디스패치 루프 종료를 1초 한도로 대기 → CTS/소켓 해제.
    private async Task TeardownConnectionAsync()
    {
        var conn = Interlocked.Exchange(ref _conn, null);
        if (conn is null) return;
        CancelAndAbort(conn);
        try { await Task.WhenAll(conn.ReceiveTask, conn.DispatchTask).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch { /* 루프 내부 예외/1초 초과 - 해제는 계속 진행 */ }
        Release(conn);
    }

    // 동기 경로(Dispose / 루프 finally / 핸드셰이크 실패): conn 이 여전히 현재 세대일 때만 분리·해제. 분리 성공 시 true.
    private bool ReleaseIfCurrent(ObsConnection conn)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _conn, null, conn), conn)) return false;
        CancelAndAbort(conn);
        Release(conn);
        return true;
    }

    private static void CancelAndAbort(ObsConnection conn)
    {
        try { conn.Cts.Cancel(); } catch { /* 무시 */ }
        try { conn.Ws.Abort(); } catch { /* 무시 */ }
    }

    private void Release(ObsConnection conn)
    {
        if (!conn.TryMarkReleased()) return;   // 이중 해제 차단
        try { conn.Ws.Dispose(); } catch { /* 무시 */ }
        try { conn.Cts.Dispose(); } catch { /* 무시 */ }
        Interlocked.Increment(ref _connectionsReleased);
    }

    public void ResetAll()
    {
        while (_queue.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _queueDepth, 0);
        Interlocked.Exchange(ref _eventsReceived, 0);
        Interlocked.Exchange(ref _dispatched, 0);
        Interlocked.Exchange(ref _queueOverflows, 0);
        Interlocked.Exchange(ref _failSafeResets, 0);
        lock (_gate) { _recentEvents.Clear(); _activeMode = null; LastAction = "리액션 엔진 초기화"; }
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Volatile.Read(ref _conn) is { } conn) ReleaseIfCurrent(conn);
    }
}