namespace PromptFlowStudio.Services.AgentFederation;

public enum KgNodeKind : byte { World4D, Shot, Character, Asset, SreNode, Agent }

public enum KgRelation : byte { Contains, AppearsIn, Uses, HostedOn, Owns, Relates, Near, DependsOn, AtRisk }

/// <summary>엣지 스냅샷 — 유효 구간 [From, To) · 추론 여부.</summary>
public readonly record struct KgEdge(int Id, int Src, int Dst, KgRelation Relation, float Weight, double ValidFromMs, double ValidToMs, bool Inferred);

/// <summary>
/// [PromptFlow Studio v16.68.0] 🧠 실시간 시공간 신경망 지식 그래프 — 인메모리 고정 용량(노드 1024 · 엣지 8192).
/// 저장: 노드별 출/입 연결 리스트(인덱스 체인 · 엣지 해제 시 프리 리스트 재사용) · (src, rel, dst) 개방 주소 해시 → O(1) 업서트.
/// 시공간: 노드 3D 위치 · 시간 구간 · 엣지 유효 구간(시점 질의) · 위치 갱신 시 반경 내 Near 엣지 자동 생성/만료.
/// 추론: 전이 규칙(Character AppearsIn Shot ∧ Shot Uses Asset ⇒ Character DependsOn Asset) · SRE 장애 영향 역전파(HostedOn/Uses/Contains → AtRisk) ·
/// 16차원 노드 임베딩 메시지 패싱(이웃 가중 평균 + 고정 사영 · tanh) → 코사인 유사도. 업서트/질의/BFS/전파 무할당(인스턴스 스크래치).
/// </summary>
public sealed class RealtimeSpatiotemporalKnowledgeGraph
{
    public const int MaxNodes = 1024, MaxEdges = 8192, Dim = 16, HashSize = 16384;
    public const float NearRadius = 3f;

    private readonly object _gate = new();
    // 노드
    private readonly string[] _name = new string[MaxNodes];
    private readonly KgNodeKind[] _kind = new KgNodeKind[MaxNodes];
    private readonly float[] _pos = new float[MaxNodes * 3];
    private readonly double[] _t0 = new double[MaxNodes], _t1 = new double[MaxNodes];
    private readonly bool[] _down = new bool[MaxNodes];
    private readonly int[] _outHead = new int[MaxNodes], _inHead = new int[MaxNodes];
    private readonly float[] _emb = new float[MaxNodes * Dim], _embNext = new float[MaxNodes * Dim];
    // 엣지
    private readonly int[] _src = new int[MaxEdges], _dst = new int[MaxEdges], _outNext = new int[MaxEdges], _inNext = new int[MaxEdges];
    private readonly KgRelation[] _rel = new KgRelation[MaxEdges];
    private readonly float[] _w = new float[MaxEdges];
    private readonly double[] _from = new double[MaxEdges], _to = new double[MaxEdges];
    private readonly bool[] _inferred = new bool[MaxEdges], _live = new bool[MaxEdges];
    private readonly int[] _hash = new int[HashSize];
    private readonly float[] _proj = new float[Dim * Dim];
    // 스크래치
    private readonly int[] _queue = new int[MaxNodes], _prev = new int[MaxNodes];
    private readonly bool[] _seen = new bool[MaxNodes];
    private int _nodes, _edgeTop, _freeHead = -1, _liveEdges, _inferredCount, _tombs;
    private long _version, _upserts, _expired, _propagations, _inferences;
    private double _clock;

    public RealtimeSpatiotemporalKnowledgeGraph()
    {
        Array.Fill(_hash, -1); Array.Fill(_outHead, -1); Array.Fill(_inHead, -1);
        uint s = 0x2545F491;
        for (var i = 0; i < _proj.Length; i++) { s ^= s << 13; s ^= s >> 17; s ^= s << 5; _proj[i] = ((s & 0xFFFF) / 65535f - 0.5f) * 0.6f + (i % (Dim + 1) == 0 ? 0.7f : 0f); }
    }

    public event Action? OnChange;

    public int NodeCount { get { lock (_gate) return _nodes; } }
    public int EdgeCount { get { lock (_gate) return _liveEdges; } }
    public int InferredEdges { get { lock (_gate) return _inferredCount; } }
    public long Version { get { lock (_gate) return _version; } }
    public long Upserts { get { lock (_gate) return _upserts; } }
    public long Expired { get { lock (_gate) return _expired; } }
    public long Propagations { get { lock (_gate) return _propagations; } }
    public long Inferences { get { lock (_gate) return _inferences; } }
    public double ClockMs { get { lock (_gate) return _clock; } }
    public string? LastAction { get; private set; }

