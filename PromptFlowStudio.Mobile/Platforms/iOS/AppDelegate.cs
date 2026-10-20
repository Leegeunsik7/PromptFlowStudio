using Foundation;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace PromptFlowStudio.Mobile;

/// <summary>v16.5.1 iOS 앱 델리게이트 — Android <c>MainApplication</c> 과 동일하게 <see cref="MauiProgram.CreateMauiApp"/> 위임.</summary>
[Register("AppDelegate")]
public sealed class AppDelegate : MauiUIApplicationDelegate
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
