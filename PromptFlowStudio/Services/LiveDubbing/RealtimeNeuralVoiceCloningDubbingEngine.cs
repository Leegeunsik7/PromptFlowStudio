using PromptFlowStudio.Services.Localization.Dubbing;

namespace PromptFlowStudio.Services.LiveDubbing;

/// <summary>립싱크 음소 클래스(입모양 기준 16종 — 라틴/로마자 · 한글 G2P 공통).</summary>
public enum LipPhoneme : byte { Rest = 0, A, E, I, O, U, Pbm, Fv, Th, L, Sz, Kg, Td, R, W, N }

/// <summary>
/// 화자 특징 임베딩(d-vector 32D) 추출기(무할당 스트리밍).
///   ▸ 24 kHz 2× 데시메이션 → 로그 간격 대역통과 24밴드(120 Hz~7 kHz · Q 3) 홉(10 ms) 에너지 → 활성 홉 로그 평균(EMA).
///   ▸ 12 kHz 4× 데시메이션 정규 자기상관 피치(40 ms 창 · 70~400 Hz · 첫 국소 최댓값 우선 = 옥타브 오류 억제) → 로그 F0 EMA.
///   ▸ 임베딩 = [밴드 로그 평균 − 평균] × 0.35 ‖ 로그 F0 가우시안 범프 8개(σ 0.28 옥타브) × 1.6 → L2 정규화.
/// </summary>
public sealed class SpeakerEmbeddingExtractor
{
    public const int Fs = 48000, Hop = 480, Bands = 24, Bumps = 8, Dim = Bands + Bumps, PitchLen = 480, PitchRing = 512, MinLag = 30, MaxLag = 171;

    private static readonly float[] Center = BuildCenters();
    private readonly float[] _b0 = new float[Bands], _a1 = new float[Bands], _a2 = new float[Bands];
    private readonly float[] _s1 = new float[Bands], _s2 = new float[Bands], _hopE = new float[Bands], _logMean = new float[Bands];
    private readonly float[] _pRing = new float[PitchRing], _pBuf = new float[PitchLen], _cum = new float[PitchLen + 1], _r = new float[MaxLag + 2];
    private int _hopN, _decN, _pHead, _pFill;
    private float _decAcc, _pAcc, _hopSq, _hopDiff, _prevX, _peak = 1e-4f;
    private float _logF0 = MathF.Log2(120f), _centroid = 1000f, _hi = 0.1f;

    public SpeakerEmbeddingExtractor()
    {
        for (var b = 0; b < Bands; b++)
        {
            var w = 2f * MathF.PI * Center[b] / (Fs / 2f);
            var alpha = MathF.Sin(w) / (2f * 3f);
            var a0 = 1f + alpha;
            _b0[b] = alpha / a0; _a1[b] = -2f * MathF.Cos(w) / a0; _a2[b] = (1f - alpha) / a0;
        }
    }

    public int ActiveHops { get; private set; }
    public int VoicedHops { get; private set; }
    public bool Valid => ActiveHops >= 10 && VoicedHops >= 5;
    public float F0Hz => MathF.Pow(2f, _logF0);
    public float Centroid => _centroid;
    public float HiRatio => _hi;
    public float LastRms { get; private set; }
    /// <summary>마지막 소비 이후 활성 홉 갱신 여부(적응 루프 트리거).</summary>
    public bool Updated { get; private set; }

    public bool TakeUpdated() { var u = Updated; Updated = false; return u; }

    private static float[] BuildCenters()
    {
        var c = new float[Bands];
        for (var b = 0; b < Bands; b++) c[b] = 120f * MathF.Pow(7000f / 120f, b / (Bands - 1f));
        return c;
    }

    public static float BandCenter(int b) => Center[b];

    public void Reset()
    {
        Array.Clear(_s1); Array.Clear(_s2); Array.Clear(_hopE); Array.Clear(_logMean); Array.Clear(_pRing);
        _hopN = _decN = _pHead = _pFill = 0; _decAcc = _pAcc = _hopSq = _hopDiff = _prevX = 0f; _peak = 1e-4f;
        _logF0 = MathF.Log2(120f); _centroid = 1000f; _hi = 0.1f;
        ActiveHops = VoicedHops = 0; Updated = false; LastRms = 0f;
    }

