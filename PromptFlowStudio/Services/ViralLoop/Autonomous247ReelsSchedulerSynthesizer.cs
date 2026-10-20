using PromptFlowStudio.Services.GraphBridge;
using PromptFlowStudio.Services.VerticalTranscode;
using PromptFlowStudio.Services.ViralReels;
namespace PromptFlowStudio.Services.ViralLoop;

public enum LoopJobStage : byte { Empty, Queued, Ingest, Render, Encode, Scheduled, Upload, Published, Failed, Canceled }
public enum LoopRegion : byte { Korea, Japan, UsEast, Spain, Brazil }

/// <summary>파이프라인 작업 슬롯(값 형식).</summary>
public struct LoopJob
{
    public int CurationSlot;
    public ulong SourceId, MediaId;
    public CurationCategory Category;
    public LoopJobStage Stage;
    public LoopRegion Region;
    public bool Immediate;
    public int Hook, Style, StageSteps, Checkpoint;
    public float StageProgress;
    public long EnqueuedTicks, DueTicks, PublishedTicks, Views1h;
    public double DurationSec;
    public float Reward;
}

/// <summary>
/// [PromptFlow Studio v17.50.0] 🚀 24/7 자율 숏폼 리믹스 &amp; 배포 오케스트레이션 합성기(<see cref="Tick"/> 정상 상태 무할당).
/// 4 단계 큐: ① 분해 — v17.47 러너에 릴스 고정 · 3 초 창 분해 + 오리지널리티 가드 Ready 대기(3 창 내 미통과 시 실패) →
/// ② 스튜디오 렌더 — 스타일 프리셋(v17.47 변환 · v17.43 플레어 · v17.46/43 직결 가중치)과 지역 언어(v17.32) 적용 후 워밍업 합성 →
/// ③ GPU 인코딩 — v17.49 트랜스코더 녹화(틱당 프레임 예산) → 4 MB 세그먼트 MP4 보관 → 지역 골든아워 슬롯 산정 →
/// ④ 업로드 — 정시 도달 · 일일 한도 · 최소 간격 · 가드 비동결 시 v17.48 브릿지에 스트림 소유권 이전 → 게시 → 1/6/24 h 인사이트 요청.
/// 인제스트/렌더/인코딩은 한 번에 1 작업, 업로드도 1 작업. 동결 · 취소 시 보관 스트림을 즉시 처분(메모리 누수 0).
/// 시간은 UTC 틱(시뮬레이션 가속 시계 또는 실시간)으로 주입된다.
/// </summary>
public sealed class Autonomous247ReelsSchedulerSynthesizer
{
    public const int Cap = 16, MaxHeldStreams = 3, MaxIngestWindows = 3;
    private static readonly int[] OffsetH = { 9, 9, -5, 1, -3 };
    private static readonly int[] GoldenStart = { 19, 20, 19, 20, 18 }, GoldenEnd = { 22, 23, 21, 22, 21 };
    private static readonly ReelDubLang[] RegionLang = { ReelDubLang.English, ReelDubLang.Japanese, ReelDubLang.English, ReelDubLang.Spanish, ReelDubLang.Spanish };
    public static readonly string[] RegionNames = { "한국", "일본", "미국 동부", "스페인", "브라질" };
    private static readonly long[] CheckpointHours = { 1, 6, 24 };
    private readonly LoopJob[] _jobs = new LoopJob[Cap];
    private readonly Stream?[] _streams = new Stream?[Cap];
    private readonly string?[] _captions = new string?[Cap];
    private readonly long[] _published = new long[64];
    private readonly ViralReelsStudioRunner _reels;
    private readonly VerticalTranscoderStudioRunner _trans;
    private readonly GraphApiBridgeStudioRunner _bridge;
    private readonly RealtimeViralTrendCurationEngine _cur;
    private readonly EngagementFeedbackSelfTuningGuard _fb;
    private int _pubHead, _pubN, _active = -1, _upload = -1, _feedbackJob = -1;
    private long _windowStart;

