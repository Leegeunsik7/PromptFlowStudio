using System.Numerics;
using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.Diagnostics;
using PromptFlowStudio.Services.Localization.Dubbing;

namespace PromptFlowStudio.Services.FacialPerformance;

public enum MouthSource : byte { Camera = 0, Audio, Fused }

/// <summary>
/// [PromptFlow Studio v17.12.0] 🗣️ AI 페이셜 퍼포먼스 &amp; Audio2Face 스튜디오 러너(60 Hz 전용 스레드 · 시뮬레이션 결정적).
/// 합성 배우(52 ARKit 참값: 발화 비짐 공동조음 · 미소/눈썹/턱 표정 · 시선/깜빡임 또는 Dead-Eye) + 포먼트 음성(마이크 48 kHz) →
/// ① 카메라 경로: 참값 → 468 랜드마크 → 헤드 포즈 + 검출 노이즈 → 역산 엔진 ② 오디오 경로: Audio2Face 비짐 →
/// ③ 입 융합(카메라 | 오디오 | 가중 융합 · 얼굴 소실 시 오디오 폴백) ④ 시선/깜빡임 생체 가드 → 최종 52 가중치 → 와이어프레임 재구성.
/// </summary>
public sealed class FacialPerformanceStudioRunner : IDisposable
{
    public const int Hz = 60, SeriesLen = 240, K = NeuralFacialLandmarkBlendshapeEngine.K, L = NeuralFacialLandmarkBlendshapeEngine.Landmarks;
    public const int SamplesPerTick = AudioToFaceVisemeMotionSynthesizer.SampleRate / Hz;
    public const float Dt = 1f / Hz;

    private readonly object _gate = new();
    private readonly float[] _gt = new float[K], _final = new float[K], _gtMouth = new float[GenerativeNeuralLipSyncSynthesizer.Shapes];
    private readonly float[] _audio = new float[SamplesPerTick];
    private readonly Vector3[] _lm = new Vector3[L], _preview = new Vector3[L];
    private readonly float[] _msS = new float[SeriesLen], _camS = new float[SeriesLen], _a2fS = new float[SeriesLen], _finS = new float[SeriesLen], _gazeS = new float[SeriesLen], _blinkS = new float[SeriesLen];
    private int _seriesHead, _seriesCount;
    private uint _rng = 0x1712u;
    private Thread? _loop;
    private volatile bool _running;
    private long _tick;
    private float _time, _actorYaw, _actorPitch, _actorTYaw, _actorTPitch, _nextSaccade = 1f, _nextBlink = 2f, _blinkStart = -10f;
    private int _visCorrect, _visTotal;

    public FacialPerformanceStudioRunner(IServiceProvider sp)
        : this((NeuralFacialLandmarkBlendshapeEngine?)sp.GetService(typeof(NeuralFacialLandmarkBlendshapeEngine)) ?? new NeuralFacialLandmarkBlendshapeEngine(),
               (AudioToFaceVisemeMotionSynthesizer?)sp.GetService(typeof(AudioToFaceVisemeMotionSynthesizer)) ?? new AudioToFaceVisemeMotionSynthesizer(),
               (BiologicalEyeGazeMicroTremorGuard?)sp.GetService(typeof(BiologicalEyeGazeMicroTremorGuard)) ?? new BiologicalEyeGazeMicroTremorGuard())
    {
    }

    public FacialPerformanceStudioRunner(NeuralFacialLandmarkBlendshapeEngine engine, AudioToFaceVisemeMotionSynthesizer a2f, BiologicalEyeGazeMicroTremorGuard guard)
    {
        Engine = engine; A2F = a2f; Guard = guard;
        Engine.Forward(_final, _preview);
        LastAction = "📋 대기 · 랜드마크 " + L + " · ARKit " + K + " · 마이크 48 kHz · 홉 10 ms · " + Engine.Path;
    }

    public event Action? OnChange;

