namespace PromptFlowStudio.Services.StationMaster;

public enum EvoParam : byte { SpeechRate, PhysicsInterval, BgmEnergy, TimeSaleInterval }

/// <summary>v17.70.0 릴스 배포 성과(v17.69 팩토리 · 플랫폼 인사이트) — 값 형식.</summary>
public struct ReelPerformance
{
    public int ReelId;
    public long Views, Shares, Comments;
    /// <summary>완청률 0~1.</summary>
    public float Completion;
    /// <summary>댓글 감성 −1~1.</summary>
    public float Sentiment;
}

/// <summary>v17.70.0 세그먼트 송출 당시 선택된 팔(파라미터별 레벨 인덱스) 스냅숏 — 성과 귀속 키.</summary>
public struct EvoArms
{
    public byte SpeechRate, PhysicsInterval, BgmEnergy, TimeSaleInterval;
    public readonly int this[int p] => p switch { 0 => SpeechRate, 1 => PhysicsInterval, 2 => BgmEnergy, _ => TimeSaleInterval };
    public void Set(int p, int v) { var b = (byte)v; switch (p) { case 0: SpeechRate = b; break; case 1: PhysicsInterval = b; break; case 2: BgmEnergy = b; break; default: TimeSaleInterval = b; break; } }
}

/// <summary>
/// [PromptFlow Studio v17.70.0] 🧬 옴니채널 바이럴 피드백 자가진화 합성기(폐루프 · 무할당).
///   ▸ 섭취: 릴 성과(완청률 · 공유율 · 댓글 감성 · 조회수) → 바이럴 성공 계수 s ∈ [0, 1] = 0.45·완청 + 0.25·min(1, 공유율/5 %) + 0.2·(감성+1)/2 + 0.1·min(1, log₁₀(조회+1)/6) ·
///     증거 가중 w = clamp(log₁₀(조회+10)/3, 0.3, 2).
///   ▸ 베이지안: 파라미터 4 × 레벨 5 팔 Beta(α, β) — 송출 당시 팔에 분수 갱신 α += w·s · β += w·(1 − s) · 망각 λ = 0.995(비정상 성과 추종).
///   ▸ 적응: Thompson 샘플링(Beta = Gamma(α)/(Gamma(α)+Gamma(β)) · 시드 xorshift · Marsaglia–Tsang) 최대 팔 = 다음 세그먼트 귀속 팔(탐색) ·
///     적용값 = 사후 평균 소프트맥스(τ) 가중 레벨 평균(활용) → EMA 평활 게이지.
///     발화 속도(WPM 배율) · 쇼러너 물리 개입 주기(s) · BGM 에너지(레벨/템포) · 타임세일 간격(s).
/// </summary>
public sealed class ClosedLoopViralFeedbackSelfEvolutionSynthesizer
{
    public const int Params = 4, Levels = 5, Hist = 120;
    public const float Prior = 1f, Forget = 0.995f, Temperature = 0.04f, Smooth = 0.35f, BaseWpm = 150f;

    /// <summary>파라미터별 레벨 값(오름차순).</summary>
    public static readonly float[] LevelValues =
    {
        0.80f, 0.90f, 1.00f, 1.12f, 1.25f,         // 발화 속도 배율(× 150 WPM)
        40f, 28f, 18f, 12f, 8f,                    // 물리 개입 주기 s(작을수록 잦음)
        0.30f, 0.45f, 0.60f, 0.75f, 0.90f,         // BGM 에너지
        600f, 420f, 300f, 210f, 150f,              // 타임세일 간격 s
    };

    private readonly float[] _a = new float[Params * Levels], _b = new float[Params * Levels];
    private readonly float[] _applied = new float[Params];
    private readonly float[] _sS = new float[Hist];
    private readonly float[][] _pS = { new float[Hist], new float[Hist], new float[Hist], new float[Hist] };
    private EvoArms _arms;
    private int _sHead, _sN;
    private uint _rng;
    private readonly uint _seed;

    public ClosedLoopViralFeedbackSelfEvolutionSynthesizer(uint seed = 0x5EEDu) { _seed = seed == 0 ? 1u : seed; Reset(); }

    public long Ingested { get; private set; }
    public long Rejected { get; private set; }
    public float LastSuccess { get; private set; }
    public float MeanSuccess { get; private set; }
    public float BestSuccess { get; private set; }
    public double LastIngestUs { get; private set; }
    public double MaxIngestUs { get; private set; }
    public EvoArms Arms => _arms;
    public float Applied(EvoParam p) => _applied[(int)p];
    public float SpeechRate => _applied[0];
    public float Wpm => BaseWpm * _applied[0];
    public float PhysicsIntervalSec => _applied[1];
    public float BgmEnergy => _applied[2];
    public double BgmBpm => 96 + 44 * Math.Clamp((_applied[2] - 0.3f) / 0.6f, 0f, 1f);
    public float TimeSaleIntervalSec => _applied[3];
    public float Mean(EvoParam p, int level) { var i = (int)p * Levels + level; return _a[i] / (_a[i] + _b[i]); }
    public float Evidence(EvoParam p, int level) { var i = (int)p * Levels + level; return _a[i] + _b[i] - 2 * Prior; }
    public static float Level(EvoParam p, int level) => LevelValues[(int)p * Levels + level];
    public int SeriesCount => _sN;
    public float SuccessSeries(int i) => _sS[(_sHead - 1 - i + 2 * Hist) % Hist];
    public float ParamSeries(EvoParam p, int i) => _pS[(int)p][(_sHead - 1 - i + 2 * Hist) % Hist];

