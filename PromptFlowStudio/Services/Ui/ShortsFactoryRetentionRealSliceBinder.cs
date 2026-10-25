using PromptFlowStudio.Services.LiveClipper;
using PromptFlowStudio.Services.RetentionHeatmap;

namespace PromptFlowStudio.Services.Ui;

public enum SliceBindStatus : byte { Idle, Bound, NoSource, BufferTooSmall, ParseMismatch, RemuxFailed }

/// <summary>
/// [PromptFlow Studio v18.0.2] 🔗 숏폼 팩토리 ③ 리텐션 실 슬라이스 바인더 — 합성 데모 클립 대신 ① <see cref="SourceTrim"/> 구간의 실제 소스 링
/// (<see cref="RollingCircularVideoBufferEngine"/> Annex-B AU · ADTS 프레임 · 정렬 PCM)을 잘라 <see cref="MicroPunchDynamicPacingReEditorEngine"/> /
/// <see cref="NaluSmartReMuxer"/> 에 직결한다(<see cref="Slice"/> · <see cref="ApplyRetention"/> 무할당).
///   ▸ 슬라이스: 링 직전 IDR ~ 구간 끝 프레임을 연속 Annex-B 로 복사 · AU 별 PTS 를 첫 IDR 원점 90 kHz 로 정규화(링 = 디코딩 순서 · DTS = PTS 단조 보정).
///   ▸ 원점: T_start − 첫 IDR PTS = <see cref="OriginTicks"/> → 리에디터가 컷 [T_start, T_end) · 펀치 시각에 같은 오프셋을 더해 리먹서가 정확한 GOP 에 1.15× SPS 크롭.
///   ▸ 오디오: PCM 구간 → (안전 BGM 시) −30 dB 덕킹 베드 믹스 → 데드에어 템포/WSOLA · ADTS 프레임 → 워프 절점으로 재타이밍(압축 구간 프레임 단위 드롭 · 스트림 복사).
/// </summary>
public sealed class ShortsFactoryRetentionRealSliceBinder
{
    public const int Fs = MicroPunchDynamicPacingReEditorEngine.Fs, AacFrameSamples = 1024, MaxAudioFrames = 4096;
    public const int MaxSec = ShortsFactoryWorkspaceContext.MaxClipMs / 1000;
    /// <summary>안전 BGM 덕킹 이득(−30 dB = 10^(−30/20)).</summary>
    public const float DuckGain = 0.031622775f;
    public const long AacFrameTicks = (long)AacFrameSamples * NaluSmartReMuxer.Clock / Fs;

    private readonly byte[] _video, _remux, _audio;
    private readonly long[] _pts, _dts, _aPtsUs, _aOutUs;
    private readonly int[] _aOff, _aLen, _keep, _punchRel = new int[ShortsFactoryWorkspaceContext.MaxZoomPunches];
    private readonly float[] _curve = new float[MaxSec + 2];
    private readonly float[] _pcm = new float[MaxSec * Fs];
    private readonly float[] _out = new float[MaxSec * Fs];
    private int _vLen, _vN, _aBytes, _aN, _keepN, _remuxLen, _inMs, _durMs;
    private long _baseUs;

    public ShortsFactoryRetentionRealSliceBinder(int maxSliceBytes = 4 << 20, int maxAccessUnits = 4_096)
    {
        _video = new byte[maxSliceBytes];
        _remux = new byte[maxSliceBytes + (maxSliceBytes >> 4) + 4_096];
        _audio = new byte[1 << 20];
        _pts = new long[maxAccessUnits]; _dts = new long[maxAccessUnits];
        _aPtsUs = new long[MaxAudioFrames]; _aOutUs = new long[MaxAudioFrames];
        _aOff = new int[MaxAudioFrames]; _aLen = new int[MaxAudioFrames]; _keep = new int[MaxAudioFrames];
        Mux = new NaluSmartReMuxer(16_384, maxAccessUnits);
    }

