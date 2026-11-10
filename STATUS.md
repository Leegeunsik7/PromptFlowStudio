# Current Project Status

- Active Milestone: 【Pluto 3.0 안정화 감사】 임시파일·무잠금·진행률 포화·장문 자막·Blazor 스레드 안전
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 SDK 부재로 미빌드)
- Tests (RAM-safe 4배치 · MaxCpuCount=2): Titan|Security 110 · StockSuite|Export|Sfx 78 · Pluto|Vision|Subtitle|Director 187 · Publish|Workspace|Modal 58 — 전량 PASS
- Updated: 2026-11-10 (KST)

## Completed
- 🧹 BabyStudio: 먹싱 temp `finally` 확정 삭제(비IO 예외 포함) · SFX WAV 부분 작성분 `finally` 정리 · `.srt.~pfs` 잔여 정리
- 🔓 VisionStory: 이미지 전체/EXIF 읽기 단일 `FileStream(FileShare.ReadWrite)` — 사용자 원본 무잠금
- 🎬 DirectorRank `ProgressPercent`: long 산술 · span≤0/최고 티어 초과 100% 포화(오버플로·0 나눗셈 차단)
- 💬 자막 클리핑 가드: 한글 30자/라틴 65자 초과 → 폰트 비례 축소(하한 11pt) · 극단 길이 말줄임 — 4K ×2
- 🧵 Blazor: Workspace `RefreshUi`/`PostUi` 관측 Task 헬퍼(12곳) · PublishModal 관측 재렌더 · Close 시 SEO CTS 취소(업로드는 백그라운드 유지)
- 🐞 PlutoDirectorBadge 구독/해제 수신자 정합(Headless_UI_Rendering_Check 기존 실패 수정)
- 🧪 신규 `PlutoStabilityAuditTests` 17

## Next
- 모바일 빌드 검증(SDK 확보 시) · 4배치 필터 밖 테스트 회귀(여유 RAM 확보 시)
