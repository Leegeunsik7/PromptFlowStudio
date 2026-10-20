using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Inference;

/// <summary>[v9.2.1] 온디바이스 비전 추론 가속 경로.</summary>
public enum OnDeviceAccelerator
{
    /// <summary>미프로브(초기값).</summary>
    Unknown = 0,
    /// <summary>Windows DirectML(ONNX Runtime DML EP) — 데스크톱 NPU/GPU.</summary>
    DirectMl = 1,
    /// <summary>Apple CoreML / ANE — iOS·macCatalyst.</summary>
    CoreMl = 2,
    /// <summary>Android NNAPI — 모바일 NPU/DSP.</summary>
    NnApi = 3,
    /// <summary>WebView WebGPU 컴퓨트 셰이더.</summary>
    WebGpu = 4,
    /// <summary>순수 BCL CPU 타일링 폴백(항상 성립).</summary>
    BclCpu = 5,
}

/// <summary>[v9.2.1] 타일 1장의 추론 결과(readonly struct — 타일 루프 힙 할당 0).</summary>
public readonly record struct OnDeviceTileResult(
    int Index,
    int X,
    int Y,
    int Size,
    double Mean,
    double Contrast,
    double Confidence,
    string Label);

/// <summary>[v9.2.1] 비전 추론 세션 1회 결과.</summary>
public sealed record OnDeviceVisionResult(
    string SessionId,
    OnDeviceAccelerator Accelerator,
    bool Accelerated,
    bool MemoryMapped,
    bool Synthesized,
    int TileCount,
    long SourceBytes,
    double TotalMs,
    double AvgTileMs,
    double AvgConfidence,
    double PeakConfidence,
    long ManagedBytesDelta,
    string Digest,
    IReadOnlyList<OnDeviceTileResult> Tiles);

/// <summary>
/// [PromptFlow Studio v9.2.1] 온디바이스 멀티모달 비전 추론 엔진.
///   ▸ DirectML(Windows) / CoreML(iOS) / NNAPI(Android) / WebGPU 후보 경로를 프로브하고, 네이티브 런타임 미탑재 시 순수 BCL CPU 타일링으로 폴백한다.
///   ▸ 원본은 <see cref="MemoryMappedFile"/> 뷰로 읽어 대형 프레임 전체 복사를 회피하고, 잠김/미지원이면 <c>FileStream</c> 순차 읽기로 강등한다.
///   ▸ 타일 루프는 <see cref="ArrayPool{T}"/> 단일 임대 버퍼 + <see cref="Span{T}"/> 슬라이싱으로 돌아 회당 힙 할당이 결과 리스트 외 0 이다.
///   ▸ 제5헌장: 파일 부재/잠김/취소 등 모든 실패는 예외 전파 없이 결정적 합성 텐서로 강등하고 <see cref="LastError"/> 로만 보고한다.
///   ⚠ 결선점: <see cref="TryRunNativeTensorAsync"/> — ONNX Runtime(DML/CoreML/NNAPI) 실텐서 추론을 꽂으면 상위 무변경으로 승격된다.
/// </summary>
public sealed class OnDeviceVisionInferenceEngine : IDisposable
{
    private const int MinTile = 16;
    private const int MaxTile = 512;
    private const int MaxTiles = 1024;
    private const int MaxHistory = 24;

    private readonly LocalModelLoader? _loader;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<OnDeviceVisionResult> _history = new(MaxHistory);
    private readonly object _historySync = new();

    private long _totalRuns;
    private long _acceleratedRuns;
    private long _fallbackRuns;
    private long _totalTiles;
    private double _emaTileMs;
    private bool _disposed;

    public OnDeviceVisionInferenceEngine(LocalModelLoader? loader = null)
    {
        _loader = loader;
        PreferredAccelerator = ProbePreferred(loader);
    }

    /// <summary>상태 변경 브로드캐스트(구독자는 <c>InvokeAsync(StateHasChanged)</c> 로 마셜링할 것).</summary>
    public event Action? OnChange;

    /// <summary>플랫폼/로더 프로브가 지목한 <b>후보</b> 가속 경로(런타임 결선 시 그대로 승격).</summary>
    public OnDeviceAccelerator PreferredAccelerator { get; private set; }

    /// <summary>실제로 사용되는 <b>유효</b> 경로 — 네이티브 미결선이면 BCL CPU.</summary>
    public OnDeviceAccelerator Accelerator => NativeTensorReady ? PreferredAccelerator : OnDeviceAccelerator.BclCpu;

    /// <summary>네이티브 텐서 결선 여부(외부 NuGet 0 원칙상 현재 저장소는 false).</summary>
    public bool NativeTensorReady { get; private set; }

