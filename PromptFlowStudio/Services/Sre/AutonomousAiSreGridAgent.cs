namespace PromptFlowStudio.Services.Sre;

public enum NodeFault : byte { None, Crashed, MemoryLeak, GcStorm, Runaway }

/// <summary>노드 상태 스냅샷(UI).</summary>
public readonly record struct SreNodeStatus(int Id, int Region, bool Alive, bool Drained, bool Isolated, NodeFault Fault, float Load, float Capacity, float Vram);

/// <summary>
/// [PromptFlow Studio v16.67.0] 🌐 Five-Nines 자율 AI SRE 에이전트 그리드 — 5 리전(서울 · 도쿄 · 프랑크푸르트 · 버지니아 · 상파울루) × 3 노드.
/// 제어 루프(<see cref="Tick"/>): 하트비트(10ms) 3회 누락 → 드레인 + 페일오버 · 반응기 격리 노드 즉시 드레인 · 재시작/재활용(2s) ·
/// 트래픽 셰이핑(중요 70% 전량 · 저우선 30% × 예산 수용률) · 예비 용량(번 레이트 결정) · 리전 지연 순 오버플로 라우팅 ·
/// 미탐지 크래시 노드 = 블랙홀(오류) · 런어웨이/GC 폭주 노드는 부분 실패 · 누수 VRAM 1.0 도달 시 OOM 크래시.
/// 자율 복구 OFF → 드레인/격리 반영/재시작/셰이핑/예비 모두 중지(대조군). SLI = 정상 / (정상 + 오류) · 셰이핑 요청은 SLI 제외. 틱 무할당.
/// </summary>
public sealed class AutonomousAiSreGridAgent
{
    public const int Regions = 5, NodesPerRegion = 3, Nodes = Regions * NodesPerRegion;
    public const double HeartbeatMs = 10, DetectMisses = 3, RestartMs = 2000, DiurnalPeriodMs = 600_000;
    public const float NodeCapacity = 5000, RegionDemand = 10000, CriticalShare = 0.7f;

    public static readonly string[] RegionNames = { "서울", "도쿄", "프랑크푸르트", "버지니아", "상파울루" };
    /// <summary>지도 좌표(0..1).</summary>
    public static readonly float[] RegionX = { 0.82f, 0.88f, 0.50f, 0.27f, 0.33f }, RegionY = { 0.36f, 0.40f, 0.28f, 0.38f, 0.75f };
    private static readonly double[] Latency =
    {
        0, 30, 230, 180, 300,
        30, 0, 220, 150, 260,
        230, 220, 0, 90, 200,
        180, 150, 90, 0, 120,
        300, 260, 200, 120, 0,
    };

    private readonly object _gate = new();
    private readonly BurnRateAndErrorBudgetOptimizer _budget;
    private readonly GlobalEdgeTelemetryAnomalyReactor _reactor;
    private readonly int[] _order = new int[Regions * Regions];
    private readonly bool[] _alive = new bool[Nodes], _drained = new bool[Nodes], _outage = new bool[Regions];
    private readonly NodeFault[] _fault = new NodeFault[Nodes];
    private readonly double[] _lastBeat = new double[Nodes], _restartAt = new double[Nodes], _surgeUntil = new double[Regions];
    private readonly float[] _load = new float[Nodes], _spare = new float[Nodes], _vram = new float[Nodes], _surge = new float[Regions];
    private readonly double[] _regionGood = new double[Regions], _regionBad = new double[Regions];
    private uint _rng = 0x9E3779B9;
    private double _clock, _good, _bad, _shaped, _latWeighted, _served, _lastTickBad, _lastTickTotal;
    private long _failovers, _restarts, _drains, _ooms, _ticks;
    private bool _autoHeal = true;

    public AutonomousAiSreGridAgent() : this(new BurnRateAndErrorBudgetOptimizer(), new GlobalEdgeTelemetryAnomalyReactor()) { }

