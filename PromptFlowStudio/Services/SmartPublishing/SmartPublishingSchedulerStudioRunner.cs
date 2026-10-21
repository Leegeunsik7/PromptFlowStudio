using System.Globalization;
using PromptFlowStudio.Services.LiveShowroom;
using PromptFlowStudio.Services.MotionTypography;
using PromptFlowStudio.Services.Publisher;
using PromptFlowStudio.Services.RetentionOptimizer;
using PromptFlowStudio.Services.RhythmAction;

namespace PromptFlowStudio.Services.SmartPublishing;

/// <summary>
/// [PromptFlow Studio v17.76.0] 📅 스마트 배포 캘린더 &amp; SFX 스튜디오 러너.
///   ▸ SFX: <see cref="ProceduralViralSoundFxSynthesizer"/> 데모 타임라인(v17.52 자막 팝 · v17.54 컷 휘쉬/드롭 임팩트 · v17.68 구매 차칭) → BGM/보컬 베드 위 블록 믹싱 → 파형 min/max 미리보기.
///   ▸ 예약: <see cref="GlobalGoldenHourPublishingScheduler"/> + <see cref="MultiChannelQuotaDistributionGuard"/> — 시계(시뮬레이터 = 가상 · 데모 1 s = 15 분) 틱마다 슬롯 도래 항목을
///     실 쿼터 밸브 통과 후 발행(v17.71 퍼블리셔 초안 업로드 · 없으면 시뮬레이션 발행) · 거부 = 재큐(순차 지연).
///   ▸ v17.75 A/B 승자 확정(<see cref="LiveMetricTrackerAndAbPromoterSynthesizer.OnPhase"/>) → 해당 릴 우선순위 승격 · 승격 API 유닛을 YT 예비분에서 차감.
/// </summary>
public sealed class SmartPublishingSchedulerStudioRunner : IDisposable
{
    public const int PreviewSec = 6, WaveCols = 240, CalendarDays = 7, DemoStepSec = 900, Block = 800;

    private readonly OmniChannelPublisherStudioRunner? _pub;
    private readonly ViralRetentionOptimizerStudioRunner? _retention;
    private readonly float[] _bed = new float[PreviewSec * ProceduralViralSoundFxSynthesizer.Fs];
    private readonly float[] _mixed = new float[PreviewSec * ProceduralViralSoundFxSynthesizer.Fs];
    private readonly int[] _calendar = new int[CalendarDays * 24];
    private readonly object _gate = new();
    private readonly long _simBase;
    private long _simOffset;
    private int _nextDemoReel = 7601;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public SmartPublishingSchedulerStudioRunner(IServiceProvider sp) : this(
        sp.GetService(typeof(OmniChannelPublisherStudioRunner)) as OmniChannelPublisherStudioRunner,
        sp.GetService(typeof(ViralRetentionOptimizerStudioRunner)) as ViralRetentionOptimizerStudioRunner) { }

    public SmartPublishingSchedulerStudioRunner(OmniChannelPublisherStudioRunner? pub = null, ViralRetentionOptimizerStudioRunner? retention = null, bool? simulate = null)
    {
        _pub = pub; _retention = retention;
        Simulated = simulate ?? pub is null || pub.Simulated;
        _simBase = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Scheduler = new GlobalGoldenHourPublishingScheduler(Guard);
        if (_retention is not null) _retention.Promoter.OnPhase += OnAbPhase;
        Dispatcher = DefaultDispatchAsync;
    }

    public event Action? OnChange;
    public ProceduralViralSoundFxSynthesizer Sfx { get; } = new();
    public MultiChannelQuotaDistributionGuard Guard { get; } = new();
    public GlobalGoldenHourPublishingScheduler Scheduler { get; }
    /// <summary>발행 실행기(교체 가능 · 테스트/실 계정) — true = 성공.</summary>
    public Func<ScheduledPost, CancellationToken, Task<bool>> Dispatcher { get; set; }
    public bool Simulated { get; }
    public bool PublisherLinked => _pub is not null;
    public bool RetentionLinked => _retention is not null;
    public long Now => Simulated ? _simBase + Interlocked.Read(ref _simOffset) : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    public bool Running => _loop is { IsCompleted: false };
    public string? LastAction { get; private set; }
    public long Dispatched { get; private set; }
    public long DispatchFailures { get; private set; }
    public long Requeues { get; private set; }
    public long AbPromotions { get; private set; }

