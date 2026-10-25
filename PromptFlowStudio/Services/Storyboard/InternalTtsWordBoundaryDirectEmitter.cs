using System.Buffers.Text;
using System.Text;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [v18.2.3] TTS 엔진 단어 경계 이벤트 1 개 — 시각 [StartMs, EndMs) · 단어는 합성 대본 문자 오프셋(복사 0).
/// </summary>
public readonly struct SpeechWordBoundaryEvent
{
    public readonly int StartMs, EndMs, TextOffset, TextLength;

    public SpeechWordBoundaryEvent(int startMs, int endMs, int textOffset, int textLength)
    {
        StartMs = startMs; EndMs = endMs; TextOffset = textOffset; TextLength = textLength;
    }

    public int DurationMs => EndMs - StartMs;
}

/// <summary>
/// [PromptFlow Studio v18.2.3] 🗣 내부 TTS 단어 경계 인메모리 직결기 — 수동 CSV 업로드 없이 합성 대본 → 단어 경계 → 샷 타임코드를 원자적으로 정렬한다.
///   ▸ <see cref="Begin"/>: 합성 대본을 소유 버퍼에 1 회 기록(용량 내 0 B) · 이벤트 초기화.
///   ▸ <see cref="Emit"/>: 엔진 방출 이벤트(<see cref="SpeechWordBoundaryEvent"/>) 수신 — 범위 · 역순 · 역전 구간은 거부(<see cref="Rejected"/>) · 0 B.
///   ▸ <see cref="EmitSpan"/>: 경계 미방출 엔진용 — 실측 발화 구간을 단어 음절 + 쉼표/마침표 호흡 가중치로 분배(미세 구간 단어 경계 · 0 B).
///   ▸ <see cref="Commit"/>/<see cref="CommitAndResync"/>: 이벤트 → UTF-8 "startMs,endMs,word" 소유 버퍼 직렬화 → <see cref="StoryboardAudioSourceStreamBinder"/> 직결 · 시퀀서 재동기화(0 B).
/// </summary>
public sealed class InternalTtsWordBoundaryDirectEmitter
{
    public const int MaxEvents = StoryboardAudioWordBoundaryAligner.MaxWords;
    /// <summary>호흡 가중치(음절 단위) — 쉼표 2 · 마침표/느낌표/물음표 3.</summary>
    public const int CommaPauseUnits = 2, StopPauseUnits = 3;

    private readonly SpeechWordBoundaryEvent[] _events = new SpeechWordBoundaryEvent[MaxEvents];
    private char[] _script = new char[1024];
    private byte[] _utf8 = new byte[1024 * 3 + MaxEvents * 26];
    private int _scriptLen, _utf8Len;

    public int Count { get; private set; }
    public int Rejected { get; private set; }
    public int Revision { get; private set; }
    public ReadOnlySpan<char> Script => _script.AsSpan(0, _scriptLen);
    public ReadOnlySpan<SpeechWordBoundaryEvent> Events => _events.AsSpan(0, Count);
    /// <summary>마지막 <see cref="Commit"/> 직렬화 결과(UTF-8 "startMs,endMs,word" 줄).</summary>
    public ReadOnlySpan<byte> Utf8 => _utf8.AsSpan(0, _utf8Len);
    public ReadOnlySpan<char> WordOf(in SpeechWordBoundaryEvent e) => _script.AsSpan(e.TextOffset, e.TextLength);

    /// <summary>합성 대본 기록 · 이벤트 초기화(용량 내 재호출 0 B).</summary>
    public void Begin(ReadOnlySpan<char> script)
    {
        if (script.Length > _script.Length)
        {
            _script = new char[Math.Max(script.Length, _script.Length * 2)];
            _utf8 = new byte[_script.Length * 3 + MaxEvents * 26];
        }
        script.CopyTo(_script);
        _scriptLen = script.Length;
        Count = Rejected = _utf8Len = 0;
        Revision++;
    }

    /// <summary>엔진 이벤트 수신 — 대본 범위 밖 · 역전 구간 · 시작 시각 역순 · 단어 없음 · 용량 초과는 거부(0 B).</summary>
    public bool Emit(in SpeechWordBoundaryEvent e)
    {
        if (Count >= MaxEvents || e.EndMs < e.StartMs || e.StartMs < 0 || e.TextLength <= 0
            || (uint)e.TextOffset > (uint)_scriptLen || e.TextLength > _scriptLen - e.TextOffset
            || (Count > 0 && e.StartMs < _events[Count - 1].StartMs) || !IsWord(Script.Slice(e.TextOffset, e.TextLength)))
        {
            Rejected++;
            return false;
        }
        _events[Count++] = e;
        return true;
    }

    /// <summary>
    /// 경계 미방출 엔진 폴백 — 대본 [<paramref name="textStart"/>, +<paramref name="textLength"/>) 의 단어를 실측 구간 [<paramref name="startMs"/>, <paramref name="endMs"/>) 에
    ///   음절 + 호흡(쉼표/마침표) 가중치로 분배해 수신. 반환 = 수신된 단어 수(0 B).
    /// </summary>
    public int EmitSpan(int textStart, int textLength, int startMs, int endMs)
    {
        if (textStart < 0 || textLength <= 0 || textStart > _scriptLen - textLength || endMs <= startMs) return 0;
        var text = Script.Slice(textStart, textLength);
        var total = 0;
        for (var p = 0; NextWord(text, ref p, out var ws, out var wl);) total += Weight(text.Slice(ws, wl), text, ws + wl);
        if (total == 0) return 0;
        long span = endMs - startMs;
        int cum = 0, emitted = 0;
        for (var p = 0; NextWord(text, ref p, out var ws, out var wl);)
        {
            var word = text.Slice(ws, wl);
            var syl = Syllables(word);
            var s = startMs + (int)(cum * span / total);
            var e = startMs + (int)((cum + syl) * span / total);
            cum += Weight(word, text, ws + wl);
            if (Emit(new SpeechWordBoundaryEvent(s, Math.Max(s, e), textStart + ws, wl))) emitted++;
        }
        return emitted;
    }

