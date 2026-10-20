using System;
using System.Diagnostics;
using System.Net;
using System.Threading;

namespace PromptFlowStudio.Services.AI.Wan;

/// <summary>현재 게시된 라우팅 테이블(디코드된 값 형식). 인덱스 -1 = 없음.</summary>
public readonly record struct MeshRoute(int PrimaryIndex, int BackupIndex, int PrimarySharePct, uint Generation)
{
    public bool HasRoute => PrimaryIndex >= 0;
}

/// <summary>흐름 1건의 경로 선택 결과(값 형식).</summary>
public readonly record struct RouteChoice(int NodeIndex, bool UsedBackup, uint Generation)
{
    public bool HasRoute => NodeIndex >= 0;
}

/// <summary>
/// [PromptFlow Studio v12.2.0-Phase3] P2P 메시 자동 부하 분산기 — 노드별 <see cref="MeshRelayNode"/> 링버퍼 지문으로 경로 비용을 산출해
///   최저 비용 2개(Primary/Backup)와 분배 비율을 동적 재구성하고, 노드 이탈/지연 시 원자적으로 우회 경로로 전환한다.
///   ▸ Lock-Free 라우팅 테이블: Primary(12bit)·Backup(12bit)·Primary 분배율%(8bit)·세대(32bit)를 <c>long</c> 1개에 패킹해
///     <see cref="Interlocked.CompareExchange(ref long, long, long)"/> 로 게시 → <see cref="SelectRoute"/> 는 Volatile 읽기 1회뿐(락·할당 0),
///     반쪽 갱신(Primary 만 바뀐 상태) 관측이 구조적으로 불가능. 세대값이 재구성↔페일오버 경합의 ABA 를 차단.
///   ▸ Atomic Failover: <see cref="ReportFailure"/> 는 Backup 을 Primary 로 CAS 승격(수 μs 이내) 후 새 Backup 을 재선출.
///     ⚠ "5ms 이내 전환"은 <b>검출 이후 전환</b> 시간이다. 무통보 이탈의 검출 시간은 무응답 판정 한도(<see cref="StaleAfterMs"/>,
///     기본 30ms = 10ms 프로브 3회 누락)에 좌우된다 - 검출 즉시 같은 틱에서 전환.
///   ▸ 흐름 고정성: 같은 flowKey 는 테이블이 바뀌지 않는 한 항상 같은 노드(splitmix64 해시 버킷 %100 &lt; 분배율 → Primary).
///   ▸ 기존 <see cref="EdgeP2PTaskRelay"/>(UDP 탐지/TCP 오프로드)·QuantumP2pMeshRouter 무변경(Additive-Only) -
///     실 프로브는 <see cref="Start"/> 의 probe 델리게이트 또는 <see cref="RecordSample"/> 로 외부에서 공급한다.
/// </summary>
public sealed class P2pLoadBalancer : IDisposable
{
    public const int ProbeIntervalMs = 10;
    public const int MaxNodes = 4094;          // 12bit 인덱스(0xFFF = 없음)
    public const double DefaultStaleAfterMs = 30;
    private const int None = 0xFFF;

    private readonly object _writeGate = new();
    private readonly Func<long> _clock;
    private MeshRelayNode[] _nodes = Array.Empty<MeshRelayNode>();
    private long _route = Pack(None, None, 0, 0);
    private System.Threading.Timer? _timer;
    private Func<int, (bool Ok, double LatencyMs, double BandwidthMbps)>? _probe;
    private int _inTick;
    private long _rebalances, _failovers, _routed, _backupRouted, _probeTicks;
    private long _lastFailoverTicks, _maxFailoverTicks;
    private uint _demoRng = 0x1234567u;
    private long _demoKilledMask;   // 데모 전용: 비트가 켜진 노드는 합성 프로브가 유실을 반환
    private bool _disposed;

    public event Action? OnChange;

    public P2pLoadBalancer() : this(Stopwatch.GetTimestamp) { }

    /// <summary>테스트용 시계 주입(Stopwatch ticks 단위).</summary>
    internal P2pLoadBalancer(Func<long> clock) => _clock = clock;

