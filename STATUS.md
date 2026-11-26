# STATUS (2026-11-26)

## 최근 작업
- Workspace 전 언어 일괄 렌더: 사전 조건 미충족 시 무응답 조기 종료 → 한국어 토스트 안내
  - `PlutoMultiLocaleBatchRenderService.DescribeRenderBlocker` 추가(순수 함수: 진행 중 · 타 작업 · 사진 2장 미만 · 채널 미준비 · 서비스 미등록 → 안내 1줄, 통과 → null)
  - 취소 시 "⏹ 전 언어 일괄 렌더가 취소되었습니다" 피드백
  - HEAD(12c0b72)의 Workspace.razor 호출부와 짝 맞춤 — 단독 빌드 복구
- FFmpeg 공유 경로: `%LocalAppData%\PromptFlowStudio\Tools\ffmpeg\bin\ffmpeg.exe` (9.0.2 essentials, `-version` exit 0)
- 직전: Workspace 주 생성 버튼 T2V/I2V 복구(`WorkspaceGenerationReadiness`)
- 테스트: `LocaleRenderBatch_Preconditions_SurfaceKoreanToastInsteadOfSilentReturn`

## 검증
- Release `-warnaserror` 경고 0 / 오류 0 (Desktop · Tests)
- xUnit 전체 1495/1495 통과(관련 65/65) · Pluto 불변식 9/6/2 유지 · DI 변경 없음(정적 헬퍼)
