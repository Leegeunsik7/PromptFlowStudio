using PromptFlowStudio.Services.StationMaster;

namespace PromptFlowStudio.Services.Publisher;

public enum ProductCategory : byte { Beauty, Tech, Fashion, Food, Lifestyle }

/// <summary>v17.71.0 숏폼 콘텐츠 분석 입력(릴 핵심 대사 · v17.67 물리 로그 · 클라이맥스 원인 · 커머스 상품).</summary>
public sealed class ShortformContext
{
    public string KeyLine { get; set; } = "";
    public string Caption { get; set; } = "";
    public string? Product { get; set; }
    public ProductCategory Category { get; set; } = ProductCategory.Lifestyle;
    public StationProgramKind Program { get; set; } = StationProgramKind.Talkshow;
    /// <summary>0 = 채팅 폭발 · 1 = 물리 피격 · 2 = 구매 폭주(v17.69 ClimaxCause).</summary>
    public int Cause { get; set; }
    public float Peak { get; set; } = 85f;
    public long Hits { get; set; }
    public long HeadHits { get; set; }
    public long Confetti { get; set; }
    public long UnitsSold { get; set; }
    public double DurationSec { get; set; } = 20;
}

/// <summary>v17.71.0 LLM 토큰 수신(스트리밍 · 무할당 스팬).</summary>
public interface IContentTokenSink
{
    void OnToken(ReadOnlySpan<char> token);
}

/// <summary>v17.71.0 콘텐츠 LLM(외부 · 로컬) — 프롬프트 → 토큰 스트리밍. 형식: <c>TITLE: …\nDESC: …\nTAGS: #a #b #c #d #e\n</c>.</summary>
public interface IContentLlmProvider
{
    Task StreamAsync(ReadOnlyMemory<char> prompt, IContentTokenSink sink, CancellationToken ct);
}

/// <summary>v17.71.0 다음 24 h 편성 추천 1 건.</summary>
public struct EpgRecommendation
{
    public int StartHour;
    public StationProgramKind Kind;
    public ProductCategory Category;
    public float Score;
    public short Theme;
}

/// <summary>
/// v17.71.0 로컬 휴리스틱 LLM(외부 LLM 부재 · 1 s 타임아웃 폴백 · 결정적) — 문맥 키워드 · 원인 · 상품으로 제목/설명/해시태그를 생성해 8 자 단위로 스트리밍.
/// </summary>
public sealed class LocalHeuristicContentLlm : IContentLlmProvider
{
    private readonly char[] _out = new char[1024];
    private ShortformContext? _ctx;
    public int ChunkChars { get; set; } = 8;
    public int SimulatedLatencyMs { get; set; }
    public ShortformContext? Context { get => _ctx; set => _ctx = value; }

    public static readonly string[][] CauseHooks =
    {
        new[] { "채팅창 폭발", "실시간 반응 미쳤다", "시청자 난리 난" },
        new[] { "직격 리액션 레전드", "피할 수 없었던 순간", "물리엔진 대참사" },
        new[] { "품절 대란", "주문 폭주 현장", "3분 만에 완판" },
    };

    public static readonly string[] CategoryTags = { "#뷰티", "#테크", "#패션", "#먹방", "#라이프스타일" };
    public static readonly string[] ProgramTags = { "#토크쇼", "#라이브커머스", "#리듬액션", "#릴스리뷰", "#ASMR" };

    public async Task StreamAsync(ReadOnlyMemory<char> prompt, IContentTokenSink sink, CancellationToken ct)
    {
        var c = _ctx ?? new ShortformContext();
        var n = Compose(c, _out);
        for (var i = 0; i < n; i += ChunkChars)
        {
            ct.ThrowIfCancellationRequested();
            if (SimulatedLatencyMs > 0) await Task.Delay(SimulatedLatencyMs, ct).ConfigureAwait(false);
            sink.OnToken(_out.AsSpan(i, Math.Min(ChunkChars, n - i)));
        }
    }

