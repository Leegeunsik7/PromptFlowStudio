namespace PromptFlowStudio.Services.NarrativeTimeline;

/// <summary>
/// [PromptFlow Studio v17.40.0] 🔀 비선형 스토리 분기 전환 &amp; 트랙 연속성 가드(<see cref="Filter"/> · <see cref="ProcessLane"/> 무할당).
///   ▸ 비주얼(12 채널 · 포즈/시선/표정/템포/조명/긴장): 분기 트리거 프레임에 즉시 오프셋 x₀ = 직전 출력 − 새 소스 · v₀ = 직전 출력 속도 포착 →
///     Bollo 관성 보간 5차 다항식 x(t) = At⁵ + Bt⁴ + Ct³ + a₀t²/2 + v₀t + x₀ (x(t₁) = ẋ(t₁) = ẍ(t₁) = 0) 로 감쇠 —
///     t₁ = max(0.45 s, 1.875·|x₀| / v한도)(채널별 프레임 한도의 50 % 속도) · 과주행 방지 t₁ ≤ −5x₀/v₀ · 새 분기 소스 위에 가산(대기 없음 · 1 프레임 내 전이 개시).
///   ▸ 오디오(음성 레인): 분기 프레임은 구 분기 발화를 그대로 렌더 후 등전력 코사인 페이드아웃(1 프레임 = 800 샘플) → 레인 리셋 → 다음 프레임 새 발화 ·
///     경계 2 프레임 무장 — 경계 계단 |Δ| &gt; 0.02(−34 dBFS) 시 지수 감쇠 DC 오프셋(τ 32 샘플) 보정.
///   ▸ 계측(가드 OFF 도 측정): 채널별 프레임 한도(포즈 0.06 m · 시선 0.12 rad · 표정 0.06 …) 초과 = 하드 컷 · 분기 경계 오디오 팝 · 최대 계단 · 편집 컷(씬 전환)은 제외.
/// </summary>
public sealed class InteractiveBranchingContinuitySafetyGuard
{
    public const int Channels = DramaturgyEmotionalArcBlockingSynthesizer.Channels, Lanes = 2, ArmFrames = 2;
    public const float BlendS = 0.45f, MaxBlendS = 3f, PopThreshold = 0.02f, OffsetTau = 32f, VelocityBudget = 0.5f, FrameS = 1f / 60f;

    /// <summary>채널별 1/60 s 당 허용 변화(초과 = 시청자가 인지하는 하드 컷).</summary>
    private static readonly float[] MaxStep = { 0.06f, 0.06f, 0.06f, 0.06f, 0.12f, 0.12f, 0.06f, 0.06f, 0.03f, 0.03f, 0.06f, 0.04f };

    private readonly float[] _last = new float[Channels], _lastVel = new float[Channels];
    private readonly float[] _sign = new float[Channels], _x0 = new float[Channels], _v0 = new float[Channels], _t1 = new float[Channels];
    private readonly float[] _a = new float[Channels], _b = new float[Channels], _c = new float[Channels], _acc = new float[Channels];
    private readonly float[] _lastSample = new float[Lanes];
    private readonly int[] _armed = new int[Lanes];
    private bool _primed, _pending, _editorial, _active;
    private float _t, _tEnd;

    public bool Enabled { get; set; } = true;
    public bool Blending => _active;
    public float BlendProgress => _active && _tEnd > 0f ? MathF.Min(1f, _t / _tEnd) : 1f;
    public float BlendDurationS => _tEnd;
    public int Transitions { get; private set; }
    public int EditorialCuts { get; private set; }
    public int HardCuts { get; private set; }
    public int PoseHardCuts { get; private set; }
    public float MaxPoseStep { get; private set; }
    public float MaxStepRatio { get; private set; }
    public float LastOffset { get; private set; }
    public int AudioBoundaries { get; private set; }
    public int AudioPops { get; private set; }
    public int AudioCorrections { get; private set; }
    public float MaxSwitchStep { get; private set; }
    public double LastBeginUs { get; private set; }

    public static float StepLimit(int ch) => MaxStep[ch];

    /// <summary>분기 전환 요청 — 다음 <see cref="Filter"/> 프레임에 관성 보간 개시(OFF 면 계측만).</summary>
    public void BeginTransition() { _pending = true; _editorial = false; }

    /// <summary>편집 컷(선형 진행 씬 전환) — 다음 프레임 기준 재설정 · 하드 컷 계측 제외.</summary>
    public void NoteEditorialCut() { if (!_pending) _editorial = true; }

    public void ArmAudio() { for (var l = 0; l < Lanes; l++) _armed[l] = ArmFrames; }

    // ══════════════════════ 비주얼(무할당) ══════════════════════

