# Current Project Status
- Active Milestone: ✅ Step 43 — 비병렬 컬렉션(`ProcessGcMode` · `TimingSensitivePerformance`) 클래스별 감사 · 안전 이관 · 숨은 위해 요소 7 건 강화
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1424/1424 전체 통과(9 m 44 s · 종료 코드 0 · 시도 실행 8 m 6 s) · Pluto 루프 449/449(25 s)
- Updated: 2026-11-24 (KST) · 기준 `225c7bd` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 43 (같은 세션 기준 14 m 14 s 경합 / 8 m 40 s 유휴 → 8 m 6 s–9 m 44 s · 비병렬 472 → 342–432 s)
- 🔀 이관 16 클래스: ProcessGcMode 12(Garment · FluidWetness · SkinOptics · StrandHair 등) · Timing 2(MasterPipelineRealRunner · LiveDubbing) → A/B 사슬
- ✂️ 게이트 분리 2: CinematicDirector · BroadcastMcr — 결정론 단언은 사슬, 동일 임계 벽시계 예산은 `*FrameBudgetIsolatedTests`(비병렬) · 테스트 +2
- 🧪 실측 폐기: 3 번째 사슬 C → 동기 CPU 중량 4 개가 xUnit 슬롯 4 개 전부 점유 · 경량 async 연속 ≈ 45 s 정지 · 타임아웃 5 건 → **동시 중량 사슬 ≤ 2** 규칙
- 🛡 강화: AvatarActing 드레인 1.9 ms 단발 게이트(기준 실패) → 동일 백로그 최선 4 창 · BroadcastMcr 단발 무할당 창 → 4 창 최솟값 · ProcessRunner 기동 실패(-1) 유한 재시도 · Pluto 재연결 다운 창 2 → 5 s
- 🔒 유지(감사 결과): Gaussian* · FacialPerformance · AcesColorScience · DexterousHand · NeuralVoice µs 게이트 · GC 전역 지표 단언 · 모달 크롤러 DI · BroadcastMasterControl 단발 창
- 📘 분류 기준 5 항 + 측정값: `docs/rules_testing.md` "비병렬 컬렉션 감사 · 안전 이관 (Step 43)"

## ⚠️ 잔여
- LiveCommerce 레이어 초과 창 게이트는 호스트 경합 민감(기준 실행 1 회 실패 · 미변경) — 전체 실행 중 타 빌드/테스트 프로세스 금지.
- 다음 후보: 병렬 구간 꼬리 B 사슬 ≈ 140–150 s → A/B 재균형 · `TitanReleaseTests`(128–165 s) 내 I/O 위주 메서드 분리.

## Fast Loops
- Pluto(~25 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
