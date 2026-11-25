using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using L2R = PromptFlowStudio.Services.LiveToReels;
using Ap = PromptFlowStudio.Services.Autopilot;
using Vt = PromptFlowStudio.Services.VerticalTranscode;

namespace PromptFlowStudio.Tests;

/// <summary>Subtask D — 사이클 CTS 전파(Fault · Disable · Dispose) · 번-인/업로드 중 릴 미디어 조기 해제(축출 · 링 덮어쓰기 · Dispose) 차단.</summary>
public class AutopilotCycleCancellationTests
{
    private static Ap.ZeroTouchAutopilotStudioRunner NewRunner(L2R.LiveToReelsFactoryStudioRunner? factory = null) =>
        new(null, factory, null, null, null, new Ap.SimulatedWebhookServer());

    /// <summary>Planning → Broadcasting(사이클 토큰 발급)까지 진행.</summary>
    private static CancellationToken StartCycle(Ap.ZeroTouchAutopilotMasterEngine e, Func<long> clock)
    {
        e.Clock = clock;
        e.Enable();
        e.Tick();
        Assert.Equal(Ap.AutopilotStage.Broadcasting, e.Stage);
        var ct = e.CycleToken;
        Assert.True(ct.CanBeCanceled);
        Assert.False(ct.IsCancellationRequested);
        return ct;
    }

    private static Vt.SegmentedChunkStream Media()
    {
        var s = new Vt.SegmentedChunkStream();
        s.Write(new byte[] { 1, 2, 3, 4 });
        return s;
    }

