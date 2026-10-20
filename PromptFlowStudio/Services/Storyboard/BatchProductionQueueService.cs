using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.16.0 대량 생산 작업 단계 — 제출 ➔ 렌더 ➔ 후처리 ➔ OSMU 패키징.</summary>
public enum BatchStage { Queued, Submitting, Rendering, PostProduction, OsmuPackaging, Completed, Failed, Canceled }

/// <summary>v16.16.0 작업 우선순위(높을수록 먼저 · 동순위는 등록 순).</summary>
public enum BatchPriority { Low = 0, Normal = 1, High = 2, Urgent = 3 }

/// <summary>v16.16.0 작업 변경 브로드캐스트 페이로드(불변 스냅숏 · 값형).</summary>
public readonly record struct BatchJobSnapshot(string Id, string Title, BatchStage Stage, int Percent, BatchPriority Priority, int Attempts, string StatusText);

/// <summary>v16.16.0 작업 실행 결과.</summary>
public sealed record BatchJobOutcome(bool Ok, string? OutputPath, string Message);

/// <summary>v16.16.0 대량 생산 작업 1건(살아있는 엔티티 — UI 는 스냅숏/속성 읽기만).</summary>
public sealed class BatchJob
{
    internal BatchJob(string id, string title, StoryboardProject project, BatchPriority priority, long seq, string? themeKey)
    {
        Id = id; Title = title; Project = project; Priority = priority; Sequence = seq; ThemeKey = themeKey;
    }

    public string Id { get; }
    public string Title { get; }
    public string? ThemeKey { get; }
    public StoryboardProject Project { get; }
    public BatchPriority Priority { get; internal set; }
    internal long Sequence { get; set; }
    // volatile — 잠금 밖 읽기에서 결과 필드(Error/OutputPath) 기록 후 Stage 공개 순서 보장(release/acquire).
    private volatile BatchStage _stage = BatchStage.Queued;
    public BatchStage Stage { get => _stage; internal set => _stage = value; }
    /// <summary>전체 진행률 0~100(단계 가중 합산).</summary>
    public int Percent { get; internal set; }
    public string StatusText { get; internal set; } = "대기";
    public string? Error { get; internal set; }
    public string? OutputPath { get; internal set; }
    public int Attempts { get; internal set; }
    public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; internal set; }
    public DateTimeOffset? FinishedAt { get; internal set; }
    internal CancellationTokenSource? Cts { get; set; }

    public bool IsActive => Stage is BatchStage.Submitting or BatchStage.Rendering or BatchStage.PostProduction or BatchStage.OsmuPackaging;
    public bool IsTerminal => Stage is BatchStage.Completed or BatchStage.Failed or BatchStage.Canceled;
    public bool CanRetry => Stage is BatchStage.Failed or BatchStage.Canceled;
    public BatchJobSnapshot Snapshot => new(Id, Title, Stage, Percent, Priority, Attempts, StatusText);
}

/// <summary>v16.16.0 러너 → 큐 진행 보고(단계 내 0~1 · 일시정지 체크포인트).</summary>
public interface IBatchJobReporter
{
    void Report(BatchStage stage, double stageProgress, string? text = null);
    /// <summary>단계 경계 체크포인트 — 일시정지 중이면 재개까지 대기(취소 가능).</summary>
    Task CheckpointAsync(CancellationToken ct);
}

/// <summary>v16.16.0 작업 1건 실행 계약(테스트·대체 파이프라인 주입).</summary>
public interface IBatchJobRunner
{
    Task<BatchJobOutcome> RunAsync(BatchJob job, IBatchJobReporter reporter, QueueResourceGovernor governor, CancellationToken ct);
}

