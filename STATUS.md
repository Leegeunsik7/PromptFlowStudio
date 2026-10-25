# Current Project Status
- Active Milestone: v18.4.0 Complete - Enterprise Inno Setup Installer Packaged
- Build Status: Desktop · net8.0-ios · Tests 3 종 Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 541/541(v18.3.0 기준) · v18.4.0 회귀 TitanRelease 계열 106/106 통과(실패 0 · 건너뜀 0)
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-26 (KST)

## Completed Deliverables
- INST-1: `build_titan_release.ps1` — PS 5.1 `$smoke.Handle` 캐시 · 스모크 소요(ms) 로깅 영구화 · `-InstallerOutputDir`(기본 `artifacts\release\installer`) 사후 복사 → `PromptFlowStudio-Setup-v<ver>.exe` (installer\Output 원본은 CI/verify_release 용 유지)
- INST-2: Inno Setup 6 ISCC 컴파일(19.9 초) — publish 번들 462 파일(93.8MB · exe · appsettings.json · wwwroot 20) → Setup 26.0MB · 시작 메뉴/제거 바로가기 · 바탕화면(desktopicon 태스크) · WebView2 EdgeUpdate `Clients\{F3017226-…}` HKLM64/WOW6432Node/HKCU 검출 + Bootstrapper 동봉
- INST-2: `artifacts\release\installer\release-checksums.sha256` — `002a9a196b8c0206db9733bc0592ee515d5bd928c4935f5006582157d11c387b  PromptFlowStudio-Setup-v18.3.0.exe` (번들 매니페스트는 대상 집합 고정이므로 분리 기록)

## Known Issues / 환경 노트
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 — 정식 배포 전 `-FfmpegPath` 지정 재빌드 필요
- Authenticode 미서명(인증서 미제공) · Android 빌드는 환경 제약으로 미검증