    public NaluSmartReMuxer Mux { get; }
    public MicroPunchDynamicPacingReEditorEngine ReEditor { get; } = new(MaxSec);
    public AudienceRetentionDropoffDetectorEngine Detector { get; } = new();
    public SliceBindStatus Status { get; private set; }
    /// <summary>슬라이스 원본 링 시퀀스 범위(합성 클립이 아닌 소스 링 실 바이트 증빙).</summary>
    public long FirstSeq { get; private set; } = -1;
    public long LastSeq { get; private set; } = -1;
    public int SliceVideoFrames => _vN;
    public int SliceAudioFrames => _aN;
    public int SliceBytes => _vLen;
    /// <summary>첫 IDR → T_start 오프셋(90 kHz).</summary>
    public long OriginTicks { get; private set; }
    public long FrameTicks { get; private set; } = NaluSmartReMuxer.Clock / 30;
    public int PunchCount => ReEditor.PunchCount;
    public int OutSamples { get; private set; }
    public float BgmGain { get; private set; }
    public int LosslessFaults { get; private set; }
    public long Binds { get; private set; }
    public double LastSliceUs { get; private set; }
    public double LastRetentionUs { get; private set; }

    public ReadOnlySpan<byte> SliceAnnexB => _video.AsSpan(0, _vLen);
    public ReadOnlySpan<long> SlicePts => _pts.AsSpan(0, _vN);
    public ReadOnlySpan<byte> RemuxedAnnexB => _remux.AsSpan(0, _remuxLen);
    public ReadOnlySpan<float> OutputPcm => _out.AsSpan(0, OutSamples);
    /// <summary>③ 이후 유지되는 ADTS 프레임 수(템포 압축 구간 드롭 반영).</summary>
    public int KeptAudioFrames => _keepN;
    public ReadOnlySpan<byte> AudioFrame(int keptIndex) => _audio.AsSpan(_aOff[_keep[keptIndex]], _aLen[_keep[keptIndex]]);
    public long AudioOutPtsUs(int keptIndex) => _aOutUs[keptIndex];
    public static long TicksToUs(long ticks) => ticks * 1_000_000 / NaluSmartReMuxer.Clock;

    /// <summary>
    /// ① 링 슬라이스(무할당) — [<paramref name="ringOriginUs"/> + Start, + End] 를 직전 IDR 부터 연속 Annex-B · ADTS 로 복사하고
    /// AU 타임스탬프를 첫 IDR 원점 90 kHz 로 정규화 → <see cref="Mux"/> 파싱. AU 수 ≠ 링 비디오 프레임 수면 <see cref="SliceBindStatus.ParseMismatch"/>.
    /// </summary>
    public SliceBindStatus Slice(RollingCircularVideoBufferEngine ring, in SourceTrim trim, long ringOriginUs)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _vLen = _vN = _aBytes = _aN = _keepN = _remuxLen = 0;
        OutSamples = 0;
        _inMs = (int)(trim.Start.Ticks / TimeSpan.TicksPerMillisecond);
        _durMs = Math.Min((int)(trim.Duration.Ticks / TimeSpan.TicksPerMillisecond), MaxSec * 1000);
        var startUs = ringOriginUs + _inMs * 1000L;
        if (_durMs <= 0 || !ring.TrySlice(startUs, startUs + _durMs * 1000L, out var s)) return Done(SliceBindStatus.NoSource, t0);
        FirstSeq = s.FirstSeq; LastSeq = s.LastSeq;
        _baseUs = s.StartPtsUs;

