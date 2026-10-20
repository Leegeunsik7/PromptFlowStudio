using System;
using System.Buffers;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI.Wan;

namespace PromptFlowStudio.Services.AI.Vision;

public enum VisionSource : byte { Heuristic = 0, Vlm = 1 }

/// <summary>v13.1.0: HUD 표시용 VLM 경로 상태.</summary>
public enum VlmHealth : byte { Disabled = 0, Standby = 1, InFlight = 2, Online = 3, Degraded = 4, Offline = 5 }

/// <summary>화면 분석 결과 1건(값 형식).</summary>
public readonly record struct VisionSceneResult(VisionScene Scene, double Confidence, VisionSource Source, long FrameSequence, double MeanLuma, double MotionEnergy);

/// <summary>
/// [PromptFlow Studio v13.0.0] 실시간 비전 컨텍스트 분석기 — <see cref="LiveStreamVirtualCamPipe"/> 송출 프레임을 1초에 1장만
///   샘플링해 224px 이내로 박스 필터 축소하고, 즉시 BCL 휴리스틱(평균 밝기·난색 비율·프레임 간 변화량)으로 1차 분류한 뒤,
///   로컬 VLM(<see cref="IVisionLanguageClient"/>)이 있으면 비동기로 2차 확정한다.
///   ▸ 폴백 모델(헌장 5): 휴리스틱 결과는 샘플 즉시(같은 틱) 게시 → VLM 이 없거나 느려도 리액션이 멈추지 않는다. VLM 은 동시 1건만
///     (진행 중이면 그 샘플은 VLM 생략) 보강 역할.
///   ▸ Zero-Alloc 핫패스: 원본 수신 버퍼는 <see cref="Start"/> 시 ArrayPool 대여(최대 1080p RGBA) · 축소/이전 프레임 버퍼는 생성 시 1회 할당 ·
///     파이프 읽기는 값 형식 헤더 경로(<see cref="LiveStreamVirtualCamPipe.TryReadLatestFrameRaw"/>) → <see cref="Tick"/> 의 휴리스틱 경로 힙 할당 0.
///     VLM 전송(PNG·base64·HTTP)만 할당이 있다(1FPS 이하).
///   ▸ 새 프레임이 없으면(시퀀스 불변) 아무것도 하지 않는다.
/// </summary>
public sealed class VisionContextAnalyzer : IDisposable
{
    public const int SampleIntervalMs = 1000;
    public const int MaxSide = 224;

    private readonly LiveStreamVirtualCamPipe _pipe;
    private readonly IVisionLanguageClient? _vlm;
    private readonly object _gate = new();
    private readonly byte[] _small = new byte[MaxSide * MaxSide * 3];
    private readonly byte[] _prevSmall = new byte[MaxSide * MaxSide * 3];
    private readonly byte[] _vlmSmall = new byte[MaxSide * MaxSide * 3];
    private byte[]? _frame;
    private int _smallW, _smallH, _prevW, _prevH;
    private bool _hasPrev;
    private double _prevLuma;
    private long _lastSeq = -1;
    private int _vlmInFlight;
    private System.Threading.Timer? _timer;
    private int _inTick;
    private CancellationTokenSource? _cts;
    private VisionSceneResult _last;
    private bool _hasLast;
    private string? _lastVlmSummary;
    private long _samples, _skippedNoNewFrame, _vlmRequests, _vlmAccepted, _vlmFallbacks;
    // v13.1.0 백프레셔/지연 계측(전부 Interlocked 값 - 드롭 경로 할당 0)
    private long _vlmDropped, _vlmSkippedOffline, _vlmTimeouts;
    private long _lastVlmLatencyTicks, _maxVlmLatencyTicks;
    private double _avgVlmLatencyMs;
    private int _lastOutcome = (int)VlmHealth.Standby;
    private readonly TimeSpan _vlmDeadline;
    // v14.3.0 가변 샘플링: 지연 p95/EWMA·타임아웃 빈도 → 타이머 주기 100~2000ms
    private readonly AdaptiveFrameSampler _sampler = new();
    private int _timerPeriodMs = AdaptiveFrameSampler.DefaultIntervalMs;

    /// <summary>VLM 요청 1건의 하드 데드라인(샘플 시점 무관 · 요청 시작부터). 초과 시 취소 → 휴리스틱 유지 + 자원 해제.</summary>
    public static readonly TimeSpan DefaultVlmDeadline = TimeSpan.FromSeconds(5);
    private bool _disposed;

