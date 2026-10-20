using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Shared.Haptics;

/// <summary>햅틱 이벤트(차별화된 하모닉 진동 대상). 원래 PLC 모듈의 FieldModels 에 함께 있던 정의를 PLC 제거 시 햅틱 전용으로 분리.</summary>
public enum HapticEvent { SwLimit, HwLimit, Alarm, SimTouch, DeploySuccess }

/// <summary>햅틱 패턴 — 이벤트 + 진동 타이밍(ms)/진폭(1~255). Android VibrationEffect.createWaveform 호환.</summary>
public sealed record HapticPattern(HapticEvent Event, long[] TimingsMs, int[] Amplitudes);

/// <summary>
/// [PromptFlow Studio v4.1] 햅틱 감각 피드백 시임 — 리미트/알람/2.5D 시뮬 조작 시 차별화된 하모닉 진동.
///   데스크톱은 <see cref="DesktopHapticEngine"/>(no-op), 안드로이드는 AndroidHapticEngine(VibrationEffect.createWaveform).
/// </summary>
public interface IHapticEngine
{
    /// <summary>이 호스트에서 진동 지원 여부(데스크톱 false).</summary>
    bool IsSupported { get; }
    /// <summary>이벤트 패턴 재생(동기 트리거).</summary>
    void Play(HapticEvent evt);
    /// <summary>이벤트 패턴 재생(비동기).</summary>
    Task PlayAsync(HapticEvent evt, CancellationToken ct = default);
}

/// <summary>이벤트별 하모닉 진동 패턴(순수 데이터 · thread-safe). Android VibrationEffect.createWaveform(timings, amplitudes, -1) 에 그대로 투입.</summary>
public static class HapticPatterns
{
    /// <summary>이벤트 → 패턴(timings/amplitudes 동일 길이 · amplitude 0~255).</summary>
    public static HapticPattern For(HapticEvent evt) => evt switch
    {
        // SW 리미트: 부드러운 단일 경고
        HapticEvent.SwLimit => new(evt, new long[] { 0, 70 }, new[] { 0, 110 }),
        // HW 리미트: 강한 이중 타격(가장 강함)
        HapticEvent.HwLimit => new(evt, new long[] { 0, 130, 70, 130 }, new[] { 0, 255, 0, 255 }),
        // 알람: 긴급 삼중
        HapticEvent.Alarm => new(evt, new long[] { 0, 90, 60, 90, 60, 90 }, new[] { 0, 220, 0, 220, 0, 220 }),
        // 2.5D 시뮬 터치: 가벼운 틱
        HapticEvent.SimTouch => new(evt, new long[] { 0, 22 }, new[] { 0, 70 }),
        // 배포 성공: 상승 하모닉
        _ => new(evt, new long[] { 0, 45, 45, 90 }, new[] { 0, 130, 0, 205 }),
    };

    /// <summary>패턴 유효성(timings/amplitudes 동일 길이 · amplitude 0~255).</summary>
    public static bool IsValid(HapticPattern p)
    {
        if (p.TimingsMs.Length != p.Amplitudes.Length || p.TimingsMs.Length == 0) return false;
        foreach (var a in p.Amplitudes) if (a < 0 || a > 255) return false;
        return true;
    }

    /// <summary>패턴 최대 진폭(세기 비교용).</summary>
    public static int PeakAmplitude(HapticPattern p)
    {
        var max = 0;
        foreach (var a in p.Amplitudes) if (a > max) max = a;
        return max;
    }
}

/// <summary>데스크톱 폴백 — 진동 HW 없음. no-op(Graceful).</summary>
public sealed class DesktopHapticEngine : IHapticEngine
{
    public bool IsSupported => false;
    public void Play(HapticEvent evt) { /* no-op */ }
    public Task PlayAsync(HapticEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}
