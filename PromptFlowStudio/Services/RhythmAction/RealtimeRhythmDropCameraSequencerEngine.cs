using PromptFlowStudio.Services.BeatSync;
namespace PromptFlowStudio.Services.RhythmAction;

public enum MvSection : byte { Intro, Verse, Buildup, Drop, Breakdown }
public enum RhythmCutReason : byte { Start, Phrase, Downbeat, Beat, Accent, Drop, Pulse }

/// <summary>리듬 컷 이벤트(값 형식) — 전환 시각 · 근거 비트 시각 · 마디 · 카메라 · 사유 · 구간 · 가드 판정 · 방위.</summary>
public struct RhythmCutEvent
{
    public double TimeSec, BeatSec;
    /// <summary>v17.63.1 A/V 프레젠테이션 시각 = 검출 시각 − 룩어헤드 오프셋(오디오를 오프셋만큼 지연 송출하면 컷이 이 오디오 시각에 정렬).</summary>
    public double PresentSec;
    public int Bar;
    public byte BeatInBar;
    public MvCam From, To;
    public RhythmCutReason Reason;
    public MvSection Section;
    public RhythmCutVerdict Verdict;
    public float AzimuthDeg;
}

/// <summary>마디 마커(다운비트마다) — 시각 · 마디 · 구간 · 프레이즈 시작 · 드롭 · 다운비트 세기 비.</summary>
public struct MusicMarker
{
    public double TimeSec;
    public int Bar;
    public MvSection Section;
    public bool PhraseStart, Drop;
    public float Ratio;
}

/// <summary>
/// [PromptFlow Studio v17.54.0] 🎬 실시간 음악 마디 드롭 컷 &amp; 카메라 리듬 시퀀싱 엔진(<see cref="Step"/> 무할당 · 60 fps).
/// v17.53 <see cref="RealtimeBeatGridTrackerEngine"/> 이벤트 링에서 새 비트만 소비해 마디 · 4/8 마디 프레이즈를 세고,
/// 다운비트 직후 60 ms 광대역 RMS 를 직전 안정 다운비트 EWMA(α 0.35) 와 비교해 구간을 판정한다 — 비 ≥ 1.35 = <b>드롭</b> · 드롭 중 비 ≤ 1/1.35 = 브레이크다운 ·
/// 연속 2 마디 4 % 이상 상승 = 빌드업 · 다운비트 라벨이 4 박 배수 간격으로 2 회 이상 안정해야 드롭 판정 · 그 외 인트로 → 버스. 구간별 컷 밀도(인트로/브레이크 8 박 · 버스 4 · 빌드업 2 · 드롭 1 박 + 더블 타임 8 분)로
/// 와이드/미디엄/클로즈업/오빗을 순환 요청하고, 드롭 다운비트 = 클로즈업 서지 · 프레이즈 시작 = 와이드 재확립 · 오프비트 악센트(비트 킥 EWMA 의 1.3× 이상) = 클로즈업.
/// 모든 요청은 <see cref="RapidCutVisualFatigueGuard"/> 심사를 거쳐 64 링 이벤트로 발행된다.
/// </summary>
public sealed class RealtimeRhythmDropCameraSequencerEngine
{
    public const int Cams = RapidCutVisualFatigueGuard.Cams, CutRing = 64, MarkerRing = 32, LoudRing = 32;
    public const float DropRatio = 1.35f, AccentRatio = 1.3f, RiseRatio = 1.04f, PostBeatS = 0.06f, RefAlpha = 0.35f;
    private static readonly MvCam[] DropCycle = { MvCam.CloseUp, MvCam.Orbit, MvCam.Medium, MvCam.CloseUp, MvCam.Orbit, MvCam.Wide };
    private static readonly MvCam[] BuildCycle = { MvCam.Medium, MvCam.CloseUp, MvCam.Orbit };
    private static readonly MvCam[] VerseCycle = { MvCam.Medium, MvCam.CloseUp, MvCam.Wide };
    private static readonly MvCam[] CalmCycle = { MvCam.Wide, MvCam.Medium };

    private readonly RhythmCutEvent[] _cuts = new RhythmCutEvent[CutRing];
    private readonly MusicMarker[] _marks = new MusicMarker[MarkerRing];
    private readonly long[] _camCuts = new long[Cams];
    private long _seenBeats;
    private int _cutHead, _cutN, _markHead, _markN, _cycle, _rise, _phraseBars = 4;
    private double _now, _lastBeatT = double.NaN, _period;
    private bool _halfDone = true, _started;
    private readonly double[] _lt = new double[LoudRing];
    private readonly float[] _lm = new float[LoudRing];
    private int _lHead, _stable;
    private long _lastDownIdx = long.MinValue;
    private float _ref, _prevLoud, _beatRef, _orbitAz = 20f;
    private double _lagEma = double.NaN;

