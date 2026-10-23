namespace PromptFlowStudio.Services.CopyrightCompliance;

public enum PolicyCategory : byte { Sexual, Hate, Dangerous, Trademark }

/// <summary>v17.91.0 금칙어 검출 1 건(원문 인덱스 · 값 형식).</summary>
public struct PolicyHit
{
    public int Start;
    public int Length;
    public short Term;
    public PolicyCategory Category;
}

/// <summary>v17.91.0 비프 구간(ms · 자막 큐 시각 비례 배치).</summary>
public struct BeepSpan
{
    public int StartMs;
    public int EndMs;
}

/// <summary>
/// [PromptFlow Studio v17.91.0] 🚫 플랫폼 커뮤니티 가이드라인 &amp; 브랜드 세이프티 가드(<see cref="Scan"/> · <see cref="Mask"/> · <see cref="RenderBeeps"/> 무할당).
///   ▸ 사전: 정책 금칙어 50 종(선정성 15 · 혐오 15 · 위험 20) + 상표 노출 10 종 — 생성자에서 소문자 · 문자/숫자만 남긴 정규형으로 1 회 고정.
///   ▸ 검열: 원문 → 정규화 버퍼(공백 · 기호 · 제로폭 제거 + 원문 위치 맵) 위 <see cref="MemoryExtensions.IndexOf{T}(ReadOnlySpan{T}, ReadOnlySpan{T})"/> Span 탐색 →
///     띄어쓰기/기호 끼워 넣기 우회("폭 탄 제 조", "p.o.r.n") 차단 · 허용 목록(예: "대마도") 위치는 제외.
///   ▸ 치환: 검출 범위(원문 기준 · 겹침 병합)를 <c>***</c> 로 마스킹 · 자막 큐 시각에 비례한 비프 구간 기록 → 1 kHz −12 dBFS 사인(5 ms 램프)으로 오디오 덮어쓰기.
/// </summary>
public sealed class PlatformPolicyBrandSafetyGuard
{
    public const int MaxText = 8192, MaxHits = 128, MaxBeeps = 64, PolicyTerms = 50, BeepPadMs = 40, BeepHz = 1000, Fs = 48000;
    public const string MaskText = "***";

    public static readonly string[] Terms =
    {
        // 선정성 15
        "야동", "음란", "포르노", "성인방송", "19금", "섹스", "몸캠", "조건만남", "원나잇", "노출사진", "porn", "nude", "xxx", "onlyfans", "hentai",
        // 혐오 15
        "틀딱", "한남충", "김치녀", "급식충", "맘충", "짱깨", "쪽바리", "흑형", "장애인새끼", "병신", "nazi", "hitler", "whitepower", "retard", "faggot",
        // 위험 20
        "자살방법", "자해", "마약", "필로폰", "대마", "코카인", "폭탄제조", "총기개조", "테러", "살인청부", "불법촬영", "몰카", "도박사이트", "토토사이트",
        "suicide", "cocaine", "methamphetamine", "bombmaking", "killyourself", "heroin",
        // 상표 10
        "나이키", "아디다스", "코카콜라", "스타벅스", "맥도날드", "nike", "adidas", "cocacola", "starbucks", "mcdonald",
    };

    public static readonly string[] Allow = { "대마도", "마약김밥", "마약떡볶이", "자해공갈" };

    private readonly char[][] _norm;
    private readonly char[][] _allow;
    private readonly PolicyCategory[] _cat;
    private readonly char[] _buf = new char[MaxText];
    private readonly int[] _map = new int[MaxText];
    private readonly byte[] _mark = new byte[MaxText];
    private readonly byte[] _allowMark = new byte[MaxText];
    private readonly PolicyHit[] _hits = new PolicyHit[MaxHits];
    private readonly BeepSpan[] _beeps = new BeepSpan[MaxBeeps];
    private int _hitCount, _beepCount, _len;

    public PlatformPolicyBrandSafetyGuard()
    {
        _norm = new char[Terms.Length][];
        _cat = new PolicyCategory[Terms.Length];
        for (var i = 0; i < Terms.Length; i++)
        {
            _norm[i] = Normalize(Terms[i]);
            _cat[i] = i < 15 ? PolicyCategory.Sexual : i < 30 ? PolicyCategory.Hate : i < PolicyTerms ? PolicyCategory.Dangerous : PolicyCategory.Trademark;
        }
        _allow = new char[Allow.Length][];
        for (var i = 0; i < Allow.Length; i++) _allow[i] = Normalize(Allow[i]);
    }

    public bool MaskTrademarks { get; set; } = true;
    public int HitCount => _hitCount;
    public ref readonly PolicyHit Hit(int i) => ref _hits[i];
    public int BeepCount => _beepCount;
    public ref readonly BeepSpan Beep(int i) => ref _beeps[i];
    public int PolicyHitCount { get; private set; }
    public int TrademarkHitCount { get; private set; }
    public bool Truncated { get; private set; }
    public long Scans { get; private set; }
    public long TotalHits { get; private set; }

    public static PolicyCategory CategoryOf(int term) => term < 15 ? PolicyCategory.Sexual : term < 30 ? PolicyCategory.Hate : term < PolicyTerms ? PolicyCategory.Dangerous : PolicyCategory.Trademark;

    public static string CategoryName(PolicyCategory c) => c switch
    {
        PolicyCategory.Sexual => "선정성", PolicyCategory.Hate => "혐오", PolicyCategory.Dangerous => "위험", _ => "상표",
    };

