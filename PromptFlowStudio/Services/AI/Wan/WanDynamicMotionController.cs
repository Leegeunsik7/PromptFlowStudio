using System;
using System.Collections.Concurrent;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

public enum PanDirection { None, Up, Down, Left, Right }
public enum ZoomDirection { None, In, Out }

/// <summary>단일 Denoising Step에 주입할 카메라 모션 벡터(불변 스냅샷).</summary>
public sealed record WanMotionVector(long Step, PanDirection Pan, double PanMagnitude, ZoomDirection Zoom, double ZoomMagnitude, double RollDegrees, DateTimeOffset At);

/// <summary>
/// [PromptFlow Studio v10.6.0-F3] 동적 카메라 모션 벡터 제어기 — Pan(상하좌우)/Zoom(In/Out)/Roll 파라미터를 실시간
///   갱신하고, 현재 Denoising Step에 비동기로 주입할 벡터를 대기열(<see cref="ConcurrentQueue{T}"/>)로 관리한다.
///   ▸ 복합 파라미터(Pan 방향+크기 등 여러 필드를 하나의 일관된 스냅샷으로 묶어야 하는 갱신)는 <c>lock</c> 으로,
///     단순 누적 카운터는 <see cref="Interlocked"/> 로, 주입 대기열 자체는 <see cref="ConcurrentQueue{T}"/> 로 —
///     그때그때 가장 맞는 스레드 세이프 기법을 쓴다(무엇이든 lock 하나로 뭉개지 않음).
///   ▸ 실 신경망 텐서에 워핑을 적용하는 대신, <see cref="WanPostProcessingEngine.SubmitFrameWithMotionAsync"/> 가
///     이 벡터를 실제로 소비해 프레임 바이트에 결정적 변형을 적용한다(계산 후 버려지는 죽은 값 금지 — v10.6.0-F1
///     보간 버그에서 얻은 교훈을 F3 설계에도 그대로 적용).
/// </summary>
public sealed class WanDynamicMotionController
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<WanMotionVector> _pending = new();
    private PanDirection _pan = PanDirection.None;
    private double _panMag;
    private ZoomDirection _zoom = ZoomDirection.None;
    private double _zoomMag;
    private double _rollDeg;
    private long _stepCounter;
    private long _injected, _enqueued;
    private WanMotionVector? _lastApplied;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public long Injected => Interlocked.Read(ref _injected);
    public long EnqueuedCount => Interlocked.Read(ref _enqueued);
    public int PendingCount => _pending.Count;
    public WanMotionVector? LastApplied { get { lock (_gate) return _lastApplied; } }

    public void SetPan(PanDirection dir, double magnitude)
    {
        lock (_gate) { _pan = dir; _panMag = Math.Clamp(magnitude, 0, 1); }
        LastAction = $"Pan 설정 · {dir} {magnitude:0.##}";
        OnChange?.Invoke();
    }

    public void SetZoom(ZoomDirection dir, double magnitude)
    {
        lock (_gate) { _zoom = dir; _zoomMag = Math.Clamp(magnitude, 0, 1); }
        LastAction = $"Zoom 설정 · {dir} {magnitude:0.##}";
        OnChange?.Invoke();
    }

    public void SetRoll(double degrees)
    {
        lock (_gate) _rollDeg = Math.Clamp(degrees, -45, 45);
        LastAction = $"Roll 설정 · {degrees:0.#}°";
        OnChange?.Invoke();
    }

    /// <summary>현재 파라미터를 스텝 벡터로 스냅샷해 주입 대기열에 적재.</summary>
    public WanMotionVector Enqueue()
    {
        WanMotionVector v;
        lock (_gate) v = new WanMotionVector(Interlocked.Increment(ref _stepCounter), _pan, _panMag, _zoom, _zoomMag, _rollDeg, DateTimeOffset.UtcNow);
        _pending.Enqueue(v);
        Interlocked.Increment(ref _enqueued);
        OnChange?.Invoke();
        return v;
    }

    /// <summary>현재 Denoising Step에 다음 대기 벡터를 주입 — 큐가 비어 있으면 현재 파라미터로 즉석 스냅샷.</summary>
    public WanMotionVector InjectNext()
    {
        var v = _pending.TryDequeue(out var q) ? q : Enqueue();
        Interlocked.Increment(ref _injected);
        lock (_gate)
        {
            _lastApplied = v;
            LastAction = $"스텝 {v.Step} 주입 · Pan={v.Pan}({v.PanMagnitude:0.##}) Zoom={v.Zoom}({v.ZoomMagnitude:0.##}) Roll={v.RollDegrees:0.#}°";
        }
        OnChange?.Invoke();
        return v;
    }

    public void ResetAll()
    {
        while (_pending.TryDequeue(out _)) { }
        lock (_gate) { _pan = PanDirection.None; _panMag = 0; _zoom = ZoomDirection.None; _zoomMag = 0; _rollDeg = 0; _lastApplied = null; LastAction = "모션 컨트롤러 초기화"; }
        Interlocked.Exchange(ref _injected, 0);
        Interlocked.Exchange(ref _enqueued, 0);
        Interlocked.Exchange(ref _stepCounter, 0);
        OnChange?.Invoke();
    }
}
