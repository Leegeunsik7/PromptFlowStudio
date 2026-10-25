using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.DependencyInjection.Extensions;   // TryAddSingleton

using Microsoft.Extensions.Logging;

using Microsoft.Maui.ApplicationModel;    // Platform.CurrentActivity (v5.0 네이티브 브릿지 팩토리)

using Microsoft.Maui.Controls.Hosting;

using Microsoft.Maui.Hosting;

using PromptFlowStudio.Mobile.Services;

using PromptFlowStudio.Services.Mobile;   // IMobilePlatformBridge / RemoteRenderClient / v5.0 AR·NFC·IMU 시임 (기존 프로젝트 재사용)

using PromptFlowStudio.Shared;            // SharedServiceRegistry (공용 RCL)



namespace PromptFlowStudio.Mobile;



/// <summary>

/// [PromptFlow Studio v4.0.0] MAUI Blazor Hybrid 진입점 — 기존 순수 BCL 서비스 DI 를 그대로 재등록하고,

///   플랫폼 시임(<see cref="IMobilePlatformBridge"/>)만 <see cref="AndroidPlatformBridge"/>로 교체한다(로직 무변경 재사용).

/// </summary>

public static class MauiProgram

{

    public static MauiApp CreateMauiApp()

    {

        var builder = MauiApp.CreateBuilder();

        builder

            .UseMauiApp<App>()

            .ConfigureFonts(fonts => { fonts.AddFont("Pretendard-Regular.ttf", "Pretendard"); });



        builder.Services.AddMauiBlazorWebView();

#if DEBUG

        builder.Services.AddBlazorWebViewDeveloperTools();

        builder.Services.AddLogging(l => l.AddDebug());

#endif



        // ── 공용 RCL 시임 등록(폴백) → 이후 플랫폼 네이티브로 override(마지막 등록 우선) ──

        builder.Services.RegisterSharedServices();

        builder.Services.AddSingleton<RemoteRenderClient>();



#if ANDROID

        // 안드로이드 네이티브(VibrationEffect 햅틱 · 플랫폼 브릿지)

        builder.Services.AddSingleton<PromptFlowStudio.Shared.Haptics.IHapticEngine, AndroidHapticEngine>();

        builder.Services.AddSingleton<IMobilePlatformBridge, AndroidPlatformBridge>();

#elif IOS

        // 애플 iOS(UIFeedbackGenerator 햅틱 · 플랫폼 브릿지)

        builder.Services.AddSingleton<PromptFlowStudio.Shared.Haptics.IHapticEngine, IosHapticEngine>();

        builder.Services.AddSingleton<IMobilePlatformBridge, IosPlatformBridge>();

#endif



        // ── v5.0 모바일 초격차 하드웨어 브릿지 override(데스크톱 스텁 → 네이티브 · 마지막 등록 우선) ──

        //   AR/NFC/IMU 구현은 기존 프로젝트 Services/Mobile/*.cs 의 #if ANDROID / #if IOS 블록(소스 링크)에서 컴파일된다.

        //   ⚠ 팩토리 람다는 최초 resolve(=UI 마운트 시점)에 실행되므로 Activity/WebView 가 준비된 뒤 생성된다.

#if ANDROID

        builder.Services.AddSingleton<IMotionTiltController, MauiMotionTiltController>();                     // 자이로+가속도 60Hz(Maui.Devices.Sensors)

        builder.Services.AddSingleton<ISpatialVoicePrompter, MauiSpatialVoicePrompter>();                     // v5.4 공간 음성-비전 앵커링(Android.Speech.SpeechRecognizer ko-KR)

        builder.Services.AddSingleton<INfcSyncCommander>(_ =>                                                 // NfcAdapter.EnableReaderMode(포그라운드 NDEF)

            new AndroidNfcSyncCommander(Platform.CurrentActivity

                ?? throw new InvalidOperationException("NFC 초기화 실패 — 현재 Activity 를 찾을 수 없습니다.")));

        builder.Services.AddSingleton<IArFieldVisionBridge>(sp =>                                             // CameraX 패스스루 + WebView 투명(Light-AR)

            new AndroidArFieldVision(

                Android.App.Application.Context,

                (AndroidX.Lifecycle.ILifecycleOwner)Platform.CurrentActivity!,

                () => MauiWebViewLocator.AndroidWebView,

                sp.GetService<IMobileVisionAiEngine>()));   // v5.2.1: ImageAnalysis → 비전 엔진 결선

#elif IOS

        builder.Services.AddSingleton<IMotionTiltController, MauiMotionTiltController>();

        builder.Services.AddSingleton<ISpatialVoicePrompter, MauiSpatialVoicePrompter>();                     // v5.4 공간 음성-비전 앵커링(Speech.SFSpeechRecognizer ko-KR)

        builder.Services.AddSingleton<INfcSyncCommander, IosNfcSyncCommander>();                             // NFCNdefReaderSession

        builder.Services.AddSingleton<IArFieldVisionBridge>(_ =>                                             // AVFoundation 프리뷰 + WKWebView Opaque=false

            new IosArFieldVision(() => MauiWebViewLocator.IosWebView));

#endif



        // ── v5.2 모바일 고도화 — 오프라인 큐/에지비전은 공통(순수 BCL·시임 위임), 백그라운드는 네이티브 override ──

        builder.Services.AddSingleton<IMobileOfflineStore, MobileOfflineStore>();      // Connectivity 자동 구독(ctor · #if ANDROID||IOS)

        builder.Services.AddSingleton<IMobileVisionAiEngine, MobileVisionAiEngine>();

#if ANDROID

        builder.Services.AddSingleton<IMobileBackgroundWorker, AndroidMobileBackgroundWorker>();   // Foreground Service

#elif IOS

        builder.Services.AddSingleton<IMobileBackgroundWorker, IosMobileBackgroundWorker>();       // BGTaskScheduler

#endif



        // ── v5.2.1 통합 파이프라인 결선 ──

        builder.Services.AddSingleton<OfflineSyncPipeline>();          // 오프라인 큐 → 원격 업로더(ctor 에서 store 바인딩)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Wan.WanLocalStubEngine>();   // v5.6 로컬 스텁 모의 렌더

        builder.Services.AddSingleton<PromptFlowStudio.Services.Wan.WanRenderClient>();   // v5.4.1 Wan 3.0 렌더 직결(오프라인 큐 폴백 · v5.6 스텁 폴백)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Flow.FlowExecutionPipeline>();   // v5.5 DAG 노드 실행기

        builder.Services.AddSingleton<PromptFlowStudio.Services.Ui.GalleryAutoSaveService>();   // v5.6 갤러리 자동 자산화

        builder.Services.AddSingleton<TelemetryPipelineWiring>();      // 비전 이상 → 오프라인 큐 적재 + 백엔드 바인딩



        // ── v9.1.0 MAUI 모바일 생태계(데스크톱 소스 링크 공유 · 순수 BCL) ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.MauiMobileBridgeAdapter>();   // v8.8.3 모바일 브리지(배터리/햅틱/화면유지 훅) — v9.1.2 거버너의 배터리 소스

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.MauiMobileGestureEngine>();   // v9.1.1 제스처 엔진(드래그/핀치→타임라인 줌 · 스와이프→내부 뷰 전환)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Agents.MobileLowPowerOptimizer>(sp =>

            new(sp.GetService<PromptFlowStudio.Services.Integration.MauiMobileBridgeAdapter>()));         // v9.1.2 저전력 거버너(FPS·스웜 스레드 권고)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Inference.MobileOfflineStorageEngine>(); // v9.1.3 오프라인 스토리지(프롬프트·타임라인 델타 로컬 영속화)



        // ── v9.2.0 온디바이스 멀티모달 AI & 크로스 디바이스 동기화(데스크톱과 동일 소스 · 순수 BCL) ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Inference.LocalModelLoader>();           // v7.0.0 백엔드 프로브 — v9.2.1 후보 가속 경로 판정 소스

        builder.Services.AddSingleton<PromptFlowStudio.Services.Inference.OnDeviceVisionInferenceEngine>(sp =>

            new(sp.GetService<PromptFlowStudio.Services.Inference.LocalModelLoader>()));                 // v9.2.1 온디바이스 비전 추론(iOS=CoreML·Android=NNAPI 후보 프로브 · MMF 뷰 + 무할당 타일링)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.CrossDeviceStateSyncPipeline>(_ => new()); // v9.2.2 크로스 디바이스 동기화(Deck→Studio LAN 접속 · LWW CRDT 델타 · AltTransportSender 로 Bluetooth 결선)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Agents.AutonomousCrashTelemetryExporter>(sp =>