    /// <summary>
    /// v17.63.1 룩어헤드 보정 — v17.53 트래커 FFT 창 버퍼링 + 다운비트 후 60 ms 라우드니스 창 때문에 컷이 비트보다 45~60 ms 늦게 결정된다.
    /// 비트 검출 지연(now − 비트 시각)을 EWMA(α 0.2 · 0~<see cref="MaxLookaheadSec"/> 클램프)로 추정해 오디오 송출 지연 오프셋으로 노출하고,
    /// 컷 이벤트에 정렬된 프레젠테이션 시각을 기록한다(영상 결정 시각은 그대로 · 오디오만 지연 = 실시간 무역행).
    /// </summary>
    public const double MaxLookaheadSec = 0.1, LagAlpha = 0.2;
    public bool LookaheadEnabled { get; set; } = true;
    /// <summary>추정 검출 지연(s) — 비트 미수신이면 0.</summary>
    public double DetectLagSec => double.IsNaN(_lagEma) ? 0 : _lagEma;
    /// <summary>오디오 프레젠테이션 지연(s) = 룩어헤드 ON 이면 검출 지연 · OFF 면 0.</summary>
    public double AvOffsetSec => LookaheadEnabled ? DetectLagSec : 0;

    /// <summary>프레이즈 길이(4 또는 8 마디).</summary>
    public int PhraseBars { get => _phraseBars; set => _phraseBars = value >= 8 ? 8 : 4; }
    /// <summary>드롭 구간 8 분음표 컷 요청(고속 BPM 에서 가드가 0.35 s 미만을 차단).</summary>
    public bool DoubleTimeDrop { get; set; } = true;
    public float OrbitDegPerSec { get; set; } = 40f;
    public MvCam Program { get; private set; } = MvCam.Wide;
    public float ProgramAzimuthDeg { get; private set; }
    public MvSection Section { get; private set; }
    public int Bar { get; private set; } = -1;
    public int BeatInBar { get; private set; }
    public int PhraseBar { get; private set; } = -1;
    public long Drops { get; private set; }
    public long Cuts { get; private set; }
    public long Requests { get; private set; }
    public long Rejected { get; private set; }
    public long BeatsSeen { get; private set; }
    public double LastDropSec { get; private set; } = double.NaN;
    public bool DropThisStep { get; private set; }
    public float LastRatio { get; private set; } = 1f;
    public double LastStepUs { get; private set; }
    public float OrbitAzimuthDeg => _orbitAz;

    public int CutCount => _cutN;
    /// <summary>i = 0 이 최신.</summary>
    public ref readonly RhythmCutEvent Cut(int i) => ref _cuts[(_cutHead - 1 - i + 2 * CutRing) % CutRing];
    public int MarkerCount => _markN;
    public ref readonly MusicMarker Marker(int i) => ref _marks[(_markHead - 1 - i + 2 * MarkerRing) % MarkerRing];
    public long CamCuts(MvCam c) => _camCuts[(int)c];

    public static float BaseAzimuth(MvCam c) => c switch { MvCam.Medium => -30f, MvCam.CloseUp => 25f, _ => 0f };
    public float Azimuth(MvCam c) => c == MvCam.Orbit ? _orbitAz : BaseAzimuth(c);
    public static string CamName(MvCam c) => c switch { MvCam.Wide => "와이드", MvCam.Medium => "미디엄", MvCam.CloseUp => "클로즈업", _ => "오빗" };
    public static string SectionName(MvSection s) => s switch
    {
        MvSection.Intro => "인트로", MvSection.Verse => "버스", MvSection.Buildup => "빌드업", MvSection.Drop => "드롭", _ => "브레이크다운",
    };
    public static string ReasonName(RhythmCutReason r) => r switch
    {
        RhythmCutReason.Start => "시작", RhythmCutReason.Phrase => "프레이즈", RhythmCutReason.Downbeat => "다운비트", RhythmCutReason.Beat => "비트",
        RhythmCutReason.Accent => "악센트", RhythmCutReason.Drop => "드롭 서지", _ => "8 분 펄스",
    };