/// <summary>
/// [PromptFlowStudio] v16.16.0 🏭 대량 생산 작업 큐 오케스트레이터 — 204+ 테마/스토리보드 일괄 등록 → 백그라운드 워커 → 거버너 슬롯 내 동시 실행.
///   ▸ 조작: 등록(테마 다중 선택/프로젝트) · 일시정지(신규 디스패치 중단 + 진행 작업은 단계 경계에서 대기) · 재개 · 우선순위 변경 · 개별 취소 · 1-Click 재시도.
///   ▸ 진행률: 단계 가중(제출 5 · 렌더 60 · 후처리 25 · OSMU 10) → 0~100 · 변경마다 <see cref="OnJobChanged"/>(스냅숏) + <see cref="OnChange"/> 방송(구독자 예외 격리).
///   ▸ ETA: 완료 작업 소요 EMA(초기 <see cref="DefaultJobSeconds"/>) × (대기 + 진행 잔여) / 동시 한도.
///   ▸ 디스패치: <see cref="QueueResourceGovernor.WaitIfCoolingAsync"/>(429 쿨다운) → <see cref="QueueResourceGovernor.AcquireAsync"/>(VRAM/RAM 한도) → 최고 우선순위 작업.
///   ▸ 작업 실패는 개별 Failed 로 마감하고 다음 작업 계속(Graceful) · 이력 상한 <see cref="MaxJobs"/>.
/// </summary>
public sealed class BatchProductionQueueService : IDisposable
{
    public const int MaxJobs = 512;
    public const double DefaultJobSeconds = 240;
    private const double EmaAlpha = 0.3;

    private readonly IBatchJobRunner _runner;
    private readonly QueueResourceGovernor _governor;
    private readonly object _gate = new();
    private readonly List<BatchJob> _jobs = new();
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private TaskCompletionSource<bool> _resumed = NewResumed(true);
    private Action[] _changeSubs = Array.Empty<Action>();
    private Action<BatchJobSnapshot>[] _jobSubs = Array.Empty<Action<BatchJobSnapshot>>();
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private long _seq;
    private int _idSeq;
    private double _avgSeconds = DefaultJobSeconds;
    private long _subscriberErrors;

    private readonly Telemetry.ProductionCostTrackerService? _cost;
    private int _budgetPaused;

    public BatchProductionQueueService(IBatchJobRunner runner, QueueResourceGovernor governor, Telemetry.ProductionCostTrackerService? cost = null)
    {
        _runner = runner;
        _governor = governor;
        _cost = cost;
        if (_cost is not null)
        {
            _cost.OnBudgetExceeded += OnBudgetExceeded;
            _cost.OnBudgetRecovered += OnBudgetRecovered;
        }
    }

    /// <summary>v16.18.0 예산 한도로 자동 일시정지된 상태(사용자 일시정지와 구분 · 한도 해제 시 자동 재개).</summary>
    public bool IsBudgetPaused => Volatile.Read(ref _budgetPaused) != 0;

    private void OnBudgetExceeded(string reason)
    {
        if (IsPaused) return;                       // 사용자 일시정지는 그대로 둔다
        Interlocked.Exchange(ref _budgetPaused, 1);
        Pause();
    }

    private void OnBudgetRecovered()
    {
        if (Interlocked.Exchange(ref _budgetPaused, 0) == 1) Resume();
    }

    /// <summary>큐 전체 변경(목록/상태) — 컴포넌트는 InvokeAsync(StateHasChanged) 로 마샬링.</summary>
    public event Action? OnChange
    {
        add { if (value is not null) lock (_gate) _changeSubs = Append(_changeSubs, value); }
        remove { if (value is not null) lock (_gate) _changeSubs = Remove(_changeSubs, value); }
    }

    /// <summary>작업 단위 진행 브로드캐스트(값형 스냅숏 · 박싱 0).</summary>
    public event Action<BatchJobSnapshot>? OnJobChanged
    {
        add { if (value is not null) lock (_gate) _jobSubs = Append(_jobSubs, value); }
        remove { if (value is not null) lock (_gate) _jobSubs = Remove(_jobSubs, value); }
    }

    public QueueResourceGovernor Governor => _governor;
    public bool IsPaused => !Volatile.Read(ref _resumed).Task.IsCompleted;
    public bool IsRunning => Volatile.Read(ref _worker) is { IsCompleted: false };
    public long SubscriberErrors => Interlocked.Read(ref _subscriberErrors);
    public double AverageJobSeconds { get { lock (_gate) return _avgSeconds; } }

