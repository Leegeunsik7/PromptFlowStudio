namespace PromptFlowStudio.Services.TrendJacking;

/// <summary>
/// [PromptFlow Studio v17.96.0] 🔥 무할당 실시간 트렌드 스파이크 레이더.
///   ▸ 급상승 토픽 피드(<c>키워드:건수;키워드:건수</c> 또는 줄바꿈 구분)를 <see cref="ReadOnlySpan{T}"/> 로 파싱 — 토픽 슬롯(<see cref="TopicCap"/>) 고정 char 버퍼 · 분 단위 언급 링(<see cref="MinuteRing"/>).
///   ▸ 분당 가속도: Velocity = 현재 분 − 직전 분 언급 수 · SpikeIndex(0~100) = Velocity×100 / (직전 분 + <see cref="SpikeDamp"/>).
///   ▸ 연관도(0~100): 대본 · 태그를 공백 제거 소문자 정규형으로 보관 → 키워드 정규형이 태그에 있으면 100 · 대본 85(+5/추가 출현, ≤95) ·
///     아니면 형태소 토큰(조사 제거) 커버리지 ×70 · 단일 토큰 어간(끝 1자 제거) 부분 일치 35. 연관도 ≥ <see cref="GoldenScore"/> 이고 상승 중이면 골든 토픽.
///   ▸ <see cref="Scan"/>(연관도 판정 + 가속도 랭킹) 경로 힙 할당 0.
/// </summary>
public sealed class RealtimeSocialTrendSpikeRadarEngine
{
    public const int TopicCap = 32, KeywordCap = 24, MinuteRing = 10, GoldenScore = 80, SpikeDamp = 20, ContentCap = 2048, TokenCap = 6;

    private static readonly string Particles = "은는이가을를의에도와과로";

    private readonly char[] _kw = new char[TopicCap * KeywordCap];
    private readonly int[] _kwLen = new int[TopicCap];
    private readonly int[] _counts = new int[TopicCap * MinuteRing];
    private readonly int[] _relevance = new int[TopicCap];
    private readonly int[] _rank = new int[TopicCap];
    private readonly char[] _script = new char[ContentCap];
    private readonly char[] _tags = new char[ContentCap];
    private int _scriptLen, _tagsLen, _topics, _head;

    public int TopicCount => _topics;
    public int GoldenCount { get; private set; }
    public long Minutes { get; private set; }
    public long Parsed { get; private set; }
    public long Malformed { get; private set; }
    public long Dropped { get; private set; }
    public long Scans { get; private set; }

    public ReadOnlySpan<char> Keyword(int topic) => _kw.AsSpan(topic * KeywordCap, _kwLen[topic]);
    public int Relevance(int topic) => _relevance[topic];
    public int Count(int topic, int minutesAgo = 0) => _counts[topic * MinuteRing + (_head - minutesAgo + MinuteRing * 2) % MinuteRing];
    public int Velocity(int topic) => Count(topic) - Count(topic, 1);
    public int SpikeIndex(int topic) => Math.Clamp(Velocity(topic) * 100 / (Count(topic, 1) + SpikeDamp), 0, 100);
    public bool IsGolden(int topic) => _relevance[topic] >= GoldenScore && Velocity(topic) > 0;

    /// <summary>가속도 내림차순 랭킹(0 = 최고 급상승) → 토픽 인덱스.</summary>
    public int Ranked(int i) => _rank[i];

    /// <summary>현재 제작 숏폼의 대본 · 태그 등록(비핫패스) — 공백 제거 · 소문자 정규형으로 고정 버퍼에 보관.</summary>
    public void SetContent(ReadOnlySpan<char> script, ReadOnlySpan<char> tags)
    {
        _scriptLen = Normalize(script, _script);
        _tagsLen = Normalize(tags, _tags);
    }

