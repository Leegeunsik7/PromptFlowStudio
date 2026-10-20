using System.Diagnostics;

namespace PromptFlowStudio.Services.EnterpriseSla;

/// <summary>레플리카 단위 서킷 브레이커 상태.</summary>
public enum BreakerState : byte { Closed, Open, HalfOpen }

/// <summary>페일오버 이벤트 종류.</summary>
public enum FailoverKind : byte { Failover, Forced, Recovered, Down, Restored }

/// <summary>페일오버 이벤트(값 타입) — Reason은 호출자 상수 문자열.</summary>
public readonly record struct FailoverEvent(long Seq, long AtMs, int Subsystem, FailoverKind Kind, int From, int To, double SwapUs, double DowntimeMs, long LkgSeq, string Reason);

/// <summary>핫스왑 대상 레플리카(샌드박스) 계약 — 상태는 고정 버퍼로 직렬화(무할당).</summary>
public interface IFailoverReplica
{
    string Name { get; }
    /// <summary>점유 자원(바이트) — 메모리 압박 시 격리 대상 선정.</summary>
    long ResourceBytes { get; }
    /// <summary>상태 직렬화 → 기록 바이트 수(-1 = 실패).</summary>
    int SaveState(Span<byte> dst);
    bool LoadState(ReadOnlySpan<byte> src);
    /// <summary>격리: 멈춘 실행 해제 · 자원 반환(빠르게 반환해야 함).</summary>
    void Quarantine();
    /// <summary>샌드박스 재초기화 → 정상이면 true(HalfOpen 프로브).</summary>
    bool Reset();
}

/// <summary>
/// [PromptFlow Studio v16.95.0] 🛡️ 무중단 프로세스 상태 페일오버 &amp; 서킷 브레이커 합성기.
/// · 서브시스템 = 활성/대기 레플리카 2개 · 레플리카별 브레이커(Closed→Open→HalfOpen→Closed).
/// · 연속 실패 ≥ 임계 또는 멈춤 발견 → 활성 격리(Quarantine) → 마지막 정상 체크포인트(LKG, 이중 버퍼)를 대기 레플리카에 복원 → (epoch|active) 원자 교체.
///   호출자는 항상 유효한 레플리카를 받음(교체 중 null 없음) · 이전 epoch 실패 보고는 무시(멈춘 레플리카의 늦은 실패 차단).
/// · 쿨다운 경과 시 HalfOpen에서 Reset 프로브 → 웜 스탠바이 복귀 · 둘 다 Open = Down(거부 집계 · 복구 시 Restored).
/// · 다운타임 = 장애 시작(첫 실패/멈춤 시작) → 교체 완료.
/// </summary>
public sealed class ZeroDowntimeProcessFailoverSynthesizer
{
    public const int MaxSubsystems = 16, EventCap = 256;
    public static readonly string[] KindNames = { "페일오버", "강제 페일오버", "레플리카 복구", "서비스 중단", "서비스 복원" };
    public static readonly string[] StateNames = { "Closed", "Open", "HalfOpen" };

    private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

    private sealed class Sub
    {
        public string Name = "";
        public readonly IFailoverReplica[] R = new IFailoverReplica[2];
        public readonly BreakerState[] State = new BreakerState[2];
        public readonly long[] OpenedTs = new long[2];
        public readonly int[] Trips = new int[2];
        public int Ae;                       // (epoch << 1) | active
        public int Failures, Down;
        public byte[] Lkg = Array.Empty<byte>(), Spare = Array.Empty<byte>();
        public int LkgLen;
        public long LkgSeq, Served, Rejected, FailuresTotal, Failovers, IncidentTs, DownTicks;
        public double LastSwapUs, MaxSwapUs, SumSwapUs;
    }

    private readonly object _gate = new();
    private readonly Sub[] _subs = new Sub[MaxSubsystems];
    private readonly FailoverEvent[] _ring = new FailoverEvent[EventCap];
    private readonly long _origin = Stopwatch.GetTimestamp();
    private int _count;
    private long _seq;

    public int FailureThreshold { get; set; } = 3;
    public double CooldownMs { get; set; } = 1500;
    public int Count => Volatile.Read(ref _count);
    public long EventSeq => Interlocked.Read(ref _seq);
    public long NowMs => (long)((Stopwatch.GetTimestamp() - _origin) * TickMs);

    /// <summary>서브시스템 등록(동일 이름 = 기존 · alreadyRegistered).</summary>
    public int Register(string name, IFailoverReplica primary, IFailoverReplica standby, int stateCapacity = 4096)
    {
        lock (_gate)
        {
            for (var i = 0; i < _count; i++) if (_subs[i].Name == name) return i;
            if (_count >= MaxSubsystems) throw new InvalidOperationException("서브시스템 상한 " + MaxSubsystems);
            var u = new Sub { Name = name, Lkg = new byte[stateCapacity], Spare = new byte[stateCapacity] };
            u.R[0] = primary; u.R[1] = standby;
            _subs[_count] = u;
            Volatile.Write(ref _count, _count + 1);
            return _count - 1;
        }
    }

