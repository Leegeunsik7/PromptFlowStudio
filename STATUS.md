# Current Project Status
- Active Milestone: HOTFIX UI/UX 전수 감사 완료 (Complete) — 156개 카탈로그 모달 인터랙션·스크롤 정상화
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 491건 = 484 전량 회귀 통과(Debug) + 타이밍 7건 Release 격리 통과(TimingSensitivePerformance) · 신규 UI 감사 3건
- Sidebar: 7대 카테고리 · 156 엔트리
- Updated At: 2026-10-22 (KST)

## Completed Deliverables
- AUDIT-1: 전역 non-passive 휠 리스너 → 툴바 요소 한정(promptflow.js) · 닫힌 관제/모바일 슬라이드·HUD 장식·.sho/.sim-bar 투명 영역 pointer-events 관통 · 모달 overscroll contain(app.css)
- AUDIT-1: 49개 UI 파일 레이아웃 규격화 — 17개 전체 스크롤 모달 sticky 헤더(✕)/푸터 · 28개 flex 본문 flex:1 1 auto·min-height:0 · 5개 ≤900px 1열 스크롤 · MediaGallery 높이 상한
- AUDIT-2: ESC 전역 닫기(CloseOpenStudios) · 👑 마스터 스위트 모달 관제 슬라이드 밖 최상위 이관 · 경량 슬라이더 15개 oninput 실시간 바인딩
- AUDIT-3: 구독 컴포넌트 IDisposable 미구현 0 · 구독/해제 쌍 누락 0 · 백그라운드 직접 StateHasChanged 0
- AUDIT-4: 커머스 상품 카드 말줄임·minmax(0,1fr) · 9:16 플레이어 11곳 비율 유지 확인 · EPG 결함 없음
- AUDIT-5: `UiExhaustiveAuditIsolatedTests` 3건(모달 스크롤 규격 168개 · IDisposable · 오버레이/ESC/휠)

## Known Issues / 환경 노트
- 호스트 부하(AutoCAD 등 2GB+ 메모리 점유) 시 타이밍 예산 테스트 격리 실행 기조 유지 — 7건(BroadcastMcr·InteractiveShowrunner·LiveCommerce·MasterStation·SemanticChatWeather·ZeroTouchAutopilot·ReelsVisualPolish) Debug 전량 실행 시 예산 초과(HEAD 베이스라인 동일)
- GUI 육안·실마우스 휠 미검증(정적 감사 + 헤드리스 테스트) · Android 빌드 미검증
