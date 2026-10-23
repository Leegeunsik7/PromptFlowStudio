# Current Project Status
- Active Milestone: v17.98.1 완료 (Complete) — 재인코딩 0 NALU 스마트 리먹서(H.264 SPS 크롭 줌 펀치) · 리에디터 실결선
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 519/519 통과(실패 0 · 건너뜀 0)
- Sidebar: 7대 카테고리 · 175 엔트리 · 도움말 175/175 · 크롤러 175 마운트 결함 0(카탈로그 변경 없음)
- Updated At: 2026-10-24 (KST)

## Completed Deliverables
- REM-1: `H264SpsCropRewriter` — SPS EBSP→RBSP · profile/스케일링 리스트/POC/해상도 파싱 · frame_cropping 오프셋 Exp-Golomb 재기록(CropUnit 정렬 · 중심 1/1.15) · VUI 비트 원문 연결 · 정지 비트/에뮬레이션 방지 재삽입 · 무할당 · 웜 ≤ 0.5 ms
- REM-1: `NaluSmartReMuxer.Remux(cropZoom)` — H.264 펀치 GOP SPS 제자리 치환/주입 → 다음 GOP 원 SPS 복원 · `ZeroTranscodePunchGops`/`CroppedSpsNals`/`MaxCropUs` · 검증기 크롭본 인정 · H.265 = 재인코딩 필요 정직 보고 유지
- REM-2: `MicroPunchDynamicPacingReEditorEngine.RemuxInto` — 템포 구간 → 90 kHz 워프 절점(WSOLA 출력 시간 맵) · 펀치 시각 → 리먹서 · 러너 절점 빌더 이관 · 모달 SPS 크롭 펀치 지표
- REM-3: `SyntheticAnnexBStream` H.264 SPS = 규격 비트스트림(High · 1920×1080 · 스케일링 리스트 · VUI) · 테스트 = 출력 재파싱 GOP 별 표시 창(펀치 1668×938 / 그 외 1920×1080) · SPS 왕복 무손실 · PTS/DTS · 무할당

## Known Issues / 환경 노트
- 크롭 줌은 GOP(키 AU) 단위 적용 · 표시 창 축소 후 플레이어 업스케일 의존 · 실 디코더 재생 미검증(합성 비트스트림) · H.265 SPS 크롭 미구현
- 실 YouTube Analytics 서버 연동 미검증 · GPU 스웜 명시 단절 50 ms 게이트는 16 코어 전량 외부 포화 시 미충족 가능(부하 환경 타이밍 간헐)
- 테스트 프로젝트 기존 분석기 경고 15(TitanReleaseTests 기존 라인) · GUI 육안 미검증 · Android 빌드 미검증
