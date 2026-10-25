using System.Buffers.Binary;
using System.Buffers.Text;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>[v18.2.1] 샷 타임코드 출처 — 음절 추정(폴백) · PCM 음성 구간(VAD) · TTS 단어 경계.</summary>
public enum AudioAlignmentSource : byte { SyllableEstimate, PcmVoiceActivity, TtsWordBoundary, AudioDurationScale }

/// <summary>[v18.2.1] PCM 음성 구간 1 개 [StartMs, EndMs) — 250 ms 미만 쉼은 구간 안으로 병합.</summary>
public struct AudioVoiceSegment
{
    public int StartMs, EndMs;
    public readonly int DurationMs => EndMs - StartMs;
}

/// <summary>[v18.2.1] TTS 단어 경계 1 개 — 시각 [StartMs, EndMs) · 단어는 원본 UTF-8 스트림 오프셋(복사 0).</summary>
public struct AudioWordBoundary
{
    public int StartMs, EndMs;
    public int TextStart, TextLength;
}

/// <summary>
/// [PromptFlow Studio v18.2.1] 🎙 무할당 오디오 단어 경계 · 침묵 감지 타임코드 정렬기 — v18.2.0 고정 음절 템포(180 ms) 추정을 실측으로 교체한다.
///   ▸ PCM 16-bit(원시 · WAV RIFF) 를 1 ms 프레임 평균 진폭으로 1 회 스캔 → 음성 구간 · <see cref="MinSilenceMs"/> 이상 무음 = 샷 경계 후보.
///   ▸ 샷 경계(누적 음절 비율 위치)를 가장 가까운 무음 뒤 발화 시작점에 단조 스냅 — 1 ms 정밀 타임코드 · 후보가 없으면 비례 추정.
///   ▸ TTS 단어 경계 스트림(UTF-8 줄 "startMs,endMs,word" · 탭 구분 허용) → 대본 단어 순번 매핑으로 샷 시작/종료 산출.
///   ▸ 오디오가 비었거나 형식 불일치 · 음성 0 이면 음절 추정치를 그대로 유지(폴백). 출력은 호출자 Span(0 B).
/// </summary>
public static class StoryboardAudioWordBoundaryAligner
{
    public const int MinSilenceMs = 250;
    /// <summary>음성 판정 1 ms 프레임 평균 |진폭| 하한(16-bit · ≈ -35 dBFS).</summary>
    public const int DefaultThreshold = 600;
    /// <summary>이보다 짧은 음성 구간은 잡음(클릭 · 숨소리)으로 버린다.</summary>
    public const int MinVoiceMs = 20;
    public const int MaxSegments = 256, MaxWords = 1024;
    public const int DefaultSampleRate = 16_000;

