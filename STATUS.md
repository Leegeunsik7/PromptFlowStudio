# Current Project Status
- Active Milestone: 【Pro Studio】 원클릭 멀티 타겟 패키지 · StockSuite 관심종목 연동 디렉터 · 갤러리 Hover-to-Play/즐겨찾기
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 574/574 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 멀티 패키지: `StockSuiteExporter.ExportMultiTargetPackageAsync(src, MultiExportOptions)` → `{name}_package/` 에 3종 동시 생성 · 규격별 [부분 구제] · 산출물 `.meta.json`(`isExported:true`)
  - HTS 16:9/21:9/32:9(1920/2560/3840×1080) 중앙 스마트 크롭 + Lanczos + Unsharp · 무음 WebM/MP4
  - 모바일 9:16 `crop=ih*9/16:ih,scale=1080:1920` 경량 MP4(veryfast·crf28·무음)
  - 쇼츠 9:16 + BGM 루프 믹싱 + 1.5초 afade out(BGM 부재 시 무음 폴백) · `BuildMultiTargetProfiles` 순수 인자 계산
- PublishModal(작업실·갤러리 공용): HTS 비율 셀렉터 + [📦 올인원 멀티 패키지 내보내기] (`PublishViewModel.ExportMultiPackageAsync`)
- `StockSuiteWatchlistService`(신규·DI 데스크톱/모바일): `watchlist.json` → `config/favorites.json` 탐색 · 다형 스키마 · 0-패딩/중복 차단 · 부재 시 코스피 TOP5 + 매그니피센트 7
- Workspace: 프롬프트 상단 [📊 내 관심종목 연동] 칩 바 → `GeminiPromptService.GenerateTickerPromptAsync`(핵심 사업 메타포 + 티커 HUD 텍스처 · 로컬 폴백)
- 갤러리: 정적 포스터(`.poster.jpg`/썸네일/CSS 스틸) 기본 · 호버 시에만 `<video>` 마운트(무음 2초 루프) · 이탈 시 언마운트
- 갤러리 ⭐: `GalleryMediaService.SetFavorite/ToggleFavorite`(메타 필드 보존·원자 교체) · 필터 탭 [⭐ 즐겨찾기]·[🎬 AI Render]·[📦 내보냄]
- Tests: StockSuiteExporter_ExportsMultiTargetPackage_GeneratesAllProfiles · StockSuiteWatchlistService_ParsesWatchlistJson_AndProvidesFallback · GalleryService_ManagesFavorites_AndFiltersCorrectly
