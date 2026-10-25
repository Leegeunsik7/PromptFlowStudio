using System.Buffers.Binary;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>[v18.2.2] 오디오 바인딩 결과.</summary>
public enum AudioBindStatus : byte { None, Bound, Empty, Unsupported, TooLarge }

/// <summary>[v18.2.2] 바인딩된 오디오 컨테이너 형식.</summary>
public enum AudioContainer : byte { None, Wav, RawPcm16, Mp3 }

/// <summary>
/// [v18.2.2] 스토리보드 실 오디오 스냅샷 — PCM 16-bit LE 인터리브(WAV 는 data 청크 슬라이스) · 바인더 버퍼를 <see cref="ReadOnlyMemory{T}"/> 로 공유(복사 0).
///   <see cref="Revision"/> = 캡처 시점 바인더 번호 — 이후 재바인딩으로 버퍼가 덮이면 <see cref="IsLive"/> 가 false.
///   [v18.2.4] MP3 는 <see cref="Pcm"/> = 오디오 프레임 비트스트림(<see cref="IsPcm"/> = false) · <see cref="Loudness"/> = 노멀라이저 실측 게인 메타데이터.
/// </summary>
public readonly struct StoryboardAudioTrack
{
    public readonly ReadOnlyMemory<byte> Pcm;
    public readonly int SampleRate, Channels, DurationMs, Revision;
    public readonly AudioContainer Container;
    public readonly StoryboardAudioSourceStreamBinder? Owner;
    public readonly LufsLoudnessReport Loudness;

    public StoryboardAudioTrack(ReadOnlyMemory<byte> pcm, int sampleRate, int channels, int durationMs, int revision, AudioContainer container,
                                StoryboardAudioSourceStreamBinder? owner, LufsLoudnessReport loudness = default)
    {
        Pcm = pcm; SampleRate = sampleRate; Channels = channels; DurationMs = durationMs; Revision = revision; Container = container; Owner = owner;
        Loudness = loudness;
    }

    public bool IsEmpty => Pcm.IsEmpty;
    /// <summary>[v18.2.4] PCM 16-bit 샘플 버퍼인지(MP3 비트스트림 = false → 샘플 소비 불가).</summary>
    public bool IsPcm => Container is AudioContainer.Wav or AudioContainer.RawPcm16;
    /// <summary>[v18.2.4] 숏폼 팩토리 내레이션 적용 게인(−14 LUFS · −1 dBTP 보정 · 미계측 = 1).</summary>
    public float GainFactor => Loudness.GainFactor;
    /// <summary>공유 버퍼가 아직 캡처 시점 오디오인지(빈 트랙 · 소유자 없는 외부 버퍼는 항상 true).</summary>
    public bool IsLive => Owner is null || Owner.Revision == Revision;
    public int Frames => Channels <= 0 || !IsPcm ? 0 : Pcm.Length / (2 * Channels);

    /// <summary>목표 레이트 <paramref name="dstRate"/> 의 샘플 <paramref name="index"/> → 원본 최근접 프레임 채널 평균(−1 ~ 1 · 범위 밖 0 · 0 B).</summary>
    public static float SampleAt(ReadOnlySpan<byte> pcm16, int sampleRate, int channels, int dstRate, long index)
    {
        if (sampleRate <= 0 || channels <= 0 || dstRate <= 0 || index < 0) return 0f;
        var frame = index * sampleRate / dstRate;
        var off = frame * channels * 2;
        if (off + channels * 2 > pcm16.Length) return 0f;
        var sum = 0;
        for (var c = 0; c < channels; c++) sum += BinaryPrimitives.ReadInt16LittleEndian(pcm16.Slice((int)off + c * 2, 2));
        return sum * (1f / 32768f) / channels;
    }
}