    public AutonomousAiSreGridAgent(IServiceProvider sp)
        : this((BurnRateAndErrorBudgetOptimizer?)sp.GetService(typeof(BurnRateAndErrorBudgetOptimizer)) ?? new BurnRateAndErrorBudgetOptimizer(),
               (GlobalEdgeTelemetryAnomalyReactor?)sp.GetService(typeof(GlobalEdgeTelemetryAnomalyReactor)) ?? new GlobalEdgeTelemetryAnomalyReactor()) { }

    public AutonomousAiSreGridAgent(BurnRateAndErrorBudgetOptimizer budget, GlobalEdgeTelemetryAnomalyReactor reactor)
    {
        _budget = budget; _reactor = reactor;
        for (var r = 0; r < Regions; r++)
        {
            // 지연 오름차순(자기 리전 먼저) — 삽입 정렬
            for (var k = 0; k < Regions; k++) _order[r * Regions + k] = k;
            for (var i = 1; i < Regions; i++)
                for (var j = i; j > 0 && Latency[r * Regions + _order[r * Regions + j]] < Latency[r * Regions + _order[r * Regions + j - 1]]; j--)
                    (_order[r * Regions + j], _order[r * Regions + j - 1]) = (_order[r * Regions + j - 1], _order[r * Regions + j]);
            _surge[r] = 1;
        }
        for (var n = 0; n < Nodes; n++) { _alive[n] = true; _vram[n] = 0.5f; }
    }

    public event Action? OnChange;

    public BurnRateAndErrorBudgetOptimizer Budget => _budget;
    public GlobalEdgeTelemetryAnomalyReactor Reactor => _reactor;
    public double ClockMs { get { lock (_gate) return _clock; } }
    public double GoodRequests { get { lock (_gate) return _good; } }
    public double BadRequests { get { lock (_gate) return _bad; } }
    public double ShapedRequests { get { lock (_gate) return _shaped; } }
    public long Failovers { get { lock (_gate) return _failovers; } }
    public long Restarts { get { lock (_gate) return _restarts; } }
    public long Drains { get { lock (_gate) return _drains; } }
    public long OomCrashes { get { lock (_gate) return _ooms; } }
    public long Ticks { get { lock (_gate) return _ticks; } }
    public string? LastAction { get; private set; }

    public bool AutoHeal
    {
        get { lock (_gate) return _autoHeal; }
        set
        {
            lock (_gate) _autoHeal = value;
            LastAction = value ? "🛡 자율 복구 ON — 페일오버 · 격리 드레인 · 셰이핑 · 예비 용량" : "⏸ 자율 복구 OFF — 수동 운영(대조군)";
            Raise();
        }
    }

    /// <summary>누적 가용률(SLI).</summary>
    public double Availability { get { lock (_gate) { var t = _good + _bad; return t > 0 ? _good / t : 1; } } }

    /// <summary>가용률의 9 개수(−log10(1 − A) · 상한 9).</summary>
    public static double NinesOf(double a) => a >= 1 ? 9 : Math.Min(9, -Math.Log10(1 - a));
    public double Nines => NinesOf(Availability);

    /// <summary>직전 틱 오류율.</summary>
    public double LastTickErrorRate { get { lock (_gate) return _lastTickTotal > 0 ? _lastTickBad / _lastTickTotal : 0; } }

    /// <summary>오버플로 포함 평균 서비스 지연(ms · 가중).</summary>
    public double MeanRouteLatencyMs { get { lock (_gate) return _served > 0 ? _latWeighted / _served : 0; } }

    public double RegionAvailability(int r) { lock (_gate) { var t = _regionGood[r] + _regionBad[r]; return t > 0 ? _regionGood[r] / t : 1; } }
    public bool IsRegionOut(int r) { lock (_gate) return _outage[r]; }
    public float SurgeOf(int r) { lock (_gate) return _surge[r]; }
    public static double LatencyBetween(int a, int b) => Latency[a * Regions + b];

