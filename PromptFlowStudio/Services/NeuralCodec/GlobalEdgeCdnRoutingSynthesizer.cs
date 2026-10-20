using System.Diagnostics;
using System.Numerics;

namespace PromptFlowStudio.Services.NeuralCodec;

public enum CdnTier : byte { Origin, Edge, Peer, Spare }

/// <summary>[v17.02.0] 라우팅 SIMD 커널 — 노드 비용 벡터 합(스칼라 비트 동일) · 최소값 인덱스.</summary>
public static class CdnSimd
{
    public static void Add(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> d)
    {
        var n = d.Length; var i = 0;
        if (Vector.IsHardwareAccelerated)
            for (var w = Vector<float>.Count; i <= n - w; i += w) (new Vector<float>(a.Slice(i)) + new Vector<float>(b.Slice(i))).CopyTo(d.Slice(i));
        for (; i < n; i++) d[i] = a[i] + b[i];
    }

    public static void AddScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> d)
    {
        for (var i = 0; i < d.Length; i++) d[i] = a[i] + b[i];
    }

    /// <summary>조화 평균(처리량 추정 — 저점에 민감해 ABR 과추정 방지).</summary>
    public static float HarmonicMean(ReadOnlySpan<float> x)
    {
        var s = 0f; var n = 0;
        for (var i = 0; i < x.Length; i++) if (x[i] > 0f) { s += 1f / x[i]; n++; }
        return n == 0 ? 0f : n / s;
    }
}

/// <summary>
/// [PromptFlow Studio v17.02.0] 🌐 글로벌 에지 CDN 라우팅 &amp; 적응형 비트레이트 합성기(클라우드 오리진 · 8 에지 PoP · 지역별 P2P 스웜).
/// 매 틱: 지역 시청자(일주기 · 플래시 크라우드) · 라스트마일 대역폭(OU 확률 과정) 갱신 → 노드 가산 비용(큐잉 M/M/1 · 과부하 패널티 · 송출 단가) →
/// 지역 × 노드 RTT 행렬 + 가산 비용 SIMD 합 → 비용 순 정렬 → 잔여 용량 워터필링(최대 4 소스 · P2P 상한 50% · 잔여는 오리진 강제) →
/// 경로 RTT/손실/지터 → ABR(조화 평균 처리량 · 버퍼 기반 BOLA형 규칙 · 2 s 세그먼트) → 감시 지역 비트레이트를 코덱 레이트 제어로.
/// 노드 · 시청자 · 네트워크는 인프로세스 시뮬레이션(결정적 시드) — 실제 PoP/BGP 연동 없음.
/// </summary>
public sealed class GlobalEdgeCdnRoutingSynthesizer
{
    public const int Nodes = 16, Regions = 6, MaxSources = 4, History = 5, PeerBase = 9;
    public const float SegmentSec = 2f, MaxBufferSec = 30f, MaxP2PShare = 0.5f, Unreachable = 1e6f;
    public static readonly double[] RungMbps = { 40, 80, 160, 320, 640, 1280 };
    public static readonly string[] NodeNames =
    {
        "오리진 us-east", "서울", "도쿄", "싱가포르", "프랑크푸르트", "런던", "애시번", "오리건", "상파울루",
        "P2P 서울", "P2P 도쿄", "P2P 싱가포르", "P2P 프랑크푸르트", "P2P 뉴욕", "P2P LA", "(예비)",
    };
    public static readonly float[] NodeLat = { 39.0f, 37.6f, 35.7f, 1.35f, 50.1f, 51.5f, 39.0f, 45.6f, -23.5f, 37.6f, 35.7f, 1.35f, 50.1f, 40.7f, 34.1f, 0f };
    public static readonly float[] NodeLon = { -77.5f, 127.0f, 139.7f, 103.8f, 8.7f, -0.1f, -77.5f, -122.7f, -46.6f, 127.0f, 139.7f, 103.8f, 8.7f, -74.0f, -118.2f, 0f };
    public static readonly string[] RegionNames = { "서울", "도쿄", "싱가포르", "프랑크푸르트", "뉴욕", "로스앤젤레스" };
    public static readonly float[] RegionLat = { 37.6f, 35.7f, 1.35f, 50.1f, 40.7f, 34.1f };
    public static readonly float[] RegionLon = { 127.0f, 139.7f, 103.8f, 8.7f, -74.0f, -118.2f };
    public static readonly string[] TierNames = { "오리진", "에지", "P2P", "예비" };
    private static readonly float[] BaseCapGbps = { 4000f, 800f, 700f, 600f, 800f, 700f, 1000f, 600f, 300f };
    private static readonly float[] RegionViewers = { 1800f, 1500f, 900f, 1300f, 2000f, 1600f };
    private static readonly float[] RegionMileMbps = { 1400f, 1100f, 700f, 900f, 1000f, 850f };
    private static readonly float[] RegionPhase = { 0f, 0.02f, 0.08f, 0.35f, 0.55f, 0.65f };

