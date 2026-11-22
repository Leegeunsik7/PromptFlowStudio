# Current Project Status
- Active Milestone: ✅ Step 30 (캡스톤) — 동반 허브 2종 관측 · 수집 후 Downloads 정리(선택) · 샌드박스 7단계 플라이휠 자가진단 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1335/1335 — 전체 단일 실행 통과(14분 59초 · 종료 코드 0) · Step 30 신규 `PlutoCapstoneSelfTestAndArchiverTests.cs` 7건
- Updated: 2026-11-23 (KST) · 기준 `9ce927a`

## Completed — Step 30
- 🔁 `PlutoDriveHealthService.Companions` — `pluto_youtube_unmatched.json` · `pluto_rehab_ledger.json` 로컬/Drive 존재 · SHA-256 · 항목 수(핵심 허브 9종 · `TotalCount` 불변) → 진단서 `### 🔁 동반 동기화 파일` 소절(경로 가림 · 해시 12자) → `Settings.razor` 9 허브 목록 바로 아래 요약
- 📦 `ArchiveIngestedExports`(영속 · 기본 OFF) — 완전 수집 + 장부 영속 후에만 `{감시 폴더}/Pluto_Ingested/` 이동 · `_2`/`_3` 접미 · 실패/격리/무관/중복/잠김 파일 무접촉 · `Workspace.razor` [📦 수집 후 폴더 정리 ON/OFF](헤더 밖)
- 🧪 `PlutoFlywheelSelfTestService`(DI 양쪽 Singleton) — 임시 폴더 7단계(플래너→출처→업로드 패키지·연결성→Studio 정규화·연결→채점·사후 분석→복권·PC 간 장부 재생→진단 .md) · `finally` 샌드박스 삭제 · `Settings.razor` [🧪 플라이휠 자가진단 실행] 단계별 PASS/FAIL·ms
- 🧠 자체 추가: 🧾 동기화 장부 기준선 판정(LocalAhead/DriveAhead vs Diverged) · 🧬 이동 직전 쓰기 거부 핸들 SHA-256 재검증(같은 이름 덮어쓰기 차단) · 🧯 미매칭 대기열/장부 영속 실패 시 이동 거부(`UnmatchedQueuePersistFailed` · `LastPersistFailed`) · 🔗 링크/정션 · 감시 폴더 밖 · 정리 폴더 자리 파일 거부 · 🧹 샌드박스 소유 증명 삭제(접두+토큰 표식+부모+링크 검사) · 🔍 라이브 메타데이터 지문 · 🧷 자가진단 단일 비행

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 성능·타이밍 테스트는 부하에 민감 — 전체 실행 전 `dotnet build-server shutdown`
