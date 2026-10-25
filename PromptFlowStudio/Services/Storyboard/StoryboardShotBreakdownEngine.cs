namespace PromptFlowStudio.Services.Storyboard;

/// <summary>[v18.2.0] 샷 권장 카메라 앵글 — 와이드(설정) · 미디엄(대화) · 클로즈업(강조).</summary>
public enum ShotAngle : byte { Wide, Medium, CloseUp }

/// <summary>
/// [v18.2.0] 대본 버퍼 위 샷 1 개 — 텍스트는 원본 버퍼 오프셋(복사 0) · 시각은 발화 + 뒤따르는 호흡(쉼) 포함 [StartMs, EndMs).
///   샷은 끊김 없이 이어진다(다음 샷 StartMs = 이전 샷 EndMs).
/// </summary>
public struct StoryboardShot
{
    public int TextStart, TextLength;
    public int StartMs, EndMs, SpeechMs;
    public int Syllables;
    public ShotAngle Angle;
    /// <summary>경계 문장 부호('.' '!' '?' ',' · 0 = 대본 끝/강제 분할).</summary>
    public char Terminator;

    public readonly int DurationMs => EndMs - StartMs;
    public readonly ReadOnlySpan<char> Text(ReadOnlySpan<char> script) => script.Slice(TextStart, TextLength);
}

/// <summary>
/// [PromptFlow Studio v18.2.0] 🎞 무할당 대본 발화 호흡 기반 샷 분할 엔진.
///   ▸ 대본을 <see cref="ReadOnlySpan{T}"/> 로 1 회 스캔 — 문장 부호(. ! ? 。 … 줄바꿈) = 하드 컷, 쉼표(, 、) = 발화가 호흡 최소 길이 이상일 때만 소프트 컷.
///   ▸ 발화 길이 = 음절 수 × 음절 템포(한글/CJK 1 자 = 1 음절 · 라틴 모음군 · 숫자 1 자) · 호흡 쉼(문장 400 ms · 쉼표 200 ms) 을 샷 끝에 포함.
///   ▸ 발화가 <see cref="MaxSpeechMs"/> 를 넘으면 다음 공백에서 강제 분할 · 앵글은 위치/부호/발화 길이로 결정 · 출력은 호출자 Span(0 B).
/// </summary>
public static class StoryboardShotBreakdownEngine
{
    public const int MaxShots = 64;
    /// <summary>한국어 숏폼 내레이션 기준 음절 템포(≈ 5.5 음절/초).</summary>
    public const int DefaultSyllableMs = 180;
    public const int SentencePauseMs = 400, CommaPauseMs = 200;
    /// <summary>쉼표 소프트 컷 최소 발화(호흡 1 회) · 강제 분할 상한.</summary>
    public const int MinBreathMs = 1_200, MaxSpeechMs = 5_000;
    /// <summary>앵글 임계 — 발화 ≥ 3 s 와이드 · &lt; 1.5 s 클로즈업.</summary>
    public const int WideSpeechMs = 3_000, CloseUpSpeechMs = 1_500;

    /// <summary>대본 → 샷 슬라이스(시간 순). <paramref name="dst"/> 가 차면 남은 대본은 버린다. 반환 = 샷 수.</summary>
    public static int Breakdown(ReadOnlySpan<char> script, Span<StoryboardShot> dst, int syllableMs = DefaultSyllableMs)
    {
        if (syllableMs <= 0) syllableMs = DefaultSyllableMs;
        int n = 0, cursorMs = 0, segStart = 0, syl = 0;
        var inLatin = false;
        var latinVowel = false;
        var latinSyl = 0;
        for (var i = 0; i < script.Length && n < dst.Length; i++)
        {
            var c = script[i];
            if (IsLatin(c))
            {
                var v = IsVowel(c);
                if (!inLatin) { inLatin = true; latinSyl = 0; latinVowel = false; }
                if (v && !latinVowel) latinSyl++;
                latinVowel = v;
                continue;
            }
            if (inLatin) { syl += Math.Max(1, latinSyl); inLatin = false; }

            if (IsSyllable(c)) { syl++; continue; }
            if (IsHardStop(c))
            {
                if (syl > 0) { Emit(script, segStart, i + 1, syl, syllableMs, SentencePauseMs, c, ref cursorMs, dst, ref n); syl = 0; }
                segStart = i + 1;
                continue;
            }
            if (IsComma(c))
            {
                if (syl * syllableMs >= MinBreathMs) { Emit(script, segStart, i + 1, syl, syllableMs, CommaPauseMs, ',', ref cursorMs, dst, ref n); syl = 0; segStart = i + 1; }
                continue;
            }
            if (char.IsWhiteSpace(c) && syl * syllableMs >= MaxSpeechMs)
            {
                Emit(script, segStart, i, syl, syllableMs, CommaPauseMs, '\0', ref cursorMs, dst, ref n);
                syl = 0;
                segStart = i + 1;
            }
        }
        if (inLatin) syl += Math.Max(1, latinSyl);
        if (syl > 0 && n < dst.Length) Emit(script, segStart, script.Length, syl, syllableMs, SentencePauseMs, '\0', ref cursorMs, dst, ref n);
        return n;
    }

    /// <summary>샷 시퀀스 총 길이(마지막 샷 끝 · ms).</summary>
    public static int TotalMs(ReadOnlySpan<StoryboardShot> shots) => shots.Length == 0 ? 0 : shots[^1].EndMs;

    /// <summary>앵글 규칙 — 첫 샷 와이드(설정) · 느낌표/물음표 클로즈업(강조) · 그 외 발화 길이.</summary>
    public static ShotAngle ChooseAngle(int index, char terminator, int speechMs)
    {
        if (terminator is '!' or '?') return ShotAngle.CloseUp;
        if (index == 0 || speechMs >= WideSpeechMs) return ShotAngle.Wide;
        return speechMs < CloseUpSpeechMs ? ShotAngle.CloseUp : ShotAngle.Medium;
    }

    private static void Emit(ReadOnlySpan<char> script, int start, int end, int syl, int syllableMs, int pauseMs, char term,
                             ref int cursorMs, Span<StoryboardShot> dst, ref int n)
    {
        while (start < end && char.IsWhiteSpace(script[start])) start++;
        while (end > start && char.IsWhiteSpace(script[end - 1])) end--;
        ref var s = ref dst[n];
        s.TextStart = start;
        s.TextLength = end - start;
        s.Syllables = syl;
        s.SpeechMs = syl * syllableMs;
        s.StartMs = cursorMs;
        s.EndMs = cursorMs + s.SpeechMs + pauseMs;
        s.Terminator = term;
        s.Angle = ChooseAngle(n, term, s.SpeechMs);
        cursorMs = s.EndMs;
        n++;
    }

    private static bool IsLatin(char c) => (uint)((c | 0x20) - 'a') <= 'z' - 'a';
    private static bool IsVowel(char c) => (c | 0x20) is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
    /// <summary>한글 음절 · CJK 통합 한자 · 가나 · 숫자 = 1 음절.</summary>
    private static bool IsSyllable(char c) => c is (>= '가' and <= '힣') or (>= '一' and <= '鿿') or (>= '぀' and <= 'ヿ') or (>= '0' and <= '9');
    private static bool IsHardStop(char c) => c is '.' or '!' or '?' or '。' or '…' or '\n' or '！' or '？';
    private static bool IsComma(char c) => c is ',' or '、' or '，';
}
