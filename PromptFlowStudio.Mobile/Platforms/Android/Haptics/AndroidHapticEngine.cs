using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.OS;
using Microsoft.Maui.ApplicationModel;
using PromptFlowStudio.Shared.Haptics;

namespace PromptFlowStudio.Mobile.Services;

/// <summary>
/// [PromptFlow Studio v4.1] 안드로이드 하모닉 햅틱 — 공용 <see cref="HapticPatterns"/>를 VibrationEffect.CreateWaveform 으로 재생.
///   리미트/알람/2.5D 터치/배포 성공마다 차별화된 진동 파형(미지원 기기는 Graceful no-op).
/// </summary>
public sealed class AndroidHapticEngine : IHapticEngine
{
    public bool IsSupported => GetVibrator()?.HasVibrator ?? false;

    public void Play(HapticEvent evt)
    {
        var v = GetVibrator();
        if (v is null || !v.HasVibrator) return;
        var p = HapticPatterns.For(evt);
        if (!HapticPatterns.IsValid(p)) return;

        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            using var effect = VibrationEffect.CreateWaveform(p.TimingsMs, p.Amplitudes, -1);   // 진폭 지원 파형
            v.Vibrate(effect);
        }
        else
        {
#pragma warning disable CA1422 // 구형 API 폴백(진폭 미지원)
            v.Vibrate(p.TimingsMs, -1);
#pragma warning restore CA1422
        }
    }

    public Task PlayAsync(HapticEvent evt, CancellationToken ct = default) { Play(evt); return Task.CompletedTask; }

    private static Vibrator? GetVibrator()
    {
        var ctx = Platform.AppContext;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
        {
            var mgr = (VibratorManager?)ctx.GetSystemService(Context.VibratorManagerService);
            return mgr?.DefaultVibrator;
        }
#pragma warning disable CA1422
        return (Vibrator?)ctx.GetSystemService(Context.VibratorService);
#pragma warning restore CA1422
    }
}
