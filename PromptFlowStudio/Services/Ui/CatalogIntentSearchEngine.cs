using System.Numerics;
using System.Text;

namespace PromptFlowStudio.Services.Ui;

/// <summary>인텐트 검색 결과 1건 — 카탈로그 인덱스 · 원점수 · 적합도(%) · 일치 개념 비트 · 제목 직접 일치 여부.</summary>
public readonly record struct IntentHit(int EntryIndex, int Score, int FitPercent, ulong ConceptMask, bool TitleHit)
{
    public StudioEntry Entry => StudioCatalog.Entries[EntryIndex];
}

/// <summary>
/// [PromptFlow Studio v17.89.0] 🧭 무할당 자연어 인텐트 검색 엔진 — "유튜브 라이브하고 싶어" · "쇼츠 자막 달기" · "조명 어둡게" 같은
/// 목적 문장을 공백/구두점 스팬 토크나이징 → 동의어 개념(≤64, ulong 비트) 사상 → 카탈로그 165 항목(제목 · 역할 요약 · 인텐트 키워드)과
/// 스팬 IndexOf 로 채점해 상위 3~5 개를 호출부 <see cref="Span{T}"/> 에 채운다. 검색 경로는 stackalloc 만 쓴다(힙 할당 0 · 1 ms 미만).
/// 한국어 어미("라이브<b>하고</b>", "어둡<b>게</b>")는 토큰 접두를 2 자까지 줄여 가며 가장 긴 일치를 쓴다.
/// </summary>
public static class CatalogIntentSearchEngine
{
    public const int MaxHits = 5;
    private const int MinPrefix = 2;
    private const int MaxQuery = 256;

