using System.Globalization;

namespace PromptFlowStudio.Services.GlobalOps;

public enum EdgeNodeKind : byte { LocalGpu, CloudGpu, EdgeCdn }

public enum EdgeNodeState : byte { Free, Provisioning, Active, Draining, Unhealthy, Standby }

public enum ScaleDecision : byte { Hold, ScaleOut, ScaleIn, Replace }

/// <summary>스케일 이벤트(시각 · 결정 · 노드 · 사유 코드 — 문자열은 UI 지연 포맷).</summary>
public readonly record struct ScaleEvent(double TimeMs, ScaleDecision Decision, int Node, int Active, double Utilization, double Forecast);

/// <summary>
/// [PromptFlow Studio v16.52.0] 📈 글로벌 SLA 오토스케일링 &amp; 에지 로드밸런서.
///   ▸ 고정 노드 풀(128 슬롯 · 리전 · 로컬/클라우드/에지 · 용량 · 기본 지연) — 배열 기반 · 프로비저닝 지연 · 드레이닝 · 헬스(연속 실패 → Unhealthy → 교체).
///   ▸ 라우팅: 리전 친화 + 가중 최소 연결 Power-of-Two-Choices(결정적 xorshift) · 과부하 노드 회피 · 전 노드 포화 시 거부(SLA 계상) — Route/Complete 무할당.
///   ▸ 웜 풀(기본 8 · 프로비저닝 완료 후 Standby → 스케일아웃 시 즉시 Active · 자동 보충) · 오토스케일: 이용률 EWMA + 추세 예측(Holt) · 목표 이용률 · 스파이크 즉시 다중 스케일아웃 · 스케일아웃 단기 쿨다운(0.5초) · 히스테리시스 스케일인(3초)(드레이닝 후 해제) · 최소/최대 노드.
///   ▸ SLA: 초 단위 성공/실패 링(1시간) · 가동률 · 99.999% 오류 예산 잔량 · 다중 윈도 번 레이트(5분/1시간) 경보 · p99 지연 근사(로그 히스토그램).
/// </summary>
public sealed class GlobalSlaAutoScalingLoadBalancer
{
    public const int MaxNodes = 128, Regions = 6, SlaRing = 3600, LatBuckets = 32, EventCap = 256;
    public const double SlaTarget = 0.99999;

    public static readonly string[] RegionNames = { "ap-northeast-2", "ap-northeast-1", "us-west-2", "us-east-1", "eu-central-1", "sa-east-1" };

    private readonly EdgeNodeKind[] _kind = new EdgeNodeKind[MaxNodes];
    private readonly EdgeNodeState[] _state = new EdgeNodeState[MaxNodes];
    private readonly int[] _region = new int[MaxNodes], _capacity = new int[MaxNodes], _active = new int[MaxNodes], _fails = new int[MaxNodes];
    private readonly double[] _baseLat = new double[MaxNodes], _readyAt = new double[MaxNodes];
    private readonly long[] _served = new long[MaxNodes];
    private readonly bool[] _warm = new bool[MaxNodes];
    private readonly long[] _ok = new long[SlaRing], _bad = new long[SlaRing];
    private readonly long[] _lat = new long[LatBuckets];
    private readonly ScaleEvent[] _events = new ScaleEvent[EventCap];
    private readonly object _gate = new();
    private uint _rng = 2463534242;
    private int _count, _evHead, _evCount;
    private long _sec = -1, _totalOk, _totalBad, _rejected, _scaleOuts, _scaleIns, _replacements, _requestsWindow, _overflows, _rejectedWindow, _warmActivations;
    private double _util, _trend, _forecast, _lastScale = double.NegativeInfinity, _lastTick;
    private bool _burnAlert;

