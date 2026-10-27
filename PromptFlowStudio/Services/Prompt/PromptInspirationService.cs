using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PromptFlowStudio.Services.Prompt;

/// <summary>스토리 레시피 장르.</summary>
public enum StoryPresetCategory { CinematicSciFi, NatureDocu, EmotionalDrama, FashionLookbook, CyberpunkAction, CuteBabyKids }

/// <summary>장르별 완성형 시나리오 프리셋 — <see cref="FullPrompt"/> 는 Wan 2.1 최적화(≤800자).</summary>
public sealed record StoryRecipe(string Title, StoryPresetCategory Category, string FullPrompt, string RecommendedCamera, string RecommendedLighting);

/// <summary>레고식 조합기 칩 — 표시 라벨(한글) + 합성 스니펫(영문).</summary>
public sealed record InspirationChip(string Label, string Snippet);

/// <summary>⭐ 사용자 커스텀 프리셋 — <c>custom_story_presets.json</c> 영속.</summary>
public sealed record UserCustomPreset(Guid Id, string Title, string Prompt, string CameraMotion, DateTime CreatedAt);

/// <summary>
/// [PromptFlowStudio] 「스토리 레시피 &amp; 인스피레이션 허브」 — 장르별 큐레이션 프리셋 + 피사체/배경/카메라/조명 칩 합성(순수 BCL).
///   ▸ 프리셋·칩은 정적 불변 배열(인스턴스 간 공유 · 조회 할당 0).
///   ▸ <see cref="BuildFromChips"/> 는 빈 칩을 건너뛰고 <see cref="PromptSynthesizerService.MaxCharacterLimit"/> 로 절삭.
///   ▸ 사용자 프리셋은 %LocalAppData%\PromptFlowStudio\custom_story_presets.json(지연 로드 · 임시파일→원자 교체 · 손상 시 .corrupt.bak 보존).
/// </summary>
public sealed class PromptInspirationService
{
    private const string QualitySuffix = "ultra-detailed, 4K cinematic quality, smooth natural motion, consistent subject identity";
    public const string CustomPresetFileName = "custom_story_presets.json";
    public const int MaxCustomTitleLength = 80;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _customGate = new();
    private List<UserCustomPreset>? _custom;

    /// <summary>사용자 프리셋 JSON 경로.</summary>
    public string CustomPresetPath { get; }

    public PromptInspirationService() : this(null) { }