    public NeuralFacialLandmarkBlendshapeEngine Engine { get; }
    public AudioToFaceVisemeMotionSynthesizer A2F { get; }
    public BiologicalEyeGazeMicroTremorGuard Guard { get; }
    public FormantSpeechSynthesizer Voice { get; } = new();
    public bool Motion { get; set; } = true;
    /// <summary>배우 시선 고정 · 깜빡임 없음(Dead-Eye 재현).</summary>
    public bool DeadEyedActor { get; set; } = true;
    public bool CameraOccluded { get; set; }
    public bool HeadMotion { get; set; } = true;
    public float NoiseMm { get; set; } = 0.3f;
    public MouthSource Mouth { get; private set; } = MouthSource.Fused;
    /// <summary>융합 시 오디오 가중(0 = 카메라 · 1 = 오디오).</summary>
    public float AudioBlend { get; set; } = 0.5f;
    public bool IsRunning => _running;
    public long Tick => Interlocked.Read(ref _tick);
    public double LastTickMs { get; private set; }
    public string? LastAction { get; private set; }
    public float Time => _time;
    public bool FaceLost { get; private set; }
    public float CameraMae { get; private set; }
    public float A2fMouthMae { get; private set; }
    public float FinalMouthMae { get; private set; }
    public float VisemeAccuracy => _visTotal > 0 ? _visCorrect / (float)_visTotal : 0f;
    public SpeechPhone TruthPhone { get; private set; }
    public float Truth(int k) => _gt[k];
    public float Final(int k) => _final[k];
    public float Camera(int k) => Engine.Weight(k);
    public Vector3 Preview(int i) => _preview[i];
    public float ActorYaw => _actorYaw;
    public float ActorPitch => _actorPitch;
    public int SeriesCount => _seriesCount;
    private int Idx(int i) => (_seriesHead - 1 - i + SeriesLen * 2) % SeriesLen;
    public float TickMsSeries(int i) => _msS[Idx(i)];
    public float CameraMaeSeries(int i) => _camS[Idx(i)];
    public float A2fMaeSeries(int i) => _a2fS[Idx(i)];
    public float FinalMaeSeries(int i) => _finS[Idx(i)];
    public float GazeSeries(int i) => _gazeS[Idx(i)];
    public float BlinkSeries(int i) => _blinkS[Idx(i)];

    private static string F2(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public void SetMouthSource(MouthSource m)
    {
        lock (_gate) Mouth = m;
        LastAction = m switch { MouthSource.Camera => "📷 입 = 카메라 역산", MouthSource.Audio => "🎙 입 = Audio2Face", _ => "🔀 입 = 융합(오디오 " + F2(AudioBlend) + ")" };
        Raise();
    }

    public void SetDeadEyed(bool on) { lock (_gate) DeadEyedActor = on; LastAction = on ? "😐 배우 Dead-Eye 재현(시선 고정 · 깜빡임 없음)" : "🙂 배우 자연 시선/깜빡임"; Raise(); }
    public void SetGuard(bool on) { lock (_gate) Guard.Enabled = on; LastAction = on ? "👁 생체 시선 가드 ON" : "👁 생체 시선 가드 OFF — 측정 그대로"; Raise(); }
    public void SetForceInject(bool on) { lock (_gate) Guard.ForceInject = on; LastAction = on ? "✨ 미세 홱보기/깜빡임 주입 강제" : "✨ 주입 = Dead-Eye 감지 연동"; Raise(); }
    public void SetOccluded(bool on) { lock (_gate) CameraOccluded = on; LastAction = on ? "🚫 카메라 얼굴 소실 — 입 = Audio2Face 폴백" : "📷 카메라 복구"; Raise(); }
    public void SetArticulation(float a) { lock (_gate) A2F.Articulation = Math.Clamp(a, 0f, 1.5f); LastAction = "👄 조음 강도 × " + F2(A2F.Articulation); Raise(); }

    // ── 합성 배우 ───────────────────────────────────────────

    private float U()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (1f / 16777216f);
    }

    private float Gauss() => (U() + U() + U() + U() - 2f) * 1.7320508f;

