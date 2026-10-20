using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.LiveDubbing;
using PromptFlowStudio.Services.Localization.Dubbing;

namespace PromptFlowStudio.Services.GlobalDubbing;

/// <summary>v17.64.0 번역 청크 1개(출력 링 위치 · 화자 종료 시각 → 사출 시각 · 지연).</summary>
public struct TranslationChunk
{
    public long Seq;
    public DubLanguage Lang;
    public int Start, Len, Words, Unknown;
    public double CloseMs, ReadyMs, EmitMs, LatencyMs, CpuUs;
}

/// <summary>v17.64.0 대상 언어 음소 이벤트(발화 타임라인 ms).</summary>
public struct PhonemeEvent
{
    public LipPhoneme Ph;
    public float DurMs;
    public double StartMs;
    public long Chunk;
}

/// <summary>v17.64.0 청크 소비자 — 원문 줄(원본 음성) · 번역 청크(무할당 스팬 전달).</summary>
public interface ITranslationChunkSink
{
    void OnSourceLine(ReadOnlySpan<char> korean);
    void OnChunk(in TranslationChunk chunk, ReadOnlySpan<char> text);
}

/// <summary>
/// [PromptFlow Studio v17.64.0] 실시간 다국어 스트리밍 번역 &amp; 음소 추출 엔진(Pure BCL · 무할당 핫패스).
///   ▸ 한국어 입력 줄 → 어절 단위 청취 시계(v17.32 <see cref="RealtimeNeuralVoiceCloningDubbingEngine.Plan"/> 한국어 발화 길이) → ≤ 3 어절 또는 구두점마다 청크 확정.
///   ▸ 청크 = 구(句) 우선 최장 일치 어휘 사전 → 미등록 어절은 국어의 로마자 표기법 음역 → 대상 언어(EN/JA/ES/ZH/FR/DE) 출력 링 · 언어별 병렬 모델 지연(≤ 130 ms) 후 사출.
///   ▸ 사출 청크 → 언어 고유 운율(모음 · 자음 · 휴지 ms) G2P → 언어별 음소 링(2048). 청크 확정 → 사출 지연 예산 200 ms.
/// </summary>
public sealed class RealtimeMultilingualTranslationPhonemeEngine
{
    public const int InputCap = 8192, OutCap = 16384, LineRing = 32, ChunkWords = 3, PendingCap = 64, RecentCap = 64, PhonemeRing = 2048, MaxWordsPerStep = 64;
    public const double BudgetMs = 200;

    public static readonly DubLanguage[] Targets = { DubLanguage.En, DubLanguage.Ja, DubLanguage.Es, DubLanguage.Zh, DubLanguage.Fr, DubLanguage.De };