    /// <summary>WAV(RIFF/WAVE · PCM 16-bit) → data 청크 슬라이스. 형식이 다르면 false.</summary>
    public static bool TryReadWav(ReadOnlySpan<byte> wav, out ReadOnlySpan<byte> pcm, out int sampleRate, out int channels)
    {
        pcm = default; sampleRate = 0; channels = 0;
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return false;
        var bits = 0;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = wav.Slice(pos, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4)), (uint)(wav.Length - pos - 8));
            var body = wav.Slice(pos + 8, size);
            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                var format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                if (format != 1 && format != 0xFFFE) return false;
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                pcm = body;
                return bits == 16 && channels > 0 && sampleRate > 0;
            }
            pos += 8 + size + (size & 1);
        }
        return false;
    }

    /// <summary>
    /// PCM 16-bit LE(인터리브) → 음성 구간(시간 순). 1 ms 프레임(샘플 경계 = f × rate / 1000) 평균 |진폭| ≥ 임계 = 음성.
    ///   <paramref name="minSilenceMs"/> 미만 무음은 구간 안으로 병합 · <see cref="MinVoiceMs"/> 미만 구간은 버림. <paramref name="totalMs"/> = 오디오 길이.
    /// </summary>
    public static int DetectVoice(ReadOnlySpan<byte> pcm16, int sampleRate, int channels, Span<AudioVoiceSegment> dst, out int totalMs,
                                  int threshold = DefaultThreshold, int minSilenceMs = MinSilenceMs)
    {
        totalMs = 0;
        if (sampleRate <= 0 || channels <= 0) return 0;
        var frames = pcm16.Length / (2 * channels);
        totalMs = (int)((long)frames * 1000 / sampleRate);
        int n = 0, open = -1, lastVoice = -1;
        for (var f = 0; f < totalMs && n < dst.Length; f++)
        {
            var s0 = (int)((long)f * sampleRate / 1000) * channels;
            var s1 = (int)((long)(f + 1) * sampleRate / 1000) * channels;
            long sum = 0;
            for (var s = s0; s < s1; s++) sum += Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice(s * 2, 2)));
            if (s1 > s0 && sum >= (long)threshold * (s1 - s0))
            {
                if (open < 0) open = f;
                lastVoice = f;
            }
            else if (open >= 0 && f - lastVoice >= minSilenceMs)
            {
                Close(dst, ref n, open, lastVoice + 1);
                open = -1;
            }
        }
        if (open >= 0 && n < dst.Length) Close(dst, ref n, open, lastVoice + 1);
        return n;
    }

    /// <summary>TTS 단어 경계 스트림(UTF-8 · 줄당 "startMs,endMs,word") 파싱. 헤더/깨진 줄 · 문장 부호만인 단어는 건너뜀.</summary>
    public static int ParseWordBoundaries(ReadOnlySpan<byte> utf8, Span<AudioWordBoundary> dst)
    {
        int n = 0, pos = 0;
        while (pos < utf8.Length && n < dst.Length)
        {
            var nl = utf8[pos..].IndexOf((byte)'\n');
            var lineLen = nl < 0 ? utf8.Length - pos : nl;
            var line = utf8.Slice(pos, lineLen);
            var lineStart = pos;
            pos += lineLen + 1;
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (!Utf8Parser.TryParse(line, out int start, out var c1) || c1 >= line.Length || !IsSep(line[c1])) continue;
            var rest = line[(c1 + 1)..];
            if (!Utf8Parser.TryParse(rest, out int end, out var c2) || c2 >= rest.Length || !IsSep(rest[c2])) continue;
            var wordOff = c1 + 1 + c2 + 1;
            var word = line[wordOff..].Trim((byte)' ');
            if (end < start || !HasWordByte(word)) continue;
            ref var w = ref dst[n++];
            w.StartMs = start;
            w.EndMs = end;
            w.TextStart = lineStart + wordOff + line[wordOff..].IndexOf(word);
            w.TextLength = word.Length;
        }
        return n;
    }

    /// <summary>
    /// 오디오 버퍼(WAV 또는 원시 PCM 16-bit 모노 @ <paramref name="sampleRate"/>) 로 샷 타임코드 정렬. 비었거나 음성 0 이면 폴백(샷 불변).
    /// </summary>
    public static AudioAlignmentSource AlignToAudio(Span<StoryboardShot> shots, ReadOnlySpan<byte> audio, Span<AudioVoiceSegment> scratch,
                                                    int sampleRate = DefaultSampleRate, int threshold = DefaultThreshold, int channels = 1)
    {
        if (shots.IsEmpty || audio.IsEmpty) return AudioAlignmentSource.SyllableEstimate;
        if (audio.Length >= 4 && audio[..4].SequenceEqual("RIFF"u8) && !TryReadWav(audio, out audio, out sampleRate, out channels))
            return AudioAlignmentSource.SyllableEstimate;
        var m = DetectVoice(audio, sampleRate, channels, scratch, out var totalMs, threshold);
        return AlignToVoice(shots, scratch[..m], totalMs);
    }

    /// <summary>
    /// 음성 구간 → 샷 경계 스냅. 경계 k 의 추정 위치 = 발화 구간 × 누적 음절 비율 · 남은 경계 수만큼 무음을 남기며 가장 가까운 무음 뒤 발화 시작점 선택
    ///   (다음 경계 추정이 더 가까운 무음은 양보). 첫 샷 = 첫 발화 시작 · 마지막 샷 끝 = 오디오 길이 · 발화 길이 = 샷 안 음성 ms → 앵글 재판정.
    /// </summary>
    public static AudioAlignmentSource AlignToVoice(Span<StoryboardShot> shots, ReadOnlySpan<AudioVoiceSegment> voice, int totalMs)
    {
        int n = shots.Length, m = voice.Length;
        if (n == 0 || m == 0) return AudioAlignmentSource.SyllableEstimate;
        var totalSyl = 0;
        foreach (ref readonly var s in shots) totalSyl += Math.Max(1, s.Syllables);
        int v0 = voice[0].StartMs, v1 = voice[m - 1].EndMs;
        int prevStart = v0, prevGap = -1, cumSyl = 0;
        shots[0].StartMs = v0;
        for (var k = 0; k < n - 1; k++)
        {
            cumSyl += Math.Max(1, shots[k].Syllables);
            var est = Estimate(v0, v1, cumSyl, totalSyl);
            var nextEst = k + 1 < n - 1 ? Estimate(v0, v1, cumSyl + Math.Max(1, shots[k + 1].Syllables), totalSyl) : int.MaxValue;
            var gaps = m - 1;
            var hi = gaps >= n - 1 ? gaps - 1 - (n - 2 - k) : gaps - 1;
            int best = -1, bestDist = int.MaxValue;
            for (var j = prevGap + 1; j <= hi; j++)
            {
                var mid = (voice[j].EndMs + voice[j + 1].StartMs) >> 1;
                var d = Math.Abs(mid - est);
                if (d < bestDist) { bestDist = d; best = j; }
            }
            int cut;
            if (best >= 0 && (gaps >= n - 1 || bestDist <= Math.Abs(((voice[best].EndMs + voice[best + 1].StartMs) >> 1) - nextEst)))
            {
                cut = voice[best + 1].StartMs;
                prevGap = best;
            }
            else cut = est;
            cut = Math.Max(cut, prevStart + 1);
            shots[k].EndMs = cut;
            shots[k + 1].StartMs = cut;
            prevStart = cut;
        }
        shots[n - 1].EndMs = Math.Max(Math.Max(totalMs, v1), prevStart + 1);
        for (var i = 0; i < n; i++)
        {
            ref var s = ref shots[i];
            s.SpeechMs = VoicedMs(voice, s.StartMs, s.EndMs);
            s.Angle = StoryboardShotBreakdownEngine.ChooseAngle(i, s.Terminator, s.SpeechMs);
        }
        return AudioAlignmentSource.PcmVoiceActivity;
    }

    /// <summary>
    /// TTS 단어 경계 → 샷 시작/종료. 샷 i 의 첫 단어 순번 = 누적 대본 단어 수(TTS 단어 수와 다르면 비례 환산) · 샷 끝 = 다음 샷 첫 단어 시작
    ///   · 마지막 샷 끝 = 마지막 단어 끝 · 발화 길이 = 샷 마지막 단어 끝 − 시작 → 앵글 재판정.
    /// </summary>
    public static AudioAlignmentSource AlignToWords(Span<StoryboardShot> shots, ReadOnlySpan<char> script, ReadOnlySpan<AudioWordBoundary> words)
    {
        int n = shots.Length, w = words.Length;
        if (n == 0 || w == 0) return AudioAlignmentSource.SyllableEstimate;
        var scriptWords = 0;
        foreach (ref readonly var s in shots) scriptWords += Math.Max(1, CountWords(s.Text(script)));
        var cum = 0;
        var prevStart = -1;
        for (var i = 0; i < n; i++)
        {
            ref var s = ref shots[i];
            var first = Math.Min(w - 1, (int)((long)cum * w / scriptWords));
            cum += Math.Max(1, CountWords(s.Text(script)));
            var last = Math.Max(first, Math.Min(w - 1, (int)((long)cum * w / scriptWords) - 1));
            s.StartMs = Math.Max(words[first].StartMs, prevStart + 1);
            s.SpeechMs = Math.Max(1, words[last].EndMs - s.StartMs);
            if (i > 0) shots[i - 1].EndMs = s.StartMs;
            prevStart = s.StartMs;
        }
        shots[n - 1].EndMs = Math.Max(words[w - 1].EndMs, prevStart + 1);
        for (var i = 0; i < n; i++)
        {
            ref var s = ref shots[i];
            s.SpeechMs = Math.Min(s.SpeechMs, s.DurationMs);
            s.Angle = StoryboardShotBreakdownEngine.ChooseAngle(i, s.Terminator, s.SpeechMs);
        }
        return AudioAlignmentSource.TtsWordBoundary;
    }

    /// <summary>대본 단어 수(공백 구분 · 글자/숫자가 1 개 이상인 토큰만).</summary>
    public static int CountWords(ReadOnlySpan<char> text)
    {
        int n = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c)) { inWord = false; continue; }
            if (!inWord && char.IsLetterOrDigit(c)) { n++; inWord = true; }
        }
        return n;
    }

    /// <summary>[a, b) 와 겹치는 음성 ms 합.</summary>
    public static int VoicedMs(ReadOnlySpan<AudioVoiceSegment> voice, int a, int b)
    {
        var sum = 0;
        foreach (ref readonly var v in voice)
        {
            if (v.StartMs >= b) break;
            sum += Math.Max(0, Math.Min(b, v.EndMs) - Math.Max(a, v.StartMs));
        }
        return sum;
    }

    private static int Estimate(int v0, int v1, int cumSyl, int totalSyl) => v0 + (int)((long)(v1 - v0) * cumSyl / totalSyl);

    private static void Close(Span<AudioVoiceSegment> dst, ref int n, int start, int end)
    {
        if (end - start < MinVoiceMs) return;
        ref var v = ref dst[n++];
        v.StartMs = start;
        v.EndMs = end;
    }

    private static bool IsSep(byte b) => b is (byte)',' or (byte)'\t';

    /// <summary>ASCII 글자/숫자 또는 멀티바이트(한글 등) 1 개 이상.</summary>
    private static bool HasWordByte(ReadOnlySpan<byte> word)
    {
        foreach (var b in word)
            if (b >= 0x80 || (uint)((b | 0x20) - 'a') <= 'z' - 'a' || (uint)(b - '0') <= 9) return true;
        return false;
    }
}
