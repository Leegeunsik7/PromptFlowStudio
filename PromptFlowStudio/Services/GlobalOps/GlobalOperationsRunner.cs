using System.Diagnostics;
using System.Globalization;

namespace PromptFlowStudio.Services.GlobalOps;

/// <summary>
/// [PromptFlow Studio v16.52.0] ▶ 글로벌 운용 러너 &amp; 1-Click 최적화.
///   ▸ 20Hz 틱: 리전별 스트리밍 트래픽(일주기 + 램프형 스파이크 주입) → 로드밸런서 라우팅 → in-flight 링(고정 4096) 완료 처리(과부하 노드 실패 확률) → 오토스케일 Tick.
///   ▸ 보안: 정상 소스 서명 패킷 + 공격 주입(위조 · 재생 · 폭주 · 형식 오류) → 제로 트러스트 Inspect · 메모리 보호 영역(라우팅 가중치 표) 주기 검증 · 로그 키 유출 스캔.
///   ▸ 핫 리로딩: 노드 우선순위 점수 = engagement 슬롯 실행(교체 즉시 반영).
///   ▸ 1-Click 최적화: 예측 기반 선제 스케일 · 비정상 노드 교체 · 만료 격리 해제 · 메모리 재검증 · 카나리 미달 모듈 롤백. 루프 Step 무할당(공격 주입 버퍼 사전 할당).
/// </summary>
public sealed class GlobalOperationsRunner : IDisposable
{
    public const int Hz = 20, InFlightCap = 4096;

    private readonly GlobalSlaAutoScalingLoadBalancer _lb;
    private readonly ZeroTrustContinuousSecurityAuditor _zt;
    private readonly LiveProductionHotReloadingEngine _hot;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly int[] _ifNode = new int[InFlightCap];
    private readonly double[] _ifDone = new double[InFlightCap];
    private readonly bool[] _ifOk = new bool[InFlightCap];
    private readonly double[] _ifLat = new double[InFlightCap];
    private readonly byte[] _pkt = new byte[256], _payload = new byte[64], _replay = new byte[256];
    private readonly byte[] _weights = new byte[256];
    private readonly double[] _features = new double[4];
    private readonly ulong[] _seq = new ulong[8];
    private int _ifCount, _replayLen, _weightsRegion = -1;
    private uint _rng = 0x9E3779B9;
    private double _carry, _spikeStart, _spikeUntil, _nextAttack;
    private Thread? _thread;
    private volatile bool _run;
    private long _steps;

    public GlobalOperationsRunner(IServiceProvider sp)
        : this((GlobalSlaAutoScalingLoadBalancer)sp.GetService(typeof(GlobalSlaAutoScalingLoadBalancer))!,
               (ZeroTrustContinuousSecurityAuditor)sp.GetService(typeof(ZeroTrustContinuousSecurityAuditor))!,
               (LiveProductionHotReloadingEngine)sp.GetService(typeof(LiveProductionHotReloadingEngine))!) { }

    public GlobalOperationsRunner(GlobalSlaAutoScalingLoadBalancer lb, ZeroTrustContinuousSecurityAuditor zt, LiveProductionHotReloadingEngine hot)
    {
        _lb = lb; _zt = zt; _hot = hot;
        for (var i = 0; i < _weights.Length; i++) _weights[i] = (byte)(i * 37);
    }

    public bool IsLive => _thread is not null;
    public double BaseRps { get; set; } = 300;
    public double SpikeMultiplier { get; set; } = 6;
    public bool InjectAttacks { get; set; } = true;
    public double NowMs => _clock.Elapsed.TotalMilliseconds;
    public long Steps => Interlocked.Read(ref _steps);
    public int InFlight => _ifCount;
    public double SpikeRampMs { get; set; } = 4000;
    public bool Spiking(double nowMs) => nowMs < _spikeUntil;

    /// <summary>스파이크 배율(시작 후 램프업 → 종료 전 램프다운 · 실 트래픽 급증 곡선 근사).</summary>
    public double SpikeFactor(double nowMs)
    {
        if (nowMs >= _spikeUntil || nowMs < _spikeStart) return 1;
        var ramp = Math.Min(1, Math.Min(nowMs - _spikeStart, _spikeUntil - nowMs) / Math.Max(1, SpikeRampMs));
        return 1 + (SpikeMultiplier - 1) * ramp;
    }
    public double LastPriority { get; private set; }
    public string? LastOptimize { get; private set; }

