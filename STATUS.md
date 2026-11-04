# Current Project Status
- Active Milestone: 【SNS 직결 + 인라인 취소】 작업실·갤러리 🚀 발행 통합 + ❌ 큐 취소 + 플래키 격리 + FFmpeg 다운로더 안정화
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 750/750 PASS(직렬 1회 ~12m40s) · FastUnit 517/517 PASS(병렬 ~56s)
- Updated: 2026-11-04 (KST)

## Completed
- SNS 직결: Workspace(미니큐 완료·로컬 산출물)·MediaGallery(타일·확대 모달) `🚀 SNS 발행` → `PublishViewModel.OpenForMedia`(경로·프로젝트명·동명 .srt·프롬프트) → `PublishModal`
- 인라인 취소: Workspace 미니큐 `❌ 취소` → `IRenderQueueService.CancelJobGroup`(대기 컷 선확정 → 진행 컷 킬·ComfyUI `/interrupt`) · 표시 즉시 `AsCancelled` 유지
- FFmpeg 다운로더: `.tmp` 다운로드 후 원자 이동 · 실패/취소/타임아웃/절단(read<total) 즉시 롤백 · 추출 `.part` finally 정리 · `WorkRoot` 테스트 격리 · `DownloadAndExtractAsync` 별칭 · `/app-settings` ❌ 설치 취소
- 테스트: `LiveToReelsFactoryIsolatedTests` 자원 소유·Dispose 격리(순서 의존 플래키 대응) · 소크/롤백 회귀 테스트 추가

## Known Issues
- 실 네트워크 다운로드·실 ffmpeg 고속 필터 품질 미검증(주입 HttpHandler/실행기 테스트) · 번들 YouTube OAuth 클라이언트 값 공란 유지
