# Current Project Status
- Active Milestone: 【Core-Robustness】 🛡️ 전사 IProcessRunner 100% 수렴 · 🌱 HTS 스마트 에코 절전 · 🧯 렌더 큐 결함 격리/⚡ 우선순위 제어
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드 — 공유 파일은 선택적 매개변수만 추가)
- Tests: FastUnit 620/620 · 전체 853/853 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 🛡️ `Services/` 직접 `new Process` 0건(허용 = `Engines/ProcessRunner.cs` 단일 게이트웨이) — Wan·Storyboard·Broadcast·Interactive·Enterprise·Spatial·Virtual·Sound·Hardware·SelfFix·Deploy·Hardening·Camera 28개 파일 이관
- 🛡️ `IProcessRunner` 확장: `RunWithEnvAsync`(자식 전용 환경변수 · 부모 오염 없음) · `StartStreaming(…, onStderrLine)`(진단 tail 보존) · `ShellOpen`(OAuth 브라우저)
- 📱 `Mobile/wwwroot/index.html` `promptflow.js` 결선(비디오 락 해제·스크러버·에코)
- 🌱 `promptflow.setEcoMode`/`ecoSuspend`/`ecoResume`: `visibilitychange`/`pagehide` 시 재생 중 `<video>` pause → 복귀 시 에코가 멈춘 것만 play · `MainForm.OnResize` 최소화 훅 · 설정 `🌱 HTS 스마트 에코 절전 모드`(기본 ON)
- 🧯 `RenderQueueService`: 예외 작업만 `Failed`+`IsQuarantined`/`QuarantineReason`(429·Invalid Input·Timeout·Network·I/O·Engine) 격리 후 큐 계속 · Retry 시 해제 · 큐 화면 `🧯 격리` 배지
- ⚡ `MoveJobUp`/`MoveJobDown`/`MoveJobToTop`(Queued/Pending 만) · 큐 화면 `▲ ▼ ⚡` 버튼
- 테스트 `CoreRobustnessTests`(정적 스캔 · 격리 후 계속 · 사유 분류 · 우선순위 재정렬)

## Next
- 셸 위임 `Process.Start(UseShellExecute)`(인스톨러·URL·StockSuite 실행) 및 `ProcessCompat.Start` 호출부 `ShellOpen` 수렴 검토
- Mobile Android SDK 환경에서 빌드 검증
