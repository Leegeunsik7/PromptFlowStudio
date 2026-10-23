using System.Globalization;

namespace PromptFlowStudio.Services.CompetitorSpy;

public enum CounterVerdict : byte { Approved, EmptyInput, Plagiarism, Duplicate }

public enum CounterTarget : byte { None, OrchestratorStage1, Queued }

/// <summary>1 단계 입력 큐 전송 기록 1 건(값 형식).</summary>
public struct CounterDispatch
{
    public ulong Fingerprint;
    public HookPattern Hook;
    public CounterTarget Target;
    public float Similarity;
    public int Attempts, SourceIndex;
    public long AtUnix;
}

/// <summary>
/// [PromptFlow Studio v17.99.0] 🛡 카운터-바이럴 스크립트 합성 &amp; 표절 방어 세이프티 가드(<see cref="Compose"/> · <see cref="Commit"/> 무할당).
///   ▸ 합성: 역공학된 훅 유형(구조 뼈대)만 차용 — 유형별 자사 템플릿 3 변형에 페르소나 · 타깃 키워드를 치환하고,
///     컷 템포 · 샷 길이 · 피크 비트 · CTA 비트를 고정 char 버퍼(<see cref="MaxScriptChars"/>)에 무할당 조립.
///   ▸ 표절 방어: 생성 훅 ↔ 경쟁 훅 정규형(소문자 · 문자/숫자만) 유사도 = max(Levenshtein 정규 유사도, 문자 바이그램 Jaccard) —
///     ≥ <see cref="SimilarityLimit"/>(기본 40 %)면 다음 변형으로 즉시 재합성, 3 변형 모두 초과 = 차단.
///   ▸ 전송: 승인 1 회 = 커밋 1 회 · 동일 지문 중복 차단 · v17.90 오케스트레이터 1 단계 입력 → 불가(가동/승인 대기) 시 로컬 대기열 · 고정 8 링 원장.
/// </summary>
public sealed class CounterViralScriptFormulaSafetyGuard
{
    public const int MaxScriptChars = 640, MaxHookChars = 128, NormMax = 128, Variants = 3, InboxCap = 8, BigramBits = 1024;
    public const float DefaultSimilarityLimit = 0.40f;

    /// <summary>[유형][변형] 자사 훅 템플릿 — {K} = 타깃 키워드 · {P} = 페르소나.</summary>
    private static readonly string[][] Templates =
    {
        new[] { "{P}가 정리한 {K} 핵심 3가지", "{K}, 이 순서대로만 하세요", "{P}의 {K} 30초 요약" },
        new[] { "{K}, 아직도 이렇게 하세요?", "{P}는 {K}를 왜 이렇게 할까?", "{K} 실패하는 진짜 이유는?" },
        new[] { "다들 {K}가 어렵다는데, {P} 생각은 달라요", "{K} 정답인 줄 알았는데 결과는 반대였습니다", "{P}가 {K}로 망한 줄 알았던 순간" },
        new[] { "{K} 이 실수 하나로 전부 날립니다", "{K} 하기 전에 이것만은 피하세요", "{P}가 경고하는 {K} 함정" },
        new[] { "{K} 고수들만 쓰는 마지막 한 수", "{P}가 처음 공개하는 {K} 루틴", "{K} 결과가 달라진 딱 한 가지" },
    };

    private readonly char[] _script = new char[MaxScriptChars];
    private readonly char[] _hook = new char[MaxHookChars];
    private readonly CounterDispatch[] _inbox = new CounterDispatch[InboxCap];
    private int _scriptLen, _hookLen, _head, _count;
    private bool _armed;

    public float SimilarityLimit { get; set; } = DefaultSimilarityLimit;
    public ReadOnlySpan<char> Script => _script.AsSpan(0, _scriptLen);
    public ReadOnlySpan<char> HookLine => _hook.AsSpan(0, _hookLen);
    public HookPattern LastPattern { get; private set; }
    public int LastVariant { get; private set; }
    public int LastAttempts { get; private set; }
    public float LastSimilarity { get; private set; }
    public float LastLevenshtein { get; private set; }
    public float LastJaccard { get; private set; }
    public ulong LastFingerprint { get; private set; }
    public CounterVerdict LastVerdict { get; private set; }
    public long Composes { get; private set; }
    public long Approved { get; private set; }
    public long Resynthesized { get; private set; }
    public long BlockedPlagiarism { get; private set; }
    public long BlockedDuplicate { get; private set; }
    public long BlockedEmpty { get; private set; }
    public long ToOrchestrator { get; private set; }
    public long Queued { get; private set; }
    public long Blocked => BlockedPlagiarism + BlockedDuplicate + BlockedEmpty;
    public int DispatchCount => _count;
    /// <summary>전송 기록(0 = 최신).</summary>
    public ref readonly CounterDispatch Dispatch(int i) => ref _inbox[(_head - 1 - i + InboxCap * 2) % InboxCap];