    // 개념 동의어 사전 — [0] = 표시명(추천 사유 태그), 나머지 = 동의어(라틴 소문자 · 대소문자 무관 비교).
    // <concepts>
    private static readonly string[][] Concepts =
    {
        new[] { "자막", "자막", "캡션", "호르모지", "subtitle", "caption", "타이포", "typography", "텍스트 오버레이" },
        new[] { "송출", "송출", "스트리밍", "stream", "rtmp", "rtmps", "srt", "게이트웨이", "인제스트", "라이브", "생방송", "온에어" },
        new[] { "아바타", "얼굴", "아바타", "립싱크", "lipsync", "페이셜", "facial", "표정", "디지털 휴먼", "버추얼", "v-tuber", "vtuber" },
        new[] { "조명", "조명", "라이팅", "lighting", "릴라이팅", "dmx", "밝기", "어둡", "밝게", "섀도우", "그림자", "광원" },
        new[] { "더빙", "더빙", "dub", "번역", "다국어", "로컬라이제이션", "localization", "tts", "외국어" },
        new[] { "플랫폼", "유튜브", "youtube", "트위치", "twitch", "치지직", "인스타", "instagram", "틱톡", "tiktok", "페이스북" },
        new[] { "숏폼", "숏폼", "쇼츠", "shorts", "릴스", "reels", "클립", "clip", "9:16", "세로 영상" },
        new[] { "커머스", "커머스", "쇼핑", "commerce", "상품", "판매", "ppl", "스폰서", "구매", "라이브 커머스" },
        new[] { "3DGS", "3dgs", "가우시안", "gaussian", "스플랫", "splat", "nerf", "볼류메트릭" },
        new[] { "카메라", "카메라", "camera", "렌즈", "lens", "구도", "시네마토", "트래킹", "프레이밍", "앵글" },
        new[] { "모션", "모션", "모캡", "mocap", "캡처", "포즈", "보행", "골격", "래그돌", "로코모션" },
        new[] { "오디오", "음악", "bgm", "비트", "beat", "리듬", "오디오", "audio", "사운드", "음향", "폴리", "믹싱", "더킹" },
        new[] { "VFX", "vfx", "파이로", "불꽃", "폭발", "물리", "날씨", "파티클", "유체", "이펙트", "효과" },
        new[] { "MCR", "mcr", "주조정실", "편성", "epg", "방송국", "스위처", "24/7", "마스터 컨트롤" },
        new[] { "채팅", "채팅", "chat", "시청자", "댓글", "인터랙션", "참여", "투표", "후원", "도네이션" },
        new[] { "바이럴", "바이럴", "viral", "조회수", "ctr", "성과", "트렌드", "예측", "알고리즘", "분석" },
        new[] { "편집", "편집", "타임라인", "timeline", "nle", "리퍼포징", "요약", "챕터", "컷 편집" },
        new[] { "각본", "각본", "대본", "스크립트", "script", "시나리오", "스토리", "내러티브" },
        new[] { "의상", "의상", "옷", "클로스", "cloth", "헤어", "머리카락", "hair", "드레이핑", "패션" },
        new[] { "색보정", "색보정", "컬러", "color", "aces", "lut", "그레이딩", "dit", "hdr" },
        new[] { "가상 프로덕션", "led", "icvfx", "버추얼 프로덕션", "가상 프로덕션", "크로마", "그린스크린", "세트 확장", "언리얼", "ue5", "가상 스튜디오", "가상스튜디오" },
        new[] { "화질", "업스케일", "8k", "4k", "화질", "디노이즈", "디노이저", "디플리커", "보간", "슬로모션" },
        new[] { "보안", "보안", "저작권", "워터마크", "drm", "c2pa", "암호", "인증", "위변조" },
        new[] { "인프라", "클라우드", "cloud", "gpu", "클러스터", "렌더 팜", "렌더팜", "cdn", "에지", "분산", "sla", "서버" },
        new[] { "아카이브", "녹화", "아카이브", "archive", "백업", "vod", "다시보기", "레코더" },
        new[] { "XR", "xr", "vr", "webxr", "vision pro", "비전 프로", "공간 비디오", "홀로그램", "홀로그래픽", "메타버스" },
        new[] { "접근성", "접근성", "수어", "배리어프리", "a11y", "청각", "시각 장애" },
        new[] { "생성 AI", "디퓨전", "diffusion", "프롬프트", "생성형", "이미지 생성", "영상 생성", "제너레이티브" },
        new[] { "QA", "qa", "테스트", "검증", "디버깅", "모니터링", "텔레메트리", "관제", "진단" },
        new[] { "토크쇼", "토크쇼", "게스트", "대담", "패널", "팟캐스트", "인터뷰" },
        new[] { "음성", "음성", "목소리", "보이스", "voice", "발화", "마이크", "성우" },
        new[] { "손 · 소품", "손가락", "핑거", "소품", "파지", "그랩", "양손" },
        new[] { "무대", "무대", "스테이지", "stage", "콘서트", "공연", "mv", "뮤직비디오" },
        new[] { "교육", "교육", "강의", "에듀", "학습", "튜토리얼" },
        new[] { "아티클", "블로그", "뉴스레터", "아티클", "seo", "카드뉴스", "og 배너" },
        new[] { "썸네일", "썸네일", "thumbnail", "배너", "포스터", "커버" },
        new[] { "게임", "게임", "아레나", "e스포츠", "넷코드", "멀티플레이" },
        new[] { "도움말", "도움말", "가이드", "사용법", "메뉴 찾기", "명령", "단축키", "help" },
    };
    // </concepts>

    // 의도 문장에 흔한 기능어 — 일치 판정에서 뺀다(무할당 스팬 비교).
    private static readonly string[] StopWords =
    {
        "싶어", "싶다", "싶은데", "하고", "하기", "해줘", "해주세요", "하는", "하려면", "어떻게", "방법", "좀", "그리고", "에서", "으로", "및", "스튜디오", "studio", "ai", "the", "a", "to", "i", "want",
    };

    private static readonly string[] Titles;      // 소문자 정규화(ToLowerInvariant) — 검색은 Ordinal(벡터화 IndexOf)
    private static readonly string[] Haystacks;
    private static readonly ulong[] EntryConcepts;
    private static readonly ulong[] TitleConcepts;
    private static readonly byte[] TermHits;   // [항목 × 개념] 헤이스택에 등장한 동의어 수 — 개념 밀도(“라이브” 1 개 vs “송출 · RTMPS · SRT” 3 개)