    // 한국어 표제(구 · 어간) → EN · JA · ES · ZH · FR · DE (로마자 · 병음) — 최장 일치 · 빈 문자열 = 조사/경칭 생략
    private static readonly string[][] Lexicon =
    {
        new[] { "안녕하세요 여러분", "hello everyone", "minasan konnichiwa", "hola a todos", "da jia hao", "bonjour a tous", "hallo zusammen" },
        new[] { "안녕하세요", "hello", "konnichiwa", "hola", "ni hao", "bonjour", "hallo" },
        new[] { "여러분이", "you all are", "minasan wa", "ustedes son", "ni men", "vous etes", "ihr seid" },
        new[] { "여러분을", "you all", "minasan o", "a ustedes", "ni men", "vous", "euch" },
        new[] { "여러분", "everyone", "minasan", "todos", "da jia", "tout le monde", "alle" },
        new[] { "오늘도", "again today", "kyou mo", "hoy tambien", "jin tian ye", "encore aujourd'hui", "heute wieder" },
        new[] { "오늘", "today", "kyou", "hoy", "jin tian", "aujourd'hui", "heute" },
        new[] { "와 주셔서", "for coming", "kite kurete", "por venir", "lai le", "d'etre venus", "dass ihr da seid" },
        new[] { "감사합니다", "thank you", "arigatou gozaimasu", "gracias", "xie xie", "merci", "danke" },
        new[] { "감사해요", "thank you", "arigatou", "gracias", "xie xie", "merci", "danke" },
        new[] { "감사", "thanks", "kansha", "gracias", "gan xie", "merci", "dank" },
        new[] { "고마워요", "thank you", "arigatou", "gracias", "xie xie", "merci", "danke" },
        new[] { "고마워", "thanks", "arigatou", "gracias", "xie xie", "merci", "danke" },
        new[] { "정말", "really", "hontou ni", "de verdad", "zhen de", "vraiment", "wirklich" },
        new[] { "진짜", "really", "maji de", "en serio", "zhen de", "vraiment", "echt" },
        new[] { "너무", "so", "totemo", "muy", "tai", "tellement", "so" },
        new[] { "사랑해요", "i love you", "aishiteru", "los quiero", "wo ai ni men", "je vous aime", "ich liebe euch" },
        new[] { "사랑", "love", "ai", "amor", "ai", "amour", "liebe" },
        new[] { "이번", "this", "kondo no", "esta", "zhe ci", "cette", "dieses" },
        new[] { "신곡은", "new song", "shinkyoku wa", "nueva cancion", "xin ge", "nouvelle chanson", "neuer song" },
        new[] { "신곡", "new song", "shinkyoku", "nueva cancion", "xin ge", "nouvelle chanson", "neuer song" },
        new[] { "준비했어요", "i prepared", "junbi shimashita", "prepare", "zhun bei le", "j'ai prepare", "vorbereitet" },
        new[] { "준비", "prepare", "junbi", "preparar", "zhun bei", "preparer", "vorbereiten" },
        new[] { "위해", "for", "tame ni", "para", "wei le", "pour", "fuer" },
        new[] { "다음 주에", "next week", "raishuu", "la proxima semana", "xia zhou", "la semaine prochaine", "naechste woche" },
        new[] { "다음", "next", "tsugi", "proximo", "xia ci", "prochain", "naechste" },
        new[] { "다시", "again", "mata", "otra vez", "zai", "encore", "wieder" },
        new[] { "만나요", "see you", "aimashou", "nos vemos", "jian", "a bientot", "bis bald" },
        new[] { "항상", "always", "itsumo", "siempre", "yi zhi", "toujours", "immer" },
        new[] { "응원해", "supporting", "ouen shite", "apoyarme", "zhi chi", "soutenir", "unterstuetzen" },
        new[] { "응원", "support", "ouen", "apoyo", "zhi chi", "soutien", "unterstuetzung" },
        new[] { "후원", "donation", "kifu", "donacion", "da shang", "don", "spende" },
        new[] { "님", "", "san", "", "", "", "" },
        new[] { "원", "won", "en", "wones", "han yuan", "wons", "won" },
        new[] { "와", "wow", "waa", "guau", "wa", "waouh", "wow" },
        new[] { "최고의", "the best", "saikou no", "el mejor", "zui bang de", "le meilleur", "der beste" },
        new[] { "최고", "the best", "saikou", "lo mejor", "zui bang", "le meilleur", "der beste" },
        new[] { "방송에서", "on stream", "haishin de", "en el directo", "zai zhi bo", "en live", "im stream" },
        new[] { "방송", "stream", "haishin", "directo", "zhi bo", "live", "stream" },
        new[] { "만들어요", "let's make", "tsukurimashou", "hagamos", "zuo ba", "faisons", "machen wir" },
        new[] { "우리", "we", "watashitachi", "nosotros", "wo men", "nous", "wir" },
        new[] { "함께", "together", "issho ni", "juntos", "yi qi", "ensemble", "zusammen" },
        new[] { "덕분에", "thanks to you", "okage de", "gracias a ustedes", "duo kui ni men", "grace a vous", "dank euch" },
        new[] { "행복해요", "i'm happy", "shiawase desu", "estoy feliz", "hen xing fu", "je suis heureux", "ich bin gluecklich" },
        new[] { "좋은", "good", "ii", "buena", "hao de", "bonne", "gute" },
        new[] { "질문이네요", "question", "shitsumon desu ne", "pregunta", "wen ti", "question", "frage" },
        new[] { "질문", "question", "shitsumon", "pregunta", "wen ti", "question", "frage" },
        new[] { "잠깐", "a moment", "chotto", "un momento", "deng yi xia", "un instant", "moment" },
        new[] { "생각해", "think", "kangaete", "pensar", "xiang", "reflechir", "denken" },
        new[] { "생각", "thought", "kangae", "idea", "xiang fa", "idee", "gedanke" },
        new[] { "음", "um", "eto", "eh", "en", "euh", "aehm" },
        new[] { "글쎄요", "well", "sou desu ne", "pues", "zhe ge", "eh bien", "naja" },
        new[] { "아마", "maybe", "tabun", "quizas", "ye xu", "peut-etre", "vielleicht" },
        new[] { "이렇게", "like this", "kou", "asi", "zhe yang", "comme ca", "so" },
        new[] { "하면", "if we do", "sureba", "si lo hacemos", "zuo de hua", "si on fait", "wenn wir" },
        new[] { "될 것 같아요", "it should work", "umaku iku", "funcionara", "ying gai xing", "ca devrait marcher", "sollte klappen" },
        new[] { "그건", "that", "sore wa", "eso", "na ge", "ca", "das" },
        new[] { "저도", "i also", "watashi mo", "yo tambien", "wo ye", "moi aussi", "ich auch" },
        new[] { "알려드릴게요", "i will tell you", "oshiemasu", "les contare", "gao su ni men", "je vous dirai", "sage ich euch" },
        new[] { "칭찬", "compliment", "homete", "cumplido", "kua jiang", "compliment", "lob" },
        new[] { "당신이", "you are", "anata ga", "tu eres", "ni shi", "tu es", "du bist" },
        new[] { "멋져요", "awesome", "suteki", "increibles", "tai bang le", "geniaux", "toll" },
        new[] { "웃겨요", "so funny", "omoshiroi", "muy gracioso", "hao xiao", "trop drole", "so lustig" },
        new[] { "웃기다", "funny", "omoshiroi", "gracioso", "hao xiao", "drole", "lustig" },
        new[] { "못 참겠어요", "i can't stop", "gaman dekinai", "no puedo parar", "ren bu zhu", "je craque", "ich kann nicht mehr" },
        new[] { "ㅋㅋ", "haha", "haha", "jaja", "haha", "haha", "haha" },
        new[] { "아", "ah", "aa", "ah", "a", "ah", "ach" },
        new[] { "반가워요", "nice to meet you", "hajimemashite", "encantado", "hen gao xing", "enchante", "freut mich" },
        new[] { "시작할게요", "let's start", "hajimemasu", "empecemos", "kai shi ba", "commencons", "fangen wir an" },
        new[] { "노래", "song", "uta", "cancion", "ge", "chanson", "lied" },
    };