    // ── SFX 미리보기(UI) ──
    public float[] WaveMin { get; } = new float[WaveCols];
    public float[] WaveMax { get; } = new float[WaveCols];
    public float[] BedMax { get; } = new float[WaveCols];
    public bool PreviewReady { get; private set; }
    public double PreviewMixUs { get; private set; }
    public ReadOnlySpan<float> Mixed => _mixed;

    public void Advance(long seconds) => Interlocked.Add(ref _simOffset, seconds);

    // ══════════════════════ SFX 데모 ══════════════════════
    /// <summary>데모 타임라인 큐 구성 → 베드 합성 → 블록 믹싱 → 파형 미리보기.</summary>
    public int BuildSfxPreview(RealtimeWordByWordMotionTypographyEngine? typo = null)
    {
        lock (_gate)
        {
            Sfx.ClearCues();
            if (typo is null)
            {
                typo = new RealtimeWordByWordMotionTypographyEngine(new SdfGlyphAtlas());
                typo.SetCaption("이 장면 진짜 실화인가요 끝까지 보세요", TypoLang.Ko, 400);
            }
            Sfx.CollectCaptions(typo);
            Span<RhythmCutEvent> cuts = stackalloc RhythmCutEvent[4];
            cuts[0] = new RhythmCutEvent { PresentSec = 1.2, Reason = RhythmCutReason.Phrase };
            cuts[1] = new RhythmCutEvent { PresentSec = 2.4, Reason = RhythmCutReason.Beat };
            cuts[2] = new RhythmCutEvent { PresentSec = 3.6, Reason = RhythmCutReason.Drop };
            cuts[3] = new RhythmCutEvent { PresentSec = 4.8, Reason = RhythmCutReason.Beat };
            Sfx.CollectCuts(cuts);
            Span<PurchaseEvent> buys = stackalloc PurchaseEvent[2];
            buys[0] = new PurchaseEvent { Qty = 1, AtTick = 1000 + 252 };   // 60 Hz 틱 → 4.2 s
            buys[1] = new PurchaseEvent { Qty = 3, AtTick = 1000 + 318 };   // 5.3 s
            Sfx.CollectPurchases(buys, 1000, 60);
            SynthesizeBed(_bed);
            _bed.AsSpan().CopyTo(_mixed);
            var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (var b = 0; b < _mixed.Length; b += Block) Sfx.Mix(_mixed.AsSpan(b, Math.Min(Block, _mixed.Length - b)), 1, b);
            PreviewMixUs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1e6 / System.Diagnostics.Stopwatch.Frequency;
            Peaks(_mixed, WaveMin, WaveMax);
            Peaks(_bed, null, BedMax);
            PreviewReady = true;
        }
        LastAction = "🔊 SFX 타임라인 " + Sfx.CueCount.ToString(CultureInfo.InvariantCulture) + " 큐 믹싱 · " + PreviewMixUs.ToString("0", CultureInfo.InvariantCulture) + " µs";
        Raise();
        return Sfx.CueCount;
    }

    /// <summary>BGM(110 BPM 킥 + 화음 패드 · −20 dBFS) + 보컬 대역(200~800 Hz 음절 변조) 데모 베드.</summary>
    public static void SynthesizeBed(Span<float> dst)
    {
        const int Fs = ProceduralViralSoundFxSynthesizer.Fs;
        for (var i = 0; i < dst.Length; i++)
        {
            var t = (double)i / Fs;
            var beat = t % (60.0 / 110);
            var kick = Math.Sin(2 * Math.PI * 60 * beat) * Math.Exp(-beat / 0.08) * 0.12;
            var pad = (Math.Sin(2 * Math.PI * 220 * t) + Math.Sin(2 * Math.PI * 277.2 * t) + Math.Sin(2 * Math.PI * 329.6 * t)) * 0.02;
            var voice = Math.Sin(2 * Math.PI * (300 + 120 * Math.Sin(2 * Math.PI * 3 * t)) * t) * (0.5 + 0.5 * Math.Abs(Math.Sin(2 * Math.PI * 4 * t))) * 0.07;
            dst[i] = (float)(kick + pad + voice);
        }
    }

    private static void Peaks(ReadOnlySpan<float> s, float[]? min, float[] max)
    {
        var per = Math.Max(1, s.Length / max.Length);
        for (var c = 0; c < max.Length; c++)
        {
            float lo = 0, hi = 0; var a = c * per; var b = Math.Min(s.Length, a + per);
            for (var i = a; i < b; i++) { var v = s[i]; if (v < lo) lo = v; if (v > hi) hi = v; }
            if (min is not null) min[c] = lo;
            max[c] = min is null ? Math.Max(hi, -lo) : hi;
        }
    }

