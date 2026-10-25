namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [v18.2.0] 샷 카드 인스펙터 1 장 — 샷 번호(1 부터) · 타임코드 · 앵글 · 9:16 프레이밍 중심(원본 ‰) · 크롭 박스(원본 px) · 대본 슬라이스 오프셋.
/// </summary>
public struct StoryboardKeyframe
{
    public int ShotNumber;
    public int StartMs, DurationMs;
    public ShotAngle Angle;
    public int CenterXPermille, CenterYPermille;
    public int CropX, CropY, CropWidth, CropHeight;
    public int TextStart, TextLength;

    public readonly int EndMs => StartMs + DurationMs;
}

/// <summary>
/// [PromptFlow Studio v18.2.0] 🎬 비주얼 샷 키프레임 타임라인 시퀀서 — <see cref="StoryboardShotBreakdownEngine"/> 결과를 시작 시각 순으로 정렬하고
///   앵글별 9:16 크롭(와이드 1.0× · 미디엄 1.25× · 클로즈업 1.6×) · 프레이밍 중심(3 분할 교차)을 규격화한다.
///   대본은 소유 버퍼에 1 회 기록(용량 내 재로드 0 B) · 샷/키프레임은 고정 배열 제자리 갱신 · 외부엔 <see cref="ReadOnlyMemory{T}"/> 로 공유(0-카피).
/// </summary>
public sealed class StoryboardKeyframeSequencer
{
    public const int SourceWidth = 1920, SourceHeight = 1080;
    public const int MaxShots = StoryboardShotBreakdownEngine.MaxShots;
    /// <summary>앵글별 크롭 높이(원본 px) — 와이드 1080 · 미디엄 864(1.25×) · 클로즈업 675(1.6×).</summary>
    public static readonly int[] CropHeights = { SourceHeight, SourceHeight * 4 / 5, SourceHeight * 5 / 8 };
    /// <summary>앵글별 세로 중심(‰) — 클로즈업일수록 얼굴 헤드룸 위로.</summary>
    public static readonly int[] CenterYPermilles = { 500, 440, 380 };
    public static readonly string[] AngleTags = { "🌄 와이드", "🧍 미디엄", "👁 클로즈업" };

    private readonly StoryboardShot[] _shots = new StoryboardShot[MaxShots];
    private readonly StoryboardKeyframe[] _keys = new StoryboardKeyframe[MaxShots];
    private char[] _script = new char[1024];
    private int _scriptLen;
    private readonly AudioVoiceSegment[] _voice = new AudioVoiceSegment[StoryboardAudioWordBoundaryAligner.MaxSegments];
    private readonly AudioWordBoundary[] _words = new AudioWordBoundary[StoryboardAudioWordBoundaryAligner.MaxWords];

    public int Count { get; private set; }
    public int TotalMs { get; private set; }
    public int Revision { get; private set; }
    public ulong Fingerprint { get; private set; }
    /// <summary>[v18.2.1] 마지막 로드의 타임코드 출처(오디오 미제공 · 무음성 = 음절 추정 폴백).</summary>
    public AudioAlignmentSource Alignment { get; private set; }
    /// <summary>[v18.2.1] 전체 샷 9:16 크롭 트랙 — <see cref="Sequence"/> 마다 제자리 갱신.</summary>
    public MultiShotDynamicReframeTracker CropTracker { get; } = new();
    public ReadOnlyMemory<ShotCropKeyframe> CropTrackMemory => CropTracker.TrackMemory;

    public ReadOnlySpan<StoryboardKeyframe> Keyframes => _keys.AsSpan(0, Count);
    public ReadOnlyMemory<StoryboardKeyframe> KeyframeMemory => _keys.AsMemory(0, Count);
    public ReadOnlySpan<char> Script => _script.AsSpan(0, _scriptLen);
    public ReadOnlyMemory<char> ScriptMemory => _script.AsMemory(0, _scriptLen);
    public StoryboardKeyframe KeyframeAt(int index) => _keys[index];
    public ReadOnlySpan<char> TextOf(in StoryboardKeyframe k) => _script.AsSpan(k.TextStart, k.TextLength);
    public static string AngleTag(ShotAngle a) => AngleTags[(int)a];

