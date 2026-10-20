namespace PromptFlowStudio.Services.SplatEnvironment;

/// <summary>
/// 상주 워커 풀(프레임당 무할당) — 첫 <see cref="Run"/> 에서 백그라운드 스레드 N 개를 띄우고, <see cref="Run"/> 마다 세마포어로 깨워
/// 원자 카운터 청크 분배로 [0, count) 를 나눠 실행한다(호출 스레드도 워커 N 번으로 참여). 워커 예외는 격리 · 집계하고 항상 완료 신호를 보낸다.
/// </summary>
internal sealed class SplatWorkerPool : IDisposable
{
    private readonly Thread[] _threads;
    private readonly SemaphoreSlim _go = new(0);
    private readonly CountdownEvent _done = new(1);
    private readonly Action<int, int, int, int> _body;
    private int _job, _count, _chunk, _next;
    private volatile bool _stop;
    private bool _started;

    public SplatWorkerPool(int workers, Action<int, int, int, int> body, string name)
    {
        _body = body;
        _threads = new Thread[Math.Max(workers, 0)];
        for (var i = 0; i < _threads.Length; i++)
        {
            var w = i;
            _threads[i] = new Thread(() => Loop(w)) { IsBackground = true, Name = name + "." + w.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        }
    }

    /// <summary>워커 수(호출 스레드 제외) — 스크래치 슬롯은 Workers + 1 개 필요.</summary>
    public int Workers => _threads.Length;
    public long Faults { get; private set; }

    /// <summary>job 을 [0, count) 청크 단위로 병렬 실행(완료까지 대기 · 무할당).</summary>
    public void Run(int job, int count, int chunk)
    {
        if (count <= 0) return;
        _job = job; _count = count; _chunk = Math.Max(chunk, 1); Volatile.Write(ref _next, 0);
        if (_threads.Length == 0 || count <= _chunk) { Work(_threads.Length); return; }
        if (!_started) { _started = true; foreach (var t in _threads) t.Start(); }
        _done.Reset(_threads.Length + 1);
        _go.Release(_threads.Length);
        Work(_threads.Length);
        _done.Signal();
        _done.Wait();
    }

    private void Work(int w)
    {
        while (true)
        {
            var s = Interlocked.Add(ref _next, _chunk) - _chunk;
            if (s >= _count) return;
            try { _body(_job, w, s, Math.Min(s + _chunk, _count)); }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidOperationException) { Faults++; PromptFlowStudio.Program.Log("SplatEnv.Worker", ex); }
        }
    }

    private void Loop(int w)
    {
        while (true)
        {
            _go.Wait();
            if (_stop) { _done.Signal(); return; }
            Work(w);
            _done.Signal();
        }
    }

    public void Dispose()
    {
        if (_stop) return;
        _stop = true;
        if (!_started) { _go.Dispose(); _done.Dispose(); return; }
        _done.Reset(_threads.Length);
        _go.Release(_threads.Length);
        _done.Wait(2000);
        _go.Dispose(); _done.Dispose();
    }
}