    private readonly CdnTier[] _tier = new CdnTier[Nodes];
    private readonly bool[] _up = new bool[Nodes];
    private readonly float[] _cap = new float[Nodes], _load = new float[Nodes], _bg = new float[Nodes], _res = new float[Nodes];
    private readonly float[] _nodeAdd = new float[Nodes], _queue = new float[Nodes], _cost = new float[Nodes], _utilS = new float[Nodes];
    private readonly int[] _order = new int[Nodes], _regionOrder = new int[Regions];
    private readonly float[] _rttBase = new float[Regions * Nodes], _alloc = new float[Regions * Nodes];
    private readonly float[] _viewers = new float[Regions], _mile = new float[Regions], _demand = new float[Regions];
    private readonly float[] _hist = new float[Regions * History], _sample = new float[Regions], _est = new float[Regions];
    private readonly float[] _buffer = new float[Regions], _segAcc = new float[Regions], _crowdT = new float[Regions], _dropT = new float[Regions];
    private readonly float[] _rtt = new float[Regions], _loss = new float[Regions], _jitter = new float[Regions];
    private readonly int[] _rung = new int[Regions], _histN = new int[Regions], _primary = new int[Regions], _stalls = new int[Regions];
    private readonly bool[] _stalled = new bool[Regions];
    private ulong _rng = 0x9E3779B97F4A7C15UL;

    public GlobalEdgeCdnRoutingSynthesizer()
    {
        for (var j = 0; j < Nodes; j++)
        {
            _tier[j] = j == 0 ? CdnTier.Origin : j < PeerBase ? CdnTier.Edge : j < PeerBase + Regions ? CdnTier.Peer : CdnTier.Spare;
            _up[j] = _tier[j] != CdnTier.Spare;
            _cap[j] = j < PeerBase ? BaseCapGbps[j] : 1f;
            _bg[j] = _tier[j] == CdnTier.Edge ? 0.25f : 0.05f;
        }
        for (var r = 0; r < Regions; r++)
        {
            _mile[r] = RegionMileMbps[r];
            _buffer[r] = 8f;
            _rung[r] = 2;
            _primary[r] = -1;
            for (var j = 0; j < Nodes; j++)
            {
                float v;
                if (_tier[j] == CdnTier.Spare) v = Unreachable;
                else if (_tier[j] == CdnTier.Peer) v = j - PeerBase == r ? 14f : Unreachable;
                else
                {
                    var km = GreatCircleKm(RegionLat[r], RegionLon[r], NodeLat[j], NodeLon[j]);
                    v = 2f * km / 200f * 1.35f + 3f + (_tier[j] == CdnTier.Origin ? 8f : 0f);
                }
                _rttBase[r * Nodes + j] = v;
            }
        }
    }

    public int ViewerRegion { get; set; }
    public double Now { get; private set; }
    public long Steps { get; private set; }
    public long Reroutes { get; private set; }
    public long Switches { get; private set; }
    public long Failovers { get; private set; }
    public double LastStepUs { get; private set; }
    public float TotalDemandGbps { get; private set; }
    public float EdgeShare { get; private set; }
    public float PeerShare { get; private set; }
    public float OriginShare { get; private set; }
    public float AvgRttMs { get; private set; }
    public string? LastEvent { get; private set; }

