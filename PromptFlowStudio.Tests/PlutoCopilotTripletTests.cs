using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>🎞 플루토 Multi-Variant Triplet Copilot — 3스타일(📷 화보 · 🎨 픽사풍 3D · 📼 16mm 레트로) · 로컬 폴백 · 아이덴티티 앵커.</summary>
public class PlutoCopilotTripletTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0);
    private const string Memo = "오늘 아기가 거실에서 처음으로 뒤집기 성공하고 방긋 웃었어";

    private sealed class FakeLlm : IPlutoLlmClient
    {
        public bool IsAvailable { get; init; } = true;
        public string? Response { get; init; }
        public Exception? Throw { get; init; }
        public int Calls;
        public string LastSystem = "";
        public string LastUser = "";

        public Task<string?> CompleteJsonAsync(string system, string user, double temperature, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            LastSystem = system; LastUser = user;
            if (Throw is not null) throw Throw;
            return Task.FromResult(Response);
        }
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-triplet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void AssertTripletShape(IReadOnlyList<PlutoScriptVariant> v)
    {
        Assert.Equal(3, v.Count);
        Assert.Equal(new[] { PlutoCopilot.StudioVariantId, PlutoCopilot.PixarVariantId, PlutoCopilot.RetroVariantId }, v.Select(x => x.VariantId).ToArray());
        Assert.Equal("📷 스튜디오 감성 화보", v[0].StyleName);
        Assert.Equal("🎨 픽사풍 3D 감성 애니", v[1].StyleName);
        Assert.Equal("📼 16mm 레트로 홈캠", v[2].StyleName);
        Assert.Contains("85mm f/1.4", v[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("creamy bokeh", v[0].Prompt, StringComparison.Ordinal);
        Assert.Contains("Pixar-style 3D", v[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("warm pastel lighting", v[1].Prompt, StringComparison.Ordinal);
        Assert.Contains("16mm", v[2].Prompt, StringComparison.Ordinal);
        Assert.Contains("film grain", v[2].Prompt, StringComparison.Ordinal);
        Assert.Contains("mom-cam", v[2].Prompt, StringComparison.Ordinal);
        foreach (var x in v)
        {
            Assert.Contains("Act 1 (0-4s) Hook:", x.Prompt, StringComparison.Ordinal);
            Assert.Contains("Act 2 (4-11s) Climax:", x.Prompt, StringComparison.Ordinal);
            Assert.Contains("Act 3 (11-15s) Emotional smile:", x.Prompt, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(x.HookTitle));
            Assert.False(string.IsNullOrWhiteSpace(x.CameraMovement));
            Assert.False(string.IsNullOrWhiteSpace(x.RecommendedBgm));
            Assert.True(x.Act1Hook.Length >= 8 && x.Act2Climax.Length >= 8 && x.Act3Ending.Length >= 8);
            Assert.EndsWith(", camera: " + x.CameraMovement, x.BuildStudioPrompt(), StringComparison.Ordinal);
        }
        Assert.Equal(3, v.Select(x => x.Prompt).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, v.Select(x => x.HookTitle).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, v.Select(x => x.CameraMovement).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoCopilot_GeneratesThreeDistinctStyleVariants_FromUserMemo()
    {
        const string json = "```json\n{\"variants\":[" +
            "{\"style\":\"retro\",\"hookTitle\":\"90년대 홈캠 감성 첫 뒤집기 📼\",\"act1\":\"grainy 16mm shot of baby straining on a rug\",\"act2\":\"baby flips over as the handheld camera wobbles\",\"act3\":\"baby grins up at the lens with a nostalgic smile\",\"camera\":\"handheld zoom-in\",\"bgm\":\"lullaby\"}," +
            "{\"style\":\"studio\",\"hookTitle\":\"화보처럼 남긴 첫 뒤집기 📷\",\"act1\":\"close portrait of baby kicking tiny legs on a playmat\",\"act2\":\"baby rolls onto the tummy in creamy bokeh\",\"act3\":\"baby beams a gummy smile under rim light\",\"camera\":\"85mm dolly-in\",\"bgm\":\"acoustic\"}," +
            "{\"style\":\"pixar\",\"hookTitle\":\"애니 속 주인공 첫 뒤집기 🎨\",\"act1\":\"3D animated baby wiggling with big curious eyes\",\"act2\":\"baby bounces over with squash-and-stretch joy\",\"act3\":\"baby giggles with a warm animated smile\",\"camera\":\"crane-down orbit\",\"bgm\":\"musicbox\"}" +
            "]}\n```";
        var llm = new FakeLlm { Response = json };
        var v = await new PlutoCopilot(null, llm).DraftTripletVariantsAsync(Memo, Now);

        Assert.Equal(1, llm.Calls);   // 1회 호출로 3스타일 동시 생성
        Assert.Contains("THREE distinct", llm.LastSystem, StringComparison.Ordinal);
        Assert.Contains(Memo, llm.LastUser, StringComparison.Ordinal);
        AssertTripletShape(v);
        Assert.All(v, x => Assert.True(x.UsedLlm));
        Assert.Equal("화보처럼 남긴 첫 뒤집기 📷", v[0].HookTitle);   // 응답 순서 무관 · 스타일 키로 슬롯 매핑
        Assert.Equal("85mm dolly-in", v[0].CameraMovement);
        Assert.Equal(PromptFlowStudio.Services.Export.BabyBgmTrack.MusicBox, v[1].BgmTrack);
        Assert.Equal(PromptFlowStudio.Services.Export.BabyBgmTrack.Lullaby, v[2].BgmTrack);
        Assert.Contains("grainy 16mm shot", v[2].Prompt, StringComparison.Ordinal);

        // [부분 구제] 슬롯 1개 누락/불량 → 해당 슬롯만 로컬 보강
        var partial = new FakeLlm { Response = "{\"variants\":[{\"style\":\"pixar\",\"hookTitle\":\"애니 🎨\",\"act1\":\"3D animated baby wiggling\",\"act2\":\"baby bounces over joyfully\",\"act3\":\"baby giggles with a smile\",\"camera\":\"orbit\",\"bgm\":\"musicbox\"},{\"style\":\"studio\",\"act1\":\"x\",\"act2\":\"y\",\"act3\":\"z\"}]}" };
        var pv = await new PlutoCopilot(null, partial).DraftTripletVariantsAsync(Memo, Now);
        Assert.Equal(3, pv.Count);
        Assert.False(pv[0].UsedLlm);
        Assert.True(pv[1].UsedLlm);
        Assert.False(pv[2].UsedLlm);
        Assert.Contains("85mm f/1.4", pv[0].Prompt, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ArgumentException>(() => new PlutoCopilot().DraftTripletVariantsAsync("  "));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoCopilot_TripletFallback_ProducesThreeValidVariants_WhenLlmFails()
    {
        var boom = new FakeLlm { Throw = new InvalidOperationException("gemini down") };
        var failed = await new PlutoCopilot(null, boom).DraftTripletVariantsAsync(Memo, Now);
        var garbage = await new PlutoCopilot(null, new FakeLlm { Response = "not json {" }).DraftTripletVariantsAsync(Memo, Now);
        var unavailable = await new PlutoCopilot(null, new FakeLlm { IsAvailable = false, Response = "{}" }).DraftTripletVariantsAsync(Memo, Now);
        var offline = await new PlutoCopilot().DraftTripletVariantsAsync(Memo, Now);

        Assert.Equal(1, boom.Calls);
        foreach (var v in new[] { failed, garbage, unavailable, offline })
        {
            AssertTripletShape(v);
            Assert.All(v, x => Assert.False(x.UsedLlm));
            Assert.All(v, x => Assert.Equal("첫 뒤집기", x.MilestoneKo));
            Assert.All(v, x => Assert.Contains("roll", x.Act1Hook, StringComparison.Ordinal));   // 마일스톤 3막 기반
            Assert.All(v, x => Assert.Contains("smile", x.Act3Ending, StringComparison.Ordinal));
        }

        // 결정론 — 동일 입력 → 동일 출력
        Assert.Equal(offline.Select(x => x.Prompt), failed.Select(x => x.Prompt));
        Assert.Equal(offline.Select(x => x.Prompt), new PlutoCopilot().DraftTripletLocal(Memo, Now).Select(x => x.Prompt));

        // 스타일별 카메라 · BGM 지시
        Assert.Contains("85mm", offline[0].CameraMovement, StringComparison.Ordinal);
        Assert.Contains("orbit", offline[1].CameraMovement, StringComparison.Ordinal);
        Assert.Contains("handheld", offline[2].CameraMovement, StringComparison.Ordinal);
        Assert.Equal(3, offline.Select(x => x.BgmTrack).Distinct().Count());

        // 하위 호환 — 단일 시나리오 API 불변
        var single = await new PlutoCopilot().DraftScriptFromMemoAsync(Memo, Now);
        Assert.Equal("첫 뒤집기", single.MilestoneKo);
        Assert.Equal(3, single.Acts.Count);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoCopilot_BlendsIdentityAnchor_IntoAllThreeVariants()
    {
        var dir = TempDir();
        try
        {
            using var vault = new PlutoIdentityVault(Path.Combine(dir, PlutoIdentityVault.FileName));
            var profile = new PlutoIdentityProfile
            {
                SubjectName = "하윤이",
                BirthDate = new DateTime(2026, 7, 5),
                Category = PlutoIdentityProfile.BabyCategory,
                Gender = "female",
                VisualFeatures = "charming dimples, soft curly baby hair, bright brown eyes",
                FavoriteItem = "fluffy bunny plush toy",
            };
            await vault.SaveProfileAsync(profile);
            var anchor = profile.GetConsistencyAnchor();

            var offline = await new PlutoCopilot { Identity = vault }.DraftTripletVariantsAsync(Memo, Now);
            Assert.Equal(3, offline.Count);
            Assert.All(offline, x => Assert.StartsWith(anchor + ", ", x.Prompt, StringComparison.Ordinal));
            Assert.All(offline, x => Assert.StartsWith(anchor, x.BuildStudioPrompt(), StringComparison.Ordinal));

            var llm = new FakeLlm { Response = "{\"variants\":[{\"style\":\"studio\",\"act1\":\"portrait of baby kicking legs\",\"act2\":\"baby rolls over in bokeh\",\"act3\":\"baby smiles softly at lens\"}]}" };
            var mixed = await new PlutoCopilot(null, llm) { Identity = vault }.DraftTripletVariantsAsync(Memo, Now);
            Assert.Contains(anchor, llm.LastUser, StringComparison.Ordinal);   // LLM 요청에도 앵커 전달
            Assert.True(mixed[0].UsedLlm);
            Assert.All(mixed, x => Assert.StartsWith(anchor + ", ", x.Prompt, StringComparison.Ordinal));
            Assert.All(mixed, x => Assert.Equal(1, CountOf(x.Prompt, anchor)));   // 멱등 — 중복 삽입 없음

            // 테마 불일치(반려동물 메모) → 아기 앵커 미삽입
            var pet = await new PlutoCopilot { Identity = vault }.DraftTripletVariantsAsync("강아지랑 공원 산책했어", Now);
            Assert.All(pet, x => Assert.DoesNotContain(anchor, x.Prompt, StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static int CountOf(string s, string sub)
    {
        var n = 0;
        for (var i = s.IndexOf(sub, StringComparison.Ordinal); i >= 0; i = s.IndexOf(sub, i + sub.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
