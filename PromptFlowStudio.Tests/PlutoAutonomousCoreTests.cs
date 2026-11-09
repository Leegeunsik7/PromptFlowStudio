using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;
using PromptFlowStudio.Services.Publishing;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>🪐 플루토 자율 AI 디렉터 — 생성형 트렌드 · 프롬프트 DNA 학습 · 코파일럿 3막 시나리오 · 소셜 카피라이터.</summary>
public class PlutoAutonomousCoreTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-dna-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoGenerativeEngine_SynthesizesDynamicTrends_WithoutHardcodedStrings()
    {
        var dir = TempDir();
        try
        {
            using var lex = new PlutoPreferenceLexicon(Path.Combine(dir, PlutoPreferenceLexicon.FileName));
            // 학습된 DNA: "nostalgic grain"(분위기) · "slow orbit"(카메라) 강화
            for (var i = 0; i < 5; i++) await lex.ReinforceAsync("toddler giggling, slow orbit, nostalgic grain, cinematic backlight");

            var gen = new PlutoGenerativeEngine(lex, new Random(7));
            var catalog = new PlutoTrendEngine().All;
            var catalogPrompts = new HashSet<string>(StringComparer.Ordinal);
            var catalogIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in catalog) { catalogPrompts.Add(c.VideoPrompt); catalogIds.Add(c.Id); }

            var now = new DateTime(2026, 11, 9, 17, 30, 0);
            var a = await gen.SynthesizeAsync(StudioThemeCategory.BabyParenting, 4, now);
            var b = await gen.SynthesizeAsync(StudioThemeCategory.BabyParenting, 4, now);
            Assert.Equal(4, a.Count);

            var prompts = new HashSet<string>(StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var usesDna = 0;
            foreach (var t in a.Concat(b))
            {
                Assert.StartsWith(PlutoGenerativeEngine.IdPrefix, t.Id, StringComparison.Ordinal);
                Assert.DoesNotContain(t.Id, catalogIds);
                Assert.DoesNotContain(t.VideoPrompt, catalogPrompts);
                Assert.True(ids.Add(t.Id), "중복 Id: " + t.Id);
                prompts.Add(t.VideoPrompt);
                // 바이럴 3막 구조
                Assert.Contains("Hook:", t.VideoPrompt, StringComparison.Ordinal);
                Assert.Contains("Core emotion:", t.VideoPrompt, StringComparison.Ordinal);
                Assert.Contains("Lingering outro:", t.VideoPrompt, StringComparison.Ordinal);
                // 컨텍스트 시드(가을)
                Assert.Contains("autumn", t.VideoPrompt, StringComparison.Ordinal);
                Assert.Contains("#Shorts", t.YouTubeHashtags);
                Assert.Contains("#Reels", t.InstagramHashtags);
                if (t.VideoPrompt.Contains("nostalgic grain", StringComparison.Ordinal) || t.CameraMotion == "slow orbit"
                    || t.VideoPrompt.Contains("cinematic backlight", StringComparison.Ordinal)) usesDna++;
            }
            Assert.True(prompts.Count >= 6, "다양성 부족: " + prompts.Count);
            Assert.True(usesDna >= 4, "DNA 토큰 활용 부족: " + usesDna);

            // 엔진 결선 → 발굴 + Find(생성 카드 조회)
            using var learning = new PlutoLearningService(Path.Combine(dir, "trend.json"), new Random(1));
            var engine = new PlutoTrendEngine(learning, gen);
            Assert.True(engine.CanDiscover);
            var fresh = await engine.DiscoverFreshTrendsAsync(StudioThemeCategory.PetsCompanions);
            Assert.Equal(4, fresh.Count);
            Assert.All(fresh, t => Assert.Equal(StudioThemeCategory.PetsCompanions, t.Category));
            Assert.Same(fresh[0], engine.Find(fresh[0].Id));

            // 생성기 미결선 → 고정 카탈로그 폴백
            var bare = new PlutoTrendEngine();
            Assert.False(bare.CanDiscover);
            var fb = await bare.DiscoverFreshTrendsAsync(StudioThemeCategory.HealingNature);
            Assert.All(fb, t => Assert.Contains(t.Id, catalogIds));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoPreferenceLexicon_ExtractsAndReinforcesTokens_OnExportSuccess()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, PlutoPreferenceLexicon.FileName);
        try
        {
            const string prompt = "baby rolling over in golden hour, eye-level dolly then macro close-up, pastel tone with warm bokeh, soft morning rays";
            var tokens = PlutoPreferenceLexicon.Extract(prompt);
            bool Has(DnaBucket b, string p) => tokens.Any(t => t.Bucket == b && t.Phrase == p);
            Assert.True(Has(DnaBucket.Lighting, "golden hour"));
            Assert.True(Has(DnaBucket.Lighting, "soft morning rays"));
            Assert.True(Has(DnaBucket.Camera, "eye-level dolly"));
            Assert.True(Has(DnaBucket.Camera, "macro close-up"));
            Assert.True(Has(DnaBucket.Atmosphere, "pastel tone"));
            Assert.True(Has(DnaBucket.Atmosphere, "warm bokeh"));
            Assert.False(Has(DnaBucket.Camera, "close-up"));   // 긴 시드 우선 — 부분 중복 억제
            Assert.DoesNotContain(tokens, t => t.Phrase.Contains("baby", StringComparison.Ordinal));   // 피사체는 스타일 토큰 아님
            // 미등록 어휘 꼬리 패턴 학습
            Assert.Contains(PlutoPreferenceLexicon.Extract("shot on anamorphic lens, teal palette"), t => t.Bucket == DnaBucket.Camera && t.Phrase == "anamorphic lens");

            // 내보내기 성공 루프: PublishViewModel → 렉시콘 강화 → pluto_dna_memory.json
            using (var lex = new PlutoPreferenceLexicon(path))
            using (var vm = new PublishViewModel(new NullYt(), new NullIg(), lexicon: lex))
            {
                vm.OpenForMedia(Path.Combine(dir, "clip.mp4"), "clip", null, prompt);
                Assert.False(await vm.RecordPlutoExportAsync());   // 트렌드 Id 없음(수동 프롬프트) → 트렌드 보상 없음, DNA 는 강화
                Assert.True(vm.LastDnaTokenCount >= 6);
                Assert.Equal(1, lex.GetFrequency(DnaBucket.Camera, "eye-level dolly"));
                Assert.Equal(0, await vm.ReinforcePlutoDnaAsync());   // 동일 프롬프트 연속 중복 보상 차단
                Assert.Equal(1, lex.GetFrequency(DnaBucket.Camera, "eye-level dolly"));
                await lex.ReinforceAsync("golden hour glow, slow orbit");
                Assert.Equal(2, lex.GetFrequency(DnaBucket.Lighting, "golden hour"));
                Assert.Equal(2 * PlutoPreferenceLexicon.ExportReward, lex.GetWeight(DnaBucket.Lighting, "golden hour"), 6);
                Assert.Equal("golden hour", lex.TopTokens(DnaBucket.Lighting, 1)[0]);
            }

            Assert.True(File.Exists(path));
            var json = await File.ReadAllTextAsync(path);
            Assert.Contains("eye-level dolly", json, StringComparison.Ordinal);

            using var reload = new PlutoPreferenceLexicon(path);
            await reload.EnsureLoadedAsync();
            Assert.Equal(2, reload.GetFrequency(DnaBucket.Lighting, "golden hour"));
            Assert.Equal(1, reload.GetFrequency(DnaBucket.Atmosphere, "pastel tone"));

            // 손상 파일 → 빈 사전으로 복구
            await File.WriteAllTextAsync(path, "{not json");
            using var broken = new PlutoPreferenceLexicon(path);
            await broken.EnsureLoadedAsync();
            Assert.Equal(0, broken.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoCopilot_GeneratesThreeActScript_FromUserMemo()
    {
        var copilot = new PlutoCopilot();
        var sc = copilot.Draft("오늘 아기가 거실에서 처음으로 뒤집기 성공하고 방긋 웃었어", new DateTime(2026, 11, 9, 9, 0, 0));
        Assert.Equal(StudioThemeCategory.BabyParenting, sc.Category);
        Assert.Equal("첫 뒤집기", sc.MilestoneKo);
        Assert.False(string.IsNullOrWhiteSpace(sc.HookTitle));
        Assert.Equal(3, sc.Acts.Count);
        Assert.Equal(0, sc.Acts[0].StartSec);
        Assert.Equal(PlutoCopilot.TotalSeconds, sc.Acts[2].EndSec);
        for (var i = 1; i < 3; i++) Assert.Equal(sc.Acts[i - 1].EndSec, sc.Acts[i].StartSec);
        Assert.Contains("roll over", sc.Acts[0].PromptEn, StringComparison.Ordinal);
        Assert.Contains("living room", sc.Acts[0].PromptEn, StringComparison.Ordinal);
        Assert.Contains("smile", sc.Acts[2].PromptEn, StringComparison.Ordinal);
        Assert.Contains("Act 1 (0-5s)", sc.VideoPrompt, StringComparison.Ordinal);
        Assert.Contains("Act 3 (10-15s)", sc.VideoPrompt, StringComparison.Ordinal);
        Assert.Contains("dolly", sc.CameraMotion, StringComparison.Ordinal);
        Assert.Equal(BabyBgmTrack.MusicBox, sc.BgmTrack);
        Assert.False(string.IsNullOrWhiteSpace(sc.BgmLabel));
        Assert.Contains("camera:", sc.BuildStudioPrompt(), StringComparison.Ordinal);

        var hundred = copilot.Draft("100일 된 우리 아기 첫 뒤집기");
        Assert.StartsWith("100일 만에 일어난 작은 기적", hundred.HookTitle, StringComparison.Ordinal);

        var pet = copilot.Draft("강아지랑 공원 산책 갔는데 너무 신나했어");
        Assert.Equal(StudioThemeCategory.PetsCompanions, pet.Category);
        Assert.Contains("park", pet.Acts[0].PromptEn, StringComparison.Ordinal);

        Assert.Equal("오늘의 작은 순간", copilot.Draft("평범한 하루").MilestoneKo);   // 미인식 → 기본 시나리오
        Assert.Throws<ArgumentException>(() => copilot.Draft("  "));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoSocialCopywriter_ComposesPost_WithTargetedHashtags()
    {
        var post = PlutoSocialCopywriter.Compose("100일 만에 일어난 작은 기적 🍼", "baby rolling over", SocialPlatform.Both);
        Assert.InRange(post.Hashtags.Count, PlutoSocialCopywriter.MinTags, PlutoSocialCopywriter.MaxTags);
        Assert.Contains("#Shorts", post.Hashtags);
        Assert.Contains("#Reels", post.Hashtags);
        Assert.Contains("#육아스타그램", post.Hashtags);
        Assert.Equal(post.Hashtags.Count, post.Hashtags.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.InRange(post.StoryLines.Count, 2, 3);
        Assert.False(string.IsNullOrWhiteSpace(post.PinnedComment));
        Assert.Contains("📌", post.FullText, StringComparison.Ordinal);

        var reels = PlutoSocialCopywriter.Compose(null, "강아지 산책", SocialPlatform.InstagramReels);
        Assert.DoesNotContain("#Shorts", reels.Hashtags);
        Assert.Equal("", reels.PinnedComment);
        Assert.Contains("#댕댕이", reels.Hashtags);

        using var vm = new PublishViewModel(new NullYt(), new NullIg());
        vm.OpenForMedia("x.mp4", "x", null, "newborn sleeping");
        vm.ApplySocialPreset(SocialPresetKind.YouTubeShorts);   // 프리셋 → 자동 초안
        Assert.NotNull(vm.PlutoSocialDraft);
        Assert.Equal(SocialPlatform.YouTubeShorts, vm.PlutoSocialDraft!.Platform);
        vm.ToYouTube = vm.ToInstagram = false;
        Assert.Null(vm.GeneratePlutoSocialDraft());
        Assert.False(vm.CopyPlutoSocialDraft());   // 초안 없음/클립보드 미주입
    }

    // ══════════════════════ 🧠 Gemini LLM 결선 · 무중단 로컬 폴백 ══════════════════════

    /// <summary>테스트용 LLM — 고정 응답 / 예외 / 지연(토큰 무시) / 미설정 시뮬레이션.</summary>
    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool IsAvailable { get; init; } = true;
        public string? Response { get; init; }
        public Exception? Throw { get; init; }
        public TaskCompletionSource<string?>? Gate { get; init; }
        public int Calls;
        public string LastSystem = "";
        public string LastUser = "";

        public async Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastSystem = system; LastUser = user;
            if (Gate is not null) return await Gate.Task.ConfigureAwait(false);   // 토큰 무시 · 테스트 통제 게이트 → 브리지 WhenAny 예산 검증
            if (Throw is not null) throw Throw;
            return Response;
        }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoGenerativeEngine_InvokesLlm_AndFallsBackToLocalCombinator_OnError()
    {
        var now = new DateTime(2026, 11, 9, 18, 0, 0);
        const string json = "```json\n{\"trends\":[" +
            "{\"title\":\"낙엽 위 첫 걸음\",\"hook\":\"가을 노을 속 첫 걸음 🍂\",\"prompt\":\"Hook: toddler stepping on crunchy autumn leaves; Core: giggling at the sound; Outro: hugging dad at golden hour\",\"camera\":\"low-angle tracking\",\"bgm\":\"Warm piano\",\"hashtags\":[\"#첫걸음\",\"가을육아\",\"#첫걸음\"]}," +
            "{\"title\":\"비눗방울 파티\",\"hook\":\"까르르 웃음 폭발 🫧\",\"prompt\":\"Hook: baby chasing giant soap bubbles in the yard; Core: bubble pops on the nose; Outro: sleepy smile on a blanket\",\"camera\":\"slow orbit\",\"bgm\":\"Music box\",\"hashtags\":[\"#비눗방울\"]}," +
            "{\"title\":\"중복\",\"hook\":\"x\",\"prompt\":\"Hook: baby chasing giant soap bubbles in the yard; Core: bubble pops on the nose; Outro: sleepy smile on a blanket\"}," +
            "{\"title\":\"누락\",\"hook\":\"\",\"prompt\":\"missing hook prompt text\"}]}\n```";

        // ① LLM 경로 — 컨텍스트(카테고리·계절·시간대) 전달 · 스키마 검증 · 부족분(2건) 로컬 보충
        var llm = new FakeLlm { Response = json };
        var gen = new PlutoGenerativeEngine(null, llm, new Random(3));
        var trends = await gen.GenerateDynamicTrendsAsync(StudioThemeCategory.BabyParenting, 4, now);
        Assert.Equal(1, llm.Calls);
        Assert.True(gen.LastUsedLlm);
        Assert.Contains("Baby", llm.LastUser, StringComparison.Ordinal);
        Assert.Contains("autumn", llm.LastUser, StringComparison.Ordinal);
        Assert.Contains("sunset", llm.LastUser, StringComparison.Ordinal);
        Assert.Equal(4, trends.Count);
        Assert.Equal("낙엽 위 첫 걸음", trends[0].Title);
        Assert.StartsWith(PlutoGenerativeEngine.IdPrefix + "llm-", trends[0].Id, StringComparison.Ordinal);
        Assert.Equal(new[] { "#Shorts", "#YouTubeShorts", "#첫걸음", "#가을육아", "#육아스타그램", "#플루토추천" }, trends[0].YouTubeHashtags);
        Assert.Equal("#Reels", trends[0].InstagramHashtags[0]);
        Assert.DoesNotContain("llm-", trends[2].Id, StringComparison.Ordinal);   // [부분 구제] 로컬 보충
        Assert.Equal(4, trends.Select(t => t.VideoPrompt).Distinct(StringComparer.Ordinal).Count());
        Assert.Same(trends[0], gen.FindRecent(trends[0].Id));

        // ② 예외 → 로컬 조합기
        var boom = new FakeLlm { Throw = new HttpRequestException("503") };
        var g2 = new PlutoGenerativeEngine(null, boom, new Random(3));
        var fb = await g2.GenerateDynamicTrendsAsync(StudioThemeCategory.PetsCompanions, 4, now);
        Assert.Equal(1, boom.Calls);
        Assert.False(g2.LastUsedLlm);
        Assert.Equal(4, fb.Count);
        Assert.All(fb, t => Assert.DoesNotContain("llm-", t.Id, StringComparison.Ordinal));

        // ③ 스키마 불일치 · 미설정(호출 0) · 3.5초 예산 초과(토큰 무시 클라이언트)
        var bad = new FakeLlm { Response = "{\"oops\":1}" };
        Assert.Equal(4, (await new PlutoGenerativeEngine(null, bad).GenerateDynamicTrendsAsync(StudioThemeCategory.HealingNature, 4, now)).Count);
        var off = new FakeLlm { IsAvailable = false, Response = json };
        var g3 = new PlutoGenerativeEngine(null, off);
        Assert.Equal(3, (await g3.GenerateDynamicTrendsAsync(StudioThemeCategory.DailyVlog, 3, now)).Count);
        Assert.Equal(0, off.Calls);
        Assert.False(g3.LastUsedLlm);

        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeLlm { Gate = gate };   // 영원히 응답 없음(토큰 무시) → 예산 만료로만 반환 가능
        Assert.Null(await PlutoLlmBridge.TryCompleteAsync(slow, "s", "u", 0.5, CancellationToken.None, TimeSpan.FromMilliseconds(150)).WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(TimeSpan.FromMilliseconds(3500), PlutoLlmBridge.Budget);

        // ④ 호출자 취소는 전파
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlutoLlmBridge.TryCompleteAsync(slow, "s", "u", 0.5, cts.Token));
        gate.SetResult(json);   // 게이트 해제(잔류 대기 정리)

        // ⑤ PlutoTrendEngine 발굴 경로도 LLM 우선
        var llm2 = new FakeLlm { Response = json };
        var engine = new PlutoTrendEngine(null, new PlutoGenerativeEngine(null, llm2, new Random(1)));
        var disc = await engine.DiscoverFreshTrendsAsync(StudioThemeCategory.BabyParenting, 2);
        Assert.Equal(2, disc.Count);
        Assert.Equal(1, llm2.Calls);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoCopilot_GeneratesRichScriptViaLlm_AndFallsBackGracefully()
    {
        var now = new DateTime(2026, 11, 9, 9, 0, 0);
        const string memo = "할머니 댁 마당에서 아기가 처음으로 강아지를 쓰다듬고 깔깔 웃었어";
        const string json = "{\"hookTitle\":\"할머니 댁 마당의 첫 교감 🐶\",\"milestone\":\"첫 교감\",\"acts\":[" +
            "{\"label\":\"설렘\",\"prompt\":\"baby reaching a tiny hand toward a fluffy puppy in grandma's sunny yard\"}," +
            "{\"label\":\"교감\",\"prompt\":\"puppy licking the baby's palm as the baby bursts into giggles\"}," +
            "{\"label\":\"미소\",\"prompt\":\"baby resting against grandma, smiling at the dozing puppy, warm haze.\"}]," +
            "\"camera\":\"eye-level slow dolly-in to macro close-up\",\"bgm\":\"acoustic guitar\"}";

        var llm = new FakeLlm { Response = json };
        var copilot = new PlutoCopilot(null, llm);
        Assert.True(copilot.HasLlm);
        var sc = await copilot.DraftScriptFromMemoAsync(memo, now);
        Assert.True(sc.UsedLlm);
        Assert.Contains(memo, llm.LastUser, StringComparison.Ordinal);
        Assert.Contains("0-4s", llm.LastSystem, StringComparison.Ordinal);
        Assert.Equal("할머니 댁 마당의 첫 교감 🐶", sc.HookTitle);
        Assert.Equal("첫 교감", sc.MilestoneKo);
        Assert.Equal(3, sc.Acts.Count);
        Assert.Equal((0, 4), (sc.Acts[0].StartSec, sc.Acts[0].EndSec));
        Assert.Equal((4, 11), (sc.Acts[1].StartSec, sc.Acts[1].EndSec));
        Assert.Equal((11, PlutoCopilot.TotalSeconds), (sc.Acts[2].StartSec, sc.Acts[2].EndSec));
        Assert.Contains("Act 3 (11-15s): baby resting against grandma", sc.VideoPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("..", sc.VideoPrompt, StringComparison.Ordinal);
        Assert.Equal(BabyBgmTrack.Acoustic, sc.BgmTrack);
        Assert.Equal(StockSuiteExporter.BabyBgmLabel(BabyBgmTrack.Acoustic), sc.BgmLabel);
        Assert.EndsWith("camera: eye-level slow dolly-in to macro close-up", sc.BuildStudioPrompt(), StringComparison.Ordinal);

        // 2막 누락 → 스키마 불일치 → 로컬 마일스톤 템플릿
        var partial = new FakeLlm { Response = "{\"acts\":[{\"label\":\"a\",\"prompt\":\"only one act here\"}]}" };
        var local = await new PlutoCopilot(null, partial).DraftScriptFromMemoAsync("오늘 아기가 거실에서 처음으로 뒤집기 성공", now);
        Assert.False(local.UsedLlm);
        Assert.Equal("첫 뒤집기", local.MilestoneKo);

        // 예외 · 오프라인(미주입) → 로컬 Draft 와 동일
        var boom = await new PlutoCopilot(null, new FakeLlm { Throw = new InvalidOperationException("down") }).DraftScriptFromMemoAsync(memo, now);
        var offline = await new PlutoCopilot().DraftScriptFromMemoAsync(memo, now);
        var baseline = new PlutoCopilot().Draft(memo, now);
        Assert.False(boom.UsedLlm);
        Assert.False(offline.UsedLlm);
        Assert.Equal(baseline.VideoPrompt, boom.VideoPrompt);
        Assert.Equal(baseline.HookTitle, offline.HookTitle);
        Assert.Equal(StudioThemeCategory.PetsCompanions, offline.Category);

        // 빈 메모 → 즉시 ArgumentException(LLM 호출 없음)
        var guard = new FakeLlm { Response = json };
        await Assert.ThrowsAsync<ArgumentException>(() => new PlutoCopilot(null, guard).DraftScriptFromMemoAsync("   "));
        Assert.Equal(0, guard.Calls);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoSocialCopywriter_ProducesLlmCaptions_WithFallbackIntegrity()
    {
        const string title = "100일 만에 일어난 작은 기적 🍼";
        const string prompt = "baby rolling over on a pastel playmat, golden hour";
        const string json = "{\"hook\":\"🍼 100일의 기다림, 드디어 뒤집었어요!\",\"story\":[\"매일 낑낑대던 우리 아기 🥹\",\"오늘 아침 드디어 성공했어요 💛\",\"엄마 아빠는 눈물 바다 😭\",\"네번째 줄은 버려요\"]," +
            "\"hashtags\":[\"#뒤집기성공\",\"#백일아기\",\"#육아스타그램\",\"#babymilestone\",\"#Shorts\",\"#성장기록\",\"육아맘\",\"#babyreels\",\"#뒤집기성공\",\"#딸바보\",\"#아빠육아\"]," +
            "\"pinnedComment\":\"여러분 아기는 언제 뒤집었나요? 👶\"}";

        var llm = new FakeLlm { Response = json };
        var post = await PlutoSocialCopywriter.DraftSocialPostAsync(llm, title, prompt, SocialPlatform.Both);
        Assert.True(post.UsedLlm);
        Assert.Contains(title, llm.LastUser, StringComparison.Ordinal);
        Assert.Contains(prompt, llm.LastUser, StringComparison.Ordinal);
        Assert.Equal(title, post.Title);
        Assert.Equal("🍼 100일의 기다림, 드디어 뒤집었어요!", post.HookIntro);
        Assert.Equal(3, post.StoryLines.Count);   // 2~3줄 상한
        Assert.InRange(post.Hashtags.Count, PlutoSocialCopywriter.MinLlmTags, PlutoSocialCopywriter.MaxTags);
        Assert.Equal("#Shorts", post.Hashtags[0]);
        Assert.Equal("#Reels", post.Hashtags[1]);
        Assert.Contains("#육아맘", post.Hashtags);
        Assert.Equal(post.Hashtags.Count, post.Hashtags.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(post.Hashtags, t => { Assert.StartsWith("#", t, StringComparison.Ordinal); Assert.DoesNotContain(" ", t, StringComparison.Ordinal); });
        Assert.Equal("여러분 아기는 언제 뒤집었나요? 👶", post.PinnedComment);
        Assert.Contains("📌 고정 댓글", post.FullText, StringComparison.Ordinal);

        // Reels 전용 → 고정 댓글 없음 · #Shorts 미강제
        var reels = await PlutoSocialCopywriter.DraftSocialPostAsync(new FakeLlm { Response = json }, title, prompt, SocialPlatform.InstagramReels);
        Assert.True(reels.UsedLlm);
        Assert.Equal("#Reels", reels.Hashtags[0]);
        Assert.DoesNotContain("#Shorts", reels.Hashtags);
        Assert.Equal("", reels.PinnedComment);

        // 스키마 불일치(스토리 1줄) · 예외 · 오프라인 → 로컬 템플릿과 동일
        var baseline = PlutoSocialCopywriter.Compose(title, prompt, SocialPlatform.Both);
        var thin = await PlutoSocialCopywriter.DraftSocialPostAsync(new FakeLlm { Response = "{\"hook\":\"h\",\"story\":[\"one\"],\"hashtags\":[\"#a\",\"#b\",\"#c\",\"#d\",\"#e\"]}" }, title, prompt, SocialPlatform.Both);
        var boom = await PlutoSocialCopywriter.DraftSocialPostAsync(new FakeLlm { Throw = new TimeoutException() }, title, prompt, SocialPlatform.Both);
        var offline = await PlutoSocialCopywriter.DraftSocialPostAsync(null, title, prompt, SocialPlatform.Both);
        foreach (var p in new[] { thin, boom, offline })
        {
            Assert.False(p.UsedLlm);
            Assert.Equal(baseline.FullText, p.FullText);
            Assert.InRange(p.Hashtags.Count, PlutoSocialCopywriter.MinTags, PlutoSocialCopywriter.MaxTags);
        }

        // VM 비동기 경로 — Gemini 미주입 → 로컬 초안 · 메시지
        using var vm = new PublishViewModel(new NullYt(), new NullIg());
        vm.OpenForMedia("x.mp4", "x", null, "newborn sleeping");
        vm.ToYouTube = true;
        var d = await vm.GeneratePlutoSocialDraftAsync();
        Assert.NotNull(d);
        Assert.False(d!.UsedLlm);
        Assert.False(vm.IsPlutoSocialBusy);
        Assert.Contains("해시태그", vm.PlutoSocialMessage, StringComparison.Ordinal);
    }

    private sealed class NullYt : IYouTubeShortsPublisher
    {
        public bool Enabled => false;
        public Task<ShortsPublishResult> PublishAsync(string videoPath, ShortsMetadata meta, string? srtPath = null, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ShortsPublishResult(false, false, null, null, false, "off"));
        public Task<bool> TestConnectionAsync(CancellationToken ct = default) => Task.FromResult(false);
    }

    private sealed class NullIg : IInstagramReelsPublisher
    {
        public bool Enabled => false;
        public Task<ReelsPublishOutcome> PublishAsync(string videoPath, string? publicVideoUrl, string caption, bool publicConsent, IProgress<double>? progress = null, CancellationToken ct = default)
            => Task.FromResult(new ReelsPublishOutcome(false, false, ReelsRoute.Mock, null, "off"));
        public Task<bool> ValidateCredentialsAsync(string? userId, string? accessToken, CancellationToken ct = default) => Task.FromResult(false);
    }
}
