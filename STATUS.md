# Current Project Status
- Active Milestone: 【Trim-Governance】 🔓 트리머 파일 락 해제 핫스왑 · 🗂️ 트림 메타데이터 동기화 · 🛡️ Vfx/Media/Comfy IProcessRunner 수렴
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile 미빌드: 공유 파일은 선택적 매개변수만 추가)
- Tests: FastUnit 616/616 PASS (Release)
- Updated: 2026-11-05 (KST)

## Completed
- 🔓 `promptflow.js`: `promptflow.releaseVideoSource`(src 제거 + `load()` → OS 핸들 해제) · `reloadVideoSource`(`?v=ticks` 캐시 버스트 · 0초 재마운트)
- 🔓 `VideoTrimService.TrimInPlaceAsync(…, VideoHandleHooks)`: 임시 컷(재생 유지) → Release → 원자 교체(공유 위반 8×120ms 재시도) → Reload(성공/원본) · 훅 예외 격리 · `Workspace.razor` 결선
- 🗂️ `.meta.json` `durationSeconds`(min(end,원본)−start) + `trimHistory[{start,end,trimmedAt}]` · 새 클립은 원본 메타 계승(즐겨찾기/내보냄 제외)
- 🗂️ `GalleryMediaService.NotifyMediaMetadataChanged`/`MediaMetadataChanged` · `GalleryItem.DurationSeconds` · 갤러리 카드 `⏱` 재생시간 뱃지 · VM 자동 갱신(새 클립 재스캔)
- 🛡️ `IProcessRunner.StartStreaming`/`RunGatedAsync`(기본 구현 · 바이너리 rawvideo 파이프 우선순위+장중 게이트) · `RunExitCodeAsync` 확장
- 🛡️ Vfx(`VfxProcess`·리라이팅·보간·업스케일) · Media(`ShortsCoverService`·`PreExportQaSentinel`·`AutomatedVideoQaInspector`) 직접 `new Process` 0 · Comfy 계열 0 확인
- 테스트 `TrimGovernanceTests`(3건 · 소스 가드 포함)

## Next
- 잔여 Wan/Storyboard/Broadcast/Spatial/Enterprise 등 직접 `Process` 호출부 단계적 이관(약 40곳)