    public void Reset()
    {
        _seenBeats = 0; _cutHead = _cutN = _markHead = _markN = _cycle = _rise = 0;
        _now = 0; _lagEma = double.NaN; _lastBeatT = double.NaN; _period = 0; _halfDone = true; _started = false; _ref = _prevLoud = _beatRef = 0; _lHead = _stable = 0; _lastDownIdx = long.MinValue; Array.Clear(_lt); Array.Clear(_lm); _orbitAz = 20f;
        Array.Clear(_camCuts);
        Program = MvCam.Wide; ProgramAzimuthDeg = 0; Section = MvSection.Intro; Bar = -1; BeatInBar = 0; PhraseBar = -1;
        Drops = Cuts = Requests = Rejected = BeatsSeen = 0; LastDropSec = double.NaN; DropThisStep = false; LastRatio = 1f;
    }

    /// <summary>프레임(무할당) — now = 오디오 시계(s).</summary>
    /// <param name="blockMeanSquare">이번 블록(끝 = now · 길이 dt) 광대역 평균 제곱 — 다운비트 직후 라우드니스 측정용.</param>
    public void Step(double now, float dt, RealtimeBeatGridTrackerEngine tracker, RapidCutVisualFatigueGuard guard, float blockMeanSquare)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _now = now; DropThisStep = false;
        _lt[_lHead] = now - dt; _lm[_lHead] = blockMeanSquare; _lHead = (_lHead + 1) % LoudRing;
        _orbitAz += OrbitDegPerSec * dt;
        if (_orbitAz > 180f) _orbitAz -= 360f;
        if (!_started)
        {
            _started = true;
            guard.Commit(now, (int)MvCam.Wide, 0f);
            Log(MvCam.Wide, MvCam.Wide, RhythmCutReason.Start, RhythmCutVerdict.Approved, 0f, now);
        }
        if (tracker.Beats < _seenBeats) _seenBeats = 0;
        var fresh = (int)Math.Min(tracker.Beats - _seenBeats, tracker.EventCount);
        for (var k = fresh - 1; k >= 0; k--) OnBeat(in tracker.Event(k), guard);
        _seenBeats = tracker.Beats;
        _period = tracker.Bpm > 0 ? 60.0 / tracker.Bpm : 0;
        if (Section == MvSection.Drop && DoubleTimeDrop && !_halfDone && _period > 0 && now >= _lastBeatT + 0.5 * _period)
        {
            _halfDone = true;
            Request(Next(DropCycle), RhythmCutReason.Pulse, false, guard, _lastBeatT + 0.5 * _period);
        }
        if (Program == MvCam.Orbit) _orbitAz = guard.ConstrainOnAir(_orbitAz);
        else guard.ConstrainOnAir(BaseAzimuth(Program));
        ProgramAzimuthDeg = Azimuth(Program);
        guard.Observe(now);
        LastStepUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private void OnBeat(in BeatEvent e, RapidCutVisualFatigueGuard guard)
    {
        BeatsSeen++;
        _lastBeatT = e.TimeSec; _halfDone = false;
        var lag = Math.Clamp(_now - e.TimeSec, 0, MaxLookaheadSec);
        _lagEma = double.IsNaN(_lagEma) ? lag : _lagEma + LagAlpha * (lag - _lagEma);
        var drop = false; var phrase = false;
        if (e.Downbeat)
        {
            Bar++; BeatInBar = 0; PhraseBar++;
            // 다운비트 직후 60 ms 광대역 RMS(베이스 근음 · 대역 편중에 둔감) · 라벨 안정(4 박 배수 간격 — 라벨 누락 마디 허용) 다운비트만 기준 갱신
            _stable = _lastDownIdx != long.MinValue && e.Index > _lastDownIdx && (e.Index - _lastDownIdx) % 4 == 0 ? _stable + 1 : 0;
            _lastDownIdx = e.Index;
            var s = PostBeatRms(e.TimeSec);
            var ratio = _ref > 1e-6f ? s / _ref : 1f;
            LastRatio = ratio;
            if (_stable >= 1 && _prevLoud > 0) _rise = s > _prevLoud * RiseRatio ? _rise + 1 : 0;
            if (Bar >= 4 && _stable >= 2 && ratio >= DropRatio && Section != MvSection.Drop)
            {
                Section = MvSection.Drop; drop = true; Drops++; LastDropSec = e.TimeSec; DropThisStep = true; PhraseBar = 0; _cycle = 0; _rise = 0;
            }
            else if (Section == MvSection.Drop && ratio <= 1f / DropRatio) { Section = MvSection.Breakdown; PhraseBar = 0; _rise = 0; }
            else if (Section != MvSection.Drop)
            {
                if (_rise >= 2) Section = MvSection.Buildup;
                else if (Section == MvSection.Buildup && _rise == 0) Section = MvSection.Verse;
                else if (Section is MvSection.Intro or MvSection.Breakdown && PhraseBar >= _phraseBars) Section = MvSection.Verse;
            }
            if (_stable >= 1) { _ref = _ref <= 0 ? s : _ref + RefAlpha * (s - _ref); _prevLoud = s; }
            phrase = PhraseBar % _phraseBars == 0;
            ref var m = ref _marks[_markHead];
            m.TimeSec = e.TimeSec; m.Bar = Bar; m.Section = Section; m.PhraseStart = phrase; m.Drop = drop; m.Ratio = ratio;
            _markHead = (_markHead + 1) % MarkerRing; if (_markN < MarkerRing) _markN++;
        }
        else BeatInBar++;
        var accentRef = _beatRef;
        _beatRef = _beatRef <= 0 ? e.Strength : _beatRef + 0.1f * (e.Strength - _beatRef);
        if (Bar < 0) return;
        if (drop) { Request(MvCam.CloseUp, RhythmCutReason.Drop, true, guard, e.TimeSec); return; }
        if (phrase) { Request(MvCam.Wide, RhythmCutReason.Phrase, false, guard, e.TimeSec); return; }
        var density = Section switch { MvSection.Drop => 1, MvSection.Buildup => 2, MvSection.Verse => 4, _ => 8 };
        var pos = Math.Max(PhraseBar, 0) * 4 + BeatInBar;
        if (pos % density == 0)
        {
            var cyc = Section switch { MvSection.Drop => DropCycle, MvSection.Buildup => BuildCycle, MvSection.Verse => VerseCycle, _ => CalmCycle };
            Request(Next(cyc), e.Downbeat ? RhythmCutReason.Downbeat : RhythmCutReason.Beat, false, guard, e.TimeSec);
        }
        else if (!e.Downbeat && Section != MvSection.Drop && accentRef > 0 && e.Strength >= AccentRatio * accentRef)
            Request(MvCam.CloseUp, RhythmCutReason.Accent, true, guard, e.TimeSec);
    }

