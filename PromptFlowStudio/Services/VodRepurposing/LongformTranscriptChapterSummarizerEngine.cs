using System.Globalization;

namespace PromptFlowStudio.Services.VodRepurposing;

/// <summary>v17.88.0 정제된 발화(풀 내 위치 · 타임코드).</summary>
public struct TranscriptUtterance
{
    public long StartMs, EndMs;
    public int Offset, Length;
}

/// <summary>v17.88.0 문장(요약 후보) — 발화 풀 슬라이스.</summary>
public struct TranscriptSentence
{
    public long StartMs;
    public int Offset, Length, Tokens, Chapter;
    public float Score;
}

/// <summary>v17.88.0 챕터 — 경계 · 키워드(어휘 id) · 3 줄 요약(문장 id).</summary>
public struct TranscriptChapter
{
    public long StartMs, EndMs;
    public int FirstSentence, SentenceCount, Words, Kw0, Kw1, Sum0, Sum1, Sum2, Best;
    public float BoundaryScore, Density;
}

/// <summary>
/// 구어체 잔여물 정제기(무할당) — 추임새(음/어/그니까/um/uh…) 제거 · 연속 반복어 축약 · 자모만의 웃음(ㅋㅋ/ㅎㅎ) 제거 · 공백 정규화.
/// </summary>
public static class SpokenFillerFilter
{
    private static readonly string[] Fillers =
    {
        "음", "음음", "으음", "어", "어어", "아", "아아", "으", "에", "에이", "그", "저", "뭐", "막", "이제", "그니까", "그러니까", "그쵸", "뭐랄까",
        "있잖아", "있잖아요", "약간", "그냥", "네네", "예예", "um", "uh", "uhm", "er", "ah", "hmm", "erm", "like",
    };

    public static bool IsFiller(ReadOnlySpan<char> word)
    {
        foreach (var f in Fillers) if (word.Equals(f, StringComparison.OrdinalIgnoreCase)) return true;
        for (var i = 0; i < word.Length; i++) if (word[i] is < 'ㄱ' or > 'ㆎ') return false;
        return word.Length > 0;   // 자모만(ㅋㅋ·ㅎㅎ·ㅠㅠ)
    }

    /// <summary>src → dst 정제 — 기록 길이 반환(dst 부족 시 절단).</summary>
    public static int Clean(ReadOnlySpan<char> src, Span<char> dst)
    {
        var n = 0; var prevStart = -1; var prevLen = 0;
        var i = 0;
        while (i < src.Length)
        {
            while (i < src.Length && char.IsWhiteSpace(src[i])) i++;
            var s = i;
            while (i < src.Length && !char.IsWhiteSpace(src[i])) i++;
            if (i == s) break;
            var word = src[s..i];
            var body = word.TrimEnd(",.!?…~");
            var tail = word[body.Length..];
            var core = body.TrimStart(",.~");
            if (core.IsEmpty || IsFiller(core))
            {
                // 추임새가 문장 끝 부호를 들고 있으면 직전 단어에 이식
                if (n > 0 && tail.IndexOfAny(".!?") >= 0 && dst[n - 1] is not ('.' or '!' or '?')) { if (n < dst.Length) dst[n++] = tail[tail.IndexOfAny(".!?")]; }
                continue;
            }
            if (prevStart >= 0 && MemoryExtensions.Equals(dst.Slice(prevStart, prevLen), core, StringComparison.OrdinalIgnoreCase)) continue;   // 반복어
            var trimmedTail = tail.TrimStart('~');
            if (trimmedTail.Length > 1) trimmedTail = trimmedTail[..1];   // "!!!" "..." → 1 자
            var need = (n > 0 ? 1 : 0) + core.Length + trimmedTail.Length;
            if (n + need > dst.Length) break;
            if (n > 0) dst[n++] = ' ';
            prevStart = n; prevLen = core.Length;
            core.CopyTo(dst[n..]); n += core.Length;
            if (trimmedTail.Length > 0) { trimmedTail.CopyTo(dst[n..]); n += trimmedTail.Length; }
        }
        return n;
    }
}