    public string NameOf(int n) { lock (_gate) return _name[n]; }
    public KgNodeKind KindOf(int n) { lock (_gate) return _kind[n]; }
    public bool IsDown(int n) { lock (_gate) return _down[n]; }
    public (float X, float Y, float Z) PositionOf(int n) { lock (_gate) return (_pos[n * 3], _pos[n * 3 + 1], _pos[n * 3 + 2]); }

    public int AddNode(string name, KgNodeKind kind, float x = 0, float y = 0, float z = 0, double t0 = 0, double t1 = double.PositiveInfinity)
    {
        lock (_gate)
        {
            if (_nodes >= MaxNodes) throw new InvalidOperationException("지식 그래프 노드 상한");
            var n = _nodes++;
            _name[n] = name; _kind[n] = kind; _t0[n] = t0; _t1[n] = t1;
            _pos[n * 3] = x; _pos[n * 3 + 1] = y; _pos[n * 3 + 2] = z;
            // 종류 원-핫 + 이름 해시 초기 임베딩
            var h = (uint)name.GetHashCode(StringComparison.Ordinal);
            for (var d = 0; d < Dim; d++) { h ^= h << 13; h ^= h >> 17; h ^= h << 5; _emb[n * Dim + d] = ((h & 0xFF) / 255f - 0.5f) * 0.2f; }
            _emb[n * Dim + (int)kind] += 1f;
            _version++;
            return n;
        }
    }

    public void Advance(double dtMs)
    {
        lock (_gate) { _clock += Math.Max(0, dtMs); ExpireLocked(); }
    }

    /// <summary>개방 주소 탐사 — 일치 슬롯 · 없으면 첫 톰스톤(-2) 또는 빈 슬롯(-1).</summary>
    private int Slot(int src, KgRelation rel, int dst)
    {
        var h = (uint)(src * 73856093 ^ (int)rel * 19349663 ^ dst * 83492791) & (HashSize - 1);
        var tomb = -1;
        for (var probe = 0; probe < HashSize; probe++)
        {
            var e = _hash[h];
            if (e == -1) return tomb >= 0 ? tomb : (int)h;
            if (e == -2) { if (tomb < 0) tomb = (int)h; }
            else if (_src[e] == src && _dst[e] == dst && _rel[e] == rel) return (int)h;
            h = (h + 1) & (HashSize - 1);
        }
        return tomb;
    }

    /// <summary>톰스톤 누적 시 라이브 엣지로 해시 재구축(무할당).</summary>
    private void RehashLocked()
    {
        Array.Fill(_hash, -1);
        for (var e = 0; e < _edgeTop; e++) if (_live[e]) _hash[Slot(_src[e], _rel[e], _dst[e])] = e;
        _tombs = 0;
    }

    /// <summary>(src, rel, dst) 업서트 — 존재 시 가중/유효 구간 갱신(무할당). 엣지 id 반환(용량 초과 −1).</summary>
    public int Upsert(int src, KgRelation rel, int dst, float weight = 1f, double ttlMs = double.PositiveInfinity)
    {
        lock (_gate) return UpsertLocked(src, rel, dst, weight, ttlMs, inferred: false);
    }

    private int UpsertLocked(int src, KgRelation rel, int dst, float weight, double ttlMs, bool inferred)
    {
        if ((uint)src >= (uint)_nodes || (uint)dst >= (uint)_nodes) return -1;
        var slot = Slot(src, rel, dst);
        var e = _hash[slot];
        _upserts++;
        if (e >= 0)
        {
            _w[e] = weight; _to[e] = _clock + ttlMs;
            if (!inferred && _inferred[e]) { _inferred[e] = false; _inferredCount--; }
            _version++;
            return e;
        }
        if (_liveEdges >= MaxEdges - 1) return -1;
        if (_freeHead >= 0) { e = _freeHead; _freeHead = _outNext[e]; }
        else e = _edgeTop++;
        _src[e] = src; _dst[e] = dst; _rel[e] = rel; _w[e] = weight; _from[e] = _clock; _to[e] = _clock + ttlMs;
        _inferred[e] = inferred; _live[e] = true;
        _outNext[e] = _outHead[src]; _outHead[src] = e;
        _inNext[e] = _inHead[dst]; _inHead[dst] = e;
        if (_hash[slot] == -2) _tombs--;
        _hash[slot] = e;
        _liveEdges++;
        if (inferred) _inferredCount++;
        _version++;
        return e;
    }

