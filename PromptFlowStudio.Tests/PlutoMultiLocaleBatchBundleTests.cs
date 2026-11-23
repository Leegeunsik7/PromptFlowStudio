using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🌍 Step 39 — 원클릭 전 언어(KO · EN · JA · ES) 일괄 분기/패키지 · Stage-9 자가진단 · 자체 추가 안전장치
/// (사이드카 묶음 전부-아니면-전무 · 커밋 실패 복원 · 게시 로캘 동결 · 같은 마스터 단일 비행).
/// 전부 임시 폴더 · 결정론 로컬 번역기(네트워크 0 · 환경 변수 무변경) — 병렬 컬렉션 안전.
/// </summary>
public sealed class PlutoMultiLocaleBatchBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_batch39_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly string[] All = { "ko-KR", "en-US", "ja-JP", "es-ES" };

    public PlutoMultiLocaleBatchBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Sub(string name)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private static string NewClipId() => "clip39_" + Guid.NewGuid().ToString("N")[..10];

    private static readonly PlutoGlossaryEntry Minjun = new("민준", "Minjun", "ミンジュン", "Minyun");

    private static IReadOnlyList<SubtitleCue> KoreanScript() => new[]
    {
        SubtitleCue.Precise(0.0, 2.5, "민준이 숲길을 걷는다"),
        SubtitleCue.Precise(2.5, 5.0, "마지막에 비밀이 밝혀진다"),
    };

    /// <summary>결정론 로컬 번역기 — 자리표시자 전부 보존 + 로캘별 본문(<paramref name="body"/> 로 로캘별 길이 · 예외 주입).</summary>
    private static PlutoTranslationProvider Provider(Func<PlutoTranslationRequest, string>? body = null) => (req, ct) =>
    {
        var text = body is null ? DefaultBody(req.TargetLocale) : body(req);
        return Task.FromResult<string?>((string.Join(" ", req.Placeholders) + " " + text).Trim());
    };

    private static string DefaultBody(string locale) => locale switch
    {
        "en-US" => "walks the trail",
        "ja-JP" => "が歩く",
        _ => "camina",
    };

    private sealed class Rig : IDisposable
    {
        public readonly PlutoMultiLanguageChannelService Channels = new();
        public readonly PlutoRenderCatalogService Catalog;
        public readonly PlutoSubtitleLocalizationService L10n;
        public readonly PlutoYouTubePublishPackService Packs;
        public readonly string ClipId = NewClipId();
        public readonly string Video;

        public Rig(string dir, PlutoTranslationProvider? provider = null)
        {
            Catalog = new PlutoRenderCatalogService(Path.Combine(dir, PlutoRenderCatalogService.FileName));
            L10n = new PlutoSubtitleLocalizationService(Channels, provider ?? Provider(), null, providerUsesNetwork: false);
            Packs = new PlutoYouTubePublishPackService(catalog: Catalog, clock: static () => T0) { Channels = Channels };
            Video = Path.Combine(dir, "forest_walk.mp4");
            File.WriteAllBytes(Video, new byte[] { 0, 0, 0, 24, 102, 116, 121, 112 });
        }

        public PlutoMultiLocaleBatchServices Deps => new(L10n, Catalog, Packs);

        public async Task SeedMasterAsync()
            => await Catalog.RecordRenderedClipAsync(new PlutoRenderedClipRecord(ClipId, "숲길 산책 브이로그", "몽타주", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby",
                new[] { "autumn" }, Video, T0, false));

        public PlutoMultiLocaleBatchRequest Request(bool sidecars = true) => new(ClipId, KoreanScript())
        {
            ContextKey = "montage", Glossary = new[] { Minjun }, Theme = "숲길 산책", CatalogTitle = "숲길 산책 브이로그",
            HookStyle = "pov-walk", StyleTags = new[] { "autumn" }, VideoPath = sidecars ? Video : null,
        };

        public string Sidecar(string locale) => PlutoYouTubePublishPackService.TryGetSidecarPath(Video, locale, out _)!;

        public void Dispose() => Catalog.Dispose();
    }

    private static int CountVariants(IReadOnlyList<PlutoRenderedClipRecord> clips, string baseId)
    {
        var n = 0;
        foreach (var c in clips) if (c.VideoId.StartsWith(baseId, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private static void AssertNoStagingLeftovers(string dir)
    {
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        Assert.Empty(Directory.GetFiles(dir, "*.bak"));
    }

    // ── 1. 원클릭 4개 로캘 — 용어집 보존 · 멱등 분기 · 4개 사이드카 원자 저장 · 기존 연결 ID 불변 ──

    [Fact]
    public async Task OneClickBatch_AllFourLocales_PreservesGlossary_IdempotentVariants_WritesFourSidecars_AndNeverClobbersLinkedYouTubeId()
    {
        using var rig = new Rig(Sub("all4"));
        await rig.SeedMasterAsync();

        var r = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps).WaitAsync(Wait);

        Assert.False(r.Busy);
        Assert.Equal(All.Length, r.Items.Count);
        for (var i = 0; i < All.Length; i++)
        {
            var it = r.Items[i];
            Assert.Equal(All[i], it.Locale);   // 결정론 순서
            Assert.True(it.Status == PlutoLocaleBatchStatus.SidecarSaved, it.Locale + ": " + it.Status + " " + it.Error);
            Assert.Equal(it.Locale, it.Pack!.Locale);
            Assert.True(File.Exists(it.SidecarPath), it.Locale);
            Assert.EndsWith("_" + it.Locale + PlutoYouTubePublishPackService.SidecarSuffix, it.SidecarPath!, StringComparison.Ordinal);
            Assert.InRange(it.SpeakingRate, PlutoMultiLanguageChannelService.MinSpeakingRate, PlutoMultiLanguageChannelService.MaxSpeakingRate);
        }
        Assert.True(r.Bundle is { Committed: true, RolledBack: false });
        AssertNoStagingLeftovers(Path.GetDirectoryName(rig.Video)!);

        // KO = 마스터 그대로(이중 레코드 없음) · EN/JA/ES = {BaseClipId}::{Locale}
        Assert.Equal(rig.ClipId, r.Find("ko-KR")!.VariantClipId);
        foreach (var loc in new[] { "en-US", "ja-JP", "es-ES" })
            Assert.Equal(rig.ClipId + PlutoMultiLanguageChannelService.VariantSeparator + loc, r.Find(loc)!.VariantClipId);

        // 📖 용어집 · 고유명사 보존(로캘별 고정 번역) — 비파괴 트랙 · 원문 불변
        Assert.Contains("Minjun", rig.L10n.GetTrack("montage", "en-US")!.Texts[0], StringComparison.Ordinal);
        Assert.Contains("ミンジュン", rig.L10n.GetTrack("montage", "ja-JP")!.Texts[0], StringComparison.Ordinal);
        Assert.Contains("Minyun", rig.L10n.GetTrack("montage", "es-ES")!.Texts[0], StringComparison.Ordinal);
        Assert.Null(rig.L10n.GetTrack("montage", "ko-KR"));
        Assert.Equal(PlutoTranslationStatus.Translated, r.Find("en-US")!.Translation);
        Assert.Contains("ja-JP", File.ReadAllText(rig.Sidecar("ja-JP"), Encoding.UTF8), StringComparison.Ordinal);

        // 멱등 — 두 번째 일괄은 레코드를 늘리지 않음
        var clipsBefore = await rig.Catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity);
        Assert.Equal(4, CountVariants(clipsBefore, rig.ClipId));
        Assert.Equal(PlutoYouTubeBindResult.Bound, await rig.Catalog.TryBindYouTubeVideoIdAsync(rig.ClipId + "::en-US", "ytEN39LINKD"));

        var again = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps).WaitAsync(Wait);
        var clipsAfter = await rig.Catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity);
        Assert.Equal(4, CountVariants(clipsAfter, rig.ClipId));
        Assert.Equal("ytEN39LINKD", (await rig.Catalog.FindByYouTubeVideoIdAsync("ytEN39LINKD"))!.YouTubeVideoId);
        Assert.Equal(rig.ClipId + "::en-US", (await rig.Catalog.FindByYouTubeVideoIdAsync("ytEN39LINKD"))!.VideoId);
        Assert.Equal(PlutoLocaleBatchStatus.AlreadyPublished, again.Find("en-US")!.Status);
        Assert.Equal("ytEN39LINKD", again.Find("en-US")!.YouTubeVideoId);
        Assert.Equal(3, again.Count(PlutoLocaleBatchStatus.SidecarSaved));
        Assert.Contains("EN 🔒게시됨", again.SummaryKorean, StringComparison.Ordinal);
    }

    // ── 2. 로캘별 TTS 초과 · 번역 예외가 형제 로캘을 멈추지 않음 ──

    [Fact]
    public async Task PerLocaleTtsOverflowAndTranslatorFailure_AreCollectedAsWarnings_WithoutAbortingSiblingLocales()
    {
        var provider = Provider(req => req.TargetLocale switch
        {
            "ja-JP" => new string('森', 180),                                      // 2.5 s 컷에 180자 → 1.30x 로도 불가
            "es-ES" => throw new HttpRequestExceptionStub("upstream 503 key=AIzaSyBATCH39LEAK0123456789abcd"),
            _ => DefaultBody(req.TargetLocale),
        });
        using var rig = new Rig(Sub("tts"), provider);
        await rig.SeedMasterAsync();

        var r = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps).WaitAsync(Wait);

        var ja = r.Find("ja-JP")!;
        Assert.True(ja.HasTtsOverflow);
        Assert.True(ja.TtsExcessChars > 0);
        Assert.Equal(PlutoMultiLanguageChannelService.MaxSpeakingRate, ja.SpeakingRate, 3);
        Assert.Contains(ja.Warnings, w => w.Contains("TTS 1.30x 초과", StringComparison.Ordinal));

        var en = r.Find("en-US")!;
        Assert.False(en.HasTtsOverflow);
        Assert.Empty(en.Warnings);

        // 번역기 예외(es) → 그 큐만 용어집 폴백 · 경고 — 일괄은 계속
        var es = r.Find("es-ES")!;
        Assert.NotEqual(PlutoLocaleBatchStatus.Failed, es.Status);
        Assert.NotEqual(PlutoTranslationStatus.Translated, es.Translation);
        Assert.NotEmpty(es.Warnings);

        // 모든 로캘 사이드카는 그대로 저장(경고는 저장을 막지 않음)
        foreach (var loc in All) Assert.Equal(PlutoLocaleBatchStatus.SidecarSaved, r.Find(loc)!.Status);
        Assert.Contains("JA 🎙️+" + ja.TtsExcessChars.ToString(System.Globalization.CultureInfo.InvariantCulture) + "자", r.SummaryKorean, StringComparison.Ordinal);
        Assert.DoesNotContain("AIzaSyBATCH39", r.SummaryKorean, StringComparison.Ordinal);
        foreach (var it in r.Items) foreach (var w in it.Warnings) Assert.DoesNotContain("AIzaSyBATCH39", w, StringComparison.Ordinal);
    }

    private sealed class HttpRequestExceptionStub : Exception
    {
        public HttpRequestExceptionStub(string m) : base(m) { }
    }

    // ── 3. Stage-9 자가진단 — 9 단계 전부 PASS · 라이브 무접촉 ──

    [Fact]
    public async Task FlywheelSelfTest_PassesAllNineStages_IncludingStartupPreSyncAndBlackBoxDiagnostic_WithoutTouchingLiveState()
    {
        Assert.Equal(9, PlutoFlywheelSelfTestService.StageCount);
        Assert.Equal(9, PlutoFlywheelSelfTestService.StageNames.Length);
        var live = Sub("live");
        var liveFile = Path.Combine(live, PlutoRenderCatalogService.FileName);
        File.WriteAllText(liveFile, "{\"live\":true}");
        var stamp = File.GetLastWriteTimeUtc(liveFile);
        var parent = Sub("selftest");

        var rep = await new PlutoFlywheelSelfTestService(parent, new[] { live }).RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var why = string.Join(" / ", Array.ConvertAll(new List<PlutoFlywheelStageResult>(rep.Stages).ToArray(), s => s.Index + ":" + s.StatusLabel + ":" + s.Detail));
        Assert.True(rep.AllPassed, why);
        var s9 = rep.Stages[8];
        Assert.Equal(9, s9.Index);
        Assert.True(s9.Passed, why);
        Assert.Contains("선조정 1회(정리 1 · 잔류 1)", s9.Detail, StringComparison.Ordinal);
        Assert.Contains("진단 6섹션", s9.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(parent, s9.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(rep.SandboxCleaned);
        Assert.True(rep.LiveStateUntouched);
        Assert.Equal("{\"live\":true}", File.ReadAllText(liveFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(liveFile));
        Assert.Empty(Directory.GetDirectories(parent, PlutoFlywheelSelfTestService.SandboxPrefix + "*"));
    }

    // ── 4. 자체 추가 ① — 사이드카 묶음 사전 검증 실패 → 전무(이전 묶음 그대로 · 혼합 세대 묶음 없음) ──

    [Fact]
    public async Task CoArchitect_SidecarBundle_PreflightFailureOnOneLocale_WritesNothing_AndKeepsPreviousBundleCoherent()
    {
        using var rig = new Rig(Sub("preflight"));
        await rig.SeedMasterAsync();
        var first = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps).WaitAsync(Wait);
        Assert.True(first.Bundle!.Committed);
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var loc in new[] { "ko-KR", "en-US", "ja-JP" }) previous[loc] = File.ReadAllText(rig.Sidecar(loc), Encoding.UTF8);

        // ES 사이드카 자리에 같은 이름 폴더가 심어짐 → 단건 가드와 같은 사유로 거부
        File.Delete(rig.Sidecar("es-ES"));
        Directory.CreateDirectory(rig.Sidecar("es-ES"));
        var r = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request() with { Theme = "숲길 산책 2" }, rig.Deps).WaitAsync(Wait);

        Assert.False(r.Bundle!.Committed);
        Assert.False(r.Bundle.RolledBack);
        foreach (var loc in All) Assert.Equal(PlutoLocaleBatchStatus.SidecarNotCommitted, r.Find(loc)!.Status);
        Assert.Contains("폴더", r.Find("es-ES")!.Error, StringComparison.Ordinal);
        Assert.Contains("묶음 미저장", r.Find("en-US")!.Error, StringComparison.Ordinal);
        foreach (var kv in previous) Assert.Equal(kv.Value, File.ReadAllText(rig.Sidecar(kv.Key), Encoding.UTF8));   // 한 글자도 안 바뀜
        Assert.True(Directory.Exists(rig.Sidecar("es-ES")));
        AssertNoStagingLeftovers(Path.GetDirectoryName(rig.Video)!);
        Assert.Contains("⏸", r.SummaryKorean, StringComparison.Ordinal);
    }

    // ── 5. 자체 추가 ① — 커밋 도중 실패(잠긴 JA 사이드카) → 이미 교체한 KO/EN 을 이전 내용으로 복원 ──

    [Fact]
    public async Task CoArchitect_SidecarBundle_CommitFailureMidway_RollsBackAlreadyReplacedLocales_LeavingNoTempOrBackupFiles()
    {
        using var rig = new Rig(Sub("rollback"));
        await rig.SeedMasterAsync();
        Assert.True((await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps).WaitAsync(Wait)).Bundle!.Committed);
        var previous = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var loc in All) previous[loc] = File.ReadAllText(rig.Sidecar(loc), Encoding.UTF8);

        PlutoMultiLocaleBatchResult r;
        using (new FileStream(rig.Sidecar("ja-JP"), FileMode.Open, FileAccess.Read, FileShare.None))   // 업로더가 JA 파일을 열고 있음
        {
            r = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request() with { CatalogTitle = "숲길 산책 브이로그 새 제목", Theme = "새 주제" }, rig.Deps).WaitAsync(Wait);
        }

        Assert.False(r.Bundle!.Committed);
        Assert.True(r.Bundle.RolledBack);
        foreach (var loc in All) Assert.Equal(PlutoLocaleBatchStatus.SidecarNotCommitted, r.Find(loc)!.Status);
        foreach (var kv in previous) Assert.Equal(kv.Value, File.ReadAllText(rig.Sidecar(kv.Key), Encoding.UTF8));   // KO · EN 복원 · JA · ES 불변
        AssertNoStagingLeftovers(Path.GetDirectoryName(rig.Video)!);
        Assert.Contains("되돌림", r.SummaryKorean, StringComparison.Ordinal);
    }

    // ── 6. 자체 추가 ② — 게시(연결)된 로캘 동결: 패키지 · 사이드카 재생성 금지(중복 업로드 · 오연결 차단) ──

    [Fact]
    public async Task CoArchitect_PublishedLocale_IsFrozen_NoRegeneratedSidecarOrTitle_WhileUnpublishedSiblingsRefresh()
    {
        using var rig = new Rig(Sub("frozen"));
        await rig.SeedMasterAsync();
        Assert.True((await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps).WaitAsync(Wait)).Bundle!.Committed);
        var jaId = rig.ClipId + "::ja-JP";
        Assert.Equal(PlutoYouTubeBindResult.Bound, await rig.Catalog.TryBindYouTubeVideoIdAsync(jaId, "ytJA39PUB00"));
        var jaTitle = (await rig.Catalog.FindByVideoIdOrTitleAsync(jaId))!.Title;
        var jaSidecar = File.ReadAllText(rig.Sidecar("ja-JP"), Encoding.UTF8);
        var jaStamp = File.GetLastWriteTimeUtc(rig.Sidecar("ja-JP"));
        var enBefore = File.ReadAllText(rig.Sidecar("en-US"), Encoding.UTF8);

        var r = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request() with { CatalogTitle = "완전히 다른 새 제목", Theme = "다른 주제" }, rig.Deps).WaitAsync(Wait);

        var ja = r.Find("ja-JP")!;
        Assert.Equal(PlutoLocaleBatchStatus.AlreadyPublished, ja.Status);
        Assert.Null(ja.Pack);
        Assert.Null(ja.SidecarPath);
        Assert.Equal("ytJA39PUB00", ja.YouTubeVideoId);
        Assert.Equal(jaSidecar, File.ReadAllText(rig.Sidecar("ja-JP"), Encoding.UTF8));
        Assert.Equal(jaStamp, File.GetLastWriteTimeUtc(rig.Sidecar("ja-JP")));
        var jaRec = (await rig.Catalog.FindByVideoIdOrTitleAsync(jaId))!;
        Assert.Equal(jaTitle, jaRec.Title);                    // 🧊 게시 후 제목 동결
        Assert.Equal("ytJA39PUB00", jaRec.YouTubeVideoId);       // 연결 불변

        Assert.Equal(PlutoLocaleBatchStatus.SidecarSaved, r.Find("en-US")!.Status);
        Assert.NotEqual(enBefore, File.ReadAllText(rig.Sidecar("en-US"), Encoding.UTF8));   // 미게시 형제는 새 제목으로 갱신
        Assert.True(r.Bundle!.Committed);
        Assert.Equal(3, r.Bundle.Results.Count);
    }

    // ── 7. 자체 추가 ③ — 같은 마스터 동시 실행(연타 · 두 창) 단일 비행: 두 번째 호출은 Busy · 무기록 ──

    [Fact]
    public async Task CoArchitect_ConcurrentBatchOnSameMaster_SecondCallReturnsBusyWithoutSideEffects_AndFirstCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PlutoTranslationProvider gated = async (req, ct) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(ct);
            return (string.Join(" ", req.Placeholders) + " " + DefaultBody(req.TargetLocale)).Trim();
        };
        using var rig = new Rig(Sub("flight"), gated);
        await rig.SeedMasterAsync();

        var first = rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps);
        await entered.Task.WaitAsync(Wait);
        Assert.True(PlutoMultiLocaleBatchBundle.IsRunning(rig.ClipId));

        // 변형 ID 로 불러도 같은 마스터 → Busy
        var second = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request() with { BaseClipId = rig.ClipId + "::ja-JP" }, rig.Deps).WaitAsync(Wait);
        Assert.True(second.Busy);
        Assert.Empty(second.Items);
        Assert.Null(second.Bundle);
        Assert.Contains("이미 진행 중", second.SummaryKorean, StringComparison.Ordinal);
        Assert.False(File.Exists(rig.Sidecar("ko-KR")));

        gate.SetResult();
        var r = await first.WaitAsync(Wait);
        Assert.False(r.Busy);
        Assert.True(r.Bundle!.Committed);
        Assert.False(PlutoMultiLocaleBatchBundle.IsRunning(rig.ClipId));
        foreach (var loc in All) Assert.True(File.Exists(rig.Sidecar(loc)), loc);

        // 완료 후엔 다시 실행 가능(잠금 누수 없음)
        var third = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps).WaitAsync(Wait);
        Assert.False(third.Busy);
    }

    // ── 8. 부분 실패 보고 — 마스터 없음 · 영상 경로 없음 · 로캘 정규화/중복 제거 ──

    [Fact]
    public async Task Batch_MissingMasterFailsPerLocale_NoVideoPathSkipsSidecars_AndLocalesAreNormalizedAndDeduplicated()
    {
        using var rig = new Rig(Sub("partial"));
        var noMaster = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(), rig.Deps).WaitAsync(Wait);
        Assert.Equal(All.Length, noMaster.Count(PlutoLocaleBatchStatus.Failed));
        Assert.Null(noMaster.Bundle);
        Assert.Contains("마스터", noMaster.Items[0].Error, StringComparison.Ordinal);

        await rig.SeedMasterAsync();
        var r = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Request(sidecars: false) with { Locales = new[] { "JA_jp", "ja-JP", "en", "xx-YY" } }, rig.Deps).WaitAsync(Wait);
        Assert.Equal(new[] { "ja-JP", "en-US" }, new[] { r.Items[0].Locale, r.Items[1].Locale });
        Assert.Equal(2, r.Items.Count);
        Assert.Equal(2, r.Count(PlutoLocaleBatchStatus.Packaged));
        Assert.Null(r.Bundle);
        Assert.False(File.Exists(rig.Sidecar("ja-JP")));
    }
}
