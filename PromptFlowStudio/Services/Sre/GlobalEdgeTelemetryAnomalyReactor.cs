using System.Diagnostics;

namespace PromptFlowStudio.Services.Sre;

public enum TelemetryMetric : byte { Cpu, Vram, Net, Gc }

public enum AnomalyKind : byte { None, MemoryLeak, GcStorm, Runaway }

/// <summary>격리 이벤트 — 반응 시간(μs: 샘플 수신 → 격리 플래그) 포함.</summary>
public readonly record struct IsolationEvent(long Seq, int Node, AnomalyKind Kind, double AtMs, double ReactionMicros, float Value, float Z);

/// <summary>
/// [PromptFlow Studio v16.67.0] 📡 글로벌 에지 텔레메트리 비주얼라이저 &amp; 이상 진단 반응기.
/// 노드별 CPU/VRAM/Network/GC(0..1 정규화) 스트림 → 64샘플 링 히스토리(시각화) · VRAM/GC 느린 EWMA 평균 + 차분 노이즈 분산 기준선(정상 샘플만 학습) z-점수.
/// 규칙: 메모리 누수(VRAM z ≥ 5 ∧ &gt; 0.75 또는 &gt; 0.95) · GC 폭주(GC z ≥ 5 ∧ &gt; 0.2 또는 &gt; 0.5) · 런어웨이(CPU − Net &gt; 0.45 — 처리량 없는 CPU 고착).
/// 연속 2샘플 → 동일 호출 안에서 격리(서브 밀리초 · 반응 μs 실측). 쿨다운 3s + 연속 정상 10샘플 → 해제. CPU 과부하 자체는 격리하지 않음(장애 전이 방지).
/// <see cref="Ingest"/> 무할당.
/// </summary>
public sealed class GlobalEdgeTelemetryAnomalyReactor
{
    public const int MaxNodes = 64, Metrics = 4, History = 64, Warmup = 30, Strikes = 2, ReleaseSamples = 10, MaxEvents = 32;
    public const double CooldownMs = 3000;
    public const float Alpha = 0.02f, MeanAlpha = 0.005f, ZThreshold = 5f;

    private readonly object _gate = new();
    private readonly float[] _mean = new float[MaxNodes * Metrics], _var = new float[MaxNodes * Metrics], _prev = new float[MaxNodes * Metrics], _lastZ = new float[MaxNodes * Metrics];
    private readonly float[] _hist = new float[MaxNodes * Metrics * History];
    private readonly int[] _histHead = new int[MaxNodes], _samples = new int[MaxNodes], _strike = new int[MaxNodes], _healthy = new int[MaxNodes];
    private readonly bool[] _isolated = new bool[MaxNodes];
    private readonly double[] _isolatedAt = new double[MaxNodes];
    private readonly AnomalyKind[] _kind = new AnomalyKind[MaxNodes];
    private readonly IsolationEvent[] _events = new IsolationEvent[MaxEvents];
    private int _evHead, _evCount;
    private double _clock, _lastReaction, _maxReaction;
    private long _ingested, _isolations, _releases, _seq, _strikesOnly;

    public event Action? OnChange;

    public double ClockMs { get { lock (_gate) return _clock; } }
    public long Ingested { get { lock (_gate) return _ingested; } }
    public long Isolations { get { lock (_gate) return _isolations; } }
    public long Releases { get { lock (_gate) return _releases; } }
    public long SingleStrikes { get { lock (_gate) return _strikesOnly; } }
    public double LastReactionMicros { get { lock (_gate) return _lastReaction; } }
    public double MaxReactionMicros { get { lock (_gate) return _maxReaction; } }
    private int _actNode = -1;
    private AnomalyKind _actKind;

    /// <summary>1줄 요약 — 상태 필드만 기록 · getter 지연 포맷(격리 경로 문자열 0).</summary>
    public string? LastAction
    {
        get
        {
            int n; AnomalyKind k;
            lock (_gate) { n = _actNode; k = _actKind; }
            if (n < 0) return null;
            return k == AnomalyKind.None ? "✅ 노드 #" + n + " 격리 해제" : "⛔ 노드 #" + n + " " + KindLabel(k) + " 격리";
        }
    }

    public bool IsIsolated(int node) { lock (_gate) return (uint)node < MaxNodes && _isolated[node]; }
    public AnomalyKind KindOf(int node) { lock (_gate) return (uint)node < MaxNodes ? _kind[node] : AnomalyKind.None; }
    public float LastZ(int node, TelemetryMetric m) { lock (_gate) return (uint)node < MaxNodes ? _lastZ[node * Metrics + (int)m] : 0; }

    public int IsolatedCount
    {
        get { lock (_gate) { var c = 0; for (var i = 0; i < MaxNodes; i++) if (_isolated[i]) c++; return c; } }
    }

    public void Advance(double dtMs) { lock (_gate) _clock += Math.Max(0, dtMs); }

