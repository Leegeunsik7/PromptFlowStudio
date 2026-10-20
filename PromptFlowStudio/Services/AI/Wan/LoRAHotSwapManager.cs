using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>메인 샘플링 루프의 진행 상태 — Pause/Resume 게이트(<see cref="LoRAHotSwapManager.WaitIfPausedAsync"/>)의 근거.
///   ⚠ 원 지시서는 "MarginCircuitBreaker 응용"을 요청했으나 이 코드베이스 전체에 그런 이름의 클래스는 없다(전수 검색
///   확인) — 실패 트립 기반 서킷브레이커(Closed/Open/HalfOpen, <see cref="PromptFlowStudio.Services.Telemetry.ExportCircuitState"/> 등과 동일
///   계열)는 "네트워크/작업 실패 누적 시 자동 개방"이 핵심인데, LoRA 스왑은 실패 반응이 아니라 자발적 유지보수
///   창(maintenance window)이라 의미가 다르다 — 그 오해를 반복하지 않도록 이 전용 3상태로 명시했다.</summary>
public enum SamplingLoopState { Running, PausedForSwap, Resuming }

public enum LoRaSwapKind { Merge, Unmerge }

/// <summary>인메모리에 유지 중인 LoRA 가중치 1건.</summary>
public sealed record LoRaWeight(string Id, double Strength, bool Merged);

/// <summary>핫스왑 1회 실행 결과.</summary>
public sealed record LoRaSwapResult(bool Ok, string LoRaId, LoRaSwapKind Kind, double PauseMs, string Note);

/// <summary>
/// [PromptFlow Studio v10.6.0-F3] LoRA 가중치 무중단 핫스왑 매니저 — 기본 가중치에 LoRA 델타를 인메모리로
///   병합(Merge)/해제(Unmerge)한다. 스왑 진행 중에는 <see cref="SamplingLoopState.PausedForSwap"/> 을 게시해
///   <see cref="WaitIfPausedAsync"/> 를 협조적으로 폴링하는 메인 샘플링 루프(<see cref="WanPostProcessingEngine.SubmitFrameWithMotionAsync"/>)
///   를 짧게 대기시키고, 완료 즉시 <see cref="SamplingLoopState.Running"/> 으로 복귀시킨다.
///   ▸ 실 텐서 병합 연산은 스텁(<see cref="SimulatedMergeMs"/> 대표 지연) — 결선점은 이 클래스의 <see cref="SwapAsync"/>
///     내부이며, 상위(호출부) API 는 실 백엔드 도입 시에도 무변경.
/// </summary>
public sealed class LoRAHotSwapManager
{
    private const int SimulatedMergeMs = 60;

    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task> _mergeWork;
    private readonly Dictionary<string, LoRaWeight> _active = new(StringComparer.Ordinal);
    private SamplingLoopState _state = SamplingLoopState.Running;
    private long _merges, _unmerges, _pauses;
    private double _lastPauseMs;

    public event Action? OnChange;
    public string? LastAction { get; private set; }
    public SamplingLoopState LoopState { get { lock (_gate) return _state; } }
    public IReadOnlyList<LoRaWeight> ActiveLoRAs { get { lock (_gate) return new List<LoRaWeight>(_active.Values); } }
    public long Merges => Interlocked.Read(ref _merges);
    public long Unmerges => Interlocked.Read(ref _unmerges);
    public long Pauses => Interlocked.Read(ref _pauses);
    public double LastPauseMs { get { lock (_gate) return _lastPauseMs; } }

    public LoRAHotSwapManager() : this(ct => Task.Delay(SimulatedMergeMs, ct)) { }

    /// <summary>병합 작업 자체를 주입 - 테스트가 스왑 구간을 게이트로 붙잡아 실시간 지연과 경쟁하지 않고 결정적으로 검증하기 위함.
    ///   DI 는 public 생성자만 고려하므로 운영 경로는 항상 위 기본 생성자(대표 지연 스텁)를 쓴다.</summary>
    internal LoRAHotSwapManager(Func<CancellationToken, Task> mergeWork) => _mergeWork = mergeWork;

    /// <summary>메인 샘플링 루프가 매 스텝 협조적으로 호출 — Running 이면 즉시 반환(비차단), 스왑 중이면 완료까지만 대기.</summary>
    public async Task WaitIfPausedAsync(CancellationToken ct = default)
    {
        while (LoopState != SamplingLoopState.Running)
            await Task.Delay(5, ct).ConfigureAwait(false);   // 스왑은 수십ms 대라 5ms 폴링으로도 체감 지연 없음
    }

    /// <summary>기본 가중치에 LoRA 델타를 인메모리 병합 — 진행 중 메인 루프를 짧게 Pause.</summary>
    public Task<LoRaSwapResult> MergeAsync(string loraId, double strength, CancellationToken ct = default)
        => SwapAsync(loraId, strength, LoRaSwapKind.Merge, ct);

    /// <summary>병합된 LoRA 델타를 해제 — 진행 중 메인 루프를 짧게 Pause.</summary>
    public Task<LoRaSwapResult> UnmergeAsync(string loraId, CancellationToken ct = default)
        => SwapAsync(loraId, 0, LoRaSwapKind.Unmerge, ct);

    private async Task<LoRaSwapResult> SwapAsync(string loraId, double strength, LoRaSwapKind kind, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        lock (_gate) { _state = SamplingLoopState.PausedForSwap; LastAction = $"{loraId} {kind} 진행 중 — 메인 샘플링 루프 Pause"; }
        Interlocked.Increment(ref _pauses);
        OnChange?.Invoke();

        try { await _mergeWork(ct).ConfigureAwait(false); }   // 실 텐서 병합 스텁(기본: 대표 지연) - 결선점
        catch (OperationCanceledException)
        {
            lock (_gate) { _state = SamplingLoopState.Running; LastAction = $"{loraId} {kind} 취소됨 — 루프 즉시 재개"; }
            OnChange?.Invoke();
            return new LoRaSwapResult(false, loraId, kind, sw.Elapsed.TotalMilliseconds, "취소됨");
        }

        lock (_gate)
        {
            if (kind == LoRaSwapKind.Merge) _active[loraId] = new LoRaWeight(loraId, strength, true);
            else _active.Remove(loraId);
            _state = SamplingLoopState.Resuming;
        }
        if (kind == LoRaSwapKind.Merge) Interlocked.Increment(ref _merges); else Interlocked.Increment(ref _unmerges);
        sw.Stop();
        lock (_gate) { _state = SamplingLoopState.Running; _lastPauseMs = sw.Elapsed.TotalMilliseconds; LastAction = $"{loraId} {kind} 완료 · Pause {sw.Elapsed.TotalMilliseconds:0.#}ms · 재개"; }
        OnChange?.Invoke();
        return new LoRaSwapResult(true, loraId, kind, sw.Elapsed.TotalMilliseconds, "완료");
    }

    public void ResetAll()
    {
        lock (_gate) { _active.Clear(); _state = SamplingLoopState.Running; _lastPauseMs = 0; LastAction = "핫스왑 매니저 초기화"; }
        Interlocked.Exchange(ref _merges, 0);
        Interlocked.Exchange(ref _unmerges, 0);
        Interlocked.Exchange(ref _pauses, 0);
        OnChange?.Invoke();
    }
}
