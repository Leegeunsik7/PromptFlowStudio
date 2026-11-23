# Current Project Status
- Active Milestone: ✅ Step 41 — `TitanReleaseTests` 무할당 단언 전량 정상 상태화 · `GovernanceOptTests` 외부 실행 안정화 · 플라이휠 자가진단 Stage 10(전 언어 일괄 렌더 · 사이드카 짝)
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1422/1422 전체 단일 실행 1회차 통과(종료 코드 0) · Pluto 루프 449/449 · 강제 전 창 감사(`PFS_ALLOC_FORCE_ALL_WINDOWS=1`) 102 클래스 통과
- Updated: 2026-11-24 (KST) · 기준 `92f6710` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 41
- 🧷 `TitanReleaseTests.cs` 원시 무할당 단언 148 건 → `SteadyStateAllocation.MinBytes` 144 · 무람다 `Probe` 4(stackalloc Span 창) — 잔여 원시 10 건은 프레임 게이트 누적 · `await` 창 · 비 0 차분 측정(의도적 제외)
- 🧪 `SteadyStateAllocation` 확장: `MinBytes(창, out windows)` · `MinBytes(창, beforeRetry, out windows)` · `Start()/Probe` · `Sample` · 강제 전 창 감사 스위치
- 🛡️ 자체 발견: ① 재시도 창의 상태 전진(카운터 · 시퀀스 · 가상 시계 · RANSAC 난수) 11 건 → 창 수 배수 단언/쌍둥이 인스턴스 ② 소비형 창 공허한 통과 → 재시도 전 복원 ③ 누적 Stopwatch → `Restart()` ④ Stage 10 고정 마스터 ID 의 프로세스 전역 단일 비행 충돌 → 실행별 고유 ID
- 🔁 `GovernanceOptTests` → `[Collection("ProcessEnvironment")]` + 검증 통과 대상만 3회 유한 재시도(무효 대상 재시도 0)
- 🎬 Stage 10: 주입 `ftyp` 페이크 렌더러 · KO/JA/ES 렌더 · EN 게시 동결(바이트 불변 · 렌더러 미호출) · `{base}_{locale}.mp4` + `_publish_pack.txt` 짝 · 잔여물 0 · 카탈로그 결선

## Fast Loops
- Pluto(~30 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- 무할당 멱등성 감사: `PFS_ALLOC_FORCE_ALL_WINDOWS=1` + 대상 클래스 필터
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
