# Current Project Status
- Active Milestone: ✅ Step 45.1 — 일관성 · 가림 처리 패치(10단계 문구 · §1~§6 툴팁 · 예외 토스트 가림 · 토글 실효 상태 · 403 재확인 · 마스터 불일치 경고)
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1440/1440 첫 실행 통과(Release 직렬 · 17 m 23 s · 종료 코드 0) · Pluto 루프 465/465(28 s)
- Updated: 2026-11-25 (KST) · 기준 `97b3f62` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Step 45.1
- 📘 `CLAUDE.md` 테스트 명령 = CI Release 직렬 명령
- 🧪 자가진단 문구 7단계 → 10단계(Settings · SelfTest 서비스 · MainForm/MauiProgram 주석) · 진단서 복사 툴팁 §1~§6(§5 용어집 · §6 최근 활동)
- 🔒 예외 토스트 `ex.Message` → `PlutoAutonomousActivityFeedService.Redact`(Settings 진단 수집 · Workspace 전 언어 일괄/렌더)
- 🔀 토글 = 실효 상태(`EffectiveOn` · `ToggleStateKorean`) — 킬 스위치/격리/미설정 시 'ON' 표시 안 함
- 🔎 `RecheckingScope` — 24h 403 래치 만료 후 성공 조회 전까지 '활성' 승격 금지 · 성공 조회 시 채널 래치 해제
- ⚠ 전 언어 일괄 렌더 — 카탈로그 마스터 ↔ 현재 작업 공간 지문 불일치 시 렌더 전/후 토스트 경고
- 🧪 1433 → 1440(`PlutoConsistencyAndRedactionTests` 6 · `PlutoYouTubeAnalyticsApiTests` +1)

## Fast Loops
- Pluto(~28 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
