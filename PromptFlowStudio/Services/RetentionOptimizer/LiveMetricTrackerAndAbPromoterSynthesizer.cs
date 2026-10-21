using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.Publisher;
using PromptFlowStudio.Services.VisualPolish;

namespace PromptFlowStudio.Services.RetentionOptimizer;

public enum AbTestPhase : byte { Collecting, Promoting, Promoted, Inconclusive, Failed }

/// <summary>v17.75.0 2×2 요인 셀(제목 × 썸네일) 누적 지표.</summary>
public struct AbCellMetrics
{
    public long Impressions, Clicks, Views;
    public double AvgViewSec;
    public readonly double Ctr => Impressions > 0 ? (double)Clicks / Impressions : 0;
}

/// <summary>v17.75.0 추적 중 A/B 실험 1 건 — 셀 0 = 제목A·썸네일A · 1 = A·B · 2 = B·A · 3 = B·B(셀별 시험 배포 ID) · 승자는 메인 영상(<see cref="VideoId"/>)에 반영.</summary>
public sealed class TrackedAbTest
{
    public const int Cells = 4;
    public int ReelId { get; init; }
    public SocialPlatform Platform { get; init; }
    public string VideoId { get; init; } = "";
    public string[] CellIds { get; } = new string[Cells];
    public AbVariantPair Pair { get; init; } = new();
    public byte[]? ThumbA { get; init; }
    public byte[]? ThumbB { get; init; }
    public double DurationSec { get; init; } = 20;
    public long DeployedAtUnix { get; init; }
    public AbCellMetrics[] Metrics { get; } = new AbCellMetrics[Cells];
    public AbTestPhase Phase { get; internal set; }
    public AbVariant? TitleWinner { get; internal set; }
    public AbVariant? ThumbWinner { get; internal set; }
    public double PTitleB { get; internal set; } = 0.5;
    public double PThumbB { get; internal set; } = 0.5;
    public int Polls { get; internal set; }
    public int PromoteAttempts { get; internal set; }
    public int Faults { get; internal set; }
    public long NextPollUnix { get; internal set; }
    public long NextRetryUnix { get; internal set; }
    public long DecidedAtUnix { get; internal set; }
    public long PromotedAtUnix { get; internal set; }
    public bool TitlePatched { get; internal set; }
    public bool ThumbPatched { get; internal set; }
    public bool RemotePatched => TitlePatched && ThumbPatched;
    public string? Note { get; internal set; }
    public static AbVariant TitleOf(int cell) => cell >= 2 ? AbVariant.B : AbVariant.A;
    public static AbVariant ThumbOf(int cell) => (cell & 1) == 1 ? AbVariant.B : AbVariant.A;
    public static int CellOf(AbVariant title, AbVariant thumb) => (title == AbVariant.B ? 2 : 0) + (thumb == AbVariant.B ? 1 : 0);
    public long TotalImpressions { get { long s = 0; foreach (var m in Metrics) s += m.Impressions; return s; } }
}

/// <summary>
/// [PromptFlow Studio v17.75.0] 📈 실시간 메트릭 추적 &amp; A/B 승자 자동 프로모터 합성기.
///   ▸ 수집: 배포 후 2 h 창 동안 셀별 지표를 5 분 주기 논블로킹 폴링(백그라운드 루프 · 직렬 게이트) — v17.71 <see cref="SocialMediaApiGatewayEngine"/> 볼트 토큰을 헤더 버퍼에 직접 복호화(송신 후 제로화).
///     YouTube = Analytics API v2 <c>reports</c>(videoThumbnailImpressions · ClickRate · views · averageViewDuration) · Instagram = Graph <c>insights</c>(reach · plays · ig_reels_avg_watch_time → 재생 전환률).
///   ▸ 판정: 2×2 요인 주변합(제목 A/B · 썸네일 A/B) CTR 베타 사후 P(B &gt; A)(v17.74 정규 근사) · 각 팔 노출 ≥ 200 · 두 요인 모두 95 % 확신 → 최종 승자.
///     마감(2 h) 시 한 요인만 확정 = 그 요인만 교체 · 미확정 = 결론 없음(원본 유지). 클릭베이트 가드: 승자 셀 평균 시청 &lt; 타 셀 최고의 80 % 면 보류.
///   ▸ 승격: YouTube <c>PUT videos?part=snippet</c>(Data API videos.update — 제목/설명/태그 교체 · 재업로드 없음 = 무중단) → <c>thumbnails/set</c>(PNG) ·
///     요청 단위 지수 백오프(5xx/429/타임아웃 · Retry-After 상한) + 실험 단위 재시도(30 s × 2ⁿ · 최대 5 회) — 한 실험의 실패가 다른 실험 폴링을 막지 않음.
///     Instagram Graph 는 게시 후 캡션/커버 편집 API 가 없어 승자 메타를 기록만 함(원격 미반영 · 차기 게시 프리셋).
/// </summary>
public sealed class LiveMetricTrackerAndAbPromoterSynthesizer : IDisposable
{
    public const int MaxTests = 16, UrlCap = 1024, HdrCap = 4096, BodyCap = 16 * 1024, MaxRetries = 3, MaxPromoteAttempts = 5, RetentionPoints = 100;
    public const long PollIntervalSec = 300, WindowSec = 7200;
    public const double Confidence = AbTestingMetadataVariantGenerator.Confidence, ClickbaitRatio = 0.8;
    public static ReadOnlySpan<byte> AnalyticsBase => "https://youtubeanalytics.googleapis.com/v2/reports?ids=channel%3D%3DMINE&startDate="u8;
    public static ReadOnlySpan<byte> VideosUpdateUrl => "https://www.googleapis.com/youtube/v3/videos?part=snippet"u8;
    public static ReadOnlySpan<byte> ThumbnailSetBase => "https://www.googleapis.com/upload/youtube/v3/thumbnails/set?videoId="u8;

