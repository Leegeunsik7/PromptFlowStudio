# Current Project Status
- Active Milestone: 4번 워크플로우 【24/7 AI 라이브 스튜디오】 쇼호스트 엔진 및 UI 결선 완료
- Build Status: Release 빌드 (-warnaserror 0W/0E) — PromptFlowStudio · PromptFlowStudio.Tests · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 전체 602/602 PASS(직렬) · LiveStudioHostTests 4/4 · RenderQueueWorkerTests 4/4
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-27 (KST)

## Completed Deliverables
- LS-1: `Services/Live/LiveStudioHostService` — LiveProductSpec → 5단계 피치(Hook~CTA) · Wan 2.1 VisualPrompt · 카메라 무빙 · 금지어/과장광고 대체 보정
- LS-2: 24/7 릴레이 `GetNextSegment` — 피치 소진 시 FAQ ↔ BGM 루프 컷 교대 후 재적재(null 반환 없음)
- LS-3: `ViewModels/LiveStudioViewModel` — 상품 입력 · 1초 틱 가상 송출 · `SendToRenderQueue` → `IRenderQueueService.Enqueue`
- LS-4: `LiveStreamingStudio.razor`(/live-studio) 상단 3분할 — 상품 입력 · ON AIR 뷰파인더/텔레프롬프터 · 피치 타임라인(렌더 큐 내보내기)
- LS-5: DI `MainForm.cs`(서비스+VM) · `MauiProgram.cs`(서비스)
- 이전: 렌더 큐(RQ-1~6) · 3번 대량 양산 · 2번 스토리보드 · 1번 프롬프트 합성기

## Known Issues / 환경 노트
- `SingleFileBinaryProcessBootstrap_Check`(5초 프로세스 스모크) 직렬 전수 실행 중 부하성 타임아웃 1회 관측 — 재실행 PASS
- 렌더 워커·라이브 송출 피치는 가상 파이프라인(시뮬레이션) — 실제 렌더/아바타 소비자 미결선
- 모바일: `ViewModels/`·`Blazor/Pages/` 미링크 → `MauiProgram.cs` VM 미등록
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 · Authenticode 미서명 · Android 빌드 미검증
