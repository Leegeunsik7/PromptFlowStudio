# Current Project Status
- Active Milestone: 【Pluto 4.6 — 두뇌 리포트 · 쇼츠/릴스 원클릭 복사 · 9:16 자막 세이프 존 & 15초 루프 페이드】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Mobile net8.0-ios CoreCompile 0W/0E · ✅ Tests 0W/0E
- Tests: ✅ 1151/1151 PASS (전체 스위트, Release, 14m53s) · 신규 4건
- Updated: 2026-11-16 (KST)

## Completed
- 🧠 `PlutoAutonomousBrainSummaryService` — 실험 장부 × 랭커 × 오답 노트 → `PlutoBrainSummarySnapshot`(판정/대기 · 챌린저 승률 · 1위 훅/룩/BGM · 가드 수 · 다음 가설 배너) · 소스별 [부분 구제] · DI 양쪽 등록
- 🧠 Workspace `[🧠 플루토 두뇌 리포트]` 토글 카드 — 로드 · 실험 판정 · 성과 피드백 시 갱신(세대 가드)
- 📋 `PlutoMultiFormatPackager.FormatForYouTubeShorts`(제목 ≤95자 단어 경계·서로게이트 안전 · 태그 5) / `FormatForInstagramReels`(태그 12 중복 제거 · 게시 시간)
- 📋 SNS 키트 패널 `[📋 쇼츠용/릴스용 원클릭 복사]` — `siteMaticClip.copy` → 네이티브 클립보드 폴백 · 상태 토스트
- 📱 9:16 몽타주 자막 박스 하단 여백 461px(≥24% · 4K 922px) — `BuildTimedSubtitleFilter(vertical9x16)` · 16:9 기존 오프셋 유지
- 🔁 9:16 BGM 먹싱 `SeamlessLoopAudio` → afade 0.3s in / 0.6s out(SFX 믹스 포함) · 기본 1.5s 유지

## Next
- 실기기 쇼츠/릴스 업로드 화면에서 자막 가림 여부 · 루프 경계 클릭 확인