    private readonly TrackedAbTest?[] _tests = new TrackedAbTest?[MaxTests];
    private readonly byte[] _url = new byte[UrlCap], _hdr = new byte[HdrCap], _body = new byte[BodyCap];
    private readonly GraphResponseBuffer _resp = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AbTestingMetadataVariantGenerator? _ab;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private int _n;

    public LiveMetricTrackerAndAbPromoterSynthesizer(SocialMediaApiGatewayEngine gateway, AbTestingMetadataVariantGenerator? ab = null) { Gateway = gateway; _ab = ab; UnixNow = () => Gateway.UnixNow(); }

    public SocialMediaApiGatewayEngine Gateway { get; set; }
    public Func<long> UnixNow { get; set; }
    public int RequestTimeoutMs { get; set; } = 10_000;
    public int RetryBaseMs { get; set; } = 250;
    public int Count => _n;
    public TrackedAbTest? At(int i) => (uint)i < (uint)_n ? _tests[i] : null;
    public TrackedAbTest? Find(int reelId, SocialPlatform p) { for (var i = 0; i < _n; i++) if (_tests[i] is { } t && t.ReelId == reelId && t.Platform == p) return t; return null; }
    public int Active { get { var a = 0; for (var i = 0; i < _n; i++) if (_tests[i]?.Phase is AbTestPhase.Collecting or AbTestPhase.Promoting) a++; return a; } }
    public bool Running => _loop is { IsCompleted: false };
    public long Ticks { get; private set; }
    public long PollsDone { get; private set; }
    public long MetricRequests { get; private set; }
    public long MetricFailures { get; private set; }
    public long Decisions { get; private set; }
    public long Promotions { get; private set; }
    public long PromoteFailures { get; private set; }
    public long Retries { get; private set; }
    public long Inconclusives { get; private set; }
    public long Duplicates { get; private set; }
    public double LastTickMs { get; private set; }
    public string? LastError { get; private set; }
    public event Action<TrackedAbTest>? OnPhase;

    public static string PhaseName(AbTestPhase p) => p switch
    {
        AbTestPhase.Collecting => "수집 중", AbTestPhase.Promoting => "승격 중", AbTestPhase.Promoted => "승자 확정", AbTestPhase.Inconclusive => "결론 없음", _ => "실패",
    };

    /// <summary>실험 등록(같은 릴·플랫폼 중복 차단 · 가득 차면 종료된 가장 오래된 실험 교체).</summary>
    public bool Track(TrackedAbTest t)
    {
        lock (_tests)
        {
            if (Find(t.ReelId, t.Platform) is not null) { Duplicates++; return false; }
            if (_n == MaxTests)
            {
                var victim = -1;
                for (var i = 0; i < _n; i++) if (_tests[i]?.Phase is AbTestPhase.Promoted or AbTestPhase.Inconclusive or AbTestPhase.Failed) { victim = i; break; }
                if (victim < 0) return false;
                Array.Copy(_tests, victim + 1, _tests, victim, _n - victim - 1); _n--;
            }
            t.NextPollUnix = t.DeployedAtUnix + PollIntervalSec;
            _tests[_n++] = t;
            return true;
        }
    }

    // ══════════════════════ 백그라운드 루프 ══════════════════════
    public void Start(int tickMs = 1000)
    {
        if (Running) return;
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { LastError = ex.Message; PromptFlowStudio.Program.Log("Retention.Promoter.Tick", ex); }
                try { await Task.Delay(tickMs, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }, ct);
    }

    public void Stop()
    {
        _loopCts?.Cancel();
        try { _loop?.Wait(5_000); } catch (AggregateException) { }
        _loopCts?.Dispose(); _loopCts = null; _loop = null;
    }