        if (s.PrependParams)
        {
            var n = ring.CopyParams(_video);
            if (n <= 0) return Done(SliceBindStatus.BufferTooSmall, t0);
            _vLen = n;
        }
        for (var q = s.FirstSeq; q <= s.LastSeq; q++)
        {
            var f = ring.Frame(q);
            if (f.Kind == MediaKind.Video)
            {
                if (_vN == _pts.Length) return Done(SliceBindStatus.BufferTooSmall, t0);
                var w = ring.CopyFrame(q, _video.AsSpan(_vLen));
                if (w < 0) return Done(SliceBindStatus.BufferTooSmall, t0);
                _vLen += w;
                var pts = (f.PtsUs - _baseUs) * NaluSmartReMuxer.Clock / 1_000_000;
                _pts[_vN] = pts;
                // 링 = 디코딩 순서 저장 · 재정렬 정보 없음 → DTS = PTS(엄격 증가 보정)
                _dts[_vN] = _vN > 0 && pts <= _dts[_vN - 1] ? _dts[_vN - 1] + 1 : pts;
                _vN++;
            }
            else
            {
                if (_aN == MaxAudioFrames) continue;
                var w = ring.CopyFrame(q, _audio.AsSpan(_aBytes));
                if (w < 0) continue;
                _aOff[_aN] = _aBytes; _aLen[_aN] = w; _aPtsUs[_aN] = f.PtsUs;
                _aBytes += w; _aN++;
            }
        }
        if (_vN == 0) return Done(SliceBindStatus.NoSource, t0);
        FrameTicks = _vN > 1 ? Math.Max(1, (_pts[_vN - 1] - _pts[0]) / (_vN - 1)) : NaluSmartReMuxer.Clock / 30;
        OriginTicks = (startUs - _baseUs) * NaluSmartReMuxer.Clock / 1_000_000;

