# Current Project Status
- Active Milestone: 【UX 고도화】 작업실 파라미터 역복원 · 산출물 폴더 열기/경로 복사 · SNS 제미나이 SEO 자동 완성
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 547/547 PASS (Release, ~43s)
- Updated: 2026-11-04 (KST)

## Completed
- Gemini 모델 엔드포인트 gemini-2.5-flash 통일
- WorkspaceRehydration(신규 · Services/Gallery): .meta.json → 프롬프트·카메라 모션·듀레이션·화면비·시드 스냅샷(레거시 메타/파일명 추정 폴백) · 단일 생성 사이드카 1회 기록
- Workspace: 🕒 최근 미디어 클릭 → 캔버스 교체 + 입력 폼 역복원 토스트 · 캔버스 하단 [📂 폴더 열기](explorer /select) · [📋 경로 복사](클립보드 토스트) · 큐 산출물에도 SNS/도구 버튼
- GeminiPromptService.GenerateSnsMetadataAsync: 플랫폼별 제목·본문·해시태그 5~8개 JSON 스키마 검증, 키 없음/실패 → 로컬 템플릿(#StockSuite #AIAnimation)
- PublishModal: [✨ AI 제목·해시태그 자동 생성] → PublishViewModel.GenerateSeoAsync(CTS 취소·발행 중 입력 보존)
- Tests: GeminiPromptService_GenerateSnsMetadata_ReturnsValidTitleAndTags · Workspace_RehydrateSettings_RestoresAllParameters

## Known Issues
- AudienceRetentionHeatmapIsolatedTests 100ms 성능 단언 — 부하 시 간헐 실패(Debug에서 101~131ms 관측)
- 실 Gemini API 응답 품질 육안 미검증(테스트는 스텁 핸들러)
