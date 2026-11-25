# Current Project Status
- Active Milestone: ✅ Subtask B — 오토파일럿 중복 게시 차단 · AutoPublish/AutoUpload 진입 전 상태 복원
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ Pluto + Autopilot 470/470(1 m 9 s) · ✅ LiveToReels/Publisher/VisualPolish/Autopilot 8/8 · 전체 스위트 이번 변경 후 미실행
- Updated: 2026-11-25 (KST) · 기준 `7ac5fb0` · 불변식 `CorePlutoFileCount=9` · `MergeableHubs=6` · `CompanionSyncFiles=2` 유지

## Completed — Subtask B
- 🐛 `Plan()`: 릴스 가동 여부 무관 `AutoPublish`·`AutoUpload` 최초 1 회 스냅숏 → OFF(매 사이클 재확인) · `Disable()`/러너 `Stop()`/`Dispose()` 복원(멱등)
- 🐛 TikTok 경로: `Guard.Enqueue(id, media, legMask)` 신규 오버로드(제외 레그 = `Skipped`) → IG/YT 교차 중복 제거 · 큐잉 즉시 200 제거(레그 `Done` 확인 후 200, 실패 503, 미확인 504 = 재시도 큐 미등록)
- 🧪 `Autopilot_SuppressesAutoPublishAndAutoUpload_AndRestoresOnDisableAndStop` · `PublishingGuard_LegMask_UploadsOnlyRequestedLeg`
- ⏭ Subtasks C–E 미착수

## Fast Loops
- Pluto(~30 s): `dotnet test PromptFlowStudio.Tests/PromptFlowStudio.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~Pluto" -p:NuGetAudit=false`
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:/Program/.git --work-tree=D:/Program/PromptFlowStudio` 필요
