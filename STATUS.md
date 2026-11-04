# Current Project Status
- Active Milestone: 【앰비언트 15s 루프 안정화】 filter_complex 정규화 + 실물 파일 검증 가드 + 캔버스 @key 강제 리로드
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 753/753 PASS(직렬 1회 ~11m09s) · FastUnit 520/520 PASS(병렬 ~50s)
- Updated: 2026-11-04 (KST)

## Completed
- 앰비언트 핑퐁: 슬로우 1.5배(정방향 7.5s + 역방향 7.5s = 15s 완결 루프) 유지 · ffmpeg `filter_complex` 구문 정규화
- 출력 가드: 렌더 완료 후 산출 파일 실물 존재·크기 검증 → 미존재 시 실패 처리(RenderQueueService·VideoEngineRenderExecutionEngine)
- Workspace 캔버스: 산출물 경로 기반 `@key` 강제 리로드(동일 파일명 갱신 시 캐시 고착 방지)
- 테스트: AmbientLoopTests·MultiDurationPipelineTests 회귀 케이스 추가

## Known Issues
- 실 ffmpeg 필터 품질 육안 미검증(실행기 주입 테스트) · 번들 YouTube OAuth 클라이언트 값 공란 유지
