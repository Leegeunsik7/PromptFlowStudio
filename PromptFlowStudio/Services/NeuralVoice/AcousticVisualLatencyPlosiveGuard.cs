using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.Localization.Dubbing;

namespace PromptFlowStudio.Services.NeuralVoice;

/// <summary>
/// [PromptFlow Studio v17.18.0] 🛡️ 음향-시각 음소 지연 동기화 &amp; 파열음 클리핑 가드(<see cref="Process"/> · <see cref="VisualWeights"/> 무할당).
///   ▸ 지연 모델: 합성 샘플 s 는 s + Lₐ(청크 + 룩어헤드 + 장치 버퍼)에 들리고, 지금 렌더한 영상 프레임은 W + Lᵥ(디스플레이)에 보인다 →
///     락: 립싱크는 타임라인을 s* = W + Lᵥ − Lₐ(샘플 정밀 · 소수 샘플)에서 평가 → 오차 = 양자화(&lt; 0.021 ms). 비보정(s = W) 오차 = Lₐ − Lᵥ.
///   ▸ 립싱크 가중치: v17.16 립싱크 표(15 입 셰이프 · v16.82 SetSpeech 동일) · 무상태 공동조음 = 직전 목표 → 현재 목표 지수 접근(τ 22 ms) — 임의 시각 재평가 가능.
///   ▸ v17.12 Audio2Face 교차 검증: 출력 오디오 분석 턱 개방 시계열 ↔ 타임라인 턱 시계열 상호상관 → A2F 분석 지연(ms) 계측(락 경로는 A2F 지연 비의존).
///   ▸ 파열음 팝 필터: P/B/T 버스트 ±12 ms 창에서 1차 고역 통과(150 Hz) 블렌드 → 저역 '퍽' 제거.
///   ▸ 소프트 니 리미터: 1 ms 룩어헤드 링버퍼 · 창 피크 dB → 소프트 니(임계 −1 dBFS · 니 6 dB · ∞:1) 이득 · 즉시 어택(룩어헤드) / 릴리스 τ 60 ms · 최종 안전 천장 −0.3 dBFS.
/// </summary>
public sealed class AcousticVisualLatencyPlosiveGuard
{
    public const int Fs = RealtimeNeuralVoiceSynthesisEngine.Fs, Lookahead = 48, Shapes = GenerativeNeuralLipSyncSynthesizer.Shapes, JawRing = 256;
    public const float CoartTauMs = 22f;

    private readonly RealtimeNeuralVoiceSynthesisEngine _voice;
    private readonly float[] _delay = new float[Lookahead + 1];   // 지연 48 + 출력 샘플 포함 창
    private readonly float[] _w = new float[Shapes];
    private readonly float[] _jawA2f = new float[JawRing], _jawTl = new float[JawRing];
    private int _dHead, _jawHead, _jawCount;
    private long _in;
    private float _gain = 1f, _hpX, _hpY;

    public AcousticVisualLatencyPlosiveGuard(RealtimeNeuralVoiceSynthesisEngine voice) => _voice = voice;

    public bool LimiterEnabled { get; set; } = true;
    public bool PopFilter { get; set; } = true;
    public bool LatencyLock { get; set; } = true;
    public float ThresholdDb { get; set; } = -1f;
    public float KneeDb { get; set; } = 6f;
    public float CeilingDb { get; set; } = -0.3f;
    public float ReleaseMs { get; set; } = 60f;
    public float InputGain { get; set; } = 1f;
    /// <summary>오디오 장치 출력 버퍼(ms).</summary>
    public float DeviceBufferMs { get; set; } = 20f;
    /// <summary>영상 표시 지연(ms · 렌더 → 디스플레이).</summary>
    public float VideoLatencyMs { get; set; } = 16.7f;
    public float AudioLatencyMs => 1000f * (RealtimeNeuralVoiceSynthesisEngine.Chunk + Lookahead) / Fs + DeviceBufferMs;
    public float AlgorithmicLatencyMs => 1000f * (RealtimeNeuralVoiceSynthesisEngine.Chunk + Lookahead) / Fs;
    public long RawOvers { get; private set; }
    public long OutputOvers { get; private set; }
    public long CeilingClamps { get; private set; }
    public long PopWindows { get; private set; }
    public float RawPeakDb { get; private set; } = -120f;
    public float OutputPeakDb { get; private set; } = -120f;
    public float GainReductionDb { get; private set; }
    public float MaxGainReductionDb { get; private set; }
    public float LockErrorMs { get; private set; }
    public float NaiveErrorMs => AudioLatencyMs - VideoLatencyMs;
    public float A2fLagMs { get; private set; }
    public double LastProcessUs { get; private set; }
    public float Weight(int s) => _w[s];
    public long Processed => _in;

