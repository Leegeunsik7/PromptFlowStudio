# Current Project Status
- Active Milestone: v17.67.0 완료 (Complete) — AI 자율 쇼러너 & 시청자 인터랙티브 물리 방송 스튜디오 (v17.66.0 포함 · 미커밋)
- Build Status: Desktop Release -warnaserror 0/0 · Mobile net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 475/475 PASS × 2회 연속(Release · MaxCpuCount=2 · v17.66 1 + v17.67 1 신규)
- Sidebar: 7대 카테고리 · 143 엔트리
- Updated At: 2026-10-21 (KST)

## Completed Deliverables
- v17.66 MCR: `BroadcastMcr/` 통합 3DGS 컴포지터(세트 30만 + 아바타 3 + PPL · 단일 래딕스 큐 · v17.56 그림자 체인) · PGM/PVW Cut/Mix/Wipe · 800 샘플/프레임 A/V 0 f · PPL 가림 60 % 회피 100 % · `_bcastMcr` · `gth-v1766`
- SHOW-1: `Showrunner/ViewerInteractivePhysicsSpawnerEngine` — `!drop tomato|balloon|ball [@host|@1|@2]` · `!confetti` · 500 풀 · v17.57 골격 캡슐 + v17.59 데님 의상 셸 반발/마찰/찌그러짐/파열 · 24 스플랫 껍질 → 컴포지터 시청자 슬롯(12,000)
- SHOW-2: `AutonomousShowrunnerAdlibSynthesizer` — 피격 우선순위 · 템플릿 + 시청자 이름 애드리브 → FSM `ForceFloor`(대본 인터럽트) · 놀람/화남 주입 · MCR `CueCloseUp`(지연 ≤ 1 f) · 채팅 폭주 진정 멘트 + 와이드
- SHOW-3: `PhysicsSpamPoolingSafetyGuard` — 1024 수신 링 · 틱당 48 · 40 객체/s 버킷(컨페티 4) · 1.5 s 사용자 쿨다운 · 레이어 3 ms 초과 시 유입 반감 · 카메라 밖 최고령 재활용
- SHOW-4: `InteractiveShowrunnerStudioModal`(`isr-`) · DI(MainForm + MauiProgram) · HUD `gth-v1767` · 카탈로그 `_showrunner`(카테고리 0) · 게이트웨이 `CommandTap`
- SHOW-5: `ShowrunnerAdlib_And_PhysicsSpamGuard_Check`(TimingSensitive) — 2만 커맨드(1만 버스트 + 지속) · 풀 ≤ 500 · 재활용 · 레이어 16.7 ms 초과 0(GC 겹침 프레임은 Inconclusive 분리 집계) · 레이어 무할당

## Pending
- v17.66/v17.67 커밋 · Android 빌드 · iOS 실기기 · GUI 육안
- 의상 충돌은 v17.59 원단 물성 기반 캡슐 셸 근사(천 입자 PBD 직접 결합 아님)
