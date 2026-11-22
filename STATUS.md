# Current Project Status
- Active Milestone: ✅ Step 25 — 폐루프 A/B 제목 CTR 귀속 · 영상 옆 업로드 패키지(.txt) · 멀티 PC 미매칭 대기열 조정 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⚠ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1291/1291 — 전체 단일 실행 통과(20분 20초 · 종료 코드 0 · Step 25 신규 `PlutoTitleAttributionAndSidecarTests.cs` 6건)
- Updated: 2026-11-22 (KST) · 기준 `73ab270`

## Completed — Step 25
- 🅰️🅱️ 카탈로그 `SelectedTitleVariant`·`PublishedTitleFraming`·`PublishedTitleText`(+대체 변형·귀속 상태 · 하위 호환) · [🅰️/🅱️ 제목 복사] · 전체 패키지 복사 = "AB" 기록
- 📈 프레이밍(outcome-proof · curiosity-question · ending-prompt) 순위 = 확정 클립 워터마크 CTR·조회수 가중 경험적 베이즈 축소 → A=1위 · B=다음 순위(서로 다름 · ≥0.6 연결성 유지)
- 💾 `WriteSidecarAsync` — `{base}_publish_pack.txt` 영상 옆 원자 기록(UTF-8 BOM · CRLF) · 경로 위생(절대 경로 · `..`/장치 접두 · 영상 확장자 · 링크/폴더 차단)
- 🔁 `ReconcileAsync` — Drive·로컬 미러·피어 사본 합집합(더 높은 조회수 우선) · 어느 PC 카탈로그든 연결된 ID 정리 · 무변경 무기록 · Workspace 60초 간격
- ☁️ `CompanionSyncFiles`(대기열) 쓰기 반환·미러·복원 편입(핵심 허브 9종·병합 허브 6종 불변) · 충돌 사본 의미 병합 지원 · 대기열 저장은 저장소 관리자 경유
- 🧠 자체 추가 — ① 실제 YouTube 제목으로 A/B 판정(교정·Diverged 제외·판정 후 동결) ② 🪦 처리 기록(무시·페어링·연결 · 120일 · 200건)으로 재수집·타 PC·병합 시 부활 차단 ③ 근거 게이트(≥2)·조회수 50 하한·클립당 1표본 ④ 사이드카 링크/폴더 차단

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 동시 세션의 외부 `testhost` 종료 시 전체 실행 중단 — 전체 실행은 조용한 머신에서
