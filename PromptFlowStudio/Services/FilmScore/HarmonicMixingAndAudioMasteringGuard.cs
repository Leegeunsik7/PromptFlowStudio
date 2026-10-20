using System.Numerics;
using PromptFlowStudio.Services.SpatialAudio;

namespace PromptFlowStudio.Services.FilmScore;

public enum LoudnessStandard : byte { EbuR128, AtscA85 }

/// <summary>
/// ITU-R BS.1770-4 라우드니스 미터(48 kHz · K 가중 2단 바이쿼드 · 100 ms 서브블록 · 무할당).
/// 모멘터리 400 ms · 숏텀 3 s · 통합 = 절대(−70) + 상대(−10 LU) 게이팅(0.1 LU 히스토그램 · 빈 에너지 합 정확).
/// </summary>
public sealed class LoudnessMeter
{
    public const int Channels = 12, SubBlock = 4800, ShortBlocks = 30, Bins = 750;
    public const float BinMin = -70f, BinStep = 0.1f;
    // 7.1.4: L R C LFE Ls Rs Lb Rb Ltf Rtf Ltb Rtb · LFE 제외 · 서라운드 1.41
    public static readonly float[] Weights = { 1f, 1f, 1f, 0f, 1.41f, 1.41f, 1.41f, 1.41f, 1f, 1f, 1f, 1f };
    private const double B10 = 1.53512485958697, B11 = -2.69169618940638, B12 = 1.19839281085285, A11 = -1.69065929318241, A12 = 0.73248077421585;
    private const double A21 = -1.99004745483398, A22 = 0.99007225036621;

    private readonly double[] _s = new double[Channels * 4];
    private readonly double[] _acc = new double[Channels];
    private readonly double[] _sub = new double[ShortBlocks];
    private readonly long[] _binN = new long[Bins];
    private readonly double[] _binE = new double[Bins];
    private int _subFill, _subHead, _subCount;

    public float Momentary { get; private set; } = -70f;
    public float ShortTerm { get; private set; } = -70f;
    public float Integrated { get; private set; } = -70f;
    public long GatedBlocks { get; private set; }
    public bool ShortValid => _subCount >= ShortBlocks;

    public static float ToLufs(double z) => z > 1e-12 ? (float)(-0.691 + 10.0 * Math.Log10(z)) : -100f;

    public void Reset()
    {
        Array.Clear(_s); Array.Clear(_acc); Array.Clear(_sub); Array.Clear(_binN); Array.Clear(_binE);
        _subFill = _subHead = _subCount = 0; Momentary = ShortTerm = Integrated = -70f; GatedBlocks = 0;
    }

    public void ResetIntegrated() { Array.Clear(_binN); Array.Clear(_binE); Integrated = -70f; GatedBlocks = 0; }

    /// <summary>평면 베드(채널 c = bed[c·stride ..]) n 샘플 계량.</summary>
    public void Process(ReadOnlySpan<float> bed, int stride, int n)
    {
        var i = 0;
        while (i < n)
        {
            var take = Math.Min(n - i, SubBlock - _subFill);
            for (var c = 0; c < Channels; c++)
            {
                if (Weights[c] == 0f) continue;
                var ch = bed.Slice(c * stride + i, take);
                double x1 = _s[c * 4], x2 = _s[c * 4 + 1], y1 = _s[c * 4 + 2], y2 = _s[c * 4 + 3], acc = _acc[c];
                // 2단을 직접형 II 전치 대신 상태 4개(중간 신호 w = 1단 출력)로 결합
                for (var k = 0; k < take; k++)
                {
                    double x = ch[k];
                    var w = B10 * x + x1;
                    x1 = B11 * x - A11 * w + x2;
                    x2 = B12 * x - A12 * w;
                    var y = w + y1;
                    y1 = -2.0 * w - A21 * y + y2;
                    y2 = w - A22 * y;
                    acc += y * y;
                }
                _s[c * 4] = x1; _s[c * 4 + 1] = x2; _s[c * 4 + 2] = y1; _s[c * 4 + 3] = y2; _acc[c] = acc;
            }
            _subFill += take; i += take;
            if (_subFill == SubBlock) CloseSubBlock();
        }
    }