    public GlobalSlaAutoScalingLoadBalancer()
    {
        AddNode(EdgeNodeKind.LocalGpu, 0, 40, 18, 0);
        AddNode(EdgeNodeKind.CloudGpu, 0, 60, 25, 0);
        AddNode(EdgeNodeKind.CloudGpu, 2, 60, 30, 0);
        AddNode(EdgeNodeKind.EdgeCdn, 4, 80, 22, 0);
    }

    public event Action? OnChange;

    public double TargetUtilization { get; set; } = 0.65;
    public double ScaleInBelow { get; set; } = 0.35;
    public double SpikeUtilization { get; set; } = 0.9;
    public double CooldownMs { get; set; } = 3000;
    public double ScaleOutCooldownMs { get; set; } = 500;
    public int WarmPoolSize { get; set; } = 8;
    public long WarmActivations => Interlocked.Read(ref _warmActivations);
    public double ProvisionMs { get; set; } = 2000;
    public int MinNodes { get; set; } = 3;
    public int MaxActive { get; set; } = 96;
    public int CloudCapacity { get; set; } = 60;
    public int FailThreshold { get; set; } = 5;
    public double OverflowFactor { get; set; } = 1.5;
    public long Overflows => Interlocked.Read(ref _overflows);
    public int NodeCount => _count;
    public EdgeNodeKind Kind(int i) => _kind[i];
    public EdgeNodeState State(int i) => _state[i];
    public int Region(int i) => _region[i];
    public int Capacity(int i) => _capacity[i];
    public int ActiveConnections(int i) => Volatile.Read(ref _active[i]);
    public long Served(int i) => Interlocked.Read(ref _served[i]);
    public double NodeUtilization(int i) => _capacity[i] <= 0 ? 0 : Volatile.Read(ref _active[i]) / (double)_capacity[i];
    public double Utilization => _util;
    public double Forecast => _forecast;
    public long Rejected => Interlocked.Read(ref _rejected);
    public long ScaleOuts => Interlocked.Read(ref _scaleOuts);
    public long ScaleIns => Interlocked.Read(ref _scaleIns);
    public long Replacements => Interlocked.Read(ref _replacements);
    public long TotalOk => Interlocked.Read(ref _totalOk);
    public long TotalBad => Interlocked.Read(ref _totalBad);
    public bool BurnAlert => _burnAlert;
    public string? LastAction { get; private set; }

    public int CountState(EdgeNodeState s) { var n = 0; for (var i = 0; i < _count; i++) if (_state[i] == s) n++; return n; }

    public int ActiveCapacity { get { var c = 0; for (var i = 0; i < _count; i++) if (_state[i] == EdgeNodeState.Active) c += _capacity[i]; return c; } }

    // ══════════════════════ 노드 풀 ══════════════════════

    /// <summary>노드 추가(빈 슬롯 재사용). readyMs 이후 Active. 반환 = 슬롯(-1 = 풀 포화).</summary>
    public int AddNode(EdgeNodeKind kind, int region, int capacity, double baseLatencyMs, double readyAtMs)
    {
        lock (_gate)
        {
            var slot = -1;
            for (var i = 0; i < _count; i++) if (_state[i] == EdgeNodeState.Free) { slot = i; break; }
            if (slot < 0) { if (_count >= MaxNodes) return -1; slot = _count++; }
            _kind[slot] = kind; _region[slot] = region; _capacity[slot] = capacity; _baseLat[slot] = baseLatencyMs;
            Volatile.Write(ref _active[slot], 0); _fails[slot] = 0; _readyAt[slot] = readyAtMs; _warm[slot] = false;
            _state[slot] = readyAtMs <= 0 ? EdgeNodeState.Active : EdgeNodeState.Provisioning;
            return slot;
        }
    }

    // ══════════════════════ 라우팅 ══════════════════════

