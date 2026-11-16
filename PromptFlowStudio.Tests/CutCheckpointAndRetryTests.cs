using System.Net;
using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Batch;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

public sealed class CutCheckpointAndRetryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_cutckpt_" + Guid.NewGuid().ToString("N"));

    public CutCheckpointAndRetryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string WriteMp4(string name, int bytes = 2048)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, ExportMediaIntegrityGuard.BuildMinimalMp4(bytes));
        return p;
    }

    [Fact]
    public void CutCheckpointService_SavesAndReusesValidMp4_AndRejectsExpiredOrCorruptFiles()
    {
        var now = DateTimeOffset.UtcNow;
        var root = Path.Combine(_dir, "ckpt");
        var svc = new CutRenderCheckpointService(() => root) { Clock = () => now };

        // 결정적 · 정규화(CRLF/공백 · 해상도/프로바이더 대소문자) · 소문자 64 hex
        var key = svc.ComputeCutKey("  sunset beach\r\nwide  ", 1, 5, "1280*720", "Wan");
        Assert.Equal(key, svc.ComputeCutKey("sunset beach\nwide", 1, 5, " 1280*720 ", "wan"));
        Assert.NotEqual(key, svc.ComputeCutKey("sunset beach\nwide", 2, 5, "1280*720", "wan"));
        Assert.True(CutRenderCheckpointService.IsValidKey(key));
        Assert.False(svc.TryGetValidCheckpoint(key, out _));
        Assert.False(svc.TryGetValidCheckpoint("..\\evil", out _));

        // 저장 → 재사용(원자 이동 · 임시 파일 잔존 없음)
        svc.SaveCheckpoint(key, WriteMp4("cut1.mp4"));
        Assert.True(svc.TryGetValidCheckpoint(key, out var cached));
        Assert.Equal(Path.Combine(root, key + ".mp4"), cached);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));

        // 손상 원본은 저장 거부
        var bad = Path.Combine(_dir, "bad.mp4");
        File.WriteAllBytes(bad, new byte[4096]);
        var key2 = svc.ComputeCutKey("p2", 1, 5, "1280*720", "wan");
        Assert.ThrowsAny<Exception>(() => svc.SaveCheckpoint(key2, bad));
        Assert.False(svc.TryGetValidCheckpoint(key2, out _));

        // 손상 캐시 파일 → 거부 + 삭제
        File.WriteAllBytes(Path.Combine(root, key2 + ".mp4"), new byte[4096]);
        Assert.False(svc.TryGetValidCheckpoint(key2, out _));
        Assert.False(File.Exists(Path.Combine(root, key2 + ".mp4")));

        // 24시간 경과 → 만료 거부 + 삭제
        var later = new CutRenderCheckpointService(() => root) { Clock = () => now.AddHours(25) };
        Assert.False(later.TryGetValidCheckpoint(key, out _));
        Assert.False(File.Exists(cached));

        // 30개 상한 — 오래된 순 정리
        var src = WriteMp4("src.mp4");
        var tick = now;
        var pruning = new CutRenderCheckpointService(() => root) { Clock = () => tick };
        string? first = null;
        for (var i = 0; i < 32; i++)
        {
            tick = now.AddMinutes(i);
            var k = pruning.ComputeCutKey("prune " + i, 1, 5, "1280*720", "wan");
            first ??= k;
            pruning.SaveCheckpoint(k, src);
        }
        Assert.Equal(CutRenderCheckpointService.MaxEntries, Directory.GetFiles(root, "*.mp4").Length);
        Assert.False(File.Exists(Path.Combine(root, first + ".mp4")));
    }

    [Fact]
    public async Task CutRetryPolicy_RetriesTransientFailureAndSucceeds_WhileFailingFastOnAuthError()
    {
        var delays = new List<TimeSpan>();
        Task Delay(TimeSpan d, CancellationToken _) { delays.Add(d); return Task.CompletedTask; }

        // 일시 예외 2회(네트워크 · 타임아웃) → 3번째 성공, 백오프 1500 → 3500ms
        var calls = 0;
        var ok = await CutRetryPolicy.ExecuteAsync<string>((_, _) =>
        {
            calls++;
            if (calls == 1) throw new HttpRequestException("503 Service Unavailable", null, HttpStatusCode.ServiceUnavailable);
            if (calls == 2) throw new TaskCanceledException("HttpClient timeout");
            return Task.FromResult("done");
        }, null, CancellationToken.None, Delay);
        Assert.Equal("done", ok);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(3500) }, delays);

        // 일시 실패 결과(429) → 재시도 후 성공
        delays.Clear(); calls = 0;
        var r = await CutRetryPolicy.ExecuteAsync((_, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1
                ? new WanGenerationResult(false, WanTaskStatus.Failed, null, "HTTP 429 Throttling.RateQuota", null, TimeSpan.Zero)
                : new WanGenerationResult(true, WanTaskStatus.Succeeded, "x.mp4", "ok", null, TimeSpan.Zero));
        }, static x => !x.Ok && CutRetryPolicy.IsTransientMessage(x.Message), CancellationToken.None, Delay);
        Assert.True(r.Ok);
        Assert.Equal(2, calls);

        // 인증 오류(401 예외 · InvalidApiKey 결과) → 즉시 실패(재시도 0)
        delays.Clear(); calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => CutRetryPolicy.ExecuteAsync<string>((_, _) =>
        {
            calls++;
            throw new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);
        }, null, CancellationToken.None, Delay));
        Assert.Equal(1, calls);
        calls = 0;
        var auth = await CutRetryPolicy.ExecuteAsync((_, _) =>
        {
            calls++;
            return Task.FromResult(new WanGenerationResult(false, WanTaskStatus.Failed, null, "InvalidApiKey: 401 invalid key (503 upstream)", null, TimeSpan.Zero));
        }, static x => !x.Ok && CutRetryPolicy.IsTransientMessage(x.Message), CancellationToken.None, Delay);
        Assert.False(auth.Ok);
        Assert.Equal(1, calls);
        Assert.Empty(delays);

        // 재시도 소진(일시 장애 지속) → 총 3회 후 마지막 예외
        calls = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => CutRetryPolicy.ExecuteAsync<string>((_, _) =>
        {
            calls++;
            throw new HttpRequestException("502 Bad Gateway", null, HttpStatusCode.BadGateway);
        }, null, CancellationToken.None, Delay));
        Assert.Equal(1 + CutRetryPolicy.MaxRetries, calls);

        // 호출자 취소 → 재시도 없이 전파
        using var cts = new CancellationTokenSource();
        calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CutRetryPolicy.ExecuteAsync<string>((_, c) =>
        {
            calls++;
            cts.Cancel();
            throw new TaskCanceledException("canceled", null, c);
        }, null, cts.Token, Delay));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task MultiCutExecution_ReusesPriorCompletedCutsFromCheckpoint_AfterPartialFailure()
    {
        var ckpt = new CutRenderCheckpointService(() => Path.Combine(_dir, "ckpt"));
        var template = new BatchRenderJob
        {
            Prompt = "neon city drone flythrough",
            Seed = 42,
            Resolution = "1280*720",
            OutputPath = Path.Combine(_dir, "out", "city.mp4"),
            DurationMode = VideoDurationMode.Sec15_Triple,
        };
        var cuts = MultiDurationPlanner.Expand(template, null, "city");
        Assert.Equal(3, cuts.Length);
        var noDelay = new Func<TimeSpan, CancellationToken, Task>(static (_, _) => Task.CompletedTask);

        // 1차: 컷 1·2 성공(컷 2는 503 1회 후 재시도 성공) · 컷 3은 일시 장애 지속 → 재시도 소진 실패
        var first = new ScriptedEngine(_dir, (cut, attempt) => cut switch
        {
            2 when attempt == 1 => "503 Service Unavailable",
            3 => "504 Gateway Timeout",
            _ => null,
        });
        var engine1 = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(first), null, (_, _) => Task.CompletedTask, ckpt) { RetryDelay = noDelay, Ambient = null };
        var r1 = new RenderExecutionResult[3];
        for (var i = 0; i < 3; i++) r1[i] = await engine1.ExecuteAsync(cuts[i], new Progress<int>(), CancellationToken.None);
        Assert.True(r1[0].Ok);
        Assert.True(r1[1].Ok);
        Assert.False(r1[2].Ok);
        Assert.Equal(1, first.CallsFor(1));
        Assert.Equal(2, first.CallsFor(2));
        Assert.Equal(1 + CutRetryPolicy.MaxRetries, first.CallsFor(3));

        // 2차(재실행): 컷 1·2 체크포인트 재사용(프로바이더 호출 0) · 컷 3만 실제 생성
        foreach (var c in cuts) if (c.OutputPath is { } p && File.Exists(p)) File.Delete(p);
        var second = new ScriptedEngine(_dir, static (_, _) => null);
        var engine2 = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(second), null, (_, _) => Task.CompletedTask, ckpt) { RetryDelay = noDelay, Ambient = null };
        var r2 = new RenderExecutionResult[3];
        for (var i = 0; i < 3; i++) r2[i] = await engine2.ExecuteAsync(cuts[i], new Progress<int>(), CancellationToken.None);
        Assert.All(r2, x => Assert.True(x.Ok));
        Assert.Equal(0, second.CallsFor(1));
        Assert.Equal(0, second.CallsFor(2));
        Assert.Equal(1, second.CallsFor(3));
        Assert.Equal(VideoEngineRenderExecutionEngine.CheckpointReuseMessage(1), r2[0].Message);
        Assert.Equal("♻️ 체크포인트 컷 재사용 (컷 #2)", r2[1].Message);
        Assert.Equal(r2[1].Message, MultiDurationPlanner.MetaOf(cuts[1], VideoEngineRenderExecutionEngine.MetaCheckpointNotice));
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(cuts[i].OutputPath, r2[i].OutputPath);
            Assert.True(ExportMediaIntegrityGuard.TryValidateMp4(r2[i].OutputPath, out _));
        }

        // 레거시 생성자(체크포인트 미주입) → 무동작 기본값: 재사용 없이 프로바이더 호출
        var legacy = new ScriptedEngine(_dir, static (_, _) => null);
        var engine3 = new VideoEngineRenderExecutionEngine(FakeVideoEngine.Selector(legacy), null, (_, _) => Task.CompletedTask) { Ambient = null };
        Assert.True((await engine3.ExecuteAsync(cuts[0], new Progress<int>(), CancellationToken.None)).Ok);
        Assert.Equal(1, legacy.CallsFor(1));
    }

    /// <summary>파트 프롬프트로 컷 번호를 식별해 (컷, 시도) 별 스크립트 실패를 반환하는 엔진.</summary>
    private sealed class ScriptedEngine(string dir, Func<int, int, string?> failure) : IVideoGenerationEngine
    {
        private readonly Dictionary<string, int> _cutOf = new(StringComparer.Ordinal);
        private readonly Dictionary<int, int> _calls = new();
        public string Key => "Fake";
        public string DisplayName => "Fake";
        public bool Enabled => true;

        public int CallsFor(int cut) => _calls.TryGetValue(cut, out var n) ? n : 0;

        public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
        {
            var cut = CutOf(request.Prompt);
            var attempt = CallsFor(cut) + 1;
            _calls[cut] = attempt;
            var fail = failure(cut, attempt);
            if (fail is not null) return Task.FromResult(new WanGenerationResult(false, WanTaskStatus.Failed, null, fail, null, TimeSpan.Zero));
            var path = Path.Combine(dir, "gen_" + Guid.NewGuid().ToString("N") + ".mp4");
            File.WriteAllBytes(path, ExportMediaIntegrityGuard.BuildMinimalMp4(2048 + cut));
            return Task.FromResult(new WanGenerationResult(true, WanTaskStatus.Succeeded, path, "ok", null, TimeSpan.Zero));
        }

        private int CutOf(string? prompt)
        {
            var p = prompt ?? "";
            if (_cutOf.TryGetValue(p, out var c)) return c;
            // MultiDurationPlanner 파트 프롬프트 = 메인 + 파트별 카메라 모션 → 파트 순서 = 첫 등장 순서
            c = MultiDurationPlanner.ResolvePartPrompt("neon city drone flythrough", null, 0, 3) == p ? 1
              : MultiDurationPlanner.ResolvePartPrompt("neon city drone flythrough", null, 1, 3) == p ? 2 : 3;
            _cutOf[p] = c;
            return c;
        }

        public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default)
            => Task.FromResult(new WanUploadResult(false, null, "n/a"));
    }
}