    /// <summary>1 틱: 폴링 기한 도래 실험 수집 → 판정 → 승격 대기 실험 처리. 반환 = 이번 틱 폴링 수.</summary>
    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var polled = 0;
        try
        {
            Ticks++;
            for (var i = 0; i < _n; i++)
            {
                var t = _tests[i];
                if (t is null) continue;
                var now = UnixNow();
                if (t.Phase == AbTestPhase.Collecting && now >= t.NextPollUnix)
                {
                    await PollAsync(t, ct).ConfigureAwait(false);
                    t.NextPollUnix = now + PollIntervalSec;
                    polled++;
                    Evaluate(t, UnixNow());
                }
                if (t.Phase == AbTestPhase.Promoting && UnixNow() >= t.NextRetryUnix) await PromoteAsync(t, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            LastTickMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _gate.Release();
        }
        return polled;
    }

    // ══════════════════════ 수집 ══════════════════════
    private async ValueTask PollAsync(TrackedAbTest t, CancellationToken ct)
    {
        PollsDone++; t.Polls++;
        var now = UnixNow();
        for (var c = 0; c < TrackedAbTest.Cells; c++)
        {
            if (string.IsNullOrEmpty(t.CellIds[c])) continue;
            var len = t.Platform == SocialPlatform.YouTube ? BuildYtMetricsUrl(t.CellIds[c], t.DeployedAtUnix, now) : BuildIgMetricsUrl(t.CellIds[c]);
            MetricRequests++;
            var st = await SendAsync(GraphHttpMethod.Get, t.Platform, len, default, default, ct).ConfigureAwait(false);
            if (st != 200 || !(t.Platform == SocialPlatform.YouTube ? ParseYtMetrics(_resp.BodySpan, ref t.Metrics[c]) : ParseIgMetrics(_resp.BodySpan, ref t.Metrics[c])))
            {
                MetricFailures++; t.Faults++; LastError = "지표 수집 실패 HTTP " + st.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }

    private int BuildYtMetricsUrl(string id, long sinceUnix, long nowUnix)
    {
        var n = 0; var u = _url.AsSpan();
        GraphBytes.Put(u, ref n, AnalyticsBase); PutDate(u, ref n, sinceUnix);
        GraphBytes.Put(u, ref n, "&endDate="u8); PutDate(u, ref n, nowUnix);
        GraphBytes.Put(u, ref n, "&metrics=videoThumbnailImpressions,videoThumbnailImpressionsClickRate,views,averageViewDuration&filters=video%3D%3D"u8);
        n += Encoding.ASCII.GetBytes(id, u[n..]);
        return n;
    }

    private int BuildIgMetricsUrl(string id)
    {
        var n = 0; var u = _url.AsSpan();
        GraphBytes.Put(u, ref n, SocialMediaApiGatewayEngine.GraphBase); n += Encoding.ASCII.GetBytes(id, u[n..]);
        GraphBytes.Put(u, ref n, "/insights?metric=reach,plays,ig_reels_avg_watch_time"u8);
        return n;
    }

    /// <summary>yyyy-MM-dd(UTC · 무할당).</summary>
    public static void PutDate(Span<byte> d, ref int n, long unix)
    {
        var dt = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, unix)).UtcDateTime;
        Put2(d, ref n, dt.Year / 100); Put2(d, ref n, dt.Year % 100); d[n++] = (byte)'-'; Put2(d, ref n, dt.Month); d[n++] = (byte)'-'; Put2(d, ref n, dt.Day);
        static void Put2(Span<byte> d, ref int n, int v) { d[n++] = (byte)('0' + v / 10); d[n++] = (byte)('0' + v % 10); }
    }

    /// <summary>Analytics 결과표 <c>"rows":[[imp, ctr%, views, avgSec]]</c> → 셀(무할당).</summary>
    public static bool ParseYtMetrics(ReadOnlySpan<byte> json, ref AbCellMetrics m)
    {
        var i = json.IndexOf("\"rows\""u8); if (i < 0) return false;
        var s = json[(i + 6)..]; var b = s.IndexOf("[["u8); if (b < 0) return false;
        s = s[(b + 2)..];
        Span<double> v = stackalloc double[4];
        for (var k = 0; k < 4; k++)
        {
            s = s.TrimStart(" \t\r\n,"u8);
            if (!Utf8Parser.TryParse(s, out double x, out var w)) return false;
            v[k] = x; s = s[w..];
        }
        if (v[0] < 0 || v[1] < 0 || v[1] > 100 || v[2] < 0 || v[3] < 0) return false;
        m.Impressions = (long)v[0]; m.Clicks = (long)Math.Round(v[0] * v[1] / 100.0); m.Views = (long)v[2]; m.AvgViewSec = v[3];
        return true;
    }

    /// <summary>Graph insights <c>data[].name/values[0].value</c> → 셀(reach = 노출 · plays = 재생 · 평균 시청 ms).</summary>
    public static bool ParseIgMetrics(ReadOnlySpan<byte> json, ref AbCellMetrics m)
    {
        long reach = Metric(json, "\"name\":\"reach\""u8), plays = Metric(json, "\"name\":\"plays\""u8), avgMs = Metric(json, "\"name\":\"ig_reels_avg_watch_time\""u8);
        if (reach < 0 || plays < 0 || avgMs < 0) return false;
        m.Impressions = reach; m.Clicks = Math.Min(reach, plays); m.Views = plays; m.AvgViewSec = avgMs / 1000.0;
        return true;

        static long Metric(ReadOnlySpan<byte> json, ReadOnlySpan<byte> key)
        {
            var i = json.IndexOf(key); if (i < 0) return -1;
            var s = json[(i + key.Length)..]; var v = s.IndexOf("\"value\":"u8); if (v < 0) return -1;
            s = s[(v + 8)..].TrimStart(" "u8);
            return Utf8Parser.TryParse(s, out long x, out _) ? x : -1;
        }
    }

    // ══════════════════════ 판정 ══════════════════════
    /// <summary>요인 주변합 판정 → 승격 대기 · 마감 처리(동기 · 무할당).</summary>
    public void Evaluate(TrackedAbTest t, long now)
    {
        if (t.Phase != AbTestPhase.Collecting) return;
        var m = t.Metrics;
        long tAc = m[0].Clicks + m[1].Clicks, tAn = m[0].Impressions + m[1].Impressions, tBc = m[2].Clicks + m[3].Clicks, tBn = m[2].Impressions + m[3].Impressions;
        long hAc = m[0].Clicks + m[2].Clicks, hAn = m[0].Impressions + m[2].Impressions, hBc = m[1].Clicks + m[3].Clicks, hBn = m[1].Impressions + m[3].Impressions;
        var deadline = now - t.DeployedAtUnix >= WindowSec;
        var min = AbTestingMetadataVariantGenerator.MinImpressions;
        var titleReady = tAn >= min && tBn >= min; var thumbReady = hAn >= min && hBn >= min;
        if (titleReady) t.PTitleB = AbTestingMetadataVariantGenerator.ProbabilityBBeatsA(tAc, tAn, tBc, tBn);
        if (thumbReady) t.PThumbB = AbTestingMetadataVariantGenerator.ProbabilityBBeatsA(hAc, hAn, hBc, hBn);
        var titleSure = titleReady && (t.PTitleB >= Confidence || t.PTitleB <= 1 - Confidence);
        var thumbSure = thumbReady && (t.PThumbB >= Confidence || t.PThumbB <= 1 - Confidence);
        if ((titleSure && thumbSure) || (deadline && (titleSure || thumbSure)))
        {
            var tw = titleSure && t.PTitleB >= Confidence ? AbVariant.B : AbVariant.A;
            var hw = thumbSure && t.PThumbB >= Confidence ? AbVariant.B : AbVariant.A;
            var win = TrackedAbTest.CellOf(tw, hw);
            var best = 0.0;
            for (var c = 0; c < TrackedAbTest.Cells; c++) if (c != win && m[c].Views >= 30 && m[c].AvgViewSec > best) best = m[c].AvgViewSec;
            if (m[win].Views >= 30 && m[win].AvgViewSec < ClickbaitRatio * best)
            {
                t.Note = "클릭베이트 의심(승자 평균 시청 " + m[win].AvgViewSec.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s < 타 셀 80 %) — 보류";
                if (deadline) Finish(t, AbTestPhase.Inconclusive);
                return;
            }
            t.TitleWinner = tw; t.ThumbWinner = hw; t.DecidedAtUnix = now; t.NextRetryUnix = now;
            Decisions++;
            if (_ab is not null && t.Pair.Chosen is null) _ab.Choose(t.Pair, tw);
            t.Note = "승자 제목 " + tw + " + 썸네일 " + hw + (titleSure && thumbSure ? "" : " (마감 부분 확정)");
            Set(t, AbTestPhase.Promoting);
            return;
        }
        if (deadline) { t.Note = "2 h 창 마감 — 95 % 미달(원본 유지)"; Finish(t, AbTestPhase.Inconclusive); }
    }

