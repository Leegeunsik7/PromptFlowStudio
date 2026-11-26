# STATUS (2026-11-26)

## 최근 작업
- 렌더 중복 산출물 근본 수정: 앰비언트 15초 후처리 성공 시 원본 5초 중간본(`wo_<ts>.mp4`) 삭제 → 갤러리에 `_ambient15s.mp4` 1개만 노출
  - `VideoEngineRenderExecutionEngine.DiscardIntermediate`(동일 경로·빈 최종본은 무동작, 실패는 격리) · 실패/가상 폴백 시 원본 보존
  - 체크포인트는 별도 캐시 사본이라 영향 없음
- Workspace 정리: 좌측 미니 큐 중복 완료 `<video>` 제거 · 빈 `프레임 신호 대기` MMF 모니터 + 16ms 폴링 루프 제거
- `예상 프레임` 정적 더미 → 실제 `🖼️ 첫 화면 미리보기`(명시 클릭 시 Gemini `gemini-2.5-flash-image` 1장, 16:9/9:16/1:1)
  - `[🎬 이 첫 화면으로 영상 생성]` → `BindStartFrameAsync`(I2V 시작 프레임) 후 생성
  - 키 미설정·실패는 `PlutoAutonomousActivityFeedService.Redact` 안내 · 응답 스키마 즉시 검증 · 28초 타임아웃 · 지수 백오프
- 테스트: `WorkspacePanelCleanupTests`(마크업 정리 · 페이로드/스키마 · 무키 폴백 · 단일 산출물 · Pluto 9/6/2)

## 검증
- Release `-warnaserror` 경고 0 / 오류 0 (Desktop · Tests, 실행 중 앱 잠금 회피용 OutDir 빌드)
- xUnit 전체 통과 · Pluto 불변식 9/6/2 유지 · DI 변경 없음(기존 GeminiPromptService 메서드 추가)
