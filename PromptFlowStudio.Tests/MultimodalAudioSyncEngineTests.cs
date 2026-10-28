using System;
using System.Threading.Tasks;
using PromptFlowStudio.Services.AI.Wan;

namespace PromptFlowStudio.Tests;

[Trait("Category", "FastUnit")]
public class MultimodalAudioSyncEngineTests
{
    private static byte[] GenerateSineWavePcm16(double freqHz, int sampleRateHz, int sampleCount, double amplitude = 0.5)
    {
        var bytes = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRateHz;
            var sample = (short)(Math.Sin(2 * Math.PI * freqHz * t) * amplitude * short.MaxValue);
            bytes[i * 2] = (byte)(sample & 0xFF);
            bytes[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }
        return bytes;
    }

    [Fact]
    public void AnalyzeFrame_PureSineWave_DetectsCorrectDominantFrequency()
    {
        var motion = new WanDynamicMotionController();
        var scheduler = new HeterogeneousTaskScheduler();
        var engine = new MultimodalAudioSyncEngine(motion, scheduler);

        const int sampleRate = 44100;
        const double trueFreq = 440.0;
        var pcm = GenerateSineWavePcm16(trueFreq, sampleRate, 1024, amplitude: 0.8);

        var features = engine.AnalyzeFrame(pcm, sampleRate);

        Assert.InRange(features.DominantFrequencyHz, trueFreq - 50, trueFreq + 50);
        Assert.True(features.Amplitude > 0.3, $"실제 Amplitude={features.Amplitude}");
    }

    [Fact]
    public void AnalyzeFrame_Silence_ReturnsZeroAmplitudeAndNoBeat()
    {
        var motion = new WanDynamicMotionController();
        var scheduler = new HeterogeneousTaskScheduler();
        var engine = new MultimodalAudioSyncEngine(motion, scheduler);

        var silence = new byte[2048];
        var features = engine.AnalyzeFrame(silence, 44100);

        Assert.Equal(0, features.Amplitude);
        Assert.False(features.BeatDetected);
    }

    [Fact]
    public void AnalyzeFrame_SuddenLoudFrameAfterQuiet_DetectsBeatAndInjectsMotion()
    {
        var motion = new WanDynamicMotionController();
        var scheduler = new HeterogeneousTaskScheduler();
        var engine = new MultimodalAudioSyncEngine(motion, scheduler);

        var quiet = GenerateSineWavePcm16(220, 44100, 1024, amplitude: 0.05);
        for (var i = 0; i < 5; i++) engine.AnalyzeFrame(quiet, 44100);

        var injectionsBefore = engine.MotionInjections;

        var loud = GenerateSineWavePcm16(220, 44100, 1024, amplitude: 0.9);
        var features = engine.AnalyzeFrame(loud, 44100);

        Assert.True(features.BeatDetected, $"Amplitude={features.Amplitude}");
        Assert.True(engine.MotionInjections > injectionsBefore, "비트 감지 시 모션이 실제로 주입돼야 함(죽은 계산 방지)");
        Assert.NotNull(motion.LastApplied);
    }

    [Fact]
    public async Task AnalyzeFrameOffloadedAsync_RoutesThroughScheduler_AndReturnsValidFeatures()
    {
        var motion = new WanDynamicMotionController();
        var scheduler = new HeterogeneousTaskScheduler();
        var engine = new MultimodalAudioSyncEngine(motion, scheduler);

        var pcm = GenerateSineWavePcm16(880, 48000, 1024, amplitude: 0.6);
        var before = scheduler.DualGpuMode ? scheduler.SecondaryTasks : scheduler.PrimaryTasks;

        var features = await engine.AnalyzeFrameOffloadedAsync(pcm, 48000);

        Assert.True(features.Amplitude > 0);
        var after = scheduler.DualGpuMode ? scheduler.SecondaryTasks : scheduler.PrimaryTasks;
        Assert.Equal(before + 1, after);
    }

    [Fact]
    public void ResetAll_ClearsCountersAndState()
    {
        var motion = new WanDynamicMotionController();
        var scheduler = new HeterogeneousTaskScheduler();
        var engine = new MultimodalAudioSyncEngine(motion, scheduler);

        var pcm = GenerateSineWavePcm16(440, 44100, 1024);
        engine.AnalyzeFrame(pcm, 44100);
        Assert.True(engine.FramesAnalyzed > 0);

        engine.ResetAll();
        Assert.Equal(0, engine.FramesAnalyzed);
        Assert.Equal(0, engine.BeatsDetected);
        Assert.Equal(0, engine.MotionInjections);
    }
}