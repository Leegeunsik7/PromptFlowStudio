# Current Project Status

- Active Milestone: 【Batch-HTS】 작업실/스토리보드 StockSuite 런처 · 대량생산 제미나이 벌크 기획 · HTS 1080p 샤프닝 내보내기
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 559/559 PASS (Release, ~30s)
- Updated: 2026-11-04 (KST)

## Completed
- Workspace/Storyboard: `💼 StockSuite로 내보내기` 옆 `🚀 StockSuite 실행`(exe 유효 시 · 2초 캐시) + 성공 토스트 내 실행 버튼 · 실행 중이면 `🔄 재실행`
- GeminiPromptService: `GenerateBatchPromptsAsync(theme, count=5)` — 1~20편 상이 앵글 JSON 스키마 검증 · 부족분 로컬 보충([부분 구제]) · 키 없음/실패 → 로컬 앵글 템플릿 · `ToBatchPasteText`(제목\t프롬프트)
- BatchStudio: 상단 `✨ AI 일괄 기획`(테마+개수) → 직접 붙여넣기 입력창 즉시 바인딩 → `＋ 대기열에 추가` 원클릭
- StockSuiteExporter: `SharpenFilter`(`scale=1920:1080:flags=lanczos,unsharp=5:5:0.8:3:3:0.4`) libx264 CRF18 `-an` 재인코딩 · 실패 → `-c:v copy` → 원본 복사 폴백 · WebM 미적용
- 설정: `stocksuite.sharpen`(기본 ON) — UserSettings 체크박스 · PublishModal `고선명 샤프닝` 체크박스 · DI 양쪽 sharpenProvider 결선
- Tests: GeminiPromptService_GenerateBatchPrompts_ReturnsRequestedCount · StockSuiteExporter_AppliesUpscaleAndSharpenFilter_WhenSharpeningRequested

## Next
- 실 ffmpeg 환경에서 1080p 샤프닝 화질/인코딩 시간 확인
- 실 Gemini 키로 벌크 기획 응답 품질·개수 준수 확인
