# Current Project Status
- Active Milestone: v17.97.0 완료 (Complete) — 바이럴 커머스 & 스마트 제휴 어트리뷰션 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 509 전량 회귀(단일 CPU · MaxCpuCount=1 · DisableParallelization) 508 통과 + 부하 타이밍 간헐 1 → `DistributedGpuCluster_And_CloudRenderingSwarm_Check`(v17.87 이후 동일) 격리 재실행 3/3 통과
- Sidebar: 7대 카테고리 · 173 엔트리 · 도움말 173/173 결함 0 · 크롤러 173 마운트 결함 0
- Updated At: 2026-10-23 (KST)

## Completed Deliverables
- VCA-1: `Services/ViralCommerce/VideoContextAffiliateProductMatcherEngine` — 대본 정규형 고정 버퍼 · 제품명50/브랜드20/카테고리20/별칭15×3 연관도 · 연관도 × EPC 상위 3 · 쿠팡/아마존 모의 12 품목 · 매칭 무할당
- VCA-2: `CompliantShortlinkLegalNoticeInjector` — subId/utm_campaign UTF-8 퍼센트 인코딩 · FNV-1a mod 62⁸ 8 자리 Base62 `pfs.link/{id}` · 공정위 필수 문구/FTC 고지 첫 줄 주입(고정 댓글 · 설명란) · `HasTopNotice` 검증 · 조립 무할당
- VCA-3: `RealtimeAffiliateAttributionSafetyGuard` — 봇 UA 시그니처 · 동일 IP 1 s 내 3 회 → 600 s 차단 · 영상당 3 링크 상한 · 영상별 클릭/전환/수수료 · 60 분 시계열 링 · 판정 무할당
- VCA-4: `ViralCommerceAffiliateStudioModal` + `ViralCommerceAffiliateStudioRunner`(추천 상품 · 고지 미리보기/1클릭 복사 · 전환/차단 게이지 · v17.95 고정 댓글 템플릿 동기화) · 카탈로그 `_viralCommerceAffiliate`(4번) · 도움말 · HUD `gth-v1797`(`VcaRun`) · DI 양쪽
- VCA-5: `ViralCommerce_And_AffiliateAttribution_Check`(TimingSensitive · 무할당 · 100 ms 예산) · 크롤러 173

## Known Issues / 환경 노트
- 제휴 카탈로그 · 클릭 스트림은 인메모리 모의(결정적 xorshift) — 실 쿠팡/아마존 API · 숏링크 리다이렉트 서버 미결선
- GUI 육안 미검증 · Android 빌드 미검증