    /// <summary>PCM 48 kHz 스트림 투입(샘플 순회 · 할당 없음).</summary>
    public void Push(ReadOnlySpan<float> x)
    {
        for (var i = 0; i < x.Length; i++)
        {
            var s = x[i];
            _hopSq += s * s;
            var d = s - _prevX; _hopDiff += d * d; _prevX = s;
            _decAcc += s;
            if ((++_decN & 1) == 0)
            {
                var y = _decAcc * 0.5f; _decAcc = 0f;
                for (var b = 0; b < Bands; b++)
                {
                    var o = _b0[b] * y + _s1[b];
                    _s1[b] = -_a1[b] * o + _s2[b];
                    _s2[b] = -_b0[b] * y - _a2[b] * o;
                    _hopE[b] += o * o;
                }
            }
            _pAcc += s;
            if ((_decN & 3) == 0)
            {
                _pRing[_pHead] = _pAcc * 0.25f; _pAcc = 0f;
                _pHead = (_pHead + 1) & (PitchRing - 1);
                if (_pFill < PitchRing) _pFill++;
            }
            if (++_hopN == Hop) EndHop();
        }
    }

    private void EndHop()
    {
        _hopN = 0;
        var rms = MathF.Sqrt(_hopSq / Hop);
        LastRms = rms;
        _peak = MathF.Max(rms, _peak * 0.998f);
        if (rms > 1e-4f && rms > 0.12f * _peak)
        {
            var k = ActiveHops < 50 ? 1f / (ActiveHops + 1) : 0.03f;
            float tot = 0f, cen = 0f, hi = 0f;
            for (var b = 0; b < Bands; b++)
            {
                var e = _hopE[b];
                _logMean[b] += k * (MathF.Log(e + 1e-9f) - _logMean[b]);
                tot += e; cen += e * Center[b];
                if (Center[b] > 3000f) hi += e;
            }
            if (tot > 0f) { _centroid += k * (cen / tot - _centroid); _hi += k * (hi / tot - _hi); }
            ActiveHops++;
            var f0 = EstimatePitch();
            if (f0 > 0f)
            {
                var kp = VoicedHops < 30 ? 1f / (VoicedHops + 1) : 0.04f;
                _logF0 += kp * (MathF.Log2(f0) - _logF0);
                VoicedHops++;
            }
            Updated = true;
        }
        Array.Clear(_hopE);
        _hopSq = _hopDiff = 0f;
    }

    private float EstimatePitch()
    {
        if (_pFill < PitchLen) return 0f;
        var start = (_pHead - PitchLen + PitchRing) & (PitchRing - 1);
        for (var i = 0; i < PitchLen; i++) _pBuf[i] = _pRing[(start + i) & (PitchRing - 1)];
        return AutocorrPitch(_pBuf, Fs / 4, _cum, _r);
    }

    /// <summary>정규 자기상관 피치(SIMD 내적 · 누적 에너지 · 첫 국소 최댓값 ≥ 0.85 × 최대 우선). 무성 = 0. r 길이 ≥ maxLag + 2.</summary>
    public static float AutocorrPitch(ReadOnlySpan<float> x, int fs, Span<float> cum, Span<float> r)
    {
        int minLag = Math.Max(2, fs / 400), maxLag = Math.Min(x.Length / 2, fs / 70);
        cum[0] = 0f;
        for (var i = 0; i < x.Length; i++) cum[i + 1] = cum[i] + x[i] * x[i];
        var e0 = cum[x.Length];
        if (e0 < 1e-8f) return 0f;
        float best = 0f; var bestLag = 0;
        for (var l = minLag; l <= maxLag + 1; l++)
        {
            var n = x.Length - l;
            float ea = cum[n], eb = e0 - cum[l];
            var v = DubSimd.Dot(x[..n], x[l..]) / MathF.Sqrt(ea * eb + 1e-12f);
            r[l] = v;
            if (l <= maxLag && v > best) { best = v; bestLag = l; }
        }
        if (best < 0.55f) return 0f;
        for (var l = minLag + 1; l < bestLag; l++)
            if (r[l] >= 0.85f * best && r[l] >= r[l - 1] && r[l] >= r[l + 1]) { bestLag = l; break; }
        return (float)fs / bestLag;
    }