    private void CloseSubBlock()
    {
        double z = 0;
        for (var c = 0; c < Channels; c++) { z += Weights[c] * _acc[c] / SubBlock; _acc[c] = 0; }
        _subFill = 0;
        _sub[_subHead] = z; _subHead = (_subHead + 1) % ShortBlocks; if (_subCount < ShortBlocks) _subCount++;
        double m = 0; var nm = Math.Min(4, _subCount);
        for (var k = 1; k <= nm; k++) m += _sub[(_subHead - k + ShortBlocks) % ShortBlocks];
        m /= nm;
        double s = 0;
        for (var k = 1; k <= _subCount; k++) s += _sub[(_subHead - k + ShortBlocks) % ShortBlocks];
        s /= _subCount;
        Momentary = ToLufs(m); ShortTerm = ToLufs(s);
        if (_subCount < 4) return;
        // 400 ms 블록(75% 겹침) → 히스토그램
        var l = ToLufs(m);
        if (l <= BinMin) return;
        var b = Math.Min(Bins - 1, (int)((l - BinMin) / BinStep));
        _binN[b]++; _binE[b] += m; GatedBlocks++;
        double e = 0; long cnt = 0;
        for (var k = 0; k < Bins; k++) { e += _binE[k]; cnt += _binN[k]; }
        if (cnt == 0) return;
        var rel = ToLufs(e / cnt) - 10f;
        e = 0; cnt = 0;
        for (var k = 0; k < Bins; k++)
        {
            if (BinMin + (k + 1) * BinStep <= rel) continue;
            e += _binE[k]; cnt += _binN[k];
        }
        Integrated = cnt > 0 ? ToLufs(e / cnt) : -70f;
    }
}

/// <summary>대사 가이드(임시 더빙) 합성: 성문 펄스(지터) → 3 포먼트 SVF(모음 순환) × 음절 포락(4.5 Hz) · 구 게이팅.</summary>
public sealed class TempDialogueSynth
{
    private static readonly float[] F1 = { 730f, 530f, 270f, 570f, 300f }, F2 = { 1090f, 1840f, 2290f, 840f, 870f }, F3 = { 2440f, 2480f, 3010f, 2410f, 2240f };
    private readonly float[] _buf = new float[NeuralCinematographicFilmScoreGenerator.Block];
    private float _ph, _t, _gate, _l1, _b1, _l2, _b2, _l3, _b3;
    private uint _r = 0x5EEDu;

    public bool Active { get; set; }
    public float Level { get; set; } = 0.25f;
    public float Pitch { get; set; } = 120f;
    public ReadOnlySpan<float> Buffer => _buf;

    public void Render()
    {
        const float dt = 1f / NeuralCinematographicFilmScoreGenerator.SampleRate;
        var k = 1f - MathF.Exp(-dt / 0.03f);
        for (var i = 0; i < _buf.Length; i++)
        {
            _t += dt;
            _gate += ((Active ? 1f : 0f) - _gate) * k;
            if (_gate < 1e-4f) { _buf[i] = 0f; continue; }
            _r ^= _r << 13; _r ^= _r >> 17; _r ^= _r << 5;
            var jit = 1f + 0.02f * ((_r & 0xFF) / 255f - 0.5f);
            var f0 = Pitch * (1f + 0.08f * MathF.Sin(MathF.Tau * 0.7f * _t)) * jit;
            _ph += f0 * dt; var pulse = 0f;
            if (_ph >= 1f) { _ph -= 1f; pulse = 1f; }
            var src = pulse - 0.002f + 0.05f * ((_r >> 8 & 0xFF) / 255f - 0.5f);
            var syl = _t * 4.5f; var v = (int)syl % 5; var sp = syl - MathF.Floor(syl);
            var env = 0.5f - 0.5f * MathF.Cos(MathF.Tau * sp);
            var y = Bp(src, F1[v], 0.12f, ref _l1, ref _b1) + 0.6f * Bp(src, F2[v], 0.1f, ref _l2, ref _b2) + 0.3f * Bp(src, F3[v], 0.08f, ref _l3, ref _b3);
            _buf[i] = y * env * _gate * Level * 4f;
        }
    }

