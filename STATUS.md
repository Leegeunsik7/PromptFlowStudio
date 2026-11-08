# Current Project Status
- Active Milestone: 【Suite Refine】 🎞️ 스크러버 동기 미리보기 · 🔇 StockSuite Silent Ambient Guard · ⏱️ 동적 큐 ETA
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 880/880 PASS (`-- RunConfiguration.MaxCpuCount=2`, 14m13s)
- Updated: 2026-11-08 (KST)

## Completed
- 🎞️ `PublishViewModel.PreviewTimestampSec`(기본 1.0) → `ExportOptions.AtSeconds` → `-ss` · 작업실 `OpenPublish` 가 동일 영상 재생 위치 주입
- 🧹 `StockSuiteExporter.CleanupPreviewFrames()` · `PreviewSessionFiles` — 새 미리보기 요청/모달 닫기/Dispose 시 임시 JPEG 삭제 · 파일명 `preview_{name}_{mm-ss-fff}.jpg`
- 🔇 `EnforceSilentAmbient(args)` — StockSuite 폴더 대상 ffmpeg 호출 오디오 매핑/코덱/필터 제거 + `-an` 강제 · `IsStockSuiteTarget(path)`
- 🔇 `StockSuiteExportResult.HasAudio` → `.meta.json` `hasAudio:false`(원본 복사 폴백만 true) · `ExportMetadata.ReadHasAudio`
- ⚠️ PublishModal BGM 선택 시 `⚠️ StockSuite backgrounds must be silent. BGM will be stripped.`
- ⏱️ `RenderQueueService.Eta.cs` — 롤링 8표본 · 가중치 4K/32:9 1.8× · 1080p60 1.0× · 9:16/1080p30 0.7× · `EstimatedRemainingTime/CompletionTime` · RenderQueue 헤더 `⏱️ ETA: ~mm:ss (Est. HH:mm:ss KST)`
- 🧪 `ScrubberSilentGuardEtaTests` 3건

## Next
- 실 ffmpeg 기준 ETA 표본 보정(GPU/CPU 폴백 분리 가중치)
- 미리보기 실기 지연 측정(< 250ms 목표)