    // ── 조회 ──
    public string Name(int s) => _subs[s].Name;
    public int ActiveIndex(int s) => Volatile.Read(ref _subs[s].Ae) & 1;
    public int Epoch(int s) => Volatile.Read(ref _subs[s].Ae) >> 1;
    public IFailoverReplica Replica(int s, int i) => _subs[s].R[i];
    public IFailoverReplica Active(int s) => _subs[s].R[ActiveIndex(s)];
    public BreakerState State(int s, int i) => _subs[s].State[i];
    public int Trips(int s, int i) => _subs[s].Trips[i];
    public bool IsDown(int s) => Volatile.Read(ref _subs[s].Down) != 0;
    public int ConsecutiveFailures(int s) => Volatile.Read(ref _subs[s].Failures);
    public long Served(int s) => Interlocked.Read(ref _subs[s].Served);
    public long Rejected(int s) => Interlocked.Read(ref _subs[s].Rejected);
    public long FailuresTotal(int s) => Interlocked.Read(ref _subs[s].FailuresTotal);
    public long Failovers(int s) => Interlocked.Read(ref _subs[s].Failovers);
    public long LkgSeq(int s) => Interlocked.Read(ref _subs[s].LkgSeq);
    public double LastSwapUs(int s) => _subs[s].LastSwapUs;
    public double MaxSwapUs(int s) => _subs[s].MaxSwapUs;
    public double MeanSwapUs(int s) { var u = _subs[s]; var n = Interlocked.Read(ref u.Failovers); return n == 0 ? 0 : u.SumSwapUs / n; }

    /// <summary>누적 다운타임 ticks(진행 중 장애 포함).</summary>
    public long DowntimeTicks(int s)
    {
        var u = _subs[s];
        lock (u)
        {
            var d = u.DownTicks;
            if (u.IncidentTs != 0) d += Stopwatch.GetTimestamp() - u.IncidentTs;
            return d;
        }
    }

    public double DowntimeMs(int s) => DowntimeTicks(s) * TickMs;

    // ── 핫패스(무할당) ──
    /// <summary>활성 레플리카 + epoch 획득(Down이면 false · 거부 집계).</summary>
    public bool TryAcquire(int s, out IFailoverReplica replica, out int epoch)
    {
        var u = _subs[s];
        var ae = Volatile.Read(ref u.Ae);
        epoch = ae >> 1;
        replica = u.R[ae & 1];
        if (Volatile.Read(ref u.Down) == 0) return true;
        Interlocked.Increment(ref u.Rejected);
        return false;
    }

    public void ReportSuccess(int s, int epoch)
    {
        var u = _subs[s];
        if ((Volatile.Read(ref u.Ae) >> 1) != epoch) return;
        Interlocked.Increment(ref u.Served);
        if (Volatile.Read(ref u.Failures) == 0) return;
        lock (u)
        {
            u.Failures = 0;
            if (u.IncidentTs != 0) { u.DownTicks += Stopwatch.GetTimestamp() - u.IncidentTs; u.IncidentTs = 0; }
        }
    }

    /// <summary>실패 보고 → 임계 도달 시 페일오버(true). 이전 epoch(교체 전 레플리카) 보고는 무시.</summary>
    public bool ReportFailure(int s, int epoch, string reason)
    {
        var u = _subs[s];
        lock (u)
        {
            if ((u.Ae >> 1) != epoch) return false;
            Interlocked.Increment(ref u.FailuresTotal);
            if (u.IncidentTs == 0) u.IncidentTs = Stopwatch.GetTimestamp();
            if (++u.Failures < FailureThreshold) return false;
        }
        return Trip(s, reason);
    }

    /// <summary>LKG 체크포인트(정상 상태에서만 · 이중 버퍼 교체 · 무할당).</summary>
    public bool Checkpoint(int s)
    {
        var u = _subs[s];
        lock (u)
        {
            if (u.Down != 0 || u.Failures > 0) return false;
            var n = u.R[u.Ae & 1].SaveState(u.Spare);
            if (n < 0) return false;
            (u.Lkg, u.Spare) = (u.Spare, u.Lkg);
            u.LkgLen = n;
            u.LkgSeq++;
            return true;
        }
    }

    public bool ForceFailover(int s) => Trip(s, "강제 페일오버", 0, FailoverKind.Forced);