    public bool Remove(int src, KgRelation rel, int dst)
    {
        lock (_gate)
        {
            if ((uint)src >= (uint)_nodes || (uint)dst >= (uint)_nodes) return false;
            var slot = Slot(src, rel, dst);
            var e = _hash[slot];
            if (e < 0) return false;
            RemoveLocked(e);
            return true;
        }
    }

    private void RemoveLocked(int e)
    {
        // 연결 리스트 해제
        ref var p = ref _outHead[_src[e]];
        while (p != e) p = ref _outNext[p];
        p = _outNext[e];
        ref var q = ref _inHead[_dst[e]];
        while (q != e) q = ref _inNext[q];
        q = _inNext[e];
        // 해시: 삭제 표식(-2 = 톰스톤) → 탐사 체인 유지
        _hash[Slot(_src[e], _rel[e], _dst[e])] = -2;
        if (++_tombs > HashSize / 4) { _live[e] = false; RehashLocked(); }
        if (_inferred[e]) _inferredCount--;
        _live[e] = false; _inferred[e] = false;
        _outNext[e] = _freeHead; _freeHead = e;
        _liveEdges--;
        _version++;
    }

    private void ExpireLocked()
    {
        for (var e = 0; e < _edgeTop; e++)
            if (_live[e] && _to[e] <= _clock) { RemoveLocked(e); _expired++; }
    }

    public bool Has(int src, KgRelation rel, int dst, double atMs = double.NaN)
    {
        lock (_gate)
        {
            if ((uint)src >= (uint)_nodes || (uint)dst >= (uint)_nodes) return false;
            var e = _hash[Slot(src, rel, dst)];
            if (e < 0) return false;
            var t = double.IsNaN(atMs) ? _clock : atMs;
            return t >= _from[e] && t < _to[e];
        }
    }

    public bool IsInferred(int src, KgRelation rel, int dst)
    {
        lock (_gate) { var e = _hash[Slot(src, rel, dst)]; return e >= 0 && _inferred[e]; }
    }

    /// <summary>시점 t 유효 이웃(출방향 · 관계 필터) 복사(무할당).</summary>
    public int Neighbors(int n, KgRelation? rel, Span<int> dst, double atMs = double.NaN)
    {
        lock (_gate)
        {
            var t = double.IsNaN(atMs) ? _clock : atMs;
            var c = 0;
            for (var e = _outHead[n]; e >= 0 && c < dst.Length; e = _outNext[e])
                if ((rel is null || _rel[e] == rel.Value) && t >= _from[e] && t < _to[e]) dst[c++] = _dst[e];
            return c;
        }
    }

    public int CopyEdges(Span<KgEdge> dst)
    {
        lock (_gate)
        {
            var c = 0;
            for (var e = 0; e < _edgeTop && c < dst.Length; e++)
                if (_live[e]) dst[c++] = new KgEdge(e, _src[e], _dst[e], _rel[e], _w[e], _from[e], _to[e], _inferred[e]);
            return c;
        }
    }

    /// <summary>위치 갱신 → 반경 내 Near 엣지(양방향 · TTL 2s) 자동 생성.</summary>
    public int MoveNode(int n, float x, float y, float z)
    {
        lock (_gate)
        {
            if ((uint)n >= (uint)_nodes) return 0;
            _pos[n * 3] = x; _pos[n * 3 + 1] = y; _pos[n * 3 + 2] = z;
            var near = 0;
            for (var m = 0; m < _nodes; m++)
            {
                if (m == n) continue;
                float dx = _pos[m * 3] - x, dy = _pos[m * 3 + 1] - y, dz = _pos[m * 3 + 2] - z;
                var d2 = dx * dx + dy * dy + dz * dz;
                if (d2 > NearRadius * NearRadius) continue;
                var w = 1f - MathF.Sqrt(d2) / NearRadius;
                UpsertLocked(n, KgRelation.Near, m, w, 2000, false);
                UpsertLocked(m, KgRelation.Near, n, w, 2000, false);
                near++;
            }
            _version++;
            return near;
        }
    }

