using System.Buffers.Binary;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.ProductionCrew;

namespace PromptFlowStudio.Services.LiveTelemetry;

/// <summary>
/// [PromptFlow Studio v16.94.0] 📊 AI 실시간 텔레메트리 &amp; 자율 튜닝 스튜디오 러너(10 Hz 표본 · 1 Hz 튜닝).
/// 선택 워크로드: 결정적 크기 시퀀스(2~192 KB)로 <see cref="AutonomousPerformanceSelfTuningSynthesizer.RentChunk"/> + 선택 SIMD 폭 커널 → 버퍼/폭 튜닝 폐루프 시연.
/// OTA 샌드박스: %LocalAppData%/PromptFlowStudio/ota/{cloud, edge-desktop, edge-mobile} — 실제 실행 바이너리는 교체하지 않음.
/// </summary>
public sealed class LiveTelemetryStudioRunner : IDisposable
{
    public const int Hz = 10, TuneEvery = 10;
    private static readonly int[] WorkSizes = { 2048, 8192, 24576, 65536, 98304, 131072, 196608, 49152, 16384, 4096 };

    private readonly object _gate = new();
    private readonly int _slotSample, _slotWork;
    private CancellationTokenSource? _cts;
    private int _workIdx, _otaBusy;
    private long _frame;

    public LiveTelemetryStudioRunner(IServiceProvider sp)
        : this((RealtimeTelemetryBottleneckProfilerEngine?)sp.GetService(typeof(RealtimeTelemetryBottleneckProfilerEngine)) ?? new RealtimeTelemetryBottleneckProfilerEngine(),
               null, (HybridCloudEdgeDeploymentRecoveryGuard?)sp.GetService(typeof(HybridCloudEdgeDeploymentRecoveryGuard)) ?? new HybridCloudEdgeDeploymentRecoveryGuard(), sp) { }

