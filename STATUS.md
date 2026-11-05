# Current Project Status - Active Milestone: 【Resilience Pro】 🧟 좀비 태스크 차단 · 🔄 저널 인스펙터 · 🎨 HTS 4대 LUT · 🛡️ Jitter 지수 백오프
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 593/593 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 🧟 `TaskJournalService`: 상태 상수 `Processing/Interrupted/UserCanceled/Failed` · `IsAutoRecoverable`(UserCanceled 자동 회수 영구 제외) · `MarkUserCanceled/Retry/Purge/PurgeAll/RecoverTaskAsync`(수동 단건 회수) · `Count` 캐시
- `BatchRenderJob.UserCancelRequested`(JsonIgnore) — `RenderQueueService.CancelJob/CancelJobGroup` 설정 → 엔진 취소 시 `UserCanceled`, 종료/단절은 `Interrupted`
- 🔄 Workspace `[🔄 미완료 클라우드 작업: N건]` 뱃지 → 저널 인스펙터 모달(Task ID·발번 시각·프롬프트 요약·상태 · 수동 회수/재시도/영구 삭제/전체 비우기)
- 🎨 `Services/Export/ColorGradingService`(신규): Pure OLED Black · Matrix Neon · Quant Mono · Warm Amber → FFmpeg `eq=` + CSS filter 근사
- Workspace `[🎨 톤 매핑 LUT]` 드롭다운 → `.ws-video` CSS filter 실시간 · `StockSuiteExporter.Lut`/`MultiExportOptions.Lut` → `-vf` 체인(스케일→60fps→LUT→워터마크) 번인
- 🛡️ `Services/Network/ResilientHttpClient`(신규): 429/502/503/504·`HttpRequestException` → 2^(n-1)초+Jitter(0~500ms) 최대 3회 · `OnRetryAttempt` → 작업실 `⏳ API 일시 지연으로 재시도 중 (n/3)...`
- 결선: `GeminiPromptService.CallAsync` · `WanApiService.QueryAsync`(DashScope) · `VideoEngineRenderExecutionEngine.DefaultDownloadAsync`
- DI: `MainForm.cs` + `MauiProgram.cs` 양쪽 `ResilientHttpClient.Shared` 싱글톤
- Tests: `ResilienceZombieLutRetryTests` 4종
