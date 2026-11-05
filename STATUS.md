# Current Project Status
- Active Milestone: 【Diagnostics】 HTS 3대 테마 HUD · 미디어 규격 인스펙터 · 렌더 캐시 자동 정리기
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 567/567 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- HtsHudThemes(`Services/Export/HtsHudTheme.cs`): OLED 딥블랙 · 네이비 퀀트(10단 호가) · 모던 미니멀(라인+볼륨) — CSS 클래스/호가 깊이/파서
- 조도 검사기: `Evaluate(luma, dimmer, theme)` WCAG 대비율 4.5:1 기준 🟢 최적 / 🟡 주의(Dimmer N% 권장 · 원클릭 적용)
- HtsHudOverlay: `Theme` 파라미터 · `RootClass(theme)` · 테마별 CSS · 미니멀 라인 차트/볼륨 바(정적 데이터)
- MediaProbeService: ffprobe JSON → ffmpeg `-i` 배너 → 헤더 폴백 · 코덱(H.264 (avc1)/VP9)·해상도@FPS·Mbps·MB·무음 · 중앙 YAVG 조도 측정(10s 타임아웃)
- Workspace: `[ℹ️ 미디어 규격]` 카드 · HUD 테마 드롭다운 · `[🔆 조도 검사]` 뱃지
- RenderCacheCleaner: `_temp_*`·`*_temp*.mp4`·`*.tmp`·`concat_*.txt`·`synth_*.wav` 회수(진행 중 보호) · 상한 초과 시 `.meta.json` 없는 오래된 미디어 LRU Prune · 앱 시작 백그라운드 실행
- UserSettings: 최대 렌더 캐시 용량(GB, 기본 10) · `[🧹 임시 캐시 즉시 비우기]` — DI: MainForm + MauiProgram
- Tests: MediaProbeService_ExtractsVideoSpecsCorrectly · RenderCacheCleaner_RemovesOrphanTempFilesSuccessfully · HtsHudOverlay_SwitchesThemesCorrectly 외 3건

## Next
- 조도 검사 실영상(ffmpeg signalstats) 실기 검증 · Android 빌드 검증