    public LiveTelemetryStudioRunner(RealtimeTelemetryBottleneckProfilerEngine profiler, AutonomousPerformanceSelfTuningSynthesizer? synth, HybridCloudEdgeDeploymentRecoveryGuard ota, IServiceProvider? sp = null, string? otaRoot = null)
    {
        Profiler = profiler;
        Synth = synth ?? (sp?.GetService(typeof(AutonomousPerformanceSelfTuningSynthesizer)) as AutonomousPerformanceSelfTuningSynthesizer)
                ?? new AutonomousPerformanceSelfTuningSynthesizer(profiler, sp?.GetService(typeof(RealtimeProductionSwarmExecutionGuard)) as RealtimeProductionSwarmExecutionGuard);
        Ota = ota;
        _slotSample = profiler.Register("텔레메트리 표본");
        _slotWork = profiler.Register("적응 워크로드");
        if (sp is not null) GaugesBound = profiler.RegisterStudios(sp);
        OtaRoot = otaRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "ota");
    }

    public event Action? OnChange;

    public RealtimeTelemetryBottleneckProfilerEngine Profiler { get; }
    public AutonomousPerformanceSelfTuningSynthesizer Synth { get; }
    public HybridCloudEdgeDeploymentRecoveryGuard Ota { get; }
    public int GaugesBound { get; }
    public bool IsRunning => _cts is not null;
    public bool Workload { get; set; } = true;
    public long Frame { get { lock (_gate) return _frame; } }
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public string OtaRoot { get; }
    public string CloudRoot => Path.Combine(OtaRoot, "cloud");
    public int ReleaseVersion { get; private set; }
    public bool OtaBusy => Volatile.Read(ref _otaBusy) != 0;

    /// <summary>1틱(무할당): 표본(+게이지) · 워크로드 · 1 Hz 튜닝.</summary>
    public void TickOnce()
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            RuntimeSample s;
            using (Profiler.Measure(_slotSample)) s = Profiler.Sample();
            if (Workload) RunWorkload();
            if (++_frame % TuneEvery == 0) Synth.Tune(s);
            LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    private void RunWorkload()
    {
        using var _ = Profiler.Measure(_slotWork);
        var need = WorkSizes[_workIdx]; _workIdx = (_workIdx + 1) % WorkSizes.Length;
        var buf = Synth.RentChunk(need);
        try
        {
            var floats = MemoryMarshal.Cast<byte, float>(buf.AsSpan(0, need & ~3));
            floats.Clear();
            var kind = Synth.SimdLanes switch { 1 => 0, 4 => 1, _ => 2 };
            AutonomousPerformanceSelfTuningSynthesizer.SumSquares(kind, floats);
        }
        finally { Synth.Return(buf); }
    }

    public void Start()
    {
        if (_cts is not null) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _cts, cts, null) is not null) { cts.Dispose(); return; }
        Synth.CalibrateSimd();
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 텔레메트리 수집 · SIMD 폭 " + Synth.SimdLanes;
        Raise();
    }

    public void Stop()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Telemetry.Tick", ex); }
                Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    // ── OTA 샌드박스 ──
    public void EnsureSandbox()
    {
        Directory.CreateDirectory(CloudRoot);
        Ota.AddNode("edge-desktop", Path.Combine(OtaRoot, "edge-desktop"));
        Ota.AddNode("edge-mobile", Path.Combine(OtaRoot, "edge-mobile"));
        if (!File.Exists(Path.Combine(CloudRoot, "runtime.bin"))) { SeedRelease(CloudRoot); ReleaseVersion = 1; }
        else if (ReleaseVersion == 0) ReleaseVersion = 1;
    }

    /// <summary>결정적 릴리스 시드(바이너리 256 KB · 모델 128 KB · 설정 JSON).</summary>
    public static void SeedRelease(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "config"));
        Directory.CreateDirectory(Path.Combine(root, "models"));
        File.WriteAllBytes(Path.Combine(root, "runtime.bin"), Pseudo(256 * 1024, 1u));
        File.WriteAllBytes(Path.Combine(root, "models", "weights.bin"), Pseudo(128 * 1024, 7u));
        File.WriteAllText(Path.Combine(root, "config", "settings.json"), "{\"version\":1,\"bufferChunk\":65536,\"simd\":\"auto\"}");
    }

    /// <summary>다음 릴리스 게시(runtime.bin 블록 2개 변경 · 설정 버전 갱신).</summary>
    public static void MutateRelease(string root, int version)
    {
        var p = Path.Combine(root, "runtime.bin");
        var b = File.ReadAllBytes(p);
        for (var k = 0; k < 2; k++)
        {
            var off = ((version * 7 + k * 13) % (b.Length / HybridCloudEdgeDeploymentRecoveryGuard.BlockSize)) * HybridCloudEdgeDeploymentRecoveryGuard.BlockSize;
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(off), version * 1000 + k);
        }
        File.WriteAllBytes(p, b);
        File.WriteAllText(Path.Combine(root, "config", "settings.json"), "{\"version\":" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"bufferChunk\":65536,\"simd\":\"auto\"}");
    }

    private static byte[] Pseudo(int n, uint seed)
    {
        var b = new byte[n]; var x = seed * 2654435761u + 1u;
        for (var i = 0; i < n; i++) { x ^= x << 13; x ^= x >> 17; x ^= x << 5; b[i] = (byte)x; }
        return b;
    }

    public void PublishNext() => RunOta(() => { EnsureSandbox(); ReleaseVersion++; MutateRelease(CloudRoot, ReleaseVersion); LastAction = "☁ 릴리스 v" + ReleaseVersion + " 게시"; });

    public void SyncAll() => RunOta(() =>
    {
        EnsureSandbox();
        Ota.SyncAll(CloudRoot, ReleaseVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var p = Ota.LastPackage;
        LastAction = (Ota.LastAction ?? "동기화") + (p is null ? "" : " · 델타 " + (p.PayloadBytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) + " KB / " + (p.TotalBytes / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) + " KB");
    });

    public void Rollback(int node) => RunOta(() => { if (!Ota.RollbackLatest(Ota.Node(node))) LastAction = "체크포인트 없음"; else LastAction = Ota.LastAction; });

    private void RunOta(Action a)
    {
        if (Interlocked.Exchange(ref _otaBusy, 1) != 0) return;
        _ = Task.Run(() =>
        {
            try { a(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) { LastAction = "⚠ OTA 실패: " + ex.Message; PromptFlowStudio.Program.Log("Telemetry.Ota", ex); }
            finally { Volatile.Write(ref _otaBusy, 0); Raise(); }
        });
        Raise();
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Telemetry.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Synth.RestoreThreadPool();
    }
}
