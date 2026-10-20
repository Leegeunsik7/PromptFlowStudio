using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Mobile;

/// <summary>카메라 프레임 픽셀 포맷 — RGBA8888 또는 YUV420(Y 평면 선두).</summary>
public enum VisionPixelFormat { Rgba8888, Yuv420 }

/// <summary>실시간 카메라 프레임 — 버퍼·해상도·포맷·타임스탬프(ms).</summary>
public sealed record VisionFrame(byte[] Buffer, int Width, int Height, VisionPixelFormat Format, long TimestampMs);

/// <summary>이상 감지 임계값 — 모션 차분·색상 변형·샘플 간격(px). 기본값으로 즉시 사용 가능.</summary>
public sealed record VisionThresholds(double MotionAlarm = 18.0, double ColorShiftAlarm = 26.0, int SampleStride = 8)
{
    public static readonly VisionThresholds Default = new();
}

/// <summary>이상 감지 결과 — 발생 여부·모션 점수·색상변형 점수·사유·타임스탬프.</summary>
public sealed record VisionAnomaly(bool Detected, double MotionScore, double ColorShiftScore, string Message, long TimestampMs);

/// <summary>
/// [PromptFlowStudio] v5.2 Task3 · 온디바이스 에지 비전 AI 프레임 분석 시임 — 순수 BCL(클라우드 0 · 외부 NuGet 0).
///   ▸ 카메라 파이프라인이 추출한 YUV/RGBA 프레임을 <see cref="SubmitFrameAsync"/>로 수신(비동기)해 <b>프레임 간 차분(Motion)</b> 과
///     <b>설비 색상 변형(Color Shift)</b> 을 로컬 연산 → 임계 초과 시 <see cref="OnAnomaly"/> 알람 발화.
///   ▸ 샘플 스트라이드로 다운샘플해 저전력 실시간 처리. 플랫폼 독립(데스크톱 빌드에서도 컴파일·테스트 가능).
/// </summary>
public interface IMobileVisionAiEngine : IDisposable
{
    /// <summary>분석 활성 여부.</summary>
    bool Running { get; }
    /// <summary>이상 감지 알람 방송.</summary>
    event Action<VisionAnomaly>? OnAnomaly;
    /// <summary>임계값 구성.</summary>
    void Configure(VisionThresholds thresholds);
    /// <summary>정상 설비 색 기준 설정(선택 · 없으면 첫 프레임 평균을 기준으로).</summary>
    void SetBaselineColor(double r, double g, double b);
    /// <summary>분석 시작/정지 토글.</summary>
    void SetRunning(bool running);
    /// <summary>실시간 프레임 수신(카메라 파이프라인이 호출) — 이전 프레임과 차분·색차 연산.</summary>
    Task SubmitFrameAsync(VisionFrame frame, CancellationToken ct = default);
    /// <summary><see cref="SubmitFrameAsync"/> 별칭 — CameraX ImageAnalysis 결선 가독성용(v5.2.1).</summary>
    Task AnalyzeFrameAsync(VisionFrame frame, CancellationToken ct = default);
    /// <summary>기준/직전 프레임 상태 초기화.</summary>
    void Reset();
}

/// <summary>비전 연산 유틸(순수 · thread-safe · 테스트 대상) — 루마 샘플·평균색·차분.</summary>
public static class VisionMath
{
    /// <summary>픽셀(x,y) 루마(0~255). RGBA=가중합, YUV=Y 평면 직접. 범위 밖/버퍼 부족은 0.</summary>
    public static double LumaAt(VisionFrame f, int x, int y)
    {
        if (f?.Buffer is null || f.Width <= 0 || f.Height <= 0) return 0;
        if (x < 0 || y < 0 || x >= f.Width || y >= f.Height) return 0;
        if (f.Format == VisionPixelFormat.Yuv420)
        {
            var idx = y * f.Width + x;
            return idx >= 0 && idx < f.Buffer.Length ? f.Buffer[idx] : 0;
        }
        var p = (y * f.Width + x) * 4;
        if (p < 0 || p + 2 >= f.Buffer.Length) return 0;
        return 0.299 * f.Buffer[p] + 0.587 * f.Buffer[p + 1] + 0.114 * f.Buffer[p + 2];
    }

    /// <summary>스트라이드 격자 루마 샘플 배열.</summary>
    public static double[] SampleLuma(VisionFrame f, int stride)
    {
        var s = Math.Max(1, stride);
        if (f?.Buffer is null || f.Width <= 0 || f.Height <= 0) return Array.Empty<double>();
        var cols = (f.Width + s - 1) / s;
        var rows = (f.Height + s - 1) / s;
        var outp = new double[cols * rows];
        var i = 0;
        for (var y = 0; y < f.Height; y += s)
            for (var x = 0; x < f.Width; x += s)
                outp[i++] = LumaAt(f, x, y);
        return outp;
    }

