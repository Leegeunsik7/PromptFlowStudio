using System.Globalization;

namespace PromptFlowStudio.Services.Empire;

public enum IpFormat : byte { Origin, ShortForm, LongForm, Podcast, LiveCommerce, VTuber }

public enum IpStatus : byte { Planned, Producing, Released, Retired }

/// <summary>IP 프랜차이즈 노드(계보 · 포맷 · 세대 · 생산 상태 · 성과 · 적합도).</summary>
public sealed class IpNode
{
    public int Id { get; init; }
    public int Parent { get; init; } = -1;
    public int Root { get; init; }
    public string Title { get; init; } = "";
    public string Logline { get; init; } = "";
    public IpFormat Format { get; init; }
    public int Generation { get; init; }
    public IpStatus Status { get; internal set; }
    public double CreatedMs { get; init; }
    public double ReadyAtMs { get; internal set; }
    public double ReleasedMs { get; internal set; }
    public long Views { get; internal set; }
    public double Engagement { get; internal set; }
    public long RevenueMicros { get; internal set; }
    public double Fitness { get; internal set; }
    public int Children { get; internal set; }
    public float X { get; internal set; }
}

/// <summary>
/// [PromptFlow Studio v16.50.0] 🧬 자가 진화형 IP 프랜차이즈 &amp; 미디어 제국 확장기.
///   ▸ 원천 IP(세계관 · 캐릭터) 시드 → 포맷 친화도 행렬(숏폼 · 장편 · 팟캐스트 · 라이브 커머스 · V-Tuber)로 스핀오프 기획 — 계보 내 포맷 중복 회피.
///   ▸ 24시간 자가 증식 오케스트레이션: 생산 슬롯 상한 · 포맷별 제작 기간 · 출시 이벤트(옴니채널 분배 연결) · 성과 피드백(조회 · 참여 · 수익) → 적합도(로그 스케일 · 연령 감쇠).
///   ▸ 진화: 적합도 상위 출시 노드 토너먼트 선택 · 세대 상한 · 노드 상한 · 저성과 리프 은퇴(최소 관찰 기간) · 트리 레이아웃(리프 순 x · 세대 y).
///   ▸ Tick: 상태 전이 · 적합도 갱신은 무할당(스핀오프 생성 시점만 할당).
/// </summary>
public sealed class SelfEvolvingMediaEmpireFranchiseEngine
{
    public const int NodeCap = 256, MaxGeneration = 5;

    private static readonly double[,] Affinity =
    {
        //            Origin  Short  Long  Pod   Comm  VTuber
        /* Origin */ { 0,     1.0,   0.9,  0.6,  0.4,  0.8 },
        /* Short  */ { 0,     0.2,   0.8,  0.4,  0.6,  0.9 },
        /* Long   */ { 0,     1.0,   0.3,  0.9,  0.5,  0.6 },
        /* Pod    */ { 0,     0.9,   0.4,  0.2,  0.3,  0.7 },
        /* Comm   */ { 0,     0.8,   0.2,  0.3,  0.2,  0.6 },
        /* VTuber */ { 0,     1.0,   0.5,  0.8,  0.9,  0.2 },
    };

    private readonly object _gate = new();
    private readonly List<IpNode> _nodes = new(NodeCap);
    private readonly Random _rng;
    private double _nextEvolve;
    private bool _running;
    private long _spinoffs, _released, _retired, _cycles;

    public SelfEvolvingMediaEmpireFranchiseEngine() : this(20260) { }

    public SelfEvolvingMediaEmpireFranchiseEngine(int seed) => _rng = new Random(seed);

    public event Action? OnChange;
    public event Action<IpNode>? OnReleased;

    public int ProductionSlots { get; set; } = 4;
    public double EvolveEveryMs { get; set; } = 4000;
    public double MinObserveMs { get; set; } = 20000;
    public double RetireFitness { get; set; } = 0.15;
    public double TimeScale { get; set; } = 1;
    public bool IsRunning => _running;
    public int Count { get { lock (_gate) return _nodes.Count; } }
    public IpNode Node(int id) { lock (_gate) return _nodes[id]; }
    public IReadOnlyList<IpNode> Nodes { get { lock (_gate) return _nodes.ToArray(); } }
    public long Spinoffs => Interlocked.Read(ref _spinoffs);
    public long ReleasedCount => Interlocked.Read(ref _released);
    public long RetiredCount => Interlocked.Read(ref _retired);
    public long Cycles => Interlocked.Read(ref _cycles);
    public string? LastAction { get; private set; }