    static CatalogIntentSearchEngine()
    {
        var n = StudioCatalog.Entries.Length;
        Titles = new string[n];
        Haystacks = new string[n];
        EntryConcepts = new ulong[n];
        TitleConcepts = new ulong[n];
        TermHits = new byte[n * Concepts.Length];
        for (var i = 0; i < n; i++)
        {
            var e = StudioCatalog.Entries[i];
            var h = StudioCatalogHelpRegistry.Find(e.Key);
            Titles[i] = e.Title.ToLowerInvariant();
            Haystacks[i] = (h is null ? e.Title : e.Title + " " + h.IntentKeywords + " " + h.RoleSummary).ToLowerInvariant();
            TitleConcepts[i] = ConceptsIn(e.Title);
            EntryConcepts[i] = ConceptsIn(Haystacks[i]);
            for (var c = 0; c < Concepts.Length; c++) TermHits[i * Concepts.Length + c] = (byte)Math.Min(255, TermsIn(Haystacks[i], Concepts[c]));
        }
    }

    public static int ConceptCount => Concepts.Length;
    public static string ConceptName(int i) => Concepts[i][0];

    /// <summary>
    /// 질의 → 상위 hits(≤ dest.Length · ≤ <see cref="MaxHits"/>) 를 점수 내림차순으로 채우고 개수를 돌려준다.
    /// 일치 0 이면 0. 무할당(stackalloc 165 슬롯).
    /// </summary>
    public static int Search(ReadOnlySpan<char> query, Span<IntentHit> dest)
    {
        var n = Titles.Length;
        var cap = Math.Min(dest.Length, MaxHits);
        if (cap <= 0 || n > 512) return 0;
        query = query.Trim();
        if (query.IsEmpty) return 0;
        if (query.Length > MaxQuery) query = query[..MaxQuery];
        Span<char> lower = stackalloc char[query.Length];
        query.ToLowerInvariant(lower);
        ReadOnlySpan<char> q = lower;

        Span<int> scores = stackalloc int[n];
        Span<ulong> masks = stackalloc ulong[n];
        Span<byte> titleHit = stackalloc byte[n];
        Span<byte> covered = stackalloc byte[n];
        scores.Clear(); masks.Clear(); titleHit.Clear(); covered.Clear();

        var tokens = 0;
        var pos = 0;
        while (NextToken(q, ref pos, out var tok))
        {
            if (IsStopWord(tok)) continue;
            tokens++;
            var tokMask = ConceptsOfToken(tok);
            for (var i = 0; i < n; i++)
            {
                var s = 0;
                var tl = LongestPrefixIn(Titles[i], tok);
                if (tl > 0) { s += tl == tok.Length ? 8 : 6; titleHit[i] = 1; }
                else
                {
                    var hl = LongestPrefixIn(Haystacks[i], tok);
                    if (hl > 0) s += hl == tok.Length ? 4 : 3;
                }
                var cm = tokMask & EntryConcepts[i];
                if (cm != 0)
                {
                    var bits = cm;
                    while (bits != 0)
                    {
                        var c = BitOperations.TrailingZeroCount(bits);
                        bits &= bits - 1;
                        s += 3 + Math.Min(4, (int)TermHits[i * Concepts.Length + c]);
                        if ((TitleConcepts[i] >> c & 1UL) != 0) s += 3;
                    }
                    masks[i] |= cm;
                }
                if (s > 0) { scores[i] += s; covered[i]++; }
            }
        }
        if (tokens == 0) return 0;

        // 다중 토큰 커버리지 보너스 — "쇼츠 자막" 은 둘 다 맞는 항목이 우선.
        for (var i = 0; i < n; i++) if (covered[i] > 1) scores[i] += 4 * (covered[i] - 1);

        var count = 0;
        for (var i = 0; i < n; i++)
        {
            var sc = scores[i];
            if (sc <= 0) continue;
            var at = count;
            while (at > 0 && dest[at - 1].Score < sc) at--;
            if (at >= cap) continue;
            var last = Math.Min(count, cap - 1);
            for (var k = last; k > at; k--) dest[k] = dest[k - 1];
            var fit = (int)Math.Clamp(100L * sc / (tokens * 18L), 1, 99);
            dest[at] = new IntentHit(i, sc, fit, masks[i], titleHit[i] != 0);
            if (count < cap) count++;
        }
        return count;
    }