/// <summary>
/// [PromptFlow Studio v17.88.0] 📝 장문 VOD 트랜스크립트 챕터 분할 &amp; 요약 엔진.
///   ▸ 적재: 타임코드 대본(<c>[hh:mm:ss]</c> · <c>mm:ss</c> · SRT) 파싱 → <see cref="SpokenFillerFilter"/> 정제 → 고정 문자 풀.
///   ▸ 분석(<see cref="Analyze"/> · 워밍업 후 무할당): 토큰 → 오픈 어드레싱 어휘표(FNV-1a) · 30 s 창 해시 키워드 벡터(128 차원) + 발화 밀도 →
///     TextTiling 블록 코사인 유사도 낙차 + 밀도 변화 → 3~7 분 제약 탐욕 경계 선택(발화 시작에 스냅).
///   ▸ 챕터별 TF-IDF 상위 2 어휘 = 제목 · 문장 점수(Σ 가중/√토큰) 상위 3 = 3 줄 요약 · 인접 내용어 바이그램 빈도 = 롱테일 SEO 5 종.
///   ▸ 포맷(<see cref="WriteChapterList"/> 등)은 호출자 Span 에 직접 기록 — 문자열 생성 없음.
/// </summary>
public sealed class LongformTranscriptChapterSummarizerEngine
{
    public const int PoolChars = 1 << 20, MaxUtterances = 16_384, MaxSentences = 32_768, MaxTokens = 262_144, VocabCap = 8192, BigramCap = 8192;
    public const int MaxChapters = 64, MaxWindows = 2048, Dim = 128, WindowMs = 30_000, BlockWindows = 4, SeoCount = 5;
    public const long MinChapterMs = 3 * 60_000, MaxChapterMs = 7 * 60_000;
    /// <summary>타임코드 없는 대본의 발화 속도 추정(자/초).</summary>
    public const double CharsPerSecond = 6.5;

    private static readonly string[] Stopwords =
    {
        "그리고", "그래서", "하지만", "그런데", "근데", "진짜", "정말", "너무", "이거", "저거", "그거", "이게", "그게", "여기", "거기", "저희", "우리", "제가", "내가", "저는",
        "이렇게", "그렇게", "저렇게", "오늘", "지금", "여러분", "많이", "조금", "같은", "이런", "그런", "저런", "있는", "없는", "하는", "되는", "어떤", "모든", "다시", "먼저",
        "때문", "정도", "부분", "경우", "생각", "이번", "바로", "계속", "가장", "아주", "다음", "하나", "the", "and", "for", "that", "this", "with", "you", "are", "was",
        "have", "just", "really", "very", "what", "but", "not", "its", "our", "your", "they", "from", "will", "can", "all", "about", "there", "then", "when",
    };

    private static readonly string[] Josa =
    {
        "에서는", "으로는", "에게서", "이라는", "에서", "으로", "에게", "까지", "부터", "라는", "처럼", "보다", "하고", "이랑", "은", "는", "이", "가", "을", "를", "에", "의", "도", "로", "와", "과", "만", "랑",
    };

    private static readonly string[] VerbEndings =
    {
        "니다", "어요", "아요", "해요", "에요", "예요", "죠", "는데", "지만", "거든요", "잖아요", "네요", "세요", "했다", "한다", "하면", "해서", "어서", "아서", "니까", "는지", "도록", "었어", "았어",
    };

