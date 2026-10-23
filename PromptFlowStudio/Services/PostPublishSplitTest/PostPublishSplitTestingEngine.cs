namespace PromptFlowStudio.Services.PostPublishSplitTest;

public enum SplitVariant : byte { A, B }

public enum SplitTestPhase : byte { Idle, Tracking, Decided, Closed }

/// <summary>5 분 간격 폴링 1 표본(누적 조회 · 초당 유속 · 완청률 · 공유) — 값 타입 · 링 슬롯 재사용.</summary>
public struct SplitMetricSample
{
    public long TSec;
    public long Views;
    public float ViewsPerSec;
    public float Completion;
    public int Shares;
}

/// <summary>변형본 1 개의 발행 정보 + 90 분 고정 시계열 링(19 표본 · 생성 시 1 회 할당).</summary>
public sealed class SplitVariantTrack
{
    private readonly SplitMetricSample[] _ring = new SplitMetricSample[PostPublishSplitTestingEngine.Capacity];
    private int _head, _count;

    public SplitVariant Variant { get; internal set; }
    public int ContentId { get; internal set; }
    public string Hook { get; internal set; } = "";
    public int ThumbSlot { get; internal set; }
    public int Channel { get; internal set; }
    public long PublishedUnix { get; internal set; }
    public int Count => _count;
    public SplitMetricSample Last => _count == 0 ? default : _ring[(_head - 1 + _ring.Length) % _ring.Length];
    public float Score { get; internal set; }
    public bool Pruned { get; internal set; }

    /// <summary>시간순 표본(0 = 최초).</summary>
    public ref readonly SplitMetricSample At(int i) => ref _ring[(_head - _count + i + _ring.Length * 2) % _ring.Length];

    /// <summary>시간당 조회 유속(발행 후 경과 기준).</summary>
    public float ViewsPerHour(long nowUnix)
    {
        var age = nowUnix - PublishedUnix;
        return age <= 0 ? 0f : Last.Views * 3600f / age;
    }

    internal void Push(in SplitMetricSample s)
    {
        _ring[_head] = s;
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length) _count++;
    }

    internal void Clear() { _head = _count = 0; Score = 0f; Pruned = false; }
}

/// <summary>
/// [PromptFlow Studio v17.93.0] ⚖️ 포스트-퍼블리시 A/B 분기 배포 &amp; 지표 추적 엔진.
///   ▸ v17.85 훅 카피/썸네일 변형 A/B 를 서로 다른 시각(시차 <see cref="StaggerSec"/>) 또는 채널에 분기 등록.
///   ▸ 발행 시점부터 90 분 골든타임 동안 5 분 간격 표본을 변형별 고정 링(19)에 무할당 기록.
///   ▸ 승패 판정 = 가중 복합 스코어(유속 · 완청률 · 공유율) + 베타 사후분포 정규 근사 P(B &gt; A) — 신뢰도 ≥ 95 % 이면 조기 확정.
///   ▸ 기록 · 스코어링 핫패스는 필드/스택만 사용(LINQ · 박싱 · 문자열 0).
/// </summary>
public sealed class PostPublishSplitTestingEngine
{
    public const long GoldenWindowSec = 90 * 60, IntervalSec = 5 * 60;
    public const int Capacity = (int)(GoldenWindowSec / IntervalSec) + 1, MinSamples = 3;
    public const float WinnerConfidence = 0.95f, WVelocity = 0.45f, WCompletion = 0.40f, WShare = 0.15f;
    public const float ShareWeight = 4f;   // 공유 1 회 = 완청 4 회 가치(베타 성공 가중)

    private readonly SplitVariantTrack _a = new() { Variant = SplitVariant.A };
    private readonly SplitVariantTrack _b = new() { Variant = SplitVariant.B };

    public SplitTestPhase Phase { get; private set; }
    public int ContentId { get; private set; }
    public long StartUnix { get; private set; }
    public long StaggerSec { get; private set; }
    public float Confidence { get; private set; }
    public SplitVariant Leader { get; private set; }
    public bool HasWinner { get; private set; }
    public SplitVariant Winner { get; private set; }
    public bool ForcedWinner { get; private set; }
    public long DecidedUnix { get; private set; }
    public long Samples { get; private set; }
    public long Rejected { get; private set; }

