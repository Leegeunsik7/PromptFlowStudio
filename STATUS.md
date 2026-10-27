# Current Project Status
- Active Milestone: 갤러리 아카이브·렌더 메타 자동 기록 · 프롬프트 레시피 허브 · 격리 테스트 웜업 안정화
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 627/627 PASS(직렬 · MaxCpuCount=1 · DisableParallelization) · GalleryMediaTests 5/5
- Updated At: 2026-10-27 (KST)

## Completed Deliverables
- GL-1~3: `GalleryMediaService` · `GalleryViewModel` · `MediaGallery.razor` 인스펙터 HUD · `Workspace.razor` 복원 주입
- GL-4: `BatchRenderJob.OutputPath`(선택) → `RenderQueueService` Completed 시 `{이름}.meta.json`(prompt/seed/resolution/createdAt/durationSec/renderDurationSec) 기록 · 실패 격리
- PR-1: `PromptInspirationService` · 스토리 레시피 허브 · 레고 빌더 모달(`PromptInputViewModel`/`Workspace.razor`) · DI `MainForm.cs`+`MauiProgram.cs`
- TS-1: PVG 폴링 0B 측정 — GC 정리 후 k≤3 웜업 · 스레드 홉 라운드 제외 · 가시성 전환 측정 전 GC 정리
- TS-2: BroadcastMcr 프레임 예산 · LAE 합성 최소 µs 측정 전 GC 정리 + 30틱 웜업
- 이전: 무선 카메라 · 숏폼 · 커머셜 · 라이브 스튜디오 · 렌더 큐 · 대량 양산 · 스토리보드 · 프롬프트 합성기

## Known Issues / 환경 노트
- 가상 렌더 파이프라인은 실제 미디어를 생성하지 않음 → `OutputPath` 지정 작업만 메타 기록
- `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 부하성 벽시계 초과 간헐 관측 이력 — 재실행 PASS
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 · Authenticode 미서명 · Android 빌드 미검증
