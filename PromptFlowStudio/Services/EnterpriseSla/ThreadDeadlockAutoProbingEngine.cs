using System.Diagnostics;

namespace PromptFlowStudio.Services.EnterpriseSla;

/// <summary>멈춤 요인 분류.</summary>
public enum HangKind : byte { None, Stalled, Spinning, Deadlock, Starvation, MemoryPressure }

/// <summary>
/// 프로빙 결과(무할당 값 타입). Slot = 감시 슬롯(-1 = 프로세스 전역) · Partner = 교착 사이클 다음 슬롯 · LockId = 대기 중 락 ·
/// StartTs = 멈춤 시작(마지막 진행/대기 시작) Stopwatch 타임스탬프 · Metric = 종류별 수치(메모리 = 힙 MB · 기아 = 큐 지연 ms).
/// </summary>
public readonly record struct HangFinding(long Seq, long AtMs, int Slot, HangKind Kind, double DurationMs, int ThreadId, int Partner, int LockId, int CycleLength, long StartTs, double Metric);

/// <summary>
/// [PromptFlow Studio v16.95.0] 🛡️ 실시간 스레드 교착/멈춤 자율 감지 엔진.
/// · 감시 슬롯: 작업 진입(Begin)/진행(Beat)/완료(End) 타임스탬프만 Volatile 기록(핫패스 무잠금 · 무할당).
/// · 판정: 무진행 &gt; 타임아웃 = 멈춤(Stalled) · 진행 중이나 작업 미완료 &gt; 타임아웃×SpinFactor = 무한 루프(Spinning).
/// · 락 교착: 프로브 락(Monitor.TryEnter 슬라이스) 대기-소유 그래프 사이클 추적 → 사이클 최소 슬롯에서 1회 보고 · AbortWait로 희생자 대기 해제.
/// · 스레드 풀 기아: 재사용 IThreadPoolWorkItem 핑 큐 지연 · 메모리: 힙 예산/GC 메모리 부하(콜드 주기).
/// · 워치독은 전용 스레드(스레드 풀 기아 중에도 동작). .NET Core는 타 스레드 스택 캡처 불가 → 스레드 ID · 보유/대기 락 · 진행 카운트로 요인 보고.
/// </summary>
public sealed class ThreadDeadlockAutoProbingEngine : IDisposable
{
    public const int MaxSlots = 32, MaxLocks = 32, FindingCap = 256;
    public static readonly string[] KindNames = { "정상", "멈춤(무진행)", "무한 루프(진행·미완료)", "락 교착", "스레드 풀 기아", "메모리 압박" };

    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    private readonly object _gate = new();
    private readonly string[] _names = new string[MaxSlots];
    private readonly double[] _timeoutMs = new double[MaxSlots];
    private readonly long[] _beatTs = new long[MaxSlots], _enterTs = new long[MaxSlots], _waitTs = new long[MaxSlots];
    private readonly long[] _beats = new long[MaxSlots], _completed = new long[MaxSlots], _opSeq = new long[MaxSlots], _waitSeq = new long[MaxSlots];
    private readonly long[] _stallOp = new long[MaxSlots], _spinOp = new long[MaxSlots], _deadWait = new long[MaxSlots];
    private readonly int[] _busy = new int[MaxSlots], _threadId = new int[MaxSlots], _waitLock = new int[MaxSlots], _abort = new int[MaxSlots];
    private readonly object[] _locks = new object[MaxLocks];
    private readonly string[] _lockNames = new string[MaxLocks];
    private readonly int[] _lockOwner = new int[MaxLocks];
    private readonly HangFinding[] _ring = new HangFinding[FindingCap];
    private readonly long _origin = Stopwatch.GetTimestamp();
    private readonly PoolPing _ping;
    private int _count, _lockCount, _pingOutstanding, _poolFlag, _memFlag;
    private long _seq, _probes, _abortedWaits, _lastMemTs, _pingQueuedTs;
    private Thread? _watchdog;
    private ManualResetEventSlim? _stop;

    public ThreadDeadlockAutoProbingEngine()
    {
        _ping = new PoolPing(this);
        for (var i = 0; i < MaxSlots; i++) { _waitLock[i] = -1; _stallOp[i] = -1; _spinOp[i] = -1; _deadWait[i] = -1; }
        for (var i = 0; i < MaxLocks; i++) _lockOwner[i] = -1;
    }

    /// <summary>새 발견(개수) — 워치독 스레드에서 호출(구독자 예외 격리).</summary>
    public event Action<int>? FindingsDetected;