    /// <summary>분류 결과 게시(휴리스틱 즉시 + VLM 확정 시 한 번 더). 틱/VLM 완료 스레드에서 호출.</summary>
    // v14.0.1: 구독자 격리 - 호출 목록을 구독/해지 시점에만 배열로 재구성(핫패스 할당 0), 한 구독자의 예외가 틱·VLM 판정·나머지 구독자를 끊지 못하게.
    private readonly object _handlerGate = new();
    private Action<VisionSceneResult>[] _sceneHandlers = Array.Empty<Action<VisionSceneResult>>();
    private Action[] _changeHandlers = Array.Empty<Action>();
    private long _subscriberFaults;

    public event Action<VisionSceneResult>? OnScene
    {
        add { if (value is not null) lock (_handlerGate) _sceneHandlers = Added(_sceneHandlers, value); }
        remove { if (value is not null) lock (_handlerGate) _sceneHandlers = Removed(_sceneHandlers, value); }
    }

    public event Action? OnChange
    {
        add { if (value is not null) lock (_handlerGate) _changeHandlers = Added(_changeHandlers, value); }
        remove { if (value is not null) lock (_handlerGate) _changeHandlers = Removed(_changeHandlers, value); }
    }

    /// <summary>구독자 예외 누적(격리되어 로그로만 남은 건수) - 0 이 아니면 HUD/바인더 쪽 결함.</summary>
    public long SubscriberFaults => Interlocked.Read(ref _subscriberFaults);

    private static T[] Added<T>(T[] list, T h)
    {
        var n = new T[list.Length + 1];
        list.CopyTo(n, 0);
        n[^1] = h;
        return n;
    }

    private static T[] Removed<T>(T[] list, T h)
    {
        var i = Array.LastIndexOf(list, h);   // 표준 이벤트와 동일하게 마지막 등록분부터 해지
        if (i < 0) return list;
        var n = new T[list.Length - 1];
        Array.Copy(list, 0, n, 0, i);
        Array.Copy(list, i + 1, n, i, list.Length - i - 1);
        return n;
    }

    private void RaiseScene(VisionSceneResult r)
    {
        var hs = Volatile.Read(ref _sceneHandlers);
        for (var i = 0; i < hs.Length; i++)
        {
            try { hs[i](r); }
            catch (Exception ex) { Interlocked.Increment(ref _subscriberFaults); Program.Log("VisionContextAnalyzer.OnScene", ex); }
        }
    }

    private void RaiseChange()
    {
        var hs = Volatile.Read(ref _changeHandlers);
        for (var i = 0; i < hs.Length; i++)
        {
            try { hs[i](); }
            catch (Exception ex) { Interlocked.Increment(ref _subscriberFaults); Program.Log("VisionContextAnalyzer.OnChange", ex); }
        }
    }

    public VisionContextAnalyzer(LiveStreamVirtualCamPipe pipe) : this(pipe, new LocalVlmHttpClient()) { }

    internal VisionContextAnalyzer(LiveStreamVirtualCamPipe pipe, IVisionLanguageClient? vlm, TimeSpan? vlmDeadline = null)
    {
        _pipe = pipe;
        _vlm = vlm;
        _vlmDeadline = vlmDeadline is { } d && d > TimeSpan.Zero ? d : DefaultVlmDeadline;
    }

    /// <summary>VLM 이 처리 중이라 대기열 없이 즉시 버린 샘플 수(백프레셔).</summary>
    public long VlmDropped => Interlocked.Read(ref _vlmDropped);
    /// <summary>서킷 개방(엔드포인트 오프라인) 중이라 요청 자체를 생략한 샘플 수.</summary>
    public long VlmSkippedOffline => Interlocked.Read(ref _vlmSkippedOffline);
    public long VlmTimeouts => Interlocked.Read(ref _vlmTimeouts);
    public TimeSpan VlmDeadline => _vlmDeadline;
    /// <summary>프레임 샘플 시점 → VLM 결과 도착까지(E2E). 응답이 없었으면 0.</summary>
    public double LastVlmLatencyMs => Interlocked.Read(ref _lastVlmLatencyTicks) * 1000.0 / Stopwatch.Frequency;
    public double MaxVlmLatencyMs => Interlocked.Read(ref _maxVlmLatencyTicks) * 1000.0 / Stopwatch.Frequency;
    public double AvgVlmLatencyMs { get { lock (_gate) return _avgVlmLatencyMs; } }
    public AdaptiveFrameSampler Sampler => _sampler;
    /// <summary>현재 적용된 타이머 주기(ms) - 샘플러 간격이 바뀌면 다음 틱 직후 반영.</summary>
    public int TimerPeriodMs => Volatile.Read(ref _timerPeriodMs);

