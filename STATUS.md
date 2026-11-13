# Current Project Status
- Active Milestone: 【사이드바 클릭 가로채기 수정 · 회원 원장 미러】 InputFile 오버레이 격리 + MemberRegistry 미러 백업/복원
- Build: ✅ Release `-warnaserror -p:NuGetAudit=false` 0/0 (PromptFlowStudio · Tests)
- Tests: ✅ 1096/1096 PASS (MemberRegistryMirror 필터 5/5)
- Updated: 2026-11-13 (KST)

## Completed
- 🐛 Workspace: 인라인 업로드 버튼(📸 스토리 · 🎞️ 몽타주 · 🎵 BGM)에 `ws-file-pick`(relative · inline-flex · overflow hidden) → 투명 `<InputFile>`이 버튼 밖(사이드바)으로 번져 파일 대화상자를 열던 버그 차단
- 🧱 `app.css` `.sidebar` `position: relative; z-index: 30` 스태킹 가드(모바일 드로어 fixed/1200은 `site-ui-advanced.css`가 우선)
- 🔐 `MemberRegistryService` 원장 미러: `PROMPTFLOW_AUTH_MIRROR`(빈값/off 비활성) → 기본 Google Drive 경로(존재 시) · 로컬 유실 시 미러 복원 · 기존 로컬은 미러로 덮어쓰지 않음
- 🧪 `MemberRegistryMirrorTests`

## Notes
- 사이드바 클릭 수정은 실제 WebView 수동 클릭 미검증
- Mobile(MAUI) 빌드 미검증
