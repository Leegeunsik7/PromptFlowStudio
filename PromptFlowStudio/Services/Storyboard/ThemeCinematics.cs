using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using PromptFlowStudio.Services.Ui;

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>v16.7.0 권장 카메라 무빙 — <see cref="WanCameraStyleStudio.Cameras"/> 인덱스와 1:1(순서 고정).</summary>
public enum CameraMotion { Orbit = 0, FpvDrone = 1, CraneUp = 2, DollyZoom = 3, SmoothPan = 4, TiltRoll = 5 }

/// <summary>v16.7.0 권장 조명.</summary>
public enum ThemeLighting { GoldenHour, StudioSoft, NaturalDaylight, MoonlightBlue, WarmTungsten, NeonFestive }

/// <summary>
/// v16.7.0 테마 시네마틱 메타데이터 — 카메라 무빙 · 조명 · 렌즈 감성 · 권장 SFX(최대 3) · 최적 모션 강도(2~4).
///   v16.8.0: <see cref="Negative"/> — 커스텀 테마 authored 네거티브(null 이면 카테고리 기본).
/// </summary>
public sealed record ThemeCinematics(CameraMotion Camera, ThemeLighting Lighting, string Lens, IReadOnlyList<string> Sfx, int MotionIntensity, string? Negative = null)
{
    /// <summary>최적 모션 강도 하한/상한.</summary>
    public const int MinIntensity = 2, MaxIntensity = 4;

    /// <summary>카메라 칩(라벨·스니펫·아이콘).</summary>
    public PromptChip CameraChip => WanCameraStyleStudio.Cameras[(int)Camera];

    /// <summary>조명 → Wan 프롬프트 구문.</summary>
    public string LightingSnippet => ThemeCinematicsCatalog.LightingSnippet(Lighting);

    /// <summary>조명 표시 라벨(아이콘 포함).</summary>
    public string LightingLabel => ThemeCinematicsCatalog.LightingLabel(Lighting);

    /// <summary>모션 강도 → Wan 렌더 MotionIntensity(0~100) 환산(2=40 · 3=60 · 4=80).</summary>
    public int RenderMotion => MotionIntensity * 20;

    /// <summary>모션 강도 → Wan 2.1 Motion Bucket(1~255) 환산(2=90 · 3=127 · 4=170).</summary>
    public int MotionBucket => MotionIntensity switch { <= 2 => 90, 3 => 127, _ => 170 };
}

/// <summary>
/// [PromptFlowStudio] v16.7.0 🎬 테마 시네마틱 메타데이터 카탈로그 — 백과사전 전 테마(아기·B2B·자동차 동일 스키마)에 연출 메타 할당.
///   ▸ 각 테마의 authored Shot(렌즈·조명·구도 서술)·비트 긴장도·폴리 토큰·배속에서 결정적으로 산출(테마마다 고유값 · 수작업 데이터 재사용).
///   ▸ 카메라: Shot 키워드 가중 스코어(회전/드론/크레인/매크로/트래킹/피시아이) → 동점·미검출 시 카테고리 기본값.
///   ▸ 조명: Shot 조명 서술 키워드 다수결 → 미검출 시 카테고리 기본값. SFX: 폴리 토큰 빈도 상위 3. 강도: 평균 긴장도 + 배속 보정.
///   ▸ 결과는 테마 인스턴스별 <see cref="ConditionalWeakTable{TKey,TValue}"/> 캐시 — 첫 조회 1회 계산 후 렌더마다 0B.
///   순수 BCL · 모바일 링크.
/// </summary>
public static class ThemeCinematicsCatalog
{
    private static readonly ConditionalWeakTable<BabyThemeDef, ThemeCinematics> Cache = new();
    private static readonly ConditionalWeakTable<BabyTheme, ThemeCinematics> PresetCache = new();

