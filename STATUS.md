# Current Project Status
- Active Milestone: v17.73.0 완료 (Complete) — AI 제로-터치 오토파일럿 하드닝 & 자율 프로덕션 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · Mobile net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 481/481 PASS × 1회(Release · MaxCpuCount=2 · 1회 기준 정책 · 신규 1) — 직전 2회 시도는 외부 부하 하 기존 타이밍 테스트(LiveCommerce 중앙 틱 · P2P 5 ms) 각 1건 실패 · 단독 통과
- Sidebar: 7대 카테고리 · 149 엔트리
- Updated At: 2026-10-21 (KST)

## Completed Deliverables
- AUTO-0: v17.72 체크포인트 커밋 `d84a9e2`(v17.73 편집 분리 커밋)
- AUTO-1: `Autopilot/HardwareSubtitleBurnInEngine` — v17.49 트랜스코더 NV12 백버퍼 제자리 SDF 하드서브(`IVideoFrameOverlay` · 외부 프로세스 0) · 큐/단어 전환 시에만 재래스터 · 세이프 존 감사 · `BurnReel`(MCR PGM 9:16 업스케일 · 그라데이션 소스 → MF GPU/관리 폴백 → fMP4) · 1080×1920 무할당
- AUTO-2: `SmartPlatformQuotaResilienceGuard` — YT 10 000 유닛(업로드 1 600 · 태평양 자정 리셋) · IG 50/일 · 200/h · TikTok 15/일 회계 · 403 quotaExceeded/429/Graph 4·17·32·613/5xx/401 분류 · 지수 백오프+지터 · Retry-After · 로컬 재시도 큐(256) · TikTok 우회 · 무할당
- AUTO-3: `RemoteCreatorTelemetryDispatcher` — `Channel<int>` 슬롯 풀 비차단 발행(무할당 · 포화 드롭 · 2 s 병합) · Discord embed / Telegram HTML JSON 직접 조립 · 429/5xx 재시도 · 시크릿 GraphSecretSlot · 환경변수
- AUTO-4: `ZeroTouchAutopilotMasterEngine` — 기획→방송→반응→클립→번-인→쿼터 라우팅 업로드→알림→쿨다운 폐루프 FSM(10 Hz 무할당) · 페일오버/비상 애드리브 감시 · 재시도 큐 펌프 · 자가 치유
- AUTO-5: `ZeroTouchAutopilotStudioRunner` + `ZeroTouchAutopilotStudioModal`(`ztp-`) · 카탈로그 `_autopilot`(카테고리 4) · HUD `gth-v1773` · DI · 게이트웨이 `LastStatus/LastErrorBody` · 시뮬레이터 쿼터 주입
- AUTO-6: `ZeroTouchAutopilot_And_BurnInGuard_Check`(TimingSensitive)

## Pending
- 실 계정 · 실 웹훅 연동 검증 · TikTok 은 v17.69 업로드 가드(시뮬 업링크) 경유 · 번-인은 PGM 정지 화면 소스(클립 프레임 디코더 부재)
- 외부 CPU 경합 시 기존 타이밍 테스트 간헐 실패 · Android 빌드 · iOS 실기기 · GUI 육안
