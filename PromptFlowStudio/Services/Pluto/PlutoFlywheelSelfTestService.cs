using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🧪 Step 30 — 플라이휠 자가진단 단계 1건 결과.</summary>
public sealed record PlutoFlywheelStageResult(int Index, string Name, bool Passed, bool Skipped, double ElapsedMs, string Detail)
{
    public string StatusLabel => Passed ? "PASS" : Skipped ? "SKIP" : "FAIL";
}

/// <summary>🧪 Step 30 — 플라이휠 자가진단 리포트(경로 미포함 · 공유 안전).</summary>
public sealed record PlutoFlywheelSelfTestReport(IReadOnlyList<PlutoFlywheelStageResult> Stages, double TotalMs, bool SandboxCleaned, bool LiveStateUntouched, DateTimeOffset StartedUtc)
{
    public int PassedCount { get { var n = 0; foreach (var s in Stages) if (s.Passed) n++; return n; } }
    public bool AllPassed => Stages.Count == PlutoFlywheelSelfTestService.StageCount && PassedCount == Stages.Count && SandboxCleaned;

    public string SummaryKorean
        => (AllPassed ? "🧪 플라이휠 자가진단 PASS " : "🧪 플라이휠 자가진단 FAIL ") + PassedCount.ToString(CultureInfo.InvariantCulture) + "/"
           + PlutoFlywheelSelfTestService.StageCount.ToString(CultureInfo.InvariantCulture) + " · " + TotalMs.ToString("0", CultureInfo.InvariantCulture) + "ms"
           + (SandboxCleaned ? " · 🧹 샌드박스 정리됨" : " · ⚠ 샌드박스 정리 실패")
           + (LiveStateUntouched ? " · 🔒 라이브 상태 무접촉" : " · ⚠ 실행 중 라이브 파일 변경 감지(다른 기능의 정상 기록일 수 있음)");
}

/// <summary>🧪 Step 30 — 샌드박스 1클릭 종단간 플라이휠 자가진단.</summary>
public interface IPlutoFlywheelSelfTestService
{
    /// <summary>실행 중(단일 비행 — 실행 중 재호출은 진행 중 작업을 공유).</summary>
    bool IsRunning { get; }
    /// <summary>마지막 리포트(없음 → null).</summary>
    PlutoFlywheelSelfTestReport? LastReport { get; }
    /// <summary>격리 임시 폴더에서 7단계 실행 → <c>finally</c> 에서 샌드박스 삭제. 단계 예외는 FAIL 로 격리 · 취소만 전파.</summary>
    Task<PlutoFlywheelSelfTestReport> RunAsync(CancellationToken ct = default);
}

/// <summary>
/// 🧪 <see cref="IPlutoFlywheelSelfTestService"/> — 결정론 · 네트워크 0 · 격리 임시 폴더 드라이런.
/// 7단계: 🧭 토픽 플래너 → 🏷️ 기획 출처 → 📦 업로드 패키지 · 연결성 → 📊 Studio 내보내기 정규화 · 연결 → 🎯 기획 채점 · 사후 분석 →
/// 🌱 유기적 복권 · 멀티 PC 장부 재생 → 🩺 진단 Markdown 스냅샷.
///   ▸ 🔒 모든 허브를 <b>명시 경로</b>로 생성(기본 경로 생성자 · <see cref="PlutoDriveStorageManager"/> 기본값 · <c>AppDataPaths</c> · Drive 미사용).
///   ▸ 🧹 샌드박스는 고유 접두 + 무작위 토큰 표식 파일로 소유를 증명할 때만 재귀 삭제(링크/정션 · 부모 밖 · 표식 불일치 → 삭제 거부).
///   ▸ 🔍 실행 전후 라이브 로컬 데이터 폴더의 메타데이터 지문(열기 없음)을 비교해 무접촉을 보고.
/// </summary>
public sealed class PlutoFlywheelSelfTestService : IPlutoFlywheelSelfTestService
{
    public const int StageCount = 9;
    public const string SandboxPrefix = "pfs_flywheel_selftest_";
    public const string SandboxMarkerFileName = ".pluto_selftest_sandbox";

    public static readonly string[] StageNames =
    {
        "🧭 토픽 플래너",
        "🏷️ 기획 출처",
        "📦 업로드 패키지 · 연결성",
        "📊 Studio 내보내기 정규화 · 연결",
        "🎯 기획 채점 · 사후 분석",
        "🌱 유기적 복권 · 멀티 PC 장부 재생",
        "🩺 진단 Markdown 스냅샷",
        "🌐 다국어 채널 분기 · 일/서 수집",
        "☁️ 기동 선조정 · 블랙박스 진단",
    };

    private const string Theme = "가을 숲 산책";
    private const string ClipA = "selftest-clip-a", ClipB = "selftest-clip-b";
    private const string YtA = "ytSELFTESTA", YtB = "ytSELFTESTB", YtStray = "ytSELFSTRAY";
    private const string EnHeader = "Video ID,Video title,Video publish time,Views,Average percentage viewed (%),Impressions,Impressions click-through rate (%),Stayed to watch (%),Likes";
    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _sandboxParent;
    private readonly IReadOnlyList<string> _liveRoots;
    private readonly object _sync = new();
    private Task<PlutoFlywheelSelfTestReport>? _inFlight;

    /// <param name="sandboxParent">샌드박스 부모 폴더(null → 시스템 임시 폴더).</param>
    /// <param name="liveRoots">무접촉 지문 대상 라이브 폴더(null → 로컬 앱 데이터 <c>PromptFlowStudio</c> · <c>Data/Pluto</c> · Drive 는 조회하지 않음).</param>
    public PlutoFlywheelSelfTestService(string? sandboxParent = null, IReadOnlyList<string>? liveRoots = null)
    {
        _sandboxParent = string.IsNullOrWhiteSpace(sandboxParent) ? Path.GetTempPath() : sandboxParent;
        _liveRoots = liveRoots ?? DefaultLiveRoots();
    }