    /// <summary>대본 로드 → 샷 분할 → 키프레임 정렬/규격화. 반환 = 샷 수(용량 내 재호출 0 B).</summary>
    public int Load(ReadOnlySpan<char> script, int syllableMs = StoryboardShotBreakdownEngine.DefaultSyllableMs)
    {
        var n = Split(script, syllableMs);
        Alignment = AudioAlignmentSource.SyllableEstimate;
        return Sequence(_shots.AsSpan(0, n));
    }

    /// <summary>[v18.2.1] 대본 + 오디오(WAV 또는 원시 PCM 16-bit 모노) → 무음 경계 스냅 타임코드. 오디오가 비거나 음성 0 이면 음절 추정 폴백(용량 내 0 B).</summary>
    public int LoadWithAudio(ReadOnlySpan<char> script, ReadOnlySpan<byte> audio, int sampleRate = StoryboardAudioWordBoundaryAligner.DefaultSampleRate, int channels = 1)
    {
        var n = Split(script, StoryboardShotBreakdownEngine.DefaultSyllableMs);
        Alignment = StoryboardAudioWordBoundaryAligner.AlignToAudio(_shots.AsSpan(0, n), audio, _voice, sampleRate, StoryboardAudioWordBoundaryAligner.DefaultThreshold, channels);
        return Sequence(_shots.AsSpan(0, n));
    }

    /// <summary>[v18.2.2] 대본 + 실측 오디오 길이(음성 미검출 오디오) → 음절 비율로 [0, <paramref name="totalMs"/>) 를 분배(음절 템포 추정 배제 · 0 B).</summary>
    public int LoadWithDuration(ReadOnlySpan<char> script, int totalMs)
    {
        var n = Split(script, StoryboardShotBreakdownEngine.DefaultSyllableMs);
        Span<AudioVoiceSegment> whole = stackalloc AudioVoiceSegment[1];
        whole[0].StartMs = 0;
        whole[0].EndMs = Math.Max(1, totalMs);
        Alignment = StoryboardAudioWordBoundaryAligner.AlignToVoice(_shots.AsSpan(0, n), whole, totalMs) == AudioAlignmentSource.SyllableEstimate
            ? AudioAlignmentSource.SyllableEstimate : AudioAlignmentSource.AudioDurationScale;
        return Sequence(_shots.AsSpan(0, n));
    }

    /// <summary>[v18.2.1] 대본 + TTS 단어 경계 스트림(UTF-8 "startMs,endMs,word" 줄) → 단어 순번 매핑 타임코드. 단어 0 이면 음절 추정 폴백(용량 내 0 B).</summary>
    public int LoadWithWordBoundaries(ReadOnlySpan<char> script, ReadOnlySpan<byte> boundaries)
    {
        var n = Split(script, StoryboardShotBreakdownEngine.DefaultSyllableMs);
        var w = StoryboardAudioWordBoundaryAligner.ParseWordBoundaries(boundaries, _words);
        Alignment = StoryboardAudioWordBoundaryAligner.AlignToWords(_shots.AsSpan(0, n), Script, _words.AsSpan(0, w));
        return Sequence(_shots.AsSpan(0, n));
    }

    private int Split(ReadOnlySpan<char> script, int syllableMs)
    {
        if (script.Length > _script.Length) _script = new char[Math.Max(script.Length, _script.Length * 2)];
        script.CopyTo(_script);
        _scriptLen = script.Length;
        return StoryboardShotBreakdownEngine.Breakdown(Script, _shots, syllableMs);
    }

    /// <summary>샷 → 키프레임(시작 시각 오름차순 삽입 정렬 · 번호 재부여 · 9:16 프레이밍).</summary>
    public int Sequence(ReadOnlySpan<StoryboardShot> shots)
    {
        var n = Math.Min(shots.Length, MaxShots);
        for (var i = 0; i < n; i++)
        {
            ref readonly var s = ref shots[i];
            ref var k = ref _keys[i];
            k.StartMs = s.StartMs;
            k.DurationMs = s.DurationMs;
            k.Angle = s.Angle;
            k.TextStart = s.TextStart;
            k.TextLength = s.TextLength;
        }
        for (var i = 1; i < n; i++)
        {
            var x = _keys[i];
            var j = i - 1;
            while (j >= 0 && _keys[j].StartMs > x.StartMs) { _keys[j + 1] = _keys[j]; j--; }
            _keys[j + 1] = x;
        }
        var total = 0;
        for (var i = 0; i < n; i++)
        {
            ref var k = ref _keys[i];
            k.ShotNumber = i + 1;
            Frame(ref k, i);
            total = Math.Max(total, k.EndMs);
        }
        Count = n;
        TotalMs = total;
        Revision++;
        Fingerprint = Hash(Keyframes, Script);
        CropTracker.Load(Keyframes);
        return n;
    }