    /// <summary>d-vector(L2 정규화) 기록.</summary>
    public void Embedding(Span<float> e)
    {
        var mean = 0f;
        for (var b = 0; b < Bands; b++) mean += _logMean[b];
        mean /= Bands;
        for (var b = 0; b < Bands; b++) e[b] = (_logMean[b] - mean) * 0.35f;
        float lo = MathF.Log2(75f), hi = MathF.Log2(420f);
        for (var j = 0; j < Bumps; j++)
        {
            var c = lo + (hi - lo) * j / (Bumps - 1f);
            var z = (_logF0 - c) / 0.28f;
            e[Bands + j] = VoicedHops > 0 ? 1.6f * MathF.Exp(-z * z) : 0f;
        }
        var n = MathF.Sqrt(DubSimd.Dot(e[..Dim], e[..Dim]));
        if (n > 1e-9f) for (var i = 0; i < Dim; i++) e[i] /= n;
    }
}

/// <summary>
/// [PromptFlow Studio v17.32.0] 🗣️ 실시간 음색 보존 신경망 다국어 동시 더빙 엔진(48 kHz · 10 ms 홉 · 레인 4 · 렌더 무할당).
///   ▸ 음색 클로닝: v17.18 액터 음성 스트림 → <see cref="SpeakerEmbeddingExtractor"/> d-vector · F0 · 스펙트럼 중심 · 고역비.
///     레인별 출력 재분석 → 폐루프 적응(F0 ×(원본/출력)^0.08 · ±20% 클램프 · 성도 배율 (F0/120)^0.35 · 기식 +0.02·Δ고역) → 화자 고유 톤 유지.
///   ▸ 번역 토큰(로마자 · 한글) → 16 립 음소 G2P(이중자 th/sh/ch/ph/ng · 악센트 문자 · 한글 초/중/종성) → 언어 운율(모음/자음 ms · 강세/모라/성조 반음 · 하강).
///   ▸ 합성(소스-필터 · 학습 모델 아님): polyBLEP 톱니 성문 + 지터 · 기식 → 포먼트 대역통과 3 + 마찰 공진 1 · 폐쇄/버스트 · 1 ms 제어율 동시조음(12 ms).
///   ▸ 지연 예산 <see cref="BudgetMs"/>: 번역 지연 + 홉 + WSOLA 창 + 립 룩어헤드 합 &lt; 500 ms(<see cref="PipelineLatencyMs"/>).
///   ▸ 음소 타임라인: 입력 10 ms 홉 링(현재/직전 음소 · 동시조음 혼합 · 포락) → 립싱크 합성기 조회.
/// </summary>
public sealed class RealtimeNeuralVoiceCloningDubbingEngine
{
    public const int Fs = SpeakerEmbeddingExtractor.Fs, Hop = SpeakerEmbeddingExtractor.Hop, Dim = SpeakerEmbeddingExtractor.Dim;
    public const int MaxLanes = 4, MaxTokens = 2048, HopRing = 4096, UttRing = 16, Control = 48, CoartSamples = 1440;
    public const float BudgetMs = 500f, LookaheadMs = 10f;
    public const int PhonemeCount = 16;

    private readonly record struct PhoneSpec(float F1, float F2, float F3, float Voice, float Noise, float NoiseHz, float Closure);

    private static readonly PhoneSpec[] Specs =
    {
        new(500, 1500, 2500, 0f, 0f, 3000, 0f),        // Rest
        new(730, 1090, 2440, 1f, 0f, 3000, 0f),        // A
        new(530, 1840, 2480, 1f, 0f, 3000, 0f),        // E
        new(270, 2290, 3010, 0.9f, 0f, 3000, 0f),      // I
        new(570, 840, 2410, 1f, 0f, 3000, 0f),         // O
        new(300, 870, 2240, 0.9f, 0f, 3000, 0f),       // U
        new(250, 1000, 2300, 0.25f, 0.35f, 1500, 0.65f), // P/B/M
        new(400, 1400, 2500, 0.2f, 0.35f, 5000, 0f),   // F/V
        new(400, 1500, 2600, 0.2f, 0.3f, 6500, 0f),    // Th
        new(360, 1300, 2700, 0.7f, 0f, 3000, 0f),      // L
        new(400, 1700, 2700, 0f, 0.45f, 7000, 0f),     // S/Z
        new(350, 1800, 2400, 0.1f, 0.4f, 2200, 0.6f),  // K/G
        new(350, 1700, 2600, 0.1f, 0.4f, 4200, 0.6f),  // T/D
        new(420, 1300, 1600, 0.7f, 0f, 3000, 0f),      // R
        new(300, 700, 2200, 0.75f, 0f, 3000, 0f),      // W
        new(250, 1700, 2600, 0.5f, 0f, 3000, 0f),      // N
    };