    /// <param name="customPresetPath">null/공백이면 기본 %LocalAppData% 경로(테스트는 임시 경로 주입).</param>
    public PromptInspirationService(string? customPresetPath)
    {
        CustomPresetPath = string.IsNullOrWhiteSpace(customPresetPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PromptFlowStudio", CustomPresetFileName)
            : customPresetPath!;
    }

    private static readonly StoryRecipe[] Presets =
    {
        // ── CuteBabyKids (인기 1위 · 최전면) ──
        new("햇살 속 첫 걸음마와 까르르 웃음", StoryPresetCategory.CuteBabyKids,
            "Adorable 1-year-old baby taking joyful first steps on a soft ivory rug, pure innocent laughter, chubby pink cheeks, cinematic morning golden-hour rays streaming through a large sunroom window, dust motes dancing, 8k resolution, ultra-photorealistic, high-end commercial look.",
            "Eye-Level Gentle Push-In, Slow Motion", "Golden Hour Window Rays, Soft Fill"),
        new("곰돌이 롬퍼와 판타지 숲속 요정", StoryPresetCategory.CuteBabyKids,
            "Chubby toddler wearing a fluffy plush brown teddy bear onesie sitting peacefully in a magical woodland glade, looking with wonder at glowing bioluminescent fireflies hovering around tiny hands, fairytale cinematic atmosphere, Pixar-meets-live-action aesthetic.",
            "Low-Angle Macro Orbit", "Magical Firefly Bokeh, Soft Forest Rim Light"),
        new("골든 리트리버와 낮잠 교감", StoryPresetCategory.CuteBabyKids,
            "Heartwarming moment of a sleeping baby in pastel pajamas snuggled gently against a sleepy, massive golden retriever on a sunlit daybed, puppy breathing softly, sheer white curtains fluttering, serene peaceful ambiance.",
            "Slow Dolly Out, High Angle", "Soft Diffused Daylight, Natural Warmth"),
        new("꼬마 우주비행사의 풍선 모험", StoryPresetCategory.CuteBabyKids,
            "Cute toddler in a miniature retro-futuristic astronaut suit holding a glowing crescent-moon balloon in a dreamy pastel galaxy nursery, floating soft movements, whimsical and heartwarming.",
            "Slow Floating Crane Reveal", "Pastel Ambient Glow, Rim Specular"),

        // ── CinematicSciFi ──
        new("궤도 정거장의 새벽", StoryPresetCategory.CinematicSciFi,
            "A lone astronaut floats beside a massive rotating space station above Earth at dawn, sunlight sweeping across solar panels, tiny debris glittering in zero gravity, the blue curve of the planet glowing below, epic sci-fi scale",
            "Slow Push-In with Subtle Orbit", "Hard Rim Sunlight, Deep Space Contrast"),
        new("붉은 사막의 착륙선", StoryPresetCategory.CinematicSciFi,
            "A sleek landing craft descends onto a red Martian desert, thrusters kicking up swirling dust clouds, explorers in white suits stepping onto the rocky surface, twin moons faint in the amber sky",
            "Low-Angle Crane Up", "Dusty Amber Haze, Volumetric Backlight"),
        new("유리 돔 미래 도시", StoryPresetCategory.CinematicSciFi,
            "A futuristic city under a gigantic glass dome on an alien ocean world, maglev trains gliding between white towers, waves crashing against the dome exterior, serene utopian atmosphere",
            "Aerial Drone Glide", "Soft Diffused Daylight, Pearlescent Reflections"),
        new("고대 외계 유적", StoryPresetCategory.CinematicSciFi,
            "An explorer with a flashlight walks through colossal alien ruins carved with glowing glyphs, floating stone fragments slowly rotating overhead, mist pooling on the ancient floor",
            "Tracking Dolly Forward", "Bioluminescent Cyan Glow, Low-Key Shadows"),

        // ── NatureDocu ──
        new("새벽 안개 피오르드", StoryPresetCategory.NatureDocu,
            "Morning mist drifting over a calm Norwegian fjord, steep cliffs wrapped in fog, a small red wooden cabin by the water, a lone eagle gliding over mirror-like water, nature documentary realism",
            "Slow Push-In with Subtle Orbit", "Volumetric Morning Rays, Golden Hour"),
        new("사바나의 황금빛 질주", StoryPresetCategory.NatureDocu,
            "A herd of zebras galloping across the African savanna at sunset, dust rising behind them, acacia trees silhouetted on the horizon, wildlife documentary telephoto realism",
            "Telephoto Lateral Tracking", "Warm Golden Hour Backlight"),
        new("심해의 발광 생물", StoryPresetCategory.NatureDocu,
            "Deep ocean bioluminescent jellyfish pulsing in pitch-black water, glowing tendrils drifting gracefully, tiny plankton sparkling around them, macro underwater documentary detail",
            "Slow Floating Drift", "Natural Bioluminescence, Pitch-Black Ambience"),
        new("열대우림의 첫 비", StoryPresetCategory.NatureDocu,
            "First rain falling on a lush tropical rainforest canopy, droplets bouncing off giant leaves, a colorful tree frog blinking on a branch, steam rising from the forest floor",
            "Macro Rack Focus Pull", "Soft Overcast Diffusion, Wet Specular Highlights"),

        // ── EmotionalDrama ──
        new("빗속의 기차역 이별", StoryPresetCategory.EmotionalDrama,
            "Two lovers say goodbye on a rainy train platform at night, the woman's hand slowly slipping away as the train doors close, raindrops streaking the window, restrained heartbreaking emotion",
            "Slow Dolly Out", "Moody Tungsten Practicals, Rain Backlight"),
        new("할머니의 부엌", StoryPresetCategory.EmotionalDrama,
            "An elderly grandmother teaching her granddaughter to knead dough in a sunlit rustic kitchen, flour floating in the air, gentle laughter and warm smiles, nostalgic heartfelt mood",
            "Handheld Gentle Sway", "Warm Window Light, Soft Bounce Fill"),
        new("옥상 위의 재회", StoryPresetCategory.EmotionalDrama,
            "Two old friends reunite on a city rooftop at dusk, slowly walking toward each other before a tight embrace, city lights flickering on behind them, cathartic emotional climax",
            "Slow Orbit Around Subjects", "Blue Hour Ambience, Warm Rim Light"),

        // ── FashionLookbook ──
        new("미니멀 화이트 스튜디오", StoryPresetCategory.FashionLookbook,
            "A fashion model in an oversized ivory trench coat walking toward camera in a minimal white studio, fabric flowing with each step, confident editorial pose at the end, luxury lookbook aesthetic",
            "Steady Front Tracking Walk", "High-Key Softbox Lighting"),
        new("파리 골목 스트리트룩", StoryPresetCategory.FashionLookbook,
            "A stylish model in a tailored camel blazer and sunglasses strolling down a cobblestone Paris alley, morning cafe tables in the background, effortless street style editorial",
            "Lateral Gimbal Follow", "Soft Morning Sun, Natural Bounce"),
        new("사막 실크 드레스", StoryPresetCategory.FashionLookbook,
            "A model in a flowing crimson silk gown standing on a vast sand dune, wind sweeping the long fabric into dramatic waves, haute couture campaign grandeur",
            "Wide Crane Reveal", "Golden Hour Side Light, Long Shadows"),

        // ── CyberpunkAction ──
        new("네온 골목 추격전", StoryPresetCategory.CyberpunkAction,
            "A hooded runner sprinting through a rain-soaked neon alley, holographic signs flickering overhead, splashing through puddles reflecting magenta and cyan lights, drones chasing from above",
            "Fast Handheld Tracking", "Neon Magenta-Cyan Contrast, Wet Reflections"),
        new("호버바이크 하이웨이", StoryPresetCategory.CyberpunkAction,
            "A rider on a glowing hoverbike weaving through dense traffic on an elevated megacity highway at night, light trails streaking past, towering holographic billboards",
            "High-Speed Chase Cam", "Neon Light Trails, Motion Blur Glow"),
        new("옥상 사이버 결투", StoryPresetCategory.CyberpunkAction,
            "Two cyber-enhanced fighters clash with glowing energy blades on a skyscraper rooftop in heavy rain, sparks scattering on impact, a giant holographic koi swimming in the sky behind them",
            "Dynamic Orbit with Speed Ramp", "Electric Blue Rim Light, Volumetric Rain"),
    };

    public static readonly InspirationChip[] Subjects =
    {
        new("🧑‍🚀 우주비행사", "a lone astronaut in a white spacesuit"),
        new("🐕 골든리트리버", "a fluffy golden retriever"),
        new("💃 패션 모델", "an elegant fashion model in flowing clothes"),
        new("🥷 사이버 닌자", "a cyber ninja with glowing visor"),
        new("👵 할머니", "a warm-hearted elderly grandmother"),
        new("🦅 독수리", "a majestic bald eagle"),
        new("👶 앙증맞은 아기", "an adorable chubby-cheeked baby"),
        new("🧸 곰돌이 옷을 입은 토들러", "a toddler in a fluffy teddy bear onesie"),
    };

    public static readonly InspirationChip[] Environments =
    {
        new("🌫 안개 피오르드", "a misty Norwegian fjord"),
        new("🌃 네온 골목", "a rain-soaked neon-lit alley"),
        new("🏜 붉은 사막", "a vast red desert with towering dunes"),
        new("🌴 열대우림", "a lush tropical rainforest"),
        new("🏙 도심 옥상", "a city rooftop overlooking skyscrapers"),
        new("⬜ 화이트 스튜디오", "a minimal white studio"),
        new("🍼 파스텔톤 햇살 아기방", "a sunlit pastel-toned nursery"),
        new("🌼 동화 같은 잔디 꽃밭", "a fairytale grassy meadow full of wildflowers"),
    };

    public static readonly InspirationChip[] Motions =
    {
        new("➡ 슬로 푸시인", "slow push-in with subtle orbit"),
        new("🔄 360 오빗", "smooth 360-degree orbit"),
        new("🚁 드론 에어리얼", "sweeping aerial drone glide"),
        new("🏃 핸드헬드 추적", "fast handheld tracking shot"),
        new("🏗 크레인 업", "low-angle crane up reveal"),
        new("🎯 랙 포커스", "macro rack focus pull"),
    };

    public static readonly InspirationChip[] Lightings =
    {
        new("🌅 골든아워", "volumetric golden hour rays"),
        new("💡 네온 대비", "neon magenta-cyan contrast lighting"),
        new("🌙 블루아워", "soft blue hour ambience"),
        new("🕯 무디 텅스텐", "moody tungsten practical lights"),
        new("☁ 소프트 디퓨즈", "soft overcast diffused light"),
        new("⚡ 림 라이트", "dramatic electric rim light"),
        new("🪟 포근한 창가 햇살", "cozy soft window sunlight"),
        new("🍭 파스텔 솜사탕 글로우", "dreamy pastel cotton-candy glow"),
    };

    /// <summary>장르별 엄선 프리셋(15개+) — 정적 불변 배열을 그대로 노출(할당 0).</summary>
    public IReadOnlyList<StoryRecipe> GetCuratedPresets() => Presets;

    /// <summary>카테고리 필터 — 모달 탭 전환 시 1회 생성.</summary>
    public IReadOnlyList<StoryRecipe> GetPresetsByCategory(StoryPresetCategory category)
    {
        var list = new List<StoryRecipe>(4);
        for (var i = 0; i < Presets.Length; i++)
            if (Presets[i].Category == category) list.Add(Presets[i]);
        return list;
    }

    /// <summary>장르 한글 라벨.</summary>
    public static string CategoryLabel(StoryPresetCategory c) => c switch
    {
        StoryPresetCategory.CinematicSciFi => "🚀 시네마틱 SF",
        StoryPresetCategory.NatureDocu => "🌿 자연 다큐",
        StoryPresetCategory.EmotionalDrama => "🎭 감성 드라마",
        StoryPresetCategory.FashionLookbook => "👗 패션 룩북",
        StoryPresetCategory.CyberpunkAction => "🌆 사이버펑크 액션",
        StoryPresetCategory.CuteBabyKids => "👶 큐트 베이비",
        _ => c.ToString(),
    };

    /// <summary>
    /// 칩 키워드 → 시네마틱 문장 합성.
    /// 포맷: "Cinematic shot of {subject} in {environment}, camera: {motion}, lighting: {lighting}, {품질}" — 빈 칩은 생략, 전부 비면 "".
    /// </summary>
    public string BuildFromChips(string? subject, string? environment, string? motion, string? lighting)
    {
        var s = subject?.Trim(); var e = environment?.Trim(); var m = motion?.Trim(); var l = lighting?.Trim();
        if (string.IsNullOrEmpty(s) && string.IsNullOrEmpty(e) && string.IsNullOrEmpty(m) && string.IsNullOrEmpty(l)) return "";

        var sb = new StringBuilder(256).Append("Cinematic shot of ");
        sb.Append(string.IsNullOrEmpty(s) ? "a striking scene" : s);
        if (!string.IsNullOrEmpty(e)) sb.Append(" in ").Append(e);
        if (!string.IsNullOrEmpty(m)) sb.Append(", camera: ").Append(m);
        if (!string.IsNullOrEmpty(l)) sb.Append(", lighting: ").Append(l);
        sb.Append(", ").Append(QualitySuffix);
        if (sb.Length > PromptSynthesizerService.MaxCharacterLimit) sb.Length = PromptSynthesizerService.MaxCharacterLimit;
        return sb.ToString();
    }

    // ── ⭐ 사용자 커스텀 프리셋 ──

    /// <summary>사용자 프리셋 스냅숏(최신 저장 순). 파일 없음/손상이면 빈 목록.</summary>
    public IReadOnlyList<UserCustomPreset> LoadCustomPresets()
    {
        lock (_customGate) return EnsureCustomLocked().ToArray();
    }

    /// <summary>큐레이션 + 사용자 프리셋 통합 조회(분리 유지).</summary>
    public (IReadOnlyList<StoryRecipe> Curated, IReadOnlyList<UserCustomPreset> Custom) GetAllPresets()
        => (Presets, LoadCustomPresets());

    /// <summary>프리셋 저장(제목 ≤80자 · 프롬프트 ≤800자 절삭). 빈 제목/프롬프트는 <see cref="ArgumentException"/>.</summary>
    public void SaveCustomPreset(string title, string prompt, string cameraMotion)
    {
        var t = title?.Trim(); var p = prompt?.Trim();
        if (string.IsNullOrEmpty(t)) throw new ArgumentException("프리셋 제목이 비어 있습니다.", nameof(title));
        if (string.IsNullOrEmpty(p)) throw new ArgumentException("프리셋 프롬프트가 비어 있습니다.", nameof(prompt));
        if (t.Length > MaxCustomTitleLength) t = t[..MaxCustomTitleLength];
        if (p.Length > PromptSynthesizerService.MaxCharacterLimit) p = p[..PromptSynthesizerService.MaxCharacterLimit];

        lock (_customGate)
        {
            var list = EnsureCustomLocked();
            list.Insert(0, new UserCustomPreset(Guid.NewGuid(), t, p, cameraMotion?.Trim() ?? "", DateTime.UtcNow));
            PersistLocked(list);
        }
    }

    /// <summary>Id 로 삭제 — 없으면 false(파일 불변).</summary>
    public bool DeleteCustomPreset(Guid presetId)
    {
        lock (_customGate)
        {
            var list = EnsureCustomLocked();
            var idx = list.FindIndex(x => x.Id == presetId);
            if (idx < 0) return false;
            list.RemoveAt(idx);
            PersistLocked(list);
            return true;
        }
    }

    private List<UserCustomPreset> EnsureCustomLocked()
    {
        if (_custom is not null) return _custom;
        _custom = new List<UserCustomPreset>();
        try
        {
            if (!File.Exists(CustomPresetPath)) return _custom;
            var arr = JsonSerializer.Deserialize<UserCustomPreset[]>(File.ReadAllText(CustomPresetPath, Encoding.UTF8), JsonOpts);
            if (arr is not null)
                foreach (var x in arr)
                    if (x is not null && x.Id != Guid.Empty && !string.IsNullOrWhiteSpace(x.Title) && !string.IsNullOrWhiteSpace(x.Prompt))
                        _custom.Add(x with { CameraMotion = x.CameraMotion ?? "" });
        }
        catch (JsonException)
        {
            // [부분 구제] 손상 파일은 덮어쓰기 전 백업 보존.
            try { File.Copy(CustomPresetPath, CustomPresetPath + ".corrupt.bak", overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return _custom;
    }

    private void PersistLocked(List<UserCustomPreset> list)
    {
        var dir = Path.GetDirectoryName(CustomPresetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = CustomPresetPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, JsonOpts), new UTF8Encoding(false));
        File.Move(tmp, CustomPresetPath, overwrite: true);
    }
}
