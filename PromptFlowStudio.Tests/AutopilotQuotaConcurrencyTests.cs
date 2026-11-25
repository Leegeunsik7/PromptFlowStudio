using System.Threading;
using System.Threading.Tasks;
using L2R = PromptFlowStudio.Services.LiveToReels;
using Pub = PromptFlowStudio.Services.Publisher;
using Ap = PromptFlowStudio.Services.Autopilot;

namespace PromptFlowStudio.Tests;

/// <summary>Subtask C — 쿼터 가드 동시성(틱 × 업로드/재시도 태스크) · 업로드 결과 반환값 고유 판독(공유 Last* 조회 금지).</summary>
public class AutopilotQuotaConcurrencyTests
{
    private const int Workers = 8;

    [Fact]
    [Trait("Category", "FastUnit")]
    public void QuotaGuard_ConcurrentObserve_CountsEverySuccessExactly()
    {
        long now = 1_760_000_000_000;
        var q = new Ap.SmartPlatformQuotaResilienceGuard { Clock = () => Interlocked.Read(ref now) };
        const int PerWorker = 5_000;
        using var go = new ManualResetEventSlim(false);
        var threads = new Thread[Workers];
        for (var w = 0; w < Workers; w++)
        {
            threads[w] = new Thread(() =>
            {
                go.Wait();
                for (var i = 0; i < PerWorker; i++) q.Observe(Ap.PublishTarget.TikTok, 200);
            });
            threads[w].Start();
        }
        go.Set();
        foreach (var t in threads) t.Join();
        Assert.Equal((long)Workers * PerWorker, q.Successes(Ap.PublishTarget.TikTok));
        Assert.Equal((long)Workers * PerWorker, q.Used(Ap.PublishTarget.TikTok));
        Assert.Equal(Ap.TargetHealth.Healthy, q.Health(Ap.PublishTarget.TikTok));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void QuotaGuard_ConcurrentEnqueuePumpRequeue_KeepsQueueConsistent()
    {
        long now = 1_760_000_000_000;
        var q = new Ap.SmartPlatformQuotaResilienceGuard { Clock = () => Interlocked.Read(ref now) };
        // YT 쿼터 소진 → 모든 YT 요청 격리
        q.Observe(Ap.PublishTarget.YouTube, 403, "{\"error\":{\"errors\":[{\"reason\":\"quotaExceeded\"}]}}"u8);
        const int PerWorker = 30;   // 8 × 30 = 240 < QueueCap(256)
        using var go = new ManualResetEventSlim(false);
        var threads = new Thread[Workers];
        for (var w = 0; w < Workers; w++)
        {
            var baseId = w * 1_000;
            threads[w] = new Thread(() =>
            {
                go.Wait();
                for (var i = 0; i < PerWorker; i++)
                {
                    // 틱(Route) · 업로드 태스크(Enqueue 같은 릴 재격리 = 중복 금지) 교차
                    q.Route(baseId + i, 0b001, 0, out _);
                    q.Enqueue(baseId + i, Ap.PublishTarget.YouTube);
                    q.Observe(Ap.PublishTarget.Instagram, 503);
                }
            });
            threads[w].Start();
        }
        go.Set();
        foreach (var t in threads) t.Join();
        Assert.Equal(Workers * PerWorker, q.QueueDepth);
        Assert.Equal(0, q.Overflows);
        Assert.Equal(Workers * PerWorker, CountUsedUnique(q));

        // 리셋 도래 → 다중 스레드 PumpDue: 각 항목 정확히 1 회 반환(중복 · 유실 0)
        Interlocked.Exchange(ref now, q.BlockedUntil(Ap.PublishTarget.YouTube) + 1);
        var seen = new int[Workers * 1_000];
        var pumped = 0;
        go.Reset();
        for (var w = 0; w < Workers; w++)
        {
            threads[w] = new Thread(() =>
            {
                var due = new Ap.QuotaRetryItem[7];
                go.Wait();
                int n;
                while ((n = q.PumpDue(due)) > 0)
                    for (var i = 0; i < n; i++) { Interlocked.Increment(ref seen[due[i].ReelId]); Interlocked.Increment(ref pumped); }
            });
            threads[w].Start();
        }
        go.Set();
        foreach (var t in threads) t.Join();
        Assert.Equal(Workers * PerWorker, pumped);
        Assert.Equal(0, q.QueueDepth);
        Assert.Equal(Workers * PerWorker, q.Retried);
        for (var i = 0; i < seen.Length; i++) Assert.True(seen[i] <= 1, "중복 반환 " + i);

        // 동시 Requeue(같은 릴 · 같은 플랫폼) → 슬롯 1 개 · 시도 +1
        var item = new Ap.QuotaRetryItem { ReelId = 4242, Target = Ap.PublishTarget.Instagram, Attempt = 2, Used = true };
        go.Reset();
        for (var w = 0; w < Workers; w++) { threads[w] = new Thread(() => { go.Wait(); q.Requeue(in item); }); threads[w].Start(); }
        go.Set();
        foreach (var t in threads) t.Join();
        Assert.Equal(1, q.QueueDepth);
        var slot = FindSlot(q, 4242);
        Assert.True(slot.Used && slot.Attempt == 3 && slot.Target == Ap.PublishTarget.Instagram, "Requeue 슬롯 " + slot.Attempt);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void QuotaGuard_LockedHotPath_StaysAllocationFree()
    {
        long now = 1_760_000_000_000;
        var q = new Ap.SmartPlatformQuotaResilienceGuard { Clock = () => now };
        var due = new Ap.QuotaRetryItem[4];
        var alloc = AllocationProbe.MinAllocated(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                q.Observe(Ap.PublishTarget.TikTok, i % 7 == 0 ? 503 : 200);
                q.Route(1000 + i, 0b111, 0b100, out _);
                q.Enqueue(5000 + (i & 15), Ap.PublishTarget.Instagram);
                q.PumpDue(due);
                _ = q.Health(Ap.PublishTarget.YouTube); _ = q.Remaining(Ap.PublishTarget.Instagram); _ = q.Queued(i & 255);
            }
        });
        Assert.Equal(0, alloc);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void UploadOutcome_ReadsOnlyTheReturnedResult()
    {
        var body429 = "{\"error\":{\"code\":4}}"u8.ToArray();
        var fail = new Pub.SocialUploadResult(Pub.SocialPlatform.Instagram, false, null, 0, 0, 1, 0, "HTTP 429", 429, 120, body429);
        var st = Ap.ZeroTouchAutopilotMasterEngine.UploadOutcome(in fail, out var ra, out var body);
        Assert.True(st == 429 && ra == 120 && ReferenceEquals(body, body429));
        var ok = new Pub.SocialUploadResult(Pub.SocialPlatform.YouTube, true, "yt1", 1, 10, 1, 0, null);   // Status 미기록 → 200 보장
        Assert.Equal(200, Ap.ZeroTouchAutopilotMasterEngine.UploadOutcome(in ok, out ra, out body));
        Assert.True(ra == 0 && body is null);
        // 실패인데 중간 단계 2xx 기록 → 성공 오인 금지(0 = 일시 백오프)
        var mid = new Pub.SocialUploadResult(Pub.SocialPlatform.Instagram, false, null, 0, 0, 1, 0, "poll timeout", 200);
        Assert.Equal(0, Ap.ZeroTouchAutopilotMasterEngine.UploadOutcome(in mid, out _, out _));
        // 가드 관측: 결과별 분류가 서로 독립
        long now = 1_760_000_000_000;
        var q = new Ap.SmartPlatformQuotaResilienceGuard { Clock = () => now };
        Assert.Equal(Ap.TargetHealth.RateLimited, q.Observe(Ap.PublishTarget.Instagram, st, body429, 120));
        Assert.True(q.BlockedUntil(Ap.PublishTarget.Instagram) - now >= 120_000);
        Assert.Equal(Ap.TargetHealth.Healthy, q.Observe(Ap.PublishTarget.YouTube, 200));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task Gateway_UploadResult_CarriesPerCallStatus_NoStaleLeak()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "pf_subC_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            long clock = 1_900_000_000;
            var sim = new Pub.SimulatedSocialApiServer(() => clock);
            using var vault = new Pub.OAuthTokenVault(Path.Combine(tmp, "v.bin"), () => (byte[])key.Clone());
            using var gw = new Pub.SocialMediaApiGatewayEngine(sim, vault) { UnixNow = () => clock, InstagramUserId = "17841400000000000", Credential = static _ => null, ChunkSize = 1024 * 1024 };
            sim.Issue(Pub.SocialPlatform.YouTube, "ya29.ok"u8, clock + 3600, "1//sim-refresh"u8);
            vault.Store(Pub.SocialPlatform.YouTube, "ya29.ok", "1//sim-refresh", clock + 3600, clock);
            using var small = L2R.LiveToReelsFactoryStudioRunner.SyntheticClip(3);
            var smallBytes = new byte[small.Length]; small.ReadAt(0, smallBytes);

            // 1) 서버 거부(제목 101 자 → 400 + 오류 본문) — 결과 고유 상태 · 본문 사본
            var bad = await gw.UploadAsync(Pub.SocialPlatform.YouTube, small, new Pub.SocialPostMetadata { Title = new string('가', 101) });
            Assert.True(!bad.Ok && bad.Status == 400 && bad.ErrorBody is { Length: > 0 }, "거부 " + bad.Status + " " + bad.Error);
            var badBody = (byte[])bad.ErrorBody!.Clone();

            // 2) 직후 다른 플랫폼 미연동 실패 — 이전 400 · 본문이 새지 않음(401 · 본문 없음 · Retry-After 0)
            var ig = await gw.UploadAsync(Pub.SocialPlatform.Instagram, small, new Pub.SocialPostMetadata { Title = "t" });
            Assert.True(!ig.Ok && ig.Status == 401 && ig.ErrorBody is null && ig.RetryAfterSec == 0, "미연동 " + ig.Status);

            // 3) 성공 — 2xx · 본문 없음
            sim.ExpectMedia(smallBytes);
            var ok = await gw.UploadAsync(Pub.SocialPlatform.YouTube, small, new Pub.SocialPostMetadata { Title = "ok #Shorts" });
            Assert.True(ok.Ok && ok.Status is >= 200 and < 300 && ok.ErrorBody is null, "성공 " + ok.Status + " " + ok.Error);

            // 4) 앞선 결과의 본문은 게이트웨이 공유 버퍼와 분리된 사본(후속 호출에 변형되지 않음)
            var bad2 = await gw.UploadAsync(Pub.SocialPlatform.YouTube, small, new Pub.SocialPostMetadata { Title = new string('나', 101) });
            Assert.True(!bad2.Ok && !ReferenceEquals(bad.ErrorBody, bad2.ErrorBody));
            Assert.Equal(badBody, bad.ErrorBody);
            Assert.Equal(400, Ap.ZeroTouchAutopilotMasterEngine.UploadOutcome(in bad, out _, out _));
            Assert.Equal(401, Ap.ZeroTouchAutopilotMasterEngine.UploadOutcome(in ig, out _, out _));
        }
        finally { try { Directory.Delete(tmp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static int CountUsedUnique(Ap.SmartPlatformQuotaResilienceGuard q)
    {
        var n = 0;
        for (var i = 0; i < Ap.SmartPlatformQuotaResilienceGuard.QueueCap; i++)
        {
            var a = q.Queued(i);
            if (!a.Used) continue;
            for (var j = 0; j < i; j++) { var b = q.Queued(j); Assert.False(b.Used && b.ReelId == a.ReelId && b.Target == a.Target, "중복 슬롯 " + a.ReelId); }
            n++;
        }
        return n;
    }

    private static Ap.QuotaRetryItem FindSlot(Ap.SmartPlatformQuotaResilienceGuard q, int reelId)
    {
        for (var i = 0; i < Ap.SmartPlatformQuotaResilienceGuard.QueueCap; i++) { var a = q.Queued(i); if (a.Used && a.ReelId == reelId) return a; }
        return default;
    }
}
