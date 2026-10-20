using PromptFlowStudio.Services.LowSpec;

namespace PromptFlowStudio.Services.Localization;

/// <summary>입모양 클래스(비짐) — 개방 모음 A · 중모음 E · 폐모음 I · 원순 O/U · 양순 MBP · 순치 FV · 일반 자음 C · 휴지.</summary>
public enum Viseme : byte { Rest, A, E, I, O, U, MBP, FV, C }

/// <summary>더빙 음소(값 형식) — 비짐 · 길이 · 유성 · 강세.</summary>
public readonly record struct DubPhoneme(Viseme Viseme, float DurationMs, bool Voiced, bool Stressed)
{
    public static (float F1, float F2) Formants(Viseme v) => v switch
    {
        Viseme.A => (800, 1250), Viseme.E => (500, 1850), Viseme.I => (300, 2250), Viseme.O => (500, 900), Viseme.U => (330, 800),
        Viseme.MBP => (250, 1000), Viseme.FV => (400, 1500), Viseme.C => (450, 1700), _ => (500, 1500),
    };

    /// <summary>입 벌림(0..1).</summary>
    public static float Openness(Viseme v) => v switch
    {
        Viseme.A => 1f, Viseme.E => 0.6f, Viseme.I => 0.3f, Viseme.O => 0.7f, Viseme.U => 0.4f, Viseme.MBP => 0f, Viseme.FV => 0.15f, Viseme.C => 0.35f, _ => 0.05f,
    };
}

/// <summary>배우 음성 프로파일 — 기본 F0 · F0 범위(반음) · 밝기(스펙트럼 중심 kHz) · 각성도(감정 강도) · 잔향 RT60 · 습윤비 · 평균 에너지(dBFS).</summary>
public readonly record struct VoiceProfile(float F0Hz, float F0RangeSemis, float Brightness, float Arousal, float Rt60Ms, float Wet, float EnergyDb);

/// <summary>톤 조율기(UI) — 피치 이동 · 밝기 보정 · 감정 강도 · 잔향 혼합 배율.</summary>
public readonly record struct VoiceToneSettings(float PitchShiftSemis = 0, float BrightnessTrim = 0, float Emotion = 1, float ReverbMix = 1)
{
    /// <summary>기본값(감정·잔향 배율 1) — 구조체 기본 생성자의 0 초기화 방지.</summary>
    public VoiceToneSettings() : this(0, 0, 1, 1) { }
}

/// <summary>외부 신경망 보코더 결선점(ONNX/VITS/XTTS 등) — 미결선/실패 시 내장 DSP 소스-필터 합성 폴백.</summary>
public interface INeuralVoiceBackend
{
    string Name { get; }
    bool TryRender(ReadOnlySpan<DubPhoneme> phonemes, in VoiceProfile profile, ReadOnlySpan<float> timbre, Span<float> output, out int written);
}

/// <summary>
/// [PromptFlow Studio v16.69.0] 🎙 초저지연 보이스 클로닝 &amp; 음향 어쿠스틱 전송기 — 16kHz.
/// 분석: 정규화 자기상관(SIMD 내적) F0 · 백분위 F0 범위 · Goertzel 8밴드 음색 임베딩(평균 제거 로그) · 스펙트럼 중심 · RMS 변동 각성도 · Schroeder 역적분 RT60.
/// 합성: 블록(256 = 16ms) 스트리밍 소스-필터 — 성문 펄스 미분 + 잡음 · 2 포먼트 공진기(32샘플마다 보간) · 기울기 필터(폐루프 이분법으로 원 배우 밝기 정합) ·
/// F0 윤곽(하강 · 강세 · 감정 비브라토) · Schroeder 잔향(4 콤 + 2 올패스 · RT60 정합). 분석/합성 경로 무할당.
/// </summary>
public sealed class NeuralVoiceCloningAcousticTransfer
{
    public const int SampleRate = 16000, Block = 256, Bands = 8, MaxPhonemes = 2048, TailMs = 1200;
    public const int AnalysisWindow = 1024, AnalysisHop = 256, MaxAnalysisFrames = 4096;
    private static readonly float[] BandHz = { 250, 500, 800, 1200, 1800, 2600, 3600, 5000 };
    private static readonly int[] CombDelay = { 557, 593, 641, 677 };
    private static readonly int[] AllpassDelay = { 142, 49 };

