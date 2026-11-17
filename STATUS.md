# Current Project Status
- Active Milestone: 【Step 11 — MF H.264 0×0 프로브 결함 수정(HW MFT 출력 자가검증 → CPU MF → 관리 코드 폴백)】 ✅ 완료
- Build: ✅ Desktop Release `-warnaserror` 0W/0E · ✅ Tests Release 0W/0E · ⏳ Mobile net8.0-ios — `ios` 워크로드 미설치(NETSDK1147)
- Tests: ✅ 1184/1184 — 전체 실행 1183 통과 + `SingleFileBinaryProcessBootstrap_Check` 단독 재실행 통과(부하 시 간헐)
- Updated: 2026-11-17 (KST)

## Completed
- Step 10: `6b1d98f` → `origin/main`, `origin/master` 동기화
- Step 11: `MfH264Encoder.TryHardware` — 비동기 HW MFT 후보별 프로브(회색 프레임 → SPS 출력 확인) 후 채택, 크기별 검증 캐시
  - 원인: 하이브리드 GPU(NVIDIA RTX 5050 + Intel) HW MFT가 입력 수락 후 0 샘플 출력 → fMP4 init 미기록 → 프로브 0×0
  - 무출력 시 MS 소프트웨어 MFT → 관리 코드 Baseline 순 투명 폴백(MF 정상 경로 무변경)
- `Media.MediaFeaturePack` 설치 확인(Online: True) · `VerticalVideoTranscode_*`, `VideoDemux_*` 통과

## Notes
- FFmpeg 폴백 계층은 미추가 — 최종 폴백이 관리 코드 H.264(외부 의존 0)라 KN 에디션에서도 0×0 불가
- `*IsolatedTests` 실시간 프레임 예산 테스트는 부하 시 간헐 실패 → 유휴 재실행

## Next
- 신규 PC: `ios` 워크로드 설치 후 Mobile 빌드 재검증
