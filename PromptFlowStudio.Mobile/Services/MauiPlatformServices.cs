using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;                 // AppInfo / Launcher / MainThread
using Microsoft.Maui.ApplicationModel.DataTransfer;    // Clipboard / Share / ShareFileRequest / ShareFile
using Microsoft.Maui.Storage;                          // FilePicker / PickOptions / FileResult
using PromptFlowStudio.Services.Platform;              // 공용 인터페이스(Shared 이관본)

namespace PromptFlowStudio.Mobile.Services;

/// <summary>
/// [PromptFlowStudio v5.3] MAUI 네이티브 플랫폼 서비스 — 데스크톱(WinForms) 구현을 대체(외부 NuGet 0 · Microsoft.Maui.* 내장 API만).
///   데스크톱 계약(<see cref="IFilePickerService"/> 등)을 모바일 네이티브 API로 구현/Graceful Stub 처리해 net8.0-android DI 정합성을 맞춘다.
/// </summary>

/// <summary>모바일 파일 선택 — <see cref="FilePicker"/> 사용. ⚠ 계약이 동기(데스크톱 STA 다이얼로그 레거시)라 백그라운드에서 async 를 구동해 대기.</summary>
public sealed class MauiFilePickerService : IFilePickerService
{
    public string? PickFile(string title, string filter, string? initialDir = null)
    {
        try
        {
            // 동기 계약 ↔ async FilePicker 임피던스: 스레드풀에서 구동 후 결과 대기(UI 스레드 직접 블로킹 회피 권장).
            //   모바일 UI 는 가급적 IMobilePlatformBridge.CaptureAsync/MediaPicker(async) 경로를 사용하세요.
            return Task.Run(async () =>
            {
                var opts = new PickOptions { PickerTitle = string.IsNullOrWhiteSpace(title) ? "파일 선택" : title };
                var res = await FilePicker.Default.PickAsync(opts).ConfigureAwait(false);
                return res?.FullPath;
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex) { Program.Log("MauiFilePicker.Pick", ex); return null; }   // 취소/미지원 → null(Graceful)
    }
}

/// <summary>모바일 클립보드 복사 — <see cref="Clipboard"/> 사용(비동기 best-effort · 데드락 회피 위해 대기하지 않음).</summary>
public sealed class MauiClipboardService : IClipboardService
{
    public bool SetText(string? text)
    {
        var s = text ?? "";
        if (s.Length == 0) return false;
        try { _ = Clipboard.Default.SetTextAsync(s); return true; }   // 클립보드 설정은 best-effort(빠름 · 실패는 드묾)
        catch (Exception ex) { Program.Log("MauiClipboard.SetText", ex); return false; }
    }
}

/// <summary>모바일 영상 내보내기 — OS <b>공유 시트</b>(<see cref="Share"/>)로 위임(저장 위치는 사용자가 앱에서 선택).</summary>
public sealed class MauiVideoExportService : IVideoExportService
{
    public async Task<string?> ExportAsync(string sourcePath, string suggestedFileName)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath)) return null;
        try
        {
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "영상 내보내기",
                File = new ShareFile(sourcePath),
            }).ConfigureAwait(false);
            return sourcePath;   // 공유 시트로 위임 완료(대상 앱/저장은 OS 가 처리)
        }
        catch (Exception ex) { Program.Log("MauiVideoExport.Export", ex); return null; }
    }
}

/// <summary>모바일 자동 업데이트 — 스토어 기반이라 앱 내 업데이트는 비활성. 설치 요청 시 스토어 페이지를 연다(Graceful Stub).</summary>
public sealed class MauiAutoUpdateService : IAutoUpdateService
{
    // 스토어 URL(placeholder · 운영 시 실 앱 ID 로 교체). Android=Play, iOS 는 App Store 링크로 분기 가능.
    private const string StoreUrl = "https://play.google.com/store/apps/details?id=com.angtec.promptflowstudio";

    public event Action? OnChange;

    public bool Enabled => false;                 // 모바일은 스토어가 업데이트를 관리 → 앱 내 자동 업데이트 비활성
    public bool UpdateAvailable => false;
    public string CurrentVersion => "v" + SafeVersion();
    public string? LatestVersion => null;
    public string? LatestReleaseNotes => null;
    public bool IsChecking => false;
    public bool IsDownloading => false;
    public int DownloadPercent => 0;
    public string? Status { get; private set; } = "모바일: 업데이트는 스토어에서 관리됩니다.";

    public Task CheckAsync(bool force = false, CancellationToken ct = default)
    {
        Status = "최신 버전은 App Store / Play 스토어에서 확인·설치됩니다.";
        OnChange?.Invoke();
        return Task.CompletedTask;
    }

    public async Task DownloadAndInstallAsync(CancellationToken ct = default)
    {
        try { await Launcher.Default.OpenAsync(StoreUrl).ConfigureAwait(false); }   // 스토어 페이지 열기(Graceful)
        catch (Exception ex) { Program.Log("MauiAutoUpdate.Store", ex); }
    }

    private static string SafeVersion()
    {
        try { return AppInfo.Current.VersionString; } catch { return "0.0.0"; }
    }
}
