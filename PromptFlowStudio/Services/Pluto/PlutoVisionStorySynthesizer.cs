using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PromptFlowStudio.Services.Export;

namespace PromptFlowStudio.Services.Pluto;

/// <summary>👁 Pluto VLM 결선점 — 이미지 1장 + 지시문 → JSON 텍스트(Gemini Vision 등). 실패는 null.</summary>
public interface IPlutoVisionClient
{
    /// <summary>키 설정 등 비전 호출 가능 여부(false → 즉시 다음 단계).</summary>
    bool IsVisionAvailable { get; }

    Task<string?> DescribeImageJsonAsync(string system, string user, byte[] image, string mimeType, double temperature, CancellationToken ct);
}

/// <summary>📸 사진 분석 출처(페일오버 단계).</summary>
public enum PlutoVisionSource : byte
{
    /// <summary>👁 Gemini Vision(이미지 직접 판독).</summary>
    Vision = 0,
    /// <summary>🦙 로컬 AI 게이트웨이(메타데이터 기반 텍스트 추론).</summary>
    Gateway = 1,
    /// <summary>⚡ 결정론 규칙 분석기(파일명 · EXIF · 부모 메모).</summary>
    Heuristic = 2,
}

/// <summary>📸 사진 속 순간의 정서 분류(템플릿 · BGM · 자막 결정 키).</summary>
public enum PlutoVisionMoodKind : byte
{
    Smile = 0,
    Sleep = 1,
    Bath = 2,
    Play = 3,
    Celebration = 4,
    Outdoor = 5,
}

/// <summary>📸 사진 1장에서 판독한 시각 속성(표정 · 시선 · 조명 · 소품 · 행동).</summary>
public sealed class PlutoVisionAttributes
{
    public PlutoVisionMoodKind Kind { get; init; }
    public string Expression { get; init; } = "";
    public string Gaze { get; init; } = "";
    public string Lighting { get; init; } = "";
    public string Props { get; init; } = "";
    public string Action { get; init; } = "";
    public bool IsPet { get; init; }
    /// <summary>EXIF DateTimeOriginal(없으면 파일 수정 시각 · 실패 null).</summary>
    public DateTime? CapturedAt { get; init; }
}

/// <summary>📸 사진 1장 → 15초 3막 스토리보드(4컷 캔버스 · 앵커 선두 · 디렉터 광학 말미).</summary>
public sealed class PlutoVisionStoryPlan
{
    /// <summary>예: "호기심 가득한 미소", "평화로운 낮잠".</summary>
    public string DetectedMood { get; init; } = "";
    /// <summary>예: "따스한 오후 거실, 노란 튤립 소품".</summary>
    public string DetectedSetting { get; init; } = "";
    /// <summary>4컷(1: Act1 3초 전 · 2~3: Act2 클라이맥스 · 4: Act3 5초 후 여운).</summary>
    public IReadOnlyList<PlutoMilestoneScene> StoryboardScenes { get; init; } = Array.Empty<PlutoMilestoneScene>();
    public string SuggestedBgmTheme { get; init; } = "";
    public BabyBgmTrack SuggestedBgmTrack { get; init; }
    public string SuggestedSubtitle { get; init; } = "";
    public string PersonaConsistencyAnchor { get; init; } = "";
    public PlutoVisionAttributes Attributes { get; init; } = new();
    public PlutoVisionSource Source { get; init; } = PlutoVisionSource.Heuristic;
    /// <summary>주입된 디렉터 티어(미주입 0).</summary>
    public int DirectorLevel { get; init; }
    /// <summary>🎯 얼굴/눈 초점(정규화 0~1 · 원점 좌상단) — Gemini Vision 판독, 실패 시 삼분할 폴백(아기 0.50,0.38 · 반려 0.50,0.55).</summary>
    public (double X, double Y) DetectedFocalPoint { get; init; } = PlutoMotionCompiler.BabyFocal;
    /// <summary>🎥 4막 Ken Burns 카메라 궤적(<see cref="PlutoMotionCompiler.CompileFfmpegZoomPanFilter"/> 입력).</summary>
    public PlutoMotionTrajectory MotionPath { get; init; } = PlutoMotionTrajectory.Empty;

    /// <summary>🎬 스토리보드 일괄 적용 입력(<c>StoryboardViewModel.ApplyDirectorShots</c>).</summary>
    public PromptFlowStudio.Services.AI.DirectorShot[] ToDirectorShots()
    {
        var shots = new PromptFlowStudio.Services.AI.DirectorShot[StoryboardScenes.Count];
        for (var i = 0; i < shots.Length; i++)
        {
            var s = StoryboardScenes[i];
            shots[i] = new PromptFlowStudio.Services.AI.DirectorShot(s.SlotIndex, s.SceneTitle, s.Prompt, s.CameraMovement, s.DurationSec);
        }
        return shots;
    }
}