    /// <summary>BFS 최단 홉 경로(시점 유효 엣지 · 양방향 탐색) — 경로 길이(노드 수) · 무할당.</summary>
    public int ShortestPath(int from, int to, Span<int> path)
    {
        lock (_gate)
        {
            if ((uint)from >= (uint)_nodes || (uint)to >= (uint)_nodes) return 0;
            Array.Clear(_seen, 0, _nodes);
            int head = 0, tail = 0;
            _queue[tail++] = from; _seen[from] = true; _prev[from] = -1;
            while (head < tail)
            {
                var u = _queue[head++];
                if (u == to) break;
                for (var pass = 0; pass < 2; pass++)
                    for (var e = pass == 0 ? _outHead[u] : _inHead[u]; e >= 0; e = pass == 0 ? _outNext[e] : _inNext[e])
                    {
                        if (_clock < _from[e] || _clock >= _to[e]) continue;
                        var v = pass == 0 ? _dst[e] : _src[e];
                        if (_seen[v]) continue;
                        _seen[v] = true; _prev[v] = u; _queue[tail++] = v;
                    }
            }
            if (!_seen[to]) return 0;
            var len = 0;
            for (var v = to; v >= 0; v = _prev[v]) len++;
            if (len > path.Length) return -len;
            var i = len;
            for (var v = to; v >= 0; v = _prev[v]) path[--i] = v;
            return len;
        }
    }

    /// <summary>전이 추론 — Character AppearsIn Shot ∧ Shot Uses Asset ⇒ Character DependsOn Asset(추론 엣지). 신규 추론 수 반환.</summary>
    public int InferDependencies()
    {
        var added = 0;
        lock (_gate)
        {
            for (var e = 0; e < _edgeTop; e++)
            {
                if (!_live[e] || _rel[e] != KgRelation.AppearsIn || _clock >= _to[e]) continue;
                int ch = _src[e], shot = _dst[e];
                for (var f = _outHead[shot]; f >= 0; f = _outNext[f])
                {
                    if (_rel[f] != KgRelation.Uses || _clock >= _to[f]) continue;
                    var before = _liveEdges;
                    UpsertLocked(ch, KgRelation.DependsOn, _dst[f], _w[e] * _w[f], Math.Min(_to[e], _to[f]) - _clock, inferred: true);
                    if (_liveEdges > before) added++;
                }
            }
            _inferences += added;
        }
        if (added > 0) { LastAction = "🧩 전이 추론 +" + added + " DependsOn"; Raise(); }
        return added;
    }

    /// <summary>SRE 노드 장애 표시 → 역전파(HostedOn ← Asset ← Uses Shot ← Contains World) 영향 노드에 AtRisk 엣지. 영향 수 반환(무할당).</summary>
    public int MarkDown(int sreNode, bool down, Span<int> impacted)
    {
        var c = 0;
        lock (_gate)
        {
            if ((uint)sreNode >= (uint)_nodes) return 0;
            _down[sreNode] = down;
            // 기존 AtRisk 정리(해당 원인)
            for (var e = _inHead[sreNode]; e >= 0;)
            {
                var next = _inNext[e];
                if (_rel[e] == KgRelation.AtRisk) RemoveLocked(e);
                e = next;
            }
            if (!down) { _version++; return 0; }
            Array.Clear(_seen, 0, _nodes);
            int head = 0, tail = 0;
            _queue[tail++] = sreNode; _seen[sreNode] = true;
            while (head < tail)
            {
                var u = _queue[head++];
                for (var e = _inHead[u]; e >= 0; e = _inNext[e])
                {
                    var r = _rel[e];
                    if (r is not (KgRelation.HostedOn or KgRelation.Uses or KgRelation.Contains or KgRelation.DependsOn) || _clock >= _to[e]) continue;
                    var v = _src[e];
                    if (_seen[v]) continue;
                    _seen[v] = true; _queue[tail++] = v;
                    if (c < impacted.Length) impacted[c] = v;
                    c++;
                }
            }
            for (var i = 0; i < Math.Min(c, impacted.Length); i++) UpsertLocked(impacted[i], KgRelation.AtRisk, sreNode, 1f, double.PositiveInfinity, true);
        }
        Raise();
        return c;
    }

