using System;
using System.Collections.Generic;
using System.Threading;

namespace PromptFlowStudio.Services.Agents;

public enum SentinelSignal { Fps, DropRate, QueueDepth, GpuLoad, LatencyMs }

public enum SentinelAnomalyKind { Overload, FrameDrop, PipelineStall, LatencySpike }

/// <summary>심각도 — Early(추세 투영 사전 경보) → Warn → Critical(자율 세션 교체).</summary>
public enum SentinelSeverity { Early, Warn, Critical }

public enum SentinelSessionState { Active, Standby, Quarantined }

public sealed record SentinelEvent(DateTimeOffset At, SentinelAnomalyKind Kind, SentinelSeverity Severity, SentinelSignal Signal, double Value, double Score, string Session, string Action);

public sealed record SentinelSessionView(string Id, string Tier, SentinelSessionState State, int Incidents, double CooldownSec);

public readonly record struct SentinelSignalView(SentinelSignal Signal, double Last, double Mean, double Std, double Slope);

/// <summary>
/// v9.6.3 실시간 이상 감지 센티널 — 신호별 고정 링버퍼(무할당 push)·EWMA 평균/분산 z-score·최근 10샘플 선형회귀 기울기로
/// 과부하·프레임 드랍·파이프라인 정체·지연 스파이크를 감지하고, 임계 교차를 수평선(5틱) 투영으로 사전 경보한다.
/// Critical 판정 시 활성 렌더 세션을 격리(쿨다운)하고 건강한 대기 세션으로 자율 교체, 대기 소진 시 스로틀링으로 Graceful.
/// </summary>
public sealed class RealTimeAnomalySentinel : IDisposable
{
    public const int Capacity = 120;
    public const int TickMs = 250;
    public const double TargetFps = 30;
    private const int Horizon = 5;
    private const double CooldownSec = 20;
    private const int MaxEvents = 32;
    private static readonly int SignalCount = Enum.GetValues<SentinelSignal>().Length;

    private sealed class Ring
    {
        public readonly double[] Values = new double[Capacity];
        public int Head;
        public int Count;
        public double Mean;
        public double Var;
        // 직전 샘플 흡수 전 통계 — z-score 는 스파이크 자신이 섞이기 전 기준선으로 산출(흡수 후 z ≤ 2 상한 회피)
        public double PrevMean;
        public double PrevVar;

        public void Push(double v)
        {
            Values[Head] = v;
            Head = (Head + 1) % Capacity;
            if (Count < Capacity) Count++;
            PrevMean = Mean;
            PrevVar = Var;
            if (Count == 1) { Mean = v; Var = 0; PrevMean = v; PrevVar = 0; return; }
            const double a = 0.2;
            var d = v - Mean;
            Mean += a * d;
            Var = (1 - a) * (Var + a * d * d);
        }

        public double At(int back) => Values[(Head - 1 - back + Capacity * 2) % Capacity];

        public double Last => Count == 0 ? 0 : At(0);

        public double Slope(int n)
        {
            n = Math.Min(n, Count);
            if (n < 3) return 0;
            double sx = 0, sy = 0, sxy = 0, sxx = 0;
            for (var i = 0; i < n; i++)
            {
                double x = i, y = At(n - 1 - i);
                sx += x; sy += y; sxy += x * y; sxx += x * x;
            }
            var den = n * sxx - sx * sx;
            return den == 0 ? 0 : (n * sxy - sx * sy) / den;
        }

        public void Reset() { Head = 0; Count = 0; Mean = 0; Var = 0; PrevMean = 0; PrevVar = 0; }
    }

    private sealed class Session
    {
        public Session(string id, string tier) { Id = id; Tier = tier; }
        public string Id { get; }
        public string Tier { get; }
        public SentinelSessionState State = SentinelSessionState.Standby;
        public int Incidents;
        public DateTimeOffset QuarantinedUntil;
    }

    private enum DemoFault { None, Overload, FrameDrop, Stall }

