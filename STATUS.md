# Current Project Status
- Active Milestone: v17.98.2 완료 (Complete) — 실기 비트스트림 재생 컨포먼스 · 플레이어 하드닝(SPS RBSP 가드 · fMP4 컨포먼스 밸리데이터 · avc3 인밴드 파라미터 셋)
- Build Status: Desktop Release -warnaserror 0/0 · net8.0-ios Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 저메모리 단일 스레드(MaxCpuCount=1 · DisableParallelization) 520/520 통과(실패 0 · 건너뜀 0)
- Sidebar: 7대 카테고리 · 175 엔트리 · 도움말 175/175 · 크롤러 175 마운트 결함 0(카탈로그 변경 없음)
- Updated At: 2026-10-24 (KST)

## Completed Deliverables
- CON-1: `BitstreamRbspConformanceSafetyEngine` — EBSP 계층(00 00 00/01/02 금지 · 00 00 03 뒤 ≤ 03 · 끝 바이트 ≠ 00 · forbidden bit) · SPS 전 구문(VUI/HRD/bitstream_restriction) 파싱 → 종료 비트 = 정지 비트 일치 · CropUnit(4:2:0 = 2×2) 정렬/오버플로 · `Harden`(03 재주입) · `AlignCrop` · 무할당 — `NaluSmartReMuxer` 크롭 SPS 출력 전 게이트(`ConformanceRejects` → 재인코딩 필요 정직 보고)
- CON-2: `IsoBmffFragmentedMp4ConformanceValidator` — 박스 트리 길이 정합(32/64 bit · size 0) · ftyp 선두 isom/mp42 · moov→moof→mdat 순서 · trex/tkhd(활성 · 직각 행렬 · w = 1.0 · 9:16 회전 반영) · avcC 구조 + SPS 가드 · mfhd 순번 · tfhd/trun 플래그 산출 길이 · data_offset ⊂ mdat · 첫 샘플 is_non_sync 0 ∧ depends_on 2 · AVCC 길이 접두 순회 동기 ⇔ IDR · avc1 인밴드 SPS/PPS 금지 · 무할당
- 하드닝: `FastShortsMuxingHardwareExporterEngine` — 클립 중 SPS/PPS 변경(크롭 줌 펀치) 시 avc3 샘플 엔트리 + 인밴드 유지(종전: 마지막 SPS 로 avcC 덮어써 펀치 소실/초기 IDR 불일치) · `TryInspect` 첫 VCL 기준 IDR 판정(SEI 선행 허용)
- CON-3: `RealBitstreamPlaybackConformance_Check`(TimingSensitivePerformance) — 줌 펀치 리먹스 → SPS 5 개 적합/CropUnit · fMP4 avc3/avc1 양 경로 적합 · 결함 주입 12 종 정확 분류 · 100 회 할당 0 B

## Known Issues / 환경 노트
- 합성 비트스트림 기반 검증 — VLC/WMF/AVPlayer/ExoPlayer 실기 재생 육안 미검증 · 슬라이스/PPS 페이로드는 비규격(구조만)
- 엑스포터 trun 에 composition offset 미기록(B 프레임 표시 순서 = DTS 공급 의존) · H.265 SPS 크롭 미구현
- 부하 환경 타이밍 간헐(GPU 스웜 50 ms 게이트 등) · 테스트 프로젝트 기존 분석기 경고 15 · GUI 육안/Android 빌드 미검증
