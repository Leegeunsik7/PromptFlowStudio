using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Nexus;

/// <summary>
/// [PromptFlow Studio v16.70.0] 🌌 옴니모달 넥서스 마스터 스튜디오 러너.
/// 30Hz 루프(<see cref="TickOnce"/> 무할당): 장면 그래프 · 3DGS 입자 · BCI 채널 · 에이전트 텔레메트리 소스 → 메시 텐서 적재 →
/// 32틱마다 저랭크 재분해 → 최신 프레임 랭크 k 계수 + 잔차 이상 점수를 텔레메트리 계층으로, 포즈 · 오디오 · 영상 · 3DGS 조각을 각 계층으로
/// 넥서스 스트리머에 송출. 1-Click 싱귤래리티는 오케스트레이터에 위임. 입력 소스는 결정적 합성 신호(라이브 엔진 프레임 결선 전)이다.
/// </summary>
public sealed class OmniModalNexusStudioRunner : IDisposable
{
    public const int Hz = 30, RefactorEvery = 32, Particles = 4096, BciChannels = 32, Agents = 24, SceneNodes = 48;

    private readonly object _gate = new();
    private readonly QuantumHybridNeuralMeshEngine _mesh;
    private readonly AutonomousCreativeSingularityOrchestrator _orch;
    private readonly SpatiotemporalCloudNexusStreamer _stream;
    private readonly float[] _scene = new float[SceneNodes], _gauss = new float[Particles], _bci = new float[BciChannels], _agents = new float[Agents];
    private readonly float[] _frame = new float[QuantumHybridNeuralMeshEngine.Features], _coeffs = new float[QuantumHybridNeuralMeshEngine.MaxRank];
    private readonly byte[] _pkt = new byte[12 * 1024];
    private readonly float[] _residualTrail = new float[96];
    private CancellationTokenSource? _cts;
    private long _tick;
    private int _busy, _trailHead;
    private float _anomaly;
    private bool _inject;

    public OmniModalNexusStudioRunner(IServiceProvider sp)
        : this((QuantumHybridNeuralMeshEngine?)sp.GetService(typeof(QuantumHybridNeuralMeshEngine)) ?? new QuantumHybridNeuralMeshEngine(),
               (AutonomousCreativeSingularityOrchestrator?)sp.GetService(typeof(AutonomousCreativeSingularityOrchestrator)) ?? new AutonomousCreativeSingularityOrchestrator(sp),
               (SpatiotemporalCloudNexusStreamer?)sp.GetService(typeof(SpatiotemporalCloudNexusStreamer)) ?? new SpatiotemporalCloudNexusStreamer()) { }

    public OmniModalNexusStudioRunner(QuantumHybridNeuralMeshEngine mesh, AutonomousCreativeSingularityOrchestrator orch, SpatiotemporalCloudNexusStreamer stream)
    {
        _mesh = mesh; _orch = orch; _stream = stream;
    }

    public event Action? OnChange;

    public QuantumHybridNeuralMeshEngine Mesh => _mesh;
    public AutonomousCreativeSingularityOrchestrator Orchestrator => _orch;
    public SpatiotemporalCloudNexusStreamer Streamer => _stream;
    public bool IsRunning => _cts is not null;
    public bool IsBusy => Volatile.Read(ref _busy) != 0;
    public long Ticks => Interlocked.Read(ref _tick);
    public float Anomaly { get { lock (_gate) return _anomaly; } }
    public string? LastAction { get; private set; }

    /// <summary>다음 틱들에 BCI · 에이전트 이상 패턴 주입(잔차 점수 검증용).</summary>
    public bool InjectAnomaly { get { lock (_gate) return _inject; } set { lock (_gate) _inject = value; } }

