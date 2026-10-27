using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using PromptFlowStudio.Services.Media;   // KineticCaptionWord/FoleySpec/VideoTransition (프리셋 authored 연출 데이터)
using PromptFlowStudio.Services.Weather; // v4.5.0 날씨/환경 테마 구성(WeatherThemeConfig)

namespace PromptFlowStudio.Services.Storyboard;

/// <summary>개별 컷의 생성 상태(대기/생성중/다운로드/완료/실패/취소).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SceneStatus>))]
public enum SceneStatus { Pending, Generating, Downloading, Completed, Failed, Canceled }

/// <summary>컷 생성 방식 — 텍스트→영상(t2v) 또는 이미지→영상(i2v).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SceneMode>))]
public enum SceneMode { TextToVideo, ImageToVideo }

/// <summary>
/// [PromptFlowStudio] 스토리보드의 개별 컷(Scene) — 편집값(프롬프트/이미지/해상도)과 런타임 상태(진행률/결과)를 함께 담는다.
///   ⚠ 실행 중 상태가 변하는 '살아있는' 엔티티라 record 가 아니라 mutable class 로 둔다(JobManager 가 제자리 갱신).
/// </summary>
public sealed class StoryboardScene
{
    /// <summary>컷 식별자(8자리) — 다운로드 폴더/키에 사용.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    // ── 편집값 ──
    public SceneMode Mode { get; set; } = SceneMode.TextToVideo;
    public string Prompt { get; set; } = "";
    public string Size { get; set; } = "1280*720";

    /// <summary>v16.6.0: 컷 네거티브 프롬프트(테마 authored · 역방향 모션 방지 포함). null/빈 값이면 엔진 기본.</summary>
    public string? NegativePrompt { get; set; }

    /// <summary>나레이션/대사 텍스트(선택). 있으면 Edge TTS 음성 + 숏츠 자막(drawtext)으로 병합.</summary>
    public string VoiceText { get; set; } = "";

    /// <summary>립싱크 적용 여부 — 켜면 병합 전 LatentSync 로 이 컷 인물의 입모양을 나레이션 오디오에 맞춘다(엔진 설치 + 나레이션 있을 때).</summary>
    public bool UseLipSync { get; set; }

    /// <summary>v16.20.0 컷 미세 표정 프리셋 — 제출 시 <see cref="FacialEmotionPuppeteerEngine"/> 가 표정 구문·얼굴 보호 네거티브를 비영속 주입(Neutral = 미적용).</summary>
    public FacialEmotion Emotion { get; set; }

    /// <summary>v16.20.0 표정 강도(0~1 · 아기 피사체는 0.75 상한 자동 적용).</summary>
    public double EmotionIntensity { get; set; } = FacialEmotionPuppeteerEngine.DefaultIntensity;

    /// <summary>v16.22.0 캐릭터 정합성 신뢰도(0~100) — 잠금 프로필 얼굴 특징 대비. 미측정 null.</summary>
    public double? IdentityConfidence { get; set; }

    // ── AI 감독 자율 검수(Gemini) 결과 ──
    /// <summary>AI 감독 품질 점수(0~100). 자율 검수 모드에서 컷 프레임을 Gemini 로 평가한 결과. 미검수면 null.</summary>
    public int? QualityScore { get; set; }
    /// <summary>AI 감독 평가 한 줄(한국어). 미검수면 null.</summary>
    public string? QAReason { get; set; }
    /// <summary>자율 보정(Re-roll)으로 프롬프트를 고쳐 재생성된 컷인지.</summary>
    public bool IsAutoHealed { get; set; }

    // ── 네이티브 비디오 시간축 분석(Gemini mp4 전체) 결과 ──
    /// <summary>비디오 시간축 점수(0~100) — 모션 자연스러움·프레임 일관성·물리 위반 종합. 미분석이면 null.</summary>
    public int? VideoTemporalScore { get; set; }
    /// <summary>비디오 시간축 분석 한 줄 평(한국어). 미분석이면 null.</summary>
    public string? TemporalReason { get; set; }

    // ── 감정선 분석(Gemini) 결과 ──
    /// <summary>컷 감정 긴장도(0~100). 동적 BGM 오케스트레이션 산출값. 미분석이면 null.</summary>
    public int? EmotionTension { get; set; }
    /// <summary>컷 지배 분위기(Cinematic/Suspense/Upbeat 등). 미분석이면 null.</summary>
    public string? EmotionMood { get; set; }

    // ── 프리셋 authored 연출 데이터(원클릭 테마 · Gemini 없이 오프라인 자동완성 · 병합이 이 값을 우선 사용) ──
    /// <summary>이 컷의 키네틱 자막 단어(밀리초 타임코드). 프리셋이 채워 두면 병합이 Gemini 대신 이 값으로 골드 팝 자막 번인.</summary>
    public List<KineticCaptionWord>? PresetKineticWords { get; set; }
    /// <summary>이 컷의 폴리 SFX 스펙(타입/시점/팬). 병합 시 SfxResolver 로 파일 해결해 공간 믹싱.</summary>
    public List<FoleySpec>? PresetFoley { get; set; }
    /// <summary>이 컷의 긴장도(0~100). 감정선 동적 BGM(볼륨/덕킹) 산출 근거. 프리셋 authored.</summary>
    public int? PresetTension { get; set; }
    /// <summary>이 컷으로 <b>들어오는</b> 스마트 트랜지션(이전 컷과의 경계 xfade). scene[0]은 루프(마지막→첫) 매치컷 메타.</summary>
    public VideoTransition? EnterTransition { get; set; }

