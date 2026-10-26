# Current Project Status
- Active Milestone: 3번 워크플로우 【대량 양산 & 비용】 UI 결선 완료
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 594/594 PASS(직렬) · MassProductionAndCostTests 15/15
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-27 (KST)

## Completed Deliverables
- MP-6: `Blazor/Pages/BatchStudio.razor`(/batch) — VM 주입 · 다중 프롬프트 oninput 바인딩 · 해상도(480p~4K)/길이/FPS/시드/자동 증분 · 비용 HUD(작업 수·VRAM·크레딧·소요 시간) · 작업 미리보기 표 · 렌더 큐 디스패치 + 토스트
- MP-7: `MassProductionViewModel` — Fps · EstimatedRenderTimeSec · GeneratedJobs(Pending 미리보기 → 디스패치 후 Queued) 추가
- MP-1~5: 비용 추산기 · 배치 디스패처 · VM · DI · 테스트(이전 커밋 ee9b041)
- 이전: 2번 스토리보드(SB-1~6) · 1번 프롬프트 합성기 완료

## Known Issues / 환경 노트
- RenderQueue → 실제 렌더 엔진 소비자 미결선
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 — 정식 배포 전 `-FfmpegPath` 지정 재빌드 필요
- Authenticode 미서명(인증서 미제공) · Android 빌드는 환경 제약으로 미검증
