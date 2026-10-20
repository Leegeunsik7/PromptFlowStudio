using System.Globalization;

namespace PromptFlowStudio.Services.World;

/// <summary>1-Click 4D 생성 결과.</summary>
public sealed record WorldGenerationResult(SceneGraph Graph, OptimizedPrompt Prompt, CompiledWorld World, WorldConsistencyReport Report, string StoryDsl, TranspileResult Story, double Millis);

/// <summary>
/// [PromptFlow Studio v16.54.0] ▶ 생성형 4D 월드 모델 스튜디오 러너.
///   ▸ Generate: 프롬프트 → 장면 그래프(보정) → 정제 프롬프트 → 4D 월드 컴파일 → 시공간 일관성 분석 → 기본 분기 스토리 트랜스파일/로드.
///   ▸ 재생 루프: 백그라운드 PeriodicTimer(월드 FPS) → 분기 플레이어 Tick(무할당 · 핫패스 가드 프로브) → 프런트 버퍼 교체.
///   ▸ 라이브 물리 슬라이더(중력/반발/마찰/반사/배속) → 월드 개입 연산(재생 중 안전 잠금).
///   ▸ 외부 생성형 비디오 백엔드 결선점 <see cref="VideoBackend"/>(4D 컨디셔닝 JSON) — 미결선 시 내장 레이트레이서 프리뷰 폴백.
/// </summary>
public sealed class WorldModelStudioRunner : IDisposable
{
    public const int FrameWidth = 192, FrameHeight = 108;
    private readonly AiGenerative4dWorldModelEngine _world;
    private readonly InteractiveVideoBranchingTranspiler _branch;
    private readonly NeuralSceneGraphPromptOptimizer _opt;
    private readonly object _gate = new();
    private readonly byte[] _back = new byte[FrameWidth * FrameHeight * 3], _front = new byte[FrameWidth * FrameHeight * 3];
    private CancellationTokenSource? _cts;
    private long _frameSeq;
    private int _guardSite = -1;

    public WorldModelStudioRunner(IServiceProvider sp)
        : this((AiGenerative4dWorldModelEngine)sp.GetService(typeof(AiGenerative4dWorldModelEngine))!, (InteractiveVideoBranchingTranspiler)sp.GetService(typeof(InteractiveVideoBranchingTranspiler))!,
               (NeuralSceneGraphPromptOptimizer)sp.GetService(typeof(NeuralSceneGraphPromptOptimizer))!)
    {
        Guard = sp.GetService(typeof(PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard)) as PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard;
        _guardSite = Guard?.Register("World4d.BranchTick", 0) ?? -1;
    }

    public WorldModelStudioRunner(AiGenerative4dWorldModelEngine world, InteractiveVideoBranchingTranspiler branch, NeuralSceneGraphPromptOptimizer opt)
    { _world = world; _branch = branch; _opt = opt; }

    public PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard? Guard { get; set; }

    /// <summary>외부 생성형 비디오 모델 결선점(4D 컨디셔닝 JSON → 결과 URI). null = 미결선.</summary>
    public Func<string, CancellationToken, Task<string?>>? VideoBackend { get; set; }

    public event Action? OnChange;

    public WorldGenerationResult? Last { get; private set; }
    public bool IsPlaying => _cts is not null;
    public long FrameSeq => Interlocked.Read(ref _frameSeq);
    public string? LastAction { get; private set; }

