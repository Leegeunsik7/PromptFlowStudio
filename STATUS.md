# Current Project Status
- Active Milestone: 3번 워크플로우 【대량 양산 & 비용】 · 【렌더링 큐】 백엔드/VM
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 593/593 PASS(직렬) · MassProductionAndCostTests 14/14
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-26 (KST)

## Completed Deliverables
- MP-1: `Services/Render/RenderCostEstimatorService.cs` — 480p/720p/1080p/4K 프로파일(VRAM·프레임 가중치·5초당 크레딧) · 0/음수/NaN 클램프
- MP-2: `Services/Batch/BatchMassProductionService.cs` — 줄 단위 BatchRenderJob 전개 · 빈 줄 제거 · 시드 +1 증가 · 무할당 CountPrompts
- MP-3: `ViewModels/MassProductionViewModel.cs` — 입력/해상도/길이 변경 시 비용·VRAM 재추산 · DispatchAllToRenderQueue(Queued 적재)
- MP-4: DI `MainForm.cs`(서비스 2 + VM) · `MauiProgram.cs`(서비스 2)
- MP-5: `PromptFlowStudio.Tests/MassProductionAndCostTests.cs` — 14케이스
- 이전: 2번 스토리보드(SB-1~6) · 1번 프롬프트 합성기 완료

## Known Issues / 환경 노트
- 대량 양산 Razor 화면 결선 미착수(VM만 등록) · RenderQueue → 실제 렌더 엔진 소비자 미결선
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 — 정식 배포 전 `-FfmpegPath` 지정 재빌드 필요
- Authenticode 미서명(인증서 미제공) · Android 빌드는 환경 제약으로 미검증
