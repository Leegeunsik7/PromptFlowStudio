# Current Project Status
- Active Milestone: ✅ Step 36 — YouTube Data API v3 쿼터 인지형 백그라운드 주기 자동 동기화(`PlutoYouTubeApiSyncService`)
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1379/1379 전체 단일 실행(종료 코드 0 · 17 m 18 s) · Pluto 루프 416/416 · 신규 `PlutoYouTubeApiAutoPollerTests` 9 건
- Updated: 2026-11-23 (KST) · 기준 `412e71d` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 36
- ⏱️ 주기 자동 동기화(기본 켬 · 30분 · 하한 5분) — `TimeProvider` 주입 단발 타이머 재무장 · 수동 `SyncNowAsync` 와 단일 실행 게이트 공유(겹치면 Busy 합류) · 수동 성공 → 다음 예정 한 주기 뒤로
- 💸 `quotaExceeded`/자체 예산 소진 → 태평양 자정+1분까지 정지(영속 · 재시작 유지) · 남은 예산 < 예비 100 unit → 자동만 정지(수동 가능) · 429/5xx → 실행 간 지수 백오프 5분×2ⁿ⁻¹(상한 6시간)
- 🔒 미설정 · `PROMPTFLOW_PLUTO_DRIVE=off`(실 네트워크) · `PROMPTFLOW_DISABLE_YOUTUBE_API=1` → 타이머 미생성 · 요청 0
- 🖥️ `Settings.razor` `[⏱️ API 주기 자동 동기화 ON/OFF]` 영속 토글 + 실시간 예정/백오프/쿼터 상태(`Changed` 구독/해제) · `Workspace.razor` 새 연결 · 대기열 회수 시에만 토스트(회로 폐기 안전)
- 🛡️ 자체 추가: ① 재시작 폭주 차단(영속 예정 · 2분 기동 유예 · 조기 발화 재무장 · 시계 되감김 상한) ② 인증 실패 정지(재저장 전 토큰/키 반복 타격 차단 · 영속) ③ 구성 세대 교체 — 연결 해제/재저장/끄기가 진행 중 자동 실행 취소 ④ 구독자별 예외 격리(`Changed` 포함) ⑤ 완료 이벤트를 실행 중 플래그 해제 뒤 발생

## Fast Loops
- Pluto(416 건 · ~25 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- 무거운 테스트 제외: `--filter "Speed!=Slow"` · CI 는 `RunConfiguration.DisableParallelization=true` 완전 직렬
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
