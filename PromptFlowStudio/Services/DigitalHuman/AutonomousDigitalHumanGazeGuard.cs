using System.Numerics;
using PromptFlowStudio.Services.Neuro;

namespace PromptFlowStudio.Services.DigitalHuman;

/// <summary>대사 감정선 분석 결과(0..1 지수 · 라벨 = 리터럴).</summary>
public readonly record struct DialogueEmotion(float Valence, float Arousal, float Fear, string Label);

/// <summary>
/// [PromptFlow Studio v16.82.0] 자율 감정선 연동 시선 · 미세 동작 가드(<see cref="Step"/> 무할당).
/// 시선: 표적(카메라 · 관객 · 시선 회피점) 현저성 × 감정 가중 확률 선택 → 고정 지속(로그정규 · 각성↑ = 짧게) →
/// 단속(Saccade) = 주계열(V_peak = 700·(1 − e^{−A/14}) °/s · D = 1.875A / V_peak) 최소 저크 궤적 → 고정 중 미세 단속 0.15–0.6° · 이동 표적 추종 ·
/// 머리-눈 협응(12° 초과분 머리 분담 · τ 0.25 s) · 폭주(IPD 64 mm · 최소 0.2 m).
/// 깜빡임: 율 = 14 + 10·각성 + 6·발화 + 6·공포 /분 · 불응기 0.35 s · 닫힘 75–100 ms &lt; 열림 170–230 ms(비대칭) · 큰 단속(&gt; 25°) 유발 깜빡임.
/// 동공: Moon–Spencer 휘도 응답 + 각성/공포 확장 + 근거리 축동 · 수축 τ 0.12 s / 확장 τ 0.45 s · 2–8 mm.
/// 가드: 안구 가동 한계(요 ±35° · 피치 +25/−30°) 초과분 머리 이관 · 폭주 최소 거리 · 응시 고착(MaxFixation) 강제 해제 → ARKit 눈 블렌드셰이프 출력.
/// </summary>
public sealed class AutonomousDigitalHumanGazeGuard
{
    public const int MaxTargets = 8, HistoryLen = 128;
    public const float YawLimit = 35f, PitchUpLimit = 25f, PitchDownLimit = 30f, HeadYawLimit = 70f, HeadPitchLimit = 40f;
    public const float Ipd = 0.064f, MinVergenceDistance = 0.2f, Vmax = 700f, VC = 14f, BlinkRefractory = 0.35f;
    public static readonly Vector3 EyeMid = new(0f, 0.026f, 0.072f);

    private readonly Vector3[] _tp = new Vector3[MaxTargets];
    private readonly float[] _ts = new float[MaxTargets];
    private readonly bool[] _tcam = new bool[MaxTargets];
    private int _tcount;
    private ulong _rng;

    // 감정(목표 / 평활)
    private float _tv = 0.5f, _ta = 0.4f, _tf = 0.1f;

    // 시선 상태(도 · 머리 기준 아님 = 세계 기준)
    private float _gY, _gP, _dist = 1f, _fcY, _fcP;
    private float _sY0, _sP0, _sY1, _sP1, _d0, _d1, _sT, _sD;
    private bool _inSac, _micro, _clamped;
    private float _fixT, _fixDur = 0.5f, _microT, _nextMicro = 0.6f;

    // 깜빡임
    private bool _blinking;
    private float _bT, _bClose, _bOpen, _nextBlink = 1.5f, _sinceBlink = 10f;

    private readonly float[] _hYaw = new float[HistoryLen], _hPitch = new float[HistoryLen], _hBlink = new float[HistoryLen], _hPupil = new float[HistoryLen];
    private int _hHead, _hTick;
    private double _camTime;

    public AutonomousDigitalHumanGazeGuard(ulong seed = 0x1682) => Reseed(seed);

    public void Reseed(ulong seed) => _rng = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

    // 입력
    public bool Speaking { get; set; }
    public float Luminance { get; set; } = 120f;
    public float MaxFixation { get; set; } = 3.5f;
    public float Valence { get; private set; } = 0.5f;
    public float Arousal { get; private set; } = 0.4f;
    public float Fear { get; private set; } = 0.1f;