    private void Finish(TrackedAbTest t, AbTestPhase p) { if (p == AbTestPhase.Inconclusive) Inconclusives++; Set(t, p); }

    private void Set(TrackedAbTest t, AbTestPhase p)
    {
        t.Phase = p;
        try { OnPhase?.Invoke(t); } catch (Exception ex) { PromptFlowStudio.Program.Log("Retention.Promoter.OnPhase", ex); }
    }

    // ══════════════════════ 승격 ══════════════════════
    private async ValueTask PromoteAsync(TrackedAbTest t, CancellationToken ct)
    {
        t.PromoteAttempts++;
        var now = UnixNow();
        if (t.Platform == SocialPlatform.Instagram)
        {
            t.Note = "IG Graph: 게시 후 캡션/커버 편집 API 미지원 — 승자 메타 기록(차기 게시 프리셋)";
            t.PromotedAtUnix = now; Promotions++; Set(t, AbTestPhase.Promoted);
            return;
        }
        var meta = t.Pair.Get(t.TitleWinner ?? AbVariant.A);
        if (!t.TitlePatched)
        {
            var bodyLen = BuildSnippetBody(t.VideoId, meta);
            var n = 0; GraphBytes.Put(_url, ref n, VideosUpdateUrl);
            var st = await SendAsync(GraphHttpMethod.Put, t.Platform, n, _body.AsMemory(0, bodyLen), JsonType, ct).ConfigureAwait(false);
            if (st == 200 && SocialMediaApiGatewayEngine.JsonField(_resp.BodySpan, "\"id\""u8).SequenceEqual(Encoding.ASCII.GetBytes(t.VideoId))) t.TitlePatched = true;
            else { PromoteFail(t, now, "videos.update HTTP " + st.ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
        }
        if (!t.ThumbPatched)
        {
            var png = t.ThumbWinner == AbVariant.B ? t.ThumbB : t.ThumbA;
            if (png is null || png.Length < 8) t.ThumbPatched = true;
            else
            {
                var n = 0; GraphBytes.Put(_url, ref n, ThumbnailSetBase); n += Encoding.ASCII.GetBytes(t.VideoId, _url.AsSpan(n));
                var st = await SendAsync(GraphHttpMethod.Post, t.Platform, n, png, PngType, ct).ConfigureAwait(false);
                if (st == 200) t.ThumbPatched = true;
                else { PromoteFail(t, now, "thumbnails.set HTTP " + st.ToString(System.Globalization.CultureInfo.InvariantCulture)); return; }
            }
        }
        t.PromotedAtUnix = now; Promotions++;
        Set(t, AbTestPhase.Promoted);
    }

    private static readonly byte[] PngType = "image/png"u8.ToArray(), JsonType = "application/json; charset=UTF-8"u8.ToArray();

    private void PromoteFail(TrackedAbTest t, long now, string why)
    {
        PromoteFailures++; LastError = why;
        if (t.PromoteAttempts >= MaxPromoteAttempts) { t.Note = "승격 실패(" + why + ") — 재시도 소진"; Set(t, AbTestPhase.Failed); return; }
        t.NextRetryUnix = now + (30L << Math.Min(10, t.PromoteAttempts));
        t.Note = "승격 재시도 대기(" + why + ")";
    }

    /// <summary>videos.update 본문(snippet 전체 교체 · 태그 # 제거 · categoryId 필수).</summary>
    private int BuildSnippetBody(string id, SocialPostMetadata meta)
    {
        var d = _body.AsSpan(); var n = 0;
        GraphBytes.Put(d, ref n, "{\"id\":"u8); MultipartBodyWriter.JsonString(d, ref n, id);
        GraphBytes.Put(d, ref n, ",\"snippet\":{\"title\":"u8); MultipartBodyWriter.JsonString(d, ref n, meta.Title.AsSpan(0, Math.Min(meta.Title.Length, 100)));
        GraphBytes.Put(d, ref n, ",\"description\":"u8); MultipartBodyWriter.JsonString(d, ref n, meta.Description.AsSpan(0, Math.Min(meta.Description.Length, 4000)));
        GraphBytes.Put(d, ref n, ",\"tags\":["u8);
        for (var i = 0; i < meta.TagCount; i++) { if (i > 0) d[n++] = (byte)','; MultipartBodyWriter.JsonString(d, ref n, meta.Tags[i].AsSpan().TrimStart('#')); }
        GraphBytes.Put(d, ref n, "],\"categoryId\":\"22\"}}"u8);
        return n;
    }

    // ══════════════════════ 유지 곡선 ══════════════════════
    /// <summary>YouTube 시청 유지 곡선(elapsedVideoTimeRatio 1 % 간격 audienceWatchRatio) → dst · 점 수(0 = 실패 · IG 는 API 부재 = 0).</summary>
    public async Task<int> FetchRetentionAsync(SocialPlatform p, string videoId, long sinceUnix, float[] dst, CancellationToken ct = default)
    {
        if (p != SocialPlatform.YouTube) return 0;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var n = BuildRetentionUrl(videoId, sinceUnix, UnixNow());
            MetricRequests++;
            var st = await SendAsync(GraphHttpMethod.Get, p, n, default, default, ct).ConfigureAwait(false);
            if (st != 200) { MetricFailures++; return 0; }
            return ParseRetention(_resp.BodySpan, dst);
        }
        finally { _gate.Release(); }
    }

    private int BuildRetentionUrl(string videoId, long sinceUnix, long nowUnix)
    {
        var n = 0; var u = _url.AsSpan();
        GraphBytes.Put(u, ref n, AnalyticsBase); PutDate(u, ref n, sinceUnix);
        GraphBytes.Put(u, ref n, "&endDate="u8); PutDate(u, ref n, nowUnix);
        GraphBytes.Put(u, ref n, "&dimensions=elapsedVideoTimeRatio&metrics=audienceWatchRatio&filters=video%3D%3D"u8);
        n += Encoding.ASCII.GetBytes(videoId, u[n..]);
        return n;
    }

    /// <summary><c>"rows":[[ratio, watch], …]</c> → dst[i](무할당).</summary>
    public static int ParseRetention(ReadOnlySpan<byte> json, Span<float> dst)
    {
        var i = json.IndexOf("\"rows\""u8); if (i < 0) return 0;
        var s = json[(i + 6)..]; var b = s.IndexOf((byte)'['); if (b < 0) return 0;
        s = s[(b + 1)..];
        var k = 0;
        while (k < dst.Length)
        {
            var o = s.IndexOf((byte)'['); if (o < 0) break;
            s = s[(o + 1)..];
            if (!Utf8Parser.TryParse(s.TrimStart(" "u8), out double _, out var w0)) break;
            s = s.TrimStart(" "u8)[w0..].TrimStart(" ,"u8);
            if (!Utf8Parser.TryParse(s, out double watch, out var w1)) break;
            s = s[w1..];
            dst[k++] = (float)Math.Clamp(watch, 0, 1.5);
        }
        return k;
    }

    // ══════════════════════ 전송(헤더 무할당 · 지수 백오프) ══════════════════════
    private int BuildHeaders(SocialPlatform p, ReadOnlySpan<byte> contentType)
    {
        var n = 0;
        Gateway.WriteAuthHeader(p, _hdr, ref n);
        if (!contentType.IsEmpty) { GraphBytes.Put(_hdr, ref n, "Content-Type: "u8); GraphBytes.Put(_hdr, ref n, contentType); GraphBytes.Put(_hdr, ref n, "\r\n"u8); }
        return n;
    }

    private async ValueTask<int> SendAsync(GraphHttpMethod m, SocialPlatform p, int urlLen, ReadOnlyMemory<byte> body, ReadOnlyMemory<byte> contentType, CancellationToken ct)
    {
        var st = 0;
        for (var attempt = 0; attempt <= MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                Retries++;
                var wait = Math.Min(8_000, RetryBaseMs << (attempt - 1));
                if (_resp.RetryAfterSec > 0) wait = Math.Min(8_000, Math.Max(wait, _resp.RetryAfterSec * 1000));
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }
            var hl = BuildHeaders(p, contentType.Span);
            _resp.Reset();
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeoutMs);
                st = await Gateway.Transport.SendAsync(m, _url.AsMemory(0, urlLen), _hdr.AsMemory(0, hl), body, _resp, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { st = 0; }
            catch (HttpRequestException) { st = 0; }
            finally { CryptographicOperations.ZeroMemory(_hdr.AsSpan(0, hl)); }
            if (st is not (0 or 429 or >= 500)) return st;
        }
        return st;
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
        OnPhase = null;
    }
}

