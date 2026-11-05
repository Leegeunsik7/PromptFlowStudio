# Current Project Status - Active Milestone: 【Perf Pro】 🚀 FFmpeg GPU 가속(NVENC/QSV/AMF) · 🟢 StockSuite 실시간 프로세스 모니터 · 📢 SNS -14 LUFS 음향 정규화
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 586/586 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 🚀 `Services/Render/HardwareAccelDetector`(신규): 1회 `ffmpeg -encoders` 프로브 → NVENC → QSV → AMF 후보별 1프레임 실검증 인코드(범용 빌드 목록 오탐 차단) · 결과 캐시 · 실패/부재 시 CPU
- `StockSuiteExporter` MP4 재인코딩(1080p 고선명·4K·60fps): 감지 GPU 인코더 우선(`h264_nvenc -preset p4 -rc vbr -cq 22` / `h264_qsv -global_quality 22` / `h264_amf cqp 22`) → 실패 시 `MarkFailed()` 강등 후 libx264 무중단 폴백 · 성공 메시지 `🚀 {벤더}` 태그 · WebM(VP9)은 CPU 유지
- `StockSuiteExporter.Accel` 프로퍼티(null = CPU 전용) — DI 양쪽 `HardwareAccelDetector.Shared` 결선
- 🟢 `Services/Process/StockSuiteProcessMonitor`(신규 · ns `ProcessBridge` — `System.Diagnostics.Process` 해석 충돌 회피): 2초 Timer 폴링(재진입 차단) · PID/WorkingSet · 변화 시에만 `OnChanged` · 조회 예외 격리 · `BringStockSuiteToFront()` = `IsIconic`→`ShowWindow(SW_RESTORE)`+`SetForegroundWindow`(`#if WINDOWS`, 그 외 스텁)
- Workspace 상단 툴바 뱃지: `🟢 StockSuite 연동 활성 (PID: n)` 클릭 → 창 활성화 / `⚪ StockSuite 미실행` 클릭 → 원클릭 실행(경로 미설정 시 안내 토스트) · 구독/해제 쌍 · `InvokeAsync(StateHasChanged)`
- 📢 `AudioMuxService`: `-af loudnorm=I=-14:LRA=11:TP=-1.5,afade=…` + `-ar 48000`(loudnorm 192kHz 업샘플 방지) · 길이 미상 시 loudnorm 단독
- Tests: `PerfProGpuProcessLoudnormTests` 4종 (GPU 감지/폴백 · exporter GPU→libx264 폴백 · 프로세스 모니터 · loudnorm) + 기존 BGM 테스트 기대값 갱신