    private static float Bp(float x, float f, float q, ref float lp, ref float bp)
    {
        var fc = 2f * MathF.Sin(MathF.PI * f / NeuralCinematographicFilmScoreGenerator.SampleRate);
        var hp = x - lp - q * bp; bp += fc * hp; lp += fc * bp;
        return bp;
    }
}

/// <summary>
/// [PromptFlow Studio v16.86.0] 🎚 하모닉 믹싱 &amp; 오디오 마스터링 다이내믹스 가드(48 kHz · 7.1.4 베드 · 무할당).
///   ▸ v16.76 채널 할당 가드: 대사 = C 전용(앵커) · 음악 스템 = C 배제 와이드 고정 팬(에너지 보존 Σg² = 1) · 폴리 = 청취자 기준 방위/고도 쌍 팬(수평 링 6 + 상층 링 4 · C 예약) · LFE = 타악/폭발 120 Hz 2단 LPF 센드.
///   ▸ 대사 명료도: 사이드체인 엔벨로프(10/250 ms) → 음악 더킹(기본 −9 dB) + 음성 대역(2.5 kHz) 다이내믹 EQ 딥 → 음성 대역 SNR 계측.
///   ▸ 라우드니스: BS.1770-4 프리 미터 → 정규화 AGC(EBU R128 −23 LUFS · ATSC A/85 −24 LKFS · 슬루 제한) → 4× 오버샘플 트루 피크 룩어헤드 리미터(EBU −1 · ATSC −2 dBTP) → 포스트 미터 · 준수 판정.
///   ▸ 출력: ITU 다운믹스 스테레오 · 선택적 v16.76 HRTF 바이노럴 가상화 · 최근 4 s 미리듣기 링(WAV 내보내기 = 제어 평면).
///   ▸ 계측: 스템 7 × 대역 10 Goertzel 스펙트럼 · 스템/채널 레벨 dBFS.
/// </summary>
public sealed class HarmonicMixingAndAudioMasteringGuard
{
    public const int Block = NeuralCinematographicFilmScoreGenerator.Block, Fs = NeuralCinematographicFilmScoreGenerator.SampleRate, Ch = 12, StemCount = 7, Bands = 10;
    public const int Lookahead = 48, TpTaps = 12, PreviewSeconds = 4;
    public static readonly float[] BandHz = { 63f, 125f, 250f, 500f, 1000f, 2000f, 4000f, 8000f, 12000f, 16000f };
    private static readonly int[] HRing = { 6, 4, 0, 1, 5, 7 }; // Lb Ls L R Rs Rb
    private static readonly int[] TRing = { 10, 8, 9, 11 };    // Ltb Ltf Rtf Rtb
    private static readonly float[] TpCoef = new float[3 * TpTaps];
    private static readonly float[] BandCoef = new float[Bands];

    private readonly float[][] _stemPan = new float[NeuralCinematographicFilmScoreGenerator.MusicStems][];
    private readonly float[] _bed = new float[Ch * Block], _gains = new float[Ch];
    private readonly float[] _music = new float[Block], _tmp = new float[Block];
    private readonly LoudnessMeter _pre = new(), _post = new();
    private readonly float[] _tpHist = new float[Ch * TpTaps];
    private readonly float[] _delay = new float[Ch * Lookahead], _req = new float[Lookahead];
    private int _dHead, _tpHead;
    private readonly float[] _eqL = new float[NeuralCinematographicFilmScoreGenerator.MusicStems], _eqB = new float[NeuralCinematographicFilmScoreGenerator.MusicStems];
    private float _lfe1, _lfe2, _dEnv, _duckGain = 1f, _eqDepth, _limGain = 1f, _normDb;
    private float _sdL, _sdB, _smL, _smB;
    private double _speechD, _speechM;
    private bool _normInit;
    private readonly float[] _spec = new float[StemCount * Bands], _stemDb = new float[StemCount], _chDb = new float[Ch];
    private readonly short[] _preview = new short[Fs * PreviewSeconds * 2];
    private int _pvHead;
    private bool _pvFull;
    private readonly float[] _outL = new float[Block], _outR = new float[Block];
    private readonly NeuralBinauralHrtfSpatializer? _hrtf;