    // ── 2번 워크플로우 컷 덱(StoryboardCut) 동기화 필드 — StoryboardViewModel.AttachProject 양방향 결선 ──
    /// <summary>컷 카메라 모션(컷 덱 편집값). null 이면 위치 기반 기본값.</summary>
    public string? CameraMotion { get; set; }
    /// <summary>컷 렌더링 시드(컷 덱 편집값). null 이면 미지정.</summary>
    public long? Seed { get; set; }
    /// <summary>컷 길이(초 · 컷 덱 편집값). null 이면 기본 5초.</summary>
    public float? DurationSec { get; set; }
    /// <summary>컷 덱 전환 효과(이전 컷 → 이 컷).</summary>
    public TransitionType CutTransition { get; set; }

    /// <summary>i2v 참조 이미지 로컬 경로(업로드용). Mode=ImageToVideo 일 때 사용.</summary>
    public string? ImagePath { get; set; }
    /// <summary>미리보기 썸네일 URL(가상호스트).</summary>
    public string? ThumbUrl { get; set; }
    /// <summary>표시용 원본 파일명.</summary>
    public string? ImageName { get; set; }

    // ── 런타임 상태(JobManager 가 갱신) ──
    public SceneStatus Status { get; set; } = SceneStatus.Pending;
    public int Percent { get; set; }
    public string StatusText { get; set; } = "대기";
    /// <summary>재생 URL — 로컬 저장 성공 시 가상호스트, 실패 시 원격 폴백.</summary>
    public string? VideoUrl { get; set; }
    /// <summary>추출된 썸네일(.jpg) URL(있을 때만). 카드에서 video 대신 img 로 표시.</summary>
    public string? PosterUrl { get; set; }
    /// <summary>[v5.7.0] 추출된 포스터의 로컬 파일 경로(pHash 정체성 대조·데스크톱 디코드용 · URL 과 별개).</summary>
    public string? PosterPath { get; set; }
    /// <summary>로컬 저장 절대경로(성공 시).</summary>
    public string? LocalPath { get; set; }
    public string? Error { get; set; }

    /// <summary>이미지→영상 컷인지.</summary>
    [JsonIgnore] public bool IsI2v => Mode == SceneMode.ImageToVideo;
    /// <summary>더 이상 생성할 필요 없는 종료 상태인지.</summary>
    [JsonIgnore] public bool IsTerminal => Status is SceneStatus.Completed or SceneStatus.Failed or SceneStatus.Canceled;
}

