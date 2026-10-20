using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PromptFlowStudio.Services.Media;   // KineticCaptionWord / FoleySpec / VideoTransition / TransitionSafety

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>영상 포맷 — 숏츠(6컷·루프·급스파이크) 또는 롱폼 브이로그(15~20컷·서사·완만한 빌드업).</summary>
public enum VideoFormat { Shorts, Longs }

/// <summary>
/// [PromptFlowStudio] 롱/숏 포맷 팩토리 — <see cref="BabyThemeDatabase"/>의 compact 6-비트를 포맷에 맞는 완성형 스토리보드로 확장. 순수 BCL(외부 NuGet 0).
///   ▸ <b>숏츠</b>: 6컷 그대로, 세로 1080×1920, 빠른 배속(테마 지정), 3초 반전 <b>무한 루프</b>(컷6≈컷1 매치컷), 긴장도 <b>급스파이크</b>, 빠른 xfade.
///   ▸ <b>롱폼(브이로그)</b>: 인트로(2) + 6비트 각 2컷 확장(12) + 아웃트로(2) = <b>16컷</b>, 가로 1920×1080, 원본 배속(1.0),
///       <b>서사형 카메라 템포 아크</b>(Slow Dolly→Handheld Push→Whip-Pan 클라이맥스→Soft Pan-Out), 완만한 <b>기승전결</b> 텐션 곡선(22→peak≈75%→26).
///   ▸ 프롬프트는 컷의 Shot(렌즈·조명·질감을 테마별로 authored)을 그대로 살리고 포맷 템포만 덧댐 — 테마 고유 미학 보존.
///   ▸ 폴리는 컷의 <see cref="BabyBeat.Sfx"/>를 소비 — 토큰 <c>type</c> 또는 <c>type@pan</c>(예: <c>chomp@L30</c>, <c>splash@R45</c>, <c>ding@C</c>)로 <b>3D 팬 세밀화</b>.
///       팬 미지정 토큰은 기존과 동일하게 좌/우/중앙 순환(하위호환 100%).
///   ▸ 두 포맷 모두 authored 데이터(키네틱/폴리/텐션/전환)를 컷에 심어 <see cref="StoryboardProject.IsBabyPreset"/>=true → <b>Gemini 없이 오프라인</b> 병합.
///   ▸ 기존 저장/열기 100% 호환(모두 직렬화 필드). 실패 없이 항상 적용(빈 키는 무시).
/// </summary>
public sealed class VideoFormatFactory
{
    private readonly BabyThemeDatabase _db;
    private readonly CorporateThemeDatabase _corp;
    private readonly AutomotiveEquipmentDatabase _auto;
    public VideoFormatFactory(BabyThemeDatabase db, CorporateThemeDatabase corp, AutomotiveEquipmentDatabase auto)
    { _db = db; _corp = corp; _auto = auto; }

    // ── 포맷 상수(UI 요약/방어용) ──
    /// <summary>숏츠 컷 수(고정).</summary>
    public const int ShortsCuts = 6;
    /// <summary>롱폼 컷 수 = 인트로2 + 본문(6비트×2) + 아웃트로2.</summary>
    public const int LongsCuts = 2 + BeatBodyCutsPer * 6 + 2;   // = 16
    private const int BeatBodyCutsPer = 2;

    private const int ShortClipMs = 4600;   // 숏츠 컷 길이(키네틱/폴리 분배 기준)
    private const int LongClipMs = 6200;    // 롱폼 컷 길이(더 길게 · 슬로우)

    /// <summary>포맷별 예상 컷 수(갤러리 배지용).</summary>
    public static int CutCountOf(VideoFormat format) => format == VideoFormat.Shorts ? ShortsCuts : LongsCuts;

    /// <summary>키 + 포맷으로 스토리보드를 구성한다(테마 없거나 project null 이면 false). 앵커(아기 사진)는 유지.</summary>
    public bool Apply(StoryboardProject project, string themeKey, VideoFormat format)
    {
        var theme = _db.Get(themeKey) ?? _corp.Get(themeKey) ?? _auto.Get(themeKey);   // 아기 → B2B 기업 → 자동차 순 조회(동일 스키마)
        if (project is null || theme is null) return false;
        return Apply(project, theme, format);
    }

