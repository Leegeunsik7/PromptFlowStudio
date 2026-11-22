# Current Project Status
- Active Milestone: ✅ Step 29 — 복권 장부 멀티 PC 동기화·합집합 병합 · 수집 후 자동 채점/복권 스윕 · 진단서 .md 파일 저장 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1328/1328 — 전체 단일 실행 통과(22분 5초 · 종료 코드 0) · Step 29 신규 `PlutoCrossPcRehabAndAutoSweepTests.cs` 10건
- Updated: 2026-11-23 (KST) · 기준 `8fa9823`

## Completed — Step 29
- 🌱 `pluto_rehab_ledger.json` 복권 장부 — Drive 동반 파일(핵심 허브 9종 · 병합 허브 6종 불변) · (키, 클립) 합집합 병합(교환·멱등) · 노트 적재/재적재 시 누락 복권만 × 0.35 재생(이중 감쇠 없음) · 장부 충돌 병합 → 오답 노트 무재시작 재적재(`OwnerHubOf`)
- 🎯 `YouTubeDownloadsAutoWatcherService` — 학습 영향 있는 자동/수동 수집 직후 `EvaluatePendingBriefOutcomesAndRehabilitationAsync`(DI 양쪽 결선)
- 💾 `ExportSnapshotMarkdownFileAsync` — `pluto_diagnostic_{yyyyMMdd_HHmmss}.md` UTF-8 BOM 원자 저장 → `Settings.razor` [💾 진단서 파일 저장(.md)]
- 🧠 자체 추가: 🔀 노트 병합 시 벌점↔목록 쌍 유지 + 장부 재생(단순 합집합은 감쇠 누락) · ⏱️ 재생 세대 가드(상대 PC 새 벌점 우선 · 렌더 시각 미상 재생 금지) · 🧱 FIFO 포화 가드 + 레거시 Step 28 목록 역충전 · 🧯 수집 후 스윕 단일 비행 코얼레싱(후행 1회 · 실패 격리) · 🔒 공백 포함 경로(`G:\내 드라이브\DATA`) 조각 노출 차단 · 🛡️ 같은 초 `_2` 접미 · 파일/폴더/링크 덮어쓰기 금지 · 링크 폴더 거부

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 성능·타이밍 테스트는 부하에 민감 — 전체 실행 전 `dotnet build-server shutdown`(VBCSCompiler 상주 시 간헐 실패 관측)