    /// <summary>샘플 수신 → 판정 → (필요 시) 즉시 격리/해제. 새로 격리되면 이상 종류 반환(무할당).</summary>
    public AnomalyKind Ingest(int node, float cpu, float vram, float net, float gc)
    {
        var t0 = Stopwatch.GetTimestamp();
        var fired = AnomalyKind.None;
        var released = false;
        lock (_gate)
        {
            if ((uint)node >= MaxNodes) return AnomalyKind.None;
            _ingested++;
            var h = _histHead[node];
            var b = node * Metrics * History;
            _hist[b + h] = cpu; _hist[b + History + h] = vram; _hist[b + 2 * History + h] = net; _hist[b + 3 * History + h] = gc;
            _histHead[node] = (h + 1) % History;
            var n = ++_samples[node];
            var o = node * Metrics;
            if (n == 1) { _mean[o] = cpu; _mean[o + 1] = vram; _mean[o + 2] = net; _mean[o + 3] = gc; _var[o + 1] = _var[o + 3] = 1e-4f; _prev[o] = cpu; _prev[o + 1] = vram; _prev[o + 2] = net; _prev[o + 3] = gc; }
            var zv = Z(o + 1, vram); var zg = Z(o + 3, gc);
            _lastZ[o + 1] = zv; _lastZ[o + 3] = zg; _lastZ[o] = cpu - net;
            var warm = n > Warmup;
            float value = 0, z = 0;
            var kind = AnomalyKind.None;
            if (vram > 0.95f || (warm && zv >= ZThreshold && vram > 0.75f)) { kind = AnomalyKind.MemoryLeak; value = vram; z = zv; }
            else if (gc > 0.5f || (warm && zg >= ZThreshold && gc > 0.2f)) { kind = AnomalyKind.GcStorm; value = gc; z = zg; }
            else if (warm && cpu - net > 0.45f) { kind = AnomalyKind.Runaway; value = cpu; z = cpu - net; }

            var p0 = _prev[o]; var p1 = _prev[o + 1]; var p2 = _prev[o + 2]; var p3 = _prev[o + 3];
            _prev[o] = cpu; _prev[o + 1] = vram; _prev[o + 2] = net; _prev[o + 3] = gc;
            if (_isolated[node])
            {
                if (kind != AnomalyKind.None) _healthy[node] = 0;
                else if (++_healthy[node] >= ReleaseSamples && _clock - _isolatedAt[node] >= CooldownMs)
                {
                    _isolated[node] = false; _kind[node] = AnomalyKind.None; _strike[node] = 0; _healthy[node] = 0;
                    _actNode = node; _actKind = AnomalyKind.None;
                    _releases++; released = true;
                }
            }
            else if (kind != AnomalyKind.None)
            {
                if (++_strike[node] >= Strikes)
                {
                    _isolated[node] = true; _kind[node] = kind; _isolatedAt[node] = _clock; _healthy[node] = 0;
                    _isolations++; fired = kind;
                    _actNode = node; _actKind = kind;
                    var us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
                    _lastReaction = us; if (us > _maxReaction) _maxReaction = us;
                    _events[_evHead] = new IsolationEvent(++_seq, node, kind, _clock, us, value, z);
                    _evHead = (_evHead + 1) % MaxEvents;
                    if (_evCount < MaxEvents) _evCount++;
                }
                else _strikesOnly++;
            }
            else
            {
                _strike[node] = 0;
                Learn(o, cpu, p0); Learn(o + 1, vram, p1); Learn(o + 2, net, p2); Learn(o + 3, gc, p3);
            }
        }
        if (fired != AnomalyKind.None || released) Raise();
        return fired;
    }

    private float Z(int i, float x) => (x - _mean[i]) / MathF.Sqrt(_var[i] + 1e-5f);

    /// <summary>기준선 학습 — 평균은 느린 EWMA(드리프트 추종 지연 = 누수 검출) · 분산은 연속 차분(노이즈 수준 · 완만한 드리프트에 둔감).</summary>
    private void Learn(int i, float x, float prev)
    {
        _mean[i] += MeanAlpha * (x - _mean[i]);
        var dd = x - prev;
        _var[i] = (1 - Alpha) * _var[i] + Alpha * dd * dd * 0.5f;
    }

    /// <summary>시각화용 히스토리(오래된 → 최신).</summary>
    public int CopyHistory(int node, TelemetryMetric m, Span<float> dst)
    {
        lock (_gate)
        {
            if ((uint)node >= MaxNodes) return 0;
            var cnt = Math.Min(Math.Min(dst.Length, History), _samples[node]);
            var b = node * Metrics * History + (int)m * History;
            var head = _histHead[node];
            for (var i = 0; i < cnt; i++) dst[i] = _hist[b + (head - cnt + i + History) % History];
            return cnt;
        }
    }

    public float Latest(int node, TelemetryMetric m)
    {
        lock (_gate)
        {
            if ((uint)node >= MaxNodes || _samples[node] == 0) return 0;
            return _hist[node * Metrics * History + (int)m * History + (_histHead[node] - 1 + History) % History];
        }
    }

    /// <summary>최근 격리 이벤트(최신 우선).</summary>
    public int CopyEvents(Span<IsolationEvent> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _evCount);
            for (var i = 0; i < n; i++) dst[i] = _events[(_evHead - 1 - i + MaxEvents * 2) % MaxEvents];
            return n;
        }
    }

    public static string KindLabel(AnomalyKind k) => k switch
    {
        AnomalyKind.MemoryLeak => "메모리 누수",
        AnomalyKind.GcStorm => "GC 폭주",
        AnomalyKind.Runaway => "CPU 런어웨이",
        _ => "정상",
    };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sre.Reactor.OnChange", ex); }
    }
}
