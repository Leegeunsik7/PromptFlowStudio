# Current Project Status
- Active Milestone: 【상용 배포 완결】 환경설정 UI · 설정 우선순위 파이프라인 · FFmpeg 로컬 탐색 · CUDA OOM 자동 복구
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 710/710 PASS(직렬 10m43s) · FastUnit 478/478 PASS(병렬 ~26s)
- Updated: 2026-10-29 (KST)

## Completed
- 환경설정: `/app-settings`(사이드바 🛠️ ⚙️ 환경설정) — ComfyUI URL+연결 테스트(`/system_stats`) · DashScope/Gemini 키(관리자만 · 마스킹 토글) · FFmpeg 경로/버전 배지 · 렌더 모드 ComfyUI/Cloud/Mock
- 설정 파이프라인: `AppSettingsManager` `user_settings.json`(원자적 .tmp→Move) → 번들 `appsettings.json`(`ComfyUI:BaseUrl`·`Render:DefaultMode`) → 내장 기본값 · 키는 기존 Wan/Gemini AES-GCM 머신바운드 저장소 위임
- FFmpeg: `FfmpegLocator` tools/ffmpeg/bin → tools → 앱 폴더 → CliPathResolver(PATH) · `FFmpegVideoStitchService` 기본 해석 결선
- OOM 가드: `RenderQueueService` 실패 메시지 CUDA OOM 감지 + `RetryCount==0` → 해상도 1단계 하향(1080→720→540) · 메타 `OomAutoDownscaled` · Queued 복귀 1회
- 이전: TTS 믹싱 · SRT 자막 · 엔진 헬스체크 · 디스크 가드 · 렌더 일원화

## Known Issues
- 실제 API 키 값은 저장소/appsettings 에 미포함(시크릿 하드코딩 금지) — 배포 PC 에서 환경설정으로 등록 필요
- ComfyUI URL 변경은 헬스체크에 재시작 후 반영(`PFS_COMFY_URL` 최우선) · `/settings` 는 기존 관리자 설정 유지
- concat `-c copy` 는 컷 코덱/해상도 동일 전제(OOM 하향 컷 혼재 시 재인코딩 필요) · 모바일 `Blazor/Pages/` 미링크
