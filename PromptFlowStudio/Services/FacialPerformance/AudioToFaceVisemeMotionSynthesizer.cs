using PromptFlowStudio.Services.DigitalHuman;
using PromptFlowStudio.Services.Localization.Dubbing;
using PromptFlowStudio.Services.Sound;

namespace PromptFlowStudio.Services.FacialPerformance;

/// <summary>
/// [PromptFlow Studio v17.12.0] 🎙️ Audio2Face 음향 음소(Viseme) → 입 모양 합성기(<see cref="Push"/> 무할당 · 홉 10 ms · 홉당 연산 서브 ms).
///   ▸ 마이크 48 kHz 모노 → 링 버퍼 → 홉(480) 마다:
///     ① RMS dB · ZCR · 러닝 피크(−3 dB/s 감쇠) ② Hann 1024 FFT(<see cref="FftPlan"/>) → 파워 스펙트럼 → 스펙트럼 중심 · 26 멜 필터뱅크 → log → DCT-II 13 MFCC
///     ③ 4:1 박스카 데시메이션(12 kHz) → 프리엠퍼시스 0.9 · Hamming 30 ms → 자기상관 → Levinson-Durbin LPC 12차 → 20 Hz 간격 포락 |1/A(e^jω)| → 포먼트 F1 · F2 피크
///     ④ 분류: 무음(피크 −30 dB 미만) 160 ms 이내 = 양순 폐쇄(P/B/M → M) · 이후 Rest · 저역 비음(F1 &lt; 300 Hz · F2 &lt; 1.5 kHz · 중심 &lt; 900 Hz · 피크 −11 dB 미만) = M ·
///        고 ZCR + 고 중심(무음 판정보다 우선 · 피크 −45 dB 까지) = 순치 마찰(F) · 그 외 모음 = (log F1, log F2) 최근접 중심(A E I O U).
///   ▸ 비짐 → 입 블렌드셰이프 15(v16.89 립싱크 표 = v16.82 SetSpeech 동일) × 음량 진폭 → 공동조음 1차 평활(어택 25 ms / 릴리즈 60 ms).
/// </summary>
public sealed class AudioToFaceVisemeMotionSynthesizer
{
    public const int SampleRate = 48000, Hop = 480, Win = 1024, Bins = Win / 2 + 1, MelBands = 26, MfccCount = 13;
    public const int Dec = 4, DecRate = SampleRate / Dec, LpcOrder = 12, LpcWin = 360, EnvBins = 200, EnvStepHz = 20, Ring = 2048, History = 240, WaveLen = 2400;
    public const int Shapes = GenerativeNeuralLipSyncSynthesizer.Shapes;
    public static readonly float[] VowelF1 = { 730, 530, 270, 570, 300 }, VowelF2 = { 1090, 1840, 2290, 840, 870 };

    private readonly FftPlan _fft = new(Win);
    private readonly float[] _ring = new float[Ring], _wave = new float[WaveLen];
    private readonly float[] _re = new float[Win], _im = new float[Win], _hann = new float[Win], _pow = new float[Bins];
    private readonly float[] _melW = new float[MelBands * Bins], _mel = new float[MelBands], _dct = new float[MfccCount * MelBands], _mfcc = new float[MfccCount];
    private readonly int[] _melLo = new int[MelBands], _melHi = new int[MelBands];
    private readonly float[] _dec = new float[LpcWin], _ham = new float[LpcWin], _ac = new float[LpcOrder + 1], _lpc = new float[LpcOrder + 1], _tmp = new float[LpcOrder + 1];
    private readonly float[] _envCos = new float[EnvBins * (LpcOrder + 1)], _envSin = new float[EnvBins * (LpcOrder + 1)], _env = new float[EnvBins];
    private readonly float[] _target = new float[Shapes], _w = new float[Shapes];
    private readonly byte[] _visHist = new byte[History];
    private readonly float[] _f1Hist = new float[History], _f2Hist = new float[History], _dbHist = new float[History];
    private int _ringPos, _pending, _waveHead, _histHead, _histCount;
    private long _samples;
    private float _peakDb = -60f;
    private int _silentHops, _sinceVoiced = 1000;