    private readonly object _gate = new();
    private readonly float[] _f0s = new float[MaxAnalysisFrames], _rms = new float[MaxAnalysisFrames * 2], _schroeder = new float[MaxAnalysisFrames * 2];
    private readonly float[] _bandAcc = new float[Bands], _timbre = new float[Bands];
    private readonly DubPhoneme[] _ph = new DubPhoneme[MaxPhonemes];
    private readonly float[][] _comb = new float[4][], _ap = new float[2][];
    private readonly int[] _combIdx = new int[4], _apIdx = new int[2];
    private readonly float[] _combLp = new float[4];
    private readonly float[] _calib = new float[SampleRate * 2];
    // 합성 상태
    private int _phCount, _phIdx;
    private double _phPos, _phase, _totalMs, _elapsedMs;
    private float _amp, _f1, _f2, _tilt, _prevX, _prevX2, _prevG, _tailLeft;
    private float _b1z1, _b1z2, _b2z1, _b2z2, _b3z1, _b3z2;
    private uint _rng = 0x1234567;
    private VoiceProfile _profile = new(120, 4, 1.4f, 0.4f, 400, 0.25f, -20);
    private VoiceToneSettings _tone = new();
    private float _calibratedTilt;
    private long _blocks, _analyses, _neuralHits;
    private double _lastRenderMicros;

    public NeuralVoiceCloningAcousticTransfer()
    {
        for (var i = 0; i < 4; i++) _comb[i] = new float[CombDelay[i]];
        for (var i = 0; i < 2; i++) _ap[i] = new float[AllpassDelay[i]];
    }

    public event Action? OnChange;

    public INeuralVoiceBackend? Neural { get; set; }
    public VoiceProfile Profile { get { lock (_gate) return _profile; } }
    public VoiceToneSettings Tone { get { lock (_gate) return _tone; } set { lock (_gate) _tone = value; } }
    public float CalibratedTilt { get { lock (_gate) return _calibratedTilt; } }
    public long Blocks { get { lock (_gate) return _blocks; } }
    public long Analyses { get { lock (_gate) return _analyses; } }
    public long NeuralHits { get { lock (_gate) return _neuralHits; } }
    public double LastRenderMicros { get { lock (_gate) return _lastRenderMicros; } }
    public static double AlgorithmicLatencyMs => Block * 1000.0 / SampleRate;
    public string? LastAction { get; private set; }

    public void CopyTimbre(Span<float> dst) { lock (_gate) _timbre.AsSpan(0, Math.Min(Bands, dst.Length)).CopyTo(dst); }

    // ══════════ 분석 ══════════

