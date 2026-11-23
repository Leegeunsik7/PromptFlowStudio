# Current Project Status
- Active Milestone: ✅ Step 35 — xUnit 스위트 실행 가속(컬렉션 병렬 · 크롤러 병렬 워커) · 부하 인지 타이밍 예산 확대 · `Speed=Slow` 트레잇
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1370/1370 — 전체 단일 실행 통과(종료 코드 0 · `build-server shutdown` 후) · 테스트 수 불변
- 전체 실행 시간: 기준 `cb9a9cf` **23 m 59 s**(직렬 · 1367/1370, 프로세스/파일 잠금 플래키 3) → **15 m 04 s**(−37 %)
- Updated: 2026-11-23 (KST) · 기준 `cb9a9cf` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 35
- ⚡ `xunit.runner.json` 전역 직렬(`parallelizeTestCollections=false`) 해제 → 컬렉션 병렬 4 스레드 · 위험군은 `DisableParallelization` 컬렉션(병렬 구간 후 단독)
- 🧭 신규 컬렉션: `ProcessEnvironment`(환경 변수 변경) · `TitanHeavyCompute`(CPU 집약 엔진 15 종 상호 직렬 · 경량 테스트와는 병렬) · 벽시계·고정 지연 클래스 16 개 → `TimingSensitivePerformance`
- 🕷️ 모달 크롤러 4 m 20 s → ~1 m 30 s(모달 전용 루트 3 워커 · 결과 카탈로그 순 집계 · 175/175 · 디스패치 4141 ≥ 기준 4138 · 결함 0)
- 🧪 `ZzCrowdProbe` 단언 0 건 13 s 덤프 → 1 s 실단언(유한성 · 월드 경계 · 활성 수) · 장기 덤프는 `PFS_CROWD_PROBE_DUMP=1`
- ⏱️ 부하 인지 예산: `SemanticChatWeather`(p95/p99 `Scaled` · 제안/수용률 `CiFrameBudget.MeetsFloor`) · `OmniLiveRestream`(먹스 50 ms · 리액션 150 ms) · 클리퍼 틱 평균 + 단일 최대값 게이트 8 곳
- 🛡️ 자체 추가: ① 숨은 전역 직렬 락 해제 ② 스레드 풀 기아 가드(최소 = 코어 × 2 · 자가 튜닝 상한 코어 × 4 와 충돌 방지) ③ CPU 집약 스위트 상호 직렬화 ④ Drive 프로브 40 ms 창 레이스 → 2 s 창 + 재연결 폴링
- `LiveCommerce` 리액션 100 ms 는 틱 기반(결정론) — 프레임 게이트는 기존 `CiFrameBudget` 유지

## Fast Loops
- Pluto(407 건 · ~45 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- 무거운 27 건 제외: `--filter "Speed!=Slow"` · CI 워크플로는 `RunConfiguration.DisableParallelization=true` 로 계속 완전 직렬
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
