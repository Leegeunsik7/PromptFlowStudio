using System.Numerics;
using PromptFlowStudio.Services.Diagnostics;

namespace PromptFlowStudio.Services.Holo;

/// <summary>공유 월드 엔티티(홀로그램) 복제 상태: 소유 피어 · 버전(LWW) · 포즈 · 속도(데드 레커닝).</summary>
public struct HoloEntityState
{
    public int Owner;
    public uint Version;
    public Vector3 Position, Velocity;
    public Quaternion Rotation;
    public float StampMs;
}

/// <summary>
/// [PromptFlow Studio v16.77.0] ⚓ 초저지연 공간 앵커 &amp; 다중 사용자 공유 홀로그래픽 월드 라우터.
/// 피어별 로컬 좌표계 ↔ 월드: 공유 앵커 관측(≥2) → 중력 정렬 Procrustes(요 + 평행이동 · 폐형해) · 잔차 RMS(mm).
/// 홀로그램 월드 포즈 = 앵커 포즈 ∘ 로컬 오프셋 → 피어별 렌더 포즈 = 역변환. P2P 풀메시 상태 동기화: 버전 LWW · 링크 지연/손실 모델 ·
/// 직결 단절 시 최소 지연 1-홉 릴레이 우회(무단절) · 수신측 데드 레커닝 외삽. 사전 할당 큐 · 틱 무할당.
/// </summary>
public sealed class SpatialAnchorHolographicWorldRouter
{
    public const int MaxPeers = 8, MaxAnchors = 16, MaxEntities = 16, QueueCap = 4096;

    private struct Msg { public int From, To, Via, Entity; public float DeliverMs; public HoloEntityState State; }

    private readonly object _gate = new();
    private readonly Vector3[] _anchorPos = new Vector3[MaxAnchors];
    private readonly Quaternion[] _anchorRot = new Quaternion[MaxAnchors];
    private readonly Vector3[] _obs = new Vector3[MaxPeers * MaxAnchors];
    private readonly bool[] _obsValid = new bool[MaxPeers * MaxAnchors];
    private readonly float[] _peerYaw = new float[MaxPeers], _peerResidualMm = new float[MaxPeers];
    private readonly Vector3[] _peerT = new Vector3[MaxPeers];
    private readonly bool[] _peerSolved = new bool[MaxPeers];
    private readonly string[] _peerName = new string[MaxPeers];
    private readonly float[] _latency = new float[MaxPeers * MaxPeers], _loss = new float[MaxPeers * MaxPeers];
    private readonly bool[] _linkUp = new bool[MaxPeers * MaxPeers];
    private readonly HoloEntityState[] _replica = new HoloEntityState[MaxPeers * MaxEntities];
    private readonly int[] _entityAnchor = new int[MaxEntities];
    private readonly Vector3[] _entityOffset = new Vector3[MaxEntities];
    private readonly Msg[] _q = new Msg[QueueCap];
    private int _qCount, _peers, _anchors, _entities;
    private float _nowMs;
    private ulong _rng = 0x5EED_1677UL;

    public int PeerCount => _peers;
    public int AnchorCount => _anchors;
    public int EntityCount => _entities;
    public float NowMs => _nowMs;
    public long Sent { get; private set; }
    public long Delivered { get; private set; }
    public long Lost { get; private set; }
    public long Relayed { get; private set; }
    public long Unroutable { get; private set; }
    public long QueueOverflows { get; private set; }
    public float MaxStalenessMs { get; private set; }
    public float MeanResidualMm { get; private set; }
    public float PredictionErrorMm { get; private set; }
    public bool DeadReckoning { get; set; } = true;
    public string? LastAction { get; private set; }