    /// <summary>테마 정의 + 포맷으로 스토리보드를 구성한다.</summary>
    public bool Apply(StoryboardProject project, BabyThemeDef theme, VideoFormat format)
    {
        if (project is null || theme is null || theme.Beats.Count == 0) return false;

        var scenes = format == VideoFormat.Longs ? BuildLongs(project, theme) : BuildShorts(project, theme);

        project.Scenes.Clear();
        var cine = theme.Cinematics;                                                // v16.7.0 시네마틱 메타 단일 패스 바인딩
        var negative = cine.Negative ?? BabyEverydayThemeDatabase.NegativeFor(theme.CategoryKey);   // v16.8.0 커스텀 네거티브 우선 · v16.6.0 실생활 테마 네거티브
        foreach (var s in scenes)
        {
            if (negative is not null) s.NegativePrompt = negative;
            s.Prompt = PromptFlowStudio.Services.Ui.WanCameraStyleStudio.ApplyCamera(s.Prompt, (int)cine.Camera);   // 카메라 그룹 배타 토큰
            project.Scenes.Add(s);
        }
        project.CinematicCamera = cine.CameraChip.Label;
        project.CinematicLighting = cine.Lighting.ToString();
        project.CinematicSfx = new List<string>(cine.Sfx);
        project.CinematicMotionIntensity = cine.MotionIntensity;

        // 공통 프로젝트 세팅 — authored 오프라인 파이프라인 활성.
        var isLong = format == VideoFormat.Longs;
        project.Title = theme.Emoji + " " + theme.Title + (isLong ? " · 롱폼" : " · 숏츠");
        project.ExportResolution = isLong ? "1920x1080" : "1080x1920";
        project.PlaybackSpeed = isLong ? 1.0 : Math.Clamp(theme.ShortSpeed <= 0 ? 1.0 : theme.ShortSpeed, 0.5, 2.0);
        project.UseTempoSpeedup = project.PlaybackSpeed > 1.001;   // 배속 필수 테마(펭귄 2.0x 등)면 자동 ON(귀여움 연출), 1.0 테마는 OFF
        project.CoverTitle = isLong ? (theme.Title + " 브이로그") : theme.Hook;
        project.UseSmartTransitions = true;
        project.UseKineticCaptions = true;
        project.UseEmotionPacing = true;
        project.UseGenFoley = true;
        project.UseVisualSfxBadges = true;
        project.EnableAudioDucking = true;
        project.UseFadeEffect = false;    // 컷 전환은 xfade 담당
        project.IsBabyPreset = true;
        project.PresetKey = theme.Key;
        project.EmbossWatermark = IsCorporate(theme.CategoryKey);   // B2B 테마면 3D 엠보싱 워터마크 자동 ON(아기 테마는 false)
        project.TelemetryHudStyle = HudStyleFor(theme.CategoryKey);  // 자동차 검사/시험 장비면 텔레메트리 HUD 스타일 자동 설정(그 외는 "")
        return true;
    }

    /// <summary>B2B(기업/자동차/사옥) 카테고리인지 — 엠보싱 워터마크 자동 활성 판정.</summary>
    private static bool IsCorporate(string? categoryKey)
        => categoryKey is not null && (categoryKey.StartsWith("corp_", StringComparison.Ordinal)
            || categoryKey.StartsWith("auto_", StringComparison.Ordinal) || categoryKey == "building");

    /// <summary>자동차 장비 카테고리 → 텔레메트리 HUD 스타일 매핑(시트=게이지 · 완성차/캘리브레이션=오실로스코프 · 전동화/다이나모/챔버=다이나모). 그 외는 빈 값.</summary>
    private static string HudStyleFor(string? categoryKey) => categoryKey switch
    {
        "auto_seat" => "gauge",
        "auto_finish" => "oscilloscope",
        "auto_ev" => "dyno",
        _ => "",
    };

    // ══════════════════════ 숏츠(6컷·루프·급스파이크) ══════════════════════
    private static List<StoryboardScene> BuildShorts(StoryboardProject project, BabyThemeDef theme)
    {
        const string size = "720*1280";   // 세로
        var list = new List<StoryboardScene>(theme.Beats.Count);
        for (var i = 0; i < theme.Beats.Count; i++)
        {
            var b = theme.Beats[i];
            list.Add(MakeScene(project, size, BuildPrompt(b.Shot, VideoFormat.Shorts), b.V, b.G,
                tension: Math.Clamp(b.T, 0, 100),                                   // authored 급스파이크 그대로
                enter: new VideoTransition(TransitionSafety.SafeXfade(b.X), FastXfadeMs(b.X)),
                foley: BuildFoley(b.Sfx, ShortClipMs),
                clipMs: ShortClipMs));
        }
        return list;
    }

    // 숏츠 전환 길이 — 임팩트 컷은 짧고 스냅 있게, 페이드는 살짝 길게.
    private static int FastXfadeMs(string x) => TransitionSafety.SafeXfade(x) switch
    {
        "fade" => 450,
        "hblur" => 400,
        _ => 300,
    };

    // ══════════════════════ 롱폼(16컷·서사·완만한 빌드업) ══════════════════════
    private static readonly string[] LongTransitions = { "fade", "dissolve", "fadeblack", "smoothleft", "smoothright" };