/// <summary>
/// [PromptFlow Studio v18.2.2] 🎙 실기 오디오 · TTS 단어 경계 스트림 바인더 — 스토리보드 샷 타임코드를 음절 추정에서 실측으로 전환한다.
///   ▸ <see cref="Bind"/>: WAV(RIFF/WAVE · PCM 16-bit) 헤더 파싱 또는 원시 PCM 16-bit → 소유 버퍼 1 회 기록(용량 내 재바인딩 0 B) · 레이트/채널/길이 산출.
///   ▸ <see cref="BindWordBoundaries"/>: TTS 단어 경계 UTF-8 스트림("startMs,endMs,word" 줄)을 소유 버퍼에 기록 — 오디오보다 우선.
///   ▸ <see cref="Resync"/>: 단어 경계 → 음성 구간(VAD) → 실측 길이 비례 순으로 시퀀서 재동기화(0 B). 오디오 · 단어 경계가 모두 없을 때만 음절 추정.
///   ▸ <see cref="Track"/>: 숏폼 팩토리 관통용 0-카피 스냅샷.
///   ▸ [v18.2.4] MP3(ID3v2 · Layer III 싱크) → <see cref="Mp3FrameHeaderBitstreamParser"/> 실측 길이 · 프레임 비트스트림 바인딩(음절 추정 배제 · 실측 길이 비례 재동기화).
///     PCM 은 바인딩 시 <see cref="LufsLoudnessComplianceNormalizer"/> 로 −14 LUFS 게인 실측(0 B) → 트랙 메타데이터로 관통.
/// </summary>
public sealed class StoryboardAudioSourceStreamBinder
{
    /// <summary>바인딩 상한(64 MB ≈ 48 kHz 스테레오 16-bit 5.8 분).</summary>
    public const int MaxBytes = 64 << 20;

    private byte[] _buf = Array.Empty<byte>();
    private byte[] _wordBuf = Array.Empty<byte>();
    private int _len, _wordLen, _dataOffset, _dataLength;

