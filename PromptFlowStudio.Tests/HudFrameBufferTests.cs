using PromptFlowStudio.Services.AudioTelemetry;
using PromptFlowStudio.Shared.Hud;

namespace PromptFlowStudio.Tests;

/// <summary>v17.30.0: Shared HUD 캔버스 프레임 버퍼 — 버전 게이트 · 양자화 · 링 순서 · 무할당 회귀 잠금.</summary>
[Trait("Category", "Integration")]
public class HudFrameBufferTests
{
    [Fact]
    public void TryCopy_OnlyWhenVersionChanged()
    {
        var b = new HudFrameBuffer(4);
        var dst = new byte[4];
        long seen = b.Version;
        Assert.False(b.TryCopy(dst, ref seen));
        b.Write(new byte[] { 1, 2, 3, 4 });
        Assert.True(b.TryCopy(dst, ref seen));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, dst);
        Assert.False(b.TryCopy(dst, ref seen));
    }

    [Fact]
    public void WriteSamples_QuantizesAroundMidAndKeepsPeakSign()
    {
        var b = new HudFrameBuffer(2);
        b.WriteSamples(new[] { 0.1f, -1f, 0f, 1f });
        var dst = new byte[2];
        long seen = -1;
        Assert.True(b.TryCopy(dst, ref seen));
        Assert.Equal(1, dst[0]);    // 구간 [0.1,-1] → -1 피크 보존
        Assert.Equal(255, dst[1]);  // 구간 [0, 1] → +1
    }

    [Fact]
    public void Push_UnrollsRingOldestFirst()
    {
        var b = new HudFrameBuffer(3);
        b.Push(0f); b.Push(0.5f); b.Push(1f); b.Push(0f);   // 마지막이 가장 오래된 0f 를 덮음
        var dst = new byte[3];
        long seen = -1;
        b.TryCopy(dst, ref seen);
        Assert.Equal(new byte[] { 128, 255, 0 }, dst);
    }

    [Fact]
    public void HotPath_IsAllocationFree()
    {
        var b = new HudFrameBuffer(256);
        var pcm = new float[2048];
        var dst = new byte[256];
        long seen = -1;
        b.WriteSamples(pcm); b.Push(0.3f); b.TryCopy(dst, ref seen); // 워밍업(JIT)
        // 최소 창(3) 판정 — 병렬 실행 중 티어드 JIT/OSR 재컴파일의 일회성 할당 배제(AllocationProbe)
        Assert.Equal(0, AllocationProbe.MinAllocated(() => { for (int i = 0; i < 100; i++) { b.WriteSamples(pcm); b.Push(0.3f); b.TryCopy(dst, ref seen); } }));
    }

    [Fact]
    public void Monitor_CopyLastPcm_ReturnsAnalyzedWindow()
    {
        using var m = new RealtimeAudioTelemetryMonitor { ToneHz = 1000f, ToneAmp = 0.5f };
        m.InjectTone(RealtimeAudioTelemetryMonitor.FftSize);
        m.Analyze();
        var pcm = new float[RealtimeAudioTelemetryMonitor.FftSize];
        int n = m.CopyLastPcm(pcm);
        Assert.Equal(RealtimeAudioTelemetryMonitor.FftSize, n);
        float peak = 0f;
        for (int i = 0; i < n; i++) peak = MathF.Max(peak, MathF.Abs(pcm[i]));
        Assert.InRange(peak, 0.49f, 0.501f);
    }

    [Fact]
    public void Ctor_RejectsInvalidCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HudFrameBuffer(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HudFrameBuffer(5000));
    }
}
