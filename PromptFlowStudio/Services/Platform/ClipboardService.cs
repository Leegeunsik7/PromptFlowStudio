using System;
using System.Windows.Forms;

namespace PromptFlowStudio.Services.Platform;

// v5.3: IClipboardService 계약은 PromptFlowStudio.Shared/Platform/PlatformInterfaces.cs 로 이관(모바일 디커플링). 데스크톱 구현만 유지.

/// <summary>
/// [PromptFlowStudio] 클립보드 복사 — WinForms <see cref="Clipboard"/> 사용(별도 NuGet·JS interop 불필요, UseWindowsForms=true).
///   ⚠ 클립보드 API 는 STA UI 스레드에서만 안전하므로, 호스트 컨트롤(<see cref="Control"/>=MainForm) 스레드로
///     <see cref="Control.Invoke(Delegate)"/> 마샬링한다(BlazorWebView 이벤트가 다른 컨텍스트여도 안전).
///   복사는 <see cref="Clipboard.SetDataObject(object,bool,int,int)"/>(copy=true 로 앱 종료 후에도 유지 + 잠금 경합 시 재시도)로 견고화.
///   어떤 실패든 false 반환 — 호출부가 토스트로 안내(Graceful).
/// </summary>
public sealed class ClipboardService(Control owner) : IClipboardService
{
    /// <inheritdoc/>
    public bool SetText(string? text)
    {
        var s = text ?? "";
        if (s.Length == 0) return false;   // 빈 텍스트는 복사하지 않음
        try
        {
            if (owner.IsDisposed) return false;
            if (owner.InvokeRequired) owner.Invoke((Action)(() => Copy(s)));
            else Copy(s);
            return true;
        }
        catch (Exception ex) { Program.Log("Clipboard.SetText", ex); return false; }
    }

    // copy=true: 앱 종료 후에도 클립보드 유지. retryTimes/retryDelay: 다른 프로세스의 클립보드 잠금 경합 대비.
    private static void Copy(string s) => Clipboard.SetDataObject(s, copy: true, retryTimes: 5, retryDelay: 60);
}
