using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>
/// [PromptFlowStudio] 테마 즐겨찾기(Pin) 서비스(v3.1.0 자율 발상 · 관찰 가능 싱글턴 · 순수 BCL).
///   ▸ 자주 쓰는 테마를 📌 고정하면 갤러리 상단에 우선 노출 → 대량 테마(102/45종)에서 즉시 접근.
///   ▸ 핀 목록을 로컬(<see cref="Services.Account.LicenseService.DataAreaRoot"/>)에 JSON 영속 → 세션 간 유지.
/// </summary>
public sealed class ThemePinService
{
    private readonly object _lock = new();
    private readonly HashSet<string> _pinned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>핀 변경 방송.</summary>
    public event Action? OnChange;

    public ThemePinService() => TryLoad();

    private string PinPath => Path.Combine(LicenseService.DataAreaRoot(), "pinned_themes.json");

    /// <summary>고정된 테마 키(읽기 전용 스냅샷).</summary>
    public IReadOnlyCollection<string> Pinned { get { lock (_lock) return _pinned.ToList(); } }
    /// <summary>고정 개수.</summary>
    public int Count { get { lock (_lock) return _pinned.Count; } }

    /// <summary>테마가 고정되었는지.</summary>
    public bool IsPinned(string? key) { if (string.IsNullOrWhiteSpace(key)) return false; lock (_lock) return _pinned.Contains(key!); }

    /// <summary>고정 토글(추가↔해제). 반환: 토글 후 고정 여부.</summary>
    public bool Toggle(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        bool nowPinned;
        lock (_lock)
        {
            if (_pinned.Contains(key!)) { _pinned.Remove(key!); nowPinned = false; }
            else { _pinned.Add(key!); nowPinned = true; }
        }
        TrySave(); OnChange?.Invoke();
        return nowPinned;
    }

    /// <summary>고정 테마를 앞으로 정렬(안정 정렬 · 나머지 순서 보존).</summary>
    public IReadOnlyList<BabyThemeDef> SortPinnedFirst(IEnumerable<BabyThemeDef> themes)
    {
        var list = (themes ?? Array.Empty<BabyThemeDef>()).ToList();
        return list.OrderByDescending(t => IsPinned(t.Key) ? 1 : 0).ToList();   // OrderBy 는 안정 정렬
    }

    private void TrySave()
    {
        try { List<string> snap; lock (_lock) snap = _pinned.ToList(); File.WriteAllText(PinPath, JsonSerializer.Serialize(snap, PromptFlowJsonContext.Default.StringList), new UTF8Encoding(false)); }
        catch (Exception ex) { Program.Log("ThemePin.Save", ex); }
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(PinPath)) return;
            var list = JsonSerializer.Deserialize(File.ReadAllText(PinPath), PromptFlowJsonContext.Default.StringList);
            if (list is not null) lock (_lock) { _pinned.Clear(); foreach (var k in list) if (!string.IsNullOrWhiteSpace(k)) _pinned.Add(k); }
        }
        catch (Exception ex) { Program.Log("ThemePin.Load", ex); }
    }
}

/// <summary>
/// [PromptFlowStudio] 테마 프롬프트 미리보기 빌더(v3.1.0 자율 발상 · static · 순수 BCL).
///   ▸ 카드 호버 시 3초 안에 훑을 수 있는 <b>프롬프트 요약 미리보기</b>(훅 + 예시 + 첫 비트 샷)를 만들어 툴팁으로 노출.
/// </summary>
public static class ThemePreviewBuilder
{
    /// <summary>툴팁용 미리보기 라인(훅 · 예시 · 컷1 샷 요약).</summary>
    public static IReadOnlyList<string> BuildLines(BabyThemeDef theme)
    {
        if (theme is null) return Array.Empty<string>();
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(theme.Hook)) lines.Add("🎯 " + theme.Hook);
        if (!string.IsNullOrWhiteSpace(theme.Example)) lines.Add("💡 " + theme.Example);
        var b1 = theme.Beats?.FirstOrDefault();
        if (b1 is not null)
        {
            lines.Add("🗣️ " + Truncate(b1.V, 40));
            lines.Add("🎞️ " + Truncate(b1.Shot, 110));
        }
        lines.Add($"🎬 {theme.Beats?.Count ?? 0}컷 · 배속 {theme.ShortSpeed:0.##}x");
        return lines;
    }

    /// <summary>단일 문자열 미리보기(title 속성/플레인 툴팁용).</summary>
    public static string Build(BabyThemeDef theme) => string.Join("\n", BuildLines(theme));

    private static string Truncate(string? s, int max)
    {
        s = (s ?? "").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }
}