    public Autonomous247ReelsSchedulerSynthesizer(ViralReelsStudioRunner reels, VerticalTranscoderStudioRunner trans, GraphApiBridgeStudioRunner bridge, RealtimeViralTrendCurationEngine cur, EngagementFeedbackSelfTuningGuard fb)
    { _reels = reels; _trans = trans; _bridge = bridge; _cur = cur; _fb = fb; }

    public int DailyCap { get; set; } = 3;
    public double MinSpacingHours { get; set; } = 3;
    public int EncodeFramesPerTick { get; set; } = 2;
    public int BridgeStepsPerTick { get; set; } = 60;
    public int RenderWarmFrames { get; set; } = 12;
    public int Width { get; set; } = 1080;
    public int Height { get; set; } = 1920;
    public int Fps { get; set; } = 30;
    public double DurationSec { get; set; } = 6;
    public int TargetKbps { get; set; } = 6000;
    public bool AllowMediaFoundation { get; set; } = true;
    public LoopRegion DefaultRegion { get; set; } = LoopRegion.Korea;
    public long Completed { get; private set; }
    public long FailedJobs { get; private set; }
    public long Rescheduled { get; private set; }
    /// <summary>스트림 소유권 계수 — 인수(트랜스코더) = 이관(브릿지) + 처분(취소/실패) 이면 누수 0.</summary>
    public long StreamsTaken { get; private set; }
    public long StreamsHanded { get; private set; }
    public long StreamsDropped { get; private set; }
    public string? LastError { get; private set; }
    public int ActiveJob => _active;
    public int UploadJob => _upload;
    public ref readonly LoopJob Job(int i) => ref _jobs[i];
    public bool HoldsStream(int i) => _streams[i] is not null;
    public string? Caption(int i) => _captions[i];
    public int HeldStreams { get { var n = 0; for (var i = 0; i < Cap; i++) if (_streams[i] is not null) n++; return n; } }
    public static string StageName(LoopJobStage s) => s switch
    {
        LoopJobStage.Queued => "대기",
        LoopJobStage.Ingest => "인제스트·분해",
        LoopJobStage.Render => "스튜디오 합성",
        LoopJobStage.Encode => "GPU 인코딩",
        LoopJobStage.Scheduled => "골든아워 대기",
        LoopJobStage.Upload => "Meta 업로드",
        LoopJobStage.Published => "게시 완료",
        LoopJobStage.Failed => "실패",
        LoopJobStage.Canceled => "취소",
        _ => "—",
    };

    public int Pending
    {
        get { var n = 0; for (var i = 0; i < Cap; i++) if (_jobs[i].Stage is >= LoopJobStage.Queued and <= LoopJobStage.Upload) n++; return n; }
    }

    public int PublishedSince(long fromTicks) { var n = 0; for (var i = 0; i < _pubN; i++) if (_published[i] >= fromTicks) n++; return n; }

    // ══════════════════════ 골든아워(무할당) ══════════════════════
    /// <summary>지역 현지 골든아워 창 안의 가장 이른 시각(≥ notBefore).</summary>
    public static long NextGolden(LoopRegion r, long notBeforeUtc)
    {
        var off = OffsetH[(int)r] * TimeSpan.TicksPerHour;
        var local = notBeforeUtc + off;
        var day = local - local % TimeSpan.TicksPerDay;
        for (var d = 0; d < 3; d++)
        {
            var s = day + d * TimeSpan.TicksPerDay + GoldenStart[(int)r] * TimeSpan.TicksPerHour;
            var e = day + d * TimeSpan.TicksPerDay + GoldenEnd[(int)r] * TimeSpan.TicksPerHour;
            if (local < s) return s - off;
            if (local < e) return local - off;
        }
        return notBeforeUtc;
    }

    public static bool InGolden(LoopRegion r, long utc)
    {
        var h = (int)((utc + OffsetH[(int)r] * TimeSpan.TicksPerHour) % TimeSpan.TicksPerDay / TimeSpan.TicksPerHour);
        return h >= GoldenStart[(int)r] && h < GoldenEnd[(int)r];
    }

    public static int LocalHour(LoopRegion r, long utc) => (int)((utc + OffsetH[(int)r] * TimeSpan.TicksPerHour) % TimeSpan.TicksPerDay / TimeSpan.TicksPerHour);
    public static (int Start, int End) GoldenWindow(LoopRegion r) => (GoldenStart[(int)r], GoldenEnd[(int)r]);

