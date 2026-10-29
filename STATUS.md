# Current Project Status
- Active Milestone: 【완벽 마감】 스마트 Concat 재인코딩 폴백 · 설정 핫리로드 · 폴더 부트스트랩 · 원스톱 배포 패키저
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (net8.0-android 미검증: SDK/JDK 미설치)
- Tests: 전체 713/713 PASS(직렬 9m59s) · FastUnit 481/481 PASS(병렬 ~27s)
- Updated: 2026-10-29 (KST)

## Completed
- 스마트 Concat: `FFmpegVideoStitchService` ffprobe 해상도 사전 점검 → 불일치면 즉시, `-c copy` 실패 시 폴백 — 최대 해상도 `scale…decrease,pad` 레터박스 + `libx264`/`aac`(무음 컷은 `anullsrc`)
- 핫리로드: `AppSettingsManager.OnSettingsChanged(UserSettings)`(구독자별 예외 격리) → `EngineHealthCheckService.AttachSettings` URL 즉시 교체 + 백그라운드 재핑(배지 '점검 중' → 결과) · 렌더 엔진 `/interrupt` 도 `ComfyBaseUrl` 실시간 평가
- 부트스트랩: `AppFolderBootstrapper` `_renders`·`_uploads`·`_gallery`·`tools/ffmpeg/bin`·`presets` 멱등 생성(Program.Main · MauiProgram)
- 패키저: `scripts/PackageRelease.ps1` publish → 환경변수/`-SecretsFile` 키를 배포 사본 appsettings.json 에만 주입 → ffmpeg/ffprobe 동봉 → `dist/PromptFlowStudio_v19.0.0_ReadyToRun.zip` · `.gitignore` dist/ 가드
- 이전: 환경설정 UI · FFmpeg 로컬 탐색 · CUDA OOM 자동 하향 · TTS/SRT · 헬스체크 · 디스크 가드

## Known Issues
- 머신바운드(AES-GCM/DPAPI) 저장 키는 타 PC 복호화 불가 → 패키저는 환경변수/로컬 secrets JSON 으로만 공급 · ZIP 에 키 포함(신뢰 채널 배포)
- `PFS_COMFY_URL` 설정 시 핫리로드보다 우선 · 패키저 실 publish/ZIP 은 미실행(스크립트 구문 검증만)
- 재인코딩 폴백은 컷 길이만큼 CPU 인코딩 시간 소요 · 모바일 `Blazor/Pages/` 미링크
