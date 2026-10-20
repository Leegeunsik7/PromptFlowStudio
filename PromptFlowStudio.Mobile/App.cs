using Microsoft.Maui.Controls;

namespace PromptFlowStudio.Mobile;

/// <summary>MAUI 애플리케이션 루트 — 단일 BlazorWebView 페이지 호스팅(코드 온리).</summary>
public sealed class App : Application
{
    public App() => MainPage = new MainPage();
}