    /// <summary>
    /// 활성 레플리카 격리 → 대기 레플리카에 LKG 복원 → 원자 교체. incidentStartTs = 장애 시작 타임스탬프(0 = 기존/지금).
    /// </summary>
    public bool Trip(int s, string reason, long incidentStartTs = 0, FailoverKind kind = FailoverKind.Failover)
    {
        var u = _subs[s];
        lock (u)
        {
            var now = Stopwatch.GetTimestamp();
            if (u.Down != 0) return false;
            if (incidentStartTs != 0 && (u.IncidentTs == 0 || incidentStartTs < u.IncidentTs)) u.IncidentTs = incidentStartTs;
            if (u.IncidentTs == 0) u.IncidentTs = now;
            var from = u.Ae & 1; var to = 1 - from;
            u.State[from] = BreakerState.Open; u.OpenedTs[from] = now; u.Trips[from]++;
            try { u.R[from].Quarantine(); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Sla.Failover.Quarantine", ex); }
            if (u.State[to] != BreakerState.Closed && !TryHeal(u, to, now))
            {
                Volatile.Write(ref u.Down, 1);
                AddEvent(s, FailoverKind.Down, from, -1, 0, 0, u.LkgSeq, reason);
                return false;
            }
            SwapTo(s, u, to, kind, reason);
            return true;
        }
    }

    private void SwapTo(int s, Sub u, int to, FailoverKind kind, string reason)
    {
        var ts = Stopwatch.GetTimestamp();
        var from = u.Ae & 1;
        if (u.LkgLen > 0)
        {
            try { u.R[to].LoadState(u.Lkg.AsSpan(0, u.LkgLen)); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("Sla.Failover.Restore", ex); }
        }
        Volatile.Write(ref u.Ae, (((u.Ae >> 1) + 1) << 1) | to);
        u.State[to] = BreakerState.Closed;
        u.Failures = 0;
        Volatile.Write(ref u.Down, 0);
        var end = Stopwatch.GetTimestamp();
        var swapUs = (end - ts) * TickMs * 1000.0;
        var downMs = u.IncidentTs != 0 ? (end - u.IncidentTs) * TickMs : 0;
        if (u.IncidentTs != 0) { u.DownTicks += end - u.IncidentTs; u.IncidentTs = 0; }
        u.LastSwapUs = swapUs; u.SumSwapUs += swapUs;
        if (swapUs > u.MaxSwapUs) u.MaxSwapUs = swapUs;
        Interlocked.Increment(ref u.Failovers);
        AddEvent(s, kind, from, to, swapUs, downMs, u.LkgSeq, reason);
    }

    /// <summary>쿨다운 경과 시 HalfOpen → Reset 프로브 → Closed/재Open.</summary>
    private bool TryHeal(Sub u, int i, long now)
    {
        if (u.State[i] == BreakerState.Closed) return true;
        if ((now - u.OpenedTs[i]) * TickMs < CooldownMs) return false;
        u.State[i] = BreakerState.HalfOpen;
        bool ok;
        try { ok = u.R[i].Reset(); }
        catch (Exception ex) { ok = false; PromptFlowStudio.Program.Log("Sla.Failover.Reset", ex); }
        u.State[i] = ok ? BreakerState.Closed : BreakerState.Open;
        if (!ok) u.OpenedTs[i] = now;
        return ok;
    }

    /// <summary>유지 보수(쿨다운 경과 Open 레플리카 HalfOpen 프로브 · Down 복원) → 복구 수.</summary>
    public int Maintain()
    {
        var healed = 0;
        for (var s = 0; s < Count; s++)
        {
            var u = _subs[s];
            lock (u)
            {
                var now = Stopwatch.GetTimestamp();
                for (var i = 0; i < 2; i++)
                {
                    if (u.State[i] != BreakerState.Open) continue;
                    if (!TryHeal(u, i, now)) continue;
                    healed++;
                    if (u.Down != 0) SwapTo(s, u, i, FailoverKind.Restored, "브레이커 HalfOpen 프로브 통과");
                    else AddEvent(s, FailoverKind.Recovered, i, i, 0, 0, u.LkgSeq, "브레이커 HalfOpen 프로브 통과");
                }
            }
        }
        return healed;
    }

    private void AddEvent(int s, FailoverKind kind, int from, int to, double swapUs, double downMs, long lkg, string reason)
    {
        lock (_ring)
        {
            var seq = _seq + 1;
            _ring[seq % EventCap] = new FailoverEvent(seq, NowMs, s, kind, from, to, swapUs, downMs, lkg, reason);
            Interlocked.Exchange(ref _seq, seq);
        }
    }

    public bool TryGetEvent(long seq, out FailoverEvent e)
    {
        lock (_ring)
        {
            if (seq <= 0 || seq > _seq || seq <= _seq - EventCap) { e = default; return false; }
            e = _ring[seq % EventCap];
            return e.Seq == seq;
        }
    }

    public FailoverEvent Event(int i) { TryGetEvent(EventSeq - i, out var e); return e; }
    public int EventCount => (int)Math.Min(EventSeq, EventCap);
}
