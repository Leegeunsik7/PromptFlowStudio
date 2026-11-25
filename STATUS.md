# Current Project Status
- Active Milestone: ✅ Subtask E — 재시도 Requeue/시계 축 통합 · 스탠바이/합성 폴백 게시 차단
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ Pluto/Autopilot/Quota/Reels 510/510(1 m 58 s) · 종단 2건 + 신규 5/5 · 전체 스위트 미실행
- Updated: 2026-11-25 (KST) · 기준 `41a9898` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `MergeableCompanions=2` 유지

## Completed — Subtask E
- 🔁 `SmartPlatformQuotaResilienceGuard`: `PumpDueReel`(첫 도래 릴만 — 무관 릴 시도 불변) · `Reinstate`(시도 보존 · 백오프 없음) · `Requeue` 시도 max 병합 · 호출자 시계 `Enqueue(…, now)` 오버로드 제거(가드 `Clock` 단일 축)
- 🧮 `ZeroTouchAutopilotMasterEngine`: 재시도 실패 = `Requeue`(시도 팩 4 bit/플랫폼 · MaxAttempts → GaveUp) · 취소/스탠바이 = `Park`(시도 미소비) · 임대 실패 = Reinstate(유실 0) · 미디어 축출 = `DroppedRetries`
- 🛰 `StandbyEngaged` 중 게시 보류(요청 타깃 재시도 큐 · 펌프 정지 · 업로드 도중 진입 시 잔여 타깃 보류) — `StandbyHolds`
- 🧪 합성 fMP4 폴백 클립(`FactoryReel.SyntheticSource` → `BurnedReel.Synthetic`) = 실 게시 차단(`PublishingSimulated` = `SimulatePublishing ?? _pub.Simulated`) — `SyntheticSkips`
- 🧪 `AutopilotRetryStandbyGuardTests` 5건

## Fast Loops
- Pluto(~30 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