    static HarmonicMixingAndAudioMasteringGuard()
    {
        // 4× 보간 폴리페이즈(위상 1..3 · 12탭 Hann 창 sinc)
        for (var p = 1; p <= 3; p++)
        {
            float sum = 0f;
            for (var j = 0; j < TpTaps; j++)
            {
                var x = j - (TpTaps / 2 - 1) - p / 4f;
                var s = MathF.Abs(x) < 1e-6f ? 1f : MathF.Sin(MathF.PI * x) / (MathF.PI * x);
                var w = 0.5f + 0.5f * MathF.Cos(MathF.PI * x / (TpTaps / 2f));
                TpCoef[(p - 1) * TpTaps + j] = s * w; sum += s * w;
            }
            for (var j = 0; j < TpTaps; j++) TpCoef[(p - 1) * TpTaps + j] /= sum;
        }
        for (var b = 0; b < Bands; b++) BandCoef[b] = 2f * MathF.Cos(MathF.Tau * BandHz[b] / Fs);
    }

    public HarmonicMixingAndAudioMasteringGuard(NeuralBinauralHrtfSpatializer? hrtf = null)
    {
        _hrtf = hrtf;
        float[][] raw =
        {
            new[] { 0.55f, 0.55f, 0f, 0f, 0.45f, 0.45f, 0f, 0f, 0f, 0f, 0f, 0f },
            new[] { 0.65f, 0.65f, 0f, 0f, 0.2f, 0.2f, 0f, 0f, 0f, 0f, 0f, 0f },
            new[] { 0.6f, 0.7f, 0f, 0f, 0.25f, 0.25f, 0f, 0f, 0f, 0f, 0f, 0f },
            new[] { 0.68f, 0.68f, 0f, 0f, 0.2f, 0.2f, 0f, 0f, 0f, 0f, 0f, 0f },
            new[] { 0.4f, 0.4f, 0f, 0f, 0.45f, 0.45f, 0.2f, 0.2f, 0.3f, 0.3f, 0.2f, 0.2f },
        };
        for (var s = 0; s < raw.Length; s++)
        {
            float e = 0f; foreach (var g in raw[s]) e += g * g;
            var k = 1f / MathF.Sqrt(e);
            _stemPan[s] = new float[Ch];
            for (var c = 0; c < Ch; c++) _stemPan[s][c] = raw[s][c] * k;
        }
        Dialogue = new TempDialogueSynth();
    }

    // ── 설정 ──
    public LoudnessStandard Standard { get; set; } = LoudnessStandard.EbuR128;
    public float TargetLufs => Standard == LoudnessStandard.EbuR128 ? -23f : -24f;
    public float CeilingDbtp => Standard == LoudnessStandard.EbuR128 ? -1f : -2f;
    public bool Normalize { get; set; } = true;
    public bool Limiter { get; set; } = true;
    public bool Ducking { get; set; } = true;
    public float DuckDepthDb { get; set; } = 9f;
    public float SpeechDipDb { get; set; } = 6f;
    public float ListenerYawDeg { get; set; }
    public bool Binaural { get; set; }
    public TempDialogueSynth Dialogue { get; }
    public readonly float[] StemFader = { 1f, 1f, 1f, 1f, 1f, 1f, 1f };

    // ── 계측 ──
    public LoudnessMeter Pre => _pre;
    public LoudnessMeter Post => _post;
    public float NormGainDb => _normDb;
    public float LimiterGrDb => -20f * MathF.Log10(MathF.Max(_limGain, 1e-6f));
    public float DuckGrDb => -20f * MathF.Log10(MathF.Max(_duckGain, 1e-6f));
    public float DialogueSnrDb { get; private set; }
    public float BlockTruePeakDbtp { get; private set; } = -100f;
    public float MaxTruePeakDbtp { get; private set; } = -100f;
    public float LfeHighBandRatio { get; private set; }
    public bool Compliant => _post.GatedBlocks > 30 && MathF.Abs(_post.Integrated - TargetLufs) <= 1f && MaxTruePeakDbtp <= CeilingDbtp + 0.2f;
    public long Blocks { get; private set; }
    public double LastProcessMs { get; private set; }
    public ReadOnlySpan<float> Bed => _bed;
    public ReadOnlySpan<float> BedChannel(int c) => _bed.AsSpan(c * Block, Block);
    public ReadOnlySpan<float> OutL => _outL;
    public ReadOnlySpan<float> OutR => _outR;
    public float Spectrum(Stem s, int band) => _spec[(int)s * Bands + band];
    public float StemDb(Stem s) => _stemDb[(int)s];
    public float ChannelDb(int c) => _chDb[c];

