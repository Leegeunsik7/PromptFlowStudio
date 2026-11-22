using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Pluto;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>🌐 Step 31 — 다국어 지역 채널 분기(ko-KR · en-US · ja-JP · es-ES) · 로캘 훅/자막/TTS · 로캘 업로드 패키지 · 일/서 Studio 분석 수집 · 자체 추가 안전장치.</summary>
public sealed class PlutoMultiLanguageChannelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_multilang_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    public PlutoMultiLanguageChannelTests() => Directory.CreateDirectory(_dir);

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

    private static PlutoRenderedClipRecord Master(string id, string title, string mode = "시리즈 1/3편")
        => new(id, title, mode, "pov-walk", PlutoColorGradingCompiler.NaturalPastelName, "Lullaby", new[] { "cat" }, "", T0, false);

    private static async Task<PlutoRenderCatalogService> FourLocaleCatalogAsync(string dir)
    {
        var cat = CatalogAt(dir);
        await cat.RecordRenderedClipAsync(Master("clip01", "고양이의 비밀 1/3편"));
        Assert.NotNull(await cat.BranchLocaleVariantAsync("clip01", "en-US", "The cat secret Ep.1/3"));
        Assert.NotNull(await cat.BranchLocaleVariantAsync("clip01", "ja-JP", "猫の秘密 第1/3話"));
        Assert.NotNull(await cat.BranchLocaleVariantAsync("clip01", "es-ES", "El secreto del gato Ep. 1/3"));
        return cat;
    }

    private static int Occurrences(string s, string token)
    {
        var n = 0;
        for (var i = s.IndexOf(token, StringComparison.Ordinal); i >= 0; i = s.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    // ── 1. 로캘 레지스트리 · 훅 · 편 토큰 · 줄바꿈 · TTS ──

    [Fact]
    public void ChannelService_AdaptsHooksTokensWrappingAndTtsRate_AcrossFourLocales()
    {
        var svc = new PlutoMultiLanguageChannelService();
        Assert.Equal(new[] { "ko-KR", "en-US", "ja-JP", "es-ES" }, svc.Profiles.Select(p => p.LocaleCode).ToArray());
        Assert.Equal(new[] { "KO", "EN", "JA", "ES" }, svc.Profiles.Select(p => p.ShortLabel).ToArray());
        Assert.True(svc.TryGetProfile("ja_jp", out var ja));
        Assert.Equal(PlutoHookAdaptationStyles.HighContextCuriosity, ja.HookAdaptationStyle);
        Assert.Equal(15, ja.MaxCharsPerLine);
        Assert.Equal(34, svc.GetProfileOrDefault("es").MaxCharsPerLine);
        Assert.Equal(17.0, svc.GetProfileOrDefault("en").TargetCharsPerSecond);
        Assert.Equal(11.0, svc.GetProfileOrDefault("ko-KR").TargetCharsPerSecond);
        Assert.Contains("#ショート", ja.DefaultHashtags);
        Assert.Contains("#cortos", svc.GetProfileOrDefault("es-ES").DefaultHashtags);

        // 편 토큰
        Assert.Equal("1/3편", svc.FormatEpisodeToken("ko-KR", 1, 3));
        Assert.Equal("Ep.1/3", svc.FormatEpisodeToken("en-US", 1, 3));
        Assert.Equal("第1/3話", svc.FormatEpisodeToken("ja-JP", 1, 3));
        Assert.Equal("Ep. 1/3", svc.FormatEpisodeToken("es-ES", 1, 3));
        Assert.Equal(string.Empty, svc.FormatEpisodeToken("ja-JP", 4, 3));

        // 훅 각색(스타일별 · 장식 중복 없음 · 일반 키 → 로캘 기본 훅)
        Assert.Equal("결론부터: 끝까지 보면 반전", svc.AdaptHook("ko-KR", "montage-hook"));
        Assert.Equal("고양이가 사라졌다", svc.AdaptHook("ko-KR", "  고양이가   사라졌다!!  "));
        Assert.Equal("Worth it: the cat vanished", svc.AdaptHook("en-US", "the cat vanished"));
        Assert.Equal("猫が消えた…その理由は？", svc.AdaptHook("ja-JP", "猫が消えた"));
        Assert.Equal("¡El gato desapareció!", svc.AdaptHook("es-ES", "El gato desapareció"));
        Assert.Equal("¡El gato desapareció!", svc.AdaptHook("es-ES", "¡El gato desapareció!"));
        var longJa = svc.AdaptHook("ja-JP", new string('猫', 60));
        Assert.True(PlutoMultiLanguageChannelService.TextLength(longJa) <= 15 * PlutoMultiLanguageChannelService.MaxSafeZoneLines);

        // 줄바꿈 — CJK 문자 경계 15 · 라틴 단어 경계 34(단어 미분할)
        var jaWrap = svc.WrapSubtitle("ja-JP", "猫の秘密を知るために私たちは夜の街を歩き続けた。そして最後に驚くべき真実を見つけた");
        Assert.All(jaWrap.Lines, l => Assert.True(PlutoMultiLanguageChannelService.TextLength(l) <= 15, l));
        Assert.Equal("猫の秘密を知るために私たちは夜の街を歩き続けた。そして最後に驚くべき真実を見つけた", string.Concat(jaWrap.Lines));
        Assert.True(jaWrap.ExceedsSafeZone);
        const string En = "The cat walked across the silent city at night looking for the one secret nobody had ever told";
        var enWrap = svc.WrapSubtitle("en-US", En);
        Assert.All(enWrap.Lines, l => Assert.True(l.Length <= 34, l));
        var words = new HashSet<string>(En.Split(' '));
        Assert.All(enWrap.Lines.SelectMany(l => l.Split(' ')), w => Assert.Contains(w, words));
        Assert.Equal(En, string.Join(" ", enWrap.Lines));
        var koWrap = svc.WrapSubtitle("ko-KR", "고양이는 조용한 밤거리를 걸으며 아무도 몰랐던 비밀을 찾아 나섰다");
        Assert.All(koWrap.Lines, l => Assert.True(l.Length <= 15, l));
        Assert.All(koWrap.Lines, l => Assert.False(l.StartsWith(' ') || l.EndsWith(' ')));

        // TTS 길이 예산 — 0.85x~1.30x 안 · 맞으면 장면 안 · 최대 속도로도 넘치면 축약 필요
        var fits = svc.ComputeTtsBudget("en-US", new string('a', 60), 3.0);
        Assert.Equal(1.238, fits.SpeakingRate, 3);
        Assert.True(fits.FitsSegment);
        Assert.False(fits.Clamped);
        var slow = svc.ComputeTtsBudget("en-US", "hi there", 3.0);
        Assert.Equal(1.0, slow.SpeakingRate);   // 짧은 문장을 늘이지 않음
        var over = svc.ComputeTtsBudget("en-US", new string('a', 80), 3.0);
        Assert.Equal(PlutoMultiLanguageChannelService.MaxSpeakingRate, over.SpeakingRate);
        Assert.True(over.Clamped);
        Assert.True(over.RequiresCondensation);
        Assert.Equal(62, over.MaxUnitsAtMaxRate);
        var jaTts = svc.ComputeTtsBudget("ja-JP", new string('猫', 30), 2.0);   // CJK 11 cps → 같은 글자 수도 더 오래 걸림
        Assert.True(jaTts.RequiresCondensation);
        var enTts = svc.ComputeTtsBudget("en-US", new string('a', 30), 2.0);
        Assert.True(enTts.FitsSegment);
        foreach (var b in new[] { fits, slow, over, jaTts, enTts })
            Assert.InRange(b.SpeakingRate, PlutoMultiLanguageChannelService.MinSpeakingRate, PlutoMultiLanguageChannelService.MaxSpeakingRate);

        // 확장 로캘 등록 — 검증 · 내장 교체 금지
        Assert.True(svc.RegisterProfile(new PlutoChannelLocaleProfile("pt-BR", "Português", PlutoHookAdaptationStyles.EmotionalUrgency, "Ep. {0}/{1}", 34, 16.0, "pt-BR-FranciscaNeural", 1.0, new[] { "#shorts" })));
        Assert.Equal("Ep. 2/5", svc.FormatEpisodeToken("pt-br", 2, 5));
        Assert.False(svc.RegisterProfile(new PlutoChannelLocaleProfile("ja-JP", "x", PlutoHookAdaptationStyles.ValueFirst, "{0}/{1}", 15, 11.0, "v", 1.0, Array.Empty<string>())));
        Assert.False(svc.RegisterProfile(new PlutoChannelLocaleProfile("fr-FR", "x", PlutoHookAdaptationStyles.ValueFirst, "Ep {0}", 34, 17.0, "v", 1.0, Array.Empty<string>())));
        Assert.False(svc.RegisterProfile(new PlutoChannelLocaleProfile("fr-FR", "x", PlutoHookAdaptationStyles.ValueFirst, "Ep {0}/{1}", 34, 17.0, "v", 2.0, Array.Empty<string>())));
    }

    // ── 2. 카탈로그 로캘 변형 분기 · 독립 1:1 연결 · 충돌 병합 ──

    [Fact]
    public async Task Catalog_BranchesLocaleVariants_WithIndependentBindings_AndSurvivesConflictMerge()
    {
        var dirA = Sub("a");
        using (var cat = await FourLocaleCatalogAsync(dirA))
        {
            var clips = await cat.GetRecentClipsAsync(10);
            Assert.Equal(4, clips.Count);
            var ja = clips.Single(c => c.VideoId == "clip01::ja-JP");
            Assert.Equal("ja-JP", ja.TargetLocale);
            Assert.Equal("clip01", ja.ParentClipId);
            Assert.True(ja.IsLocaleVariant);
            Assert.Equal("시리즈 1/3편", ja.RenderMode);   // 렌더 메타 상속(편 가드)
            Assert.Null(ja.YouTubeVideoId);
            var master = clips.Single(c => c.VideoId == "clip01");
            Assert.Equal("ko-KR", master.TargetLocale);
            Assert.Null(master.ParentClipId);

            // 원본 로캘 분기 → 마스터 그대로(중복 레코드 없음) · 멱등
            Assert.Equal("clip01", (await cat.BranchLocaleVariantAsync("clip01", "ko", "무시"))!.VideoId);
            Assert.Equal("clip01::ja-JP", (await cat.BranchLocaleVariantAsync("clip01", "ja-JP"))!.VideoId);
            Assert.Equal(4, (await cat.GetRecentClipsAsync(10)).Count);
            Assert.Null(await cat.BranchLocaleVariantAsync("nope", "ja-JP"));

            // 로캘별 독립 1:1 연결
            Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("clip01", "AAAAAAAAAAA"));
            Assert.Equal(PlutoYouTubeBindResult.IdTakenByOtherClip, await cat.TryBindYouTubeVideoIdAsync("clip01::ja-JP", "AAAAAAAAAAA"));
            Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("clip01::ja-JP", "BBBBBBBBBBB"));
            Assert.Equal(PlutoYouTubeBindResult.Bound, await cat.TryBindYouTubeVideoIdAsync("clip01::en-US", "DDDDDDDDDDD"));
            Assert.Equal("clip01::ja-JP", (await cat.FindByYouTubeVideoIdAsync("BBBBBBBBBBB"))!.VideoId);

            // 게시 후 제목 동결
            Assert.Equal("猫の秘密 第1/3話", (await cat.BranchLocaleVariantAsync("clip01", "ja-JP", "別のタイトル 第1/3話"))!.Title);
            // 미게시 변형은 제목 갱신
            Assert.Equal("El gato Ep. 1/3", (await cat.BranchLocaleVariantAsync("clip01", "es-ES", "El gato Ep. 1/3"))!.Title);
        }

        // 재적재 — 로캘 · 부모 · 연결 보존
        string currentJson;
        using (var reload = CatalogAt(dirA))
        {
            var es = await reload.FindByVideoIdOrTitleAsync("clip01::es-ES");
            Assert.Equal("es-ES", es!.TargetLocale);
            Assert.Equal("clip01", es.ParentClipId);
            currentJson = File.ReadAllText(reload.StoragePath);
        }

        // 다른 PC: es 변형을 CCCC 에 연결 · 워터마크 반영
        var dirB = Sub("b");
        string otherJson;
        using (var other = await FourLocaleCatalogAsync(dirB))
        {
            Assert.Equal(PlutoYouTubeBindResult.Bound, await other.TryBindYouTubeVideoIdAsync("clip01::es-ES", "CCCCCCCCCCC"));
            Assert.True(await other.UpdateYouTubeWatermarkAsync("clip01::es-ES", new PlutoYouTubeIngestWatermark(500, 0, 66, 48, 4.1, T0.AddDays(2))));
            otherJson = File.ReadAllText(other.StoragePath);
        }

        // 구버전 PC 가 쓴 듯 현재 쪽에서 로캘 필드 제거
        var root = JsonNode.Parse(currentJson)!.AsObject();
        foreach (var c in root["Clips"]!.AsArray()) { c!.AsObject().Remove("TargetLocale"); c.AsObject().Remove("ParentClipId"); }
        var merged = PlutoConflictMergeService.MergeJson(PlutoRenderCatalogService.FileName, root.ToJsonString(), otherJson);
        Assert.NotNull(merged);

        var dirC = Sub("c");
        using var mergedCat = CatalogAt(dirC);
        Directory.CreateDirectory(Path.GetDirectoryName(mergedCat.StoragePath)!);
        File.WriteAllText(mergedCat.StoragePath, merged);
        var all = await mergedCat.GetRecentClipsAsync(10);
        Assert.Equal(4, all.Count);
        var mEs = all.Single(c => c.VideoId == "clip01::es-ES");
        Assert.Equal("CCCCCCCCCCC", mEs.YouTubeVideoId);
        Assert.Equal(500, mEs.YouTubeWatermark!.Views);
        Assert.Equal("es-ES", mEs.TargetLocale);
        Assert.Equal("clip01", mEs.ParentClipId);
        Assert.Equal("BBBBBBBBBBB", all.Single(c => c.VideoId == "clip01::ja-JP").YouTubeVideoId);
        Assert.Equal("AAAAAAAAAAA", all.Single(c => c.VideoId == "clip01").YouTubeVideoId);
        var ids = all.Where(c => c.YouTubeVideoId is not null).Select(c => c.YouTubeVideoId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());   // 1:1 유지
    }

    // ── 3. 로캘 업로드 패키지 · 사이드카 ──

    [Fact]
    public async Task PublishPack_GeneratesLocalizedTitlesHashtagsAndCta_AndLocaleSidecars()
    {
        var svc = new PlutoYouTubePublishPackService();
        PlutoPublishPackRequest Req(string theme, string title, string? locale) => new(theme, title, 1, 3, "pov-walk", new[] { "cat" }) { TargetLocale = locale };

        var ja = svc.Generate(Req("猫の秘密", "猫の秘密 第1/3話", "ja-JP"));
        Assert.Equal("ja-JP", ja.Locale);
        Assert.Equal(1, Occurrences(ja.TitleA.Title, "第1/3話"));
        Assert.Equal(1, Occurrences(ja.TitleB.Title, "第1/3話"));
        Assert.NotEqual(ja.TitleA.Title, ja.TitleB.Title);
        Assert.Contains("#shorts", ja.Hashtags);
        Assert.Contains("#ショート", ja.Hashtags);
        Assert.Equal("最後まで見ると分かる", ja.HookLine);
        Assert.Contains("シリーズ 第1/3話", ja.Description);
        Assert.StartsWith("最後まで見ると分かる\n", ja.Description, StringComparison.Ordinal);
        Assert.Contains("第2話", ja.PinnedComment);
        Assert.False(ja.IsKorean);

        var es = svc.Generate(Req("El secreto del gato", "El secreto del gato Ep. 1/3", "es-ES"));
        Assert.Equal(1, Occurrences(es.TitleA.Title, "Ep. 1/3"));
        Assert.Equal(1, Occurrences(es.TitleB.Title, "Ep. 1/3"));
        Assert.Contains("#cortos", es.Hashtags);
        Assert.Contains("episodio 2", es.PinnedComment);
        Assert.Contains("Serie, episodio 1 de 3", es.Description);

        var en = svc.Generate(Req("The cat secret", "The cat secret Ep.1/3", "en-US"));
        Assert.Equal(1, Occurrences(en.TitleA.Title, "Ep.1/3"));
        Assert.DoesNotContain("#cortos", en.Hashtags);
        Assert.Contains("Series part 1 of 3", en.Description);

        var ko = svc.Generate(Req("고양이의 비밀", "고양이의 비밀 1/3편", "ko-KR"));
        Assert.Contains("1/3편", ko.TitleA.Title);
        Assert.Contains("#쇼츠", ko.Hashtags);
        Assert.True(ko.IsKorean);

        var legacy = svc.Generate(Req("고양이의 비밀", "고양이의 비밀 1/3편", null));
        Assert.Equal(string.Empty, legacy.Locale);
        Assert.DoesNotContain("#쇼츠", legacy.Hashtags);   // 레거시 출력 불변

        // 사이드카 — {base}_{locale}_publish_pack.txt · 레거시 이름과 공존 · BOM + CRLF
        var dir = Sub("side");
        var video = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(video, new byte[] { 0, 1, 2 });
        var rJa = await svc.WriteSidecarAsync(video, ja, "clip01::ja-JP");
        var rEs = await svc.WriteSidecarAsync(video, es, "clip01::es-ES");
        var rLegacy = await svc.WriteSidecarAsync(video, legacy, "clip01");
        Assert.True(rJa.Ok && rEs.Ok && rLegacy.Ok);
        Assert.Equal(Path.Combine(dir, "clip_ja-JP_publish_pack.txt"), rJa.Path);
        Assert.Equal(Path.Combine(dir, "clip_es-ES_publish_pack.txt"), rEs.Path);
        Assert.Equal(Path.Combine(dir, "clip_publish_pack.txt"), rLegacy.Path);
        var bytes = File.ReadAllBytes(rJa.Path!);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("# 로캘: ja-JP\r\n", text);
        Assert.Contains("第1/3話", text);
        Assert.DoesNotContain("\r\r", text);
        Assert.Equal(0, text.Replace("\r\n", "", StringComparison.Ordinal).Count(ch => ch == '\n'));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    // ── 4. 일/서 YouTube Studio CSV/ZIP → 정확한 로캘 변형 연결 ──

    [Fact]
    public async Task StudioNormalizer_ParsesJapaneseAndSpanishExports_AndLinkerBindsExactLocaleVariant()
    {
        const string JaCsv = "動画,動画のタイトル,動画公開時刻,視聴回数,平均再生率(%),インプレッションのクリック率(%),視聴を選択(%)\n"
            + "合計,,,2400,,,\n"
            + "BBBBBBBBBBB,猫の秘密 第1/3話,2026/12/02,1500,55.5,6.2,72.3\n";
        var jaReport = YouTubeStudioAnalyticsNormalizer.Parse(new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(JaCsv)).ToArray(), "表データ.csv");
        Assert.True(jaReport.HeaderRecognized);
        Assert.Equal("ja", jaReport.Language);
        var jr = Assert.Single(jaReport.Rows);
        Assert.Equal("BBBBBBBBBBB", jr.YouTubeVideoId);
        Assert.Equal(72.3, jr.Hook3sRetention, 3);
        Assert.False(jr.HookEstimated);
        Assert.Equal(6.2, jr.Ctr, 3);
        Assert.Equal(55.5, jr.RawAveragePercentViewed, 3);
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero), jr.PublishedAtUtc);
        Assert.Equal("ja", jr.ExportLanguage);

        const string EsCsv = "Vídeo,Título del vídeo,Hora de publicación del vídeo,Visualizaciones,Porcentaje medio visto (%),Tasa de clics de las impresiones (%),Se quedaron a ver (%)\n"
            + "Total,,,1800,,,\n"
            + "CCCCCCCCCCC,El secreto del gato Ep. 1/3,2 dic 2026,\"1.200\",\"48,5\",\"4,1\",\"66,0\"\n";
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var w = new StreamWriter(zip.CreateEntry("Datos del gráfico.csv").Open(), new UTF8Encoding(false)))
                    w.Write("Fecha,Visualizaciones,Se quedaron a ver (%)\n2026-12-02,1200,66\n");
                using (var w = new StreamWriter(zip.CreateEntry("Datos de la tabla.csv").Open(), new UTF8Encoding(false)))
                    w.Write(EsCsv);
            }
            zipBytes = ms.ToArray();
        }
        var esReport = YouTubeStudioAnalyticsNormalizer.Parse(zipBytes, "Contenido 2026-12-05.zip");
        Assert.Equal("es", esReport.Language);
        Assert.Equal("Datos de la tabla.csv", esReport.SourceEntryName);
        var er = Assert.Single(esReport.Rows);
        Assert.Equal(1200, er.Views);
        Assert.Equal(66.0, er.Hook3sRetention, 3);
        Assert.False(er.HookEstimated);
        Assert.Equal(4.1, er.Ctr, 3);
        Assert.Equal(48.5, er.RawAveragePercentViewed, 3);
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero), er.PublishedAtUtc);
        Assert.True(YouTubeStudioAnalyticsNormalizer.LooksLikeStudioExportName("表データ.csv"));
        Assert.True(YouTubeStudioAnalyticsNormalizer.LooksLikeStudioExportName("Datos de la tabla.csv"));

        // 기존 한/영 헤더 판정 불변
        var mapKo = YouTubeStudioAnalyticsNormalizer.MapHeader(new[] { "콘텐츠", "동영상 제목", "조회수", "평균 조회율(%)" }, out var lk);
        Assert.Equal("ko", lk);
        Assert.True(mapKo.IsTable);
        YouTubeStudioAnalyticsNormalizer.MapHeader(new[] { "Content", "Video title", "Views", "Average percentage viewed (%)" }, out var le);
        Assert.Equal("en", le);

        using var cat = await FourLocaleCatalogAsync(Sub("link"));
        var linker = new PlutoYouTubeCatalogLinker(cat);
        var links = await linker.LinkAsync(new[] { jr, er });
        Assert.Equal("clip01::ja-JP", links[0].Clip?.VideoId);
        Assert.Equal(PlutoYouTubeLinkTier.ExactLocal, links[0].Tier);
        Assert.True(links[0].NewlyBound);
        Assert.Equal("clip01::es-ES", links[1].Clip?.VideoId);
        Assert.Equal("BBBBBBBBBBB", (await cat.FindByVideoIdOrTitleAsync("clip01::ja-JP"))!.YouTubeVideoId);
        Assert.Equal("CCCCCCCCCCC", (await cat.FindByVideoIdOrTitleAsync("clip01::es-ES"))!.YouTubeVideoId);
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("clip01"))!.YouTubeVideoId);          // 마스터 · en 무접촉
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("clip01::en-US"))!.YouTubeVideoId);
    }

    // ── 5. 🧠 자체 추가 ① — 정확 제목 일치의 교차 로캘 오연결(첫 적중 클립) 재라우팅/거부 ──

    [Fact]
    public async Task CoArchitect_ExactTitleCrossLocaleHijack_IsRoutedToLocaleSibling_OrRejectedWhenAmbiguous()
    {
        using var cat = CatalogAt(Sub("hijack"));
        await cat.RecordRenderedClipAsync(Master("brand", "PLUTO Ep.1/3") with { TargetLocale = "en-US" });
        Assert.NotNull(await cat.BranchLocaleVariantAsync("brand", "es-ES", "PLUTO Ep. 1/3"));   // 정규형 둘 다 "plutoep13"
        await cat.RecordRenderedClipAsync(Master("solo", "PLUTO 단독", "Ambient15s") with { RenderedAtUtc = T0.AddMinutes(1) });
        await cat.RecordRenderedClipAsync(Master("plain", "Gatos", "Ambient15s") with { TargetLocale = "en-US", RenderedAtUtc = T0.AddMinutes(2) });
        Assert.NotNull(await cat.BranchLocaleVariantAsync("plain", "es-ES", "Gatos"));          // 비시리즈 · 같은 제목
        var clips = await cat.GetRecentClipsAsync(20);
        Assert.Equal("plutoep13", PlutoRenderCatalogService.NormalizeKey("PLUTO Ep. 1/3"));
        Assert.Equal("plutoep13", PlutoRenderCatalogService.NormalizeKey("PLUTO Ep.1/3"));

        // 첫 적중 클립이 en 마스터여도 es 토큰 행은 es 형제로
        var enMaster = clips.Single(c => c.VideoId == "brand");
        YouTubeAnalyticsRow Row(string? yt, string title, string? lang) => new(yt, title, T0.AddDays(1), 900, 60, false, 45, 45, 4.0, false) { ExportLanguage = lang };
        Assert.Equal("brand::es-ES", PlutoYouTubeCatalogLinker.RouteExactTitleToLocale(enMaster, Row(null, "PLUTO Ep. 1/3", "en"), clips).Clip?.VideoId);
        Assert.Equal("brand", PlutoYouTubeCatalogLinker.RouteExactTitleToLocale(enMaster, Row(null, "PLUTO Ep.1/3", "es"), clips).Clip?.VideoId);
        // 단일 로캘 클립은 기존 동작
        var solo = clips.Single(c => c.VideoId == "solo");
        Assert.Same(solo, PlutoYouTubeCatalogLinker.RouteExactTitleToLocale(solo, Row(null, "PLUTO 단독", "ja"), clips).Clip);
        // 같은 제목 형제 · 강한 신호 없음 → 헤더 언어로 고름 · 언어도 없으면 모호 거부
        var plain = clips.Single(c => c.VideoId == "plain");
        Assert.Equal("plain::es-ES", PlutoYouTubeCatalogLinker.RouteExactTitleToLocale(plain, Row(null, "Gatos", "es"), clips).Clip?.VideoId);
        var amb = PlutoYouTubeCatalogLinker.RouteExactTitleToLocale(plain, Row(null, "Gatos", null), clips);
        Assert.Null(amb.Clip);
        Assert.Contains("모호", amb.Reason);

        // 통합 — LinkAsync 가 es 형제에 연결하고 en 마스터는 건드리지 않음 · 모호 행은 미매칭(대기열)
        var links = await new PlutoYouTubeCatalogLinker(cat).LinkAsync(new[] { Row("EEEEEEEEEEE", "PLUTO Ep. 1/3", "en"), Row("FFFFFFFFFFF", "Gatos", null) });
        Assert.Equal("brand::es-ES", links[0].Clip?.VideoId);
        Assert.False(links[1].IsMatched);
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("brand"))!.YouTubeVideoId);
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("plain"))!.YouTubeVideoId);
        Assert.Null((await cat.FindByVideoIdOrTitleAsync("plain::es-ES"))!.YouTubeVideoId);
    }

    // ── 6. 🧠 자체 추가 ② — CJK/스페인어 편 토큰 인식(편 가드 · 토큰 잔여물) ──

    [Fact]
    public void CoArchitect_LocalizedEpisodeTokens_KeepEpisodeGuardAndCleanTokens()
    {
        Assert.Equal(2, PlutoYouTubeCatalogLinker.ExtractEpisode("猫の秘密 第2/3話"));
        Assert.Equal(3, PlutoYouTubeCatalogLinker.ExtractEpisode("猫の秘密 第3話"));
        Assert.Equal(2, PlutoYouTubeCatalogLinker.ExtractEpisode("El gato Ep. 2/3"));
        Assert.Equal(2, PlutoYouTubeCatalogLinker.ExtractEpisode("The cat Ep.2/3"));
        Assert.Equal(4, PlutoYouTubeCatalogLinker.ExtractEpisode("El gato Episodio 4"));
        Assert.Equal(2, PlutoYouTubeCatalogLinker.ExtractEpisode("고양이 2/3편"));   // 레거시
        Assert.Equal(3, PlutoYouTubeCatalogLinker.ExtractEpisode("고양이 제3편"));
        Assert.Equal(4, PlutoYouTubeCatalogLinker.ExtractEpisode("cats EP 4"));
        Assert.Equal(0.0, PlutoYouTubeCatalogLinker.TitleSimilarity("猫の秘密 第2/3話", "猫の秘密 第1/3話"));   // 🛡️ 다른 편
        Assert.True(PlutoYouTubeCatalogLinker.TitleSimilarity("猫の秘密 第1/3話", "猫の秘密 第1/3話") >= 0.99);
        Assert.Equal(new[] { "猫の秘密" }, PlutoYouTubeCatalogLinker.Tokenize("猫の秘密 第1/3話").ToArray());
        Assert.Equal(new[] { "el", "gato" }, PlutoYouTubeCatalogLinker.Tokenize("El gato Ep. 1/3").ToArray());   // "/3" 잔여 토큰 없음
        Assert.Equal("猫の秘密", PlutoYouTubePublishPackService.StripEpisodeToken("猫の秘密 第1/3話"));
        Assert.Equal("El gato", PlutoYouTubePublishPackService.StripEpisodeToken("El gato Ep. 1/3"));
        Assert.Equal("고양이의 비밀", PlutoYouTubePublishPackService.StripEpisodeToken("고양이의 비밀 1/3편"));

        // 편 가드: ja 2편 행은 ja 1편 변형에 붙지 않음(카탈로그 RenderMode 1/3 · 제목 第1/3話)
        var ep1 = Master("m", "猫の秘密 第1/3話") with { VideoId = "m::ja-JP", TargetLocale = "ja-JP", ParentClipId = "m" };
        Assert.True(PlutoYouTubeCatalogLinker.EpisodeConflict("猫の秘密 第2/3話", ep1));
        Assert.False(PlutoYouTubeCatalogLinker.EpisodeConflict("猫の秘密 第1/3話", ep1));

        // 제목 로캘 추정(강한 신호)
        Assert.Equal("ja-JP", PlutoMultiLanguageChannelService.InferTitleLocale("猫の秘密 第1/3話"));
        Assert.Equal("ko-KR", PlutoMultiLanguageChannelService.InferTitleLocale("Cat secret 1/3편"));
        Assert.Equal("es-ES", PlutoMultiLanguageChannelService.InferTitleLocale("Gato Ep. 1/3"));
        Assert.Equal("en-US", PlutoMultiLanguageChannelService.InferTitleLocale("Cat Ep.1/3"));
        Assert.Equal("es-ES", PlutoMultiLanguageChannelService.InferTitleLocale("¿Dónde está el gato?"));
        Assert.Null(PlutoMultiLanguageChannelService.InferTitleLocale("Cat secret"));
    }

    // ── 7. 🧠 자체 추가 ③ — 스페인어 Studio 날짜(게시일 null → Tier 2 불가) · 강한 로캘 필터 퍼지 연결 ──

    [Fact]
    public async Task CoArchitect_SpanishStudioDates_ParseSoFuzzyTierRoutesToSpanishVariant()
    {
        Assert.True(YouTubeStudioAnalyticsNormalizer.TryParsePublishDate("2 dic 2026", out var d1));
        Assert.Equal(new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.Zero), d1);
        Assert.True(YouTubeStudioAnalyticsNormalizer.TryParsePublishDate("15 de agosto de 2026", out var d2));
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), d2);
        Assert.True(YouTubeStudioAnalyticsNormalizer.TryParsePublishDate("3 sept. 2026", out var d3));
        Assert.Equal(9, d3.Month);
        Assert.True(YouTubeStudioAnalyticsNormalizer.TryParsePublishDate("4 ene 2027", out var d4));
        Assert.Equal(1, d4.Month);
        Assert.False(YouTubeStudioAnalyticsNormalizer.TryParsePublishDate("31 feb 2026", out _));
        Assert.False(YouTubeStudioAnalyticsNormalizer.TryParseSpanishDate("2 dic", out _));
        Assert.True(YouTubeStudioAnalyticsNormalizer.TryParsePublishDate("Nov 3, 2026", out _));   // 영어 불변

        using var cat = await FourLocaleCatalogAsync(Sub("fuzzy"));
        // 정확 제목이 아닌(#shorts 추가) 스페인어 행 → Tier 2 — en/ko/ja 형제는 강한 로캘(es)로 제외
        var csv = "Vídeo,Título del vídeo,Hora de publicación del vídeo,Visualizaciones,Se quedaron a ver (%)\n"
            + "GGGGGGGGGGG,El secreto del gato Ep. 1/3 #shorts,2 dic 2026,700,\"61,5\"\n";
        var report = YouTubeStudioAnalyticsNormalizer.ParseCsv(csv, "Datos de la tabla.csv");
        var row = Assert.Single(report.Rows);
        Assert.NotNull(row.PublishedAtUtc);
        var links = await new PlutoYouTubeCatalogLinker(cat).LinkAsync(report.Rows);
        Assert.Equal(PlutoYouTubeLinkTier.Fuzzy, links[0].Tier);
        Assert.Equal("clip01::es-ES", links[0].Clip?.VideoId);

        // 약한 신호(헤더 언어)는 동점만 가름 — 강한 신호 없는 비시리즈 동명 형제
        var clips = new List<PlutoRenderedClipRecord>
        {
            Master("p", "Gatos felices", "Ambient15s") with { TargetLocale = "en-US" },
            Master("p", "Gatos felices", "Ambient15s") with { VideoId = "p::es-ES", TargetLocale = "es-ES", ParentClipId = "p" },
        };
        var tie = new YouTubeAnalyticsRow("HHHHHHHHHHH", "Gatos felices hoy", T0.AddDays(1), 500, 60, false, 45, 45, 4, false);
        Assert.Null(PlutoYouTubeCatalogLinker.SelectFuzzyCandidate(tie, clips).Clip);   // 모호
        Assert.Equal("p::es-ES", PlutoYouTubeCatalogLinker.SelectFuzzyCandidate(tie with { ExportLanguage = "es" }, clips).Clip?.VideoId);
        Assert.Equal("p", PlutoYouTubeCatalogLinker.SelectFuzzyCandidate(tie with { ExportLanguage = "en" }, clips).Clip?.VideoId);
    }

    // ── 8. 🧠 자체 추가 ④ — 변형 계보는 ID 에서 파생(구버전 PC 왕복 · 위조 필드 · 경로 주입) ──

    [Fact]
    public async Task CoArchitect_VariantLineage_IsDerivedFromId_AndLocaleInputsAreHardened()
    {
        var dir = Sub("lineage");
        string path;
        using (var seed = CatalogAt(dir)) path = seed.StoragePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var at = T0.ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(path, "{\"Version\":1,\"Clips\":["
            + "{\"VideoId\":\"m1\",\"Title\":\"t\",\"RenderedAtUtc\":\"" + at + "\",\"TargetLocale\":\"../../x\",\"ParentClipId\":\"zzz\"},"
            + "{\"VideoId\":\"m1::ja-JP\",\"Title\":\"猫\",\"RenderedAtUtc\":\"" + at + "\",\"TargetLocale\":\"es-ES\"},"
            + "{\"VideoId\":\"m1::es-ES\",\"Title\":\"gato\",\"RenderedAtUtc\":\"" + at + "\"},"
            + "{\"VideoId\":\"m2::bogus$\",\"Title\":\"x\",\"RenderedAtUtc\":\"" + at + "\",\"TargetLocale\":\"ja\"}]}");
        using var cat = CatalogAt(dir);
        var clips = await cat.GetRecentClipsAsync(10);
        var m1 = clips.Single(c => c.VideoId == "m1");
        Assert.Equal("ko-KR", m1.TargetLocale);          // 위조 로캘 → 기본
        Assert.Null(m1.ParentClipId);                    // 마스터는 부모 없음
        var ja = clips.Single(c => c.VideoId == "m1::ja-JP");
        Assert.Equal("ja-JP", ja.TargetLocale);          // 필드 불일치 → ID 우선
        Assert.Equal("m1", ja.ParentClipId);
        var es = clips.Single(c => c.VideoId == "m1::es-ES");
        Assert.Equal("es-ES", es.TargetLocale);          // 필드 없음(구버전 PC) → ID 파생
        Assert.Equal("m1", es.ParentClipId);
        var bogus = clips.Single(c => c.VideoId == "m2::bogus$");
        Assert.Null(bogus.ParentClipId);
        Assert.Equal("ja-JP", bogus.TargetLocale);

        // 변형의 변형 → 마스터 기준 평탄화 · 위조 로캘 거부
        Assert.Equal("m1::es-ES", PlutoMultiLanguageChannelService.BuildVariantClipId("m1::ja-JP", "es"));
        Assert.Equal(new PlutoLocaleVariantId("m1", "es-ES"), PlutoMultiLanguageChannelService.ParseVariantClipId("m1::ja-JP::es-ES"));
        Assert.Equal("m1::en-US", (await cat.BranchLocaleVariantAsync("m1::ja-JP", "en"))!.VideoId);
        Assert.Null(await cat.BranchLocaleVariantAsync("m1", "../x"));
        Assert.Null(await cat.BranchLocaleVariantAsync("m1", "ja-JP\\..\\x"));
        Assert.Null(PlutoMultiLanguageChannelService.NormalizeLocale("ja/JP"));
        Assert.Null(PlutoMultiLanguageChannelService.NormalizeLocale("x"));
        Assert.Equal("ja-JP", PlutoMultiLanguageChannelService.NormalizeLocale("JA_jp"));
        Assert.Null(PlutoYouTubePublishPackService.TryGetSidecarPath(Path.Combine(_dir, "v.mp4"), "..\\evil", out var why));
        Assert.False(string.IsNullOrEmpty(why));
        Assert.Equal(Path.Combine(_dir, "v_en-US_publish_pack.txt"), PlutoYouTubePublishPackService.TryGetSidecarPath(Path.Combine(_dir, "v.mp4"), "en", out _));
    }

    // ── 9. 🧠 자체 추가 ⑤ — 문자소 안전 줄바꿈 · 금칙 · 장면 전환 TTS 가드 ──

    [Fact]
    public void CoArchitect_GraphemeSafeWrapping_Kinsoku_AndSceneCutTtsGuard()
    {
        var svc = new PlutoMultiLanguageChannelService();
        // 분해형 악센트(o + U+0301) → NFC 합성 · 결합 문자가 줄머리에 홀로 오지 않음
        var decomposed = "La canci" + "ó" + "n más bonita del mundo entero para todos nosotros hoy y mañana";
        var es = svc.WrapSubtitle("es-ES", decomposed);
        Assert.Contains("canción", string.Join(" ", es.Lines));
        Assert.Contains("mañana", string.Join(" ", es.Lines));
        Assert.All(es.Lines, l =>
        {
            Assert.True(PlutoMultiLanguageChannelService.TextLength(l) <= 34, l);
            Assert.NotEqual(UnicodeCategory.NonSpacingMark, CharUnicodeInfo.GetUnicodeCategory(l, 0));
        });
        // 줄 폭보다 긴 단어 → 강제 분할(넘침 금지)
        var longWord = svc.WrapSubtitle("en-US", "Supercalifragilisticexpialidociousextraordinary wow");
        Assert.All(longWord.Lines, l => Assert.True(l.Length <= 34, l));
        Assert.Equal("Supercalifragilisticexpialidociousextraordinary", longWord.Lines[0] + longWord.Lines[1].Split(' ')[0]);
        // 이모지 서로게이트 쌍 미분할
        var emoji = svc.WrapSubtitle("ja-JP", string.Concat(Enumerable.Repeat("😺", 20)));
        Assert.Equal(2, emoji.Lines.Count);
        Assert.All(emoji.Lines, l => { Assert.False(char.IsHighSurrogate(l[^1])); Assert.False(char.IsLowSurrogate(l[0])); });
        Assert.Equal(15, PlutoMultiLanguageChannelService.TextLength(emoji.Lines[0]));
        // 금칙: 다음 줄이 "。"로 시작하지 않도록 당겨 내림
        var kin = svc.WrapSubtitle("ja-JP", "あいうえおかきくけこさしすせそ。たちつてと");
        Assert.Equal("あいうえおかきくけこさしす", kin.Lines[0][..13]);
        Assert.StartsWith("そ。", kin.Lines[1], StringComparison.Ordinal);
        Assert.All(kin.Lines, l => Assert.DoesNotContain(l[0], "、。」ー"));
        var open = svc.WrapSubtitle("ja-JP", "あいうえおかきくけこさしすせ「そたちつてと」");
        Assert.All(open.Lines, l => Assert.False(l.EndsWith('「'), l));

        // 장면 전환 가드 · 비정상 입력
        var tiny = svc.ComputeTtsBudget("en-US", "hello world", 0.3);
        Assert.Equal(0.0, tiny.AvailableSeconds);
        Assert.True(tiny.RequiresCondensation);
        Assert.Equal(0, tiny.MaxUnitsAtMaxRate);
        var nan = svc.ComputeTtsBudget("ja-JP", "猫", double.NaN);
        Assert.True(nan.RequiresCondensation);
        var empty = svc.ComputeTtsBudget("es-ES", "  ¡! 😺 ", 2.0);
        Assert.Equal(0, empty.SpeechUnits);
        Assert.True(empty.FitsSegment);
        var guard = svc.ComputeTtsBudget("en-US", new string('a', 34), 2.0);   // 2.0s 그대로면 1.0x 로 맞지만 가드 0.15s 때문에 가속
        Assert.Equal(2.0 - PlutoMultiLanguageChannelService.SceneCutGuardSeconds, guard.AvailableSeconds, 6);
        Assert.True(guard.SpeakingRate > 1.0);
        Assert.True(guard.FitsSegment);
        Assert.Equal(8, PlutoMultiLanguageChannelService.CountSpeechUnits("¡Hola, gato!"));
    }
}
