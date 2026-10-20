namespace PromptFlowStudio.Services.ViralLoop;

public enum LoopGuardState : byte { Learning, Active, Warning, Frozen }

/// <summary>스타일 프리셋 — v17.47 변환 레벨 · v17.43 플레어 · v17.49 직결 합성 가중치.</summary>
public readonly record struct ReelsStylePreset(string Name, int TransformLevel, bool Flare, float LensWeight, float SetWeight, float VfxIntensity);

/// <summary>
/// [PromptFlow Studio v17.50.0] 🚀 인게이지먼트 피드백 루프 &amp; 자가 튜닝 세이프티 가드(<see cref="Observe"/> · <see cref="SampleHook"/> · <see cref="SampleStyle"/> 무할당).
/// <b>튜닝</b>: 후킹 템플릿 6 · 스타일 프리셋 4 를 각각 Beta(α, β) 팔로 두고 발행 후 1 h · 6 h · 24 h 인사이트에서
/// 보상 = 0.7·완청률(평균 시청 ms ÷ 길이) + 0.3·min(20·(저장 + 댓글 + 공유) ÷ 조회, 1) 을 가중(1 · 2 · 6) 증분 갱신한다.
/// 다음 리메이크는 Thompson 표본(Marsaglia–Tsang 감마 · xorshift · 할당 0)으로 팔을 고르고, VFX 강도는 스타일 사후 평균 가중합으로 적응한다.
/// <b>섀도우밴 조기 징후</b>: 1 h 조회를 직전 게시물 EWMA 기준선과 비교 — 기준선 확보(3 건) 후 0.25 미만 2 연속 또는 0.1 미만 1 건이면 동결,
/// 0.5 미만 / 6 h 성장 정체(6 h &lt; 1.15 × 1 h)는 경고. 동결 시 큐 즉시 정지 · 48 h 쿨다운 후 수동 재개만 허용 · 해당 팔 패널티.
/// IP 전환 · 탐지 회피 기능은 두지 않는다 — 동결은 계정 보호를 위한 정지이며 재개는 사람이 판단한다.
/// </summary>
public sealed class EngagementFeedbackSelfTuningGuard
{
    public const int Hooks = 6, Styles = 4, HistCap = 64, BaselineMin = 3;
    public const double CooldownHours = 48;
    public static readonly string[] HookTemplates =
    {
        "끝까지 보면 소름 돋는 3초 👀",
        "POV: 이걸 아직도 모른다고?",
        "Wait for it… 🎬",
        "스크롤 멈추세요 — 이건 진짜입니다",
        "Nobody talks about this 🤫",
        "저장해두고 두 번 보게 될 영상 📌",
    };
    public static readonly ReelsStylePreset[] StylePresets =
    {
        new("미니멀 클린", 1, false, 0.1f, 0.35f, 0.2f),
        new("시네마틱 아나모픽", 1, true, 0.35f, 0.45f, 0.55f),
        new("네온 VFX 하이", 2, true, 0.6f, 0.55f, 0.9f),
        new("다큐 리프레임", 3, false, 0.2f, 0.4f, 0.35f),
    };
    private readonly double[] _ha = new double[Hooks], _hb = new double[Hooks], _sa = new double[Styles], _sb = new double[Styles];
    private readonly float[] _rewardHist = new float[HistCap], _ratioHist = new float[HistCap];
    private int _histHead, _histN, _lowStreak, _posts;
    private ulong _rng = 0x9E3779B97F4A7C15UL;
    private double _baseline;

    public EngagementFeedbackSelfTuningGuard() => Reset();

    public LoopGuardState State { get; private set; }
    public double Baseline1h => _baseline;
    public int BaselinePosts => _posts;
    public float LastRatio { get; private set; } = 1f;
    public float ShadowScore { get; private set; }
    public long FrozenAtTicks { get; private set; }
    public long CooldownUntilTicks { get; private set; }
    public long Freezes { get; private set; }
    public long Warnings { get; private set; }
    public long Observations { get; private set; }
    public float LastReward { get; private set; }
    public float LastCompletion { get; private set; }
    public int HistoryCount => _histN;
    public float RewardHistory(int i) => _rewardHist[(_histHead - 1 - i + 2 * HistCap) % HistCap];
    public float RatioHistory(int i) => _ratioHist[(_histHead - 1 - i + 2 * HistCap) % HistCap];
    public double HookMean(int i) => _ha[i] / (_ha[i] + _hb[i]);
    public double StyleMean(int i) => _sa[i] / (_sa[i] + _sb[i]);
    public double HookPulls(int i) => _ha[i] + _hb[i] - 2;
    public double StylePulls(int i) => _sa[i] + _sb[i] - 2;
    public bool IsFrozen => State == LoopGuardState.Frozen;
    public static string StateName(LoopGuardState s) => s switch { LoopGuardState.Active => "정상", LoopGuardState.Warning => "주의", LoopGuardState.Frozen => "동결", _ => "학습 중" };

    /// <summary>스타일 사후 평균 가중 VFX 강도(0~1).</summary>
    public float AdaptiveVfx
    {
        get
        {
            double w = 0, s = 0;
            for (var i = 0; i < Styles; i++) { var m = StyleMean(i); w += m; s += m * StylePresets[i].VfxIntensity; }
            return w > 0 ? (float)(s / w) : 0.5f;
        }
    }

    public void Reset()
    {
        for (var i = 0; i < Hooks; i++) { _ha[i] = 1; _hb[i] = 1; }
        for (var i = 0; i < Styles; i++) { _sa[i] = 1; _sb[i] = 1; }
        _histHead = 0; _histN = 0; _lowStreak = 0; _posts = 0; _baseline = 0;
        State = LoopGuardState.Learning; ShadowScore = 0; Freezes = 0; Warnings = 0; Observations = 0;
    }

