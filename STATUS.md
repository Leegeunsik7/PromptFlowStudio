# Current Project Status
- Active Milestone: v18.0.0 완료 (Complete) — 175 분절 모달 → 32 엔터프라이즈 슈퍼-워크스페이스 통폐합 · 서브탭 딥링크 라우터 · 원스톱 숏폼 AI 팩토리
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 522/522 통과(실패 0 · 건너뜀 0)
- Sidebar: 6 그룹 · 32 워크스페이스(가상프로덕션 4 · 디지털휴먼 4 · 방송MCR 4 · 라이브커머스 3 · 소셜/바이럴 4 · 시스템/코어 13) · 175 기존 키 서브탭 매핑 175/175 · 중복 0
- Updated At: 2026-10-25 (KST)

## Completed Deliverables
- CNS-1: `StudioWorkspaceCatalog` — 32 워크스페이스 · `ResolveWorkspaceSubTabRoute`(키 → 워크스페이스/탭/엔진 · 0 B) · 사이드바 검색이 175 기능 제목까지 읽어 해당 탭 직행 · Ctrl+K 허브 결과에 「워크스페이스 ▸ 탭」 표기 · 통합 모달 외 29 워크스페이스는 기존 엔진 모달 위 `StudioWorkspaceDock` 상단 탭
- CNS-2: `ShortsStudioWorkspaceModal` — ① 절삭&리프레임 ② 자막&더빙 ③ 리텐션&사운드 ④ 인출&썸네일 · 엔진 8 종 하위 탭 주입 · `ShortsFactoryWorkspaceContext` 탭 공유(순서 강제 · 상류 변경 시 하류 확정 해제 · 무할당)
- CNS-3: `SocialPublisherWorkspaceModal`(OAuth2 볼트 / 다채널 변형 / A/B 레이스+게이트웨이 3 탭) · `ViralGrowthCommerceWorkspaceModal`(트렌드재킹·스파이·제휴 태거·골든아워 단일 전략 룸)
- CNS-4: `StudioCatalogHelpRegistry.WorkspaceGuides`(32 종합 가이드 · 도움말 서랍 표시) · `SuperWorkspaceConsolidation_And_SubTabRouting_Check` · 크롤러 `AllSuperWorkspaces_MountAndTabSwitchIntegrity`(32 마운트 · 탭/엔진 전환 결함 0) · 기존 175 모달 크롤 유지

## Known Issues / 환경 노트
- 기존 175 엔진 모달 · 백엔드 무변경(하위 탭 주입) — 독 워크스페이스는 엔진 모달 스크림을 독 높이만큼 내리는 CSS 오버라이드 방식(GUI 육안 미검증)
- 숏폼 컨텍스트 값은 워크스페이스 상단 바 공유 상태 — 개별 엔진 러너 내부 파라미터로의 역주입은 미결선
- 부하 환경 타이밍 간헐(GPU 스웜 50 ms 게이트 등) · 테스트 프로젝트 기존 분석기 경고 15 · Android 빌드 미검증
