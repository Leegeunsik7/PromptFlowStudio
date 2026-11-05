# Current Project Status - Active Milestone: 【Quant Ambient】 ⏩ 정방향 크로스페이드 루프 · 📊 티커 차트 스틸(I2V) · 🖥️ 듀얼 모니터 분할
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 596/596 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- ⏩ `AmbientLoopService`: `LoopMode { SlowStretch, PingPong, ForwardCrossfade }` · `BuildForwardCrossfadeFilterGraph` — 3.3x 슬로우(16.5s) → `trim` main/tail → `[tail][main]xfade=fade:1.5:offset=0` 15초(reverse 없음)
- 엔진 기본값 ForwardCrossfade(`ambient.loopmode` 메타 · 핑퐁 메타 우선) · `TaskJournalService` 회수 경로 동일 · 기존 bool 오버로드 보존
- 📊 `Services/Chart/ChartStillGeneratorService`(신규 · Pure BCL 래스터 + PNG 인코더): OLED 블랙 · 네온 캔들 · MA20/60 · 볼린저 20,2σ · 피보나치 · 거래량 · 10호가 그리드 · 5×7 비트맵 글꼴 · 티커+일자 결정적 시드
- Workspace `[📊 차트 스틸 즉시 생성]` → 선택 칩(없으면 첫 관심종목) 1920×1080 PNG → 참조 이미지(START FRAME · STILL) 바인딩 + I2V 감지
- 🖥️ `StockSuiteExporter.DualMonitor.cs`: `ExportDualMonitorSplitAsync(src, bezel=40)` — (3840+베젤)×1080 채움 스케일 → 좌 `crop=1920:1080:0:0` / 우 `crop=1920:1080:{1920+베젤}:0` → `bg_screen_1/2`(.webm/.mp4) · [부분 구제] · `stocksuite_displays.json` 갱신
- PublishModal `[🖥️ 듀얼 모니터 분할 내보내기]` · `PublishViewModel.ExportDualMonitorSplitAsync`
- DI: `MainForm.cs` + `MauiProgram.cs` 양쪽 `ChartStillGeneratorService` 싱글톤
- Tests: `QuantAmbientCrossfadeChartDualTests` 3종 · `AmbientLoopTests` 엔진 기본 모드 단언 갱신
