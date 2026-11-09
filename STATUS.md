# Current Project Status

- Active Milestone: 【Baby Studio Step 7】 🎵 단일 MP4 BGM 먹싱 · ⏱️ 동적 페이드아웃 · 🎧 캔버스 BGM 미리듣기
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 909/909 PASS (MaxCpuCount=2)
- Updated: 2026-11-09 (KST)

## Completed
- 🎵 BGM ON(`ExportProfile.BabyCinematic`) → 주 산출물에 직접 먹싱(임시 `.~bgm` → 원자적 교체) · `_baby_bgm.mp4` 동반 파일 제거
  - Silent Ambient Guard(`-an`)는 HTS/듀얼 모니터 프로필에만 적용 · 먹싱 실패 시 무음 산출물 보존
- ⏱️ `MediaProbeService.GetDurationAsync` → `afade=t=out:st={길이-1.5:F2}:d=1.5`(0 클램프 · 길이 미상 시 생략)
- 🎧 `pfsBgmPreview` — 캔버스 `video.ws-video` 재생/일시정지/시킹/종료에 0.25 볼륨 동기(듀얼 버퍼 재결선)
- 🧪 `BabyBgmAndGrowthBadgeTests` 6건(단일 파일 먹싱 · 페이드아웃 계산 · 가드 면제)

## Next
- 실제 ffmpeg 기반 오디오 스트림 통합 검증(ffprobe 스트림 카운트)
- 모바일 빌드 검증(Android SDK 확보 시)
