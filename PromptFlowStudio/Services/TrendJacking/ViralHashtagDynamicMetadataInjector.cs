namespace PromptFlowStudio.Services.TrendJacking;

public enum TrendPlatform : byte { YouTubeShorts, InstagramReels, TikTok }

/// <summary>
/// [PromptFlow Studio v17.96.0] #️⃣ 바이럴 해시태그 &amp; 메타데이터 동적 인젝터.
///   ▸ 레이더 골든 토픽(가속도 랭킹 순) 중 <see cref="TrendSpikeCooldownAntiSpamGuard"/> 허용분만 채택(최대 <see cref="MaxTrends"/>).
///   ▸ YouTube Shorts: <see cref="YtTitleCap"/>(100)자 이내 훅 타이틀 + 설명 하단 상위 3 핵심 해시태그.
///   ▸ Instagram Reels: 본문 + 하단 5 큐레이션 태그(트렌드 2 + 틈새 2 + 메가 1 · 트렌드 부족분은 틈새로 보충).
///   ▸ TikTok: <see cref="TtCaptionCap"/>(150)자 캡션 · 트렌드 2 + 틈새 1 + 메가 1.
///   ▸ 전 패키지를 고정 char 버퍼에 직접 조합 — 해시태그 정규화(공백/기호 제거) · 대소문자 무시 중복 제거 · 조합 경로 힙 할당 0.
/// </summary>
public sealed class ViralHashtagDynamicMetadataInjector
{
    public const int YtTitleCap = 100, YtDescCap = 1000, IgCaptionCap = 2200, TtCaptionCap = 150, MaxTrends = 3, TagCap = 32, NicheCap = 8;
    public const string YtMega = "#Shorts", IgMega = "#reels", TtMega = "#fyp";

    private readonly char[] _ytTitle = new char[YtTitleCap];
    private readonly char[] _ytDesc = new char[YtDescCap];
    private readonly char[] _ig = new char[IgCaptionCap];
    private readonly char[] _tt = new char[TtCaptionCap];
    private readonly int[] _picked = new int[MaxTrends];
    private readonly int[] _nicheStart = new int[NicheCap];
    private readonly int[] _nicheLen = new int[NicheCap];
    private int _ytTitleLen, _ytDescLen, _igLen, _ttLen, _pickedCount, _nicheCount;
    private int _ytTags, _igTags, _ttTags, _igTrendTags, _igNicheTags, _igMegaTags;

    public ReadOnlySpan<char> YouTubeTitle => _ytTitle.AsSpan(0, _ytTitleLen);
    public ReadOnlySpan<char> YouTubeDescription => _ytDesc.AsSpan(0, _ytDescLen);
    public ReadOnlySpan<char> InstagramCaption => _ig.AsSpan(0, _igLen);
    public ReadOnlySpan<char> TikTokCaption => _tt.AsSpan(0, _ttLen);
    public int PickedCount => _pickedCount;
    public int Picked(int i) => _picked[i];
    public int YouTubeTagCount => _ytTags;
    public int InstagramTagCount => _igTags;
    public int InstagramTrendTags => _igTrendTags;
    public int InstagramNicheTags => _igNicheTags;
    public int InstagramMegaTags => _igMegaTags;
    public int TikTokTagCount => _ttTags;
    public long Composed { get; private set; }
    public long Rejected { get; private set; }

    public static string PlatformName(TrendPlatform p) => p switch
    {
        TrendPlatform.InstagramReels => "Instagram Reels",
        TrendPlatform.TikTok => "TikTok",
        _ => "YouTube Shorts",
    };

    public ReadOnlySpan<char> Primary(TrendPlatform p) => p switch
    {
        TrendPlatform.InstagramReels => InstagramCaption,
        TrendPlatform.TikTok => TikTokCaption,
        _ => YouTubeTitle,
    };

