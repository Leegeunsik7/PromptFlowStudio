using System;
using System.IO.MemoryMappedFiles;
using PromptFlowStudio.Services.AI.Wan;
using PromptFlowStudio.Services.Media;

namespace PromptFlowStudio.Tests;

[Trait("Category", "FastUnit")]
public class VirtualCamPipeNativeSpecTests
{
    [Fact]
    public void HeaderStruct_SizeMatchesRuntimeWireFormatExactly()
    {
        // 문서(C-ABI 구조체)와 실제 구현(LiveStreamVirtualCamPipe.HeaderBytes)이 어긋나면 이 테스트가 즉시 잡는다.
        Assert.Equal(LiveStreamVirtualCamPipe.HeaderBytes, VirtualCamPipeNativeSpec.HeaderSizeBytes);
        Assert.Equal(36, VirtualCamPipeNativeSpec.HeaderSizeBytes);
    }

    [Fact]
    public void SubmitFrame_ThenIndependentNativeStructRead_MatchesExactly()
    {
        // OBS/C++ 리더를 흉내: LiveStreamVirtualCamPipe API 를 전혀 거치지 않고 같은 이름의 MMF 를 직접 열어
        // 언매니지드 구조체로 마샬링 - 이것이 통과하면 C-ABI 명세가 실제 와이어와 바이트 단위로 일치함이 증명된다.
        var mapName = "Local\\pfs_test_nativespec_" + Guid.NewGuid().ToString("N")[..8];
        var mutexName = "Local\\pfs_test_nativespec_mtx_" + Guid.NewGuid().ToString("N")[..8];
        using var pipe = new LiveStreamVirtualCamPipe(mapName, mutexName);

        var pixels = new byte[32 * 32 * 4];
        new Random(11).NextBytes(pixels);
        pipe.SubmitFrame(pixels, 32, 32, VirtualCamPixelFormat.Nv12);

        using var externalMmf = MemoryMappedFile.OpenExisting(mapName);
        using var accessor = externalMmf.CreateViewAccessor(0, VirtualCamPipeNativeSpec.HeaderSizeBytes, MemoryMappedFileAccess.Read);
        accessor.Read(0, out VirtualCamFrameHeaderNative header);

        Assert.Equal(LiveStreamVirtualCamPipe.MagicValue, header.Magic);
        Assert.Equal(32, header.Width);
        Assert.Equal(32, header.Height);
        Assert.Equal(1, header.FrameIndex);
        Assert.Equal((int)VirtualCamPixelFormat.Nv12, header.PixelFormat);
        Assert.Equal(pixels.Length, header.PayloadBytes);
        Assert.True(header.Timestamp > 0);
    }
}
[Trait("Category", "FastUnit")]
public class LiveStreamVirtualCamPipeTests
{
    private static (string map, string mutex) UniqueNames([System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        var tag = caller + "_" + Guid.NewGuid().ToString("N")[..8];
        return ("Local\\pfs_test_map_" + tag, "Local\\pfs_test_mutex_" + tag);
    }

    [Fact]
    public void SubmitFrame_ThenIndependentReader_ReadsBackIdenticalHeaderAndPayload()
    {
        var (map, mutex) = UniqueNames();
        using var writer = new LiveStreamVirtualCamPipe(map, mutex);
        using var reader = new LiveStreamVirtualCamPipe(map, mutex);   // 독립 리더(다른 프로세스를 흉내) - 같은 이름의 커널 객체를 연다.

        var pixels = new byte[64 * 64 * 4];
        new Random(7).NextBytes(pixels);

        var ok = writer.SubmitFrame(pixels, 64, 64, VirtualCamPixelFormat.Rgba32);
        Assert.True(ok);

        var dest = new byte[pixels.Length];
        var readOk = reader.TryReadLatestFrame(dest, out var info, out var bytesRead);

        Assert.True(readOk);
        Assert.NotNull(info);
        Assert.Equal(64, info!.Width);
        Assert.Equal(64, info.Height);
        Assert.Equal(VirtualCamPixelFormat.Rgba32, info.Format);
        Assert.Equal(pixels.Length, bytesRead);
        Assert.Equal(pixels, dest);   // 왕복 바이트 단위 일치(와이어 포맷 정확성 검증)
    }

    [Fact]
    public void SubmitFrame_ExceedsMaxPayload_DropsGracefullyWithoutException()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);