    public string PeerName(int p) => _peerName[p] ?? "";
    public bool IsSolved(int p) => _peerSolved[p];
    public float PeerYawDeg(int p) => _peerYaw[p] * (180f / MathF.PI);
    public Vector3 PeerTranslation(int p) => _peerT[p];
    public float ResidualMm(int p) => _peerResidualMm[p];
    public bool LinkUp(int a, int b) => _linkUp[a * MaxPeers + b];
    public float LinkLatencyMs(int a, int b) => _latency[a * MaxPeers + b];
    public Vector3 AnchorPosition(int a) => _anchorPos[a];
    public HoloEntityState Replica(int peer, int e) => _replica[peer * MaxEntities + e];
    public int EntityAnchor(int e) => _entityAnchor[e];

    public void Reset()
    {
        lock (_gate)
        {
            _peers = _anchors = _entities = _qCount = 0; _nowMs = 0;
            Array.Clear(_obsValid); Array.Clear(_peerSolved); Array.Clear(_replica); Array.Clear(_linkUp);
            Sent = Delivered = Lost = Relayed = Unroutable = QueueOverflows = 0;
        }
    }

    public int AddPeer(string name)
    {
        lock (_gate)
        {
            if (_peers >= MaxPeers) return -1;
            var p = _peers++;
            _peerName[p] = name;
            _peerYaw[p] = 0; _peerT[p] = Vector3.Zero; _peerSolved[p] = p == 0; // 피어 0 = 월드 원점(호스트)
            for (var q = 0; q < _peers; q++)
            {
                SetLink(p, q, true, 8f + 6f * Math.Abs(p - q), 0f);
            }
            return p;
        }
    }

    public void SetLink(int a, int b, bool up, float latencyMs, float loss)
    {
        lock (_gate)
        {
            _linkUp[a * MaxPeers + b] = _linkUp[b * MaxPeers + a] = up;
            _latency[a * MaxPeers + b] = _latency[b * MaxPeers + a] = latencyMs;
            _loss[a * MaxPeers + b] = _loss[b * MaxPeers + a] = Math.Clamp(loss, 0f, 1f);
        }
    }

    public int AddAnchor(Vector3 worldPos, Quaternion worldRot)
    {
        lock (_gate)
        {
            if (_anchors >= MaxAnchors) return -1;
            _anchorPos[_anchors] = worldPos; _anchorRot[_anchors] = worldRot;
            return _anchors++;
        }
    }

    /// <summary>피어 p 가 자기 로컬 좌표계에서 앵커 a 를 관측.</summary>
    public void ObserveAnchor(int p, int a, Vector3 localPos)
    {
        lock (_gate) { _obs[p * MaxAnchors + a] = localPos; _obsValid[p * MaxAnchors + a] = true; }
    }

    /// <summary>
    /// 중력 정렬 Procrustes: world = R_y(θ)·local + t. θ = atan2(Σ(l×w)_y, Σ l·w)(xz 중심화) · t = c_w − R c_l. 반환 = 성공(관측 ≥ 2).
    /// </summary>
    public bool SolvePeer(int p)
    {
        lock (_gate)
        {
            if (p == 0) { _peerSolved[0] = true; return true; }
            int n = 0; Vector3 cl = default, cw = default;
            for (var a = 0; a < _anchors; a++)
                if (_obsValid[p * MaxAnchors + a]) { cl += _obs[p * MaxAnchors + a]; cw += _anchorPos[a]; n++; }
            if (n < 2) return false;
            cl /= n; cw /= n;
            float sDot = 0, sCross = 0;
            for (var a = 0; a < _anchors; a++)
            {
                if (!_obsValid[p * MaxAnchors + a]) continue;
                var l = _obs[p * MaxAnchors + a] - cl; var w = _anchorPos[a] - cw;
                sDot += l.X * w.X + l.Z * w.Z;
                sCross += l.Z * w.X - l.X * w.Z; // R_y(θ): x' = x cos + z sin · z' = −x sin + z cos
            }
            var th = MathF.Atan2(sCross, sDot);
            _peerYaw[p] = th;
            _peerT[p] = cw - RotY(cl, th);
            double se = 0;
            for (var a = 0; a < _anchors; a++)
            {
                if (!_obsValid[p * MaxAnchors + a]) continue;
                se += Vector3.DistanceSquared(LocalToWorldUnsafe(p, _obs[p * MaxAnchors + a]), _anchorPos[a]);
            }
            _peerResidualMm[p] = (float)Math.Sqrt(se / n) * 1000f;
            _peerSolved[p] = true;
            float sum = 0; var m = 0;
            for (var q = 1; q < _peers; q++) if (_peerSolved[q]) { sum += _peerResidualMm[q]; m++; }
            MeanResidualMm = m > 0 ? sum / m : 0;
            return true;
        }
    }

