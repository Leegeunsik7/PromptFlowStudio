using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>[v18.2.5] .storyv 판독 결과 — 정상 · 빈 입력 · 구문 오류 · 미지원 버전 · 범위/연속성 위반 · 지문 불일치 · 크롭 트랙 불일치 · 용량 초과 · I/O 오류.</summary>
public enum StoryvManifestStatus : byte { None, Ok, Empty, Malformed, UnsupportedVersion, OutOfRange, FingerprintMismatch, CropMismatch, TooLarge, IoError }

/// <summary>
/// [v18.2.5] 매니페스트 오디오 헤더(PCM 본문 제외) — 컨테이너 · 레이트 · 채널 · 길이 · 라우드니스 리포트.
///   [v18.2.6] + 원본 절대 경로 · SHA-256 64-bit 체크섬(자동 재연결 앵커 · 경로 없음 = null).
/// </summary>
public readonly struct StoryvAudioHeader
{
    public readonly AudioContainer Container;
    public readonly int SampleRate, Channels, DurationMs;
    public readonly LufsLoudnessReport Loudness;
    public readonly string? SourcePath;
    public readonly ulong Checksum;

    public StoryvAudioHeader(AudioContainer container, int sampleRate, int channels, int durationMs, LufsLoudnessReport loudness,
                             string? sourcePath = null, ulong checksum = 0)
    {
        Container = container; SampleRate = sampleRate; Channels = channels; DurationMs = durationMs; Loudness = loudness;
        SourcePath = sourcePath; Checksum = checksum;
    }

    /// <summary>트랙 → 헤더(경로 · 체크섬은 캡처 시점 바인더가 아직 같은 오디오일 때만).</summary>
    public static StoryvAudioHeader From(in StoryboardAudioTrack t) => t.Owner is { } o && !t.IsEmpty && t.IsLive
        ? new(t.Container, t.SampleRate, t.Channels, t.DurationMs, t.Loudness, o.SourcePath, o.SourceChecksum)
        : new(t.Container, t.SampleRate, t.Channels, t.DurationMs, t.Loudness);
    public bool IsEmpty => Container == AudioContainer.None;
}

/// <summary>
/// [PromptFlow Studio v18.2.5] 💾 스토리보드 시퀀스 프로젝트 매니페스트(.storyv) 영속화 엔진 — BCL <see cref="Utf8JsonWriter"/>/<see cref="Utf8JsonReader"/> 만 사용.
///   ▸ <see cref="Write"/>: 샷 메타(타임코드 · 앵글 · 9:16 프레이밍 · 대본 오프셋) · 9:16 크롭 키프레임 시계열 · 대본 · 오디오 헤더 · 지문을 단일 UTF-8 JSON 으로 직렬화
///     (출력 버퍼 · 라이터 재사용 · 속성명 사전 인코딩 → 용량 내 0 B).
///   ▸ <see cref="Read"/>/<see cref="Verify"/>: 스택 리더로 스캔 → 스크래치 버퍼에 검증(범위 · 시간 연속성 · 대본 오프셋 · 지문 재계산 · 크롭 트랙 = 샷 프레이밍) → 통과 시에만 시퀀서에 원자 복원.
///   ▸ <see cref="SaveAsync"/>/<see cref="LoadAsync(string, StoryboardKeyframeSequencer, CancellationToken)"/>: 임시 파일 기록 후 원자 교체 · 재판독 무결성 검증.
///   ▸ [v18.2.6] 오디오 헤더에 원본 절대 경로(audioSourcePath) · SHA-256 64-bit 체크섬(audioChecksum) 기록 → <see cref="StoryboardAudioSourceStreamBinder.HydrateAsync(StoryvAudioHeader, CancellationToken)"/> 자동 재연결.
///     구 매니페스트(필드 없음)는 경로 null 로 판독(상위 호환 · 버전 불변).
/// </summary>
public sealed class StoryboardSequenceManifestEngine
{
    public const string Extension = ".storyv";
    public const string Format = "storyv";
    public const int Version = 1;
    public const int MaxBytes = 4 << 20;
    public const int MaxScriptChars = 64 * 1024;