    private readonly object _gate = new();
    private readonly Ring[] _rings;
    private readonly List<Session> _sessions = new();
    private readonly SentinelEvent[] _events = new SentinelEvent[MaxEvents];
    private readonly SentinelSeverity?[] _raised = new SentinelSeverity?[4];
    private readonly Random _rng = new(9601);
    private System.Threading.Timer? _timer;
    private int _eventHead, _eventCount;
    private Session _active;
    private DemoFault _fault;
    private string? _faultSession;
    private int _faultTick;
    private long _ticks, _switches, _early, _warn, _critical, _throttled;
    private bool _disposed;

    public event Action? OnChange;

    public RealTimeAnomalySentinel()
    {
        _rings = new Ring[SignalCount];
        for (var i = 0; i < _rings.Length; i++) _rings[i] = new Ring();
        _sessions.Add(new Session("render-A", "GPU 주 세션"));
        _sessions.Add(new Session("render-B", "GPU 보조 세션"));
        _sessions.Add(new Session("render-C", "CPU 폴백 세션"));
        _active = _sessions[0];
        _active.State = SentinelSessionState.Active;
    }

    public bool DemoRunning => _timer is not null;
    public string? LastAction { get; private set; }
    public long Ticks => Interlocked.Read(ref _ticks);
    public long Switches => Interlocked.Read(ref _switches);
    public long EarlyCount => Interlocked.Read(ref _early);
    public long WarnCount => Interlocked.Read(ref _warn);
    public long CriticalCount => Interlocked.Read(ref _critical);
    public long ThrottleCount => Interlocked.Read(ref _throttled);
    public string ActiveSession { get { lock (_gate) return _active.Id; } }
    public string? ActiveFault { get { lock (_gate) return _fault == DemoFault.None ? null : _fault.ToString(); } }

    public SentinelSignalView Signal(SentinelSignal s)
    {
        lock (_gate)
        {
            var r = _rings[(int)s];
            return new SentinelSignalView(s, r.Last, r.Mean, Math.Sqrt(r.Var), r.Slope(10));
        }
    }

