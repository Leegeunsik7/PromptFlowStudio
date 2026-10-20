namespace PromptFlowStudio.Mobile;

/// <summary>
/// [PromptFlow Studio v5.1] 플랫폼 WebView 로케이터 — AR 카메라 패스스루가 BlazorWebView 를 투명화하려면
/// 현재 플랫폼 WebView 핸들이 필요하다. <c>MainPage</c> 가 <c>BlazorWebView.BlazorWebViewInitialized</c> 에서 여기에 주입하고,
/// <c>AndroidArFieldVision</c>/<c>IosArFieldVision</c> 가 팩토리 람다로 읽어간다(null 이면 카메라만 켜고 투명화는 생략 · Graceful).
/// </summary>
public static class MauiWebViewLocator
{
#if ANDROID
    /// <summary>현재 Android BlazorWebView 의 네이티브 WebView(초기화 시 주입).</summary>
    public static Android.Webkit.WebView? AndroidWebView { get; set; }
#endif
#if IOS
    /// <summary>현재 iOS BlazorWebView 의 네이티브 WKWebView(초기화 시 주입).</summary>
    public static WebKit.WKWebView? IosWebView { get; set; }
#endif
}