    public double StaleAfterMs { get; set; } = DefaultStaleAfterMs;
    public int NodeCount => Volatile.Read(ref _nodes).Length;
    public bool Running => Volatile.Read(ref _timer) is not null;
    public long Rebalances => Interlocked.Read(ref _rebalances);
    public long Failovers => Interlocked.Read(ref _failovers);
    public long RoutedRequests => Interlocked.Read(ref _routed);
    public long BackupRouted => Interlocked.Read(ref _backupRouted);
    public long ProbeTicks => Interlocked.Read(ref _probeTicks);
    /// <summary>마지막 페일오버의 검출→게시 소요(ms). 페일오버 이력이 없으면 0.</summary>
    public double LastFailoverMs => Interlocked.Read(ref _lastFailoverTicks) * 1000.0 / Stopwatch.Frequency;
    public double MaxFailoverMs => Interlocked.Read(ref _maxFailoverTicks) * 1000.0 / Stopwatch.Frequency;
    public MeshRoute Route => Decode(Volatile.Read(ref _route));

    public string NodeIdAt(int index) => Volatile.Read(ref _nodes)[index].NodeId;

    public MeshNodeFingerprint FingerprintAt(int index)
        => Volatile.Read(ref _nodes)[index].Fingerprint(_clock(), StaleTicks());

    private long StaleTicks() => (long)(StaleAfterMs * Stopwatch.Frequency / 1000.0);

    // ── 패킹: [gen:32][share:8][backup:12][primary:12] ─────────────────────────────
    private static long Pack(int primary, int backup, int share, uint gen)
        => ((long)gen << 32) | ((long)(share & 0xFF) << 24) | ((long)(backup & 0xFFF) << 12) | (long)(primary & 0xFFF);

    private static MeshRoute Decode(long packed)
    {
        var p = (int)(packed & 0xFFF);
        var b = (int)((packed >> 12) & 0xFFF);
        var share = (int)((packed >> 24) & 0xFF);
        var gen = (uint)((ulong)packed >> 32);
        return new MeshRoute(p == None ? -1 : p, b == None ? -1 : b, share, gen);
    }

    /// <summary>노드 등록(설정 시점 - copy-on-write 로 1회 할당). 반환값 = 노드 인덱스.</summary>
    public int AddNode(string nodeId, int ringCapacity = MeshRelayNode.DefaultRingCapacity)
    {
        lock (_writeGate)
        {
            var cur = _nodes;
            if (cur.Length >= MaxNodes) throw new InvalidOperationException($"노드 상한({MaxNodes}) 초과");
            var next = new MeshRelayNode[cur.Length + 1];
            cur.CopyTo(next, 0);
            next[^1] = new MeshRelayNode(nodeId, ringCapacity);
            Volatile.Write(ref _nodes, next);
            return next.Length - 1;
        }
    }

    public void RecordSample(int nodeIndex, double latencyMs, double bandwidthMbps)
        => Volatile.Read(ref _nodes)[nodeIndex].RecordSample(latencyMs, bandwidthMbps, _clock());

    public void RecordLoss(int nodeIndex) => Volatile.Read(ref _nodes)[nodeIndex].RecordLoss();

    /// <summary>흐름 1건의 릴레이 노드 선택 - 락·할당 0(패킹 워드 Volatile 읽기 1회 + 해시).</summary>
    public RouteChoice SelectRoute(ulong flowKey)
    {
        var r = Decode(Volatile.Read(ref _route));
        if (!r.HasRoute) return new RouteChoice(-1, false, r.Generation);
        var useBackup = r.BackupIndex >= 0 && (int)(Mix(flowKey) % 100) >= r.PrimarySharePct;
        Interlocked.Increment(ref _routed);
        if (useBackup) Interlocked.Increment(ref _backupRouted);
        return new RouteChoice(useBackup ? r.BackupIndex : r.PrimaryIndex, useBackup, r.Generation);
    }