    public IReadOnlyList<SentinelSessionView> Sessions
    {
        get
        {
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                var list = new List<SentinelSessionView>(_sessions.Count);
                foreach (var s in _sessions)
                    list.Add(new SentinelSessionView(s.Id, s.Tier, s.State, s.Incidents,
                        s.State == SentinelSessionState.Quarantined ? Math.Max(0, (s.QuarantinedUntil - now).TotalSeconds) : 0));
                return list;
            }
        }
    }

    /// <summary>최신순 이벤트.</summary>
    public IReadOnlyList<SentinelEvent> Events
    {
        get
        {
            lock (_gate)
            {
                var list = new List<SentinelEvent>(_eventCount);
                for (var i = 0; i < _eventCount; i++) list.Add(_events[(_eventHead - 1 - i + MaxEvents) % MaxEvents]);
                return list;
            }
        }
    }

    /// <summary>외부 텔레메트리 주입(링버퍼 push, 무할당).</summary>
    public void Ingest(SentinelSignal signal, double value)
    {
        lock (_gate) _rings[(int)signal].Push(value);
    }

    /// <summary>한 틱 분의 샘플 주입 후 평가.</summary>
    public void Evaluate()
    {
        lock (_gate) EvaluateLocked();
        OnChange?.Invoke();
    }

    public void StartDemo()
    {
        lock (_gate)
        {
            if (_timer is not null || _disposed) return;
            _timer = new System.Threading.Timer(_ => DemoTick(), null, 0, TickMs);
            LastAction = $"데모 텔레메트리 기동 · {TickMs}ms 틱";
        }
        OnChange?.Invoke();
    }

    public void StopDemo()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            LastAction = "데모 정지";
        }
        OnChange?.Invoke();
    }

    public void InjectOverload() => Inject(DemoFault.Overload, "GPU 과부하 램프");
    public void InjectFrameDrop() => Inject(DemoFault.FrameDrop, "프레임 드랍 램프");
    public void InjectStall() => Inject(DemoFault.Stall, "파이프라인 정체 램프");

    private void Inject(DemoFault fault, string label)
    {
        lock (_gate)
        {
            _fault = fault;
            _faultSession = _active.Id;
            _faultTick = 0;
            LastAction = $"{_active.Id} ← {label} 주입";
        }
        OnChange?.Invoke();
    }

    /// <summary>격리 해제·결함 제거·링 초기화.</summary>
    public void ResetAll()
    {
        lock (_gate)
        {
            foreach (var r in _rings) r.Reset();
            foreach (var s in _sessions) { s.State = SentinelSessionState.Standby; s.Incidents = 0; }
            _active = _sessions[0];
            _active.State = SentinelSessionState.Active;
            _fault = DemoFault.None;
            _faultSession = null;
            Array.Clear(_raised);
            _eventCount = _eventHead = 0;
            LastAction = "센티널 초기화";
        }
        OnChange?.Invoke();
    }

    private void DemoTick()
    {
        lock (_gate)
        {
            if (_timer is null) return;
            double fps = TargetFps + Noise(1), drop = Math.Max(0, 0.004 + Noise(0.002)), queue = Math.Max(0, 4 + Noise(1.5)),
                   gpu = 60 + Noise(4), lat = 40 + Noise(5);
            // 결함은 주입 당시 세션에 귀속 — 교체되면 정상 신호로 회복
            if (_fault != DemoFault.None && _faultSession == _active.Id)
            {
                var t = ++_faultTick;
                switch (_fault)
                {
                    case DemoFault.Overload: gpu = Math.Min(100, 70 + 3 * t); lat += t * 4; break;
                    case DemoFault.FrameDrop: drop = 0.008 * t; fps = Math.Max(1, TargetFps - 1.4 * t); break;
                    case DemoFault.Stall: queue = 4 + 3 * t; fps = Math.Max(1, TargetFps - 0.5 * t); break;
                }
            }
            _rings[(int)SentinelSignal.Fps].Push(fps);
            _rings[(int)SentinelSignal.DropRate].Push(drop);
            _rings[(int)SentinelSignal.QueueDepth].Push(queue);
            _rings[(int)SentinelSignal.GpuLoad].Push(gpu);
            _rings[(int)SentinelSignal.LatencyMs].Push(lat);
            EvaluateLocked();
        }
        OnChange?.Invoke();
    }

    private void EvaluateLocked()
    {
        _ticks++;
        var now = DateTimeOffset.UtcNow;
        foreach (var s in _sessions)
            if (s.State == SentinelSessionState.Quarantined && s.QuarantinedUntil <= now) s.State = SentinelSessionState.Standby;

        var gpu = _rings[(int)SentinelSignal.GpuLoad];
        var fps = _rings[(int)SentinelSignal.Fps];
        var drop = _rings[(int)SentinelSignal.DropRate];
        var queue = _rings[(int)SentinelSignal.QueueDepth];
        var lat = _rings[(int)SentinelSignal.LatencyMs];

        // 과부하: 3연속 ≥95 Critical, ≥90 Warn, 투영 ≥95 Early
        var gpuSlope = gpu.Slope(10);
        Raise(SentinelAnomalyKind.Overload, SentinelSignal.GpuLoad, gpu.Last, gpuSlope,
            Sustained(gpu, 3, 95) ? SentinelSeverity.Critical
            : gpu.Last >= 90 ? SentinelSeverity.Warn
            : gpu.Last >= 80 && gpu.Last + gpuSlope * Horizon >= 95 ? SentinelSeverity.Early : null);

        // 프레임 드랍: 드랍률 2연속 ≥8% 또는 fps < 60% Critical
        var dropSlope = drop.Slope(10);
        Raise(SentinelAnomalyKind.FrameDrop, SentinelSignal.DropRate, drop.Last, dropSlope,
            Sustained(drop, 2, 0.08) || (fps.Count > 0 && fps.Last < TargetFps * 0.6) ? SentinelSeverity.Critical
            : drop.Last >= 0.03 ? SentinelSeverity.Warn
            : drop.Last >= 0.015 && drop.Last + dropSlope * Horizon >= 0.08 ? SentinelSeverity.Early : null);

        // 정체: 큐 ≥48 Critical, 6연속 증가 + fps 비상승 Warn, 투영 ≥48 Early
        var qSlope = queue.Slope(10);
        Raise(SentinelAnomalyKind.PipelineStall, SentinelSignal.QueueDepth, queue.Last, qSlope,
            queue.Last >= 48 ? SentinelSeverity.Critical
            : Rising(queue, 6) && fps.Slope(10) <= 0 ? SentinelSeverity.Warn
            : queue.Last >= 12 && queue.Last + qSlope * Horizon >= 48 ? SentinelSeverity.Early : null);

        // 지연 스파이크: >250ms Critical, EWMA z > 3.5 Warn
        var z = lat.PrevVar > 1e-9 ? (lat.Last - lat.PrevMean) / Math.Sqrt(lat.PrevVar) : 0;
        Raise(SentinelAnomalyKind.LatencySpike, SentinelSignal.LatencyMs, lat.Last, z,
            lat.Last > 250 ? SentinelSeverity.Critical : lat.Count > 20 && z > 3.5 ? SentinelSeverity.Warn : null);
    }

    private void Raise(SentinelAnomalyKind kind, SentinelSignal signal, double value, double score, SentinelSeverity? severity)
    {
        var k = (int)kind;
        var sessionId = _active.Id;
        if (severity is null) { _raised[k] = null; return; }
        // 디바운스: 동일/하위 심각도 반복은 무시, 상향 시에만 발화
        if (_raised[k] is { } prev && prev >= severity.Value) return;
        _raised[k] = severity;
        string action;
        switch (severity.Value)
        {
            case SentinelSeverity.Early: _early++; action = "사전 경보 — 추세 투영 임계 교차"; break;
            case SentinelSeverity.Warn: _warn++; action = "경고 — 관측 강화"; break;
            default: _critical++; action = SwitchSessionLocked(kind); break;
        }
        _events[_eventHead] = new SentinelEvent(DateTimeOffset.UtcNow, kind, severity.Value, signal, Math.Round(value, 3), Math.Round(score, 3), sessionId, action);
        _eventHead = (_eventHead + 1) % MaxEvents;
        if (_eventCount < MaxEvents) _eventCount++;
        LastAction = $"{kind} {severity} · {action}";
    }

    private string SwitchSessionLocked(SentinelAnomalyKind kind)
    {
        var from = _active;
        Session? next = null;
        foreach (var s in _sessions)
            if (s != from && s.State == SentinelSessionState.Standby) { next = s; break; }
        if (next is null)
        {
            _throttled++;
            return "대기 세션 소진 — 스로틀링(프레임 스킵) 유지";
        }
        from.Incidents++;
        from.State = SentinelSessionState.Quarantined;
        from.QuarantinedUntil = DateTimeOffset.UtcNow.AddSeconds(CooldownSec);
        next.State = SentinelSessionState.Active;
        _active = next;
        _switches++;
        // 신규 세션 기준선 재수립
        foreach (var r in _rings) r.Reset();
        Array.Clear(_raised);
        return $"자율 교체 {from.Id} → {next.Id} ({kind})";
    }

    private static bool Sustained(Ring r, int n, double threshold)
    {
        if (r.Count < n) return false;
        for (var i = 0; i < n; i++) if (r.At(i) < threshold) return false;
        return true;
    }

    private static bool Rising(Ring r, int n)
    {
        if (r.Count <= n) return false;
        for (var i = 0; i < n; i++) if (r.At(i) <= r.At(i + 1)) return false;
        return true;
    }

    private double Noise(double amp) => (_rng.NextDouble() * 2 - 1) * amp;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