    /// <summary>표시 순서(진행 → 대기[우선순위·등록순] → 종료) 스냅숏 목록.</summary>
    public IReadOnlyList<BatchJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                var list = new List<BatchJob>(_jobs);
                list.Sort(DisplayOrder);
                return list;
            }
        }
    }

    public int Count(BatchStage stage)
    {
        lock (_gate)
        {
            var n = 0;
            foreach (var j in _jobs) if (j.Stage == stage) n++;
            return n;
        }
    }

    public int ActiveCount
    {
        get { lock (_gate) { var n = 0; foreach (var j in _jobs) if (j.IsActive) n++; return n; } }
    }

    public BatchJob? Find(string id)
    {
        lock (_gate) { foreach (var j in _jobs) if (j.Id == id) return j; }
        return null;
    }

    /// <summary>프로젝트 1건 등록. 상한 초과 시 종료 작업부터 정리, 그래도 가득이면 null.</summary>
    public BatchJob? Enqueue(StoryboardProject project, BatchPriority priority = BatchPriority.Normal, string? title = null, string? themeKey = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        BatchJob job;
        lock (_gate)
        {
            if (_jobs.Count >= MaxJobs) _jobs.RemoveAll(static j => j.IsTerminal && j.Stage != BatchStage.Failed);
            if (_jobs.Count >= MaxJobs) return null;
            var id = "b" + Interlocked.Increment(ref _idSeq).ToString("x4", CultureInfo.InvariantCulture);
            job = new BatchJob(id, string.IsNullOrWhiteSpace(title) ? project.Title : title!, project, priority, ++_seq, themeKey);
            _jobs.Add(job);
        }
        Changed(job);
        _wake.Release();
        return job;
    }

    /// <summary>테마 다중 선택 일괄 등록 — build(themeKey) 가 프로젝트를 구성(null 이면 건너뜀). 반환: 등록 수.</summary>
    public int EnqueueThemes(IReadOnlyList<string> themeKeys, Func<string, StoryboardProject?> build, BatchPriority priority = BatchPriority.Normal)
    {
        ArgumentNullException.ThrowIfNull(themeKeys);
        ArgumentNullException.ThrowIfNull(build);
        var n = 0;
        foreach (var key in themeKeys)
        {
            if (string.IsNullOrWhiteSpace(key)) continue;
            StoryboardProject? p;
            try { p = build(key); }
            catch (Exception ex) { Program.Log("BatchQueue.BuildTheme", ex); continue; }
            if (p is not null && Enqueue(p, priority, null, key) is not null) n++;
        }
        return n;
    }

    public bool SetPriority(string id, BatchPriority priority)
    {
        var j = Find(id);
        if (j is null || j.IsTerminal || j.Priority == priority) return false;
        lock (_gate) j.Priority = priority;
        Changed(j);
        return true;
    }

    /// <summary>대기 작업을 맨 앞으로(최고 우선순위 + 등록 순서 선두).</summary>
    public bool MoveToFront(string id)
    {
        var j = Find(id);
        if (j is null || j.Stage != BatchStage.Queued) return false;
        lock (_gate)
        {
            long min = long.MaxValue;
            foreach (var x in _jobs) if (x.Sequence < min) min = x.Sequence;
            j.Priority = BatchPriority.Urgent;
            j.Sequence = min - 1;
        }
        Changed(j);
        return true;
    }

    /// <summary>개별 취소 — 대기면 즉시 Canceled, 진행 중이면 CTS 취소(러너가 정리 후 Canceled).</summary>
    public bool Cancel(string id)
    {
        var j = Find(id);
        if (j is null || j.IsTerminal) return false;
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            if (j.Stage == BatchStage.Queued) { j.StatusText = "취소됨"; j.FinishedAt = DateTimeOffset.Now; j.Stage = BatchStage.Canceled; }
            else cts = j.Cts;
        }
        if (cts is not null) { try { cts.Cancel(); } catch (ObjectDisposedException) { } }
        Changed(j);
        return true;
    }

    /// <summary>1-Click 재시도 — 실패/취소 작업을 대기열로(완료 컷은 프로젝트에 보존돼 재과금 0).</summary>
    public bool Retry(string id)
    {
        var j = Find(id);
        if (j is null || !j.CanRetry) return false;
        lock (_gate)
        {
            j.Percent = 0; j.StatusText = "재시도 대기"; j.Error = null;
            j.FinishedAt = null; j.StartedAt = null; j.Sequence = ++_seq;
            j.Stage = BatchStage.Queued;
        }
        Changed(j);
        _wake.Release();
        return true;
    }

    public int RetryAllFailed()
    {
        var n = 0;
        foreach (var j in Jobs) if (j.Stage == BatchStage.Failed && Retry(j.Id)) n++;
        return n;
    }

    /// <summary>종료(완료/취소) 작업 정리 — 실패는 재시도 위해 유지.</summary>
    public int ClearFinished()
    {
        int n;
        lock (_gate) n = _jobs.RemoveAll(static j => j.Stage is BatchStage.Completed or BatchStage.Canceled);
        if (n > 0) RaiseChange();
        return n;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_resumed.Task.IsCompleted) return;
            _resumed = NewResumed(false);
        }
        RaiseChange();
    }

    public void Resume()
    {
        Interlocked.Exchange(ref _budgetPaused, 0);
        TaskCompletionSource<bool> r;
        lock (_gate) r = _resumed;
        if (!r.TrySetResult(true)) return;
        _wake.Release();
        RaiseChange();
    }

    /// <summary>백그라운드 워커 시작(중복 호출 무시).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_worker is { IsCompleted: false }) return;
            _workerCts?.Dispose();
            _workerCts = new CancellationTokenSource();
            var ct = _workerCts.Token;
            _worker = Task.Run(() => WorkerLoopAsync(ct));
        }
        RaiseChange();
    }

    /// <summary>워커 정지 + 진행 작업 전체 취소.</summary>
    public async Task StopAsync()
    {
        Task? w;
        lock (_gate)
        {
            w = _worker;
            try { _workerCts?.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var j in _jobs) { try { j.Cts?.Cancel(); } catch (ObjectDisposedException) { } }
        }
        Resume();
        if (w is not null) { try { await w.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        RaiseChange();
    }

    /// <summary>전체 예상 잔여 시간 — EMA 평균 × (대기 + 진행 잔여) / 동시 한도.</summary>
    public TimeSpan EstimateRemaining()
    {
        double work = 0;
        lock (_gate)
        {
            foreach (var j in _jobs)
            {
                if (j.Stage == BatchStage.Queued) work += _avgSeconds;
                else if (j.IsActive) work += _avgSeconds * (1 - j.Percent / 100.0);
            }
        }
        return TimeSpan.FromSeconds(Math.Round(work / Math.Max(1, _governor.CurrentLimit)));
    }

    /// <summary>개별 작업 예상 완료까지(대기열 위치 반영).</summary>
    public TimeSpan EstimateFor(string id)
    {
        lock (_gate)
        {
            var target = FindLocked(id);
            if (target is null || target.IsTerminal) return TimeSpan.Zero;
            if (target.IsActive) return TimeSpan.FromSeconds(Math.Round(_avgSeconds * (1 - target.Percent / 100.0)));
            var ahead = 0;
            foreach (var j in _jobs) if (j.Stage == BatchStage.Queued && j != target && QueueOrder(j, target) < 0) ahead++;
            var limit = Math.Max(1, _governor.CurrentLimit);
            return TimeSpan.FromSeconds(Math.Round(_avgSeconds * (ahead / limit + 1)));
        }
    }

    /// <summary>단계 가중 → 전체 % (순수 함수).</summary>
    public static int OverallPercent(BatchStage stage, double stageProgress)
    {
        var p = Math.Clamp(double.IsFinite(stageProgress) ? stageProgress : 0, 0, 1);
        var (from, span) = stage switch
        {
            BatchStage.Submitting => (0, 5),
            BatchStage.Rendering => (5, 60),
            BatchStage.PostProduction => (65, 25),
            BatchStage.OsmuPackaging => (90, 10),
            BatchStage.Completed => (100, 0),
            _ => (0, 0),
        };
        return Math.Min(100, from + (int)Math.Round(span * p));
    }

    public static string StageLabel(BatchStage s) => s switch
    {
        BatchStage.Queued => "⏳ 대기",
        BatchStage.Submitting => "📤 제출",
        BatchStage.Rendering => "🎞️ 렌더",
        BatchStage.PostProduction => "🎚️ 후처리",
        BatchStage.OsmuPackaging => "🌐 OSMU",
        BatchStage.Completed => "✅ 완료",
        BatchStage.Failed => "⚠ 실패",
        _ => "⛔ 취소",
    };

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        var running = new List<Task>(8);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Task<bool> resumed;
                lock (_gate) resumed = _resumed.Task;
                if (!resumed.IsCompleted) { await resumed.WaitAsync(ct).ConfigureAwait(false); continue; }

                if (!HasQueued()) { await _wake.WaitAsync(ct).ConfigureAwait(false); continue; }

                // v16.18.0 예산 한도 → 신규 디스패치 대신 자동 일시정지(진행 중 작업은 유지).
                if (_cost is { IsVideoGenerationBlocked: true }) { OnBudgetExceeded("budget"); continue; }

                await _governor.WaitIfCoolingAsync(ct).ConfigureAwait(false);
                var lease = await _governor.AcquireAsync(ct).ConfigureAwait(false);
                BatchJob? job = null;
                lock (_gate)
                {
                    if (_resumed.Task.IsCompleted)
                    {
                        job = NextQueuedLocked();
                        if (job is not null)
                        {
                            job.Stage = BatchStage.Submitting; job.Percent = 0; job.StatusText = "제출 준비";
                            job.StartedAt = DateTimeOffset.Now; job.Attempts++;
                            job.Cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        }
                    }
                }
                if (job is null) { lease.Dispose(); continue; }
                Changed(job);
                running.RemoveAll(static t => t.IsCompleted);
                running.Add(RunJobAsync(job, lease));
            }
        }
        catch (OperationCanceledException) { /* 정지 */ }
        catch (Exception ex) { Program.Log("BatchQueue.Worker", ex); }
        finally
        {
            try { await Task.WhenAll(running).ConfigureAwait(false); } catch { /* 개별 작업에서 처리됨 */ }
        }
    }

    private async Task RunJobAsync(BatchJob job, GovernorLease lease)
    {
        var started = Environment.TickCount64;
        var cts = job.Cts!;
        // v16.18.0 비용 귀속 스코프(AsyncLocal → 러너 하위 Wan/Gemini/TTS 호출 비용이 이 작업·테마·플랫폼에 집계).
        using var scope = _cost?.BeginScope(job.Title, job.ThemeKey, job.Project.TargetPlatform.ToString());
        try
        {
            var outcome = await Task.Run(() => _runner.RunAsync(job, new Reporter(this, job), _governor, cts.Token), cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // ⚠ Stage 는 마지막에 기록 — 잠금 밖 관찰자(UI·폴링)가 종료 단계를 본 시점엔 결과 필드가 이미 채워져 있어야 한다.
                if (cts.IsCancellationRequested) { job.StatusText = "취소됨"; job.Stage = BatchStage.Canceled; }
                else if (outcome.Ok)
                {
                    job.OutputPath = outcome.OutputPath; job.Percent = 100; job.StatusText = "완료 — " + outcome.Message; job.Stage = BatchStage.Completed;
                    if (scope is not null) scope.Succeeded = true;
                    var sec = Math.Max(1, (Environment.TickCount64 - started) / 1000.0);
                    _avgSeconds = _avgSeconds * (1 - EmaAlpha) + sec * EmaAlpha;
                }
                else { job.Error = outcome.Message; job.StatusText = "실패"; job.Stage = BatchStage.Failed; }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { job.StatusText = "취소됨"; job.Stage = BatchStage.Canceled; }
        }
        catch (Exception ex)
        {
            Program.Log("BatchQueue.RunJob", ex);
            lock (_gate) { job.Error = ex.Message; job.StatusText = "실패"; job.Stage = BatchStage.Failed; }
        }
        finally
        {
            lock (_gate) { job.FinishedAt = DateTimeOffset.Now; job.Cts = null; }
            cts.Dispose();
            lease.Dispose();
            Changed(job);
            _wake.Release();
        }
    }

    private sealed class Reporter(BatchProductionQueueService owner, BatchJob job) : IBatchJobReporter
    {
        private int _lastPct = -1;
        private BatchStage _lastStage = BatchStage.Queued;

        public void Report(BatchStage stage, double stageProgress, string? text = null)
        {
            if (stage is BatchStage.Queued or BatchStage.Completed or BatchStage.Failed or BatchStage.Canceled) return;
            var pct = OverallPercent(stage, stageProgress);
            lock (owner._gate)
            {
                if (job.IsTerminal) return;
                if (pct == _lastPct && stage == _lastStage && text is null) return;   // 동일 값 반복 보고 생략
                _lastPct = pct; _lastStage = stage;
                job.Stage = stage;
                job.Percent = Math.Max(job.Percent, pct);
                if (text is not null) job.StatusText = text;
            }
            owner.Changed(job);
        }

        public Task CheckpointAsync(CancellationToken ct)
        {
            Task<bool> r;
            lock (owner._gate) r = owner._resumed.Task;
            return r.IsCompleted ? Task.CompletedTask : WaitAsync(r, ct);

            async Task WaitAsync(Task<bool> t, CancellationToken c)
            {
                lock (owner._gate) job.StatusText = "⏸ 일시정지(단계 경계)";
                owner.Changed(job);
                await t.WaitAsync(c).ConfigureAwait(false);
            }
        }
    }

    private bool HasQueued()
    {
        lock (_gate) { foreach (var j in _jobs) if (j.Stage == BatchStage.Queued) return true; }
        return false;
    }

    private BatchJob? NextQueuedLocked()
    {
        BatchJob? best = null;
        foreach (var j in _jobs)
            if (j.Stage == BatchStage.Queued && (best is null || QueueOrder(j, best) < 0)) best = j;
        return best;
    }

    private BatchJob? FindLocked(string id)
    {
        foreach (var j in _jobs) if (j.Id == id) return j;
        return null;
    }

    private static int QueueOrder(BatchJob a, BatchJob b)
    {
        var p = ((int)b.Priority).CompareTo((int)a.Priority);
        return p != 0 ? p : a.Sequence.CompareTo(b.Sequence);
    }

    private static int Rank(BatchJob j) => j.IsActive ? 0 : j.Stage == BatchStage.Queued ? 1 : j.Stage == BatchStage.Failed ? 2 : 3;

    private static int DisplayOrder(BatchJob a, BatchJob b)
    {
        var r = Rank(a).CompareTo(Rank(b));
        return r != 0 ? r : Rank(a) == 1 ? QueueOrder(a, b) : a.Sequence.CompareTo(b.Sequence);
    }

    private void Changed(BatchJob job)
    {
        Action<BatchJobSnapshot>[] subs;
        lock (_gate) subs = _jobSubs;
        if (subs.Length > 0)
        {
            var snap = job.Snapshot;
            foreach (var s in subs)
            {
                try { s(snap); } catch (Exception ex) { Interlocked.Increment(ref _subscriberErrors); Program.Log("BatchQueue.OnJobChanged", ex); }
            }
        }
        RaiseChange();
    }

    private void RaiseChange()
    {
        Action[] subs;
        lock (_gate) subs = _changeSubs;
        foreach (var s in subs)
        {
            try { s(); } catch (Exception ex) { Interlocked.Increment(ref _subscriberErrors); Program.Log("BatchQueue.OnChange", ex); }
        }
    }

    private static TaskCompletionSource<bool> NewResumed(bool done)
    {
        var t = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (done) t.TrySetResult(true);
        return t;
    }

    private static T[] Append<T>(T[] a, T v)
    {
        var n = new T[a.Length + 1];
        Array.Copy(a, n, a.Length);
        n[^1] = v;
        return n;
    }

    private static T[] Remove<T>(T[] a, T v) where T : class
    {
        var i = Array.IndexOf(a, v);
        if (i < 0) return a;
        var n = new T[a.Length - 1];
        Array.Copy(a, 0, n, 0, i);
        Array.Copy(a, i + 1, n, i, a.Length - i - 1);
        return n;
    }

    public void Dispose()
    {
        if (_cost is not null)
        {
            _cost.OnBudgetExceeded -= OnBudgetExceeded;
            _cost.OnBudgetRecovered -= OnBudgetRecovered;
        }
        lock (_gate)
        {
            try { _workerCts?.Cancel(); } catch (ObjectDisposedException) { }
            foreach (var j in _jobs) { try { j.Cts?.Cancel(); } catch (ObjectDisposedException) { } }
        }
        _resumed.TrySetResult(true);
        _workerCts?.Dispose();
        _wake.Dispose();
    }
}