    /// <summary>1-Click 4D 월드 생성(재생 중이면 정지 후 교체).</summary>
    public WorldGenerationResult Generate(string prompt, int fps = 30)
    {
        StopPlayback();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate)
        {
            var (graph, op) = _opt.AnalyzeAndOptimize(prompt);
            _world.Fps = fps;
            var compiled = _opt.Compile(graph, _world);
            var report = _world.Analyze(Math.Min(graph.Duration, 6f));
            var dsl = InteractiveVideoBranchingTranspiler.BuildDefaultStory(_world);
            var story = _branch.Transpile(dsl, _world);
            if (story.Program is not null) { _branch.ResetStats(); _branch.Load(story.Program, _world); }
            _world.Render(_back, FrameWidth, FrameHeight);
            Array.Copy(_back, _front, _back.Length);
            Interlocked.Increment(ref _frameSeq);
            Last = new WorldGenerationResult(graph, op, compiled, report, dsl, story, sw.Elapsed.TotalMilliseconds);
            LastAction = "🌐 4D 월드 생성 · 일관성 " + report.Score.ToString(CultureInfo.InvariantCulture) + "점 · " + sw.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + "ms";
        }
        Raise();
        return Last;
    }

    /// <summary>사용자 편집 DSL 재트랜스파일 → 성공 시 현재 월드 상태에서 로드.</summary>
    public TranspileResult LoadStory(string dsl)
    {
        StopPlayback();
        TranspileResult r;
        lock (_gate)
        {
            r = _branch.Transpile(dsl, _world);
            if (r.Program is not null) { _branch.ResetStats(); _branch.Load(r.Program, _world); }
        }
        Raise();
        return r;
    }

    public void StartPlayback()
    {
        if (_cts is not null || _branch.Program is null) return;
        if (_branch.Ended) { var p = _branch.Program; lock (_gate) _branch.Load(p, _world); }
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
        LastAction = "▶ 인터랙티브 재생";
        Raise();
    }

    public void StopPlayback()
    {
        var c = Interlocked.Exchange(ref _cts, null);
        if (c is null) return;
        c.Cancel(); c.Dispose();
        LastAction = "⏸ 재생 일시정지";
        Raise();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Math.Max(12, _world.Fps)));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                bool alive;
                var probe = _guardSite >= 0 ? Guard?.Begin(_guardSite) : null;
                alive = TickOnce();
                if (probe is { } pr) Guard!.End(in pr);
                if ((Interlocked.Read(ref _frameSeq) & 7) == 0) Raise();
                if (!alive) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { PromptFlowStudio.Program.Log("World4d.Loop", ex); }
        if (!ct.IsCancellationRequested) { Interlocked.Exchange(ref _cts, null)?.Dispose(); LastAction = "🏁 엔딩 도달"; Raise(); }
    }

    /// <summary>1프레임 진행(무할당) — 테스트/수동 스텝 공용.</summary>
    public bool TickOnce()
    {
        bool alive;
        lock (_gate)
        {
            alive = _branch.Tick(_back, FrameWidth, FrameHeight);
            _back.AsSpan().CopyTo(_front);
        }
        Interlocked.Increment(ref _frameSeq);
        return alive;
    }

    public void CopyFrame(Span<byte> dst) { lock (_gate) _front.AsSpan().CopyTo(dst); }

    public void Choose(int choice) => _branch.Choose(choice);

    public bool Rewind() => _branch.Rewind(Math.Min(2, Math.Max(1, _branch.HistoryCount)));

    public bool Preview(int choice, Span<byte> rgb, int w, int h) => _branch.PreviewChoice(choice, 45, rgb, w, h);

    public void SetPhysics(WorldOpKind kind, float value)
    {
        lock (_gate)
        {
            _world.Apply(kind == WorldOpKind.Gravity ? new WorldOp(kind, -1, 0, -value, 0) : new WorldOp(kind, -1, value, 0, 0));
            if (_cts is null) { _world.Render(_back, FrameWidth, FrameHeight); _back.AsSpan().CopyTo(_front); }
        }
        LastAction = "🎚 물리 " + kind + " = " + value.ToString("0.00", CultureInfo.InvariantCulture);
        Raise();
    }

    public void SetLighting(LightPreset p)
    {
        lock (_gate)
        {
            _world.SetLighting(p);
            if (_cts is null) { _world.Render(_back, FrameWidth, FrameHeight); _back.AsSpan().CopyTo(_front); }
        }
        Raise();
    }

    public WorldConsistencyReport Reanalyze(float seconds)
    {
        lock (_gate) return _world.Analyze(seconds);
    }

    /// <summary>4D 컨디셔닝 패키지 생성 → 외부 백엔드 제출(미결선 시 내장 프리뷰 폴백 안내).</summary>
    public async Task<(string Json, string Status)> ExportConditioningAsync(CancellationToken ct = default)
    {
        string json;
        lock (_gate)
        {
            var op = Last?.Prompt;
            json = _world.BuildConditioningJson(op?.Refined ?? "", op?.Negative ?? "", Last?.Graph.Duration ?? 5f);
        }
        var backend = VideoBackend;
        if (backend is null) return (json, "외부 생성형 비디오 백엔드 미결선 — 내장 레이트레이서 프리뷰 사용");
        try
        {
            using var to = CancellationTokenSource.CreateLinkedTokenSource(ct);
            to.CancelAfter(TimeSpan.FromSeconds(30));
            var uri = await backend(json, to.Token).ConfigureAwait(false);
            return (json, uri is null ? "백엔드 응답 없음 — 내장 프리뷰 폴백" : "제출 완료 · " + uri);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            return (json, "백엔드 실패(" + ex.GetType().Name + ") — 내장 프리뷰 폴백");
        }
    }

    private void Raise()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { PromptFlowStudio.Program.Log("World4d.Runner.OnChange", ex); }
    }

    public void Dispose() => StopPlayback();
}
