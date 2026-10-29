# Current Project Status
- Active Milestone: 【보충 고도화】 4대 사각지대 해소 — 스토리보드 렌더 일원화 · 멀티 컷 합본 · 커머셜 로고 번인 · 비용 영속/취소 전파
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 681/681 PASS(직렬 11m34s) · FastUnit 449/449 PASS(병렬 ~31s)
- Updated: 2026-10-29 (KST)

## Completed
- 렌더 일원화: `Storyboard.razor` 전체 생성(`StartAll`) → `StoryboardViewModel.DispatchToRenderQueue` 단일 경로(앵커·MasterSeed+Index·VRAM 가드) · 레거시 `Jobs.StartAsync` 호출 제거
- 멀티 컷 합본: `FFmpegVideoStitchService` — 그룹 키(`stitch.group`) 전 컷 Completed 시 concat demuxer `-c copy` → `{project}_full.mp4` · ffmpeg 부재 시 `.stitch.json` 가상 병합 · Storyboard/AiShortsCutdown `🎬 Export Full Video`
- 커머셜 번인: `VideoEngineRenderExecutionEngine` 추론 직후 로고 overlay(세이프존 `W-w-10:H-h-10` 등)+디스클레이머 drawtext → 원본 경로 교체 · 메타 `commercial.overlayApplied`
- 비용 원장: `cost_ledger.json` 원자적 교체 영속 — 재기동 후 당월 누적·Budget Cap 차단 유지 검증
- 취소 전파: 큐 CTS → 엔진 ct(`ProcessRunner` KillTree) + ComfyUI `POST /interrupt`(2s 타임아웃·격리)

## Known Issues
- 미분류 1개(`ZzCrowdProbe.Probe`) — 전체 실행에만 포함
- 비용 예약(Reservation)은 메모리 전용 · 시뮬레이션 폴백 컷은 산출물 없음 → 합본 버튼 비활성
- concat `-c copy` 는 컷 코덱/해상도 동일 전제 · 모바일 `Blazor/Pages/` 미링크 · Android 빌드 미검증
