# Current Project Status
- Active Milestone: 【Governance】 📅 KRX 휴장일 캘린더 · 🖼️ 갤러리 스냅샷 매니저 · 🛡️ 전사 FFmpeg 실행기(IProcessRunner) 일원화
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 605/605 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 📅 `KrxHolidayCalendar`(신규 · BCL `KoreanLunisolarCalendar`): 신정·삼일절·근로자의 날(5/1)·어린이날·부처님오신날·현충일·광복절·개천절·한글날·성탄절 · 설/추석 3일 · 대체공휴일(설·추석·어린이날 2014~ · 국경일 2021~ · 부처님오신날/성탄절 2023~) · 연말 폐장일(12/31 또는 직전 영업일) · 비정기 휴장 `AddClosure` · 연도별 캐시
- 🛡️ `TradingSafetyGuardService`: `RespectKrxHolidays`(기본 ON) · `IsHolidayNow` · 휴장일 자동 해제(GPU 100%)
- ⏱️ `Workspace.razor`: 10초 `PeriodicTimer` 판정 틱 — 상태 변경 시에만 `InvokeAsync(StateHasChanged)` · Dispose 시 CTS 해제
- 🖼️ 갤러리: `GalleryMediaType.Still`(`_stills/*.png`) · `[🖼️ 스냅샷]` 탭(`GalleryFilter.Snapshots` · AI Render 탭에서 제외) · 카드 호버 `[바탕화면 적용]·[탐색기에서 보기]·[스냅샷 삭제]` · 다중 선택 삭제 기존 체크박스 연동
- 🎛️ `IProcessRunner`/`SystemProcessRunner`(정적 `ProcessRunner` 훅 위임) · `AsFfmpegRunner` 어댑터 · `AudioMuxService`·`StoryboardStitchService` DI 생성자 · `MainForm.cs`·`MauiProgram.cs` 등록
- 테스트 `GovernanceKrxGallerySnapshotTests`(3건)

## Next
- 나머지 직접 `new Process()` FFmpeg 호출부(VideoMergeService 등) `IProcessRunner` 단계적 이관