    // 국어의 로마자 표기법(초성 19 · 중성 21 · 종성 28)
    private static readonly string[] RrCho = { "g", "kk", "n", "d", "tt", "r", "m", "b", "pp", "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h" };
    private static readonly string[] RrJung = { "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa", "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i" };
    private static readonly string[] RrJong = { "", "k", "k", "k", "n", "n", "n", "t", "l", "k", "m", "l", "l", "l", "p", "l", "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", "t" };

    // 언어 운율(모음 · 자음 · 휴지 ms) · 병렬 청크 모델 지연(기본 + 어절당 ms)
    private static readonly float[] VowelMs = { 100f, 72f, 80f, 115f, 90f, 100f };
    private static readonly float[] ConsMs = { 62f, 48f, 52f, 60f, 56f, 66f };
    private static readonly float[] PauseMs = { 170f, 150f, 150f, 170f, 160f, 170f };
    private static readonly float[] ModelBaseMs = { 48f, 62f, 52f, 58f, 54f, 56f };
    private static readonly float[] ModelWordMs = { 14f, 16f, 14f, 15f, 15f, 15f };

    private sealed class LangState
    {
        public readonly char[] Out = new char[OutCap];
        public readonly TranslationChunk[] Pending = new TranslationChunk[PendingCap];
        public readonly PhonemeEvent[] Ph = new PhonemeEvent[PhonemeRing];
        public int OutPos, PHead, PCount;
        public long PhHead, PhCount;
        public double PhCursorMs, MaxLatencyMs, SumLatencyMs;
        public long Emitted;
        public float LastLatencyMs;
    }

