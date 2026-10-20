using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.8.0 커스텀 테마 컷(저작 단위).</summary>
public sealed class CustomThemeCut
{
    public string Shot { get; set; } = "";
    public string Voice { get; set; } = "";
    public string Gold { get; set; } = "";
    public int Tension { get; set; } = 50;
    public string Sfx { get; set; } = "";
}

/// <summary>v16.8.0 커스텀 테마 정의(.pftheme 항목 · 편집 모델).</summary>
public sealed class CustomThemeDefinition
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Emoji { get; set; } = "✨";
    public string CategoryKey { get; set; } = "everyday";
    public string Hook { get; set; } = "";
    public string Example { get; set; } = "";
    public double Speed { get; set; } = 1.0;
    public CameraMotion Camera { get; set; } = CameraMotion.SmoothPan;
    public ThemeLighting Lighting { get; set; } = ThemeLighting.NaturalDaylight;
    public List<string> Sfx { get; set; } = new();
    public int MotionIntensity { get; set; } = 3;
    public string Negative { get; set; } = "";
    public List<CustomThemeCut> Cuts { get; set; } = new();
}

/// <summary>v16.8.0 .pftheme 파일 봉투(단일/다중 테마 공용).</summary>
public sealed class PfThemeFile
{
    public string Format { get; set; } = CustomThemeRegistryService.FormatId;
    public int Version { get; set; } = CustomThemeRegistryService.FormatVersion;
    public List<CustomThemeDefinition> Themes { get; set; } = new();
}

/// <summary>v16.8.0 가져오기 결과 — 성공 키 · 이름 변경된 키 · 거부 사유.</summary>
public sealed record ThemeImportResult(IReadOnlyList<string> Imported, IReadOnlyList<string> Renamed, IReadOnlyList<string> Errors)
{
    public bool Ok => Imported.Count > 0 && Errors.Count == 0;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PfThemeFile))]
internal sealed partial class CustomThemeJsonContext : JsonSerializerContext;

/// <summary>
/// [PromptFlowStudio] v16.8.0 🛠️ 커스텀 테마 레지스트리 — 사용자 저작 테마를 <c>%LocalAppData%\PromptFlowStudio\Themes\</c> 에
///   테마당 1개 <c>.pftheme</c>(JSON)로 영구 저장하고 <see cref="BabyThemeDatabase"/> 에 "🛠️ 내 커스텀 테마" 카테고리로 동적 병합.
///   ▸ 병합된 테마는 내장 130종과 동일한 <see cref="BabyThemeDef"/> — 인스펙터 배지·스마트 추천·검색·<see cref="VideoFormatFactory"/> 공유.
///     사용자 지정 카메라/조명/SFX/강도/네거티브는 <see cref="ThemeCinematicsCatalog.Register"/> 로 산출값 대신 사용.
///   ▸ 가져오기: 형식/버전 검증 · 크기·개수·길이 상한 · 내장 키 충돌은 접미사로 재명명 · 문제 항목만 거부([부분 구제]).
///   ▸ 저장은 임시 파일 → 원자 교체(손상 방지). 디스크 오류는 예외 대신 메시지(Graceful). 순수 BCL · System.Text.Json 소스 생성.
/// </summary>
public sealed class CustomThemeRegistryService
{
    public const string FormatId = "pftheme", Extension = ".pftheme", CategoryKey = "custom";
    public const int FormatVersion = 1;
    public const int MaxFileBytes = 1024 * 1024, MaxThemesPerFile = 200, MaxCuts = 20, MinCuts = 1;
    private const int MaxText = 600, MaxShort = 80;

    private readonly BabyThemeDatabase _db;
    private readonly object _gate = new();
    private readonly Dictionary<string, CustomThemeDefinition> _themes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>저장 폴더(기본 %LocalAppData%\PromptFlowStudio\Themes).</summary>
    public string Root { get; }

    /// <summary>목록 변경 알림(저장·삭제·가져오기).</summary>
    public event Action? OnChange;

