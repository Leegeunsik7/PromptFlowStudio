# Current Project Status

- Active Milestone: 【Pluto Autonomous Prompt Evolution Engine】 🎬 XP → 디렉터 랭크 → 시네마토그래피 광학 자동 주입
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 SDK 부재로 미빌드)
- Tests: `~Director|~Pluto` 88/88 PASS (신규 `PlutoDirectorRankTests` 15) — 호스트 RAM 제약으로 전체 스위트 미실행
- Updated: 2026-11-10 (KST)

## Completed
- 🎬 `PlutoDirectorTier`/`PlutoDirectorRankService`(`IPlutoDirectorRankService` 싱글톤, MainForm·MauiProgram) — `PlutoLearningService.CurrentXP` 기반
  - Lv.1 내추럴 비기너(0) · Lv.2 감성 스토리텔러(20) · Lv.3 시네마틱 마스터(50) · Lv.4 바이럴 마에스트로(100, 목표 200)
  - `EnrichPromptWithOptics` 멱등 · 마침표 보존 · XP 공급 예외 → Lv.1 폴백 · `Changed` 학습 이벤트 중계
- 🪐 `PlutoCopilot.Director` — LLM 요청 지시문 + 로컬 Draft/Triplet/LLM 결과 프롬프트 말미 광학 결합
- 🎞 `PlutoMilestoneOrchestrator(llm, identity, director)` — LLM 지시문 + `ApplyDirectorOptics` 4컷 전 슬롯 주입
- 🖥 `PlutoDirectorBadge`(파라미터 주입 · 진행 바 · 호버/포커스 팝오버) — Workspace 툴바 · 트렌드 랩 HUD · 코파일럿 드로어

## Next
- 모바일 빌드 검증(SDK 확보 시) · 전체 스위트 회귀(여유 RAM 확보 시)
- 실 영상 모델로 티어별 광학 키워드 체감 품질 A/B 검증