    // 출력 / 진단
    public float GazeYaw => _gY;
    public float GazePitch => _gP;
    public float FocusDistance => _dist;
    public float EyeYawLeft { get; private set; }
    public float EyeYawRight { get; private set; }
    public float EyePitch { get; private set; }
    public float HeadYaw { get; private set; }
    public float HeadPitch { get; private set; }
    public float Blink { get; private set; }
    public float PupilMm { get; private set; } = 4f;
    public bool InSaccade => _inSac;
    public int CurrentTarget { get; private set; } = -1;
    public int TargetCount => _tcount;
    public long Saccades { get; private set; }
    public long Microsaccades { get; private set; }
    public long Blinks { get; private set; }
    public long Clamps { get; private set; }
    public long StareBreaks { get; private set; }
    public long VergenceClamps { get; private set; }
    public float LastAmplitude { get; private set; }
    public float LastDuration { get; private set; }
    public float LastPeakVelocity { get; private set; }
    public double Time { get; private set; }
    public float FixationTime => _fixT;
    public float BlinkRatePerMin => Time > 1e-3 ? (float)(Blinks * 60.0 / Time) : 0f;
    public float CameraContactRatio => Time > 1e-3 ? (float)(_camTime / Time) : 0f;
    public string? LastAction { get; private set; }

    public Vector3 TargetPosition(int i) => _tp[i];
    public float TargetSalience(int i) => _ts[i];
    public bool TargetIsCamera(int i) => _tcam[i];

    /// <summary>이력 링(오래된 → 최신 · k = 0..HistoryLen−1).</summary>
    public float HistoryYaw(int k) => _hYaw[(_hHead + k) % HistoryLen];
    public float HistoryPitch(int k) => _hPitch[(_hHead + k) % HistoryLen];
    public float HistoryBlink(int k) => _hBlink[(_hHead + k) % HistoryLen];
    public float HistoryPupil(int k) => _hPupil[(_hHead + k) % HistoryLen];

    // ───────────────────────── 모델 ─────────────────────────

    public static float MainSequencePeakVelocity(float amplitudeDeg) => Vmax * (1f - MathF.Exp(-MathF.Max(amplitudeDeg, 0f) / VC));

    public static float MainSequenceDuration(float amplitudeDeg)
    {
        var a = MathF.Max(amplitudeDeg, 1e-3f);
        return Math.Clamp(1.875f * a / MainSequencePeakVelocity(a), 0.02f, 0.25f);
    }

    /// <summary>최소 저크 위치(0..1).</summary>
    public static float MinJerk(float t) { t = Math.Clamp(t, 0f, 1f); return t * t * t * (10f + t * (-15f + 6f * t)); }

    /// <summary>깜빡임 눈꺼풀 닫힘(0..1): 닫힘 = smoothstep · 열림 = 1 − smoothstep(느림).</summary>
    public static float BlinkProfile(float t, float close, float open)
    {
        if (t <= 0f) return 0f;
        if (t < close) { var u = t / close; return u * u * (3f - 2f * u); }
        var o = Math.Clamp((t - close) / open, 0f, 1f);
        return 1f - o * o * (3f - 2f * o);
    }

    /// <summary>Moon–Spencer(1944) 동공 지름(mm) — L cd/m².</summary>
    public static float MoonSpencer(float luminance) => 4.9f - 3f * MathF.Tanh(0.4f * MathF.Log10(MathF.Max(luminance, 1e-4f)));

    public static void AnglesTo(Vector3 target, out float yawDeg, out float pitchDeg, out float dist)
    {
        var d = target - EyeMid;
        yawDeg = MathF.Atan2(d.X, d.Z) * (180f / MathF.PI);
        pitchDeg = MathF.Atan2(d.Y, MathF.Sqrt(d.X * d.X + d.Z * d.Z)) * (180f / MathF.PI);
        dist = d.Length();
    }

    private float Rand()
    {
        var x = _rng; x ^= x >> 12; x ^= x << 25; x ^= x >> 27; _rng = x;
        return ((x * 0x2545F4914F6CDD1DUL) >> 40) / 16777216f;
    }

