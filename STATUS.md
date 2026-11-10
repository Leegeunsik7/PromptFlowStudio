# Current Project Status

- Active Milestone: 【Workspace Subtitle Burn-In Parity】 💌 작업실 단일 내보내기 경로 감성 자막 번인 연동
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 936/936 PASS (재실행 · 11m 37s · MaxCpuCount=2) — 1차 실행 1건 간헐 실패(재현 안 됨 · 미식별)
- Updated: 2026-11-10 (KST)

## Completed
- 🏷️ `Workspace.razor` — 캔버스 툴바 `🏷️ 감성 자막 포함` 토글(기본 OFF · `data-canvas-tool="letter-subs"`)
- 🧠 `GenerateWorkspaceLetterAsync` — 작업실 프롬프트 + 활성 프로필 이름 → `PlutoSubtitleDirector`(Gemini 우선 · 실패/미주입 → 로컬 결정론 폴백)
- 🎬 `StockSuiteExporter.ExportWithSubtitlesAsync` — 호출 범위 한정 `BurnSubtitles/SubtitleCues` 설정 → 1~4·5~10·11~14초 `drawtext enable` 번인 + `{출력명}.srt` · finally 원복(공유 싱글톤 누수 차단)
- 🔌 `ExportToStockSuite` — LUT · 성장 뱃지 · BGM 기존 체인 유지 + 자막 큐 전달
- 🧪 `Workspace_ExportWithSubtitles_AppliesTimedBurnInAndCreatesSrt` (ON: 시간 게이트·SRT · OFF: 필터/SRT 없음 · 상태 원복)

## Next
- 간헐 실패 테스트 식별(trx 로깅 상시화) · 모바일 빌드 검증(Android SDK 확보 시)