    // splitmix64 - 순차 flowKey 도 버킷에 고르게 분산.
    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15ul;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9ul;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBul;
        return x ^ (x >> 31);
    }

    /// <summary>노드 이탈/지연 통보 → 해당 노드가 Primary/Backup 이면 즉시 CAS 우회(Backup 승격) 후 새 Backup 재선출.
    ///   전환이 일어났으면 true.</summary>
    public bool ReportFailure(int nodeIndex)
    {
        var t0 = _clock();
        var nodes = Volatile.Read(ref _nodes);
        if ((uint)nodeIndex >= (uint)nodes.Length) return false;
        nodes[nodeIndex].MarkDown();

        var switched = false;
        while (true)
        {
            var cur = Volatile.Read(ref _route);
            var r = Decode(cur);
            if (r.PrimaryIndex != nodeIndex && r.BackupIndex != nodeIndex) break;
            var np = r.PrimaryIndex == nodeIndex ? r.BackupIndex : r.PrimaryIndex;
            var next = Pack(np < 0 ? None : np, None, np < 0 ? 0 : 100, r.Generation + 1);
            if (Interlocked.CompareExchange(ref _route, next, cur) == cur)
            {
                if (r.PrimaryIndex == nodeIndex) { RecordFailoverLatency(_clock() - t0); switched = true; }   // Backup 만 빠진 경우는 페일오버로 집계하지 않음
                break;
            }
        }
        Rebalance();   // 새 Backup 재선출(전환 자체는 위 CAS 에서 이미 완료 - 이 재구성은 전환 지연에 포함되지 않음)
        return switched;
    }

    private void RecordFailoverLatency(long ticks)
    {
        Interlocked.Increment(ref _failovers);
        Interlocked.Exchange(ref _lastFailoverTicks, ticks);
        var max = Interlocked.Read(ref _maxFailoverTicks);
        while (ticks > max)
        {
            var seen = Interlocked.CompareExchange(ref _maxFailoverTicks, ticks, max);
            if (seen == max) break;
            max = seen;
        }
    }

    /// <summary>전 노드 지문으로 최저 비용 2개를 뽑아 테이블 재게시. 분배율 = 비용 역비례(낮은 쪽이 더 많이 받음, 50~100%).
    ///   이전 Primary 가 이번에 Down 으로 판정됐다면 이 재구성이 곧 페일오버(검출→게시 시간 기록). 무할당.</summary>
    public MeshRoute Rebalance()
    {
        lock (_writeGate)
        {
            using var gcGuard = PromptFlowStudio.Services.Diagnostics.ZeroGcRegionScope.Enter("P2pLoadBalancer.Rebalance");   // v12.3.0: 10ms 재구성 핫패스 할당 회귀 검출
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var t0 = _clock();
                var cur = Volatile.Read(ref _route);
                var prev = Decode(cur);
                var nodes = _nodes;
                var stale = StaleTicks();

                int best = -1, second = -1;
                double bestCost = double.PositiveInfinity, secondCost = double.PositiveInfinity;
                var prevPrimaryDown = false;
                for (var i = 0; i < nodes.Length; i++)
                {
                    var fp = nodes[i].Fingerprint(t0, stale);
                    if (i == prev.PrimaryIndex && fp.Health == MeshNodeHealth.Down) prevPrimaryDown = true;
                    if (fp.Health == MeshNodeHealth.Down) continue;
                    if (fp.Cost < bestCost) { second = best; secondCost = bestCost; best = i; bestCost = fp.Cost; }
                    else if (fp.Cost < secondCost) { second = i; secondCost = fp.Cost; }
                }

                var share = best < 0 ? 0 : second < 0 ? 100
                    : (int)Math.Clamp(Math.Round(secondCost / (bestCost + secondCost) * 100), 50, 100);
                var next = Pack(best < 0 ? None : best, second < 0 ? None : second, share, prev.Generation + 1);
                if ((next & 0xFFFFFFFFL) == (cur & 0xFFFFFFFFL)) return prev;   // 내용 불변 - 세대만 올리는 무의미 게시 생략
                if (Interlocked.CompareExchange(ref _route, next, cur) != cur) continue;   // 동시 페일오버가 먼저 게시 → 재계산

                Interlocked.Increment(ref _rebalances);
                if (prevPrimaryDown && best != prev.PrimaryIndex) RecordFailoverLatency(_clock() - t0);
                return Decode(next);
            }
            return Route;
        }
    }

    /// <summary>10ms 프로브 루프 기동 - probe(노드 인덱스) 가 (성공, ms, Mbps) 를 돌려주면 기록 후 재구성.
    ///   probe 가 null 이면 재구성(무응답 검출)만 주기 실행하고 샘플은 <see cref="RecordSample"/> 로 외부 공급.</summary>
    public void Start(Func<int, (bool Ok, double LatencyMs, double BandwidthMbps)>? probe = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _probe = probe;
        if (Volatile.Read(ref _timer) is not null) return;
        var t = new System.Threading.Timer(OnTimer, null, ProbeIntervalMs, ProbeIntervalMs);
        if (Interlocked.CompareExchange(ref _timer, t, null) is not null) t.Dispose();
        else PromptFlowStudio.Services.Diagnostics.GcLatencySession.Acquire();
        OnChange?.Invoke();
    }

    /// <summary>테스트용: 타이머 없이 프로브 델리게이트만 지정(<see cref="ProbeTick"/> 직접 구동과 병용).</summary>
    internal void SetProbe(Func<int, (bool Ok, double LatencyMs, double BandwidthMbps)>? probe) => _probe = probe;

    public void Stop()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        if (t is null) return;
        t.Dispose();
        PromptFlowStudio.Services.Diagnostics.GcLatencySession.Release();
        OnChange?.Invoke();
    }

    private void OnTimer(object? _)
    {
        if (Interlocked.CompareExchange(ref _inTick, 1, 0) != 0) return;
        try { ProbeTick(); }
        catch (Exception ex) { Program.Log("P2pLoadBalancer.Tick", ex); }
        finally { Volatile.Write(ref _inTick, 0); }
    }

    /// <summary>프로브 1주기(타이머 콜백 본체 - 테스트에서 직접 호출 가능). 테이블이 바뀌었으면 OnChange.</summary>
    public void ProbeTick()
    {
        var probe = _probe;
        var nodes = Volatile.Read(ref _nodes);
        if (probe is not null)
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                (bool Ok, double LatencyMs, double BandwidthMbps) r;
                try { r = probe(i); } catch { r = default; }
                if (r.Ok) nodes[i].RecordSample(r.LatencyMs, r.BandwidthMbps, _clock());
                else nodes[i].RecordLoss();
            }
        }
        Interlocked.Increment(ref _probeTicks);
        var before = Volatile.Read(ref _route);
        Rebalance();
        if (Volatile.Read(ref _route) != before) OnChange?.Invoke();
    }

    /// <summary>HUD 데모: 합성 노드 4개(서로 다른 기준 레이턴시/대역폭 + 결정적 지터) 등록 후 프로브 루프 기동.
    ///   ⚠ 실 네트워크 측정이 아니다(표시/동작 검증용).</summary>
    public void StartDemo()
    {
        if (NodeCount == 0)
        {
            AddNode("edge-seoul");
            AddNode("edge-tokyo");
            AddNode("edge-singapore");
            AddNode("edge-frankfurt");
        }
        Start(DemoProbe);
    }

    private (bool Ok, double LatencyMs, double BandwidthMbps) DemoProbe(int i)
    {
        if ((Interlocked.Read(ref _demoKilledMask) & (1L << (i & 63))) != 0) return (false, 0, 0);
        var x = _demoRng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _demoRng = x;
        var jitter = (x >> 8) * (1.0 / 16777216.0) * 4.0;
        return i switch
        {
            0 => (true, 8 + jitter, 900),
            1 => (true, 32 + jitter, 600),
            2 => (true, 71 + jitter, 400),
            _ => (true, 245 + jitter, 150),
        };
    }

    /// <summary>HUD 데모: 현재 Primary 노드를 이탈시킴(합성 프로브 유실 고정 + 즉시 페일오버). 전환됐으면 true.</summary>
    public bool DemoKillPrimary()
    {
        var p = Route.PrimaryIndex;
        if (p < 0 || p > 63) return false;
        long cur, next;
        do { cur = Interlocked.Read(ref _demoKilledMask); next = cur | (1L << p); } while (Interlocked.CompareExchange(ref _demoKilledMask, next, cur) != cur);
        var ok = ReportFailure(p);
        OnChange?.Invoke();
        return ok;
    }

    /// <summary>HUD 데모: 이탈시킨 노드 전부 복귀(다음 프로브 성공 샘플부터 재편입).</summary>
    public void DemoReviveAll() { Interlocked.Exchange(ref _demoKilledMask, 0); OnChange?.Invoke(); }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _rebalances, 0);
        Interlocked.Exchange(ref _failovers, 0);
        Interlocked.Exchange(ref _routed, 0);
        Interlocked.Exchange(ref _backupRouted, 0);
        Interlocked.Exchange(ref _probeTicks, 0);
        Interlocked.Exchange(ref _lastFailoverTicks, 0);
        Interlocked.Exchange(ref _maxFailoverTicks, 0);
        OnChange?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>