    public AudioToFaceVisemeMotionSynthesizer()
    {
        for (var i = 0; i < Win; i++) _hann[i] = 0.5f - 0.5f * MathF.Cos(MathF.Tau * i / Win);
        for (var i = 0; i < LpcWin; i++) _ham[i] = 0.54f - 0.46f * MathF.Cos(MathF.Tau * i / (LpcWin - 1));
        static double Mel(double f) => 2595 * Math.Log10(1 + f / 700);
        static double Hz(double m) => 700 * (Math.Pow(10, m / 2595) - 1);
        double m0 = Mel(80), m1 = Mel(7600);
        Span<double> edge = stackalloc double[MelBands + 2];
        for (var b = 0; b < MelBands + 2; b++) edge[b] = Hz(m0 + (m1 - m0) * b / (MelBands + 1)) * Win / SampleRate;
        for (var b = 0; b < MelBands; b++)
        {
            double lo = edge[b], c = edge[b + 1], hi = edge[b + 2];
            _melLo[b] = (int)Math.Ceiling(lo); _melHi[b] = Math.Min(Bins - 1, (int)Math.Floor(hi));
            for (var k = _melLo[b]; k <= _melHi[b]; k++)
                _melW[b * Bins + k] = (float)(k <= c ? (k - lo) / (c - lo) : (hi - k) / (hi - c));
        }
        for (var n = 0; n < MfccCount; n++)
            for (var b = 0; b < MelBands; b++)
                _dct[n * MelBands + b] = (float)Math.Cos(Math.PI * n * (b + 0.5) / MelBands);
        for (var f = 0; f < EnvBins; f++)
            for (var k = 0; k <= LpcOrder; k++)
            {
                var om = 2 * Math.PI * (f * EnvStepHz) / DecRate * k;
                _envCos[f * (LpcOrder + 1) + k] = (float)Math.Cos(om); _envSin[f * (LpcOrder + 1) + k] = (float)Math.Sin(om);
            }
    }

    public float Articulation { get; set; } = 1f;
    public float AttackMs { get; set; } = 25f;
    public float ReleaseMs { get; set; } = 60f;
    public float SilenceDb { get; set; } = 30f;
    public Viseme Viseme { get; private set; }
    public float RmsDb { get; private set; } = -120f;
    public float PeakDb => _peakDb;
    public float Zcr { get; private set; }
    public float CentroidHz { get; private set; }
    public float F1 { get; private set; }
    public float F2 { get; private set; }
    public float Amplitude { get; private set; }
    public long Hops { get; private set; }
    public long Samples => _samples;
    public double LastHopUs { get; private set; }
    public double MaxHopUs { get; private set; }
    /// <summary>홉 연산 시간 EMA(µs · 스케줄러 지터 완화).</summary>
    public double AvgHopUs { get; private set; }
    public void ResetTiming() { MaxHopUs = 0; AvgHopUs = LastHopUs; }
    /// <summary>알고리즘 지연(ms) = 분석 창 중심까지 + 홉 대기 상한.</summary>
    public float AlgorithmicLatencyMs => 1000f * (Win / 2 + Hop) / SampleRate;
    public float Mfcc(int n) => _mfcc[n];
    public float Envelope(int bin) => _env[bin];
    public float Weight(int s) => _w[s];
    public float Target(int s) => _target[s];
    public int HistoryCount => _histCount;
    private int H(int back) => (_histHead - 1 - back + 2 * History) % History;
    public Viseme VisemeHistory(int back) => (Viseme)_visHist[H(back)];
    public float F1History(int back) => _f1Hist[H(back)];
    public float F2History(int back) => _f2Hist[H(back)];
    public float DbHistory(int back) => _dbHist[H(back)];
    /// <summary>최근 파형(back = 0 최신).</summary>
    public float WaveSample(int back) => _wave[(_waveHead - 1 - back + 2 * WaveLen) % WaveLen];

