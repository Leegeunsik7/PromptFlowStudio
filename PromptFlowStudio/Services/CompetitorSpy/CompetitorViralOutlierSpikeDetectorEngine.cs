using System.Diagnostics;

namespace PromptFlowStudio.Services.CompetitorSpy;

/// <summary>이상치 클립 1 건(값 형식) — 지표는 모두 채널 기준 대비 상대값.</summary>
public struct OutlierClip
{
    public int Index;
    public float Views, ViewsRatio, AccelRatio, Completion, EngagementPerMille, Score;
}

/// <summary>
/// [PromptFlow Studio v17.99.0] 🕵️ 경쟁 채널 바이럴 이상치(Outlier) 스파이크 감지 엔진(<see cref="Analyze"/> 무할당).
///   ▸ 입력: 최근 업로드 영상 메트릭 시계열 4 축(조회수 · 업로드 경과 시간 · 평균 시청 비율 · 댓글 수)을 <see cref="ReadOnlySpan{T}"/> 로 1 회 스캔.
///   ▸ 기준선: 조회수 중앙값 × 3.0 미만 '평상' 영상만의 채널 평균(조회수 · 시간당 속도) — 이상치가 평균을 끌어올려 서로를 가리는 편향 제거.
///   ▸ 이상치: 조회수 ≥ 기준 × 3.0(300 %). 바이럴 이상치 지수 0~100 =
///     100 × (0.40 · 스파이크 + 0.25 · 가속도 + 0.20 · 완청 추정 + 0.15 · 댓글 참여), 스파이크/가속도 = log₁₀(배율) 0~1 포화, 참여 = 댓글/1000 뷰 10 포화.
///   ▸ 결과: 고정 상한 <see cref="MaxVideos"/> 배열에 지수 내림차순 삽입 정렬.
/// </summary>
public sealed class CompetitorViralOutlierSpikeDetectorEngine
{
    public const int MaxVideos = 64, MinVideos = 3;
    public const float SpikeRatio = 3f, EngagementSaturation = 10f;
    public const float WSpike = 0.40f, WAccel = 0.25f, WCompletion = 0.20f, WEngage = 0.15f;

    private readonly OutlierClip[] _outliers = new OutlierClip[MaxVideos];

    public int VideoCount { get; private set; }
    public int OutlierCount { get; private set; }
    public int Truncated { get; private set; }
    public int NormalCount { get; private set; }
    public float Median { get; private set; }
    public float ChannelMeanViews { get; private set; }
    public float ChannelMeanVelocity { get; private set; }
    public float TopScore => OutlierCount > 0 ? _outliers[0].Score : 0f;
    public long Analyses { get; private set; }
    public double LastAnalyzeUs { get; private set; }
    /// <summary>이상치(0 = 최고 지수).</summary>
    public ref readonly OutlierClip Outlier(int i) => ref _outliers[i];

    /// <summary>
    /// 이상치 스캔(무할당). 길이는 4 축 중 최솟값 · 상한 <see cref="MaxVideos"/>(초과분 = <see cref="Truncated"/>).
    /// NaN/음수는 0 으로 간주. 반환 = 이상치 수.
    /// </summary>
    public int Analyze(ReadOnlySpan<float> views, ReadOnlySpan<float> ageHours, ReadOnlySpan<float> watchRatio, ReadOnlySpan<float> comments)
    {
        var t0 = Stopwatch.GetTimestamp();
        Analyses++;
        var n = Math.Min(Math.Min(views.Length, ageHours.Length), Math.Min(watchRatio.Length, comments.Length));
        Truncated = n > MaxVideos ? n - MaxVideos : 0;
        if (n > MaxVideos) n = MaxVideos;
        VideoCount = n;
        OutlierCount = 0;
        ChannelMeanViews = ChannelMeanVelocity = Median = 0f;
        NormalCount = 0;
        if (n < MinVideos) { LastAnalyzeUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds; return 0; }

        Span<float> sorted = stackalloc float[MaxVideos];
        for (var i = 0; i < n; i++)
        {
            var v = Clean(views[i]);
            var k = i;
            while (k > 0 && sorted[k - 1] > v) { sorted[k] = sorted[k - 1]; k--; }
            sorted[k] = v;
        }
        Median = (n & 1) != 0 ? sorted[n >> 1] : 0.5f * (sorted[(n >> 1) - 1] + sorted[n >> 1]);
        var cut = Median * SpikeRatio;
        double sumViews = 0, sumVel = 0;
        var normal = 0;
        for (var i = 0; i < n; i++)
        {
            var v = Clean(views[i]);
            if (v >= cut) continue;
            sumViews += v;
            sumVel += v / Math.Max(1f, Clean(ageHours[i]));
            normal++;
        }
        NormalCount = normal;
        if (normal == 0) { LastAnalyzeUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds; return 0; }
        ChannelMeanViews = (float)(sumViews / normal);
        ChannelMeanVelocity = (float)(sumVel / normal);

        for (var i = 0; i < n; i++)
        {
            var v = Clean(views[i]);
            var ratio = ChannelMeanViews > 0f ? v / ChannelMeanViews : 0f;
            if (ratio < SpikeRatio) continue;
            var vel = v / Math.Max(1f, Clean(ageHours[i]));
            var accel = ChannelMeanVelocity > 0f ? vel / ChannelMeanVelocity : 10f;
            var completion = Math.Clamp(Clean(watchRatio[i]), 0f, 1f);
            var engage = Clean(comments[i]) * 1000f / v;
            var score = 100f * (WSpike * Log10Sat(ratio) + WAccel * Log10Sat(accel) + WCompletion * completion + WEngage * Math.Min(1f, engage / EngagementSaturation));
            Insert(new OutlierClip { Index = i, Views = v, ViewsRatio = ratio, AccelRatio = accel, Completion = completion, EngagementPerMille = engage, Score = score });
        }
        LastAnalyzeUs = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
        return OutlierCount;
    }

    /// <summary>log₁₀(배율) 0~1 포화(1× = 0 · 10× = 1).</summary>
    public static float Log10Sat(float ratio) => ratio <= 1f ? 0f : Math.Min(1f, MathF.Log10(ratio));

    public void Reset()
    {
        Array.Clear(_outliers);
        VideoCount = OutlierCount = Truncated = NormalCount = 0;
        ChannelMeanViews = ChannelMeanVelocity = Median = 0f;
        Analyses = 0;
        LastAnalyzeUs = 0;
    }

    private void Insert(in OutlierClip c)
    {
        var k = OutlierCount++;
        while (k > 0 && _outliers[k - 1].Score < c.Score) { _outliers[k] = _outliers[k - 1]; k--; }
        _outliers[k] = c;
    }

    private static float Clean(float v) => float.IsNaN(v) || v < 0f ? 0f : v;
}
