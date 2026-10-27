# Current Project Status
- Active Milestone: 인스피레이션 허브 👶 큐트 베이비 & 키즈 카테고리 · 레고 칩 보강
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 631/631 PASS(직렬 · MaxCpuCount=1 · DisableParallelization) · PromptInspirationTests 13/13
- Updated At: 2026-10-27 (KST)

## Completed Deliverables
- PR-3: `StoryPresetCategory.CuteBabyKids`(👶 큐트 베이비) 프리셋 4종 · 피사체/배경/조명 칩 각 2종 · `Workspace.razor` 필터 인기순(👶 최전면) · 테스트 `GetCuratedPresets_ContainsCuteBabyKidsCategory`
- PR-2: `UserCustomPreset` · `Load/Save/DeleteCustomPreset` · `GetAllPresets`(큐레이션/사용자 분리) — `%LocalAppData%\PromptFlowStudio\custom_story_presets.json`(지연 로드 · tmp→원자 교체 · 손상 시 `.corrupt.bak` 보존)
- PR-2: `PromptInputViewModel.CustomPresets`(ObservableCollection) · `SaveCurrentAsPreset`/`DeletePreset`/`ApplyCustomPreset` · `Workspace.razor` ⭐ 탭 + 제목 저장폼 + 적용/삭제 카드
- PR-1: `PromptInspirationService` · 스토리 레시피 허브 · 레고 빌더 모달 · DI `MainForm.cs`+`MauiProgram.cs`
- GL-1~4: 갤러리 아카이브 · 인스펙터 HUD · 렌더 메타 `{이름}.meta.json` 자동 기록
- 이전: 무선 카메라 · 숏폼 · 커머셜 · 라이브 스튜디오 · 렌더 큐 · 대량 양산 · 스토리보드 · 프롬프트 합성기

## Known Issues / 환경 노트
- 가상 렌더 파이프라인은 실제 미디어를 생성하지 않음 → `OutputPath` 지정 작업만 메타 기록
- `SingleFileBinaryProcessBootstrap_Check` 직렬 전수 중 부하성 벽시계 초과 간헐 관측 이력 — 금회 PASS
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록(커스텀 프리셋 서비스는 공유 컴파일)
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 · Authenticode 미서명 · Android 빌드 미검증