    public void Filter(ReadOnlySpan<float> src, Span<float> dst, float dt)
    {
        if (!_primed)
        {
            src[..Channels].CopyTo(dst); src[..Channels].CopyTo(_last); Array.Clear(_lastVel);
            _primed = true; _pending = _editorial = false;
            return;
        }
        if (_pending)
        {
            _pending = false; Transitions++;
            if (Enabled) Begin(src);
        }
        if (_editorial)
        {
            _editorial = false; EditorialCuts++; _active = false;
            src[..Channels].CopyTo(dst); src[..Channels].CopyTo(_last); Array.Clear(_lastVel);
            return;
        }
        if (_active)
        {
            _t += dt;
            for (var k = 0; k < Channels; k++) dst[k] = src[k] + Eval(k, _t);
            if (_t >= _tEnd) _active = false;
        }
        else src[..Channels].CopyTo(dst);

        var worst = 0f; var pose = 0f;
        var scale = FrameS / MathF.Max(dt, 1e-4f);
        for (var k = 0; k < Channels; k++)
        {
            var d = dst[k] - _last[k];
            var a = MathF.Abs(d);
            var r = a * scale / MaxStep[k];
            if (r > worst) worst = r;
            if (k < 4 && a > pose) pose = a;
            _lastVel[k] = d / MathF.Max(dt, 1e-4f);
            _last[k] = dst[k];
        }
        if (worst > MaxStepRatio) MaxStepRatio = worst;
        if (pose > MaxPoseStep) MaxPoseStep = pose;
        if (worst > 1f) { HardCuts++; if (pose * scale > MaxStep[0]) PoseHardCuts++; }
    }

    private void Begin(ReadOnlySpan<float> src)
    {
        var ts = System.Diagnostics.Stopwatch.GetTimestamp();
        _t = 0f; _tEnd = 0f; var off = 0f;
        for (var k = 0; k < Channels; k++)
        {
            // 진행 중 보간 잔차까지 포함(직전 출력 기준) — 연속 분기에도 위치 연속
            var x0 = _last[k] - src[k];
            var v0 = _lastVel[k];
            var s = x0 < 0f ? -1f : 1f;
            x0 *= s; v0 *= s;
            if (v0 > 0f) v0 = 0f;
            off = MathF.Max(off, x0);
            if (x0 < 1e-6f && v0 > -1e-6f) { _t1[k] = 0f; _x0[k] = _v0[k] = _a[k] = _b[k] = _c[k] = _acc[k] = 0f; _sign[k] = s; continue; }
            var t1 = BlendS;
            if (v0 < 0f) t1 = MathF.Min(t1, -5f * x0 / v0);
            t1 = MathF.Min(MaxBlendS, MathF.Max(t1, 1.875f * x0 / (VelocityBudget * MaxStep[k] / FrameS)));
            t1 = MathF.Max(t1, 1e-3f);
            var t2 = t1 * t1; var t3 = t2 * t1;
            var a0 = (-8f * v0 * t1 - 20f * x0) / t2;
            _a[k] = -(a0 * t2 + 6f * v0 * t1 + 12f * x0) / (2f * t3 * t2);
            _b[k] = (3f * a0 * t2 + 16f * v0 * t1 + 30f * x0) / (2f * t2 * t2);
            _c[k] = -(3f * a0 * t2 + 12f * v0 * t1 + 20f * x0) / (2f * t3);
            _acc[k] = a0; _x0[k] = x0; _v0[k] = v0; _t1[k] = t1; _sign[k] = s;
            if (t1 > _tEnd) _tEnd = t1;
        }
        LastOffset = off;
        _active = _tEnd > 0f;
        LastBeginUs = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    private float Eval(int k, float t)
    {
        var t1 = _t1[k];
        if (t >= t1) return 0f;
        var t2 = t * t;
        return _sign[k] * (((_a[k] * t + _b[k]) * t + _c[k]) * t2 * t + 0.5f * _acc[k] * t2 + _v0[k] * t + _x0[k]);
    }

    // ══════════════════════ 오디오(무할당) ══════════════════════

    /// <summary>등전력 코사인 페이드아웃(버퍼 끝 = 0).</summary>
    public static void FadeOut(Span<float> pcm)
    {
        var n = pcm.Length;
        if (n == 0) return;
        var inv = 0.5f * MathF.PI / n;
        for (var i = 0; i < n; i++) pcm[i] *= MathF.Cos((i + 1) * inv);
    }

    /// <summary>레인 블록 경계 계측 · 무장 경계는 DC 오프셋 보정(ON).</summary>
    public void ProcessLane(int lane, Span<float> pcm)
    {
        if (pcm.Length == 0) return;
        var step = MathF.Abs(pcm[0] - _lastSample[lane]);
        if (_armed[lane] > 0)
        {
            if (Enabled && step > PopThreshold)
            {
                var off = _lastSample[lane] - pcm[0];
                var g = 1f; var k = MathF.Exp(-1f / OffsetTau);
                for (var i = 0; i < pcm.Length && g > 1e-5f; i++) { pcm[i] += off * g; g *= k; }
                AudioCorrections++;
                step = MathF.Abs(pcm[0] - _lastSample[lane]);
            }
            AudioBoundaries++;
            if (step > MaxSwitchStep) MaxSwitchStep = step;
            if (step > PopThreshold) AudioPops++;
            _armed[lane]--;
        }
        _lastSample[lane] = pcm[^1];
    }

    public void ResetMetrics()
    {
        Transitions = EditorialCuts = HardCuts = PoseHardCuts = AudioBoundaries = AudioPops = AudioCorrections = 0;
        MaxPoseStep = MaxStepRatio = MaxSwitchStep = LastOffset = 0f;
    }
}