    private float Gauss() => (Rand() + Rand() + Rand() - 1.5f) * 2f;

    // ───────────────────────── 제어 ─────────────────────────

    public void ClearTargets() { _tcount = 0; CurrentTarget = -1; }

    public int AddTarget(Vector3 p, float salience, bool camera)
    {
        if (_tcount >= MaxTargets) return -1;
        _tp[_tcount] = p; _ts[_tcount] = MathF.Max(salience, 0f); _tcam[_tcount] = camera;
        return _tcount++;
    }

    public void MoveTarget(int i, Vector3 p) { if ((uint)i < (uint)_tcount) _tp[i] = p; }

    public void SetEmotion(float valence, float arousal, float fear)
    {
        _tv = Math.Clamp(valence, 0f, 1f); _ta = Math.Clamp(arousal, 0f, 1f); _tf = Math.Clamp(fear, 0f, 1f);
    }

    /// <summary>v16.x BCI 감정 상태 결선.</summary>
    public void SetEmotion(in EmotionState s) => SetEmotion(s.Valence, s.Arousal, s.Fear);

    public void SnapEmotion() { Valence = _tv; Arousal = _ta; Fear = _tf; }

    private static readonly string[] Pos = { "사랑", "기쁘", "기뻐", "행복", "고마", "감사", "좋아", "멋져", "최고", "웃", "love", "happy", "thank", "great", "glad", "wonderful", "joy" };
    private static readonly string[] Sad = { "슬프", "슬퍼", "미안", "눈물", "외로", "그리워", "아프", "sad", "sorry", "cry", "lonely", "miss" };
    private static readonly string[] Ang = { "화나", "화가", "분노", "짜증", "용서 못", "싫어", "angry", "hate", "furious", "annoy" };
    private static readonly string[] Fea = { "무서", "두려", "공포", "위험", "도망", "살려", "scared", "afraid", "fear", "danger", "run", "help" };
    private static readonly string[] Sur = { "놀라", "깜짝", "세상에", "설마", "진짜", "wow", "surprise", "really", "what" };