    private static void WaitUntil(Func<bool> cond, int ms = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!cond()) { Assert.True(sw.ElapsedMilliseconds < ms, "조건 대기 타임아웃"); Thread.Sleep(10); }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Fault_CancelsCycleToken()
    {
        var ap = NewRunner();
        try
        {
            var e = ap.Engine;
            long clock = 1_000;
            e.BroadcastMinMs = 1_000; e.ReactTimeoutMs = 1_000;
            var ct = StartCycle(e, () => clock);
            clock += 2_000; e.Tick();
            Assert.Equal(Ap.AutopilotStage.Reacting, e.Stage);
            clock += 2_000; e.Tick();   // 릴스 미결선 → 수동 클립 실패 → Fault
            Assert.Equal(Ap.AutopilotStage.Cooldown, e.Stage);
            Assert.Equal(1, e.Faults);
            Assert.True(ct.IsCancellationRequested);
            Assert.Equal(1, e.CycleCancels);

            // 다음 사이클 = 새 토큰(이전 취소 상태 비전파)
            e.CooldownMs = 0;
            clock += 10; e.Tick();
            Assert.Equal(Ap.AutopilotStage.Planning, e.Stage);
            clock += 10; e.Tick();
            Assert.False(e.CycleToken.IsCancellationRequested);
            e.Disable();
        }
        finally { ap.Dispose(); }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Disable_CancelsCycleToken_Idempotently()
    {
        var ap = NewRunner();
        try
        {
            var e = ap.Engine;
            long clock = 1_000;
            var ct = StartCycle(e, () => clock);
            e.Disable();
            Assert.True(ct.IsCancellationRequested);
            Assert.Equal(1, e.CycleCancels);
            e.Disable();
            Assert.Equal(1, e.CycleCancels);
        }
        finally { ap.Dispose(); }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Dispose_CancelsCycleToken()
    {
        var ap = NewRunner();
        var e = ap.Engine;
        long clock = 1_000;
        var ct = StartCycle(e, () => clock);
        e.Dispose();
        Assert.True(ct.IsCancellationRequested);
        Assert.False(e.Enabled);
        Assert.False(e.CycleToken.CanBeCanceled);
        ap.Dispose();
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void BurnedReel_DisposeWhileLeased_DefersUntilLastRelease()
    {
        var m = Media();
        var b = new Ap.BurnedReel { ReelId = 7, Media = m, Meta = new() };
        Assert.True(b.TryAcquire());
        Assert.True(b.TryAcquire());
        b.Dispose();   // 링 덮어쓰기/엔진 Dispose 경로
        Assert.True(m.CanRead);
        Assert.True(b.DisposePending);
        Assert.False(b.TryAcquire());   // 해제 요청 후 신규 임대 금지
        b.Release();
        Assert.True(m.CanRead);
        b.Release();
        Assert.False(m.CanRead);
        Assert.True(b.IsDisposed);
        b.Release(); b.Dispose();   // 멱등
        Assert.Equal(0, b.Leases);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void BurnedReel_DisposeWithoutLease_DisposesImmediately()
    {
        var m = Media();
        var b = new Ap.BurnedReel { ReelId = 8, Media = m, Meta = new() };
        b.Dispose();
        Assert.False(m.CanRead);
        Assert.True(b.IsDisposed);
        Assert.False(b.TryAcquire());
    }

    [Fact]
    public void FactoryEviction_And_Remove_SkipPinnedReel_DisposeDeferredUntilUnpin()
    {
        var factory = new L2R.LiveToReelsFactoryStudioRunner();
        try
        {
            factory.UseSyntheticSource(4);
            var req = new L2R.ClimaxClipRequest { Id = 1, StartMs = 0, EndMs = 4_000, Peak = 95, Cause = L2R.ClimaxCause.Physics };
            for (var i = 0; i < L2R.LiveToReelsFactoryStudioRunner.MaxReels; i++) factory.ProcessRequest(in req);
            Assert.Equal(L2R.LiveToReelsFactoryStudioRunner.MaxReels, factory.ReelCount);
            var oldest = factory.Reel(factory.ReelCount - 1)!;   // 축출 1 순위
            Assert.NotNull(oldest.Media);
            Assert.True(oldest.TryPin());
            Assert.True(oldest.InUse);

            factory.ProcessRequest(in req);   // 링 포화 → 축출
            Assert.Same(oldest, factory.FindReel(oldest.Id));
            Assert.NotNull(oldest.Media);

            factory.RemoveReel(oldest.Id);
            Assert.Same(oldest, factory.FindReel(oldest.Id));

            var media = oldest.Media!;
            oldest.Dispose();   // 러너 Dispose 경로
            Assert.Same(media, oldest.Media);
            Assert.True(media.CanRead);
            Assert.True(oldest.DisposePending);
            Assert.False(oldest.TryPin());
            oldest.Unpin();
            Assert.Null(oldest.Media);
            Assert.False(media.CanRead);
            Assert.False(oldest.InUse);
        }
        finally { factory.Dispose(); }
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("dispose")]
    [InlineData("fault")]
    public async Task ActiveBurn_PinsReel_AndObservesCycleCancellation(string how)
    {
        var factory = new L2R.LiveToReelsFactoryStudioRunner();
        factory.UseSyntheticSource(4);
        var ap = NewRunner(factory);
        try
        {
            var e = ap.Engine;
            long clock = 1_000;
            e.BroadcastMinMs = 60_000; e.ReactTimeoutMs = 600_000; e.ClipTimeoutMs = 600_000; e.BurnTimeoutMs = 1_000;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            e.BurnHook = async ct =>
            {
                entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { observed.TrySetResult(); throw; }
            };
            var cycle = StartCycle(e, () => clock);
            var req = new L2R.ClimaxClipRequest { Id = 1, StartMs = 0, EndMs = 4_000, Peak = 95, Cause = L2R.ClimaxCause.Physics };
            factory.ProcessRequest(in req);
            clock += 100; e.Tick();
            Assert.Equal(Ap.AutopilotStage.Reacting, e.Stage);
            clock += 100; e.Tick();
            Assert.Equal(Ap.AutopilotStage.Clipping, e.Stage);
            clock += 100; e.Tick();
            Assert.Equal(Ap.AutopilotStage.BurningIn, e.Stage);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var reel = factory.FindReel(e.CurrentReel)!;
            Assert.True(reel.InUse);
            var media = reel.Media!;
            factory.RemoveReel(reel.Id);   // 번-인 중 축출 시도 → 차단
            Assert.Same(reel, factory.FindReel(reel.Id));
            reel.Dispose();                // 강제 해제 요청 → 지연
            Assert.True(media.CanRead);

            switch (how)
            {
                case "disable": e.Disable(); break;
                case "dispose": e.Dispose(); break;
                default: clock += 2_000; e.Tick(); Assert.Equal(Ap.AutopilotStage.Cooldown, e.Stage); break;   // 번-인 타임아웃 → Fault
            }
            Assert.True(cycle.IsCancellationRequested);
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(10));   // 사이클 취소 관측
            WaitUntil(() => !reel.InUse);
            Assert.Null(reel.Media);        // 마지막 Unpin 에서 지연 해제 수행
            Assert.False(media.CanRead);
            Assert.Null(e.Burned(reel.Id)); // 취소된 사이클 결과물 링 미등록
        }
        finally { ap.Dispose(); factory.Dispose(); }
    }
}
