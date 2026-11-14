# Current Project Status
- Active Milestone: 【Pluto 4.1 하드닝 — 3/4 Drive 헬스 · 4/4 Windows 가드 · 크롤러 누수 제거】
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1113/1113 PASS (11 m · `InteractiveModalCrawlerTests` 175 모달 결함 0)
- Updated: 2026-11-14 (KST)

## Completed
- 🎬 1/4 `PlutoMontageStoryGenerator` 3막 자막 · `SubtitleCue` 소수초 SRT
- 🔁 2/4 `SwapCandidate` · `MoveSelectedCut` · Workspace `[선별 상세]` HITL
- ☁️ 3/4 `PlutoDriveHealthService`(DI 양쪽) + Settings 백업 상태 카드(7파일 · `LastMirrorError`)
- 🪟 4/4 Pluto 색/초점/큐레이션 System.Drawing `OperatingSystem.IsWindows()` 가드(CA1416)
- 🧵 크롤러: 모달마다 루트 DI 재생성·폐기(Runner Stop+Join) + GC → 4 GB/154 스레드 → 피크 ~1.0 GB/58 · 종료 525 MB/32
- 🛡 `LiveToReelsFactoryStudioRunner`·`StreamGatewayClipperStudioRunner` Dispose 경합(해제된 WaitHandle → testhost 크래시) 차단

## Next
- Mobile(MAUI) 빌드 검증(4/4 가드 실기)
- `_omniPublisher` 단일 모달 피크(~1 GB) 원인 분석
