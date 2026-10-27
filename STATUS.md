# Current Project Status
- Active Milestone: 【보충 P1】 렌더 큐 실물 추론 엔진 어댑터 결선(오프라인 시뮬레이션 폴백)
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 634/634 PASS(직렬 · MaxCpuCount=1 · DisableParallelization) · RenderQueueWorkerTests 6/6
- Updated At: 2026-10-28 (KST)

## Completed Deliverables
- P1: `IRenderExecutionEngine` · `VideoEngineRenderExecutionEngine`(VideoEngineSelector 활성 엔진 → ComfyUI/Cloud, 미가용 시 Mock 시뮬레이션) · `RenderQueueService` 엔진 주입·진행률 동기 반영·산출 파일 검증·실패 `ErrorMessage` 격리 · `BatchRenderJob.EngineKind/ResultPath/ErrorMessage` · VM `ActiveEngineKind/LastErrorMessage` · DI 양쪽
- P0: Seed long 일원화 — `LiveVideoCanvasStudio.Seed`·`WanVideoRequest.Seed` long · 갤러리 복원 int 다운캐스트 제거 · 🎲 `NextInt64` · 클라우드(DashScope/SiliconFlow/Runway)는 `ToCloudSeed` 31비트 결정적 폴딩 · 로컬 엔진 원값 · origin/main 정렬(upstream=origin/main)
- PR-2: `UserCustomPreset` · `Load/Save/DeleteCustomPreset` · `GetAllPresets`(큐레이션/사용자 분리) — `%LocalAppData%\PromptFlowStudio\custom_story_presets.json`(지연 로드 · tmp→원자 교체 · 손상 시 `.corrupt.bak` 보존)
- PR-2: `PromptInputViewModel.CustomPresets`(ObservableCollection) · `SaveCurrentAsPreset`/`DeletePreset`/`ApplyCustomPreset` · `Workspace.razor` ⭐ 탭 + 제목 저장폼 + 적용/삭제 카드
- PR-1: `PromptInspirationService` · 스토리 레시피 허브 · 레고 빌더 모달 · DI `MainForm.cs`+`MauiProgram.cs`
- GL-1~4: 갤러리 아카이브 · 인스펙터 HUD · 렌더 메타 `{이름}.meta.json` 자동 기록
- 이전: 무선 카메라 · 숏폼 · 커머셜 · 라이브 스튜디오 · 렌더 큐 · 대량 양산 · 스토리보드 · 프롬프트 합성기

## Known Issues / 환경 노트
- 시뮬레이션 폴백은 실제 미디어 미생성 → `OutputPath` 지정 작업만 메타 기록 · 실물 엔진은 로컬 산출물 옆 메타 기록(원격 URL은 메타 생략) · 큐 UI 엔진 배지 Razor 표출 미결선
- `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 부하성 벽시계 초과 간헐 관측 이력 — 금회 PASS
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록(커스텀 프리셋 서비스는 공유 컴파일)
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 · Authenticode 미서명 · Android 빌드 미검증