/// <summary>
/// v17.75.0 YouTube Analytics v2 · Data API videos.update/thumbnails.set · Graph insights 시뮬레이터(동기 완료 · 결정적 · 회귀/데모).
///   ▸ 영상 프로필(숨은 CTR · 평균 시청 · 시간당 노출 · 배포 시각 · 유지 곡선 훅/중반 낙폭)을 시계 기준 누적 지표로 응답 · Bearer/OAuth 토큰 고정시간 비교.
///   ▸ videos.update: JSON id 일치 · title 1~100 · categoryId 필수 · thumbnails/set: PNG 시그니처 · ≤ 2 MB. 장애 주입(<see cref="FailNext"/>).
/// </summary>
public sealed class SimulatedRetentionAnalyticsServer : IGraphTransport
{
    public const int MaxVideos = 64;

    private sealed class Profile
    {
        public string Id = "";
        public double Ctr, AvgViewSec, ImpressionsPerHour, DurationSec, HookDrop, MidDropAt, MidDrop;
        public long DeployedAt;
    }

    private readonly Profile[] _v = new Profile[MaxVideos];
    private readonly byte[] _yt = new byte[64], _ig = new byte[64];
    private int _n, _ytLen, _igLen, _failStatus, _failCount;

    public SimulatedRetentionAnalyticsServer(Func<long> clock) => Clock = clock;

