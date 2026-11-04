# Current Project Status
- Active Milestone: 【렌더 파이프라인 실물화】 DashScope 원격 비디오 로컬 다운로드 + 실시간 렌더 모드 동기화 + Mock testsrc 실물 생성
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 765/765 PASS(직렬 1회 ~10m45s) · FastUnit 532/532 PASS(병렬 ~31s)
- Updated: 2026-11-04 (KST)

## Completed
- DashScope 원격 비디오: 클라우드 결과 URL → 로컬 파일 다운로드 파이프라인 구축(VideoEngineRenderExecutionEngine)
- 실시간 렌더 모드 동기화: 런타임 엔진 설정 변경을 렌더 실행 엔진에 즉시 반영(IRenderExecutionEngine·RenderQueueService·MainForm·Workspace)
- Mock 모드: ffmpeg `testsrc` 기반 더미 mp4 실물 생성 → 출력 존재 가드 통과
- 테스트: RenderEngineSettingsSyncTests 추가

## Known Issues
- TitanReleaseTests.VirtualProduction_And_CinematicMastering_Check 타이밍 단언(MaxSolveMicros<1000µs) 부하 시 간헐 실패(1회 1554µs, 단독 재실행 통과)
- 실 ffmpeg 필터 품질 육안 미검증 · 번들 YouTube OAuth 클라이언트 값 공란 유지
