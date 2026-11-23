using System;
using System.Collections.Concurrent;
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
/// 🎬 Step 40 — 원클릭 전 언어 일괄 렌더/패키지({base}_{locale}.mp4 + {base}_{locale}_publish_pack.txt 짝 커밋)
/// · 게시 로캘/TTS 초과 게이트 · 카탈로그 출력 경로 결선 · 자체 추가 안전장치(스테이징 검증 커밋 · 짝 롤백 · A/V 길이 가드 · 게시 재확인 · 디스크 점검 · 공용 단일 비행).
/// 전부 임시 폴더 · 결정론 페이크 렌더러(FFmpeg 0 · 네트워크 0 · 환경 변수 무변경) — 병렬 컬렉션 안전 · 클립 ID 는 테스트마다 GUID.
/// </summary>
public sealed class PlutoMultiLocaleBatchRenderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_render40_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private static readonly string[] All = { "ko-KR", "en-US", "ja-JP", "es-ES" };
    private static readonly byte[] Ftyp = { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p' };

    public PlutoMultiLocaleBatchRenderTests() => Directory.CreateDirectory(_dir);

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

    private static IReadOnlyList<SubtitleCue> KoreanScript() => new[]
    {
        SubtitleCue.Precise(0.0, 2.5, "민준이 숲길을 걷는다"),
        SubtitleCue.Precise(2.5, 5.0, "마지막에 비밀이 밝혀진다"),
    };

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

    /// <summary>결정론 페이크 렌더러 — 스테이징에 MP4 서명 + 로캘 + 첫 자막 기록 · 작업 기록 · 로캘별 훅.</summary>
    private sealed class FakeRenderer
    {
        public readonly ConcurrentQueue<PlutoLocaleRenderJob> Jobs = new();
        public Func<PlutoLocaleRenderJob, CancellationToken, Task<PlutoLocaleRenderOutcome?>>? Hook;
        public double AudioSeconds = 4.9;

        public async Task<PlutoLocaleRenderOutcome> RenderAsync(PlutoLocaleRenderJob job, CancellationToken ct)
        {
            Jobs.Enqueue(job);
            if (Hook is { } h && await h(job, ct).ConfigureAwait(false) is { } overridden) return overridden;
            var body = Encoding.UTF8.GetBytes(job.Locale + "|" + (job.Cues.Count > 0 ? job.Cues[0].Text : ""));
            var bytes = new byte[Ftyp.Length + body.Length];
            Ftyp.CopyTo(bytes, 0);
            body.CopyTo(bytes, Ftyp.Length);
            await File.WriteAllBytesAsync(job.StagingPath, bytes, ct).ConfigureAwait(false);
            return new PlutoLocaleRenderOutcome(true, "ok") { AudioDurationSeconds = AudioSeconds, VideoDurationSeconds = 5.0 };
        }

        public bool Rendered(string locale) { foreach (var j in Jobs) if (j.Locale == locale) return true; return false; }
    }

    private sealed class Rig : IDisposable
    {
        public readonly PlutoMultiLanguageChannelService Channels = new();
        public readonly PlutoRenderCatalogService Catalog;
        public readonly PlutoSubtitleLocalizationService L10n;
        public readonly PlutoYouTubePublishPackService Packs;
        public readonly PlutoAutonomousActivityFeedService Feed;
        public readonly PlutoMultiLocaleBatchRenderService Service;
        public readonly FakeRenderer Renderer = new();
        public readonly string ClipId = "clip40_" + Guid.NewGuid().ToString("N")[..10];
        public readonly string Dir;
        public readonly string Video;
        public long FreeBytes = long.MaxValue;

        public Rig(string dir, PlutoTranslationProvider? provider = null, string masterName = "forest_walk.mp4")
        {
            Dir = dir;
            Catalog = new PlutoRenderCatalogService(Path.Combine(dir, PlutoRenderCatalogService.FileName));
            L10n = new PlutoSubtitleLocalizationService(Channels, provider ?? Provider(), null, providerUsesNetwork: false);
            Packs = new PlutoYouTubePublishPackService(catalog: Catalog, clock: static () => T0) { Channels = Channels };
            Feed = new PlutoAutonomousActivityFeedService(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "feed")).FullName, PlutoAutonomousActivityFeedService.FeedFileName));   // 피드 비동기 영속(.tmp)은 산출물 폴더 밖
            Service = new PlutoMultiLocaleBatchRenderService(Channels, Renderer.RenderAsync, Feed, static () => T0, _ => FreeBytes);
            Video = Path.Combine(dir, masterName);
            File.WriteAllBytes(Video, Ftyp);
        }

        public PlutoMultiLocaleBatchServices Deps => new(L10n, Catalog, Packs);

        public Task SeedMasterAsync()
            => Catalog.RecordRenderedClipAsync(new PlutoRenderedClipRecord(ClipId, "숲길 산책 브이로그", "몽타주", "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby",
                new[] { "autumn" }, Video, T0, false));

        public PlutoMultiLocaleBatchRequest Batch() => new(ClipId, KoreanScript())
        {
            ContextKey = "montage", Theme = "숲길 산책", CatalogTitle = "숲길 산책 브이로그", HookStyle = "pov-walk", StyleTags = new[] { "autumn" }, VideoPath = Video,
        };

        public PlutoMultiLocaleRenderRequest Request(PlutoTtsOverflowRenderPolicy policy = PlutoTtsOverflowRenderPolicy.Skip) => new(Batch()) { TtsOverflowPolicy = policy };

        public Task<PlutoMultiLocaleRenderResult> RunAsync(PlutoMultiLocaleRenderRequest? req = null, CancellationToken ct = default)
            => Service.RenderAllLocalesAsync(req ?? Request(), Deps, null, ct).WaitAsync(Wait);

        public string Mp4(string locale) => Path.Combine(Dir, "forest_walk_" + locale + ".mp4");
        public string Pack(string locale) => Path.Combine(Dir, "forest_walk_" + locale + PlutoYouTubePublishPackService.SidecarSuffix);

        public async Task<PlutoRenderedClipRecord?> RecordAsync(string id)
        {
            foreach (var c in await Catalog.GetRecentClipsAsync(PlutoRenderCatalogService.Capacity)) if (string.Equals(c.VideoId, id, StringComparison.OrdinalIgnoreCase)) return c;
            return null;
        }

        public void Dispose() { Feed.Dispose(); Catalog.Dispose(); }
    }

    private static void AssertNoLeftovers(string dir)
    {
        foreach (var pattern in new[] { "*" + PlutoMultiLocaleBatchRenderService.StagingMarker + ".mp4", "*.tmp", "*.bak" })
        {
            var left = Directory.GetFiles(dir, pattern);
            Assert.True(left.Length == 0, pattern + " 잔류: " + string.Join(", ", Array.ConvertAll(left, Path.GetFileName)));
        }
    }

    private static void Seed(string path, string text) => File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));

    // ── 1. 4개 로캘 렌더 · 사이드카 짝 · 카탈로그 출력 경로 결선 · 로캘 자막/TTS 투영 ──

    [Fact]
    public async Task RenderAllLocales_ProducesMp4AndSidecarPerLocale_BindsVariantOutputPaths_AndProjectsLocalizedSubtitlesAndVoices()
    {
        using var rig = new Rig(Sub("all4"));
        await rig.SeedMasterAsync();

        var r = await rig.RunAsync();

        Assert.False(r.Busy);
        Assert.False(r.Cancelled);
        Assert.Equal(All.Length, r.Items.Count);
        var voices = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < All.Length; i++)
        {
            var it = r.Items[i];
            Assert.Equal(All[i], it.Locale);
            Assert.True(it.Status == PlutoLocaleRenderStatus.Rendered, it.Locale + ": " + it.Status + " " + it.Error);
            Assert.Equal(rig.Mp4(it.Locale), it.VideoPath);
            Assert.Equal(rig.Pack(it.Locale), it.SidecarPath);
            Assert.True(File.Exists(it.VideoPath));
            var sidecar = File.ReadAllText(it.SidecarPath!, Encoding.UTF8);
            Assert.Contains("forest_walk_" + it.Locale + ".mp4", sidecar, StringComparison.Ordinal);   // 사이드카는 로캘 MP4 를 가리킨다
            Assert.Contains(it.Locale, sidecar, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(rig.Dir, "forest_walk_" + it.Locale + "_" + it.Locale + PlutoYouTubePublishPackService.SidecarSuffix)));   // 이중 접미사 없음
            voices.Add(it.VoiceId!);
        }
        Assert.Equal(All.Length, voices.Count);   // 로캘별 TTS 음성
        AssertNoLeftovers(rig.Dir);

        // 로캘 자막 투영(마스터 타임라인 그대로)
        Assert.Contains("walks the trail", File.ReadAllText(rig.Mp4("en-US"), Encoding.UTF8), StringComparison.Ordinal);
        Assert.Contains("민준", File.ReadAllText(rig.Mp4("ko-KR"), Encoding.UTF8), StringComparison.Ordinal);
        foreach (var j in rig.Renderer.Jobs)
        {
            Assert.Equal(5.0, j.TimelineSeconds, 3);
            Assert.Equal(5.0, j.Cues[^1].EffectiveEndSec, 3);
            Assert.NotEqual(j.FinalPath, j.StagingPath);
            Assert.Equal(Path.GetDirectoryName(j.FinalPath), Path.GetDirectoryName(j.StagingPath));
        }

        // 카탈로그 — EN/JA/ES 변형 출력 경로 = 로캘 MP4 · 마스터(KO) 경로는 그대로
        foreach (var loc in new[] { "en-US", "ja-JP", "es-ES" })
        {
            Assert.True(r.Find(loc)!.CatalogBound, loc);
            Assert.Equal(rig.Mp4(loc), (await rig.RecordAsync(rig.ClipId + PlutoMultiLanguageChannelService.VariantSeparator + loc))!.OutputPath);
        }
        Assert.Equal(rig.Video, (await rig.RecordAsync(rig.ClipId))!.OutputPath);
        Assert.False(r.Find("ko-KR")!.CatalogBound);

        // 레거시 호환 · 병합: 새 필드 없이 같은 레코드 → 합집합 병합은 출력 경로를 잃지 않는다
        var reloaded = new PlutoRenderCatalogService(Path.Combine(rig.Dir, PlutoRenderCatalogService.FileName));
        using (reloaded)
            Assert.Equal(rig.Mp4("ja-JP"), (await reloaded.FindByVideoIdOrTitleAsync(rig.ClipId + "::ja-JP"))!.OutputPath);

        Assert.Contains("🎞️ 4개", r.SummaryKorean, StringComparison.Ordinal);
        Assert.Equal(PlutoActivityKind.LocaleRender, rig.Feed.Latest!.Kind);
        Assert.False(rig.Service.IsRunning(rig.ClipId));
    }

    // ── 2. 게시된 로캘 건너뜀(MP4 · 사이드카 바이트 불변) · 형제는 정상 ──

    [Fact]
    public async Task AlreadyPublishedLocale_IsSkippedWithoutTouchingItsMp4OrSidecar_WhileSiblingsRender()
    {
        using var rig = new Rig(Sub("pub"));
        await rig.SeedMasterAsync();
        Assert.NotNull(await rig.Catalog.BranchLocaleVariantAsync(rig.ClipId, "en-US"));
        Assert.Equal(PlutoYouTubeBindResult.Bound, await rig.Catalog.TryBindYouTubeVideoIdAsync(rig.ClipId + "::en-US", "ytEN40LINKD"));
        Seed(rig.Mp4("en-US"), "published-en-video");
        Seed(rig.Pack("en-US"), "published-en-pack");

        var r = await rig.RunAsync();

        var en = r.Find("en-US")!;
        Assert.Equal(PlutoLocaleRenderStatus.AlreadyPublished, en.Status);
        Assert.Equal("ytEN40LINKD", en.YouTubeVideoId);
        Assert.False(rig.Renderer.Rendered("en-US"));
        Assert.Equal("published-en-video", File.ReadAllText(rig.Mp4("en-US")));
        Assert.Equal("published-en-pack", File.ReadAllText(rig.Pack("en-US")));
        foreach (var loc in new[] { "ko-KR", "ja-JP", "es-ES" })
        {
            Assert.Equal(PlutoLocaleRenderStatus.Rendered, r.Find(loc)!.Status);
            Assert.True(File.Exists(rig.Pack(loc)));
        }
        Assert.Equal("ytEN40LINKD", (await rig.RecordAsync(rig.ClipId + "::en-US"))!.YouTubeVideoId);
        Assert.Contains("EN 🔒게시됨", r.SummaryKorean, StringComparison.Ordinal);
    }

    // ── 3a. 로캘 1개 렌더 실패 격리(이전 MP4 보존) ──

    [Fact]
    public async Task SingleLocaleRenderFailure_IsIsolated_KeepsPreviousMp4_AndSiblingsCommit()
    {
        using var rig = new Rig(Sub("fail"));
        await rig.SeedMasterAsync();
        Seed(rig.Mp4("ja-JP"), "previous-ja-video");
        Seed(rig.Pack("ja-JP"), "previous-ja-pack");
        rig.Renderer.Hook = (job, ct) => job.Locale == "ja-JP"
            ? throw new InvalidOperationException("ffmpeg exited 1 key=AIzaSySTEP40LEAK0123456789abcdef")
            : Task.FromResult<PlutoLocaleRenderOutcome?>(null);

        var r = await rig.RunAsync();

        var ja = r.Find("ja-JP")!;
        Assert.Equal(PlutoLocaleRenderStatus.RenderFailed, ja.Status);
        Assert.DoesNotContain("AIzaSySTEP40", ja.Error!, StringComparison.Ordinal);
        Assert.Equal("previous-ja-video", File.ReadAllText(rig.Mp4("ja-JP")));
        Assert.Equal("previous-ja-pack", File.ReadAllText(rig.Pack("ja-JP")));
        Assert.NotEqual(rig.Mp4("ja-JP"), (await rig.RecordAsync(rig.ClipId + "::ja-JP"))!.OutputPath);   // 실패 로캘은 결선 안 함
        foreach (var loc in new[] { "ko-KR", "en-US", "es-ES" }) Assert.Equal(PlutoLocaleRenderStatus.Rendered, r.Find(loc)!.Status);
        Assert.Equal(rig.Mp4("es-ES"), (await rig.RecordAsync(rig.ClipId + "::es-ES"))!.OutputPath);
        AssertNoLeftovers(rig.Dir);
    }

    // ── 3b. 취소 — 커밋된 형제 MP4 · 카탈로그 보존 · 진행 중 로캘 스테이징 회수 ──

    [Fact]
    public async Task Cancellation_MidBatch_PreservesCommittedSiblings_AndLeavesNoStagingOrHalfWrittenMp4()
    {
        using var rig = new Rig(Sub("cancel"));
        await rig.SeedMasterAsync();
        using var cts = new CancellationTokenSource();
        rig.Renderer.Hook = async (job, ct) =>
        {
            if (job.Locale != "ja-JP") return null;
            await File.WriteAllBytesAsync(job.StagingPath, Ftyp, CancellationToken.None);   // 반쯤 쓴 산출물
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return null;
        };

        var r = await rig.RunAsync(rig.Request(), cts.Token);

        Assert.True(r.Cancelled);
        Assert.Equal(PlutoLocaleRenderStatus.Rendered, r.Find("ko-KR")!.Status);
        Assert.Equal(PlutoLocaleRenderStatus.Rendered, r.Find("en-US")!.Status);
        Assert.Equal(PlutoLocaleRenderStatus.Cancelled, r.Find("ja-JP")!.Status);
        Assert.Equal(PlutoLocaleRenderStatus.Cancelled, r.Find("es-ES")!.Status);
        Assert.False(rig.Renderer.Rendered("es-ES"));
        Assert.True(File.Exists(rig.Mp4("en-US")) && File.Exists(rig.Pack("en-US")));
        Assert.False(File.Exists(rig.Mp4("ja-JP")));
        Assert.False(File.Exists(rig.Pack("ja-JP")));
        Assert.Equal(rig.Mp4("en-US"), (await rig.RecordAsync(rig.ClipId + "::en-US"))!.OutputPath);
        AssertNoLeftovers(rig.Dir);
        Assert.False(rig.Service.IsRunning(rig.ClipId));
        Assert.Contains("취소", r.SummaryKorean, StringComparison.Ordinal);
    }

    // ── 4. 🛡️ 자체 추가 ① — TTS 1.30x 초과 게이트 + 낭독 오디오 길이 가드 ──

    [Fact]
    public async Task TtsOverflow_SkipPolicyBlocksOnlyThatLocale_AndFlaggedPolicyRejectsAudioThatOverrunsTimeline()
    {
        var provider = Provider(req => req.TargetLocale == "ja-JP" ? new string('森', 180) : DefaultBody(req.TargetLocale));
        using var rig = new Rig(Sub("tts"), provider);
        await rig.SeedMasterAsync();
        Seed(rig.Mp4("ja-JP"), "previous-ja-video");

        // Skip(기본) — ja 만 보류 · 렌더러 미호출 · 기존 파일 그대로 · 형제 정상
        var r = await rig.RunAsync();
        var ja = r.Find("ja-JP")!;
        Assert.Equal(PlutoLocaleRenderStatus.TtsOverflowBlocked, ja.Status);
        Assert.True(ja.TtsExcessChars > 0);
        Assert.Equal(PlutoMultiLanguageChannelService.MaxSpeakingRate, ja.SpeakingRate, 3);
        Assert.False(rig.Renderer.Rendered("ja-JP"));
        Assert.Equal("previous-ja-video", File.ReadAllText(rig.Mp4("ja-JP")));
        Assert.False(File.Exists(rig.Pack("ja-JP")));
        foreach (var loc in new[] { "ko-KR", "en-US", "es-ES" }) Assert.Equal(PlutoLocaleRenderStatus.Rendered, r.Find(loc)!.Status);
        Assert.Contains("JA 🎙️보류+", r.SummaryKorean, StringComparison.Ordinal);

        // RenderFlagged — 낭독 오디오가 타임라인(5 s) + 허용 오차를 넘으면 커밋 거부(A/V 싱크 보호)
        rig.Renderer.AudioSeconds = 6.4;
        var flagged = await rig.RunAsync(rig.Request(PlutoTtsOverflowRenderPolicy.RenderFlagged));
        var jaF = flagged.Find("ja-JP")!;
        Assert.Equal(PlutoLocaleRenderStatus.OutputRejected, jaF.Status);
        Assert.Contains("A/V", jaF.Error!, StringComparison.Ordinal);
        Assert.Equal("previous-ja-video", File.ReadAllText(rig.Mp4("ja-JP")));
        AssertNoLeftovers(rig.Dir);

        // 같은 정책 · 오디오가 타임라인 안 → 경고와 함께 커밋
        rig.Renderer.AudioSeconds = 5.1;
        var ok = await rig.RunAsync(rig.Request(PlutoTtsOverflowRenderPolicy.RenderFlagged));
        Assert.Equal(PlutoLocaleRenderStatus.Rendered, ok.Find("ja-JP")!.Status);
        Assert.Contains(ok.Find("ja-JP")!.Warnings, w => w.Contains("길이 동등성", StringComparison.Ordinal));
        Assert.Contains("ja-JP|", File.ReadAllText(rig.Mp4("ja-JP"), Encoding.UTF8), StringComparison.Ordinal);
    }

    // ── 5. 🛡️ 자체 추가 ② — 스테이징 검증 커밋 + 영상/사이드카 짝 롤백 ──

    [Fact]
    public async Task CorruptStagedOutput_IsRejected_AndSidecarCommitFailure_RestoresPreviousMp4ByteForByte()
    {
        using var rig = new Rig(Sub("atomic"));
        await rig.SeedMasterAsync();
        Seed(rig.Mp4("en-US"), "previous-en-video");
        Seed(rig.Pack("en-US"), "previous-en-pack");
        Seed(rig.Mp4("es-ES"), "previous-es-video");
        Seed(rig.Pack("es-ES"), "previous-es-pack");

        // (a) 렌더러가 Ok 를 보고했지만 산출물이 MP4 가 아님(잘린/빈 파일) → 커밋 거부
        rig.Renderer.Hook = async (job, ct) =>
        {
            if (job.Locale != "en-US") return null;
            await File.WriteAllBytesAsync(job.StagingPath, Encoding.UTF8.GetBytes("truncated!"), ct);
            return new PlutoLocaleRenderOutcome(true);
        };
        // (b) es 사이드카를 다른 프로세스가 독점 → 사이드카 커밋 실패 → 새 MP4 회수 · 이전 MP4 복원
        PlutoMultiLocaleRenderResult r;
        using (new FileStream(rig.Pack("es-ES"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            r = await rig.RunAsync();

        var en = r.Find("en-US")!;
        Assert.Equal(PlutoLocaleRenderStatus.OutputRejected, en.Status);
        Assert.Contains("ftyp", en.Error!, StringComparison.Ordinal);
        Assert.Equal("previous-en-video", File.ReadAllText(rig.Mp4("en-US")));
        Assert.Equal("previous-en-pack", File.ReadAllText(rig.Pack("en-US")));

        var es = r.Find("es-ES")!;
        Assert.Equal(PlutoLocaleRenderStatus.RenderFailed, es.Status);
        Assert.Contains("이전 영상 복원", es.Error!, StringComparison.Ordinal);
        Assert.Equal("previous-es-video", File.ReadAllText(rig.Mp4("es-ES")));
        Assert.Equal("previous-es-pack", File.ReadAllText(rig.Pack("es-ES")));
        Assert.NotEqual(rig.Mp4("es-ES"), (await rig.RecordAsync(rig.ClipId + "::es-ES"))!.OutputPath);

        Assert.Equal(PlutoLocaleRenderStatus.Rendered, r.Find("ja-JP")!.Status);
        AssertNoLeftovers(rig.Dir);

        // 순수 검증기 단위 확인
        var good = Path.Combine(rig.Dir, "probe_good.mp4");
        File.WriteAllBytes(good, Ftyp);
        Assert.Null(PlutoMultiLocaleBatchRenderService.ValidateStaged(good));
        Assert.NotNull(PlutoMultiLocaleBatchRenderService.ValidateStaged(Path.Combine(rig.Dir, "missing.mp4")));
    }

    // ── 6. 🛡️ 자체 추가 ③ — 렌더 도중 게시 연결(TOCTOU) 재확인 ──

    [Fact]
    public async Task LocalePublishedDuringRender_IsNotOverwritten_AtCommitTime()
    {
        using var rig = new Rig(Sub("race"));
        await rig.SeedMasterAsync();
        Seed(rig.Mp4("es-ES"), "uploaded-es-video");
        rig.Renderer.Hook = async (job, ct) =>
        {
            if (job.Locale == "es-ES")   // 자동 폴러 · 다른 PC 가 렌더 도중 연결
                Assert.Equal(PlutoYouTubeBindResult.Bound, await rig.Catalog.TryBindYouTubeVideoIdAsync(rig.ClipId + "::es-ES", "ytES40RACE0", ct));
            return null;
        };

        var r = await rig.RunAsync();

        var es = r.Find("es-ES")!;
        Assert.Equal(PlutoLocaleRenderStatus.AlreadyPublished, es.Status);
        Assert.Equal("ytES40RACE0", es.YouTubeVideoId);
        Assert.Equal("uploaded-es-video", File.ReadAllText(rig.Mp4("es-ES")));
        Assert.False(File.Exists(rig.Pack("es-ES")));
        Assert.Equal(PlutoLocaleRenderStatus.Rendered, r.Find("en-US")!.Status);
        AssertNoLeftovers(rig.Dir);
    }

    // ── 7. 🛡️ 자체 추가 ④ — 디스크 여유 공간 사전 점검 · 마스터 경로 충돌 가드 ──

    [Fact]
    public async Task InsufficientDisk_StartsNoRender_AndLocaleSuffixedMasterIsNeverOverwritten()
    {
        using var rig = new Rig(Sub("disk"));
        await rig.SeedMasterAsync();
        rig.FreeBytes = 1024;

        var r = await rig.RunAsync();

        Assert.Empty(rig.Renderer.Jobs);
        foreach (var loc in All) Assert.Equal(PlutoLocaleRenderStatus.InsufficientDisk, r.Find(loc)!.Status);
        Assert.Contains("💽부족", r.SummaryKorean, StringComparison.Ordinal);

        // 마스터가 이미 로캘 접미사(trip_en-US.mp4) → 줄기 정규화(누적 접미사 금지) · en 출력 = 마스터 → 덮어쓰기 거부
        using var rig2 = new Rig(Sub("stem"), masterName: "trip_en-US.mp4");
        await rig2.SeedMasterAsync();
        var r2 = await rig2.RunAsync();
        Assert.Equal(PlutoLocaleRenderStatus.PreflightFailed, r2.Find("en-US")!.Status);
        Assert.Equal(Ftyp, File.ReadAllBytes(rig2.Video));
        Assert.Equal(Path.Combine(rig2.Dir, "trip_ja-JP.mp4"), r2.Find("ja-JP")!.VideoPath);
        Assert.False(File.Exists(Path.Combine(rig2.Dir, "trip_en-US_ja-JP.mp4")));
    }

    // ── 8. 🛡️ 자체 추가 ⑤ — 일괄 패키지(Step 39)와 단일 비행 슬롯 공유 ──

    [Fact]
    public async Task RenderBatch_SharesSingleFlightWithPackageBatch_SoSidecarsAreNeverCrossWritten_AndLockIsReleased()
    {
        using var rig = new Rig(Sub("flight"));
        await rig.SeedMasterAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Renderer.Hook = async (job, ct) =>
        {
            if (job.Locale == "ko-KR") { entered.TrySetResult(); await release.Task.WaitAsync(Wait, ct); }
            return null;
        };

        var first = rig.Service.RenderAllLocalesAsync(rig.Request(), rig.Deps, null);
        await entered.Task.WaitAsync(Wait);
        Assert.True(rig.Service.IsRunning(rig.ClipId));

        var pkg = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Batch(), rig.Deps).WaitAsync(Wait);
        Assert.True(pkg.Busy);
        var second = await rig.Service.RenderAllLocalesAsync(rig.Request(), rig.Deps, null).WaitAsync(Wait);
        Assert.True(second.Busy);
        Assert.Empty(second.Items);
        Assert.False(File.Exists(Path.Combine(rig.Dir, "forest_walk_en-US" + PlutoYouTubePublishPackService.SidecarSuffix)));

        release.TrySetResult();
        var done = await first.WaitAsync(Wait);
        Assert.Equal(All.Length, done.Count(PlutoLocaleRenderStatus.Rendered));
        Assert.False(rig.Service.IsRunning(rig.ClipId));

        var again = await rig.Channels.BranchAndExportAllLocalesAsync(rig.Batch() with { VideoPath = null }, rig.Deps).WaitAsync(Wait);   // 잠금 누수 없음
        Assert.False(again.Busy);
    }

    // ── Step 41 · Stage 10 — 샌드박스 자가진단 전 언어 일괄 렌더 · 사이드카 짝 출력 ──

    private static string Why(PlutoFlywheelSelfTestReport rep)
        => string.Join(" / ", Array.ConvertAll(new List<PlutoFlywheelStageResult>(rep.Stages).ToArray(), s => s.Index + ":" + s.StatusLabel + ":" + s.Detail));

    [Fact]
    public async Task FlywheelSelfTest_Stage10_RendersEligibleLocales_FreezesPublishedLocale_AndCoOutputsMp4SidecarPairs_WithoutTouchingLiveState()
    {
        Assert.Equal(10, PlutoFlywheelSelfTestService.StageCount);
        Assert.Equal(10, PlutoFlywheelSelfTestService.StageNames.Length);
        Assert.Contains("일괄 렌더", PlutoFlywheelSelfTestService.StageNames[9], StringComparison.Ordinal);
        Assert.Equal(All, PlutoFlywheelSelfTestService.BatchRenderLocales);
        Assert.Equal("en-US", PlutoFlywheelSelfTestService.BatchRenderPublishedLocale);
        var live = Sub("live10");
        var liveFile = Path.Combine(live, PlutoRenderCatalogService.FileName);
        File.WriteAllText(liveFile, "{\"live\":true}");
        var stamp = File.GetLastWriteTimeUtc(liveFile);
        var parent = Sub("selftest10");

        var rep = await new PlutoFlywheelSelfTestService(parent, new[] { live }).RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.True(rep.AllPassed, Why(rep));
        Assert.Equal(10, rep.Stages.Count);
        var s10 = rep.Stages[9];
        Assert.Equal(10, s10.Index);
        Assert.Equal(PlutoFlywheelSelfTestService.StageNames[9], s10.Name);
        Assert.True(s10.Passed && !s10.Skipped, Why(rep));
        Assert.Contains("렌더 3개(KO·JA·ES)", s10.Detail, StringComparison.Ordinal);
        Assert.Contains("EN 🔒게시 동결", s10.Detail, StringComparison.Ordinal);
        Assert.Contains("짝 3쌍", s10.Detail, StringComparison.Ordinal);
        Assert.Contains("잔여물 0", s10.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(parent, s10.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PASS 10/10", rep.SummaryKorean, StringComparison.Ordinal);
        Assert.True(rep.SandboxCleaned);
        Assert.True(rep.LiveStateUntouched);
        Assert.Equal("{\"live\":true}", File.ReadAllText(liveFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(liveFile));
        Assert.Empty(Directory.GetDirectories(parent, PlutoFlywheelSelfTestService.SandboxPrefix + "*"));
    }

    /// <summary>
    /// 자체 추가 — 일괄 단일 비행 슬롯(<see cref="PlutoMultiLocaleBatchBundle"/>)은 프로세스 전역 정적 맵이다. Stage 10 이 고정 마스터 ID 를 쓰면
    /// 동시에 도는 두 자가진단 인스턴스(설정 화면 + 병렬 테스트 컬렉션)가 서로를 Busy 로 보고 정상 파이프라인을 FAIL 로 보고한다 → 실행마다 고유 ID.
    /// </summary>
    [Fact]
    public async Task CoArchitect_ConcurrentSelfTestInstances_DoNotCollideOnProcessGlobalBatchRenderSlot()
    {
        var live = Sub("live10c");
        var a = new PlutoFlywheelSelfTestService(Sub("selftest10a"), new[] { live });
        var b = new PlutoFlywheelSelfTestService(Sub("selftest10b"), new[] { live });

        var reports = await Task.WhenAll(a.RunAsync(), b.RunAsync()).WaitAsync(TimeSpan.FromSeconds(120));

        foreach (var rep in reports)
        {
            Assert.True(rep.AllPassed, Why(rep));
            Assert.True(rep.Stages[9].Passed, Why(rep));
            Assert.True(rep.SandboxCleaned);
        }
        Assert.NotEqual(a.LastSandboxPath, b.LastSandboxPath);
    }
}