    private readonly char[] _in = new char[InputCap];
    private readonly double[] _lineSubmit = new double[LineRing];
    private readonly LangState[] _lang = new LangState[Targets.Length];
    private readonly TranslationChunk[] _recent = new TranslationChunk[RecentCap];
    private int _inLen, _scan, _chunkStart = -1, _chunkEnd, _chunkWords, _recentHead, _recentN;
    private long _lineWrite, _lineRead, _seq;
    private bool _inLine;
    private double _wordEndMs;
    private int _active = 0b11;

    public RealtimeMultilingualTranslationPhonemeEngine()
    {
        for (var i = 0; i < _lang.Length; i++) _lang[i] = new LangState();
    }

    public ITranslationChunkSink? Sink { get; set; }
    public long Submitted { get; private set; }
    public long DroppedInput { get; private set; }
    public long Chunks => _seq;
    public long Words { get; private set; }
    public long UnknownWords { get; private set; }
    public long BudgetMisses { get; private set; }
    public long PhonemesExtracted { get; private set; }
    public double LastStepUs { get; private set; }
    public double MaxStepUs { get; private set; }
    public int PendingInputChars => _inLen - _scan;
    public bool Idle => _scan >= _inLen && _chunkStart < 0 && PendingTotal == 0;
    public int PendingTotal { get { var n = 0; foreach (var l in _lang) n += l.PCount; return n; } }

    /// <summary>번역 대상 언어 비트마스크(<see cref="Targets"/> 인덱스).</summary>
    public int ActiveMask { get => _active; set => _active = value & ((1 << Targets.Length) - 1); }
    public static int IndexOf(DubLanguage l) { for (var i = 0; i < Targets.Length; i++) if (Targets[i] == l) return i; return -1; }
    public bool IsActive(DubLanguage l) { var i = IndexOf(l); return i >= 0 && (_active & (1 << i)) != 0; }

    public double MaxLatencyMs(DubLanguage l) { var i = IndexOf(l); return i < 0 ? 0 : _lang[i].MaxLatencyMs; }
    public float LastLatencyMs(DubLanguage l) { var i = IndexOf(l); return i < 0 ? 0f : _lang[i].LastLatencyMs; }
    public double AvgLatencyMs(DubLanguage l) { var i = IndexOf(l); return i < 0 || _lang[i].Emitted == 0 ? 0 : _lang[i].SumLatencyMs / _lang[i].Emitted; }
    public long EmittedChunks(DubLanguage l) { var i = IndexOf(l); return i < 0 ? 0 : _lang[i].Emitted; }
    public double MaxLatencyAll { get { double m = 0; foreach (var l in _lang) if (l.MaxLatencyMs > m) m = l.MaxLatencyMs; return m; } }

    public int RecentCount => _recentN;
    public ref readonly TranslationChunk Recent(int i) => ref _recent[(_recentHead - 1 - i + 2 * RecentCap) % RecentCap];
    public ReadOnlySpan<char> Text(in TranslationChunk c) { var i = IndexOf(c.Lang); return i < 0 ? default : _lang[i].Out.AsSpan(c.Start, c.Len); }

