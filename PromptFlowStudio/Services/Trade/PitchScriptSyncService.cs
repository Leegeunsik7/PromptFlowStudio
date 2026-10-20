using System;
using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Trade;

/// <summary>피치 모드 조정 옵션 — 와이어프레임 네온·더빙 언어·3색 조명·HUD 스타일·버추얼 무대.</summary>
public sealed record PitchOptions(
    string NeonColor, IReadOnlyList<string> DubLanguages, bool MultiLighting, string HudStyle, string VirtualStudioTemplate);

/// <summary>승인 반영 결과.</summary>
public sealed record PitchApplyResult(bool Ok, string Summary);

/// <summary>
/// [PromptFlowStudio] 인터랙티브 피치 옵션 ↔ project.json 양방향 동기화기(v3.2.0) — 순수 C# BCL(외부 라이브러리 0 · 신규 스키마 0).
///   ▸ 피치 모드에서 바이어 요구에 맞춰 조정한 <b>네온 컬러(cyan/magenta/gold)</b>·<b>더빙 타깃 언어</b>·<b>3색 조명</b>·HUD·버추얼 무대를
///     승인(Approve) 클릭 시 기존 스토리보드 필드로 <b>즉시 역반영</b>한다(<see cref="ApplyToProject"/>).
///   ▸ 반대로 현재 project.json 상태를 피치 UI 초기값으로 로드(<see cref="ReadFromProject"/>) → 왕복 무손실. 신규 필드 없이 v3.0/v2.x 필드 재사용(100% 역호환).
/// </summary>
public sealed class PitchScriptSyncService
{
    /// <summary>지원 네온 컬러.</summary>
    public static readonly IReadOnlyList<string> NeonColors = new[] { "cyan", "magenta", "gold" };
    /// <summary>지원 더빙 언어(코드).</summary>
    public static readonly IReadOnlyList<string> DubCodes = new[] { "en", "de", "zh", "ja" };

    /// <summary>현재 프로젝트 상태를 피치 옵션으로 읽는다.</summary>
    public PitchOptions ReadFromProject(StoryboardProject p)
    {
        var neonRaw = (p.WireframeNeonColor ?? "").ToLowerInvariant();
        var neon = NeonColors.Contains(neonRaw) ? neonRaw : "cyan";
        return new PitchOptions(neon, p.SelectedDubbingLanguages.ToList(), p.MultiLighting, p.TelemetryHudStyle ?? "", p.VirtualStudioTemplate ?? "");
    }

    /// <summary>피치 옵션을 프로젝트에 반영한다(승인). 기존 필드만 갱신 · 신규 스키마 없음.</summary>
    public PitchApplyResult ApplyToProject(StoryboardProject p, PitchOptions o)
    {
        if (p is null || o is null) return new(false, "대상이 없습니다.");

        p.WireframeNeonColor = NeonColors.Contains((o.NeonColor ?? "").ToLowerInvariant()) ? o.NeonColor!.ToLowerInvariant() : "cyan";

        var codes = (o.DubLanguages ?? Array.Empty<string>())
            .Select(c => (c ?? "").Trim().ToLowerInvariant()).Where(c => DubCodes.Contains(c)).Distinct().ToList();
        p.DubbingLanguages.Clear();
        foreach (var c in codes) p.DubbingLanguages.Add(c);

        p.MultiLighting = o.MultiLighting;
        p.TelemetryHudStyle = o.HudStyle ?? "";
        p.VirtualStudioTemplate = o.VirtualStudioTemplate ?? "";

        var langLabel = codes.Count > 0 ? string.Join("/", codes.Select(c => c.ToUpperInvariant())) : "없음";
        var lightLabel = o.MultiLighting ? "3색(주간/노을/야경)" : "단일";
        return new(true, $"승인 반영 · 네온 {p.WireframeNeonColor} · 더빙 {langLabel} · 조명 {lightLabel} · HUD {(string.IsNullOrEmpty(p.TelemetryHudStyle) ? "off" : p.TelemetryHudStyle)}");
    }

    /// <summary>더빙 언어 토글(불변 옵션 반환 · UI 상태 갱신용).</summary>
    public static PitchOptions ToggleDub(PitchOptions o, string code)
    {
        var set = o.DubLanguages.ToList();
        code = (code ?? "").ToLowerInvariant();
        if (set.Contains(code)) set.Remove(code); else if (DubCodes.Contains(code)) set.Add(code);
        return o with { DubLanguages = set };
    }
}
