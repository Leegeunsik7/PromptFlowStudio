using System;
using System.Threading;

#if ANDROID
using AndroidX.Camera.Core;
#endif

namespace PromptFlowStudio.Services.Mobile;

/// <summary>
/// [PromptFlowStudio] v5.2.1 Task1 · CameraX 프레임 피드 파이프라인 — 순수 BCL 변환/스킵(외부 NuGet 0).
///   ▸ CPU 스파이크 방지: <see cref="FrameSkipGate"/> 로 최소 간격 + 처리 중 프레임 드롭(백프레셔).
///   ▸ YUV_420_888 Y 평면을 <b>패딩 제거 W×H 루마 버퍼</b>로 경량 추출(할당 1회) → <see cref="VisionFrame"/>(Yuv420).
///   ▸ Android <c>ImageAnalysis.IAnalyzer</c>(#if ANDROID)가 이 헬퍼로 프레임을 만들어 <see cref="IMobileVisionAiEngine.AnalyzeFrameAsync"/>에 연결.
/// </summary>
public sealed class FrameSkipGate
{
    private readonly object _lock = new();
    private readonly long _minIntervalMs;
    private long _lastMs = long.MinValue;
    private volatile bool _inFlight;

    /// <summary>최소 처리 간격(ms) — 예: 100ms ≈ 10fps 분석(카메라 30fps 중 스킵).</summary>
    public FrameSkipGate(long minIntervalMs = 100) => _minIntervalMs = Math.Max(0, minIntervalMs);

    /// <summary>지금 프레임을 처리할지 — 최소 간격 미달 또는 처리 중이면 false(드롭). true 면 in-flight 마킹.</summary>
    public bool TryEnter(long nowMs)
    {
        lock (_lock)
        {
            if (_inFlight) return false;                       // 이전 프레임 분석 중 → 드롭(백프레셔 방지)
            if (nowMs - _lastMs < _minIntervalMs) return false; // 간격 미달 → 스킵
            _lastMs = nowMs;
            _inFlight = true;
            return true;
        }
    }

    /// <summary>처리 완료 표시(다음 프레임 허용).</summary>
    public void Leave() { lock (_lock) _inFlight = false; }
}

/// <summary>YUV_420_888 ↔ 루마 버퍼 경량 변환(순수 · 테스트 대상 · 최소 할당).</summary>
public static class YuvConvert
{
    /// <summary>
    /// Y 평면(rowStride/pixelStride 패딩 포함)에서 <b>패딩을 제거한 W×H 루마 버퍼</b>를 추출한다(할당 1회).
    ///   pixelStride==1 이고 rowStride==width 면 한 줄 통째 복사(빠른 경로).
    /// </summary>
    public static byte[] PackLumaPlane(byte[] yPlane, int width, int height, int rowStride, int pixelStride)
    {
        if (yPlane is null || width <= 0 || height <= 0) return Array.Empty<byte>();
        var rs = rowStride <= 0 ? width : rowStride;
        var ps = pixelStride <= 0 ? 1 : pixelStride;
        var outp = new byte[width * height];
        var dst = 0;
        for (var row = 0; row < height; row++)
        {
            var baseIdx = row * rs;
            if (ps == 1)
            {
                // 빠른 경로: 행 앞 width 바이트만 복사(패딩 rs-width 는 건너뜀).
                var copy = Math.Min(width, Math.Max(0, yPlane.Length - baseIdx));
                if (copy > 0) Buffer.BlockCopy(yPlane, baseIdx, outp, dst, copy);
                dst += width;
            }
            else
            {
                for (var col = 0; col < width; col++)
                {
                    var src = baseIdx + col * ps;
                    outp[dst++] = src < yPlane.Length ? yPlane[src] : (byte)0;
                }
            }
        }
        return outp;
    }

    /// <summary>추출한 루마 버퍼로 <see cref="VisionFrame"/>(Yuv420) 생성 — 엔진은 Y 평면을 루마로 직접 사용.</summary>
    public static VisionFrame ToLumaFrame(byte[] packedLuma, int width, int height, long timestampMs)
        => new(packedLuma, width, height, VisionPixelFormat.Yuv420, timestampMs);
}

#if ANDROID
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI Android 헤드 전용] — CameraX ImageAnalysis 로 프레임을 받아 비전 엔진에 연결.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>CameraX ImageAnalysis 분석기 — Y 평면 경량 추출 → 비전 엔진 결선(스킵/드롭 적용).</summary>
public sealed class ArVisionAnalyzer : Java.Lang.Object, ImageAnalysis.IAnalyzer
{
    private readonly IMobileVisionAiEngine _engine;
    private readonly FrameSkipGate _gate;

    public ArVisionAnalyzer(IMobileVisionAiEngine engine, FrameSkipGate? gate = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _gate = gate ?? new FrameSkipGate(100);
    }

    public void Analyze(IImageProxy image)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            if (image is null) return;
            if (!_gate.TryEnter(now)) { image.Close(); return; }   // 스킵/드롭 시 즉시 반환(반드시 Close)

            var w = image.Width;
            var h = image.Height;
            var plane = image.GetPlanes()?[0];
            if (plane is null) { image.Close(); _gate.Leave(); return; }

            var buf = plane.Buffer;
            var y = new byte[buf.Remaining()];
            buf.Get(y);                                    // Y 평면 바이트 복사(Close 전에 반드시 복사)
            var rowStride = plane.RowStride;
            var pixelStride = plane.PixelStride;
            image.Close();                                 // 프레임 즉시 반환(다음 프레임 수신 재개)

            var luma = YuvConvert.PackLumaPlane(y, w, h, rowStride, pixelStride);
            var frame = YuvConvert.ToLumaFrame(luma, w, h, now);

            // 엔진 분석은 비동기(분석기 스레드 비블로킹) — 완료 시 in-flight 해제.
            _ = _engine.AnalyzeFrameAsync(frame, CancellationToken.None)
                       .ContinueWith(_ => _gate.Leave(), TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            Program.Log("ArVisionAnalyzer.Analyze", ex);
            try { image?.Close(); } catch { }
            _gate.Leave();
        }
    }
}
#endif
