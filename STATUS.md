# Current Project Status
- Active Milestone: v17.99.1 완료 (Complete) — YouTube Analytics 유지율 수집 · NALU 스마트 리먹서 · 타이밍 플레이크 해소
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 519 전량 회귀 2 회 연속 519/519 통과(실패 0 · 건너뜀 0) — `DistributedGpuCluster` · `ShowrunnerAdlib` · `GenerativeFilmScore`(신규 관측) 플레이크 해소
- Sidebar: 7대 카테고리 · 175 엔트리 · 도움말 175/175 · 크롤러 175 마운트 결함 0(카탈로그 변경 없음)
- Updated At: 2026-10-23 (KST)

## Completed Deliverables
- ING-1: `RetentionHeatmap/YouTubeAnalyticsRetentionCollector` — `audienceWatchRatio`×`elapsedVideoTimeRatio` · 환경 변수 OAuth2(토큰/refresh) · 미설정 = 모의(실데이터 아님 표기) · 429 Retry-After 쿨다운(요청 0) · 단절/타임아웃/401/형식 → 실응답 캐시 → 세이프티 프리셋 · Utf8JsonReader
- MUX-1: `NaluSmartReMuxer` — H.264/H.265 Annex-B NAL/AU 파싱 · GOP 스냅 스트림 복사 · SPS/PPS(/VPS) 원문 주입(중복 생략) · 템포 맵 PTS/DTS 재타이밍 · DTS 엄격 증가 · NAL 바이트 무손실 검증 · 펀치 GOP = 재인코딩 필요 집계 · 무할당
- MUX-2: `SyntheticAnnexBStream` 구조 합성 클립(B 재정렬 · 다중 슬라이스) · 러너 `IngestAsync`/`Remux` · 모달 수집/리먹스 카드 · HUD `gth-v1798` 출처/무손실/A·V 줄
- QA: `RetentionIngestRemuxTests` 8 건 · 프리셋 A/V 편차 ≤ 1.16 ms · 끝 드리프트 ≤ 0.81 ms · 소크 12 s ≈ 420 사이클/70 만 프레임 손상 0 · 후반 메모리 Δ ≈ 0 · 핸들 Δ ≤ 3
- FLAKE: GPU 스웜 무통보 단절 전제 명시 성립(JIT 워밍 후 로컬 < RTT → 원격 0 배정) · 명시 단절 최선 3 · 클라우드 배정 관측 배리어 · 쇼러너 결정적 재활용 경로 · 폴리 무할당 MinAllocated

## Known Issues / 환경 노트
- 실 YouTube Analytics 서버 연동 미검증(가짜 HTTP 핸들러만) · 리먹스 대상은 합성 비트스트림(실 디코딩 미검증) · 줌 펀치 GOP 재인코딩 미구현(인코더 없음)
- GPU 스웜 명시 단절 50 ms 게이트는 16 코어 전량 외부 포화 시 OS 스케줄링으로 미충족(8 코어 부하 통과)
- 테스트 프로젝트 기존 분석기 경고 15(TitanReleaseTests 기존 라인) · GUI 육안 미검증 · Android 빌드 미검증
