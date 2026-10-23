using System.Buffers.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.OmniViral;

/// <summary>
/// [PromptFlow Studio v17.90.0] 📡 실시간 멀티플랫폼 그로스 텔레메트리 애그리게이터.
///   ▸ 입력: YouTube Data API(videos.list statistics — 숫자가 문자열) · YouTube Analytics(reports columnHeaders/rows) ·
///     Instagram Graph API(insights data[].name/values[].value) 응답 UTF-8 을 <see cref="Utf8JsonReader"/> 로 무할당 파싱, 또는 직접 수치.
///   ▸ 정규화: 플랫폼 기준선 대비 좋아요율 · 공유율 · 시청 지속률(평균 시청 ÷ 클립 길이) → 0~100 성장 지수.
///   ▸ K-Factor = i × c (i = 공유율 × 공유당 노출 <see cref="InvitesPerShare"/>, c = 기본 전환 0.10 + 0.30 × 지속률) — 조회수 가중 합산.
///   ▸ <see cref="Evaluate"/>(100 ms 틱 · 무할당): K ≥ 1.2 가 3 틱 연속 + 최소 조회 충족 → 추가 신디케이션 트리거 1 회(K &lt; 1.0 에서 재무장).
/// </summary>
public sealed class MultiPlatformGrowthTelemetryAggregator
{
    public const int Platforms = ViralPipelineContext.Platforms;
    public const double TargetK = 1.2, RearmK = 1.0, InvitesPerShare = 100, MinViews = 1000;
    public const int SustainTicks = 3;

    private static readonly double[] BaseLikeRate = { 0.040, 0.060, 0.080, 0.020 };
    private static readonly double[] BaseShareRate = { 0.004, 0.012, 0.010, 0.006 };

    private readonly long[] _views = new long[Platforms], _likes = new long[Platforms], _shares = new long[Platforms];
    private readonly double[] _watchSec = new double[Platforms], _index = new double[Platforms], _k = new double[Platforms];
    private int _streak;
    private bool _armed = true;

    public double ClipSeconds { get; set; } = 30;
    public double KFactor { get; private set; }
    public double PeakK { get; private set; }
    public double GrowthIndex { get; private set; }
    public long TotalViews { get; private set; }
    public long Evaluations { get; private set; }
    public long Triggers { get; private set; }
    public long ParseErrors { get; private set; }
    public bool TargetReached => KFactor >= TargetK;

    public long Views(int p) => _views[p];
    public long Likes(int p) => _likes[p];
    public long Shares(int p) => _shares[p];
    public double WatchSeconds(int p) => _watchSec[p];
    public double Index(int p) => _index[p];
    public double K(int p) => _k[p];
    public double Retention(int p) => ClipSeconds > 0 ? Math.Clamp(_watchSec[p] / ClipSeconds, 0, 1.5) : 0;

    public void Ingest(int platform, long views, long likes, long shares, double avgWatchSec)
    {
        if ((uint)platform >= Platforms) return;
        _views[platform] = Math.Max(0, views);
        _likes[platform] = Math.Max(0, likes);
        _shares[platform] = Math.Max(0, shares);
        _watchSec[platform] = Math.Max(0, avgWatchSec);
    }

    /// <summary>YouTube Data API videos.list(statistics.viewCount/likeCount 문자열) 또는 Analytics reports(columnHeaders + rows[0]) — 성공 여부.</summary>
    public bool IngestYouTubeJson(ReadOnlySpan<byte> utf8)
    {
        long views = _views[0], likes = _likes[0], shares = _shares[0];
        var watch = _watchSec[0];
        Span<byte> cols = stackalloc byte[16];
        int colCount = 0, rowCol = -1, depthRows = -1;
        var any = false;
        try
        {
            var r = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            byte pending = 0;   // 1 views · 2 likes · 3 shares · 4 avgViewDuration
            var inHeaders = false;
            while (r.Read())
            {
                switch (r.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pending = 0;
                        if (r.ValueTextEquals("viewCount"u8)) pending = 1;
                        else if (r.ValueTextEquals("likeCount"u8)) pending = 2;
                        else if (r.ValueTextEquals("shareCount"u8)) pending = 3;
                        else if (r.ValueTextEquals("columnHeaders"u8)) inHeaders = true;
                        else if (r.ValueTextEquals("rows"u8)) { inHeaders = false; depthRows = r.CurrentDepth; }
                        else if (inHeaders && r.ValueTextEquals("name"u8)) pending = 9;
                        break;
                    case JsonTokenType.String when pending == 9:
                        if (colCount < cols.Length) cols[colCount++] = ColumnCode(ref r);
                        pending = 0;
                        break;
                    case JsonTokenType.StartArray when depthRows >= 0 && r.CurrentDepth == depthRows + 1:
                        rowCol = rowCol < 0 ? 0 : -2;   // 첫 행만
                        break;
                    case JsonTokenType.EndArray when depthRows >= 0 && r.CurrentDepth == depthRows + 1:
                        if (rowCol >= 0) rowCol = -2;
                        break;
                    case JsonTokenType.String or JsonTokenType.Number:
                        if (rowCol >= 0)
                        {
                            if (rowCol < colCount && ReadNumber(ref r, out var cv)) { Assign(cols[rowCol], cv, ref views, ref likes, ref shares, ref watch); any = true; }
                            rowCol++;
                        }
                        else if (pending is >= 1 and <= 4 && ReadNumber(ref r, out var v)) { Assign(pending, v, ref views, ref likes, ref shares, ref watch); any = true; }
                        pending = 0;
                        break;
                }
            }
        }
        catch (JsonException) { ParseErrors++; return false; }
        if (!any) { ParseErrors++; return false; }
        Ingest(0, views, likes, shares, watch);
        return true;
    }

