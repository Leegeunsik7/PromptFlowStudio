using UIKit;

namespace PromptFlowStudio.Mobile;

/// <summary>v16.5.1 iOS 진입점(CS5001 해소) — UIApplication 메인 루프를 <see cref="AppDelegate"/> 로 기동.</summary>
public static class IosProgram   // ⚠ PromptFlowStudio.Program(공용 로그 시임)과 이름 충돌 회피
{
    private static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