    public void ResetMeters() { _pre.Reset(); _post.Reset(); MaxTruePeakDbtp = -100f; _normInit = false; }

    private static float Db(double e) => e > 1e-12 ? (float)(10.0 * Math.Log10(e)) : -120f;

    /// <summary>방위(° +오른쪽) · 고도(°) → 12채널 에너지 보존 쌍 팬(C · LFE 제외).</summary>
    public static void PanDirection(float azDeg, float elDeg, Span<float> g)
    {
        g.Clear();
        var h = Math.Clamp(elDeg / 45f, 0f, 1f);
        float gh = MathF.Cos(h * MathF.PI * 0.5f), gt = MathF.Sin(h * MathF.PI * 0.5f);
        var az = MathF.IEEERemainder(azDeg, 360f);
        PanRing(az, HRing, g, gh);
        if (gt > 1e-4f) PanRing(az, TRing, g, gt);
    }

    private static void PanRing(float az, int[] ring, Span<float> g, float scale)
    {
        var n = ring.Length;
        for (var k = 0; k < n; k++)
        {
            int a = ring[k], b = ring[(k + 1) % n];
            var a0 = NeuralBinauralHrtfSpatializer.BedLayout[a].Az; var a1 = NeuralBinauralHrtfSpatializer.BedLayout[b].Az;
            if (a1 <= a0) a1 += 360f;
            var x = az; if (x < a0) x += 360f;
            if (x < a0 || x > a1) continue;
            var t = (x - a0) / (a1 - a0);
            g[a] += scale * MathF.Cos(t * MathF.PI * 0.5f); g[b] += scale * MathF.Sin(t * MathF.PI * 0.5f);
            return;
        }
    }

    private static void Goertzel(ReadOnlySpan<float> x, Span<float> dst, float smooth)
    {
        for (var b = 0; b < Bands; b++)
        {
            float s1 = 0f, s2 = 0f, c = BandCoef[b];
            for (var i = 0; i < x.Length; i++) { var s0 = x[i] + c * s1 - s2; s2 = s1; s1 = s0; }
            var p = (s1 * s1 + s2 * s2 - c * s1 * s2) / (x.Length * x.Length * 0.25f);
            var db = Db(p);
            dst[b] += (db - dst[b]) * smooth;
        }
    }

    private void AddToBed(ReadOnlySpan<float> mono, ReadOnlySpan<float> gains)
    {
        for (var c = 0; c < Ch; c++)
        {
            var g = gains[c];
            if (g == 0f) continue;
            AudioSimd.MulAdd(_bed.AsSpan(c * Block, Block), mono, g);
        }
    }

    /// <summary>1블록 믹스·마스터(무할당).</summary>
    public void Process(NeuralCinematographicFilmScoreGenerator score, AutonomousNeuralFoleySfxSynthesizer foley)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        const float dt = 1f / Fs;
        Array.Clear(_bed);
        Dialogue.Render();
        var dlg = Dialogue.Buffer;
        float aAtt = 1f - MathF.Exp(-dt / 0.01f), aRel = 1f - MathF.Exp(-dt / 0.25f);
        float speechF = 2f * MathF.Sin(MathF.PI * 2500f / Fs);

        // ── 대사 사이드체인 → 더킹 목표 · 음성 대역 에너지 ──
        double ed = 0;
        for (var i = 0; i < Block; i++)
        {
            var x = dlg[i] * StemFader[(int)Stem.Dialogue];
            var a = MathF.Abs(x);
            _dEnv += (a - _dEnv) * (a > _dEnv ? aAtt : aRel);
            var hp = x - _sdL - 0.7f * _sdB; _sdB += speechF * hp; _sdL += speechF * _sdB;
            ed += _sdB * _sdB;
        }
        var talking = _dEnv > 0.003f;
        var duckTarget = Ducking && talking ? MathF.Pow(10f, -DuckDepthDb / 20f) : 1f;
        var eqTarget = Ducking && talking ? 1f - MathF.Pow(10f, -SpeechDipDb / 20f) : 0f;
        var duckK = 1f - MathF.Exp(-(float)Block / Fs / (duckTarget < _duckGain ? 0.05f : 0.4f));
        var duck0 = _duckGain; _duckGain += (duckTarget - _duckGain) * duckK;
        var eq0 = _eqDepth; _eqDepth += (eqTarget - _eqDepth) * duckK;

