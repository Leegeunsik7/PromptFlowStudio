namespace PromptFlowStudio.Services.ViralExport;

public enum HookCopyKind : byte { Question, Number, Twist }

/// <summary>v17.85.0 아바타 표정/모션 프레임(값 형식).</summary>
public struct FaceFrame
{
    public long TUs;
    public float JawOpen, Smile, Motion;
}

/// <summary>v17.85.0 썸네일 후보 키프레임.</summary>
public struct ThumbCandidate
{
    public long TUs;
    public float JawOpen, Smile, Motion, Score, PredictedCtr;
}

/// <summary>
/// [PromptFlow Studio v17.85.0] 🖼️ 고CTR 썸네일 &amp; 훅 카피 자동 생성기.
///   ▸ <see cref="Push"/>(60 fps 무할당): 표정 개방도(JawOpen 45 %) · 미소(Smile 35 %) · 모션 에너지(20 %) 복합 점수 —
///     1 s 이상 떨어진 상위 3 키프레임을 A/B/C 썸네일 베이스로 유지(근접 후보는 강한 쪽으로 교체).
///   ▸ <see cref="Compose"/>(무할당): 대본 키워드(최장 실사 토큰 · 조사/불용어 제거) → ① 의문형 호기심 · ② 파격 수치 · ③ 극적 반전 경고 카피를 고정 char 버퍼에 절차 합성.
/// </summary>
public sealed class HighCtrThumbnailHookGenerator
{
    public const int Slots = 3, CopyCap = 96, KeyCap = 24;
    public const long SeparationUs = 1_000_000;
    public const float WJaw = 0.45f, WSmile = 0.35f, WMotion = 0.20f;
    private static readonly string[] Stop = { "진짜", "그냥", "이거", "완전", "너무", "정말", "이건", "그리고", "근데", "역대급", "실화냐" };
    private const string Particles = "은는이가을를에의도로와과";

    private readonly ThumbCandidate[] _top = new ThumbCandidate[Slots];
    private readonly char[][] _copy = { new char[CopyCap], new char[CopyCap], new char[CopyCap] };
    private readonly int[] _copyLen = new int[Slots];
    private readonly char[] _key = new char[KeyCap];
    private readonly string?[] _cache = new string?[Slots];
    private int _n, _keyLen;

    public int Count => _n;
    public long Frames { get; private set; }
    public long Composed { get; private set; }
    public ReadOnlySpan<char> Keyword => _key.AsSpan(0, _keyLen);

    /// <summary>점수 내림차순 후보(0 = 최고).</summary>
    public ThumbCandidate Candidate(int i) => _top[Math.Clamp(i, 0, Slots - 1)];

    public void Reset()
    {
        Array.Clear(_top); Array.Clear(_copyLen); Array.Clear(_cache);
        _n = 0; _keyLen = 0; Frames = 0;
    }

    public static float FaceScore(in FaceFrame f) => WJaw * Math.Clamp(f.JawOpen, 0f, 1f) + WSmile * Math.Clamp(f.Smile, 0f, 1f) + WMotion * Math.Clamp(f.Motion, 0f, 1f);

    public void Push(in FaceFrame f)
    {
        Frames++;
        var s = FaceScore(f);
        // 1 s 이내 기존 후보 → 강한 쪽 유지
        for (var i = 0; i < _n; i++)
        {
            if (Math.Abs(_top[i].TUs - f.TUs) >= SeparationUs) continue;
            if (s <= _top[i].Score) return;
            _top[i] = Make(f, s);
            Sort();
            return;
        }
        if (_n < Slots) { _top[_n++] = Make(f, s); Sort(); return; }
        if (s <= _top[Slots - 1].Score) return;
        _top[Slots - 1] = Make(f, s);
        Sort();
    }

    private static ThumbCandidate Make(in FaceFrame f, float s) => new()
    {
        TUs = f.TUs, JawOpen = f.JawOpen, Smile = f.Smile, Motion = f.Motion, Score = s,
        PredictedCtr = 2.0f + 9.0f * s,   // % — 고개방 표정 · 고모션 썸네일일수록 클릭률 상승(정규화 회귀)
    };

