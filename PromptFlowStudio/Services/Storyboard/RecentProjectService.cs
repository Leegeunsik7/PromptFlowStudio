using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>최근 프로젝트 항목 — 파일 절대경로 + 제목 + 마지막 접근 시각(ISO-8601).</summary>
public sealed record RecentProject(string Path, string Title, string SavedAt);

/// <summary>
/// [PromptFlowStudio] 최근 프로젝트(MRU) 관리 — 저장/열기한 프로젝트 파일 경로를 <c>%LocalAppData%/PromptFlowStudio/recents.json</c>
///   에 최대 10개 보관(앱 재시작 후에도 유지). 최신이 앞. 순수 <see cref="System.Text.Json"/>(외부 NuGet 0).
///   변경 시 <see cref="OnChange"/> 방송. 존재하지 않는 파일은 열기 시 호출부가 <see cref="Remove"/> 로 정리한다.
/// </summary>
public sealed class RecentProjectService
{
    private const int MaxItems = 10;
    private readonly string _path;
    private readonly object _gate = new();
    private List<RecentProject> _items = new();

    /// <summary>목록 변경 방송(구독: 컴포넌트).</summary>
    public event Action? OnChange;

    public RecentProjectService()
    {
        _path = System.IO.Path.Combine(LicenseService.DataAreaRoot(), "recents.json");
        LoadFromDisk();
    }

    /// <summary>현재 최근 목록(최신순 스냅샷 복사본 — 열거 중 변경 안전).</summary>
    public IReadOnlyList<RecentProject> Items
    {
        get { lock (_gate) return _items.ToList(); }
    }

    /// <summary>항목을 맨 앞으로(있으면 승격, 없으면 추가) · 10개 초과분 제거 · 저장.</summary>
    public void Touch(string projectPath, string title)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return;
        lock (_gate)
        {
            var full = SafeFull(projectPath);
            _items.RemoveAll(r => PathEq(r.Path, full));
            _items.Insert(0, new RecentProject(full, string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title, DateTime.Now.ToString("o")));
            if (_items.Count > MaxItems) _items = _items.Take(MaxItems).ToList();
            SaveToDisk();
        }
        OnChange?.Invoke();
    }

    /// <summary>항목 제거(삭제된 파일 클릭 시) · 저장.</summary>
    public void Remove(string projectPath)
    {
        bool changed;
        lock (_gate)
        {
            var full = SafeFull(projectPath);
            changed = _items.RemoveAll(r => PathEq(r.Path, full)) > 0;
            if (changed) SaveToDisk();
        }
        if (changed) OnChange?.Invoke();
    }

    // ── 영속화 ──
    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize(File.ReadAllText(_path), PromptFlowJsonContext.Default.RecentProjectList);
            if (list is not null)
                _items = list.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Path)).Take(MaxItems).ToList();
        }
        catch (Exception ex) { Program.Log("RecentProjectService.Load", ex); _items = new(); }
    }

    private void SaveToDisk()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, PromptFlowJsonContext.Default.RecentProjectList));
            File.Copy(tmp, _path, overwrite: true);
            try { File.Delete(tmp); } catch { /* 무시 */ }
        }
        catch (Exception ex) { Program.Log("RecentProjectService.Save", ex); }
    }

    private static string SafeFull(string p) { try { return System.IO.Path.GetFullPath(p); } catch { return p; } }
    private static bool PathEq(string a, string b) => string.Equals(SafeFull(a), SafeFull(b), StringComparison.OrdinalIgnoreCase);
}
