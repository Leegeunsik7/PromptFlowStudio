# Current Project Status
- Active Milestone: ✅ Step 22 — YouTube Studio CSV/ZIP 자동 수집 · Shorts 정규화 · 카탈로그 연결 · 성숙도 증분 학습 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⚠ Mobile net8.0-ios — `ios` 워크로드 미설치(미검증)
- Tests: ✅ 1269/1269 — 전체 단일 실행 통과(14분 21초 · 종료 코드 0 · 기존 1261 + Step 22 8)
- Updated: 2026-11-20 (KST) · 기준 `fe7e6da`

## Completed — Step 22
- 📊 `YouTubeStudioAnalyticsNormalizer` — `.csv`/`.zip`(메모리 해제 · BOM) · 한/영 헤더 · 천 단위/`%`/로캘 날짜 · 시청 유지 → `Hook3sRetention` · 100% 초과 무릎점(70) 지수 포화 · 조회수 10 미만 노이즈 제외 · CTR 결측 중립(2.2)
- 📺 `PlutoYouTubeCatalogLinker` — Tier 1 `YouTubeVideoId` O(1) · Tier 2 제목 Dice + `n/N편` 일치 + 게시 7일 창 → 연결 영속 · Tier 3 사유 보고
- 📈 성숙도 증분 — 카탈로그 워터마크(조회수·단계) · 동일 스냅샷 0건 · ≥2배 & +200회 → `IngestMaturityDeltaAsync`(0.5→0.25→0.125 · 3단계 · 단계별 장부)
- 🖥️ Workspace 일괄 패널 — `.csv/.zip` 선택 · [📂 다운로드 폴더 최신 내보내기 학습] · Studio 표 붙여넣기도 새 경로로 라우팅
- 🧠 자체 추가 — ① 멀티 PC 워터마크 최댓값 병합(증분 재적용 차단) ② 1:1 연결 가드 · 모호 후보 거부 · 병합 중복 연결 해소 ③ ZIP 내용 기반 표 선택(차트/합계 제외) · 해제 상한
- 🧪 `PlutoYouTubeAnalyticsIngestionTests.cs` 8건

## Notes
- ⚠️ `.git` 은 `D:\Program` — `--git-dir=D:\Program\.git --work-tree=D:\Program\PromptFlowStudio` 필요
- ⚠️ 동시 세션의 외부 `testhost` 종료 시 전체 실행 중단(크래시 이벤트 없음) — 전체 실행은 조용한 머신에서