/// <summary>
/// [PromptFlowStudio] v16.16.0 기본 파이프라인 러너 — <see cref="StoryboardJobManager"/>(제출·렌더) → <see cref="PostProductionEngine"/>(후처리) → <see cref="OsmuExportPackager"/>(선택).
///   ▸ 두 엔진은 싱글턴 단일 파이프라인이라 단계별 전용 게이트로 직렬화(렌더 중 다른 작업 후처리는 병행 가능).
///   ▸ 렌더는 거버너 쿼터 쿨다운을 먼저 대기 · 렌더 실패 컷 메시지에 429/쿼터가 있으면 <see cref="QueueResourceGovernor.ReportThrottle"/> 로 공유 백오프.
///   ▸ 후처리/OSMU 실패는 렌더 결과(컷 병합본)를 살려 [부분 구제] 성공으로 보고.
/// </summary>
public sealed class StoryboardBatchJobRunner(StoryboardJobManager jobs, PostProductionEngine post, OsmuExportPackager? osmu = null) : IBatchJobRunner
{
    private static readonly SemaphoreSlim RenderGate = new(1, 1);
    private static readonly SemaphoreSlim PostGate = new(1, 1);

    /// <summary>후처리 후 OSMU 패키징(9:16·1:1·4:5) 수행 여부.</summary>
    public bool OsmuPackaging { get; set; }