    private void Sort()
    {
        for (var i = 1; i < _n; i++)
        {
            var c = _top[i]; var j = i - 1;
            while (j >= 0 && _top[j].Score < c.Score) { _top[j + 1] = _top[j]; j--; }
            _top[j + 1] = c;
        }
    }

    // ══════════════════════ 훅 카피 절차 합성(무할당) ══════════════════════

    public void Compose(ReadOnlySpan<char> script, int seconds, int percent)
    {
        ExtractKeyword(script);
        var kw = Keyword;
        seconds = Math.Clamp(seconds, 1, 999); percent = Math.Clamp(percent, 1, 9999);

        // ① 의문형 호기심
        var b = _copy[0].AsSpan(); var n = 0;
        Put(b, ref n, kw); Put(b, ref n, ", 이게 진짜 된다고?");
        _copyLen[0] = n;

        // ② 파격 수치
        b = _copy[1].AsSpan(); n = 0;
        Put(b, ref n, kw); Put(b, ref n, " 단 "); Num(b, ref n, seconds); Put(b, ref n, "초 만에 "); Num(b, ref n, percent); Put(b, ref n, "% 폭발");
        _copyLen[1] = n;

        // ③ 극적 반전 경고
        b = _copy[2].AsSpan(); n = 0;
        Put(b, ref n, "⚠️ 끝까지 보세요… "); Put(b, ref n, kw); Put(b, ref n, "의 충격 반전");
        _copyLen[2] = n;

        Array.Clear(_cache);
        Composed++;
    }

    public ReadOnlySpan<char> Copy(int i) => _copy[Math.Clamp(i, 0, Slots - 1)].AsSpan(0, _copyLen[Math.Clamp(i, 0, Slots - 1)]);

    /// <summary>UI 표시용 문자열(합성 1 회당 슬롯별 1 회 생성 · 캐시).</summary>
    public string CopyText(int i)
    {
        i = Math.Clamp(i, 0, Slots - 1);
        return _cache[i] ??= new string(Copy(i));
    }

    public static float CopyCtrBonus(HookCopyKind k) => k switch { HookCopyKind.Question => 0.6f, HookCopyKind.Number => 0.9f, _ => 0.8f };

    public static string KindName(HookCopyKind k) => k switch { HookCopyKind.Question => "의문형 호기심", HookCopyKind.Number => "파격 수치", _ => "반전 경고" };

    private void ExtractKeyword(ReadOnlySpan<char> s)
    {
        int bestS = -1, bestL = 0, i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && !char.IsLetterOrDigit(s[i])) i++;
            var st = i;
            while (i < s.Length && char.IsLetterOrDigit(s[i])) i++;
            var len = i - st;
            if (len < 2) continue;
            var tok = s.Slice(st, len);
            if (len > 2 && Particles.Contains(tok[^1])) len--;
            if (IsStop(s.Slice(st, len)) || IsStop(tok)) continue;
            if (len > bestL) { bestL = len; bestS = st; }
        }
        if (bestS < 0) { "이 장면".AsSpan().CopyTo(_key); _keyLen = 4; return; }
        _keyLen = Math.Min(bestL, KeyCap);
        s.Slice(bestS, _keyLen).CopyTo(_key);
    }

    private static bool IsStop(ReadOnlySpan<char> t)
    {
        for (var i = 0; i < Stop.Length; i++) if (t.SequenceEqual(Stop[i])) return true;
        return false;
    }

    private static void Put(Span<char> b, ref int n, ReadOnlySpan<char> s)
    {
        var k = Math.Min(s.Length, b.Length - n);
        s.Slice(0, k).CopyTo(b.Slice(n));
        n += k;
    }

    private static void Num(Span<char> b, ref int n, int v)
    {
        if (v.TryFormat(b.Slice(n), out var w, default, System.Globalization.CultureInfo.InvariantCulture)) n += w;
    }
}
