# Current Project Status
- Active Milestone: 【Step 4】 인프라 & 자산 관제 정밀 감사 · 렌더큐→갤러리 딥링크 · I2V 복원 완결 · VRAM 가드레일
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 657/657 PASS(직렬 · MaxCpuCount=1 · DisableParallelization) · Gallery+RenderQueue 22/22
- Updated: 2026-10-28 (KST)

## Completed
- Step4 딥링크: 렌더 큐 Completed 작업 `📂 갤러리에서 보기` → `gallery?focus=<OutputPath>` → `GalleryViewModel.FocusByPath`(필터/검색 해제 · 인스펙터 선택)
- Step4 I2V 복원: `.meta.json` `referenceImagePath` → `GalleryItem`/`GalleryRestoreContext` → `PromptInputViewModel.ApplyGalleryRestoreAsync`(첫 프레임 · `I2vModeRequested`) · 작업실 `_imagePath` 바인딩 · 파일 유실 시 T2V
- Step4 VRAM: `RenderQueueService` 디스패치 직전 VRAM ≥95% → 자동 일시정지(`IsVramPaused`) + 경고 배너 · 수동 재개 시 해제 · 워커 가동 중 텔레메트리 폴링(참조 카운트) · 프로브 예외 격리
- Step4 인프라: `.gitignore` `artifacts/` 추가 · 사이드바 NavLink 15개 전수 `@page` 매칭(글로벌 관제는 오버레이 · 노드 `/node-studio`)
- Step3: I2V 엔진 실물 바인딩 · 커머셜/라이브 큐 결선 · 비용 예약 가드
- Step2: 작업실↔스토리보드↔숏폼 결선 · 카메라 캡처 롤/`SmartCameraMerge` · `CameraStudio.razor` 통합

## Known Issues
- VRAM 가드는 nvidia-smi 있을 때만(모바일·비 NVIDIA → 가드 생략) · 첫 샘플 이전 디스패치는 미검사
- 비용 예약은 메모리 전용(재시작 시 소멸) · 커머셜 로고/디스클레이머 합성 미결선
- 시뮬레이션 폴백은 실제 미디어 미생성 → `OutputPath` 지정 작업만 메타 기록 · 딥링크도 동일
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 · Android 빌드 미검증
