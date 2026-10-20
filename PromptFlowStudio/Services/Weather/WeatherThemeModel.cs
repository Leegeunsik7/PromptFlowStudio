using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlowStudio.Services.Weather;

/// <summary>기후 — 맑음/비/눈/안개.</summary>
public enum WeatherClimate { Clear, Rain, Snow, Fog }

/// <summary>시간대 — 새벽/정오/노을/밤.</summary>
public enum WeatherTimeOfDay { Dawn, Noon, Sunset, Night }

/// <summary>
/// [PromptFlowStudio] v4.5.0 날씨/환경 테마 구성 DTO — 프로젝트(project.json)에 임베드되는 기상 연출 설정.
///   ▸ 모든 필드에 기본값이 있어 구 <c>project.json</c>(필드 부재)은 기본값(비활성)으로 로드된다 → 100% 역호환.
///   ▸ <see cref="Intensity"/>(1~100)는 세터에서 자동 클램프 → 역직렬화된 이상값(0/음수/200)도 안전.
///   ⚠ mutable class(프로젝트가 편집하는 살아있는 값) — StoryboardScene 과 동일한 방침.
/// </summary>
public sealed class WeatherThemeConfig
{
    /// <summary>날씨 연출 활성 여부. 기본 false → 구 JSON 은 날씨 미적용(역호환).</summary>
    public bool Enabled { get; set; }

    /// <summary>기후(기본 맑음 = enum 0 · 역호환 안전).</summary>
    public WeatherClimate Climate { get; set; } = WeatherClimate.Clear;

    /// <summary>시간대(기본 정오).</summary>
    public WeatherTimeOfDay TimeOfDay { get; set; } = WeatherTimeOfDay.Noon;

    private int _intensity = 50;
    /// <summary>기상 강도(1~100). 세터에서 자동 클램프 — 이상값 유입 방어.</summary>
    public int Intensity
    {
        get => _intensity;
        set => _intensity = Math.Clamp(value, 1, 100);
    }

    /// <summary>기후 앰비언스(빗소리/바람 백색소음) 믹싱 여부. 기본 true.</summary>
    public bool Ambience { get; set; } = true;

    /// <summary>적용된 프리셋 키(표시/재적용용). 비면 수동 구성.</summary>
    public string? PresetKey { get; set; }

    /// <summary>깊은 복사(프로젝트 간 값 전달용).</summary>
    public WeatherThemeConfig Clone() =>
        new() { Enabled = Enabled, Climate = Climate, TimeOfDay = TimeOfDay, Intensity = Intensity, Ambience = Ambience, PresetKey = PresetKey };

    /// <summary>프리셋 → 활성화된 구성으로 변환.</summary>
    public static WeatherThemeConfig FromPreset(WeatherPreset p)
    {
        // 프리셋 null 방어 — 잘못된 호출에도 기본 구성 반환.
        if (p is null) return new WeatherThemeConfig();
        return new WeatherThemeConfig { Enabled = true, Climate = p.Climate, TimeOfDay = p.TimeOfDay, Intensity = p.Intensity, PresetKey = p.Key };
    }
}

/// <summary>날씨 갤러리 카드 프리셋 — 키·제목·훅·이모지·기후·시간대·강도(+선택 썸네일 URL).</summary>
public sealed record WeatherPreset(
    string Key, string Title, string Hook, string Emoji,
    WeatherClimate Climate, WeatherTimeOfDay TimeOfDay, int Intensity, string? ImageUrl = null);

/// <summary>
/// [PromptFlowStudio] 날씨 테마 카탈로그 — 기후×시간대×강도 조합 프리셋을 오프라인으로 생성(외부 의존 0).
///   갤러리(<c>WeatherThemeGallery</c>)가 이 목록을 <c>&lt;Virtualize&gt;</c> 로 표출한다.
/// </summary>
public static class WeatherThemeCatalog
{
    /// <summary>기후 한국어 라벨.</summary>
    public static string ClimateLabel(WeatherClimate c) => c switch
    {
        WeatherClimate.Clear => "맑음",
        WeatherClimate.Rain => "비",
        WeatherClimate.Snow => "눈",
        WeatherClimate.Fog => "안개",
        _ => "기타",
    };

