# Current Project Status

- Active Milestone: 【Pluto Tri-Brain 100% — SEO Failover】 🔁 SEO 제목/태그도 Gemini → 로컬 게이트웨이 → 템플릿
- Build: Release `-warnaserror` 0W/0E — PromptFlowStudio · PromptFlowStudio.Tests (Mobile: 로컬 Android SDK 부재로 미빌드)
- Tests: 전체 953/953 PASS · `~Pluto|~Publish` 73/73 PASS
- Updated: 2026-11-10 (KST)

## Completed
- 🔁 `PublishViewModel.GenerateSeoAsync` — `CaptionClient`(페일오버 체인 · 미주입 시 Gemini 단독) → `PlutoLlmBridge.TryCompleteAsync` → `ParseSnsMetadata` 스키마 즉시 검증 → 실패 시 `BuildLocalSnsMetadata`
- 🏷 상태 문구 `플루토(AI) SEO 생성 중…` / `플루토(AI) SEO 자동 완성` (Gemini 하드코딩 제거)
- ♻️ `GeminiPromptService.BuildSnsUserPrompt` 추출 — 직접 호출 · 체인 공용 사용자 지시(시스템 프롬프트 `SnsSeoInstruction` 유지)
- 🖱 `PublishModal.razor` 툴팁 체인 표기
- 🧪 `PublishViewModel_GeneratesSeoMetadata_ViaFailoverChain` — 기준선/게이트웨이 폴백/Gemini 우선/오프라인/스키마 불일치

## Next
- 모바일 빌드 검증(Android SDK 확보 시)
