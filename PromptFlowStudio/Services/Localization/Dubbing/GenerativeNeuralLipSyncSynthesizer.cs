using PromptFlowStudio.Services.DigitalHuman;

namespace PromptFlowStudio.Services.Localization.Dubbing;

using Viseme = PromptFlowStudio.Services.DigitalHuman.Viseme;

/// <summary>
/// [PromptFlow Studio v16.89.0] 👄 립싱크 &amp; 음소/비짐 정렬 합성기(60 Hz · 무할당 · 학습 모델 미사용).
///   ▸ 음소 → 비짐(v16.82 Viseme 8종 + 진폭) · 공동조음(Cohen-Massaro 지배 함수 α·exp(−θ|t − c|) · 양순음 폐쇄 우선 α = 4) → 입 블렌드셰이프 15개(ARKit · v16.82 SetSpeech 표 동일).
///   ▸ 구동: 오디오 재생 시계(샘플 정확 타임코드) + 선행 리드 → v16.82 근육 시뮬레이터 Speech 레이어 → Step(활성 τ · 연조직 2차 응답).
///   ▸ 동기 측정: 목표(리드 없음) vs 실제 입 개구(15 셰이프 합) 시계열 MSE 최소 지연(±50 ms · 1 ms 보간) · 대사 단위 자동 보정(리드 += 0.5·지연) → 잔여 오차 · 프레임 오프셋.
///   ▸ 오디오 역추출: 더빙 오디오 10 ms 프레임 에너지 + LPC(12 kHz · 12차) F1/F2 ÷ 성도 배율 → 최근접 모음 비짐 → 계획 대비 정확도.
///   ▸ 모션 델타: 프레임 간 Σ|Δw| · 추종 오차 Σ|목표 − 실제|.
/// </summary>
public sealed class GenerativeNeuralLipSyncSynthesizer
{
    public const int Hz = 60, Shapes = 15, History = 240, MaxLagMs = 50, VisemeCount = 8;
    public static readonly ArkitBlendshape[] Mouth =
    {
        ArkitBlendshape.JawOpen, ArkitBlendshape.MouthClose, ArkitBlendshape.MouthFunnel, ArkitBlendshape.MouthPucker,
        ArkitBlendshape.MouthStretchLeft, ArkitBlendshape.MouthStretchRight, ArkitBlendshape.MouthSmileLeft, ArkitBlendshape.MouthSmileRight,
        ArkitBlendshape.MouthLowerDownLeft, ArkitBlendshape.MouthLowerDownRight, ArkitBlendshape.MouthUpperUpLeft, ArkitBlendshape.MouthUpperUpRight,
        ArkitBlendshape.MouthPressLeft, ArkitBlendshape.MouthPressRight, ArkitBlendshape.MouthRollLower,
    };
    private static readonly float[] Targets = BuildTargets();
    private static readonly float[] VowelF1 = { 730, 530, 300, 570, 320 }, VowelF2 = { 1090, 1840, 2250, 840, 870 };

    private readonly object _gate = new();
    private readonly NeuralFacialMuscleSimulator _face;
    private readonly float[] _w = new float[Shapes], _prevW = new float[Shapes], _tgt = new float[Shapes], _ym = new float[Shapes], _yp = new float[Shapes], _cmd = new float[Shapes];
    private readonly float[] _histTarget = new float[History], _histActual = new float[History];
    private readonly float[] _dec = new float[NeuralVoiceCloningDubbingEngine.MaxSamples / 2];
    private readonly float[] _win = new float[180], _r = new float[13], _a = new float[13], _ta = new float[13];
    private readonly byte[] _audioVis = new byte[NeuralVoiceCloningDubbingEngine.MaxSamples / 240];
    private int _head, _count, _calibCounter, _audioFrames, _lineFrames;

    public GenerativeNeuralLipSyncSynthesizer(NeuralFacialMuscleSimulator? face = null) => _face = face ?? new NeuralFacialMuscleSimulator();

