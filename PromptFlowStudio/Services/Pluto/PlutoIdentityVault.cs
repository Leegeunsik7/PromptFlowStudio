using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>🍼 Pluto 아이덴티티 프로필 — 아기/반려동물 이름 · 출생일 · 외모 특징 · 애착 소품(캐릭터 일관성 앵커 원천).</summary>
public sealed class PlutoIdentityProfile
{
    public const string BabyCategory = "Baby";
    public const string PetCategory = "Pet";
    public const int MaxNameLength = 20;
    public const int MaxFeaturesLength = 200;
    public const int MaxItemLength = 60;

    public string Id { get; set; } = "";
    public string SubjectName { get; set; } = "";
    public DateTime BirthDate { get; set; } = DateTime.Today;
    /// <summary>"Baby" | "Pet".</summary>
    public string Category { get; set; } = BabyCategory;
    /// <summary>선택 — "female" | "male" | "" (미지정 시 앵커에서 생략).</summary>
    public string Gender { get; set; } = "";
    public string VisualFeatures { get; set; } = "";
    public string FavoriteItem { get; set; } = "";
    public bool IsActive { get; set; } = true;

    [JsonIgnore] public bool IsPet => string.Equals(Category, PetCategory, StringComparison.OrdinalIgnoreCase);

    /// <summary>📅 오늘 기준 D-Day(최소 1 — 미래 출생일/태명 포함).</summary>
    [JsonIgnore] public int CurrentDDay => DDayOn(DateTime.Today);

    public int DDayOn(DateTime today) => Math.Max(1, (int)(today.Date - BirthDate.Date).TotalDays);

    /// <summary>🏷️ 캔버스 성장 뱃지 — "🍼 하윤이 · D+128" / "🐾 콩이 · D+40".</summary>
    public string FormatBadge(DateTime today) =>
        (IsPet ? "🐾 " : "🍼 ") + Clean(SubjectName, MaxNameLength) + " · D+" + DDayOn(today).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// 🧬 캐릭터 일관성 앵커 — "same consistent baby, female, {VisualFeatures}, holding {FavoriteItem}".
    /// 빈 항목은 생략 · 개행/쉼표 연속 정리 · 길이 상한.
    /// </summary>
    public string GetConsistencyAnchor()
    {
        var sb = new StringBuilder(160);
        sb.Append("same consistent ").Append(IsPet ? "pet" : "baby");
        var g = Clean(Gender, 12);
        if (g.Length > 0) sb.Append(", ").Append(g);
        var f = Clean(VisualFeatures, MaxFeaturesLength);
        if (f.Length > 0) sb.Append(", ").Append(f);
        var item = Clean(FavoriteItem, MaxItemLength);
        if (item.Length > 0) sb.Append(IsPet ? ", with " : ", holding ").Append(item);
        return sb.ToString();
    }

    public PlutoIdentityProfile Clone() => (PlutoIdentityProfile)MemberwiseClone();

    /// <summary>개행·탭 → 공백, 연속 공백 1개, 양끝 쉼표/공백 제거, 상한 절단.</summary>
    internal static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(ch);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim(' ', ',', ';');
    }
}

/// <summary>
/// 🍼 Pluto Identity &amp; Persona Vault — 아기/반려동물 프로필을 <c>pluto_identity_vault.json</c>(LocalAppData) 에 영속.
/// 활성 프로필 1개 유지 · 원자적 저장(tmp → move) · 손상 파일은 빈 볼트로 재시작 · 저장 실패 시 메모리 상태 유지([부분 구제]).
/// </summary>
public sealed class PlutoIdentityVault : IDisposable
{
    public const string FileName = "pluto_identity_vault.json";

    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private List<PlutoIdentityProfile> _profiles = new();
    private volatile bool _loaded;
    private int _disposed;

    public PlutoIdentityVault() : this(null) { }

