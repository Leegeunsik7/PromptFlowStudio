namespace PromptFlowStudio.Services.Storyboard;

/// <summary>[v18.2.3] 자막 형식 — SubRip(.srt · 쉼표 밀리초) · WebVTT(.vtt · 마침표 밀리초).</summary>
public enum SubtitleFormat : byte { Unknown, SubRip, WebVtt }

/// <summary>[v18.2.3] 자막 구간 1 개 — 시각 [StartMs, EndMs) · 본문은 파서 대본 버퍼 오프셋(태그 제거 · 여러 줄은 공백 결합).</summary>
public struct SubtitleCue
{
    public int Index;
    public int StartMs, EndMs;
    public int TextStart, TextLength;
    public readonly int DurationMs => EndMs - StartMs;
}

/// <summary>
/// [PromptFlow Studio v18.2.3] 📝 무할당 표준 자막(SRT · WebVTT) 타임스탬프 파서 — 외부 자막 파일을 샷 타임라인 · 단어 경계로 직접 변환한다.
///   ▸ 텍스트를 <see cref="ReadOnlySpan{T}"/> 로 1 회 줄 스캔 — 번호 줄 · "HH:mm:ss,fff --> HH:mm:ss,fff"(VTT 는 '.' · 시 생략 · 큐 설정 허용) · 본문(빈 줄까지).
///   ▸ 본문은 소유 대본 버퍼에 기록(&lt;i&gt; · &lt;v 화자&gt; 등 태그 제거 · 큐 사이 줄바꿈 = 샷 하드 컷) · 용량 내 재파싱 0 B.
///   ▸ 깨진 타임코드 · 역전/역순 구간은 해당 큐만 건너뛰고(<see cref="Skipped"/>) 다음 정상 큐부터 복구 — 예외 0.
///   ▸ <see cref="EmitTo"/>: 큐 구간 안을 쉼표/마침표 미세 구간 가중치로 분배해 단어 경계로 자동 등록(<see cref="InternalTtsWordBoundaryDirectEmitter"/>).
/// </summary>
public sealed class StandardSubtitleTimestampParser
{
    public const int MaxCues = StoryboardAudioWordBoundaryAligner.MaxWords;

    private readonly SubtitleCue[] _cues = new SubtitleCue[MaxCues];
    private char[] _script = new char[1024];
    private int _scriptLen;

    public SubtitleFormat Format { get; private set; }
    public int Count { get; private set; }
    /// <summary>건너뛴 깨진 큐 수(타임코드 구문 오류 · 역전 · 역순).</summary>
    public int Skipped { get; private set; }
    public ReadOnlySpan<SubtitleCue> Cues => _cues.AsSpan(0, Count);
    /// <summary>큐 본문 결합 대본(큐 사이 '\n').</summary>
    public ReadOnlySpan<char> Script => _script.AsSpan(0, _scriptLen);
    public ReadOnlySpan<char> TextOf(in SubtitleCue c) => _script.AsSpan(c.TextStart, c.TextLength);
    public int TotalMs => Count == 0 ? 0 : _cues[Count - 1].EndMs;

    /// <summary>"WEBVTT" 헤더면 WebVTT · 아니면 첫 타임코드 줄의 밀리초 구분자로 판정.</summary>
    public static SubtitleFormat Detect(ReadOnlySpan<char> text)
    {
        text = StripBom(text);
        if (text.StartsWith("WEBVTT", StringComparison.Ordinal)) return SubtitleFormat.WebVtt;
        var arrow = text.IndexOf("-->", StringComparison.Ordinal);
        if (arrow < 0) return SubtitleFormat.Unknown;
        var left = text[..arrow].TrimEnd();
        return left.Length >= 4 && left[^4] == ',' ? SubtitleFormat.SubRip : left.Length >= 4 && left[^4] == '.' ? SubtitleFormat.WebVtt : SubtitleFormat.Unknown;
    }

    /// <summary>
    /// 자막 텍스트 → 큐 · 대본(용량 내 0 B). 반환 = 정상 큐 수. 본문이 빈 큐는 버림 · 깨진 큐는 <see cref="Skipped"/> 집계 후 다음 빈 줄부터 복구.
    /// </summary>
    public int Parse(ReadOnlySpan<char> text)
    {
        text = StripBom(text);
        if (text.Length + 1 > _script.Length) _script = new char[Math.Max(text.Length + 1, _script.Length * 2)];
        Format = Detect(text);
        Count = Skipped = _scriptLen = 0;
        int pos = 0, prevStart = -1, seq = 0, prevNumber = -1;
        while (pos < text.Length && Count < MaxCues)
        {
            var line = NextLine(text, ref pos);
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
            {
                prevNumber = TryParseIndex(line);
                continue;
            }
            seq++;
            if (!TryParseTiming(line, arrow, out var start, out var end) || start < prevStart)
            {
                Skipped++;
                SkipBlock(text, ref pos);
                prevNumber = -1;
                continue;
            }
            var textStart = _scriptLen + (_scriptLen > 0 ? 1 : 0);
            var w = textStart;
            while (pos < text.Length)
            {
                var save = pos;
                var body = NextLine(text, ref pos);
                if (body.IsWhiteSpace()) break;
                if (body.IndexOf("-->", StringComparison.Ordinal) >= 0) { pos = save; break; }   // 빈 줄 누락 — 다음 큐로 복구
                if (w > textStart) _script[w++] = ' ';
                w = AppendStripped(body.Trim(), w, textStart);
            }
            while (w > textStart && _script[w - 1] == ' ') w--;
            if (w == textStart) { prevNumber = -1; continue; }
            if (textStart > 0) _script[textStart - 1] = '\n';
            ref var c = ref _cues[Count++];
            c.Index = prevNumber > 0 ? prevNumber : seq;
            c.StartMs = start;
            c.EndMs = end;
            c.TextStart = textStart;
            c.TextLength = w - textStart;
            _scriptLen = w;
            prevStart = start;
            prevNumber = -1;
        }
        return Count;
    }