    public NeuralFacialMuscleSimulator Face => _face;
    public float LeadSeconds { get; set; }
    public bool Feedforward { get; set; } = true;
    public bool AutoCalibrate { get; set; } = true;
    public float Articulation { get; set; } = 1f;
    public float CoarticulationMs { get; set; } = 45f;
    public float MeasuredLagMs { get; private set; }
    public float SyncErrorMs { get; private set; }
    public float LineSyncErrorMs { get; private set; }
    public int FrameOffset => (int)MathF.Round(LineSyncErrorMs * Hz / 1000f);
    public float MotionDelta { get; private set; }
    public float TrackingError { get; private set; }
    public Viseme CurrentViseme { get; private set; }
    public Ph CurrentPhone { get; private set; }
    public float ExtractionAccuracy { get; private set; }
    public int ExtractedFrames => _audioFrames;
    public long Frames { get; private set; }
    public double LastStepMs { get; private set; }
    public float Weight(int s) => _cmd[s];
    public float Achieved(int s) => _face.Weight(Mouth[s]);
    public float Target(int s) => _tgt[s];
    public float TargetHistory(int back) => _histTarget[(_head - 1 - back + 2 * History) % History];
    public float ActualHistory(int back) => _histActual[(_head - 1 - back + 2 * History) % History];
    public int HistoryCount => _count;
    public Viseme AudioViseme(int frame) => (Viseme)_audioVis[frame];

    public static string ShapeName(int s) => NeuralFacialMuscleSimulator.Name(Mouth[s]);

    private static float[] BuildTargets()
    {
        var t = new float[VisemeCount * Shapes];
        void S(Viseme v, ArkitBlendshape k, float w) { for (var s = 0; s < Shapes; s++) if (Mouth[s] == k) t[(int)v * Shapes + s] = w; }
        S(Viseme.A, ArkitBlendshape.JawOpen, 0.55f); S(Viseme.A, ArkitBlendshape.MouthLowerDownLeft, 0.3f); S(Viseme.A, ArkitBlendshape.MouthLowerDownRight, 0.3f); S(Viseme.A, ArkitBlendshape.MouthStretchLeft, 0.1f); S(Viseme.A, ArkitBlendshape.MouthStretchRight, 0.1f);
        S(Viseme.E, ArkitBlendshape.JawOpen, 0.3f); S(Viseme.E, ArkitBlendshape.MouthStretchLeft, 0.45f); S(Viseme.E, ArkitBlendshape.MouthStretchRight, 0.45f); S(Viseme.E, ArkitBlendshape.MouthSmileLeft, 0.15f); S(Viseme.E, ArkitBlendshape.MouthSmileRight, 0.15f);
        S(Viseme.I, ArkitBlendshape.JawOpen, 0.15f); S(Viseme.I, ArkitBlendshape.MouthStretchLeft, 0.55f); S(Viseme.I, ArkitBlendshape.MouthStretchRight, 0.55f); S(Viseme.I, ArkitBlendshape.MouthSmileLeft, 0.25f); S(Viseme.I, ArkitBlendshape.MouthSmileRight, 0.25f);
        S(Viseme.O, ArkitBlendshape.JawOpen, 0.4f); S(Viseme.O, ArkitBlendshape.MouthFunnel, 0.65f); S(Viseme.O, ArkitBlendshape.MouthPucker, 0.25f);
        S(Viseme.U, ArkitBlendshape.JawOpen, 0.12f); S(Viseme.U, ArkitBlendshape.MouthPucker, 0.8f); S(Viseme.U, ArkitBlendshape.MouthFunnel, 0.35f);
        S(Viseme.M, ArkitBlendshape.MouthClose, 0.7f); S(Viseme.M, ArkitBlendshape.MouthPressLeft, 0.45f); S(Viseme.M, ArkitBlendshape.MouthPressRight, 0.45f); S(Viseme.M, ArkitBlendshape.MouthRollLower, 0.2f);
        S(Viseme.F, ArkitBlendshape.MouthRollLower, 0.55f); S(Viseme.F, ArkitBlendshape.MouthUpperUpLeft, 0.2f); S(Viseme.F, ArkitBlendshape.MouthUpperUpRight, 0.2f); S(Viseme.F, ArkitBlendshape.JawOpen, 0.08f);
        return t;
    }

    public static float TargetWeight(Viseme v, int shape) => Targets[(int)v * Shapes + shape];

