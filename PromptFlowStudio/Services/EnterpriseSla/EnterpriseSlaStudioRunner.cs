using System.Buffers.Binary;

namespace PromptFlowStudio.Services.EnterpriseSla;

/// <summary>주입 장애 종류(1-Click 시뮬레이터).</summary>
public enum SlaFault : byte { None, Exception, Hang, Spin, Deadlock, Memory }

/// <summary>
/// 샌드박스 레플리카(시뮬레이션): 상태 = 처리 카운터 + 체크섬(16 B). 주입 장애는 실제 스레드 동작으로 재현 —
/// 예외 throw · ManualResetEventSlim 무한 대기(멈춤) · Beat만 하는 스핀(무한 루프) · 1 MB×N 밸러스트(메모리). Quarantine이 모두 해제.
/// </summary>
public sealed class SimulatedReplica : IFailoverReplica
{
    private readonly ManualResetEventSlim _quarantine = new(false);
    private byte[][]? _ballast;
    private int _throwLeft, _mode;

    public SimulatedReplica(string name) => Name = name;

    public string Name { get; }
    public long Counter { get; private set; }
    public ulong Checksum { get; private set; }
    public long Resets { get; private set; }
    public bool Quarantined => _quarantine.IsSet;
    public long ResourceBytes => _ballast is { } b ? (long)b.Length * (1 << 20) : 0;

    public void ArmThrow(int n) => Volatile.Write(ref _throwLeft, n);
    public void Arm(SlaFault f) => Volatile.Write(ref _mode, (int)f);

    /// <summary>1 단위 작업 — 정상 true · 격리로 풀려난 경우 false.</summary>
    public bool Step(ThreadDeadlockAutoProbingEngine engine, int slot, int ballastMb)
    {
        if (_quarantine.IsSet) return false;
        if (Volatile.Read(ref _throwLeft) > 0) { Interlocked.Decrement(ref _throwLeft); throw new InvalidOperationException("주입된 런타임 예외(" + Name + ")"); }
        switch ((SlaFault)Interlocked.Exchange(ref _mode, 0))
        {
            case SlaFault.Hang:
                _quarantine.Wait(30_000);
                return false;
            case SlaFault.Spin:
                var t0 = Environment.TickCount64;
                while (!_quarantine.IsSet && Environment.TickCount64 - t0 < 30_000) { engine.Beat(slot); Thread.SpinWait(400); }
                return false;
            case SlaFault.Memory:
                var b = new byte[ballastMb][];
                for (var i = 0; i < b.Length; i++) { b[i] = new byte[1 << 20]; b[i][i & 1023] = 1; }
                _ballast = b;
                break;
        }
        Counter++;
        Checksum = (Checksum ^ (ulong)Counter) * 0x100000001B3UL;
        engine.Beat(slot);
        return true;
    }

    public int SaveState(Span<byte> dst)
    {
        if (dst.Length < 16) return -1;
        BinaryPrimitives.WriteInt64LittleEndian(dst, Counter);
        BinaryPrimitives.WriteUInt64LittleEndian(dst[8..], Checksum);
        return 16;
    }

    public bool LoadState(ReadOnlySpan<byte> src)
    {
        if (src.Length < 16) return false;
        Counter = BinaryPrimitives.ReadInt64LittleEndian(src);
        Checksum = BinaryPrimitives.ReadUInt64LittleEndian(src[8..]);
        return true;
    }

    public void Quarantine()
    {
        _ballast = null;
        _quarantine.Set();
    }

    public bool Reset()
    {
        _quarantine.Reset();
        Volatile.Write(ref _throwLeft, 0);
        Volatile.Write(ref _mode, 0);
        _ballast = null;
        Resets++;
        return true;
    }
}

/// <summary>
/// [PromptFlow Studio v16.95.0] 🛡️ AI 엔터프라이즈 SLA &amp; 무중단 복구 스튜디오 러너.
/// 서브시스템 4종 × 전용 워커 스레드(25 Hz 작업 · 4회마다 LKG 체크포인트) · 워치독 20 ms · 10 Hz 유지 보수/매니페스트 미러링.
/// 교착 주입 = 인접 2개 레인이 프로브 락 A/B를 반대 순서로 실제 Monitor 획득(랑데부) → 대기-소유 사이클.
/// </summary>
public sealed class EnterpriseSlaStudioRunner : IDisposable
{
    public const int Lanes = 4, Hz = 10, WorkIntervalMs = 40, CheckpointEvery = 4;
    public static readonly string[] LaneNames = { "추론 파이프라인", "렌더 큐", "오디오 합성", "웹훅 디스패처" };
    public static readonly string[] FaultNames = { "없음", "런타임 예외", "멈춤(Hang)", "무한 루프", "락 교착", "메모리 고갈" };

