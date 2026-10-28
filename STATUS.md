# Current Project Status
- Active Milestone: 【Step 1】 무선 카메라 + 모션 카메라 → 🎥 스마트 카메라 & 모션 단일 스튜디오 통합
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 643/643 PASS(직렬 · MaxCpuCount=1 · DisableParallelization) · Camera 17/17
- Updated: 2026-10-28 (KST)

## Completed
- Step1: `CameraStudio.razor`(/camera-studio · 📡/🕹️ 탭 · 레거시 /wireless-camera·/motion·/motion-camera → ?tab= replace 포워딩 · 캡처→I2V 참조+모션 조합→드래프트 원클릭 전송) · 사이드바/가이드/팔레트/도움말 단일화 · `CameraStudioTests`
- QA: `scripts/CaptureAllScreens.ps1`(DPI PerMonitorV2 · WebView2 CDP 1920x1080@1x · 9라우트 순회 → `artifacts/screenshots/01~09`) · h1 포커스 아웃라인 제거 · Win10 미지원 이모지(🪄🪝⏻) 치환 · 라이브 placeholder `&#10;` 노출 · 갤러리 카드 액션 정렬 · 숏폼 패널 폭 정렬
- UI: 전역 커스텀 스크롤바(테마 토큰) · 사이드바/모바일 드로어 인디고 액센트 바+그라데이션 · 영감 모달 탭 글로우·카드 부유·빈 상태 · 숏폼/카메라 HUD 배지·세이프존 시안/마젠타 · 렌더 큐 엔진 뱃지 색 구분·실패 툴팁
- P3: `NetworkCameraStreamReceiver`(HTTP 스냅샷/MJPEG 3초 타임아웃 · ffmpeg RTSP/RTMP/SRT 1프레임 · 실패/WebRTC→`MockStreamReceiver` 폴백) · `WirelessCameraService` 수신기 위임·`CaptureFrameAsync`·실측 해상도/MIME DataUrl · DI 양쪽
- P2: 스토리보드 컷 덱 ↔ `_project.Scenes` 양방향 동기화 · `RenderQueue.razor` 실행 엔진 HUD·실패 배너·재큐잉
- P1: `IRenderExecutionEngine` · `VideoEngineRenderExecutionEngine`(ComfyUI/Cloud, 미가용 시 Mock) · 큐 진행률/산출 검증/실패 격리
- P0: Seed long 일원화 · 클라우드 `ToCloudSeed` 31비트 결정적 폴딩 · origin/main 정렬

## Known Issues
- 시뮬레이션 폴백은 실제 미디어 미생성 → `OutputPath` 지정 작업만 메타 기록
- `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 벽시계 초과 간헐 이력
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크(VM 미등록) · iOS 는 ffmpeg 불가 → 카메라 RTSP 폴백
- 로컬 ffmpeg.exe 부재(`-AllowMissingFfmpeg`) · Authenticode 미서명 · Android 빌드 미검증