    private static Vector3 RotY(Vector3 v, float th)
    {
        float c = MathF.Cos(th), s = MathF.Sin(th);
        return new Vector3(v.X * c + v.Z * s, v.Y, -v.X * s + v.Z * c);
    }

    private Vector3 LocalToWorldUnsafe(int p, Vector3 l) => RotY(l, _peerYaw[p]) + _peerT[p];

    public Vector3 LocalToWorld(int p, Vector3 l) { lock (_gate) return LocalToWorldUnsafe(p, l); }
    public Vector3 WorldToLocal(int p, Vector3 w) { lock (_gate) return RotY(w - _peerT[p], -_peerYaw[p]); }

    /// <summary>엔티티(홀로그램) 등록: 소유 피어 · 기준 앵커 · 앵커 기준 오프셋.</summary>
    public int AddEntity(int owner, int anchor, Vector3 offset)
    {
        lock (_gate)
        {
            if (_entities >= MaxEntities) return -1;
            var e = _entities++;
            _entityAnchor[e] = anchor; _entityOffset[e] = offset;
            ref var s = ref _replica[owner * MaxEntities + e];
            s.Owner = owner; s.Version = 1; s.Rotation = Quaternion.Identity; s.StampMs = _nowMs;
            s.Position = _anchorPos[anchor] + Vector3.Transform(offset, _anchorRot[anchor]);
            for (var p = 0; p < _peers; p++) _replica[p * MaxEntities + e].Owner = owner;
            return e;
        }
    }

    /// <summary>소유자가 자기 엔티티 포즈 갱신(앵커 기준 로컬 이동 → 월드 · 버전 증가 · 속도 추정).</summary>
    public void UpdateOwned(int e, Vector3 anchorLocal, Quaternion rot)
    {
        lock (_gate)
        {
            var a = _entityAnchor[e];
            ref var s = ref _replica[OwnerOf(e) * MaxEntities + e];
            var wp = _anchorPos[a] + Vector3.Transform(_entityOffset[e] + anchorLocal, _anchorRot[a]);
            var dt = MathF.Max(1f, _nowMs - s.StampMs) * 0.001f;
            s.Velocity = Vector3.Lerp(s.Velocity, (wp - s.Position) / dt, 0.5f);
            s.Position = wp; s.Rotation = rot; s.Version++; s.StampMs = _nowMs;
        }
    }

    public int OwnerOf(int e) => _replica[e].Owner;

    /// <summary>
    /// 시뮬레이션 틱(dtMs): 소유 엔티티 상태를 모든 피어로 송신(직결 단절 시 최소 지연 릴레이) → 도착 메시지 적용(LWW) → 지연 기반 최대 노후도 계산. 무할당.
    /// </summary>
    public void Tick(float dtMs)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Holo.Router");
            _nowMs += dtMs;
            for (var e = 0; e < _entities; e++)
            {
                var owner = OwnerOf(e);
                var st = _replica[owner * MaxEntities + e];
                for (var to = 0; to < _peers; to++)
                {
                    if (to == owner) continue;
                    Route(owner, to, e, in st);
                }
            }
            // 배달
            var w = 0;
            for (var i = 0; i < _qCount; i++)
            {
                ref var m = ref _q[i];
                if (m.DeliverMs > _nowMs) { _q[w++] = m; continue; }
                if (m.Via >= 0)
                {
                    // 릴레이 노드 도착 → 최종 목적지로 재송신
                    var hop = m; hop.From = m.Via; hop.Via = -1;
                    var k = hop.From * MaxPeers + hop.To;
                    if (!_linkUp[k] || NextFloat() < _loss[k]) { Lost++; continue; }
                    hop.DeliverMs = _nowMs + _latency[k];
                    _q[w++] = hop; Relayed++;
                    continue;
                }
                ref var dst = ref _replica[m.To * MaxEntities + m.Entity];
                if (m.State.Version > dst.Version) dst = m.State;
                Delivered++;
            }
            _qCount = w;