/// <summary>
/// 📸 Pluto Vision-to-Story — 정지 사진 1장 → 15초 3막 내러티브(Act1 셔터 3초 전 호기심 → Act2 사진 속 클라이맥스 → Act3 5초 후 여운).
///   ▸ 판독 페일오버: ① Gemini Vision(이미지) → ② 로컬 AI 게이트웨이(파일명 · EXIF · 메모 텍스트 추론) → ③ 결정론 규칙 분석기.
///   ▸ 활성 프로필 일관성 앵커(<see cref="PlutoIdentityVault"/>) 선두 · 디렉터 티어 광학(<see cref="IPlutoDirectorRankService"/>) 말미 — 전 컷 멱등.
///   ▸ 단계별 스키마 검증 · 누락 슬롯 템플릿 보강([부분 구제]) · 호출자 취소만 전파.
/// </summary>
public sealed class PlutoVisionStorySynthesizer
{
    public const int SceneCount = PlutoMilestoneOrchestrator.SceneCount;
    public const int ActCount = 3;
    public const long MaxImageBytes = 20L * 1024 * 1024;
    public const int ExifScanBytes = 128 * 1024;
    public const int MaxFieldLength = 80;
    public const int MaxSubtitleLength = 60;

    public static readonly TimeSpan DefaultVisionBudget = TimeSpan.FromSeconds(8);

    public const string VisionInstruction =
        "You are Pluto, a cinematic baby/pet short-film director. Study the photo and reconstruct a 15-second 3-act story around it. " +
        "Act 1 (slot 1, 0-4s): the curious movement 3 seconds BEFORE the shutter. Act 2 (slot 2 rise 4-8s, slot 3 peak 8-12s): the climactic smile/action captured in the photo. " +
        "Act 3 (slot 4, 12-15s): the warm, peaceful afterglow 5 seconds AFTER the shot. Keep the same subject, outfit, room and lighting in every slot. " +
        "Return JSON only: {\"kind\":\"smile|sleep|bath|play|celebration|outdoor\",\"mood\":\"<Korean short mood>\",\"setting\":\"<Korean setting and props>\"," +
        "\"expression\":\"\",\"gaze\":\"\",\"lighting\":\"\",\"props\":\"\",\"action\":\"\",\"bgm\":\"\",\"subtitle\":\"<Korean one-line emotional caption>\"," +
        "\"focal_x\":0.52,\"focal_y\":0.38," +
        "\"scenes\":[{\"slot\":1,\"title\":\"<Korean>\",\"prompt\":\"<English video prompt>\",\"camera\":\"\"}]} with exactly 4 scenes. " +
        "focal_x/focal_y are the normalized [0.0-1.0] image coordinates (origin top-left) of the subject's eyes/face center.";

    private const string GatewayPreamble = "No pixels are available — infer the photo from this metadata.\n";

    private readonly IPlutoVisionClient? _vision;
    private readonly IPlutoLlmClient? _llm;
    private readonly PlutoIdentityVault? _identity;
    private readonly IPlutoDirectorRankService? _director;

    /// <summary>비전 단계 예산(기본 8초).</summary>
    public TimeSpan? VisionBudget { get; init; }

    /// <summary>게이트웨이 단계 예산(null → 브리지 기본).</summary>
    public TimeSpan? LlmBudget { get; init; }

    public PlutoVisionStorySynthesizer(IPlutoVisionClient? vision = null, IPlutoLlmClient? llm = null, PlutoIdentityVault? identity = null, IPlutoDirectorRankService? director = null)
    {
        _vision = vision;
        _llm = llm;
        _identity = identity;
        _director = director;
    }

    // ══════════════════════ 🔤 규칙 분석 사전 ══════════════════════

    private static readonly (string Token, PlutoVisionMoodKind Kind)[] KindLexicon =
    {
        ("sleep", PlutoVisionMoodKind.Sleep), ("nap", PlutoVisionMoodKind.Sleep), ("잠", PlutoVisionMoodKind.Sleep), ("낮잠", PlutoVisionMoodKind.Sleep), ("새근", PlutoVisionMoodKind.Sleep),
        ("bath", PlutoVisionMoodKind.Bath), ("목욕", PlutoVisionMoodKind.Bath), ("거품", PlutoVisionMoodKind.Bath),
        ("birthday", PlutoVisionMoodKind.Celebration), ("cake", PlutoVisionMoodKind.Celebration), ("돌잔치", PlutoVisionMoodKind.Celebration), ("생일", PlutoVisionMoodKind.Celebration), ("100일", PlutoVisionMoodKind.Celebration), ("백일", PlutoVisionMoodKind.Celebration),
        ("park", PlutoVisionMoodKind.Outdoor), ("outdoor", PlutoVisionMoodKind.Outdoor), ("beach", PlutoVisionMoodKind.Outdoor), ("공원", PlutoVisionMoodKind.Outdoor), ("산책", PlutoVisionMoodKind.Outdoor), ("바다", PlutoVisionMoodKind.Outdoor),
        ("play", PlutoVisionMoodKind.Play), ("toy", PlutoVisionMoodKind.Play), ("ball", PlutoVisionMoodKind.Play), ("놀이", PlutoVisionMoodKind.Play), ("장난감", PlutoVisionMoodKind.Play), ("뒤집기", PlutoVisionMoodKind.Play), ("걸음", PlutoVisionMoodKind.Play),
        ("smile", PlutoVisionMoodKind.Smile), ("laugh", PlutoVisionMoodKind.Smile), ("웃", PlutoVisionMoodKind.Smile), ("미소", PlutoVisionMoodKind.Smile), ("방긋", PlutoVisionMoodKind.Smile),
    };

