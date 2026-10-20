# Current Project Status
- Active Milestone: v17.65.0 완료 (Complete) — 멀티 아바타 협업 & 턴테이킹 토크쇼 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · Mobile net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 473/473 PASS × 2회 연속(Release · MaxCpuCount=2 · 신규 1 · 플래키 0)
- Sidebar: 7대 카테고리 · 141 엔트리
- Updated At: 2026-10-21 (KST)

## Completed Deliverables
- TALK-0: `.gitignore` 에 `.claude/` 추가 · headroom 캐시 3파일 인덱스 제거(`git rm --cached` · `prompts/` 추적 유지)
- TALK-1: `Talkshow/RealtimeTurnTakingFsmEngine` — 3인 Speaking/Yielding/Listening/Interrupted · EOU → 150~300 ms 침묵 인계(각성·질문 가변) · 64 이벤트 링 + `in` 디스패치
- TALK-2: `MutualGazeListeningReactionSynthesizer` — 머리 70 %/안구 잔여 · 회피/미세 단속/깜빡임(노려봄 방지) · 화자 정서가/각성 동조 끄덕임·미소·놀람 → ARKit 52 · v17.60 수동작
- TALK-3: `TalkshowMultiCamDeadlockGuard` — v17.39 프레이밍 4캠(투샷·OTS·바스트·리액션 CU) 최소 0.35 s 유지 · 역할/토픽 오너/대기/점유 점수 오버랩 판정 · 바지인 수락/거부 · 1.2 s 침묵 데드락 강제 인계
- TALK-4: `MultiAvatarTalkshowStudioModal`(`mtk-`) · `MultiAvatarTalkshowStudioRunner` DI(MainForm + MauiProgram) · HUD `gth-v1765` · 카탈로그 `_talkshow`(카테고리 1)
- TALK-5: `MultiAvatarTalkshow_TurnTaking_Check`(TimingSensitivePerformance) — 실측 인계 150~267 ms · 컷 34 · 최소 유지 0.35 s · 데드락 2 · 미해결 오버랩 ≤ 1틱 · 최장 응시 3.9 s · 틱 무할당

## Pending
- Android 빌드 · iOS 실기기 · GUI 육안
- v17.55 3DGS 세트 래스터 미리보기 미결선(프로그램 모니터는 투영 SVG)