    public long PhonemeCount(DubLanguage l) { var i = IndexOf(l); return i < 0 ? 0 : Math.Min(_lang[i].PhCount, PhonemeRing); }
    /// <summary>최근 음소(0 = 최신).</summary>
    public ref readonly PhonemeEvent Phoneme(DubLanguage l, int i) { var s = _lang[IndexOf(l)]; return ref s.Ph[(int)((s.PhHead - 1 - i + 2L * PhonemeRing) % PhonemeRing)]; }
    /// <summary>타임라인 시각(ms)의 예측 음소(링 역순 탐색 · 무할당).</summary>
    public LipPhoneme PredictAt(DubLanguage l, double ms)
    {
        var i = IndexOf(l); if (i < 0) return LipPhoneme.Rest;
        var s = _lang[i]; var n = (int)Math.Min(s.PhCount, PhonemeRing);
        for (var k = 0; k < n; k++)
        {
            ref readonly var e = ref s.Ph[(int)((s.PhHead - 1 - k + 2L * PhonemeRing) % PhonemeRing)];
            if (e.StartMs <= ms) return ms < e.StartMs + e.DurMs ? e.Ph : LipPhoneme.Rest;
        }
        return LipPhoneme.Rest;
    }

    public void Reset()
    {
        _inLen = _scan = _chunkEnd = _chunkWords = _recentHead = _recentN = 0; _chunkStart = -1;
        _lineWrite = _lineRead = _seq = 0; _inLine = false; _wordEndMs = 0;
        Submitted = DroppedInput = Words = UnknownWords = BudgetMisses = PhonemesExtracted = 0; MaxStepUs = 0;
        foreach (var l in _lang) { l.OutPos = l.PHead = l.PCount = 0; l.PhHead = l.PhCount = 0; l.PhCursorMs = l.MaxLatencyMs = l.SumLatencyMs = 0; l.Emitted = 0; l.LastLatencyMs = 0; }
    }

    public void ResetLatency() { foreach (var l in _lang) { l.MaxLatencyMs = 0; l.SumLatencyMs = 0; l.Emitted = 0; } BudgetMisses = 0; MaxStepUs = 0; }

    // ══════════════════════ 입력(호출 스레드 · 스팬 복사) ══════════════════════
    /// <summary>한국어 대사 1줄 적재(청취 시계 = max(직전 어절 종료, 적재 시각)). 링 포화 시 압축 후 거부.</summary>
    public bool Submit(ReadOnlySpan<char> korean, double nowMs)
    {
        korean = korean.Trim();
        if (korean.IsEmpty) return false;
        if (_lineWrite - _lineRead >= LineRing - 1) { DroppedInput++; return false; }
        if (_inLen + korean.Length + 1 > InputCap)
        {
            var keep = _chunkStart >= 0 ? _chunkStart : _scan;
            _in.AsSpan(keep, _inLen - keep).CopyTo(_in);
            _inLen -= keep; _scan -= keep; if (_chunkStart >= 0) { _chunkStart -= keep; _chunkEnd -= keep; }
            if (_inLen + korean.Length + 1 > InputCap) { DroppedInput++; return false; }
        }
        korean.CopyTo(_in.AsSpan(_inLen));
        _inLen += korean.Length;
        _in[_inLen++] = '\n';
        _lineSubmit[_lineWrite % LineRing] = nowMs;
        _lineWrite++;
        Submitted++;
        Sink?.OnSourceLine(korean);
        return true;
    }

    // ══════════════════════ 스트리밍 틱(무할당 핫패스) ══════════════════════
    public void Step(double nowMs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        using (ZeroGcRegionScope.Enter("Translate.Step"))
        {
            Listen(nowMs);
            EmitReady(nowMs);
        }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastStepUs = us; if (us > MaxStepUs) MaxStepUs = us;
    }

    private void Listen(double nowMs)
    {
        for (var w = 0; w < MaxWordsPerStep; w++)
        {
            if (!_inLine)
            {
                if (_lineRead >= _lineWrite || _scan >= _inLen) return;
                _wordEndMs = Math.Max(_wordEndMs, _lineSubmit[_lineRead % LineRing]);
                _inLine = true;
            }
            while (_scan < _inLen && _in[_scan] == ' ') _scan++;
            if (_scan >= _inLen) return;
            if (_in[_scan] == '\n')
            {
                if (_chunkStart >= 0) CloseChunk(_wordEndMs);
                _scan++; _lineRead++; _inLine = false;
                continue;
            }
            var we = _scan;
            while (we < _inLen && _in[we] != ' ' && _in[we] != '\n') we++;
            var word = _in.AsSpan(_scan, we - _scan);
            var end = _wordEndMs + RealtimeNeuralVoiceCloningDubbingEngine.Plan(DubLanguage.Ko, word) * 1000.0 / RealtimeNeuralVoiceCloningDubbingEngine.Fs;
            if (end > nowMs) return;   // 아직 발화 중(청취 전)
            _wordEndMs = end;
            if (_chunkStart < 0) { _chunkStart = _scan; _chunkWords = 0; }
            _chunkEnd = we; _chunkWords++; Words++;
            _scan = we;
            var last = word[^1];
            if (_chunkWords >= ChunkWords || last is ',' or '.' or '!' or '?' or '~') CloseChunk(end);
        }
    }