    /// <summary>PCM(16kHz) → 프로파일 + 음색 임베딩(<paramref name="timbre"/> 8) · 무할당.</summary>
    public VoiceProfile Analyze(ReadOnlySpan<float> pcm, Span<float> timbre)
    {
        lock (_gate)
        {
            var frames = Math.Min(MaxAnalysisFrames, Math.Max(0, (pcm.Length - AnalysisWindow) / AnalysisHop + 1));
            // RMS(dB) · 최대
            var maxDb = -120f;
            for (var f = 0; f < frames; f++)
            {
                var w = pcm.Slice(f * AnalysisHop, AnalysisWindow);
                var e = DynamicLowBitQuantizationFallbackRouter.DotF32(w, w) / AnalysisWindow;
                var db = 10 * MathF.Log10(e + 1e-12f);
                _rms[f] = db;
                if (db > maxDb) maxDb = db;
            }
            int voiced = 0;
            double dbSum = 0, dbSq = 0;
            _bandAcc.AsSpan().Clear();
            const int minLag = SampleRate / 400, maxLag = SampleRate / 70;
            Span<float> r = stackalloc float[maxLag + 2];
            for (var f = 0; f < frames; f++)
            {
                if (_rms[f] < maxDb - 18) continue;
                dbSum += _rms[f]; dbSq += _rms[f] * _rms[f];
                var w = pcm.Slice(f * AnalysisHop, AnalysisWindow);
                // 정규화 자기상관 — 최대의 90% 이상인 최소 지연(옥타브 오류 억제)
                const int n = AnalysisWindow - maxLag - 1;
                var a = w[..n];
                var ea = DynamicLowBitQuantizationFallbackRouter.DotF32(a, a);
                var best = 0f;
                for (var lag = minLag; lag <= maxLag + 1; lag++)
                {
                    var b = w.Slice(lag, n);
                    var v = DynamicLowBitQuantizationFallbackRouter.DotF32(a, b) / MathF.Sqrt(ea * DynamicLowBitQuantizationFallbackRouter.DotF32(b, b) + 1e-12f);
                    r[lag] = v;
                    if (v > best) best = v;
                }
                if (best < 0.5f) continue;
                var pick = -1;
                for (var lag = minLag + 1; lag <= maxLag; lag++)
                    if (r[lag] >= 0.9f * best && r[lag] >= r[lag - 1] && r[lag] >= r[lag + 1]) { pick = lag; break; }
                if (pick < 0) continue;
                var den = r[pick - 1] - 2 * r[pick] + r[pick + 1];
                var frac = den != 0 ? 0.5f * (r[pick - 1] - r[pick + 1]) / den : 0;
                _f0s[voiced++] = SampleRate / (pick + frac);
                // Goertzel 8밴드(창 앞 512)
                // 밴드당 6점 Goertzel(중심 ±30%) 평균 — 조화음 간격에 따른 단일 빈 누락 방지
                for (var k = 0; k < Bands; k++)
                {
                    float acc = 0;
                    for (var q = 0; q < 6; q++) acc += Goertzel(w[..512], BandHz[k] * (0.7f + 0.12f * q));
                    _bandAcc[k] += acc / 6;
                }
            }
            float f0 = 120, range = 0;
            if (voiced > 0)
            {
                var s = _f0s.AsSpan(0, voiced);
                s.Sort();
                f0 = s[voiced / 2];
                var lo = s[voiced / 10]; var hi = s[Math.Min(voiced - 1, voiced * 9 / 10)];
                range = 12 * MathF.Log2(hi / lo);
            }
            // 음색 임베딩(평균 제거 로그 에너지 · 단위 벡터) · 스펙트럼 중심
            float mean = 0, cw = 0, ct = 0;
            for (var k = 0; k < Bands; k++) { timbre[k] = MathF.Log10(_bandAcc[k] + 1e-9f); mean += timbre[k]; var amp = MathF.Sqrt(_bandAcc[k]); cw += amp * BandHz[k]; ct += amp; }   // 진폭 가중 중심
            mean /= Bands;
            float norm = 0;
            for (var k = 0; k < Bands; k++) { timbre[k] -= mean; norm += timbre[k] * timbre[k]; }
            norm = norm > 0 ? 1 / MathF.Sqrt(norm) : 0;
            for (var k = 0; k < Bands; k++) timbre[k] *= norm;
            var brightness = ct > 0 ? cw / ct / 1000f : 1f;
            var vcount = 0;
            for (var f = 0; f < frames; f++) if (_rms[f] >= maxDb - 18) vcount++;
            var dbMean = vcount > 0 ? dbSum / vcount : -60;
            var dbStd = vcount > 1 ? Math.Sqrt(Math.Max(0, dbSq / vcount - dbMean * dbMean)) : 0;
            var arousal = 0.5f * Math.Clamp(((float)dbStd - 3f) / 9f, 0f, 1f) + 0.5f * Math.Clamp(range / 12f, 0f, 1f);
            var rt60 = EstimateRt60(pcm);
            _profile = new VoiceProfile(f0, range, brightness, arousal, rt60, Math.Clamp(rt60 / 1500f, 0.1f, 0.6f), (float)dbMean);
            timbre[..Bands].CopyTo(_timbre);
            _analyses++;
            return _profile;
        }
    }

    private static float Goertzel(ReadOnlySpan<float> x, float hz)
    {
        var coeff = 2 * MathF.Cos(2 * MathF.PI * hz / SampleRate);
        float s1 = 0, s2 = 0;
        for (var i = 0; i < x.Length; i++) { var s = x[i] + coeff * s1 - s2; s2 = s1; s1 = s; }
        return (s1 * s1 + s2 * s2 - coeff * s1 * s2) / x.Length;
    }