    /// <summary>Instagram Graph API 미디어 인사이트(plays/views · likes · shares · ig_reels_avg_watch_time(ms)) — 성공 여부.</summary>
    public bool IngestInstagramJson(ReadOnlySpan<byte> utf8)
    {
        long views = _views[1], likes = _likes[1], shares = _shares[1];
        var watch = _watchSec[1];
        var any = false;
        try
        {
            var r = new Utf8JsonReader(utf8);
            byte metric = 0;
            var expectName = false; var expectValue = false;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.PropertyName)
                {
                    expectName = r.ValueTextEquals("name"u8);
                    expectValue = r.ValueTextEquals("value"u8);
                    continue;
                }
                if (expectName && r.TokenType == JsonTokenType.String)
                {
                    metric = r.ValueTextEquals("plays"u8) || r.ValueTextEquals("views"u8) ? (byte)1
                        : r.ValueTextEquals("likes"u8) ? (byte)2
                        : r.ValueTextEquals("shares"u8) ? (byte)3
                        : r.ValueTextEquals("ig_reels_avg_watch_time"u8) ? (byte)5 : (byte)0;
                }
                else if (expectValue && metric != 0 && r.TokenType == JsonTokenType.Number && r.TryGetDouble(out var v))
                {
                    if (metric == 5) watch = v / 1000.0; else Assign(metric, v, ref views, ref likes, ref shares, ref watch);
                    any = true;
                }
                expectName = expectValue = false;
            }
        }
        catch (JsonException) { ParseErrors++; return false; }
        if (!any) { ParseErrors++; return false; }
        Ingest(1, views, likes, shares, watch);
        return true;
    }

    private static byte ColumnCode(ref Utf8JsonReader r) =>
        r.ValueTextEquals("views"u8) ? (byte)1 : r.ValueTextEquals("likes"u8) ? (byte)2 : r.ValueTextEquals("shares"u8) ? (byte)3 : r.ValueTextEquals("averageViewDuration"u8) ? (byte)4 : (byte)0;

    private static bool ReadNumber(ref Utf8JsonReader r, out double v)
    {
        if (r.TokenType == JsonTokenType.Number) return r.TryGetDouble(out v);
        v = 0;
        return !r.HasValueSequence && !r.ValueIsEscaped && Utf8Parser.TryParse(r.ValueSpan, out v, out var used) && used == r.ValueSpan.Length;
    }

    private static void Assign(byte code, double v, ref long views, ref long likes, ref long shares, ref double watch)
    {
        switch (code)
        {
            case 1: views = (long)v; break;
            case 2: likes = (long)v; break;
            case 3: shares = (long)v; break;
            case 4: watch = v; break;
        }
    }

    /// <summary>100 ms 판정(무할당) — 이번 틱에 추가 신디케이션을 발화하면 true.</summary>
    public bool Evaluate()
    {
        Evaluations++;
        long total = 0;
        double kSum = 0, idxSum = 0;
        for (var p = 0; p < Platforms; p++)
        {
            var v = _views[p];
            if (v <= 0) { _index[p] = 0; _k[p] = 0; continue; }
            double like = (double)_likes[p] / v, share = (double)_shares[p] / v, ret = Retention(p);
            _index[p] = 100.0 * (0.35 * Math.Min(like / BaseLikeRate[p], 2) / 2 + 0.40 * Math.Min(share / BaseShareRate[p], 2) / 2 + 0.25 * Math.Min(ret / 0.6, 1));
            _k[p] = share * InvitesPerShare * (0.10 + 0.30 * Math.Min(ret, 1));
            total += v;
            kSum += _k[p] * v;
            idxSum += _index[p] * v;
        }
        TotalViews = total;
        KFactor = total > 0 ? kSum / total : 0;
        GrowthIndex = total > 0 ? idxSum / total : 0;
        if (KFactor > PeakK) PeakK = KFactor;

        if (!_armed) { if (KFactor < RearmK) { _armed = true; _streak = 0; } return false; }
        _streak = KFactor >= TargetK && total >= MinViews ? _streak + 1 : 0;
        if (_streak < SustainTicks) return false;
        _armed = false;
        _streak = 0;
        Triggers++;
        return true;
    }

    public void Reset()
    {
        Array.Clear(_views); Array.Clear(_likes); Array.Clear(_shares);
        Array.Clear(_watchSec); Array.Clear(_index); Array.Clear(_k);
        _streak = 0; _armed = true;
        KFactor = PeakK = GrowthIndex = 0;
        TotalViews = Evaluations = Triggers = ParseErrors = 0;
    }
}
