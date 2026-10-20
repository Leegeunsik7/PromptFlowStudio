using PromptFlowStudio.Services.Localization.Dubbing;

namespace PromptFlowStudio.Services.LiveDubbing;

/// <summary>
/// [PromptFlow Studio v17.32.0] 🛡 A/V 지터 보상 &amp; 음소 드리프트 방어 가드(레인 1개당 1 인스턴스 · 무할당).
///   ▸ WSOLA 시간 신축(피치 보존): 창 20 ms Hann(50% OLA = 1) · 출력 홉 10 ms · 분석 홉 α·10 ms · 허용 ±5 ms 탐색
///     (4× 데시메이션 정규 상관 SIMD → 원해상도 ±3 정밀화) — 자연 연속(직전 선택 + 홉) 템플릿과 최대 유사 구간 선택.
///   ▸ 드리프트 제어: α = (남은 입력 음소 + 미소비 백로그) / (발화 목표 종료까지 남은 출력) · 0.6~1.6 · EMA 0.15
///     → 언어별 음절 증가/감소(예: 한국어 대비 스페인어 +)를 원본 발화 구간에 맞춰 흡수. OFF = α 1(누적 지연).
///   ▸ 입모양 스무딩: 3차 Hermite(Catmull-Rom 접선 × (1 − 장력) · Fritsch–Carlson 단조 제한) + 이항 [1 2 1] 전처리 · 미세 역전(± <see cref="FlickerEps"/>) 평균 고정 · 프레임 속도 제한.
///   ▸ A/V 정렬: 립 평가 룩어헤드 <see cref="LipLagMs"/> 만큼 오디오 지연 → 립-오디오 오프셋 0. 지표: 드리프트(발화 종료 실측 · 예측) · 지연 · 플리커 지수.
/// </summary>
public sealed class AudioVisualSyncJitterCompensationGuard
{
    public const int Fs = RealtimeNeuralVoiceCloningDubbingEngine.Fs, Hop = RealtimeNeuralVoiceCloningDubbingEngine.Hop, Win = 2 * Hop, Tol = 240, Dec = 4;
    public const int InCap = 1 << 15, ExpRing = 16, Mouth = PhonemeStreamProceduralLipSyncSynthesizer.MouthCount;
    public const float LipLagMs = 30f, MinRatio = 0.6f, MaxRatio = 1.6f, FlickerEps = 0.06f, MaxStep = 0.15f, Tension = 0.15f;

    private static readonly float[] Hann = BuildHann();
    private readonly float[] _in = new float[InCap], _acc = new float[Win], _pull = new float[Hop];
    private readonly float[] _tpl = new float[Win], _reg = new float[Win + 2 * Tol];
    private readonly float[] _tplD = new float[Win / Dec], _regD = new float[(Win + 2 * Tol) / Dec];
    private readonly long[] _expSlot = new long[ExpRing], _expEnd = new long[ExpRing];
    private readonly float[] _f1 = new float[Mouth], _f2 = new float[Mouth];
    private long _inWrite, _prevSel = -1, _outClock;
    private double _pa = Tol;
    private int _expCount, _startSeen, _endSeen;
    private float _alpha = 1f;
    private int _frames;

    public AudioVisualSyncJitterCompensationGuard() { }

    /// <summary>WSOLA 드리프트 보정 + Hermite 스무딩 + A/V 지연 보상.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>테스트/수동: 신축비 고정(0 = 자동).</summary>
    public float ForcedRatio { get; set; }
    public float Ratio => _alpha;
    public long OutputClock => _outClock;
    public double InputConsumed => _pa - Tol;
    public long LastInputPos { get; private set; }
    public float LastDriftMs { get; private set; }
    public float MaxAbsDriftMs { get; private set; }
    public float ProjectedDriftMs { get; private set; }
    public float LastLatencyMs { get; private set; }
    public float MaxLatencyMs { get; private set; }
    public int UtterancesMeasured { get; private set; }
    public float FlickerIndex { get; private set; }
    public long SmoothedFrames { get; private set; }
    public long FlickerHolds { get; private set; }
    public float LastCorrelation { get; private set; }
    public double LastHopUs { get; private set; }
    public double MaxHopUs { get; private set; }
    public float LipEvalLagMs => Enabled ? LipLagMs : 0f;
    public float AudioDelayMs => Enabled ? LipLagMs : 0f;
    /// <summary>립 평가 시각 ↔ 재생 오디오 시각 오프셋(가드 ON = 0 · OFF = 홀드 지연 ≈ 반 홉 + 반 프레임).</summary>
    public float LipAudioOffsetMs => Enabled ? 0f : 1000f * Hop / 2f / Fs + 1000f / 120f;

    private static float[] BuildHann()
    {
        var w = new float[Win];
        for (var i = 0; i < Win; i++) w[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / Win);
        return w;
    }

