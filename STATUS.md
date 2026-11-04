# Current Project Status
- Active Milestone: 【제미나이 AI 스튜디오 고도화】 듀레이션 연동 · Vision I2V 역추출 · 스토리보드 4컷 자동 디렉팅 · 로컬 폴백 가드
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 545/545 PASS(~29s) · GeminiPromptServiceTests 3/3 PASS
- Updated: 2026-11-04 (KST)

## Completed
- GeminiPromptService(신규 · Services/AI): gemini-2.0-flash · 듀레이션 인지 System Instruction(15s-Ambient → 루프 친화 미세 동세 지침 강제 + 응답 누락 시 루프 키워드 보장)
- AnalyzeReferenceImageAsync: 참조 이미지 base64 인라인(≤16MB) → Wan 2.1 I2V 프롬프트 역생성
- DirectStoryboardAsync: 한 줄 스토리 → 4컷(프롬프트·카메라·듀레이션) JSON 스키마 즉시 검증, 불일치 시 로컬 4컷 템플릿
- FallbackLocalEnhance: 키 없음/네트워크/HTTP 오류 → 로컬 시네마틱 사전(8K·35mm·shallow DoF·smooth motion) 무중단 강화
- Workspace: [✨ AI 문장 자동 강화] 이미지 有 → Vision I2V, 無 → 듀레이션 연동 · "⏳ 강화 중..." · 폴백 토스트
- 제미나이 픽: 📈 퀀트 OLED 차트 · 💹 핀테크 앰비언트 · 💻 트레이딩 데스크 추가
- Storyboard: [✨ 제미나이 4컷 자동 디렉팅] → StoryboardViewModel.ApplyDirectorShots(시드 전파·씬 동기화)
- DI: MainForm.cs + MauiProgram.cs 양쪽 등록 · 기존 GeminiPromptEnhancer 보존

## Known Issues
- TitanReleaseTests.VirtualProduction_And_CinematicMastering_Check 타이밍 단언 부하 시 간헐 실패
- 실 Gemini API 응답 품질 육안 미검증(테스트는 스텁 핸들러)
