using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Localization;

namespace PromptFlowStudio.Services.LiveStream;

public enum LiveSourceKind : byte { Camera, Gaussian3d, Icvfx }
public enum LiveTransition : byte { Cut, Dissolve, Wipe }
public enum SwitchReason : byte { Speech, Motion, Director, MaxHold, Manual }

/// <summary>입력 소스(지연 = 캡처 → 스위처 도착까지의 영상 프레임 수).</summary>
public sealed record LiveSource(string Name, LiveSourceKind Kind, int LatencyFrames, uint Tint);

/// <summary>전환 기록.</summary>
public readonly record struct SwitchEvent(long Frame, int From, int To, LiveTransition Transition, SwitchReason Reason, float Score);

/// <summary>
/// [PromptFlow Studio v16.71.0] AI 라이브 방송 자율 카메라 스위칭 엔진.
/// 60fps · 48kHz(800샘플/프레임) 단일 타임라인에서 멀티캠 · 3DGS 가상캠 · ICVFX 소스를 프레임 링(깊이 8)으로 받아,
/// 소스별 영상 지연을 오디오 지연선으로 보상(최대 지연 정렬)해 A/V 를 맞춘다. 매 프레임 소스별 모션 에너지(SIMD 절대차) · 발화 RMS ·
/// 디렉터 바이어스로 점수를 내고 최소/최대 홀드 · 히스테리시스로 전환을 결정 — 발화 전환은 Cut, 가상↔실사는 Wipe, 그 외 Dissolve(SIMD 보간).
/// 오디오는 등전력 크로스페이드(Cut 은 32샘플 마이크로 페이드). 플래시/클랩 마커로 A/V 오프셋을 실측하는 싱크 모니터 포함. 핫패스 무할당.
/// 데모 소스는 절차적 합성 프레임이며, 실 피드는 <see cref="Submit"/> 로 주입한다.
/// </summary>
public sealed class AiLiveBroadcastAutonomousSwitchingEngine
{
    public const int Fps = 60, SampleRate = 48000, SamplesPerFrame = SampleRate / Fps, W = 128, H = 72, FrameBytes = W * H * 4, Ring = 8, MaxSources = 8, LogSize = 64;
    public const int DissolveFrames = 12, WipeFrames = 18, MicroFade = 32;

    private readonly object _gate = new();
    private readonly LiveSource[] _src = new LiveSource[MaxSources];
    private readonly byte[] _video = new byte[MaxSources * Ring * FrameBytes];
    private readonly float[] _audio = new float[MaxSources * Ring * SamplesPerFrame];
    private readonly long[] _videoTime = new long[MaxSources * Ring], _audioTime = new long[MaxSources * Ring];
    private readonly byte[] _program = new byte[FrameBytes];
    private readonly float[] _programAudio = new float[SamplesPerFrame];
    private readonly float[] _motion = new float[MaxSources], _speech = new float[MaxSources], _bias = new float[MaxSources], _score = new float[MaxSources];
    private readonly float[] _demoMotion = new float[MaxSources], _demoSpeech = new float[MaxSources], _phase = new float[MaxSources], _blockX = new float[MaxSources];
    private readonly long[] _clapAt = new long[MaxSources];
    private readonly SwitchEvent[] _log = new SwitchEvent[LogSize];
    private int _count, _maxLatency, _live, _preview = -1, _from = -1, _transFrames, _transPos, _logHead, _manual = -1;
    private LiveTransition _trans, _manualTrans;
    private long _frame, _liveSince, _switches, _lastFlash = long.MinValue, _lastClick = long.MinValue;
    private float _avOffsetMs, _renderMicros;
    private bool _compensate = true;