    // ══════════════════════ 예약 · 디스패치 ══════════════════════
    /// <summary>릴 예약(지금 준비 완료 · 대상 권역/플랫폼) — 반환 = 슬롯 시각(0 = 거부/배치 불가).</summary>
    public long Schedule(int reelId, PublishPlatforms platforms = PublishPlatforms.Both, GoldenRegions regions = GoldenRegions.All, byte priority = 0, bool abWinner = false, object? payload = null)
    {
        long slot;
        lock (_gate)
        {
            var i = Scheduler.Enqueue(reelId, Now, platforms, regions, priority, abWinner, payload, Now);
            slot = i >= 0 && Scheduler.At(i).State == ScheduledState.Queued ? Scheduler.At(i).SlotUnix : 0;
        }
        LastAction = slot > 0 ? "📅 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " 예약 → " + Kst(slot) + " KST" : "⚠ 릴 #" + reelId.ToString(CultureInfo.InvariantCulture) + " 예약 거부(중복/포화/배치 불가)";
        Raise();
        return slot;
    }

    /// <summary>데모 일괄 예약(8 건 · 우선순위 혼합 · 1 건 A/B 승자).</summary>
    public int ScheduleDemoBatch(int count = 8)
    {
        var ok = 0;
        for (var k = 0; k < count; k++)
        {
            var id = _nextDemoReel++;
            var regions = (k % 3) switch { 0 => GoldenRegions.All, 1 => GoldenRegions.Asia, _ => GoldenRegions.NorthAmerica | GoldenRegions.Europe };
            if (Schedule(id, k % 4 == 3 ? PublishPlatforms.YouTube : PublishPlatforms.Both, regions, (byte)(k % 3), k == count - 1) > 0) ok++;
        }
        LastAction = "📅 데모 " + ok.ToString(CultureInfo.InvariantCulture) + "/" + count.ToString(CultureInfo.InvariantCulture) + " 건 예약 · 지연 " + Scheduler.Deferrals.ToString(CultureInfo.InvariantCulture);
        Raise();
        return ok;
    }

