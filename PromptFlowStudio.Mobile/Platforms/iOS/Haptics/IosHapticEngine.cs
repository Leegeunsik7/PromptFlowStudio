using System.Threading;
using System.Threading.Tasks;
using UIKit;
using PromptFlowStudio.Shared.Haptics;

namespace PromptFlowStudio.Mobile.Services;

/// <summary>
/// [PromptFlow Studio v4.2] iOS 하모닉 햅틱 — UIFeedbackGenerator 로 이벤트별 차별화 진동.
///   공용 <see cref="HapticPatterns"/>의 세기 의미를 iOS 표준 피드백 타입에 매핑(정밀 파형은 CoreHaptics 로 확장 가능).
/// </summary>
public sealed class IosHapticEngine : IHapticEngine
{
    public bool IsSupported => true;

    public void Play(HapticEvent evt)
    {
        switch (evt)
        {
            case HapticEvent.HwLimit:
                using (var g = new UINotificationFeedbackGenerator()) g.NotificationOccurred(UINotificationFeedbackType.Error);
                break;
            case HapticEvent.Alarm:
                using (var g = new UINotificationFeedbackGenerator()) g.NotificationOccurred(UINotificationFeedbackType.Warning);
                break;
            case HapticEvent.DeploySuccess:
                using (var g = new UINotificationFeedbackGenerator()) g.NotificationOccurred(UINotificationFeedbackType.Success);
                break;
            case HapticEvent.SwLimit:
                using (var g = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Medium)) g.ImpactOccurred();
                break;
            default: // SimTouch
                using (var g = new UIImpactFeedbackGenerator(UIImpactFeedbackStyle.Light)) g.ImpactOccurred();
                break;
        }
    }

    public Task PlayAsync(HapticEvent evt, CancellationToken ct = default) { Play(evt); return Task.CompletedTask; }
}
