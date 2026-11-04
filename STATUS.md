# Current Project Status
- Active Milestone: 【UI 역할 분리】 예상 첫 프레임 시뮬레이터 ↔ 라이브 비디오 캔버스 중복 제거 — 정적 Start Frame 스틸 프리뷰어 전환
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: FastUnit 542/542 PASS(병렬 ~31s) · FirstFrame/Storyboard/BatchStudio 관련 29/29 PASS
- Updated: 2026-11-04 (KST)

## Completed
- FirstFramePreviewDeck: OnStartGenerate/CanStart·클릭 생성·▶ 오버레이·카메라 모션 애니메이션 제거 → 정적 스틸 뷰(3분할 그리드·카메라 가이드 SVG·START FRAME 배지)
- Workspace: 참조 이미지 즉시 첫 프레임 표시 · 생성은 [영상 생성 시작] 버튼만 · 재생/SNS 발행은 라이브 비디오 캔버스 전담
- Storyboard: '컷 앵커 스틸 뷰'로 전환 · 렌더는 [일괄 렌더]/컷별 [렌더]만
- BatchStudio(대량생산): 붙여넣기 첫 행 대본 기반 '스틸 썸네일·레이아웃 검토 뷰'
- 테스트: FirstFrameSimulation_LiveRender_Check — 렌더 큐 비결선(OnStartGenerate/video/ClickStart 부재) 단언으로 갱신

## Known Issues
- TitanReleaseTests.VirtualProduction_And_CinematicMastering_Check 타이밍 단언(MaxSolveMicros<1000µs) 부하 시 간헐 실패
- 실 ffmpeg 필터 품질 육안 미검증 · 번들 YouTube OAuth 클라이언트 값 공란 유지
