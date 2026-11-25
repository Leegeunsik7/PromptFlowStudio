using System.Threading;
using System.Threading.Tasks;
using L2R = PromptFlowStudio.Services.LiveToReels;
using Ap = PromptFlowStudio.Services.Autopilot;

namespace PromptFlowStudio.Tests;

/// <summary>Subtask E — 재시도 Requeue 통합(MaxAttempts · GaveUp · 무관 릴 페널티 0) · 가드 시계 축 일관성 · 스탠바이/합성 폴백 게시 차단.</summary>
public class AutopilotRetryStandbyGuardTests
{
    private const long UnixBase = 1_760_000_000_000;

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Requeue_HonorsMaxAttempts_ThenGivesUp_OnGuardClock()
    {
        long now = UnixBase;
        var q = new Ap.SmartPlatformQuotaResilienceGuard { Clock = () => now };
        Assert.True(q.Enqueue(7, Ap.PublishTarget.YouTube));
        Assert.Equal(now, q.Queued(0).EnqueuedMs);
        var due = new Ap.QuotaRetryItem[4];
        var requeued = 0;
        for (var round = 0; round < Ap.SmartPlatformQuotaResilienceGuard.MaxAttempts + 2; round++)
        {
            now += Ap.SmartPlatformQuotaResilienceGuard.MaxBackoffMs + 1;
            var n = q.PumpDueReel(due);
            if (n == 0) break;
            Assert.Equal(1, n);
            Assert.Equal(requeued, due[0].Attempt);
            if (!q.Requeue(in due[0])) break;
            requeued++;
            var s = q.Queued(0);
            Assert.True(s.Used && s.Attempt == requeued, "시도 " + s.Attempt);
            // 가드 시계 축(Unix ms) — TickCount64 축 혼입 시 NextAtMs 가 now 보다 수십 년 과거
            Assert.InRange(s.NextAtMs, now, now + Ap.SmartPlatformQuotaResilienceGuard.MaxBackoffMs);
        }
        Assert.Equal(Ap.SmartPlatformQuotaResilienceGuard.MaxAttempts - 1, requeued);
        Assert.Equal(1, q.GaveUp);
        Assert.Equal(0, q.QueueDepth);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PumpDueReel_LeavesUnrelatedReelsQueued_WithoutAttemptPenalty()
    {
        long now = UnixBase;
        var q = new Ap.SmartPlatformQuotaResilienceGuard { Clock = () => now };
        q.Enqueue(1, Ap.PublishTarget.YouTube);
        q.Enqueue(2, Ap.PublishTarget.Instagram);
        q.Enqueue(1, Ap.PublishTarget.Instagram);
        var due = new Ap.QuotaRetryItem[8];
        var n = q.PumpDueReel(due);
        Assert.Equal(2, n);
        Assert.True(due[0].ReelId == 1 && due[1].ReelId == 1);
        Assert.Equal(1, q.QueueDepth);
        var other = q.Queued(1);
        Assert.True(other.Used && other.ReelId == 2 && other.Attempt == 0, "무관 릴 시도 " + other.Attempt);
        Assert.Equal(0, q.GaveUp);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Reinstate_PreservesAttempt_WithoutBackoffPenalty()
    {
        long now = UnixBase;
        var q = new Ap.SmartPlatformQuotaResilienceGuard { Clock = () => now };
        q.Enqueue(3, Ap.PublishTarget.TikTok);
        var due = new Ap.QuotaRetryItem[2];
        Assert.Equal(1, q.PumpDueReel(due));
        Assert.True(q.Requeue(in due[0]));
        now += Ap.SmartPlatformQuotaResilienceGuard.MaxBackoffMs + 1;
        Assert.Equal(1, q.PumpDueReel(due));
        Assert.Equal(1, due[0].Attempt);
        Assert.True(q.Reinstate(in due[0]));
        var s = q.Queued(0);
        Assert.True(s.Used && s.Attempt == 1 && s.NextAtMs == now, "복귀 " + s.Attempt + " @" + s.NextAtMs);
        Assert.Equal(0, q.GaveUp);
    }

    private static async Task Drive(Ap.ZeroTouchAutopilotMasterEngine e, Func<long> step, Func<bool> until, int maxTicks)
    {
        for (var i = 0; i < maxTicks && !until(); i++) { step(); e.Tick(); if (e.TaskRunning) await Task.Delay(10); }
    }

    private static Ap.ZeroTouchAutopilotStudioRunner NewPipeline(L2R.LiveToReelsFactoryStudioRunner factory)
    {
        var ap = new Ap.ZeroTouchAutopilotStudioRunner(null, factory, null, null, null, new Ap.SimulatedWebhookServer());
        var e = ap.Engine;
        e.BroadcastMinMs = 1_000; e.ReactTimeoutMs = 600_000_000; e.ClipTimeoutMs = 600_000; e.CooldownMs = 500;
        e.MaxBurnSec = 3; e.BurnFps = 15; e.AllowMediaFoundation = false; e.RequestMask = 0b011; e.RerouteMask = 0;
        ap.BurnIn.Resize(180, 320);
        return ap;
    }

    [Fact]
    public async Task SyntheticFallbackClip_IsNotPublished_WhenPublishingIsLive()
    {
        var factory = new L2R.LiveToReelsFactoryStudioRunner();
        factory.UseSyntheticSource(4);
        var ap = NewPipeline(factory);
        try
        {
            var e = ap.Engine;
            long clock = 1_000;
            e.Clock = () => clock; ap.Quota.Clock = () => UnixBase + clock;
            e.SimulatePublishing = false;
            e.Enable();
            await Drive(e, () => clock += 100, () => e.Stage == Ap.AutopilotStage.Reacting, 50);
            var req = new L2R.ClimaxClipRequest { Id = 1, StartMs = 0, EndMs = 4_000, Peak = 94, Cause = L2R.ClimaxCause.Physics };
            var reel = factory.ProcessRequest(in req);
            Assert.True(reel.SyntheticSource, "합성 폴백 표식");
            await Drive(e, () => clock += 100, () => e.Stage == Ap.AutopilotStage.Cooldown, 3_000);
            Assert.True(e.Faults == 0, "FSM 장애 " + e.LastError);
            Assert.True(e.Burned(reel.Id) is { Synthetic: true }, "번-인 릴 합성 표식");
            Assert.Equal(1, e.SyntheticSkips);
            Assert.Equal(0, e.Published);
            Assert.Equal(0, ap.Quota.QueueDepth);
            e.Disable();
        }
        finally { ap.Dispose(); factory.Dispose(); }
    }

    [Fact]
    public async Task StandbyEngaged_HoldsPublish_ThenRetryFailureRequeuesWithAttemptOnGuardClock()
    {
        var factory = new L2R.LiveToReelsFactoryStudioRunner();
        factory.UseSyntheticSource(4);
        var ap = NewPipeline(factory);
        try
        {
            var e = ap.Engine;
            long clock = 1_000;
            var standby = true;
            e.Clock = () => Interlocked.Read(ref clock); ap.Quota.Clock = () => UnixBase + Interlocked.Read(ref clock);
            e.SimulatePublishing = true;   // 합성 클립 허용 — 스탠바이 게이트만 검증
            e.StandbyProbe = () => Volatile.Read(ref standby);
            e.Enable();
            await Drive(e, () => Interlocked.Add(ref clock, 100), () => e.Stage == Ap.AutopilotStage.Reacting, 50);
            var req = new L2R.ClimaxClipRequest { Id = 1, StartMs = 0, EndMs = 4_000, Peak = 94, Cause = L2R.ClimaxCause.Chat };
            var reel = factory.ProcessRequest(in req);
            await Drive(e, () => Interlocked.Add(ref clock, 100), () => e.Stage == Ap.AutopilotStage.Cooldown, 3_000);
            Assert.True(e.Faults == 0, "FSM 장애 " + e.LastError);
            Assert.Equal(1, e.StandbyHolds);
            Assert.Equal(0, e.Published);
            Assert.Equal(2, ap.Quota.QueueDepth);
            // 스탠바이 지속 = 재시도 펌프 정지(큐 잔류)
            for (var i = 0; i < 30; i++) { Interlocked.Add(ref clock, 1_000); e.Tick(); }
            Assert.Equal(0, ap.Quota.Retried);
            Assert.Equal(2, ap.Quota.QueueDepth);
            // 복귀 → 재시도(퍼블리셔 미결선 = 503) → Requeue 시도 +1(초기화 · 포기 없음)
            Volatile.Write(ref standby, false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 10_000 && !(ap.Quota.Retried >= 2 && ap.Quota.QueueDepth == 2 && !e.RetryTaskRunning))
            { Interlocked.Add(ref clock, 100); e.Tick(); await Task.Delay(10); }
            Assert.True(ap.Quota.Retried >= 2 && ap.Quota.QueueDepth == 2, "재시도 " + ap.Quota.Retried + " 큐 " + ap.Quota.QueueDepth);
            var guardNow = UnixBase + Interlocked.Read(ref clock);
            var seen = 0;
            for (var i = 0; i < Ap.SmartPlatformQuotaResilienceGuard.QueueCap; i++)
            {
                var s = ap.Quota.Queued(i);
                if (!s.Used) continue;
                seen++;
                Assert.Equal(reel.Id, s.ReelId);
                Assert.True(s.Attempt == 1, "시도 " + s.Attempt);
                Assert.True(s.NextAtMs > UnixBase && s.NextAtMs <= guardNow + Ap.SmartPlatformQuotaResilienceGuard.MaxBackoffMs, "가드 시계 축 " + s.NextAtMs);
            }
            Assert.Equal(2, seen);
            Assert.Equal(0, ap.Quota.GaveUp);
            Assert.Equal(0, e.Published);
            e.Disable();
        }
        finally { ap.Dispose(); factory.Dispose(); }
    }
}
