# Current Project Status
- Active Milestone: v17.79.0 완료 (Complete) — 프로덕션 OAuth2 라이브 배포 & E2E 퍼블리싱 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 486/487 전량 회귀(단일 CPU) + 실패 1 `GaussianSplat_ShadowCasting_And_Relighting_Check` 격리 재실행 통과(부하 타이밍)
- Sidebar: 7대 카테고리 · 155 엔트리
- Updated At: 2026-10-22 (KST)

## Completed Deliverables
- LIVE-1: `LivePublish/LivePublishContractSimulator` — Google OAuth2 · Meta Graph 계약 패리티(`SimulatedMetaGraphServer` 확장)
- LIVE-2: `ProductionOAuth2TokenVaultGateway` — PKCE S256 · state CSRF · 만료 10 분 전 무할당 갱신 · AES-GCM 볼트 변조 탐지
- LIVE-3: `ProductionResumableVideoUploaderEngine` — YouTube 308/Range 오프셋 재개 · IG 릴스 3 단계 상태 머신
- LIVE-4: `LivePublishingPreflightRollbackGuard` — 스펙 검증 · 비공개 드라이런 2 단계 커밋 롤백 · 웹훅 알림
- LIVE-5: `ProductionLivePublisherStudioRunner` + `ProductionLivePublisherStudioModal` · 카탈로그 `_liveOAuthPublisher` · HUD `gth-v1779` · DI
- LIVE-6: `ProductionLivePublisher_And_E2EValidation_Check`(TimingSensitive)

## Known Issues / 환경 노트
- 호스트 메모리 압축·CPU 경합 시 프레임/그림자 예산 임계 간헐 초과(격리 재실행 통과)
- 실제 Google/Meta 계정 라이브 호출 미검증(시뮬레이터 계약만) · Android 빌드 · GUI 육안 미검증
