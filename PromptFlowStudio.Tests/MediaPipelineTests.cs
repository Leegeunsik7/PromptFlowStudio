using System.Threading.Tasks;
using PromptFlowStudio.Services.AI;
using Xunit;

namespace PromptFlowStudio.Tests;

public class MediaPipelineTests
{
    [Fact]
    public async Task AudioSfx_PromptMapping_ZeroAlloc_Check()
    {
        const string storm = "A lighthouse in a violent storm with lightning, cinematic dolly";
        const string baby = "아기가 아기방에서 웃으며 기어가는 장면";
        const string none = "abstract geometric shapes";

        var a = AudioSfxEngine.GenerateAudioPrompt(storm);
        Assert.Contains("thunder", a);
        Assert.Equal("thunder", AudioSfxEngine.ClassifySfxKey(storm));
        var b = AudioSfxEngine.GenerateAudioPrompt(baby);
        Assert.Contains("giggles", b);
        Assert.Contains("indoor", b);
        Assert.Equal("ambient", AudioSfxEngine.ClassifySfxKey(none));
        Assert.Same(AudioSfxEngine.GenerateAudioPrompt(null), AudioSfxEngine.GenerateAudioPrompt(""));
        Assert.Same(a, AudioSfxEngine.GenerateAudioPrompt(storm));   // 캐시 참조 반환

        var sink = 0;
        var bytes = AllocationProbe.MinAllocated(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                sink += AudioSfxEngine.GenerateAudioPrompt(storm).Length;
                sink += AudioSfxEngine.GenerateAudioPrompt(baby).Length;
                sink += AudioSfxEngine.GenerateAudioPrompt(none).Length;
            }
        });
        Assert.Equal(0, bytes);
        Assert.True(sink > 0);

        // Graceful: 영상 부재 → 예외 없이 Applied=false(원본 유지).
        var r = await AudioSfxEngine.AutoFoleyAsync(new FfmpegAudioMuxer(), @"C:\definitely\not\real\clip.mp4", storm);
        Assert.False(r.Applied);
    }
}