    // 브이로그 내레이터 톤의 리액션/브릿지 라인(본문 확장 컷 · 짧은 감성 나레이션).
    private static readonly (string Voice, string Gold)[] BridgeLines =
    {
        ("이 순간을 놓칠 수 없었어요", "순간"),
        ("이 표정 좀 보세요 너무 사랑스럽죠", "사랑스럽죠"),
        ("엄마 아빠는 그만 웃음이 터졌답니다", "웃음"),
        ("오늘도 이렇게 무럭무럭 자라네요", "무럭무럭"),
        ("작은 몸짓 하나하나가 기적 같아요", "기적"),
        ("바로 이런 게 육아의 행복이겠죠", "행복"),
    };

    // 브릿지(리액션) 컷의 절제된 폴리 세트(테마 무관 · 잔잔).
    private static readonly string[] BridgeFoley = { "sparkle@R30,giggle@L20", "cute@C,pop@R25", "giggle@R35", "sparkle@L25,cute@C", "pop@L20", "giggle@R30,sparkle@L20" };

    private static List<StoryboardScene> BuildLongs(StoryboardProject project, BabyThemeDef theme)
    {
        const string size = "1280*720";   // 가로 브이로그
        var beats = theme.Beats;

        // 1) 컷 대본 조립(내레이션/골드/샷/폴리) — 인트로2 + 본문(비트×2) + 아웃트로2.
        var plan = new List<(string Voice, string Gold, string Shot, string Sfx)>
        {
            ($"오늘은 우리 아기의 {theme.Title} 브이로그", "브이로그",
                "35mm lens, warm soft morning window light, wide cinematic establishing shot of a cozy sunlit nursery where an adorable baby is about to start the day, gentle film grain, homely vlog atmosphere", "pluck@C,sparkle@R25"),
            ("천천히 그 하루를 함께 따라가 볼까요", "천천히",
                "50mm lens, soft diffused daylight, warm close-up portrait of a smiling baby looking gently at the camera, downy hair catching the light, heartwarming vlog intro", "cute@C,ding@L20"),
        };
        for (var i = 0; i < beats.Count; i++)
        {
            var b = beats[i];
            var bridge = BridgeLines[i % BridgeLines.Length];
            plan.Add((b.V, b.G, b.Shot, b.Sfx));                                                            // 본문 A: 핵심 액션(테마 고유 샷/폴리)
            plan.Add((bridge.Voice, bridge.Gold,
                "tender detail reaction insert, " + b.Shot + ", shallow focus soft cinematic bokeh, warm tones", BridgeFoley[i % BridgeFoley.Length]));   // 본문 B: 리액션 b-roll
        }
        plan.Add(("정말 사랑스러운 하루였어요", "사랑스러운",
            "50mm lens, golden hour warm backlight, cinematic wide shot of a content sleepy baby after a fun day, soft rim glow on chubby cheeks, cozy vlog outro", "sparkle@C,cute@L25"));
        plan.Add(("다음 이야기도 기대해 주세요 안녕", "다음",
            "85mm lens, dreamy soft backlight, tender close-up of a baby's peaceful smile drifting toward sleep, gentle bokeh, heartwarming vlog ending", "cute@R20,sparkle@L20"));

        // 2) 완만한 기승전결 긴장 곡선(22→peak≈75%→26) + 서사형 카메라 템포 아크.
        var n = plan.Count;
        var list = new List<StoryboardScene>(n);
        for (var i = 0; i < n; i++)
        {
            var (voice, gold, shot, sfx) = plan[i];
            list.Add(MakeScene(project, size, BuildPromptTempo(shot, LongsCameraTempo(i, n)), voice, gold,
                tension: LongsTension(i, n),
                enter: new VideoTransition(TransitionSafety.SafeXfade(LongTransitions[i % LongTransitions.Length]), LongXfadeMs(i)),
                foley: BuildFoley(sfx, LongClipMs),
                clipMs: LongClipMs));
        }
        return list;
    }

    /// <summary>완만한 빌드업 곡선 — 22에서 시작, 전체의 약 75% 지점에서 ~90 피크, 이후 부드럽게 하강해 아웃트로 ~26.</summary>
    private static int LongsTension(int i, int n)
    {
        if (n <= 1) return 40;
        double p = i / (double)(n - 1);
        const double pk = 0.75;
        double v = p <= pk ? (p / pk) : 1.0 - (p - pk) / (1.0 - pk) * 0.94;   // 상승 100% → 하강해 종단 ~0.06(=26)
        return (int)Math.Round(22 + Math.Clamp(v, 0, 1) * 68);               // 22 ~ 90
    }