    // 언어 운율: 모음 ms · 자음 ms · 휴지 ms · 반음 폭 · 강세 유형(0 평탄 · 1 강세 · 2 모라 · 3 성조) · 번역 지연 ms
    private readonly record struct Prosody(float VowelMs, float ConsMs, float PauseMs, float RangeSt, byte Kind, float MtMs);

    private static Prosody ProsodyOf(DubLanguage l) => l switch
    {
        DubLanguage.Ko => new(85f, 55f, 160f, 3f, 0, 0f),
        DubLanguage.En => new(100f, 62f, 170f, 5f, 1, 160f),
        DubLanguage.Ja => new(72f, 48f, 150f, 3f, 2, 210f),
        DubLanguage.Zh => new(115f, 60f, 170f, 6f, 3, 190f),
        DubLanguage.Es => new(80f, 52f, 150f, 4f, 0, 180f),
        DubLanguage.Fr => new(90f, 56f, 160f, 3f, 0, 185f),
        DubLanguage.De => new(100f, 66f, 170f, 4f, 1, 175f),
        _ => new(90f, 58f, 160f, 4f, 0, 200f),
    };

    private static readonly sbyte[] ZhTones = { 4, 1, -3, 2 };
    // 한글 초성 19 · 중성 21 · 종성 28 → 립 음소(0 = 없음)
    private static readonly byte[] Cho = { 11, 11, 15, 12, 12, 13, 6, 6, 6, 10, 10, 0, 10, 10, 10, 11, 12, 6, 0 };
    private static readonly byte[] Jung = { 1, 2, 1, 2, 4, 2, 4, 2, 4, 1, 2, 2, 4, 5, 4, 2, 3, 5, 5, 3, 3 };
    private static readonly byte[] Jong = { 0, 11, 11, 11, 15, 15, 15, 12, 9, 9, 9, 9, 9, 9, 9, 9, 6, 6, 6, 12, 12, 15, 12, 12, 11, 12, 6, 0 };

    private sealed class Lane
    {
        public readonly LipPhoneme[] Ph = new LipPhoneme[MaxTokens];
        public readonly int[] Dur = new int[MaxTokens];
        public readonly float[] Sem = new float[MaxTokens];
        public readonly byte[] Flags = new byte[MaxTokens];   // 1 = 발화 시작 · 2 = 발화 끝
        public readonly byte[] HopPh = new byte[HopRing], HopPrev = new byte[HopRing];
        public readonly float[] HopBlend = new float[HopRing], HopEnv = new float[HopRing];
        public readonly long[] StartPos = new long[UttRing], EndPos = new long[UttRing];
        public readonly SpeakerEmbeddingExtractor Out = new();
        public DubLanguage Lang = DubLanguage.En;
        public long Head, Tail, Clock, Remaining, TokenElapsed;
        public int Started, Done;
        public LipPhoneme Prev;
        public bool Speaking, Seeded;
        public float F0 = 120f, Scale = 1f, Breath = 0.1f;
        // 합성 상태
        public float Phase, F0Cur = 120f, F1, F2, F3, NoiseHz = 3000f, Voice, Noise, Amp;
        public float B10, B1a1, B1a2, B20, B2a1, B2a2, B30, B3a1, B3a2, BN0, BNa1, BNa2;
        public float S11, S12, S21, S22, S31, S32, SN1, SN2;
        public uint Rng = 0x9E3779B9u;
    }

    private readonly Lane[] _lanes = new Lane[MaxLanes];
    private readonly float[] _e1 = new float[Dim], _e2 = new float[Dim];

    public RealtimeNeuralVoiceCloningDubbingEngine()
    {
        for (var i = 0; i < MaxLanes; i++) { _lanes[i] = new Lane(); _lanes[i].Rng += (uint)i * 7919u; ResetLane(i); }
    }

    public SpeakerEmbeddingExtractor Source { get; } = new();
    /// <summary>음색 클로닝 폐루프(OFF = 기본 성인 남성 톤 고정).</summary>
    public bool CloneEnabled { get; set; } = true;
    public double LastRenderUs { get; private set; }
    public double MaxRenderUs { get; private set; }

    public static float TranslationLatencyMs(DubLanguage l) => ProsodyOf(l).MtMs;
    /// <summary>파이프라인 지연(번역 + 엔진 홉 + 룩어헤드 + WSOLA 창/탐색 + 립 Hermite 룩어헤드).</summary>
    public static float PipelineLatencyMs(DubLanguage l, bool guard) =>
        TranslationLatencyMs(l) + 1000f * Hop / Fs + LookaheadMs
        + (guard ? 1000f * (AudioVisualSyncJitterCompensationGuard.Win + AudioVisualSyncJitterCompensationGuard.Tol) / Fs + AudioVisualSyncJitterCompensationGuard.LipLagMs : 1000f * AudioVisualSyncJitterCompensationGuard.Win / Fs);