    private uint Next() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return x; }

    /// <summary>요청 라우팅(리전 친화 P2C · 가중 최소 연결). 반환 = 노드(-1 = 포화 거부). 무할당.</summary>
    public int Route(int clientRegion)
    {
        lock (_gate)
        {
            var n = _count;
            int a = -1, b = -1;
            // 1) 같은 리전 후보 2개(무작위 시작점 순환 스캔 → 결정적 · 편향 없음)
            var start = n == 0 ? 0 : (int)(Next() % (uint)n);
            for (var t = 0; t < n && (a < 0 || b < 0); t++)
            {
                var i = (start + t) % n;
                if (!Routable(i) || _region[i] != clientRegion) continue;
                if (a < 0) a = i; else b = i;
            }
            // 2) 리전 후보 부족 → 전역 후보
            for (var t = 0; t < n * 2 && (a < 0 || b < 0); t++)
            {
                var i = (int)(Next() % (uint)n);
                if (!Routable(i)) continue;
                if (a < 0) a = i; else if (i != a) b = i;
            }
            if (a < 0)
            {
                for (var i = 0; i < n; i++) if (Routable(i)) { a = i; break; }
                if (a < 0)
                {
                    // 3) 전 노드 정원 포화 → 큐잉 오버커밋(정원 × OverflowFactor 이내 최저 부하 노드) — 지연 증가로 흡수, 그 이상만 거부
                    var best = -1; var bu = double.MaxValue;
                    for (var i = 0; i < n; i++)
                    {
                        if (_state[i] != EdgeNodeState.Active || _active[i] >= _capacity[i] * OverflowFactor) continue;
                        var u = Cost(i, clientRegion);
                        if (u < bu) { bu = u; best = i; }
                    }
                    if (best < 0) { Interlocked.Increment(ref _rejected); _rejectedWindow++; RecordOutcome(false, _lastTick); return -1; }
                    _active[best]++;
                    Interlocked.Increment(ref _served[best]);
                    Interlocked.Increment(ref _overflows);
                    _requestsWindow++;
                    return best;
                }
            }
            var pick = b < 0 || Cost(a, clientRegion) <= Cost(b, clientRegion) ? a : b;
            _active[pick]++;
            Interlocked.Increment(ref _served[pick]);
            _requestsWindow++;
            return pick;
        }
    }

    private bool Routable(int i) => _state[i] == EdgeNodeState.Active && _active[i] < _capacity[i];

    private double Cost(int i, int region) => (_active[i] + 1.0) / _capacity[i] + (_region[i] == region ? 0 : 0.35);

    /// <summary>예상 지연(기본 + 큐잉 + 리전 간 RTT).</summary>
    public double EstimateLatencyMs(int node, int clientRegion)
    {
        var u = NodeUtilization(node);
        return _baseLat[node] * (1 + 3 * u * u) + (_region[node] == clientRegion ? 0 : 60);
    }

    /// <summary>요청 완료(성공/실패 · 지연). 연속 실패 → Unhealthy. 무할당.</summary>
    public void Complete(int node, bool ok, double latencyMs, double nowMs)
    {
        if ((uint)node >= (uint)_count) return;
        lock (_gate)
        {
            if (_active[node] > 0) _active[node]--;
            if (ok) _fails[node] = 0;
            else if (++_fails[node] >= FailThreshold && _state[node] == EdgeNodeState.Active) _state[node] = EdgeNodeState.Unhealthy;
            RecordOutcome(ok, nowMs);
            var bucket = latencyMs <= 1 ? 0 : Math.Min(LatBuckets - 1, (int)(Math.Log2(latencyMs) * 2));
            _lat[bucket]++;
        }
    }

    private void RecordOutcome(bool ok, double nowMs)
    {
        var sec = (long)(nowMs / 1000);
        if (sec != _sec)
        {
            var steps = _sec < 0 ? SlaRing : Math.Min(SlaRing, sec - _sec);
            for (var k = 1; k <= steps; k++) { var idx = (int)(((_sec < 0 ? sec : _sec) + k) % SlaRing); _ok[idx] = 0; _bad[idx] = 0; }
            _sec = sec;
        }
        var j = (int)(sec % SlaRing);
        if (ok) { _ok[j]++; Interlocked.Increment(ref _totalOk); }
        else { _bad[j]++; Interlocked.Increment(ref _totalBad); }
    }

    // ══════════════════════ SLA ══════════════════════

    /// <summary>윈도(초) 가동률. 요청 0이면 1.</summary>
    public double Availability(int windowSec)
    {
        lock (_gate)
        {
            if (_sec < 0) return 1;
            long ok = 0, bad = 0;
            for (var k = 0; k < Math.Min(windowSec, SlaRing); k++) { var j = (int)(((_sec - k) % SlaRing + SlaRing) % SlaRing); ok += _ok[j]; bad += _bad[j]; }
            return ok + bad == 0 ? 1 : ok / (double)(ok + bad);
        }
    }

    public double LifetimeAvailability { get { var o = TotalOk; var b = TotalBad; return o + b == 0 ? 1 : o / (double)(o + b); } }

    /// <summary>오류 예산 잔량(0~1): 허용 실패 = 총 요청 × (1-목표).</summary>
    public double ErrorBudgetRemaining { get { var tot = TotalOk + TotalBad; var allowed = tot * (1 - SlaTarget); return allowed <= 0 ? (TotalBad == 0 ? 1 : 0) : Math.Max(0, 1 - TotalBad / allowed); } }

    /// <summary>번 레이트 = 윈도 실패율 / 허용 실패율.</summary>
    public double BurnRate(int windowSec) => (1 - Availability(windowSec)) / (1 - SlaTarget);

    /// <summary>p 분위 지연 근사(ms).</summary>
    public double LatencyPercentile(double p)
    {
        lock (_gate)
        {
            long tot = 0;
            foreach (var c in _lat) tot += c;
            if (tot == 0) return 0;
            var target = (long)Math.Ceiling(tot * p); long acc = 0;
            for (var b = 0; b < LatBuckets; b++) { acc += _lat[b]; if (acc >= target) return Math.Pow(2, (b + 1) / 2.0); }
            return Math.Pow(2, LatBuckets / 2.0);
        }
    }

    // ══════════════════════ 오토스케일 ══════════════════════

    /// <summary>주기 틱: 프로비저닝 완료 · 드레이닝 해제 · 비정상 교체 · 이용률/예측 → 스케일 결정. 반환 = 결정. 무할당.</summary>
    public ScaleDecision Tick(double nowMs)
    {
        var decision = ScaleDecision.Hold;
        var changed = false;
        lock (_gate)
        {
            _lastTick = nowMs;
            for (var i = 0; i < _count; i++)
            {
                if (_state[i] == EdgeNodeState.Provisioning && nowMs >= _readyAt[i]) { _state[i] = _warm[i] ? EdgeNodeState.Standby : EdgeNodeState.Active; _warm[i] = false; changed = true; }
                else if (_state[i] == EdgeNodeState.Draining && _active[i] == 0) { _state[i] = EdgeNodeState.Free; changed = true; }
                else if (_state[i] == EdgeNodeState.Unhealthy)
                {
                    _state[i] = EdgeNodeState.Free; _active[i] = 0;
                    var r = ProvisionLocked(_region[i], nowMs);
                    Interlocked.Increment(ref _replacements);
                    Push(new ScaleEvent(nowMs, ScaleDecision.Replace, r, CountActiveLocked(), _util, _forecast));
                    decision = ScaleDecision.Replace; changed = true;
                }
            }
            int load = 0, cap = 0, act = 0, prov = 0, warm = 0;
            for (var i = 0; i < _count; i++)
            {
                if (_state[i] == EdgeNodeState.Active) { load += _active[i]; cap += _capacity[i]; act++; }
                else if (_state[i] == EdgeNodeState.Provisioning && !_warm[i]) { cap += _capacity[i]; prov++; }
                else if (_state[i] == EdgeNodeState.Standby || _state[i] == EdgeNodeState.Provisioning) warm++;
            }
            var demand = load + _rejectedWindow * 2;                                                         // 거부 = 미충족 수요(정원 포화로 load 과소추정 보정)
            var u = cap == 0 ? 1 : demand / (double)cap;
            var prevUtil = _util;
            _util = _util <= 0 ? u : _util * 0.6 + u * 0.4;
            _trend = _trend * 0.7 + (_util - prevUtil) * 0.3;
            _forecast = Math.Max(0, _util + _trend * 5);
            var dt = nowMs - _lastScale;
            if (decision == ScaleDecision.Hold && (u >= SpikeUtilization || _forecast > TargetUtilization) && dt >= ScaleOutCooldownMs && act + prov < MaxActive)
            {
                // 예측 수요(현재 수요 vs 추세 예측 중 큰 값) 기준 목표 이용률 달성에 부족한 용량(준비 중 포함) → 노드 수(스파이크 = 즉시 다중)
                var forecastDemand = Math.Max(demand, _forecast * cap);
                var need = (int)Math.Ceiling((forecastDemand / TargetUtilization - cap) / Math.Max(1, CloudCapacity));
                need = Math.Clamp(need, u >= SpikeUtilization ? 1 : 0, MaxActive - act - prov);
                for (var k = 0; k < need; k++)
                {
                    var slot = ActivateStandbyLocked(BusiestRegionLocked());                                   // 웜 풀 즉시 활성화 우선
                    if (slot >= 0) { Interlocked.Increment(ref _warmActivations); warm--; }
                    else slot = ProvisionLocked(BusiestRegionLocked(), nowMs);
                    if (slot < 0) break;
                    Push(new ScaleEvent(nowMs, ScaleDecision.ScaleOut, slot, act, u, _forecast));
                    Interlocked.Increment(ref _scaleOuts);
                    decision = ScaleDecision.ScaleOut;
                }
                if (decision == ScaleDecision.ScaleOut) { _lastScale = nowMs; changed = true; }
            }
            else if (decision == ScaleDecision.Hold && _util < ScaleInBelow && _forecast < ScaleInBelow && act > MinNodes && dt >= CooldownMs * 2 && prov == 0)
            {
                var victim = -1;
                for (var i = 0; i < _count; i++)
                    if (_state[i] == EdgeNodeState.Active && _kind[i] == EdgeNodeKind.CloudGpu && (victim < 0 || _active[i] < _active[victim])) victim = i;
                if (victim >= 0)
                {
                    _state[victim] = EdgeNodeState.Draining;
                    Push(new ScaleEvent(nowMs, ScaleDecision.ScaleIn, victim, act - 1, u, _forecast));
                    Interlocked.Increment(ref _scaleIns);
                    _lastScale = nowMs; decision = ScaleDecision.ScaleIn; changed = true;
                }
            }
            // 웜 풀 보충(프로비저닝 완료 시 Standby — 라우팅 제외 · 스케일아웃 시 즉시 Active)
            for (var k = warm; k < WarmPoolSize && act + prov + k < MaxActive; k++)
            {
                var slot = ProvisionLocked(k % Regions, nowMs);
                if (slot < 0) break;
                _warm[slot] = true;
            }
            var burn5 = BurnRateLocked(300); var burn60 = BurnRateLocked(3600);
            _burnAlert = burn5 > 14.4 && burn60 > 6;
            _requestsWindow = 0; _rejectedWindow = 0;
        }
        if (decision != ScaleDecision.Hold) LastAction = decision switch
        {
            ScaleDecision.ScaleOut => "⬆ 스케일아웃 · 이용률 " + (_util * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
            ScaleDecision.ScaleIn => "⬇ 스케일인(드레이닝)",
            _ => "♻ 비정상 노드 교체",
        };
        if (changed) Raise();
        return decision;
    }

    private double BurnRateLocked(int windowSec)
    {
        if (_sec < 0) return 0;
        long ok = 0, bad = 0;
        for (var k = 0; k < Math.Min(windowSec, SlaRing); k++) { var j = (int)(((_sec - k) % SlaRing + SlaRing) % SlaRing); ok += _ok[j]; bad += _bad[j]; }
        return ok + bad == 0 ? 0 : bad / (double)(ok + bad) / (1 - SlaTarget);
    }

    private int ProvisionLocked(int region, double nowMs)
    {
        var slot = -1;
        for (var i = 0; i < _count; i++) if (_state[i] == EdgeNodeState.Free) { slot = i; break; }
        if (slot < 0) { if (_count >= MaxNodes) return -1; slot = _count++; }
        _kind[slot] = EdgeNodeKind.CloudGpu; _region[slot] = region; _capacity[slot] = CloudCapacity; _baseLat[slot] = 24 + region * 2;
        _active[slot] = 0; _fails[slot] = 0; _readyAt[slot] = nowMs + ProvisionMs; _state[slot] = EdgeNodeState.Provisioning; _warm[slot] = false;
        return slot;
    }

    /// <summary>Standby 노드 활성화(선호 리전 우선). 반환 = 슬롯(-1 없음).</summary>
    private int ActivateStandbyLocked(int region)
    {
        var any = -1;
        for (var i = 0; i < _count; i++)
        {
            if (_state[i] != EdgeNodeState.Standby) continue;
            if (_region[i] == region) { _state[i] = EdgeNodeState.Active; return i; }
            if (any < 0) any = i;
        }
        if (any >= 0) _state[any] = EdgeNodeState.Active;
        return any;
    }

    private int BusiestRegionLocked()
    {
        Span<int> load = stackalloc int[Regions];
        Span<int> cap = stackalloc int[Regions];
        for (var i = 0; i < _count; i++)
            if (_state[i] is EdgeNodeState.Active or EdgeNodeState.Provisioning) { load[_region[i]] += _active[i]; cap[_region[i]] += _capacity[i]; }
        var best = 0; var bu = -1.0;
        for (var r = 0; r < Regions; r++) { var u = cap[r] == 0 ? (load[r] > 0 ? 2 : 0) : load[r] / (double)cap[r]; if (u > bu) { bu = u; best = r; } }
        return best;
    }

    private int CountActiveLocked() { var n = 0; for (var i = 0; i < _count; i++) if (_state[i] == EdgeNodeState.Active) n++; return n; }

    private void Push(ScaleEvent e) { _events[_evHead] = e; _evHead = (_evHead + 1) % EventCap; if (_evCount < EventCap) _evCount++; }

    public int CopyEvents(Span<ScaleEvent> dst)
    {
        lock (_gate)
        {
            var n = Math.Min(dst.Length, _evCount);
            for (var i = 0; i < n; i++) dst[i] = _events[(_evHead - 1 - i + EventCap) % EventCap];
            return n;
        }
    }

    /// <summary>노드 장애 주입(테스트/시연).</summary>
    public void MarkUnhealthy(int node) { lock (_gate) if (_state[node] == EdgeNodeState.Active) _state[node] = EdgeNodeState.Unhealthy; Raise(); }

    public static string StateLabel(EdgeNodeState s) => s switch
    {
        EdgeNodeState.Provisioning => "준비 중", EdgeNodeState.Active => "가동", EdgeNodeState.Draining => "드레이닝", EdgeNodeState.Unhealthy => "비정상", EdgeNodeState.Standby => "웜 대기", _ => "해제",
    };

    public static string KindLabel(EdgeNodeKind k) => k switch { EdgeNodeKind.LocalGpu => "로컬 GPU", EdgeNodeKind.CloudGpu => "클라우드 GPU", _ => "에지 CDN" };

    public static string Nines(double availability)
        => availability >= 1 ? "100%" : (availability * 100).ToString("0.#####", CultureInfo.InvariantCulture) + "%";

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Sla.OnChange", ex); } }
}