    private void CloseChunk(double closeMs)
    {
        var src = _in.AsSpan(_chunkStart, _chunkEnd - _chunkStart);
        var words = _chunkWords;
        _chunkStart = -1; _chunkWords = 0;
        for (var li = 0; li < _lang.Length; li++)
        {
            if ((_active & (1 << li)) == 0) continue;
            var s = _lang[li];
            if (s.PCount >= PendingCap) { BudgetMisses++; continue; }
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (s.OutPos + src.Length * 6 + 16 > OutCap) s.OutPos = 0;
            var n = Translate(li, src, s.Out.AsSpan(s.OutPos), out var unk);
            UnknownWords += unk;
            ref var c = ref s.Pending[(s.PHead + s.PCount) % PendingCap];
            c.Seq = ++_seq; c.Lang = Targets[li]; c.Start = s.OutPos; c.Len = n; c.Words = words; c.Unknown = unk;
            c.CloseMs = closeMs; c.ReadyMs = closeMs + ModelBaseMs[li] + ModelWordMs[li] * words; c.EmitMs = 0; c.LatencyMs = 0;
            c.CpuUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            s.OutPos += n;
            s.PCount++;
        }
    }

    private void EmitReady(double nowMs)
    {
        for (var li = 0; li < _lang.Length; li++)
        {
            var s = _lang[li];
            while (s.PCount > 0 && s.Pending[s.PHead].ReadyMs <= nowMs)
            {
                ref var c = ref s.Pending[s.PHead];
                c.EmitMs = nowMs; c.LatencyMs = nowMs - c.CloseMs;
                s.LastLatencyMs = (float)c.LatencyMs; s.SumLatencyMs += c.LatencyMs; s.Emitted++;
                if (c.LatencyMs > s.MaxLatencyMs) s.MaxLatencyMs = c.LatencyMs;
                if (c.LatencyMs > BudgetMs) BudgetMisses++;
                var text = s.Out.AsSpan(c.Start, c.Len);
                Extract(li, s, text, nowMs, c.Seq);
                _recent[_recentHead] = c; _recentHead = (_recentHead + 1) % RecentCap; if (_recentN < RecentCap) _recentN++;
                Sink?.OnChunk(in c, text);
                s.PHead = (s.PHead + 1) % PendingCap; s.PCount--;
            }
        }
    }