        if (Mux.Parse(_video.AsSpan(0, _vLen), NalCodec.H264) != _vN) return Done(SliceBindStatus.ParseMismatch, t0);
        Mux.SetTimestamps(_pts.AsSpan(0, _vN), _dts.AsSpan(0, _vN));
        return Done(SliceBindStatus.Bound, t0);
    }

    /// <summary>
    /// ③ 리텐션 적용(무할당) — 구간 유지율 곡선 → 이탈 분석 · PCM 구간(+ −30 dB BGM) → 펀치(원본 ms → 클립 원점) · 템포 계획 · WSOLA 리패키징 →
    /// <see cref="MicroPunchDynamicPacingReEditorEngine.RemuxInto"/>(원점 오프셋 · SPS 줌 펀치) → 무손실 검증 → ADTS 재타이밍.
    /// <paramref name="pcm"/> · <paramref name="bgm"/> = 48 kHz 모노 16 bit(<paramref name="pcm"/> 0 = 링 원점) · <paramref name="bgm"/> 은 반복 베드.
    /// </summary>
    public SliceBindStatus ApplyRetention(ReadOnlySpan<short> pcm, ReadOnlySpan<short> bgm, ReadOnlySpan<int> punchSrcMs, bool bgmSafe)
    {
        if (Status != SliceBindStatus.Bound) return Status;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = BeginRetention(bgm, bgmSafe, out var s0);
        for (var i = 0; i < n; i++)
        {
            var at = s0 + i;
            var v = at < pcm.Length ? pcm[(int)at] * (1f / 32768f) : 0f;
            if (BgmGain > 0f) v += bgm[(int)(at % bgm.Length)] * (BgmGain / 32768f);
            _pcm[i] = v;
        }
        return FinishRetention(punchSrcMs, t0, n);
    }

    /// <summary>
    /// [v18.2.2] ③ 리텐션 — 스토리보드 실 오디오(PCM 16-bit LE 인터리브 · 임의 레이트/채널 · 0 = 링 원점)를 복사 없이 48 kHz 모노 최근접 샘플링으로 소비(무할당).
    /// </summary>
    public SliceBindStatus ApplyRetention(ReadOnlySpan<byte> pcm16, int sampleRate, int channels, ReadOnlySpan<short> bgm, ReadOnlySpan<int> punchSrcMs, bool bgmSafe)
    {
        if (Status != SliceBindStatus.Bound) return Status;
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var n = BeginRetention(bgm, bgmSafe, out var s0);
        for (var i = 0; i < n; i++)
        {
            var at = s0 + i;
            var v = PromptFlowStudio.Services.Storyboard.StoryboardAudioTrack.SampleAt(pcm16, sampleRate, channels, Fs, at);
            if (BgmGain > 0f) v += bgm[(int)(at % bgm.Length)] * (BgmGain / 32768f);
            _pcm[i] = v;
        }
        ExternalAudioBinds++;
        return FinishRetention(punchSrcMs, t0, n);
    }

    /// <summary>[v18.2.2] 실 오디오 PCM 으로 ③ 을 수행한 횟수.</summary>
    public long ExternalAudioBinds { get; private set; }

    /// <summary>[v18.2.2] 리텐션 출력 PCM(48 kHz 모노 · 바인더 버퍼 공유) — ④ 내레이션 트랙 입력.</summary>
    public ReadOnlyMemory<float> OutputPcmMemory => _out.AsMemory(0, OutSamples);

    private int BeginRetention(ReadOnlySpan<short> bgm, bool bgmSafe, out long s0)
    {
        var secs = _durMs / 1000;
        for (var i = 0; i <= secs; i++) _curve[i] = ShortsFactoryPipelineContext.RetentionAt(_inMs + i * 1000) / 1000f;
        Detector.Analyze(_curve.AsSpan(0, secs + 1));
        s0 = (long)_inMs * (Fs / 1000);
        BgmGain = bgmSafe && bgm.Length > 0 ? DuckGain : 0f;
        return _durMs * (Fs / 1000);
    }

    private SliceBindStatus FinishRetention(ReadOnlySpan<int> punchSrcMs, long t0, int n)
    {
        var pn = 0;
        foreach (var p in punchSrcMs)
            if (pn < _punchRel.Length && p >= _inMs && p < _inMs + _durMs) _punchRel[pn++] = p - _inMs;
        ReEditor.Plan(Detector, _pcm.AsSpan(0, n), _punchRel.AsSpan(0, pn));
        OutSamples = ReEditor.Repackage(_pcm.AsSpan(0, n), _out);

        _remuxLen = ReEditor.RemuxInto(Mux, _video.AsSpan(0, _vLen), OriginTicks, FrameTicks, _remux);
        if (_remuxLen < 0 || Mux.OutCount == 0) { _remuxLen = 0; return Retained(SliceBindStatus.RemuxFailed, t0); }
        LosslessFaults = Mux.VerifyLossless(_video.AsSpan(0, _vLen), _remux.AsSpan(0, _remuxLen));
        RetimeAudio();
        return Retained(SliceBindStatus.Bound, t0);
    }

    /// <summary>ADTS 프레임 → 출력 시간(비디오 첫 출력 AU 와 같은 워프 · 오프셋) · 압축 구간은 프레임 간격 미만 충돌 시 드롭.</summary>
    private void RetimeAudio()
    {
        var knots = ReEditor.Knots;
        ref readonly var src0 = ref Mux.Au(Mux.OutSourceAu(0));
        var delta = Mux.OutAu(0).Pts - NaluSmartReMuxer.Warp(knots, src0.Pts);
        long end = Mux.OutEndTicks > 0 ? Mux.OutEndTicks : long.MaxValue, next = long.MinValue;
        for (var i = 0; i < _aN; i++)
        {
            var src = (_aPtsUs[i] - _baseUs) * NaluSmartReMuxer.Clock / 1_000_000;
            if (src < src0.Pts) continue;
            var o = NaluSmartReMuxer.Warp(knots, src) + delta;
            if (o < next || o >= end) continue;
            _keep[_keepN] = i;
            _aOutUs[_keepN++] = TicksToUs(o);
            next = o + AacFrameTicks;
        }
    }

    private SliceBindStatus Done(SliceBindStatus st, long t0)
    {
        Status = st;
        LastSliceUs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return st;
    }

    private SliceBindStatus Retained(SliceBindStatus st, long t0)
    {
        Status = st;
        if (st == SliceBindStatus.Bound) Binds++;
        LastRetentionUs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return st;
    }
}
