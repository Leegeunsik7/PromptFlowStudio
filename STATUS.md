# Current Project Status
- Active Milestone: ✅ Step 39 — 원클릭 전 언어(KO · EN · JA · ES) 일괄 분기/패키지 · Stage-9 플라이휠 자가진단 · `TitanReleaseTests` 무할당 단언 정상 상태 측정
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1401/1401 전체 단일 실행(종료 코드 0 · 17 m 58 s · 첫 실행 통과) · Pluto 루프 438/438 · 신규 `PlutoMultiLocaleBatchBundleTests` 8 건
- Updated: 2026-11-23 (KST) · 기준 `c614542` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 39
- 🌍 `IPlutoMultiLanguageChannelService.BranchAndExportAllLocalesAsync` → `PlutoMultiLocaleBatchBundle`: 번역(용어집 · 고유명사 보존 · 오프라인 폴백) → 자막/TTS 0.85x–1.30x 적합 경고 → `{BaseClipId}::{Locale}` 멱등 분기 → 로캘 패키지 → `{base}_{locale}_publish_pack.txt` 묶음 저장 · 로캘별 실패/경고는 형제 계속
- 🖱️ Workspace `[🌍 전 언어 일괄 분기/패키지]`(KO · EN · JA · ES 칩 옆 · 상단 헤더 무변경) · 로캘별 요약 토스트
- 🧪 자가진단 9단계: ⑨ 기동 선조정 단일 비행/멱등 · 다른 PC 연결 ID 대기열 정리 · 진단 6섹션(§5 용어집 · §6 활동) 비밀값/경로 가림
- 🧷 `TitanReleaseTests`: 이미 `ProcessGcMode`(직렬) — 원인은 병렬 경합이 아닌 계층 JIT/OSR 1회성 전이 → `SteadyStateAllocation.MinBytes`(최대 4창 최소 0 B) · `docs/rules_testing.md` 갱신
- 🛡️ 자체 추가: ① 사이드카 묶음 전부-아니면-전무(사전 검증 → 스테이징 → 백업 교체 커밋 · 도중 실패 시 이전 묶음 복원) ② 게시(연결)된 로캘 동결(패키지/사이드카 재생성 금지 → 중복 업로드 · 오연결 차단) ③ 같은 마스터 일괄 단일 비행(Busy · 백업/복원 교차 차단)

## Fast Loops
- Pluto(438 건 · ~35 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
