using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Ui;

/// <summary>사용자가 저장한 모션 프리셋 1개 — 이름 + 조합 스니펫 + 저장시각.</summary>
public sealed record CustomPreset(string Id, string Name, string Snippet, string SavedAt);

/// <summary>
/// [PromptFlowStudio] 사용자 정의 모션 프리셋 즐겨찾기 — <c>%LocalAppData%\PromptFlowStudio\custom_presets.json</c> 에
///   영속화(순수 System.Text.Json). /motion 페이지가 조합을 저장/불러오기/삭제한다. 변경 시 <see cref="OnChange"/>.
/// </summary>
public sealed class CustomPresetService
{
    private readonly string _path;
    private readonly object _lock = new();

    public event Action? OnChange;

    public CustomPresetService() => _path = Path.Combine(LicenseService.DataAreaRoot(), "custom_presets.json");

    /// <summary>저장된 프리셋(최신순 — 새로 저장한 것이 앞).</summary>
    public List<CustomPreset> List() { lock (_lock) return Load(); }

    public void Save(string name, string snippet)
    {
        var n = (name ?? "").Trim();
        var s = (snippet ?? "").Trim();
        if (n.Length == 0 || s.Length == 0) return;
        lock (_lock)
        {
            var list = Load();
            list.Insert(0, new CustomPreset(Guid.NewGuid().ToString("N")[..8], n, s, DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
            Persist(list);
        }
        OnChange?.Invoke();
    }

    public void Remove(string id)
    {
        bool changed;
        lock (_lock)
        {
            var list = Load();
            changed = list.RemoveAll(p => p.Id == id) > 0;
            if (changed) Persist(list);
        }
        if (changed) OnChange?.Invoke();
    }

    private List<CustomPreset> Load()
    {
        try
        {
            return File.Exists(_path)
                ? (JsonSerializer.Deserialize(File.ReadAllText(_path), PromptFlowJsonContext.Default.CustomPresetList) ?? new())
                : new();
        }
        catch (Exception ex) { Program.Log("CustomPreset.Load", ex); return new(); }
    }

    private void Persist(List<CustomPreset> list)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, PromptFlowJsonContext.Default.CustomPresetList));
            File.Copy(tmp, _path, overwrite: true);
            try { File.Delete(tmp); } catch { /* 임시파일 삭제 실패 무시 */ }
        }
        catch (Exception ex) { Program.Log("CustomPreset.Save", ex); }
    }
}
