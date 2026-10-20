using System;
using System.Collections.Generic;

namespace PromptFlowStudio.Services.Telemetry;

/// <summary>
/// 고정용량 원형 버퍼 — <see cref="Add"/> 는 재사용 배열에 덮어쓰기만 하므로 안정상태(용량 도달 후) 힙 할당 0.
///   스냅샷(<see cref="SnapshotNewestFirst"/>)을 요청한 순간에만 결과 배열 1회 할당(UI 바인딩 등 소비 시점 전용).
///   내부 락으로 스레드 세이프 — 쓰기(타이머 틱)와 읽기(HUD 스냅샷)가 다른 스레드에서 동시에 와도 안전.
/// </summary>
public sealed class CircularBuffer<T>
{
    private readonly T[] _items;
    private readonly object _gate = new();
    private int _head;    // 다음에 쓸 위치
    private int _count;

    public CircularBuffer(int capacity) => _items = new T[Math.Max(1, capacity)];

    public int Capacity => _items.Length;
    public int Count { get { lock (_gate) return _count; } }

    public void Add(T item)
    {
        lock (_gate)
        {
            _items[_head] = item;
            _head = (_head + 1) % _items.Length;
            if (_count < _items.Length) _count++;
        }
    }

    public void Clear()
    {
        lock (_gate) { Array.Clear(_items, 0, _items.Length); _head = 0; _count = 0; }
    }

    /// <summary>최신순 스냅샷 — <paramref name="take"/> 이 0 이하면 전체(최대 <see cref="Capacity"/>)를 반환.</summary>
    public T[] SnapshotNewestFirst(int take = 0)
    {
        lock (_gate)
        {
            var n = take > 0 ? Math.Min(take, _count) : _count;
            var result = new T[n];
            for (var i = 0; i < n; i++)
            {
                var idx = ((_head - 1 - i) % _items.Length + _items.Length) % _items.Length;
                result[i] = _items[idx];
            }
            return result;
        }
    }
}

/// <summary>OTLP 관제 집계 스냅샷 — HUD 바인딩 전용 불변 뷰(윈도우 기준 성공/실패·PII 마스킹 총량·서킷 상태).</summary>
public sealed record OtlpVisualizerSnapshot(
    int WindowSize, int Sent, int Failed, double SuccessRatePct, int TotalMaskedFields, double AvgElapsedMs,
    ExportCircuitState CurrentState, int TransitionCount, DateTimeOffset At);

/// <summary>
/// [PromptFlow Studio v10.5.0-F4] OTLP 로그 리포트 시각화 프로바이더 — <see cref="ProductionTelemetryExporter"/> 의
///   배치/서킷 이력(내부 <see cref="CircularBuffer{T}"/>, Zero-Alloc 안정상태)을 구독해 최근 N건(기본
///   <see cref="DefaultWindowSize"/>=50)의 성공/실패율·PII 마스킹 총량·평균 소요시간·서킷브레이커(Closed/Open/HalfOpen)
///   현재 상태 및 전이 건수를 집계, HUD 바인딩용 불변 스냅샷(<see cref="OtlpVisualizerSnapshot"/>)으로 노출한다.
///   ▸ Exporter.OnChange 는 매 데모 틱(1초)마다 발화되지만 배치/전이 건수가 실제로 늘지 않은 호출(빈 버퍼 no-op 등)은
///     재집계·재방송을 생략해 불필요한 HUD 리렌더를 억제한다.
/// </summary>
public sealed class OtlpTelemetryVisualizerProvider : IDisposable
{
    public const int DefaultWindowSize = 50;

    private readonly ProductionTelemetryExporter _exporter;
    private readonly object _gate = new();
    private OtlpVisualizerSnapshot _snapshot;
    private int _lastBatchCountSeen = -1, _lastTransitionCountSeen = -1;
    private long _updates;
    private bool _disposed;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public long Updates => System.Threading.Interlocked.Read(ref _updates);

    public OtlpTelemetryVisualizerProvider(ProductionTelemetryExporter exporter)
    {
        _exporter = exporter;
        _snapshot = Aggregate(Array.Empty<ExportBatchResult>(), Array.Empty<CircuitTransitionView>(), ExportCircuitState.Closed);
        _exporter.OnChange += OnExporterChanged;
    }

    /// <summary>최신 집계 스냅샷(불변) — HUD 는 이 참조 하나만 읽으면 됨.</summary>
    public OtlpVisualizerSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    private void OnExporterChanged()
    {
        var batches = _exporter.RecentBatches;          // 이미 최신순(Exporter 자체 CircularBuffer 스냅샷)
        var transitions = _exporter.RecentTransitions;
        // 실질 변화(배치/전이 건수 증가) 없으면 재집계·재방송 생략 — 1초 폴링성 틱의 불필요 리렌더 억제.
        if (batches.Count == _lastBatchCountSeen && transitions.Count == _lastTransitionCountSeen) return;
        _lastBatchCountSeen = batches.Count; _lastTransitionCountSeen = transitions.Count;

        var windowed = Window(batches, DefaultWindowSize);
        var windowedTransitions = Window(transitions, DefaultWindowSize);
        var snap = Aggregate(windowed, windowedTransitions, _exporter.Circuit.State);
        lock (_gate) { _snapshot = snap; LastAction = $"집계 갱신 · 윈도우 {windowed.Count}건 · 성공률 {snap.SuccessRatePct:0.#}%"; }
        System.Threading.Interlocked.Increment(ref _updates);
        OnChange?.Invoke();
    }

    private static IReadOnlyList<T> Window<T>(IReadOnlyList<T> src, int n)
    {
        if (src.Count <= n) return src;
        var arr = new T[n];
        for (var i = 0; i < n; i++) arr[i] = src[i];   // src 는 이미 최신순 — 앞에서부터 n개만 취함
        return arr;
    }

    private static OtlpVisualizerSnapshot Aggregate(IReadOnlyList<ExportBatchResult> batches, IReadOnlyList<CircuitTransitionView> transitions, ExportCircuitState current)
    {
        var sent = 0; var failed = 0; var masked = 0; var elapsedTotal = 0.0; var counted = 0;
        for (var i = 0; i < batches.Count; i++)
        {
            var b = batches[i];
            if (b.RecordCount == 0) continue;   // 신규 레코드 없는 빈 플러시(유휴 틱·서킷Open 유예)는 실배치가 아니므로 성공률 집계 제외
            counted++;
            if (b.Sent) sent++; else failed++;
            masked += b.MaskedFields;
            elapsedTotal += b.ElapsedMs;
        }
        var total = sent + failed;
        var rate = total > 0 ? sent * 100.0 / total : 100.0;
        var avgMs = counted > 0 ? elapsedTotal / counted : 0.0;
        return new OtlpVisualizerSnapshot(counted, sent, failed, Math.Round(rate, 1), masked, Math.Round(avgMs, 2), current, transitions.Count, DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exporter.OnChange -= OnExporterChanged;
    }
}
