# Current Project Status

- Active Milestone: 【스토리보드】 4컷 자동 합성(Stitch) · 💼 StockSuite 원클릭 에셋 내보내기
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 549/549 PASS (Release, ~39s)
- Updated: 2026-11-04 (KST)

## Completed
- StoryboardStitchService(신규 · Services/Render): 4컷 그룹 완료 감지 → concat demuxer(`-f concat -safe 0`) `-c:v copy` 우선 → 실패 시 `scale=1280:720`+pad 정규화 재인코딩 · 임시 파일→실물 검증→원자적 이동 · `storyboard_{ts}_full20s.mp4` + `.storyboard.json`(컷별 프롬프트·시작/종료 타임스탬프)
- StockSuiteExporter(신규 · Services/Export): 설정 폴더에 `bg_ambient.mp4` 원자적 덮어쓰기(`.~pfs` 임시→Move) · 미설정/무효 경로 시 NeedsPathSetup 안내
- UserPreferenceService: `stocksuite.assets` 키 + UserSettings 💼 StockSuite 에셋 경로 필드(검증 버튼 · localStorage 영속)
- Storyboard: 렌더 큐 4컷 완료 시 마스터 자동 합성(그룹당 1회 · CTS Dispose) → 최종 영상/갤러리 등록 · [▶ 마스터 영상 재생] [📦 전체 결합 파일 내보내기] [💼 StockSuite로 내보내기]
- Workspace(캔버스·완료 패널) · PublishModal(PublishViewModel.ExportToStockSuiteAsync) 💼 버튼
- DI: MainForm · MauiProgram 양쪽 등록
- Tests: StoryboardStitchService_GeneratesValidConcatListAndExecutesFfmpeg · StockSuiteExporter_CopiesFileToTargetDirectorySuccessfully

## Next
- 실제 ffmpeg 환경에서 해상도 혼재 4컷 합성 E2E 확인