    public DubLanguage LanguageOf(int lane) => _lanes[lane].Lang;
    public long RemainingSamples(int lane) => _lanes[lane].Remaining;
    public bool IsSpeaking(int lane) => _lanes[lane].Speaking;
    public long ClockOf(int lane) => _lanes[lane].Clock;
    public int UtterancesStarted(int lane) => _lanes[lane].Started;
    public int UtterancesDone(int lane) => _lanes[lane].Done;
    public long UtteranceStartPos(int lane, int k) => _lanes[lane].StartPos[k % UttRing];
    public long UtteranceEndPos(int lane, int k) => _lanes[lane].EndPos[k % UttRing];
    public float CloneF0(int lane) => _lanes[lane].F0;
    public float FormantScale(int lane) => _lanes[lane].Scale;
    public float Breathiness(int lane) => _lanes[lane].Breath;
    public SpeakerEmbeddingExtractor Output(int lane) => _lanes[lane].Out;

    public void ResetLane(int lane)
    {
        var l = _lanes[lane];
        l.Head = l.Tail = l.Clock = l.Remaining = l.TokenElapsed = 0;
        l.Started = l.Done = 0; l.Prev = LipPhoneme.Rest; l.Speaking = l.Seeded = false;
        l.F0 = 120f; l.Scale = 1f; l.Breath = 0.1f;
        l.Phase = 0f; l.F0Cur = 120f; l.F1 = 500f; l.F2 = 1500f; l.F3 = 2500f; l.NoiseHz = 3000f; l.Voice = l.Noise = l.Amp = 0f;
        l.S11 = l.S12 = l.S21 = l.S22 = l.S31 = l.S32 = l.SN1 = l.SN2 = 0f;
        Array.Clear(l.HopPh); Array.Clear(l.HopPrev); Array.Clear(l.HopBlend); Array.Clear(l.HopEnv);
        l.Out.Reset();
        UpdateCoefs(l);
    }

    public void PushSource(ReadOnlySpan<float> pcm) => Source.Push(pcm);

    /// <summary>원본 화자 교체 — 임베딩 · 레인 클론 재시드.</summary>
    public void ResetCloning()
    {
        Source.Reset();
        for (var i = 0; i < MaxLanes; i++) { var l = _lanes[i]; l.Out.Reset(); l.Seeded = false; l.F0 = 120f; l.Scale = 1f; l.Breath = 0.1f; }
    }

    // ══════════════════════ G2P · 운율(문자 순회 · 할당 없음) ══════════════════════

    /// <summary>계획 길이(샘플) — 큐 미적재 드라이런.</summary>
    public static long Plan(DubLanguage lang, ReadOnlySpan<char> text) => Parse(null, lang, text);

    /// <summary>번역 토큰 적재(직전 발화 뒤 연속) → 계획 샘플 수.</summary>
    public long Enqueue(int lane, DubLanguage lang, ReadOnlySpan<char> text)
    {
        var l = _lanes[lane];
        l.Lang = lang;
        var before = l.Tail;
        var n = Parse(l, lang, text);
        if (l.Tail > before)
        {
            l.Flags[before % MaxTokens] |= 1;
            l.Flags[(l.Tail - 1) % MaxTokens] |= 2;
            l.Remaining += n;
        }
        return n;
    }

