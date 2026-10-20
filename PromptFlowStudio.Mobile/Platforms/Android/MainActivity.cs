using Android.App;
using Android.Content.PM;
using Android.OS;

namespace PromptFlowStudio.Mobile;

/// <summary>안드로이드 진입 액티비티 — 회전/화면비/DeX(멀티윈도우) 변경에 재생성 없이 대응.</summary>
[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    ResizeableActivity = true)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState) => base.OnCreate(savedInstanceState);
}
