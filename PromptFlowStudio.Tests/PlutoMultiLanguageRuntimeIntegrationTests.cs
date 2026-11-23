using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 🌐 Step 32 — 다국어 런타임 결선: 로캘 자막 줄바꿈/TTS 적합 · 로캘 패키지 동작 시 변형 자동 보장 · 로캘별 제목 프레이밍 귀속 ·
/// 두뇌/진단/플래너 로캘 분해 · 8단계 자가진단 · 자체 추가 안전장치(축출 보호 · 변형 채점 격리 · 한국어 장식 누출/번역 누락 · 로캘 오귀속).
/// </summary>
public sealed class PlutoMultiLanguageRuntimeIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_mlruntime_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    public PlutoMultiLanguageRuntimeIntegrationTests() => Directory.CreateDirectory(_dir);

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

    private static PlutoRenderedClipRecord Master(string id, string title, string mode = "몽타주", DateTimeOffset? at = null)
        => new(id, title, mode, "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "cat" }, "", at ?? T0, false);

    /// <summary>귀속 확정 · 워터마크 반영 레코드(로캘 변형 ID 면 계보 필드도 채움).</summary>
    private static PlutoRenderedClipRecord Attributed(string id, string yt, string framing, double ctr, long views = 1000, double hook = 50, double completion = 40)
    {
        var v = PlutoMultiLanguageChannelService.ParseVariantClipId(id);
        return Master(id, id) with
        {
            YouTubeVideoId = yt,
            YouTubeWatermark = new PlutoYouTubeIngestWatermark(views, 1, hook, completion, ctr, T0),
            PublishedTitleFraming = framing,
            TitleAttribution = PlutoTitleAttributionStates.Confirmed,
            TargetLocale = v.Locale ?? "ko-KR",
            ParentClipId = v.Locale is null ? null : v.BaseClipId,
        };
    }

    private static string Repeat(char ch, int n) => new(ch, n);

    private static List<string> Lines(IReadOnlyList<SubtitleCue> cues)
    {
        var list = new List<string>();
        foreach (var c in cues) list.AddRange(c.Text.Split('\n'));
        return list;
    }

    // ── 1. 런타임 자막 투영 — 로캘 줄 길이 · 금칙 · 편 토큰 · TTS 1.30x 초과 정확 글자 수 ──

    [Fact]
    public void LocaleFitter_AppliesMaxCharsPerLineAndKinsoku_AndReportsExactTtsExcessAboveCeiling()
    {
        var channels = new PlutoMultiLanguageChannelService();
        var ja = channels.GetProfileOrDefault("ja-JP");
        IReadOnlyList<SubtitleCue> displayed = PlutoSeriesBatchPlanner.DecorateSubtitles(new[]
        {
            SubtitleCue.Precise(0.0, 4.0, "最後まで見ると分かる、秋の森の散歩の本当の秘密。"),
            SubtitleCue.Precise(4.0, 6.0, Repeat('あ', 40)),   // 40 낭독 단위 · 2.0초
            SubtitleCue.Precise(6.0, 8.0, Repeat('い', 26)),   // 정확히 상한(⌊11 × 1.30 × 1.85⌋ = 26)
        }, 1, 3);
        var snapshot = displayed[0].Text;

        var p = PlutoLocaleSubtitleFitter.Project(displayed, channels, "ja-JP", 1, 3);
        Assert.False(p.IsIdentity);
        Assert.Equal("ja-JP", p.Locale);
        Assert.Equal(ja.DefaultTtsVoiceId, p.VoiceId);
        Assert.Equal(snapshot, displayed[0].Text);                       // 입력(편집 원문) 불변
        foreach (var line in Lines(p.Cues))
        {
            Assert.InRange(PlutoMultiLanguageChannelService.TextLength(line), 1, ja.MaxCharsPerLine);
            Assert.False(line[0] is '、' or '。' or '）', "행두 금칙: " + line);
        }
        Assert.StartsWith("第1/3話 · ", p.Cues[0].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("편", p.Cues[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("（次回へ続く）", string.Concat(Lines(p.Cues)), StringComparison.Ordinal);

        // TTS — 40자/1.85초 가용 → 1.30x 상한 초과 · 정확히 14자(40 − 26) 줄이기 · 26자는 초과 없음
        Assert.Equal(14, p.Fits[1].ExcessChars);
        Assert.True(p.Fits[1].Tts.Clamped);
        Assert.Equal(PlutoMultiLanguageChannelService.MaxSpeakingRate, p.Fits[1].Tts.SpeakingRate, 3);
        Assert.Equal(0, p.Fits[2].ExcessChars);
        Assert.Equal(1, p.TtsOverflowCueCount);
        Assert.Equal(14, p.TotalExcessChars);
        Assert.Contains("14자 줄이기", p.WarningKorean, StringComparison.Ordinal);
        Assert.Contains("#2 14자", p.WarningKorean, StringComparison.Ordinal);
        Assert.InRange(p.SpeakingRate, PlutoMultiLanguageChannelService.MinSpeakingRate, PlutoMultiLanguageChannelService.MaxSpeakingRate);

        // 2줄 초과(40자 → 3줄) 큐는 시간 비례 분할 — 첫 시작 · 마지막 끝 정확 보존
        Assert.True(p.Fits[1].ExceedsSafeZone);
        Assert.True(p.Cues.Count > displayed.Count);
        var split = new List<SubtitleCue>();
        foreach (var c in p.Cues) if (c.EffectiveStartSec >= 4.0 - 1e-9 && c.EffectiveEndSec <= 6.0 + 1e-9) split.Add(c);
        Assert.True(split.Count >= 2);
        Assert.Equal(4.0, split[0].EffectiveStartSec, 6);
        Assert.Equal(6.0, split[^1].EffectiveEndSec, 6);
        for (var i = 1; i < split.Count; i++) Assert.Equal(split[i - 1].EffectiveEndSec, split[i].EffectiveStartSec, 6);

        // en-US — 줄당 34자 · 단어 경계
        var en = PlutoLocaleSubtitleFitter.Project(new[] { SubtitleCue.Precise(0, 6, "Wait until the end to see why this quiet autumn forest walk changed everything") }, channels, "en-US");
        foreach (var line in Lines(en.Cues)) Assert.InRange(line.Length, 1, 34);
        Assert.Equal("Wait until the end to see why this quiet autumn forest walk changed everything", string.Join(" ", Lines(en.Cues)));

        // 낭독 계획 → 로캘 음성 + 적합 속도 → Edge 운율
        var plan = PlutoLocaleSubtitleFitter.PlanNarration(channels, "ja-JP", Repeat('う', 40), 3.0)!;
        Assert.Equal("ja-JP-NanamiNeural", plan.VoiceId);
        Assert.InRange(plan.SpeakingRate, 1.27, 1.28);                    // (40/11)/2.85 = 1.276
        Assert.Equal(0, plan.ExcessChars);
        var prosody = plan.ToProsody();
        Assert.Equal("ja-JP-NanamiNeural", prosody.EdgeVoice);
        Assert.Equal("+28%", prosody.Rate);
        Assert.True(prosody.Speed <= 1.2);
        Assert.Equal(1.0, PlutoLocaleSubtitleFitter.PlanNarration(channels, "es-ES", "Hola", 0)!.SpeakingRate, 3);   // 구간 미상 → 기본 속도

        // 기본 로캘 · 서비스 없음 → 원문 그대로(동일 인스턴스)
        Assert.Same(displayed, PlutoLocaleSubtitleFitter.Project(displayed, channels, "ko-KR", 1, 3).Cues);
        Assert.Same(displayed, PlutoLocaleSubtitleFitter.Project(displayed, null, "ja-JP", 1, 3).Cues);
        Assert.Null(PlutoLocaleSubtitleFitter.PlanNarration(channels, "ko", "안녕", 3));
    }

    // ── 2. 로캘 패키지 생성/복사/사이드카 → {Base}::{Locale} 멱등 보장 → ja/es Studio 내보내기 도착 즉시 연결 ──

    [Fact]
    public async Task LocalizedPackActions_UpsertVariantIdempotently_AndJaEsStudioExportsLinkOnArrival()
    {
        using var cat = CatalogAt(Sub("upsert"));
        await cat.RecordRenderedClipAsync(Master("clip01", "고양이의 비밀"));
        var svc = new PlutoYouTubePublishPackService(catalog: cat, clock: static () => T0);
        var jaPack = await svc.GenerateAsync(new PlutoPublishPackRequest("猫の秘密", "猫の秘密", 0, 0, "pov-walk", new[] { "cat" }) { TargetLocale = "ja-JP" });
        var esPack = await svc.GenerateAsync(new PlutoPublishPackRequest("El secreto del gato", "El secreto del gato", 0, 0, "pov-walk", new[] { "cat" }) { TargetLocale = "es" });
        Assert.Equal("ja-JP", jaPack.Locale);
        Assert.Equal("猫の秘密", jaPack.CatalogTitle);
        Assert.Equal("es-ES", esPack.Locale);

        var v1 = await svc.EnsureLocaleVariantAsync("clip01", jaPack);
        var v2 = await svc.EnsureLocaleVariantAsync("clip01::ja-JP", jaPack);   // 변형 ID 로 다시 → 같은 레코드
        Assert.Equal("clip01::ja-JP", v1!.VideoId);
        Assert.Equal("clip01::ja-JP", v2!.VideoId);
        Assert.Equal("猫の秘密", v1.Title);
        Assert.Equal(2, (await cat.GetRecentClipsAsync(100)).Count);           // 마스터 + 변형 1건(중복 없음)
        Assert.Null(await svc.EnsureLocaleVariantAsync("clip01", jaPack with { Locale = string.Empty }));   // 레거시 패키지 → 무동작
        Assert.Null(await new PlutoYouTubePublishPackService().EnsureLocaleVariantAsync("clip01", jaPack)); // 카탈로그 미연결

        // 🅰️ 복사 → 변형에 기록(마스터 무접촉) · 💾 로캘 사이드카
        Assert.True(await svc.RecordSelectionAsync("clip01", jaPack, "A"));
        Assert.True(await svc.RecordSelectionAsync("clip01", esPack, "AB"));
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("clip01"))!.PublishedTitleText);
        Assert.Equal(jaPack.TitleA.Title, (await cat.FindByVideoIdOrTitleAsync("clip01::ja-JP"))!.PublishedTitleText);
        Assert.Equal(esPack.TitleA.Title, (await cat.FindByVideoIdOrTitleAsync("clip01::es-ES"))!.PublishedTitleText);
        var video = Path.Combine(Sub("media"), "clip01.mp4");
        File.WriteAllBytes(video, new byte[] { 0, 0, 0, 24 });
        var side = await svc.WriteSidecarAsync(video, jaPack, "clip01::ja-JP");
        Assert.True(side.Ok);
        Assert.EndsWith("clip01_ja-JP_publish_pack.txt", side.Path, StringComparison.Ordinal);

        // 📊 일/서 Studio 내보내기 → 로캘 변형에 자동 연결(대기열로 새지 않음)
        var jaCsv = "動画,動画のタイトル,動画公開時刻,視聴回数,平均再生率(%),インプレッションのクリック率(%),視聴を選択(%)\n"
            + "JJJJJJJJJJJ," + jaPack.TitleA.Title + ",2026/12/02,900,51.0,6.6,70.5\n";
        var esCsv = "Vídeo,Título del vídeo,Hora de publicación del vídeo,Visualizaciones,Porcentaje medio visto (%),Tasa de clics de las impresiones (%),Se quedaron a ver (%)\n"
            + "EEEEEEEEEEE,\"" + esPack.TitleA.Title + "\",2 dic 2026,700,\"47,0\",\"4,4\",\"63,0\"\n";
        var linker = new PlutoYouTubeCatalogLinker(cat);
        var jaRows = YouTubeStudioAnalyticsNormalizer.Parse(Encoding.UTF8.GetBytes(jaCsv), "表データ.csv").Rows;
        var esRows = YouTubeStudioAnalyticsNormalizer.Parse(Encoding.UTF8.GetBytes(esCsv), "Datos de la tabla.csv").Rows;
        var links = await linker.LinkAsync(new[] { jaRows[0], esRows[0] });
        Assert.Equal("clip01::ja-JP", links[0].Clip?.VideoId);
        Assert.Equal("clip01::es-ES", links[1].Clip?.VideoId);
        Assert.Equal("JJJJJJJJJJJ", (await cat.FindByVideoIdOrTitleAsync("clip01::ja-JP"))!.YouTubeVideoId);
        Assert.Equal("EEEEEEEEEEE", (await cat.FindByVideoIdOrTitleAsync("clip01::es-ES"))!.YouTubeVideoId);
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("clip01"))!.YouTubeVideoId);

        // 🧊 연결 후 재보장 — YouTube ID · 게시 제목 동결(다른 제목 패키지로 다시 생성해도 덮지 않음)
        var again = await svc.EnsureLocaleVariantAsync("clip01", jaPack with { CatalogTitle = "別のタイトル" });
        Assert.Equal("JJJJJJJJJJJ", again!.YouTubeVideoId);
        Assert.Equal("猫の秘密", again.Title);
        Assert.Equal(3, (await cat.GetRecentClipsAsync(100)).Count);
    }

    // ── 3. 로캘별 제목 프레이밍 CTR 순위 — ja = 호기심형 · ko = 성과 증명형 유지 · 콜드스타트 → 전역 폴백 ──

    [Fact]
    public void PerLocaleFramingRanking_PicksCuriosityForJa_KeepsOutcomeProofForKo_AndFallsBackWhenCold()
    {
        var clips = new List<PlutoRenderedClipRecord>
        {
            Attributed("ko1", "yt000000001", PlutoTitleFramings.OutcomeProof, 8.0),
            Attributed("ko2", "yt000000002", PlutoTitleFramings.OutcomeProof, 8.0),
            Attributed("ko3", "yt000000003", PlutoTitleFramings.OutcomeProof, 8.0),
            Attributed("ko4", "yt000000004", PlutoTitleFramings.CuriosityQuestion, 5.0),
            Attributed("ko5", "yt000000005", PlutoTitleFramings.CuriosityQuestion, 5.0),
            Attributed("m1::ja-JP", "yt000000006", PlutoTitleFramings.CuriosityQuestion, 12.0),
            Attributed("m2::ja-JP", "yt000000007", PlutoTitleFramings.CuriosityQuestion, 11.0),
            Attributed("m3::ja-JP", "yt000000008", PlutoTitleFramings.OutcomeProof, 6.0),
            Attributed("m4::es-ES", "yt000000009", PlutoTitleFramings.OutcomeProof, 3.0),   // 로캘 1건 → 콜드스타트
            Attributed("m5::en-US", "yt000000010", PlutoTitleFramings.EndingPrompt, 30.0, views: 40),   // 조회 < 50 → 귀속 제외
        };
        var svc = new PlutoYouTubePublishPackService();
        svc.RefreshFramingEvidence(clips);

        var global = svc.GetFramingRanking();
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, global[0].Framing);   // 🧮 전역 풀링은 고CTR 로캘(ja)에 끌려감(심슨 역설)

        var ko = svc.GetLocaleFramingRanking("ko-KR");
        Assert.True(ko.LocaleEvidenced);
        Assert.Equal(5, ko.LocaleSamples);
        Assert.Equal(PlutoTitleFramings.OutcomeProof, ko.TopFraming);

        var ja = svc.GetLocaleFramingRanking("ja");
        Assert.True(ja.LocaleEvidenced);
        Assert.Equal("ja-JP", ja.Locale);
        Assert.Equal(3, ja.LocaleSamples);
        Assert.Equal(PlutoTitleFramings.CuriosityQuestion, ja.TopFraming);

        var es = svc.GetLocaleFramingRanking("es-ES");
        Assert.False(es.LocaleEvidenced);
        Assert.Equal(1, es.LocaleSamples);
        Assert.Equal(global[0].Framing, es.TopFraming);                         // 콜드스타트 → 전역 사전

        var enCold = svc.GetLocaleFramingRanking("en-US");
        Assert.False(enCold.LocaleEvidenced);
        Assert.Equal(0, enCold.LocaleSamples);

        // 순수 API · 레거시(로캘 없음) 동일성
        Assert.Equal(PlutoTitleFramings.OutcomeProof, PlutoYouTubePublishPackService.RankForLocale(PlutoYouTubePublishPackService.ComputeFramingStats(clips),
            PlutoYouTubePublishPackService.ComputeFramingStats(clips, "ko-KR"), "ko-KR").TopFraming);
        Assert.Equal(PlutoYouTubePublishPackService.ComputeFramingStats(clips).Length, PlutoYouTubePublishPackService.ComputeFramingStats(clips, null).Length);
        Assert.Equal("ja-JP", PlutoYouTubePublishPackService.LocaleOf(clips[5]));
        Assert.Equal("ko-KR", PlutoYouTubePublishPackService.LocaleOf(clips[0]));

        // 생성 — ko 패키지 A = 성과 증명형 · ja 패키지 A 는 성과 증명형이 아님(로캘 1위 호기심형 또는 연결성 안전형)
        var koPack = svc.Generate(new PlutoPublishPackRequest("가을 숲 산책", "가을 숲 산책", 0, 0, "pov-walk", null) { TargetLocale = "ko-KR" });
        Assert.Equal(PlutoTitleFramings.OutcomeProof, koPack.TitleA.Framing);
        var jaPack = svc.Generate(new PlutoPublishPackRequest("秋の森の散歩", "秋の森の散歩", 0, 0, "pov-walk", null) { TargetLocale = "ja-JP" });
        Assert.NotEqual(PlutoTitleFramings.OutcomeProof, jaPack.TitleA.Framing);
        Assert.Contains(jaPack.TitleA.Framing, new[] { PlutoTitleFramings.CuriosityQuestion, PlutoTitleFramings.CatalogSafe });
    }

    // ── 4. 두뇌 · 진단 · 플래너 로캘 분해 + 교차 로캘 각색 기획 ──

    private static List<PlutoRenderedClipRecord> CrossLocaleCatalog()
    {
        PlutoRenderedClipRecord M(string id, string title, string yt, long views, double hook, double comp, double ctr)
            => Master(id, title) with { YouTubeVideoId = yt, YouTubeWatermark = new PlutoYouTubeIngestWatermark(views, 1, hook, comp, ctr, T0) };
        return new List<PlutoRenderedClipRecord>
        {
            M("ko1", "가을 숲 산책", "yt0000000k1", 1500, 70, 60, 8),   // 합성 68 · 로캘 기준선 ≈ 50.9 → +17p 승자
            M("ko2", "겨울 바다 여행", "yt0000000k2", 1000, 50, 40, 4),
            M("ko3", "봄 꽃길 브이로그", "yt0000000k3", 800, 45, 35, 3),
            M("ko2::ja-JP", "冬の海の旅", "yt0000000j1", 600, 60, 50, 9) with { TargetLocale = "ja-JP", ParentClipId = "ko2" },
        };
    }

    [Fact]
    public async Task BrainDiagnosticAndPlanner_SurfacePerLocaleMetrics_AndCrossLocaleExpansionBrief()
    {
        var clips = CrossLocaleCatalog();
        var realized = PlutoRealizedYouTubeSummary.Compute(clips);
        Assert.Equal(4, realized.Locales.Count);
        Assert.Equal(new[] { "KO", "EN", "JA", "ES" }, new[] { realized.Locales[0].ShortLabel, realized.Locales[1].ShortLabel, realized.Locales[2].ShortLabel, realized.Locales[3].ShortLabel });
        Assert.Equal(3, realized.Locales[0].LinkedCount);
        Assert.Equal(0, realized.Locales[1].LinkedCount);
        Assert.Equal(1, realized.Locales[2].LinkedCount);
        Assert.Equal(9.0, realized.Locales[2].WeightedCtrPct, 3);
        var label = realized.ToLocaleLabel();
        Assert.Contains("KO 3편 CTR", label, StringComparison.Ordinal);
        Assert.Contains("EN —", label, StringComparison.Ordinal);
        Assert.Contains("JA 1편 CTR 9.0%", label, StringComparison.Ordinal);

        // 🧠 두뇌 리포트 · 🩺 진단 Markdown
        using var cat = CatalogAt(Sub("brain"));
        for (var i = clips.Count - 1; i >= 0; i--) await cat.RecordRenderedClipAsync(clips[i]);
        var brain = await new PlutoAutonomousBrainSummaryService(null, null, null) { Catalog = cat }.GetSummaryAsync();
        Assert.Equal(4, brain.LocaleBreakdown.Count);
        Assert.Contains("JA 1편", brain.LocaleBreakdownLabel, StringComparison.Ordinal);
        var md = await new PlutoSystemDiagnosticService(null, cat, null, null, () => T0.UtcDateTime).BuildSnapshotMarkdownAsync();
        Assert.Contains(PlutoSystemDiagnosticService.LocaleHeading, md, StringComparison.Ordinal);
        Assert.Contains("| KO (ko-KR) | 3 | 3 |", md, StringComparison.Ordinal);
        Assert.Contains("| JA (ja-JP) | 1 | 1 | 9.0% |", md, StringComparison.Ordinal);
        Assert.Contains("| ES (es-ES) | 0 | 0 | — |", md, StringComparison.Ordinal);

        // 🗺️ 플래너 — 검증된 ko 승자(ko1)를 실측 성과 있는 미분기 로캘(ja-JP)로 각색
        var inputs = new PlutoTopicPlannerInputs { RecentClips = clips, NowUtc = T0, CrossLocaleExpansion = true };
        var briefs = PlutoAutonomousTopicPlannerService.Synthesize(inputs);
        PlutoTopicBrief? xl = null;
        foreach (var b in briefs) if (b.Kind == PlutoTopicBriefKinds.CrossLocaleAdaptation) xl = b;
        Assert.NotNull(xl);
        Assert.True(xl!.IsCrossLocale);
        Assert.Equal("ko1", xl.SourceClipId);
        Assert.Equal("ja-JP", xl.TargetLocale);
        Assert.Equal("pov-walk", xl.HookStyle);
        Assert.Contains("ko1::ja-JP", xl.RationaleKorean, StringComparison.Ordinal);
        Assert.StartsWith("tp-", xl.BriefId, StringComparison.Ordinal);
        Assert.Empty(PlutoAutonomousTopicPlannerService.Synthesize(inputs with { CrossLocaleExpansion = false }));   // 순수 합성 기본 = 끔(다른 후보 없음)

        // 이미 ja 분기 → 다음 미분기 로캘(콜드 → 내장 순서 en-US)
        var branched = new List<PlutoRenderedClipRecord>(clips) { Master("ko1::ja-JP", "秋の森の散歩") with { TargetLocale = "ja-JP", ParentClipId = "ko1" } };
        Assert.Equal("en-US", PlutoAutonomousTopicPlannerService.ProposeCrossLocale(inputs with { RecentClips = branched }, PlutoAutonomousTopicPlannerService.OrderFraming(null), null)!.TargetLocale);

        // 🛡️ 오답 노트 가드 — 훅 토큰 · 훅×룩 조합 차단 → 각색 제안 없음 · 로캘 측정 1편뿐 → 승자 판정 보류
        var suppressed = new HashSet<string>(StringComparer.Ordinal) { "pov-walk" };
        Assert.Null(PlutoAutonomousTopicPlannerService.ProposeCrossLocale(inputs with { Suppressed = suppressed }, Array.Empty<PlutoTitleFramingStat>(), null));
        var pair = PlutoPairAttribution.PairKey("pov-walk", PlutoColorGradingCompiler.NaturalPastelName)!;
        Assert.Null(PlutoAutonomousTopicPlannerService.ProposeCrossLocale(inputs with { SuppressedComposites = new HashSet<string>(StringComparer.Ordinal) { pair } }, Array.Empty<PlutoTitleFramingStat>(), null));
        Assert.Null(PlutoAutonomousTopicPlannerService.ProposeCrossLocale(inputs with { RecentClips = clips.GetRange(0, 1) }, Array.Empty<PlutoTitleFramingStat>(), null));

        // 🧩 다양성 — 각색 기획은 같은 훅/룩을 써도 다른 기획을 밀어내지 않음
        var other = new PlutoTopicBrief(1, "tp-x", PlutoTopicBriefKinds.ParetoExploit, "t", "a", "단편", 1, 0, "pov-walk", xl.ColorLookId, xl.BgmMood, Array.Empty<string>(), PlutoTitleFramings.OutcomeProof, false, 1, "");
        Assert.Same(other, PlutoAutonomousTopicPlannerService.Diversify(other, new[] { xl }, Array.Empty<PlutoRankedOption>(), Array.Empty<PlutoRankedOption>()));
        Assert.Same(xl, PlutoAutonomousTopicPlannerService.Diversify(xl, new[] { other }, Array.Empty<PlutoRankedOption>(), Array.Empty<PlutoRankedOption>()));
    }

    // ── 5. 🧪 8단계 플라이휠 자가진단 — 샌드박스 전 단계 PASS · 라이브 무접촉 ──

    [Fact]
    public async Task FlywheelSelfTest_PassesAllEightStages_IncludingMultiLanguageStage_WithoutTouchingLiveState()
    {
        Assert.Equal(8, PlutoFlywheelSelfTestService.StageCount);
        Assert.Equal(8, PlutoFlywheelSelfTestService.StageNames.Length);
        Assert.Contains("다국어", PlutoFlywheelSelfTestService.StageNames[7], StringComparison.Ordinal);
        var live = Sub("live");
        var liveFile = Path.Combine(live, PlutoRenderCatalogService.FileName);
        File.WriteAllText(liveFile, "{\"live\":true}");
        var stamp = File.GetLastWriteTimeUtc(liveFile);
        var parent = Sub("tmp");
        var svc = new PlutoFlywheelSelfTestService(parent, new[] { live });
        var rep = await svc.RunAsync();
        var why = string.Join(" / ", Array.ConvertAll(new List<PlutoFlywheelStageResult>(rep.Stages).ToArray(), s => s.Index + ":" + s.StatusLabel + ":" + s.Detail));
        Assert.True(rep.AllPassed, why);
        Assert.Equal(8, rep.Stages.Count);
        Assert.True(rep.Stages[7].Passed, why);
        Assert.Contains("ja-JP", rep.Stages[7].Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(parent, rep.Stages[7].Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(rep.SandboxCleaned);
        Assert.True(rep.LiveStateUntouched);
        Assert.Equal("{\"live\":true}", File.ReadAllText(liveFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(liveFile));
        Assert.Empty(Directory.GetDirectories(parent, PlutoFlywheelSelfTestService.SandboxPrefix + "*"));
    }

    // ── 6a. 🧠 자체 추가 ① — 용량 축출 보호: 꼬리 근처 마스터 분기 시 새 변형/마스터 보존 · 학습 이력보다 미게시 변형을 먼저 축출 ──

    [Fact]
    public async Task CoArchitect_CapacityEviction_NeverDropsFreshVariantOrMaster_AndSacrificesUnpublishedVariantBeforeLearnedClip()
    {
        using var cat = CatalogAt(Sub("evict"));
        await cat.RecordRenderedClipAsync(Master("old", "오래된 영상", at: T0));
        for (var i = 1; i < PlutoRenderCatalogService.Capacity; i++)
            await cat.RecordRenderedClipAsync(Master("c" + i.ToString("000", System.Globalization.CultureInfo.InvariantCulture), "영상 " + i, at: T0.AddMinutes(i)));
        Assert.Equal(PlutoRenderCatalogService.Capacity, (await cat.GetRecentClipsAsync(500)).Count);

        // 꼬리(가장 오래된) 마스터 분기 — 예전엔 새 변형이 즉시 잘려 null 아닌 결과가 유령 레코드였음
        var v = await cat.BranchLocaleVariantAsync("old", "ja-JP", "古い動画");
        Assert.NotNull(v);
        Assert.NotNull(await cat.FindByVideoIdOrTitleAsync("old::ja-JP"));
        Assert.NotNull(await cat.FindByVideoIdOrTitleAsync("old"));
        Assert.Null(await cat.FindByVideoIdOrTitleAsync("c001"));          // 대신 다음으로 오래된 비보호 레코드 축출
        Assert.Equal(PlutoRenderCatalogService.Capacity, (await cat.GetRecentClipsAsync(500)).Count);
        Assert.True(await new PlutoYouTubePublishPackService(catalog: cat).RecordSelectionAsync("old",
            new PlutoYouTubePublishPackService().Generate(new PlutoPublishPackRequest("古い動画", "古い動画", 0, 0, null, null) { TargetLocale = "ja-JP" }), "A"));   // 선택 기록이 실제로 남음

        // 꼬리 후보가 학습 이력(YouTube 연결) 보유 → 미게시 로캘 변형을 먼저 희생
        Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("c002", "ytLEARNED02"));
        Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("old", "ytLEARNEDOL"));
        await cat.RecordRenderedClipAsync(Master("fresh", "새 영상", at: T0.AddDays(1)));
        Assert.NotNull(await cat.FindByVideoIdOrTitleAsync("c002"));
        Assert.Null(await cat.FindByVideoIdOrTitleAsync("old::ja-JP"));
        Assert.NotNull(await cat.FindByVideoIdOrTitleAsync("fresh"));
        Assert.Equal(PlutoRenderCatalogService.Capacity, (await cat.GetRecentClipsAsync(500)).Count);
        Assert.NotNull(await cat.BranchLocaleVariantAsync("old", "ja-JP"));   // 마스터에서 멱등 재분기 가능
    }

    // ── 6b. 🧠 자체 추가 ② — 로캘 변형은 마스터 기획 기대치로 채점하지 않음(콜드 지역 채널이 전역 오답 노트를 오염시키지 않게) ──

    [Fact]
    public async Task CoArchitect_LocaleVariants_AreNotGradedAgainstMasterBrief_SoColdChannelsCannotPoisonMistakeNotebook()
    {
        var dir = Sub("grade");
        using var cat = CatalogAt(dir);
        var master = Master("m1", "가을 숲 산책") with
        {
            AppliedBriefId = "tp-00000001", AppliedBriefKind = PlutoTopicBriefKinds.ParetoExploit, AppliedBriefExpectedRetention = 50.0, AppliedBriefHookStyle = "pov-walk",
            AppliedBriefColorLookId = PlutoColorGradingCompiler.NaturalPastelName, AppliedBriefTheme = "가을 숲 산책", AppliedBriefAppliedAtUtc = T0,
            YouTubeVideoId = "ytMASTER001", YouTubeWatermark = new PlutoYouTubeIngestWatermark(1200, 1, 62, 52, 6, T0),
        };
        await cat.RecordRenderedClipAsync(master);
        var variant = await cat.BranchLocaleVariantAsync("m1", "ja-JP", "秋の森の散歩");
        Assert.True(variant!.HasAppliedBrief);                                   // 계보 상속(Step 31)
        Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("m1::ja-JP", "ytVARIANT01"));
        Assert.True(await cat.UpdateYouTubeWatermarkAsync("m1::ja-JP", new PlutoYouTubeIngestWatermark(400, 1, 15, 8, 1.0, T0)));   // 콜드 채널 초기 저조(심각 미달 수준)

        var clips = await cat.GetRecentClipsAsync(100);
        PlutoRenderedClipRecord v = null!, m = null!;
        foreach (var c in clips) { if (c.VideoId == "m1::ja-JP") v = c; else if (c.VideoId == "m1") m = c; }
        Assert.Null(PlutoBriefOutcomeAttributionService.Grade(v));
        Assert.NotNull(PlutoBriefOutcomeAttributionService.Grade(m));
        Assert.Equal(1, PlutoBriefHitRateSummary.Compute(clips).PendingClips);   // 변형은 대기 건수에 안 잡힘

        using var notebook = new PlutoMistakeNotebookService(Path.Combine(dir, PlutoMistakeNotebookService.FileName), () => T0.UtcDateTime.AddDays(1));
        using var briefs = new PlutoBriefOutcomeAttributionService(cat, notebook);
        var sweep = await briefs.EvaluatePendingBriefOutcomesAndRehabilitationAsync();
        Assert.Equal(1, sweep.EvaluatedCount);
        Assert.Equal("m1", sweep.Verdicts[0].ClipVideoId);
        Assert.Equal(0, sweep.PostMortemCount);
        Assert.Empty(notebook.GetSuppressedCompositeKeys());
    }

    // ── 6c. 🧠 자체 추가 ③ — 한국어 편 장식 누출 차단 · en 이중 언어 승격 · 번역 누락 경고 · 편집 인덱스 불변 ──

    [Fact]
    public void CoArchitect_KoreanDecorationNeverLeaks_BilingualCuesPromote_UntranslatedCuesWarn_AndEditIndicesHold()
    {
        var channels = new PlutoMultiLanguageChannelService();
        var baseCues = new[]
        {
            SubtitleCue.Precise(0, 3, "끝까지 보면 반전", "Watch till the end"),
            SubtitleCue.Precise(3, 6, "숲이 말을 걸어요", "The forest talks back"),
        };
        var state = PlutoSubtitleOverlayState.FromBase(baseCues);
        var shown = PlutoSeriesSubtitleOverlay.Compose(state, null, PlutoSeriesEscalationMode.AutoRewrite, 2, 3);
        Assert.StartsWith("2/3편 · ", shown[0].Text, StringComparison.Ordinal);

        var en = PlutoLocaleSubtitleFitter.Project(shown, channels, "en-US", 2, 3);
        Assert.Equal("Ep.2/3 · Watch till the end", en.Cues[0].Text);
        Assert.Equal("The forest talks back (to be continued)", en.Cues[1].Text.Replace("\n", " ", StringComparison.Ordinal));   // 34자 초과 → 단어 경계 2줄
        Assert.Null(en.Cues[0].SecondaryText);
        Assert.Equal(0, en.UntranslatedCueCount);
        foreach (var c in en.Cues) Assert.DoesNotContain("편", c.Text, StringComparison.Ordinal);

        var es = PlutoLocaleSubtitleFitter.Project(shown, channels, "es-ES", 3, 3);   // 장식 편 번호와 다른 편 → 한국어 장식 그대로 본문 취급
        Assert.Equal(2, es.UntranslatedCueCount);
        Assert.Contains("번역 안 된 한글 자막 2컷", es.WarningKorean, StringComparison.Ordinal);
        var esOk = PlutoLocaleSubtitleFitter.Project(new[] { SubtitleCue.Precise(0, 3, "Mira hasta el final") }, channels, "es-ES", 3, 3);
        Assert.Equal("Ep. 3/3 · Mira hasta el final (final)", esOk.Cues[0].Text.Replace("\n", " ", StringComparison.Ordinal));
        Assert.Equal(2, esOk.Fits[0].LineCount);                                  // 37자 > 34 → 단어 경계 2줄(안전 영역 이내 · 분할 없음)
        Assert.Single(esOk.Cues);
        Assert.Equal(string.Empty, esOk.WarningKorean);

        // 투영은 렌더 전용 — 편집은 여전히 원문 인덱스로 반영되고 원문 큐 수 불변
        var edited = PlutoSeriesSubtitleOverlay.ApplyEdit(state, null, PlutoSeriesEscalationMode.AutoRewrite, 2, 3, 1, "숲이 대답해요 (다음 편에 계속)");
        Assert.Equal(2, edited.BaseCues.Count);
        Assert.Equal("숲이 대답해요", edited.BaseCues[1].Text);
        Assert.Equal("끝까지 보면 반전", edited.BaseCues[0].Text);
        Assert.Equal(PlutoLocaleSubtitleFitter.SeriesTail("pt-BR", true), PlutoLocaleSubtitleFitter.SeriesTail("en-US", true));   // 사용자 정의 로캘 → 영어 꼬리
    }

    // ── 6d. 🧠 자체 추가 ④ — 로캘 패키지 선택은 그 로캘 변형에만 기록(마스터 · 다른 로캘 오귀속 차단) ──

    [Fact]
    public async Task CoArchitect_LocalizedSelection_IsRoutedToLocaleVariant_NeverMisattributedToMasterOrSibling()
    {
        using var cat = CatalogAt(Sub("route"));
        await cat.RecordRenderedClipAsync(Master("clip01", "고양이의 비밀"));
        var svc = new PlutoYouTubePublishPackService(catalog: cat);
        var esPack = svc.Generate(new PlutoPublishPackRequest("El gato", "El gato", 0, 0, null, null) { TargetLocale = "es-ES" });

        // 다른 로캘 변형 ID 로 호출해도(UI 가 직전 ja 변형을 들고 있는 경합) es 변형으로 재라우팅
        Assert.NotNull(await cat.BranchLocaleVariantAsync("clip01", "ja-JP", "猫"));
        Assert.True(await svc.RecordSelectionAsync("clip01::ja-JP", esPack, "B"));
        Assert.Equal(esPack.TitleB.Title, (await cat.FindByVideoIdOrTitleAsync("clip01::es-ES"))!.PublishedTitleText);
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("clip01::ja-JP"))!.PublishedTitleText);
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("clip01"))!.PublishedTitleText);

        // 마스터 없음 → 거부(고아 변형/오귀속 생성 금지) · 레거시 패키지는 기존 경로 유지
        Assert.False(await svc.RecordSelectionAsync("ghost", esPack, "A"));
        Assert.Null(await cat.FindByVideoIdOrTitleAsync("ghost::es-ES"));
        var legacy = svc.Generate(new PlutoPublishPackRequest("고양이", "고양이의 비밀", 0, 0, null, null));
        Assert.Equal(string.Empty, legacy.Locale);
        Assert.True(await svc.RecordSelectionAsync("clip01", legacy, "A"));
        Assert.Equal(legacy.TitleA.Title, (await cat.FindByVideoIdOrTitleAsync("clip01"))!.PublishedTitleText);
    }
}