    private static int Hits(string s, string[] keys)
    {
        var n = 0;
        foreach (var k in keys) if (s.Contains(k, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    /// <summary>대사 한/영 키워드 + 문장부호 → 감정선(정서가 · 각성 · 공포).</summary>
    public static DialogueEmotion AnalyzeDialogue(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new DialogueEmotion(0.5f, 0.35f, 0.05f, "중립");
        int p = Hits(line, Pos), s = Hits(line, Sad), a = Hits(line, Ang), f = Hits(line, Fea), u = Hits(line, Sur);
        var bang = 0; foreach (var c in line) if (c == '!') bang++;
        var v = 0.5f + 0.22f * p - 0.2f * s - 0.22f * a - 0.15f * f;
        var ar = 0.35f + 0.18f * a + 0.2f * f + 0.18f * u + 0.06f * p - 0.1f * s + 0.08f * MathF.Min(bang, 3);
        var fe = 0.05f + 0.3f * f;
        v = Math.Clamp(v, 0f, 1f); ar = Math.Clamp(ar, 0f, 1f); fe = Math.Clamp(fe, 0f, 1f);
        var label = fe > 0.5f ? "공포" : a > 0 && v < 0.4f ? "분노" : s > 0 && v < 0.45f ? "슬픔" : u > 0 && ar > 0.5f ? "놀람" : v > 0.6f ? "기쁨" : "중립";
        return new DialogueEmotion(v, ar, fe, label);
    }

    /// <summary>즉시 단속(테스트 · UI 클릭): 세계 요/피치(도) · 초점 거리.</summary>
    public void ForceSaccade(float yaw, float pitch, float dist = 1f)
    {
        CurrentTarget = -1;
        StartSaccade(yaw, pitch, dist, micro: false);
        _fixDur = 1.5f;
    }

    public void ForceBlink() => StartBlink();

    public void Reset()
    {
        _gY = _gP = 0f; _dist = 1f; _fcY = _fcP = 0f; _inSac = _blinking = _clamped = false;
        _fixT = 0f; _fixDur = 0.4f; _microT = 0f; _sinceBlink = 10f; _nextBlink = 1.5f;
        HeadYaw = HeadPitch = 0f; Blink = 0f; PupilMm = 4f;
        Saccades = Microsaccades = Blinks = Clamps = StareBreaks = VergenceClamps = 0; Time = 0; _camTime = 0;
        Array.Clear(_hYaw); Array.Clear(_hPitch); Array.Clear(_hBlink); Array.Clear(_hPupil);
        CurrentTarget = -1;
    }

    // ───────────────────────── 스텝 ─────────────────────────

    /// <summary>1프레임(무할당).</summary>
    public void Step(float dt)
    {
        if (dt <= 0f) return;
        Time += dt;
        var ke = 1f - MathF.Exp(-dt / 0.6f);
        Valence += (_tv - Valence) * ke; Arousal += (_ta - Arousal) * ke; Fear += (_tf - Fear) * ke;
        UpdateBlink(dt);
        UpdateGaze(dt);
        UpdateHeadAndEyes(dt);
        UpdatePupil(dt);
        if (CurrentTarget >= 0 && _tcam[CurrentTarget] && !_inSac) _camTime += dt;
        if ((++_hTick & 1) == 0)
        {
            _hYaw[_hHead] = _gY; _hPitch[_hHead] = _gP; _hBlink[_hHead] = Blink; _hPupil[_hHead] = PupilMm;
            _hHead = (_hHead + 1) % HistoryLen;
        }
    }

    private void UpdateBlink(float dt)
    {
        _sinceBlink += dt;
        if (_blinking)
        {
            _bT += dt;
            Blink = BlinkProfile(_bT, _bClose, _bOpen);
            if (_bT >= _bClose + _bOpen) { _blinking = false; Blink = 0f; }
            return;
        }
        _nextBlink -= dt;
        if (_nextBlink <= 0f && _sinceBlink >= BlinkRefractory) StartBlink();
    }

    private float BlinkRate() => 14f + 10f * Arousal + (Speaking ? 6f : 0f) + 6f * Fear;

    private void StartBlink()
    {
        if (_blinking || _sinceBlink < BlinkRefractory) return;
        _blinking = true; _bT = 0f; _sinceBlink = 0f;
        _bClose = 0.075f + 0.025f * (1f - Arousal); _bOpen = 0.17f + 0.06f * (1f - Arousal);
        var mean = 60f / BlinkRate();
        _nextBlink = BlinkRefractory + (-MathF.Log(MathF.Max(Rand(), 1e-6f))) * MathF.Max(mean - BlinkRefractory, 0.1f);
        Blinks++;
    }

    private void UpdateGaze(float dt)
    {
        if (_inSac)
        {
            float py = _gY, pp = _gP;
            _sT += dt;
            var s = MinJerk(_sT / _sD);
            _gY = _sY0 + (_sY1 - _sY0) * s; _gP = _sP0 + (_sP1 - _sP0) * s; _dist = _d0 + (_d1 - _d0) * s;
            float dy = _gY - py, dp = _gP - pp;
            var vel = MathF.Sqrt(dy * dy + dp * dp) / dt;
            if (!_micro && vel > LastPeakVelocity) LastPeakVelocity = vel;
            if (_sT >= _sD) { _inSac = false; if (!_micro) _fixT = 0f; _microT = 0f; }
            return;
        }
        _fixT += dt; _microT += dt;
        if (_fixT >= MaxFixation) { StareBreaks++; LastAction = "🛡 응시 고착 해제"; ChooseNext(); return; }
        if (_fixT >= _fixDur) { ChooseNext(); return; }
        // 이동 표적 추종(오차 &gt; 1.5° = 교정 단속 · 그 외 부드러운 추종)
        if (CurrentTarget >= 0)
        {
            AnglesTo(_tp[CurrentTarget], out var ty, out var tpch, out var td);
            float ey = ty - _fcY, ep = tpch - _fcP;
            if (ey * ey + ep * ep > 1.5f * 1.5f) { var keep = _fixT; StartSaccade(ty, tpch, td, micro: false); _fixT = keep; return; }
            var k = MathF.Min(1f, dt * 8f);
            _fcY += ey * k; _fcP += ep * k; _gY += ey * k; _gP += ep * k; _dist += (td - _dist) * k;
        }
        if (_microT >= _nextMicro)
        {
            var amp = 0.15f + 0.45f * Rand(); var a = Rand() * 2f * MathF.PI;
            StartSaccade(_fcY + amp * MathF.Cos(a), _fcP + amp * MathF.Sin(a), _dist, micro: true);
            _nextMicro = 0.4f + 0.8f * Rand();
        }
    }

    private void ChooseNext()
    {
        var pAvert = Math.Clamp(0.12f + 0.35f * Fear + 0.2f * (1f - Valence) * (Speaking ? 1f : 0.6f), 0f, 0.7f);
        float y, p, d; bool cam = false, avert;
        if (_tcount == 0 || Rand() < pAvert)
        {
            avert = true;
            var side = Rand() < 0.5f ? -1f : 1f;
            y = HeadYaw + side * (8f + 10f * Rand() + 15f * Fear * Rand());
            p = -(6f + 8f * Rand()) + 10f * Fear * Rand();
            d = 1.2f;
            CurrentTarget = -1;
        }
        else
        {
            avert = false;
            var camBias = 1f + 2f * Valence * (Speaking ? 1.5f : 1f) * (1f - Fear);
            var sum = 0f;
            for (var i = 0; i < _tcount; i++) sum += Weight(i, camBias);
            var r = Rand() * sum; var pick = _tcount - 1;
            for (var i = 0; i < _tcount; i++) { r -= Weight(i, camBias); if (r <= 0f) { pick = i; break; } }
            CurrentTarget = pick; cam = _tcam[pick];
            AnglesTo(_tp[pick], out y, out p, out d);
        }
        var mean = (0.35f + 1.1f * (1f - Arousal)) * (cam ? 1.5f : 1f) * (avert ? 0.6f : 1f);
        _fixDur = Math.Clamp(mean * MathF.Exp(0.4f * Gauss()), 0.12f, 6f);
        StartSaccade(y, p, d, micro: false);
        _fixT = 0f;
    }

    private float Weight(int i, float camBias) => _ts[i] * (_tcam[i] ? camBias : 1f) * (i == CurrentTarget ? 0.35f : 1f);

    private void StartSaccade(float yaw, float pitch, float dist, bool micro)
    {
        float dy = yaw - _gY, dp = pitch - _gP;
        var amp = MathF.Sqrt(dy * dy + dp * dp);
        _sY0 = _gY; _sP0 = _gP; _sY1 = yaw; _sP1 = pitch; _d0 = _dist; _d1 = dist;
        _sT = 0f; _sD = MainSequenceDuration(amp); _micro = micro; _inSac = true;
        if (!micro) { LastPeakVelocity = 0f; _fcY = yaw; _fcP = pitch; Saccades++; LastAmplitude = amp; LastDuration = _sD; }
        else Microsaccades++;
        if (!micro && amp > 25f && Rand() < 0.6f) StartBlink(); // 단속 유발 깜빡임
    }

    private void UpdateHeadAndEyes(float dt)
    {
        // 머리 분담(12°/10° 초과분)
        var hy = MathF.Sign(_gY) * MathF.Max(0f, MathF.Abs(_gY) - 12f) * 0.8f;
        var hp = MathF.Sign(_gP) * MathF.Max(0f, MathF.Abs(_gP) - 10f) * 0.6f;
        var k = 1f - MathF.Exp(-dt / 0.25f);
        var headY = HeadYaw + (hy - HeadYaw) * k; var headP = HeadPitch + (hp - HeadPitch) * k;
        // 가드: 안구 가동 한계 초과분 → 머리 이관(세계 시선 보존)
        var ey = _gY - headY; var ep = _gP - headP; var clamped = false;
        if (ey > YawLimit) { headY += ey - YawLimit; ey = YawLimit; clamped = true; }
        else if (ey < -YawLimit) { headY += ey + YawLimit; ey = -YawLimit; clamped = true; }
        if (ep > PitchUpLimit) { headP += ep - PitchUpLimit; ep = PitchUpLimit; clamped = true; }
        else if (ep < -PitchDownLimit) { headP += ep + PitchDownLimit; ep = -PitchDownLimit; clamped = true; }
        if (clamped && !_clamped) { Clamps++; LastAction = "🛡 안구 가동 한계 → 머리 이관"; }
        _clamped = clamped;
        HeadYaw = Math.Clamp(headY, -HeadYawLimit, HeadYawLimit); HeadPitch = Math.Clamp(headP, -HeadPitchLimit, HeadPitchLimit);
        EyePitch = ep;
        // 폭주: 초점 거리 하한 가드 → 눈별 요
        var dist = _dist;
        if (dist < MinVergenceDistance) { dist = MinVergenceDistance; VergenceClamps++; }
        var ry = _gY * (MathF.PI / 180f); var rp = _gP * (MathF.PI / 180f);
        var f = EyeMid + new Vector3(MathF.Sin(ry) * MathF.Cos(rp), MathF.Sin(rp), MathF.Cos(ry) * MathF.Cos(rp)) * dist;
        float lx = Ipd * 0.5f, rx = -Ipd * 0.5f;
        var yl = MathF.Atan2(f.X - lx, f.Z - EyeMid.Z) * (180f / MathF.PI) - HeadYaw;
        var yr = MathF.Atan2(f.X - rx, f.Z - EyeMid.Z) * (180f / MathF.PI) - HeadYaw;
        EyeYawLeft = Math.Clamp(yl, -YawLimit, YawLimit); EyeYawRight = Math.Clamp(yr, -YawLimit, YawLimit);
    }

    private void UpdatePupil(float dt)
    {
        var near = _dist < 0.5f ? 0.4f * (0.5f - _dist) / 0.3f : 0f;
        var target = Math.Clamp(MoonSpencer(Luminance) + 1.2f * (Arousal - 0.5f) + 0.8f * Fear - near, 2f, 8f);
        var tau = target < PupilMm ? 0.12f : 0.45f;
        PupilMm = Math.Clamp(PupilMm + (target - PupilMm) * (1f - MathF.Exp(-dt / tau)), 2f, 8f);
    }

    /// <summary>ARKit 눈 블렌드셰이프 출력(시선 레이어): 깜빡임 · 시선 방향 눈꺼풀 추종 · 공포/각성 눈 크게.</summary>
    public void WriteToFace(NeuralFacialMuscleSimulator face)
    {
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeBlinkLeft, Blink);
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeBlinkRight, Blink);
        var up = Math.Clamp(EyePitch / PitchUpLimit, 0f, 1f); var down = Math.Clamp(-EyePitch / PitchDownLimit, 0f, 1f);
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeLookUpLeft, up); face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeLookUpRight, up);
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeLookDownLeft, down); face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeLookDownRight, down);
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeLookOutLeft, Math.Clamp(EyeYawLeft / YawLimit, 0f, 1f));
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeLookInLeft, Math.Clamp(-EyeYawLeft / YawLimit, 0f, 1f));
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeLookInRight, Math.Clamp(EyeYawRight / YawLimit, 0f, 1f));
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeLookOutRight, Math.Clamp(-EyeYawRight / YawLimit, 0f, 1f));
        var wide = Math.Clamp(0.6f * Fear + 0.5f * MathF.Max(0f, Arousal - 0.6f), 0f, 1f) * (1f - Blink);
        face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeWideLeft, wide); face.SetLayer(FaceLayer.Gaze, ArkitBlendshape.EyeWideRight, wide);
    }

    /// <summary>눈별 시선 방향(머리 좌표 · side +1 = 왼눈).</summary>
    public Vector3 EyeDirection(int side)
    {
        var y = (side > 0 ? EyeYawLeft : EyeYawRight) * (MathF.PI / 180f); var p = EyePitch * (MathF.PI / 180f);
        return new Vector3(MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), MathF.Cos(y) * MathF.Cos(p));
    }
}
