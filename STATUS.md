# Current Project Status
- Active Milestone: v17.93.0 완료 (Complete) — AI 자율 포스트-퍼블리시 A/B 스플릿 테스트 & 패자 회수기 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 505 전량 회귀(단일 CPU · MaxCpuCount=1 · DisableParallelization) 504 통과 + 부하 타이밍 간헐 1 → `DistributedGpuCluster_And_CloudRenderingSwarm_Check`(v17.87 이후 동일) 격리 재실행 3/3 통과
- Sidebar: 7대 카테고리 · 169 엔트리 · 도움말 169/169 결함 0 · 크롤러 169 마운트 결함 0
- Updated At: 2026-10-23 (KST)

## Completed Deliverables
- PST-1: `Services/PostPublishSplitTest/PostPublishSplitTestingEngine` — v17.85 훅/썸네일 A/B 시차 또는 채널 분리 배포 · 90 분 × 5 분 고정 링(19) 무할당 기록(누적 조회 · 초당 유속 · 완청률 · 공유) · 가중 복합 스코어 + 베타 사후 정규 근사 P(B>A) ≥ 95 % 조기 승자 · 만료 시 스코어 우세 확정 · 수동 강제 승자
- PST-2: `LowPerformanceAutoPrunerEngine` — 골든타임 종료 시 패자 · < 50 뷰/h · 완청 < 35 % 판정 → 비공개(기본)/삭제 · 429/5xx 3 회 재시도 · 회수 중복 차단 · 감사 링 64(사유 플래그 · HTTP · 시도)
- PST-3: `ViralWinnerSubChannelPromoterGuard` — 승자 MP4/자막/메타 `ReadOnlyMemory` 참조 승격 → v17.81 매트릭스 라우터(채널 쿼터 · 골든타임 · 45 분+ 시차) + 0~30 분 xorshift 지터 · 중복/24h 상한/에셋 누락 차단 · 승격 경로 무할당
- PST-4: `PostPublishSplitTestStudioModal` + `PostPublishSplitTestStudioRunner`(A/B 조회 · 시청 시간 SVG · 회수 카운트다운 게이지 · 강제 승자 · 삭제 모드 · 로그 32) · 카탈로그 `_postPublishSplitTest`(4번) · 도움말 1행 · HUD `gth-v1793`(`PstRun`) · DI 양쪽
- PST-5: `PostPublishSplitTest_And_AutoPruner_Check`(TimingSensitive) · v17.92 카탈로그 단언 증가 허용

## Known Issues / 환경 노트
- 플랫폼 지표/공개 상태 변경은 인메모리 시뮬레이터(`SimulatedVideoVisibilityGateway` · 결정적 곡선) — 실 API는 `IVideoVisibilityGateway` 구현 결선 필요
- GUI 육안 미검증 · Android 빌드 미검증