    /// <summary>추천 사유 태그(UI 표시용 — 렌더 경로에서만 호출, 핫패스 아님).</summary>
    public static string DescribeReason(in IntentHit h)
    {
        var sb = new StringBuilder(64);
        if (h.TitleHit) sb.Append("제목 일치");
        var m = h.ConceptMask;
        var shown = 0;
        while (m != 0 && shown < 3)
        {
            var b = BitOperations.TrailingZeroCount(m);
            m &= m - 1;
            if (sb.Length > 0) sb.Append(" · ");
            sb.Append('#').Append(Concepts[b][0]);
            shown++;
        }
        return sb.Length == 0 ? "역할 설명 일치" : sb.ToString();
    }

    /// <summary>개념 비트 → 표시명 목록(추천 카드 태그 칩).</summary>
    public static int ReasonTags(in IntentHit h, Span<int> conceptIdx)
    {
        var m = h.ConceptMask;
        var k = 0;
        while (m != 0 && k < conceptIdx.Length) { conceptIdx[k++] = BitOperations.TrailingZeroCount(m); m &= m - 1; }
        return k;
    }

    private static bool NextToken(ReadOnlySpan<char> q, ref int pos, out ReadOnlySpan<char> tok)
    {
        while (pos < q.Length && IsSep(q[pos])) pos++;
        var start = pos;
        while (pos < q.Length && !IsSep(q[pos])) pos++;
        tok = q[start..pos];
        return !tok.IsEmpty;
    }

    private static bool IsSep(char c) => char.IsWhiteSpace(c) || c is ',' or '.' or '!' or '?' or '·' or '/' or '"' or '\'' or '(' or ')' or '~';

    private static bool IsStopWord(ReadOnlySpan<char> tok)
    {
        foreach (var w in StopWords) if (tok.SequenceEqual(w)) return true;
        return false;
    }

    /// <summary>tok 의 가장 긴 접두(≥ 2 자 · 1 자 토큰은 그대로)가 text 에 포함되면 그 길이, 아니면 0.</summary>
    private static int LongestPrefixIn(string text, ReadOnlySpan<char> tok)
    {
        var span = text.AsSpan();
        if (tok.Length < MinPrefix) return !IsLatin(tok) && span.IndexOf(tok) >= 0 ? tok.Length : 0;
        // 라틴 토큰은 접두 축약 없이 전체만(“rtmps” → “rt” 오탐 방지).
        var min = IsLatin(tok) ? tok.Length : MinPrefix;
        for (var len = tok.Length; len >= min; len--)
            if (span.IndexOf(tok[..len]) >= 0) return len;
        return 0;
    }

    private static bool IsLatin(ReadOnlySpan<char> tok)
    {
        foreach (var c in tok) if (c > 0x7F) return false;
        return true;
    }

    private static ulong ConceptsOfToken(ReadOnlySpan<char> tok)
    {
        ulong mask = 0;
        for (var c = 0; c < Concepts.Length; c++)
        {
            var g = Concepts[c];
            for (var j = 1; j < g.Length; j++)
            {
                var t = g[j].AsSpan();
                // 토큰이 동의어를 품거나("라이브하고" ⊃ "라이브"), 2 자 이상 토큰이 동의어의 접두("쇼" ✕ · "캡" ✕ · "스트리" ✓)
                if (tok.IndexOf(t) >= 0 ||
                    tok.Length >= MinPrefix && !IsLatin(tok) && t.StartsWith(tok))
                { mask |= 1UL << c; break; }
            }
        }
        return mask;
    }

    private static int TermsIn(string text, string[] group)
    {
        var k = 0;
        for (var j = 1; j < group.Length; j++) if (text.AsSpan().Contains(group[j], StringComparison.OrdinalIgnoreCase)) k++;
        return k;
    }

    private static ulong ConceptsIn(string text)
    {
        ulong mask = 0;
        var span = text.AsSpan();
        for (var c = 0; c < Concepts.Length; c++)
        {
            var g = Concepts[c];
            for (var j = 1; j < g.Length; j++)
                if (span.Contains(g[j], StringComparison.OrdinalIgnoreCase)) { mask |= 1UL << c; break; }
        }
        return mask;
    }
}