        // ── 음악 스템: 페이더 × 더킹 램프 × 음성 대역 딥 → 고정 팬 · 대역 계측 ──
        Array.Clear(_music);
        for (var s = 0; s < NeuralCinematographicFilmScoreGenerator.MusicStems; s++)
        {
            var src = score.StemBuffer((Stem)s);
            var f = StemFader[s];
            float l = _eqL[s], b = _eqB[s];
            for (var i = 0; i < Block; i++)
            {
                var u = (float)i / Block;
                var x = src[i] * f;
                var hp = x - l - 1.4f * b; b += speechF * hp; l += speechF * b;
                _tmp[i] = (x - b * (eq0 + (_eqDepth - eq0) * u)) * (duck0 + (_duckGain - duck0) * u);
            }
            _eqL[s] = l; _eqB[s] = b;
            AddToBed(_tmp, _stemPan[s]);
            AudioSimd.MulAdd(_music, _tmp, 1f);
            Goertzel(_tmp, _spec.AsSpan(s * Bands, Bands), 0.3f);
            _stemDb[s] = Db(AudioSimd.SumSquares(_tmp) / Block);
        }
        double em = 0;
        for (var i = 0; i < Block; i++)
        {
            var hp = _music[i] - _smL - 0.7f * _smB; _smB += speechF * hp; _smL += speechF * _smB;
            em += _smB * _smB;
        }
        _speechD = _speechD * 0.9 + ed * 0.1; _speechM = _speechM * 0.9 + em * 0.1;
        DialogueSnrDb = _speechD > 1e-9 ? Db(_speechD / Math.Max(_speechM, 1e-12)) : 0f;

        // ── 대사 → C 전용 ──
        var cdl = _bed.AsSpan(2 * Block, Block);
        AudioSimd.MulAdd(cdl, dlg, StemFader[(int)Stem.Dialogue]);
        Goertzel(dlg, _spec.AsSpan((int)Stem.Dialogue * Bands, Bands), 0.3f);
        _stemDb[(int)Stem.Dialogue] = Db(AudioSimd.SumSquares(dlg) / Block * StemFader[(int)Stem.Dialogue] * StemFader[(int)Stem.Dialogue]);

        // ── 폴리: 청취자 기준 방향 팬 + LFE 센드 ──
        var lfe = _bed.AsSpan(3 * Block, Block);
        var perc = score.StemBuffer(Stem.Percussion);
        Array.Clear(_tmp);
        var ff = StemFader[(int)Stem.Foley];
        var yaw = ListenerYawDeg * MathF.PI / 180f;
        Span<float> g = _gains;
        double efol = 0;
        for (var v = 0; v < AutonomousNeuralFoleySfxSynthesizer.MaxVoices; v++)
        {
            if (!foley.RenderedThisBlock(v)) continue;
            var buf = foley.VoiceBuffer[v];
            var d = foley.VoicePosition(v) - foley.Listener;
            var az = (MathF.Atan2(d.X, d.Z) - yaw) * 180f / MathF.PI;
            var el = MathF.Asin(Math.Clamp(d.Y / MathF.Max(d.Length(), 1e-3f), -1f, 1f)) * 180f / MathF.PI;
            PanDirection(az, el, g);
            for (var c = 0; c < Ch; c++) g[c] *= ff;
            AddToBed(buf, g);
            var k = foley.VoiceKind(v);
            if (k is FoleyKind.Explosion or FoleyKind.Impact) AudioSimd.MulAdd(_tmp, buf, 0.7f * ff);
            efol += AudioSimd.SumSquares(buf);
        }
        {
            var d = foley.RustlePosition - foley.Listener;
            PanDirection((MathF.Atan2(d.X, d.Z) - yaw) * 180f / MathF.PI, 0f, g);
            for (var c = 0; c < Ch; c++) g[c] *= ff;
            AddToBed(foley.RustleBuffer, g);
            efol += AudioSimd.SumSquares(foley.RustleBuffer);
        }
        _stemDb[(int)Stem.Foley] = Db(efol / Block * ff * ff);
        AudioSimd.MulAdd(_tmp, perc, 0.5f * StemFader[(int)Stem.Percussion]);
        var lc = 1f - MathF.Exp(-MathF.Tau * 120f / Fs);
        double lfeE = 0, lfeHi = 0;
        float hiL = 0f;
        for (var i = 0; i < Block; i++)
        {
            _lfe1 += lc * (_tmp[i] - _lfe1); _lfe2 += lc * (_lfe1 - _lfe2);
            lfe[i] = _lfe2 * 0.5f;
            var hi = lfe[i] - hiL; hiL += 0.3f * hi;
            lfeE += lfe[i] * lfe[i]; lfeHi += hi * hi;
        }
        LfeHighBandRatio = lfeE > 1e-12 ? (float)(lfeHi / lfeE) : 0f;
        Goertzel(_bed.AsSpan(4 * Block, Block), _spec.AsSpan((int)Stem.Foley * Bands, Bands), 0.3f);