    public async Task<BatchJobOutcome> RunAsync(BatchJob job, IBatchJobReporter reporter, QueueResourceGovernor governor, CancellationToken ct)
    {
        var project = job.Project;
        if (!jobs.Enabled) return new(false, null, "생성 엔진이 구성되지 않았습니다.");
        if (project.Scenes.Count == 0) return new(false, null, "컷이 없습니다.");

        reporter.Report(BatchStage.Submitting, 0, "📤 렌더 슬롯 대기");
        await RenderGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await governor.WaitIfCoolingAsync(ct).ConfigureAwait(false);
            await reporter.CheckpointAsync(ct).ConfigureAwait(false);
            while (jobs.IsRunning || jobs.IsMerging) await Task.Delay(500, ct).ConfigureAwait(false);   // 수동 생성과 겹침 방지
            reporter.Report(BatchStage.Submitting, 1, "📤 컷 제출");

            void Mirror()
            {
                if (jobs.Current != project) return;
                reporter.Report(BatchStage.Rendering, jobs.OverallPercent / 100.0,
                    jobs.IsMerging ? "🎞️ 컷 병합 중…" : "🎞️ 컷 렌더 중… " + jobs.OverallPercent.ToString(CultureInfo.InvariantCulture) + "%");
            }
            jobs.OnChange += Mirror;
            using (ct.Register(static s => ((StoryboardJobManager)s!).Cancel(), jobs))
            {
                try { await jobs.StartAsync(project, parallel: false).ConfigureAwait(false); }
                finally { jobs.OnChange -= Mirror; }
            }
            ct.ThrowIfCancellationRequested();
        }
        finally { RenderGate.Release(); }