    public int CountStatus(IpStatus s) { lock (_gate) { var n = 0; foreach (var x in _nodes) if (x.Status == s) n++; return n; } }

    public static double FormatAffinity(IpFormat from, IpFormat to) => Affinity[(int)from, (int)to];

    /// <summary>포맷별 제작 기간(ms, TimeScale 적용 전).</summary>
    public static double ProductionMs(IpFormat f) => f switch
    {
        IpFormat.ShortForm => 3000, IpFormat.LongForm => 12000, IpFormat.Podcast => 6000, IpFormat.LiveCommerce => 5000, IpFormat.VTuber => 8000, _ => 0,
    };

    // ══════════════════════ 시드 · 제어 ══════════════════════

    /// <summary>원천 IP 시드(즉시 출시 상태 — 진화의 루트). 반환 = 노드 ID.</summary>
    public int SeedIp(string title, string logline, double nowMs)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("IP 제목이 비어 있습니다.", nameof(title));
        IpNode n;
        lock (_gate)
        {
            if (_nodes.Count >= NodeCap) throw new InvalidOperationException("IP 노드 상한에 도달했습니다.");
            var id = _nodes.Count;
            n = new IpNode { Id = id, Root = id, Title = title.Trim(), Logline = logline.Trim(), Format = IpFormat.Origin, Status = IpStatus.Released, CreatedMs = nowMs, ReleasedMs = nowMs, Fitness = 0.5 };
            _nodes.Add(n);
        }
        LastAction = "🌱 원천 IP 시드 · " + n.Title;
        Raise();
        return n.Id;
    }

    public void Start(double nowMs)
    {
        if (Count == 0) throw new InvalidOperationException("원천 IP를 먼저 시드하세요.");
        _running = true; _nextEvolve = nowMs;
        LastAction = "♾ 24시간 자가 증식 가동";
        Raise();
    }

    public void Stop() { _running = false; LastAction = "⏸ 자가 증식 정지"; Raise(); }

    public void Reset() { lock (_gate) _nodes.Clear(); _running = false; Interlocked.Exchange(ref _spinoffs, 0); Interlocked.Exchange(ref _released, 0); Interlocked.Exchange(ref _retired, 0); Raise(); }

    // ══════════════════════ 성과 피드백 ══════════════════════

    /// <summary>성과 반영(조회 · 참여율 0~1 · 수익). 무할당.</summary>
    public void ReportPerformance(int id, long views, double engagement, long revenueMicros)
    {
        lock (_gate)
        {
            if ((uint)id >= (uint)_nodes.Count) return;
            var n = _nodes[id];
            n.Views += views; n.RevenueMicros += revenueMicros;
            n.Engagement = n.Engagement <= 0 ? engagement : n.Engagement * 0.8 + engagement * 0.2;
        }
    }

    /// <summary>적합도 = 0.5·log10(조회+1)/7 + 0.3·참여 + 0.2·log10($+1)/5, 출시 후 연령 감쇠(반감 10분). 0~1.</summary>
    public static double ComputeFitness(long views, double engagement, long revenueMicros, double ageMs)
    {
        var v = Math.Min(1, Math.Log10(views + 1.0) / 7);
        var r = Math.Min(1, Math.Log10(revenueMicros / 1e6 + 1) / 5);
        var f = 0.5 * v + 0.3 * Math.Clamp(engagement, 0, 1) + 0.2 * r;
        return f * (0.6 + 0.4 * Math.Pow(0.5, Math.Max(0, ageMs) / 600000));
    }

    // ══════════════════════ 진화 ══════════════════════

    /// <summary>주기 틱: 생산 완료 → 출시 · 적합도 갱신 · (주기) 은퇴/스핀오프. 반환 = 상태 변화 수.</summary>
    public int Tick(double nowMs)
    {
        if (!_running) return 0;
        var changed = 0;
        IpNode? released = null;
        lock (_gate)
        {
            for (var i = 0; i < _nodes.Count; i++)
            {
                var n = _nodes[i];
                if (n.Status == IpStatus.Planned && ActiveProductions() < ProductionSlots) { n.Status = IpStatus.Producing; n.ReadyAtMs = nowMs + ProductionMs(n.Format) / Math.Max(0.01, TimeScale); changed++; }
                else if (n.Status == IpStatus.Producing && nowMs >= n.ReadyAtMs && released is null) { n.Status = IpStatus.Released; n.ReleasedMs = nowMs; released = n; changed++; }
                if (n.Status == IpStatus.Released && n.Format != IpFormat.Origin) n.Fitness = ComputeFitness(n.Views, n.Engagement, n.RevenueMicros, nowMs - n.ReleasedMs);
            }
            // 원천 IP 적합도 = 자손 평균(최소 0.4)
            for (var i = 0; i < _nodes.Count; i++)
            {
                var n = _nodes[i];
                if (n.Format != IpFormat.Origin) continue;
                double sum = 0; var k = 0;
                foreach (var c in _nodes) if (c.Root == n.Id && c.Id != n.Id && c.Status == IpStatus.Released) { sum += c.Fitness; k++; }
                n.Fitness = Math.Max(0.4, k > 0 ? sum / k : 0.5);
            }
        }
        if (released is not null)
        {
            Interlocked.Increment(ref _released);
            LastAction = "🎬 출시 · " + released.Title;
            try { OnReleased?.Invoke(released); } catch (Exception ex) { PromptFlowStudio.Program.Log("Franchise.OnReleased", ex); }
        }
        if (nowMs >= _nextEvolve)
        {
            _nextEvolve = nowMs + EvolveEveryMs / Math.Max(0.01, TimeScale);
            changed += Evolve(nowMs);
        }
        if (changed > 0) Raise();
        return changed;
    }

    private int ActiveProductions() { var n = 0; foreach (var x in _nodes) if (x.Status == IpStatus.Producing) n++; return n; }

    /// <summary>진화 1사이클: 저성과 리프 은퇴 → 토너먼트 선택 → 최고 친화 미탐색 포맷 스핀오프 기획. 반환 = 변화 수.</summary>
    public int Evolve(double nowMs)
    {
        Interlocked.Increment(ref _cycles);
        var changed = 0;
        IpNode? spawn = null;
        lock (_gate)
        {
            foreach (var n in _nodes)
            {
                if (n.Status != IpStatus.Released || n.Format == IpFormat.Origin || n.Children > 0) continue;
                if (nowMs - n.ReleasedMs >= MinObserveMs / Math.Max(0.01, TimeScale) && n.Fitness < RetireFitness) { n.Status = IpStatus.Retired; Interlocked.Increment(ref _retired); changed++; }
            }
            var planned = 0;
            foreach (var n in _nodes) if (n.Status is IpStatus.Planned or IpStatus.Producing) planned++;
            if (planned < ProductionSlots * 2 && _nodes.Count < NodeCap)
            {
                var parent = Tournament(3);
                if (parent is not null && PickFormat(parent, out var fmt))
                {
                    var root = _nodes[parent.Root];
                    spawn = new IpNode
                    {
                        Id = _nodes.Count, Parent = parent.Id, Root = parent.Root, Format = fmt, Generation = parent.Generation + 1, Status = IpStatus.Planned, CreatedMs = nowMs,
                        Title = SpinoffTitle(root.Title, fmt, parent.Generation + 1), Logline = SpinoffLogline(root, parent, fmt),
                    };
                    _nodes.Add(spawn);
                    parent.Children++;
                    changed++;
                }
            }
        }
        if (spawn is not null) { Interlocked.Increment(ref _spinoffs); LastAction = "🧬 스핀오프 기획 · " + spawn.Title; }
        return changed;
    }

    /// <summary>토너먼트 선택(출시 · 세대 상한 미만 · 확장 여지 있는 노드 k개 중 최고 적합도).</summary>
    private IpNode? Tournament(int k)
    {
        IpNode? best = null;
        var eligible = 0;
        foreach (var n in _nodes) if (Eligible(n)) eligible++;
        if (eligible == 0) return null;
        for (var t = 0; t < k; t++)
        {
            var pick = _rng.Next(eligible);
            foreach (var n in _nodes)
            {
                if (!Eligible(n)) continue;
                if (pick-- == 0) { if (best is null || n.Fitness > best.Fitness) best = n; break; }
            }
        }
        return best;
    }

    private bool Eligible(IpNode n) => n.Status == IpStatus.Released && n.Generation < MaxGeneration && n.Children < 3 && PickFormat(n, out _);

    /// <summary>부모 기준 최고 친화 포맷 — 같은 부모의 자식 포맷 · 부모 자신의 포맷 제외.</summary>
    private bool PickFormat(IpNode parent, out IpFormat fmt)
    {
        fmt = IpFormat.Origin;
        var best = 0.0;
        for (var f = 1; f <= (int)IpFormat.VTuber; f++)
        {
            var cand = (IpFormat)f;
            if (cand == parent.Format) continue;
            var dup = false;
            foreach (var c in _nodes) if (c.Parent == parent.Id && c.Format == cand) { dup = true; break; }
            if (dup) continue;
            var a = Affinity[(int)parent.Format, f];
            if (a > best) { best = a; fmt = cand; }
        }
        return best > 0.25;
    }

    public static string FormatLabel(IpFormat f) => f switch
    {
        IpFormat.ShortForm => "숏폼", IpFormat.LongForm => "장편", IpFormat.Podcast => "팟캐스트", IpFormat.LiveCommerce => "라이브 커머스", IpFormat.VTuber => "V-Tuber", _ => "원천 IP",
    };

    public static string FormatIcon(IpFormat f) => f switch
    {
        IpFormat.ShortForm => "📱", IpFormat.LongForm => "🎬", IpFormat.Podcast => "🎙️", IpFormat.LiveCommerce => "🛍️", IpFormat.VTuber => "🧚", _ => "🌍",
    };

    private static string SpinoffTitle(string root, IpFormat f, int gen) => root + " " + f switch
    {
        IpFormat.ShortForm => "숏츠 시리즈",
        IpFormat.LongForm => "극장판",
        IpFormat.Podcast => "비하인드 토크",
        IpFormat.LiveCommerce => "굿즈 라이브",
        _ => "버추얼 라이브",
    } + (gen > 1 ? " G" + gen.ToString(CultureInfo.InvariantCulture) : "");

    private static string SpinoffLogline(IpNode root, IpNode parent, IpFormat f) => f switch
    {
        IpFormat.ShortForm => parent.Title + "의 명장면을 60초 훅으로 재편집한 세로형 연작",
        IpFormat.LongForm => root.Logline + " — 확장된 세계관을 다룬 장편",
        IpFormat.Podcast => parent.Title + " 제작진·캐릭터 페르소나가 나누는 토크쇼",
        IpFormat.LiveCommerce => parent.Title + " 공식 굿즈를 AI 쇼호스트가 판매하는 라이브",
        _ => parent.Title + " 캐릭터가 V-Tuber로 시청자와 실시간 소통",
    };

    /// <summary>트리 레이아웃: 리프 순서 x(0~1) · 부모 = 자식 평균. 결과는 노드 X에 기록.</summary>
    public void Layout()
    {
        lock (_gate)
        {
            var leaf = 0; var leaves = 0;
            foreach (var n in _nodes) if (n.Children == 0) leaves++;
            for (var i = 0; i < _nodes.Count; i++) if (_nodes[i].Parent < 0) Place(_nodes[i], ref leaf, Math.Max(1, leaves));
        }
    }

    private float Place(IpNode n, ref int leaf, int leaves)
    {
        if (n.Children == 0) { n.X = (leaf++ + 0.5f) / leaves; return n.X; }
        float sum = 0; var k = 0;
        for (var i = n.Id + 1; i < _nodes.Count; i++) if (_nodes[i].Parent == n.Id) { sum += Place(_nodes[i], ref leaf, leaves); k++; }
        n.X = k > 0 ? sum / k : (leaf++ + 0.5f) / leaves;
        return n.X;
    }

    private void Raise() { try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Franchise.OnChange", ex); } }
}
