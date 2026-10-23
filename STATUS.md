# Current Project Status
- Active Milestone: v17.94.0 완료 (Complete) — 프로덕션 플랫폼 비지빌리티 게이트웨이 & 실시간 텔레메트리 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 506 전량 회귀(단일 CPU · MaxCpuCount=1 · DisableParallelization) 505 통과 + 부하 타이밍 간헐 1 → `DistributedGpuCluster_And_CloudRenderingSwarm_Check`(v17.87 이후 동일) 격리 재실행 3/3 통과
- Sidebar: 7대 카테고리 · 170 엔트리 · 도움말 170/170 결함 0 · 크롤러 170 마운트 결함 0
- Updated At: 2026-10-23 (KST)

## Completed Deliverables
- PVG-1: `Services/PlatformVisibility/ProductionPlatformVisibilityGatewayEngine` — v17.93 `IVideoVisibilityGateway` 실체 · YouTube `PUT videos?part=status`(public/unlisted/private) · `DELETE videos` · IG Graph 숨김(comment_enabled)/삭제/조회 · ID 스키마 검증 · 2xx 응답 스키마 확정 · 재사용 버퍼 무할당 · `PROMPTFLOW_PVG_LIVE=1` 미설정 시 `PlatformApiContractSimulator` 결정적 폴백
- PVG-2: `TokenLifecycleRateLimitBackoffGuard` — 만료 5 분 전 갱신(v17.79 볼트 결선 `VaultPlatformTokenSource` · 401 재발급) · 429/Graph 4·17·32·613 지수 백오프(1.5 s×2ⁿ⁻¹ · 상한 30 s · 지터 ≤25 % · Retry-After 우선) · 일일 쿼터(YT 10k units · IG 4.8k) 90 % 안전 정지 · quotaExceeded 당일 소진
- PVG-3: `RealtimeTimeseriesMetricPollingEngine` — 5 분 간격 `videos.list?part=statistics` 50 ID 일괄 + Analytics 완청률 · IG insights → 분당 유속 링(19) 무할당 → v17.93 스플릿 테스터 피딩
- PVG-4: `PlatformVisibilityGatewayStudioModal` + 러너(연결 뱃지 · 쿼터 바 · 영상별 수동 전환 · 유속 SVG · 429/쿼터/토큰 주입) · 카탈로그 `_platformVisibilityGateway`(4번) · 도움말 · HUD `gth-v1794`(`PvgRun`) · DI 양쪽 · `GraphHttpMethod.Delete` 추가 · OAuth 스코프 force-ssl/yt-analytics/instagram_manage_insights 추가
- PVG-5: `ProductionPlatformVisibilityGateway_And_Telemetry_Check`(TimingSensitive · Mock HttpHandler 실 HttpClient 경로) · v17.93 카탈로그 단언 증가 허용

## Known Issues / 환경 노트
- 실 엔드포인트 미호출 검증(계약 시뮬레이터 + Mock HttpHandler) — 실 계정 연동은 `PROMPTFLOW_PVG_LIVE=1` + v17.79 볼트 재인증(스코프 추가) 필요
- IG Graph API 공식 비공개 전환 부재 → 비공개 요청은 댓글 잠금 숨김으로 대체
- GUI 육안 미검증 · Android 빌드 미검증
