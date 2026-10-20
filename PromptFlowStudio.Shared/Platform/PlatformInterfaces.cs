using System;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Platform;

// [PromptFlowStudio v5.3] 플랫폼 종속 서비스 인터페이스 4종 — 데스크톱/모바일 공용 계약(Shared RCL).
//   구현체는 호스트별 분리: 데스크톱(WinForms) = PromptFlowStudio, 모바일(MAUI) = PromptFlowStudio.Mobile.
//   ⚠ 네임스페이스는 기존 소비자(Blazor @inject · MainForm DI)와의 호환을 위해 PromptFlowStudio.Services.Platform 그대로 유지.
//     (net8.0-android 빌드가 데스크톱 전용 WinForms/Drawing 구현체를 끌어오지 않도록 계약만 여기로 이관.)

/// <summary>파일 열기 대화상자 계약(경로 선택용). 취소/실패는 null.</summary>
public interface IFilePickerService
{
    /// <summary>파일을 고른다(데스크톱=OpenFileDialog · 모바일=FilePicker). 취소하면 null.</summary>
    string? PickFile(string title, string filter, string? initialDir = null);
}

/// <summary>텍스트를 시스템 클립보드에 복사하는 계약(1클릭 복사용).</summary>
public interface IClipboardService
{
    /// <summary>텍스트를 클립보드에 복사한다. 빈 문자열/실패는 false(Graceful).</summary>
    bool SetText(string? text);
}

/// <summary>완성 영상을 사용자가 지정한 위치로 내보내는 계약(데스크톱=SaveFileDialog · 모바일=OS 공유 시트).</summary>
public interface IVideoExportService
{
    /// <summary>저장 위치를 받아 <paramref name="sourcePath"/> 를 내보낸다. 취소/실패는 null.</summary>
    Task<string?> ExportAsync(string sourcePath, string suggestedFileName);
}

/// <summary>앱 내 자동 업데이트 계약(데스크톱=GitHub Releases · 모바일=스토어 위임 · 관찰 가능).</summary>
public interface IAutoUpdateService
{
    /// <summary>상태 변경 방송(구독: UI 뱃지).</summary>
    event Action? OnChange;

    /// <summary>업데이트 확인이 가능한 구성인지.</summary>
    bool Enabled { get; }
    bool UpdateAvailable { get; }
    string CurrentVersion { get; }
    string? LatestVersion { get; }
    /// <summary>최신 릴리스 설명(마크다운 원문). 없으면 null.</summary>
    string? LatestReleaseNotes { get; }
    bool IsChecking { get; }
    bool IsDownloading { get; }
    int DownloadPercent { get; }
    string? Status { get; }

    /// <summary>최신 릴리스 확인(force=true 면 강제 재확인).</summary>
    Task CheckAsync(bool force = false, CancellationToken ct = default);

    /// <summary>새 버전 설치(데스크톱=Setup.exe 무음 · 모바일=스토어 열기).</summary>
    Task DownloadAndInstallAsync(CancellationToken ct = default);
}