    /// <summary>음소 → (비짐, 진폭, 지배 α).</summary>
    public static (Viseme V, float Amp, float Alpha) Map(Ph p) => p switch
    {
        Ph.A => (Viseme.A, 1f, 1f), Ph.E => (Viseme.E, 1f, 1f), Ph.I => (Viseme.I, 1f, 1f), Ph.O => (Viseme.O, 1f, 1f), Ph.U => (Viseme.U, 1f, 1f),
        Ph.P or Ph.B or Ph.M => (Viseme.M, 1f, 4f), Ph.F or Ph.V => (Viseme.F, 1f, 2.5f),
        Ph.W => (Viseme.U, 0.7f, 1.5f), Ph.Y => (Viseme.I, 0.6f, 1f),
        Ph.S or Ph.Z or Ph.T or Ph.D or Ph.N or Ph.L or Ph.Sh => (Viseme.I, 0.35f, 0.6f),
        Ph.K or Ph.G or Ph.Ng or Ph.H or Ph.R => (Viseme.E, 0.4f, 0.4f),
        _ => (Viseme.Rest, 0f, 0.8f),
    };

    /// <summary>시각 t 입 블렌드셰이프(공동조음 · 무할당) → dst(15).</summary>
    public void Evaluate(DubPlan plan, float t, Span<float> dst, out Viseme dominant, out Ph phone)
    {
        dst.Clear(); dominant = Viseme.Rest; phone = Ph.Sil;
        var theta = 1f / MathF.Max(CoarticulationMs / 1000f, 0.005f);
        float sumD = 0.25f; var bestD = 0f; // 휴지 기저 지배(입 닫힘 복귀)
        var k0 = Math.Max(plan.PhoneBefore(t), 0);
        var from = Math.Max(0, k0 - 6); var to = Math.Min(plan.Count - 1, k0 + 6);
        var en = plan.ContourAt(plan.Energy, t);
        for (var k = from; k <= to; k++)
        {
            var (v, amp, alpha) = Map(plan.Phone[k]);
            var c = plan.Start[k] + 0.5f * plan.Dur[k];
            var inside = t >= plan.Start[k] && t < plan.Start[k] + plan.Dur[k];
            var d = alpha * MathF.Exp(-theta * MathF.Max(0f, MathF.Abs(t - c) - 0.5f * plan.Dur[k] * 0.6f));
            if (inside) { phone = plan.Phone[k]; }
            if (d > bestD) { bestD = d; dominant = v; }
            sumD += d;
            if (v == Viseme.Rest) continue;
            var g = d * amp * Articulation * MathF.Min(1.3f, 0.6f + 0.4f * en);
            var o = (int)v * Shapes;
            for (var s = 0; s < Shapes; s++) dst[s] += Targets[o + s] * g;
        }
        var inv = 1f / sumD;
        for (var s = 0; s < Shapes; s++) dst[s] = Math.Clamp(dst[s] * inv, 0f, 1f);
        if (t < 0f || t > plan.Duration) { dst.Clear(); dominant = Viseme.Rest; phone = Ph.Sil; }
    }

    public void ResetSync() { lock (_gate) { _head = _count = 0; _calibCounter = 0; _lineFrames = 0; Array.Clear(_prevW); } }