    private static readonly JsonEncodedText PFormat = JsonEncodedText.Encode("format"), PVersion = JsonEncodedText.Encode("version"),
        PFingerprint = JsonEncodedText.Encode("fingerprint"), PTotal = JsonEncodedText.Encode("totalMs"), PAlignment = JsonEncodedText.Encode("alignment"),
        PScript = JsonEncodedText.Encode("script"), PAudio = JsonEncodedText.Encode("audio"), PShots = JsonEncodedText.Encode("shots"), PCrop = JsonEncodedText.Encode("cropTrack"),
        PContainer = JsonEncodedText.Encode("container"), PRate = JsonEncodedText.Encode("sampleRate"), PChannels = JsonEncodedText.Encode("channels"),
        PDuration = JsonEncodedText.Encode("durationMs"), PMeasured = JsonEncodedText.Encode("lufsMeasured"), PIntegrated = JsonEncodedText.Encode("integratedLufs"),
        PTarget = JsonEncodedText.Encode("targetLufs"), PPeak = JsonEncodedText.Encode("truePeakDbtp"), PGain = JsonEncodedText.Encode("gainDb"),
        PLimited = JsonEncodedText.Encode("peakLimited"), PBlocks = JsonEncodedText.Encode("gatedBlocks"),
        PSourcePath = JsonEncodedText.Encode("audioSourcePath"), PChecksum = JsonEncodedText.Encode("audioChecksum"),
        PN = JsonEncodedText.Encode("n"), PStart = JsonEncodedText.Encode("start"), PEnd = JsonEncodedText.Encode("end"), PDur = JsonEncodedText.Encode("dur"),
        PAngle = JsonEncodedText.Encode("angle"), PCx = JsonEncodedText.Encode("cx"), PCy = JsonEncodedText.Encode("cy"), PX = JsonEncodedText.Encode("x"),
        PY = JsonEncodedText.Encode("y"), PW = JsonEncodedText.Encode("w"), PH = JsonEncodedText.Encode("h"), PTs = JsonEncodedText.Encode("ts"), PTl = JsonEncodedText.Encode("tl"),
        VFormat = JsonEncodedText.Encode(Format);

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, SkipValidation = false };

    private readonly ArrayBufferWriter<byte> _out = new(16 * 1024);
    private readonly Utf8JsonWriter _w;
    private readonly StoryboardKeyframe[] _keys = new StoryboardKeyframe[StoryboardKeyframeSequencer.MaxShots];
    private readonly ShotCropKeyframe[] _crop = new ShotCropKeyframe[StoryboardKeyframeSequencer.MaxShots];
    private char[] _script = new char[1024];
    private byte[] _file = new byte[16 * 1024];
    private int _keyCount, _cropCount, _scriptLen;

    public StoryboardSequenceManifestEngine() => _w = new Utf8JsonWriter(_out, WriterOptions);

    /// <summary>마지막 <see cref="Write"/> 출력(엔진 버퍼 슬라이스 · 다음 Write 까지 유효).</summary>
    public ReadOnlyMemory<byte> Output => _out.WrittenMemory;
    public StoryvManifestStatus LastStatus { get; private set; }
    /// <summary>마지막 판독 매니페스트의 지문 · 샷 수 · 정렬 출처 · 오디오 헤더.</summary>
    public ulong LastFingerprint { get; private set; }
    public int LastShots { get; private set; }
    public AudioAlignmentSource LastAlignment { get; private set; }
    public StoryvAudioHeader LastAudio { get; private set; }
    public long Writes { get; private set; }
    public long Restores { get; private set; }

    /// <summary>시퀀서 상태(+ 오디오 헤더) → .storyv UTF-8 JSON(용량 내 0 B). 반환 = <see cref="Output"/>.</summary>
    public ReadOnlyMemory<byte> Write(StoryboardKeyframeSequencer seq, in StoryboardAudioTrack audio = default) => Write(seq, StoryvAudioHeader.From(audio));

    public ReadOnlyMemory<byte> Write(StoryboardKeyframeSequencer seq, in StoryvAudioHeader audio)
    {
        _out.ResetWrittenCount();
        _w.Reset(_out);
        var w = _w;
        w.WriteStartObject();
        w.WriteString(PFormat, VFormat);
        w.WriteNumber(PVersion, Version);
        w.WriteNumber(PFingerprint, seq.Fingerprint);
        w.WriteNumber(PTotal, seq.TotalMs);
        w.WriteNumber(PAlignment, (int)seq.Alignment);
        w.WriteString(PScript, seq.Script);

        w.WriteStartObject(PAudio);
        w.WriteNumber(PContainer, (int)audio.Container);
        w.WriteNumber(PRate, audio.SampleRate);
        w.WriteNumber(PChannels, audio.Channels);
        w.WriteNumber(PDuration, audio.DurationMs);
        ref readonly var l = ref audio.Loudness;
        w.WriteBoolean(PMeasured, l.Measured);
        w.WriteNumber(PIntegrated, Finite(l.IntegratedLufs));
        w.WriteNumber(PTarget, Finite(l.TargetLufs));
        w.WriteNumber(PPeak, Finite(l.TruePeakDbtp));
        w.WriteNumber(PGain, Finite(l.GainDb));
        w.WriteBoolean(PLimited, l.PeakLimited);
        w.WriteNumber(PBlocks, l.GatedBlocks);
        if (audio.SourcePath is { } src) w.WriteString(PSourcePath, src);
        else w.WriteNull(PSourcePath);
        w.WriteNumber(PChecksum, audio.Checksum);
        w.WriteEndObject();

        w.WriteStartArray(PShots);
        foreach (ref readonly var k in seq.Keyframes)
        {
            w.WriteStartObject();
            w.WriteNumber(PN, k.ShotNumber);
            w.WriteNumber(PStart, k.StartMs);
            w.WriteNumber(PDur, k.DurationMs);
            w.WriteNumber(PAngle, (int)k.Angle);
            WriteBox(w, k.CenterXPermille, k.CenterYPermille, k.CropX, k.CropY, k.CropWidth, k.CropHeight);
            w.WriteNumber(PTs, k.TextStart);
            w.WriteNumber(PTl, k.TextLength);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray(PCrop);
        foreach (ref readonly var c in seq.CropTracker.Track)
        {
            w.WriteStartObject();
            w.WriteNumber(PN, c.ShotNumber);
            w.WriteNumber(PStart, c.StartMs);
            w.WriteNumber(PEnd, c.EndMs);
            WriteBox(w, c.CenterXPermille, c.CenterYPermille, c.CropX, c.CropY, c.CropWidth, c.CropHeight);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
        w.Flush();
        Writes++;
        return _out.WrittenMemory;
    }

    /// <summary>판독 · 검증만(시퀀서 불변) — 결과는 <see cref="LastFingerprint"/> 등에 기록.</summary>
    public StoryvManifestStatus Verify(ReadOnlySpan<byte> json) => LastStatus = Parse(json);

    /// <summary>판독 · 검증 통과 시에만 시퀀서에 원자 복원(실패 = 시퀀서 불변).</summary>
    public StoryvManifestStatus Read(ReadOnlySpan<byte> json, StoryboardKeyframeSequencer seq)
    {
        var st = Parse(json);
        if (st == StoryvManifestStatus.Ok)
        {
            seq.Restore(_script.AsSpan(0, _scriptLen), _keys.AsSpan(0, _keyCount), LastAlignment);
            if (seq.Fingerprint != LastFingerprint) st = StoryvManifestStatus.FingerprintMismatch;
            else Restores++;
        }
        return LastStatus = st;
    }

    /// <summary>디스크 저장 — 같은 폴더 임시 파일에 기록 후 원자 교체 → 재판독 검증. 반환 = 검증 결과.</summary>
    public async Task<StoryvManifestStatus> SaveAsync(string path, StoryboardKeyframeSequencer seq, StoryboardAudioTrack audio = default, CancellationToken ct = default)
    {
        var data = Write(seq, audio);
        var tmp = path + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await fs.WriteAsync(data, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(tmp, path, true);
            return await VerifyFileAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            return LastStatus = StoryvManifestStatus.IoError;
        }
        catch (OperationCanceledException)
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>디스크 파일 재판독 무결성 검증(시퀀서 불변).</summary>
    public async Task<StoryvManifestStatus> VerifyFileAsync(string path, CancellationToken ct = default)
    {
        var n = await ReadFileAsync(path, ct).ConfigureAwait(false);
        return n < 0 ? LastStatus : Verify(_file.AsSpan(0, n));
    }

    /// <summary>디스크 파일 → 시퀀서 복원.</summary>
    public async Task<StoryvManifestStatus> LoadAsync(string path, StoryboardKeyframeSequencer seq, CancellationToken ct = default)
    {
        var n = await ReadFileAsync(path, ct).ConfigureAwait(false);
        return n < 0 ? LastStatus : Read(_file.AsSpan(0, n), seq);
    }

    /// <summary>스트림(파일 선택기 등) → 시퀀서 복원 — 엔진 파일 버퍼 재사용(용량 내 0 B).</summary>
    public async Task<StoryvManifestStatus> LoadAsync(Stream stream, long lengthHint, StoryboardKeyframeSequencer seq, CancellationToken ct = default)
    {
        if (lengthHint > MaxBytes) return LastStatus = StoryvManifestStatus.TooLarge;
        var n = await Fill(stream, lengthHint, ct).ConfigureAwait(false);
        return n < 0 ? LastStatus : Read(_file.AsSpan(0, n), seq);
    }

    private async Task<int> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            if (fs.Length > MaxBytes) { LastStatus = StoryvManifestStatus.TooLarge; return -1; }
            return await Fill(fs, fs.Length, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastStatus = StoryvManifestStatus.IoError;
            return -1;
        }
    }

    private async Task<int> Fill(Stream s, long lengthHint, CancellationToken ct)
    {
        // +1 = EOF 확인용 여유(정확한 길이 파일도 재할당 없이 0 바이트 읽기로 종료)
        if (lengthHint >= _file.Length) _file = new byte[(int)Math.Min(MaxBytes, Math.Max(lengthHint + 1, _file.Length * 2L))];
        var n = 0;
        while (true)
        {
            if (n == _file.Length)
            {
                if (n >= MaxBytes) { LastStatus = StoryvManifestStatus.TooLarge; return -1; }
                Array.Resize(ref _file, (int)Math.Min(MaxBytes, n * 2L));
            }
            var r = await s.ReadAsync(_file.AsMemory(n), ct).ConfigureAwait(false);
            if (r == 0) return n;
            n += r;
        }
    }

    private StoryvManifestStatus Parse(ReadOnlySpan<byte> json)
    {
        _keyCount = _cropCount = _scriptLen = 0;
        LastShots = 0;
        if (json.IsEmpty) return StoryvManifestStatus.Empty;
        if (json.Length > MaxBytes) return StoryvManifestStatus.TooLarge;
        var r = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        bool format = false, version = false, script = false;
        ulong fp = 0;
        int total = -1, align = 0;
        var audio = default(StoryvAudioHeader);
        try
        {
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return StoryvManifestStatus.Malformed;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals(PFormat.EncodedUtf8Bytes)) { r.Read(); if (!r.ValueTextEquals(VFormat.EncodedUtf8Bytes)) return StoryvManifestStatus.Malformed; format = true; }
                else if (r.ValueTextEquals(PVersion.EncodedUtf8Bytes)) { r.Read(); if (r.GetInt32() != Version) return StoryvManifestStatus.UnsupportedVersion; version = true; }
                else if (r.ValueTextEquals(PFingerprint.EncodedUtf8Bytes)) { r.Read(); fp = r.GetUInt64(); }
                else if (r.ValueTextEquals(PTotal.EncodedUtf8Bytes)) { r.Read(); total = r.GetInt32(); }
                else if (r.ValueTextEquals(PAlignment.EncodedUtf8Bytes)) { r.Read(); align = r.GetInt32(); }
                else if (r.ValueTextEquals(PScript.EncodedUtf8Bytes))
                {
                    r.Read();
                    if (r.TokenType != JsonTokenType.String) return StoryvManifestStatus.Malformed;
                    var max = r.HasValueSequence ? (int)r.ValueSequence.Length : r.ValueSpan.Length;
                    if (max > MaxScriptChars) return StoryvManifestStatus.TooLarge;
                    if (max > _script.Length) _script = new char[Math.Max(max, _script.Length * 2)];
                    _scriptLen = r.CopyString(_script);
                    script = true;
                }
                else if (r.ValueTextEquals(PAudio.EncodedUtf8Bytes)) { if (!ReadAudio(ref r, LastAudio.SourcePath, out audio)) return StoryvManifestStatus.Malformed; }
                else if (r.ValueTextEquals(PShots.EncodedUtf8Bytes)) { if (!ReadShots(ref r)) return StoryvManifestStatus.Malformed; }
                else if (r.ValueTextEquals(PCrop.EncodedUtf8Bytes)) { if (!ReadCrop(ref r)) return StoryvManifestStatus.Malformed; }
                else { r.Read(); r.Skip(); }   // 미지 속성 — 상위 호환
            }
            if (r.TokenType != JsonTokenType.EndObject) return StoryvManifestStatus.Malformed;
        }
        catch (JsonException) { return StoryvManifestStatus.Malformed; }
        catch (InvalidOperationException) { return StoryvManifestStatus.Malformed; }
        catch (FormatException) { return StoryvManifestStatus.Malformed; }

        if (!format || !version || !script || _keyCount == 0) return StoryvManifestStatus.Malformed;
        if ((uint)align > (uint)AudioAlignmentSource.AudioDurationScale) return StoryvManifestStatus.OutOfRange;

        // 범위 · 연속성 — 시작 오름차순 · 길이 > 0 · 대본 오프셋 · 9:16 박스 원본 안
        var keys = _keys.AsSpan(0, _keyCount);
        var end = 0;
        for (var i = 0; i < keys.Length; i++)
        {
            ref readonly var k = ref keys[i];
            if (k.ShotNumber != i + 1 || k.StartMs < 0 || k.DurationMs <= 0 || (i > 0 && k.StartMs < keys[i - 1].StartMs)) return StoryvManifestStatus.OutOfRange;
            if ((uint)k.Angle > (uint)ShotAngle.CloseUp) return StoryvManifestStatus.OutOfRange;
            if (k.TextStart < 0 || k.TextLength < 0 || k.TextStart + k.TextLength > _scriptLen) return StoryvManifestStatus.OutOfRange;
            if (k.CropWidth <= 0 || k.CropHeight <= 0 || k.CropX < 0 || k.CropY < 0
                || k.CropX + k.CropWidth > StoryboardKeyframeSequencer.SourceWidth || k.CropY + k.CropHeight > StoryboardKeyframeSequencer.SourceHeight)
                return StoryvManifestStatus.OutOfRange;
            end = Math.Max(end, k.EndMs);
        }
        if (total != end) return StoryvManifestStatus.OutOfRange;

        // 크롭 트랙 = 샷 프레이밍(1:1)
        if (_cropCount != _keyCount) return StoryvManifestStatus.CropMismatch;
        for (var i = 0; i < keys.Length; i++)
        {
            ref readonly var k = ref keys[i];
            ref readonly var c = ref _crop[i];
            if (c.ShotNumber != k.ShotNumber || c.StartMs != k.StartMs || c.EndMs != k.EndMs || c.CenterXPermille != k.CenterXPermille || c.CenterYPermille != k.CenterYPermille
                || c.CropX != k.CropX || c.CropY != k.CropY || c.CropWidth != k.CropWidth || c.CropHeight != k.CropHeight)
                return StoryvManifestStatus.CropMismatch;
        }

        if (StoryboardKeyframeSequencer.Hash(keys, _script.AsSpan(0, _scriptLen)) != fp) return StoryvManifestStatus.FingerprintMismatch;
        LastFingerprint = fp;
        LastShots = _keyCount;
        LastAlignment = (AudioAlignmentSource)align;
        LastAudio = audio;
        return StoryvManifestStatus.Ok;
    }

    /// <summary>오디오 헤더 판독 — [v18.2.6] 경로가 직전 판독과 같으면 문자열 재사용(재판독 0 B) · 경로 상한 초과 = 오류.</summary>
    private static bool ReadAudio(ref Utf8JsonReader r, string? lastPath, out StoryvAudioHeader audio)
    {
        audio = default;
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
        int container = 0, rate = 0, ch = 0, dur = 0, blocks = 0;
        bool measured = false, limited = false;
        double integ = 0, target = 0, peak = 0, gain = 0;
        string? path = null;
        ulong checksum = 0;
        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals(PSourcePath.EncodedUtf8Bytes))
            {
                r.Read();
                if (r.TokenType == JsonTokenType.Null) continue;
                if (r.TokenType != JsonTokenType.String) return false;
                if ((r.HasValueSequence ? r.ValueSequence.Length : r.ValueSpan.Length) > StoryboardAudioSourceStreamBinder.MaxPathChars * 6) return false;
                path = lastPath is not null && r.ValueTextEquals(lastPath) ? lastPath : r.GetString();
                if (path is { Length: 0 or > StoryboardAudioSourceStreamBinder.MaxPathChars }) return false;
                continue;
            }
            if (r.ValueTextEquals(PChecksum.EncodedUtf8Bytes)) { r.Read(); checksum = r.GetUInt64(); continue; }
            if (r.ValueTextEquals(PContainer.EncodedUtf8Bytes)) { r.Read(); container = r.GetInt32(); }
            else if (r.ValueTextEquals(PRate.EncodedUtf8Bytes)) { r.Read(); rate = r.GetInt32(); }
            else if (r.ValueTextEquals(PChannels.EncodedUtf8Bytes)) { r.Read(); ch = r.GetInt32(); }
            else if (r.ValueTextEquals(PDuration.EncodedUtf8Bytes)) { r.Read(); dur = r.GetInt32(); }
            else if (r.ValueTextEquals(PMeasured.EncodedUtf8Bytes)) { r.Read(); measured = r.GetBoolean(); }
            else if (r.ValueTextEquals(PIntegrated.EncodedUtf8Bytes)) { r.Read(); integ = r.GetDouble(); }
            else if (r.ValueTextEquals(PTarget.EncodedUtf8Bytes)) { r.Read(); target = r.GetDouble(); }
            else if (r.ValueTextEquals(PPeak.EncodedUtf8Bytes)) { r.Read(); peak = r.GetDouble(); }
            else if (r.ValueTextEquals(PGain.EncodedUtf8Bytes)) { r.Read(); gain = r.GetDouble(); }
            else if (r.ValueTextEquals(PLimited.EncodedUtf8Bytes)) { r.Read(); limited = r.GetBoolean(); }
            else if (r.ValueTextEquals(PBlocks.EncodedUtf8Bytes)) { r.Read(); blocks = r.GetInt32(); }
            else { r.Read(); r.Skip(); }
        }
        if (r.TokenType != JsonTokenType.EndObject || (uint)container > (uint)AudioContainer.Mp3 || rate < 0 || ch < 0 || dur < 0) return false;
        audio = new StoryvAudioHeader((AudioContainer)container, rate, ch, dur, new LufsLoudnessReport(measured, integ, target, peak, gain, limited, blocks), path, checksum);
        return true;
    }

    private bool ReadShots(ref Utf8JsonReader r)
    {
        if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return false;
        while (r.Read() && r.TokenType == JsonTokenType.StartObject)
        {
            if (_keyCount >= _keys.Length) return false;
            ref var k = ref _keys[_keyCount++];
            k = default;
            int angle = -1;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals(PN.EncodedUtf8Bytes)) { r.Read(); k.ShotNumber = r.GetInt32(); }
                else if (r.ValueTextEquals(PStart.EncodedUtf8Bytes)) { r.Read(); k.StartMs = r.GetInt32(); }
                else if (r.ValueTextEquals(PDur.EncodedUtf8Bytes)) { r.Read(); k.DurationMs = r.GetInt32(); }
                else if (r.ValueTextEquals(PAngle.EncodedUtf8Bytes)) { r.Read(); angle = r.GetInt32(); }
                else if (r.ValueTextEquals(PTs.EncodedUtf8Bytes)) { r.Read(); k.TextStart = r.GetInt32(); }
                else if (r.ValueTextEquals(PTl.EncodedUtf8Bytes)) { r.Read(); k.TextLength = r.GetInt32(); }
                else if (!ReadBox(ref r, ref k.CenterXPermille, ref k.CenterYPermille, ref k.CropX, ref k.CropY, ref k.CropWidth, ref k.CropHeight)) { r.Read(); r.Skip(); }
            }
            if (r.TokenType != JsonTokenType.EndObject || angle < 0 || angle > byte.MaxValue) return false;
            k.Angle = (ShotAngle)angle;
        }
        return r.TokenType == JsonTokenType.EndArray;
    }

    private bool ReadCrop(ref Utf8JsonReader r)
    {
        if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return false;
        while (r.Read() && r.TokenType == JsonTokenType.StartObject)
        {
            if (_cropCount >= _crop.Length) return false;
            ref var c = ref _crop[_cropCount++];
            c = default;
            while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
            {
                if (r.ValueTextEquals(PN.EncodedUtf8Bytes)) { r.Read(); c.ShotNumber = r.GetInt32(); }
                else if (r.ValueTextEquals(PStart.EncodedUtf8Bytes)) { r.Read(); c.StartMs = r.GetInt32(); }
                else if (r.ValueTextEquals(PEnd.EncodedUtf8Bytes)) { r.Read(); c.EndMs = r.GetInt32(); }
                else if (!ReadBox(ref r, ref c.CenterXPermille, ref c.CenterYPermille, ref c.CropX, ref c.CropY, ref c.CropWidth, ref c.CropHeight)) { r.Read(); r.Skip(); }
            }
            if (r.TokenType != JsonTokenType.EndObject) return false;
        }
        return r.TokenType == JsonTokenType.EndArray;
    }

    private static void WriteBox(Utf8JsonWriter w, int cx, int cy, int x, int y, int width, int height)
    {
        w.WriteNumber(PCx, cx);
        w.WriteNumber(PCy, cy);
        w.WriteNumber(PX, x);
        w.WriteNumber(PY, y);
        w.WriteNumber(PW, width);
        w.WriteNumber(PH, height);
    }

    /// <summary>현재 속성이 9:16 박스 필드면 값을 읽고 true(그 외 false — 리더 위치 불변).</summary>
    private static bool ReadBox(ref Utf8JsonReader r, ref int cx, ref int cy, ref int x, ref int y, ref int width, ref int height)
    {
        if (r.ValueTextEquals(PCx.EncodedUtf8Bytes)) { r.Read(); cx = r.GetInt32(); }
        else if (r.ValueTextEquals(PCy.EncodedUtf8Bytes)) { r.Read(); cy = r.GetInt32(); }
        else if (r.ValueTextEquals(PX.EncodedUtf8Bytes)) { r.Read(); x = r.GetInt32(); }
        else if (r.ValueTextEquals(PY.EncodedUtf8Bytes)) { r.Read(); y = r.GetInt32(); }
        else if (r.ValueTextEquals(PW.EncodedUtf8Bytes)) { r.Read(); width = r.GetInt32(); }
        else if (r.ValueTextEquals(PH.EncodedUtf8Bytes)) { r.Read(); height = r.GetInt32(); }
        else return false;
        return true;
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0d;

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 임시 파일 정리 실패 무시 */ }
    }
}