    private readonly int[] _slot = new int[Lanes], _sub = new int[Lanes], _pending = new int[Lanes], _dlRole = new int[Lanes];
    private readonly SimulatedReplica[] _rep = new SimulatedReplica[Lanes * 2];
    private readonly Thread?[] _workers = new Thread?[Lanes];
    private readonly ManualResetEventSlim[] _rendezvous = { new(false), new(false) };
    private readonly int _lockA, _lockB;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private long _frame;

    public EnterpriseSlaStudioRunner(IServiceProvider sp)
        : this((ThreadDeadlockAutoProbingEngine?)sp.GetService(typeof(ThreadDeadlockAutoProbingEngine)) ?? new ThreadDeadlockAutoProbingEngine(),
               (ZeroDowntimeProcessFailoverSynthesizer?)sp.GetService(typeof(ZeroDowntimeProcessFailoverSynthesizer)) ?? new ZeroDowntimeProcessFailoverSynthesizer(),
               sp.GetService(typeof(EnterpriseSlaAvailabilityGuard)) as EnterpriseSlaAvailabilityGuard) { }

    public EnterpriseSlaStudioRunner(ThreadDeadlockAutoProbingEngine engine, ZeroDowntimeProcessFailoverSynthesizer synth, EnterpriseSlaAvailabilityGuard? guard = null, double timeoutMs = 250)
    {
        Engine = engine;
        Synth = synth;
        Guard = guard ?? new EnterpriseSlaAvailabilityGuard(engine, synth);
        for (var i = 0; i < Lanes; i++)
        {
            _rep[i * 2] = new SimulatedReplica(LaneNames[i] + " R0");
            _rep[i * 2 + 1] = new SimulatedReplica(LaneNames[i] + " R1");
            _slot[i] = engine.Register(LaneNames[i], timeoutMs);
            _sub[i] = synth.Register(LaneNames[i], _rep[i * 2], _rep[i * 2 + 1], 64);
            Guard.Bind(_slot[i], _sub[i]);
        }
        _lockA = engine.RegisterLock("공유 락 A(모델 캐시)");
        _lockB = engine.RegisterLock("공유 락 B(출력 버스)");
        Guard.OnChange += Raise;
    }

    public event Action? OnChange;

    public ThreadDeadlockAutoProbingEngine Engine { get; }
    public ZeroDowntimeProcessFailoverSynthesizer Synth { get; }
    public EnterpriseSlaAvailabilityGuard Guard { get; }
    public bool IsRunning => _cts is not null;
    public long Frame => Interlocked.Read(ref _frame);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int BallastMb { get; set; } = 160;
    public long HeapBudgetHeadroomMb { get; set; } = 96;
    public int LockA => _lockA;
    public int LockB => _lockB;

