using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Render;

namespace PromptFlowStudio.Services.Prompt;

/// <summary>⭐ 작업실 전체 설정 프리셋 — 프롬프트·네거티브·카메라 모션·스타일·듀레이션 모드 스냅숏.</summary>
public sealed record WorkspaceUserPreset(
    Guid Id, string Name, string Prompt, string NegativePrompt, string CameraMotion, string Style,
    VideoDurationMode DurationMode, DateTime CreatedAt);

/// <summary>
/// [PromptFlowStudio] ⭐ 사용자 커스텀 프리셋 관리자 — %LocalAppData%\PromptFlowStudio\user_presets.json 영속.
///   ▸ 지연 로드 · lock 직렬화 · 임시파일 → 원자 교체 · 손상 시 <c>.corrupt.bak</c> [부분 구제] 보존.
///   ▸ 동일 이름 재저장은 덮어쓰기(Id 유지) — 중복 칩 차단.
/// </summary>
public sealed class PresetManagerService
{
    public const string FileName = "user_presets.json";
    public const int MaxNameLength = 60;
    public const int MaxNegativeLength = 500;
    public const int MaxPresets = 100;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _gate = new();
    private List<WorkspaceUserPreset>? _items;

    public string StoragePath { get; }

    /// <summary>프리셋 목록 변경 통지(저장·삭제). 구독자 예외는 격리.</summary>
    public event Action? Changed;

    public PresetManagerService() : this(null) { }

    public PresetManagerService(string? storagePath)
    {
        StoragePath = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", FileName)
            : storagePath;
    }

    /// <summary>최신 저장 순 스냅숏. 파일 없음/손상이면 빈 목록.</summary>
    public IReadOnlyList<WorkspaceUserPreset> GetAll()
    {
        lock (_gate) return EnsureLocked().ToArray();
    }

    public WorkspaceUserPreset? Find(Guid id)
    {
        lock (_gate)
        {
            var list = EnsureLocked();
            for (var i = 0; i < list.Count; i++) if (list[i].Id == id) return list[i];
            return null;
        }
    }

    /// <summary>저장(이름 ≤60 · 프롬프트 ≤800 · 네거티브 ≤500 절삭). 빈 이름/프롬프트는 <see cref="ArgumentException"/>. 동일 이름은 덮어쓰기.</summary>
    public WorkspaceUserPreset Save(string name, string prompt, string? negativePrompt, string? cameraMotion, string? style, VideoDurationMode durationMode)
    {
        var n = name?.Trim(); var p = prompt?.Trim();
        if (string.IsNullOrEmpty(n)) throw new ArgumentException("프리셋 이름이 비어 있습니다.", nameof(name));
        if (string.IsNullOrEmpty(p)) throw new ArgumentException("프리셋 프롬프트가 비어 있습니다.", nameof(prompt));
        if (n.Length > MaxNameLength) n = n[..MaxNameLength];
        if (p.Length > PromptSynthesizerService.MaxCharacterLimit) p = p[..PromptSynthesizerService.MaxCharacterLimit];
        var neg = negativePrompt?.Trim() ?? "";
        if (neg.Length > MaxNegativeLength) neg = neg[..MaxNegativeLength];
        if (!Enum.IsDefined(durationMode)) durationMode = VideoDurationMode.Sec5_Single;

        WorkspaceUserPreset saved;
        lock (_gate)
        {
            var list = EnsureLocked();
            var idx = list.FindIndex(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
            var id = idx >= 0 ? list[idx].Id : Guid.NewGuid();
            if (idx >= 0) list.RemoveAt(idx);
            saved = new WorkspaceUserPreset(id, n, p, neg, cameraMotion?.Trim() ?? "", style?.Trim() ?? "", durationMode, DateTime.UtcNow);
            list.Insert(0, saved);
            if (list.Count > MaxPresets) list.RemoveRange(MaxPresets, list.Count - MaxPresets);
            PersistLocked(list);
        }
        RaiseChanged();
        return saved;
    }

    /// <summary>Id 삭제 — 없으면 false(파일 불변).</summary>
    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            var list = EnsureLocked();
            var idx = list.FindIndex(x => x.Id == id);
            if (idx < 0) return false;
            list.RemoveAt(idx);
            PersistLocked(list);
        }
        RaiseChanged();
        return true;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Program.Log("PresetManager.Changed", ex); }
    }

    private List<WorkspaceUserPreset> EnsureLocked()
    {
        if (_items is not null) return _items;
        _items = new List<WorkspaceUserPreset>();
        try
        {
            if (!File.Exists(StoragePath)) return _items;
            var arr = JsonSerializer.Deserialize<WorkspaceUserPreset[]>(File.ReadAllText(StoragePath, Encoding.UTF8), JsonOpts);
            if (arr is not null)
                foreach (var x in arr)
                    if (x is not null && x.Id != Guid.Empty && !string.IsNullOrWhiteSpace(x.Name) && !string.IsNullOrWhiteSpace(x.Prompt))
                        _items.Add(x with
                        {
                            NegativePrompt = x.NegativePrompt ?? "",
                            CameraMotion = x.CameraMotion ?? "",
                            Style = x.Style ?? "",
                            DurationMode = Enum.IsDefined(x.DurationMode) ? x.DurationMode : VideoDurationMode.Sec5_Single,
                        });
        }
        catch (JsonException)
        {
            // [부분 구제] 손상 파일은 덮어쓰기 전 백업 보존.
            try { File.Copy(StoragePath, StoragePath + ".corrupt.bak", overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Program.Log("PresetManager.Load", ex); }
        return _items;
    }

    private void PersistLocked(List<WorkspaceUserPreset> list)
    {
        var dir = Path.GetDirectoryName(StoragePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = StoragePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts), new UTF8Encoding(false));
        File.Move(tmp, StoragePath, overwrite: true);
    }
}