    public CdnTier Tier(int j) => _tier[j];
    public bool NodeUp(int j) => _up[j];
    public float NodeLoadGbps(int j) => _load[j];
    public float NodeCapacityGbps(int j) => _cap[j];
    public float NodeUtil(int j) => _cap[j] <= 0f ? 0f : _load[j] / _cap[j];
    public float Alloc(int r, int j) => _alloc[r * Nodes + j];
    public float BaseRttMs(int r, int j) => _rttBase[r * Nodes + j];
    public int Primary(int r) => _primary[r];
    public float Viewers(int r) => _viewers[r];
    public float DemandGbps(int r) => _demand[r];
    public float LastMileMbps(int r) => _mile[r];
    public float ThroughputMbps(int r) => _sample[r];
    public float EstimateMbps(int r) => _est[r];
    public float BufferSec(int r) => _buffer[r];
    public int Rung(int r) => _rung[r];
    public int Stalls(int r) => _stalls[r];
    public bool Stalled(int r) => _stalled[r];
    public float RegionRttMs(int r) => _rtt[r];
    public float RegionLoss(int r) => _loss[r];
    public float RegionJitterMs(int r) => _jitter[r];
    public bool CrowdActive(int r) => _crowdT[r] > 0f;
    public bool DropActive(int r) => _dropT[r] > 0f;

    public double ViewerRateMbps => RungMbps[_rung[ViewerRegion]];
    public float PathRttMs => _rtt[ViewerRegion];
    public float PathLoss => _loss[ViewerRegion];
    public float PathJitterMs => _jitter[ViewerRegion];
    public int PathPrimary => _primary[ViewerRegion];
    public int TotalStalls { get { var s = 0; for (var r = 0; r < Regions; r++) s += _stalls[r]; return s; } }

    public static float GreatCircleKm(float lat1, float lon1, float lat2, float lon2)
    {
        const double d2r = Math.PI / 180;
        var dlat = (lat2 - lat1) * d2r; var dlon = (lon2 - lon1) * d2r;
        var a = Math.Sin(dlat / 2) * Math.Sin(dlat / 2) + Math.Cos(lat1 * d2r) * Math.Cos(lat2 * d2r) * Math.Sin(dlon / 2) * Math.Sin(dlon / 2);
        return (float)(2 * 6371.0 * Math.Asin(Math.Min(1, Math.Sqrt(a))));
    }

    private static float QueueMs(float rho) { var p = Math.Clamp(rho, 0f, 0.98f); return 2f * p / (1f - p); }

    private float NodeLoss(int j)
    {
        var l = 0.001f;
        if (_tier[j] == CdnTier.Peer) l += 0.012f;
        var rho = NodeUtil(j);
        if (rho > 0.9f) l += (rho - 0.9f) * 0.25f;
        return MathF.Min(l, 0.3f);
    }

    private float Gauss()
    {
        var u1 = Next01(); var u2 = Next01();
        return MathF.Sqrt(-2f * MathF.Log(MathF.Max(u1, 1e-7f))) * MathF.Cos(MathF.Tau * u2);
    }