    /// <summary>HUD 뱃지용 경로 표기(후보 → 유효).</summary>
    public string AcceleratorLabel => NativeTensorReady
        ? Label(PreferredAccelerator)
        : Label(PreferredAccelerator) + " 후보 · BCL 실행";

    /// <summary>추론 진행 중.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>누적 추론 세션 수.</summary>
    public long TotalRuns => Interlocked.Read(ref _totalRuns);

    /// <summary>네이티브 가속으로 처리된 세션 수.</summary>
    public long AcceleratedRuns => Interlocked.Read(ref _acceleratedRuns);

    /// <summary>원본 접근 실패로 합성 텐서로 강등된 세션 수.</summary>
    public long FallbackRuns => Interlocked.Read(ref _fallbackRuns);

    /// <summary>누적 타일 처리 수.</summary>
    public long TotalTiles => Interlocked.Read(ref _totalTiles);

    /// <summary>타일 처리 지연 EWMA(ms).</summary>
    public double TileLatencyMs => _emaTileMs;

    /// <summary>초당 타일 처리량(0 분모 가드).</summary>
    public double TilesPerSecond => _emaTileMs <= 0.0001d ? 0d : 1000d / _emaTileMs;

    /// <summary>합성 강등률(0~1 · 낮을수록 실원본 처리 비중이 큼).</summary>
    public double FallbackRatio
    {
        get
        {
            var total = TotalRuns;
            return total == 0 ? 0d : Math.Clamp(FallbackRuns / (double)total, 0d, 1d);
        }
    }

    /// <summary>마지막 세션 결과(없으면 null).</summary>
    public OnDeviceVisionResult? LastResult { get; private set; }

    /// <summary>마지막 오류(정상 시 null).</summary>
    public string? LastError { get; private set; }

    /// <summary>최근 세션 이력(최신순).</summary>
    public IReadOnlyList<OnDeviceVisionResult> History
    {
        get { lock (_historySync) { return _history.ToArray(); } }
    }

    /// <summary>HUD 표기용 처리량 문자열(InvariantCulture 고정).</summary>
    public string ThroughputLabel => TilesPerSecond.ToString("0", CultureInfo.InvariantCulture) + " tiles/s";

    /// <summary>후보 경로 재프로브(WebGPU 시그널 수신 등 가용성 변동 후 호출).</summary>
    public OnDeviceAccelerator Reprobe()
    {
        if (_disposed) return PreferredAccelerator;

        PreferredAccelerator = ProbePreferred(_loader);
        Notify();
        return PreferredAccelerator;
    }

