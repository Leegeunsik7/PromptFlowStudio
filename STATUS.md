# Current Project Status
- Active Milestone: UI-INTERACTIVE-SMOKE-CRAWLER-AND-SWITCH-BINDING-AUDIT (Complete) — 156개 모달 동적 인터랙션 전수 검증
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 492건 — Release 단일 스레드 전량 실행 491 통과 + DistributedGpuCluster 1건 부하 타이밍 실패(격리 재실행 3/3 통과)
- Sidebar: 7대 카테고리 · 156 엔트리
- Updated At: 2026-10-22 (KST)

## Completed Deliverables
- CRAWL-1: `InteractiveModalCrawlerTests` — BCL Renderer 서브클래스(외부 bUnit 0) · 프로덕션 DI(`MainForm.ConfigureServices` 추출) 실마운트 · onclick/oninput→onchange 실발화 · 빈 람다/값 고정/예외/비동기 예외 판정 · `PFS_CRAWL_ONLY` 키 필터
- CRAWL-1 실측: 마운트 156/156 · 디스패치 3,869건(스위치 181 · 슬라이더 578 · 버튼 2,839 · 셀렉트 91 · 입력 180) · 정상률 100% · 결함 0 · 비활성 스킵 117
- CRAWL-2 수정(수정 전 1차 크롤 3,845 디스패치 · 결함 86건 · 마운트 실패 1):
  - SpatialComputingStudioModal: WebXR 프로브 null → 마운트 NRE 가드
  - StreamGatewayClipperStudioModal: `@for` 루프 변수 캡처 `_ids[p]` → IndexOutOfRange 3건 교정
  - IntelligentStageLightingStudioModal: DMX 페이더 64개 · 긴장도 슬라이더 → 파킹/수동 설정점 표시
  - MultiAgentCrewStudioModal: 오버라이드 슬라이더 10개 → 잠금 값 표시
  - CinematographyStudioModal: 조리개 → 수동 ManualFStop 표시
  - GenerativeEnvironmentStudioModal: 태양 고도·방위 → 러너 Setpoint 프로퍼티 결선

## Known Issues / 환경 노트
- 호스트 부하 시 타이밍 예산 테스트 격리 실행 기조 유지(DistributedGpuCluster 하트비트 60 ms 포함)
- 전량 테스트는 메모리 압박 시 `-- RunConfiguration.DisableParallelization=true` 단일 스레드 실행 권장(크롤러 단독 약 6분)
- GUI 육안·실마우스 미검증(헤드리스 크롤) · Android 빌드 미검증
