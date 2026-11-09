using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>🍼 플루토 아이덴티티 볼트 — D-Day · 캐릭터 일관성 앵커 · JSON 영속 · 성장 뱃지 · 엔진 앵커 결선.</summary>
public class PlutoIdentityVaultTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static PlutoIdentityProfile Hayoon() => new()
    {
        SubjectName = "하윤이",
        BirthDate = new DateTime(2026, 7, 5),
        Category = PlutoIdentityProfile.BabyCategory,
        Gender = "female",
        VisualFeatures = "charming dimples, soft curly baby hair,\n bright brown eyes, tiny fingers",
        FavoriteItem = "fluffy bunny plush toy",
    };

    [Fact]
    [Trait("Category", "FastUnit")]
    public void PlutoIdentityVault_CalculatesDDay_AndGeneratesConsistencyAnchor()
    {
        var p = Hayoon();
        Assert.Equal(128, p.DDayOn(new DateTime(2026, 11, 10)));
        Assert.Equal(1, p.DDayOn(new DateTime(2026, 7, 5)));     // 출생일 당일 → 최소 1
        Assert.Equal(1, p.DDayOn(new DateTime(2026, 6, 1)));     // 미래 출생일(태명) → 최소 1
        Assert.Equal(Math.Max(1, (int)(DateTime.Today - p.BirthDate.Date).TotalDays), p.CurrentDDay);

        Assert.Equal("same consistent baby, female, charming dimples, soft curly baby hair, bright brown eyes, tiny fingers, holding fluffy bunny plush toy",
            p.GetConsistencyAnchor());

        var pet = new PlutoIdentityProfile { SubjectName = "콩이", Category = "pet", VisualFeatures = "fluffy soft fur", FavoriteItem = "" };
        Assert.True(pet.IsPet);
        Assert.Equal("same consistent pet, fluffy soft fur", pet.GetConsistencyAnchor());

        // 앵커 선두 삽입 · 멱등 · 빈 앵커 → 원문
        var anchor = p.GetConsistencyAnchor();
        var once = PlutoIdentityVault.ApplyAnchor("baby giggling in a sunlit nursery", anchor);
        Assert.StartsWith(anchor + ", ", once, StringComparison.Ordinal);
        Assert.Equal(once, PlutoIdentityVault.ApplyAnchor(once, anchor));
        Assert.Equal("x", PlutoIdentityVault.ApplyAnchor("x", ""));

        // 테마 일치 시에만 앵커
        Assert.Equal(anchor, PlutoIdentityVault.AnchorFor(p, StudioThemeCategory.BabyParenting));
        Assert.Equal("", PlutoIdentityVault.AnchorFor(p, StudioThemeCategory.HealingNature));
        Assert.Equal("", PlutoIdentityVault.AnchorFor(pet, StudioThemeCategory.BabyParenting));
        Assert.NotEqual("", PlutoIdentityVault.AnchorFor(pet, StudioThemeCategory.PetsCompanions));
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoIdentityVault_SavesAndLoadsProfile_Persistently()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, PlutoIdentityVault.FileName);
            string firstId;
            using (var v = new PlutoIdentityVault(path))
            {
                Assert.Null(await v.GetActiveProfileAsync());
                var changed = 0;
                v.Changed += () => changed++;
                var p = Hayoon();
                await v.SaveProfileAsync(p);
                firstId = p.Id;
                Assert.False(string.IsNullOrWhiteSpace(firstId));
                Assert.Equal(1, changed);

                // 두 번째 활성 프로필 저장 → 첫 프로필 비활성
                await v.SaveProfileAsync(new PlutoIdentityProfile { SubjectName = "콩이", Category = PlutoIdentityProfile.PetCategory, BirthDate = new DateTime(2025, 1, 1) });
                Assert.Equal("콩이", (await v.GetActiveProfileAsync())!.SubjectName);
                var again = Hayoon();
                again.Id = firstId;   // 동일 Id 업서트 → 재활성
                await v.SaveProfileAsync(again);
            }
            Assert.True(File.Exists(path));

            using (var v2 = new PlutoIdentityVault(path))
            {
                var loaded = await v2.GetActiveProfileAsync();
                Assert.NotNull(loaded);
                Assert.Equal(firstId, loaded!.Id);
                Assert.Equal("하윤이", loaded.SubjectName);
                Assert.Equal(new DateTime(2026, 7, 5), loaded.BirthDate);
                Assert.Equal(PlutoIdentityProfile.BabyCategory, loaded.Category);
                Assert.Equal("charming dimples, soft curly baby hair, bright brown eyes, tiny fingers", loaded.VisualFeatures);   // 개행 정규화
                Assert.Equal("fluffy bunny plush toy", loaded.FavoriteItem);
                Assert.Equal(2, v2.Profiles.Count);

                await v2.DeleteProfileAsync(firstId);
                Assert.Null(await v2.GetActiveProfileAsync());
                Assert.Single(v2.Profiles);
            }

            // 손상 파일 → 빈 볼트로 재시작(예외 없음)
            await File.WriteAllTextAsync(path, "{not json");
            using var v3 = new PlutoIdentityVault(path);
            Assert.Null(await v3.GetActiveProfileAsync());
            await Assert.ThrowsAsync<ArgumentException>(() => v3.SaveProfileAsync(new PlutoIdentityProfile { SubjectName = "  " }));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void Workspace_UpdatesGrowthBadge_FromActiveIdentityProfile()
    {
        var today = new DateTime(2026, 11, 10);
        var baby = Hayoon();
        Assert.Equal("🍼 하윤이 · D+128", baby.FormatBadge(today));
        Assert.Equal("🍼 하윤이 · D+128", PlutoIdentityVault.BadgeFor(baby, null, today));
        Assert.Equal("🍼 하윤이 · 첫 뒤집기", PlutoIdentityVault.BadgeFor(baby, "  첫 뒤집기 ", today));   // 사용자 문구 우선

        var pet = new PlutoIdentityProfile { SubjectName = "콩이", Category = PlutoIdentityProfile.PetCategory, BirthDate = new DateTime(2026, 10, 1) };
        Assert.Equal("🐾 콩이 · D+40", PlutoIdentityVault.BadgeFor(pet, "", today));

        Assert.Null(PlutoIdentityVault.BadgeFor(null, null, today));   // 프로필 없음 → 기존 성장 단계 뱃지
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public async Task PlutoEngines_PrependConsistencyAnchor_FromActiveProfile()
    {
        var dir = TempDir();
        try
        {
            using var v = new PlutoIdentityVault(Path.Combine(dir, PlutoIdentityVault.FileName));
            var p = Hayoon();
            await v.SaveProfileAsync(p);
            var anchor = p.GetConsistencyAnchor();
            var now = new DateTime(2026, 11, 10, 9, 0, 0);

            var gen = new PlutoGenerativeEngine(null, new Random(3)) { Identity = v };
            var baby = await gen.SynthesizeAsync(StudioThemeCategory.BabyParenting, 3, now);
            Assert.All(baby, t => Assert.StartsWith(anchor + ", ", t.VideoPrompt, StringComparison.Ordinal));
            var healing = await gen.SynthesizeAsync(StudioThemeCategory.HealingNature, 2, now);
            Assert.All(healing, t => Assert.DoesNotContain(anchor, t.VideoPrompt, StringComparison.Ordinal));   // 테마 불일치 → 앵커 없음

            var llm = gen.ParseLlmTrends("{\"trends\":[{\"title\":\"첫 걸음\",\"hook\":\"드디어 걸었어요 🥹\",\"prompt\":\"toddler taking first wobbly steps on a rug\"}]}",
                StudioThemeCategory.BabyParenting, 2, now);
            Assert.NotNull(llm);
            Assert.StartsWith(anchor, llm![0].VideoPrompt, StringComparison.Ordinal);

            var copilot = new PlutoCopilot { Identity = v };
            var script = await copilot.DraftScriptFromMemoAsync("오늘 아기가 처음으로 뒤집기를 했어요", now);
            Assert.StartsWith(anchor + ", ", script.VideoPrompt, StringComparison.Ordinal);
            Assert.StartsWith(anchor, script.BuildStudioPrompt(), StringComparison.Ordinal);

            // 볼트 미주입 → 기존 동작 불변
            var plain = new PlutoGenerativeEngine(null, new Random(3)).Synthesize(StudioThemeCategory.BabyParenting, 1, now);
            Assert.StartsWith("Hook: ", plain[0].VideoPrompt, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