    public AiLiveBroadcastAutonomousSwitchingEngine()
    {
        Array.Fill(_clapAt, long.MinValue);
        AddSource(new LiveSource("CAM1 앵커 클로즈업", LiveSourceKind.Camera, 1, 0xFF3A4F8C));
        AddSource(new LiveSource("CAM2 게스트", LiveSourceKind.Camera, 1, 0xFF8C4F3A));
        AddSource(new LiveSource("CAM3 와이드", LiveSourceKind.Camera, 2, 0xFF3A8C5C));
        AddSource(new LiveSource("CAM4 지브 크레인", LiveSourceKind.Camera, 2, 0xFF7A3A8C));
        AddSource(new LiveSource("3DGS 가상캠", LiveSourceKind.Gaussian3d, 4, 0xFF2A7A8C));
        AddSource(new LiveSource("ICVFX LED 볼륨", LiveSourceKind.Icvfx, 3, 0xFF8C7A2A));
    }

    public event Action? OnChange;

    // ── 설정/상태 ──
    public int MinHoldFrames { get; set; } = 90;
    public int MaxHoldFrames { get; set; } = 600;
    public float Hysteresis { get; set; } = 0.25f;
    public bool CompensateLatency { get { lock (_gate) return _compensate; } set { lock (_gate) _compensate = value; } }
    public int SourceCount { get { lock (_gate) return _count; } }
    public LiveSource SourceAt(int i) { lock (_gate) return _src[i]; }
    public int Live { get { lock (_gate) return _live; } }
    public int Preview { get { lock (_gate) return _preview; } }
    public bool InTransition { get { lock (_gate) return _transFrames > 0; } }
    public LiveTransition CurrentTransition { get { lock (_gate) return _trans; } }
    public long Frame { get { lock (_gate) return _frame; } }
    public long Switches => Interlocked.Read(ref _switches);
    public float AvOffsetMs { get { lock (_gate) return _avOffsetMs; } }
    public float RenderMicros { get { lock (_gate) return _renderMicros; } }
    public int MaxLatencyFrames { get { lock (_gate) return _maxLatency; } }
    public float PipelineDelayMs { get { lock (_gate) return (_compensate ? _maxLatency : 0) * 1000f / Fps; } }
    public string? LastAction { get; private set; }
    public ReadOnlySpan<float> ProgramAudio => _programAudio;

    public float ScoreOf(int s) { lock (_gate) return _score[s]; }
    public float MotionOf(int s) { lock (_gate) return _motion[s]; }
    public float SpeechOf(int s) { lock (_gate) return _speech[s]; }

    public int AddSource(LiveSource s)
    {
        lock (_gate)
        {
            if (_count >= MaxSources) return -1;
            _src[_count] = s with { LatencyFrames = Math.Clamp(s.LatencyFrames, 0, Ring - 2) };
            _maxLatency = Math.Max(_maxLatency, _src[_count].LatencyFrames);
            return _count++;
        }
    }

    /// <summary>데모 소스 활동(모션 0..1 · 발화 0..1).</summary>
    public void SetActivity(int s, float motion, float speech) { lock (_gate) { _demoMotion[s] = Math.Clamp(motion, 0, 1); _demoSpeech[s] = Math.Clamp(speech, 0, 1); } }

    /// <summary>디렉터(시청자 반응) 바이어스.</summary>
    public void SetBias(ReadOnlySpan<float> bias) { lock (_gate) bias[..Math.Min(bias.Length, _count)].CopyTo(_bias); }

    /// <summary>캡처 시각 <paramref name="frame"/> 에 플래시+클랩 마커(싱크 측정).</summary>
    public void ScheduleClap(int s, long frame) { lock (_gate) _clapAt[s] = frame; }

    /// <summary>수동 Take(다음 렌더 프레임에 적용).</summary>
    public void Take(int s, LiveTransition t) { lock (_gate) { _manual = s; _manualTrans = t; } }

    public int CopyLog(Span<SwitchEvent> dst)
    {
        lock (_gate)
        {
            var n = (int)Math.Min(Math.Min(dst.Length, LogSize), _switches);
            for (var i = 0; i < n; i++) dst[i] = _log[(_logHead - 1 - i + LogSize * 2) % LogSize];
            return n;
        }
    }