    public bool IsRunning { get { lock (_sync) return _inFlight is { IsCompleted: false }; } }
    public PlutoFlywheelSelfTestReport? LastReport { get; private set; }
    /// <summary>마지막 실행의 샌드박스 경로(테스트 검증용 · 삭제 후에도 값 유지).</summary>
    public string? LastSandboxPath { get; private set; }

    public static IReadOnlyList<string> DefaultLiveRoots()
    {
        var baseDir = Path.Combine(global::PromptFlowStudio.Services.Platform.AppDataPaths.LocalAppData, "PromptFlowStudio");
        return new[] { baseDir, Path.Combine(baseDir, "Data", PlutoDriveStorageManager.SubFolder) };
    }

    public Task<PlutoFlywheelSelfTestReport> RunAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            if (_inFlight is { IsCompleted: false } running) return running;   // 🧷 단일 비행 — 연타 · 동시 클릭이 샌드박스를 겹쳐 만들지 않음
            _inFlight = Task.Run(() => RunCoreAsync(ct), ct);
            return _inFlight;
        }
    }

    private async Task<PlutoFlywheelSelfTestReport> RunCoreAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        var total = Stopwatch.StartNew();
        var before = Fingerprint(_liveRoots);
        var results = new List<PlutoFlywheelStageResult>(StageCount);
        var token = Guid.NewGuid().ToString("N");
        string? root = null;
        Context? ctx = null;
        var cleaned = false;
        try
        {
            root = CreateSandbox(_sandboxParent, token);
            LastSandboxPath = root;
            ctx = new Context(root);
            var failed = false;
            for (var i = 0; i < StageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (failed) { results.Add(new PlutoFlywheelStageResult(i + 1, StageNames[i], false, true, 0, "선행 단계 실패로 건너뜀")); continue; }
                var t = Stopwatch.GetTimestamp();
                try
                {
                    var detail = await RunStageAsync(i, ctx, ct).ConfigureAwait(false);
                    results.Add(new PlutoFlywheelStageResult(i + 1, StageNames[i], true, false, Stopwatch.GetElapsedTime(t).TotalMilliseconds, detail));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed = true;
                    results.Add(new PlutoFlywheelStageResult(i + 1, StageNames[i], false, false, Stopwatch.GetElapsedTime(t).TotalMilliseconds,
                        PlutoSystemDiagnosticService.RedactPaths(ex.Message)));
                }
            }
        }
        finally
        {
            ctx?.Dispose();
            cleaned = root is null || TryDeleteSandbox(_sandboxParent, root, token);
        }
        var report = new PlutoFlywheelSelfTestReport(results, total.Elapsed.TotalMilliseconds, cleaned, FingerprintEquals(before, Fingerprint(_liveRoots)), started);
        LastReport = report;
        return report;
    }

    private static Task<string> RunStageAsync(int index, Context c, CancellationToken ct) => index switch
    {
        0 => Task.FromResult(StagePlanner(c)),
        1 => StageProvenanceAsync(c, ct),
        2 => Task.FromResult(StagePublishPack(c)),
        3 => StageStudioExportAsync(c, ct),
        4 => StageGradingAsync(c, ct),
        5 => StageRehabilitationAsync(c, ct),
        6 => StageDiagnosticAsync(c, ct),
        7 => StageMultiLanguageAsync(c, ct),
        _ => StageStartupSyncAsync(c, ct),
    };

    // ── ① 🧭 토픽 플래너 ──

    private static string StagePlanner(Context c)
    {
        var briefs = PlutoAutonomousTopicPlannerService.Synthesize(PlannerInputs(null));
        Require(briefs.Count > 0, "기획안 0건");
        var top = briefs[0];
        Require(!string.IsNullOrWhiteSpace(top.HookStyle) && !string.IsNullOrWhiteSpace(top.ColorLookId) && !string.IsNullOrWhiteSpace(top.BgmMood), "1위 기획안 결정 축 누락");
        c.Brief = top with { TotalEpisodes = 1, ExpectedComposite = 50.0 };
        c.Pair = PlutoPairAttribution.PairKey(top.HookStyle, top.ColorLookId) ?? throw new InvalidOperationException("조합 키 생성 실패");
        return "기획 " + briefs.Count.ToString(CultureInfo.InvariantCulture) + "건 · 1위 " + top.HookStyle + " × " + top.ColorLookId;
    }

    // ── ② 🏷️ 기획 출처 ──

    private static async Task<string> StageProvenanceAsync(Context c, CancellationToken ct)
    {
        var brief = c.Brief!;
        var arm = PlutoAppliedBriefArm.From(brief, new DateTimeOffset(T0)) ?? throw new InvalidOperationException("기획 무장 실패");
        var at = new DateTimeOffset(T0.AddMinutes(10));
        var rec = PlutoBriefOutcomeAttributionService.StampProvenance(
            new PlutoRenderedClipRecord(ClipA, CatalogTitleA, "몽타주", brief.HookStyle, brief.ColorLookId, brief.BgmMood, new[] { "autumn" }, "", at, false),
            arm, at, 1, out _, out var decision);
        Require(decision == PlutoBriefStampDecision.Stamped && rec.HasAppliedBrief, "출처 찍기 실패(" + decision.ToString() + ")");
        await c.Catalog.RecordRenderedClipAsync(rec, ct).ConfigureAwait(false);
        using var reread = new PlutoRenderCatalogService(c.CatalogPath);   // 💾 디스크 왕복
        var back = await reread.FindByVideoIdOrTitleAsync(ClipA, ct).ConfigureAwait(false);
        Require(back is not null && string.Equals(back.AppliedBriefId, brief.BriefId, StringComparison.Ordinal), "출처가 카탈로그 디스크 왕복에서 유실");
        return "출처 " + brief.BriefId + " 영속 · 기대 합성 " + brief.ExpectedComposite.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private const string CatalogTitleA = "가을 숲 산책 브이로그";
    private const string CatalogTitleB = "가을 숲 산책 브이로그 두번째 이야기";

    // ── ③ 📦 업로드 패키지 · 연결성 ──

    private static string StagePublishPack(Context c)
    {
        var brief = c.Brief!;
        var svc = new PlutoYouTubePublishPackService(clock: static () => new DateTimeOffset(T0));
        var pack = svc.Generate(new PlutoPublishPackRequest(Theme, CatalogTitleA, 1, 1, brief.HookStyle, brief.StyleTags));
        Require(!string.IsNullOrWhiteSpace(pack.TitleA.Title) && !string.IsNullOrWhiteSpace(pack.TitleB.Title), "제목 후보 누락");
        var best = pack.TitleA.LinkabilityScore >= pack.TitleB.LinkabilityScore ? pack.TitleA : pack.TitleB;
        Require(best.AutoLinkable, "자동 연결 가능한 제목 없음(연결성 " + best.LinkabilityScore.ToString("0.00", CultureInfo.InvariantCulture) + ")");
        Require(pack.ToSidecarText("selftest.mp4", ClipA, new DateTimeOffset(T0)).Length > 0, "사이드카 본문 비어 있음");
        c.PublishedTitle = best.Title;
        return "제목 " + best.Variant + " 연결성 " + best.LinkabilityScore.ToString("0.00", CultureInfo.InvariantCulture) + " ≥ " + PlutoYouTubeCatalogLinker.MinFuzzyScore.ToString("0.00", CultureInfo.InvariantCulture);
    }

    // ── ④ 📊 Studio 내보내기 정규화 · 연결 ──

    private static async Task<string> StudioImportAsync(Context c, string csv, string fileName, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        var report = YouTubeStudioAnalyticsNormalizer.Parse(bytes, fileName);
        Require(report.HeaderRecognized && report.Rows.Count > 0, "Studio 표 헤더 인식 실패");
        var r = await c.Importer.ImportYouTubeStudioFileAsync(bytes, fileName, "YouTube Shorts", ct).ConfigureAwait(false);
        Require(!r.UnmatchedQueuePersistFailed, "미매칭 대기열 영속 실패");
        return r.SummaryKorean;
    }

    private static async Task<string> StageStudioExportAsync(Context c, CancellationToken ct)
    {
        // 업로드 제목(③) · 게시일 = 렌더 당일 · 조회 300 · 평균 조회율 15% · 3초 유지 30% · CTR 2.0% → 기대 50 대비 심각 미달 + 무관한 미매칭 1행
        var csv = EnHeader + "\n"
            + YtA + "," + Quote(c.PublishedTitle!) + ",\"Jan 5, 2026\",300,15,5000,2.0,30,10\n"
            + YtStray + ",\"전혀 다른 요리 영상 #shorts\",\"Jan 5, 2026\",120,40,900,3.0,50,2\n";
        var summary = await StudioImportAsync(c, csv, "Table data.csv", ct).ConfigureAwait(false);
        var clip = await c.Catalog.FindByYouTubeVideoIdAsync(YtA, ct).ConfigureAwait(false);
        Require(clip is not null && string.Equals(clip.VideoId, ClipA, StringComparison.Ordinal), "업로드 제목이 카탈로그 클립에 연결되지 않음");
        Require(clip!.YouTubeWatermark is { Views: >= PlutoBriefOutcomeAttributionService.MinViews }, "실측 워터마크 미반영");
        Require(c.Queue.Count == 1, "미매칭 행 대기열 보존 실패(" + c.Queue.Count.ToString(CultureInfo.InvariantCulture) + "건)");
        return summary;
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // ── ⑤ 🎯 기획 채점 · 사후 분석 ──

    private static async Task<string> StageGradingAsync(Context c, CancellationToken ct)
    {
        c.Clock.Now = T0.AddDays(1);
        var sweep = await c.Briefs.EvaluatePendingBriefOutcomesAndRehabilitationAsync(ct).ConfigureAwait(false);
        Require(sweep.EvaluatedCount == 1, "채점 " + sweep.EvaluatedCount.ToString(CultureInfo.InvariantCulture) + "건(기대 1)");
        var v = sweep.Verdicts[0];
        Require(v.IsSevere && sweep.PostMortemCount == 1, "심각 미달 사후 분석 미기록(Δ " + v.SurpriseDelta.ToString("0.0", CultureInfo.InvariantCulture) + ")");
        Require(c.Notebook.GetSuppressedCompositeKeys().Contains(c.Pair!), "오답 노트 조합 가드 미발동");
        var next = PlutoAutonomousTopicPlannerService.Synthesize(PlannerInputs(c.Notebook.GetSuppressedCompositeKeys()));
        foreach (var b in next)
            Require(!(string.Equals(b.HookStyle, c.Brief!.HookStyle, StringComparison.Ordinal) && string.Equals(b.ColorLookId, c.Brief.ColorLookId, StringComparison.Ordinal)), "플래너가 차단된 조합을 다시 제안");
        Require((await c.Briefs.EvaluatePendingBriefOutcomesAndRehabilitationAsync(ct).ConfigureAwait(false)).EvaluatedCount == 0, "재스윕 이중 채점");
        c.PenaltyBeforeRehab = c.Notebook.GetEffectivePenalty(c.Pair!);
        File.Copy(c.NotebookPath, Path.Combine(c.PeerDir, PlutoMistakeNotebookService.FileName), overwrite: false);   // 🖥️ PC-B = 복권 전 노트
        return "Δ " + v.SurpriseDelta.ToString("+0.0;-0.0", CultureInfo.InvariantCulture) + "p · 사후 분석 1건 · 다음 기획에서 조합 차단";
    }

    // ── ⑥ 🌱 유기적 복권 · 멀티 PC 장부 재생 ──

    private static async Task<string> StageRehabilitationAsync(Context c, CancellationToken ct)
    {
        var brief = c.Brief!;
        c.Clock.Now = T0.AddDays(2);
        // 크리에이터가 같은 조합으로 새 기획 결정을 내려 다시 렌더(벌점 이후 · 다른 기획 ID) → 기대 초과 실측
        PlutoAppliedBriefArm? arm = new("selftest-brief-2", brief.Kind, brief.Format, 1, Theme, brief.HookStyle, brief.ColorLookId, brief.BgmMood, brief.TitleFraming, 50.0, new DateTimeOffset(T0.AddDays(2)));
        var at = new DateTimeOffset(T0.AddDays(2).AddMinutes(5));
        var rec = PlutoBriefOutcomeAttributionService.StampProvenance(
            new PlutoRenderedClipRecord(ClipB, CatalogTitleB, "몽타주", brief.HookStyle, brief.ColorLookId, brief.BgmMood, new[] { "autumn" }, "", at, false), arm, at, 1, out _, out var d);
        Require(d == PlutoBriefStampDecision.Stamped, "두 번째 출처 찍기 실패");
        await c.Catalog.RecordRenderedClipAsync(rec, ct).ConfigureAwait(false);
        var csv = EnHeader + "\n" + YtB + "," + Quote(CatalogTitleB) + ",\"Jan 7, 2026\",500,60,5000,5.0,70,10\n";
        await StudioImportAsync(c, csv, "Table data (1).csv", ct).ConfigureAwait(false);

        c.Clock.Now = T0.AddDays(3);
        var sweep = await c.Briefs.EvaluatePendingBriefOutcomesAndRehabilitationAsync(ct).ConfigureAwait(false);
        Require(sweep.HitCount >= 1, "기대 초과 실측 미적중");
        Require(ContainsKey(sweep.RehabilitatedKeys, c.Pair!), "유기적 복권 미발동");
        Require(c.Notebook.LedgerContains(c.Pair!, ClipB), "복권 장부 미기록");
        var penaltyA = c.Notebook.GetEffectivePenalty(c.Pair!);
        Require(penaltyA < c.PenaltyBeforeRehab, "복권 후 벌점 감쇠 없음");

        // 🖥️ PC-B — 복권 전 노트 + PC-A 장부 합집합 → 적재 시 1회 재생 · 재적재 멱등
        var ledgerA = await File.ReadAllTextAsync(c.Notebook.LedgerStoragePath, ct).ConfigureAwait(false);
        var merged = PlutoConflictMergeService.MergeJson(PlutoRehabilitationLedger.FileName, ledgerA, "{\"Version\":1,\"Records\":[]}") ?? throw new InvalidOperationException("장부 병합 실패");
        var records = PlutoDriveHealthService.CountEntries(Encoding.UTF8.GetBytes(merged));
        Require(records >= 1, "병합 장부 비어 있음");
        var peerNotebook = Path.Combine(c.PeerDir, PlutoMistakeNotebookService.FileName);
        double stale;
        using (var before = new PlutoMistakeNotebookService(peerNotebook, c.Clock.Get))   // 장부 도착 전 PC-B(복권 모름)
        {
            await before.EnsureLoadedAsync(ct).ConfigureAwait(false);
            stale = before.GetEffectivePenalty(c.Pair!);
            Require(stale > penaltyA, "PC-B 가 장부 없이 복권을 알고 있음");
        }
        await File.WriteAllTextAsync(Path.Combine(c.PeerDir, PlutoRehabilitationLedger.FileName), merged, ct).ConfigureAwait(false);
        int replayed;
        double peerPenalty;
        using (var peer = new PlutoMistakeNotebookService(peerNotebook, c.Clock.Get))
        {
            await peer.EnsureLoadedAsync(ct).ConfigureAwait(false);
            replayed = peer.LastLedgerReconcile.Replayed;
            Require(replayed == records, "PC-B 장부 재생 " + replayed.ToString(CultureInfo.InvariantCulture) + "건(기대 " + records.ToString(CultureInfo.InvariantCulture) + ")");   // (키, 클립) 기록마다 정확히 1회
            peerPenalty = peer.GetEffectivePenalty(c.Pair!);
            // 정확히 1회 감쇠(× 0.35) — PC-A 노트의 다른 갱신(폐루프 성공 회복 등)은 노트 병합 몫이라 장부 재생과 무관
            Require(Math.Abs(peerPenalty - stale * PlutoMistakeNotebookService.RehabilitationFactor) < 1e-9, "PC-B 재생 감쇠 불일치");
            Require(peer.LedgerContains(c.Pair!, ClipB), "PC-B 장부 미적재");
        }
        using (var peer2 = new PlutoMistakeNotebookService(Path.Combine(c.PeerDir, PlutoMistakeNotebookService.FileName), c.Clock.Get))
        {
            await peer2.EnsureLoadedAsync(ct).ConfigureAwait(false);
            Require(peer2.LastLedgerReconcile.Replayed == 0 && Math.Abs(peer2.GetEffectivePenalty(c.Pair!) - peerPenalty) < 1e-9, "재적재 이중 감쇠");
        }
        return "복권 1건 · 벌점 " + c.PenaltyBeforeRehab.ToString("0.00", CultureInfo.InvariantCulture) + " → " + penaltyA.ToString("0.00", CultureInfo.InvariantCulture) + " · PC-B 재생 " + replayed.ToString(CultureInfo.InvariantCulture) + "건 · 재적재 0건";
    }

    private static bool ContainsKey(IReadOnlyList<string> keys, string key)
    {
        foreach (var k in keys) if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ── ⑦ 🩺 진단 Markdown 스냅샷 ──

    private static async Task<string> StageDiagnosticAsync(Context c, CancellationToken ct)
    {
        // 동반 파일을 샌드박스 Drive · 로컬 미러 양쪽에 같은 내용으로 배치 → 일치 2/2 기대
        foreach (var dir in new[] { c.DriveDir, c.LocalDir }) Directory.CreateDirectory(dir);
        foreach (var (src, name) in new[] { (c.QueuePath, PlutoYouTubeUnmatchedQueueService.FileName), (c.Notebook.LedgerStoragePath, PlutoRehabilitationLedger.FileName) })
        {
            File.Copy(src, Path.Combine(c.DriveDir, name), overwrite: false);
            File.Copy(src, Path.Combine(c.LocalDir, name), overwrite: false);
        }
        var drive = new SandboxDriveHealth(new PlutoDriveHealthService { LocalMirrorRoot = c.LocalDir }, c.DriveDir, c.StudioDir);
        var report = drive.CheckHealth();
        Require(report.Companions.Count == PlutoDriveHealthService.CompanionSyncFiles.Length && report.CompanionInSyncCount == report.Companions.Count, "동반 파일 동기화 판정 실패");
        var diag = new PlutoSystemDiagnosticService(drive, c.Catalog, c.Notebook, null, () => c.Clock.Now);
        var md = await diag.BuildSnapshotMarkdownAsync(ct).ConfigureAwait(false);
        Require(md.StartsWith(PlutoSystemDiagnosticService.Title, StringComparison.Ordinal), "진단 제목 누락");
        Require(md.Contains(PlutoSystemDiagnosticService.CompanionHeading, StringComparison.Ordinal) && md.Contains(PlutoRehabilitationLedger.FileName, StringComparison.Ordinal), "동반 동기화 소절 누락");
        Require(md.Contains("## 4. 🛡️ 오답 노트 활성 가드", StringComparison.Ordinal) && md.Contains("## 2. 📺 실측 YouTube", StringComparison.Ordinal), "진단 섹션 누락");
        Require(md.IndexOf(c.Root, StringComparison.OrdinalIgnoreCase) < 0, "진단서에 샌드박스 절대 경로 노출");
        var saved = PlutoSystemDiagnosticService.WriteSnapshotFile(Path.Combine(c.Root, "diagnostics"), md, T0, ct);
        Require(saved.Ok && saved.FullPath is not null && File.Exists(saved.FullPath), "진단서 파일 저장 실패");
        return "Markdown " + md.Length.ToString(CultureInfo.InvariantCulture) + "자 · 동반 " + report.CompanionInSyncCount.ToString(CultureInfo.InvariantCulture) + "/"
            + report.Companions.Count.ToString(CultureInfo.InvariantCulture) + " 일치 · .md 저장";
    }

    // ── ⑧ 🌐 다국어 채널 분기 · 일/서 수집 ──

    private const string JaCatalogTitle = "秋の森の散歩ブイログ";
    private const string YtJa = "ytSELFTESTJ";
    private const string JaHeader = "動画,動画のタイトル,動画公開時刻,視聴回数,平均再生率(%),インプレッションのクリック率(%),視聴を選択(%)";

    private static async Task<string> StageMultiLanguageAsync(Context c, CancellationToken ct)
    {
        var channels = new PlutoMultiLanguageChannelService();
        var ja = channels.GetProfileOrDefault("ja-JP");
        // (a) ja-JP 줄바꿈 — 줄당 문자소 ≤ 15 · 금칙(행두 、。) · 로캘 편 토큰 재장식(한국어 장식 누출 없음)
        var wrap = channels.WrapSubtitle("ja-JP", "最後まで見ると分かる、秋の森の散歩の本当の秘密がここにあります。");
        Require(wrap.Lines.Count >= 2, "ja-JP 줄바꿈 미적용");
        foreach (var line in wrap.Lines)
        {
            Require(PlutoMultiLanguageChannelService.TextLength(line) <= ja.MaxCharsPerLine, "ja-JP 줄 길이 초과");
            Require(line.Length > 0 && line[0] is not ('、' or '。'), "ja-JP 금칙(행두) 위반");
        }
        var displayed = PlutoSeriesBatchPlanner.DecorateSubtitles(new[]
        {
            PromptFlowStudio.Services.Export.SubtitleCue.Precise(0.0, 2.5, "秋の森へ"),
            PromptFlowStudio.Services.Export.SubtitleCue.Precise(2.5, 5.0, "最後に分かる"),
        }, 1, 2);
        var proj = PlutoLocaleSubtitleFitter.Project(displayed, channels, "ja-JP", 1, 2);
        Require(proj.Cues.Count == 2 && proj.Cues[0].Text.StartsWith("第1/2話 · ", StringComparison.Ordinal) && proj.Cues[1].Text.EndsWith(PlutoLocaleSubtitleFitter.SeriesTail("ja-JP", false), StringComparison.Ordinal),
            "ja-JP 편 토큰 재장식 실패");
        Require(proj.Cues[0].Text.IndexOf('편') < 0 && !proj.HasTtsOverflow && string.Equals(proj.VoiceId, ja.DefaultTtsVoiceId, StringComparison.Ordinal), "ja-JP 자막/TTS 투영 이상");

        // (b) 로캘 패키지 → {ClipA}::ja-JP 변형 멱등 보장 · 로캘 사이드카
        var svc = new PlutoYouTubePublishPackService(catalog: c.Catalog, clock: static () => new DateTimeOffset(T0)) { Channels = channels };
        var brief = c.Brief!;
        var pack = await svc.GenerateAsync(new PlutoPublishPackRequest(JaCatalogTitle, JaCatalogTitle, 0, 0, brief.HookStyle, brief.StyleTags) { TargetLocale = "ja-JP" }, ct).ConfigureAwait(false);
        Require(pack.Locale == "ja-JP" && string.Join(" ", pack.Hashtags).Contains("#ショート", StringComparison.Ordinal), "ja-JP 업로드 패키지 로캘 누락");
        var before = (await c.Catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false)).Count;
        var variant = await svc.EnsureLocaleVariantAsync(ClipA, pack, ct).ConfigureAwait(false);
        var again = await svc.EnsureLocaleVariantAsync(ClipA + PlutoMultiLanguageChannelService.VariantSeparator + "ja-JP", pack, ct).ConfigureAwait(false);
        var after = (await c.Catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false)).Count;
        var variantId = ClipA + PlutoMultiLanguageChannelService.VariantSeparator + "ja-JP";
        Require(variant is { IsLocaleVariant: true } && string.Equals(variant.VideoId, variantId, StringComparison.Ordinal) && again?.VideoId == variantId && after == before + 1, "로캘 변형 멱등 분기 실패");
        var video = Path.Combine(c.StudioDir, "selftest_ja.mp4");
        Directory.CreateDirectory(c.StudioDir);
        await File.WriteAllBytesAsync(video, new byte[] { 0, 0, 0, 24, 102, 116, 121, 112 }, ct).ConfigureAwait(false);
        var side = await svc.WriteSidecarAsync(video, pack, variantId, ct).ConfigureAwait(false);
        Require(side.Ok && side.Path is not null && side.Path.EndsWith("_ja-JP" + PlutoYouTubePublishPackService.SidecarSuffix, StringComparison.Ordinal) && File.Exists(side.Path), "로캘 사이드카 저장 실패");
        var best = pack.TitleA.LinkabilityScore >= pack.TitleB.LinkabilityScore ? pack.TitleA : pack.TitleB;
        Require(await svc.RecordSelectionAsync(ClipA, pack, best.Variant, ct).ConfigureAwait(false), "로캘 제목 선택 기록 실패");
        var master = await c.Catalog.FindByVideoIdOrTitleAsync(ClipA, ct).ConfigureAwait(false);
        Require(master is not null && master.YouTubeVideoId == YtA && !string.Equals(master.PublishedTitleText, best.Title, StringComparison.Ordinal), "ja-JP 제목 선택이 마스터에 오귀속");

        // (c) 일본어 Studio 내보내기(視聴を選択(%)) → 변형 자동 연결 · 3초 유지율 반영 · 대기열 불변
        var queueBefore = c.Queue.Count;
        var csv = JaHeader + "\n合計,,,800,,,\n" + YtJa + "," + Quote(best.Title) + ",2026/01/05,800,52.0,6.5,71.0\n";
        var summary = await StudioImportAsync(c, csv, "表データ.csv", ct).ConfigureAwait(false);
        var linked = await c.Catalog.FindByYouTubeVideoIdAsync(YtJa, ct).ConfigureAwait(false);
        Require(linked is not null && string.Equals(linked.VideoId, variantId, StringComparison.Ordinal), "일본어 내보내기가 ja-JP 변형에 연결되지 않음");
        Require(linked!.YouTubeWatermark is { } jw && Math.Abs(jw.HookPct - 71.0) < 0.01, "視聴を選択(%) → 3초 유지율 미반영");
        Require(c.Queue.Count == queueBefore, "일본어 행이 미매칭 대기열로 샘");
        var locales = PlutoRealizedYouTubeSummary.Compute(await c.Catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity, ct).ConfigureAwait(false)).Locales;
        var jaRow = 0;
        foreach (var r in locales) if (r.Locale == "ja-JP") jaRow = r.LinkedCount;
        Require(jaRow == 1, "로캘 분해 JA 연결 수 불일치");
        return "ja-JP 줄 " + wrap.Lines.Count.ToString(CultureInfo.InvariantCulture) + " · 변형 " + variantId + " · 사이드카 · " + summary;
    }

    // ── ⑨ ☁️ 기동 선조정 · 블랙박스 진단(Step 38) ──

    private const string YtPeer = "ytSELFPEER9", YtKeep = "ytSELFKEEP9";
    private const string LeakKey = "AIzaSySELFTEST9LEAK0123456789abcd";

    private static async Task<string> StageStartupSyncAsync(Context c, CancellationToken ct)
    {
        // (a) 모사 Drive — 다른 PC 가 Drive 카탈로그에 YouTube ID 를 이미 연결 · 이 PC 대기열엔 같은 ID + 미연결 1건
        var s9 = Path.Combine(c.Root, "s9");
        var primary = Path.Combine(s9, "gdrive", PlutoDriveStorageManager.SubFolder);
        var local = Path.Combine(s9, "local");
        Directory.CreateDirectory(primary);
        Directory.CreateDirectory(local);
        using (var peer = new PlutoRenderCatalogService(Path.Combine(primary, PlutoRenderCatalogService.FileName)))
        {
            await peer.RecordRenderedClipAsync(new PlutoRenderedClipRecord("selftest-peer", "첫눈 오는 골목 산책", "몽타주", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby",
                new[] { "autumn" }, "", new DateTimeOffset(T0), false), ct).ConfigureAwait(false);
            Require(await peer.TryBindYouTubeVideoIdAsync("selftest-peer", YtPeer, ct).ConfigureAwait(false) == PlutoYouTubeBindResult.Bound, "다른 PC 연결 모사 실패");
        }
        var storage = new PlutoDriveStorageManager(primary, local);   // 🔒 레거시 시드 없음 · 기본 경로 미사용
        using var mine = new PlutoRenderCatalogService(Path.Combine(s9, "pcA", PlutoRenderCatalogService.FileName));
        using var queue = new PlutoYouTubeUnmatchedQueueService(storage, mine, c.Loop, () => new DateTimeOffset(c.Clock.Now));
        var at = new DateTimeOffset(T0);
        var queued = await queue.EnqueueAsync(new[]
        {
            new PlutoUnmatchedYouTubeRow(new YouTubeAnalyticsRow(YtPeer, "첫눈 오는 골목 산책", at.AddDays(1), 300, 66, false, 48, 48, 5.0, false), "unmatched", "Table data.csv", at),
            new PlutoUnmatchedYouTubeRow(new YouTubeAnalyticsRow(YtKeep, "아직 미연결 영상", at.AddDays(1), 120, 66, false, 48, 48, 5.0, false), "unmatched", "Table data.csv", at),
        }, ct).ConfigureAwait(false);
        Require(queued == 2, "미매칭 대기열 적재 실패");

        // (b) 단일 비행 · 멱등 — 동시 2회 + 재호출 3회째도 같은 결과(본체 1회) · 가짜 환경(격리 스위치 무관 · 결정론)
        PlutoStartupDriveSyncReport r1;
        using (var sync = new PlutoStartupDriveSyncService(storage, () => queue, null, null, static _ => null))
        {
            var t1 = sync.RunOnceAsync(ct);
            var t2 = sync.RunOnceAsync(ct);
            r1 = await t1.ConfigureAwait(false);
            var r2 = await t2.ConfigureAwait(false);
            var r3 = await sync.RunOnceAsync(ct).ConfigureAwait(false);
            Require(ReferenceEquals(r1, r2) && ReferenceEquals(r1, r3) && sync.RunCount == 1, "기동 선조정 단일 비행/멱등 위반");
        }
        Require(r1.Attempted && r1.DriveError is null && r1.UnmatchedError is null, "기동 선조정 실패: " + (r1.DriveError ?? r1.UnmatchedError ?? r1.DriveSkipReason ?? "—"));
        Require(r1.Unmatched is { PrunedLinked: 1, Remaining: 1 } && queue.IsResolved(YtPeer), "다른 PC 연결 ID 대기열 정리 실패");
        var pending = queue.GetPending();
        Require(pending.Count == 1 && pending[0].YouTubeVideoId == YtKeep, "미연결 행이 잘못 정리됨");

        // (c) 블랙박스 진단 6 섹션 — 용어집 요약 · 최근 자율 활동(비밀값 · 경로 가림)
        using var arc = new PlutoSeriesArcMemoryService(Path.Combine(s9, "arc", PlutoSeriesArcMemoryService.FileName));
        Require(await arc.UpsertGlossaryEntryAsync(Theme, new PlutoGlossaryEntry("숲길", "Forest Trail", "森の小道", "Sendero del bosque"), ct).ConfigureAwait(false), "용어집 등록 실패");
        using var feed = new PlutoAutonomousActivityFeedService(Path.Combine(s9, "feed", PlutoAutonomousActivityFeedService.FeedFileName));
        Require(feed.Record(PlutoActivityKind.DriveSync, r1.SummaryKorean), "활동 피드 기록 실패");
        Require(feed.Record(PlutoActivityKind.ApiPause, "API 정지 key=" + LeakKey + " " + Path.Combine(s9, "clip|final.mp4")), "활동 피드 기록 실패");
        var drive = new SandboxDriveHealth(new PlutoDriveHealthService { LocalMirrorRoot = c.LocalDir }, c.DriveDir, c.StudioDir);
        var diag = new PlutoSystemDiagnosticService(drive, c.Catalog, c.Notebook, null, () => c.Clock.Now) { SeriesArc = arc, ActivityFeed = feed };
        var md = await diag.BuildSnapshotMarkdownAsync(ct).ConfigureAwait(false);
        var last = -1;
        foreach (var h in new[] { "## 1. ", "## 2. ", "## 3. ", "## 4. ", PlutoSystemDiagnosticService.GlossaryHeading, PlutoSystemDiagnosticService.ActivityHeading })
        {
            var at2 = md.IndexOf(h, StringComparison.Ordinal);
            Require(at2 > last, "진단 섹션 누락/순서 이상: " + h);
            last = at2;
        }
        var glossary = md[md.IndexOf(PlutoSystemDiagnosticService.GlossaryHeading, StringComparison.Ordinal)..md.IndexOf(PlutoSystemDiagnosticService.ActivityHeading, StringComparison.Ordinal)];
        Require(glossary.Contains("활성 시리즈 용어집 1개", StringComparison.Ordinal), "§5 용어집 요약 누락");
        var activity = md[md.IndexOf(PlutoSystemDiagnosticService.ActivityHeading, StringComparison.Ordinal)..];
        Require(activity.Contains("DriveSync", StringComparison.Ordinal) && activity.Contains("ApiPause", StringComparison.Ordinal), "§6 최근 자율 활동 누락");
        Require(md.IndexOf("AIzaSySELFTEST9", StringComparison.Ordinal) < 0, "진단서에 비밀값 노출");
        Require(md.IndexOf(c.Root, StringComparison.OrdinalIgnoreCase) < 0 && md.IndexOf(s9, StringComparison.OrdinalIgnoreCase) < 0, "진단서에 샌드박스 절대 경로 노출");
        return "선조정 1회(정리 " + r1.Unmatched!.PrunedLinked.ToString(CultureInfo.InvariantCulture) + " · 잔류 " + r1.Unmatched.Remaining.ToString(CultureInfo.InvariantCulture)
            + ") · 진단 6섹션 " + md.Length.ToString(CultureInfo.InvariantCulture) + "자 · 비밀값/경로 가림";
    }

    /// <summary>진단 서비스가 인자 없는 <c>CheckHealth()</c> 로 라이브 Drive/계정 폴더를 보지 않도록 샌드박스 폴더로 고정.</summary>
    private sealed class SandboxDriveHealth : IPlutoDriveHealthService
    {
        private readonly PlutoDriveHealthService _inner;
        private readonly string _pluto, _studio;
        public SandboxDriveHealth(PlutoDriveHealthService inner, string pluto, string studio) { _inner = inner; _pluto = pluto; _studio = studio; }
        public PlutoDriveHealthReport CheckHealth(string? plutoDriveDir = null, string? studioDriveDir = null) => _inner.CheckHealth(_pluto, _studio);
        public Task<PlutoDriveHealthReport> ForceSyncAndVerifyAsync(CancellationToken ct = default) => Task.FromResult(CheckHealth());
    }

    // ── 플래너 입력(결정론 고정 후보) ──

    private static PlutoRankedOption Opt(PlutoPatternKind kind, string label, double final, double trials = 0, double mult = 1.0, int paretoTier = -1, double bonus = 0.0)
        => new(PlutoAdaptivePatternRanker.PrefixOf(kind) + label, label, 1.0, mult, 0.0, final, "")
        {
            ArmTrials = trials,
            ExplorationBonus = bonus,
            Posterior = PlutoPosteriorConfidence.Compute(mult, trials),
            Pareto = paretoTier < 0 ? PlutoParetoAssessment.None : new PlutoParetoAssessment(paretoTier, 1.0, false, 0.0),
            ObjectiveSamples = paretoTier >= 0 ? 4.0 : 0.0,
        };

    private static PlutoTopicPlannerInputs PlannerInputs(IReadOnlySet<string>? suppressedComposites) => new()
    {
        Hooks = new[]
        {
            Opt(PlutoPatternKind.Hook, "pov-walk", 1.6, trials: 12, mult: 1.6, paretoTier: 0),
            Opt(PlutoPatternKind.Hook, "close-up reveal", 1.1, trials: 4, mult: 1.1),
            Opt(PlutoPatternKind.Hook, "question opener", 0.99, trials: 1, bonus: 0.6),
        },
        Looks = new[]
        {
            Opt(PlutoPatternKind.Look, PlutoColorGradingCompiler.GoldenHourWarmName, 1.2),
            Opt(PlutoPatternKind.Look, PlutoColorGradingCompiler.NaturalPastelName, 1.0),
        },
        BgmMoods = new[] { Opt(PlutoPatternKind.BgmMood, "Lullaby", 1.1), Opt(PlutoPatternKind.BgmMood, "Upbeat", 1.0) },
        StyleTags = new[] { Opt(PlutoPatternKind.StyleTag, "autumn", 1.1) },
        FramingRanking = PlutoAutonomousTopicPlannerService.OrderFraming(null),
        NowUtc = new DateTimeOffset(T0),
        SeedTheme = Theme,
        SuppressedComposites = suppressedComposites,
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // ── 샌드박스 · 실행 문맥 ──

    private sealed class MutableClock
    {
        public DateTime Now = T0;
        public DateTime Get() => Now;
    }

    private sealed class Context : IDisposable
    {
        public readonly string Root, CatalogPath, NotebookPath, QueuePath, PeerDir, DriveDir, LocalDir, StudioDir;
        public readonly MutableClock Clock = new();
        public readonly PlutoRenderCatalogService Catalog;
        public readonly PlutoMistakeNotebookService Notebook;
        public readonly PlutoClosedLoopReinforcementService Loop;
        public readonly PlutoYouTubeUnmatchedQueueService Queue;
        public readonly PlutoTelemetryBatchImporter Importer;
        public readonly PlutoBriefOutcomeAttributionService Briefs;
        public PlutoTopicBrief? Brief;
        public string? Pair, PublishedTitle;
        public double PenaltyBeforeRehab;

        public Context(string root)
        {
            Root = root;
            var pcA = Path.Combine(root, "pcA");
            PeerDir = Path.Combine(root, "pcB");
            DriveDir = Path.Combine(root, "drive");
            LocalDir = Path.Combine(root, "local");
            StudioDir = Path.Combine(root, "studio");
            Directory.CreateDirectory(pcA);
            Directory.CreateDirectory(PeerDir);
            CatalogPath = Path.Combine(pcA, PlutoRenderCatalogService.FileName);
            NotebookPath = Path.Combine(pcA, PlutoMistakeNotebookService.FileName);
            QueuePath = Path.Combine(pcA, PlutoYouTubeUnmatchedQueueService.FileName);
            // 🔒 모든 허브 = 명시 샌드박스 경로(기본 경로 · 저장소 관리자 · Drive 미사용)
            Catalog = new PlutoRenderCatalogService(CatalogPath);
            Notebook = new PlutoMistakeNotebookService(NotebookPath, Clock.Get);
            Loop = new PlutoClosedLoopReinforcementService(null, Notebook, null);
            Queue = new PlutoYouTubeUnmatchedQueueService(QueuePath, Catalog, Loop, () => new DateTimeOffset(Clock.Now));
            Importer = new PlutoTelemetryBatchImporter(Catalog, Loop, Queue);
            Briefs = new PlutoBriefOutcomeAttributionService(Catalog, Notebook);
        }

        public void Dispose()
        {
            TryDispose(Briefs);
            TryDispose(Queue);
            TryDispose(Loop);
            TryDispose(Notebook);
            TryDispose(Catalog);
        }

        private static void TryDispose(IDisposable d)
        {
            try { d.Dispose(); }
            catch (Exception ex) { Debug.WriteLine("[Pluto.SelfTest] dispose: " + ex.Message); }
        }
    }

    /// <summary>🧹 고유 샌드박스 생성(부모 바로 아래 · 접두 + GUID · 소유 표식 파일).</summary>
    public static string CreateSandbox(string parent, string token)
    {
        var p = Path.GetFullPath(parent);
        var di = new DirectoryInfo(p);
        if (!di.Exists) Directory.CreateDirectory(p);
        var root = Path.Combine(p, SandboxPrefix + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("샌드박스 이름 충돌");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, SandboxMarkerFileName), token);
        return root;
    }

    /// <summary>
    /// 🧹 소유가 증명된 샌드박스만 재귀 삭제 — ① 부모 바로 아래 ② 접두 일치 ③ 재분석 지점(링크/정션) 아님 ④ 표식 파일 내용 = 이번 실행 토큰.
    /// 어느 하나라도 어긋나면 삭제하지 않음(false). 일시 잠금은 짧게 재시도. 예외 비전파.
    /// </summary>
    public static bool TryDeleteSandbox(string parent, string root, string token)
    {
        try
        {
            var full = Path.GetFullPath(root);
            var parentFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
            var dirParent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(full));
            if (dirParent is null || !string.Equals(Path.TrimEndingDirectorySeparator(dirParent), parentFull, StringComparison.OrdinalIgnoreCase)) return false;
            if (!Path.GetFileName(Path.TrimEndingDirectorySeparator(full)).StartsWith(SandboxPrefix, StringComparison.Ordinal)) return false;
            var di = new DirectoryInfo(full);
            if (!di.Exists) return true;
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            var marker = Path.Combine(full, SandboxMarkerFileName);
            if (!File.Exists(marker) || !string.Equals(File.ReadAllText(marker).Trim(), token, StringComparison.Ordinal)) return false;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.Delete(full, recursive: true);   // .NET — 하위 링크/정션은 링크 자체만 제거(대상 추적 없음)
                    return !Directory.Exists(full);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 4) { Debug.WriteLine("[Pluto.SelfTest] sandbox delete: " + ex.Message); return false; }
                    Thread.Sleep(40 * (attempt + 1));
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Debug.WriteLine("[Pluto.SelfTest] sandbox delete guard: " + ex.Message);
            return false;
        }
    }

    /// <summary>🔍 라이브 폴더 최상위 파일 메타데이터 지문(이름 · 크기 · 수정 시각 — 파일을 열지 않음 · 하위 폴더 미탐색).</summary>
    public static SortedDictionary<string, string> Fingerprint(IReadOnlyList<string> roots)
    {
        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in roots)
        {
            if (string.IsNullOrWhiteSpace(r)) continue;
            try
            {
                if (!Directory.Exists(r)) continue;
                foreach (var f in Directory.EnumerateFiles(r))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        map[f] = fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { map[f] = "?"; }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { Debug.WriteLine("[Pluto.SelfTest] fingerprint: " + ex.Message); }
        }
        return map;
    }

    public static bool FingerprintEquals(SortedDictionary<string, string> a, SortedDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal)) return false;
        return true;
    }
}
