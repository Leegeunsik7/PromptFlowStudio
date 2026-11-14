# Current Project Status
- Active Milestone: 【Pluto 4.2.1 — 학습 가중치 → 훅·룩·태그 순방향 랭킹(읽기 경로 폐루프)】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile `net8.0-ios` CoreCompile 0W/0E · ✅ Tests 0W/0E
- Tests: ✅ 1121/1121 PASS (전체 스위트, Release, 13m24s) · 신규 `PlutoAdaptiveRankerTests` 3/3
- Updated: 2026-11-15 (KST)

## Completed
- 🧠 `PlutoAdaptivePatternRanker` — `pattern:hook:/look:/bgm:/style:` 가중치 × (1 − clamp(오답 벌점/2, 0, 0.85)) × 순번 사전점수 + FNV 타이브레이커 · 배지(🔥/⚠️/✨) · 태그 `#` 유무 정규화 · DI 데스크톱/모바일
- `PlutoLearningService.GetPatternWeights(prefix)` — 학습 토큰 스냅샷(기본 후보 밖 학습 훅도 랭킹 편입)
- 🎬 `PlutoMontageStoryGenerator` — 랭커 1위 템플릿 훅 Act1 우선 · LLM 프롬프트에 검증 훅/강화 태그 · 랭커 미주입 시 기존 동작
- 📱 `PlutoMultiFormatPackager` — 강화 태그 선두 재정렬 · 🔥 학습 훅 첫 제목 · LLM 프롬프트 반영
- 🧩 Workspace: 학습 추천 스트립(훅·룩 배지) + [🧠 학습 최적 룩·훅 적용](룩 고정 → 프리뷰/자막 재생성 · 몽타주 없으면 프롬프트 훅 주입) · 세션 초기화/성과 피드백 직후 갱신

## Next
- 성과 학습 HUD 카드(XP 증분 · 차단 훅 · 1위 룩) 검토
- BGM 무드 1위 추천을 `_bgmTrack` 자동 적용에 결선 검토