    public Func<long> Clock { get; set; }
    public long Requests { get; private set; }
    public long Unauthorized { get; private set; }
    public long Rejects { get; private set; }
    public long MetricQueries { get; private set; }
    public long Updates { get; private set; }
    public long ThumbnailSets { get; private set; }
    public long Injected { get; private set; }
    public string? LastUpdatedId { get; private set; }
    public string? LastUpdatedTitle { get; private set; }
    public int LastTagCount { get; private set; }
    public int LastThumbBytes { get; private set; }
    public string? LastReject { get; private set; }

    public void Issue(SocialPlatform p, ReadOnlySpan<byte> token)
    {
        if (p == SocialPlatform.YouTube) { token.CopyTo(_yt); _ytLen = token.Length; } else { token.CopyTo(_ig); _igLen = token.Length; }
    }

    public void FailNext(int status, int count = 1) { _failStatus = status; _failCount = count; }

    /// <summary>영상 프로필 등록(같은 ID = 갱신).</summary>
    public void Register(string id, double ctr, double avgViewSec, double impressionsPerHour, long deployedAt, double durationSec = 20, double hookDrop = 0.25, double midDropAt = 0.5, double midDrop = 0.15)
    {
        var p = FindProfile(id);
        if (p is null) { if (_n == MaxVideos) return; p = _v[_n++] = new Profile { Id = id }; }
        p.Ctr = ctr; p.AvgViewSec = avgViewSec; p.ImpressionsPerHour = impressionsPerHour; p.DeployedAt = deployedAt; p.DurationSec = durationSec; p.HookDrop = hookDrop; p.MidDropAt = midDropAt; p.MidDrop = midDrop;
    }