    /// <summary>두 샘플 배열의 평균 절대 차분(길이 다르면 0 · 정합 실패 방어).</summary>
    public static double MeanAbsDiff(double[] a, double[] b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length) return 0;
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    /// <summary>스트라이드 격자 평균 RGB(YUV 는 루마를 회색으로 근사).</summary>
    public static (double R, double G, double B) AverageRgb(VisionFrame f, int stride)
    {
        var s = Math.Max(1, stride);
        if (f?.Buffer is null || f.Width <= 0 || f.Height <= 0) return (0, 0, 0);
        double r = 0, g = 0, b = 0; long n = 0;
        for (var y = 0; y < f.Height; y += s)
            for (var x = 0; x < f.Width; x += s)
            {
                if (f.Format == VisionPixelFormat.Yuv420) { var l = LumaAt(f, x, y); r += l; g += l; b += l; }
                else
                {
                    var p = (y * f.Width + x) * 4;
                    if (p + 2 < f.Buffer.Length) { r += f.Buffer[p]; g += f.Buffer[p + 1]; b += f.Buffer[p + 2]; }
                }
                n++;
            }
        return n == 0 ? (0, 0, 0) : (r / n, g / n, b / n);
    }

    /// <summary>RGB 유클리드 거리(색상 변형 정도).</summary>
    public static double ColorDistance((double R, double G, double B) a, (double R, double G, double B) b)
        => Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));
}

/// <summary>순수 BCL 에지 비전 엔진(데스크톱/모바일 공용 · 카메라 프레임 소스는 플랫폼이 공급).</summary>
public sealed class MobileVisionAiEngine : IMobileVisionAiEngine
{
    private readonly object _lock = new();
    private VisionThresholds _thresholds = VisionThresholds.Default;
    private double[]? _prevLuma;
    private (double R, double G, double B)? _baseline;
    private volatile bool _running = true;
    private bool _disposed;

    public bool Running => _running;
    public event Action<VisionAnomaly>? OnAnomaly;

    public void Configure(VisionThresholds thresholds) => _thresholds = thresholds ?? VisionThresholds.Default;
    public void SetBaselineColor(double r, double g, double b) { lock (_lock) _baseline = (r, g, b); }
    public void SetRunning(bool running) => _running = running;
    public void Reset() { lock (_lock) { _prevLuma = null; _baseline = null; } }

    public async Task SubmitFrameAsync(VisionFrame frame, CancellationToken ct = default)
    {
        if (_disposed || !_running || frame?.Buffer is null || frame.Width <= 0 || frame.Height <= 0) return;

        VisionThresholds th = _thresholds;
        VisionAnomaly? alarm = null;
        try
        {
            await Task.Run(() =>
            {
                var luma = VisionMath.SampleLuma(frame, th.SampleStride);
                var avg = VisionMath.AverageRgb(frame, th.SampleStride);

                double motion;
                double colorShift;
                lock (_lock)
                {
                    motion = _prevLuma is null ? 0 : VisionMath.MeanAbsDiff(_prevLuma, luma);
                    _prevLuma = luma;
                    _baseline ??= avg;                                   // 첫 프레임을 기준색으로
                    colorShift = VisionMath.ColorDistance(avg, _baseline.Value);
                }

                var detected = motion > th.MotionAlarm || colorShift > th.ColorShiftAlarm;
                if (detected)
                {
                    var msg = motion > th.MotionAlarm && colorShift > th.ColorShiftAlarm ? "설비 이상: 급격한 모션 + 색상 변형"
                            : motion > th.MotionAlarm ? "설비 이상: 급격한 모션 감지"
                            : "설비 이상: 색상 변형 감지";
                    alarm = new VisionAnomaly(true, Math.Round(motion, 2), Math.Round(colorShift, 2), msg, frame.TimestampMs);
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Program.Log("VisionAi.SubmitFrame", ex); return; }

        if (alarm is not null && !_disposed) OnAnomaly?.Invoke(alarm);
    }

    public Task AnalyzeFrameAsync(VisionFrame frame, CancellationToken ct = default) => SubmitFrameAsync(frame, ct);   // 별칭

    public void Dispose()
    {
        if (_disposed) return;   // 멱등
        _disposed = true;
        _running = false;
        OnAnomaly = null;
        lock (_lock) { _prevLuma = null; _baseline = null; }
    }
}
