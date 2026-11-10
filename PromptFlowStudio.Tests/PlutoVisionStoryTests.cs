using System.Text;
using PromptFlowStudio.Services.Export;
using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>📸 Pluto Vision-to-Story — 사진 1장 → 15초 3막 4컷 · 통합 디렉터 HUD.</summary>
public sealed class PlutoVisionStoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pfs_vstory_" + Guid.NewGuid().ToString("N"));

    public PlutoVisionStoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "PromptFlowStudio", "PromptFlowStudio.csproj"))) return d.FullName;
        throw new DirectoryNotFoundException("레포 루트");
    }

    /// <summary>JPEG 헤더 + EXIF ASCII 촬영 시각 블록(규칙 분석 · 비전 입력 공용 가짜 사진).</summary>
    private string WritePhoto(string name, string? exifDate)
    {
        var path = Path.Combine(_dir, name);
        var bytes = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x40 };
        bytes.AddRange(Encoding.ASCII.GetBytes("Exif\0\0MM"));
        if (exifDate is not null) bytes.AddRange(Encoding.ASCII.GetBytes(exifDate + "\0"));
        bytes.AddRange(new byte[64]);
        bytes.Add(0xFF); bytes.Add(0xD9);
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private sealed class FakeVision : IPlutoVisionClient
    {
        public bool IsVisionAvailable { get; init; } = true;
        public string? Response { get; init; }
        public bool Throw { get; init; }
        public bool Hang { get; init; }
        public int Calls;
        public string LastUser = "";
        public string LastMime = "";
        public int LastBytes;

        public async Task<string?> DescribeImageJsonAsync(string system, string user, byte[] image, string mimeType, double temperature, CancellationToken ct)
        {
            Calls++;
            LastUser = user;
            LastMime = mimeType;
            LastBytes = image.Length;
            if (Throw) throw new HttpRequestException("vision offline");
            if (Hang) await Task.Delay(Timeout.Infinite, CancellationToken.None);   // 토큰 무시 클라이언트
            return Response;
        }
    }

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool IsAvailable { get; init; } = true;
        public string? Response { get; init; }
        public int Calls;

        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Response);
        }
    }

    [Fact]
    public void PlutoTrendLab_RendersUnifiedDirectorRank_WithoutOldLevel()
    {
        // 표시 로직 — "🪐 칭호 (Lv.N)" · "(XP/다음 XP)" · 해금 광학 툴팁 누적
        var tier = PlutoDirectorRankService.TierFor(25);
        Assert.Equal("🪐 감성 스토리텔러 (Lv.2)", PlutoDirectorRankService.HudTitle(tier));
        Assert.Equal("(25/50 XP)", PlutoDirectorRankService.XpLabel(25, tier));
        Assert.Equal("(0/20 XP)", PlutoDirectorRankService.XpLabel(-5, PlutoDirectorRankService.TierFor(-5)));
        var tip = PlutoDirectorRankService.UnlockedOpticsTooltip(tier);
        Assert.Contains("85mm f/1.4, 골든아워 역광", tip, StringComparison.Ordinal);
        Assert.Contains("50mm 표준 렌즈", tip, StringComparison.Ordinal);
        Assert.DoesNotContain("4K 피부 텍스처", tip, StringComparison.Ordinal);   // Lv.3 미해금
        Assert.Contains("4K 피부 텍스처", PlutoDirectorRankService.UnlockedOpticsTooltip(PlutoDirectorRankService.TierFor(60)), StringComparison.Ordinal);
        Assert.All(PlutoDirectorRankService.Tiers, t => Assert.NotEmpty(t.KoreanTraits));

        // 트렌드 랩 — 구 10 XP 두뇌 카운터 제거 · 통합 배지 단일 표시
        var root = RepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Pages", "PlutoTrendLab.razor"));
        Assert.DoesNotContain("AI 두뇌", page, StringComparison.Ordinal);
        Assert.DoesNotContain("CurrentBrainLevel", page, StringComparison.Ordinal);
        Assert.DoesNotContain("LevelFor(", page, StringComparison.Ordinal);
        Assert.DoesNotContain("pl-brain-fill", page, StringComparison.Ordinal);
        Assert.Contains("<PlutoDirectorBadge Director=\"Director\" Unified=\"true\" />", page, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(page, "<PlutoDirectorBadge"));

        var badge = File.ReadAllText(Path.Combine(root, "PromptFlowStudio", "Blazor", "Components", "PlutoDirectorBadge.razor"));
        Assert.Contains("PlutoDirectorRankService.HudTitle(tier)", badge, StringComparison.Ordinal);
        Assert.Contains("PlutoDirectorRankService.XpLabel(xp, tier)", badge, StringComparison.Ordinal);
        Assert.Contains("PlutoDirectorRankService.UnlockedOpticsTooltip(tier)", badge, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?m)^\s*@inject\b", badge);   // 모바일 링크 서브컴포넌트
    }

    [Fact]
    public async Task PlutoVisionStorySynthesizer_GeneratesCoordinatedStory_WithIdentityAndOptics()
    {
        using var vault = new PlutoIdentityVault(Path.Combine(_dir, "vault.json"));
        await vault.SaveProfileAsync(new PlutoIdentityProfile { SubjectName = "하늘", VisualFeatures = "chubby cheeks, curly brown hair", FavoriteItem = "yellow duck plush" });
        var anchor = (await vault.GetActiveProfileAsync())!.GetConsistencyAnchor();

        var vision = new FakeVision
        {
            Response = """
            {"kind":"smile","mood":"호기심 가득한 미소","setting":"따스한 오후 거실, 노란 튤립 소품","expression":"wide gummy smile","gaze":"looking at mom",
             "lighting":"warm afternoon window light","props":"yellow tulips","action":"reaching for the tulips","bgm":"Twinkling Music Box","subtitle":"방긋, 튤립보다 환한 너",
             "scenes":[{"slot":1,"title":"3초 전","prompt":"the baby spots the tulips and tilts the head curiously","camera":"Eye-Level Push-In"},
                       {"slot":2,"title":"빌드업","prompt":"the baby leans toward the tulips, giggle building"},
                       {"slot":3,"title":"그 미소","prompt":"the baby bursts into a wide gummy smile at mom, tulips in frame"},
                       {"slot":4,"title":"여운","prompt":"the baby settles into a calm happy glow"}]}
            """,
        };
        var synth = new PlutoVisionStorySynthesizer(vision, new FakeLlm { Response = null }, vault, new PlutoDirectorRankService(() => 60));
        var photo = WritePhoto("IMG_0420.jpg", "2026:05:05 17:30:00");

        var plan = await synth.SynthesizeFromImageAsync(photo, "튤립을 처음 본 날");

        Assert.Equal(PlutoVisionSource.Vision, plan.Source);
        Assert.Equal(1, vision.Calls);
        Assert.Equal("image/jpeg", vision.LastMime);
        Assert.True(vision.LastBytes > 0);
        Assert.Contains("Director rank Lv.3 Cinematic Master", vision.LastUser, StringComparison.Ordinal);
        Assert.Contains("튤립을 처음 본 날", vision.LastUser, StringComparison.Ordinal);

        Assert.Equal("호기심 가득한 미소", plan.DetectedMood);
        Assert.Equal("따스한 오후 거실, 노란 튤립 소품", plan.DetectedSetting);
        Assert.Equal("방긋, 튤립보다 환한 너", plan.SuggestedSubtitle);
        Assert.Equal(BabyBgmTrack.MusicBox, plan.SuggestedBgmTrack);
        Assert.Equal(3, plan.DirectorLevel);
        Assert.Equal(anchor, plan.PersonaConsistencyAnchor);

        // 3막 · 4컷 · 15초
        Assert.Equal(PlutoVisionStorySynthesizer.SceneCount, plan.StoryboardScenes.Count);
        Assert.StartsWith("Act 1 · ", plan.StoryboardScenes[0].SceneTitle, StringComparison.Ordinal);
        Assert.StartsWith("Act 2 · ", plan.StoryboardScenes[1].SceneTitle, StringComparison.Ordinal);
        Assert.StartsWith("Act 2 · ", plan.StoryboardScenes[2].SceneTitle, StringComparison.Ordinal);
        Assert.StartsWith("Act 3 · ", plan.StoryboardScenes[3].SceneTitle, StringComparison.Ordinal);
        Assert.Equal(new[] { 1, 2, 2, 3 }, plan.StoryboardScenes.Select(s => PlutoVisionStorySynthesizer.ActOf(s.SlotIndex)).ToArray());
        Assert.Equal(15f, plan.StoryboardScenes.Sum(s => s.DurationSec));
        Assert.Equal("0-4s", plan.StoryboardScenes[0].TimeRange);
        Assert.Equal("12-15s", plan.StoryboardScenes[3].TimeRange);

        // 앵커 선두 · 디렉터 광학 말미(전 컷)
        Assert.All(plan.StoryboardScenes, s =>
        {
            Assert.StartsWith(anchor, s.Prompt, StringComparison.Ordinal);
            Assert.Contains("anamorphic horizontal lens flare", s.Prompt, StringComparison.Ordinal);
        });
        Assert.Contains("wide gummy smile", plan.StoryboardScenes[2].Prompt, StringComparison.Ordinal);

        // 멱등 광학 · 스토리보드 입력
        var again = PlutoVisionStorySynthesizer.ApplyOptics(plan, PlutoDirectorRankService.TierFor(60));
        for (var i = 0; i < plan.StoryboardScenes.Count; i++) Assert.Equal(plan.StoryboardScenes[i].Prompt, again.StoryboardScenes[i].Prompt);
        var shots = plan.ToDirectorShots();
        Assert.Equal(4, shots.Length);
        Assert.Equal(15f, shots.Sum(s => s.DurationSec));
        Assert.Equal(plan.StoryboardScenes[0].Prompt, shots[0].Prompt);
    }

    [Fact]
    public async Task PlutoVisionStorySynthesizer_FallsBackGracefully_WhenVisionOffline()
    {
        var photo = WritePhoto("haneul_nap_tulip.jpg", "2026:03:14 08:15:00");
        Assert.Equal(new DateTime(2026, 3, 14, 8, 15, 0), PlutoVisionStorySynthesizer.TryReadExifDate(File.ReadAllBytes(photo)));

        // ① 키 없음(비전 미가용 · LLM 미가용) → 규칙 분석
        var offline = new FakeVision { IsVisionAvailable = false, Response = "{}" };
        var plan = await new PlutoVisionStorySynthesizer(offline, new FakeLlm { IsAvailable = false }, null, new PlutoDirectorRankService(() => 0))
            .SynthesizeFromImageAsync(photo);
        Assert.Equal(0, offline.Calls);
        Assert.Equal(PlutoVisionSource.Heuristic, plan.Source);
        Assert.Equal(PlutoVisionMoodKind.Sleep, plan.Attributes.Kind);
        Assert.Equal("평화로운 낮잠", plan.DetectedMood);
        Assert.Contains("노란 튤립", plan.DetectedSetting, StringComparison.Ordinal);
        Assert.Equal(BabyBgmTrack.Lullaby, plan.SuggestedBgmTrack);
        Assert.False(string.IsNullOrWhiteSpace(plan.SuggestedSubtitle));
        Assert.Equal(PlutoMilestoneOrchestrator.DefaultBabyAnchor, plan.PersonaConsistencyAnchor);
        Assert.Equal(4, plan.StoryboardScenes.Count);
        Assert.All(plan.StoryboardScenes, s =>
        {
            Assert.StartsWith(PlutoMilestoneOrchestrator.DefaultBabyAnchor, s.Prompt, StringComparison.Ordinal);
            Assert.Contains("fresh morning window light", s.Prompt, StringComparison.Ordinal);   // EXIF 08시 → 아침 조명
            Assert.Contains("50mm lens", s.Prompt, StringComparison.Ordinal);                     // Lv.1 광학
        });

        // ② 비전 예외 → 로컬 게이트웨이(텍스트) 채택
        var gatewayJson = """{"kind":"play","mood":"신나는 첫 뒤집기","scenes":[{"slot":3,"prompt":"the baby rolls over triumphantly"}]}""";
        var llm = new FakeLlm { Response = gatewayJson };
        var viaGateway = await new PlutoVisionStorySynthesizer(new FakeVision { Throw = true }, llm).SynthesizeFromImageAsync(photo);
        Assert.Equal(PlutoVisionSource.Gateway, viaGateway.Source);
        Assert.Equal(1, llm.Calls);
        Assert.Equal("신나는 첫 뒤집기", viaGateway.DetectedMood);
        Assert.Equal(PlutoVisionMoodKind.Play, viaGateway.Attributes.Kind);
        Assert.Equal(BabyBgmTrack.Acoustic, viaGateway.SuggestedBgmTrack);
        Assert.Contains("rolls over triumphantly", viaGateway.StoryboardScenes[2].Prompt, StringComparison.Ordinal);
        Assert.Equal(0, viaGateway.DirectorLevel);   // 디렉터 미주입 → 광학 없음
        Assert.DoesNotContain("50mm lens", viaGateway.StoryboardScenes[0].Prompt, StringComparison.Ordinal);

        // ③ 비전 무응답(토큰 무시) + 게이트웨이 깨진 JSON → 예산 내 규칙 분석
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hung = await new PlutoVisionStorySynthesizer(new FakeVision { Hang = true }, new FakeLlm { Response = "not json {" })
        {
            VisionBudget = TimeSpan.FromMilliseconds(150),
        }.SynthesizeFromImageAsync(photo);
        sw.Stop();
        Assert.Equal(PlutoVisionSource.Heuristic, hung.Source);
        Assert.True(sw.ElapsedMilliseconds < 3000, "elapsed " + sw.ElapsedMilliseconds);

        // ④ 파일 없음 · 의존성 없음 → 그래도 4컷
        var missing = await new PlutoVisionStorySynthesizer().SynthesizeFromImageAsync(Path.Combine(_dir, "nope.png"), "강아지랑 공원 산책");
        Assert.Equal(PlutoVisionSource.Heuristic, missing.Source);
        Assert.Equal(PlutoVisionMoodKind.Outdoor, missing.Attributes.Kind);
        Assert.True(missing.Attributes.IsPet);
        Assert.Equal(PlutoMilestoneOrchestrator.DefaultPetAnchor, missing.PersonaConsistencyAnchor);
        Assert.Equal(4, missing.StoryboardScenes.Count);
        Assert.All(missing.StoryboardScenes, s => Assert.Contains("the pet", s.Prompt, StringComparison.Ordinal));

        // ⑤ 호출자 취소만 전파
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PlutoVisionStorySynthesizer().SynthesizeFromImageAsync(photo, null, cts.Token));
    }

    private static int CountOf(string s, string token)
    {
        var n = 0;
        for (var i = s.IndexOf(token, StringComparison.Ordinal); i >= 0; i = s.IndexOf(token, i + token.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