    /// <summary>피드 파싱 → 현재 분 버킷 누적. 반환 = 유효 레코드 수. 무할당.</summary>
    public int Ingest(ReadOnlySpan<char> feed)
    {
        var n = 0;
        while (!feed.IsEmpty)
        {
            var end = feed.IndexOfAny(';', '\n');
            var rec = end < 0 ? feed : feed[..end];
            feed = end < 0 ? default : feed[(end + 1)..];
            rec = rec.Trim();
            if (rec.IsEmpty) continue;
            var colon = rec.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(rec[(colon + 1)..].Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var cnt) || cnt < 0)
            {
                Malformed++;
                continue;
            }
            var key = rec[..colon].Trim();
            if (key.IsEmpty || key.Length > KeywordCap) { Malformed++; continue; }
            var t = FindOrAdd(key);
            if (t < 0) { Dropped++; continue; }
            _counts[t * MinuteRing + _head] += cnt;
            n++;
        }
        Parsed += n;
        return n;
    }

    /// <summary>분 경계 — 링 헤드 전진 · 새 분 버킷 0 초기화.</summary>
    public void CloseMinute()
    {
        _head = (_head + 1) % MinuteRing;
        for (var t = 0; t < TopicCap; t++) _counts[t * MinuteRing + _head] = 0;
        Minutes++;
    }

    /// <summary>전 토픽 연관도 판정 + 가속도 랭킹. 반환 = 골든 토픽 수. 핫패스 — 무할당.</summary>
    public int Scan()
    {
        var golden = 0;
        for (var t = 0; t < _topics; t++)
        {
            _relevance[t] = ScoreRelevance(Keyword(t));
            _rank[t] = t;
            if (IsGolden(t)) golden++;
        }
        for (var i = 1; i < _topics; i++)
        {
            var v = _rank[i];
            var j = i - 1;
            while (j >= 0 && Better(v, _rank[j])) { _rank[j + 1] = _rank[j]; j--; }
            _rank[j + 1] = v;
        }
        GoldenCount = golden;
        Scans++;
        return golden;
    }

    /// <summary>키워드 1 건 연관도(0~100) — 등록된 대본 · 태그 대비. 무할당.</summary>
    public int ScoreRelevance(ReadOnlySpan<char> keyword)
    {
        Span<char> norm = stackalloc char[KeywordCap];
        var len = Normalize(keyword, norm);
        if (len == 0) return 0;
        var k = norm[..len];
        var tags = _tags.AsSpan(0, _tagsLen);
        var script = _script.AsSpan(0, _scriptLen);
        if (tags.IndexOf(k) >= 0) return 100;
        var occ = Occurrences(script, k);
        if (occ > 0) return Math.Min(95, 85 + (occ - 1) * 5);

        var total = 0;
        var covered = 0;
        var stemHit = false;
        var rest = keyword;
        while (!rest.IsEmpty && total < TokenCap)
        {
            rest = rest.TrimStart();
            if (rest.IsEmpty) break;
            var sp = rest.IndexOfAny(' ', '\t');
            var tok = sp < 0 ? rest : rest[..sp];
            rest = sp < 0 ? default : rest[sp..];
            var tl = Normalize(tok, norm);
            if (tl > 1 && Particles.Contains(norm[tl - 1])) tl--;   // 형태소: 말미 조사 제거
            if (tl == 0) continue;
            total++;
            var tk = norm[..tl];
            if (tags.IndexOf(tk) >= 0 || script.IndexOf(tk) >= 0) { covered++; continue; }
            if (tl >= 3 && (tags.IndexOf(tk[..(tl - 1)]) >= 0 || script.IndexOf(tk[..(tl - 1)]) >= 0)) stemHit = true;
        }
        if (total == 0) return 0;
        if (covered > 0) return covered * 70 / total;
        return stemHit ? 35 : 0;
    }

    public void Reset()
    {
        Array.Clear(_kwLen); Array.Clear(_counts); Array.Clear(_relevance); Array.Clear(_rank);
        _topics = _head = 0;
        GoldenCount = 0;
        Minutes = Parsed = Malformed = Dropped = Scans = 0;
    }

    private bool Better(int a, int b)
    {
        var va = Velocity(a);
        var vb = Velocity(b);
        return va != vb ? va > vb : Count(a) > Count(b);
    }

    private int FindOrAdd(ReadOnlySpan<char> key)
    {
        for (var t = 0; t < _topics; t++)
            if (Keyword(t).Equals(key, StringComparison.OrdinalIgnoreCase)) return t;
        if (_topics == TopicCap) return -1;
        var slot = _topics++;
        key.CopyTo(_kw.AsSpan(slot * KeywordCap));
        _kwLen[slot] = key.Length;
        return slot;
    }

    private static int Occurrences(ReadOnlySpan<char> s, ReadOnlySpan<char> k)
    {
        var n = 0;
        int i;
        while ((i = s.IndexOf(k)) >= 0) { n++; s = s[(i + k.Length)..]; }
        return n;
    }

    /// <summary>공백 · '#' 제거 + 소문자 → dst. 반환 = 기록 길이(dst 상한 절단).</summary>
    public static int Normalize(ReadOnlySpan<char> src, Span<char> dst)
    {
        var n = 0;
        foreach (var ch in src)
        {
            if (char.IsWhiteSpace(ch) || ch == '#') continue;
            if (n == dst.Length) break;
            dst[n++] = char.ToLowerInvariant(ch);
        }
        return n;
    }
}