    /// <summary>[v18.2.5] 편집기 · 매니페스트 전용 — 키프레임 고정 배열 전체(용량 <see cref="MaxShots"/>) 제자리 편집 창.</summary>
    internal Span<StoryboardKeyframe> EditBuffer => _keys;

    /// <summary>[v18.2.5] 편집 확정 — 번호 재부여 · 총 길이 · Revision · 지문 · 크롭 트랙 갱신(수동 프레이밍 보존 · 0 B). 반환 = 샷 수.</summary>
    internal int Commit(int count)
    {
        var n = Math.Clamp(count, 0, MaxShots);
        var total = 0;
        for (var i = 0; i < n; i++)
        {
            ref var k = ref _keys[i];
            k.ShotNumber = i + 1;
            total = Math.Max(total, k.EndMs);
        }
        Count = n;
        TotalMs = total;
        Revision++;
        Fingerprint = Hash(Keyframes, Script);
        CropTracker.Load(Keyframes);
        return n;
    }

    /// <summary>[v18.2.5] 매니페스트 복원 — 대본 · 키프레임(검증 완료분)을 소유 버퍼에 기록 후 확정(용량 내 0 B).</summary>
    internal int Restore(ReadOnlySpan<char> script, ReadOnlySpan<StoryboardKeyframe> keys, AudioAlignmentSource alignment)
    {
        if (script.Length > _script.Length) _script = new char[Math.Max(script.Length, _script.Length * 2)];
        script.CopyTo(_script);
        _scriptLen = script.Length;
        var n = Math.Min(keys.Length, MaxShots);
        keys[..n].CopyTo(_keys);
        Alignment = alignment;
        return Commit(n);
    }

    /// <summary>9:16 크롭 — 와이드는 정중앙 · 그 외는 3 분할선(333/667 ‰) 교차로 시선 리듬. 박스 폭은 짝수 정렬 · 원본 안으로 클램프.</summary>
    public static void Frame(ref StoryboardKeyframe k, int index)
    {
        var a = (int)k.Angle;
        k.CenterXPermille = k.Angle == ShotAngle.Wide ? 500 : (index & 1) == 0 ? 333 : 667;
        k.CenterYPermille = CenterYPermilles[a];
        k.CropHeight = CropHeights[a];
        k.CropWidth = (k.CropHeight * 9 / 16 + 1) & ~1;
        k.CropX = Math.Clamp(k.CenterXPermille * SourceWidth / 1000 - k.CropWidth / 2, 0, SourceWidth - k.CropWidth);
        k.CropY = Math.Clamp(k.CenterYPermille * SourceHeight / 1000 - k.CropHeight / 2, 0, SourceHeight - k.CropHeight);
    }

    /// <summary>FNV-1a 64 — 키프레임 타임코드/앵글/크롭 + 대본.</summary>
    public static ulong Hash(ReadOnlySpan<StoryboardKeyframe> keys, ReadOnlySpan<char> script)
    {
        var h = 14695981039346656037UL;
        foreach (ref readonly var k in keys)
        {
            h = (h ^ (uint)k.StartMs) * 1099511628211UL;
            h = (h ^ (uint)k.DurationMs) * 1099511628211UL;
            h = (h ^ (uint)k.Angle) * 1099511628211UL;
            h = (h ^ (uint)k.CropX) * 1099511628211UL;
            h = (h ^ (uint)(k.TextStart << 16 ^ k.TextLength)) * 1099511628211UL;
        }
        foreach (var c in script) h = (h ^ c) * 1099511628211UL;
        return h;
    }
}