    /// <summary>메시지 패싱 1회 — h' = tanh(W · (0.5 h + 0.5 Σ w·h_nbr / Σ w)) · L2 정규화(무할당).</summary>
    public void PropagateEmbeddings()
    {
        lock (_gate)
        {
            Span<float> agg = stackalloc float[Dim];
            for (var n = 0; n < _nodes; n++)
            {
                agg.Clear();
                float tw = 0;
                for (var pass = 0; pass < 2; pass++)
                    for (var e = pass == 0 ? _outHead[n] : _inHead[n]; e >= 0; e = pass == 0 ? _outNext[e] : _inNext[e])
                    {
                        if (_clock >= _to[e]) continue;
                        var v = pass == 0 ? _dst[e] : _src[e];
                        var w = _w[e];
                        for (var d = 0; d < Dim; d++) agg[d] += w * _emb[v * Dim + d];
                        tw += w;
                    }
                var inv = tw > 0 ? 0.5f / tw : 0f;
                var self = tw > 0 ? 0.5f : 1f;
                float norm = 0;
                for (var o = 0; o < Dim; o++)
                {
                    float s = 0;
                    for (var d = 0; d < Dim; d++) s += _proj[o * Dim + d] * (self * _emb[n * Dim + d] + inv * agg[d]);
                    var t = MathF.Tanh(s);
                    _embNext[n * Dim + o] = t; norm += t * t;
                }
                norm = norm > 0 ? 1f / MathF.Sqrt(norm) : 0f;
                for (var o = 0; o < Dim; o++) _embNext[n * Dim + o] *= norm;
            }
            Array.Copy(_embNext, _emb, _nodes * Dim);
            _propagations++;
            _version++;
        }
    }

    public float Similarity(int a, int b)
    {
        lock (_gate)
        {
            float dot = 0, na = 0, nb = 0;
            for (var d = 0; d < Dim; d++) { var x = _emb[a * Dim + d]; var y = _emb[b * Dim + d]; dot += x * y; na += x * x; nb += y * y; }
            return na > 0 && nb > 0 ? dot / MathF.Sqrt(na * nb) : 0f;
        }
    }

    /// <summary>동일 종류 중 최근접 이웃(코사인) — 무할당.</summary>
    public int MostSimilar(int n, out float score)
    {
        score = -2;
        var best = -1;
        lock (_gate)
            for (var m = 0; m < _nodes; m++)
            {
                if (m == n || _kind[m] != _kind[n]) continue;
                float dot = 0, na = 0, nb = 0;
                for (var d = 0; d < Dim; d++) { var x = _emb[n * Dim + d]; var y = _emb[m * Dim + d]; dot += x * y; na += x * x; nb += y * y; }
                var s = na > 0 && nb > 0 ? dot / MathF.Sqrt(na * nb) : 0f;
                if (s > score) { score = s; best = m; }
            }
        return best;
    }

    /// <summary>그래프 다이제스트(노드/엣지 상태 FNV-1a 64) — 스웜 동기화 합의 대상.</summary>
    public ulong Digest()
    {
        lock (_gate)
        {
            var h = 14695981039346656037UL;
            void Mix(ulong v) { h ^= v; h *= 1099511628211UL; }
            Mix((ulong)_nodes);
            for (var e = 0; e < _edgeTop; e++)
                if (_live[e]) { Mix((ulong)_src[e] << 32 | (uint)_dst[e]); Mix((ulong)_rel[e] << 32 | BitConverter.SingleToUInt32Bits(_w[e])); }
            for (var n = 0; n < _nodes; n++) Mix(_down[n] ? 1UL : 0UL);
            return h;
        }
    }

    public static string KindLabel(KgNodeKind k) => k switch
    {
        KgNodeKind.World4D => "4D 월드", KgNodeKind.Shot => "샷", KgNodeKind.Character => "캐릭터",
        KgNodeKind.Asset => "자산", KgNodeKind.SreNode => "SRE 노드", _ => "에이전트",
    };

    public static string RelationLabel(KgRelation r) => r switch
    {
        KgRelation.Contains => "포함", KgRelation.AppearsIn => "등장", KgRelation.Uses => "사용", KgRelation.HostedOn => "호스팅",
        KgRelation.Owns => "소유", KgRelation.Relates => "관계", KgRelation.Near => "근접", KgRelation.DependsOn => "의존", _ => "위험",
    };

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Federation.Kg.OnChange", ex); }
    }
}
