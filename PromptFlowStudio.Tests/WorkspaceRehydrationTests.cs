using System;
using System.IO;
using PromptFlowStudio.Services.Gallery;
using PromptFlowStudio.Services.Render;
using Xunit;

namespace PromptFlowStudio.Tests;

public class WorkspaceRehydrationTests
{
    [Fact]
    [Trait("Category", "FastUnit")]
    public void Workspace_RehydrateSettings_RestoresAllParameters()
    {
        // ① 확장 필드(durationMode · cameraMotion · aspectRatio) 명시 메타 → 그대로 복원
        const string json = "{\"title\":\"ws_1\",\"prompt\":\"neon city, slow 360-degree orbit shot around the subject\",\"seed\":4242,"
                          + "\"resolution\":\"720x1280\",\"durationSec\":15,\"durationMode\":\"Sec15_AmbientSlow\",\"cameraMotion\":\"Orbit\",\"aspectRatio\":\"9:16\"}";
        var s = WorkspaceRehydration.FromJson(@"C:\out\ws_1.mp4", json);
        Assert.True(s.HasMetadata);
        Assert.Equal("neon city, slow 360-degree orbit shot around the subject", s.Prompt);
        Assert.Equal("Orbit", s.CameraMotion);
        Assert.Equal(VideoDurationMode.Sec15_AmbientSlow, s.DurationMode);
        Assert.Equal("9:16", s.AspectRatio);
        Assert.Equal(WorkspaceRehydration.SizePortrait, s.WorkspaceSize);
        Assert.Equal(4242, s.Seed);

        // ② 렌더 큐 레거시 메타(확장 필드 없음) → durationSec · 프롬프트 카메라 토큰 · 해상도로 추정
        var legacy = WorkspaceRehydration.FromJson(@"C:\out\cut.mp4",
            "{\"prompt\":\"forest, crane up shot rising to reveal the scene\",\"seed\":7,\"resolution\":\"1280x720\",\"durationSec\":10}");
        Assert.Equal("Crane Up", legacy.CameraMotion);
        Assert.Equal(VideoDurationMode.Sec10_Dual, legacy.DurationMode);
        Assert.Equal("16:9", legacy.AspectRatio);
        Assert.Equal(WorkspaceRehydration.SizeLandscape, legacy.WorkspaceSize);

        // ③ 메타 없음/손상 → 파일명 기반 기본값 · 무예외
        var none = WorkspaceRehydration.FromJson(@"C:\out\proj_15s.mp4", null);
        Assert.False(none.HasMetadata);
        Assert.Equal(VideoDurationMode.Sec15_Triple, none.DurationMode);
        Assert.False(WorkspaceRehydration.FromJson("x.mp4", "{broken").HasMetadata);
        Assert.Equal(WorkspaceRehydration.SizeSquare, WorkspaceRehydration.ResolveWorkspaceSize("1024x1000", null));
        Assert.Equal(VideoDurationMode.Sec10_Dual, WorkspaceRehydration.ResolveDuration("10s", 0, ""));

        // ④ 단일 생성 사이드카 기록 → 로드 왕복(기존 메타는 덮어쓰지 않음)
        var dir = Path.Combine(Path.GetTempPath(), "pfs_rehydrate_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var media = Path.Combine(dir, "ws_single.mp4");
            File.WriteAllBytes(media, new byte[] { 0, 0, 0, 0x18 });
            Assert.True(WorkspaceRehydration.WriteIfMissing(media, "cat, dolly zoom vertigo effect on the subject", VideoDurationMode.Sec5_Single, "960*960", 99));
            Assert.False(WorkspaceRehydration.WriteIfMissing(media, "other", VideoDurationMode.Sec10_Dual, "1280*720", 1));
            var back = WorkspaceRehydration.Load(media);
            Assert.True(back.HasMetadata);
            Assert.Equal("cat, dolly zoom vertigo effect on the subject", back.Prompt);
            Assert.Equal("Dolly Zoom", back.CameraMotion);
            Assert.Equal(VideoDurationMode.Sec5_Single, back.DurationMode);
            Assert.Equal("1:1", back.AspectRatio);
            Assert.Equal(WorkspaceRehydration.SizeSquare, back.WorkspaceSize);
            Assert.Equal(99, back.Seed);
            Assert.False(WorkspaceRehydration.WriteIfMissing(Path.Combine(dir, "missing.mp4"), "p", VideoDurationMode.Sec5_Single, "1280*720", 0));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
