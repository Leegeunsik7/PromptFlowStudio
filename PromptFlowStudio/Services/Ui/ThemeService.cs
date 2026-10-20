using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PromptFlowStudio.Services.Account;   // LicenseService.DataAreaRoot()

namespace PromptFlowStudio.Services.Ui;

/// <summary>테마 색상 토큰 묶음(모두 CSS 색상 문자열). UI 전역 커스텀 프로퍼티로 매핑된다.</summary>
public sealed record ThemeColors(
    string Bg, string Card, string Card2, string Line,
    string TextMain, string TextSub, string TextMuted,
    string Accent, string AccentHover, string Accent2);

/// <summary>이름 붙은 테마(내장 프리셋 또는 사용자 커스텀).</summary>
public sealed record AppTheme(string Name, ThemeColors Colors, bool BuiltIn = false);

/// <summary>
/// [PromptFlowStudio] 스튜디오 커스텀 테마 엔진 — 순수 BCL + CSS 커스텀 프로퍼티(외부 NuGet 0, JS interop 없음).
///   ▸ 활성 테마의 색상을 <see cref="BuildCss"/> 로 <c>:root{ --token: … !important }</c> 오버라이드 문자열로 만들어
///     MainLayout 이 <c>&lt;style&gt;</c> 로 렌더한다. app.css 의 <c>:root[data-theme]</c>(특이성 0,2,0)를 이기려 !important 사용.
///   ▸ 내장 프리셋 4종(Classic Dark / Cyberpunk Neon / Cinematic Gold / Studio Slate) + 사용자 커스텀(themes.json 영속).
///   ▸ 라이브 프리뷰: <see cref="Preview"/>(비영속) → OnChange → 앱 전체 즉시 반영. 저장은 <see cref="SaveCustom"/>+<see cref="Apply"/>.
///   ▸ 보안: 모든 색상 값은 <see cref="SafeColor"/> 로 검증(해시/rgb/hsl/이름만 허용) — Import JSON 을 통한 CSS/스타일 주입 차단.
///   ▸ thread-safe: 커스텀 목록·활성·파일 IO 는 lock 으로 직렬화. 실패는 Graceful(기본 테마로 폴백).
/// </summary>
public sealed class ThemeService
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _exportDir;

    private readonly List<AppTheme> _custom = new();
    private AppTheme? _persisted;   // 영속(저장된) 활성 테마
    private AppTheme? _preview;     // 라이브 프리뷰(비영속) — 있으면 우선

    public event Action? OnChange;

    public ThemeService()
    {
        _path = Path.Combine(LicenseService.DataAreaRoot(), "themes.json");
        _exportDir = Path.Combine(LicenseService.DataAreaRoot(), "exports");
        Load();
    }

    // ── 내장 프리셋 4종 ──
    public static readonly IReadOnlyList<AppTheme> BuiltIns = new[]
    {
        new AppTheme("Classic Dark",   new("#0a0f1a", "#111a2e", "#0d1524", "#223047", "#f8fafc", "#cbd5e1", "#94a3b8", "#7c6cff", "#f472b6", "#22d3ee"), true),
        new AppTheme("Cyberpunk Neon", new("#08060f", "#12102a", "#0d0b20", "#34215a", "#f3eaff", "#c4b0e8", "#8a78b8", "#ff2bd6", "#00e5ff", "#b026ff"), true),
        new AppTheme("Cinematic Gold", new("#120f08", "#1c1810", "#15110a", "#3a2f1c", "#f8f2e4", "#d8c9a6", "#a08a5e", "#e3b155", "#f6d488", "#c98a2f"), true),
        new AppTheme("Studio Slate",   new("#171a21", "#21262f", "#1b1f27", "#333a47", "#eef2f8", "#b4becd", "#7e8798", "#5b8def", "#7aa5f5", "#46c7ac"), true),
        // 2026 트렌디 신규 3종 — 지정 토큰(card/card-2/line/accent/text-main) + 색조 맞춤 보조 토큰(bg/sub/muted/accent-hover/accent-2).
        new AppTheme("Obsidian Mint",   new("#080d12", "#0d131a", "#141e29", "#1e2d3d", "#f0fdf4", "#a7c0b5", "#6b8478", "#10b981", "#5eead4", "#22d3ee"), true),
        new AppTheme("Cosmic Lavender", new("#0b0818", "#120e21", "#1c1633", "#2d2252", "#faf5ff", "#cbb8e8", "#8b7bb0", "#a855f7", "#c084fc", "#e879f9"), true),
        new AppTheme("Midnight Coral",  new("#070c15", "#0c1421", "#132035", "#1e3252", "#fff7ed", "#d6c3b0", "#7e8fa8", "#f97316", "#fb923c", "#38bdf8"), true),
    };

    /// <summary>현재 활성 테마(프리뷰 > 영속). null 이면 기본(app.css data-theme 시스템).</summary>
    public AppTheme? Active { get { lock (_gate) return _preview ?? _persisted; } }

    /// <summary>MainLayout 이 &lt;style&gt; 로 렌더할 :root 오버라이드 CSS. 활성 없으면 빈 문자열.</summary>
    public string ActiveCss { get { var a = Active; return a is null ? "" : BuildCss(a.Colors); } }

    public IReadOnlyList<AppTheme> Custom { get { lock (_gate) return _custom.ToList(); } }
    public IEnumerable<AppTheme> All => BuiltIns.Concat(Custom);

    // ── 적용/프리뷰 ──
    /// <summary>테마를 영속 활성으로 지정(null = 기본). 프리뷰는 해제된다.</summary>
    public void Apply(AppTheme? theme)
    {
        lock (_gate) { _persisted = theme; _preview = null; PersistActiveName(); }
        OnChange?.Invoke();
    }

    /// <summary>이름으로 적용(내장/커스텀). "" 또는 미존재 → 기본.</summary>
    public void ApplyByName(string? name)
        => Apply(string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(t => t.Name == name));

    /// <summary>라이브 프리뷰(비영속) — 색상만 즉시 반영. 저장 전 미리보기용.</summary>
    public void Preview(ThemeColors colors)
    {
        lock (_gate) _preview = new AppTheme("(미리보기)", colors);
        OnChange?.Invoke();
    }

    /// <summary>프리뷰 취소 → 마지막 영속 활성으로 복원.</summary>
    public void ClearPreview()
    {
        lock (_gate) _preview = null;
        OnChange?.Invoke();
    }

    // ── 커스텀 저장/삭제 ──
    /// <summary>커스텀 테마 저장(동일 이름 덮어쓰기) 후 활성화. 실패해도 예외 없이 결과 반환.</summary>
    public AppTheme SaveCustom(string name, ThemeColors colors)
    {
        var safe = SanitizeName(name);
        var theme = new AppTheme(safe, Normalize(colors));
        lock (_gate)
        {
            _custom.RemoveAll(t => t.Name == safe);
            _custom.Add(theme);
            _persisted = theme; _preview = null;
            Save();
        }
        OnChange?.Invoke();
        return theme;
    }

    public void DeleteCustom(string name)
    {
        lock (_gate)
        {
            _custom.RemoveAll(t => t.Name == name);
            if (_persisted?.Name == name) _persisted = null;
            Save();
        }
        OnChange?.Invoke();
    }

    // ── 내보내기/불러오기 ──
    /// <summary>테마를 exports 폴더에 JSON 파일로 저장하고 경로 반환. 실패 시 null(Graceful).</summary>
    public string? ExportToFile(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(_exportDir);
            var file = Path.Combine(_exportDir, SanitizeFileName(theme.Name) + ".pfxtheme.json");
            File.WriteAllText(file, ExportJson(theme), new UTF8Encoding(false));
            return file;
        }
        catch (Exception ex) { Program.Log("ThemeService.Export", ex); return null; }
    }

    /// <summary>테마 1개를 JSON 문자열로 직렬화(내보내기/클립보드용).</summary>
    public string ExportJson(AppTheme theme)
        => JsonSerializer.Serialize(new ThemeDto(theme.Name, theme.Colors), PromptFlowJsonContext.Default.ThemeDto);

    /// <summary>JSON 문자열을 커스텀 테마로 불러와 저장·활성화. (ok, 메시지, 테마).</summary>
    public (bool Ok, string Message, AppTheme? Theme) ImportJson(string json)
    {
        try
        {
            var dto = JsonSerializer.Deserialize(json, PromptFlowJsonContext.Default.ThemeDto);
            if (dto?.Colors is null || string.IsNullOrWhiteSpace(dto.Name))
                return (false, "올바른 테마 파일이 아닙니다.", null);
            var theme = SaveCustom(dto.Name, dto.Colors);   // 검증·저장·활성
            return (true, $"'{theme.Name}' 테마를 불러왔습니다.", theme);
        }
        catch (Exception ex) { Program.Log("ThemeService.Import", ex); return (false, "불러오기 실패: " + ex.Message, null); }
    }

    // ── CSS 생성 ──
    /// <summary>테마 색상을 app.css 토큰으로 매핑한 :root 오버라이드(!important — data-theme 특이성 상회).</summary>
    public static string BuildCss(ThemeColors c)
    {
        var bg = SafeColor(c.Bg); var card = SafeColor(c.Card); var card2 = SafeColor(c.Card2); var line = SafeColor(c.Line);
        var tx = SafeColor(c.TextMain); var ts = SafeColor(c.TextSub); var tm = SafeColor(c.TextMuted);
        var ac = SafeColor(c.Accent); var ah = SafeColor(c.AccentHover); var a2 = SafeColor(c.Accent2);

        var sb = new StringBuilder(":root{");
        // 요구사항 시맨틱 별칭
        sb.Append($"--bg-primary:{bg}!important;--card-bg:{card}!important;--accent-color:{ac}!important;--accent-hover:{ah}!important;--text-main:{tx}!important;--line-color:{line}!important;");
        // 실제 앱 토큰 리매핑(컴포넌트가 실제로 참조)
        sb.Append($"--bg:{bg}!important;--canvas:{bg}!important;--card:{card}!important;--card-2:{card2}!important;");
        sb.Append($"--line:{line}!important;--hairline:{line}!important;");
        sb.Append($"--ink:{tx}!important;--sub:{ts}!important;");
        sb.Append($"--text-primary:{tx}!important;--text-secondary:{ts}!important;--text-muted:{tm}!important;");
        sb.Append($"--accent:{ac}!important;--accent-2:{a2}!important;");
        sb.Append($"--flow-violet:{ac}!important;--flow-cyan:{a2}!important;--flow-magenta:{ah}!important;");
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>CSS 색상 값 검증 — #hex / rgb()/hsl() / 이름만 허용. 그 외는 안전 폴백(#000)으로 대체(스타일 주입 차단).</summary>
    public static string SafeColor(string? v)
    {
        var s = (v ?? "").Trim();
        if (HexRx.IsMatch(s) || FuncRx.IsMatch(s) || NameRx.IsMatch(s)) return s;
        return "#000000";
    }

    // ── 영속 ──
    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var dto = JsonSerializer.Deserialize(File.ReadAllText(_path), PromptFlowJsonContext.Default.ThemeStoreDto);
            if (dto?.Custom is not null)
                foreach (var t in dto.Custom.Where(t => t?.Colors is not null && !string.IsNullOrWhiteSpace(t.Name)))
                    _custom.Add(new AppTheme(t!.Name!, Normalize(t.Colors!)));
            if (!string.IsNullOrWhiteSpace(dto?.ActiveName))
                _persisted = All.FirstOrDefault(t => t.Name == dto!.ActiveName);
        }
        catch (Exception ex) { Program.Log("ThemeService.Load", ex); /* 손상 → 기본 */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var dto = new StoreDto(_persisted?.Name, _custom.Select(t => new ThemeDto(t.Name, t.Colors)).ToList());
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, PromptFlowJsonContext.Default.ThemeStoreDto), new UTF8Encoding(false));
            File.Copy(tmp, _path, overwrite: true);
            try { File.Delete(tmp); } catch { /* 무시 */ }
        }
        catch (Exception ex) { Program.Log("ThemeService.Save", ex); }
    }

    private void PersistActiveName()
    {
        // 활성 이름만 바뀐 경우도 파일에 기록(커스텀 목록은 그대로).
        Save();
    }

    private static ThemeColors Normalize(ThemeColors c) => new(
        SafeColor(c.Bg), SafeColor(c.Card), SafeColor(c.Card2), SafeColor(c.Line),
        SafeColor(c.TextMain), SafeColor(c.TextSub), SafeColor(c.TextMuted),
        SafeColor(c.Accent), SafeColor(c.AccentHover), SafeColor(c.Accent2));

    private static string SanitizeName(string? n)
    {
        var s = (n ?? "").Trim();
        if (s.Length == 0) return "내 테마";
        return s.Length <= 40 ? s : s[..40];
    }

    private static string SanitizeFileName(string n)
    {
        foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(n) ? "theme" : n;
    }

    private static readonly Regex HexRx = new(@"^#[0-9a-fA-F]{3,8}$", RegexOptions.Compiled);
    private static readonly Regex FuncRx = new(@"^(rgb|rgba|hsl|hsla)\([0-9.,%\s/]+\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NameRx = new(@"^[a-zA-Z]{2,20}$", RegexOptions.Compiled);


    // 직렬화 DTO(레코드 그대로 저장 가능하나 명시 DTO 로 스키마 고정).
    internal sealed record ThemeDto(string? Name, ThemeColors? Colors);
    internal sealed record StoreDto(string? ActiveName, List<ThemeDto>? Custom);
}
