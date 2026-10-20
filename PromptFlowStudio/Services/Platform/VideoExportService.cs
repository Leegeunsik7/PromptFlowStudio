using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PromptFlowStudio.Services.Platform;

// v5.3: IVideoExportService 계약은 PromptFlowStudio.Shared/Platform/PlatformInterfaces.cs 로 이관(모바일 디커플링). 데스크톱 구현만 유지.

/// <summary>
/// [PromptFlowStudio] 영상 내보내기 — WinForms <see cref="SaveFileDialog"/> 로 사용자 지정 위치에 저장.
///   ⚠ 파일 대화상자는 STA UI 스레드에서만 안전하므로, 호스트 컨트롤(<see cref="Control"/>=MainForm)의
///     스레드로 <see cref="Control.Invoke(Delegate)"/> 마샬링해 띄운다(BlazorWebView 이벤트가 다른 컨텍스트여도 안전).
///   BlazorWebView 호스트가 WinForms 라 별도 NuGet 불필요(UseWindowsForms=true).
/// </summary>
public sealed class VideoExportService(Control owner) : IVideoExportService
{
    /// <inheritdoc/>
    public Task<string?> ExportAsync(string sourcePath, string suggestedFileName)
    {
        string? Show()
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("내보낼 영상 파일을 찾을 수 없습니다.", sourcePath);

            using var dlg = new SaveFileDialog
            {
                Title = "비디오 내보내기",
                Filter = "MP4 비디오 (*.mp4)|*.mp4|모든 파일 (*.*)|*.*",
                DefaultExt = "mp4",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = SanitizeName(suggestedFileName),
                InitialDirectory = SafeDir(Environment.SpecialFolder.MyVideos),
            };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return null;   // 취소
            File.Copy(sourcePath, dlg.FileName, overwrite: true);
            return dlg.FileName;
        }

        // owner(폼) 스레드로 마샬링 — 이미 그 스레드면 InvokeRequired=false 라 직접 실행(데드락 없음).
        var result = owner.InvokeRequired ? owner.Invoke(Show) : Show();
        return Task.FromResult(result);
    }

    private static string SanitizeName(string? n)
    {
        if (string.IsNullOrWhiteSpace(n)) return "promptflow_export.mp4";
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim();
        if (string.IsNullOrWhiteSpace(Path.GetExtension(n))) n += ".mp4";
        return n;
    }

    private static string SafeDir(Environment.SpecialFolder f)
    { try { return Environment.GetFolderPath(f); } catch { return ""; } }
}
