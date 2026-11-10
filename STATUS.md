# Current Project Status

- Active Milestone: 【Pluto Emotional Letter Subtitle Director】 💌 3막 감성 편지 자막 + 시간 동기 번인 파이프라인
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 935/935 PASS (실패 0 · 9m 39s · MaxCpuCount=2)
- Updated: 2026-11-10 (KST)

## Completed
- 💌 `PlutoSubtitleScript` — SubjectName · Act1/2/3 · UsedLlm · `ToCues()` · `ToSrtFormat()` · `ToFullLetterText()` (01~04 · 05~10 · 11~14초)
- 🧠 `PlutoSubtitleDirector.GenerateLetterScriptAsync` — Gemini(3.5초 예산 · `LlmBudget` 주입) · 막별 스키마 검증 [부분 구제] · 테마 8종 결정론 로컬 템플릿(FNV-1a) · 아이덴티티 볼트 이름 · 받침 호격
- 🎬 `StockSuiteExporter.BurnSubtitles/SubtitleCues` — `drawtext enable='between(t,s,e)'` 하단 중앙 박스 체인 · drawtext 실패 → 오버레이 제외 재시도(`⚠ 자막 생략`) · `{출력}.srt` 원자적 동반 기록
- 🪟 `PublishModal.razor` — 감성 편지 박스(Gemini/Local 뱃지 · 타임스탬프 칩) · `📋 자막 전체 복사` · `🏷️ 영상에 감성 자막 번인`
- 🧩 `PublishViewModel` — 생성/복사/번인 토글 · 공유 싱글톤 자막 상태 export 후 리셋
- 🔌 DI: `MainForm.cs` + `MauiProgram.cs` 싱글톤
- 🧪 `PlutoSubtitleDirectorTests` 3건

## Next
- 작업실(Workspace) 내보내기 경로 자막 번인 연동 · 모바일 빌드 검증(Android SDK 확보 시)