            new(sp.GetService<PromptFlowStudio.Services.Inference.OnDeviceVisionInferenceEngine>(),

                sp.GetService<PromptFlowStudio.Services.Agents.MobileLowPowerOptimizer>(),

                sp.GetService<PromptFlowStudio.Services.Inference.QuantumResistantLedgerSigner>()));     // v9.2.3 크래시/NPU 텔레메트리(모바일 스롯틀 자율 판정 · WORM 원장 바인딩 · 원장 미주입은 서명만 생략)



        // ── v9.3.0 공간 글래스모피즘 · 양자 내성 P2P 메시 · 자율 복구 워치독(데스크톱과 동일 소스 · 순수 BCL) ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.SpatialGlassmorphismRenderer>();           // v9.3.1 글래스 뎁스 레이어(matrix3d · Fresnel · 굴절)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.QuantumP2pMeshRouter>(sp =>

            new(sp.GetService<PromptFlowStudio.Services.Inference.QuantumResistantLedgerSigner>(), "deck"));   // v9.3.2 PQC 메시(모바일 노드 ID=deck · 원장 미주입은 봉인만 생략)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Agents.SelfHealingPipelineWatchdog>(sp =>

            new(sp.GetService<PromptFlowStudio.Services.Agents.AutonomousCrashTelemetryExporter>(),

                sp.GetService<PromptFlowStudio.Services.Agents.MobileLowPowerOptimizer>()));                 // v9.3.3 워치독(교착 CTS 교체 · 압축 수집 · NPU 감속 → 저전력 거버너)



        // ── v9.4.0 자율 워크플로우 합성 · 초저지연 엣지 방송 · 멀티테넌트 거버너(데스크톱과 동일 소스 · 순수 BCL) ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Agents.AutonomousWorkflowSynthesizer>();          // v9.4.1 의도 → DAG 합성(Kahn 수렴)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.ZeroLatencyEdgeBroadcaster>();        // v9.4.2 엣지 프리뷰 방송(Deck 수신 · 바인드 실패 시 인프로세스 강등)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.MultiTenantResourceGovernor>(_ => new());       // v9.4.3 테넌트 SLA 거버너(모바일 RAM 기준 용량 자동 산정)

        // ── v9.5.0 적응형 뉴럴 디렉터 · 분산 엣지 합의 메시 · 제로트러스트 거버넌스 오디터 ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Agents.AdaptiveNeuralDirector>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.DistributedEdgeConsensusMesh>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.ZeroTrustGovernanceAuditor>();

        // ── v9.6.0 자율 뉴럴 메모리 스트리머 · 양자 내성 신원 연합 · 실시간 이상 감지 센티널 ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Inference.AutonomousNeuralMemoryStreamer>(_ => new());

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.QuantumIdentityFederation>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Agents.RealTimeAnomalySentinel>();

        // ── v10.5.0-F1: 보안 시크릿 격리 볼트 — DPAPI 는 #if WINDOWS 격리라 모바일에선 환경변수/Graceful 폴백 경로만 탐(등록 안전).

        builder.Services.AddSingleton<PromptFlowStudio.Services.Security.SecureSecretVault>();

        // ── v9.7.0 자율 스웜 가버넌스 · 양자 스토리지 봉인 · 자기 최적화 멀티모달 파이프라인 ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Agents.AutonomousSwarmGovernanceEngine>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Inference.QuantumStorageSealingEngine>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.SelfOptimizingMultimodalPipeline>();

        // ── v9.8.0 뉴럴 타일링 업스케일러 · 기밀 컴퓨팅 증명기 · 적응형 멀티클라우드 페일오버 ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.AutonomousNeuralTilingUpscaler>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.ConfidentialComputingAttestor>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.AdaptiveMultiCloudEdgeFailover>();

        // ── v9.9.0 스웜 합의 엔진 · 양자 저항 상태 봉인기 · 자기 최적화 파이프라인 튜너 ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Agents.SwarmConsensusEngine>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Security.QuantumResistantStateSealer>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Pipeline.SelfOptimizingPipelineTuner>();

        // ── v10.0.0 Enterprise Core Release: 감사 텔레메트리 집계기 · 강화 정책 집행기 · 자율 헬스 오케스트레이터 ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Telemetry.EnterpriseAuditTelemetryAggregator>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Security.HardenedSecurityPolicyEnforcer>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Orchestration.AutonomousSystemHealthOrchestrator>();

        // ── v10.0.0-LTS: 핫픽스 패치 오케스트레이터 · 제로트러스트 프로덕션 텔레메트리 익스포터 ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Operations.LtsHotfixPatcher>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Telemetry.ProductionTelemetryExporter>();

        // ── v10.5.0-F4: OTLP 로그 리포트 시각화 프로바이더 — 순수 집계/CircularBuffer(하드웨어 비의존) → 데스크톱+모바일 공용.

        builder.Services.AddSingleton<PromptFlowStudio.Services.Telemetry.OtlpTelemetryVisualizerProvider>();

        // ── v10.3.0 프로덕션 아티팩트 패키징 & 릴리스 무결성 최종 검증(하드웨어 비의존 → 데스크톱+모바일 공용) ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Operations.ProductionArtifactPackager>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Security.ReleaseIntegrityValidator>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Operations.ProductionManifestExporter>();

        // ── v10.5.0-F5: 핫픽스 자동 서명기 — LtsHotfixPatcher/ReleaseIntegrityValidator 재사용(순수 파일 I/O) → 데스크톱+모바일 공용.

        builder.Services.AddSingleton<PromptFlowStudio.Services.Operations.HotfixAutoSignerEngine>();

        // ── v10.1.0 알리바바 Wan 2.1 저사양 가속: 순수 계산(AlibabaWanLowSpecEngine)만 등록.

        //    AdaptiveHardwareProfiler/WanPipelineOrchestrator 는 WanPipelineHardening(Process 기반 nvidia-smi)에

        //    의존해 iOS/Android 미지원(Process.Start 불가) — WanPipelineHardening 과 동일하게 Mobile DI 미등록.

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.AlibabaWanLowSpecEngine>();

        // ── v10.5.0-F3: 하드웨어 자율 캘리브레이터 — 순수 스칼라 산술(Process/하드웨어 API 비의존) → 모바일 등록 가능.

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.AutoTunerHardwareCalibrator>();

        // ── v10.2.0 성능 튜닝/벤치마킹: 3종 모두 하드웨어 비의존 → 모바일 등록 가능(WanPipelineOrchestrator 는 여전히 데스크톱 전용).

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanPerformanceBenchmarker>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.LivePipelineAutoTuner>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.BenchmarkingReportGenerator>();

        // v11.0.1-Mobile-MultiPlatform: OperationsCanaryHealthChecker 데스크톱에만 등록돼 있던 걸 여기서 보강

        //   (SecureSecretVault/OtlpTelemetryVisualizerProvider/LivePipelineAutoTuner 3대 의존 전부 위에서 등록됨).

        builder.Services.AddSingleton<PromptFlowStudio.Services.Operations.OperationsCanaryHealthChecker>();

        // v11.2.0: OBS WebSocket 5.0 리액션 엔진 - WanDynamicMotionController/LoRAHotSwapManager 는 이미 위에서 등록됨.

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.InteractiveReactionEngine>();

        // ── v10.6.0-F2: 이종 하드웨어 스케줄러 — 비-Windows 는 Registry 조회 구간 자체가 #if 로 제외돼 항상 빈 목록/

        //    단일 폴백 모드로 안전 동작 → 모바일 등록 가능(원 지시서는 MainForm.cs 만 명시했으나, 아래 두 엔진의

        //    ctor 필수 의존이라 모바일에서도 등록하지 않으면 DI 해석이 깨짐 — 누락 시 회귀이므로 함께 등록).

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.HeterogeneousTaskScheduler>();

        // ── v10.6.0-F3: 동적 모션 제어기/LoRA 핫스왑 매니저 — 순수 BCL, 하드웨어 비의존 → 모바일 등록 가능

        //    (원 지시서는 MainForm.cs 만 명시했으나, WanPostProcessingEngine 의 ctor 필수 의존이라 모바일 DI 회귀 방지 위해 함께 등록).

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanDynamicMotionController>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.LoRAHotSwapManager>();

        // v11.0.0: multimodal audio sync engine - pure BCL FFT, reuses WanDynamicMotionController/HeterogeneousTaskScheduler -> desktop+mobile shared.

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.MultimodalAudioSyncEngine>();

        // v12.2.0-Phase3: 리액션 오버레이/메시 부하분산 - 순수 BCL·하드웨어 비의존(Services\** 소스 링크로 모바일에도 컴파일됨) → 양쪽 DI 등록 헌장.

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.StreamerReactionEngine>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.P2pLoadBalancer>();

        // ── v10.6.0: Wan 2.1 후처리/프리뷰 — 의존 전이가 전부 하드웨어 비의존(DirectMlNativeAdapter 도 비-Windows 폴백 내장)이라 모바일 등록 가능.

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanPostProcessingEngine>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanProgressivePreviewer>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.WanQualitySpeedGovernor>();   // v15.1.0 Wan 품질·속도 거버너(순수 BCL)

        // ── v10.5.0-F2: ComfyUiWanConnector 는 순수 네트워킹(HttpClient/ClientWebSocket)이라 모바일 등록 가능.

        //    DirectMlWanConnector/WanBackendFactory 는 AdaptiveHardwareProfiler(Process 기반) 의존 전이로 데스크톱 전용 미등록.

        builder.Services.AddSingleton<PromptFlowStudio.Services.AI.Wan.ComfyUiWanConnector>();



        // ── v13.0.1 모바일 DI 미등록 교정 — MobileStudioDeck(ILocalMediaService·DistributedRenderGrid·WebhookDispatcher·WebRtcStreamer)와

        //    MobileNavDrawer(SidebarGuideService)가 @inject 하는데 모바일 컨테이너에 없어 루트 마운트 시

        //    "Cannot provide a value for property" 로 모바일 UI 전체가 붕괴하던 결함. 생성자 의존 2종(IVideoMergeService←LocalMediaService,

        //    NodeWorkerService←DistributedRenderGrid) 포함. 7종 모두 링크 소스(Services/**)이며 Compile Remove 대상 아님.

        //    LocalMediaService/VideoMergeService 는 IConfiguration 을 요구 - MAUI 빌더 구성 객체를 명시 등록(이미 있으면 유지).

        builder.Services.TryAddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(builder.Configuration);

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.IVideoMergeService, PromptFlowStudio.Services.Media.VideoMergeService>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.FfmpegVideoMixerService>();   // v16.12.0 후처리 엔진(데스크톱 동일 · ffmpeg 부재 시 Graceful)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.FfmpegAudioMixerService>();

        builder.Services.TryAddSingleton<PromptFlowStudio.Services.Media.IVoiceoverService, PromptFlowStudio.Services.Media.VoiceoverService>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.AiVoiceoverTtsService>();     // v16.14.0 동화 나레이션 TTS(로컬 TTS 미등록 시 Edge/ElevenLabs만)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.AutoSubtitleGenerator>();     // v16.14.0 세이프존 자막

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.PostProductionEngine>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.OsmuSmartReframingService>();   // v16.15.0 OSMU 재구성

        builder.Services.AddSingleton<PromptFlowStudio.Services.Storyboard.SocialMetadataAiGenerator>(); // v16.15.0 (Gemini/오프라인 SEO 미등록 시 로컬 템플릿)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.OsmuExportPackager>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.CommercialProductSegmenterService>();   // v16.19.0 커머셜 브랜드(HUD 카드 공유 · 스튜디오 페이지는 데스크톱 전용)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.SponsorVideoCompositorService>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Storyboard.CommercialAdScriptAiGenerator>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.CommercialBrandCampaignService>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Flow.VisualNodeGraph>();                // v16.21.0 노드 그래프 미러(실행 파이프라인·브릿지 서버는 데스크톱 전용)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.CanvasMaskingService>();          // v16.21.0 마스크 캔버스(원격 동기화 수신 적용)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.LiveSessionSyncClient>();   // v16.21.0 데스크톱 라이브 세션 클라이언트

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.LiveSessionSyncBridge>();   // v16.21.0 HUD 카드 공유(자동 시작 없음 · HttpListener 미지원 시 Start Graceful 실패)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.AiAudioDrivenLipSyncService>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Storyboard.CharacterIdentityLockService>();   // v16.22.0 캐릭터 아이덴티티(ffmpeg 부재 시 특징 추출만 Graceful 생략)

        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Storyboard.EpisodicStorySeriesBuilder(

            sp.GetRequiredService<PromptFlowStudio.Services.Storyboard.CharacterIdentityLockService>(), new PromptFlowStudio.Services.Storyboard.StoryboardRepository()));   // v16.22.0 시리즈 빌더(저장소 미등록 → 전용 인스턴스)   // v16.20.0 립싱크 오케스트레이터(ILipSyncService 미등록 → 원격 API/FFmpeg 보정만)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.ViralHookVariantGeneratorService>();   // v16.23.0 후킹 변주(A/B 패키저·게시는 데스크톱 전용 — 소셜 브릿지 미등록)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.RetentionHeatmapPredictorEngine>();    // v16.23.0 유지율 예측/램핑

        builder.Services.AddSingleton<PromptFlowStudio.Services.Storyboard.CulturalLocalizationTranspiler>();   // v16.24.0 현지화(Gemini 미등록 시 오프라인 변환·글로서리)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.MultilingualVoiceCloningDubbingService>(); // v16.24.0 다국어 더빙(ffmpeg 부재 시 Graceful)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Broadcast.RealtimeAvatarPuppeteerService>(); // v16.25.0 아바타 퍼페티어(HUD 카드 공유 · 스튜디오 페이지는 데스크톱 전용)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Broadcast.AiLiveLoopStreamerService>();      // v16.25.0 루프 송출(ffmpeg 부재 시 Graceful)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Broadcast.LiveChatAiReactionEngine>();       // v16.25.0 채팅 AI 반응

        builder.Services.AddSingleton<PromptFlowStudio.Services.Interactive.InteractiveConversionAnalytics>();    // v16.26.0 인터랙티브 전환율 분석(HUD 카드 공유)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Interactive.ShoppableVideoOverlayEngine>();       // v16.26.0 쇼핑 핫스팟(ffmpeg 부재 시 추적 Graceful)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Interactive.InteractiveChoiceBranchingService>(); // v16.26.0 분기 오케스트레이터(선형 렌더 Graceful 생략)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Enterprise.CloudRenderFarmDispatcherService>();   // v16.27.0 렌더 팜(HUD 공유 · ffmpeg 부재 시 Graceful)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Enterprise.TeamCollaborationSessionBridge>();     // v16.27.0 팀 협업(참가 모드 · 호스트는 데스크톱 권장)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Enterprise.C2paContentProvenanceEngine>();        // v16.27.0 C2PA 검증(인젝션은 ffmpeg 필요)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Autonomous.PerformanceFeedbackLoopEngine>();      // v16.28.0 성과 학습(HUD 공유)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Autonomous.CampaignRoiOptimizationEngine>();      // v16.28.0 ROI 재배분
        builder.Services.AddSingleton<PromptFlowStudio.Services.Autonomous.AutonomousCampaignDirectorService>();  // v16.28.0 디렉터(생산/게시 미등록 → 기획·학습만)
        builder.Services.AddSingleton<PromptFlowStudio.Services.MultiEngine.GenerativePromptTranspilerEngine>();      // v16.29.0 프롬프트 트랜스파일러
        builder.Services.AddSingleton<PromptFlowStudio.Services.MultiEngine.PluginExtensionMarketplaceFramework>();   // v16.29.0 플러그인(모바일은 동적 DLL 로드 미스캔 · 목록만)
        builder.Services.AddSingleton<PromptFlowStudio.Services.MultiEngine.GenerativeVideoMultiEngineRouter>();      // v16.29.0 루터(Wan/로컬 미등록 → 원격 어댑터만)
        builder.Services.AddSingleton(sp =>                                                                          // v16.30.0 제로 트러스트 쉴드(DPAPI-NG KEK · AES-256-GCM · SSO · HMAC 감사 체인)
        {
            var z = new PromptFlowStudio.Services.Master.ZeroTrustSecurityShieldService(sp.GetRequiredService<PromptFlowStudio.Services.Security.SecureSecretVault>(),
                sp.GetService<PromptFlowStudio.Services.Security.EnterpriseSecurityAudit>());
            z.Unlock();
            return z;
        });
        builder.Services.AddSingleton<PromptFlowStudio.Services.Master.CloudDeltaAssetSyncEngine>();           // v16.30.0 CDC 델타 청크 동기화 · P2P 릴레이
        builder.Services.AddSingleton<PromptFlowStudio.Services.Master.UniversalMasterStudioOrchestrator>();   // v16.30.0 30 모듈 매트릭스 + 마스터 파이프라인
        builder.Services.AddSingleton<PromptFlowStudio.Services.Spatial.Spatial3dGaussianSplattingEngine>();      // v16.31.0 3D 가우시안 스플래팅 씬 복원(단안 깊이 · 패닝 정합 · PLY)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Spatial.MvHevcSpatialVideoTranscoder>();          // v16.31.0 DIBR SBS · MV-HEVC(spatial CLI) · 바이노럴 · 공간 패키지
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Spatial.ImmersiveVisionProInteractionBridge(   // v16.31.0 시선/제스처 공간 오버레이 브릿지
            sp.GetService<PromptFlowStudio.Services.Interactive.ShoppableVideoOverlayEngine>(),
            sp.GetService<PromptFlowStudio.Services.Interactive.InteractiveChoiceBranchingService>(),
            sp.GetService<PromptFlowStudio.Services.Interactive.InteractiveConversionAnalytics>()));
        builder.Services.AddSingleton<PromptFlowStudio.Services.Virtual.CinematicCameraMotionControlEngine>();          // v16.32.0 시네마틱 카메라 리그 궤적 · 프롬프트 인젝션 · UE JSON/CSV
        builder.Services.AddSingleton<PromptFlowStudio.Services.Virtual.AiChromaKeyingAndRelightingEngine>();          // v16.32.0 스마트 크로마키 · 스필 억제 · 조명 매칭 리라이팅
        builder.Services.AddSingleton<PromptFlowStudio.Services.Virtual.UnrealEngine5LiveStagingBridge>();             // v16.32.0 UE5 OSC 카메라 동기화 · NDI/RTSP 수신 · 스테이징 패키지 · IDisposable
        builder.Services.AddSingleton<PromptFlowStudio.Services.Sound.AiGenerativeFoleySoundscapeEngine>();           // v16.33.0 모션/키워드 → 절차적 Foley SFX · 타임코드 인젝션
        builder.Services.AddSingleton<PromptFlowStudio.Services.Sound.AudioStemSeparationAndMixingEngine>();          // v16.33.0 4-스템 분리(BCL HPSS · Demucs 선택) · 덕킹/EQ/LUFS 믹서
        builder.Services.AddSingleton<PromptFlowStudio.Services.Sound.DolbyAtmosSpatialAudioMapper>();                // v16.33.0 Atmos 오브젝트 매핑 · 바이노럴/5.1/7.1.4/ADM BWF 마스터
        builder.Services.AddSingleton<PromptFlowStudio.Services.Vfx.AiNeuralVfxRelightingEngine>();                 // v16.34.0 깊이/법선 기반 포인트·스포트·디렉셔널 리라이팅 + 그림자
        builder.Services.AddSingleton<PromptFlowStudio.Services.Vfx.SuperResolution8kUpscalerEngine>();             // v16.34.0 Real-ESRGAN/Compact 8K 업스케일 · 디노이즈/디블록 폴백
        builder.Services.AddSingleton<PromptFlowStudio.Services.Vfx.MotionFrameInterpolationEngine>();              // v16.34.0 RIFE/FLAVR 60·120fps 보간 · 8x 슬로모션(MCI 폴백)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Screenplay.WorldbuildingStoryAgentEngine(sp));     // v16.35.0 멀티 에이전트 세계관/인물/관계도(Gemini 선택 · BCL 토론 폴백 · JSON 영구 저장)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Screenplay.SceneBeatSheetScriptGenerator(sp));     // v16.35.0 3막/숏폼 비트 시트 · Fountain 대본 생성/파싱
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Screenplay.ScreenplayToStoryboardConverter(sp));   // v16.35.0 대본 → 컷/프롬프트/립싱크/TTS 큐(테마 시네마틱 매칭)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwin.AiDigitalTwinGeneratorEngine(sp));          // v16.36.0 사진/영상 → 3DMM 메시 · 알베도 · 피부/헤어/체형 · 정체성 벡터 · 테마 결합
        builder.Services.AddSingleton<PromptFlowStudio.Services.DigitalTwin.RealtimeWebcamMotionCaptureTracker>();                     // v16.36.0 33 관절 · 468 안면 · 21×2 손 60FPS 추적(웹캠 BCL/외부 MediaPipe/합성)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwin.FacialAndHandGestureMappingController(sp));   // v16.36.0 EWMA 지터 억제 · 리그 리타겟 · 표정/제스처 · BVH · 인젝션
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Music.AiGenerativeMusicEngine(sp));            // v16.37.0 Suno/Udio 어댑터 + BCL 절차적 신디사이저/포먼트 보컬 폴백
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Music.AudioBeatVisualSyncEngine(sp));          // v16.37.0 BPM/온셋/드랍 분석 · 무할당 컷 매칭 · 전환/VFX/스피드 램프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Music.AlgorithmicChoreographyController(sp));  // v16.37.0 절차적 댄스 안무 · 트윈 리타겟 · MV 구성
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VTuber.VTuberPersonaMemoryEngine(sp));        // v16.38.0 페르소나 · 인메모리 벡터 RAG 장기 기억 · Gemini/BCL 답변
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VTuber.LiveVTuberBroadcastingBridge(sp));      // v16.38.0 VMC(OSC) · VTube Studio · OBS WebSocket · 오버레이 SSE
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VTuber.FanInteractionReactionController(sp));  // v16.38.0 후원 리액션 큐 · 감사 안무/징글/VFX · 자율 방송
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Gaming.RealtimeGameVisionEventExtractor());            // v16.39.0 게임 화면 캡처(GDI) · HUD 비전/OCR · 게임 이벤트
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Gaming.LiveGamingCommentaryVoiceEngine(sp));          // v16.39.0 이벤트 → 페르소나 해설 · 1초 예산 TTS
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Gaming.InteractiveGamingOverlayController(sp));       // v16.39.0 훈수/예측 투표/동료 아바타 오버레이
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Xr.VolumetricVideoHologramStreamer(sp)); // v16.40.0 볼류메트릭 홀로그램 스트리머
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Xr.VirtualConcertStageOrchestrator(sp)); // v16.40.0 가상 콘서트 무대 오케스트레이터
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Xr.MultiUserVirtualAudienceBridge(sp)); // v16.40.0 다자간 가상 관객 브릿지
        builder.Services.AddSingleton<PromptFlowStudio.Services.Cinema.CinematicUniverseTimelineEngine>(); // v16.41.0 유니버스 개연성 검증
        builder.Services.AddSingleton<PromptFlowStudio.Services.Cinema.AutonomousSceneBlockingEngine>(); // v16.41.0 자율 샷 리스트
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.FeatureFilmMasteringPackager(sp)); // v16.41.0 장편/시리즈 마스터링
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Mam.EnterpriseMamSmartIndexingEngine()); // v16.42.0 MAM 시맨틱 색인
        builder.Services.AddSingleton<PromptFlowStudio.Services.Mam.AiContentModerationComplianceShield>(); // v16.42.0 글로벌 심의 쉴드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Mam.MultiCloudEdgeCdnDistributionRouter(sp)); // v16.42.0 멀티 CDN · DRM 라우터
        builder.Services.AddSingleton<PromptFlowStudio.Services.Mcr.ContextualServerSideAdInsertionEngine>(); // v16.43.0 SSAI 인젝터
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Mcr.AutonomousMcrProgramSwitcherEngine(sp)); // v16.43.0 MCR 스위처
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Mcr.DynamicAdRevenueOptimizationEngine(sp)); // v16.43.0 eCPM 수익 최적화
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Newsroom.RealtimeNewsFeedFactCheckEngine(sp)); // v16.44.0 속보 수집 · 팩트체크
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Newsroom.NewsMotionGraphicsOverlayEngine()); // v16.44.0 뉴스 그래픽
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Newsroom.DualAiNewsAnchorGenerator(sp)); // v16.44.0 듀얼 앵커
        builder.Services.AddSingleton<PromptFlowStudio.Services.Sports.BiometricSportsAnalyticsOverlayEngine>(); // v16.45.0 속도/거리/심박/xG·xA · 미니맵 오버레이
        builder.Services.AddSingleton<PromptFlowStudio.Services.Sports.AutomatedSportsHighlightReplayEngine>(); // v16.45.0 함성/골 감지 · 120fps 보간 슬로 리플레이
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sports.AiSportsMulticamAutoDirectorEngine(sp)); // v16.45.0 9캠 볼 추적 · 자율 PGM 스위칭
        builder.Services.AddSingleton<PromptFlowStudio.Services.EduTech.InteractiveEduVideoQuizEngine>(); // v16.46.0 타임코드 퀴즈 인젝션 · 채점 · BKT 숙달도
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.EduTech.AdaptiveAiTutorCoPilot(sp)); // v16.46.0 RAG 튜터 · 비유 보충 강의 · 립싱크 아바타
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.EduTech.ScormXApiElearningPackager(sp)); // v16.46.0 SCORM 1.2/2004 · xAPI · LTI 1.3 패키저
        builder.Services.AddSingleton<PromptFlowStudio.Services.Accessibility.AiSignLanguageAvatarSynthesizer>(); // v16.47.0 KSL/ASL 수어 번역 · 트윈 리그 PiP 아바타
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Accessibility.GenerativeAudioDescriptionEngine(sp)); // v16.47.0 대사 공백 화면 해설 · 자동 더킹
        builder.Services.AddSingleton<PromptFlowStudio.Services.Accessibility.WcagAccessibilityCorrectionFilter>(); // v16.47.0 Daltonization · WCAG 명암비 · 난독증 자막
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Accessibility.BarrierFreePackager(sp)); // v16.47.0 1-Click 배리어프리 패키지
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Commerce.LiveCommerceInventoryOrderBridge(sp)); // v16.48.0 재고/주문 무할당 수신 · Shopify/Woo/REST · 웹훅 HMAC
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Commerce.AiLiveShowHostEngine(sp)); // v16.48.0 자율 쇼호스트 · 채팅 Q&A · 품절/폭주 멘트
        builder.Services.AddSingleton<PromptFlowStudio.Services.Commerce.ShoppertainmentOverlayEngine>(); // v16.48.0 타임세일 3D · 구매 토스트 · 회전 카드 · QR
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Commerce.ShoppertainmentShowRunner(sp)); // v16.48.0 1-Click 쇼퍼테인먼트 방송
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Podcast.MultiAgentTalkshowConversationOrchestrator(sp)); // v16.49.0 MC+패널 발화권 · 찬반 토론/인터뷰 · Gemini 페르소나
        builder.Services.AddSingleton<PromptFlowStudio.Services.Podcast.AutonomousPodcastVideoMultiCamEngine>(); // v16.49.0 액티브 스피커 탐지 · 자동 멀티캠 · 하이라이트
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Podcast.InteractiveAudienceAgendaInterventionEngine(sp)); // v16.49.0 시청자 개입 채택 · 패널 라우팅
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Podcast.PodcastStudioShowRunner(sp)); // v16.49.0 1-Click 자율 팟캐스트 녹화/송출
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Empire.OmnichannelGlobalBroadcastDispatcher()); // v16.50.0 53채널 렌디션 공유 · 메타 최적화 · 병렬 송출/페일오버
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Empire.SelfEvolvingMediaEmpireFranchiseEngine()); // v16.50.0 IP 스핀오프 자가 증식
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Empire.ExecutiveEmpireTelemetryAndRevenueMatrix(sp)); // v16.50.0 50시스템 헬스·자율 복구 · 메모리 · 수익/정산
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Empire.MediaEmpireRunner(sp)); // v16.50.0 1-Click 미디어 제국 가동
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Hardening.ZeroAllocHotPathProfilingGuard()); // v16.51.0 핫패스 할당/GC 프로브 · SIMD 무할당 우회 · 히트맵
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Hardening.EnterpriseE2eSystemHealthAuditor(sp, System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(builder.Services, d => d.ServiceType)))); // v16.51.0 E2E 교차 진단 · 픽스 주입
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Hardening.ProductionReleaseDeploymentPackager(sp)); // v16.51.0 6타깃 publish 계획 · 트림/AOT IL 분석 · 서명 매니페스트 번들
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalOps.LiveProductionHotReloadingEngine(sp)); // v16.52.0 식/규칙 DSL 트랜스파일 · 서명 ALC 핫 스왑 · 카나리/롤백
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalOps.GlobalSlaAutoScalingLoadBalancer()); // v16.52.0 P2C 라우팅 · 예측 오토스케일 · 99.999% SLA/번 레이트
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalOps.ZeroTrustContinuousSecurityAuditor(sp)); // v16.52.0 키 유출 · 메모리 변조 복원 · 패킷 위조/재생/폭주 격리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalOps.GlobalOperationsRunner(sp)); // v16.52.0 글로벌 운용 러너 · 1-Click 최적화
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sovereign.SovereignMediaCloudConfidentialMesh()); // v16.53.0 TEE 원격 증명 · 데이터 주권 배치 · 기밀 채널 렌더
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sovereign.PostQuantumMediaVaultEngine(sp)); // v16.53.0 ML-KEM-768+ECDH / ML-DSA-65+ECDSA 하이브리드 금고 · 워터마크 · PQ 원장
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sovereign.MultiTenantStudioGovernanceEngine(sp)); // v16.53.0 RBAC/ABAC · 테넌트 경계 · 쿼터 · 감사 체인
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sovereign.SovereignCloudOrchestrator(sp)); // v16.53.0 1-Click 양자 보안 봉인
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.World.AiGenerative4dWorldModelEngine()); // v16.54.0 4D 시공간 월드 모델 · 물리 시뮬 · 레이트레이서 · 상태 매트릭스
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.World.InteractiveVideoBranchingTranspiler()); // v16.54.0 분기 DSL 트랜스파일러 · 무할당 심리스 분기 플레이어
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.World.NeuralSceneGraphPromptOptimizer()); // v16.54.0 4D 장면 그래프 · 프롬프트 정제 · 퍼셉트론
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.World.WorldModelStudioRunner(sp)); // v16.54.0 1-Click 4D 생성 · 인터랙티브 재생 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Teleport.AiVolumetricTeleportationMeshEncoder(sp)); // v16.55.0 트윈/포인트 클라우드 → 동적 메시 코덱 · 노이즈 게이트 · MTU+XOR FEC
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Teleport.SpatialAudioVisualBeamformingEngine()); // v16.55.0 시청자 귀/눈 추적 · 지연-합 음향 집속 · 홀로 위상
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Teleport.HolographicTelepresenceRelayBridge(sp)); // v16.55.0 Vision Pro/Quest 3/WebXR/홀로그램 SFU 중계 · 50ms 예산
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Neuro.NeuroBioSignalEmotionParserEngine()); // v16.56.0 EEG/HRV/GSR 파서 · 몰입/흥분/쾌적/공포 지수 · UDP
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Neuro.BioFeedbackGenerativeMediaModifier(sp)); // v16.56.0 감정 → 그레이딩/BGM/효과음/VFX/분기 변조
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Neuro.CollectiveEmotionHeatmapAggregator()); // v16.56.0 집단 공감도 · 피크 · 3D 감정 스펙트럼
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Neuro.BciNeuroStudioRunner(sp)); // v16.56.0 BCI 바이오피드백 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MediaPipeline.UnlimitedMediaStreamBufferPipeline()); // v16.57.0 무제한 청크 스트리밍 수신 · 메모리 맵 프로브 · 프록시
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MediaPipeline.AutoPresetContextTuner()); // v16.57.0 NLU+비전 오토 프리셋
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MediaPipeline.UniversalFullFeatureChainAdapter(sp)); // v16.57.0 전체 기능 오토 체이닝
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Neural3d.AiGaussianSplattingRasterizerEngine()); // v16.58.0 3DGS EWA 타일 라스터라이저 · 다시점 복원 · PLY
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Neural3d.DynamicNeRfToSplattingConverter()); // v16.58.0 NeRF↔3DGS 변환 · 키/델타 스트림(MTU+FEC)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Neural3d.NeuralCameraPoseRelightingEngine()); // v16.58.0 LM PnP 서브밀리 추적 · HDR SH9 리라이트 · 그림자
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Neural3d.GaussianSplattingStudioRunner(sp)); // v16.58.0 3DGS 라이브 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Icvfx.IcvfxLedWallFrustumRendererEngine()); // v16.59.0 LED 월 이너/아우터 프러스텀 · 오프액시스 · 지연 보상
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Icvfx.AcesColorManagementAndMoireSuppressionEngine()); // v16.59.0 ACES/OCIO · 3D LUT · PQ · 무아레 위상 상쇄
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Icvfx.AgenticAutonomousVirtualProductionDirector(sp)); // v16.59.0 카메라/조명(DMX·Art-Net)/VFX 에이전트 · 코디네이터
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Icvfx.VirtualProductionStudioRunner(sp)); // v16.59.0 ICVFX 자율 디렉팅 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Metaverse.SpatialXrMetaverseBroadcastingEngine()); // v16.61.0 6DoF 3DGS/4D 스테이지 · 스테레오 · XRS1 씬 스트림 · gRPC/WebXR
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Metaverse.SpatialComputingInteractionBridge()); // v16.61.0 Vision Pro/Quest 시선 · 핀치 · 서사 분기
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Metaverse.NeuralHologramMultiuserStateSync()); // v16.61.0 다중 사용자 동기화 · 홀로 오버레이
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Metaverse.SpatialXrStudioRunner(sp)); // v16.61.0 공간 XR 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Synergy.EngineHyperAccelerationOptimizer()); // v16.62.0 SIMD 하이퍼 가속 · 메모리 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Synergy.EnhancedScriptToScreenDirector(sp)); // v16.62.0 대본 → 숏/OST/폴리 · 일관성 검수
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Synergy.DeepCrossEngineSynergyOrchestrator(sp)); // v16.62.0 BCI·ICVFX·3DGS·4D 교차 시너지 버스
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Hardware.HardwareZeroCopyDirectPathPipeline()); // v16.63.0 CPU-GPU Zero-Copy DirectPath 공유 메모리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Hardware.DistributedRenderFarmSyncEngine()); // v16.63.0 분산 렌더 노드 팜(로컬·RFT1 원격)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Hardware.HardwareVideoEncoderOffloader(sp)); // v16.63.0 NVENC/AMF/QSV 인코더 오프로드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Hardware.HardwareAccelerationStudioRunner(sp)); // v16.63.0 하드웨어 가속 스튜디오 루프
        builder.Services.AddSingleton(sp => PromptFlowStudio.Services.Edge.RealtimeGlobalMediaMeshRouter.BuildDemo(out _, out _)); // v16.64.0 글로벌 미디어 메시(컷스루 · ABR · 무단절 우회)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Edge.AiEdgeSwarmNodeOrchestrator()); // v16.64.0 에지 스웜(SWR1 하트비트 · SIMD 배정)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Edge.EdgeAutonomousAiAgentEngine(sp)); // v16.64.0 온디바이스 AI 에이전트(int8 증류 · 클라우드 0)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Edge.EdgeSwarmStudioRunner(sp)); // v16.64.0 에지 스웜 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LowSpec.HardwareAdaptiveLowSpecProfiler()); // v16.65.0 적응형 저사양 프로파일러(LOD · 연산 해상도)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LowSpec.DynamicLowBitQuantizationFallbackRouter()); // v16.65.0 INT4/INT8 가변 양자화 · iGPU/CPU SIMD 폴백
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LowSpec.ZeroAllocMemorySqueezerGuard()); // v16.65.0 아레나 압착 · GC 억제 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LowSpec.LowSpecPerformanceStudioRunner(sp)); // v16.65.0 저사양 초고성능 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Production.GlobalCloudEdgeOtaSyncEngine()); // v16.66.0 클라우드-에지 델타 OTA 동기화(A/B 무단절)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Production.AiSelfHealingQaTelemetryAuditor()); // v16.66.0 AI 자율 복구 QA · 서명 크래시 텔레메트리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Production.EnterpriseProductionPackagingVerifier()); // v16.66.0 5 플랫폼 패키징 · ECDSA 무결성 검증
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Production.EnterpriseProductionReleaseStudioRunner(sp)); // v16.66.0 엔터프라이즈 릴리스 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sre.BurnRateAndErrorBudgetOptimizer()); // v16.67.0 다중 창 번 레이트 · 오류 예산 자율 최적화
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sre.GlobalEdgeTelemetryAnomalyReactor()); // v16.67.0 노드 텔레메트리 이상 반응기(서브 ms 격리)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sre.AutonomousAiSreGridAgent(sp)); // v16.67.0 Five-Nines AI SRE 그리드 제어 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Sre.GlobalOperationsGridStudioRunner(sp)); // v16.67.0 글로벌 SRE 관제 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.AgentFederation.RealtimeSpatiotemporalKnowledgeGraph()); // v16.68.0 시공간 신경망 지식 그래프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.AgentFederation.ZeroTrustInterAgentConsensusResolver()); // v16.68.0 ML-DSA-65 BFT 에이전트 합의
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.AgentFederation.MultiAgentSwarmFederationOrchestrator(sp)); // v16.68.0 멀티 에이전트 스웜 연합(무할당 버스)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.AgentFederation.MultiAgentFederationStudioRunner(sp)); // v16.68.0 연합 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.RealtimeNeuralAudioDubbingEngine()); // v16.69.0 G2V · DTW 립싱크 · SIMD 입 ROI 워프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.MultiLanguageCulturalNuanceAdaptor()); // v16.69.0 관용구/은어/금기 문화 적응 · 자막
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.NeuralVoiceCloningAcousticTransfer()); // v16.69.0 보이스 클로닝(F0·음색·잔향 전송)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.GlobalLocalizationStudioRunner(sp)); // v16.69.0 로컬라이제이션 스튜디오 파이프라인
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Nexus.QuantumHybridNeuralMeshEngine()); // v16.70.0 양자 영감 시공간 텐서 메시(저랭크 · SQA QUBO)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Nexus.SpatiotemporalCloudNexusStreamer()); // v16.70.0 단일 FEC · HMAC 넥서스 스트림
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Nexus.AutonomousCreativeSingularityOrchestrator(sp)); // v16.70.0 시나리오 한 줄 → 모듈 오토 체이닝 DAG
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Nexus.OmniModalNexusStudioRunner(sp)); // v16.70.0 옴니모달 넥서스 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveStream.AiLiveBroadcastAutonomousSwitchingEngine()); // v16.71.0 60fps 자율 스위처(Cut/Dissolve/Wipe · A/V 지연 보상)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveStream.InteractiveStreamViewerFeedbackDirector()); // v16.71.0 채팅/후원/투표 → 월드·BGM·조명·모션
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveStream.UltraLowLatencyMultiPlatformStreamer()); // v16.71.0 RTMP·SRT·WebRTC 멀티캐스트
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveStream.LiveBroadcastStudioRunner(sp)); // v16.71.0 라이브 방송 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.OpenUsd.OpenUsdSpatialAssetCompositorEngine()); // v16.72.0 OpenUSD(.usda/.usdc/.usdz) 파서 · 레이어 합성 · 3DGS/4D 바인딩
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.OpenUsd.GenerativeWorldProceduralEngine()); // v16.72.0 프롬프트 → 지형/바이옴/자산 절차 생성(시밀러리티 시드)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.OpenUsd.NeuralPhysicsCollisionSimulator()); // v16.72.0 강체/천/유체 SIMD 물리 · 음향 반사 메시
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.OpenUsd.OpenUsdSpatialStudioRunner(sp)); // v16.72.0 USD 월드 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Twin.RealtimeDigitalTwinPhysicalSyncEngine()); // v16.73.0 IoT/센서 → USD/3DGS 트윈 동기화(지연 보상 · 양방향 명령)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Twin.NeuralSyntheticDataGeneratorEngine()); // v16.73.0 도메인 랜덤화 합성 RGB/깊이/세그/박스 데이터셋
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Twin.AutonomousSensorFeedbackLoopSimulator()); // v16.73.0 가상 센서 → 메트릭 스트림 → PID 피드백 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Twin.DigitalTwinStudioRunner(sp)); // v16.73.0 디지털 트윈 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.PathTracing.MaterialXBsdfShaderSynthesizer()); // v16.74.0 MaterialX standard_surface 파싱 · GGX/Lambert BSDF · 재질 진단
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.PathTracing.AiRealtimeSpatiotemporalDenoiser()); // v16.74.0 SVGF 계열 시공간 디노이저 · Catmull-Rom 업스케일
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.PathTracing.PathTracingStudioRunner(sp)); // v16.74.0 패스 트레이싱 스튜디오 루프
        builder.Services.AddSingleton(sp => sp.GetRequiredService<PromptFlowStudio.Services.PathTracing.PathTracingStudioRunner>().Tracer); // v16.74.0 볼류메트릭 패스 트레이서(러너 소유)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Mocap.NeuralMarkerlessPoseEstimationEngine()); // v16.75.0 다중 시점 SIMD 삼각측량 · 이상치 제거 · 단일 시점 리프팅 · One-Euro
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Mocap.AutoRiggingAndRetargetingSynthesizer()); // v16.75.0 오토 리깅 · 스킨 가중치 · DOF 클램프 리타겟
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Mocap.GenerativeCharacterPerformanceGuard()); // v16.75.0 대사/BCI 표정 · 비셈 · 발 고정 IK · 지면/충돌/관성 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Mocap.MotionCaptureStudioRunner(sp)); // v16.75.0 모션 캡처 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAudio.RealtimeAcousticRaytracingEngine()); // v16.76.0 음선 추적 RT60/EDC · 이미지 소스 · Maekawa 회절 폐색
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAudio.Generative3DSoundscapeSynthesizer(48000)); // v16.76.0 절차적 음원 · 도플러 · VBAP 7.1.4 · FDN · 객체 메타데이터
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAudio.NeuralBinauralHrtfSpatializer(48000)); // v16.76.0 Brown–Duda HRTF · 머리 추적 · 베드 가상화
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAudio.SpatialAudioStudioRunner(sp)); // v16.76.0 공간 오디오 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Holo.RealtimeVolumetricHologramStreamerEngine()); // v16.77.0 SIMD 공간 양자화 · 키/델타 varint · CRC32 · MTU 분할 · PLI
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Holo.AiTelepresenceAvatarSynthesizer()); // v16.77.0 Eye-Contact 시선 교정 · BCI 미세 표정 · 립싱크 지연 · 전용 HRTF 바이노럴
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Holo.SpatialAnchorHolographicWorldRouter()); // v16.77.0 중력 정렬 앵커 Procrustes · P2P LWW 메시 · 릴레이 우회 · 데드 레커닝
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Holo.HolographicTeleportationStudioRunner(sp)); // v16.77.0 홀로그래픽 텔레포테이션 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.NeuralChoiceTreeEmotionEvaluator()); // v16.78.0 BCI · 시선 · 텍스트/음성 6차원 감정 융합 · JS 발산 · 선택지 softmax
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.ZeroSeamlessTransitionBlendingGuard(48000)); // v16.78.0 SIMD 프레임 · 4D 스플랫 · 등전력 영교차 오디오 크로스페이드 · 팝 감지
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.RealtimeInteractiveNarrativeBranchingEngine(sp.GetService(typeof(PromptFlowStudio.Services.Cinema.NeuralChoiceTreeEmotionEvaluator)) as PromptFlowStudio.Services.Cinema.NeuralChoiceTreeEmotionEvaluator)); // v16.78.0 서사 그래프 · 결정 창 프리페치 · 감독 개입
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinema.InteractiveCinemaStudioRunner(sp)); // v16.78.0 인터랙티브 시네마 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Editorial.NeuralRhythmMontageSynthesizer(48000)); // v16.79.0 온셋 플럭스 · 자기상관 템포 · 빗살 위상 · 감정 곡선 호흡 · 비트 몽타주 조립
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Editorial.AutonomousMultiCameraEditorialEngine(sp.GetService(typeof(PromptFlowStudio.Services.Editorial.NeuralRhythmMontageSynthesizer)) as PromptFlowStudio.Services.Editorial.NeuralRhythmMontageSynthesizer)); // v16.79.0 SIMD 모션/오디오 · 시선 · 180° 규칙 교차 편집
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Editorial.NleTimelineFormatExportGuard()); // v16.79.0 EDL(CMX3600) · FCPXML 1.9 · AAF(CFB) 무할당 스트리밍 익스포트
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Editorial.AutonomousEditorialStudioRunner(sp)); // v16.79.0 자율 편집 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicMastering.RealtimeIcvfxLedWallCameraTrackingEngine()); // v16.80.0 6DoF α-β 예측 · 오프-액시스 투영 · 인너 프러스텀 · 레이어 시차
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicMastering.NeuralColorGradingAcesMasteringPipeline()); // v16.80.0 AP0→ACEScg SIMD · 감정 MLP 룩 · 33³ LUT · SDR/HDR 마스터링
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicMastering.VirtualProductionRenderSyncGuard()); // v16.80.0 Genlock PLL · SMPTE TC · 셔터 위상 스왑 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicMastering.VirtualProductionMasteringStudioRunner(sp)); // v16.80.0 가상 프로덕션 & 마스터링 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GenerativeVfx.NeuralParticleFluidDynamicsEngine(PromptFlowStudio.Services.GenerativeVfx.GenerativeVfxStudioRunner.DefaultCapacity)); // v16.81.0 SIMD 이중 밀도 완화 유체 · 신경망 에디 점성 · 표면장력
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GenerativeVfx.VolumetricFireSmokeAtmosphericSynthesizer()); // v16.81.0 프롬프트 이벤트 볼륨 화염/연기 · 압력 투영 · 광 투과율
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GenerativeVfx.InteractiveVfxCollisionSplashGuard()); // v16.81.0 OpenUSD 지형 · 3DGS 프록시 충돌 · 비산/파편 · 젖음 피드백
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GenerativeVfx.GenerativeVfxStudioRunner(sp)); // v16.81.0 제너레이티브 VFX 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalHuman.NeuralFacialMuscleSimulator()); // v16.82.0 ARKit 52 · FLAME 턱 LBS · 근육 동역학 SIMD 표정 합성
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalHuman.PhotorealisticMicroSkinSssSynthesizer()); // v16.82.0 Burley SSS 확산 · 모공/표정 주름 · 경로 추적 재질
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalHuman.AutonomousDigitalHumanGazeGuard()); // v16.82.0 감정선 시선 단속 · 깜빡임 · 동공 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalHuman.DigitalHumanPerformanceStudioRunner(sp)); // v16.82.0 디지털 휴먼 표정 연기 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HairCloth.NeuralStrandHairDynamicsEngine()); // v16.83.0 DFTL 스트랜드 헤어/모피 · SIMD 형상 이완 · 바람/관성 · 바디 충돌
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HairCloth.GenerativeGarmentClothCollisionSynthesizer()); // v16.83.0 XPBD 의상 드레이핑 · 바디 캡슐 충돌 · 주름/변형률 스펙트럼
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HairCloth.NeuralHairClothShaderIntegrationGuard()); // v16.83.0 Marschner/KK 헤어 · 패브릭 셰이딩 · 딥 오패시티 · 에너지 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HairCloth.NeuralHairClothStudioRunner(sp)); // v16.83.0 헤어 & 의상 다이내믹스 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Atmosphere.AtmosphericScatteringLightingEngine()); // v16.84.0 Rayleigh/Mie 대기 산란 · 투과율 LUT · SIMD 하늘 광원 · 안개/가시거리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Atmosphere.DynamicMicroWeatherSimulatorEngine()); // v16.84.0 미세 기상(강우/눈 입자 · 회전 돌풍 · 운량 · 번개) · 미세 기후
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Atmosphere.EnvironmentalBiomeWetnessGuard()); // v16.84.0 지형 바이옴 습윤/적설 물 수지 · Lagarde 재질 오버레이 · 3DGS/캐릭터
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Atmosphere.GenerativeEnvironmentStudioRunner(sp)); // v16.84.0 제너레이티브 환경 & 미세 기상 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinematography.NeuralCameraTrajectoryEngine()); // v16.85.0 6DoF 카메라 궤적 최적화(장애물/지형/가시선) · 자동 줌 · 랙 포커스
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinematography.GenerativeShotFramingEvaluator()); // v16.85.0 삼등분/황금비/헤드룸/리드룸 구도 미학 점수 · 샷 타입 분류
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinematography.AnamorphicLensBokehSynthesizer()); // v16.85.0 아나모픽 타원 보케 · 스트릭 플레어 · 왜곡/색수차(3DGS · 경로 추적)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Cinematography.NeuralCinematographyStudioRunner(sp)); // v16.85.0 신경망 시네마토그래피 & 구도 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FilmScore.NeuralCinematographicFilmScoreGenerator()); // v16.86.0 감정선/컷 적응 오케스트라 BGM 실시간 편곡 · SIMD 보이스 렌더
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FilmScore.AutonomousNeuralFoleySfxSynthesizer()); // v16.86.0 발걸음/의상 마찰/VFX 폭발·충돌 폴리 · 샘플 정확 동기 · 음속 지연
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FilmScore.HarmonicMixingAndAudioMasteringGuard(new PromptFlowStudio.Services.SpatialAudio.NeuralBinauralHrtfSpatializer())); // v16.86.0 R128/A85 라우드니스 · 트루 피크 · 대사 더킹 · 7.1.4 채널 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FilmScore.FilmScoreStudioRunner(sp)); // v16.86.0 영화 음악 & 사운드 디자인 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VideoDiffusion.RealtimeLatentVideoDiffusionEngine()); // v16.87.0 라텐트 CFG-DDIM 디노이징 · SIMD · 플로 워핑 노이즈 · 디코더
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VideoDiffusion.NeuralTemporalConsistencyGuard()); // v16.87.0 블록 옵티컬 플로 · 전후방 가림 · 디플리커 시간 블렌드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VideoDiffusion.ControlNetSpatialConditioningSynthesizer()); // v16.87.0 ControlNet 깊이(지형)/포즈(모캡)/카메라 컨디셔닝
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VideoDiffusion.LatentVideoDiffusionStudioRunner(sp)); // v16.87.0 비디오 디퓨전 & 시공간 일관성 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Continuity.RealtimeScriptSupervisorContinuityEngine()); // v16.88.0 샷 연속성 검증(소품/조명/의상/상처/포즈/180° 축) · SIMD · 비동기 1슬롯
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Continuity.NeuralSceneCoherenceStoryBibleMatcher()); // v16.88.0 스토리 바이블 파서 · 캐릭터/장소/시대/타임라인/태양 정합성
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Continuity.AutomaticContinuityCorrectionGuard()); // v16.88.0 USD/3DGS/룩/의상/마크/포즈/VFX 자동 보정 · 수동 검토 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Continuity.ScriptContinuityStudioRunner(sp)); // v16.88.0 스크립트 연속성 & 씬 정합성 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.Dubbing.NeuralVoiceCloningDubbingEngine()); // v16.89.0 음색 복제 · 운율 전이 · 13개 언어 소스-필터 더빙 · 등시성 · SIMD
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.Dubbing.GenerativeNeuralLipSyncSynthesizer()); // v16.89.0 음소→비짐 공동조음 · 근육 역동역학 피드포워드 · 동기 계측/보정 · 오디오 역추출
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.Dubbing.CulturalLocalizationSubtitleGuard()); // v16.89.0 관용구/유머/경어/민감 표현 적응 · CPS · 구도 기반 자막 배치
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Localization.Dubbing.NeuralLipSyncDubbingStudioRunner(sp)); // v16.89.0 글로벌 로컬라이제이션 & 신경망 립싱크 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ContentSecurity.NeuralInvisibleWatermarkEncoderEngine()); // v16.90.0 DCT QIM 비디오 · 차분 위상 오디오 · 3DGS 워터마크(키 = PROMPTFLOW_WM_KEY 환경변수)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ContentSecurity.DeepfakeProtectionContentAuthenticityGuard(sp.GetRequiredService<PromptFlowStudio.Services.ContentSecurity.NeuralInvisibleWatermarkEncoderEngine>())); // v16.90.0 봉인 지문 HMAC-SHA512 · 재압축/국소 위변조 판정 · 역추적
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ContentSecurity.EnterpriseDigitalRightsMonetizationSynthesizer()); // v16.90.0 계약 DSL · 워터폴 정산 · 해시 체인 원장 · 유출 디바이스 차단
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ContentSecurity.ContentSecurityStudioRunner(sp)); // v16.90.0 콘텐츠 보안 & 디지털 저작권 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ProductionCrew.AutonomousDirectorialConsensusEvaluator()); // v16.91.0 감정 목표 함수 합의 · 역할 권한 · Hedge 신뢰 가중 · 박자 정렬 · 오버라이드
        builder.Services.AddSingleton(sp => { var g = new PromptFlowStudio.Services.ProductionCrew.RealtimeProductionSwarmExecutionGuard(); g.RegisterStudios(sp); return g; }); // v16.91.0 v16.71~90 스튜디오 20종 프레임/메모리 예산 · 선점/거부/격리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ProductionCrew.MultiAgentVirtualProductionCrewOrchestratorEngine(sp.GetRequiredService<PromptFlowStudio.Services.ProductionCrew.AutonomousDirectorialConsensusEvaluator>(), sp.GetRequiredService<PromptFlowStudio.Services.ProductionCrew.RealtimeProductionSwarmExecutionGuard>())); // v16.91.0 크루 7 메시지 버스 · 제안 정책 · 작업 DAG 분배
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ProductionCrew.MultiAgentCrewStudioRunner(sp)); // v16.91.0 멀티에이전트 프로덕션 크루 스튜디오 루프
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CloudVerification.CloudSourceCodeIntegrityVerifierEngine()); // v16.92.0 소스 구문 정합성 & Zero-Alloc 정적/동적 검증
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CloudVerification.RealtimeUiExceptionSelfHealingGuard()); // v16.92.0 UI 예외 탐지 · 스냅샷 롤백 · 진단 매니페스트
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CloudVerification.FullUiInteractiveMenuAutomatedDriver(sp.GetRequiredService<PromptFlowStudio.Services.CloudVerification.RealtimeUiExceptionSelfHealingGuard>())); // v16.92.0 UI 가상 순회 드라이버
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CloudVerification.CloudVerificationStudioRunner(sp)); // v16.92.0 클라우드 소스 검증 & UI 인터랙티브 검사 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SelfFix.RealUiSelfHealingStudioRunner(sp)); // v16.93.0 실 RenderTree DOM 구동 · 소스 자동 수정 · 폐루프 회귀 검증
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveTelemetry.RealtimeTelemetryBottleneckProfilerEngine()); // v16.94.0 µs 병목 프로파일러 · GC/CPU/스레드 풀/렌더 스태킹
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveTelemetry.AutonomousPerformanceSelfTuningSynthesizer(sp.GetRequiredService<PromptFlowStudio.Services.LiveTelemetry.RealtimeTelemetryBottleneckProfilerEngine>(), sp.GetService<PromptFlowStudio.Services.ProductionCrew.RealtimeProductionSwarmExecutionGuard>())); // v16.94.0 버퍼/SIMD/스레드 풀 셀프 튜닝
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveTelemetry.HybridCloudEdgeDeploymentRecoveryGuard()); // v16.94.0 델타 OTA · 체크포인트 롤백
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveTelemetry.LiveTelemetryStudioRunner(sp)); // v16.94.0 실시간 텔레메트리 & 자율 튜닝 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.EnterpriseSla.ThreadDeadlockAutoProbingEngine()); // v16.95.0 멈춤/무한 루프/락 교착/기아/메모리 자율 감지
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.EnterpriseSla.ZeroDowntimeProcessFailoverSynthesizer()); // v16.95.0 서킷 브레이커 · LKG 핫스왑 페일오버
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.EnterpriseSla.EnterpriseSlaAvailabilityGuard(sp.GetRequiredService<PromptFlowStudio.Services.EnterpriseSla.ThreadDeadlockAutoProbingEngine>(), sp.GetRequiredService<PromptFlowStudio.Services.EnterpriseSla.ZeroDowntimeProcessFailoverSynthesizer>())); // v16.95.0 99.999% SLA 자율 복구 · 해시 체인 매니페스트
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.EnterpriseSla.EnterpriseSlaStudioRunner(sp)); // v16.95.0 엔터프라이즈 SLA & 무중단 복구 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GpuSwarm.DistributedHeterogeneousGpuTopologyEngine()); // v16.96.0 이기종 GPU 토폴로지 · VRAM/처리율 SIMD 프로빙
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GpuSwarm.DynamicCloudRenderSwarmLoadBalancer(sp.GetRequiredService<PromptFlowStudio.Services.GpuSwarm.DistributedHeterogeneousGpuTopologyEngine>())); // v16.96.0 적응 타일/프레임 LPT 로드밸런서
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GpuSwarm.LowLatencyFrameAssemblyFallbackGuard(sp.GetRequiredService<PromptFlowStudio.Services.GpuSwarm.DistributedHeterogeneousGpuTopologyEngine>(), sp.GetRequiredService<PromptFlowStudio.Services.GpuSwarm.DynamicCloudRenderSwarmLoadBalancer>())); // v16.96.0 제로카피 조립 · 로컬 페일백
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GpuSwarm.GpuClusterSwarmStudioRunner(sp)); // v16.96.0 분산 GPU 클러스터 & 렌더 스웜 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialXr.OpenXrWebXrSpatialTrackingEngine()); // v16.97.0 OpenXR/WebXR 6DoF 예측 · 양안 뷰/투영
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialXr.FoveatedCompressionReprojectionSynthesizer(PromptFlowStudio.Services.SpatialXr.SpatialComputingStudioRunner.EyeW, PromptFlowStudio.Services.SpatialXr.SpatialComputingStudioRunner.EyeH)); // v16.97.0 시선 포비에이티드 압축 · ATW/PTW 재투영
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialXr.SpatialHandGestureHapticGuard()); // v16.97.0 26관절 제스처 · 소품 충돌 · 햅틱 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialXr.SpatialComputingStudioRunner(sp)); // v16.97.0 공간 컴퓨팅 & WebXR 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialSlam.NeuralSpatialSlamTrackingEngine(PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.W, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.H, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.F, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.F, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.Cx, PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner.Cy)); // v16.98.0 6DoF SLAM · 퇴화 인지 ICP · 루프 폐쇄
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialSlam.LidarPointCloudMeshReconstructionSynthesizer()); // v16.98.0 TSDF 융합 · Surface Nets 메쉬 · OpenUSD/3DGS 변환
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialSlam.SpatialPlaneDetectionAnchorGuard()); // v16.98.0 RANSAC 평면 분할 · 부양/관통 앵커 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialSlam.SpatialSlamStudioRunner(sp)); // v16.98.0 신경망 공간 SLAM & 3D 환경 재구성 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwinWorld.RealtimeDigitalTwinSpatialSyncEngine()); // v16.99.0 실물↔트윈 6DoF 양방향 동기화 · Horn 캘리브레이션
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwinWorld.UnifiedMultiPhysicsWorldDynamicsSynthesizer()); // v16.99.0 강체 · 천(v16.83) · 유체(v16.81) · 기상(v16.84) 통합 물리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DigitalTwinWorld.DigitalTwinWorldStudioRunner(sp)); // v16.99.0 디지털 트윈 & 옴니버스 월드 스튜디오(라이브 USD 동기화 · 앵커 락 내장)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HoloTelepresence.HolographicLightFieldTelepresenceEngine()); // v17.00.0 45 시점 라이트필드 SIMD 슬라이싱 · 볼류메트릭 6DoF 텔레프레전스 코덱
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HoloTelepresence.UniversalAgiCinematicDirectorSynthesizer()); // v17.00.0 관객 감응 서사 분기 · 카메라/조명/BGM/표정 오케스트레이션
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HoloTelepresence.SpatialHapticCognitiveInteractionGuard()); // v17.00.0 햅틱 역각/진동 · 관통 차단 · 사이버 멀미 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HoloTelepresence.HolographicTelepresenceStudioRunner(sp)); // v17.00.0 홀로그래픽 텔레프레전스 & AGI 디렉터 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.RoboticMoCo.RoboticMoCoInverseKinematicsEngine()); // v17.01.0 6축 해석 IK(8해 최근접) · SIMD 저크 제한 프로파일 · 토크 부하 · 패스 사전 검증
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.RoboticMoCo.PhysicalStageDmxActuatorSynthesizer()); // v17.01.0 DMX512 4 유니버스 · Art-Net/ArtSync · 조명/기상/VFX → 무대 액추에이터
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.RoboticMoCo.HardwareInTheLoopSafetyEStopGuard()); // v17.01.0 SSM 분리 감시 · SLAM 평면/침범 불가 구역 · 동기 정지 · 이중 릴레이 E-Stop
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.RoboticMoCo.RoboticMoCoStudioRunner(sp)); // v17.01.0 로보틱 MoCo & 물리 스테이지 HIL 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualArena.DeterministicRollbackNetcodeEngine()); // v17.03.0 GGPO형 결정론 롤백(Q16 정수 물리 · 64 링 스냅샷 · FNV 체크섬 · 예측 12 · 입력 지연)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualArena.MassiveSpatialInterestManagementSynthesizer()); // v17.03.0 AOI 공간 해시(64×64) · FoV 원뿔 셀 컬링 · 우선순위 누적 · 시청자 바이트 예산
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualArena.ClientSpatialPredictionClockSyncGuard()); // v17.03.0 NTP 최소 RTT + PI 클록 드리프트 보정 · 에르미트 보간 · 데드 레커닝 · 오차 수렴
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualArena.VirtualArenaNetcodeStudioRunner(sp)); // v17.03.0 가상 아레나 & 롤백 넷코드 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MassiveCrowd.MassiveAgenticCrowdSimulationEngine()); // v17.04.0 10만 군중(공간 해시 · RVO2 ORCA · 감정 전파 SIMD · 밀도장 플로킹)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MassiveCrowd.SwarmStadiumAcousticsSynthesizer()); // v17.04.0 16 섹터 함성/박수/챈트/야유/파도 · 음압 모델 · v16.76 HRTF 공간화(전용 인스턴스)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MassiveCrowd.BehavioralLodSpatialCullingGuard()); // v17.04.0 FoV/거리 행동 LOD(풀 본·ORCA / 플로킹 / 밀도장) · 16.6 ms 예산 피드백
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MassiveCrowd.MassiveCrowdStudioRunner(sp)); // v17.04.0 대규모 군중 시뮬레이션 & 음향 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MasterControl.RealtimeBroadcastVisionSwitcherEngine()); // v17.05.0 16 입력 비전 스위처(핀 메모리 맵 · 컷/디졸브/와이프 SIMD · T-바 · 멀티뷰어 · AFV)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MasterControl.AutonomousLiveInstantReplaySynthesizer()); // v17.05.0 ISO 링버퍼 자동 하이라이트 클립 · 움직임 보상 보간 120 fps 슬로모션 · 송출 큐
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MasterControl.OnAirBroadcastSignalFailSafeGuard()); // v17.05.0 ST 2110 -20/-30/-40 · ST 2022-7 무결절 병합 · PTP 서보 · 결손/블랙 비상 컷
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MasterControl.BroadcastMasterControlStudioRunner(sp)); // v17.05.0 방송 비전 스위처 & 마스터 컨트롤 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ArGraphics.FreeDTrackingProtocolLensDistortionEngine()); // v17.06.0 FreeD D1 트래킹(체크섬 · SIMD 역직렬화) · 렌즈 표 FOV/K1/K2 · ST 맵 · 지연 정합 · 캘리브레이션
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ArGraphics.OnAirArTelestrationSportsTelemetrySynthesizer()); // v17.06.0 지면 고정 AR 텔레스트레이션(링 · 궤적 · 거리 · 화살표 · 오프사이드) · 선수 가림 · 스포츠 HUD
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ArGraphics.AncillaryDataKeyFillGraphicsCompositingGuard()); // v17.06.0 Key/Fill 프리멀티/스트레이트 가드 · 합법 레벨 · 타이틀 세이프 · ST 2110-40 ANC(CDP · ATC · FreeD)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ArGraphics.BroadcastArGraphicsStudioRunner(sp)); // v17.06.0 방송 AR 그래픽 & 텔레스트레이션 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Deflicker.SubPixelOpticalFlowTexturePinningEngine()); // v17.07.0 3단 피라미드 서브픽셀 LK 흐름 · 신뢰도 · 앵커 텍스처 좌표 트랙 · SIMD 피닝
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Deflicker.HighFrequencyDetailBlindDeflickerSynthesizer()); // v17.07.0 라플라시안 조명/질감 분리 · 질감 층 디플리커 · 구조 보호 · 가림 무효화
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Deflicker.GenerativeMorphingSuppressionGuard()); // v17.07.0 챔퍼 윤곽 거리 타일 모핑 감지 · 영역 억제/프레임 롤백
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Deflicker.TemporalDeflickerStudioRunner(sp)); // v17.07.0 시공간 디플리커 & 텍스처 피닝 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticMatting.RealtimeSemanticAlphaMattingEngine()); // v17.08.0 마커리스 시맨틱 트라이맵 · SIMD α 사영/전경 언믹스 · 반투명 회귀
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticMatting.GenerativeVirtualSetZDepthSynthesizer()); // v17.08.0 OpenUSD 가상 세트 · SLAM 바닥 앵커 · Z-Depth 오클루전
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticMatting.DynamicEnvironmentRelightingGuard()); // v17.08.0 HDRI SH9 리라이팅 · 림/스페큘러 · 라이트 랩 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticMatting.SemanticMattingStudioRunner(sp)); // v17.08.0 시맨틱 매팅 & 가상 세트 융합 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LedVolume.InnerFrustumOffAxisProjectionEngine()); // v17.09.0 FreeD 예측 · 왜곡 역보정 내부 프러스텀 · Kooima SIMD
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LedVolume.LedWallMoireSuppressionSynthesizer()); // v17.09.0 OLPF/착란원 모아레 억제 · 외부 프러스텀 환경 맵
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LedVolume.PtpGenlockPhaseSyncTearingGuard()); // v17.09.0 PTP/SMPTE 2059 젠록 · 티어링/블랙 밴드 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LedVolume.IcvfxLedWallStudioRunner(sp)); // v17.09.0 인카메라 VFX LED 월 & 젠록 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ColorScience.RealtimeAcesColorTransformEngine()); // v17.10.0 ACES 2.0 색공간 SIMD 변환 · 사면체 3D LUT 33/65
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ColorScience.OnSetLiveDitGradingColorDecisionSynthesizer()); // v17.10.0 ASC CDL 3-Way 그레이딩 · 퍼레이드/벡터스코프/False Color
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ColorScience.LedSpectralMetamerismGamutClippingGuard()); // v17.10.0 LED 메타메리즘 분광 역산 · ACES RGC 색역 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ColorScience.LiveDitColorStudioRunner(sp)); // v17.10.0 라이브 DIT 컬러 사이언스 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MarkerlessMocap.MarkerlessMultiViewPoseTriangulationEngine()); // v17.11.0 멀티뷰 에피폴라 게이트 + SIMD DLT 3D 관절 삼각측량
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MarkerlessMocap.BiomechanicalSkeletonRetargetingSynthesizer()); // v17.11.0 디지털 휴먼/OpenUSD 리그 리타게팅 · 발 고정 FABRIK
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MarkerlessMocap.KinematicJointLimitBonePenetrationGuard()); // v17.11.0 튐/떨림 필터 · ROM 락 · 자가 관통 차단
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MarkerlessMocap.MarkerlessMocapStudioRunner(sp)); // v17.11.0 AI 무마커 모션 캡처 & 리타게팅 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FacialPerformance.NeuralFacialLandmarkBlendshapeEngine()); // v17.12.0 468 랜드마크 → 52 ARKit 블렌드셰이프 SIMD 역산
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FacialPerformance.AudioToFaceVisemeMotionSynthesizer()); // v17.12.0 Audio2Face FFT/MFCC/LPC 비짐 립싱크
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FacialPerformance.BiologicalEyeGazeMicroTremorGuard()); // v17.12.0 생체 시선 · 미세 홱보기 · 무의식 깜빡임 가드
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FacialPerformance.FacialPerformanceStudioRunner(sp)); // v17.12.0 AI 페이셜 퍼포먼스 & Audio2Face 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousHand.DexterousFingerPoseTriangulationEngine()); // v17.13.0 42 키포인트 SIMD DLT 삼각측량 · 가림 뼈 길이 복원
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousHand.VirtualPropContactGraspingSynthesizer()); // v17.13.0 소품 SDF 접촉 매니폴드 · 관통 락 · 파지 부착
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousHand.InterDigitalSelfCollisionDislocationGuard()); // v17.13.0 ROM 클램프 · 탈구 복귀 · 지간 충돌 분리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousHand.DexterousHandStudioRunner(sp)); // v17.13.0 AI 덱스터러스 핸드 & 소품 접촉 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.AnatomicalTissue.NeuralAnatomicalMuscleFasciaEngine()); // v17.14.0 근육 팽창 · 근막 슬라이딩 SIMD 피부 변형
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.AnatomicalTissue.AnatomicalTissueStudioRunner(sp)); // v17.14.0 AI 해부학 근육 & 연조직 스튜디오(지글/주름 합성기 · 체적/교차 가드 내장)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.StrandHair.StrandHairPbdDynamicsEngine()); // v17.15.0 가이드 256 × 자식 400 스트랜드 SIMD PBD
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.StrandHair.HairGroomingStudioRunner(sp)); // v17.15.0 AI 헤어 동역학 & 뉴럴 그루밍 스튜디오(Marschner/기류 합성기 · 두피 앵커/SDF 가드 내장)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Garment.GarmentDynamicsStudioRunner(sp)); // v17.16.0 AI 다층 의상 동역학 & 드레이핑 스튜디오(전용 근육 피부 · 다층 PBD · 직물 합성기 · 레이어/헤어 가드 내장)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SkinOptics.SkinSssHemodynamicsStudioRunner(sp)); // v17.17.0 AI 피부 SSS & 미세 혈류 스튜디오(분리형 SSS · 혈류 홍조 · 각막/눈물막 가드 · v17.12 페이셜 결선)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralVoice.NeuralVoiceStudioRunner(sp)); // v17.18.0 AI 뉴럴 음성 & 성도 음향 스튜디오(감정 운율 · 켈리-록바움 성도 · A/V 지연/파열음 가드 내장)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.FluidWetness.FluidWetnessStudioRunner(sp)); // v17.19.0 AI 다상 유체 습윤 & 액적 스튜디오(피막 확산 · 직물 위킹 · 액적 활주 · 젖은 모발/의상 흡착 가드 내장)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GaussianAvatar.GaussianAvatarStudioRunner(sp)); // v17.20.0 AI 4D 가우시안 아바타 스튜디오(DQS 가우시안 스키닝 · SH 차수 3 재조명 · 16 비트 래딕스 정렬 타일 스플래팅 가드 내장)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.StereoXr.SpatialComputingXrStudioRunner(sp)); // v17.22.0 AI 공간 컴퓨팅 & WebXR 스트리밍 스튜디오(양안 포비티드 VRS · WebRTC RTP/L16/데이터 채널 · M2P ATW 가드)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialAcoustics.SpatialAcousticsStudioRunner(sp)); // v17.23.0 AI 6DoF 공간 음향 & 광선 추적 스튜디오(BVH 음향 광선 추적 · 재질 흡음 RT60 · 6DoF HRTF 바이노럴 · 도플러/회절 위상 가드)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.CardNewsReelsGenerator>();     // v17.30.1 카드뉴스 릴스(ffmpeg 부재 시 HTML 템플릿 폴백)
        builder.Services.AddSingleton(PromptFlowStudio.Services.SocialMedia.InstagramReelsService.Create); // v17.30.1 IG Reels 발행(URL 경로 · 로컬 브릿지 미탑재 시 Graceful)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.AudioTelemetry.RealtimeAudioTelemetryMonitor(sp)); // v17.29.0 실시간 오디오/텔레메트리 모니터 뼈대
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CinematicDirector.CinematicDirectorStudioRunner(sp)); // v17.39.0 AI 시네마틱 감독 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.StageLighting.IntelligentStageLightingStudioRunner(sp)); // v17.41.0 AI 지능형 무대 조명 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Pyrotechnics.NeuralVfxPyrotechnicsStudioRunner(sp)); // v17.42.0 AI 뉴럴 특수효과 & 파이로 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LensOptics.CinematicLensOpticsStudioRunner(sp)); // v17.43.0 AI 시네마틱 렌즈 & 포스트 FX 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GenlockFrustum.GenlockFrustumLedWallStudioRunner(sp)); // v17.44.0 AI ICVFX LED 월 관제 스튜디오
        builder.Services.AddSingleton<PromptFlowStudio.Services.OpticalInertialTracking.CameraTrackingStudioRunner>(); // v17.45.0 AI 카메라 트래킹 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.BroadcastMcr.BroadcastMcrStudioRunner(sp));
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Showrunner.InteractiveShowrunnerStudioRunner(sp));
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.KineticBRoll.KineticTypographyBRollStudioRunner(sp)); // v17.78.0 키네틱 자막 & B-Roll
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveClipper.LiveToShortsAutoClipperStudioRunner(sp)); // v17.84.0 라이브 → 숏폼 오토 클리퍼(v17.83 송출 미디어 탭 · 120 s 무할당 H.264/AAC 롤링 링 + IDR/SPS/PPS 인덱스 · 1 s 채팅 가속/후원/RMS·고역/감정 급변 복합 스코어 ≥ 0.85 → −20 s/+15 s 직전 IDR 슬라이스) · BelowNormal 워커 격리 v17.77 9:16 세이프존 · v17.78 호르모지 키네틱 자막 · v17.76 임팩트 SFX → v17.79/v17.81 배포 큐 원클릭
        builder.Services.AddSingleton<PromptFlowStudio.Services.Ui.ShortsFactoryOneClickRenderOrchestrator>(); // v18.0.2 숏폼 팩토리 원클릭 풀체인 렌더(① 소스 링 실 슬라이스 → ② 키네틱 자막 → ③ SPS 줌 펀치 리먹스 · BGM −30 dB 덕킹 → ④ fMP4 · 썸네일 PNG) · 버퍼 첫 렌더 시 지연 할당
        builder.Services.AddSingleton<PromptFlowStudio.Services.Ui.ShortsToPublisherHandoffBridge>(); // v18.0.4 숏폼 팩토리 → 소셜 퍼블리셔 인메모리 핸드오프(fMP4 · 썸네일 PNG ReadOnlyMemory 공유 · 0-카피)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveArchive.LiveArchiveCloudVaultStudioRunner(sp)); // v17.87.0 라이브 클라우드 아카이브 볼트(v17.83 송출 탭 체인 → 5/15 분 IDR 정렬 fMP4 세그먼트 · GOP 조각 실시간 플러시/.part 복구) · 순수 SigV4 S3/R2/MinIO 8 MB 멀티파트 + 20 Mbps 셰이핑 · 10 GB 로컬 자동 회수 · 7/30/90 일 콜드 티어 태그
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.PostPublishSplitTest.PostPublishSplitTestStudioRunner(sp)); // v17.93.0 포스트-퍼블리시 A/B & 패자 회수기(v17.85 훅/썸네일 변형 분기 · 5 분 폴링 무할당 링 · 베타 승률 ≥ 95 % · 저성과 자동 비공개/삭제 감사 · v17.81 매트릭스 승자 증폭)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.TrendJacking.TrendJackingStudioRunner(sp)); // v17.96.0 실시간 트렌드재킹 & 해시태그 인젝터(Span 피드 파싱 · 분당 가속도 랭킹 · 대본/태그 연관도 0~100 · 골든 ≥ 80 · YT 100자 훅 + 3 태그 · IG 트렌드2/틈새2/메가1 · 무관 < 70 차단 · 4 시간 태그 쿨다운)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CompetitorSpy.CompetitorViralSpyStudioRunner(sp)); // v17.99.0 경쟁 채널 바이럴 스파이 & 역공학(Span 메트릭 스캔 · 중앙값×3 미만 평상 평균 대비 300 % 이상치 · 바이럴 이상치 지수 0~100 · 도입 3 초 훅 의문/반전/공포·경고/호기심 규칙 분류 · 휘도 컷 템포 · 100 ms 피크 분산 청사진 · 구조만 차용한 대항 기획안 · Levenshtein/Jaccard ≥ 40 % 즉시 재합성 → v17.90 오케스트레이터 ①단계 입력)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.RetentionHeatmap.AudienceRetentionHeatmapStudioRunner(sp)); // v17.98.0 시청자 유지율 히트맵 & 리에디터(초 단위 유지율 10 ms 1 s 창 미분 이탈 절벽 · 데드존 누수 · 절벽 0.3 s 전 v17.77 1.15× 줌 펀치 + v17.76 임팩트 FX · 데드에어 1.05~1.15× v17.82 WSOLA 템포 · 예상 상승 ≥ 1.5 %p 가드 → v17.93 A/B Variant-R / v17.81 매트릭스 이관)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ViralCommerce.ViralCommerceAffiliateStudioRunner(sp)); // v17.97.0 바이럴 커머스 & 제휴 어트리뷰션(Span 대본 엔티티 추출 · 연관도 × EPC 상위 3 상품 · subId/캠페인 결합 8 자리 Base62 pfs.link 숏링크 · 공정위/FTC 고지 최상단 주입 → v17.95 고정 댓글 · v17.94 설명란 · 봇 UA/동일 IP 초당 다중 클릭 차단 · 영상당 링크 3 상한 · 분 시계열 어트리뷰션)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GoldenHourEngagement.GoldenHourEngagementStudioRunner(sp)); // v17.95.0 골든아워 댓글 & 인게이지먼트 부스터(무할당 Span 감정 분류 · 링크 도배/욕설/트롤 자동 숨김 · 업로드 60 s 내 앵커 고정 댓글 · 톤 맞춤 답글 · 15~45 s 지터 · 시간당 30 상한 쿨다운)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.PlatformVisibility.PlatformVisibilityGatewayStudioRunner(sp)); // v17.94.0 플랫폼 비지빌리티 게이트웨이(IVideoVisibilityGateway 실체 · YouTube/IG 가시성 전환 · 토큰 5 분 전 갱신 · 429 지수 백오프 · 쿼터 90 % 안전 정지 · 5 분 시계열 유속 → v17.93 A/B 피딩)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MasterPipeline.MasterPipelineRealRunnerStudioRunner(sp)); // v17.92.0 마스터 실러너 E2E 샌드박스(v17.84→v17.85→v17.91→v17.82→v17.81→v17.88→v17.87 실 싱글톤 직결 · 풀 리스 무할당 컨텍스트 · 인터락 원자 롤백 · 헤드리스 배치 전용 스레드)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CopyrightCompliance.CopyrightComplianceStudioRunner(sp)); // v17.91.0 저작권 검역 & Content ID 가드(무할당 음향 지문 위험도 · 침해 구간 10 ms · 로열티 프리 BGM 치환 −14 LUFS · 브랜드 세이프티 금칙어 마스킹/비프)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.OmniViral.OmniViralGrowthStudioRunner(sp)); // v17.90.0 옴니 바이럴 마스터 콕핏(v17.84~v17.88 7 단계 원클릭 DAG · 단일 인메모리 컨텍스트 · 인터락 서킷브레이커 · 100 ms K-Factor 텔레메트리)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VodRepurposing.VodRepurposingStudioRunner(sp)); // v17.88.0 VOD 리퍼포징(v17.87 아카이브 길이 · 대본 → 3~7 분 챕터/3 줄 요약/SEO 5 · ArrayPool 마크다운 블로그/뉴스레터 · SDF 카드뉴스 5 장 + OG 배너 인메모리 PNG)
        builder.Services.AddSingleton<PromptFlowStudio.Services.Ui.IntentNavigationRunner>(); // v17.89.0 🧭 AI 인텐트 내비게이션(무할당 동의어 개념 검색 → 상위 3~5 스튜디오 추천 · 165 카탈로그 1:1 도움말 레지스트리 · 모달 헤더 ? 서랍)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveEngagement.LiveAudienceEngagementStudioRunner(sp)); // v17.86.0 라이브 시청자 인터랙션(v17.83 옴니 채팅 링 비파괴 Seq 열람 → !1~!4/!찬성/!반대 투표·퀴즈 1 인 1 표 · 슈퍼챗/비트 KRW → 후원 골 바 + 3 s 골드 베지에 스팅어) · 1080p 무할당 더티 영역 Porter-Duff 알파 오버레이 합성
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ViralExport.ViralPredictorShortsExportStudioRunner(sp)); // v17.85.0 바이럴 예측 스코어러 & 숏폼 MP4 익스포터(v17.84 클리퍼 완료본 → 4 축 바이럴 스코어 S/A/B/C · 고CTR 썸네일/훅 카피 · 무할당 인메모리 fMP4 인출)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveRestream.OmniLiveRestreamStudioRunner(sp)); // v17.83.0 옴니채널 라이브 RTMPS(C0/C1/C2 핸드셰이크 · connect/createStream/publish · Annex-B → FLV AVC/AAC 태그 · 4096 청크 1 회 직렬화 → YouTube/Twitch 제로-카피 분기 · 신규/드롭 타겟 IDR 게이트) · YouTube 폴링 JSON + Twitch IRC 통합 링 → 슈퍼챗 KRW 중요도 → v17.80 감정 + v17.68 제스처 · 100 ms 큐/RTT 정체 지수 → 6/4/2.5 Mbps IDR 정렬 ABR
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MultilingualDubbing.MultilingualDubbingStudioRunner(sp)); // v17.82.0 다국어 더빙(v17.64 번역 음소 → EN/ES/JA 성문 펄스·포먼트 합성 → v17.80 SOLA 원본 화자 음색 이식 → WSOLA 0.8~1.25× 원본 컷 정합) · 48 kHz 16.7 ms Goertzel 포먼트 → 14 비젬 → ARKit 입/턱 리타게팅 → v17.58 3DGS 얼굴 · A/V 위상 드리프트 µs 추적 15 ms 초과 시 보간 계수 가감속 1 프레임 복원
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MatrixSyndication.MatrixSyndicationStudioRunner(sp)); // v17.81.0 준법형 다계정 매트릭스(정규 OAuth2 소유 채널 Primary/Sub/Clip · 육안 구별 변형 1:1 매핑 · 45~120 분 시차 · v17.76 골든타임 + 채널별 쿼터 버킷) · 9:16/1:1 스마트 블러 패딩 · 0.5~1.0 s 범퍼 · 로고 워터마크 · v17.64 다국어 자막 번-인 · 30 분 도달 0/API 거절 → 해당 계정 동결 · v17.73 웹훅 · 관리자 승인/취소(우회 재라우팅 없음)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ViralVoiceFx.ViralVoiceFxStudioRunner(sp)); // v17.80.0 감정 보이스 SOLA 피치/포먼트 0.7~1.4× (v17.65 [excited]/[whisper]/[serious]/[funny] 태그 · 50 ms 글라이드 · v17.72 군중 감성 추종) → 바이럴 FX 랙(전화기 300~3.4k BPF+새츄 · 110 Hz 링 보코더 · T60 1.2 s 리버브 펀치 · f/2 서브하모닉 딥 베이스) → 1 ms 룩어헤드 4× 트루 피크 소프트 니 리미터(−1 dBFS · 클리핑 0)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LivePublish.ProductionLivePublisherStudioRunner(sp)); // v17.79.0 실제 SNS 라이브 배포(Google/Meta OAuth2 PKCE · AES-GCM 256 볼트 · 만료 10 분 전 논블로킹 갱신 · YouTube Resumable 256 KB~1 MB 청크 + 308/Range 재개 · IG 릴스 컨테이너→rupload→폴링→publish · 프리플라이트 9:16/30·60fps/100MB/44.1·48kHz · 비공개 드라이런 2 단계 커밋 롤백 · v17.73 웹훅 알림)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VisualHooks.KineticVisualHookStudioRunner(sp)); // v17.77.0 키네틱 비주얼 훅 & 세이프존
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SmartPublishing.SmartPublishingSchedulerStudioRunner(sp)); // v17.76.0 스마트 배포 캘린더 & SFX
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.RetentionOptimizer.ViralRetentionOptimizerStudioRunner(sp)); // v17.75.0 릴스 유지율 & A/B 최적화
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VisualPolish.ReelsVisualPolishStudioRunner(sp)); // v17.74.0 릴스 비주얼 폴리시 & 썸네일 팩토리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Autopilot.ZeroTouchAutopilotStudioRunner(sp)); // v17.73.0 제로-터치 자율 오토파일럿
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SemanticChat.SemanticChatWeatherStudioRunner(sp)); // v17.72.0 시맨틱 채팅 & 날씨 엔진
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Publisher.OmniChannelPublisherStudioRunner(sp)); // v17.71.0 AI 옴니채널 퍼블리셔 & QoL
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.StationMaster.MasterStationNetworkStudioRunner(sp)); // v17.70.0 24/7 방송국 마스터 편성 센터
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveToReels.LiveToReelsFactoryStudioRunner(sp)); // v17.69.0 라이브-투-릴스 자동 팩토리
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveShowroom.LiveCommerceStudioRunner(sp)); // v17.68.0 버추얼 라이브 커머스 스튜디오 // v17.67.0 AI 자율 쇼러너 & 인터랙티브 스튜디오 // v17.66.0 브로드캐스트 주조정실(MCR) 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Talkshow.MultiAvatarTalkshowStudioRunner(sp)); // v17.65.0 멀티 아바타 토크쇼 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GlobalDubbing.GlobalDubbingStudioRunner(sp)); // v17.64.0 실시간 다국어 더빙 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.StreamGateway.StreamGatewayClipperStudioRunner(sp)); // v17.62.0 스트림 게이트웨이 & 클리퍼 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.InfluencerLive.VirtualInfluencerLiveStudioRunner(sp)); // v17.61.0 버추얼 인플루언서 라이브 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.AvatarActing.AvatarActingDirectorStudioRunner(sp)); // v17.60.0 아바타 자율 연기 디렉터 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GaussianClothHair.GaussianClothHairStudioRunner(sp)); // v17.59.0 가우시안 클로스 & 헤어 피직스 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GaussianFaceHand.GaussianFacialHandStudioRunner(sp)); // v17.58.0 가우시안 페이셜 & 핑거 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SkinnedSplatAvatar.DualQuaternionGaussianAvatarStudioRunner(sp)); // v17.57.0 4D 가우시안 아바타 스튜디오(DQS)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SplatRelighting.GaussianSplatRelightingStudioRunner(sp)); // v17.56.0 3DGS 섀도우 & 릴라이팅 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SplatEnvironment.GaussianSplatEnvironmentStudioRunner(sp)); // v17.55.0 3D 가우시안 스플래팅 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.RhythmAction.RhythmActionMvStudioRunner(sp)); // v17.54.0 MV 리듬 액션 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.BeatSync.BeatSyncAudioMixStudioRunner(sp)); // v17.53.0 비트 동기화 & 오디오 더킹 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MotionTypography.MotionOverlayStudioRunner(sp)); // v17.52.0 9:16 모션 타이포그래피 스튜디오(모바일: 대체 글리프)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.OmniChannel.OmniChannelEngagementStudioRunner(sp)); // v17.51.0 옴니채널 크로스 배포 & 댓글 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ViralLoop.ViralLoopAutomationStudioRunner(sp)); // v17.50.0 AI 24/7 바이럴 오토메이션 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VerticalTranscode.VerticalTranscoderStudioRunner(sp)); // v17.49.0 AI 9:16 하드웨어 트랜스코더 스튜디오(모바일: 관리 코드 폴백)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.GraphBridge.GraphApiBridgeStudioRunner(sp)); // v17.48.0 AI 인스타그램 API 연동 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ViralReels.ViralReelsStudioRunner(sp)); // v17.47.0 AI 릴스 바이럴 리믹스 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SetExtension.VirtualSetExtensionStudioRunner(sp)); // v17.46.0 AI 세트 확장 & 크로마 블렌딩 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.NarrativeTimeline.NarrativeTimelineStudioRunner(sp)); // v17.40.0 AI 각본 내러티브 타임라인 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ProceduralFoley.ProceduralFoleyStudioRunner(sp)); // v17.38.0 AI 절차적 물리 폴리 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralRagdoll.NeuralRagdollStudioRunner(sp)); // v17.37.0 AI 신경망 래그돌 & 충격 복원 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.DexterousManipulation.DexterousManipulationStudioRunner(sp)); // v17.36.0 AI 양손 정밀 조작 & 소품 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ContactLocomotion.ContactLocomotionStudioRunner(sp)); // v17.35.0 AI 접촉 로코모션 & 계단 보행 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VisualQa.HeadlessVisualQaStudioRunner(sp)); // v17.34.0 AI 헤드리스 시각 QA & 디버깅 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.MotionMatching.NeuralMotionMatchingStudioRunner(sp)); // v17.33.0 AI 뉴럴 모션 매칭 & 생체역학 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.LiveDubbing.MultilingualDubbingLipSyncStudioRunner(sp)); // v17.32.0 AI 다국어 더빙 & 립싱크 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.VirtualLive.VirtualLiveBroadcastStudioRunner(sp)); // v17.31.0 AI 버추얼 라이브 방송 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.Microclimate.AtmosphericWeatherStudioRunner(sp)); // v17.28.0 AI 대기 기상 & 미기후 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.WorldExpansion.GenerativeWorldStudioRunner(sp)); // v17.27.0 AI 생성형 월드 확장 & 4D 인페인팅 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.ContinuumPhysics.ContinuumPhysicsStudioRunner(sp)); // v17.26.0 AI 실시간 뉴럴 물리 & 연속체 역학 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.HolographicTelepresence.NeuralTelepresenceStudioRunner(sp)); // v17.25.0 AI 홀로그래픽 텔레프레전스 스튜디오
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.SpatialHaptics.SpatialHapticsStudioRunner(sp)); // v17.24.0 AI 6DoF 공간 햅틱 & 역각 피드백 스튜디오(1 kHz 갓-오브젝트 켈빈-보이트 · 진동 촉각 텍스처/맥동 합성 · 듀얼 레이트 POPC 버징 가드)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.CognitiveCinematography.CognitiveCinematographyStudioRunner(sp)); // v17.21.0 AI 시네마토그래피 & 카메라 디렉터 스튜디오(인지 프레이밍 · 레이 마칭 갓레이 · 육각 보케 · 포커스 풀링/가림 가드)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralCodec.NeuralVolumetricVideoCodecEngine()); // v17.02.0 볼류메트릭 코덱(움직임 보상 · SIMD 잔차 양자화 · 3DGS 스플랫 시공간 잔차 · 학습 R-Q 레이트 제어)
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralCodec.GlobalEdgeCdnRoutingSynthesizer()); // v17.02.0 오리진/에지/P2P 워터필링 라우팅 · RTT/부하 비용 SIMD · ABR 래더
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralCodec.PacketLossConcealmentFecGuard()); // v17.02.0 리드-솔로몬 FEC(SSSE3 GF) · 인터리빙 · 지터 버퍼 · 움직임 외삽 은닉 · PLI
        builder.Services.AddSingleton(sp => new PromptFlowStudio.Services.NeuralCodec.NeuralCodecEdgeCdnStudioRunner(sp)); // v17.02.0 신경망 코덱 & 글로벌 에지 CDN 스튜디오
        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.AutomatedVideoQaInspector>();   // v16.17.0 자동 QA 검수기(직접 게시 브릿지는 데스크톱 전용 — IYouTubePublishService 미등록)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Storyboard.QueueResourceGovernor>();   // v16.16.0 쿼터/자원 거버너(대량 생산 큐·허브는 데스크톱 전용 — StoryboardJobManager 미등록)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Telemetry.ProductionCostTrackerService>();   // v16.18.0 비용 원장/예산(HUD 카드 공유 · 대시보드 페이지는 데스크톱 전용)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Telemetry.SystemHardwareTelemetryMonitor>(); // v16.18.0 RAM/CPU 텔레메트리(ResourceMonitor·JobManager 미등록 → GPU/컷 측정 생략)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Media.ILocalMediaService, PromptFlowStudio.Services.Media.LocalMediaService>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Ui.SidebarGuideService>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Wan.WanTokenVault>();   // v15.11.0 다중 클라우드 Key Vault(데스크톱과 동일)

        builder.Services.AddSingleton<PromptFlowStudio.Services.Wan.NodeWorkerService>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Wan.DistributedRenderGrid>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.WebhookDispatcher>();

        builder.Services.AddSingleton<PromptFlowStudio.Services.Integration.WebRtcStreamer>();



        // ── v5.3 플랫폼 서비스 MAUI 네이티브 구현 등록(데스크톱 WinForms 구현 대체 · net8.0-android DI 정합) ──

        builder.Services.AddSingleton<PromptFlowStudio.Services.Platform.IFilePickerService, MauiFilePickerService>();      // FilePicker

        builder.Services.AddSingleton<PromptFlowStudio.Services.Platform.IClipboardService, MauiClipboardService>();        // Clipboard

        builder.Services.AddSingleton<PromptFlowStudio.Services.Platform.IVideoExportService, MauiVideoExportService>();    // Share Sheet

        builder.Services.AddSingleton<PromptFlowStudio.Services.Platform.IAutoUpdateService, MauiAutoUpdateService>();      // 스토어 위임



        // ── 기존 v1.0~v3.8 백엔드 서비스 재등록(로직 무변경) ──

        //   ⚠ 데스크톱 MainForm 의 DI 목록과 동일하게 유지(공용 헬퍼로 추출 권장).

        //   예) VideoFormatFactory, BabyThemeDatabase, AlibabaWanHybridEngine, EmotionalVoiceSynthesizer,

        //       SpatialAudioEngine, DigitalKeepsakeCardService, StudioMemoryAudit, ILocalMediaService 등.

        MobileServiceRegistry.RegisterSharedServices(builder.Services);



        var mauiApp = builder.Build();

        _ = mauiApp.Services.GetService(typeof(TelemetryPipelineWiring));   // v5.2.1: 즉시 생성 → 비전→큐 결선 + 업로더/백엔드 바인딩(OfflineSyncPipeline 동반 생성)

        return mauiApp;

    }

}