    /// <summary>결정적 조합(무할당 · dst 기록 길이 반환).</summary>
    public static int Compose(ShortformContext c, Span<char> dst)
    {
        var n = 0;
        var hooks = CauseHooks[Math.Clamp(c.Cause, 0, 2)];
        var h = hooks[(int)(Math.Abs(c.Peak * 7 + c.Hits) % hooks.Length)];
        Put(dst, ref n, "TITLE: ");
        if (c.Product is { Length: > 0 } p && c.Cause == 2) { Put(dst, ref n, p); Put(dst, ref n, " "); }
        Put(dst, ref n, h);
        if (c.HeadHits > 0) Put(dst, ref n, " 헤드샷까지");
        Put(dst, ref n, c.Peak >= 95 ? " 🔥🔥🔥" : " 🔥");
        Put(dst, ref n, " #Shorts\nDESC: ");
        var line = c.KeyLine.Length > 0 ? c.KeyLine : c.Caption;
        if (line.Length > 0) { Put(dst, ref n, "\""); Put(dst, ref n, line.AsSpan(0, Math.Min(line.Length, 80))); Put(dst, ref n, "\" "); }
        Put(dst, ref n, "라이브 방송 바이럴 지수 ");
        n += Fmt(dst[n..], (int)c.Peak);
        Put(dst, ref n, "점을 찍은 하이라이트! ");
        if (c.Hits > 0) { Put(dst, ref n, "시청자 물리 개입 "); n += Fmt(dst[n..], (int)c.Hits); Put(dst, ref n, "회 "); }
        if (c.UnitsSold > 0) { Put(dst, ref n, "실시간 판매 "); n += Fmt(dst[n..], (int)c.UnitsSold); Put(dst, ref n, "개 "); }
        Put(dst, ref n, "전체 방송은 채널에서 확인하세요.\nTAGS: #쇼츠 ");
        Put(dst, ref n, ProgramTags[(int)c.Program]); Put(dst, ref n, " ");
        Put(dst, ref n, CategoryTags[(int)c.Category]); Put(dst, ref n, " ");
        Put(dst, ref n, c.Cause == 1 ? "#물리엔진" : c.Cause == 2 ? "#완판" : "#실시간반응"); Put(dst, ref n, " #버추얼휴먼\n");
        return n;
    }

    private static void Put(Span<char> d, ref int n, ReadOnlySpan<char> s) { var k = Math.Min(s.Length, d.Length - n); s[..k].CopyTo(d[n..]); n += k; }
    private static int Fmt(Span<char> d, int v) => v.TryFormat(d, out var w, default, System.Globalization.CultureInfo.InvariantCulture) ? w : 0;
}

/// <summary>
/// [PromptFlow Studio v17.71.0] 🤖 AI 숏폼 콘텐츠 추천 &amp; 제목/해시태그 자동 생성기(무할당 스트리밍 파서) + 다음 24 h 편성 선제 추천.
///   ▸ 프롬프트: 핵심 대사 · 캡션 · v17.67 물리 로그(피격/헤드샷/컨페티) · 판매 수량 · 클라이맥스 원인/피크 → 재사용 char 버퍼(4 KB) 조립.
///   ▸ 스트리밍: 토큰 → 줄 단위 상태 기계(TITLE/DESC/TAGS) → 고정 버퍼(제목 100 · 설명 900 · 태그 5 × 30) 직접 기록 — 토큰 경계가 키워드/줄을 쪼개도 정합.
///   ▸ 정규화 가드: 제목 ≤ 100(YouTube) · 설명+태그 ≤ 2200(IG) · 해시태그 정확히 5(# 접두 · 공백/특수문자 제거 · 중복 제거 · 부족분 문맥 보충) · #Shorts 보장.
///   ▸ 1 s 타임아웃/예외 → <see cref="LocalHeuristicContentLlm"/> 즉시 폴백([부분 구제]: 이미 받은 제목은 유지).
///   ▸ 편성 추천: (프로그램 × 상품 카테고리) 성과 사후 평균(릴 성공 계수 · 망각) × 시간대 트래픽 곡선 → 상위 3 슬롯.
/// </summary>
public sealed class AiContentTrendRecommendationSynthesizer : IContentTokenSink
{
    public const int PromptCap = 4096, TitleCap = 100, DescCap = 900, TagCap = 30, Tags = 5, LineCap = 1024, Kinds = 5, Categories = 5;
    public static readonly string[] Themes = { "아침 수다 라이브", "신상 언박싱 특가", "주말 비트 페스티벌", "주간 바이럴 결산", "심야 힐링 라운지", "팬 Q&A 스페셜" };

    private readonly char[] _prompt = new char[PromptCap], _title = new char[TitleCap], _titleSave = new char[TitleCap], _desc = new char[DescCap], _line = new char[LineCap];
    private readonly char[][] _tags = { new char[TagCap], new char[TagCap], new char[TagCap], new char[TagCap], new char[TagCap] };
    private readonly int[] _tagLen = new int[Tags];
    private readonly float[] _a = new float[Kinds * Categories], _b = new float[Kinds * Categories];
    private readonly EpgRecommendation[] _recs = new EpgRecommendation[3];
    private int _promptLen, _titleLen, _descLen, _tagN, _lineLen, _section;
    private bool _titleLocked, _prefixed;