    /// <summary>Schroeder 역적분 — 발화 종료 이후 꼬리 −5 → −25dB 기울기(T20) → RT60(ms).</summary>
    private float EstimateRt60(ReadOnlySpan<float> pcm)
    {
        const int hop = 128;
        var frames = Math.Min(_schroeder.Length, pcm.Length / hop);
        if (frames < 8) return 0;
        var max = 0f;
        for (var f = 0; f < frames; f++)
        {
            var w = pcm.Slice(f * hop, hop);
            var e = DynamicLowBitQuantizationFallbackRouter.DotF32(w, w);
            _rms[f] = e;
            if (e > max) max = e;
        }
        var end = -1;
        for (var f = frames - 1; f >= 0; f--) if (_rms[f] >= max * 0.01f) { end = f; break; }   // −20dB 이상 마지막 프레임 = 발화 끝
        if (end < 0 || end >= frames - 4) return 0;
        double acc = 0;
        for (var f = frames - 1; f >= end; f--) { acc += _rms[f]; _schroeder[f] = (float)acc; }
        var e0 = _schroeder[end];
        int a = -1, b = -1;
        for (var f = end; f < frames; f++)
        {
            var db = 10 * MathF.Log10(_schroeder[f] / e0 + 1e-12f);
            if (a < 0 && db <= -5) a = f;
            if (db <= -25) { b = f; break; }
        }
        if (a < 0 || b <= a) return 0;
        // 최소제곱 기울기(dB/프레임)
        double sx = 0, sy = 0, sxx = 0, sxy = 0; var n = b - a + 1;
        for (var f = a; f <= b; f++) { var y = 10 * Math.Log10(_schroeder[f] / e0 + 1e-12); sx += f; sy += y; sxx += (double)f * f; sxy += f * y; }
        var slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        return slope < 0 ? (float)(-60 / slope * hop * 1000.0 / SampleRate) : 0;
    }

    // ══════════ 합성 ══════════

    /// <summary>발화 시작 — 음소 복사 · 상태 초기화(무할당).</summary>
    public void Begin(ReadOnlySpan<DubPhoneme> phonemes, in VoiceProfile profile)
    {
        lock (_gate)
        {
            _phCount = Math.Min(MaxPhonemes, phonemes.Length);
            phonemes[.._phCount].CopyTo(_ph);
            _profile = profile;
            _totalMs = 0;
            for (var i = 0; i < _phCount; i++) _totalMs += _ph[i].DurationMs;
            _phIdx = 0; _phPos = 0; _elapsedMs = 0; _phase = 0;
            _amp = 0; _prevX = 0; _prevX2 = 0; _prevG = 0;
            (_f1, _f2) = _phCount > 0 ? DubPhoneme.Formants(_ph[0].Viseme) : (500, 1500);
            _b1z1 = _b1z2 = _b2z1 = _b2z2 = _b3z1 = _b3z2 = 0;
            for (var i = 0; i < 4; i++) { Array.Clear(_comb[i]); _combIdx[i] = 0; _combLp[i] = 0; }
            for (var i = 0; i < 2; i++) { Array.Clear(_ap[i]); _apIdx[i] = 0; }
            _tilt = Math.Clamp(_calibratedTilt + _tone.BrightnessTrim, -0.95f, 0.95f);
            _tailLeft = TailMs * SampleRate / 1000f;
        }
    }

    public bool Finished { get { lock (_gate) return _phIdx >= _phCount && _tailLeft <= 0; } }