        // ── 라우드니스 정규화(프리 미터 숏텀 → 슬루 제한 AGC) ──
        _pre.Process(_bed, Block, Block);
        var norm0 = _normDb;
        if (Normalize && _pre.ShortTerm > -60f && _pre.Momentary > -60f)
        {
            var want = Math.Clamp(TargetLufs - _pre.ShortTerm, -30f, 20f);
            if (!_normInit && _pre.ShortValid) { _normDb = want; _normInit = true; }
            else if (_normInit) _normDb += Math.Clamp(want - _normDb, -6f * Block / Fs, 2f * Block / Fs);
        }
        else if (!Normalize) _normDb = 0f;
        float ga = MathF.Pow(10f, norm0 / 20f), gb = MathF.Pow(10f, _normDb / 20f);
        for (var c = 0; c < Ch; c++)
        {
            var ch = _bed.AsSpan(c * Block, Block);
            for (var i = 0; i < Block; i++) ch[i] *= ga + (gb - ga) * ((float)i / Block);
        }

        // ── 4× 트루 피크 룩어헤드 리미터(채널 연동) ──
        var ceil = MathF.Pow(10f, CeilingDbtp / 20f);
        var relK = 1f - MathF.Exp(-dt / 0.1f);
        var blockTp = 0f;
        for (var i = 0; i < Block; i++)
        {
            var pk = 0f;
            for (var c = 0; c < Ch; c++)
            {
                var x = _bed[c * Block + i];
                var hb = c * TpTaps;
                _tpHist[hb + _tpHead] = x;
                pk = MathF.Max(pk, MathF.Abs(x));
                for (var p = 0; p < 3; p++)
                {
                    float y = 0f; var cb = p * TpTaps;
                    for (var j = 0; j < TpTaps; j++) y += TpCoef[cb + j] * _tpHist[hb + (_tpHead - j + TpTaps) % TpTaps];
                    pk = MathF.Max(pk, MathF.Abs(y));
                }
            }
            _tpHead = (_tpHead + 1) % TpTaps;
            // 창 = [n − L, n](지연선 출력 표본 포함) → 덮어쓰기 전 최소 + 신규 요구
            var r = pk > ceil ? ceil / pk : 1f;
            var need = r;
            for (var k = 0; k < Lookahead; k++) need = MathF.Min(need, _req[k]);
            _req[_dHead] = r;
            _limGain = Limiter ? (need < _limGain ? need : _limGain + (need - _limGain) * relK) : 1f;
            for (var c = 0; c < Ch; c++)
            {
                ref var slot = ref _delay[c * Lookahead + _dHead];
                var o = slot; slot = _bed[c * Block + i];
                // 지연선 L(48) ≫ FIR 군지연(≈ 5) → 보간 피크가 출력 전에 창에 진입
                _bed[c * Block + i] = o * _limGain;
                blockTp = MathF.Max(blockTp, MathF.Abs(_bed[c * Block + i]));
            }
            _dHead = (_dHead + 1) % Lookahead;
        }
        BlockTruePeakDbtp = 20f * MathF.Log10(MathF.Max(blockTp, 1e-6f));
        _post.Process(_bed, Block, Block);
        for (var c = 0; c < Ch; c++) _chDb[c] = Db(AudioSimd.SumSquares(_bed.AsSpan(c * Block, Block)) / Block);
        MaxTruePeakDbtp = MathF.Max(MaxTruePeakDbtp, PostTruePeak());