    public AiContentTrendRecommendationSynthesizer()
    {
        Array.Fill(_a, 1f); Array.Fill(_b, 1f);
        Fallback = new LocalHeuristicContentLlm();
    }

    public IContentLlmProvider? Llm { get; set; }
    public LocalHeuristicContentLlm Fallback { get; }
    public int TimeoutMs { get; set; } = 1_000;
    public long Generations { get; private set; }
    public long Fallbacks { get; private set; }
    public long Tokens { get; private set; }
    public long Normalizations { get; private set; }
    public double LastGenerateMs { get; private set; }
    public double LastTokenUs { get; private set; }
    public double MaxTokenUs { get; private set; }
    public ReadOnlySpan<char> Prompt => _prompt.AsSpan(0, _promptLen);
    public ReadOnlySpan<char> Title => _title.AsSpan(0, _titleLen);
    public ReadOnlySpan<char> Description => _desc.AsSpan(0, _descLen);
    public int TagCount => _tagN;
    public ReadOnlySpan<char> Tag(int i) => _tags[i].AsSpan(0, _tagLen[i]);
    public int RecommendationCount { get; private set; }
    public ref readonly EpgRecommendation Recommendation(int i) => ref _recs[i];
    public float Posterior(StationProgramKind k, ProductCategory c) { var i = (int)k * Categories + (int)c; return _a[i] / (_a[i] + _b[i]); }

    public static string CategoryName(ProductCategory c) => c switch
    {
        ProductCategory.Beauty => "뷰티", ProductCategory.Tech => "테크", ProductCategory.Fashion => "패션", ProductCategory.Food => "푸드", _ => "라이프스타일",
    };

    // ══════════════════════ 프롬프트(무할당) ══════════════════════
    public int BuildPrompt(ShortformContext c)
    {
        var n = 0; var d = _prompt.AsSpan();
        P(d, ref n, "당신은 한국어 숏폼 바이럴 카피라이터입니다. 아래 라이브 하이라이트로 YouTube Shorts/Instagram Reels 메타데이터를 작성하세요.\n");
        P(d, ref n, "형식(정확히 3줄): TITLE: 100자 이내 클릭 유도 제목(#Shorts 포함)\nDESC: 2~3문장 요약\nTAGS: 해시태그 정확히 5개(#으로 시작, 공백 구분)\n\n[하이라이트]\n");
        P(d, ref n, "- 프로그램: "); P(d, ref n, MasterStationEpgSchedulerEngine.KindName(c.Program)); P(d, ref n, "\n- 클라이맥스: ");
        P(d, ref n, c.Cause == 1 ? "물리 피격" : c.Cause == 2 ? "구매 폭주" : "채팅 폭발"); P(d, ref n, " · 바이럴 지수 "); I(d, ref n, (int)c.Peak);
        P(d, ref n, "\n- 핵심 대사: "); P(d, ref n, c.KeyLine.AsSpan(0, Math.Min(c.KeyLine.Length, 300)));
        P(d, ref n, "\n- 자막: "); P(d, ref n, c.Caption.AsSpan(0, Math.Min(c.Caption.Length, 200)));
        P(d, ref n, "\n- 물리 로그: 피격 "); I(d, ref n, (int)c.Hits); P(d, ref n, "회 · 헤드샷 "); I(d, ref n, (int)c.HeadHits); P(d, ref n, "회 · 컨페티 "); I(d, ref n, (int)c.Confetti); P(d, ref n, "회");
        if (c.Product is { Length: > 0 } pr) { P(d, ref n, "\n- 상품: "); P(d, ref n, pr); P(d, ref n, " ("); P(d, ref n, CategoryName(c.Category)); P(d, ref n, ") · 판매 "); I(d, ref n, (int)c.UnitsSold); P(d, ref n, "개"); }
        P(d, ref n, "\n- 길이: "); I(d, ref n, (int)c.DurationSec); P(d, ref n, "초\n");
        _promptLen = n;
        return n;
    }

    private static void P(Span<char> d, ref int n, ReadOnlySpan<char> s) { var k = Math.Min(s.Length, d.Length - n); s[..k].CopyTo(d[n..]); n += k; }
    private static void I(Span<char> d, ref int n, int v) { if (v.TryFormat(d[n..], out var w, default, System.Globalization.CultureInfo.InvariantCulture)) n += w; }

    // ══════════════════════ 스트리밍 파서(무할당) ══════════════════════
    public void Reset() { _titleLen = _descLen = _tagN = _lineLen = 0; _section = 0; _titleLocked = _prefixed = false; Array.Clear(_tagLen); }

