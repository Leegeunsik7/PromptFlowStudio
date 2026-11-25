# Current Project Status
- Active Milestone: ✅ MCP/lockfile 테스트 정합 — `16d692b` playwright MCP 제거 후 chrome-devtools 단독 구성
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 전체 스위트 1463/1463 통과(직렬 · 17 m 47 s · 실패 0)
- Updated: 2026-11-25 (KST) · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `MergeableCompanions=2` 유지

## Completed
- 🧪 `SecurityAndTelemetryTests.cs`: `.mcp.json` playwright 부재 단언(3곳) · `McpConfig_HasChromeDevtoolsOnly_AsValidJson` 개명
- 📌 `McpLockfileIsolationTests.Pins` = `chrome-devtools-mcp@1.10.1` 단일(package.json ↔ lock 정확 핀 일치)

## Fast Loops
- Pluto(~30 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