    private Profile? FindProfile(ReadOnlySpan<char> id) { for (var i = 0; i < _n; i++) if (id.SequenceEqual(_v[i].Id)) return _v[i]; return null; }

    private Profile? FindProfile(ReadOnlySpan<byte> id)
    {
        Span<char> c = stackalloc char[Math.Min(id.Length, 128)];
        var n = Encoding.ASCII.GetChars(id[..c.Length], c);
        return FindProfile(c[..n]);
    }

    /// <summary>유지 곡선 모형(시점 비율 r ∈ [0,1]) — 3 s 훅 낙폭 + 중반 시그모이드 낙폭 + 완만한 지수 감쇠.</summary>
    public static double RetentionAt(double r, double durationSec, double hookDrop, double midDropAt, double midDrop)
    {
        var sec = r * durationSec;
        var hook = hookDrop * Math.Min(1, sec / 3.0);
        var mid = midDrop / (1 + Math.Exp(-(r - midDropAt) * 40));
        return Math.Clamp((1 - hook - mid) * Math.Exp(-0.25 * r), 0, 1);
    }

    public ValueTask<int> SendAsync(GraphHttpMethod method, ReadOnlyMemory<byte> url, ReadOnlyMemory<byte> headers, ReadOnlyMemory<byte> body, GraphResponseBuffer resp, CancellationToken ct)
        => new(Handle(method, url.Span, headers.Span, body.Span, resp));

    private int Reject(GraphResponseBuffer r, int st, string why) { Rejects++; LastReject = why; return Body(r, st, "{\"error\":{\"message\":\"rejected\"}}"u8); }
    private static int Body(GraphResponseBuffer r, int st, ReadOnlySpan<byte> json) { json.CopyTo(r.Body); r.Length = json.Length; return st; }

    private bool Authorized(SocialPlatform p, ReadOnlySpan<byte> headers)
    {
        var auth = GraphBytes.Header(headers, "Authorization"u8);
        var tok = p == SocialPlatform.YouTube ? _yt.AsSpan(0, _ytLen) : _ig.AsSpan(0, _igLen);
        ReadOnlySpan<byte> given = auth.StartsWith("Bearer "u8) ? auth[7..] : auth.StartsWith("OAuth "u8) ? auth[6..] : default;
        var ok = !tok.IsEmpty && given.Length == tok.Length && CryptographicOperations.FixedTimeEquals(given, tok);
        if (!ok) Unauthorized++;
        return ok;
    }

    private static ReadOnlySpan<byte> After(ReadOnlySpan<byte> s, ReadOnlySpan<byte> key)
    {
        var i = s.IndexOf(key); if (i < 0) return default;
        var v = s[(i + key.Length)..]; var e = v.IndexOf((byte)'&');
        return e < 0 ? v : v[..e];
    }