    public void ResetMeters() { RawOvers = OutputOvers = CeilingClamps = 0; RawPeakDb = OutputPeakDb = -120f; MaxGainReductionDb = 0f; }

    private static float Db(float x) => 20f * MathF.Log10(MathF.Max(1e-9f, x));

    /// <summary>소프트 니 이득 계산(dB 입력 → 이득 dB · ∞:1).</summary>
    public static float SoftKneeGainDb(float levelDb, float thresholdDb, float kneeDb)
    {
        var over = levelDb - thresholdDb;
        if (over <= -kneeDb * 0.5f) return 0f;
        if (over >= kneeDb * 0.5f) return -over;
        var x = over + kneeDb * 0.5f;
        return -x * x / (2f * kneeDb);
    }

    private bool InPopWindow(long sample)
    {
        var win = 12 * Fs / 1000;
        for (var k = 0; k < Math.Min(8, _voice.EventCount); k++)
        {
            var ev = _voice.Event(k);
            if (ev.BurstSample >= 0 && sample >= ev.BurstSample - win && sample <= ev.BurstSample + win) return true;
        }
        return false;
    }

    /// <summary>합성 청크 → 팝 필터 → 룩어헤드 소프트 니 리미터 → dst(동일 길이 · 지연 48 샘플 · 무할당).</summary>
    public void Process(ReadOnlySpan<float> src, Span<float> dst)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var hpA = MathF.Exp(-MathF.Tau * 150f / Fs);
        var rel = 1f - MathF.Exp(-1000f / (Fs * ReleaseMs));
        var ceil = MathF.Pow(10f, CeilingDb / 20f);
        var popStart = _in;
        var pop = PopFilter && InPopWindow(popStart + src.Length / 2);
        if (pop) PopWindows++;
        for (var i = 0; i < src.Length; i++)
        {
            var x = src[i] * InputGain;
            // 1차 고역 통과(팝 창에서만 적용 · 상태는 항상 갱신)
            var hp = hpA * (_hpY + x - _hpX);
            _hpX = x; _hpY = hp;
            if (pop) x = hp;
            var ax = MathF.Abs(x);
            if (ax > 1f) RawOvers++;
            var rdb = Db(ax);
            if (rdb > RawPeakDb) RawPeakDb = rdb;
            _delay[_dHead] = x;
            _dHead = (_dHead + 1) % (Lookahead + 1);
            var y = _delay[_dHead];   // 48 샘플 지연(창 = 출력 샘플 … 최신 입력)
            if (LimiterEnabled)
            {
                var peak = 0f;
                for (var k = 0; k <= Lookahead; k++) { var v = MathF.Abs(_delay[k]); if (v > peak) peak = v; }
                var target = MathF.Pow(10f, SoftKneeGainDb(Db(peak), ThresholdDb, KneeDb) / 20f);
                _gain = target < _gain ? target : _gain + (target - _gain) * rel;
                y *= _gain;
                if (MathF.Abs(y) > ceil) { y = MathF.CopySign(ceil, y); CeilingClamps++; }
            }
            var ay = MathF.Abs(y);
            if (ay > 1f) OutputOvers++;
            var odb = Db(ay);
            if (odb > OutputPeakDb) OutputPeakDb = odb;
            dst[i] = y;
            _in++;
        }
        GainReductionDb = -Db(_gain);
        if (GainReductionDb > MaxGainReductionDb) MaxGainReductionDb = GainReductionDb;
        LastProcessUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>
    /// 지금(합성 시계 W) 렌더하는 영상 프레임의 입 가중치. 락 = 표시 시각에 들리는 오디오 샘플 s* = W + Lᵥ − Lₐ 에서 타임라인 평가(무할당).
    /// </summary>
    public void VisualWeights(long synthClock)
    {
        var sStar = LatencyLock ? synthClock + (VideoLatencyMs - AudioLatencyMs) * Fs / 1000.0 : synthClock;
        EvaluateAt(sStar, _w);
        // 오차: 표시 시각(W + Lᵥ) − 그 입 모양 음향이 들리는 시각(s + Lₐ)
        LockErrorMs = (float)Math.Abs((synthClock + VideoLatencyMs * Fs / 1000.0) - (sStar + AudioLatencyMs * Fs / 1000.0)) * 1000f / Fs;
        LockErrorMs = MathF.Max(LockErrorMs, LatencyLock ? 0.5f * 1000f / Fs : 0f);   // 소수 샘플 → 출력 샘플 양자화 반 샘플
    }

