# Current Project Status
- Active Milestone: 【Pro-Governance】 🎛️ 핵심 비디오 서비스 IProcessRunner 수렴 · ✂️ 무손실 키프레임 트리머 · 📈 KRX 4단계 세션 관제
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 613/613 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 🎛️ `AmbientLoopService`(`IProcessRunner` 생성자) · `MediaProbeService`(ffprobe/배너/휘도 3경로) · `VideoMergeService`(병합·포스터·프레임 — 레거시 `new Process()` 제거, 진행률 라인 콜백 유지) 전량 `IProcessRunner` 경유 · DI(`MainForm.cs`·`MauiProgram.cs`) 결선
- ✂️ `Services/Video/VideoTrimService`(신규): `-ss -to -i -c copy -avoid_negative_ts make_zero` · 구간 검증 · 새 클립(`_trim_{s}-{e}s`) / 덮어쓰기(임시 산출 → 검증 → 원자적 교체, 실패 시 원본 보존)
- ✂️ `Workspace.razor` 스크러버 바 `[✂️ 구간 자르기]` — Start/End 0.1s 입력 · 현재 위치 1클릭 마킹 · `[현재 영상에 덮어쓰기]`/`[새 클립으로 갤러리 저장]`
- 📅 `KrxHolidayCalendar.Sessions`: `%LOCALAPPDATA%/PromptFlowStudio/Config/krx_closures.json`(closures · sessions 오버라이드 — 수능 10:00 개장·조기 폐장) · 불량 항목 [부분 구제] · 앱 시작 시 로드
- 📈 `KrxSessionStatus` PreMarket/RegularMarket/PostMarket/Closed · `ResolveSession`(다음 전환 시각·남은 시간) · 기본 정규장 판정이 세션 머신 경유 · 툴바 4색 배지 + 남은 시간 툴팁(10초 틱 · 분 단위 변경 시만 재렌더)
- 테스트 `ProcessRunnerGovernanceTests`(8건)

## Next
- 잔여 Media/Wan 계열 직접 `Process` 호출부 `IProcessRunner` 단계적 이관