    private readonly char[] _pool = new char[PoolChars];
    private readonly TranscriptUtterance[] _utt = new TranscriptUtterance[MaxUtterances];
    private readonly TranscriptSentence[] _sent = new TranscriptSentence[MaxSentences];
    private readonly TranscriptChapter[] _ch = new TranscriptChapter[MaxChapters];
    private readonly int[] _tokVocab = new int[MaxTokens], _tokSent = new int[MaxTokens];
    private readonly ulong[] _vHash = new ulong[VocabCap];
    private readonly int[] _vOff = new int[VocabCap], _vLen = new int[VocabCap], _vCount = new int[VocabCap], _vDf = new int[VocabCap], _vSlot = new int[VocabCap];
    private readonly int[] _chTf = new int[MaxChapters * VocabCap];
    private readonly long[] _bKey = new long[BigramCap];
    private readonly int[] _bCount = new int[BigramCap];
    private readonly float[] _win = new float[MaxWindows * Dim];
    private readonly int[] _winChars = new int[MaxWindows];
    private readonly float[] _score = new float[MaxWindows];
    private readonly int[] _seoA = new int[SeoCount], _seoB = new int[SeoCount];
    private int _poolLen, _uttN, _sentN, _tokN, _vocabN, _chN, _winN, _seoN;

    public int UtteranceCount => _uttN;
    public int SentenceCount => _sentN;
    public int TokenCount => _tokN;
    public int VocabCount => _vocabN;
    public int ChapterCount => _chN;
    public int WindowCount => _winN;
    public int SeoKeywordCount => _seoN;
    public long DurationMs => _uttN == 0 ? 0 : _utt[_uttN - 1].EndMs;
    public long FillersRemoved { get; private set; }
    public long Analyses { get; private set; }
    public double LastAnalyzeMs { get; private set; }
    public ref readonly TranscriptChapter Chapter(int i) => ref _ch[i];
    public ref readonly TranscriptSentence Sentence(int i) => ref _sent[i];
    public ref readonly TranscriptUtterance Utterance(int i) => ref _utt[i];
    public ReadOnlySpan<char> UtteranceText(int i) => _pool.AsSpan(_utt[i].Offset, _utt[i].Length);
    public ReadOnlySpan<char> SentenceText(int i) => _pool.AsSpan(_sent[i].Offset, _sent[i].Length);
    public ReadOnlySpan<char> Word(int vocab) => vocab < 0 ? default : _pool.AsSpan(_vOff[vocab], _vLen[vocab]);
    public float WindowScore(int w) => _score[w];

    // ══════════════════════ 적재(비핫패스) ══════════════════════

    public void Clear()
    {
        _poolLen = _uttN = _sentN = _tokN = _chN = _winN = _seoN = 0;
        ClearVocab();
        FillersRemoved = 0;
    }

    /// <summary>발화 1 건 정제 적재 — 성공 여부(풀/슬롯 포화 시 false).</summary>
    public bool AddUtterance(long startMs, long endMs, ReadOnlySpan<char> raw)
    {
        if (_uttN >= MaxUtterances || _poolLen + raw.Length + 1 > PoolChars) return false;
        var n = SpokenFillerFilter.Clean(raw, _pool.AsSpan(_poolLen, Math.Min(raw.Length + 1, PoolChars - _poolLen)));
        FillersRemoved += CountWords(raw) - CountWords(_pool.AsSpan(_poolLen, n));
        if (n == 0) return true;
        if (_uttN > 0 && startMs < _utt[_uttN - 1].StartMs) startMs = _utt[_uttN - 1].StartMs;
        if (_uttN > 0 && _utt[_uttN - 1].EndMs > startMs) _utt[_uttN - 1].EndMs = startMs;
        _utt[_uttN++] = new TranscriptUtterance { StartMs = startMs, EndMs = Math.Max(endMs, startMs + 500), Offset = _poolLen, Length = n };
        _poolLen += n;
        return true;
    }

    private static int CountWords(ReadOnlySpan<char> s)
    {
        var n = 0; var inWord = false;
        foreach (var c in s) { var w = !char.IsWhiteSpace(c); if (w && !inWord) n++; inWord = w; }
        return n;
    }