    /// <summary>비트 시각부터 60 ms(링에 있는 블록) 평균 제곱 → RMS.</summary>
    private float PostBeatRms(double beatT)
    {
        double sum = 0; var n = 0;
        for (var i = 0; i < LoudRing; i++)
        {
            var t = _lt[i];
            if (t >= beatT - 0.009 && t < beatT + PostBeatS && _lm[i] > 0) { sum += _lm[i]; n++; }
        }
        return n > 0 ? (float)Math.Sqrt(sum / n) : 0f;
    }

    private MvCam Next(MvCam[] cycle)
    {
        var c = cycle[_cycle++ % cycle.Length];
        if (c == Program) c = cycle[_cycle++ % cycle.Length];
        return c;
    }

    private void Request(MvCam to, RhythmCutReason why, bool accent, RapidCutVisualFatigueGuard guard, double beatSec)
    {
        if (to == Program) return;
        Requests++;
        var az = Azimuth(to);
        var v = guard.Review(_now, (int)Program, (int)to, ref az, accent);
        if (v is not (RhythmCutVerdict.Approved or RhythmCutVerdict.AxisClamped)) { Rejected++; return; }
        if (to == MvCam.Orbit) _orbitAz = az;
        guard.Commit(_now, (int)to, az);
        Log(Program, to, why, v, az, beatSec);
        Program = to; ProgramAzimuthDeg = az;
        Cuts++; _camCuts[(int)to]++;
    }

    private void Log(MvCam from, MvCam to, RhythmCutReason why, RhythmCutVerdict v, float az, double beatSec)
    {
        ref var c = ref _cuts[_cutHead];
        c.TimeSec = _now; c.PresentSec = _now - AvOffsetSec; c.BeatSec = beatSec; c.Bar = Bar; c.BeatInBar = (byte)BeatInBar; c.From = from; c.To = to;
        c.Reason = why; c.Section = Section; c.Verdict = v; c.AzimuthDeg = az;
        _cutHead = (_cutHead + 1) % CutRing; if (_cutN < CutRing) _cutN++;
    }
}
