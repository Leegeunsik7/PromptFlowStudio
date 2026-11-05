# Current Project Status
- Active Milestone: 【Pro HTS】 내보내기 메타 규격 통일/역방향 치유 · ⚡ 60fps 모션 보간 · 📅 장중 시간대별 플레이리스트 스케줄러
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 578/578 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- `ExportMetadata`(신규): 원본 `.meta.json` 에 `isExported:true` · `exportedAt`(ISO-8601 UTC) · `exportedTargets[]`(중복 제거 누적) 병합 · 기존 필드 보존 · 원자 교체
  - 적용 경로: `ExportAsync`(MP4/WebM · 배치 팩 경유) · `ExportMultiTargetPackageAsync`(원본 + 산출물 메타)
- 역방향 치유: `GalleryMediaService(Func<string?> stockSuiteDir)` 스캔 시 StockSuite 폴더 산출물과 출처 마커(`.pfs-src`)/파일명/크기+SHA-256 매칭 → `isExported` 보정(`LastHealedCount`)
- ⚡ 60fps: `BuildVideoFilter(sharpen, smooth60, lowSpec)` → 샤프닝 뒤 `minterpolate=fps=60:mi_mode=mci:mc_mode=aobmc:me_mode=bidir`(저사양 `fps=60`)
  - `ExportAsync/ExportBatchPackAsync(smooth60)` · `MultiExportOptions.Smooth60`(HTS 규격만) · 환경설정 `stocksuite.smooth60`(기본 OFF) · PublishModal/UserSettings 체크박스
- 📅 `StockSuitePlaylistService`(신규·DI 데스크톱/모바일): 4슬롯(장초반/정규장/장마감/야간) → 슬롯 파일 내보내기 + `playlist.json` + `hotreload.trigger`(file=playlist.json) · 미지정 슬롯 기존 파일 유지 · 자정 넘김 `ResolveActiveSlot` · 스키마 검증 `TryParse`
- MediaGallery: 툴바 [📅 장중 스케줄러] 모달 → [StockSuite 스케줄 발행] · 내보내기 후 아카이브 즉시 재스캔(📦 탭 반영)
- Tests: StockSuiteExporter_UnifiesExportMetadata_AcrossAllExportMethods · GalleryMediaService_RetroactivelyHealsExportedFlag_FromStockSuiteTargets · StockSuiteExporter_Applies60FpsInterpolationFilter_WhenRequested · StockSuitePlaylistService_GeneratesValidPlaylistSchedule_AndWritesJson
