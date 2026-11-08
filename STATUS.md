# Current Project Status
- Active Milestone: 【Security Audio】 🔇 StockSuite 무음 폴백 강화 · 🎵 쇼츠 BGM 격리 · 🎞️ 갤러리 미리보기 시각 동기
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 883/883 PASS (`-- RunConfiguration.MaxCpuCount=2`, 10m26s)
- Updated: 2026-11-08 (KST)

## Completed
- 🔇 `StockSuiteExporter.CopyAsync` — 오디오 동반 원본은 원본 복사 폴백 거부 → `SilentFallbackRejectedMessage` · `ContainsAudioTrack`(BCL MP4 `hdlr soun`/MKV `A_*` 스캔) · `SourceAudioProbe` 주입
- 🎵 `enum ExportProfile` + `IsStockSuiteTarget(path, profile)` — HtsDesktop/DualMonitorSplit 만 `-an` 강제, SnsShorts/MobileVertical 면제 · `ProfileOf(key)` 멀티 패키지 매핑
- 🎞️ `GalleryViewModel.Begin/EndHoverPreview` · `ResolvePreviewTimestamp`(호버 위치 → 직전 위치 → 중간점 → 1초) → `PublishViewModel.Initialize(path, sec)` · MediaGallery `OpenPublish` 결선
- 🧪 `SecurityAudioFallbackGalleryTests` 3건 · `ScrubberSilentGuardEtaTests` 쇼츠 BGM 기대값 갱신

## Next
- 확대 모달 `<video>` 실제 재생 위치(JS interop) 전달
- 실 ffmpeg 기준 ETA 표본 보정(GPU/CPU 폴백 분리 가중치)