    public void Reset()
    {
        Array.Clear(_in); Array.Clear(_acc); Array.Clear(_f1); Array.Clear(_f2);
        _inWrite = 0; _prevSel = -1; _outClock = 0; _pa = Tol; _expCount = _startSeen = _endSeen = 0; _alpha = 1f; _frames = 0;
        LastInputPos = 0; LastDriftMs = ProjectedDriftMs = LastLatencyMs = 0f;
        ResetMetrics();
    }

    public void ResetMetrics()
    {
        MaxAbsDriftMs = MaxLatencyMs = FlickerIndex = 0f; UtterancesMeasured = 0; SmoothedFrames = FlickerHolds = 0; MaxHopUs = 0;
    }

    /// <summary>발화 기대치 등록(원본 슬롯 시작 · 목표 종료 — 출력 샘플 시계).</summary>
    public void ExpectUtterance(long slotStartOut, long targetEndOut)
    {
        var k = _expCount % ExpRing;
        _expSlot[k] = slotStartOut; _expEnd[k] = targetEndOut;
        _expCount++;
    }

    // ══════════════════════ WSOLA(무할당 핫패스) ══════════════════════

    /// <summary>출력 1 홉(10 ms) 생성 — 필요 입력을 엔진 레인에서 당겨 옴.</summary>
    public void NextHop(RealtimeNeuralVoiceCloningDubbingEngine eng, int lane, Span<float> hop)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var alpha = ComputeAlpha(eng, lane);
        var nominal = (long)Math.Round(_pa);
        var need = nominal + Tol + Win + 1;
        while (_inWrite < need)
        {
            eng.Render(lane, _pull);
            for (var i = 0; i < Hop; i++) _in[(_inWrite + i) & (InCap - 1)] = _pull[i];
            _inWrite += Hop;
        }

        long sel;
        if (_prevSel < 0) { sel = nominal; LastCorrelation = 1f; }
        else
        {
            var natural = _prevSel + Hop;
            for (var i = 0; i < Win; i++) _tpl[i] = _in[(natural + i) & (InCap - 1)];
            var r0 = nominal - Tol;
            for (var i = 0; i < _reg.Length; i++) _reg[i] = _in[(r0 + i) & (InCap - 1)];
            for (var i = 0; i < _tplD.Length; i++) _tplD[i] = _tpl[i * Dec];
            for (var i = 0; i < _regD.Length; i++) _regD[i] = _reg[i * Dec];
            int wd = _tplD.Length, steps = 2 * Tol / Dec;
            var tE = DubSimd.Dot(_tplD, _tplD);
            float best = float.NegativeInfinity; var bestD = Tol / Dec;
            for (var d = 0; d <= steps; d++)
            {
                var c = _regD.AsSpan(d, wd);
                var score = DubSimd.Dot(_tplD, c) / MathF.Sqrt(DubSimd.Dot(c, c) * tE + 1e-12f);
                // 동점 시 공칭(δ = 0) 쪽 선호 → 정상 상태 항등
                score -= 1e-4f * Math.Abs(d - Tol / Dec);
                if (score > best) { best = score; bestD = d; }
            }
            var coarse = bestD * Dec;
            float bestF = float.NegativeInfinity; var bestOff = coarse;
            var tEF = DubSimd.Dot(_tpl, _tpl);
            for (var o = Math.Max(0, coarse - Dec + 1); o <= Math.Min(2 * Tol, coarse + Dec - 1); o++)
            {
                var c = _reg.AsSpan(o, Win);
                var score = DubSimd.Dot(_tpl, c) / MathF.Sqrt(DubSimd.Dot(c, c) * tEF + 1e-12f);
                if (score > bestF) { bestF = score; bestOff = o; }
            }
            LastCorrelation = float.IsFinite(bestF) ? bestF : 0f;
            sel = r0 + bestOff;
        }

