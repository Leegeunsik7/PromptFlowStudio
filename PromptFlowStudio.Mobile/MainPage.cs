using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Maui.Controls;

namespace PromptFlowStudio.Mobile;

/// <summary>
/// BlazorWebView 호스트 페이지 — 기존 .razor UI 를 안드로이드/iOS WebView 에서 그대로 렌더(코드 온리).
///   Selector <c>#app</c> 에 <see cref="Main"/> 루트 컴포넌트를 마운트하고,
///   v5.1.1 에서 네이티브 WebView 초기화 시 <b>배경을 투명화</b>해 AR 카메라 패스스루가 뒤에서 비치도록 결선한다.
/// </summary>
public sealed class MainPage : ContentPage
{
    public MainPage()
    {
        var blazor = new BlazorWebView { HostPage = "wwwroot/index.html" };
        blazor.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Main) });
        blazor.BlazorWebViewInitialized += OnBlazorWebViewInitialized;   // v5.1.1 AR 투명 WebView 결선
        Content = blazor;
    }

    /// <summary>
    /// 네이티브 WebView 준비 시 배경을 투명(Transparent/Clear)으로 만들고 <see cref="MauiWebViewLocator"/> 에 핸들을 주입한다.
    ///   → <c>AndroidArFieldVision</c>/<c>IosArFieldVision</c> 가 카메라 프리뷰를 WebView 뒤에 깔고 Light-AR 로 융합.
    ///   ⚠ 데스크톱(net8.0-windows)은 이 파일(모바일 프로젝트)을 컴파일하지 않으므로 무영향.
    /// </summary>
    private static void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
#if ANDROID
        // e.WebView 는 Android.Webkit.WebView 타입.
        e.WebView.SetBackgroundColor(Android.Graphics.Color.Transparent);
        MauiWebViewLocator.AndroidWebView = e.WebView;
#elif IOS
        // e.WebView 는 WebKit.WKWebView 타입.
        e.WebView.Opaque = false;
        e.WebView.BackgroundColor = UIKit.UIColor.Clear;
        e.WebView.ScrollView.BackgroundColor = UIKit.UIColor.Clear;
        MauiWebViewLocator.IosWebView = e.WebView;
#endif
    }
}