    private void ActorWeights()
    {
        var t = _time;
        Array.Clear(_gt);
        // 발화: 현재 음소 비짐 목표 → 공동조음(τ 40 ms)
        TruthPhone = FormantSpeechSynthesizer.PhoneAt(Voice.SampleIndex * 1000.0 / AudioToFaceVisemeMotionSynthesizer.SampleRate);
        var v = FormantSpeechSynthesizer.VisemeOf(TruthPhone);
        var k = 1f - MathF.Exp(-Dt / 0.04f);
        for (var s = 0; s < _gtMouth.Length; s++) _gtMouth[s] += (GenerativeNeuralLipSyncSynthesizer.TargetWeight(v, s) - _gtMouth[s]) * k;
        for (var s = 0; s < _gtMouth.Length; s++) _gt[(int)GenerativeNeuralLipSyncSynthesizer.Mouth[s]] = _gtMouth[s];
        var smile = 0.3f + 0.25f * MathF.Sin(0.31f * t);
        _gt[(int)ArkitBlendshape.MouthSmileLeft] = Math.Clamp(_gt[(int)ArkitBlendshape.MouthSmileLeft] + smile, 0f, 1f);
        _gt[(int)ArkitBlendshape.MouthSmileRight] = Math.Clamp(_gt[(int)ArkitBlendshape.MouthSmileRight] + smile * 0.9f, 0f, 1f);
        _gt[(int)ArkitBlendshape.CheekSquintLeft] = 0.6f * smile; _gt[(int)ArkitBlendshape.CheekSquintRight] = 0.55f * smile;
        _gt[(int)ArkitBlendshape.BrowInnerUp] = 0.6f * MathF.Max(0f, MathF.Sin(0.47f * t));
        var bd = 0.5f * MathF.Max(0f, -MathF.Sin(0.29f * t));
        _gt[(int)ArkitBlendshape.BrowDownLeft] = bd; _gt[(int)ArkitBlendshape.BrowDownRight] = bd;
        _gt[(int)ArkitBlendshape.BrowOuterUpLeft] = 0.4f * MathF.Max(0f, MathF.Sin(0.47f * t + 0.5f));
        _gt[(int)ArkitBlendshape.NoseSneerLeft] = 0.3f * MathF.Max(0f, MathF.Sin(0.21f * t - 1f));
        var jaw = 0.12f * MathF.Sin(0.37f * t);
        _gt[(int)(jaw > 0 ? ArkitBlendshape.JawLeft : ArkitBlendshape.JawRight)] = MathF.Abs(jaw);

        // 시선 · 깜빡임
        if (DeadEyedActor) { _actorTYaw = 0; _actorTPitch = 0; _actorYaw = 0; _actorPitch = 0; }
        else
        {
            if (t >= _nextSaccade) { _nextSaccade = t + 0.6f + 1.4f * U(); _actorTYaw = 12f * (2f * U() - 1f); _actorTPitch = 8f * (2f * U() - 1f); }
            var g = 1f - MathF.Exp(-Dt / 0.025f);
            _actorYaw += (_actorTYaw - _actorYaw) * g; _actorPitch += (_actorTPitch - _actorPitch) * g;
            if (t >= _nextBlink) { _nextBlink = t + 2.5f + 3f * U(); _blinkStart = t; }
            var b = BiologicalEyeGazeMicroTremorGuard.BlinkProfile(t - _blinkStart);
            _gt[(int)ArkitBlendshape.EyeBlinkLeft] = b; _gt[(int)ArkitBlendshape.EyeBlinkRight] = b;
        }
        NeuralFacialLandmarkBlendshapeEngine.GazeToWeights(1, _actorYaw, _actorPitch, _gt);
        NeuralFacialLandmarkBlendshapeEngine.GazeToWeights(-1, _actorYaw, _actorPitch, _gt);
    }

    // ── 1 틱 ────────────────────────────────────────────────

