using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Mobile;    // RemoteRenderClient(URL 검증 · ws 변환 재사용)
using PromptFlowStudio.Services.Timeline;  // MultiTrackTimelineState · TimelineClip · TimelineTrackKind

namespace PromptFlowStudio.Services.Integration;

/// <summary>협업 허브 상태 — 유휴/수신중/실패.</summary>
public enum CollabHubState { Idle, Listening, Failed }

/// <summary>
/// [PromptFlow Studio v8.2.0] 실시간 멀티유저 협업 허브(관찰 가능 싱글턴 · 순수 BCL <see cref="System.Net.WebSockets"/> · 외부 실시간 SDK 0 · IDisposable).
///   ▸ <see cref="HttpListener"/> 로 <c>ws://localhost:{port}/collab/timeline</c> 업그레이드를 수용해 여러 편집자 세션을 유지한다(각 세션=송신 <see cref="Channel{T}"/> + 수신 루프).
///   ▸ 로컬 <see cref="MultiTrackTimelineState"/> 편집을 <b>델타로 캡처</b>(스냅샷 diff)해 <see cref="CollaborativeState"/> CRDT 에 반영하고 전 세션·피어로 브로드캐스트한다.
///   ▸ 수신 델타는 CRDT LWW 로 병합 — 승자만 로컬 타임라인에 미러링하고 <b>나머지 세션에 릴레이</b>(충돌 없는 수렴). 재전송/역순 도착도 멱등.
///   ▸ <see cref="ConnectPeerAsync"/> 로 다른 허브에 <see cref="ClientWebSocket"/> 로 합류(피어 메시). 접속 즉시 상태 스냅샷을 교환해 수렴.
///   ▸ Graceful: 포트 점유/권한 실패는 삼키고 <see cref="State"/>=Failed · 피어 0이어도 로컬 CRDT 는 정상 동작(우아한 저하).
///   ⚠ <see cref="OnChange"/> 는 수신/타임라인 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링(제3헌장).
/// </summary>
public sealed class LiveCollaborationHub : IDisposable
{
    private sealed class CollabSession
    {
        public required string Id { get; init; }
        public required WebSocket Socket { get; init; }
        public required Channel<string> Outbound { get; init; }
        public string Label { get; init; } = "peer";
    }

    private readonly MultiTrackTimelineState _timeline;
    private readonly CollaborativeState _crdt = new();
    private readonly ConcurrentDictionary<string, CollabSession> _sessions = new();
    private readonly object _gate = new();                      // CRDT + 스냅샷 캡처 직렬화
    private readonly Dictionary<string, string> _lastSig = new();  // 로컬 diff 캡처용 클립 서명
    private bool _applyingRemote;                               // 원격 미러링 중 로컬 재캡처 억제

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    private long _received, _broadcast, _applied, _conflicts;

    public LiveCollaborationHub(MultiTrackTimelineState timeline)
    {
        _timeline = timeline;
        PrimeFromTimeline();                 // 현재 타임라인을 CRDT 시드로 편입(새 피어 초기 동기화 기준)
        _timeline.OnChange += OnLocalTimelineChanged;   // 로컬 편집 → 델타 캡처
    }

    /// <summary>상태 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;
    private void Notify() => OnChange?.Invoke();

    // ── 상태(HUD) ──
    public CollabHubState State { get; private set; } = CollabHubState.Idle;
    public string? Prefix { get; private set; }
    public string? LastError { get; private set; }
    public string SiteId => _crdt.SiteId;
    public bool IsListening => State == CollabHubState.Listening;
    public int ConnectedPeers => _sessions.Count;
    public int SyncedClips => _crdt.LiveCount;
    public long Clock => _crdt.Clock;
    public long DeltasReceived => Interlocked.Read(ref _received);
    public long DeltasBroadcast => Interlocked.Read(ref _broadcast);
    public long DeltasApplied => Interlocked.Read(ref _applied);
    public long Conflicts => Interlocked.Read(ref _conflicts);

    // ── 수신 서버 ─────────────────────────────────────────────────────────────