    public PlutoIdentityVault(string? storagePath)
    {
        _path = string.IsNullOrWhiteSpace(storagePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", FileName)
            : storagePath;
    }

    public string StoragePath => _path;

    /// <summary>상태 변경 통지(백그라운드 스레드 가능).</summary>
    public event Action? Changed;

    /// <summary>🍼 메모리 캐시된 활성 프로필 사본(미적재/없음 → null). 동기 핫패스용.</summary>
    public PlutoIdentityProfile? ActiveProfile
    {
        get
        {
            lock (_sync)
                foreach (var p in _profiles)
                    if (p.IsActive) return p.Clone();
            return null;
        }
    }

    public IReadOnlyList<PlutoIdentityProfile> Profiles
    {
        get
        {
            lock (_sync)
            {
                var list = new List<PlutoIdentityProfile>(_profiles.Count);
                foreach (var p in _profiles) list.Add(p.Clone());
                return list;
            }
        }
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            List<PlutoIdentityProfile>? data = null;
            try
            {
                if (File.Exists(_path))
                {
                    await using var fs = File.OpenRead(_path);
                    data = await JsonSerializer.DeserializeAsync<List<PlutoIdentityProfile>>(fs, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("[Pluto.Identity] load failed: " + ex.Message);   // 손상 파일 → 빈 볼트로 재시작
            }
            lock (_sync)
            {
                _profiles = new List<PlutoIdentityProfile>();
                if (data is not null)
                    foreach (var p in data)
                        if (p is not null && !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.SubjectName))
                            _profiles.Add(Normalize(p));
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<PlutoIdentityProfile?> GetActiveProfileAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return ActiveProfile;
    }

    /// <summary>프로필 업서트 — Id 미지정 시 Guid 발급 · 활성 저장 시 다른 프로필 비활성 · 즉시 원자적 영속.</summary>
    public async Task SaveProfileAsync(PlutoIdentityProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.SubjectName)) throw new ArgumentException("SubjectName required", nameof(profile));
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(profile.Id)) profile.Id = Guid.NewGuid().ToString("N");
        var copy = Normalize(profile.Clone());
        string json;
        lock (_sync)
        {
            if (copy.IsActive)
                foreach (var p in _profiles) p.IsActive = false;
            var idx = _profiles.FindIndex(p => string.Equals(p.Id, copy.Id, StringComparison.Ordinal));
            if (idx >= 0) _profiles[idx] = copy; else _profiles.Add(copy);
            json = JsonSerializer.Serialize(_profiles);
        }
        await PersistAsync(json, ct).ConfigureAwait(false);
        RaiseChanged();
    }

    public async Task DeleteProfileAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        string json;
        lock (_sync)
        {
            if (_profiles.RemoveAll(p => string.Equals(p.Id, id, StringComparison.Ordinal)) == 0) return;
            json = JsonSerializer.Serialize(_profiles);
        }
        await PersistAsync(json, ct).ConfigureAwait(false);
        RaiseChanged();
    }

    /// <summary>🧬 테마와 프로필 구분 일치 시 앵커(Baby ↔ 육아, Pet ↔ 반려동물), 아니면 빈 문자열.</summary>
    public static string AnchorFor(PlutoIdentityProfile? profile, StudioThemeCategory category)
    {
        if (profile is null) return "";
        var match = profile.IsPet ? category == StudioThemeCategory.PetsCompanions : category == StudioThemeCategory.BabyParenting;
        return match ? profile.GetConsistencyAnchor() : "";
    }

    /// <summary>🧬 프롬프트 선두에 앵커 삽입(멱등 — 이미 포함 시 원본 유지).</summary>
    public static string ApplyAnchor(string? prompt, string? anchor)
    {
        var p = prompt ?? "";
        if (string.IsNullOrWhiteSpace(anchor)) return p;
        if (p.Contains(anchor, StringComparison.OrdinalIgnoreCase)) return p;
        return p.Length == 0 ? anchor : anchor + ", " + p.TrimStart();
    }

    /// <summary>🏷️ 활성 프로필 뱃지 — 사용자 문구가 있으면 "🍼 이름 · 문구", 없으면 "🍼 이름 · D+N". 프로필 없음 → null(기존 성장 단계 뱃지 사용).</summary>
    public static string? BadgeFor(PlutoIdentityProfile? profile, string? customText, DateTime today)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.SubjectName)) return null;
        if (string.IsNullOrWhiteSpace(customText)) return profile.FormatBadge(today);
        return (profile.IsPet ? "🐾 " : "🍼 ") + PlutoIdentityProfile.Clean(profile.SubjectName, PlutoIdentityProfile.MaxNameLength) + " · " + customText.Trim();
    }

    private static PlutoIdentityProfile Normalize(PlutoIdentityProfile p)
    {
        p.SubjectName = PlutoIdentityProfile.Clean(p.SubjectName, PlutoIdentityProfile.MaxNameLength);
        p.Category = p.IsPet ? PlutoIdentityProfile.PetCategory : PlutoIdentityProfile.BabyCategory;
        p.Gender = PlutoIdentityProfile.Clean(p.Gender, 12);
        p.VisualFeatures = PlutoIdentityProfile.Clean(p.VisualFeatures, PlutoIdentityProfile.MaxFeaturesLength);
        p.FavoriteItem = PlutoIdentityProfile.Clean(p.FavoriteItem, PlutoIdentityProfile.MaxItemLength);
        p.BirthDate = p.BirthDate.Date;
        return p;
    }

    private async Task PersistAsync(string json, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.Identity] persist failed: " + ex.Message);   // [부분 구제] 메모리 내 프로필 유지
        }
        finally { _gate.Release(); }
    }

    private void RaiseChanged()
    {
        var h = Changed;
        if (h is null) return;
        foreach (Action a in h.GetInvocationList())
        {
            try { a(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.Identity] Changed handler: " + ex.Message); }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Changed = null;
        _gate.Dispose();
    }
}
