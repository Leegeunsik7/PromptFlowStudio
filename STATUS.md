# Current Project Status
- Active Milestone: 【작업실 헤더·플로팅 버튼 CSS 간격 다듬기 (ba8c6a7 후속)】
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests 0W/0E
- Tests: ✅ 1141/1141 PASS (전체 스위트, Release, 12m48s) · `EngineRoutingAndProjectExportTests` 10/10
- Updated: 2026-11-15 (KST)

## Completed
- 🎨 Workspace CSS 전용 수정(바인딩·마크업 무변경)
  - 헤더 행 간격 8px/14px(≥1600px 18px) · 기본 행 하단 여백 8px · 뒤로/PlutoCore 버튼 하단 10px 패딩 제거 → 수직 중앙 정렬
  - 상태 배지(`ws-ssmon`/`ws-ipc`/`ws-recover`) 최소 높이 26px 중앙 정렬 · 배지 영역 `max-width:100%`
  - Pluto 2열 스트립 gap 10px(≥1600px 14px) · 셀 패딩 8px/10px · `align-items: stretch`로 높이 일치
  - 캔버스 툴바 gap 6px/8px · 토글·프로젝트 입력 최소 높이 26px
  - 🪐 FAB: 최소 높이 44px · 최대 폭 360px · 위치 24px · 본문 하단 여백 104px

## Next
- 실제 앱에서 1280/1920px 및 ≤1100px 헤더 레이아웃 육안 확인
- ComfyUI 온라인 판정은 헬스 체크 스냅샷 기반 — 첫 렌더 직전 즉시 핑 검토