    /// <summary>52 ARKit 배열에 입 셰이프 기록(나머지 미접촉).</summary>
    public void WriteMouth(Span<float> w52)
    {
        for (var s = 0; s < Shapes; s++) w52[(int)GenerativeNeuralLipSyncSynthesizer.Mouth[s]] = _w[s];
    }

    public void Reset()
    {
        Array.Clear(_ring); Array.Clear(_w); Array.Clear(_target); Array.Clear(_wave);
        _ringPos = _pending = _histCount = 0; _peakDb = -60f; _silentHops = 0; _sinceVoiced = 1000;
        Viseme = Viseme.Rest;
    }

    /// <summary>마이크 샘플 투입(홉 단위 분석 · 무할당).</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
        {
            _ring[_ringPos] = samples[i];
            _ringPos = (_ringPos + 1) & (Ring - 1);
            _wave[_waveHead] = samples[i];
            _waveHead = (_waveHead + 1) % WaveLen;
            _samples++;
            if (++_pending == Hop) { _pending = 0; AnalyzeHop(); }
        }
    }

    private float Back(int n) => _ring[(_ringPos - 1 - n + 2 * Ring) & (Ring - 1)];

    private void AnalyzeHop()
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        // ① 에너지 · ZCR
        double e = 0; var zc = 0;
        var prev = Back(Hop);
        for (var n = Hop - 1; n >= 0; n--)
        {
            var x = Back(n);
            e += x * x;
            if ((x >= 0) != (prev >= 0)) zc++;
            prev = x;
        }
        RmsDb = (float)(10 * Math.Log10(e / Hop + 1e-12));
        Zcr = zc / (float)Hop;
        _peakDb = MathF.Max(_peakDb - 0.03f, RmsDb);
        // ② FFT · 멜 · MFCC
        for (var i = 0; i < Win; i++) { _re[i] = Back(Win - 1 - i) * _hann[i]; _im[i] = 0f; }
        _fft.Transform(_re, _im, false);
        double ps = 0, pc = 0;
        for (var k = 0; k < Bins; k++)
        {
            var p = _re[k] * _re[k] + _im[k] * _im[k];
            _pow[k] = p; ps += p; pc += p * k;
        }
        CentroidHz = ps > 0 ? (float)(pc / ps * SampleRate / Win) : 0f;
        for (var b = 0; b < MelBands; b++)
        {
            var acc = 1e-10f;
            for (var k = _melLo[b]; k <= _melHi[b]; k++) acc += _melW[b * Bins + k] * _pow[k];
            _mel[b] = MathF.Log(acc);
        }
        for (var n = 0; n < MfccCount; n++)
        {
            var acc = 0f;
            for (var b = 0; b < MelBands; b++) acc += _dct[n * MelBands + b] * _mel[b];
            _mfcc[n] = acc;
        }
        // ③ LPC 포먼트(12 kHz)
        var prevD = 0f;
        for (var i = 0; i < LpcWin; i++)
        {
            var o = (LpcWin - 1 - i) * Dec;
            var d = 0.25f * (Back(o) + Back(o + 1) + Back(o + 2) + Back(o + 3));
            _dec[i] = (d - 0.9f * prevD) * _ham[i];
            prevD = d;
        }
        for (var k = 0; k <= LpcOrder; k++)
        {
            var acc = 0f;
            for (var i = k; i < LpcWin; i++) acc += _dec[i] * _dec[i - k];
            _ac[k] = acc;
        }
        Levinson();
        Formants();
        // ④ 분류
        var fric = Zcr > 0.2f && CentroidHz > 2500f && RmsDb > _peakDb - 45f && RmsDb > -75f;
        var silent = !fric && (RmsDb < _peakDb - SilenceDb || RmsDb < -70f);
        Viseme v;
        if (fric) { _silentHops = 0; v = Viseme.F; }
        else if (silent)
        {
            _silentHops++;
            v = _sinceVoiced * 10 < 400 && _silentHops * 10 <= 160 ? Viseme.M : Viseme.Rest;
        }
        else
        {
            _silentHops = 0;
            if (F1 < 300f && F2 < 1500f && CentroidHz < 900f && RmsDb < _peakDb - 11f) v = Viseme.M;
            else v = NearestVowel(F1, F2);
            _sinceVoiced = 0;
        }
        if (silent || fric) _sinceVoiced++;
        Viseme = v;
        Amplitude = v is Viseme.M or Viseme.F ? 1f : v == Viseme.Rest ? 0f : Math.Clamp((RmsDb - (_peakDb - SilenceDb)) / 20f, 0.25f, 1f);
        // ⑤ 비짐 → 입 셰이프 + 공동조음
        float ka = 1f - MathF.Exp(-1000f * Hop / SampleRate / AttackMs), kr = 1f - MathF.Exp(-1000f * Hop / SampleRate / ReleaseMs);
        for (var s = 0; s < Shapes; s++)
        {
            var t = Math.Clamp(GenerativeNeuralLipSyncSynthesizer.TargetWeight(v, s) * Amplitude * Articulation, 0f, 1f);
            _target[s] = t;
            _w[s] += (t - _w[s]) * (t > _w[s] ? ka : kr);
        }
        _visHist[_histHead] = (byte)v; _f1Hist[_histHead] = F1; _f2Hist[_histHead] = F2; _dbHist[_histHead] = RmsDb;
        _histHead = (_histHead + 1) % History;
        if (_histCount < History) _histCount++;
        Hops++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastHopUs = us;
        if (Hops > 4 && us > MaxHopUs) MaxHopUs = us;
        AvgHopUs = Hops <= 1 ? us : AvgHopUs + 0.05 * (us - AvgHopUs);
    }

    private void Levinson()
    {
        Array.Clear(_lpc);
        _lpc[0] = 1f;
        var err = _ac[0] * 1.0001f + 1e-12f;
        for (var i = 1; i <= LpcOrder; i++)
        {
            var acc = _ac[i];
            for (var j = 1; j < i; j++) acc += _lpc[j] * _ac[i - j];
            var k = -acc / err;
            for (var j = 0; j <= i; j++) _tmp[j] = _lpc[j];
            for (var j = 1; j < i; j++) _lpc[j] = _tmp[j] + k * _tmp[i - j];
            _lpc[i] = k;
            err *= 1f - k * k;
            if (err <= 1e-12f) break;
        }
    }

    private void Formants()
    {
        for (var f = 0; f < EnvBins; f++)
        {
            float re = 0, im = 0;
            var o = f * (LpcOrder + 1);
            for (var k = 0; k <= LpcOrder; k++) { re += _lpc[k] * _envCos[o + k]; im -= _lpc[k] * _envSin[o + k]; }
            _env[f] = -10f * MathF.Log10(re * re + im * im + 1e-12f);
        }
        float f1 = 0, f2 = 0;
        for (var f = 1; f < EnvBins - 1; f++)
        {
            if (!(_env[f] > _env[f - 1] && _env[f] >= _env[f + 1])) continue;
            var hz = f * EnvStepHz;
            if (f1 == 0) { if (hz >= 200 && hz <= 1000) f1 = hz; continue; }
            if (hz > f1 + 150 && hz >= 600 && hz <= 3000) { f2 = hz; break; }
        }
        F1 = f1; F2 = f2 > 0 ? f2 : f1 * 1.6f;
    }

    public static Viseme NearestVowel(float f1, float f2)
    {
        if (f1 <= 0) return Viseme.A;
        float l1 = MathF.Log(f1), l2 = MathF.Log(MathF.Max(f2, 1f));
        var best = 0; var bd = float.MaxValue;
        for (var v = 0; v < 5; v++)
        {
            float d1 = l1 - MathF.Log(VowelF1[v]), d2 = l2 - MathF.Log(VowelF2[v]);
            var d = d1 * d1 + d2 * d2;
            if (d < bd) { bd = d; best = v; }
        }
        return (Viseme)(best + 1);
    }
}

