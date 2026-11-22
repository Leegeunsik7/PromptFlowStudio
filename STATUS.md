# Current Project Status
- Active Milestone: ✅ Step 27 — 적용 기획안 실측 채점 · 자동 사후 분석 · 기획 적중률 피드백 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏸ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1306/1306 — 전체 단일 실행 통과(16분 7초 · 종료 코드 0) · Step 27 신규 `PlutoBriefOutcomeAttributionTests.cs` 7건
- Updated: 2026-11-22 (KST) · 기준 `2a10428`

## Completed — Step 27
- 🎯 [기획 즉시 적용] 출처 → `PlutoRenderedClipRecord.AppliedBrief*`(ID·형식·기대 합성·프레이밍·종류·훅/룩/BGM·테마·적용 시각·채점 여부·SurpriseDelta) — 레거시 JSON 호환 · 병합 시 채점 완료 > 태깅 > 없음 묶음 이전
- 📏 기대 합성 = 0.4·3초 + 0.4·완주 + 0.2·min(100, CTR×10) — 채널 기준선(조회수 가중) → 훅 실측 경험적 베이즈 수축(k=2) → 종류 보정 · 실측 없음 → 사전 40
- 🩻 `PlutoBriefOutcomeAttributionService`(DI 양쪽 Singleton · 카탈로그 Changed 구독) — 조회 ≥ 50 · 카탈로그 게이트 CAS 로 클립당 정확히 1회 채점 · Δ ≤ −12p → 오답 노트 `pair:`훅+룩 · `domain_hook:` 조합만 차단(벌점 2.0)
- 📐 적중률은 카탈로그 채점 결과에서 파생(별도 카운터 없음) → 기획기 활용 ×(0.8+0.4·축소 적중률) / 탐색·콜드스타트 버킷 별도 (3건부터) · 기획기는 차단 조합을 대안 룩으로 교체/탈락
- 🧠 두뇌 리포트 본문 '기획안 적중률 요약'(평가 ≥ 1건일 때만) — 상단 3행 헤더 불변
- 🧠 자체 추가: 🎯 출처 무장 충실도(룩/BGM 변경 → 미귀속)·48h TTL·편수 예산·타 기획 적용 시 해제 · 🧮 기획 결정(ID+적용 시각) 단위 집계(시리즈 1건 · 사후 분석 1회 · 병합본 재채점 없음) · 🆔 기획안 ID 를 최종 테마·교체 룩/BGM·편 번호로 재발급(빈 테마 해시 충돌 제거)

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 동시 세션(E2E 등) 부하 시 성능·타이밍 테스트 간헐 실패 — 전체 실행은 조용한 머신에서