    private int Handle(GraphHttpMethod m, ReadOnlySpan<byte> url, ReadOnlySpan<byte> headers, ReadOnlySpan<byte> body, GraphResponseBuffer r)
    {
        Requests++;
        if (_failCount > 0) { _failCount--; Injected++; r.RetryAfterSec = _failStatus == 429 ? 1 : 0; return Body(r, _failStatus, "{\"error\":{\"code\":503}}"u8); }
        var now = Clock();
        if (url.StartsWith("https://youtubeanalytics.googleapis.com/v2/reports?"u8))
        {
            if (m != GraphHttpMethod.Get) return Reject(r, 405, "method");
            if (!Authorized(SocialPlatform.YouTube, headers)) return Body(r, 401, "{\"error\":{\"code\":401}}"u8);
            if (After(url, "ids="u8).IsEmpty || After(url, "startDate="u8).Length != 10 || After(url, "endDate="u8).Length != 10) return Reject(r, 400, "params");
            var p = FindProfile(After(url, "filters=video%3D%3D"u8));
            if (p is null) return Reject(r, 404, "video");
            MetricQueries++;
            var d = r.Body.AsSpan(); var n = 0;
            if (url.IndexOf("dimensions=elapsedVideoTimeRatio"u8) >= 0)
            {
                GraphBytes.Put(d, ref n, "{\"kind\":\"youtubeAnalytics#resultTable\",\"rows\":["u8);
                for (var k = 1; k <= LiveMetricTrackerAndAbPromoterSynthesizer.RetentionPoints; k++)
                {
                    if (k > 1) d[n++] = (byte)',';
                    var ratio = k / 100.0;
                    d[n++] = (byte)'['; Utf8Formatter.TryFormat(ratio, d[n..], out var w, new System.Buffers.StandardFormat('F', 2)); n += w; d[n++] = (byte)',';
                    Utf8Formatter.TryFormat(RetentionAt(ratio, p.DurationSec, p.HookDrop, p.MidDropAt, p.MidDrop), d[n..], out w, new System.Buffers.StandardFormat('F', 4)); n += w; d[n++] = (byte)']';
                }
                GraphBytes.Put(d, ref n, "]}"u8);
                r.Length = n; return 200;
            }
            var hours = Math.Max(0, now - p.DeployedAt) / 3600.0;
            var imp = (long)(p.ImpressionsPerHour * hours);
            var views = (long)Math.Round(imp * p.Ctr);
            GraphBytes.Put(d, ref n, "{\"kind\":\"youtubeAnalytics#resultTable\",\"columnHeaders\":[],\"rows\":[["u8);
            GraphBytes.Put(d, ref n, imp); d[n++] = (byte)',';
            Utf8Formatter.TryFormat(p.Ctr * 100, d[n..], out var w2, new System.Buffers.StandardFormat('F', 4)); n += w2; d[n++] = (byte)',';
            GraphBytes.Put(d, ref n, views); d[n++] = (byte)',';
            Utf8Formatter.TryFormat(p.AvgViewSec, d[n..], out w2, new System.Buffers.StandardFormat('F', 2)); n += w2;
            GraphBytes.Put(d, ref n, "]]}"u8);
            r.Length = n; return 200;
        }
        if (url.SequenceEqual(LiveMetricTrackerAndAbPromoterSynthesizer.VideosUpdateUrl))
        {
            if (m != GraphHttpMethod.Put) return Reject(r, 405, "videos.update method");
            if (!Authorized(SocialPlatform.YouTube, headers)) return Body(r, 401, "{\"error\":{\"code\":401}}"u8);
            if (!GraphBytes.Header(headers, "Content-Type"u8).StartsWith("application/json"u8)) return Reject(r, 400, "ctype");
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body.ToArray());
                var root = doc.RootElement;
                var id = root.GetProperty("id").GetString() ?? "";
                var sn = root.GetProperty("snippet");
                var title = sn.GetProperty("title").GetString() ?? "";
                if (FindProfile(id.AsSpan()) is null) return Reject(r, 404, "videoNotFound");
                if (title.Length is 0 or > 100) return Reject(r, 400, "title length");
                if (!sn.TryGetProperty("categoryId", out _)) return Reject(r, 400, "categoryId");
                LastTagCount = sn.TryGetProperty("tags", out var tags) ? tags.GetArrayLength() : 0;
                LastUpdatedId = id; LastUpdatedTitle = title; Updates++;
                var d = r.Body.AsSpan(); var n = 0;
                GraphBytes.Put(d, ref n, "{\"kind\":\"youtube#video\",\"id\":\""u8); n += Encoding.ASCII.GetBytes(id, d[n..]); GraphBytes.Put(d, ref n, "\"}"u8);
                r.Length = n; return 200;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { return Reject(r, 400, "json"); }
        }
        if (url.StartsWith(LiveMetricTrackerAndAbPromoterSynthesizer.ThumbnailSetBase))
        {
            if (m != GraphHttpMethod.Post) return Reject(r, 405, "thumb method");
            if (!Authorized(SocialPlatform.YouTube, headers)) return Body(r, 401, "{\"error\":{\"code\":401}}"u8);
            if (FindProfile(url[LiveMetricTrackerAndAbPromoterSynthesizer.ThumbnailSetBase.Length..]) is null) return Reject(r, 404, "thumb video");
            if (!GraphBytes.Header(headers, "Content-Type"u8).SequenceEqual("image/png"u8) || body.Length < 8 || !body[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return Reject(r, 400, "png");
            if (body.Length > 2 * 1024 * 1024) return Reject(r, 413, "thumb size");
            ThumbnailSets++; LastThumbBytes = body.Length;
            return Body(r, 200, "{\"kind\":\"youtube#thumbnailSetResponse\",\"items\":[{\"default\":{}}]}"u8);
        }
        if (url.StartsWith(SocialMediaApiGatewayEngine.GraphBase) && url.IndexOf("/insights?"u8) > 0)
        {
            if (!Authorized(SocialPlatform.Instagram, headers)) return Body(r, 401, "{\"error\":{\"code\":190}}"u8);
            var path = url[SocialMediaApiGatewayEngine.GraphBase.Length..];
            var p = FindProfile(path[..path.IndexOf((byte)'/')]);
            if (p is null) return Reject(r, 404, "media");
            MetricQueries++;
            var hours = Math.Max(0, now - p.DeployedAt) / 3600.0;
            var reach = (long)(p.ImpressionsPerHour * hours); var plays = (long)Math.Round(reach * p.Ctr);
            var d = r.Body.AsSpan(); var n = 0;
            GraphBytes.Put(d, ref n, "{\"data\":[{\"name\":\"reach\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8); GraphBytes.Put(d, ref n, reach);
            GraphBytes.Put(d, ref n, "}]},{\"name\":\"plays\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8); GraphBytes.Put(d, ref n, plays);
            GraphBytes.Put(d, ref n, "}]},{\"name\":\"ig_reels_avg_watch_time\",\"period\":\"lifetime\",\"values\":[{\"value\":"u8); GraphBytes.Put(d, ref n, (long)(p.AvgViewSec * 1000));
            GraphBytes.Put(d, ref n, "}]}]}"u8);
            r.Length = n; return 200;
        }
        return Reject(r, 404, "unknown");
    }
}