    public void Prepare()
    {
        if (_weightsRegion < 0) _weightsRegion = _zt.Protect("LB 라우팅 가중치 표", _weights);
    }

    public void Start()
    {
        if (_thread is not null) return;
        Prepare();
        _run = true;
        var th = new Thread(Loop) { IsBackground = true, Name = "PFS.GlobalOps" };
        _thread = th;
        th.Start();
    }

    public void Stop()
    {
        var th = Interlocked.Exchange(ref _thread, null);
        if (th is null) return;
        _run = false;
        th.Join(1500);
    }

    /// <summary>트래픽 스파이크 주입(지속 ms).</summary>
    public void TriggerSpike(double nowMs, double durationMs = 12000) { _spikeStart = nowMs; _spikeUntil = nowMs + durationMs; }

    private uint Next() { var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x; return x; }
    private double Uniform() => Next() / (double)uint.MaxValue;

    /// <summary>러너 1틱(테스트/수동 구동 가능). 무할당.</summary>
    public void Step(double dtSec, double nowMs)
    {
        // 1) 트래픽 생성 · 라우팅
        var rps = BaseRps * (1 + 0.3 * Math.Sin(nowMs / 60000 * 2 * Math.PI)) * SpikeFactor(nowMs);
        _carry += rps * dtSec;
        var n = (int)_carry; _carry -= n;
        for (var k = 0; k < n; k++)
        {
            var region = (int)(Next() % GlobalSlaAutoScalingLoadBalancer.Regions);
            var node = _lb.Route(region);
            if (node < 0) continue;
            var lat = _lb.EstimateLatencyMs(node, region) * (0.7 + 0.6 * Uniform());
            var u = _lb.NodeUtilization(node);
            var ok = Uniform() > (u > 0.97 ? 0.002 : 0.0000005);
            if (_ifCount >= InFlightCap) { _lb.Complete(node, ok, lat, nowMs); continue; }
            _ifNode[_ifCount] = node; _ifDone[_ifCount] = nowMs + lat * 4; _ifOk[_ifCount] = ok; _ifLat[_ifCount] = lat; _ifCount++;   // 보유 시간 ≈ 세그먼트 전송(지연 × 4)
        }
        // 2) 완료 처리(스왑-제거)
        for (var i = 0; i < _ifCount;)
        {
            if (_ifDone[i] > nowMs) { i++; continue; }
            _lb.Complete(_ifNode[i], _ifOk[i], _ifLat[i], nowMs);
            var last = --_ifCount;
            _ifNode[i] = _ifNode[last]; _ifDone[i] = _ifDone[last]; _ifOk[i] = _ifOk[last]; _ifLat[i] = _ifLat[last];
        }
        _lb.Tick(nowMs);
        // 3) 핫 리로드 점수(우선순위)
        _features[0] = 0.04 + 0.02 * Uniform(); _features[1] = 0.6; _features[2] = _lb.Utilization; _features[3] = _lb.LatencyPercentile(0.99);
        LastPriority = _hot.Score("engagement", _features);
        // 4) 보안: 정상 패킷 + 공격 주입
        for (var s = 0; s < 4; s++)
        {
            Fill(_payload);
            var len = _zt.Seal(_pkt, (ushort)(10 + s), ++_seq[s], _payload);
            _zt.Inspect(_pkt.AsSpan(0, len), nowMs, nowMs);
            if (s == 0 && _replayLen == 0) { _pkt.AsSpan(0, len).CopyTo(_replay); _replayLen = len; }
        }
        if (InjectAttacks && nowMs >= _nextAttack)
        {
            _nextAttack = nowMs + 1500;
            switch (Next() % 4)
            {
                case 0: { var len = _zt.Seal(_pkt, 66, ++_seq[6], _payload); _pkt[HeaderPayloadIndex] ^= 0x5A; _zt.Inspect(_pkt.AsSpan(0, len), nowMs); break; }   // 위조
                case 1: if (_replayLen > 0) _zt.Inspect(_replay.AsSpan(0, _replayLen), nowMs); break;                                                              // 재생
                case 2: _pkt[0] = 0xDE; _pkt[6] = 0; _pkt[7] = 77; _zt.Inspect(_pkt.AsSpan(0, 24), nowMs); break;                                                 // 형식 오류
                default: { for (var f = 0; f < 450; f++) { var len = _zt.Seal(_pkt, 99, ++_seq[7], _payload); _zt.Inspect(_pkt.AsSpan(0, len), nowMs); } break; }   // 폭주(정상 서명 대량)
            }
            if (Next() % 5 == 0) _weights[(int)(Next() % 256)] ^= 0xFF;                                                                                             // 메모리 변조 주입
        }
        _zt.Tick(nowMs);
        Interlocked.Increment(ref _steps);
    }

