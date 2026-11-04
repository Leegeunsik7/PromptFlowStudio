# Current Project Status
- Active Milestone: 【멀티 듀레이션】 5s/10s/15s 파이프라인 + I2V 앵커 체이닝 + 15s 앰비언트 슬로우 루프
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 739/739 PASS(직렬 ~14m) · FastUnit 507/507 PASS(병렬 ~30s)
- Updated: 2026-11-04 (KST)

## Completed
- 모델: `VideoDurationMode{Sec5_Single,Sec10_Dual,Sec15_Triple,Sec15_AmbientSlow}` · `BatchRenderJob.DurationMode/TargetDurationSeconds/IsAmbientLoop/StitchGroupId`
- 계획기: `Services/Render/MultiDurationPlanner` — 5초 하위 컷 2~3개 전개(동일 그룹 키 · 빈 서브 프롬프트는 메인 상속 + 파트별 카메라 모션 차등) · 출력 `{project}_10s/_15s/_ambient15s.mp4`
- 큐: `IRenderQueueService.EnqueueJob` 원자 적재 · 컷 완료 시 `ffmpeg -sseof -0.1 … -vframes 1` 마지막 프레임 → 다음 컷 `ReferenceImagePath`(I2V) 주입 · 전 컷 완료 시 `StitchCutsAsync` 자동 합본(그룹당 1회 · `OnDurationStitched`)
- 앰비언트: `AmbientLoopService` — `setpts=3.0*PTS,minterpolate='fps=30:mi_mode=mci'` · 옵션 핑퐁(1.5x + reverse concat) · 무음 15초 · 엔진 후처리 결선
- 비용: `ProductionCostTrackerService.EstimateCost(job)` 가중치 1x/2x/3x/1x(하위 컷은 컷당 1회분)
- UI: Workspace 듀레이션 칩 4종 + 파트 서브 프롬프트 · 갤러리 모달 `🔁 무한 반복 재생` 토글(기본 on)

## Known Issues
- 5s 단일은 기존 직접 생성 경로, 10s/15s/Ambient는 렌더 큐 경유(진행은 큐 화면에서 확인)
- 실 ffmpeg/엔진으로 체이닝·minterpolate 품질 미검증(주입 실행기 테스트) · 번들 YouTube OAuth 클라이언트 값 공란 유지