    // 카메라 키워드 → (카메라, 가중치). 다단어 구문은 더 강한 근거.
    private static readonly (string Key, CameraMotion Cam, int W)[] CameraKeys =
    {
        ("orbit", CameraMotion.Orbit, 4), ("360", CameraMotion.Orbit, 3), ("spinning", CameraMotion.Orbit, 2), ("carousel", CameraMotion.Orbit, 3), ("swirl", CameraMotion.Orbit, 1), ("twirl", CameraMotion.Orbit, 2),
        ("drone", CameraMotion.FpvDrone, 4), ("aerial", CameraMotion.FpvDrone, 3), ("swoop", CameraMotion.FpvDrone, 3), ("flying", CameraMotion.FpvDrone, 1), ("bird's-eye", CameraMotion.FpvDrone, 3),
        ("crane", CameraMotion.CraneUp, 4), ("reveal", CameraMotion.CraneUp, 2), ("majestic wide", CameraMotion.CraneUp, 3), ("low-angle", CameraMotion.CraneUp, 2), ("rising", CameraMotion.CraneUp, 1), ("towering", CameraMotion.CraneUp, 2),
        ("dolly zoom", CameraMotion.DollyZoom, 5), ("vertigo", CameraMotion.DollyZoom, 4), ("extreme macro", CameraMotion.DollyZoom, 3), ("extreme close-up", CameraMotion.DollyZoom, 3), ("macro", CameraMotion.DollyZoom, 1), ("probe", CameraMotion.DollyZoom, 2),
        ("tracking", CameraMotion.SmoothPan, 4), ("pan", CameraMotion.SmoothPan, 2), ("passing", CameraMotion.SmoothPan, 2), ("along the", CameraMotion.SmoothPan, 2), ("gliding", CameraMotion.SmoothPan, 1), ("walking forward", CameraMotion.SmoothPan, 2),
        ("fisheye", CameraMotion.TiltRoll, 4), ("tilt-shift", CameraMotion.TiltRoll, 3), ("dutch", CameraMotion.TiltRoll, 3), ("bouncing", CameraMotion.TiltRoll, 2), ("whip", CameraMotion.TiltRoll, 2), ("tumbl", CameraMotion.TiltRoll, 1),
    };

    private static readonly (string Key, ThemeLighting Light)[] LightKeys =
    {
        ("golden", ThemeLighting.GoldenHour), ("sunset", ThemeLighting.GoldenHour), ("sun flare", ThemeLighting.GoldenHour), ("backlit", ThemeLighting.GoldenHour), ("dusk", ThemeLighting.GoldenHour),
        ("softbox", ThemeLighting.StudioSoft), ("studio", ThemeLighting.StudioSoft), ("pastel", ThemeLighting.StudioSoft), ("diffus", ThemeLighting.StudioSoft), ("led", ThemeLighting.StudioSoft), ("overcast", ThemeLighting.StudioSoft),
        ("sunny", ThemeLighting.NaturalDaylight), ("daylight", ThemeLighting.NaturalDaylight), ("bright", ThemeLighting.NaturalDaylight), ("dappled", ThemeLighting.NaturalDaylight), ("blue sky", ThemeLighting.NaturalDaylight), ("morning", ThemeLighting.NaturalDaylight),
        ("moon", ThemeLighting.MoonlightBlue), ("blue-hour", ThemeLighting.MoonlightBlue), ("night", ThemeLighting.MoonlightBlue), ("starlight", ThemeLighting.MoonlightBlue), ("cosmic", ThemeLighting.MoonlightBlue), ("nebula", ThemeLighting.MoonlightBlue),
        ("tungsten", ThemeLighting.WarmTungsten), ("candle", ThemeLighting.WarmTungsten), ("lantern", ThemeLighting.WarmTungsten), ("amber", ThemeLighting.WarmTungsten), ("cozy", ThemeLighting.WarmTungsten), ("fireplace", ThemeLighting.WarmTungsten),
        ("neon", ThemeLighting.NeonFestive), ("twinkling", ThemeLighting.NeonFestive), ("festive", ThemeLighting.NeonFestive), ("hologram", ThemeLighting.NeonFestive), ("laser", ThemeLighting.NeonFestive), ("disco", ThemeLighting.NeonFestive),
    };

    private const int CameraCount = 6, LightCount = 6;

