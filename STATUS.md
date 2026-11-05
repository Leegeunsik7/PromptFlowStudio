# Current Project Status
- Active Milestone: 【Suite-Refine】 📈 티커 배지 시세 자동 채움 · 🖥️ 기본 해상도 영속(벤치마크 Ultra → 4K) · ☑️ 갤러리 다중 선택 모드
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: FastUnit 632/632 · 전체 864/865 (1 flaky: `TradingSafetyGuardService_DetectsMarketHours` 부하 시 타이밍 실패 · 단독 3/3 PASS)
- Updated: 2026-11-06 (KST)

## Completed
- 📈 `PublishViewModel` ← `ChartStillGeneratorService`(IPC 캐시 → quotes.json) 주입 · `SetTicker`/`TryAutoFillQuote` 마지막 종가·직전 대비 등락률 자동 채움 · `$185.50`/`74,500원` · `+2.4%` 포맷 · 미스 시 입력 보존
- 📈 `PublishModal.razor` 티커 변경/배지 ON 시 자동 채움 · `📈` 재조회 버튼 · 출처 메시지
- 🖥️ `UserPreferenceService.ExportResolution`(Res1080p·Res4K_UHD·Res21x9·Res32x9) + `DefaultResolution`(손상 값 → 1080p) · `ExportResolutionInfo`(라벨·픽셀·StockSuite 매핑)
- 🖥️ `HardwareBenchmarkService.ApplyRecommendationAsync` — Ultra 4K+60fps · Balanced 1080p+60fps · Eco 1080p+30fps · 샤프닝 ON · `UserSettings` 결선 + 기본 해상도 드롭다운
- 🖥️ `PublishViewModel.Open` → `StockSuiteResolution` 을 `DefaultResolution` 으로 초기화
- ☑️ `GalleryViewModel` `IsSelectModeActive`·`SelectedMediaIds`·`SelectAll`/`DeselectAll`·`ExportSelectedBundleAsync`
- ☑️ `MediaGallery.razor` `[☑️ Select Mode]` → 카드 체크박스 · `[📦 Export Selected (n)]` · `[🗑️ Delete Selected (n)]`(확인) · Select/Deselect All
- 🧪 `SuiteRefineQuoteResolutionSelectTests` 3건

## Next
- 울트라와이드(21:9 · 32:9) StockSuite 내보내기 스케일 필터 지원(현재 1080p 폴백)
