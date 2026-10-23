using System.Diagnostics;
using System.Threading.Channels;

namespace PromptFlowStudio.Services.MasterPipeline;

/// <summary>v17.92.0 배치 작업(단일 라이브 세그먼트 입력).</summary>
public readonly record struct BatchRenderJob(long Id, int SegmentSeconds);

/// <summary>
/// v17.92.0 무인 추출 미디어 패키지 — 9:16 쇼츠 MP4 + 1:1 카드뉴스 PNG 5 + 마크다운 아티클.
/// 스테이징 버퍼는 실행 직후 회수되므로 패키지는 작업당 1 회 독립 복사본을 보유한다(비핫패스).
/// </summary>
public sealed class BatchMediaPackage
{
    public long JobId { get; init; }
    public long RunId { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public double ElapsedMs { get; init; }
    public byte[] ShortsMp4 { get; init; } = Array.Empty<byte>();
    public byte[][] Cards { get; init; } = Array.Empty<byte[]>();
    public string Markdown { get; init; } = "";
    public float Score { get; init; }
    public float RiskAfter { get; init; }
    public int VaultObjects { get; init; }
    public long VaultBytes { get; init; }
    public long ReclaimedBytes { get; init; }

    public long TotalBytes
    {
        get
        {
            long n = ShortsMp4.Length + System.Text.Encoding.UTF8.GetByteCount(Markdown);
            foreach (var c in Cards) n += c.Length;
            return n;
        }
    }
}

/// <summary>
/// [PromptFlow Studio v17.92.0] 🏭 헤드리스 백그라운드 배치 렌더 드라이버.
///   ▸ <see cref="Channel{T}"/> Bounded(<see cref="Capacity"/> · 포화 시 거절) 작업 큐 + 전용 단일 스레드(BelowNormal · 백그라운드) — UI 스레드는 Enqueue 만(비차단).
///   ▸ 작업 1 건 = <see cref="RealRunnerE2EChainExecutionEngine.RunAllAsync"/> ①→⑦ 무인 실행 → 패키지 복사 → 스테이징 즉시 회수.
///   ▸ 스레드는 첫 Enqueue 시 지연 기동, Dispose 시 취소 + 1.5 s 조인.
/// </summary>
public sealed class HeadlessBatchRenderDriver : IDisposable
{
    public const int Capacity = 8, History = 4;

    private readonly Channel<BatchRenderJob> _jobs = Channel.CreateBounded<BatchRenderJob>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false,
    });
    private readonly CancellationTokenSource _cts = new();
    private readonly BatchMediaPackage?[] _history = new BatchMediaPackage?[History];
    private readonly object _gate = new();
    private Thread? _thread;
    private long _nextId, _active = -1;
    private int _head, _count, _disposed;

    public HeadlessBatchRenderDriver(RealRunnerE2EChainExecutionEngine engine) => Engine = engine ?? throw new ArgumentNullException(nameof(engine));

    public event Action<BatchMediaPackage>? OnPackage;
    public RealRunnerE2EChainExecutionEngine Engine { get; }
    public bool WorkerAlive => _thread is { IsAlive: true };
    public int WorkerThreadId { get; private set; }
    public long ActiveJob => Interlocked.Read(ref _active);
    public bool Processing => ActiveJob >= 0;
    public int Pending => _jobs.Reader.Count;
    public long Enqueued { get; private set; }
    public long Rejected { get; private set; }
    public long Completed { get; private set; }
    public long Failed { get; private set; }
    public int PackageCount { get { lock (_gate) return _count; } }

    /// <summary>최근 패키지(0 = 최신).</summary>
    public BatchMediaPackage? Package(int i)
    {
        lock (_gate) return i < 0 || i >= _count ? null : _history[(_head - 1 - i + History * 2) % History];
    }

    /// <summary>작업 투입(비차단) — 큐 포화/종료 시 −1.</summary>
    public long Enqueue(int segmentSeconds = RealRunnerE2EChainExecutionEngine.DefaultSegmentSeconds)
    {
        if (Volatile.Read(ref _disposed) != 0) return -1;
        var job = new BatchRenderJob(Interlocked.Increment(ref _nextId), segmentSeconds);
        if (!_jobs.Writer.TryWrite(job)) { Rejected++; return -1; }
        Enqueued++;
        EnsureWorker();
        return job.Id;
    }

    /// <summary>큐 소진 대기(테스트 · 종료 직전) — 시간 내 소진 여부.</summary>
    public async Task<bool> DrainAsync(int timeoutMs = 60_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (Pending == 0 && !Processing) return true;
            await Task.Delay(20).ConfigureAwait(false);
        }
        return false;
    }

    private void EnsureWorker()
    {
        lock (_gate)
        {
            if (_thread is not null) return;
            _thread = new Thread(Worker) { IsBackground = true, Name = "PFS.HeadlessBatchRender", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }
    }

    private void Worker()
    {
        WorkerThreadId = Environment.CurrentManagedThreadId;
        var ct = _cts.Token;
        try
        {
            while (_jobs.Reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
            {
                while (_jobs.Reader.TryRead(out var job))
                {
                    Interlocked.Exchange(ref _active, job.Id);
                    try { Process(job, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { Failed++; PromptFlowStudio.Program.Log("MasterPipeline.Batch", ex); }
                    finally { Interlocked.Exchange(ref _active, -1); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { PromptFlowStudio.Program.Log("MasterPipeline.BatchLoop", ex); }
    }

    private void Process(BatchRenderJob job, CancellationToken ct)
    {
        var t0 = Stopwatch.GetTimestamp();
        var state = Engine.RunAllAsync(job.SegmentSeconds, ct).GetAwaiter().GetResult();
        var ctx = Engine.Context;
        BatchMediaPackage pkg;
        if (state == RunnerChainState.Completed)
        {
            var cards = new byte[RealRunnerChainContext.Cards][];
            for (var i = 0; i < cards.Length; i++) cards[i] = ctx.CardSpan(i).ToArray();
            pkg = new BatchMediaPackage
            {
                JobId = job.Id, RunId = ctx.RunId, Ok = true, ShortsMp4 = ctx.NaluSpan.ToArray(), Cards = cards, Markdown = new string(ctx.TextSpan),
                Score = ctx.Score, RiskAfter = ctx.RiskAfter, VaultObjects = ctx.VaultObjects, VaultBytes = ctx.VaultBytes,
                ElapsedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds,
            };
            Engine.ReleaseStaging();
            Completed++;
        }
        else
        {
            pkg = new BatchMediaPackage
            {
                JobId = job.Id, RunId = ctx.RunId, Ok = false, Error = Engine.LastError, Score = ctx.Score, RiskAfter = ctx.RiskAfter,
                ReclaimedBytes = Engine.LastReclaimedBytes, ElapsedMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds,
            };
            Failed++;
        }
        lock (_gate)
        {
            _history[_head] = pkg;
            _head = (_head + 1) % History;
            if (_count < History) _count++;
        }
        try { OnPackage?.Invoke(pkg); }
        catch (Exception ex) { PromptFlowStudio.Program.Log("MasterPipeline.BatchPackage", ex); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _jobs.Writer.TryComplete();
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        var t = _thread;
        if (t is not null && t.ManagedThreadId != Environment.CurrentManagedThreadId) t.Join(1500);
        _cts.Dispose();
    }
}