    /// <summary>게이지 정규화(0~1 · 레벨 범위 기준).</summary>
    public float Normalized(EvoParam p)
    {
        float lo = Level(p, 0), hi = Level(p, Levels - 1);
        return Math.Clamp((_applied[(int)p] - lo) / (hi - lo), 0f, 1f);
    }

    public static string ParamName(EvoParam p) => p switch
    {
        EvoParam.SpeechRate => "발화 속도", EvoParam.PhysicsInterval => "물리 개입 주기", EvoParam.BgmEnergy => "BGM 텐션", _ => "타임세일 간격",
    };

    public void Reset()
    {
        Array.Fill(_a, Prior); Array.Fill(_b, Prior);
        for (var p = 0; p < Params; p++) { _applied[p] = Level((EvoParam)p, Levels / 2); _arms.Set(p, Levels / 2); }
        Ingested = Rejected = 0; LastSuccess = MeanSuccess = BestSuccess = 0; MaxIngestUs = 0; _sN = 0; _rng = _seed;
    }

    /// <summary>바이럴 성공 계수(무할당).</summary>
    public static float SuccessCoefficient(in ReelPerformance m)
    {
        var views = Math.Max(0, m.Views);
        var shareRate = views > 0 ? (float)m.Shares / views : 0f;
        var s = 0.45f * Math.Clamp(m.Completion, 0f, 1f) + 0.25f * MathF.Min(1f, shareRate / 0.05f)
            + 0.2f * (Math.Clamp(m.Sentiment, -1f, 1f) + 1f) * 0.5f + 0.1f * MathF.Min(1f, MathF.Log10(views + 1f) / 6f);
        return Math.Clamp(s, 0f, 1f);
    }

    public static float EvidenceWeight(in ReelPerformance m) => Math.Clamp(MathF.Log10(Math.Max(0, m.Views) + 10f) / 3f, 0.3f, 2f);

    // ══════════════════════ 섭취 + 적응(무할당) ══════════════════════
    /// <summary>성과 섭취 → 송출 당시 팔 사후 갱신 → 다음 세그먼트 팔/적용값 재산출. false = 무효 메트릭.</summary>
    public bool Ingest(in ReelPerformance m, in EvoArms aired)
    {
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (m.Views < 0 || !float.IsFinite(m.Completion) || !float.IsFinite(m.Sentiment) || m.Shares < 0) { Rejected++; return false; }
        var s = SuccessCoefficient(in m);
        var w = EvidenceWeight(in m);
        for (var i = 0; i < _a.Length; i++) { _a[i] = Prior + (_a[i] - Prior) * Forget; _b[i] = Prior + (_b[i] - Prior) * Forget; }
        for (var p = 0; p < Params; p++)
        {
            var k = p * Levels + Math.Clamp(aired[p], 0, Levels - 1);
            _a[k] += w * s; _b[k] += w * (1f - s);
        }
        Ingested++;
        LastSuccess = s; MeanSuccess += (s - MeanSuccess) / Math.Min(Ingested, 50); if (s > BestSuccess) BestSuccess = s;
        Adapt();
        _sS[_sHead] = s;
        for (var p = 0; p < Params; p++) _pS[p][_sHead] = Normalized((EvoParam)p);
        _sHead = (_sHead + 1) % Hist; if (_sN < Hist) _sN++;
        var us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
        LastIngestUs = us; if (us > MaxIngestUs && Ingested > 2) MaxIngestUs = us;
        return true;
    }

    private void Adapt()
    {
        Span<float> wgt = stackalloc float[Levels];
        for (var p = 0; p < Params; p++)
        {
            var best = 0; var bestScore = float.MinValue; var maxMean = float.MinValue;
            for (var l = 0; l < Levels; l++)
            {
                var i = p * Levels + l;
                float a = _a[i], b = _b[i], n = a + b;
                var mean = a / n;
                float ga = Gamma(a), gb = Gamma(b);
                var score = ga / MathF.Max(ga + gb, 1e-12f);
                if (score > bestScore) { bestScore = score; best = l; }
                if (mean > maxMean) maxMean = mean;
            }
            // 사후 소프트맥스 가중 레벨 평균(탐색 없는 활용값) → EMA
            float sum = 0, val = 0;
            for (var l = 0; l < Levels; l++) { var i = p * Levels + l; wgt[l] = MathF.Exp((_a[i] / (_a[i] + _b[i]) - maxMean) / Temperature); sum += wgt[l]; }
            for (var l = 0; l < Levels; l++) val += wgt[l] / sum * Level((EvoParam)p, l);
            _applied[p] += (val - _applied[p]) * Smooth;
            _arms.Set(p, best);
        }
    }

