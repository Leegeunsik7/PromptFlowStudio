# Current Project Status
- Active Milestone: v18.0.3 완료 (Complete) — 숏폼 팩토리 로컬 MP4 실 파일 임포트 · 실 키프레임 썸네일 하드닝
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 525/525 통과(실패 0 · 건너뜀 0 · 9 분 8 초)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-25 (KST)

## Completed Deliverables
- IMP-1: `LocalVideoFileRingBufferSourceInjector` — 순수 C# ISO-BMFF(moov/trak/mdhd/hdlr/stsd avcC·esds/stts/ctts/stss/stsz/stsc/stco·co64) → AVCC→Annex-B(AUD + 키프레임 SPS/PPS) · 원시 AAC→ADTS · DTS 병합 링 주입(PTS 단조 · 결함 집계) · 링 kbps 자동 재구성 · 주입 핫패스 0 B
- IMP-2: `RealKeyframeThumbnailRasterizer` — 피크 인접 출력 IDR → 관리 Baseline/MF 디코딩(NV12→9:16 크롭) · 불가 시 IDR RBSP 비트 할당 맵 → 1080×1920 PNG + 108×192 미리보기 · `ValidatePng`(IHDR/IDAT/IEND · CRC) · 동일 입력 캐시
- IMP-3: `ShortsStudioWorkspaceModal` ① [📁 로컬 비디오 열기] · 파일 배지(이름 · 해상도 · 길이) · 리허설 자동 비활성 · 절삭 범위 = 파일 길이 · `ImportLocalVideo` 오케스트레이터 결선 · HUD `gth-v1803`
- IMP-4: `ShortsFactory_RealFileImport_And_Thumbnail_Check`(TimingSensitivePerformance) — 합성 MP4 → 링 IDR 원문 관통 · ①~④ fMP4 · PNG 구조 · 5 회 누수 < 256 KB · 주입 0 B · 손상 파일 리허설 폴백

## Known Issues / 환경 노트
- AAC 미디코딩: ③ 데드에어 계획용 PCM = AAC 프레임 크기 에너지 엔벨로프 프록시(출력 오디오는 원본 AAC 스트림 복사)
- 썸네일 실 디코딩은 관리 Baseline 부분집합/MF MFT 성공 시만 · 그 외 IDR 비트 할당 맵(디코딩 화상 아님 · `Kind` 로 구분)
- 링 창 120 s · 샘플 표 16,384 · 임포트 300 MB 상한 · 편집 목록(elst) · 조각화 MP4 미지원 · 파일 임포트 UI 육안 미검증
- 부하 환경 타이밍 간헐(GPU 스웜 50 ms 게이트 등) · 테스트 프로젝트 기존 분석기 경고 15 · Android 빌드 미검증