    /// <summary>1 틱: 슬롯 도래 항목 → 실 쿼터 소비 → 발행 · 거부 = 다음 허용 시각으로 재큐.</summary>
    public async Task<int> TickAsync(CancellationToken ct = default)
    {
        var done = 0;
        for (var guard = 0; guard < GlobalGoldenHourPublishingScheduler.Cap; guard++)
        {
            ScheduledPost p; var now = Now;
            lock (_gate) { if (!Scheduler.TakeDue(now, out p)) break; }
            var allowed = true; var retryAt = now;
            lock (_gate)
            {
                // 양 플랫폼 사전 확인 → 둘 다 통과할 때만 소비(부분 소비 방지)
                if ((p.Platforms & PublishPlatforms.YouTube) != 0 && Guard.NextAllowed(PublishPlatforms.YouTube, now) > now) { allowed = false; retryAt = Math.Max(retryAt, Guard.NextAllowed(PublishPlatforms.YouTube, now)); }
                if ((p.Platforms & PublishPlatforms.Instagram) != 0 && Guard.NextAllowed(PublishPlatforms.Instagram, now) > now) { allowed = false; retryAt = Math.Max(retryAt, Guard.NextAllowed(PublishPlatforms.Instagram, now)); }
                if (allowed)
                {
                    if ((p.Platforms & PublishPlatforms.YouTube) != 0) Guard.TryConsume(PublishPlatforms.YouTube, now);
                    if ((p.Platforms & PublishPlatforms.Instagram) != 0) Guard.TryConsume(PublishPlatforms.Instagram, now);
                }
            }
            if (!allowed)
            {
                lock (_gate) Scheduler.Enqueue(p.ReelId, retryAt, p.Platforms, p.Regions, p.Priority, p.AbWinner, p.Payload, now);
                Requeues++;
                continue;
            }
            bool ok;
            try { ok = await Dispatcher(p, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { ok = false; PromptFlowStudio.Program.Log("SmartPublishing.Dispatch", ex); }
            lock (_gate) Scheduler.Record(in p, now, ok);
            if (ok) Dispatched++; else DispatchFailures++;
            LastAction = (ok ? "🚀 " : "⚠ ") + GlobalGoldenHourPublishingScheduler.RegionFlag(p.SlotRegion) + " 릴 #" + p.ReelId.ToString(CultureInfo.InvariantCulture) + (ok ? " 골든타임 발행" : " 발행 실패") + " · " + Kst(now) + " KST";
            done++;
        }
        if (done > 0) Raise();
        return done;
    }

    /// <summary>기본 발행기: v17.71 초안이 있으면 실제 업로드 · 없으면 시뮬레이션 성공.</summary>
    private async Task<bool> DefaultDispatchAsync(ScheduledPost p, CancellationToken ct)
    {
        if (_pub?.FindDraft(p.ReelId) is not null && _pub.MediaFor(p.ReelId) is { } media)
        {
            var yt = _pub.UploadYouTube; var ig = _pub.UploadInstagram;
            try
            {
                _pub.UploadYouTube = (p.Platforms & PublishPlatforms.YouTube) != 0; _pub.UploadInstagram = (p.Platforms & PublishPlatforms.Instagram) != 0;
                return await _pub.UploadDraftAsync(p.ReelId, media).ConfigureAwait(false) > 0;
            }
            finally { _pub.UploadYouTube = yt; _pub.UploadInstagram = ig; }
        }
        return Simulated;
    }

    /// <summary>가상 시계 전진 + 틱.</summary>
    public async Task StepAsync(long seconds = DemoStepSec, CancellationToken ct = default)
    {
        if (Simulated) Advance(seconds);
        await TickAsync(ct).ConfigureAwait(false);
        Raise();
    }

    public void Start()
    {
        if (Running) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await StepAsync(Simulated ? DemoStepSec : 0, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex) { LastAction = "⚠ " + ex.Message; PromptFlowStudio.Program.Log("SmartPublishing.Loop", ex); }
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }, ct);
        LastAction = Simulated ? "▶ 자동 진행(1 s = 가상 15 분)" : "▶ 실시간 예약 발행"; Raise();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(5_000); } catch (AggregateException) { }
        _cts?.Dispose(); _cts = null; _loop = null;
        Raise();
    }

    private void OnAbPhase(TrackedAbTest t)
    {
        if (t.Phase != AbTestPhase.Promoted) return;
        bool promoted;
        lock (_gate)
        {
            if (t.RemotePatched) Guard.ConsumeYtUnits(Now, 2 * 50);   // videos.update + thumbnails.set
            promoted = Scheduler.Promote(t.ReelId);
        }
        if (promoted) { AbPromotions++; LastAction = "🏆 릴 #" + t.ReelId.ToString(CultureInfo.InvariantCulture) + " A/B 승자 → 예약 우선순위 승격"; Raise(); }
    }

    // ══════════════════════ UI 보조 ══════════════════════
    /// <summary>KST 오늘 자정(UTC unix).</summary>
    public long KstMidnight() { var off = 9 * 3600L; return (Now + off) / 86_400 * 86_400 - off; }

    public ReadOnlySpan<int> Calendar()
    {
        lock (_gate) Scheduler.FillCalendar(_calendar, KstMidnight(), CalendarDays);
        return _calendar;
    }

    /// <summary>KST 시(0~23) 가 권역 골든타임이면 비트(1 = 아시아 · 2 = 북미 · 4 = 유럽).</summary>
    public static int GoldenMaskAtKstHour(int kstHour)
    {
        var m = 0;
        for (var r = 0; r < GlobalGoldenHourPublishingScheduler.Regions; r++)
        {
            var local = ((kstHour - 9 + GlobalGoldenHourPublishingScheduler.UtcOffsetHours[r]) % 24 + 24) % 24;
            if (local >= GlobalGoldenHourPublishingScheduler.GoldenStartHour[r] && local < GlobalGoldenHourPublishingScheduler.GoldenEndHour[r]) m |= 1 << r;
        }
        return m;
    }

    public static string Kst(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).ToOffset(TimeSpan.FromHours(9)).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("SmartPublishing.OnChange", ex); }
    }

    public void Dispose()
    {
        Stop();
        if (_retention is not null) _retention.Promoter.OnPhase -= OnAbPhase;
        OnChange = null;
    }

    public string Summary() =>
        "SFX " + Sfx.CueCount.ToString(CultureInfo.InvariantCulture) + " 큐 · 예약 " + Scheduler.Count.ToString(CultureInfo.InvariantCulture) + " · 지연 " + Scheduler.Deferrals.ToString(CultureInfo.InvariantCulture)
        + " · 발행 " + Dispatched.ToString(CultureInfo.InvariantCulture) + " · YT " + Guard.YtUsedToday.ToString(CultureInfo.InvariantCulture) + "/" + MultiChannelQuotaDistributionGuard.YtDailyUnits.ToString(CultureInfo.InvariantCulture);
}