    public static string VerdictName(CounterVerdict v) => v switch
    {
        CounterVerdict.Approved => "승인",
        CounterVerdict.EmptyInput => "키워드 없음",
        CounterVerdict.Plagiarism => "표절 차단",
        _ => "중복 기획",
    };

    public static string TargetName(CounterTarget t) => t switch
    {
        CounterTarget.OrchestratorStage1 => "오케스트레이터 ①단계",
        CounterTarget.Queued => "로컬 대기열",
        _ => "없음",
    };

    /// <summary>대항 기획안 합성 + 표절 검사(무할당). 경쟁 훅과 ≥ 한계 유사 시 다음 변형으로 재합성.</summary>
    public CounterVerdict Compose(in ViralFormulaBlueprint bp, ReadOnlySpan<char> persona, ReadOnlySpan<char> keyword, ReadOnlySpan<char> competitorHook, int startVariant)
    {
        Composes++;
        _armed = false;
        _scriptLen = _hookLen = 0;
        LastAttempts = 0;
        LastSimilarity = LastLevenshtein = LastJaccard = 0f;
        LastFingerprint = 0;
        LastPattern = bp.Hook;
        keyword = keyword.Trim();
        persona = persona.Trim();
        if (keyword.IsEmpty) { BlockedEmpty++; return LastVerdict = CounterVerdict.EmptyInput; }
        if (persona.IsEmpty) persona = "우리 채널".AsSpan();

        var row = Templates[(int)bp.Hook];
        var start = ((startVariant % Variants) + Variants) % Variants;
        var ok = false;
        for (var a = 0; a < Variants && !ok; a++)
        {
            LastAttempts = a + 1;
            LastVariant = (start + a) % Variants;
            _hookLen = Expand(row[LastVariant], persona, keyword, _hook);
            LastSimilarity = Similarity(HookLine, competitorHook, out var lev, out var jac);
            LastLevenshtein = lev; LastJaccard = jac;
            ok = LastSimilarity < SimilarityLimit;
        }
        if (LastAttempts > 1) Resynthesized += LastAttempts - 1;
        if (!ok) { _hookLen = 0; BlockedPlagiarism++; return LastVerdict = CounterVerdict.Plagiarism; }

        BuildScript(in bp, persona, keyword);
        var h = 0xCBF29CE484222325UL;
        for (var i = 0; i < _scriptLen; i++) h = (h ^ _script[i]) * 0x100000001B3UL;
        LastFingerprint = h;
        for (var i = 0; i < _count; i++)
            if (Dispatch(i).Fingerprint == h) { BlockedDuplicate++; return LastVerdict = CounterVerdict.Duplicate; }
        Approved++;
        _armed = true;
        return LastVerdict = CounterVerdict.Approved;
    }

    /// <summary>전송 확정 기록(승인 직후 1 회). 반환 = 기록 여부.</summary>
    public bool Commit(CounterTarget target, int sourceIndex, long nowUnix)
    {
        if (!_armed || target == CounterTarget.None) return false;
        _armed = false;
        _inbox[_head] = new CounterDispatch
        {
            Fingerprint = LastFingerprint, Hook = LastPattern, Target = target, Similarity = LastSimilarity,
            Attempts = LastAttempts, SourceIndex = sourceIndex, AtUnix = nowUnix,
        };
        _head = (_head + 1) % InboxCap;
        if (_count < InboxCap) _count++;
        if (target == CounterTarget.OrchestratorStage1) ToOrchestrator++; else Queued++;
        return true;
    }

    /// <summary>정규형 유사도 = max(Levenshtein, 바이그램 Jaccard) · 0~1(무할당 · 스택 버퍼).</summary>
    public static float Similarity(ReadOnlySpan<char> a, ReadOnlySpan<char> b, out float levenshtein, out float jaccard)
    {
        Span<char> na = stackalloc char[NormMax];
        Span<char> nb = stackalloc char[NormMax];
        var la = Normalize(a, na);
        var lb = Normalize(b, nb);
        levenshtein = jaccard = 0f;
        if (la == 0 || lb == 0) return 0f;
        levenshtein = 1f - Levenshtein(na[..la], nb[..lb]) / (float)Math.Max(la, lb);
        jaccard = BigramJaccard(na[..la], nb[..lb]);
        return Math.Max(levenshtein, jaccard);
    }

