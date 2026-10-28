# Current Project Status
- Active Milestone: 【Step 2】 스마트 카메라 실전 파이프라인 — 캡처 롤 → 모션 연출 → 작업실 I2V 원클릭
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 646/646 PASS(직렬 · MaxCpuCount=1 · DisableParallelization) · Camera 20/20
- Updated: 2026-10-28 (KST)

## Completed
- Step2: 캡처 롤(`RecentCaptures` 최신순 5장 롤오버 · 활성 프레임 선택 · 해제 후 유지) · 16:9/9:16/1:1 세이프존 가이드 · 모션 강도(Subtle/Normal/Dynamic)·속도(0.5/1.0/1.5x) + 활성 프레임 배경 3D 궤적 프리뷰 · `SmartCameraMerge.MergeCameraPrompt`(기존 카메라 지시어 감지·교체·중복 제거·멱등) · `TransferToWorkspace` → 작업실 `ApplyCameraTransferAsync`(_uploads 저장·I2V 자동 활성화·HUD 펄스·토스트)
- Step1: `CameraStudio.razor`(/camera-studio · 📡/🕹️ 탭 · 레거시 /wireless-camera·/motion·/motion-camera → ?tab= replace 포워딩 · 캡처→I2V 참조+모션 조합→드래프트 원클릭 전송) · 사이드바/가이드/팔레트/도움말 단일화 · `CameraStudioTests`
- QA: `scripts/CaptureAllScreens.ps1`(DPI PerMonitorV2 · WebView2 CDP 1920x1080@1x · 9라우트 순회 → `artifacts/screenshots/01~09`) · h1 포커스 아웃라인 제거 · Win10 미지원 이모지(🪄🪝⏻) 치환 · 라이브 placeholder `&#10;` 노출 · 갤러리 카드 액션 정렬 · 숏폼 패널 폭 정렬
- UI: 전역 커스텀 스크롤바(테마 토큰) · 사이드바/모바일 드로어 인디고 액센트 바+그라데이션 · 영감 모달 탭 글로우·카드 부유·빈 상태 · 숏폼/카메라 HUD 배지·세이프존 시안/마젠타 · 렌더 큐 엔진 뱃지 색 구분·실패 툴팁
- P3: `NetworkCameraStreamReceiver`(HTTP 스냅샷/MJPEG 3초 타임아웃 · ffmpeg RTSP/RTMP/SRT 1프레임 · 실패/WebRTC→`MockStreamReceiver` 폴백) · `WirelessCameraService` 수신기 위임·`CaptureFrameAsync`·실측 해상도/MIME DataUrl · DI 양쪽
- P2: 스토리보드 컷 덱 ↔ `_project.Scenes` 양방향 동기화 · `RenderQueue.razor` 실행 엔진 HUD·실패 배너·재큐잉
- P1: `IRenderExecutionEngine` · `VideoEngineRenderExecutionEngine`(ComfyUI/Cloud, 미가용 시 Mock) · 큐 진행률/산출 검증/실패 격리

## Known Issues
- 시뮬레이션 폴백은 실제 미디어 미생성 → `OutputPath` 지정 작업만 메타 기록
- `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 벽시계 초과 간헐 이력
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크(VM 미등록) · iOS 는 ffmpeg 불가 → 카메라 RTSP 폴백
- 로컬 ffmpeg.exe 부재(`-AllowMissingFfmpeg`) · Authenticode 미서명 · Android 빌드 미검증
