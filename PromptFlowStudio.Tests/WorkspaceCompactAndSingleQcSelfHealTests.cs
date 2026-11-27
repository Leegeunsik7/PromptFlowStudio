using PromptFlowStudio.Services.AI;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio.Tests;

/// <summary>🩺 5초 단일 생성 렌더 후 QC · 1회 자가 치유 · 컴팩트 워크스페이스 마커 · 자율 발견 UX 갭 수정 · Pluto 9/6/2.</summary>
[Trait("Category", "FastUnit")]
public class WorkspaceCompactAndSingleQcSelfHealTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_ws_qc5_" + Guid.NewGuid().ToString("N"));

    public WorkspaceCompactAndSingleQcSelfHealTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private const string Prompt = "a toddler runs across the park chasing soap bubbles";

    private static readonly RenderQualityReport Frozen = new(true, RenderQualityDefect.Frozen, 5, "정지 화면");
    private static readonly RenderQualityReport Clean = new(true, RenderQualityDefect.None, 5, "정상");

    private (WanJobState State, FakeWan Wan, FakeMedia Media) Make()
    {
        var wan = new FakeWan();
        var media = new FakeMedia(_dir);
        return (new WanJobState(wan, media, new WanEngineTests.FakeWanConfig()), wan, media);
    }

    [Fact]
    public async Task FrozenFirstTake_IsHealedOnce_WithStabilizedRetry_AndLearned()
    {
        var (state, wan, _) = Make();
        var mem = new PlutoVideoLearningMemoryService(Path.Combine(_dir, PlutoVideoLearningMemoryService.FileName));
        var probes = 0;
        state.LearningMemory = mem;
        state.QualityProbe = (_, _) => Task.FromResult(++probes == 1 ? Frozen : Clean);
        var phasesAtQc = new List<WanJobPhase>();
        state.OnChange += () => { if (state.StatusText.StartsWith("🩺 렌더 품질", StringComparison.Ordinal)) phasesAtQc.Add(state.Phase); };

        await state.StartAsync(new WanVideoRequest { Prompt = Prompt, NegativePrompt = "blurry", Seed = 7 });

        Assert.Equal(2, wan.Requests.Count);
        Assert.Equal(2, probes);
        Assert.Equal(WanJobPhase.Completed, state.Phase);
        Assert.True(state.QcSelfHealed);
        Assert.False(state.QcDegraded);
        Assert.True(state.IsLocal);
        Assert.StartsWith(WanJobState.QcHealedPrefix, state.StatusText, StringComparison.Ordinal);
        Assert.Equal(Prompt, state.LastPrompt);   // 학습/메타 기준은 원문

        var retry = wan.Requests[1];
        Assert.Contains(RenderQueueService.QcStabilizeAnchor, retry.Prompt, StringComparison.Ordinal);
        Assert.Contains("blurry", retry.NegativePrompt, StringComparison.Ordinal);
        Assert.Contains("frozen frame", retry.NegativePrompt, StringComparison.Ordinal);
        Assert.Equal(7, retry.Seed);   // 시드·이미지 등 나머지는 유지
        Assert.All(phasesAtQc, p => Assert.Equal(WanJobPhase.Downloading, p));   // QC 는 Completed 방송 전(IsBusy 유지)
        Assert.Equal(1, mem.LearnedRuleCount);   // 진단 회피 규칙 1건(첫 결함만 — 재시도는 정상)
    }

    [Fact]
    public async Task StillDefectiveAfterRetry_CompletesDegraded_NoThirdAttempt()
    {
        var (state, wan, _) = Make();
        state.QualityProbe = (_, _) => Task.FromResult(Frozen);

        await state.StartAsync(new WanVideoRequest { Prompt = Prompt });

        Assert.Equal(2, wan.Requests.Count);   // 1회 상한
        Assert.Equal(WanJobPhase.Completed, state.Phase);
        Assert.True(state.QcSelfHealed);
        Assert.True(state.QcDegraded);
        Assert.StartsWith(WanJobState.QcDegradedPrefix, state.StatusText, StringComparison.Ordinal);
        Assert.NotNull(state.LocalPath);   // 최종 클립은 노출
    }

    [Fact]
    public async Task QcSkipped_ForMockMode_RemoteFallback_NoProbe_AndProbeFailure()
    {
        var (mock, wanMock, _) = Make();
        var calls = 0;
        mock.QualityProbe = (_, _) => { calls++; return Task.FromResult(Frozen); };
        mock.SkipQc = () => true;
        await mock.StartAsync(new WanVideoRequest { Prompt = Prompt });
        Assert.Equal(0, calls);
        Assert.Single(wanMock.Requests);
        Assert.False(mock.QcSelfHealed);

        var (remote, wanRemote, mediaRemote) = Make();
        mediaRemote.FailDownload = true;
        remote.QualityProbe = (_, _) => { calls++; return Task.FromResult(Frozen); };
        await remote.StartAsync(new WanVideoRequest { Prompt = Prompt });
        Assert.Equal(0, calls);   // 원격 URL 폴백은 QC 생략
        Assert.False(remote.IsLocal);
        Assert.Single(wanRemote.Requests);

        var (none, wanNone, _) = Make();   // 프로브·ffmpeg 분석기 미설정 → 기존 동작 그대로
        await none.StartAsync(new WanVideoRequest { Prompt = Prompt });
        Assert.Single(wanNone.Requests);
        Assert.StartsWith("완성! 로컬 저장됨", none.StatusText, StringComparison.Ordinal);

        var (boom, wanBoom, _) = Make();
        boom.QualityProbe = (_, _) => throw new InvalidOperationException("ffmpeg crashed");
        await boom.StartAsync(new WanVideoRequest { Prompt = Prompt });
        Assert.Equal(WanJobPhase.Completed, boom.Phase);   // QC 자체 실패는 격리
        Assert.Single(wanBoom.Requests);

        var (unchecked_, wanUnchecked, _) = Make();
        unchecked_.QualityProbe = (_, _) => Task.FromResult(RenderQualityReport.Skipped("ffmpeg 부재 — QC 생략"));
        await unchecked_.StartAsync(new WanVideoRequest { Prompt = Prompt });
        Assert.Single(wanUnchecked.Requests);
        Assert.False(unchecked_.QcSelfHealed);
    }

    [Fact]
    public async Task CancelDuringQc_EndsCanceled_WithoutRetry()
    {
        var (state, wan, _) = Make();
        state.QualityProbe = async (_, ct) => { state.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Frozen; };
        await state.StartAsync(new WanVideoRequest { Prompt = Prompt });
        Assert.Equal(WanJobPhase.Canceled, state.Phase);
        Assert.Single(wan.Requests);
    }

    [Fact]
    public void RetryRequest_KeepsStabilizeAnchor_WithinServerLimit_AndIsIdempotent()
    {
        var longPrompt = new string('a', 790);
        var r = WanJobState.BuildQcRetryRequest(new WanVideoRequest { Prompt = longPrompt, ImagePath = "x.png" });
        Assert.True(r.Prompt.Length <= WanJobState.MaxPromptChars);
        Assert.EndsWith(RenderQueueService.QcStabilizeAnchor, r.Prompt, StringComparison.Ordinal);
        Assert.Equal("x.png", r.ImagePath);

        var twice = WanJobState.BuildQcRetryRequest(WanJobState.BuildQcRetryRequest(new WanVideoRequest { Prompt = Prompt }));
        Assert.Equal(1, CountOf(twice.Prompt, RenderQueueService.QcStabilizeAnchor));
        Assert.Equal(1, CountOf(twice.NegativePrompt!, "frozen frame"));
    }

    // ── 워크스페이스 마커(컴팩트 레이아웃 · 결선 · 자율 발견 수정) ──

    [Fact]
    public void Workspace_CompactLayout_And5sQcWiring_Markers()
    {
        var ws = WorkspaceSource();
        Assert.Contains("height: min(20vh, 172px)", ws, StringComparison.Ordinal);                 // 유휴 16:9 뷰파인더 축소
        Assert.Contains(".ws-prog { display: flex; flex-direction: column; gap: 8px; min-height: 88px;", ws, StringComparison.Ordinal);
        Assert.DoesNotContain("min-height: 160px", ws, StringComparison.Ordinal);
        Assert.Contains("max-height: min(42vh, 400px)", ws, StringComparison.Ordinal);            // 결과 영상 16:9 상한
        Assert.Contains("grid-template-columns: 104px minmax(0, 1fr) 40px", ws, StringComparison.Ordinal);   // 튜너 1행
        Assert.Contains("⚙️ Wan 2.1/2.6 파라미터 튜너", ws, StringComparison.Ordinal);
        Assert.Contains(".ws-actions { display: flex; flex-wrap: wrap;", ws, StringComparison.Ordinal);

        Assert.Contains("State.LearningMemory ??= PlutoLearn;", ws, StringComparison.Ordinal);
        Assert.Contains("State.Stitcher ??=", ws, StringComparison.Ordinal);
        Assert.Contains("State.SkipQc = () => qcApp.IsMockMode;", ws, StringComparison.Ordinal);   // this 캡처 없음
        Assert.Contains("data-qc-5s=", ws, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_DiscoveredGapFixes_Markers()
    {
        var ws = WorkspaceSource();
        Assert.Contains("Program.Log(\"Workspace.Preflight\", ex);", ws, StringComparison.Ordinal);            // 보정 예외 → 로컬 폴백
        Assert.Contains("_magicSource = _magic is null ? null : source;", ws, StringComparison.Ordinal);      // 낡은 확장본 폐기
        Assert.Contains("if (!string.Equals(_magicSource, _prompt, StringComparison.Ordinal)) { _magic = null; return; }", ws, StringComparison.Ordinal);
        Assert.Contains("if (State.IsBusy || _disposed) return;   // 보정 대기 중", ws, StringComparison.Ordinal);
    }

    [Fact]
    public void PlutoInvariants_Unchanged()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoConflictMergeService.MergeableCompanions.Count);
    }

    // ── fakes ──

    private sealed class FakeWan : IWanApiService
    {
        public readonly List<WanVideoRequest> Requests = new();
        public bool Enabled => true;
        public Task<WanSubmitResult> SubmitAsync(WanVideoRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WanTaskResult> QueryAsync(string taskId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(bool Ok, string Message)> ValidateKeyAsync(string apiKey, string baseUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WanUploadResult> UploadImageAsync(string localImagePath, string? model = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WanGenerationResult> GenerateAsync(WanVideoRequest request, IProgress<WanProgress>? progress = null, CancellationToken ct = default)
        {
            Requests.Add(request);
            var id = "task" + Requests.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(new WanGenerationResult(true, WanTaskStatus.Succeeded, "https://example.invalid/" + id + ".mp4", "ok", id, TimeSpan.FromSeconds(1)));
        }
    }

    private sealed class FakeMedia(string root) : ILocalMediaService
    {
        public bool FailDownload;
        public string MediaRoot => root;
        public string VirtualHost => "media.local";
        public Task<LocalMediaResult> DownloadAsync(string jobId, string remoteUrl, IProgress<double>? progress = null, CancellationToken ct = default)
        {
            if (FailDownload) return Task.FromResult(new LocalMediaResult(false, null, remoteUrl, remoteUrl, 0, "fail"));
            var p = Path.Combine(root, jobId + ".mp4");
            File.WriteAllBytes(p, new byte[] { 1, 2, 3 });
            return Task.FromResult(new LocalMediaResult(true, p, "https://media.local/" + jobId + ".mp4", remoteUrl, 3, "ok"));
        }
        public string ToPlaybackUrl(string localPath) => "https://media.local/" + Path.GetFileName(localPath);
        public string ToFileUrl(string localPath) => "file:///" + localPath;
    }

    private static string WorkspaceSource() => File.ReadAllText(Path.Combine(RepoRoot(), "PromptFlowStudio", "Blazor", "Pages", "Workspace.razor"));

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Launcher.csproj"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