    /// <summary>1틱(무할당).</summary>
    public void Step()
    {
        lock (_gate)
        {
            using var zg = ZeroGcRegionScope.Enter("FacialPerformance.Step");
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Motion) _time += Dt;
            // 마이크(포먼트 음성) → Audio2Face
            Voice.Generate(_audio);
            A2F.Push(_audio);
            var heard = FormantSpeechSynthesizer.PhoneAt(Voice.SampleIndex * 1000.0 / AudioToFaceVisemeMotionSynthesizer.SampleRate - 15.0);
            if (FormantSpeechSynthesizer.BoundaryDistanceMs(Voice.SampleIndex * 1000.0 / AudioToFaceVisemeMotionSynthesizer.SampleRate - 15.0) > 40)
            {
                _visTotal++;
                if (A2F.Viseme == FormantSpeechSynthesizer.VisemeOf(heard)) _visCorrect++;
                if (_visTotal > 6000) { _visTotal /= 2; _visCorrect /= 2; }
            }
            ActorWeights();

            // 카메라: 참값 → 랜드마크 → 헤드 포즈 + 노이즈
            FaceLost = CameraOccluded;
            if (!FaceLost)
            {
                Engine.Forward(_gt, _lm);
                var t = _time;
                var q = HeadMotion
                    ? Quaternion.CreateFromYawPitchRoll(15f * MathF.PI / 180f * MathF.Sin(0.4f * t), 8f * MathF.PI / 180f * MathF.Sin(0.53f * t), 5f * MathF.PI / 180f * MathF.Sin(0.29f * t))
                    : Quaternion.Identity;
                var tr = new Vector3(0.02f * MathF.Sin(0.23f * t), 0.01f * MathF.Cos(0.31f * t), 0.55f);
                var nm = NoiseMm * 0.001f;
                for (var i = 0; i < L; i++)
                    Engine.SetObservation(i, Vector3.Transform(_lm[i], q) + tr + new Vector3(nm * Gauss(), nm * Gauss(), nm * Gauss()));
                Engine.Solve();
            }

            // 최종 52: 카메라 기본 → 입 융합 → 생체 시선 가드
            Engine.Weights.CopyTo(_final);
            var src = FaceLost ? MouthSource.Audio : Mouth;
            for (var s = 0; s < GenerativeNeuralLipSyncSynthesizer.Shapes; s++)
            {
                var k = (int)GenerativeNeuralLipSyncSynthesizer.Mouth[s];
                var a = A2F.Weight(s);
                // 미소는 표정 성분이 지배적 → 오디오는 하한(max)으로만 기여
                var expressive = GenerativeNeuralLipSyncSynthesizer.Mouth[s] is ArkitBlendshape.MouthSmileLeft or ArkitBlendshape.MouthSmileRight;
                if (src == MouthSource.Audio) _final[k] = expressive ? MathF.Max(_final[k], a) : a;
                else if (src == MouthSource.Fused) _final[k] = expressive ? MathF.Max(_final[k], a) : (1f - AudioBlend) * _final[k] + AudioBlend * a;
            }
            Engine.Gaze(1, out var yl, out var pl);
            Engine.Gaze(-1, out var yr, out var pr);
            if (FaceLost) { yl = Guard.MeasuredYaw(0); pl = Guard.MeasuredPitch(0); yr = Guard.MeasuredYaw(1); pr = Guard.MeasuredPitch(1); }
            Guard.Process(yl, pl, yr, pr, FaceLost ? 0f : Engine.Weight(ArkitBlendshape.EyeBlinkLeft), FaceLost ? 0f : Engine.Weight(ArkitBlendshape.EyeBlinkRight), Dt);
            Guard.WriteEyes(_final);
            Engine.Forward(_final, _preview);

            // 지표
            float ce = 0, ae = 0, fe = 0; var cn = 0;
            for (var k = 0; k < K; k++)
            {
                if (k is >= (int)ArkitBlendshape.EyeBlinkLeft and <= (int)ArkitBlendshape.EyeLookUpRight) continue;
                ce += MathF.Abs(Engine.Weight(k) - _gt[k]); cn++;
            }
            for (var s = 0; s < GenerativeNeuralLipSyncSynthesizer.Shapes; s++)
            {
                var k = (int)GenerativeNeuralLipSyncSynthesizer.Mouth[s];
                ae += MathF.Abs(A2F.Weight(s) - _gtMouth[s]);
                fe += MathF.Abs(_final[k] - _gt[k]);
            }
            if (!FaceLost) CameraMae = ce / cn;
            A2fMouthMae = ae / GenerativeNeuralLipSyncSynthesizer.Shapes;
            FinalMouthMae = fe / GenerativeNeuralLipSyncSynthesizer.Shapes;
            var ms = (float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            var h = _seriesHead;
            _msS[h] = ms; _camS[h] = CameraMae; _a2fS[h] = A2fMouthMae; _finS[h] = FinalMouthMae; _gazeS[h] = Guard.Yaw(0); _blinkS[h] = Guard.BlinkL;
            _seriesHead = (h + 1) % SeriesLen;
            if (_seriesCount < SeriesLen) _seriesCount++;
            Interlocked.Increment(ref _tick);
            LastTickMs = ms;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _loop = new Thread(Loop) { IsBackground = true, Name = "PF.FacialPerformance" };
            _loop.Start();
            LastAction = "▶ 페이셜 캡처 가동 · 60 Hz · 468 랜드마크 → 52 ARKit 역산 · 마이크 → Audio2Face 비짐 · 생체 시선 가드";
        }
        Raise();
    }

    public void Stop()
    {
        Thread? t;
        lock (_gate) { if (!_running) return; _running = false; t = _loop; _loop = null; }
        t?.Join(3000);
        LastAction = "⏸ 페이셜 캡처 정지";
        Raise();
    }

    private void Loop()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long n = 0;
        while (_running)
        {
            try { Step(); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException) { PromptFlowStudio.Program.Log("FacialPerformance.Step", ex); }
            n++;
            var wait = n * 1000.0 / Hz - sw.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -250) { sw.Restart(); n = 0; }   // 과부하 시 따라잡기 폭주 방지
            if (n % 6 == 0) Raise();
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("FacialPerformance.Runner.OnChange", ex); }
    }

    public void Dispose() => Stop();
}