    public static int Levenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        Span<int> prev = stackalloc int[NormMax + 1];
        Span<int> cur = stackalloc int[NormMax + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            var t = prev; prev = cur; cur = t;
        }
        return prev[b.Length];
    }

    /// <summary>문자 바이그램 해시 비트셋(1024) Jaccard.</summary>
    public static float BigramJaccard(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length < 2 || b.Length < 2) return a.SequenceEqual(b) ? 1f : 0f;
        Span<ulong> sa = stackalloc ulong[BigramBits / 64];
        Span<ulong> sb = stackalloc ulong[BigramBits / 64];
        sa.Clear(); sb.Clear();
        Fill(a, sa); Fill(b, sb);
        int inter = 0, uni = 0;
        for (var i = 0; i < sa.Length; i++)
        {
            inter += System.Numerics.BitOperations.PopCount(sa[i] & sb[i]);
            uni += System.Numerics.BitOperations.PopCount(sa[i] | sb[i]);
        }
        return uni == 0 ? 0f : inter / (float)uni;
    }

    public void Reset()
    {
        Array.Clear(_script); Array.Clear(_hook); Array.Clear(_inbox);
        _scriptLen = _hookLen = _head = _count = 0;
        _armed = false;
        SimilarityLimit = DefaultSimilarityLimit;
        LastPattern = HookPattern.Statement;
        LastVariant = LastAttempts = 0;
        LastSimilarity = LastLevenshtein = LastJaccard = 0f;
        LastFingerprint = 0;
        LastVerdict = CounterVerdict.Approved;
        Composes = Approved = Resynthesized = BlockedPlagiarism = BlockedDuplicate = BlockedEmpty = ToOrchestrator = Queued = 0;
    }

    // ══════════════════════ 내부 ══════════════════════

    private void BuildScript(in ViralFormulaBlueprint bp, ReadOnlySpan<char> persona, ReadOnlySpan<char> keyword)
    {
        _scriptLen = 0;
        Put("[0~3s 훅 · "); Put(ViralHookAudiovisualReverseEngineer.PatternNames[(int)bp.Hook]); Put("] "); Put(HookLine); Put("\n");
        Put("[3s~ 본문] 컷 "); Num(bp.CutsPerSec); Put("회/초 템포 · 샷 "); Num(bp.AvgShotSec); Put("s 이내 전환으로 "); Put(keyword); Put(" 시연\n");
        Put("[비트] 오디오 피크 "); Num(bp.PeakDensityPerSec); Put("/s 지점마다 키워드 강조 컷 · 분산도 "); Num(bp.PeakDispersion); Put("\n");
        Put("[CTA] "); Put(persona); Put(" 채널에서 "); Put(keyword); Put(" 다음 편 공개");
    }

    private void Put(ReadOnlySpan<char> s)
    {
        var n = Math.Min(s.Length, MaxScriptChars - _scriptLen);
        s[..n].CopyTo(_script.AsSpan(_scriptLen));
        _scriptLen += n;
    }

    private void Num(float v)
    {
        if (v.TryFormat(_script.AsSpan(_scriptLen), out var w, "0.0", CultureInfo.InvariantCulture)) _scriptLen += w;
    }

    private static int Expand(string template, ReadOnlySpan<char> persona, ReadOnlySpan<char> keyword, Span<char> dst)
    {
        var t = template.AsSpan();
        var n = 0;
        for (var i = 0; i < t.Length; i++)
        {
            ReadOnlySpan<char> s = default;
            if (t[i] == '{' && i + 2 < t.Length && t[i + 2] == '}')
            {
                s = t[i + 1] == 'K' ? keyword : t[i + 1] == 'P' ? persona : t.Slice(i, 3);
                i += 2;
            }
            else s = t.Slice(i, 1);
            var c = Math.Min(s.Length, dst.Length - n);
            s[..c].CopyTo(dst[n..]);
            n += c;
        }
        return n;
    }

    private static int Normalize(ReadOnlySpan<char> src, Span<char> dst)
    {
        var n = 0;
        for (var i = 0; i < src.Length && n < dst.Length; i++)
            if (char.IsLetterOrDigit(src[i])) dst[n++] = char.ToLowerInvariant(src[i]);
        return n;
    }

    private static void Fill(ReadOnlySpan<char> s, Span<ulong> bits)
    {
        for (var i = 0; i + 1 < s.Length; i++)
        {
            var h = (uint)(s[i] * 31 + s[i + 1]) * 2654435761u >> 22;   // 상위 10 비트 = 0~1023
            bits[(int)(h >> 6)] |= 1UL << (int)(h & 63);
        }
    }
}