    private float Next01()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / 16777216f;
    }

    // ── 이벤트 ──
    public void FailNode(int j)
    {
        if (j <= 0 || j >= PeerBase || !_up[j]) return;
        _up[j] = false; Failovers++;
        LastEvent = "⛔ PoP 장애 · " + NodeNames[j];
    }

    public void RestoreNode(int j)
    {
        if (j < 0 || j >= PeerBase || _up[j]) return;
        _up[j] = true;
        LastEvent = "✅ PoP 복구 · " + NodeNames[j];
    }

    public void ToggleNode(int j) { if (_up[j]) FailNode(j); else RestoreNode(j); }
    public void FlashCrowd(int r, float seconds = 20f) { _crowdT[r] = seconds; LastEvent = "👥 플래시 크라우드 · " + RegionNames[r]; }
    public void BandwidthDrop(int r, float seconds = 15f) { _dropT[r] = seconds; LastEvent = "📉 라스트마일 대역폭 급락 · " + RegionNames[r]; }

    /// <summary>1틱(무할당).</summary>
    public void Step(float dt, double now)
    {
        var t0 = Stopwatch.GetTimestamp();
        Now = now;
        // ── 수요 · 라스트마일 ──
        for (var r = 0; r < Regions; r++)
        {
            if (_crowdT[r] > 0f) _crowdT[r] -= dt;
            if (_dropT[r] > 0f) _dropT[r] -= dt;
            var diurnal = 0.6f + 0.4f * MathF.Sin(MathF.Tau * (float)(now / 120.0 + RegionPhase[r]));
            _viewers[r] = RegionViewers[r] * diurnal * (_crowdT[r] > 0f ? 3f : 1f);
            var mean = RegionMileMbps[r] * (_dropT[r] > 0f ? 0.25f : 1f);
            _mile[r] = MathF.Max(5f, _mile[r] + 0.8f * (mean - _mile[r]) * dt + 0.12f * mean * MathF.Sqrt(dt) * Gauss());
            _demand[r] = _viewers[r] * (float)RungMbps[_rung[r]] / 1000f;
            var pj = PeerBase + r;
            _cap[pj] = _viewers[r] * 60f * 0.5f / 1000f;   // 시청자 업로드 60 Mbps × 참여 50%
            _up[pj] = _viewers[r] > 50f;
        }
        // ── 노드 가산 비용(직전 부하) ──
        for (var j = 0; j < Nodes; j++)
        {
            _utilS[j] += (NodeUtil(j) - _utilS[j]) * MathF.Min(1f, 2f * dt);   // 평활 이용률(진동 억제 · 0.5 s)
            var rho = _utilS[j];
            _queue[j] = QueueMs(rho);
            var price = _tier[j] == CdnTier.Origin ? 30f : _tier[j] == CdnTier.Edge ? 12f : 0f;   // 송출 단가(ms 등가) — P2P 오프로드 우선
            _nodeAdd[j] = _up[j] ? _queue[j] + (rho > 0.85f ? 300f * (rho - 0.85f) : 0f) + price : Unreachable;
            if (_tier[j] == CdnTier.Edge) _bg[j] = Math.Clamp(_bg[j] + 0.02f * MathF.Sqrt(dt) * Gauss(), 0.1f, 0.45f);
            _res[j] = _up[j] ? _cap[j] * MathF.Max(0f, 0.9f - _bg[j]) : 0f;   // 10% 헤드룸
        }
        // ── 지역 정렬(수요 내림차순 · 삽입 정렬) ──
        for (var r = 0; r < Regions; r++) _regionOrder[r] = r;
        for (var a = 1; a < Regions; a++)
        {
            var v = _regionOrder[a]; var b = a - 1;
            while (b >= 0 && _demand[_regionOrder[b]] < _demand[v]) { _regionOrder[b + 1] = _regionOrder[b]; b--; }
            _regionOrder[b + 1] = v;
        }
        Array.Clear(_alloc);
        float edge = 0f, peer = 0f, origin = 0f;
        for (var k = 0; k < Regions; k++)
        {
            var r = _regionOrder[k];
            CdnSimd.Add(_rttBase.AsSpan(r * Nodes, Nodes), _nodeAdd, _cost);
            for (var j = 0; j < Nodes; j++) _order[j] = j;
            for (var a = 1; a < Nodes; a++)
            {
                var v = _order[a]; var b = a - 1;
                while (b >= 0 && _cost[_order[b]] > _cost[v]) { _order[b + 1] = _order[b]; b--; }
                _order[b + 1] = v;
            }
            var left = _demand[r]; var sources = 0;
            for (var i = 0; i < Nodes && left > 1e-6f && sources < MaxSources; i++)
            {
                var j = _order[i];
                if (_cost[j] >= Unreachable * 0.5f) break;
                var avail = _tier[j] == CdnTier.Peer ? MathF.Min(_res[j], _demand[r] * MaxP2PShare) : _res[j];
                if (j == 0) avail = left;   // 오리진 = 최종 보장
                var take = MathF.Min(left, avail);
                if (take <= 1e-6f) continue;
                _alloc[r * Nodes + j] += take; _res[j] -= take; left -= take; sources++;
            }
            if (left > 1e-6f) { _alloc[r * Nodes] += left; _res[0] -= left; }
            var prev = _primary[r]; var best = 0; var bestA = -1f;
            for (var j = 0; j < Nodes; j++) if (_alloc[r * Nodes + j] > bestA) { bestA = _alloc[r * Nodes + j]; best = j; }
            _primary[r] = best;
            if (prev >= 0 && prev != best) Reroutes++;
        }
        // ── 부하 · 경로 지표 ──
        for (var j = 0; j < Nodes; j++) _load[j] = _up[j] ? _cap[j] * _bg[j] : 0f;
        for (var r = 0; r < Regions; r++)
            for (var j = 0; j < Nodes; j++)
            {
                var a = _alloc[r * Nodes + j];
                if (a <= 0f) continue;
                _load[j] += a;
                if (_tier[j] == CdnTier.Origin) origin += a; else if (_tier[j] == CdnTier.Peer) peer += a; else edge += a;
            }
        float rttW = 0f, vw = 0f;
        for (var r = 0; r < Regions; r++)
        {
            float rtt = 0f, loss = 0f, jit = 0f, cong = 0f;
            var d = MathF.Max(_demand[r], 1e-6f);
            for (var j = 0; j < Nodes; j++)
            {
                var a = _alloc[r * Nodes + j];
                if (a <= 0f) continue;
                var w = a / d;
                var q = QueueMs(NodeUtil(j));
                var lr = _rttBase[r * Nodes + j] + q;
                rtt += w * lr;
                loss += w * NodeLoss(j);
                jit += w * (0.8f + 0.04f * lr + 0.5f * q + (_tier[j] == CdnTier.Peer ? 3f : 0f));
                cong += w * Math.Clamp((NodeUtil(j) - 0.92f) * 6f, 0f, 0.85f);
            }
            _rtt[r] = rtt; _loss[r] = loss; _jitter[r] = jit;
            rttW += rtt * _viewers[r]; vw += _viewers[r];
            _sample[r] = _mile[r] * (1f - cong);
            Abr(r, dt);
        }
        TotalDemandGbps = edge + peer + origin;
        var tot = MathF.Max(TotalDemandGbps, 1e-6f);
        EdgeShare = edge / tot; PeerShare = peer / tot; OriginShare = origin / tot;
        AvgRttMs = vw > 0f ? rttW / vw : 0f;
        Steps++;
        LastStepUs = (Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency;
    }

    /// <summary>버퍼 진행 + 세그먼트 경계 ABR 결정(조화 평균 · 버퍼 하한 강등 · 여유 시 1단 승급).</summary>
    private void Abr(int r, float dt)
    {
        var rate = (float)RungMbps[_rung[r]];
        var fill = dt * _sample[r] / rate;
        if (_stalled[r])
        {
            _buffer[r] += fill;
            if (_buffer[r] >= 1f) _stalled[r] = false;
        }
        else
        {
            _buffer[r] = MathF.Min(MaxBufferSec, _buffer[r] + fill - dt);
            if (_buffer[r] <= 0f) { _buffer[r] = 0f; _stalled[r] = true; _stalls[r]++; }
        }
        _segAcc[r] += dt;
        if (_segAcc[r] < SegmentSec) return;
        _segAcc[r] = 0f;
        _hist[r * History + _histN[r] % History] = _sample[r];
        _histN[r]++;
        _est[r] = CdnSimd.HarmonicMean(_hist.AsSpan(r * History, Math.Min(_histN[r], History)));
        var cand = 0;
        for (var i = RungMbps.Length - 1; i >= 0; i--) if (RungMbps[i] <= 0.8 * _est[r]) { cand = i; break; }
        var cur = _rung[r];
        int next;
        if (_buffer[r] < 4f) next = Math.Min(cand, Math.Max(0, cur - 1));
        else if (cand > cur) next = _buffer[r] > 12f ? cur + 1 : cur;
        else next = cand;
        if (next != cur) { _rung[r] = next; Switches++; }
    }
}