    /// <summary>타임라인 시각(합성 샘플 · 소수) → 입 가중치(직전 목표 → 현재 목표 지수 접근).</summary>
    public void EvaluateAt(double sample, Span<float> dst)
    {
        Viseme cur = Viseme.Rest, prev = Viseme.Rest;
        double onset = 0;
        for (var k = 0; k < _voice.EventCount; k++)
        {
            var ev = _voice.Event(k);
            if (ev.Sample > sample) continue;
            cur = RealtimeNeuralVoiceSynthesisEngine.VisemeOf(ev.Phone);
            onset = ev.Sample;
            prev = k + 1 < _voice.EventCount ? RealtimeNeuralVoiceSynthesisEngine.VisemeOf(_voice.Event(k + 1).Phone) : Viseme.Rest;
            break;
        }
        var a = 1f - MathF.Exp(-(float)((sample - onset) * 1000.0 / Fs) / CoartTauMs);
        for (var s = 0; s < Shapes; s++)
        {
            float p = GenerativeNeuralLipSyncSynthesizer.TargetWeight(prev, s), c = GenerativeNeuralLipSyncSynthesizer.TargetWeight(cur, s);
            dst[s] = p + (c - p) * a;
        }
    }

    /// <summary>A2F(v17.12) 턱 개방 ↔ 타임라인 턱(동일 합성 시계 · 10 ms 홉) 기록.</summary>
    public void RecordJaw(float a2fJaw, long synthClockAtHopEnd)
    {
        Span<float> tmp = stackalloc float[Shapes];
        EvaluateAt(synthClockAtHopEnd - Lookahead, tmp);
        _jawA2f[_jawHead] = a2fJaw; _jawTl[_jawHead] = tmp[0];
        _jawHead = (_jawHead + 1) % JawRing;
        if (_jawCount < JawRing) _jawCount++;
    }

    /// <summary>상호상관 최대 지연(홉 10 ms 단위 · 0–8 홉 · 2 차 보간) → A2F 분석 지연(ms).</summary>
    public void MeasureA2fLag()
    {
        if (_jawCount < 64) return;
        Span<double> cc = stackalloc double[9];
        var n = _jawCount - 9;
        for (var lag = 0; lag < 9; lag++)
        {
            double s = 0, ma = 0, mt = 0;
            for (var i = 0; i < n; i++) { ma += _jawA2f[Idx(i)]; mt += _jawTl[Idx(i + lag)]; }
            ma /= n; mt /= n;
            for (var i = 0; i < n; i++) s += (_jawA2f[Idx(i)] - ma) * (_jawTl[Idx(i + lag)] - mt);
            cc[lag] = s;
        }
        var best = 0;
        for (var lag = 1; lag < 9; lag++) if (cc[lag] > cc[best]) best = lag;
        var off = 0.0;
        if (best > 0 && best < 8) { var den = cc[best - 1] - 2 * cc[best] + cc[best + 1]; if (Math.Abs(den) > 1e-12) off = 0.5 * (cc[best - 1] - cc[best + 1]) / den; }
        A2fLagMs = (float)((best + off) * 10.0);
    }

    private int Idx(int back) => (_jawHead - 1 - back + 2 * JawRing) % JawRing;
}
