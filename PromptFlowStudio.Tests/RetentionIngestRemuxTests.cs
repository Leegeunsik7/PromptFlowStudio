using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using PromptFlowStudio.Services.RetentionHeatmap;
using Xunit.Abstractions;

namespace PromptFlowStudio.Tests;

/// <summary>
/// v17.99.1 — YouTube Analytics 유지율 수집기(모의 · 실응답 파싱 · 429 쿨다운 · 단절/인증/형식 폴백) ·
/// NALU 스마트 리먹서(H.264/H.265 무손실 · GOP 스냅 · 파라미터 셋 주입 · PTS/DTS 재타이밍 · 무할당) · 러너 결속 · 장기 소크.
/// 외부 네트워크 미사용(가짜 <see cref="HttpMessageHandler"/>).
/// </summary>
[Collection("ProcessGcMode")]   // 무할당 · 메모리/핸들 델타 계측 → 직렬화
[Trait("Category", "Integration")]
public sealed class RetentionIngestRemuxTests
{
    private readonly ITestOutputHelper _out;
    public RetentionIngestRemuxTests(ITestOutputHelper output) => _out = output;

    // ══════════════════════ 가짜 HTTP ══════════════════════

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        public int Calls, TokenCalls;
        public string? LastAuth;
        public Uri? LastUri;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsoluteUri.StartsWith(YouTubeAnalyticsRetentionCollector.TokenEndpoint, StringComparison.Ordinal))
            {
                TokenCalls++;
                return Task.FromResult(Json("{\"access_token\":\"tok-refreshed\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"));
            }
            Calls++;
            LastAuth = request.Headers.Authorization?.ToString();
            LastUri = request.RequestUri;
            return Task.FromResult(Respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>API 응답 형태(resultTable) — 100 점 · 0.01 간격 · 25 % 지점 급락.</summary>
    private static string Report(bool reorderColumns = false, float overshoot = 0f)
    {
        var sb = new StringBuilder();
        sb.Append("{\"kind\":\"youtubeAnalytics#resultTable\",\"columnHeaders\":[");
        sb.Append(reorderColumns
            ? "{\"name\":\"relativeRetentionPerformance\",\"columnType\":\"METRIC\",\"dataType\":\"FLOAT\"},{\"name\":\"audienceWatchRatio\",\"columnType\":\"METRIC\",\"dataType\":\"FLOAT\"},{\"name\":\"elapsedVideoTimeRatio\",\"columnType\":\"DIMENSION\",\"dataType\":\"FLOAT\"}"
            : "{\"name\":\"elapsedVideoTimeRatio\",\"columnType\":\"DIMENSION\",\"dataType\":\"FLOAT\"},{\"name\":\"audienceWatchRatio\",\"columnType\":\"METRIC\",\"dataType\":\"FLOAT\"},{\"name\":\"relativeRetentionPerformance\",\"columnType\":\"METRIC\",\"dataType\":\"FLOAT\"}");
        sb.Append("],\"rows\":[");
        for (var i = 1; i <= 100; i++)
        {
            var x = i / 100.0;
            var y = (i <= 25 ? 0.95 - 0.004 * i : 0.70 - 0.003 * (i - 25)) + (i == 1 ? overshoot : 0);
            if (i > 1) sb.Append(',');
            var xs = x.ToString("0.00", CultureInfo.InvariantCulture); var ys = y.ToString("0.0000", CultureInfo.InvariantCulture);
            sb.Append('[').Append(reorderColumns ? "0.5," + ys + "," + xs : xs + "," + ys + ",0.5").Append(']');
        }
        sb.Append("]}");
        return sb.ToString();
    }

    private static YouTubeAnalyticsCredentials Creds(bool refresh = false) =>
        refresh ? new(null, "cid", "csecret", "rtok", "vid123") : new("tok-static", null, null, null, "vid123");

    // ══════════════════════ 수집기 ══════════════════════

    [Fact]
    public async Task YouTubeAnalyticsCollector_Mock_Live_RateLimit_Fallback_Check()
    {
        var preset = new float[31];
        AudienceRetentionHeatmapStudioRunner.BuildCurve(0, preset);
        var dst = new float[31];

        // ── 자격 증명 없음 → 모의(프리셋 그대로 · 요청 0) ──
        var h0 = new FakeHandler();
        using (var mock = new YouTubeAnalyticsRetentionCollector(() => default, h0))
        {
            Assert.False(mock.IsConfigured);
            Assert.Equal(31, await mock.CollectAsync(30, preset, dst));
            Assert.Equal(RetentionSource.Mock, mock.LastSource);
            Assert.Equal(RetentionFault.NoCredentials, mock.LastFault);
            Assert.Equal(0, h0.Calls);
            Assert.Equal(preset, dst);
            Assert.Contains("모의", YouTubeAnalyticsRetentionCollector.SourceName(mock.LastSource), StringComparison.Ordinal);
        }

        // ── 실응답 → Live · 쿼리 · 베어러 · 초 단위 보간 ──
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var h = new FakeHandler { Respond = _ => Json(Report()) };
        using var c = new YouTubeAnalyticsRetentionCollector(() => Creds(), h, () => now);
        Assert.Equal(31, await c.CollectAsync(30, preset, dst));
        Assert.Equal(RetentionSource.Live, c.LastSource);
        Assert.Equal(100, c.LastPoints);
        Assert.Equal("Bearer tok-static", h.LastAuth);
        var q = Uri.UnescapeDataString(h.LastUri!.Query);
        Assert.Contains("metrics=audienceWatchRatio,relativeRetentionPerformance", q, StringComparison.Ordinal);
        Assert.Contains("dimensions=elapsedVideoTimeRatio", q, StringComparison.Ordinal);
        Assert.Contains("filters=video==vid123", q, StringComparison.Ordinal);
        Assert.Contains("endDate=2026-10-23", q, StringComparison.Ordinal);
        Assert.Equal(1f, dst[0]);
        Assert.Equal(0.70f - 0.003f * 25f, dst[15], 3);
        Assert.Equal(0.70f - 0.003f * 75f, dst[30], 3);
        var live = (float[])dst.Clone();

        // ── 429 + Retry-After 120 s → 캐시 폴백 · 쿨다운 중 요청 0 · 쿨다운 경과 후 재개 ──
        h.Respond = _ => { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)); return r; };
        Array.Clear(dst);
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionSource.FallbackCache, c.LastSource);
        Assert.Equal(RetentionFault.RateLimited, c.LastFault);
        Assert.Equal(live, dst);
        Assert.Equal(now.AddSeconds(120), c.CooldownUntil);
        var calls = h.Calls;
        now = now.AddSeconds(60);
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(calls, h.Calls);   // 쿨다운 중 요청 생략(자동 재시도 없음)
        Assert.Equal(RetentionFault.CooldownActive, c.LastFault);
        Assert.Equal(RetentionSource.FallbackCache, c.LastSource);
        now = now.AddSeconds(61);
        h.Respond = _ => Json(Report());
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionSource.Live, c.LastSource);
        Assert.Equal(calls + 1, h.Calls);

        // ── 길이가 다른 요청 → 캐시 비율 재보간 ──
        var d20 = new float[21];
        h.Respond = _ => throw new HttpRequestException("network down");
        Assert.Equal(21, await c.CollectAsync(20, preset, d20));
        Assert.Equal(RetentionSource.FallbackCache, c.LastSource);
        Assert.Equal(RetentionFault.Network, c.LastFault);
        Assert.Equal(live[30], d20[20], 4);

        // ── 타임아웃 · 401 · 5xx · 형식 오류 · 빈 리포트 ──
        h.Respond = _ => throw new TaskCanceledException("timeout");
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionFault.Timeout, c.LastFault);
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionFault.Unauthorized, c.LastFault);
        h.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionFault.HttpError, c.LastFault);
        Assert.Equal(503, c.LastHttpStatus);
        h.Respond = _ => Json("{\"columnHeaders\":[{\"name\":\"elapsedVideoTimeRatio\"},{\"name\":\"audienceWatchRatio\"}],\"rows\":[[0.5,0.4],[0.2,0.3]]}");
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionFault.Malformed, c.LastFault);
        h.Respond = _ => Json("{\"columnHeaders\":[{\"name\":\"elapsedVideoTimeRatio\"},{\"name\":\"audienceWatchRatio\"}],\"rows\":[]}");
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionFault.EmptyReport, c.LastFault);
        h.Respond = _ => Json("<html>quota</html>");
        await c.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionFault.Malformed, c.LastFault);
        Assert.Equal(RetentionSource.FallbackCache, c.LastSource);
        Assert.Equal(live, dst);

        // ── 캐시 없음 → 세이프티 프리셋 ──
        var h2 = new FakeHandler { Respond = _ => throw new HttpRequestException("offline") };
        using var c2 = new YouTubeAnalyticsRetentionCollector(() => Creds(), h2, () => now);
        Array.Clear(dst);
        await c2.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionSource.FallbackPreset, c2.LastSource);
        Assert.Equal(preset, dst);
        Assert.False(c2.HasCache);

        // ── OAuth2 refresh_token → 토큰 교환 1 회 · 만료 전 재사용 ──
        var h3 = new FakeHandler { Respond = _ => Json(Report()) };
        using var c3 = new YouTubeAnalyticsRetentionCollector(() => Creds(refresh: true), h3, () => now);
        await c3.CollectAsync(30, preset, dst);
        await c3.CollectAsync(30, preset, dst);
        Assert.Equal(RetentionSource.Live, c3.LastSource);
        Assert.Equal(1, h3.TokenCalls);
        Assert.Equal(1, c3.TokenRefreshes);
        Assert.Equal("Bearer tok-refreshed", h3.LastAuth);
    }

    [Fact]
    public void YouTubeAnalyticsReport_Parse_Check()
    {
        var xs = new float[128]; var ys = new float[128];
        Assert.Equal(100, YouTubeAnalyticsRetentionCollector.ParseReport(Encoding.UTF8.GetBytes(Report(reorderColumns: true)), xs, ys));
        Assert.Equal(0.01f, xs[0], 4);
        Assert.Equal(1f, xs[99], 4);
        Assert.Equal(0.95f - 0.004f * 25, ys[24], 4);
        // 재시청으로 1 초과 → 1 클램프
        Assert.Equal(100, YouTubeAnalyticsRetentionCollector.ParseReport(Encoding.UTF8.GetBytes(Report(overshoot: 0.6f)), xs, ys));
        Assert.Equal(1f, ys[0]);
        Assert.Equal(-1, YouTubeAnalyticsRetentionCollector.ParseReport("{\"rows\":[[0.1,0.9]]}"u8, xs, ys));            // 헤더 없음
        Assert.Equal(-1, YouTubeAnalyticsRetentionCollector.ParseReport("[1,2]"u8, xs, ys));
        Assert.Equal(-1, YouTubeAnalyticsRetentionCollector.ParseReport("{\"columnHeaders\":[{\"name\":\"elapsedVideoTimeRatio\"},{\"name\":\"audienceWatchRatio\"}],\"rows\":[[1.5,0.2]]}"u8, xs, ys));
        Assert.Equal(-1, YouTubeAnalyticsRetentionCollector.ParseReport("{\"columnHeaders\":[{\"name\":\"elapsedVideoTimeRatio\"},{\"name\":\"audienceWatchRatio\"}],\"rows\":[[0.1,"u8, xs, ys));
        Assert.Equal(0, YouTubeAnalyticsRetentionCollector.ParseReport("{\"kind\":\"x\",\"columnHeaders\":[{\"name\":\"elapsedVideoTimeRatio\"},{\"name\":\"audienceWatchRatio\"}]}"u8, xs, ys));
        Assert.True(YouTubeAnalyticsRetentionCollector.TryParseToken("{\"access_token\":\"abc\",\"expires_in\":3599,\"scope\":\"x\"}"u8, out var t, out var e) && t == "abc" && e == 3599);
        Assert.False(YouTubeAnalyticsRetentionCollector.TryParseToken("{\"error\":\"invalid_grant\"}"u8, out _, out _));
        // 비율 → 초 보간: 단조 감소 입력 → 단조 감소 출력 · 0 s = 1
        var dst = new float[31];
        YouTubeAnalyticsRetentionCollector.ParseReport(Encoding.UTF8.GetBytes(Report()), xs, ys);
        YouTubeAnalyticsRetentionCollector.Resample(xs.AsSpan(0, 100), ys.AsSpan(0, 100), dst);
        Assert.Equal(1f, dst[0]);
        for (var i = 1; i < dst.Length; i++) Assert.True(dst[i] <= dst[i - 1] + 1e-6f, "monotonic " + i);
    }

    // ══════════════════════ NALU 리먹서 ══════════════════════

    [Theory]
    [InlineData(NalCodec.H264, 2, true)]
    [InlineData(NalCodec.H264, 0, false)]
    [InlineData(NalCodec.H265, 2, false)]
    [InlineData(NalCodec.H265, 1, true)]
    public void NaluReMuxer_Lossless_GopSnap_Retime_Check(NalCodec codec, int bFrames, bool paramsEveryGop)
    {
        const int fps = 30, gop = 30, frames = 300;
        var st = new SyntheticAnnexBStream(codec, frames, gop, bFrames, fps, paramsEveryGop, 0xC0FFEEu + (uint)codec);
        var m = new NaluSmartReMuxer();
        Assert.Equal(frames, m.Parse(st.Bytes, codec));
        Assert.Equal(frames / gop, m.KeyCount);
        Assert.Equal(0, m.LeadingBytes);
        m.SetTimestamps(st.Pts, st.Dts);
        for (var i = 0; i < frames; i++)
        {
            Assert.True(m.Au(i).Key == (i % gop == 0), "key " + i);
            Assert.True(m.Au(i).Dts <= m.Au(i).Pts);
        }
        // NAL 구간은 원본을 빈틈없이 분할
        var sum = 0; for (var i = 0; i < m.NalCount; i++) { Assert.Equal(sum, m.Nal(i).Offset); sum += m.Nal(i).Length; }
        Assert.Equal(st.Bytes.Length, sum);

        var dst = new byte[st.Bytes.Length + 8192];
        var origin = st.Pts.Min();
        var originMs = (int)((origin * 1000 + NaluSmartReMuxer.Clock - 1) / NaluSmartReMuxer.Clock);   // 올림 — 컷 시작이 키 AU 표시 시각 이상
        var durMs = frames * 1000 / fps;

        // ── 항등: 전체 1 컷 · 항등 맵 → 바이트/타임스탬프 완전 일치 ──
        var w = m.Remux(st.Bytes, new[] { new RemuxCut(originMs, originMs + durMs) }, ReadOnlySpan<WarpKnot>.Empty, st.FrameTicks, dst);
        Assert.Equal(st.Bytes.Length, w);
        Assert.True(dst.AsSpan(0, w).SequenceEqual(st.Bytes), "identity bytes");
        for (var i = 0; i < frames; i++) { Assert.Equal(st.Pts[i], m.OutAu(i).Pts); Assert.Equal(st.Dts[i], m.OutAu(i).Dts); }   // 첫 DTS = 0 · 재정렬 지연 보존
        Assert.Equal(0, m.VerifyLossless(st.Bytes, dst.AsSpan(0, w)));
        Assert.Equal(m.NalCount, m.VerifiedNalCount);
        Assert.Equal(0, m.TimestampRepairs);

        // ── GOP 단위 분할 → 재결합 = 원본 바이트 ──
        var cuts = new RemuxCut[frames / gop];
        for (var g = 0; g < cuts.Length; g++) cuts[g] = new RemuxCut(originMs + g * 1000, originMs + (g + 1) * 1000 - 1);   // 끝 −1 ms: 올림 원점이 다음 키 AU 표시 시각을 넘지 않도록
        w = m.Remux(st.Bytes, cuts, ReadOnlySpan<WarpKnot>.Empty, st.FrameTicks, dst);
        Assert.True(dst.AsSpan(0, w).SequenceEqual(st.Bytes), "split/rejoin bytes");
        Assert.Equal(cuts.Length, m.CopiedGops);
        Assert.Equal(0, m.InjectedParamNals);

        // ── GOP 중간 컷(3.4 s ~ 5.2 s · 7.5 s ~ 8.1 s) → GOP 스냅 · 파라미터 셋 주입(첫 GOP 만 보유 시) · 독립 디코딩 구조 ──
        w = m.Remux(st.Bytes, new[] { new RemuxCut(originMs + 3400, originMs + 5200), new RemuxCut(originMs + 7500, originMs + 8100) }, ReadOnlySpan<WarpKnot>.Empty, st.FrameTicks, dst);
        Assert.True(w > 0);
        Assert.Equal(5 * gop, m.OutCount);   // [3 s, 6 s) + [7 s, 9 s) — 끝 경계 = 컷 끝 이상인 첫 키 AU
        Assert.Equal(5, m.CopiedGops);
        Assert.True(m.OutAu(0).Key && m.OutSourceAu(0) == 3 * gop && m.OutAu(3 * gop).Key && m.OutSourceAu(3 * gop) == 7 * gop);
        Assert.Equal(paramsEveryGop ? 0 : (codec == NalCodec.H265 ? 3 : 2), m.InjectedParamNals);   // 첫 세그먼트에만 주입 · 둘째는 동일 셋 기출력
        Assert.True(m.PaddingTicks > 0);
        Assert.Equal(0, m.VerifyLossless(st.Bytes, dst.AsSpan(0, w)));
        for (var i = 1; i < m.OutCount; i++) Assert.True(m.OutAu(i).Dts > m.OutAu(i - 1).Dts, "dts strictly increasing " + i);
        Assert.Equal(0, m.TimestampRepairs);
        // 세그먼트 연결 경계: 2 번째 세그먼트 최소 PTS = 1 번째 최대 PTS + 1 프레임
        long max1 = long.MinValue, min2 = long.MaxValue;
        for (var i = 0; i < 3 * gop; i++) max1 = Math.Max(max1, m.OutAu(i).Pts);
        for (var i = 3 * gop; i < m.OutCount; i++) min2 = Math.Min(min2, m.OutAu(i).Pts);
        Assert.Equal(max1 + st.FrameTicks, min2);

        // ── 템포 맵: 2~4 s 를 1.15× 로 압축 → PTS/DTS 동일 재매핑 · 엄격 증가 · 끝 = 10 s − 2/1.15·0.15 s ──
        long T(int ms) => origin + (long)ms * NaluSmartReMuxer.Clock / 1000;
        var outEnd4 = 2000 + (int)Math.Round(2000 / 1.15);
        var knots = new[] { new WarpKnot(T(0), T(0)), new WarpKnot(T(2000), T(2000)), new WarpKnot(T(4000), T(outEnd4)), new WarpKnot(T(durMs), T(outEnd4 + durMs - 4000)) };
        w = m.Remux(st.Bytes, new[] { new RemuxCut(originMs, originMs + durMs) }, knots, st.FrameTicks, dst, new[] { originMs + 2500, originMs + 2600, originMs + 6100 });
        Assert.True(dst.AsSpan(0, w).SequenceEqual(st.Bytes), "retime keeps bytes");
        Assert.Equal(0, m.VerifyLossless(st.Bytes, dst.AsSpan(0, w)));
        Assert.Equal(2, m.ReencodeGops);   // 펀치 3 건 → GOP 2(2 s) · GOP 6(6 s)
        for (var i = 1; i < m.OutCount; i++) Assert.True(m.OutAu(i).Dts > m.OutAu(i - 1).Dts);
        for (var i = 0; i < m.OutCount; i++)
        {
            Assert.True(m.OutAu(i).Pts >= m.OutAu(i).Dts);
            var expect = NaluSmartReMuxer.Warp(knots, st.Pts[m.OutSourceAu(i)]);   // 절점이 원점(origin → origin) 고정 · 첫 DTS = 0
            Assert.InRange(m.OutAu(i).Pts - expect, -1, 1);
        }
        var outDurMs = (m.OutEndTicks - origin) * 1000.0 / NaluSmartReMuxer.Clock;
        Assert.InRange(outDurMs, outEnd4 + durMs - 4000 - 5, outEnd4 + durMs - 4000 + 40);
        Assert.Equal(0, m.TimestampRepairs);

        // ── v17.98.1 SPS 크롭 줌 펀치: H.264 = 펀치 GOP(2 · 6) SPS 1.15× 크롭 재작성 → 다음 GOP 원 SPS 복원 · H.265 = 재인코딩 필요 유지 ──
        var punches = new[] { originMs + 2500, originMs + 2600, originMs + 6100 };
        var cut1 = new[] { new RemuxCut(originMs, originMs + durMs) };
        w = m.Remux(st.Bytes, cut1, knots, st.FrameTicks, dst, punches, 1.15f);
        Assert.Equal(0, m.VerifyLossless(st.Bytes, dst.AsSpan(0, w)));
        Assert.Equal(frames, m.OutCount);
        Assert.Equal(0, m.TimestampRepairs);
        for (var i = 0; i < m.OutCount; i++) Assert.InRange(m.OutAu(i).Pts - NaluSmartReMuxer.Warp(knots, st.Pts[m.OutSourceAu(i)]), -1, 1);
        if (codec == NalCodec.H264)
        {
            Assert.Equal(2, m.ZeroTranscodePunchGops);
            Assert.Equal(0, m.ReencodeGops);
            Assert.Equal(2, m.CroppedSpsNals);
            // 출력 재파싱 → 각 GOP(키 AU) 활성 SPS 표시 창: 펀치 GOP = 1668×938 · 그 외 = 1920×1080
            var m2 = new NaluSmartReMuxer();
            m2.Parse(dst.AsSpan(0, w), codec);
            int curW = 0, curH = 0, g = 0;
            for (var i = 0; i < m2.NalCount; i++)
            {
                ref readonly var u = ref m2.Nal(i);
                if (u.Type == 7) Assert.True(m2.Cropper.TryReadCrop(dst.AsSpan(u.Offset, u.Length), out curW, out curH));
                else if (u.Key && u.FirstSlice)
                {
                    var punchGop = g == 2 || g == 6;
                    Assert.Equal(punchGop ? 1668 : 1920, curW);
                    Assert.Equal(punchGop ? 938 : 1080, curH);
                    g++;
                }
            }
            Assert.Equal(frames / gop, g);
            // 크롭 비활성 재기록(zoom 1) = 원문 비트 동일(파서/라이터 왕복 무손실)
            var spsNal = m.Nal(m.Au(0).FirstNal + 1);
            Assert.Equal(7, spsNal.Type);
            var rt = new byte[256];
            var rn = m.Cropper.Rewrite(st.Bytes.AsSpan(spsNal.Offset, spsNal.Length), 1f, rt);
            Assert.True(rt.AsSpan(0, rn).SequenceEqual(st.Bytes.AsSpan(spsNal.Offset, spsNal.Length)), "sps round trip");
        }
        else
        {
            Assert.Equal(2, m.ReencodeGops);
            Assert.Equal(0, m.ZeroTranscodePunchGops);
            Assert.True(dst.AsSpan(0, w).SequenceEqual(st.Bytes), "h265 punch keeps bytes");
        }

        // ── 손상 검출: 출력 1 바이트 변조 → 불일치 ──
        dst[w / 2] ^= 0x5A;
        Assert.True(m.VerifyLossless(st.Bytes, dst.AsSpan(0, w)) > 0);
        dst[w / 2] ^= 0x5A;

        // ── 비단조 맵 거부 ──
        Assert.Throws<ArgumentException>(() => m.Remux(st.Bytes, new[] { new RemuxCut(0, 1000) }, new[] { new WarpKnot(10, 10), new WarpKnot(5, 20) }, st.FrameTicks, dst));

        // ── 무할당: Parse · Remux(+ SPS 크롭 펀치) · Verify ──
        Assert.Equal(0, AllocationProbe.MinAllocated(() =>
        {
            m.Parse(st.Bytes, codec);
            m.SetTimestamps(st.Pts, st.Dts);
            var n = m.Remux(st.Bytes, cut1, knots, st.FrameTicks, dst);
            m.VerifyLossless(st.Bytes, dst.AsSpan(0, n));
            n = m.Remux(st.Bytes, cut1, knots, st.FrameTicks, dst, punches, 1.15f);
            m.VerifyLossless(st.Bytes, dst.AsSpan(0, n));
        }));
        if (codec == NalCodec.H264) Assert.InRange(m.MaxCropUs, 0, 500);   // 웜 상태 SPS 비트 재작성 ≤ 0.5 ms
    }

    // ══════════════════════ 러너 결속 ══════════════════════

    [Fact]
    public async Task RetentionRunner_Ingest_And_Remux_Binding_Check()
    {
        var h = new FakeHandler { Respond = _ => Json(Report()) };
        var configured = false;
        using var run = new AudienceRetentionHeatmapStudioRunner(new YouTubeAnalyticsRetentionCollector(() => configured ? Creds() : default, h));
        var changes = 0;
        run.OnChange += () => changes++;

        // 모의 수집: 프리셋 곡선 유지 · 출처 = 모의
        var slumps0 = run.Detector.SlumpCount;
        Assert.Equal(RetentionSource.Mock, await run.IngestAsync());
        Assert.Equal(slumps0, run.Detector.SlumpCount);
        Assert.Contains("모의", run.LastAction, StringComparison.Ordinal);
        Assert.Equal(0, h.Calls);

        // 실응답 수집: 곡선 교체 → 재분석 → 7.5 s 부근 급락 1 건 이상
        configured = true;
        Assert.Equal(RetentionSource.Live, await run.IngestAsync());
        Assert.Equal(1, h.Calls);
        Assert.Contains("실데이터", run.LastAction, StringComparison.Ordinal);
        Assert.Equal(0.70f - 0.003f * 25f, run.Detector.Curve[15], 3);

        // 장애 → 직전 실응답 캐시
        h.Respond = _ => throw new HttpRequestException("down");
        Assert.Equal(RetentionSource.FallbackCache, await run.IngestAsync());
        Assert.Contains("캐시", run.LastAction, StringComparison.Ordinal);

        // 프리셋별 리먹스(H.264 · H.265 · H.264): 무손실 · A/V 편차 ≤ 1 ms · 끝 드리프트 ≤ 1 프레임
        for (var p = 0; p < AudienceRetentionHeatmapStudioRunner.PresetNames.Length; p++)
        {
            run.SetPreset(p);
            var w = run.Remux();
            Assert.True(w > 0, "remux " + p);
            Assert.True(run.Rescued);
            Assert.True(run.Remuxed, "lossless " + p + " faults " + run.RemuxLosslessFaults);
            Assert.Equal(run.Stream.Frames, run.ReMuxer.OutCount);
            Assert.Equal(p == 1 ? NalCodec.H265 : NalCodec.H264, run.Stream.Codec);
            Assert.InRange(run.MaxAvDesyncMs, 0, 2.0);   // 정수 ms 템포 맵 절단 오차 — 1 프레임(33 ms) · 립싱크 허용(ITU-R BT.1359 +45/−125 ms) 대비 무시 가능
            Assert.InRange(run.EndDriftMs, 0, 1000.0 / MicroPunchDynamicPacingReEditorEngine.VideoFps);
            Assert.Equal(0, run.ReMuxer.TimestampRepairs);
            if (run.ReEditor.TempoCount > 0) Assert.True(run.ReMuxer.OutEndTicks * 1000 / NaluSmartReMuxer.Clock < run.ReEditor.SourceDurationMs, "tempo shortens " + p);
            if (run.ReEditor.PunchCount > 0)
            {
                // H.264 = SPS 크롭(재인코딩 0) · H.265 = 재인코딩 필요 정직 보고
                Assert.InRange(run.ReMuxer.ReencodeGops + run.ReMuxer.ZeroTranscodePunchGops, 1, run.ReEditor.PunchCount);
                if (p == 1) Assert.Equal(0, run.ReMuxer.ZeroTranscodePunchGops); else Assert.Equal(0, run.ReMuxer.ReencodeGops);
            }
            _out.WriteLine("preset " + p + " " + run.LastAction + " · 끝 드리프트 " + run.EndDriftMs.ToString("0.00", CultureInfo.InvariantCulture) + " ms · 템포 " + run.ReEditor.TempoCount + " · 펀치 " + run.ReEditor.PunchCount);
        }
        Assert.True(changes > 0);
        run.Reset();
        Assert.Equal(0, run.Remuxes);
        Assert.False(run.Remuxed);
    }

    // ══════════════════════ 장기 소크 ══════════════════════

    [Fact]
    public async Task RetentionPipeline_Soak_NoLeak_Check()
    {
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("PFS_SOAK_SECONDS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 12.0;
        var h = new FakeHandler();
        var k = 0;
        // 순환 장애: 정상 · 429 · 단절 · 형식 오류 · 401
        h.Respond = _ => (k++ % 5) switch
        {
            0 => Json(Report()),
            1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            2 => throw new HttpRequestException("down"),
            3 => Json("{oops"),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized),
        };
        var clock = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.Zero);
        using var run = new AudienceRetentionHeatmapStudioRunner(new YouTubeAnalyticsRetentionCollector(() => Creds(), h, () => clock));

        // 워밍업(JIT · 합성 스트림 3 종 · 버퍼) 후 기준선
        for (var p = 0; p < 3; p++) { run.SetPreset(p); run.Remux(); await run.IngestAsync(); }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var proc = Process.GetCurrentProcess();
        proc.Refresh();
        long mem0 = GC.GetTotalMemory(true), handles0 = proc.HandleCount, threads0 = proc.Threads.Count;

        var sw = Stopwatch.StartNew();
        long cycles = 0, frames = 0, bytes = 0, faults = 0;
        double maxDesync = 0;
        var sources = new int[5];
        long memMid = -1, cyclesMid = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (memMid < 0 && sw.Elapsed.TotalSeconds >= seconds / 2)
            {
                // 중간 체크포인트 — 전반부 = 풀(ArrayPool · HttpClient 내부)/JIT 정착 구간 · 후반부 증가분만 누수 판정
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                memMid = GC.GetTotalMemory(true); cyclesMid = cycles;
            }
            run.SetPreset((int)(cycles % 3));
            clock = clock.AddSeconds(61);   // 429 쿨다운(60 s 기본) 해제
            sources[(int)await run.IngestAsync()]++;
            var w = run.Remux();
            if (w <= 0 || !run.Remuxed) faults++;
            frames += run.ReMuxer.OutCount;
            bytes += w;
            maxDesync = Math.Max(maxDesync, run.MaxAvDesyncMs);
            cycles++;
        }
        sw.Stop();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        proc.Refresh();
        long mem1 = GC.GetTotalMemory(true), handles1 = proc.HandleCount, threads1 = proc.Threads.Count;
        var report = "소크 " + sw.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s · 사이클 " + cycles + " · 리먹스 프레임 " + frames + " · 바이트 " + bytes +
                     " · 손상 " + faults + " · 최대 A/V " + maxDesync.ToString("0.00", CultureInfo.InvariantCulture) + " ms · 수집 Live/캐시/프리셋 " + sources[1] + "/" + sources[3] + "/" + sources[4] +
                     " · 메모리 Δ 전체 " + (mem1 - mem0) + " B · 후반 " + (mem1 - memMid) + " B/" + (cycles - cyclesMid) + " 사이클 · 핸들 Δ " + (handles1 - handles0) + " · 스레드 Δ " + (threads1 - threads0);
        _out.WriteLine(report);
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "pfs_retention_soak_report.txt"), report); } catch (IOException) { /* best-effort 사본 */ }

        Assert.True(cycles >= 50, report);
        Assert.Equal(0, faults);
        Assert.True(maxDesync <= 2.0, report);
        Assert.True(sources[1] > 0 && sources[3] > 0, report);
        // 누수 = 사이클 비례 증가: 리먹스 1 회 출력 ≈ 380 KB 이므로 출력/곡선 버퍼를 사이클마다 붙잡으면 후반부만 수십 MB.
        // 후반부 증가 < 사이클당 1 KB(런타임 풀 변동 여유) · 전체 < 2 MB(일회성 풀 정착 포함)
        Assert.True(memMid > 0 && mem1 - memMid < Math.Max(64 * 1024, (cycles - cyclesMid) * 1024), report);
        Assert.True(mem1 - mem0 < 2 * 1024 * 1024, report);
        Assert.True(handles1 - handles0 <= 32, report);   // 런타임 내부 핸들 변동 여유
    }
}
