using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.AspNetCore.Components.WebView.WindowsForms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PromptFlowStudio.Blazor;
using PromptFlowStudio.Services.Account;
using PromptFlowStudio.Services.Media;
using PromptFlowStudio.Services.Platform;
using PromptFlowStudio.Services.Storyboard;
using PromptFlowStudio.Services.Wan;

namespace PromptFlowStudio;

/// <summary>
/// PromptFlow Studio 메인 창 — WinForms + BlazorWebView 하이브리드(HTTP 포트 없음 · 오프라인 단일 EXE).
///   프롬프트→영상 생성 워크플로우 스튜디오. 회원 로그인/가입·오프라인 라이선스·관리자(사용자 설정)
///   서브시스템을 DI 로 결선하고, Blazor UI(App)를 #app 에 마운트한다.
/// </summary>
public sealed class MainForm : Form
{
    public MainForm(IConfiguration config)
    {
        Text = "PromptFlow Studio — 프롬프트→영상 생성 워크플로우";
        BackColor = Color.FromArgb(12, 10, 26);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 720);
        ClientSize = new Size(1360, 860);
        WindowState = FormWindowState.Maximized;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* 기본 아이콘 */ }

        var services = new ServiceCollection();
        services.AddWindowsFormsBlazorWebView();
        PromptFlowStudio.Shared.SharedServiceRegistry.RegisterSharedServices(services);   // v4.1 공용 RCL 시임 1줄 통합 등록(데스크톱 폴백)
        services.AddSingleton<PromptFlowStudio.Shared.Cross.IParityProbe, PromptFlowStudio.Services.Cross.DesktopParityProbe>();  // v4.2 데스크톱 실 기능 프로브(기본 프로브 override)
        services.AddSingleton<PromptFlowStudio.Shared.Store.ISchemaCompatProbe, PromptFlowStudio.Services.Store.DesktopSchemaCompatProbe>();  // v4.3 데스크톱 실 스키마 왕복 프로브(override)
        services.AddSingleton<PromptFlowStudio.Shared.Deploy.IBuildLauncher, PromptFlowStudio.Services.Deploy.DesktopBuildLauncher>();       // v4.4 데스크톱 ps1/sh 패키징 런처(override)
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<LicenseService>();              // (구) RSA 오프라인 라이선스 — 현재는 SerialLicenseService 로 대체
        services.AddSingleton<SerialLicenseService>();        // 시리얼 라이선스 발급/검증(XXXX-XXXX · Used/Unused · license_registry.json)
        services.AddSingleton<MemberRegistryService>();       // 회원 원장(PBKDF2 · 파일 영구저장 · 관리자 시드)
        services.AddSingleton<MemberDirectorySyncService>();  // 가입 시 회원 프로필을 관리자 메일로 통보(+JSON 첨부 · best-effort)
        services.AddSingleton<JusoAddressService>();          // 행안부 도로명주소 검색 API(회원가입 주소검색 · 키 없으면 모의 DB 폴백)
        services.AddSingleton<SiteEmailOtpService>();         // 이메일 본인인증 OTP(SMTP + 데모 폴백)
        services.AddSingleton<InstallTelemetryService>();     // 설치 PC 텔레메트리(자동 수집)
        services.AddSingleton<SessionState>();                // 회원 로그인 세션(게이트/RBAC)
        services.AddSingleton<PromptFlowStudio.Services.Help.HelpState>();          // 도움말 드로어 열림 상태(전역 공유)
        services.AddSingleton<PromptFlowStudio.Services.Ui.UserPreferenceService>();// 사용자 설정(localStorage)
        services.AddSingleton<PromptFlowStudio.Services.Ui.ThemeService>();         // 커스텀 테마 엔진(themes.json · :root 오버라이드 · 프리셋4+커스텀)
        services.AddSingleton<PromptFlowStudio.Services.Ui.CommandPaletteService>();// 커맨드 팔레트 명령 등록소
        services.AddSingleton<PromptFlowStudio.Services.Ui.StudioNavigationService>(); // v17.50.1 사이드바 → 스토리보드 컨텍스트 스튜디오 위임

        // ── 영상 생성(통의만상 Wan 2.1 · DashScope async REST) ──
        services.AddSingleton<IWanConfigService, WanConfigService>();   // Wan 동적 설정(config.json · 재시작 불필요 · OnConfigChanged)
        services.AddSingleton<PromptFlowStudio.Services.Wan.WanTokenVault>();   // v15.11.0 다중 클라우드 Key Vault(프로바이더별 다중 키 라운드로빈 · 관리자 UI 즉시 반영 · 인메모리)
        services.AddSingleton<ILocalMediaService, LocalMediaService>(); // 로컬 미디어 자산화(스트리밍 다운로드 · 재생 경로 헬퍼 · 외부 NuGet 0)
        services.AddSingleton<IMediaLibraryService, MediaLibraryService>(); // 로컬 미디어 탐색(갤러리·퀵갤러리 · 열거/삭제/탐색기)
        services.AddSingleton<PromptFlowStudio.Services.Ui.PromptDraftService>(); // 모션프리셋→워크스페이스 프롬프트 전달
        services.AddSingleton<PromptFlowStudio.Services.Ui.CustomPresetService>(); // 사용자 정의 모션 프리셋 즐겨찾기(custom_presets.json)
        services.AddSingleton<PromptFlowStudio.Services.Wan.QuotaRateGuardService>();  // v1.8.0 지능형 속도 제한 방어(429/쓰로틀 지수 백오프 · 쿨다운 상태 카드 · 관찰 가능)
        services.AddSingleton<ComfyUiVideoEngine>();                               // v15.14.0 로컬 GPU 엔진 단일 인스턴스(Wan 클라우드 전멸 폴백 + 엔진 선택기 공유)
        services.AddSingleton<IWanApiService>(sp => new WanApiService(              // Wan API 연동(제출/폴링/결과 URL/키검증 · IWanConfigService 에서 라이브 로드 · QuotaRateGuard 자동 방어)
            sp.GetRequiredService<IWanConfigService>(), sp.GetRequiredService<PromptFlowStudio.Services.Wan.QuotaRateGuardService>(),
            sp.GetRequiredService<PromptFlowStudio.Services.Wan.WanTokenVault>(), null,
            sp.GetRequiredService<ComfyUiVideoEngine>(),                            // v15.14.0 클라우드 키 전멸 → 로컬 GPU 다이렉트 폴백
            sp.GetRequiredService<PromptFlowStudio.Services.Telemetry.ProductionCostTrackerService>()));   // v16.18.0 비용 기록 + 예산 차단
        // ── 비디오 생성 엔진 추상화(플러그인) ── Wan(동일 인스턴스) + 로컬 CLI. VideoEngineSelector 가 설정에 따라 활성 엔진 선택.
        services.AddSingleton<IVideoGenerationEngine>(sp => (IVideoGenerationEngine)sp.GetRequiredService<IWanApiService>()); // Wan
        services.AddSingleton<IVideoGenerationEngine>(sp => sp.GetRequiredService<ComfyUiVideoEngine>()); // 로컬 CLI(ComfyUI/CogVideoX · 동일 인스턴스)
        services.AddSingleton<VideoEngineSelector>();                            // 활성 엔진 선택기(EngineConfigService.VideoEngineKey)
        services.AddSingleton<IWanPromptBoosterService, WanPromptBoosterService>(); // 시네마틱 프롬프트 부스터(BCL · 한글 원문 보존 + 영문 태그)
        services.AddSingleton<GeminiConfigService>();                              // Gemini API 키 동적 설정(관리자 UI 등록 · 검증 핑 · override.json 영속 · 재시작 불필요 · 모든 Gemini 서비스의 키 근원)
        services.AddSingleton<IGeminiDirectorService, GeminiDirectorService>();     // AI 감독 자율 검수(Gemini 멀티모달 · QualityScore + 프롬프트 보정 Re-roll · 키 없으면 비활성)
        services.AddSingleton<GeminiClient>();                                      // Gemini 멀티모달 REST 공용 클라이언트(텍스트+이미지/오디오/비디오 인라인 · 3대 고급 서비스 공유)
        services.AddSingleton<IGeminiVideoAnalyzerService, GeminiVideoAnalyzerService>(); // 네이티브 MP4 통비디오 시간축 분석(VideoTemporalScore + 모션 보정)
        services.AddSingleton<IGeminiKineticSyncService, GeminiKineticSyncService>();     // 키네틱 자막/SFX 밀리초 타임코드 추출(단어별 highlight + SfxTriggers)
        services.AddSingleton<IGeminiColorGradingService, GeminiColorGradingService>();   // AI 시네마틱 컬러 그레이딩(분위기별 ffmpeg vf · 안전 검증)
        services.AddSingleton<IGeminiEmotionPacingService, GeminiEmotionPacingService>(); // 감정선 분석 & 동적 BGM 오케스트레이션(Tension→덕킹/페이드)
        services.AddSingleton<IGeminiTransitionDirectorService, GeminiTransitionDirectorService>(); // 지능형 매치컷/스마트 트랜지션(xfade · 프레임 시각분석)
        services.AddSingleton<IGeminiLocalizationService, GeminiLocalizationService>();   // 글로벌 다국어 번역 & SEO(metadata_{lang}.json 영속화)
        services.AddSingleton<ISwarmAgencyService, SwarmAgencyService>();                 // Multi-Agent Swarm(Writer/Director/Marketer 자율 토론 기획)
        services.AddSingleton<IAutoViralTrendService, AutoViralTrendService>();           // 실시간 트렌드 하이재킹(Google Trends RSS · BCL XML · 키 불필요)
        services.AddSingleton<IYouTubePublishService, YouTubePublishService>();           // 유튜브 자동 퍼블리싱(Data API v3 resumable · OAuth 리프레시 · 자격 없으면 비활성)
        services.AddSingleton<IGenAudioFoleyService, GenAudioFoleyService>();             // 생성형 폴리 & 3D 공간 음향(Gemini 오디오 프롬프트 → 엔진/Sfx + pan)
        services.AddSingleton<WanJobState>();                    // 생성 작업 상태(제출→폴링→다운로드→완료 · UI 관찰 · OnConfigChanged 릴레이)
        services.AddSingleton<PromptFlowStudio.Services.Media.FfmpegVideoMixerService>();   // v16.12.0 트랜지션·심리스 병합(ffmpeg xfade)
        services.AddSingleton<PromptFlowStudio.Services.Media.FfmpegAudioMixerService>();   // v16.12.0 BGM 루핑+Auto-Foley SFX 멀티트랙 믹싱
        services.AddSingleton<PromptFlowStudio.Services.Media.AiVoiceoverTtsService>();     // v16.14.0 동화 나레이션 TTS(ElevenLabs→Edge→로컬 폴백 · 톤/배속)
        services.AddSingleton<PromptFlowStudio.Services.Media.AutoSubtitleGenerator>();     // v16.14.0 숏폼 세이프존 SRT/ASS 자막 + 번인 필터
        services.AddSingleton<PromptFlowStudio.Services.Media.OsmuSmartReframingService>(); // v16.15.0 OSMU 9:16/1:1/4:5 재구성(블러 배경·센터 프레이밍)
        services.AddSingleton<SocialMetadataAiGenerator>();                                 // v16.15.0 Gemini 소셜 제목/본문/해시태그(오프라인 SEO 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Media.OsmuExportPackager>();        // v16.15.0 OSMU 플랫폼별 MP4+메타데이터 폴더/ZIP 패키징
        services.AddSingleton<PromptFlowStudio.Services.Media.CommercialProductSegmenterService>(); // v16.19.0 협찬 제품 누끼(알파 마스크)+장면 조명 매칭
        services.AddSingleton<PromptFlowStudio.Services.Media.SponsorVideoCompositorService>();     // v16.19.0 협찬 합성(누끼·로고·PPL·CTA 세이프존)
        services.AddSingleton<CommercialAdScriptAiGenerator>();                                    // v16.19.0 Gemini 15s/30s CM 카피·징글(로컬 템플릿 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Media.CommercialBrandCampaignService>();    // v16.19.0 1-Click 커머셜 캠페인 매니저
        services.AddSingleton<PromptFlowStudio.Services.Media.AutomatedVideoQaInspector>(); // v16.17.0 자동 QA(blackdetect·freezedetect·astats·자막 가림) 통과/경고/거부 리포트
        services.AddSingleton<DirectSocialPublisherBridge>();                              // v16.17.0 YouTube/TikTok/Instagram 직접 게시·예약 브릿지(QA 게이트·OAuth 갱신·429 백오프)
        services.AddSingleton<PromptFlowStudio.Services.Media.PostProductionEngine>();      // v16.12.0 최종 내보내기(병합 → 믹싱 → 먹싱) · v16.14.0 나레이션 덕킹+자막 번인
        services.AddSingleton<IVideoMergeService, VideoMergeService>(); // ffmpeg 비디오 병합(외부 프로세스 · BCL only · 해상도 통일 재인코딩)
        services.AddSingleton<PromptFlowStudio.Services.Wan.WanVideoPostEngine>(); // v1.8.0 무료 결함 자동 보정(FFmpeg deflicker/minterpolate/unsharp · 병합 전 컷별 조건부 자동 실행)
        services.AddSingleton<PromptFlowStudio.Services.Wan.NvencGpuMotionEngine>(); // v2.0.0 NVENC/CUDA GPU 가속 60fps 모션 보간(자동 탐지 · GPU→CPU 이중 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Wan.CudaZeroCopyEngine>();    // v2.1.0 Full VRAM Zero-Copy CUDA(scale_cuda→NVENC · 3단 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Media.DynamicPlatformSubtitleService>(); // v2.2.0 플랫폼 화면비별 동적 자막 재-번인 연산기(세이프존 Y-앵커·폰트 스케일)
        services.AddSingleton<PromptFlowStudio.Services.Media.MultiPlatformRenderEngine>(); // v2.1.0 멀티 플랫폼 일괄 동시 렌더링(플랫폼별 규격 영상 배출 · v2.2.0 동적 자막 연동)
        services.AddSingleton<IVoiceoverService, VoiceoverService>();   // Edge TTS 나레이션 합성(ClientWebSocket · BCL only · 실패 시 Graceful null)
        services.AddSingleton<IShortsCoverService, ShortsCoverService>(); // 숏츠 커버 썸네일 생성(ffmpeg drawtext · 포스터 위 대형 문구)
        // ── 엔터프라이즈 오픈소스 AI 엔진(모두 로컬 CLI · 외부 NuGet 0 · 미설치 시 Graceful) ──
        services.AddSingleton<PromptFlowStudio.Services.Engines.EngineConfigService>(); // AI 엔진 동적 설정(engines.json · Exe/Args · 재시작 불필요 · OnChange)
        services.AddSingleton<ILocalTtsService, LocalTtsService>();       // 오프라인 TTS(Kokoro-82M/F5-TTS · WAV 합성 · Edge TTS 대체)
        services.AddSingleton<IVideoUpscaleService, VideoUpscaleService>(); // 4K 업스케일(Real-ESRGAN/CUGAN · ffmpeg 분해→ncnn→재조립 · 진행률)
        services.AddSingleton<IWhisperSttService, WhisperSttService>();   // 오프라인 STT(Faster-Whisper · .srt 추출 · 컷 자막 동기화)
        services.AddSingleton<ILipSyncService, LipSyncService>();         // 립싱크(LatentSync · 입모양 동기화)
        services.AddSingleton<AiAudioDrivenLipSyncService>();             // v16.20.0 음소 타임라인 + 원격 Wav2Lip/MuseTalk → LatentSync → FFmpeg 프레임 보정 폴백
        services.AddSingleton<CharacterIdentityLockService>();            // v16.22.0 얼굴 특징 벡터 · 컷 정합성 신뢰도 · InstantID/PuLID 워크플로 결선
        services.AddSingleton<EpisodicStorySeriesBuilder>();              // v16.22.0 캐릭터 프로필/시즌·에피소드 · 프로필 전파 · 스타일 드리프트 억제
        services.AddSingleton<PromptFlowStudio.Services.Media.ViralHookVariantGeneratorService>();   // v16.23.0 첫 3초 A/B/C 후킹 변주 렌더
        services.AddSingleton<PromptFlowStudio.Services.Media.RetentionHeatmapPredictorEngine>();    // v16.23.0 유지율 곡선 예측 · 스피드 램핑/펀치-인
        services.AddSingleton<MultiVariantPublishPackager>();             // v16.23.0 A/B 변주 메타데이터 패키징 · 소셜 브릿지 일괄 게시
        services.AddSingleton<CulturalLocalizationTranspiler>();          // v16.24.0 화폐/단위/관용구/금기어 현지화 + Gemini 각색(오프라인 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Media.MultilingualVoiceCloningDubbingService>();   // v16.24.0 음색 매칭/복제 34개 로캘 더빙 · 재정렬 · 패키지
        services.AddSingleton<PromptFlowStudio.Services.Broadcast.RealtimeAvatarPuppeteerService>();   // v16.25.0 채팅 응답 TTS → 송출 PCM 주입 + 아바타 립싱크/표정 오버레이
        services.AddSingleton<PromptFlowStudio.Services.Broadcast.AiLiveLoopStreamerService>();        // v16.25.0 24/7 플레이리스트 루프 RTMP/SRT 동시 송출 + 워치독
        services.AddSingleton<PromptFlowStudio.Services.Broadcast.LiveChatAiReactionEngine>();         // v16.25.0 라이브 채팅 필터·우선순위 → Gemini 응답(폴백) → TTS → 아바타
        services.AddSingleton<PromptFlowStudio.Services.Interactive.InteractiveConversionAnalytics>();      // v16.26.0 A/B 선택 비율 · 이탈률 · 핫스팟 CTR/CVR 집계
        services.AddSingleton<PromptFlowStudio.Services.Interactive.ShoppableVideoOverlayEngine>();         // v16.26.0 쇼핑 태그 핫스팟 추적 · SVG/drawbox 오버레이
        services.AddSingleton<PromptFlowStudio.Services.Interactive.InteractiveChoiceBranchingService>();   // v16.26.0 분기 결정 트리 · 라이브 투표 · 인터랙티브 패키지
        services.AddSingleton<PromptFlowStudio.Services.Enterprise.CloudRenderFarmDispatcherService>();   // v16.27.0 클라우드 GPU 렌더 팜 Split-Merge · 로컬 폴백
        services.AddSingleton<PromptFlowStudio.Services.Enterprise.TeamCollaborationSessionBridge>();     // v16.27.0 팀 협업 WebSocket · RBAC · 컷 리뷰/승인
        services.AddSingleton<PromptFlowStudio.Services.Enterprise.C2paContentProvenanceEngine>();        // v16.27.0 C2PA 서명 매니페스트 + 비가시 워터마크
        services.AddSingleton<PromptFlowStudio.Services.Autonomous.PerformanceFeedbackLoopEngine>();      // v16.28.0 성과 보상 · UCB 특성 밴딧 · Gemini 프롬프트 자가 정제(폴백)
        services.AddSingleton<PromptFlowStudio.Services.Autonomous.CampaignRoiOptimizationEngine>();      // v16.28.0 eCPM/eCPA 최소화 예산·렌더 슬롯 재배분 · 기여도
        services.AddSingleton<PromptFlowStudio.Services.Autonomous.AutonomousCampaignDirectorService>();  // v16.28.0 트렌드 → 24h 스케줄 → 생산 → 승인/게시 → 학습 자율 루프
        services.AddSingleton<PromptFlowStudio.Services.MultiEngine.GenerativePromptTranspilerEngine>();   // v16.29.0 엔진별 프롬프트 방언 트랜스파일(Wan 태그 · Sora 서술 · Runway 카메라 · Luma 간결)
        services.AddSingleton(sp =>                                                                          // v16.29.0 플러그인 런타임(신뢰 게이트 · collectible ALC · 핫 리로드)
        {
            var f = new PromptFlowStudio.Services.MultiEngine.PluginExtensionMarketplaceFramework(sp.GetRequiredService<PromptFlowStudio.Services.Security.SecureSecretVault>());
            f.ScanAndLoad();
            f.EnableHotReload();
            return f;
        });
        services.AddSingleton<PromptFlowStudio.Services.MultiEngine.GenerativeVideoMultiEngineRouter>();   // v16.29.0 Wan/Sora 2/Luma/Runway/로컬/플러그인 동적 라우팅 + 장애 조치
        services.AddSingleton<IVideoGenerationEngine>(sp => sp.GetRequiredService<PromptFlowStudio.Services.MultiEngine.GenerativeVideoMultiEngineRouter>());   // 엔진 선택기에 "⚡ 멀티 엔진 루터" 노출
        services.AddSingleton(sp =>                                                                          // v16.30.0 제로 트러스트 쉴드(DPAPI-NG KEK · AES-256-GCM · SSO · HMAC 감사 체인)
        {
            var z = new PromptFlowStudio.Services.Master.ZeroTrustSecurityShieldService(sp.GetRequiredService<PromptFlowStudio.Services.Security.SecureSecretVault>(),
                sp.GetService<PromptFlowStudio.Services.Security.EnterpriseSecurityAudit>());
            z.Unlock();
            return z;
        });
        services.AddSingleton<PromptFlowStudio.Services.Master.CloudDeltaAssetSyncEngine>();           // v16.30.0 CDC 델타 청크 동기화 · P2P 릴레이
        services.AddSingleton<PromptFlowStudio.Services.Master.UniversalMasterStudioOrchestrator>();   // v16.30.0 30 모듈 매트릭스 + 마스터 파이프라인
        services.AddSingleton<PromptFlowStudio.Services.Spatial.Spatial3dGaussianSplattingEngine>();      // v16.31.0 3D 가우시안 스플래팅 씬 복원(단안 깊이 · 패닝 정합 · PLY)
        services.AddSingleton<PromptFlowStudio.Services.Spatial.MvHevcSpatialVideoTranscoder>();          // v16.31.0 DIBR SBS · MV-HEVC(spatial CLI) · 바이노럴 · 공간 패키지
        services.AddSingleton(sp => new PromptFlowStudio.Services.Spatial.ImmersiveVisionProInteractionBridge(   // v16.31.0 시선/제스처 공간 오버레이 브릿지
            sp.GetService<PromptFlowStudio.Services.Interactive.ShoppableVideoOverlayEngine>(),
            sp.GetService<PromptFlowStudio.Services.Interactive.InteractiveChoiceBranchingService>(),
            sp.GetService<PromptFlowStudio.Services.Interactive.InteractiveConversionAnalytics>()));
        services.AddSingleton<PromptFlowStudio.Services.Virtual.CinematicCameraMotionControlEngine>();          // v16.32.0 시네마틱 카메라 리그 궤적 · 프롬프트 인젝션 · UE JSON/CSV
        services.AddSingleton<PromptFlowStudio.Services.Virtual.AiChromaKeyingAndRelightingEngine>();          // v16.32.0 스마트 크로마키 · 스필 억제 · 조명 매칭 리라이팅
        services.AddSingleton<PromptFlowStudio.Services.Virtual.UnrealEngine5LiveStagingBridge>();             // v16.32.0 UE5 OSC 카메라 동기화 · NDI/RTSP 수신 · 스테이징 패키지 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Sound.AiGenerativeFoleySoundscapeEngine>();           // v16.33.0 모션/키워드 → 절차적 Foley SFX · 타임코드 인젝션
        services.AddSingleton<PromptFlowStudio.Services.Sound.AudioStemSeparationAndMixingEngine>();          // v16.33.0 4-스템 분리(BCL HPSS · Demucs 선택) · 덕킹/EQ/LUFS 믹서
        services.AddSingleton<PromptFlowStudio.Services.Sound.DolbyAtmosSpatialAudioMapper>();                // v16.33.0 Atmos 오브젝트 매핑 · 바이노럴/5.1/7.1.4/ADM BWF 마스터
        services.AddSingleton<PromptFlowStudio.Services.Vfx.AiNeuralVfxRelightingEngine>();                 // v16.34.0 깊이/법선 기반 포인트·스포트·디렉셔널 리라이팅 + 그림자
        services.AddSingleton<PromptFlowStudio.Services.Vfx.SuperResolution8kUpscalerEngine>();             // v16.34.0 Real-ESRGAN/Compact 8K 업스케일 · 디노이즈/디블록 폴백
        services.AddSingleton<PromptFlowStudio.Services.Vfx.MotionFrameInterpolationEngine>();              // v16.34.0 RIFE/FLAVR 60·120fps 보간 · 8x 슬로모션(MCI 폴백)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Screenplay.WorldbuildingStoryAgentEngine(sp));     // v16.35.0 멀티 에이전트 세계관/인물/관계도(Gemini 선택 · BCL 토론 폴백 · JSON 영구 저장)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Screenplay.SceneBeatSheetScriptGenerator(sp));     // v16.35.0 3막/숏폼 비트 시트 · Fountain 대본 생성/파싱
        services.AddSingleton(sp => new PromptFlowStudio.Services.Screenplay.ScreenplayToStoryboardConverter(sp));   // v16.35.0 대본 → 컷/프롬프트/립싱크/TTS 큐(테마 시네마틱 매칭)
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwin.AiDigitalTwinGeneratorEngine(sp));          // v16.36.0 사진/영상 → 3DMM 메시 · 알베도 · 피부/헤어/체형 · 정체성 벡터 · 테마 결합
        services.AddSingleton<PromptFlowStudio.Services.DigitalTwin.RealtimeWebcamMotionCaptureTracker>();                     // v16.36.0 33 관절 · 468 안면 · 21×2 손 60FPS 추적(웹캠 BCL/외부 MediaPipe/합성)
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwin.FacialAndHandGestureMappingController(sp));   // v16.36.0 EWMA 지터 억제 · 리그 리타겟 · 표정/제스처 · BVH · 인젝션
        services.AddSingleton(sp => new PromptFlowStudio.Services.Music.AiGenerativeMusicEngine(sp));            // v16.37.0 Suno/Udio 어댑터 + BCL 절차적 신디사이저/포먼트 보컬 폴백
        services.AddSingleton(sp => new PromptFlowStudio.Services.Music.AudioBeatVisualSyncEngine(sp));          // v16.37.0 BPM/온셋/드랍 분석 · 무할당 컷 매칭 · 전환/VFX/스피드 램프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Music.AlgorithmicChoreographyController(sp));  // v16.37.0 절차적 댄스 안무 · 트윈 리타겟 · MV 구성
        services.AddSingleton(sp => new PromptFlowStudio.Services.VTuber.VTuberPersonaMemoryEngine(sp));        // v16.38.0 페르소나 · 인메모리 벡터 RAG 장기 기억 · Gemini/BCL 답변
        services.AddSingleton(sp => new PromptFlowStudio.Services.VTuber.LiveVTuberBroadcastingBridge(sp));      // v16.38.0 VMC(OSC) · VTube Studio · OBS WebSocket · 오버레이 SSE
        services.AddSingleton(sp => new PromptFlowStudio.Services.VTuber.FanInteractionReactionController(sp));  // v16.38.0 후원 리액션 큐 · 감사 안무/징글/VFX · 자율 방송
        services.AddSingleton(sp => new PromptFlowStudio.Services.Gaming.RealtimeGameVisionEventExtractor());            // v16.39.0 게임 화면 캡처(GDI) · HUD 비전/OCR · 게임 이벤트
        services.AddSingleton(sp => new PromptFlowStudio.Services.Gaming.LiveGamingCommentaryVoiceEngine(sp));          // v16.39.0 이벤트 → 페르소나 해설 · 1초 예산 TTS
        services.AddSingleton(sp => new PromptFlowStudio.Services.Gaming.InteractiveGamingOverlayController(sp));       // v16.39.0 훈수/예측 투표/동료 아바타 오버레이
        services.AddSingleton(sp => new PromptFlowStudio.Services.Xr.VolumetricVideoHologramStreamer(sp));    // v16.40.0 모캡/트윈/RGB-D → 볼류메트릭 포인트 클라우드 · 무할당 코덱 · WebXR SSE · RTP/SDP
        services.AddSingleton(sp => new PromptFlowStudio.Services.Xr.VirtualConcertStageOrchestrator(sp));    // v16.40.0 비트/안무 → 조명·레이저·스모크·파이로 큐시트 · Atmos 패닝 · 60Hz 재생
        services.AddSingleton(sp => new PromptFlowStudio.Services.Xr.MultiUserVirtualAudienceBridge(sp));     // v16.40.0 응원봉/함성/이모트 · UDP/채팅 · 열지도 · 하이프 무대 트리거
        services.AddSingleton<PromptFlowStudio.Services.Cinema.CinematicUniverseTimelineEngine>();                       // v16.41.0 에피소드/스핀오프 이벤트 컴파일 · 무할당 개연성(Plot Hole) 검증 · 타임라인 그래프
        services.AddSingleton<PromptFlowStudio.Services.Cinema.AutonomousSceneBlockingEngine>();                         // v16.41.0 블로킹 · 렌즈/화각 · 3점 조명 · 마스터/OTS/CU 샷 리스트 · 180° 검사
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.FeatureFilmMasteringPackager(sp));         // v16.41.0 장편/시리즈 · 챕터 · 다국어 오디오/자막 · HLS · DCP 패키징
        services.AddSingleton(sp => new PromptFlowStudio.Services.Mam.EnterpriseMamSmartIndexingEngine());                  // v16.42.0 자산 256D 시맨틱 색인 · 지각 해시 · SHA-256 중복 제거 · SIMD 검색
        services.AddSingleton<PromptFlowStudio.Services.Mam.AiContentModerationComplianceShield>();                         // v16.42.0 FCC/KCSC/AVMSD·AI Act/Ofcom/BPO 심의 · 연령 등급 · 저작권 · 딥페이크 · 브랜드 안전
        services.AddSingleton(sp => new PromptFlowStudio.Services.Mam.MultiCloudEdgeCdnDistributionRouter(sp));            // v16.42.0 지오 라우팅 · 토큰 DRM · HLS/DASH 동적 매니페스트 오프로더 · 멀티 CDN 배포
        services.AddSingleton<PromptFlowStudio.Services.Mcr.ContextualServerSideAdInsertionEngine>();                         // v16.43.0 맥락 광고 브레이크 · SCTE-35 · HLS/DASH 인젝션 · 라이브 큐
        services.AddSingleton(sp => new PromptFlowStudio.Services.Mcr.AutonomousMcrProgramSwitcherEngine(sp));            // v16.43.0 PGM/PVW 관제 · 0초 에머전시 바이패스 · AI 자율 연출
        services.AddSingleton(sp => new PromptFlowStudio.Services.Mcr.DynamicAdRevenueOptimizationEngine(sp));            // v16.43.0 RTB 2nd-price · 하한 오토스케일 · 지분 정산 HMAC 원장
        services.AddSingleton(sp => new PromptFlowStudio.Services.Newsroom.RealtimeNewsFeedFactCheckEngine(sp));          // v16.44.0 RSS/API/소셜/웹훅 수집 · 클러스터 교차 검증 · Gemini 팩트체크
        services.AddSingleton(sp => new PromptFlowStudio.Services.Newsroom.NewsMotionGraphicsOverlayEngine());            // v16.44.0 등급별 Lower-Third · 티커 · 인포그래픽 합성 · OBS SSE
        services.AddSingleton(sp => new PromptFlowStudio.Services.Newsroom.DualAiNewsAnchorGenerator(sp));               // v16.44.0 앵커×리포터 대본 · 텔레프롬프터 · 립싱크 브릿지 진행
        services.AddSingleton<PromptFlowStudio.Services.Sports.BiometricSportsAnalyticsOverlayEngine>(); // v16.45.0 속도/거리/심박/xG·xA · 미니맵 오버레이
        services.AddSingleton<PromptFlowStudio.Services.Sports.AutomatedSportsHighlightReplayEngine>(); // v16.45.0 함성/골 감지 · 120fps 보간 슬로 리플레이
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sports.AiSportsMulticamAutoDirectorEngine(sp)); // v16.45.0 9캠 볼 추적 · 자율 PGM 스위칭
        services.AddSingleton<PromptFlowStudio.Services.EduTech.InteractiveEduVideoQuizEngine>(); // v16.46.0 타임코드 퀴즈 인젝션 · 채점 · BKT 숙달도
        services.AddSingleton(sp => new PromptFlowStudio.Services.EduTech.AdaptiveAiTutorCoPilot(sp)); // v16.46.0 RAG 튜터 · 비유 보충 강의 · 립싱크 아바타
        services.AddSingleton(sp => new PromptFlowStudio.Services.EduTech.ScormXApiElearningPackager(sp)); // v16.46.0 SCORM 1.2/2004 · xAPI · LTI 1.3 패키저
        services.AddSingleton<PromptFlowStudio.Services.Accessibility.AiSignLanguageAvatarSynthesizer>(); // v16.47.0 KSL/ASL 수어 번역 · 트윈 리그 PiP 아바타
        services.AddSingleton(sp => new PromptFlowStudio.Services.Accessibility.GenerativeAudioDescriptionEngine(sp)); // v16.47.0 대사 공백 화면 해설 · 자동 더킹
        services.AddSingleton<PromptFlowStudio.Services.Accessibility.WcagAccessibilityCorrectionFilter>(); // v16.47.0 Daltonization · WCAG 명암비 · 난독증 자막
        services.AddSingleton(sp => new PromptFlowStudio.Services.Accessibility.BarrierFreePackager(sp)); // v16.47.0 1-Click 배리어프리 패키지
        services.AddSingleton(sp => new PromptFlowStudio.Services.Commerce.LiveCommerceInventoryOrderBridge(sp)); // v16.48.0 재고/주문 무할당 수신 · Shopify/Woo/REST · 웹훅 HMAC
        services.AddSingleton(sp => new PromptFlowStudio.Services.Commerce.AiLiveShowHostEngine(sp)); // v16.48.0 자율 쇼호스트 · 채팅 Q&A · 품절/폭주 멘트
        services.AddSingleton<PromptFlowStudio.Services.Commerce.ShoppertainmentOverlayEngine>(); // v16.48.0 타임세일 3D · 구매 토스트 · 회전 카드 · QR
        services.AddSingleton(sp => new PromptFlowStudio.Services.Commerce.ShoppertainmentShowRunner(sp)); // v16.48.0 1-Click 쇼퍼테인먼트 방송
        services.AddSingleton(sp => new PromptFlowStudio.Services.Podcast.MultiAgentTalkshowConversationOrchestrator(sp)); // v16.49.0 MC+패널 발화권 · 찬반 토론/인터뷰 · Gemini 페르소나
        services.AddSingleton<PromptFlowStudio.Services.Podcast.AutonomousPodcastVideoMultiCamEngine>(); // v16.49.0 액티브 스피커 탐지 · 자동 멀티캠 · 하이라이트
        services.AddSingleton(sp => new PromptFlowStudio.Services.Podcast.InteractiveAudienceAgendaInterventionEngine(sp)); // v16.49.0 시청자 개입 채택 · 패널 라우팅
        services.AddSingleton(sp => new PromptFlowStudio.Services.Podcast.PodcastStudioShowRunner(sp)); // v16.49.0 1-Click 자율 팟캐스트 녹화/송출
        services.AddSingleton(sp => new PromptFlowStudio.Services.Empire.OmnichannelGlobalBroadcastDispatcher()); // v16.50.0 53채널 렌디션 공유 · 메타 최적화 · 병렬 송출/페일오버
        services.AddSingleton(sp => new PromptFlowStudio.Services.Empire.SelfEvolvingMediaEmpireFranchiseEngine()); // v16.50.0 IP 스핀오프 자가 증식
        services.AddSingleton(sp => new PromptFlowStudio.Services.Empire.ExecutiveEmpireTelemetryAndRevenueMatrix(sp)); // v16.50.0 50시스템 헬스·자율 복구 · 메모리 · 수익/정산
        services.AddSingleton(sp => new PromptFlowStudio.Services.Empire.MediaEmpireRunner(sp)); // v16.50.0 1-Click 미디어 제국 가동
        services.AddSingleton(sp => new PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard()); // v16.51.0 핫패스 할당/GC 프로브 · SIMD 무할당 우회 · 히트맵
        services.AddSingleton(sp => new PromptFlowStudio.Services.Hardening.EnterpriseE2eSystemHealthAuditor(sp, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(services, d => d.ServiceType)))); // v16.51.0 E2E 교차 진단 · 픽스 주입
        services.AddSingleton(sp => new PromptFlowStudio.Services.Hardening.ProductionReleaseDeploymentPackager(sp)); // v16.51.0 6타깃 publish 계획 · 트림/AOT IL 분석 · 서명 매니페스트 번들
        services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalOps.LiveProductionHotReloadingEngine(sp)); // v16.52.0 식/규칙 DSL 트랜스파일 · 서명 ALC 핫 스왑 · 카나리/롤백
        services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalOps.GlobalSlaAutoScalingLoadBalancer()); // v16.52.0 P2C 라우팅 · 예측 오토스케일 · 99.999% SLA/번 레이트
        services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalOps.ZeroTrustContinuousSecurityAuditor(sp)); // v16.52.0 키 유출 · 메모리 변조 복원 · 패킷 위조/재생/폭주 격리
        services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalOps.GlobalOperationsRunner(sp)); // v16.52.0 글로벌 운용 러너 · 1-Click 최적화
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sovereign.SovereignMediaCloudConfidentialMesh()); // v16.53.0 TEE 원격 증명 · 데이터 주권 배치 · 기밀 채널 렌더
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sovereign.PostQuantumMediaVaultEngine(sp)); // v16.53.0 ML-KEM-768+ECDH / ML-DSA-65+ECDSA 하이브리드 금고 · 워터마크 · PQ 원장
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sovereign.MultiTenantStudioGovernanceEngine(sp)); // v16.53.0 RBAC/ABAC · 테넌트 경계 · 쿼터 · 감사 체인
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sovereign.SovereignCloudOrchestrator(sp)); // v16.53.0 1-Click 양자 보안 봉인
        services.AddSingleton(sp => new PromptFlowStudio.Services.World.AiGenerative4dWorldModelEngine()); // v16.54.0 4D 시공간 월드 모델 · 물리 시뮬 · 레이트레이서 · 상태 매트릭스
        services.AddSingleton(sp => new PromptFlowStudio.Services.World.InteractiveVideoBranchingTranspiler()); // v16.54.0 분기 DSL 트랜스파일러 · 무할당 심리스 분기 플레이어
        services.AddSingleton(sp => new PromptFlowStudio.Services.World.NeuralSceneGraphPromptOptimizer()); // v16.54.0 4D 장면 그래프 · 프롬프트 정제 · 퍼셉트론
        services.AddSingleton(sp => new PromptFlowStudio.Services.World.WorldModelStudioRunner(sp)); // v16.54.0 1-Click 4D 생성 · 인터랙티브 재생 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Teleport.AiVolumetricTeleportationMeshEncoder(sp)); // v16.55.0 트윈/포인트 클라우드 → 동적 메시 코덱 · 노이즈 게이트 · MTU+XOR FEC
        services.AddSingleton(sp => new PromptFlowStudio.Services.Teleport.SpatialAudioVisualBeamformingEngine()); // v16.55.0 시청자 귀/눈 추적 · 지연-합 음향 집속 · 홀로 위상
        services.AddSingleton(sp => new PromptFlowStudio.Services.Teleport.HolographicTelepresenceRelayBridge(sp)); // v16.55.0 Vision Pro/Quest 3/WebXR/홀로그램 SFU 중계 · 50ms 예산
        services.AddSingleton(sp => new PromptFlowStudio.Services.Neuro.NeuroBioSignalEmotionParserEngine()); // v16.56.0 EEG/HRV/GSR 파서 · 몰입/흥분/쾌적/공포 지수 · UDP
        services.AddSingleton(sp => new PromptFlowStudio.Services.Neuro.BioFeedbackGenerativeMediaModifier(sp)); // v16.56.0 감정 → 그레이딩/BGM/효과음/VFX/분기 변조
        services.AddSingleton(sp => new PromptFlowStudio.Services.Neuro.CollectiveEmotionHeatmapAggregator()); // v16.56.0 집단 공감도 · 피크 · 3D 감정 스펙트럼
        services.AddSingleton(sp => new PromptFlowStudio.Services.Neuro.BciNeuroStudioRunner(sp)); // v16.56.0 BCI 바이오피드백 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.MediaPipeline.UnlimitedMediaStreamBufferPipeline()); // v16.57.0 무제한 청크 스트리밍 수신 · 메모리 맵 프로브 · 프록시
        services.AddSingleton(sp => new PromptFlowStudio.Services.MediaPipeline.AutoPresetContextTuner()); // v16.57.0 NLU+비전 오토 프리셋
        services.AddSingleton(sp => new PromptFlowStudio.Services.MediaPipeline.UniversalFullFeatureChainAdapter(sp)); // v16.57.0 전체 기능 오토 체이닝
        services.AddSingleton(sp => new PromptFlowStudio.Services.Neural3d.AiGaussianSplattingRasterizerEngine()); // v16.58.0 3DGS EWA 타일 라스터라이저 · 다시점 복원 · PLY
        services.AddSingleton(sp => new PromptFlowStudio.Services.Neural3d.DynamicNeRfToSplattingConverter()); // v16.58.0 NeRF↔3DGS 변환 · 키/델타 스트림(MTU+FEC)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Neural3d.NeuralCameraPoseRelightingEngine()); // v16.58.0 LM PnP 서브밀리 추적 · HDR SH9 리라이트 · 그림자
        services.AddSingleton(sp => new PromptFlowStudio.Services.Neural3d.GaussianSplattingStudioRunner(sp)); // v16.58.0 3DGS 라이브 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Icvfx.IcvfxLedWallFrustumRendererEngine()); // v16.59.0 LED 월 이너/아우터 프러스텀 · 오프액시스 · 지연 보상
        services.AddSingleton(sp => new PromptFlowStudio.Services.Icvfx.AcesColorManagementAndMoireSuppressionEngine()); // v16.59.0 ACES/OCIO · 3D LUT · PQ · 무아레 위상 상쇄
        services.AddSingleton(sp => new PromptFlowStudio.Services.Icvfx.AgenticAutonomousVirtualProductionDirector(sp)); // v16.59.0 카메라/조명(DMX·Art-Net)/VFX 에이전트 · 코디네이터
        services.AddSingleton(sp => new PromptFlowStudio.Services.Icvfx.VirtualProductionStudioRunner(sp)); // v16.59.0 ICVFX 자율 디렉팅 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Metaverse.SpatialXrMetaverseBroadcastingEngine()); // v16.61.0 6DoF 3DGS/4D 스테이지 · 스테레오 · XRS1 씬 스트림 · gRPC/WebXR
        services.AddSingleton(sp => new PromptFlowStudio.Services.Metaverse.SpatialComputingInteractionBridge()); // v16.61.0 Vision Pro/Quest 시선 · 핀치 · 서사 분기
        services.AddSingleton(sp => new PromptFlowStudio.Services.Metaverse.NeuralHologramMultiuserStateSync()); // v16.61.0 다중 사용자 동기화 · 홀로 오버레이
        services.AddSingleton(sp => new PromptFlowStudio.Services.Metaverse.SpatialXrStudioRunner(sp)); // v16.61.0 공간 XR 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Synergy.EngineHyperAccelerationOptimizer()); // v16.62.0 SIMD 하이퍼 가속 · 메모리 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.Synergy.EnhancedScriptToScreenDirector(sp)); // v16.62.0 대본 → 숏/OST/폴리 · 일관성 검수
        services.AddSingleton(sp => new PromptFlowStudio.Services.Synergy.DeepCrossEngineSynergyOrchestrator(sp)); // v16.62.0 BCI·ICVFX·3DGS·4D 교차 시너지 버스
        services.AddSingleton(sp => new PromptFlowStudio.Services.Hardware.HardwareZeroCopyDirectPathPipeline()); // v16.63.0 CPU-GPU Zero-Copy DirectPath 공유 메모리
        services.AddSingleton(sp => new PromptFlowStudio.Services.Hardware.DistributedRenderFarmSyncEngine()); // v16.63.0 분산 렌더 노드 팜(로컬·RFT1 원격)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Hardware.HardwareVideoEncoderOffloader(sp)); // v16.63.0 NVENC/AMF/QSV 인코더 오프로드
        services.AddSingleton(sp => new PromptFlowStudio.Services.Hardware.HardwareAccelerationStudioRunner(sp)); // v16.63.0 하드웨어 가속 스튜디오 루프
        services.AddSingleton(sp => PromptFlowStudio.Services.Edge.RealtimeGlobalMediaMeshRouter.BuildDemo(out _, out _)); // v16.64.0 글로벌 미디어 메시(컷스루 · ABR · 무단절 우회)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Edge.AiEdgeSwarmNodeOrchestrator()); // v16.64.0 에지 스웜(SWR1 하트비트 · SIMD 배정)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Edge.EdgeAutonomousAiAgentEngine(sp)); // v16.64.0 온디바이스 AI 에이전트(int8 증류 · 클라우드 0)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Edge.EdgeSwarmStudioRunner(sp)); // v16.64.0 에지 스웜 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.LowSpec.HardwareAdaptiveLowSpecProfiler()); // v16.65.0 적응형 저사양 프로파일러(LOD · 연산 해상도)
        services.AddSingleton(sp => new PromptFlowStudio.Services.LowSpec.DynamicLowBitQuantizationFallbackRouter()); // v16.65.0 INT4/INT8 가변 양자화 · iGPU/CPU SIMD 폴백
        services.AddSingleton(sp => new PromptFlowStudio.Services.LowSpec.ZeroAllocMemorySqueezerGuard()); // v16.65.0 아레나 압착 · GC 억제 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.LowSpec.LowSpecPerformanceStudioRunner(sp)); // v16.65.0 저사양 초고성능 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Production.GlobalCloudEdgeOtaSyncEngine()); // v16.66.0 클라우드-에지 델타 OTA 동기화(A/B 무단절)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Production.AiSelfHealingQaTelemetryAuditor()); // v16.66.0 AI 자율 복구 QA · 서명 크래시 텔레메트리
        services.AddSingleton(sp => new PromptFlowStudio.Services.Production.EnterpriseProductionPackagingVerifier()); // v16.66.0 5 플랫폼 패키징 · ECDSA 무결성 검증
        services.AddSingleton(sp => new PromptFlowStudio.Services.Production.EnterpriseProductionReleaseStudioRunner(sp)); // v16.66.0 엔터프라이즈 릴리스 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sre.BurnRateAndErrorBudgetOptimizer()); // v16.67.0 다중 창 번 레이트 · 오류 예산 자율 최적화
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sre.GlobalEdgeTelemetryAnomalyReactor()); // v16.67.0 노드 텔레메트리 이상 반응기(서브 ms 격리)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sre.AutonomousAiSreGridAgent(sp)); // v16.67.0 Five-Nines AI SRE 그리드 제어 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Sre.GlobalOperationsGridStudioRunner(sp)); // v16.67.0 글로벌 SRE 관제 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.AgentFederation.RealtimeSpatiotemporalKnowledgeGraph()); // v16.68.0 시공간 신경망 지식 그래프
        services.AddSingleton(sp => new PromptFlowStudio.Services.AgentFederation.ZeroTrustInterAgentConsensusResolver()); // v16.68.0 ML-DSA-65 BFT 에이전트 합의
        services.AddSingleton(sp => new PromptFlowStudio.Services.AgentFederation.MultiAgentSwarmFederationOrchestrator(sp)); // v16.68.0 멀티 에이전트 스웜 연합(무할당 버스)
        services.AddSingleton(sp => new PromptFlowStudio.Services.AgentFederation.MultiAgentFederationStudioRunner(sp)); // v16.68.0 연합 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.RealtimeNeuralAudioDubbingEngine()); // v16.69.0 G2V · DTW 립싱크 · SIMD 입 ROI 워프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.MultiLanguageCulturalNuanceAdaptor()); // v16.69.0 관용구/은어/금기 문화 적응 · 자막
        services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.NeuralVoiceCloningAcousticTransfer()); // v16.69.0 보이스 클로닝(F0·음색·잔향 전송)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.GlobalLocalizationStudioRunner(sp)); // v16.69.0 로컬라이제이션 스튜디오 파이프라인
        services.AddSingleton(sp => new PromptFlowStudio.Services.Nexus.QuantumHybridNeuralMeshEngine()); // v16.70.0 양자 영감 시공간 텐서 메시(저랭크 · SQA QUBO)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Nexus.SpatiotemporalCloudNexusStreamer()); // v16.70.0 단일 FEC · HMAC 넥서스 스트림
        services.AddSingleton(sp => new PromptFlowStudio.Services.Nexus.AutonomousCreativeSingularityOrchestrator(sp)); // v16.70.0 시나리오 한 줄 → 모듈 오토 체이닝 DAG
        services.AddSingleton(sp => new PromptFlowStudio.Services.Nexus.OmniModalNexusStudioRunner(sp)); // v16.70.0 옴니모달 넥서스 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveStream.AiLiveBroadcastAutonomousSwitchingEngine()); // v16.71.0 60fps 자율 스위처(Cut/Dissolve/Wipe · A/V 지연 보상)
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveStream.InteractiveStreamViewerFeedbackDirector()); // v16.71.0 채팅/후원/투표 → 월드·BGM·조명·모션
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveStream.UltraLowLatencyMultiPlatformStreamer()); // v16.71.0 RTMP·SRT·WebRTC 멀티캐스트
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveStream.LiveBroadcastStudioRunner(sp)); // v16.71.0 라이브 방송 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.OpenUsd.OpenUsdSpatialAssetCompositorEngine()); // v16.72.0 OpenUSD(.usda/.usdc/.usdz) 파서 · 레이어 합성 · 3DGS/4D 바인딩
        services.AddSingleton(sp => new PromptFlowStudio.Services.OpenUsd.GenerativeWorldProceduralEngine()); // v16.72.0 프롬프트 → 지형/바이옴/자산 절차 생성(시밀러리티 시드)
        services.AddSingleton(sp => new PromptFlowStudio.Services.OpenUsd.NeuralPhysicsCollisionSimulator()); // v16.72.0 강체/천/유체 SIMD 물리 · 음향 반사 메시
        services.AddSingleton(sp => new PromptFlowStudio.Services.OpenUsd.OpenUsdSpatialStudioRunner(sp)); // v16.72.0 USD 월드 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Twin.RealtimeDigitalTwinPhysicalSyncEngine()); // v16.73.0 IoT/센서 → USD/3DGS 트윈 동기화(지연 보상 · 양방향 명령)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Twin.NeuralSyntheticDataGeneratorEngine()); // v16.73.0 도메인 랜덤화 합성 RGB/깊이/세그/박스 데이터셋
        services.AddSingleton(sp => new PromptFlowStudio.Services.Twin.AutonomousSensorFeedbackLoopSimulator()); // v16.73.0 가상 센서 → 메트릭 스트림 → PID 피드백 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Twin.DigitalTwinStudioRunner(sp)); // v16.73.0 디지털 트윈 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.PathTracing.MaterialXBsdfShaderSynthesizer()); // v16.74.0 MaterialX standard_surface 파싱 · GGX/Lambert BSDF · 재질 진단
        services.AddSingleton(sp => new PromptFlowStudio.Services.PathTracing.AiRealtimeSpatiotemporalDenoiser()); // v16.74.0 SVGF 계열 시공간 디노이저 · Catmull-Rom 업스케일
        services.AddSingleton(sp => new PromptFlowStudio.Services.PathTracing.PathTracingStudioRunner(sp)); // v16.74.0 패스 트레이싱 스튜디오 루프
        services.AddSingleton(sp => sp.GetRequiredService<PromptFlowStudio.Services.PathTracing.PathTracingStudioRunner>().Tracer); // v16.74.0 볼류메트릭 패스 트레이서(러너 소유)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Mocap.NeuralMarkerlessPoseEstimationEngine()); // v16.75.0 다중 시점 SIMD 삼각측량 · 이상치 제거 · 단일 시점 리프팅 · One-Euro
        services.AddSingleton(sp => new PromptFlowStudio.Services.Mocap.AutoRiggingAndRetargetingSynthesizer()); // v16.75.0 오토 리깅 · 스킨 가중치 · DOF 클램프 리타겟
        services.AddSingleton(sp => new PromptFlowStudio.Services.Mocap.GenerativeCharacterPerformanceGuard()); // v16.75.0 대사/BCI 표정 · 비셈 · 발 고정 IK · 지면/충돌/관성 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.Mocap.MotionCaptureStudioRunner(sp)); // v16.75.0 모션 캡처 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAudio.RealtimeAcousticRaytracingEngine()); // v16.76.0 음선 추적 RT60/EDC · 이미지 소스 · Maekawa 회절 폐색
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAudio.Generative3DSoundscapeSynthesizer(48000)); // v16.76.0 절차적 음원 · 도플러 · VBAP 7.1.4 · FDN · 객체 메타데이터
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAudio.NeuralBinauralHrtfSpatializer(48000)); // v16.76.0 Brown–Duda HRTF · 머리 추적 · 베드 가상화
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAudio.SpatialAudioStudioRunner(sp)); // v16.76.0 공간 오디오 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Holo.RealtimeVolumetricHologramStreamerEngine()); // v16.77.0 SIMD 공간 양자화 · 키/델타 varint · CRC32 · MTU 분할 · PLI
        services.AddSingleton(sp => new PromptFlowStudio.Services.Holo.AiTelepresenceAvatarSynthesizer()); // v16.77.0 Eye-Contact 시선 교정 · BCI 미세 표정 · 립싱크 지연 · 전용 HRTF 바이노럴
        services.AddSingleton(sp => new PromptFlowStudio.Services.Holo.SpatialAnchorHolographicWorldRouter()); // v16.77.0 중력 정렬 앵커 Procrustes · P2P LWW 메시 · 릴레이 우회 · 데드 레커닝
        services.AddSingleton(sp => new PromptFlowStudio.Services.Holo.HolographicTeleportationStudioRunner(sp)); // v16.77.0 홀로그래픽 텔레포테이션 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.NeuralChoiceTreeEmotionEvaluator()); // v16.78.0 BCI · 시선 · 텍스트/음성 6차원 감정 융합 · JS 발산 · 선택지 softmax
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.ZeroSeamlessTransitionBlendingGuard(48000)); // v16.78.0 SIMD 프레임 · 4D 스플랫 · 등전력 영교차 오디오 크로스페이드 · 팝 감지
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.RealtimeInteractiveNarrativeBranchingEngine(sp.GetService(typeof(PromptFlowStudio.Services.Cinema.NeuralChoiceTreeEmotionEvaluator)) as PromptFlowStudio.Services.Cinema.NeuralChoiceTreeEmotionEvaluator)); // v16.78.0 서사 그래프 · 결정 창 프리페치 · 감독 개입
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.InteractiveCinemaStudioRunner(sp)); // v16.78.0 인터랙티브 시네마 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Editorial.NeuralRhythmMontageSynthesizer(48000)); // v16.79.0 온셋 플럭스 · 자기상관 템포 · 빗살 위상 · 감정 곡선 호흡 · 비트 몽타주 조립
        services.AddSingleton(sp => new PromptFlowStudio.Services.Editorial.AutonomousMultiCameraEditorialEngine(sp.GetService(typeof(PromptFlowStudio.Services.Editorial.NeuralRhythmMontageSynthesizer)) as PromptFlowStudio.Services.Editorial.NeuralRhythmMontageSynthesizer)); // v16.79.0 SIMD 모션/오디오 · 시선 · 180° 규칙 교차 편집
        services.AddSingleton(sp => new PromptFlowStudio.Services.Editorial.NleTimelineFormatExportGuard()); // v16.79.0 EDL(CMX3600) · FCPXML 1.9 · AAF(CFB) 무할당 스트리밍 익스포트
        services.AddSingleton(sp => new PromptFlowStudio.Services.Editorial.AutonomousEditorialStudioRunner(sp)); // v16.79.0 자율 편집 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicMastering.RealtimeIcvfxLedWallCameraTrackingEngine()); // v16.80.0 6DoF α-β 예측 · 오프-액시스 투영 · 인너 프러스텀 · 레이어 시차
        services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicMastering.NeuralColorGradingAcesMasteringPipeline()); // v16.80.0 AP0→ACEScg SIMD · 감정 MLP 룩 · 33³ LUT · SDR/HDR 마스터링
        services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicMastering.VirtualProductionRenderSyncGuard()); // v16.80.0 Genlock PLL · SMPTE TC · 셔터 위상 스왑 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicMastering.VirtualProductionMasteringStudioRunner(sp)); // v16.80.0 가상 프로덕션 & 마스터링 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.GenerativeVfx.NeuralParticleFluidDynamicsEngine(PromptFlowStudio.Services.GenerativeVfx.GenerativeVfxStudioRunner.DefaultCapacity)); // v16.81.0 SIMD 이중 밀도 완화 유체 · 신경망 에디 점성 · 표면장력
        services.AddSingleton(sp => new PromptFlowStudio.Services.GenerativeVfx.VolumetricFireSmokeAtmosphericSynthesizer()); // v16.81.0 프롬프트 이벤트 볼륨 화염/연기 · 압력 투영 · 광 투과율
        services.AddSingleton(sp => new PromptFlowStudio.Services.GenerativeVfx.InteractiveVfxCollisionSplashGuard()); // v16.81.0 OpenUSD 지형 · 3DGS 프록시 충돌 · 비산/파편 · 젖음 피드백
        services.AddSingleton(sp => new PromptFlowStudio.Services.GenerativeVfx.GenerativeVfxStudioRunner(sp)); // v16.81.0 제너레이티브 VFX 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalHuman.NeuralFacialMuscleSimulator()); // v16.82.0 ARKit 52 · FLAME 턱 LBS · 근육 동역학 SIMD 표정 합성
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalHuman.PhotorealisticMicroSkinSssSynthesizer()); // v16.82.0 Burley SSS 확산 · 모공/표정 주름 · 경로 추적 재질
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalHuman.AutonomousDigitalHumanGazeGuard()); // v16.82.0 감정선 시선 단속 · 깜빡임 · 동공 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalHuman.DigitalHumanPerformanceStudioRunner(sp)); // v16.82.0 디지털 휴먼 표정 연기 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.HairCloth.NeuralStrandHairDynamicsEngine()); // v16.83.0 DFTL 스트랜드 헤어/모피 · SIMD 형상 이완 · 바람/관성 · 바디 충돌
        services.AddSingleton(sp => new PromptFlowStudio.Services.HairCloth.GenerativeGarmentClothCollisionSynthesizer()); // v16.83.0 XPBD 의상 드레이핑 · 바디 캡슐 충돌 · 주름/변형률 스펙트럼
        services.AddSingleton(sp => new PromptFlowStudio.Services.HairCloth.NeuralHairClothShaderIntegrationGuard()); // v16.83.0 Marschner/KK 헤어 · 패브릭 셰이딩 · 딥 오패시티 · 에너지 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.HairCloth.NeuralHairClothStudioRunner(sp)); // v16.83.0 헤어 & 의상 다이내믹스 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Atmosphere.AtmosphericScatteringLightingEngine()); // v16.84.0 Rayleigh/Mie 대기 산란 · 투과율 LUT · SIMD 하늘 광원 · 안개/가시거리
        services.AddSingleton(sp => new PromptFlowStudio.Services.Atmosphere.DynamicMicroWeatherSimulatorEngine()); // v16.84.0 미세 기상(강우/눈 입자 · 회전 돌풍 · 운량 · 번개) · 미세 기후
        services.AddSingleton(sp => new PromptFlowStudio.Services.Atmosphere.EnvironmentalBiomeWetnessGuard()); // v16.84.0 지형 바이옴 습윤/적설 물 수지 · Lagarde 재질 오버레이 · 3DGS/캐릭터
        services.AddSingleton(sp => new PromptFlowStudio.Services.Atmosphere.GenerativeEnvironmentStudioRunner(sp)); // v16.84.0 제너레이티브 환경 & 미세 기상 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinematography.NeuralCameraTrajectoryEngine()); // v16.85.0 6DoF 카메라 궤적 최적화(장애물/지형/가시선) · 자동 줌 · 랙 포커스
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinematography.GenerativeShotFramingEvaluator()); // v16.85.0 삼등분/황금비/헤드룸/리드룸 구도 미학 점수 · 샷 타입 분류
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinematography.AnamorphicLensBokehSynthesizer()); // v16.85.0 아나모픽 타원 보케 · 스트릭 플레어 · 왜곡/색수차(3DGS · 경로 추적)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Cinematography.NeuralCinematographyStudioRunner(sp)); // v16.85.0 신경망 시네마토그래피 & 구도 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.FilmScore.NeuralCinematographicFilmScoreGenerator()); // v16.86.0 감정선/컷 적응 오케스트라 BGM 실시간 편곡 · SIMD 보이스 렌더
        services.AddSingleton(sp => new PromptFlowStudio.Services.FilmScore.AutonomousNeuralFoleySfxSynthesizer()); // v16.86.0 발걸음/의상 마찰/VFX 폭발·충돌 폴리 · 샘플 정확 동기 · 음속 지연
        services.AddSingleton(sp => new PromptFlowStudio.Services.FilmScore.HarmonicMixingAndAudioMasteringGuard(new PromptFlowStudio.Services.SpatialAudio.NeuralBinauralHrtfSpatializer())); // v16.86.0 R128/A85 라우드니스 · 트루 피크 · 대사 더킹 · 7.1.4 채널 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.FilmScore.FilmScoreStudioRunner(sp)); // v16.86.0 영화 음악 & 사운드 디자인 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.VideoDiffusion.RealtimeLatentVideoDiffusionEngine()); // v16.87.0 라텐트 CFG-DDIM 디노이징 · SIMD · 플로 워핑 노이즈 · 디코더
        services.AddSingleton(sp => new PromptFlowStudio.Services.VideoDiffusion.NeuralTemporalConsistencyGuard()); // v16.87.0 블록 옵티컬 플로 · 전후방 가림 · 디플리커 시간 블렌드
        services.AddSingleton(sp => new PromptFlowStudio.Services.VideoDiffusion.ControlNetSpatialConditioningSynthesizer()); // v16.87.0 ControlNet 깊이(지형)/포즈(모캡)/카메라 컨디셔닝
        services.AddSingleton(sp => new PromptFlowStudio.Services.VideoDiffusion.LatentVideoDiffusionStudioRunner(sp)); // v16.87.0 비디오 디퓨전 & 시공간 일관성 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Continuity.RealtimeScriptSupervisorContinuityEngine()); // v16.88.0 샷 연속성 검증(소품/조명/의상/상처/포즈/180° 축) · SIMD · 비동기 1슬롯
        services.AddSingleton(sp => new PromptFlowStudio.Services.Continuity.NeuralSceneCoherenceStoryBibleMatcher()); // v16.88.0 스토리 바이블 파서 · 캐릭터/장소/시대/타임라인/태양 정합성
        services.AddSingleton(sp => new PromptFlowStudio.Services.Continuity.AutomaticContinuityCorrectionGuard()); // v16.88.0 USD/3DGS/룩/의상/마크/포즈/VFX 자동 보정 · 수동 검토 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.Continuity.ScriptContinuityStudioRunner(sp)); // v16.88.0 스크립트 연속성 & 씬 정합성 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.Dubbing.NeuralVoiceCloningDubbingEngine()); // v16.89.0 음색 복제 · 운율 전이 · 13개 언어 소스-필터 더빙 · 등시성 · SIMD
        services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.Dubbing.GenerativeNeuralLipSyncSynthesizer()); // v16.89.0 음소→비짐 공동조음 · 근육 역동역학 피드포워드 · 동기 계측/보정 · 오디오 역추출
        services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.Dubbing.CulturalLocalizationSubtitleGuard()); // v16.89.0 관용구/유머/경어/민감 표현 적응 · CPS · 구도 기반 자막 배치
        services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.Dubbing.NeuralLipSyncDubbingStudioRunner(sp)); // v16.89.0 글로벌 로컬라이제이션 & 신경망 립싱크 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.ContentSecurity.NeuralInvisibleWatermarkEncoderEngine()); // v16.90.0 DCT QIM 비디오 · 차분 위상 오디오 · 3DGS 워터마크(키 = PROMPTFLOW_WM_KEY 환경변수)
        services.AddSingleton(sp => new PromptFlowStudio.Services.ContentSecurity.DeepfakeProtectionContentAuthenticityGuard(sp.GetRequiredService<PromptFlowStudio.Services.ContentSecurity.NeuralInvisibleWatermarkEncoderEngine>())); // v16.90.0 봉인 지문 HMAC-SHA512 · 재압축/국소 위변조 판정 · 역추적
        services.AddSingleton(sp => new PromptFlowStudio.Services.ContentSecurity.EnterpriseDigitalRightsMonetizationSynthesizer()); // v16.90.0 계약 DSL · 워터폴 정산 · 해시 체인 원장 · 유출 디바이스 차단
        services.AddSingleton(sp => new PromptFlowStudio.Services.ContentSecurity.ContentSecurityStudioRunner(sp)); // v16.90.0 콘텐츠 보안 & 디지털 저작권 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.ProductionCrew.AutonomousDirectorialConsensusEvaluator()); // v16.91.0 감정 목표 함수 합의 · 역할 권한 · Hedge 신뢰 가중 · 박자 정렬 · 오버라이드
        services.AddSingleton(sp => { var g = new PromptFlowStudio.Services.ProductionCrew.RealtimeProductionSwarmExecutionGuard(); g.RegisterStudios(sp); return g; }); // v16.91.0 v16.71~90 스튜디오 20종 프레임/메모리 예산 · 선점/거부/격리
        services.AddSingleton(sp => new PromptFlowStudio.Services.ProductionCrew.MultiAgentVirtualProductionCrewOrchestratorEngine(sp.GetRequiredService<PromptFlowStudio.Services.ProductionCrew.AutonomousDirectorialConsensusEvaluator>(), sp.GetRequiredService<PromptFlowStudio.Services.ProductionCrew.RealtimeProductionSwarmExecutionGuard>())); // v16.91.0 크루 7 메시지 버스 · 제안 정책 · 작업 DAG 분배
        services.AddSingleton(sp => new PromptFlowStudio.Services.ProductionCrew.MultiAgentCrewStudioRunner(sp)); // v16.91.0 멀티에이전트 프로덕션 크루 스튜디오 루프
        services.AddSingleton(sp => new PromptFlowStudio.Services.CloudVerification.CloudSourceCodeIntegrityVerifierEngine()); // v16.92.0 소스 구문 정합성 & Zero-Alloc 정적/동적 검증
        services.AddSingleton(sp => new PromptFlowStudio.Services.CloudVerification.RealtimeUiExceptionSelfHealingGuard()); // v16.92.0 UI 예외 탐지 · 스냅샷 롤백 · 진단 매니페스트
        services.AddSingleton(sp => new PromptFlowStudio.Services.CloudVerification.FullUiInteractiveMenuAutomatedDriver(sp.GetRequiredService<PromptFlowStudio.Services.CloudVerification.RealtimeUiExceptionSelfHealingGuard>())); // v16.92.0 UI 가상 순회 드라이버
        services.AddSingleton(sp => new PromptFlowStudio.Services.CloudVerification.CloudVerificationStudioRunner(sp)); // v16.92.0 클라우드 소스 검증 & UI 인터랙티브 검사 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.SelfFix.RealUiSelfHealingStudioRunner(sp)); // v16.93.0 실 RenderTree DOM 구동 · 소스 자동 수정 · 폐루프 회귀 검증
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveTelemetry.RealtimeTelemetryBottleneckProfilerEngine()); // v16.94.0 µs 병목 프로파일러 · GC/CPU/스레드 풀/렌더 스태킹
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveTelemetry.AutonomousPerformanceSelfTuningSynthesizer(sp.GetRequiredService<PromptFlowStudio.Services.LiveTelemetry.RealtimeTelemetryBottleneckProfilerEngine>(), sp.GetService<PromptFlowStudio.Services.ProductionCrew.RealtimeProductionSwarmExecutionGuard>())); // v16.94.0 버퍼/SIMD/스레드 풀 셀프 튜닝
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveTelemetry.HybridCloudEdgeDeploymentRecoveryGuard()); // v16.94.0 델타 OTA · 체크포인트 롤백
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveTelemetry.LiveTelemetryStudioRunner(sp)); // v16.94.0 실시간 텔레메트리 & 자율 튜닝 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.EnterpriseSla.ThreadDeadlockAutoProbingEngine()); // v16.95.0 멈춤/무한 루프/락 교착/기아/메모리 자율 감지
        services.AddSingleton(sp => new PromptFlowStudio.Services.EnterpriseSla.ZeroDowntimeProcessFailoverSynthesizer()); // v16.95.0 서킷 브레이커 · LKG 핫스왑 페일오버
        services.AddSingleton(sp => new PromptFlowStudio.Services.EnterpriseSla.EnterpriseSlaAvailabilityGuard(sp.GetRequiredService<PromptFlowStudio.Services.EnterpriseSla.ThreadDeadlockAutoProbingEngine>(), sp.GetRequiredService<PromptFlowStudio.Services.EnterpriseSla.ZeroDowntimeProcessFailoverSynthesizer>())); // v16.95.0 99.999% SLA 자율 복구 · 해시 체인 매니페스트
        services.AddSingleton(sp => new PromptFlowStudio.Services.EnterpriseSla.EnterpriseSlaStudioRunner(sp)); // v16.95.0 엔터프라이즈 SLA & 무중단 복구 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.GpuSwarm.DistributedHeterogeneousGpuTopologyEngine()); // v16.96.0 이기종 GPU 토폴로지 · VRAM/처리율 SIMD 프로빙
        services.AddSingleton(sp => new PromptFlowStudio.Services.GpuSwarm.DynamicCloudRenderSwarmLoadBalancer(sp.GetRequiredService<PromptFlowStudio.Services.GpuSwarm.DistributedHeterogeneousGpuTopologyEngine>())); // v16.96.0 적응 타일/프레임 LPT 로드밸런서
        services.AddSingleton(sp => new PromptFlowStudio.Services.GpuSwarm.LowLatencyFrameAssemblyFallbackGuard(sp.GetRequiredService<PromptFlowStudio.Services.GpuSwarm.DistributedHeterogeneousGpuTopologyEngine>(), sp.GetRequiredService<PromptFlowStudio.Services.GpuSwarm.DynamicCloudRenderSwarmLoadBalancer>())); // v16.96.0 제로카피 조립 · 로컬 페일백
        services.AddSingleton(sp => new PromptFlowStudio.Services.GpuSwarm.GpuClusterSwarmStudioRunner(sp)); // v16.96.0 분산 GPU 클러스터 & 렌더 스웜 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialXr.OpenXrWebXrSpatialTrackingEngine()); // v16.97.0 OpenXR/WebXR 6DoF 예측 · 양안 뷰/투영
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialXr.FoveatedCompressionReprojectionSynthesizer(PromptFlowStudio.Services.SpatialXr.SpatialComputingStudioRunner.EyeW, PromptFlowStudio.Services.SpatialXr.SpatialComputingStudioRunner.EyeH)); // v16.97.0 시선 포비에이티드 압축 · ATW/PTW 재투영
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialXr.SpatialHandGestureHapticGuard()); // v16.97.0 26관절 제스처 · 소품 충돌 · 햅틱 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialXr.SpatialComputingStudioRunner(sp)); // v16.97.0 공간 컴퓨팅 & WebXR 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialSlam.NeuralSpatialSlamTrackingEngine(PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.W, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.H, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.F, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.F, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.Cx, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.Cy)); // v16.98.0 6DoF SLAM · 퇴화 인지 ICP · 루프 폐쇄
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialSlam.LidarPointCloudMeshReconstructionSynthesizer()); // v16.98.0 TSDF 융합 · Surface Nets 메쉬 · OpenUSD/3DGS 변환
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialSlam.SpatialPlaneDetectionAnchorGuard()); // v16.98.0 RANSAC 평면 분할 · 부양/관통 앵커 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner(sp)); // v16.98.0 신경망 공간 SLAM & 3D 환경 재구성 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwinWorld.RealtimeDigitalTwinSpatialSyncEngine()); // v16.99.0 실물↔트윈 6DoF 양방향 동기화 · Horn 캘리브레이션
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwinWorld.UnifiedMultiPhysicsWorldDynamicsSynthesizer()); // v16.99.0 강체 · 천(v16.83) · 유체(v16.81) · 기상(v16.84) 통합 물리
        services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwinWorld.DigitalTwinWorldStudioRunner(sp)); // v16.99.0 디지털 트윈 & 옴니버스 월드 스튜디오(라이브 USD 동기화 · 앵커 락 내장)
        services.AddSingleton(sp => new PromptFlowStudio.Services.HoloTelepresence.HolographicLightFieldTelepresenceEngine()); // v17.00.0 45 시점 라이트필드 SIMD 슬라이싱 · 볼류메트릭 6DoF 텔레프레전스 코덱
        services.AddSingleton(sp => new PromptFlowStudio.Services.HoloTelepresence.UniversalAgiCinematicDirectorSynthesizer()); // v17.00.0 관객 감응 서사 분기 · 카메라/조명/BGM/표정 오케스트레이션
        services.AddSingleton(sp => new PromptFlowStudio.Services.HoloTelepresence.SpatialHapticCognitiveInteractionGuard()); // v17.00.0 햅틱 역각/진동 · 관통 차단 · 사이버 멀미 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.HoloTelepresence.HolographicTelepresenceStudioRunner(sp)); // v17.00.0 홀로그래픽 텔레프레전스 & AGI 디렉터 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.RoboticMoCo.RoboticMoCoInverseKinematicsEngine()); // v17.01.0 6축 해석 IK(8해 최근접) · SIMD 저크 제한 프로파일 · 토크 부하 · 패스 사전 검증
        services.AddSingleton(sp => new PromptFlowStudio.Services.RoboticMoCo.PhysicalStageDmxActuatorSynthesizer()); // v17.01.0 DMX512 4 유니버스 · Art-Net/ArtSync · 조명/기상/VFX → 무대 액추에이터
        services.AddSingleton(sp => new PromptFlowStudio.Services.RoboticMoCo.HardwareInTheLoopSafetyEStopGuard()); // v17.01.0 SSM 분리 감시 · SLAM 평면/침범 불가 구역 · 동기 정지 · 이중 릴레이 E-Stop
        services.AddSingleton(sp => new PromptFlowStudio.Services.RoboticMoCo.RoboticMoCoStudioRunner(sp)); // v17.01.0 로보틱 MoCo & 물리 스테이지 HIL 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualArena.DeterministicRollbackNetcodeEngine()); // v17.03.0 GGPO형 결정론 롤백(Q16 정수 물리 · 64 링 스냅샷 · FNV 체크섬 · 예측 12 · 입력 지연)
        services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualArena.MassiveSpatialInterestManagementSynthesizer()); // v17.03.0 AOI 공간 해시(64×64) · FoV 원뿔 셀 컬링 · 우선순위 누적 · 시청자 바이트 예산
        services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualArena.ClientSpatialPredictionClockSyncGuard()); // v17.03.0 NTP 최소 RTT + PI 클록 드리프트 보정 · 에르미트 보간 · 데드 레커닝 · 오차 수렴
        services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualArena.VirtualArenaNetcodeStudioRunner(sp)); // v17.03.0 가상 아레나 & 롤백 넷코드 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.MassiveCrowd.MassiveAgenticCrowdSimulationEngine()); // v17.04.0 10만 군중(공간 해시 · RVO2 ORCA · 감정 전파 SIMD · 밀도장 플로킹)
        services.AddSingleton(sp => new PromptFlowStudio.Services.MassiveCrowd.SwarmStadiumAcousticsSynthesizer()); // v17.04.0 16 섹터 함성/박수/챈트/야유/파도 · 음압 모델 · v16.76 HRTF 공간화(전용 인스턴스)
        services.AddSingleton(sp => new PromptFlowStudio.Services.MassiveCrowd.BehavioralLodSpatialCullingGuard()); // v17.04.0 FoV/거리 행동 LOD(풀 본·ORCA / 플로킹 / 밀도장) · 16.6 ms 예산 피드백
        services.AddSingleton(sp => new PromptFlowStudio.Services.MassiveCrowd.MassiveCrowdStudioRunner(sp)); // v17.04.0 대규모 군중 시뮬레이션 & 음향 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.MasterControl.RealtimeBroadcastVisionSwitcherEngine()); // v17.05.0 16 입력 비전 스위처(핀 메모리 맵 · 컷/디졸브/와이프 SIMD · T-바 · 멀티뷰어 · AFV)
        services.AddSingleton(sp => new PromptFlowStudio.Services.MasterControl.AutonomousLiveInstantReplaySynthesizer()); // v17.05.0 ISO 링버퍼 자동 하이라이트 클립 · 움직임 보상 보간 120 fps 슬로모션 · 송출 큐
        services.AddSingleton(sp => new PromptFlowStudio.Services.MasterControl.OnAirBroadcastSignalFailSafeGuard()); // v17.05.0 ST 2110 -20/-30/-40 · ST 2022-7 무결절 병합 · PTP 서보 · 결손/블랙 비상 컷
        services.AddSingleton(sp => new PromptFlowStudio.Services.MasterControl.BroadcastMasterControlStudioRunner(sp)); // v17.05.0 방송 비전 스위처 & 마스터 컨트롤 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.ArGraphics.FreeDTrackingProtocolLensDistortionEngine()); // v17.06.0 FreeD D1 트래킹(체크섬 · SIMD 역직렬화) · 렌즈 표 FOV/K1/K2 · ST 맵 · 지연 정합 · 캘리브레이션
        services.AddSingleton(sp => new PromptFlowStudio.Services.ArGraphics.OnAirArTelestrationSportsTelemetrySynthesizer()); // v17.06.0 지면 고정 AR 텔레스트레이션(링 · 궤적 · 거리 · 화살표 · 오프사이드) · 선수 가림 · 스포츠 HUD
        services.AddSingleton(sp => new PromptFlowStudio.Services.ArGraphics.AncillaryDataKeyFillGraphicsCompositingGuard()); // v17.06.0 Key/Fill 프리멀티/스트레이트 가드 · 합법 레벨 · 타이틀 세이프 · ST 2110-40 ANC(CDP · ATC · FreeD)
        services.AddSingleton(sp => new PromptFlowStudio.Services.ArGraphics.BroadcastArGraphicsStudioRunner(sp)); // v17.06.0 방송 AR 그래픽 & 텔레스트레이션 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.Deflicker.SubPixelOpticalFlowTexturePinningEngine()); // v17.07.0 3단 피라미드 서브픽셀 LK 흐름 · 신뢰도 · 앵커 텍스처 좌표 트랙 · SIMD 피닝
        services.AddSingleton(sp => new PromptFlowStudio.Services.Deflicker.HighFrequencyDetailBlindDeflickerSynthesizer()); // v17.07.0 라플라시안 조명/질감 분리 · 질감 층 디플리커 · 구조 보호 · 가림 무효화
        services.AddSingleton(sp => new PromptFlowStudio.Services.Deflicker.GenerativeMorphingSuppressionGuard()); // v17.07.0 챔퍼 윤곽 거리 타일 모핑 감지 · 영역 억제/프레임 롤백
        services.AddSingleton(sp => new PromptFlowStudio.Services.Deflicker.TemporalDeflickerStudioRunner(sp)); // v17.07.0 시공간 디플리커 & 텍스처 피닝 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticMatting.RealtimeSemanticAlphaMattingEngine()); // v17.08.0 마커리스 시맨틱 트라이맵 · SIMD α 사영/전경 언믹스 · 반투명 회귀
        services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticMatting.GenerativeVirtualSetZDepthSynthesizer()); // v17.08.0 OpenUSD 가상 세트 · SLAM 바닥 앵커 · Z-Depth 오클루전
        services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticMatting.DynamicEnvironmentRelightingGuard()); // v17.08.0 HDRI SH9 리라이팅 · 림/스페큘러 · 라이트 랩 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticMatting.SemanticMattingStudioRunner(sp)); // v17.08.0 시맨틱 매팅 & 가상 세트 융합 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.LedVolume.InnerFrustumOffAxisProjectionEngine()); // v17.09.0 FreeD 예측 · 왜곡 역보정 내부 프러스텀 · Kooima SIMD
        services.AddSingleton(sp => new PromptFlowStudio.Services.LedVolume.LedWallMoireSuppressionSynthesizer()); // v17.09.0 OLPF/착란원 모아레 억제 · 외부 프러스텀 환경 맵
        services.AddSingleton(sp => new PromptFlowStudio.Services.LedVolume.PtpGenlockPhaseSyncTearingGuard()); // v17.09.0 PTP/SMPTE 2059 젠록 · 티어링/블랙 밴드 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.LedVolume.IcvfxLedWallStudioRunner(sp)); // v17.09.0 인카메라 VFX LED 월 & 젠록 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.ColorScience.RealtimeAcesColorTransformEngine()); // v17.10.0 ACES 2.0 색공간 SIMD 변환 · 사면체 3D LUT 33/65
        services.AddSingleton(sp => new PromptFlowStudio.Services.ColorScience.OnSetLiveDitGradingColorDecisionSynthesizer()); // v17.10.0 ASC CDL 3-Way 그레이딩 · 퍼레이드/벡터스코프/False Color
        services.AddSingleton(sp => new PromptFlowStudio.Services.ColorScience.LedSpectralMetamerismGamutClippingGuard()); // v17.10.0 LED 메타메리즘 분광 역산 · ACES RGC 색역 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.ColorScience.LiveDitColorStudioRunner(sp)); // v17.10.0 라이브 DIT 컬러 사이언스 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.MarkerlessMocap.MarkerlessMultiViewPoseTriangulationEngine()); // v17.11.0 멀티뷰 에피폴라 게이트 + SIMD DLT 3D 관절 삼각측량
        services.AddSingleton(sp => new PromptFlowStudio.Services.MarkerlessMocap.BiomechanicalSkeletonRetargetingSynthesizer()); // v17.11.0 디지털 휴먼/OpenUSD 리그 리타게팅 · 발 고정 FABRIK
        services.AddSingleton(sp => new PromptFlowStudio.Services.MarkerlessMocap.KinematicJointLimitBonePenetrationGuard()); // v17.11.0 튐/떨림 필터 · ROM 락 · 자가 관통 차단
        services.AddSingleton(sp => new PromptFlowStudio.Services.MarkerlessMocap.MarkerlessMocapStudioRunner(sp)); // v17.11.0 AI 무마커 모션 캡처 & 리타게팅 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.FacialPerformance.NeuralFacialLandmarkBlendshapeEngine()); // v17.12.0 468 랜드마크 → 52 ARKit 블렌드셰이프 SIMD 역산
        services.AddSingleton(sp => new PromptFlowStudio.Services.FacialPerformance.AudioToFaceVisemeMotionSynthesizer()); // v17.12.0 Audio2Face FFT/MFCC/LPC 비짐 립싱크
        services.AddSingleton(sp => new PromptFlowStudio.Services.FacialPerformance.BiologicalEyeGazeMicroTremorGuard()); // v17.12.0 생체 시선 · 미세 홱보기 · 무의식 깜빡임 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.FacialPerformance.FacialPerformanceStudioRunner(sp)); // v17.12.0 AI 페이셜 퍼포먼스 & Audio2Face 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousHand.DexterousFingerPoseTriangulationEngine()); // v17.13.0 42 키포인트 SIMD DLT 삼각측량 · 가림 뼈 길이 복원
        services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousHand.VirtualPropContactGraspingSynthesizer()); // v17.13.0 소품 SDF 접촉 매니폴드 · 관통 락 · 파지 부착
        services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousHand.InterDigitalSelfCollisionDislocationGuard()); // v17.13.0 ROM 클램프 · 탈구 복귀 · 지간 충돌 분리
        services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousHand.DexterousHandStudioRunner(sp)); // v17.13.0 AI 덱스터러스 핸드 & 소품 접촉 스튜디오
        services.AddSingleton(sp => new PromptFlowStudio.Services.AnatomicalTissue.NeuralAnatomicalMuscleFasciaEngine()); // v17.14.0 근육 팽창 · 근막 슬라이딩 SIMD 피부 변형
        services.AddSingleton(sp => new PromptFlowStudio.Services.AnatomicalTissue.AnatomicalTissueStudioRunner(sp)); // v17.14.0 AI 해부학 근육 & 연조직 스튜디오(지글/주름 합성기 · 체적/교차 가드 내장)
        services.AddSingleton(sp => new PromptFlowStudio.Services.StrandHair.StrandHairPbdDynamicsEngine()); // v17.15.0 가이드 256 × 자식 400 스트랜드 SIMD PBD
        services.AddSingleton(sp => new PromptFlowStudio.Services.StrandHair.HairGroomingStudioRunner(sp)); // v17.15.0 AI 헤어 동역학 & 뉴럴 그루밍 스튜디오(Marschner/기류 합성기 · 두피 앵커/SDF 가드 내장)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Garment.GarmentDynamicsStudioRunner(sp)); // v17.16.0 AI 다층 의상 동역학 & 드레이핑 스튜디오(전용 근육 피부 · 다층 PBD · 직물 합성기 · 레이어/헤어 가드 내장)
        services.AddSingleton(sp => new PromptFlowStudio.Services.SkinOptics.SkinSssHemodynamicsStudioRunner(sp)); // v17.17.0 AI 피부 SSS & 미세 혈류 스튜디오(분리형 SSS · 혈류 홍조 · 각막/눈물막 가드 · v17.12 페이셜 결선)
        services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralVoice.NeuralVoiceStudioRunner(sp)); // v17.18.0 AI 뉴럴 음성 & 성도 음향 스튜디오(감정 운율 · 켈리-록바움 성도 · A/V 지연/파열음 가드 내장)
        services.AddSingleton(sp => new PromptFlowStudio.Services.FluidWetness.FluidWetnessStudioRunner(sp)); // v17.19.0 AI 다상 유체 습윤 & 액적 스튜디오(피막 확산 · 직물 위킹 · 액적 활주 · 젖은 모발/의상 흡착 가드 내장)
        services.AddSingleton(sp => new PromptFlowStudio.Services.GaussianAvatar.GaussianAvatarStudioRunner(sp)); // v17.20.0 AI 4D 가우시안 아바타 스튜디오(DQS 가우시안 스키닝 · SH 차수 3 재조명 · 16 비트 래딕스 정렬 타일 스플래팅 가드 내장)
        services.AddSingleton(sp => new PromptFlowStudio.Services.StereoXr.SpatialComputingXrStudioRunner(sp)); // v17.22.0 AI 공간 컴퓨팅 & WebXR 스트리밍 스튜디오(양안 포비티드 VRS · WebRTC RTP/L16/데이터 채널 · M2P ATW 가드)
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAcoustics.SpatialAcousticsStudioRunner(sp)); // v17.23.0 AI 6DoF 공간 음향 & 광선 추적 스튜디오(BVH 음향 광선 추적 · 재질 흡음 RT60 · 6DoF HRTF 바이노럴 · 도플러/회절 위상 가드)
        services.AddSingleton<PromptFlowStudio.Services.Media.CardNewsReelsGenerator>();     // v17.30.1 카드뉴스 9:16 릴스 합성(ffmpeg drawtext+concat · HTML 템플릿 폴백)
        services.AddSingleton(PromptFlowStudio.Services.SocialMedia.InstagramReelsService.Create); // v17.30.1 IG Reels 발행(컨테이너→FINISHED 폴링→media_publish · 로컬은 브릿지 위임)
        services.AddSingleton(sp => new PromptFlowStudio.Services.AudioTelemetry.RealtimeAudioTelemetryMonitor(sp)); // v17.29.0 실시간 오디오/텔레메트리 모니터 뼈대(RMS · 피크 · 영교차 · 로그 밴드 스펙트럼)
        services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicDirector.CinematicDirectorStudioRunner(sp)); // v17.39.0 AI 샷 구도(4 카메라 · SIMD 후보 · 삼분선/리드룸/DoF) · 발화/충격 컷 합성 · 180° 선/점프컷 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.StageLighting.IntelligentStageLightingStudioRunner(sp)); // v17.41.0 무빙헤드 에르미트 추적 · Art-Net DMX512 44 Hz · CCT/볼류메트릭 빔 합성 · BT.1702 PSE/조도 광학 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.Pyrotechnics.NeuralVfxPyrotechnicsStudioRunner(sp)); // v17.42.0 32³ 연소 유체(부력 · 보텍스 컨파인먼트 · SIMD Jacobi) · 65,536 스파크 링버퍼 · 발광 포인트 라이트 → v17.41 · 버짓/NaN/에너지 보존 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.LensOptics.CinematicLensOpticsStudioRunner(sp)); // v17.43.0 아나모픽 2.0x 왜곡/색수차/브리딩 SIMD LUT · 타원 보케/블루 스트릭/할레이션 · ACEScc 블룸 니 · 4 ms 피라미드 적응
        services.AddSingleton(sp => new PromptFlowStudio.Services.GenlockFrustum.GenlockFrustumLedWallStudioRunner(sp)); // v17.44.0 곡면 LED 오프액시스 전단 SIMD · 인너(v17.43 FoV)/아우터(v17.41 HDRI 링버퍼) 듀얼 프러스텀 · PTP 젠록 ±0.5 µs · 1 µs 버퍼 보정 가드
        services.AddSingleton<PromptFlowStudio.Services.OpticalInertialTracking.CameraTrackingStudioRunner>(); // v17.45.0 FreeD D1 1000 Hz 무할당 디코더 · 광학-관성 12D EKF · 렌더 지연 3차 테일러 예측/에르미트 Swim 상쇄 · 오클루전 데드레커닝 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.BroadcastMcr.BroadcastMcrStudioRunner(sp));
        services.AddSingleton(sp => new PromptFlowStudio.Services.Showrunner.InteractiveShowrunnerStudioRunner(sp));
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveShowroom.LiveCommerceStudioRunner(sp)); // v17.68.0 3DGS 상품(≤ 5만) R 손목 앵커 · 360° 쇼케이스 광택 · MCR 상품 클로즈업 · 구매 → 100 ms 감사 멘트/박수·엄지척 · 타임세일/재고/품절 CTA 배너 · 과장 광고 치환/차단 · 가격 정합성 배너 차단 // v17.67.0 v17.62 채팅 `!drop`·`!confetti` → 500 풀 3DGS 프롭 · v17.57 골격 캡슐 + v17.59 의상 셸 반발/찌그러짐/파열 · 쇼러너 피격 애드리브(FSM 강제 발언권 · 표정) + v17.66 MCR 긴급 CU · 40/s 버킷 · 1.5 s 쿨다운 · 카메라 밖 재활용 // v17.66.0 v17.55 세트 30만 + v17.57/58 아바타 2~3 기 + PPL 소품 공용 버퍼 → 단일 래딕스 깊이 큐 통합 래스터(v17.56 그림자 체인) · PGM/PVW Cut/Mix/Wipe · 800 샘플/프레임 A/V 동기 · v17.62 링 탭 · v17.41 DMX 키 · PPL 가림 60 % 회피 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.Talkshow.MultiAvatarTalkshowStudioRunner(sp)); // v17.65.0 호스트+게스트 ≤ 2 턴테이킹 FSM(EOU → 150~300 ms 인계 · 무할당 이벤트) · 상호 시선(회피/단속/깜빡임) + 경청 리액션(끄덕임/미소/놀람 · v17.58 52 · v17.60 수동작) · v17.39 투샷/OTS/바스트/리액션 자동 컷(≥ 0.35 s) · 우선순위 오버랩/데드락 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalDubbing.GlobalDubbingStudioRunner(sp)); // v17.64.0 한국어 어절 청취 → 3 어절 청크 스트리밍 번역(≤ 200 ms) · 언어별 음소 링 · v17.32 원본 음색 다국어 합성 + v17.60 감정 운율 · 플랫폼별 언어 트랙 라우팅(v17.62) · 오디오 마스터 시계 ARKit 가우시안 립싱크(< 50 ms) · v17.58 찢어짐 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.StreamGateway.StreamGatewayClipperStudioRunner(sp)); // v17.62.0 v17.49 인코더 탭 → 32 MB 압축 NAL/AAC 링(30 s) → 최인접 IDR 무재인코딩 fMP4 리먹스(≤ 10 ms) · Twitch IRC/TLS · YouTube 폴링 · 치지직 WSS 게이트웨이 · 지터 백오프 · 500 msg/s 우선/테일 드롭 배압
        services.AddSingleton(sp => new PromptFlowStudio.Services.InfluencerLive.VirtualInfluencerLiveStudioRunner(sp)); // v17.61.0 채팅 NDJSON/IRCv3 무할당 파싱 · 의도 분류 · 100 ms 리액션 선점(v17.60 연기) · 250 % 스파이크 → 15~30 s 9:16 fMP4(v17.52 자막 · v17.49) → v17.51 배포 · 누출 버킷/레이드/비속어 섀도우밴
        services.AddSingleton(sp => new PromptFlowStudio.Services.AvatarActing.AvatarActingDirectorStudioRunner(sp)); // v17.60.0 텍스트-투-제스처 트랜스포머(셀프 어텐션 · 운율 비트) · FABRIK 상지 IK + v17.33 보행 레이어 블렌딩 · 스윙-트위스트 관절 한계 가드 · v17.58 표정/손가락
        services.AddSingleton(sp => new PromptFlowStudio.Services.GaussianClothHair.GaussianClothHairStudioRunner(sp)); // v17.59.0 PBD 직물 격자/헤어 스트랜드(SIMD 베를레 · GS 구속 · 공분산 정렬) · 공기역학 바람/24 관절 캡슐 충돌 · 신장 110 % 클램프 · 스윕 구 CCD
        services.AddSingleton(sp => new PromptFlowStudio.Services.GaussianFaceHand.GaussianFacialHandStudioRunner(sp)); // v17.58.0 ARKit 52 가우시안 페이셜 델타(SIMD) · 양손 20 관절 핑거 DQS · 핀치 접촉면 · 찢어짐/관통 가드 · v17.32 더빙 음소
        services.AddSingleton(sp => new PromptFlowStudio.Services.SkinnedSplatAvatar.DualQuaternionGaussianAvatarStudioRunner(sp)); // v17.57.0 래그돌 24 관절 DQS 가우시안 스키닝(μ · Σ SIMD) · 캡슐 계층 + 광원 뎁스 셀프 섀도우 · det 체적 가드 · v17.33/v17.37 소스
        services.AddSingleton(sp => new PromptFlowStudio.Services.SplatRelighting.GaussianSplatRelightingStudioRunner(sp)); // v17.56.0 키 라이트 뎁스 맵 PCSS/SIMD PCF 접지 그림자 · 3DGS 환경광 역추출 → 디지털 휴먼 셰이더/DIT · 슬로프 바이어스 · 3.5 ms 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.SplatEnvironment.GaussianSplatEnvironmentStudioRunner(sp)); // v17.55.0 SIMD 조기 컬링 · 래딕스 정렬 · 16×16 타일 전방-후방 블렌딩 3DGS · 차수 3 SH · VRAM/LOD 가드 · v17.44 인너/v17.46 확장 결선
        services.AddSingleton(sp => new PromptFlowStudio.Services.RhythmAction.RhythmActionMvStudioRunner(sp)); // v17.54.0 마디/드롭 리듬 컷 시퀀서 · 0.35 s 최소 유지/180° 가드 · 60~120 Hz 베이스 반응 DMX/파이로/플레어 · v17.39/41/42/43 결선
        services.AddSingleton(sp => new PromptFlowStudio.Services.BeatSync.BeatSyncAudioMixStudioRunner(sp)); // v17.53.0 스펙트럼 플럭스 비트 그리드 · 음성 사이드체인 더킹 · −14 LUFS/−1 dBTP 가드 · v17.52 단어/v17.39 컷/v17.49 인코딩 결선
        services.AddSingleton(sp => new PromptFlowStudio.Services.MotionTypography.MotionOverlayStudioRunner(sp)); // v17.52.0 SDF 아틀라스 단어 단위 Pop/Typewriter/Bounce 자막 · 진행률/투표/배지/전환 RGBA Z 합성 · 3 플랫폼 데드존 교집합 세이프 존 · v17.49 인코딩 직전 NV12 합성
        services.AddSingleton(sp => new PromptFlowStudio.Services.OmniChannel.OmniChannelEngagementStudioRunner(sp)); // v17.51.0 9:16 마스터 → IG(v17.48) · YouTube Shorts(Data v3 재개) · TikTok(v2) 분기 · 플랫폼 메타 변환 · 댓글 의도 분류/다국어 답글 · YT 유닛/TT 분당 한도 · 유사도 ≤ 40% · 45~180 s 페이싱
        services.AddSingleton(sp => new PromptFlowStudio.Services.ViralLoop.ViralLoopAutomationStudioRunner(sp)); // v17.50.0 트렌드 큐레이션(피드+Graph) · 분해→합성→GPU 인코딩→업로드 4 단계 · 골든아워/일일 한도 · 1/6/24 h 인사이트 Thompson 자가 튜닝 · 섀도우밴 동결(48 h 수동 재개)
        services.AddSingleton(sp => new PromptFlowStudio.Services.VerticalTranscode.VerticalTranscoderStudioRunner(sp)); // v17.49.0 MF 하드웨어 → 소프트웨어 H.264 · 관리 코드 Baseline 폴백 · AAC-LC · fMP4 4 MB 세그먼트 → v17.48 직결 · 스튜디오 직결 9:16 합성 · 8 Mbps QP 가드 · 로컬 HTTPS 웹훅
        services.AddSingleton(sp => new PromptFlowStudio.Services.GraphBridge.GraphApiBridgeStudioRunner(sp)); // v17.48.0 Graph API v21.0 Utf8JsonReader 무할당 인제스천/서명 웹훅 · rupload 4 MB 청크 재개 업로드 → 폴링 → media_publish · 토큰 버킷 200/h · 429 지수 백오프 · AES-GCM 토큰 · 장기 토큰 갱신
        services.AddSingleton(sp => new PromptFlowStudio.Services.ViralReels.ViralReelsStudioRunner(sp)); // v17.47.0 트렌딩 피드 무할당 파싱/랭킹 · 3초 훅 SIMD 분해 · 긴장 아크/더빙/세트 치환/펀치인/플레어/폴리 9:16 리믹스 · SSIM 60% · 원본 오디오 지문 차단 · 크레딧 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.SetExtension.VirtualSetExtensionStudioRunner(sp)); // v17.46.0 LED 볼륨 Sutherland-Hodgman 오버런 · v17.27 월드 세트 확장 SIMD 투영 · LED↔크로마 게인 매칭/페더/디스필 · 에지 록 래치 · 그린 블리딩 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.NarrativeTimeline.NarrativeTimelineStudioRunner(sp)); // v17.40.0 Fountain 각본 → 5 트랙 시퀀서 · 긴장 아크/프록세믹스 블로킹 · 분기 관성 보간 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.ProceduralFoley.ProceduralFoleyStudioRunner(sp)); // v17.38.0 AI 헤르츠 접촉 모달 폴리(16 모드 바이쿼드 뱅크 · SIMD) · 샷 노이즈 마찰/의상 스침 · 4× 트루 피크 소프트 리미터/LUFS → v17.23 바이노럴
        services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralRagdoll.NeuralRagdollStudioRunner(sp)); // v17.37.0 AI 하이브리드 액티브 래그돌(24 관절 캡슐 · 모터 PD · SIMD 전환 가중) · 전도 판독/절차 기립 · 특이점/각속도/해밀토니안 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousManipulation.DexterousManipulationStudioRunner(sp)); // v17.36.0 AI 덱스그랩(SDF 접촉 · 힘 폐쇄) · 다자간 인계/폐쇄 체인 운반 · 마찰 원뿔 슬립/관통 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.ContactLocomotion.ContactLocomotionStudioRunner(sp)); // v17.35.0 AI 3D 발 배치 · 계단 승하강 합성 · ZMP 동적 안정(LIPM/DCM · 포착 스텝 · 각운동량)
        services.AddSingleton(sp => new PromptFlowStudio.Services.VisualQa.HeadlessVisualQaStudioRunner(sp)); // v17.34.0 AI 헤드리스 프레임버퍼(Blazor RenderTree 구조 래스터 + 3D) · SSIM 시각 회귀 · 계단 ZMP 시각 디버깅 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.MotionMatching.NeuralMotionMatchingStudioRunner(sp)); // v17.33.0 AI 관성 모션 매칭(AABB SIMD 검색) · 지형 FABRIK/ZMP 균형 · 풋스케이팅/과신전 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.LiveDubbing.MultilingualDubbingLipSyncStudioRunner(sp)); // v17.32.0 AI 다국어 동시 더빙(d-vector 음색 클로닝 · 48 kHz 합성) · 음소 60 fps 립싱크 · WSOLA/Hermite A/V 가드
        services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualLive.VirtualLiveBroadcastStudioRunner(sp)); // v17.31.0 AI 버추얼 라이브 방송(RTMP FLV · WebRTC RTP ABR 송출 · 채팅 감정/슈퍼챗 리액션 · 안티 트롤링 · P-Skip 안정 보간)
        services.AddSingleton(sp => new PromptFlowStudio.Services.Microclimate.AtmosphericWeatherStudioRunner(sp)); // v17.28.0 AI 대기 기상 & 미기후 스튜디오(3D 나비에-스톡스 풍장 · 구름/강수 · 헤어/의상 항력 · 젖음/적설 · 헬름홀츠-호지 마하 가드)
        services.AddSingleton(sp => new PromptFlowStudio.Services.WorldExpansion.GenerativeWorldStudioRunner(sp)); // v17.27.0 AI 생성형 월드 확장 & 4D 인페인팅 스튜디오(해시 그리드 MLP 월드 확장 · 심도 유도 인페인팅 · 옥트리 LoD 고정 풀 안티 팝핑)
        services.AddSingleton(sp => new PromptFlowStudio.Services.ContinuumPhysics.ContinuumPhysicsStudioRunner(sp)); // v17.26.0 AI 실시간 뉴럴 물리 & 연속체 역학 스튜디오(APIC MPM 점탄소성 · 보로노이 파괴/소성 · SDF 비침투 해밀토니안 가드)
        services.AddSingleton(sp => new PromptFlowStudio.Services.HolographicTelepresence.NeuralTelepresenceStudioRunner(sp)); // v17.25.0 AI 홀로그래픽 텔레프레전스 스튜디오(옥트리 LoD 가우시안 체적 스트리밍 · 아이컨택/AEC/햅틱 크로스모달 · GGPO 결정적 롤백)
        services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialHaptics.SpatialHapticsStudioRunner(sp)); // v17.24.0 AI 6DoF 공간 햅틱 & 역각 피드백 스튜디오(1 kHz 갓-오브젝트 켈빈-보이트 · 진동 촉각 텍스처/맥동 합성 · 듀얼 레이트 POPC 버징 가드)
        services.AddSingleton(sp => new PromptFlowStudio.Services.CognitiveCinematography.CognitiveCinematographyStudioRunner(sp)); // v17.21.0 AI 시네마토그래피 & 카메라 디렉터 스튜디오(인지 프레이밍 · 레이 마칭 갓레이 · 육각 보케 · 포커스 풀링/가림 가드)
        services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralCodec.NeuralVolumetricVideoCodecEngine()); // v17.02.0 볼류메트릭 코덱(움직임 보상 · SIMD 잔차 양자화 · 3DGS 스플랫 시공간 잔차 · 학습 R-Q 레이트 제어)
        services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralCodec.GlobalEdgeCdnRoutingSynthesizer()); // v17.02.0 오리진/에지/P2P 워터필링 라우팅 · RTT/부하 비용 SIMD · ABR 래더
        services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralCodec.PacketLossConcealmentFecGuard()); // v17.02.0 리드-솔로몬 FEC(SSSE3 GF) · 인터리빙 · 지터 버퍼 · 움직임 외삽 은닉 · PLI
        services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralCodec.NeuralCodecEdgeCdnStudioRunner(sp)); // v17.02.0 신경망 코덱 & 글로벌 에지 CDN 스튜디오
        services.AddSingleton<PromptFlowStudio.Services.Flow.VisualNodeGraph>();                  // v16.21.0 타입 지정 비주얼 노드 그래프(에디터·동기화 공유)
        services.AddSingleton<PromptFlowStudio.Services.Integration.LiveSessionSyncBridge>();     // v16.21.0 데스크톱 코어 → 모바일/웹 WebSocket 라이브 브로드캐스트
        services.AddSingleton<PromptFlowStudio.Services.Integration.RemoteGpuOffloadRelay>();     // v16.21.0 클라우드 GPU 그래프 오프로딩 릴레이(PFS_GPU_WORKER_*)
        services.AddSingleton<PromptFlowStudio.Services.Flow.IRemoteGraphRunner>(sp => sp.GetRequiredService<PromptFlowStudio.Services.Integration.RemoteGpuOffloadRelay>());
        services.AddSingleton<PromptFlowStudio.Services.Flow.NodeGraphExecutionPipeline>();       // v16.21.0 레벨 병렬 노드 실행(원격 실패 시 로컬 폴백)
        services.AddSingleton<CanvasMaskingService>();                                             // v16.21.0 브러시 마스크·박스 추적·키프레임 캔버스
        services.AddSingleton<IStoryboardScriptWizardService, StoryboardScriptWizardService>(); // 원클릭 대본→컷 오토파서(BCL 정규식)
        services.AddSingleton<BabyThemePresetService>();       // 아기 숏츠 원클릭 테마 프리셋(10종 완성형 6컷 · authored 키네틱/폴리/텐션/전환 · 오프라인)
        services.AddSingleton<CustomThemeRegistryService>();    // v16.8.0 커스텀 테마 저작·.pftheme 영구 저장(%LocalAppData%\PromptFlowStudio\Themes) + 백과사전 동적 병합
        services.AddSingleton<SmartThemeMatcherService>();      // v16.7.0 앵커 사진 ↔ 테마 스마트 매처(32×32 색감/구도 인덱서 · 상위 3 추천)
        services.AddSingleton<BabyThemeDatabase>();             // 아기 테마 백과사전(10대 카테고리 × 소분류 50+ 테마 · compact 6-비트 계층형 인메모리)
        services.AddSingleton<CorporateThemeDatabase>();        // v2.3.0 B2B 기업/장비 홍보 테마 백과사전(브랜딩/중장비/스마트팩토리/제품 · 동일 스키마)
        services.AddSingleton<AutomotiveEquipmentDatabase>();  // v2.5.0 자동차 전문 장비 백과사전(시트 검사·완성차/캘리브레이션·전동화/다이나모/챔버 3대 라인업 33종)
        services.AddSingleton<VideoFormatFactory>();            // 롱/숏 포맷 팩토리(숏츠 6컷 루프 vs 롱폼 16컷 서사 · 비트→완성형 스토리보드 확장 · 아기+B2B+자동차 테마 조회)
        services.AddSingleton<CorporateBuildingThemeEngine>();  // v2.4.0 사옥 이미지 기반 AI 시네마틱 브랜드 홍보 엔진(드론360·홀로그램·야경 반사 6컷)
        services.AddSingleton<MultiLightingBuildingEngine>();   // v2.5.0 사옥 3색 조명 멀티배치(주간/노을/야경 병렬 그레이딩)
        services.AddSingleton<PromptFlowStudio.Services.Media.TelemetryHudOverlayEngine>();  // v2.5.0 검사/시험 CSV → 계기 HUD 번인(게이지/오실로스코프/다이나모)
        services.AddSingleton<PromptFlowStudio.Services.Cad.CadWireframeOverlayEngine>();    // v2.6.0 CAD/STL 3D 홀로그램 와이어프레임 렌더러(순수 BCL PNG + colorkey/글로우)
        services.AddSingleton<PromptFlowStudio.Services.Media.GlobalMultiDubbingEngine>();   // v2.6.0 글로벌 다국어 더빙/동적 자막 국가별 병렬 내보내기(en/de/zh/ja)
        services.AddSingleton<PromptFlowStudio.Services.Wan.AiQualityGuardService>();        // v2.6.0 AI 비주얼 퀄리티 감수(SSIM/라플라시안 비트맵) + QuotaRateGuard 연동 재발주
        services.AddSingleton<PromptFlowStudio.Services.Trade.BuyerEmblemEngraveService>();  // v2.6.0 바이어 엠블럼 음각 각인
        services.AddSingleton<PromptFlowStudio.Services.Trade.SpecSheetPdfExporter>();       // v2.6.0 B2B 제안서용 순수 BCL PDF 스펙 시트
        services.AddSingleton<PromptFlowStudio.Services.Studio.StudioBenchmarkEngine>();     // v2.7.0 파이프라인 벤치마킹 & 하드웨어 자동 교정
        services.AddSingleton<PromptFlowStudio.Services.Studio.DeliveryManifestService>();   // v2.7.0 납품 종합 명세서(SHA256 · HTML/MD)
        services.AddSingleton<PromptFlowStudio.Services.Studio.StudioProjectPackager>();     // v2.7.0 방송 마스터 ZIP 백업 패키저(Pure BCL ZipArchive)
        services.AddSingleton<PromptFlowStudio.Services.Studio.LicenseQuotaGuard>();         // v2.7.0 상용 라이선스/오프라인 쿼터 가드(RSA/SHA256)
        services.AddSingleton<PromptFlowStudio.Services.Studio.StudioFolderLinkService>();   // v2.7.0 결과 폴더 딥링크(File Explorer)
        services.AddSingleton<PromptFlowStudio.Services.Broadcast.LiveBroadcastStreamEngine>();      // v2.8.0 NDI/RTMP/SRT 라이브 방송 송출
        services.AddSingleton<PromptFlowStudio.Services.Broadcast.MultiGpuClusterEngine>();          // v2.8.0 멀티 GPU(Dual NVENC) 분산 렌더링 클러스터
        services.AddSingleton<PromptFlowStudio.Services.Broadcast.MotionTrackingSubtitleService>();  // v2.8.0 오프라인 픽셀 모션 트래킹 추종 자막(블록 매칭)
        services.AddSingleton<PromptFlowStudio.Services.Broadcast.SwitchingCountdownService>();      // v2.8.0 방송 스위칭 카운트다운
        services.AddSingleton<PromptFlowStudio.Services.Broadcast.OnAirBadgeService>();              // v2.8.0 4K ON AIR 글래스모피즘 뱃지
        services.AddSingleton<PromptFlowStudio.Services.Voice.VoiceAiDirectorEngine>();              // v2.9.0 실시간 음성 명령 AI 감독 믹서(오프라인 NLU)
        services.AddSingleton<PromptFlowStudio.Services.Voice.VoiceEditHistoryLabeler>();            // v2.9.0 음성 편집 이력 자동 라벨러
        services.AddSingleton<PromptFlowStudio.Services.Cloud.MultiTenantCloudSyncService>();        // v2.9.0 엔터프라이즈 멀티 테넌트 중앙 클라우드 동기화
        services.AddSingleton<PromptFlowStudio.Services.Director.AiCoDirectorAgent>();               // v3.0.0 멀티모달 AI 자율 가이드 연출 감독(오프라인 휴리스틱)
        services.AddSingleton<PromptFlowStudio.Services.Media.VirtualStudioCompositor>();            // v3.0.0 3D 버추얼 스튜디오 크로마키/배경 합성
        services.AddSingleton<ViralityScorePredictor>();                                            // v3.0.0 바이럴 숏츠 첫 3초 훅 점수 예측
        services.AddSingleton<PromptFlowStudio.Services.Security.EnterpriseSecurityAudit>();         // v3.0.0 AES-256 해시체인 변조불가 감사 로그
        services.AddSingleton<ThemeSearchFilterService>();                                          // v3.1.0 테마 실시간 키워드 검색 필터(LINQ)
        services.AddSingleton<ThemePinService>();                                                   // v3.1.0 테마 즐겨찾기(Pin) 영속
        services.AddSingleton<PromptFlowStudio.Services.Ui.StudioMemoryAudit>();                     // v3.1.0 덱 구독/핸들 누수 감사 레지스트리
        services.AddSingleton<SmartThemeRecommender>();                                             // v3.2.0 문맥 기반 AI 프리셋 추천(TF-IDF 코사인)
        services.AddSingleton<PromptFlowStudio.Services.Trade.PitchScriptSyncService>();             // v3.2.0 피치 옵션 ↔ project.json 양방향 동기화
        services.AddSingleton<ScriptPolishEnhancer>();                                              // v3.3.0 AI 대본 오프라인 폴리싱 & 서사 컷 확장
        services.AddSingleton<PromptFlowStudio.Services.Trade.ClientBrandingPresetService>();        // v3.3.0 멀티 클라이언트 다이내믹 브랜딩 스위처
        services.AddSingleton<PromptFlowStudio.Services.Media.PreExportQaSentinel>();                // v3.3.0 배포 전 오디오/블랙 프레임 QA 센티널
        services.AddSingleton<PromptFlowStudio.Services.Media.ThumbnailCaptureService>();            // v3.3.0 플랫폼 썸네일 자동 포착
        services.AddSingleton<PromptFlowStudio.Services.Diagnostics.StudioSelfDiagnosticEngine>();   // v3.4.0 자율 진단 & 세션 자가 치유(FFmpeg hang 정독·GC/VRAM)
        services.AddSingleton<PromptFlowStudio.Services.Diagnostics.EnterpriseAssetIndexer>();       // v3.4.0 중앙 자산 역색인(SHA256·태그 초고속 검색)
        services.AddSingleton<PromptFlowStudio.Services.Diagnostics.AutonomousRecoverySentinel>();   // v3.4.0 워커 와치독 & OOM 페일오버
        services.AddSingleton<PromptFlowStudio.Services.Marketing.MarketingAutomationEngine>();      // v3.5.0 웹훅 기반 SNS 자율 발행 파이프라인
        services.AddSingleton<PromptFlowStudio.Services.Marketing.AbTestingAnalyticsEngine>();       // v3.5.0 글로벌 수주 A/B 테스팅 & 전환 예측
        services.AddSingleton<PromptFlowStudio.Services.Media.MultiThumbnailAbMixer>();              // v3.5.0 지능형 멀티 썸네일 A/B 믹서
        services.AddSingleton<PromptFlowStudio.Services.Media.BabyKeepsakeOverlayEngine>();          // v3.7.0 24K 골드 아우라 + 이름/D+Day 각인 오버레이(순수 BCL PNG + drawtext)
        services.AddSingleton<PromptFlowStudio.Services.Media.DigitalKeepsakeCardService>();         // v3.7.0 4K 프리미엄 디지털 성장 기념 카드(순수 BCL PNG/PDF · 내장 5×7 폰트)
        services.AddSingleton<PromptFlowStudio.Services.Wan.AlibabaWanHybridEngine>();               // v3.8.0 완 로컬(0원)/클라우드 하이브리드 + Wan 3.0급 무료 화질 보완(minterpolate/cas/eq)
        services.AddSingleton<PromptFlowStudio.Services.Voice.EmotionalVoiceSynthesizer>();          // v3.8.0 오프라인 감정 보이스(페르소나×5개국 · asetrate/atempo 피치·속도 변조)
        services.AddSingleton<PromptFlowStudio.Services.Media.SpatialAudioEngine>();                 // v3.8.0 3D 바이노럴 카메라 연동 입체 음향(pan 등파워 + 원근 볼륨)
        services.AddSingleton<PromptFlowStudio.Services.Wan.WanPipelineHardening>();                 // v4.4.3 Task2 하이브리드 폴백 하드닝(nvidia-smi TimeSpan 타임아웃→0MB · 5xx/단선 지수백오프→로컬 폴백 · Polly 미사용 순수 BCL)
        services.AddSingleton<PromptFlowStudio.Services.Media.AudioEngineHardening>();               // v4.4.3 Task3 3D 음향 고립 차단(pan/sofalizer Exit1 흡수→원음 패스스루 · TTS WAV/MP3 잠금/0바이트 무한대기 가드)
        services.AddSingleton<PromptFlowStudio.Services.Weather.WeatherEngine>();                    // v4.5.0 날씨 프롬프트+입체 앰비언스 합성(anoisesrc 빗/바람 → amix · SpatialAudioEngine pan 연동)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.IMobilePlatformBridge, PromptFlowStudio.Services.Mobile.DesktopPlatformBridge>();  // v4.0.0 모바일 네이티브 브릿지 시임(데스크톱=ffmpeg.exe · 모바일 프로젝트가 Android 구현으로 대체)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.IArFieldVisionBridge, PromptFlowStudio.Services.Mobile.DesktopArFieldVision>();   // v5.0 Task1 AR 카메라 패스스루 시임(데스크톱 시뮬 · MAUI 헤드=CameraX/AVFoundation 대체)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.INfcSyncCommander, PromptFlowStudio.Services.Mobile.DesktopNfcSyncCommander>();    // v5.0 Task2 NFC 논스톱 체인 시임(데스크톱 모의 · MAUI 헤드=Android.Nfc/CoreNFC 대체)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.IMotionTiltController, PromptFlowStudio.Services.Mobile.DesktopMotionTiltController>(); // v5.0 Task3 자이로 틸트 시임(데스크톱 no-op · MAUI 헤드=Maui.Sensors 대체)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.ISpatialVoicePrompter, PromptFlowStudio.Services.Mobile.DesktopSpatialVoicePrompter>(); // v5.4 Task3 공간 음성-비전 앵커링(데스크톱=데모 문장 구조화 · MAUI 헤드=Android.Speech/iOS Speech 대체)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.IMobileOfflineStore, PromptFlowStudio.Services.Mobile.MobileOfflineStore>();          // v5.2 Task2 오프라인-퍼스트 큐(AES-GCM 암호화 JSON · 원자적 동기화 · 순수 BCL)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.IMobileVisionAiEngine, PromptFlowStudio.Services.Mobile.MobileVisionAiEngine>();      // v5.2 Task3 온디바이스 에지 비전(모션/색차 이상감지 · 순수 BCL)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.IMobileBackgroundWorker, PromptFlowStudio.Services.Mobile.DesktopMobileBackgroundWorker>(); // v5.2 Task4 백그라운드 스캔(데스크톱=인프로세스 · MAUI=FGS/BGTask)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.OfflineSyncPipeline>();                       // v5.2.1 오프라인 큐→원격 업로더 결선(ctor 에서 store 바인딩)
        services.AddSingleton<PromptFlowStudio.Services.Mobile.TelemetryPipelineWiring>();                   // v5.2.1 비전 이상→오프라인 큐 적재 + 백엔드 동기화 바인딩
        services.AddSingleton<PromptFlowStudio.Services.Mobile.RemoteRenderClient>();                // v4.0.0 리모트 렌더 오프로딩(HTTP/WebSocket · AES-256 암호화 전송)
        services.AddSingleton<PromptFlowStudio.Services.Wan.WanLocalStubEngine>();                    // v5.6 백엔드 미구성 시 로컬 스텁 모의 렌더(오프라인 DAG/HUD 테스트)
        services.AddSingleton<PromptFlowStudio.Services.Wan.WanRenderClient>();                       // v5.4.1 Wan 3.0 렌더 직결(모션+음성 컴파일→JSON POST · 실패 시 오프라인 큐 무손실 · v5.6 스텁 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Wan.NodeWorkerService>();                     // v6.0.1 단일 GPU 워커 전송 계층(하트비트 프로브 · 세그먼트 렌더/폴링 · 노드 장애→NodeFault 승격 · 순수 BCL)
        services.AddSingleton<PromptFlowStudio.Services.Wan.DistributedRenderGrid>();                 // v6.0.1 원격 GPU 워커 분산 렌더 그리드(최소부하 워크풀 배정 + BCL 페일오버 재배정 + 하트비트 자가치유 · 관찰가능 · IDisposable)
        services.AddSingleton<PromptFlowStudio.Services.Integration.WebhookDispatcher>();             // v6.1.0 B2B Webhook 비동기 디스패처(Channel 큐 + HMAC-SHA256 서명 + 지수 백오프 재시도 · 관찰가능 · IDisposable)
        services.AddSingleton<PromptFlowStudio.Services.Integration.WebRtcStreamer>();                // v6.1.0 WebRTC 초저지연 라이브 프리뷰 스트리머(시그널링 래퍼 + 스틸 프레임 Graceful 폴백 · 관찰가능 · IDisposable)
        services.AddSingleton<PromptFlowStudio.Services.Integration.ApiGatewayEngine>();              // v6.2.0 B2B API 게이트웨이(X-API-Key HMAC 검증 + 테넌트 Token Bucket 레이트리밋 429 + 그리드 큐 할당 · 관찰가능)
        services.AddSingleton<PromptFlowStudio.Services.Integration.TelemetryAggregator>();           // v6.2.0 경량 무의존 텔레메트리 집계기(60초 링버퍼 RPS/지연/Webhook/GPU 슬롯 · 1초 롤링 · 관찰가능 · IDisposable)
        services.AddSingleton<PromptFlowStudio.Services.Integration.EmbeddedHttpServer>();            // v6.3.0 경량 REST Edge 서버(순수 BCL HttpListener · POST /api/v1/render/batch → ApiGatewayEngine 위임 · 완전 비동기 · 관찰가능 · IDisposable)
        services.AddSingleton<PromptFlowStudio.Services.Inference.LocalModelLoader>();                // v7.0.0 로컬 추론 백엔드/모델 프로브(WebGPU/DirectML 가용성 · 미결선 시 BCL)
        services.AddSingleton<PromptFlowStudio.Services.Inference.WebGpuInferenceEngine>();           // v7.0.0 Local-First 추론 엔진(프롬프트 부스팅/스틸컷/오디오 RMS · 가속 미가용 시 BCL Graceful 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Inference.HybridNodeMeshScheduler>();         // v7.0.0 로컬-클라우드 하이브리드 스케줄러(경량=로컬 · 고해상=DistributedRenderGrid · 노드 없으면 로컬 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Timeline.MultiTrackTimelineState>();          // v7.0.0 멀티트랙 NLE 타임라인 상태(비디오/보컬/자이로/프롬프트 · 스냅 · 관찰가능)
        services.AddSingleton<PromptFlowStudio.Services.Media.AudioWaveSynthesizer>();                // v7.2.0 순수 BCL 오디오 파형 합성기(RIFF WAV 44.1kHz · ONNX TTS 결선점 · Graceful 폴백)
        services.AddSingleton<PromptFlowStudio.Services.Timeline.AiAutoDirectorEngine>();             // v7.2.0 AI 멀티모달 오토 디렉터(대본→4트랙 자동 배치 · 감정 휴리스틱 · 순수 BCL)
        services.AddSingleton<PromptFlowStudio.Services.Timeline.TimelineRenderCompiler>();           // v7.3.0 타임라인 4트랙 시간순 병합 → WanRenderRequest[] 컴파일(순수 BCL)
        services.AddSingleton<PromptFlowStudio.Services.Timeline.FcpXmlExporter>();                   // v7.3.0 FCPXML 1.9 / OTIO JSON 내보내기(System.Xml.Linq · 외부 라이브러리 0)
        services.AddSingleton<PromptFlowStudio.Services.Agents.AgentSwarmOrchestrator>();             // v8.0.0 자율형 에이전트 스웜(Director/Audio/Editor/QC · Channel 버스 · 타임라인 자율 교정 · IDisposable)
        services.AddSingleton<PromptFlowStudio.Services.Media.Spatial3DVideoEngine>();                // v8.0.0 공간 3D 비디오 & Binaural 엔진(깊이맵 벡터 → SBS 스테레오 + 자이로 방위 · 순수 BCL)
        services.AddSingleton<PromptFlowStudio.Services.Integration.C2PaWatermarkEngine>();           // v8.0.0 C2PA 위변조 방지 워터마크(SHA-256 하드바인딩 + HMAC 서명 + JUMBF · 검증 실패 보안 경고)
        services.AddSingleton<PromptFlowStudio.Services.Integration.SystemStressAuditor>();           // v8.1.0 경량 인메모리 스트레스 오디터(게이트웨이 429/C2PA 무결/스웜 수렴 · 격리 인스턴스 · GC/지연 지표)
        services.AddSingleton<PromptFlowStudio.Services.Wan.CloudEdgeAutoScaler>();                   // v8.2.0 클라우드 엣지 오토스케일러(그리드 백로그>가용슬롯 시 동적 프로비저닝 · 유휴 Scale-in · API 실패 시 BCL 스텁 폴백 · 관찰가능 · IDisposable)
        services.AddSingleton<PromptFlowStudio.Services.Integration.LiveCollaborationHub>();          // v8.2.0 실시간 멀티유저 협업 허브(Pure BCL WebSocket · MultiTrackTimelineState 델타 CRDT LWW 동기화 · 피어 메시 · 관찰가능 · IDisposable)
        services.AddSingleton<PromptFlowStudio.Services.Deploy.DeploymentPackager>();                 // v8.2.0 엔터프라이즈 K8s/Docker 자동 패키저(Dockerfile · Deployment/Service/HPA YAML · 순수 BCL 문자열 조립 · 관찰가능)
        services.AddSingleton<PromptFlowStudio.Services.Integration.TenantPolicyEngine>(sp =>
            new(sp.GetRequiredService<PromptFlowStudio.Services.Integration.C2PaWatermarkEngine>()));            // v8.3.1 RBAC·데이터주권·일일쿼터 거버넌스(위반→403/429 방송 + C2PA 위반 봉인 · 관찰가능)
        services.AddSingleton<PromptFlowStudio.Services.Agents.AiSelfDiagnosticEngine>(sp =>
            new(sp.GetRequiredService<PromptFlowStudio.Services.Integration.TelemetryAggregator>(),
                sp.GetService<PromptFlowStudio.Services.Agents.AgentSwarmOrchestrator>()));                     // v8.3.2 텔레메트리 EWMA/슬롯 이상 사전감지 → HealDirective 발화(Channel) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Agents.AiFleetOrchestrator>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Agents.AiSelfDiagnosticEngine>()));                     // v8.4.1 HealDirective 구독 → 동적 스폰/격리/드레이닝 + 최소부하 로드밸런싱 · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.AirGappedDisasterRecoveryEngine>();         // v8.4.2 세션/타임라인 SHA-256 무결성 스냅샷 & 원클릭 복구(폐쇄망 · 순수 BCL · 관찰가능)
        services.AddSingleton<PromptFlowStudio.Services.Integration.ZeroTrustAuditExporter>(sp =>
            new(sp.GetRequiredService<PromptFlowStudio.Services.Integration.TenantPolicyEngine>(),
                sp.GetRequiredService<PromptFlowStudio.Services.Integration.C2PaWatermarkEngine>()));           // v8.4.3 정책 위반/C2PA 변조 → 해시체인 WORM 감사 + C2PA 서명 보고서 · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Agents.AutonomousPromptOptimizer>();                    // v8.5.1 스웜/타임라인 피드백 → 멱등 토큰 압축 & 품질/압축률 EWMA 자율 튜닝 · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Inference.MultiModalNeuralCache>();                     // v8.5.2 MMF 링 버퍼 + ArrayPool Zero-Copy 추론 텐서/임베딩 캐시 · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.EnterpriseSsoEngine>(sp =>
            new(sp.GetRequiredService<PromptFlowStudio.Services.Integration.TenantPolicyEngine>()));            // v8.5.3 OIDC(JWS HS256/RS256)/SAML BCL 검증 → TenantPolicyEngine RBAC 바인딩 · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.DistributedConsensusEngine>();              // v8.6.1 순수 BCL Raft 합의(리더 선출·로그 복제·하트비트 · 과반 커밋) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Agents.PredictiveTelemetryEngine>(sp =>
            new(sp.GetRequiredService<PromptFlowStudio.Services.Integration.TelemetryAggregator>(),
                sp.GetService<PromptFlowStudio.Services.Agents.AiFleetOrchestrator>()));                       // v8.6.2 링버퍼 선형회귀 장애 예측 → 플릿 선제 스케일아웃 경고 · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Inference.NativePluginSandbox>();                       // v8.6.3 AssemblyLoadContext 격리 + 시간/메모리 임계 강제 플러그인 샌드박스 · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.MultiRegionFailoverEngine>();               // v8.7.1 멀티 리전 Active-Active 페일오버(BCL HttpClient/Socket 헬스 프로브 · 쿼럼 미달 시 Standby 자동 승격) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Inference.QuantumResistantLedgerSigner>();              // v8.7.2 PQC(ML-DSA 스텁) WORM 원장 서명기(SHA-256 하드바인딩 + HMAC-SHA512 서명 스텁 + 해시체인) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.ZeroTrustMeshProxy>();                      // v8.7.3 제로 트러스트 메쉬 프록시(HMAC SPIFFE 토큰 검증 + 서킷 브레이커 Closed/Open/HalfOpen) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Agents.EdgeSwarmFederation>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Agents.AgentSwarmOrchestrator>()));                     // v8.8.1 엣지 스웜 연합(HMAC 서명 P2P 델타 중계 · 지문 dedupe 멱등 · 6라운드 합의 수렴 · 하트비트 재편입) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.AutonomousMemoryCompactor>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Inference.MultiModalNeuralCache>()));                   // v8.8.2 자율 메모리 압축기(LOH/POH 파편화 표집 · CompactOnce 압축 수집 · ArrayPool 상한/뉴럴캐시 자율 튜닝) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.MauiMobileBridgeAdapter>();                 // v8.8.3 MAUI 모바일 브리지(플랫폼 식별만 #if 격리 · 햅틱/공유/배터리/화면유지 Func 훅 + 스텁 폴백 보류 큐) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.GraphifyQueryEngine>();                    // v8.9.1 Graphify 지식 그래프 쿼리 엔진(graph.json 1회 파싱 → CSR 인접 인덱스 · God Node 의존/피의존 역추적 · 402 커뮤니티 경계 응집도) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.McpTestAutomationBridge>();                // v8.9.2 MCP 자동화 검증 브리지(Playwright/Chrome DevTools JSON-RPC 2.0 · ClientWebSocket/HttpClient · Blazor E2E 무인 시나리오 + 0.9초 스텁 폴백) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.ZeroAllocPipelineSealer>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Integration.AutonomousMemoryCompactor>()));            // v8.9.3 Zero-Alloc 파이프라인 봉인기(전용 스레드 GetAllocatedBytesForCurrentThread 델타 · LOH/POH 세대 증분 · SHA-256 봉인 인증서) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.EnterpriseMasterOrchestrator>(sp =>
            new(sp));                                                                                        // v9.0.1 엔터프라이즈 마스터 오케스트레이터(v8 전 모듈 계층 등록 → Core/Distributed/Security/Intelligence/Edge 순차 시동 · 5초 재프로브 롤업) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.ProductionSecuritySealer>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Integration.C2PaWatermarkEngine>(),
                sp.GetService<PromptFlowStudio.Services.Inference.QuantumResistantLedgerSigner>(),
                sp.GetService<PromptFlowStudio.Services.Integration.EnterpriseSsoEngine>(),
                sp.GetService<PromptFlowStudio.Services.Integration.ZeroTrustMeshProxy>()));                  // v9.0.2 프로덕션 보안 최종 봉인기(C2PA 출처 · WORM 원장 · SAML/OIDC · ZTNA 4주 단일 헬스체크 → SHA-256 지문 + HMAC 서명 인증서) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.MasterReleaseManifest>();                 // v9.0.3 마스터 릴리스 매니페스트(배포 산출물 스트리밍 SHA-256 지문 → 루트 해시 봉인 · Air-Gapped 자립형 오프라인 라이선스 토큰 · JSON 반출) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.MauiMobileGestureEngine>();              // v9.1.1 MAUI 제스처 엔진(포인터 전용 드래그/핀치/스와이프 인식 · 핀치→타임라인 트랙 줌 연동 · 좌우 스와이프→MobileInternalView 상태 머신 전환) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Agents.MobileLowPowerOptimizer>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Integration.MauiMobileBridgeAdapter>()));            // v9.1.2 모바일 저전력 거버너(배터리 잔량/충전/화면 상태 → UI 렌더 FPS + 스웜 추론 스레드 권고 · 브리지 미결선은 Balanced 폴백) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Inference.MobileOfflineStorageEngine>();             // v9.1.3 모바일 오프라인 스토리지(시네마틱 프롬프트 + 타임라인 델타 인메모리 보관 → SHA-256 꼬리 지문 바이너리 원자적 영속화 · 5초 자동 플러시) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Inference.OnDeviceVisionInferenceEngine>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Inference.LocalModelLoader>()));                    // v9.2.1 온디바이스 비전 추론(DirectML/CoreML/NNAPI/WebGPU 후보 프로브 · MemoryMappedFile 뷰 + ArrayPool/Span 무할당 타일링 · 합성 텐서 강등) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.CrossDeviceStateSyncPipeline>(_ => new());  // v9.2.2 크로스 디바이스 동기화(LAN TcpListener/TcpClient P2P · LWW-Register CRDT 델타 수렴 · HMAC 상수시간 검증 · 대체 전송/보류 큐 강등) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Agents.AutonomousCrashTelemetryExporter>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Inference.OnDeviceVisionInferenceEngine>(),
                sp.GetService<PromptFlowStudio.Services.Agents.MobileLowPowerOptimizer>(),
                sp.GetService<PromptFlowStudio.Services.Inference.QuantumResistantLedgerSigner>()));        // v9.2.3 자율 크래시/NPU 텔레메트리(고정 링버퍼 무할당 수집 · 스롯틀 자율 판정 · 집계 바인딩 정규화 → SHA-256 + HMAC + WORM 원장) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Media.SpatialGlassmorphismRenderer>();              // v9.3.1 공간 글래스모피즘 렌더러(원근 matrix3d · Schlick Fresnel · Snell 굴절 · Blinn-Phong 스펙큘러 · 깊이 프로스트) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.QuantumP2pMeshRouter>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Inference.QuantumResistantLedgerSigner>()));        // v9.3.2 양자 내성 P2P 메시(ML-DSA 홉 체인 서명 · Dijkstra 동적 토폴로지 · 자가치유 재라우팅 · 집계 바인딩 원장 봉인) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Agents.SelfHealingPipelineWatchdog>(sp =>
            new(sp.GetService<PromptFlowStudio.Services.Agents.AutonomousCrashTelemetryExporter>(),
                sp.GetService<PromptFlowStudio.Services.Agents.MobileLowPowerOptimizer>()));                // v9.3.3 자율 복구 워치독(핫패스 교착 CTS 교체 · 누수/파편화 압축 수집 · NPU 과부하 거버너 감속) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Agents.AutonomousWorkflowSynthesizer>();            // v9.4.1 자율 워크플로우 합성(한/영 의도 파싱 → 컷별 DAG · Identity Chaining · Kahn 수렴 순환 차단 · 집계 바인딩 지문) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Integration.ZeroLatencyEdgeBroadcaster>();          // v9.4.2 초저지연 엣지 브로드캐스터(Channel DropOldest → Socket NoDelay 팬아웃 · 50ms SLA/P95 · 루프백 프로브 · 인프로세스 강등) · 관찰가능 · IDisposable
        services.AddSingleton<PromptFlowStudio.Services.Integration.MultiTenantResourceGovernor>(_ => new());         // v9.4.3 멀티테넌트 거버너(NPU/GPU/RAM Hard-cap 격리 · 90% 워터마크 사전 차단 · 하위 등급 원자 선점 · Revoked 토큰) · 관찰가능
        services.AddSingleton<PromptFlowStudio.Services.Agents.AdaptiveNeuralDirector>();
        services.AddSingleton<PromptFlowStudio.Services.Integration.DistributedEdgeConsensusMesh>();
        services.AddSingleton<PromptFlowStudio.Services.Integration.ZeroTrustGovernanceAuditor>();
        services.AddSingleton<PromptFlowStudio.Services.Inference.AutonomousNeuralMemoryStreamer>(_ => new());
        services.AddSingleton<PromptFlowStudio.Services.Integration.QuantumIdentityFederation>();
        services.AddSingleton<PromptFlowStudio.Services.Agents.RealTimeAnomalySentinel>();
        // ── v10.5.0-F1: 보안 시크릿 격리 볼트(DPAPI/환경변수) — 아래 서명 키 필요 서비스들보다 먼저 등록(의존 순서 명확화용 · DI 자체는 순서 무관).
        services.AddSingleton<PromptFlowStudio.Services.Security.SecureSecretVault>();
        // ── v9.7.0 자율 스웜 가버넌스 · 양자 스토리지 봉인 · 자기 최적화 멀티모달 파이프라인 ──
        services.AddSingleton<PromptFlowStudio.Services.Agents.AutonomousSwarmGovernanceEngine>();
        services.AddSingleton<PromptFlowStudio.Services.Inference.QuantumStorageSealingEngine>();
        services.AddSingleton<PromptFlowStudio.Services.Integration.SelfOptimizingMultimodalPipeline>();
        // ── v9.8.0 뉴럴 타일링 업스케일러 · 기밀 컴퓨팅 증명기 · 적응형 멀티클라우드 페일오버 ──
        services.AddSingleton<PromptFlowStudio.Services.Media.AutonomousNeuralTilingUpscaler>();
        services.AddSingleton<PromptFlowStudio.Services.Integration.ConfidentialComputingAttestor>();
        services.AddSingleton<PromptFlowStudio.Services.Integration.AdaptiveMultiCloudEdgeFailover>();
        // ── v9.9.0 스웜 합의 엔진 · 양자 저항 상태 봉인기 · 자기 최적화 파이프라인 튜너 ──
        services.AddSingleton<PromptFlowStudio.Services.Agents.SwarmConsensusEngine>();
        services.AddSingleton<PromptFlowStudio.Services.Security.QuantumResistantStateSealer>();
        services.AddSingleton<PromptFlowStudio.Services.Pipeline.SelfOptimizingPipelineTuner>();
        // ── v10.0.0 Enterprise Core Release: 감사 텔레메트리 집계기 · 강화 정책 집행기 · 자율 헬스 오케스트레이터 ──
        services.AddSingleton<PromptFlowStudio.Services.Telemetry.EnterpriseAuditTelemetryAggregator>();
        services.AddSingleton<PromptFlowStudio.Services.Security.HardenedSecurityPolicyEnforcer>();
        services.AddSingleton<PromptFlowStudio.Services.Orchestration.AutonomousSystemHealthOrchestrator>();
        // ── v10.0.0-LTS: 핫픽스 패치 오케스트레이터 · 제로트러스트 프로덕션 텔레메트리 익스포터(생성자 DI 자동해석) ──
        services.AddSingleton<PromptFlowStudio.Services.Operations.LtsHotfixPatcher>();
        services.AddSingleton<PromptFlowStudio.Services.Telemetry.ProductionTelemetryExporter>();
        // ── v10.5.0-F4: OTLP 로그 리포트 시각화 프로바이더 — 순수 집계/CircularBuffer(하드웨어 비의존) → 데스크톱+모바일 공용.
        services.AddSingleton<PromptFlowStudio.Services.Telemetry.OtlpTelemetryVisualizerProvider>();
        // ── v10.3.0 프로덕션 아티팩트 패키징 & 릴리스 무결성 최종 검증(하드웨어 비의존 → 데스크톱+모바일 공용) ──
        services.AddSingleton<PromptFlowStudio.Services.Operations.ProductionArtifactPackager>();
        services.AddSingleton<PromptFlowStudio.Services.Security.ReleaseIntegrityValidator>();
        services.AddSingleton<PromptFlowStudio.Services.Operations.ProductionManifestExporter>();
        // ── v10.5.0-F5: 핫픽스 자동 서명기 — LtsHotfixPatcher/ReleaseIntegrityValidator 재사용(순수 파일 I/O) → 데스크톱+모바일 공용.
        services.AddSingleton<PromptFlowStudio.Services.Operations.HotfixAutoSignerEngine>();
        // ── v10.1.0 알리바바 Wan 2.1 저사양 가속(4~8GB VRAM) ── AdaptiveHardwareProfiler/WanPipelineOrchestrator 는
        //    WanPipelineHardening(Process 기반 nvidia-smi) 의존이라 데스크톱 전용(Mobile DI 미등록 · 동일 정책).
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.AlibabaWanLowSpecEngine>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.AdaptiveHardwareProfiler>();
        // ── v10.5.0-F3: 하드웨어 자율 캘리브레이터 — 순수 스칼라 산술(Process/하드웨어 API 비의존) → 데스크톱+모바일 공용.
        //    Benchmarker/Tuner 둘 다 이걸 참조하므로 반드시 그 앞에 등록.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.AutoTunerHardwareCalibrator>();
        // ── v10.2.0 성능 튜닝/벤치마킹: 3종 모두 하드웨어 비의존(가상 예산·순수 상태기계·파일 I/O) → 데스크톱+모바일 공용.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanPerformanceBenchmarker>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.LivePipelineAutoTuner>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.BenchmarkingReportGenerator>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanPipelineOrchestrator>();   // ctor 가 LivePipelineAutoTuner 도 요구(위에서 먼저 등록)
        // v10.7.1-LTS-OpsCanary: SecureSecretVault/OtlpTelemetryVisualizerProvider/LivePipelineAutoTuner 셋 다 위에서 먼저 등록됨.
        services.AddSingleton<PromptFlowStudio.Services.Operations.OperationsCanaryHealthChecker>();
        // ── v10.6.0-F2: 이종 하드웨어 스케줄러 — Registry 조회(#if WINDOWS)+MMF 폴백, 비-Windows 는 항상 빈 목록/단일
        //    폴백 모드로 안전 동작 → 데스크톱+모바일 공용. WanPostProcessingEngine/WanProgressivePreviewer 가 ctor 로
        //    요구하므로 반드시 그 앞에 등록.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.HeterogeneousTaskScheduler>();
        // ── v10.6.0-F3: 동적 모션 제어기/LoRA 핫스왑 매니저 — 순수 BCL(ConcurrentQueue/Dictionary/Task.Delay)이라
        //    하드웨어 비의존 → 데스크톱+모바일 공용. WanPostProcessingEngine ctor 가 요구하므로 그 앞에 등록.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanDynamicMotionController>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.LoRAHotSwapManager>();
        // v11.0.0: multimodal audio sync engine - pure BCL FFT, reuses WanDynamicMotionController/HeterogeneousTaskScheduler -> desktop+mobile shared.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.MultimodalAudioSyncEngine>();
        // ── v10.6.0: Wan 2.1 후처리/프리뷰 — 의존 전이(AutonomousNeuralTilingUpscaler/LivePipelineAutoTuner/MMF)가
        //    전부 하드웨어 비의존(DirectMlNativeAdapter 도 비-Windows 폴백 내장)이라 데스크톱+모바일 공용 등록.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanPostProcessingEngine>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanProgressivePreviewer>();
        // v15.1.0: Wan 품질·속도 거버너(지연 EWMA·백프레셔 드롭률 → 스텝/해상도 래더) - 순수 BCL Interlocked, 데스크톱+모바일 공용.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanQualitySpeedGovernor>();
        // v11.1.0: Win32 공유메모리 가상카메라 브로드캐스터(MemoryMappedFile+Mutex) - 하드웨어 비의존 순수 BCL.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.LiveStreamVirtualCamPipe>();
        // v11.2.0: P2P 탐지/오프로드 릴레이 - 순수 소켓(BCL), 생성자에선 소켓을 열지 않음(Start() 명시 호출 필요).
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.EdgeP2PTaskRelay>();
        // v11.2.0: OBS WebSocket 5.0 리액션 엔진 - 생성자는 소켓을 열지 않음(ConnectAsync 명시 호출 필요).
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.InteractiveReactionEngine>();
        // v12.2.0-Phase3: 스트리머 리액션 오버레이(MultimodalAudioSyncEngine 구독) + P2P 메시 부하분산 - 순수 BCL, 생성자는 타이머/소켓 미기동(Start 명시 호출).
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.StreamerReactionEngine>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.P2pLoadBalancer>();
        // v12.2.0-Phase4: EdgeP2PTaskRelay 실 Ping/Pong RTT·손실 → P2pLoadBalancer 결선(EdgeP2PTaskRelay 가 데스크톱 전용 등록이라 데스크톱만).
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.EdgeRelayProbeBinder>();
        // v13.0.0: 비전 컨텍스트 분석(가상카메라 1FPS 샘플 → BCL 휴리스틱 + 로컬 VLM) → 리액션/모션/프롬프트 변조.
        //   LiveStreamVirtualCamPipe 가 데스크톱 전용 등록이라 데스크톱만. 분석기는 자동 기동하지 않음(방송 화면 분석은 HUD 에서 명시 opt-in).
        services.AddSingleton<PromptFlowStudio.Services.AI.Vision.VisionContextAnalyzer>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Vision.VisionReactionBinder>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Vision.VisionModelProbeEngine>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Vision.VisionActionMapper>();   // v13.3.0 VLM 요약문 → 모션 스파이크/스타일 LoRA 스왑(3초 쿨다운)   // v13.2.0 Ollama 비전 모델 자율 프로빙 + 합성 프레임 벤치마크(HUD 버튼·분석 시작 시 자동 프로빙)
        // ── v10.5.0-F2: Wan 백엔드 커넥터 — ComfyUiWanConnector 는 순수 네트워킹(데스크톱+모바일 공용),
        // ── v10.5.0-F2: Wan 백엔드 커넥터 — ComfyUiWanConnector 는 순수 네트워킹(데스크톱+모바일 공용),
        //    DirectMlWanConnector/WanBackendFactory 는 AdaptiveHardwareProfiler 의존이 전이돼 데스크톱 전용.
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.ComfyUiWanConnector>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.DirectMlWanConnector>();
        services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanBackendFactory>();
        services.AddSingleton<PromptFlowStudio.Services.Flow.FlowExecutionPipeline>();                // v5.5 DAG 노드 실행기(프롬프트→모션→Wan→프리뷰 팩토리 + 기본 그래프 · WanRenderClient 직결)
        services.AddSingleton<PromptFlowStudio.Services.Ui.GalleryAutoSaveService>();                 // v5.6 플로우 생성 자산 → Project.Gallery + 미디어 폴더 자동 등록
        // ── v5.8.2: AI 15초 컷다운 — ffmpeg Process 실행(#if WINDOWS) 의존이라 데스크톱 전용(모바일 미등록).
        services.AddSingleton<PromptFlowStudio.Services.Wan.AiShortsCutdownEngine>();
        services.AddSingleton<MultiPlatformSafeZoneService>();  // 멀티 플랫폼 세이프존(숏츠/릴스/틱톡 UI 가림 회피 자막·뱃지 Y 보정)
        services.AddSingleton<PromptFlowStudio.Services.Platform.PlatformDeepLinkService>();  // 플랫폼 음원 검색 딥링크(기본 브라우저 구동)
        services.AddSingleton<PromptFlowStudio.Services.Wan.SmartFramingBoundingService>();   // 피사체 세이프존 자동 바운딩(프롬프트 프레이밍 보정)
        services.AddSingleton<LlmVisionCropService>();                          // Gemini Vision 무모델 얼굴 오토 크롭 4K 썸네일
        services.AddSingleton<PromptFlowStudio.Services.Ui.SwitchRoleGuideService>();   // 스위치 역할 실시간 가이드(ON/OFF 연출차·권장 시나리오)
        services.AddSingleton<PromptFlowStudio.Services.Ui.HelpContentService>();       // 메뉴별 컨텍스트 도움말 콘텐츠(오프라인 authored)
        services.AddSingleton<PromptFlowStudio.Services.Platform.ResourceMonitorService>();  // 렌더 리소스 감시(CPU/RAM BCL + GPU nvidia-smi)
        services.AddSingleton<PromptFlowStudio.Services.Ui.GranularHelpService>();       // 메뉴 전용 컨텍스트 도움말(9메뉴 × 사용법/팁/FAQ)
        services.AddSingleton<PromptFlowStudio.Services.Ui.SidebarGuideService>();       // 사이드바 호버 가이드(단축키/요약/역할/순서)
        services.AddSingleton<AssetMetadataService>();                                  // 갤러리 자산 메타 인스펙터(영상→원본 프로젝트)
        services.AddSingleton<ProjectHandoffService>();                                 // 갤러리→스토리보드 원클릭 복원 핸드오프
        services.AddSingleton<VisualSfxCaptionService>();       // 음소거 시청 대응 시각 효과음 뱃지(폴리 트리거 → 이모지 뱃지 · 오프라인)
        services.AddSingleton<BabySeoAgent>();                  // 육아 A/B SEO(고효율 검색어 주입 + 제목 3종 + metadata_seo.json · 오프라인)
        services.AddSingleton<AutoThumbnailService>();          // 표정 피크 자동 썸네일 크롭 + Real-ESRGAN 4K
        services.AddSingleton<OsmuMetadataPackager>();          // OSMU 멀티플랫폼(유튜브/인스타/틱톡) 메타데이터 재구성
        services.AddSingleton<PlatformSeoAgent>();              // v2.0.0 8대 플랫폼 특화 OSMU(네이버클립/당근/샤오홍슈/핀터레스트/치지직 확장)
        services.AddSingleton<KoreanPromptLocalizer>();         // v2.1.0 프롬프트 한국어 현지화(연출 태그·한글 요약 · 오프라인)
        services.AddSingleton<LumaFaceDetector>();              // v2.3.0 오프라인 피부톤(YCbCr)/휘도 가변 얼굴 탐지(순수 BCL 바이트 연산)
        services.AddSingleton<FaceCenterAutoCropService>();     // v2.2.0 오프라인 아기 얼굴 1:1 정중앙 오토 크롭(FFmpeg · v2.3.0 가변 얼굴 탐지 Y-Bias)
        services.AddSingleton<SfxAuditionService>();            // v2.3.0 노드 클릭 SFX 시연(폴리→이모지/라벨 · Web Audio 합성음)
        services.AddSingleton<CorporateWatermarkEmbossService>(); // v2.3.0 B2B 3D 로고 엠보싱 워터마크(FFmpeg convolution 필터 빌더)
        services.AddSingleton<ThemeInstantGeneratorService>();  // v2.2.0 테마 즉시 생성 사전 점검(레퍼런스 연동 · 원스톱)
        services.AddSingleton<ICharacterAnchorService, CharacterAnchorService>(); // 캐릭터/스타일 앵커(I2V 일관성 · 컷 참조 이미지 바인딩)
        services.AddSingleton<StoryboardRepository>();          // 스토리보드 프로젝트 JSON 저장/불러오기(%LocalAppData%/Projects)
        services.AddSingleton<RecentProjectService>();          // 최근 프로젝트 MRU(recents.json · 최대 10 · 재시작 보존)
        services.AddSingleton<StoryboardJobManager>();           // 스토리보드 배치 생성(순차/병렬 큐 · 컷별 진행 · 자동 병합 · 완료 이벤트)
        services.AddSingleton<QueueResourceGovernor>();          // v16.16.0 API 쿼터(429/503 백오프·키 로테이션) + VRAM/RAM 동적 동시성 거버너
        services.AddSingleton<IBatchJobRunner, StoryboardBatchJobRunner>(); // v16.16.0 제출→렌더→후처리→OSMU 파이프라인 러너
        services.AddSingleton<PromptFlowStudio.Services.Telemetry.ProductionCostTrackerService>();   // v16.18.0 AI 비용/토큰 원장 + 일간/월간 예산 한도 차단(영구 저장)
        services.AddSingleton<PromptFlowStudio.Services.Telemetry.SystemHardwareTelemetryMonitor>(); // v16.18.0 VRAM/RAM/CPU EWMA · 컷당 렌더 시간 · OOM 징후 → 거버너 동시성 축소
        services.AddSingleton<BatchProductionQueueService>();    // v16.16.0 대량 생산 큐(우선순위·일시정지/재개·개별 취소·재시도·ETA)
        services.AddSingleton<BatchProductionService>();         // 멀티 숏츠 대량 양산 배치 팩토리(CSV/TXT 큐 · 백그라운드 순차 · 커버/메타데이터 · Graceful Skip)
        services.AddSingleton<IVideoExportService>(new VideoExportService(this)); // 완성 영상 내보내기(SaveFileDialog · this 폼 스레드로 마샬링)
        services.AddSingleton<IClipboardService>(new ClipboardService(this));     // 클립보드 복사(WinForms Clipboard · STA 폼 스레드로 마샬링)
        services.AddSingleton<IFilePickerService>(new FilePickerService(this));    // 파일 열기 대화상자(OpenFileDialog · STA 폼 스레드로 마샬링 · 엔진 경로 찾기)
        services.AddSingleton<IYoutubeMetadataService, YoutubeMetadataService>(); // 유튜브 숏츠 메타데이터 자동 생성(제목/설명/해시태그 · BCL 정규식)
        services.AddSingleton<IAutoUpdateService, AutoUpdateService>();           // 자체 자동 업데이트(GitHub Releases · BCL only)
        services.AddSingleton<ErrorDialogService>();                              // 오류 인간화 + 전역 표출(StackTrace 숨김 · 해결 가이드)
        services.AddSingleton<IResourceCleanupService, ResourceCleanupService>(); // 임시 파일/캐시 파편 자동 정소(24h · 백그라운드)

        // ── 메일 발송 설정 · 실발송 ──
        services.AddSingleton<SmtpConfigService>();           // 동적 SMTP 설정(로컬 AES-256-GCM 암호화 파일 · 재시작 불필요)
        services.AddSingleton<ISmtpConfigProvider>(s => s.GetRequiredService<SmtpConfigService>());  // 발송 시점 유효설정 공급
        services.AddSingleton<SmtpEmailService>();            // 이메일 실발송(OTP/테스트/통보 · BCL SmtpClient)

        // ── 보안 배포 · 올인원 인스톨러 & 단일 EXE 패키징(발급자 PC 전용 · 관리자 설정 › 보안·배포 탭) ──
        services.AddSingleton<PromptFlowStudio.Services.Deploy.PromptFlowPackagerService>();
        services.AddSingleton<PromptFlowStudio.Services.Deploy.DeploymentRunnerHardening>();   // v4.4.4 Task1 배포 스크립트 실행 보안(셸 이스케이프·경로 이탈 차단·CancellationToken+TimeSpan 10분→Kill(true) 좀비 방어)
        services.AddSingleton<PromptFlowStudio.Services.Deploy.ArtifactIntegrityGuard>();       // v4.4.4 Task2 산출물 무결성(존재·0바이트·확장자 정량검증 + IOException 파일락 Graceful 안내)

        var sp = services.BuildServiceProvider();
        _services = sp;
        // 기동 시 — 관리자 시드 보장 + 설치 PC 텔레메트리 1회 수집(실패해도 앱 기동 무영향).
        try { sp.GetRequiredService<MemberRegistryService>(); } catch { /* 시드 실패 무시 */ }
        try { sp.GetRequiredService<PromptFlowStudio.Services.Mobile.OfflineSyncPipeline>(); } catch { /* v5.2.1 업로더 바인딩 실패 무시 */ }
        try { sp.GetRequiredService<PromptFlowStudio.Services.Mobile.TelemetryPipelineWiring>(); } catch { /* v5.2.1 비전→큐 결선 실패 무시 */ }
        try { sp.GetRequiredService<PromptFlowStudio.Services.Agents.AiFleetOrchestrator>(); } catch { /* v8.4.1 자가진단→플릿 이벤트 구독 결선 실패 무시(내부 AiSelfDiagnosticEngine 동반 인스턴스화 → Telemetry 구독 활성) */ }
        try { sp.GetRequiredService<PromptFlowStudio.Services.Integration.ZeroTrustAuditExporter>(); } catch { /* v8.4.3 위반/변조→감사 이벤트 구독 결선 실패 무시(TenantPolicyEngine/C2PA 동반 인스턴스화) */ }
        try { sp.GetRequiredService<PromptFlowStudio.Services.Agents.PredictiveTelemetryEngine>(); } catch { /* v8.6.2 텔레메트리→예측 구독 결선 실패 무시(내부 플릿 동반 인스턴스화) */ }
        try { sp.GetRequiredService<InstallTelemetryService>().CaptureAndPersist(); } catch { /* 텔레메트리 실패 무시 */ }
        try
        {
            // v11.1.0: WanPostProcessingEngine 보간 프레임 -> LiveStreamVirtualCamPipe 오프로딩(128x128 RGBA 고정 -
            //   WanPostProcessingEngine.FrameBytes(64KB) 스텁 해상도와 정확히 일치, 재사용 스크래치로 Zero-Alloc).
            var postProc = sp.GetRequiredService<PromptFlowStudio.Services.AI.Wan.WanPostProcessingEngine>();
            var virtualCam = sp.GetRequiredService<PromptFlowStudio.Services.AI.Wan.LiveStreamVirtualCamPipe>();
            var vcamScratch = new byte[128 * 128 * 4];
            // v15.1.0 WAN-OPT-3: 렌더러 스레드는 락-프리 스테이징에 게시만(Mutex 대기 0) → 전용 펌프 스레드가 60FPS 위상 보간 송출.
            var wanGovernor = sp.GetRequiredService<PromptFlowStudio.Services.AI.Wan.WanQualitySpeedGovernor>();
            virtualCam.AttachGovernor(wanGovernor);
            sp.GetRequiredService<PromptFlowStudio.Services.Wan.WanRenderClient>().Governor = wanGovernor;
            postProc.OnChange += () =>
            {
                var n = postProc.CopyLastInterpolatedFrameTo(vcamScratch);
                if (n > 0) virtualCam.StageFrame(vcamScratch.AsSpan(0, n), 128, 128, PromptFlowStudio.Services.AI.Wan.VirtualCamPixelFormat.Rgba32);
            };
            virtualCam.StartPump(60);
            // v12.2.0-Phase4: 리액션 오버레이를 송출 직전에 합성 - 파티클이 없으면 파이프가 복사 없이 통과(Passthrough).
            virtualCam.AttachOverlay(sp.GetRequiredService<PromptFlowStudio.Services.AI.Wan.StreamerReactionEngine>());
            // v13.0.0: 장면 매퍼를 즉시 해석(OnScene 구독) + Wan 전송 직전 프롬프트 변조 훅 결선(분석기 정지 중엔 변조 없음=원본 통과).
            var visionBinder = sp.GetRequiredService<PromptFlowStudio.Services.AI.Vision.VisionReactionBinder>();
            sp.GetRequiredService<PromptFlowStudio.Services.Wan.WanRenderClient>().PromptModulator = visionBinder.ApplyPromptModulation;
            // v13.3.0: VLM 확정 응답의 요약문을 액션 매퍼로(분석기 정지 중엔 호출 없음)
            sp.GetRequiredService<PromptFlowStudio.Services.AI.Vision.VisionContextAnalyzer>().AttachActionMapper(sp.GetRequiredService<PromptFlowStudio.Services.AI.Vision.VisionActionMapper>());
        }
        catch { /* v11.1.0 스트리밍 오프로딩 결선 실패 무시(가상카메라 미가용해도 후처리 파이프라인 자체엔 영향 없음) */ }

        // 로컬 미디어 루트를 미리 만들어 둔다(가상 호스트 매핑 대상 폴더).
        var media = sp.GetRequiredService<ILocalMediaService>();
        try { Directory.CreateDirectory(media.MediaRoot); } catch { /* 폴더 없어도 매핑 자체는 치명적이지 않음 */ }

        // 임시/캐시 파편 자동 정소(24h 이상) — 백그라운드, 실패해도 기동 무영향.
        try { _ = sp.GetRequiredService<IResourceCleanupService>().CleanupOnStartupAsync(); } catch { /* 정소 실패 무시 */ }

        var view = new BlazorWebView
        {
            Dock = DockStyle.Fill,
            HostPage = "wwwroot/index.html",
            Services = sp,
        };

        // ⚠ BlazorWebView 는 https 가상 오리진에서 실행돼 <video src="file:///C:/…"> 로컬 재생이 보안정책상 막힌다.
        //   해결: 미디어 폴더(%LocalAppData%/…/Media)를 가상 호스트에 매핑 → <video src="https://{VirtualHost}/{JobId}/video.mp4">
        //   로 디스크에서 직접 스트리밍(HTTP Range 로 탐색/시킹 지원 · RAM 0). CoreWebView2 는 초기화 이후 사용 가능.
        //   AccessKind.Allow: 앱 오리진(https://0.0.0.0)에서의 교차 오리진 접근 허용(미디어는 다른 가상 호스트).
        view.BlazorWebViewInitialized += (_, _) =>
        {
            try
            {
                view.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    media.VirtualHost, media.MediaRoot, CoreWebView2HostResourceAccessKind.Allow);
            }
            catch (Exception ex) { Program.Log("VirtualHostMapping", ex); }
        };

        view.RootComponents.Add<App>("#app");
        Controls.Add(view);
    }

    private readonly ServiceProvider? _services;

    /// <summary>OBS WebSocket 5.0 기본 엔드포인트(obs-websocket 기본 포트 4455, 루프백) - 환경변수 PFS_OBS_URL 로 재정의.</summary>
    internal const string DefaultObsUrl = "ws://127.0.0.1:4455";

    // v12.1.0: 네트워크 서비스 수명주기 결선 - 창 표시 직후 기동(UI 스레드 비차단), 닫힘 시 정리.
    //   ▸ P2P: Start() 는 동기 바인드라 즉시 반환 - 포트 점유 등은 로그만 남기고 기동 계속(HUD 토글로 재시도).
    //     PFS_P2P_AUTOSTART=0 이면 자동 기동 생략(0.0.0.0 바인드는 최초 1회 Windows 방화벽 확인창을 띄울 수 있음).
    //   ▸ OBS: 비밀번호는 PFS_OBS_PASSWORD 환경변수에서만 읽는다(하드코딩 금지). OBS 미실행이면 Failed 상태로 남고 HUD 재연결 스위치로 복구.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_services is null) return;

        if (!string.Equals(Environment.GetEnvironmentVariable("PFS_P2P_AUTOSTART"), "0", StringComparison.Ordinal))
        {
            try
            {
                _services.GetRequiredService<PromptFlowStudio.Services.AI.Wan.EdgeP2PTaskRelay>().Start();
                _services.GetRequiredService<PromptFlowStudio.Services.AI.Wan.EdgeRelayProbeBinder>().Start();   // v12.2.0-Phase4: 피어 RTT/손실 프로브 → 밸런서
            }
            catch (Exception ex) { Program.Log("EdgeP2P.AutoStart", ex); }
        }

        try
        {
            var reaction = _services.GetRequiredService<PromptFlowStudio.Services.AI.Wan.InteractiveReactionEngine>();
            var url = Environment.GetEnvironmentVariable("PFS_OBS_URL");
            if (!Uri.TryCreate(string.IsNullOrWhiteSpace(url) ? DefaultObsUrl : url, UriKind.Absolute, out var obsUri)) obsUri = new Uri(DefaultObsUrl);
            var pw = Environment.GetEnvironmentVariable("PFS_OBS_PASSWORD");
            _ = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));   // 핸드셰이크 한도 - 초과 시 Failed
                try { await reaction.ConnectAsync(obsUri, string.IsNullOrEmpty(pw) ? null : pw, cts.Token).ConfigureAwait(false); }
                catch { /* ConnectAsync 내부에서 이미 Failed 게시 + 로그 */ }
            });
        }
        catch (Exception ex) { Program.Log("InteractiveReaction.AutoConnect", ex); }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || _services is null) return;
        try { _services.GetRequiredService<PromptFlowStudio.Services.AI.Wan.EdgeRelayProbeBinder>().Stop(); } catch { /* 무시 */ }
        try { _services.GetRequiredService<PromptFlowStudio.Services.AI.Wan.EdgeP2PTaskRelay>().Stop(); } catch { /* 무시 */ }
        try
        {
            var reaction = _services.GetRequiredService<PromptFlowStudio.Services.AI.Wan.InteractiveReactionEngine>();
            Task.Run(reaction.DisconnectAsync).Wait(TimeSpan.FromSeconds(1.5));   // 정상 Close 프레임 시도(내부 1초 한도) - 이후 Dispose 가 잔여 정리
        }
        catch { /* 무시 */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _services?.Dispose(); } catch { /* 무시 */ }
        }
        base.Dispose(disposing);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 다크 타이틀바(Win10 2004+ / Win11) — 실패해도 무시.
        try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { /* 구버전 무시 */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
