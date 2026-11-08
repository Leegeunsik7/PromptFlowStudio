# Current Project Status
- Active Milestone: 【Suite Refine】 📱 9:16 세로 단일 내보내기 · ⚡ WebM 텔레메트리 · 💾 이름 붙은 사용자 프리셋 영속 · 👁️ 1프레임 번인 미리보기
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 877/877 PASS (`-- RunConfiguration.MaxCpuCount=2`, 11m09s)
- Updated: 2026-11-08 (KST)

## Completed
- 📱 `ExportResolution.Res9_16` / `StockSuiteResolution.Vertical9x16`(1080×1920) — `crop=ih*9/16:ih,scale=1080:1920:flags=lanczos,unsharp=…` · 모달 `9x16` 옵션
- 📱 `FintechExportPreset.MobileShorts` → `Vertical9x16`
- ⚡ WebM VP9 경로 `EncoderTelemetry("libvpx-vp9", false, ms)` → `.meta.json`
- 💾 `UserPreferenceService.CustomPresets`(`export.customPresets` JSON) · `SaveCustomPreset(name, preset)` 이름 업서트(상한 20)
- 💾 `PublishViewModel.SaveCustomPreset(name)/ApplyCustomPreset` · 모달 프리셋 이름 입력 + `user:` 드롭다운 항목
- 👁️ `StockSuiteExporter.GeneratePreviewFrameAsync(src, ExportOptions)` — `-ss 1 -vframes 1` JPEG(경량 프로브 풀 자동 분류) · 모달 `[👁️ Preview Frame]` 인라인 썸네일
- 🧪 `SuiteRefineVerticalWebmPreviewTests` 4건

## Next
- 미리보기 실기 지연 측정(< 250ms 목표 · 4K/minterpolate 제외 체인)
- 크로스디졸브 실기 검증(WebView2 · 트림 덮어쓰기 핸들 해제 타이밍)