    /// <summary>
    /// 메타데이터 패키지 조합(쿨다운 기록 없음 — 미리보기). <paramref name="niche"/> = 쉼표 구분 틈새 태그.
    /// 반환 = 채택 트렌드 수. 핫패스 — 무할당.
    /// </summary>
    public int Compose(RealtimeSocialTrendSpikeRadarEngine radar, TrendSpikeCooldownAntiSpamGuard guard, double nowUnix, ReadOnlySpan<char> summary, ReadOnlySpan<char> niche)
    {
        _pickedCount = 0;
        for (var i = 0; i < radar.TopicCount && _pickedCount < MaxTrends; i++)
        {
            var t = radar.Ranked(i);
            if (!radar.IsGolden(t)) continue;
            if (guard.Evaluate(radar.Keyword(t), radar.Relevance(t), nowUnix) != TrendInjectDecision.Allowed) { Rejected++; continue; }
            _picked[_pickedCount++] = t;
        }
        SplitNiche(niche);
        BuildYouTube(radar, summary, niche);
        BuildInstagram(radar, summary, niche);
        BuildTikTok(radar, summary, niche);
        Composed++;
        return _pickedCount;
    }

    /// <summary>채택 트렌드 쿨다운 기록(원클릭 주입 확정).</summary>
    public void Commit(RealtimeSocialTrendSpikeRadarEngine radar, TrendSpikeCooldownAntiSpamGuard guard, double nowUnix)
    {
        for (var i = 0; i < _pickedCount; i++) guard.Record(radar.Keyword(_picked[i]), nowUnix);
    }

    public void Reset()
    {
        _ytTitleLen = _ytDescLen = _igLen = _ttLen = _pickedCount = _nicheCount = 0;
        _ytTags = _igTags = _ttTags = _igTrendTags = _igNicheTags = _igMegaTags = 0;
        Composed = Rejected = 0;
    }

    // ── YouTube: "🔥 {트렌드1} | {요약}" ≤ 100 · 설명 = 요약 + 3 태그 ──
    private void BuildYouTube(RealtimeSocialTrendSpikeRadarEngine radar, ReadOnlySpan<char> summary, ReadOnlySpan<char> niche)
    {
        var t = _ytTitle.AsSpan();
        var n = 0;
        if (_pickedCount > 0)
        {
            n = Put(t, n, "🔥 ");
            n = Put(t, n, radar.Keyword(_picked[0]));
            n = Put(t, n, " | ");
        }
        n = PutClipped(t, n, summary.Trim());
        _ytTitleLen = n;

        var d = _ytDesc.AsSpan();
        var m = PutClipped(d, 0, summary.Trim());
        m = Put(d, m, "\n\n");
        var tagStart = m;
        _ytTags = 0;
        for (var i = 0; i < _pickedCount && _ytTags < 3; i++) m = AddTag(d, m, tagStart, radar.Keyword(_picked[i]), ref _ytTags);
        for (var i = 0; i < _nicheCount && _ytTags < 3; i++) m = AddTag(d, m, tagStart, Niche(niche, i), ref _ytTags);
        if (_ytTags < 3) m = AddTag(d, m, tagStart, YtMega, ref _ytTags);
        _ytDescLen = m;
    }

    // ── Instagram: 훅 + 요약 + "." 구분 + 트렌드 2 · 틈새 2 · 메가 1 ──
    private void BuildInstagram(RealtimeSocialTrendSpikeRadarEngine radar, ReadOnlySpan<char> summary, ReadOnlySpan<char> niche)
    {
        var c = _ig.AsSpan();
        var n = 0;
        if (_pickedCount > 0)
        {
            n = Put(c, n, "지금 뜨는 ");
            n = Put(c, n, radar.Keyword(_picked[0]));
            n = Put(c, n, " 🔥\n");
        }
        n = PutClipped(c, n, summary.Trim());
        n = Put(c, n, "\n.\n");
        var tagStart = n;
        _igTags = 0;
        for (var i = 0; i < _pickedCount && _igTags < 2; i++) n = AddTag(c, n, tagStart, radar.Keyword(_picked[i]), ref _igTags);
        _igTrendTags = _igTags;
        for (var i = 0; i < _nicheCount && _igTags < 4; i++) n = AddTag(c, n, tagStart, Niche(niche, i), ref _igTags);
        _igNicheTags = _igTags - _igTrendTags;
        var before = _igTags;
        n = AddTag(c, n, tagStart, IgMega, ref _igTags);
        _igMegaTags = _igTags - before;
        _igLen = n;
    }

