# Current Project Status
- Active Milestone: 【Pluto Deep-Motion】 작업실 1-Click 15초 모션 비디오 렌더 UI 결선
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio (`-p:NuGetAudit=false` · Mobile 미빌드)
- Tests: 대상 필터 133/133 PASS (Motion · Vision · BabyStudio · SocialRadar · TitanRelease) — 전체 스위트 미실행(직전 전체 1007/1007)
- Updated: 2026-11-10 (KST)

## Completed
- 🎥 코어 커밋 `53e6fdf`: `PlutoMotionCompiler` · `ExportMotionVideoFromPhotoAsync` · `PlutoVisionStoryPlan.MotionPath` → origin main/master 푸시
- 🎬 `Blazor/Pages/Workspace.razor`: `[🎬 15초 모션 비디오 즉시 렌더]` 버튼(`data-motion-render`) — 📸 사진 스토리 완료 후 노출
  - `RenderMotionVideoAsync`: KO/EN 편지 자막 큐 · BGM(선택 트랙 · OFF → 자장가) · SFX 강제 ON · `plan.MotionPath` 궤적 전달
  - 진행 스피너(`ws-motion-spin`) · 결과 토스트 · `📂 위치 열기`(`RevealInExplorer` → `%TEMP%\PromptFlowStudio\motion\`)
  - 익스포터 공유 상태 보호: `_stockBusy` 상호 배제 · 취소(`_mediaCts`) · 예외 격리
- 🧪 `PlutoMotionCompilerTests.Workspace_Wires_OneClick_MotionVideo_Render` 결선 검증

## Notes
- 실제 앱에서 버튼 클릭 렌더(FFmpeg 실행)는 수동 미검증 — 마크업/결선 테스트와 백엔드 테스트로만 확인
- 출력 해상도는 1080×1920 고정(4K 토글 미결선)