    /// <summary>
    /// 큐 → 단어 경계 등록 — 직결기에 대본을 기록하고 큐마다 [StartMs, EndMs) 를 음절 + 쉼표/마침표 호흡 가중치로 분배. 반환 = 등록 단어 수(용량 내 0 B).
    /// </summary>
    public int EmitTo(InternalTtsWordBoundaryDirectEmitter emitter)
    {
        emitter.Begin(Script);
        var n = 0;
        foreach (ref readonly var c in Cues) n += emitter.EmitSpan(c.TextStart, c.TextLength, c.StartMs, c.EndMs);
        return n;
    }

    /// <summary>원자 정렬 — 파싱된 큐 → 단어 경계 → 바인더 직결 → 시퀀서 재동기화(0 B).</summary>
    public AudioAlignmentSource ApplyTo(InternalTtsWordBoundaryDirectEmitter emitter, StoryboardAudioSourceStreamBinder binder, StoryboardKeyframeSequencer seq)
    {
        EmitTo(emitter);
        return emitter.CommitAndResync(binder, seq);
    }

    /// <summary>타임코드 "[HH:]mm:ss[,.]fff"(시 자릿수 자유 · 밀리초 1~3 자리) → ms. 범위 밖 · 구문 오류 false.</summary>
    public static bool TryParseTimecode(ReadOnlySpan<char> s, out int ms)
    {
        ms = 0;
        s = s.Trim();
        int p = 0;
        if (!ReadInt(s, ref p, 9, out var a) || p >= s.Length || s[p++] != ':') return false;
        if (!ReadInt(s, ref p, 2, out var b)) return false;
        long h = 0, m = a, sec = b;
        if (p < s.Length && s[p] == ':')
        {
            p++;
            if (!ReadInt(s, ref p, 2, out var c)) return false;
            h = a; m = b; sec = c;
        }
        if (p >= s.Length || s[p] is not (',' or '.')) return false;
        var f0 = ++p;
        if (!ReadInt(s, ref p, 3, out var frac) || p != s.Length) return false;
        for (var d = p - f0; d < 3; d++) frac *= 10;
        if (m >= 60 || sec >= 60) return false;
        var total = ((h * 60 + m) * 60 + sec) * 1000 + frac;
        if (total > int.MaxValue) return false;
        ms = (int)total;
        return true;
    }

    /// <summary>타이밍 줄 "시작 --> 끝[ 큐 설정]" 파싱(끝 ≥ 시작).</summary>
    public static bool TryParseTiming(ReadOnlySpan<char> line, int arrow, out int startMs, out int endMs)
    {
        endMs = 0;
        if (!TryParseTimecode(line[..arrow], out startMs)) return false;
        var right = line[(arrow + 3)..].TrimStart();
        var sp = right.IndexOfAny(' ', '\t');
        if (sp >= 0) right = right[..sp];
        return TryParseTimecode(right, out endMs) && endMs >= startMs;
    }

    private static ReadOnlySpan<char> StripBom(ReadOnlySpan<char> s) => s.Length > 0 && s[0] == '﻿' ? s[1..] : s;

    private static ReadOnlySpan<char> NextLine(ReadOnlySpan<char> text, ref int pos)
    {
        var rest = text[pos..];
        var nl = rest.IndexOf('\n');
        var line = nl < 0 ? rest : rest[..nl];
        pos += nl < 0 ? rest.Length : nl + 1;
        return line.Length > 0 && line[^1] == '\r' ? line[..^1] : line;
    }

    private static void SkipBlock(ReadOnlySpan<char> text, ref int pos)
    {
        while (pos < text.Length)
        {
            var save = pos;
            var line = NextLine(text, ref pos);
            if (line.IsWhiteSpace()) return;
            if (line.IndexOf("-->", StringComparison.Ordinal) >= 0) { pos = save; return; }
        }
    }

    private static int TryParseIndex(ReadOnlySpan<char> line)
    {
        line = line.Trim();
        int p = 0;
        return line.Length > 0 && ReadInt(line, ref p, 9, out var v) && p == line.Length ? (int)v : -1;
    }

    private static bool ReadInt(ReadOnlySpan<char> s, ref int p, int maxDigits, out long v)
    {
        v = 0;
        var start = p;
        while (p < s.Length && p - start < maxDigits && (uint)(s[p] - '0') <= 9) v = v * 10 + (s[p++] - '0');
        return p > start;
    }

    /// <summary>본문 줄 기록 — "&lt;...&gt;" 태그 제거 · 제어 문자/줄바꿈 → 공백 · 연속 공백 1 개.</summary>
    private int AppendStripped(ReadOnlySpan<char> body, int w, int lineStart)
    {
        var inTag = false;
        foreach (var ch in body)
        {
            if (inTag) { if (ch == '>') inTag = false; continue; }
            if (ch == '<') { inTag = true; continue; }
            var c = char.IsWhiteSpace(ch) || char.IsControl(ch) ? ' ' : ch;
            if (c == ' ' && (w == lineStart || _script[w - 1] == ' ')) continue;
            _script[w++] = c;
        }
        return w;
    }
}
