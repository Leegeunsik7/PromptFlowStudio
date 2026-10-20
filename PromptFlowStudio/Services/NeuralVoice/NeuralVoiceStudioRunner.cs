using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.FacialPerformance;
using PromptFlowStudio.Services.HoloTelepresence;
using PromptFlowStudio.Services.SkinOptics;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.NeuralVoice;

/// <summary>
/// [PromptFlow Studio v17.18.0] 🎙️ AI 뉴럴 음성 &amp; 성도 음향 스튜디오 러너(60 Hz 전용 스레드 · 결정적).
/// v17.00 AGI 디렉터 비트 → 대사 선택 · v17.17 혈류 합성기(정서 → 분노/수줍음/긴장/심박) → 감정 운율 →
/// 160 샘플 청크 × 5(800 = 1 틱) 음성/성도 합성 → 팝 필터 + 소프트 니 리미터 → v17.12 Audio2Face 분석(지연 교차 검증) →
/// 표시 시각 락 립싱크 가중치 · 스펙트로그램(1024 FFT) · 포먼트 트랙.
/// </summary>
public sealed class NeuralVoiceStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, TickSamples = BiomechanicalVocalTractResonanceSynthesizer.Fs / Hz, SpecCols = 160, SpecBins = 96, OutRing = 4096, Fft = 1024;
    public const float Dt = 1f / Hz;
    public static readonly string[] Lines =
    {
        "안녕하세요, 오늘 무대를 시작합니다.",
        "Stop! Put the bag back, please.",
        "정말 믿을 수 없어요! 어떻게 이럴 수가 있죠?",
        "Maybe... we should talk about it tomorrow?",
        "괜찮아요, 천천히 다시 해 봐요.",
        "Bob picked a big pink apple.",
    };

    private readonly object _gate = new();
    private readonly float[] _raw = new float[RealtimeNeuralVoiceSynthesisEngine.Chunk], _out = new float[RealtimeNeuralVoiceSynthesisEngine.Chunk];
    private readonly float[] _ring = new float[OutRing];
    private readonly float[] _re = new float[Fft], _im = new float[Fft], _hann = new float[Fft];
    private readonly float[] _spec = new float[SpecCols * SpecBins];
    private readonly float[] _formTrack = new float[SpecCols * 4];
    private readonly float[] _msS = new float[SeriesLen], _f0S = new float[SeriesLen], _enS = new float[SeriesLen], _grS = new float[SeriesLen], _lagS = new float[SeriesLen];
    private readonly byte[] _bmp = new byte[54 + SpecCols * SpecBins * 3];
    private readonly FftPlan _fft = new(Fft);
    private readonly SeparableNeuralSkinSssEngine _skin = new();
    private int _ringHead, _specHead, _specCount, _seriesHead, _seriesCount, _beat = -1, _line;
    private long _a2fHops;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;

    public NeuralVoiceStudioRunner(IServiceProvider sp) : this() { }

    public NeuralVoiceStudioRunner()
    {
        Tract = new BiomechanicalVocalTractResonanceSynthesizer();
        Voice = new RealtimeNeuralVoiceSynthesisEngine(Tract);
        Guard = new AcousticVisualLatencyPlosiveGuard(Voice);
        Director = new UniversalAgiCinematicDirectorSynthesizer();
        Vascular = new MicroVascularHemodynamicsFlushingSynthesizer(_skin);
        A2F = new AudioToFaceVisemeMotionSynthesizer();
        for (var i = 0; i < Fft; i++) _hann[i] = 0.5f - 0.5f * MathF.Cos(MathF.Tau * i / Fft);
        Voice.SetText(Lines[0]);
        LastAction = "📋 대기 · 성도 " + Tract.Sections + " 구간(" + Tract.TractLengthCm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " cm) · 청크 " + RealtimeNeuralVoiceSynthesisEngine.Chunk + " 샘플 · " + Tract.Path;
    }

    public event Action? OnChange;

    public BiomechanicalVocalTractResonanceSynthesizer Tract { get; }
    public RealtimeNeuralVoiceSynthesisEngine Voice { get; }
    public AcousticVisualLatencyPlosiveGuard Guard { get; }
    public UniversalAgiCinematicDirectorSynthesizer Director { get; }
    public MicroVascularHemodynamicsFlushingSynthesizer Vascular { get; }
    public AudioToFaceVisemeMotionSynthesizer A2F { get; }
    public bool Motion { get; set; } = true;
    /// <summary>감정 = 디렉터/v17.17 혈류(참) 또는 수동(거짓).</summary>
    public bool DirectorEmotion { get; set; } = true;
    public bool AutoLines { get; set; } = true;
    public float ManualAnger { get; set; }
    public float ManualJoy { get; set; } = 0.3f;
    public float ManualTension { get; set; }
    public float ManualShy { get; set; }
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public int LineIndex => _line;
    public string CurrentLine { get; private set; } = Lines[0];
    public int SpecCount => _specCount;
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float F0Series(int i) => _f0S[Idx(i)];
    public float EnergySeries(int i) => _enS[Idx(i)];
    public float GainReductionSeries(int i) => _grS[Idx(i)];
    public float LagSeries(int i) => _lagS[Idx(i)];
    public float Sample(int back) => _ring[(_ringHead - 1 - back + 2 * OutRing) % OutRing];
    /// <summary>포먼트 트랙(col 0 = 최신 · k = 0..3).</summary>
    public float FormantTrack(int col, int k) => _formTrack[((_specHead - 1 - col + 2 * SpecCols) % SpecCols) * 4 + k];

    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    // ── 제어 평면 ───────────────────────────────────────────

    public void Say(string text)
    {
        lock (_gate) { Voice.SetText(text); CurrentLine = text; AutoLines = false; }
        LastAction = "🗨 대사: " + text;
        Raise();
    }

    public void SayLine(int i)
    {
        lock (_gate) { _line = ((i % Lines.Length) + Lines.Length) % Lines.Length; Voice.SetText(Lines[_line]); CurrentLine = Lines[_line]; }
        LastAction = "🗨 대사 " + (_line + 1) + ": " + CurrentLine;
        Raise();
    }

    public void SetSpeakers(SpeakerPreset a, SpeakerPreset b, float morph)
    {
        lock (_gate) { Voice.Primary = a; Voice.Secondary = b; Voice.Morph = Math.Clamp(morph, 0f, 1f); }
        LastAction = "🎭 음색 모핑 " + a + " → " + b + " α " + F2(Voice.Morph);
        Raise();
    }

    public void SetManualEmotion(float anger, float joy, float tension, float shy)
    {
        lock (_gate) { DirectorEmotion = false; ManualAnger = anger; ManualJoy = joy; ManualTension = tension; ManualShy = shy; }
        LastAction = "🎚 수동 감정 · 분노 " + F2(anger) + " · 기쁨 " + F2(joy) + " · 긴장 " + F2(tension) + " · 수줍음 " + F2(shy);
        Raise();
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당): 감정 → 800 샘플 합성/가드 → A2F → 립싱크 락 → 스펙트로그램.</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("NeuralVoice.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!Motion) return;
            if (DirectorEmotion)
            {
                Director.Tick(Dt);
                Vascular.SetEmotion(Director.Valence, Director.Arousal, Director.Tension, ReadOnlySpan<float>.Empty);
                Vascular.Update(Dt);
                Voice.Emotion = new VoiceEmotion(Vascular.Anger, MathF.Max(0f, Director.Valence) * (1f - Vascular.Tension), Vascular.Tension, Vascular.Shyness, Vascular.Bpm);
                if (AutoLines && Director.CurrentBeat != _beat)
                {
                    _beat = Director.CurrentBeat;
                    _line = _beat % Lines.Length;
                    Voice.SetText(Lines[_line]);
                    CurrentLine = Lines[_line];
                }
            }
            else Voice.Emotion = new VoiceEmotion(ManualAnger, ManualJoy, ManualTension, ManualShy, 62f + 50f * ManualAnger + 25f * ManualTension);
            for (var c = 0; c < TickSamples / RealtimeNeuralVoiceSynthesisEngine.Chunk; c++)
            {
                Voice.RenderChunk(_raw);
                Guard.Process(_raw, _out);
                A2F.Push(_out);
                if (A2F.Hops != _a2fHops) { _a2fHops = A2F.Hops; Guard.RecordJaw(A2F.Weight(0), Voice.SampleClock); }
                for (var i = 0; i < _out.Length; i++) { _ring[_ringHead] = _out[i]; _ringHead = (_ringHead + 1) % OutRing; }
            }
            Guard.VisualWeights(Voice.SampleClock);
            if ((_tick & 15) == 0) Guard.MeasureA2fLag();
            Spectrum();
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _f0S[h] = Voice.F0Sample(0); _enS[h] = Voice.CurrentEnergy; _grS[h] = Guard.GainReductionDb; _lagS[h] = Guard.A2fLagMs;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    private void Spectrum()
    {
        for (var i = 0; i < Fft; i++) { _re[i] = Sample(Fft - 1 - i) * _hann[i]; _im[i] = 0f; }
        _fft.Transform(_re, _im, false);
        var col = _specHead * SpecBins;
        for (var b = 0; b < SpecBins; b++)
        {
            var bin = 1 + b * (5000 * Fft / BiomechanicalVocalTractResonanceSynthesizer.Fs) / SpecBins;
            var p = _re[bin] * _re[bin] + _im[bin] * _im[bin];
            _spec[col + b] = Math.Clamp((10f * MathF.Log10(p + 1e-12f) + 60f) / 70f, 0f, 1f);
        }
        Tract.UpdateFormants();
        for (var k = 0; k < 4; k++) _formTrack[_specHead * 4 + k] = Tract.Formant(k);
        _specHead = (_specHead + 1) % SpecCols;
        if (_specCount < SpecCols) _specCount++;
    }

    private static byte B8(float v) => (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);

    /// <summary>스펙트로그램 BMP(가로 = 시간 · 세로 = 0–5 kHz · 포먼트 F1–F4 점).</summary>
    public string SpectrogramBase64()
    {
        lock (_gate)
        {
            var d = _bmp.AsSpan();
            d.Slice(0, 54).Clear();
            d[0] = (byte)'B'; d[1] = (byte)'M';
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(2), _bmp.Length);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(10), 54);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(14), 40);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(18), SpecCols);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(22), -SpecBins);
            d[26] = 1; d[28] = 24;
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(d.Slice(34), _bmp.Length - 54);
            for (var y = 0; y < SpecBins; y++)
                for (var x = 0; x < SpecCols; x++)
                {
                    var back = SpecCols - 1 - x;
                    var v = back < _specCount ? _spec[((_specHead - 1 - back + 2 * SpecCols) % SpecCols) * SpecBins + (SpecBins - 1 - y)] : 0f;
                    float r = MathF.Min(1f, v * 1.6f), g = MathF.Max(0f, v * 1.4f - 0.4f), b = MathF.Min(1f, 0.25f + v * 0.9f) * (1f - v * 0.6f);
                    if (back < _specCount)
                        for (var k = 0; k < 4; k++)
                        {
                            var fy = SpecBins - 1 - (int)(FormantTrack(back, k) / 5000f * SpecBins);
                            if (fy == y && FormantTrack(back, k) > 0f) { r = 1f; g = 1f; b = 1f; }
                        }
                    var o = 54 + (y * SpecCols + x) * 3;
                    d[o] = B8(b); d[o + 1] = B8(g); d[o + 2] = B8(r);
                }
            return Convert.ToBase64String(_bmp);
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.NeuralVoice" };
            _loop.Start();
            LastAction = "▶ 음성 합성 가동 · 48 kHz · 청크 " + F2(Voice.ChunkMs) + " ms · 감정 운율 → 성도 도파관 → 팝/리미터 → 립싱크 락";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("NeuralVoice.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("NeuralVoice.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