public enum SpeechPhone : byte { Rest, A, E, I, O, U, M, P, B, F }

/// <summary>
/// 결정적 포먼트 음성 합성기(마이크 입력 모사 · 무할당): 성문 펄스(지터 · 기울기) → 3단 2극 공명기(F1 · F2 · F3) · 비음(저 F1 · 감쇠) · 파열(폐쇄 + 버스트) · 마찰(고역 잡음).
/// </summary>
public sealed class FormantSpeechSynthesizer
{
    public const int SampleRate = AudioToFaceVisemeMotionSynthesizer.SampleRate;
    private static readonly (SpeechPhone P, int Ms)[] Script =
    {
        (SpeechPhone.A, 220), (SpeechPhone.M, 90), (SpeechPhone.A, 200), (SpeechPhone.E, 220), (SpeechPhone.Rest, 150), (SpeechPhone.I, 200), (SpeechPhone.P, 80), (SpeechPhone.I, 200),
        (SpeechPhone.O, 220), (SpeechPhone.B, 70), (SpeechPhone.O, 200), (SpeechPhone.U, 220), (SpeechPhone.F, 130), (SpeechPhone.U, 200), (SpeechPhone.Rest, 300),
        (SpeechPhone.A, 180), (SpeechPhone.E, 180), (SpeechPhone.M, 100), (SpeechPhone.I, 180), (SpeechPhone.O, 180), (SpeechPhone.U, 220), (SpeechPhone.Rest, 400),
    };
    public static readonly int ScriptMs = TotalMs();