    // ══════════════════════ 큐 ══════════════════════
    /// <summary>큐레이션 슬롯을 작업으로 적재(점유 실패 · 큐 가득 · 동결 시 −1).</summary>
    public int Enqueue(int curationSlot, bool immediate, LoopRegion region, long now)
    {
        if (_fb.IsFrozen || !_cur.TryClaim(curationSlot)) return -1;
        var j = -1;
        for (var i = 0; i < Cap && j < 0; i++) if (_jobs[i].Stage is LoopJobStage.Empty or LoopJobStage.Published or LoopJobStage.Failed or LoopJobStage.Canceled && (_jobs[i].Stage != LoopJobStage.Published || _jobs[i].Checkpoint >= 3)) j = i;
        if (j < 0) { _cur.SetState(curationSlot, CurationState.New); return -1; }
        ref readonly var it = ref _cur.Item(curationSlot);
        ref var job = ref _jobs[j];
        job = default;
        job.CurationSlot = curationSlot; job.SourceId = it.Id; job.Category = it.Category; job.Stage = LoopJobStage.Queued; job.Region = region; job.Immediate = immediate;
        job.Hook = _fb.SampleHook(); job.Style = _fb.SampleStyle(); job.EnqueuedTicks = now; job.DurationSec = DurationSec;
        return j;
    }

    public void Cancel(int j)
    {
        ref var job = ref _jobs[j];
        if (job.Stage is LoopJobStage.Empty or LoopJobStage.Published or LoopJobStage.Failed or LoopJobStage.Canceled) return;
        if (j == _active) { if (_trans.State == TranscodeSessionState.Recording) _trans.CancelRecording(); _active = -1; }
        if (j == _upload) { _bridge.CancelPublish(); _upload = -1; }
        Drop(j);
        job.Stage = LoopJobStage.Canceled;
        _cur.SetState(job.CurationSlot, CurationState.Skipped);
    }

    /// <summary>동결 — 진행 · 대기 작업 전부 취소, 보관 스트림 처분.</summary>
    public void FreezeAll() { for (var i = 0; i < Cap; i++) Cancel(i); }

    private void Drop(int j) { if (_streams[j] is { } s) { s.Dispose(); StreamsDropped++; } _streams[j] = null; }

    // ══════════════════════ 틱 ══════════════════════
    /// <summary>오케스트레이션 1 틱 — 단계 전이 프레임만 할당(캡션 · 스트림), 대기/진행 틱은 무할당.</summary>
    public void Tick(long now)
    {
        if (_fb.IsFrozen) { if (Pending > 0) FreezeAll(); FeedbackTick(now); return; }
        if (_active < 0) _active = NextQueued();
        if (_active >= 0) Advance(_active, now);
        UploadTick(now);
        FeedbackTick(now);
    }

    private int NextQueued()
    {
        if (HeldStreams >= MaxHeldStreams) return -1;
        var best = -1;
        for (var i = 0; i < Cap; i++)
            if (_jobs[i].Stage == LoopJobStage.Queued && (best < 0 || _jobs[i].Immediate && !_jobs[best].Immediate || _jobs[i].Immediate == _jobs[best].Immediate && _jobs[i].EnqueuedTicks < _jobs[best].EnqueuedTicks)) best = i;
        return best;
    }