    /// <summary>실 피드 주입: 캡처 시각 기준 영상(RGBA W×H) · 오디오(800샘플).</summary>
    public void Submit(int s, long captureFrame, ReadOnlySpan<byte> rgba, ReadOnlySpan<float> pcm)
    {
        lock (_gate)
        {
            var slot = s * Ring + (int)(captureFrame % Ring);
            if (!rgba.IsEmpty) { rgba[..FrameBytes].CopyTo(_video.AsSpan(slot * FrameBytes, FrameBytes)); _videoTime[slot] = captureFrame; }
            if (!pcm.IsEmpty) { pcm[..SamplesPerFrame].CopyTo(_audio.AsSpan(slot * SamplesPerFrame, SamplesPerFrame)); _audioTime[slot] = captureFrame; }
        }
    }

    /// <summary>데모 소스 캡처(절차적 · 무할당): 틴트 배경 + 모션 블록 + 발화 톤 + 클랩 마커.</summary>
    public void CaptureDemo(long frame)
    {
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Live.Switch.Capture");
            for (var s = 0; s < _count; s++)
            {
                var slot = s * Ring + (int)(frame % Ring);
                var px = MemoryMarshal.Cast<byte, uint>(_video.AsSpan(slot * FrameBytes, FrameBytes));
                var clap = _clapAt[s] == frame;
                px.Fill(clap ? 0xFFFFFFFF : _src[s].Tint);
                if (!clap)
                {
                    _blockX[s] = (_blockX[s] + 0.5f + 6f * _demoMotion[s]) % (W - 24);
                    var bx = (int)_blockX[s]; var by = 18 + (int)(8 * MathF.Sin(frame * 0.07f + s));
                    for (var y = by; y < by + 24; y++) px.Slice(y * W + bx, 24).Fill(0xFFE8E0D0);
                    var mouth = (int)(10 * _demoSpeech[s] * (0.5f + 0.5f * MathF.Sin(frame * 0.9f + s)));
                    for (var y = 56; y < 58 + mouth / 3; y++) px.Slice(y * W + 54, 20).Fill(0xFF201010);
                }
                _videoTime[slot] = frame;
                var a = _audio.AsSpan(slot * SamplesPerFrame, SamplesPerFrame);
                var amp = 0.45f * _demoSpeech[s];
                var w = 2 * MathF.PI * (140 + 45 * s) / SampleRate;
                var ph = _phase[s];
                for (var i = 0; i < SamplesPerFrame; i++)
                {
                    a[i] = amp * (0.6f + 0.4f * MathF.Sin(ph * 0.031f)) * MathF.Sin(ph);
                    ph += w;
                }
                _phase[s] = ph % (2 * MathF.PI * 1000);
                if (clap) a[0] = 1f;
                _audioTime[slot] = frame;
            }
        }
    }

    /// <summary>
    /// 프로그램 1프레임 렌더(무할당): 정렬 시각 결정 → 특징(모션 · 발화) → 자율 결정 → 전환 합성(영상 SIMD · 오디오 등전력) → 싱크 모니터.
    /// </summary>
    public void RenderFrame(long frame)
    {
        var t0 = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            using var guard = ZeroGcRegionScope.Enter("Live.Switch.Render");
            _frame = frame;
            for (var s = 0; s < _count; s++)
            {
                var vt = VideoTime(s, frame);
                _motion[s] += 0.25f * (MotionEnergy(s, vt) - _motion[s]);
                var rms = Rms(AudioSpan(s, AudioTime(s, frame)));
                _speech[s] += (rms > _speech[s] ? 0.5f : 0.08f) * (rms - _speech[s]);
            }
            Decide(frame);

            var live = VideoSpan(_live, VideoTime(_live, frame));
            var liveA = AudioSpan(_live, AudioTime(_live, frame));
            if (_transFrames > 0 && _from >= 0)
            {
                var old = VideoSpan(_from, VideoTime(_from, frame));
                var oldA = AudioSpan(_from, AudioTime(_from, frame));
                var p0 = (float)_transPos / _transFrames;
                var p1 = (float)(_transPos + 1) / _transFrames;
                switch (_trans)
                {
                    case LiveTransition.Dissolve:
                        RealtimeNeuralAudioDubbingEngine.LerpRow(old, live, (ushort)(256 * p1), _program);
                        break;
                    case LiveTransition.Wipe:
                        var edge = (int)(W * p1);
                        for (var y = 0; y < H; y++)
                        {
                            var row = y * W * 4;
                            live.Slice(row, edge * 4).CopyTo(_program.AsSpan(row));
                            old.Slice(row + edge * 4, (W - edge) * 4).CopyTo(_program.AsSpan(row + edge * 4));
                        }
                        break;
                    default:
                        live.CopyTo(_program);
                        break;
                }
                for (var i = 0; i < SamplesPerFrame; i++)
                {
                    var p = _trans == LiveTransition.Cut ? Math.Min(1f, (float)i / MicroFade) : p0 + (p1 - p0) * i / SamplesPerFrame;
                    var g = p * MathF.PI / 2;
                    _programAudio[i] = oldA[i] * MathF.Cos(g) + liveA[i] * MathF.Sin(g);
                }
                if (++_transPos >= _transFrames) { _transFrames = 0; _from = -1; }
            }
            else
            {
                live.CopyTo(_program);
                liveA.CopyTo(_programAudio);
            }

            // 싱크 모니터: 프로그램 플래시(평균 G > 240) · 클릭(피크 > 0.9) 프레임 차.
            var g2 = 0; for (var i = 1; i < FrameBytes; i += 64) g2 += _program[i];
            if (g2 > 240 * (FrameBytes / 64)) _lastFlash = frame;
            var peak = 0f; foreach (var v in _programAudio) peak = MathF.Max(peak, MathF.Abs(v));
            if (peak > 0.9f) _lastClick = frame;
            if (_lastFlash != long.MinValue && _lastClick != long.MinValue && Math.Abs(_lastFlash - _lastClick) <= 30 && (frame == _lastFlash || frame == _lastClick))
                _avOffsetMs = (_lastClick - _lastFlash) * 1000f / Fps;
            _renderMicros = (float)((Stopwatch.GetTimestamp() - t0) * 1e6 / Stopwatch.Frequency);
        }
    }

    private long VideoTime(int s, long frame) => _compensate ? frame - _maxLatency : frame - _src[s].LatencyFrames;
    private long AudioTime(int s, long frame) => _compensate ? frame - _maxLatency : frame;

    private Span<byte> VideoSpan(int s, long t) => _video.AsSpan((s * Ring + (int)(((t % Ring) + Ring) % Ring)) * FrameBytes, FrameBytes);
    private Span<float> AudioSpan(int s, long t) => _audio.AsSpan((s * Ring + (int)(((t % Ring) + Ring) % Ring)) * SamplesPerFrame, SamplesPerFrame);

    private void Decide(long frame)
    {
        if (_manual >= 0)
        {
            var m = _manual; _manual = -1;
            if (m != _live && m < _count) Switch(frame, m, _manualTrans, SwitchReason.Manual, _score[m]);
            return;
        }
        var bestSpeech = 0f;
        for (var s = 0; s < _count; s++) bestSpeech = MathF.Max(bestSpeech, _speech[s]);
        var best = -1; var second = -1;
        for (var s = 0; s < _count; s++)
        {
            var speech = bestSpeech > 0.02f ? _speech[s] / bestSpeech : 0;
            _score[s] = 1.0f * speech + 0.6f * MathF.Min(1, _motion[s] * 30) + _bias[s];
            if (best < 0 || _score[s] > _score[best]) { second = best; best = s; }
            else if (second < 0 || _score[s] > _score[second]) second = s;
        }
        _preview = best == _live ? second : best;
        if (_transFrames > 0) return;
        var held = frame - _liveSince;
        if (held < MinHoldFrames) return;
        var cur = _score[_live];
        if (best != _live && _score[best] > cur * (1 + Hysteresis) + 0.05f)
        {
            var speechDriven = _speech[best] > 0.05f && _speech[best] >= bestSpeech * 0.99f && _speech[_live] < _speech[best] * 0.5f;
            var reason = speechDriven ? SwitchReason.Speech : _bias[best] > 0.3f ? SwitchReason.Director : SwitchReason.Motion;
            Switch(frame, best, PickTransition(best, speechDriven), reason, _score[best]);
        }
        else if (held >= MaxHoldFrames && _preview >= 0)
            Switch(frame, _preview, PickTransition(_preview, false), SwitchReason.MaxHold, _score[_preview]);
    }

    private LiveTransition PickTransition(int to, bool speechDriven)
    {
        if ((_src[to].Kind == LiveSourceKind.Camera) != (_src[_live].Kind == LiveSourceKind.Camera)) return LiveTransition.Wipe;
        return speechDriven ? LiveTransition.Cut : LiveTransition.Dissolve;
    }

    private void Switch(long frame, int to, LiveTransition t, SwitchReason reason, float score)
    {
        _from = _live; _live = to; _liveSince = frame; _trans = t;
        _transFrames = t switch { LiveTransition.Dissolve => DissolveFrames, LiveTransition.Wipe => WipeFrames, _ => 1 };
        _transPos = 0;
        _log[_logHead] = new SwitchEvent(frame, _from, to, t, reason, score);
        _logHead = (_logHead + 1) % LogSize;
        Interlocked.Increment(ref _switches);
    }

    /// <summary>현재 프레임과 직전 프레임의 평균 절대차(0..1) — Vector&lt;byte&gt; 절대차 · 2단 확장 합산.</summary>
    private float MotionEnergy(int s, long t)
    {
        var a = VideoSpan(s, t); var b = VideoSpan(s, t - 1);
        return MeanAbsDiff(a, b);
    }

    public static float MeanAbsDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var n = Math.Min(a.Length, b.Length);
        if (n == 0) return 0;
        ulong sum = 0; var i = 0;
        if (Vector.IsHardwareAccelerated && n >= Vector<byte>.Count)
        {
            var acc = Vector<uint>.Zero;
            for (; i <= n - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                var va = new Vector<byte>(a[i..]); var vb = new Vector<byte>(b[i..]);
                var d = Vector.Max(va, vb) - Vector.Min(va, vb);
                Vector.Widen(d, out var lo, out var hi);
                Vector.Widen(lo + hi, out var l2, out var h2);
                acc += l2 + h2;
            }
            for (var k = 0; k < Vector<uint>.Count; k++) sum += acc[k];
        }
        for (; i < n; i++) sum += (uint)Math.Abs(a[i] - b[i]);
        return (float)(sum / (255.0 * n));
    }

    public static float Rms(ReadOnlySpan<float> x)
    {
        var e = LowSpec.DynamicLowBitQuantizationFallbackRouter.DotF32(x, x);
        return x.Length > 0 ? MathF.Sqrt(e / x.Length) : 0;
    }

    /// <summary>프로그램/소스 프레임 복사(UI 썸네일 · 인코더 입력). <paramref name="s"/> = -1 이면 프로그램.</summary>
    public void CopyFrame(int s, Span<byte> dst)
    {
        lock (_gate)
        {
            if (s < 0) _program.CopyTo(dst);
            else VideoSpan(s, VideoTime(s, _frame)).CopyTo(dst);
        }
    }

    public void Announce(string action)
    {
        LastAction = action;
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Live.Switch.OnChange", ex); }
    }
}