    /// <summary>롱폼 서사 위치별 카메라 템포 — 도입 Slow Dolly → 상승 Handheld Push → 클라이맥스 Whip-Pan → 종결 Soft Pan-Out.</summary>
    private static string LongsCameraTempo(int i, int n)
    {
        double p = i / (double)Math.Max(1, n - 1);
        if (i <= 1) return "slow cinematic establishing dolly-in, gentle smooth motion, shallow depth of field, serene";
        if (i >= n - 2) return "soft slow pan-out, camera gently pulls back, warm tender motion, shallow depth of field";
        if (p < 0.42) return "steady medium dolly, gentle push-in, soft depth of field";
        if (p < 0.72) return "energetic handheld push-in, lively dynamic framing";
        return "fast whip-pan, punchy high-energy motion, dynamic";   // 클라이맥스 존(~0.72~0.9)
    }

    // 롱폼 전환 길이 — 성기고 부드럽게(600~900ms), 도입/종료는 더 길게.
    private static int LongXfadeMs(int i) => i == 0 ? 900 : 600 + (i % 3) * 120;

    // ══════════════════════ 공통 헬퍼 ══════════════════════
    private static StoryboardScene MakeScene(StoryboardProject project, string size, string prompt, string voice, string gold,
        int tension, VideoTransition enter, IReadOnlyList<FoleySpec> foley, int clipMs) => new()
    {
        Mode = string.IsNullOrWhiteSpace(project.AnchorImagePath) ? SceneMode.TextToVideo : SceneMode.ImageToVideo,
        Prompt = prompt,
        VoiceText = voice,
        Size = size,
        PresetTension = Math.Clamp(tension, 0, 100),
        PresetKineticWords = BabyThemePresetService.BuildKineticWords(voice, gold, clipMs),
        PresetFoley = foley.ToList(),
        EnterTransition = enter,
    };

    /// <summary>Shot(렌즈·조명·질감 authored) + 포맷 템포를 Wan 2.1 극사실 4K 프롬프트로 조립. Shot 이 미학을 온전히 담으므로 조명/렌즈를 덮어쓰지 않는다.</summary>
    private static string BuildPrompt(string shot, VideoFormat format) => BuildPromptTempo(shot, format == VideoFormat.Longs
        ? "slow cinematic push-in, gentle smooth camera motion, shallow depth of field, serene mood"
        : "snappy energetic framing, quick lively motion, punchy vivid colors, dynamic");

    /// <summary>Shot + 명시적 카메라 템포로 프롬프트 조립(롱폼 서사 아크용).</summary>
    private static string BuildPromptTempo(string shot, string tempo)
        => $"{shot}, {tempo}, 4K UHD, photorealistic, hyper-detailed";

    /// <summary>
    /// 비트의 콤마 구분 폴리 토큰을 컷 구간에 균등 분배 → authored <see cref="FoleySpec"/> 목록.
    ///   토큰은 <c>type</c> 또는 <c>type@pan</c>. pan 은 <c>L##</c>(좌)/<c>R##</c>(우 · ##=0~100%)/<c>C</c>(중앙) 또는 부호 있는 소수(-1~1).
    ///   pan 미지정 토큰은 좌(-0.35)→우(+0.35)→중앙(0) 순환(하위호환). type 은 항상 정제되어 SfxResolver/뱃지맵과 호환.
    /// </summary>
    private static IReadOnlyList<FoleySpec> BuildFoley(string sfx, int clipMs)
    {
        var tokens = (sfx ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<FoleySpec>(tokens.Length);
        if (tokens.Length == 0) return list;

        const int start = 150;
        var span = Math.Max(600, clipMs - 550);
        var autoPans = new[] { -0.35, 0.35, 0.0 };
        for (var i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            var at = tok.IndexOf('@');
            var type = (at >= 0 ? tok[..at] : tok).Trim();
            if (type.Length == 0) continue;
            var pan = at >= 0 ? ParsePan(tok[(at + 1)..]) : autoPans[i % autoPans.Length];
            var ts = start + (int)Math.Round(span * (i + 0.5) / tokens.Length);
            list.Add(new FoleySpec(type, Math.Clamp(ts, 0, clipMs - 200), Math.Clamp(pan, -1.0, 1.0)));
        }
        return list;
    }

    /// <summary>3D 팬 코드 파싱 — <c>C</c>=0, <c>L##</c>=-##/100, <c>R##</c>=+##/100, 또는 부호 있는 소수. 미상은 0(중앙).</summary>
    private static double ParsePan(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return 0;
        var head = char.ToUpperInvariant(s[0]);
        if (head == 'C') return 0;
        if ((head == 'L' || head == 'R') && int.TryParse(s.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct))
            return (head == 'L' ? -1 : 1) * Math.Clamp(pct, 0, 100) / 100.0;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }
}