    private void Advance(int j, long now)
    {
        ref var job = ref _jobs[j];
        switch (job.Stage)
        {
            case LoopJobStage.Queued:
                _cur.SetState(job.CurationSlot, CurationState.InPipeline);
                var st = EngagementFeedbackSelfTuningGuard.StylePresets[job.Style];
                _reels.SetPin(true);
                _reels.SelectReel((int)(job.SourceId % int.MaxValue));
                _reels.SetLevel(st.TransformLevel); _reels.SetFlare(st.Flare); _reels.SetLanguage(RegionLang[(int)job.Region]);
                _windowStart = _reels.Windows;
                job.Stage = LoopJobStage.Ingest; job.StageSteps = 0; job.StageProgress = 0;
                break;
            case LoopJobStage.Ingest:
                if (!_reels.IsRunning) for (var i = 0; i < 30; i++) _reels.Step();
                job.StageSteps++;
                var windows = _reels.Windows - _windowStart;
                job.StageProgress = Math.Min(1f, (float)(_reels.Frame + 180 * Math.Min(windows, 1)) / 360f);
                if (windows >= 1 && _reels.Guard.Verdict == ReelsPublishVerdict.Ready)
                {
                    _captions[j] = EngagementFeedbackSelfTuningGuard.HookTemplates[job.Hook] + "\n" + _reels.BuildCaption();
                    _trans.Configure(Width, Height, Fps, TargetKbps, DurationSec, true, AllowMediaFoundation);
                    var s = EngagementFeedbackSelfTuningGuard.StylePresets[job.Style];
                    _trans.Synth.LensWeight = s.LensWeight * (0.5f + _fb.AdaptiveVfx); _trans.Synth.SetWeight = s.SetWeight;
                    job.Stage = LoopJobStage.Render; job.StageSteps = 0; job.StageProgress = 0;
                }
                else if (windows >= MaxIngestWindows) Fail(j, "오리지널리티 가드 미통과(" + ReelsOriginalityAntiShadowbanGuard.VerdictName(_reels.Guard.Verdict) + ")");
                break;
            case LoopJobStage.Render:
                if (!_trans.IsRunning) for (var i = 0; i < 2; i++) _trans.Step();
                job.StageSteps += 2;
                job.StageProgress = Math.Min(1f, job.StageSteps / (float)RenderWarmFrames);
                if (job.StageSteps >= RenderWarmFrames)
                {
                    if (!_trans.BeginRecording()) { Fail(j, "인코더 열기 실패"); break; }
                    job.Stage = LoopJobStage.Encode; job.StageProgress = 0;
                }
                break;
            case LoopJobStage.Encode:
                if (!_trans.IsRunning) for (var i = 0; i < EncodeFramesPerTick && _trans.State == TranscodeSessionState.Recording; i++) _trans.Step();
                job.StageProgress = (float)_trans.Progress;
                if (_trans.State == TranscodeSessionState.Ready)
                {
                    _streams[j] = _trans.TakeReady();
                    if (_streams[j] is not null) StreamsTaken++;
                    job.DueTicks = Schedule(job.Region, job.Immediate, now);
                    job.Stage = LoopJobStage.Scheduled; job.StageProgress = 1f;
                    _active = -1;
                }
                else if (_trans.State == TranscodeSessionState.Failed) Fail(j, "인코딩 실패");
                break;
        }
    }

    /// <summary>발행 시각 — 즉시 or 지역 골든아워, 최소 간격 · 일일 한도 반영.</summary>
    private long Schedule(LoopRegion r, bool immediate, long now)
    {
        var last = LastPlannedOrPublished();
        var t = Math.Max(now, last + (long)(MinSpacingHours * TimeSpan.TicksPerHour));
        for (var guard = 0; guard < 14; guard++)
        {
            if (!immediate) t = NextGolden(r, t);
            if (PublishedOrPlannedIn(t - TimeSpan.TicksPerDay, t) < DailyCap) return t;
            t += 6 * TimeSpan.TicksPerHour;
        }
        return t;
    }

    private long LastPlannedOrPublished()
    {
        long last = long.MinValue / 2;
        for (var i = 0; i < _pubN; i++) last = Math.Max(last, _published[i]);
        for (var i = 0; i < Cap; i++) if (_jobs[i].Stage is LoopJobStage.Scheduled or LoopJobStage.Upload) last = Math.Max(last, _jobs[i].DueTicks);
        return last;
    }

    private int PublishedOrPlannedIn(long from, long to)
    {
        var n = 0;
        for (var i = 0; i < _pubN; i++) if (_published[i] > from && _published[i] <= to) n++;
        for (var i = 0; i < Cap; i++) if (_jobs[i].Stage is LoopJobStage.Scheduled or LoopJobStage.Upload && _jobs[i].DueTicks > from && _jobs[i].DueTicks <= to) n++;
        return n;
    }