        var done = 0;
        var throttled = false;
        foreach (var s in project.Scenes)
        {
            if (s.Status == SceneStatus.Completed) { done++; continue; }
            var e = s.Error;
            if (e is not null && (e.Contains("429", StringComparison.Ordinal) || e.Contains("쿼터", StringComparison.Ordinal) || e.Contains("rate", StringComparison.OrdinalIgnoreCase))) throttled = true;
        }
        if (throttled) governor.ReportThrottle(429);
        else if (done > 0) governor.ReportSuccess();
        if (done != project.Scenes.Count)
            return new(false, null, (project.Scenes.Count - done).ToString(CultureInfo.InvariantCulture) + "개 컷 미완성" + (throttled ? "(쿼터 제한 — 재시도 권장)" : ""));

        await reporter.CheckpointAsync(ct).ConfigureAwait(false);
        reporter.Report(BatchStage.PostProduction, 0, "🎚️ 후처리 대기");
        string? final = project.FinalVideoPath;
        await PostGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var r = await post.ExportAsync(project, new Progress<PostProductionProgress>(p => reporter.Report(BatchStage.PostProduction, p.Percent / 100.0, "🎚️ " + p.Stage)),
                PostTransition.Crossfade, ct).ConfigureAwait(false);
            if (r.Ok) final = r.OutputPath;
            else if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            else if (final is null) return new(false, null, "후처리 실패: " + r.Message);
        }
        finally { PostGate.Release(); }

        if (OsmuPackaging && osmu is { IsAvailable: true } && final is not null)
        {
            await reporter.CheckpointAsync(ct).ConfigureAwait(false);
            reporter.Report(BatchStage.OsmuPackaging, 0, "🌐 OSMU 패키징");
            var req = new OsmuExportRequest(final, new[] { OsmuTarget.YouTubeShorts, OsmuTarget.InstagramFeed, OsmuTarget.Pinterest }, Zip: true);
            var o = await osmu.ExportAsync(project, req, new Progress<PostProductionProgress>(p => reporter.Report(BatchStage.OsmuPackaging, p.Percent / 100.0)), ct).ConfigureAwait(false);
            if (o.Ok) return new(true, o.OutputPath, "OSMU " + o.Message);
            return new(true, final, "최종 영상 완료(OSMU 실패: " + o.Message + ")");
        }
        return new(true, final, "최종 영상 완료");
    }
}