    public AudioBindStatus Status { get; private set; }
    public AudioContainer Container { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int DurationMs { get; private set; }
    public int Revision { get; private set; }
    public string? SourceName { get; private set; }
    public bool HasAudio => Status == AudioBindStatus.Bound;
    /// <summary>[v18.2.4] PCM 샘플 오디오 바인딩(VAD · 라우드니스 계측 가능) — MP3 비트스트림은 false.</summary>
    public bool HasPcm => HasAudio && Container != AudioContainer.Mp3;
    /// <summary>[v18.2.4] 마지막 MP3 분석 결과(MP3 아님 = default).</summary>
    public Mp3StreamInfo Mp3 { get; private set; }
    /// <summary>[v18.2.4] 라우드니스 실측 · −14 LUFS 보정 게인(MP3 · 미바인딩 = 미계측).</summary>
    public LufsLoudnessReport Loudness { get; private set; }
    public bool HasWordBoundaries => _wordLen > 0;
    /// <summary>원본 바이트(WAV 헤더 포함) · PCM 데이터 슬라이스(MP3 = 오디오 프레임 구간) · 단어 경계 스트림.</summary>
    public ReadOnlySpan<byte> Raw => _buf.AsSpan(0, _len);
    public ReadOnlySpan<byte> Pcm => _buf.AsSpan(_dataOffset, _dataLength);
    public ReadOnlySpan<byte> WordBoundaries => _wordBuf.AsSpan(0, _wordLen);

    /// <summary>숏폼 팩토리 관통 스냅샷 — 미바인딩이면 빈 트랙.</summary>
    public StoryboardAudioTrack Track => HasAudio
        ? new StoryboardAudioTrack(_buf.AsMemory(_dataOffset, _dataLength), SampleRate, Channels, DurationMs, Revision, Container, this, Loudness)
        : default;

    /// <summary>
    /// 오디오 바이트 바인딩 — "RIFF" 로 시작하면 WAV 파싱, ID3v2 · Layer III 싱크(또는 .mp3 이름)면 MP3 프레임 분석, 아니면 원시 PCM 16-bit(<paramref name="rawSampleRate"/> · <paramref name="rawChannels"/>).
    ///   용량 내 재호출 0 B. 실패 시 이전 오디오를 비우고 상태만 기록.
    /// </summary>
    public AudioBindStatus Bind(ReadOnlySpan<byte> audio, int rawSampleRate = StoryboardAudioWordBoundaryAligner.DefaultSampleRate, int rawChannels = 1, string? name = null)
    {
        if (audio.Length > MaxBytes) return Fail(AudioBindStatus.TooLarge);
        if (audio.Length > _buf.Length) _buf = new byte[Math.Max(audio.Length, Math.Min(MaxBytes, _buf.Length * 2))];
        audio.CopyTo(_buf);
        _len = audio.Length;
        SourceName = name;
        return Parse(rawSampleRate, rawChannels);
    }

    /// <summary>스트림(파일 · 네트워크) → 소유 버퍼 직접 수신 후 <see cref="Bind"/> 와 같은 파싱(비핫패스 · 용량 내 재수신 0 B 버퍼).</summary>
    public async Task<AudioBindStatus> BindAsync(Stream stream, long lengthHint, string? name = null,
                                                 int rawSampleRate = StoryboardAudioWordBoundaryAligner.DefaultSampleRate, int rawChannels = 1, CancellationToken ct = default)
    {
        if (lengthHint > MaxBytes) return Fail(AudioBindStatus.TooLarge);
        var cap = (int)Math.Max(lengthHint, 4096);
        if (cap > _buf.Length) _buf = new byte[cap];
        var len = 0;
        while (true)
        {
            if (len == _buf.Length)
            {
                if (len >= MaxBytes) return Fail(AudioBindStatus.TooLarge);
                var grown = new byte[Math.Min(MaxBytes, len * 2)];
                _buf.AsSpan(0, len).CopyTo(grown);
                _buf = grown;
            }
            var r = await stream.ReadAsync(_buf.AsMemory(len), ct).ConfigureAwait(false);
            if (r == 0) break;
            len += r;
        }
        _len = len;
        SourceName = name;
        return Parse(rawSampleRate, rawChannels);
    }

    /// <summary>TTS 단어 경계 스트림 기록(용량 내 0 B) · 단어가 없으면 해제. 반환 = 파싱 가능한 단어 수(최대 64 개까지 확인).</summary>
    public int BindWordBoundaries(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > _wordBuf.Length) _wordBuf = new byte[Math.Max(utf8.Length, _wordBuf.Length * 2)];
        utf8.CopyTo(_wordBuf);
        _wordLen = utf8.Length;
        Revision++;
        Span<AudioWordBoundary> probe = stackalloc AudioWordBoundary[64];
        var n = StoryboardAudioWordBoundaryAligner.ParseWordBoundaries(WordBoundaries, probe);
        if (n == 0) _wordLen = 0;
        return n;
    }

    public void Clear()
    {
        _len = _wordLen = _dataOffset = _dataLength = 0;
        Status = AudioBindStatus.None;
        Container = AudioContainer.None;
        SampleRate = Channels = DurationMs = 0;
        Mp3 = default;
        Loudness = default;
        SourceName = null;
        Revision++;
    }

    /// <summary>
    /// 시퀀서 재동기화(0 B) — 단어 경계 → 음성 구간(VAD) → 실측 길이 비례. 오디오 · 단어 경계가 모두 없을 때만 음절 추정.
    ///   반환 = 적용된 타임코드 출처(오디오가 바인딩돼 있으면 절대 <see cref="AudioAlignmentSource.SyllableEstimate"/> 가 아님 · 대본이 비면 예외적으로 추정).
    /// </summary>
    public AudioAlignmentSource Resync(StoryboardKeyframeSequencer seq, ReadOnlySpan<char> script)
    {
        if (HasWordBoundaries)
        {
            seq.LoadWithWordBoundaries(script, WordBoundaries);
            if (seq.Alignment != AudioAlignmentSource.SyllableEstimate || !HasAudio) return seq.Alignment;
        }
        if (!HasAudio)
        {
            seq.Load(script);
            return seq.Alignment;
        }
        if (!HasPcm)
        {
            seq.LoadWithDuration(script, DurationMs);   // [v18.2.4] MP3 = 프레임 실측 길이 비례(VAD 는 PCM 전용)
            return seq.Alignment;
        }
        seq.LoadWithAudio(script, Pcm, SampleRate, Channels);
        if (seq.Alignment == AudioAlignmentSource.SyllableEstimate) seq.LoadWithDuration(script, DurationMs);
        return seq.Alignment;
    }

