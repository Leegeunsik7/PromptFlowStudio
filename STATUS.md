# Current Project Status
- Active Milestone: 【Pluto 4.1 하드닝 — Mobile iOS CA1416 `Process` 가드 · 브랜치 동기화】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile `net8.0-ios` CoreCompile 0W/0E
- Tests: ✅ 60/60 targeted PASS · ⚠️ 전체 스위트 3건 실패(아래, 베이스라인 동일)
- Updated: 2026-11-14 (KST)

## Completed
- 📱 iOS CA1416 `Process` 경고 5건 제거: `StockSuiteExporter.cs` · `PlutoCoreProcessManager.cs` · `StockSuiteProcessMonitor.cs`
- 🔀 `origin/main` · `origin/master` fast-forward 동기화
- ☁️ 3/4 `PlutoDriveHealthService` + Settings 백업 상태 카드 · 🪟 4/4 System.Drawing Windows 가드
- 🧵 크롤러 모달별 DI 루트 재생성·폐기 · Runner Dispose 경합 차단

## Known Issues (under investigation)
- 3 isolated tests (`LiveCommerceIsolatedTests`, `LiveAudienceEngagementIsolatedTests`, `PlatformVisibilityGatewayIsolatedTests`) fail at baseline `a8ed9e5` too, deterministically (`1112` allocations / `34.0 ms` median tick); likely an environment/local-state or .NET workload change since 17:32; under investigation.

## Next
- 위 3건 원인 규명(`TitanReleaseTests.cs` 폴링 경로 할당 1,112 B)
- `_omniPublisher` 단일 모달 피크(~1 GB) 원인 분석