    public double SpinFactor { get; set; } = 4;
    public bool StarvationProbe { get; set; } = true;
    public double StarvationMs { get; set; } = 500;
    public double PingIntervalMs { get; set; } = 200;
    public double MemoryIntervalMs { get; set; } = 1000;
    /// <summary>관리 힙 예산(바이트 · 0 = 끔).</summary>
    public long HeapBudgetBytes { get; set; }
    public int LockSliceMs { get; set; } = 5;
    public int IntervalMs { get; private set; } = 20;
    public bool IsWatching => _watchdog is not null;

    public int Count => Volatile.Read(ref _count);
    public int LockCount => Volatile.Read(ref _lockCount);
    public long FindingSeq => Interlocked.Read(ref _seq);
    public long Probes => Interlocked.Read(ref _probes);
    public long AbortedWaits => Interlocked.Read(ref _abortedWaits);
    public double LastProbeUs { get; private set; }
    public double PoolLatencyMs { get; private set; }
    public long HeapBytes { get; private set; }
    public double MemoryLoad { get; private set; }
    public long NowMs => (long)((Stopwatch.GetTimestamp() - _origin) * TickMs);

    public string Name(int s) => _names[s];
    public double Timeout(int s) => _timeoutMs[s];
    public bool IsBusy(int s) => Volatile.Read(ref _busy[s]) != 0;
    public long Beats(int s) => Volatile.Read(ref _beats[s]);
    public long Completed(int s) => Volatile.Read(ref _completed[s]);
    public int ThreadIdOf(int s) => Volatile.Read(ref _threadId[s]);
    public int WaitingLock(int s) => Volatile.Read(ref _waitLock[s]);
    public double OpElapsedMs(int s) => IsBusy(s) ? Ms(Stopwatch.GetTimestamp() - Volatile.Read(ref _enterTs[s])) : 0;
    public double SinceBeatMs(int s) => Ms(Stopwatch.GetTimestamp() - Volatile.Read(ref _beatTs[s]));
    public string LockName(int l) => _lockNames[l];
    public int LockOwner(int l) => Volatile.Read(ref _lockOwner[l]);

    private static double Ms(long ticks) => ticks * TickMs;

    /// <summary>감시 슬롯 등록(동일 이름 = 기존 슬롯 · alreadyRegistered).</summary>
    public int Register(string name, double timeoutMs)
    {
        lock (_gate)
        {
            for (var i = 0; i < _count; i++) if (_names[i] == name) { _timeoutMs[i] = timeoutMs; return i; }
            if (_count >= MaxSlots) throw new InvalidOperationException("감시 슬롯 상한 " + MaxSlots);
            var s = _count;
            _names[s] = name; _timeoutMs[s] = Math.Max(1, timeoutMs);
            _beatTs[s] = Stopwatch.GetTimestamp();
            Volatile.Write(ref _count, s + 1);
            return s;
        }
    }

    /// <summary>프로브 락 등록(동일 이름 = 기존).</summary>
    public int RegisterLock(string name)
    {
        lock (_gate)
        {
            for (var i = 0; i < _lockCount; i++) if (_lockNames[i] == name) return i;
            if (_lockCount >= MaxLocks) throw new InvalidOperationException("프로브 락 상한 " + MaxLocks);
            var l = _lockCount;
            _locks[l] = new object(); _lockNames[l] = name;
            Volatile.Write(ref _lockCount, l + 1);
            return l;
        }
    }