    private void UploadTick(long now)
    {
        if (_upload < 0)
        {
            var next = -1;
            for (var i = 0; i < Cap; i++)
            {
                ref var sj = ref _jobs[i];
                if (sj.Stage != LoopJobStage.Scheduled || sj.DueTicks > now) continue;
                if (!sj.Immediate && !InGolden(sj.Region, now)) { sj.DueTicks = NextGolden(sj.Region, now); Rescheduled++; continue; } // 지연으로 창을 놓치면 다음 골든아워로 이월
                if (next < 0 || sj.DueTicks < _jobs[next].DueTicks) next = i;
            }
            if (next < 0 || _bridge.Dispatcher.IsActive) return;
            var s = _streams[next]; _streams[next] = null;
            if (s is null) { Fail(next, "인코딩 결과 없음"); return; }
            StreamsHanded++;
            if (!_bridge.PublishEncoded(s, _captions[next] ?? "#reels")) { Fail(next, _bridge.LastAction ?? "발행 거부"); return; }
            _upload = next;
            _jobs[next].Stage = LoopJobStage.Upload; _jobs[next].StageProgress = 0;
            return;
        }
        ref var job = ref _jobs[_upload];
        if (!_bridge.IsRunning) for (var i = 0; i < BridgeStepsPerTick && _bridge.Dispatcher.IsActive; i++) _bridge.Step();
        job.StageProgress = (float)_bridge.Dispatcher.Progress;
        var ph = _bridge.Dispatcher.Phase;
        if (ph == ReelsPublishPhase.Published)
        {
            job.MediaId = _bridge.Dispatcher.MediaId; job.PublishedTicks = now; job.Stage = LoopJobStage.Published; job.StageProgress = 1f; job.Checkpoint = 0;
            _published[_pubHead] = now; _pubHead = (_pubHead + 1) % _published.Length; if (_pubN < _published.Length) _pubN++;
            _cur.SetState(job.CurationSlot, CurationState.Published);
            Completed++;
            _upload = -1;
        }
        else if (ph is ReelsPublishPhase.Failed or ReelsPublishPhase.Canceled) { var u = _upload; _upload = -1; Fail(u, _bridge.Dispatcher.LastError ?? "업로드 실패"); }
    }

    private void FeedbackTick(long now)
    {
        if (_feedbackJob >= 0)
        {
            ref var job = ref _jobs[_feedbackJob];
            if (!_bridge.IsRunning) for (var i = 0; i < 4; i++) _bridge.Step();
            if (_bridge.TryTakeInsights(job.MediaId, out var m))
            {
                var cp = job.Checkpoint;
                if (cp == 0) job.Views1h = Math.Max(m.Views, 0);
                var froze = _fb.Observe(job.Hook, job.Style, cp, in m, job.Views1h, job.DurationSec, now);
                job.Reward = _fb.LastReward;
                job.Checkpoint = cp + 1;
                _feedbackJob = -1;
                if (froze) FreezeAll();
            }
            return;
        }
        for (var i = 0; i < Cap; i++)
        {
            ref var job = ref _jobs[i];
            if (job.Stage != LoopJobStage.Published || job.Checkpoint >= 3 || job.MediaId == 0) continue;
            if (now - job.PublishedTicks < CheckpointHours[job.Checkpoint] * TimeSpan.TicksPerHour) continue;
            _bridge.RequestInsights(job.MediaId);
            _feedbackJob = i;
            return;
        }
    }

    private void Fail(int j, string why)
    {
        ref var job = ref _jobs[j];
        if (j == _active) { if (_trans.State == TranscodeSessionState.Recording) _trans.CancelRecording(); _active = -1; }
        Drop(j);
        job.Stage = LoopJobStage.Failed;
        _cur.SetState(job.CurationSlot, CurationState.Skipped);
        FailedJobs++;
        LastError = why;
    }

    /// <summary>미디어 ID → 작업(시뮬레이션 인사이트 모델용 · 없으면 −1).</summary>
    public int FindByMedia(ulong mediaId) { for (var i = 0; i < Cap; i++) if (_jobs[i].MediaId == mediaId && mediaId != 0) return i; return -1; }

    public void DisposeAll() { for (var i = 0; i < Cap; i++) Drop(i); }
}