    public int Slot(int lane) => _slot[lane];
    public int Sub(int lane) => _sub[lane];
    public SimulatedReplica Replica(int lane, int i) => _rep[lane * 2 + i];
    public SimulatedReplica ActiveReplica(int lane) => _rep[lane * 2 + Synth.ActiveIndex(_sub[lane])];
    public SlaFault Pending(int lane) => (SlaFault)Volatile.Read(ref _pending[lane]);

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null) return;
            var cts = new CancellationTokenSource();
            _cts = cts;
            Engine.HeapBudgetBytes = GC.GetTotalMemory(false) + HeapBudgetHeadroomMb * 1024 * 1024;
            Engine.Start(20);
            Guard.AttachGlobal();
            Guard.Arm();
            for (var i = 0; i < Lanes; i++)
            {
                var t = new Thread(WorkerLoop) { IsBackground = true, Name = "PF.SLA.Lane" + i };
                _workers[i] = t;
                t.Start(i);
            }
            _ = Task.Run(() => LoopAsync(cts.Token));
            LastAction = "▶ SLA 감시 시작 · 워커 " + Lanes + " · 워치독 " + Engine.IntervalMs + " ms";
        }
        Raise();
    }

    public void Stop()
    {
        CancellationTokenSource? c;
        lock (_gate) { c = _cts; _cts = null; }
        if (c is null) return;
        c.Cancel();
        for (var i = 0; i < _rep.Length; i++) _rep[i].Quarantine();
        _rendezvous[0].Set(); _rendezvous[1].Set();
        for (var i = 0; i < Lanes; i++) { _workers[i]?.Join(2000); _workers[i] = null; }
        for (var i = 0; i < _rep.Length; i++) if (_rep[i].Quarantined) _rep[i].Reset();
        Engine.Stop();
        Engine.HeapBudgetBytes = 0;
        Guard.Disarm();
        c.Dispose();
        LastAction = "⏸ SLA 감시 정지";
        Raise();
    }

    private void WorkerLoop(object? o)
    {
        var lane = (int)o!;
        var ct = _cts?.Token ?? new CancellationToken(true);
        long n = 0;
        while (!ct.IsCancellationRequested)
        {
            try { if (Cycle(lane) && ++n % CheckpointEvery == 0) Synth.Checkpoint(_sub[lane]); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Sla.Worker", ex); }
            ct.WaitHandle.WaitOne(WorkIntervalMs);
        }
    }

    /// <summary>레인 1회 작업(활성 레플리카 · 감시 스코프 · 예외 경계).</summary>
    public bool Cycle(int lane)
    {
        var sub = _sub[lane];
        if (!Synth.TryAcquire(sub, out var r, out var epoch)) return false;
        var fault = (SlaFault)Interlocked.Exchange(ref _pending[lane], 0);
        var rep = (SimulatedReplica)r;
        switch (fault)
        {
            case SlaFault.Exception: rep.ArmThrow(Synth.FailureThreshold); break;
            case SlaFault.Hang: case SlaFault.Spin: case SlaFault.Memory: rep.Arm(fault); break;
        }
        using (Engine.Enter(_slot[lane]))
        {
            if (fault == SlaFault.Deadlock) return Guard.Run(sub, epoch, r, new LaneArgs(this, lane), static (_, a) => a.Runner.DeadlockStep(a.Lane));
            return Guard.Run(sub, epoch, r, new LaneArgs(this, lane), static (x, a) => ((SimulatedReplica)x).Step(a.Runner.Engine, a.Runner._slot[a.Lane], a.Runner.BallastMb));
        }
    }

    private readonly record struct LaneArgs(EnterpriseSlaStudioRunner Runner, int Lane);

    /// <summary>교착 재현: 역할 0 = A→B · 역할 1 = B→A, 첫 락 보유 후 랑데부 → 상호 대기 사이클.</summary>
    private bool DeadlockStep(int lane)
    {
        var role = _dlRole[lane];
        var slot = _slot[lane];
        var first = role == 0 ? _lockA : _lockB;
        var second = role == 0 ? _lockB : _lockA;
        if (!Engine.AcquireLock(slot, first, 3000)) return false;
        try
        {
            _rendezvous[role].Set();
            for (var k = 0; k < 50 && !_rendezvous[1 - role].IsSet; k++) { Engine.Beat(slot); _rendezvous[1 - role].Wait(20); }
            if (!Engine.AcquireLock(slot, second, 10_000)) return false;
            Engine.ReleaseLock(slot, second);
            Engine.Beat(slot);
            return true;
        }
        finally { Engine.ReleaseLock(slot, first); }
    }

    /// <summary>1-Click 장애 주입(다음 작업 주기에 활성 레플리카로 적용).</summary>
    public void Inject(int lane, SlaFault f)
    {
        if (f == SlaFault.Deadlock)
        {
            var p = (lane + 1) % Lanes;
            _rendezvous[0].Reset(); _rendezvous[1].Reset();
            _dlRole[lane] = 0; _dlRole[p] = 1;
            Volatile.Write(ref _pending[p], (int)f);
        }
        Volatile.Write(ref _pending[lane], (int)f);
        LastAction = "💥 " + LaneNames[lane] + " ← " + FaultNames[(int)f] + " 주입";
        Raise();
    }

    public void ForceFailover(int lane)
    {
        Synth.ForceFailover(_sub[lane]);
        Guard.Tick();
        LastAction = Guard.LastAction;
        Raise();
    }

    /// <summary>10 Hz 유지 보수 틱: 브레이커 HalfOpen 프로브 · 매니페스트 미러링 · 가용성 표본.</summary>
    public void TickOnce()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Synth.Maintain();
        Guard.HandleFindings();
        Guard.Tick();
        Interlocked.Increment(ref _frame);
        LastTickMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Hz));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { TickOnce(); }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("Sla.Tick", ex); }
                Raise();
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sla.Runner.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        Guard.OnChange -= Raise;
    }
}