    private VisionActionMapper? _actionMapper;
    /// <summary>v13.3.0: VLM 확정 시 요약문을 액션(모션 스파이크·스타일 LoRA)으로 변환할 매퍼 결선(null = 해제).</summary>
    public void AttachActionMapper(VisionActionMapper? mapper) => Volatile.Write(ref _actionMapper, mapper);
    public bool ActionMapperAttached => Volatile.Read(ref _actionMapper) is not null;

    public VlmHealth VlmHealthState
    {
        get
        {
            if (_vlm is not { Enabled: true }) return VlmHealth.Disabled;
            if (Volatile.Read(ref _vlmInFlight) != 0) return VlmHealth.InFlight;
            if (_vlm is IVlmHealthProbe p && (p.CircuitOpen || p.LastCallFailed)) return VlmHealth.Offline;
            return (VlmHealth)Volatile.Read(ref _lastOutcome);
        }
    }

    public bool Running => Volatile.Read(ref _timer) is not null;
    public bool VlmEnabled => _vlm?.Enabled == true;
    public IVisionLanguageClient? Vlm => _vlm;
    public long Samples => Interlocked.Read(ref _samples);
    public long SkippedNoNewFrame => Interlocked.Read(ref _skippedNoNewFrame);
    public long VlmRequests => Interlocked.Read(ref _vlmRequests);
    public long VlmAccepted => Interlocked.Read(ref _vlmAccepted);
    public long VlmFallbacks => Interlocked.Read(ref _vlmFallbacks);
    public VisionSceneResult? LastResult { get { lock (_gate) return _hasLast ? _last : null; } }
    public string? LastVlmSummary { get { lock (_gate) return _lastVlmSummary; } }
    public (int Width, int Height) LastSampleSize { get { lock (_gate) return (_smallW, _smallH); } }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_timer is not null) return;
            _frame ??= ArrayPool<byte>.Shared.Rent(LiveStreamVirtualCamPipe.MaxPayloadBytes);
            _cts = new CancellationTokenSource();
            var period = _sampler.IntervalMs;
            _timerPeriodMs = period;
            _timer = new System.Threading.Timer(OnTimer, null, period, period);
        }
        RaiseChange();
    }

    public void Stop()
    {
        System.Threading.Timer? t;
        CancellationTokenSource? cts;
        lock (_gate) { t = _timer; _timer = null; cts = _cts; _cts = null; }
        if (t is null) return;
        t.Dispose();
        try { cts?.Cancel(); } catch { /* 무시 */ }
        cts?.Dispose();
        // _frame 은 진행 중 틱이 끝난 뒤 Dispose 에서 반환(틱과의 경합 방지 - 재시작 시 재사용)
        RaiseChange();
    }

    private void OnTimer(object? _)
    {
        if (Interlocked.CompareExchange(ref _inTick, 1, 0) != 0) return;
        try { Tick(); ApplyAdaptivePeriod(); }
        catch (Exception ex) { Program.Log("VisionContextAnalyzer.Tick", ex); }
        finally { Volatile.Write(ref _inTick, 0); }
    }

    /// <summary>샘플러 간격이 바뀌었으면 타이머 주기 교체. _gate 안에서 _timer 확인 → Stop(락 안에서 null 교체)과 경합해도 파기된 타이머 Change 없음.</summary>
    private void ApplyAdaptivePeriod()
    {
        var next = _sampler.IntervalMs;
        if (next == Volatile.Read(ref _timerPeriodMs)) return;
        lock (_gate)
        {
            if (_timer is null) return;
            _timer.Change(next, next);
            _timerPeriodMs = next;
        }
    }

    /// <summary>샘플 1회(타이머 본체 - 테스트에서 직접 호출). 새 프레임이 있어 분류했으면 true.</summary>
    public bool Tick()
    {
        if (Volatile.Read(ref _disposed)) return false;   // 파기 후 직접 호출/잔류 콜백 - 풀 버퍼 재대여 차단
        var sampledAt = Stopwatch.GetTimestamp();   // E2E 지연 기준점(프레임 샘플 시점)
        var frame = _frame ??= ArrayPool<byte>.Shared.Rent(LiveStreamVirtualCamPipe.MaxPayloadBytes);
        if (!_pipe.TryReadLatestFrameRaw(frame, out var h, out var n)) return false;
        if (h.Sequence == Volatile.Read(ref _lastSeq)) { Interlocked.Increment(ref _skippedNoNewFrame); return false; }
        Volatile.Write(ref _lastSeq, h.Sequence);

        VisionSceneResult result;
        lock (_gate)
        {
            if (!Downscale(frame.AsSpan(0, n), h.Width, h.Height, h.Format, _small, out _smallW, out _smallH)) return false;
            result = ClassifyLocked(h.Sequence);
            // 다음 틱의 변화량 비교용 이전 프레임 보관
            _small.AsSpan(0, _smallW * _smallH * 3).CopyTo(_prevSmall);
            _prevW = _smallW; _prevH = _smallH; _hasPrev = true; _prevLuma = result.MeanLuma;
            _last = result; _hasLast = true;
        }
        Interlocked.Increment(ref _samples);
        RaiseScene(result);

        if (_vlm is { Enabled: true } vlm)
        {
            if (vlm is IVlmHealthProbe { CircuitOpen: true })
            {
                Interlocked.Increment(ref _vlmSkippedOffline);   // 오프라인 - 네트워크 시도 없이 휴리스틱만(서킷이 닫히면 자동 재시도)
            }
            else if (Interlocked.Exchange(ref _vlmInFlight, 1) == 0)
            {
                int w, hh;
                lock (_gate) { w = _smallW; hh = _smallH; _small.AsSpan(0, w * hh * 3).CopyTo(_vlmSmall); }
                var ct = _cts?.Token ?? CancellationToken.None;
                _ = RunVlmAsync(w, hh, h.Sequence, result, sampledAt, ct);
            }
            else
            {
                // 백프레셔: 이미 추론 중이면 대기열에 쌓지 않고 즉시 버린다(카운터 1증가 · 할당 0) - 느린 VLM 이 메모리를 키우지 않음.
                Interlocked.Increment(ref _vlmDropped);
            }
        }
        RaiseChange();
        return true;
    }

    private async Task RunVlmAsync(int w, int h, long seq, VisionSceneResult heuristic, long sampledAt, CancellationToken ct)
    {
        // 요청별 하드 데드라인 - using 으로 타이머·링크 등록을 반드시 해제(응답이 끝내 안 와도 자원 잔류 없음).
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_vlmDeadline);
        try
        {
            Interlocked.Increment(ref _vlmRequests);
            var reply = await _vlm!.DescribeAsync(_vlmSmall.AsMemory(0, w * h * 3), w, h, deadline.Token).ConfigureAwait(false);
            var latencyTicks = Stopwatch.GetTimestamp() - sampledAt;
            RecordLatency(latencyTicks);
            _sampler.RecordLatency(latencyTicks * 1000.0 / Stopwatch.Frequency);
            if (reply is not { } r || r.Scene == VisionScene.Unknown)
            {
                Interlocked.Increment(ref _vlmFallbacks);
                Volatile.Write(ref _lastOutcome, (int)(_vlm is IVlmHealthProbe { LastCallFailed: true } ? VlmHealth.Offline : VlmHealth.Degraded));
                return;   // 휴리스틱 결과 유지
            }
            Volatile.Write(ref _lastOutcome, (int)VlmHealth.Online);
            var confirmed = new VisionSceneResult(r.Scene, r.Confidence, VisionSource.Vlm, seq, heuristic.MeanLuma, heuristic.MotionEnergy);
            lock (_gate)
            {
                _lastVlmSummary = r.Summary;
                if (_last.FrameSequence <= seq) _last = confirmed;   // 더 새 프레임 결과를 오래된 VLM 응답이 덮지 않게
            }
            Interlocked.Increment(ref _vlmAccepted);
            RaiseScene(confirmed);
            // v13.3.0: 요약문 → 액션 디스패치(스레드풀 연속 · 예외 비전파 · 쿨다운은 매퍼 내부)
            if (Volatile.Read(ref _actionMapper) is { } mapper) _ = mapper.DispatchActionAsync(r.Summary, r.Scene, ct);
            RaiseChange();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            Interlocked.Increment(ref _vlmTimeouts);   // 데드라인 초과 - 휴리스틱 유지, 다음 샘플에서 재시도
            _sampler.RecordTimeout();                  // v14.3.0: 타임아웃 빈도 상승 → 즉시 2초 주기로 완화
            Interlocked.Increment(ref _vlmFallbacks);
            Volatile.Write(ref _lastOutcome, (int)VlmHealth.Degraded);
        }
        catch (OperationCanceledException) { /* Stop */ }
        catch (Exception ex) { Interlocked.Increment(ref _vlmFallbacks); Volatile.Write(ref _lastOutcome, (int)VlmHealth.Degraded); Program.Log("VisionContextAnalyzer.Vlm", ex); }
        finally
        {
            Volatile.Write(ref _vlmInFlight, 0);
            RaiseChange();
        }
    }

    private void RecordLatency(long ticks)
    {
        Interlocked.Exchange(ref _lastVlmLatencyTicks, ticks);
        var max = Interlocked.Read(ref _maxVlmLatencyTicks);
        while (ticks > max)
        {
            var seen = Interlocked.CompareExchange(ref _maxVlmLatencyTicks, ticks, max);
            if (seen == max) break;
            max = seen;
        }
        var ms = ticks * 1000.0 / Stopwatch.Frequency;
        lock (_gate) _avgVlmLatencyMs = _avgVlmLatencyMs <= 0 ? ms : _avgVlmLatencyMs * 0.8 + ms * 0.2;
    }

    /// <summary>RGBA32 또는 NV12(Y 평면만 → 회색) 원본을 긴 변 <see cref="MaxSide"/> 이하 RGB24 로 박스 평균 축소(무할당).</summary>
    internal static bool Downscale(ReadOnlySpan<byte> src, int w, int h, VirtualCamPixelFormat fmt, Span<byte> dst, out int dw, out int dh)
    {
        dw = dh = 0;
        if (w <= 0 || h <= 0) return false;
        var bpp = fmt == VirtualCamPixelFormat.Rgba32 ? 4 : 1;
        if (src.Length < w * h * bpp) return false;
        var scale = Math.Max(1.0, Math.Max(w, h) / (double)MaxSide);
        dw = Math.Max(1, (int)(w / scale));
        dh = Math.Max(1, (int)(h / scale));
        if (dst.Length < dw * dh * 3) return false;

        for (var y = 0; y < dh; y++)
        {
            var sy0 = y * h / dh; var sy1 = Math.Max(sy0 + 1, (y + 1) * h / dh);
            for (var x = 0; x < dw; x++)
            {
                var sx0 = x * w / dw; var sx1 = Math.Max(sx0 + 1, (x + 1) * w / dw);
                int r = 0, g = 0, b = 0, cnt = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var row = sy * w;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        if (bpp == 4) { var o = (row + sx) * 4; r += src[o]; g += src[o + 1]; b += src[o + 2]; }
                        else { var yv = src[row + sx]; r += yv; g += yv; b += yv; }
                        cnt++;
                    }
                }
                var d = (y * dw + x) * 3;
                dst[d] = (byte)(r / cnt); dst[d + 1] = (byte)(g / cnt); dst[d + 2] = (byte)(b / cnt);
            }
        }
        return true;
    }

    // 휴리스틱 임계값(결정적). 밝기는 BT.601 근사 0..255.
    internal const double DarkLuma = 45, FlashJump = 55, WarmRatioForFlash = 0.35, ActionMotion = 28;

    private VisionSceneResult ClassifyLocked(long seq)
    {
        var px = _smallW * _smallH;
        long lumaSum = 0; var warm = 0;
        for (var i = 0; i < px; i++)
        {
            int r = _small[i * 3], g = _small[i * 3 + 1], b = _small[i * 3 + 2];
            lumaSum += (r * 299 + g * 587 + b * 114) / 1000;
            if (r > 180 && r > g + 20 && g > b) warm++;   // 주황/노랑 계열 고휘도(화염·섬광 근사)
        }
        var luma = (double)lumaSum / px;
        var warmRatio = (double)warm / px;

        double motion = 0;
        if (_hasPrev && _prevW == _smallW && _prevH == _smallH)
        {
            long diff = 0;
            var len = px * 3;
            for (var i = 0; i < len; i++) diff += Math.Abs(_small[i] - _prevSmall[i]);
            motion = (double)diff / len;
        }

        var (scene, conf) = Classify(luma, _hasPrev ? luma - _prevLuma : 0, warmRatio, motion);
        return new VisionSceneResult(scene, conf, VisionSource.Heuristic, seq, luma, motion);
    }

    /// <summary>순수 규칙 분류(테스트 가능). 우선순위: 섬광(급격한 밝기 상승+난색) → 어두움 → 고변화 → 정적.</summary>
    internal static (VisionScene Scene, double Confidence) Classify(double meanLuma, double lumaJump, double warmRatio, double motion)
    {
        if (lumaJump >= FlashJump && warmRatio >= WarmRatioForFlash) return (VisionScene.ExplosionFlash, Math.Min(1, 0.6 + warmRatio * 0.5));
        if (meanLuma < DarkLuma) return (VisionScene.DarkScene, Math.Min(1, 0.6 + (DarkLuma - meanLuma) / DarkLuma * 0.4));
        if (motion >= ActionMotion) return (VisionScene.HighAction, Math.Min(1, 0.55 + motion / 255));
        return (VisionScene.Calm, 0.6);
    }

    /// <summary>마지막 축소 프레임 복사(HUD 썸네일/테스트) - 반환 = 바이트 수.</summary>
    public int CopyLastSample(Span<byte> destination)
    {
        lock (_gate)
        {
            var n = Math.Min(_smallW * _smallH * 3, destination.Length);
            _small.AsSpan(0, n).CopyTo(destination);
            return n;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        // 타이머 Dispose 는 진행 중 콜백을 기다리지 않는다 - 틱 종료까지 대기 후 영구 점유(이후 콜백 즉시 반환)해야 사용 중 풀 버퍼 반환(타 대여자 오염)이 없다.
        var idle = SpinWait.SpinUntil(() => Interlocked.CompareExchange(ref _inTick, 1, 0) == 0, 1000);
        var f = Interlocked.Exchange(ref _frame, null);
        if (f is not null && idle) ArrayPool<byte>.Shared.Return(f);   // 1초 내 미종료면 반환 포기(GC 회수가 오염보다 안전)
        (_vlm as IDisposable)?.Dispose();
    }
}