/// <summary>
/// [PromptFlowStudio] 스토리보드 프로젝트 — 여러 컷(Scene)을 하나의 영상 흐름으로 묶는다.
///   <see cref="FinalVideoPath"/> 는 병합 결과 자리(향후 ffmpeg 확장) — 현재는 미구현(완성 컷 이어보기로 대체).
/// </summary>
public sealed class StoryboardProject
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Title { get; set; } = "새 스토리보드";

    /// <summary>컷 목록(순서 = 영상 순서). init 세터 — 역직렬화 시 채워지고, 이후 리스트 내용은 자유롭게 편집(Add/Remove).</summary>
    public List<StoryboardScene> Scenes { get; init; } = new();

    /// <summary>병합된 최종 영상 경로(모든 컷 완료 시 ffmpeg 자동 병합으로 채워짐).</summary>
    public string? FinalVideoPath { get; set; }

    /// <summary>최종 병합 해상도 "가로x세로"(예: 1280x720, 1920x1080, 1080x1920 쇼츠). VideoMergeService 의 scale/pad 대상.</summary>
    public string ExportResolution { get; set; } = "1280x720";

    /// <summary>선택적 배경음악(BGM) 로컬 경로. 지정 시 병합에 오디오 트랙으로 합성(-shortest · aac 128k · 짧으면 반복).</summary>
    public string? BgmPath { get; set; }

    /// <summary>부드러운 전환 — 켜면 컷마다 0.5초 페이드 인/아웃(dip-to-black) + BGM 마지막 1초 페이드아웃. 기본 false.</summary>
    public bool UseFadeEffect { get; set; }

    /// <summary>오디오 덕킹 — 켜면 나레이션(TTS) 음성이 나오는 구간에 BGM 볼륨을 자동으로 낮춘다(방송용 믹싱). 기본 true.
    ///   BGM+나레이션이 동시에 있을 때만 적용(sidechaincompress). BGM 단독/나레이션 단독은 기존 흐름.</summary>
    public bool EnableAudioDucking { get; set; } = true;

    /// <summary>BGM 기본 음량(0.0~1.0) — 나레이션과 믹스될 때의 기준 레벨. 덕킹 시 이 레벨에서 나레이션 구간만 더 감쇄. 기본 0.3.
    ///   ⚠ BGM 단독 재생 시엔 적용하지 않는다(단독은 원음 유지 · 기존 흐름).</summary>
    public double BgmVolume { get; set; } = 0.3;

    /// <summary>숏츠 커버(썸네일)용 대형 문구. 포스터(.jpg) 위에 drawtext 로 합성해 cover.jpg 를 만든다. 줄바꿈으로 여러 줄 가능(자동 줄맞춤).</summary>
    public string CoverTitle { get; set; } = "";

    /// <summary>오프라인 TTS 우선 — 켜면 나레이션을 로컬 엔진(Kokoro/F5-TTS)으로 합성(설치 시). 미설치/실패면 온라인 Edge TTS 로 폴백. 기본 false.</summary>
    public bool UseOfflineTts { get; set; }

    /// <summary>메인 캐릭터/스타일 앵커 이미지 로컬 경로(선택). 설정 시 모든 컷을 이 이미지 기반 I2V 로 생성해 등장인물·미술 스타일 일관성 유지.
    ///   컷 자체 참조 이미지가 있으면 그쪽이 우선. <see cref="CharacterAnchorService"/> 가 바인딩.</summary>
    public string? AnchorImagePath { get; set; }

    /// <summary>v16.22.0 고정 캐릭터 스냅숏(얼굴·의상/소품·스타일·시드·음성) — 제출 시 비영속 주입. null 이면 미적용(역호환).</summary>
    public CharacterLockSnapshot? CharacterLock { get; set; }

    /// <summary>[v5.6] 플로우 노드가 자산화한 갤러리 항목(추출 프레임·병합 롱폼 등). 기본 빈 목록 → 구 project.json 역호환.</summary>
    public List<GalleryAsset> Gallery { get; set; } = new();

    /// <summary>브랜드 워터마크(채널 로고) 이미지 로컬 경로(선택). 설정 시 병합 마지막 단계에서 우측 상단에 오버레이 합성(알파 유지).
    ///   ⚠ 자막/업스케일 이후에 얹으므로 로고가 선명하게 유지된다. 미설정/파일없음이면 무시(Graceful).</summary>
    public string? WatermarkImagePath { get; set; }

    /// <summary>스마트 사운드 FX — 켜면 컷 전환 지점마다 숏츠 전용 효과음(Whoosh 등)을 자동 믹싱한다.
    ///   효과음은 <c>%LocalAppData%\PromptFlowStudio\Sfx\</c>(없으면 앱번들 Sfx)에서 탐색 — 파일 없으면 무시(Graceful). 기본 false.</summary>
    public bool EnableSmartSfx { get; set; }

    /// <summary>4K 업스케일 — 켜면 병합(자막 번인) 전에 각 컷을 Real-ESRGAN/CUGAN 으로 업스케일한다(파이프라인 순서 고정: 업스케일 → 번인).
    ///   ⚠ 자막/UI 는 업스케일 <b>후</b>에 번인되어야 폰트 뭉개짐이 없다. 기본 false(엔진 설치 시에만 동작).</summary>
    public bool UpscaleTo4K { get; set; }

    /// <summary>업스케일 엔진 — "RealEsrgan"(실사) / "RealCugan"(2D·애니). 기본 실사.</summary>
    public string UpscaleEngine { get; set; } = "RealEsrgan";

    /// <summary>AI 감독 자율 검수 모드 — 켜면 컷 생성 후 Gemini 로 프레임을 검수하고 기준 미만이면 프롬프트를 자동 보정해 재생성한다.
    ///   Gemini API 키(appsettings Gemini:ApiKey)가 있어야 동작(없으면 무시). 기본 false.</summary>
    public bool AutoDirectorMode { get; set; }

    /// <summary>자율 검수 채택 기준점(0~100). 이 점수 이상이면 그대로 채택, 미만이면 Re-roll. 기본 75.</summary>
    public int QualityThreshold { get; set; } = 75;

    /// <summary>컷당 최대 자동 재생성(Re-roll) 횟수. 기본 2.</summary>
    public int MaxReroll { get; set; } = 2;

    /// <summary>네이티브 비디오 시간축 분석 — 켜면 컷 생성 후 mp4 전체를 Gemini 로 분석(모션/일관성/물리)해 VideoTemporalScore 기록,
    ///   기준(QualityThreshold) 미만이면 모션 보정 프롬프트로 재생성. Gemini 키 없으면 무시(기존 파이프라인 정상). 기본 false.</summary>
    public bool UseVideoAnalysis { get; set; }

    /// <summary>키네틱 하이라이트 자막 — 켜면 병합 시 Gemini 로 나레이션의 단어별 밀리초 타임코드를 추출해 단어 단위 색상 전환 자막 + 정밀 SFX 를 번인.
    ///   나레이션(VoiceText) 있는 컷에만 적용. 실패/키없음이면 정적 자막으로 폴백. 기본 false.</summary>
    public bool UseKineticCaptions { get; set; }

    /// <summary>AI 시네마틱 컬러 그레이딩 — 켜면 병합 시 Gemini 가 분위기에 맞는 ffmpeg 색보정 필터를 산출해 전 컷에 적용(안전 검증).
    ///   실패/키없음/위험 필터면 원본 톤 유지. 기본 false.</summary>
    public bool UseAiColorGrading { get; set; }

    /// <summary>감정선 동적 BGM 오케스트레이션 — 켜면 컷별 Tension 을 Gemini 로 분석해 BGM 볼륨 엔벨로프(긴장 씬 고조)·덕킹 강도·오디오 페이드를 자동 조절.
    ///   BGM/나레이션 있을 때 의미. 실패/키없음이면 기존 고정 믹싱. 기본 false.</summary>
    public bool UseEmotionPacing { get; set; }

    /// <summary>지능형 스마트 트랜지션 — 켜면 인접 컷의 엔딩/시작 프레임을 Gemini 로 시각 분석해 컷 경계에 xfade(fade/slide/zoom/blur)를 자동 적용.
    ///   컷 2개 이상일 때 의미. 실패/키없음이면 전환 없이 concat. 기본 false.</summary>
    public bool UseSmartTransitions { get; set; }

    /// <summary>생성형 폴리 & 3D 공간 음향 — 켜면 컷을 Gemini 로 분석해 효과음(폴리)을 생성/해결하고 피사체 위치에 맞춰 좌우 공간 팬으로 믹싱.
    ///   로컬 폴리 엔진 또는 내장 Sfx 라이브러리로 해결. 실패/키없음이면 폴리 없이 병합. 기본 false.</summary>
    public bool UseGenFoley { get; set; }

    /// <summary>음소거 시청 대응 시각 효과음 뱃지 — 켜면 폴리 트리거 순간에 이모지 결합 뱃지(에취! 🤧 등)를 화면 상단 중앙에 팝업 번인.
    ///   프리셋 authored 폴리를 소비해 오프라인 구동. 기본 false.</summary>
    public bool UseVisualSfxBadges { get; set; }

    /// <summary>아기 숏츠 원클릭 테마 프리셋으로 구성된 프로젝트인지 — true 면 컷의 authored 연출 데이터(키네틱/폴리/텐션/전환)를 병합이 Gemini 없이 사용.</summary>
    public bool IsBabyPreset { get; set; }
    /// <summary>적용된 프리셋 테마 키(예: "lemon", "naptime"). 표시/재적용용.</summary>
    public string? PresetKey { get; set; }

    /// <summary>v16.7.0 테마 시네마틱 바인딩 — 카메라 무빙 라벨(<see cref="PromptFlowStudio.Services.Ui.WanCameraStyleStudio.Cameras"/> Label). null 이면 미적용.</summary>
    public string? CinematicCamera { get; set; }
    /// <summary>v16.7.0 테마 권장 조명(<see cref="ThemeLighting"/> 이름).</summary>
    public string? CinematicLighting { get; set; }
    /// <summary>v16.7.0 테마 권장 효과음 키워드(최대 3).</summary>
    public List<string>? CinematicSfx { get; set; }
    /// <summary>v16.7.0 테마 최적 모션 강도(2~4 · 0=미적용).</summary>
    public int CinematicMotionIntensity { get; set; }

    /// <summary>숏츠 템포 배속(1.0=원본). 병합 시 setpts(영상)+atempo(오디오)로 적용. 프리셋: 1.0/1.15/1.25/1.5.</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    /// <summary>템포 배속 적용 여부 — 켜면 <see cref="PlaybackSpeed"/>대로 영상+오디오가 함께 빨라짐. 끄면 배속 무시(원본 1.0).
    ///   빠른 테마(펭귄 2.0x 등) 원클릭 적용 시 <see cref="Services.Storyboard.VideoFormatFactory"/>가 자동 ON. 구버전 JSON(필드 부재)은 true 로 로드(기존 배속 동작 보존).</summary>
    public bool UseTempoSpeedup { get; set; } = true;

    /// <summary>실효 배속 — <see cref="UseTempoSpeedup"/> OFF 면 1.0. 병합/타임라인이 이 값을 사용(직렬화 제외).</summary>
    [JsonIgnore] public double EffectiveSpeed => UseTempoSpeedup ? PlaybackSpeed : 1.0;

    /// <summary>자막/뱃지 세이프존 대상 플랫폼(기본 None=전체화면·기존 위치). 병합이 플랫폼별 안전 Y 밴드로 키네틱 자막/뱃지 오프셋 자동 보정.</summary>
    public Services.Media.SafePlatform TargetPlatform { get; set; } = Services.Media.SafePlatform.None;

    /// <summary>피사체 세이프존 자동 바운딩 — 켜면 세로 컷 생성 시 Wan 프롬프트에 '아기 얼굴을 상단-중앙 안전대에 배치' 프레이밍 키워드를 자동 주입(멱등·비영속).
    ///   <see cref="TargetPlatform"/> 안전대 기준. 세로(쇼츠)에서만 적용. 기본 false(역호환).</summary>
    public bool SmartFraming { get; set; }

    // ── v1.8.0 · Wan 무료 요금제 결함 자동 보정(Free-Tier Deficit Compensator) ──
    //   ⚠ 전부 기본값으로 자동 작동(원클릭 Auto-Pilot) · 구버전 JSON(필드 부재)은 이 기본값으로 로드 → 100% 역호환.

    /// <summary>✨ 완벽 자동 보정(Auto-Pilot) 마스터 스위치 — 켜면 무료 모델 결함(프롬프트 환각·깜빡임·모션 뭉개짐·속도 제한)을
    ///   한 번에 자동 탐지·보정한다. 끄면 아래 개별 보정을 모두 무시(기존 파이프라인 그대로). 기본 <b>true</b>.</summary>
    public bool AutoPilot { get; set; } = true;

    /// <summary>프롬프트 위생화 + 안정화 네거티브 자동 주입(<see cref="Services.Wan.WanPromptSanitizer"/>). 생성 시 멱등·비영속 주입. 기본 true.</summary>
    public bool AutoSanitizePrompt { get; set; } = true;

    /// <summary>FFmpeg 자동 디플리커(+시간축 디노이즈) — 프레임 깜빡임 제거. 병합 전 컷별 조건부 자동 실행. 기본 true.</summary>
    public bool AutoDeflicker { get; set; } = true;

    /// <summary>언샤프 마스크 + Lanczos 리샘플로 선명도 복원. 병합 전 컷별 조건부 자동 실행. 기본 true.</summary>
    public bool AutoSharpen { get; set; } = true;

    /// <summary>Minterpolate 모션 보상 프레임 보간(저 FPS 뭉개짐 완화). 연산이 무거워 기본 <b>false</b>(오픈트인). AutoPilot 하위 옵션.</summary>
    public bool AutoInterpolate { get; set; }

    /// <summary>시간적 슈퍼 레졸루션(tmix 중앙 가중 3프레임 평균) — 시간축 노이즈 저감으로 유료급 매끄러움. 병합 전 컷별 자동 실행. 기본 true.</summary>
    public bool AutoTemporalSr { get; set; } = true;

    /// <summary>디밴딩 색상 안정화(deband) — 하늘/그라데이션의 계단식 컬러 밴딩 억제. 병합 전 컷별 자동 실행. 기본 true.</summary>
    public bool AutoDeband { get; set; } = true;

    /// <summary>실효 프롬프트 보정 여부 — <see cref="AutoPilot"/> AND <see cref="AutoSanitizePrompt"/>(직렬화 제외).</summary>
    [JsonIgnore] public bool AutoFixPrompt => AutoPilot && AutoSanitizePrompt;

    /// <summary>실효 영상 보정 여부 — <see cref="AutoPilot"/> AND (디플리커/샤픈/보간/시간적SR/디밴딩 중 하나 이상)(직렬화 제외).</summary>
    [JsonIgnore] public bool AutoFixVideo => AutoPilot && (AutoDeflicker || AutoSharpen || AutoInterpolate || AutoTemporalSr || AutoDeband);

    // ── v2.1.0 · 멀티 플랫폼 일괄 동시 렌더링(OSMU 규격 영상 파일 배출) ──

    /// <summary>멀티 플랫폼 일괄 렌더링 — 켜면 최종 병합 후 선택 플랫폼별 규격 영상을 동시 배출한다. 기본 <b>false</b>(구 JSON 부재 시 미동작 · 역호환).</summary>
    public bool MultiPlatformExport { get; set; }

    /// <summary>B2B 3D 엠보싱 워터마크 — 켜면 병합 시 로고(<see cref="WatermarkImagePath"/>)를 우측 상단에 <b>양각 엠보스</b>로 합성한다.
    ///   B2B 테마 적용 시 <see cref="Services.Storyboard.VideoFormatFactory"/>가 자동 ON. 기본 <b>false</b>(아기 테마·구 JSON 은 기존 평면 워터마크 · 역호환).</summary>
    public bool EmbossWatermark { get; set; }

    /// <summary>일괄 렌더링 대상 플랫폼(<see cref="Services.Media.SafePlatform"/> 이름 목록). 비어 있으면 일괄 렌더링을 건너뛴다.</summary>
    public List<string> RenderPlatforms { get; init; } = new();

    /// <summary>파싱된 유효 대상 플랫폼(None 제외 · 중복 제거 · 직렬화 제외).</summary>
    [JsonIgnore]
    public IReadOnlyList<SafePlatform> SelectedRenderPlatforms =>
        RenderPlatforms
            .Select(n => Enum.TryParse<SafePlatform>(n, out var p) ? p : SafePlatform.None)
            .Where(p => p != SafePlatform.None)
            .Distinct()
            .ToList();

    // ── v2.5.0 · 자동차/B2B 전문 장비 — 텔레메트리 HUD & 사옥 3색 조명 멀티배치 ──

    /// <summary>텔레메트리 HUD 스타일 — 자동차 검사/시험 장비 테마 적용 시 <see cref="Services.Storyboard.VideoFormatFactory"/>가
    ///   카테고리별로 자동 설정("gauge"=시트/게이지 · "oscilloscope"=완성차·캘리브레이션 · "dyno"=전동화·다이나모). 빈 값이면 HUD 비활성(역호환).</summary>
    public string TelemetryHudStyle { get; set; } = "";

    /// <summary>텔레메트리 CSV 경로 — 실측 검사/시험 데이터(time,voltage,torque,rpm,angle,pressure,result). 지정 시 병합 후
    ///   <see cref="Services.Media.TelemetryHudOverlayEngine"/>가 최종 영상 위에 실시간 게이지·그래프·PASS/FAIL HUD를 번인한다. 비면 건너뜀(역호환).</summary>
    public string TelemetryCsvPath { get; set; } = "";

    /// <summary>사옥 3색 조명 멀티배치 — 켜면 사옥 홍보 마스터를 주간/노을/야경 3색 조명으로 일괄 병렬 그레이딩 배출한다.
    ///   <see cref="Services.Storyboard.MultiLightingBuildingEngine"/>가 사옥 쇼케이스 생성 시 자동 ON. 기본 <b>false</b>(역호환).</summary>
    public bool MultiLighting { get; set; }

    // ── v2.6.0 · 글로벌 B2B 수주 — CAD 홀로그램 · 다국어 더빙 · AI 퀄리티 가드 · 바이어 각인 ──

    /// <summary>CAD/STL 3D 메시 파일 경로(선택). 지정 시 병합 후 <see cref="Services.Cad.CadWireframeOverlayEngine"/>가
    ///   장비 3D 홀로그램 와이어프레임 회전 샷을 최종 영상 위에 합성한다. 비면 건너뜀(역호환).</summary>
    public string CadModelPath { get; set; } = "";

    /// <summary>3D 와이어프레임 네온 컬러 — "cyan"(시안) · "magenta"(마젠타) · "emerald"(에메랄드). 기본 cyan.</summary>
    public string WireframeNeonColor { get; set; } = "cyan";

    /// <summary>글로벌 더빙 대상 언어 코드 목록(<see cref="Services.Media.GlobalMultiDubbingEngine"/> 언어 코드: en/de/zh/ja).
    ///   비어 있으면 다국어 내보내기를 건너뛴다(역호환).</summary>
    public List<string> DubbingLanguages { get; init; } = new();

    /// <summary>파싱된 유효 더빙 언어(중복 제거 · 알 수 없는 코드 제외 · 직렬화 제외).</summary>
    [JsonIgnore]
    public IReadOnlyList<string> SelectedDubbingLanguages =>
        DubbingLanguages.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim().ToLowerInvariant()).Distinct().ToList();

    /// <summary>AI 비주얼 퀄리티 가드 — 켜면 컷 생성 직후 <see cref="Services.Wan.AiQualityGuardService"/>가 프레임 손상/안면 뭉개짐을
    ///   비트맵 수준으로 감수하고, 임계값 미달 시 해당 컷을 자동 재발주(Retry)한다. 기본 <b>false</b>(역호환).</summary>
    public bool AutoQualityGuard { get; set; }

    /// <summary>퀄리티 가드 SSIM 임계값(0~1) — 프레임 간 구조적 유사도 최소 허용치. 미만이면 손상으로 판정해 재발주. 기본 0.62.
    ///   ⚠ 기존 <see cref="QualityThreshold"/>(0~100 · Gemini 자율 검수 점수)와 별개의 오프라인 비트맵 가드 임계값이다.</summary>
    public double SsimQualityThreshold { get; set; } = 0.62;

    /// <summary>SSIM 퀄리티 가드 자동 재발주 최대 횟수(컷당). 기본 1.</summary>
    public int SsimRetryLimit { get; set; } = 1;

    /// <summary>글로벌 바이어 사명(선택) — 스펙 시트/각인에 사용(예: "Mercedes-Benz", "BYD"). 빈 값이면 미사용.</summary>
    public string BuyerName { get; set; } = "";

    /// <summary>바이어 엠블럼/로고 이미지 경로(선택). 지정 시 병합 후 <see cref="Services.Trade.BuyerEmblemEngraveService"/>가
    ///   좌측 하단에 음각 각인(engrave) 합성한다. 비면 건너뜀(역호환).</summary>
    public string BuyerEmblemPath { get; set; } = "";

    // ── v2.7.0 · 상용화 — 하드웨어 자동 교정된 병렬 스레드 ──

    /// <summary>벤치마크 자동 교정 병렬 가동 스레드 수(<see cref="Services.Studio.StudioBenchmarkEngine"/>가 기록). 0이면 호출부 기본값 사용(역호환).</summary>
    public int TunedParallelThreads { get; set; }

    // ── v2.8.0 · 차세대 방송 주조종실 — NDI/RTMP 송출 · 멀티GPU · 모션 트래킹 · ON AIR ──

    /// <summary>라이브 송출 프로토콜 — "rtmp"(OBS/vMix/유튜브) · "srt"(저지연) · "ndi"(로컬 방송망). 기본 rtmp.</summary>
    public string LiveProtocol { get; set; } = "rtmp";

    /// <summary>라이브 송출 대상 주소(rtmp://… · srt://… · NDI 소스명). 비면 송출 비활성(역호환).</summary>
    public string LiveUrl { get; set; } = "";

    /// <summary>멀티 GPU 분산 렌더링 — 켜면 컷 후처리/모션 보간을 장착된 N개 GPU에 라운드로빈 분산한다. 기본 false(단일 GPU · 역호환).</summary>
    public bool MultiGpuRender { get; set; }

    /// <summary>모션 트래킹 강조 라벨 텍스트(선택). 지정 + ROI 유효 시 병합 후 라벨이 피사체를 추종 합성. 비면 건너뜀(역호환).</summary>
    public string MotionTrackLabel { get; set; } = "";

    /// <summary>모션 트래킹 관심 영역(ROI) — 정규화 좌표(0~1). W/H가 0이면 트래킹 비활성.</summary>
    public double MotionRoiX { get; set; }
    public double MotionRoiY { get; set; }
    public double MotionRoiW { get; set; }
    public double MotionRoiH { get; set; }

    /// <summary>ON AIR 방송 뱃지 — 켜면 최종 영상 우측 상단에 4K 글래스모피즘 ON AIR 뱃지를 합성한다. 기본 false(역호환).</summary>
    public bool OnAirBadge { get; set; }

    /// <summary>스위칭 카운트다운 — 켜면 도입부에 3·2·1 방송 스위칭 카운트다운 비주얼을 합성한다. 기본 false(역호환).</summary>
    public bool SwitchCountdown { get; set; }

    // ── v2.9.0 · 엔터프라이즈 멀티 테넌트 중앙 클라우드 동기화 ──

    /// <summary>소속 테넌트 식별자(멀티 테넌트 데이터 격리). 비면 로컬 전용(역호환).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>낙관적 잠금용 동기화 버전. Push 성공 시 서버 버전으로 갱신 · 충돌 감지 기준. 기본 0(역호환).</summary>
    public int SyncVersion { get; set; }

    // ── v3.0.0 · 3D 버추얼 스튜디오 크로마키 합성 ──

    /// <summary>버추얼 스튜디오 배경 템플릿 키(<see cref="Services.Media.VirtualStudioCompositor"/> · 예: cyber_showroom/control_room/forest_magic).
    ///   지정 시 병합 후 크로마키 배경 분리 + 3D 무대 합성. 비면 건너뜀(역호환).</summary>
    public string VirtualStudioTemplate { get; set; } = "";

    /// <summary>크로마키 배경 색 — "green" 또는 "blue". 기본 green.</summary>
    public string ChromaColor { get; set; } = "green";

    // ── v3.7.0 · 초개인화 아기 성장 마일스톤 (전부 기본값 · 구 JSON 부재 시 이 기본값 로드 → 100% 역호환) ──

    /// <summary>아기 이름(각인 오버레이/기념 카드용 · 예: "예준"). 비면 이름 각인 생략(역호환).</summary>
    public string BabyName { get; set; } = "";

    /// <summary>아기 탄생일 "yyyy-MM-dd"(D+Day 계산 근거). 비면 D-Day 각인 생략(역호환).</summary>
    public string BabyBirthDate { get; set; } = "";

    /// <summary>예술 텍스처 스타일 키(<see cref="Services.Storyboard.BabyPromptMutator"/>). 비면 렌더 시 시드로 무작위 선택(매 컷 고유).</summary>
    public string BabyArtStyle { get; set; } = "";

    /// <summary>프로시저럴 변종 시드 — 0이면 합성 시마다 새 시드 생성(세상에 하나뿐인 컷). >0이면 재현 고정. 기본 0(역호환).</summary>
    public int BabyArtSeed { get; set; }

    /// <summary>5단계 감정 마이크로 아크(<see cref="Services.Media.BabyEmotionalArcEngine"/>) — 줌속도/렌즈블러/조명펄스 매핑. 기본 false(역호환).</summary>
    public bool BabyEmotionArc { get; set; }

    /// <summary>24K 골드 아우라 + 이름/D+Day 각인 오버레이(<see cref="Services.Media.BabyKeepsakeOverlayEngine"/>). 기본 false(역호환).</summary>
    public bool BabyGoldKeepsake { get; set; }

    // ── v3.8.0 · 알라바바 완 로컬/클라우드 하이브리드 렌더링 + 감정 보이스 · 3D 입체 음향 (전부 기본값 · 역호환) ──

    /// <summary>완 렌더링 모드 — "local"(로컬 완 1.3B · 0원 무료 1순위) / "cloud_upscale"(Wan 3.0 원격 업스케일). 기본 local.</summary>
    public string WanRenderMode { get; set; } = "local";

    /// <summary>Wan 3.0급 무료 화질 보완 — minterpolate 60fps + cas 샤픈 + eq 고대비 그레이딩 후처리(<see cref="Services.Wan.AlibabaWanHybridEngine"/>). 기본 false(역호환).</summary>
    public bool WanQualityBoost { get; set; }

    /// <summary>화질 보완 프레임 보간 목표 fps(minterpolate). 기본 60.</summary>
    public int WanEnhanceFps { get; set; } = 60;

    /// <summary>탐지된 GPU VRAM(MB) — 0이면 미탐지(하이브리드 엔진이 nvidia-smi 로 채움 · 모델 티어 자동 선택). 기본 0.</summary>
    public int DetectedVramMb { get; set; }

    /// <summary>감정 보이스 페르소나 키(<see cref="Services.Voice.EmotionalVoiceSynthesizer"/>: mom/dad/pro). 비면 기본 톤(역호환).</summary>
    public string VoiceTone { get; set; } = "";

    /// <summary>나레이션 언어 코드(ko/en/de/zh/ja). 기본 ko(원본).</summary>
    public string VoiceLanguage { get; set; } = "ko";

    /// <summary>3D 바이노럴 입체 음향 — 카메라 오르빗/패닝을 좌우·원근 공간감으로 번인(<see cref="Services.Media.SpatialAudioEngine"/>). 기본 false(역호환).</summary>
    public bool SpatialAudio { get; set; }

    /// <summary>키네틱 타이포그래피 자막 — 나레이션 파형 피크에 반응해 자막 크기/위치가 동적 변화(<see cref="Services.Voice.KineticTypographySubtitleBuilder"/>). 기본 false(역호환).</summary>
    public bool KineticSpatialSubtitle { get; set; }

    // ── v4.0.0 · 모바일 이행(.NET MAUI Blazor) · 리모트 렌더 오프로딩 · 온디바이스 배경 분리 (전부 기본값 · 역호환) ──

    /// <summary>리모트 렌더 워커(로컬 PC RTX GPU) 주소(http(s)/ws(s)). 비면 로컬/모바일 자체 렌더(역호환).</summary>
    public string RemoteWorkerUrl { get; set; } = "";

    /// <summary>리모트 렌더 오프로딩 — 켜면 스마트폰에서 발생한 렌더를 PC 워커로 위임(<see cref="Services.Mobile.RemoteRenderClient"/>). 기본 false(역호환).</summary>
    public bool RemoteRenderEnabled { get; set; }

    /// <summary>모바일 캡처 소스 — "camera"(즉석 촬영) / "gallery"(갤러리) / ""(미사용). 기본 ""(역호환).</summary>
    public string MobileCaptureSource { get; set; } = "";

    /// <summary>온디바이스 배경 자동 분리 — 켜면 촬영 사진 배경을 크로마키 알파로 분리(<see cref="Services.Mobile.MobileBackgroundSeparator"/>). 기본 false(역호환).</summary>
    public bool ChromaAutoKey { get; set; }

    // ── v4.5.0 · 날씨/환경(Dynamic Weather) 테마존 (전부 기본값 · 구 JSON 부재/null 모두 안전 · 100% 역호환) ──

    private WeatherThemeConfig _weather = new();
    /// <summary>날씨/환경 테마 구성(기후·시간대·강도·앰비언스). 구 <c>project.json</c> 은 필드 부재 → 기본(비활성) 구성으로 로드,
    ///   JSON 에 <c>"Weather": null</c> 이 와도 세터가 기본 인스턴스로 보정한다(null 참조 방어 · 역호환).</summary>
    public WeatherThemeConfig Weather
    {
        get => _weather;
        set => _weather = value ?? new WeatherThemeConfig();   // ⚠ null 방어 — 항상 비-null 보장
    }

    /// <summary>UI 배속 드롭다운 프리셋 (값, 표시명).</summary>
    public static readonly (double Value, string Label)[] SpeedPresets =
    {
        (1.0,  "원본 (1.0x)"),
        (1.15, "빠르게 (1.15x)"),
        (1.25, "숏츠 (1.25x)"),
        (1.5,  "매우 빠르게 (1.5x)"),
        (2.0,  "펭귄 초고속 (2.0x)"),
    };

    /// <summary>UI 드롭다운용 해상도 프리셋 (값, 표시명).</summary>
    public static readonly (string Value, string Label)[] ResolutionPresets =
    {
        ("1280x720",  "가로 HD · 1280×720"),
        ("1920x1080", "가로 FHD · 1920×1080"),
        ("1080x1920", "세로 쇼츠 · 1080×1920"),
        ("720x1280",  "세로 HD · 720×1280"),
    };

    /// <summary>"WxH" 문자열을 (가로,세로)로 파싱(x·X·* 허용). 실패 시 기본값 1280×720.</summary>
    public static (int W, int H) ParseResolution(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s))
        {
            var parts = s.ToLowerInvariant().Split('x', 'X', '*');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out var w) && int.TryParse(parts[1].Trim(), out var h)
                && w > 0 && h > 0)
                return (w, h);
        }
        return (1280, 720);
    }
}