    private static readonly (string Token, string English, string Korean)[] PropLexicon =
    {
        ("tulip", "yellow tulips", "노란 튤립"), ("튤립", "yellow tulips", "노란 튤립"),
        ("flower", "fresh flowers", "생화"), ("꽃", "fresh flowers", "생화"),
        ("balloon", "pastel balloons", "파스텔 풍선"), ("풍선", "pastel balloons", "파스텔 풍선"),
        ("teddy", "a soft teddy bear", "곰 인형"), ("인형", "a soft plush toy", "애착 인형"),
        ("ball", "a colorful ball", "알록달록 공"), ("공놀이", "a colorful ball", "알록달록 공"),
        ("blanket", "a knitted blanket", "니트 담요"), ("이불", "a knitted blanket", "니트 담요"),
        ("cake", "a small celebration cake", "축하 케이크"), ("케이크", "a small celebration cake", "축하 케이크"),
        ("duck", "a rubber duck", "고무 오리"), ("오리", "a rubber duck", "고무 오리"),
    };

    private static readonly string[] PetTokens = { "dog", "puppy", "cat", "kitten", "pet", "강아지", "고양이", "댕댕", "냥" };

    private sealed class KindTemplate
    {
        public PlutoVisionMoodKind Kind;
        public string Mood = "", Setting = "", Expression = "", Gaze = "", Action = "", Bgm = "", Subtitle = "";
        public BabyBgmTrack Track;
        public string[] Titles = Array.Empty<string>();
        /// <summary>슬롯별 동작 문장({s}=주어 · {a}=행동 · {p}=소품 절).</summary>
        public string[] Beats = Array.Empty<string>();
    }

    private static readonly KindTemplate[] Templates =
    {
        new()
        {
            Kind = PlutoVisionMoodKind.Smile, Mood = "호기심 가득한 미소", Setting = "따스한 오후 거실", Expression = "bright open-mouthed smile",
            Gaze = "looking straight into the lens", Action = "reaching toward the camera", Bgm = "Twinkling Music Box Waltz", Track = BabyBgmTrack.MusicBox,
            Subtitle = "방긋, 세상에서 제일 예쁜 미소를 보여줘서 고마워",
            Titles = new[] { "3초 전의 호기심", "설렘의 빌드업", "사진 속 그 미소", "5초 후의 여운" },
            Beats = new[]
            {
                "{s} notices something just off-frame, tilting the head with wide curious eyes, tiny hands fidgeting{p}",
                "{s} leans in, cheeks lifting as a giggle builds, {a}{p}",
                "{s} bursts into the exact moment from the photo — {e}, {g}{p}",
                "{s} settles back with a soft content sigh, the smile fading into a peaceful glow{p}",
            },
        },
        new()
        {
            Kind = PlutoVisionMoodKind.Sleep, Mood = "평화로운 낮잠", Setting = "포근한 침실 오후 햇살", Expression = "relaxed sleeping face with a faint smile",
            Gaze = "eyes gently closed", Action = "curling up into a cozy nap", Bgm = "Soft Piano Lullaby", Track = BabyBgmTrack.Lullaby,
            Subtitle = "새근새근, 꿈속에서도 행복하길",
            Titles = new[] { "3초 전의 하품", "스르르 감기는 눈", "사진 속 꿀잠", "5초 후의 꿈결" },
            Beats = new[]
            {
                "{s} gives a tiny yawn, eyelids heavy, stretching little arms{p}",
                "{s} blinks slowly as drowsiness wins, {a}{p}",
                "{s} rests in the exact moment from the photo — {e}, {g}{p}",
                "{s} breathes slowly in deep sleep, chest rising softly as dust motes drift in the light{p}",
            },
        },
        new()
        {
            Kind = PlutoVisionMoodKind.Bath, Mood = "첨벙첨벙 목욕 놀이", Setting = "하얀 거품 가득한 욕실", Expression = "delighted splashing grin",
            Gaze = "watching the bubbles", Action = "splashing water with both hands", Bgm = "Bubbly Music Box", Track = BabyBgmTrack.MusicBox,
            Subtitle = "보글보글, 거품보다 더 몽글몽글한 너",
            Titles = new[] { "3초 전의 첫 물장구", "보글보글 거품", "사진 속 함박웃음", "5초 후의 수건 포옹" },
            Beats = new[]
            {
                "{s} pats the water surface curiously, droplets catching the light{p}",
                "{s} giggles as bubbles pop around, {a}{p}",
                "{s} lights up in the exact moment from the photo — {e}, {g}{p}",
                "{s} is wrapped in a soft hooded towel, cheeks rosy and calm{p}",
            },
        },
        new()
        {
            Kind = PlutoVisionMoodKind.Play, Mood = "신나는 놀이 시간", Setting = "햇살 드는 놀이 매트", Expression = "focused then triumphant grin",
            Gaze = "eyes locked on the toy", Action = "grabbing the toy with determination", Bgm = "Warm Acoustic Ukulele", Track = BabyBgmTrack.Acoustic,
            Subtitle = "해냈다! 오늘도 한 뼘 더 자랐어",
            Titles = new[] { "3초 전의 탐색", "영차영차 도전", "사진 속 성공의 순간", "5초 후의 뿌듯함" },
            Beats = new[]
            {
                "{s} spots the toy and scoots forward, eyes wide with curiosity{p}",
                "{s} wiggles and reaches with effort, {a}{p}",
                "{s} triumphs in the exact moment from the photo — {e}, {g}{p}",
                "{s} hugs the prize proudly and relaxes with a satisfied little smile{p}",
            },
        },
        new()
        {
            Kind = PlutoVisionMoodKind.Celebration, Mood = "축하의 반짝임", Setting = "파스텔 풍선 파티룸", Expression = "sparkling surprised smile",
            Gaze = "gazing at the glowing candle", Action = "clapping tiny hands", Bgm = "Warm Acoustic Celebration", Track = BabyBgmTrack.Acoustic,
            Subtitle = "태어나 줘서 고마워, 우리의 기적",
            Titles = new[] { "3초 전의 두근거림", "반짝이는 축하", "사진 속 기적의 순간", "5초 후의 포근한 품" },
            Beats = new[]
            {
                "{s} looks around at the decorations in wonder, fingers grasping the air{p}",
                "{s} bounces with excitement as family cheers softly, {a}{p}",
                "{s} shines in the exact moment from the photo — {e}, {g}{p}",
                "{s} nestles into a warm embrace, eyes half-closed in happy calm{p}",
            },
        },
        new()
        {
            Kind = PlutoVisionMoodKind.Outdoor, Mood = "바람 따라 산책", Setting = "초록빛 공원 잔디밭", Expression = "breezy joyful smile",
            Gaze = "following a fluttering leaf", Action = "toddling forward on the grass", Bgm = "Warm Acoustic Picnic", Track = BabyBgmTrack.Acoustic,
            Subtitle = "바람도, 햇살도 너를 반기는 오늘",
            Titles = new[] { "3초 전의 바람", "한 걸음 두 걸음", "사진 속 햇살 미소", "5초 후의 노을빛 여운" },
            Beats = new[]
            {
                "{s} feels a gentle breeze, hair fluttering, looking up curiously{p}",
                "{s} moves forward with wobbly delight, {a}{p}",
                "{s} beams in the exact moment from the photo — {e}, {g}{p}",
                "{s} sits down on the soft grass, watching the sky with peaceful eyes{p}",
            },
        },
    };