/// <summary>
/// [PromptFlow Studio v13.0.0] 비전 장면 → 리액션 매핑(VISION-2) — <see cref="VisionContextAnalyzer.OnScene"/> 를 구독해
///   <see cref="StreamerReactionEngine"/> 임펄스·<see cref="WanDynamicMotionController"/> 모션 주입·Wan 프롬프트 변조를 결정적으로 적용.
///   ▸ 신뢰도 &lt; <see cref="MinConfidence"/> 무시 · 같은 장면은 <see cref="CooldownMs"/> 이내 재발동 억제(1FPS 연속 분류로 폭주 방지).
///   ▸ 프롬프트 변조: 마커 구간 <c>[vision-ctx]…[/vision-ctx]</c> 1개만 유지(멱등, 헌장 5) · <see cref="ModifierTtlMs"/> 동안 재확인 없으면 자동 해제.
///     적용점은 <see cref="PromptFlowStudio.Services.Wan.WanRenderClient.PromptModulator"/>(품질 가드레일 주입 직전, null=원본 통과).
/// </summary>
public sealed class VisionReactionBinder : IDisposable
{
    public const double MinConfidence = 0.55;
    public const int CooldownMs = 3000;
    public const int ModifierTtlMs = 10_000;
    internal const string MarkerOpen = "[vision-ctx]";
    internal const string MarkerClose = "[/vision-ctx]";

