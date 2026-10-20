using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Sre;

/// <summary>
/// [PromptFlow Studio v16.67.0] 🌐 글로벌 가동률 &amp; SRE 관제 스튜디오 러너 — 30Hz 실시간 루프 × 시간 배속(1~120×) · 10ms 제어 서브스텝.
/// 가용률(9 개수) 이력 링 120칸. <see cref="TickOnce"/> 무할당.
/// </summary>
public sealed class GlobalOperationsGridStudioRunner : IDisposable
{
    public const int TickHz = 30, HistoryLength = 120, MaxSubsteps = 400;
    public const double SubstepMs = 10;

    private readonly object _gate = new();
    private readonly AutonomousAiSreGridAgent _grid;
    private readonly float[] _ninesHist = new float[HistoryLength], _burnHist = new float[HistoryLength];
    private CancellationTokenSource? _cts;
    private int _histHead, _timeScale = 10;
    private long _frameSeq;
    private double _carry;

    public GlobalOperationsGridStudioRunner(IServiceProvider sp)
        : this((AutonomousAiSreGridAgent?)sp.GetService(typeof(AutonomousAiSreGridAgent)) ?? new AutonomousAiSreGridAgent()) { }

    public GlobalOperationsGridStudioRunner(AutonomousAiSreGridAgent grid) => _grid = grid;

    public event Action? OnChange;

    public AutonomousAiSreGridAgent Grid => _grid;
    public bool IsRunning => _cts is not null;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public string? LastAction { get; private set; }

    public int TimeScale
    {
        get => Volatile.Read(ref _timeScale);
        set => Volatile.Write(ref _timeScale, Math.Clamp(value, 1, 120));
    }

    /// <summary>실시간 dt × 배속 → 10ms 서브스텝 제어 틱(무할당).</summary>
    public int TickOnce(double realDtMs)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Sre.Runner.Tick");
            _carry += realDtMs * TimeScale;
            var steps = 0;
            while (_carry >= SubstepMs && steps < MaxSubsteps) { _grid.Tick(SubstepMs); _carry -= SubstepMs; steps++; }
            if (steps == MaxSubsteps) _carry = 0;
            _ninesHist[_histHead] = (float)_grid.Nines;
            _burnHist[_histHead] = (float)_grid.Budget.Current.BurnShort;
            _histHead = (_histHead + 1) % HistoryLength;
            Interlocked.Increment(ref _frameSeq);
            return steps;
        }
    }

    public int CopyHistory(Span<float> nines, Span<float> burn)
    {
        lock (_gate)
        {
            var n = Math.Min(Math.Min(nines.Length, burn.Length), HistoryLength);
            for (var i = 0; i < n; i++)
            {
                var k = (_histHead + HistoryLength - n + i) % HistoryLength;
                nines[i] = _ninesHist[k]; burn[i] = _burnHist[k];
            }
            return n;
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 글로벌 SRE 관제 라이브";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / TickHz));
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var now = Stopwatch.GetTimestamp();
                var dt = Math.Min(200.0, (now - last) * 1000.0 / Stopwatch.Frequency);
                last = now;
                try { TickOnce(dt); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Sre.Tick", ex); }
                if ((FrameSeq & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sre.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