    private static long Parse(Lane? l, DubLanguage lang, ReadOnlySpan<char> text)
    {
        var p = ProsodyOf(lang);
        long total = 0;
        var vowel = 0; var sem = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            var c = char.ToLowerInvariant(text[i]);
            var next = i + 1 < text.Length ? char.ToLowerInvariant(text[i + 1]) : ' ';
            if (c >= '가' && c <= '힣')
            {
                var s = c - 0xAC00; int cho = s / (21 * 28), jung = s / 28 % 21, jong = s % 28;
                if (Cho[cho] != 0) total += Emit(l, (LipPhoneme)Cho[cho], p.ConsMs, sem);
                if (jung is 9 or 10 or 14 or 15 or 16) total += Emit(l, LipPhoneme.W, p.ConsMs * 0.6f, sem);
                sem = VowelSem(p, vowel++, sem);
                total += Emit(l, (LipPhoneme)Jung[jung], p.VowelMs, sem);
                if (Jong[jong] != 0) total += Emit(l, (LipPhoneme)Jong[jong], p.ConsMs * 0.8f, sem);
                continue;
            }
            LipPhoneme ph;
            var dur = p.ConsMs;
            switch (c)
            {
                case 'a' or 'à' or 'á' or 'â': ph = LipPhoneme.A; break;
                case 'e' or 'é' or 'è' or 'ê' or 'ä': ph = LipPhoneme.E; break;
                case 'i' or 'í' or 'y' or 'î': ph = LipPhoneme.I; break;
                case 'o' or 'ó' or 'ô' or 'ö': ph = LipPhoneme.O; break;
                case 'u' or 'ú' or 'ü' or 'û': ph = LipPhoneme.U; break;
                case 'b' or 'm' or 'p':
                    if (c == 'p' && next == 'h') { ph = LipPhoneme.Fv; i++; } else ph = LipPhoneme.Pbm;
                    break;
                case 'f' or 'v': ph = LipPhoneme.Fv; break;
                case 't' when next == 'h': ph = LipPhoneme.Th; i++; break;
                case 's' or 'c' when next == 'h': ph = LipPhoneme.Sz; i++; break;
                case 'c' when next is 'e' or 'i': ph = LipPhoneme.Sz; break;
                case 'c' or 'k' or 'g' or 'q' or 'x': ph = LipPhoneme.Kg; break;
                case 't' or 'd': ph = LipPhoneme.Td; break;
                case 's' or 'z' or 'j' or 'ç' or 'ß': ph = LipPhoneme.Sz; break;
                case 'l': ph = LipPhoneme.L; break;
                case 'r': ph = LipPhoneme.R; break;
                case 'w': ph = LipPhoneme.W; break;
                case 'n' or 'ñ': ph = LipPhoneme.N; if (next == 'g') i++; break;
                case ',' or '.' or '!' or '?' or ';' or ':' or '、' or '。':
                    total += Emit(l, LipPhoneme.Rest, p.PauseMs, sem);
                    continue;
                default: continue;   // 공백 · h · 아포스트로피 = 연음
            }
            if (ph <= LipPhoneme.U)
            {
                sem = VowelSem(p, vowel++, sem);
                dur = p.VowelMs * (p.Kind == 1 && (vowel & 1) == 1 ? 1.25f : 1f);
            }
            else if (i > 0 && char.ToLowerInvariant(text[i - 1]) == c) continue;   // 중복 자음 축약
            if (c == 'x') total += Emit(l, LipPhoneme.Kg, dur * 0.6f, sem) + Emit(l, LipPhoneme.Sz, dur * 0.6f, sem);
            else total += Emit(l, ph, dur, sem);
        }
        return total;
    }

    private static float VowelSem(in Prosody p, int vowel, float prev) => p.Kind switch
    {
        1 => ((vowel & 1) == 0 ? 0.6f * p.RangeSt : 0f) - 0.12f * vowel,
        2 => (vowel == 0 ? -1f : 1.5f) - 0.1f * vowel,
        3 => ZhTones[vowel & 3] * p.RangeSt / 6f,
        _ => 0.5f * p.RangeSt * MathF.Sin(vowel * 1.7f) * 0.5f - 0.12f * vowel,
    };

    private static long Emit(Lane? l, LipPhoneme ph, float ms, float sem)
    {
        var n = (int)(ms * Fs / 1000f);
        if (l is null) return n;
        if (l.Tail - l.Head >= MaxTokens) return 0;
        var k = l.Tail % MaxTokens;
        l.Ph[k] = ph; l.Dur[k] = n; l.Sem[k] = sem; l.Flags[k] = 0;
        l.Tail++;
        return n;
    }

    // ══════════════════════ 합성(무할당 핫패스) ══════════════════════

    /// <summary>레인 오디오 렌더(1 ms 제어 · 샘플 공진기 · 출력 재분석 → 음색 적응).</summary>
    public void Render(int lane, Span<float> dst)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var l = _lanes[lane];
        var done = 0;
        while (done < dst.Length)
        {
            var off = (int)(l.Clock % Control);
            if (off == 0) ControlStep(l);
            var n = Math.Min(dst.Length - done, Control - off);
            SynthBlock(l, dst.Slice(done, n));
            done += n;
        }
        l.Out.Push(dst);
        if (CloneEnabled && Source.Valid)
        {
            if (!l.Seeded) { l.F0 = Source.F0Hz; l.Seeded = true; }
            if (l.Out.Valid && l.Out.TakeUpdated())
            {
                // F0 폐루프(원본 ±20% 클램프 = 피치 오검출 발산 차단) · 성도 배율 = (F0/120)^0.35 · 기식 = 고역비 차
                var src = Source.F0Hz;
                l.F0 = Math.Clamp(l.F0 * MathF.Pow(src / MathF.Max(l.Out.F0Hz, 40f), 0.08f), src * 0.8f, src * 1.2f);
                l.Scale += 0.05f * (Math.Clamp(MathF.Pow(src / 120f, 0.35f), 0.85f, 1.4f) - l.Scale);
                l.Breath = Math.Clamp(l.Breath + 0.02f * (Source.HiRatio - l.Out.HiRatio), 0f, 0.6f);
            }
        }
        else if (!CloneEnabled) { l.F0 = 120f; l.Scale = 1f; l.Breath = 0.1f; l.Seeded = false; }
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastRenderUs = us;
        if (us > MaxRenderUs) MaxRenderUs = us;
    }

    public void ResetTiming() => MaxRenderUs = 0;

    private void ControlStep(Lane l)
    {
        // 토큰 진행
        if (l.Speaking)
        {
            var k = l.Head % MaxTokens;
            if (l.TokenElapsed >= l.Dur[k])
            {
                if ((l.Flags[k] & 2) != 0) { l.EndPos[l.Done % UttRing] = l.Clock; l.Done++; }
                l.Prev = l.Ph[k];
                l.Head++; l.TokenElapsed = 0; l.Speaking = false;
            }
        }
        if (!l.Speaking && l.Head < l.Tail)
        {
            var k = l.Head % MaxTokens;
            if ((l.Flags[k] & 1) != 0) { l.StartPos[l.Started % UttRing] = l.Clock; l.Started++; }
            l.Speaking = true; l.TokenElapsed = 0;
        }

        LipPhoneme ph; float frac = 0f, sem = 0f;
        if (l.Speaking)
        {
            var k = l.Head % MaxTokens;
            ph = l.Ph[k]; sem = l.Sem[k];
            frac = l.Dur[k] > 0 ? (float)l.TokenElapsed / l.Dur[k] : 1f;
        }
        else { ph = LipPhoneme.Rest; if (l.Head >= l.Tail) l.Remaining = 0; }

        var sp = Specs[(int)ph];
        float voice = sp.Voice, noise = sp.Noise;
        if (sp.Closure > 0f)
        {
            if (frac < sp.Closure) noise = 0f;
            else { voice = 0f; noise *= 1f - 0.6f * (frac - sp.Closure) / (1f - sp.Closure); }
        }
        var kf = 1f - MathF.Exp(-1f / 12f);   // 동시조음 12 ms
        var scale = l.Scale;
        l.F1 += kf * (sp.F1 * scale - l.F1);
        l.F2 += kf * (sp.F2 * scale - l.F2);
        l.F3 += kf * (sp.F3 * scale - l.F3);
        l.NoiseHz += 0.5f * (sp.NoiseHz - l.NoiseHz);
        var ka = 1f - MathF.Exp(-1f / 6f);
        l.Voice += ka * (voice - l.Voice);
        l.Noise += 0.5f * (noise - l.Noise);
        var jitter = 1f + 0.004f * ((Next(l) & 0xFFFF) / 32768f - 1f);
        var targetF0 = l.F0 * MathF.Pow(2f, sem / 12f);
        l.F0Cur += 0.25f * (targetF0 - l.F0Cur);
        l.F0Cur *= jitter;
        UpdateCoefs(l);

        if (l.Clock % Hop == Hop / 2)
        {
            var h = (int)(l.Clock / Hop % HopRing);
            l.HopPh[h] = (byte)ph; l.HopPrev[h] = (byte)l.Prev;
            l.HopBlend[h] = Math.Clamp((float)l.TokenElapsed / CoartSamples, 0f, 1f);
            l.HopEnv[h] = l.Voice + l.Noise;
        }
    }

    private static uint Next(Lane l) { var x = l.Rng; x ^= x << 13; x ^= x >> 17; x ^= x << 5; return l.Rng = x; }

    private static void Bp(float f, float bw, out float b0, out float a1, out float a2)
    {
        f = Math.Clamp(f, 60f, Fs * 0.45f);
        var w = 2f * MathF.PI * f / Fs;
        var alpha = MathF.Sin(w) * bw / (2f * f);
        var a0 = 1f + alpha;
        b0 = alpha / a0; a1 = -2f * MathF.Cos(w) / a0; a2 = (1f - alpha) / a0;
    }

    private static void UpdateCoefs(Lane l)
    {
        Bp(l.F1, 80f, out l.B10, out l.B1a1, out l.B1a2);
        Bp(l.F2, 100f, out l.B20, out l.B2a1, out l.B2a2);
        Bp(l.F3, 140f, out l.B30, out l.B3a1, out l.B3a2);
        Bp(l.NoiseHz, l.NoiseHz * 0.5f, out l.BN0, out l.BNa1, out l.BNa2);
    }

    private void SynthBlock(Lane l, Span<float> dst)
    {
        var dt = l.F0Cur / Fs;
        var breath = l.Breath;
        for (var i = 0; i < dst.Length; i++)
        {
            // polyBLEP 톱니
            l.Phase += dt;
            if (l.Phase >= 1f) l.Phase -= 1f;
            var saw = 2f * l.Phase - 1f;
            if (l.Phase < dt) { var t = l.Phase / dt; saw -= t + t - t * t - 1f; }
            else if (l.Phase > 1f - dt) { var t = (l.Phase - 1f) / dt; saw -= t * t + t + t + 1f; }
            var wn = (Next(l) & 0xFFFFFF) / 8388608f - 1f;
            var glot = saw * l.Voice;
            var exc = glot + wn * l.Voice * breath * 0.6f;
            var o1 = l.B10 * exc + l.S11; l.S11 = -l.B1a1 * o1 + l.S12; l.S12 = -l.B10 * exc - l.B1a2 * o1;
            var o2 = l.B20 * exc + l.S21; l.S21 = -l.B2a1 * o2 + l.S22; l.S22 = -l.B20 * exc - l.B2a2 * o2;
            var o3 = l.B30 * exc + l.S31; l.S31 = -l.B3a1 * o3 + l.S32; l.S32 = -l.B30 * exc - l.B3a2 * o3;
            var nx = wn * l.Noise;
            var on = l.BN0 * nx + l.SN1; l.SN1 = -l.BNa1 * on + l.SN2; l.SN2 = -l.BN0 * nx - l.BNa2 * on;
            var y = 2.2f * o1 + 1.4f * o2 + 0.8f * o3 + 1.2f * on;
            dst[i] = y / (1f + MathF.Abs(y));
        }
        var n = dst.Length;
        l.Clock += n;
        if (l.Speaking) { l.TokenElapsed += n; l.Remaining = Math.Max(0, l.Remaining - n); }
    }

    // ══════════════════════ 음소 타임라인 · 음색 지표 ══════════════════════

    /// <summary>입력 샘플 위치의 음소(10 ms 홉 해상도 · 링 범위 밖 = 휴지).</summary>
    public LipPhoneme PhonemeAt(int lane, long pos, out LipPhoneme prev, out float blend, out float env)
    {
        var l = _lanes[lane];
        var hop = pos / Hop;
        if (pos < 0 || hop * Hop + Hop / 2 > l.Clock || l.Clock / Hop - hop >= HopRing) { prev = LipPhoneme.Rest; blend = 1f; env = 0f; return LipPhoneme.Rest; }
        var h = (int)(hop % HopRing);
        prev = (LipPhoneme)l.HopPrev[h]; blend = l.HopBlend[h]; env = l.HopEnv[h];
        return (LipPhoneme)l.HopPh[h];
    }

    /// <summary>원본 화자 d-vector ↔ 레인 출력 d-vector 코사인 유사도(SIMD).</summary>
    public float Similarity(int lane)
    {
        if (!Source.Valid || !_lanes[lane].Out.Valid) return 0f;
        Source.Embedding(_e1);
        _lanes[lane].Out.Embedding(_e2);
        return Cosine(_e1, _e2);
    }

    public static float Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var d = DubSimd.Dot(a, b);
        var n = MathF.Sqrt(DubSimd.Dot(a, a) * DubSimd.Dot(b, b));
        return n > 1e-12f ? d / n : 0f;
    }

    public static bool IsVowel(LipPhoneme p) => p is >= LipPhoneme.A and <= LipPhoneme.U;

    public static string Name(LipPhoneme p) => p switch
    {
        LipPhoneme.Rest => "·", LipPhoneme.Pbm => "P/B/M", LipPhoneme.Fv => "F/V", LipPhoneme.Sz => "S/Z", LipPhoneme.Kg => "K/G", LipPhoneme.Td => "T/D",
        _ => p.ToString(),
    };
}