    private static readonly string?[] Modifiers =
    {
        null,                                                                   // Unknown
        null,                                                                   // Calm
        " [vision-ctx]low-key lighting, deep cinematic shadows, moody night ambience[/vision-ctx]",
        " [vision-ctx]explosive burst, bright fireball glow, debris particles, camera shake[/vision-ctx]",
        " [vision-ctx]dynamic fast motion, motion blur, energetic action framing[/vision-ctx]",
    };

    private readonly VisionContextAnalyzer _analyzer;
    private readonly StreamerReactionEngine _fx;
    private readonly WanDynamicMotionController _motion;
    private readonly Func<long> _clock;
    private readonly object _gate = new();
    private readonly long[] _lastFiredTicks = new long[5];
    private VisionScene _activeModifierScene;
    private long _modifierSetTicks;
    private long _reactions, _suppressed, _ignoredLowConfidence;
    private bool _disposed;

    public event Action? OnChange;

    public VisionReactionBinder(VisionContextAnalyzer analyzer, StreamerReactionEngine fx, WanDynamicMotionController motion)
        : this(analyzer, fx, motion, Stopwatch.GetTimestamp) { }

    internal VisionReactionBinder(VisionContextAnalyzer analyzer, StreamerReactionEngine fx, WanDynamicMotionController motion, Func<long> clock)
    {
        _analyzer = analyzer;
        _fx = fx;
        _motion = motion;
        _clock = clock;
        for (var i = 0; i < _lastFiredTicks.Length; i++) _lastFiredTicks[i] = long.MinValue / 2;
        _analyzer.OnScene += OnScene;
    }