    /// <summary>스트리밍 블록 렌더(≤ <see cref="Block"/> 권장) — 기록 샘플 수(0 = 종료) · 무할당.</summary>
    public int Render(Span<float> output)
    {
        lock (_gate)
        {
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var n = 0;
            var p = _profile; var tone = _tone;
            var baseF0 = p.F0Hz * MathF.Pow(2, tone.PitchShiftSemis / 12f);
            var arousal = Math.Clamp(p.Arousal * tone.Emotion, 0f, 1.5f);
            var wet = Math.Clamp(p.Wet * tone.ReverbMix, 0f, 0.9f);
            Span<float> g = stackalloc float[4];
            for (var c = 0; c < 4; c++) g[c] = p.Rt60Ms > 0 ? MathF.Pow(10, -3f * CombDelay[c] / (p.Rt60Ms / 1000f * SampleRate)) : 0;
            float a1b1 = 0, a2b1 = 0, gb1 = 0, a1b2 = 0, a2b2 = 0, gb2 = 0;
            Coeffs(2600, 220, out var a1b3, out var a2b3, out var gb3);
            for (; n < output.Length; n++)
            {
                if (_phIdx >= _phCount && _tailLeft <= 0) break;
                float target = 0, noiseMix = 0;
                var voiced = false; var stressed = false;
                var vis = Viseme.Rest;
                if (_phIdx < _phCount)
                {
                    ref readonly var ph = ref _ph[_phIdx];
                    vis = ph.Viseme; voiced = ph.Voiced; stressed = ph.Stressed;
                    (target, noiseMix) = vis switch
                    {
                        Viseme.A or Viseme.E or Viseme.I or Viseme.O or Viseme.U => (0.4f + 0.6f * DubPhoneme.Openness(vis), 0.03f),   // 개구도-음량 상관
                        Viseme.MBP => (voiced ? 0.25f : 0.05f, 0.05f),
                        Viseme.FV => (0.35f, 0.9f),
                        Viseme.C => (voiced ? 0.5f : 0.35f, voiced ? 0.3f : 0.85f),
                        _ => (0f, 0f),
                    };
                }
                else _tailLeft--;
                // 32샘플마다 포먼트 보간 · 계수 갱신
                if ((n & 31) == 0)
                {
                    var (tf1, tf2) = DubPhoneme.Formants(vis);
                    _f1 += 0.35f * (tf1 - _f1); _f2 += 0.35f * (tf2 - _f2);
                    Coeffs(_f1, 90, out a1b1, out a2b1, out gb1);
                    Coeffs(_f2, 120, out a1b2, out a2b2, out gb2);
                }
                _amp += (target - _amp) * 0.006f;
                // F0 윤곽: 하강(평균 1) · 강세 · 감정 비브라토
                var prog = _totalMs > 0 ? (float)(_elapsedMs / _totalMs) : 0;
                var f0 = baseF0 * (1.06f - 0.12f * prog) * (stressed ? 1.06f : 1f) * (1 + arousal * 0.08f * MathF.Sin((float)(2 * Math.PI * 3.1 * _elapsedMs / 1000)));
                _phase += f0 / SampleRate;
                if (_phase >= 1) _phase -= 1;
                // Rosenberg 성문 펄스(개방 0.4 · 폐쇄 0.16 · 폐쇄 시 기울기 불연속 → −6dB/oct 미분 스펙트럼)
                var gl = _phase < 0.4 ? 0.5f * (1 - MathF.Cos((float)(Math.PI * _phase / 0.4)))
                    : _phase < 0.56 ? MathF.Cos((float)(Math.PI / 2 * (_phase - 0.4) / 0.16)) : 0f;
                var src = voiced ? (gl - _prevG) * 6f : 0f;
                _prevG = gl;
                _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5;
                var noise = ((_rng & 0xFFFF) / 32768f - 1f) * 0.5f;
                var exc = (src * (1 - noiseMix) + noise * noiseMix) * _amp;
                // 포먼트 공진기(병렬) + 고정 F3
                var y1 = Biquad(exc, a1b1, a2b1, gb1, ref _b1z1, ref _b1z2) * (_f1 / 600f);   // 저 F1 에 몰리는 저역 조화음 에너지 보정
                var y2 = Biquad(exc, a1b2, a2b2, gb2, ref _b2z1, ref _b2z2);
                var y3 = Biquad(exc, a1b3, a2b3, gb3, ref _b3z1, ref _b3z2);
                var x = y1 + 0.8f * y2 + 0.5f * y3 + noise * noiseMix * _amp * 0.3f;
                // 기울기(밝기)
                // 2차 기울기 (1 − t·z⁻¹)²
                var dry = x - 2 * _tilt * _prevX + _tilt * _tilt * _prevX2;
                _prevX2 = _prevX; _prevX = x;
                // Schroeder 잔향
                float rev = 0;
                for (var c = 0; c < 4; c++)
                {
                    var buf = _comb[c]; var i = _combIdx[c];
                    var o = buf[i];
                    _combLp[c] = o * 0.8f + _combLp[c] * 0.2f;
                    buf[i] = dry + _combLp[c] * g[c];
                    _combIdx[c] = i + 1 == buf.Length ? 0 : i + 1;
                    rev += o;
                }
                rev *= 0.25f;
                for (var c = 0; c < 2; c++)
                {
                    var buf = _ap[c]; var i = _apIdx[c];
                    var d = buf[i];
                    var v = rev + 0.5f * d;
                    buf[i] = v;
                    rev = d - 0.5f * v;
                    _apIdx[c] = i + 1 == buf.Length ? 0 : i + 1;
                }
                output[n] = 0.3f * (dry * (1 - wet) + rev * wet * 1.6f);
                if (_phIdx < _phCount)
                {
                    _phPos += 1000.0 / SampleRate; _elapsedMs += 1000.0 / SampleRate;
                    if (_phPos >= _ph[_phIdx].DurationMs) { _phPos -= _ph[_phIdx].DurationMs; _phIdx++; }
                }
            }
            if (n > 0) _blocks++;
            _lastRenderMicros = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            return n;
        }
    }