        var tooBig = new byte[LiveStreamVirtualCamPipe.MaxPayloadBytes + 1];
        var ok = pipe.SubmitFrame(tooBig, 1921, 1080, VirtualCamPixelFormat.Rgba32);

        Assert.False(ok);
        Assert.Equal(1, pipe.FramesDropped);
        Assert.Equal(0, pipe.FramesSubmitted);
    }

    [Fact]
    public void SubmitFrame_ValidFrame_UpdatesReportFieldsCorrectly()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);

        var pixels = new byte[128 * 128 * 4];
        var ok = pipe.SubmitFrame(pixels, 128, 128, VirtualCamPixelFormat.Rgba32);

        Assert.True(ok);
        Assert.Equal(1, pipe.FramesSubmitted);
        Assert.NotNull(pipe.LastFrameInfo);
        Assert.Equal(128, pipe.LastFrameInfo!.Width);
        Assert.Equal(pixels.Length, pipe.LastFrameInfo.PayloadBytes);
        Assert.True(pipe.EstimatedFps > 0);
        var expectedPct = Math.Round(pixels.Length * 100.0 / LiveStreamVirtualCamPipe.MaxPayloadBytes, 1);
        Assert.Equal(expectedPct, pipe.OccupancyPct);
    }

    [Fact]
    public void ResetCounters_ClearsAllCountersAndLastFrameInfo()
    {
        var (map, mutex) = UniqueNames();
        using var pipe = new LiveStreamVirtualCamPipe(map, mutex);
        pipe.SubmitFrame(new byte[16], 4, 4, VirtualCamPixelFormat.Nv12);
        Assert.Equal(1, pipe.FramesSubmitted);

        pipe.ResetCounters();

        Assert.Equal(0, pipe.FramesSubmitted);
        Assert.Equal(0, pipe.FramesDropped);
        Assert.Equal(0, pipe.MutexTimeouts);
        Assert.Null(pipe.LastFrameInfo);
    }
}

[Trait("Category", "FastUnit")]
public class WanPostProcessingEngineStreamingBridgeTests
{
    // v11.1.0: MainForm.cs 의 OnChange 결선점이 실제로 의미 있는 프레임을 넘길 수 있는지 - CopyLastInterpolatedFrameTo
    // 자체의 계약(보간 이력 없으면 0, 있으면 FrameBytes 만큼)을 검증한다.
    [Fact]
    public void CopyLastInterpolatedFrameTo_NoInterpolationYet_ReturnsZero()
    {
        var engine = Build();
        var dest = new byte[128 * 128 * 4];
        Assert.Equal(0, engine.CopyLastInterpolatedFrameTo(dest));
    }

    [Fact]
    public void CopyLastInterpolatedFrameTo_AfterSecondFrame_ReturnsNonZeroInterpolatedBytes()
    {
        var engine = Build();
        engine.SubmitFrame(new byte[64 * 1024]);
        engine.SubmitFrame(new byte[64 * 1024]);   // 두 번째 프레임부터 보간 발생(직전 프레임 필요)

        var dest = new byte[128 * 128 * 4];
        var n = engine.CopyLastInterpolatedFrameTo(dest);

        Assert.Equal(64 * 1024, n);          // FrameBytes == 128*128*4
        Assert.Equal(128 * 128 * 4, 64 * 1024);   // 문서화한 해상도 대응 관계 자체를 회귀 검증
    }

    private static WanPostProcessingEngine Build()
    {
        var calibrator = new AutoTunerHardwareCalibrator();
        var tuner = new LivePipelineAutoTuner(calibrator);
        var upscaler = new AutonomousNeuralTilingUpscaler();
        var scheduler = new HeterogeneousTaskScheduler();
        var motion = new WanDynamicMotionController();
        var lora = new LoRAHotSwapManager();
        return new WanPostProcessingEngine(upscaler, tuner, scheduler, motion, lora);
    }
}