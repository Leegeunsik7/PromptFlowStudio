# Current Project Status
- Active Milestone: ✅ Step 44 — TitanRelease 순수 계산 19 메서드 A/B 사슬 분리 · 재균형(동시 중량 사슬 ≤ 2 유지) · LiveCommerce 창 단위 부하 표본 게이트
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1426/1426 첫 실행 통과(7 m 35 s · 종료 코드 0) · Pluto 루프 451/451(24 s)
- Updated: 2026-11-24 (KST) · 기준 `06d15ff` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 44 (기준 13 m 21 s 경합 호스트 / Step 43 8 m 6 s–9 m 44 s → 7 m 35 s)
- 🔀 `TitanReleaseTests`(ProcessGcMode) → `TitanReleaseChainATests` 8 · `TitanReleaseChainBTests` 11 메서드 · 전용 헬퍼 12 동반 이동 · 공유 2 `internal`
- ⚖️ 사슬: A 136 s / B 144 s(꼬리 148 s) · ProcessGcMode 303 → 138 s · Timing 168 s
- 🛡 `WindowContention` — 기준 초과 창 직후 부하 표본 최대값으로 판정(기준 임계 불변 · 유휴 탐침 0) → LiveCommerce 레이어 초과 창 게이트
- 🔒 소스 가드 +1: 사슬 분리 클래스 직렬 전용 요소 0 · `MinBytes` 재시도 창만 · GUID 임시 경로 · `TitanHeavy*` 정의 정확히 2 · 테스트 +2(1424 → 1426)
- 🧪 `PFS_ALLOC_FORCE_ALL_WINDOWS=1` 멱등성 감사 19/19 · 제외 3(라이브 FPS · 하트비트 창 · 동시 피크)
- 📘 `docs/rules_testing.md` "TitanRelease 사슬 분리 · A/B 재균형 · 창 단위 부하 표본 (Step 44)"

## ⚠️ 잔여
- 병렬 꼬리 95–105 s 미달성: 사슬 작업 합 ≈ 280 s ÷ 2 → 하한 ≈ 140 s. 다음 후보 = CinematicDirector 56 s · BroadcastMcr 48 s · GarmentDynamics 48 s 내부 반복 축소.
- 전체 실행 중 타 프로젝트 테스트/빌드 동시 실행 시 비병렬 구간 선형 지연(기준 실측 801 s).

## Fast Loops
- Pluto(~24 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