            float stale = 0, perr = 0; var pn = 0;
            for (var e = 0; e < _entities; e++)
            {
                var owner = OwnerOf(e);
                ref readonly var truth = ref _replica[owner * MaxEntities + e];
                for (var p = 0; p < _peers; p++)
                {
                    if (p == owner) continue;
                    ref readonly var r = ref _replica[p * MaxEntities + e];
                    stale = MathF.Max(stale, _nowMs - r.StampMs);
                    perr += Vector3.Distance(PredictUnsafe(p, e), truth.Position); pn++;
                }
            }
            MaxStalenessMs = stale;
            PredictionErrorMm = pn > 0 ? perr / pn * 1000f : 0;
        }
    }

    private void Route(int from, int to, int e, in HoloEntityState st)
    {
        var k = from * MaxPeers + to;
        var via = -1;
        var lat = _latency[k];
        if (!_linkUp[k])
        {
            // 1-홉 릴레이 탐색(최소 총 지연)
            var best = float.MaxValue;
            for (var r = 0; r < _peers; r++)
            {
                if (r == from || r == to || !_linkUp[from * MaxPeers + r] || !_linkUp[r * MaxPeers + to]) continue;
                var l = _latency[from * MaxPeers + r] + _latency[r * MaxPeers + to];
                if (l < best) { best = l; via = r; }
            }
            if (via < 0) { Unroutable++; return; }
            k = from * MaxPeers + via;
            lat = _latency[k];
        }
        Sent++;
        if (NextFloat() < _loss[k]) { Lost++; return; }
        if (_qCount >= QueueCap) { QueueOverflows++; return; }
        _q[_qCount++] = new Msg { From = from, To = to, Via = via, Entity = e, DeliverMs = _nowMs + lat, State = st };
    }

    /// <summary>피어 p 가 보는 엔티티 e 월드 위치(데드 레커닝 외삽 · 최대 250 ms).</summary>
    public Vector3 Predict(int p, int e) { lock (_gate) return PredictUnsafe(p, e); }

    private Vector3 PredictUnsafe(int p, int e)
    {
        ref readonly var r = ref _replica[p * MaxEntities + e];
        if (!DeadReckoning || p == r.Owner) return r.Position;
        return r.Position + r.Velocity * (MathF.Min(250f, _nowMs - r.StampMs) * 0.001f);
    }

    /// <summary>피어 p 의 로컬 좌표계 렌더 위치(앵커 해석 → 역변환).</summary>
    public Vector3 RenderPosition(int p, int e) { lock (_gate) return RotY(PredictUnsafe(p, e) - _peerT[p], -_peerYaw[p]); }

    /// <summary>모든 피어가 엔티티 최신 버전을 보유하는지.</summary>
    public bool Converged()
    {
        lock (_gate)
        {
            for (var e = 0; e < _entities; e++)
            {
                var v = _replica[OwnerOf(e) * MaxEntities + e].Version;
                for (var p = 0; p < _peers; p++) if (_replica[p * MaxEntities + e].Version != v) return false;
            }
            return true;
        }
    }

    public void Describe(string action) => LastAction = action;

    private float NextFloat()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return (_rng >> 40) / (float)(1 << 24);
    }
}
