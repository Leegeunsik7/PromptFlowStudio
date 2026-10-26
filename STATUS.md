# Current Project Status
- Active Milestone: v18.4.3 Complete - CI/CD 릴리즈 워크플로우 단일화 및 테스트 수 검증 게이트 확립
- Build Status: Desktop · Tests Release -warnaserror 0/0 · net8.0-android 미검증(SDK/JDK 미설치)
- Test Suite: 541/541 직렬 실행(MaxCpuCount=1 · 실패 0 · 건너뜀 0) · TRX 게이트 로컬 재현 exit 0
- Sidebar: 6 그룹 · 32 워크스페이스 유지(신규 모달 0)
- Updated At: 2026-10-26 (KST)

## Completed Deliverables
- WFL-1: `.github/workflows/release.yml` — 트리거 `v[0-9]+.[0-9]+.[0-9]+` 태그 + workflow_dispatch 전용 · test(0/0 빌드 → xUnit 직렬 → TRX Total≥541/Failed 0/Skipped 0 단언) → installer(build_titan_release.ps1 → verify_release.ps1 -InstallCycle → artifacts/release/installer 업로드) → publish-release(Setup-v*.exe · release-checksums.sha256 · pfi1.manifest · release/verify JSON 단일 게시)
- WFL-2: `.github/workflows/release-pipeline.yml` — 태그 트리거·softprops 게시 제거, PR/main·master Push 전용 CI · windows-desktop 잡 xUnit 전수 + TRX 업로드
- WFL-3: `PromptFlowStudio/.github/` 삭제(release.yml 중복 · store-deploy.yml 은 부재한 tools/PromptFlowCiGate 의존 + APP_VERSION 4.4.0 고정 → 데드로 판단 삭제)
- WFL-4: YAML 구문 검증(PyYAML 3종 OK) · TitanReleaseTests 워크플로우 계약 단언 갱신(게시 단일화·중복 .github 부재 검증 추가)

## Known Issues / 환경 노트
- 로컬 ffmpeg.exe 부재로 `-AllowMissingFfmpeg` 빌드 — 정식 배포 전 `-FfmpegPath` 지정 재빌드 필요
- Authenticode 미서명(인증서 미제공) · Android 빌드는 환경 제약으로 미검증
- 스토어 배포 워크플로우 필요 시 CiGate 도구 구현 후 루트 `.github/workflows/` 에 재작성