    public SreNodeStatus StatusOf(int n)
    {
        lock (_gate)
            return new SreNodeStatus(n, n / NodesPerRegion, _alive[n], _drained[n], _reactor.IsIsolated(n), _fault[n], _load[n], NodeCapacity, _vram[n]);
    }

    // ── 결함 주입 ──
    public void CrashNode(int n) { lock (_gate) { _alive[n] = false; _fault[n] = NodeFault.Crashed; } Act("💥 노드 " + NodeName(n) + " 크래시 주입"); }
    public void InjectLeak(int n) { lock (_gate) if (_alive[n]) _fault[n] = NodeFault.MemoryLeak; Act("💧 노드 " + NodeName(n) + " 메모리 누수 주입"); }
    public void InjectGcStorm(int n) { lock (_gate) if (_alive[n]) _fault[n] = NodeFault.GcStorm; Act("🌀 노드 " + NodeName(n) + " GC 폭주 주입"); }
    public void InjectRunaway(int n) { lock (_gate) if (_alive[n]) _fault[n] = NodeFault.Runaway; Act("🔥 노드 " + NodeName(n) + " CPU 런어웨이 주입"); }

    public void RegionOutage(int r, bool down)
    {
        lock (_gate)
        {
            _outage[r] = down;
            for (var k = 0; k < NodesPerRegion; k++)
            {
                var n = r * NodesPerRegion + k;
                if (down) { _alive[n] = false; _fault[n] = NodeFault.Crashed; }
                else if (!_alive[n]) _restartAt[n] = _clock;
            }
        }
        Act(down ? "🌩 " + RegionNames[r] + " 리전 전면 장애" : "🌤 " + RegionNames[r] + " 리전 복구");
    }

    public void Surge(int r, float multiplier, double durationMs)
    {
        lock (_gate) { _surge[r] = Math.Clamp(multiplier, 1f, 20f); _surgeUntil[r] = _clock + durationMs; }
        Act("📈 " + RegionNames[r] + " 트래픽 ×" + multiplier.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " 급증");
    }

    public static string NodeName(int n) => RegionNames[n / NodesPerRegion] + "-" + (n % NodesPerRegion + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private float Noise() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return ((_rng & 0xFFFF) / 65535f - 0.5f) * 0.02f; }

