# Current Project Status
- Active Milestone: ✅ Workspace 예외 토스트 일괄 리댁션 — 잔여 `ex.Message` 46곳 → `PlutoAutonomousActivityFeedService.Redact`
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ Pluto+Workspace+Redact 필터 482/482 통과(57 s · 실패 0)
- Updated: 2026-11-25 (KST) · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `MergeableCompanions=2` 유지

## Completed
- 🔒 `Workspace.razor`: 몽타주·A/B·시리즈·저널·기획·배치·립싱크·로케일 등 모든 예외 토스트 `Redact(ex.Message)` 경유(원시 `ex.Message` 0)
- 🧪 `PlutoConsistencyAndRedactionTests.WorkspaceExceptionToasts_AllRouteThroughRedact_AndMaskPathsAndSecrets`: 정규식 잔여 0 단언 + 경로/토큰 마스킹

## Fast Loops
- Pluto(~30 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
