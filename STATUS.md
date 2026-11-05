# Current Project Status
- Active Milestone: 【Quant Core】 📈 실시세 OHLCV 이평선 차트 · 🎞️ 1프레임 스크러버/📸 스냅샷 · 🛡️ 장중 트레이딩 세이프가드
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 602/602 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 📈 `ChartStillGeneratorService`: `OhlcvCandle`(검증) · `OhlcvQuoteParser`(`quotes.json` 티커 맵/단일 · 객체/배열 캔들) · IPC 캐시 → quotes.json → 시드 합성 폴백 · MA5(골드)/MA20(레드)/MA60(그린) · 거래량 전일 종가 대비 색상 · `ChartStillResult.RealData`
- 🔌 `StockSuiteIpcBridge`: `QuotePushed`(≤512KB · 스키마 검증) → `OnQuotePushed` → `ChartStillGeneratorService.PushQuotes`(MainForm 결선)
- 🎞️ Workspace `[⏮ -1F] [+1F ⏭]`: FFmpeg 프로브 FPS(폴백 30) 프레임 격자 스텝(`StockSuiteExporter.StepFrame`) · `pfsFrameScrub` JS
- 📸 `StockSuiteExporter.ExtractSnapshotAsync`: `-ss {t} -i {video} -vframes 1 -q:v 2 out.png` → `MediaRoot/_stills`(갤러리 자동 노출) · 🖼️ 바탕화면 설정(`#if WINDOWS` SystemParametersInfo)
- 🛡️ `Services/Process/TradingSafetyGuardService`(신규): 평일 09:00~15:30 KST(UTC+9 고정·사용자 구간·자정 넘김) · `ProcessRunner` 훅(ffmpeg BelowNormal · 동시 인코딩 1 게이트, AsyncLocal 재진입) · `RenderQueueService.TradingGuard` 슬롯 공유 · 설정 토글(기본 ON) · 작업실 인디케이터
- DI: `MainForm.cs`·`MauiProgram.cs` 양쪽 등록 · 테스트 `QuantCoreOhlcvScrubberGuardTests`(3건)

## Next
- 공휴일(휴장일) 캘린더 반영 · 인디케이터 장 개시/마감 시점 자동 리렌더 타이머
