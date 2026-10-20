namespace PromptFlowStudio.Services.ViralLoop;

public enum CurationCategory : byte { Entertainment, Tech, Life, Food, Fitness }
public enum CurationSource : byte { TrendFeed, GraphApi }
public enum CurationState : byte { New, Queued, InPipeline, Published, Skipped }

/// <summary>큐레이션 링 슬롯(값 형식) — 퍼머링크는 엔진 소유 고정 바이트 영역.</summary>
public struct CurationItemSpan
{
    public ulong Id;
    public CurationSource Source;
    public CurationCategory Category;
    public CurationState State;
    public long Views, Shares, Saves, Likes, Comments;
    public float Velocity, Score;
    public long FirstSeenTicks, UpdatedTicks;
    public int LinkLen;
}

/// <summary>
/// [PromptFlow Studio v17.50.0] 🚀 실시간 바이럴 릴스 큐레이션 &amp; 원클릭 인제스트 엔진(<see cref="Refresh"/> · 랭킹 · <see cref="TryClaim"/> 무할당).
/// 소스 ① v17.47 트렌드 피드 상위(조회 · 저장 · 공유 · 확산 속도 · 분류) ② v17.48 Graph API 인제스천 링(해시태그 top_media · 본인 미디어 —
/// 공유수 비공개이므로 좋아요/댓글 가중). 96 슬롯 링에 (소스, ID) 중복 없이 갱신 적재하고 카테고리별 상위 10 · 전체 상위 16 인덱스를
/// 점수(조회 + 6·공유 + 3·저장 + 10·좋아요 + 30·댓글) × 신선도로 삽입 정렬한다. Graph 미디어 카테고리는 활성 해시태그 키워드로 분류.
/// 원본 영상(CDN media_url)은 내려받지 않는다 — 분해 단계는 v17.47 시뮬레이션 디코더(시드 = 미디어 ID)를 사용한다.
/// </summary>
public sealed class RealtimeViralTrendCurationEngine
{
    public const int Cap = 96, PerCategory = 10, TopAll = 16, LinkCap = 160, Categories = 5;
    public static readonly string[] CategoryNames = { "엔터", "테크", "라이프", "푸드", "피트니스" };
    private readonly CurationItemSpan[] _items = new CurationItemSpan[Cap];
    private readonly byte[] _links = new byte[Cap * LinkCap];
    private readonly int[] _rankCat = new int[Categories * PerCategory], _rankCatN = new int[Categories], _rankAll = new int[TopAll];
    private int _count, _next, _rankAllN;

    public int Count => _count;
    public long Refreshes { get; private set; }
    public long Inserted { get; private set; }
    public long Updated { get; private set; }
    public double LastRefreshUs { get; private set; }
    public ref readonly CurationItemSpan Item(int slot) => ref _items[slot];
    public ReadOnlySpan<byte> Link(int slot) => _links.AsSpan(slot * LinkCap, _items[slot].LinkLen);
    public int RankCount(CurationCategory c) => _rankCatN[(int)c];
    /// <summary>카테고리 c 의 i 위 슬롯.</summary>
    public int Ranked(CurationCategory c, int i) => _rankCat[(int)c * PerCategory + i];
    public int TopCount => _rankAllN;
    public int Top(int i) => _rankAll[i];
    public static string CategoryName(CurationCategory c) => CategoryNames[(int)c];

    /// <summary>v17.47 피드 분류(댄스 · 푸드 · 여행 · 피트니스 · 코미디) → 큐레이션 카테고리.</summary>
    public static CurationCategory FromFeed(int feedCategory) => feedCategory switch { 0 or 4 => CurationCategory.Entertainment, 1 => CurationCategory.Food, 2 => CurationCategory.Life, 3 => CurationCategory.Fitness, _ => CurationCategory.Life };

    /// <summary>해시태그 키워드 → 카테고리(무할당 · 소문자 비교).</summary>
    public static CurationCategory FromHashtag(ReadOnlySpan<byte> tag)
    {
        if (Has(tag, "tech"u8) || Has(tag, "ai"u8) || Has(tag, "gadget"u8) || Has(tag, "code"u8)) return CurationCategory.Tech;
        if (Has(tag, "food"u8) || Has(tag, "recipe"u8) || Has(tag, "cook"u8)) return CurationCategory.Food;
        if (Has(tag, "fit"u8) || Has(tag, "gym"u8) || Has(tag, "workout"u8)) return CurationCategory.Fitness;
        if (Has(tag, "dance"u8) || Has(tag, "funny"u8) || Has(tag, "comedy"u8) || Has(tag, "music"u8) || Has(tag, "kpop"u8)) return CurationCategory.Entertainment;
        return CurationCategory.Life;
    }

