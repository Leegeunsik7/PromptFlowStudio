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

    private void Raise() => Changed?.Invoke();
}