/// [PromptFlow Studio v12.2.0-Phase4] <see cref="EdgeP2PTaskRelay"/>(실 UDP Ping/Pong) ↔ <see cref="P2pLoadBalancer"/> 실체화 결선.
///   ▸ RTT: Relay 가 타임스탬프 에코 Pong 을 받을 때마다 <see cref="EdgeP2PTaskRelay.OnPeerRtt"/> → <see cref="P2pLoadBalancer.RecordSample"/>.
///     피어 NodeId 가 처음 보이면 밸런서 노드로 자동 등록.
///   ▸ 손실: <see cref="Tick"/>(기본 50ms)마다 발견된 피어 전원에게 유니캐스트 Ping - 직전 Ping 이 응답 없이 남아 있으면 손실 1회
///     (<see cref="P2pLoadBalancer.RecordLoss"/>). 연속 <see cref="LossesForFailover"/>회면 <see cref="P2pLoadBalancer.ReportFailure"/> 로
///     즉시 원자 페일오버(무응답 한도 대기 없이). Pong 이 다시 오면 다음 재구성에서 자동 재편입.
///   ▸ ⚠ 대역폭: UDP Ping 은 대역폭을 측정하지 못한다 - 전 노드에 동일한 중립값(<see cref="UnmeasuredBandwidthMbps"/>)을 기록하므로
///     경로 비용 차이는 순수하게 RTT·지터·손실에서만 나온다(실측이 아닌 값을 실측처럼 쓰지 않기 위한 정직 처리).
///   ▸ 밸런서의 무응답 한도(StaleAfterMs)를 Ping 주기×(손실 한도+1) 이상으로 올린다 - 10ms 재구성 주기에서 Ping 사이 공백을
///     이탈로 오판하지 않게.
/// </summary>
public sealed class EdgeRelayProbeBinder : IDisposable
{
    public const int DefaultPingIntervalMs = 50;
    public const int LossesForFailover = 3;
    public const double UnmeasuredBandwidthMbps = 100;