    // ══════════════════════ 검열(무할당) ══════════════════════

    /// <summary>원문 검열 — 검출 수. 히트는 원문 인덱스 기준(<see cref="Hit"/>).</summary>
    public int Scan(ReadOnlySpan<char> text)
    {
        Scans++;
        _hitCount = 0;
        PolicyHitCount = TrademarkHitCount = 0;
        Truncated = false;
        _len = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!char.IsLetterOrDigit(c)) continue;
            if (_len >= MaxText) { Truncated = true; break; }
            _buf[_len] = char.ToLowerInvariant(c);
            _map[_len++] = i;
        }
        var norm = _buf.AsSpan(0, _len);
        Array.Clear(_allowMark, 0, _len);
        foreach (var a in _allow)
        {
            var from = 0;
            while (from < _len)
            {
                var at = norm[from..].IndexOf(a);
                if (at < 0) break;
                at += from;
                _allowMark.AsSpan(at, a.Length).Fill(1);
                from = at + 1;
            }
        }
        for (var t = 0; t < _norm.Length; t++)
        {
            var term = _norm[t];
            var from = 0;
            while (from < _len && _hitCount < MaxHits)
            {
                var at = norm[from..].IndexOf(term);
                if (at < 0) break;
                at += from;
                from = at + 1;
                if (_allowMark[at] != 0) continue;
                ref var h = ref _hits[_hitCount++];
                h.Start = _map[at];
                h.Length = _map[at + term.Length - 1] - h.Start + 1;
                h.Term = (short)t;
                h.Category = _cat[t];
                if (h.Category == PolicyCategory.Trademark) TrademarkHitCount++; else PolicyHitCount++;
            }
        }
        TotalHits += _hitCount;
        return _hitCount;
    }

    /// <summary>검열 + 마스킹 — 검출 범위(겹침 병합)를 <c>***</c> 로 치환해 dst 에 기록. 반환 = 기록 문자 수(dst 부족 시 잘림).</summary>
    public int Mask(ReadOnlySpan<char> text, Span<char> dst)
    {
        Scan(text);
        var n = text.Length;
        Array.Clear(_mark, 0, Math.Min(n, MaxText));
        for (var i = 0; i < _hitCount; i++)
        {
            ref readonly var h = ref _hits[i];
            if (h.Category == PolicyCategory.Trademark && !MaskTrademarks) continue;
            var end = Math.Min(MaxText, h.Start + h.Length);
            for (var k = h.Start; k < end; k++) _mark[k] = 1;
        }
        var w = 0;
        for (var i = 0; i < n && w < dst.Length; i++)
        {
            if (i < MaxText && _mark[i] != 0)
            {
                while (i + 1 < n && i + 1 < MaxText && _mark[i + 1] != 0) i++;
                foreach (var c in MaskText) { if (w >= dst.Length) break; dst[w++] = c; }
                continue;
            }
            dst[w++] = text[i];
        }
        return w;
    }

    /// <summary>자막 큐 1 개 마스킹 + 비프 구간 기록(문자 위치 → 큐 시각 비례 · ±40 ms 패드).</summary>
    public int MaskCue(ReadOnlySpan<char> text, int startMs, int endMs, Span<char> dst)
    {
        var w = Mask(text, dst);
        var len = Math.Max(1, text.Length);
        var dur = Math.Max(0, endMs - startMs);
        for (var i = 0; i < _hitCount && _beepCount < MaxBeeps; i++)
        {
            ref readonly var h = ref _hits[i];
            if (h.Category == PolicyCategory.Trademark && !MaskTrademarks) continue;
            ref var b = ref _beeps[_beepCount++];
            b.StartMs = Math.Max(startMs, startMs + (int)((long)dur * h.Start / len) - BeepPadMs);
            b.EndMs = Math.Min(endMs, startMs + (int)((long)dur * (h.Start + h.Length) / len) + BeepPadMs);
        }
        return w;
    }

    public void ClearBeeps() => _beepCount = 0;

    /// <summary>기록된 비프 구간을 오디오에 덮어쓰기(1 kHz · −12 dBFS · 5 ms 램프 크로스페이드). 반환 = 처리 샘플 수.</summary>
    public long RenderBeeps(Span<float> pcm)
    {
        const float Amp = 0.251f;
        const int Ramp = Fs / 200;
        long total = 0;
        for (var i = 0; i < _beepCount; i++)
        {
            ref readonly var b = ref _beeps[i];
            var s0 = (int)Math.Clamp((long)b.StartMs * Fs / 1000, 0, pcm.Length);
            var s1 = (int)Math.Clamp((long)b.EndMs * Fs / 1000, s0, pcm.Length);
            var len = s1 - s0;
            for (var n = 0; n < len; n++)
            {
                var w = MathF.Min(1f, MathF.Min(n, len - 1 - n) / (float)Ramp);
                var tone = Amp * MathF.Sin(2f * MathF.PI * BeepHz * n / Fs);
                pcm[s0 + n] = pcm[s0 + n] * (1f - w) + tone * w;
            }
            total += len;
        }
        return total;
    }

    private static char[] Normalize(string s)
    {
        var n = 0;
        foreach (var c in s) if (char.IsLetterOrDigit(c)) n++;
        var r = new char[n];
        n = 0;
        foreach (var c in s) if (char.IsLetterOrDigit(c)) r[n++] = char.ToLowerInvariant(c);
        return r;
    }
}