    public int CopyResidualTrail(Span<float> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _residualTrail.Length);
            for (var i = 0; i < n; i++) dst[i] = _residualTrail[(_trailHead + _residualTrail.Length - n + i) % _residualTrail.Length];
            return n;
        }
    }

    /// <summary>1틱(무할당): 소스 합성 → 텐서 적재 → (주기적) 재분해 → 계수 · 잔차 → 계층별 송출.</summary>
    public float TickOnce()
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Nexus.Runner.Tick");
            var t = _tick / (float)Hz;
            for (var i = 0; i < SceneNodes; i++) _scene[i] = MathF.Sin(0.7f * t + i * 0.13f) * (1 + (i & 3) * 0.25f);
            for (var i = 0; i < Particles; i++) _gauss[i] = 0.5f + 0.5f * MathF.Sin(1.1f * t + (i >> 8) * 0.4f);
            for (var c = 0; c < BciChannels; c++) _bci[c] = 0.8f * MathF.Sin(2 * MathF.PI * 10 * t + c * 0.2f) + 0.3f * MathF.Sin(2 * MathF.PI * 1.7f * t + c);
            for (var a = 0; a < Agents; a++) _agents[a] = 0.4f + 0.3f * MathF.Sin(0.4f * t + a * 0.5f);
            if (_inject)
            {
                for (var c = 0; c < BciChannels; c += 3) _bci[c] += 4f * ((c & 4) == 0 ? 1 : -1);
                for (var a = 1; a < Agents; a += 5) _agents[a] = -3f;
            }
            _mesh.PushFrame(_scene, _gauss, _bci, _agents);
            if (_tick % RefactorEvery == RefactorEvery - 1 || _mesh.Rank == 0 && _tick > 4) _mesh.Factorize();
            _mesh.CopyLatest(_frame);
            var k = _mesh.Encode(_frame, _coeffs);
            _anomaly = _mesh.Rank > 0 ? _mesh.Residual(_frame) : 0;
            _residualTrail[_trailHead] = _anomaly;
            _trailHead = (_trailHead + 1) % _residualTrail.Length;

            // 계층별 페이로드(제어 16B · 포즈 64B · 텔레메트리 계수 · 오디오 960B · 영상 6KB · 3DGS 12KB).
            var tickNo = (uint)_tick;
            var p = _pkt.AsSpan();
            BinaryPrimitives.WriteUInt32LittleEndian(p, tickNo); p[4..16].Clear();
            _stream.Enqueue(NexusLayer.Control, p[..16]);
            MemoryMarshal.AsBytes(_scene.AsSpan(0, 16)).CopyTo(p);
            _stream.Enqueue(NexusLayer.Pose, p[..64]);
            BinaryPrimitives.WriteInt32LittleEndian(p, k);
            BinaryPrimitives.WriteSingleLittleEndian(p[4..], _anomaly);
            MemoryMarshal.AsBytes(_coeffs.AsSpan(0, k)).CopyTo(p[8..]);
            _stream.Enqueue(NexusLayer.Telemetry, p[..(8 + k * 4)]);
            for (var i = 0; i < 960; i++) p[i] = (byte)(128 + 100 * _bci[i % BciChannels] / 5);
            _stream.Enqueue(NexusLayer.Audio, p[..960]);
            for (var i = 0; i < 6144; i++) p[i] = (byte)(tickNo + i * 7);
            _stream.Enqueue(NexusLayer.Video, p[..6144]);
            MemoryMarshal.AsBytes(_gauss.AsSpan(0, 3072)).CopyTo(p);
            _stream.Enqueue(NexusLayer.Splat, p[..12288]);
            _stream.Flush();
            _tick++;
            return _anomaly;
        }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 넥서스 메시 · 스트림 가동";
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 넥서스 정지";
        Raise();
    }

    /// <summary>1-Click 자율 싱귤래리티(백그라운드 · 중복 실행 차단).</summary>
    public async Task<SingularityRun?> CreateAsync(string scenario)
    {
        if (Interlocked.Exchange(ref _busy, 1) != 0) return null;
        Raise();
        try
        {
            var run = await Task.Run(() => _orch.Run(scenario)).ConfigureAwait(false);
            LastAction = _orch.LastAction;
            return run;
        }
        finally { Volatile.Write(ref _busy, 0); Raise(); }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Nexus.Tick", ex); }
                if ((Ticks & 3) == 0) Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Nexus.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