    private sealed class Slot
    {
        public int Index;
        public int Outstanding;   // 1 = 응답 대기 중인 Ping 존재
        public int Losses;
        public int Reported;
    }

    private readonly P2pLoadBalancer _lb;
    private readonly EdgeP2PTaskRelay _relay;
    private readonly int _pingIntervalMs;
    private readonly object _gate = new();
    private readonly System.Collections.Generic.Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private System.Threading.Timer? _timer;
    private int _inTick;
    private long _rttSamples, _losses, _failoversTriggered, _lastRttMicros;
    private bool _disposed;

    public event Action? OnChange;

    public EdgeRelayProbeBinder(P2pLoadBalancer balancer, EdgeP2PTaskRelay relay) : this(balancer, relay, DefaultPingIntervalMs) { }

    internal EdgeRelayProbeBinder(P2pLoadBalancer balancer, EdgeP2PTaskRelay relay, int pingIntervalMs)
    {
        _lb = balancer;
        _relay = relay;
        _pingIntervalMs = Math.Max(10, pingIntervalMs);
        _lb.StaleAfterMs = Math.Max(_lb.StaleAfterMs, _pingIntervalMs * (LossesForFailover + 1.0));
        _relay.OnPeerRtt += OnRtt;
    }

    public bool Running => Volatile.Read(ref _timer) is not null;
    public int PingIntervalMs => _pingIntervalMs;
    public long RttSamples => Interlocked.Read(ref _rttSamples);
    public long LossesRecorded => Interlocked.Read(ref _losses);
    public long FailoversTriggered => Interlocked.Read(ref _failoversTriggered);
    public double LastRttMs => Interlocked.Read(ref _lastRttMicros) / 1000.0;

