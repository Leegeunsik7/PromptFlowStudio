using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🅰️🅱️ Step 25 — 폐루프 A/B 제목 CTR 귀속 · 영상 옆 .txt 업로드 패키지 · 멀티 PC 미매칭 대기열 조정 · 자체 추가 안전장치.</summary>
public sealed class PlutoTitleAttributionAndSidecarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_title_attr_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    public PlutoTitleAttributionAndSidecarTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Sub(string name)
    {
        var d = Path.Combine(_dir, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static PlutoRenderCatalogService CatalogAt(string dir) => new(new PlutoDriveStorageManager(null, dir));

    private static PlutoRenderedClipRecord Clip(string id, string title, DateTimeOffset at, string outputPath = "")
        => new(id, title, "Ambient15s", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "autumn" }, outputPath, at, false);

    private static YouTubeAnalyticsRow YtRow(string ytId, string title, long views, double ctr)
        => new(ytId, title, T0.AddDays(1), views, 66, false, 48, 48, ctr, false);

    private static PlutoUnmatchedYouTubeRow Pending(string ytId, string title, long views)
        => new(YtRow(ytId, title, views, 5.0), "unmatched", "Table data.csv", T0);

    private static PlutoPublishPackRequest Req(string title) => new(title, title, 0, 0, "pov-walk", new[] { "autumn" });

    private sealed class NullLoop : IPlutoClosedLoopReinforcementService
    {
        private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
        public PlutoReinforcementOutcome? LastOutcome => null;
        public event Action? Changed { add { } remove { } }
        public Task<PlutoReinforcementOutcome> IngestPerformanceAsync(PlutoVideoPerformanceTelemetry t, CancellationToken ct = default)
            => Task.FromResult(_keys.Add(t.Platform + "|" + t.VideoId)
                ? new PlutoReinforcementOutcome(false, false, 0, 1.0, "ok")
                : new PlutoReinforcementOutcome(false, false, 0, 1.0, "dup") { AlreadyRegistered = true });
        public Task<PlutoReinforcementOutcome> IngestMaturityDeltaAsync(PlutoVideoPerformanceTelemetry matured, PlutoVideoPerformanceTelemetry previous, int stage, double weight, CancellationToken ct = default)
            => Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "delta"));
        public Task<PlutoReinforcementOutcome> IngestExperimentOutcomeAsync(PlutoExperimentReinforcementRequest request, CancellationToken ct = default)
            => Task.FromResult(new PlutoReinforcementOutcome(false, false, 0, 1.0, "noop"));
    }

    /// <summary>게시 → YouTube 연결 → 반영(자동 연결 · 수동 페어링 공용 경로).</summary>
    private static async Task PublishAndLinkAsync(PlutoRenderCatalogService cat, IPlutoClosedLoopReinforcementService loop, string clipId, string ytId, string realizedTitle, long views, double ctr)
    {
        Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync(clipId, ytId));
        var clip = (await cat.FindByYouTubeVideoIdAsync(ytId))!;
        await PlutoTelemetryBatchImporter.ApplyYouTubeRowAsync(cat, loop, clip, YtRow(ytId, realizedTitle, views, ctr), "YouTube Shorts", CancellationToken.None);
    }

    // ── ① A/B 선택 기록 → 실측 CTR → 다음 패키지 프레이밍 순위 ──

    [Fact]
    public async Task CopyingTitleVariant_RecordsMetadata_AndRealizedCtrPromotesWinningFramingInFuturePacks()
    {
        var dir = Sub("p1");
        using var cat = CatalogAt(dir);
        var loop = new NullLoop();
        var svc = new PlutoYouTubePublishPackService(catalog: cat, clock: () => T0);
        var titles = new[] { "가을 단풍 숲길 산책 브이로그 모음", "겨울 첫눈 내리는 공원 산책 기록", "봄 벚꽃 강변 자전거 여행 기록" };
        for (var i = 0; i < titles.Length; i++) await cat.RecordRenderedClipAsync(Clip("clip" + i, titles[i], T0.AddHours(i)));

        // 근거 없음 → Step 24 기본 프레이밍(정보형 A · 호기심형 B) 유지
        var p0 = await svc.GenerateAsync(Req(titles[0]));
        Assert.Equal(PlutoTitleFramings.OutcomeProof, p0.TitleA.Framing);
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, p0.TitleB.Framing);

        // 제목 B 복사 → 카탈로그에 변형 · 프레이밍 · 원문 영속
        Assert.True(await svc.RecordSelectionAsync("clip0", p0, "B"));
        using (var reopened = CatalogAt(dir))
        {
            var c0 = (await reopened.FindByVideoIdOrTitleAsync("clip0"))!;
            Assert.Equal("B", c0.SelectedTitleVariant);
            Assert.Equal(PlutoTitleFramings.CuriosityQuestion, c0.PublishedTitleFraming);
            Assert.Equal(p0.TitleB.Title, c0.PublishedTitleText);
            Assert.Equal(PlutoTitleAttributionStates.Copied, c0.TitleAttribution);
            Assert.Null(c0.AlternateTitleText);
        }
        await PublishAndLinkAsync(cat, loop, "clip0", "YtCurious01", p0.TitleB.Title, 2000, 9.0);

        var p1 = await svc.GenerateAsync(Req(titles[1]));
        Assert.True(await svc.RecordSelectionAsync("clip1", p1, "A"));
        await PublishAndLinkAsync(cat, loop, "clip1", "YtOutcome01", p1.TitleA.Title, 2000, 3.0);

        Assert.Equal(PlutoTitleAttributionStates.Confirmed, (await cat.FindByVideoIdOrTitleAsync("clip0"))!.TitleAttribution);
        Assert.Equal(PlutoTitleAttributionStates.Confirmed, (await cat.FindByVideoIdOrTitleAsync("clip1"))!.TitleAttribution);
        var p2 = await svc.GenerateAsync(Req(titles[2]));
        // 표본 2건(9% vs 3%)만으로 이미 피드백 반영 — 호기심형이 A로 승격 · 미시험 결말형(전역 평균)이 실측 최저 정보형보다 앞섬
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, p2.TitleA.Framing);
        Assert.Equal(PlutoTitleFramings.EndingPrompt, p2.TitleB.Framing);
        Assert.True(await svc.RecordSelectionAsync("clip2", p2, "A"));
        await PublishAndLinkAsync(cat, loop, "clip2", "YtCurious02", p2.TitleA.Title, 1500, 8.0);

        // 실측 CTR(9 · 8 vs 3) → 호기심형이 A로 승격 · A/B는 서로 다르고 둘 다 자동 연결 가능
        var next = await svc.GenerateAsync(Req("여름 바닷가 노을 산책 브이로그 모음"));
        var ranking = svc.GetFramingRanking();
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, ranking[0].Framing);
        Assert.Equal(2, ranking[0].Samples);
        Assert.Equal(3500, ranking[0].Views);
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, next.TitleA.Framing);
        Assert.NotEqual(next.TitleA.Title, next.TitleB.Title);
        Assert.NotEqual(next.TitleA.Framing, next.TitleB.Framing);
        Assert.True(next.TitleA.AutoLinkable && next.TitleB.AutoLinkable);
        Assert.True(next.TitleA.LinkabilityScore >= PlutoYouTubeCatalogLinker.MinFuzzyScore);
        Assert.Equal(PlutoTitleFramings.OutcomeProof, ranking[^1].Framing);   // 실측 최저 CTR → 최하위
    }

    // ── ② 영상 옆 {base}_publish_pack.txt ──

    [Fact]
    public async Task Sidecar_IsWrittenBesideRenderedMp4_AsUtf8_AndUnsafePathsAreRejected()
    {
        var dir = Sub("render out");
        var mp4 = Path.Combine(dir, "가을 산책 01.mp4");
        File.WriteAllBytes(mp4, new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70 });
        var svc = new PlutoYouTubePublishPackService(clock: () => T0);
        var pack = svc.Generate(new PlutoPublishPackRequest("가을 산책", "가을 산책 2/3편", 2, 3, "pov-walk", new[] { "autumn", "walk" }));

        var r = await svc.WriteSidecarAsync(mp4, pack, "clip_autumn");
        Assert.True(r.Ok, r.MessageKorean);
        Assert.Equal(Path.Combine(dir, "가을 산책 01" + PlutoYouTubePublishPackService.SidecarSuffix), r.Path);
        var bytes = File.ReadAllBytes(r.Path!);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);   // UTF-8 BOM → 메모장 한글 안전
        var text = File.ReadAllText(r.Path!, Encoding.UTF8);
        Assert.Contains(pack.TitleA.Title, text, StringComparison.Ordinal);
        Assert.Contains(pack.TitleB.Title, text, StringComparison.Ordinal);
        Assert.Contains(pack.PinnedComment, text, StringComparison.Ordinal);
        Assert.Contains("#shorts", text, StringComparison.Ordinal);
        Assert.Contains("[설명]", text, StringComparison.Ordinal);
        Assert.Contains("clip_autumn", text, StringComparison.Ordinal);
        Assert.Contains("2/3편", text, StringComparison.Ordinal);
        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        // 재저장 → 원자 교체(덮어쓰기) · 파일 1개 유지
        Assert.True((await svc.WriteSidecarAsync(mp4, pack, null)).Ok);
        Assert.Single(Directory.GetFiles(dir, "*" + PlutoYouTubePublishPackService.SidecarSuffix));

        // 경로 위생 — 상대 경로 · 상위 이동 · 비영상 확장자 · 장치 접두 · 부재 영상 · 같은 이름 폴더
        foreach (var bad in new[] { "relative\\clip.mp4", Path.Combine(dir, "..", "escape.mp4"), Path.Combine(dir, "notes.txt"), @"\\?\" + mp4, Path.Combine(dir, "missing.mp4"), "" })
        {
            var br = await svc.WriteSidecarAsync(bad, pack);
            Assert.False(br.Ok, bad);
            Assert.Null(br.Path);
        }
        Assert.Null(PlutoYouTubePublishPackService.TryGetSidecarPath(Path.Combine(dir, "..", "x.mp4"), out var why));
        Assert.False(string.IsNullOrEmpty(why));
        var blocked = Path.Combine(dir, "blocked.mp4");
        File.WriteAllBytes(blocked, new byte[] { 1 });
        Directory.CreateDirectory(Path.Combine(dir, "blocked" + PlutoYouTubePublishPackService.SidecarSuffix));
        Assert.False((await svc.WriteSidecarAsync(blocked, pack)).Ok);
        Assert.False(File.Exists(Path.Combine(_dir, "escape" + PlutoYouTubePublishPackService.SidecarSuffix)));
    }

    // ── ③ 멀티 PC 미매칭 대기열 조정 ──

    [Fact]
    public async Task CrossPcReconcile_PrunesRowsLinkedOnEitherPc_AndKeepsHigherViewUnlinkedRows()
    {
        string a = Sub("pcA"), b = Sub("pcB");
        using var catA = CatalogAt(a);
        using var catB = CatalogAt(b);
        var loop = new NullLoop();
        await catB.RecordRenderedClipAsync(Clip("clip_b", "겨울 산책", T0));
        Assert.Equal(PlutoYouTubeBindResult.Bound, await catB.TryBindYouTubeVideoIdAsync("clip_b", "LinkedOnB01"));   // PC B에서만 연결
        await catA.RecordRenderedClipAsync(Clip("clip_a", "봄 산책", T0));
        Assert.Equal(PlutoYouTubeBindResult.Bound, await catA.TryBindYouTubeVideoIdAsync("clip_a", "LinkedOnA01"));   // PC A에서만 연결

        string qa = Path.Combine(a, PlutoYouTubeUnmatchedQueueService.FileName), qb = Path.Combine(b, PlutoYouTubeUnmatchedQueueService.FileName);
        using var queueA = new PlutoYouTubeUnmatchedQueueService(qa, catA, loop, () => T0);
        using (var queueB = new PlutoYouTubeUnmatchedQueueService(qb, catB, loop, () => T0.AddHours(1)))
        {
            Assert.Equal(3, await queueA.EnqueueAsync(new[] { Pending("RowShared01", "공유 행", 100), Pending("LinkedOnB01", "B 연결 행", 50), Pending("RowOnlyA001", "A 전용", 70) }));
            Assert.Equal(3, await queueB.EnqueueAsync(new[] { Pending("RowShared01", "공유 행", 900), Pending("RowOnlyB001", "B 전용", 300), Pending("LinkedOnA01", "A 연결 행", 40) }));
        }

        var changed = 0;
        queueA.Changed += () => Interlocked.Increment(ref changed);
        var r = await queueA.ReconcileAsync(new[] { qb });
        Assert.True(r.Changed);
        Assert.Equal(2, r.PrunedLinked);   // LinkedOnB01(PC B 카탈로그) · LinkedOnA01(PC A 카탈로그)
        Assert.Equal(3, r.Remaining);   // RowShared01 · RowOnlyA001 · RowOnlyB001
        Assert.Equal(2, r.MergedFromPeers);   // RowOnlyB001 신규 + RowShared01 성숙(100 → 900)
        Assert.Equal(1, changed);

        var pending = queueA.GetPending();
        Assert.Equal(3, pending.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pending)
        {
            ids.Add(p.YouTubeVideoId);
            if (p.YouTubeVideoId == "RowShared01") Assert.Equal(900, p.Row.Views);   // 더 성숙한 스냅샷 유지
        }
        Assert.Contains("RowOnlyB001", ids);
        Assert.Contains("RowOnlyA001", ids);
        Assert.DoesNotContain("LinkedOnB01", ids);
        Assert.DoesNotContain("LinkedOnA01", ids);

        // 영속 · 재조정은 무변경(무기록 · 무통지)
        using (var reopened = new PlutoYouTubeUnmatchedQueueService(qa, catA, loop, () => T0))
        {
            await reopened.EnsureLoadedAsync();
            Assert.Equal(3, reopened.Count);
            Assert.True(reopened.IsResolved("LinkedOnB01"));
        }
        var again = await queueA.ReconcileAsync(new[] { qb });
        Assert.False(again.Changed);
        Assert.Equal(1, changed);
    }

    // ── ④ 자체 추가 #1 — 실제 게시 제목으로 A/B 귀속 판정(전체 패키지 복사 · 게시 전 수정) ──

    [Fact]
    public async Task CoArchitect_RealizedYouTubeTitle_ResolvesFullPackVariant_FreezesIt_AndEditedTitlesAreExcluded()
    {
        using var cat = CatalogAt(Sub("p4"));
        var loop = new NullLoop();
        var svc = new PlutoYouTubePublishPackService(catalog: cat);
        await cat.RecordRenderedClipAsync(Clip("full_pack", "가을 단풍 숲길 산책 브이로그 모음", T0));
        await cat.RecordRenderedClipAsync(Clip("edited", "겨울 첫눈 내리는 공원 산책 기록", T0.AddHours(1)));

        var p = await svc.GenerateAsync(Req("가을 단풍 숲길 산책 브이로그 모음"));
        Assert.True(await svc.RecordSelectionAsync("full_pack", p, "AB"));   // 전체 패키지 → 판정 대기(A 잠정 + B 대체)
        var pending = (await cat.FindByVideoIdOrTitleAsync("full_pack"))!;
        Assert.Equal("A", pending.SelectedTitleVariant);
        Assert.Equal(p.TitleB.Title, pending.AlternateTitleText);

        // 크리에이터가 실제로는 B를 게시(대소문자 · 공백 차이) → B로 교정 확정
        await PublishAndLinkAsync(cat, loop, "full_pack", "YtFullPack1", "  " + p.TitleB.Title.ToUpperInvariant() + " ", 3000, 7.5);
        var resolved = (await cat.FindByVideoIdOrTitleAsync("full_pack"))!;
        Assert.Equal("B", resolved.SelectedTitleVariant);
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, resolved.PublishedTitleFraming);
        Assert.Equal(p.TitleB.Title, resolved.PublishedTitleText);
        Assert.Equal(PlutoTitleAttributionStates.Confirmed, resolved.TitleAttribution);
        Assert.Null(resolved.AlternateTitleText);

        // 🧊 판정 후 재복사는 실측 귀속을 덮지 않음
        Assert.False(await svc.RecordSelectionAsync("full_pack", p, "A"));
        Assert.Equal("B", (await cat.FindByVideoIdOrTitleAsync("full_pack"))!.SelectedTitleVariant);

        // 게시 전 제목을 통째로 고침 → Diverged(귀속 제외)
        var pe = await svc.GenerateAsync(Req("겨울 첫눈 내리는 공원 산책 기록"));
        Assert.True(await svc.RecordSelectionAsync("edited", pe, "A"));
        await PublishAndLinkAsync(cat, loop, "edited", "YtEdited001", "역대급 반전 대공개 미쳤다", 5000, 12.0);
        Assert.Equal(PlutoTitleAttributionStates.Diverged, (await cat.FindByVideoIdOrTitleAsync("edited"))!.TitleAttribution);

        Assert.Equal(1, svc.RefreshFramingEvidence(await cat.GetRecentClipsAsync(100)));   // 확정 1건만 표본(수정 제목 12% CTR 은 오귀속 금지)
        foreach (var s in svc.GetFramingRanking())
            if (s.Framing == PlutoTitleFramings.OutcomeProof) Assert.Equal(0, s.Samples);

        // 순수 판정 — 미기록 · 빈 제목 → 그대로
        var bare = Clip("bare", "x", T0);
        Assert.Same(bare, PlutoYouTubePublishPackService.ResolveRealizedTitle(bare, "anything"));
        Assert.Same(resolved, PlutoYouTubePublishPackService.ResolveRealizedTitle(resolved, null));
    }

    // ── ⑤ 자체 추가 #2 — 처리 기록(🪦)으로 무시/페어링 행의 부활 차단(재수집 · 다른 PC · 충돌 병합) ──

    [Fact]
    public async Task CoArchitect_Tombstones_StopDismissedRowsFromResurrectingAcrossReimportsPcsAndConflictMerges()
    {
        string a = Sub("tA"), b = Sub("tB");
        using var catA = CatalogAt(a);
        using var catB = CatalogAt(b);
        var loop = new NullLoop();
        string qa = Path.Combine(a, PlutoYouTubeUnmatchedQueueService.FileName), qb = Path.Combine(b, PlutoYouTubeUnmatchedQueueService.FileName);
        var now = DateTimeOffset.UtcNow;   // 충돌 병합은 실시계로 TTL 판정 → 처리 기록도 실시계 기준
        using var queueA = new PlutoYouTubeUnmatchedQueueService(qa, catA, loop, () => now);
        using var queueB = new PlutoYouTubeUnmatchedQueueService(qb, catB, loop, () => now);
        await queueA.EnqueueAsync(new[] { Pending("OtherChan01", "다른 채널 영상", 500), Pending("KeepRow0001", "보존 행", 10) });
        await queueB.EnqueueAsync(new[] { Pending("OtherChan01", "다른 채널 영상", 800) });

        Assert.True(await queueA.DismissAsync("OtherChan01"));
        // 자동 감시가 같은 기간 내보내기(더 성숙)를 다시 수집해도 되살아나지 않음
        Assert.Equal(0, await queueA.EnqueueAsync(new[] { Pending("OtherChan01", "다른 채널 영상", 5000) }));
        Assert.Equal(1, queueA.Count);

        // 다른 PC(B)는 A의 처리 기록을 받아 정리 · A는 B 사본의 더 큰 조회수 행을 되살리지 않음
        var rb = await queueB.ReconcileAsync(new[] { qa });
        Assert.Equal(1, rb.PrunedResolved);
        Assert.Equal("KeepRow0001", Assert.Single(queueB.GetPending()).YouTubeVideoId);   // A의 보존 행은 합류 · 무시 행은 정리
        Assert.True(queueB.IsResolved("OtherChan01"));
        var ra = await queueA.ReconcileAsync(new[] { qb });
        Assert.Equal(0, ra.MergedFromPeers);
        Assert.Single(queueA.GetPending());

        // 충돌 사본 의미 병합도 같은 규칙(동반 파일 · 핵심 허브 불변식 유지)
        var current = File.ReadAllText(qa);
        var other = JsonSerializer.Serialize(new PlutoYouTubeUnmatchedQueueService.Snapshot
        {
            Rows = new List<PlutoYouTubeUnmatchedQueueService.RowDto>
            {
                new() { YouTubeVideoId = "OtherChan01", Title = "다른 채널 영상", Views = 9999, Ctr = 5, Hook = 60, Completion = 40, QueuedAtUtc = T0 },
                new() { YouTubeVideoId = "FromPeer001", Title = "새 행", Views = 20, Ctr = 5, Hook = 60, Completion = 40, QueuedAtUtc = T0.AddMinutes(5) },
            },
        });
        Assert.True(PlutoConflictMergeService.IsMergeable(PlutoYouTubeUnmatchedQueueService.FileName));
        var merged = JsonSerializer.Deserialize<PlutoYouTubeUnmatchedQueueService.Snapshot>(PlutoConflictMergeService.MergeJson(PlutoYouTubeUnmatchedQueueService.FileName, current, other)!)!;
        var mergedIds = new List<string>();
        foreach (var row in merged.Rows!) mergedIds.Add(row.YouTubeVideoId);
        Assert.DoesNotContain("OtherChan01", mergedIds);
        Assert.Contains("FromPeer001", mergedIds);
        Assert.Contains("KeepRow0001", mergedIds);
        Assert.Contains(merged.Resolved!, t => t.YouTubeVideoId == "OtherChan01" && t.Kind == PlutoYouTubeUnmatchedQueueService.TombstoneDismissed);
        Assert.Null(PlutoConflictMergeService.MergeJson(PlutoYouTubeUnmatchedQueueService.FileName, current, "{broken"));

        // TTL 만료 처리 기록은 병합에서 소멸(영구 차단 아님)
        var stale = new PlutoYouTubeUnmatchedQueueService.Snapshot
        {
            Resolved = new List<PlutoYouTubeUnmatchedQueueService.TombstoneDto> { new() { YouTubeVideoId = "OldDismiss1", Kind = "dismissed", AtUtc = T0 } },
        };
        Assert.Empty(PlutoYouTubeUnmatchedQueueService.MergeSnapshots(new[] { stale }, T0 + PlutoYouTubeUnmatchedQueueService.TombstoneTtl + TimeSpan.FromDays(1)).Resolved!);

        // Drive 동기화 편입 — 핵심 허브 9종 · 병합 허브 6종 불변식은 그대로
        Assert.Equal(9, PlutoDriveHealthService.PlutoFiles.Length);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.DoesNotContain(PlutoYouTubeUnmatchedQueueService.FileName, PlutoDriveHealthService.PlutoFiles);
        Assert.Contains(PlutoYouTubeUnmatchedQueueService.FileName, PlutoDriveHealthService.SyncFiles);
        Assert.Equal(PlutoDriveHealthService.PlutoFiles.Length + 1, PlutoDriveHealthService.SyncFiles.Length);
    }

    // ── ⑥ 자체 추가 #3 — 근거 게이트 · 조회수 가중 축소 · 클립당 1표본(멀티 PC/재수집 이중 집계 차단) ──

    [Fact]
    public void CoArchitect_FramingEvidence_IsShrunkViewWeightedGatedAndCountedOncePerVideo()
    {
        static PlutoRenderedClipRecord Attributed(string id, string yt, string framing, string state, long views, double ctr, int stage = 0)
            => Clip(id, "제목 " + id, T0) with
            {
                YouTubeVideoId = yt, SelectedTitleVariant = "A", PublishedTitleFraming = framing, PublishedTitleText = "제목 " + id, TitleAttribution = state,
                YouTubeWatermark = new PlutoYouTubeIngestWatermark(views, stage, 60, 40, ctr, T0),
            };

        var svc = new PlutoYouTubePublishPackService();
        // 근거 1건(호기심형 CTR 15%) → 게이트 미달 → 기본 순서 유지
        Assert.Equal(1, svc.RefreshFramingEvidence(new[] { Attributed("c1", "YtGate00001", PlutoTitleFramings.CuriosityQuestion, PlutoTitleAttributionStates.Confirmed, 5000, 15) }));
        Assert.Equal(PlutoTitleFramings.OutcomeProof, svc.GetFramingRanking()[0].Framing);
        Assert.Equal(PlutoTitleFramings.OutcomeProof, svc.Generate(Req("가을 단풍 숲길 산책 브이로그 모음")).TitleA.Framing);

        var clips = new[]
        {
            Attributed("o1", "YtOutcome01", PlutoTitleFramings.OutcomeProof, PlutoTitleAttributionStates.Confirmed, 2000, 6, stage: 3),   // 성숙 증분 여러 번 → 여전히 1표본
            Attributed("o2", "YtOutcome02", PlutoTitleFramings.OutcomeProof, PlutoTitleAttributionStates.Confirmed, 2000, 6),
            Attributed("o2dup", "YtOutcome02", PlutoTitleFramings.OutcomeProof, PlutoTitleAttributionStates.Confirmed, 2000, 6),        // 같은 YouTube ID 중복(병합 잔재) → 무시
            Attributed("e1", "YtEnding001", PlutoTitleFramings.EndingPrompt, PlutoTitleAttributionStates.Confirmed, 40, 30),             // 조회수 50 미만 → 잡음 제외
            Attributed("e2", "YtEnding002", PlutoTitleFramings.EndingPrompt, PlutoTitleAttributionStates.Confirmed, 200, 9),             // 가중치 0.2
            Attributed("d1", "YtDiverge01", PlutoTitleFramings.CuriosityQuestion, PlutoTitleAttributionStates.Diverged, 9000, 25),      // 수정 제목 → 제외
            Attributed("p1", "YtCopied001", PlutoTitleFramings.CuriosityQuestion, PlutoTitleAttributionStates.Copied, 9000, 25),        // 미확정 → 제외
        };
        var stats = PlutoYouTubePublishPackService.ComputeFramingStats(clips);
        PlutoTitleFramingStat Of(string f) { foreach (var s in stats) if (s.Framing == f) return s; throw new InvalidOperationException(f); }
        Assert.Equal(2, Of(PlutoTitleFramings.OutcomeProof).Samples);
        Assert.Equal(4000, Of(PlutoTitleFramings.OutcomeProof).Views);
        Assert.Equal(1, Of(PlutoTitleFramings.EndingPrompt).Samples);
        Assert.Equal(0, Of(PlutoTitleFramings.CuriosityQuestion).Samples);

        // 경험적 베이즈: 가중 평균 μ = (2·6 + 0.2·9)/2.2 · 축소값은 평균과 μ 사이(행운의 저조회 1편이 과대평가되지 않음)
        var mu = (12 + 1.8) / 2.2;
        var ending = Of(PlutoTitleFramings.EndingPrompt);
        Assert.Equal(9, ending.MeanCtrPct, 6);
        Assert.Equal((1.8 + PlutoYouTubePublishPackService.FramingPriorStrength * mu) / (0.2 + PlutoYouTubePublishPackService.FramingPriorStrength), ending.ShrunkCtrPct, 6);
        Assert.True(ending.ShrunkCtrPct < ending.MeanCtrPct && ending.ShrunkCtrPct > mu);
        Assert.Equal(mu, Of(PlutoTitleFramings.CuriosityQuestion).ShrunkCtrPct, 6);   // 미시험 프레이밍 = 전역 평균(탐색 여지)

        Assert.Equal(3, svc.RefreshFramingEvidence(clips));
        var ranking = svc.GetFramingRanking();
        Assert.Equal(PlutoTitleFramings.EndingPrompt, ranking[0].Framing);
        Assert.Equal(PlutoTitleFramings.OutcomeProof, ranking[2].Framing);   // 평균 미만 → 미시험보다 아래
        var pack = svc.Generate(Req("가을 단풍 숲길 산책 브이로그 모음"));
        Assert.Equal(PlutoTitleFramings.EndingPrompt, pack.TitleA.Framing);
        Assert.NotEqual(pack.TitleA.Title, pack.TitleB.Title);
        Assert.True(pack.TitleA.AutoLinkable && pack.TitleB.AutoLinkable);
    }
}