    /// <summary>대본 전체를 [<paramref name="startMs"/>, <paramref name="endMs"/>) 에 분배.</summary>
    public int EmitSpan(int startMs, int endMs) => EmitSpan(0, _scriptLen, startMs, endMs);

    /// <summary>
    /// 바인딩된 실 오디오 기준 분배 — 첫 발화 시작 ~ 마지막 발화 끝(VAD) · 음성 미검출이면 [0, 길이) · 오디오가 없으면 <paramref name="fallbackMs"/>(0 B).
    /// </summary>
    public int EmitFromAudio(StoryboardAudioSourceStreamBinder audio, int fallbackMs)
    {
        if (!audio.HasAudio) return fallbackMs > 0 ? EmitSpan(0, fallbackMs) : 0;
        Span<AudioVoiceSegment> voice = stackalloc AudioVoiceSegment[StoryboardAudioWordBoundaryAligner.MaxSegments];
        var m = StoryboardAudioWordBoundaryAligner.DetectVoice(audio.Pcm, audio.SampleRate, audio.Channels, voice, out var totalMs);
        return m == 0 ? EmitSpan(0, Math.Max(1, Math.Max(totalMs, audio.DurationMs))) : EmitSpan(voice[0].StartMs, voice[m - 1].EndMs);
    }

    /// <summary>이벤트 → UTF-8 소유 버퍼 직렬화 → 바인더 직결. 반환 = 수신 단어 수(바인더가 거부하면 0 · 0 B).</summary>
    public int Commit(StoryboardAudioSourceStreamBinder binder)
    {
        var dst = _utf8.AsSpan();
        var n = 0;
        foreach (ref readonly var e in Events)
        {
            if (dst.Length - n < 25 + e.TextLength * 3) break;   // 겹친 이벤트 과다 — 용량 내 앞부분만
            Utf8Formatter.TryFormat(e.StartMs, dst[n..], out var w); n += w;
            dst[n++] = (byte)',';
            Utf8Formatter.TryFormat(e.EndMs, dst[n..], out w); n += w;
            dst[n++] = (byte)',';
            n += Encoding.UTF8.GetBytes(WordOf(e), dst[n..]);
            dst[n++] = (byte)'\n';
        }
        _utf8Len = n;
        return Count == 0 || binder.BindWordBoundaries(Utf8) == 0 ? 0 : Count;
    }

    /// <summary>원자 정렬 — <see cref="Commit"/> 후 합성 대본으로 시퀀서 재동기화(단어 경계 우선 · 0 B).</summary>
    public AudioAlignmentSource CommitAndResync(StoryboardAudioSourceStreamBinder binder, StoryboardKeyframeSequencer seq)
    {
        Commit(binder);
        return binder.Resync(seq, Script);
    }

    /// <summary>음절 수 — 한글/CJK/가나/숫자 1 자 = 1 · 라틴 모음군 = 1 · 최소 1.</summary>
    public static int Syllables(ReadOnlySpan<char> word)
    {
        int n = 0;
        var prevVowel = false;
        foreach (var c in word)
        {
            if (c is (>= '가' and <= '힣') or (>= '一' and <= '鿿') or (>= '぀' and <= 'ヿ') or (>= '0' and <= '9')) { n++; prevVowel = false; continue; }
            var v = (c | 0x20) is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
            if (v && !prevVowel) n++;
            prevVowel = v;
        }
        return Math.Max(1, n);
    }

    /// <summary>다음 단어(공백 구분 · 글자/숫자 1 개 이상 — <see cref="StoryboardAudioWordBoundaryAligner.CountWords"/> 와 같은 규칙).</summary>
    public static bool NextWord(ReadOnlySpan<char> text, ref int pos, out int start, out int length)
    {
        while (pos < text.Length)
        {
            while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            start = pos;
            while (pos < text.Length && !char.IsWhiteSpace(text[pos])) pos++;
            length = pos - start;
            if (length > 0 && IsWord(text.Slice(start, length))) return true;
        }
        start = length = 0;
        return false;
    }

    private static bool IsWord(ReadOnlySpan<char> s)
    {
        foreach (var c in s) if (char.IsLetterOrDigit(c)) return true;
        return false;
    }

    /// <summary>단어 가중치 = 음절 + 단어 끝(또는 직후 공백 전) 문장 부호 호흡.</summary>
    private static int Weight(ReadOnlySpan<char> word, ReadOnlySpan<char> text, int end)
    {
        var w = Syllables(word);
        var last = word[^1];
        if (last is '.' or '!' or '?' or '。' or '…' or '！' or '？') w += StopPauseUnits;
        else if (last is ',' or '、' or '，') w += CommaPauseUnits;
        else if (end < text.Length && text[end] == '\n') w += StopPauseUnits;
        return w;
    }
}
