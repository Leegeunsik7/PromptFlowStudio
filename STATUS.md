# Current Project Status

- Active Milestone: 【Pluto 3.0 Vision-to-Story】 📸 사진 1장 → 15초 3막 4컷 스토리보드 + 🪐 통합 디렉터 HUD
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 SDK 부재로 미빌드)
- Tests: `~VisionStory|~Director|~Pluto` 91/91 PASS (신규 `PlutoVisionStoryTests` 3) — 호스트 RAM 제약으로 전체 스위트 미실행
- Updated: 2026-11-10 (KST)

## Completed
- 🪐 트렌드 랩 HUD 통합 — 구 10 XP 두뇌 카운터 제거 · `PlutoDirectorBadge Unified` 단일 표시(`HudTitle` · `XpLabel` · 해금 광학 `UnlockedOpticsTooltip`)
- 📸 `PlutoVisionStorySynthesizer`(싱글톤, MainForm·MauiProgram) — `SynthesizeFromImageAsync(imagePath, parentMemo, ct)`
  - 판독 페일오버: Gemini Vision(`IPlutoVisionClient` · 8초) → 로컬 게이트웨이(파일명·EXIF·메모 텍스트) → 규칙 분석기(6 정서 템플릿)
  - Act1 셔터 3초 전(0-4s) · Act2 빌드업/사진 속 클라이맥스(4-12s) · Act3 5초 후 여운(12-15s) — 앵커 선두 · 디렉터 광학 말미 멱등
  - `PlutoVisionStoryPlan`: 무드 · 세팅 · 4컷 · BGM 테마/트랙 · 감성 자막 초안 · 출처 · 디렉터 Lv
- 🖥 Workspace `📸 사진으로 15초 스토리 완성`(클릭/드롭) + `📸 참조 사진으로` — 4컷 일괄 · BGM ON · 감성 자막 ON(초안 우선)

## Next
- 모바일 빌드 검증(SDK 확보 시) · 전체 스위트 회귀(여유 RAM 확보 시)
- 실 사진 샘플로 Gemini Vision 판독 품질 · 로컬 VLM(이미지 입력) 게이트웨이 확장 검토
