# Current Project Status

- Active Milestone: 【Pipeline】 제미나이 한/영 번역 브릿지 · SNS 쇼츠 BGM 믹서 · StockSuite 원클릭 런처
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: Android SDK 미설치)
- Tests: FastUnit 557/557 PASS (Release, ~44s)
- Updated: 2026-11-04 (KST)

## Completed
- GeminiPromptService: 한글 감지(`ContainsHangul` · 할당 0) → "Professional Cinematic Video Prompt Translation" 모드 · 응답에 한글 잔존/키 없음 → 로컬 시각 용어집 폴백 · `SourceKorean` 원문 보존
- Workspace: `✨ AI 문장 자동 강화` 하단 `🇰🇷 원문` 서브 라벨(툴팁) — 치환본 유지 시에만 표시
- AudioMuxService(신규): `-stream_loop -1` BGM · `-c:v copy -c:a aac -b:a 192k -shortest` · `afade=t=out:st={d-1.5}:d=1.5` · `-t {d}` · 프리셋(신스웨이브/로파이)은 AiGenerativeMusicEngine 로컬 합성 WAV 캐시 · 실패 시 무음 원본 발행([부분 구제])
- PublishModal/PublishViewModel: `🎵 배경음악 선택`(무음/핀테크 신스웨이브/로파이/사용자 MP3) → 발행 전 합성본 업로드
- StockSuiteExporter: `ValidateExecutablePath` · `Launch(restart)` — 실행 중이면 경고 + `🔄 재실행`(종료 후 시작) · 예외 격리
- PublishModal: 내보내기 성공 후 `🚀 StockSuite 실행` · UserSettings: `StockSuite 실행 파일 경로` + 검증(`stocksuite.exe` 키)
- DI: MainForm · MauiProgram 양쪽 AudioMuxService 등록 + StockSuiteExporter executableProvider 결선
- Tests: GeminiPromptService_TranslatesKoreanPromptToCinematicEnglish · AudioMuxService_BuildsCorrectFfmpegMuxingArguments · StockSuiteExporter_ValidatesExecutablePathSuccessfully

## Next
- 실 ffmpeg 환경에서 BGM 합성 결과(길이·페이드) 및 YouTube/Instagram 업로드 E2E 확인
- 실 StockSuite.exe 실행/재실행 실기 검증
