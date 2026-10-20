using System;
using System.Windows.Forms;

namespace PromptFlowStudio.Services.Platform;

// v5.3: IFilePickerService 계약은 PromptFlowStudio.Shared/Platform/PlatformInterfaces.cs 로 이관(모바일 디커플링). 데스크톱 구현만 유지.

/// <summary>
/// [PromptFlowStudio] 파일 선택기 — WinForms <see cref="OpenFileDialog"/>(별도 NuGet 불필요, UseWindowsForms).
///   ⚠ 파일 대화상자는 STA UI 스레드에서만 안전 → 호스트 컨트롤(MainForm) 스레드로 <see cref="Control.Invoke(Delegate)"/> 마샬링
///     (VideoExportService/ClipboardService 와 동일 전략). 취소/실패는 null(Graceful).
/// </summary>
public sealed class FilePickerService(Control owner) : IFilePickerService
{
    /// <inheritdoc/>
    public string? PickFile(string title, string filter, string? initialDir = null)
    {
        string? Show()
        {
            using var dlg = new OpenFileDialog
            {
                Title = string.IsNullOrWhiteSpace(title) ? "파일 선택" : title,
                Filter = string.IsNullOrWhiteSpace(filter) ? "모든 파일 (*.*)|*.*" : filter,
                CheckFileExists = true,
                Multiselect = false,
            };
            if (!string.IsNullOrWhiteSpace(initialDir) && System.IO.Directory.Exists(initialDir)) dlg.InitialDirectory = initialDir!;
            return dlg.ShowDialog(owner) == DialogResult.OK ? dlg.FileName : null;
        }

        try { return owner.InvokeRequired ? (string?)owner.Invoke(Show) : Show(); }
        catch (Exception ex) { Program.Log("FilePicker.PickFile", ex); return null; }
    }
}