    // ══════════════════════ 번역(구 우선 최장 일치 · 음역 폴백 · 무할당) ══════════════════════
    /// <summary>한국어 스팬 → 대상 언어(<see cref="Targets"/> 인덱스) 출력 스팬. 반환 = 기록 문자 수.</summary>
    public static int Translate(int lang, ReadOnlySpan<char> src, Span<char> dst, out int unknown)
    {
        unknown = 0;
        var n = 0; var i = 0;
        while (i < src.Length)
        {
            if (src[i] is ' ' or '\n') { i++; continue; }
            var rest = src[i..];
            var best = -1; var bestLen = 0;
            for (var k = 0; k < Lexicon.Length; k++)
            {
                var key = Lexicon[k][0];
                if (key.Length > bestLen && rest.StartsWith(key, StringComparison.Ordinal)) { best = k; bestLen = key.Length; }
            }
            var wordEnd = i + (best >= 0 ? bestLen : 0);
            while (wordEnd < src.Length && src[wordEnd] is not (' ' or '\n')) wordEnd++;
            var punct = PunctOf(src[wordEnd - 1]);
            if (best >= 0)
            {
                var t = Lexicon[best][1 + lang];
                if (t.Length > 0) n = Append(dst, n, t);
            }
            else
            {
                var w = src[i..wordEnd];
                var start = n;
                if (n > 0 && n < dst.Length) dst[n++] = ' ';
                var any = false;
                foreach (var ch in w)
                {
                    if (ch >= '가' && ch <= '힣')
                    {
                        var s = ch - 0xAC00;
                        n = Raw(dst, n, RrCho[s / (21 * 28)]); n = Raw(dst, n, RrJung[s / 28 % 21]); n = Raw(dst, n, RrJong[s % 28]);
                        any = true;
                    }
                    else if (char.IsAsciiLetterOrDigit(ch) && n < dst.Length) { dst[n++] = char.ToLowerInvariant(ch); any = true; }
                }
                if (any) unknown++; else n = start;
            }
            if (punct != '\0' && n > 0 && n < dst.Length) dst[n++] = punct;
            i = wordEnd;
        }
        return n;
    }

    private static char PunctOf(char c) => c is ',' or '.' or '!' or '?' ? c : c == '~' ? '!' : '\0';

    private static int Append(Span<char> dst, int n, string t)
    {
        if (n > 0 && n < dst.Length) dst[n++] = ' ';
        return Raw(dst, n, t);
    }

    private static int Raw(Span<char> dst, int n, string t)
    {
        var k = Math.Min(t.Length, dst.Length - n);
        t.AsSpan(0, k).CopyTo(dst[n..]);
        return n + k;
    }

    // ══════════════════════ G2P 음소 추출(언어 운율 · 링 기록) ══════════════════════
    private void Extract(int li, LangState s, ReadOnlySpan<char> text, double nowMs, long chunk)
    {
        var t = Math.Max(s.PhCursorMs, nowMs);
        float v = VowelMs[li], cns = ConsMs[li], pause = PauseMs[li];
        for (var i = 0; i < text.Length; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            var next = i + 1 < text.Length ? char.ToLowerInvariant(text[i + 1]) : ' ';
            LipPhoneme ph; var dur = cns;
            switch (c)
            {
                case 'a': ph = LipPhoneme.A; dur = v; break;
                case 'e': ph = LipPhoneme.E; dur = v; break;
                case 'i' or 'y': ph = LipPhoneme.I; dur = v; break;
                case 'o': ph = LipPhoneme.O; dur = v; break;
                case 'u': ph = LipPhoneme.U; dur = v; break;
                case 'b' or 'm' or 'p': ph = c == 'p' && next == 'h' ? LipPhoneme.Fv : LipPhoneme.Pbm; if (ph == LipPhoneme.Fv) i++; break;
                case 'f' or 'v': ph = LipPhoneme.Fv; break;
                case 't' when next == 'h': ph = LipPhoneme.Th; i++; break;
                case 's' or 'c' when next == 'h': ph = LipPhoneme.Sz; i++; break;
                case 'c' or 'k' or 'g' or 'q' or 'x': ph = LipPhoneme.Kg; break;
                case 't' or 'd': ph = LipPhoneme.Td; break;
                case 's' or 'z' or 'j': ph = LipPhoneme.Sz; break;
                case 'l': ph = LipPhoneme.L; break;
                case 'r': ph = LipPhoneme.R; break;
                case 'w': ph = LipPhoneme.W; break;
                case 'n': ph = LipPhoneme.N; if (next == 'g') i++; break;
                case ',' or '.' or '!' or '?': ph = LipPhoneme.Rest; dur = pause; break;
                default: continue;
            }
            ref var e = ref s.Ph[(int)(s.PhHead % PhonemeRing)];
            e.Ph = ph; e.DurMs = dur; e.StartMs = t; e.Chunk = chunk;
            s.PhHead++; s.PhCount++; PhonemesExtracted++;
            t += dur;
        }
        s.PhCursorMs = t;
    }
}