    // ── 핫패스(무잠금 · 무할당) ──
    public void Begin(int s)
    {
        var now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _threadId[s], Environment.CurrentManagedThreadId);
        Interlocked.Increment(ref _opSeq[s]);
        Volatile.Write(ref _enterTs[s], now);
        Volatile.Write(ref _beatTs[s], now);
        Volatile.Write(ref _busy[s], 1);
    }

    public void Beat(int s)
    {
        Volatile.Write(ref _beatTs[s], Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _beats[s]);
    }

    public void End(int s)
    {
        Volatile.Write(ref _busy[s], 0);
        Volatile.Write(ref _beatTs[s], Stopwatch.GetTimestamp());
        Interlocked.Increment(ref _completed[s]);
    }

    public OpScope Enter(int s) { Begin(s); return new OpScope(this, s); }

    public readonly ref struct OpScope
    {
        private readonly ThreadDeadlockAutoProbingEngine _e;
        private readonly int _s;
        internal OpScope(ThreadDeadlockAutoProbingEngine e, int s) { _e = e; _s = s; }
        public void Dispose() => _e.End(_s);
    }

    /// <summary>프로브 락 획득(Monitor.TryEnter 슬라이스) — AbortWait 또는 타임아웃 시 false.</summary>
    public bool AcquireLock(int s, int lockId, int timeoutMs = System.Threading.Timeout.Infinite)
    {
        var o = _locks[lockId];
        var t0 = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref _waitSeq[s]);
        Volatile.Write(ref _abort[s], 0);
        Volatile.Write(ref _waitTs[s], t0);
        Volatile.Write(ref _waitLock[s], lockId);
        try
        {
            while (true)
            {
                if (Monitor.TryEnter(o, LockSliceMs)) { Volatile.Write(ref _lockOwner[lockId], s); return true; }
                if (Interlocked.Exchange(ref _abort[s], 0) != 0) { Interlocked.Increment(ref _abortedWaits); return false; }
                if (timeoutMs >= 0 && Ms(Stopwatch.GetTimestamp() - t0) > timeoutMs) return false;
            }
        }
        finally { Volatile.Write(ref _waitLock[s], -1); }
    }

    public void ReleaseLock(int s, int lockId)
    {
        if (Volatile.Read(ref _lockOwner[lockId]) == s) Volatile.Write(ref _lockOwner[lockId], -1);
        Monitor.Exit(_locks[lockId]);
    }

    /// <summary>교착 희생자 대기 해제 요청(다음 슬라이스에서 AcquireLock = false).</summary>
    public void AbortWait(int s) => Volatile.Write(ref _abort[s], 1);

    // ── 프로브 ──
    /// <summary>1회 프로빙(무할당) → 새 발견 수.</summary>
    public int Probe()
    {
        lock (_gate)
        {
            var t0 = Stopwatch.GetTimestamp();
            var before = _seq;
            Interlocked.Increment(ref _probes);
            for (var s = 0; s < _count; s++) ProbeSlot(s, t0);
            ProbeDeadlocks(t0);
            if (StarvationProbe) ProbePool(t0);
            if (HeapBudgetBytes > 0) ProbeMemory(t0);
            LastProbeUs = Ms(Stopwatch.GetTimestamp() - t0) * 1000.0;
            return (int)(_seq - before);
        }
    }

    private void ProbeSlot(int s, long now)
    {
        if (Volatile.Read(ref _busy[s]) == 0 || Volatile.Read(ref _waitLock[s]) >= 0) return; // 락 대기 = 교착 프로브 담당
        var op = Volatile.Read(ref _opSeq[s]);
        var beatTs = Volatile.Read(ref _beatTs[s]);
        var enterTs = Volatile.Read(ref _enterTs[s]);
        var t = _timeoutMs[s];
        var sinceBeat = Ms(now - beatTs);
        if (sinceBeat > t)
        {
            if (_stallOp[s] == op) return;
            _stallOp[s] = op;
            Add(s, HangKind.Stalled, sinceBeat, _threadId[s], -1, -1, 0, beatTs, Beats(s));
            return;
        }
        var sinceEnter = Ms(now - enterTs);
        if (sinceEnter > t * SpinFactor && _spinOp[s] != op)
        {
            _spinOp[s] = op;
            Add(s, HangKind.Spinning, sinceEnter, _threadId[s], -1, -1, 0, enterTs, Beats(s));
        }
    }

    /// <summary>대기-소유 그래프 사이클 추적: s → owner(waitLock(s)) → … → s.</summary>
    private void ProbeDeadlocks(long now)
    {
        for (var s = 0; s < _count; s++)
        {
            var l = Volatile.Read(ref _waitLock[s]);
            if (l < 0) continue;
            int cur = s, len = 0, minSlot = s, partner = -1;
            while (len < MaxSlots)
            {
                var wl = Volatile.Read(ref _waitLock[cur]);
                if (wl < 0) break;
                var owner = Volatile.Read(ref _lockOwner[wl]);
                if (owner < 0 || owner == cur) break;
                if (len == 0) partner = owner;
                len++;
                if (owner < minSlot) minSlot = owner;
                cur = owner;
                if (cur == s) break;
            }
            if (cur != s || len < 2 || minSlot != s) continue;
            var w = Volatile.Read(ref _waitSeq[s]);
            if (_deadWait[s] == w) continue;
            _deadWait[s] = w;
            var waitTs = Volatile.Read(ref _waitTs[s]);
            Add(s, HangKind.Deadlock, Ms(now - waitTs), _threadId[s], partner, l, len, waitTs, len);
        }
    }

    private void ProbePool(long now)
    {
        if (Volatile.Read(ref _pingOutstanding) != 0)
        {
            var waited = Ms(now - _pingQueuedTs);
            if (waited > StarvationMs && _poolFlag == 0)
            {
                _poolFlag = 1;
                Add(-1, HangKind.Starvation, waited, 0, -1, -1, 0, _pingQueuedTs, ThreadPool.PendingWorkItemCount);
            }
            return;
        }
        _poolFlag = 0;
        if (Ms(now - _pingQueuedTs) < PingIntervalMs) return;
        _pingQueuedTs = now;
        Volatile.Write(ref _pingOutstanding, 1);
        ThreadPool.UnsafeQueueUserWorkItem(_ping, preferLocal: false);
    }

    private void ProbeMemory(long now)
    {
        if (_lastMemTs != 0 && Ms(now - _lastMemTs) < MemoryIntervalMs) return;
        _lastMemTs = now;
        var heap = GC.GetTotalMemory(false);
        HeapBytes = heap;
        var info = GC.GetGCMemoryInfo();
        MemoryLoad = info.HighMemoryLoadThresholdBytes > 0 ? (double)info.MemoryLoadBytes / info.HighMemoryLoadThresholdBytes : 0;
        var over = heap > HeapBudgetBytes || MemoryLoad >= 1.0;
        if (!over) { _memFlag = 0; return; }
        if (_memFlag != 0) return;
        _memFlag = 1;
        Add(-1, HangKind.MemoryPressure, 0, 0, -1, -1, 0, now, heap / (1024d * 1024d));
    }

    /// <summary>메모리 압박 판정 재무장(교정 직후 재측정 허용).</summary>
    public void RearmMemory() { lock (_gate) { _memFlag = 0; _lastMemTs = 0; } }

    private void Add(int slot, HangKind kind, double durMs, int tid, int partner, int lockId, int cycle, long startTs, double metric)
    {
        var seq = _seq + 1;
        _ring[seq % FindingCap] = new HangFinding(seq, NowMs, slot, kind, durMs, tid, partner, lockId, cycle, startTs, metric);
        Interlocked.Exchange(ref _seq, seq);
    }

    public bool TryGetFinding(long seq, out HangFinding f)
    {
        lock (_gate)
        {
            if (seq <= 0 || seq > _seq || seq <= _seq - FindingCap) { f = default; return false; }
            f = _ring[seq % FindingCap];
            return f.Seq == seq;
        }
    }

    /// <summary>최신 기준 i번째 발견(0 = 최신).</summary>
    public HangFinding Finding(int i) { TryGetFinding(FindingSeq - i, out var f); return f; }
    public int FindingCount => (int)Math.Min(FindingSeq, FindingCap);

    // ── 워치독(전용 스레드) ──
    public void Start(int intervalMs = 20)
    {
        lock (_gate)
        {
            if (_watchdog is not null) return;
            IntervalMs = Math.Clamp(intervalMs, 1, 1000);
            _stop = new ManualResetEventSlim(false);
            _watchdog = new Thread(WatchLoop) { IsBackground = true, Name = "PF.SLA.Watchdog", Priority = ThreadPriority.AboveNormal };
            _watchdog.Start(_stop);
        }
    }

    private void WatchLoop(object? o)
    {
        var stop = (ManualResetEventSlim)o!;
        try
        {
            while (!stop.Wait(IntervalMs))
            {
                try
                {
                    var n = Probe();
                    if (n > 0) FindingsDetected?.Invoke(n);
                }
                catch (Exception ex) { PromptFlowStudio.Program.Log("Sla.Watchdog", ex); }
            }
        }
        catch (ObjectDisposedException) { }
    }

    public void Stop()
    {
        Thread? t; ManualResetEventSlim? s;
        lock (_gate) { t = _watchdog; s = _stop; _watchdog = null; _stop = null; }
        if (t is null || s is null) return;
        s.Set();
        if (t != Thread.CurrentThread && t.Join(2000)) s.Dispose();
    }

    public void Dispose() => Stop();

    private sealed class PoolPing : IThreadPoolWorkItem
    {
        private readonly ThreadDeadlockAutoProbingEngine _e;
        public PoolPing(ThreadDeadlockAutoProbingEngine e) => _e = e;
        public void Execute()
        {
            _e.PoolLatencyMs = Ms(Stopwatch.GetTimestamp() - Volatile.Read(ref _e._pingQueuedTs));
            Volatile.Write(ref _e._pingOutstanding, 0);
        }
    }
}