    private float _phase, _tilt, _amp, _f1 = 500, _f2 = 1500, _hp;
    private float _y11, _y12, _y21, _y22, _y31, _y32;
    private uint _rng = 0xC0FFEEu;
    private long _n;

    public bool Loop { get; set; } = true;
    /// <summary>단일 음소 고정(null = 스크립트).</summary>
    public SpeechPhone? Hold { get; set; }
    public float Gain { get; set; } = 1f;
    public long SampleIndex => _n;
    public SpeechPhone Current => Hold ?? PhoneAt(_n * 1000.0 / SampleRate);

    private static int TotalMs()
    {
        var t = 0;
        foreach (var s in Script) t += s.Ms;
        return t;
    }

    public static SpeechPhone PhoneAt(double ms)
    {
        var t = ms % ScriptMs;
        if (t < 0) t += ScriptMs;
        foreach (var s in Script) { if (t < s.Ms) return s.P; t -= s.Ms; }
        return SpeechPhone.Rest;
    }

    /// <summary>음소 경계까지 거리(ms).</summary>
    public static double BoundaryDistanceMs(double ms)
    {
        var t = ms % ScriptMs;
        if (t < 0) t += ScriptMs;
        foreach (var s in Script) { if (t < s.Ms) return Math.Min(t, s.Ms - t); t -= s.Ms; }
        return 0;
    }

    private static double PhoneStartMs(double ms)
    {
        var t = ms % ScriptMs; var b = ms - t;
        foreach (var s in Script) { if (t < s.Ms) return b; t -= s.Ms; b += s.Ms; }
        return b;
    }

