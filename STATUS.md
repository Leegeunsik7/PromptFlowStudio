# Current Project Status

- Active Milestone: 【Pluto Timeline SFX Sound Director】 🔔 절차적 SFX 합성 · 3막 큐 시트 · BGM+SFX `amix` 멀티트랙 먹싱
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: `~Sfx|~Pluto|~BabyBgm|~StockSuite` 108/108 · `~Export|~Audio|~Baby` 124/124 PASS (호스트 RAM 제약으로 전체 스위트 미실행)
- Updated: 2026-11-10 (KST)

## Completed
- 🎹 `PlutoAudioSynthesizer` — 48kHz/16-bit/스테레오 PCM WAV(44B RIFF) · ADSR + 사인 배음 + LPF 노이즈 · 결정론 · −1 dBFS 피크
  - 📸 `ShutterClick` 0.15s · ✨ `ChimeSparkle` 0.8s(오르골 아르페지오) · 🌙 `BedtimeTone` 1.2s(디튠 벨)
- 🎬 `PlutoSfxDirector`(DI 싱글톤 `Shared`, MainForm·MauiProgram) — 800ms/0.7 · 5500ms/0.6 · 13200ms/0.55 · 길이 초과 큐 제외
- 🎚 `StockSuiteExporter.IncludeSfx` — `[1:a]loudnorm…[bgm]; [n:a]volume,adelay=ms|ms[dX]; amix=inputs=N:normalize=0 → alimiter 0.89 [aout]`
  - SFX 합성/믹싱 실패 → 경고 로그 후 BGM 단독 폴백 · 임시 WAV `finally` 정리 · 메시지 `🔔 감성 효과음 · N큐`
- 🖥 Workspace `🔔 감성 효과음(SFX)` 토글 · PublishModal 체크박스(기본 ON · BGM ON 시 적용)

## Next
- 실 FFmpeg 필터그래프 검증(로컬 ffmpeg 부재 — `amix normalize`는 FFmpeg ≥4.4 필요, 미지원 시 BGM 폴백)
- 모바일 빌드 검증(Android SDK 확보 시) · 전체 스위트 회귀(여유 RAM 확보 시)