    private static bool Has(ReadOnlySpan<byte> s, ReadOnlySpan<byte> k)
    {
        for (var i = 0; i + k.Length <= s.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < k.Length && ok; j++) ok = (s[i + j] | 0x20) == k[j];
            if (ok) return true;
        }
        return false;
    }

    /// <summary>두 소스를 링에 병합 · 재랭킹(무할당).</summary>
    public void Refresh(ViralReels.RealtimeViralReelsIngestionEngine? feed, GraphBridge.RealtimeMetaGraphApiIngestionEngine? graph, long nowTicks)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (feed is not null)
            for (var i = 0; i < feed.TopCount; i++)
            {
                ref readonly var m = ref feed.Top(i);
                ref var it = ref Upsert(CurationSource.TrendFeed, (ulong)m.Id, nowTicks, out _);
                it.Category = FromFeed(m.Category); it.Views = m.Views; it.Shares = m.Shares; it.Saves = m.Saves; it.Likes = 0; it.Comments = 0; it.Velocity = m.Velocity;
            }
        if (graph is not null)
        {
            var cat = FromHashtag(graph.Hashtag);
            for (var i = 0; i < graph.Count; i++)
            {
                ref readonly var m = ref graph.Media(i);
                if (!m.IsReel || m.Own) continue;
                ref var it = ref Upsert(CurationSource.GraphApi, m.Id, nowTicks, out var slot);
                it.Category = cat; it.Views = m.Views; it.Likes = m.Likes; it.Comments = m.Comments; it.Shares = 0; it.Saves = 0;
                var ageH = m.TimestampUnix > 0 ? Math.Max((nowTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond - m.TimestampUnix, 3600) / 3600f : 24f;
                it.Velocity = (m.Views + 10f * m.Likes) / ageH;
                var link = graph.Permalink(i);
                var n = Math.Min(link.Length, LinkCap);
                link[..n].CopyTo(_links.AsSpan(slot * LinkCap)); it.LinkLen = n;
            }
        }
        Rank(nowTicks);
        Refreshes++;
        LastRefreshUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private ref CurationItemSpan Upsert(CurationSource src, ulong id, long now, out int slot)
    {
        for (var i = 0; i < _count; i++)
            if (_items[i].Id == id && _items[i].Source == src) { slot = i; Updated++; _items[i].UpdatedTicks = now; return ref _items[i]; }
        slot = _count < Cap ? _count++ : Evict();
        ref var it = ref _items[slot];
        it = default;
        it.Id = id; it.Source = src; it.FirstSeenTicks = now; it.UpdatedTicks = now;
        Inserted++;
        return ref it;
    }

    /// <summary>교체 대상 — 처리 완료/건너뜀 우선, 없으면 최저 점수 신규(파이프라인 진행 중은 보호).</summary>
    private int Evict()
    {
        var best = -1; var bs = float.MaxValue;
        for (var k = 0; k < Cap; k++)
        {
            var i = (_next + k) % Cap;
            ref readonly var it = ref _items[i];
            if (it.State is CurationState.Queued or CurationState.InPipeline) continue;
            var s = it.State is CurationState.Published or CurationState.Skipped ? -1f : it.Score;
            if (s < bs) { bs = s; best = i; }
        }
        _next = (best + 1) % Cap;
        return best < 0 ? _next : best;
    }

    public static float Score(in CurationItemSpan it, long now)
    {
        var raw = it.Views + 6f * it.Shares + 3f * it.Saves + 10f * it.Likes + 30f * it.Comments;
        var ageH = (now - it.FirstSeenTicks) / (float)TimeSpan.TicksPerHour;
        return raw * (1f + it.Velocity / 1e5f) / (1f + 0.02f * ageH);
    }

    private void Rank(long now)
    {
        Array.Clear(_rankCatN);
        _rankAllN = 0;
        for (var i = 0; i < _count; i++)
        {
            ref var it = ref _items[i];
            it.Score = Score(in it, now);
            if (it.State == CurationState.Skipped) continue;
            var c = (int)it.Category;
            Insert(_rankCat.AsSpan(c * PerCategory, PerCategory), ref _rankCatN[c], i);
            Insert(_rankAll, ref _rankAllN, i);
        }
    }

    private void Insert(Span<int> list, ref int n, int slot)
    {
        var s = _items[slot].Score;
        var pos = n;
        while (pos > 0 && _items[list[pos - 1]].Score < s) pos--;
        if (pos >= list.Length) return;
        for (var k = Math.Min(n, list.Length - 1); k > pos; k--) list[k] = list[k - 1];
        list[pos] = slot;
        if (n < list.Length) n++;
    }

    /// <summary>자율 트리거용 — 미사용 최고 점수 슬롯(선호 카테고리 우선 · 없으면 전체). 없으면 −1.</summary>
    public int BestUnclaimed(int preferredCategory)
    {
        if (preferredCategory >= 0)
            for (var i = 0; i < _rankCatN[preferredCategory]; i++) { var s = _rankCat[preferredCategory * PerCategory + i]; if (_items[s].State == CurationState.New) return s; }
        for (var i = 0; i < _rankAllN; i++) if (_items[_rankAll[i]].State == CurationState.New) return _rankAll[i];
        return -1;
    }

    /// <summary>원클릭/자율 인제스트 — New 슬롯을 Queued 로 점유.</summary>
    public bool TryClaim(int slot)
    {
        if ((uint)slot >= (uint)_count || _items[slot].State != CurationState.New) return false;
        _items[slot].State = CurationState.Queued;
        return true;
    }

    public void SetState(int slot, CurationState s) { if ((uint)slot < (uint)_count) _items[slot].State = s; }
}
