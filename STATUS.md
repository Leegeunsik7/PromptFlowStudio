# Current Project Status
- Active Milestone: v17.89.0 완료 (Complete) — AI 인텐트 내비게이션 & 전수 인터랙티브 도움말 시스템
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 501 전량 회귀(단일 CPU · MaxCpuCount=1 · DisableParallelization) 499 통과 + 실패 2 → `VodRepurposing_And_ArticleSynthesizer_Check`(카탈로그 164 고정 단언 → ≥164 로 수정) · `DistributedGpuCluster_And_CloudRenderingSwarm_Check`(벽시계 타이밍 간헐 · v17.87 이후 동일) 격리 재실행 3/3 통과
- Sidebar: 7대 카테고리 · 165 엔트리
- Updated At: 2026-10-23 (KST)

## Completed Deliverables
- NAV-1: `Services/Ui/CatalogIntentSearchEngine` — 소문자 정규화 + Ordinal 스팬 토크나이징 · 38 개념 동의어 사전(자막=캡션=호르모지 · 송출=스트리밍=RTMPS · 얼굴=아바타=립싱크 …) ulong 비트 · 한국어 어미 접두 축약 · 개념 밀도 가중 → 상위 3~5 · 적합도 % · 사유 태그 · stackalloc 무할당 · 평균 0.14 ms
- NAV-2: `StudioCatalogHelpRegistry` — 165 항목 1:1(역할 1줄 · 핵심 조작 3 · Step 1➔3 · 연관 메뉴 · 인텐트 키워드), 각 모달 마크업 부제/실제 버튼·슬라이더 라벨에서 추출 · `CountDefects` 누락/빈칸/임시문구/끊긴 링크 0
- NAV-3: `IntentNavigationHubStudioModal`(실시간 질의 · 추천 카드 · 바로 열기 · 사용법 펼침 · 퀵 필터 5) · Ctrl+K → 허브(`/` 는 팔레트 유지) · 사이드바 🧭 버튼 · 카탈로그 `_intentNavigationHub`(⚙️ 시스템 & QA 카테고리) · HUD `gth-v1789`(`NavRun`) · DI 양쪽
- NAV-4: `StudioHelpDrawer` + `promptflow.js siteMaticStudioHelp`(MutationObserver 가 `*-modal` ✕ 좌측에 원형 ? 주입) · 열린 플래그/헤더 제목으로 키 해석 · 연관 메뉴 = 호스트 모달 ✕ 클릭 후 대상 전환
- NAV-5: `IntentNavigation_And_ExhaustiveHelpRegistry_Check`(TimingSensitive) · 크롤러 165 모달 결함 0

## Known Issues / 환경 노트
- 카탈로그 0번은 "가상 프로덕션"이라 허브는 기존 [⚙️ 시스템 & QA 인프라](5번)에 등록
- 도움말 문구는 마크업 추출 + 템플릿 기반 — 일부 불릿이 짧은 라벨(예: 「SPP」) 수준 · ? 버튼 주입은 DOM 규약 의존 · GUI 육안 미검증 · Android 빌드 미검증
