using System;
using System.Collections.Generic;
using System.Linq;
using PromptFlowStudio.Services.Storyboard;   // StoryboardProject

namespace PromptFlowStudio.Services.Marketing;

/// <summary>국가별 수주 전환 예측 점수.</summary>
public sealed record CountryScore(string Country, string Flag, int Score);

/// <summary>A/B 변종 — 키·이름·이모지·스타일 설명·국가별 점수·종합 점수.</summary>
public sealed record AbVariant(string Key, string Name, string Emoji, string StyleDesc, IReadOnlyList<CountryScore> Scores, int OverallScore);

/// <summary>A/B 테스트 리포트 — 3 변종·최적 변종·요약.</summary>
public sealed record AbReport(IReadOnlyList<AbVariant> Variants, string BestVariantKey, string Summary);

/// <summary>
/// [PromptFlowStudio] 글로벌 수주 A/B 테스팅 &amp; 반응 예측 분석기(v3.5.0 · 순수 BCL 정량 휴리스틱 · 외부 ML 라이브러리 0).
///   ▸ 동일 프로젝트에서 [A 시네마틱 드론 · B 3D CAD 와이어프레임 · C 텔레메트리 HUD] 3종 변종을 자동 분기(<see cref="ApplyVariant"/>).
///   ▸ <b>스타일×바이어 국가 친화도 매트릭스</b> + 프로젝트 신호(CAD/HUD 유무)로 국가별 <b>수주 전환 예측 점수</b>를 산출(<see cref="Analyze"/>).
///   ▸ 국가별 선호(예: 독일=기술/CAD · 미국/일본=시네마틱 · 중국=데이터/HUD)를 반영해 최적 변종을 추천(결정론적 · 오프라인).
/// </summary>
public sealed class AbTestingAnalyticsEngine
{
    /// <summary>3종 변종 정의.</summary>
    public static readonly IReadOnlyList<(string Key, string Name, string Emoji, string Style)> Variants = new[]
    {
        ("cinematic", "시네마틱 드론", "🎥", "웅장한 항공 드론 + 골든아워 시네마틱"),
        ("cad_wireframe", "3D CAD 와이어프레임", "🧊", "장비 3D 홀로그램 와이어프레임 회전"),
        ("telemetry_hud", "텔레메트리 HUD", "📊", "실측 계기판 HUD + PASS/FAIL 데이터"),
    };

    /// <summary>타깃 국가(기본).</summary>
    public static readonly IReadOnlyList<(string Country, string Flag)> Countries = new[]
    {
        ("미국", "🇺🇸"), ("독일", "🇩🇪"), ("중국", "🇨🇳"), ("일본", "🇯🇵"),
    };

    // 스타일 × 국가 기본 친화도(0~100).
    private static readonly Dictionary<string, Dictionary<string, int>> Affinity = new()
    {
        ["cinematic"] = new() { ["미국"] = 82, ["독일"] = 62, ["중국"] = 60, ["일본"] = 80 },
        ["cad_wireframe"] = new() { ["미국"] = 60, ["독일"] = 85, ["중국"] = 68, ["일본"] = 72 },
        ["telemetry_hud"] = new() { ["미국"] = 70, ["독일"] = 74, ["중국"] = 86, ["일본"] = 64 },
    };

    /// <summary>프로젝트 신호를 반영해 3 변종의 국가별 전환 예측을 산출한다.</summary>
    public AbReport Analyze(StoryboardProject project, IReadOnlyList<(string Country, string Flag)>? countries = null)
    {
        var cs = countries ?? Countries;
        var hasCad = !string.IsNullOrWhiteSpace(project?.CadModelPath);
        var hasHud = !string.IsNullOrWhiteSpace(project?.TelemetryHudStyle);

        var variants = new List<AbVariant>();
        foreach (var (key, name, emoji, style) in Variants)
        {
            var scores = new List<CountryScore>();
            foreach (var (country, flag) in cs)
            {
                var baseScore = Affinity.TryGetValue(key, out var row) && row.TryGetValue(country, out var v) ? v : 55;
                var boost = SignalBoost(key, hasCad, hasHud);
                scores.Add(new CountryScore(country, flag, Math.Clamp(baseScore + boost, 0, 100)));
            }
            var overall = (int)Math.Round(scores.Average(s => s.Score));
            variants.Add(new AbVariant(key, name, emoji, style, scores, overall));
        }
        var best = variants.OrderByDescending(v => v.OverallScore).First();
        return new AbReport(variants, best.Key, $"최적 변종: {best.Emoji} {best.Name}(종합 {best.OverallScore}점) · 국가별 전환 예측 완료");
    }

    /// <summary>변종 스타일을 프로젝트에 적용한다(기존 필드만 · 신규 스키마 없음).</summary>
    public string ApplyVariant(StoryboardProject project, string variantKey)
    {
        if (project is null) return "대상 없음";
        switch (variantKey)
        {
            case "cinematic":
                project.TelemetryHudStyle = ""; project.VirtualStudioTemplate = "";
                return "A 시네마틱 드론 스타일 적용";
            case "cad_wireframe":
                project.WireframeNeonColor = string.IsNullOrWhiteSpace(project.WireframeNeonColor) ? "cyan" : project.WireframeNeonColor;
                return "B 3D CAD 와이어프레임 스타일 적용" + (string.IsNullOrWhiteSpace(project.CadModelPath) ? " (STL 등록 필요)" : "");
            case "telemetry_hud":
                if (string.IsNullOrWhiteSpace(project.TelemetryHudStyle)) project.TelemetryHudStyle = "gauge";
                return "C 텔레메트리 HUD 스타일 적용";
            default: return "알 수 없는 변종";
        }
    }

    /// <summary>신호 기반 가산점(순수 · 테스트 대상) — CAD 보유 시 와이어프레임, HUD 설정 시 HUD 변종 가산.</summary>
    public static int SignalBoost(string variantKey, bool hasCad, bool hasHud) => variantKey switch
    {
        "cad_wireframe" => hasCad ? 8 : 0,
        "telemetry_hud" => hasHud ? 8 : 0,
        "cinematic" => 3,   // 시네마틱은 기본 보편 가산
        _ => 0,
    };
}
