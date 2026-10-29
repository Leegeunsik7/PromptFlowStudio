using PromptFlowStudio.Services.AudioTelemetry;

namespace PromptFlowStudio.Tests;

/// <summary>v17.29.0: 실시간 오디오/텔레메트리 모니터 — 지표 정확도 · 스펙트럼 밴드 피크 · 클립 · 무할당 회귀 잠금.</summary>
[Trait("Category", "FastUnit")]
public class RealtimeAudioTelemetryMonitorTests
{
    [Fact]
    public void Empty_Analyze_ReturnsFloor()
    {
        using var m = new RealtimeAudioTelemetryMonitor();
        var s = m.Analyze();
        Assert.Equal(0f, s.Rms);
        Assert.Equal(RealtimeAudioTelemetryMonitor.FloorDb, s.RmsDb);
        Assert.False(s.Clipping);
    }

    [Fact]
    public void Sine_RmsPeakZeroCrossings_MatchTheory()
    {
        using var m = new RealtimeAudioTelemetryMonitor { ToneHz = 1000f, ToneAmp = 0.5f };
        m.InjectTone(RealtimeAudioTelemetryMonitor.FftSize);
        var s = m.Analyze();
        Assert.InRange(s.Rms, 0.5f / MathF.Sqrt(2f) - 0.01f, 0.5f / MathF.Sqrt(2f) + 0.01f);
        Assert.InRange(s.Peak, 0.49f, 0.501f);
        // 2048 샘플 @48 kHz = 42.67 ms → 1 kHz 는 약 85 회 부호 반전
        Assert.InRange(s.ZeroCrossings, 83, 87);
    }

    [Fact]
    public void Sine_SpectrumPeak_LandsInToneBand()
    {
        using var m = new RealtimeAudioTelemetryMonitor { ToneHz = 1000f, ToneAmp = 0.5f };
        m.InjectTone(RealtimeAudioTelemetryMonitor.FftSize);
        m.Analyze();
        var bands = m.BandLevels;
        int best = 0;
        for (int b = 1; b < bands.Length; b++) if (bands[b] > bands[best]) best = b;
        double center = RealtimeAudioTelemetryMonitor.BandCenterHz(best);
        Assert.InRange(center, 800.0, 1250.0);
        Assert.True(bands[best] > 200); // −6 dBFS ≈ 240
    }

    [Fact]
    public void FullScale_IsClipping_AndCounted()
    {
        using var m = new RealtimeAudioTelemetryMonitor();
        Span<float> buf = stackalloc float[512];
        for (int i = 0; i < buf.Length; i++) buf[i] = (i & 1) == 0 ? 1f : -1f;
        m.Push(buf);
        var s = m.Analyze();
        Assert.True(s.Clipping);
        Assert.Equal(1, m.ClipEvents);
    }

    [Fact]
    public void Analyze_IsAllocationFree()
    {
        using var m = new RealtimeAudioTelemetryMonitor();
        m.InjectTone(RealtimeAudioTelemetryMonitor.FftSize);
        m.Analyze(); // 워밍업(JIT)
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) { m.InjectTone(480); m.Analyze(); }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Subscriber_Exception_IsIsolated()
    {
        using var m = new RealtimeAudioTelemetryMonitor();
        m.OnChange += () => throw new InvalidOperationException("boom");
        m.Start();
        m.Stop();
        Assert.False(m.IsRunning);
    }
}
