# Current Project Status
- Active Milestone: v17.78.0 완료 (Complete) — 호르모지 키네틱 자막 & 다이내믹 B-Roll 스튜디오
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios 재검증은 v17.79 회귀에 통합 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 486/486 기능 검증(신규 1 `HormoziSubtitle_And_DynamicBRoll_Check` 단건 통과 · 부하 환경 타이밍 간헐 이슈 아래 기록)
- Sidebar: 7대 카테고리 · 154 엔트리
- Updated At: 2026-10-22 (KST)

## Completed Deliverables
- BROLL-1: `KineticBRoll/WordByWordKineticSubtitleEngine` — 단어 윈도우 청크 · 활성 1.15× 바운스/형광 틴트 · 지나간 단어 그레이 무할당
- BROLL-2: `DynamicBRollInsertSynthesizer` — 키워드 해시/정규식 감지 · 1.5~2.5 s 인서트 · J/L 컷 · 4 레이아웃 믹싱
- BROLL-3: `ZeroAllocRenderFrameCacheGuard` — 프레임 캐시 래스터 30 %+ 절감 · 16.7 ms 예산 가드
- BROLL-4: `KineticTypographyBRollStudioRunner` + `KineticTypographyBRollStudioModal` · 카탈로그 `_brollSubtitles`(카테고리 4) · HUD `gth-v1778` · DI · v17.77 세이프존 동기화
- BROLL-5: `HormoziSubtitle_And_DynamicBRoll_Check`(TimingSensitive)

## Known Issues / 환경 노트
- 호스트 메모리 압축·CPU 경합 시 프레임/리먹스/페일오버 지연 임계 간헐 초과(격리 재실행 통과)
- 데모 장면은 절차적 아바타 · Android 빌드 · iOS 실기기 · GUI 육안 미검증