    public CustomThemeRegistryService(BabyThemeDatabase db) : this(db, null) { }

    public CustomThemeRegistryService(BabyThemeDatabase db, string? root)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        Root = string.IsNullOrWhiteSpace(root)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", "Themes")
            : root!;
        Reload();
    }

    /// <summary>저장된 커스텀 테마(키 오름차순 스냅숏).</summary>
    public IReadOnlyList<CustomThemeDefinition> Themes
    {
        get
        {
            lock (_gate)
            {
                var list = new List<CustomThemeDefinition>(_themes.Values);
                list.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                return list;
            }
        }
    }

    /// <summary>키로 커스텀 정의 조회(편집용 복제본).</summary>
    public CustomThemeDefinition? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        lock (_gate) return _themes.TryGetValue(key!, out var d) ? Clone(d) : null;
    }

    /// <summary>커스텀 키 여부.</summary>
    public bool IsCustom(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_gate) return _themes.ContainsKey(key!);
    }

    /// <summary>디스크에서 다시 읽어 병합(손상 파일은 건너뜀).</summary>
    public IReadOnlyList<string> Reload()
    {
        var errors = new List<string>();
        lock (_gate)
        {
            _themes.Clear();
            try
            {
                if (Directory.Exists(Root))
                    foreach (var f in Directory.EnumerateFiles(Root, "*" + Extension))
                    {
                        try
                        {
                            var file = Parse(File.ReadAllText(f, Encoding.UTF8), out var err);
                            if (file is null) { errors.Add(Path.GetFileName(f) + ": " + err); continue; }
                            foreach (var d in file.Themes)
                                if (Normalize(d, out var e) is { } n && !_db.IsBuiltInKey(n.Key)) _themes[n.Key] = n;
                                else if (e is not null) errors.Add(Path.GetFileName(f) + ": " + e);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(Path.GetFileName(f) + ": " + ex.Message); }
                    }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex.Message); }
            MergeLocked();
        }
        return errors;
    }

    /// <summary>테마 저장(신규/수정). 키가 비면 이름에서 생성. 성공 시 최종 키, 실패 시 null + 사유.</summary>
    public string? Save(CustomThemeDefinition definition, out string? error)
    {
        var n = Normalize(definition, out error);
        if (n is null) return null;
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(definition.Key) || _db.IsBuiltInKey(n.Key)) n.Key = UniqueKeyLocked(n.Key);
            if (!TryWriteLocked(n, out error)) return null;
            _themes[n.Key] = n;
            MergeLocked();
        }
        Notify();
        return n.Key;
    }

    /// <summary>테마 삭제(파일 포함).</summary>
    public bool Delete(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_gate)
        {
            if (!_themes.Remove(key!)) return false;
            try { var p = PathFor(key!); if (File.Exists(p)) File.Delete(p); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 목록에서는 제거 */ }
            MergeLocked();
        }
        Notify();
        return true;
    }

    /// <summary>.pftheme JSON 내보내기 — 키 목록(null/빈 = 전체). 단일·다중 동일 봉투.</summary>
    public string Export(IEnumerable<string>? keys = null)
    {
        var file = new PfThemeFile();
        lock (_gate)
        {
            if (keys is null) foreach (var d in _themes.Values) file.Themes.Add(Clone(d));
            else foreach (var k in keys) if (k is not null && _themes.TryGetValue(k, out var d)) file.Themes.Add(Clone(d));
        }
        file.Themes.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        return JsonSerializer.Serialize(file, CustomThemeJsonContext.Default.PfThemeFile);
    }

    /// <summary>.pftheme 파일로 내보내기(기본 위치 = Root\Exports). 성공 시 경로.</summary>
    public string? ExportToFile(IEnumerable<string>? keys, string? path, out string? error)
    {
        error = null;
        var json = Export(keys);
        try
        {
            path ??= Path.Combine(Root, "Exports", "themes_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + Extension);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            WriteAtomic(path, json);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = "내보내기 실패: " + ex.Message;
            return null;
        }
    }

    /// <summary>.pftheme JSON 가져오기 — 유효 항목만 저장·병합([부분 구제]), 내장 키 충돌·기존 커스텀 키는 재명명.</summary>
    public ThemeImportResult Import(string? json, bool overwriteExisting = false)
    {
        var imported = new List<string>();
        var renamed = new List<string>();
        var errors = new List<string>();
        if (json is not null && Encoding.UTF8.GetByteCount(json) > MaxFileBytes) { errors.Add("파일이 너무 큽니다(최대 1MB)."); return new(imported, renamed, errors); }
        var file = Parse(json, out var perr);
        if (file is null) { errors.Add(perr ?? "형식 오류"); return new(imported, renamed, errors); }

        lock (_gate)
        {
            foreach (var d in file.Themes)
            {
                var n = Normalize(d, out var e);
                if (n is null) { errors.Add((string.IsNullOrWhiteSpace(d?.Name) ? "(이름 없음)" : d!.Name) + ": " + e); continue; }
                var original = n.Key;
                if (_db.IsBuiltInKey(n.Key) || (!overwriteExisting && _themes.ContainsKey(n.Key))) n.Key = UniqueKeyLocked(n.Key);
                if (!TryWriteLocked(n, out var we)) { errors.Add(n.Name + ": " + we); continue; }
                _themes[n.Key] = n;
                imported.Add(n.Key);
                if (!string.Equals(original, n.Key, StringComparison.OrdinalIgnoreCase)) renamed.Add(original + "→" + n.Key);
            }
            if (imported.Count > 0) MergeLocked();
        }
        if (imported.Count > 0) Notify();
        return new(imported, renamed, errors);
    }

    /// <summary>스트림 가져오기(드래그 앤 드롭 · 1MB 상한).</summary>
    public async System.Threading.Tasks.Task<ThemeImportResult> ImportAsync(Stream stream, bool overwriteExisting = false, System.Threading.CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > MaxFileBytes)
                return new(Array.Empty<string>(), Array.Empty<string>(), new[] { "파일이 너무 큽니다(최대 1MB)." });
            ms.Write(buf, 0, read);
        }
        return Import(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length), overwriteExisting);
    }

    /// <summary>정의 → 백과사전 테마(컷 = 비트) 변환.</summary>
    public static BabyThemeDef ToThemeDef(CustomThemeDefinition d)
    {
        var beats = new BabyBeat[d.Cuts.Count];
        for (var i = 0; i < beats.Length; i++)
        {
            var c = d.Cuts[i];
            beats[i] = new BabyBeat(c.Voice, c.Gold, c.Shot, c.Tension, i == 0 ? "fade" : (i % 2 == 0 ? "hblur" : "slideleft"), c.Sfx);
        }
        var hook = d.Hook.Length > 0 ? d.Hook : d.Name;
        var example = d.Example.Length > 0 ? d.Example : "🛠️ 내가 만든 커스텀 테마";
        return new BabyThemeDef(d.Key, CategoryKey, d.Emoji, d.Name, hook, example, d.Speed, beats);
    }

    /// <summary>정의 → 사용자 지정 시네마틱 메타.</summary>
    public static ThemeCinematics ToCinematics(CustomThemeDefinition d)
    {
        string? lens = null;
        foreach (var c in d.Cuts) if ((lens = ThemeCinematicsCatalog.LensOf(c.Shot)) is not null) break;
        return new ThemeCinematics(d.Camera, d.Lighting, lens ?? "50mm 시네마틱", d.Sfx.ToArray(), d.MotionIntensity,
            d.Negative.Length > 0 ? PromptFlowStudio.Services.Wan.MotionVectorPromptEngine.AppendReverseGuard(d.Negative) : null);
    }

    // ══ 내부 ══

    private void MergeLocked()
    {
        if (_themes.Count == 0) { _db.SetCustomCategories(null); return; }
        var defs = new List<BabyThemeDef>(_themes.Count);
        foreach (var d in _themes.Values)
        {
            var t = ToThemeDef(d);
            ThemeCinematicsCatalog.Register(t, ToCinematics(d));
            defs.Add(t);
        }
        defs.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));
        _db.SetCustomCategories(new[] { new BabyCategory(CategoryKey, "🛠️", "내 커스텀 테마", "직접 저작한 테마 · .pftheme 공유", defs) });
    }

    private static PfThemeFile? Parse(string? json, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(json)) { error = "빈 파일입니다."; return null; }
        PfThemeFile? file;
        try { file = JsonSerializer.Deserialize(json, CustomThemeJsonContext.Default.PfThemeFile); }
        catch (JsonException ex) { error = "JSON 파싱 실패: " + ex.Message; return null; }
        if (file is null) { error = "빈 문서입니다."; return null; }
        if (!string.Equals(file.Format, FormatId, StringComparison.OrdinalIgnoreCase)) { error = "pftheme 형식이 아닙니다."; return null; }
        if (file.Version < 1 || file.Version > FormatVersion) { error = $"지원하지 않는 버전입니다(v{file.Version})."; return null; }
        file.Themes ??= new();
        if (file.Themes.Count == 0) { error = "테마가 없습니다."; return null; }
        if (file.Themes.Count > MaxThemesPerFile) { error = $"테마가 너무 많습니다(최대 {MaxThemesPerFile})."; return null; }
        return file;
    }

    /// <summary>검증 + 정규화(길이 상한·범위 클램프·enum 검증) — 원본은 건드리지 않는 복제본 반환.</summary>
    internal static CustomThemeDefinition? Normalize(CustomThemeDefinition? d, out string? error)
    {
        error = null;
        if (d is null) { error = "항목이 비어 있습니다."; return null; }
        var name = Cap(d.Name, MaxShort);
        if (name.Length == 0) { error = "테마 이름이 필요합니다."; return null; }
        var cuts = d.Cuts ?? new();
        var outCuts = new List<CustomThemeCut>(Math.Min(cuts.Count, MaxCuts));
        foreach (var c in cuts)
        {
            if (c is null || string.IsNullOrWhiteSpace(c.Shot)) continue;
            if (outCuts.Count == MaxCuts) break;
            outCuts.Add(new CustomThemeCut
            {
                Shot = Cap(c.Shot, MaxText), Voice = Cap(c.Voice, MaxShort), Gold = Cap(c.Gold, MaxShort),
                Tension = Math.Clamp(c.Tension, 0, 100), Sfx = SanitizeSfxTokens(c.Sfx),
            });
        }
        if (outCuts.Count < MinCuts) { error = "장면 묘사가 있는 컷이 최소 1개 필요합니다."; return null; }
        if (!Enum.IsDefined(d.Camera) || !Enum.IsDefined(d.Lighting)) { error = "카메라/조명 값이 올바르지 않습니다."; return null; }

        var sfx = new List<string>(3);
        foreach (var s in d.Sfx ?? new())
        {
            var t = SanitizeToken(s);
            if (t.Length > 0 && !sfx.Contains(t)) sfx.Add(t);
            if (sfx.Count == 3) break;
        }
        var speed = double.IsFinite(d.Speed) ? Math.Clamp(d.Speed, 0.5, 2.0) : 1.0;
        var emoji = Cap(d.Emoji, 8);
        var key = SanitizeKey(string.IsNullOrWhiteSpace(d.Key) ? name : d.Key);
        return new CustomThemeDefinition
        {
            Key = key, Name = name, Emoji = emoji.Length == 0 ? "✨" : emoji, CategoryKey = SanitizeToken(d.CategoryKey) is { Length: > 0 } ck ? ck : "everyday",
            Hook = Cap(d.Hook, MaxText), Example = Cap(d.Example, MaxText), Speed = speed,
            Camera = d.Camera, Lighting = d.Lighting, Sfx = sfx,
            MotionIntensity = Math.Clamp(d.MotionIntensity, ThemeCinematics.MinIntensity, ThemeCinematics.MaxIntensity),
            Negative = Cap(d.Negative, 500), Cuts = outCuts,
        };
    }

    private string UniqueKeyLocked(string baseKey)
    {
        var k = baseKey;
        for (var i = 2; _db.IsBuiltInKey(k) || _themes.ContainsKey(k); i++) k = baseKey + "_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return k;
    }

    private bool TryWriteLocked(CustomThemeDefinition d, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(Root);
            var file = new PfThemeFile { Themes = { d } };
            WriteAtomic(PathFor(d.Key), JsonSerializer.Serialize(file, CustomThemeJsonContext.Default.PfThemeFile));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "저장 실패: " + ex.Message;
            return false;
        }
    }

    private static void WriteAtomic(string path, string json)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    private string PathFor(string key) => Path.Combine(Root, key + Extension);

    private void Notify()
    {
        try { OnChange?.Invoke(); } catch (Exception ex) { Program.Log("CustomThemeRegistry.OnChange", ex); }   // 구독자 예외 격리
    }

    /// <summary>키 정규화 — 영숫자/밑줄만, 소문자, "ct_" 접두(내장 키와 구분 · 경로 조작 차단).</summary>
    internal static string SanitizeKey(string? raw)
    {
        var sb = new StringBuilder("ct_");
        foreach (var ch in raw ?? "")
        {
            if (sb.Length >= 40) break;
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if ((ch == '_' || ch == '-' || ch == ' ') && sb[^1] != '_') sb.Append('_');
        }
        var s = sb.ToString().TrimEnd('_');
        if (s.StartsWith("ct_ct_", StringComparison.Ordinal)) s = s[3..];
        // 한글 등 비ASCII 이름 → 해시 접미사로 고유 키.
        return s.Length > 3 ? s : "ct_" + ((uint)StableHash(raw ?? "")).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var ch in s) h = (h ^ ch) * 16777619;
            return h;
        }
    }

    private static string SanitizeToken(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, 24));
        foreach (var ch in s.Trim())
        {
            if (sb.Length >= 24) break;
            if (char.IsAsciiLetterOrDigit(ch) || ch == '_') sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    // 폴리 토큰 "type" 또는 "type@pan" 콤마 목록 — 허용 문자만 통과(최대 6개).
    private static string SanitizeSfxTokens(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        var count = 0;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (count == 6) break;
            var at = part.IndexOf('@');
            var type = SanitizeToken(at < 0 ? part : part[..at]);
            if (type.Length == 0) continue;
            var pan = at < 0 ? "" : part[(at + 1)..].Trim();
            if (pan.Length > 0 && (pan.Length > 4 || !(pan == "C" || ((pan[0] == 'L' || pan[0] == 'R') && int.TryParse(pan.AsSpan(1), out _))))) pan = "";
            if (sb.Length > 0) sb.Append(',');
            sb.Append(type);
            if (pan.Length > 0) sb.Append('@').Append(pan);
            count++;
        }
        return sb.ToString();
    }

    private static string Cap(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[..max];
    }

    private static CustomThemeDefinition Clone(CustomThemeDefinition d) => new()
    {
        Key = d.Key, Name = d.Name, Emoji = d.Emoji, CategoryKey = d.CategoryKey, Hook = d.Hook, Example = d.Example, Speed = d.Speed,
        Camera = d.Camera, Lighting = d.Lighting, Sfx = new List<string>(d.Sfx), MotionIntensity = d.MotionIntensity, Negative = d.Negative,
        Cuts = d.Cuts.ConvertAll(static c => new CustomThemeCut { Shot = c.Shot, Voice = c.Voice, Gold = c.Gold, Tension = c.Tension, Sfx = c.Sfx }),
    };
}