    public static Viseme VisemeOf(SpeechPhone p) => p switch
    {
        SpeechPhone.A => Viseme.A, SpeechPhone.E => Viseme.E, SpeechPhone.I => Viseme.I, SpeechPhone.O => Viseme.O, SpeechPhone.U => Viseme.U,
        SpeechPhone.M or SpeechPhone.P or SpeechPhone.B => Viseme.M, SpeechPhone.F => Viseme.F, _ => Viseme.Rest,
    };

    private float Noise()
    {
        var x = _rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _rng = x;
        return (x >> 8) * (2f / 16777216f) - 1f;
    }

    private static void Coef(float f, float bw, out float a1, out float a2, out float b0)
    {
        var r = MathF.Exp(-MathF.PI * bw / SampleRate);
        a1 = 2f * r * MathF.Cos(MathF.Tau * f / SampleRate); a2 = -r * r; b0 = 1f - a1 - a2;
    }

    public void Generate(Span<float> dst)
    {
        const float kS = 1f - 0.9979f;   // ≈ 10 ms 평활
        for (var i = 0; i < dst.Length; i++, _n++)
        {
            var ms = _n * 1000.0 / SampleRate;
            var p = Hold ?? PhoneAt(ms);
            float tf1, tf2, ta, voiced = 1f, noise = 0f;
            switch (p)
            {
                case SpeechPhone.A: case SpeechPhone.E: case SpeechPhone.I: case SpeechPhone.O: case SpeechPhone.U:
                    tf1 = AudioToFaceVisemeMotionSynthesizer.VowelF1[(int)p - 1]; tf2 = AudioToFaceVisemeMotionSynthesizer.VowelF2[(int)p - 1]; ta = 1f; break;
                case SpeechPhone.M: tf1 = 250; tf2 = 1100; ta = 0.22f; break;
                case SpeechPhone.B: tf1 = 220; tf2 = 900; ta = 0.15f; break;
                case SpeechPhone.P:
                {
                    tf1 = _f1; tf2 = _f2; ta = 0f; voiced = 0f;
                    var into = Hold.HasValue ? 0 : ms - PhoneStartMs(ms);
                    if (into > 65) { noise = 0.35f; ta = 1f; }
                    break;
                }
                case SpeechPhone.F: tf1 = _f1; tf2 = _f2; ta = 1f; voiced = 0f; noise = 0.12f; break;
                default: tf1 = _f1; tf2 = _f2; ta = 0f; voiced = 0f; break;
            }
            _amp += (ta - _amp) * kS;
            _f1 += (tf1 - _f1) * kS * 0.5f; _f2 += (tf2 - _f2) * kS * 0.5f;
            var f0 = 115f + 10f * MathF.Sin((float)(ms * 0.0007));
            _phase += f0 / SampleRate * (1f + 0.01f * Noise());
            var src = 0f;
            if (_phase >= 1f) { _phase -= 1f; src = 8f; }
            _tilt = 0.9f * _tilt + src;
            var x = _tilt * voiced;
            Coef(_f1, p is SpeechPhone.M or SpeechPhone.B ? 120f : 80f, out var a1, out var a2, out var b0);
            var y1 = b0 * x + a1 * _y11 + a2 * _y12; _y12 = _y11; _y11 = y1;
            Coef(_f2, p is SpeechPhone.M or SpeechPhone.B ? 400f : 100f, out a1, out a2, out b0);
            var y2 = b0 * y1 + a1 * _y21 + a2 * _y22; _y22 = _y21; _y21 = y2;
            Coef(2500f, 150f, out a1, out a2, out b0);
            var y3 = b0 * y2 + a1 * _y31 + a2 * _y32; _y32 = _y31; _y31 = y3;
            var nz = Noise(); var hp = nz - _hp; _hp = nz;
            dst[i] = Gain * (_amp * (0.5f * y3 + 0.5f * y2 + noise * hp) + 1e-4f * nz);
        }
    }

    public void Reset() { _n = 0; _phase = _tilt = _amp = 0; _y11 = _y12 = _y21 = _y22 = _y31 = _y32 = 0; }
}