    public SplitVariantTrack Track(SplitVariant v) => v == SplitVariant.A ? _a : _b;
    public SplitVariantTrack Loser => Track(Winner == SplitVariant.A ? SplitVariant.B : SplitVariant.A);
    public static string VariantName(SplitVariant v) => v == SplitVariant.A ? "A" : "B";
    public static string PhaseName(SplitTestPhase p) => p switch
    {
        SplitTestPhase.Tracking => "추적 중",
        SplitTestPhase.Decided => "승자 확정",
        SplitTestPhase.Closed => "골든타임 종료",
        _ => "대기",
    };

    /// <summary>골든타임 종료 시각(늦게 발행된 변형 기준 + 90 분).</summary>
    public long GoldenEndUnix => StartUnix + StaggerSec + GoldenWindowSec;
    public long RemainingSec(long nowUnix) => Phase == SplitTestPhase.Idle ? GoldenWindowSec : Math.Max(0, GoldenEndUnix - nowUnix);

    // ══════════════════════ 분기 배포(비핫패스) ══════════════════════

    /// <summary>변형 A/B 분기 등록 — B 는 <paramref name="staggerSec"/> 뒤 또는 다른 채널. 기존 추적은 리셋.</summary>
    public void Deploy(int contentId, string hookA, int thumbA, int channelA, string hookB, int thumbB, int channelB, long nowUnix, long staggerSec)
    {
        if (contentId <= 0) throw new ArgumentOutOfRangeException(nameof(contentId));
        if (channelA == channelB && staggerSec <= 0) throw new ArgumentException("같은 채널 분기는 시차(> 0 s)가 필요합니다.", nameof(staggerSec));
        ContentId = contentId;
        StartUnix = nowUnix;
        StaggerSec = Math.Max(0, staggerSec);
        Set(_a, hookA, thumbA, channelA, nowUnix);
        Set(_b, hookB, thumbB, channelB, nowUnix + StaggerSec);
        Confidence = 0.5f; Leader = SplitVariant.A;
        HasWinner = ForcedWinner = false;
        DecidedUnix = 0;
        Phase = SplitTestPhase.Tracking;
    }

    private void Set(SplitVariantTrack t, string hook, int thumb, int channel, long published)
    {
        t.Clear();
        t.ContentId = ContentId;
        t.Hook = hook ?? "";
        t.ThumbSlot = thumb;
        t.Channel = channel;
        t.PublishedUnix = published;
    }

    public void Reset()
    {
        _a.Clear(); _b.Clear();
        Phase = SplitTestPhase.Idle;
        ContentId = 0; StartUnix = StaggerSec = DecidedUnix = 0;
        Confidence = 0f; HasWinner = ForcedWinner = false;
    }

    // ══════════════════════ 폴링 기록 · 스코어링(핫패스 · 무할당) ══════════════════════

    /// <summary>표본 1 건 기록 — 발행 전 · 골든타임 밖 · 역행 시각은 거절. 반환 = 기록 여부.</summary>
    public bool Record(SplitVariant v, long nowUnix, long views, float completion, int shares)
    {
        var t = Track(v);
        if (Phase is SplitTestPhase.Idle || nowUnix < t.PublishedUnix || nowUnix > t.PublishedUnix + GoldenWindowSec || (t.Count > 0 && nowUnix <= t.Last.TSec)) { Rejected++; return false; }
        var prev = t.Last;
        var dt = t.Count == 0 ? nowUnix - t.PublishedUnix : nowUnix - prev.TSec;
        var dv = Math.Max(0, views - prev.Views);
        SplitMetricSample s;
        s.TSec = nowUnix;
        s.Views = Math.Max(views, prev.Views);
        s.ViewsPerSec = dt <= 0 ? 0f : (float)dv / dt;
        s.Completion = Math.Clamp(completion, 0f, 1f);
        s.Shares = Math.Max(shares, prev.Shares);
        t.Push(s);
        Samples++;
        return true;
    }

