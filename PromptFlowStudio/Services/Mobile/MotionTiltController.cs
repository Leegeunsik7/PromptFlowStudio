using System;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID || IOS
using Microsoft.Maui.Devices.Sensors;
using Microsoft.Maui.ApplicationModel;
#endif

namespace PromptFlowStudio.Services.Mobile;

/// <summary>기울기 샘플 — Euler(pitch/roll/yaw · 도) + Quaternion(x,y,z,w) + 타임스탬프(ms).</summary>
public sealed record TiltSample(double Pitch, double Roll, double Yaw, double Qx, double Qy, double Qz, double Qw, long TimestampMs)
{
    public static readonly TiltSample Zero = new(0, 0, 0, 0, 0, 0, 1, 0);
}

/// <summary>
/// [PromptFlowStudio] v5.0 Task3 · 자이로(IMU) 기반 모션 제어 시임 — MAUI <c>Microsoft.Maui.Devices.Sensors</c>(Gyroscope/Accelerometer).
///   ▸ 60Hz 폴링으로 Pitch/Roll(+Yaw) 를 산출해 <see cref="OnTilt"/>로 방송 → Blazor RCL 이 3D CAD 뷰어/PLC 서보 조그에 실시간 연동.
///   ▸ ⚠ 배터리 누수·메모리 릭 방지: 백그라운드 진입/모달 종료 시 <see cref="StopAsync"/>+<see cref="IDisposable.Dispose"/>로 센서 구독을 <b>멱등</b> 해제(v4.4.4 하드닝 계승).
///   ▸ 데스크톱은 <see cref="DesktopMotionTiltController"/>(<c>IsSupported=false</c>). 모바일 MAUI 헤드가 네이티브 대체.
/// </summary>
public interface IMotionTiltController : IDisposable
{
    /// <summary>자이로/가속도 센서 지원 여부(모바일).</summary>
    bool IsSupported { get; }
    /// <summary>현재 폴링 중인지.</summary>
    bool Running { get; }
    /// <summary>최신 기울기 샘플.</summary>
    TiltSample Latest { get; }
    /// <summary>기울기 갱신 방송(폴링 주기마다).</summary>
    event Action<TiltSample>? OnTilt;
    /// <summary>지정 Hz(기본 60)로 센서 폴링 시작(멱등).</summary>
    Task StartAsync(int hz = 60, CancellationToken ct = default);
    /// <summary>센서 폴링 정지 + 구독 해제(멱등 · 배터리/릭 방어의 핵심).</summary>
    Task StopAsync();
}

/// <summary>
/// [PromptFlowStudio] IMU 수학 유틸(순수 · 테스트 대상) — 중력벡터→기울기, Euler→Quaternion, 저역통과(지터 완화).
/// </summary>
public static class TiltMath
{
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>가속도(중력) 벡터 → (pitch, roll) 도. 0 벡터/NaN 방어(하한).</summary>
    public static (double Pitch, double Roll) FromGravity(double ax, double ay, double az)
    {
        // 분모 0 방어 — 정지 시 벡터가 0에 가까워도 NaN/DivideByZero 없이 0 반환.
        var denom = Math.Sqrt(ay * ay + az * az);
        var pitch = denom < 1e-6 ? 0.0 : Math.Atan2(-ax, denom) * Rad2Deg;
        var roll = Math.Abs(az) < 1e-6 && Math.Abs(ay) < 1e-6 ? 0.0 : Math.Atan2(ay, az) * Rad2Deg;
        return (Safe(pitch), Safe(roll));
    }

    /// <summary>Euler(도) → Quaternion(x,y,z,w).</summary>
    public static (double X, double Y, double Z, double W) EulerToQuaternion(double pitchDeg, double rollDeg, double yawDeg)
    {
        double cy = Math.Cos(yawDeg / Rad2Deg * 0.5), sy = Math.Sin(yawDeg / Rad2Deg * 0.5);
        double cp = Math.Cos(pitchDeg / Rad2Deg * 0.5), sp = Math.Sin(pitchDeg / Rad2Deg * 0.5);
        double cr = Math.Cos(rollDeg / Rad2Deg * 0.5), sr = Math.Sin(rollDeg / Rad2Deg * 0.5);
        return (sr * cp * cy - cr * sp * sy,
                cr * sp * cy + sr * cp * sy,
                cr * cp * sy - sr * sp * cy,
                cr * cp * cy + sr * sp * sy);
    }

    /// <summary>저역통과 필터 — 지터 완화(alpha 0~1, 클수록 반응 빠름).</summary>
    public static double LowPass(double previous, double current, double alpha)
    {
        var a = Math.Clamp(alpha, 0.0, 1.0);
        var p = Safe(previous); var c = Safe(current);
        return p + a * (c - p);
    }