    /// <summary>지정 포트(기본 8788)에서 WebSocket 협업 수신 시작(localhost). 이미 수신 중이면 무시. 실패는 Graceful(State=Failed).</summary>
    public bool Start(int port = 8788)
    {
        if (IsListening) return true;
        var prefix = $"http://localhost:{Math.Clamp(port, 1, 65535)}/collab/";
        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            Prefix = prefix; LastError = null; State = CollabHubState.Listening;
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            Notify();
            return true;
        }
        catch (Exception ex)
        {
            Program.Log("Collab.Start", ex);
            State = CollabHubState.Failed; LastError = Short(ex);
            Notify();
            return false;
        }
    }

    /// <summary>수신 중지 — 리스너 종료 + 전 세션 정리. 멱등.</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 무시 */ }
        try { _listener?.Stop(); _listener?.Close(); } catch { /* 무시 */ }
        _listener = null;
        foreach (var s in _sessions.Values) CloseSession(s, "hub stop");
        State = CollabHubState.Idle; Prefix = null;
        Notify();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { Program.Log("Collab.Accept", ex); break; }
            _ = HandleContextAsync(ctx, ct);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var path = (ctx.Request.Url?.AbsolutePath ?? "").TrimEnd('/');
            if (!ctx.Request.IsWebSocketRequest || !path.EndsWith("/timeline", StringComparison.Ordinal))
            {
                // 헬스/논-WS 요청 → 간단 응답.
                ctx.Response.StatusCode = ctx.Request.IsWebSocketRequest ? 404 : 200;
                var body = Encoding.UTF8.GetBytes($"{{\"service\":\"PromptFlow Collab\",\"site\":\"{_crdt.SiteId}\",\"peers\":{_sessions.Count}}}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
                ctx.Response.OutputStream.Close();
                return;
            }
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            RegisterSession(wsCtx.WebSocket, "inbound", ct);
        }
        catch (Exception ex) { Program.Log("Collab.Handle", ex); }
    }

    // ── 피어 합류(클라이언트) ───────────────────────────────────────────────────

    /// <summary>다른 협업 허브에 합류한다(ws/wss 또는 http→ws 자동 변환). 실패는 Graceful(false 반환).</summary>
    public async Task<bool> ConnectPeerAsync(string? url, CancellationToken ct = default)
    {
        var target = (url ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return false;
        if (!target.Contains("/collab/", StringComparison.OrdinalIgnoreCase))
            target = RemoteRenderClient.BuildEndpoint(target, "collab/timeline");
        var ws = RemoteRenderClient.ToWebSocketUrl(target);
        if (!RemoteRenderClient.IsValidWorkerUrl(ws)) { LastError = "유효하지 않은 피어 주소"; Notify(); return false; }
        try
        {
            var client = new ClientWebSocket();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(new Uri(ws), linked.Token).ConfigureAwait(false);
            RegisterSession(client, "peer:" + ws, _cts?.Token ?? CancellationToken.None);
            return true;
        }
        catch (Exception ex)
        {
            Program.Log("Collab.ConnectPeer", ex);
            LastError = "피어 접속 실패: " + Short(ex);
            Notify();
            return false;   // 우아한 저하 — 로컬 CRDT 는 계속 동작
        }
    }

    // ── 세션 수명주기 ───────────────────────────────────────────────────────────

    private void RegisterSession(WebSocket socket, string label, CancellationToken ct)
    {
        var session = new CollabSession
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Socket = socket,
            Outbound = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true }),
            Label = label,
        };
        _sessions[session.Id] = session;
        _ = Task.Run(() => SendPumpAsync(session, ct));
        _ = Task.Run(() => ReceiveLoopAsync(session, ct));
        SendSnapshot(session);   // 접속 즉시 상태 전송(수렴)
        Notify();
    }

    /// <summary>현재 CRDT 살아있는 델타 전량을 이 세션에 밀어 넣는다(상태 기반 초기 동기화).</summary>
    private void SendSnapshot(CollabSession session)
    {
        List<TimelineDelta> snap;
        lock (_gate) snap = _crdt.SnapshotDeltas().ToList();
        foreach (var d in snap) session.Outbound.Writer.TryWrite(CollaborativeState.Serialize(d));
    }

    private async Task SendPumpAsync(CollabSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in session.Outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (session.Socket.State != WebSocketState.Open) break;
                var bytes = Encoding.UTF8.GetBytes(msg);
                await session.Socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                Interlocked.Increment(ref _broadcast);
            }
        }
        catch (OperationCanceledException) { /* 종료 */ }
        catch (Exception ex) { Program.Log("Collab.Send", ex); }
    }

    private async Task ReceiveLoopAsync(CollabSession session, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var acc = new MemoryStream();
        try
        {
            while (session.Socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult res;
                try { res = await session.Socket.ReceiveAsync(buffer, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }

                if (res.MessageType == WebSocketMessageType.Close) break;
                acc.Write(buffer, 0, res.Count);
                if (!res.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(acc.ToArray());
                acc.SetLength(0);
                var delta = CollaborativeState.Deserialize(json);
                if (delta is not null) IngestRemote(delta, session.Id);
            }
        }
        catch (Exception ex) { Program.Log("Collab.Receive", ex); }
        finally { CloseSession(session, "receive end"); Notify(); }
    }

    private void CloseSession(CollabSession session, string reason)
    {
        _sessions.TryRemove(session.Id, out _);
        try { session.Outbound.Writer.TryComplete(); } catch { /* 무시 */ }
        try
        {
            if (session.Socket.State == WebSocketState.Open)
                _ = session.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
        }
        catch { /* 무시 */ }
        try { session.Socket.Dispose(); } catch { /* 무시 */ }
    }

    // ── CRDT 병합 ↔ 타임라인 미러링 ───────────────────────────────────────────

    /// <summary>원격 델타 병합 — LWW 승자면 로컬 타임라인에 반영하고 나머지 세션으로 릴레이한다.</summary>
    private void IngestRemote(TimelineDelta delta, string fromSessionId)
    {
        Interlocked.Increment(ref _received);
        bool won;
        lock (_gate) won = _crdt.Apply(delta);
        if (!won) { Interlocked.Increment(ref _conflicts); return; }   // 더 최신 승자 보유 → 충돌 패자(무시 · 멱등)

        MirrorToTimeline(delta);
        Interlocked.Increment(ref _applied);
        Relay(CollaborativeState.Serialize(delta), exceptSessionId: fromSessionId);
        Notify();
    }

    /// <summary>승자 델타를 로컬 타임라인에 미러링(재캡처 억제 가드 아래).</summary>
    private void MirrorToTimeline(TimelineDelta d)
    {
        lock (_gate)
        {
            _applyingRemote = true;
            try
            {
                if (d.Kind == CollabOpKind.Remove) _timeline.RemoveClip(d.ClipId);
                else _timeline.UpsertClip(d.ClipId, (TimelineTrackKind)d.TrackKind, d.StartSec, d.DurationSec, d.Label, d.Accent);
                RebuildSignature();   // 미러링 결과를 캡처 기준선으로 갱신(에코 방지)
            }
            finally { _applyingRemote = false; }
        }
    }

    // ── 로컬 편집 캡처(스냅샷 diff → 델타) ─────────────────────────────────────

    private void OnLocalTimelineChanged()
    {
        if (_applyingRemote) return;   // 원격 미러링이 유발한 변경은 재브로드캐스트하지 않음
        List<TimelineDelta> deltas;
        lock (_gate)
        {
            if (_applyingRemote) return;
            deltas = CaptureLocalDeltas();
        }
        if (deltas.Count == 0) return;
        foreach (var d in deltas) Relay(CollaborativeState.Serialize(d), exceptSessionId: null);
        Notify();
    }

    /// <summary>현재 타임라인과 마지막 서명을 diff 해 변경분을 LocalUpsert/Remove 델타로 캡처(락 아래 호출).</summary>
    private List<TimelineDelta> CaptureLocalDeltas()
    {
        var deltas = new List<TimelineDelta>();
        var clips = _timeline.Clips;
        var seen = new HashSet<string>();
        foreach (var c in clips)
        {
            seen.Add(c.Id);
            var sig = Signature(c);
            if (_lastSig.TryGetValue(c.Id, out var prev) && prev == sig) continue;
            _lastSig[c.Id] = sig;
            deltas.Add(_crdt.LocalUpsert(c));
        }
        foreach (var goneId in _lastSig.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _lastSig.Remove(goneId);
            deltas.Add(_crdt.LocalRemove(goneId));
        }
        return deltas;
    }

    /// <summary>타임라인 현재 상태를 CRDT 시드로 편입 + 캡처 기준선 확립(생성 시 1회).</summary>
    private void PrimeFromTimeline()
    {
        lock (_gate)
        {
            foreach (var c in _timeline.Clips) { _crdt.LocalUpsert(c); _lastSig[c.Id] = Signature(c); }
        }
    }

    private void RebuildSignature()
    {
        _lastSig.Clear();
        foreach (var c in _timeline.Clips) _lastSig[c.Id] = Signature(c);
    }

    private static string Signature(TimelineClip c)
        => $"{(int)c.Kind}|{c.StartSec:0.###}|{c.DurationSec:0.###}|{c.Label}|{c.Accent}";

    // ── 브로드캐스트 ──────────────────────────────────────────────────────────

    /// <summary>메시지를 (지정 세션 제외) 전 세션에 릴레이한다.</summary>
    private void Relay(string message, string? exceptSessionId)
    {
        foreach (var s in _sessions.Values)
        {
            if (s.Id == exceptSessionId) continue;
            if (s.Socket.State == WebSocketState.Open) s.Outbound.Writer.TryWrite(message);
        }
    }

    private static string Short(Exception ex)
    {
        var m = ex.Message ?? "";
        return m.Length <= 120 ? m : m[..120] + "…";
    }

    public void Dispose()
    {
        try { _timeline.OnChange -= OnLocalTimelineChanged; } catch { /* 무시 */ }
        try { _cts?.Cancel(); } catch { /* 무시 */ }
        foreach (var s in _sessions.Values) CloseSession(s, "dispose");
        try { _listener?.Stop(); _listener?.Close(); } catch { /* 무시 */ }
        try { _cts?.Dispose(); } catch { /* 무시 */ }
    }
}
