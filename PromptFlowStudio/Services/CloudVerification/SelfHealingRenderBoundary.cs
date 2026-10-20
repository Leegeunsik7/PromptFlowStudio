using Microsoft.AspNetCore.Components.Web;

namespace PromptFlowStudio.Services.CloudVerification;

/// <summary>
/// [PromptFlow Studio v16.92.0] 자율 복원 렌더 경계 — 하위 렌더 예외를 가드에 보고하고, 연속 한도 미만이면 지연 후
/// <see cref="ErrorBoundaryBase.Recover"/>로 이전 정상 렌더 트리로 복귀(한도 초과 = 비정상 루프 → ErrorContent 유지).
/// @inject 미사용(모바일 서브컴포넌트 규칙) — 가드는 파라미터로 전달.
/// </summary>
public sealed class SelfHealingRenderBoundary : ErrorBoundary
{
    [Microsoft.AspNetCore.Components.Parameter] public RealtimeUiExceptionSelfHealingGuard? Guard { get; set; }
    [Microsoft.AspNetCore.Components.Parameter] public string Component { get; set; } = "UI";
    [Microsoft.AspNetCore.Components.Parameter] public int RecoverDelayMs { get; set; } = 600;

    /// <summary>정상 렌더 완료(예외 없음) → 렌더 연속 실패 카운터 초기화.</summary>
    protected override void OnAfterRender(bool firstRender)
    {
        if (CurrentException is null) Guard?.ReportRenderHealthy();
    }

    protected override async Task OnErrorAsync(Exception exception)
    {
        try
        {
            var heal = Guard?.ReportRender(exception, Component) ?? false;
            if (!heal) return;
            await Task.Delay(RecoverDelayMs).ConfigureAwait(false);
            await InvokeAsync(Recover).ConfigureAwait(false);
        }
        catch (Exception ex) { PromptFlowStudio.Program.Log("CloudVerify.RenderBoundary", ex); }
    }
}