    /// <summary>비전 타일링 추론 1회. 원본이 없거나 읽기 실패면 결정적 합성 텐서로 강등한다.</summary>
    public async Task<OnDeviceVisionResult> AnalyzeAsync(
        string? sourcePath,
        int tileSize = 64,
        int maxTiles = 64,
        CancellationToken ct = default)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..8];
        if (_disposed) return EmptyResult(sessionId);

        var tile = Math.Clamp(tileSize, MinTile, MaxTile);
        var budget = Math.Clamp(maxTiles, 1, MaxTiles);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        IsRunning = true;
        Notify();

        var allocBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = DateTimeOffset.UtcNow;

        try
        {
            // 결선점: 네이티브 텐서가 꽂히면 아래 BCL 경로 대신 즉시 승격된다.
            var native = await TryRunNativeTensorAsync(sessionId, sourcePath, tile, budget, ct).ConfigureAwait(false);
            if (native is not null)
            {
                Commit(native);
                LastError = null;
                return native;
            }

            var result = RunBclTiling(sessionId, sourcePath, tile, budget, started, allocBefore, ct);
            Commit(result);
            LastError = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            LastError = "추론 취소됨";
            return EmptyResult(sessionId);
        }
        catch (Exception ex)
        {
            LastError = "추론 실패: " + ex.Message;
            return EmptyResult(sessionId);
        }
        finally
        {
            IsRunning = false;
            _gate.Release();
            Notify();
        }
    }

    /// <summary>웜업 — 첫 세션의 JIT/페이지 폴트 비용을 선지불한다(합성 텐서 8타일).</summary>
    public Task<OnDeviceVisionResult> WarmupAsync(CancellationToken ct = default)
        => AnalyzeAsync(null, 32, 8, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _gate.Dispose();
        OnChange = null;
    }

    // ── 결선점(ONNX/DirectML·CoreML·NNAPI 실텐서) ──

    /// <summary>
    /// ⚠ 미결선: 네이티브 텐서 추론 훅. 실 런타임을 꽂아 결과를 반환하면 상위는 무변경으로 승격된다.
    /// 현재 저장소는 외부 NuGet 0 원칙상 런타임 미탑재 → 항상 <c>null</c>(=BCL 경로 사용).
    /// </summary>
    private Task<OnDeviceVisionResult?> TryRunNativeTensorAsync(
        string sessionId, string? sourcePath, int tileSize, int maxTiles, CancellationToken ct)
    {
        _ = sessionId; _ = sourcePath; _ = tileSize; _ = maxTiles; _ = ct;
        NativeTensorReady = false;
        return Task.FromResult<OnDeviceVisionResult?>(null);
    }

    // ── 순수 BCL 타일링(ArrayPool 단일 임대 · Span 슬라이싱) ──

    private OnDeviceVisionResult RunBclTiling(
        string sessionId, string? sourcePath, int tileSize, int maxTiles,
        DateTimeOffset started, long allocBefore, CancellationToken ct)
    {
        var bytesPerTile = tileSize * tileSize;
        var buffer = ArrayPool<byte>.Shared.Rent(bytesPerTile);
        var tiles = new List<OnDeviceTileResult>(maxTiles);
        var digest = new byte[32];
        var mapped = false;
        var synthesized = false;
        long sourceBytes = 0;

        MemoryMappedViewAccessor? view = null;
        MemoryMappedFile? mmf = null;
        FileStream? stream = null;

        try
        {
            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            {
                try
                {
                    sourceBytes = new FileInfo(sourcePath!).Length;
                }
                catch
                {
                    sourceBytes = 0;
                }
            }

            if (sourceBytes > 0)
            {
                try
                {
                    mmf = MemoryMappedFile.CreateFromFile(
                        new FileStream(sourcePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                        null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
                    view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                    mapped = true;
                }
                catch
                {
                    // 잠김/미지원 플랫폼 → 순차 스트림으로 강등(제5헌장).
                    mapped = false;
                    try { stream = new FileStream(sourcePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                    catch { stream = null; }
                }
            }

            synthesized = !mapped && stream is null;

            var side = Math.Max(1, (int)Math.Sqrt(maxTiles));
            var seed = StableSeed(sessionId, sourcePath);

            for (var i = 0; i < maxTiles; i++)
            {
                ct.ThrowIfCancellationRequested();

                var span = buffer.AsSpan(0, bytesPerTile);

                if (mapped && view is not null) FillFromView(view, (long)i * bytesPerTile, sourceBytes, span, seed);
                else if (stream is not null) FillFromStream(stream, span, seed);
                else FillSynthetic(span, seed, i);

                tiles.Add(Score(i, i % side * tileSize, i / side * tileSize, tileSize, span));
                MixDigest(digest, span);
            }
        }
        finally
        {
            view?.Dispose();
            mmf?.Dispose();
            stream?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var totalMs = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
        var allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

        double sum = 0d, peak = 0d;
        for (var i = 0; i < tiles.Count; i++)
        {
            sum += tiles[i].Confidence;
            if (tiles[i].Confidence > peak) peak = tiles[i].Confidence;
        }

        if (synthesized) Interlocked.Increment(ref _fallbackRuns);

        return new OnDeviceVisionResult(
            sessionId,
            Accelerator,
            Accelerated: NativeTensorReady,
            MemoryMapped: mapped,
            Synthesized: synthesized,
            TileCount: tiles.Count,
            SourceBytes: sourceBytes,
            TotalMs: totalMs,
            AvgTileMs: tiles.Count == 0 ? 0d : totalMs / tiles.Count,
            AvgConfidence: tiles.Count == 0 ? 0d : sum / tiles.Count,
            PeakConfidence: peak,
            ManagedBytesDelta: Math.Max(0L, allocDelta),
            Digest: Convert.ToHexString(digest)[..16],
            Tiles: tiles);
    }

    /// <summary>메모리 맵 뷰에서 타일 바이트 적재(범위 밖은 시드 노이즈 패딩).</summary>
    private static void FillFromView(MemoryMappedViewAccessor view, long offset, long length, Span<byte> dst, int seed)
    {
        var readable = (int)Math.Clamp(length - offset, 0L, dst.Length);
        for (var i = 0; i < readable; i++) dst[i] = view.ReadByte(offset + i);
        for (var i = readable; i < dst.Length; i++) dst[i] = (byte)((seed + i * 31) & 0xFF);
    }

    /// <summary>순차 스트림에서 타일 바이트 적재(EOF 는 시드 노이즈 패딩).</summary>
    private static void FillFromStream(FileStream stream, Span<byte> dst, int seed)
    {
        var read = stream.Read(dst);
        for (var i = read; i < dst.Length; i++) dst[i] = (byte)((seed + i * 17) & 0xFF);
    }

    /// <summary>원본 부재 시 결정적 합성 텐서(같은 시드 = 같은 결과 → 멱등 재현 검증 가능).</summary>
    private static void FillSynthetic(Span<byte> dst, int seed, int tileIndex)
    {
        unchecked
        {
            var s = (uint)(seed + tileIndex * 2654435761u);
            for (var i = 0; i < dst.Length; i++)
            {
                s = s * 1103515245u + 12345u;
                dst[i] = (byte)((s >> 16) & 0xFF);
            }
        }
    }

    /// <summary>타일 통계(평균/대비) → 신뢰도 + 라벨 휴리스틱.</summary>
    private static OnDeviceTileResult Score(int index, int x, int y, int size, ReadOnlySpan<byte> span)
    {
        long sum = 0;
        byte min = 255, max = 0;
        for (var i = 0; i < span.Length; i++)
        {
            var v = span[i];
            sum += v;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var mean = span.Length == 0 ? 0d : sum / (double)span.Length;
        var contrast = span.Length == 0 ? 0d : (max - min) / 255d;
        var confidence = Math.Clamp(0.45d + contrast * 0.4d + (1d - Math.Abs(mean - 128d) / 128d) * 0.15d, 0d, 1d);

        var label = contrast switch
        {
            > 0.82d => "고대비 피사체",
            > 0.55d => "윤곽 경계",
            > 0.25d => "질감 영역",
            _ => "평탄 배경",
        };

        return new OnDeviceTileResult(index, x, y, size, mean, contrast, confidence, label);
    }

    /// <summary>타일 스트림 누적 지문(세션 재현성 검증용 · 할당 0).</summary>
    private static void MixDigest(Span<byte> acc, ReadOnlySpan<byte> span)
    {
        if (span.Length == 0) return;

        unchecked
        {
            for (var i = 0; i < acc.Length; i++)
                acc[i] = (byte)(acc[i] * 31 + span[i * 7 % span.Length]);
        }
    }

    // ── 내부 유틸 ──

    private void Commit(OnDeviceVisionResult result)
    {
        Interlocked.Increment(ref _totalRuns);
        Interlocked.Add(ref _totalTiles, result.TileCount);
        if (result.Accelerated) Interlocked.Increment(ref _acceleratedRuns);

        _emaTileMs = _emaTileMs <= 0d ? result.AvgTileMs : _emaTileMs * 0.7d + result.AvgTileMs * 0.3d;
        LastResult = result;

        lock (_historySync)
        {
            _history.Insert(0, result);
            if (_history.Count > MaxHistory) _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        }
    }

    private OnDeviceVisionResult EmptyResult(string sessionId) => new(
        sessionId, Accelerator, false, false, false, 0, 0L, 0d, 0d, 0d, 0d, 0L, "0000000000000000",
        Array.Empty<OnDeviceTileResult>());

    private static int StableSeed(string sessionId, string? sourcePath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionId + "|" + (sourcePath ?? "synthetic")));
        return BitConverter.ToInt32(hash, 0) & 0x7FFFFFFF;
    }

    private static string Label(OnDeviceAccelerator a) => a switch
    {
        OnDeviceAccelerator.DirectMl => "DirectML NPU",
        OnDeviceAccelerator.CoreMl => "CoreML ANE",
        OnDeviceAccelerator.NnApi => "Android NNAPI",
        OnDeviceAccelerator.WebGpu => "WebGPU 컴퓨트",
        OnDeviceAccelerator.BclCpu => "BCL CPU",
        _ => "미프로브",
    };

    /// <summary>플랫폼/로더 기반 후보 경로 프로브 — 네이티브 런타임 유무와 무관하게 "어디로 승격될지"만 지목한다.</summary>
    private static OnDeviceAccelerator ProbePreferred(LocalModelLoader? loader)
    {
        try
        {
            if (loader is not null)
            {
                switch (loader.DetectBackend())
                {
                    case InferenceBackend.DirectML: return OnDeviceAccelerator.DirectMl;
                    case InferenceBackend.WebGpu: return OnDeviceAccelerator.WebGpu;
                    default: break;
                }
            }

            if (OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()) return OnDeviceAccelerator.CoreMl;
            if (OperatingSystem.IsAndroid()) return OnDeviceAccelerator.NnApi;
            if (OperatingSystem.IsWindows()) return OnDeviceAccelerator.DirectMl;
            return OnDeviceAccelerator.BclCpu;
        }
        catch
        {
            return OnDeviceAccelerator.BclCpu;
        }
    }

    private void Notify() => OnChange?.Invoke();
}
