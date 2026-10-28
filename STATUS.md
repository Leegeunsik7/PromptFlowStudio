# Current Project Status
- Active Milestone: 【Step 2】 제작 코어(작업실 ↔ 스토리보드 ↔ 숏폼) 파이프라인 정밀 감사 · 일관성/해상도 결선
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 649/649 PASS(직렬 · MaxCpuCount=1 · DisableParallelization) · Storyboard+AiShorts 31/31
- Updated: 2026-10-28 (KST)

## Completed
- Core: 작업실 `🎬 스토리보드로 4컷 확장`(`ExpandFromWorkspace` · 1번 컷 원문 + 기승전결 뼈대 · 시드 핀 계승/미고정 시 결정적 시드) · `PropagateConsistency` 시드 `MasterSeed+(컷-1)` · 스토리보드 `📥 렌더 큐 일괄 전송`(1번 컷 피사체·참조 이미지 공통 앵커 · `BatchRenderJob` 시퀀스 메타) · 숏폼 큐 전송 9:16 강제 정규화 + BPM/전환 박/FX/StartSec 기록 · `.meta.json` 메타 확장
- Step2 카메라: 캡처 롤 · 세이프존 가이드 · 모션 강도/속도 · `SmartCameraMerge` · 작업실 I2V 자동 활성화
- Step1: `CameraStudio.razor` 통합(/camera-studio · 레거시 라우트 포워딩) · `CameraStudioTests`
- QA: `scripts/CaptureAllScreens.ps1`(DPI PerMonitorV2 · CDP 1920x1080@1x · 9라우트 캡처) · h1 포커스 아웃라인 제거 · Win10 미지원 이모지 치환 · 라이브 placeholder `&#10;` 수정 · 갤러리 카드/숏폼 패널 정렬
- P1~P3: 렌더 실행 엔진·큐 진행률/실패 격리 · 컷 덱 ↔ Scenes 양방향 동기화 · 네트워크 카메라 수신기(폴백 포함)

## Known Issues
- 스토리보드 렌더 큐 전송분의 `ReferenceImagePath` 는 메타 기록만 — 엔진 I2V 업로드 결선은 JobManager 경로 유지
- 시뮬레이션 폴백은 실제 미디어 미생성 → `OutputPath` 지정 작업만 메타 기록
- `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 벽시계 초과 간헐 이력
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 · 로컬 ffmpeg.exe 부재 · Authenticode 미서명 · Android 빌드 미검증