    private void OnScene(VisionSceneResult r)
    {
        try { Apply(r); }
        catch (Exception ex) { Program.Log("VisionReactionBinder.Apply", ex); }
    }

    public long Reactions => Interlocked.Read(ref _reactions);
    public long SuppressedByCooldown => Interlocked.Read(ref _suppressed);
    public long IgnoredLowConfidence => Interlocked.Read(ref _ignoredLowConfidence);

    /// <summary>현재 유효한 프롬프트 변조 구간(없으면 null). TTL 경과 시 자동 해제.</summary>
    public string? CurrentModifier
    {
        get
        {
            lock (_gate)
            {
                if (_activeModifierScene == VisionScene.Unknown) return null;
                if (ElapsedMs(_modifierSetTicks) > ModifierTtlMs) { _activeModifierScene = VisionScene.Unknown; return null; }
                return Modifiers[(int)_activeModifierScene];
            }
        }
    }

    public VisionScene ActiveModifierScene { get { lock (_gate) return CurrentModifier is null ? VisionScene.Unknown : _activeModifierScene; } }

    private double ElapsedMs(long since) => (_clock() - since) * 1000.0 / Stopwatch.Frequency;

    /// <summary>장면 1건 적용(분석기 이벤트 경로 · 테스트 직접 호출). 리액션을 발동했으면 true.</summary>
    public bool Apply(VisionSceneResult r)
    {
        if (r.Confidence < MinConfidence) { Interlocked.Increment(ref _ignoredLowConfidence); return false; }
        if (r.Scene is VisionScene.Unknown or VisionScene.Calm) return false;

        var idx = (int)r.Scene;
        lock (_gate)
        {
            if (ElapsedMs(_lastFiredTicks[idx]) < CooldownMs) { Interlocked.Increment(ref _suppressed); return false; }
            _lastFiredTicks[idx] = _clock();
            _activeModifierScene = r.Scene;
            _modifierSetTicks = _lastFiredTicks[idx];
        }

        switch (r.Scene)
        {
            case VisionScene.ExplosionFlash:
                _fx.TriggerManual(ReactionAffect.Hype, Math.Clamp(r.Confidence, 0.6, 1.0));
                _motion.SetZoom(ZoomDirection.In, 0.8);
                _motion.SetRoll(6);   // 충격 흔들림
                break;
            case VisionScene.HighAction:
                _fx.TriggerManual(ReactionAffect.Excited, 0.6);
                _motion.SetPan(PanDirection.Right, 0.5);
                break;
            case VisionScene.DarkScene:
                _motion.SetZoom(ZoomDirection.In, 0.2);   // 느린 푸시인(긴장감) - 파티클 분출 없음
                break;
        }
        _motion.InjectNext();
        Interlocked.Increment(ref _reactions);
        OnChange?.Invoke();
        return true;
    }

    /// <summary>Wan 프롬프트에 현재 장면 변조 구간을 멱등 적용 - 기존 마커 구간은 교체/제거, 변조 없으면 기존 구간만 제거(없으면 원본 그대로).</summary>
    public string ApplyPromptModulation(string prompt) => Modulate(prompt, CurrentModifier);

    internal static string Modulate(string prompt, string? modifier)
    {
        prompt ??= string.Empty;
        var open = prompt.IndexOf(MarkerOpen, StringComparison.Ordinal);
        if (open >= 0)
        {
            var close = prompt.IndexOf(MarkerClose, open, StringComparison.Ordinal);
            if (close >= 0)
            {
                var start = open > 0 && prompt[open - 1] == ' ' ? open - 1 : open;
                prompt = prompt.Remove(start, close + MarkerClose.Length - start);
            }
        }
        return modifier is null ? prompt : prompt + modifier;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _analyzer.OnScene -= OnScene;
    }
}
