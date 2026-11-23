using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

/// <summary>
/// 📖 Step 34 — 다국어 자막 번역: 가장 긴 용어집 · 고유명사 자리표시자 보존 · 용어집 PC 간 병합(레거시 호환) · 비파괴 로캘 트랙(한글 잔존 경고 해제 · TTS 재적합) ·
/// 오프라인/회로 차단 폴백(네트워크 0) · 자체 추가(자리표시자 무결성/되돌려주기 가드 · 낡은 트랙 펜스 · 삭제 표식 LWW + 시즌 무관 범위 · 글자 예산/톤 전달).
/// </summary>
public sealed class PlutoSubtitleTranslationAndGlossaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_subtrans_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    public PlutoSubtitleTranslationAndGlossaryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly PlutoGlossaryEntry[] Glossary =
    {
        new("민준", "Minjun", "ミンジュン", "Minjun"),
        new("민준이네 카페", "Minjun's Café", "ミンジュンのカフェ", "Café de Minjun"),
    };

    private static List<SubtitleCue> KoreanCues() => new()
    {
        SubtitleCue.Precise(0.0, 3.0, "민준이네 카페에서 민준이 @pluto_kr 와 BTS 노래를"),
        SubtitleCue.Precise(3.0, 6.0, "오늘은 민준이 직접 커피를 내려요"),
        SubtitleCue.Precise(6.0, 9.0, "끝까지 보면 반전이 있어요"),
    };

    /// <summary>로캘별 결정론 번역기 — 자리표시자를 공백 · 대소문자 · 전각 괄호로 흔들어 돌려준다.</summary>
    private static PlutoTranslationProvider FakeProvider(List<PlutoTranslationRequest>? log = null) => (req, _) =>
    {
        lock (log ?? new List<PlutoTranslationRequest>()) log?.Add(req);
        var m = req.MaskedText;
        var tail = m.Contains("[[G4]]") ? " [[G4]]" : "";
        string? outText = req.TargetLocale switch
        {
            "en-US" when m.Contains("[[G3]]") => "At [[g0]] , [[G1]] sings a [[ G3 ]] song with [[G2]]" + tail,
            "ja-JP" when m.Contains("[[G3]]") => "【【Ｇ０】】で[[G1]] は[[G2]]と[[G3]]の歌を" + tail.Trim(),
            "es-ES" when m.Contains("[[G3]]") => "En [[G0]], [[G1]] canta [[G3]] con [[G2]]" + tail,
            "en-US" when m.Contains("[[G0]]") => "Today [[G0]] brews the coffee himself",
            "ja-JP" when m.Contains("[[G0]]") => "今日は[[G0]]が自らコーヒーを淹れます",
            "es-ES" when m.Contains("[[G0]]") => "Hoy [[G0]] prepara el café él mismo",
            "en-US" => "Watch to the end for a twist",
            "ja-JP" => "最後まで見ると驚きが",
            "es-ES" => "Mira hasta el final",
            _ => null,
        };
        return Task.FromResult(outText);
    };

    // ── 1. 가장 긴 용어집 · 고유명사 자리표시자 가림/복원(en · ja · es) ──

    [Fact]
    public async Task LongestMatchGlossaryAndProperNounSentinels_RestoreExactSurfaces_ForAllThreeLocales()
    {
        var text = "민준이네 카페에서 민준이 @pluto_kr 와 BTS 노래를 #shorts";
        var mask = PlutoGlossaryShield.Mask(text, Glossary, "en-US");
        Assert.Equal("[[G0]]에서 [[G1]]이 [[G2]] 와 [[G3]] 노래를 [[G4]]", mask.MaskedText);   // 긴 용어가 짧은 용어에 쪼개지지 않음
        Assert.Equal("민준이네 카페", mask.Tokens[0].Source);
        Assert.Equal("Minjun's Café", mask.Tokens[0].Replacement);
        Assert.True(mask.Tokens[0].IsGlossary);
        Assert.Equal("Minjun", mask.Tokens[1].Replacement);
        Assert.Equal("@pluto_kr", mask.Tokens[2].Replacement);   // 핸들 · 대문자 라틴 · 태그는 원문 그대로
        Assert.Equal("BTS", mask.Tokens[3].Replacement);
        Assert.Equal("#shorts", mask.Tokens[4].Replacement);
        Assert.Equal("ミンジュンのカフェ", PlutoGlossaryShield.Mask(text, Glossary, "ja-JP").Tokens[0].Replacement);

        var cues = new List<SubtitleCue> { SubtitleCue.Precise(0, 4, text) };
        var svc = new PlutoSubtitleLocalizationService(new PlutoMultiLanguageChannelService(), FakeProvider(), providerUsesNetwork: false);
        var en = (await svc.TranslateAsync("c1", cues, "en-US", Glossary)).Track!;
        Assert.Equal("At Minjun's Café, Minjun sings a BTS song with @pluto_kr #shorts", en.Texts[0].Replace(" ,", ","));
        Assert.Equal(1, en.ProviderCueCount);
        var ja = (await svc.TranslateAsync("c1", cues, "ja-JP", Glossary)).Track!;
        Assert.Equal("ミンジュンのカフェでミンジュンは@pluto_krとBTSの歌を#shorts", ja.Texts[0]);   // 전각 괄호 · 둘레 공백 수리
        var es = (await svc.TranslateAsync("c1", cues, "es-ES", Glossary)).Track!;
        Assert.Equal("En Café de Minjun, Minjun canta BTS con @pluto_kr #shorts", es.Texts[0]);
        Assert.Equal(0, es.FallbackCueCount);
        // 결정론 폴백(번역기 없음)도 같은 가장 긴 일치 규칙
        Assert.Equal("Minjun's Café에서 Minjun이 @pluto_kr 와 BTS 노래를 #shorts", PlutoGlossaryShield.ApplyGlossary(mask));
    }

    // ── 2. 용어집 PC 간 병합 · 레거시 호환 · 불변식 ──

    [Fact]
    public async Task ConflictMerge_MergesGlossaryAcrossPcs_AndStaysBackwardCompatibleWithLegacyArcFiles()
    {
        Assert.Equal(9, PlutoDriveHealthService.CorePlutoFileCount);
        Assert.Equal(6, PlutoConflictMergeService.MergeableHubs.Count);
        Assert.Equal(2, PlutoDriveHealthService.CompanionSyncFiles.Length);

        const string legacy = "{\"Version\":1,\"Series\":[{\"Key\":\"cafe#3\",\"Title\":\"카페\",\"Total\":3,\"UpdatedUtc\":\"2026-12-01T09:00:00+00:00\",\"Episodes\":[{\"N\":1,\"VideoId\":\"v1\"}]}]}";
        var scope = PlutoSeriesArcMemoryService.GlossaryScopeOf("카페");
        var pcB = "{\"Version\":1,\"Series\":[],\"Glossary\":{\"" + scope + "\":["
            + "{\"Source\":\"민준\",\"En\":\"Minjun\",\"Ja\":\"ミンジュン\",\"UpdatedUtc\":\"2026-12-01T10:00:00+00:00\"},"
            + "{\"Source\":\"라떼\",\"En\":\"latte\",\"UpdatedUtc\":\"2026-12-01T10:00:00+00:00\"}]}}";
        var pcC = "{\"Version\":1,\"Series\":[],\"Glossary\":{\"" + scope + "\":["
            + "{\"Source\":\"민준\",\"En\":\"Min-jun\",\"UpdatedUtc\":\"2026-12-01T11:00:00+00:00\"},"
            + "{\"Source\":\"라떼\",\"UpdatedUtc\":\"2026-12-01T12:00:00+00:00\",\"Deleted\":true}],"
            + "\"*\":[{\"Source\":\"구독\",\"En\":\"subscribe\",\"Es\":\"suscríbete\",\"UpdatedUtc\":\"2026-12-01T09:30:00+00:00\"}]}}";

        // 레거시 ↔ 용어집 사본: 시리즈/편은 그대로 · 용어집은 채택
        var m1 = PlutoConflictMergeService.MergeJson(PlutoSeriesArcMemoryService.FileName, legacy, pcB)!;
        var j1 = JsonNode.Parse(m1)!.AsObject();
        Assert.Equal("cafe#3", j1["Series"]![0]!["Key"]!.GetValue<string>());
        Assert.Equal(2, j1["Glossary"]![scope]!.AsArray().Count);
        // 레거시끼리는 용어집 키를 만들지 않음(양방향 호환)
        Assert.Null(JsonNode.Parse(PlutoConflictMergeService.MergeJson(PlutoSeriesArcMemoryService.FileName, legacy, legacy)!)!["Glossary"]);

        // 3자 병합 — LWW · 삭제 표식 전파 · 전역 범위 합집합 · 순서 무관(결정론)
        var m2 = PlutoConflictMergeService.MergeJson(PlutoSeriesArcMemoryService.FileName, m1, pcC)!;
        var m2r = PlutoConflictMergeService.MergeJson(PlutoSeriesArcMemoryService.FileName, pcC, m1)!;
        Assert.Equal(JsonNode.Parse(m2)!["Glossary"]!.ToJsonString(), JsonNode.Parse(m2r)!["Glossary"]!.ToJsonString());

        var path = Path.Combine(_dir, PlutoSeriesArcMemoryService.FileName);
        File.WriteAllText(path, m2);
        using var arc = new PlutoSeriesArcMemoryService(path) { UtcNowProvider = () => T0.AddDays(1) };
        await arc.EnsureLoadedAsync();
        var g = arc.GetGlossary("카페 1/3편");   // 같은 테마의 편 제목 → 같은 범위
        Assert.Contains(g, e => e.Source == "민준" && e.EnUs == "Min-jun" && e.JaJp is null);   // 더 최근 기록이 통째로 이김
        Assert.DoesNotContain(g, e => e.Source == "라떼");                                       // 삭제가 되살아나지 않음
        Assert.Contains(g, e => e.Source == "구독" && e.EsEs == "suscríbete");                   // 전역 항목 상속
        Assert.Single(arc.GetSeriesArcs());                                                      // 시리즈 아크 보존

        // 레거시 파일 적재 → 용어집 없음 · 저장해도 Glossary 키 없음
        var legacyPath = Path.Combine(_dir, "legacy_" + PlutoSeriesArcMemoryService.FileName);
        File.WriteAllText(legacyPath, legacy);
        using var arc2 = new PlutoSeriesArcMemoryService(legacyPath);
        await arc2.EnsureLoadedAsync();
        Assert.Empty(arc2.GetGlossary("카페"));
        await arc2.SetEscalationModeAsync(PlutoSeriesEscalationMode.RecommendOnly);
        Assert.DoesNotContain("Glossary", File.ReadAllText(legacyPath), StringComparison.Ordinal);
    }

    // ── 3. 비파괴 로캘 트랙 · 한글 잔존 경고 해제 · TTS 재적합 ──

    [Fact]
    public async Task NonDestructiveLocaleTrack_PreservesKoreanSource_ClearsUntranslatedWarning_AndRefitsTts()
    {
        var channels = new PlutoMultiLanguageChannelService();
        var state = PlutoSubtitleOverlayState.FromBase(KoreanCues());
        var displayed = PlutoSeriesSubtitleOverlay.Compose(state, null, PlutoSeriesEscalationMode.AutoRewrite, 1, 3);
        var snapshot = new List<SubtitleCue>(displayed);

        var before = PlutoLocaleSubtitleFitter.Project(displayed, channels, "ja-JP", 1, 3);
        Assert.Equal(3, before.UntranslatedCueCount);
        Assert.Contains("번역 안 된 한글", before.WarningKorean, StringComparison.Ordinal);

        var svc = new PlutoSubtitleLocalizationService(channels, FakeProvider(), providerUsesNetwork: false);
        var result = await svc.TranslateAsync("series:cafe#3|ep1", displayed, "ja-JP", Glossary, 1, 3);
        Assert.Equal(PlutoTranslationStatus.Translated, result.Status);
        var track = svc.GetTrack("series:cafe#3|ep1", "ja");   // 로캘 정규화 조회
        Assert.Same(result.Track, track);
        Assert.Null(svc.GetTrack("series:cafe#3|ep1", "es-ES"));   // 로캘별 독립 보관

        var after = PlutoLocaleSubtitleFitter.Project(displayed, channels, "ja-JP", 1, 3, track);
        Assert.True(after.UsedTranslationTrack);
        Assert.Equal(0, after.UntranslatedCueCount);
        Assert.DoesNotContain("번역 안 된", after.WarningKorean, StringComparison.Ordinal);
        Assert.StartsWith("第1/3話 · ", after.Cues[0].Text.Replace("\n", ""), StringComparison.Ordinal);   // 로캘 편 토큰 재장식
        Assert.Contains("ミンジュンのカフェ", after.Cues[0].Text.Replace("\n", ""), StringComparison.Ordinal);
        Assert.False(after.HasTtsOverflow);
        Assert.InRange(after.SpeakingRate, PlutoMultiLanguageChannelService.MinSpeakingRate, PlutoMultiLanguageChannelService.MaxSpeakingRate);
        foreach (var f in after.Fits) Assert.False(f.Untranslated);

        // 원문 불변(표시 자막 · 편집 원문)
        Assert.Equal(snapshot, displayed);
        Assert.Equal("민준이네 카페에서 민준이 @pluto_kr 와 BTS 노래를", state.BaseCues[0].Text);
        // 한국어 투영은 트랙과 무관하게 원문 그대로
        Assert.True(PlutoLocaleSubtitleFitter.Project(displayed, channels, "ko-KR", 1, 3, track).IsIdentity);
        // 다른 로캘 트랙은 적용되지 않음
        Assert.False(PlutoLocaleSubtitleFitter.Project(displayed, channels, "es-ES", 1, 3, track).UsedTranslationTrack);
    }

    // ── 4. 오프라인 · 회로 차단 · 미구성 → 네트워크 0 · 용어집 폴백 ──

    [Fact]
    public async Task OfflineUnconfiguredOrCircuitOpen_MakesZeroProviderCalls_AndFallsBackToGlossary()
    {
        Assert.Equal("off", Environment.GetEnvironmentVariable(PlutoDriveStorageManager.DriveEnvVar));   // 테스트 하네스
        var calls = 0;
        PlutoTranslationProvider network = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult<string?>("SHOULD NOT BE CALLED"); };
        var channels = new PlutoMultiLanguageChannelService();

        // ① PROMPTFLOW_PLUTO_DRIVE=off + 네트워크 번역기 → 호출 0
        var offline = new PlutoSubtitleLocalizationService(channels, network);
        Assert.False(offline.IsProviderReachable);
        var r = await offline.TranslateAsync("m", KoreanCues(), "en-US", Glossary);
        Assert.Equal(PlutoTranslationStatus.OfflineFallback, r.Status);
        Assert.Equal("Minjun's Café에서 Minjun이 @pluto_kr 와 BTS 노래를", r.Track!.Texts[0]);
        Assert.Equal(3, r.Track.FallbackCueCount);
        // ② 번역기 미주입 · ③ 미구성 · ④ 회로 열림
        Assert.Equal(PlutoTranslationStatus.OfflineFallback, (await new PlutoSubtitleLocalizationService(channels).TranslateAsync("m", KoreanCues(), "es-ES", Glossary)).Status);
        var unconfigured = new PlutoSubtitleLocalizationService(channels, network) { NetworkAllowed = () => true, ProviderConfigured = () => false };
        Assert.Equal(PlutoTranslationStatus.OfflineFallback, (await unconfigured.TranslateAsync("m", KoreanCues(), "ja-JP", Glossary)).Status);
        var breaker = new ProviderCircuitBreaker { Clock = () => T0 };
        for (var i = 0; i < ProviderCircuitBreaker.FailureThreshold; i++) breaker.RecordTransientFailure(PlutoSubtitleLocalizationService.ProviderName, "boom");
        var tripped = new PlutoSubtitleLocalizationService(channels, network, breaker) { NetworkAllowed = () => true };
        Assert.False(tripped.IsProviderReachable);
        Assert.Equal(PlutoTranslationStatus.OfflineFallback, (await tripped.TranslateAsync("m", KoreanCues(), "en-US", Glossary)).Status);
        Assert.Equal(0, calls);

        // ⑤ 실패가 이어지면 회로가 열려 남은 큐는 호출 없이 즉시 폴백(throw 없음)
        var failing = 0;
        PlutoTranslationProvider throwing = (_, _) => { Interlocked.Increment(ref failing); throw new InvalidOperationException("503"); };
        var cues = new List<SubtitleCue>();
        for (var i = 0; i < 6; i++) cues.Add(SubtitleCue.Precise(i * 2, i * 2 + 2, "민준 컷 " + i));
        var breaker2 = new ProviderCircuitBreaker { Clock = () => T0 };
        var flaky = new PlutoSubtitleLocalizationService(channels, throwing, breaker2) { NetworkAllowed = () => true };
        var fr = await flaky.TranslateAsync("m", cues, "en-US", Glossary);
        Assert.Equal(ProviderCircuitBreaker.FailureThreshold, failing);
        Assert.Equal(6, fr.Track!.FallbackCueCount);
        Assert.Equal("Minjun 컷 5", fr.Track.Texts[5]);

        // ⑥ 기본 LLM 번역기 — 미구성이면 LLM 자체를 호출하지 않음 · JSON 스키마 즉시 검증
        var llm = new FakeLlm { Available = false };
        Assert.Null(await PlutoSubtitleLocalizationService.FromLlm(llm)(new PlutoTranslationRequest("ko-KR", "en-US", "x", 10, 1, "t", Array.Empty<string>()), default));
        Assert.Equal(0, llm.Calls);
        Assert.Equal("hi", PlutoSubtitleLocalizationService.ParseProviderJson("{\"text\":\"hi\"}"));
        Assert.Null(PlutoSubtitleLocalizationService.ParseProviderJson("{\"txt\":\"hi\"}"));
        Assert.Null(PlutoSubtitleLocalizationService.ParseProviderJson("not json"));
    }

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool Available { get; set; }
        public int Calls;
        public bool IsAvailable => Available;
        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult<string?>("{\"text\":\"x\"}"); }
    }

    // ── 5a. 자체 추가 ① 자리표시자 무결성 · 되돌려주기 가드 · 원문 [[…]] 충돌 ──

    [Fact]
    public async Task SelfAddition_SentinelIntegrityAndEchoGuard_RejectCorruptedProviderOutputPerCue()
    {
        var mask = PlutoGlossaryShield.Mask("민준이네 카페와 민준", Glossary, "en-US");   // G0 · G1
        Assert.Null(PlutoGlossaryShield.Restore("At [[G0]] only", mask, "en-US", out _));               // 누락
        Assert.Null(PlutoGlossaryShield.Restore("[[G0]] [[G1]] [[G1]]", mask, "en-US", out _));         // 중복
        Assert.Null(PlutoGlossaryShield.Restore("[[G0]] [[G1]] [[G7]]", mask, "en-US", out _));         // 미지 번호
        Assert.Equal("Minjun's Café and Minjun", PlutoGlossaryShield.Restore("[[G0]] and[[g1]]", mask, "en-US", out var repaired));
        Assert.True(repaired);                                                                         // 붙은 단어 · 소문자 수리

        // 원문에 이미 있던 [[G0]] 문자열은 자리표시자로 오인되지 않고 그대로 보존
        var literal = PlutoGlossaryShield.Mask("코드 [[G0]] 를 민준", Glossary, "en-US");
        Assert.Equal("코드 [[G0]] 를 [[G1]]", literal.MaskedText);
        Assert.Equal("[[G0]]", literal.Tokens[0].Replacement);
        Assert.Equal("code [[G0]] for Minjun", PlutoGlossaryShield.Restore("code [[G0]] for [[G1]]", literal, "en-US", out _));

        // 큐 단위 거부 → 그 큐만 용어집 폴백 · 되돌려준 한글은 번역으로 치지 않음(경고 유지)
        var cues = new List<SubtitleCue>
        {
            SubtitleCue.Precise(0, 3, "민준 좋아요"),
            SubtitleCue.Precise(3, 6, "민준 최고"),
            SubtitleCue.Precise(6, 9, "고마워요"),
        };
        PlutoTranslationProvider bad = (req, _) => Task.FromResult<string?>(req.MaskedText switch
        {
            "[[G0]] 좋아요" => "[[G0]] likes it",       // 정상
            "[[G0]] 최고" => "The best!",                // 자리표시자 누락
            _ => "고마워요",                              // 원문 되돌려주기
        });
        var svc = new PlutoSubtitleLocalizationService(new PlutoMultiLanguageChannelService(), bad, providerUsesNetwork: false);
        var r = await svc.TranslateAsync("m", cues, "en-US", Glossary);
        Assert.Equal(PlutoTranslationStatus.Partial, r.Status);
        Assert.Equal(1, r.Track!.ProviderCueCount);
        Assert.Equal(2, r.Track.RejectedProviderCueCount);
        Assert.Equal(new[] { "Minjun likes it", "Minjun 최고", "고마워요" }, r.Track.Texts);
        var fit = PlutoLocaleSubtitleFitter.Project(cues, new PlutoMultiLanguageChannelService(), "en-US", 0, 0, r.Track);
        Assert.Equal(2, fit.UntranslatedCueCount);   // 거짓 해제 없음
    }

    // ── 5b. 자체 추가 ② 낡은 번역 트랙 펜스(원문 편집 · 재타이밍) ──

    [Fact]
    public async Task SelfAddition_StaleTrackFence_IgnoresTranslationAfterSourceEditOrRetime()
    {
        var channels = new PlutoMultiLanguageChannelService();
        var cues = KoreanCues();
        var svc = new PlutoSubtitleLocalizationService(channels, FakeProvider(), providerUsesNetwork: false);
        var track = (await svc.TranslateAsync("m", cues, "en-US", Glossary)).Track!;
        Assert.True(track.IsFreshFor(cues, 0, 0));
        Assert.True(PlutoLocaleSubtitleFitter.Project(cues, channels, "en-US", 0, 0, track).UsedTranslationTrack);

        // 크리에이터가 한국어 원문을 고침 → 옛 번역은 더 이상 번역이 아님
        var edited = PlutoSeriesSubtitleOverlay.EditPlain(cues, 1, "오늘은 민준이 라떼를 만들어요");
        Assert.False(track.IsFreshFor(edited, 0, 0));
        var stale = PlutoLocaleSubtitleFitter.Project(edited, channels, "en-US", 0, 0, track);
        Assert.False(stale.UsedTranslationTrack);
        Assert.True(stale.StaleTranslationTrack);
        Assert.Equal(3, stale.UntranslatedCueCount);
        Assert.Contains("다시 번역", stale.WarningKorean, StringComparison.Ordinal);

        // 재타이밍(구간만 변경)도 낡음 — 큐 정렬이 바뀌면 인덱스 대응이 깨진다
        var retimed = new List<SubtitleCue>(cues) { [2] = SubtitleCue.Precise(6.0, 8.0, cues[2].Text) };
        Assert.False(track.IsFreshFor(retimed, 0, 0));
        // 용어집 변경 감지 지문
        Assert.NotEqual(track.GlossaryFingerprint, PlutoSubtitleLocalizationService.GlossaryFingerprintOf(new[] { Glossary[0] with { EnUs = "Min-jun" }, Glossary[1] }, "en-US"));
        Assert.Equal(track.GlossaryFingerprint, PlutoSubtitleLocalizationService.GlossaryFingerprintOf(new[] { Glossary[1], Glossary[0] }, "en-US"));   // 순서 무관
    }

    // ── 5c. 자체 추가 ③ 삭제 표식 LWW · 시즌 무관 범위 · 전역 가림 ──

    [Fact]
    public async Task SelfAddition_GlossaryTombstonesAndSeasonAgnosticScope_SurviveSeasonSplitAndCrossPcMerge()
    {
        var now = T0;
        var path = Path.Combine(_dir, PlutoSeriesArcMemoryService.FileName);
        using var arc = new PlutoSeriesArcMemoryService(path) { UtcNowProvider = () => now };
        Assert.Equal(PlutoSeriesArcMemoryService.GlossaryScopeOf("카페 일기"), PlutoSeriesArcMemoryService.GlossaryScopeOf("카페 일기 2/3편"));
        Assert.Equal(PlutoSeriesArcMemoryService.GlobalGlossaryScope, PlutoSeriesArcMemoryService.GlossaryScopeOf(null));

        Assert.True(await arc.UpsertGlossaryEntryAsync("카페 일기 1/3편", new PlutoGlossaryEntry("민준", "Minjun", "ミンジュン", null)));
        Assert.False(await arc.UpsertGlossaryEntryAsync("카페 일기", new PlutoGlossaryEntry("민준", "Minjun", "ミンジュン", null)));   // 변경 없음
        Assert.False(await arc.UpsertGlossaryEntryAsync("카페 일기", new PlutoGlossaryEntry("빈칸", null, " ", null)));               // 대상 없음 거부
        Assert.True(await arc.UpsertGlossaryEntryAsync(null, new PlutoGlossaryEntry("민준", "MJ (global)", null, null)));
        Assert.True(await arc.UpsertGlossaryEntryAsync(null, new PlutoGlossaryEntry("구독", "subscribe", null, null)));
        var g = arc.GetGlossary("카페 일기 3/3편");
        Assert.Equal("Minjun", Assert.Single(g, e => e.Source == "민준").EnUs);   // 시리즈 항목이 전역을 가림
        Assert.Contains(g, e => e.Source == "구독");

        // 새 시즌 분리 · 편수 변경과 무관하게 같은 용어집
        await arc.StartNewSeasonAsync("카페 일기", 3);
        Assert.Equal(g.Count, arc.GetGlossary("카페 일기 1/5편").Count);

        // 삭제 → 삭제 표식 영속 → 오래된 사본과 병합해도 되살아나지 않음
        var olderCopy = File.ReadAllText(path);
        now = T0.AddMinutes(5);
        Assert.True(await arc.RemoveGlossaryEntryAsync("카페 일기", "민준"));
        Assert.False(await arc.RemoveGlossaryEntryAsync("카페 일기", "민준"));
        var withTombstone = File.ReadAllText(path);
        Assert.Contains("\"Deleted\":true", withTombstone, StringComparison.Ordinal);
        Assert.Equal("MJ (global)", Assert.Single(arc.GetGlossary("카페 일기"), e => e.Source == "민준").EnUs);   // 시리즈 삭제 → 전역 표기 복귀
        var merged = PlutoConflictMergeService.MergeJson(PlutoSeriesArcMemoryService.FileName, olderCopy, withTombstone)!;
        File.WriteAllText(path, merged);
        Assert.True(await arc.ReloadFromDiskAsync());
        Assert.DoesNotContain(arc.GetGlossary("카페 일기"), e => e.Source == "민준" && e.EnUs == "Minjun");

        // 재등록은 삭제 표식보다 새 시각 → 다시 살아남
        now = T0.AddMinutes(10);
        Assert.True(await arc.UpsertGlossaryEntryAsync("카페 일기", new PlutoGlossaryEntry("민준", "Minjun Park", null, null)));
        Assert.Contains(arc.GetGlossary("카페 일기"), e => e.Source == "민준" && e.EnUs == "Minjun Park");
    }

    // ── 5d. 자체 추가 ④ 낭독 글자 예산 · 문화 톤 전달 · 예산 초과 보고 ──

    [Fact]
    public async Task SelfAddition_CharBudgetAndCulturalToneArePassedPerSegment_AndOverBudgetIsReported()
    {
        var channels = new PlutoMultiLanguageChannelService();
        var log = new List<PlutoTranslationRequest>();
        var svc = new PlutoSubtitleLocalizationService(channels, FakeProvider(log), providerUsesNetwork: false);
        var cues = KoreanCues();
        await svc.TranslateAsync("m", cues, "es-ES", Glossary);
        Assert.Equal(3, log.Count);
        Assert.Equal(channels.ComputeTtsBudget("es-ES", "", 3.0).MaxUnitsAtMaxRate, log[0].CharBudget);
        Assert.Equal((int)Math.Floor(17.0 * 1.30 * (3.0 - PlutoMultiLanguageChannelService.SceneCutGuardSeconds)), log[0].CharBudget);
        Assert.Equal("ko-KR", log[0].SourceLocale);
        Assert.Contains("España", log[0].ToneHint, StringComparison.Ordinal);
        Assert.Equal(new[] { "[[G0]]", "[[G1]]", "[[G2]]", "[[G3]]" }, log[0].Placeholders);
        Assert.Contains("です・ます", PlutoSubtitleLocalizationService.ToneHintFor("ja"), StringComparison.Ordinal);
        Assert.NotEqual(PlutoSubtitleLocalizationService.ToneHintFor("en-US"), PlutoSubtitleLocalizationService.ToneHintFor("es-ES"));

        // 0.5초 컷에 긴 번역 → 예산 초과 집계 + 투영이 TTS 초과(1.30x)를 정확히 보고
        PlutoTranslationProvider verbose = (_, _) => Task.FromResult<string?>("This is a far too long translation for such a tiny half second cut");
        var tiny = new List<SubtitleCue> { SubtitleCue.Precise(0, 0.5, "짧은 컷") };
        var r = await new PlutoSubtitleLocalizationService(channels, verbose, providerUsesNetwork: false).TranslateAsync("t", tiny, "en-US", null);
        Assert.Equal(1, r.Track!.OverBudgetCueCount);
        Assert.Contains("예산 초과", r.MessageKorean, StringComparison.Ordinal);
        var fit = PlutoLocaleSubtitleFitter.Project(tiny, channels, "en-US", 0, 0, r.Track);
        Assert.True(fit.UsedTranslationTrack);
        Assert.True(fit.HasTtsOverflow);
        Assert.Equal(0, fit.UntranslatedCueCount);

        // 기본 로캘 · 빈 자막 → 대상 아님
        Assert.Equal(PlutoTranslationStatus.NotApplicable, (await svc.TranslateAsync("m", cues, "ko-KR", Glossary)).Status);
        Assert.Equal(PlutoTranslationStatus.NotApplicable, (await svc.TranslateAsync("m", Array.Empty<SubtitleCue>(), "en-US", Glossary)).Status);
    }
}
