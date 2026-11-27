using System.Collections.ObjectModel;
using PromptFlowStudio.Services.Gallery;

namespace PromptFlowStudio.ViewModels;

public enum GalleryFilter { All, Vertical9x16, Landscape16x9, Favorites, AiRender, Exported, Snapshots }

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
        _gallery.MediaMetadataChanged += OnMediaMetadataChanged;   // 싱글톤 수명 동일 — 해제 불요
    }

    private string? _lastDir;

    /// <summary>✂️ 트림 등 메타 변경 — 기존 항목은 뱃지만 재렌더, 목록에 없는 새 클립은 마지막 폴더 재스캔.</summary>
    private void OnMediaMetadataChanged(string mediaPath)
    {
        if (_gallery.FindByPath(mediaPath) is null && _lastDir is { Length: > 0 } dir) { Refresh(dir); return; }
        Raise();
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
    /// <summary>🔎 다각도 칩 필터(화면비 · 해상도 · 코덱).</summary>
    public GalleryAspectChip AspectChip { get; set; }
    public GalleryResolutionChip ResolutionChip { get; set; }
    public GalleryCodecChip CodecChip { get; set; }
    /// <summary>💽 스토리지 점유율(필터 재적용 시 갱신).</summary>
    public GalleryStorageBreakdown Storage { get; private set; } = new(0, 0, 0, 0, 0, 0, -1);
    /// <summary>검색/칩 조건 활성 여부(일괄 액션 노출 기준).</summary>
    public bool HasActiveSearch => !string.IsNullOrWhiteSpace(SearchQuery) || AspectChip != GalleryAspectChip.Any
        || ResolutionChip != GalleryResolutionChip.Any || CodecChip != GalleryCodecChip.Any;

    public void Refresh(string directoryPath)
    {
        _lastDir = directoryPath;
        _gallery.ScanGalleryItems(directoryPath);
        ApplyFilter();
    }

    public void SetFilter(GalleryFilter filter)
    {
        SelectedFilter = filter;
        ApplyFilter();
    }

    // ── 🎞️ 발행 미리보기 시각 — Hover-to-Play(2초 무음 루프) 위치 추적 ──
    /// <summary>호버 미리보기 루프 길이(초) — MediaGallery <c>ontimeupdate</c> 2초 되감기와 동일.</summary>
    public const double HoverLoopSec = 2.0;
    /// <summary>단조 시계(ms) — 테스트 주입용.</summary>
    public Func<long> ClockMs { get; set; } = static () => Environment.TickCount64;
    private Guid? _hoverId;
    private long _hoverStartMs;
    private Guid? _lastPreviewId;
    private double _lastPreviewSec;

    /// <summary>호버 재생 시작 기록.</summary>
    public void BeginHoverPreview(Guid itemId) { _hoverId = itemId; _hoverStartMs = ClockMs(); }

    /// <summary>호버 이탈 — 마지막 재생 위치를 해당 항목 미리보기 시각으로 보존.</summary>
    public void EndHoverPreview(Guid itemId)
    {
        if (_hoverId != itemId) return;
        RecordPreviewTime(itemId, CurrentHoverSec());
        _hoverId = null;
    }

    /// <summary>명시 미리보기 시각 지정(디자인 프레임 등).</summary>
    public void RecordPreviewTime(Guid itemId, double sec)
    {
        if (!double.IsFinite(sec) || sec <= 0) return;
        _lastPreviewId = itemId;
        _lastPreviewSec = sec;
    }

    private double CurrentHoverSec()
    {
        var ms = ClockMs() - _hoverStartMs;
        return ms <= 0 ? 0 : (ms % (long)(HoverLoopSec * 1000)) / 1000.0;
    }

    /// <summary>
    /// 🎞️ PublishModal 미리보기 시각 — 진행 중 호버 위치 → 직전 호버/지정 위치 → 길이 중간점 → 1초 순.
    /// </summary>
    public double ResolvePreviewTimestamp(GalleryItem? item)
    {
        const double Fallback = 1.0;
        if (item is null) return Fallback;
        if (_hoverId == item.ItemId && CurrentHoverSec() is var h && h > 0) return h;
        if (_lastPreviewId == item.ItemId && _lastPreviewSec > 0) return _lastPreviewSec;
        return item.DurationSeconds is double d && double.IsFinite(d) && d > 0 ? d / 2 : Fallback;
    }

    /// <summary>경로 기반 해석(라이브러리 타일/확대 모달) — 갤러리 미등록이면 1초.</summary>
    public double ResolvePreviewTimestamp(string? mediaPath)
        => string.IsNullOrWhiteSpace(mediaPath) ? 1.0 : ResolvePreviewTimestamp(_gallery.FindByPath(mediaPath));

    /// <summary>🎞️ 확대 재생(라이트박스) <c>&lt;video&gt;</c> 요소 id.</summary>
    public const string LightboxVideoId = "gl-lightbox-video";
    /// <summary>JS interop 조회 상한 — 초과/실패 시 1초 내 <see cref="ResolvePreviewTimestamp(string?)"/> 폴백.</summary>
    public static readonly TimeSpan LightboxQueryTimeout = TimeSpan.FromMilliseconds(800);

    /// <summary>
    /// 🎞️ 라이트박스 재생 위치 — <c>promptflow.getVideoCurrentTime(id)</c> 의 정확한 currentTime(초).
    /// JS 미가용·예외·타임아웃·음수/비유한 값 → 호버/중간점/1초 폴백.
    /// </summary>
    public async Task<double> ResolveLightboxTimestampAsync(Microsoft.JSInterop.IJSRuntime? js, string? mediaPath, string elementId = LightboxVideoId)
    {
        if (js is not null)
        {
            using var cts = new CancellationTokenSource(LightboxQueryTimeout);
            try
            {
                var t = await js.InvokeAsync<double>("promptflow.getVideoCurrentTime", cts.Token, new object?[] { elementId });
                if (double.IsFinite(t) && t >= 0) return t;
            }
            catch (Exception ex) when (ex is Microsoft.JSInterop.JSException or OperationCanceledException or InvalidOperationException or TaskCanceledException)
            {
                Program.Log("Gallery.LightboxTimestamp", ex);
            }
        }
        return ResolvePreviewTimestamp(mediaPath);
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
        var chips = new GallerySearchQuery(null, AspectChip, ResolutionChip, CodecChip);
        var all = _gallery.Items;
        foreach (var it in all)
            if (Matches(it, SelectedFilter, q) && GalleryMediaService.MatchesSearch(it, chips)) MediaItems.Add(it);
        if (SelectedItem is { } sel && !MediaItems.Contains(sel)) SelectedItem = null;
        Storage = _gallery.ComputeStorage();
        Raise();
    }

    public void SetAspectChip(GalleryAspectChip c) { AspectChip = AspectChip == c ? GalleryAspectChip.Any : c; ApplyFilter(); }
    public void SetResolutionChip(GalleryResolutionChip c) { ResolutionChip = ResolutionChip == c ? GalleryResolutionChip.Any : c; ApplyFilter(); }
    public void SetCodecChip(GalleryCodecChip c) { CodecChip = CodecChip == c ? GalleryCodecChip.Any : c; ApplyFilter(); }

    /// <summary>현재 검색 결과(샘플 제외) 수.</summary>
    public int SearchResultCount
    {
        get { var n = 0; foreach (var it in MediaItems) if (!it.IsSample) n++; return n; }
    }

    /// <summary>🗑 검색된 결과만 일괄 영구 삭제 — 성공 개수.</summary>
    public int DeleteSearchResults()
    {
        var ids = new List<Guid>(MediaItems.Count);
        foreach (var it in MediaItems) if (!it.IsSample) ids.Add(it.ItemId);
        if (ids.Count == 0) return 0;
        var n = _gallery.DeleteGalleryItems(ids);
        AfterBulkDelete();
        StatusMessage = n == ids.Count ? "🗑 검색 결과 " + n + "개 삭제됨" : "🗑 검색 결과 " + n + "/" + ids.Count + "개 삭제 — 일부 실패";
        Raise();
        return n;
    }

    /// <summary>📤 검색된 결과 일괄 내보내기 — 성공 개수.</summary>
    public int ExportSearchResults(string? targetDirectory = null)
    {
        var list = new List<GalleryItem>(MediaItems.Count);
        foreach (var it in MediaItems) if (!it.IsSample) list.Add(it);
        if (list.Count == 0) { StatusMessage = "📤 내보낼 검색 결과가 없습니다"; Raise(); return 0; }
        var dir = string.IsNullOrWhiteSpace(targetDirectory) ? GalleryMediaService.DefaultExportDirectory(DateTime.Now) : targetDirectory;
        try
        {
            var (n, d) = GalleryMediaService.ExportItems(list, dir);
            StatusMessage = "📤 " + n + "/" + list.Count + "개 내보냄 → " + d;
            Raise();
            return n;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Program.Log("GalleryVm.Export", ex);
            StatusMessage = "⚠ 내보내기 실패: " + ex.Message;
            Raise();
            return 0;
        }
    }

    public static bool Matches(GalleryItem it, GalleryFilter filter, string query)
    {
        if (filter == GalleryFilter.Vertical9x16 && it.AspectCategory != AspectCategory.Vertical9x16) return false;
        if (filter == GalleryFilter.Landscape16x9 && it.AspectCategory != AspectCategory.Landscape16x9) return false;
        if (filter == GalleryFilter.Favorites && !it.IsFavorite) return false;
        if (filter == GalleryFilter.AiRender && (it.IsSample || it.IsMock || it.IsExported || it.IsStill)) return false;
        if (filter == GalleryFilter.Snapshots && !it.IsStill) return false;
        if (filter == GalleryFilter.Exported && !it.IsExported) return false;
        return GalleryMediaService.MatchesText(it, query);   // 키워드 AND · 제목/프롬프트/파일명(티커)
    }

    public void RestoreToWorkspace(GalleryItem item)
    {
        if (item is null) return;
        _gallery.QueueRestore(_gallery.PackageRestoreContext(item));
        StatusMessage = "작업실로 복원 — " + item.Title;
        Raise();
        RestoreRequested?.Invoke();
    }

    /// <summary>⭐ 즐겨찾기 토글 — 메타 저장 후 필터 재적용(즐겨찾기 탭에서 해제 시 즉시 제외).</summary>
    public bool ToggleFavorite(GalleryItem item)
    {
        if (item is null) return false;
        var ok = _gallery.ToggleFavorite(item.ItemId);
        StatusMessage = ok ? (item.IsFavorite ? "⭐ 즐겨찾기 추가 — " : "☆ 즐겨찾기 해제 — ") + item.Title : "즐겨찾기 저장 실패(파일 잠금 또는 출력 폴더 외부)";
        ApplyFilter();
        return ok;
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

    /// <summary>⏳ 체크된 항목 비동기 영구 삭제 — 진행 보고 · 취소 시 조기 종료(이미 지운 항목 반영).</summary>
    public async Task<BatchDeleteResult> DeleteCheckedAsync(IProgress<BatchProgressReport>? progress = null, CancellationToken ct = default)
    {
        if (_checked.Count == 0) return default;
        var ids = new List<Guid>(_checked);
        var r = await _gallery.DeleteBatchAsync(ids, progress, ct).ConfigureAwait(false);
        AfterBulkDelete();
        StatusMessage = r.Canceled ? "⏹ 일괄 삭제 취소됨 — " + r.Deleted + "/" + r.Requested + "개 삭제됨"
            : r.Deleted == r.Requested ? "🗑 선택 " + r.Deleted + "개 삭제됨"
            : "🗑 " + r.Deleted + "/" + r.Requested + "개 삭제 — 일부 실패(파일 잠금 또는 출력 폴더 외부)";
        Raise();
        return r;
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

    // ── ☑️ 다중 선택 모드: 체크박스 노출 · 선택 번들 내보내기 · 일괄 삭제 ──
    /// <summary>선택 모드 활성(카드 체크박스 노출). 끄면 선택 해제.</summary>
    public bool IsSelectModeActive { get; private set; }

    public void ToggleSelectMode()
    {
        IsSelectModeActive = !IsSelectModeActive;
        if (!IsSelectModeActive) _checked.Clear();
        Raise();
    }

    /// <summary>선택된 미디어 ID(절대 경로) — 디스크에 남은 체크 항목만.</summary>
    public HashSet<string> SelectedMediaIds
    {
        get
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_checked.Count == 0) return set;
            foreach (var it in _gallery.Items)
                if (!it.IsSample && it.FilePath.Length > 0 && _checked.Contains(it.ItemId)) set.Add(it.FilePath);
            return set;
        }
    }

    /// <summary>현재 목록(샘플 제외) 전부 선택.</summary>
    public void SelectAll()
    {
        foreach (var it in MediaItems) if (!it.IsSample) _checked.Add(it.ItemId);
        Raise();
    }

    public void DeselectAll() => ClearChecks();

    /// <summary>📦 선택 항목만 .pfsbundle 로 내보내기 — 선택 없음 → null.</summary>
    public async Task<PromptFlowStudio.Services.Export.AssetBundleExportResult?> ExportSelectedBundleAsync(
        PromptFlowStudio.Services.Export.AssetBundleService bundles, string outputPath, bool includePresets = true, CancellationToken ct = default)
        => await ExportSelectedBundleAsync(bundles, outputPath, includePresets, null, ct).ConfigureAwait(false);

    /// <summary>⏳ 진행 보고 + 취소 지원 선택 번들 내보내기 — 취소 시 <see cref="OperationCanceledException"/> 전파(상태 문구 갱신).</summary>
    public async Task<PromptFlowStudio.Services.Export.AssetBundleExportResult?> ExportSelectedBundleAsync(
        PromptFlowStudio.Services.Export.AssetBundleService bundles, string outputPath, bool includePresets, IProgress<BatchProgressReport>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundles);
        var ids = SelectedMediaIds;
        if (ids.Count == 0) { StatusMessage = "📦 내보낼 선택 항목이 없습니다"; Raise(); return null; }
        PromptFlowStudio.Services.Export.AssetBundleExportResult r;
        try { r = await bundles.ExportBundleAsync(outputPath, new List<string>(ids), includePresets, progress, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { StatusMessage = "⏹ 번들 내보내기 취소됨"; Raise(); throw; }
        StatusMessage = r.Ok ? r.Message : "⚠ " + r.Message;
        Raise();
        return r;
    }

    // ── 👍 Pluto 학습 등록 · 🗑️ 중복/실패 정리 ──
    /// <summary>
    /// 👍 학습 등록 — 원문(없으면 사용 프롬프트)·보정본을 성공 레시피로 기록하고 메타 <c>plutoLearned</c> 표시(재클릭 중복 학습 차단).
    /// 샘플·Mock·빈 프롬프트·이미 등록됨은 false. 메모리 서비스 예외 격리.
    /// </summary>
    public bool LearnFromItem(GalleryItem item, PromptFlowStudio.Services.AI.PlutoVideoLearningMemoryService? memory)
    {
        if (item is null || memory is null) return false;
        if (item.IsSample || item.IsMock || item.IsStill) { SetStatus("👍 학습 등록은 실제 AI 렌더 영상만 가능합니다"); return false; }
        if (item.IsLearned) { SetStatus("👍 이미 학습 등록된 클립입니다 — " + item.Title); return false; }
        if (string.IsNullOrWhiteSpace(item.Prompt)) { SetStatus("👍 프롬프트 메타가 없어 학습할 수 없습니다"); return false; }
        string? ruleId;
        try { ruleId = memory.RecordPositiveOutcome(item.RawPrompt ?? item.Prompt, item.Prompt); }
        catch (Exception ex) { Program.Log("Gallery.Learn", ex); SetStatus("⚠ 학습 등록 실패: " + ex.Message); return false; }
        if (ruleId is null) { SetStatus("👍 학습할 키워드가 없습니다 — " + item.Title); return false; }
        var marked = _gallery.MarkLearned(item.ItemId);
        SetStatus(marked ? "🧠 Pluto 학습 등록 완료 — " + item.Title : "🧠 학습 등록 완료(메타 표시 실패 · 파일 잠금)");
        return true;
    }

    private int _cleanupBusy;
    /// <summary>정리 계획/실행 진행 중(중복 클릭 · 동시 실행 차단).</summary>
    public bool IsCleanupBusy => Volatile.Read(ref _cleanupBusy) != 0;
    /// <summary>마지막 정리 계획(확인 대기) — 실행/취소 시 null.</summary>
    public IReadOnlyList<GalleryCleanupCandidate>? PendingCleanup { get; private set; }

    /// <summary>🗑️ 중복/실패 정리 계획 수립(스레드풀 · 삭제 없음). 이미 진행 중이면 null.</summary>
    public async Task<IReadOnlyList<GalleryCleanupCandidate>?> PlanCleanupAsync(string? root)
    {
        if (Interlocked.CompareExchange(ref _cleanupBusy, 1, 0) != 0) return null;
        Raise();
        try
        {
            var plan = await Task.Run(() => _gallery.PlanCleanup(root)).ConfigureAwait(false);
            PendingCleanup = plan;
            StatusMessage = plan.Count == 0 ? "✨ 정리할 중복/실패 파일이 없습니다" : null;
            return plan;
        }
        catch (Exception ex) { Program.Log("Gallery.PlanCleanup", ex); PendingCleanup = null; StatusMessage = "⚠ 정리 계획 실패: " + ex.Message; return null; }
        finally { Volatile.Write(ref _cleanupBusy, 0); Raise(); }
    }

    /// <summary>확인된 정리 계획 실행 — 이미 진행 중이거나 계획 없음이면 null.</summary>
    public async Task<GalleryCleanupResult?> ExecutePendingCleanupAsync()
    {
        var plan = PendingCleanup;
        if (plan is null || plan.Count == 0) return null;
        if (Interlocked.CompareExchange(ref _cleanupBusy, 1, 0) != 0) return null;
        Raise();
        try
        {
            var r = await Task.Run(() => _gallery.ExecuteCleanup(plan)).ConfigureAwait(false);
            PendingCleanup = null;
            if (SelectedItem is { } sel && !File.Exists(sel.FilePath) && !sel.IsSample) SelectedItem = null;
            AfterBulkDelete();
            StatusMessage = "🗑️ 중복/실패 " + r.Deleted.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 정리 · "
                + PromptFlowStudio.Services.Media.MediaLibraryService.FormatBytes(r.FreedBytes) + " 확보"
                + (r.Failed > 0 ? " · " + r.Failed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개 실패(사용 중)" : "");
            return r;
        }
        catch (Exception ex) { Program.Log("Gallery.Cleanup", ex); StatusMessage = "⚠ 정리 실패: " + ex.Message; return null; }
        finally { Volatile.Write(ref _cleanupBusy, 0); Raise(); }
    }

    /// <summary>정리 확인 취소.</summary>
    public void CancelPendingCleanup() { PendingCleanup = null; Raise(); }

    /// <summary>정리 후보 사유별 개수(확인 배너 문구).</summary>
    public static (int Duplicates, int Broken, int Orphans, long Bytes) Summarize(IReadOnlyList<GalleryCleanupCandidate>? plan)
    {
        int d = 0, b = 0, o = 0; long bytes = 0;
        if (plan is null) return (0, 0, 0, 0);
        for (var i = 0; i < plan.Count; i++)
        {
            bytes += plan[i].SizeBytes;
            switch (plan[i].Reason)
            {
                case GalleryCleanupReason.Duplicate: d++; break;
                case GalleryCleanupReason.EmptyOrBroken: b++; break;
                default: o++; break;
            }
        }
        return (d, b, o, bytes);
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
