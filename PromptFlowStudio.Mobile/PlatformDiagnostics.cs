namespace PromptFlowStudio;

/// <summary>
/// [PromptFlow Studio v5.1] 모바일 호스트용 <c>Program.Log</c> 심(shim).
///   데스크톱 프로젝트의 순수 BCL 서비스(소스 링크)는 진단 로그를 <c>Program.Log(tag, ex)</c> 로 남긴다.
///   데스크톱 <c>Program.cs</c>(WinForms 진입점)는 모바일에 링크되지 않으므로, 동일 시그니처를 모바일 어셈블리에 제공해
///   링크된 서비스들이 그대로 컴파일·동작하게 한다(로그는 Debug 출력으로 전환 · 앱을 죽이지 않음).
///   ⚠ MAUI 진입점은 <c>PromptFlowStudio.Mobile</c> 네임스페이스에서 별도 생성되므로 이 <c>PromptFlowStudio.Program</c> 과 충돌하지 않는다.
/// </summary>
internal static class Program
{
    internal static void Log(string tag, System.Exception? ex)
    {
        try { System.Diagnostics.Debug.WriteLine($"[PromptFlow] {tag}: {ex?.ToString() ?? "-"}"); }
        catch { /* 진단 실패는 삼킨다 — 앱을 죽이지 않는다 */ }
    }
}
