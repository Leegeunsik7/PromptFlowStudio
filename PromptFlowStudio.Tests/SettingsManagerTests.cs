using Microsoft.Extensions.Configuration;
using PromptFlowStudio.Services.Configuration;
using PromptFlowStudio.Services.Render;
using PromptFlowStudio.ViewModels;

namespace PromptFlowStudio.Tests;

/// <summary>[⚙️ 환경설정] — user_settings.json 우선 · 번들 appsettings 폴백 · 원자적 저장 · 손상 복원력.</summary>
public class SettingsManagerTests
{
    private static IConfiguration Bundled(string? url = "http://10.0.0.5:8188/", string? mode = "Cloud")
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ComfyUI:BaseUrl"] = url,
            ["Render:DefaultMode"] = mode,
        }).Build();

    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pfs-settings-" + Guid.NewGuid().ToString("N"));
        return Path.Combine(dir, AppSettingsManager.FileName);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void SettingsManager_SavesAndLoads_UserSettingsJson()
    {
        var path = TempFile();
        try
        {
            // 저장본 없음 → 번들 기본값(정규화)
            var m = new AppSettingsManager(Bundled(), path);
            Assert.False(m.LoadedFromUser);
            Assert.Equal("http://10.0.0.5:8188", m.ComfyUiUrl);
            Assert.Equal(RenderEngineKind.Cloud, m.RenderMode);

            var fired = 0;
            m.OnChanged += () => fired++;
            Assert.True(m.Save(" http://127.0.0.1:9999/ ", "mock"));
            Assert.Equal(1, fired);
            Assert.True(m.IsMockMode);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp"));

            // 재시작 — 사용자 저장본이 번들 기본값보다 우선
            var reloaded = new AppSettingsManager(Bundled(), path);
            Assert.True(reloaded.LoadedFromUser);
            Assert.Equal("http://127.0.0.1:9999", reloaded.ComfyUiUrl);
            Assert.Equal(RenderEngineKind.Mock, reloaded.RenderMode);

            // 손상 파일 → 번들 기본값으로 Graceful 폴백
            File.WriteAllText(path, "{ not json");
            var broken = new AppSettingsManager(Bundled(), path);
            Assert.False(broken.LoadedFromUser);
            Assert.Equal("http://10.0.0.5:8188", broken.ComfyUiUrl);

            // 번들 값조차 없으면 내장 기본값
            var empty = new AppSettingsManager(Bundled(null, null), path + ".none");
            Assert.Equal(AppSettingsManager.DefaultComfyUiUrl, empty.ComfyUiUrl);
            Assert.Equal(RenderEngineKind.ComfyUi, empty.RenderMode);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void SettingsViewModel_NonAdminSave_PersistsModeWithoutTouchingKeys()
    {
        var path = TempFile();
        try
        {
            var m = new AppSettingsManager(Bundled(), path);
            var vm = new SettingsViewModel(m, wan: null, gemini: null, engines: null, () => new FfmpegLocation(null, "미설치"))
            {
                ComfyUiUrl = "http://192.168.0.2:8188",
                RenderMode = RenderEngineKind.Mock,
                DashScopeKey = "sk-should-not-persist",
            };
            Assert.True(vm.Save(includeKeys: false));
            Assert.True(vm.StatusOk);
            Assert.Equal("http://192.168.0.2:8188", m.ComfyUiUrl);
            Assert.True(m.IsMockMode);
            Assert.DoesNotContain("sk-", File.ReadAllText(path));   // user_settings.json 에 키 평문 금지
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void MockMode_ForcesSimulation_OnRenderEngineAdapter()
    {
        var mock = true;
        var engine = new VideoEngineRenderExecutionEngine(null, null, null) { ForceSimulation = () => mock };
        Assert.False(engine.IsAvailable);
        mock = false;
        Assert.False(engine.IsAvailable);   // 선택기 없음 → 여전히 비가용(시뮬레이션)
    }
}