    // ── TikTok: ≤ 150 자 · 트렌드 2 + 틈새 1 + 메가 1 (태그 공간 우선 확보 후 요약 절단) ──
    private void BuildTikTok(RealtimeSocialTrendSpikeRadarEngine radar, ReadOnlySpan<char> summary, ReadOnlySpan<char> niche)
    {
        Span<char> tags = stackalloc char[TtCaptionCap];
        var tn = 0;
        _ttTags = 0;
        for (var i = 0; i < _pickedCount && _ttTags < 2; i++) tn = AddTag(tags, tn, 0, radar.Keyword(_picked[i]), ref _ttTags);
        var before = _ttTags;
        for (var i = 0; i < _nicheCount && _ttTags < before + 1; i++) tn = AddTag(tags, tn, 0, Niche(niche, i), ref _ttTags);
        tn = AddTag(tags, tn, 0, TtMega, ref _ttTags);

        var c = _tt.AsSpan();
        var room = Math.Max(0, TtCaptionCap - tn - 1);
        var n = PutClipped(c[..room], 0, summary.Trim());
        n = Put(c, n, " ");
        n = Put(c, n, tags[..tn]);
        _ttLen = n;
    }

    private void SplitNiche(ReadOnlySpan<char> niche)
    {
        _nicheCount = 0;
        var pos = 0;
        while (pos < niche.Length && _nicheCount < NicheCap)
        {
            var rest = niche[pos..];
            var comma = rest.IndexOf(',');
            var len = comma < 0 ? rest.Length : comma;
            var seg = rest[..len];
            var lead = seg.Length - seg.TrimStart().Length;
            var trimmed = seg.Trim();
            if (!trimmed.IsEmpty)
            {
                _nicheStart[_nicheCount] = pos + lead;
                _nicheLen[_nicheCount] = trimmed.Length;
                _nicheCount++;
            }
            pos += len + 1;
        }
    }

    private ReadOnlySpan<char> Niche(ReadOnlySpan<char> niche, int i) => niche.Slice(_nicheStart[i], _nicheLen[i]);

    /// <summary>"#" + 문자/숫자/_ 만 남긴 정규 태그를 " " 구분으로 덧붙임 — tagStart 이후 동일 태그(대소문자 무시) 존재 시 생략.</summary>
    private static int AddTag(Span<char> dst, int n, int tagStart, ReadOnlySpan<char> raw, ref int count)
    {
        Span<char> tag = stackalloc char[TagCap];
        var tl = 0;
        tag[tl++] = '#';
        foreach (var ch in raw)
        {
            if (tl == TagCap) break;
            if (char.IsLetterOrDigit(ch) || ch == '_') tag[tl++] = ch;
        }
        if (tl == 1) return n;
        ReadOnlySpan<char> t = tag[..tl];
        ReadOnlySpan<char> existing = dst[tagStart..n];
        var idx = 0;
        while (idx < existing.Length)
        {
            var sp = existing[idx..].IndexOf(' ');
            var tok = sp < 0 ? existing[idx..] : existing.Slice(idx, sp);
            if (tok.Equals(t, StringComparison.OrdinalIgnoreCase)) return n;
            if (sp < 0) break;
            idx += sp + 1;
        }
        var need = (n > tagStart ? 1 : 0) + tl;
        if (n + need > dst.Length) return n;
        if (n > tagStart) dst[n++] = ' ';
        t.CopyTo(dst[n..]);
        count++;
        return n + tl;
    }

    private static int Put(Span<char> dst, int n, ReadOnlySpan<char> s)
    {
        var k = Math.Min(s.Length, dst.Length - n);
        if (k <= 0) return n;
        s[..k].CopyTo(dst[n..]);
        return n + k;
    }

    /// <summary>남은 공간 초과 시 "…" 로 절단(서로게이트 쌍 분리 방지).</summary>
    private static int PutClipped(Span<char> dst, int n, ReadOnlySpan<char> s)
    {
        var room = dst.Length - n;
        if (room <= 0) return n;
        if (s.Length <= room) return Put(dst, n, s);
        var k = room - 1;
        if (k > 0 && char.IsHighSurrogate(s[k - 1])) k--;
        n = Put(dst, n, s[..k]);
        dst[n++] = '…';
        return n;
    }
}