    /// <summary>1프레임(무할당): 재생 시계 t(오디오 샘플 정확) → 리드 평가 → 근육 구동 → 동기 계측.</summary>
    public void Step(DubPlan plan, float audioTime, float dt)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // 역동역학 피드포워드: 근육 활성(1차 τ) + 연조직(2차 ω, ζ) 의 연속 역모델 u = y + k₁y' + k₂y''(중앙 차분 · 비대칭 τ)
            var tc = audioTime + LeadSeconds;
            Evaluate(plan, tc - dt, _ym, out _, out _);
            Evaluate(plan, tc, _w, out var dom, out _);
            Evaluate(plan, tc + dt, _yp, out _, out _);
            Evaluate(plan, audioTime, _tgt, out _, out var ph);
            CurrentViseme = dom; CurrentPhone = ph;
            const float om = 2f * MathF.PI * NeuralFacialMuscleSimulator.TissueHz, zeta = NeuralFacialMuscleSimulator.TissueDamping;
            for (var s = 0; s < Shapes; s++)
            {
                var y = _w[s];
                if (Feedforward)
                {
                    var d1 = (_yp[s] - _ym[s]) / (2f * dt); var d2 = (_yp[s] - 2f * y + _ym[s]) / (dt * dt);
                    var tau = d1 >= 0f ? NeuralFacialMuscleSimulator.TauActivate : NeuralFacialMuscleSimulator.TauRelease;
                    var k1 = 2f * zeta / om + tau; var k2 = 1f / (om * om) + tau * 2f * zeta / om;
                    y = Math.Clamp(y + k1 * d1 + k2 * d2, 0f, 1f);
                }
                _cmd[s] = y;
                _face.SetLayer(FaceLayer.Speech, Mouth[s], y);
            }
            _face.Step(dt);
            float md = 0f, te = 0f, apT = 0f, apA = 0f;
            for (var s = 0; s < Shapes; s++)
            {
                md += MathF.Abs(_cmd[s] - _prevW[s]); _prevW[s] = _cmd[s];
                var a = _face.Weight(Mouth[s]);
                te += MathF.Abs(_tgt[s] - a);
                apT += _tgt[s]; apA += a;
            }
            MotionDelta = md; TrackingError = te;
            // 입 개구 지표 = 15 셰이프 합(턱 단독보다 상관 피크 안정)
            _histTarget[_head] = apT; _histActual[_head] = apA;
            _head = (_head + 1) % History; if (_count < History) _count++;
            _lineFrames++;
            if (++_calibCounter >= Hz / 2 && _count >= 2 * Hz) { _calibCounter = 0; Calibrate(); }
            Frames++;
            LastStepMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        }
    }

    /// <summary>
    /// 지연 추정: 실제 개구 a(i) ≈ 목표 y(i + L)(L 지연 · 1 ms 분해능 선형 보간) 의 평균제곱오차 최소 L ∈ [−MaxLagMs, +MaxLagMs].
    /// 상관 피크 대신 MSE(음절 주기 다중 피크 모호성 억제) · 최근 90 프레임 · 발화 ≥ 50%.
    /// </summary>
    private float EstimateLag(int n)
    {
        var active = 0; for (var i = 0; i < n; i++) if (TargetHistory(i) > 0.05f) active++;
        if (n < 20 || active < n / 3) return float.NaN;
        float best = float.MaxValue; var bestMs = 0;
        var edge = (int)MathF.Ceiling(MaxLagMs * Hz / 1000f) + 1;
        for (var ms = -MaxLagMs; ms <= MaxLagMs; ms++)
        {
            var lf = ms * Hz / 1000f; double e = 0;
            for (var i = edge; i < n - edge; i++)
            {
                var x = i + lf; var i0 = (int)MathF.Floor(x); var f = x - i0;
                var y = TargetHistory(i0) + (TargetHistory(i0 + 1) - TargetHistory(i0)) * f;
                var d = ActualHistory(i) - y; e += d * d;
            }
            if (e < best) { best = (float)e; bestMs = ms; }
        }
        // back 인덱스 증가 = 과거 → a(i) ≈ y(i + L), L > 0 = 실제가 L 만큼 늦음
        return bestMs;
    }

    /// <summary>진행 중 지연(최근 90 프레임 · 표시용 · 리드 불변).</summary>
    private void Calibrate()
    {
        var l = EstimateLag(Math.Min(_count, 90));
        if (!float.IsNaN(l)) { SyncErrorMs = l; MeasuredLagMs = l + LeadSeconds * 1000f; }
    }

    /// <summary>대사 시작(지연 측정 창 기준점).</summary>
    public void BeginLine() { lock (_gate) _lineFrames = 0; }

    /// <summary>대사 종료: 대사 전체 구간 지연 → 대사 동기 오차 · 자동 보정(리드 += 0.5·지연). 반환 = 오차 ms(NaN = 측정 불가).</summary>
    public float EndLine()
    {
        lock (_gate)
        {
            var l = EstimateLag(Math.Min(_lineFrames, Math.Min(_count, History - 2)));
            _lineFrames = 0;
            if (float.IsNaN(l)) return l;
            LineSyncErrorMs = l; SyncErrorMs = l; MeasuredLagMs = l + LeadSeconds * 1000f;
            if (AutoCalibrate) LeadSeconds = Math.Clamp(LeadSeconds + 0.5f * l / 1000f, -0.05f, 0.25f);
            return l;
        }
    }

    /// <summary>오디오 역추출(제어 평면 · 선할당): 10 ms 프레임 에너지 · LPC F1/F2 → 모음 비짐 · 계획 대비 정확도.</summary>
    public float ExtractFromAudio(ReadOnlySpan<float> audio, DubPlan plan, float vtf)
    {
        lock (_gate)
        {
            var n = audio.Length / 2;
            for (var i = 0; i < n; i++) _dec[i] = 0.5f * (audio[2 * i] + audio[2 * i + 1]);
            const int hop = 120, w = 180;
            var frames = Math.Min((n - w) / hop, _audioVis.Length);
            float emax = 1e-6f;
            for (var f = 0; f < frames; f++) { var e = DubSimd.Dot(_dec.AsSpan(f * hop, w), _dec.AsSpan(f * hop, w)); emax = MathF.Max(emax, e); }
            int ok = 0, tot = 0;
            for (var f = 0; f < Math.Max(frames, 0); f++)
            {
                var st = f * hop;
                var e = DubSimd.Dot(_dec.AsSpan(st, w), _dec.AsSpan(st, w));
                if (e < 0.03f * emax) { _audioVis[f] = (byte)Viseme.Rest; continue; }
                var (f1, f2) = Lpc(st, w);
                var v = Viseme.E; var bd = float.MaxValue;
                for (var k = 0; k < 5; k++)
                {
                    var a = MathF.Log(MathF.Max(f1, 50f) / (VowelF1[k] * vtf)); var b = MathF.Log(MathF.Max(f2, 50f) / (VowelF2[k] * vtf));
                    var d = a * a + 0.6f * b * b;
                    if (d < bd) { bd = d; v = (Viseme)(k + 1); }
                }
                _audioVis[f] = (byte)v;
                // 계획 대비: 프레임 중심이 모음 중앙 60% 안
                var tc = (st + w * 0.5f) / 12000f; var pk = plan.PhoneAt(tc);
                if (pk < 0 || !NeuralVoiceCloningDubbingEngine.IsVowel(plan.Phone[pk])) continue;
                var u = (tc - plan.Start[pk]) / plan.Dur[pk];
                if (u < 0.2f || u > 0.8f) continue;
                tot++; if (Map(plan.Phone[pk]).V == v) ok++;
            }
            _audioFrames = Math.Max(frames, 0);
            ExtractionAccuracy = tot > 0 ? ok / (float)tot : 0f;
            return ExtractionAccuracy;
        }
    }

    private (float, float) Lpc(int start, int w)
    {
        for (var i = 0; i < w; i++)
        {
            var x = _dec[start + i] - 0.9f * (start + i > 0 ? _dec[start + i - 1] : 0f);
            _win[i] = x * (0.54f - 0.46f * MathF.Cos(MathF.Tau * i / (w - 1)));
        }
        var ws = _win.AsSpan(0, w);
        const int order = 12;
        for (var k = 0; k <= order; k++) _r[k] = DubSimd.Dot(ws[..(w - k)], ws[k..w]);
        if (_r[0] <= 1e-9f) return (0f, 0f);
        _r[0] *= 1.0001f;
        Array.Clear(_a); _a[0] = 1f; var err = _r[0];
        for (var i = 1; i <= order; i++)
        {
            var acc = _r[i];
            for (var j = 1; j < i; j++) acc += _a[j] * _r[i - j];
            var k = -acc / err;
            Array.Copy(_a, _ta, order + 1);
            for (var j = 1; j < i; j++) _a[j] = _ta[j] + k * _ta[i - j];
            _a[i] = k; err *= 1f - k * k;
            if (err <= 0f) break;
        }
        float f1 = 0f, f2 = 0f, p1 = 0f, p2 = 0f;
        for (var f = 150f; f <= 3600f; f += 20f)
        {
            float re = 0f, im = 0f;
            for (var j = 0; j <= order; j++) { var ang = -MathF.Tau * f * j / 12000f; re += _a[j] * MathF.Cos(ang); im += _a[j] * MathF.Sin(ang); }
            var pw = 1f / (re * re + im * im + 1e-12f);
            if (p1 > p2 && p1 > pw && f > 190f)
            {
                if (f1 == 0f) f1 = f - 20f; else if (f2 == 0f && f - 20f - f1 > 250f) { f2 = f - 20f; break; }
            }
            p2 = p1; p1 = pw;
        }
        return (f1, f2);
    }
}