    // ══════════════════════ 난수(무할당 · 결정적) ══════════════════════
    private float U() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return ((_rng >> 8) + 0.5f) / 16777216f; }
    private float N() { var u1 = U(); var u2 = U(); return MathF.Sqrt(-2f * MathF.Log(u1)) * MathF.Cos(2f * MathF.PI * u2); }

    /// <summary>Gamma(k, 1) — Marsaglia–Tsang(k ≥ 1) · k &lt; 1 은 Gamma(k+1)·U^(1/k).</summary>
    private float Gamma(float k)
    {
        if (k < 1f) return Gamma(k + 1f) * MathF.Pow(U(), 1f / k);
        var d = k - 1f / 3f; var c = 1f / MathF.Sqrt(9f * d);
        for (var guard = 0; guard < 64; guard++)
        {
            float x, v;
            do { x = N(); v = 1f + c * x; } while (v <= 0f);
            v = v * v * v;
            var u = U();
            if (u < 1f - 0.0331f * x * x * x * x || MathF.Log(u) < 0.5f * x * x + d * (1f - v + MathF.Log(v))) return d * v;
        }
        return k;
    }

    /// <summary>v17.75 외부 진단 보정 — 적용값을 레벨 범위 안에서 가산 이동(무할당 · 이후 섭취의 EMA 가 사후 평균으로 서서히 복귀).</summary>
    public float Nudge(EvoParam p, float delta)
    {
        float lo = Level(p, 0), hi = Level(p, Levels - 1);
        var i = (int)p;
        _applied[i] = Math.Clamp(_applied[i] + delta, MathF.Min(lo, hi), MathF.Max(lo, hi));
        return _applied[i];
    }

    /// <summary>탐색 없는 최선(사후 평균 최대) 팔.</summary>
    public int BestLevel(EvoParam p)
    {
        var best = 0; var bm = float.MinValue;
        for (var l = 0; l < Levels; l++) { var m = Mean(p, l); if (m > bm) { bm = m; best = l; } }
        return best;
    }
}

/// <summary>
/// v17.70.0 시뮬레이션 시청자 반응 모델(데모/회귀 · 결정적) — 숨은 선호 팔에 가까울수록 완청 · 공유 · 감성 상승 + 잡음.
/// 실 플랫폼 인사이트(v17.50 IG Insights · YT Analytics)가 결선되면 <see cref="ClosedLoopViralFeedbackSelfEvolutionSynthesizer.Ingest"/> 에 직접 공급.
/// </summary>
public sealed class SimulatedReelAudienceModel
{
    private uint _rng;
    private readonly int[] _pref = { 3, 1, 4, 2 };

    public SimulatedReelAudienceModel(uint seed = 0xC0FFEEu) => _rng = seed == 0 ? 1u : seed;

    public int Preferred(EvoParam p) => _pref[(int)p];
    public void SetPreferred(EvoParam p, int level) => _pref[(int)p] = Math.Clamp(level, 0, ClosedLoopViralFeedbackSelfEvolutionSynthesizer.Levels - 1);
    public float Noise { get; set; } = 0.06f;

    private float Next() { _rng ^= _rng << 13; _rng ^= _rng >> 17; _rng ^= _rng << 5; return (_rng & 0xFFFFFF) / 16777216f; }

    public ReelPerformance Respond(int reelId, in EvoArms arms, float peak = 85f)
    {
        var fit = 0f;
        for (var p = 0; p < ClosedLoopViralFeedbackSelfEvolutionSynthesizer.Params; p++) fit += 1f - MathF.Abs(arms[p] - _pref[p]) / 4f;
        fit /= ClosedLoopViralFeedbackSelfEvolutionSynthesizer.Params;   // 0~1
        var n = (Next() - 0.5f) * 2f * Noise;
        var completion = Math.Clamp(0.15f + 0.7f * fit * fit + n, 0f, 1f);
        var views = (long)(2_000 + 60_000 * fit * fit * (0.6f + peak / 250f) * (0.8f + 0.4f * Next()));
        return new ReelPerformance
        {
            ReelId = reelId, Views = views, Completion = completion,
            Shares = (long)(views * Math.Clamp(0.004f + 0.05f * fit * fit + n * 0.1f, 0f, 0.2f)),
            Comments = (long)(views * 0.01f), Sentiment = Math.Clamp(-0.4f + 1.2f * fit + n, -1f, 1f),
        };
    }
}