    private AudioBindStatus Parse(int rawSampleRate, int rawChannels)
    {
        Revision++;
        var raw = Raw;
        Mp3 = default;
        Loudness = default;
        if (raw.IsEmpty) return Fail(AudioBindStatus.Empty);
        int rate, ch;
        if (!(raw.Length >= 4 && raw[..4].SequenceEqual("RIFF"u8)) && Mp3FrameHeaderBitstreamParser.LooksLikeMp3(raw))
        {
            // ID3v2 · .mp3 이름 = MP3 확정(실패 = Unsupported) · 맨 싱크만 일치하면 분석 성공 시에만 MP3(아니면 원시 PCM)
            var sure = raw[0] == (byte)'I' || SourceName is { } n && n.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
            var st = ParseMp3(raw);
            if (st == AudioBindStatus.Bound) return st;
            if (sure) return Fail(st);
        }
        else if (SourceName is { } m && m.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            var st = ParseMp3(raw);
            return st == AudioBindStatus.Bound ? st : Fail(st);
        }
        if (raw.Length >= 4 && raw[..4].SequenceEqual("RIFF"u8))
        {
            if (!StoryboardAudioWordBoundaryAligner.TryReadWav(raw, out var data, out rate, out ch)) return Fail(AudioBindStatus.Unsupported);
            _dataOffset = (int)ByteOffsetOf(raw, data);
            _dataLength = data.Length;
            Container = AudioContainer.Wav;
        }
        else
        {
            rate = rawSampleRate; ch = rawChannels;
            if (rate <= 0 || ch <= 0) return Fail(AudioBindStatus.Unsupported);
            _dataOffset = 0;
            _dataLength = raw.Length & ~1;
            Container = AudioContainer.RawPcm16;
        }
        var frames = _dataLength / (2 * ch);
        if (frames == 0) return Fail(AudioBindStatus.Empty);
        _dataLength = frames * 2 * ch;
        SampleRate = rate;
        Channels = ch;
        DurationMs = (int)((long)frames * 1000 / rate);
        Loudness = LufsLoudnessComplianceNormalizer.Measure(Pcm, rate, ch);
        Status = AudioBindStatus.Bound;
        return Status;
    }

    /// <summary>[v18.2.4] MP3 — 프레임 헤더 실측(0 B) · 오디오 프레임 구간 = 바인딩 버퍼 슬라이스 · 라우드니스는 미계측(디코딩 없음 → 게인 1). 실패 시 상태 불변(호출자가 판단).</summary>
    private AudioBindStatus ParseMp3(ReadOnlySpan<byte> raw)
    {
        var info = Mp3FrameHeaderBitstreamParser.Analyze(raw);
        if (!info.IsOk || info.DurationMs <= 0) return AudioBindStatus.Unsupported;
        Mp3 = info;
        _dataOffset = info.DataOffset;
        _dataLength = info.DataLength;
        Container = AudioContainer.Mp3;
        SampleRate = info.SampleRate;
        Channels = info.Channels;
        DurationMs = info.DurationMs;
        Status = AudioBindStatus.Bound;
        return Status;
    }

    /// <summary>슬라이스의 부모 버퍼 내 바이트 오프셋.</summary>
    private static long ByteOffsetOf(ReadOnlySpan<byte> parent, ReadOnlySpan<byte> child) =>
        System.Runtime.CompilerServices.Unsafe.ByteOffset(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(parent),
                                                          ref System.Runtime.InteropServices.MemoryMarshal.GetReference(child));

    /// <summary>실패 — 오디오 해제 · 버퍼가 덮였을 수 있으므로 번호 증가(이전 스냅샷 무효화).</summary>
    private AudioBindStatus Fail(AudioBindStatus st)
    {
        Revision++;
        _len = 0;
        _dataOffset = _dataLength = 0;
        Container = AudioContainer.None;
        SampleRate = Channels = DurationMs = 0;
        Mp3 = default;
        Loudness = default;
        Status = st;
        return st;
    }
}
