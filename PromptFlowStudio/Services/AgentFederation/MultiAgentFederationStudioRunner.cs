using System.Diagnostics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.AgentFederation;

/// <summary>
/// [PromptFlow Studio v16.68.0] 🤖 멀티 에이전트 연합 &amp; 지식 그래프 스튜디오 러너 — 20Hz.
/// <see cref="TickOnce"/>(무할당): 0.25s마다 작업 투입(종류 순환 · 데모 자원) · 캐릭터 궤도 이동(근접 엣지) · 버스/작업/그래프 진행.
/// 제어 평면(서명 합의)은 루프에서 5s마다 경합 시나리오(연출 ↔ VFX 카메라 제어권)로 별도 실행 — 핫패스 가드 밖.
/// </summary>
public sealed class MultiAgentFederationStudioRunner : IDisposable
{
    public const int TickHz = 20;

    private readonly object _gate = new();
    private readonly MultiAgentSwarmFederationOrchestrator _fed;
    private CancellationTokenSource? _cts;
    private long _frameSeq;
    private double _sinceSubmit, _sinceContention;
    private int _kind;
    private bool _contentionFlip;

    public MultiAgentFederationStudioRunner(IServiceProvider sp)
        : this((MultiAgentSwarmFederationOrchestrator?)sp.GetService(typeof(MultiAgentSwarmFederationOrchestrator)) ?? new MultiAgentSwarmFederationOrchestrator()) { }

    public MultiAgentFederationStudioRunner(MultiAgentSwarmFederationOrchestrator fed) => _fed = fed;

    public event Action? OnChange;

    public MultiAgentSwarmFederationOrchestrator Federation => _fed;
    public bool IsRunning => _cts is not null;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public string? LastAction { get; private set; }

    /// <summary>1틱(무할당).</summary>
    public void TickOnce(double dtMs)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Federation.Runner.Tick");
            _sinceSubmit += dtMs;
            while (_sinceSubmit >= 250)
            {
                _sinceSubmit -= 250;
                var k = (AgentTaskKind)(_kind % MultiAgentSwarmFederationOrchestrator.Kinds);
                var res = k switch
                {
                    AgentTaskKind.Shot => _fed.Shots[_kind % 4],
                    AgentTaskKind.Sound => _fed.Assets[2],
                    AgentTaskKind.Effect => _fed.Shots[(_kind + 1) % 4],
                    AgentTaskKind.Ops => _fed.SreNodes[_kind & 1],
                    _ => _fed.Assets[_kind % 4],
                };
                _fed.Submit(k, 0.3f + (_kind % 3) * 0.2f, res);
                _kind++;
            }
            var t = _fed.ClockMs / 1000.0;
            _fed.Graph.MoveNode(_fed.Characters[2], (float)(Math.Cos(t * 0.6) * 6), (float)(Math.Sin(t * 0.6) * 3), 0);
            _fed.Tick(dtMs);
            Interlocked.Increment(ref _frameSeq);
        }
    }

    /// <summary>경합 시나리오 — 연출/VFX가 S3 샷 카메라 제어권을 번갈아 강한 확신으로 요청(제어 평면).</summary>
    public ClaimOutcome RunContention()
    {
        var shot = _fed.Shots[2];
        _contentionFlip = !_contentionFlip;
        return _contentionFlip
            ? _fed.Claim((int)FedAgentRole.Director, shot, AgentTaskKind.Shot, 0.95f)
            : _fed.Claim((int)FedAgentRole.Vfx, shot, AgentTaskKind.Effect, 0.9f);
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 연합 라이브";
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
                try
                {
                    TickOnce(dt);
                    _sinceContention += dt;
                    if (_sinceContention >= 5000) { _sinceContention = 0; RunContention(); }
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or System.Security.Cryptography.CryptographicException)
                { PromptFlowStudio.Program.Log("Federation.Tick", ex); }
                if ((FrameSeq & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Federation.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