    /// <summary>1 제어 틱(무할당).</summary>
    public void Tick(double dtMs)
    {
        var drained = false;
        lock (_gate)
        {
            _ticks++;
            _clock += dtMs;
            var dt = dtMs / 1000.0;
            var heal = _autoHeal;
            var dec = _budget.Current;
            var reserve = heal ? 1f + dec.ReserveRatio : 1f;
            var admission = heal ? dec.AdmissionRatio : 1f;

            // 1) 하트비트 · 탐지 · 재시작
            for (var n = 0; n < Nodes; n++)
            {
                if (_alive[n]) _lastBeat[n] = _clock;
                else if (heal && !_drained[n] && _clock - _lastBeat[n] >= HeartbeatMs * DetectMisses)
                {
                    _drained[n] = true; _drains++; _failovers++; drained = true;
                    if (_restartAt[n] <= 0) _restartAt[n] = _clock + RestartMs;
                }
                if (heal && _fault[n] != NodeFault.None && _fault[n] != NodeFault.Crashed && _reactor.IsIsolated(n) && _restartAt[n] <= 0)
                    _restartAt[n] = _clock + RestartMs;   // 격리 노드 재활용 예약
                if (_restartAt[n] > 0 && _clock >= _restartAt[n])
                {
                    if (_outage[n / NodesPerRegion]) { _restartAt[n] = _clock + RestartMs; continue; }   // 리전 복구 대기
                    _alive[n] = true; _fault[n] = NodeFault.None; _vram[n] = 0.5f; _drained[n] = false; _restartAt[n] = 0; _lastBeat[n] = _clock;
                    _restarts++;
                }
            }
            for (var r = 0; r < Regions; r++) if (_surge[r] > 1 && _clock >= _surgeUntil[r]) _surge[r] = 1;

            // 2) 용량 · 라우팅
            for (var n = 0; n < Nodes; n++)
            {
                var routable = !_drained[n] && !(heal && _reactor.IsIsolated(n));
                _spare[n] = routable ? NodeCapacity * reserve : 0;
                _load[n] = 0;
            }
            var diurnal = 1 + 0.1 * Math.Sin(2 * Math.PI * _clock / DiurnalPeriodMs);
            double tickBad = 0, tickGood = 0;
            for (var r = 0; r < Regions; r++)
            {
                var demand = RegionDemand * diurnal * _surge[r];
                var be = demand * (1 - CriticalShare);
                var admitted = demand - be + be * admission;
                _shaped += (demand - admitted) * dt;
                var remaining = admitted;
                for (var k = 0; k < Regions && remaining > 1e-9; k++)
                {
                    var rr = _order[r * Regions + k];
                    var take = Assign(rr, remaining);
                    _latWeighted += take * dt * Latency[r * Regions + rr];
                    _served += take * dt;
                    remaining -= take;
                }
                var shortfall = Math.Max(0, remaining) * dt;
                tickBad += shortfall;
                _regionBad[r] += shortfall;
            }

            // 3) 서비스 결과 · 텔레메트리 → 반응기
            for (var n = 0; n < Nodes; n++)
            {
                var reg = n / NodesPerRegion;
                var served = _load[n] * dt;
                var failShare = !_alive[n] ? 1.0 : _fault[n] switch { NodeFault.Runaway => 0.8, NodeFault.GcStorm => 0.3, _ => 0.0 };
                tickBad += served * failShare; _regionBad[reg] += served * failShare;
                tickGood += served * (1 - failShare); _regionGood[reg] += served * (1 - failShare);
                if (!_alive[n]) continue;
                var util = _load[n] / (NodeCapacity * reserve);
                float cpu = 0.05f + 0.9f * util + Noise(), net = 0.9f * util + Noise(), gc = 0.04f + Noise();
                if (_fault[n] == NodeFault.MemoryLeak) _vram[n] += (float)(0.03 * dt);
                var vram = _vram[n] + Noise();
                if (_fault[n] == NodeFault.GcStorm) gc = 0.55f + Noise();
                if (_fault[n] == NodeFault.Runaway) { cpu = 0.98f; net = 0.18f * util; }
                if (_vram[n] >= 1f) { _alive[n] = false; _fault[n] = NodeFault.Crashed; _ooms++; continue; }   // OOM
                if (_reactor.Ingest(n, cpu, vram, net, gc) != AnomalyKind.None && heal) { _drains++; drained = true; }
            }
            _reactor.Advance(dtMs);
            _good += tickGood; _bad += tickBad;
            _lastTickBad = tickBad; _lastTickTotal = tickBad + tickGood;
            _budget.Record(tickGood, tickBad);
            _budget.Tick(dtMs);
        }
        if (drained) Raise();
    }

    /// <summary>리전 여유 용량에 비례 분배 — 배정량 반환.</summary>
    private double Assign(int region, double amount)
    {
        double spare = 0;
        var b = region * NodesPerRegion;
        for (var k = 0; k < NodesPerRegion; k++) spare += _spare[b + k];
        if (spare <= 0) return 0;
        var take = Math.Min(amount, spare);
        for (var k = 0; k < NodesPerRegion; k++)
        {
            var share = (float)(take * _spare[b + k] / spare);
            _load[b + k] += share;
            _spare[b + k] -= share;
        }
        return take;
    }

    private void Act(string msg) { LastAction = msg; Raise(); }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sre.Grid.OnChange", ex); }
    }
}