    /// <summary>밸런서 노드 인덱스 조회(아직 한 번도 관측 안 된 피어면 -1).</summary>
    public int IndexOf(string nodeId) { lock (_gate) return _slots.TryGetValue(nodeId, out var s) ? s.Index : -1; }

    private Slot GetOrAdd(string nodeId)
    {
        lock (_gate)
        {
            if (_slots.TryGetValue(nodeId, out var s)) return s;
            s = new Slot { Index = _lb.AddNode(nodeId) };   // 피어 최초 관측 시 1회(설정성 할당)
            _slots[nodeId] = s;
            return s;
        }
    }

    // Relay UDP 수신 스레드.
    private void OnRtt(PeerRttSample sample)
    {
        var slot = GetOrAdd(sample.NodeId);
        _lb.RecordSample(slot.Index, sample.RttMs, UnmeasuredBandwidthMbps);
        Volatile.Write(ref slot.Outstanding, 0);
        Interlocked.Exchange(ref slot.Losses, 0);
        Interlocked.Exchange(ref slot.Reported, 0);
        Interlocked.Increment(ref _rttSamples);
        Interlocked.Exchange(ref _lastRttMicros, (long)(sample.RttMs * 1000));
    }

    /// <summary>프로브 1주기(타이머 본체 - 테스트에서 직접 호출): 미응답 판정 → 손실/페일오버 → 새 Ping 발신 → 밸런서 재구성.</summary>
    public void Tick()
    {
        foreach (var peer in _relay.Peers)
        {
            var slot = GetOrAdd(peer.NodeId);
            if (Interlocked.Exchange(ref slot.Outstanding, 1) == 1)
            {
                _lb.RecordLoss(slot.Index);
                Interlocked.Increment(ref _losses);
                if (Interlocked.Increment(ref slot.Losses) >= LossesForFailover && Interlocked.Exchange(ref slot.Reported, 1) == 0)
                {
                    if (_lb.ReportFailure(slot.Index)) Interlocked.Increment(ref _failoversTriggered);
                }
            }
            if (peer.UdpPort > 0) _ = _relay.PingAsync(new IPEndPoint(peer.Address, peer.UdpPort));   // PingAsync 는 내부에서 예외 흡수
        }
        _lb.ProbeTick();
        OnChange?.Invoke();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _timer) is not null) return;
        var t = new System.Threading.Timer(OnTimer, null, _pingIntervalMs, _pingIntervalMs);
        if (Interlocked.CompareExchange(ref _timer, t, null) is not null) t.Dispose();
        OnChange?.Invoke();
    }

    public void Stop()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        if (t is null) return;
        t.Dispose();
        OnChange?.Invoke();
    }

    private void OnTimer(object? _)
    {
        if (Interlocked.CompareExchange(ref _inTick, 1, 0) != 0) return;
        try { Tick(); }
        catch (Exception ex) { Program.Log("EdgeRelayProbeBinder.Tick", ex); }
        finally { Volatile.Write(ref _inTick, 0); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _relay.OnPeerRtt -= OnRtt;
        Stop();
    }
}
