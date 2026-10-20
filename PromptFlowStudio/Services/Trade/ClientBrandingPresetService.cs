using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using PromptFlowStudio.Services.Account;      // LicenseService.DataAreaRoot
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Trade;

/// <summary>고객사 브랜드 프로필 — 로고·브랜드 네온·엠보싱 깊이(0~5)·하단 자막 색상.</summary>
public sealed record ClientBrandProfile(string Id, string Name, string LogoPath, string NeonColor, int EmbossDepth, string SubtitleColor);

/// <summary>브랜드 적용 결과.</summary>
public sealed record BrandApplyResult(bool Ok, string Summary);

/// <summary>
/// [PromptFlowStudio] B2B 멀티 클라이언트 다이내믹 브랜딩 스위처(v3.3.0 · 관찰 가능 싱글턴 · 순수 BCL · 신규 스키마 0).
///   ▸ 고객사별 가이드라인(로고·브랜드 네온·3D 엠보싱 깊이·자막 색)을 <b>프리셋 프로필</b>로 등록·영속(DataAreaRoot/client_brands.json).
///   ▸ 프로필 선택 시 project.json <b>기존 필드</b>(WatermarkImagePath·WireframeNeonColor·EmbossWatermark)로 <b>1초 즉시 교체</b>(<see cref="ApplyToProject"/>) — 스키마 변경 없음.
///   ▸ 자막 색은 프로필에 보관해 렌더 시 참조(어드바이저리). 미등록 시 대표 3사 샘플 프로필 시드.
/// </summary>
public sealed class ClientBrandingPresetService
{
    private static readonly string[] Neons = { "cyan", "magenta", "gold", "emerald" };

    private readonly object _lock = new();
    private readonly List<ClientBrandProfile> _profiles = new();

    /// <summary>프로필 변경 방송.</summary>
    public event Action? OnChange;

    public ClientBrandingPresetService() { TryLoad(); if (_profiles.Count == 0) SeedDefaults(); }

    private string StorePath => Path.Combine(LicenseService.DataAreaRoot(), "client_brands.json");

    /// <summary>등록된 프로필 목록.</summary>
    public IReadOnlyList<ClientBrandProfile> Profiles { get { lock (_lock) return _profiles.ToList(); } }

    /// <summary>ID 로 프로필 조회(없으면 null).</summary>
    public ClientBrandProfile? Get(string? id) { if (string.IsNullOrWhiteSpace(id)) return null; lock (_lock) return _profiles.FirstOrDefault(p => p.Id == id); }

    /// <summary>프로필 등록/갱신(ID 없으면 신규 발급 · 네온/깊이 정규화). 반환: 저장된 프로필.</summary>
    public ClientBrandProfile Save(ClientBrandProfile profile)
    {
        var norm = Normalize(profile);
        lock (_lock)
        {
            var idx = _profiles.FindIndex(p => p.Id == norm.Id);
            if (idx >= 0) _profiles[idx] = norm; else _profiles.Add(norm);
        }
        TrySave(); OnChange?.Invoke();
        return norm;
    }

    /// <summary>프로필 삭제.</summary>
    public bool Remove(string id)
    {
        bool removed;
        lock (_lock) removed = _profiles.RemoveAll(p => p.Id == id) > 0;
        if (removed) { TrySave(); OnChange?.Invoke(); }
        return removed;
    }

    /// <summary>프로필을 프로젝트에 즉시 적용한다(기존 필드만 갱신 · 신규 스키마 없음).</summary>
    public BrandApplyResult ApplyToProject(StoryboardProject project, ClientBrandProfile profile)
    {
        if (project is null || profile is null) return new(false, "대상이 없습니다.");
        if (!string.IsNullOrWhiteSpace(profile.LogoPath)) project.WatermarkImagePath = profile.LogoPath;
        project.WireframeNeonColor = Neons.Contains((profile.NeonColor ?? "").ToLowerInvariant()) ? profile.NeonColor!.ToLowerInvariant() : "cyan";
        project.EmbossWatermark = profile.EmbossDepth > 0;
        return new(true, $"브랜드 적용 · {profile.Name} · 네온 {project.WireframeNeonColor} · 엠보싱 {(profile.EmbossDepth > 0 ? $"깊이 {profile.EmbossDepth}" : "off")} · 자막 {profile.SubtitleColor}");
    }

    /// <summary>프로필 정규화(ID 발급 · 네온/깊이/색 클램프).</summary>
    public static ClientBrandProfile Normalize(ClientBrandProfile p)
    {
        var id = string.IsNullOrWhiteSpace(p.Id) ? "brand_" + Guid.NewGuid().ToString("N")[..6] : p.Id.Trim();
        var neon = Neons.Contains((p.NeonColor ?? "").ToLowerInvariant()) ? p.NeonColor!.ToLowerInvariant() : "cyan";
        var depth = Math.Clamp(p.EmbossDepth, 0, 5);
        var sub = string.IsNullOrWhiteSpace(p.SubtitleColor) ? "#FFFFFF" : p.SubtitleColor.Trim();
        return new ClientBrandProfile(id, string.IsNullOrWhiteSpace(p.Name) ? id : p.Name.Trim(), p.LogoPath ?? "", neon, depth, sub);
    }

    private void SeedDefaults()
    {
        lock (_lock)
        {
            _profiles.Add(new ClientBrandProfile("brand_default", "기본(중립)", "", "cyan", 2, "#FFFFFF"));
            _profiles.Add(new ClientBrandProfile("brand_mobility", "모빌리티 블루", "", "cyan", 3, "#E6EDFF"));
            _profiles.Add(new ClientBrandProfile("brand_premium", "프리미엄 골드", "", "gold", 4, "#F5C451"));
        }
        TrySave();
    }

    private void TrySave()
    {
        try { List<ClientBrandProfile> snap; lock (_lock) snap = _profiles.ToList(); File.WriteAllText(StorePath, JsonSerializer.Serialize(snap, PromptFlowJsonContext.Default.ClientBrandProfileList), new UTF8Encoding(false)); }
        catch (Exception ex) { Program.Log("ClientBrand.Save", ex); }
    }

    private void TryLoad()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize(File.ReadAllText(StorePath), PromptFlowJsonContext.Default.ClientBrandProfileList);
            if (list is not null) lock (_lock) { _profiles.Clear(); _profiles.AddRange(list.Select(Normalize)); }
        }
        catch (Exception ex) { Program.Log("ClientBrand.Load", ex); }
    }
}
