using System.Text.RegularExpressions;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>전사 IProcessRunner 수렴 정적 스캔 · 렌더 큐 결함 격리 · 우선순위 재정렬 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class CoreRobustnessTests
{
    /// <summary>프로세스 직접 생성이 허용되는 유일한 게이트웨이(ProcessRunner).</summary>
    private static readonly string[] AllowList = { Path.Combine("Services", "Engines", "ProcessRunner.cs") };

    /// <summary>직접 <c>new Process</c> 생성 탐지(셸 위임 URL/인스톨러 열기 <c>Process.Start(UseShellExecute)</c> 는 CLI 가 아니므로 범위 외).</summary>
    private static readonly Regex DirectProcess = new(@"new\s+(System\.Diagnostics\.)?Process\s*[\(\{]", RegexOptions.Compiled);

    private static string? FindAppRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cand = Path.Combine(dir.FullName, "PromptFlowStudio", "Services");
            if (Directory.Exists(cand)) return Path.Combine(dir.FullName, "PromptFlowStudio");
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void AllServices_UtilizeIProcessRunner_StrictScan()
    {
        var root = FindAppRoot();
        Assert.NotNull(root);
        var services = Path.Combine(root!, "Services");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root!, file);
            if (Array.IndexOf(AllowList, rel) >= 0) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("///", StringComparison.Ordinal) || t.StartsWith("*", StringComparison.Ordinal)) continue;
                if (DirectProcess.IsMatch(lines[i])) offenders.Add(rel + ":" + (i + 1));
            }
        }
        Assert.True(offenders.Count == 0, "직접 Process 생성 잔존: " + string.Join(", ", offenders));
    }

    private sealed class FlakyEngine(Func<BatchRenderJob, RenderExecutionResult> run) : IRenderExecutionEngine
    {
        public bool IsAvailable => true;
        public string EngineKind => RenderEngineKind.ComfyUi;
        public Task<RenderExecutionResult> ExecuteAsync(BatchRenderJob job, IProgress<int> progress, CancellationToken ct) => Task.FromResult(run(job));
    }

    [Fact]
    public async Task RenderQueueService_QuarantinesFailingJob_AndContinuesRemainingQueue()
    {
        var engine = new FlakyEngine(j => j.Prompt == "bad"
            ? throw new HttpRequestException("Too Many Requests", null, System.Net.HttpStatusCode.TooManyRequests)
            : new RenderExecutionResult(true, RenderEngineKind.ComfyUi, null, "ok"));
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false, engine);
        var jobs = new[]
        {
            new BatchRenderJob { Prompt = "a", Seed = 1 },
            new BatchRenderJob { Prompt = "bad", Seed = 2 },
            new BatchRenderJob { Prompt = "c", Seed = 3 },
        };
        Assert.Equal(3, q.Enqueue(jobs));

        await q.ProcessQueueAsync(CancellationToken.None);

        Assert.Equal(BatchJobStatus.Completed, jobs[0].Status);
        Assert.Equal(BatchJobStatus.Failed, jobs[1].Status);
        Assert.True(jobs[1].IsQuarantined);
        Assert.Equal("API Rate Limit 429", jobs[1].QuarantineReason);
        Assert.Equal(BatchJobStatus.Completed, jobs[2].Status);   // 격리 후 나머지 대기열 계속 처리
        Assert.False(jobs[2].IsQuarantined);

        Assert.True(q.RetryJob(jobs[1].JobId));                     // 재적재 시 격리 해제
        Assert.False(jobs[1].IsQuarantined);
        Assert.Null(jobs[1].QuarantineReason);
    }

    [Fact]
    public void RenderQueueService_ClassifiesQuarantineReasons()
    {
        Assert.Equal("Invalid Input", RenderQueueService.ClassifyQuarantineReason(new ArgumentException("bad prompt")));
        Assert.Equal("API Rate Limit 429", RenderQueueService.ClassifyQuarantineReason(new InvalidOperationException("HTTP 429 rate limit")));
        Assert.Equal("Timeout", RenderQueueService.ClassifyQuarantineReason(new TimeoutException("x")));
        Assert.Equal("Engine Error", RenderQueueService.ClassifyQuarantineReason(new InvalidOperationException("렌더 엔진 실행 실패")));
    }

    [Fact]
    public void RenderQueueService_ReordersPriority_MovesItemToTopCorrectly()
    {
        using var q = new RenderQueueService(TimeSpan.Zero, autoStart: false);
        var done = new BatchRenderJob { Prompt = "done" };
        var a = new BatchRenderJob { Prompt = "a" };
        var b = new BatchRenderJob { Prompt = "b" };
        var c = new BatchRenderJob { Prompt = "c" };
        q.Enqueue(new[] { done, a, b, c });
        done.Status = BatchJobStatus.Completed;   // 완료 항목은 재정렬 대상 아님

        Assert.True(q.MoveJobToTop(c.JobId));
        Assert.Equal(new[] { "done", "c", "a", "b" }, Prompts(q));
        Assert.False(q.MoveJobToTop(c.JobId));     // 이미 1순위

        Assert.True(q.MoveJobDown(c.JobId));
        Assert.Equal(new[] { "done", "a", "c", "b" }, Prompts(q));

        Assert.True(q.MoveJobUp(b.JobId));
        Assert.Equal(new[] { "done", "a", "b", "c" }, Prompts(q));

        Assert.False(q.MoveJobUp(a.JobId));        // 위에는 완료 항목뿐
        Assert.False(q.MoveJobDown(c.JobId));      // 최하단
        Assert.False(q.MoveJobToTop(done.JobId));  // 완료 항목 이동 불가
        Assert.False(q.MoveJobUp(Guid.NewGuid()));
    }

    private static string[] Prompts(RenderQueueService q)
    {
        var snap = q.Snapshot();
        var arr = new string[snap.Length];
        for (var i = 0; i < snap.Length; i++) arr[i] = snap[i].Prompt;
        return arr;
    }
}