        for (var i = 0; i < Win; i++) _acc[i] += Hann[i] * _in[(sel + i) & (InCap - 1)];
        _acc.AsSpan(0, Hop).CopyTo(hop);
        _acc.AsSpan(Hop, Win - Hop).CopyTo(_acc);
        Array.Clear(_acc, Win - Hop, Hop);
        _prevSel = sel;
        LastInputPos = sel + Hop / 2;
        _pa += alpha * Hop;
        _outClock += Hop;
        Measure(eng, lane);

        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastHopUs = us;
        if (us > MaxHopUs) MaxHopUs = us;
    }

    private float ComputeAlpha(RealtimeNeuralVoiceCloningDubbingEngine eng, int lane)
    {
        if (ForcedRatio > 0f) return _alpha = ForcedRatio;
        var remIn = eng.RemainingSamples(lane);
        var backlog = Math.Max(0.0, _inWrite - _pa - Tol - Win);
        float target = 1f;
        if (Enabled && remIn > 0 && _expCount > 0)
        {
            var remOut = _expEnd[(_expCount - 1) % ExpRing] - _outClock;
            target = (float)Math.Clamp((remIn + backlog) / Math.Max(remOut, 4.0 * Hop), MinRatio, MaxRatio);
        }
        _alpha += 0.15f * (target - _alpha);
        if (!Enabled) _alpha = 1f;
        var eff = _alpha;
        ProjectedDriftMs = remIn > 0 && _expCount > 0
            ? 1000f * (float)(_outClock + (remIn + backlog) / eff - _expEnd[(_expCount - 1) % ExpRing]) / Fs
            : LastDriftMs;
        return _alpha;
    }

    private void Measure(RealtimeNeuralVoiceCloningDubbingEngine eng, int lane)
    {
        // 입력 위치(발화 시작/종료)가 출력에 도달한 시각 ↔ 기대치
        while (_startSeen < eng.UtterancesStarted(lane) && _startSeen < _expCount && _pa >= eng.UtteranceStartPos(lane, _startSeen))
        {
            LastLatencyMs = 1000f * (_outClock - _expSlot[_startSeen % ExpRing]) / Fs;
            if (LastLatencyMs > MaxLatencyMs) MaxLatencyMs = LastLatencyMs;
            _startSeen++;
        }
        while (_endSeen < eng.UtterancesDone(lane) && _endSeen < _expCount && _pa >= eng.UtteranceEndPos(lane, _endSeen))
        {
            LastDriftMs = 1000f * (_outClock - _expEnd[_endSeen % ExpRing]) / Fs;
            if (MathF.Abs(LastDriftMs) > MaxAbsDriftMs) MaxAbsDriftMs = MathF.Abs(LastDriftMs);
            UtterancesMeasured++;
            _endSeen++;
        }
    }

    // ══════════════════════ 3차 Hermite 입모양 스무딩 ══════════════════════

    /// <summary>
    /// 키프레임 6개(k−2..k+3 · 10 ms 간격 · 행 = 입모양 <see cref="Mouth"/>) → 이항 [1 2 1]/4 전처리 → k..k+1 사이 u ∈ [0,1) 3차 Hermite → dst.
    /// 가드 OFF 는 호출자가 최근접 홀드 사용. prev = 직전 출력 프레임(속도 제한 기준).
    /// </summary>
    public void Hermite(ReadOnlySpan<float> keys6, float u, ReadOnlySpan<float> prev, Span<float> dst)
    {
        float u2 = u * u, u3 = u2 * u;
        float h00 = 2f * u3 - 3f * u2 + 1f, h10 = u3 - 2f * u2 + u, h01 = -2f * u3 + 3f * u2, h11 = u3 - u2;
        var m = dst.Length;
        for (var s = 0; s < m; s++)
        {
            float q0 = keys6[s], q1 = keys6[m + s], q2 = keys6[2 * m + s], q3 = keys6[3 * m + s], q4 = keys6[4 * m + s], q5 = keys6[5 * m + s];
            float a = 0.25f * (q0 + q2) + 0.5f * q1, b = 0.25f * (q1 + q3) + 0.5f * q2, c = 0.25f * (q2 + q4) + 0.5f * q3, d = 0.25f * (q3 + q5) + 0.5f * q4;
            float d0 = b - a, d1 = c - b, d2 = d - c;
            // 미세 역전(지터) → 4점 평균 고정
            if ((d0 * d1 < 0f && MathF.Abs(d0) < FlickerEps && MathF.Abs(d1) < FlickerEps) || (d1 * d2 < 0f && MathF.Abs(d1) < FlickerEps && MathF.Abs(d2) < FlickerEps))
            {
                var mean = 0.25f * (a + b + c + d);
                b = c = mean; d1 = 0f;
                FlickerHolds++;
            }
            float m1 = 0.5f * (c - a) * (1f - Tension), m2 = 0.5f * (d - b) * (1f - Tension);
            if (d1 == 0f) m1 = m2 = 0f;
            else
            {
                if (m1 * d1 <= 0f) m1 = 0f;
                if (m2 * d1 <= 0f) m2 = 0f;
                var lim = 3f * MathF.Abs(d1);
                m1 = Math.Clamp(m1, -lim, lim); m2 = Math.Clamp(m2, -lim, lim);
            }
            var v = h00 * b + h10 * m1 + h01 * c + h11 * m2;
            v = Math.Clamp(v, prev[s] - MaxStep, prev[s] + MaxStep);
            dst[s] = Math.Clamp(v, 0f, 1f);
        }
        SmoothedFrames++;
    }

    /// <summary>플리커 지수(입모양 2차 차분 절대 평균 × 100 · EMA) — 매 프레임 출력 후 호출.</summary>
    public void MeasureFlicker(ReadOnlySpan<float> frame)
    {
        if (_frames >= 2)
        {
            var sum = 0f;
            for (var s = 0; s < frame.Length; s++) sum += MathF.Abs(frame[s] - 2f * _f1[s] + _f2[s]);
            var v = 100f * sum / frame.Length;
            FlickerIndex += (_frames < 60 ? 1f / (_frames - 1) : 0.02f) * (v - FlickerIndex);
        }
        _f1.AsSpan().CopyTo(_f2);
        frame.CopyTo(_f1);
        _frames++;
    }
}
