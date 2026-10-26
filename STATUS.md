# Current Project Status
- Active Milestone: 렌더링 큐(Render Queue) 백그라운드 엔진 및 UI 결선 완료
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 598/598 PASS(직렬) · RenderQueueWorkerTests 4/4 · MassProductionAndCostTests 15/15
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-27 (KST)

## Completed Deliverables
- RQ-1: `Services/Render/IRenderQueueService` · `RenderQueueService` — 잠금 기반 큐 · Enqueue(중복 차단)/Cancel/Retry/ClearCompleted/Pause/Resume · 단일 워커 자동 기동 · 진행률 5% 틱 · CompletedAt
- RQ-2: `BatchRenderJob` ProgressPercent·CompletedAt, `BatchJobStatus.Cancelled` 추가
- RQ-3: `MassProductionViewModel.DispatchAllToRenderQueue` → `IRenderQueueService.Enqueue` 직결(RenderQueue = 전역 큐)
- RQ-4: `ViewModels/RenderQueueViewModel` — 대기/렌더/완료/실패 카운트 · ActiveJob · 스냅샷 목록
- RQ-5: `Blazor/Pages/RenderQueue.razor`(/queue) — 요약 HUD · 일시정지/재개 · 완료 정리 · 작업 표(상태 뱃지·진행률·✕/↺)
- RQ-6: DI `MainForm.cs`(서비스+VM) · `MauiProgram.cs`(서비스)
- 이전: 3번 대량 양산(MP-1~7) · 2번 스토리보드 · 1번 프롬프트 합성기

## Known Issues / 환경 노트
- 렌더 워커는 가상 파이프라인(시뮬레이션) — 실제 렌더 엔진 소비자 미결선
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 — 정식 배포 전 `-FfmpegPath` 지정 재빌드 필요
- Authenticode 미서명(인증서 미제공) · Android 빌드는 환경 제약으로 미검증
