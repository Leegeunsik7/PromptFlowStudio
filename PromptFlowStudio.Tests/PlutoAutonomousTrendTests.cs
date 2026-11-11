using System.Text.Json;
using PromptFlowStudio.Services.Pluto;

namespace PromptFlowStudio.Tests;

/// <summary>☁️🔄 Pluto Drive 볼트 저장소(로컬 폴백 · 원자 기록) + 자율 트렌드 수확기(신선도 점검 → +2 XP 적재).</summary>
public class PlutoAutonomousTrendTests
{
    private static string TempRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "pfs-pluto-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task PlutoDriveStorageManager_FallsBackToLocal_WhenDriveMissing()
    {
        var root = TempRoot();
        try
        {
            var missingDrive = Path.Combine(root, "G_unmounted", "DATA", "Pluto");   // 볼트 루트(DATA) 부재 = Drive 미마운트
            var local = Path.Combine(root, "Local", "Data", "Pluto");
            var legacy = Path.Combine(root, "Legacy");
            Directory.CreateDirectory(legacy);
            await File.WriteAllTextAsync(Path.Combine(legacy, "seed.json"), "{\"xp\":7}");
            var sm = new PlutoDriveStorageManager(missingDrive, local, legacy);

            Assert.False(sm.IsCloudSynced);
            Assert.Equal(local, sm.ActiveRoot);
            Assert.Equal("💾 로컬 폴백", sm.StatusLabel);
            Assert.True(Directory.Exists(local));
            Assert.False(Directory.Exists(Path.GetDirectoryName(missingDrive)));   // 미마운트 경로에 임의 폴더 생성 금지

            var path = sm.ResolveDataPath("trend.json");
            Assert.Equal(Path.Combine(local, "trend.json"), path);

            Assert.True(await sm.WriteAllTextAtomicAsync("trend.json", "{\"a\":1}"));
            Assert.Equal("{\"a\":1}", await sm.ReadAllTextAsync("trend.json"));
            Assert.Equal(string.Empty, await sm.ReadAllTextAsync("missing.json"));

            Assert.Equal("{\"xp\":7}", await sm.ReadAllTextAsync("seed.json"));   // 레거시 위치 → 폴백 복사 시드(누적 XP 보존)
            Assert.True(File.Exists(Path.Combine(legacy, "seed.json")));          // 원본 유지

            // 학습 서비스 결선 — 폴백 루트에 영속
            using var learning = new PlutoLearningService(sm);
            await learning.AddBonusXpAsync("xp:test", 2);
            Assert.True(File.Exists(Path.Combine(local, PlutoLearningService.FileName)));
            Assert.Equal(Path.Combine(local, PlutoLearningService.FileName), learning.StoragePath);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task PlutoDriveStorageManager_UsesDrive_WhenVaultRootPresent()
    {
        var root = TempRoot();
        try
        {
            var vault = Path.Combine(root, "G", "DATA");
            Directory.CreateDirectory(vault);
            var primary = Path.Combine(vault, "Pluto");
            var local = Path.Combine(root, "Local");
            Directory.CreateDirectory(local);
            await File.WriteAllTextAsync(Path.Combine(local, PlutoIdentityVault.FileName), "[]");
            var sm = new PlutoDriveStorageManager(primary, local);

            Assert.True(sm.IsCloudSynced);
            Assert.Equal(primary, sm.ActiveRoot);
            Assert.StartsWith("☁️ ", sm.StatusLabel);
            Assert.Equal(Path.Combine(primary, PlutoIdentityVault.FileName), sm.ResolveDataPath(PlutoIdentityVault.FileName));
            Assert.True(File.Exists(Path.Combine(primary, PlutoIdentityVault.FileName)));   // 로컬 폴백 사본 → Drive 시드

            Assert.True(await sm.WriteAllTextAtomicAsync("x.json", "1"));
            Assert.Equal("1", await File.ReadAllTextAsync(Path.Combine(primary, "x.json")));
            Assert.False(File.Exists(Path.Combine(local, "x.json")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task PlutoDriveStorageManager_UsesAtomicWrite_PreventingPartialCorruption()
    {
        var root = TempRoot();
        try
        {
            var vault = Path.Combine(root, "G", "DATA");
            Directory.CreateDirectory(vault);
            var primary = Path.Combine(vault, "Pluto");
            var local = Path.Combine(root, "Local");
            var sm = new PlutoDriveStorageManager(primary, local);
            var changed = 0;
            sm.Changed += () => Interlocked.Increment(ref changed);

            Assert.True(await sm.WriteAllTextAtomicAsync("mem.json", "{\"v\":1}"));
            Assert.True(await sm.WriteAllTextAtomicAsync("mem.json", "{\"v\":2}"));   // 교체 기록
            var target = Path.Combine(primary, "mem.json");
            Assert.Equal("{\"v\":2}", await File.ReadAllTextAsync(target));
            Assert.Empty(Directory.GetFiles(primary, "*.tmp"));

            // 클라우드 동기화 클라이언트가 대상 파일을 잠근 상황(삭제/교체 불가) → 원본 무손상 · 로컬 폴백으로 기록 · 임시 파일 잔존 없음
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.True(await sm.WriteAllTextAtomicAsync("mem.json", "{\"v\":3}"));
            }
            Assert.Equal("{\"v\":2}", await File.ReadAllTextAsync(target));
            Assert.Equal("{\"v\":3}", await File.ReadAllTextAsync(Path.Combine(local, "mem.json")));
            Assert.Empty(Directory.GetFiles(primary, "*.tmp"));
            Assert.Empty(Directory.GetFiles(local, "*.tmp"));
            Assert.False(sm.IsCloudSynced);   // 실패 직후 Drive 일시 차단(ProbeInterval)
            Assert.True(changed >= 1);
            Assert.Equal("{\"v\":3}", await sm.ReadAllTextAsync("mem.json"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task PlutoAutonomousTrendHarvester_TriggersAutoHarvest_WhenDataStale()
    {
        var root = TempRoot();
        try
        {
            var local = Path.Combine(root, "Local");
            Directory.CreateDirectory(local);
            var staleUtc = DateTime.UtcNow.AddHours(-13);
            var seed = new Dictionary<string, PlutoLearningService.Entry>
            {
                [PlutoLearningService.TrendIngestSourceKey] = new() { BonusXp = 2, Interactions = 1, LastUtc = staleUtc },
            };
            await File.WriteAllTextAsync(Path.Combine(local, PlutoLearningService.FileName), JsonSerializer.Serialize(seed));
            var sm = new PlutoDriveStorageManager(null, local);
            using var learning = new PlutoLearningService(sm, new Random(1));
            var engine = new PlutoTrendEngine(learning);   // 생성기 미주입 → 큐레이션 카탈로그 표본(결정론)
            using var harvester = new PlutoAutonomousTrendHarvester(learning, sm, engine);
            var events = 0;
            harvester.Changed += () => Interlocked.Increment(ref events);

            await learning.EnsureLoadedAsync();
            Assert.True(harvester.IsStale(DateTime.UtcNow));
            Assert.Empty(learning.GetActiveViralTrends());
            var before = learning.CurrentXP;

            var r = await harvester.TryHarvestAsync();

            Assert.True(r.Harvested);
            Assert.Equal("stale", r.Reason);
            Assert.Equal(PlutoLearningService.TrendIngestXp, r.XpAwarded);
            Assert.Equal(before + PlutoLearningService.TrendIngestXp, learning.CurrentXP);
            Assert.Equal(PlutoTrendAnalyzer.KnownTags.Count, r.TokenCount);
            Assert.NotNull(r.TopTag);
            Assert.NotEmpty(learning.GetActiveViralTrends());   // 활성 프롬프트 모디파이어 즉시 갱신
            Assert.False(harvester.IsStale(DateTime.UtcNow));
            Assert.True(harvester.LastHarvestUtc > staleUtc);
            Assert.True(events >= 2);
            Assert.True(File.Exists(Path.Combine(local, PlutoAutonomousTrendHarvester.PatternsFileName)));
            Assert.Contains("HookStructure", await sm.ReadAllTextAsync(PlutoAutonomousTrendHarvester.PatternsFileName));

            // 신선 → 재수확 안 함(XP 파밍 방지)
            var again = await harvester.TryHarvestAsync();
            Assert.False(again.Harvested);
            Assert.Equal("fresh", again.Reason);
            Assert.Equal(before + PlutoLearningService.TrendIngestXp, learning.CurrentXP);

            // 영속 확인 — 새 인스턴스 재적재
            using var reloaded = new PlutoLearningService(sm);
            await reloaded.EnsureLoadedAsync();
            Assert.Equal(learning.CurrentXP, reloaded.CurrentXP);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task PlutoAutonomousTrendHarvester_Start_RunsInitialHarvestInBackground()
    {
        var root = TempRoot();
        try
        {
            var sm = new PlutoDriveStorageManager(null, Path.Combine(root, "Local"));
            using var learning = new PlutoLearningService(sm);
            using var harvester = new PlutoAutonomousTrendHarvester(learning, sm, null, startupDelay: TimeSpan.Zero, interval: TimeSpan.FromHours(6));
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            harvester.Changed += () => { if (harvester.LastResult is { Harvested: true }) done.TrySetResult(); };

            harvester.Start();
            harvester.Start();   // 멱등
            Assert.True(harvester.IsRunning);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(PlutoLearningService.TrendIngestXp, learning.CurrentXP);
            Assert.StartsWith("🔄 자율 탐색 가동 중 (최근 갱신: 방금 전)", harvester.StatusLabel);
            harvester.Dispose();
            Assert.False(harvester.IsRunning);
        }
        finally { Cleanup(root); }
    }

    [Theory]
    [InlineData(10, "방금 전")]
    [InlineData(90, "1분 전")]
    [InlineData(3 * 3600, "3시간 전")]
    [InlineData(50 * 3600, "2일 전")]
    public void PlutoAutonomousTrendHarvester_FormatTimeAgo(int seconds, string expected)
    {
        var now = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, PlutoAutonomousTrendHarvester.FormatTimeAgo(now.AddSeconds(-seconds), now));
        Assert.Equal("기록 없음", PlutoAutonomousTrendHarvester.FormatTimeAgo(null, now));
    }
}
