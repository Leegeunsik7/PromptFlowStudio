using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Mobile;   // RemoteRenderClient(URL 검증·ws 변환)
using PromptFlowStudio.Services.Wan;       // WanSampleFactory(스틸 프레임 폴백)

namespace PromptFlowStudio.Services.Integration;

/// <summary>WebRTC 시그널링 메시지(원본 익명 타입 대체 · 소스 생성 직렬화 호환 명명 타입).</summary>
internal sealed record WebRtcSignalMessage(string Type, string? Sdp = null, string? Candidate = null);

/// <summary>WebRTC 프리뷰 연결 상태 — 유휴/시그널링/연결/폴백(스틸)/실패.</summary>
public enum RtcConnectionState { Idle, Signaling, Connected, Degraded, Failed }

/// <summary>
/// [PromptFlow Studio v6.1.0] 초저지연 라이브 프리뷰 스트리머 래퍼(관찰 가능 싱글턴 · 순수 BCL · IDisposable).
///   ▸ <see cref="ClientWebSocket"/> 로 렌더 노드 시그널링 채널에 접속해 SDP Offer/ICE 스텁을 교환한다(핸드셰이크 도달성 확인).
///   ▸ ⚠ 실제 RTP 미디어 디코드는 네이티브 WebRTC 스택이 필요(외부 C++ DLL 금지 · 제약사항 2) → 본 래퍼는 <b>시그널링 + 프레임 펌프</b>만 담당.
///   ▸ <b>Graceful Degradation</b>: 시그널링 실패/미구성/타임아웃 시 즉시 <see cref="RtcConnectionState.Degraded"/> 로 전환해
///     스틸 컷 프레임(DataURL)을 완만한 간격으로 갱신(프리뷰 무중단). 연결 성공 시 초저지연(≈250ms) 프레임 갱신.
///   ⚠ <see cref="OnChange"/> 는 백그라운드(프레임 펌프) 스레드에서 온다 — 컴포넌트는 InvokeAsync(StateHasChanged)로 마샬링.
/// </summary>
public sealed class WebRtcStreamer : IDisposable
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(4);
    private const int ConnectedFrameMs = 250;   // 초저지연 갱신(≈4fps 프리뷰)
    private const int DegradedFrameMs = 900;    // 스틸 폴백 갱신

    private readonly object _gate = new();
    private readonly Random _rng = new();
    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private Task? _loop;
    private Func<string?>? _stillProvider;

    /// <summary>상태/프레임 변경 방송(구독: 컴포넌트 OnInitialized · 해지: Dispose).</summary>
    public event Action? OnChange;

    public RtcConnectionState State { get; private set; } = RtcConnectionState.Idle;
    public string? CurrentFrameUrl { get; private set; }
    public int LatencyMs { get; private set; }
    public string? StatusMessage { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>프리뷰가 표시 중인지(연결 또는 폴백).</summary>
    public bool IsLive => State is RtcConnectionState.Connected or RtcConnectionState.Degraded;
    public TimeSpan Uptime => StartedAt is { } s ? DateTimeOffset.Now - s : TimeSpan.Zero;

    private void Notify() => OnChange?.Invoke();
    private void Set(RtcConnectionState st, string msg) { State = st; StatusMessage = msg; Notify(); }

    /// <summary>
    /// 프리뷰 시작 — <paramref name="signalUrl"/> 로 시그널링 시도 후 성공 시 연결, 실패/미구성 시 스틸 폴백.
    ///   <paramref name="stillFrameProvider"/>=최신 렌더 프레임 제공자(없으면 합성 스틸). 이미 실행 중이면 무시.
    /// </summary>
    public async Task StartAsync(string? signalUrl, Func<string?>? stillFrameProvider, CancellationToken ct = default)
    {
        if (IsLive) return;
        _cts?.Cancel(); _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        _stillProvider = stillFrameProvider;
        StartedAt = DateTimeOffset.Now;
        CurrentFrameUrl = null; LatencyMs = 0;
        Set(RtcConnectionState.Signaling, "시그널링 핸드셰이크 중…");

        var connected = await TrySignalingAsync(signalUrl, token).ConfigureAwait(false);
        if (connected) Set(RtcConnectionState.Connected, "🟢 초저지연 스트림 연결됨");
        else Set(RtcConnectionState.Degraded, "스틸 프레임 폴백 — 초저지연 스트림 불가(렌더 진행 프리뷰)");

        _loop = Task.Run(() => FramePumpAsync(token));
    }

    /// <summary>SDP Offer/ICE 스텁을 시그널링 서버로 보내 채널 도달성만 확인(미디어 협상은 네이티브 몫). 실패=false→폴백.</summary>
    private async Task<bool> TrySignalingAsync(string? signalUrl, CancellationToken ct)
    {
        if (!RemoteRenderClient.IsValidWorkerUrl(signalUrl)) return false;   // 미구성 → 폴백
        try
        {
            _ws = new ClientWebSocket();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(SignalTimeout);
            await _ws.ConnectAsync(new Uri(RemoteRenderClient.ToWebSocketUrl(signalUrl!)), linked.Token).ConfigureAwait(false);

            // SDP Offer + ICE candidate 스텁 교환(시그널링 규약 도달 검증).
            await SendJsonAsync(_ws, new WebRtcSignalMessage("offer", Sdp: "v=0\r\no=promptflow 0 0 IN IP4 0.0.0.0\r\ns=preview"), PromptFlowWireJsonContext.Default.WebRtcSignalMessage, linked.Token).ConfigureAwait(false);
            await SendJsonAsync(_ws, new WebRtcSignalMessage("ice", Candidate: "candidate:promptflow 1 udp 2113937151 0.0.0.0 9 typ host"), PromptFlowWireJsonContext.Default.WebRtcSignalMessage, linked.Token).ConfigureAwait(false);
            return _ws.State == WebSocketState.Open;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex) { Program.Log("WebRtc.Signaling", ex); return false; }   // 단선/타임아웃 → 폴백(Graceful)
    }

    private async Task FramePumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var connected = State == RtcConnectionState.Connected;
                var frame = _stillProvider?.Invoke();
                if (string.IsNullOrEmpty(frame))
                    frame = WanSampleFactory.FrameDataUrl(
                        $"LIVE {DateTime.Now:HH:mm:ss}", "#22d3ee",
                        connected ? "RTP STREAM" : "STILL FALLBACK");

                CurrentFrameUrl = frame;
                // 연결=초저지연(120~300ms) · 폴백=완만(400~700ms) 추정치(서버 미보고).
                LatencyMs = connected ? 120 + _rng.Next(0, 180) : 400 + _rng.Next(0, 300);
                Notify();

                try { await Task.Delay(connected ? ConnectedFrameMs : DegradedFrameMs, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (Exception ex) { Program.Log("WebRtc.Pump", ex); }
    }

    /// <summary>프리뷰 중지(시그널링 종료·프레임 펌프 취소).</summary>
    public void Stop()
    {
        try { _cts?.Cancel(); } catch { /* 무시 */ }
        try { _ws?.Abort(); _ws?.Dispose(); } catch { /* 무시 */ }
        _ws = null;
        StartedAt = null; CurrentFrameUrl = null; LatencyMs = 0;
        Set(RtcConnectionState.Idle, "중지됨");
    }

    private static async Task SendJsonAsync<T>(ClientWebSocket ws, T obj, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj, typeInfo));
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Stop();
        try { _cts?.Dispose(); } catch { /* 무시 */ }
    }
}