    /// <summary>시간대 한국어 라벨.</summary>
    public static string TimeLabel(WeatherTimeOfDay t) => t switch
    {
        WeatherTimeOfDay.Dawn => "새벽",
        WeatherTimeOfDay.Noon => "정오",
        WeatherTimeOfDay.Sunset => "노을",
        WeatherTimeOfDay.Night => "밤",
        _ => "종일",
    };

    /// <summary>기후 이모지.</summary>
    public static string ClimateEmoji(WeatherClimate c) => c switch
    {
        WeatherClimate.Clear => "☀️",
        WeatherClimate.Rain => "🌧️",
        WeatherClimate.Snow => "❄️",
        WeatherClimate.Fog => "🌫️",
        _ => "☁️",
    };

    /// <summary>기후 네온 강조색(글래스모피즘 카드 테두리/글로우 · CSS 변수 주입용).</summary>
    public static string NeonColor(WeatherClimate c) => c switch
    {
        WeatherClimate.Clear => "#f5a623",
        WeatherClimate.Rain => "#38bdf8",
        WeatherClimate.Snow => "#a5f3fc",
        WeatherClimate.Fog => "#a78bfa",
        _ => "#7dd3fc",
    };

    /// <summary>강도 티어 라벨.</summary>
    public static string TierLabel(int intensity) => intensity switch
    {
        <= 33 => "약",
        <= 66 => "보통",
        _ => "강",
    };

    private static readonly (int Value, string _)[] Tiers = { (30, "약"), (60, "보통"), (90, "강") };

    /// <summary>전체 프리셋(기후 4 × 시간대 4 × 강도 3 = 48종). 최초 1회 생성 후 캐시.</summary>
    public static IReadOnlyList<WeatherPreset> All { get; } = Build();

    private static List<WeatherPreset> Build()
    {
        var list = new List<WeatherPreset>(48);
        foreach (WeatherClimate c in Enum.GetValues<WeatherClimate>())
            foreach (WeatherTimeOfDay t in Enum.GetValues<WeatherTimeOfDay>())
                foreach (var (intensity, _) in Tiers)
                {
                    var key = $"{c}_{t}_{intensity}".ToLowerInvariant();
                    var title = $"{ClimateLabel(c)} · {TimeLabel(t)} · {TierLabel(intensity)}";
                    var hook = Hook(c, t, intensity);
                    list.Add(new WeatherPreset(key, title, hook, ClimateEmoji(c), c, t, intensity));
                }
        return list;
    }

    private static string Hook(WeatherClimate c, WeatherTimeOfDay t, int intensity)
    {
        var mood = c switch
        {
            WeatherClimate.Clear => "청명한 하늘과 또렷한 대기",
            WeatherClimate.Rain => intensity > 66 ? "번개 치는 폭우와 젖은 반사광" : "차분한 빗줄기와 젖은 노면",
            WeatherClimate.Snow => intensity > 66 ? "휘몰아치는 눈보라" : "포근하게 내리는 함박눈",
            WeatherClimate.Fog => intensity > 66 ? "짙은 볼류메트릭 안개" : "은은하게 깔린 물안개",
            _ => "기상 연출",
        };
        var timeMood = t switch
        {
            WeatherTimeOfDay.Dawn => "새벽 골든글로우",
            WeatherTimeOfDay.Noon => "밝은 자연광",
            WeatherTimeOfDay.Sunset => "노을 골든아워",
            WeatherTimeOfDay.Night => "달빛 야경 톤",
            _ => "",
        };
        return $"{mood} · {timeMood}";
    }
}
