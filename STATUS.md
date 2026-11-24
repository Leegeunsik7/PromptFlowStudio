# Current Project Status
- Active Milestone: ✅ Step 45 — Pluto YouTube Analytics API v2 정밀 CTR · 유지율 동기화 · FastProbe 벽시계 테스트 `TimingSensitivePerformance` 재분류
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1433/1433 첫 실행 통과(10 m 32 s · 종료 코드 0) · Pluto 루프 458/458(34 s)
- Updated: 2026-11-24 (KST) · 기준 `2fad7e9` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 45
- 📊 `PlutoYouTubeApiSyncService` Analytics v2 `reports.query` — 요청 지표 공식명 `videoThumbnailImpressionsClickRate` · 파서는 레거시 `impressionsClickThroughRate` 병행 수용
- 🛡 안전장치 4: 보고 지연 창(태평양 오늘 − 2 · 게시일 가드) · 403 범위 채널 단위 강등(인증 정지 비전파) · Studio 실측 우선 순서 · 헤더 이름 매핑 스키마 검증
- ⏱ `RenderEngine_UnprobedOfflineComfyUI…FirstClick` → `EngineRoutingFastProbeTimingTests`(`TimingSensitivePerformance` · 5000 ms 불변) · 나머지 병렬 유지
- 🧪 테스트 1426 → 1433 · `PlutoYouTubeAnalyticsApiTests` 요청 URL 공식 지표명 단언
- 📘 `docs/rules_testing.md` "Pluto YouTube Analytics v2 정밀 지표 · FastProbe 벽시계 재분류 (Step 45)"

## ⚠️ 잔여
- 병렬 꼬리 하한 ≈ 140 s(사슬 작업 합 ÷ 2). 후보 = CinematicDirector · BroadcastMcr · GarmentDynamics 내부 반복 축소.
- 전체 실행 시간은 호스트 부하에 따라 변동(이번 10 m 32 s).

## Fast Loops
- Pluto(~34 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
