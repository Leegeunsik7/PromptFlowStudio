using System;
using System.IO;

namespace PromptFlowStudio.Services.Configuration;

/// <summary>
/// [PromptFlowStudio] 앱 시작 시 필수 폴더 보장 — <c>_renders</c> · <c>_uploads</c> · <c>_gallery</c> · <c>tools/ffmpeg/bin</c> · <c>presets</c>.
///   ▸ 이미 있으면 무동작(멱등) · 폴더별 예외 격리(읽기 전용 설치 경로 · 모바일 샌드박스에서도 기동 계속).
/// </summary>
public static class AppFolderBootstrapper
{
    public static readonly string[] RequiredFolders =
    [
        "_renders",
        "_uploads",
        "_gallery",
        Path.Combine("tools", "ffmpeg", "bin"),
        "presets",
    ];

    /// <summary><paramref name="root"/>(null 이면 <see cref="AppContext.BaseDirectory"/>) 아래 필수 폴더 생성 — 보장된 폴더 수 반환.</summary>
    public static int EnsureFolders(string? root = null)
    {
        var baseDir = string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root;
        var ok = 0;
        for (var i = 0; i < RequiredFolders.Length; i++)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(baseDir, RequiredFolders[i]));
                ok++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                Program.Log("AppFolderBootstrapper", ex);
            }
        }
        return ok;
    }
}
