# Current Project Status
- Active Milestone: ✅ Step 37 — 호스트 기동 백그라운드 자율 데몬(`PlutoBackgroundAutonomyBootstrapper`) · 통합 자율 활동 피드(`PlutoAutonomousActivityFeedService`)
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1386/1386 전체 단일 실행(종료 코드 0 · 12 m 28 s) · Pluto 루프 423/423 · 신규 `PlutoStartupBootstrapperAndActivityFeedTests` 7 건
- Updated: 2026-11-23 (KST) · 기준 `ef9f1e7` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 37
- 🚀 `MainForm.OnShown` · `MauiProgram` 빌드 직후 `StartInBackground()` — Workspace 진입 없이 Downloads 감시 · API 주기 동기화(설정 + 켬일 때) · 기획 채점 기동 · 단일 비행 멱등(Workspace 재진입은 합류 · 예정 재설정 없음)
- 🔒 `PROMPTFLOW_PLUTO_DRIVE=off` → 전부 차단 · `PROMPTFLOW_DISABLE_DOWNLOADS_WATCHER=1` / `PROMPTFLOW_DISABLE_YOUTUBE_API=1` 개별 차단(기동 시 재평가 · 주입 환경 읽기)
- 🛰️ 피드 최근 30건 · `PlutoLocalOnly/pluto_autonomous_activity.json`(Drive 허브 아님) · API 새 연결/회수/정지 · Downloads 수집(+`Pluto_Ingested/`) · 채점 · −12pt 사후 분석 · +8pt 복권
- 🖥️ `Settings.razor` 최근 자율 활동 피드 + `[🧹 활동 로그 비우기]` · `Workspace.razor` 두뇌 리포트 본문 최근 활동 1줄(상단 3행 무변경) · 수동 동기화도 기록
- 🛡️ 자체 추가: ① 호스트 단일 구독 + 영속 중복 키 ② 유예 중 폐기 → 데몬/구독 부활 차단 · 기동은 스레드 풀 ③ 비차단 기록(감시기 수집 게이트 무차단) · 영속 실패 격리 ④ 비밀값 우선 → 경로 가림 · 적재 시 재가림 · 클라우드 경로 거부

## Fast Loops
- Pluto(423 건 · ~27 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