    // ══════════════════════ 🎬 공개 API ══════════════════════

    /// <summary>📸 사진 → 15초 3막 스토리 플랜. 비전/게이트웨이 실패·미설정 → 규칙 분석기(파일명 · EXIF · 메모 · 프로필 앵커).</summary>
    public async Task<PlutoVisionStoryPlan> SynthesizeFromImageAsync(string imagePath, string? parentMemo = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var memo = Clean(parentMemo, 120);
        var (anchor, profilePet) = await ResolveAnchorAsync(ct).ConfigureAwait(false);
        var (bytes, header) = await ReadImageAsync(imagePath, ct).ConfigureAwait(false);
        var attrs = AnalyzeHeuristic(imagePath, header, memo, profilePet);
        var tier = await ResolveTierAsync(ct).ConfigureAwait(false);
        var local = BuildLocal(attrs, anchor, memo);

        // ① 👁 Gemini Vision
        var visionAttempted = false;
        if (bytes is not null && ProbeVision(_vision))
        {
            visionAttempted = true;
            var json = await TryVisionAsync(bytes, MimeOf(imagePath), BuildUserRequest(attrs, memo, tier, imagePath, false), ct).ConfigureAwait(false);
            if (Merge(local, json, PlutoVisionSource.Vision, memo) is { } v) return Finish(v, tier);
        }

        // ② 🦙 로컬 AI 게이트웨이(텍스트 추론) — 비전이 이미 Gemini를 시도했다면 체인 1단계 생략
        var textStage = _llm;
        if (visionAttempted)
        {
            if (textStage is PlutoLlmFailoverClient chain) textStage = chain.Gateway;
            else if (ReferenceEquals(textStage, _vision)) textStage = null;
        }
        if (textStage is not null)
        {
            var text = await PlutoLlmBridge.TryCompleteAsync(textStage, VisionInstruction, GatewayPreamble + BuildUserRequest(attrs, memo, tier, imagePath, true), 0.8, ct, LlmBudget).ConfigureAwait(false);
            if (Merge(local, text, PlutoVisionSource.Gateway, memo) is { } g) return Finish(g, tier);
        }

        // ③ ⚡ 규칙 분석기
        return Finish(local, tier);
    }

