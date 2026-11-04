using System.Collections.ObjectModel;
using PromptFlowStudio.Services.Gallery;

namespace PromptFlowStudio.ViewModels;

public enum GalleryFilter { All, Vertical9x16, Landscape16x9 }

/// <summary>
/// [PromptFlowStudio] 갤러리 화면 VM — 카테고리/검색 필터 · 인스펙터 선택 · Workspace 복원 핸드오프.
///   ▸ <see cref="MediaItems"/>는 마지막 스캔 결과에 필터를 적용한 뷰(스캔은 <see cref="Refresh"/>).
/// </summary>
public sealed class GalleryViewModel
{
    private readonly GalleryMediaService _gallery;

    public GalleryViewModel(GalleryMediaService gallery)
    {
        _gallery = gallery ?? throw new ArgumentNullException(nameof(gallery));
    }

    public event Action? Changed;
    /// <summary>복원 번들 큐잉 직후 발화 — 페이지가 Workspace 로 이동.</summary>
    public event Action? RestoreRequested;

    public ObservableCollection<GalleryItem> MediaItems { get; } = new();
    public GalleryItem? SelectedItem { get; private set; }
    public GalleryFilter SelectedFilter { get; set; } = GalleryFilter.All;
    public string SearchQuery { get; set; } = "";
    public int TotalCount => _gallery.Items.Count;
    public string? StatusMessage { get; private set; }

    public void Refresh(string directoryPath)
    {
        _gallery.ScanGalleryItems(directoryPath);
        ApplyFilter();
    }

    public void SetFilter(GalleryFilter filter)
    {
        SelectedFilter = filter;
        ApplyFilter();
    }

    public void SelectMedia(GalleryItem item)
    {
        SelectedItem = item;
        Raise();
    }

    /// <summary>렌더 큐 딥링크 — 필터/검색 해제 후 경로가 일치하는 항목을 인스펙터에 선택. 없으면 false + 상태 메시지.</summary>
    public bool FocusByPath(string? mediaPath)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return false;
        SelectedFilter = GalleryFilter.All;
        SearchQuery = "";
        ApplyFilter();
        if (_gallery.FindByPath(mediaPath) is not { } item)
        {
            StatusMessage = "갤러리에서 찾을 수 없음 — " + Path.GetFileName(mediaPath);
            Raise();
            return false;
        }
        SelectMedia(item);
        return true;
    }

    public void ApplyFilter()
    {
        MediaItems.Clear();
        var q = SearchQuery?.Trim() ?? "";
        foreach (var it in _gallery.Items)
            if (Matches(it, SelectedFilter, q)) MediaItems.Add(it);
        if (SelectedItem is { } sel && !MediaItems.Contains(sel)) SelectedItem = null;
        Raise();
    }

    public static bool Matches(GalleryItem it, GalleryFilter filter, string query)
    {
        if (filter == GalleryFilter.Vertical9x16 && it.AspectCategory != AspectCategory.Vertical9x16) return false;
        if (filter == GalleryFilter.Landscape16x9 && it.AspectCategory != AspectCategory.Landscape16x9) return false;
        return query.Length == 0
            || it.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || it.Prompt.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    public void RestoreToWorkspace(GalleryItem item)
    {
        if (item is null) return;
        _gallery.QueueRestore(_gallery.PackageRestoreContext(item));
        StatusMessage = "작업실로 복원 — " + item.Title;
        Raise();
        RestoreRequested?.Invoke();
    }

    public bool Delete(GalleryItem item)
    {
        if (item is null) return false;
        var ok = _gallery.DeleteGalleryItem(item.ItemId);
        StatusMessage = ok ? "삭제됨 — " + item.Title : "삭제 실패(파일 잠금 또는 출력 폴더 외부)";
        if (ok && ReferenceEquals(SelectedItem, item)) SelectedItem = null;
        ApplyFilter();
        return ok;
    }

    // ── 🧹 정화 도구: Mock 일괄 삭제 · 체크박스 다중 선택 삭제 ──
    private readonly HashSet<Guid> _checked = new();
    public int CheckedCount => _checked.Count;
    public int MockCount => _gallery.MockCount;
    public bool IsChecked(GalleryItem item) => item is not null && _checked.Contains(item.ItemId);

    public void ToggleCheck(GalleryItem item)
    {
        if (item is null) return;
        if (!_checked.Remove(item.ItemId)) _checked.Add(item.ItemId);
        Raise();
    }

    public void ClearChecks()
    {
        _checked.Clear();
        Raise();
    }

    /// <summary>체크된 항목 영구 삭제 — 성공 개수.</summary>
    public int DeleteChecked()
    {
        if (_checked.Count == 0) return 0;
        var requested = _checked.Count;
        var n = _gallery.DeleteGalleryItems(_checked);
        AfterBulkDelete();
        StatusMessage = n == requested ? "🗑 선택 " + n + "개 삭제됨" : "🗑 " + n + "/" + requested + "개 삭제 — 일부 실패(파일 잠금 또는 출력 폴더 외부)";
        Raise();
        return n;
    }

    /// <summary>🧹 Mock 더미 일괄 영구 삭제 — 성공 개수.</summary>
    public int DeleteMockAssets()
    {
        var requested = _gallery.MockCount;
        var n = _gallery.DeleteMockAssets();
        AfterBulkDelete();
        StatusMessage = requested == 0 ? "🧹 삭제할 Mock 더미 파일이 없습니다" : n == requested ? "🧹 Mock 더미 " + n + "개 삭제 완료" : "🧹 Mock " + n + "/" + requested + "개 삭제 — 일부 실패";
        Raise();
        return n;
    }

    private void AfterBulkDelete()
    {
        _checked.Clear();
        ApplyFilter();   // 삭제된 선택 항목은 필터 재적용 시 해제
    }

    /// <summary>외부(카드 액션) 상태 메시지 표시.</summary>
    public void SetStatus(string? message)
    {
        StatusMessage = message;
        Raise();
    }

    private void Raise() => Changed?.Invoke();
}
