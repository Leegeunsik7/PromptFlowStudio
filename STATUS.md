# Current Project Status
- Active Milestone: 【작업실 UX】 인라인 큐 진행률 + FFmpeg 원클릭 설치 + 앰비언트 고속 필터 + 📈 핀테크 프리셋
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 746/746 PASS(직렬 ~10m) · FastUnit 514/514 PASS(병렬 ~45s)
- Updated: 2026-11-04 (KST)

## Completed
- 작업실: `Services/Render/MiniQueueProgress` — 적재 JobId 집계(컷 n/N·%) → `🎞️ 합본 중` → 완료 시 Workspace 인라인 `<video>` 자동 재생 · 합본 실패 `OnDurationStitched` 표시 · `RenderQueue.OnChange` 구독/해제
- FFmpeg: `FfmpegAutoDownloaderService` — Gyan.dev→GitHub BtbN 미러 페일오버 · 2회 지수백오프 · 10분 타임아웃 · SHA-256 `FixedTimeEquals` · 파일명 기준 추출(Zip-Slip 차단)+MZ 검증 → `tools/ffmpeg/bin/` · `/app-settings` ⬇️ 버튼+진행바(250ms 스로틀) → 재탐색 `정상 설치됨 🟢`
- 앰비언트: 기본 `setpts=3.0*PTS,fps=30`(minterpolate 제거) · 핑퐁 기본 on(1.5x+reverse concat) · `highQuality` 옵트인으로 기존 minterpolate 보존
- 인스피레이션: `StoryPresetCategory.FintechTrading` 5종 + `FintechNegative` 칩(프리셋 적용 시 네거티브 자동 결합 · 중복 차단)

## Known Issues
- `LiveToReelsFactoryIsolatedTests` 직렬 전체 실행 1회 실패 후 재실행·단독 통과 — 순서 의존 플래키 의심(본 변경과 무관 파일)
- 실 네트워크 다운로드·실 ffmpeg 고속 필터 품질 미검증(주입 HttpHandler/실행기 테스트) · 번들 YouTube OAuth 클라이언트 값 공란 유지