    /// <summary>⚡ 결정론 규칙 분석 — 파일명 · 부모 메모 토큰 → 정서/소품/반려 여부, EXIF 촬영 시각 → 조명.</summary>
    public static PlutoVisionAttributes AnalyzeHeuristic(string? imagePath, ReadOnlySpan<byte> header, string? memo, bool profilePet = false)
    {
        var name = string.IsNullOrWhiteSpace(imagePath) ? "" : Path.GetFileNameWithoutExtension(imagePath);
        var text = (name + " " + (memo ?? "")).ToLowerInvariant();

        var kind = PlutoVisionMoodKind.Smile;
        for (var i = 0; i < KindLexicon.Length; i++)
            if (text.Contains(KindLexicon[i].Token, StringComparison.Ordinal)) { kind = KindLexicon[i].Kind; break; }

        var props = "";
        for (var i = 0; i < PropLexicon.Length; i++)
            if (text.Contains(PropLexicon[i].Token, StringComparison.Ordinal)) { props = PropLexicon[i].English; break; }

        var pet = profilePet;
        for (var i = 0; !pet && i < PetTokens.Length; i++)
            if (text.Contains(PetTokens[i], StringComparison.Ordinal)) pet = true;

        var captured = TryReadExifDate(header);
        if (captured is null && !string.IsNullOrWhiteSpace(imagePath))
        {
            try { if (File.Exists(imagePath)) captured = File.GetLastWriteTime(imagePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }

        var t = TemplateOf(kind);
        return new PlutoVisionAttributes
        {
            Kind = kind,
            Expression = t.Expression,
            Gaze = t.Gaze,
            Lighting = LightingFor(captured, kind),
            Props = props,
            Action = t.Action,
            IsPet = pet,
            CapturedAt = captured,
        };
    }

    /// <summary>⚡ 로컬 결정론 플랜(앵커 선두 · 광학 미적용) — 같은 입력 → 같은 결과.</summary>
    public static PlutoVisionStoryPlan BuildLocal(PlutoVisionAttributes attrs, string? anchor, string? memo = null)
    {
        var t = TemplateOf(attrs.Kind);
        var a = string.IsNullOrWhiteSpace(anchor)
            ? (attrs.IsPet ? PlutoMilestoneOrchestrator.DefaultPetAnchor : PlutoMilestoneOrchestrator.DefaultBabyAnchor)
            : anchor.Trim();
        var subject = attrs.IsPet ? "the pet" : "the baby";
        var propClause = attrs.Props.Length > 0 ? ", " + attrs.Props + " nearby" : "";
        var note = Clean(memo, 120);
        var scenes = new PlutoMilestoneScene[SceneCount];
        for (var i = 0; i < SceneCount; i++)
        {
            var body = t.Beats[i]
                .Replace("{s}", subject, StringComparison.Ordinal)
                .Replace("{a}", attrs.Action, StringComparison.Ordinal)
                .Replace("{e}", attrs.Expression, StringComparison.Ordinal)
                .Replace("{g}", attrs.Gaze, StringComparison.Ordinal)
                .Replace("{p}", propClause, StringComparison.Ordinal);
            body += ", " + attrs.Lighting + ", same outfit and room as the reference photo";
            if (note.Length > 0) body += ", " + note;
            scenes[i] = NewScene(i + 1, ActLabel(i + 1) + t.Titles[i], PlutoIdentityVault.ApplyAnchor(body, a), PlutoMilestoneOrchestrator.SlotCameras[i]);
        }
        var setting = t.Setting + (attrs.Props.Length > 0 ? ", " + KoreanProp(attrs.Props) + " 소품" : "");
        return new PlutoVisionStoryPlan
        {
            DetectedMood = t.Mood,
            DetectedSetting = setting,
            StoryboardScenes = scenes,
            SuggestedBgmTheme = t.Bgm,
            SuggestedBgmTrack = t.Track,
            SuggestedSubtitle = t.Subtitle,
            PersonaConsistencyAnchor = a,
            Attributes = attrs,
            Source = PlutoVisionSource.Heuristic,
            DetectedFocalPoint = PlutoMotionCompiler.FallbackFocal(attrs.IsPet),
            MotionPath = PlutoMotionCompiler.SynthesizeFourAct(PlutoMotionCompiler.FallbackFocal(attrs.IsPet)),
        };
    }

    /// <summary>슬롯 → 막(1: Act1 · 2~3: Act2 · 4: Act3).</summary>
    public static int ActOf(int slot) => slot <= 1 ? 1 : slot >= SceneCount ? 3 : 2;

    /// <summary>🎬 디렉터 광학 말미 결합(멱등 · null 티어 → 원본).</summary>
    public static PlutoVisionStoryPlan ApplyOptics(PlutoVisionStoryPlan plan, PlutoDirectorTier? tier)
    {
        if (tier is null || plan.StoryboardScenes.Count == 0) return plan;
        var scenes = new PlutoMilestoneScene[plan.StoryboardScenes.Count];
        for (var i = 0; i < scenes.Length; i++)
        {
            var s = plan.StoryboardScenes[i];
            scenes[i] = NewScene(s.SlotIndex, s.SceneTitle, PlutoDirectorRankService.Enrich(s.Prompt, tier), s.CameraMovement);
        }
        return With(plan, scenes, plan.Source, tier.Level);
    }

    // ══════════════════════ 🔧 내부 ══════════════════════

    private PlutoVisionStoryPlan Finish(PlutoVisionStoryPlan plan, PlutoDirectorTier? tier) => ApplyOptics(plan, tier);

    private async Task<string?> TryVisionAsync(byte[] bytes, string mime, string user, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(VisionBudget ?? DefaultVisionBudget);
        try
        {
            var call = _vision!.DescribeImageJsonAsync(VisionInstruction, user, bytes, mime, 0.6, cts.Token);
            var done = await Task.WhenAny(call, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token)).ConfigureAwait(false);
            if (done != call)
            {
                ct.ThrowIfCancellationRequested();
                _ = call.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);   // 토큰 무시 클라이언트 — 관측만
                return null;
            }
            return await call.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.VisionStory] vision: " + ex.Message); return null; }   // [부분 구제] 다음 단계
        finally
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }   // 대기 Delay · 토큰 존중 클라이언트 정리
        }
    }

    /// <summary>비전/게이트웨이 JSON 검증 병합 — 속성 · 슬롯별 채택, 무효 슬롯은 로컬 유지. 유효 필드 0 → null.</summary>
    internal static PlutoVisionStoryPlan? Merge(PlutoVisionStoryPlan local, string? json, PlutoVisionSource source, string? memo = null)
    {
        using var doc = PlutoLlmBridge.TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var root = doc.RootElement;

        var la = local.Attributes;
        var kind = ParseKind(PlutoLlmBridge.Str(root, "kind", 20), la.Kind);
        var t = TemplateOf(kind);
        var kindChanged = kind != la.Kind;
        var attrs = new PlutoVisionAttributes
        {
            Kind = kind,
            Expression = Pick(root, "expression", kindChanged ? t.Expression : la.Expression),
            Gaze = Pick(root, "gaze", kindChanged ? t.Gaze : la.Gaze),
            Lighting = Pick(root, "lighting", la.Lighting),
            Props = Pick(root, "props", la.Props),
            Action = Pick(root, "action", kindChanged ? t.Action : la.Action),
            IsPet = la.IsPet,
            CapturedAt = la.CapturedAt,
        };
        var attrsChanged = kindChanged
            || !string.Equals(attrs.Expression, la.Expression, StringComparison.Ordinal)
            || !string.Equals(attrs.Gaze, la.Gaze, StringComparison.Ordinal)
            || !string.Equals(attrs.Lighting, la.Lighting, StringComparison.Ordinal)
            || !string.Equals(attrs.Props, la.Props, StringComparison.Ordinal)
            || !string.Equals(attrs.Action, la.Action, StringComparison.Ordinal);
        var baseline = attrsChanged ? BuildLocal(attrs, local.PersonaConsistencyAnchor, memo) : local;   // 판독 속성으로 템플릿 슬롯 재구성

        var used = attrsChanged;
        var scenes = new PlutoMilestoneScene[SceneCount];
        for (var i = 0; i < SceneCount; i++) scenes[i] = baseline.StoryboardScenes[i];
        if (root.TryGetProperty("scenes", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            var pos = 0;
            foreach (var e in arr.EnumerateArray())
            {
                pos++;
                if (e.ValueKind != JsonValueKind.Object) continue;
                var slot = e.TryGetProperty("slot", out var sv) && sv.ValueKind == JsonValueKind.Number && sv.TryGetInt32(out var n) ? n : pos;
                if (slot < 1 || slot > SceneCount) continue;
                var prompt = PlutoLlmBridge.Str(e, "prompt", PlutoMilestoneOrchestrator.MaxPromptLength);
                if (prompt.Length == 0) continue;
                var b = baseline.StoryboardScenes[slot - 1];
                var title = PlutoLlmBridge.Str(e, "title", PlutoMilestoneOrchestrator.MaxTitleLength);
                var camera = PlutoLlmBridge.Str(e, "camera", 60);
                scenes[slot - 1] = NewScene(slot,
                    title.Length > 0 ? ActLabel(slot) + title : b.SceneTitle,
                    PlutoIdentityVault.ApplyAnchor(prompt, local.PersonaConsistencyAnchor),
                    camera.Length > 0 ? camera : b.CameraMovement);
                used = true;
            }
        }

        var mood = PlutoLlmBridge.Str(root, "mood", MaxFieldLength);
        var setting = PlutoLlmBridge.Str(root, "setting", MaxFieldLength);
        var subtitle = PlutoLlmBridge.Str(root, "subtitle", MaxSubtitleLength);
        var bgm = PlutoLlmBridge.Str(root, "bgm", MaxFieldLength);
        if (mood.Length > 0 || setting.Length > 0 || subtitle.Length > 0 || bgm.Length > 0) used = true;
        // 🎯 초점 좌표 — 픽셀을 본 비전 응답만 채택(게이트웨이는 텍스트 추론 → 삼분할 폴백 유지). 범위 밖/비수치 → 폴백.
        var focal = baseline.DetectedFocalPoint;
        var motion = baseline.MotionPath;
        if (source == PlutoVisionSource.Vision && TryFocal(root, out var vf))
        {
            focal = vf;
            motion = PlutoMotionCompiler.SynthesizeFourAct(vf);
            used = true;
        }
        if (!used) return null;

        return new PlutoVisionStoryPlan
        {
            DetectedMood = mood.Length > 0 ? mood : baseline.DetectedMood,
            DetectedSetting = setting.Length > 0 ? setting : baseline.DetectedSetting,
            StoryboardScenes = scenes,
            SuggestedBgmTheme = bgm.Length > 0 ? bgm : baseline.SuggestedBgmTheme,
            SuggestedBgmTrack = bgm.Length > 0 ? TrackFor(bgm, baseline.SuggestedBgmTrack) : baseline.SuggestedBgmTrack,
            SuggestedSubtitle = subtitle.Length > 0 ? subtitle : baseline.SuggestedSubtitle,
            PersonaConsistencyAnchor = local.PersonaConsistencyAnchor,
            Attributes = attrs,
            Source = source,
            DetectedFocalPoint = focal,
            MotionPath = motion,
        };
    }

    /// <summary>🎯 <c>focal_x</c>/<c>focal_y</c> 스키마 검증 — 둘 다 유한 수치 · [0,1] 범위일 때만 true.</summary>
    internal static bool TryFocal(JsonElement root, out (double X, double Y) focal)
    {
        focal = default;
        if (!TryUnit(root, "focal_x", out var x) || !TryUnit(root, "focal_y", out var y)) return false;
        focal = (x, y);
        return true;
    }

    private static bool TryUnit(JsonElement root, string name, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var e)) return false;
        var ok = e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetDouble(out value),
            JsonValueKind.String => double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
        return ok && double.IsFinite(value) && value >= 0.0 && value <= 1.0;
    }

    private static string Pick(JsonElement root, string name, string fallback)
    {
        var v = PlutoLlmBridge.Str(root, name, MaxFieldLength);
        return v.Length > 0 ? v : fallback;
    }

    private static PlutoVisionMoodKind ParseKind(string s, PlutoVisionMoodKind fallback)
    {
        if (s.Length == 0) return fallback;
        return Enum.TryParse<PlutoVisionMoodKind>(s, true, out var k) && Enum.IsDefined(k) ? k : fallback;
    }

    private static BabyBgmTrack TrackFor(string bgm, BabyBgmTrack fallback)
    {
        if (bgm.Contains("lullaby", StringComparison.OrdinalIgnoreCase) || bgm.Contains("piano", StringComparison.OrdinalIgnoreCase) || bgm.Contains("자장", StringComparison.Ordinal)) return BabyBgmTrack.Lullaby;
        if (bgm.Contains("music box", StringComparison.OrdinalIgnoreCase) || bgm.Contains("musicbox", StringComparison.OrdinalIgnoreCase) || bgm.Contains("오르골", StringComparison.Ordinal)) return BabyBgmTrack.MusicBox;
        if (bgm.Contains("acoustic", StringComparison.OrdinalIgnoreCase) || bgm.Contains("ukulele", StringComparison.OrdinalIgnoreCase) || bgm.Contains("guitar", StringComparison.OrdinalIgnoreCase) || bgm.Contains("어쿠스틱", StringComparison.Ordinal)) return BabyBgmTrack.Acoustic;
        return fallback;
    }

    private static string BuildUserRequest(PlutoVisionAttributes attrs, string memo, PlutoDirectorTier? tier, string imagePath, bool includeHeuristics)
    {
        var sb = new StringBuilder(512);
        sb.Append("Subject: ").Append(attrs.IsPet ? "pet" : "baby").Append(" (identity anchor is prepended automatically — do not repeat it)\n");
        if (memo.Length > 0) sb.Append("Parent memo: ").Append(memo).Append('\n');
        if (includeHeuristics)
        {
            var name = string.IsNullOrWhiteSpace(imagePath) ? "" : Clean(Path.GetFileNameWithoutExtension(imagePath), 60);
            if (name.Length > 0) sb.Append("Photo file name: ").Append(name).Append('\n');
            if (attrs.CapturedAt is { } at) sb.Append("Captured at: ").Append(at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("Heuristic guess: kind=").Append(attrs.Kind.ToString().ToLowerInvariant()).Append(", lighting=").Append(attrs.Lighting);
            if (attrs.Props.Length > 0) sb.Append(", props=").Append(attrs.Props);
            sb.Append('\n');
        }
        sb.Append("Slot cameras: ");
        for (var i = 0; i < SceneCount; i++)
        {
            if (i > 0) sb.Append(" | ");
            sb.Append(PlutoMilestoneOrchestrator.TimeRangeOf(i + 1)).Append(' ').Append(PlutoMilestoneOrchestrator.SlotCameras[i]);
        }
        if (tier is not null) sb.Append('\n').Append(PlutoDirectorRankService.LlmDirective(tier));
        return sb.ToString();
    }

    private async Task<(string Anchor, bool IsPet)> ResolveAnchorAsync(CancellationToken ct)
    {
        if (_identity is null) return ("", false);
        try
        {
            var p = await _identity.GetActiveProfileAsync(ct).ConfigureAwait(false);
            return p is null ? ("", false) : (p.GetConsistencyAnchor(), p.IsPet);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.VisionStory] identity fallback: " + ex.Message); return ("", false); }
    }

    private async Task<PlutoDirectorTier?> ResolveTierAsync(CancellationToken ct)
    {
        if (_director is null) return null;
        try
        {
            await _director.EnsureLoadedAsync(ct).ConfigureAwait(false);
            return _director.GetCurrentTier();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Pluto.VisionStory] director fallback: " + ex.Message); return null; }
    }

    /// <summary>이미지 읽기 — 상한 이내면 전체(비전용) · 초과면 EXIF 헤더만. 실패 → (null, 빈 헤더).</summary>
    private static async Task<(byte[]? Bytes, byte[] Header)> ReadImageAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return (null, Array.Empty<byte>());
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length == 0) return (null, Array.Empty<byte>());
            // 🔓 FileShare.ReadWrite — 사용자 원본(편집기·동기화 클라이언트 점유 중)을 잠그지 않고 읽기.
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous);
            if (fi.Length <= MaxImageBytes)
            {
                var buf = new byte[fi.Length];
                var read = await fs.ReadAtLeastAsync(buf, buf.Length, false, ct).ConfigureAwait(false);   // 읽는 중 축소 대비
                if (read == 0) return (null, Array.Empty<byte>());
                var all = read == buf.Length ? buf : buf.AsSpan(0, read).ToArray();
                return (all, all);
            }
            var head = new byte[ExifScanBytes];
            var n = await fs.ReadAtLeastAsync(head, head.Length, false, ct).ConfigureAwait(false);
            return (null, n == head.Length ? head : head.AsSpan(0, n).ToArray());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("[Pluto.VisionStory] read: " + ex.Message);
            return (null, Array.Empty<byte>());
        }
    }

    /// <summary>EXIF ASCII 날짜("YYYY:MM:DD HH:MM:SS") 첫 일치 — 앞 128KB만 스캔. 없으면 null.</summary>
    public static DateTime? TryReadExifDate(ReadOnlySpan<byte> data)
    {
        var limit = Math.Min(data.Length, ExifScanBytes) - 19;
        for (var i = 0; i <= limit; i++)
        {
            if (data[i + 4] != (byte)':' || data[i + 7] != (byte)':' || data[i + 10] != (byte)' ' || data[i + 13] != (byte)':' || data[i + 16] != (byte)':') continue;
            if (!Digits(data, i, 4) || !Digits(data, i + 5, 2) || !Digits(data, i + 8, 2) || !Digits(data, i + 11, 2) || !Digits(data, i + 14, 2) || !Digits(data, i + 17, 2)) continue;
            int y = Num(data, i, 4), mo = Num(data, i + 5, 2), d = Num(data, i + 8, 2), h = Num(data, i + 11, 2), mi = Num(data, i + 14, 2), s = Num(data, i + 17, 2);
            if (y < 1990 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo) || h > 23 || mi > 59 || s > 59) continue;
            return new DateTime(y, mo, d, h, mi, s, DateTimeKind.Unspecified);
        }
        return null;
    }

    private static bool Digits(ReadOnlySpan<byte> d, int at, int len)
    {
        for (var k = 0; k < len; k++) if ((uint)(d[at + k] - '0') > 9) return false;
        return true;
    }

    private static int Num(ReadOnlySpan<byte> d, int at, int len)
    {
        var v = 0;
        for (var k = 0; k < len; k++) v = v * 10 + (d[at + k] - '0');
        return v;
    }

    private static string LightingFor(DateTime? at, PlutoVisionMoodKind kind)
    {
        if (kind == PlutoVisionMoodKind.Bath) return "soft diffused bathroom light with gentle steam";
        if (at is not { } t) return "warm soft window light";
        return t.Hour switch
        {
            >= 5 and < 10 => "fresh morning window light",
            >= 10 and < 16 => "bright natural afternoon daylight",
            >= 16 and < 19 => "warm golden-hour sunlight",
            _ => "cozy warm lamp light at night",
        };
    }

    private static string KoreanProp(string english)
    {
        for (var i = 0; i < PropLexicon.Length; i++)
            if (string.Equals(PropLexicon[i].English, english, StringComparison.OrdinalIgnoreCase)) return PropLexicon[i].Korean;
        return english;
    }

    private static string ActLabel(int slot) => "Act " + ActOf(slot).ToString(CultureInfo.InvariantCulture) + " · ";

    private static KindTemplate TemplateOf(PlutoVisionMoodKind kind)
    {
        for (var i = 0; i < Templates.Length; i++) if (Templates[i].Kind == kind) return Templates[i];
        return Templates[0];
    }

    private static PlutoMilestoneScene NewScene(int slot, string title, string prompt, string camera) => new()
    {
        SlotIndex = slot,
        TimeRange = PlutoMilestoneOrchestrator.TimeRangeOf(slot),
        SceneTitle = title,
        Prompt = prompt,
        CameraMovement = camera,
        RecommendedBgm = "",
    };

    private static PlutoVisionStoryPlan With(PlutoVisionStoryPlan p, IReadOnlyList<PlutoMilestoneScene> scenes, PlutoVisionSource source, int directorLevel) => new()
    {
        DetectedMood = p.DetectedMood,
        DetectedSetting = p.DetectedSetting,
        StoryboardScenes = scenes,
        SuggestedBgmTheme = p.SuggestedBgmTheme,
        SuggestedBgmTrack = p.SuggestedBgmTrack,
        SuggestedSubtitle = p.SuggestedSubtitle,
        PersonaConsistencyAnchor = p.PersonaConsistencyAnchor,
        Attributes = p.Attributes,
        Source = source,
        DirectorLevel = directorLevel,
        DetectedFocalPoint = p.DetectedFocalPoint,
        MotionPath = p.MotionPath,
    };

    private static bool ProbeVision(IPlutoVisionClient? v)
    {
        if (v is null) return false;
        try { return v.IsVisionAvailable; } catch { return false; }
    }

    private static string MimeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        ".heic" => "image/heic",
        _ => "image/jpeg",
    };

    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var c = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return c.Length > max ? c[..max].TrimEnd() : c;
    }
}