        // ── 출력: 바이노럴(v16.76) 또는 ITU 다운믹스 → 미리듣기 링 ──
        Array.Clear(_outL); Array.Clear(_outR);
        if (Binaural && _hrtf is not null) _hrtf.VirtualizeBed(_bed, Block, Block, _outL, _outR);
        else
            for (var i = 0; i < Block; i++)
            {
                float L = _bed[i], R = _bed[Block + i], C = _bed[2 * Block + i], Ls = _bed[4 * Block + i], Rs = _bed[5 * Block + i], Lb = _bed[6 * Block + i], Rb = _bed[7 * Block + i];
                float T = 0.5f * (_bed[8 * Block + i] + _bed[10 * Block + i]), U = 0.5f * (_bed[9 * Block + i] + _bed[11 * Block + i]);
                _outL[i] = L + 0.707f * C + 0.707f * Ls + 0.5f * Lb + 0.5f * T;
                _outR[i] = R + 0.707f * C + 0.707f * Rs + 0.5f * Rb + 0.5f * U;
            }
        for (var i = 0; i < Block; i++)
        {
            _preview[_pvHead] = (short)Math.Clamp((int)(_outL[i] * 32767f), -32768, 32767);
            _preview[_pvHead + 1] = (short)Math.Clamp((int)(_outR[i] * 32767f), -32768, 32767);
            _pvHead += 2; if (_pvHead >= _preview.Length) { _pvHead = 0; _pvFull = true; }
        }
        Blocks++;
        LastProcessMs = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    private readonly float[] _ptHist = new float[Ch * TpTaps];
    private int _ptHead;

    /// <summary>출력 트루 피크(4× 보간 · dBTP).</summary>
    private float PostTruePeak()
    {
        var pk = 0f;
        for (var i = 0; i < Block; i++)
        {
            for (var c = 0; c < Ch; c++)
            {
                var hb = c * TpTaps;
                var x = _bed[c * Block + i];
                _ptHist[hb + _ptHead] = x;
                pk = MathF.Max(pk, MathF.Abs(x));
                for (var p = 0; p < 3; p++)
                {
                    float y = 0f; var cb = p * TpTaps;
                    for (var j = 0; j < TpTaps; j++) y += TpCoef[cb + j] * _ptHist[hb + (_ptHead - j + TpTaps) % TpTaps];
                    pk = MathF.Max(pk, MathF.Abs(y));
                }
            }
            _ptHead = (_ptHead + 1) % TpTaps;
        }
        return 20f * MathF.Log10(MathF.Max(pk, 1e-6f));
    }

    /// <summary>최근 4 s 스테레오 16-bit WAV(제어 평면 · 할당).</summary>
    public byte[] ExportPreviewWav()
    {
        var frames = (_pvFull ? _preview.Length : _pvHead) / 2;
        var bytes = new byte[44 + frames * 4];
        var s = bytes.AsSpan();
        "RIFF"u8.CopyTo(s); BitConverter.TryWriteBytes(s[4..], 36 + frames * 4); "WAVEfmt "u8.CopyTo(s[8..]);
        BitConverter.TryWriteBytes(s[16..], 16); BitConverter.TryWriteBytes(s[20..], (short)1); BitConverter.TryWriteBytes(s[22..], (short)2);
        BitConverter.TryWriteBytes(s[24..], Fs); BitConverter.TryWriteBytes(s[28..], Fs * 4); BitConverter.TryWriteBytes(s[32..], (short)4); BitConverter.TryWriteBytes(s[34..], (short)16);
        "data"u8.CopyTo(s[36..]); BitConverter.TryWriteBytes(s[40..], frames * 4);
        var start = _pvFull ? _pvHead : 0;
        for (var f = 0; f < frames * 2; f++)
        {
            var v = _preview[(start + f) % _preview.Length];
            BitConverter.TryWriteBytes(s[(44 + f * 2)..], v);
        }
        return bytes;
    }
}