    public void OnToken(ReadOnlySpan<char> token)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Tokens++;
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (ch == '\r') continue;
            if (ch == '\n') { FlushLine(); continue; }
            if (_lineLen < LineCap) _line[_lineLen++] = ch;
            // 줄 접두 판정 이후 섹션 본문은 즉시 반영(스트리밍 미리보기)
            if (_lineLen == 6 || _lineLen == 5) Classify();
        }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastTokenUs = us; if (us > MaxTokenUs && Tokens > 3) MaxTokenUs = us;
    }

    private void Classify()
    {
        ReadOnlySpan<char> l = _line.AsSpan(0, _lineLen);
        if (l.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase)) { _section = 1; _prefixed = true; }
        else if (l.StartsWith("DESC:", StringComparison.OrdinalIgnoreCase)) { _section = 2; _prefixed = true; }
        else if (l.StartsWith("TAGS:", StringComparison.OrdinalIgnoreCase)) { _section = 3; _prefixed = true; }
    }

    private void FlushLine()
    {
        Classify();
        var l = _line.AsSpan(0, _lineLen);
        var body = _prefixed ? l[(l.IndexOf(':') + 1)..].Trim() : l.Trim();   // 접두 없는 줄 = 직전 섹션 이어쓰기
        switch (_section)
        {
            case 1: if (!_titleLocked) { _titleLen = Copy(body, _title); _titleLocked = _titleLen > 0; } break;
            case 2: if (body.IsEmpty) break; if (_descLen > 0 && _descLen < DescCap) _desc[_descLen++] = ' '; _descLen += Copy(body, _desc.AsSpan(_descLen)); break;
            case 3: AddTags(body); break;
        }
        _lineLen = 0; _prefixed = false;
    }

    private static int Copy(ReadOnlySpan<char> s, Span<char> d) { var k = Math.Min(s.Length, d.Length); s[..k].CopyTo(d); return k; }

    private void AddTags(ReadOnlySpan<char> s)
    {
        while (!s.IsEmpty && _tagN < Tags)
        {
            s = s.TrimStart(" ,\t");
            var e = s.IndexOfAny(" ,\t");
            var w = e < 0 ? s : s[..e];
            s = e < 0 ? default : s[e..];
            AddTag(w);
        }
    }

    /// <summary>해시태그 정규화(# 접두 · 문자/숫자/_ 만 · 30 자 · 대소문자 무시 중복 제거).</summary>
    public bool AddTag(ReadOnlySpan<char> w)
    {
        if (_tagN >= Tags) return false;
        var dst = _tags[_tagN]; var n = 0;
        dst[n++] = '#';
        for (var i = 0; i < w.Length && n < TagCap; i++) { var c = w[i]; if (char.IsLetterOrDigit(c) || c == '_') dst[n++] = c; }
        if (n <= 1) return false;
        for (var k = 0; k < _tagN; k++) if (Tag(k).Equals(dst.AsSpan(0, n), StringComparison.OrdinalIgnoreCase)) return false;
        _tagLen[_tagN++] = n;
        return true;
    }

    // ══════════════════════ 생성(LLM → 폴백 → 정규화) ══════════════════════
    public async Task<bool> GenerateAsync(ShortformContext c, CancellationToken ct = default)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        BuildPrompt(c);
        Reset();
        var ok = false;
        if (Llm is { } llm)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeoutMs);
            try { await llm.StreamAsync(_prompt.AsMemory(0, _promptLen), this, cts.Token).ConfigureAwait(false); FlushPending(); ok = _titleLen > 0 && _tagN > 0; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { FlushPending(); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or FormatException) { PromptFlowStudio.Program.Log("ContentLlm", ex); FlushPending(); }
        }
        if (!ok)
        {
            Fallbacks++;
            var savedLen = _titleLen;   // [부분 구제] 받은 제목 유지(LLM 이 제목만 보내고 지연된 경우)
            if (savedLen > 0) _title.AsSpan(0, savedLen).CopyTo(_titleSave);
            Reset();
            Fallback.Context = c;
            await Fallback.StreamAsync(_prompt.AsMemory(0, _promptLen), this, ct).ConfigureAwait(false);
            FlushPending();
            if (savedLen > 0) { _titleSave.AsSpan(0, savedLen).CopyTo(_title); _titleLen = savedLen; }
        }
        Normalize(c);
        Generations++;
        LastGenerateMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        return true;
    }

    private void FlushPending() { if (_lineLen > 0) FlushLine(); }

    /// <summary>플랫폼 정책 정규화 — 제목 #Shorts · 100 자 · 태그 5 개 보충 · 설명 길이(IG 2200 − 태그).</summary>
    public void Normalize(ShortformContext c)
    {
        if (_titleLen == 0) { var k = Copy(c.Caption.Length > 0 ? c.Caption : "라이브 하이라이트", _title); _titleLen = k; Normalizations++; }
        if (((ReadOnlySpan<char>)_title.AsSpan(0, _titleLen)).IndexOf("#Shorts", StringComparison.OrdinalIgnoreCase) < 0)
        {
            const string S = " #Shorts";
            if (_titleLen + S.Length > TitleCap) _titleLen = TitleCap - S.Length;
            S.AsSpan().CopyTo(_title.AsSpan(_titleLen)); _titleLen += S.Length; Normalizations++;
        }
        if (_tagN < Tags)
        {
            Normalizations++;
            AddTag("쇼츠"); AddTag(LocalHeuristicContentLlm.ProgramTags[(int)c.Program]); AddTag(LocalHeuristicContentLlm.CategoryTags[(int)c.Category]);
            AddTag("버추얼휴먼"); AddTag("PromptFlow"); AddTag("바이럴"); AddTag("라이브");
        }
        var tagChars = 0; for (var i = 0; i < _tagN; i++) tagChars += _tagLen[i] + 1;
        var maxDesc = Math.Min(DescCap, 2200 - _titleLen - 2 - tagChars);
        if (_descLen > maxDesc) { _descLen = maxDesc; Normalizations++; }
    }

    /// <summary>업로드 메타데이터로 내보내기(제목 · 설명 · 태그 문자열 생성 = 비핫패스).</summary>
    public void ExportTo(SocialPostMetadata m)
    {
        m.Title = new string(Title); m.Description = new string(Description);
        m.TagCount = _tagN;
        for (var i = 0; i < _tagN; i++) m.Tags[i] = new string(Tag(i));
    }

    // ══════════════════════ 편성 추천(무할당) ══════════════════════
    /// <summary>릴 성과 귀속(프로그램 × 카테고리 · 성공 계수 0~1 · 망각 0.98).</summary>
    public void RecordOutcome(StationProgramKind k, ProductCategory c, float success)
    {
        for (var i = 0; i < _a.Length; i++) { _a[i] = 1f + (_a[i] - 1f) * 0.98f; _b[i] = 1f + (_b[i] - 1f) * 0.98f; }
        var j = (int)k * Categories + (int)c;
        var s = Math.Clamp(success, 0f, 1f);
        _a[j] += s; _b[j] += 1f - s;
    }

    /// <summary>시간대 트래픽(v17.70 일주기 모델과 동일 곡선 · 0~1).</summary>
    public static float HourTraffic(int hour)
    {
        var m = ((hour % 24) + 24) % 24 * 60 + 30;
        return 0.12f + 0.78f * MathF.Max(0f, MathF.Sin(MathF.PI * ((m - 360 + (m < 360 ? 1440 : 0)) % 1440) / 1080f));
    }

    /// <summary>다음 24 h 상위 3 편성 추천 — 점수 = 사후 평균 × (0.4 + 0.6·트래픽) · 앰비언트는 저트래픽 시간대만 · 시간대/종류 중복 회피.</summary>
    public int Recommend(int nowHour)
    {
        RecommendationCount = 0;
        for (var r = 0; r < 3; r++)
        {
            var best = new EpgRecommendation { Score = -1f };
            for (var dh = 1; dh <= 24; dh++)
            {
                var h = (nowHour + dh) % 24;
                var tr = HourTraffic(h);
                for (var k = 0; k < Kinds; k++)
                {
                    if (k == (int)StationProgramKind.Ambient && tr > 0.3f) continue;
                    for (var c = 0; c < Categories; c++)
                    {
                        var score = Posterior((StationProgramKind)k, (ProductCategory)c) * (0.4f + 0.6f * tr);
                        var dup = false;
                        for (var q = 0; q < RecommendationCount; q++) if (Math.Abs(_recs[q].StartHour - h) < 3 || _recs[q].Kind == (StationProgramKind)k && _recs[q].Category == (ProductCategory)c) dup = true;
                        if (dup || score <= best.Score) continue;
                        best = new EpgRecommendation { StartHour = h, Kind = (StationProgramKind)k, Category = (ProductCategory)c, Score = score, Theme = (short)((k + c + h / 6) % Themes.Length) };
                    }
                }
            }
            if (best.Score < 0) break;
            _recs[RecommendationCount++] = best;
        }
        return RecommendationCount;
    }
}
