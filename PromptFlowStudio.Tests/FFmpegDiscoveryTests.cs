using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Tests;

/// <summary>FFmpeg 탐색 — 배포 동봉(tools/) 우선 → 앱 폴더 → 시스템 PATH 폴백 순서 잠금.</summary>
public class FFmpegDiscoveryTests
{
    private static readonly string Exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return Path.GetFullPath(path);
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void FFmpegDiscovery_FindsLocalToolsBinary_BeforeSystemPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "pfs-ffmpeg-" + Guid.NewGuid().ToString("N"));
        const string system = @"C:\sys\ffmpeg.exe";
        try
        {
            Directory.CreateDirectory(root);

            // 로컬 없음 → 시스템 PATH 폴백
            var none = FfmpegLocator.Locate(root, () => system);
            Assert.Equal(system, none.Path);
            Assert.Equal("시스템 PATH", none.Source);

            // ③ 앱 폴더 → ② tools → ① tools/ffmpeg/bin 순으로 상위 우선순위가 덮어씀
            var appDir = Touch(Path.Combine(root, Exe));
            Assert.Equal(appDir, FfmpegLocator.Locate(root, () => system).Path);

            var tools = Touch(Path.Combine(root, "tools", Exe));
            Assert.Equal(tools, FfmpegLocator.Locate(root, () => system).Path);

            var bin = Touch(Path.Combine(root, "tools", "ffmpeg", "bin", Exe));
            var hit = FfmpegLocator.Locate(root, () => system);
            Assert.Equal(bin, hit.Path);
            Assert.True(hit.Found);
            Assert.Equal("로컬 tools", hit.Source);

            // 어디에도 없으면 미설치(폴백 예외 격리)
            var miss = FfmpegLocator.Locate(Path.Combine(root, "empty"), () => throw new InvalidOperationException("PATH"));
            Assert.False(miss.Found);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [Fact]
    [Trait("Category", "FastUnit")]
    public void StitchService_UsesInjectedResolver_ForAvailability()
    {
        var svc = new FFmpegVideoStitchService(() => null, (_, _, _) => Task.FromResult(true));
        Assert.False(svc.IsFfmpegAvailable);
        var withExe = new FFmpegVideoStitchService(() => @"C:\tools\ffmpeg\bin\ffmpeg.exe", (_, _, _) => Task.FromResult(true));
        Assert.True(withExe.IsFfmpegAvailable);
    }
}