    /// <summary>백과사전 테마 → 시네마틱 메타(인스턴스 캐시).</summary>
    public static ThemeCinematics For(BabyThemeDef theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return Cache.GetValue(theme, static t =>
        {
            Span<int> cam = stackalloc int[CameraCount];
            Span<int> light = stackalloc int[LightCount];
            var sfx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tension = 0;
            string? lens = null;
            for (var i = 0; i < t.Beats.Count; i++)
            {
                var b = t.Beats[i];
                Score(b.Shot, cam, light);
                lens ??= LensOf(b.Shot);
                tension += b.T;
                CountSfx(b.Sfx, sfx);
            }
            var avg = t.Beats.Count == 0 ? 50 : tension / t.Beats.Count;
            return Build(t.CategoryKey, cam, light, lens, sfx, avg, t.ShortSpeed);
        });
    }

    /// <summary>
    /// v16.8.0 커스텀 테마의 사용자 지정 메타를 등록(산출값 대신 사용) — 인스펙터·추천·팩토리가 동일 경로로 공유.
    /// </summary>
    public static void Register(BabyThemeDef theme, ThemeCinematics cinematics)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(cinematics);
        Cache.AddOrUpdate(theme, cinematics);
    }

    /// <summary>원클릭 프리셋 테마(<see cref="BabyThemePresetService"/>) → 시네마틱 메타(인스턴스 캐시).</summary>
    public static ThemeCinematics For(BabyTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        return PresetCache.GetValue(theme, static t =>
        {
            Span<int> cam = stackalloc int[CameraCount];
            Span<int> light = stackalloc int[LightCount];
            var sfx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var tension = 0;
            string? lens = null;
            for (var i = 0; i < t.Cuts.Count; i++)
            {
                var c = t.Cuts[i];
                Score(c.Prompt, cam, light);
                lens ??= LensOf(c.Prompt);
                tension += c.Tension;
                for (var f = 0; f < c.Foley.Count; f++) Bump(sfx, c.Foley[f].SfxType);
            }
            var avg = t.Cuts.Count == 0 ? 50 : tension / t.Cuts.Count;
            return Build("", cam, light, lens, sfx, avg, t.Speed);
        });
    }

    /// <summary>조명 → Wan 프롬프트 구문.</summary>
    public static string LightingSnippet(ThemeLighting l) => l switch
    {
        ThemeLighting.GoldenHour => "warm golden hour lighting",
        ThemeLighting.StudioSoft => "soft diffused studio lighting",
        ThemeLighting.NaturalDaylight => "clean natural daylight",
        ThemeLighting.MoonlightBlue => "cool moonlit blue ambience",
        ThemeLighting.WarmTungsten => "cozy warm tungsten glow",
        _ => "vibrant festive neon lights",
    };

    /// <summary>조명 표시 라벨.</summary>
    public static string LightingLabel(ThemeLighting l) => l switch
    {
        ThemeLighting.GoldenHour => "🌇 골든아워",
        ThemeLighting.StudioSoft => "💡 스튜디오 소프트",
        ThemeLighting.NaturalDaylight => "☀️ 자연광",
        ThemeLighting.MoonlightBlue => "🌙 문라이트",
        ThemeLighting.WarmTungsten => "🕯️ 웜 텅스텐",
        _ => "🎆 네온 페스티브",
    };

    /// <summary>SFX 토큰 → 아이콘.</summary>
    public static string SfxIcon(string? sfx) => sfx switch
    {
        "giggle" => "😆", "splash" => "💦", "whoosh" => "💨", "boing" => "🦘", "pop" => "🫧", "sparkle" or "twinkle" => "✨",
        "chomp" => "😋", "crash" => "💥", "ding" => "🔔", "clap" => "👏", "drumroll" => "🥁", "suspense" => "🤫",
        "squeak" => "🐭", "pluck" => "🎸", "cute" => "🥰", _ => "🔊",
    };

    // 카테고리 기본값(Shot 근거가 약할 때).
    private static (CameraMotion Cam, ThemeLighting Light) DefaultsFor(string? cat) => cat switch
    {
        "food" or "snack" => (CameraMotion.DollyZoom, ThemeLighting.GoldenHour),
        "sleep" or "dream" => (CameraMotion.SmoothPan, ThemeLighting.MoonlightBlue),
        "outdoor" or "everyday" or "playground" => (CameraMotion.SmoothPan, ThemeLighting.NaturalDaylight),
        "kidscafe" => (CameraMotion.TiltRoll, ThemeLighting.StudioSoft),
        "amusement" => (CameraMotion.Orbit, ThemeLighting.NeonFestive),
        "space" or "fantasy" => (CameraMotion.FpvDrone, ThemeLighting.MoonlightBlue),
        "baby_milestone" or "special" or "family" => (CameraMotion.CraneUp, ThemeLighting.StudioSoft),
        _ => (CameraMotion.SmoothPan, ThemeLighting.StudioSoft),
    };

    private static ThemeCinematics Build(string? cat, ReadOnlySpan<int> cam, ReadOnlySpan<int> light, string? lens,
        Dictionary<string, int> sfx, int avgTension, double speed)
    {
        var (dc, dl) = DefaultsFor(cat);
        var c = ArgMax(cam, (int)dc);
        var l = ArgMax(light, (int)dl);
        var intensity = avgTension < 45 ? 2 : avgTension < 62 ? 3 : 4;
        if (speed > 1.05 && intensity < ThemeCinematics.MaxIntensity) intensity++;
        return new ThemeCinematics((CameraMotion)c, (ThemeLighting)l, lens ?? "50mm 시네마틱", TopSfx(sfx), intensity);
    }

    // 최댓값 인덱스 — 0점 또는 기본값과 동점이면 기본값 우선.
    private static int ArgMax(ReadOnlySpan<int> s, int fallback)
    {
        var best = fallback;
        for (var i = 0; i < s.Length; i++)
            if (s[i] > s[best]) best = i;
        return s[best] == 0 ? fallback : best;
    }

    private static void Score(string? text, Span<int> cam, Span<int> light)
    {
        if (string.IsNullOrEmpty(text)) return;
        var s = text.AsSpan();
        foreach (var (k, c, w) in CameraKeys)
            if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) cam[(int)c] += w;
        foreach (var (k, l) in LightKeys)
            if (s.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) light[(int)l]++;
    }

    /// <summary>Shot 서술 → 렌즈 감성 태그(첫 근거).</summary>
    internal static string? LensOf(string? shot)
    {
        if (string.IsNullOrEmpty(shot)) return null;
        var s = shot.AsSpan();
        if (s.IndexOf("fisheye", StringComparison.OrdinalIgnoreCase) >= 0) return "Fisheye 광각 왜곡";
        if (s.IndexOf("anamorphic", StringComparison.OrdinalIgnoreCase) >= 0) return "Anamorphic 시네마스코프";
        if (s.IndexOf("tilt-shift", StringComparison.OrdinalIgnoreCase) >= 0) return "Tilt-shift 미니어처";
        if (s.IndexOf("macro", StringComparison.OrdinalIgnoreCase) >= 0) return "Macro 디테일";
        var mm = s.IndexOf("mm", StringComparison.OrdinalIgnoreCase);
        if (mm <= 0) return null;
        var st = mm;
        while (st > 0 && char.IsAsciiDigit(s[st - 1])) st--;
        if (st == mm || !int.TryParse(s[st..mm], out var f)) return null;
        var feel = f <= 24 ? "초광각" : f <= 40 ? "광각 스토리" : f <= 60 ? "표준 시네마틱" : "인물 보케";
        return string.Concat(s[st..(mm + 2)], " ", feel);
    }

    private static void CountSfx(string? sfx, Dictionary<string, int> counts)
    {
        if (string.IsNullOrWhiteSpace(sfx)) return;
        var rest = sfx.AsSpan();
        while (rest.Length > 0)
        {
            var comma = rest.IndexOf(',');
            var tok = comma < 0 ? rest : rest[..comma];
            rest = comma < 0 ? ReadOnlySpan<char>.Empty : rest[(comma + 1)..];
            var at = tok.IndexOf('@');
            if (at >= 0) tok = tok[..at];
            tok = tok.Trim();
            if (tok.Length > 0) Bump(counts, tok.ToString());
        }
    }

    private static void Bump(Dictionary<string, int> counts, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
    }

    // 빈도 상위 3(동점은 키 사전순 — 결정적).
    private static IReadOnlyList<string> TopSfx(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return Array.Empty<string>();
        var list = new List<KeyValuePair<string, int>>(counts);
        list.Sort(static (a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value) : string.CompareOrdinal(a.Key, b.Key));
        var n = Math.Min(3, list.Count);
        var arr = new string[n];
        for (var i = 0; i < n; i++) arr[i] = list[i].Key;
        return arr;
    }
}