    private const int HeaderPayloadIndex = ZeroTrustContinuousSecurityAuditor.HeaderSize + 3;

    private void Fill(byte[] b) { for (var i = 0; i < b.Length; i += 4) { var x = Next(); b[i] = (byte)x; b[i + 1] = (byte)(x >> 8); b[i + 2] = (byte)(x >> 16); b[i + 3] = (byte)(x >> 24); } }

    /// <summary>1-Click 글로벌 운용 최적화. 반환 = 요약.</summary>
    public string OptimizeNow(double nowMs)
    {
        // 예측 이용률 → 목표 이용률 달성까지 선제 스케일(쿨다운 무시)
        var cap = Math.Max(1, _lb.ActiveCapacity);
        var load = _lb.Utilization * cap;
        var targetCap = Math.Max(_lb.Forecast, _lb.Utilization) * cap / _lb.TargetUtilization;
        var add = (int)Math.Ceiling((targetCap - cap - _lb.CountState(EdgeNodeState.Provisioning) * _lb.CloudCapacity) / _lb.CloudCapacity);
        for (var k = 0; k < Math.Min(add, 16); k++) if (_lb.AddNode(EdgeNodeKind.CloudGpu, k % GlobalSlaAutoScalingLoadBalancer.Regions, _lb.CloudCapacity, 26, nowMs + _lb.ProvisionMs) < 0) break;
        _lb.Tick(nowMs);
        // 격리 해제(재범 없으면 다음 위반 시 즉시 재격리)
        var released = 0;
        for (var s = 0; s < ZeroTrustContinuousSecurityAuditor.MaxSources; s++) if (_zt.IsQuarantined(s, nowMs)) { _zt.ReleaseSource(s); released++; }
        var tamper = _zt.VerifyMemory(nowMs);
        // 카나리 미달 모듈 롤백
        var rolled = 0;
        var cur = _hot.Current("engagement");
        if (cur is not null && _hot.Previous("engagement") is not null)
        {
            Span<double> probe = stackalloc double[4];
            probe[0] = 0.05; probe[1] = 0.6; probe[2] = 0.5; probe[3] = 40;
            var v = _hot.Score("engagement", probe);
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0 || v > 1) { if (_hot.Rollback("engagement", DateTimeOffset.UtcNow)) rolled++; }
        }
        LastOptimize = "⚙ 최적화 · 선제 노드 +" + Math.Max(0, add).ToString(CultureInfo.InvariantCulture) + " · 격리 해제 " + released.ToString(CultureInfo.InvariantCulture)
            + " · 메모리 복원 " + tamper.ToString(CultureInfo.InvariantCulture) + " · 롤백 " + rolled.ToString(CultureInfo.InvariantCulture) + " (부하 " + load.ToString("0", CultureInfo.InvariantCulture) + ")";
        return LastOptimize;
    }

    private void Loop()
    {
        var period = 1000.0 / Hz;
        var last = NowMs;
        while (_run)
        {
            var now = NowMs;
            try { Step((now - last) / 1000, now); }
            catch (Exception ex) { PromptFlowStudio.Program.Log("GlobalOps.Tick", ex); }
            last = now;
            var wait = period - (NowMs - now);
            if (wait > 1) Thread.Sleep((int)wait);
        }
    }

    public void Dispose() => Stop();
}