    private static double Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;
}

/// <summary>데스크톱 폴백 — IMU 미지원(<c>IsSupported=false</c>). 폴링 no-op(배터리 이슈 없음).</summary>
public sealed class DesktopMotionTiltController : IMotionTiltController
{
    private bool _disposed;
    public bool IsSupported => false;
    public bool Running { get; private set; }
    public TiltSample Latest { get; private set; } = TiltSample.Zero;
    /// <summary>데스크톱은 틸트를 발생시키지 않는다(no-op 이벤트 · 구독은 허용하되 절대 호출 안 됨).</summary>
    public event Action<TiltSample>? OnTilt { add { } remove { } }

    public Task StartAsync(int hz = 60, CancellationToken ct = default) { Running = false; return Task.CompletedTask; }
    public Task StopAsync() { Running = false; return Task.CompletedTask; }

    public void Dispose()
    {
        if (_disposed) return;   // 멱등
        _disposed = true;
    }
}

#if ANDROID || IOS
// ════════════════════════════════════════════════════════════════════════════
//  [MAUI 모바일 헤드 전용] — Microsoft.Maui.Devices.Sensors Gyroscope+Accelerometer 60Hz.
//  ⚠ 배터리/릭 방어: Stop()·Dispose() 에서 반드시 이벤트 해제 + 센서 Stop.
// ════════════════════════════════════════════════════════════════════════════

public sealed class MauiMotionTiltController : IMotionTiltController
{
    private bool _disposed;
    private bool _hooked;
    private double _yaw;                 // 자이로 z 적분(요)
    private long _lastTicks;
    private double _fPitch, _fRoll;      // 저역통과 상태

    public bool IsSupported => Gyroscope.Default.IsSupported || Accelerometer.Default.IsSupported;
    public bool Running { get; private set; }
    public TiltSample Latest { get; private set; } = TiltSample.Zero;
    public event Action<TiltSample>? OnTilt;

    public Task StartAsync(int hz = 60, CancellationToken ct = default)
    {
        if (Running) return Task.CompletedTask;   // 멱등 — 중복 구독 방지(릭 방어)
        var speed = SensorSpeed.Game;             // ≈ 50~60Hz
        try
        {
            if (Accelerometer.Default.IsSupported)
            {
                Accelerometer.Default.ReadingChanged += OnAccel;
                Accelerometer.Default.Start(speed);
            }
            if (Gyroscope.Default.IsSupported)
            {
                Gyroscope.Default.ReadingChanged += OnGyro;
                Gyroscope.Default.Start(speed);
            }
            _hooked = true; Running = true; _lastTicks = DateTime.UtcNow.Ticks;
        }
        catch { /* 센서 시작 실패 → Graceful(미동작) */ }
        return Task.CompletedTask;
    }

    private void OnAccel(object? sender, AccelerometerChangedEventArgs e)
    {
        var d = e.Reading.Acceleration;
        var (pitch, roll) = TiltMath.FromGravity(d.X, d.Y, d.Z);
        _fPitch = TiltMath.LowPass(_fPitch, pitch, 0.25);   // 지터 완화
        _fRoll = TiltMath.LowPass(_fRoll, roll, 0.25);
        Emit();
    }

    private void OnGyro(object? sender, GyroscopeChangedEventArgs e)
    {
        var now = DateTime.UtcNow.Ticks;
        var dt = Math.Clamp((now - _lastTicks) / (double)TimeSpan.TicksPerSecond, 0, 0.1);
        _lastTicks = now;
        _yaw += e.Reading.AngularVelocity.Z * (180.0 / Math.PI) * dt;   // 요 적분
        Emit();
    }

    private void Emit()
    {
        var (qx, qy, qz, qw) = TiltMath.EulerToQuaternion(_fPitch, _fRoll, _yaw);
        Latest = new TiltSample(_fPitch, _fRoll, _yaw, qx, qy, qz, qw, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        OnTilt?.Invoke(Latest);
    }

    public Task StopAsync()
    {
        if (!_hooked) { Running = false; return Task.CompletedTask; }
        try
        {
            Accelerometer.Default.ReadingChanged -= OnAccel;
            Gyroscope.Default.ReadingChanged -= OnGyro;
            if (Accelerometer.Default.IsMonitoring) Accelerometer.Default.Stop();
            if (Gyroscope.Default.IsMonitoring) Gyroscope.Default.Stop();
        }
        catch { /* 무시 */ }
        _hooked = false; Running = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;   // 멱등 — 두 번 호출돼도 안전
        _ = StopAsync();                            // 센서 구독 확실 해제(배터리/릭 방어)
        OnTilt = null;
    }
}
#endif