    /// <summary>전체 발화 합성 — 신경망 결선 우선 · 폴백 DSP(블록 스트리밍). 기록 샘플 수(무할당).</summary>
    public int Synthesize(ReadOnlySpan<DubPhoneme> phonemes, in VoiceProfile profile, Span<float> output)
    {
        if (Neural is { } nb)
        {
            Span<float> tim = stackalloc float[Bands];
            CopyTimbre(tim);
            try
            {
                if (nb.TryRender(phonemes, profile, tim, output, out var w)) { lock (_gate) _neuralHits++; return w; }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException) { PromptFlowStudio.Program.Log("Localization.Voice.Neural", ex); }
        }
        Begin(phonemes, profile);
        var total = 0;
        while (total < output.Length)
        {
            var w = Render(output.Slice(total, Math.Min(Block, output.Length - total)));
            if (w == 0) break;
            total += w;
        }
        return total;
    }

    /// <summary>음색 전송 보정 — 기준 발화로 기울기 계수를 이분 탐색해 원 배우 밝기(스펙트럼 중심)에 정합. 최종 밝기 반환.</summary>
    public float CalibrateTimbre(in VoiceProfile reference)
    {
        Span<DubPhoneme> calib = stackalloc DubPhoneme[12];
        Viseme[] seq = CalibrationSeq;
        for (var i = 0; i < calib.Length; i++) calib[i] = new DubPhoneme(seq[i % seq.Length], seq[i % seq.Length] is Viseme.C or Viseme.MBP ? 60 : 130, seq[i % seq.Length] != Viseme.C, i % 4 == 0);
        Span<float> tim = stackalloc float[Bands];
        Span<float> refTim = stackalloc float[Bands];
        CopyTimbre(refTim);
        var savedTone = Tone;
        Tone = savedTone with { BrightnessTrim = 0 };
        float lo = -0.95f, hi = 0.95f, got = 0;
        var dry = reference with { Wet = 0, Rt60Ms = 0 };
        for (var it = 0; it < 9; it++)
        {
            var mid = (lo + hi) / 2;
            lock (_gate) _calibratedTilt = mid;
            var n = Synthesize(calib, dry, _calib);
            got = Analyze(_calib.AsSpan(0, n), tim).Brightness;
            if (got < reference.Brightness) lo = mid; else hi = mid;
        }
        lock (_gate) { _calibratedTilt = (lo + hi) / 2; _profile = reference; refTim.CopyTo(_timbre); }   // 보정 분석이 덮어쓴 기준 프로파일/음색 복원
        Tone = savedTone;
        LastAction = "🎚 음색 전송 보정 — 밝기 " + reference.Brightness.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "kHz 정합";
        Raise();
        return got;
    }

    private static readonly Viseme[] CalibrationSeq = { Viseme.A, Viseme.C, Viseme.E, Viseme.MBP, Viseme.I, Viseme.O, Viseme.C, Viseme.U };

    /// <summary>RBJ 대역통과(0dB 피크) 계수 — a1/a2 · 이득.</summary>
    private static void Coeffs(float f, float bw, out float a1, out float a2, out float g)
    {
        var w0 = 2 * MathF.PI * f / SampleRate;
        var alpha = MathF.Sin(w0) * MathF.Sinh(MathF.Log(2) / 2 * (bw / f) * w0 / MathF.Sin(w0));
        var a0 = 1 + alpha;
        a1 = -2 * MathF.Cos(w0) / a0; a2 = (1 - alpha) / a0; g = alpha / a0;
    }

    private static float Biquad(float x, float a1, float a2, float g, ref float z1, ref float z2)
    {
        // y = g·x − g·x[n−2] − a1·y[n−1] − a2·y[n−2] (전치 직접형 II)
        var y = g * x + z1;
        z1 = -a1 * y + z2;
        z2 = -g * x - a2 * y;
        return y;
    }

    /// <summary>음색 유사도(코사인) — 두 임베딩.</summary>
    public static float TimbreSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        float d = 0, na = 0, nb = 0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return na > 0 && nb > 0 ? d / MathF.Sqrt(na * nb) : 0;
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("Localization.Voice.OnChange", ex); }
    }
}
