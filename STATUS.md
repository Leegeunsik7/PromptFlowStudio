# Current Project Status
- Active Milestone: 【무이음새 앰비언트 핑퐁 + 환경설정 Drive 미러】
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1109/1109 PASS (SettingsManager 미러/복원 신규 · AmbientCloudLoopTests 갱신)
- Updated: 2026-11-13 (KST)

## Completed
- 🔁 Cloud 앰비언트 핑퐁 ☑ → 2세그먼트 `setpts=1.5*PTS,fps=30,split=2 · reverse · concat=n=2` = 정 7.5s + 역 7.5s = 15.00s (t=0 ≡ t=15 프레임, `<video loop>` 무이음새)
- 🔁 핑퐁 ☐ → 기존 `xfade fade 0.75` ×2 + `tpad` + `trim=15` 유지
- 💾 `AppSettingsManager`: `user_settings.json` 로컬 부재 + `G:\내 드라이브\DATA\PromptFlowStudio\user_settings.json` 존재 시 시작 시 자동 복원(로컬 존재 시 덮어쓰기 금지 · 손상 미러 거부)
- 💾 저장 성공 시 미러로 원자 복제(tmp → `File.Replace`), 실패는 `LastMirrorError`만 기록. 경로 해석은 `MemberRegistryService.ResolveMirrorDir()`(`PROMPTFLOW_AUTH_MIRROR` / off) 공유

## Next
- Drive 미러 상태(`LastMirrorError`)를 환경설정 화면에 노출
