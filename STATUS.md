# STATUS (2026-11-26)

## 최근 작업
- 멀티 컷(10s 2컷 · 15s 3컷) 컷 전환 방식 `CutTransitionMode` 추가(`MultiDurationPlanner` · 메타 `duration.transition`)
  - 기본 **독립 시네마틱 컷**: 컷별 자기 프롬프트 생성 · 마지막 프레임 앵커 미주입 · `ContinuityHint`(피사체/유니폼/전신) 멱등 부착 · 0.25s `xfade`(+`acrossfade`) 합본(`FFmpegVideoStitchService.StitchCrossfadeAsync`, 실패/무 ffmpeg → 하드 컷 concat/가상 매니페스트)
  - 옵션 **연속 프레임 이어붙이기**: 기존 `-sseof` 마지막 프레임 I2V 체인 + concat 유지(`RenderQueueService.ChainDurationCutAsync` 게이트)
- `생성 시작`: 메인 프롬프트가 비어도 멀티 컷 파트 1 이 있으면 활성 · 파트 1 을 주 프롬프트로 폴백(`WorkspaceGenerationReadiness` 오버로드 · `ResolvePrimaryPrompt`)
- 첫 화면 미리보기 → 컷 1 시작 프레임 자동 바인딩(토글 · 지우기 버튼 · 사용자 이미지 우선 · 로컬 폴백 제외 · 작업 소유 사본 `cut1_*.png`)
- 테스트: `MultiCutTransitionModeTests`(독립/체인 · xfade 오프셋 · 폴백 · 파트 1 · 컷 1 바인딩 · Pluto 9/6/2) · 체인 테스트 명시 모드 · Titan 마커 갱신

## 검증
- Release `-warnaserror` 경고 0 / 오류 0 (Desktop · Tests — 실행 중 앱이 bin/Release 잠금 → OutDir VerifyRelease 빌드, 앱 재시작 필요)
- xUnit 1531/1531 통과(exit 0 · 직전 회차 LiveCommerce 벽시계 예산 지터 1건 → 단독 재실행 통과 후 전량 재실행) · Pluto 9/6/2 유지 · DI 변경 없음