    /// <summary>대본 텍스트 적재 — 줄 머리 타임코드(<c>[01:02:03]</c> · <c>12:34</c>) · SRT 블록 · 무타임코드(발화 속도 추정) 지원. 적재 발화 수 반환.</summary>
    public int Load(ReadOnlySpan<char> text)
    {
        Clear();
        long clock = 0, pendingStart = -1, pendingEnd = -1;
        var rest = text;
        while (!rest.IsEmpty)
        {
            var nl = rest.IndexOf('\n');
            var line = (nl < 0 ? rest : rest[..nl]).Trim();
            rest = nl < 0 ? default : rest[(nl + 1)..];
            if (line.IsEmpty) continue;
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow > 0)
            {
                if (TryParseClock(line[..arrow].Trim(), out var a, out _)) pendingStart = a;
                if (TryParseClock(line[(arrow + 3)..].Trim(), out var b, out _)) pendingEnd = b;
                continue;
            }
            if (IsAllDigits(line)) continue;   // SRT 순번
            long start;
            if (line[0] == '[')
            {
                var close = line.IndexOf(']');
                if (close > 0 && TryParseClock(line[1..close], out start, out _)) line = line[(close + 1)..].Trim();
                else start = -1;
            }
            else if (TryParseClock(line, out start, out var used)) line = line[used..].TrimStart(" -–:\t");
            else start = -1;
            if (start < 0 && pendingStart >= 0) start = pendingStart;
            if (start < 0) start = clock;
            var est = (long)(line.Length / CharsPerSecond * 1000);
            var end = pendingEnd > start ? pendingEnd : start + Math.Max(1000, est);
            pendingStart = pendingEnd = -1;
            if (!AddUtterance(start, end, line)) break;
            clock = end;
        }
        return _uttN;
    }

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>머리 <c>h:mm:ss[,.mmm]</c> · <c>mm:ss</c> 파싱 — 소비 길이 반환.</summary>
    public static bool TryParseClock(ReadOnlySpan<char> s, out long ms, out int used)
    {
        ms = 0; used = 0;
        long acc = 0; var parts = 0; var i = 0;
        while (parts < 3)
        {
            var st = i;
            while (i < s.Length && i - st < 3 && char.IsAsciiDigit(s[i])) i++;
            if (i == st || !int.TryParse(s[st..i], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) break;
            acc = acc * 60 + v; parts++;
            if (i < s.Length && s[i] == ':' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])) { i++; continue; }
            break;
        }
        if (parts < 2) return false;
        ms = acc * 1000;
        if (i < s.Length && s[i] is ',' or '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1]))
        {
            var st = ++i;
            while (i < s.Length && i - st < 3 && char.IsAsciiDigit(s[i])) i++;
            if (int.TryParse(s[st..i], NumberStyles.None, CultureInfo.InvariantCulture, out var frac)) ms += i - st == 3 ? frac : i - st == 2 ? frac * 10 : frac * 100;
        }
        used = i;
        return true;
    }

    // ══════════════════════ 분석(워밍업 후 무할당) ══════════════════════

    /// <summary>문장 분할 → 어휘/창 벡터 → 경계 → 챕터 키워드/요약 → SEO. 챕터 수 반환.</summary>
    public int Analyze()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _sentN = _tokN = _chN = _seoN = 0;
        ClearVocab();
        Array.Clear(_bKey); Array.Clear(_bCount);
        if (_uttN == 0) { LastAnalyzeMs = 0; return 0; }
        _winN = (int)Math.Min(MaxWindows, DurationMs / WindowMs + 1);
        Array.Clear(_win, 0, _winN * Dim);
        Array.Clear(_winChars, 0, _winN);
        Array.Clear(_score, 0, _winN);

        for (var u = 0; u < _uttN; u++) SplitSentences(u);

        // 1) 창 벡터 · 밀도
        for (var t = 0; t < _tokN; t++)
        {
            var w = Win(_sent[_tokSent[t]].StartMs);
            _win[w * Dim + (int)(_vHash[_tokVocab[t]] & (Dim - 1))] += 1f;
        }
        for (var u = 0; u < _uttN; u++) _winChars[Win(_utt[u].StartMs)] += _utt[u].Length;

        // 2) 경계 점수(1 − 블록 코사인) + 0.3 · 밀도 변화
        for (var b = 1; b < _winN; b++)
        {
            float dot = 0, na = 0, nb = 0; int ca = 0, cb = 0;
            for (var k = 0; k < Dim; k++)
            {
                float l = 0, r = 0;
                for (var j = 1; j <= BlockWindows; j++)
                {
                    if (b - j >= 0) l += _win[(b - j) * Dim + k];
                    if (b + j - 1 < _winN) r += _win[(b + j - 1) * Dim + k];
                }
                dot += l * r; na += l * l; nb += r * r;
            }
            for (var j = 1; j <= BlockWindows; j++) { if (b - j >= 0) ca += _winChars[b - j]; if (b + j - 1 < _winN) cb += _winChars[b + j - 1]; }
            var cos = na > 0 && nb > 0 ? dot / MathF.Sqrt(na * nb) : 0f;
            var dens = Math.Abs(ca - cb) / (float)Math.Max(1, Math.Max(ca, cb));
            _score[b] = 1f - cos + 0.3f * dens;
        }

        // 3) 3~7 분 탐욕 경계
        long total = DurationMs, start = 0;
        var firstSent = 0;
        while (_chN < MaxChapters - 1 && total - start > MaxChapterMs)
        {
            var lo = (int)((start + MinChapterMs + WindowMs - 1) / WindowMs);
            var hi = (int)(Math.Min(start + MaxChapterMs, total - MinChapterMs) / WindowMs);
            if (hi < lo) hi = lo;
            var best = lo; var bestScore = -1f;
            for (var b = lo; b <= hi && b < _winN; b++) if (_score[b] > bestScore) { bestScore = _score[b]; best = b; }
            var cut = SnapToSentence(best * (long)WindowMs, firstSent);
            if (cut <= firstSent || cut >= _sentN) break;
            var cutMs = _sent[cut].StartMs;
            if (cutMs - start < MinChapterMs - WindowMs) break;
            PushChapter(start, cutMs, firstSent, cut, bestScore);
            start = cutMs; firstSent = cut;
        }
        PushChapter(start, total, firstSent, _sentN, _chN == 0 ? 0f : _ch[_chN - 1].BoundaryScore);
        if (_chN > 0) _ch[0].StartMs = 0;

        // 4) 챕터 TF · DF
        Array.Clear(_chTf, 0, _chN * VocabCap);
        for (var t = 0; t < _tokN; t++)
        {
            var c = _sent[_tokSent[t]].Chapter; var v = _tokVocab[t];
            if (_chTf[c * VocabCap + v]++ == 0) _vDf[v]++;
        }
        for (var c = 0; c < _chN; c++) Summarize(c);
        SelectSeo();

        Analyses++;
        LastAnalyzeMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return _chN;
    }

    private int Win(long ms) => (int)Math.Min(_winN - 1, ms / WindowMs);

    private int SnapToSentence(long ms, int from)
    {
        for (var s = from + 1; s < _sentN; s++) if (_sent[s].StartMs >= ms) return s;
        return _sentN;
    }

    private void PushChapter(long startMs, long endMs, int firstSent, int endSent, float score)
    {
        ref var c = ref _ch[_chN];
        c = default;
        c.StartMs = startMs; c.EndMs = endMs; c.FirstSentence = firstSent; c.SentenceCount = endSent - firstSent; c.BoundaryScore = score;
        c.Kw0 = c.Kw1 = c.Sum0 = c.Sum1 = c.Sum2 = c.Best = -1;
        var chars = 0;
        for (var s = firstSent; s < endSent; s++) { _sent[s].Chapter = _chN; c.Words += _sent[s].Tokens; chars += _sent[s].Length; }
        c.Density = chars / (float)Math.Max(1.0, (endMs - startMs) / 1000.0);
        _chN++;
    }

    private void SplitSentences(int u)
    {
        ref readonly var ut = ref _utt[u];
        var text = _pool.AsSpan(ut.Offset, ut.Length);
        var s = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            var end = i == text.Length || (text[i] is '.' or '?' or '!' or '…' or '。' && (i + 1 == text.Length || text[i + 1] == ' '));
            if (!end) continue;
            var stop = i < text.Length ? i + 1 : i;
            while (s < stop && text[s] == ' ') s++;
            if (stop - s >= 2 && _sentN < MaxSentences)
            {
                var lead = ut.Length == 0 ? 0 : (long)((ut.EndMs - ut.StartMs) * (s / (double)ut.Length));
                ref var st = ref _sent[_sentN];
                st = new TranscriptSentence { StartMs = ut.StartMs + lead, Offset = ut.Offset + s, Length = stop - s };
                Tokenize(_sentN);
                _sentN++;
            }
            s = stop;
        }
    }

    private void Tokenize(int sent)
    {
        ref var st = ref _sent[sent];
        var text = _pool.AsSpan(st.Offset, st.Length);
        var prev = -1;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            var b = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            if (i == b) break;
            st.Tokens++;
            var tok = Normalize(text[b..i], out var keep);
            if (!keep || _tokN >= MaxTokens) { prev = -1; continue; }
            var v = Intern(tok, st.Offset + b);
            if (v < 0) { prev = -1; continue; }
            _tokVocab[_tokN] = v; _tokSent[_tokN] = sent; _tokN++;
            if (prev >= 0 && prev != v) AddBigram(prev, v);
            prev = v;
        }
    }

    /// <summary>조사 절단 · 서술 어미/불용어/짧은 토큰 배제.</summary>
    private static ReadOnlySpan<char> Normalize(ReadOnlySpan<char> tok, out bool keep)
    {
        keep = false;
        var hangul = tok.Length > 0 && tok[0] is >= '가' and <= '힣';
        if (hangul)
        {
            foreach (var e in VerbEndings) if (tok.Length > e.Length && tok.EndsWith(e, StringComparison.Ordinal)) return tok;
            foreach (var j in Josa) if (tok.Length - j.Length >= 2 && tok.EndsWith(j, StringComparison.Ordinal)) { tok = tok[..^j.Length]; break; }
            if (tok.Length < 2) return tok;
        }
        else if (tok.Length < 3 || char.IsAsciiDigit(tok[0])) return tok;
        foreach (var w in Stopwords) if (tok.Equals(w, StringComparison.OrdinalIgnoreCase)) return tok;
        keep = true;
        return tok;
    }

    private static ulong Fnv(ReadOnlySpan<char> s)
    {
        var h = 14695981039346656037UL;
        foreach (var c in s) { h ^= char.ToLowerInvariant(c); h *= 1099511628211UL; }
        return h;
    }

    private void ClearVocab()
    {
        Array.Fill(_vSlot, -1);
        _vocabN = 0;
    }

    /// <summary>오픈 어드레싱 어휘 등록(표면형 = 첫 출현 위치) — 어휘 id.</summary>
    private int Intern(ReadOnlySpan<char> tok, int poolOffset)
    {
        var h = Fnv(tok);
        var slot = (int)(h & (VocabCap - 1));
        for (var probe = 0; probe < VocabCap; probe++, slot = (slot + 1) & (VocabCap - 1))
        {
            var v = _vSlot[slot];
            if (v < 0)
            {
                if (_vocabN >= VocabCap * 3 / 4) return -1;
                v = _vocabN++;
                _vSlot[slot] = v; _vHash[v] = h; _vOff[v] = poolOffset; _vLen[v] = tok.Length; _vCount[v] = 1; _vDf[v] = 0;
                return v;
            }
            if (_vHash[v] == h && _vLen[v] == tok.Length && MemoryExtensions.Equals(_pool.AsSpan(_vOff[v], _vLen[v]), tok, StringComparison.OrdinalIgnoreCase)) { _vCount[v]++; return v; }
        }
        return -1;
    }

    private void AddBigram(int a, int b)
    {
        var key = ((long)(a + 1) << 32) | (uint)(b + 1);
        var slot = (int)((ulong)key * 0x9E3779B97F4A7C15UL >> 51) & (BigramCap - 1);
        for (var probe = 0; probe < 64; probe++, slot = (slot + 1) & (BigramCap - 1))
        {
            if (_bKey[slot] == key) { _bCount[slot]++; return; }
            if (_bKey[slot] == 0) { _bKey[slot] = key; _bCount[slot] = 1; return; }
        }
    }

    private float Idf(int v) => MathF.Log((1f + _chN) / (1f + _vDf[v])) + 1f;

    private void Summarize(int c)
    {
        ref var ch = ref _ch[c];
        float b0 = 0, b1 = 0;
        var row = c * VocabCap;
        for (var v = 0; v < _vocabN; v++)
        {
            var tf = _chTf[row + v];
            if (tf == 0) continue;
            var w = tf * Idf(v) * (1f + 0.04f * _vLen[v]);
            if (w > b0) { b1 = b0; ch.Kw1 = ch.Kw0; b0 = w; ch.Kw0 = v; }
            else if (w > b1) { b1 = w; ch.Kw1 = v; }
        }
        // 문장 점수 → 상위 3(시간순)
        float s0 = -1, s1 = -1, s2 = -1; int i0 = -1, i1 = -1, i2 = -1;
        var t = 0;
        while (t < _tokN && _sent[_tokSent[t]].Chapter < c) t++;
        for (var s = ch.FirstSentence; s < ch.FirstSentence + ch.SentenceCount; s++)
        {
            float sum = 0;
            for (; t < _tokN && _tokSent[t] == s; t++) { var v = _tokVocab[t]; sum += _chTf[row + v] * Idf(v); }
            ref var st = ref _sent[s];
            var len = st.Length;
            st.Score = st.Tokens < 3 || len > 160 ? 0 : sum / MathF.Sqrt(st.Tokens + 1f);
            if (st.Score > s0) { s2 = s1; i2 = i1; s1 = s0; i1 = i0; s0 = st.Score; i0 = s; }
            else if (st.Score > s1) { s2 = s1; i2 = i1; s1 = st.Score; i1 = s; }
            else if (st.Score > s2) { s2 = st.Score; i2 = s; }
        }
        ch.Best = i0;
        // 시간순 정렬(3 원소)
        if (i1 >= 0 && i0 > i1) (i0, i1) = (i1, i0);
        if (i2 >= 0 && i1 > i2) (i1, i2) = (i2, i1);
        if (i1 >= 0 && i0 > i1) (i0, i1) = (i1, i0);
        ch.Sum0 = i0; ch.Sum1 = i1; ch.Sum2 = i2;
    }

    private void SelectSeo()
    {
        // 바이그램(빈도 ≥ 2) 상위 → 부족분은 전역 TF-IDF 유니그램
        for (var k = 0; k < SeoCount; k++)
        {
            var best = -1; var bestW = 0f;
            for (var s = 0; s < BigramCap; s++)
            {
                if (_bKey[s] == 0 || _bCount[s] < 2) continue;
                int a = (int)(_bKey[s] >> 32) - 1, b = (int)(_bKey[s] & 0xFFFFFFFF) - 1;
                if (SeoHas(a, b)) continue;
                var w = _bCount[s] * (_vLen[a] + _vLen[b]) * 0.5f;
                if (w > bestW) { bestW = w; best = s; }
            }
            if (best < 0) break;
            _seoA[_seoN] = (int)(_bKey[best] >> 32) - 1; _seoB[_seoN] = (int)(_bKey[best] & 0xFFFFFFFF) - 1; _seoN++;
        }
        while (_seoN < SeoCount)
        {
            var best = -1; var bestW = 0f;
            for (var v = 0; v < _vocabN; v++)
            {
                if (_vCount[v] < 2 || SeoHas(v, -1)) continue;
                var w = _vCount[v] * (1f + 0.04f * _vLen[v]);
                if (w > bestW) { bestW = w; best = v; }
            }
            if (best < 0) break;
            _seoA[_seoN] = best; _seoB[_seoN] = -1; _seoN++;
        }
    }

    private bool SeoHas(int a, int b)
    {
        for (var i = 0; i < _seoN; i++)
        {
            if (_seoA[i] == a && _seoB[i] == b) return true;
            if (b < 0 && (_seoA[i] == a || _seoB[i] == a)) return true;
        }
        return false;
    }

    // ══════════════════════ 포맷(무할당 · 호출자 Span) ══════════════════════

    /// <summary>YouTube 호환 타임코드(<c>MM:SS</c> · 1 시간 이상 <c>H:MM:SS</c>).</summary>
    public static int FormatTimecode(long ms, Span<char> dst)
    {
        var sec = Math.Max(0, ms / 1000);
        long h = sec / 3600, m = sec / 60 % 60, s = sec % 60;
        var n = 0;
        if (h > 0) { h.TryFormat(dst, out n, default, CultureInfo.InvariantCulture); dst[n++] = ':'; }
        m.TryFormat(dst[n..], out var w, "00", CultureInfo.InvariantCulture); n += w;
        dst[n++] = ':';
        s.TryFormat(dst[n..], out w, "00", CultureInfo.InvariantCulture); n += w;
        return n;
    }

    /// <summary>챕터 제목 "키워드1 · 키워드2".</summary>
    public int WriteChapterTitle(int c, Span<char> dst)
    {
        ref readonly var ch = ref _ch[c];
        var n = 0;
        if (ch.Kw0 < 0) { n = Put(dst, 0, "이야기 "); (c + 1).TryFormat(dst[n..], out var w, default, CultureInfo.InvariantCulture); return n + w; }
        n = Put(dst, n, Word(ch.Kw0));
        if (ch.Kw1 >= 0) { n = Put(dst, n, " · "); n = Put(dst, n, Word(ch.Kw1)); }
        return n;
    }

    /// <summary>"00:00 - 제목" 줄 목록.</summary>
    public int WriteChapterList(Span<char> dst)
    {
        var n = 0;
        for (var c = 0; c < _chN && n < dst.Length - 64; c++)
        {
            n += FormatTimecode(_ch[c].StartMs, dst[n..]);
            n = Put(dst, n, " - ");
            n += WriteChapterTitle(c, dst[n..]);
            dst[n++] = '\n';
        }
        return n;
    }

    /// <summary>챕터 3 줄 요약("• 문장" × ≤3).</summary>
    public int WriteSummary(int c, Span<char> dst, ReadOnlySpan<char> bullet)
    {
        ref readonly var ch = ref _ch[c];
        var n = 0;
        for (var k = 0; k < 3; k++)
        {
            var s = k == 0 ? ch.Sum0 : k == 1 ? ch.Sum1 : ch.Sum2;
            if (s < 0) continue;
            n = Put(dst, n, bullet);
            n = Put(dst, n, SentenceText(s));
            if (n < dst.Length) dst[n++] = '\n';
        }
        return n;
    }

    /// <summary>SEO 키워드 i(바이그램은 공백 결합).</summary>
    public int WriteSeo(int i, Span<char> dst, bool hashtag = false)
    {
        var n = 0;
        if (hashtag) n = Put(dst, n, "#");
        n = Put(dst, n, Word(_seoA[i]));
        if (_seoB[i] >= 0) { if (!hashtag) n = Put(dst, n, " "); n = Put(dst, n, Word(_seoB[i])); }
        return n;
    }

    public bool SeoIsLongTail(int i) => _seoB[i] >= 0;

    private static int Put(Span<char> dst, int n, ReadOnlySpan<char> s)
    {
        var k = Math.Min(s.Length, dst.Length - n);
        if (k <= 0) return n;
        s[..k].CopyTo(dst[n..]);
        return n + k;
    }
}
