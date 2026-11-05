# Current Project Status - Active Milestone: 【Pro Workflow】 🎬 티커 올인원 I2V 오토-디렉터 · 🩺 StockSuite 헬스 닥터 · 🔀 캔버스 A/B 스플릿 비교기
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 599/599 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 🎬 `Services/Chart/TickerAutoDirector`(신규): ① `ChartStillGeneratorService.GenerateAsync` 1920×1080 PNG → ② START FRAME 바인딩 콜백 → ③ `GeminiPromptService.GenerateTickerPromptAsync` 산업 메타포 프롬프트 · 단계별 예외 격리([부분 구제]) · 재진입 차단 · 취소 전파
- Workspace 관심종목 칩 클릭 · IPC `TickerSelected` 모두 `GenerateTickerPromptAsync` → 오토-디렉터 경유(1클릭 → [생성]만) · `BindStartFrameAsync` 공용화(차트 스틸 버튼 재사용) · 프롬프트 히스토리 Snap
- 🩺 `Services/Export/StockSuiteHealthService`(신규): `ValidateIntegrationHealthAsync` — AssetDirectory(쓰기/삭제 프로브) · DisplaySchema(`stocksuite_displays.json` 스키마) · ManifestIntegrity(SHA-256 형식·활성/백업 존재·크기) · IpcPipeConnection(브릿지 상태 또는 250ms 클라이언트 접속 지연) → `HealthCheckResult`(Healthy/Degraded/Unhealthy)
- 🩹 `RepairAsync`: 폴더 생성(상위 존재 시) · 손상 파일 `.corrupt` 보존 후 표준 규격 원자적 재작성(매니페스트는 활성 에셋 SHA-256 재계산 · 백업 자동 편입) · 중지 IPC 브릿지 재기동 → 재진단
- UserSettings StockSuite 카드 `[🩺 StockSuite 연동 정밀 진단]` · `[🩹 기본 규격으로 1클릭 복구]` · 항목별 상태 리포트
- 🔀 Workspace `[🔀 A/B 비교]` 토글 — 원본 비디오(좌) | `backdrop-filter` LUT + 샤프닝 근사(우) · `promptflow.js` `pfsAbSplit` 문서 위임 포인터 드래그(마우스/터치/펜) · 키보드 ←/→/Home/End · .NET 왕복 0
- DI: `MainForm.cs` + `MauiProgram.cs` 양쪽 `StockSuiteHealthService` 싱글톤
- Tests: `ProWorkflowDirectorHealthTests` 3종(오토-디렉터 전체 파이프라인 · 손상 진단/복구 · 정상 Healthy + 실 Named Pipe 프로브)