    // ══════════════════════ Thompson 표본(무할당) ══════════════════════
    public int SampleHook() => Argmax(_ha, _hb, Hooks);
    public int SampleStyle() => Argmax(_sa, _sb, Styles);

    private int Argmax(double[] a, double[] b, int n)
    {
        var best = 0; var bv = -1.0;
        for (var i = 0; i < n; i++) { var v = Beta(a[i], b[i]); if (v > bv) { bv = v; best = i; } }
        return best;
    }

    private double U()
    {
        _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17;
        return ((_rng >> 11) + 0.5) * (1.0 / 9007199254740992.0);
    }

    private double Normal() => Math.Sqrt(-2 * Math.Log(U())) * Math.Cos(2 * Math.PI * U());

    private double Gamma(double k)
    {
        if (k < 1) return Gamma(k + 1) * Math.Pow(U(), 1 / k);
        var d = k - 1.0 / 3; var c = 1 / Math.Sqrt(9 * d);
        while (true)
        {
            double x, v;
            do { x = Normal(); v = 1 + c * x; } while (v <= 0);
            v = v * v * v;
            var u = U();
            if (u < 1 - 0.0331 * x * x * x * x || Math.Log(u) < 0.5 * x * x + d * (1 - v + Math.Log(v))) return d * v;
        }
    }

    public double Beta(double a, double b) { var x = Gamma(a); var y = Gamma(b); return x / (x + y); }

    // ══════════════════════ 관측 ══════════════════════
    public static float Reward(in GraphBridge.GraphMediaInsights m, double durationSec, out float completion)
    {
        completion = m.AvgWatchMs > 0 && durationSec > 0 ? (float)Math.Clamp(m.AvgWatchMs / (durationSec * 1000), 0, 1) : 0f;
        var views = Math.Max(m.Views, 1);
        var eng = (Math.Max(m.Saved, 0) + Math.Max(m.Comments, 0) + Math.Max(m.Shares, 0)) / (float)views;
        return 0.7f * completion + 0.3f * Math.Min(20f * eng, 1f);
    }

    /// <summary>체크포인트(0 = 1 h · 1 = 6 h · 2 = 24 h) 인사이트 반영(무할당) — 동결 판정 결과 반환(true = 이번 관측으로 동결).</summary>
    public bool Observe(int hook, int style, int checkpoint, in GraphBridge.GraphMediaInsights m, long views1h, double durationSec, long nowTicks)
    {
        Observations++;
        var r = Reward(in m, durationSec, out var comp);
        LastReward = r; LastCompletion = comp;
        double w = checkpoint switch { 0 => 1, 1 => 2, _ => 6 };
        _ha[hook] += w * r; _hb[hook] += w * (1 - r);
        _sa[style] += w * r; _sb[style] += w * (1 - r);
        var froze = false;
        if (checkpoint == 0)
        {
            var ratio = _posts >= BaselineMin && _baseline > 0 ? (float)(Math.Max(m.Views, 0) / _baseline) : 1f;
            LastRatio = ratio;
            _rewardHist[_histHead] = r; _ratioHist[_histHead] = ratio;
            _histHead = (_histHead + 1) % HistCap;
            if (_histN < HistCap) _histN++;
            if (_posts >= BaselineMin)
            {
                if (ratio < 0.25f) _lowStreak++; else _lowStreak = 0;
                if (ratio < 0.1f || _lowStreak >= 2) { Freeze(hook, style, nowTicks); froze = true; }
                else if (ratio < 0.5f) { if (State != LoopGuardState.Frozen) { State = LoopGuardState.Warning; Warnings++; } }
                else if (State == LoopGuardState.Warning || State == LoopGuardState.Learning) State = LoopGuardState.Active;
            }
            ShadowScore = (float)Math.Clamp(1 - ratio, 0, 1);
            if (!froze && ratio >= 0.5f) { _baseline = _posts == 0 ? m.Views : 0.7 * _baseline + 0.3 * m.Views; _posts++; }
            if (_posts >= BaselineMin && State == LoopGuardState.Learning) State = LoopGuardState.Active;
        }
        else if (checkpoint == 1 && views1h > 0 && m.Views < 1.15 * views1h && State is LoopGuardState.Active or LoopGuardState.Learning)
        {
            State = LoopGuardState.Warning; Warnings++;
        }
        return froze;
    }

    /// <summary>외부 신호(API 제재 코드 등)로 즉시 동결.</summary>
    public void Freeze(int hook, int style, long nowTicks)
    {
        if (State == LoopGuardState.Frozen) return;
        State = LoopGuardState.Frozen;
        FrozenAtTicks = nowTicks; CooldownUntilTicks = nowTicks + (long)(CooldownHours * TimeSpan.TicksPerHour);
        Freezes++;
        if ((uint)hook < Hooks) _hb[hook] += 3;
        if ((uint)style < Styles) _sb[style] += 3;
        _lowStreak = 0;
    }

    public bool CanResume(long nowTicks) => State == LoopGuardState.Frozen && nowTicks >= CooldownUntilTicks;

    /// <summary>수동 재개 — 쿨다운 경과 후에만. 기준선은 재학습.</summary>
    public bool Resume(long nowTicks)
    {
        if (!CanResume(nowTicks)) return false;
        State = LoopGuardState.Learning; _posts = 0; _baseline = 0; ShadowScore = 0;
        return true;
    }
}