    /// <summary>가중 복합 스코어(0~1) — 상대 유속 · 완청률 · 공유율.</summary>
    public static float CompositeScore(in SplitMetricSample s, float peerViewsPerSec)
    {
        var vMax = Math.Max(s.ViewsPerSec, peerViewsPerSec);
        var vel = vMax <= 0f ? 0.5f : s.ViewsPerSec / vMax;
        var share = s.Views <= 0 ? 0f : Math.Min(1f, s.Shares * 20f / s.Views);
        return WVelocity * vel + WCompletion * s.Completion + WShare * share;
    }

    /// <summary>
    /// 베타 사후분포(성공 = 완청 + 공유 × 가중 · 시행 = 조회) 정규 근사 P(B &gt; A).
    /// </summary>
    public static float ProbabilityBBeatsA(in SplitMetricSample a, in SplitMetricSample b)
    {
        BetaMoments(a, out var ma, out var va);
        BetaMoments(b, out var mb, out var vb);
        var sd = Math.Sqrt(va + vb);
        return sd <= 0 ? 0.5f : (float)NormalCdf((mb - ma) / sd);
    }

    private static void BetaMoments(in SplitMetricSample s, out double mean, out double var)
    {
        var n = (double)Math.Max(0, s.Views);
        var k = Math.Min(n, n * s.Completion + s.Shares * (double)ShareWeight);
        var al = 1 + k; var be = 1 + n - k; var sum = al + be;
        mean = al / sum;
        var = al * be / (sum * sum * (sum + 1));
    }

    /// <summary>표준정규 누적분포 — Abramowitz-Stegun 7.1.26 erf 근사(|ε| &lt; 1.5e-7).</summary>
    public static double NormalCdf(double z)
    {
        var x = Math.Abs(z) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * x);
        var erf = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return z >= 0 ? 0.5 * (1 + erf) : 0.5 * (1 - erf);
    }

    /// <summary>스코어 · 신뢰도 갱신 + 승자 판정(양측 ≥ 3 표본 · 신뢰도 ≥ 95 % · 복합 스코어 동의) · 골든타임 종료 전이. 반환 = 이번 호출에서 승자 확정.</summary>
    public bool Evaluate(long nowUnix)
    {
        if (Phase is SplitTestPhase.Idle or SplitTestPhase.Closed) return false;
        var decided = false;
        if (_a.Count > 0 && _b.Count > 0)
        {
            var la = _a.Last; var lb = _b.Last;
            _a.Score = CompositeScore(la, lb.ViewsPerSec);
            _b.Score = CompositeScore(lb, la.ViewsPerSec);
            var pB = ProbabilityBBeatsA(la, lb);
            Leader = pB >= 0.5f ? SplitVariant.B : SplitVariant.A;
            Confidence = pB >= 0.5f ? pB : 1f - pB;
            var scoreAgrees = Leader == SplitVariant.B ? _b.Score >= _a.Score : _a.Score >= _b.Score;
            if (!HasWinner && _a.Count >= MinSamples && _b.Count >= MinSamples && Confidence >= WinnerConfidence && scoreAgrees)
            {
                HasWinner = true; Winner = Leader; DecidedUnix = nowUnix;
                Phase = SplitTestPhase.Decided;
                decided = true;
            }
        }
        if (nowUnix >= GoldenEndUnix)
        {
            if (!HasWinner && _a.Count > 0 && _b.Count > 0)
            {
                // 골든타임 만료 — 신뢰도 미달이면 복합 스코어 우세 쪽을 잠정 승자로 확정
                HasWinner = true; Winner = _b.Score > _a.Score ? SplitVariant.B : SplitVariant.A; DecidedUnix = nowUnix;
                decided = true;
            }
            Phase = SplitTestPhase.Closed;
        }
        return decided;
    }

    /// <summary>수동 강제 승자 선택(신뢰도 무시 · 감사 플래그).</summary>
    public bool ForceWinner(SplitVariant v, long nowUnix)
    {
        if (Phase == SplitTestPhase.Idle) return false;
        HasWinner = true; Winner = v; ForcedWinner = true; DecidedUnix = nowUnix;
        if (Phase == SplitTestPhase.Tracking) Phase = SplitTestPhase.Decided;
        return true;
    }
}
